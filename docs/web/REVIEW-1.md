# Review 1: web app redesign (variant C), waves 0–3

Package `review-1` (wave 4, WORK-PLAN 4). Report only: no code was changed. The `fix-1` package works through the
findings below and marks each one **fixed** or **deferred (reason)** in place.

## What was run

| Check | Result |
|---|---|
| `dotnet build AirplayBridge.sln -warnaserror` | green: 0 warnings, 0 errors |
| `dotnet test AirplayBridge.sln --filter "Category!=SpeakerALive&Category!=ShairportLive"` | `Bridge.Tests` 498/498 passed. `Airplay2.Sender.Tests` 565/566: `FakeReceiverAudioTests.Dropped_packets_are_requested_and_answered(rawReplies: False, secondSocket: False, extended: True)` failed once under the full parallel run (`Expected [.., 524, 529]`, `Actual [.., 524]`) and passed on its own (4/4). This is a timing flake in the sender tests. The web work does not touch that code, and this review does not count it as a finding (see the end of the file). |
| `tools/web-screenshots.ps1 -Package review-1` | 96 PNGs: routes `/`, `/receivers`, `/groups`, `/groups/new`, `/groups/{id}`, `/settings`, `/settings/diagnostics`, `/_styleguide` × 390×844, 768×1024, 1280×900 × light, dark × en, de, with the `mobile-polish` setup (group "Downstairs" active, both fakes connected). The script reported no horizontal overflow. |
| Extra screenshots | `nooutput/` (no output selected; not-found route), `extra/` (844×390 landscape; 390×2200 page ends in de; 320×640 dark de; `/_styleguide?dialog=1` phone sheet; `hero/` = the Player with a demo track through `mobile-polish.ps1 -HeroShots`), `mockups/` (the three mockups rendered for comparison) |
| `tools/web-audit.ps1` (routable pages only; the not-found content cannot be audited, see finding 21) | **84/84 passed**: 7 routes (`/`, `/receivers`, `/settings`, `/settings/diagnostics`, `/groups`, `/groups/new`, `/groups/{id}`) × 390×844, 768×1024, 844×390 (touch emulated) × light, dark × en, de. In every run overflow is 0 and `unnamed` is empty. `small` lists only the allow-listed `.theme-btn.is-active` and `select#lang-select`. |

All PNGs and logs are under `artifacts/screenshots/review-1/` (git-ignored): `build.log`, `test.log`, `shots.log`,
`audit.log`, `extra-*.log`.

## Findings

Severity: **blocker** (broken or unusable), **major** (breaks a spec rule a user notices, or a real defect),
**minor** (deviation or UX weakness worth fixing in fix-1), **nit** (cosmetic or a spec text correction).
"Spec fix" means the code is reasonable and UI-SPEC should be changed to match it (fix-1 owns `UI-SPEC.md`).

There are no blockers.

### 1. Clearing the latency field shows an English message with a C# property name (major)

- **Where:** `src/Bridge.Web/Pages/Settings.razor:52` (`<InputNumber id="latency" …>`)
- **Spec:** WORK-PLAN 1 ("every string a user sees comes from the catalog"), UI-SPEC 4.6 (validation messages come from the catalog), 3.5.
- **What is wrong:** `InputNumber<int>` has a parsing check of its own. When the field is empty or holds text that is not
  an integer, `BindConverter.TryConvertTo<int>` fails and `InputNumber` writes its default `ParsingErrorMessage`,
  "The {0} field must be a number." Because the input has no `DisplayName`, `{0}` becomes `LatencyFrames`. The user
  therefore sees "The LatencyFrames field must be a number." in every language, a C# identifier included. A user gets
  there by selecting the field and deleting its value, or by typing "1e" in Chrome. The `Catalog*` attributes of
  `SettingsModel` never run for this case: the parse fails first.
- **Expected fix:** pass `ParsingErrorMessage="@L["settings.validation.latencyRange"]"`, or add a new key such as
  `settings.validation.latencyNumber` to all seven catalogs (it needs a consumer for `CatalogUsageTests`). Add a bUnit test
  in `SettingsDiagnosticsTests`: `Change("")` on `#latency` under `de` shows the catalog text, and neither
  "LatencyFrames" nor "must be a number" appears.

