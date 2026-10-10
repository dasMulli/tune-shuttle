# Development

For people working on the code: how the repository is laid out, how to build and test it, the rules for live
receivers, the conventions, and how to work on the web app. How the system is built is in
[`ARCHITECTURE.md`](ARCHITECTURE.md); the protocol in [`airplay2/SPEC.md`](airplay2/SPEC.md).

## Repository layout

```
TuneShuttle.sln                 every project below, plus the librespot-dotnet projects the bridge uses
Directory.Build.props           net10.0, C# latest, nullable, implicit usings, warnings as errors, XML docs
Directory.Packages.props        central NuGet versions for src/ and tests/
.editorconfig                   code style (file-scoped namespaces as a warning, i.e. an error)
build.ps1                       CI script: build, offline tests, publish, container image
src/
  Airplay2.Sender/              the AirPlay 2 protocol library (Crypto, Tlv8, Plist, Alac, Discovery, Rtsp, Pairing,
                                Session, Rtp, Streams, Playback, AirplaySender.cs)
  Airplay2.Sender.Cli/          the `airplay2` CLI (discover, info, pair, play, volume, probe)
  Bridge.Contracts/             DTOs, routes and WebSocket messages shared by service and web app
  Bridge.Service/               the `bridge` host: Api/, Store/ (schema.sql), Output/, Spotify/, Diagnostics/
  Bridge.Web/                   the Blazor WebAssembly SPA (Pages/, Components/, Layout/, Localization/,
                                Resources/i18n/, wwwroot/)
tests/
  Airplay2.FakeReceiver/        the in-process AirPlay 2 receiver every test project uses
  Airplay2.Sender.Tests/        unit, fake-receiver, loopback and live tests of the sender and the CLI
  Bridge.Tests/                 store, API, sink, deployment and release tests; Web/ holds the web app tests (bUnit)
tools/
  web-screenshots.ps1           screenshots of the web app in headless Edge
  web-audit.ps1                 layout audit of the web app in headless Edge
  privacy-scan.ps1              fails on personal or household data in the tracked files (see "Privacy scan")
  screenshot-setups/            scripts that put the fake receivers into a state before screenshots or audits
docker/
  Dockerfile                    the Linux image of the bridge
  shairport-sync/               the shairport-sync test receiver (compose.yaml) and the scripts that run live tests
                                against it from inside Docker's VM
install/
  windows/install-service.ps1   Windows Service and firewall rules
  linux/tune-shuttle.service    systemd unit
librespot-dotnet/               the C# port of librespot, with its own solution, props and tests (see its README)
docs/                           see docs/README.md
```

`librespot-dotnet/` keeps its own `Directory.Build.props` (MSBuild stops at the nearest one), so the root props never
apply to it; keep the two in step by hand. The reference checkouts the code was written against live in `reference/`
(git-ignored); [`REFERENCE-SOURCES.md`](REFERENCE-SOURCES.md) has the commands to recreate them.

## Build and test

Requirements: .NET SDK 10.0 (tested with 10.0.401). No node and no `wasm-tools` workload; the Blazor app builds with
the plain SDK. PowerShell 7 and Microsoft Edge for the web tools, Docker Desktop for the container image and the
shairport-sync receiver.

```powershell
dotnet build TuneShuttle.sln
dotnet test TuneShuttle.sln --filter "Category!=SpeakerALive&Category!=ShairportLive"
dotnet test tests/Bridge.Tests --filter FullyQualifiedName~Bridge.Tests.Web     # only the web app tests
```

Warnings are errors in this solution (`TreatWarningsAsErrors`; only CS1591, a missing XML doc, is exempt). The 30 s
pacer accuracy test is tagged `Category=Timing`; add `&Category!=Timing` on a loaded machine.

Test categories:

