using System.Text.RegularExpressions;
using Bridge.Web.Localization;
using Xunit;

namespace Bridge.Tests.Web;

/// <summary>
/// Static checks between the sources of <c>src/Bridge.Web</c> and the source catalog <c>de.json</c> (docs/DEVELOPMENT.md, "Static checks";
/// UI-SPEC 10): every key-shaped literal used as an <see cref="I18n"/> argument (<c>L["…"]</c>, <c>L.Format("…"</c>,
/// <c>L.Plural("…"</c>, the same on <c>_l</c>/<c>l</c>/<c>i18n</c>) or as a validation <c>ErrorMessage</c> exists in
/// <c>de.json</c>, and every <c>de.json</c> key appears as a literal somewhere in those sources (plural keys through
/// their base key). Keys are literal strings by rule (docs/DEVELOPMENT.md, "Translations"); an interpolated key is a failure of its own.
/// </summary>
public sealed partial class CatalogUsageTests
{
    /// <summary>
    /// Catalog keys that no source has to use. Empty by design: UI-SPEC 10 names a consumer for every key, so a key
    /// without one is either dead (remove it from all seven catalogs) or a page that does not render it yet (a bug).
    /// </summary>
    private static readonly string[] UnusedAllowList = [];

    private static readonly string[] PluralSuffixes = ["_one", "_few", "_many", "_other"];

    [GeneratedRegex(@"^[a-z]+(\.[A-Za-z0-9]+)+$")]
    private static partial Regex KeyShape();

    /// <summary>
    /// A catalog call with a literal first argument: <c>L["key"]</c>, <c>L.Format("key", …)</c>, <c>L.Plural("key", …)</c>
    /// on the names the app gives its <see cref="I18n"/> (<c>L</c> in components, <c>_l</c>/<c>l</c> in services). The
    /// argument may start on the next line (<c>L.Plural(\n "key",</c>).
    /// </summary>
    [GeneratedRegex(@"(?<![\w.])(?:L|l|_l|i18n|_i18n)\s*(?:\[|\.(?<method>Format|Plural)\s*\()\s*""(?<key>[^""\r\n]*)""")]
    private static partial Regex CatalogCall();

    /// <summary>A validation attribute's message, which the Settings page translates (UI-SPEC 4.6).</summary>
    [GeneratedRegex(@"\bErrorMessage\s*=\s*""(?<key>[^""\r\n]*)""")]
    private static partial Regex ErrorMessage();

    /// <summary>A key built at runtime: <c>L[$"pills.{kind}"]</c>, <c>L.Format($"…"</c> (forbidden, docs/DEVELOPMENT.md, "Translations").</summary>
    [GeneratedRegex(@"(?<![\w.])(?:L|l|_l|i18n|_i18n)\s*(?:\[|\.(?:Format|Plural)\s*\()\s*(?:\$|@\$|\$@)""")]
    private static partial Regex InterpolatedKey();

