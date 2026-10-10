// Browser plumbing of the TuneShuttle web app (UI-SPEC 2.3). An ES module, imported once by BrowserBridge.cs with
// IJSRuntime.InvokeAsync<IJSObjectReference>("import", "./js/app.js"); components never talk to IJSRuntime directly.
// The module owns no UI. Theme state lives in the index.html bootstrap (window.bridgeTheme), which also applies it
// before the first paint.

// The key keeps the product's old name (AirPlay Bridge, renamed TuneShuttle on 2026-10-10): renaming it would reset
// every user's stored language. Same key as the bootstrap in index.html.
const LANG_KEY = "airplay-bridge-lang";
const SUPPORTED_LANGS = ["de", "en", "fr", "es", "ru", "uk", "pt"];

// ---- theme ---------------------------------------------------------------------------------------------------------

/** "light" | "dark" | "system": the stored choice (a ?theme= override is never stored and not reported here). */
export function getTheme() {
    return window.bridgeTheme ? window.bridgeTheme.read() : "system";
}

/** Applies and stores the choice: data-theme attribute, theme-color override meta, localStorage ("system" clears). */
export function setTheme(theme) {
    if (window.bridgeTheme) window.bridgeTheme.apply(theme);
}

/** True when the primary pointer can hover; false on touch screens (the theme picker's first-tap-opens branch). */
export function hoverCapable() {
    return window.matchMedia("(hover: hover)").matches;
}

// ---- language ------------------------------------------------------------------------------------------------------

/** The supported two-letter code a language tag names ("de", "de-DE", "pt_BR"), else "". */
function supportedLang(code) {
    if (typeof code !== "string" || code.length < 2) return "";
    const two = code.slice(0, 2).toLowerCase();
    const separator = code.length === 2 || code.charAt(2) === "-" || code.charAt(2) === "_";
    return separator && SUPPORTED_LANGS.includes(two) ? two : "";
}

/**
 * The language to start with: the ?lang= query if it names a supported code (one-shot, never stored), else
 * localStorage["airplay-bridge-lang"], else the first of navigator.languages with a supported two-letter code, else ""
 * (the caller falls back to "en"). Same logic as window.bridgeLang.get() in index.html.
 */
export function getLang() {
    const query = supportedLang(new URLSearchParams(window.location.search).get("lang"));
    if (query) return query;
    try {
        const stored = supportedLang(window.localStorage.getItem(LANG_KEY));
        if (stored) return stored;
    } catch (e) {
        // storage blocked (private mode, policy): fall through to the browser languages
    }
    const languages = navigator.languages && navigator.languages.length ? navigator.languages : [navigator.language];
    for (const language of languages) {
        const code = supportedLang(language);
        if (code) return code;
    }
    return "";
}

/** Stores the user's choice ("" removes it: back to browser negotiation) and sets <html lang>. */
export function setLang(code) {
    try {
        if (code === "") window.localStorage.removeItem(LANG_KEY);
        else window.localStorage.setItem(LANG_KEY, code);
    } catch (e) {
        // storage blocked: the choice holds for this page only
    }
    document.documentElement.lang = code === "" ? (getLang() || "en") : code;
}

/** Sets <html lang> only (start-up, after the culture is chosen). */
export function setDocumentLang(code) {
    document.documentElement.lang = code;
}

// ---- interaction helpers -------------------------------------------------------------------------------------------

/**
 * Calls dotnetRef.invokeMethodAsync(method) for every document click whose target is outside element. A bubbling
 * "click" listener (not pointerdown), like the reference apps' pickers: the tap that opens a picker is itself a click
 * inside the element, so it never closes what it just opened. The event path is captured at dispatch time, so a click
 * on something inside the element that a re-render detaches still counts as inside. Returns { dispose() }.
 */
export function onOutsideClick(element, dotnetRef, method) {
    let disposed = false;
    const handler = (e) => {
        const path = typeof e.composedPath === "function" ? e.composedPath() : [];
        if (path.includes(element) || (element && element.contains(e.target))) return;
        dotnetRef.invokeMethodAsync(method).catch((err) => {
            // A click racing the component's disposal rejects with "object reference disposed": expected, ignored.
            // Anything else (an exception in the [JSInvokable] handler) is logged so a broken close stays visible.
            if (!disposed && !/disposed/i.test(String(err))) console.warn("onOutsideClick", method, err);
        });
    };
    document.addEventListener("click", handler);
    return {
        dispose() {
            disposed = true;
            document.removeEventListener("click", handler);
        }
    };
}

