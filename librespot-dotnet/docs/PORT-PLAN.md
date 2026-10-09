# librespot → C# port plan

Reference: `D:\airplay2-streamer\reference\librespot` at commit `e023adbbf017ae1fc10d01531dbe50c409786f2d` (v0.8.0).
Target: `D:\airplay2-streamer\librespot-dotnet` (this repository), .NET 10, C# latest.

This document is the contract between the architect stage (done) and the porting agents. Each porting
agent gets **one work package** (section 4), ports every Rust file listed for it, keeps the public
contracts in section 5 stable (extend, do not break), and finishes with `dotnet build Librespot.sln`
and `dotnet test Librespot.sln` green.

Contents

1. [Conventions for porters](#1-conventions-for-porters)
2. [NuGet mapping](#2-nuget-mapping)
3. [What the skeleton already provides](#3-what-the-skeleton-already-provides)
4. [Work packages and waves](#4-work-packages-and-waves)
5. [Per-package API contracts and verification](#5-per-package-api-contracts-and-verification)
6. [File → package index](#6-file--package-index)

---

## 1. Conventions for porters

### 1.1 Files, namespaces, names

* **One C# file per Rust file, same name, snake_case → PascalCase**: `core/src/spotify_uri.rs` → `src/Librespot.Core/SpotifyUri.cs`, `audio/src/fetch/receive.rs` → `src/Librespot.Audio/Fetch/Receive.cs`. A `mod.rs` becomes the directory's name (`dealer/mod.rs` → `Dealer/Dealer.cs`, `fetch/mod.rs` → `Fetch/Fetch.cs`, `mercury/mod.rs` → `Mercury/Mercury.cs`). Reviewers diff file against file. The skeleton's stub files already follow this (`Dealer/Manager.cs`, `Mercury/{Mercury,Types,Sender}.cs`, `Fetch/Fetch.cs`); **never rename a stub file that another package references** – if a name is wrong, fix it in this plan first.
* **Namespaces mirror crate/module paths**, flattened one level: a module that is a single file in `crate/src/` lives in the crate namespace (`core::spotify_id::SpotifyId` → `Librespot.Core.SpotifyId`, because `Librespot.Core.SpotifyId.SpotifyId` would be unreadable). A module that is a directory gets a sub-namespace (`core::dealer::manager` → `Librespot.Core.Dealer`, `playback::audio_backend::pipe` → `Librespot.Playback.AudioBackend`). The protobuf crate is the exception: generated types keep protoc's package-derived namespaces (section 3.1). **Exception – no `Librespot.Core.Dealer.Protocol` namespace**: `core/src/dealer/protocol.rs` and `dealer/protocol/request.rs` (`Dealer/Protocol.cs`, `Dealer/Protocol/Request.cs`) both live in `Librespot.Core.Dealer`, because a sub-namespace named `Protocol` would shadow the identifier `Protocol` (`Librespot.Protocol.*`) in every `Librespot.Core.Dealer` file. The same rule applies to any other directory that would be named `Protocol`. **Exception – no `Librespot.Metadata.Playlist` namespace**: the `metadata/src/playlist/*.rs` files (`Playlist/*.cs`) live in `Librespot.Metadata`, for the same reason: a sub-namespace named `Playlist` would shadow the `Playlist` record in every `Librespot.Metadata` file.
* **Name collisions with generated protobuf types**: many port types share their name with a `Spotify.Metadata.*` / `Spotify.Connectstate.*` / `Spotify.Player.Proto.*` message (`Track`, `Album`, `Artist`, `Episode`, `Show`, `Image`, `Restriction`, `SalePeriod`, `Copyright`, `ExternalId`, `ContentRating`, `Availability`, `VideoFile`, `AudioFile`, `Context`, `PlayOrigin`, `ProvidedTrack`, ...). Never `using Spotify.Metadata;` wholesale in a file that defines one of these. Import the message with a **`Proto`-prefixed alias**: `using ProtoTrack = Spotify.Metadata.Track;`, `using ProtoAudioFile = Spotify.Metadata.AudioFile;`, `using ProtoFormat = Spotify.Metadata.AudioFile.Types.Format;` (pattern in `src/Librespot.Metadata/Audio/File.cs`). When two generated namespaces define the same name (`PlayOrigin` in `Spotify.Connectstate` and `Spotify.Player.Proto`) alias the one upstream imports (pattern in `src/Librespot.Core/Dealer/Protocol/Request.cs`).
* **File-scoped namespaces**, `using` directives sorted System-first, `var` where the type is obvious, braces always.
* **XML doc comments on every public type and member.** Translate the Rust doc comment; where upstream has none, write one sentence. Reference the upstream name when it differs: `/// Upstream <c>get_player_event_channel</c>.`
* Start each file with a one-line `// Port of <crate>/src/<file>.rs.` header. When you deliberately deviate from upstream, say so in a comment at the deviation.
* Keep upstream's constant names as `PascalCase` constants (`PING_TIMEOUT` → `PingTimeout`) and keep the values.
* Mark anything you leave for another package with `// TODO(port: <package-id>)`. Never leave a bare `TODO`.
* **Renames forced by .NET**: `Version` → `LibrespotVersion` (clashes with `System.Version`); `SpotifyUri.Local.Artist` → `ArtistName` (clashes with the sibling record `Artist`); `PlayerCommand.Load.Play` → `StartPlaying`; `Librespot.Core.HttpClient` keeps its name and the implicit `using System.Net.Http;` is removed solution-wide in `Directory.Build.props` (write `System.Net.Http.HttpClient` when you need the BCL one); `socket::connect` → `ApSocket.ConnectAsync` (file `Socket.cs`; a type named `Socket` in `Librespot.Core` would shadow `System.Net.Sockets.Socket`); `proxytunnel::proxy_connect` → `ProxyTunnel.ProxyConnectAsync`; `discovery/examples/discovery.rs` → class `DiscoveryExample` (file `Examples/Discovery.cs`, entry `DiscoveryExample.RunAsync(args, ct)`) and `discovery/examples/discovery_group.rs` → class `DiscoveryGroupExample` (file `Examples/DiscoveryGroup.cs`, entry `DiscoveryGroupExample.RunAsync(args, ct)`) (a class named `Discovery` in `Librespot.Examples` would hide the `Librespot.Discovery` namespace). Add to this list when you must rename.

### 1.2 Type mapping

| Rust | C# |
|---|---|
| `struct` with only data, `#[derive(Clone, Debug, PartialEq)]` | `sealed record` (positional when ≤ 4 fields, otherwise `init` properties). Value-type-like newtypes (`SpotifyId`, `FileId`, `AudioKey`, `Date`) are `readonly record struct` / `readonly struct` with value equality. |
| `enum` without payload | `enum`. Keep discriminant values. `impl FromStr` → `static X? Parse(string)` in an `XExtensions` class, `Display` → `ToDisplayString()`. |
| `enum` with payload (`PlayerEvent`, `SpotifyUri`, `VolumeCtrl`, `LoadContextOptions`) | `abstract record Base { private Base() {} public sealed record Variant(...) : Base; }` – a closed hierarchy, pattern-matched with `switch`. |
| trait with `&self` methods | `interface IFoo`; default method bodies for upstream default impls. |
| trait with associated types / static constructors (`Metadata`, `VolumeMapping`, `Open`) | interface with `static abstract` members (`IMetadata<TSelf,TMessage>`, `IVolumeMapping`), or a delegate type where upstream uses fn pointers (`SinkBuilder`, `MixerFn`, `DithererBuilder`, `DnsSdServiceBuilder`). |
| `Box<dyn Fn(..)>` | a named `delegate`. |
| `Result<T, Error>` | return `T`, throw `LibrespotException` (section 1.4). `Result<T, DomainError>` → throw the domain exception. |
| `Option<T>` | `T?` (nullable reference or `Nullable<T>`). |
| `Vec<u8>`, `Bytes`, `&[u8]` | `byte[]` for owned data you hand out, `ReadOnlyMemory<byte>` for data you keep and slice, `ReadOnlySpan<byte>` for parameters that are only read, `Span<byte>` for in-place work. Never `List<byte>`. |
| `String`, `&str`, `Cow<'static, str>` | `string`. |
| `u8/u16/u32/u64/u128/usize` | `byte/ushort/uint/ulong/UInt128/int` (use `long` for sizes that can exceed 2 GB, e.g. file offsets). Keep upstream's width for wire-format fields. |
| `HashMap<K,V>` / `HashSet` / `VecDeque` / `PriorityQueue` | `Dictionary<K,V>` / `HashSet` / `Queue`+`LinkedList` or `Deque`-like / `PriorityQueue<TElement,TPriority>`. |
| `std::time::Duration` / `Instant` / `SystemTime` | `TimeSpan` / `Stopwatch.GetTimestamp()`+`Stopwatch.GetElapsedTime` (or `TimeProvider`) / `DateTimeOffset`. |
| `PathBuf` | `string` path (`Path.Combine`). |
| `Url` | `Uri`. |
| `u128` | `UInt128`. `num_bigint::BigUint` → `System.Numerics.BigInteger` (unsigned big-endian helpers, see `DiffieHellman.cs`). |
| `thread::spawn` + `JoinHandle` | a dedicated `Thread` only where upstream explicitly uses a thread (player loop, event handler); otherwise `Task.Run`. |

### 1.3 Async, channels, locking

* **`async fn` → `async Task<T>`/`async Task`, every awaitable public method takes a trailing `CancellationToken cancellationToken = default`** and passes it down. Use `.ConfigureAwait(false)` in library code. Name async methods with the `Async` suffix.
* `tokio::spawn(fut)` → `Task.Run(...)` or `_ = RunAsync(ct)` with the task stored so it can be awaited/cancelled on shutdown. `Session::spawn` → `Task.Run`.
* **`tokio::sync::mpsc` (bounded / unbounded) → `System.Threading.Channels`** (`Channel.CreateUnbounded<T>()` / `CreateBounded`). Sender → `ChannelWriter<T>`, receiver → `ChannelReader<T>`. `tx.send(x)?` → `if (!writer.TryWrite(x)) throw LibrespotException.Internal("...")`. Dropping the sender → `writer.TryComplete()`.
* **`tokio::sync::oneshot` → `TaskCompletionSource<T>`** created with `TaskCreationOptions.RunContinuationsAsynchronously`; `send(v)` → `TrySetResult(v)`, dropped sender → `TrySetException(LibrespotException.Aborted(...))` / `TrySetCanceled()`.
* `tokio::sync::watch`, `broadcast` → `Channel` per subscriber or an `event` + snapshot property.
* **`futures::Stream<Item=T>` → `IAsyncEnumerable<T>`** (or expose the `ChannelReader<T>` when the consumer wants `TryRead`). `StreamExt::next()` → `await foreach` / `reader.ReadAsync(ct)`.
* `tokio::select!` → `await Task.WhenAny(...)` on the pending tasks, or a single loop over a merged `Channel`. The player and spirc loops: read the upstream `select!` carefully and keep the same priority order.
* `tokio::time::sleep` / `timeout` → `Task.Delay(span, ct)` / `task.WaitAsync(span, ct)`. `Sleep` with `reset()` → recreate a `Task.Delay` with a fresh linked `CancellationTokenSource`.
* `std::sync::Mutex<T>` guarding short, non-awaiting sections → `lock (Lock) { }` (`SessionComponent.Lock` is there for that). `tokio::sync::Mutex` or any section that awaits → `SemaphoreSlim(1,1)` with `await sem.WaitAsync(ct)` + `try/finally`. `RwLock` → `ReaderWriterLockSlim` (see `Session.cs`). `Arc<Mutex<T>>` shared across tasks → one object with its own lock, passed by reference.
* `Arc<T>` / `Weak<T>` → plain references. The GC handles cycles; where upstream relied on `Drop` to tear things down (dropping the last `Session` closes the connection), add an explicit `Shutdown()`/`Dispose()` and call it from the owner.
* `AtomicUsize` etc. → `Interlocked` / `Volatile`. `OnceLock` → `Lazy<T>`.
* `BiLock` (audio channel) → a single `Channel<ChannelEvent>` with two reader views.
* Blocking APIs that upstream exposes next to async ones (`get_access_token` vs `get_access_token_async`, `fetch_blocking`) stay: implement the async one and make the sync one `GetAwaiter().GetResult()` of it.

### 1.4 Errors

Upstream has one `Error { kind, source }` type plus many small `thiserror` enums converted into it. The C# shape (already in `src/Librespot.Core/Error.cs`):

* `LibrespotException : Exception` with `ErrorKind Kind` and `string Detail`; `Message` renders exactly like upstream `Display` (`"{kind text} { {detail} }"`). Static factories `LibrespotException.Aborted(..)`, `.NotFound(..)`, ... mirror `Error::aborted(..)` etc.
* Every upstream domain error enum becomes `XxxException : LibrespotException` with a nested `enum Reason` for the variants and the `Kind` fixed the way the upstream `impl From<XxxError> for Error` fixes it (see `SessionException`, `AudioKeyException`, `MercuryException`, `SinkException`, ...). Add `public static XxxException Variant(args)` factories that build the upstream `#[error("...")]` text. Create new ones the same way.
* `impl From<ForeignError> for Error` → `LibrespotException.From(Exception)` classifies BCL exceptions (IO, sockets, HTTP, JSON, protobuf, channels). Wrap at the boundary where upstream used `?` on a foreign error: `catch (Exception e) when (e is not LibrespotException) { throw LibrespotException.From(e); }`.
* `Result<_, ()>`/`FromStr` failures → return `null` (`T?`), not an exception.
* Never swallow exceptions where upstream logs and continues; log them at the same level (`warn!` → `LogWarning`) and continue.

### 1.5 Logging

`log` crate → `Microsoft.Extensions.Logging` through the static hook `Librespot.Core.Logging.LibrespotLog`:

```csharp
private static readonly ILogger Log = LibrespotLog.CreateLogger<Session>();   // category = full type name
Log.LogDebug("Connecting to AP \"{Host}:{Port}\"", ap.Host, ap.Port);          // structured, not interpolated
```

`error!/warn!/info!/debug!/trace!` → `LogError/LogWarning/LogInformation/LogDebug/LogTrace`. `debug!(target: "x", ..)` → `LibrespotLog.CreateLogger("x")`. The CLI wires `LibrespotLog.Factory` to a console logger with the same `-v`/`--quiet` semantics as upstream `setup_logging`.

### 1.6 Serialization

* **serde JSON → `System.Text.Json`.** Plain DTOs: `sealed record` with `[JsonPropertyName("snake_case")]`; add `[JsonSourceGenerationOptions]`/`JsonSerializerContext` partial classes per assembly for AOT-friendliness when a DTO set is large (dealer protocol, spclient responses). Custom `serialize_with`/`deserialize_with` → `JsonConverter<T>` (see `CredentialsJsonConverter`; `core/src/deserialize_with.rs` becomes `DeserializeWith.cs` with the same helpers, owned by `core-dealer`). `serde_json::Value` → `JsonElement`/`JsonNode`.
* **protobuf → `Google.Protobuf` generated classes** (`Librespot.Protocol`). `Message::parse_from_bytes` → `T.Parser.ParseFrom(bytes)`, `write_to_bytes` → `ToByteArray()`, proto2 `has_x()`/`x()` → `HasX`/`X`, `set_x` → property set, `SpecialFields` has no equivalent. protobuf-json-mapping → `JsonFormatter`/`JsonParser` from Google.Protobuf.
* `quick_xml` (ProductInfo) → `System.Xml.XmlReader`.
* base64 → `Convert.To/FromBase64String`; hex → `Convert.ToHexStringLower` / `FromHexString`; `data-encoding` BASE64URL → `Base64Url` (`System.Buffers.Text`).
* byteorder → `System.Buffers.Binary.BinaryPrimitives`.

### 1.7 Crypto (all built-in, no BouncyCastle)

| Need | .NET |
|---|---|
| Shannon stream cipher + MAC | `Librespot.Core.Connection.Shannon` (ported, tested). |
| AES-128-CTR (audio decrypt, discovery blob) | `Librespot.Core.Crypto.AesCtr` (**ported and tested**, foundation): `new AesCtr(key, iv) { Position = byteOffset }.Apply(span)` for streaming (`AudioDecrypt.Read`/`Seek` set `Position`), `AesCtr.Transform(key, iv, byteOffset, span)` one-shot (discovery blob). Big-endian 128-bit counter = IV + offset/16, exactly `ctr::Ctr128BE<Aes128>` + `seek`. Do not write another CTR implementation. |
| AES-192-ECB (credential blob, `Credentials::with_blob`) | `aes.DecryptEcb(data, PaddingMode.None)` with a 24-byte key. |
| Diffie-Hellman, 768-bit prime | `Librespot.Core.DhLocalKeys` (ported, `BigInteger.ModPow`). |
| RSA-SHA1 signature check of the AP's DH key (handshake) | `RSA.Create()`, `ImportParameters(new RSAParameters { Modulus = SERVER_KEY, Exponent = [1,0,1] })`, `VerifyData(gs, signature, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1)`. |
| HMAC-SHA1 | `HMACSHA1.HashData(key, data)`. |
| PBKDF2-HMAC-SHA1 | `Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA1, length)`. |
| SHA-1 | `SHA1.HashData`. |
| Random | `RandomNumberGenerator.Fill` for keys/nonces, `Random.Shared` for shuffles (`SmallRng::seed_from_u64` → `new Random((int)seed)` is *not* the same sequence; see package `connect-state`). |

### 1.8 Tests

* xunit in `tests/Librespot.Tests`, one folder per project (`Core/`, `Playback/`, ...), file `<RustFile>Tests.cs`.
* Port every `#[cfg(test)] mod tests` of the files you own (listed per package). Keep the test names.
* Network tests (anything that would talk to Spotify) must be `[Fact(Skip = "network")]` or behind an environment variable `LIBRESPOT_LIVE_TESTS=1`.
* `dotnet test` must stay green; `dotnet build` must stay warning-free apart from `CS1573/CS1591` doc warnings.

---

## 2. NuGet mapping

All versions verified against nuget.org on 2026-10-08 with `dotnet package search --exact-match`; they are pinned in `Directory.Packages.props` (central package management – do **not** put versions in csproj files).

| Rust crate(s) | NuGet package | Version | Used by |
|---|---|---|---|
| protobuf, protobuf-codegen | `Google.Protobuf` + `Grpc.Tools` (bundles protoc; build-time codegen, `PrivateAssets=all`) | 3.36.2 / 2.84.0 | Protocol, Core, Metadata, Connect |
| log, env_logger | `Microsoft.Extensions.Logging.Abstractions` (libraries), `Microsoft.Extensions.Logging` + `.Console` (CLI, examples) | 10.0.12 | all |
| serde, serde_json, serde_repr | `System.Text.Json` (in the shared framework, no package) | – | Core, Metadata, Connect, Discovery |
| symphonia (vorbis) | `NVorbis` | 0.10.5 | Playback |
| symphonia (mp3) | `NLayer` | 3.0.0 | Playback |
| symphonia (flac) | *not ported* (local FLAC files only) | – | – |
| rodio + cpal | `NAudio` + `NAudio.Wasapi` (`WasapiPlayer`; the meta-package only pulls in WASAPI on a Windows TFM, the solution targets plain `net10.0`) | 3.1.0 | Playback |
| libmdns, dns-sd, zbus/avahi | **hand-written minimal mDNS responder** in `Librespot.Discovery/Mdns/` (decision, see package `discovery`). `Makaretu.Dns.Multicast` 0.27.0 exists but is unmaintained since 2020 and misbehaves with multiple NICs on Windows, so it is not used. | – | Discovery |
| hyper, hyper-util, hyper-tls, hyper-proxy2, http, http-body-util, reqwest | `System.Net.Http` (`SocketsHttpHandler`, HTTP/1.1+2, proxy via `WebProxy`) | – | Core, Audio, OAuth, Discovery (server: `HttpListener` or a tiny `TcpListener` HTTP/1.1 parser; see package) |
| tokio-tungstenite | `System.Net.WebSockets.ClientWebSocket` | – | Core (dealer) |
| tokio, futures | `System.Threading.Tasks`, `System.Threading.Channels` (framework) | – | all |
| aes, ctr, hmac, pbkdf2, sha1, rsa, num-bigint | `System.Security.Cryptography`, `System.Numerics` (framework; see 1.7) | – | Core, Audio, Discovery |
| shannon | ported (`Connection/Shannon.cs`) | – | Core |
| oauth2 | hand-written (PKCE = SHA256 + base64url, token endpoint = form POST) | – | OAuth |
| open | `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })` | – | OAuth |
| getopts | hand-written option parser in the CLI (keeps upstream's exact option names/help; `System.CommandLine` is still prerelease and changes the help format) | – | Cli |
| uuid | `Guid` | – | Core, Connect |
| rand, rand_distr | `Random`, `RandomNumberGenerator`; triangular/normal distributions hand-written in `Dither.cs` | – | Playback, Connect |
| time | `DateTimeOffset` | – | Core |
| url, form_urlencoded | `Uri`, `Uri.EscapeDataString`, `System.Web.HttpUtility`-free manual query building | – | Core, Discovery |
| quick-xml | `System.Xml` | – | Core |
| flate2 | `System.IO.Compression.GZipStream` (spclient gzip) | – | Core |
| governor (rate limiter) | hand-written token bucket in `HttpClient.cs` | – | Core |
| sysinfo | `Environment.OSVersion`, `OperatingSystem.*` | – | Core, Cli |
| tempfile | `Path.GetTempFileName()` + `FileOptions.DeleteOnClose` | – | Audio |
| shell-words | hand-written splitter (`subprocess` backend) | – | Playback |
| zerocopy | `MemoryMarshal.AsBytes` | – | Playback |
| priority-queue | `PriorityQueue<TElement,TPriority>` | – | Core (cache) |
| xunit test stack | `xunit` 2.9.3, `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.10.1 | | Tests |

BouncyCastle is **not** needed: everything upstream uses is covered by the framework (see 1.7).

---

## 3. What the skeleton already provides

### 3.1 `Librespot.Protocol`

Exactly the 46 `.proto` files that `protocol/build.rs` compiles, copied to `src/Librespot.Protocol/proto/` with their relative paths, compiled by `Grpc.Tools` (`<Protobuf Include="proto\**\*.proto" ProtoRoot="proto" GrpcServices="None" />`). `google/protobuf/any.proto` / `duration.proto` come from Grpc.Tools' include directory.

Two documented patches (marked `// librespot-dotnet patch:` in the files):

1. `authentication.proto`, `keyexchange.proto`, `mercury.proto`, `pubsub.proto` have no `package`, so protoc would put them in the global namespace. Added `option csharp_namespace = "Librespot.Protocol.<File>";`.
2. `spotify/login5/v3/challenges/{code,hashcash}.proto`: the default namespace `Spotify.Login5.V3.Challenges` collides with the `Challenges` message of `login5.proto` (CS0101). Added `option csharp_namespace = "Spotify.Login5.V3.ChallengeTypes";`.

No proto2 extensions are used by the compiled set; no field/message name collisions occurred.

C# namespace of each Rust module (protoc derives them from `package`):

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

Enum values are PascalCased by protoc with the enum-name prefix stripped (`AUTHENTICATION_USER_PASS` → `AuthenticationType.AuthenticationUserPass`, `ErrorCode::TryAnotherAP` → `ErrorCode.TryAnotherAp`).

`protocol/src/impl_trait/*.rs` → `ImplTrait/Context.cs` (`ContextPage.FromUris/FromTracks`, `Context.ByteHash`) and `ImplTrait/Player.cs` (`PlayerProtoConversions.ToConnectState()/ToPlayerProto()` extension methods).

### 3.2 Fully ported (wave 1, done)

`Librespot.Core`: `Error.cs`, `Config.cs` (`SessionConfig`, `DeviceType`, `Platform`), `Version.cs` (`LibrespotVersion`), `FileId.cs`, `Packet.cs`, `Component.cs` (`SessionComponent` base = the `component!` macro), `SpotifyId.cs`, `SpotifyUri.cs`, `Date.cs`, `DiffieHellman.cs`, `Connection/Shannon.cs`, `Connection/Codec.cs` (`ApCodec`), `Crypto/AesCtr.cs` (AES-128-CTR, tested against NIST SP 800-38A F.5.1), `Util.cs` (`SeqGenerator`, `UrlUtil`), `Authentication.cs` (`Credentials` + JSON, `WithBlob` TODO), `Logging/LibrespotLog.cs`, and the `Session` facade with all data accessors.
Contract-only stubs in `Librespot.Core` (every stub member is tagged `TODO(port: <package-id>)`, which is the authoritative owner): `ApResolve.cs`, `HttpClient.cs`, `Util.cs`/`HashCash` (core-connection); `Dealer/Manager.cs`, `Dealer/Protocol.cs` (`Message`, `PayloadValue`, `MessagePayloadValue`, `FallbackWrapper<T>`), `Dealer/Protocol/Request.cs` (`Request`, `Command` + all option records with `[JsonPropertyName]`) (core-dealer); `Session.cs`, `AudioKey.cs`, `Channel.cs`, `Cache.cs`, `Mercury/Mercury.cs`, `Mercury/Types.cs`, `Mercury/Sender.cs` (core-session); `SpClient.cs`, `Login5.cs`, `Token.cs`, `CdnUrl.cs` (core-spclient).
`Librespot.Playback`: `Lib.cs`, `Config.cs`, `Mixer/Mixer.cs`, `Mixer/Mappings.cs`, `Mixer/SoftMixer.cs`, `Decoder/Decoder.cs` (`AudioPacket`, `IAudioDecoder`), `AudioBackend/AudioBackend.cs` (`ISink`, `ISinkAsBytes`, registry), `Player.cs` (`PlayerEvent`, `PlayerCommand`, `Player` facade, dB helpers), `LocalFile.cs` (`LocalFileLookup` contract).
`Librespot.Connect`: `Model.cs`, `State.cs` (`ConnectConfig`). `Librespot.Metadata`: `Metadata.cs` (`IMetadata`, `MetadataException`), `Audio/File.cs`, `Audio/Item.cs` (shape), `Shapes.cs`. `Librespot.Audio`: `Fetch/Fetch.cs` (`AudioFetchParams` full, rest contract), `Decrypt.cs` (contract). `Librespot.OAuth`: `OAuthError.cs`, `OAuth.cs` (contract). `Librespot.Discovery`: `Discovery.cs` (contract).

Tests: 72 passing (`ShannonTests` with vectors generated from the Rust `shannon` crate, `ApCodecTests`, `AesCtrTests`, `SpotifyIdTests`, `SpotifyUriTests`, `MiscCoreTests`, `VolumeMappingTests`, `ProtocolTests`).

---

## 4. Work packages and waves

Rules:

1. **Depends on** lists packages of strictly **earlier** waves. They are fully ported when the package starts; the porter may read and call their real code.
2. **Same-wave, via contract**: two packages of the same wave touch each other only through members that already exist in the skeleton (section 5). Both agents code against the stub, neither needs the other's code to compile, neither changes the stub's signature. Integration/smoke tests wait until both landed; unit tests must not need the other (inject fakes).
3. **Forward, via contract**: a package may code against stubs of a **later**-wave package when its row says so. The members it may use are enumerated in its section-5 entry and are frozen (signature changes need a plan change first). Such a package cannot be smoke-tested in its own wave; its smoke test is listed as *deferred* in section 5 and is run by the later package's porter as part of that package's verification. Today this applies to `core-dealer` and `metadata` (both wave 2, both need `core-spclient`/`core-session` members that only exist as stubs).
4. Every stub member carries `TODO(port: <package-id>)`; that tag is the authoritative owner of the member, and sections 5/6 agree with it. A porter who finds a tag that contradicts this plan stops and reports instead of implementing it.

| Wave | Package id | Project | Rust lines | Depends on |
|---|---|---|---|---|
| 1 | `foundation` | (all) | 3 253 (done) | – |
| 2 | `core-connection` | Librespot.Core | 1 385 | foundation |
| 2 | `core-dealer` | Librespot.Core | 1 556 | foundation; `core-connection` same-wave via contract (`ApResolver.ResolveAsync("dealer")`, `HttpClient` proxy settings); **forward via contract**: `core-spclient` (`Session.Login5.AuthTokenAsync()` → `Token.AccessToken`) |
| 2 | `oauth` | Librespot.OAuth | 1 009 | foundation |
| 2 | `metadata` | Librespot.Metadata | 2 526 | foundation; **forward via contract**: `core-spclient` (`SpClient.GetTrackMetadataAsync`/`GetEpisodeMetadataAsync`/`GetAlbumMetadataAsync`/`GetArtistMetadataAsync`/`GetShowMetadataAsync`/`GetLyricsAsync`/`GetPlaylistAsync`/`GetMetadataAsync`), `core-session` (`Session.Mercury.GetAsync`) |
| 2 | `playback-pipeline` | Librespot.Playback | 1 064 | foundation |
| 2 | `playback-backends` | Librespot.Playback | 743 real (2 334 incl. skipped) | foundation; `playback-pipeline` same-wave via the `Converter` contract |
| 3 | `core-session` | Librespot.Core | 2 280 | core-connection (upstream `session.rs` calls no `Login5Manager`/`SpClient` member; it only owns their accessors, which already exist on the facade) |
| 3 | `core-spclient` | Librespot.Core | 1 632 | core-connection; `core-session` same-wave via contract (`Session.Mercury.GetAsync` in `TokenProvider`, `Session` data accessors, `Session.Cache`) |
| 3 | `audio-fetch` | Librespot.Audio | 1 505 | foundation (`AesCtr`); `core-session` same-wave via contract (`Session.Cache.File/SaveFile`, `Session.Config.TmpDir`); `core-spclient` same-wave via contract (`CdnUrl.ResolveAudioAsync`/`TryGetUrls`, `SpClient.StreamFromCdnAsync` → `HttpResponseMessage`); `HttpClient.GetRetryAfter` (core-connection, wave 2). Does **not** use `Session.AudioKey` (the player requests keys) or `Session.Channel` (dead code upstream) |
| 3 | `connect-state` | Librespot.Connect | 2 620 | core-dealer (`Request` for `set_last_command`); `core-spclient` same-wave via contract (`SpClient.GetContextAsync`/`GetAutoplayContextAsync`/`GetNextPageAsync`/`PutConnectStateRequestAsync`). No metadata dependency: `connect/src/state/metadata.rs` is connect's own module (upstream `connect/Cargo.toml` depends only on core, playback, protocol) |
| 3 | `discovery` | Librespot.Discovery | 1 118 (+ ~500 new mDNS) | foundation (`AesCtr`, `DhLocalKeys`); core-connection (`Credentials.WithBlob`) |
| 4 | `playback-player` | Librespot.Playback | 2 704 | audio-fetch, metadata, playback-pipeline (`LocalFileLookup`, decoders), playback-backends, core-session (`AudioKey.RequestAsync`) |
| 4 | `connect-spirc` | Librespot.Connect | 1 952 | connect-state, core-dealer, core-spclient, core-session; `playback-player` same-wave via the `Player` facade contract |
| 5 | `cli` | Librespot.Cli | 2 501 | everything |
| 5 | `examples` | examples/Librespot.Examples | 259 | everything |

Total Rust in scope: 29 700 lines (`wc -l` over all 134 `.rs` files of the reference checkout: everything under core/, audio/, metadata/, connect/, discovery/, playback/, oauth/, protocol/, src/, examples/; index in section 6).

---

## 5. Per-package API contracts and verification

For each package: files, target folder, design decisions, the public API other packages rely on, and how to verify. "Contract" members already exist as stubs (`throw new NotImplementedException("TODO(port: id)")`) unless stated otherwise; keep their signatures.

### 5.1 `foundation` (wave 1, done)

Files: `protocol/build.rs`, `protocol/src/lib.rs`, `protocol/src/impl_trait.rs`, `protocol/src/impl_trait/context.rs`, `protocol/src/impl_trait/player.rs`, `core/build.rs`, `core/src/lib.rs`, `core/src/error.rs`, `core/src/config.rs`, `core/src/version.rs`, `core/src/file_id.rs`, `core/src/packet.rs`, `core/src/component.rs`, `core/src/spotify_id.rs`, `core/src/spotify_uri.rs`, `core/src/date.rs`, `core/src/diffie_hellman.rs`, `core/src/connection/codec.rs`, `audio/src/lib.rs`, `metadata/src/lib.rs`, `playback/src/lib.rs`, `playback/src/config.rs`, `playback/src/mixer/mod.rs`, `playback/src/mixer/mappings.rs`, `playback/src/mixer/softmixer.rs`, `connect/src/lib.rs`, `connect/src/model.rs`, `src/lib.rs`, and the `shannon` crate.
Verify: `dotnet test` (66 tests). Nothing to do; later packages may add members to these files.

### 5.2 `core-connection` (wave 2) — `src/Librespot.Core/` (+ `Connection/`)

Files: `core/src/connection/mod.rs` → `Connection/Connection.cs`, `connection/handshake.rs` → `Connection/Handshake.cs`, `proxytunnel.rs` → `ProxyTunnel.cs`, `socket.rs` → `Socket.cs`, `authentication.rs` → `Authentication.cs` (`Credentials.WithBlob`), `http_client.rs` → `HttpClient.cs` (exists, stubs tagged `core-connection`), `apresolve.rs` → `ApResolve.cs` (exists, tagged `core-connection`), `util.rs` → `Util.cs` (exists; `HashCash.Solve` tagged `core-connection`, `CancelOnDrop`/`TimeoutOnDrop` → `TaskGuard` helpers). (`deserialize_with.rs` belongs to `core-dealer`, its only consumer.)

Design: `Transport` wraps a `TcpClient`/`NetworkStream` + `ApCodec`, reads through `System.IO.Pipelines.PipeReader` (`TryDecode(ref ReadOnlySequence<byte>)` is designed for it) and writes with a `SemaphoreSlim` so `SendAsync` is serialized. Handshake: `ClientHello` (product/platform/version from `LibrespotVersion`/`Platform`, `DhLocalKeys.Random()`), `APResponseMessage`, RSA-SHA1 verify with the embedded `SERVER_KEY`, HMAC-SHA1 key material → `ApCodec(sendKey, recvKey)`, `ClientResponsePlaintext`. `authenticate`: `ClientResponseEncrypted` (`SystemInfo` with `Platform.Os`/`OsVersion`, device id, version string) → `APWelcome` → `Credentials` with `AuthenticationStoredSpotifyCredentials`, or `AuthFailure` → `ApAuthenticationException.LoginFailed(code)`. Proxy: `ProxyTunnel` issues `CONNECT host:port` over the proxy socket (HTTP/1.1 parse of the response, 200 only). `HttpClient`: `SocketsHttpHandler` (HTTP/2 allowed, proxy, `Accept-Encoding`), user agent `Spotify/{SpotifyVersion} {Os}/{OsVersion} (librespot)` (fallback constant), token-bucket rate limiter (300 calls / 30 s, max wait 10 s), 429/5xx → `HttpClientException` with `RetryAfter`. `ApResolver`: GET `https://apresolve.spotify.com/?type=accesspoint&type=dealer&type=spclient` into `ApResolveData` (JSON), fallback hosts, `PortConfig()` filtering, round-robin `Queue<SocketAddress>` per type.

Contract (exists):
```csharp
namespace Librespot.Core.Connection;
sealed class Transport : IAsyncDisposable { ValueTask SendAsync(PacketType cmd, ReadOnlyMemory<byte> payload, CancellationToken); ValueTask<ApPacket?> ReceiveAsync(CancellationToken); }
static class ApConnection { Task<Transport> ConnectWithRetryAsync(string host, ushort port, Uri? proxy, byte maxRetries, CancellationToken); Task<Transport> ConnectAsync(string host, ushort port, Uri? proxy, CancellationToken); Task<Credentials> AuthenticateAsync(Transport, Credentials, string deviceId, CancellationToken); }
static class Handshake { Task<Transport> PerformAsync(TcpClient client, CancellationToken); }
sealed class ApAuthenticationException : LibrespotException { Reason Cause; ErrorCode? ErrorCode; }
namespace Librespot.Core;
sealed class HttpClient { HttpClient(Uri? proxy); System.Net.Http.HttpClient Inner; Task<HttpResponseMessage> RequestAsync(HttpRequestMessage, CancellationToken); Task<byte[]> RequestBodyAsync(HttpRequestMessage, CancellationToken); Task<HttpResponseMessage> RequestStreamAsync(HttpRequestMessage, CancellationToken); /* ResponseHeadersRead, non-2xx returned not thrown */ static TimeSpan? GetRetryAfter(HttpResponseHeaders); }
sealed class ApResolver : SessionComponent { ushort? PortConfig(); Task<SocketAddress> ResolveAsync(string endpoint, CancellationToken); }   // endpoint: "accesspoint" | "dealer" | "spclient"
readonly record struct SocketAddress(string Host, ushort Port);
record Credentials { static Credentials WithBlob(string username, ReadOnlySpan<byte> encryptedBlob, ReadOnlySpan<byte> deviceId); }
static class HashCash { static void Solve(ReadOnlySpan<byte> ctx, ReadOnlySpan<byte> prefix, int length, Span<byte> dst); }
```
Verify: port the `#[cfg(test)]` tests in `handshake.rs` (key derivation / `solve_hash_cash` vectors) and add a test for `Credentials.WithBlob` using a blob produced by encrypting a known plaintext with the same key derivation (write the encrypt side in the test). Smoke: `Session`-less `ApConnection.ConnectWithRetryAsync("ap.spotify.com", 4070, null, 1)` completes the handshake (network test, skipped by default); `ApResolver.ResolveAsync("accesspoint")` returns a host:port.

### 5.3 `core-dealer` (wave 2) — `src/Librespot.Core/Dealer/`

Files: `core/src/dealer/mod.rs` → `Dealer/Dealer.cs`, `dealer/manager.rs` → `Dealer/Manager.cs` (exists, keep the name), `dealer/maps.rs` → `Dealer/Maps.cs`, `dealer/protocol.rs` → `Dealer/Protocol.cs` (exists: public types), `dealer/protocol/request.rs` → `Dealer/Protocol/Request.cs` (exists: all DTO records), `core/src/deserialize_with.rs` → `DeserializeWith.cs` (the `JsonConverter<T>` helpers `Base64Proto`, `JsonProto`, `OptionJsonProto`, `VecJsonProto`, `BoolFromString`; dealer is their only consumer).

**All of these files are in namespace `Librespot.Core.Dealer`** (`DeserializeWith.cs` in `Librespot.Core`); there is no `Librespot.Core.Dealer.Protocol` namespace (section 1.1).

Design: `ClientWebSocket` to `wss://{host}:{port}/?access_token={token}` (host/port from `ApResolver.ResolveAsync("dealer")` – core-connection, same wave; token from `Session.Login5.AuthTokenAsync()` – core-spclient, **forward via contract**: the only member of core-spclient this package may touch, do not change its signature), ping every 30 s, reconnect with backoff, `Builder` → `Dealer` with handler map (`HandlerMap` = trie keyed by uri segments, `SubscriberMap` likewise), `Responder` that must reply once (`Send`/`ForceUnanswered`; upstream replies "unanswered" on drop → do it in `Dispose`). Messages are JSON (`type`: `message` | `request` | `ping` | `pong`); the private envelopes `WebsocketMessage`/`WebsocketRequest`/`MessageOrRequest`/`Payload` (`message_ident`, `key`, `headers`, `payload.compressed`) stay `internal` – the *public* `Request` is what the request envelope's base64(+gzip) payload decodes to. Message payloads are base64 (possibly gzip) protobufs or JSON (`handle_transfer_encoding`). `Message.TryFromJson<M>()` / `FromRaw<M>()` (stubs exist) parse protobufs with `JsonParser` (ignore unknown fields; on failure return `FallbackWrapper<M>.Fallback(JsonElement)`) / `M.Parser.ParseFrom`. `Command` is the `endpoint`-tagged JSON union → write the `JsonConverter<Command>` (`Unknown` = untagged fallback keeping the raw `JsonElement`) and the converters for the proto-typed properties marked in `Request.cs`; add `Command.ToDisplayString()`.

Contract (exists – do not change signatures, fill in the bodies):
```csharp
namespace Librespot.Core.Dealer;
abstract record MessagePayloadValue { String(string) | Bytes(byte[]) | Json(JsonElement) }     // raw websocket payload element
abstract record PayloadValue { Empty() | Raw(byte[]) | Json(string) }                           // decoded Message payload
abstract record FallbackWrapper<T> where T : IMessage<T> { Inner(T) | Fallback(JsonElement) }
sealed record Message { IReadOnlyDictionary<string,string> Headers; PayloadValue Payload; string Uri;
    static FallbackWrapper<M> TryFromJson<M>(Message); static M FromRaw<M>(Message); }
sealed record Request { uint MessageId; string SentByDeviceId; Command Command; }               // upstream protocol::Request
abstract record Command { Transfer(TransferCommand) | Play(PlayCommand) | Pause(PauseCommand) | SeekTo(SeekToCommand) | SetShufflingContext(SetValueCommand) | SetRepeatingTrack(SetValueCommand) | SetRepeatingContext(SetValueCommand) | AddToQueue(AddToQueueCommand) | SetQueue(SetQueueCommand) | SetOptions(SetOptionsCommand) | UpdateContext(UpdateContextCommand) | SkipNext(SkipNextCommand) | SkipPrev(GenericCommand) | Resume(GenericCommand) | Unknown(JsonElement); string ToDisplayString(); }
// + TransferCommand, PlayCommand, PauseCommand, SeekToCommand, SkipNextCommand, SetValueCommand, AddToQueueCommand, SetQueueCommand,
//   SetOptionsCommand, UpdateContextCommand, GenericCommand, TransferOptions, PlayOptions, OptionsOptions, SkipTo, LoggingParams
//   with exactly upstream's fields (see Request.cs; proto-typed fields use the generated message types).
enum Reply { Success, Failure, Unanswered }
sealed record RequestReply(Request Request, ChannelWriter<Reply> ReplyTo);
readonly record struct Result<T>(T? Value, LibrespotException? Error) { bool IsOk; T Unwrap(); }   // stream element = upstream Result<T, Error>
sealed class DealerManager : SessionComponent {
    Task StartAsync(CancellationToken);
    IAsyncEnumerable<Message> AddListenFor(string url);                                 // upstream add_listen_for -> Subscription
    IAsyncEnumerable<Result<T>> ListenFor<T>(string uri, Func<Message, T> parse);       // upstream listen_for(uri, fn(Message)->Result<T>)
    ChannelReader<RequestReply> AddHandleFor(string url);                               // upstream add_handle_for
    ChannelReader<RequestReply> HandleFor(string uri);                                  // upstream handle_for (same receiver)
    bool Handles(string uri);
    Task CloseAsync(); }
```
`connect-spirc` calls `ListenFor("hm://connect-state/v1/cluster", Message.FromRaw<ClusterUpdate>)`, `ListenFor("social-connect/v2/session_update", Message.TryFromJson<SessionUpdate>)` (→ `FallbackWrapper<SessionUpdate>`), `ListenFor("hm://pusher/v1/connections/", extractConnectionId)` and `HandleFor("hm://connect-state/v1/player/command")`; `connect-state` reads `Request.MessageId` / `SentByDeviceId` (`set_last_command`) and `Request.Command`.

Verify: upstream has no tests; write tests that deserialize a `request` and a `message` sample JSON (shapes from `protocol.rs`: `{"type":"request","message_ident":..,"key":..,"headers":{..},"payload":{"compressed":"<base64 gzip json>"}}` whose inner JSON is `{"message_id":1,"sent_by_device_id":"x","command":{"endpoint":"play",...}}`) into `Request`/`Command` variants including `Unknown`, plus `HandlerMap`/`SubscriberMap` matching rules with the upstream `maps.rs` semantics (longest-prefix handler, subscriber by prefix). **Deferred smoke** (run by the `core-spclient` porter, wave 3, with `core-session` landed): `DealerManager.StartAsync` connects and `AddListenFor("hm://pusher/v1/connections/")` yields the connection id message.

### 5.4 `oauth` (wave 2) — `src/Librespot.OAuth/`

Files: `oauth/src/lib.rs` → `OAuth.cs` (+ `OAuthError.cs` exists), `oauth/examples/device_auth.rs`, `oauth_async.rs`, `oauth_sync.rs` → sub-commands `device-auth`, `oauth-async`, `oauth-sync` in `examples/Librespot.Examples/Examples/`.

Design: no oauth2 crate → hand-written: PKCE verifier (43–128 random url-safe chars) + S256 challenge, authorize URL with `response_type=code&client_id&redirect_uri&code_challenge&scope&state`, local `TcpListener` on the redirect URI's host:port answering one HTTP request (`get_authcode_listener`) or stdin (`get_authcode_stdin`), token exchange = `application/x-www-form-urlencoded` POST to `TOKEN_URL`; device flow = POST to `DEVICE_AUTHORIZATION_URL` then poll with `grant_type=urn:ietf:params:oauth:grant-type:device_code` honouring `interval`, `slow_down`, `authorization_pending`, `expired_token`. Use one `System.Net.Http.HttpClient` (system proxy) per client. Open browser via `Process.Start`.

Contract (exists): `OAuthToken`, `OAuthClient{Builder}`, `OAuthFlow.GetAccessToken`, `DeviceAuthorization`, `DeviceAuthClient{Builder}`; all `*Async` methods take a `CancellationToken`. `LibrespotException.From(OAuthException)` already maps kinds.

Verify: port the `#[cfg(test)]` tests in `oauth/src/lib.rs` (`get_code` parsing of redirect URLs, builder validation). Add a test that runs the listener against a loopback `HttpClient` GET `/?code=abc&state=...`. Smoke: `Librespot.Examples oauth-sync` prints a token (manual, needs a browser).

### 5.5 `metadata` (wave 2) — `src/Librespot.Metadata/`

Files: every file under `metadata/src/` except `lib.rs` (28 files; `album.rs`, `artist.rs`, `audio/file.rs` (exists), `audio/item.rs` (shape exists), `audio/mod.rs`, `availability.rs`, `content_rating.rs`, `copyright.rs`, `episode.rs`, `error.rs` (exists in `Metadata.cs`), `external_id.rs`, `image.rs`, `lyrics.rs`, `playlist/{annotation,attribute,diff,item,list,mod,operation,permission}.rs`, `request.rs`, `restriction.rs`, `sale_period.rs`, `show.rs`, `track.rs`, `util.rs`, `video.rs`). Move the placeholder types out of `Shapes.cs` into their real files (`Artist.cs`, `Track.cs`, `Availability.cs`, `Image.cs`) and delete `Shapes.cs`; **the type names and the namespace `Librespot.Metadata` of `Tracks`, `ArtistsWithRole`, `ArtistWithRole`, `UnavailabilityReason`, `ImageSize` must not change** – `Librespot.Playback/Player.cs` and `Audio/Item.cs` (foundation) reference them.

Name collisions: every one of `Track`, `Album`, `Artist`, `Episode`, `Show`, `Image`, `Restriction`, `SalePeriod`, `Copyright`, `ExternalId`, `ContentRating`, `Availability`, `VideoFile` collides with a `Spotify.Metadata.*` message. Use the `Proto`-prefixed alias convention from section 1.1 in every file (`using ProtoTrack = Spotify.Metadata.Track;`), never `using Spotify.Metadata;`.

Forward dependency (rule 3 in section 4): the only `core-spclient` / `core-session` members this package may call are `Session.SpClient.GetTrackMetadataAsync`, `GetEpisodeMetadataAsync`, `GetAlbumMetadataAsync`, `GetArtistMetadataAsync`, `GetShowMetadataAsync`, `GetLyricsAsync`, `GetLyricsForImageAsync(SpotifyId trackId, FileId imageId, ct)`, `GetPlaylistAsync`, `GetMetadataAsync(ExtensionKind, SpotifyUri)` and `Session.Mercury.GetAsync(uri)` (→ `MercuryResponse.Payload[0]`). Their signatures are frozen.

Design: each metadata type is a `sealed record` implementing `IMetadata<TSelf, TMessage>` with `static RequestAsync` calling `session.SpClient.Get*MetadataAsync(uri)` (or `MercuryManager.GetAsync("hm://...")` for the `MercuryRequest` trait: playlists, lyrics) and `static Parse(TMessage, SpotifyUri)`. The `impl_deref_wrapped!`/`impl_from_repeated!`/`impl_try_from_repeated!` macros → list wrapper classes deriving from `List<T>` with a constructor from `IEnumerable<TMessage>` (pattern in `Shapes.cs`). `AudioItem.GetFileAsync` implements the Track/Episode/Local branches (local: `UniqueFields.Local` from the `spotify:local:` URI only; the actual file lookup is in `playback-pipeline`'s `LocalFile.cs`). `Availability` logic (`available_in_country`, catalogue checks) and `MetadataException.ExplicitContentFiltered` exactly as upstream.

Contract (exists): `IMetadata<TSelf,TMessage>`, `MetadataException`, `AudioItem`/`CoverImage`/`UniqueFields`, `AudioFileFormat`/`AudioFiles`, `ArtistWithRole`/`ArtistsWithRole`, `Tracks`, `UnavailabilityReason`. Add (names fixed): `Track`, `Album`, `Artist`, `Episode`, `Show`, `Playlist`, `Lyrics`, `Image`/`Images`, `Availability`/`Availabilities`, `Restriction`/`Restrictions`, `SalePeriod`, `ExternalId`, `Copyright`, `ContentRating`, `VideoFiles`, `PlaylistAnnotation`, `PlaylistAttributes`, `PlaylistItem`, `PlaylistOperation`, `Capabilities`/`PermissionLevels`, `PlaylistDiff`.

Verify: there are no upstream unit tests; write parse tests feeding hand-built `Spotify.Metadata.Track`/`Album` messages (incl. a track with `alternative`, with restrictions for country "XX", with 16-byte file ids) and check `Parse`, availability and `AudioFiles` mapping. **Deferred smoke** (run by the `core-spclient` porter, wave 3; network, skipped by default): `Track.GetAsync(session, "spotify:track:4GNcXTGWmnZ3ySrqvol3o4")` returns the name.

### 5.6 `playback-pipeline` (wave 2) — `src/Librespot.Playback/`

Files: `playback/src/convert.rs` → `Convert.cs`, `dither.rs` → `Dither.cs`, `decoder/mod.rs` → `Decoder/Decoder.cs` (exists), `decoder/symphonia_decoder.rs` → `Decoder/VorbisDecoder.cs` + `Decoder/Mp3Decoder.cs` (note the file rename: no symphonia in .NET; the `SymphoniaDecoder` facade that picks a decoder by MIME becomes `Decoder/AudioDecoders.cs` with `static IAudioDecoder Create(Stream, string? mime, ...)`), `decoder/passthrough_decoder.rs` → `Decoder/PassthroughDecoder.cs`, `symphonia_util.rs` → `Decoder/DecoderUtil.cs`, `local_file.rs` → `LocalFile.cs`.

Design: `Converter` keeps upstream's scale/dither math bit-for-bit (`SHIFT_S16=15`, `SHIFT_S24=23`, `SHIFT_S32=31`, clamp bounds, dither at 16-bit level then scale); `F64ToS24_3` returns packed little-endian 3-byte samples (`i24`). Ditherers: triangular = sum of two uniforms in [-0.5,0.5), gaussian = Box-Muller with σ = 0.5, high-pass = triangular differenced with the previous noise. Decoders: NVorbis `VorbisReader` over the `Stream` (must support `Seek`; `AudioDecrypt`/`AudioFile` are seekable), output interleaved stereo f64 at 44.1 kHz; NLayer `MpegFile` for MP3. `Seek(positionMs)` returns the actual position; `NextPacket()` returns `(AudioPacketPosition, AudioPacket.Samples)` of ~`SAMPLES_PER_SECOND / 10` samples; upstream's symphonia-specific `skipped` flag semantics after seek must be preserved (the first packet after a seek reports `Skipped = true` when the decoder landed before the target). Passthrough: raw Ogg pages with the Spotify header stripped (`Player.SpotifyOggHeaderEnd`), granule-position based `PositionMs`. `LocalFile`: `LocalFileLookup` indexing `PlayerConfig.LocalFileDirectories`, matching `spotify:local:` URIs by tag metadata (use NVorbis/NLayer tags; ID3 via NLayer's `Id3` support – keep it minimal, document unsupported formats).

Contract (exists): `Converter` (`F64ToF32/S32/S24/S24_3/S16`), `IDitherer`/`DithererBuilder`/`Dither.MakeDitherer<T>()`/`Dither.FindDitherer(name)`, `TriangularDitherer`/`GaussianDitherer`/`HighPassDitherer`, `AudioPacket`, `AudioPacketPosition`, `IAudioDecoder`, `DecoderException`, and in `LocalFile.cs` (mirrors upstream exactly: `LocalFileLookup(HashMap<SpotifyUri, PathBuf>)` + `get(&SpotifyUri) -> Option<&Path>` + free fn `create_local_file_lookup(&[PathBuf])`; `player.rs:1260` calls `get` with a general `SpotifyUri`):
```csharp
namespace Librespot.Playback;
sealed class LocalFileLookup { LocalFileLookup(); string? Get(SpotifyUri uri); static LocalFileLookup Create(IReadOnlyList<string> directories); }
```
Add: `static class AudioDecoders { static IAudioDecoder Create(Stream stream, string? mimeType, bool passthrough); }` and a `LocalFileMetadata` record (`Name`, `Artists`, `Album`, `AlbumArtists`, `Number`, `DiscNumber`, `Language`, `DurationMs`) returned by `IAudioDecoder.LocalFileMetadata()` for local files.

Verify: tests for `Converter` with known inputs (0.0, ±1.0, ±0.5, out-of-range clamps, dithered vs not), ditherer statistics (mean ≈ 0, bounded range), decoding a small generated Ogg Vorbis file (generate with NVorbis? no encoder – check in a tiny `.ogg` test asset under `tests/Librespot.Tests/Assets/`, < 50 kB, and assert sample count/rate/channels), `FindDitherer("tpdf_hp")`.

### 5.7 `playback-backends` (wave 2) — `src/Librespot.Playback/AudioBackend/`

Files: `playback/src/audio_backend/mod.rs` → `AudioBackend/AudioBackend.cs` (exists; register the real builders), `pipe.rs` → `AudioBackend/Pipe.cs` (`StdoutSink`: stdout or a file path, `ISinkAsBytes`), `subprocess.rs` → `AudioBackend/Subprocess.cs` (`SubprocessSink`: `shell-words` split → `Process` with redirected stdin, restart on `Start`), `rodio.rs` → `AudioBackend/NAudio.cs` (`NAudioSink`: `WasapiPlayer` (`WasapiPlayerBuilder`, shared mode; `WasapiOut` is `[Obsolete]` in NAudio 3.1) fed by an f32 `BufferedWaveProvider` like rodio's queue, opened in the sink's `AudioFormat` (F64/F32 IEEE, S32/S24/S16 PCM; S24 and S24_3 both map to 24-bit like rodio's `I24`); unsupported exact formats fall back to the device mix format with a warning, like rodio. Device selection by friendly name, back-pressure: block in `Write` while the buffer holds > ~500 ms (upstream: 26 queued chunks, its "half second buffer")).
**skip: Linux-only; implement only the NAudio/WASAPI, pipe, and subprocess backends**: `alsa.rs`, `gstreamer.rs`, `jackaudio.rs`, `portaudio.rs`, `pulseaudio.rs`, `sdl.rs`, `mixer/alsamixer.rs`. Do not create files for them; the registry comment lists them as not ported.

Design: sinks are created on the player thread by the `SinkBuilder`; for pipe/subprocess, `Start`/`Stop` open/close the output (close the file / kill the process) so pausing releases it (`SinkStatus.TemporarilyClosed`). `NAudioSink` follows rodio semantics instead: it opens the device and starts playing on creation, keeps it open, and `Stop` drains the queue and pauses. Uses `Converter` (same wave, contract exists) only through `SinkAsBytesExtensions.WriteAsBytes`.

Contract (exists): `ISink`, `ISinkAsBytes`, `SinkBuilder`, `AudioBackends.Backends`/`Find`, `SinkException`. Registry names: `"naudio"` (default), `"pipe"`, `"subprocess"`.

Verify: `StdoutSink` writing to a temp file path produces the expected byte layout for S16 input (use a fake `Converter`? `Converter` is a stub until `playback-pipeline` lands – test with `AudioPacket.Raw` which bypasses conversion); `SubprocessSink` with `cmd /c more > file` (Windows) / `cat > file` receives bytes; NAudio sink: `[Fact(Skip="audio device")]` that opens the default device and writes 100 ms of silence.

### 5.8 `core-session` (wave 3) — `src/Librespot.Core/`

Files: `core/src/session.rs` → `Session.cs` (exists; implement `ConnectAsync`, `SendPacket`, dispatch, keep-alive), `channel.rs` → `Channel.cs` (exists), `audio_key.rs` → `AudioKey.cs` (exists), `mercury/mod.rs` → `Mercury/Mercury.cs` (exists), `mercury/sender.rs` → `Mercury/Sender.cs` (exists), `mercury/types.rs` → `Mercury/Types.cs` (exists), `cache.rs` → `Cache.cs` (exists, tagged `core-session`), `core/tests/connect.rs` → `tests/Librespot.Tests/Core/ConnectTests.cs`. No file renames: the Mercury split already exists in the skeleton.

Design: `ConnectAsync` = the upstream loop (`MAX_AP_TRIES = 6`, retry on `TryAnotherAp` or transport errors, abort on other `LoginFailed`), then store credentials in `Cache` when changed, then start (a) the sender task: `Channel<(byte, ReadOnlyMemory<byte>)>` drained into `Transport.SendAsync`, (b) the dispatch task: `Transport.ReceiveAsync` loop routing by `PacketType` (Ping → time delta + `PendingPong`; `PongAck`; `CountryCode`; `StreamChunkRes`/`ChannelError` → `Channel.Dispatch`; `AesKey`/`AesKeyError` → `AudioKey.Dispatch`; Mercury* → `Mercury.Dispatch`; `ProductInfo` → XML → `ReplaceUserAttributes`; ignore `SecretBlock`, `LegacyWelcome`, `UnknownDataAllZeros`, `LicenseVersion`), with the keep-alive state machine and timeouts (`INITIAL_PING_TIMEOUT 20 s`, `PING_TIMEOUT 80 s`, `PONG_DELAY 60 s`, `PONG_ACK_TIMEOUT 20 s`) – implement with a `CancellationTokenSource`-reset `Task.Delay` raced against `ReceiveAsync`. On stream end/error/timeout: `Shutdown()`. Non-premium account: upstream calls `exit(1)`; the port raises `Session.UnsupportedAccountType` and the CLI exits. `AudioKeyManager`: `RequestKey` packet = `file_id(20) | track gid(16) | seq(u32 BE) | 0x0000`, pending `Dictionary<uint, TaskCompletionSource<AudioKey>>`, 1.5 s timeout; its only caller is `playback-player` (`player.rs:1111`). `ChannelManager` (**dead code upstream**: only constructed in `session.rs:109` and fed by the dispatch task; no crate calls `allocate()` since audio moved to the CDN): port it minimally and faithfully – `Allocate`, `Dispatch` (parse `StreamChunkRes`: channel id, header blocks until a zero-length header, then data, into `ChannelEvent`s), `GetDownloadRateEstimate`, `Shutdown` – but budget no integration work and expect no consumer (`audio-fetch` does not use it). `MercuryManager`: `MercuryRequest.Encode(seq)` exactly as upstream (`seq len u16 | seq | flags 0x01 | part count u16 | header len u16 | Header proto | parts`), 4-byte sequence, partial-response reassembly (`MercuryPending`), subscriptions `Dictionary<string, List<ChannelWriter<MercuryResponse>>>`, `MercurySender` as a `ChannelWriter`-like class. `Cache`: `credentials.json` (the `Credentials` JSON), `volume` (text), `files/<2 hex>/<rest>` with `SizeLimiter` (LRU by mtime, `PriorityQueue<string, DateTime>`), unix permission bits only on Unix (`File.SetUnixFileMode`).

Contract (exists): everything in `Session.cs`, `AudioKey.cs`, `Channel.cs`, `Mercury/Mercury.cs`, `Mercury/Types.cs`, `Mercury/Sender.cs`, `Cache.cs`. Consumers: `core-spclient` (same wave) calls `Session.Mercury.GetAsync(...)` (keymaster token), `Session.ApResolver`, `Session.HttpClient`, `Session.Cache` and the session data accessors; `audio-fetch` (same wave) calls `Session.Cache.File`/`SaveFile` and reads `Session.Config.TmpDir`; `metadata` (wave 2, forward) calls `Session.Mercury.GetAsync`; `playback-player` (wave 4) calls `Session.AudioKey.RequestAsync`. Nobody calls `Session.Channel`.

Verify: port `cache.rs` `#[cfg(test)]` (SizeLimiter tests) and `core/tests/connect.rs` (`test_connection`: bad credentials must fail with a non-empty message; network, skipped by default). Unit-test the dispatch routing with a fake `Transport` (make `Transport` constructible from any `Stream` pair for tests) feeding a `Ping` and a `ProductInfo` XML and asserting `TimeDelta`/`UserData`. Smoke: `librespot` CLI (later) logs `Authenticated as '<user>' !` and `Country: "XX"`.

### 5.9 `core-spclient` (wave 3) — `src/Librespot.Core/`

Files: `core/src/spclient.rs` → `SpClient.cs`, `login5.rs` → `Login5.cs`, `token.rs` → `Token.cs`, `cdn_url.rs` → `CdnUrl.cs`.

Design: `SpClient`: base URL from `ApResolver.ResolveAsync("spclient")`, headers `Authorization: Bearer <login5 token>`, `client-token` (from the client-token endpoint `https://clienttoken.spotify.com/v1/clienttoken` with protobuf `ClientTokenRequest`, solving the hashcash challenge via `HashCash.Solve`), `Spotify-App-Version`, `App-Platform`, gzip; `RequestStrategy` retries (`TryTimes` 3 attempts, `Infinitely` with backoff) and 429 `Retry-After`; all endpoint helpers as listed in the contract; `StreamFromCdnAsync(string cdnUrl, long offset, long length)` = GET of that URL with `Range: bytes={offset}-{offset+length-1}` via `HttpClient.RequestStreamAsync` (`ResponseHeadersRead`), returning the **`HttpResponseMessage`** unchanged – audio-fetch needs the status (206 vs 429), `Content-Range` and the body stream, so non-2xx must be returned, not thrown (upstream returns the raw `ResponseFuture` stream). `Login5Manager`: `LoginRequest` with `ClientInfo{client_id, device_id}` + `StoredCredential`/`Password`/`OneTimeToken`, `HashcashChallenge` solving (`LoginRequest.challenge_solutions`), retries (`MAX_LOGIN_TRIES = 3`), caches the `Token`; `login()` is only valid for android/ios client ids (upstream `OnlyForMobile`). `TokenProvider`: keymaster via mercury `hm://keymaster/token/authenticated?scope=...&client_id=...&device_id=...`, JSON → `Token.FromJson`. `CdnUrl.ResolveAudioAsync`: `SpClient.GetAudioStorageAsync(fileId)` → `StorageResolveResponse` (CDN only), expiry parsed from the URL query (`__token__=...~exp=...~` or `verify=...-...`) – port the regex-free parsing and its tests.

Contract (exists): all public members of `SpClient`, `Login5Manager`, `TokenProvider`, `Token`, `CdnUrl`, `MaybeExpiringUrl`, the exception types. `metadata` (wave 2, forward), `core-dealer` (wave 2, forward: `Login5Manager.AuthTokenAsync`), `connect-state`, `audio-fetch` (same wave: `CdnUrl.ResolveAudioAsync`/`TryGetUrls`, `SpClient.StreamFromCdnAsync`) and `connect-spirc` consume these. Signatures are frozen:
```csharp
Task<HttpResponseMessage> SpClient.StreamFromCdnAsync(string cdnUrl, long offset, long length, CancellationToken ct = default);
Task<Token> Login5Manager.AuthTokenAsync(CancellationToken ct = default);   // Token.AccessToken
Task<CdnUrl> CdnUrl.ResolveAudioAsync(Session session, CancellationToken ct = default); IReadOnlyList<string> CdnUrl.TryGetUrls();
```

Verify: port `cdn_url.rs` `#[cfg(test)]` (URL expiry parsing). Unit-test `Token.FromJson` and `IsExpired`, the client-token hashcash against a known challenge (take the vector from `util.rs` tests if present, else construct one with a small `length`). Smoke (network): `SpClient.GetTrackMetadataAsync(uri)` returns a parsable `Spotify.Metadata.Track`. **Also run the deferred smoke tests of `core-dealer` (5.3) and `metadata` (5.5)** – they are the first end-to-end check of the forward contracts.

### 5.10 `audio-fetch` (wave 3) — `src/Librespot.Audio/`

Files: `audio/src/decrypt.rs` → `Decrypt.cs` (exists), `fetch/mod.rs` → `Fetch/Fetch.cs` (exists), `fetch/receive.rs` → `Fetch/Receive.cs`, `range_set.rs` → `RangeSet.cs`. AES-CTR is **already provided** by `Librespot.Core.Crypto.AesCtr` (foundation, tested) – do not create another one.

Design: `AudioFile.OpenAsync`: cache hit (`Session.Cache.File(fileId)`) → `CachedAudioFile` (a `FileStream`); else `AudioFileStreaming`: `new CdnUrl(fileId).ResolveAudioAsync(session)`, then for each url of `TryGetUrls()` call `Session.SpClient.StreamFromCdnAsync(url, 0, AudioFetchParams.MinimumDownloadSize)` with a 10 s timeout and take the first `HttpResponseMessage` with status 206; read the file size from its `Content-Range` header (`"bytes a-b/size"`, upstream `fetch/mod.rs:458-493`), hand the response (its body is the first chunk) to the fetch task. Temp file in `Session.Config.TmpDir` (`FileOptions.DeleteOnClose`) written by the fetch task (`AudioFileFetch` in `Receive.cs`: `StreamLoaderCommand` channel, `RangeSet` of downloaded bytes guarded by a lock + `ManualResetEventSlim`/`TaskCompletionSource` "data available" signal, ping-time and throughput estimates, read-ahead/prefetch policy exactly as upstream; each further range request is another `StreamFromCdnAsync` whose status is checked: 206 → body, 429 → `HttpClient.GetRetryAfter(response.Headers)` sleep, else `AudioFileException.StatusCode` – `receive.rs:84-98`). On completion the whole file is `Session.Cache.SaveFile`d. `Read` blocks (sync, it is called from the decoder on the player thread) until the range is present, `Seek` switches to random-access mode. `AudioDecrypt`: `AesCtr` with key = the `AudioKey` and `AUDIO_AESIV = 72 e0 67 fb dd cb cf 77 eb e8 bc 64 3f 63 0d 93`; `Seek` sets `AesCtr.Position` (the helper handles the intra-block offset); identity when the key is null (local files). The key itself is obtained by `playback-player` (`Session.AudioKey.RequestAsync`) and passed in; this package does not call `AudioKeyManager` or `ChannelManager`.

Contract (exists): `AudioFile` (abstract `Stream` + `OpenAsync`, `GetStreamLoaderController`, `IsCached`), `StreamLoaderController` (all methods), `AudioFetchParams`, `AudioDecrypt`, `AudioFileException`. `playback-player` uses them through these signatures only.

Verify: port `range_set.rs` tests if any (none upstream – write them: add/subtract/contains/intersection on overlapping ranges). (`AesCtr` is already tested in `AesCtrTests`.) `AudioDecrypt.Seek` into the middle of a block then `Read` matches `AesCtr.Transform` of the whole buffer. Fetch logic: give `AudioFileStreaming` an `internal` constructor taking a `Func<string url, long offset, long length, CancellationToken, Task<HttpResponseMessage>>` (production: `Session.SpClient.StreamFromCdnAsync`) and serve a local byte array with status 206 + `Content-Range` from the fake; assert that `Read` of a not-yet-downloaded range blocks and completes, and that a 429 with `Retry-After` is retried. No test may need the real `SpClient`/`Session` connection.

### 5.11 `connect-state` (wave 3) — `src/Librespot.Connect/` (+ `State/`)

Files: `connect/src/state.rs` → `State.cs` (exists with `ConnectConfig`; add `ConnectState`), `state/context.rs` → `State/Context.cs`, `state/handle.rs` → `State/Handle.cs`, `state/metadata.rs` → `State/Metadata.cs` (connect's **own** `Metadata` trait for `ProvidedTrack` metadata keys – the `metadata::Metadata` imported by `state/{context,handle,options,tracks,transfer}.rs` is this module, not the `librespot-metadata` crate; this package has no dependency on package `metadata`), `state/options.rs` → `State/Options.cs`, `state/provider.rs` → `State/Provider.cs`, `state/restrictions.rs` → `State/Restrictions.cs`, `state/tracks.rs` → `State/Tracks.cs`, `state/transfer.rs` → `State/Transfer.cs`, `context_resolver.rs` → `ContextResolver.cs`, `shuffle_vec.rs` → `ShuffleVec.cs`.

Design: `ConnectState` owns the `PutStateRequest` (`Spotify.Connectstate`) and the current/prev/next track lists (`ProvidedTrack`), queue revision, autoplay context, shuffle seed; `ContextResolver` queues `ResolveContext`s and fetches contexts/pages via `SpClient.GetContextAsync`/`GetAutoplayContextAsync`/`GetNextPageAsync`. `ShuffleVec<T>`: Fisher-Yates with a seedable RNG so `Unshuffle` can restore the original order; upstream uses `SmallRng::seed_from_u64` – the .NET port uses a tiny xoshiro-based `SeededRng` class (do not use `System.Random` – its sequence is unspecified across runtimes) and documents that seeds are not interchangeable with the Rust binary (they are only used in-process and in `transfer_shuffle`). Keep `is_first` semantics (current track stays first).

Contract: internal to the connect project (`internal sealed class ConnectState`), consumed by `connect-spirc`. Public: `ConnectConfig` (exists). Uses from `core-dealer` (wave 2, done): `Librespot.Core.Dealer.Request` (`MessageId`, `SentByDeviceId` in `SetLastCommand`), `TransferOptions`. Fix the internal surface `spirc.rs` uses: `ConnectState.New(ConnectConfig, Session)`, `DeviceInfo`, `Player` (the `PlayerState`), `IsActive`, `IsPlaying`, `IsPause`, `SetVolume`, `SetLastCommand(Request)`, `SetNow`, `SetActive`, `SetOrigin`, `SetSessionId`, `UpdateCurrentIndex`, `UpdatePosition`, `UpdateDuration`, `UpdateQueueRevision`, `ResetPlaybackToPosition`, `UpdatePositionInRelation`, `BecameInactiveAsync`, `NotifyNewDeviceAppearedAsync`, `NotifyVolumeChangedAsync`, `SendStateAsync`, plus everything in `state/*.rs` (`handle_initial_transfer`, `finish_transfer`, `shuffle`, `next_track`, `prev_track`, `add_to_queue`, `set_repeat_*`, `update_restrictions`, `merge_context`, ...).

Verify: port the `shuffle_vec.rs` `#[cfg(test)]` tests (shuffle/unshuffle roundtrip, `is_first`). Unit-test `ConnectState` track navigation (next/prev with repeat/shuffle, queue insertion) with synthetic `ContextPage`s – these are pure in-memory operations.

### 5.12 `discovery` (wave 3) — `src/Librespot.Discovery/` (+ `Mdns/`)

Files: `discovery/src/lib.rs` → `Discovery.cs` (exists), `server.rs` → `Server.cs`, `discovery/examples/discovery.rs`, `discovery_group.rs` → sub-commands `discovery`, `discovery-group` in `examples/`. **skip: Linux-only**: `avahi.rs` (zbus/Avahi backend; registry entry stays `null`). New: `Mdns/MdnsResponder.cs`, `Mdns/DnsPacket.cs` (replaces the `libmdns` crate).

Design: `DiscoveryServer` = HTTP/1.1 on `TcpListener` (port from builder, 0 = ephemeral; `HttpListener` needs URL ACLs on Windows, so use the raw listener with a minimal request parser: method, path, query string, `application/x-www-form-urlencoded` POST body) answering `GET /?action=getInfo` (JSON `GetInfoResponse` with `deviceID`, `remoteName`, `publicKey` (base64 DH public key), `deviceType`, `brandDisplayName`, `modelDisplayName`, `clientID`, `groupStatus`, `availability`, `supported_capabilities`, `aliases`, ...) and `POST /?action=addUser` (`userName`, `blob`, `clientKey`: shared key = DH, `base_key = SHA1(shared)[..16]`, `checksum_key = HMAC-SHA1(base_key, "checksum")`, `encryption_key = HMAC-SHA1(base_key, "encryption")[..16]`, verify `HMAC-SHA1(checksum_key, encrypted)`, AES-128-CTR decrypt with the IV from the blob (`AesCtr.Transform(encryption_key, iv, 0, data)`, foundation) → `Credentials.WithBlob(userName, blob, deviceId)`), responses exactly like upstream (`{"status":101,"spotifyError":0,"statusString":"OK"}`). mDNS responder: join `224.0.0.251:5353` (and `ff02::fb`) on the selected interfaces, answer PTR `_spotify-connect._tcp.local` → `<name>._spotify-connect._tcp.local` with SRV (port), TXT (`CPath=/`, `VERSION=1.0`, `Stack=SP`), A/AAAA records of the advertised IPs, honour unicast-response bit, send unsolicited announcements on start and goodbye (TTL 0) on shutdown. DNS packet encode/decode with name compression (~300 lines).

Contract (exists): `Discovery`, `DiscoveryBuilder`, `DiscoveryEvent`, `DiscoveryException`, `DnsSdHandle`, `DnsSdServiceBuilder`, `DnsSdBackends` (`"libmdns"` entry gets the real builder).

Verify: upstream has `#[cfg(test)]` in `discovery/src/lib.rs`? No, but there are dev-deps (`hex`, `futures`) for doctests – port the `server.rs` crypto path as a unit test: build a `getInfo` → `addUser` exchange in-process with a test client doing the DH + AES-CTR + HMAC side, assert the decoded `Credentials`. DNS packet tests: encode a PTR/SRV/TXT/A answer and decode it back; decode a captured query for `_spotify-connect._tcp.local`. Smoke: run `Librespot.Examples discovery` and pick the device from the Spotify app on a phone in the same LAN.

### 5.13 `playback-player` (wave 4) — `src/Librespot.Playback/Player.cs`

Files: `playback/src/player.rs` → `Player.cs` (exists with the facade; add `PlayerInternal`, `PlayerState`, `PlayerPreload`, `PlayerTrackLoader`, `NormalisationData`, `Subfile`).

Design: `Player.Create` spawns a dedicated thread (`new Thread(..) { IsBackground = true, Name = "player" }`) running `PlayerInternal.Run()`: a loop that (1) drains the command channel (`ChannelReader<PlayerCommand>.TryRead` + `WaitToReadAsync` when idle), (2) handles the current state (`Stopped`, `Loading` (awaits the loader task), `Playing` (decode next packet → normalisation → volume → `sink.Write`), `Paused`, `EndOfTrack`, `Invalid`), (3) preload handling, (4) periodic `PositionChanged`. `Player.Create` builds the `LocalFileLookup` once (`LocalFileLookup.Create(config.LocalFileDirectories)`). `PlayerTrackLoader.LoadTrack` runs on the thread pool: `AudioItem.GetFileAsync` → local file? (`lookup.Get(trackUri)` → `load_local_track`) → format selection by `Bitrate` (`formats` lists exactly as upstream) → `AudioFile.OpenAsync` → `Session.AudioKey.RequestAsync(trackId, fileId)` (the only caller of `AudioKeyManager`) → `new AudioDecrypt(key, file)` → skip Spotify header (`Subfile` = a `Stream` wrapper with offset+length) → `NormalisationData.ParseFromOgg` → `AudioDecoders.Create` → seek to `position_ms`. Normalisation (`Basic`/`Dynamic` limiter with attack/release/knee, two-channel integrators) and volume (`IVolumeGetter.AttenuationFactor`) applied to the f64 samples exactly as upstream; `SinkStatus` callbacks and `TimeToPreloadNextTrack` 30 s before the end; `PLAYER_COUNTER` → static int.

Contract (exists): the whole `Player` facade, `PlayerEvent`, `PlayerCommand`, `QueueTrack`, `SinkStatus`, `SinkEventCallback`, `NormalisationData`, `PlayerEventChannel`. `connect-spirc` and `cli` call only these.

Verify: unit-test `NormalisationData.ParseFromOgg` on a byte array laid out like the Spotify header (gain/peak floats at offset 144), `GetFactor` for the table of cases in upstream comments, the limiter math with a constant-amplitude signal (compare to hand-computed values), and the state machine with a fake `IAudioDecoder`/`ISink`/`AudioFile` (inject through an internal constructor): Load → Playing → EndOfTrack, Pause/Play, Seek. Smoke: `Librespot.Examples play <token> <track>` plays to NAudio.

### 5.14 `connect-spirc` (wave 4) — `src/Librespot.Connect/Spirc.cs`

Files: `connect/src/spirc.rs` → `Spirc.cs` (exists with the handle; add `SpircTask`).

Design: `Spirc.CreateAsync` = upstream `Spirc::new`: connect the session if not connected, build `ConnectState`, subscribe to dealer uris with `DealerManager.ListenFor<T>(uri, parse)` exactly as `spirc.rs:168-213` (`hm://pusher/v1/connections/` with `extract_connection_id`, `hm://connect-state/v1/cluster` / `.../connect/volume` / `.../connect/logout` / `hm://playlist/v2/playlist/` / `spotify:user:attributes:update` / `spotify:user:attributes:mutated` with `Message.FromRaw<T>`, `social-connect/v2/session_update` with `Message.TryFromJson<SessionUpdate>` → `FallbackWrapper<SessionUpdate>` handled as in `handle_session_update`: `Inner` → update, `Fallback` containing `WIFI_BROADCAST_CHANGED` → debug log, else warn) and the request handler `HandleFor("hm://connect-state/v1/player/command")`, start `SpircTask.RunAsync()` and return it as the task to await. `SpircTask` loop: `select!` over dealer messages, requests, player events, commands, the context resolver and the volume-update debounce → in .NET a single loop awaiting `Task.WhenAny` on the pending reads (keep the branch order). Every command handler (`handle_transfer`, `handle_play`, `handle_pause`, `handle_seek`, `handle_next`, `handle_prev`, `handle_shuffle`, `handle_repeat_*`, `handle_add_queue`, `handle_set_queue`, `handle_set_options`, `handle_update_context`, `handle_load`, `handle_volume_*`, `handle_disconnect`, `handle_activate`, `notify`/`send_state`) ported one-to-one; `PlayerEvent` → state updates and `PutStateRequest` sends with `PutStateReason`. Volume: `volume_steps`, `initial_volume`, `disable_volume`, cache `SaveVolume`.

Contract (exists): `Spirc` methods, `LoadRequest`/`LoadRequestOptions`/`PlayingTrack`, `SpircException`, `ConnectConfig`.

Verify: no upstream unit tests. Test the pure pieces: volume step math, `LoadRequest` → initial `PlayOrigin`/context conversion, command routing with a fake dealer `RequestReply` channel and a fake `Player` (the facade sends commands on a channel – read them back). Smoke: `librespot --name test` shows up in the Spotify app, transfer playback to it, next/prev/volume work.

### 5.15 `cli` (wave 5) — `src/Librespot.Cli/`

Files: `src/main.rs` → `Program.cs` (exists) + `Setup.cs` (the `Setup` struct and `get_setup`) + `Options.cs` (option table), `src/player_event_handler.rs` → `PlayerEventHandler.cs`.

Design: a hand-written `getopts`-compatible parser (`-x`/`--long`, `=`/space values, `--` terminator, the same `usage()` text) driving the exact same option list (`--name`, `--backend`, `--device`, `--format`, `--dither`, `--mixer`, `--cache`, `--system-cache`, `--cache-size-limit`, `--disable-audio-cache`, `--disable-credential-cache`, `--bitrate`, `--onevent`, `--emit-sink-events`, `--verbose`, `--quiet`, `--username`/`--password` (removed upstream → error text), `--access-token`, `--enable-oauth`, `--oauth-port`, `--device-type`, `--device-is-group`, `--initial-volume`, `--volume-ctrl`, `--volume-range`, `--enable-volume-normalisation`, `--normalisation-*`, `--zeroconf-port`, `--zeroconf-interface`, `--zeroconf-backend`, `--disable-discovery`, `--autoplay`, `--disable-gapless`, `--passthrough`, `--proxy`, `--ap-port`, `--temp-dir`, `--local-file-dir`, `--position-update-interval`, `--check`, `--version`); env-var fallbacks (`LIBRESPOT_*`) as upstream. `setup_logging` → `LibrespotLog.Factory = LoggerFactory.Create(b => b.AddSimpleConsole(...).AddFilter(...))` with `-v` → Trace for `Librespot.*`, default Info, `--quiet` Warning; `RUST_LOG` → `LIBRESPOT_LOG`. Main loop: discovery → credentials → `Session.ConnectAsync` → `Player.Create` → `Spirc.CreateAsync`, reconnect on session invalidation with the stored credentials, `Console.CancelKeyPress` → `Spirc.Shutdown` then exit (second Ctrl-C exits immediately). `PlayerEventHandler`: thread reading `PlayerEventChannel`, environment-variable table per event exactly as upstream, `Process.Start(onevent)` with the env, `run_program_on_sink_events`.

Verify: unit-test the option parser against the upstream help text cases (every option, short/long, invalid values → the upstream error messages). Smoke: `librespot --version`, `librespot --check`, `librespot --name x --backend pipe --device out.pcm` + transfer from the app writes PCM.

### 5.16 `examples` (wave 5) — `examples/Librespot.Examples/Examples/`

Files: `examples/get_creds.rs` → `GetCreds.cs`, `get_token.rs` → `GetToken.cs`, `play.rs` → `Play.cs`, `play_connect.rs` → `PlayConnect.cs`, `playlist_tracks.rs` → `PlaylistTracks.cs` (the oauth and discovery examples are owned by packages `oauth` and `discovery`, same project). Decision: **one console project with a sub-command per example** (`Librespot.Examples play <token> <track>`), so there is one csproj to maintain and the solution stays small; `Program.cs` already dispatches on `args[0]`.

Verify: each sub-command compiles and prints upstream's usage line when given no arguments; manual smoke with a token.

---

## 6. File → package index

Every `.rs` file of the reference checkout (134 files, `find . -name '*.rs'`), exactly once (`skip` = Linux-only, assigned but not ported).

| Rust file | Package |
|---|---|
| protocol/build.rs, protocol/proto/mod.rs (include shim for the build.rs output; nothing to port), protocol/src/lib.rs, protocol/src/impl_trait.rs, protocol/src/impl_trait/context.rs, protocol/src/impl_trait/player.rs | foundation |
| core/build.rs, core/src/lib.rs, core/src/error.rs, core/src/config.rs, core/src/version.rs, core/src/file_id.rs, core/src/packet.rs, core/src/component.rs, core/src/spotify_id.rs, core/src/spotify_uri.rs, core/src/date.rs, core/src/diffie_hellman.rs, core/src/connection/codec.rs | foundation |
| core/src/connection/mod.rs, core/src/connection/handshake.rs, core/src/proxytunnel.rs, core/src/socket.rs, core/src/authentication.rs, core/src/http_client.rs, core/src/apresolve.rs, core/src/util.rs | core-connection |
| core/src/dealer/mod.rs, core/src/dealer/manager.rs, core/src/dealer/maps.rs, core/src/dealer/protocol.rs, core/src/dealer/protocol/request.rs, core/src/deserialize_with.rs | core-dealer |
| core/src/session.rs, core/src/channel.rs, core/src/audio_key.rs, core/src/mercury/mod.rs, core/src/mercury/sender.rs, core/src/mercury/types.rs, core/src/cache.rs, core/tests/connect.rs | core-session |
| core/src/spclient.rs, core/src/login5.rs, core/src/token.rs, core/src/cdn_url.rs | core-spclient |
| oauth/src/lib.rs, oauth/examples/device_auth.rs, oauth/examples/oauth_async.rs, oauth/examples/oauth_sync.rs | oauth |
| audio/src/lib.rs | foundation |
| audio/src/decrypt.rs, audio/src/fetch/mod.rs, audio/src/fetch/receive.rs, audio/src/range_set.rs | audio-fetch |
| metadata/src/lib.rs | foundation |
| metadata/src/album.rs, artist.rs, audio/file.rs, audio/item.rs, audio/mod.rs, availability.rs, content_rating.rs, copyright.rs, episode.rs, error.rs, external_id.rs, image.rs, lyrics.rs, playlist/annotation.rs, playlist/attribute.rs, playlist/diff.rs, playlist/item.rs, playlist/list.rs, playlist/mod.rs, playlist/operation.rs, playlist/permission.rs, request.rs, restriction.rs, sale_period.rs, show.rs, track.rs, util.rs, video.rs | metadata |
| playback/src/lib.rs, playback/src/config.rs, playback/src/mixer/mod.rs, playback/src/mixer/mappings.rs, playback/src/mixer/softmixer.rs | foundation |
| playback/src/convert.rs, dither.rs, decoder/mod.rs, decoder/symphonia_decoder.rs, decoder/passthrough_decoder.rs, symphonia_util.rs, local_file.rs | playback-pipeline |
| playback/src/audio_backend/mod.rs, pipe.rs, subprocess.rs, rodio.rs | playback-backends |
| playback/src/audio_backend/alsa.rs, gstreamer.rs, jackaudio.rs, portaudio.rs, pulseaudio.rs, sdl.rs, playback/src/mixer/alsamixer.rs | playback-backends (**skip: Linux-only; implement only the NAudio/WASAPI, pipe, and subprocess backends**) |
| playback/src/player.rs | playback-player |
| connect/src/lib.rs, connect/src/model.rs | foundation |
| connect/src/state.rs, state/context.rs, state/handle.rs, state/metadata.rs, state/options.rs, state/provider.rs, state/restrictions.rs, state/tracks.rs, state/transfer.rs, context_resolver.rs, shuffle_vec.rs | connect-state |
| connect/src/spirc.rs | connect-spirc |
| discovery/src/lib.rs, discovery/src/server.rs, discovery/examples/discovery.rs, discovery/examples/discovery_group.rs | discovery |
| discovery/src/avahi.rs | discovery (**skip: Linux-only** Avahi D-Bus backend; the built-in responder replaces it) |
| src/lib.rs | foundation |
| src/main.rs, src/player_event_handler.rs | cli |
| examples/get_creds.rs, get_token.rs, play.rs, play_connect.rs, playlist_tracks.rs | examples |
