// Port of core/src/util.rs.
//
// Fully ported here: SeqGenerator, get_next_query_separator.
// TODO(port: core-connection): CancelOnDrop / TimeoutOnDrop (task-handle guards; in .NET use CancellationTokenSource
//   + Task and `await using`), solve_hash_cash (SHA-1 based hashcash for login5/client-token challenges).
using System.Numerics;

namespace Librespot.Core;

/// <summary>Monotonic, wrapping sequence-number generator (upstream <c>SeqGenerator&lt;T&gt;</c>).</summary>
public struct SeqGenerator<T> where T : IBinaryInteger<T>
{
    private T _value;

    public SeqGenerator(T value)
    {
        _value = value;
    }

    /// <summary>Returns the current value and advances by one (wrapping on overflow).</summary>
    public T Get()
    {
        var current = _value;
        unchecked
        {
            _value = current + T.One;
        }

        return current;
    }
}

public static class UrlUtil
{
    /// <summary>Upstream <c>get_next_query_separator</c>: "?" when the URL has no query yet, otherwise "&amp;".</summary>
    public static string GetNextQuerySeparator(string url)
    {
        var idx = url.IndexOf('?');
        if (idx < 0)
        {
            return "?";
        }

        return idx == url.Length - 1 ? "" : "&";
    }
}

public static class HashCash
{
    /// <summary>
    /// Upstream <c>solve_hash_cash(ctx, prefix, length, dst)</c>: finds a 16-byte suffix such that SHA-1(ctx)
    /// XOR'ed with SHA-1(prefix || suffix) has <paramref name="length"/> trailing zero bits.
    /// </summary>
    public static void Solve(ReadOnlySpan<byte> ctx, ReadOnlySpan<byte> prefix, int length, Span<byte> dst) =>
        throw new NotImplementedException("TODO(port: core-connection)");
}
