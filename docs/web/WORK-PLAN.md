# AirPlay Bridge web app: work plan for the variant C redesign

Companion of `docs/web/UI-SPEC.md` (the spec; section numbers below refer to it). Repository `D:\airplay2-streamer`,
solution `AirplayBridge.sln`, web app `src/Bridge.Web`, tests `tests/Bridge.Tests` (web tests under `Web/`). .NET SDK
10.0.401, no node. Warnings are errors. Shell: Windows PowerShell, `dotnet` from the repository root.

Revised after review: a wave 0 (`foundation`) now delivers the shared browser plumbing, the route moves and the
test-file split before anything else runs, so every later wave owns disjoint paths and the app is never broken
between waves.

## 1. Rules for every package

- Touch only the paths your package owns (section 4 lists them per package, exhaustively). Other packages run at the
  same time in the same working tree. Never `git commit`, `stash`, `checkout` or `reset`.
- Build with `dotnet build AirplayBridge.sln -warnaserror` and run `dotnet test tests/Bridge.Tests --filter
  "FullyQualifiedName~Bridge.Tests.Web"` before you report; a package that leaves the solution red is not done.
  The full `dotnet test` (all projects) is run by the review packages.
- Never contact real AirPlay receivers; the service is only run as `dotnet run --project src/Bridge.Service --
  --Bridge:FakeReceivers=2` (two in-process fakes, ids `02:FA:CE:00:00:01/02`, names "Fake Receiver 1/2") or through
  `WebApplicationFactory` in tests. Spotify stays off in both.
- Every string a user sees comes from the catalog (`I18n`); no literal English in `.razor` files except the
  developer pages `/_styleguide` and `/_audit`, `aria-hidden` glyphs, and the static `wwwroot/index.html` (UI-SPEC
  8.1). The `de`/`en` texts are in UI-SPEC 10; do not invent new strings without adding them to both catalogs (and
  listing the additions in your report).
- Catalog keys appear in source as **literal strings** (`L["pills.playing"]`, `L.Format("notices.failed", …)`,
  `ErrorMessage = "settings.validation.nameEmpty"`), never built by interpolation (`$"pills.{kind}"`): a `switch`
  over the kinds is the pattern. `CatalogUsageTests` (2.4) relies on it.
- Colours come from tokens; no hex values outside `app.css` (exceptions: the two SVG chevrons of `.lang-select` /
  `select.input` listed in UI-SPEC 3.2, and the `theme-color` metas in `index.html`).
- Each package ends with a short report (in its result, not a file): what was built, deviations from the spec with
  the reason, the screenshot paths, open questions for the reviewer.

## 2. Test strategy

### 2.1 bUnit component tests

`tests/Bridge.Tests/Web/ComponentTestBase.cs` (created by foundation in wave 0 from the old `WebComponentTests`
fixture: `BunitContext _ctx`, `BridgeState _state`, `UseApi(...)`, `PathOf(...)`) and `WebTestSupport.cs` (owned by
i18n-core in wave 1, then shared read-only) gain:

```csharp
internal static class UiTest
{
    /// Registers everything a component needs: BridgeState, a stub API (StubHandler), BridgeCommands with I18n,
    /// I18n in the given language, BrowserBridge over a mocked "./js/app.js" module.
    public static (BunitContext Ctx, BridgeState State, StubHandler Api, I18n L, BunitJSModuleInterop Module)
        Create(string lang = "en", bool hoverCapable = true,
               Func<HttpRequestMessage, string?, Task<HttpResponseMessage>>? respond = null);
}
```

- The module mock: `ctx.JSInterop.SetupModule("./js/app.js")` with `SetupVoid("setTheme", _ => true)`,
  `Setup<string>("getTheme").SetResult("system")`, `Setup<bool>("hoverCapable").SetResult(hoverCapable)`,
  `Setup<string>("getLang").SetResult(lang)`, `SetupVoid("setLang", …)`, `SetupVoid("measure", …)`,
  `Setup<IJSObjectReference>("onOutsideClick", …)` (a nested module mock with `SetupVoid("dispose")`),
  `Setup<IJSObjectReference>("rememberActive", …)` (nested mock with `SetupVoid("restore")`),
  `Setup<string>("audit").SetResult("{}")`. `ElementReference.FocusAsync` is served by
  `ctx.JSInterop.SetupVoid("Blazor._internal.domWrapper.focus", _ => true)`. Components must work when a call is
  answered late (they render without the answer first).
