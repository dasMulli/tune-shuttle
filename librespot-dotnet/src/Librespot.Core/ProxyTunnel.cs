// Port of core/src/proxytunnel.rs.
using System.Text;

namespace Librespot.Core;

/// <summary>HTTP <c>CONNECT</c> tunnelling through a proxy (upstream private module <c>proxytunnel</c>).</summary>
internal static class ProxyTunnel
{
    /// <summary>The header limit of the upstream parser (<c>[httparse::EMPTY_HEADER; 16]</c>).</summary>
    private const int MaxHeaders = 16;

    /// <summary>
    /// Upstream <c>proxy_connect(proxy_connection, connect_host, connect_port)</c>: sends
    /// <c>CONNECT host:port HTTP/1.1</c> over <paramref name="proxyConnection"/> and returns the same stream once the
    /// proxy answered <c>200</c>.
    /// </summary>
    /// <exception cref="IOException">Early EOF, a malformed response, or a status other than 200.</exception>
    public static async Task<Stream> ProxyConnectAsync(Stream proxyConnection, string connectHost, string connectPort, CancellationToken cancellationToken = default)
    {
        var request = Encoding.UTF8.GetBytes($"CONNECT {connectHost}:{connectPort} HTTP/1.1\r\n\r\n");
        await proxyConnection.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await proxyConnection.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Upstream reuses the request buffer (resized to its capacity) for the response.
        var buffer = new byte[Math.Max(request.Length, 64)];

        var offset = 0;
        while (true)
        {
            var bytesRead = await proxyConnection.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                throw new IOException("Early EOF from proxy");
            }

            offset += bytesRead;

            var response = ParseResponse(buffer.AsSpan(0, offset));
            if (response is { } complete)
            {
                return complete.Code switch
                {
                    200 => proxyConnection, // Proxy says all is well
                    var code => throw new IOException($"Proxy responded with {code}: {complete.Reason}"),
                };
            }

            if (offset >= buffer.Length)
            {
                Array.Resize(ref buffer, buffer.Length + 100);
            }
        }
    }

    /// <summary>
    /// Minimal replacement for <c>httparse::Response::parse</c>: returns null while the response head is incomplete,
    /// the status code and reason phrase once it is complete, and throws on malformed input.
    /// </summary>
    /// <remarks>
    /// Like httparse, an omitted reason phrase is the empty string, so upstream's <c>"no reason"</c> fallback (only
    /// reached for <c>None</c>, which a complete parse never produces) is not needed.
    /// </remarks>
    internal static (int Code, string Reason)? ParseResponse(ReadOnlySpan<byte> data)
    {
        // Status line: HTTP/1.x SP 3DIGIT SP reason CRLF
        var statusEnd = FindLineEnd(data, 0, out var statusNext);

        var versionPart = statusEnd < 0 ? data : data[..statusEnd];
        ReadOnlySpan<byte> expected = "HTTP/1."u8;
        var checkLength = Math.Min(versionPart.Length, expected.Length + 1);
        for (var i = 0; i < checkLength; i++)
        {
            var ok = i < expected.Length ? versionPart[i] == expected[i] : versionPart[i] is (byte)'0' or (byte)'1';
            if (!ok)
            {
                throw new IOException("invalid HTTP version");
            }
        }

        if (statusEnd < 0)
        {
            return null;
        }

        var statusLine = data[..statusEnd];
        if (statusLine.Length < 12 || statusLine[8] != (byte)' ')
        {
            throw new IOException("invalid HTTP version");
        }

        var codeBytes = statusLine.Slice(9, 3);
        foreach (var b in codeBytes)
        {
            if (b is < (byte)'0' or > (byte)'9')
            {
                throw new IOException("invalid response status");
            }
        }

        var code = ((codeBytes[0] - '0') * 100) + ((codeBytes[1] - '0') * 10) + (codeBytes[2] - '0');

        var reason = string.Empty;
        if (statusLine.Length > 12)
        {
            if (statusLine[12] != (byte)' ')
            {
                throw new IOException("invalid response status");
            }

            reason = Encoding.Latin1.GetString(statusLine[13..]);
        }

        // Headers until an empty line.
        var position = statusNext;
        var headers = 0;
        while (true)
        {
            var lineEnd = FindLineEnd(data, position, out var next);
            if (lineEnd < 0)
            {
                return null;
            }

            if (lineEnd == position)
            {
                return (code, reason);
            }

            var line = data[position..lineEnd];
            var colon = line.IndexOf((byte)':');
            if (colon <= 0 || !IsToken(line[..colon]))
            {
                throw new IOException("invalid header name");
            }

            if (++headers > MaxHeaders)
            {
                throw new IOException("too many headers");
            }

            position = next;
        }
    }

    /// <summary>Index of the line terminator (CRLF or bare LF) starting at <paramref name="start"/>, or -1.</summary>
    private static int FindLineEnd(ReadOnlySpan<byte> data, int start, out int next)
    {
        var lf = data[start..].IndexOf((byte)'\n');
        if (lf < 0)
        {
            next = -1;
            return -1;
        }

        lf += start;
        next = lf + 1;
        return lf > start && data[lf - 1] == (byte)'\r' ? lf - 1 : lf;
    }

    private static bool IsToken(ReadOnlySpan<byte> name)
    {
        foreach (var b in name)
        {
            var ok = b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9'
                || "!#$%&'*+-.^_`|~"u8.Contains(b);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
