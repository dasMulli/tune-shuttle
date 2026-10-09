# AirPlay 2 bridge: work plan

Status: 2026-10-09, revised the same day after review (see section 9 for what changed). Binding inputs:
`docs/PLAN.md` (goals, decisions, architecture, volume model, deployment, milestones) and `docs/airplay2/SPEC.md`
(decisions D1-D17, open questions P1-P21) with its two parts. This plan turns them into work packages of
1,500-3,500 lines each, in waves such that every package depends only on earlier waves. The scaffold under `src/`,
`tests/` and `AirplayBridge.sln` is in place and green (`dotnet build`, `dotnet test`: 66 tests); it already
implements and verifies the crypto primitives, TLV8, bplist, the ALAC codec and the TXT/feature parsing, and it
defines every cross-package contract as C# interfaces and records with `// TODO(impl: <package-id>)` stubs. Packages
fill the stubs; they do not change the contracts without recording the change in this file.

## 1. Conventions

- **Language and runtime**: C# latest on .NET 10 (`net10.0`), `Nullable` enabled, `ImplicitUsings` enabled,
  warnings as errors, file-scoped namespaces, `record`/`record struct` for data, XML docs on every public member
  (`GenerateDocumentationFile` is on; CS1591 is suppressed so missing docs do not fail the build, but packages add
  them). `.editorconfig` at the root (same as librespot-dotnet).
- **Async**: every I/O method is `async` and takes a `CancellationToken` as its last parameter; no `.Result`/`.Wait()`,
  no `Task.Run` to fake asynchrony, no blocking inside `async` methods. The two deliberate exceptions are documented:
  `IGroupPlayer.Write` blocks the librespot player thread as its backpressure (SPEC-audio.md 8.2) and the pacer runs
  on a dedicated thread with `Stopwatch` sleeps.
- **Producer/consumer**: `System.Threading.Channels` (`Channel<T>`, bounded, `DropOldest` for event feeds, `Wait` for
  audio) instead of events or `BlockingCollection`.
- **Logging**: `Microsoft.Extensions.Logging.ILogger<T>` injected through constructors; structured messages
  (`"SETUP stream {StreamType} to {Receiver}"`), `Debug` for wire-level detail, never `Console.WriteLine` in libraries.
- **Errors**: specific exceptions (`RtspStatusException`, `PairingException` with `PairingFailure`), never swallowed
  silently; transport failures surface as `Closed` tasks on connections and `Lost` states on group members.
- **Threading**: one owner per socket; state machines guard their state with a `SemaphoreSlim` or a single
  consumer loop; cross-thread hand-off through channels.
- **Tests**: xunit 2.9.3 with `Microsoft.NET.Test.Sdk`; `[Fact]`/`[Theory]`; vectors from `docs/airplay2/test-vectors`
  (linked into the test output as `TestVectors/`); loopback sockets on `127.0.0.1` with OS-chosen ports. **Live
  tests** carry `[Trait("Category", "SpeakerALive")]` or `[Trait("Category", "ShairportLive")]` **and** return early
  (`Assert.Skip`-style: log "AIRPLAY_LIVE not set" and return) unless the environment variable `AIRPLAY_LIVE=1` is
  set; the plain `dotnet test` therefore never touches a receiver, the CI invocation additionally excludes them
  (`dotnet test --filter "Category!=SpeakerALive&Category!=ShairportLive"`), and every runner that *wants* them
  sets the variable (`docker/shairport-sync/run-live-tests-in-vm.ps1` does; a SpeakerA run is
  `$env:AIRPLAY_LIVE=1; dotnet test --filter Category=SpeakerALive`). ShairportLive tests also read `SPS_HOST`/`SPS_PORT`.
  NSubstitute for interface doubles where the fake receiver is too heavy.
- **Licences in code**: ported Apple ALAC files keep the Apache-2.0 header note; nothing is transcribed from
  owntone-server (GPL): where the spec describes owntone's behaviour (group sync, metadata rtptimes, start-up
  order) the package reimplements that **behaviour from the spec's description**, with its own names, and never
  opens owntone's files to copy from. librespot-dotnet (MIT) is referenced, not copied.
- **Git**: packages work on branches and never commit on the user's behalf; the user merges.
- **Frame-size rule** (SPEC-control 2.5, D9): HAP frames we send carry at most 1024 bytes of plaintext and that cap
  is a `const`, not an option; frames we receive may carry up to 65 535 bytes and every reader (control connection,
  event channel, fake receiver) is sized by the length field.

## 2. Project layout

```
AirplayBridge.sln                      all projects below plus the librespot-dotnet ones (solution folder librespot-dotnet)
Directory.Build.props / Directory.Packages.props / .editorconfig / .dockerignore   conventions and central package versions for src/ and tests/
src/
  Airplay2.Sender/                     the protocol library (no ASP.NET; no Spotify session/playback code, see the note below)
    Crypto/      HapKdf, ChaCha20Poly1305Aead, X25519KeyPair, Ed25519KeyPair, Srp6a/SrpClient/SrpServer, HapChannelKeys, HapFrameCipher   [done]
    Tlv8/        Tlv8Document, TlvType/Method/Error/Flags                                                                   [done]
    Plist/       BinaryPlist, PlistDictionary, PlistUid                                                                    [done]
    Alac/        BitBuffer, AdaptiveGolomb, Predictor, StereoMatrix, AlacEncoder, AlacDecoder, AlacMagicCookie             [done]
    Discovery/   AirplayFeatures, AirplayStatusFlags, AirplayTxtRecord, RaopTxtRecord, ReceiverCapabilities [done]; DiscoveredReceiver, IReceiverBrowser, MdnsReceiverBrowser [mdns-browser]
    Rtsp/        RtspRequest/Response/IncomingRequest, RtspClientIdentity, RtspMessageCodec, IRtspConnection, RtspConnection [rtsp-transport]
    Pairing/     ControllerIdentity, PairingCredentials, PairingSession, IPairingClient, HapPairingClient, IPairingStore, HapAccessoryPairing (internal) [pairing]
    Session/     IReceiverSession, ReceiverSession, IEventChannel, SessionSetupRequest/Result, StreamSetupRequest/Result, TrackMetadata, DmapEncoder, ReceiverQuirks [session-streams]
    Rtp/         IMasterClock, MasterClock, NtpTimestamp [done]; RtpAudioPacketBuilder, SyncPacket, PtpSyncPacket, TimingPacket, RetransmitRequest, IPacketHistory, ITimingResponder, IControlPort [rtp-timing-control]
    Streams/     IStreamTransport, RealtimeStreamTransport (NTP and PTP syncs) [audio-pipeline-group-sync], IPtpMaster, PtpMaster [ptp-master], BufferedStreamTransport [buffered-stream]
    Playback/    VolumeMapping [done]; GroupVolumeModel, Timeline, IGroupPlayer, GroupPlayer [audio-pipeline-group-sync]
    AirplaySender.cs  facade                                                                                               [sender-api-cli]
  Airplay2.Sender.Cli/                 `airplay2` executable: discover, info, pair, play, volume, probe                   [sender-api-cli]; JsonFilePairingStore.cs is owned by bridge-store (added in wave 1)
  Bridge.Contracts/                    DTOs, routes, WebSocket message types shared by service and web app               [done; extended by bridge-service]
  Bridge.Service/                      ASP.NET Core host: Api/, Store/ (schema.sql, SqliteBridgeStore), Output/AirplaySink, Spotify/SpotifyHost [bridge-store (Store/, Api/Mapping.cs), bridge-service (everything else)]
  Bridge.Web/                          Blazor WebAssembly SPA, hosted by Bridge.Service                                    [web-app]
tests/
  Airplay2.FakeReceiver/               in-process receiver used by every test project                                     [fake-receiver]; RtspServerConnection.cs is owned by rtsp-transport (added in wave 1)
  Airplay2.Sender.Tests/               unit + fake-receiver + live tests for the sender
  Bridge.Tests/                        store, API, sink and end-to-end tests for the bridge; Web/ holds the web-app tests (references Bridge.Web, bUnit)
docker/Dockerfile                      Linux image of the bridge; docker/shairport-sync/ local test receiver (TEST-RECEIVER.md)
install/windows/install-service.ps1    Windows Service + firewall; install/linux/airplay-bridge.service systemd unit
docs/airplay2/                         SPEC.md (index), SPEC-control.md, SPEC-audio.md, WORK-PLAN.md, TEST-RECEIVER.md, test-vectors/, probes/
```

