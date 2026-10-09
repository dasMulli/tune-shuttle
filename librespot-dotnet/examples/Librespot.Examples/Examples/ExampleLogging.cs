// Shared env_logger setup of the examples/*.rs examples (get_creds, get_token, play_connect, playlist_tracks).
// Not a port of an upstream file: upstream configures the `env_logger` crate at the top of each `main`.
using System.Globalization;
using System.Text;
using Librespot.Core;
using Librespot.Core.Logging;
using Librespot.OAuth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;

namespace Librespot.Examples;

/// <summary>
/// A small model of <c>env_logger</c> for the examples: a set of <c>module=level</c> directives where the most
/// specific (longest) module prefix matching a logger category wins, as in <c>env_logger</c>'s filter.
/// </summary>
/// <remarks>
/// Rust module paths are mapped to the C# logger categories (full type names): the crate name <c>librespot</c>
/// becomes <c>Librespot</c> and <c>librespot_&lt;crate&gt;</c> becomes <c>Librespot.&lt;Crate&gt;</c>
/// (<c>librespot_core</c> → <c>Librespot.Core</c>, <c>librespot_oauth</c> → <c>Librespot.OAuth</c>). Module paths
/// below the crate (<c>librespot_core::session</c>) are mapped to the crate, and the <c>/regex</c> suffix of
/// <c>RUST_LOG</c> is ignored. Output goes to stderr, as with <c>env_logger</c>.
/// </remarks>
internal static class ExampleLogging
{
    /// <summary>The environment variable read by <c>env_logger::init()</c> / <c>env_logger::builder()</c>.</summary>
    internal const string EnvVar = "RUST_LOG";

    /// <summary>
    /// Builds a logger factory from <c>RUST_LOG</c> (when <paramref name="fromEnvironment"/>) and the explicit
    /// <paramref name="modules"/> directives (upstream <c>filter_module</c> / <c>parse_filters</c>), which replace an
    /// environment directive for the same module. With no directive at all, only errors are logged
    /// (<c>env_logger</c>'s default). The factory is installed into <see cref="LibrespotLog.Factory"/> and
    /// <see cref="OAuthLog.Factory"/>.
    /// </summary>
    /// <param name="fromEnvironment">Read <c>RUST_LOG</c> first (upstream <c>env_logger::builder()</c> and <c>env_logger::init()</c>).</param>
    /// <param name="modules">Explicit <c>(rust module, level)</c> directives.</param>
    /// <returns>The factory; the caller disposes it when the example ends.</returns>
    internal static ILoggerFactory Init(bool fromEnvironment, params (string Module, LogLevel Level)[] modules)
    {
        var directives = new Dictionary<string, LogLevel>(StringComparer.Ordinal);
        if (fromEnvironment && Environment.GetEnvironmentVariable(EnvVar) is { Length: > 0 } spec)
        {
            foreach (var (category, level) in ParseFilters(spec))
            {
                directives[category] = level;
            }
        }

        foreach (var (module, level) in modules)
        {
            directives[MapModule(module)] = level;
        }

        if (directives.Count == 0)
        {
            directives[""] = LogLevel.Error;
        }

        var loggerFactory = LoggerFactory.Create(b => b
            .SetMinimumLevel(LogLevel.Trace)
            .AddFilter((category, level) => level >= LevelFor(directives, category ?? ""))
            .AddConsole(o =>
            {
                o.FormatterName = ConsoleFormatterNames.Simple;
                o.LogToStandardErrorThreshold = LogLevel.Trace;
            }));
        LibrespotLog.Factory = loggerFactory;
        OAuthLog.Factory = loggerFactory;
        return loggerFactory;
    }

    /// <summary>
    /// Parses an <c>env_logger</c> filter string (<c>"info"</c>, <c>"librespot=trace,warn"</c>) into
    /// <c>(category prefix, level)</c> pairs. A bare level applies to every category (prefix <c>""</c>), a bare module
    /// name enables every level of that module; invalid directives are ignored, as <c>env_logger</c> does (it only
    /// prints a warning).
    /// </summary>
    /// <param name="spec">The filter string.</param>
    /// <returns>The parsed directives in order.</returns>
    internal static List<(string Category, LogLevel Level)> ParseFilters(string spec)
    {
        var result = new List<(string, LogLevel)>();

        // A `/regex` suffix filters messages by content; not supported here.
        var slash = spec.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            spec = spec[..slash];
        }

