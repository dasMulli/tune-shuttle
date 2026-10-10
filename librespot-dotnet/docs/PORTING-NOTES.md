# Porting notes

How librespot's Rust maps to the C# in this port: the conventions every file follows, the idiom mapping, the
libraries chosen per crate, the protobuf namespaces, and the known deviations from upstream. Read it before changing a
ported file or porting an upstream change. Reference: librespot v0.8.0, commit
`e023adbbf017ae1fc10d01531dbe50c409786f2d`, cloned to `../reference/librespot` (see
[`../../docs/REFERENCE-SOURCES.md`](../../docs/REFERENCE-SOURCES.md)).

## Conventions

* **One C# file per Rust file, same name, snake_case → PascalCase**: `core/src/spotify_uri.rs` →
  `src/Librespot.Core/SpotifyUri.cs`, `audio/src/fetch/receive.rs` → `src/Librespot.Audio/Fetch/Receive.cs`. A `mod.rs`
  becomes the directory's name (`dealer/mod.rs` → `Dealer/Dealer.cs`, `fetch/mod.rs` → `Fetch/Fetch.cs`). Reviewers
  diff file against file.
* Each file starts with a one-line `// Port of <crate>/src/<file>.rs.` header. A deliberate deviation from upstream is
  said in a comment at the deviation (`// Deviation: ...`).
* **Namespaces mirror crate/module paths**, flattened one level: a module that is a single file in `crate/src/` lives
  in the crate namespace (`core::spotify_id::SpotifyId` → `Librespot.Core.SpotifyId`); a module that is a directory gets
  a sub-namespace (`core::dealer::manager` → `Librespot.Core.Dealer`, `playback::audio_backend::pipe` →
  `Librespot.Playback.AudioBackend`). Exceptions: there is no `Librespot.Core.Dealer.Protocol` namespace
  (`Dealer/Protocol.cs` and `Dealer/Protocol/Request.cs` live in `Librespot.Core.Dealer`) and no
  `Librespot.Metadata.Playlist` namespace (`Playlist/*.cs` live in `Librespot.Metadata`), because a sub-namespace
  named `Protocol` or `Playlist` would shadow the identifier of the same name in every file of the parent namespace.
  The `connect/src/state/*.rs` files also live in `Librespot.Connect`. Generated protobuf types keep protoc's
  namespaces (see "Protocol buffers").
* **Name collisions with generated protobuf types**: many port types share their name with a `Spotify.Metadata.*`,
  `Spotify.Connectstate.*` or `Spotify.Player.Proto.*` message (`Track`, `Album`, `Artist`, `Episode`, `Show`, `Image`,
  `Restriction`, `AudioFile`, `Context`, `PlayOrigin`, `ProvidedTrack`, ...). Never import `Spotify.Metadata` wholesale
  in a file that defines one of these; import the message with a `Proto`-prefixed alias:
  `using ProtoTrack = Spotify.Metadata.Track;` (pattern in `src/Librespot.Metadata/Audio/File.cs`). When two generated
  namespaces define the same name, alias the one upstream imports (pattern in
  `src/Librespot.Core/Dealer/Protocol/Request.cs`).
* File-scoped namespaces, `using` directives sorted System-first, `var` where the type is obvious, braces always.
* **XML doc comments on every public type and member.** Translate the Rust doc comment; where upstream has none,
  write one sentence. Name the upstream item when it differs: `/// Upstream <c>get_player_event_channel</c>.`
* Upstream constants keep their values and become PascalCase (`PING_TIMEOUT` → `PingTimeout`).
* **Renames forced by .NET**: `Version` → `LibrespotVersion` (clashes with `System.Version`);
  `SpotifyUri.Local.Artist` → `ArtistName`; `PlayerCommand.Load.Play` → `StartPlaying`; `Librespot.Core.HttpClient`
  keeps its name and the implicit `using System.Net.Http;` is removed in `Directory.Build.props` (write
  `System.Net.Http.HttpClient` for the BCL one); `socket::connect` → `ApSocket.ConnectAsync` (file `Socket.cs`);
  `proxytunnel::proxy_connect` → `ProxyTunnel.ProxyConnectAsync`; the discovery examples → classes
  `DiscoveryExample` and `DiscoveryGroupExample`; `src/player_event_handler.rs::EventHandler` → `PlayerEventHandler`.
  Add to this list when you must rename.

