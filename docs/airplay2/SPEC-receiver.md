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
  selection; we play, but our audible delay (sender latency plus our own, Section 5.4) is not compensated. Hook: the
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
| R3 | **Formats**: realtime ALAC/44100/16/2 (352 frames per packet); buffered ALAC/44100/16/2 and AAC-LC/44100/2 (1024 frames per packet). `/info` `supportedFormats` advertises exactly these (`audioStream = 0x40000`, `bufferStream = 0x440000`; `0x40000` alone when no AAC decoder is loaded). Output is always S16 interleaved 44.1 kHz stereo. | The output pipeline is 44.1/16 (SPEC.md D1); `[sps] AIRPLAY2.md` lists AAC/44.1 as the basic buffered format every sender supports. |
| R4 | **AAC-LC through ffmpeg's libavcodec** via P/Invoke (FFmpeg.AutoGen, MIT, pinned to one FFmpeg major), behind `IAacDecoder`. The library path is configurable; when the native libraries are missing the receiver starts, advertises ALAC-only formats and logs the reason once. The decoder is fed ADTS-framed blocks (`[sps] ap2_buffered_audio_processor.c:41-90`). | .NET has no AAC decoder; shairport-sync needs libavcodec for the same reason; a managed port is a later option behind the same interface. Decided with the owner on 2026-10-26. |
| R5 | **One `PtpPort` per process** owns UDP 319/320 and routes messages by source address to the grandmaster (`PtpMaster`, our receivers) or to the slave of the current sender session (`PtpSlave`). The slave is passive by default: it computes the offset from Sync/Follow_Up and sends nothing except two Announces of its own clock at session start (what nqptp does, SPEC-audio.md 4.4 note); Delay_Req is an option (RQ5). When 319/320 cannot be bound the receiver runs in **anchor-only timing** (R6). | nqptp proves a silent slave is accepted by Apple senders; our grandmaster already binds the ports when sending to PTP-only receivers, and two sockets cannot share them. |
| R6 | **Playout rule**: a frame with intended play time `T` (sender timeline, from SETRATEANCHORTIME or 0xD7 syncs, mapped to local time through the PTP offset) is written to the output when `now >= T - Lpipe`, where `Lpipe` is the output's write-to-audible delay (Section 5.4). Without a PTP offset (anchor-only) the first anchor is mapped to `now + AnchorLead` and later anchors keep the same offset. Frames whose `T` is already past are written at once (realtime streams arrive with little lead, Section 5.4). Drift is corrected by dropping or repeating single frames when the buffer runs more than `DriftThreshold` early or late. | Buffered senders push audio seconds ahead; playing it as it arrives would run ahead of the phone's progress and break pause. Absolute alignment with the sender's clock is only needed for N1. |
| R7 | **Pairing**: transient pair-setup by default (status flags `0x4`). With a configured password: regular pair-setup with the password as the SRP PIN, status flags `0x84` (`PasswordNeeded`), transient M1 refused with 470, registered controllers persisted and pair-verify accepted. | shairport-sync's password mode is proven with iOS (`[sps] shairport.c:1687-1689`, SPEC-control.md 1.4 bit 7); `HapAccessoryPairing` already implements both procedures. |
| R8 | **Newest source wins.** The AirPlay source claims the output on the sender's **stream SETUP** (not on connect, not on pairing); the claim pauses Spotify through Spirc. Spotify claims the output on its `Playing` event; the claim ends the AirPlay session (connection closed, iOS shows the speaker as disconnected). A second sender's stream SETUP ends the first sender's session the same way. When a sender leaves, the output is unowned; Spotify stays paused until the user plays. | HomePod behaviour; the person who just pressed play gets what they expect. Decided with the owner on 2026-10-26. |
| R9 | **Volume**: a sender `SET_PARAMETER volume` in dB maps through the inverse of SPEC.md D10 (`level = round((dB + 30) / 0.3)`, `-144` or below `-30` → 0) to the master level G of the output with source `airplay`; `GET_PARAMETER volume` answers the current G in the airplay2-receiver shape (`volume: -24.000000\r\n`, no leading blank line, SPEC-control.md 4.11 note). Level changes made in the web app while AirPlay owns the output are applied to the receivers but are not pushed to the sender (N2). | The group volume model stays the single place where levels live. |
| R10 | **Metadata**: DMAP text, artwork and progress from the sender (`SET_PARAMETER`, SPEC-control.md 5.2-5.4) are decoded, shown in the web app and forwarded to the receivers through the existing now-playing path. The receiver advertises feature bits 15, 16, 17 and `md=0,1,2`, not bit 50. | The group player already sends now-playing to receivers; bit 50 would require the `POST /command` protocol (N5). |
| R11 | **Identity**: one receiver identity per installation, created on first start and stored in `settings`: `deviceid` (random locally-administered MAC, also the HAP accessory pairing identifier and `pi`), `psi` (UUID), the Ed25519 long-term key (`pk`). The name defaults to the Spotify device name and is settable separately. | Receiver A uses its MAC as `pi` and pairing id (SPEC-control.md 1.2); a stable identity keeps pair-verify working for password mode. |
| R12 | **Self-exclusion**: `ReceiverInventory` ignores discovered receivers whose `deviceid` or `pk` equals our own; they never reach the store or the web app. | Our browser sees our own advertisement. |
| R13 | **One receiver per host.** The product receiver binds TCP 7000 (`Bridge:Airplay:RtspPort`); tests bind port 0 and run the fake receiver and the product receiver only on loopback. | `[sps] AIRPLAY2.md`: senders are confused by two AirPlay 2 receivers at one address. |
| R14 | **`POST /fp-setup`** is answered with the canned FairPlay replies shairport-sync uses (`[sps] rtsp.c:1843-1930`, MIT), selected by the request's mode byte; nothing is derived from them. | Our feature word carries bit 14 (shairport-sync's does), and iOS then sends `/fp-setup` before pairing. |
| R15 | **Feature word** `0x00018340405FCA00` (TXT `features=0x405FCA00,0x18340`): shairport-sync's default plus the metadata bits 15-17; bits 9, 11, 14, 15, 16, 17, 18, 19, 20, 22, 30, 38, 40, 41, 47, 48. `srcvers 366.0`, `protovers 1.1`, `model TuneShuttle1,1`, `manufacturer TuneShuttle`. The word is one constant (`ReceiverFeatures.Default`) so the probes can change it in one place. | A word proven with iOS and macOS senders (`[sps] shairport.c:1628`); bit table in SPEC-control.md 1.3. |
| R16 | **Timeouts**: a sender that sends nothing on the control connection for 30 s after a stream SETUP is considered gone (iOS sends `/feedback` every 2 s, SPEC-control.md 4.10); a closed control connection ends the session at once (event channel closed, ports released, output released). | `[sps]` uses a 60 s TCP keepalive; 30 s keeps the output from staying blocked after a phone leaves the Wi-Fi. |

