// Tests for the getopts-compatible parser and the librespot option table (src/Librespot.Cli/Options.cs).
// Upstream has no unit tests for src/main.rs; these pin getopts' parsing rules and help layout.
using Librespot.Cli;
using Xunit;

namespace Librespot.Tests.Cli;

public class OptionsTests
{
    private static Options.Matches Parse(params string[] args) => CliOptions.Build().Parse(args);

    private static GetoptsException ParseFails(params string[] args) =>
        Assert.Throws<GetoptsException>(() => CliOptions.Build().Parse(args));

    [Fact]
    public void ShortAndLongFlags()
    {
        var m = Parse("-v", "--quiet");
        Assert.True(m.OptPresent("verbose"));
        Assert.True(m.OptPresent("v"));
        Assert.True(m.OptPresent("quiet"));
        Assert.False(m.OptPresent("disable-gapless"));
        Assert.Null(m.OptStr("verbose"));
    }

    [Fact]
    public void ShortFlagCluster()
    {
        var m = Parse("-vqg");
        Assert.True(m.OptPresent("verbose"));
        Assert.True(m.OptPresent("quiet"));
        Assert.True(m.OptPresent("disable-gapless"));
    }

    [Theory]
    [InlineData("-nKitchen")]
    [InlineData("-n", "Kitchen")]
    [InlineData("--name=Kitchen")]
    [InlineData("--name", "Kitchen")]
    [InlineData("-vnKitchen")]
    public void OptionValueForms(params string[] args)
    {
        Assert.Equal("Kitchen", Parse(args).OptStr("name"));
    }

    [Fact]
    public void LongOptionValueMayContainEquals()
    {
        Assert.Equal("a=b", Parse("--onevent=a=b").OptStr("onevent"));
    }

    [Fact]
    public void EmptyValueWithEquals()
    {
        var m = Parse("--name=");
        Assert.True(m.OptPresent("name"));
        Assert.Equal(string.Empty, m.OptStr("name"));
    }

    [Fact]
    public void ValueStartingWithDashIsTakenAsArgument()
    {
        Assert.Equal("-v", Parse("--name", "-v").OptStr("name"));
    }

    [Fact]
    public void DoubleDashTerminatesOptions()
    {
        var m = Parse("-q", "--", "-v", "x");
        Assert.True(m.OptPresent("quiet"));
        Assert.False(m.OptPresent("verbose"));
        Assert.Equal(["-v", "x"], m.Free);
    }

    [Fact]
    public void FreeArgumentsFloat()
    {
        var m = Parse("foo", "-v", "-", "bar");
        Assert.True(m.OptPresent("verbose"));
        Assert.Equal(["foo", "-", "bar"], m.Free);
    }

    [Fact]
    public void SingleCharacterLongNameIsAShortName()
    {
        // getopts `Name::from_str`: "--n=x" names the short option n.
        Assert.Equal("x", Parse("--n=x").OptStr("name"));
    }

    [Fact]
    public void LongOnlyOptions()
    {
        var m = Parse("--group", "--volume-steps", "32", "--enable-device-auth", "--zeroconf-backend", "libmdns");
        Assert.True(m.OptPresent("group"));
        Assert.Equal("32", m.OptStr("volume-steps"));
        Assert.True(m.OptPresent("enable-device-auth"));
        Assert.Equal("libmdns", m.OptStr("zeroconf-backend"));
    }

    [Fact]
    public void MultiOption()
    {
        var m = Parse("-l", "a", "--local-file-dir", "b", "-lc");
        Assert.Equal(["a", "b", "c"], m.OptStrs("local-file-dir"));
        Assert.Equal("a", m.OptStr("local-file-dir"));
    }

    [Fact]
    public void UnrecognizedLongOption()
    {
        var e = ParseFails("--nope");
        Assert.Equal(GetoptsFail.UnrecognizedOption, e.Fail);
        Assert.Equal("Unrecognized option: 'nope'", e.Message);
    }

    [Fact]
    public void UnrecognizedShortOption()
    {
        Assert.Equal("Unrecognized option: '9'", ParseFails("-v9").Message);
    }

    [Fact]
    public void ArgumentMissing()
    {
        Assert.Equal("Argument to option 'name' missing", ParseFails("--name").Message);
        Assert.Equal("Argument to option 'n' missing", ParseFails("-vn").Message);
    }

    [Fact]
    public void OptionDuplicated()
    {
        Assert.Equal("Option 'name' given more than once", ParseFails("-n", "a", "--name", "b").Message);
        Assert.Equal("Option 'verbose' given more than once", ParseFails("-v", "-v").Message);
    }

    [Fact]
    public void UnexpectedArgument()
    {
        Assert.Equal("Option 'verbose' does not take an argument", ParseFails("--verbose=1").Message);
    }

    [Fact]
    public void OptDefinedAndUndefined()
    {
        var m = Parse();
        Assert.True(m.OptDefined("verbose"));
        Assert.True(m.OptDefined("v"));
        Assert.True(m.OptDefined("tmp"));
        Assert.False(m.OptDefined("temp-dir"));
        Assert.False(m.OptDefined("vq"));
        // getopts panics for undefined options.
        Assert.Throws<InvalidOperationException>(() => m.OptPresent("nope"));
    }

