using System.Text;
using Librespot.Core;
using Xunit;

namespace Librespot.Tests.Core;

public class ProxyTunnelTests
{
    [Fact]
    public async Task Connect_succeeds_on_200_and_sends_connect_request()
    {
        var stream = new ScriptedStream("HTTP/1.1 200 Connection established\r\nProxy-Agent: test\r\n\r\n", chunkSize: 7);

        var result = await ProxyTunnel.ProxyConnectAsync(stream, "ap.spotify.com", "4070");

        Assert.Same(stream, result);
        Assert.Equal("CONNECT ap.spotify.com:4070 HTTP/1.1\r\n\r\n", Encoding.ASCII.GetString(stream.Written.ToArray()));
    }

    [Fact]
    public async Task Connect_fails_on_other_status()
    {
        var stream = new ScriptedStream("HTTP/1.1 407 Proxy Authentication Required\r\n\r\n");
        var error = await Assert.ThrowsAsync<IOException>(() => ProxyTunnel.ProxyConnectAsync(stream, "h", "1"));
        Assert.Equal("Proxy responded with 407: Proxy Authentication Required", error.Message);
    }

    [Fact]
    public async Task Connect_reports_missing_reason_as_empty_like_httparse()
    {
        var stream = new ScriptedStream("HTTP/1.1 407\r\n\r\n");
        var error = await Assert.ThrowsAsync<IOException>(() => ProxyTunnel.ProxyConnectAsync(stream, "h", "1"));
        Assert.Equal("Proxy responded with 407: ", error.Message);
    }

    [Fact]
    public async Task Connect_fails_on_early_eof()
    {
        var stream = new ScriptedStream("HTTP/1.1 200 OK\r\n");
        var error = await Assert.ThrowsAsync<IOException>(() => ProxyTunnel.ProxyConnectAsync(stream, "h", "1"));
        Assert.Equal("Early EOF from proxy", error.Message);
    }

    [Fact]
    public async Task Connect_fails_on_garbage()
    {
        var stream = new ScriptedStream("SSH-2.0-OpenSSH\r\n\r\n");
        await Assert.ThrowsAsync<IOException>(() => ProxyTunnel.ProxyConnectAsync(stream, "h", "1"));
    }

    [Theory]
    [InlineData("HTTP/1.1 200 OK\r\n", false)]
    [InlineData("HTTP/1.1 200 OK\r\n\r\n", true)]
    [InlineData("HTTP/1.0 200\r\n\r\n", true)]
    [InlineData("HTTP/1.1 200 OK\nA: b\n\n", true)]
    [InlineData("HTTP/1.", false)]
    public void Parse_response_completeness(string response, bool complete)
    {
        var parsed = ProxyTunnel.ParseResponse(Encoding.ASCII.GetBytes(response));
        Assert.Equal(complete, parsed is not null);
        if (parsed is { } p)
        {
            Assert.Equal(200, p.Code);
        }
    }

    [Fact]
    public void Parse_response_rejects_too_many_headers()
    {
        var response = "HTTP/1.1 200 OK\r\n" + string.Concat(Enumerable.Range(0, 17).Select(i => $"H{i}: v\r\n")) + "\r\n";
        var error = Assert.Throws<IOException>(() => ProxyTunnel.ParseResponse(Encoding.ASCII.GetBytes(response)));
        Assert.Equal("too many headers", error.Message);
    }

    /// <summary>Reads come from a fixed script (in chunks), writes are captured.</summary>
    private sealed class ScriptedStream(string script, int chunkSize = int.MaxValue) : Stream
    {
        private readonly byte[] _script = Encoding.ASCII.GetBytes(script);
        private int _position;

        public MemoryStream Written { get; } = new();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, chunkSize), _script.Length - _position);
            Array.Copy(_script, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