## Type mapping

| Rust | C# |
|---|---|
| `struct` with only data, `#[derive(Clone, Debug, PartialEq)]` | `sealed record` (positional when ≤ 4 fields, otherwise `init` properties). Value-type-like newtypes (`SpotifyId`, `FileId`, `AudioKey`, `Date`) are `readonly record struct` / `readonly struct` with value equality. |
| `enum` without payload | `enum`, keeping the discriminant values. `impl FromStr` → `static X? Parse(string)` in an `XExtensions` class, `Display` → `ToDisplayString()`. |
| `enum` with payload (`PlayerEvent`, `SpotifyUri`, `VolumeCtrl`, `LoadContextOptions`) | `abstract record Base { private Base() {} public sealed record Variant(...) : Base; }`, a closed hierarchy matched with `switch`. |
| trait with `&self` methods | `interface IFoo`, with default bodies for upstream default impls. |
| trait with associated types or static constructors (`Metadata`, `VolumeMapping`, `Open`) | interface with `static abstract` members (`IMetadata<TSelf,TMessage>`, `IVolumeMapping`), or a delegate type where upstream uses fn pointers (`SinkBuilder`, `MixerFn`, `DithererBuilder`, `DnsSdServiceBuilder`). |
| `Box<dyn Fn(..)>` | a named `delegate`. |
| `Result<T, Error>` | return `T`, throw `LibrespotException` (see "Errors"). `Result<T, DomainError>` → throw the domain exception. |
| `Option<T>` | `T?`. |
| `Vec<u8>`, `Bytes`, `&[u8]` | `byte[]` for owned data handed out, `ReadOnlyMemory<byte>` for data kept and sliced, `ReadOnlySpan<byte>` for read-only parameters, `Span<byte>` for in-place work. Never `List<byte>`. |
| `String`, `&str`, `Cow<'static, str>` | `string`. |
| `u8/u16/u32/u64/u128/usize` | `byte/ushort/uint/ulong/UInt128/int` (`long` for sizes that can exceed 2 GB, such as file offsets). Wire-format fields keep upstream's width. |
| `HashMap` / `HashSet` / `VecDeque` / priority queue | `Dictionary<K,V>` / `HashSet<T>` / `Queue<T>` or `LinkedList<T>` / `PriorityQueue<TElement,TPriority>`. |
| `Duration` / `Instant` / `SystemTime` | `TimeSpan` / `Stopwatch.GetTimestamp()` + `Stopwatch.GetElapsedTime` (or `TimeProvider`) / `DateTimeOffset`. |
| `PathBuf`, `Url` | `string` path (`Path.Combine`), `Uri`. |
| `num_bigint::BigUint` | `System.Numerics.BigInteger` with unsigned big-endian helpers (see `DiffieHellman.cs`). |
| `thread::spawn` + `JoinHandle` | a dedicated `Thread` only where upstream explicitly uses a thread (player loop, event handler); otherwise `Task.Run`. |

## Async, channels, locking

* `async fn` → `async Task<T>`/`async Task` with the `Async` suffix; every awaitable public method takes a trailing
  `CancellationToken cancellationToken = default` and passes it down. Library code uses `.ConfigureAwait(false)`.
* `tokio::spawn(fut)` → `Task.Run(...)` or `_ = RunAsync(ct)`, with the task stored so it can be awaited or cancelled
  on shutdown.
* `tokio::sync::mpsc` → `System.Threading.Channels` (`ChannelWriter<T>` / `ChannelReader<T>`).
  `tx.send(x)?` → `if (!writer.TryWrite(x)) throw LibrespotException.Internal("...")`; dropping the sender →
  `writer.TryComplete()`.