## 3. Discovery and `/info`

### 3.1 Services

Two DNS-SD services through `Librespot.Discovery.Mdns.MdnsResponder` (one responder instance owned by the receiver's
advertiser; the Spotify Zeroconf responder stays separate), re-registered on every name or password change:

| Service | Instance name | Port |
|---|---|---|
| `_airplay._tcp` | `<name>` | the RTSP port |
| `_raop._tcp` | `<deviceid without colons>@<name>` | the RTSP port |

`_airplay._tcp` TXT (SPEC-control.md 1.2; shairport-sync's set, `[sps] bonjour_strings.c:60-85`, `rtsp.c:999-1104`):
`acl=0`, `deviceid=<MAC>`, `features=0x405FCA00,0x18340`, `flags=0x4` (or `0x84` with a password), `gid=<deviceid>`,
`gcgl=0`, `model=TuneShuttle1,1`, `manufacturer=TuneShuttle`, `protovers=1.1`, `pi=<deviceid>`, `psi=<uuid>`,
`pk=<64 hex>`, `srcvers=366.0`, `fex=<base64 of the little-endian features, no padding>`.

`_raop._tcp` TXT (SPEC-control.md 1.5; `[sps] bonjour_strings.c:1-57`): `cn=0,1,2`, `da=true`, `et=0`, `ft=<same as
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

### 3.3 Name changes and shutdown

Renaming re-registers both services (goodbye, then announce). Disabling the receiver or stopping the service sends the
goodbye records, closes listening sockets and ends a running session (Section 4.9).

## 4. Control plane as a receiver

### 4.1 Connections

The RTSP listener (TCP, `Bridge:Airplay:RtspPort`, default 7000, 0 in tests) accepts any number of connections; each
is an `RtspServerConnection` (moved from the fake receiver) with the HAP channel cipher switched on after pairing. A
connection is **the session connection** once it has completed a session SETUP. Requests other than `GET /info`,
`POST /info`, `OPTIONS`, `POST /fp-setup`, `POST /auth-setup` and the pairing POSTs on an unpaired connection are
answered 470 (the fake receiver's `RequirePairing` rule stays the product behaviour).

### 4.2 Pairing

`HapAccessoryPairing` (already in `Airplay2.Sender/Pairing`, internal, visible to the receiver project) handles
`/pair-setup`, `/pair-verify`, `/pair-add`, `/pair-remove`, `/pair-list`. Options per R7: `AllowTransient =
password is null`, `Pin = password`, `MaxControllers 16`. Registered controllers (identifier + Ed25519 LTPK) are
persisted through `IControllerStore` (the bridge backs it with the `airplay_controllers` table, Section 8.4; tests use
an in-memory store). `POST /auth-setup` is answered as the fake does (200 with 1076 zero bytes, SPEC-control.md 3.10).

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
old session is ended at that connection's **stream SETUP** (R8).

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
| 96 | bind UDP data and control sockets (port 0); note `controlPort` of the sender (for 0xD5 requests); `spf` must be 352, `sr` 44100 | 200 `{streams: [{type: 96, dataPort, controlPort, streamID}]}` |
| 103 | bind a TCP data listener (port 0); accept one connection; `audioFormat` must be one of R3's (else 400 and the formats logged) | 200 `{streams: [{type: 103, dataPort, controlPort, streamID, audioBufferSize: 8388608}]}` |
| other | | 400 |

`streamID` is a per-process counter starting at 1. The stream SETUP is the **claim** (R8): the receiver raises
`SenderStreamStarted` and the bridge pauses Spotify and routes the output; a second sender's stream SETUP ends the first
session. `/info` `statusFlags` and the TXT `flags` gain bit 17 (`ReceiverSessionIsActive`) while a stream is set up
(`[ap2r] ap2-receiver.py:564, 631, 794`).

### 4.7 SETRATEANCHORTIME and FLUSHBUFFERED

Bodies per SPEC-audio.md 4.3. `rate 1` sets the anchor `(networkTime, rtpTime)` on the playout buffer and resumes the
output; `rate 0` pauses the output and keeps the buffered frames; `networkTimeTimelineID` is recorded and compared with
the slave's grandmaster (a mismatch is logged, the anchor is applied anyway, RQ4). `FLUSHBUFFERED`: immediate form
drops frames with sequence below `flushUntilSeq` (23-bit modular compare) and flushes the output; deferred form
(`flushFromSeq` present) registers a splice: when the block `flushFromSeq` arrives, blocks up to but excluding
`flushUntilSeq` are dropped without flushing the output. Both answer 200.

### 4.8 SET_PARAMETER, GET_PARAMETER, feedback, other

| Request | Handling |
|---|---|
| `SET_PARAMETER` `text/parameters` `volume: <dB>` | R9; raises `SenderVolume(level)`; 200 |
| `SET_PARAMETER` `application/x-dmap-tagged` | `DmapDecoder` (new, next to `DmapEncoder`): `mlit {minm, asar, asal, astm, …}`; unknown tags skipped; `RTP-Info rtptime` kept as the item's key; raises `SenderMetadata`; 200 |
| `SET_PARAMETER` `image/jpeg` / `image/png` | artwork bytes (≤ 1 MiB, larger answered 200 and dropped) with the `rtptime` key; raises `SenderArtwork`; 200 |
| `SET_PARAMETER` `text/parameters` `progress: a/b/c` | start/current/end RTP times; raises `SenderProgress`; 200 |
| `GET_PARAMETER` `volume\r\n` | 200 `text/parameters` `volume: <current G in dB with six decimals>\r\n` (R9) |
| `POST /feedback` | 200 with `{streams: [{type, sr: 44100}]}` while a stream is set up, else an empty body; refreshes the R16 liveness timer (every request does) |
| `POST /command` | 200, empty (bit 50 is not advertised; bodies are logged at debug level) |
| `POST /audioMode`, `SETMAGICCOOKIE`, `PUT /setProperty` | 200, empty |
| `POST /fp-setup` | R14 |
| `OPTIONS` | 200 with `Public: ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, POST, GET, PUT` |
| unknown method or path | 501 / 404 |

### 4.9 TEARDOWN and session end

`TEARDOWN` with `{streams: [{streamID}]}` ends that stream (sockets closed, buffer dropped, output released, status
flag 17 cleared) and answers 200; `TEARDOWN` with `{}` or no body ends the session: stream, event channel, PTP slave,
`SenderGone(reason)`. The same end-of-session runs when the connection closes, the liveness timer (R16) fires, the
bridge asks for it (R8: `EndSessionAsync(reason)`), or the receiver stops.

## 5. Audio plane as a receiver

### 5.1 Realtime stream (type 96)

UDP data packets: RTP header, ChaCha20-Poly1305 payload with `shk` (SPEC-audio.md 1.2-1.3), 352 ALAC frames each.
The receiver (promoted from `FakeReceiver.Audio`) tracks the 16-bit sequence, asks for gaps with 0xD5 to the sender's
control port (8-byte form) within 30 ms of detecting them and at most three times per gap, accepts 0xD6 wrapped
replies on the control socket and raw retransmissions on the data socket, and decodes with `AlacDecoder` (magic cookie
for 352/16/2 from `AlacMagicCookie.Default44100Stereo16`). Packets arriving more than 2 s before their play time or
after it are counted and dropped.

Timing in this mode comes from 0xD7 sync packets on our control port (SPEC-audio.md 4.4 table): `now_frame` at PTP
time `T` with the grandmaster id; the pair `(T, now_frame)` is the anchor for RTP timestamp → play time. Until the
first sync, frames are released anchor-only (R6). The marker bit on the first packet after FLUSH re-anchors on the
next sync.

### 5.2 Buffered stream (type 103)

TCP data connection, 2-byte length-prefixed blocks laid out as SPEC-audio.md 4.2: 23-bit sequence, RTP timestamp,
SSRC = format code, encrypted payload. Decoder selection per block: SSRC `0x16000000` → AAC-LC/44.1 (1024 frames
per block); `0x0000FACE` → ALAC/44.1/16; any other SSRC → the decoder of the stream SETUP's `audioFormat`, and the
SSRC is logged once (RQ3). The reader keeps at most `AudioBufferSize` (8 MiB) of undecoded blocks and stops reading the
socket when full (TCP window is the flow control, as `[sps]` does); blocks whose play time has passed are dropped
without decrypting. AAC blocks get a 7-byte ADTS header prepended before decoding; the first block after a start,
flush or discontinuity is decoded and muted (`[sps] ap2_buffered_audio_processor.c:578-583`).

### 5.3 Playout buffer

`PlayoutBuffer` (library) is the one place where decoded frames wait:

- Input: `Enqueue(rtpTimestamp, sequence, ReadOnlyMemory<short>)` from either stream reader (decoded, S16 stereo).
- Anchor: `SetAnchor(rtpTime, senderTimeNs, timelineId)` from SETRATEANCHORTIME or 0xD7; `Pause()`, `Resume()`,
  `Flush(untilSeq?)`, `Splice(fromSeq, untilSeq)`.
- Clock: `IPtpSlave.TryMapToLocal(senderTimeNs, out localTicks)`; when it has no estimate the anchor-only mapping
  of R6 applies (`AnchorLead` default 300 ms).
- Release thread (one per buffer, `PrecisionSleeper`): writes the next frame to `IReceiverAudioOutput.WriteAsync` when
  `now >= T(frame) - Lpipe`; frames whose `T - Lpipe` is more than `LateThreshold` (200 ms) in the past are dropped
  and counted (`LateFrames`); gaps in the sequence that are not filled by the time the next frame is due are filled with
  silence of the missing length (`MissingFrames`).
- Drift: every 10 s the buffer compares the cumulative written frames with the elapsed local time at the anchor's rate;
  more than `DriftThreshold` (20 ms) ahead → one frame is dropped per block until within threshold; behind → one frame
  repeated per block. Counters `DroppedForDrift`, `RepeatedForDrift`.
- Statistics snapshot for Diagnostics: buffered milliseconds, late, missing, drift corrections, decoder, stream type.

### 5.4 `Lpipe` and the output

The output is the existing `IGroupPlayer`: a PCM ring (`RingHighWaterFrames` 44 100, prefill 8 820), a pacer that
takes 352 frames per packet and sends them `L` (`LatencyFrames`, default 77 175) ahead of their audible time, plus the
receiver-side lead of 11 025 frames (SPEC-audio.md 1.5). For the AirPlay source the ring must stay shallow, otherwise
its fill adds a variable delay: `Lpipe = LatencyFrames + 11025 + PrefillFrames` frames (≈ 2.2 s at the defaults), and
the release thread never writes more than `PrefillFrames + 352` frames ahead of the pacer's read position
(`IGroupPlayer` exposes `FramesQueued`). Realtime senders deliver about 2.0 s ahead of the intended play time, so with
the default `L` their frames are released immediately (the R6 clamp); buffered senders deliver far ahead and are
released on time. The audible delay behind the sender's own display is therefore `Lpipe` minus the sender's lead,
about 0.25 s for buffered and about 2.2 s for realtime streams at the default latency (RQ7 measures both).

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
    /// <summary>Frames queued in the output ahead of its pacer (5.4); 0 when no output is connected.</summary>
    int FramesQueued { get; }
}
```

| Sender action | Output action (through `IReceiverAudioOutput`) |
|---|---|
| `SETRATEANCHORTIME rate 0`, or realtime stream silent for 2 s | `PauseAsync` (the group player's pause: receivers flushed, as the Spotify sink does) |
| `SETRATEANCHORTIME rate 1` | `ResumeAsync`, then frames per R6 |
| `FLUSH`, immediate `FLUSHBUFFERED` | buffer dropped, `FlushAsync` (receivers re-anchor on the next packet) |
| deferred `FLUSHBUFFERED` | buffer splice only; the output never notices (gapless track change) |
| `SET_PARAMETER volume` | `SetMasterLevelAsync(level, source: airplay)` |
| DMAP + artwork + progress with one `rtptime` | `SetNowPlayingAsync(metadata, artwork, elapsed = (current − start) / 44100)` once all three with the same key arrived or 500 ms after the first |
| `TEARDOWN`, connection lost, evicted | `ReleaseAsync` (the output is unowned; receivers stay connected, as after a Spotify stop) |

## 6. Timing: `PtpPort`, `PtpSlave`, `PtpMaster`

### 6.1 `PtpPort` (in `Airplay2.Sender/Streams`)

Owns the two `UdpReceiveLoop`s on 319/320 (bind address and ports from options; port 0 in tests) and a routing table:
`Register(IPAddress source, IPtpEndpoint handler)` / `Unregister`. Every datagram goes to the handler registered for
its source address; unregistered sources go to the default handler (the master, which keeps today's
`ForeignAnnounce`/`FirstMessageFromPeer` behaviour) when one is registered, else they are counted and dropped.
Sending: `SendAsync(IPEndPoint, ReadOnlyMemory<byte>, eventPort: bool)`. `PtpMaster` is refactored to take an
`IPtpPort` instead of binding its own sockets; `PtpMasterOptions.EventPort/GeneralPort` move to `PtpPortOptions`.
Existing PTP tests keep passing with a port per test (port 0). Binding failure raises `PtpPortException` once; the
bridge logs it as a warning and the receiver runs anchor-only (R6).

### 6.2 `PtpSlave` (in `Airplay2.Receiver/Timing`, promoted from `FakePtpSlave`)

Per sender session: accepts the sender's addresses (session SETUP `timingPeerInfo.Addresses`, the connection's remote
address, SETPEERS additions). On each two-step Sync + Follow_Up pair it computes `offset = t2(local receive of Sync) −
t1(preciseOriginTimestamp)`; the estimate is the minimum over a sliding window of 8 pairs (network delay only adds,
as nqptp assumes, SPEC-audio.md 4.4), re-based when the grandmaster identity changes. `TryMapToLocal(senderNs)` =
`senderNs + offset` in local monotonic nanoseconds (`Stopwatch`-based `IMonotonicClock` shared with `PtpMaster`'s
`IMasterClock`). Announces from the sender are recorded (grandmaster id, priority); at session start the slave sends two
Announces of its own clock id as nqptp does. `SendDelayRequests` is an option, off by default (RQ5). Statistics:
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
version is fixed in `Directory.Packages.props` and recorded in `REFERENCE-SOURCES.md` with the licence note: LGPL 2.1+,
dynamically linked, no GPL components). Linux/container: the distribution's `libavcodec`/`libavutil`/`libswresample`
packages of that major in the runtime image (Section 10). Windows: an LGPL **shared** build of that major copied next to
`bridge.exe` by `tools/get-ffmpeg.ps1` (download, verify the published checksum, extract the `avcodec-*.dll`,
`avutil-*.dll`, `swresample-*.dll` only); `build.ps1` calls it unless `-SkipFfmpeg`; the Windows installer copies what
is in `artifacts/bridge`.

