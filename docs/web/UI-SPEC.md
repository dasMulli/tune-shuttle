# AirPlay Bridge web app: UI specification (variant C)

Status: architecture, 2026-10-10, revised after review. Implementers follow this document; where it is silent, the
mockups in `docs/web/mockups/` decide (`variant-c-desktop-light-en.html`, `variant-c-desktop-dark-de.html`,
`variant-c-phone-light-en.html`; ignore their `<x-dc>`/`support.js` wrapper). Where the mockups and this document
disagree, this document wins (it fixes the mockups' contrast problems and adds what the mockups do not show).
**Section 8.1 lists what the mockups draw and this app deliberately does not build**; those items are not "silence"
and the mockup rule does not apply to them.

The app is `src/Bridge.Web`, a Blazor WebAssembly SPA served by `src/Bridge.Service` at `http://<host>:8080`. The
house style is the one of `D:\daikin-ai-automation\src\DasMulli.DaikinOnecta.Web` and
`D:\smarthome-integrations\src\DasMulli.ButtonWeb` (server-rendered Razor Pages); this document ports their
mechanisms (tokens, theme picker, catalog) to the SPA. It does not copy their files.

Sections: 1 tokens and CSS, 2 theme, 3 language, 4 layout and pages, 5 mobile and breakpoints, 6 accessibility,
7 status pill, 8 what stays, 9 file map, 10 copy catalog (every string, de + en).

---

## 1. Design tokens and base CSS

### 1.1 Token set

One accent blue: **`#2f6df6`** (light) with its dark-mode counterpart **`#4c8dff`** (the Daikin pair; the old
`#0a6cff` of app.css goes away). Everything the mockups use takes the Daikin values below. The `*-ink` and `*-fill`
tokens are additions: the raw status colours are tuned as dots, borders and fills, and several of them fail 4.5:1 as
small text on the light surfaces (`--ok` 4.14, `--warn` 3.64, `--accent` 4.0 on `--bg`; see 6.3). **Text is always
set in an ink token; dots, borders, fills and large glyphs use the raw token.**

| Token | Light | Dark | Use |
|---|---|---|---|
| `--bg` | `#eef1f6` | `#0d1117` | page ground, `theme-color` |
| `--surface` | `#ffffff` | `#161c24` | cards, top bar, inputs, dialog |
| `--surface-2` | `#f1f4f9` | `#0f141b` | pill backgrounds, tab rail, slider track, log box |
| `--line` | `#e0e5ee` | `#283039` | card borders, row separators |
| `--line-strong` | `#d2d9e4` | `#323b46` | control borders (buttons, selects, pills, pickers) |
| `--text` | `#1b2330` | `#e6edf3` | body text |
| `--muted` | `#5f6b7a` | `#93a0ad` | secondary text, labels, inactive tabs |
| `--accent` | `#2f6df6` | `#4c8dff` | primary buttons, active tab fill, slider fill, focus ring, brand tile, radio dot |
| `--accent-ink` | `#1f5be0` | `#70a6ff` | accent-coloured TEXT and links, the big volume number, ghost buttons |
| `--accent-soft` | `rgba(47,109,246,.12)` | `rgba(76,141,255,.16)` | selected row, active theme button, accent chip background |
| `--accent-contrast` | `#ffffff` | `#0d1117` | text on `--accent` |
| `--ok` | `#1a8f4c` | `#3fb950` | live dot, ok borders |
| `--ok-ink` | `#167a41` | `#3fb950` | "live", "playing", "online", "connected" text |
| `--warn` | `#b7791f` | `#d8a23a` | warn borders, paused dot |
| `--warn-ink` | `#8a5a12` | `#d8a23a` | "PIN needed", "paused" text |
| `--err` | `#d92d20` | `#f85149` | error borders, danger button fill (light uses `--err-ink` as the fill, see 1.6) |
| `--err-ink` | `#b42318` | `#ff6b63` | error text |
| `--ok-fill` | `#e8f4ed` | `#1b2f29` | success toast background |
| `--warn-fill` | `#f8f2e9` | `#2d2c27` | paused banner background |
| `--err-fill` | `#fbeae9` | `#312228` | error toast background |
| `--info-fill` | `#eaf0fe` | `#1c2a3e` | info toast and connecting banner background |
| `--shadow` | `0 1px 2px rgba(16,24,40,.06), 0 6px 18px rgba(16,24,40,.08)` | `0 1px 2px rgba(0,0,0,.4), 0 8px 22px rgba(0,0,0,.45)` | cards, artwork |
| `--shadow-lift` | `0 2px 6px rgba(16,24,40,.10), 0 14px 32px rgba(16,24,40,.14)` | `0 2px 6px rgba(0,0,0,.5), 0 16px 36px rgba(0,0,0,.55)` | toasts, dialog, knob |
| `--radius` | `14px` | same | cards, dialog, artwork |
| `--radius-sm` | `9px` | same | buttons, inputs, selects |
| `--radius-pill` | `999px` | same | tabs, pills, chips, pickers, tracks |
| `--font` | `system-ui, -apple-system, "Segoe UI", Roboto, sans-serif` | same | everything |
| `--font-mono` | `ui-monospace, "Cascadia Mono", Consolas, monospace` | same | log, addresses, ids |
| `--topbar-h` | `56px` | same | fallback; the shell publishes the measured height (5.2) |
| `--tabbar-h` | `56px` | same | phone tab bar without the safe-area inset |

`--line` against `--surface` is 1.42:1 (light) / 1.51:1 (dark): separators are decoration, never the only cue.

### 1.2 The exact CSS pattern

`wwwroot/css/app.css` starts with exactly this structure (the Daikin pattern). **Rule: the two dark blocks are
identical, byte for byte; a token is added to both or to neither.** `DesignTokenTests` (WORK-PLAN, design-system)
parses the file and fails when they differ.

```css
:root {
    color-scheme: light dark;
    /* light tokens, every row of 1.1 */
}

@media (prefers-color-scheme: dark) {
    :root:not([data-theme="light"]) {
        /* dark tokens, every row of 1.1 that differs */
    }
}

:root[data-theme="light"] { color-scheme: light; }

:root[data-theme="dark"] {
    color-scheme: dark;
    /* dark tokens again: the same lines as the media block */
}
```

No `data-theme` attribute = follow the OS. `data-theme="light"` / `"dark"` = the user's choice, wins over the OS.

### 1.3 Base rules

```css
*, *::before, *::after { box-sizing: border-box; }
html { -webkit-text-size-adjust: 100%; }
body { margin: 0; background: var(--bg); color: var(--text); font-family: var(--font); font-size: 16px;
       line-height: 1.35; -webkit-font-smoothing: antialiased; }
a { color: var(--accent-ink); text-decoration: none; }          a:hover { text-decoration: underline; }
button, input, select { font: inherit; color: inherit; }        button { cursor: pointer; }
h1 { font-size: 1.25rem; font-weight: 700; margin: 0 0 1rem; }  h1:focus { outline: none; } /* FocusOnNavigate */
h2 { font-size: 1.05rem; font-weight: 700; margin: 0; }
.visually-hidden { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
```

Focus ring, everywhere interactive: `:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }`.
Inside a filled accent control (primary button, active tab) the ring is `outline-color: var(--text)` so it stays
visible against the accent fill. Never `outline: none` without a replacement.

Reduced motion block, last in the file:

```css
@media (prefers-reduced-motion: reduce) {
    *, *::before, *::after { animation: none !important; transition: none !important; scroll-behavior: auto !important; }
}
```

Nothing else is needed there: the base `.status::before` rule (section 7) already holds the static halo; the live
pulse is an `animation` on `.status--live::before` only, and switching the animation off leaves the static halo
visible for every state.

### 1.4 Top bar (sticky, translucent)

```css
.topbar { position: sticky; top: 0; z-index: 20; display: flex; align-items: center; justify-content: space-between;
          gap: 12px; padding: .7rem 1.25rem; background: color-mix(in srgb, var(--surface) 88%, transparent);
          -webkit-backdrop-filter: saturate(1.2) blur(8px); backdrop-filter: saturate(1.2) blur(8px);
          border-bottom: 1px solid var(--line); }
.brand { display: flex; align-items: center; gap: .6rem; min-width: 0; }
.brand__tile { width: 34px; height: 34px; border-radius: 10px; display: grid; place-items: center; flex: none;
               background: linear-gradient(135deg, var(--accent), color-mix(in srgb, var(--accent) 55%, #7b5cff));
               box-shadow: 0 2px 6px rgba(47,109,246,.35); }      /* dark: box-shadow 0 2px 6px rgba(0,0,0,.5) */
.brand__name { font-size: 1.05rem; font-weight: 700; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.tabs { display: flex; gap: 2px; padding: 2px; background: var(--surface-2); border: 1px solid var(--line-strong); border-radius: var(--radius-pill); }
.tabs a { padding: .32rem .85rem; border-radius: var(--radius-pill); font-size: .85rem; font-weight: 600; color: var(--muted); }
.tabs a:hover { color: var(--text); text-decoration: none; }
.tabs a.active { background: var(--accent); color: var(--accent-contrast); }
.tools { display: flex; align-items: center; gap: .5rem; min-width: 0; }
```

The brand tile holds the mockup's AirPlay glyph (the three arcs, dot and triangle SVG, stroke `#fff`, 22px desktop /
20px phone, `aria-hidden`). The white stroke is a non-text graphic: 4.5:1 on the light accent and 3.2:1 on the dark
accent, which clears the 3:1 floor for graphics; the same holds for the white note of the artwork placeholder. The
tabs are centred by the three-column flex (`justify-content: space-between` with the brand and the
tools of similar width) at ≥ 700px; at ≥ 1024px they are absolutely centred like Daikin (`position: absolute;
left: 50%; transform: translateX(-50%)`) so they never shift between pages.

### 1.5 Components (class names are the contract between CSS and components)

