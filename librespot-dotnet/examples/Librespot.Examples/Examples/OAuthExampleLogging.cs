// Shared logging setup of the oauth examples (oauth/examples/*.rs all start with the same env_logger block).
using Librespot.Core.Logging;
using Librespot.OAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Librespot.Examples;

/// <summary>The <c>env_logger</c> setup shared by the <c>oauth-sync</c>, <c>oauth-async</c> and <c>device-auth</c> examples.</summary>
internal static class OAuthExampleLogging
{
    /// <summary>
    /// Upstream <c>env_logger::Builder::new().parse_filters("librespot=trace").init()</c>: trace level for the
    /// <c>Librespot*</c> categories, written to stderr (as env_logger does) so stdout only carries the example's own
    /// output. The factory is installed into <see cref="LibrespotLog.Factory"/> and <see cref="OAuthLog.Factory"/>.
    /// </summary>
    /// <returns>The factory; the caller disposes it when the example ends.</returns>
    internal static ILoggerFactory CreateTraceFactory()
    {
        var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter((category, _) => category?.StartsWith("Librespot", StringComparison.Ordinal) == true)
            .AddConsole(o =>
            {
                o.FormatterName = ConsoleFormatterNames.Simple;
                o.LogToStandardErrorThreshold = LogLevel.Trace;
            }));
        LibrespotLog.Factory = loggerFactory;
        OAuthLog.Factory = loggerFactory;
        return loggerFactory;
    }
}
