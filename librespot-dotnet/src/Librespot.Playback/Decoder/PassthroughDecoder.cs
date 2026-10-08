// Port of playback/src/decoder/passthrough_decoder.rs. The `ogg` crate's PacketReader/PacketWriter are ported in
// Ogg.cs.
using Librespot.Core.Logging;
using Librespot.Metadata.Audio;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback.Decoder;

/// <summary>
/// Passes Ogg Vorbis pages through without decoding: re-muxes the stream (with the Spotify header already stripped)
/// into a fresh logical stream whose granule positions start at the seek position. Upstream <c>PassthroughDecoder</c>.
/// </summary>
public sealed class PassthroughDecoder : IAudioDecoder
{
    private static readonly ILogger Log = LibrespotLog.CreateLogger<PassthroughDecoder>();

    private readonly Stream _input;
    private readonly OggPacketReader _rdr;
    // Upstream maps write_packet's io::Error into DecoderError::PassthroughDecoder; the writer here buffers in memory
    // (upstream PacketWriter<Vec<u8>>, whose writes cannot fail either), so its calls are not wrapped.
    private readonly OggPacketWriter _wtr = new();
    private readonly byte[] _ident;
    private readonly byte[] _comment;
    private readonly byte[] _setup;
    private bool _eos;
    private bool _bos;
    private ulong _ofsgpPage;
    private uint _streamSerial;
    private bool _disposed;

    /// <summary>Constructs a new decoder from a seekable stream (upstream <c>PassthroughDecoder::new</c>). The decoder owns <paramref name="rdr"/>.</summary>
    /// <exception cref="DecoderException">The format is not Ogg Vorbis or the Vorbis headers are missing.</exception>
    public PassthroughDecoder(Stream rdr, AudioFileFormat format)
    {
        if (!AudioFiles.IsOggVorbis(format))
        {
            throw new DecoderException(
                DecoderException.Reason.PassthroughDecoder,
                $"Passthrough decoder is not implemented for format {format}");
        }

        _input = rdr;
        _rdr = new OggPacketReader(rdr);
        _streamSerial = unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        Log.LogInformation("Starting passthrough track with serial {StreamSerial}", _streamSerial);

        try
        {
            // search for ident, comment, setup
            _ident = GetHeader(1, _rdr);
            _comment = GetHeader(3, _rdr);
            _setup = GetHeader(5, _rdr);
        }
        catch
        {
            // Upstream drops the reader (and with it the stream) when new() fails.
            rdr.Dispose();
            throw;
        }

        // remove un-needed packets
        _rdr.DeleteUnreadPackets();
    }

    /// <inheritdoc />
    public uint Seek(uint positionMs)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var absgp = SaturateToU64(positionMs * AudioConstants.PagesPerMs);

        // add an eos to previous stream if missing
        if (_bos && !_eos)
        {
            OggPacket? pck;
            try
            {
                pck = _rdr.ReadPacket();
            }
            catch (OggReadException)
            {
                pck = null;
            }

            if (pck is not null)
            {
                var absgpPage = unchecked(pck.AbsgpPage - _ofsgpPage);
                _wtr.WritePacket(pck.Data, _streamSerial, PacketWriteEndInfo.EndStream, absgpPage);
            }
            else
            {
                Log.LogWarning("Cannot write EoS after seeking");
            }
        }

        _eos = false;
        _bos = false;
        _ofsgpPage = 0;
        _streamSerial = unchecked(_streamSerial + 1);

        try
        {
            _rdr.SeekAbsgp(null, absgp);
        }
        catch (OggReadException e)
        {
            throw new DecoderException(DecoderException.Reason.PassthroughDecoder, e.Message, e);
        }

        // need to set some offset for next_page()
        OggPacket? packet;
        try
        {
            packet = _rdr.ReadPacket();
        }
        catch (OggReadException e)
        {
            throw new DecoderException(DecoderException.Reason.PassthroughDecoder, e.Message, e);
        }