/**
 * Calls preventDefault on keydown events inside element whose key is one of keys (the theme picker's arrows, Home and
 * End, which would otherwise also scroll the page). Propagation is untouched, so Blazor's handlers still run.
 * Returns { dispose() }.
 */
export function preventKeys(element, keys) {
    const wanted = new Set(keys || []);
    const handler = (e) => {
        if (wanted.has(e.key)) e.preventDefault();
    };
    if (element) element.addEventListener("keydown", handler);
    return {
        dispose() {
            if (element) element.removeEventListener("keydown", handler);
        }
    };
}

/**
 * Moves the focus out of element: blurs document.activeElement when it is element or inside it (the theme picker
 * after a pointer selection or Escape, so :focus-within no longer keeps the pill unfolded). Does nothing otherwise.
 * Chromium keeps the sequential focus starting point, so the next Tab still continues after the blurred control.
 */
export function blur(element) {
    const active = document.activeElement;
    if (element && active && active !== document.body && element.contains(active) && typeof active.blur === "function") {
        active.blur();
    }
}

const measured = new Map();

/**
 * Publishes the rendered height of the first element matching selector as a CSS custom property on <html>
 * (Math.ceil(height) + "px"), kept current by a ResizeObserver. cssVar may be given with or without the leading "--"
 * ("topbar-h" and "--topbar-h" both write --topbar-h). Calling it again for the same property replaces the observer;
 * when nothing matches, the property is removed so the stylesheet's fallback applies.
 */
export function measure(selector, cssVar) {
    const name = cssVar.startsWith("--") ? cssVar : "--" + cssVar;
    const previous = measured.get(name);
    if (previous) previous.disconnect();
    measured.delete(name);

    const root = document.documentElement;
    const element = document.querySelector(selector);
    if (!element) {
        root.style.removeProperty(name);
        return;
    }
    const write = () => root.style.setProperty(name, Math.ceil(element.getBoundingClientRect().height) + "px");
    write();
    if (typeof ResizeObserver === "function") {
        const observer = new ResizeObserver(write);
        observer.observe(element);
        measured.set(name, observer);
    }
}

/** Remembers the focused element; restore() focuses it again if it is still in the document (dialogs, 4.11). */
export function rememberActive() {
    const active = document.activeElement;
    return {
        restore() {
            if (active && active !== document.body && document.contains(active) && typeof active.focus === "function") {
                active.focus();
            }
        }
    };
}

// ---- layout audit (UI-SPEC 5.6) ------------------------------------------------------------------------------------

const INTERACTIVE = "a[href], button, input, select, textarea, [role=radio], [role=button], summary";
const NAMEABLE = "a[href], button, input:not([type=hidden]), select, textarea, [role=radio], [role=button], [role=dialog]";
const MIN_TARGET = 44;

function cssEscape(value) {
    return window.CSS && typeof window.CSS.escape === "function" ? window.CSS.escape(value) : value.replace(/[^\w-]/g, "\\$&");
}