| Class | What | Key values |
|---|---|---|
| `.card` | surface container | `background: var(--surface); border: 1px solid var(--line); border-radius: var(--radius); box-shadow: var(--shadow); padding: 1.1rem 1.2rem; display: flex; flex-direction: column; gap: .9rem; min-width: 0` |
| `.card__head` | title row | `display: flex; align-items: center; justify-content: space-between; gap: .5rem; flex-wrap: wrap` |
| `.label` | small caps label | `font-size: .68rem; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; color: var(--muted)` |
| `.meta` | secondary line | `font-size: .8rem; color: var(--muted); overflow-wrap: anywhere` |
| `.pill` | state pill | `font-size: .7rem; font-weight: 600; padding: .15rem .5rem; border-radius: var(--radius-pill); border: 1px solid var(--line-strong); color: var(--muted); background: var(--surface-2); white-space: nowrap` |
| `.pill--ok` | playing/online/connected | `color: var(--ok-ink); border-color: color-mix(in srgb, var(--ok) 40%, var(--line))` |
| `.pill--warn` | PIN needed | `color: var(--warn-ink); border-color: color-mix(in srgb, var(--warn) 40%, var(--line))` |
| `.pill--err` | error | `color: var(--err-ink); border-color: color-mix(in srgb, var(--err) 40%, var(--line))` |
| `.pill--accent` | connecting…, active | `color: var(--accent-ink); border-color: color-mix(in srgb, var(--accent) 40%, var(--line))` |
| `.chip` | tag | `display: inline-flex; align-items: center; gap: .3rem; font-size: .78rem; font-weight: 600; padding: .28rem .65rem; border-radius: var(--radius-pill); background: var(--surface-2); border: 1px solid var(--line); color: var(--text)` |
| `.chip--accent` | Spotify chip, output tag, members chip | `background: var(--accent-soft); border-color: transparent; color: var(--accent-ink)` (the mockups colour this text with the raw `--accent`; the ink token is used, see 6.3) |
| `.chip--members` | phone members line (4.3) | `.chip.chip--accent` + `max-width: 100%; white-space: normal; overflow-wrap: anywhere; text-align: center; line-height: 1.3; justify-content: center` so three long names wrap inside the card instead of overflowing it |
| `.tools` | top-bar right cluster | `display: flex; align-items: center; gap: .5rem; min-width: 0` (also in 1.4) |
| `.theme-picker` | theme radio group | `display: flex; align-items: center; gap: 2px; padding: 2px; border: 1px solid var(--line-strong); border-radius: var(--radius-pill); background: var(--surface-2)` |
| `.theme-btn` | one theme option | `display: grid; place-items: center; width: 26px; height: 26px; padding: 0; border-radius: 50%; border: 0; background: transparent; color: var(--muted); overflow: hidden; transition: width .15s ease, opacity .15s ease` (ButtonWeb's values; `overflow: hidden` is what makes the collapse rule of 2.4 visible) ; `.theme-btn .ico { width: 15px; height: 15px }` (the reference apps' 15px, not the mockup's 18px; 18px inside the 34px phone button, 5.2); hover `color: var(--accent-ink)` under `(hover: hover)` |
| `.theme-btn.is-active` | chosen option | `background: var(--accent-soft); color: var(--accent)` (the mockup's colour; the icon is a glyph, not text: 3.8:1 light / 4.3:1 dark on `--accent-soft` over `--surface-2`, above the 3:1 floor for graphics; listed in 6.3) |
| `.lang-select` | top-bar language select | `font-size: .85rem; padding: .35rem 1.6rem .35rem .7rem; min-height: 32px; min-width: 118px; max-width: 11rem; border: 1px solid var(--line-strong); border-radius: var(--radius-sm); color: var(--text); appearance: none; background: var(--surface) url(<chevron svg>) no-repeat right .55rem center; text-overflow: ellipsis` (the chevron colours are in 3.2); the Settings page uses the `Field` variant of the component with `.input` instead (4.6) |
| `.only-phone` / `.only-wide` | responsive toggles | `.only-phone { display: none }` at ≥ 700px, `.only-wide { display: none }` at ≤ 699px; used where the phone shows different text or actions than the desktop (4.3). Nothing else is hidden with these two classes |
| `.btn` | button | `font-size: .85rem; font-weight: 600; padding: .45rem .9rem; min-height: 36px; border-radius: var(--radius-sm); border: 1px solid var(--line-strong); background: var(--surface); color: var(--text)`; hover `border-color: var(--accent)`; disabled `opacity: .5; cursor: default` |
| `.btn--primary` | main action | `background: var(--accent); border-color: var(--accent); color: var(--accent-contrast)` |
| `.btn--ghost` | low-key action | `border-color: transparent; background: transparent; color: var(--accent-ink)` |
| `.btn--danger` | delete/forget | light: `background: var(--err-ink); border-color: var(--err-ink); color: #fff`; dark: `background: var(--err); border-color: var(--err); color: var(--accent-contrast)` |
| `.btn--sm` | row buttons | `padding: .25rem .6rem; font-size: .78rem; min-height: 30px` (44px hit area on touch via `::before`, 5.4) |
| `.btn--icon` | icon-only round | `width: 2.2rem; height: 2.2rem; padding: 0; border-radius: 50%; display: grid; place-items: center` |
| `.step` | − / + and transport | `width: 2.2rem; height: 2.2rem; border-radius: 50%; border: 1px solid var(--line-strong); background: var(--surface); font-weight: 700; font-size: 1.05rem; display: grid; place-items: center` |
| `.step--lg` | prev/next | `width: 2.8rem; height: 2.8rem` |
| `.play` | play/pause | `.btn--primary` + `width: 3.6rem; height: 3.6rem; border-radius: 50%; padding: 0; display: grid; place-items: center` |
| `.row` | list row | `display: flex; align-items: center; gap: .8rem; padding: .7rem .4rem; border-top: 1px solid var(--line); min-height: 44px`; first row no border |
| `.row.is-active` | selected output | `background: var(--accent-soft); border-radius: 10px; border-top-color: transparent; padding-left: .7rem; padding-right: .7rem` |
| `.radio` | custom radio glyph | `width: 20px; height: 20px; border-radius: 50%; border: 2px solid var(--line-strong); background: var(--surface); display: grid; place-items: center; flex: none`; `.is-on { border-color: var(--accent) }`; `.is-on::after { content:""; width: 10px; height: 10px; border-radius: 50%; background: var(--accent) }` |
| `.art` | artwork | `width: 200px; height: 200px; border-radius: 16px; object-fit: cover; box-shadow: var(--shadow); flex: none`; placeholder: the mockup gradient `linear-gradient(135deg,#1d4ed8,#7c3aed 60%,#ec4899)` with the white note glyph |
| `.big` | large number | `font-size: 1.7rem; font-weight: 700; color: var(--accent-ink); min-width: 2.6rem; text-align: right; font-variant-numeric: tabular-nums` |
| `.field` | form field | `display: flex; flex-direction: column; gap: .3rem` ; `.field > label` is `.label`-like but `text-transform: none; font-size: .85rem; font-weight: 600; color: var(--text)` |
| `.input` | text/number/select | `padding: .5rem .7rem; min-height: 40px; border: 1px solid var(--line-strong); border-radius: var(--radius-sm); background: var(--surface); color: var(--text)`; invalid `border-color: var(--err)` |
| `.hint` | field hint | `.meta` + `margin: 0` |
| `.validation-message` | DataAnnotations message | `color: var(--err-ink); font-size: .85rem` |
| `.empty` | empty state | `.card` with `border-style: dashed; box-shadow: none; color: var(--muted); text-align: center; padding: 2rem 1rem` |
| `.facts` | dl grid | `display: grid; grid-template-columns: max-content 1fr; gap: .25rem 1rem; margin: 0`; `dt { color: var(--muted) }`; `dd { margin: 0; overflow-wrap: anywhere }` |
| `.log` | log box | `font-family: var(--font-mono); font-size: .78rem; background: var(--surface-2); border: 1px solid var(--line); border-radius: var(--radius-sm); padding: .6rem; max-height: 28rem; overflow: auto; overflow-wrap: anywhere` ; `.log__line--warn { color: var(--warn-ink) }`, `--fail`/`--crit` `var(--err-ink)`, `--dbug`/`--trce` `var(--muted)` |

### 1.6 Sliders

Sliders stay native `<input type="range">` (keyboard, screen readers, touch for free) and are styled to look like
the mockup's track:

```css
.level { display: grid; grid-template-columns: auto 1fr auto; align-items: center; gap: .8rem; --fill-color: var(--accent); }
.level--hero { grid-template-columns: auto 1fr auto auto; }                   /* − track + number */
.level--compact { grid-template-columns: 1fr auto; gap: .4rem; width: 150px; }  /* track + number, Output rows */
.row.is-quiet .level { --fill-color: var(--line-strong); }                     /* de-emphasised: Output rows that are not the output (4.3) */
.level input[type="range"] { -webkit-appearance: none; appearance: none; width: 100%; height: 28px; background: transparent; margin: 0; }
.level input[type="range"]::-webkit-slider-runnable-track { height: 6px; border-radius: var(--radius-pill); border: 1px solid var(--line);
    background: linear-gradient(var(--fill-color), var(--fill-color)) 0 / var(--fill, 0%) 100% no-repeat var(--surface-2); }
.level input[type="range"]::-moz-range-track { height: 6px; border-radius: var(--radius-pill); border: 1px solid var(--line); background: var(--surface-2); }
.level input[type="range"]::-moz-range-progress { height: 6px; border-radius: var(--radius-pill); background: var(--fill-color); }
.level input[type="range"]::-webkit-slider-thumb { -webkit-appearance: none; width: 18px; height: 18px; margin-top: -7px; border-radius: 50%;
    background: var(--surface); border: 2px solid var(--accent); box-shadow: 0 1px 3px rgba(16,24,40,.25); }
.level input[type="range"]::-moz-range-thumb { width: 18px; height: 18px; border-radius: 50%; background: var(--surface); border: 2px solid var(--accent); }
.level--hero input[type="range"]::-webkit-slider-runnable-track { height: 10px; }
.level--hero input[type="range"]::-webkit-slider-thumb { width: 24px; height: 24px; margin-top: -8px; }
.level input[type="range"]:focus-visible { outline: none; }
.level input[type="range"]:focus-visible::-webkit-slider-thumb { outline: 2px solid var(--accent); outline-offset: 2px; }
.level output { font-variant-numeric: tabular-nums; text-align: right; min-width: 1.6rem; color: var(--muted); font-size: .8rem; }
.level output.draft { color: var(--accent-ink); font-weight: 600; }
.level--compact output.draft { color: var(--accent-ink); }
```

`LevelSlider` sets `style="--fill: {value}%"` on the input so the WebKit track shows the filled part (Firefox uses
`::-moz-range-progress`). The fill colour is `--fill-color`: accent by default, `--line-strong` (the mockup's grey
fill) inside a `.row.is-quiet`, which `OutputList` sets on every receiver row that is not the current output (4.3);
the thumb border stays accent so the control is still recognisable. Member mini-bars that are not interactive (Player
hero, 4.3) use `.track`/`.track__fill` exactly as in the mockup (`height: 6px`, fill `var(--accent)`), never a
disabled range input. There is no `.track__knob` anywhere in the app (8.1): the only draggable thumbs are the native
range thumbs.

The old `.level` rules (label column of 5.5rem) go away; the label is a visually-hidden `<label>` unless the
variant shows it.

`LevelSlider` contract (design-system, wave 1): parameters `Variant` (`Default|Compact|Hero`), `Label` (string,
required: the caller passes the catalog text), `HideLabel` (bool), `Value`, `Disabled`, `ValueText`
(`Func<int, string>`, default `v => v.ToString(CultureInfo.CurrentCulture)`: the number alone, so the component holds
no English), `ShowValue` (default true), `ValueChanged`/the send callback as today. `aria-valuetext` and the visible
`<output>` both come from `ValueText`. Every page caller passes `ValueText="v => L.Format(\"common.levelText\",
(\"value\", v))"` ("{{value}} of 100"). Public method `Task StepAsync(int delta)`: clamps `Value + delta` to 0..100,
updates the draft, raises `DraftChanged` and sends through the same `CoalescingSender` path as a drag; `MasterVolume`
(4.3) calls it through `@ref` for the −/+ steppers.

---

## 2. Theme support

### 2.1 Storage and attribute

- `localStorage["airplay-bridge-theme"]` is `"light"` or `"dark"`; absent means system. Nothing else is stored.
- `<html data-theme="light|dark">` mirrors the stored value; no attribute for system.
- `<meta name="theme-color">`: two media-scoped metas (`#eef1f6` for `(prefers-color-scheme: light)`, `#0d1117`
  for dark) plus, for an explicit choice, one media-less override meta inserted **before** them
  (`id="theme-color-override"`, the ButtonWeb mechanism; browsers take the first matching meta). Choosing system
  removes the override.

### 2.2 Pre-paint bootstrap in `wwwroot/index.html`

The inline script sits in `<head>` after the two theme-color metas and before the stylesheet link, so the first
paint already has the right scheme:

```html
<script>
(function () {
    var KEY = "airplay-bridge-theme";
    function applyThemeColor(theme) {
        var override = document.getElementById("theme-color-override");
        if (theme !== "light" && theme !== "dark") { if (override) override.remove(); return; }
        if (!override) { override = document.createElement("meta"); override.id = "theme-color-override";
                         override.name = "theme-color"; document.head.insertBefore(override, document.head.firstChild); }
        override.content = document.querySelector('meta[name="theme-color"][media="(prefers-color-scheme: ' + theme + ')"]').content;
    }
    function apply(theme, persist) {
        if (theme === "light" || theme === "dark") document.documentElement.setAttribute("data-theme", theme);
        else document.documentElement.removeAttribute("data-theme");
        applyThemeColor(theme);
        if (persist) { try { if (theme === "light" || theme === "dark") localStorage.setItem(KEY, theme); else localStorage.removeItem(KEY); } catch (e) {} }
    }
    function read() { try { var t = localStorage.getItem(KEY); return t === "light" || t === "dark" ? t : "system"; } catch (e) { return "system"; } }
    var q = new URLSearchParams(location.search).get("theme");       // one-shot override for screenshots and shared links; never stored
    apply(q === "light" || q === "dark" || q === "system" ? q : read(), false);
    window.bridgeTheme = { apply: function (t) { apply(t, true); }, read: read };
})();
</script>
```

`?theme=light|dark|system` is honoured on every load but never persisted; `?lang=` works the same way (3.2). This is
the screenshot mechanism of the work plan (test strategy); it is harmless in production.

### 2.3 How Blazor talks to it: a plain JS file driven by the component

`wwwroot/js/app.js` is an ES module loaded with `IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/app.js")`
by `BrowserBridge` (a scoped-free singleton service `Bridge.Web/BrowserBridge.cs`). `index.html`, `app.js` and
`BrowserBridge.cs` are delivered together by the `foundation` package (WORK-PLAN, wave 0) so that both wave-1
packages can build on them. The theme and language components call it; the module owns no UI. It exports:

| Export | Does |
|---|---|
| `getTheme()` | `window.bridgeTheme.read()` → `"light"|"dark"|"system"` |
| `setTheme(theme)` | `window.bridgeTheme.apply(theme)` (attribute, meta, storage) |
| `hoverCapable()` | `matchMedia("(hover: hover)").matches` |
| `getLang()` | the `?lang=` query if it names a supported code, else `localStorage["airplay-bridge-lang"]`, else the first of `navigator.languages` whose two-letter code is supported, else `""` (caller falls back to `"en"`) |
| `setLang(code)` | stores `airplay-bridge-lang` (or removes it when `code === ""`) and sets `document.documentElement.lang = code` |
| `setDocumentLang(code)` | `document.documentElement.lang = code` only (start-up) |
| `onOutsideClick(element, dotnetRef, method)` | registers a document **`click`** listener (bubbling phase, like both reference pickers' `document.addEventListener("click", …)`) that invokes `dotnetRef.invokeMethodAsync(method)` when `!element.contains(e.target)`; returns `{ dispose() }`. A `click` is used, not `pointerdown`: the tap that opens the picker is itself a click inside `element`, so the handler never fires for it and the open-then-close-on-the-same-tap sequence cannot happen |
| `measure(selector, cssVar)` | ResizeObserver that writes `Math.ceil(height) + "px"` of the element to `--cssVar` on `<html>` (the shell publishes `--topbar-h` and `--tabbar-h`) |
| `rememberActive()` | stores `document.activeElement` and returns `{ restore() }`, which focuses it again if it is still in the document (the pairing dialog, 4.11) |
| `audit()` | the layout audit of 5.6; returns the JSON object described there |

`BrowserBridge` wraps these as `Task<string> GetThemeAsync()`, `Task SetThemeAsync(string)`, `Task<bool>
HoverCapableAsync()`, `Task<string> GetLangAsync()`, `Task SetLangAsync(string)`, `Task<IAsyncDisposable>
OnOutsideClickAsync(ElementReference, DotNetObjectReference<T>, string)`, `Task MeasureAsync(string, string)`,
`Task<IJSObjectReference> RememberActiveAsync()` (call `restore` on it), `Task<string> AuditAsync()` (the JSON as a
string). In bUnit the module is mocked (`JSInterop.SetupModule("./js/app.js")`), so components never call
`IJSRuntime` directly.

### 2.4 The theme picker (component `Components/ThemePicker.razor`)

Exactly the reference apps' control: a `role="radiogroup"` pill with three `role="radio"` buttons, light / dark /
system, in that order, icons sun / moon / half-disc (the SVG paths of ButtonWeb's `_Layout`). Behaviour:

- Collapsed it shows only the active option (`.theme-picker:not(:hover):not(:focus-within):not(.theme-picker--open)
  .theme-btn:not(.is-active) { width: 0; opacity: 0; pointer-events: none }`); hover or focus-within unfolds it.
- Touch (no hover, `hoverCapable() == false`): the first tap on the pill adds `theme-picker--open` instead of
  selecting (`e.detail !== 0` distinguishes pointer clicks from keyboard activation, which always selects); selecting
  collapses it; a click outside (`onOutsideClick`, a document `click` listener that ignores targets inside the
  picker, 2.3) or Escape collapses it. The opening tap is inside the picker and therefore never collapses what it
  just opened; `ShellTests` covers both "a click outside collapses" and "a click inside does not".
- `aria-checked="true|false"` follows the choice; the checked button has `tabindex="0"`, the others `-1` (roving
  tabindex: one Tab stop); Arrow Right/Down and Left/Up move focus **and** selection (wrapping), Home/End go to the
  first/last; `aria-label` on every button (theme.light / theme.dark / theme.system) and on the group (theme.groupLabel).
- Selecting calls `BrowserBridge.SetThemeAsync`; the component keeps `_theme` and re-renders. The initial value comes
  from `GetThemeAsync` in `OnAfterRenderAsync(firstRender)` (prerendering is not used, but the module is only there
  after the first render).
- Size: 26px buttons in a 2px-padded pill at ≥ 700px; 34px buttons at ≤ 699px (5.2). The buttons are **not** grown
  to 44px and get **no** `::before` halo: `.theme-btn` has `overflow: hidden` (it drives the collapse animation) and
  would clip a halo, and three 44px buttons would not fit beside the status pill at 320px (ButtonWeb's site.css
  documents the same decision). 34px clears the 24px WCAG 2.5.8 floor; the audit's `small` list (5.6) allow-lists
  `.theme-btn`, and the Settings Appearance card is the 44px way to choose a theme on a phone.

`Pages/Settings.razor` has an **Appearance** card with the same choice as three ordinary labelled radio inputs (no
collapsing), driven by the same `BrowserBridge`; it is the thumb-friendly place on phones.

---

## 3. Language support

### 3.1 Model (port of `I18nCatalog`, plus additions)

`Bridge.Web/Localization/I18n.cs`, a singleton registered in `Program.AddBridgeClient`. Daikin's `I18nCatalog`
(indexer, `GetString`, `Format`, `ResolveCulture`; neither reference catalog has plural keys) is the reference for
the embedded-JSON loading, the flattening and the fallback order. **New here, with no reference implementation:**
`Plural`/`PluralRules`, `SetLanguage`, `Changed`, `Culture`, `Keys()`. Daikin's `Format` formats with
`CultureInfo.CurrentCulture`; this `Format` uses the `Culture` property, and `SetLanguage` sets
`CultureInfo.CurrentCulture`/`CurrentUICulture` and the `DefaultThreadCurrent*` to the same value, so the two agree
(and the `Format` helpers of 3.4, which read `CurrentCulture`, print the same language as the catalog).

```csharp
public sealed record Language(string Code, string NativeName);

public sealed class I18n
{
    public const string SourceCulture = "de";                   // the catalogs' source language
    public const string DefaultCulture = "en";                  // served when nothing matches
    public static readonly IReadOnlyList<Language> Languages = [ de Deutsch, en English, fr Français, es Español,
                                                                  ru Русский, uk Українська, pt "Português (Brasil)" ];
    public static readonly string[] SupportedCultures;          // the codes
    public I18n(string? initialCode = null);                    // loads every embedded catalog, flattens to "a.b.c"
    public string Code { get; }                                 // the resolved two-letter code
    public CultureInfo Culture { get; }                         // de→"de-DE", en→"en-US", fr→"fr-FR", es→"es-ES", ru→"ru-RU", uk→"uk-UA", pt→"pt-BR"
    public event Action? Changed;
    public string this[string key] { get; }                     // current language → German → the key itself
    public string Format(string key, params (string Name, object? Value)[] args);   // {{name}} replacement, values formatted with Culture
    public string Plural(string key, long count, params (string Name, object? Value)[] args); // picks key_one/_few/_many/_other, adds {{count}}
    public void SetLanguage(string code);                       // resolves, sets CultureInfo.DefaultThreadCurrentCulture/UICulture and CurrentCulture/CurrentUICulture, raises Changed
    public static string Resolve(string? code);                 // supported two-letter code or DefaultCulture
    public IReadOnlyDictionary<string, string> Keys(string code);  // tests: the flattened catalog
}
```

- Catalogs are **embedded resources** `Resources/i18n/{code}.json` (csproj: `<EmbeddedResource Include="Resources\i18n\*.json"
  LogicalName="Bridge.Web.Resources.i18n.%(Filename)%(Extension)" />`), i18next JSON: nested objects, string leaves,
  `{{placeholders}}`, plural suffixes `_one`, `_few`, `_many`, `_other`. Embedded (not fetched) so bUnit tests and the
  first render have them without HTTP; seven catalogs are a few KB each.
- Lookup order: the current language → German (source) → the key itself (never throws; a missing key is visible as
  the key, and the parity test catches it).
- `Plural`: CLDR cardinal categories implemented in `Localization/PluralRules.cs`:
  `de`, `en`, `es`: `n == 1 → one` else `other`; `pt` (pt-BR): `n == 0 || n == 1 → one`; `fr`: `n == 0 || n == 1 → one`;
  `ru`, `uk`: `n % 10 == 1 && n % 100 != 11 → one`, `n % 10 in 2..4 && n % 100 not in 12..14 → few`, else `many`
  (`other` is for fractions and unused with integer counts). Lookup falls back `category → other → base key`.
- `Format` replaces `{{name}}` with `Convert.ToString(value, Culture)`; numbers formatted upstream with the helpers of 3.4.

### 3.2 Selection, storage, start-up

- `localStorage["airplay-bridge-lang"]` holds the code; absent = browser negotiation (`navigator.languages`), then `en`.
- `?lang=xx` on the URL is a one-shot override (not stored), like `?theme=`.
- Start-up (`Program.Main`): after `builder.Build()`, `var js = host.Services.GetRequiredService<IJSRuntime>();
  var code = await js.InvokeAsync<string>("bridgeLang.get")` (a second tiny inline function in index.html, same
  logic as the module's `getLang`, so the culture is set before the first render), then `I18n.SetLanguage(code)` and
  `js.InvokeVoidAsync("bridgeLang.setDocument", code)`. `<html lang>` therefore always matches.
- Switching at runtime (`LanguageSelect` → `I18n.SetLanguage` + `BrowserBridge.SetLangAsync`) raises `Changed`;
  `BridgeComponentBase` subscribes to it like to `BridgeState.Changed` and re-renders, `MainLayout` too, so the
  whole app switches without a reload. `PageTitle` values are rebuilt on render.
- `Components/LanguageSelect.razor` renders a `<select>` listing `I18n.Languages` with the native names as option
  text and the code as value, `@onchange` → switch. Parameters: `Variant` (`Bar`, the default, or `Field`) and `Id`
  (string, default `"lang-select"`). `Bar`: `<select id=… class="lang-select" aria-label="{language.label}"
  title="{language.label}">`, the top-bar pill look of 1.5. `Field`: `<select id=… class="input">` with **no**
  `aria-label`; the surrounding `.field` (Settings, 4.6) supplies `<label for="{Id}">`, and `.input` gives the full
  width, 44px and 16px font of 5.4. The mockup's chevron background SVG of `.lang-select` uses the `--muted` colour:
  light `%235f6b7a`, dark `%2393a0ad` (two rules); the `Field` variant uses the same chevron through `.input` when it
  is a `select` (`select.input { appearance: none; background: … }`, same two rules).

### 3.3 Globalization data in WASM (csproj)

`Directory.Build.props` sets `<InvariantGlobalization>true</InvariantGlobalization>` for every project; with it the
WASM runtime ships no ICU and `1234.5.ToString("N1", de)` would print `1,234.5`. `src/Bridge.Web/Bridge.Web.csproj`
overrides:

```xml
<InvariantGlobalization>false</InvariantGlobalization>
<!-- All ICU data (icudt.dat), not the shard picked from navigator.language at boot: the user switches to ru/uk at runtime. -->
<BlazorWebAssemblyLoadAllGlobalizationData>true</BlazorWebAssemblyLoadAllGlobalizationData>
```

(If the SDK reports the property as unknown, `<WasmIncludeFullIcu>true</WasmIncludeFullIcu>` is the equivalent; verify
by checking that `bin/.../wwwroot/_framework/blazor.boot.json` lists `icudt.dat`.) `tests/Bridge.Tests/Bridge.Tests.csproj`
also sets `<InvariantGlobalization>false</InvariantGlobalization>`, otherwise the formatting tests of 3.4 run on
invariant data on the desktop runtime. **This is intended and applies to the whole test project**, including the
service and live tests: they must not depend on the invariant culture being the process culture. Service code
(`src/Bridge.Service`, `Bridge.Contracts`) keeps `InvariantGlobalization=true` from `Directory.Build.props` and keeps
formatting log lines, `ApiRoutes` and JSON with the invariant culture; nothing in this spec changes the service. The
`<SatelliteResourceLanguages>en</SatelliteResourceLanguages>` of `Directory.Build.props` filters only .resx satellite
assemblies; it has no effect on the embedded JSON catalogs and must not be changed.

### 3.4 Formatting (through the selected culture)

`Format.cs` grows culture-aware helpers; every page uses them instead of `CultureInfo.InvariantCulture` for things a
user reads:

| Helper | Output | Example de / en |
|---|---|---|
| `Format.Integer(long)` | `N0` in `CultureInfo.CurrentCulture` | `1.234` / `1,234` |
| `Format.Decimal(double, int digits)` | `N{digits}` | `1,75` / `1.75` |
| `Format.Percent(double ratio, int digits)` | `P{digits}` with the culture's spacing; consumer: the retransmit ratio on Diagnostics (4.7) | `0,4 %` / `0.4%` |
| `Format.Milliseconds(int frames)` | whole ms as `N0` (the unit "ms" is part of the catalog string that embeds it) | `1.750` / `1,750` |
| `Format.Time(DateTimeOffset)` | local time, culture `T` pattern | `14:05:09` / `2:05:09 PM` |
| `Format.DateTime(DateTimeOffset)` | local, culture `G` pattern | `10.10.2026 14:05:09` / `10/10/2026 2:05:09 PM` |
| `Format.PlaybackTime(long ms)` | unchanged: `m:ss` invariant (universal) | `1:12` |
| `Format.Remaining(long ms)` | `"−" + PlaybackTime` (U+2212) | `−2:22` |
| `Format.FramesAsMs(int)` | stays (invariant, used in hints with `Integer` around it) | |

Log lines keep `HH:mm:ss.fff` invariant (they are diagnostics, aligned columns). There is no `Format.Seconds`: the
only consumer would have been the mockups' Stream fact, which is not built (8.1); the "1,75 s" in
settings.latencyHint2 is literal catalog text.

### 3.5 What is not translated

Text that originates in the service stays as the service sends it (English): problem-details titles and details,
`PairingStartedDto.Message`, log lines, pending-probe names, receiver names and models. The dialog composes its own
localized sentence for service-originated prompts (`pairing.prompt`) and shows `prompt.Message` only as the muted
second line when the origin is `User`.

### 3.6 The catalog

Section 10 lists every key with the German source text and the English text. `i18n-core` ships `de.json` and
`en.json`; `i18n-fill` adds fr, es, ru, uk, pt with the same key set (parity test: every key of `de.json` exists in
every other catalog, including the plural variants required by that language's categories; `en` must have `_one` and
`_other` for every plural key, `ru`/`uk` `_one`, `_few`, `_many`, `_other`).

---

## 4. Layout and pages

### 4.1 Routes

| Route | Page | Tab | Notes |
|---|---|---|---|
| `/` | Player | Player | was `/now-playing`; the old route is dropped |
| `/receivers` | Receivers | Receivers | was `/` |
| `/groups` | Groups (list) | Player | reachable from Output → "Edit"/"New group"; back link to `/` |
| `/groups/new` | Group editor (create) | Player | |
| `/groups/{id}` | Group editor | Player | |
| `/settings` | Settings | Settings | |
| `/settings/diagnostics` | Diagnostics | Settings | sub-page: back link "Settings" |
| `/_styleguide` | component gallery (no data) | none | used by the screenshot checks; harmless in production |
| `/_audit` | layout audit (5.6) | none | |

Tab highlighting: `NavLink` with `Match="NavLinkMatch.Prefix"` on `/receivers` and `/settings`; the Player tab is
active for `/` and `/groups*` (a small `TabLink` component computes `active` from `NavigationManager.Uri`).

`App.razor` NotFound: `<h1>` app.notFoundTitle + `<p>` app.notFound + link to `/` (nav.player), inside MainLayout.

Page titles: every page writes `<PageTitle>@L.Format("app.pageTitle", ("page", L[key]))</PageTitle>` itself, with
`key` = nav.player (`/`), nav.receivers, nav.groups (`/groups*`), nav.settings, nav.diagnostics
(`/settings/diagnostics`), styleguide.title, audit.title, app.notFoundTitle. There is no `BridgeTitle` helper
component; pages do not depend on the shell for their title.

The route moves (`/` → Player, `/receivers`, `/settings/diagnostics`; `/now-playing` and `/diagnostics` dropped) are
made on the **existing** pages by the `foundation` package before any page is rewritten (WORK-PLAN, wave 0), so no
two pages ever claim `/` and the old links never 404 during later waves.

### 4.2 Shell (`Layout/MainLayout.razor`)

```
<header class="topbar">
  <a class="brand" href="/"> tile + <span class="brand__name">{app.name}</span> </a>   (aria-label app.name on the link)
  <nav class="tabs" aria-label="{nav.sections}">  Player | Receivers | Settings  </nav>     (hidden < 700px)
  <div class="tools"> <LanguageSelect/> <ThemePicker/> <StatusPill/> </div>
</header>
<ConnectionBanner/>                 slim strip under the bar, only while not connected (4.10)
<main class="main"> @Body </main>
<NoticeArea/>                       toasts (4.9), fixed
<TabBar/>                           phone tab bar (5.2), only < 700px
<PairingDialog/>
```

`main`: `max-width: 1440px; margin: 0 auto; padding: 1.25rem; padding-bottom: calc(1.25rem + var(--tabbar-h) +
env(safe-area-inset-bottom))` at < 700px, `padding-bottom: 2rem` otherwise. Every page starts with an `<h1>` (the
page name; `FocusOnNavigate` keeps targeting it); on the Player page the `<h1>` is visually hidden (the hero is the
title), elsewhere it is shown.

### 4.3 Player page (`Pages/Player.razor`, route `/`)

Two columns at ≥ 1024px (`grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 1.25rem; align-items: start`): hero
left, Output and Status stacked right. One column below.

**Now-playing hero** (`section.card.hero`, `aria-label` player.nowPlaying), components `Components/NowPlayingHero.razor`:

1. Top: artwork (`.art`, `<img>` with `alt` = player.artworkAlt `{{album}}`, or the gradient placeholder,
   `aria-hidden`), beside it (phone: below it, centred) a column: chip `.chip--accent` "Spotify · {{user}}" (player.spotifyChip;
   player.spotifyOnly when no user; the chip is omitted when Spotify is not connected), `<strong class="hero__title">`
   title (1.6rem / 1.2rem phone, `overflow-wrap: anywhere`, max 2 lines with `-webkit-line-clamp: 2`), artist
   (1.05rem), `.meta` "{{album}} · {{duration}}" (player.albumMeta; album alone if no duration).
   Nothing playing: title = player.nothingPlaying (muted), second line player.startInSpotify when Spotify is connected
   else player.selectDevice with `{{device}}` = `State.Status.DeviceName`, or player.bridgeFallback ("the bridge")
   while `Status` is null (today's fallback, now catalogued).
   The Spotify volume `<meter>` of today's NowPlaying page is **dropped** (8.1): the master slider follows the
   Spotify slider (player.followsSpotify says so) and `PlaybackDto.SpotifyVolume` is no longer shown anywhere.
2. Progress row: `.meta` elapsed, `.track` (8px, `role="progressbar"` with `aria-valuemin/max/now` and
   `aria-valuetext` = player.progressText "{{elapsed}} of {{total}}"), `.meta` remaining as `−m:ss`. Hidden when
   `DurationMs == 0`. The 1 s re-render of the current page stays. **No knob and no seeking**: the desktop mockup
   draws a `.track__knob` on this bar, but there is no seek API, so the bar is a plain read-only progress bar
   without a handle (8.1).
3. Transport, centred: `.step.step--lg` previous (player.previous), `.play` play/pause (player.play / player.pause),
   `.step.step--lg` next (player.next); icons are the mockup SVGs; all disabled while Spotify is not connected or a
   command is in flight; `aria-label` on each.
4. Master volume block (`Components/MasterVolume.razor`), separated by a top border:
   - Row 1: `.label` player.masterVolume "{{output}}" where output is the active group's name or the single
     receiver's name; player.masterVolumeNone when nothing is selected. Right: `.meta` player.followsSpotify.
   - Row 2: `.step` "−" (player.quieter), `LevelSlider Variant="Hero"` (`Label` = player.masterVolume text, hidden;
     `ValueText` = common.levelText "{{value}} of 100"), `.step` "+" (player.louder), `.big` number. Steps are ±5,
     clamped 0..100: `MasterVolume` holds the slider in `@ref` and calls its public `StepAsync(±5)` (1.6), which
     clamps, updates the draft, raises `DraftChanged` and sends through the same `CoalescingSender` path as a drag.
     This is the contract between design-system and player-page; there is no second path. Target: group →
     `Commands.SetGroupLevelAsync(groupId, g)`; receiver → `Commands.SetReceiverLevelAsync(id, level)`; none →
     disabled, number shows `–` (U+2013, a glyph, not catalog text).
   - Row 3: member levels, `display: flex; flex-wrap: wrap; gap: .5rem 1.5rem`; each `min-width: 200px; flex: 1`:
     `.meta` name (min-width 7.5rem, ellipsis), `.track` with `.track__fill` at the member's r_i, `.meta` number.
     While the master slider is dragged the fills show `GroupVolumePreview.Preview` (the existing live preview);
     the clamp hint (groups.clampHint) appears as `.meta` under the row when `GroupVolumePreview.Clamps`. For a single
     receiver output this row is omitted (one member = the master).
   Phone (≤ 699px, `.only-phone`): row 3 is replaced by one `.chip.chip--members` line (1.5), e.g.
   "Downstairs · Test Speaker ABCD 40 · Office 24": player.membersChip with `{{output}}` = the group name and
   `{{members}}` = the members as "name level" (name, a space, the level) joined with " · ". Names are used as they
   come from the receivers (the phone mockup's shortened "SpeakerA" is illustrative; nothing truncates), and the chip
   wraps onto several lines inside the card (`white-space: normal; max-width: 100%`). The chip is omitted for a
   single-receiver output and when nothing is selected. Row 3 (desktop) carries `.only-wide`.

**Output card** (`section.card`, `Components/OutputList.razor`):

- Head: `<h2>` player.output; right, a `.card__actions` flex group (`gap: .5rem`) with three elements, toggled by
  the utility classes of 1.5:
  - `.btn.btn--ghost.btn--sm.only-wide` player.newGroup (→ `/groups/new`; desktop/tablet only);
  - `a.only-phone` player.editGroups (→ `/groups`, `font-size: .82rem; font-weight: 600`; the phone mockup's link;
    the Groups page has groups.newGroup, so nothing is lost);
  - `.btn.btn--sm` player.stopOutput (`Commands.ClearOutputAsync`): on desktop/tablet always rendered and disabled
    when nothing is selected; on phones rendered **only while an output is selected** (a disabled button would only
    take width there). The phone therefore keeps a stop path on the Player page; the Receivers page's Disconnect and
    the group editor's groups.stopOutput are the other two.
- Directly under the head, when `State.Loaded`, there is at least one receiver and `State.Output` has neither
  `GroupId` nor `ReceiverId`: `<p class="meta output__none" role="status">` player.noOutput ("Nothing selected: audio
  is discarded. Choose a receiver or a group."). This is the only place player.noOutput renders; `PlayerTests`
  asserts the English text contains "Nothing selected" (the existing test's phrase).
- A `<fieldset class="output__list">` with a visually-hidden `<legend>` player.outputChoice (no `role`/`aria-label`:
  a fieldset is named by its legend, 6.1) of `.row` labels, each wrapping a visually-hidden native `<input
  type="radio" name="output">` and the `.radio` glyph (`.is-on` when checked), so arrow keys, Space and screen readers
  work natively. Checking a row calls `Commands.ActivateGroupAsync(groupId)` or `Commands.ConnectAsync(receiverId)`;
  the row shows `.is-active` for the current output (`State.Output.GroupId` / `ReceiverId`); while a request runs the
  row's radio is disabled and the pill shows pills.connecting.
- Order: groups (by name), then receivers (by name). Row content:
  - Group: `<strong>` name, meta, pill (4.8), `.btn.btn--sm` player.edit (→ `/groups/{id}`; `aria-label`
    player.editGroup "{{name}}"). Radio disabled when the group has no members or no member is online.
    Meta is two spans: `.meta.only-wide` player.groupMeta "Group · {{members}}" (names joined by ", "; player.groupEmpty
    when none) and `.meta.only-phone` with the member names joined by ", " alone (the phone mockup's "SpeakerA, Office";
    player.groupEmptyShort when none).
  - Receiver: `<strong>` name, meta, pill (4.8), then either `LevelSlider Variant="Compact"` (150px, `Label` =
    receivers.volumeOf "{{name}}" hidden, `ValueText` common.levelText, sends `Commands.SetReceiverLevelAsync`; value
    = `State.LevelOf(receiver)`) or, when `Pairing == PinRequired`, `.btn.btn--ghost.btn--sm` player.pair
    (`Commands.StartPairingAsync`). Radio disabled when offline or `Pairing == Disabled`. Meta is two spans:
    `.meta.only-wide` player.receiverMeta "{{model}} · {{address}}" (player.unknownModel when no model; address
    without port) and `.meta.only-phone` with the model alone (player.unknownModel when none; the phone mockup's
    "Apple receiver"). Receiver rows that are not the current output carry `is-quiet` so their compact slider's fill
    is the grey `--line-strong` of the mockup (1.6); the output's row keeps the accent fill. Group rows have no slider.
  - Empty state (no receivers, `State.Loaded`): `.empty` receivers.empty + receivers.emptyHint.
  Phone: the compact slider is hidden (`display: none` ≤ 699px, the Receivers page has the full one); the row keeps
  radio, name, meta and pill (5.3).

**Status card** (`section.card`, `Components/StatusCard.razor`):

- Head: `<h2>` player.status; right: link `/settings/diagnostics` player.diagnostics.
- Grid of three (≥ 1024px) / two (700–1023) / one column facts, each `.label` + value (600 weight):
  player.spotify → player.connectedAs "{{user}}" in `--ok-ink` / player.connected / player.notConnected (muted);
  player.outputFact → player.outputConnected (`--ok-ink`) / player.outputNone (muted); player.receiversFact →
  `Plural("player.receiversOnline", onlineCount)` ("{{count}} of {{total}} online"); player.liveUpdates → the status
  text of 7 in its ink.
- **Not built from the mockups** (8.1): the "Stream · Realtime · NTP · 1.75 s" fact (stream type, timing and latency
  come from `SettingsDto`, which the Player does not load; the Settings page shows the same values as its fields) and
  the "Retransmits 0.4 % · 0 late" fact (it lives on Diagnostics as the retransmit-ratio line, 4.7). The card has
  exactly the four facts above.

### 4.4 Receivers page (`Pages/Receivers.razor`, route `/receivers`)

`<h1>` receivers.title; `.meta` lead line (receivers.lead: device name + Spotify state + output summary, as today but
through the catalog). Then `.cards` grid: `grid-template-columns: repeat(auto-fill, minmax(320px, 1fr)); gap: 1.25rem`
(one column < 700px). Each receiver is an `article.card.receiver[data-receiver-id]`:

1. `.card__head`: `<h2>` name; pill (4.8).
2. `.meta`: "{{model}} · {{address}}:{{port}}" (receivers.unknownModel when no model).
3. Level block: `.label` receivers.volume, then `LevelSlider Variant="Hero"` (`Label` = receivers.volumeOf "{{name}}"
   hidden, `ValueText` common.levelText) with the `.big` number (same look as the master slider; no −/+ steppers here).
4. Tags row (`.tags`, chips): chip "AirPlay 2" when `Capabilities.AirPlay2`, "PTP", "Buffered" (receivers.tags.*),
   the pairing chip (pills.paired `.chip--accent`, pills.pinNeeded `.chip` with `--warn-ink`, pills.noPin, pills.homekitOnly),
   group chip receivers.tags.group "{{name}}", output chip receivers.tags.output (`.chip--accent`) when in the output.
5. Error line (`.meta` in `--err-ink`, `role="alert"`) when `State == Error` and `Error` is set.
6. Actions row (`display: flex; flex-wrap: wrap; gap: .5rem`): `.btn.btn--primary` receivers.connect /
   receivers.connecting ("Connecting…", the busy label) or `.btn` receivers.disconnect; `.btn` receivers.pairWithPin
   when `PinRequired`; `.btn.btn--ghost` receivers.forgetPairing when `Paired`. Disabled rules as today (offline,
   `Disabled` pairing). The Connect button's `title` is receivers.connectTitle when enabled and
   receivers.offlineTitle when it is disabled because the receiver is offline.
7. `<details class="details">` with `<summary>` receivers.details: the offset form (label receivers.offset, number
   input `.input` 7.5rem, `.btn.btn--sm` receivers.apply, hint receivers.offsetHint "{{ms}} ms", the two error texts
   receivers.offsetInvalid / receivers.offsetRange "{{max}}"), and a `.facts` list of id, manufacturer, port,
   features/flags hex (receivers.facts.*).

Empty states: not loaded → `.meta` common.connecting; no receivers → `.empty` receivers.empty + receivers.emptyHint.

### 4.5 Groups (`Pages/Groups.razor` at `/groups`, `Pages/GroupEditor.razor` at `/groups/new` and `/groups/{id}`)

`/groups`: `<h1>` groups.title; `.meta` groups.lead (the G / r_i explanation); `.btn.btn--primary` groups.newGroup;
a `.card` list of `.row`s: name, `.meta` member names, pill, `.btn.btn--sm` groups.edit. Empty → `.empty` groups.empty.
A back link (`.btn.btn--ghost` with a ← glyph, nav.backToPlayer) at the top.

`/groups/new`: `<h1>` groups.create; one `.card` with the name field (groups.name, placeholder groups.namePlaceholder,
maxlength 64) and `.btn.btn--primary` groups.createAction; on success navigate to `/groups/{id}`.

`/groups/{id}` (group editor), `<h1>` = the group name, back link to `/groups` (nav.backToGroups); page title
nav.groups for all three routes (4.1):

- `.card` **Name**: inline rename (`.input` with `aria-label` = groups.name, the key reused for today's "Group name";
  `.btn.btn--primary` groups.save, `.btn` groups.cancel) behind a `.btn.btn--ghost.btn--sm` groups.rename; delete at the bottom of the page in a `.card.card--danger` (border
  `color-mix(in srgb, var(--err) 40%, var(--line))`): groups.deleteHint, `.btn.btn--danger` groups.delete → inline
  confirm groups.deleteConfirm "{{name}}" with `.btn.btn--danger` groups.deleteYes and `.btn` groups.keep; after
  deleting navigate to `/groups`.
- `.card` **Output**: pill (active/ready/…) and `.btn.btn--primary` groups.activate (disabled without members,
  `title` groups.activateHint; enabled: `title` groups.activateTitle) or `.btn` groups.stopOutput when active.
- `.card` **Master**: `.label` groups.master "Master (G)", `LevelSlider Variant="Hero"` (`Label` = groups.master
  hidden, `ValueText` common.levelText) + `.big`; clamp hint groups.clampHint under it while previewing (existing
  behaviour).
- `.card` **Members** (groups.members): one `.row` per member: name, pill, `LevelSlider Variant="Compact"`
  (`Label` = groups.memberLevel "{{name}}" hidden, `ValueText` common.levelText, disabled while the master is
  dragged, values from the preview; these rows are never `is-quiet`),
  `.btn.btn--icon` with an × (`aria-label` groups.remove "{{name}}"). Below: the add form (`<select class="input">`
  groups.choose with the candidates, offline ones suffixed groups.offlineSuffix, `.btn` groups.add); groups.noMembers
  when empty.
- Unknown id → `.empty` groups.notFound with the back link.

All group commands and the busy/preview logic of today's `Groups.razor` move over unchanged.

### 4.6 Settings (`Pages/Settings.razor`, route `/settings`)

`<h1>` settings.title; single column, `max-width: 44rem`. Cards in this order:

1. **Bridge** (`EditForm`, `.card`): fields settings.deviceName (hint settings.deviceNameHint), settings.latency
   (number input, step 441; hint settings.latencyHint with `{{ms}}`, `{{min}}`, `{{max}}` and settings.latencyHint2),
   settings.stream (`<select>`: settings.streamRealtime / settings.streamBuffered; hint settings.streamHint),
   settings.poll (checkbox row: `.field--check { display: grid; grid-template-columns: auto 1fr }`, hint
   settings.pollHint). Validation messages from the catalog (`SettingsModel` keeps the attributes; the page maps the
   attribute's `ErrorMessage` names — set them to the catalog keys `settings.validation.*` and render
   `I18n[message]`). Actions: `.btn.btn--primary` settings.save / settings.saving, `.btn.btn--ghost` settings.reset.
   Load failure: `.meta` settings.loadFailed + `.btn` common.retry.
2. **Appearance** (`.card`): settings.appearance; a `<fieldset>` with `<legend>` settings.theme (visible, styled like
   `.field > label`) and three `.field--check` rows of native radio inputs (theme.light, theme.dark, theme.system,
   each with `<label for>`, 44px rows) bound to `BrowserBridge`; `.field` settings.language = `<label
   for="settings-language">` + `<LanguageSelect Variant="Field" Id="settings-language" />` (3.2: `.input`, full width,
   44px, 16px font on phones).
3. **Spotify** (`.card`): settings.spotify; status line (settings.spotifyConnected "{{user}}" when `SpotifyUser` is
   set, settings.spotifyConnectedNoUser when connected without a user name, settings.spotifyNotConnected otherwise);
   `.btn.btn--ghost` settings.forgetSpotify → inline confirm settings.forgetConfirm, `.btn.btn--danger` settings.forget,
   `.btn` common.keep.
4. **Diagnostics** link card (`.card` with a single `.row`: settings.diagnosticsLink, `.meta` settings.diagnosticsHint,
   chevron) → `/settings/diagnostics`.

### 4.7 Diagnostics (`Pages/Diagnostics.razor`, route `/settings/diagnostics`)

Back link to `/settings` (nav.backToSettings), `<h1>` diagnostics.title, page title nav.diagnostics. Grid
`repeat(auto-fill, minmax(320px, 1fr))` of three cards and the full-width log card; every card is a `.facts` list
(`dt` = label, `dd` = value) and the values are today's values, formatted through the culture:

1. **Status** (`<h2>` diagnostics.status), from `StatusDto`:
   diagnostics.version → `Version`; diagnostics.runningSince → `Format.DateTime(StartedAt)`; diagnostics.spotify →
   diagnostics.spotifyConnectedAs "{{user}}" / diagnostics.connected (connected, no user) / diagnostics.notConnected;
   diagnostics.deviceName → `DeviceName`; diagnostics.output → the active group's name or the output receiver's name
   from `BridgeState`, common.none when there is none; diagnostics.receiversOnNetwork → `Format.Integer(Receivers)`;
   diagnostics.liveUpdates → `Plural("diagnostics.liveUpdatesValue", connections, ("state", word), ("events",
   Format.Integer(events)))` where `word` is status.live / status.connecting / status.paused / status.offline chosen
   by the mapping of section 7 (today's `Connection.ToString().ToLowerInvariant()` goes away). Note the key:
   diagnostics.liveUpdates is the label, diagnostics.liveUpdatesValue_one/_other the plural value.
2. **Network and statistics** (`<h2>` diagnostics.network), from `DiagnosticsDto`: diagnostics.timingPort /
   diagnostics.controlPort → the port or diagnostics.notBound when 0; diagnostics.packetsSent →
   `Format.Integer(PacketsSent)`; diagnostics.retransmits → `Format.Integer(RetransmitRequests)`;
   diagnostics.lateWakeups → `Format.Integer(LateWakeups)`; **diagnostics.retransmitRatio** → `Format("diagnostics.
   retransmitRatioValue", ("percent", Format.Percent(RetransmitRequests / (double)PacketsSent, 1)), ("late",
   Format.Integer(LateWakeups)))` ("0.4 % · 0 late", the mockups' Status-card fact, here as a line of this card),
   or `–` (U+2013) while `PacketsSent == 0`; diagnostics.localAddresses → the list in `--font-mono`, the first five
   and `Plural("diagnostics.more", rest)` as today, diagnostics.noneReported when empty. The "Updated" line
   diagnostics.updated "{{time}}" with `Format.Time`; load failure diagnostics.unavailable "{{detail}}" + common.retry.
3. **Pending probes** (`<h2>` diagnostics.pendingProbes): the `OpenQuestionsPending` names as `.meta` lines (service
   text, 3.5); empty → `.meta` diagnostics.noneReported (replaces today's "None.").
4. **Live log** (`<h2>` diagnostics.log): controls `.field` inline (label diagnostics.level, `<select class="input">`
   with diagnostics.levels.*), `.btn.btn--ghost.btn--sm` diagnostics.pause / diagnostics.resume, diagnostics.clear;
   lines in `.log` with the level classes of 1.5; diagnostics.noLines when empty.

### 4.8 Pills for receivers and groups (`Components/StatePill.razor`)

```
<StatePill Receiver="receiver" />   /   <StatePill Group="group" />
```

Receiver, first match: `!Online` → pills.offline (`.pill`); `State == Streaming` → pills.playing (`.pill--ok`);
`Connected` → pills.connected (`.pill--ok`); `Connecting` → pills.connecting (`.pill--accent`); `Error` →
pills.error (`.pill--err`, `title` = the error); `Pairing == PinRequired` → pills.pinNeeded (`.pill--warn`);
`Pairing == Disabled` → pills.homekitOnly (`.pill`); otherwise pills.online (`.pill--ok`).

Group: `Active` and any member `Streaming` → pills.playing (`.pill--ok`); `Active` → pills.active (`.pill--accent`);
no members → pills.empty (`.pill`); any member's receiver `PinRequired` and not `Paired` → pills.pinNeeded
(`.pill--warn`); no member online → pills.offline (`.pill`); otherwise pills.ready (`.pill--ok`). Every pill has
`title` = pills.*.title (the longer explanation).

### 4.9 Notices → toasts (`Components/NoticeArea.razor`)

A fixed `aside.toasts` (`aria-live="polite"`): desktop/tablet `top: calc(var(--topbar-h) + .75rem); right: 1.25rem;
width: min(26rem, calc(100vw - 2rem))`; phone `left: .75rem; right: .75rem; bottom: calc(var(--tabbar-h) +
env(safe-area-inset-bottom) + .75rem); top: auto`. Each `.toast` = `.card` padding `.75rem 2.6rem .75rem 1rem`, with a
kind class `.toast--ok` / `.toast--err` / `.toast--info` that sets border and background (`--ok-fill`/`--err-fill`/
`--info-fill`, border `color-mix(in srgb, var(--ok|err|accent) 40%, var(--line-strong))`), `box-shadow:
var(--shadow-lift)`, `role="alert"` for errors, `role="status"` otherwise, a `.toast__close` icon button
(`aria-label` common.dismiss, 44px on touch). The old `.notice`, `.notice-error` and `.dismiss` classes go away;
`ShellTests` (the moved `Notices_show_api_errors_and_can_be_dismissed` test) selects `.toast--err` and `.toast__close`. Success/info auto-dismiss after 6 s (unchanged
`AutoDismiss`), errors stay. Newest at the bottom (desktop) / top (phone). Max 5 (unchanged).

Catalogued notice texts (BridgeCommands uses `I18n`): notices.failed "{{what}} failed. {{detail}}" with `what` from
actions.* (actions.connect "{{name}}", …), notices.settingsSaved, notices.paired "{{name}}", notices.spotifyForgotten,
notices.connectFailed "{{name}}: {{error}}". `PlaybackAsync(string command)` maps `"play"`/`"pause"`/`"next"`/`"prev"`
to actions.playbackPlay/Pause/Next/Previous (a `switch`; any other value throws `ArgumentOutOfRangeException`,
the four transport buttons are the only callers), so no raw command word is interpolated into a sentence.

### 4.10 Connection banner (`Components/ConnectionBanner.razor`)

A full-width strip directly under the top bar (`position: sticky; top: var(--topbar-h); z-index: 19`; `top: 0` at
≤ 699px where the top bar is not sticky, 5.2), `role="status"`,
font `.85rem`, padding `.45rem 1.25rem`, `display: flex; gap: .6rem; align-items: center`, hidden while `Connected`:
- `Connecting` → background `--info-fill`, border-bottom `color-mix(in srgb, var(--accent) 40%, var(--line))`, text
  banner.connecting.
- `Disconnected` with `NextAttemptAt` → `--warn-fill`, text banner.paused ("{{error}}" appended as banner.pausedError
  when there is one) + banner.retrying.
- `Disconnected` without a retry → `--err-fill`, text banner.offline + `.btn.btn--sm` common.reload (`location.reload`
  through `NavigationManager.NavigateTo(uri, forceLoad: true)`).

### 4.11 Pairing dialog (`Components/PairingDialog.razor`)

A modal: `.scrim` (`position: fixed; inset: 0; background: rgba(9,12,18,.45); z-index: 60; display: grid;
place-items: center; padding: 1rem`) and `.dialog` (`.card` + `box-shadow: var(--shadow-lift); width: min(26rem, 100%);
max-height: calc(100dvh - 2rem); overflow: auto`), `role="dialog" aria-modal="true" aria-labelledby`. On phones
(≤ 699px) the dialog is a **top-anchored sheet, not a bottom sheet**: `.scrim { align-items: start; padding:
max(.75rem, env(safe-area-inset-top)) .75rem .75rem }` and `.dialog { width: 100% }`. Reason: the on-screen keyboard
opens with the focused PIN field, and on iOS a bottom-anchored `position: fixed` element stays laid out against the
layout viewport and ends up under the keyboard; a sheet at the top with the PIN field in its upper half stays visible
on every platform without a `visualViewport` listener. In addition the viewport meta carries
`interactive-widget=resizes-content` (5.2) so Chrome/Android shrink the layout viewport with the keyboard. Headless
screenshots cannot show the keyboard; review-2 lists the real-device check (WORK-PLAN).

Content: `<h2>` pairing.title "{{name}}"; `<p>` pairing.prompt (Service origin) or `prompt.Message` (User origin,
muted); the PIN field: `<label for="pairing-pin" class="label">` pairing.pin, `<input id="pairing-pin" class="input
pin">` (`inputmode="numeric"`, `autocomplete="one-time-code"`, `maxlength="8"`, `placeholder="0000"`,
`font-size: 1.6rem; letter-spacing: .4em; text-align: center; font-variant-numeric: tabular-nums`), error line
(`role="alert"`, `--err-ink`): pairing.rejected / pairing.expired / pairing.failedDetail "{{detail}}" when
`PinResult.Message` is set, pairing.failed (no placeholder) when it is null / pairing.invalid; actions right-aligned:
`.btn.btn--primary` pairing.pair or pairing.startAgain, `.btn` common.cancel.

Focus: when the dialog opens (`OnAfterRenderAsync`, token changed) the component calls
`BrowserBridge.RememberActiveAsync()` and then `ElementReference.FocusAsync()` on the PIN input; on close it calls
`restore` on the remembered handle. Escape = cancel (`@onkeydown` on the dialog). Tab trap: the dialog's `@onkeydown`
handler sees `Tab` on the last focusable element (the cancel button) and `Shift+Tab` on the first (the PIN input)
and calls `FocusAsync` on the opposite end (`preventDefault` via `@onkeydown:preventDefault` only for those two
cases, so other Tabs stay native). `GroupsPairingTests` asserts the trap: render with a pairing prompt, `KeyDown(Tab)`
on the cancel button → the JSInterop mock records a `focus` call for the PIN input's element (bUnit's
`ElementReference.FocusAsync` goes through `JSInterop` and is set up with `SetupVoid("Blazor._internal.domWrapper.focus", _ => true)`);
the same for `Shift+Tab` on the PIN input. "Focus returns on close" is asserted as the `restore` call on the mocked
`rememberActive` handle.

---

## 5. Mobile, tablet, desktop

### 5.1 Breakpoints

| Name | Width | Shell | Content |
|---|---|---|---|
| phone | ≤ 699px | top bar: brand + theme + status (tabs and language hidden); **bottom tab bar** | one column, full-width cards, hero first, phone hero layout (centred artwork 120px) |
| tablet | 700–1023px | tabs in the top bar (flow, not absolute), language select visible | one column, full-width cards; receivers/diagnostics grids auto-fill (two cards side by side from 700px if ≥ 320px each) |
| desktop | ≥ 1024px | tabs absolutely centred | Player two columns; receivers grid; settings 44rem |
| wide | ≥ 1280px | same | `main` max-width 1440px, three receiver cards per row |

Two width queries decide the shell: `@media (max-width: 699px)` and `@media (min-width: 1024px)`. The complete list
of media queries `app.css` may contain (`DesignTokenTests` greps for it; anything else is a finding):

| Query | Used for |
|---|---|
| `(max-width: 699px)` | the phone shell and every phone override of 5.2/5.3, `.only-wide` |
| `(min-width: 700px)` | `.only-phone { display: none }` |
| `(min-width: 1024px)` | absolutely centred tabs, the Player's two columns, the three-column Status facts |
| `(max-width: 359px)` | hides `.brand__name` |
| `(max-height: 500px)` | landscape phones: hero artwork 96px, hero padding `.8rem` |
| `(max-width: 699px), (pointer: coarse)` | the touch block of 5.4 (the width half makes it reproducible in headless Edge, which reports `pointer: coarse` as false, 5.6) |
| `(hover: hover)` | every `:hover` rule is wrapped in it so touch devices do not get sticky hover states |
| `(prefers-reduced-motion: reduce)` | 1.3 |
| `(prefers-color-scheme: dark)` | 1.2 |

700–1023px gets the Status card's two columns through plain `repeat(auto-fit, minmax(10rem, 1fr))`, not a query;
three receiver cards per row at ≥ 1280px come from the `auto-fill` grid of 4.4 by themselves. Landscape phones
(844×390) get the phone shell (width rules) with the tab bar.

### 5.2 Phone shell (≤ 699px)

- `.topbar { position: static; padding: 10px 14px; gap: 8px; backdrop-filter: none; -webkit-backdrop-filter: none;
  background: var(--surface) }` (the phone mockup: the bar scrolls away with the page, which also saves height in
  landscape; the bottom tab bar is the fixed navigation); `.tabs { display: none }`; `.lang-select { display: none }`;
  `.brand__tile` 32px, glyph 20px; `.brand__name { font-size: 1rem }`, hidden below 360px (`@media (max-width: 359px)`).
  Because the bar is not sticky, the connection banner (4.10) is `top: 0` at ≤ 699px.
- The theme picker buttons are 34px (`.ico` 18px) with `border: 1px solid var(--line-strong); background:
  var(--surface)` in the collapsed state (the mockup's single round button); unfolding works as on desktop (first
  tap opens; the open pill is 3 × 34px + gaps ≈ 110px wide). No halo, not 44px: see 2.4.
- The status pill keeps its text (`.74rem`), `min-width: 0; overflow: hidden; text-overflow: ellipsis`.
- `nav.tabbar` (`Components/TabBar.razor`): `position: fixed; left: 0; right: 0; bottom: 0; z-index: 20; display: flex;
  border-top: 1px solid var(--line); background: color-mix(in srgb, var(--surface) 94%, transparent); backdrop-filter:
  blur(8px); padding: 6px 8px calc(6px + env(safe-area-inset-bottom))`; three `a` items `flex: 1; display: flex;
  flex-direction: column; align-items: center; gap: 2px; min-height: 44px; font-size: .68rem; font-weight: 600;
  color: var(--muted)`, `.active { color: var(--accent-ink) }`, icons: play triangle / the AirPlay arcs / gear (the
  phone mockup's SVGs), `aria-label` nav.sections on the nav, `aria-current="page"` on the active link.
- `index.html` viewport: `width=device-width, initial-scale=1, viewport-fit=cover, interactive-widget=resizes-content`
  (the last token makes Chrome/Android shrink the layout viewport when the keyboard opens, so fixed elements move
  above it; browsers that do not know it ignore it); the tab bar and the toasts pad with
  `env(safe-area-inset-bottom)`, the top bar with `env(safe-area-inset-top)` (`padding-top: calc(10px +
  env(safe-area-inset-top))`).
- The shell publishes `--topbar-h` and `--tabbar-h` from the measured elements (`BrowserBridge.MeasureAsync`) so the
  banner, toasts and `main` padding follow text-size changes.
- `html, body { overflow-x: hidden }` is **not** used to hide overflow; the audit (5.6) must pass without it.
  Every flex child that holds text has `min-width: 0`; grids use `minmax(0, 1fr)`.

### 5.3 Player page on phones (order and sizes, from the phone mockup)

The phone keeps every desktop component value of 1.5–1.7 **except** the overrides below, all under
`@media (max-width: 699px)` (the complete list; the phone mockup's remaining values equal the desktop ones):

| Selector | Phone value |
|---|---|
| `.card` | `padding: 1rem; gap: .8rem` |
| `.row` | `gap: .6rem; padding: .6rem .2rem` (`.row.is-active` keeps its `.7rem` side padding) |
| `.meta` | `font-size: .78rem` |
| `.label` | `font-size: .66rem` |
| `.chip` | `font-size: .76rem; padding: .28rem .6rem` |
| `.brand__name`, `.topbar`, `.brand__tile`, `.theme-btn`, `.status` | as in 5.2 |
| `.hero` | `align-items: center; text-align: center; gap: .6rem`; artwork `.art` 120px (96px under `(max-height: 500px)`); `.hero__title` 1.2rem, max 2 lines; artist as `.meta`; the chip row centred |
| progress row | `.track` 5px, times at both ends (`width: 100%`) |
| transport | `.step.step--lg` 44px, `.play` 56px |
| master row | `.step` 40px, hero slider, `.big` 1.3rem; row 3 replaced by `.chip--members` (4.3) |
| Output card | `padding: .8rem 1rem`; rows: radio, name + `.meta.only-phone`, pill (compact slider hidden); head actions per 4.3 (player.editGroups link, Stop output only while selected) |
| Status card | one column of facts |
| `main` | `padding: 1rem .875rem calc(1rem + var(--tabbar-h) + env(safe-area-inset-bottom))` |
| `.cards` (Receivers), Diagnostics grid | one column (`grid-template-columns: 1fr`) |
| `.level--compact` in Output rows | `display: none` |
| `.toasts`, `.scrim`/`.dialog`, `.banner` | the phone positions of 4.9, 4.11, 4.10/5.2 |

Order on `/`: hero card, Output card, Status card (single column).

### 5.4 Touch targets (`@media (max-width: 699px), (pointer: coarse)`)

- `.btn`, `.step`, `.toast__close`, `.tabbar a`, `.btn--icon`, `.input`, `select.input`, the Appearance radios'
  `.field--check` rows: `min-height: 44px` (`.btn--sm` keeps its 30px box and grows a centred `::before` hit area of
  44×44 — it has no `overflow: hidden` — so rows do not get taller). **Not grown**: `.theme-btn` (34px, 2.4) and the
  top-bar `.lang-select` (hidden on phones anyway, 5.2; 32px on tablets with a coarse pointer, allow-listed in 5.6
  like the reference apps).
- Range inputs: `height: 44px` (the track stays 6/10px); the thumb is **24px on every variant** (the hero's 24px
  is unchanged; the mockup's 22px phone knob is rounded up to 24px; there is no 28px thumb); the whole 44px band
  is draggable because the input itself is 44px tall.
- `select`, `input`: `font-size: 16px` (iOS zoom guard).
- Row buttons sit at least 12px apart (`gap`).
- Headless Edge never matches `(pointer: coarse)` (5.6), so the width half of the query is what screenshots and the
  audit exercise at 390px; the `(pointer: coarse)` half only adds the same rules for coarse-pointer tablets and
  desktops wider than 699px and is covered by no automated check (review-2 runs an emulated-touch pass, WORK-PLAN).

### 5.5 Tablet (700–1023px)

Single column with full-width cards; the Player page stacks hero → Output → Status; the Receivers grid shows two
cards per row from 700px (`minmax(320px, 1fr)` yields two at ≥ 680px of content width). Top bar as desktop (tabs in
the flow between brand and tools; `gap: 12px`; the language select shows the native name).

### 5.6 Layout audit page (`/_audit`, used by `tools/web-audit.ps1`)

`Pages/Audit.razor` takes `?route=/receivers` (default `/`), renders that page's component inside the normal layout
(a `DynamicComponent` of the page type resolved from the route table of `App`), and calls `app.js` `audit()` once
the page has data: **trigger = `State.Loaded` is true and the routed component has rendered at least once after
that, plus a 500 ms settle timer** (the pages fill from REST and then the WebSocket; "after the second render" would
measure an empty page). Until then `<pre id="audit-result">` is empty; `tools/web-audit.ps1` retries the dump (with
a larger `--virtual-time-budget`, 2 s steps up to 30 s) until the `<pre>` is non-empty and fails after that.

`audit()` returns, written as JSON into `<pre id="audit-result">`:

- `overflow`: `document.documentElement.scrollWidth - window.innerWidth` (must be 0);
- `small`: selectors of interactive elements (`a[href]`, `button`, `input`, `select`, `textarea`, `[role=radio]`,
  `[role=button]`, `summary`) whose `getBoundingClientRect()` is < 44 × 44 CSS px, computed **unconditionally when
  `innerWidth <= 699`** or when `(pointer: coarse)` matches (headless Edge reports `(pointer: coarse)` = false and
  `(hover: hover)` = true at every `--window-size`, verified with a `matchMedia` probe at 390×844, so a coarse-only
  rule would never run). Visually-hidden inputs (the output radios, the hidden labels' inputs) are skipped when
  their wrapping `label` is itself ≥ 44 × 44. Elements with `display: none` or zero size are skipped. The script
  prints `small` and fails only on entries outside the allow-list `.theme-btn`, `.lang-select`;
- `unnamed`: selectors of `a[href]`, `button`, `input` (not `type=hidden`), `select`, `textarea`, `[role=radio]`,
  `[role=button]`, `[role=dialog]` without an accessible name. The name is computed with this rule set, in order,
  and the element is unnamed only when every step yields an empty string after trimming: (1) `aria-labelledby`
  → the concatenated `textContent` of the referenced ids; (2) `aria-label`; (3) for `input`/`select`/`textarea`:
  a `<label for=id>` or the closest wrapping `<label>` → its text content excluding the control itself; (4) for
  `button`/`a`/`summary`/`[role=button]`/`[role=radio]`: the element's `textContent`, which includes visually-hidden
  spans and the text of any descendant SVG `<title>`; (5) `alt` of a descendant `img`; (6) `title`; (7) for `input`
  of type `button`/`submit`: `value`; (8) `placeholder` alone does **not** count (it is a hint, not a name).
  `aria-hidden="true"` elements and their descendants are skipped entirely. This is a subset of the accessible-name
  computation, chosen so that an icon-only button with `aria-label` is named, an icon-only button with only an SVG
  without `<title>` is unnamed, and a range input with a visually-hidden `<label for>` is named.

The script dumps the DOM with headless Edge (`--dump-dom`) and fails when `overflow > 0`, `unnamed` is non-empty or
`small` has entries outside the allow-list. Touch behaviour itself (the first-tap-opens picker, the 44px slider
band) cannot be seen here; it is covered by bUnit with `hoverCapable` mocked to `false` and by review-2's emulated
run. Development data comes from the fake receivers.

---

## 6. Accessibility

### 6.1 Structure

- Real `<button type="button">` for actions, `<a>` for navigation, `<input type="range">` for levels, native radios
  inside labels for the output list and the Appearance theme choice, `<select>` for language and log level,
  `<fieldset>`/`<legend>` around radio sets (the legend names the group; never an additional `role="radiogroup"` or
  `aria-label` on the fieldset; the Output list's legend is visually hidden, the Appearance card's is visible),
  `<label for>` on every input (the `Field` variant of `LanguageSelect` included), `<dialog>`-like
  modal with `aria-modal`, one `<h1>` per page, `<h2>` per card, `aria-label` on `nav`s and on every icon-only button
  (previous/next/play/pause/quieter/louder/close/remove/theme buttons), `aria-current="page"` on the active tab,
  `role="status"` for the status pill, the banner and non-error toasts, `role="alert"` for error toasts and inline
  errors, `aria-live="polite"` on the toast region, `aria-valuetext` on sliders and the progress bar.
- `FocusOnNavigate` (`App.razor`) keeps moving focus to the `<h1>` after navigation; the Player's hidden `<h1>` is
  focusable (`tabindex="-1"`).

### 6.2 Keyboard

- Theme picker: Tab reaches the checked option; arrows/Home/End move and select; Escape collapses (all tested in
  `ShellTests` with bUnit `KeyDown`). Language select: native. Tabs: Tab/Enter. Output list: Tab to the group, arrows
  move the native radio (selection follows = activation; this is the native radio behaviour and the mockup's
  semantics; `PlayerTests` tests the `change` of a radio, not the arrow keys). Sliders: arrows ±1, PageUp/Down ±10
  (`step` 1; the browser's PageUp/Down is 10% of the range), Home/End 0/100 — **native range behaviour, not
  tested** (bUnit has no browser); only the `input` event → coalescing sender path is tested. Steppers:
  Enter/Space (native button). Dialog: Tab trap and focus return tested as described in 4.11; Escape cancels
  (tested); Enter submits the form (native, not tested).
- Every control shows the focus ring of 1.3; no `outline: none` without an alternative. Focus is invisible in
  headless screenshots; review-2 checks the CSS rules and the styleguide's focus-state sample (a `.btn` rendered
  with a `.is-focus-sample` class that applies the ring statically).

### 6.3 Contrast (WCAG 2.1 AA, text ≥ 4.5:1, UI components ≥ 3:1), computed for the tokens of 1.1

| Pair | Light | Dark |
|---|---|---|
| `--text` on `--bg` / `--surface` / `--surface-2` | 13.95 / 15.79 / 14.33 | 16.02 / 14.50 / 15.64 |
| `--muted` on `--bg` / `--surface` / `--surface-2` | 4.79 / 5.43 / 4.92 | 7.09 / 6.42 / 6.93 |
| `--muted` on `--accent-soft` (over surface) | 4.66 | 5.11 |
| `--accent-ink` on `--bg` / `--surface` / `--surface-2` / `--accent-soft` | 5.11 / 5.78 / 5.25 / 4.96 | 7.74 / 7.01 / 7.56 / 5.58 |
| `--accent-contrast` on `--accent` (buttons, active tab) | 4.53 | 5.91 |
| `--ok-ink` on `--surface` / `--surface-2` / `--bg` / `--ok-fill` | 5.39 / 4.89 / 4.76 / 4.77 | 6.74 / 7.28 / 7.45 / 5.57 |
| `--warn-ink` on `--surface` / `--surface-2` / `--bg` / `--warn-fill` | 5.91 / 5.36 / 5.22 / 5.31 | 7.46 / 8.05 / 8.24 / 6.09 |
| `--err-ink` on `--surface` / `--surface-2` / `--bg` / `--err-fill` | 6.57 / 5.96 / 5.81 / 5.65 | 6.15 / 6.63 / 6.79 / 5.42 |
| `--accent-ink` on `--info-fill` | 5.07 | 5.92 |
| white on `--err-ink` (light danger button) / `--accent-contrast` on `--err` (dark) | 6.57 | 5.65 |
| `--accent` on `--surface` (slider fill, radio dot; non-text, floor 3.0) | 4.53 | 5.35 |
| `--accent` on `--surface-2` (slider fill on its track; non-text, floor 3.0) | 4.11 | 5.78 |
| `--accent` on `--bg` (focus ring; non-text, floor 3.0) | 4.00 | 6.36 |
| `--accent` on `--accent-soft` over `--surface-2` (`.theme-btn.is-active` icon; non-text, floor 3.0) | 3.8 | 4.3 |
| `#fff` on `--accent` (brand glyph, placeholder note; non-text, floor 3.0) | 4.53 | 3.2 |
| `--text` on `--ok-fill` / `--warn-fill` / `--err-fill` / `--info-fill` (toast body) | ≥ 12 | 11.8–12.8 |
| `--muted` on the four fills (toast secondary text) | ≥ 4.5 | 5.24–5.66 |
| `--line-strong` on `--surface` (control borders, decorative; **report only, no floor**) | 1.42 | 1.51 |

Raw `--accent` as text on light surfaces is 4.0–4.5 and raw `--ok`/`--warn` 3.2–4.1: that is why text uses the ink
tokens. The mockups still colour text with the raw tokens (`.pill--online { color: var(--ok) }`,
`.chip--accent { color: var(--accent) }`, `.big`, `a { color: var(--accent) }`); the spec overrides each with the ink
token, and `DesignTokenTests` enforces it by grepping `app.css`: a declaration `color: var(--ok)`, `color:
var(--warn)`, `color: var(--err)` or `color: var(--accent)` (the raw tokens as `color`) is a failure unless its
selector is on the allow-list `.theme-btn.is-active` (icon glyph). `background`, `border-color`, `box-shadow`,
`outline` and `fill`/`stroke` may use the raw tokens.

`DesignTokenTests` recomputes every row above from the tokens in `app.css` with **minimum semantics**: it asserts
`ratio >= 4.5` for text rows and `ratio >= 3.0` for the rows marked non-text; the numbers in the table are
informative, not asserted to equality (independent recomputations differ by ±0.02 from rounding). Translucent
tokens (`--accent-soft`) are composited over the named backdrop per channel in sRGB 0–255,
`c = round(a · fg + (1 − a) · bg)`, before the WCAG relative-luminance formula; the composited backdrop for
"`--accent-soft` over `--surface`" is `--surface`, for the theme button `--surface-2`. The `--line-strong` row is
printed in the test output and has no floor (it is decorative, 1.1).

### 6.4 Motion

The live dot pulses (`@keyframes pulse`, 1.8 s, section 7: adapted from Daikin's keyframes, which are hard-coded to
`--ok`; here they use `currentColor` and are applied to `.status--live::before` only); the theme picker animates
width/opacity 0.15 s; cards do not lift on hover (they are not clickable). `prefers-reduced-motion: reduce` turns
the animation and the transitions off (1.3) and the static halo of the base rule shows for every state.

---

## 7. Status pill semantics (`Components/StatePill` is for receivers; this is `Components/StatusPill.razor`)

Mapped from `BridgeState.Connection`, `ConnectionError`, `NextAttemptAt`:

| State | Class | Text (de / en) | Colour | Dot |
|---|---|---|---|---|
| `Connecting` | `status status--connecting` | status.connecting "verbinde…" / "connecting…" | `--accent-ink`, border `color-mix(in srgb, var(--accent) 45%, var(--line-strong))` | static |
| `Connected` | `status status--live` | status.live "live" / "live" | `--ok-ink`, border mix with `--ok` | pulsing: `.status--live::before { animation: pulse 1.8s ease-out infinite }`, `@keyframes pulse { 0% { box-shadow: 0 0 0 0 color-mix(in srgb, currentColor 70%, transparent) } 70% { box-shadow: 0 0 0 6px transparent } 100% { box-shadow: 0 0 0 0 transparent } }`; while it runs the animated `box-shadow` replaces the base rule's static halo for this state only, the other three states keep the static halo |
| `Disconnected`, `NextAttemptAt != null` | `status status--paused` | status.paused "pausiert" / "paused" | `--warn-ink`, border mix with `--warn` | static |
| `Disconnected`, `NextAttemptAt == null` | `status status--error` | status.offline "offline" / "offline" | `--err-ink`, border mix with `--err` | static |

`title` = status.title.* (the longer sentence, with `{{error}}` for paused/offline). `role="status"` so transitions
are announced. Base rule: `.status { display: inline-flex; align-items: center; gap: .4rem; font-size: .78rem;
font-weight: 600; padding: .3rem .7rem; border-radius: var(--radius-pill); border: 1px solid var(--line-strong);
background: var(--surface); color: var(--muted) } .status::before { content: ""; width: 8px; height: 8px;
border-radius: 50%; background: currentColor; box-shadow: 0 0 0 3px color-mix(in srgb, currentColor 25%, transparent) }`.

`LiveIndicator.razor` is deleted; `StatusPill` replaces it.

---

## 8. What stays

Unchanged: `BridgeState.cs`, `BridgeEventClient.cs`, `BridgeApiClient.cs`, `CoalescingSender.cs`,
`GroupVolumePreview.cs`, `src/Bridge.Contracts/*` (no additive DTO field is needed: every value the pages show is in
the existing DTOs), the service (`src/Bridge.Service`), the routes in `ApiRoutes`, the WebSocket protocol.

### 8.1 Drawn in the mockups or shown today, deliberately not built

| Item | Where | Decision |
|---|---|---|
| Status-card fact "Stream · Realtime · NTP · 1.75 s" | both desktop mockups | not built: needs `SettingsDto`, which the Player does not load; the Settings fields show the same values (4.3) |
| Status-card fact "Retransmits 0.4 % · 0 late" | both desktop mockups | moved to Diagnostics as diagnostics.retransmitRatio (4.7); not on the Player |
| `.track__knob` on the progress bar | desktop and phone mockups | not built: no seek API; the progress bar has no handle (4.3). `.track__knob` is not defined in `app.css` |
| Grey `.track__fill` on non-output rows | desktop mockup | kept as the `--fill-color` of the compact range input (`.row.is-quiet`, 1.6) |
| Shortened member names in the phone members chip | phone mockup | not built: real names, wrapping chip (4.3) |
| Raw `--accent`/`--ok` as text colour | all mockups | replaced by the ink tokens (6.3) |
| Theme picker 44px touch target | — | not built: 34px, allow-listed (2.4) |
| Bottom-sheet pairing dialog on phones | 4.11 of the first draft | replaced by a top-anchored sheet (4.11) |
| Spotify volume `<meter>` | today's NowPlaying page | dropped (4.3); `PlaybackDto.SpotifyVolume` is unused by the web app |
| `/now-playing`, `/diagnostics` routes | today's pages | dropped in wave 0 (4.1), no redirects |
| Static index.html texts ("Loading the AirPlay Bridge app…", "An unhandled error has occurred.", "Reload", "x") | `wwwroot/index.html` | stay English, static HTML: they render before the runtime and the catalog exist. There are no catalog keys for them (app.loading/app.error do not exist) |
| `Format.Seconds`, units.* keys | first draft | removed, no consumer (3.4) |

Changed in a contained way:
- `BridgeCommands.cs`: takes `I18n` (constructor parameter; `Program.AddBridgeClient` registers it) and composes its
  notice texts from the catalog (4.9). Behaviour, return values and the `Commands_turn_api_errors_into_notices` test
  expectations (`Not found` stays in the detail) are unchanged.
- `Format.cs`: new helpers (3.4); existing ones stay.
- `SettingsModel.cs`: `ErrorMessage` values become catalog keys (`settings.validation.nameEmpty`, …) that the page
  translates.
- `Program.cs`: registers `I18n` and `BrowserBridge`, sets the culture before `RunAsync` (3.2).
- `BridgeComponentBase.cs`: also re-renders on `I18n.Changed`; exposes `[Inject] I18n L`.
- `Bridge.Web.csproj`: embedded catalogs, globalization (3.3). `Bridge.Tests.csproj`: `InvariantGlobalization=false`.
- `BridgeState.Apply(PairingRequired)` keeps building its English `Message`; the dialog ignores it for Service-origin
  prompts (3.5).

- `Components/StateBadge.razor` and `Components/LiveIndicator.razor` are **deleted**: `StatePill` (4.8) replaces the
  first (groups-and-pairing deletes the file once `Groups.razor`, its last user, is rewritten), `StatusPill` (7)
  the second (shell deletes it).

Existing tests keep passing where the behaviour is unchanged. The `foundation` package (wave 0) splits
`WebComponentTests.cs` into the per-page test files of section 9 without changing a test, so that every later
package owns the tests of its own pages. Selectors and texts that the restyle changes are then updated by the
package that changes the markup: `.badge` → `.pill`, `article.receiver h2`, `.level output`, `form.offset` inside
`details`; `.notice-error`/`.notice .dismiss` → `.toast--err`/`.toast__close` (shell, in `ShellTests`); and the
texts: `"Connected"` → `"connected"` (pills.connected), `"PIN pairing needed"` → `"PIN needed"` (pills.pinNeeded),
`"Available"` → `"online"` (pills.online), all in `ReceiversTests` by receivers-page. `"Start again"`
(pairing.startAgain) and `"Nothing selected"` (the start of player.noOutput) match the catalog unchanged. The
catalog values are not bent to the old tests.

---

## 9. File map (after all packages)

```
src/Bridge.Web/
  App.razor, _Imports.razor, Program.cs, Bridge.Web.csproj
  BrowserBridge.cs                      JS module wrapper (2.3)
  BridgeApiClient.cs, BridgeCommands.cs, BridgeEventClient.cs, BridgeState.cs, CoalescingSender.cs, Format.cs,
  GroupVolumePreview.cs, SettingsModel.cs
  Localization/I18n.cs, Localization/PluralRules.cs
  Resources/i18n/{de,en,fr,es,ru,uk,pt}.json
  Layout/MainLayout.razor
  Components/BridgeComponentBase.cs, ThemePicker.razor, LanguageSelect.razor, StatusPill.razor, TabLink.razor,
             TabBar.razor, ConnectionBanner.razor, NoticeArea.razor, PairingDialog.razor, LevelSlider.razor,
             StatePill.razor, NowPlayingHero.razor, MasterVolume.razor, OutputList.razor, StatusCard.razor
             (deleted: StateBadge.razor, LiveIndicator.razor)
  Pages/Player.razor, Receivers.razor, Groups.razor, GroupEditor.razor, Settings.razor, Diagnostics.razor,
        Styleguide.razor, Audit.razor            (deleted: NowPlaying.razor)
  wwwroot/index.html, wwwroot/css/app.css, wwwroot/js/app.js, wwwroot/favicon.svg
tests/Bridge.Tests/Web/
  BridgeStateTests.cs, WebApiClientTests.cs, WebLogicTests.cs, WebTestSupport.cs   (existing)
  ComponentTestBase.cs (the fixture of the old WebComponentTests: BunitContext, BridgeState, UseApi)
  DesignTokenTests.cs, I18nTests.cs, FormattingTests.cs, CatalogUsageTests.cs
  ShellTests.cs, PlayerTests.cs, ReceiversTests.cs, SettingsDiagnosticsTests.cs, GroupsPairingTests.cs
  (deleted: WebComponentTests.cs, split by foundation in wave 0)
tools/
  web-screenshots.ps1, web-audit.ps1, screenshot-setups/<package>.ps1   (one setup script per package)
docs/web/
  UI-SPEC.md (this), WORK-PLAN.md, mockups/, REVIEW-1.md, REVIEW-2.md (written by the review packages)
artifacts/screenshots/<package>/<route>-<w>x<h>-<theme>-<lang>.png   (git-ignored output)
```

`wwwroot/favicon.svg`: the brand tile (rounded square in the accent gradient with the white AirPlay glyph); linked
from index.html with `type="image/svg+xml"`.

---

## 10. Copy catalog (de = source, en)

Keys are the i18next paths of `Resources/i18n/de.json` / `en.json`. Placeholders are `{{name}}`. Plural keys list
their `_one`/`_other` forms (ru/uk add `_few`/`_many` in i18n-fill). Ellipses are U+2026, the minus is U+2212, the
middle dot is U+00B7.

**Every key has exactly one or more named consumers in sections 4–7**; `CatalogUsageTests` (WORK-PLAN 2.4) fails on
a key that no source file uses, and its allow-list is empty. Keys whose place is not obvious from their section:
app.name → `.brand__name` and the brand link's `aria-label` (4.2); nav.groups / nav.diagnostics → page titles (4.1);
common.none → the Diagnostics output fact (4.7); common.levelText → every `LevelSlider` caller (1.6);
receivers.connectTitle / receivers.offlineTitle → the Connect button's `title` (4.4); groups.activateTitle → the
Activate button's `title` when enabled (4.5); settings.spotifyConnectedNoUser → the Spotify card (4.6);
diagnostics.spotify … diagnostics.version → the Diagnostics Status card (4.7); styleguide.title / audit.title → the
`<h1>` and page title of the two developer pages; player.bridgeFallback → the device-name fallback (4.3);
player.noOutput → the Output card's no-output line (4.3). Removed from the first draft because nothing consumed
them: app.loading, app.error (index.html is static English, 8.1), common.close, common.save, common.working,
common.yes, common.no, units.*, player.title, player.levelText and groups.masterText (both → common.levelText),
actions.playback (→ the four actions.playback* keys).

### app, nav, theme, language, common

| Key | de | en |
|---|---|---|
| app.name | AirPlay Bridge | AirPlay Bridge |
| app.pageTitle | {{page}} · AirPlay Bridge | {{page}} · AirPlay Bridge |
| app.notFoundTitle | Nicht gefunden | Not found |
| app.notFound | Unter dieser Adresse gibt es nichts. | There is nothing at this address. |
| nav.sections | Bereiche | Sections |
| nav.player | Player | Player |
| nav.receivers | Empfänger | Receivers |
| nav.settings | Einstellungen | Settings |
| nav.groups | Gruppen | Groups |
| nav.diagnostics | Diagnose | Diagnostics |
| nav.backToPlayer | Zurück zum Player | Back to the player |
| nav.backToSettings | Zurück zu den Einstellungen | Back to settings |
| nav.backToGroups | Zurück zu den Gruppen | Back to the groups |
| theme.groupLabel | Design | Theme |
| theme.light | Hell | Light |
| theme.dark | Dunkel | Dark |
| theme.system | System (folgt dem Betriebssystem) | System (follows the operating system) |
| language.label | Sprache | Language |
| common.loading | Lade… | Loading… |
| common.connecting | Verbinde mit der Bridge… | Connecting to the bridge… |
| common.retry | Erneut versuchen | Retry |
| common.reload | Neu laden | Reload |
| common.cancel | Abbrechen | Cancel |
| common.dismiss | Ausblenden | Dismiss |
| common.keep | Behalten | Keep |
| common.none | Keine | None |
| common.levelText | {{value}} von 100 | {{value}} of 100 |

### status (the live pill, 7) and banner (4.10)

| Key | de | en |
|---|---|---|
| status.live | live | live |
| status.connecting | verbinde… | connecting… |
| status.paused | pausiert | paused |
| status.offline | offline | offline |
| status.title.live | Live-Updates sind aktiv | Live updates are on |
| status.title.connecting | Verbinde mit der Bridge für Live-Updates | Connecting to the bridge for live updates |
| status.title.paused | Live-Updates pausiert: {{error}} Es wird gleich erneut versucht. | Live updates paused: {{error}} Retrying shortly. |
| status.title.offline | Keine Verbindung zur Bridge: {{error}} | No connection to the bridge: {{error}} |
| banner.connecting | Verbinde mit der Bridge für Live-Updates… | Connecting to the bridge for live updates… |
| banner.paused | Live-Updates pausiert. | Live updates paused. |
| banner.pausedError | Live-Updates pausiert: {{error}} | Live updates paused: {{error}} |
| banner.retrying | Es wird gleich erneut versucht. | Retrying shortly. |
| banner.offline | Keine Verbindung zur Bridge. Die Seiten zeigen den letzten bekannten Stand. | No connection to the bridge. The pages show the last known state. |

### pills (4.8)

| Key | de | en |
|---|---|---|
| pills.playing | spielt | playing |
| pills.connected | verbunden | connected |
| pills.connecting | verbinde… | connecting… |
| pills.error | Fehler | error |
| pills.pinNeeded | PIN nötig | PIN needed |
| pills.homekitOnly | nur HomeKit | HomeKit only |
| pills.online | online | online |
| pills.offline | offline | offline |
| pills.active | aktiv | active |
| pills.ready | bereit | ready |
| pills.empty | leer | empty |
| pills.paired | gekoppelt | paired |
| pills.noPin | keine PIN nötig | no PIN needed |
| pills.title.playing | Audio wird gesendet. | Audio is being sent. |
| pills.title.connected | Sitzung aufgebaut; Audio geht hierhin, sobald Spotify spielt. | Session set up; audio goes here when Spotify plays. |
| pills.title.connecting | Kopplung und Sitzungsaufbau laufen. | Pairing and setting up the session. |
| pills.title.error | Der letzte Versuch ist fehlgeschlagen: {{error}} | The last attempt failed: {{error}} |
| pills.title.pinNeeded | Dieser Empfänger braucht einmalig eine PIN-Kopplung. | This receiver needs a one-time PIN pairing. |
| pills.title.homekitOnly | Nur Mitglieder des HomeKit-Zuhauses dürfen verbinden. | Only members of the HomeKit home may connect. |
| pills.title.online | Im Netz, nicht verbunden. | On the network, not connected. |
| pills.title.offline | Nicht im Netz gesehen. | Not seen on the network. |
| pills.title.active | Diese Gruppe ist die Ausgabe. | This group is the output. |
| pills.title.ready | Alle Mitglieder sind erreichbar. | Every member is reachable. |
| pills.title.empty | Die Gruppe hat noch keine Mitglieder. | The group has no members yet. |
| pills.title.paired | Eine PIN-Kopplung ist gespeichert. | A PIN pairing is stored. |
| pills.title.noPin | Verbindet ohne PIN (transiente Kopplung). | Connects without a PIN (transient pairing). |

### player (4.3)

| Key | de | en |
|---|---|---|
| player.nowPlaying | Wiedergabe | Now playing |
| player.nothingPlaying | Nichts spielt | Nothing playing |
| player.startInSpotify | Starte die Wiedergabe in einer Spotify-App. | Start playback in a Spotify app. |
| player.selectDevice | Wähle „{{device}}“ als Gerät in einer Spotify-App. | Select "{{device}}" as the device in a Spotify app. |
| player.bridgeFallback | die Bridge | the bridge |
| player.spotifyChip | Spotify · {{user}} | Spotify · {{user}} |
| player.spotifyOnly | Spotify | Spotify |
| player.artworkAlt | Cover von {{album}} | Cover of {{album}} |
| player.albumMeta | {{album}} · {{duration}} | {{album}} · {{duration}} |
| player.progressText | {{elapsed}} von {{total}} | {{elapsed}} of {{total}} |
| player.previous | Vorheriger Titel | Previous track |
| player.next | Nächster Titel | Next track |
| player.play | Wiedergabe | Play |
| player.pause | Pause | Pause |
| player.masterVolume | Master-Lautstärke · {{output}} | Master volume · {{output}} |
| player.masterVolumeNone | Master-Lautstärke · keine Ausgabe | Master volume · no output |
| player.followsSpotify | folgt dem Spotify-Regler | follows the Spotify slider |
| player.quieter | Leiser | Quieter |
| player.louder | Lauter | Louder |
| player.membersChip | {{output}} · {{members}} | {{output}} · {{members}} |
| player.output | Ausgabe | Output |
| player.outputChoice | Ausgabe wählen | Choose the output |
| player.newGroup | Neue Gruppe | New group |
| player.editGroups | Gruppen bearbeiten | Edit groups |
| player.stopOutput | Ausgabe stoppen | Stop output |
| player.edit | Bearbeiten | Edit |
| player.editGroup | Gruppe {{name}} bearbeiten | Edit group {{name}} |
| player.pair | Koppeln | Pair |
| player.groupMeta | Gruppe · {{members}} | Group · {{members}} |
| player.groupEmpty | Gruppe · keine Mitglieder | Group · no members |
| player.groupEmptyShort | keine Mitglieder | no members |
| player.receiverMeta | {{model}} · {{address}} | {{model}} · {{address}} |
| player.unknownModel | Unbekanntes Modell | Unknown model |
| player.noOutput | Nichts ausgewählt: Audio wird verworfen. Wähle einen Empfänger oder eine Gruppe. | Nothing selected: audio is discarded. Choose a receiver or a group. |
| player.status | Status | Status |
| player.diagnostics | Diagnose | Diagnostics |
| player.spotify | Spotify | Spotify |
| player.connectedAs | verbunden · {{user}} | connected · {{user}} |
| player.connected | verbunden | connected |
| player.notConnected | nicht verbunden | not connected |
| player.outputFact | Ausgabe | Output |
| player.outputConnected | verbunden | connected |
| player.outputNone | keine | none |
| player.receiversFact | Empfänger | Receivers |
| player.receiversOnline_one | {{count}} von {{total}} online | {{count}} of {{total}} online |
| player.receiversOnline_other | {{count}} von {{total}} online | {{count}} of {{total}} online |
| player.liveUpdates | Live-Updates | Live updates |

### receivers (4.4)

| Key | de | en |
|---|---|---|
| receivers.title | Empfänger | Receivers |
| receivers.lead | Spotify-Gerät {{device}}, {{spotify}}. {{output}} | Spotify device {{device}}, {{spotify}}. {{output}} |
| receivers.leadSpotifyConnected | verbunden als {{user}} | connected as {{user}} |
| receivers.leadSpotifyConnectedNoUser | mit Spotify verbunden | connected to Spotify |
| receivers.leadSpotifyNotConnected | nicht mit Spotify verbunden | not connected to Spotify |
| receivers.leadOutputGroup | Ausgabe: Gruppe {{name}}. | Output: group {{name}}. |
| receivers.leadOutputReceiver | Ausgabe: {{name}}. | Output: {{name}}. |
| receivers.leadOutputNone | Keine Ausgabe gewählt; Audio wird verworfen. | No output selected; audio is discarded. |
| receivers.empty | Noch keine AirPlay-Empfänger gefunden. | No AirPlay receivers found yet. |
| receivers.emptyHint | Empfänger erscheinen hier, sobald die Bridge sie im Netz sieht (mDNS). Prüfe, ob der Lautsprecher an ist und im selben Netz wie die Bridge. | Receivers appear here as soon as the bridge sees them on the network (mDNS). Check that the speaker is on and in the same network as the bridge. |
| receivers.unknownModel | Unbekanntes Modell | Unknown model |
| receivers.volume | Lautstärke | Volume |
| receivers.volumeOf | Lautstärke von {{name}} | Volume of {{name}} |
| receivers.connect | Verbinden | Connect |
| receivers.connecting | Verbinde… | Connecting… |
| receivers.disconnect | Trennen | Disconnect |
| receivers.pairWithPin | Mit PIN koppeln | Pair with PIN |
| receivers.forgetPairing | Kopplung vergessen | Forget pairing |
| receivers.connectTitle | Nur zu diesem Empfänger spielen | Play to this receiver alone |
| receivers.offlineTitle | Der Empfänger ist offline | The receiver is offline |
| receivers.details | Details | Details |
| receivers.offset | Versatz | Offset |
| receivers.offsetHint | {{frames}} Frames = {{ms}} ms (Latenz-Trimmung, ±2 s) | {{frames}} frames = {{ms}} ms (latency trim, ±2 s) |
| receivers.apply | Übernehmen | Apply |
| receivers.offsetInvalid | Gib eine ganze Zahl an Frames ein. | Enter a whole number of frames. |
| receivers.offsetRange | Der Versatz muss innerhalb von ±{{max}} Frames (2 s) liegen. | The offset must be within ±{{max}} frames (2 s). |
| receivers.tags.airplay2 | AirPlay 2 | AirPlay 2 |
| receivers.tags.ptp | PTP | PTP |
| receivers.tags.buffered | Gepuffert | Buffered |
| receivers.tags.group | Gruppe: {{name}} | Group: {{name}} |
| receivers.tags.output | Ausgabe | Output |
| receivers.facts.id | Kennung | Id |
| receivers.facts.manufacturer | Hersteller | Manufacturer |
| receivers.facts.port | Port | Port |
| receivers.facts.features | Features | Features |
| receivers.facts.flags | Flags | Flags |

### groups (4.5)

| Key | de | en |
|---|---|---|
| groups.title | Gruppen | Groups |
| groups.lead | Eine Gruppe spielt synchron auf mehreren Empfängern. Ihr Master-Pegel G skaliert jeden Mitgliedspegel r_i mit G'/G, die Balance zwischen den Räumen bleibt also wie eingestellt (Mitglieder begrenzen bei 100). Der Spotify-Regler bewegt G der aktiven Gruppe. | A group plays to several receivers in sync. Its master level G scales every member's level r_i by G'/G, so the balance between rooms stays as you set it (members clamp at 100). The Spotify volume slider moves G of the active group. |
| groups.newGroup | Neue Gruppe | New group |
| groups.create | Gruppe anlegen | Create a group |
| groups.createAction | Anlegen | Create |
| groups.name | Name | Name |
| groups.namePlaceholder | Wohnzimmer + Küche | Living room + kitchen |
| groups.empty | Noch keine Gruppen. Lege eine an, füge Empfänger hinzu und aktiviere sie. | No groups yet. Create one, add receivers, then activate it. |
| groups.edit | Bearbeiten | Edit |
| groups.rename | Umbenennen | Rename |
| groups.save | Speichern | Save |
| groups.cancel | Abbrechen | Cancel |
| groups.output | Ausgabe | Output |
| groups.activate | Aktivieren | Activate |
| groups.activateHint | Füge zuerst Empfänger hinzu | Add receivers first |
| groups.activateTitle | Zu dieser Gruppe spielen | Play to this group |
| groups.stopOutput | Ausgabe stoppen | Stop output |
| groups.master | Master (G) | Master (G) |
| groups.clampHint | Einige Mitglieder erreichen 100 und begrenzen; die Balance zwischen ihnen ändert sich. | Some members reach 100 and clamp; the balance between them changes. |
| groups.members | Mitglieder | Members |
| groups.noMembers | Noch keine Mitglieder. | No members yet. |
| groups.memberLevel | Pegel von {{name}} | Level of {{name}} |
| groups.addReceiver | Empfänger hinzufügen | Add receiver |
| groups.choose | Auswählen… | Choose… |
| groups.offlineSuffix | (offline) | (offline) |
| groups.add | Hinzufügen | Add |
| groups.remove | {{name}} entfernen | Remove {{name}} |
| groups.deleteHint | Löscht die Gruppe; die Empfänger bleiben erhalten. | Deletes the group; the receivers stay. |
| groups.delete | Gruppe löschen | Delete group |
| groups.deleteConfirm | {{name}} löschen? | Delete {{name}}? |
| groups.deleteYes | Löschen | Delete |
| groups.keep | Behalten | Keep |
| groups.notFound | Diese Gruppe gibt es nicht (mehr). | This group does not exist (any more). |

### pairing (4.11)

| Key | de | en |
|---|---|---|
| pairing.title | Mit {{name}} koppeln | Pair with {{name}} |
| pairing.prompt | {{name}} braucht eine PIN-Kopplung. Gib die PIN ein, die der Empfänger jetzt anzeigt. | {{name}} needs a PIN pairing. Enter the PIN it shows now. |
| pairing.pin | PIN | PIN |
| pairing.pair | Koppeln | Pair |
| pairing.startAgain | Neu starten | Start again |
| pairing.rejected | Der Empfänger hat die PIN abgelehnt. Starte neu; er zeigt dann eine neue PIN. | The receiver rejected the PIN. Start again; it then shows a new PIN. |
| pairing.expired | Diese Kopplung ist abgelaufen. Starte neu; der Empfänger zeigt dann eine neue PIN. | This pairing expired. Start again; the receiver then shows a new PIN. |
| pairing.failed | Die Kopplung ist fehlgeschlagen. | The pairing failed. |
| pairing.failedDetail | Die Kopplung ist fehlgeschlagen: {{detail}} | The pairing failed: {{detail}} |
| pairing.invalid | Gib die PIN ein, die der Empfänger anzeigt. | Enter the PIN the receiver shows. |

### settings (4.6)

| Key | de | en |
|---|---|---|
| settings.title | Einstellungen | Settings |
| settings.bridge | Bridge | Bridge |
| settings.deviceName | Spotify-Gerätename | Spotify device name |
| settings.deviceNameHint | Der Name in Spotify-Apps. Eine Änderung startet das Spotify-Connect-Gerät neu. | The name shown in Spotify apps. Changing it restarts the Spotify Connect device. |
| settings.latency | Latenz L (Frames) | Latency L (frames) |
| settings.latencyHint | = {{ms}} ms bei 44,1 kHz ({{min}}–{{max}}). | = {{ms}} ms at 44.1 kHz ({{min}}–{{max}}). |
| settings.latencyHint2 | Wie weit vor der Wiedergabe die Empfänger das Audio bekommen; der Standard 77175 Frames (1,75 s) passt für die meisten. Eine Änderung baut die Ausgabe neu auf. | How far ahead of playback the receivers get the audio; the default 77175 frames (1.75 s) fits most receivers. Changing it rebuilds the output. |
| settings.stream | Stream-Typ | Stream type |
| settings.streamRealtime | Realtime (RTP über UDP, NTP- oder PTP-Timing) | Realtime (RTP over UDP, NTP or PTP timing) |
| settings.streamBuffered | Gepuffert (TCP, PTP-Timing) | Buffered (TCP, PTP timing) |
| settings.streamHint | Realtime ist der zuerst gebaute und getestete Weg; Gepuffert wird nur für Empfänger genutzt, die es können. | Realtime is what the bridge was built and tested with first; Buffered is used only for receivers that support it. |
| settings.poll | Empfänger-Lautstärke abfragen | Poll receiver volume |
| settings.pollHint | Liest die Lautstärke alle paar Sekunden vom Empfänger zurück, für Geräte, deren Tasten keine Änderungen über den Ereigniskanal melden. | Reads the volume back from receivers every few seconds, for receivers whose own buttons do not report changes over the event channel. |
| settings.save | Speichern | Save |
| settings.saving | Speichere… | Saving… |
| settings.reset | Zurücksetzen | Reset |
| settings.loadFailed | Die Einstellungen konnten nicht geladen werden. {{detail}} | The settings could not be loaded. {{detail}} |
| settings.validation.nameEmpty | Der Gerätename ist leer. | The device name is empty. |
| settings.validation.nameTooLong | Der Gerätename ist länger als 64 Zeichen. | The device name is longer than 64 characters. |
| settings.validation.latencyRange | Die Latenz muss zwischen 22050 und 176400 Frames liegen. | The latency must be between 22050 and 176400 frames. |
| settings.validation.stream | Der Stream-Typ ist Realtime oder Buffered. | The stream type is Realtime or Buffered. |
| settings.appearance | Darstellung | Appearance |
| settings.theme | Design | Theme |
| settings.language | Sprache | Language |
| settings.spotify | Spotify | Spotify |
| settings.spotifyConnected | Verbunden als {{user}}. | Connected as {{user}}. |
| settings.spotifyConnectedNoUser | Verbunden. | Connected. |
| settings.spotifyNotConnected | Nicht verbunden. Wähle die Bridge in einer Spotify-App, um dich anzumelden. | Not connected. Select the bridge in a Spotify app to log in. |
| settings.forgetSpotify | Spotify-Anmeldung vergessen | Forget Spotify login |
| settings.forgetConfirm | Die gespeicherte Spotify-Anmeldung vergessen? Die Bridge braucht dann eine neue Übergabe aus einer Spotify-App. | Forget the cached Spotify login? The bridge then needs a new hand-off from a Spotify app. |
| settings.forget | Vergessen | Forget |
| settings.diagnosticsLink | Diagnose | Diagnostics |
| settings.diagnosticsHint | Status, Netzwerk, Statistik und Live-Log | Status, network, statistics and the live log |

### diagnostics (4.7)

| Key | de | en |
|---|---|---|
| diagnostics.title | Diagnose | Diagnostics |
| diagnostics.status | Status | Status |
| diagnostics.version | Version | Version |
| diagnostics.runningSince | Läuft seit | Running since |
| diagnostics.spotify | Spotify | Spotify |
| diagnostics.spotifyConnectedAs | verbunden als {{user}} | connected as {{user}} |
| diagnostics.connected | verbunden | connected |
| diagnostics.notConnected | nicht verbunden | not connected |
| diagnostics.deviceName | Gerätename | Device name |
| diagnostics.output | Ausgabe | Output |
| diagnostics.receiversOnNetwork | Empfänger im Netz | Receivers on the network |
| diagnostics.liveUpdates | Live-Updates | Live updates |
| diagnostics.liveUpdatesValue_one | {{state}} ({{count}} Verbindung, {{events}} Ereignisse) | {{state}} ({{count}} connection, {{events}} events) |
| diagnostics.liveUpdatesValue_other | {{state}} ({{count}} Verbindungen, {{events}} Ereignisse) | {{state}} ({{count}} connections, {{events}} events) |
| diagnostics.network | Netzwerk und Statistik | Network and statistics |
| diagnostics.timingPort | Timing-Port (UDP) | Timing port (UDP) |
| diagnostics.controlPort | Control-Port (UDP) | Control port (UDP) |
| diagnostics.notBound | noch nicht gebunden | not bound yet |
| diagnostics.packetsSent | Gesendete Pakete | Packets sent |
| diagnostics.retransmits | Neusendungen | Retransmit requests |
| diagnostics.lateWakeups | Verspätete Pacer-Aufwachen | Late pacer wake-ups |
| diagnostics.retransmitRatio | Neusendungsquote | Retransmit ratio |
| diagnostics.retransmitRatioValue | {{percent}} · {{late}} verspätet | {{percent}} · {{late}} late |
| diagnostics.localAddresses | Lokale Adressen | Local addresses |
| diagnostics.noneReported | Keine gemeldet. | None reported. |
| diagnostics.more_one | {{count}} weitere | {{count}} more |
| diagnostics.more_other | {{count}} weitere | {{count}} more |
| diagnostics.updated | Stand {{time}} | Updated {{time}} |
| diagnostics.unavailable | Diagnose nicht verfügbar. {{detail}} | Diagnostics unavailable. {{detail}} |
| diagnostics.pendingProbes | Offene Probes | Pending probes |
| diagnostics.log | Live-Log | Live log |
| diagnostics.level | Stufe | Level |
| diagnostics.levels.all | Alle | All |
| diagnostics.levels.info | Information | Information |
| diagnostics.levels.warning | Warnung | Warning |
| diagnostics.levels.error | Fehler | Error |
| diagnostics.pause | Anhalten | Pause |
| diagnostics.resume | Weiter | Resume |
| diagnostics.clear | Leeren | Clear |
| diagnostics.noLines | Noch keine Log-Zeilen. | No log lines yet. |

### notices and actions (4.9; used by BridgeCommands)

| Key | de | en |
|---|---|---|
| notices.failed | {{what}} fehlgeschlagen. {{detail}} | {{what}} failed. {{detail}} |
| notices.settingsSaved | Einstellungen gespeichert. | Settings saved. |
| notices.paired | Mit {{name}} gekoppelt. | Paired with {{name}}. |
| notices.spotifyForgotten | Die Spotify-Anmeldung ist vergessen; wähle die Bridge in einer Spotify-App, um dich neu anzumelden. | The Spotify login is forgotten; select the bridge in a Spotify app to log in again. |
| notices.connectFailed | {{name}}: {{error}} | {{name}}: {{error}} |
| notices.connectFailedDefault | die Verbindung ist fehlgeschlagen. | the connection failed. |
| notices.unreachable | Die Bridge ist nicht erreichbar: {{error}} | The bridge is not reachable: {{error}} |
| notices.timeout | Die Bridge hat nicht rechtzeitig geantwortet. | The bridge did not answer in time. |
| actions.refresh | Laden des Bridge-Zustands | Loading the bridge state |
| actions.connect | Verbinden von {{name}} | Connecting {{name}} |
| actions.disconnect | Trennen von {{name}} | Disconnecting {{name}} |
| actions.setVolume | Setzen der Lautstärke von {{name}} | Setting the volume of {{name}} |
| actions.setOffset | Setzen des Versatzes von {{name}} | Setting the offset of {{name}} |
| actions.startPairing | Starten der Kopplung mit {{name}} | Starting the pairing with {{name}} |
| actions.forgetPairing | Vergessen der Kopplung von {{name}} | Forgetting the pairing of {{name}} |
| actions.createGroup | Anlegen der Gruppe {{name}} | Creating the group {{name}} |
| actions.renameGroup | Umbenennen der Gruppe | Renaming the group |
| actions.deleteGroup | Löschen der Gruppe | Deleting the group |
| actions.addMember | Hinzufügen von {{name}} | Adding {{name}} |
| actions.removeMember | Entfernen von {{name}} | Removing {{name}} |
| actions.setGroupVolume | Setzen der Gruppenlautstärke | Setting the group volume |
| actions.activateGroup | Aktivieren der Gruppe | Activating the group |
| actions.stopOutput | Stoppen der Ausgabe | Stopping the output |
| actions.playbackPlay | Starten der Wiedergabe | Starting playback |
| actions.playbackPause | Pausieren | Pausing |
| actions.playbackNext | Weiterschalten zum nächsten Titel | Skipping to the next track |
| actions.playbackPrevious | Zurückschalten zum vorherigen Titel | Going back to the previous track |
| actions.saveSettings | Speichern der Einstellungen | Saving the settings |
| actions.forgetSpotify | Vergessen der Spotify-Anmeldung | Forgetting the Spotify login |

### styleguide and audit (developer pages; English only is acceptable, keys exist for parity)

| Key | de | en |
|---|---|---|
| styleguide.title | Styleguide | Styleguide |
| audit.title | Layout-Prüfung | Layout audit |
