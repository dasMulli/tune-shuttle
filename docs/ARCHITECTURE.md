# Architecture

How TuneShuttle is built: the components and how audio and control flow between them, the two audio sources and who
owns the output, the group volume model, what is stored, how receivers come and go, the API, the deployment shapes, and
the design decisions behind them. The AirPlay 2 protocol itself is specified in [`airplay2/SPEC.md`](airplay2/SPEC.md)
and its two parts, the receiver role in [`airplay2/SPEC-receiver.md`](airplay2/SPEC-receiver.md); the web app in
[`web/UI-SPEC.md`](web/UI-SPEC.md). For building and testing see [`DEVELOPMENT.md`](DEVELOPMENT.md).

## Components and data flow

```
 Spotify app ──(Spotify Connect: Zeroconf handoff, then Spotify's servers)──┐
 iPhone / iPad / Mac ──(AirPlay 2: mDNS, RTSP/HAP on TCP 7000, PTP, RTP)────┼──────────────────┐
                                                                            ▼                  ▼
 Bridge.Service (ASP.NET Core host, one process)
 ├─ SpotifyHost ── librespot-dotnet: Session, Spirc, Player (BridgeMixer: no attenuation)
 │                      │ f64 PCM, 44.1 kHz stereo, on librespot's player thread
 │                      ▼
 ├─ AirplaySink ── converts to S16, blocks above the player's high-water mark, discards while no output is connected
 │                      │
 ├─ AirplaySource ── Airplay2.Receiver: AirplayReceiver (advertiser, RTSP/HAP, ALAC + AAC decoders, PtpSlave)
 │                      │                 └─ PlayoutBuffer: S16 frames released at the sender's play time
 │                      ▼
 ├─ SourceSwitch ── owner: none | spotify | airplay; claims, evictions, the per-write gate (newest source wins)
 │                      ▼
 ├─ OutputManager ── owns the active group player for the selected output; drops player commands of a non-owner
 │                      ▼
 │   Airplay2.Sender: GroupPlayer ── PCM ring, pacer thread, one Timeline, ALAC encoder, GroupVolumeModel
 │                      ▼
 │   stream transport ── RealtimeStreamTransport (RTP over UDP, NTP or PTP timing)
 │                    └─ BufferedStreamTransport (TCP, PTP timing)
 │                      ▼ one encrypted session per receiver (ReceiverSession over RtspConnection)
 ├─ PtpPort (shared, UDP 319/320) ◄── PtpMaster (the sender's PTP transports) and PtpSlave (AirplaySource)
 ├─ ReceiverInventory ── mDNS browser + store (ignores our own receiver); ReceiverJanitor / ReceiverPruner
 ├─ PairingCoordinator ── the web app's PIN flow
 ├─ SqliteBridgeStore ── bridge.db
 └─ Api (REST + WebSocket) and the Bridge.Web SPA (static files)
                        ▼
 AirPlay 2 receivers: one, or a synchronized group
```

**Projects.** `src/Airplay2.Sender` is the protocol library: mDNS discovery (`MdnsReceiverBrowser`, on the DNS
packet code of `Librespot.Discovery`), the RTSP client with HAP channel encryption, pairing (`HapPairingClient`),
the receiver session (`ReceiverSession`), RTP/ALAC, the timing responder and control port, the PTP grandmaster
(`PtpMaster`), the stream transports, the group player and the `AirplaySender` facade that ties them together. It
uses no ASP.NET and no Spotify session or playback code. `src/Airplay2.Receiver` is the receiver role on the same
building blocks: the advertiser (`_airplay._tcp`/`_raop._tcp` through the `Librespot.Discovery` responder), the RTSP
server with HAP accessory pairing, the realtime and buffered stream receivers, the ALAC and AAC-LC (ffmpeg) decoders,
the `PtpSlave` and the `PlayoutBuffer`, behind the `AirplayReceiver` facade; it references `Airplay2.Sender` and uses no
ASP.NET, Spotify or SQLite code (SPEC-receiver.md R2). `src/Airplay2.Sender.Cli` (`airplay2`) drives the sender library
without Spotify. `src/Bridge.Contracts` holds the DTOs, routes and WebSocket message types shared by the service and
the web app. `src/Bridge.Service` (`bridge`) is the host, `src/Bridge.Web` the Blazor WebAssembly SPA.
`librespot-dotnet/` is the C# port of librespot that provides Spotify Connect.

