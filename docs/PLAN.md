# Project plan: Spotify Connect to AirPlay 2 bridge

Status: agreed goals and decisions as of 2026-10-09. The librespot C# port (`librespot-dotnet/`) is complete and
verified end to end; everything below builds on it.

## Goals

1. **AirPlay 2 sender library** (`Airplay2.Sender`), usable on its own, with a CLI harness that can play a file to
   one receiver or a group without Spotify involved.
2. **Bridge service** that appears as a Spotify Connect device and forwards the audio to AirPlay 2 receivers.
   It always accepts playback; audio is silently discarded while no receiver is connected.
3. **Web app**, served by the service, for everything that needs a user: receiver discovery, connect and disconnect,
   pairing (transient where the receiver allows it, PIN flow otherwise), group management, and volume.

## Decisions

| Topic | Decision |
|---|---|
| Receivers per session | Single receivers **and** groups from v1. Groups are the point of the project. |
| AirPlay 1 fallback | Not needed. Nice to have for the standalone sender later. |
| No receiver connected | Discard audio silently. Local playback is not a goal. |
| User interaction | Only through the web app. The service never prompts on a console. |
| Persistence | SQLite (pairing keys, receivers, groups, per-device levels, Spotify credential cache, device name). |
| Deployment | Windows Service **and** a Linux container for Kubernetes. The pod gets a macvlan interface, so mDNS and inbound connections work without host networking tricks. |
| Lowest priority extras | The web app itself as an audio receiver (Web Audio over WebSocket). |

## Architecture

```
 Spotify app ──(Spotify Connect)──> librespot-dotnet (Session, Spirc, Player)
                                          │ PCM 44.1 kHz / 16-bit stereo via a custom Sink
                                          ▼
                                   Bridge service (ASP.NET Core generic host)
                                   ├─ AirplayOutput: sink → sender, group volume model, metadata forwarding
                                   ├─ Airplay2.Sender: discovery, pairing, RTSP, RTP/ALAC, timing, groups
                                   ├─ Store: SQLite via EF Core or Dapper
                                   └─ Web: REST + WebSocket API, static SPA
                                          │
                                          ▼
                                 AirPlay 2 receivers (one or a synchronized group)
```

Projects (all under `src/` next to `librespot-dotnet/`):

- `Airplay2.Sender` — the protocol library. No Spotify or ASP.NET dependencies.
- `Airplay2.Sender.Cli` — harness: `discover`, `pair`, `play <file.wav> --to <name> [--to <name>...]`, `volume`.
- `Bridge.Service` — the host: librespot session, output pipeline, store, API, web app hosting, Windows Service and
  systemd/container integration.
- `Bridge.Web` — the SPA (framework to be chosen when we get there; it only needs the REST/WebSocket API).
- `tests/` — unit tests per project, plus protocol tests against recorded traffic where possible.

### AirPlay 2 sender: scope and the stream type question

Components, in dependency order:

1. mDNS browse for `_airplay._tcp` and `_raop._tcp`, with TXT parsing (features bitmask, model, flags, pairing
   requirements). Reuse the responder's packet code from `Librespot.Discovery` for parsing.
2. HTTP/RTSP client over a raw socket (the control channel becomes encrypted after pairing, so a plain HttpClient
   cannot be used).
3. Pairing: `pair-setup` and `pair-verify` (SRP-6a, Curve25519, Ed25519, HKDF, ChaCha20-Poly1305). Transient
   pairing first; full HomeKit pairing with a PIN entered in the web app for receivers that require it. Keys persist
   in SQLite.
4. Encrypted control channel, `SETUP` (session and stream), `RECORD`, `SET_PARAMETER` (volume, metadata, artwork,
   progress), `FLUSH`/`FLUSHBUFFERED`, `TEARDOWN`, event channel.
5. Audio path: ALAC encoder (port of Apple's open-source encoder), RTP packetization, per-session AES keys.
6. Timing and groups: the sender is the clock master for every receiver in a group. Start with the realtime stream
   type (RAOP-style RTP, NTP-style timing, ALAC) because that is the path a third-party multi-room sender has
   already proven (OwnTone synchronizes AirPlay 2 receivers this way) and it needs no AAC encoder or PTP
   implementation. Keep the stream layer pluggable so the buffered stream type (PTP clock, larger receiver-side
   buffer, what Apple's own senders use for groups) can be added if realtime proves unreliable across receivers.
   Both have roughly two seconds of end-to-end latency; pause, seek and skip flush the receiver buffer.

### Volume model

Per-device level `r_i` (0..100) stored per receiver. A group has one master level `G`. Moving `G` scales every
`r_i` by `G'/G`, so the relative differences between rooms are preserved, as on Apple devices. Receivers that
reach 100 clamp. The Spotify app's volume slider drives `G` of the active group; the web app shows both `G` and the
individual `r_i`. Changes made on a receiver itself (its own buttons) are read back via the event channel and
update `r_i` without touching the others.

### Spotify side

- One librespot Spirc instance per **output**: initially one device, named by the user. Later, optionally one
  Spotify Connect device per receiver or group so the pick happens in the Spotify app.
- Spotify playback events map to AirPlay: pause/seek/skip → flush; track change → metadata and artwork; volume → `G`.
- Credentials from the Zeroconf handoff are cached in SQLite so the device comes back after a restart without a new
  handoff. Session reconnect follows the CLI's existing loop.

### Persistence (SQLite)

Tables: `receivers` (id, name, model, last address, features, pairing state), `pairings` (receiver, our keys, their
long-term public key), `groups` (id, name) and `group_members` (group, receiver, level), `settings` (Spotify device
name, selected output), `spotify_credentials`.

### Deployment

- .NET generic host with `UseWindowsService()` and `UseSystemd()`; configuration via `appsettings.json` and
  environment variables; the SQLite file lives in a configurable data directory (a mounted volume in the container).
- Container: Linux `net10.0` image, no NAudio dependency on that path (the output sink is the AirPlay sender). mDNS
  responder and browser bind per interface, which works on the macvlan pod interface.
- Windows: an install script that registers the service and adds firewall rules for mDNS (UDP 5353), the Zeroconf
  HTTP port, the web app port, and the sender's RTP/timing/control ports.
- Logging to console (container) and to files (Windows), with a diagnostics page in the web app.

## Milestones

1. **Sender M1**: discover receivers, transient pairing, play a WAV to one receiver from the CLI.
2. **Sender M2**: full HomeKit pairing with PIN, metadata/artwork/progress, volume, flush on seek.
3. **Sender M3**: groups: one clock master, N receivers in sync, group volume model.
4. **Bridge M1**: service hosts librespot with the AirPlay sink, SQLite store, REST/WebSocket API, Windows Service
   and container builds. Receiver selection via API only.
5. **Web M1**: discovery, connect/disconnect, pairing flows, groups, volumes, status and diagnostics.
6. **Polish**: per-receiver Spotify devices, browser receiver, buffered stream type if needed.

## Open questions

- Which receivers are available for testing (HomePod, Apple TV, Sonos, third-party). This decides which pairing
  paths and stream quirks get exercised first.
- Web app framework and whether a native tray app is wanted later (currently: web only).