/** tag#id.class.class (or tag[aria-label="…"] / tag[name="…"] when it has neither) */
function ownSelector(el) {
    let s = el.tagName.toLowerCase();
    if (el.id) return s + "#" + cssEscape(el.id);
    const classes = Array.from(el.classList);
    if (classes.length) return s + classes.map((c) => "." + cssEscape(c)).join("");
    const label = el.getAttribute("aria-label");
    if (label) return s + '[aria-label="' + label.replace(/"/g, '\\"') + '"]';
    const name = el.getAttribute("name");
    if (name) return s + '[name="' + name.replace(/"/g, '\\"') + '"]';
    const type = el.getAttribute("type");
    if (type) s += '[type="' + type + '"]';
    return s;
}

/** A readable, valid CSS selector: the closest ancestor with an id or a class, a space, the element itself. */
function describe(el) {
    let parent = el.parentElement;
    while (parent && parent !== document.body && !parent.id && parent.classList.length === 0) parent = parent.parentElement;
    const own = ownSelector(el);
    return parent && parent !== document.body ? ownSelector(parent) + " " + own : own;
}

function isBigEnough(rect) {
    return rect.width >= MIN_TARGET && rect.height >= MIN_TARGET;
}

function smallTargets() {
    const small = [];
    for (const el of document.querySelectorAll(INTERACTIVE)) {
        if (el.matches("input[type=hidden]")) continue;
        const rect = el.getBoundingClientRect();
        if (rect.width === 0 || rect.height === 0) continue; // display: none, collapsed, zero size
        if (isBigEnough(rect)) continue;
        // A (visually hidden) input whose label is the target: the wrapping <label>, or a <label for> it owns.
        if (el.labels && Array.from(el.labels).some((label) => isBigEnough(label.getBoundingClientRect()))) continue;
        small.push(describe(el));
    }
    return small;
}

function text(value) {
    return (value || "").replace(/\s+/g, " ").trim();
}

function labelText(control) {
    const labels = control.labels && control.labels.length
        ? Array.from(control.labels)
        : [control.id ? document.querySelector('label[for="' + cssEscape(control.id) + '"]') : null, control.closest("label")];
    return labels
        .filter((label) => label)
        .map((label) => {
            const copy = label.cloneNode(true);
            // the control's own text (a select's options) is not part of its name
            copy.querySelectorAll("input, select, textarea").forEach((c) => c.remove());
            return text(copy.textContent);
        })
        .join(" ");
}

/** The subset of the accessible-name computation of UI-SPEC 5.6, steps (1) to (8). */
function accessibleName(el) {
    const tag = el.tagName.toLowerCase();
    const labelledby = el.getAttribute("aria-labelledby");
    if (labelledby) {
        const name = text(labelledby.split(/\s+/).map((id) => { const r = document.getElementById(id); return r ? r.textContent : ""; }).join(" "));
        if (name) return name;
    }
    const aria = text(el.getAttribute("aria-label"));
    if (aria) return aria;
    if (tag === "input" || tag === "select" || tag === "textarea") {
        const label = text(labelText(el));
        if (label) return label;
    }
    if (tag === "button" || tag === "a" || tag === "summary" || el.matches("[role=button], [role=radio]")) {
        const content = text(el.textContent); // includes visually hidden spans and SVG <title> text
        if (content) return content;
    }
    const img = el.querySelector("img[alt]");
    if (img && text(img.getAttribute("alt"))) return text(img.getAttribute("alt"));
    const title = text(el.getAttribute("title"));
    if (title) return title;
    if (tag === "input" && (el.type === "button" || el.type === "submit")) {
        const value = text(el.value);
        if (value) return value;
    }
    return ""; // (8) a placeholder alone is not a name
}

function unnamedControls() {
    const unnamed = [];
    for (const el of document.querySelectorAll(NAMEABLE)) {
        if (el.closest('[aria-hidden="true"]')) continue;
        if (!accessibleName(el)) unnamed.push(describe(el));
    }
    return unnamed;
}

/** Text that must never be cut by an ellipsis: the shell's connection word. */
const NEVER_TRUNCATED = ".status__text";

/** Visible elements of NEVER_TRUNCATED whose text does not fit their box (scrollWidth > clientWidth). */
function truncatedTexts() {
    const truncated = [];
    for (const el of document.querySelectorAll(NEVER_TRUNCATED)) {
        if (el.getClientRects().length === 0) continue; // display: none
        if (el.scrollWidth > el.clientWidth) truncated.push(describe(el) + " \"" + text(el.textContent) + "\"");
    }
    return truncated;
}

/**
 * The layout audit of UI-SPEC 5.6, returned as a JSON string (BrowserBridge.AuditAsync hands it on unchanged):
 * { overflow, small, unnamed, truncated }. overflow = scrollWidth - innerWidth (must be 0). small lists interactive
 * elements under 44 x 44 CSS px; it is computed unconditionally at innerWidth <= 699 (headless Edge never reports a
 * coarse pointer) or when (pointer: coarse) matches, else it is empty. unnamed lists controls without an accessible
 * name. truncated lists texts that must stay whole but are cut by an ellipsis (the status word); it is
 * checked from 360px (below that the phone shell may shorten the word, 5.2).
 */
export function audit() {
    const phoneOrTouch = window.innerWidth <= 699 || window.matchMedia("(pointer: coarse)").matches;
    return JSON.stringify({
        overflow: document.documentElement.scrollWidth - window.innerWidth,
        small: phoneOrTouch ? smallTargets() : [],
        unnamed: unnamedControls(),
        truncated: window.innerWidth >= 360 ? truncatedTexts() : []
    });
}
