using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Bridge.Web.Components;
using Bunit;
using Microsoft.AspNetCore.Components;
using Xunit;
using Xunit.Abstractions;

namespace Bridge.Tests.Web;

/// <summary>
/// Static checks of <c>src/Bridge.Web/wwwroot/css/app.css</c> (WORK-PLAN 2.4, UI-SPEC 1.1, 1.2, 5.1, 6.3): the theme
/// pattern, the token set, the media-query list, the hover guard, no raw status colours as text, and the WCAG contrast
/// table recomputed from the tokens with minimum semantics.
/// </summary>
public sealed partial class DesignTokenTests(ITestOutputHelper output)
{
    private const string DarkMediaQuery = "(prefers-color-scheme: dark)";
    private const string DarkMediaSelector = ":root:not([data-theme=\"light\"])";
    private const string DarkChoiceSelector = ":root[data-theme=\"dark\"]";
    private const string LightChoiceSelector = ":root[data-theme=\"light\"]";

    /// <summary>UI-SPEC 1.1: every token, and whether the table gives it a dark value of its own.</summary>
    private static readonly (string Name, bool HasDark)[] SpecTokens =
    [
        ("--bg", true), ("--surface", true), ("--surface-2", true), ("--line", true), ("--line-strong", true),
        ("--text", true), ("--muted", true), ("--accent", true), ("--accent-ink", true), ("--accent-soft", true),
        ("--accent-contrast", true), ("--ok", true), ("--ok-ink", true), ("--warn", true), ("--warn-ink", true),
        ("--err", true), ("--err-ink", true), ("--ok-fill", true), ("--warn-fill", true), ("--err-fill", true),
        ("--info-fill", true), ("--shadow", true), ("--shadow-lift", true), ("--radius", false), ("--radius-sm", false),
        ("--radius-pill", false), ("--font", false), ("--font-mono", false), ("--topbar-h", false), ("--tabbar-h", false),
    ];

    /// <summary>UI-SPEC 5.1: the complete list of media queries app.css may contain.</summary>
    private static readonly string[] AllowedMediaQueries =
    [
        "(max-width: 699px)",
        "(min-width: 700px)",
        "(min-width: 1024px)",
        "(max-width: 359px)",
        "(max-width: 759px)",
        "(max-height: 500px)",
        "(max-width: 699px), (pointer: coarse)",
        "(hover: hover)",
        "(prefers-reduced-motion: reduce)",
        "(prefers-color-scheme: dark)",
    ];

    private static readonly Lazy<Stylesheet> s_css = new(() => Stylesheet.Parse(File.ReadAllText(CssPath())));

    private static Stylesheet Css => s_css.Value;

