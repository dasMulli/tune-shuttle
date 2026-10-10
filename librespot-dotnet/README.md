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

Verified end to end on 2026-10-09: the CLI in discovery mode was picked from the Spotify app, logged in via the
Zeroconf credential handoff, and played through WASAPI, with pause, volume, seek, skip, shuffle, repeat and transfer
all handled. TuneShuttle's bridge (`../src/Bridge.Service`) uses `Librespot.Core`, `Librespot.Connect`, `Librespot.Metadata`,
`Librespot.Playback` and `Librespot.Discovery` with its own sink.

## Deviations from upstream

Not ported, by design: the Linux/native-only audio backends (`alsa`, `pulseaudio`, `jackaudio`, `gstreamer`,
`portaudio`, `sdl`, the `rodiojack` variant, the `alsamixer` mixer), the `avahi` and `dns-sd` discovery backends, FLAC
decoding (upstream uses it only for local files), and `util.rs`' `keep_flushing`. The decoders are NVorbis and NLayer
instead of symphonia (no MP3 gapless trimming), the dealer uses `ClientWebSocket`'s keep-alive for its ping, and the
mDNS responder replaces libmdns with per-interface answers. The full list, with the reasons, is in
[docs/PORTING-NOTES.md](docs/PORTING-NOTES.md#known-deviations).

Known issue: `VorbisDecoder` asks NVorbis for `TotalSamples` on load and on every seek, which reads every remaining
Ogg page of a streamed track. A burst of rapid track changes can then exhaust the local rate limiter for `scdn.co`,
the load times out and the player skips the track. The fix is described in the porting notes.

## Tests

`dotnet test Librespot.sln` runs the unit tests. Tests that need the network or an audio device are skipped by default;
set `LIBRESPOT_LIVE_TESTS=1` (plus `LIBRESPOT_ACCESS_TOKEN` for the ones that talk to Spotify) to run the live tests.
The build treats warnings as errors.

The conventions every ported file follows, the Rust-to-C# idiom mapping, the NuGet choices per crate and the protobuf
namespaces are in [docs/PORTING-NOTES.md](docs/PORTING-NOTES.md).

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
docs/PORTING-NOTES.md         conventions, idiom mapping, deviations
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
