// Logging hook for Librespot.OAuth. Not a port of an upstream file: upstream logs through the global `log` crate,
// which the C# port routes through Librespot.Core.Logging.LibrespotLog. Librespot.Core depends on Librespot.OAuth
// (as upstream core depends on oauth), so this assembly cannot see LibrespotLog and carries its own hook.
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Librespot.OAuth;

/// <summary>
/// The <c>log</c> crate facade for <c>librespot-oauth</c>. The host sets <see cref="Factory"/> once at startup
/// (normally to the same factory it assigns to <c>Librespot.Core.Logging.LibrespotLog.Factory</c>); until then
/// logging is a no-op.
/// </summary>
public static class OAuthLog
{
    private static ILoggerFactory _factory = NullLoggerFactory.Instance;

    /// <summary>The logger factory used by the OAuth clients. Defaults to a null factory.</summary>
    public static ILoggerFactory Factory
    {
        get => Volatile.Read(ref _factory);
        set => Volatile.Write(ref _factory, value ?? NullLoggerFactory.Instance);
    }

    /// <summary>
    /// Creates a logger whose category is the full name of <typeparamref name="T"/>. The logger forwards to
    /// <see cref="Factory"/> as it is when a message is logged, so it can be stored in a <c>static readonly</c> field
    /// before the host assigns the factory.
    /// </summary>
    public static ILogger CreateLogger<T>() => new ForwardingLogger(typeof(T).FullName!.Replace('+', '.'));

    /// <summary>Creates a logger for an explicit category (use the Rust <c>target:</c> when upstream sets one).</summary>
    /// <param name="category">The logger category.</param>
    public static ILogger CreateLogger(string category) => new ForwardingLogger(category);

    /// <summary>
    /// A logger bound late to <see cref="Factory"/>: the real logger is created on first use and re-created only
    /// when <see cref="Factory"/> changes.
    /// </summary>
    private sealed class ForwardingLogger(string category) : ILogger
    {
        private Binding? _binding;

        private ILogger Current
        {
            get
            {
                var factory = Factory;
                var binding = Volatile.Read(ref _binding);
                if (binding is null || !ReferenceEquals(binding.Factory, factory))
                {
                    binding = new Binding(factory, factory.CreateLogger(category));
                    Volatile.Write(ref _binding, binding);
                }

                return binding.Logger;
            }
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => Current.BeginScope(state);

        public bool IsEnabled(LogLevel logLevel) => Current.IsEnabled(logLevel);

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Current.Log(logLevel, eventId, state, exception, formatter);

        private sealed record Binding(ILoggerFactory Factory, ILogger Logger);
    }
}