### 2. Phone Output rows show the group's Edit button, which UI-SPEC 5.3 and the phone mockup leave out (minor, mobile UX)

- **Where:** `src/Bridge.Web/Components/OutputList.razor:89` (group row `a.btn.btn--sm` player.edit). Screenshots
  `player-390x844-*-*.png`, `extra/hero/hero-390x844-light-en.png`.
- **Spec:** 5.3, Output card row: "rows: radio, name + `.meta.only-phone`, pill (compact slider hidden); head
  actions per 4.3 (player.editGroups link …)". `variant-c-phone-light-en.html` has no Edit button in the rows.
- **What is wrong:** At 390px the group row also carries the 44×44 "Edit"/"Bearbeiten" box. That squeezes the members
  line to two lines ("Fake Receiver 1, Fake / Receiver 2"), and the row is about 20px taller than the receiver rows.
  The head's "Edit groups" link already leads to the editor.
- **Expected fix:** give the group Edit link `only-wide`, so the phone row shows only radio, name, meta and pill.
  The receiver row's player.pair button (a PIN receiver) can stay on phones: it is the only one-tap way to pair from
  the Player. Add it to the 5.3 row list in the spec. Update `PlayerTests` if a test counts Edit links.

### 3. The theme picker stays unfolded after a pointer selection, and Escape has no visible effect while focus is inside (minor, mobile UX)

- **Where:** `src/Bridge.Web/wwwroot/css/app.css:260-265` (`.theme-picker:not(:hover):not(:focus-within):not(.theme-picker--open) …`),
  `src/Bridge.Web/Components/ThemePicker.razor:203-209` (Escape only clears `_open`).
- **Spec:** 2.4 ("selecting collapses it … Escape collapses it"), 6.2.
- **What is wrong:** Chromium (Android Chrome, desktop Chrome and Edge) focuses a `<button>` when it is clicked or
  tapped. After the second tap selects a theme, `_open` is false, but the tapped button still has focus, so
  `:focus-within` keeps all three options unfolded until the user taps somewhere else. Escape has the same problem:
  focus stays in the group, so the pill does not collapse. The bUnit tests check only the `theme-picker--open` class,
  which is why they pass. The reference apps share the CSS, but they are server-rendered pages where a choice usually
  reloads.
- **Expected fix (required):** after a pointer selection (`e.Detail != 0`) and after Escape, blur the focused button
  through a small `app.js` export (for example `blur(element)`, added to the 2.3 export list), so focus leaves the
  group and `:focus-within` no longer holds it open. A CSS change alone is not enough: replacing `:focus-within` with
  `:has(:focus-visible)` fixes only the tap case, because the keyboard focus that sits in the group when Escape is
  pressed is itself `:focus-visible` (Chromium also makes a pointer-focused element match `:focus-visible` once a key is
  pressed), so the pill would still stay unfolded after Escape.
- **Optional refinement:** additionally unfold on keyboard focus only,
  `.theme-picker:not(:hover):not(:has(:focus-visible)):not(.theme-picker--open) …` (the same in the phone block at
  `app.css:888-892`), which keeps a stray pointer focus from unfolding the pill. Update the 2.4 rule text if this is done.
- **Tests:** extend `ShellTests.Theme_picker_escape_collapses_the_open_pill` (`ShellTests.cs:165`) and the pointer
  selection test to assert the mocked `app.js` `blur` invocation (the module mock records it like `setTheme`), so the
  Escape path is covered and not only the `theme-picker--open` class. A keyboard selection (`e.Detail == 0`) must not
  blur. review-2's emulated-touch pass should confirm the fix.

### 4. Output rows put a button, a range input and an `<output>` inside a `<label>` (minor, a11y and HTML validity)

- **Where:** `src/Bridge.Web/Components/OutputList.razor:102-139` (receiver row `label.row` containing `.row__level` with
  `LevelSlider` or the Pair `<button>`); group rows (`:68-90`) contain only an `<a>`, which is allowed.