    [Fact]
    public void Dark_blocks_are_identical_and_follow_the_theme_pattern()
    {
        var media = Css.Rules.Where(r => r.Selector == DarkMediaSelector && r.Media.SequenceEqual([DarkMediaQuery])).ToList();
        var choice = Css.Rules.Where(r => r.Selector == DarkChoiceSelector && r.Media.Count == 0).ToList();
        var lightChoice = Css.Rules.Where(r => r.Selector == LightChoiceSelector && r.Media.Count == 0).ToList();
        var root = Css.Rules.Where(r => r.Selector == ":root" && r.Media.Count == 0).ToList();

        Assert.Single(media);
        Assert.Single(choice);
        Assert.Single(lightChoice);
        Assert.Single(root);

        Assert.Equal("light dark", root[0].Value("color-scheme"));
        Assert.Equal([new Declaration("color-scheme", "light")], lightChoice[0].Declarations);

        // The explicit dark choice = color-scheme: dark + exactly the lines of the media block, in the same order.
        Assert.Equal("dark", choice[0].Value("color-scheme"));
        var choiceTokens = choice[0].Declarations.Where(d => d.Name != "color-scheme").ToList();
        Assert.All(media[0].Declarations, d => Assert.StartsWith("--", d.Name, StringComparison.Ordinal));
        Assert.Equal(media[0].Declarations, choiceTokens);
        Assert.NotEmpty(choiceTokens);

        // Nothing else redefines a :root token: no fourth place where a theme value could drift. (Component-local
        // custom properties such as .level's --fill-color are not tokens and may be set by any rule.)
        var tokenNames = root[0].Declarations.Select(d => d.Name).Where(n => n.StartsWith("--", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var redefinitions = Css.Rules
            .Where(r => r != root[0] && r != media[0] && r != choice[0])
            .SelectMany(r => r.Declarations.Where(d => tokenNames.Contains(d.Name)).Select(d => $"{r.Selector} {{ {d.Name} }}"))
            .ToList();
        Assert.True(redefinitions.Count == 0, "Tokens redefined outside the token blocks: " + string.Join(" | ", redefinitions));
    }

    [Fact]
    public void Every_spec_token_is_declared_in_the_light_block_and_the_dark_ones_in_both_dark_blocks()
    {
        var light = Css.Rules.Single(r => r.Selector == ":root" && r.Media.Count == 0);
        var media = Css.Rules.Single(r => r.Selector == DarkMediaSelector);
        var choice = Css.Rules.Single(r => r.Selector == DarkChoiceSelector);

        foreach (var (name, hasDark) in SpecTokens)
        {
            Assert.True(light.Value(name) is not null, $"{name} is missing in :root");
            if (hasDark)
            {
                Assert.True(media.Value(name) is not null, $"{name} is missing in the prefers-color-scheme dark block");
                Assert.True(choice.Value(name) is not null, $"{name} is missing in :root[data-theme=\"dark\"]");
            }
        }

        // Every dark token overrides a light one (a token is added to both themes or to neither).
        foreach (var declaration in media.Declarations)
        {
            Assert.True(light.Value(declaration.Name) is not null, $"{declaration.Name} is dark-only");
        }

        Assert.Equal("#2f6df6", light.Value("--accent"));
        Assert.Equal("#4c8dff", media.Value("--accent"));
    }

    [Fact]
    public void Media_queries_are_the_ones_of_the_spec_and_reduced_motion_comes_last()
    {
        var used = Css.MediaQueries.Distinct(StringComparer.Ordinal).ToList();
        var unexpected = used.Except(AllowedMediaQueries, StringComparer.Ordinal).ToList();
        Assert.True(unexpected.Count == 0, "Media queries outside UI-SPEC 5.1: " + string.Join(" | ", unexpected));
        Assert.Equal("(prefers-reduced-motion: reduce)", Css.MediaQueries[^1]);
        Assert.Contains("(max-width: 699px), (pointer: coarse)", used);
        Assert.Contains("(hover: hover)", used);
    }

    [Fact]
    public void No_rule_reorders_flex_or_grid_children_so_the_tab_order_is_the_visual_order()
    {
        // WCAG 1.3.2 / 2.4.3: a visual order that differs from the DOM order makes Tab and a screen reader jump (the
        // phone member rows of the group editor once moved the slider below the remove button with `order`).
        var offenders = Css.Rules
            .SelectMany(r => r.Declarations.Where(d => d.Name == "order").Select(d => $"{r.Selector} {{ order: {d.Value} }}"))
            .ToList();
        Assert.True(offenders.Count == 0, "order declarations: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void Every_hover_rule_is_guarded_by_hover_hover()
    {
        var unguarded = Css.Rules
            .Where(r => HoverPseudo().IsMatch(r.Selector))
            .Where(r => !r.Media.Contains("(hover: hover)"))
            .Select(r => r.Selector)
            .ToList();
        Assert.True(unguarded.Count == 0, "Hover rules outside @media (hover: hover): " + string.Join(" | ", unguarded));
    }

    [Fact]
    public void Unguarded_not_hover_selectors_never_apply_on_touch()
    {
        // The one exemption of the hover guard above is the theme picker's desktop collapse rule, ":not(:hover)": it
        // must never match on a touch screen, where a tap leaves :hover stuck (REVIEW-2 #1). Every selector that uses
        // it therefore excludes the touch class, and each one has a touch twin without :hover in the same rule.
        var rules = Css.Rules.Where(r => r.Selector.Contains(":not(:hover)", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(rules);
        foreach (var rule in rules)
        {
            var selectors = rule.Selector.Split(',').Select(x => x.Trim()).ToList();
            var desktop = selectors.Where(x => x.Contains(":not(:hover)", StringComparison.Ordinal)).ToList();
            Assert.All(desktop, x => Assert.StartsWith(".theme-picker:not(.theme-picker--touch):not(:hover)", x, StringComparison.Ordinal));
            Assert.Contains(selectors, x => x.StartsWith(".theme-picker--touch:not(:focus-within):not(.theme-picker--open)", StringComparison.Ordinal)
                                            && !x.Contains("hover", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Raw_status_colours_are_never_used_as_text_colour()
    {
        var offenders = new List<string>();
        foreach (var rule in Css.Rules)
        {
            foreach (var declaration in rule.Declarations)
            {
                if (declaration.Name == "color" && RawColourValue().IsMatch(declaration.Value)
                    && !rule.Selector.Split(',').All(s => s.Contains(".theme-btn.is-active", StringComparison.Ordinal)))
                {
                    offenders.Add($"{rule.Selector} {{ color: {declaration.Value} }}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Raw --ok/--warn/--err/--accent as text colour: " + string.Join(" | ", offenders));
    }

    [Fact]
    public void No_colour_literal_outside_the_token_blocks()
    {
        var offenders = Css.Rules
            .Where(r => !r.Selector.StartsWith(":root", StringComparison.Ordinal))
            .SelectMany(r => r.Declarations.Where(d => ColourLiteral().IsMatch(d.Value)).Select(d => $"{r.Selector} {{ {d.Name}: {d.Value} }}"))
            .ToList();
        Assert.True(offenders.Count == 0, "Colour literals outside the token blocks: " + string.Join(" | ", offenders));
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Contrast_table_meets_the_minimums(string theme)
    {
        var tokens = Tokens(theme);
        var failures = new List<string>();

        foreach (var row in ContrastRows())
        {
            var backdrop = Resolve(tokens, row.Background);
            if (row.Over is { } over)
            {
                backdrop = backdrop.Over(Resolve(tokens, over));
            }

            var foreground = Resolve(tokens, row.Foreground).Over(backdrop);
            var ratio = Contrast(foreground, backdrop);
            var floor = row.Kind switch { Kind.Text => 4.5, Kind.NonText => 3.0, _ => 0.0 };
            output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"{theme,-5} {row.Foreground} on {row.Background}{(row.Over is null ? "" : " over " + row.Over)}: {ratio:0.00} ({row.Kind}{(floor > 0 ? ", floor " + floor.ToString("0.0", CultureInfo.InvariantCulture) : ", report only")})"));
            if (ratio < floor)
            {
                failures.Add(string.Create(CultureInfo.InvariantCulture, $"{row.Foreground} on {row.Background}: {ratio:0.00} < {floor:0.0}"));
            }
        }

        Assert.True(failures.Count == 0, $"{theme}: " + string.Join(" | ", failures));
    }

    [Fact]
    public void Compositing_and_contrast_math_matches_known_values()
    {
        // WCAG extremes and the spec's composited accent-soft example (rgba(47,109,246,.12) over #ffffff).
        Assert.Equal(21.0, Contrast(Rgba.Parse("#000000"), Rgba.Parse("#ffffff")), 2);
        Assert.Equal(1.0, Contrast(Rgba.Parse("#777777"), Rgba.Parse("#777777")), 2);
        var soft = Rgba.Parse("rgba(47, 109, 246, .12)").Over(Rgba.Parse("#ffffff"));
        Assert.Equal((230, 237, 254), (soft.R, soft.G, soft.B)); // 0.12 * 109 + 0.88 * 255 = 237.48
        Assert.Equal(4.53, Contrast(Rgba.Parse("#ffffff"), Rgba.Parse("#2f6df6")), 1);
    }

    private enum Kind
    {
        Text,
        NonText,
        Report,
    }

    private sealed record ContrastRow(string Foreground, string Background, Kind Kind, string? Over = null);

    /// <summary>UI-SPEC 6.3, one row per pair (backdrops of translucent tokens named in <see cref="ContrastRow.Over"/>).</summary>
    private static IEnumerable<ContrastRow> ContrastRows()
    {
        string[] surfaces = ["--bg", "--surface", "--surface-2"];
        string[] fills = ["--ok-fill", "--warn-fill", "--err-fill", "--info-fill"];

        foreach (var s in surfaces)
        {
            yield return new("--text", s, Kind.Text);
            yield return new("--muted", s, Kind.Text);
            yield return new("--accent-ink", s, Kind.Text);
            yield return new("--ok-ink", s, Kind.Text);
            yield return new("--warn-ink", s, Kind.Text);
            yield return new("--err-ink", s, Kind.Text);
        }

        yield return new("--muted", "--accent-soft", Kind.Text, Over: "--surface");
        yield return new("--accent-ink", "--accent-soft", Kind.Text, Over: "--surface");
        yield return new("--accent-contrast", "--accent", Kind.Text);
        yield return new("--ok-ink", "--ok-fill", Kind.Text);
        yield return new("--warn-ink", "--warn-fill", Kind.Text);
        yield return new("--err-ink", "--err-fill", Kind.Text);
        yield return new("--accent-ink", "--info-fill", Kind.Text);
        yield return new("--danger-fg", "--danger-bg", Kind.Text); // white on --err-ink (light) / --accent-contrast on --err (dark)
        foreach (var f in fills)
        {
            yield return new("--text", f, Kind.Text);
            yield return new("--muted", f, Kind.Text);
        }

        yield return new("--accent", "--surface", Kind.NonText);   // slider fill, radio dot
        yield return new("--accent", "--surface-2", Kind.NonText); // slider fill on its track
        yield return new("--accent", "--bg", Kind.NonText);        // focus ring
        yield return new("--accent", "--accent-soft", Kind.NonText, Over: "--surface-2"); // .theme-btn.is-active icon
        yield return new("--on-accent", "--accent", Kind.NonText); // brand glyph, placeholder note
        yield return new("--line-strong", "--surface", Kind.Report); // decorative control borders: printed only
    }

    private static Dictionary<string, string> Tokens(string theme)
    {
        var tokens = Css.Rules.Single(r => r.Selector == ":root" && r.Media.Count == 0).Declarations
            .Where(d => d.Name.StartsWith("--", StringComparison.Ordinal))
            .ToDictionary(d => d.Name, d => d.Value, StringComparer.Ordinal);
        if (theme == "dark")
        {
            foreach (var d in Css.Rules.Single(r => r.Selector == DarkChoiceSelector).Declarations.Where(d => d.Name.StartsWith("--", StringComparison.Ordinal)))
            {
                tokens[d.Name] = d.Value;
            }
        }

        return tokens;
    }

    private static Rgba Resolve(Dictionary<string, string> tokens, string name)
    {
        var value = name.StartsWith("--", StringComparison.Ordinal) ? $"var({name})" : name;
        for (var depth = 0; depth < 8; depth++)
        {
            var reference = VarReference().Match(value);
            if (!reference.Success)
            {
                return Rgba.Parse(value);
            }

            value = tokens.TryGetValue(reference.Groups[1].Value, out var next)
                ? next
                : throw new InvalidOperationException($"Unknown token {reference.Groups[1].Value}");
        }

        throw new InvalidOperationException($"Token {name} does not resolve to a colour");
    }

    private static double Contrast(Rgba a, Rgba b)
    {
        var la = a.Luminance;
        var lb = b.Luminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string CssPath()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "Bridge.Web", "wwwroot", "css", "app.css");
                if (File.Exists(Path.Combine(dir.FullName, "AirplayBridge.sln")) && File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        throw new FileNotFoundException("src/Bridge.Web/wwwroot/css/app.css not found above the test directory");
    }

    // ":hover" that is not the ":not(:hover)" of the theme picker's collapse rule.
    [GeneratedRegex(@"(?<!:not\():hover")]
    private static partial Regex HoverPseudo();

    [GeneratedRegex(@"^var\(\s*--(ok|warn|err|accent)\s*\)$")]
    private static partial Regex RawColourValue();

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(")]
    private static partial Regex ColourLiteral();

    [GeneratedRegex(@"^\s*var\(\s*(--[\w-]+)\s*\)\s*$")]
    private static partial Regex VarReference();

    /// <summary>An sRGB colour with alpha, channels 0-255.</summary>
    private readonly partial record struct Rgba(int R, int G, int B, double A)
    {
        public double Luminance => (0.2126 * Linear(R)) + (0.7152 * Linear(G)) + (0.0722 * Linear(B));

        public static Rgba Parse(string value)
        {
            value = value.Trim();
            var hex = Hex().Match(value);
            if (hex.Success)
            {
                var h = hex.Groups[1].Value;
                if (h.Length == 3)
                {
                    h = string.Concat(h.Select(c => new string(c, 2)));
                }

                return new Rgba(
                    int.Parse(h.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(h.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    int.Parse(h.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                    1.0);
            }

            var rgba = Function().Match(value);
            if (rgba.Success)
            {
                var parts = rgba.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries);
                return new Rgba(
                    int.Parse(parts[0], CultureInfo.InvariantCulture),
                    int.Parse(parts[1], CultureInfo.InvariantCulture),
                    int.Parse(parts[2], CultureInfo.InvariantCulture),
                    parts.Length > 3 ? double.Parse(parts[3], CultureInfo.InvariantCulture) : 1.0);
            }

            throw new FormatException($"Not a colour: {value}");
        }

        /// <summary>Composites this colour over an opaque backdrop per channel: round(a·fg + (1 − a)·bg).</summary>
        public Rgba Over(Rgba backdrop) => A >= 1.0
            ? this
            : new Rgba(Mix(R, backdrop.R), Mix(G, backdrop.G), Mix(B, backdrop.B), 1.0);

        private int Mix(int fg, int bg) => (int)Math.Round((A * fg) + ((1 - A) * bg), MidpointRounding.AwayFromZero);

        private static double Linear(int channel)
        {
            var c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        [GeneratedRegex(@"^#([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$")]
        private static partial Regex Hex();

        [GeneratedRegex(@"^rgba?\(([^)]*)\)$")]
        private static partial Regex Function();
    }

    private sealed record Declaration(string Name, string Value);

    private sealed record Rule(string Selector, IReadOnlyList<string> Media, IReadOnlyList<Declaration> Declarations)
    {
        public string? Value(string name) => Declarations.LastOrDefault(d => d.Name == name)?.Value;
    }

    /// <summary>
    /// A small CSS reader for this one file: comments stripped, style rules with their enclosing <c>@media</c> preludes
    /// (whitespace-normalised), declarations split on top-level semicolons. <c>@keyframes</c> bodies are skipped.
    /// </summary>
    private sealed class Stylesheet
    {
        public List<Rule> Rules { get; } = [];

        public List<string> MediaQueries { get; } = [];

        public static Stylesheet Parse(string css)
        {
            var sheet = new Stylesheet();
            var text = Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            var i = 0;
            sheet.ParseBlock(text, ref i, []);
            return sheet;
        }

        private static string Normalise(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

        private void ParseBlock(string s, ref int i, List<string> media)
        {
            var prelude = new StringBuilder();
            while (i < s.Length)
            {
                var c = s[i];
                if (c is '"' or '\'')
                {
                    prelude.Append(ReadString(s, ref i));
                    continue;
                }

                if (c == '}')
                {
                    i++;
                    return;
                }

                if (c != '{')
                {
                    prelude.Append(c);
                    i++;
                    continue;
                }

                i++;
                var head = Normalise(prelude.ToString());
                prelude.Clear();
                if (head.StartsWith("@media", StringComparison.Ordinal))
                {
                    var query = Normalise(head["@media".Length..]);
                    MediaQueries.Add(query);
                    media.Add(query);
                    ParseBlock(s, ref i, media);
                    media.RemoveAt(media.Count - 1);
                }
                else if (head.StartsWith('@'))
                {
                    SkipBlock(s, ref i);
                }
                else
                {
                    var body = ReadBody(s, ref i);
                    Rules.Add(new Rule(head, [.. media], SplitDeclarations(body)));
                }
            }
        }

        private static string ReadString(string s, ref int i)
        {
            var quote = s[i];
            var start = i++;
            while (i < s.Length && s[i] != quote)
            {
                i += s[i] == '\\' ? 2 : 1;
            }

            i++;
            return s[start..Math.Min(i, s.Length)];
        }

        private static void SkipBlock(string s, ref int i)
        {
            var depth = 1;
            while (i < s.Length && depth > 0)
            {
                if (s[i] is '"' or '\'')
                {
                    ReadString(s, ref i);
                    continue;
                }

                depth += s[i] switch { '{' => 1, '}' => -1, _ => 0 };
                i++;
            }
        }

        private static string ReadBody(string s, ref int i)
        {
            var body = new StringBuilder();
            while (i < s.Length && s[i] != '}')
            {
                if (s[i] is '"' or '\'')
                {
                    body.Append(ReadString(s, ref i));
                    continue;
                }

                body.Append(s[i]);
                i++;
            }

            i++;
            return body.ToString();
        }

        private static List<Declaration> SplitDeclarations(string body)
        {
            var list = new List<Declaration>();
            var current = new StringBuilder();
            var depth = 0;
            char? quote = null;
            foreach (var c in body + ";")
            {
                if (quote is not null)
                {
                    quote = c == quote ? null : quote;
                    current.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '"' or '\'':
                        quote = c;
                        break;
                    case '(':
                        depth++;
                        break;
                    case ')':
                        depth--;
                        break;
                    case ';' when depth == 0:
                        var text = current.ToString();
                        current.Clear();
                        var colon = text.IndexOf(':', StringComparison.Ordinal);
                        if (colon > 0)
                        {
                            list.Add(new Declaration(Normalise(text[..colon]), Normalise(text[(colon + 1)..])));
                        }

                        continue;
                }

                current.Append(c);
            }

            return list;
        }
    }
}

/// <summary>
/// The LevelSlider half of the design-system contract (UI-SPEC 1.6): markup and classes the CSS relies on, and the
/// public <see cref="LevelSlider.StepAsync"/> the master volume's steppers call (kept next to the CSS checks because
/// the design-system package owns both).
/// </summary>
public sealed class LevelSliderContractTests : IAsyncDisposable
{
    private readonly BunitContext _ctx = new();

    public ValueTask DisposeAsync() => _ctx.DisposeAsync();

    [Fact]
    public void Hero_slider_renders_the_contract_markup()
    {
        var cut = _ctx.Render<LevelSlider>(p => p
            .Add(x => x.Variant, LevelSlider.LevelVariant.Hero)
            .Add(x => x.Label, "Master volume")
            .Add(x => x.HideLabel, true)
            .Add(x => x.Value, 40)
            .Add(x => x.ValueText, v => v.ToString(CultureInfo.InvariantCulture) + " of 100")
            .Add(x => x.Send, (_, _) => Task.FromResult(true)));

        Assert.Equal("level level--hero", cut.Find("div").GetAttribute("class"));
        var input = cut.Find("input[type=range]");
        Assert.Equal("40 of 100", input.GetAttribute("aria-valuetext"));
        Assert.Equal("--fill: 40%", input.GetAttribute("style"));
        // HideLabel: no <label> at all (a compact slider sits inside the Output list's label.row), the name is aria-label.
        Assert.Empty(cut.FindAll("label"));
        Assert.Equal("Master volume", input.GetAttribute("aria-label"));
        var output = cut.Find(".level output");
        Assert.Equal("big", output.GetAttribute("class"));
        Assert.Equal("40", output.TextContent); // the visible number; the sentence is for screen readers
    }

    [Fact]
    public void Default_value_text_is_the_number_alone_and_output_text_can_replace_it()
    {
        var cut = _ctx.Render<LevelSlider>(p => p
            .Add(x => x.Variant, LevelSlider.LevelVariant.Compact)
            .Add(x => x.Label, "Volume")
            .Add(x => x.Value, 7)
            .Add(x => x.Disabled, true)
            .Add(x => x.OutputText, _ => "–")
            .Add(x => x.Send, (_, _) => Task.FromResult(true)));

        Assert.Equal("level level--compact is-disabled", cut.Find("div").GetAttribute("class"));
        Assert.Equal("7", cut.Find("input").GetAttribute("aria-valuetext"));
        Assert.Equal("–", cut.Find("output").TextContent);
        Assert.Equal("level__label", cut.Find("label").GetAttribute("class"));
        Assert.Equal(cut.Find("input").Id, cut.Find("label").GetAttribute("for"));
        Assert.Null(cut.Find("input").GetAttribute("aria-label")); // the visible label names it
    }

    [Fact]
    public void Before_and_after_frame_the_track_and_the_value_comes_last()
    {
        var cut = _ctx.Render<LevelSlider>(p => p
            .Add(x => x.Variant, LevelSlider.LevelVariant.Hero)
            .Add(x => x.Label, "Master volume")
            .Add(x => x.HideLabel, true)
            .Add(x => x.Value, 32)
            .Add(x => x.Before, "<button type=\"button\" class=\"step\">−</button>")
            .Add(x => x.After, "<button type=\"button\" class=\"step\">+</button>")
            .Add(x => x.Send, (_, _) => Task.FromResult(true)));

        var order = cut.Find("div").Children.Select(e => e.TagName.ToLowerInvariant() + ":" + e.TextContent).ToList();
        Assert.Equal(["button:−", "input:", "button:+", "output:32"], order);
    }

    [Fact]
    public void ShowValue_false_renders_no_output()
    {
        var cut = _ctx.Render<LevelSlider>(p => p
            .Add(x => x.Variant, LevelSlider.LevelVariant.Hero)
            .Add(x => x.Label, "Master volume")
            .Add(x => x.HideLabel, true)
            .Add(x => x.Value, 32)
            .Add(x => x.ShowValue, false)
            .Add(x => x.Send, (_, _) => Task.FromResult(true)));

        Assert.Empty(cut.FindAll("output"));
        Assert.Equal(["input"], cut.Find("div").Children.Select(e => e.TagName.ToLowerInvariant()));
    }

    [Fact]
    public async Task A_drag_shows_the_draft_until_the_send_settles()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<int>();
        var drafts = new List<int?>();
        var cut = _ctx.Render<LevelSlider>(p => p
            .Add(x => x.Variant, LevelSlider.LevelVariant.Compact)
            .Add(x => x.Label, "Volume")
            .Add(x => x.HideLabel, true)
            .Add(x => x.Value, 40)
            .Add(x => x.Send, async (level, _) =>
            {
                lock (sent)
                {
                    sent.Add(level);
                }

                return await gate.Task;
            })
            .Add(x => x.DraftChanged, (int? d) => drafts.Add(d)));

        var drag = cut.Find("input").InputAsync(new ChangeEventArgs { Value = "73" });
        cut.WaitForAssertion(() => Assert.Equal([73], sent));
        Assert.Equal([73], drafts);
        Assert.Equal("draft", cut.Find("output").GetAttribute("class"));
        Assert.Equal("73", cut.Find("output").TextContent);
        Assert.Equal("--fill: 73%", cut.Find("input").GetAttribute("style"));

        gate.SetResult(true);
        await drag;
        cut.WaitForAssertion(() => Assert.Equal([73, null], drafts));
        cut.WaitForAssertion(() => Assert.Null(cut.Find("output").GetAttribute("class"))); // settled: no draft class
        Assert.Equal("40", cut.Find("output").TextContent); // the reported value again (the caller has not changed it)
    }

    [Fact]
    public async Task StepAsync_clamps_shows_the_draft_and_sends_through_the_coalescing_sender()
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sent = new List<int>();
        var drafts = new List<int?>();
        var cut = _ctx.Render<LevelSlider>(p => p
            .Add(x => x.Label, "Master volume")
            .Add(x => x.Value, 98)
            .Add(x => x.Send, async (level, _) =>
            {
                lock (sent)
                {
                    sent.Add(level);
                }

                return await gate.Task;
            })
            .Add(x => x.DraftChanged, (int? d) => drafts.Add(d)));

        var step = cut.Instance.StepAsync(5);
        cut.WaitForAssertion(() => Assert.Equal("100", cut.Find("output").TextContent)); // 98 + 5, clamped
        cut.WaitForAssertion(() => Assert.Equal([100], sent));
        Assert.Equal("draft", cut.Find("output").GetAttribute("class"));
        Assert.Equal("--fill: 100%", cut.Find("input").GetAttribute("style"));
        Assert.Equal([100], drafts);

        gate.SetResult(true);
        await step;
        cut.WaitForAssertion(() => Assert.Equal("98", cut.Find("output").TextContent)); // settled: the reported value again
        Assert.Equal([100, null], drafts);

        await cut.Instance.StepAsync(-500);
        Assert.Equal([100, 0], sent);
    }

    [Fact]
    public async Task StepAsync_does_nothing_while_disabled()
    {
        var sent = 0;
        var cut = _ctx.Render<LevelSlider>(p => p
            .Add(x => x.Label, "Volume")
            .Add(x => x.Value, 50)
            .Add(x => x.Disabled, true)
            .Add(x => x.Send, (_, _) =>
            {
                Interlocked.Increment(ref sent);
                return Task.FromResult(true);
            }));

        await cut.Instance.StepAsync(5);
        Assert.Equal(0, sent);
        Assert.Equal("50", cut.Find("output").TextContent);
    }
}
