using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;

namespace Bridge.Web.Localization;

/// <summary>
/// A language the app offers: its two-letter code and the name the picker shows for it, written in that language
/// itself (someone who landed in a language they cannot read still recognises their own entry).
/// </summary>
/// <param name="Code">Two-letter code, also the catalog's file name.</param>
/// <param name="NativeName">The language's own name for itself.</param>
public sealed record Language(string Code, string NativeName);

/// <summary>
/// The app's message catalog (UI-SPEC 3.1), a port of the house <c>I18nCatalog</c> for the SPA. The catalogs are the
/// embedded i18next JSON files <c>Resources/i18n/{code}.json</c> (nested objects, string leaves, <c>{{name}}</c>
/// placeholders, plural suffixes <c>_one</c>/<c>_few</c>/<c>_many</c>/<c>_other</c>), flattened to <c>"a.b.c"</c>
/// keys. German is the source language: a key missing from the current language falls back to German, then to the key
/// itself (never throws, so a gap is visible on screen and caught by the parity tests).
/// <para>
/// Unlike the server-rendered reference, the language is state of this singleton: <see cref="SetLanguage"/> switches
/// it at runtime, sets the default-thread cultures to <see cref="Culture"/> (so the <see cref="Format"/> helpers and this
/// catalog print the same language) and raises <see cref="Changed"/>, on which every component re-renders.
/// </para>
/// </summary>
public sealed class I18n
{
    /// <summary>The language the catalogs are written in, and the fallback for missing keys.</summary>
    public const string SourceCulture = "de";

    /// <summary>The language served when nothing matches (no stored choice, no supported browser language).</summary>
    public const string DefaultCulture = "en";

    /// <summary>Every language the app offers, in the order the language select lists them.</summary>
    public static readonly IReadOnlyList<Language> Languages =
    [
        new("de", "Deutsch"),
        new("en", "English"),
        new("fr", "Français"),
        new("es", "Español"),
        new("ru", "Русский"),
        new("uk", "Українська"),
        new("pt", "Português (Brasil)"),
    ];

    /// <summary>The codes of <see cref="Languages"/>, derived (edit <see cref="Languages"/>, never this).</summary>
    public static readonly string[] SupportedCultures = [.. Languages.Select(language => language.Code)];

    private const string ResourcePrefix = "Bridge.Web.Resources.i18n.";

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>(StringComparer.Ordinal);

    private readonly Dictionary<string, Dictionary<string, string>> _catalogs = new(StringComparer.Ordinal);
    private volatile State _current;

    /// <summary>
    /// Loads every embedded catalog that exists (a language without a catalog yet is served from German) and selects
    /// <paramref name="initialCode"/> (resolved; <see cref="DefaultCulture"/> when null or unsupported). The constructor
    /// does not touch the process cultures; <see cref="SetLanguage"/> does.
    /// </summary>
    public I18n(string? initialCode = null)
        : this(LoadEmbedded(), initialCode)
    {
    }

    /// <summary>
    /// A catalog over the given flattened catalogs (code → key → text) instead of the embedded ones; for tests of the
    /// lookup and fallback rules. The source catalog (<see cref="SourceCulture"/>) is required.
    /// </summary>
    internal I18n(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> catalogs, string? initialCode = null)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        foreach (var (code, catalog) in catalogs)
        {
            _catalogs[code] = new Dictionary<string, string>(catalog, StringComparer.Ordinal);
        }

        if (!_catalogs.ContainsKey(SourceCulture))
        {
            throw new InvalidOperationException($"Missing the source catalog '{ResourcePrefix}{SourceCulture}.json'.");
        }

