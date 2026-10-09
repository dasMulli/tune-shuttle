// Port of audio/src/decrypt.rs.
using Librespot.Core;
using Librespot.Core.Crypto;

namespace Librespot.Audio;

/// <summary>
/// A seekable stream that decrypts the wrapped audio file on the fly with AES-128-CTR (upstream
/// <c>AudioDecrypt</c>). A <c>null</c> key makes it pass the data through unaltered (some files are unencrypted,
/// e.g. local files).
/// </summary>
public sealed class AudioDecrypt : Stream
{
    /// <summary>The fixed CTR initial counter block for Spotify audio files (upstream <c>AUDIO_AESIV</c>).</summary>
    internal static ReadOnlySpan<byte> AudioAesIv =>
    [
        0x72, 0xe0, 0x67, 0xfb, 0xdd, 0xcb, 0xcf, 0x77, 0xeb, 0xe8, 0xbc, 0x64, 0x3f, 0x63, 0x0d, 0x93,
    ];

    private readonly Stream _reader;

    // a `null` cipher is a convenience to make `AudioDecrypt` pass files unaltered
    private readonly AesCtr? _cipher;

    /// <summary>Wraps <paramref name="reader"/>; decrypts with <paramref name="key"/> unless it is <c>null</c>.</summary>
    /// <param name="key">The audio key of the file, or <c>null</c> for unencrypted files.</param>
    /// <param name="reader">The (encrypted) file; disposed together with this stream.</param>
    public AudioDecrypt(AudioKey? key, Stream reader)
    {
        _reader = reader;
        _cipher = key is { } k ? new AesCtr(k.Key, AudioAesIv) : null;
    }

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => _reader.CanSeek;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => _reader.Length;

    /// <inheritdoc />
    public override long Position
    {
        get => _reader.Position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <inheritdoc />
    public override void Flush()
    {
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>Reads from the wrapped stream and decrypts the bytes read in place.</summary>
    public override int Read(Span<byte> buffer)
    {
        var len = _reader.Read(buffer);

        _cipher?.Apply(buffer[..len]);

        return len;
    }

    /// <summary>Seeks the wrapped stream and moves the keystream to the new position.</summary>
    public override long Seek(long offset, SeekOrigin origin)
    {
        var newPos = _reader.Seek(offset, origin);

        if (_cipher is not null)
        {
            _cipher.Position = newPos;
        }

        return newPos;
    }

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cipher?.Dispose();
            _reader.Dispose();
        }

        base.Dispose(disposing);
    }
}