**Audio path.** librespot's player calls `AirplaySink.Write` on its own thread. The sink converts to 16-bit samples
and hands them to the active `IGroupPlayer`; `Write` blocks while the player's ring is full, which is the backpressure
that keeps librespot in real time. Without a connected output the samples are dropped. The group player's pacer thread
takes 352 frames at a time on the timeline's schedule, ALAC-encodes them once and gives the packet to the transport,
which encrypts it per receiver (ChaCha20-Poly1305 with that receiver's key) and sends it. Silence is inserted on
underrun. The default sender latency L is 77 175 frames (1.75 s): audio is sent that far ahead of the moment it
should be heard.

**Timing.** The bridge is the single clock master for every receiver. Under NTP the receivers ask the sender's timing
responder (one UDP socket per process) for the time and get sync packets (0xD4) once a second on their control port;
under PTP the sender runs a PTP grandmaster and sends 0xD7 syncs. One `PtpPort` per process owns UDP 319/320 and routes
each message by type and source to the grandmaster or to the receiver role's `PtpSlave`, so the two roles share the
ports (SPEC-receiver.md R5). Retransmit requests (0xD5) are answered from a
per-receiver history of the last 1000 packets. A group that contains one receiver needing PTP runs entirely under PTP.

**Control path.** For each receiver `AirplaySender` runs `GET /info`, pairing (pair-verify with stored keys,
transient pairing, or the PIN flow), and `ReceiverSession` then performs the start-up sequence (session `SETUP`, event
channel, `RECORD`, stream `SETUP`, volume) over the encrypted RTSP connection, followed by metadata, artwork and
progress. The bridge also opens the receiver's event channel, on which the receiver sends its own messages; its
remote-control commands reach Spirc.

**Spotify events.** `PlayerEventMapper` turns librespot's player events into group-player actions: pause, seek and
skip flush the receivers, a track change sends metadata, artwork and progress, and the Spotify volume sets the master
level. Receivers stay connected while Spotify is paused or stopped; a receiver is torn down only when the user
disconnects it, removes it from the active group or selects another output.

**AirPlay receiver.** `AirplaySource` (a hosted service) runs one `AirplayReceiver` built from the stored settings
(enabled, name, password, identity) and `Bridge:Airplay`. It accepts one Apple sender at a time: `GET /info` and
`/fp-setup`, transient pairing (or the password as the SRP PIN), session and stream `SETUP`, then the realtime stream
(type 96, ALAC) or the buffered stream (type 103, ALAC or AAC-LC). Decoded 44.1 kHz/16-bit frames go into the
`PlayoutBuffer`, each stamped with the moment the sender wants it heard (mapped to local time through the `PtpSlave`'s
offset, or anchor-only without PTP), and are released into the active `IGroupPlayer` early enough to cover the
output's own write-to-audible delay (SPEC-receiver.md R6). The sender's volume sets the master level G with source
`airplay`; its DMAP metadata, artwork and progress become the AirPlay playback view and are forwarded to the receivers
through the existing now-playing path. Settings changes take effect without a restart: a new name or password
re-announces the services, `enabled` starts or stops the receiver.

**The source rule (newest wins, SPEC-receiver.md R8).** `SourceSwitch` holds the one owner of the output: none,
Spotify or AirPlay. An AirPlay stream `SETUP` claims it: Spotify is paused through Spirc, and the claim is granted only
after that pause reached the player. Spotify's `Playing` event claims it back: the AirPlay session is ended (the Apple
device shows the speaker as disconnected). A second Apple sender's stream `SETUP` ends the first one's session. When a
source leaves, the output is unowned; Spotify stays paused until someone plays. Every write and every player command
(start, pause, flush, now-playing, source level) is gated by ownership, and the playback view (`GET /api/playback`,
`playbackChanged`) is always the owner's. While AirPlay owns the output, the web app's transport commands answer `409`
and speaker buttons are ignored (the Apple device is the remote).

## Group volume model

Every receiver has a level `r_i` (0..100). A group has one master level `G`. Moving `G` to `G'` scales every member's
`r_i` by `G'/G`, so the differences between rooms stay as the user set them, as on Apple devices; members that would
pass 100 clamp at 100. The model keeps an unclamped weight per member, so a member clamped on the way up returns to its
old relation with the others on the way down, and `G = 0` does not erase the relation (the store remembers the
reference level of a group whose `G` is 0). For a single receiver as the output, `G` and `r_i` are the same value.