- **Spec:** 4.3 (rows are labels wrapping the radio), 6.1 (real semantic controls).
- **What is wrong:** HTML allows a `<label>` no labelable descendants other than its labelled control. Labelable
  elements include `button`, `input` and `output`. The code works around the browser's label activation with
  `@onclick:preventDefault` on a `display: contents` span. Clicks behave, but the markup is invalid, and some screen
  readers (VoiceOver in particular) announce nested controls inside a label oddly.
- **Expected fix:** make the row a `div.row` that holds the `<label>` (radio glyph, name, meta, pill; `flex: 1`;
  `min-height: 44px`) and the level block or Pair button as a sibling. Then the preventDefault workaround can go.
  `.row.is-active` and `.is-quiet` stay on the `div`. If fix-1 defers this, write the exception into UI-SPEC 4.3.

### 5. Client-side English error text appears in the banner and the status title (minor, i18n)

- **Where:** `src/Bridge.Web/BridgeEventClient.cs:194` (`error = "The bridge closed the connection."`) and `:202/:208`
  (`ex.Message` of `WebSocketException`). The text reaches `ConnectionBanner` as banner.pausedError and
  `StatusPill` as status.title.paused/offline.
- **Spec:** 3.5 (only text that *originates in the service* stays English), 8 (`BridgeEventClient.cs` stays unchanged).
- **What is wrong:** In German the banner reads "Live-Updates pausiert: The bridge closed the connection. Es wird gleich
  erneut versucht." This sentence comes from the client, not the service. The runtime's WebSocket messages are English
  as well.
- **Expected fix:** this needs a spec decision, because 8 freezes `BridgeEventClient`. Option a): keep the client
  unchanged, and have `ConnectionBanner`/`StatusPill` use the error-free sentences (banner.paused, status.title.paused
  without `{{error}}`) unless the language is English. Option b): add a key such as `banner.closedByBridge` and map that one
  known sentence in the two components. Either way, write the rule into 3.5.

### 6. The phone members chip stretches across the whole card (nit, mockup fidelity)

- **Where:** `src/Bridge.Web/wwwroot/css/app.css:408-415` (`.chip--members`), rendered at `MasterVolume.razor:51`.
  Screenshots `player-390x844-*`, `extra/hero/hero-390x844-light-en.png` against `mockups/variant-c-phone-light-en.png`.
- **Spec:** 4.3 / 1.5 (`.chip--members`), phone mockup (the chip is a centred pill as wide as its text).
- **What is wrong:** `.master` is a flex column whose children stretch, so the chip becomes a full-width bar.
- **Expected fix:** add `align-self: center` to `.chip--members`. It keeps `max-width: 100%`, so long names still wrap.

### 7. With no output, the master number shows "–" in large accent ink, and it looks like a third stepper (nit)

- **Where:** `src/Bridge.Web/Components/MasterVolume.razor:26,56` and `app.css:681`. Screenshot
  `nooutput/player-390x844-light-en.png`, right of the "+" button.
- **Spec:** 4.3 ("none → disabled, number shows `–`").
- **What is wrong:** The glyph is set in `.big` (1.3–1.7rem, `--accent-ink`, bold), so a blue bar sits next to the "+"
  stepper and reads as a control.
- **Expected fix:** set the `.big` output to `color: var(--muted)` while the slider is disabled (for example `.level.is-disabled
  output.big { color: var(--muted) }`; `LevelSlider` already adds `is-disabled`).

### 8. Back links (`.btn.btn--ghost` with ←) are indented against the `<h1>` (nit)

- **Where:** `Pages/Groups.razor:12`, `Pages/GroupEditor.razor:17`, `Pages/Diagnostics.razor:12`. Screenshots
  `groups-390x844-*`, `settings-diagnostics-*`.
- **Spec:** 4.5 / 4.7 (a back link at the top). The mockups do not draw it.
- **What is wrong:** The ghost button's padding (.9rem) and its 44px touch box move the arrow about 14px to the right of the
  heading's left edge. Each back-link page shows the misalignment.
- **Expected fix:** add a `.btn--back` modifier (`margin-inline-start: -.9rem`; on touch, also `margin-block: -.4rem` so the
  link does not add height above the heading), used by the three pages.

### 9. Spec fix: landscape phones get the tablet shell, not the phone shell (minor, spec)