The librespot projects are referenced by `ProjectReference` into `../librespot-dotnet/src/...`; they keep their own
`Directory.Build.props` (MSBuild stops at the nearest one), so the root props never apply to them.

**Dependency note (PLAN.md "No Spotify or ASP.NET dependencies")**: `Airplay2.Sender` references
`Librespot.Discovery` for its mDNS packet codec (`Mdns/DnsPacket.cs`), and `Librespot.Discovery` references
`Librespot.Core`, so the Spotify core *library* is a transitive dependency of the sender assembly. No Spotify session,
protocol or playback code is used or reachable from the sender; the dependency is accepted and recorded here rather
than hidden. The clean alternative, a dependency-free `Librespot.Discovery.Mdns` project (the codec file has only
`System.*` usings), is a change inside librespot-dotnet and is left to `mdns-browser` as an optional follow-up; it
must not be done by copying the file.

**Cross-folder files** (one owner, stated so two packages never write the same file): `tests/Airplay2.FakeReceiver/
RtspServerConnection.cs` is owned by `rtsp-transport` and added in wave 1 (the fake receiver and the event-channel
tests consume it); `src/Airplay2.Sender.Cli/JsonFilePairingStore.cs` and `src/Bridge.Service/Api/Mapping.cs` are
owned by `bridge-store` and added in wave 1 (`sender-api-cli` and `bridge-service` consume them and may extend
`Mapping.cs` for new DTO fields); `src/Airplay2.Sender/Pairing/HapAccessoryPairing.cs` is owned by `pairing`
(wave 1) and consumed by `fake-receiver` (wave 2) through `InternalsVisibleTo`.

## 3. NuGet choices (versions verified against api.nuget.org on 2026-10-09)