        _current = State.For(Resolve(initialCode));
    }

    /// <summary>Raised after <see cref="SetLanguage"/> switched to another language.</summary>
    public event Action? Changed;

    /// <summary>The current language's two-letter code (one of <see cref="SupportedCultures"/>).</summary>
    public string Code => _current.Code;

    /// <summary>The culture of the current language: de-DE, en-US, fr-FR, es-ES, ru-RU, uk-UA or pt-BR.</summary>
    public CultureInfo Culture => _current.Culture;

    /// <summary>The text of <paramref name="key"/>: current language, then German, then the key itself.</summary>
    public string this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            var code = Code;
            return TryGet(code, key, out var text) || TryGet(SourceCulture, key, out text) ? text : key;
        }
    }

    /// <summary>The supported two-letter code <paramref name="code"/> names ("de", "de-DE", "pt_BR"), else <see cref="DefaultCulture"/>.</summary>
    public static string Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return DefaultCulture;
        }

        var trimmed = code.Trim();
        if (trimmed.Length < 2 || (trimmed.Length > 2 && trimmed[2] is not ('-' or '_')))
        {
            return DefaultCulture;
        }

        var twoLetter = trimmed[..2].ToLowerInvariant();
        return Array.IndexOf(SupportedCultures, twoLetter) >= 0 ? twoLetter : DefaultCulture;
    }

    /// <summary>
    /// The text of <paramref name="key"/> with every <c>{{name}}</c> replaced by its value, converted with
    /// <see cref="Culture"/> (format numbers upstream with the <see cref="Bridge.Web.Format"/> helpers). Unknown
    /// placeholders stay as they are; a null value becomes the empty string.
    /// </summary>
    public string Format(string key, params (string Name, object? Value)[] args) => Replace(this[key], args, Culture);

    /// <summary>
    /// The plural form of <paramref name="key"/> for <paramref name="count"/>: <c>key_{category}</c> by the language's
    /// CLDR category (<see cref="PluralRules"/>), then <c>key_other</c>, then <c>key</c>, first in the current language
    /// and then in German (with the German category). <c>{{count}}</c> is the count in the culture's integer format;
    /// <paramref name="args"/> fill the other placeholders (and win over <c>count</c> if they name it).
    /// </summary>
    public string Plural(string key, long count, params (string Name, object? Value)[] args)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(args);
        var state = _current;
        if (!TryGetPlural(state.Code, key, count, out var text) && !TryGetPlural(SourceCulture, key, count, out text))
        {
            text = key;
        }

        text = Replace(text, args, state.Culture);
        return text.Replace("{{count}}", count.ToString("N0", state.Culture), StringComparison.Ordinal);
    }

    /// <summary>
    /// Switches the language: resolves <paramref name="code"/> (<see cref="Resolve"/>), sets
    /// both <c>DefaultThreadCurrent*</c> cultures to <see cref="Culture"/>, and raises <see cref="Changed"/> when the
    /// language actually changed. Call it on the renderer's thread (the browser has only one).
    /// <para>
    /// It deliberately leaves <see cref="CultureInfo.CurrentCulture"/>/<see cref="CultureInfo.CurrentUICulture"/>
    /// alone: those are AsyncLocal, and a value set in an event handler would be captured by every flow started
    /// afterwards (the pages' <c>Every()</c> timer loops), pinning the previous language's culture in those flows after
    /// the next switch. The app never sets them, so every flow reads the default-thread cultures and the
    /// <see cref="Bridge.Web.Format"/> helpers follow each switch. (Tests isolate themselves with an AsyncLocal culture
    /// of their own.)
    /// </para>
    /// </summary>
    public void SetLanguage(string? code)
    {
        var previous = _current;
        var next = State.For(Resolve(code));
        _current = next;
        CultureInfo.DefaultThreadCurrentCulture = next.Culture;
        CultureInfo.DefaultThreadCurrentUICulture = next.Culture;
        if (previous.Code != next.Code)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>The flattened catalog of <paramref name="code"/> as embedded (empty when there is none yet); for tests.</summary>
    public IReadOnlyDictionary<string, string> Keys(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return _catalogs.TryGetValue(code, out var catalog) ? catalog : Empty;
    }

    private bool TryGet(string code, string key, out string text)
    {
        if (_catalogs.TryGetValue(code, out var catalog) && catalog.TryGetValue(key, out var found))
        {
            text = found;
            return true;
        }

        text = string.Empty;
        return false;
    }

    private bool TryGetPlural(string code, string key, long count, out string text) =>
        TryGet(code, key + PluralRules.Suffix(PluralRules.Select(code, count)), out text)
        || TryGet(code, key + PluralRules.Suffix(PluralCategory.Other), out text)
        || TryGet(code, key, out text);

    private static string Replace(string text, (string Name, object? Value)[] args, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(args);
        foreach (var (name, value) in args)
        {
            text = text.Replace("{{" + name + "}}", Convert.ToString(value, culture) ?? string.Empty, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Every embedded catalog that exists (a language without a catalog yet is served from German).</summary>
    private static Dictionary<string, IReadOnlyDictionary<string, string>> LoadEmbedded()
    {
        var assembly = typeof(I18n).Assembly;
        var catalogs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var code in SupportedCultures)
        {
            if (LoadJson(assembly, ResourcePrefix + code + ".json") is { } catalog)
            {
                catalogs[code] = Flatten(catalog);
            }
        }

        return catalogs;
    }

    private static JsonObject? LoadJson(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        return stream is null ? null : JsonNode.Parse(stream) as JsonObject;
    }

    /// <summary>Flattens the nested catalog into <c>"a.b.c"</c> keys; non-string leaves are ignored.</summary>
    private static Dictionary<string, string> Flatten(JsonObject catalog)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(catalog, string.Empty);
        return result;

        void Walk(JsonObject node, string prefix)
        {
            foreach (var (name, value) in node)
            {
                var key = prefix.Length == 0 ? name : prefix + "." + name;
                switch (value)
                {
                    case JsonObject child:
                        Walk(child, key);
                        break;
                    case JsonValue leaf when leaf.TryGetValue<string>(out var text):
                        result[key] = text;
                        break;
                }
            }
        }
    }

    /// <summary>The language and its culture, swapped as one reference so readers never see a mixed pair.</summary>
    private sealed record State(string Code, CultureInfo Culture)
    {
        public static State For(string code) => new(code, CultureInfo.GetCultureInfo(code switch
        {
            "de" => "de-DE",
            "en" => "en-US",
            "fr" => "fr-FR",
            "es" => "es-ES",
            "ru" => "ru-RU",
            "uk" => "uk-UA",
            "pt" => "pt-BR",
            _ => "en-US",
        }));
    }
}
