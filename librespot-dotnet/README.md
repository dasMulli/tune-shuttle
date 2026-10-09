# librespot-dotnet

A clean, idiomatic C# (.NET 10) port of [librespot](https://github.com/librespot-org/librespot), the
open-source Spotify client library with Spotify Connect support. The port tracks upstream commit
`e023adbbf017ae1fc10d01531dbe50c409786f2d` (v0.8.0); the Rust reference lives in
`../reference/librespot` (see `../docs/REFERENCE-SOURCES.md`).

Like upstream, this only works with Spotify Premium accounts.

## Status

**Port complete.** Every upstream crate is ported file for file: each of the 134 `.rs` files in the reference
has a C# counterpart or a documented platform skip, and no `TODO(port: ...)` markers are left.

| Area | State |
|------|-------|
| `Librespot.Protocol` | All 46 upstream `.proto` files compiled with protoc at build time; `impl_trait` conversions ported. |
| `Librespot.OAuth` | Authorization-code + PKCE flow with the local redirect server, device-code flow, sync and async variants, token refresh. |
| `Librespot.Core` | AP resolve, handshake, Shannon codec, connection/authentication, Mercury, channels, audio keys, dealer (websocket), spclient, login5, token provider, client token, HTTP client with proxy support, `Session`, credentials cache. |
| `Librespot.Audio` | CDN URL resolution, range-based streaming fetch with read-ahead and throughput/ping tracking, AES decryption. |
| `Librespot.Metadata` | All metadata types (album, artist, track, episode, show, playlist, lyrics, ...) and their protobuf/JSON mappings. |
| `Librespot.Playback` | Decoders (Vorbis, MP3, passthrough), converter with dithering, normaliser, player loop, mixers (softvol), local files, and the `naudio`, `pipe` and `subprocess` sinks. |
| `Librespot.Connect` | Spirc / Connect state machine, context resolution, shuffle/repeat, volume and transfer handling. |
| `Librespot.Discovery` | Zeroconf HTTP server (getInfo/addUser with the upstream DH + AES/HMAC blob) and a built-in mDNS responder. |
| `Librespot.Cli` | The `librespot` binary with all 53 upstream options (same names, order, defaults and help texts), `--version`, `--backend ?`, `--device ?`. |
| `examples/` | The upstream examples as 10 sub-commands: `get-creds`, `get-token`, `play`, `play-connect`, `playlist-tracks`, `discovery`, `discovery-group`, `device-auth`, `oauth-async`, `oauth-sync`. |

Not ported, by design:

* the Linux/native-only audio backends `alsa`, `pulseaudio`, `jackaudio`, `gstreamer`, `portaudio`, `sdl`, the
  `rodiojack` variant and the `alsamixer` mixer (they stay unknown names, as in an upstream build without those features);
* the `avahi` and `dns-sd` discovery backends (their registry entries stay known-but-unavailable);
* FLAC decoding, which upstream only uses for local files;
* `util.rs`' `keep_flushing`, which is not needed with `ClientWebSocket`.

Verified end to end on 2026-10-09: the CLI in discovery mode was picked from the Spotify app, logged in via the
Zeroconf credential handoff, and played through WASAPI, with pause, volume, seek, skip, shuffle, repeat and transfer
all handled.

Known issue:

* `VorbisDecoder` asks NVorbis for `TotalSamples` on every seek and on load. NVorbis answers that by reading every
  remaining Ogg page of the stream (verified with a counting stream), which upstream's symphonia avoids by reading only
  the last page. On a streamed track in random-access mode this turns into one 64 KiB CDN request per page run, so a
  burst of rapid track changes can exhaust the local 300-requests-per-30-s rate limiter for `scdn.co`; the fetch task
  then dies and the load times out ("wait timeout exceeded"), and the player skips to the next track. Fix: find the
  final granule position by reading the last Ogg page (the parser in `Decoder/Ogg.cs` can do this) instead of calling
  `TotalSamples`, and only touch NVorbis' page index lazily.

Tests: `dotnet test Librespot.sln` runs 819 tests, 809 pass and 10 are skipped (tests that need the network or an
audio device). Set `LIBRESPOT_LIVE_TESTS=1` (plus `LIBRESPOT_ACCESS_TOKEN` for the Spotify ones) to run the live tests.
The build treats warnings as errors.

The complete work breakdown, conventions and per-package contracts are in [docs/PORT-PLAN.md](docs/PORT-PLAN.md).

## Layout

```
Librespot.sln
Directory.Build.props         net10.0, nullable, implicit usings, central package management
Directory.Packages.props      the single place NuGet versions live
src/Librespot.Protocol        librespot-protocol  (protobufs)
src/Librespot.OAuth           librespot-oauth
src/Librespot.Core            librespot-core
src/Librespot.Audio           librespot-audio
src/Librespot.Metadata        librespot-metadata
src/Librespot.Playback        librespot-playback
src/Librespot.Connect         librespot-connect
src/Librespot.Discovery       librespot-discovery
src/Librespot.Cli             the `librespot` binary (src/main.rs)
examples/Librespot.Examples   upstream examples/ as sub-commands of one console app
tests/Librespot.Tests         xunit
docs/PORT-PLAN.md             the port plan
```

Project references mirror the crate dependency graph (`protocol <- core`, `oauth <- core` (oauth has no protocol dependency), `core <- audio/metadata <- playback <- connect`, `core <- discovery`).

## Building

Requirements: .NET SDK 10.0 (protoc comes from the `Grpc.Tools` NuGet package, nothing else to install).

```
dotnet build Librespot.sln
dotnet test  Librespot.sln
dotnet run --project src/Librespot.Cli -- --version
```

## Platform notes

* Audio output is NAudio (WASAPI) on Windows, plus `pipe` and `subprocess` backends everywhere. The
  Linux-only upstream backends (alsa, pulseaudio, jack, gstreamer, portaudio, sdl, alsamixer) are not ported.
  The default backend is called `naudio` (upstream: `rodio`); `--backend rodio` is accepted as an alias.
* mDNS/DNS-SD discovery uses a small built-in responder (upstream `libmdns`); avahi and dns-sd are not ported.
* Ogg Vorbis is decoded with NVorbis, MP3 with NLayer. FLAC (used by upstream only for local files) is out of scope for now.

## License

MIT, see [LICENSE](LICENSE). librespot is Copyright (c) 2015 Paul Lietar and the librespot-org contributors.
