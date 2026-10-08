using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Librespot.Core.Logging;

/// <summary>
/// Replacement for the Rust <c>log</c> crate facade. Upstream code logs through the global
/// <c>log</c> macros; here every type obtains an <see cref="ILogger"/> from
/// <see cref="Factory"/>, which the host application sets once at startup
/// (<c>LibrespotLog.Factory = LoggerFactory.Create(...)</c>). Until then logging is a no-op.
/// </summary>
/// <remarks>
/// Logger category names mirror the Rust module path (<c>librespot_core::session</c> becomes
/// <c>Librespot.Core.Session</c>), so a <c>--verbose</c> switch can filter by namespace the way the
/// Rust CLI filters by <c>RUST_LOG</c> target.
/// </remarks>
public static class LibrespotLog
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>The logger factory used by all librespot components. Defaults to a null factory.</summary>
    public static ILoggerFactory Factory
    {
        get => _factory;
        set => _factory = value ?? NullLoggerFactory.Instance;
    }

    /// <summary>Creates a logger whose category is the full name of <typeparamref name="T"/>.</summary>
    public static ILogger CreateLogger<T>() => _factory.CreateLogger<T>();

    /// <summary>Creates a logger for an explicit category (use the Rust <c>target:</c> when upstream sets one).</summary>
    public static ILogger CreateLogger(string category) => _factory.CreateLogger(category);
}