| Category | Runs | Gate |
|---|---|---|
| (none) | everything offline: unit tests, the in-process fake receiver, loopback sockets, `WebApplicationFactory`, bUnit | always |
| `Timing` | the 30 s pacer accuracy run | always; excluded with `-ExcludeTimingTests` |
| `SpeakerALive` | against a Test Speaker ABCD on the LAN | `AIRPLAY_LIVE=1` plus the target (`AIRPLAY_LIVE_RECEIVER` or `AIRPLAY_LIVE_RECEIVER_HOST`, see "Live tests"); audio additionally `AIRPLAY_AUDIO_I_KNOW=1` |
| `ShairportLive` | against shairport-sync in Docker | `AIRPLAY_LIVE=1`; set by the VM scripts |

A live test returns early unless its gate is set, so a plain `dotnet test` never contacts a receiver even without the
filter. xunit 2.9 has no dynamic skip, so such a test is reported as passed; its output says why.
`RepositoryHygieneTests` checks that every live test has the gate.

The librespot port has its own solution and gate: `dotnet test librespot-dotnet/Librespot.sln`; its network tests
run only with `LIBRESPOT_LIVE_TESTS=1` (plus `LIBRESPOT_ACCESS_TOKEN` for the ones that talk to Spotify).

The CI script runs the whole sequence:

```powershell
./build.ps1                      # Release build, offline tests, publish win-x64, container image
./build.ps1 -SkipContainer       # without Docker
./build.ps1 -SkipTests -SelfContained -SkipContainer   # only the self-contained Windows bundle
```

Parameters: `-Configuration Debug|Release` (default Release), `-Output` (default `artifacts`), `-Runtime` (default
`win-x64`), `-SelfContained`, `-ImageTag` (default `tune-shuttle:dev`), `-SkipBuild`, `-SkipPrivacyScan`, `-SkipTests`, `-SkipPublish`,
`-SkipContainer`, `-RequireContainer` (fail instead of warn without Docker), `-ExcludeTimingTests`. The test step
clears `AIRPLAY_LIVE` as a second guard. Before the tests it runs the privacy scan (below).

| Output | Path |
|---|---|
| Bridge (`bridge.exe` + web app) | `artifacts/bridge/` (what `install/windows/install-service.ps1` reads by default) |
| CLI (`airplay2.exe`) | `artifacts/airplay2/` |
| Test results (TRX) | `artifacts/test-results/` |
| Container image | `tune-shuttle:dev` |

`artifacts/` is git-ignored. Two test classes pin the release plumbing: `ReleaseConsistencyTests` (`appsettings.json`
equals the `BridgeOptions` defaults; publish folder and web port agree between `build.ps1`, the install script and the
Dockerfile) and `DeploymentTests` (the endpoint key in the install script, the Dockerfile and the systemd unit).
`QuirkDefaultsTests` pins the receiver defaults of `SPEC.md` section 4.2, and a test checks that every relative link
in `README.md` points to a file that exists.

### Privacy scan

The repository is public, and its specs and test vectors come from captures of real receivers on a home network.
`tools/privacy-scan.ps1` keeps personal and household data out: it reads every tracked (and every new, not ignored)
file except `reference/`, binary plists included, and exits 1 with `path:line: [check] text` for each hit.

```powershell
./tools/privacy-scan.ps1          # also step 2 of ./build.ps1 (skip with -SkipPrivacyScan)
```

It flags the home network's subnets, domain and host names, the TV's user-given name, MAC addresses and IPv6 interface
ids derived from globally administered (real hardware) MACs, IPv6 ULAs with a global id, receiver `pk` values other
than the RFC 8032 test keys, and 32-hex ids next to "client" or "secret" other than the public Spotify client ids
librespot ships. The exact identifiers that were replaced are not in the repository: the script reads them from the
git-ignored `.local/privacy-scan-patterns.txt` (one value per line, also matched hex-encoded) and the Spotify developer
credentials from `.local/spotify-dev-app.json`, when those files exist.

Rules for new material: addresses in docs and tests come from 192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24 and
2001:db8::/32 (loopback where the test is about loopback); device ids are locally administered (`02:...`); keys are
test keys; receiver names are neutral ("Living room TV", "Kitchen", "Hallway"). Anonymize a new capture before
committing it, in binary files with same-length replacements so offsets stay valid (SPEC-control.md 1.7).

### Running the bridge from the repository

