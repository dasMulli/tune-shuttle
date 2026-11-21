# Configuration and API

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
| `Bridge:LocalAddress` | empty (all interfaces) | Local address the sender's UDP sockets, the AirPlay receiver's RTSP listener and the PTP port bind to, for machines with several networks |
| `Bridge:ConnectTimeout` | `00:01:00` | Upper bound for connecting one receiver |
| `Bridge:ReconnectDelay` | `00:00:05` | First delay before a lost receiver is connected again; doubles per failure |
| `Bridge:MaxReconnectDelay` | `00:05:00` | Longest delay between two reconnect attempts |
| `Bridge:ResolveTimeout` | `00:00:03` | How long a connect waits for a receiver to show up in mDNS before it uses its last known address |
| `Bridge:FileLogging` | `true` | Daily log files under `<DataDirectory>/logs` (Windows only) |
| `Bridge:LogRetentionDays` | `7` | Days of log files to keep |
| `Bridge:SenderPorts:Timing`, `Bridge:SenderPorts:Control` | `0` | Reserved: the sender chooses its UDP ports itself, and a non-zero value only logs a warning |
| `Bridge:EnableSpotify` | `true` | `false` runs without Spotify Connect (API and receivers only) |
| `Bridge:BrowseReceivers` | `true` | `false` turns off receiver discovery |
| `Bridge:Airplay:Enabled` | `true` | Start the AirPlay receiver. Settings overrides it |
| `Bridge:Airplay:RtspPort` | `7000` | TCP port of the AirPlay receiver (the Apple device's control connection) |
| `Bridge:Airplay:PtpEventPort`, `Bridge:Airplay:PtpGeneralPort` | `319`, `320` | UDP ports of the PTP clock, shared by the receiver and the sender; when they cannot be bound the receiver plays with anchor-only timing |
| `Bridge:Airplay:FfmpegPath` | empty (the bridge's folder, then the library path) | Directory of the FFmpeg 7.1 libraries for AAC (see [Installing and running](INSTALL.md#the-ffmpeg-libraries)) |
| `Bridge:Airplay:AnchorLeadMs` | `300` | Without PTP: how far ahead the receiver schedules the first audio of a stream |
| `Bridge:Airplay:SessionTimeoutSeconds` | `30` | An Apple device silent this long during a stream is considered gone and the speakers are released |
| `Bridge:FakeReceivers` | `0` | Development only: N in-process fake receivers instead of the network (Debug builds from the repository) |

## API

The web app talks to the bridge through a REST API under `/api` and a WebSocket at `/ws/events`; other tools can use
them too. Requests and answers are JSON; errors are RFC 9457 problem details. The full route table is in
[`docs/ARCHITECTURE.md`](ARCHITECTURE.md#bridge-api); when the bridge runs in the Development environment
(`dotnet run`), the OpenAPI document is at `/openapi/v1.json`.

| Area | Routes |
|---|---|
| Receivers | `GET /api/receivers`, `GET /api/receivers/{id}`, `POST .../connect`, `POST .../disconnect`, `PUT .../volume`, `PUT .../offset`, `DELETE /api/receivers/{id}`, `DELETE /api/receivers?offline=true` |
| Pairing | `POST /api/receivers/{id}/pair` (the receiver shows its PIN), `POST .../pair/pin`, `DELETE .../pairing` |
| Groups | `GET`/`POST /api/groups`, `GET`/`PUT`/`DELETE /api/groups/{id}`, `POST .../members`, `DELETE .../members/{receiverId}`, `PUT .../volume`, `POST .../activate` |
| Output and playback | `GET`/`PUT /api/output`, `GET /api/playback`, `POST /api/playback` (`play`, `pause`, `next`, `prev`; `409` while AirPlay plays), `GET /api/playback/artwork` (the AirPlay sender's artwork) |
| Settings and status | `GET`/`PUT /api/settings` (with the AirPlay receiver's settings; the password is never returned), `GET /api/status` (with the active source and the receiver's state), `GET /api/diagnostics`, `DELETE /api/spotify` (forget the Spotify login) |

The WebSocket sends a snapshot on connect (status, receivers, groups, output, playback) and then one JSON event per
change (`receiverChanged`, `receiverRemoved`, `groupChanged`, `groupRemoved`, `outputChanged`, `playbackChanged`,
`volumeChanged`, `pairingRequired`, `statusChanged`); send `{"type":"subscribe","log":true}` for the live log and
`{"type":"ping"}` for a `pong`.