        if (packet is null)
        {
            throw new DecoderException(DecoderException.Reason.PassthroughDecoder, "Packet is None");
        }

        var newPage = packet.AbsgpPage;
        _ofsgpPage = newPage;
        Log.LogDebug("Seek to offset page {NewPage}", newPage);
        return PositionPcmToMs(newPage);
    }

    /// <inheritdoc />
    public (AudioPacketPosition Position, AudioPacket Packet)? NextPacket()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // write headers if we are (re)starting
        // (Upstream clones the headers because write_packet takes ownership; OggPacketWriter only reads them.)
        if (!_bos)
        {
            _wtr.WritePacket(_ident, _streamSerial, PacketWriteEndInfo.EndPage, 0);
            _wtr.WritePacket(_comment, _streamSerial, PacketWriteEndInfo.NormalPacket, 0);
            _wtr.WritePacket(_setup, _streamSerial, PacketWriteEndInfo.EndPage, 0);
            _bos = true;
            Log.LogDebug("Wrote Ogg headers");
        }

        while (true)
        {
            OggPacket? pck;
            try
            {
                pck = _rdr.ReadPacket();
            }
            catch (OggReadException e) when (e.Cause == OggReadException.Reason.NoCapturePatternFound)
            {
                pck = null;
            }
            catch (OggReadException e)
            {
                throw new DecoderException(DecoderException.Reason.PassthroughDecoder, e.Message, e);
            }

            if (pck is null)
            {
                Log.LogInformation("end of streaming");
                return null;
            }

            var pckgpPage = pck.AbsgpPage;

            // skip till we have audio and a calculable granule position
            if (pckgpPage == 0 || pckgpPage == _ofsgpPage)
            {
                continue;
            }

            // set packet type
            PacketWriteEndInfo inf;
            if (pck.LastInStream)
            {
                _eos = true;
                inf = PacketWriteEndInfo.EndStream;
            }
            else if (pck.LastInPage)
            {
                inf = PacketWriteEndInfo.EndPage;
            }
            else
            {
                inf = PacketWriteEndInfo.NormalPacket;
            }

            _wtr.WritePacket(pck.Data, _streamSerial, inf, unchecked(pckgpPage - _ofsgpPage));

            if (!_wtr.IsEmpty)
            {
                var positionMs = PositionPcmToMs(pckgpPage);
                var packetPosition = new AudioPacketPosition(positionMs, false);

                var oggData = new AudioPacket.Raw(_wtr.TakeData());

                return (packetPosition, oggData);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _input.Dispose();
        }
    }

    private static byte[] GetHeader(byte code, OggPacketReader rdr)
    {
        OggPacket pck;
        try
        {
            pck = rdr.ReadPacketExpected();
        }
        catch (OggReadException e)
        {
            throw new DecoderException(DecoderException.Reason.PassthroughDecoder, e.Message, e);
        }

        // Deviation: upstream indexes data[0] unchecked (a panic on an empty packet); treat that as invalid data.
        if (pck.Data.Length == 0)
        {
            throw new DecoderException(DecoderException.Reason.PassthroughDecoder, "Invalid Data");
        }

        var pktType = pck.Data[0];
        Log.LogDebug("Vorbis header type {PktType}", pktType);

        if (pktType != code)
        {
            throw new DecoderException(DecoderException.Reason.PassthroughDecoder, "Invalid Data");
        }

        return pck.Data;
    }

    private static uint PositionPcmToMs(ulong positionPcm) =>
        (uint)Math.Min(positionPcm * AudioConstants.MsPerPage, uint.MaxValue);

    /// <summary>Rust <c>f64 as u64</c>: saturating, NaN becomes 0.</summary>
    private static ulong SaturateToU64(double value) =>
        double.IsNaN(value) || value <= 0 ? 0 : value >= ulong.MaxValue ? ulong.MaxValue : (ulong)value;
}
