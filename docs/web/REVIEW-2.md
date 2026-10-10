# Review 2: web app redesign (variant C), after fix-1

Package `review-2` (wave 6, WORK-PLAN 4). This is a report only, and no code was changed. It has three focuses: mobile UX
(with an emulated-touch pass), accessibility (UI-SPEC 6) and i18n. It also re-checks every REVIEW-1 item that fix-1
marked fixed. `fix-2` works through the findings below and marks each one **fixed** or **deferred (reason)** in place.
It also writes the closing "State at the end" section.

## What was run

| Check | Result |
|---|---|
| `dotnet build AirplayBridge.sln -warnaserror` | green: 0 warnings, 0 errors (`build.log`) |
| `dotnet test tests/Bridge.Tests --filter FullyQualifiedName~Bridge.Tests.Web` | 423/423 passed (`test-web.log`) |
| `dotnet test AirplayBridge.sln --filter "Category!=SpeakerALive&Category!=ShairportLive"` | `Bridge.Tests` 509/509, `Airplay2.Sender.Tests` 566/566. The sender flake from review-1 and fix-1 did not occur this time (`test-full.log`). |
| `tools/web-screenshots.ps1` (service on port 18092, `--Bridge:FakeReceivers=2`, `mobile-polish.ps1` setup: group "Downstairs" active, both fakes connected) | `phone/`: 7 routes plus `/_styleguide?dialog=1` × 390×844 × light, dark × en, de (32). `i18n/`: 7 routes × 390×844, 1280×900 × fr, ru, uk, pt, es, de (84). `nooutput/`: `/` and `/groups` before the setup (no output, no group) × 390×844, 844×390 × light, dark (8). The script reported no horizontal overflow. |
| `mobile-polish.ps1 -HeroShots` | `hero/`: the Player with a demo track at 390×844 and 844×390 in light and dark (en), plus 390×844 in ru (5) |
| Emulated-touch pass (CDP, see below) | `touch/`: `/` and `/settings` at 390×844 and 844×390 with `pointer: coarse` and `hover: none`, a JSON report per size, plus picker crops (14) |
| Real app states over CDP | `states/`: the **real** pairing dialog (a `pairingRequired` event dispatched on the app's own WebSocket) at 390×844 and 844×390, each also with a keyboard-sized viewport; the connection banner (WebSocket pointed at a dead port) at both sizes, en and de; an error toast (first `GET /api/status` answered 502) at both sizes; safe-area insets through `Emulation.setSafeAreaInsetsOverride`; the styleguide focus sample in both themes (14) |
| Landscape with touch emulated | `landscape-touch/`: the 7 routes at 844×390, light en and dark de (14), each with an `audit()` result |
| `tools/web-audit.ps1` | **84/84 passed**: the 7 routes × 390×844, 768×1024, 844×390 (touch) × light, dark × en, de (`audit.log`). **49/49 passed**: the 7 routes × 390×844 light × en, de, fr, ru, uk, pt, es (`audit-langs.log`). In every run, overflow is 0 and `unnamed` is empty. `small` holds only the allow-listed `.theme-btn.is-active`, and `select#lang-select` from 700px with a coarse pointer. The language audits ran at 390 only, and the 768 audit only in en and de, so the 700–767px band in the long languages was not covered (finding 12). |
| Tablet widths in the long languages (added when the review findings were checked) | `tablet/`: `/` and `/receivers` × 700×900, 768×1024, 1024×768 × light × fr, pt, ru, uk (24). At 700×900 the brand name, the top-bar language select and the status pill are truncated in pt, ru and uk (finding 12); at 768 and 1024 everything fits. |
| Runtime language switch (CDP, `lang-probe.ps1`) | Choosing uk on Settings sets `<html lang="uk">`, stores `airplay-bridge-lang`, and switches the title, the tab bar and both selects at once. After a reload the page stays uk. `?lang=de` and `?lang=en` override the stored choice once. |

Everything is in `artifacts/screenshots/review-2/` (git-ignored). The probe scripts are in `probes/` (`cdp.ps1`
helpers, `touch-pass.ps1`, `states.ps1`, `lang-probe.ps1`). fix-2 can rerun them against a service started with the
fakes. `montage/` holds side-by-side sheets of the language sets.

### Emulated-touch pass (WORK-PLAN review-2, item 1)

The `browser-use` and `chrome-devtools` MCP servers did not connect in this session, so the pass used a PowerShell
WebSocket client over the DevTools protocol (`probes/touch-pass.ps1`). It started headless Edge with
`--remote-debugging-port=9222 --window-size=390,844`. Like the screenshot script, it set the viewport with
`Emulation.setDeviceMetricsOverride` (with `mobile: true`), because headless Edge never lays out narrower than 504px.
It then called `Emulation.setTouchEmulationEnabled {enabled: true, maxTouchPoints: 5}` and `Emulation.setEmulatedMedia`
with `pointer: coarse`, `hover: none`, `any-pointer: coarse` and `any-hover: none`. Taps are
`Input.dispatchTouchEvent` touchStart/touchEnd pairs.

| Check | 390×844 | 844×390 (the coarse half of the query alone) |
|---|---|---|
| `matchMedia`: `(pointer: coarse)` / `(hover: hover)` / the 5.4 touch query | true / false / true | true / false / true |
| Range inputs (height) | 44px | 44px (hero and both compact sliders) |
| Slider thumbs (UA shadow `#thumb` box via `DOM.getBoxModel`) | 24×24 | 24×24 on all three |
| `.step`, `.play`, `.btn--sm`, Output row labels, card-head links | 44, 56, 44×86, 59, 44 | 44, 48, 44×85–89, 63, 44 |
| Settings: `.field--check` rows, `.input`s, `.btn`s | 44 each | 44 each |
| Settings: font size of every `input`/`select` | 16px (all 9) | 16px (all 9) |
| `audit()` on `/` and `/settings` | overflow 0, unnamed none, small = `.theme-btn.is-active` | the same plus `select#lang-select` (allowed) |
| Theme picker, first tap on the pill | `theme-picker--open`, selection unchanged ("System"), all three 34px | the same, 26px |
| Second tap on "Dark" | selects Dark (`data-theme="dark"`, stored), `--open` removed, focus blurred to `<body>` | the same |
| ... 1 s later | **still unfolded** (34/34/34): the tapped button keeps `:hover` → finding 1 | the same (26/26/26) |
| Tap elsewhere | collapses (0/34/0) | collapses |
| Sticky hover **colour** after tapping `+` | none: border and background unchanged although the button matches `:hover` (every colour rule is under `(hover: hover)`) | none |
| Settings Appearance "Light" row tapped on its text | radio checked, `data-theme="light"`, top-bar picker follows | the same |

## Findings

Severities: **blocker** (broken or unusable), **major** (breaks a spec rule a user notices, or a real defect),
**minor** (a deviation or UX weakness worth fixing in fix-2), **nit** (cosmetic, or a spec text correction).

There are no blockers. The majors are 1, 2 and 12. Finding 12 was added when the review was checked; it takes the
next free number so the references to 1–11 stay valid.

### 1. On touch screens the theme picker stays unfolded after a choice: sticky `:hover` holds it open (major, mobile UX; REVIEW-1 #3 not closed on touch)

- **Where:** `src/Bridge.Web/wwwroot/css/app.css:260-265` (`.theme-picker:not(:hover):not(:focus-within):not(.theme-picker--open) …`)
  and the phone variant at `:912-916`. `tests/Bridge.Tests/Web/DesignTokenTests.cs:312-314` exempts `:not(:hover)`
  from the hover-guard test.
- **Spec:** 2.4 ("selecting collapses it"), 5.1 (every `:hover` rule sits in `(hover: hover)` "so touch devices do not
  get sticky hover states"), REVIEW-1 #3 ("review-2's emulated-touch pass should confirm the collapse").
- **What is wrong:** fix-1's blur works: after the second tap, focus is on `<body>` and `theme-picker--open` is gone.
  But mobile Chromium (and iOS Safari) leaves `:hover` on the last tapped element until the next tap elsewhere. The
  collapse rule is not guarded and requires `:not(:hover)`, so all three options stay unfolded after the user picked
  one. The emulated run shows this directly: `pickerHover: true`, widths `34/34/34` one second after the selecting tap,
  and `0/34/0` only after a tap on the hero (`touch/touch-pass-390x844-light-en.json`,
  `touch/touch-picker-selected-390x844-light-en.png`). The same thing happens at 844×390. The first tap also unfolds
  the pill through `:hover` alone, which is harmless.
- **Expected fix:** key the touch collapse on a class instead of on `:not(:hover)`. `ThemePicker` adds
  `theme-picker--touch` to the group when `hoverCapable()` is false (it already reads that value for the first-tap
  branch, 2.4). The collapse rules keep their current form for hover-capable pointers and get a touch twin that has no
  `:hover` in it: `.theme-picker--touch:not(:focus-within):not(.theme-picker--open)` with the same declarations
  (`{ gap: 0 }`, `.theme-btn:not(.is-active) { width: 0; opacity: 0; pointer-events: none }`, and the phone
  collapsed-look rules in the `(max-width: 699px)` block). The existing `:not(:hover)` rules get a
  `:not(.theme-picker--touch)` so only one of the two applies. A sticky `:hover` on a touch screen then no longer
  holds the pill open, and the desktop hover unfold is untouched.
  Do **not** use the simpler-looking variant "drop `:not(:hover)` from the base rule and restore the unfold with
  `.theme-picker:hover …` inside `(hover: hover)`": the collapse rule
  `.theme-picker:not(:focus-within):not(.theme-picker--open) .theme-btn:not(.is-active)` has specificity (0,5,0) and the
  gap rule (0,3,0), so the unfold rules `.theme-picker:hover .theme-btn:not(.is-active)` (0,4,0) and
  `.theme-picker:hover { gap: 2px }` (0,2,0) lose regardless of source order, and desktop users would silently lose the
  hover unfold. The phone collapsed look would also need a hover restore under `(max-width: 699px) and (hover: hover)`,
  a query that the closed list of 5.1 (and `DesignTokenTests`) rejects. If an unfold rule is ever placed in the
  `(hover: hover)` block instead, it must carry the same `:not()` chain so the specificity is equal and source order
  decides.
  `DesignTokenTests.HoverPseudo`: remove the `:not(:hover)` exemption if no unguarded `:not(:hover)` remains;
  otherwise narrow it to the documented desktop collapse rules, which never match on a touch screen once the touch
  class is set. Update the 2.4 collapse rule text and the bUnit test that covers `hoverCapable() == false` (assert the
  class).
  Verify both input types: re-run `probes/touch-pass.ps1` (`picker2_afterSecondTap.widths` must read `0/34/0` at
  390×844, and `0/26/0` at 844×390, or `0/34/0` there if finding 5 is adopted). Add a mouse-hover probe (CDP
  `Input.dispatchMouseEvent` `mouseMoved` over the pill with `hover: hover` emulated) or a styleguide check that proves
  the desktop pill still unfolds to `26/26/26` on hover and collapses to `0/26/0` when the pointer leaves, so the
  desktop unfold is proven rather than assumed.

### 2. In English the connection banner shows a raw runtime resource key: "Live updates paused: net_webstatus_ConnectFailure Retrying shortly." (major, i18n/UX)

- **Where:** `src/Bridge.Web/Components/ConnectionError.cs:30` (`_ when l.Code == English => error`), used by
  `ConnectionBanner` (banner.pausedError) and `StatusPill` (status.title.paused/offline). Screenshots
  `states/banner-player-390x844-light-en.png`, `states/banner-player-844x390-light-en.png`.
- **Spec:** 3.5 (client text "shown as is while the language is English"), 4.10, 7. WORK-PLAN 1 (no key paths on screen).
- **What is wrong:** the WASM runtime ships without its exception message resources and reports the resource *key*
  as `WebSocketException.Message`. That happens even in the Debug build that `dotnet run` serves. When the bridge is
  unreachable (a restart, Wi-Fi lost), which is the most common reason for this banner, an English user reads an
  identifier. Because the key has no full stop, the sentence also runs on into "Retrying shortly.". In de, the same
  state correctly reads "Live-Updates pausiert. Es wird gleich erneut versucht." (`states/banner-player-390x844-dark-de.png`).
- **Expected fix:** show the runtime text in no language. Keep the mapping of the known sentence to
  banner.closedByBridge. For every other error, use the error-free sentences (banner.paused + banner.retrying,
  status.title.* without `{{error}}`) in English too, and log the detail to the console as the other languages already
  do. Update UI-SPEC 3.5 and `ShellTests.Connection_banner_never_shows_the_clients_english_in_another_language`, which
  should become "…never shows the client's runtime text" and assert en as well. If an English detail is still wanted,
  accept it only when it is a sentence (for example, it ends in `.`, `!` or `?` and contains a space), which drops
  `net_webstatus_*` keys.

### 3. Landscape phones get the centred dialog, and with the keyboard open the PIN field ends below the visible part of the dialog (minor, mobile UX)

- **Where:** `app.css:819-840` (`.scrim` centres `.dialog` at ≥ 700px) and `:1022-1033` (the `(max-height: 500px)` block
  has no dialog rule). Screenshots `states/dialog-844x390-light-en.png` and
  `states/dialog-844x390-light-en-kbd190.png`. The second simulates `interactive-widget=resizes-content` with a 200px
  layout viewport.
- **Spec:** 4.11 (a top-anchored sheet "so the PIN field stays above the on-screen keyboard"; that rule is width-only,
  ≤ 699px), 5.1 (landscape phones get the tablet shell).
- **What is wrong:** at 844×390 the dialog is centred (62–328px), which is fine. With a landscape keyboard, though, the
  visible viewport is about 150–200px. The dialog then becomes a 168px scroll box: the title and the two-line prompt
  fill it, and the PIN field (152–205px) and the Pair/Cancel buttons sit below its visible bottom (184px). Chrome
  usually scrolls the focused field into view, but the dialog's scroll position is left to the browser. In portrait
  (390×844 → 508px with the keyboard) everything fits (dialog 12–278px).
- **Expected fix:** add `.scrim { align-items: start }` and a smaller dialog padding/gap to the `(max-height: 500px)`
  block (the same reasoning as the phone sheet). Optionally, after focusing the PIN field, call
  `scrollIntoView({block: "nearest"})` on it (an `app.js` helper) so the field is visible whatever the browser does.
  Keep it on the manual list for a real device (below).

### 4. Diagnostics at ≥ 700px: long fact labels squeeze the address list into a 60–90px column that breaks IP addresses mid-number (minor, i18n/layout)

- **Where:** `app.css:609-611` (`.facts { grid-template-columns: max-content 1fr }`, `dd { overflow-wrap: anywhere }`),
  `Pages/Diagnostics.razor:48-70` (`dd.addresses`). Screenshots `i18n/settings-diagnostics-1280x900-light-{pt,es,de}.png`,
  crop `montage/crop-diag-pt-1280.png`.
- **Spec:** 4.7 (the addresses are in `--font-mono` in the card), 5.2 ("nothing cut off"), WORK-PLAN review-2 item 3
  (overflow in longer languages).
- **What is wrong:** the Network card is one cell of the `minmax(320px, 1fr)` grid. The label column takes the longest
  label's max-content width: pt "Despertares atrasados do temporizador", es "Despertares tardíos del temporizador",
  de "Verspätete Pacer-Aufwachen". That leaves the value column a few characters wide, so "203.0.113.150" wraps as
  "203.0.113. / 150", and the five addresses become a 30-line strip. It does not overflow, but it is hard to read, and
  copying an address from it is error-prone. Phones are fine, because the facts are stacked there (5.3).
- **Expected fix:** give the address list the full card width at every size. Either `dd.addresses { grid-column: 1 / -1 }`
  with its `dt` on its own row (`.facts dt:has(+ dd.addresses) { grid-column: 1 / -1 }`), or render the addresses as a
  separate block under the `dl`. Also consider `minmax(0, max-content) minmax(9rem, 1fr)` for the grid, so a long label
  wraps before a value does. Check pt and es at 768 and 1280.

### 5. On coarse-pointer screens wider than 699px the theme buttons stay 26px (minor, mobile UX)

- **Where:** `app.css:244-258` (26px), `:910-911` (34px only in the `(max-width: 699px)` block), touch block `:1036-1065`.
  Measured in `touch/touch-pass-844x390-light-en.json` (`themeBtns: 26x26`). Screenshot `touch/touch-player-844x390-light-en.png`.
- **Spec:** 2.4 (26px at ≥ 700px, 34px at ≤ 699px; allow-listed), 5.4.
- **What is wrong:** on a landscape phone or a touch tablet, the picker is the only top-bar control that stays below
  the 34px phone size. It is 26px next to 44px tabs, and that is close to the 24px floor of WCAG 2.5.8. The top bar has
  room for the 34px variant at 844px (the tools cluster is about 290px wide).
- **Expected fix:** add `.theme-btn { width: 34px; height: 34px } .theme-btn .ico { width: 18px; height: 18px }` to the
  `(max-width: 699px), (pointer: coarse)` block. The phone block keeps its collapsed-look rules. Update the 2.4 text
  ("34px on phones and coarse pointers").
- **Width budget not yet measured at tablet widths:** only the 844×390 budget above was checked. At 700px the top bar
  already truncates in pt, ru and uk with the 26px picker (finding 12). At 768px in pt it has only a little slack with
  the collapsed pill, and the open pill grows by about 28px more with 34px buttons. The audit measures only the
  collapsed pill. Before adopting the 34px rule, probe 700×900 and 768×1024 with touch emulated
  (`probes/touch-pass.ps1` can do it), `lang=pt` and `theme-picker--open` present, and assert overflow 0 and an
  untruncated `.status__text`. Adopt it only together with the shrink rules of finding 12.

### 6. Group editor member sliders on phones have a track of about 70px (minor, mobile UX)

- **Where:** `app.css:959` (`.row .level--compact { width: 120px }` on phones). Screenshots
  `phone/groups-<id>-390x844-*.png`, `i18n/groups-<id>-390x844-light-*.png`.
- **Spec:** 5.3 (the 120px rule), 5.4 (the 44px slider band).
- **What is wrong:** the 120px box holds the track and the number, so the draggable track is about 70px for 0–100.
  That is roughly 1.4 levels per CSS px: a thumb can barely hit a chosen value, and the start of a drag already moves
  the level by several steps. The 44px band makes the slider easy to grab but not precise. The Receivers page's full
  slider is the precise path, but the member level r_i is set only here.
- **Expected fix:** on phones, put the member slider on its own line under the name and pill. For example, give the
  member row `flex-wrap: wrap` and the `.level--compact` `flex: 1 1 100%; width: auto` at ≤ 699px, keeping the remove
  button on the first line. The slider then spans the card (about 300px). Add a row to the 5.3 table to replace the
  120px rule.

### 7. The page behind the pairing dialog still scrolls on touch (nit, mobile UX)

- **Where:** `Components/PairingDialog.razor` (no scroll lock), `app.css:819`. Measured: `body` overflow stays
  `visible` while the dialog is open (`states/*dialog*` JSON in the run log).
- **What is wrong:** a swipe on the scrim scrolls the Player underneath (scroll chaining). iOS Safari is especially
  prone to this while the keyboard is up. The dialog itself is fine.
- **Expected fix:** `.scrim { overscroll-behavior: contain; touch-action: none }` plus `.dialog { touch-action: pan-y;
  overscroll-behavior: contain }`, or a `:root:has(.scrim) { overflow: hidden }` rule. The second is simplest and needs
  no JS; check that the 5.1 media-query list is unaffected.

### 8. The long stream options are cut off in the collapsed `<select>` on phones (nit, mobile UX/i18n)

- **Where:** `Pages/Settings.razor:74` (`InputSelect id="stream"`), catalogs settings.streamRealtime /
  settings.streamBuffered. Screenshots `phone/settings-390x844-*-de.png`, `i18n/settings-390x844-light-*.png`:
  "Realtime (RTP über UDP, NTP- oder PT…", "Temps réel (RTP sur UDP, synchronisat…".
- **What is wrong:** the closed select shows a truncated choice in every language at 390px. The native picker sheet
  shows the whole text, but the field at rest does not.
- **Expected fix:** shorten the option texts to the mode name ("Realtime" / "Buffered" and the translations) and move
  the transport detail into settings.streamHint, which already explains the modes. Change all seven catalogs.

### 9. "1 events": the event count in diagnostics.liveUpdatesValue has no plural (nit, i18n)

- **Where:** `Resources/i18n/{en,de,fr,pt,es}.json:278-279` ("{{count}} connection, {{events}} events"), consumer
  `Pages/Diagnostics.razor` (`Plural` on the connection count only).
- **What is wrong:** the plural follows `count`, the connections, while `events` is always plural. With exactly one
  event (right after a reconnect) en reads "1 events", de "1 Ereignisse", fr "1 événements". ru and uk avoid this with
  "событий: N" / "подій: N".
- **Expected fix:** use the ru/uk pattern in the other five catalogs ("events: {{events}}", "Ereignisse: {{events}}",
  …). That needs no code change, and the parity and placeholder tests stay green.

### 10. The toast region is an unnamed `complementary` landmark (nit, a11y; needs a spec change)

- **Where:** `Components/NoticeArea.razor:6` (`<aside class="toasts" aria-live="polite">`).
- **Spec:** 4.9 names the element ("A fixed `aside.toasts` (`aria-live="polite"`)"), and 6.1 requires
  `role="status"` on non-error toasts, `role="alert"` on error toasts and `aria-live="polite"` on the toast region. The
  `aside` is therefore specified, and changing it is a spec change. `ShellTests` selects `aside.toasts`
  (`ShellTests.cs:559` and `:684`).
- **What is wrong:** `<aside>` exposes a complementary landmark that is always present and has no name. A screen-reader
  landmark list shows an empty "complementary" entry, which is noise next to the named `nav`s.
- **Expected fix:** make it `<div class="toasts" aria-live="polite">`. **Keep the container's `aria-live`**: a live
  region must exist before its content changes (the reason `ConnectionBanner` keeps its always-present
  `div.banner-slot`, 4.10), and a `role="status"` node inserted into the DOM is not announced reliably on its own, so
  dropping the container's `aria-live` would make success and info toasts unreliable. Keep `role="alert"` on error
  toasts (assertive, intended). For the others, either keep `role="status"` or drop it to avoid a double announcement
  from the nested live region; the container's `aria-live` already covers them. Update UI-SPEC 4.9 (the element) and
  6.1 (the role rule, if `role="status"` is dropped) in the same change, and change the two `ShellTests` selectors from
  `aside.toasts` to `.toasts`.

### 11. Tooling: headless Edge processes were left over after this review's runs (nit, tooling; reproduce before fixing)

- **Where:** `tools/web-screenshots.ps1` `Stop-Tree` (`taskkill /PID <launcher> /T /F`), the same helper in
  `tools/web-audit.ps1` and `tools/screenshot-setups/mobile-polish.ps1`.
- **What was seen:** after this review's runs, 22 `msedge.exe` processes with temporary `bridge-screens-*` profiles
  were still alive and had to be stopped by hand. Leftovers keep the temp profiles locked, so `Remove-Item -Recurse`
  fails silently.
- **Not reproduced as attributed:** a later full 24-screenshot run of `tools/web-screenshots.ps1` (own port 18093,
  `mobile-polish.ps1` setup) left no `msedge.exe` with a `bridge-screens` profile, so `Stop-Tree` worked. When the
  findings were checked, no Edge process with a `bridge-*` or `r2-edge-*` profile was running. The probes in
  `probes/` name their profiles `r2-edge-*` (`cdp.ps1:51`), and the hero shots and the audit use `bridge-hero-*` and
  `bridge-audit-*`, so they cannot explain `bridge-screens-*` leftovers. The likelier cause is a run aborted mid-way
  (a tool timeout that killed PowerShell before its `finally` block ran), which no `Stop-Tree` change can catch.
- **Expected fix:** reproduce first, with the tools alone. After one complete run of each script
  (`web-screenshots.ps1`, `web-audit.ps1`, `mobile-polish.ps1 -HeroShots`, and `probes/touch-pass.ps1` through
  `cdp.ps1`), count the `msedge.exe` processes whose command line contains that script's profile prefix
  (`Get-CimInstance Win32_Process -Filter "Name='msedge.exe'"`, match on `CommandLine`). Only if a complete run leaks:
  after `taskkill`, also stop the processes whose command line contains the run's `--user-data-dir`, or close the
  browser over the DevTools protocol (`Browser.close`) before killing it. If only the probes leak, fix `probes/cdp.ps1`
  (`Browser.close` before `taskkill`). If nothing leaks, mark this finding "not reproduced"; at most, let each script
  stop stale processes of its own profile prefix at start-up, so aborted runs do not pile up.

### 12. At 700–760px the top bar truncates the brand, the language select and the status pill in pt, ru and uk (major, i18n/mobile UX)

- **Where:** `app.css:162-232` (`.topbar`, `.brand__name` with `min-width: 0` and ellipsis, `.tabs { flex: none }`,
  `.tools { min-width: 0 }`, `.lang-select { min-width: 118px; max-width: 11rem }`), `:269-292` (`.status` with
  `min-width: 0`, `.status__text` with ellipsis). Screenshots `tablet/player-700x900-light-{pt,ru,uk}.png` and
  `tablet/receivers-700x900-light-ru.png`; the same pages at `tablet/*-768x1024-*` and `tablet/*-1024x768-*` fit.
- **Spec:** 5.5 (tablet shell 700–1023px, tabs in the flow, the language select with the native name), 5.2 ("nothing
  cut off"), 7 (the status pill is the live connection state; it is the only place the shell shows it).
- **What is wrong:** with the tabs in the flow (`flex: none`), the remaining width at 700px is shared by three
  shrinkable boxes: the brand name, the language select and the status pill. In pt the bar reads "AirPlay B…",
  "Português (…" and "ao…"; in ru "AirPlay B…" and "о…" ("онлайн"); in uk the pill reads "в е…" ("в ефірі"). The status
  pill losing its word hides the connection state. The pill is the only place that shows it on every page (the Player's
  Status card and Diagnostics show it on those pages only), and the dot colour alone does not carry it (WCAG 1.4.1). The
  band 700 to about 760px includes 720px-wide Android phones in landscape. This was missed because the i18n screenshots
  were taken at 390 and 1280 only and the 768 audit ran only in en and de (see "What was run"); `audit()` reports
  horizontal overflow, which is 0 here, not ellipsis.
- **Expected fix:** let the brand name and the top-bar language select give way first, and keep the status word
  intact. The phone shell already hides both, and Settings has the 44px language field (4.6), so neither is the only
  path to anything. UI-SPEC 5.1 closes the media-query list, so prefer flex rules over a new width query:
  `.status { flex-shrink: 0 }` (or `.status__text { min-width: max-content }` at ≥ 700px), `.lang-select { min-width: 0;
  flex-shrink: 3 }` (a larger shrink factor than the brand name), and `.brand__name` keeps `min-width: 0` with
  ellipsis. Check that the select still shows at least the start of the native name; if it becomes unreadably narrow,
  hiding it is acceptable, but then add the query (for example `(max-width: 759px)` with `.lang-select { display:
  none }`) to the 5.1 table and to `DesignTokenTests` in the same change. Document the rules in 5.5, and change the
  `.lang-select` row of the 1.5 table (it specifies `min-width: 118px`). At 360–699px the phone shell already keeps the
  pill readable (5.2), and nothing changes there.
- **Verification:** extend `tools/web-audit.ps1` with a truncation check for `.status__text` (`scrollWidth >
  clientWidth` is a failure) and run it, plus the final screenshot set, at 700×900 and 768×1024 in all seven
  languages (light), in addition to the existing sizes. Re-check `tablet/` at 700×900 in pt, ru and uk: the pill shows
  "ao vivo" / "онлайн" / "в ефірі" in full. Pair this with finding 5's open-pill probe.

## REVIEW-1 re-check (every item marked fixed)

| # | fix-1 status | Re-check |
|---|---|---|
| 1 | fixed | Confirmed. `Settings.razor` passes `ParsingErrorMessage="settings.validation.latencyNumber"` and renders `L[message]`; `SettingsDiagnosticsTests.Clearing_the_latency_shows_the_catalog_text_not_the_property_name` is green. |
| 2 | fixed | Confirmed. The phone Output rows show radio, name, model and pill only (`phone/player-390x844-*`, `hero/hero-390x844-*`); Edit is `.only-wide`. |
| 3 | fixed (required part) | **Partly.** Blur after a pointer selection and after Escape works (focus goes to `<body>`), but on touch the pill stays unfolded through sticky `:hover`: see finding 1. |
| 4 | fixed | Confirmed. `div.row.row--choice > label.row__label`; the slider and buttons are siblings; the audit's `unnamed` is empty, and the radio labels measure 59–63px. |
| 5 | fixed | Confirmed for de (`states/banner-player-390x844-dark-de.png`). In en, the runtime text it lets through is a resource key: finding 2. |
| 6 | fixed | Confirmed. The members chip is a centred pill in every phone shot. |
| 7 | fixed | Confirmed. The "–" is muted with no output (`nooutput/player-390x844-light-en.png`, `-844x390-dark-en.png`). |
| 8 | fixed | Confirmed. The ← of the back links lines up with the `<h1>` on Groups, the editor and Diagnostics, in all languages. |
| 9–17, 19, 23 | fixed (spec) | Confirmed in UI-SPEC: 5.1 landscape tablet shell and `(max-height: 500px)` list, 5.4 `.btn--sm` box, 5.3 master `.step` 44px, 4.3 Status columns and Output fact states, 5.3 phone overrides, 7 layered pulse, 4.4 HomeKit title, 4.1/9 `NotFound.razor`, 4.3 busy row, 3.1 `SetLanguage`. The Status card shows 2 × 2 facts at 1280 (`i18n/player-1280x900-*`). |
| 18 | fixed | Confirmed. `.input--sm` on desktop. On touch, 44px and 16px win (the touch block comes later in the file); the Settings probe measured 16px on every input and select. |
| 20 | fixed | Confirmed. `BridgeCommands.IsFailure` is the catch filter in `Settings.razor:279` and `Diagnostics.razor:346`; the three timeout tests are green. |
| 21 | fixed | Confirmed. `NotFound.razor:11` is `a.btn.btn--ghost.btn--back`. |
| 22 | fixed | Confirmed. No `style=` attribute in a `.razor` file except the `--fill`/fill-width custom properties and the styleguide. |
| 24 | fixed | Confirmed. `StatusCard.razor:27` and `Diagnostics.razor:265` switch on `StatusPill.KindOf(State)`. |
| 25 | fixed | Confirmed. The phone Output head fits one line in de, fr, ru, uk, pt and es (`montage/i18n-390-player.png`). |
| 26 | fixed | Confirmed. `TabLink` has `_disposed` and a logged continuation. |

## Accessibility (UI-SPEC 6), traced

- **Names and roles:** `unnamed` is empty in all 133 audit runs (7 routes, 3 sizes, 2 themes, 7 languages). This
  includes the real pairing dialog (`states/` audit), the toast state and the banner state. Icon-only buttons
  (transport, steppers, theme options, toast close, member remove) carry `aria-label`. The progress bar has
  `role="progressbar"`, `aria-labelledby="hero-title"` and `aria-valuetext`. The status pill and banner slot are
  `role="status"`, and error toasts are `role="alert"`.
- **Fieldsets:** two in the app, both named only by their legend: `OutputList.razor:57` (a visually hidden legend,
  player.outputChoice) and `Settings.razor:101` (a visible legend, settings.theme). Neither has a role or `aria-label`.
  The styleguide copies them.
- **Keyboard paths in bUnit:** theme picker arrows, Home/End, Escape and blur (`ShellTests`); dialog focus on open
  (`Pairing_dialog_remembers_the_focus_and_focuses_the_pin_field_when_it_opens`), the Tab trap in both directions
  including the inner steps (`Pairing_dialog_traps_tab_between_the_pin_field_and_the_cancel_button`), and Escape with
  `restore` (`Pairing_dialog_escape_cancels_and_returns_the_focus`); the group rename's Escape focus return.
  `FocusOnNavigate` targets the `h1`; the Player's hidden `h1` has `tabindex="-1"`.
- **DesignTokenTests:** present and green. They cover identical dark blocks, the token set, the media-query list, the
  hover guard (with the `:not(:hover)` exemption of finding 1), the raw-colour grep
  (`Raw_status_colours_are_never_used_as_text_colour`), no colour literal outside the token blocks, and the contrast
  minimums in both themes.
- **Focus ring:** `:focus-visible, .is-focus-sample { outline: 2px solid var(--accent); outline-offset: 2px }`. The
  `--text` ring applies on primary, play and the active tab. The only `outline: none` rules are `h1:focus` (not a
  control) and the range input, whose thumb carries the ring. The styleguide sample renders the ring in both themes
  (`states/focus-sample-390-{light,dark}.png`; computed `rgb(47,109,246)` / `rgb(76,141,255)` on the plain button and
  `--text` on the primary). The output radios show the ring on the `.radio` glyph (`.row input:focus-visible + .radio`).

## i18n

- **Overflow:** no horizontal overflow anywhere. The screenshot script reported nothing, and the 49 language audits all
  have overflow 0. In fr, ru, uk, pt and es, nothing is cut off at 390 or 1280. Overflow 0 does not rule out
  truncation, though: the i18n screenshots were taken only at 390 and 1280, and the 768 audit ran only in en and de.
  At 700px (the narrowest tablet shell, which includes 720px-wide Android phones in landscape), pt, ru and uk truncate
  the brand name, the top-bar language select and the live-status pill (finding 12); at 768 and 1024 everything fits
  (`tablet/`). The other layout weakness is the Diagnostics address column (finding 4).
- **Untranslated keys:** no key path appears on any screen. Catalog values equal to English are only proper names,
  placeholders-only strings and real loanwords ("Bridge", "Spotify", "PIN", fr "Volume"/"Pause"/"Sections", es "error",
  pt "Status"/"Volume"/"online"). The service's pending-probe names stay English by design (3.5). The one exception
  on screen is the runtime key of finding 2.
- **Plurals:** "2 из 2 в сети", "1 подключение", "1 підключення" and fr "1 connexion" are correct. The only defect is
  the events count (finding 9).
- **Number formats** in the browser (WASM ICU): the latency hint reads "1.750 ms bei 44,1 kHz (22.050–176.400)" in de,
  "1,750 ms at 44.1 kHz (22,050–176,400)" in en, and "1 750 мс … (22 050–176 400)" in ru/uk/fr. Dates read
  "10.10.2026 08:01:19" (de), "10/10/2026 8:01:19 AM" (en) and "10/10/2026 8:01:19" (es). The retransmit ratio shows
  "–" here because nothing is streamed with Spotify off. `FormattingTests` cover "0,4 %" (de, fr, es…) and "0.4%" (en)
  on the desktop runtime with the same ICU data.
- **`<html lang>`:** it is set at start-up from `?lang=`, the stored choice or the browser, it follows a runtime
  switch, and it survives a reload (the lang probe above).
- **The Settings language field** is a `<select id="settings-language" class="input">` under
  `<label for="settings-language">` "Language" / "Sprache" / …, with no `aria-label`. It is 44px tall with 16px text on
  phones.

## Mobile UX (390×844 and 844×390)

- **Thumb reach:** on the phone Player, the master steppers and slider sit at about 45–55% of the height. The Output
  rows follow, and the bottom tab bar is at the very bottom. With a track playing, the transport is at about 45% of the
  height (`hero/hero-390x844-*`). The only controls in the hard-to-reach top band are the theme picker and the brand link,
  and the Settings Appearance card is the thumb-friendly alternative for the theme (2.4).
- **Targets:** every interactive element is ≥ 44 × 44 except the allow-listed theme button (34px on phones; 26px in
  landscape, see finding 5) and the top-bar language select (landscape/tablet only).
- **Nothing cut off, no horizontal scroll:** this holds in every phone shot and in the 844×390 landscape shots, in all
  languages. A landscape phone only 700–760px wide gets the narrow tablet shell, where pt, ru and uk truncate the top
  bar (finding 12). The last
  card ends above the tab bar (`states/safearea-player-390x844-light-en-end.png`).
- **Inputs:** 16px in every `input`/`select` on touch (9 of 9 on Settings). The PIN field is 25.6px.
- **Safe areas:** with `Emulation.setSafeAreaInsetsOverride` (top 47, bottom 34): top-bar padding-top 57px, tab-bar
  padding-bottom 40px, `--tabbar-h` 91px, and `main` padding-bottom 107px, so the content clears the home indicator. In
  landscape (left/right 47, bottom 21), the bar and `main` pad by 47px on both sides (`states/safearea-*`).
- **Toasts:** on phones the error toast sits 12px above the tab bar (bottom 775 vs. tab bar top 787) with a 44px close
  button. In landscape it sits under the sticky bar at the right.
- **Banner:** a static strip under the bar on phones, sticky in landscape. See finding 2 for its English text.
- **Pairing sheet:** the real dialog is top-anchored on phones (12–278px), the PIN field has focus, and the dialog fits
  the 508px keyboard viewport. For landscape, see finding 3.
- **Hero:** with and without a track, it matches the phone mockup (minus the knob, 8.1). In landscape the master row is
  on the first screen.

## Manual checks that remain (cannot be automated here)

1. On-screen keyboard over the pairing sheet on **iOS Safari** and **Android Chrome** (portrait and landscape):
   the PIN field and the Pair button stay visible (`interactive-widget=resizes-content` is Chrome-only; iOS relies on the
   top anchoring). Landscape is the risky case (finding 3).
2. `env(safe-area-inset-*)` on a notched iPhone in Safari and as a home-screen web app. The CDP override confirms the
   CSS arithmetic, not the device.
3. Drag feel of the 44px slider band: the hero slider, the Receivers slider and the short group-editor member sliders
   (finding 6). Also check that a vertical page scroll that starts on a slider still scrolls the page.
4. `<select>` picker sheets on iOS (language, stream type, log level, group member choice): the wheel or sheet opens,
   and the change event fires once.
5. The theme picker's sticky-hover collapse on a real Android phone and a real iPhone after finding 1 is fixed
   (the emulation reproduces it, but the real gesture handling should be confirmed once).
6. A coarse-pointer tablet at ≥ 1024px (for example an iPad in landscape): the touch block applies, and the absolutely
   centred tabs do not collide with the tools cluster in the longest language (pt "Português (Brasil)" in the select).

## Looks right (do not undo)

- fix-1's blur after a pointer selection and Escape, the first-tap-opens branch on touch, and the outside-click collapse:
  all were observed working in the emulated run. Only the CSS `:hover` dependency (finding 1) is left.
- The `(hover: hover)` guard on every colour rule: no hover colour sticks after a tap.
- The touch block at both halves of its query: at 844×390 with only `pointer: coarse` matching, every rule of 5.4
  applies (44px ranges, 24px thumbs, 44px buttons and tabs, 16px inputs).
- The top-anchored phone dialog with the PIN field in the upper third, and focus moved to the field on open.
- The Settings Appearance rows as whole-row labels (44px), synchronised with the top-bar picker in both directions.
- Language switching without a reload, `<html lang>`, stored choice and one-shot query, and culture-correct numbers
  and dates in all seven languages.
- The phone Output card head on one line in every language, and the wrapping members chip with real names.

## Not findings

- `?theme=` and `?lang=` overrides showing "System" in the picker: this is by design (2.2, REVIEW-1 "Not findings").
- English pending-probe names on Diagnostics: service text (3.5).
- The `/_styleguide` page's English sample texts: it is a developer page (WORK-PLAN 1).
- A third review is not needed. The findings are contained (CSS, two small C# changes, catalog text, spec text, a tooling check), and
  fix-2 can verify them with the probes in `artifacts/screenshots/review-2/probes/`.