    [Fact]
    public void EveryUpstreamOptionIsDefined()
    {
        string[] longs =
        [
            "help", "version", "verbose", "quiet", "disable-audio-cache", "disable-credential-cache", "disable-discovery",
            "disable-gapless", "emit-sink-events", "enable-volume-normalisation", "enable-oauth", "enable-device-auth",
            "name", "bitrate", "format", "dither", "device-type", "group", "tmp", "cache", "system-cache",
            "cache-size-limit", "backend", "username", "password", "access-token", "oauth-port", "onevent",
            "alsa-mixer-control", "alsa-mixer-device", "alsa-mixer-index", "mixer", "device", "initial-volume",
            "volume-ctrl", "volume-range", "volume-steps", "normalisation-method", "normalisation-gain-type",
            "normalisation-pregain", "normalisation-threshold", "normalisation-attack", "normalisation-release",
            "normalisation-knee", "zeroconf-port", "proxy", "ap-port", "autoplay", "zeroconf-interface",
            "zeroconf-backend", "local-file-dir", "passthrough",
        ];
        var m = Parse();
        foreach (var l in longs)
        {
            Assert.True(m.OptDefined(l), l);
        }

        Assert.Equal(longs.Length, CliOptions.Build().Groups.Count);

        // Every short name is unique.
        var shorts = CliOptions.Build().Groups.Select(g => g.ShortName).Where(s => s.Length > 0).ToList();
        Assert.Equal(shorts.Count, shorts.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void UsageLayout()
    {
        var opts = new Options()
            .OptFlag("h", "help", "Print this help menu.")
            .OptOpt("n", "name", "Device name. Defaults to Librespot.", "NAME")
            .OptFlag("", "group", "Whether the device represents a group. Defaults to false.")
            .OptOpt("C", "system-cache", "Path to a directory where system files (credentials, volume) will be cached. May be different from the `--cache` option value.", "PATH");

        var expected =
            "brief\n\nOptions:\n" +
            "    -h, --help          Print this help menu.\n" +
            "    -n, --name NAME     Device name. Defaults to Librespot.\n" +
            "        --group         Whether the device represents a group. Defaults to\n" +
            "                        false.\n" +
            "    -C, --system-cache PATH\n" +
            "                        Path to a directory where system files (credentials,\n" +
            "                        volume) will be cached. May be different from the\n" +
            "                        `--cache` option value.\n";
        Assert.Equal(expected, opts.Usage("brief"));
    }

    [Fact]
    public void UsageRowWithoutHintKeepsTrailingSpace()
    {
        var usage = CliOptions.Build().Usage("b");
        Assert.Contains("    -G, --disable-audio-cache \n                        Disable caching of the audio data.\n", usage);
        Assert.Contains("    -j, --enable-oauth  Perform interactive OAuth sign in.\n", usage);
        Assert.Contains("    -f, --format FORMAT Output format {F64|F32|S32|S24|S24_3|S16}. Defaults to\n                        S16.\n", usage);
        Assert.EndsWith("    -P, --passthrough   Pass a raw stream to the output. Only works with the\n                        pipe and subprocess backends.\n", usage);
    }

    [Fact]
    public void EachSplitWithin()
    {
        Assert.Equal(["a b c"], Options.EachSplitWithin("  a   b c  ", 54));
        Assert.Equal(["aaa", "bbb"], Options.EachSplitWithin("aaa bbb", 5));
        Assert.Equal(["aaaaaaa", "b"], Options.EachSplitWithin("aaaaaaa b", 5));
        Assert.Equal(["x", "y"], Options.EachSplitWithin("x\ny", 54));
        Assert.Empty(Options.EachSplitWithin("", 54));
    }

    [Fact]
    public void OptionalArgument()
    {
        var opts = new Options().OptFlagOpt("m", "maybe", "d", "VAL").OptFlag("f", "flag", "d");

        // A short option without an attached value takes the next argument unless it is an option.
        Assert.Equal("x", opts.Parse(["-m", "x"]).OptStr("m"));
        Assert.Equal("x", opts.Parse(["-mx"]).OptStr("m"));
        var beforeOption = opts.Parse(["-m", "-f"]);
        Assert.True(beforeOption.OptPresent("m"));
        Assert.Null(beforeOption.OptStr("m"));
        Assert.True(beforeOption.OptPresent("f"));

        // A long option only takes `=VALUE`; a following argument stays free.
        var longForm = opts.Parse(["--maybe", "x"]);
        Assert.True(longForm.OptPresent("maybe"));
        Assert.Null(longForm.OptStr("maybe"));
        Assert.Equal(["x"], longForm.Free);
        Assert.Equal("y", opts.Parse(["--maybe=y"]).OptStr("maybe"));

        Assert.Contains("-m, --maybe [VAL]", opts.Usage("b"));
    }

    [Fact]
    public void InvalidNamesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new Options().OptFlag("ab", "long", "d"));
        Assert.Throws<ArgumentException>(() => new Options().OptFlag("a", "l", "d"));
    }
}