| Need | Choice | Reason |
|---|---|---|
| ChaCha20-Poly1305, HKDF, SHA-512, HMAC | .NET 10 BCL (`System.Security.Cryptography`) | Present and supported on this host (`ChaCha20Poly1305.IsSupported == true`); the wrapper falls back to BouncyCastle where an OS lacks it. |
| X25519, Ed25519 | **BouncyCastle.Cryptography 2.7.0** | .NET 10.0.12 exports no X25519/Ed25519 type (checked by reflection); BouncyCastle's `Org.BouncyCastle.Math.EC.Rfc7748.X25519` and `Rfc8032.Ed25519` are low-level, allocation-light and verified against the RFC vectors. |
| SRP-6a | hand-written over `System.Numerics.BigInteger` | HomeKit's variant (3072-bit group, SHA-512, padded `k`/`u`) is small; vectors generated with srptools 1.0.1 (independent Python implementation) from the RFC 5054 inputs. |
| bplist | hand-written `BinaryPlist` | No maintained .NET package writes `bplist00` with ordered dictionaries; the format is 300 lines; verified against the SpeakerA's responses and plistlib both ways. |
| SQLite | **Microsoft.Data.Sqlite 10.0.12 + Dapper 2.1.89** | Five tables, hand-written SQL in `schema.sql`; EF Core would add migrations tooling and a model for no benefit. |
| Hosting | **Microsoft.Extensions.Hosting 10.0.12**, `.WindowsServices`, `.Systemd` | PLAN.md deployment targets; `UseWindowsService()` + `UseSystemd()` both on one host. |
| Web API | ASP.NET Core (framework reference), **Microsoft.AspNetCore.OpenApi 10.0.12** in Development | Minimal APIs + raw WebSocket (`UseWebSockets`), no SignalR (the web app is the only client and the message set is small). |
| Web app | **Blazor WebAssembly** (`Microsoft.AspNetCore.Components.WebAssembly` 10.0.12, `.DevServer`, `.Server` for hosting) | No node toolchain is available. Blazor WASM is built by the .NET SDK alone (verified: the solution builds without the `wasm-tools` workload as long as AOT/SIMD/relinking stay at their defaults), shares the `Bridge.Contracts` DTOs at compile time, and its view-model/state code is unit-testable with xunit. A no-build vanilla JS SPA would avoid the ~2 MB initial download but has no shared types, no compiler and no test runner without node. Both choices fit PLAN.md's "web only"; Blazor is recommended. |
| CLI | **System.CommandLine 2.0.12** | Stable 2.0 API (`SetAction`, `Parse().InvokeAsync()`). |
| Tests | Microsoft.NET.Test.Sdk 18.10.1, xunit 2.9.3, xunit.runner.visualstudio 3.1.5, NSubstitute 6.2.0 | Same as librespot-dotnet. |
| Web component tests | bunit 2.11.3 (added by web-app, wave 7, 2026-10-09) | Supports .NET 10; used only by `tests/Bridge.Tests/Web`. |
| Not used | NAudio (Windows only; the bridge's sink is the AirPlay sender), Zeroconf/Makaretu.Dns (the librespot mDNS codec is reused), SignalR, EF Core | PLAN.md: container path must not depend on NAudio. |

## 4. Live tests: what is allowed when

| Stage | Allowed against the Test Speaker ABCD (192.0.2.26) | Never |
|---|---|---|
| any time | mDNS browsing; unauthenticated `GET /info` | the LG TV (AirPlay wakes it); any receiver not listed here; the Apple receiver without its owner's go-ahead |
| once `rtsp-transport` + `pairing` exist (end of wave 1) | transient pair-setup M1-M4, `GET /info` inside the encrypted channel, graceful close; probes P2, P6, P11 | PIN pairing attempts on devices that show a PIN |
| once `session-streams` exists (wave 3) | session SETUP, event channel, RECORD, stream SETUP, SET_PARAMETER, FLUSH, TEARDOWN; probes **P1 (control part, decisive for D1)**, P3, P4, P5, P8, P9, P10, P13, P15 (control part), P21 | sending audio packets |
| once `audio-pipeline-group-sync` exists (wave 4) | **audio only as a 3-second quiet test tone** (440 Hz, -24 dB full scale, level ≤ 20 → wire volume ≤ -24 dB) with an explicit `--i-know` flag (the CLI refuses audio without it, exit code 2); probes P1 (audio confirmation), P7, P12, P14, P15, P18, P19 | music, long sessions without the user present |
| final integration (wave 8) | longer sessions and group tests with the user present | |

The D1 gate (NTP vs PTP) is therefore **settled in wave 3** by the control part of P1: 200 + `eventPort` on the NTP
session SETUP and `ITimingResponder.RepliesSent > 0` within 2 s of the stream SETUP, with no audio sent. Wave 4
starts knowing the SpeakerA's timing protocol.

Every live test logs the complete wire exchange (decrypted) to `artifacts/live/<date>-<probe>.log` and appends its
outcome to the probe table in `SPEC.md`.

## 5. Spec section ownership

Every subsection of the two spec parts is implemented by exactly one package; where a section describes both a
primitive and its use, the split is stated. "scaffold" means done in wave 0. Sections not listed are data,
diagrams or probes (SPEC-control 1.7, 1.8, 6, 7, appendices; SPEC-audio 0, 9, appendix) and belong to no package.

| Spec section | Owner | Split / note |
|---|---|---|
| SPEC-control 1.1-1.6 | mdns-browser | 1.2-1.6 parsing and the decision procedure are in the scaffold (`Discovery/`); mdns-browser adds the browser and the merge of the two services |
| SPEC-control 2.1-2.4 | rtsp-transport | codec, connection, identity headers, status handling, the Content-Type/Content-Length rule |
| SPEC-control 2.5 | scaffold (`HapChannelKeys`, `HapFrameCipher`: keys, nonce, AAD, send cap 1024, receive up to 65 535) / rtsp-transport (switching a live socket to the cipher, "response to the pairing request is still plaintext", frame-to-message reassembly) | |
| SPEC-control 3.1-3.9 | pairing | both sides: `HapPairingClient` and `HapAccessoryPairing`; `IPairingStore` persistence contract (3.8) is implemented by bridge-store (SQLite) and the JSON store |
| SPEC-control 3.10 | session-streams | `AuthSetupAsync` behind `ReceiverQuirks.SendAuthSetup`; `/fp-setup` and `/pair-setup-pin` are not implemented |
| SPEC-control 4.1-4.5, 4.9-4.12 | session-streams | start-up order (D5), session SETUP, event channel, RECORD, TEARDOWN, feedback, GET_PARAMETER (parser in the scaffold's `VolumeMapping.ParseParameter`) |
| SPEC-control 4.6-4.8 | session-streams (request bodies and headers for `SETPEERS`, `SETRATEANCHORTIME`, `FLUSH`, `FLUSHBUFFERED`) / audio-pipeline-group-sync (**when** to send FLUSH, SPEC-audio 8.4) / buffered-stream (when to send the anchor and FLUSHBUFFERED, SPEC-audio 4.5) | |
| SPEC-control 5.1 | scaffold (`VolumeMapping`) / session-streams (`SetVolumeAsync`, `GetVolumeAsync`) / audio-pipeline-group-sync (when: after stream SETUP, on change, coalescing, poll) | |
| SPEC-control 5.2-5.4 | session-streams | `DmapEncoder`, artwork, progress requests; the rtptime values come from audio-pipeline-group-sync (SPEC-audio 7) |
| SPEC-audio 1.1 | session-streams | stream SETUP body (type 96) |
| SPEC-audio 1.2, 1.3 | scaffold (`ChaCha20Poly1305Aead`) / rtp-timing-control (`RtpAudioPacketBuilder`: header, nonce counter, AAD, trailer) | audio-pipeline-group-sync only *calls* the builder per receiver |
| SPEC-audio 1.4, 5 | scaffold (ALAC) | |
| SPEC-audio 1.5, 1.6 | audio-pipeline-group-sync | `L`, anchoring, first sync before first audio, 7 s rule |
| SPEC-audio 2.1 | scaffold (`MasterClock`, `NtpTimestamp`) | |
| SPEC-audio 2.2 | rtp-timing-control | `TimingPacket`, `TimingResponder` |
| SPEC-audio 2.3 | rtp-timing-control (`SyncPacket.Write`, `IControlPort.SendSyncAsync`) / audio-pipeline-group-sync (**cadence**: one per second per receiver, `now_frame = next_ts - L - offset_i`, `0x90` on start/join/flush) | the cadence logic lives in `RealtimeStreamTransport` only |
| SPEC-audio 2.4 | rtp-timing-control | `RetransmitRequest`, `PacketHistory`, `ControlPort` with the address-first lookup rule and both reply forms; audio-pipeline-group-sync only registers receivers |
| SPEC-audio 3.1-3.4 | audio-pipeline-group-sync | one timeline, join/remove/resync, `offset_frames`, drift notes |
| SPEC-audio 4.1-4.3, 4.5 | buffered-stream | negotiation, TCP packet layout, anchor/flush driving |
| SPEC-audio 4.4 (PTP grandmaster: Announce/Sync/Follow_Up/Delay_Resp/Signaling, clock id) | ptp-master | `PtpMaster : IPtpMaster` |
| SPEC-audio 4.4 (0xD7 sync packet layout) | rtp-timing-control (`PtpSyncPacket.Write`) / audio-pipeline-group-sync (sending it instead of 0xD4 when the transport has an `IPtpMaster`) | |
| SPEC-audio 6.1 | scaffold (`VolumeMapping.LevelToDb/FormatParameter`) / session-streams (`SetVolumeAsync` on the wire) / audio-pipeline-group-sync (ordering and coalescing) | |
| SPEC-audio 6.2 | session-streams (event-channel message parsing, `GetVolumeAsync` incl. the leading-CRLF rule) / audio-pipeline-group-sync (the 3 s poll fallback, mapping to `GroupVolumeModel.ReceiverReported`) | |
| SPEC-audio 6.3 | audio-pipeline-group-sync | `GroupVolumeModel` |
| SPEC-audio 7 | audio-pipeline-group-sync | rtptime bookkeeping (`start`, `display`, `current`, `end`) behind `IGroupPlayer.SetNowPlayingAsync`; session-streams only sends what it is given |
| SPEC-audio 8.1-8.3, 8.5 | audio-pipeline-group-sync | ring, pacer, underrun, threads |
| SPEC-audio 8.4 | audio-pipeline-group-sync (the sink-side actions: flush, re-anchor, timestamp discipline) / bridge-service (mapping librespot `PlayerEvent`s to those actions) | |
| SPEC.md D9 | scaffold (`ChaCha20Poly1305Aead.NonceFromCounter`, `HapFrameCipher`) / rtp-timing-control (RTP nonce counter in `RtpAudioPacketBuilder`) | |

## 6. Work packages

Format: id, wave, dependencies, size estimate (production + test lines), what it delivers, the public contract it must
honour (types already in the scaffold unless marked *new*), and how to verify. "Fake" means the in-process
`Airplay2.FakeReceiver`; "live" means the SpeakerA under the rules of section 4. Waves: 0 scaffold; 1 mdns-browser,
rtsp-transport, rtp-timing-control, bridge-store, pairing; 2 fake-receiver; 3 session-streams; 4
audio-pipeline-group-sync, ptp-master; 5 sender-api-cli, buffered-stream; 6 bridge-service; 7 web-app;
8 final-integration. Every dependency is in an earlier wave; no package depends on another of the same wave.

### Wave 0 (done in the scaffold): `crypto-tlv8-bplist-srp`, `alac-encoder`

`Airplay2.Sender/Crypto`, `Tlv8`, `Plist`, `Alac`, `Discovery/{AirplayFeatures,TxtRecords,ReceiverCapabilities}`,
`Rtp/Timing` (`MasterClock`, `NtpTimestamp`), `Playback/VolumeMapping`. Verified by 63 tests: RFC 7748 §5.2/§6.1,
RFC 8032 §7.1 (3 vectors), RFC 8439 §2.8.2, RFC 5869 test case 1, ten HKDF-SHA512 HomeKit known answers computed
independently with Python's `hmac`/`hashlib` (`docs/airplay2/probes/gen_hkdf_vectors.py`; control/events/pair-setup/
pair-verify keys from fixed 64- and 32-byte secrets) plus a manual extract/expand cross-check, HAP frame round
trip/fragmentation/tamper, receive-side frames of 3000 and 65 535 bytes, SRP vectors (2) plus wrong-PIN and
malformed-B rejection, TLV8 fragmentation, bplist decode of both SpeakerA captures + re-encode + plistlib vector + all
value types + >255 objects, ALAC bit-exact on all six reference packets in order, decoder on the same, round trips
(8 signals incl. partial frames, 200-packet stream), TXT/feature decoding of both LAN receivers, the 1.6 decision
procedure, volume mapping incl. the leading-CRLF `GET_PARAMETER` form, NTP timestamps.

### `mdns-browser` (wave 1) — depends on: scaffold — size: ~1,800

Delivers `MdnsReceiverBrowser` (SPEC-control 1.1-1.6) on top of `Librespot.Discovery.Mdns.DnsMessage`: multicast
sockets per interface and family (224.0.0.251 / ff02::fb, port 5353, `ReuseAddress`, also a unicast-response query from
an ephemeral port as a fallback for reflectors), PTR queries for both service types every `QueryInterval`, a cache
keyed by instance name that merges `_airplay` and `_raop` records of one device (by `deviceid`/`@`-prefix), SRV →
A/AAAA resolution with follow-up queries when missing, TTL expiry with grace, goodbye (TTL 0) handling, addresses
taken only from A/AAAA records (never the UDP source), `Found/Updated/Lost` events, `FindAsync` by id.

Contract: `IReceiverBrowser`, `ReceiverBrowserOptions`, `DiscoveredReceiver`, `ReceiverEvent` (scaffold).
Verify: unit tests feeding recorded packets (`probes/browse.json` turned into DNS messages with the librespot
encoder; reflector case with source 203.0.113.1; IPv6 AAAA; TTL expiry with a fake clock; Updated on changed `gid`);
loopback test with an `MdnsResponder` from librespot advertising a fake service (the test project references
`Librespot.Discovery` directly for that); live: `airplay2 discover` must list the SpeakerA with the decoded
capabilities from SPEC-control 1.7 (allowed any time). Optional follow-up recorded in section 2: a dependency-free
`Librespot.Discovery.Mdns` project.

### `rtsp-transport` (wave 1) — depends on: scaffold — size: ~2,200

Delivers `RtspMessageCodec` (encode requests per SPEC-control 2.1-2.2 with the `RtspRequest` Content-Type/Content-Length
rule: Content-Type whenever set, Content-Length whenever Content-Type is set or the body is non-empty, so
`POST /pair-pin-start` goes out with `Content-Type: application/octet-stream` and `Content-Length: 0`; parse
responses and incoming requests with case-insensitive headers, `Content-Length`, either protocol token, `bplist`
magic sniffing when headers are odd) and `RtspConnection` (raw TCP, 25 s connect timeout, one request in flight, CSeq
matching with FIFO fallback, per-request timeout, `EnableEncryption` switching reads/writes to `HapFrameCipher` with
the "response to the pairing request is still plaintext" rule, receive buffer sized for 65 535-byte frames, `Closed`
task, decrypted wire logging at Debug). Also `RtspServerConnection` (*new*, in `Airplay2.FakeReceiver`, owned by this
package) that accepts a socket and serves requests, used by the fake receiver and by the event-channel server side.

Contract: `IRtspConnection`, `RtspConnectionOptions`, `RtspRequest/Response/IncomingRequest`, `RtspMessageCodec`.
Verify: codec unit tests against the captured `GET /info` exchange (request bytes from `probes/get_info_SpeakerA.txt`,
response head + the 525-byte body), the pin-start form (Content-Type + Content-Length: 0, no body), split-read parsing
(headers in one chunk, body in three), encrypted framing round trip through a loopback pair of connections (frames not
aligned with messages, >1024-byte bodies in both directions, a single 4000-byte frame from the server side), CSeq
mismatch handling, timeout and close propagation; live: `GET /info` RTSP/1.0 bare and with the qualifier body against
the SpeakerA must equal the stored vectors (allowed any time).

### `rtp-timing-control` (wave 1) — depends on: scaffold — size: ~2,000

Delivers `RtpAudioPacketBuilder.Build` (SPEC-audio 1.2-1.3), `SyncPacket.Write` (2.3 layout), `PtpSyncPacket.Write`
(4.4 layout, 28 bytes), `TimingPacket.TryParse/Reply/Write`, `RetransmitRequest.TryParse` (8- and 16-byte forms),
`PacketHistory` (ring of 1000 complete packets, wrap-safe range check), `TimingResponder` (one UDP socket, answers
0xD2 with receive/transmit NTP from `IMasterClock`, replies to the source address), `ControlPort` (one UDP socket;
sends syncs on request; receives 0xD5, finds the receiver by source **address first, port only to disambiguate**
(SPEC-audio 2.4) and answers from its history with the wrapped 0xD6 form or the raw packet on the data port; logs
unknown types). It does **not** decide when syncs are sent (that cadence is audio-pipeline-group-sync's).

Contract: everything in `Rtp/Packets.cs` (scaffold).
Verify: byte-exact unit tests against the worked example in SPEC-audio 1.3 (header `80 60 12 34 00 01 58 88 DE AD BE EF`,
nonce 7) with a known key and a check that decrypting with `ChaCha20Poly1305Aead` recovers the frame; sync packet
layout against SPEC-audio 2.3 and the 0xD7 layout against 4.4; timing reply fields against 2.2 (reference = request
transmit); retransmit parse of both lengths; loopback UDP tests: a fake receiver thread sends 0xD2 and 0xD5 and checks
the replies, drops every 50th packet and verifies byte-identical retransmissions (this is probe P14 on the fake), a
request from a port other than the registered one is still answered, two registered receivers on 127.0.0.1 are told
apart by port. No live test.

### `bridge-store` (wave 1) — depends on: scaffold — size: ~1,600

Delivers the full `SqliteBridgeStore` (all `IBridgeStore` methods with Dapper, `user_version` migrations, WAL,
`IPairingStore` mapping to `pairings`, controller identity in `settings`), a `JsonFilePairingStore` (*new*, in
`Airplay2.Sender.Cli`, owned by this package, same `IPairingStore`) for the CLI, and the `ReceiverRow`/`GroupRow` ↔ DTO
mapping helpers in `Bridge.Service/Api/Mapping.cs` (*new*, owned by this package; bridge-service may extend it).

Contract: `IBridgeStore`, `IPairingStore`, `schema.sql` (scaffold; new columns only via a migration to `user_version 2`).
Verify: xunit against a temp database file: CRUD for every table, cascade deletes, idempotent `InitializeAsync`,
migration from version 1 to the current version, concurrent writers, pairing round trip with real
`PairingCredentials`, credential blob round trip. No live test.

### `pairing` (wave 1) — depends on: scaffold (crypto, TLV8, the `IRtspConnection` contract) — size: ~2,400

Delivers `HapPairingClient` (SPEC-control 3): transient M1-M4 (`/pair-pin-start` HKP 4, Flags 0x10, PIN 3939, M2 proof
verified), regular M1-M6 split into start/finish for the web app (HKP 3, `Method` selectable for P6, M5 with
`iOSDeviceX` signature, M6 decrypted and the accessory signature **verified**), pair-verify M1-M4 (ephemeral X25519,
`PV-Msg02/03` nonces, accessory signature verified, M4 `Error` checked), error mapping (470 → `TransientRefused` /
`CredentialsRejected`, TLV errors), and the identity/credential records. Also `HapAccessoryPairing` (internal, in
`src/Airplay2.Sender/Pairing`, scaffold stub): the accessory side of the same three procedures (SRP server with
configurable PIN, Ed25519 identity, controller registry, pair-verify, 470 switch), so the fake receiver and the
pairing tests share **one** implementation and the fake receiver's handlers are a thin adapter over it.

Contract: `IPairingClient`, `PairingSession`, `PairingCredentials`, `ControllerIdentity`, `PinPairingInProgress`,
`PairingException`, `HapAccessoryPairing`/`HapAccessoryOptions`/`HapAccessoryResponse` (scaffold).
Verify: unit tests running client and accessory against each other over an in-memory `IRtspConnection` pair (a
test double in `Airplay2.Sender.Tests` that pipes `RtspRequest`s to the accessory and back; this is why the package
needs no rtsp-transport) for all three procedures, including wrong PIN (TLV Error 2), 470 on transient, tampered M6
signature (must fail), unknown controller on pair-verify (`CredentialsRejected`), TLV fragmentation of the 384-byte
keys, header presence (`X-Apple-HKP`, `Content-Type`), that the control keys derived on both sides match and equal the
HKDF known answers for a fixed secret; once rtsp-transport has landed (same wave, integration at the wave end) the
same tests run over a loopback `RtspConnection` pair; live (allowed from the end of wave 1): transient M1-M4 against
the SpeakerA, then an encrypted `GET /info` (probes P2, P11; P6 method-byte comparison on two connections).

### `fake-receiver` (wave 2) — depends on: rtsp-transport, rtp-timing-control, pairing, crypto, alac — size: ~2,800

Delivers `FakeReceiver` (SPEC-control 4-6 receiver side as shairport-sync and airplay2-receiver do it): TCP control
port on loopback serving `GET /info` (recorded SpeakerA plist with name/`initialVolume` options and `txtAirPlay`),
pairing POSTs as a thin adapter over `HapAccessoryPairing` (no SRP/Ed25519/X25519 code of its own), encryption
switch after M4, `/auth-setup` answered with 200 + 1076 zero bytes (option `AnswerAuthSetup`), session SETUP
(`eventPort`, NTP accepted or 501 per option, PTP `timingPeerInfo`), event channel listener that sends `updateInfo`
on connect or after RECORD and `sendMediaRemoteCommand` on demand (option `LargeEventFrames` sends the `updateInfo`
request as one frame larger than 1024 bytes, to exercise the receive rule), RECORD (`Audio-Latency: 0`), SETPEERS,
stream SETUP (UDP data + control ports, optional `streamID`), SET_PARAMETER (volume parsed, DMAP parsed, artwork
stored, progress parsed), GET_PARAMETER volume in the shairport shape (`\r\nvolume: %.6f\r\n`) or the ap2r shape per
option, FLUSH, TEARDOWN both forms (`Connection: close` on the session form), feedback (`{streams:[...]}`), 30 s
event-answer timeout; UDP: decrypts RTP with `shk`, ALAC-decodes, records sequence gaps and sends 0xD5 requests
(both forms accepted back; option to send them from a second socket to exercise the address-first lookup), sends
0xD2 timing requests to the announced `timingPort` (300 ms x3 then every 3 s) and records replies, records 0xD4 and
0xD7 syncs. Every observation goes to `Events`.

Contract: `FakeReceiver`, `FakeReceiverOptions`, `FakeReceiverEvent` (scaffold; new options are additive).
Verify: self-tests driving it with hand-built requests through `RtspConnection` (pairing, SETUP, audio decode of the
reference ALAC packets sent as RTP), option switches (RefuseNtp, no streamID, 470 on transient, both GET_PARAMETER
shapes), and a smoke test that it survives a dropped connection. No live test.

### `session-streams` (wave 3) — depends on: pairing, rtsp-transport, fake-receiver — size: ~3,000

Delivers `ReceiverSession` (SPEC-control 3.10, 4-5, D5-D7, D10, D12, D17): `GetInfoAsync`, `PairAsync` (verify with
stored credentials → transient → PIN via prompt, with the 470/`CredentialsRejected` fallbacks and `IPairingStore`
updates), `AuthSetupAsync` (3.10, behind `ReceiverQuirks.SendAuthSetup`), `SetupSessionAsync` (NTP and PTP bodies,
`groupUUID`, `isMultiSelectAirPlay`, `ClockID` written as int64), `EventChannel` (reverse TCP with retries, swapped
keys, immediate 200 answers, parsed messages, frames up to 65 535 bytes, `Closed`), `RecordAsync`, `SetPeersAsync`,
`SetupStreamAsync` (type 96 body per SPEC-audio 1.1, `streamID` optional), volume set/get (`GetVolumeAsync` with the
leading-CRLF rule), `DmapEncoder`, artwork, progress, `FlushAsync`, `SetRateAnchorTimeAsync`/`FlushBufferedAsync`
(bodies only), `FeedbackAsync`, `TeardownAsync` (D7), and the `probe` command bodies for P1 (control part, decisive),
P3, P4, P5, P8, P9, P10, P13, P15 (control part), P21 as reusable methods.

Contract: `IReceiverSession`, `IEventChannel`, `ReceiverEventMessage`, the request/result records, `ReceiverQuirks`,
`DmapEncoder` (scaffold).
Verify: fake-receiver tests for the full start-up order (asserting the exact request sequence and bodies the fake
recorded), both start-up orders, the auth-setup quirk on/off, event channel timing options and large event frames,
feedback loop, teardown forms, pair-verify reuse after reconnect, PIN flow with the prompt, 470 fallback; DMAP bytes
against SPEC-control 5.2 (`mlit` with `minm/asar/asal`); live (allowed from this wave): pairing + session SETUP +
event channel + RECORD + stream SETUP + volume + TEARDOWN on the SpeakerA without sending audio, and the probes listed;
outcomes recorded in `SPEC.md` section 4. **Exit criterion of the wave: P1's control result is written into SPEC.md
and the default `TimingProtocol` for the SpeakerA is fixed** (quirk/default in `AirplaySenderOptions`).

### `audio-pipeline-group-sync` (wave 4) — depends on: session-streams, rtp-timing-control, alac — size: ~3,200

Delivers `RealtimeStreamTransport` (per-receiver `RtpAudioPacketBuilder` + `PacketHistory`, CONNECTED/STREAMING states,
initial `0x90` sync + marker packet on join/resync, periodic syncs every 44100 frames with per-receiver
`offset_frames`; **both sync flavours**: 0xD4 with NTP times when constructed without an `IPtpMaster`, 0xD7 with
`Clock.PtpNowNanos` and `Ptp.ClockId` when constructed with one, so the P1 outcome is a constructor argument, not a
code change), `Timeline` pacing, `GroupPlayer` (SPEC-audio 3, 7 and 8: PCM ring with high-water blocking `Write`,
pre-fill, pacer thread at 352-frame cadence from `Transport.Clock` with late-wakeup catch-up, silence on underrun,
silence timeout → pause, `AddReceiverAsync` running the D5 sequence through `IReceiverSession`, feedback/progress
timers, volume poll fallback, `GroupVolumeModel` with the G/r_i scaling and clamping, the metadata rtptime
bookkeeping of SPEC-audio 7 (`start`/`display`/`current`/`end`) behind `SetNowPlayingAsync`, event mapping from the
event channel to `GroupPlayerEvent`, pause/flush/resume re-anchoring per SPEC-audio 8.4, statistics), Windows timer
resolution (`timeBeginPeriod`/high-resolution waitable timer) and Linux `nanosleep` pacing. Depends on `IPtpMaster`
only as a contract; the fake receiver records 0xD7 syncs without a PTP slave.

Contract: `IStreamTransport` (incl. `Clock`), `IGroupPlayer`, `GroupPlayerOptions`, `GroupPlayerEvent`,
`GroupVolumeModel`, `Timeline` (scaffold).
Verify: fake-receiver tests with two fake receivers in one group: identical packets, per-receiver keys, sync packets
with correct `now_frame`/`next_frame` relation and cadence (both 0xD4 and 0xD7 with a stub `IPtpMaster`), join
mid-stream gets `0x90` + marker, flush/resume, 0xD5 answered from history, decoded PCM equals the input, pacer timing
within ±2 ms over 30 s, underrun inserts silence, `Write` blocks above high water, progress bodies match the SPEC-audio
7 formulas for a fresh start, a track switch and a mid-track join; volume model unit tests (scaling, clamp at 100,
receiver-reported change leaves others alone); live (allowed from this wave, 3 s quiet tone only, `--i-know`):
`airplay2 play tone:3 --to "Test Speaker ABCD" --level 15 --i-know` and the probes P1 (audio confirmation), P7, P12,
P14, P15, P18, P19.

Contract changes (recorded 2026-10-09 by audio-pipeline-group-sync; sender-api-cli and bridge-service build on them):
- `GroupPlayer(transport, sessionFactory, options, logger)`: the factory returns a **paired** `IReceiverSession`
  (GET /info and pairing are the factory's, as `AirplaySender.ConnectAsync` does them); `AddReceiverAsync` runs the
  rest of D5 and throws `InvalidOperationException` for an unpaired session. Join-time metadata is sent in the
  background (best effort), so `AddReceiverAsync` returns once the member is registered.
- Ownership: the player owns and disposes the transport; it never disposes the shared clock, timing responder,
  control port or PTP master (the host does).
- `GroupPlayerEvent.MemberStateChanged(id, Lost, "removed")` is also published by `RemoveReceiverAsync`; a member whose
  FLUSH did not complete is re-anchored on resume anyway (published as `Connected`).
- `IStreamTransport.Send(..., ntpNow)`: `ntpNow` is the instant the packet is **due** (the pacer's deadline, the sync's
  `T`), not the instant of the call.
- `Timeline.Skip` keeps the anchor and the sequence (sender-side stall recovery, SPEC-audio 8.3 note); `Timeline`
  keeps a 64-bit frame distance to its anchor, so deadlines stay monotonic past 2^32 frames.
- `GroupVolumeModel.Levels` is an immutable snapshot replaced on each change (safe to iterate on any thread).
- `IGroupPlayer.FlushAsync` does not re-send progress: after a seek the caller sends `SetNowPlayingAsync` with the new
  elapsed time. Pause/flush cancellation bounds only the wait for the player; the FLUSH fan-out always completes.
- New members: `GroupPlayerOptions.PrefillTimeout`, `StatisticsInterval`, `TeardownTimeout`, `StallThreshold`,
  `VolumeMinInterval`, `RandomStreamKey`, `GroupContainsGroupLeader`, `IsMultiSelectAirPlay`, `Sender` (hosts supply a
  stable `SenderIdentity`; the default is a test placeholder); `GroupPlayer.IsFaulted` (pacer thread failed: `Write`
  discards, `StartAsync` throws, the host rebuilds the player), `Stalls`, `FramesDiscarded`; `RealtimeStreamTransport`
  `AddTimingPeer`/`RemoveTimingPeer` (reference-counted PTP peers), `LatencyFrames`, statistics and probe switches.
- Under PTP the grandmaster peer comes from the receiver's `timingPeerInfo.Addresses` in the SETUP answer
  (`PtpSessionWiring.SelectPeerAddress`), falling back to the RTSP address.

### `ptp-master` (wave 4) — depends on: rtp-timing-control, session-streams (for the ShairportLive session) — size: ~1,600

Delivers `PtpMaster : IPtpMaster` (SPEC-audio 4.4: UDP 319/320, Announce/Sync/Follow_Up/Delay_Resp/Signaling with
the TLVs libairptp sends, unicast per peer, clock id `0xFFFF << 48 | seed`, Follow_Up timestamps from `IMasterClock`,
peer liveness), the fake receiver's minimal PTP slave (consumes Sync/Follow_Up, optionally sends Delay_Req) and the
`SETPEERS` wiring test. It is in wave 4 regardless of P1's outcome: the realtime-under-PTP path needs it if P1 fails,
the buffered stream (wave 5) and the shairport-sync tests need it in any case.

Contract: `IPtpMaster` (scaffold).
Verify: packet-layout unit tests against SPEC-audio 4.4 (every message type, TLVs, header flags); loopback test with
the fake slave: offset converges, Delay_Req answered, peer dropped after 15 s; ShairportLive tests inside the Docker
VM (`docker/shairport-sync/run-live-tests-in-vm.ps1`): PTP session SETUP accepted, `nqptp` follows our clock (its
log), 0xD7 syncs accepted by the realtime stream (`RealtimeStreamTransport` with this master, 3 s of silence packets
into the dummy backend); live on the SpeakerA per section 4 rules (P17 capture during a P1-PTP control probe).

### `sender-api-cli` (wave 5) — depends on: audio-pipeline-group-sync, ptp-master, mdns-browser, bridge-store (for the JSON pairing store) — size: ~1,800

Delivers `AirplaySender` (facade: shared clock/timing/control/PTP master, session factory with quirks, the default
timing protocol per receiver from the P1 result, `ConnectAsync`, `BeginPinPairingAsync`/`CompletePinPairingAsync`,
`CreateGroupPlayerAsync` choosing the transport), the CLI commands (`discover`, `info`, `pair`, `play` with a WAV
reader for 16-bit 44.1 kHz stereo and `tone:<s>`, `volume`, `probe <P1..P21> <name>` dispatching to the
session/pipeline probe methods), the **`--i-know` guard**: `play` and every audio-sending probe return exit code 2
without sending a packet unless the flag is given, `--level` defaults to 15, console logging and exit codes.

Contract: `AirplaySender`, `AirplaySenderOptions` (scaffold), CLI verbs and options as in `Program.cs` (scaffold:
`play ... --i-know`, `probe <id> <name> [--i-know]`).
Verify: fake-receiver end-to-end test through the facade (discover is mocked, play 2 s of a generated WAV to two fake
receivers, decoded output equals input); CLI argument parsing tests incl. the guard (no `--i-know` → exit 2, no
socket opened); live: Sender M1 from PLAN.md (play a WAV to one receiver) with the 3-second limit, Sender M2/M3 steps
with the user present.

### `buffered-stream` (wave 5, optional) — depends on: ptp-master, session-streams, rtp-timing-control — size: ~2,400

Delivers `BufferedStreamTransport` (TCP data connection, length-prefixed packets with the 23-bit sequence, SSRC
format code, `SETRATEANCHORTIME`/`FLUSHBUFFERED` driving, run-ahead flow control), with ALAC 44.1/16 first and the
format question P16 recorded.

Contract: `BufferedStreamTransport` (scaffold), `IStreamTransport`.
Verify: packet-layout unit tests against SPEC-audio 4.2; fake-receiver buffered reader; ShairportLive tests inside the
Docker VM: buffered stream plays into the dummy backend (the receiver's logs show the frames); live on the SpeakerA per
section 4 rules (P16).

Contract changes (recorded 2026-10-09 at the wave 5 checkpoint; all additive unless marked):
- buffered-stream: `BufferedStreamTransport` moved to its own file; besides the 3-argument constructor it has
  `(clock, ptp, control, BufferedStreamOptions?, ILogger?)`, `WrapSessionFactory`, `CreateGroupPlayer(factory,
  options, logger)` (wraps the factory and aligns `GroupPlayerOptions.LatencyFrames` with the transport's L),
  `AttachSession`/`DetachSession`, `AnchorAsync`, `PauseAsync`, `FlushAsync`/`FlushReceiverAsync`/`FlushRangeAsync`,
  `WaitForWindowAsync`, events and counters; new types `BufferedStreamOptions`, `BufferedReceiverSession` (the
  `IReceiverSession` decorator: PTP forced, peer add/remove, type-103 SETUP, FLUSH mapped to rate 0 + FLUSHBUFFERED),
  `BufferedAudioFormat`, `BufferedAudioPacket`, `AlacEscapeFrame`, `NetworkTime`, `BufferedStreamConstants`.
  New `IStreamLatency { LatencyFrames }`, implemented by both transports; `GroupPlayer` reads L through it.
- sender-api-cli: `AirplaySenderOptions` gains `DefaultTimingProtocol`, `Connection`, `Session`, `Ptp`, `LocalAddress`,
  `ConnectAttempts`, `ConnectRetryDelay`, `MaxRetryDelay`, `PinPairingTimeout`. `AirplaySender` gains `StartAsync`,
  `Timing`, `Control`, `Ptp`, `EnsurePtpMasterAsync`, `PendingPinPairings`, `QuirksFor`/static `ResolveQuirks`,
  `TimingProtocolFor`, `ChooseTransport`, static `DeriveIdentity`, static `InfoRequest`, and
  `CreateGroupPlayerAsync(receivers, options, ct)` (realtime/buffered and NTP/PTP from the members; a buffered player
  is built with `BufferedStreamTransport.CreateGroupPlayer`); the browser is created lazily. CLI: `Program.RunAsync`,
  `BuildRootCommand`, `CreateInvocationConfiguration` (`TeardownGrace` 60 s for Ctrl+C), `CliServices`,
  `IReceiverDirectory`, `IPcmSource`/`WavReader`; recursive `--verbose`/`--log-file`, `info --qualifier`,
  `pair --pin/--forget`, `play --no-metadata`, probe `--seconds/--level/--latency/--sync-flags/--withhold`; exit codes
  3 (probe unavailable: P16, P20) and 130 (interrupted). `tests/Airplay2.Sender.Tests` references the CLI project.

### `bridge-service` (wave 6) — depends on: sender-api-cli, bridge-store, fake-receiver — size: ~3,200

Delivers `SpotifyHost` (librespot `Session`/`Spirc`/`Player` with `AirplaySink`, Zeroconf discovery with the stored
device name, credential cache via `IBridgeStore`, reconnect loop as in `Librespot.Cli`, player events → group player
per SPEC-audio 8.4: pause/seek/skip → flush, track change → metadata/artwork/progress, Spotify volume → G, receiver
buttons → Spirc play/pause/next/prev), an `OutputManager` (*new*: owns the active `IGroupPlayer`, applies the selected
output from settings, reconnects receivers, persists levels), the `BridgeController` logic for every route, `EventHub`
(WebSocket fan-out, subscribe/ping), receiver inventory (browser + store merge), pairing flow with tokens, settings,
status, diagnostics (log ring buffer, ports, statistics), Windows file logging, OpenAPI in Development, the Dockerfile
build check (`docker build -f docker/Dockerfile .`), install scripts verified (the Windows script passes `-WebPort` to
the service as `--Kestrel:Endpoints:Http:Url`).

Contract: `ApiRoutes`, DTOs and `BridgeEvent`/`BridgeClientMessage` in `Bridge.Contracts` (scaffold; additive
changes allowed), `AirplaySink`, `IBridgeStore`, `SpotifyHost` (scaffold).
Verify: `WebApplicationFactory` tests for every endpoint against a temp database and fake receivers (connect a fake,
set volumes, create a group, select output, WebSocket receives the events), a sink test driving `AirplaySink` with
synthetic `AudioPacket.Samples` and checking the fake receivers decode it, a Spotify mapping test with a stubbed
player event stream (no Spotify login in CI); live: Bridge M1 from PLAN.md with the user present (real Spotify
handoff, 3-second audio rule lifted only with the user present).

### `web-app` (wave 7) — depends on: bridge-service — size: ~2,800

Delivers the Blazor pages: Receivers (list with state, connect/disconnect, pairing dialog for the PIN flow fed by
`PairingRequired` events, per-receiver slider, offset field), Groups (create/rename/delete, add/remove members, master
slider that shows the scaled r_i live, activate), Now Playing (track, artwork, Spotify volume), Settings (device name,
latency, stream preference, poll toggle, forget Spotify), Diagnostics (status, ports, statistics, live log, pending
probes); a `BridgeEventClient` (WebSocket with reconnect), a `BridgeState` container, responsive CSS.

Contract: consumes `Bridge.Contracts` only; no server changes except additive DTO fields recorded here.
Verify: xunit tests for `BridgeState` and the API client against a `WebApplicationFactory` of the service with fake
receivers; component tests with bUnit if a version compatible with .NET 10 is on NuGet at package start (check
`bunit` on api.nuget.org; otherwise test the state container and drive the pages by hand); manual check in a browser
against the service with two fake receivers (`bridge --Bridge:FakeReceivers=2`, a development switch added by
bridge-service).

### `final-integration` (wave 8) — depends on: everything — size: ~1,500 (mostly tests and docs)

Delivers the end-to-end runs with the user present (Sender M1-M3, Bridge M1, Web M1 from PLAN.md), the probe results
written into `SPEC.md` section 4 and the resulting quirk defaults, `README.md` for the repository (build, run, install,
container), the `docs/airplay2/TEST-RECEIVER.md` update with what shairport-sync actually exercised, a CI script
(`build.ps1`: build, test excluding live categories, publish win-x64 and the container), and a review of every
remaining `TODO(impl:` marker (there must be none).

## 7. Bridge API surface

REST (JSON, `Bridge.Contracts`), all under `/api`:

| Method | Route | Body → Response | Notes |
|---|---|---|---|
| GET | `/receivers` | → `ReceiverDto[]` | discovery merged with the store |
| GET | `/receivers/{id}` | → `ReceiverDto` | |
| POST | `/receivers/{id}/pair` | → `PairingStartedDto` | PIN flow: receiver shows its PIN |
| POST | `/receivers/{id}/pair/pin` | `PairingPinRequest` → `ReceiverDto` | finishes M3-M6 + verify, stores keys |
| DELETE | `/receivers/{id}/pairing` | → 204 | forget stored keys |
| POST | `/receivers/{id}/connect` | → `ReceiverDto` | single-receiver output; emits `PairingRequired` when a PIN is needed |
| POST | `/receivers/{id}/disconnect` | → `ReceiverDto` | |
| PUT | `/receivers/{id}/volume` | `VolumeRequest` → `ReceiverDto` | r_i |
| PUT | `/receivers/{id}/offset` | `OffsetRequest` → `ReceiverDto` | offset_frames |
| GET | `/groups` | → `GroupDto[]` | |
| POST | `/groups` | `GroupRequest` → `GroupDto` | |
| GET | `/groups/{id}` | → `GroupDto` | |
| PUT | `/groups/{id}` | `GroupRequest` → `GroupDto` | rename |
| DELETE | `/groups/{id}` | → 204 | |
| POST | `/groups/{id}/members` | `GroupMemberRequest` → `GroupDto` | |
| DELETE | `/groups/{id}/members/{receiverId}` | → `GroupDto` | |
| PUT | `/groups/{id}/volume` | `VolumeRequest` → `GroupDto` | G; scales members |
| POST | `/groups/{id}/activate` | → `OutputDto` | select as output |
| GET / PUT | `/output` | `OutputRequest` → `OutputDto` | selected output |
| GET | `/playback` | → `PlaybackDto` | |
| POST | `/playback` | `PlaybackCommandRequest` (`play`, `pause`, `next`, `prev`) → `PlaybackDto` | forwarded to Spirc |
| GET / PUT | `/settings` | `SettingsDto` | |
| GET | `/status` | → `StatusDto` | implemented in the scaffold |
| GET | `/diagnostics` | → `DiagnosticsDto` | |
| DELETE | `/spotify` | → 204 | forget cached credentials |

Errors: RFC 9457 problem details (`404` unknown id, `409` state conflicts such as "receiver busy", `422` validation,
`502` receiver errors with the RTSP status in `detail`).

WebSocket `/ws/events`: server → client `BridgeEvent` JSON with a `type` discriminator (`receiverChanged`,
`receiverRemoved`, `groupChanged`, `groupRemoved`, `outputChanged`, `playbackChanged`, `volumeChanged`,
`pairingRequired`, `statusChanged`, `log`); client → server `BridgeClientMessage` (`subscribe {log}`, `ping`). One
message per frame; the server sends a full `statusChanged` + current receivers/groups/output on connect.

## 8. SQLite schema

`src/Bridge.Service/Store/schema.sql` (embedded, applied by `SqliteBridgeStore.InitializeAsync`, `PRAGMA user_version = 1`):

| Table | Columns | PLAN.md mapping |
|---|---|---|
| `receivers` | `id` (deviceid), `name`, `model`, `manufacturer`, `last_address`, `last_port`, `features`, `status_flags`, `public_key`, `pairing_state`, `level`, `offset_frames`, `quirks` (JSON), `last_seen`, `created_at` | receivers (id, name, model, last address, features, pairing state) + per-device level |
| `pairings` | `receiver_id` (FK), `controller_id`, `controller_seed`, `accessory_id`, `accessory_public_key`, `created_at` | pairings (receiver, our keys, their long-term public key) |
| `groups` | `id` (UUID = groupUUID), `name`, `master_level`, `created_at` | groups (id, name) + G |
| `group_members` | `group_id`, `receiver_id`, `level`, `position` | group_members (group, receiver, level) |
| `settings` | `key`, `value` | settings (device name, selected output, latency, stream preference, controller identity) |
| `spotify_credentials` | `username`, `auth_type`, `auth_data`, `updated_at` | spotify_credentials |

## 9. Milestone mapping and revision record

| PLAN.md milestone | Packages |
|---|---|
| Sender M1 (discover, transient pairing, play WAV to one receiver) | mdns-browser, rtsp-transport, rtp-timing-control, pairing, fake-receiver, session-streams, audio-pipeline-group-sync, sender-api-cli (+ ptp-master if P1 chose PTP) |
| Sender M2 (PIN pairing, metadata/artwork/progress, volume, flush on seek) | pairing, session-streams, sender-api-cli (+ web-app for the PIN entry) |
| Sender M3 (groups, one clock master, group volume) | audio-pipeline-group-sync |
| Bridge M1 | bridge-store, bridge-service |
| Web M1 | web-app |
| Polish | buffered-stream, final-integration |

Revision 2026-10-09 (review): `pairing` moved from wave 2 to wave 1 with an in-memory connection double, so
`fake-receiver` (wave 2) can build on `HapAccessoryPairing` (now an internal class of `Airplay2.Sender/Pairing`,
owned by `pairing`) instead of a second accessory implementation; `ptp-buffered-stream` split into `ptp-master`
(wave 4, unconditional) and `buffered-stream` (wave 5, optional) so no package is "pulled forward" into a wave it
would share with its consumer, and `PtpSyncPacket.Write` moved to `rtp-timing-control`; the D1 gate is the
control-only P1 in wave 3; section 5 (ownership) added; `IStreamTransport.Clock`, `ulong` clock ids,
`IReceiverSession.AuthSetupAsync`, the `RtspRequest` Content-Type/Content-Length rule, the `IControlPort` lookup rule
and the CLI `--i-know` guard recorded as contract changes; `AIRPLAY_LIVE=1` wired into the VM script, which now uses
the artifacts output layout; cross-folder file owners stated; the Librespot.Core transitive dependency recorded.