The Spotify app's volume slider drives `G` of the active output (librespot's mixer is replaced by `BridgeMixer`, which
never attenuates: audio is sent at full scale and each receiver attenuates it with its own level). Level changes made
in the web app are pushed back to Spotify. A level that a receiver reports on its own (its buttons) changes only its
`r_i`. Because no receiver tested so far reports such changes over the event channel, the bridge polls
`GET_PARAMETER volume` every 3 s while playing (`Bridge:PollReceiverVolumeSeconds`). On the wire a level maps to
`-30 + 0.3 * level` dB, and level 0 is sent as `-144` (mute).

## Spotify side

`SpotifyHost` runs one Spotify Connect device, named in Settings (`Bridge:DeviceName` until then). It announces itself
over Zeroconf (mDNS plus a small HTTP server on `Bridge:ZeroconfPort`). The credentials from a Zeroconf handoff are
cached in SQLite, so after a restart the device logs in again without a new handoff. A failed connect is retried with
back-off (5 s doubling to 5 min); rejected cached credentials are forgotten and the device waits for the next handoff;
changing the device name relaunches Zeroconf. Receiver buttons (play, pause, next, previous sent over the event
channel) and the web app's transport buttons are forwarded to Spirc.

## Persistence

One SQLite file, `<DataDirectory>/bridge.db`, written with Microsoft.Data.Sqlite and Dapper. The schema is
`src/Bridge.Service/Store/schema.sql` (WAL journal, foreign keys on), applied at start-up; upgrades are numbered
migrations on `PRAGMA user_version` in `StoreMigrations`: version 2 adds `airplay_controllers` (SPEC-receiver.md 8.4).

| Table | Holds |
|---|---|
| `receivers` | One row per receiver ever seen: id (the TXT `deviceid`), name, model, manufacturer, last address and port, features, status flags, public key, pairing state, level `r_i` (default 50), `offset_frames`, quirk overrides (JSON), `last_seen`, `created_at` |
| `pairings` | Per receiver: our controller id and Ed25519 seed, the receiver's identifier and long-term public key |
| `groups` | id (a UUID, also sent as `groupUUID` in the session `SETUP`), name, master level `G` |
| `group_members` | group, receiver, the member's level, position |
| `settings` | Key/value: device name, selected output, latency, stream preference, volume poll, controller identity, Spotify device id, group reference levels, `forget_offline_after`; for the AirPlay receiver `airplay_enabled`, `airplay_name`, `airplay_password` (null = open) and `airplay_identity` (JSON: device id, `psi`, Ed25519 seed; created once, R11) |
| `airplay_controllers` | Apple senders paired with the password (version 2): their HAP pairing identifier, Ed25519 long-term public key and `created_at`, so pair-verify works on reconnect (R7) |
| `spotify_credentials` | The cached librespot credentials |

Deleting a receiver cascades to its pairing and group memberships. Settings changed in the web app are stored here
and win over the configuration file. The CLI does not use SQLite: it keeps its pairings in `airplay2-pairings.json`
next to the executable.

