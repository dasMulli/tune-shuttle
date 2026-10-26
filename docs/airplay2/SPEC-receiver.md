# AirPlay 2 receiver specification: TuneShuttle as an AirPlay speaker

Written 2026-10-26. TuneShuttle gains a second audio source: it advertises itself as one AirPlay 2 receiver on the LAN,
so an iPhone, iPad or Mac can pick "TuneShuttle" in the AirPlay menu and what it plays comes out of the selected
TuneShuttle output (one receiver or a synchronized group), exactly as Spotify Connect does today. This document is the
contract for the implementation: the decisions (R1-R16), what the receiver must advertise, answer and decode, how the
audio is timed into the existing output pipeline, how the two sources share the output, what changes in the API, the
web app, the store and the deployment, how it is tested, the open questions with their probes, and the implementation
waves.

The sender-side protocol knowledge this builds on is in [`SPEC-control.md`](SPEC-control.md) and
[`SPEC-audio.md`](SPEC-audio.md); this document cites their sections instead of repeating them and uses the same
citation prefixes (`[sps]` shairport-sync, `[ap2r]` airplay2-receiver, `[ot]` owntone, `[pyatv]`, see SPEC-control.md
0). The decisions of the sender (`SPEC.md` D1-D17) stay in force; where the receiver role needs something different it
says so. How the existing system is built is in [`../ARCHITECTURE.md`](../ARCHITECTURE.md).

## 0. Summary