Tests: `tests/Airplay2.Receiver.Tests` has `aac-44100-stereo-1024.json`-style vectors under
`docs/airplay2/test-vectors/`: a 2 s 440 Hz tone encoded once with the ffmpeg CLI (`-c:a aac -b:a 128k`) into raw AAC
frames (the generator command is recorded next to the vector), plus the expected RMS and frequency of the decoded
output (bit-exactness is not expected from AAC). The decode tests run when the native library loads and otherwise pass
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

Singleton. State `Owner ∈ {None, Spotify, Airplay}`, `SenderInfo?`. `Claim(source)`: if another source owns the output,
run its eviction (Spotify: `SpotifyHost.TryCommand("pause")`; Airplay: `AirplaySource.EndSessionAsync("replaced by
Spotify")`), then set the owner and publish `statusChanged`. `Release(source)` sets `None` when that source owns it.
`MayWrite(source)` is the gate both sinks check per write (a volatile read; frames of a non-owner are counted and
dropped). Triggers: `PlayerEventMapper`'s `Playing` event → `Claim(Spotify)`; `AirplaySource` on
`SenderStreamStarted` → `Claim(Airplay)`; `SenderGone`/stream end → `Release(Airplay)`; Spotify's `Stopped` →
`Release(Spotify)` (Spotify's pause keeps the ownership, as today the receivers stay connected). Default owner at
start: `None`; Spotify's first write while `None` claims (keeps today's behaviour when nothing else happens).

