# TuneShuttle

TuneShuttle is a Spotify Connect to AirPlay 2 bridge. It shows up in the Spotify app as a Connect device and plays
what you send it on one AirPlay 2 speaker or on a group of them in sync. Everything else (finding speakers, pairing,
building groups, volumes) happens in a web app that TuneShuttle serves itself. It is written in C# (.NET 10) and runs
as a Windows Service, under systemd on Linux, or in a container on Kubernetes.

Before 2026-10-10 the product was called "AirPlay Bridge" (Windows service `AirplayBridge`, container image
`airplay2-bridge`); older logs and installs still show that name.

The repository ([github.com/dasMulli/tune-shuttle](https://github.com/dasMulli/tune-shuttle)) also holds the parts it
is built from, usable on their own:

| Part | Where | What it is |
|---|---|---|
| AirPlay 2 sender library | [`src/Airplay2.Sender`](src/Airplay2.Sender) | Discovery, HomeKit pairing (transient and PIN), the encrypted control channel, the realtime stream (ALAC over RTP, NTP or PTP timing), the buffered stream, synchronized group playback and the group volume model. No Spotify or ASP.NET code. |
| `airplay2` CLI | [`src/Airplay2.Sender.Cli`](src/Airplay2.Sender.Cli) | Discover receivers, pair, play a WAV file to one receiver or a group, set the volume, run protocol probes. |
| Bridge and web app | [`src/Bridge.Service`](src/Bridge.Service), [`src/Bridge.Web`](src/Bridge.Web) | The `bridge` service: Spotify Connect, the AirPlay output, a SQLite store, a REST/WebSocket API and the web app. |
| librespot-dotnet | [`librespot-dotnet/`](librespot-dotnet) | A C# port of librespot, the Spotify Connect client library ([its README](librespot-dotnet/README.md)). |

How it fits together: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md). Working on the code:
[`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md). All documents: [`docs/README.md`](docs/README.md).

## Requirements

- A **Spotify Premium** account (librespot, like every third-party Connect client, works only with Premium).
- One or more **AirPlay 2** receivers. AirPlay 1 is not supported. See "Receivers" for what has been tested.
- A machine on the **same network** as the receivers and the phones running Spotify, with inbound UDP and mDNS
  allowed through its firewall (see "Firewall and network"). Windows (x64) or Linux (x64 or arm64).
- To build: the .NET SDK 10.0. To run a framework-dependent build: the ASP.NET Core 10 runtime (a self-contained
  build needs nothing). Docker for the container image.

## Installing and running

Build the bundle from the repository root first:

```powershell
./build.ps1 -SkipTests -SkipContainer                 # framework-dependent, win-x64, into artifacts/bridge
./build.ps1 -SkipTests -SkipContainer -SelfContained  # if the target machine has no .NET 10 runtime
```

Then open `http://<host>:8080` in a browser. To just try it from a checkout: `dotnet run --project src/Bridge.Service`.

### As a Windows Service

```powershell
# elevated PowerShell, after ./build.ps1:
./install/windows/install-service.ps1 -WebPort 8080 -DataDir "$env:ProgramData\TuneShuttle"
./install/windows/install-service.ps1 -Uninstall
```

The script registers the service `TuneShuttle` (automatic start) from `artifacts/bridge` (`-PublishDir` to change),
passes the data directory and the web port on its command line, and adds inbound firewall rules for the Private and
Domain profiles: one for `bridge.exe` (it covers the ports chosen at run time), UDP 5353 for mDNS, and the web port.

An install from before the rename is removed with its firewall rules. Its data stays in
`%ProgramData%\AirplayBridge`: pass that folder as `-DataDir` (or move it) to keep receivers, groups, pairings and the
Spotify login.

### On Linux with systemd

```sh
dotnet publish src/Bridge.Service -c Release -r linux-x64 -o out     # or linux-arm64; add --self-contained without a .NET runtime
sudo useradd --system --home-dir /var/lib/tune-shuttle tune-shuttle
sudo mkdir -p /opt/tune-shuttle /var/lib/tune-shuttle && sudo cp -r out/. /opt/tune-shuttle/
sudo chown tune-shuttle /var/lib/tune-shuttle
sudo cp install/linux/tune-shuttle.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now tune-shuttle
```

The unit ([`install/linux/tune-shuttle.service`](install/linux/tune-shuttle.service)) runs `/opt/tune-shuttle/bridge` as
the user `tune-shuttle` with `Type=notify`, keeps the data in `/var/lib/tune-shuttle` and listens on port 8080. Logs go
to the journal (`journalctl -u tune-shuttle`).

### In a container (Kubernetes)

```sh
docker build -f docker/Dockerfile -t tune-shuttle .
docker network create -d macvlan --subnet=192.0.2.0/24 --gateway=192.0.2.1 -o parent=eth0 lan
docker run -d --network lan --ip 192.0.2.60 -v bridge-data:/data tune-shuttle
```

The image runs `bridge.dll` on the ASP.NET Core 10 runtime, keeps its data in `/data` (mount a volume there) and
listens on 8080. It builds for `amd64` and `arm64` (`docker buildx build --platform linux/arm64 ...`, see
[`docs/DEVELOPMENT.md`](docs/DEVELOPMENT.md#container-image)).

**The container needs its own address on the receivers' LAN.** Give it a macvlan interface (in Kubernetes for
example with Multus); that makes mDNS and inbound UDP work without host networking. A NAT-ed bridge network does not
work, because receivers send UDP back to the bridge (NTP timing requests, retransmit requests, PTP) and Spotify must
reach the Zeroconf port.

## Using the web app

The web app works on phones, tablets and desktops. Its pages: **Player**, **Receivers**, **Groups** and **Settings**
(with **Diagnostics**).

**Spotify.** In the Spotify app on the same network, open the device picker and choose "TuneShuttle" (rename it under
Settings). The first time, this hands the login over to the bridge (Spotify's Zeroconf handoff). The login is cached,
so after a restart the device is back without another handoff; *Forget Spotify login* under Settings removes it.
Spotify's play, pause, skip, seek and volume act on the speakers, and the Player page shows the track and has its own
transport buttons. While no speaker is selected, the bridge accepts playback and discards the audio.

**Receivers** lists every AirPlay receiver the bridge finds on the network, with its state, its level and, under
*Details*, its model, address and a latency offset (in frames; use it when one speaker plays ahead of or behind the
others). *Connect* makes a receiver the output on its own; *Disconnect* stops it.

**Pairing.** Most speakers pair without any input (transient pairing). A receiver that requires a PIN shows "PIN
needed": press *Pair with PIN* (or *Connect*), the speaker shows a 4-digit PIN, and the web app opens a dialog to enter
it. The pairing is stored, so later connects need no PIN; *Forget pairing* removes it. A wrong PIN can be retried with
*Start again*.

**Forgetting receivers.** An offline receiver says when it was last seen. *Forget* removes it with its pairing, group
memberships and settings (after a confirmation), and *Forget all offline receivers* does that for every offline one.
Online receivers cannot be forgotten, because discovery would bring them back within seconds. Offline receivers that
carry nothing of yours (no pairing, no group, not the output, default level and offset) are forgotten automatically
after 3 days; Settings changes that (0 = never).

**Player and output.** The Player page shows the track, the transport buttons, the master volume and the **Output**
card, where you pick what plays: one receiver, a group, or nothing (*Stop output*).

**Groups.** A group plays to several receivers in sync. Create one (*New group*), add members, set each member's level,
then *Activate* it. The group has a master level G and each member a level r_i. Moving the master scales every member
by the same factor, so the balance between rooms stays as you set it (like Apple devices); a member that reaches 100
stays there, and the balance comes back when you turn down again. The Spotify volume slider drives the master level.
Changing a member's level, in the web app or on the speaker itself, changes only that member.

**Settings** holds the Spotify device name, the sender latency L (how far ahead the receivers get the audio; default
77 175 frames = 1.75 s), the stream type (Realtime, or Buffered for receivers that support it), the receiver volume
poll, the "forget offline receivers after" days, and **Appearance**: the theme (light, dark or system) and the language
(Deutsch, English, Français, Español, Русский, Українська, Português (Brasil); the browser's language until you choose
one). The theme and the language are stored in the browser; the top bar has a theme picker and, on wider screens, a
language select. **Diagnostics** shows the status, the bridge's addresses and ports, packet statistics and a live log.

## The CLI

`airplay2` drives the sender library without Spotify (published by `build.ps1` to `artifacts/airplay2/`, or run with
`dotnet run --project src/Airplay2.Sender.Cli -- <command>`):

```powershell
airplay2 discover
airplay2 info "Kitchen"
airplay2 pair "Living Room"                          # the PIN flow if the receiver asks for one
airplay2 play music.wav --to "Kitchen" --to "Living Room" --level 30 --i-know
airplay2 play tone:3 --to "Kitchen" --level 15 --i-know
airplay2 volume "Kitchen" 25
```

| Command | Does |
|---|---|
| `discover [--seconds 5]` | Browses `_airplay._tcp` and `_raop._tcp` and prints every receiver with its decoded features, pairing mode, stream types and timing protocol. |
| `info <name or IP> [--port 7000] [--qualifier]` | Unauthenticated `GET /info`. |
| `pair <name> [--pin 1234] [--forget]` | Pairs (transient check, or the PIN flow; asks for the PIN when `--pin` is missing) and stores the keys in `airplay2-pairings.json` next to the executable; `--forget` deletes them. |
| `play <file.wav or tone:<s>> --to <name> [--to ...] [--level 15] [--latency 77175] [--no-metadata] --i-know` | Plays a 16-bit 44.1 kHz stereo WAV, or a quiet 440 Hz tone of `<s>` seconds, to one receiver or to several in sync. |
| `volume <name> [level]` | Reads or sets a receiver's volume. |
| `probe <P1..P21> <name> [--i-know] [--seconds] [--level] [--latency] [--sync-flags] [--withhold]` | Runs one of the protocol probes of [`docs/airplay2/SPEC.md`](docs/airplay2/SPEC.md) section 4 and appends its log to `artifacts/live/`. |

Global options: `--verbose` / `-v` (Debug log on the console, including the decrypted wire exchange) and
`--log-file <path>`.

**`--i-know` is required for any audio.** Without it, `play` and every probe that sends audio exit with code 2 before
they open a socket, because AirPlay wakes receivers and plays at their current volume. Exit codes: 0 ok, 1 failure,
2 audio refused, 3 probe not available, 130 interrupted (after Ctrl+C the CLI still sends `TEARDOWN`, for up to 60 s).

## Configuration

The bridge reads, in increasing priority: `appsettings.json` next to the executable, environment variables with the
`BRIDGE_` prefix (`__` as the separator, e.g. `BRIDGE_Bridge__DeviceName`), and the command line (`--Bridge:DeviceName
Kitchen`). What you change in the web app is stored in the database and wins over all three.

| Key | Default | Meaning |
|---|---|---|
| `DataDirectory` | `<app>/data` | Holds `bridge.db`, `logs/` and `tmp/` |
| `Kestrel:Endpoints:Http:Url` | `http://0.0.0.0:8080` | Address and port of the web app and the API |
| `Logging:LogLevel:Airplay2` | `Information` | `Debug` logs the decrypted RTSP exchange; `Logging:LogLevel:Librespot` likewise for Spotify |
| `Bridge:DeviceName` | `TuneShuttle` | Spotify Connect device name. Settings overrides it |
| `Bridge:LatencyFrames` | `77175` | Sender latency L in frames (1.75 s at 44.1 kHz); Settings accepts 22 050-176 400. Settings overrides it |
| `Bridge:PreferredStream` | `Realtime` | `Realtime` or `Buffered`; buffered is used only for receivers that support it and does not play on the srcvers-366 speaker. Settings overrides it |
| `Bridge:PollReceiverVolumeSeconds` | `3` | Seconds between `GET_PARAMETER volume` polls while playing, which pick up level changes made on a speaker; 0 turns the poll off. Settings switches the poll on or off (at least every second) |
| `Bridge:ForgetOfflineAfter` | `3.00:00:00` | Offline receivers without user state (no pairing, no group, not the selected output, default level and offset) are forgotten once not seen for this long; checked a minute after start-up, then hourly; `00:00:00` never forgets. Settings overrides it (in days) |
| `Bridge:ZeroconfPort` | `0` (any free port) | TCP port of the Spotify Connect Zeroconf server; set it to open a fixed port in a firewall |
| `Bridge:LocalAddress` | empty (all interfaces) | Local address the sender's UDP sockets bind to, for machines with several networks |
| `Bridge:ConnectTimeout` | `00:01:00` | Upper bound for connecting one receiver |
| `Bridge:ReconnectDelay` | `00:00:05` | First delay before a lost receiver is connected again; doubles per failure |
| `Bridge:MaxReconnectDelay` | `00:05:00` | Longest delay between two reconnect attempts |
| `Bridge:ResolveTimeout` | `00:00:03` | How long a connect waits for a receiver to show up in mDNS before it uses its last known address |
| `Bridge:FileLogging` | `true` | Daily log files under `<DataDirectory>/logs` (Windows only) |
| `Bridge:LogRetentionDays` | `7` | Days of log files to keep |
| `Bridge:SenderPorts:Timing`, `Bridge:SenderPorts:Control` | `0` | Reserved: the sender chooses its UDP ports itself, and a non-zero value only logs a warning |
| `Bridge:EnableSpotify` | `true` | `false` runs without Spotify Connect (API and receivers only) |
| `Bridge:BrowseReceivers` | `true` | `false` turns off receiver discovery |
| `Bridge:FakeReceivers` | `0` | Development only: N in-process fake receivers instead of the network (Debug builds from the repository) |

## API

The web app talks to the bridge through a REST API under `/api` and a WebSocket at `/ws/events`; other tools can use
them too. Requests and answers are JSON; errors are RFC 9457 problem details. The full route table is in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#bridge-api); when the bridge runs in the Development environment
(`dotnet run`), the OpenAPI document is at `/openapi/v1.json`.

| Area | Routes |
|---|---|
| Receivers | `GET /api/receivers`, `GET /api/receivers/{id}`, `POST .../connect`, `POST .../disconnect`, `PUT .../volume`, `PUT .../offset`, `DELETE /api/receivers/{id}`, `DELETE /api/receivers?offline=true` |
| Pairing | `POST /api/receivers/{id}/pair` (the receiver shows its PIN), `POST .../pair/pin`, `DELETE .../pairing` |
| Groups | `GET`/`POST /api/groups`, `GET`/`PUT`/`DELETE /api/groups/{id}`, `POST .../members`, `DELETE .../members/{receiverId}`, `PUT .../volume`, `POST .../activate` |
| Output and playback | `GET`/`PUT /api/output`, `GET /api/playback`, `POST /api/playback` (`play`, `pause`, `next`, `prev`) |
| Settings and status | `GET`/`PUT /api/settings`, `GET /api/status`, `GET /api/diagnostics`, `DELETE /api/spotify` (forget the Spotify login) |

The WebSocket sends a snapshot on connect (status, receivers, groups, output, playback) and then one JSON event per
change (`receiverChanged`, `receiverRemoved`, `groupChanged`, `groupRemoved`, `outputChanged`, `playbackChanged`,
`volumeChanged`, `pairingRequired`, `statusChanged`); send `{"type":"subscribe","log":true}` for the live log and
`{"type":"ping"}` for a `pong`.

## Firewall and network

- **Same network.** Spotify finds the bridge, and the bridge finds the receivers, by mDNS (UDP 5353, multicast). Both
  need the same LAN or an mDNS reflector between VLANs. A receiver the bridge has seen before is reached at its last
  address even when mDNS is blocked.
- **Inbound UDP.** Receivers send NTP timing requests and retransmit requests to the bridge on UDP ports chosen at
  run time, and PTP receivers talk to its PTP clock on UDP 319/320. If the timing requests are blocked, an NTP
  receiver such as a third-party AirPlay 2 speaker (srcvers 366.0, features 0x445F8A00,0x1C340) answers the stream setup with `400` after about 30 s. The Windows install script adds a
  rule for `bridge.exe` that covers these ports; on Linux, allow the bridge's inbound UDP from the LAN.
- **Spotify Zeroconf** needs its HTTP port reachable from the phones: any free port by default, fixed with
  `Bridge:ZeroconfPort`.
- **The web port** (8080 by default) for the browser.
- **The CLI** needs the same inbound UDP. On Windows, allow `airplay2.exe` inbound on the private profile (or run it
  with `dotnet run`):

  ```powershell
  New-NetFirewallRule -DisplayName "airplay2 CLI" -Program "$PWD\artifacts\airplay2\airplay2.exe" -Direction Inbound -Action Allow -Profile Private
  ```

- **Containers** need a macvlan interface (see "In a container"); NAT does not work.

## Known receiver quirks

Receivers are told apart by what their AirPlay stack advertises in the mDNS TXT record (`srcvers`, `features`), not by
product name. The rules live in `src/Airplay2.Sender/Session/KnownReceiverQuirks.cs`: keyed on TXT `srcvers` (`model`
only for shairport-sync), first match wins; anything else takes the defaults.

| Receiver (TXT) | Status |
|---|---|
| shairport-sync 5.5.2 (`model=ShairportSync`, also `srcvers=366.0`) | Test receiver for transient and password/PIN pairing, PTP sessions, the realtime stream under PTP and the buffered stream. It refuses NTP sessions, so it always runs under PTP. |
| `srcvers=366.0`, `features=0x445F8A00,0x1C340` (receiver A, a third-party speaker) | The live target. Transient pairing, the realtime stream under NTP, retransmits, volume, artwork and progress work. Quirks below. |
| Apple devices (HomePod, Apple TV), TVs, others | Not tested yet. A receiver that refuses NTP needs PTP; add a rule to `KnownReceiverQuirks.cs`. |

Quirks of receiver A (`srcvers=366.0`):

- **No track titles.** It answers the DMAP text metadata with `500`, sometimes only after about 10 s. The bridge sends
  the title once per session and then stops; artwork and progress are accepted.
- **Slow flushes.** `FLUSH` (pause, seek, skip) and `TEARDOWN` answers take 2-30 s on Wi-Fi, sometimes longer, so a
  seek or skip can take that long before the new audio is heard. A `TEARDOWN` that is not answered within 10 s is
  given up and the connection closed.
- **No buffered stream.** It accepts the buffered stream's setup but rejects playback (`SETRATEANCHORTIME` `400`), so
  keep the stream type at Realtime.
- **No volume reports.** It does not report level changes made with its own buttons; the volume poll picks them up.

How each of these was found: [`docs/airplay2/SPEC.md`](docs/airplay2/SPEC.md) section 4.

## Troubleshooting

| Symptom | What to check |
|---|---|
| The device does not appear in Spotify | The phone and the bridge are on the same network; UDP 5353 inbound is allowed; the Zeroconf port is reachable (fix it with `Bridge:ZeroconfPort` and open it). Diagnostics shows whether Spotify is connected. |
| A receiver is not listed | The speaker is on and in the same network; mDNS is not blocked between them. `airplay2 discover` on the same machine shows what mDNS sees. |
| *Connect* fails after about 30 s with `400` | Inbound UDP to the bridge is blocked (NTP timing requests). Add the firewall rule (see "Firewall and network"). |
| *Connect* takes long or times out | Slow Wi-Fi receivers can need tens of seconds; `Bridge:ConnectTimeout` (1 minute) is the limit. |
| Seeking or skipping is slow | The receiver answers `FLUSH` slowly (receiver A takes 2-30 s); nothing to fix on the bridge. |
| No title on the receiver | It rejects DMAP text (receiver A does); artwork and progress still go out. |
| Nothing plays with the Buffered stream type | Switch Settings back to Realtime; not every receiver plays the buffered stream. |
| The rooms of a group are out of step | Set a per-receiver offset under Receivers > Details. |
| "Bridge:FakeReceivers is set, but this build has no fake receiver" in the log | Fake receivers exist only in Debug builds from the repository. |

The logs: the Diagnostics page (live), `<DataDirectory>/logs` on Windows, `journalctl -u tune-shuttle` under systemd,
`docker logs` in a container. `Logging:LogLevel:Airplay2=Debug` adds the decrypted protocol exchange.

## Licences

There is no licence file at the repository root yet. [`librespot-dotnet/`](librespot-dotnet) is a port of librespot
and carries its MIT licence ([`librespot-dotnet/LICENSE`](librespot-dotnet/LICENSE)). The ALAC codec in
`src/Airplay2.Sender/Alac` is a port of Apple's ALAC (Apache-2.0), and the ported files keep that notice.
owntone-server (GPL-2.0) was read only to understand how it synchronizes receivers; none of its code was copied. The
reference checkouts and their licences are listed in [`docs/REFERENCE-SOURCES.md`](docs/REFERENCE-SOURCES.md).
