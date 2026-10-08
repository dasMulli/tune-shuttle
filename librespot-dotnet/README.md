# librespot-dotnet

A clean, idiomatic C# (.NET 10) port of [librespot](https://github.com/librespot-org/librespot), the
open-source Spotify client library with Spotify Connect support. The port tracks upstream commit
`e023adbbf017ae1fc10d01531dbe50c409786f2d` (v0.8.0); the Rust reference lives in
`../reference/librespot` (see `../docs/REFERENCE-SOURCES.md`).

Like upstream, this only works with Spotify Premium accounts.

## Status

**Skeleton stage.** The solution compiles and the tests pass, but most of the logic still has to be
ported. What exists today:

| Area | State |
|------|-------|
| `Librespot.Protocol` | All 46 upstream `.proto` files compiled with protoc at build time; `impl_trait` conversions ported. |
| `Librespot.Core` | Error hierarchy, `SessionConfig`/`DeviceType`, version constants, `SpotifyId`, `SpotifyUri`, `FileId`, `Date`, `PacketType`, Diffie-Hellman, **Shannon cipher + AP framing codec** (test vectors from the Rust crate), `Credentials` (JSON-compatible with upstream caches), the `Session` facade with all data accessors. Everything network-facing is a stub marked `TODO(port: <package>)`. |
| `Librespot.Playback` | Config enums, `ISink`/`ISinkAsBytes`, `IMixer`/`IVolumeGetter`, volume mappings + `SoftMixer`, `AudioPacket`, `PlayerEvent`/`PlayerCommand`, `Player` facade. Decoders, converter, player loop, sinks are stubs. |
| `Librespot.Audio`, `Librespot.Metadata`, `Librespot.Connect`, `Librespot.Discovery`, `Librespot.OAuth` | Public contracts only. |
| `Librespot.Cli`, `examples/` | Entry points only (`librespot --version` works). |

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
* mDNS/DNS-SD discovery uses a small built-in responder (upstream `libmdns`); avahi and dns-sd are not ported.
* Ogg Vorbis is decoded with NVorbis, MP3 with NLayer. FLAC (used by upstream only for local files) is out of scope for now.

## License

MIT, see [LICENSE](LICENSE). librespot is Copyright (c) 2015 Paul Lietar and the librespot-org contributors.