        foreach (var raw in spec.Split(','))
        {
            var directive = raw.Trim();
            if (directive.Length == 0)
            {
                continue;
            }

            var eq = directive.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0)
            {
                if (ParseLevel(directive) is { } globalLevel)
                {
                    result.Add(("", globalLevel));
                }
                else
                {
                    result.Add((MapModule(directive), LogLevel.Trace));
                }

                continue;
            }

            var module = directive[..eq].Trim();
            if (module.Length > 0 && ParseLevel(directive[(eq + 1)..].Trim()) is { } level)
            {
                result.Add((MapModule(module), level));
            }
        }

        return result;
    }

    /// <summary>Maps a Rust module path's crate (<c>librespot_core::x</c>) to the C# category prefix (<c>Librespot.Core</c>).</summary>
    /// <param name="module">The Rust module path.</param>
    /// <returns>The category prefix.</returns>
    internal static string MapModule(string module)
    {
        var crate = module.Split("::")[0];
        if (crate == "librespot")
        {
            return "Librespot";
        }

        if (crate.StartsWith("librespot_", StringComparison.Ordinal))
        {
            var sub = crate["librespot_".Length..];
            var pascal = sub == "oauth"
                ? "OAuth"
                : string.Concat(sub.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
            return "Librespot." + pascal;
        }

        return crate;
    }

    /// <summary>The level of the longest directive that prefixes <paramref name="category"/>; <see cref="LogLevel.None"/> when none matches.</summary>
    /// <param name="directives">Category prefix → level.</param>
    /// <param name="category">The logger category.</param>
    /// <returns>The minimum enabled level.</returns>
    internal static LogLevel LevelFor(IReadOnlyDictionary<string, LogLevel> directives, string category)
    {
        var bestLength = -1;
        var best = LogLevel.None;
        foreach (var (prefix, level) in directives)
        {
            if (prefix.Length > bestLength && category.StartsWith(prefix, StringComparison.Ordinal))
            {
                bestLength = prefix.Length;
                best = level;
            }
        }

        return best;
    }

    /// <summary>The <c>log::LevelFilter</c> names (case-insensitive), mapped to <see cref="LogLevel"/>.</summary>
    /// <param name="s">The level name.</param>
    /// <returns>The level, or null when <paramref name="s"/> is not a level name.</returns>
    internal static LogLevel? ParseLevel(string s) => s.ToLowerInvariant() switch
    {
        "off" => LogLevel.None,
        "error" => LogLevel.Error,
        "warn" => LogLevel.Warning,
        "info" => LogLevel.Information,
        "debug" => LogLevel.Debug,
        "trace" => LogLevel.Trace,
        _ => null,
    };
}

/// <summary>Formatting helpers shared by the examples.</summary>
internal static class ExampleFormat
{
    /// <summary>Rust <c>Display</c> of a librespot <c>Error</c> (or <c>OAuthError</c>) for any exception.</summary>
    /// <param name="e">The exception.</param>
    /// <returns>The display text.</returns>
    internal static string Display(Exception e) =>
        e is LibrespotException or OAuthException ? e.Message : LibrespotException.From(e).Message;

    /// <summary>Rust <c>&lt;str as Debug&gt;</c> for the ASCII range: the string in double quotes, with quotes, backslashes and control characters escaped.</summary>
    /// <param name="s">The string.</param>
    /// <returns>The quoted string.</returns>
    internal static string DebugString(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var ch in s)
        {
            _ = ch switch
            {
                '"' => sb.Append("\\\""),
                '\\' => sb.Append("\\\\"),
                '\n' => sb.Append("\\n"),
                '\r' => sb.Append("\\r"),
                '\t' => sb.Append("\\t"),
                '\0' => sb.Append("\\0"),
                _ when char.IsControl(ch) => sb.Append(CultureInfo.InvariantCulture, $"\\u{{{(int)ch:x}}}"),
                _ => sb.Append(ch),
            };
        }

        return sb.Append('"').ToString();
    }
}
