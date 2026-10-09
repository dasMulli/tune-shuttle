// Tests for examples/Librespot.Examples/Examples/ExampleLogging.cs (the examples' env_logger model; no upstream tests).
using Librespot.Examples;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Librespot.Tests.Examples;

public class ExampleLoggingTests
{
    [Fact]
    public void ParseFilters_ModuleDirectiveAndGlobalLevel()
    {
        var filters = ExampleLogging.ParseFilters("librespot=trace,warn");

        Assert.Equal([("Librespot", LogLevel.Trace), ("", LogLevel.Warning)], filters);
    }

    [Fact]
    public void ParseFilters_BareModuleEnablesTrace()
    {
        var filters = ExampleLogging.ParseFilters("librespot_core");

        Assert.Equal([("Librespot.Core", LogLevel.Trace)], filters);
    }

    [Fact]
    public void ParseFilters_InvalidLevelIsIgnored()
    {
        Assert.Empty(ExampleLogging.ParseFilters("foo=bogus"));
        Assert.Equal([("", LogLevel.Information)], ExampleLogging.ParseFilters("foo=bogus,info"));
    }

    [Fact]
    public void ParseFilters_RegexSuffixIsIgnored()
    {
        Assert.Equal([("Librespot", LogLevel.Debug)], ExampleLogging.ParseFilters("librespot=debug/some.*text"));
    }

    [Theory]
    [InlineData("librespot", "Librespot")]
    [InlineData("librespot_core", "Librespot.Core")]
    [InlineData("librespot_core::session", "Librespot.Core")]
    [InlineData("librespot_oauth::x", "Librespot.OAuth")]
    [InlineData("librespot_some_crate", "Librespot.SomeCrate")]
    [InlineData("other::module", "other")]
    public void MapModule_MapsCrateToCategoryPrefix(string module, string expected)
    {
        Assert.Equal(expected, ExampleLogging.MapModule(module));
    }

    [Fact]
    public void LevelFor_LongestMatchingPrefixWins()
    {
        var directives = new Dictionary<string, LogLevel>
        {
            [""] = LogLevel.Warning,
            ["Librespot"] = LogLevel.Information,
            ["Librespot.Core"] = LogLevel.Trace,
        };

        Assert.Equal(LogLevel.Trace, ExampleLogging.LevelFor(directives, "Librespot.Core.Session"));
        Assert.Equal(LogLevel.Information, ExampleLogging.LevelFor(directives, "Librespot.Playback.Player"));
        Assert.Equal(LogLevel.Warning, ExampleLogging.LevelFor(directives, "System.Net.Http"));
    }

    [Fact]
    public void LevelFor_NoMatchReturnsNone()
    {
        var directives = new Dictionary<string, LogLevel> { ["Librespot"] = LogLevel.Trace };

        Assert.Equal(LogLevel.None, ExampleLogging.LevelFor(directives, "System.Net.Http"));
    }

    [Theory]
    [InlineData("off", LogLevel.None)]
    [InlineData("ERROR", LogLevel.Error)]
    [InlineData("warn", LogLevel.Warning)]
    [InlineData("Info", LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("trace", LogLevel.Trace)]
    public void ParseLevel_AcceptsLevelFilterNames(string name, LogLevel expected)
    {
        Assert.Equal(expected, ExampleLogging.ParseLevel(name));
    }

    [Fact]
    public void ParseLevel_RejectsUnknownNames()
    {
        Assert.Null(ExampleLogging.ParseLevel("bogus"));
    }
}
