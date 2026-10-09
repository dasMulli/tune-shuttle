# airplay2-streamer

A Spotify Connect to AirPlay 2 bridge in C# (.NET 10). The bridge shows up in the Spotify app as a Connect device and
forwards what it plays to one AirPlay 2 receiver or to a synchronized group of them. Everything a user does
(discovering receivers, pairing, building groups, volumes) happens in a web app the bridge serves itself.

The repository holds three things:

| Part | Where | What it is |
|---|---|---|
| AirPlay 2 sender library | [`src/Airplay2.Sender`](src/Airplay2.Sender) | mDNS discovery, HAP pairing (transient and PIN), the encrypted RTSP control channel, the realtime stream (ALAC over RTP, NTP or PTP timing), the buffered stream, group playback with one master clock, the group volume model. No Spotify or ASP.NET code. |
| CLI harness | [`src/Airplay2.Sender.Cli`](src/Airplay2.Sender.Cli) (`airplay2`) | `discover`, `info`, `pair`, `play`, `volume`, `probe`: drive the sender without Spotify. |
| Bridge service and web app | [`src/Bridge.Service`](src/Bridge.Service) (`bridge`), [`src/Bridge.Web`](src/Bridge.Web) | ASP.NET Core host with librespot ([`librespot-dotnet/`](librespot-dotnet), a C# port of librespot), the AirPlay sink, a SQLite store, a REST/WebSocket API and a Blazor WebAssembly SPA. Runs as a Windows Service, under systemd or in a Linux container. |

Design and protocol documents: [`docs/PLAN.md`](docs/PLAN.md) (goals and decisions),
[`docs/airplay2/SPEC.md`](docs/airplay2/SPEC.md) (the protocol, the decisions D1-D17 and every live probe result),
[`docs/airplay2/WORK-PLAN.md`](docs/airplay2/WORK-PLAN.md) (packages, contracts, API, schema),
[`docs/airplay2/TEST-RECEIVER.md`](docs/airplay2/TEST-RECEIVER.md) (the shairport-sync test receiver) and
[`docs/airplay2/USER-VERIFICATION.md`](docs/airplay2/USER-VERIFICATION.md) (the checks that need a person in the room).

## Status

| Milestone (PLAN.md) | State |
|---|---|
| Sender M1: discover, transient pairing, play to one receiver | Done. Verified against a Test Speaker ABCD with a 3-second quiet tone (`airplay2 play tone:3`). |
| Sender M2: PIN pairing, metadata/artwork/progress, volume, flush on seek | Implemented and tested against the in-process fake receiver and shairport-sync (PIN pairing with `--password`). The SpeakerA answers DMAP text with `500`, so only artwork and progress reach it. PIN pairing on a real receiver that asks for a PIN is still unchecked. |
| Sender M3: groups, one clock master, group volume | Implemented and tested with two fake receivers. Not yet run with two real receivers. |
| Bridge M1: librespot + AirPlay sink, SQLite, API, service and container builds | Implemented. Connect, volume and disconnect were run against the SpeakerA without audio. A real Spotify handoff is still unchecked. |
| Web M1: discovery, pairing flows, groups, volumes, diagnostics | Implemented, with bUnit and API tests. |
| Polish: buffered stream | Works on shairport-sync. It does not play on the SpeakerA (`SETRATEANCHORTIME` is rejected, probe P16), so the realtime stream stays the default. |

The open items, and how to check them, are listed in [USER-VERIFICATION.md](docs/airplay2/USER-VERIFICATION.md).

## Requirements

- .NET SDK 10.0 (tested with 10.0.401). You do not need node or the `wasm-tools` workload; the Blazor app builds with the plain SDK.
- To run the bridge or CLI against real receivers, the machine must be on the same LAN as the receivers, and
  inbound UDP and mDNS must be allowed through its firewall (see "Firewall" below).
- Optional: Docker Desktop, to build the container image and for the shairport-sync test receiver.

## Build and test

```powershell
dotnet build AirplayBridge.sln
dotnet test AirplayBridge.sln --filter "Category!=SpeakerALive&Category!=ShairportLive"
```

Warnings are errors in this solution. The test filter shown above leaves out the live tests. Even when they are
selected, live tests return early unless `AIRPLAY_LIVE=1` is set, so a plain `dotnet test` never contacts a receiver.
The 30-second pacer accuracy test is tagged `Category=Timing`; add `&Category!=Timing` on a loaded machine. A live
test that returns early is reported as passed (xunit 2.9 has no dynamic skip); its output says why.

The CI script does the whole sequence:

```powershell
./build.ps1                      # Release build, tests without live categories, publish win-x64, container image
./build.ps1 -SkipContainer       # without Docker
./build.ps1 -SkipTests -SelfContained -SkipContainer   # only the self-contained Windows bundle
```

| Output | Path |
|---|---|
| Bridge (Windows, `bridge.exe` + web app) | `artifacts/bridge/` (the folder `install/windows/install-service.ps1` reads by default) |
| CLI (`airplay2.exe`) | `artifacts/airplay2/` |
| Test results (TRX) | `artifacts/test-results/` |
| Container image | `airplay2-bridge:dev` (`-ImageTag` to change) |

Parameters: `-Configuration Debug|Release`, `-Runtime` (default `win-x64`), `-SelfContained`, `-ImageTag`,
`-SkipBuild`, `-SkipTests`, `-SkipPublish`, `-SkipContainer`, `-RequireContainer`, `-ExcludeTimingTests`.

## The CLI

```powershell
dotnet run --project src/Airplay2.Sender.Cli -- discover
dotnet run --project src/Airplay2.Sender.Cli -- info "Test Speaker ABCD"
dotnet run --project src/Airplay2.Sender.Cli -- pair "Living Room"                  # PIN flow if the receiver asks for one
dotnet run --project src/Airplay2.Sender.Cli -- play music.wav --to "Kitchen" --to "Living Room" --level 30 --i-know
dotnet run --project src/Airplay2.Sender.Cli -- play tone:3 --to "Kitchen" --level 15 --i-know
dotnet run --project src/Airplay2.Sender.Cli -- volume "Kitchen" 25
```

| Command | Does |
|---|---|
| `discover [--seconds 5]` | Browses `_airplay._tcp`/`_raop._tcp` and prints every receiver with decoded features, pairing mode, stream types and timing protocol. |
| `info <name or IP> [--port 7000] [--qualifier]` | Unauthenticated `GET /info`. |
| `pair <name> [--pin 1234] [--forget]` | Pairs (transient check, or the PIN flow) and stores the keys in `airplay2-pairings.json` next to the executable. |
| `play <file.wav or tone:<s>> --to <name> [--to ...] [--level 15] [--latency 77175] [--no-metadata] --i-know` | Plays a 16-bit 44.1 kHz stereo WAV, or a quiet 440 Hz tone, to one receiver or to a group in sync. |
| `volume <name> [level]` | Reads or sets a receiver's volume. |
| `probe <P1..P21> <name> [--i-know]` | Runs one of the protocol probes in SPEC.md section 4 and appends its log to `artifacts/live/`. |

Global options: `--verbose` (Debug log on the console, including the decrypted wire exchange) and `--log-file <path>`.

**`--i-know` is required for any audio.** Without it, `play` and every probe that sends audio exit with code 2
before they open a socket. This guard exists because AirPlay wakes receivers and plays at their current volume. Exit
codes: 0 ok, 1 failure, 2 audio refused, 3 probe not available, 130 interrupted (after Ctrl+C the CLI still sends
TEARDOWN for up to 60 s).

## Running the bridge

### From the repository (development)

```powershell
dotnet run --project src/Bridge.Service                                  # http://localhost:8080
dotnet run --project src/Bridge.Service -- --Bridge:FakeReceivers=2      # two in-process fake receivers instead of the LAN
```

`dotnet run` uses `src/Bridge.Service/Properties/launchSettings.json`, which sets `ASPNETCORE_ENVIRONMENT=Development`
(OpenAPI document at `/openapi/v1.json`). The build output has no `wwwroot/`; the bridge finds the web app through the
static web assets manifest next to `bridge.dll` in every environment, so `dotnet bin/Debug/net10.0/bridge.dll` (which
runs as Production) serves it too. Published folders and the container carry the web app in `wwwroot/`.

`--Bridge:FakeReceivers=N` only works in Debug builds made from the full repository. It starts N fake receivers on
loopback, which is enough to try every page of the web app without a speaker.

Open `http://<host>:8080`. Then:

1. In the Spotify app on the same network, open the device picker and choose the bridge (default name "AirPlay
   Bridge"; you can rename it under Settings). The Zeroconf handoff logs the bridge in. The credentials are cached in
   SQLite, so after a restart the device comes back without a new handoff.
2. **Receivers** lists everything mDNS finds. *Connect* makes a receiver the output. A receiver that needs a PIN shows
   one on its screen, and the web app opens a PIN dialog for it.
3. **Groups**: create a group, add members, then *Activate* it. The master slider scales every member's level and
   keeps the differences between them, as Apple devices do. The Spotify volume slider drives that master level.
4. **Now Playing** shows the track, the artwork and the Spotify volume. **Settings** holds the device name, the sender
   latency L, the stream type preference and the volume poll. **Diagnostics** shows status, ports, statistics and a
   live log.

While no receiver is connected, the bridge accepts playback and discards the audio (PLAN.md).

### Configuration

The bridge reads, in increasing priority: `appsettings.json`, environment variables with the `BRIDGE_` prefix (`__` as
separator), and the command line (`--Key value`). Whatever the user changes in the web app is stored in SQLite and
overrides these defaults.

| Key | Default | Meaning |
|---|---|---|
| `DataDirectory` | `<app>/data` | `bridge.db`, `logs/`, `tmp/` |
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:8080` | Web app and API |
| `Bridge:DeviceName` | `AirPlay Bridge` | Spotify Connect name until set in the web app |
| `Bridge:LatencyFrames` | `77175` | Sender latency L (1.75 s; SPEC.md D1) |
| `Bridge:PreferredStream` | `Realtime` | `Buffered` only for receivers that play it (not the SpeakerA, probe P16) |
| `Bridge:PollReceiverVolumeSeconds` | `3` | `GET_PARAMETER volume` poll while playing; 0 turns it off |
| `Bridge:ZeroconfPort` | `0` (any) | Spotify Zeroconf HTTP port |
| `Bridge:SenderPorts:Timing` / `:Control` | `0` (any) | Fixed UDP ports for the NTP timing responder and the control port, for firewalls that need fixed ports |
| `Bridge:LocalAddress` | all interfaces | Address the sender's UDP sockets bind to |
| `Bridge:ConnectTimeout` | `00:01:00` | Upper bound for connecting one receiver |
| `Bridge:FileLogging`, `Bridge:LogRetentionDays` | `true`, `7` | Daily log files under `<DataDirectory>/logs` (Windows) |
| `Logging:LogLevel:Airplay2` | `Information` | `Debug` logs the decrypted RTSP exchange |

### As a Windows Service

```powershell
./build.ps1 -SkipTests -SkipContainer                  # or -SelfContained if the target has no .NET 10 runtime
# elevated PowerShell:
./install/windows/install-service.ps1 -WebPort 8080 -DataDir "$env:ProgramData\AirplayBridge"
./install/windows/install-service.ps1 -Uninstall
```

The script registers the service `AirplayBridge` (automatic start), passes the data directory and the web port on
its command line, and adds inbound firewall rules for the Private and Domain profiles: one per-program rule for
`bridge.exe`, plus UDP 5353 (mDNS) and the web port. The per-program rule matters because the timing, control and
Zeroconf ports are chosen at run time.

### On Linux with systemd

Publish for `linux-x64` (`./build.ps1 -Runtime linux-x64 -SkipContainer`) and copy the output to
`/opt/airplay-bridge`. Then install [`install/linux/airplay-bridge.service`](install/linux/airplay-bridge.service),
which uses `Type=notify` and `BRIDGE_DataDirectory=/var/lib/airplay-bridge`.

### In a container (Kubernetes)

```powershell
docker build -f docker/Dockerfile -t airplay2-bridge .
docker network create -d macvlan --subnet=192.0.2.0/24 --gateway=192.0.2.1 -o parent=eth0 lan
docker run -d --network lan --ip 192.0.2.60 -v bridge-data:/data airplay2-bridge
```

The image runs `bridge.dll` on the ASP.NET Core 10 runtime, keeps its data in `/data` (mount a volume there) and
listens on 8080. The pod needs an interface on the receivers' LAN: a macvlan interface (for example with Multus) gives
it working mDNS and inbound UDP without host networking. NAT-ed bridge networking does not work, because receivers
connect back to the sender (event channel, NTP timing requests, retransmit requests).

### Firewall

AirPlay receivers open connections back to the sender. Under NTP timing, which is what the SpeakerA uses, the receiver
sends timing requests to the sender's UDP timing port. If those requests are blocked, the receiver answers the stream
SETUP with `400` after about 30 s (SPEC.md section 4, "Sender M1"). The install script handles this for the service.
For the CLI, allow `airplay2.exe` inbound on the private profile, or run it with `dotnet run`:

```powershell
New-NetFirewallRule -DisplayName "airplay2 CLI" -Program "$PWD\artifacts\airplay2\airplay2.exe" -Direction Inbound -Action Allow -Profile Private
```

## Receivers

| Receiver | Status |
|---|---|
| Test Speaker ABCD (AirPlay 2, srcvers 366.0) | Live target. Transient pairing, realtime stream under NTP, wrapped retransmits, volume, artwork and progress all work. DMAP text is answered with `500`. The buffered stream is rejected. FLUSH and TEARDOWN answers can take 2-30 s on Wi-Fi. |
| shairport-sync 5.5.2 (Docker) | Test receiver for pairing (transient and password/PIN), PTP sessions, the realtime stream under PTP, and the buffered stream. It refuses NTP sessions. See [TEST-RECEIVER.md](docs/airplay2/TEST-RECEIVER.md). |
| In-process fake receiver | [`tests/Airplay2.FakeReceiver`](tests/Airplay2.FakeReceiver): the deterministic target of the unit and end-to-end tests. |
| Apple devices (HomePod, Apple TV), TVs, others | Not tested yet. A receiver that refuses NTP needs the PTP quirk (`ReceiverQuirks.TimingProtocol`). |

The per-model defaults that came out of the live probes are in `src/Airplay2.Sender/Session/KnownReceiverQuirks.cs`;
SPEC.md section 4 explains each of them.

## Tests against live receivers

Live tests carry `[Trait("Category", "SpeakerALive")]` or `[Trait("Category", "ShairportLive")]` and run only with
`AIRPLAY_LIVE=1`. Tests that send audio additionally need `AIRPLAY_AUDIO_I_KNOW=1` and play only a 3-second quiet
tone.

```powershell
$env:AIRPLAY_LIVE=1; dotnet test --filter Category=SpeakerALive                      # control-plane tests, no audio
$env:AIRPLAY_LIVE=1; $env:AIRPLAY_AUDIO_I_KNOW=1; dotnet test --filter "FullyQualifiedName~SpeakerA_sender_M1"
./docker/shairport-sync/run-live-tests-in-vm.ps1 -Filter "Category=ShairportLive&FullyQualifiedName!~PtpLiveTests&FullyQualifiedName!~BufferedLiveTests"
./docker/shairport-sync/run-ptp-live-tests-in-vm.ps1                                # PtpLiveTests
./docker/shairport-sync/run-ptp-live-tests-in-vm.ps1 -Filter "FullyQualifiedName~BufferedLiveTests&Category=ShairportLive"
```

Each of those runs writes its decrypted wire log to `artifacts/live/`, which git ignores.

## Licences

There is no licence file at the repository root yet. [`librespot-dotnet/`](librespot-dotnet) is a port of librespot
and carries its MIT licence ([`librespot-dotnet/LICENSE`](librespot-dotnet/LICENSE)). The ALAC codec in
`src/Airplay2.Sender/Alac` is a port of Apple's ALAC (Apache-2.0), and the ported files keep that notice.
owntone-server (GPL-2.0) was read only to understand how it synchronizes receivers; none of its code was copied. The
reference checkouts and their licences are listed in [`docs/REFERENCE-SOURCES.md`](docs/REFERENCE-SOURCES.md).