```powershell
dotnet run --project src/Bridge.Service                                  # http://localhost:8080, the real LAN
dotnet run --project src/Bridge.Service -- --Bridge:FakeReceivers=2      # two in-process fake receivers instead
```

`dotnet run` uses `src/Bridge.Service/Properties/launchSettings.json`, which sets `ASPNETCORE_ENVIRONMENT=Development`
(OpenAPI at `/openapi/v1.json`). The build output has no `wwwroot/`; the bridge finds the web app through the static
web assets manifest next to `bridge.dll` in every environment, so `dotnet bin/Debug/net10.0/bridge.dll` (Production)
serves it too.

## Live tests

```powershell
$env:AIRPLAY_LIVE=1
$env:AIRPLAY_LIVE_RECEIVER="Test Speaker ABCD"   # the receiver's mDNS name or device id, as `airplay2 discover` shows it
$env:AIRPLAY_LIVE_RECEIVER_HOST="192.0.2.26"     # its IP address (example value: use your receiver's)
dotnet test --filter Category=SpeakerALive                                            # control-plane tests, no audio
$env:AIRPLAY_AUDIO_I_KNOW=1; dotnet test --filter "FullyQualifiedName~SpeakerA_sender_M1"
./docker/shairport-sync/run-live-tests-in-vm.ps1 -Filter "Category=ShairportLive&FullyQualifiedName!~PtpLiveTests&FullyQualifiedName!~BufferedLiveTests"
./docker/shairport-sync/run-ptp-live-tests-in-vm.ps1                                # PtpLiveTests
./docker/shairport-sync/run-ptp-live-tests-in-vm.ps1 -Filter "FullyQualifiedName~BufferedLiveTests&Category=ShairportLive"
```

The SpeakerALive tests have no built-in target: nothing in the repository names or addresses a particular receiver.
They read it from the environment and return early (reported as passed, with the reason in the output) when the variable
they need is unset (`tests/Airplay2.Sender.Tests/LiveTarget.cs`):

| Variable | Used by | Value |
|---|---|---|
| `AIRPLAY_LIVE_RECEIVER` | the tests that discover the receiver by mDNS (`ReceiverBrowserLiveTests`, `CliLiveTests`, `BridgeLiveTests`) | its mDNS instance name or device id (what `--to` accepts) |
| `AIRPLAY_LIVE_RECEIVER_HOST` | the tests that connect directly (RTSP, pairing, session, playback, PTP, buffered) | its IPv4 or IPv6 address |
| `AIRPLAY_LIVE_RECEIVER_PORT` | the same | its RTSP port; default 7000 |

The receiver must be a Test Speaker ABCD (the direct-connect tests use its TXT record and quirks, and `RtspLiveTests`
compares its `GET /info` with the stored vectors). Each run writes its decrypted wire log to `artifacts/live/`. The CLI's `probe` command runs the protocol probes of
`SPEC.md` section 4 one at a time (`airplay2 probe P3 "Test Speaker ABCD"`) and appends its log there too.

### Live-device rules

AirPlay wakes receivers, and they play at their current volume. These rules hold for every live run, by a person or
an agent:

- **Allowed:** the Test Speaker ABCD and the shairport-sync container. mDNS browsing and unauthenticated `GET /info`
  are fine at any time; pairing, session `SETUP`/`TEARDOWN` and control-plane probes without audio are fine too.
- **Never:** the LG TV "Living room TV" (AirPlay turns it on), any receiver not listed here, and the Apple receiver
  "Hallway" without its owner's go-ahead. No PIN pairing attempts on devices that show a PIN.
- **Audio** only as a 3-second quiet test tone (440 Hz, -24 dBFS) at a level of at most 20 (15 is the default), and
  only with the explicit acknowledgement: `--i-know` for the CLI (without it `play` and the audio probes exit with
  code 2 before opening a socket), `AIRPLAY_AUDIO_I_KNOW=1` for the tests. The probes enforce the 3 s and level 20
  caps (`PlaybackProbeOptions`); `play` warns above them. Music and long sessions only with Martin present.
- Every live run logs the complete wire exchange to `artifacts/live/`. Record outcomes in `SPEC.md` section 4.