### 8.3 `AirplaySource`

Hosted service; builds `AirplayReceiver` from settings (enabled, name, password, identity) and `BridgeOptions.Airplay`
(RTSP port, bind address, ffmpeg path, PTP options); implements `IReceiverAudioOutput` against `OutputManager`:
`WriteAsync` → the active player's `Write` when `MayWrite(Airplay)`; `PauseAsync`/`ResumeAsync`/`FlushAsync` → the
player; `SetMasterLevelAsync(level, "airplay")` → `OutputManager.SetSourceLevelAsync` (the generalisation of today's
`SetSpotifyVolumeAsync`, see 8.6); `SetNowPlayingAsync` → `OutputManager.SetNowPlayingAsync`. Restarts the receiver on
settings changes (name, password, enabled) and keeps running when the output changes (the player reference is read per
write). Publishes `statusChanged` (receiver state, sender), `playbackChanged` (source `airplay`, title, artist, album,
duration, position, artwork url) and the Diagnostics snapshot.

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

### 8.6 `IPlaybackOutput` / `OutputManager` changes

`SetSpotifyVolumeAsync(level)` becomes `SetSourceLevelAsync(level, source)` (sources `spotify`, `airplay`;
`OutputManager.SourceSpotify` exists, `SourceAirplay` is added); `SpotifyVolumeUpdates` are published only while
Spotify owns the output. `IGroupPlayer` gains `FramesQueued` (ring count) for 5.4. Everything else in the output path
is unchanged.