A new library `src/Airplay2.Receiver` (promoted from the test project `tests/Airplay2.FakeReceiver`, which already holds
the receiver side of HAP pairing, an RTSP server with the encrypted channel, the event channel, session and stream
SETUP, the realtime stream with retransmit requests, an ALAC decoder and a PTP slave) advertises `_airplay._tcp` and
`_raop._tcp`, accepts one sender at a time, decodes ALAC (managed) and AAC-LC (ffmpeg's libavcodec through P/Invoke),
and hands 44.1 kHz/16-bit stereo frames, each stamped with the moment the sender wants it heard, to a playout buffer.
The bridge hosts the library as `AirplaySource`; a `SourceSwitch` in front of the output lets the newest source win
(an AirPlay stream SETUP pauses Spotify and takes the output; a Spotify play ends the AirPlay session). One shared
`PtpPort` owns UDP 319/320 for both roles (grandmaster toward our receivers, slave toward the sender). Settings, status,
playback and diagnostics gain the AirPlay fields; the Settings page gets an "AirPlay receiver" card and the player's
hero shows "AirPlay · <sender>".

## 1. Scope

**Goals (v1).**

- G1. The receiver appears in the AirPlay menu of iOS 17+, iPadOS 17+ and macOS 14+ devices on the same LAN and plays
  what they send to the selected TuneShuttle output, with volume, pause, seek and track changes working.
- G2. Spotify Connect keeps working exactly as today; the two sources share the output with the "newest wins" rule.
- G3. The web app shows what is playing from AirPlay (title, artist, album, artwork, progress, sender name), lets the
  user turn the receiver on and off, name it and give it a password, and shows the receiver's state in Diagnostics.
- G4. Everything runs in the one existing process in all three deployment shapes (Windows Service, systemd, container).
- G5. The library works without the bridge (tests, a later CLI), as `Airplay2.Sender` does.

**Non-goals (v1), each with the hook for later.**

- N1. **Taking part in an iOS multi-speaker selection in sync with other speakers.** iOS may still add us to such a
  selection; we play, but our audible delay (the per-stream offset `D` of R6 plus our receivers' own, Section 5.4) is not compensated. Hook: the
  `audioLatencies` entry of `/info` and a `PtpSlave` that disciplines the output timeline (RQ6).
- N2. **Remote control of the sender** from the web app or from a speaker's buttons (play/pause/next on the iPhone).
  While AirPlay owns the output the transport buttons are disabled and speaker buttons are ignored. Hook: DACP
  (`DACP-ID`/`Active-Remote` headers, `_dacp._tcp`, `GET /ctrl-int/1/<cmd>`) as `[sps] dacp.c` does; shairport-sync
  reports it works for classic senders and is experimental for AirPlay 2 senders.
- N3. **AirPlay 1 (RAOP with RSA, ANNOUNCE/SDP), iTunes for Windows, Apple TV as a sender, screen mirroring, video.**
- N4. **48 kHz, 24-bit and surround formats.** We advertise only the 44.1 kHz stereo formats (R3), so no resampler is
  needed; the sender transcodes.
- N5. **HomeKit (`POST /configure`, access control), bit-50 now-playing (`POST /command` plists), per-user passwords.**
- N6. **Local audio output.** Audio always goes to the AirPlay output, never to a sound card (ARCHITECTURE.md decision).
- N7. **More than one sender at a time.** The newest replaces the current one (R8).

## 2. Decisions

Numbered R1-R16 so code and tests can refer to them (`SPEC-receiver.md R5`).

| # | Decision | Why |
|---|---|---|
| R1 | **An AirPlay 2 receiver**: HAP pairing, encrypted control channel, `timingProtocol: PTP`, stream types 96 (realtime) and 103 (buffered). No AirPlay 1 path. | iOS sends buffered AAC to AirPlay 2 receivers and falls back to the ANNOUNCE/RSA path only when the buffered-audio bit is absent (`[ap2r] bitflags.py:118-120`); the NTP bit makes iOS "die on play" (`bitflags.py:134-137`). AirPlay 1 would need Apple's private RSA key. |
| R2 | **Library first**: `src/Airplay2.Receiver` is built from the fake receiver's code; `tests/Airplay2.FakeReceiver` becomes a thin wrapper that adds its quirk switches and recording. `Airplay2.Receiver` references `Airplay2.Sender` (Crypto, Tlv8, Plist, Rtsp codec, Alac, Rtp, Streams/PTP, Session/Dmap) and `Librespot.Discovery` (the mDNS responder); it uses no ASP.NET, Spotify or SQLite code. | 3 300 lines of tested receiver-side code exist; the repository's parts are usable on their own (README). |
| R3 | **Formats**: realtime ALAC/44100/16/2 (352 frames per packet); buffered ALAC/44100/16/2 (352 frames per block) and AAC-LC/44100/2 (1024 frames per block). `/info` `supportedFormats` advertises exactly these (`audioStream = 0x40000`, `bufferStream = 0x440000`). When no AAC decoder is loaded (R4) the receiver uses the **ALAC-only variant** `ReceiverFeatures.AlacOnly` consistently in all three places that advertise AAC: `bufferStream = 0x40000`, feature bit 20 cleared (R15), `_raop` `cn=0,1` (3.1), so a sender never sees AAC offered in one place and refused in another (RQ11). Output is always S16 interleaved 44.1 kHz stereo. | The output pipeline is 44.1/16 (SPEC.md D1); `[sps] AIRPLAY2.md` lists AAC/44.1 as the basic buffered format every sender supports. |
| R4 | **AAC-LC through ffmpeg's libavcodec** via P/Invoke (FFmpeg.AutoGen, LGPL-3.0 for the 7.x bindings, Section 7.2, pinned to one FFmpeg major), behind `IAacDecoder`. The library path is configurable; when the native libraries are missing (`IAacDecoderFactory.TryCreate` fails) the receiver starts with the ALAC-only variant of R3 (`ReceiverFeatures.AlacOnly`) and logs the reason once. The decoder is fed ADTS-framed blocks (`[sps] ap2_buffered_audio_processor.c:41-90`). | .NET has no AAC decoder; shairport-sync needs libavcodec for the same reason; a managed port is a later option behind the same interface. Decided with the owner on 2026-10-26. |
| R5 | **One `PtpPort` per process** owns UDP 319/320 and routes messages by source address to the grandmaster (`PtpMaster`, our receivers) or to the slave of the current sender session (`PtpSlave`). The slave is passive by default: it computes the offset from Sync/Follow_Up and sends nothing except two Announces of its own clock at session start (what nqptp does, SPEC-audio.md 4.4 note); Delay_Req is an option (RQ5). When 319/320 cannot be bound the receiver runs in **anchor-only timing** (R6). | nqptp proves a silent slave is accepted by Apple senders; our grandmaster already binds the ports when sending to PTP-only receivers, and two sockets cannot share them. |
| R6 | **Playout rule** (the one lateness rule; 5.1-5.4 apply it, none adds another): a frame with intended play time `T` (sender timeline, from SETRATEANCHORTIME or 0xD7 syncs, mapped to local time through the PTP offset) has the release time `release = T - Lpipe + D`, where `Lpipe` is the output's write-to-audible delay (Section 5.4) and `D` is a per-stream offset learnt from the first frame released after a (re-)anchor: `D = max(0, Lpipe - (T(first) - now))`, i.e. 0 when the sender's lead exceeds `Lpipe` (buffered streams) and `Lpipe - lead` otherwise (realtime streams, about 0.45 s: they play `D` late instead of being dropped). The frame is written to the output when `now >= release`; it is **late** only when `now > release + LateThreshold` (200 ms), and late frames are dropped and counted. Without a PTP offset (anchor-only) the first anchor is mapped to `now + AnchorLead` and later anchors keep the same offset. Drift is corrected by dropping or repeating single frames when the buffer runs more than `DriftThreshold` early or late. | Buffered senders push audio seconds ahead; playing it as it arrives would run ahead of the phone's progress and break pause. Absolute alignment with the sender's clock is only needed for N1. |
| R7 | **Pairing**: transient pair-setup by default (status flags `0x4`). `HapAccessoryPairing` needs a PIN even then, so in this open mode `Pin` is a random 8-digit string generated at every receiver start and never shown: a regular pair-setup M1 is accepted and fails at M3 with `Error 2` (nobody knows the PIN); nothing is refused with 470. With a configured password the inverse: regular pair-setup with the password as the SRP PIN, status flags `0x84` (`PasswordNeeded`), transient M1 refused with 470, registered controllers persisted (4.2) and pair-verify accepted. | shairport-sync's password mode is proven with iOS (`[sps] shairport.c:1687-1689`, SPEC-control.md 1.4 bit 7); `HapAccessoryPairing` already implements both procedures. |
| R8 | **Newest source wins.** The AirPlay source claims the output on the sender's **stream SETUP** (not on connect, not on pairing); the claim pauses Spotify through Spirc. Spotify claims the output on its `Playing` event; the claim ends the AirPlay session (connection closed, iOS shows the speaker as disconnected). A second sender's stream SETUP ends the first sender's session the same way. When a sender leaves, the output is unowned; Spotify stays paused until the user plays. | HomePod behaviour; the person who just pressed play gets what they expect. Decided with the owner on 2026-10-26. |
| R9 | **Volume**: a sender `SET_PARAMETER volume` in dB maps through the existing inverse of SPEC.md D10, `VolumeMapping.DbToLevel` (`level = round((dB + 30) / 0.3)`, `-144` or anything at or below `-30` → 0; no second mapping is written), to the master level G of the output with source `airplay`; `GET_PARAMETER volume` answers the current G in the airplay2-receiver shape (`volume: -24.000000\r\n`, no leading blank line, SPEC-control.md 4.11 note; `VolumeMapping.FormatParameter` produces it). While no output is selected (`OutputManager.CurrentLevelAsync` is null) the value answered, also as `/info` `initialVolume`, is the last level the sender set in this session, else 50 (`-15.0` dB); a `SET_PARAMETER volume` in that state is stored as the AirPlay source level and applied when an output is selected. Level changes made in the web app while AirPlay owns the output are applied to the receivers but are not pushed to the sender (N2). | The group volume model stays the single place where levels live. |
| R10 | **Metadata**: DMAP text, artwork and progress from the sender (`SET_PARAMETER`, SPEC-control.md 5.2-5.4) are decoded, shown in the web app and forwarded to the receivers through the existing now-playing path. The receiver advertises feature bits 15, 16, 17 and `md=0,1,2`, not bit 50. | The group player already sends now-playing to receivers; bit 50 would require the `POST /command` protocol (N5). |
| R11 | **Identity**: one receiver identity per installation, created on first start and stored in `settings`: `deviceid` (random locally-administered MAC, also the HAP accessory pairing identifier and `pi`), `psi` (UUID), the Ed25519 long-term key as its 32-byte **seed** (`pk` is derived with `Ed25519KeyPair.FromSeed`; storing `pk` alone would lose the signing key). The name defaults to the Spotify device name and is settable separately. | Receiver A uses its MAC as `pi` and pairing id (SPEC-control.md 1.2); a stable identity keeps pair-verify working for password mode. |
| R12 | **Self-exclusion**: `ReceiverInventory` ignores discovered receivers whose `deviceid` or `pk` equals our own; they never reach the store or the web app. | Our browser sees our own advertisement. |
| R13 | **One receiver per host.** The product receiver binds TCP 7000 (`Bridge:Airplay:RtspPort`); tests bind port 0 and run the fake receiver and the product receiver only on loopback. | `[sps] AIRPLAY2.md`: senders are confused by two AirPlay 2 receivers at one address. |
| R14 | **`POST /fp-setup`** is answered with the canned FairPlay replies shairport-sync uses (`[sps] rtsp.c:1843-1930`, MIT), selected by the request's mode byte; nothing is derived from them. | Our feature word carries bit 14 (shairport-sync's does), and iOS then sends `/fp-setup` before pairing. |
| R15 | **Feature word** `0x00018340405FCA00` (TXT `features=0x405FCA00,0x18340`): shairport-sync's default plus the metadata bits 15-17; bits 9, 11, 14, 15, 16, 17, 18, 19, 20, 22, 30, 38, 40, 41, 47, 48. `srcvers 366.0`, `protovers 1.1`, `model TuneShuttle1,1`, `manufacturer TuneShuttle`. The word is one constant (`ReceiverFeatures.Default`) so the probes can change it in one place; `ReceiverFeatures.AlacOnly` is the same word with bit 20 cleared (TXT `features=0x404FCA00,0x18340`; bit 20 is in the low word), used when no AAC decoder loads (R3). | A word proven with iOS and macOS senders (`[sps] shairport.c:1628`); bit table in SPEC-control.md 1.3. |
| R16 | **Timeouts**: a sender that sends nothing on the control connection for 30 s after a stream SETUP is considered gone (iOS sends `/feedback` every 2 s, SPEC-control.md 4.10); a closed control connection ends the session at once (event channel closed, ports released, output released). | `[sps]` uses a 60 s TCP keepalive; 30 s keeps the output from staying blocked after a phone leaves the Wi-Fi. |

## 3. Discovery and `/info`

### 3.1 Services

Two DNS-SD services through `Librespot.Discovery.Mdns.MdnsResponder` (one responder instance owned by the receiver's
advertiser; the Spotify Zeroconf responder stays separate), re-registered (goodbye, then announce) on a name or
password change and on nothing else (3.3): stream start and end change `/info` `statusFlags` only, not the TXT
records (4.6):

| Service | Instance name | Port |
|---|---|---|
| `_airplay._tcp` | `<name>` | the RTSP port |
| `_raop._tcp` | `<deviceid without colons>@<name>` | the RTSP port |

`_airplay._tcp` TXT (SPEC-control.md 1.2; shairport-sync's set, `[sps] bonjour_strings.c:60-85`, `rtsp.c:999-1104`):
`acl=0`, `deviceid=<MAC>`, `features=0x405FCA00,0x18340`, `flags=0x4` (or `0x84` with a password), `gid=<deviceid>`,
`gcgl=0`, `model=TuneShuttle1,1`, `manufacturer=TuneShuttle`, `protovers=1.1`, `pi=<deviceid>`, `psi=<uuid>`,
`pk=<64 hex>`, `srcvers=366.0`, `fex=<base64 of the little-endian features, no padding>`.

`_raop._tcp` TXT (SPEC-control.md 1.5; `[sps] bonjour_strings.c:1-57`): `cn=0,1,2` (`cn=0,1` in the ALAC-only variant,
R3), `da=true`, `et=0`, `ft=<same as
features>`, `md=0,1,2`, `am=TuneShuttle1,1`, `pk=<same>`, `sf=<same as flags>`, `tp=UDP`, `vn=65537`, `vs=366.0`,
`ss=16`, `sr=44100`, `ch=2`, `txtvers=1`, `pw=false`/`pw=true`.

### 3.2 `GET /info`

Answered on any connection, paired or not, with a bplist built from the captured template
`docs/airplay2/test-vectors/info-response.bplist` (as the fake receiver does) with these keys replaced or added:
`name`, `model`, `manufacturer`, `deviceID`, `pi`, `psi`, `pk` (32 bytes), `features` (the R15 word as an integer),
`statusFlags`, `sourceVersion`, `protocolVersion`, `senderAddress` (`<ip>:<port>` of the requesting connection),
`initialVolume` (the current G in dB, R9), `supportedFormats {audioStream, bufferStream}` (R3), `txtAirPlay` (the
`_airplay._tcp` TXT as one data blob, needed in `updateInfo`). The stage-1 variant (`POST /info` with a `qualifier`
`txtAirPlay`) is answered like `GET /info` (`[sps] rtsp.c:1172-1200`).

### 3.3 Settings changes and shutdown

Each setting has its own effect; only `enabled` restarts anything:

| Setting | Effect |
|---|---|
| name | `ReceiverAdvertiser` re-registers both services (goodbye, then announce) and `/info` answers the new name; a running session is untouched (RP12 expects the menu to follow, not a disconnect) |
| password | new `HapAccessoryOptions` (R7) for new connections; TXT `flags`/`sf` and `pw` re-registered; a running session and its pairing stay |
| enabled → false | goodbye records, listening sockets closed, a running session ended (Section 4.9) |
| enabled → true | the receiver starts as at service start |

Stopping the service behaves like `enabled → false`.

## 4. Control plane as a receiver

### 4.1 Connections

The RTSP listener (TCP, `Bridge:Airplay:RtspPort`, default 7000, 0 in tests) accepts any number of connections; each
is an `RtspServerConnection` (moved from the fake receiver) with the HAP channel cipher switched on after pairing. A
connection is **the session connection** once it has completed a session SETUP. Requests other than `GET /info`,
`POST /info`, `OPTIONS`, `POST /fp-setup`, `POST /auth-setup` and the pairing POSTs on an unpaired connection are
answered 470 (the fake receiver's `RequirePairing` rule stays the product behaviour).

### 4.2 Pairing

`HapAccessoryPairing` (already in `Airplay2.Sender/Pairing`, internal; wave 0 `receiver-extract` adds the
`InternalsVisibleTo` entries for `Airplay2.Receiver` and `Airplay2.Receiver.Tests`, Section 13) handles
`/pair-pin-start`, `/pair-setup` and `/pair-verify`. `/pair-add`, `/pair-remove` and `/pair-list` are not implemented
and are answered 404 until a sender is seen using them (iOS and macOS do not use them for AirPlay in the open mode;
shairport-sync's `handle_pair_add/remove/list` is the shape to follow then). Options per R7: `AllowTransient =
password is null`, `Pin = password ?? <random 8 digits per start>`, `MaxControllers 16`. Registered controllers
(identifier + Ed25519 LTPK) are persisted through `IControllerStore` (`LoadAsync()`, `SaveAsync(id, key)`,
`RemoveAsync(id)`; the bridge backs it with the `airplay_controllers` table, Section 8.4; tests use
`InMemoryControllerStore`). The hook into the pairing code is made in wave 0 `receiver-extract`, in
`Airplay2.Sender/Pairing/HapAccessoryPairing.cs`: `HapControllerRegistry` gets an optional change callback
(`Action<string, byte[]?> onChanged`, called after `TryAddOrUpdate`, `Set` and `Remove` with the new key or null) and
`HapAccessoryPairing` a constructor that takes a registry. At start the receiver seeds the registry with `Set` from
`LoadAsync`; the callback writes through to the store (awaited on the handler path of M5; a
store failure is logged and the pairing still answers). `MdnsResponder` is used the same way (internal, made visible in
wave 0). `POST /auth-setup` is answered as the fake does (200 with 1076 zero bytes, SPEC-control.md 3.10).

### 4.3 Session SETUP

Body without `streams` (SPEC-control.md 4.2). Read `timingProtocol`, `name`, `model`, `deviceID`, `macAddress`,
`osName`, `osVersion`, `sessionUUID`, `timingPeerInfo.Addresses`, `timingPeerInfo.ClockID`, `groupUUID`:

| `timingProtocol` | Answer |
|---|---|
| `PTP` | 200 `{eventPort, timingPort: 0, timingPeerInfo: {Addresses: [<our addresses, the connection's local address first>], ID: <that address>}}`; the event listener is accepting before the answer is written (`[sps] rtsp.c:2626-2638`); the `PtpSlave` is started for the sender's addresses and clock id (R5) |
| `NTP` | 501 (R1; our fake receiver's `RefuseNtp` behaviour becomes the product behaviour) |
| `None` with `isRemoteControlOnly` | 200 `{eventPort, timingPort: 0}`; nothing else happens; the connection is not a session connection (RQ9) |
| anything else | 400 |

The sender's `name` and `model` become `SenderInfo` (shown in the web app). A session SETUP on a second connection
while a session connection exists is accepted (iOS always allows interruption, SPEC-control.md 1.4 bit 17) and the
old session is ended at that connection's **stream SETUP** (R8). Until then both sessions exist: `PtpPort.Register`
for the new session's `PtpSlave` **replaces** the handler of an address the old session also registered (the newest
session's slave wins; the old slave is unregistered when its connection ends and never re-registers), only the old
session has a stream and feeds the `PlayoutBuffer`, and the old session's event channel stays open until its eviction.

### 4.4 Event channel

TCP listener bound to port 0 before the session SETUP is answered; one connection accepted; framing of SPEC-control.md
2.5 with the swapped event keys (SPEC-control.md 4.3). On accept the receiver sends `POST /command {type: "updateInfo",
value: <the /info dict>}` (shairport-sync behaviour; receiver A sends nothing, both are accepted by senders) and
answers nothing else. The channel is closed with the session. `FakeEventChannel` is the implementation to promote.

### 4.5 RECORD, SETPEERS, SETPEERSX, FLUSH

`RECORD` → 200 with `Audio-Latency: 0`. `SETPEERS`/`SETPEERSX` → 200; the addresses are added to the slave's accepted
sources. `FLUSH` (realtime; `RTP-Info: seq=…;rtptime=…`) → 200, drop every buffered frame and the output is flushed
(Section 5.5).

### 4.6 Stream SETUP

Body with `streams[0]` (SPEC-control.md 4.4). The connection must be encrypted (470 otherwise). `shk` must be 32 bytes
(400 otherwise). `type`:

| type | Setup | Answer |
|---|---|---|
| 96 | bind UDP data and control sockets (port 0); note `controlPort` of the sender (for 0xD5 requests); `spf` must be 352 and `sr` 44100 (400 otherwise) | 200 `{streams: [{type: 96, dataPort, controlPort, streamID}]}` |
| 103 | bind a TCP data listener (port 0); accept one connection; `audioFormat` must be one of R3's (else 400 and the formats logged); `controlPort` in the answer is a UDP control socket bound (port 0) for the stream but carrying no traffic (no 0xD5/0xD7 exists on a TCP stream, SPEC-audio.md 4.1), closed with the stream | 200 `{streams: [{type: 103, dataPort, controlPort, streamID, audioBufferSize: 8388608}]}` |
| other | | 400 |

`streamID` is a per-process counter starting at 1. The stream SETUP is the **claim** (R8): the receiver raises
`SenderStreamStarted` and the bridge pauses Spotify and routes the output; a second sender's stream SETUP ends the first
session. `/info` `statusFlags` gain bit 17 (`ReceiverSessionIsActive`) while a stream is set up (`[ap2r] ap2-receiver.py:564,
631, 794`); bit 17 is reflected in `/info` only, the TXT `flags`/`sf` are **not** re-announced for it (shairport-sync's
behaviour; 3.1 lists the only re-registration triggers).

### 4.7 SETRATEANCHORTIME and FLUSHBUFFERED

Bodies per SPEC-audio.md 4.3. `rate 1` sets the anchor `(networkTime, rtpTime)` on the playout buffer and resumes the
output; `rate 0` pauses the output and keeps the buffered frames; `networkTimeTimelineID` is recorded and compared with
the slave's grandmaster (a mismatch is logged, the anchor is applied anyway, RQ4). `FLUSHBUFFERED`: immediate form
drops frames with sequence below `flushUntilSeq` (23-bit modular compare), flushes the output and **pauses** it
(`PauseAsync`; nothing plays until the next `rate 1`, SPEC-audio.md 4.3: "stop playing, reset the anchor"); deferred
form (`flushFromSeq` present) registers a splice: when the block `flushFromSeq` arrives, blocks up to but excluding
`flushUntilSeq` are dropped without flushing or pausing the output. Both answer 200.

### 4.8 SET_PARAMETER, GET_PARAMETER, feedback, other

| Request | Handling |
|---|---|
| `SET_PARAMETER` `text/parameters` `volume: <dB>` | R9; raises `SenderVolume(level)`; 200 |
| `SET_PARAMETER` `application/x-dmap-tagged` | `DmapDecoder` (new, next to `DmapEncoder`): `mlit {minm, asar, asal, astm, …}`; unknown tags skipped; `RTP-Info rtptime` kept as the item's key; raises `SenderMetadata`; 200 |
| `SET_PARAMETER` `image/jpeg` / `image/png` | artwork bytes (≤ 1 MiB, larger answered 200 and dropped) with the `rtptime` key; raises `SenderArtwork`; 200 |
| `SET_PARAMETER` `text/parameters` `progress: a/b/c` | start/current/end RTP times; raises `SenderProgress`; 200 |
| `GET_PARAMETER` `volume\r\n` | 200 `text/parameters` `volume: <current G in dB with six decimals>\r\n` (R9, `VolumeMapping.FormatParameter`; the no-output default of R9) |
| `POST /feedback` | 200 with `{streams: [{type, sr: 44100}]}` while a stream is set up, else an empty body; refreshes the R16 liveness timer (every request does) |
| `POST /command` | 200, empty (bit 50 is not advertised; bodies are logged at debug level) |
| `POST /audioMode`, `SETMAGICCOOKIE`, `PUT /setProperty` | 200, empty |
| `POST /fp-setup` | R14 |
| `OPTIONS` | 200 with `Public: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, POST, GET, PUT` |
| unknown method or path | 501 / 404 |

### 4.9 TEARDOWN and session end

`TEARDOWN` with `{streams: [{streamID}]}` ends that stream (sockets closed, buffer dropped, output released, status
flag 17 cleared) and answers 200; `TEARDOWN` with `{}` ends the session: stream, event channel, PTP slave,
`SenderGone(reason)`. A body-less `TEARDOWN` is treated like `{}`, a **deliberate deviation** from shairport-sync's
no-op ("has no plist -- nothing done", SPEC-control.md 4.9): pyatv-style senders send it to end the session, and on a
session connection it cannot mean anything else. The same end-of-session runs when the connection closes, the liveness
timer (R16) fires, the bridge asks for it (R8: `EndSessionAsync(reason)`), or the receiver stops. `ControlSession`
serialises request handlers and `EndSessionAsync` with one `SemaphoreSlim` (the fake's one read loop assumed no
concurrent end); a handler that finds the session ended when it acquires the lock answers 451.

## 5. Audio plane as a receiver

### 5.1 Realtime stream (type 96)

UDP data packets: RTP header, ChaCha20-Poly1305 payload with `shk` (SPEC-audio.md 1.2-1.3), 352 ALAC frames each.
The receiver (promoted from `FakeReceiver.Audio`) tracks the 16-bit sequence, asks for gaps with 0xD5 to the sender's
control port (8-byte form) within 30 ms of detecting them and at most three times per gap, accepts 0xD6 wrapped
replies on the control socket and raw retransmissions on the data socket, and decodes with `AlacDecoder` (magic cookie
for 352/16/2 from `AlacMagicCookie.Default44100Stereo16`). Packets whose RTP timestamp is more than 2 s ahead of the next due
frame (a corrupt timestamp) or whose release time (R6) is more than `LateThreshold` in the past are counted and
dropped; a normally arriving realtime packet is neither: its `T - Lpipe` is about 0.45 s in the past on arrival, and the
per-stream offset `D` of R6 makes it play `D` late instead of being dropped (5.4).

Timing in this mode comes from 0xD7 sync packets on our control port (SPEC-audio.md 4.4 table): `now_frame` at PTP
time `T` with the grandmaster id; the pair `(T, now_frame)` is the anchor for RTP timestamp → play time. Until the
first sync, frames are released anchor-only (R6). The marker bit on the first packet after FLUSH re-anchors on the
next sync (and `D` is learnt again). Realtime senders send no `SETRATEANCHORTIME`, so the playout buffer resumes the
output itself: the first frame released while the output is paused (at stream start and after the 2 s silence pause of
5.5) is preceded by `ResumeAsync`; the trigger is that first released frame, not the 0xD7 sync (5.3, 5.5).

### 5.2 Buffered stream (type 103)

TCP data connection, 2-byte length-prefixed blocks laid out as SPEC-audio.md 4.2: 23-bit sequence, RTP timestamp,
SSRC = format code, encrypted payload. Decoder selection per block: SSRC `0x16000000` → AAC-LC/44.1 (1024 frames
per block); `0x0000FACE` → ALAC/44.1/16; any other SSRC → the decoder of the stream SETUP's `audioFormat`, and the
SSRC is logged once (RQ3). The reader keeps at most `AudioBufferSize` (8 MiB) of undecoded blocks and stops reading the
socket when full (TCP window is the flow control, as `[sps]` does); blocks whose release time (R6) is more than `LateThreshold`
in the past are dropped without decrypting. AAC blocks get a 7-byte ADTS header prepended before decoding; the first block after a start,
flush or discontinuity is decoded and muted (`[sps] ap2_buffered_audio_processor.c:578-583`).

### 5.3 Playout buffer

`PlayoutBuffer` (library) is the one place where decoded frames wait:

- Input: `Enqueue(rtpTimestamp, sequence, ReadOnlyMemory<short>)` from either stream reader (decoded, S16 stereo).
- Anchor: `SetAnchor(rtpTime, senderTimeNs, timelineId)` from SETRATEANCHORTIME or 0xD7; `Pause()`, `Resume()`,
  `Flush(untilSeq?)`, `Splice(fromSeq, untilSeq)`.
- Clock: `IPtpSlave.TryMapToLocal(senderTimeNs, out localNs)`; when it has no estimate the anchor-only mapping
  of R6 applies (`AnchorLead` default 300 ms). `now` is the one clock of the process: `AirplaySource` passes
  `AirplaySender.Clock` (the sender's `IMasterClock`, which the `GroupPlayer` pacer schedules by) to `AirplayReceiver`,
  and the buffer and the `PtpSlave` read it through an `IMonotonicClock` adapter over that instance, so the comparison
  below and the pacer's schedule are on the same timeline (6.2).
- Release thread (one per buffer, `PrecisionSleeper`): writes the next frame to `IReceiverAudioOutput.WriteAsync` when
  `now >= release(frame) = T(frame) - Lpipe + D` (R6). `D` is learnt from the first frame after a (re-)anchor,
  `D = max(0, Lpipe - (T(first) - now))`: 0 for buffered streams, about 0.45 s for realtime streams (5.4); it is kept
  until the next anchor, FLUSH or re-anchor marker and shown in the statistics. Frames whose release time is more than
  `LateThreshold` (200 ms) in the past are dropped and counted (`LateFrames`); gaps in the sequence that are not filled
  by the time the next frame is due are filled with silence of the missing length (`MissingFrames`). Before the first
  frame released while the output is paused the thread calls `ResumeAsync` (5.1, 5.5).
- Drift: every 10 s the buffer compares the cumulative written frames with the elapsed local time at the anchor's rate;
  more than `DriftThreshold` (20 ms) ahead → one frame is dropped per block until within threshold; behind → one frame
  repeated per block. Counters `DroppedForDrift`, `RepeatedForDrift`.
- Statistics snapshot for Diagnostics: buffered milliseconds, `D`, late, missing, drift corrections, decoder, stream type.

### 5.4 `Lpipe` and the output

The output is the existing `IGroupPlayer`: a PCM ring (`RingHighWaterFrames` 44 100, prefill 8 820), a pacer that
takes 352 frames per packet and sends them `L` (`LatencyFrames`, default 77 175) ahead of their audible time, plus the
receiver-side lead of 11 025 frames (SPEC-audio.md 1.5). For the AirPlay source the ring must stay shallow, otherwise
its fill adds a variable delay: `Lpipe = LatencyFrames + 11025 + PrefillFrames` frames (≈ 2.2 s at the defaults), and
the release thread never writes more than `PrefillFrames + 352` frames ahead of the pacer's read position
(`IGroupPlayer` exposes the existing `GroupPlayer.QueuedFrames`, 8.6). Realtime senders deliver a packet **1.75 s**
before its intended play time: the 0xD7 sync anchors `now_frame` at `T` and `L = 77175` frames at offset 16
(SPEC-audio.md 4.4 table); the 2.0 s figure holds only at a shairport-sync receiver, which adds its own 11 025 frames.
That lead is shorter than `Lpipe`, so the R6 offset is `D = Lpipe - 1.75 s`, about 0.45 s, and every realtime frame
plays `D` late rather than being dropped (a sender that applied the 11 025 margin itself would get `D` of about 0.2 s).
Buffered senders deliver seconds ahead, `D = 0`, and their frames are released on time. The audible delay behind the
sender's own display is therefore `D` plus our receivers' own fixed delay beyond the 11 025 lead (about 0.25 s on
shairport-sync, SPEC-audio.md 8.2): about 0.25 s for buffered and about 0.45 s more (0.7 s) for realtime streams at the
default latency; a smaller `L` shrinks `D` (RQ7 measures both).

### 5.5 Pause, flush, volume, metadata into the output

The library's only view of the outside world for audio is this interface; the bridge implements it against the
output manager (8.3), tests with a recorder:

```csharp
public interface IReceiverAudioOutput
{
    /// <summary>Released frames (S16 interleaved stereo, 44.1 kHz) in play order; may block briefly for backpressure.</summary>
    ValueTask WriteAsync(ReadOnlyMemory<short> frames, CancellationToken cancellationToken);
    Task PauseAsync(CancellationToken cancellationToken);
    Task ResumeAsync(CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
    Task SetMasterLevelAsync(int level, CancellationToken cancellationToken);
    Task SetNowPlayingAsync(TrackMetadata metadata, ReadOnlyMemory<byte>? artwork, string? artworkContentType, TimeSpan elapsed, CancellationToken cancellationToken);
    /// <summary>The session ended; the output is no longer owned by the receiver.</summary>
    Task ReleaseAsync(CancellationToken cancellationToken);
    /// <summary>Frames queued in the output ahead of its pacer: the existing <c>GroupPlayer.QueuedFrames</c> lifted onto <c>IGroupPlayer</c> (5.4, 8.6); 0 when no output is connected.</summary>
    int QueuedFrames { get; }
}
```

| Sender action | Output action (through `IReceiverAudioOutput`) |
|---|---|
| `SETRATEANCHORTIME rate 0`, or realtime stream silent for 2 s | `PauseAsync` (the group player's pause: receivers flushed, as the Spotify sink does) |
| `SETRATEANCHORTIME rate 1` | `ResumeAsync`, then frames per R6 |
| realtime stream (type 96): the first frame released while the output is paused (stream start, after the 2 s silence pause, after `FLUSH`) | `ResumeAsync`, then frames per R6 (5.1: realtime senders send no `SETRATEANCHORTIME`, so without this row the pacer never starts and `GroupPlayer.Write` blocks on a full ring) |
| `FLUSH` (realtime) | buffer dropped, `FlushAsync` (receivers re-anchor on the next packet); the next released frame resumes per the row above |
| immediate `FLUSHBUFFERED` | frames below `flushUntilSeq` dropped, `FlushAsync`, then `PauseAsync`; nothing plays until the next `rate 1` (4.7) |
| deferred `FLUSHBUFFERED` | buffer splice only; the output never notices (gapless track change) |
| `SET_PARAMETER volume` | `SetMasterLevelAsync(level)` (the bridge implementation supplies `OutputManager.SourceAirplay`, 8.3) |
| DMAP + artwork + progress with one `rtptime` | `SetNowPlayingAsync(metadata, artwork, elapsed)` once all three with the same key arrived or 500 ms after the first, with `elapsed = (rtpTimestamp of the next frame the buffer will release − start) / 44100`, read from the `PlayoutBuffer` at the moment of the call (the player defines `elapsed` as the track position of the next sample written, `GroupPlayer.SetNowPlayingAsync`, and the next released frame is `Lpipe - D` behind the sender's `current`); only when the buffer is empty is the sender's `(current − start) / 44100` used |
| `TEARDOWN`, connection lost, evicted | `ReleaseAsync`: `PauseAsync` first (left running, the pacer would send 5 s of silence and auto-pause with a FLUSH, `GroupPlayer.SilenceTimeout`), then the output is unowned; receivers stay connected, as after a Spotify stop |

## 6. Timing: `PtpPort`, `PtpSlave`, `PtpMaster`

### 6.1 `PtpPort` (in `Airplay2.Sender/Streams`)

Owns the two `UdpReceiveLoop`s on 319/320 (bind address `Bridge:LocalAddress` or any, ports from `PtpPortOptions`;
port 0 in tests) and routes every datagram by **message type and source**: Sync, Follow_Up, Announce and Signaling from
an address registered with `Register(IPAddress source, IPtpEndpoint slave)` go to that slave; everything else
(Delay_Req, Pdelay_Req, Delay_Resp, and any message from an unregistered address) goes to the default handler, the
master, which keeps today's `ForeignAnnounce`/`FirstMessageFromPeer` behaviour; without a default handler such
datagrams are counted and dropped. Routing by type, not by address alone, lets one port serve both roles when the peer
is also 127.0.0.1 (the Bridge loopback test of 11.1). `Register` replaces an existing handler for the address (4.3);
`Unregister(source, handler)` removes it only while it is still that handler. Sending:
`SendAsync(IPEndPoint, ReadOnlyMemory<byte>, eventPort: bool)`. Binding is `EnsureBoundAsync` (idempotent, on first
use by either role); a failure raises `PtpPortException`. `PtpMaster` is refactored to take an `IPtpPort` instead of
binding its own sockets; `PtpMasterOptions.EventPort/GeneralPort` move to `PtpPortOptions`, and `PlaybackProbes` and
the CLI (`CliCommands`, `ProbeCommand`), which read them, are files the `ptp-port` package touches. Existing PTP tests
keep passing with a port per test (port 0).

Wiring: the bridge creates **one** `PtpPort` at start (`Program.cs`, wave 0 scaffold) and hands it to both roles.
`AirplaySenderOptions.PtpPort` (new, `IPtpPort?`) is what `AirplaySender.EnsurePtpMasterAsync` builds the grandmaster
on; `AirplaySenderProvider` passes the bridge's instance, and `AirplaySource` gives the same instance to the receiver's
`PtpSlave`. When the option is null (CLI, probes, tests) the sender creates and binds a private `PtpPort`, so it binds
319/320 exactly as today. A bind failure keeps today's sender behaviour: `EnsurePtpMasterAsync` throws
`PtpMasterException` (wrapping the `PtpPortException`) and the connect to a PTP receiver fails with that reason; the
receiver role logs the same failure as a warning once and runs anchor-only (R6). The bridge disposes the port at
shutdown after the sender and the receiver.

### 6.2 `PtpSlave` (in `Airplay2.Receiver/Timing`, promoted from `FakePtpSlave`)

Per sender session: accepts the sender's addresses (session SETUP `timingPeerInfo.Addresses`, the connection's remote
address, SETPEERS additions). On each two-step Sync + Follow_Up pair it computes `offset = t2(local receive of Sync) −
t1(preciseOriginTimestamp)`; the estimate is the minimum over a sliding window of 8 pairs (network delay only adds,
as nqptp assumes, SPEC-audio.md 4.4), re-based when the grandmaster identity changes. `TryMapToLocal(senderNs)` =
`senderNs + offset` in local monotonic nanoseconds, read from the `IMonotonicClock` adapter over the process's one
`IMasterClock` (`AirplaySender.Clock`, 5.3), the clock `PtpMaster` and the `GroupPlayer` pacer use. Announces from the
sender are recorded (grandmaster id, priority); at session start the slave sends two Announces of its own clock id as
nqptp does, to the sender's addresses on `PtpSlaveOptions.PeerGeneralPort` (default 320; Delay_Req, when enabled, goes
to `PeerEventPort`, default 319; the loopback tests set both to the test master's ports). `SendDelayRequests` is an
option, off by default (RQ5). Statistics:
offset, pairs seen, last Sync age.

## 7. Decoders

### 7.1 ALAC

`AlacDecoder` (existing, 16-bit). A `Default44100Stereo16` magic cookie (frame length 352, bit depth 16, 2 channels,
the fmtp defaults of SPEC-audio.md 5.2) is added to `AlacMagicCookie`. Buffered ALAC blocks (`0x0000FACE`) use the same
decoder with frame length 352.

### 7.2 AAC-LC through libavcodec

`IAacDecoder { bool TryDecode(ReadOnlySpan<byte> adtsBlock, Span<short> pcmOut, out int frames); void Reset(); }`
with `IAacDecoderFactory.TryCreate(out IAacDecoder?, out string? reason)`. `FfmpegAacDecoder` (in
`Airplay2.Receiver/Codecs`) uses FFmpeg.AutoGen (`DynamicallyLoadedBindings`, `ffmpeg.RootPath` from
`Bridge:Airplay:FfmpegPath` or the default search path): `avcodec_find_decoder(AV_CODEC_ID_AAC)`,
`avcodec_alloc_context3`, `avcodec_open2`, `avcodec_send_packet` with the ADTS block, `avcodec_receive_frame`; output
`fltp` or `s16p` planar frames are interleaved and converted to S16 with clipping. One decoder per stream, `Reset`
flushes (`avcodec_flush_buffers`). Not thread-safe; owned by the buffered reader.

Versions: the FFmpeg major that the pinned FFmpeg.AutoGen package targets (7.x at the time of writing; the exact package
version is fixed in `Directory.Packages.props` and recorded in `REFERENCE-SOURCES.md` with the licence note: the FFmpeg libraries
LGPL-2.1+, dynamically linked, no GPL components; the FFmpeg.AutoGen 7.x bindings **LGPL-3.0**, the `LICENSE.txt`
inside package 7.1.1 as verified on 2026-10-26, while the repository has since relicensed later majors under MIT, so
the note is re-verified whenever the pin changes). Linux/container: the distribution's `libavcodec`/`libavutil`/`libswresample`
packages of that major in the runtime image (Section 10). Windows: an LGPL **shared** build of that major copied next to
`bridge.exe` by `tools/get-ffmpeg.ps1` (download, verify the published checksum, extract the `avcodec-*.dll`,
`avutil-*.dll`, `swresample-*.dll` only); `build.ps1` calls it unless `-SkipFfmpeg`; the Windows installer copies what
is in `artifacts/bridge`.

Tests: `tests/Airplay2.Receiver.Tests` has `aac-44100-stereo-1024.json`-style vectors under
`docs/airplay2/test-vectors/`: a 2 s 440 Hz tone encoded once with the ffmpeg CLI (`-c:a aac -b:a 128k -f adts`) and split by a
generator script into a JSON array of base64 **raw** AAC frames with the 7-byte ADTS headers stripped (the test
prepends them again as 5.2 does, so nothing is double-framed; the ffmpeg command and the script are recorded next to
the vector), plus the expected RMS and frequency of the decoded output (bit-exactness is not expected from AAC). The decode tests run when the native library loads and otherwise pass
with the output "skipped: libavcodec not found" (the repository's gate pattern, DEVELOPMENT.md "Live tests"); the
container build step runs them with the library present.

## 8. Bridge integration

### 8.1 Components

```
 iPhone / Mac ──(AirPlay 2: mDNS, RTSP/HAP, PTP, RTP/TCP)──┐
                                                           ▼
 Bridge.Service
 ├─ AirplaySource (hosted service) ── Airplay2.Receiver: AirplayReceiver ── PlayoutBuffer ──┐
 ├─ SpotifyHost ── librespot ── AirplaySink ───────────────────────────────────────────────┤
 │                                                                                         ▼
 ├─ SourceSwitch ── owner: none | spotify | airplay; claims, evictions, frame gate
 │                                                                                         ▼
 ├─ OutputManager ── IGroupPlayer (unchanged pipeline)
 ├─ PtpPort (shared) ◄── PtpMaster (OutputManager's PTP transports) and PtpSlave (AirplaySource)
 └─ Api / EventHub / SqliteBridgeStore / Bridge.Web
```

### 8.2 `SourceSwitch`

Singleton. State `Owner ∈ {None, Spotify, Airplay}`, `SenderInfo?`. **Every** call a source makes on the one
`IGroupPlayer` is gated by ownership, not only the per-frame writes: `MayWrite(source)` is the per-write gate both
sinks check (a volatile read; frames of a non-owner are counted and dropped), and the player commands (`StartAsync`/
`PauseAsync` queued by `AirplaySink.Start()`/`Stop()`, and `FlushAsync`, `SetNowPlayingAsync`, `SetSourceLevelAsync`
from `PlayerEventMapper` and `AirplaySource`) go through `OutputManager`, which drops them for a non-owner (counted,
logged at debug level). A dropped `Start` is remembered: when that source later gains ownership the switch calls
`AirplaySink.StartIfRunning()` (Spotify) or `AirplaySource.ResumeIfPlaying()` (AirPlay), so the player is started once,
by its owner, and Spotify's `StartAsync` never re-anchors a player AirPlay still owns.

`ClaimAsync(source)` runs the eviction to completion **before** it grants: Spotify is evicted with
`SpotifyHost.TryCommand("pause")`, and the claim awaits the `PauseAsync` that the resulting `AirplaySink.Stop()`
queues (the sink exposes the `Command.Done` of its last pause as `Task LastPauseCompleted`), at most 2 s; AirPlay is
evicted with `AirplaySource.EndSessionAsync("replaced by Spotify")`, which completes after the receiver's
`ReleaseAsync` (5.5: pause, then unowned). Only then the owner is set and `statusChanged` is published, and only then
may the claimant touch the player (`AirplaySource` calls `ResumeAsync` after Spotify's pause has flushed the receivers;
without this order the queued pause drops the ring and stops the pacer under the AirPlay stream, and
`GroupPlayer.Write` blocks forever on its first full ring). `Release(source)` sets `None` when that source owns it.
Triggers: `PlayerEventMapper`'s `Playing` event → `ClaimAsync(Spotify)`; `AirplaySource` on `SenderStreamStarted` →
`ClaimAsync(Airplay)`; `SenderGone`/stream end → `Release(Airplay)`; Spotify's `Stopped` → `Release(Spotify)`
(Spotify's pause keeps the ownership, as today the receivers stay connected). Default owner at start: `None`; Spotify's
first write while `None` claims (keeps today's behaviour when nothing else happens).

The **playback view belongs to the owner**. `RuntimeState.Playback` and `playbackChanged` are written for `Owner` only:
`PlayerEventMapper` keeps its DTO in `RuntimeState.SpotifyPlayback` and publishes it as `Playback` only while
`Owner != Airplay` (so the `Paused` event the AirPlay eviction itself causes cannot flip the web app back to the Spotify
view); `AirplaySource` keeps `RuntimeState.AirplayPlayback` and publishes only while `Owner == Airplay`; the switch
republishes the new owner's view (an empty one for `None`) on every ownership change, so `GET /api/playback` and the
events agree. `StatusDto.ActiveSource` is `Owner` (null for `None`), read from the switch by `DtoFactory`.

### 8.3 `AirplaySource`

Hosted service; builds `AirplayReceiver` from settings (enabled, name, password, identity) and `BridgeOptions.Airplay`
(RTSP port, ffmpeg path, PTP options; the bind address is `Bridge:LocalAddress`, 8.5), and gives it the sender's clock
(`AirplaySender.Clock`, 5.3) and the shared `PtpPort` (6.1); implements `IReceiverAudioOutput` against
`OutputManager`: `WriteAsync` → the active player's `Write` when `MayWrite(Airplay)`; `PauseAsync`/`ResumeAsync`/
`FlushAsync` → the player through `OutputManager`'s ownership gate (8.2); `SetMasterLevelAsync(level)` →
`OutputManager.SetSourceLevelAsync(level, OutputManager.SourceAirplay)` (the generalisation of today's
`SetSpotifyVolumeAsync`, see 8.6); `SetNowPlayingAsync` → `OutputManager.SetNowPlayingAsync`. Applies settings changes
per 3.3 (name and password without a restart or a session end; `enabled` starts or stops the receiver) and keeps running
when the output changes (the player reference is read per write). Exposes its state as properties (`ReceiverStatus`,
`SenderInfo`, `RuntimeState.AirplayPlayback` with title, artist, album, duration, position and artwork key, the
Diagnostics snapshot); `DtoFactory` (package `api`) reads them when it builds `StatusDto`, `PlaybackDto` and
`DiagnosticsDto`, and `AirplaySource` raises `statusChanged`/`playbackChanged` through `EventHub` with DTOs built by
`DtoFactory`, never with DTO code of its own (8.2 says when it may publish).

### 8.4 Store

Migration to `user_version 2`:

```sql
CREATE TABLE IF NOT EXISTS airplay_controllers (
    controller_id TEXT PRIMARY KEY,   -- the sender's HAP pairing identifier
    public_key    BLOB NOT NULL,      -- its Ed25519 LTPK, 32 bytes
    created_at    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);
```

New `settings` keys: `airplay_enabled` (`true`/`false`, default true), `airplay_name` (default: the device name),
`airplay_password` (null = open), `airplay_identity` (JSON `{deviceId, psi, seed}`; created once).

### 8.5 Configuration (`BridgeOptions.Airplay`)

| Key | Default | Meaning |
|---|---|---|
| `Bridge:Airplay:Enabled` | `true` | start the receiver (the stored setting wins) |
| `Bridge:Airplay:RtspPort` | `7000` | RTSP listener |
| `Bridge:Airplay:PtpEventPort` / `PtpGeneralPort` | `319` / `320` | the shared `PtpPort` |
| `Bridge:Airplay:FfmpegPath` | (search path) | directory of the libavcodec libraries |
| `Bridge:Airplay:AnchorLeadMs` | `300` | R6 anchor-only lead |
| `Bridge:Airplay:SessionTimeoutSeconds` | `30` | R16 |
| `Bridge:LocalAddress` (existing) | any | reused as the bind address of the RTSP listener and the `PtpPort`; there is no separate `Bridge:Airplay:BindAddress` |

### 8.6 `IPlaybackOutput` / `OutputManager` changes

`SetSpotifyVolumeAsync(level)` becomes `SetSourceLevelAsync(level, source)` (sources `spotify`, `airplay`;
`OutputManager.SourceSpotify` exists, `SourceAirplay` is added); `SpotifyVolumeUpdates` are published only while
Spotify owns the output. `OutputManager` gains the ownership gate of 8.2 for player commands. `IGroupPlayer` gains
`QueuedFrames`: the existing `GroupPlayer.QueuedFrames` (ring count) lifted onto the interface for 5.4, not a second
name. These signature changes, `SourceAirplay`, `AirplaySenderOptions.PtpPort` and the `Program.cs` registrations of
`SourceSwitch`, `AirplaySource` and the shared `PtpPort` are made in the wave 0 scaffold (Section 13) so the wave 3
packages compile against them from the start. Everything else in the output path is unchanged.

## 9. API, events and web app

### 9.1 DTOs (`Bridge.Contracts`)

| DTO | Change |
|---|---|
| `SettingsDto` | `+ bool? AirplayEnabled, string? AirplayName, string? AirplayPassword, bool? AirplayPasswordSet`. In a `PUT`, null keeps the value (as `ForgetOfflineAfterDays`), `AirplayPassword = ""` clears the password, any other string sets it; `AirplayPasswordSet` is ignored. Answers always carry `AirplayEnabled`, `AirplayName` and `AirplayPasswordSet`, and never the password (`AirplayPassword` is null) |
| `StatusDto` | `+ string? ActiveSource` (`"spotify"`, `"airplay"`, null: `SourceSwitch.Owner`, 8.2), `+ AirplayReceiverDto? Airplay` = `(string State, string Name, string? SenderName, string? SenderModel, bool AacAvailable)` with `State ∈ off, starting, advertising, connected, streaming, error` |
| `PlaybackDto` | `+ string? Source` (`"spotify"`, `"airplay"`, null; always the owner's view, 8.2); for AirPlay `ArtworkUrl` = `/api/playback/artwork?v=<rtptime>` |
| `DiagnosticsDto` | `+ AirplayDiagnosticsDto? Airplay` = `(int RtspPort, bool PtpBound, long? PtpOffsetNs, string? StreamType, string? Format, int BufferedMs, int StreamOffsetMs, long LateFrames, long MissingFrames, long DriftCorrections, string? DecoderStatus)` |

Routes: `GET /api/playback/artwork` → the sender's artwork bytes with its content type and an `ETag` of the `rtptime`;
404 when the source is not AirPlay or no artwork arrived. `POST /api/playback` commands answer 409 while the source is
AirPlay (N2). WebSocket: no new message types; `statusChanged` and `playbackChanged` carry the new fields.

### 9.2 Web app (UI-SPEC.md conventions; the copy goes into every language of the catalog)

- **Player hero** (UI-SPEC 4.3): when `Playback.Source == "airplay"` the chip is `.chip--accent` "AirPlay · {{sender}}"
  (player.airplayChip; player.airplayOnly without a sender name) instead of the Spotify chip; title/artist/album/progress
  from `PlaybackDto` as today; the transport buttons are disabled with the hint player.airplayNoControl in the `.meta`
  slot. The master volume block is unchanged (the slider follows the sender's volume; player.followsSender replaces
  player.followsSpotify while AirPlay owns the output).
- **Settings** (UI-SPEC 4.6): a new card **AirPlay receiver** between Bridge and Appearance: settings.airplay (title),
  settings.airplayEnabled (checkbox row), settings.airplayName (text, hint settings.airplayNameHint: "how the speaker
  appears on Apple devices"), settings.airplayPassword (text, hint settings.airplayPasswordHint: "Apple devices ask for
  it; empty means anyone on the network"), with the page's validation and save pattern.
- **Diagnostics** (UI-SPEC 4.7): a fourth card **AirPlay receiver** from `DiagnosticsDto.Airplay` and `StatusDto.Airplay`:
  diagnostics.airplayState (state word, diagnostics.airplayStates.*), diagnostics.airplaySender, diagnostics.airplayStream
  ("{{type}} · {{format}}"), diagnostics.airplayBuffer ("{{ms}} ms buffered · offset {{offset}} ms", the `D` of R6), diagnostics.airplayLate ("{{late}} late ·
  {{missing}} missing · {{drift}} drift"), diagnostics.airplayPtp (offset in ms or diagnostics.airplayPtpNotBound),
  diagnostics.airplayDecoder.
- **Receivers page**: unchanged (R12 keeps us out of the list).
- **Catalog keys.** The `i18n` package adds them to all seven `Resources/i18n/*.json` catalogs in wave 3, before the UI
  packages run, and records them in UI-SPEC.md section 10; the UI packages never touch `Resources/i18n`
  (`CatalogUsageTests` and `I18nTests` would otherwise make three parallel packages edit the same seven files). de is
  the source language; the other five are translated by `i18n` in the catalog's register:

| Key | de | en |
|---|---|---|
| player.airplayChip | AirPlay · {{sender}} | AirPlay · {{sender}} |
| player.airplayOnly | AirPlay | AirPlay |
| player.airplayNoControl | Steuerung am Apple-Gerät | Control it on the Apple device |
| player.followsSender | folgt dem Sender | follows the sender |
| settings.airplay | AirPlay-Empfänger | AirPlay receiver |
| settings.airplayEnabled | Als AirPlay-Lautsprecher anbieten | Offer as an AirPlay speaker |
| settings.airplayName | Name | Name |
| settings.airplayNameHint | So erscheint der Lautsprecher auf Apple-Geräten | How the speaker appears on Apple devices |
| settings.airplayPassword | Passwort | Password |
| settings.airplayPasswordHint | Apple-Geräte fragen danach; leer heißt: alle im Netzwerk | Apple devices ask for it; empty means anyone on the network |
| diagnostics.airplay | AirPlay-Empfänger | AirPlay receiver |
| diagnostics.airplayState | Zustand | State |
| diagnostics.airplayStates.off / starting / advertising / connected / streaming / error | Aus / Startet / Sichtbar / Verbunden / Spielt / Fehler | Off / Starting / Advertising / Connected / Streaming / Error |
| diagnostics.airplaySender | Sender | Sender |
| diagnostics.airplayStream | {{type}} · {{format}} | {{type}} · {{format}} |
| diagnostics.airplayBuffer | {{ms}} ms gepuffert · Versatz {{offset}} ms | {{ms}} ms buffered · offset {{offset}} ms |
| diagnostics.airplayLate | {{late}} verspätet · {{missing}} fehlend · {{drift}} Drift | {{late}} late · {{missing}} missing · {{drift}} drift |
| diagnostics.airplayPtp | PTP-Versatz {{ms}} ms | PTP offset {{ms}} ms |
| diagnostics.airplayPtpNotBound | PTP-Port nicht gebunden (Anker-Timing) | PTP port not bound (anchor-only timing) |
| diagnostics.airplayDecoder | Decoder | Decoder |

## 10. Deployment

| Shape | Changes |
|---|---|
| All | New listeners: TCP `Bridge:Airplay:RtspPort` (7000), UDP 319/320, per-session UDP data/control (dynamic) and TCP event/data ports (dynamic). mDNS 5353 is already open. |
| Windows Service | `install-service.ps1`: rules "TuneShuttle AirPlay RTSP" (TCP 7000) and "TuneShuttle PTP" (UDP 319-320); the program rule covers the dynamic ports. `build.ps1` runs `tools/get-ffmpeg.ps1` (Section 7.2) so `artifacts/bridge` carries the ffmpeg DLLs; the installer copies them with the rest. |
| systemd | `AmbientCapabilities=CAP_SYS_NICE CAP_NET_BIND_SERVICE` (319/320 are privileged ports); the install notes list the `libavcodec` package of the pinned major. |
| Container | `docker/Dockerfile` runtime stage: `apt-get install` the `libavcodec`, `libavutil`, `libswresample` packages of the pinned major from the base image's Debian release (the image's Debian release and the package names are verified during implementation and recorded in the Dockerfile comment); `EXPOSE 7000/tcp 319/udp 320/udp`; the macvlan note already covers inbound UDP. |

README "Firewall and network" and ARCHITECTURE.md "Deployment" list the ports; TEST-RECEIVER.md gets the note that
the product receiver and the shairport-sync container cannot share a host (R13).

## 11. Tests

### 11.1 Offline (always run)

- Unit: `DmapDecoder`, volume dB ↔ level (R9, `VolumeMapping` round trip and the no-output default), TXT/`/info`
  builders (R15 `Default` and `AlacOnly`, 3.1-3.2), `PlayoutBuffer` (anchor mapping; the R6 release rule and the learnt
  offset `D` with a fake clock: a realtime-like lead of 1.75 s plays `D` late and drops nothing, a buffered lead plays on
  time, frames past `release + LateThreshold` are dropped; late/missing/drift counters; flush and splice semantics; an
  immediate flush pauses and a deferred one does not; `ResumeAsync` before the first frame released while paused),
  `PtpSlave` offset estimation (fed synthetic Sync/Follow_Up pairs with known offsets and delays), `PtpPort` routing by
  type and source (both peers on one address), buffered block framing and SSRC selection, ADTS header construction,
  `FfmpegAacDecoder` (when the library loads, 7.2).
- Loopback (`Airplay2.Receiver.Tests`): our own `Airplay2.Sender` drives the product receiver on loopback with port 0:
  discovery through a loopback `MdnsResponder`/browser pair; transient pairing and pair-verify; password pairing
  (regular pair-setup, controller persisted, pair-verify on a second connection, wrong password → `Error 2`); session
  SETUP under PTP with the sender's `PtpMaster` on its own `PtpPort` (port 0) and the receiver's `PtpSlave` on a second
  `PtpPort` whose `PeerEventPort`/`PeerGeneralPort` point at the master's ports (both ends are 127.0.0.1, 6.1), the
  slave following the master (offset within 5 ms of the injected truth); realtime stream of a 3 s tone (via `RealtimeStreamTransport` under PTP: 0xD7
  syncs, dropped packets → 0xD5 → recovered); buffered ALAC stream via `BufferedStreamTransport` with pause, seek
  (immediate flush), splice (deferred flush); buffered AAC via a test-only raw block writer replaying the AAC vector
  (the sender has no AAC encoder); output captured by a recording `IReceiverAudioOutput` and compared against the
  tone (RMS, continuity, sample count within one packet); SET_PARAMETER volume/DMAP/artwork/progress → events;
  `/feedback` liveness; TEARDOWN variants; eviction by `EndSessionAsync`.
- Bridge (`Bridge.Tests`): `SourceSwitch` claims and evictions with stubbed sources (a claim grants only after the
  evicted source's pause or session end completed; a non-owner's Start/Pause/Flush/now-playing is dropped and a
  remembered Start replayed on ownership; the playback view and `ActiveSource` follow the owner, including through the
  `Paused` event an eviction causes); `AirplaySource` against a stub player (writes gated, pause/flush/now-playing
  forwarded with the buffer-derived `elapsed`, level set with source `airplay`, `ReleaseAsync` pauses first); migration to version 2;
  settings round trip with masking; DTO/JSON shape; `/api/playback/artwork`; self-exclusion in `ReceiverInventory`;
  `PtpPort` shared between an `OutputManager` PTP transport and the receiver in one process (ports 0; the routing by
  message type of 6.1 separates the two roles although both peers are 127.0.0.1).
- Web (bUnit): hero chip and disabled transport for source `airplay`, the Settings card (load, validation, save
  payload with null-keeps), the Diagnostics card, every new catalog key present in every language (the existing
  catalog completeness test of `Bridge.Tests/Web` covers new keys automatically).
- `RepositoryHygieneTests`: every live test gated; `tools/privacy-scan.ps1` passes (no sender names, addresses or
  identities in tracked files; test fixtures use synthetic identities).

### 11.2 Secondary

shairport-sync in Docker cannot act as a sender. pyatv streams only the RAOP/AirPlay 1 path with legacy pairing
(`[pyatv] docs/documentation/supported_features.md:30`), which we do not accept (R1), so it is not a test sender
either. The loopback tests with our own sender are the automated coverage; Apple senders are covered by 11.3.

### 11.3 Live probes (the owner's devices; results recorded in SPEC.md section 4 style and in a new MANUAL-TESTS.md section 9 "AirPlay receiver probes (RP1-RP12)", added in wave 5)

| # | Question | Pass criterion |
|---|---|---|
| RP1 | Does an iPhone list the receiver and connect? | "TuneShuttle" in Control Center; `GET /info`, `/fp-setup`, pairing, session SETUP (PTP) in the log |
| RP2 | Which stream type and format does the Music app use? Another app (e.g. a podcast app, Safari video)? | stream SETUP logged with `type`, `audioFormat`, SSRC of the first block |
| RP3 | Does audio play on the selected output within 3 s of pressing play, and does the sender's progress match the audible position within the expected delay (5.4)? | listening + log timestamps |
| RP4 | Pause, resume, seek, next track, previous track from the phone | correct behaviour, no stuck audio, FLUSHBUFFERED forms logged |
| RP5 | Does iOS keep streaming for 10 minutes with the passive PTP slave? Does it send Delay_Req-related messages or complain? | no disconnect; PTP statistics in Diagnostics |
| RP6 | Volume buttons and the volume slider on the phone | master level follows; `GET_PARAMETER` answers accepted |
| RP7 | Metadata, artwork and progress appear in the web app and on the speaker's display (if any) | web app shows them within 2 s of a track change |
| RP8 | Take-over both ways: AirPlay while Spotify plays; Spotify play while AirPlay plays; a second phone | R8 behaviour observed on both phones |
| RP9 | Password mode: iOS asks for the password; wrong password refused; reconnect without a new prompt | pair-setup M1-M6 and later pair-verify in the log |
| RP10 | macOS sender (Music app and system output) | as RP1-RP4 |
| RP11 | Does iOS accept the receiver when libavcodec is absent (ALAC-only formats)? What does it send then? | logged; decides whether the ALAC-only fallback is useful |
| RP12 | Rename and disable/enable from the Settings page while a phone shows the AirPlay menu | the menu follows within 10 s |

## 12. Open questions (RQ) and their defaults

| # | Question | Default until answered | Probe |
|---|---|---|---|
| RQ1 | Does iOS require bit 46 (HomeKit pairing) or bit 51 for the password flow? | R15 word (no 46/51), as shairport-sync | RP9 |
| RQ2 | Does iOS accept `updateInfo` on the event channel, or must we stay silent like receiver A? | send it (shairport-sync behaviour) | RP1 |
| RQ3 | Which SSRC does an Apple sender use for buffered ALAC/44.1/16, if ever? | decode unknown SSRCs by the stream SETUP's `audioFormat` | RP2 |
| RQ4 | Does the sender's `networkTimeTimelineID` always equal the grandmaster it announces to us? | apply the anchor regardless, log a mismatch | RP5 |
| RQ5 | Does iOS need Delay_Req from the slave, or an Announce at all? | passive slave with two Announces | RP5 |
| RQ6 | How large is the audible delay behind the phone for buffered and realtime streams, and does iOS read `audioLatencies` from `/info`? | no `audioLatencies` | RP3 |
| RQ7 | Does a smaller `LatencyFrames` (0.5 s) give realtime streams a usable margin without hurting the group? | keep the default | RP3 with the latency setting changed |
| RQ8 | Does iOS re-try `/pair-setup` after a 470 on transient M1 in password mode, or does it need the `pw` TXT key? | `pw=true` set, 470 on transient | RP9 |
| RQ9 | Does any sender open a `timingProtocol: None` remote-control session toward a speaker? | accept and ignore | RP1, RP10 |
| RQ10 | Does macOS system output use the realtime stream, and does it tolerate the extra realtime delay `D` (about 0.45 s at the default latency, 5.4)? | as designed; RQ7 if not | RP10 |
| RQ11 | Does iOS accept the ALAC-only variant (feature bit 20 cleared, `cn=0,1`, `bufferStream 0x40000`) when libavcodec is missing, and what does it stream then? | `ReceiverFeatures.AlacOnly`, consistent in word, TXT and `supportedFormats` (R3) | RP11 |

## 13. Implementation waves

Each wave ends with the whole solution green (`dotnet build TuneShuttle.sln`, `dotnet test` with the offline filter,
`tools/privacy-scan.ps1`), a review of each package, the fixes, and one commit. Dependencies: 1 needs 0; 2 needs 1; 3 needs
2; 4 needs 3 (the catalog keys of `i18n`); 5 needs 4.

| Wave | Packages | Acceptance |
|---|---|---|
| 0 Extraction and scaffold | `receiver-extract`: create `src/Airplay2.Receiver` and move `RtspServerConnection`, the pairing handlers, `FakeEventChannel` → `EventChannelServer`, `FakeParsing` → `RequestParsing`, the realtime UDP receiver (`FakeReceiver.Audio` core) and `FakePtpSlave` → `PtpSlave` into it with product names and XML docs; the fake receiver keeps its options and events and delegates. Visibility: `InternalsVisibleTo` for `Airplay2.Receiver` and `Airplay2.Receiver.Tests` added to `src/Airplay2.Sender/Airplay2.Sender.csproj` (for `HapAccessoryPairing`, `HapControllerRegistry`, `UdpReceiveLoop`, `PrecisionSleeper`) and to `librespot-dotnet/src/Librespot.Discovery/Librespot.Discovery.csproj` (in-tree, not a submodule; for `MdnsResponder` and its record types); `tests/Airplay2.Sender.Tests` gets a project reference to `Airplay2.Receiver` for the moved types its support files use (`FakeReceiverHarness`, `RtspTestSupport`, `SessionTestSupport`, `PlaybackTestSupport` and the tests that name them). The 4.2 hook (`HapControllerRegistry` change callback, `HapAccessoryPairing` registry constructor) is made here. `scaffold`: the public surface of Sections 4-8 as compiling types (`AirplayReceiver`, `AirplayReceiverOptions`, `ReceiverEvent` records, `IReceiverAudioOutput`, `PlayoutBuffer`, `IAacDecoder(+Factory)`, `PtpPort`, `DmapDecoder`, `SourceSwitch`, `AirplaySource`, DTO additions, settings keys, migration 2 registered) with `NotImplementedException` bodies where behaviour is pending; the 8.6 signature changes (`SetSourceLevelAsync`, `OutputManager.SourceAirplay`, `IGroupPlayer.QueuedFrames`, `AirplaySenderOptions.PtpPort`); the `Program.cs` registrations of `SourceSwitch`, `AirplaySource` and the shared `PtpPort`; plus the new test projects. | all existing tests green; solution warning-free |
| 1 Protocol foundations | `advertise` (3.1-3.3: TXT, `/info`, responder registration, rename); `control-plane` (4.1-4.9 state machine including the SET_PARAMETER/GET_PARAMETER handlers in `RtspRequestHandler`, liveness, `/fp-setup`; calls the helpers of `dmap-and-volume` and the existing `VolumeMapping.DbToLevel`/`FormatParameter` for R9); `ptp-port` (6.1 `PtpPort`, the `PtpMaster` refactor, `AirplaySenderOptions.PtpPort`, the `PlaybackProbes`/CLI option moves; 6.2 `PtpSlave` estimation and peer ports); `dmap-and-volume` (`DmapDecoder`/`DmapItem` and pure helpers only: no handler code and no new volume mapping); `aac-decoder` (7.2 binding, vectors, `tools/get-ffmpeg.ps1`) | unit tests of 11.1 for these; loopback pairing + session SETUP tests |
| 2 Audio plane | `realtime-input` (5.1 incl. 0xD7 anchors, retransmits); `buffered-input` (5.2 framing, SSRC, decrypt, ADTS, decoder select); `playout` (5.3-5.5 buffer, release thread, drift, pause/flush/splice); `receiver-facade` (`AirplayReceiver` wiring, events, `EndSessionAsync`, statistics) | loopback audio tests of 11.1 (realtime, buffered ALAC, buffered AAC) |
| 3 Bridge | `source-switch` (8.2 `SourceSwitch`, the `OutputManager` ownership gate, the `AirplaySink` and `PlayerEventMapper` hooks; may edit `Output/SourceSwitch.cs`, `Output/OutputManager.cs`, `Output/AirplaySink.cs`, `Output/RuntimeState.cs`, `Spotify/PlayerEventMapper.cs`); `airplay-source` (8.3, settings, identity, store 8.4, options 8.5, self-exclusion R12; may edit `Bridge.Service/Airplay/*`, the store and migration, `BridgeOptions.cs`, `ReceiverInventory.cs`; compiles against the scaffolded `SourceSwitch`/`OutputManager` surface and edits neither); `api` (9.1 DTOs, `DtoFactory`, `EventHub` payloads, routes, artwork; may edit `Bridge.Contracts` and `Bridge.Service/Api/*`); `i18n` (the 9.2 catalog keys in all seven `Resources/i18n/*.json`, UI-SPEC.md section 10); `deployment` (Section 10, docs for README/ARCHITECTURE/DEVELOPMENT) | Bridge tests of 11.1; catalog completeness tests green |
| 4 Web | `web-player` (hero chip, disabled transport); `web-settings` (card); `web-diagnostics` (card); none of them edits `Resources/i18n` (the keys exist since wave 3) | bUnit tests of 11.1 |
| 5 Integration | `integration`: end-to-end bridge test (`WebApplicationFactory` with the receiver on loopback, our sender streaming, status/playback events observed), SPEC.md index + docs/README + the new MANUAL-TESTS.md section 9 "AirPlay receiver probes (RP1-RP12)", `build.ps1` end to end, privacy scan, final review | `./build.ps1 -SkipContainer` green |

## 14. Names used in code

| Name | Where | Meaning |
|---|---|---|
| `AirplayReceiver`, `AirplayReceiverOptions`, `ReceiverEvent.*`, `ReceiverStatus` | `Airplay2.Receiver` | the facade, its options (name, identity, password, ports, formats, timing), its event stream and state |
| `ReceiverIdentity` | `Airplay2.Receiver` | device id, psi, Ed25519 seed/public key (R11) |
| `ReceiverFeatures` (`Default`, `AlacOnly`), `ReceiverTxtRecords`, `InfoResponseBuilder`, `ReceiverAdvertiser` | `Airplay2.Receiver/Discovery` | R3, R15 and Section 3 |
| `RtspServerConnection`, `RtspRequestHandler`, `RequestParsing`, `ControlSession`, `EventChannelServer` | `Airplay2.Receiver/Rtsp`, `/Session` | Section 4 |
| `IControllerStore`, `InMemoryControllerStore` | `Airplay2.Receiver/Pairing` | registered controllers (R7, 4.2), fed by the `HapControllerRegistry` change callback |
| `RealtimeStreamReceiver`, `BufferedStreamReceiver`, `BufferedBlock` | `Airplay2.Receiver/Streams` | 5.1, 5.2 |
| `PlayoutBuffer`, `PlayoutStatistics`, `IReceiverAudioOutput` | `Airplay2.Receiver/Playout` | 5.3-5.5 |
| `IAacDecoder`, `IAacDecoderFactory`, `FfmpegAacDecoder`, `AdtsHeader` | `Airplay2.Receiver/Codecs` | 7.2 |
| `PtpSlave`, `IPtpSlave`, `PtpSlaveOptions` (`PeerEventPort`, `PeerGeneralPort`), `IMonotonicClock` | `Airplay2.Receiver/Timing` | 6.2 |
| `PtpPort`, `IPtpPort`, `PtpPortOptions`, `IPtpEndpoint`, `PtpPortException`, `AirplaySenderOptions.PtpPort` | `Airplay2.Sender/Streams`, `Airplay2.Sender` | 6.1 |
| `DmapDecoder`, `DmapItem` | `Airplay2.Sender/Session` | 4.8 |
| `AlacMagicCookie.Default44100Stereo16` | `Airplay2.Sender/Alac` | 7.1 |
| `SourceSwitch`, `AudioSource` (enum), `RuntimeState.SpotifyPlayback`/`AirplayPlayback`, `AirplaySink.StartIfRunning`/`LastPauseCompleted` | `Bridge.Service/Output` | 8.2 |
| `AirplaySource`, `AirplayOptions`, `AirplaySettings` | `Bridge.Service/Airplay` | 8.3, 8.5 |
| `SetSourceLevelAsync`, `OutputManager.SourceAirplay`, `IGroupPlayer.QueuedFrames` | `Bridge.Service/Output`, `Airplay2.Sender/Playback` | 8.6 |
| `AirplayReceiverDto`, `AirplayDiagnosticsDto` | `Bridge.Contracts` | 9.1 |
| `Lpipe`, `AnchorLead`, `LateThreshold`, `DriftThreshold` | `PlayoutBuffer` options | R6, 5.3-5.4 |
| `D` (`PlayoutStatistics.StreamOffset`) | `PlayoutBuffer` | the learnt per-stream offset of R6 |