* `tokio::sync::oneshot` → `TaskCompletionSource<T>` with `RunContinuationsAsynchronously`; a dropped sender →
  `TrySetException(LibrespotException.Aborted(...))` or `TrySetCanceled()`.
* `watch`, `broadcast` → a `Channel` per subscriber, or an `event` plus a snapshot property.
* `futures::Stream<Item=T>` → `IAsyncEnumerable<T>` (or the `ChannelReader<T>` when the consumer wants `TryRead`).
* `tokio::select!` → `await Task.WhenAny(...)` on the pending tasks, or one loop over a merged channel, keeping
  upstream's priority order (the player and Spirc loops in particular).
* `sleep` / `timeout` → `Task.Delay(span, ct)` / `task.WaitAsync(span, ct)`; a `Sleep` that is `reset()` → a fresh
  `Task.Delay` with a new linked `CancellationTokenSource`.
* `std::sync::Mutex` around short, non-awaiting sections → `lock`; `tokio::sync::Mutex` or any section that awaits →
  `SemaphoreSlim(1, 1)` with `try/finally`; `RwLock` → `ReaderWriterLockSlim`.
* `Arc<T>` / `Weak<T>` → plain references. Where upstream relies on `Drop` to tear things down (dropping the last
  `Session` closes the connection), there is an explicit `Shutdown()`/`Dispose()` that the owner calls.
* Atomics → `Interlocked` / `Volatile`; `OnceLock` → `Lazy<T>`.
* Blocking APIs that upstream offers next to async ones (`get_access_token` vs `get_access_token_async`) stay: the async
  one is implemented and the sync one is `GetAwaiter().GetResult()` of it.

## Errors

* `LibrespotException : Exception` (`src/Librespot.Core/Error.cs`) carries `ErrorKind Kind` and `string Detail`; its
  `Message` renders like upstream's `Display`. Static factories (`LibrespotException.Aborted(..)`, `.NotFound(..)`, ...)
  mirror `Error::aborted(..)` and its siblings.
* Every upstream domain error enum is an `XxxException : LibrespotException` with a nested `enum Reason` and the `Kind`
  fixed the way upstream's `impl From<XxxError> for Error` fixes it (`SessionException`, `AudioKeyException`,
  `MercuryException`, `SinkException`, ...), with factories that build upstream's `#[error("...")]` text.
* `impl From<ForeignError> for Error` → `LibrespotException.From(Exception)`, which classifies BCL exceptions. Wrap at
  the boundary where upstream used `?` on a foreign error:
  `catch (Exception e) when (e is not LibrespotException) { throw LibrespotException.From(e); }`.
* `Result<_, ()>` and `FromStr` failures return `null`, not an exception.
* Where upstream logs and continues, log at the same level and continue; never swallow silently.

## Logging

The `log` crate → `Microsoft.Extensions.Logging` through the static hook `Librespot.Core.Logging.LibrespotLog`:

```csharp
private static readonly ILogger Log = LibrespotLog.CreateLogger<Session>();   // category = full type name
Log.LogDebug("Connecting to AP \"{Host}:{Port}\"", ap.Host, ap.Port);          // structured, not interpolated
```

