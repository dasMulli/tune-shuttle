using System.Text.RegularExpressions;
using Bridge.Contracts;
using Bridge.Web;
using Bridge.Web.Components;
using Bridge.Web.Localization;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// The message catalog (UI-SPEC 3.1, 3.6, 10): the embedded de/en catalogs, lookup and fallback, <c>Format</c>,
/// <c>Plural</c> with the CLDR categories, <c>SetLanguage</c>; plus the i18n-core components and the catalog-backed
/// texts of <see cref="BridgeCommands"/> and <see cref="SettingsModel"/>. In <see cref="ProcessCultureCollection"/>:
/// <c>SetLanguage</c> changes the process-wide default-thread cultures.
/// </summary>
[Collection(ProcessCultureCollection.Name)]
public sealed partial class I18nTests
{
    /// <summary>Every language has its own catalog since i18n-fill.</summary>
    private static readonly string[] CatalogLanguages = I18n.SupportedCultures;

    /// <summary>The plural keys of UI-SPEC 10 (base keys).</summary>
    private static readonly string[] PluralBases = ["diagnostics.liveUpdatesValue", "diagnostics.more", "player.receiversOnline"];

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"_(one|few|many|other)$")]
    private static partial Regex PluralSuffix();

    [GeneratedRegex(@"\p{L}'\p{L}")]
    private static partial Regex AsciiApostrophe();

    public static TheoryData<string> Translations => new(I18n.SupportedCultures.Where(code => code != I18n.SourceCulture));

    public static TheoryData<string> AllLanguages => new(I18n.SupportedCultures);

    [Fact]
    public void Languages_are_the_seven_of_the_spec_in_picker_order()
    {
        Assert.Equal(["de", "en", "fr", "es", "ru", "uk", "pt"], I18n.SupportedCultures);
        Assert.Equal(["Deutsch", "English", "Français", "Español", "Русский", "Українська", "Português (Brasil)"], I18n.Languages.Select(l => l.NativeName));
        Assert.Equal("de", I18n.SourceCulture);
        Assert.Equal("en", I18n.DefaultCulture);
    }

    [Fact]
    public void De_and_en_catalogs_are_embedded_and_have_the_same_keys()
    {
        var l = new I18n();
        var de = l.Keys("de");
        var en = l.Keys("en");
        Assert.True(de.Count > 250, $"de.json has only {de.Count} keys");
        Assert.Empty(de.Keys.Except(en.Keys));
        Assert.Empty(en.Keys.Except(de.Keys));
        Assert.All(de, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
        Assert.All(en, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value), pair.Key));
        Assert.Empty(l.Keys("xx"));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_catalog_has_the_german_key_set_with_its_own_plural_forms(string code)
    {
        var l = new I18n();
        var de = l.Keys(I18n.SourceCulture);
        var catalog = l.Keys(code);
        Assert.NotEmpty(catalog);

        // Plain keys: exactly the German ones.
        var dePlain = de.Keys.Where(k => !PluralSuffix().IsMatch(k)).ToHashSet(StringComparer.Ordinal);
        var plain = catalog.Keys.Where(k => !PluralSuffix().IsMatch(k)).ToHashSet(StringComparer.Ordinal);
        Assert.True(dePlain.SetEquals(plain), $"{code}: missing [{string.Join(", ", dePlain.Except(plain))}], extra [{string.Join(", ", plain.Except(dePlain))}]");

        // Plural keys: every German base with exactly the forms the language's categories need.
        var forms = catalog.Keys.Where(k => PluralSuffix().IsMatch(k)).ToHashSet(StringComparer.Ordinal);
        var expected = PluralBases.SelectMany(b => RequiredPluralSuffixes(code).Select(s => b + s)).ToHashSet(StringComparer.Ordinal);
        Assert.True(expected.SetEquals(forms), $"{code}: missing [{string.Join(", ", expected.Except(forms))}], extra [{string.Join(", ", forms.Except(expected))}]");

        Assert.All(catalog, pair =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"{code}: {pair.Key} is empty");
            Assert.Equal(pair.Value.Trim(), pair.Value);
        });
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void Catalog_texts_follow_the_typography_rules(string code)
    {
        // UI-SPEC 10: ellipses are U+2026, never three dots; placeholders are complete {{name}} pairs. In the translated
        // catalogs apostrophes are the typographic U+2019 (fr "l’audio", uk "зв’язку"), never the ASCII one; the prime
        // of the groups formula "G'/G" is not an apostrophe (letter, then slash), so only letter'letter is rejected.
        // en (wave 1, the spec's own copy) keeps its ASCII possessive in groups.lead ("member's").
        var checkApostrophes = code != "en";
        Assert.All(new I18n().Keys(code), pair =>
        {
            Assert.DoesNotContain("...", pair.Value, StringComparison.Ordinal);
            Assert.False(checkApostrophes && AsciiApostrophe().IsMatch(pair.Value), $"{code}: {pair.Key} uses the ASCII apostrophe instead of U+2019: {pair.Value}");
            var withoutPlaceholders = Placeholder().Replace(pair.Value, string.Empty);
            Assert.False(withoutPlaceholders.Contains('{', StringComparison.Ordinal) || withoutPlaceholders.Contains('}', StringComparison.Ordinal), $"{code}: {pair.Key} has a broken placeholder: {pair.Value}");
            Assert.False(pair.Value.Contains("  ", StringComparison.Ordinal), $"{code}: {pair.Key} has a double space");
        });
    }

    [Theory]
    [InlineData("fr", "nav.receivers", "Récepteurs")]
    [InlineData("fr", "player.selectDevice", "Choisissez « {{device}} » comme appareil dans une app Spotify.")]
    [InlineData("fr", "settings.title", "Réglages")]
    [InlineData("fr", "app.notFound", "Il n’y a rien à cette adresse.")]
    [InlineData("es", "nav.receivers", "Receptores")]
    [InlineData("es", "player.liveUpdates", "Actualizaciones en directo")]
    [InlineData("es", "notices.failed", "Error al {{what}}. {{detail}}")]
    [InlineData("es", "settings.title", "Ajustes")]
    [InlineData("pt", "nav.settings", "Ajustes")]
    [InlineData("pt", "player.selectDevice", "Selecione “{{device}}” como dispositivo em um app do Spotify.")]
    [InlineData("ru", "nav.receivers", "Приёмники")]
    [InlineData("ru", "pills.pinNeeded", "нужен PIN")]
    [InlineData("ru", "status.live", "онлайн")]
    [InlineData("ru", "diagnostics.liveUpdatesValue_many", "{{state}} ({{count}} подключений, событий: {{events}})")]
    [InlineData("uk", "nav.settings", "Параметри")]
    [InlineData("uk", "status.live", "в ефірі")]
    [InlineData("uk", "diagnostics.liveUpdatesValue_many", "{{state}} ({{count}} підключень, подій: {{events}})")]
    public void Translations_hold_their_own_language(string code, string key, string text) =>
        Assert.Equal(text, new I18n().Keys(code)[key]);

    [Theory]
    [MemberData(nameof(Translations))]
    public void Translations_are_not_copies_of_another_catalog(string code)
    {
        // A copied catalog (another language under a new file name) passes the key parity; it does not pass this. Close
        // languages share real words (es and pt: 18 %), so the bar is a quarter, far below a copy.
        var l = new I18n();
        var catalog = l.Keys(code);
        foreach (var other in I18n.SupportedCultures.Where(c => c != code))
        {
            var reference = l.Keys(other);
            var same = catalog.Count(pair => reference.TryGetValue(pair.Key, out var text) && text == pair.Value && Placeholder().Replace(pair.Value, string.Empty).Any(char.IsLower));
            Assert.True(same < catalog.Count / 4, $"{code}: {same} of {catalog.Count} texts are identical to {other}");
        }
    }

    [Theory]
    [InlineData("app.name", "AirPlay Bridge", "AirPlay Bridge")]
    [InlineData("nav.receivers", "Empfänger", "Receivers")]
    [InlineData("common.levelText", "{{value}} von 100", "{{value}} of 100")]
    [InlineData("receivers.connecting", "Verbinde…", "Connecting…")]
    [InlineData("player.bridgeFallback", "die Bridge", "the bridge")]
    [InlineData("player.groupEmptyShort", "keine Mitglieder", "no members")]
    [InlineData("pairing.failedDetail", "Die Kopplung ist fehlgeschlagen: {{detail}}", "The pairing failed: {{detail}}")]
    [InlineData("diagnostics.retransmitRatio", "Neusendungsquote", "Retransmit ratio")]
    [InlineData("diagnostics.retransmitRatioValue", "{{percent}} · {{late}} verspätet", "{{percent}} · {{late}} late")]
    [InlineData("actions.playbackPlay", "Starten der Wiedergabe", "Starting playback")]
    [InlineData("actions.playbackPause", "Pausieren", "Pausing")]
    [InlineData("actions.playbackNext", "Weiterschalten zum nächsten Titel", "Skipping to the next track")]
    [InlineData("actions.playbackPrevious", "Zurückschalten zum vorherigen Titel", "Going back to the previous track")]
    [InlineData("settings.validation.latencyRange", "Die Latenz muss zwischen 22.050 und 176.400 Frames liegen.", "The latency must be between 22,050 and 176,400 frames.")]
    [InlineData("player.receiversOnline_one", "{{count}} von {{total}} online", "{{count}} of {{total}} online")]
    [InlineData("diagnostics.liveUpdatesValue_other", "{{state}} ({{count}} Verbindungen, Ereignisse: {{events}})", "{{state}} ({{count}} connections, events: {{events}})")]
    [InlineData("settings.streamRealtime", "Realtime", "Realtime")]
    [InlineData("settings.streamBuffered", "Gepuffert", "Buffered")]
    public void Catalogs_hold_the_texts_of_the_spec(string key, string de, string en)
    {
        var l = new I18n();
        Assert.Equal(de, l.Keys("de")[key]);
        Assert.Equal(en, l.Keys("en")[key]);
    }

    [Theory]
    [InlineData("app.loading")]
    [InlineData("app.error")]
    [InlineData("common.close")]
    [InlineData("common.save")]
    [InlineData("common.working")]
    [InlineData("common.yes")]
    [InlineData("common.no")]
    [InlineData("player.title")]
    [InlineData("player.levelText")]
    [InlineData("groups.masterText")]
    [InlineData("actions.playback")]
    public void Keys_removed_from_the_first_draft_are_absent(string key)
    {
        var l = new I18n();
        foreach (var code in CatalogLanguages)
        {
            Assert.False(l.Keys(code).ContainsKey(key), $"{code}: {key}");
        }

        Assert.DoesNotContain(l.Keys("de").Keys, k => k.StartsWith("units.", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Every_placeholder_of_a_german_text_appears_in_the_translation_and_no_other(string code)
    {
        var l = new I18n();
        var de = l.Keys(I18n.SourceCulture);
        Assert.All(l.Keys(code), pair =>
        {
            // _few/_many exist only in ru/uk; they carry the placeholders of the German _other form.
            var germanKey = PluralSuffix().Replace(pair.Key, m => m.Groups[1].Value is "few" or "many" ? "_other" : m.Value);
            var expected = Placeholders(de[germanKey]);
            Assert.True(expected.SetEquals(Placeholders(pair.Value)), $"{code}: {pair.Key}: de {{{string.Join(",", expected)}}} {code} \"{pair.Value}\"");
        });
    }

    [Fact]
    public void Plural_keys_have_the_forms_of_each_languages_categories()
    {
        var l = new I18n();
        var bases = l.Keys("de").Keys.Where(k => PluralSuffix().IsMatch(k)).Select(k => PluralSuffix().Replace(k, string.Empty)).Distinct();
        Assert.Equal(PluralBases, bases.Order(StringComparer.Ordinal));
        foreach (var code in CatalogLanguages)
        {
            foreach (var key in PluralBases)
            {
                foreach (var suffix in RequiredPluralSuffixes(code))
                {
                    Assert.True(l.Keys(code).ContainsKey(key + suffix), $"{code}: {key}{suffix}");
                }

                Assert.False(l.Keys(code).ContainsKey(key), $"{code}: {key} (base key next to the plural forms)");
            }
        }

        Assert.Equal(["_one", "_other"], RequiredPluralSuffixes("en"));
        Assert.Equal(["_one", "_few", "_many", "_other"], RequiredPluralSuffixes("ru"));
        Assert.Equal(["_one", "_few", "_many", "_other"], RequiredPluralSuffixes("uk"));
    }

    [Theory]
    [InlineData("ru", 1, "онлайн (1 подключение, событий: 1 234)")]
    [InlineData("ru", 2, "онлайн (2 подключения, событий: 1 234)")]
    [InlineData("ru", 5, "онлайн (5 подключений, событий: 1 234)")]
    [InlineData("ru", 11, "онлайн (11 подключений, событий: 1 234)")]
    [InlineData("ru", 21, "онлайн (21 подключение, событий: 1 234)")]
    [InlineData("ru", 22, "онлайн (22 подключения, событий: 1 234)")]
    [InlineData("ru", 0, "онлайн (0 подключений, событий: 1 234)")]
    [InlineData("uk", 1, "в ефірі (1 підключення, подій: 1 234)")]
    [InlineData("uk", 3, "в ефірі (3 підключення, подій: 1 234)")]
    [InlineData("uk", 12, "в ефірі (12 підключень, подій: 1 234)")]
    [InlineData("uk", 101, "в ефірі (101 підключення, подій: 1 234)")]
    [InlineData("fr", 0, "en direct (0 connexion, événements : 1 234)")]
    [InlineData("fr", 1, "en direct (1 connexion, événements : 1 234)")]
    [InlineData("fr", 2, "en direct (2 connexions, événements : 1 234)")]
    [InlineData("pt", 0, "ao vivo (0 conexão, eventos: 1.234)")]
    [InlineData("pt", 1, "ao vivo (1 conexão, eventos: 1.234)")]
    [InlineData("pt", 2, "ao vivo (2 conexões, eventos: 1.234)")]
    [InlineData("es", 0, "en directo (0 conexiones, eventos: 1.234)")]
    [InlineData("es", 1, "en directo (1 conexión, eventos: 1.234)")]
    [InlineData("es", 2, "en directo (2 conexiones, eventos: 1.234)")]
    public void Plural_picks_the_form_of_the_real_catalogs(string code, long connections, string expected)
    {
        // The Diagnostics live-updates line (UI-SPEC 4.7), as the page composes it.
        using var scope = new CultureScope(code);
        var l = new I18n(code);
        var text = l.Plural("diagnostics.liveUpdatesValue", connections, ("state", l["status.live"]), ("events", Format.Integer(1234)));
        Assert.Equal(expected, Spaces(text));
    }

    [Theory]
    [InlineData("fr", "1 sur 2 en ligne", "1 234 de plus", "73 sur 100")]
    [InlineData("es", "1 de 2 en línea", "1.234 más", "73 de 100")]
    [InlineData("pt", "1 de 2 online", "mais 1.234", "73 de 100")]
    [InlineData("ru", "1 из 2 в сети", "ещё 1 234", "73 из 100")]
    [InlineData("uk", "1 з 2 у мережі", "ще 1 234", "73 з 100")]
    public void Plural_and_Format_fill_the_real_catalogs_in_the_catalog_culture(string code, string online, string more, string level)
    {
        using var scope = new CultureScope(code);
        var l = new I18n(code);
        Assert.Equal(online, l.Plural("player.receiversOnline", 1, ("total", 2)));
        Assert.Equal(more, Spaces(l.Plural("diagnostics.more", 1234)));
        Assert.Equal(level, l.Format("common.levelText", ("value", 73)));
        Assert.Equal(l["nav.receivers"] + " · AirPlay Bridge", l.Format("app.pageTitle", ("page", l["nav.receivers"])));
    }

    [Fact]
    public void Lookup_falls_back_from_the_language_to_german_to_the_key()
    {
        using var scope = new CultureScope("en");
        var l = new I18n(Catalogs(("de", [("a.both", "beide"), ("a.deOnly", "nur deutsch")]), ("en", [("a.both", "both")])), "en");
        Assert.Equal("both", l["a.both"]);
        Assert.Equal("nur deutsch", l["a.deOnly"]); // missing en key -> German
        Assert.Equal("a.nowhere", l["a.nowhere"]);   // missing everywhere -> the key itself
        Assert.Equal("nur deutsch", l.Format("a.deOnly"));

        // A language the given catalogs lack (every real language has one since i18n-fill) is served from German.
        l.SetLanguage("fr");
        try
        {
            Assert.Equal("fr", l.Code);
            Assert.Equal("beide", l["a.both"]);
        }
        finally
        {
            l.SetLanguage("en");
        }
    }

    [Fact]
    public void Real_catalogs_serve_the_current_language_in_every_language()
    {
        using var scope = new CultureScope("en");
        var l = new I18n("de");
        Assert.Equal("Empfänger", l["nav.receivers"]);
        string[] expected = ["Empfänger", "Receivers", "Récepteurs", "Receptores", "Приёмники", "Приймачі", "Receptores"];
        foreach (var (code, text) in I18n.SupportedCultures.Zip(expected))
        {
            l.SetLanguage(code);
            Assert.Equal(text, l["nav.receivers"]);
            Assert.Equal(l.Keys(code)["settings.title"], l["settings.title"]); // served from the language's own catalog
        }

        l.SetLanguage("en");
        Assert.Equal("no.such.key", l["no.such.key"]);
    }

    [Theory]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    [InlineData("  ", "en")]
    [InlineData("de", "de")]
    [InlineData("DE", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("pt_BR", "pt")]
    [InlineData("pt-PT", "pt")]
    [InlineData("uk", "uk")]
    [InlineData("it", "en")]
    [InlineData("d", "en")]
    [InlineData("deu", "en")]
    [InlineData("zh-Hans", "en")]
    public void Resolve_maps_a_language_tag_to_a_supported_code_or_english(string? code, string expected) =>
        Assert.Equal(expected, I18n.Resolve(code));

    [Theory]
    [InlineData("de", "de-DE")]
    [InlineData("en", "en-US")]
    [InlineData("fr", "fr-FR")]
    [InlineData("es", "es-ES")]
    [InlineData("ru", "ru-RU")]
    [InlineData("uk", "uk-UA")]
    [InlineData("pt", "pt-BR")]
    public void SetLanguage_sets_the_culture_of_the_catalog_and_of_the_process(string code, string culture)
    {
        using var scope = new CultureScope("en");
        var l = new I18n();
        l.SetLanguage(code);
        Assert.Equal(code, l.Code);
        Assert.Equal(culture, l.Culture.Name);
        Assert.Equal(culture, System.Globalization.CultureInfo.DefaultThreadCurrentCulture?.Name);
        Assert.Equal(culture, System.Globalization.CultureInfo.DefaultThreadCurrentUICulture?.Name);

        // Every flow without an AsyncLocal culture (all of the app's) reads them, so the Format helpers follow.
        Assert.Equal(1234.ToString("N0", l.Culture), CultureScope.InFreshFlow(() => Format.Integer(1234)));
        Assert.Equal(culture, CultureScope.InFreshFlow(() => System.Globalization.CultureInfo.CurrentUICulture.Name));

        // The AsyncLocal cultures are never set (a flow started later would capture them): this flow keeps the scope's.
        Assert.Equal("en-US", System.Globalization.CultureInfo.CurrentCulture.Name);
        Assert.Equal("en-US", System.Globalization.CultureInfo.CurrentUICulture.Name);
    }

    [Fact]
    public void The_constructor_does_not_touch_the_process_culture_and_SetLanguage_raises_Changed_on_a_switch_only()
    {
        using var scope = new CultureScope("en");
        var before = System.Globalization.CultureInfo.DefaultThreadCurrentCulture;
        var l = new I18n("de");
        Assert.Equal("de", l.Code);
        Assert.Equal("de-DE", l.Culture.Name);
        Assert.Same(before, System.Globalization.CultureInfo.DefaultThreadCurrentCulture);

        var changes = 0;
        l.Changed += () => changes++;
        l.SetLanguage("de-AT");
        Assert.Equal(0, changes);
        Assert.Equal("de-DE", System.Globalization.CultureInfo.DefaultThreadCurrentCulture?.Name); // the culture is (re)applied anyway
        l.SetLanguage("en");
        Assert.Equal(1, changes);
        l.SetLanguage("xx"); // unsupported -> English: no change
        Assert.Equal(1, changes);
        l.SetLanguage("ru");
        Assert.Equal(2, changes);
        Assert.Equal("ru", l.Code);
    }

    [Fact]
    public void Format_replaces_placeholders_with_values_in_the_catalog_culture()
    {
        using var scope = new CultureScope("en");
        var l = new I18n(Catalogs(("de", [("x.n", "{{n}} und {{n}} ({{name}}) {{unknown}}")])), "de");
        Assert.Equal("1,5 und 1,5 (Küche) {{unknown}}", l.Format("x.n", ("n", 1.5), ("name", "Küche")));
        Assert.Equal(" und  () {{unknown}}", l.Format("x.n", ("n", null), ("name", null)));

        var real = new I18n("en");
        Assert.Equal("73 of 100", real.Format("common.levelText", ("value", 73)));
        Assert.Equal("Pair with {{name}}", real.Format("pairing.title"));
        Assert.Equal("Receivers · AirPlay Bridge", real.Format("app.pageTitle", ("page", real["nav.receivers"])));
    }

    [Fact]
    public void Plural_picks_the_category_form_and_fills_count()
    {
        using var scope = new CultureScope("en");
        var l = new I18n("en");
        Assert.Equal("1 of 2 online", l.Plural("player.receiversOnline", 1, ("total", 2)));
        Assert.Equal("2 of 2 online", l.Plural("player.receiversOnline", 2, ("total", 2)));
        Assert.Equal("live (1 connection, events: 1,234)", l.Plural("diagnostics.liveUpdatesValue", 1, ("state", "live"), ("events", "1,234")));
        Assert.Equal("live (0 connections, events: 0)", l.Plural("diagnostics.liveUpdatesValue", 0, ("state", "live"), ("events", "0")));
        Assert.Equal("1,234 more", l.Plural("diagnostics.more", 1234));

        l.SetLanguage("de");
        Assert.Equal("1 von 2 online", l.Plural("player.receiversOnline", 1, ("total", 2)));
        Assert.Equal("live (1 Verbindung, Ereignisse: 3)", l.Plural("diagnostics.liveUpdatesValue", 1, ("state", "live"), ("events", "3")));
        Assert.Equal("live (5 Verbindungen, Ereignisse: 3)", l.Plural("diagnostics.liveUpdatesValue", 5, ("state", "live"), ("events", "3")));
        Assert.Equal("1.234 weitere", l.Plural("diagnostics.more", 1234));
        Assert.Equal("no.plural", l.Plural("no.plural", 3));
    }

    [Fact]
    public void Plural_falls_back_from_the_category_to_other_to_the_base_key_then_to_german()
    {
        var l = new I18n(
            Catalogs(
                ("de", [("p.full_one", "ein Ding"), ("p.full_other", "{{count}} Dinge"), ("p.deOnly_one", "eins"), ("p.deOnly_other", "{{count}} (de)")]),
                ("ru", [("p.full_one", "{{count}} one"), ("p.full_other", "{{count}} other"), ("p.base", "{{count}} base")])),
            "ru");
        Assert.Equal("21 one", l.Plural("p.full", 21));
        Assert.Equal("3 other", l.Plural("p.full", 3));   // "few" is missing -> other
        Assert.Equal("5 other", l.Plural("p.full", 5));   // "many" is missing -> other
        Assert.Equal("7 base", l.Plural("p.base", 7));    // no plural forms -> the base key
        Assert.Equal("eins", l.Plural("p.deOnly", 1));    // nothing in ru -> German with the German category
        Assert.Equal("21 (de)", l.Plural("p.deOnly", 21)); // ru would say "one"; German says other
        Assert.Equal("p.none", l.Plural("p.none", 2));
        Assert.Equal("ignored other", l.Plural("p.full", 4, ("count", "ignored"))); // a caller's own count wins
    }

    [Theory]
    [InlineData("de", "other,one,other,other,other,other,other,other,other,other")]
    [InlineData("en", "other,one,other,other,other,other,other,other,other,other")]
    [InlineData("es", "other,one,other,other,other,other,other,other,other,other")]
    [InlineData("fr", "one,one,other,other,other,other,other,other,other,other")]
    [InlineData("pt", "one,one,other,other,other,other,other,other,other,other")]
    [InlineData("ru", "many,one,few,many,many,one,few,many,one,many")]
    [InlineData("uk", "many,one,few,many,many,one,few,many,one,many")]
    public void Plural_categories_follow_cldr_for_the_sample_numbers(string code, string expected)
    {
        long[] samples = [0, 1, 2, 5, 11, 21, 22, 25, 101, 111];
        var actual = samples.Select(n => PluralRules.Select(code, n).ToString().ToLowerInvariant());
        Assert.Equal(expected.Split(','), actual);

        // Through I18n.Plural: with every form in the language's catalog, the chosen form is the category.
        (string, string)[] forms = [("k_one", "one"), ("k_few", "few"), ("k_many", "many"), ("k_other", "other")];
        var l = new I18n(code == "de" ? Catalogs(("de", forms)) : Catalogs(("de", []), (code, forms)), code);
        Assert.Equal(expected.Split(','), samples.Select(n => l.Plural("k", n)));
    }

    [Theory]
    [InlineData(2, "few")]
    [InlineData(12, "many")]
    [InlineData(14, "many")]
    [InlineData(104, "few")]
    [InlineData(112, "many")]
    [InlineData(1001, "one")]
    [InlineData(-21, "one")]
    [InlineData(long.MinValue, "many")] // …808
    public void Russian_and_ukrainian_edge_counts(long count, string expected)
    {
        Assert.Equal(expected, PluralRules.Select("ru", count).ToString().ToLowerInvariant());
        Assert.Equal(expected, PluralRules.Select("uk", count).ToString().ToLowerInvariant());
        Assert.Equal("_" + expected, PluralRules.Suffix(PluralRules.Select("uk", count)));
    }

    [Fact]
    public async Task Commands_compose_their_notices_from_the_catalog_in_the_current_language()
    {
        using var scope = new CultureScope("en");
        var (ctx, state, api, l, _) = UiTest.Create("de", respond: (_, _) => Task.FromResult(
            StubHandler.Problem(System.Net.HttpStatusCode.NotFound, "Not found", "No receiver x.")));
        await using var context = ctx;
        var commands = ctx.Services.GetRequiredService<BridgeCommands>();
        Assert.False(await commands.ConnectAsync("x", CancellationToken.None));
        Assert.Equal("Verbinden von x fehlgeschlagen. Not found: No receiver x.", Assert.Single(state.Notices).Text);

        l.SetLanguage("en");
        Assert.False(await commands.PlaybackAsync("pause", CancellationToken.None));
        Assert.Contains(state.Notices, n => n.Text == "Pausing failed. Not found: No receiver x.");
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = commands.PlaybackAsync("stop", CancellationToken.None); });
        Assert.Single(api.Requests, r => r.Path == "/api/playback" && r.Body!.Contains("\"pause\"", StringComparison.Ordinal)); // "stop" never left

        var invalid = await commands.SubmitPinAsync(new PairingPrompt("x", "t", "m", PairingPromptOrigin.User), " ", CancellationToken.None);
        Assert.Equal(new PinResult(PinOutcome.Invalid, "Enter the PIN the receiver shows."), invalid);
    }

    [Fact]
    public async Task Connect_names_a_failure_without_a_receiver_error_with_the_catalog_default()
    {
        using var scope = new CultureScope("de");
        var (ctx, state, api, _, _) = UiTest.Create("de", respond: (request, _) => Task.FromResult(
            request.Method == HttpMethod.Post
                ? StubHandler.Json(Dto.Receiver("k", "Küche", state: ReceiverConnectionState.Error, error: null))
                : StubHandler.Problem(System.Net.HttpStatusCode.NotFound, "Not found", "No stub answers this request.")));
        await using var context = ctx;
        Assert.True(await ctx.Services.GetRequiredService<BridgeCommands>().ConnectAsync("k", CancellationToken.None));
        Assert.Equal("/api/receivers/k/connect", Assert.Single(api.Requests).Path);
        Assert.Equal(ReceiverConnectionState.Error, state.Receiver("k")?.State);
        Assert.Equal("Küche: die Verbindung ist fehlgeschlagen.", Assert.Single(state.Notices).Text);
    }

    [Theory]
    [InlineData("play", "Starting playback")]
    [InlineData("pause", "Pausing")]
    [InlineData("next", "Skipping to the next track")]
    [InlineData("prev", "Going back to the previous track")]
    public async Task Playback_commands_name_their_action_in_a_failure_notice(string command, string what)
    {
        using var scope = new CultureScope("en");
        var (ctx, state, api, _, _) = UiTest.Create("en");
        await using var _ = ctx;
        Assert.False(await ctx.Services.GetRequiredService<BridgeCommands>().PlaybackAsync(command, CancellationToken.None));
        Assert.Equal($"{what} failed. Not found: No stub answers this request.", Assert.Single(state.Notices).Text);
        var request = Assert.Single(api.Requests);
        Assert.Equal("/api/playback", request.Path);
        Assert.Contains($"\"{command}\"", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_names_network_failures_from_the_catalog()
    {
        var de = new I18n("de");
        // The client's runtime text (in WASM "TypeError: Failed to fetch", parser messages) never reaches the screen (3.5).
        Assert.Equal("Die Bridge ist nicht erreichbar.", BridgeCommands.Describe(new HttpRequestException("TypeError: Failed to fetch"), de));
        Assert.Equal("The bridge is not reachable.", BridgeCommands.Describe(new HttpRequestException("TypeError: Failed to fetch")));
        Assert.Equal("Die Bridge hat nicht rechtzeitig geantwortet.", BridgeCommands.Describe(new TimeoutException(), de));
        Assert.Equal("The bridge did not answer in time.", BridgeCommands.Describe(new TimeoutException()));
        Assert.Equal("Die Antwort der Bridge war nicht lesbar.", BridgeCommands.Describe(new System.Text.Json.JsonException("'<' is an invalid start of a value."), de));
        Assert.Equal("The bridge's answer could not be read.", BridgeCommands.Describe(new InvalidOperationException("boom")));
    }

    [Theory]
    [InlineData("", "DeviceName", "settings.validation.nameEmpty")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345", "DeviceName", "settings.validation.nameTooLong")]
    [InlineData("1000", "LatencyFrames", "settings.validation.latencyRange")]
    [InlineData("Lossless", "PreferredStream", "settings.validation.stream")]
    public void Settings_validation_messages_are_catalog_keys_translated_when_a_catalog_is_offered(string value, string member, string key)
    {
        var model = new SettingsModel { DeviceName = "Bridge", LatencyFrames = 77175, PreferredStream = "Realtime" };
        switch (member)
        {
            case "DeviceName": model.DeviceName = value; break;
            case "LatencyFrames": model.LatencyFrames = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
            default: model.PreferredStream = value; break;
        }

        // Without a catalog: the key (a page renders L[message]).
        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, new(model), results, validateAllProperties: true));
        Assert.Equal(key, Assert.Single(results).ErrorMessage);

        // With the app's services (Blazor's DataAnnotationsValidator passes them): the text in the current language.
        var de = new I18n("de");
        var services = new ServiceCollection().AddSingleton(de).BuildServiceProvider();
        results.Clear();
        Assert.False(System.ComponentModel.DataAnnotations.Validator.TryValidateObject(model, new(model, services, null), results, validateAllProperties: true));
        Assert.Equal(de[key], Assert.Single(results).ErrorMessage);
        Assert.Equal(de[key], de[de[key]]); // a translated message passes L[...] unchanged
    }

    [Fact]
    public async Task State_pill_maps_receivers_first_match_wins_with_class_text_and_title()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, _) = UiTest.Create("en");
        await using (ctx)
        {
            string Render(ReceiverDto receiver)
            {
                var span = ctx.Render<StatePill>(p => p.Add(x => x.Receiver, receiver)).Find("span");
                Assert.False(string.IsNullOrEmpty(span.GetAttribute("title")));
                return $"{span.ClassName}|{span.TextContent}";
            }

            Assert.Equal("pill|offline", Render(Dto.Receiver("a", "A", online: false, state: ReceiverConnectionState.Streaming)));
            Assert.Equal("pill pill--ok|playing", Render(Dto.Receiver("a", "A", state: ReceiverConnectionState.Streaming, pairing: PairingState.PinRequired)));
            Assert.Equal("pill pill--ok|connected", Render(Dto.Receiver("a", "A", state: ReceiverConnectionState.Connected)));
            Assert.Equal("pill pill--accent|connecting…", Render(Dto.Receiver("a", "A", state: ReceiverConnectionState.Connecting)));
            Assert.Equal("pill pill--err|error", Render(Dto.Receiver("a", "A", state: ReceiverConnectionState.Error, error: "refused", pairing: PairingState.PinRequired)));
            Assert.Equal("pill pill--warn|PIN needed", Render(Dto.Receiver("a", "A", pairing: PairingState.PinRequired)));
            Assert.Equal("pill|HomeKit only", Render(Dto.Receiver("a", "A", pairing: PairingState.Disabled)));
            Assert.Equal("pill pill--ok|online", Render(Dto.Receiver("a", "A", pairing: PairingState.Paired)));

            var error = ctx.Render<StatePill>(p => p.Add(x => x.Receiver, Dto.Receiver("a", "A", state: ReceiverConnectionState.Error, error: "refused"))).Find("span");
            Assert.Equal("The last attempt failed: refused", error.GetAttribute("title"));
            var noDetail = ctx.Render<StatePill>(p => p.Add(x => x.Receiver, Dto.Receiver("a", "A", state: ReceiverConnectionState.Error, error: null))).Find("span");
            Assert.Equal("pill pill--err|error", $"{noDetail.ClassName}|{noDetail.TextContent}");
            Assert.Equal("The last attempt failed: the connection failed.", noDetail.GetAttribute("title")); // BridgeCommands' default
            var online = ctx.Render<StatePill>(p => p.Add(x => x.Receiver, Dto.Receiver("a", "A"))).Find("span");
            Assert.Equal(l["pills.title.online"], online.GetAttribute("title"));

            l.SetLanguage("de");
            Assert.Equal("pill pill--warn|PIN nötig", Render(Dto.Receiver("a", "A", pairing: PairingState.PinRequired)));
        }
    }

    [Fact]
    public async Task State_pill_maps_groups_with_the_members_receivers_from_the_state()
    {
        using var scope = new CultureScope("en");
        var (ctx, state, _, _, _) = UiTest.Create("en");
        await using (ctx)
        {
            state.ApplyReceiver(Dto.Receiver("a", "A"));
            state.ApplyReceiver(Dto.Receiver("pin", "Pin", pairing: PairingState.PinRequired));
            state.ApplyReceiver(Dto.Receiver("off", "Off", online: false));
            string Render(GroupDto group)
            {
                var span = ctx.Render<StatePill>(p => p.Add(x => x.Group, group)).Find("span");
                Assert.False(string.IsNullOrEmpty(span.GetAttribute("title")));
                return $"{span.ClassName}|{span.TextContent}";
            }

            var streaming = new GroupDto("g", "G", 50, true, [new GroupMemberDto("a", "A", 50, ReceiverConnectionState.Streaming)]);
            Assert.Equal("pill pill--ok|playing", Render(streaming));
            Assert.Equal("pill pill--accent|active", Render(Dto.Group("g", "G", 50, true, ("a", "A", 50))));
            Assert.Equal("pill pill--accent|active", Render(Dto.Group("g", "G", 50, true)));
            Assert.Equal("pill|empty", Render(Dto.Group("g", "G", 50, false)));
            Assert.Equal("pill pill--warn|PIN needed", Render(Dto.Group("g", "G", 50, false, ("a", "A", 50), ("pin", "Pin", 50))));
            Assert.Equal("pill|offline", Render(new GroupDto("g", "G", 50, false, [new GroupMemberDto("off", "Off", 50, ReceiverConnectionState.Available), new GroupMemberDto("gone", "Gone", 50, ReceiverConnectionState.Offline)])));
            Assert.Equal("pill pill--ok|ready", Render(Dto.Group("g", "G", 50, false, ("off", "Off", 50), ("a", "A", 50))));
            Assert.Equal("pill pill--ok|ready", Render(Dto.Group("g", "G", 50, false, ("unknown", "Unknown", 50)))); // not in the state, member state Available
        }
    }

    [Fact]
    public async Task State_pill_needs_exactly_one_of_receiver_and_group()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, _, _) = UiTest.Create();
        await using (ctx)
        {
            Assert.Throws<InvalidOperationException>(() => ctx.Render<StatePill>());
            Assert.Throws<InvalidOperationException>(() => ctx.Render<StatePill>(p => p
                .Add(x => x.Receiver, Dto.Receiver("a", "A"))
                .Add(x => x.Group, Dto.Group("g", "G", 50, false))));
        }
    }

    [Fact]
    public async Task Language_select_bar_lists_the_languages_and_switches_catalog_storage_and_culture()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, module) = UiTest.Create("en");
        await using (ctx)
        {
            var cut = ctx.Render<LanguageSelect>();
            var select = cut.Find("select");
            Assert.Equal("lang-select", select.Id);
            Assert.Equal("lang-select", select.ClassName);
            Assert.Equal("Language", select.GetAttribute("aria-label"));
            Assert.Equal("Language", select.GetAttribute("title"));
            Assert.Equal(I18n.SupportedCultures, cut.FindAll("option").Select(o => o.GetAttribute("value")));
            Assert.Equal(I18n.Languages.Select(x => x.NativeName), cut.FindAll("option").Select(o => o.TextContent));
            Assert.Equal("en", Assert.Single(cut.FindAll("option[selected]")).GetAttribute("value"));

            var changes = 0;
            l.Changed += () => changes++;
            select.Change("de");
            Assert.Equal("de", l.Code);
            Assert.Equal(1, changes);
            // The handler ran in the renderer's flow; the default-thread culture is what every later render reads.
            Assert.Equal("de-DE", System.Globalization.CultureInfo.DefaultThreadCurrentCulture?.Name);
            Assert.Equal("de", Assert.Single(module.Invocations["setLang"]).Arguments[0]);
            cut.WaitForAssertion(() => Assert.Equal("Sprache", cut.Find("select").GetAttribute("aria-label")));
            Assert.Equal("de", Assert.Single(cut.FindAll("option[selected]")).GetAttribute("value"));
        }
    }

    [Fact]
    public async Task Language_select_switches_even_when_storing_the_choice_fails()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, module) = UiTest.Create("en");
        await using (ctx)
        {
            module.SetupVoid("setLang", _ => true).SetException(new Microsoft.JSInterop.JSException("storage blocked"));
            var cut = ctx.Render<LanguageSelect>();
            cut.Find("select").Change("de");
            Assert.Equal("de", l.Code);
            Assert.Equal("de", Assert.Single(module.Invocations["setLang"]).Arguments[0]);
            cut.WaitForAssertion(() => Assert.Equal("Sprache", cut.Find("select").GetAttribute("aria-label")));
            Assert.Equal("de", Assert.Single(cut.FindAll("option[selected]")).GetAttribute("value"));
        }
    }

    [Fact]
    public async Task Language_select_field_variant_is_an_input_named_by_the_callers_label()
    {
        using var scope = new CultureScope("de");
        var (ctx, _, _, l, _) = UiTest.Create("de");
        await using (ctx)
        {
            var cut = ctx.Render<LanguageSelect>(p => p.Add(x => x.Variant, LanguageSelect.Field).Add(x => x.Id, "settings-language"));
            var select = cut.Find("select");
            Assert.Equal("settings-language", select.Id);
            Assert.Equal("input", select.ClassName);
            Assert.False(select.HasAttribute("aria-label"));
            Assert.False(select.HasAttribute("title"));
            Assert.Equal("de", Assert.Single(cut.FindAll("option[selected]")).GetAttribute("value"));

            // Switched elsewhere (the top-bar select): this one follows.
            l.SetLanguage("uk");
            cut.WaitForAssertion(() => Assert.Equal("uk", Assert.Single(cut.FindAll("option[selected]")).GetAttribute("value")));
            Assert.Throws<InvalidOperationException>(() => ctx.Render<LanguageSelect>(p => p.Add(x => x.Variant, "Dropdown")));
        }
    }

    [Fact]
    public async Task Bridge_components_rerender_on_a_language_switch_and_stop_after_disposal()
    {
        using var scope = new CultureScope("en");
        var (ctx, _, _, l, _) = UiTest.Create("en");
        await using (ctx)
        {
            var cut = ctx.Render<LanguageProbe>();
            Assert.Equal("Receivers", cut.Find("p").TextContent);
            l.SetLanguage("de");
            cut.WaitForAssertion(() => Assert.Equal("Empfänger", cut.Find("p").TextContent));
            Assert.Equal(1, cut.Instance.LanguageChanges);

            cut.Instance.Dispose();
            l.SetLanguage("en"); // unsubscribed: no callback into a disposed component
            Assert.Equal(1, cut.Instance.LanguageChanges);
        }
    }

    private static HashSet<string> Placeholders(string text) => [.. Placeholder().Matches(text).Select(m => m.Groups[1].Value)];

    /// <summary>The plural forms a catalog of <paramref name="code"/> must carry (UI-SPEC 3.6): its CLDR integer categories.</summary>
    private static string[] RequiredPluralSuffixes(string code) => code is "ru" or "uk"
        ? ["_one", "_few", "_many", "_other"]
        : ["_one", "_other"];

    // ICU versions differ in the grouping and percent spaces (U+00A0, U+202F or a plain space); compare with plain spaces.
    private static string Spaces(string text) => text.Replace(' ', ' ').Replace(' ', ' ');

    private static Dictionary<string, IReadOnlyDictionary<string, string>> Catalogs(params (string Code, (string Key, string Text)[] Entries)[] catalogs) =>
        catalogs.ToDictionary(c => c.Code, c => (IReadOnlyDictionary<string, string>)c.Entries.ToDictionary(e => e.Key, e => e.Text, StringComparer.Ordinal), StringComparer.Ordinal);

    /// <summary>A minimal <see cref="BridgeComponentBase"/> that prints one catalog text.</summary>
    private sealed class LanguageProbe : BridgeComponentBase
    {
        public int LanguageChanges { get; private set; }

        protected override void OnLanguageChanged() => LanguageChanges++;

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddContent(1, L["nav.receivers"]);
            builder.CloseElement();
        }
    }
}