    [GeneratedRegex(@"@\*.*?\*@|/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex BlockComment();

    [GeneratedRegex(@"^\s*//.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();

    [Fact]
    public void The_scanner_sees_the_sources_and_every_call_shape()
    {
        var sources = Sources();
        Assert.Contains(sources, s => s.Path.EndsWith("BridgeCommands.cs", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Path.EndsWith("MainLayout.razor", StringComparison.Ordinal));
        Assert.DoesNotContain(sources, s => s.Path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        var uses = Uses(sources);
        Assert.True(uses.Count > 250, $"only {uses.Count} catalog calls found");
        Assert.Contains(uses, u => u.Key == "settings.validation.nameEmpty" && u.Method == "ErrorMessage");
        Assert.Contains(uses, u => u.Key == "diagnostics.liveUpdatesValue" && u.Method == "Plural"); // argument on the next line
        Assert.Contains(uses, u => u.Key == "notices.failed" && u.Method == "Format");                // _l in BridgeCommands
        Assert.Contains(uses, u => u.Key == "pills.playing" && u.Method == "Indexer");

        // The shapes themselves, on samples.
        Assert.Equal(["a.b", "c.d", "e.f", "g.h"], CatalogCall().Matches("@L[\"a.b\"] L.Format(\"c.d\", x) _l.Plural(\n    \"e.f\", 2) l[ \"g.h\" ] Browser.MeasureAsync(\"nav.tabbar\") xL[\"no.match\"] a.L[\"no.match\"]").Select(m => m.Groups["key"].Value));
        Assert.Matches(InterpolatedKey(), "L[$\"pills.{kind}\"]");
        Assert.DoesNotMatch(InterpolatedKey(), "L[\"pills.playing\"]");
        Assert.Equal("x = \"kept\"; \n", Strip("x = \"kept\"; @* \"gone.key\" *@/* \"gone.too\" */\n  // \"gone.line\""));
    }

    [Fact]
    public void Every_key_the_sources_ask_for_exists_in_the_source_catalog()
    {
        var de = new I18n().Keys(I18n.SourceCulture);
        var missing = Uses(Sources())
            .Where(u => KeyShape().IsMatch(u.Key))
            .Where(u => u.Method == "Plural"
                ? !de.ContainsKey(u.Key + "_other") && !de.ContainsKey(u.Key)
                : !de.ContainsKey(u.Key))
            .Select(u => $"{u.Key} ({u.Method}, {u.File})")
            .Distinct()
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(missing.Count == 0, "Keys used in src/Bridge.Web but missing from de.json:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Every_catalog_argument_is_a_key_shaped_literal()
    {
        var sources = Sources();
        var notKeys = Uses(sources)
            .Where(u => !KeyShape().IsMatch(u.Key))
            .Select(u => $"\"{u.Key}\" ({u.Method}, {u.File})")
            .ToList();
        Assert.True(notKeys.Count == 0, "Catalog calls whose literal is not a key:\n" + string.Join("\n", notKeys));

        var interpolated = sources
            .SelectMany(s => InterpolatedKey().Matches(s.Text).Select(m => $"{s.File}: {m.Value}"))
            .ToList();
        Assert.True(interpolated.Count == 0, "Catalog keys built by interpolation (use a switch over literal keys):\n" + string.Join("\n", interpolated));
    }

    [Fact]
    public void Every_key_of_the_source_catalog_is_used_somewhere()
    {
        Assert.Empty(UnusedAllowList);
        var text = string.Concat(Sources().Select(s => s.Text));
        var unused = new I18n().Keys(I18n.SourceCulture).Keys
            .Select(BaseKey)
            .Distinct()
            .Where(key => !text.Contains('"' + key + '"', StringComparison.Ordinal))
            .Except(UnusedAllowList)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.True(unused.Count == 0, "de.json keys that no source in src/Bridge.Web uses:\n" + string.Join("\n", unused));
    }

    [Fact]
    public void Plural_keys_are_used_through_Plural_and_plain_keys_never_are()
    {
        var de = new I18n().Keys(I18n.SourceCulture);
        var pluralBases = de.Keys.Where(IsPluralForm).Select(BaseKey).ToHashSet(StringComparer.Ordinal);
        var wrong = Uses(Sources())
            .Where(u => KeyShape().IsMatch(u.Key))
            .Where(u => (u.Method == "Plural") != pluralBases.Contains(u.Key))
            .Select(u => $"{u.Key} ({u.Method}, {u.File})")
            .Distinct()
            .ToList();
        Assert.True(wrong.Count == 0, "Plural keys must go through L.Plural with their base key, other keys never:\n" + string.Join("\n", wrong));
    }

    private static bool IsPluralForm(string key) => PluralSuffixes.Any(suffix => key.EndsWith(suffix, StringComparison.Ordinal));

    private static string BaseKey(string key)
    {
        foreach (var suffix in PluralSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return key[..^suffix.Length];
            }
        }

        return key;
    }

    private static List<Use> Uses(IEnumerable<Source> sources) =>
    [
        .. sources.SelectMany(s => CatalogCall().Matches(s.Text)
            .Select(m => new Use(m.Groups["key"].Value, m.Groups["method"].Success ? m.Groups["method"].Value : "Indexer", s.File))
            .Concat(ErrorMessage().Matches(s.Text).Select(m => new Use(m.Groups["key"].Value, "ErrorMessage", s.File)))),
    ];

    /// <summary>Every <c>.razor</c> and <c>.cs</c> file of <c>src/Bridge.Web</c> (not bin/obj), comments stripped.</summary>
    private static List<Source> Sources()
    {
        var root = WebRoot();
        return
        [
            .. Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".cs", StringComparison.Ordinal))
                .Where(path =>
                {
                    var relative = Path.GetRelativePath(root, path);
                    return !relative.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        && !relative.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                })
                .Order(StringComparer.Ordinal)
                .Select(path => new Source(path, Path.GetRelativePath(root, path).Replace('\\', '/'), Strip(File.ReadAllText(path)))),
        ];
    }

    /// <summary>
    /// Removes Razor (<c>@* *@</c>) and C# block comments and whole-line <c>//</c> comments (doc comments included), so
    /// a key mentioned only in a comment does not count as used. Trailing <c>//</c> comments stay (a URL in a string
    /// looks the same); the sources keep keys out of them.
    /// </summary>
    private static string Strip(string text) => LineComment().Replace(BlockComment().Replace(text, string.Empty), string.Empty);

    /// <summary>The <c>src/Bridge.Web</c> directory of the repository (also used by the other source checks).</summary>
    internal static string WebRoot()
    {
        // The build output first (the usual layout), then this source file (an --artifacts-path build outside the tree).
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory(), Path.GetDirectoryName(ThisFile())! })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "Bridge.Web");
                if (File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")) && Directory.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException("src/Bridge.Web not found above the test directory");
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private sealed record Source(string Path, string File, string Text);

    private sealed record Use(string Key, string Method, string File);
}