`error!/warn!/info!/debug!/trace!` → `LogError/LogWarning/LogInformation/LogDebug/LogTrace`;
`debug!(target: "x", ..)` → `LibrespotLog.CreateLogger("x")`. A host (the CLI, or TuneShuttle's bridge) sets
`LibrespotLog.Factory`.

## Serialization

* serde JSON → `System.Text.Json`: `sealed record` DTOs with `[JsonPropertyName("snake_case")]`; custom
  `serialize_with`/`deserialize_with` → `JsonConverter<T>` (see `CredentialsJsonConverter`, `DeserializeWith.cs`);
  `serde_json::Value` → `JsonElement`/`JsonNode`.
* protobuf → the `Google.Protobuf` classes generated in `Librespot.Protocol`: `parse_from_bytes` →
  `T.Parser.ParseFrom(bytes)`, `write_to_bytes` → `ToByteArray()`, proto2 `has_x()`/`x()` → `HasX`/`X`;
  protobuf-json-mapping → `JsonFormatter`/`JsonParser`.
* `quick_xml` → `System.Xml.XmlReader`; base64 → `Convert`; hex → `Convert.ToHexStringLower`/`FromHexString`;
  BASE64URL → `Base64Url`; byteorder → `BinaryPrimitives`.

## Crypto

Everything is in the .NET framework; the port does not use BouncyCastle.

| Need | .NET |
|---|---|
| Shannon stream cipher + MAC | `Librespot.Core.Connection.Shannon` (ported) |
| AES-128-CTR (audio decryption, discovery blob) | `Librespot.Core.Crypto.AesCtr`: big-endian 128-bit counter = IV + offset/16, like `ctr::Ctr128BE<Aes128>` with `seek`. Use it; do not write another CTR implementation. |
| AES-192-ECB (credential blob) | `aes.DecryptEcb(data, PaddingMode.None)` with a 24-byte key |
| Diffie-Hellman, 768-bit prime | `Librespot.Core.DhLocalKeys` (`BigInteger.ModPow`) |
| RSA-SHA1 check of the AP's DH key | `RSA.VerifyData(..., HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)` |
| HMAC-SHA1, PBKDF2-HMAC-SHA1, SHA-1 | `HMACSHA1.HashData`, `Rfc2898DeriveBytes.Pbkdf2`, `SHA1.HashData` |
| Random | `RandomNumberGenerator.Fill` for keys and nonces; the shuffle uses `SeededRng` (in `ShuffleVec.cs`), a reimplementation of rand's `SmallRng` (see "Known deviations") |

## Tests

* xunit in `tests/Librespot.Tests`, one folder per project (`Core/`, `Playback/`, ...), file `<RustFile>Tests.cs`.
* Every `#[cfg(test)] mod tests` of a ported file is ported with its test names.
* Tests that talk to Spotify or need the network run only with `LIBRESPOT_LIVE_TESTS=1` (plus `LIBRESPOT_ACCESS_TOKEN`
  for the Spotify ones); tests that need an audio device are skipped.
* `dotnet build Librespot.sln` treats warnings as errors (CS1591, a missing XML doc, is exempt).

## NuGet mapping

Versions are pinned in `Directory.Packages.props` (central package management; no versions in project files).

| Rust crate(s) | .NET | Used by |
|---|---|---|
| protobuf, protobuf-codegen | `Google.Protobuf` + `Grpc.Tools` (bundles protoc; build-time code generation) | Protocol, Core, Metadata, Connect |
| log, env_logger | `Microsoft.Extensions.Logging.Abstractions` (libraries), `Microsoft.Extensions.Logging` + `.Console` (CLI, examples) | all |
| serde, serde_json, serde_repr | `System.Text.Json` | Core, Metadata, Connect, Discovery |
| symphonia (vorbis) | `NVorbis` | Playback |
| symphonia (mp3) | `NLayer` | Playback |
| symphonia (flac) | not ported (upstream uses it for local files only) | – |
| rodio + cpal | `NAudio` + `NAudio.Wasapi` (`WasapiPlayer`) | Playback |
| libmdns, dns-sd, zbus/avahi | a hand-written minimal mDNS responder in `Librespot.Discovery/Mdns/` (`Makaretu.Dns.Multicast` is unmaintained and misbehaves with several NICs on Windows) | Discovery |
| hyper, reqwest, http and friends | `System.Net.Http` (`SocketsHttpHandler`, proxy via `WebProxy`); the Zeroconf server is a small HTTP/1.1 parser on `TcpListener` (`HttpListener` needs URL ACLs on Windows) | Core, Audio, OAuth, Discovery |
| tokio-tungstenite | `System.Net.WebSockets.ClientWebSocket` | Core (dealer) |
| tokio, futures | `System.Threading.Tasks`, `System.Threading.Channels` | all |
| aes, ctr, hmac, pbkdf2, sha1, rsa, num-bigint | `System.Security.Cryptography`, `System.Numerics` | Core, Audio, Discovery |
| shannon | ported (`Connection/Shannon.cs`) | Core |
| oauth2 | hand-written (PKCE = SHA-256 + base64url, token endpoint = form POST) | OAuth |
| open | `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` | OAuth |
| getopts | a hand-written getopts-compatible parser (keeps upstream's option names and help layout) | Cli |
| uuid, time, url | `Guid`, `DateTimeOffset`, `Uri` | Core, Connect, Discovery |
| rand, rand_distr | `RandomNumberGenerator`, `SeededRng`; the dither distributions are hand-written in `Dither.cs` | Playback, Connect |
| quick-xml, flate2 | `System.Xml`, `GZipStream` | Core |
| governor (rate limiter) | a hand-written token bucket in `HttpClient.cs` | Core |
| tempfile | `Path.GetTempFileName()` + `FileOptions.DeleteOnClose` | Audio |
| shell-words | a hand-written splitter (`subprocess` backend) | Playback |
| xunit test stack | `xunit` 2.9.3, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | Tests |

## Protocol buffers

The 46 `.proto` files that `protocol/build.rs` compiles are in `src/Librespot.Protocol/proto/`, compiled by
`Grpc.Tools` (`GrpcServices="None"`). Two patches, marked `// librespot-dotnet patch:` in the files:

1. `authentication.proto`, `keyexchange.proto`, `mercury.proto`, `pubsub.proto` have no `package`; they get
   `option csharp_namespace = "Librespot.Protocol.<File>";`.
2. `spotify/login5/v3/challenges/{code,hashcash}.proto`: the default namespace collides with the `Challenges` message
   of `login5.proto`; they get `option csharp_namespace = "Spotify.Login5.V3.ChallengeTypes";`.

| Rust module (`librespot_protocol::…`) | C# namespace |
|---|---|
| `authentication`, `keyexchange`, `mercury`, `pubsub` | `Librespot.Protocol.Authentication` / `.Keyexchange` / `.Mercury` / `.Pubsub` |
| `connect`, `player` | `Spotify.Connectstate` |
| `devices` | `Spotify.Connectstate.Devices` |
| `media` | `Spotify.Common.Media` |
| `connectivity` | `Spotify.Clienttoken.Data.V0` |
| `clienttoken_http` | `Spotify.Clienttoken.Http.V0` |
| `entity_extension_data`, `extended_metadata`, `extension_kind` | `Spotify.Extendedmetadata` |
| `metadata` | `Spotify.Metadata` (nested enums: `AudioFile.Types.Format`, `Image.Types.Size`, ...) |
| `playlist_annotate3` | `SpotifyPlaylistAnnotate3.Proto` |
| `playlist_permission` | `Spotify.PlaylistPermission.Proto` |
| `playlist4_external` | `Spotify.Playlist4.Proto` |
| `lens_model` | `Spotify.Lens.Model.Proto` |
| `signal_model` | `Spotify.Playlist.Signal.Proto` |
| `login5`, `client_info`, `user_info` | `Spotify.Login5.V3` |
| `code`, `hashcash` (challenges) | `Spotify.Login5.V3.ChallengeTypes` |
| `credentials` | `Spotify.Login5.V3.Credentials` |
| `identifiers` | `Spotify.Login5.V3.Identifiers` |
| `storage_resolve` | `Spotify.Download.Proto` |
| `user_attributes` | `Spotify.UserAttributes.Proto` |
| `autoplay_context_request`, `context_player_options`, `context_track`, `context`, `restrictions`, `context_page`, `play_origin`, `suppressions` | `Spotify.Player.Proto` |
| `transfer_state`, `playback`, `play_history`, `session`, `queue`, `instrumentation_params` | `Spotify.Player.Proto.Transfer` |
| `social_connect_v2` | `Socialconnect` |
| `canvaz` | `Spotify.Canvaz.Cache` |
| `canvaz_meta` | `Spotify.Canvaz` |
| `explicit_content_pubsub` | `Spotify.ExplicitContent.Proto` |

Enum values are PascalCased by protoc with the enum-name prefix stripped (`AUTHENTICATION_USER_PASS` →
`AuthenticationType.AuthenticationUserPass`). `protocol/src/impl_trait/*.rs` → `ImplTrait/Context.cs` and
`ImplTrait/Player.cs` (extension methods).

## Known deviations

Each deviation is also commented at its site (search for `Deviation`). The larger ones:

* **Not ported:** the Linux/native-only audio backends `alsa`, `pulseaudio`, `jackaudio`, `gstreamer`, `portaudio`,
  `sdl`, the `rodiojack` variant and the `alsamixer` mixer (they stay unknown names, as in an upstream build without
  those features); the `avahi` and `dns-sd` discovery backends (known but unavailable); FLAC decoding; `util.rs`'
  `keep_flushing` (not needed with `ClientWebSocket`).
* **Audio backends:** `naudio` (WASAPI, Windows) replaces rodio/cpal and is the default; `--backend rodio` is accepted
  as an alias. `pipe` and `subprocess` work everywhere.
* **Decoders:** NVorbis and NLayer instead of symphonia. Both decode samples, not packets: each `NextPacket()` returns
  up to 100 ms of samples. NVorbis seeks sample-accurately, so `Seek()` returns the requested position. NLayer has no
  gapless support: the encoder delay and padding of an MP3's LAME tag are not trimmed.
* **Known issue (Vorbis):** `VorbisDecoder` asks NVorbis for `TotalSamples` on load and on every seek, and NVorbis
  answers by reading every remaining Ogg page. On a streamed track this becomes one 64 KiB CDN request per page run, so
  a burst of rapid track changes can exhaust the local 300-requests-per-30-s limiter for `scdn.co`; the load then
  times out and the player skips the track. The fix is to read the final granule position from the last Ogg page
  (`Decoder/Ogg.cs` can parse it) instead of calling `TotalSamples`.
* **Player:** `PlayerInternal` is a synchronous loop on its own thread instead of a future driven by `block_on`; loads
  run on the thread pool and are awaited on shutdown; decoders are disposed explicitly where Rust drops them.
* **Dealer:** `ClientWebSocket` cannot send PING frames or surface PONGs, so upstream's ping task is expressed with
  the built-in keep-alive (`KeepAliveInterval` = `PING_INTERVAL`, `KeepAliveTimeout` = `PING_TIMEOUT`); Rust drops are
  explicit `Release`/`Dispose` calls; the final join on shutdown is bounded by `WEBSOCKET_CLOSE_TIMEOUT`.
* **mDNS responder** (replaces libmdns), for hosts with several network interfaces: a multicast join failure on one
  interface is skipped instead of failing the address family; responses go out on the interface the query arrived on;
  address records list that interface's addresses; identical records are not repeated in one message. Group, port and
  hop limit come from `MdnsEndpoint` (production: `MdnsEndpoint.Default`), so tests can run on a private group.
* **Discovery:** the Zeroconf HTTP server listens only on IPv4 on Windows, so no AAAA records are announced there.
* **Shuffle:** `SeededRng` (`ShuffleVec.cs`) reimplements rand 0.9's `SmallRng` (xoshiro256++ seeded through SplitMix64) and its
  `random_range`, so a seed shuffles like the Rust binary; `System.Random` is not used.
* **CLI:** a getopts-compatible parser keeps upstream's option names, order, defaults and help texts; the `/regex`
  filter of `env_logger` is not supported.
* **Examples:** one console app with a sub-command per upstream example (`examples/Librespot.Examples`).