- `hoverCapable: false` is how touch behaviour is tested (the theme picker's first-tap-opens branch); headless
  screenshots cannot exercise it (UI-SPEC 5.6).
- Culture: `UiTest.Create("de")` calls `I18n.SetLanguage("de")`, which sets `CultureInfo.CurrentCulture` for the
  test thread; tests that assert formatted numbers do so in a `using var _ = new CultureScope("de")` helper that
  restores the previous culture (`WebTestSupport.cs`).
- Rendering a page with the layout: not needed; pages render on their own (`ctx.Render<Player>()`), the shell
  components render on their own.
- Keyboard: bUnit's `element.KeyDown(Key.ArrowRight)` drives the theme picker and the dialog's Tab trap;
  `element.Change("de")` the select; `element.Input("73")` the sliders (unchanged from today's tests). Native
  browser behaviour (range PageUp/Down, Home/End, Enter submitting a form, radio arrow keys) is not tested.
- Selectors are class names of UI-SPEC 1.5 and `data-*` ids (`data-receiver-id`, `data-group-id`), never text,
  except where the test is about the text.

### 2.2 Screenshots with headless Edge (`tools/web-screenshots.ps1`, owned by design-system; never edited by others)

```
tools/web-screenshots.ps1 -Package shell [-Routes /,/receivers,/settings] [-Sizes 390x844,768x1024,1280x900]
                          [-Themes light,dark] [-Langs en] [-Port 8080] [-NoStart] [-Setup tools/screenshot-setups/shell.ps1]
```

- Starts `dotnet run --project src/Bridge.Service -- --Bridge:FakeReceivers=2` as a background process (unless
  `-NoStart`, when a service is already running), polls `http://localhost:8080/api/status` for up to 90 s, then runs
  the `-Setup` script if given (`& $Setup -BaseUrl http://localhost:8080`; a plain PowerShell file that puts the
  fakes into the wanted state through the REST API, e.g. `POST /api/receivers/02:FA:CE:00:00:01/connect`), then for
  every route × size × theme × lang runs
  `& "C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe" --headless=new --disable-gpu --hide-scrollbars
  --window-size=W,H --virtual-time-budget=15000 --screenshot="artifacts/screenshots/<package>/<name>-<W>x<H>-<theme>-<lang>.png"
  "http://localhost:8080<route>?theme=<theme>&lang=<lang>"`, waits for the file, and finally stops the service.
  `--virtual-time-budget` gives the WASM runtime time to boot and connect the WebSocket (verified: a 15 s budget
  renders the app with data; raise it if a screenshot shows the loading splash).
- **Route → file name**: `/` → `player`; otherwise strip the leading slash and replace every character that is not
  `[A-Za-z0-9]` with `-` (`/settings/diagnostics` → `settings-diagnostics`, `/groups/new` → `groups-new`,
  `/groups/<id>` → `groups-<id with colons etc. as ->`, `/_styleguide` → `-styleguide`). Deep links work because the
  service maps `MapFallbackToFile("index.html")` (verified, `src/Bridge.Service/Program.cs:65`).
- Each package that needs a state ships its own setup script `tools/screenshot-setups/<package>.ps1` (owned by that
  package; the directory is created by design-system with a `README.md` one-liner). Nobody edits
  `web-screenshots.ps1` after wave 1; a missing feature is reported, not patched in.
- **Dark mode**: the `?theme=dark` query handled by the index.html bootstrap (UI-SPEC 2.2; a one-shot override that is
  never stored). This is the chosen mechanism because it is deterministic and also exercises the bootstrap. The
  alternative, `--force-dark-mode`, was verified to flip `prefers-color-scheme: dark` in headless Edge and is used
  once per shell screenshot set (`-Themes system-dark`) to prove that "system" follows the OS.
- `?lang=de` etc. likewise; `-Langs de,ru` is used by i18n-fill and mobile-polish for the longest strings.
- Phone landscape `844x390` is part of mobile-polish's set.
- Headless Edge reports `(pointer: coarse)` false and `(hover: hover)` true at every size (verified with a
  `matchMedia` probe through `--dump-dom` at 390×844); the touch block of UI-SPEC 5.4 is reached through its
  `(max-width: 699px)` half, the hover guard is never exercised by screenshots.
- `artifacts/` is git-ignored (verified, `.gitignore:11`); packages list their files in the report, reviewers open
  them. bUnit is 2.11.3 (`BunitContext`).

### 2.3 Layout audit (`tools/web-audit.ps1`, owned by mobile-polish; UI-SPEC 5.6)

Dumps `http://localhost:8080/_audit?route=<route>&theme=<theme>` with `--dump-dom --window-size=390,844` (and
768×1024), retrying with a growing `--virtual-time-budget` (2 s steps, up to 30 s) until `<pre id="audit-result">`
is non-empty (the page fills it only after `State.Loaded` + one render + 500 ms); parses the JSON; fails on
`overflow > 0`, on any `unnamed` entry, and on `small` entries outside the allow-list (`.theme-btn`, `.lang-select`);
prints the full `small` list. `small` is computed unconditionally at `innerWidth <= 699`, so it is meaningful in
headless Edge.

### 2.4 Static checks in xunit

- `DesignTokenTests` (design-system) parses `wwwroot/css/app.css`:
  1. the `:root[data-theme="dark"]` block and the `@media (prefers-color-scheme: dark) { :root:not([data-theme="light"]) }`
     block contain byte-identical declaration lists (whitespace-normalised);
  2. every token of UI-SPEC 1.1 is declared in the light block and (where the table gives a dark value) in both dark
     blocks;
  3. the set of `@media` queries in the file is a subset of the list in UI-SPEC 5.1;
  4. no declaration `color: var(--ok)`, `color: var(--warn)`, `color: var(--err)` or `color: var(--accent)` exists
     except under the selector `.theme-btn.is-active` (UI-SPEC 6.3);
  5. the contrast table of UI-SPEC 6.3 with **minimum semantics**: every text row ≥ 4.5, every non-text row ≥ 3.0,
     the `--line-strong` row only printed. Translucent tokens are composited over the named backdrop per channel
     (`round(a·fg + (1−a)·bg)` in 0–255 sRGB) before the WCAG relative-luminance formula; no equality assertions.
- `I18nTests` (i18n-core, extended by i18n-fill): key parity across catalogs; every `{{placeholder}}` of a `de` string
  appears in every translation; plural variants per language; `Format`/`Plural` behaviour; fallback order.
- `CatalogUsageTests` (i18n-fill): (a) every string literal in `src/Bridge.Web/**/*.razor|*.cs` that looks like a
  key (`^[a-z]+(\.[A-Za-z0-9]+)+$`) and is used as an `I18n` argument or `ErrorMessage` exists in `de.json`;
  (b) every `de.json` key is used: a key is "used" when it appears as a literal anywhere in those sources, and a
  plural key `x_one/_few/_many/_other` counts as used when its base `x` appears. The allow-list of unused keys is
  **empty** (UI-SPEC 10 names a consumer for every key); adding to it needs a reason in the report.

## 3. Waves

| Wave | Packages | Gate |
|---|---|---|
| 0 | foundation | solution green; the 12 existing web tests green in their new files; `/`, `/receivers`, `/settings/diagnostics` answer (old look) |
| 1 | design-system, i18n-core | solution green; `/_styleguide` screenshots; `DesignTokenTests`, `I18nTests`, `FormattingTests` |
| 2 | shell, player-page, receivers-page, settings-diagnostics | each page screenshot at 3 sizes × 2 themes; its tests |
| 3 | groups-and-pairing, i18n-fill, mobile-polish | every route in every size/theme, de + en + ru; audit passes |
| 4 | review-1 | `docs/web/REVIEW-1.md` |
| 5 | fix-1 | findings closed |
| 6 | review-2 (mobile UX, a11y, i18n focus; emulated-touch pass) | `docs/web/REVIEW-2.md` |
| 7 | fix-2 | findings closed; final screenshot set; full `dotnet test` |

Every `dependsOn` is in an earlier wave. Packages in one wave own disjoint paths (section 4 lists every path; a path
not listed under any package of the wave is frozen for that wave).

## 4. Packages

### `foundation` (wave 0) — depends on: nothing — size: ~700 lines

Builds:
- `src/Bridge.Web/wwwroot/index.html`, complete and final: `<html lang="en">`, viewport
  `width=device-width, initial-scale=1, viewport-fit=cover, interactive-widget=resizes-content`, the two media-scoped
  `theme-color` metas, the pre-paint theme bootstrap of UI-SPEC 2.2 (`window.bridgeTheme`), `window.bridgeLang =
  { get(), setDocument(code) }` with the logic of 2.3 `getLang` (supported codes hard-coded: de, en, fr, es, ru, uk,
  pt), the loading splash (static English, styled with `var(--bg)`/`var(--text)`/`var(--muted)` so it follows the
  tokens once `app.css` defines them), `#blazor-error-ui` (static English, token colours), favicon link
  `type="image/svg+xml"` to `favicon.svg` (the file comes in wave 1; a missing favicon is harmless), the stylesheet
  link. No catalog keys exist for these texts (UI-SPEC 8.1).
- `src/Bridge.Web/wwwroot/js/app.js`: every export of UI-SPEC 2.3 (`getTheme`, `setTheme`, `hoverCapable`,
  `getLang`, `setLang`, `setDocumentLang`, `onOutsideClick` with a document `click` listener, `measure`,
  `rememberActive`, `audit` exactly as 5.6 specifies, accessible-name rules included).
- `src/Bridge.Web/BrowserBridge.cs` (UI-SPEC 2.3; the class only, registration is i18n-core's).
- Route moves on the existing pages, one line each: `Pages/Receivers.razor` → `@page "/receivers"`,
  `Pages/NowPlaying.razor` → `@page "/"`, `Pages/Diagnostics.razor` → `@page "/settings/diagnostics"`; the `href`s in
  `Layout/MainLayout.razor` and in any page that links to the old routes (grep `now-playing`, `href="diagnostics"`,
  `href="/"`), nothing else in those files.
- Test split, no test changed: `tests/Bridge.Tests/Web/ComponentTestBase.cs` (the fixture of `WebComponentTests`:
  `BunitContext`, `BridgeState`, `UseApi` overloads, `PathOf`, `IAsyncDisposable`), `PlayerTests.cs`
  (`Now_playing_shows_the_track_and_sends_transport_commands`), `ReceiversTests.cs`
  (`Receivers_page_lists_receivers_and_connect_applies_the_answer`, `Receiver_slider_and_offset_send_their_values`,
  `Receivers_page_against_the_service_connects_a_fake_receiver_and_sets_its_volume`), `SettingsDiagnosticsTests.cs`
  (`Settings_page_validates_like_the_service_and_saves`, `Diagnostics_page_shows_statistics_and_the_live_log`),
  `GroupsPairingTests.cs` (the two `Pairing_dialog_*` tests, `Group_master_slider_previews_…`,
  `Groups_page_creates_adds_members_and_activates`), `ShellTests.cs`
  (`Notices_show_api_errors_and_can_be_dismissed`); `WebComponentTests.cs` deleted.

Owns: the files above only. Does not touch `app.css`, `LevelSlider.razor`, `Program.cs`, any other test file.

Contract for later waves: the file names above; the `app.js` export names and semantics; `window.bridgeTheme` /
`window.bridgeLang`; `BrowserBridge`'s method names; the route table of UI-SPEC 4.1 (already in force from here on);
the test file per page.

Verify: `dotnet build -warnaserror`; `dotnet test --filter FullyQualifiedName~Bridge.Tests.Web` green with the same
test count as before the split; run the service and `--dump-dom` `/`, `/receivers`, `/settings/diagnostics`,
`/groups`, `/settings` at 1280×900: each renders its old page (no "ambiguous route", no NotFound); `?theme=dark`
sets `data-theme="dark"` on `<html>` and the override meta (visible in the dump).

### `design-system` (wave 1) — depends on: foundation — size: ~1,800 lines

Builds: `wwwroot/css/app.css` rewritten from scratch to UI-SPEC 1 (tokens, the exact theme pattern, base, top bar,
tabs, tools, `.lang-select` and `select.input`, `.theme-picker`/`.theme-btn` incl. the collapse rule, `.status*` with
the pulse keyframes of 7, cards, labels, pills, chips incl. `.chip--members`, buttons, steps, play, rows incl.
`.is-quiet`, radio glyph, artwork, big number, fields, inputs, hints, validation, empty, facts, log, sliders of 1.6
incl. `.track` and `--fill-color`, toasts (`.toast--ok/err/info`), banner, dialog (top-anchored phone sheet), tab
bar, details/summary, `.only-phone`/`.only-wide`, the breakpoints and media-query list of 5.1, the phone overrides
of 5.2/5.3, the touch block of 5.4, the `(hover: hover)` guard on every hover rule, reduced motion);
`wwwroot/favicon.svg`; `Components/LevelSlider.razor` with the contract of UI-SPEC 1.6 (`Variant`, `Label`,
`HideLabel`, `Value`, `Disabled`, `ValueText` with the number-only default, `ShowValue`, the `--fill` style, public
`StepAsync(int delta)`; it keeps `input[type=range]` and `.level output` so the existing tests pass unchanged);
`Pages/Styleguide.razor` at `/_styleguide`: one static page that renders every component class of UI-SPEC 1.5–1.7
and 7 in every state with placeholder text, including a `.btn.is-focus-sample` with the focus ring applied
statically and the dialog sample (no `BridgeState`, no catalog); `tools/web-screenshots.ps1` (2.2, final) and
`tools/screenshot-setups/README.md`; `tests/Bridge.Tests/Web/DesignTokenTests.cs` (2.4, all five checks).

Owns: exactly `wwwroot/css/app.css`, `wwwroot/favicon.svg`, `Components/LevelSlider.razor`, `Pages/Styleguide.razor`,
`tools/web-screenshots.ps1`, `tools/screenshot-setups/README.md`, `tests/Bridge.Tests/Web/DesignTokenTests.cs`.
Does not touch: `index.html`, `app.js`, `BrowserBridge.cs`, any other `.razor`, `Program.cs`, the catalogs, any other
test file.

Contract for wave 2: the class names and token names of the spec are final; `LevelSlider`'s parameters and
`StepAsync` as in UI-SPEC 1.6; the screenshot script's parameters of 2.2.

Verify: `dotnet build -warnaserror`; `DesignTokenTests` green; the existing web tests still pass (the old pages
render with the new CSS); screenshots of `/_styleguide` at 390×844, 768×1024, 1280×900 in light and dark (6 files)
and one `system-dark` via `--force-dark-mode`; visually check in the PNGs: no horizontal scrollbar at 390, the
sticky bar is translucent over scrolled content at 1280, every pill and button is legible in both themes, the live
dot has its halo, the theme picker shows one button collapsed.

### `i18n-core` (wave 1) — depends on: foundation — size: ~1,700 lines

Builds: `Localization/I18n.cs` and `Localization/PluralRules.cs` to UI-SPEC 3.1 (the additions are new code, no
reference implementation); `Resources/i18n/de.json` and `en.json` with every key of UI-SPEC 10 (nested JSON, in the
spec's order); `Bridge.Web.csproj` changes of 3.3 (embedded resources, globalization) and
`tests/Bridge.Tests/Bridge.Tests.csproj` (`InvariantGlobalization=false`, intended for the whole test project);
`Program.cs` (register `I18n` and `BrowserBridge`, culture bootstrap of 3.2 through `IJSRuntime` before `RunAsync`;
`AddBridgeClient` keeps its signature plus an optional `I18n? i18n = null` for tests); `Format.cs` helpers of 3.4
(no `Seconds`); `BridgeCommands.cs` taking `I18n` and using the `notices.*`/`actions.*` keys incl. the
`PlaybackAsync` switch (behaviour unchanged); `SettingsModel.cs` error messages as catalog keys;
`Components/BridgeComponentBase.cs` (`[Inject] I18n L`, re-render on `I18n.Changed`); `Components/LanguageSelect.razor`
(`Variant`, `Id`, 3.2); `Components/StatePill.razor` (UI-SPEC 4.8, both parameters, literal keys via `switch`);
`tests/Bridge.Tests/Web/WebTestSupport.cs` (`UiTest.Create`, `CultureScope`, the module mock of 2.1),
`ComponentTestBase.cs` (register `I18n` so the moved tests keep working), `I18nTests.cs`, `FormattingTests.cs`
(de/en: `Integer`, `Decimal`, `Percent`, `Milliseconds`, `Time`, `DateTime`, `Remaining`; plural categories for all
seven languages with the CLDR sample numbers 0, 1, 2, 5, 11, 21, 22, 25, 101, 111), and the minimal edits to every
existing test that constructs `BridgeCommands` (`WebApiClientTests.cs` lines 144/294/331: pass an `I18n`).

Owns: the files above; in `tests/Bridge.Tests/Web` it may edit every file except `DesignTokenTests.cs` (design-system's)
for the `BridgeCommands` constructor change only. Does not touch: `app.css`, `index.html`, `app.js`,
`BrowserBridge.cs`, `LevelSlider.razor`, pages, layout, `favicon.svg`.

Contract for wave 2: the `I18n` API of UI-SPEC 3.1 exactly; `BridgeComponentBase.L`; `StatePill` parameters;
`LanguageSelect` parameters `Variant` (`Bar` default | `Field`) and `Id`; `UiTest.Create` signature of 2.1;
`Format` helper names of 3.4; `SettingsModel` error messages are the `settings.validation.*` keys.

Verify: `dotnet build -warnaserror`; `dotnet test --filter FullyQualifiedName~Bridge.Tests.Web` green including the
moved tests; `I18nTests`: de/en parity, placeholders, fallback (missing en key → de text → key), `Plural` in all
seven languages (the five that have no catalog yet still resolve categories); `FormattingTests` prove
`1234.5.ToString("N1")` is `1.234,5` under de (i.e. ICU data is really loaded in the test runtime); run the service
and open the app once: it still works (English, old look) and `blazor.boot.json` in the build output lists
`icudt.dat`; `localStorage["airplay-bridge-lang"]="de"` + reload does not break the start and sets `<html lang="de">`.

### `shell` (wave 2) — depends on: design-system, i18n-core — size: ~1,500 lines

Builds: `Layout/MainLayout.razor` to UI-SPEC 4.2 and 5.2 (brand link with app.name, tabs via
`Components/TabLink.razor`, tools, banner, main, toasts, tab bar, dialog host; publishes `--topbar-h`/`--tabbar-h`
through `BrowserBridge.MeasureAsync` in `OnAfterRenderAsync(firstRender)`); `Components/ThemePicker.razor` (UI-SPEC
2.4, full keyboard and touch behaviour, `DotNetObjectReference` for `onOutsideClick`, disposed);
`Components/StatusPill.razor` (UI-SPEC 7) and deletion of `Components/LiveIndicator.razor`; `Components/TabBar.razor`;
`Components/ConnectionBanner.razor` (4.10); `Components/NoticeArea.razor` as toasts (4.9, `.toast--*`,
`.toast__close`); `App.razor` (NotFound text from the catalog with its own `PageTitle`, `FocusOnNavigate`);
`tests/Bridge.Tests/Web/ShellTests.cs` (grows; the moved notices test is updated to the toast selectors);
`tools/screenshot-setups/shell.ps1` (connects Fake Receiver 1 so the status pill is "live" and a toast can be
provoked by a failing call, e.g. `POST /api/receivers/00:00:00:00:00:00/connect`).

Owns: exactly those files. No `BridgeTitle` component (pages write their own `PageTitle`, UI-SPEC 4.1). Does not
touch pages, `app.css`, `app.js`, catalogs.

Verify: `ShellTests`: theme picker renders three radios with `aria-checked`, roving `tabindex`, ArrowRight selects
the next and calls `setTheme`, Escape collapses, with `hoverCapable: false` a pointer click on the collapsed picker
opens instead of selecting and a second click selects, keyboard activation selects at once, the `onOutsideClick`
callback collapses the open picker (invoke the `[JSInvokable]` method directly), a click inside does not;
`StatusPill` maps the four states to class and text (en and de); `ConnectionBanner` shows connecting / paused with
error / offline with the reload button; `NoticeArea` renders `role="alert"` and `.toast--err` for errors, dismisses
through `.toast__close`, auto-dismisses success; `LanguageSelect` change switches `I18n` and the tab labels re-render
in German; `TabBar` marks the active route with `aria-current`. Screenshots of `/` (the old NowPlaying page until
player-page lands, that is fine) and `/settings` at the three sizes in both themes plus 390×844 in de; check: tabs
centred at 1280, hidden at 390 with the tab bar visible, the status pill and theme picker fit at 390 with no
overflow, the toast position above the tab bar at 390 and under the top bar at 1280, the top bar scrolls away at
390 and stays sticky at 1280.

### `player-page` (wave 2) — depends on: design-system, i18n-core — size: ~2,000 lines

Builds: `Pages/Player.razor` (route `/`; `Pages/NowPlaying.razor` deleted) with `Components/NowPlayingHero.razor`,
`Components/MasterVolume.razor` (steppers through `LevelSlider.StepAsync` via `@ref`), `Components/OutputList.razor`
(fieldset + legend, `.only-phone`/`.only-wide` metas and head actions, `is-quiet` rows, the player.noOutput line),
`Components/StatusCard.razor` (four facts, nothing else) to UI-SPEC 4.3 and 5.3; `tests/Bridge.Tests/Web/PlayerTests.cs`
(the moved Now-playing test grows); `tools/screenshot-setups/player-page.ps1`.

Owns: exactly those files. The route `/` is free since wave 0.

Verify: `PlayerTests`: hero shows title/artist/album/times and the remaining time with U+2212, no `.track__knob`
in the markup; device-name fallback "the bridge" while `Status` is null; transport sends play/pause/next/prev and
is disabled without Spotify; master slider for a group sends `PUT /api/groups/{id}/volume` and previews member
fills; for a single receiver sends the receiver route; − and + step by 5 and clamp (through `StepAsync`); the
output radios activate a group / connect a receiver and show `.is-active` from `State.Output`; non-output receiver
rows have `is-quiet`; offline receivers' radios are disabled; PIN receivers show the Pair button and the compact
slider is absent; Stop output clears; the no-output line contains "Nothing selected" when nothing is selected and
is absent otherwise; the legend names the fieldset (no `aria-label`); Status card facts in en and de
(`"1 of 2 online"` / `"1 von 2 online"`); `en`/`de` members chip text. Screenshots of `/` at the three sizes in
both themes with the fake receivers (setup: Fake Receiver 1 connected as the output: `POST
/api/receivers/02:FA:CE:00:00:01/connect`) and once with a group of both fakes active (`POST /api/groups`, members,
activate; the setup script takes `-Scenario receiver|group`), in en and de at 390.

### `receivers-page` (wave 2) — depends on: design-system, i18n-core — size: ~1,200 lines

Builds: `Pages/Receivers.razor` to UI-SPEC 4.4 (route `/receivers`, in force since wave 0);
`tests/Bridge.Tests/Web/ReceiversTests.cs` (the three moved tests with selectors and texts updated: `.badge` →
`.pill`, `"Connected"` → `"connected"`, `"PIN pairing needed"` → `"PIN needed"`, `"Available"` → `"online"`, plus
details/offset and tag coverage); `tools/screenshot-setups/receivers-page.ps1` (one fake connected).

Owns: exactly those files.

Verify: tests above green; screenshots of `/receivers` at the three sizes in both themes (two fake receivers, one
connected) in en and de (390): two cards per row at 768, three at 1280, one at 390; the big number and slider are
legible; `details` collapsed by default; the Connect button's `title` present.

### `settings-diagnostics` (wave 2) — depends on: design-system, i18n-core — size: ~1,400 lines

Builds: `Pages/Settings.razor` (UI-SPEC 4.6, incl. the Appearance fieldset with the plain theme radios through
`BrowserBridge` and `<LanguageSelect Variant="Field" Id="settings-language" />` under a `<label for>`),
`Pages/Diagnostics.razor` (4.7, route `/settings/diagnostics` in force since wave 0, back link, the four cards with
the listed facts incl. the retransmit ratio, culture formatting); `tests/Bridge.Tests/Web/SettingsDiagnosticsTests.cs`
(the two moved tests grow: validation messages come from the catalog in de; Appearance radios call `setTheme`; the
language select has a `<label for>`; diagnostics numbers are formatted per culture, e.g. `1.234` under de; the
retransmit ratio shows `0,4 %` under de and `–` with zero packets; the live-updates word comes from status.*; the
log level filter); `tools/screenshot-setups/settings-diagnostics.ps1` (one fake connected so the statistics move).

Owns: exactly those files.

Verify: tests green; screenshots of `/settings` and `/settings/diagnostics` at the three sizes in both themes, de at
390 (the longest hints); check: form fields 44px on phone, both selects show their chevron in both themes, the log
box scrolls inside the card and does not widen the page.

### `groups-and-pairing` (wave 3) — depends on: shell, player-page, receivers-page — size: ~1,900 lines

Builds: `Pages/Groups.razor` (list, `/groups`), `Pages/GroupEditor.razor` (`/groups/new`, `/groups/{id}`) to UI-SPEC
4.5; `Components/PairingDialog.razor` restyled to 4.11 (top-anchored sheet on phones, `rememberActive`/`restore`,
Tab trap, Escape, catalog texts incl. pairing.failed/failedDetail); deletion of `Components/StateBadge.razor` (its
last user was the old `Groups.razor`); `tests/Bridge.Tests/Web/GroupsPairingTests.cs` (the four moved tests grow);
`tools/screenshot-setups/groups-and-pairing.ps1`.

Owns: exactly those files. Does not touch `app.js` (foundation delivered `rememberActive`), `app.css`.
settings-diagnostics is not a dependency: nothing here uses the Settings or Diagnostics pages or their tests.

Verify: tests: create → navigates to the editor; rename (input named by groups.name); add/remove members; master
preview and clamp hint; activate and stop; delete with confirm → list; unknown id → not found; dialog opens on
`pairingRequired`, calls `rememberActive` and focuses the PIN input, Tab on the cancel button focuses the PIN input
and Shift+Tab on the PIN input focuses the cancel button (JSInterop `focus` calls), wrong PIN → rejected + Start
again, failed with/without detail picks the right key, Escape cancels and `restore` is called, cancel calls nothing;
de texts. Screenshots: `/groups`, `/groups/<id>` (a group of both fakes), and `/` with the dialog open (the setup
script triggers `POST /api/receivers/<id>/pair` on a PIN fake if the dev switch offers one, otherwise the state is
injected on the `/_styleguide` dialog sample) at the three sizes in both themes; the phone sheet sits at the top
with the PIN field in the upper half.

### `i18n-fill` (wave 3) — depends on: shell, player-page, receivers-page, settings-diagnostics — size: ~1,300 lines

Builds: `Resources/i18n/fr.json`, `es.json`, `ru.json`, `uk.json`, `pt.json` (complete translations of UI-SPEC 10;
ru/uk with `_one/_few/_many/_other`, fr/pt with `_one/_other` under their rules), `tests/Bridge.Tests/Web/I18nTests.cs`
extended (parity for all seven, placeholder parity, plural variant presence, `Plural` with the real catalogs),
`CatalogUsageTests.cs` (2.4, empty allow-list), and `FormattingTests.cs` extended for ru/uk/pt/fr/es (`Time`,
`Integer`, `Percent`); `tools/screenshot-setups/i18n-fill.ps1`.

Owns: exactly those files (the two test files pass from i18n-core to i18n-fill here; i18n-core is finished).

Verify: tests green; screenshots at 390×844 light of `/`, `/receivers`, `/settings` in ru and de (the longest
strings) and 1280×900 in fr; check: no truncated tab labels in the tab bar, the top bar does not wrap, pills do not
overflow their rows, the Settings hints wrap cleanly, the members chip wraps instead of overflowing.

### `mobile-polish` (wave 3) — depends on: shell, player-page, receivers-page, settings-diagnostics — size: ~1,200 lines

Builds: `Pages/Audit.razor` (UI-SPEC 5.6: trigger after `State.Loaded` + render + 500 ms) and `tools/web-audit.ps1`
(2.3); the responsive pass over `app.css` only (the page packages' markup is fixed by then; mobile-polish may edit
`app.css` and, for layout-only attribute changes such as a missing `min-width: 0` class, the wave-2 page and
component `.razor` files — `Player.razor`, `NowPlayingHero`, `MasterVolume`, `OutputList`, `StatusCard`,
`Receivers.razor`, `Settings.razor`, `Diagnostics.razor`, `MainLayout`, `TabBar`, `NoticeArea`, `ConnectionBanner` —
with a note in the report; **not** `Groups.razor`, `GroupEditor.razor`, `PairingDialog.razor`, which
groups-and-pairing owns in this wave); landscape rules (`max-height: 500px`), `env(safe-area-inset-*)` on every fixed
element, the 44px touch block, the `(hover: hover)` guard; `README.md` web section (how to run with the fakes, the
screenshot and audit scripts); `tools/screenshot-setups/mobile-polish.ps1`.

Verify: `tools/web-audit.ps1` passes for `/`, `/receivers`, `/settings`, `/settings/diagnostics`, `/groups` at 390
and 768 in both themes (`small` empty apart from the allow-list); screenshots of every route at 390×844, 844×390,
768×1024, 1280×900 in both themes (en), the full set listed in the report; manual check in the PNGs: the phone hero
matches the phone mockup (minus the knob), thumbs can reach the sliders (range inputs are 44px tall), the tab bar
never covers content (last card fully visible when scrolled to the end: add a screenshot taken with
`--window-size=390,2200` to see the page end), toasts sit above the tab bar, the top bar scrolls away on the phone.

### `review-1` (wave 4) — depends on: everything in waves 0–3 — size: ~600 lines (a report)

Does: reads UI-SPEC.md and every file of `src/Bridge.Web` and `tests/Bridge.Tests/Web`; runs `dotnet build
-warnaserror`, the full `dotnet test` (excluding the live categories), `tools/web-screenshots.ps1 -Package review-1`
over every route, size and theme in en and de, and `tools/web-audit.ps1`; compares every page against the mockups
and the spec section by section (tokens, theme pattern, picker behaviour, catalog completeness and the empty
allow-list, routes, copy, breakpoints and the media-query list, a11y list of UI-SPEC 6, status mapping of 7, the
"what stays" and the 8.1 "not built" lists); writes `docs/web/REVIEW-1.md`: a numbered findings list, each with
severity (blocker / major / minor / nit), file and line, the spec reference, what is wrong, and the expected fix;
plus a "looks right" list so fix-1 does not undo good work. No code changes.

### `fix-1` (wave 5) — depends on: review-1 — size: ~1,200 lines

Does: fixes every blocker and major and the minors that are cheap, in the files they concern (this package owns all
of `src/Bridge.Web`, `tests/Bridge.Tests/Web`, `tools/`, `docs/web/UI-SPEC.md` for spec corrections); marks each
finding in `REVIEW-1.md` as fixed / deferred with a reason; re-runs the tests, the audit and the screenshot set.

### `review-2` (wave 6) — depends on: fix-1 — size: ~500 lines (a report)

Does: a second pass with three focuses.

1. **Mobile UX** on the 390×844 and 844×390 screenshots of every route and state (now-playing with and without a
   track, output with a group, the pairing sheet, a toast, the banner): thumb reach, target sizes (the audit's
   `small` list must be empty except `.theme-btn`/`.lang-select`), nothing cut off, no horizontal scroll, text sizes
   ≥ 16px in inputs, safe areas. **Emulated-touch pass**: start Edge with `--remote-debugging-port=9222
   --window-size=390,844` and, through the CDP (the `browser-use` CLI or `chrome-devtools-cli` skill if available;
   otherwise a 40-line PowerShell WebSocket script), call `Emulation.setTouchEmulationEnabled {enabled:true}`,
   `Emulation.setEmulatedMedia {features:[{name:"pointer",value:"coarse"},{name:"hover",value:"none"}]}`, then
   navigate to `/` and `/settings` and capture screenshots and `matchMedia` results: the `(pointer: coarse)` rules of
   5.4 apply (44px rows, 24px thumbs), a tap on the theme picker opens it (`theme-picker--open` present, no
   selection change), a second tap selects, and no `:hover` colour sticks after a tap. If no CDP route works in the
   environment, the report says so and lists these as **manual checks**, together with what is always manual: the
   on-screen keyboard over the pairing sheet on iOS Safari and Android Chrome, `env(safe-area-inset-*)` on a notched
   device, scroll and drag feel of the 44px slider band, `<select>` picker sheets on iOS.
2. **Accessibility** per UI-SPEC 6: names, roles, keyboard paths traced in the bUnit tests (incl. the dialog Tab
   trap and focus return), `DesignTokenTests` present and green incl. the raw-colour grep, focus ring checked through
   the CSS rules and the styleguide's `.is-focus-sample` (focus cannot be shown headlessly), the audit's `unnamed`
   list empty on every route, every fieldset named by a legend only.
3. **i18n**: open the app in de, fr, ru, uk, pt, es screenshots at 390 and 1280 and look for overflow, untranslated
   keys (a key path on screen), wrong plurals, wrong number formats (`0,4 %` in de, `0.4%` in en, `1.234` / `1,234`),
   `<html lang>` updated, the Settings language field labelled.

Writes `docs/web/REVIEW-2.md` in the same format, also re-checking that every REVIEW-1 item marked fixed is fixed.

### `fix-2` (wave 7) — depends on: review-2 — size: ~900 lines

Does: closes REVIEW-2 findings (same ownership as fix-1), runs the full test suite, regenerates the complete
screenshot set (`-Package final`, every route × 4 sizes × 2 themes × en, de, ru) and the audit, updates `README.md`
if a user-facing behaviour changed, and writes the final section of `REVIEW-2.md` ("state at the end": open items,
if any, with reasons, and the manual checks that remain).

## 5. Package reports and the orchestrator

Each package returns: files changed (absolute paths), tests added (names), screenshot paths, deviations, open
questions. The orchestrator gates each wave on: solution builds with warnings as errors, `dotnet test --filter
FullyQualifiedName~Bridge.Tests.Web` green, screenshots present for the package's routes (wave 0: the dump-dom
route check instead). Waves 4–7 are the review cycles the user asked for; a third review is not planned but
review-2 may recommend one if fix-2 would be large.