The checks that need a person in the room are in [`airplay2/MANUAL-TESTS.md`](airplay2/MANUAL-TESTS.md).

## Code conventions

- **Language**: C# latest on `net10.0`, nullable and implicit usings on, warnings as errors, file-scoped namespaces,
  `record`/`record struct` for data, XML doc comments on every public member (`GenerateDocumentationFile` is on).
  Code style is in `.editorconfig` (the same as librespot-dotnet's).
- **Async**: every I/O method is `async` and takes a `CancellationToken` as its last parameter; no `.Result`/`.Wait()`,
  no `Task.Run` to fake asynchrony. Two documented exceptions: `IGroupPlayer.Write` blocks librespot's player thread
  as backpressure, and the pacer runs on a dedicated thread with `Stopwatch` sleeps.
- **Producer/consumer**: `System.Threading.Channels` (bounded; `DropOldest` for event feeds, `Wait` for audio) instead
  of events or `BlockingCollection`.
- **Logging**: `ILogger<T>` injected through constructors, structured messages (`"SETUP stream {StreamType} to
  {Receiver}"`), `Debug` for wire-level detail, never `Console.WriteLine` in libraries.
- **Errors**: specific exceptions (`RtspStatusException`, `PairingException` with `PairingFailure`), never swallowed
  silently; transport failures surface as `Closed` tasks on connections and `Lost` states on group members.
- **Threading**: one owner per socket; state machines guard their state with a `SemaphoreSlim` or a single consumer
  loop; hand-offs between threads go through channels.
- **Frame size**: encrypted HAP frames we send carry at most 1024 bytes of plaintext (a `const`, the SpeakerA closes the
  connection otherwise); every reader accepts frames up to 65 535 bytes.
- **Tests**: xunit 2.9.3, NSubstitute for interface doubles, bUnit 2.11.3 for components. Vectors come from
  `docs/airplay2/test-vectors` (linked into the test output as `TestVectors/`); loopback sockets on `127.0.0.1` with
  OS-chosen ports.
- **Licences in code**: ported Apple ALAC files keep their Apache-2.0 notice. Nothing is transcribed from
  owntone-server (GPL-2.0): where the spec describes owntone's behaviour it is reimplemented from the description, with
  our own names. librespot-dotnet (MIT) is referenced, not copied.
- **Docs in comments**: refer to the spec by document and section (`SPEC-audio.md 8.4`, `SPEC.md D7`) and to these
  documents by name and section heading.
- **Commits**: a short imperative subject line (`Receiver housekeeping: forget, pruning, last seen`), a body that
  says what changed and why, wrapped at about 75 characters.

## Test receivers

**The fake receiver** ([`tests/Airplay2.FakeReceiver`](../tests/Airplay2.FakeReceiver)) is an in-process AirPlay 2
receiver: pairing (through the same `HapAccessoryPairing` code the tests use), the encrypted control channel, every
RTSP request the sender makes, the event channel, RTP decryption and ALAC decoding, timing requests, retransmit
requests and a minimal PTP slave. It serves the SpeakerA's recorded `/info` and has switches for the receiver
behaviours seen live (NTP refused, no `streamID`, DMAP refused, both `GET_PARAMETER` shapes, large event frames). It
is the primary target of the unit and end-to-end tests.

The bridge can run on fake receivers instead of the LAN: `--Bridge:FakeReceivers=N` starts N of them on loopback
(ids `02:FA:CE:00:00:01`, `02:FA:CE:00:00:02`, ..., names "Fake Receiver 1", ...) and switches mDNS browsing off. This
works only in Debug builds from the full repository (the fake lives under `tests/`, compiled in through the
`BRIDGE_FAKE_RECEIVERS` constant); a Release build logs a warning and ignores it. Debug builds with fakes also map
`POST /api/dev/fake-receivers/{id}/offline?daysAgo=N`, which takes a fake off the network as if it was last seen N
days ago.

**shairport-sync** 5.5.2 in Docker is the second target, for pairing (transient and password/PIN), PTP sessions, the
realtime stream under PTP and the buffered stream. It refuses NTP sessions and is reachable only from inside Docker's
VM, so the live tests run there through `docker/shairport-sync/run-live-tests-in-vm.ps1` and
`run-ptp-live-tests-in-vm.ps1`. Setup and what it can and cannot test: [`airplay2/TEST-RECEIVER.md`](airplay2/TEST-RECEIVER.md).

## mDNS loopback tests

`ReceiverBrowserLoopbackTests` run the real `MdnsReceiverBrowser` and librespot's `MdnsResponder` against each other
with real sockets, but on a private endpoint instead of 224.0.0.251:5353: 239.255.0.251 / ff05::1:fb, ports
53530-53533, hop limit 1 (`MdnsEndpoint`, one port per test). Nothing else joins those groups, so the made-up
"ap2 browser loopback" receivers never reach an mDNS reflector, another VLAN or a running bridge. Production code
always uses `MdnsEndpoint.Default`.

## Web app development

Working on the web app needs no speaker and no Spotify account: run the bridge with the fake receivers (see "Running
the bridge from the repository") and open `http://localhost:8080`. After a rebuild of `src/Bridge.Web`, restart a
running bridge; it serves the files it found when it started.

The reference for every page, component, token and string is [`web/UI-SPEC.md`](web/UI-SPEC.md), with the mockups in
`docs/web/mockups/`. Rules that the tests enforce:

- Every string a user sees comes from the catalog (`I18n`); no literal English in `.razor` files except the developer
  pages `/_styleguide` and `/_audit`, `aria-hidden` glyphs and the static `wwwroot/index.html`.
- Catalog keys appear in the source as literal strings (`L["pills.playing"]`), never built by interpolation; a
  `switch` over the cases is the pattern. `CatalogUsageTests` relies on it.
- Colours come from tokens; no hex values outside `app.css` (exceptions: the two SVG chevrons of the selects and the
  `theme-color` metas in `index.html`).

Two developer pages help: `/_styleguide` renders every component in every state, and `/_audit?route=<route>` renders
a page and writes the layout audit's JSON into `<pre id="audit-result">`.

`?theme=light|dark|system` and `?lang=<code>` on any URL apply once without being stored; the scripts use them.

### Screenshots

```powershell
# One PNG per route x size x theme x language, in artifacts/screenshots/<Package>/.
pwsh tools/web-screenshots.ps1 -Package mine -Routes /,/receivers,/settings -Sizes 390x844,844x390,768x1024,1280x900 -Themes light,dark
pwsh tools/web-screenshots.ps1 -Package mine -Routes / -Sizes 390x2200            # a tall viewport shows the end of a page
pwsh tools/web-screenshots.ps1 -Package mine -Routes / -Setup tools/screenshot-setups/mobile-polish.ps1   # with an active group
pwsh tools/web-screenshots.ps1 -Package mine -Routes /_styleguide -Themes light,dark,system-dark
```

The script starts the bridge itself (`--Bridge:FakeReceivers=2`, Spotify off, file logging off, a fresh temporary
data directory), waits for `/api/status`, runs the optional `-Setup` script, takes the pictures and stops the bridge.
`-NoStart` uses a bridge already running on `-Port` (default 8080). Other parameters: `-Langs` (default `en`),
`-OutDir`, `-Budget` (ms to wait for the WASM boot, default 15000), `-Settle` (ms for the data, default 2500),
`-ScrollY`, `-ShowScrollbars`, `-Edge` (path of `msedge.exe`). File names are
`<route>-<W>x<H>-<theme>-<lang>.png`, with `/` named `player`.

How dark mode is captured: `light` and `dark` load the page with the one-shot `?theme=` query, which the theme
bootstrap in `index.html` applies before the first paint. `system-dark` loads `?theme=system` in an Edge started with
`--force-dark-mode`, which flips `prefers-color-scheme`, to prove that "system" follows the OS.

Headless Edge never lays a page out narrower than 504 CSS px, whatever `--window-size` says, so both scripts drive
Edge over the DevTools protocol and set the exact viewport with `Emulation.setDeviceMetricsOverride`.

**Setup scripts** (`tools/screenshot-setups/*.ps1`) are plain PowerShell files with a mandatory `-BaseUrl` that put
the two fakes into a state through the REST API before the pictures are taken; each run starts from an empty data
directory, so a script creates everything it needs. `web-screenshots.ps1` passes only `-BaseUrl`, which is why
`player-page-group.ps1` exists as a wrapper for `player-page.ps1 -Scenario group`.

| Script | State |
|---|---|
| `shell.ps1` | Fake Receiver 1 connected; `-ToastShots <dir>` also captures an error toast |
| `player-page.ps1` (`-Scenario receiver\|group`), `player-page-group.ps1` | one receiver as the output, or the group "Downstairs" active |
| `receivers-page.ps1` | Fake Receiver 1 connected at level 65 |
| `settings-diagnostics.ps1` | Fake Receiver 1 connected, so the statistics move |
| `groups-and-pairing.ps1` | the groups "Downstairs" (both fakes) and "Upstairs" (empty); prints the id for `/groups/<id>` |
| `i18n-fill.ps1` | the state with the most text: a long group name active with both fakes |
| `mobile-polish.ps1` | a 63-character group name active with both fakes; `-HeroShots <dir>` also captures the Player with a demo track injected in the browser |
| `forget-receivers.ps1` | Fake Receiver 2 offline, "last seen 3 days ago" (through the development-only route) |

### Layout audit

```powershell
pwsh tools/web-audit.ps1
pwsh tools/web-audit.ps1 -Langs en,de,ru -Setup tools/screenshot-setups/mobile-polish.ps1
pwsh tools/web-audit.ps1 -NoStart -Port 18080 -Routes /,/receivers -Sizes 390x844 -Themes dark
```

Loads `/_audit` for every route × size × theme × language (defaults: `/`, `/receivers`, `/settings`,
`/settings/diagnostics`, `/groups` at 390x844 and 768x1024, light and dark, en) and fails (exit code 1) on

- horizontal overflow,
- a control without an accessible name,
- an interactive element under 44 × 44 px other than the allow-listed `.theme-btn`, checked at widths up to 699px and
  wherever the pointer is coarse,
- a cut-off connection word in the top bar (from 360px wide),
- and, with the theme picker unfolded (transitions off for that measurement): overflow, a cut word, a theme button 0px
  wide (the pill did not open), or a top-bar language select narrower than 72px.

`-Pointer auto` (the default) audits sizes under 1024px wide as touch devices (DevTools touch emulation, so
`(pointer: coarse)` matches and the touch rules apply); `-Pointer fine` audits them with a mouse, `-Pointer coarse`
also audits desktop sizes as touch screens. `-Budget` (default 30000 ms) bounds the wait for one page.

### Component tests

The web tests live in `tests/Bridge.Tests/Web`, one file per page or area (`PlayerTests`, `ReceiversTests`,
`GroupsPairingTests`, `SettingsDiagnosticsTests`, `ShellTests`, ...). `UiTest.Create(lang, hoverCapable, respond)` in
`WebTestSupport.cs` registers everything a component needs: `BridgeState`, a stub API, `BridgeCommands`, `I18n` in
the given language and a mocked `./js/app.js` module. `hoverCapable: false` is how touch behaviour is tested (the theme
picker's first-tap-opens branch); `CultureScope` sets and restores the culture for number-format assertions. Select
elements by the class names of UI-SPEC and `data-*` ids, never by text unless the test is about the text.

### Static checks

- `DesignTokenTests` parses `wwwroot/css/app.css`: the two dark-mode blocks are identical, every token of UI-SPEC 1.1
  is declared, the media queries are the allowed list of UI-SPEC 5.1, status colours are never used as text colour,
  hover rules are guarded by `(hover: hover)`, no rule reorders children, and the WCAG contrast table of UI-SPEC 6.3
  holds.
- `I18nTests`: key parity across the catalogs, placeholders, plural variants per language, fallback order, the
  language list.
- `CatalogUsageTests`: every key used in the source exists in `de.json`, and every key in `de.json` is used (the
  allow-list of unused keys is empty).
- `FormattingTests`: numbers, dates and plurals in every language.

## Themes

Light, dark or system. The colours are CSS custom properties in `wwwroot/css/app.css`: the light values on `:root`,
the dark values twice, under `:root[data-theme="dark"]` (an explicit choice) and under
`@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) }` (system), and the two dark blocks must stay
byte-identical (`DesignTokenTests`). A token is added to both or to neither.

An inline script in `wwwroot/index.html` runs before the stylesheet: it reads the stored choice from
`localStorage["airplay-bridge-theme"]` (absent = system) or a one-shot `?theme=` query, sets `data-theme` on `<html>`
and the matching `theme-color` meta, and exposes `window.bridgeTheme` (`apply`, `read`). The top-bar
`ThemePicker` and the Settings > Appearance radios call it through `BrowserBridge` and `js/app.js`. The storage keys
keep the product's old name on purpose: renaming them would reset every user's choice.

## Translations

The catalogs are i18next-style JSON files in `src/Bridge.Web/Resources/i18n/<code>.json` (nested objects, `{{name}}`
placeholders, plural suffixes `_one`/`_few`/`_many`/`_other`), embedded in the assembly and flattened to `a.b.c` keys
by `Localization/I18n.cs`. German is the source language: a key missing from the current language falls back to German,
then to the key itself (visible on screen and caught by the parity tests). `L["key"]`, `L.Format("key", ...)` and
`L.Plural("key", count)` look texts up; `I18n.SetLanguage` switches the language at run time, sets the culture used by
the `Format` helpers and re-renders every component. Plural categories come from `Localization/PluralRules.cs`.

The language is chosen in this order: `?lang=` (one-shot), `localStorage["airplay-bridge-lang"]`, the browser's
languages, English. The inline bootstrap in `index.html` applies it to `<html lang>` before Blazor starts. The
supported languages: de, en, fr, es, ru, uk, pt (Brazilian Portuguese).

### Adding a language

It touches six places, all pinned by tests so nothing can be forgotten silently:

1. Copy `src/Bridge.Web/Resources/i18n/de.json` to `<code>.json` and translate every key, including the plural
   variants (`_one`, `_few`, `_many`, `_other`) the language's CLDR categories need.
2. Add the language to `I18n.Languages` (picker order, native name) and its culture to `State.For` in
   `src/Bridge.Web/Localization/I18n.cs`.
3. Add its CLDR plural rule to `src/Bridge.Web/Localization/PluralRules.cs`.
4. Add the code to `SUPPORTED` in `src/Bridge.Web/wwwroot/index.html` and the four splash/error-strip strings to the
   inline map `T` there (these show before Blazor has loaded).
5. Add the code to `SUPPORTED_LANGS` in `src/Bridge.Web/wwwroot/js/app.js`.
6. Update `Languages_are_the_seven_of_the_spec_in_picker_order` in `tests/Bridge.Tests/Web/I18nTests.cs`, then run
   `dotnet test tests/Bridge.Tests --filter FullyQualifiedName~Bridge.Tests.Web` (the parity and plural-category
   tests catch missing keys) and `pwsh tools/web-screenshots.ps1 -Package i18n -Langs <code>` to see the result.

## Container image

```powershell
docker build -f docker/Dockerfile -t tune-shuttle .                       # for the build host's architecture
docker buildx build --platform linux/arm64 -f docker/Dockerfile -t <registry>/tune-shuttle:<tag> --push .
```

The SDK stage runs natively on the build host and cross-compiles for `TARGETARCH`; only the runtime stage is pulled
for the target platform, so an arm64 image (e.g. for a Raspberry Pi) builds without QEMU. The header of
`docker/Dockerfile` has the command with a placeholder registry. `./build.ps1` builds `tune-shuttle:dev` for the host.

## Manual tests

Listening tests, a real Spotify handoff, a second real receiver, PIN pairing on a real device, the web app on real
phones and the deployments are a maintained checklist with their last known results:
[`airplay2/MANUAL-TESTS.md`](airplay2/MANUAL-TESTS.md).