## 9. API, events and web app

### 9.1 DTOs (`Bridge.Contracts`)

| DTO | Change |
|---|---|
| `SettingsDto` | `+ bool? AirplayEnabled, string? AirplayName, string? AirplayPassword, bool? AirplayPasswordSet`. In a `PUT`, null keeps the value (as `ForgetOfflineAfterDays`), `AirplayPassword = ""` clears the password, any other string sets it; `AirplayPasswordSet` is ignored. Answers always carry `AirplayEnabled`, `AirplayName` and `AirplayPasswordSet`, and never the password (`AirplayPassword` is null) |
| `StatusDto` | `+ string? ActiveSource` (`"spotify"`, `"airplay"`, null), `+ AirplayReceiverDto? Airplay` = `(string State, string Name, string? SenderName, string? SenderModel, bool AacAvailable)` with `State ∈ off, starting, advertising, connected, streaming, error` |
| `PlaybackDto` | `+ string? Source` (`"spotify"`, `"airplay"`, null); for AirPlay `ArtworkUrl` = `/api/playback/artwork?v=<rtptime>` |
| `DiagnosticsDto` | `+ AirplayDiagnosticsDto? Airplay` = `(int RtspPort, bool PtpBound, long? PtpOffsetNs, string? StreamType, string? Format, int BufferedMs, long LateFrames, long MissingFrames, long DriftCorrections, string? DecoderStatus)` |

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
  ("{{type}} · {{format}}"), diagnostics.airplayBuffer ("{{ms}} ms"), diagnostics.airplayLate ("{{late}} late ·
  {{missing}} missing · {{drift}} drift"), diagnostics.airplayPtp (offset in ms or diagnostics.airplayPtpNotBound),
  diagnostics.airplayDecoder.
- **Receivers page**: unchanged (R12 keeps us out of the list).

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

- Unit: `DmapDecoder`, volume dB ↔ level (R9), TXT/`/info` builders (R15, 3.1-3.2), `PlayoutBuffer` (anchor mapping,
  R6 release rule with a fake clock, late/missing/drift counters, flush and splice semantics), `PtpSlave` offset
  estimation (fed synthetic Sync/Follow_Up pairs with known offsets and delays), `PtpPort` routing, buffered block
  framing and SSRC selection, ADTS header construction, `FfmpegAacDecoder` (when the library loads, 7.2).
- Loopback (`Airplay2.Receiver.Tests`): our own `Airplay2.Sender` drives the product receiver on loopback with port 0:
  discovery through a loopback `MdnsResponder`/browser pair; transient pairing and pair-verify; password pairing
  (regular pair-setup, controller persisted, pair-verify on a second connection, wrong password → `Error 2`); session
  SETUP under PTP with the sender's `PtpMaster` on test ports and the receiver's `PtpSlave` following it (offset
  within 5 ms of the injected truth); realtime stream of a 3 s tone (via `RealtimeStreamTransport` under PTP: 0xD7
  syncs, dropped packets → 0xD5 → recovered); buffered ALAC stream via `BufferedStreamTransport` with pause, seek
  (immediate flush), splice (deferred flush); buffered AAC via a test-only raw block writer replaying the AAC vector
  (the sender has no AAC encoder); output captured by a recording `IReceiverAudioOutput` and compared against the
  tone (RMS, continuity, sample count within one packet); SET_PARAMETER volume/DMAP/artwork/progress → events;
  `/feedback` liveness; TEARDOWN variants; eviction by `EndSessionAsync`.
- Bridge (`Bridge.Tests`): `SourceSwitch` claims and evictions with stubbed sources; `AirplaySource` against a stub
  player (writes gated, pause/flush/now-playing forwarded, level set with source `airplay`); migration to version 2;
  settings round trip with masking; DTO/JSON shape; `/api/playback/artwork`; self-exclusion in `ReceiverInventory`;
  `PtpPort` shared between an `OutputManager` PTP transport and the receiver in one process (ports 0).
- Web (bUnit): hero chip and disabled transport for source `airplay`, the Settings card (load, validation, save
  payload with null-keeps), the Diagnostics card, every new catalog key present in every language (the existing
  catalog completeness test of `Bridge.Tests/Web` covers new keys automatically).
- `RepositoryHygieneTests`: every live test gated; `tools/privacy-scan.ps1` passes (no sender names, addresses or
  identities in tracked files; test fixtures use synthetic identities).

### 11.2 Secondary

shairport-sync in Docker cannot act as a sender. pyatv streams only the RAOP/AirPlay 1 path with legacy pairing
(`[pyatv] docs/documentation/supported_features.md:30`), which we do not accept (R1), so it is not a test sender
either. The loopback tests with our own sender are the automated coverage; Apple senders are covered by 11.3.

### 11.3 Live probes (the owner's devices; results recorded in SPEC.md section 4 style and MANUAL-TESTS.md section 6)

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
| RQ10 | Does macOS system output use the realtime stream, and does it tolerate the 2 s extra delay? | as designed | RP10 |

## 13. Implementation waves

Each wave ends with the whole solution green (`dotnet build TuneShuttle.sln`, `dotnet test` with the offline filter,
`tools/privacy-scan.ps1`), a review of each package, the fixes, and one commit. Dependencies: 1 needs 0; 2 needs 1; 3 and
4 need 2; 5 needs 3 and 4.

| Wave | Packages | Acceptance |
|---|---|---|
| 0 Extraction and scaffold | `receiver-extract`: create `src/Airplay2.Receiver` and move `RtspServerConnection`, the pairing handlers, `FakeEventChannel` → `EventChannelServer`, `FakeParsing` → `RequestParsing`, the realtime UDP receiver (`FakeReceiver.Audio` core) and `FakePtpSlave` → `PtpSlave` into it with product names and XML docs; the fake receiver keeps its options and events and delegates. `scaffold`: the public surface of Sections 4-8 as compiling types (`AirplayReceiver`, `AirplayReceiverOptions`, `ReceiverEvent` records, `IReceiverAudioOutput`, `PlayoutBuffer`, `IAacDecoder(+Factory)`, `PtpPort`, `DmapDecoder`, `SourceSwitch`, `AirplaySource`, DTO additions, settings keys, migration 2 registered) with `NotImplementedException` bodies where behaviour is pending, plus the new test projects. | all existing tests green; solution warning-free |
| 1 Protocol foundations | `advertise` (3.1-3.3: TXT, `/info`, responder registration, rename); `control-plane` (4.1-4.9 state machine, liveness, `/fp-setup`); `ptp-port` (6.1 refactor of `PtpMaster`, 6.2 `PtpSlave` estimation); `dmap-and-volume` (`DmapDecoder`, R9 mapping, `GET_PARAMETER` shape); `aac-decoder` (7.2 binding, vectors, `tools/get-ffmpeg.ps1`) | unit tests of 11.1 for these; loopback pairing + session SETUP tests |
| 2 Audio plane | `realtime-input` (5.1 incl. 0xD7 anchors, retransmits); `buffered-input` (5.2 framing, SSRC, decrypt, ADTS, decoder select); `playout` (5.3-5.5 buffer, release thread, drift, pause/flush/splice); `receiver-facade` (`AirplayReceiver` wiring, events, `EndSessionAsync`, statistics) | loopback audio tests of 11.1 (realtime, buffered ALAC, buffered AAC) |
| 3 Bridge | `source-switch` (8.2, `PlayerEventMapper` hook, `OutputManager` 8.6); `airplay-source` (8.3, settings, identity, store 8.4, options 8.5, self-exclusion R12); `api` (9.1 DTOs, routes, events, artwork); `deployment` (Section 10, docs for README/ARCHITECTURE/DEVELOPMENT) | Bridge tests of 11.1 |
| 4 Web | `web-player` (hero chip, disabled transport); `web-settings` (card); `web-diagnostics` (card); `i18n` (every key in every language) | bUnit tests of 11.1 |
| 5 Integration | `integration`: end-to-end bridge test (`WebApplicationFactory` with the receiver on loopback, our sender streaming, status/playback events observed), SPEC.md index + docs/README + MANUAL-TESTS section 6 (RP1-RP12), `build.ps1` end to end, privacy scan, final review | `./build.ps1 -SkipContainer` green |

## 14. Names used in code

| Name | Where | Meaning |
|---|---|---|
| `AirplayReceiver`, `AirplayReceiverOptions`, `ReceiverEvent.*`, `ReceiverStatus` | `Airplay2.Receiver` | the facade, its options (name, identity, password, ports, formats, timing), its event stream and state |
| `ReceiverIdentity` | `Airplay2.Receiver` | device id, psi, Ed25519 seed/public key (R11) |
| `ReceiverFeatures`, `ReceiverTxtRecords`, `InfoResponseBuilder`, `ReceiverAdvertiser` | `Airplay2.Receiver/Discovery` | R15 and Section 3 |
| `RtspServerConnection`, `RtspRequestHandler`, `RequestParsing`, `ControlSession`, `EventChannelServer` | `Airplay2.Receiver/Rtsp`, `/Session` | Section 4 |
| `IControllerStore`, `InMemoryControllerStore` | `Airplay2.Receiver/Pairing` | registered controllers (R7) |
| `RealtimeStreamReceiver`, `BufferedStreamReceiver`, `BufferedBlock` | `Airplay2.Receiver/Streams` | 5.1, 5.2 |
| `PlayoutBuffer`, `PlayoutStatistics`, `IReceiverAudioOutput` | `Airplay2.Receiver/Playout` | 5.3-5.5 |
| `IAacDecoder`, `IAacDecoderFactory`, `FfmpegAacDecoder`, `AdtsHeader` | `Airplay2.Receiver/Codecs` | 7.2 |
| `PtpSlave`, `IPtpSlave`, `IMonotonicClock` | `Airplay2.Receiver/Timing` | 6.2 |
| `PtpPort`, `IPtpPort`, `PtpPortOptions`, `IPtpEndpoint` | `Airplay2.Sender/Streams` | 6.1 |
| `DmapDecoder`, `DmapItem` | `Airplay2.Sender/Session` | 4.8 |
| `AlacMagicCookie.Default44100Stereo16` | `Airplay2.Sender/Alac` | 7.1 |
| `SourceSwitch`, `AudioSource` (enum) | `Bridge.Service/Output` | 8.2 |
| `AirplaySource`, `AirplayOptions`, `AirplaySettings` | `Bridge.Service/Airplay` | 8.3, 8.5 |
| `SetSourceLevelAsync`, `OutputManager.SourceAirplay`, `IGroupPlayer.FramesQueued` | `Bridge.Service/Output`, `Airplay2.Sender/Playback` | 8.6 |
| `AirplayReceiverDto`, `AirplayDiagnosticsDto` | `Bridge.Contracts` | 9.1 |
| `Lpipe`, `AnchorLead`, `LateThreshold`, `DriftThreshold` | `PlayoutBuffer` options | R6, 5.3-5.4 |
