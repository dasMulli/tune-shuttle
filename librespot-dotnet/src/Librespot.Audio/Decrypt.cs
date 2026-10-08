// Port of audio/src/decrypt.rs.
// TODO(port: audio-fetch): AES-128-CTR over the file: .NET has no CTR mode, so implement CTR on top of
// Aes.Create() with ECB (EncryptEcb per counter block). IV is AUDIO_AESIV (fixed 16 bytes, see upstream),
// counter = IV + (position / 16), big-endian add; Seek recomputes the counter and the intra-block offset.
using Librespot.Core;

namespace Librespot.Audio;

/// <summary>A seekable stream that decrypts the wrapped audio file on the fly (identity when <c>key</c> is null, for local files).</summary>
public sealed class AudioDecrypt : Stream
{
    private readonly Stream _reader;
    private readonly AudioKey? _key;

    public AudioDecrypt(AudioKey? key, Stream reader)
    {
        _key = key;
        _reader = reader;
    }

    public override bool CanRead => true;
    public override bool CanSeek => _reader.CanSeek;
    public override bool CanWrite => false;
    public override long Length => _reader.Length;

    public override long Position
    {
        get => _reader.Position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) => throw new NotImplementedException("TODO(port: audio-fetch)");

    public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException("TODO(port: audio-fetch)");

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _reader.Dispose();
        }

        base.Dispose(disposing);
    }
}