- **Where:** `UI-SPEC.md` 5.1 (the last paragraph: "Landscape phones (844×390) get the phone shell (width rules) with the tab
  bar") against `app.css:990-1006` and `extra/player-844x390-light-en.png`.
- **What is wrong:** 844px is wider than 699px, so width queries cannot give it the phone shell. The implementation
  documents this correctly: it gives the tablet shell with sticky top-bar tabs (44px on touch) and no tab bar. The result
  works: the audit passes at 844×390 with touch emulated, and the hero fits the first screen.
- **Expected fix:** rewrite the 5.1 sentence to describe what is built. Also list in 5.1 the extra `(max-height: 500px)`
  rules (`.hero__title` 1.3rem, `.play` 48px, top-bar padding 5px, brand tile 28px, tab padding). They go beyond the
  spec's "artwork 96px, padding .8rem". Make sure the 48px play button stays ≥ 44px.

### 10. Spec fix: `.btn--sm` gets a 44px box on touch instead of the `::before` hit area (nit, spec)

- **Where:** `app.css:1017-1021` against UI-SPEC 5.4 ("`.btn--sm` keeps its 30px box and grows a centred `::before` hit area").
- **What is wrong:** The spec contradicts itself. 5.6's audit measures `getBoundingClientRect()` of the element, which never
  counts a pseudo-element, so the `::before` approach would fail the audit. The implementation grows the box to
  44×44 and uses `margin-block: -.4rem` inside rows and the banner. That is the right choice.
- **Expected fix:** change 5.4 to describe the 44px box and the negative block margin.

### 11. Spec fix: the master `.step` is 40px in 5.3 but 44px in 5.4 (nit, spec)

- **Where:** UI-SPEC 5.3 table ("master row `.step` 40px") against 5.4 (`.step` min 44px) and `app.css:1012-1014`.
- **What is wrong:** The 44px rule wins, which is correct for touch. Only the 5.3 table is wrong.
- **Expected fix:** change the 5.3 table to 44px.

### 12. Spec fix: Status card columns between 700 and 1023px (nit, spec)

- **Where:** UI-SPEC 4.3 ("three (≥ 1024px) / two (700–1023) / one column") and 5.1 ("700–1023px gets … two columns
  through plain `repeat(auto-fit, minmax(10rem, 1fr))`"), against `app.css:742` and `player-768x1024-*.png`.
- **What is wrong:** At 768px, `auto-fit` with a 10rem minimum fits **four** columns into the card. The result looks
  good: all four facts sit in one row. At ≥ 1024 the fixed three columns leave "Live updates" alone on a second
  row (`player-1280x900-*`).
- **Expected fix:** change the spec to "auto-fit (four at tablet width)". Consider `repeat(2, …)` at ≥ 1024 (2 × 2) or
  four columns, so no fact sits alone. Decide which in fix-1.

### 13. Phone overrides that are not in the "complete list" of UI-SPEC 5.3 (nit, spec)

- **Where:** `app.css:917` (`.hero__artist` muted .78rem), `:920` (`.master__head` centred), `:932` (`.row .level--compact`
  120px for the group editor members), `:937-940` (`.facts` stacked label-over-value on phones).
- **Spec:** 5.3 calls its table "the complete list".
- **What is wrong:** Nothing visible: all four improve the phone layout (Diagnostics and Receivers details read much
  better stacked). They are simply undocumented.
- **Expected fix:** add the four rows to the 5.3 table.

### 14. The live pulse keeps the static halo under the animated ring (nit, spec)

- **Where:** `app.css:301-305` against UI-SPEC 7 ("while it runs the animated `box-shadow` replaces the base rule's static
  halo").
- **What is wrong:** The keyframes layer the 3px static halo under the growing ring, so the first frame of a screenshot
  shows the halo too. This deviates from the spec, and it looks better.
- **Expected fix:** change the spec text in 7 to describe the layered keyframes.

### 15. The Status card's Output fact can also say "connecting…" or "error" (nit, spec)

- **Where:** `Components/StatusCard.razor:14-20` against UI-SPEC 4.3 (player.outputConnected / player.outputNone only).
- **What is wrong:** When an output is selected but not connected, the fact shows pills.connecting (accent) or
  pills.error (err) instead of "none". This avoids "none" under an active Output row, a sensible deviation, and
  `Status_card_output_fact_follows_a_selected_output_that_is_not_connected_yet` covers it.
- **Expected fix:** add the two states to 4.3.

### 16. The Connect button's title for a HomeKit-only receiver uses pills.title.homekitOnly (nit, spec)

- **Where:** `Pages/Receivers.razor:213-221` against UI-SPEC 4.4 (only receivers.connectTitle / receivers.offlineTitle).
- **What is wrong:** A sensible addition that the spec does not list.
- **Expected fix:** add it to 4.4.

### 17. `Pages/NotFound.razor` is missing from the file map (nit, spec)

- **Where:** UI-SPEC 4.1 ("`App.razor` NotFound: `<h1>` … inside MainLayout") and 9.
- **What is wrong:** The not-found content lives in its own route-less component. It focuses its own `<h1>`, writes its own title
  and follows the language, all tested in `ShellTests`. The file map in 9 does not list it.
- **Expected fix:** add `Pages/NotFound.razor` to 9.

### 18. The log level select on Diagnostics is larger than the buttons beside it (nit)

- **Where:** `Pages/Diagnostics.razor:119-131`. Screenshot `settings-diagnostics-1280x900-*`.
- **What is wrong:** The `select.input` (16px text, 40px tall, 44px on touch) sits next to two `.btn--sm` ghost buttons
  (.78rem). The head row looks uneven on desktop.
- **Expected fix:** a `.input--sm` variant for desktop only (`font-size: .85rem; min-height: 32px`). The touch block keeps
  44px and 16px.

### 19. Spec fix: a busy Output row keeps its radio enabled (minor, spec)

- **Where:** `src/Bridge.Web/Components/OutputList.razor:69` (the group row radio has no busy condition in `disabled`;
  the row carries `aria-busy` and `ActivateAsync` ignores a repeat), the receiver rows likewise, against UI-SPEC 4.3
  ("while a request runs the row's radio is disabled and the pill shows pills.connecting").
- **What is wrong:** The code deviates from the spec for a good reason: disabling the radio that has keyboard focus
  drops focus to `<body>`, so a keyboard user who checks a row loses their place.
  `PlayerTests.A_busy_row_says_connecting_and_keeps_its_radio_enabled` (`PlayerTests.cs:561`) pins this behaviour.
  Recorded here so fix-1 does not "correct" the code back to the spec.
- **Expected fix:** do not change the code. Rewrite the 4.3 sentence to: "while a request runs the row carries
  `aria-busy`, the pill shows pills.connecting and a repeated check is ignored; the radio stays enabled so keyboard
  focus is kept."

### 20. A request timeout on Settings and Diagnostics shows the unhandled-error strip instead of the retry state (minor)

- **Where:** `src/Bridge.Web/Pages/Settings.razor:255` (`LoadAsync`) and `src/Bridge.Web/Pages/Diagnostics.razor:342`
  (`RefreshAsync`).
- **Spec:** 4.6 (settings.loadFailed + Retry), 4.7 (diagnostics.unavailable + Retry), 4.9 (failures become catalog text).
- **What is wrong:** Both catch lists name only `BridgeApiException`, `HttpRequestException` and `JsonException`, plus
  `OperationCanceledException` when the page's own token is cancelled. An `HttpClient` timeout arrives as
  `TaskCanceledException` with an inner `TimeoutException` while the token is still live, which is exactly the case
  `BridgeCommands.IsFailure` (`BridgeCommands.cs:264`) treats as a failure. On Settings it escapes `OnInitializedAsync`
  and Blazor shows the static "An unhandled error has occurred" strip instead of settings.loadFailed + Retry. On
  Diagnostics the first refresh crashes the page the same way, and a timeout in a later tick ends the `Every()` loop
  silently (`Observe` logs it, but the page stops refreshing for good).
- **Expected fix:** catch what `BridgeCommands.IsFailure` treats as a failure (`TimeoutException`, and
  `TaskCanceledException` with a `TimeoutException` inner while the token is not cancelled), for example by making
  `IsFailure` `internal static` and using it as the filter in both pages, and render the existing error state with
  `BridgeCommands.Describe` (it already maps both to notices.timeout). Add a bUnit test per page whose API stub throws
  `new TaskCanceledException(null, new TimeoutException())`: the page shows the catalog text (settings.loadFailed /
  diagnostics.unavailable with notices.timeout), and Retry with a working stub loads the page. For Diagnostics also
  assert that a timeout in a later tick leaves the refresh loop running.

### 21. The not-found page's only action is a 20px inline link (minor, mobile UX)

- **Where:** `src/Bridge.Web/Pages/NotFound.razor:11` (`<p><a href="./">nav.player</a></p>`). Screenshot
  `nooutput/no-such-page-390x844-light-en.png`.
- **Spec:** 5.4 / 5.6 (interactive elements, `a[href]` included, are ≥ 44 × 44 on phones).
- **What is wrong:** The link is a plain inline `<a>` about 20px tall at 390px. It is not a link inside running text but
  the page's only action, so the 44px floor applies. Nothing catches it: `tools/web-audit.ps1` only audits routable
  pages (`Audit.razor` resolves the component from the route table, so an unknown address can never be audited), and
  `ShellTests.Unknown_address_shows_the_not_found_text_in_the_shell` checks the link's text, not its class. The audit
  results above therefore say nothing about the not-found route.
- **Expected fix:** render the link as a button-styled link, `class="btn btn--ghost btn--back"` (the `.btn--back`
  modifier of finding 8), and assert the class in `ShellTests.Unknown_address_shows_the_not_found_text_in_the_shell`.
  Mention in 5.6 that the not-found content is outside the audit's reach and covered by that test.

### 22. Inline style attributes instead of app.css classes (minor, CSS contract)

- **Where:** `src/Bridge.Web/Components/OutputList.razor:125` (`<span class="row__level" style="display: contents" …>`)
  and `src/Bridge.Web/Pages/Settings.razor:81` (`<p class="hint" id="poll-hint" style="padding-inline-start: calc(18px + .6rem)">`).
- **Spec:** 1.5 ("class names are the contract between CSS and components"); the only sanctioned inline style is
  `LevelSlider`'s `--fill` custom property (1.6).
- **What is wrong:** Layout lives in markup, where the theme and breakpoint rules of `app.css` cannot see or override it.
  This review's hard-coded-value check looked for colours only and missed both.
- **Expected fix:** move both into `app.css`: `.row__level { display: contents }`, and a rule for the hint under a
  check row keyed to `.field--check`'s 18px box + .6rem gap (for example `.field--check + .hint { padding-inline-start:
  calc(18px + .6rem) }`, or a `.hint--indent` modifier). Drop the `style` attributes. If finding 4 is done first, the
  `.row__level` span may disappear altogether.

### 23. Spec fix: `SetLanguage` sets only the default-thread cultures (minor, spec)

- **Where:** UI-SPEC 3.1 (the paragraph before the signature block: "`SetLanguage` sets
  `CultureInfo.CurrentCulture`/`CurrentUICulture` and the `DefaultThreadCurrent*`", and the signature comment of
  `SetLanguage`) against `Localization/I18n.cs:165-176`.
- **What is wrong:** The code deliberately sets only `CultureInfo.DefaultThreadCurrentCulture`/`DefaultThreadCurrentUICulture`,
  and its doc comment says why: `CurrentCulture` is an `AsyncLocal`, so setting it would be captured by every flow
  started afterwards (for example the `Every()` timer loops) and would pin the previous language in those flows after
  the next switch. The behaviour is right and `FormattingTests.A_flow_started_after_a_switch_follows_the_next_switch`
  covers it; only the spec text contradicts it.
- **Expected fix:** rewrite the 3.1 sentence and the signature comment to: "sets
  `DefaultThreadCurrentCulture`/`DefaultThreadCurrentUICulture`; never the `AsyncLocal` `CurrentCulture`, so every flow
  follows the latest switch", with the reason from the code comment. Check that 3.4 ("the `Format` helpers read
  `CurrentCulture`") still reads correctly afterwards (the app never sets `CurrentCulture`, so it resolves to the
  default-thread culture).

### 24. The section-7 connection mapping is written out three times (minor, maintainability)

- **Where:** `StatusPill.KindOf` (`Components/StatusPill.razor:18`, which `ConnectionBanner` already reuses),
  `Components/StatusCard.razor:27-33` (`liveClass`/`liveText`) and `Pages/Diagnostics.razor:265-271` (`ConnectionWord`).
- **Spec:** 7 (Connected → live, Connecting → connecting, Disconnected with `NextAttemptAt` → paused, else offline).
- **What is wrong:** Nothing visible today, but a change to the mapping has to be made in three places, and the review's
  "looks right" list only checked the pill.
- **Expected fix:** have `StatusCard` and `Diagnostics` switch on `StatusPill.KindOf(State)` like `ConnectionBanner`,
  keeping their own class and key choice. No behaviour change; `Status_card_shows_four_facts…` and
  `Live_updates_word_comes_from_the_status_keys` keep covering it.

### 25. In German the phone Output card head wraps into two rows (nit, mobile UX)

- **Where:** `src/Bridge.Web/Components/OutputList.razor:21` (`.card__head` with `.card__actions`). Screenshots
  `player-390x844-light-de.png`, `player-390x844-dark-de.png`.
- **Spec:** 5.3 (phone Output card), 1.5 (`.card__head` wraps by design).
- **What is wrong:** At 390px in German, "Ausgabe" stands alone on the first row and "Gruppen bearbeiten" + "Ausgabe
  stoppen" (a 44px box) take the second; in English all three fit on one row. It reads fine, but it is the one place
  where the phone layout differs by language, and the longer catalogs (fr, pt, ru, uk) will hit it too.
- **Expected fix:** reduce the stop button's horizontal padding on phones (for example
  `.output .card__actions .btn--sm { padding-inline: .5rem }` inside the phone query, keeping the 44px height) and check
  the de screenshots. If the head still wraps in de, accept the wrap and note it in 5.3. review-2's i18n pass verifies
  it against the ru/uk screenshots either way.

### 26. `TabLink` discards the re-render task on navigation (nit, Blazor)

- **Where:** `src/Bridge.Web/Components/TabLink.razor:53` (`OnLocationChanged … => _ = InvokeAsync(StateHasChanged)`).
- **What is wrong:** Every other component routes background work through `Observe` or a logged continuation. Here a
  dispatch that faults (a navigation racing the layout's teardown, which the Router does at every found/not-found
  switch) is dropped unobserved, and nothing stops the handler from running after `Dispose`.
- **Expected fix:** mirror `LanguageSelect.OnLanguageChanged`: a `_disposed` flag set in `Dispose` and checked first,
  and a continuation that logs a fault while ignoring `OperationCanceledException`/`ObjectDisposedException`.

## Looks right (do not undo)

Tokens and theme
- Every token of 1.1 has the exact light/dark values. The two dark blocks are byte-identical, the 1.2 structure is
  exact, and the extra tokens (`--on-accent`, `--brand-violet`, `--art-gradient`, `--scrim`, `--brand-shadow`, `--thumb-shadow`,
  `--danger-*`, `--chevron`) keep every colour literal inside the token blocks. `DesignTokenTests` enforces this, together with the
  raw-colour grep and the contrast minimums.
- The media-query set is exactly the nine of 5.1. Every `:hover` rule sits under `(hover: hover)`, and reduced motion comes last.
- Ink tokens are used for all text (`.text-*` utilities, pills, chips, `.big`, links). The only raw `color: var(--accent)` is
  the allow-listed `.theme-btn.is-active`.
- The `index.html` bootstrap matches 2.2 to the letter (one-shot `?theme=`, override meta inserted first, removed for system).
  `bridgeLang` matches `getLang`. The viewport meta has `viewport-fit=cover, interactive-widget=resizes-content`.
- `app.js` has every export of 2.3. `audit()` implements the 5.6 name rules, including label and visually-hidden handling.
- Theme picker: radiogroup with radio buttons, roving tabindex, arrows/Home/End with wrapping, `preventKeys` against scrolling,
  the first tap opens on touch (`e.Detail`), keyboard activation selects, document-click outside collapses, sync with the
  Settings Appearance radios through `BrowserBridge.ThemeChanged`. Apart from finding 3 this is all correct.

Language
- `I18n` follows 3.1 (fallback order language → de → key, `Plural` category → other → base, culture set on switch), with
  one deliberate deviation in how the culture is set: see finding 23.
  `PluralRules` passes the CLDR samples. The csproj has the ICU settings, and the de formatting is real (`1.234`, `0,4 %`,
  `10.10.2026 06:42:55`).
- All seven catalogs have key parity. `CatalogUsageTests` runs with an **empty allow-list** and passes. Keys are literal
  everywhere (switches, no interpolation), and there is no literal English in any `.razor` file outside `/_styleguide` and `/_audit`.
- The `LanguageSelect` Bar and Field variants, and `<html lang>` follow a switch. The whole app re-renders in place.

Routes, shell, pages
- The routes of 4.1 are in place: `/now-playing` and `/diagnostics` are gone, the Player tab owns `/groups*`, and page titles use
  app.pageTitle on every page. The deleted files (`LiveIndicator`, `StateBadge`, `NowPlaying`) are gone.
- The "what stays" files (`BridgeState`, `BridgeEventClient`, `BridgeApiClient`, `CoalescingSender`, `GroupVolumePreview`,
  `Bridge.Contracts`, `Bridge.Service`) have no diff since before wave 0.
- The status pill mapping of 7 (four classes, catalog text, titles without a dangling colon). The same mapping is written
  out twice more, see finding 24. The banner slot is always present,
  with `role="status"`. Toasts carry the kind class and role, and errors stay on screen.
- Player: the hero has no knob (8.1), the progress bar has `aria-valuetext`, and the remaining time uses U+2212. The transport
  is disabled without Spotify. The steppers use `StepAsync(±5)`. Member bars show on wide screens and the members chip on
  phones. The output fieldset is named by its legend only. Rows are `is-quiet`/`is-active`. The no-output line appears in the
  right state. The Status card has exactly four facts and no Stream or Retransmits facts (8.1).
- Receivers: cards follow 4.4 (pill, meta, hero slider, tags including the pairing chip, the error alert, actions with the busy
  label and titles, a collapsed `details` with a validated offset form and facts). The grid shows 1/2/2 cards at 390/768/1280.
- Groups list and editor follow 4.5. The inline rename and delete confirm return focus correctly, and the not-found state works.
- Settings: the order of 4.6, validation messages from the catalog that follow a language switch, the Appearance fieldset with
  a visible legend and 44px rows, the language `Field` variant under `<label for>`, and the forget confirm.
- Diagnostics: the four cards of 4.7, culture formatting, the retransmit ratio or `–`, the live-updates word from status.*,
  log pause, clear and filter, and the log box as a focusable `role="log"`.
- The pairing dialog is a top-anchored sheet on phones with the PIN field in the upper half (`extra/-styleguide-dialog-1-390x844-light-en.png`).
  It has `rememberActive`/`restore`, the Tab trap, Escape, and the catalog error texts.

Mobile
- No horizontal overflow at 320, 390, 768, 844×390 or 1280. The bottom tab bar never hides the last card
  (`extra/*-390x2200-*`). The top bar scrolls away on phones and stays sticky at ≥ 700. At 320px the brand name hides
  and the pill and picker fit.
- The touch block makes every target ≥ 44px except the allow-listed theme button and language select. Range inputs are 44px
  tall with 24px thumbs, inputs use 16px text, and gaps are 12px. The audit confirms this at 390, 768 and 844×390 with touch emulated.
- The phone hero matches the phone mockup closely, apart from findings 6 and 7 and the spec's own additions (the chip, the
  album line and the master label row are in 4.3).

## Not findings

- The sender-test flake noted above is outside the web packages. Run it again in fix-1's full test pass. If it fails again, report it
  to whoever owns `tests/Airplay2.Sender.Tests` (it is timing-sensitive under the parallel run).
- `?theme=dark` shows the page dark while the picker and the Appearance radios show "System". This is by design: the query is a
  one-shot override that is never stored (2.2), and `getTheme` reports the stored choice.