The data directory also holds `logs/` (daily log files, Windows only, kept `Bridge:LogRetentionDays` days) and `tmp/`
(librespot's downloads in flight).

## Receiver inventory and pruning

`ReceiverInventory` runs the mDNS browser for `_airplay._tcp` and `_raop._tcp`, merges the two records of one device,
writes every discovered receiver into the store (keeping the user's level, offset and quirks), and publishes
`receiverChanged`. When a receiver (re)appears it asks the output manager to retry it. To connect, it resolves a
receiver id through mDNS and falls back to the stored last address after `Bridge:ResolveTimeout` (3 s), for networks
where mDNS is slow or blocked. Lost receivers are reconnected with back-off (`Bridge:ReconnectDelay`, doubling to
`Bridge:MaxReconnectDelay`).

A receiver is **offline** when it is neither in the browser's current snapshot nor connecting, connected or
streaming. Only offline receivers can be forgotten: an online one would be back from mDNS within seconds (the API
answers `409`). Forgetting (`ReceiverJanitor`) deletes the row with its pairing and group memberships, clears it as the
selected output, takes it out of the running output, and publishes `receiverRemoved`, `groupChanged` for every group
that lost it (an emptied group is kept) and `outputChanged` when the output changed.

`ReceiverPruner` runs one minute after start-up (so discovery has seen what is on the network) and then hourly. It
forgets offline receivers that were last seen longer ago than the retention (`Bridge:ForgetOfflineAfter`, 3 days;
the Settings page overrides it in days; 0 turns pruning off) and carry no user state. A receiver is kept when it is
paired, in a group, the selected output, or has its own level (other than 50), offset or quirks, and when it was
never seen.

## Bridge API

REST under `/api` (JSON, camelCase, the records of `Bridge.Contracts`) and one WebSocket at `/ws/events`. Errors are
RFC 9457 problem details: `404` unknown id, `409` state conflicts (a busy or online receiver, Spotify not
connected), `422` validation (including a wrong PIN), `502` receiver errors with the RTSP status in `detail`. In the
Development environment the OpenAPI document is at `/openapi/v1.json`.

| Method | Route | Body → answer | Notes |
|---|---|---|---|
| GET | `/receivers` | → `ReceiverDto[]` | discovery merged with the store; carries `lastSeen` |
| DELETE | `/receivers?offline=true` | → `{ "removed": [ids] }` | forgets every offline receiver; without `offline=true`: `422` |
| GET | `/receivers/{id}` | → `ReceiverDto` | |
| DELETE | `/receivers/{id}` | → `204` | forgets one receiver; `409` while it is online, connecting, connected or streaming |
| POST | `/receivers/{id}/pair` | → `PairingStartedDto` | starts the PIN flow: the receiver shows its PIN; answers a pairing token |
| POST | `/receivers/{id}/pair/pin` | `PairingPinRequest` (token, PIN) → `ReceiverDto` | finishes pairing, stores the keys |
| DELETE | `/receivers/{id}/pairing` | → `204` | forgets the stored keys |
| POST | `/receivers/{id}/connect` | → `ReceiverDto` | makes it the single output; publishes `pairingRequired` when a PIN is needed |
| POST | `/receivers/{id}/disconnect` | → `ReceiverDto` | |
| PUT | `/receivers/{id}/volume` | `VolumeRequest` → `ReceiverDto` | `r_i`, 0..100 |
| PUT | `/receivers/{id}/offset` | `OffsetRequest` → `ReceiverDto` | `offset_frames` |
| GET / POST | `/groups` | `GroupRequest` → `GroupDto` | list / create |
| GET / PUT / DELETE | `/groups/{id}` | `GroupRequest` → `GroupDto` | read / rename / delete (`204`) |
| POST | `/groups/{id}/members` | `GroupMemberRequest` → `GroupDto` | |
| DELETE | `/groups/{id}/members/{receiverId}` | → `GroupDto` | |
| PUT | `/groups/{id}/volume` | `VolumeRequest` → `GroupDto` | `G`; scales the members |
| POST | `/groups/{id}/activate` | → `OutputDto` | selects the group as the output |
| GET / PUT | `/output` | `OutputRequest` → `OutputDto` | the selected output (a group, a receiver, or none) |
| GET / POST | `/playback` | `PlaybackCommandRequest` (`play`, `pause`, `next`, `prev`) → `PlaybackDto` | the owner's view, with `source` (`spotify`, `airplay` or null); commands go to Spirc; `409` without Spotify and while the source is AirPlay |
| GET | `/playback/artwork` | → image bytes | the AirPlay sender's artwork with its content type and an `ETag` of its `rtptime`; `404` when the source is not AirPlay or no artwork arrived |
| GET / PUT | `/settings` | `SettingsDto` | device name, latency, poll, stream type, `forgetOfflineAfterDays`, `airplayEnabled`, `airplayName`, `airplayPassword` (a `PUT` without a field keeps the value; `""` clears the password); answers carry `airplayPasswordSet`, never the password |
| GET | `/status` | → `StatusDto` | with `activeSource` (the `SourceSwitch` owner) and `airplay` (receiver state, name, sender, `aacAvailable`) |
| GET | `/diagnostics` | → `DiagnosticsDto` | recent log, local addresses, ports, packet statistics, probes still open; `airplay`: RTSP port, PTP bound and offset, stream type and format, buffer, offset `D`, late/missing/drift counters, decoder status |
| DELETE | `/spotify` | → `204` | forgets the cached Spotify credentials |

Debug builds started with `--Bridge:FakeReceivers` add `POST /api/dev/fake-receivers/{id}/offline?daysAgo=N` for the
screenshot scripts.

WebSocket `/ws/events`: the server sends one JSON `BridgeEvent` per frame with a `type` discriminator:
`receiverChanged`, `receiverRemoved`, `groupChanged`, `groupRemoved`, `outputChanged`, `playbackChanged`,
`volumeChanged`, `pairingRequired`, `statusChanged`, `log`, `pong`. On connect it first sends a snapshot:
`statusChanged`, a `receiverChanged` per receiver, a `groupChanged` per group, then `outputChanged` and
`playbackChanged`. The client may send `{"type":"subscribe","log":true}` to receive the live log and
`{"type":"ping"}`. Each client has a bounded queue that drops the oldest events, so a slow browser never blocks the
bridge.

## Deployment

The service is an ASP.NET Core generic host with `UseWindowsService()` (service name `TuneShuttle`) and
`UseSystemd()` (`Type=notify`), so the same build runs in three shapes:

| Shape | How | Notes |
|---|---|---|
| Windows Service | `install/windows/install-service.ps1` over the published `artifacts/bridge` (with the ffmpeg DLLs from `tools/get-ffmpeg.ps1`) | Data in `%ProgramData%\TuneShuttle`; inbound firewall rules for the program, UDP 5353, the web port, "TuneShuttle AirPlay RTSP" (TCP 7000) and "TuneShuttle PTP" (UDP 319-320) |
| Linux with systemd | `install/linux/tune-shuttle.service`, binaries in `/opt/tune-shuttle` | Data in `/var/lib/tune-shuttle`; `CAP_SYS_NICE` so the pacer can raise its priority, `CAP_NET_BIND_SERVICE` for UDP 319/320; the distribution's FFmpeg 7.1 packages (`libavcodec61`, `libavutil59`, `libswresample5`) for AAC |
| Container (Kubernetes) | `docker/Dockerfile`: the ASP.NET Core 10 runtime on `debian:trixie-slim` with the FFmpeg 7.1 packages, `amd64` or `arm64` | Data in the `/data` volume; `EXPOSE 8080/tcp 7000/tcp 319/udp 320/udp`; the pod needs a macvlan interface on the receivers' LAN |

Listeners (the same in every shape):

| Port | Owner | Notes |
|---|---|---|
| TCP 8080 | Kestrel (`Kestrel:Endpoints:Http:Url`) | web app, API, WebSocket |
| UDP 5353 | mDNS: Spotify Zeroconf, the receiver's advertiser, the receiver browser | multicast |
| TCP, any free port | Spotify Zeroconf HTTP server (`Bridge:ZeroconfPort`) | |
| TCP 7000 | the AirPlay receiver's RTSP listener (`Bridge:Airplay:RtspPort`) | one receiver per host (R13) |
| UDP 319/320 | the shared `PtpPort` (`Bridge:Airplay:PtpEventPort`/`PtpGeneralPort`) | privileged ports; unbindable → the sender cannot use PTP receivers and the receiver runs anchor-only (R5, R6) |
| UDP and TCP, dynamic | the sender's timing, control and RTP ports; the receiver's per-session audio data, control and event ports | covered by the Windows program rule |

Without the FFmpeg 7.1 libraries the receiver advertises the ALAC-only formats (R3, R4).

Configuration comes from `appsettings.json`, then `BRIDGE_`-prefixed environment variables, then the command line;
values changed in the web app are stored in SQLite and win. The web app is served by the same process: published
layouts carry it in `wwwroot/`, and a run from the build output finds it through the static web assets manifest.

Logging goes to the console (the container's log), to an in-memory ring of the last 500 lines that the Diagnostics
page shows live, and on Windows to daily files under `<DataDirectory>/logs`.

AirPlay receivers send UDP back to the sender (NTP timing requests, retransmit requests, PTP messages), Spotify's
Zeroconf needs mDNS and an inbound HTTP port, and Apple senders connect to the receiver's TCP and UDP ports. A NAT-ed container network therefore does not work; the
macvlan interface gives the pod its own address on the LAN without host networking.

## Design decisions

| Topic | Decision | Why |
|---|---|---|
| Receivers per output | Single receivers **and** groups | Groups are the point of the project. |
| Clock | The bridge is the one clock master for every receiver of a group: one timeline, one timing responder and control port per process, per-receiver keys and offsets | What OwnTone does to synchronize AirPlay 2 receivers; it keeps the rooms in step without receiver-to-receiver coordination. |
| Stream type | The realtime stream (type 96: RTP, ALAC 44.1 kHz/16-bit, NTP timing) first; the buffered stream (type 103, PTP) is a second, pluggable transport, used only when preferred and supported | Needs no AAC encoder and no PTP daemon. A third-party AirPlay 2 speaker (srcvers 366.0, features 0x445F8A00,0x1C340) does not play the buffered stream with ALAC (probe P16). |
| Timing protocol | NTP by default, PTP per receiver from `KnownReceiverQuirks` (shairport-sync refuses NTP); a group with one PTP member runs entirely under PTP | The srcvers-366 speaker accepts NTP (probe P1). |
| Pairing | Transient pairing where the receiver allows it, the PIN flow through the web app otherwise; pair-verify with stored keys on every reconnect; one controller identity per installation | The PIN must be entered by a person, and the web app is where people are. |
| No receiver connected | Accept playback and discard the audio | Spotify should never fail to play; local playback is not a goal. |
| User interaction | Only through the web app; the service never prompts | It runs headless as a service or in a pod. |
| AirPlay 1 | No fallback | Every target receiver speaks AirPlay 2. |
| Persistence | SQLite with hand-written SQL (Microsoft.Data.Sqlite + Dapper) | Seven small tables; EF Core would add migrations tooling and a model for no benefit. |
| Crypto | The .NET BCL for ChaCha20-Poly1305, HKDF, SHA-512 and SRP-6a (hand-written on `BigInteger`); BouncyCastle only for X25519 and Ed25519 | .NET 10 has no X25519/Ed25519 type. |
| bplist, ALAC | Hand-written binary plist reader/writer; Apple's ALAC encoder ported bit-exact | Receivers accept only `bplist00`; no maintained .NET package fits either. |
| Web app | Blazor WebAssembly, built by the plain .NET SDK, served by the bridge | No node toolchain; shares the `Bridge.Contracts` types at compile time; its state code is unit-testable. |
| Live updates | A raw WebSocket with a small message set, no SignalR | The web app is the only client. |
| Audio output libraries | No NAudio on the bridge's path | The sink is the AirPlay sender, and the container runs on Linux. |
| Sender dependencies | `Airplay2.Sender` references `Librespot.Discovery` for its mDNS packet code, so `Librespot.Core` is a transitive dependency; no Spotify session, protocol or playback code is used or reachable from the sender | Reusing the codec beats a second one; the dependency is recorded rather than hidden. |
| CLI | System.CommandLine; audio only with `--i-know` | AirPlay wakes receivers and plays at their current volume. |
| AirPlay receiver | An AirPlay 2 receiver only (HAP pairing, PTP, stream types 96 and 103), no AirPlay 1 path (SPEC-receiver.md R1); a library of its own, `Airplay2.Receiver`, built from the fake receiver's tested code (R2) | iOS sends buffered AAC to AirPlay 2 receivers; AirPlay 1 would need Apple's private RSA key. |
| Receiver formats | 44.1 kHz/16-bit stereo only: ALAC (realtime and buffered) and AAC-LC (buffered); without the AAC decoder the ALAC-only variant everywhere it is advertised (R3) | The output pipeline is 44.1/16; no resampler. |
| AAC decoder | FFmpeg's libavcodec through FFmpeg.AutoGen, loaded dynamically, one pinned FFmpeg major (7.1), behind `IAacDecoder` (R4) | .NET has no AAC decoder; shairport-sync needs libavcodec for the same reason. |
| PTP ports | One `PtpPort` per process, shared by the grandmaster and the receiver's passive slave; anchor-only timing when it cannot bind (R5, R6) | Two sockets cannot share 319/320. |
| Two sources | Newest source wins: an AirPlay stream `SETUP` pauses Spotify, a Spotify play ends the AirPlay session (R8) | What a HomePod does; the person who just pressed play gets what they expect. |
| Receiver volume | The sender's dB maps through the inverse of the sender's mapping to the master level G (R9) | The group volume model stays the one place where levels live. |
| Receiver identity | One per installation, stored in `settings`; our own advertisement is ignored by the receiver browser (R11, R12) | Stable pairing for password mode; the bridge must not list itself. |
| Receivers per host | One; TCP 7000 (R13) | Senders are confused by two AirPlay 2 receivers at one address. |

The protocol-level decisions (D1-D17: start-up order, wire hygiene, `TEARDOWN` body, retransmit reply form, nonce
rules, volume mapping, metadata) are in [`airplay2/SPEC.md`](airplay2/SPEC.md) section 2, and the defaults that came
out of the live probes in its section 4.2.
