// Port of the subset of the `ogg` crate (v0.9.2: reading.rs, writing.rs, crc.rs) that
// playback/src/decoder/passthrough_decoder.rs uses: PacketReader (read_packet, read_packet_expected,
// seek_absgp, delete_unread_packets) and PacketWriter (write_packet). There is no maintained .NET Ogg
// packet reader/writer with these exact semantics (granule positions per page, last-in-page/stream flags,
// bisection seek), so the crate logic is ported 1:1 here as an internal helper of the passthrough decoder.
using System.Buffers.Binary;

namespace Librespot.Playback.Decoder;

/// <summary>Upstream <c>ogg::OggReadError</c>; <see cref="Exception.Message"/> renders like its <c>Display</c>.</summary>
internal sealed class OggReadException : Exception
{
    /// <summary>The <c>OggReadError</c> variants.</summary>
    public enum Reason
    {
        /// <summary>The capture pattern for a new page was not found where one was expected.</summary>
        NoCapturePatternFound,

        /// <summary>Invalid stream structure version.</summary>
        InvalidStreamStructVer,

        /// <summary>Mismatch of the CRC with the calculated value.</summary>
        HashMismatch,

        /// <summary>I/O error occurred.</summary>
        ReadError,

        /// <summary>Some constraint required by the spec was not met.</summary>
        InvalidData,
    }

    private OggReadException(Reason cause, Exception? inner = null)
        : base(Describe(cause), inner)
    {
        Cause = cause;
    }

    /// <summary>The variant.</summary>
    public Reason Cause { get; }

    public static OggReadException NoCapturePatternFound() => new(Reason.NoCapturePatternFound);

    public static OggReadException InvalidStreamStructVer() => new(Reason.InvalidStreamStructVer);

    public static OggReadException HashMismatch() => new(Reason.HashMismatch);

    public static OggReadException ReadError(Exception inner) => new(Reason.ReadError, inner);

    public static OggReadException InvalidData() => new(Reason.InvalidData);

    private static string Describe(Reason cause) => cause switch
    {
        Reason.NoCapturePatternFound => "No Ogg capture pattern found",
        Reason.InvalidStreamStructVer => "A non zero stream structure version was passed",
        Reason.HashMismatch => "CRC32 hash mismatch",
        Reason.ReadError => "I/O error",
        _ => "Constraint violated",
    };
}

/// <summary>Upstream <c>ogg::Packet</c>.</summary>
internal sealed class OggPacket
{
    /// <summary>The data the packet contains.</summary>
    public required byte[] Data { get; init; }

    /// <summary>True iff this packet is the first one in the page.</summary>
    public bool FirstInPage { get; init; }

    /// <summary>True iff this packet is the first one in the logical bitstream.</summary>
    public bool FirstInStream { get; init; }

    /// <summary>True iff this packet is the last one in the page.</summary>
    public bool LastInPage { get; init; }

    /// <summary>True iff this packet is the last one in the logical bitstream.</summary>
    public bool LastInStream { get; init; }

    /// <summary>Absolute granule position of the page the packet ended in.</summary>
    public ulong AbsgpPage { get; init; }

    /// <summary>Serial number of the logical bitstream.</summary>
    public uint StreamSerial { get; init; }

    /// <summary>Checksum of the page the packet ended in.</summary>
    public uint ChecksumPage { get; init; }
}

/// <summary>Upstream <c>ogg::PacketWriteEndInfo</c>.</summary>
internal enum PacketWriteEndInfo
{
    /// <summary>No ends here, just a normal packet.</summary>
    NormalPacket,

    /// <summary>Force-end the current page.</summary>
    EndPage,

    /// <summary>End the whole logical stream.</summary>
    EndStream,
}

/// <summary>Upstream <c>ogg::crc</c>: CRC-32 with the Vorbis/Ogg parameters (poly 0x04c11db7, no reflection).</summary>
internal static class OggCrc
{
    private static readonly uint[] Lookup = CreateLookup();

    public static uint Update(uint cur, ReadOnlySpan<byte> data)
    {
        var ret = cur;
        foreach (var b in data)
        {
            ret = (ret << 8) ^ Lookup[(b ^ (ret >> 24)) & 0xff];
        }

        return ret;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0, data);

    private static uint[] CreateLookup()
    {
        var table = new uint[256];
        for (uint idx = 0; idx < 256; idx++)
        {
            var r = idx << 24;
            for (var i = 0; i < 8; i++)
            {
                r = (r << 1) ^ ((r >> 31) & 1) * 0x04c11db7u;
            }

            table[idx] = r;
        }

        return table;
    }
}

/// <summary>Upstream <c>PageBaseInfo</c>: information about an Ogg page shared between multiple places.</summary>
internal sealed class OggPageBaseInfo
{
    /// <summary>The first packet is continued from the page before.</summary>
    public bool StartsWithContinued;

    /// <summary>This page is the first one in the logical bitstream.</summary>
    public bool FirstPage;

    /// <summary>This page is the last one in the logical bitstream.</summary>
    public bool LastPage;

    /// <summary>Absolute granule position.</summary>
    public ulong Absgp;

    /// <summary>Page counter.</summary>
    public uint SequenceNum;

    /// <summary>Page checksum.</summary>
    public uint Checksum;

    /// <summary>(offset, length) of each packet; the last one is the continued packet if <see cref="EndsWithContinued"/>.</summary>
    public List<(ushort Offs, ushort Len)> PacketPositions = [];

    /// <summary>The last packet is continued in subsequent page(s).</summary>
    public bool EndsWithContinued;
}

/// <summary>Upstream <c>PageParser</c> + <c>OggPage</c>: a fully parsed Ogg page.</summary>
internal sealed class OggPage
{
    public required OggPageBaseInfo Bi { get; init; }

    public uint StreamSerial { get; init; }

    /// <summary>Number of packet-ending segments.</summary>
    public ushort PacketCount { get; set; }

    /// <summary>The page body (packet data).</summary>
    public required byte[] Body { get; init; }

    /// <summary>Whether there is an ending packet in the page.</summary>
    public bool HasPacketEnd => Bi.PacketPositions.Count - (Bi.EndsWithContinued ? 1 : 0) > 0;

    /// <summary>Whether there is a packet that both starts and ends inside the page.</summary>
    public bool HasWholePacket =>
        Bi.PacketPositions.Count - (Bi.EndsWithContinued ? 1 : 0) - (Bi.StartsWithContinued ? 1 : 0) > 0;

    /// <summary>Whether there is a starting packet in the page.</summary>
    public bool HasPacketStart => Bi.PacketPositions.Count - (Bi.StartsWithContinued ? 1 : 0) > 0;

    /// <summary>
    /// Upstream <c>PageParser::new</c> + <c>parse_segments</c> + <c>parse_packet_data</c> (checksum verified). The
    /// stream structure version check of <c>PageParser::new</c> is done by the caller before it reads the segments,
    /// as in the crate. <paramref name="header"/> is modified (its CRC field is zeroed).
    /// </summary>
    public static OggPage Parse(byte[] header, byte[] segments, byte[] packetData)
    {
        var headerTypeFlag = header[5];
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(22));
        var bi = new OggPageBaseInfo
        {
            StartsWithContinued = (headerTypeFlag & 0x01) != 0,
            FirstPage = (headerTypeFlag & 0x02) != 0,
            LastPage = (headerTypeFlag & 0x04) != 0,
            Absgp = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(6)),
            SequenceNum = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(18)),
            Checksum = checksum,
        };

        // parse_segments
        ushort packetCount = 0;
        bi.EndsWithContinued = bi.StartsWithContinued;
        foreach (var val in segments)
        {
            if (val < 255)
            {
                packetCount++;
            }

            bi.EndsWithContinued = !(val < 255);
        }

        ushort curPacketSiz = 0;
        ushort curPacketOffs = 0;
        foreach (var val in segments)
        {
            curPacketSiz += val;
            if (val < 255)
            {
                bi.PacketPositions.Add((curPacketOffs, curPacketSiz));
                curPacketOffs += curPacketSiz;
                curPacketSiz = 0;
            }
        }

        if (bi.EndsWithContinued)
        {
            bi.PacketPositions.Add((curPacketOffs, curPacketSiz));
        }

        // parse_packet_data: verify the checksum with the CRC field zeroed (the checksum was read above).
        header.AsSpan(22, 4).Clear();
        var hashCalculated = OggCrc.Update(0, header);
        hashCalculated = OggCrc.Update(hashCalculated, segments);
        hashCalculated = OggCrc.Update(hashCalculated, packetData);
        if (checksum != hashCalculated)
        {
            throw OggReadException.HashMismatch();
        }

        return new OggPage
        {
            Bi = bi,
            StreamSerial = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(14)),
            PacketCount = packetCount,
            Body = packetData,
        };
    }

    /// <summary>The page body size the segment table announces.</summary>
    public static int BodySize(ReadOnlySpan<byte> segments)
    {
        var size = 0;
        foreach (var val in segments)
        {
            size += val;
        }

        return size;
    }
}

/// <summary>Upstream <c>BasePacketReader</c>: packet cache per logical stream, fed page by page.</summary>
internal sealed class OggBasePacketReader
{
    private sealed class PageInfo
    {
        public required OggPageBaseInfo Bi;
        public byte PacketIdx;
        public required byte[] PageBody;
        public List<byte[]> LastOverlapPck = [];

        public bool IsFirstPckInPg => PacketIdx == 0;

        public bool IsLastPckInPg => PacketIdx + 1 + (Bi.EndsWithContinued ? 1 : 0) == Bi.PacketPositions.Count;
    }

    private Dictionary<uint, PageInfo> _pageInfos = [];
    private uint? _streamWithStuff;
    private bool _hasSeeked;

    /// <summary>Extracts a packet from the cache, or null when a new page must be pushed first.</summary>
    public OggPacket? ReadPacket()
    {
        if (_streamWithStuff is not { } strSerial)
        {
            return null;
        }

        var pgInfo = _pageInfos[strSerial];
        var (offs, len) = pgInfo.Bi.PacketPositions[pgInfo.PacketIdx];

        // If there is a continued packet, and we are at the start right now,
        // and we actually have its end in the current page, glue it together.
        var needToGlue = pgInfo.PacketIdx == 0 &&
                         pgInfo.Bi.StartsWithContinued &&
                         !(pgInfo.Bi.EndsWithContinued && pgInfo.Bi.PacketPositions.Count == 1);
        byte[] packetContent;
        if (needToGlue)
        {
            int siz = len;
            foreach (var pck in pgInfo.LastOverlapPck)
            {
                siz += pck.Length;
            }

            packetContent = new byte[siz];
            var at = 0;
            foreach (var pck in pgInfo.LastOverlapPck)
            {
                pck.CopyTo(packetContent, at);
                at += pck.Length;
            }

            pgInfo.LastOverlapPck = [];
            pgInfo.PageBody.AsSpan(offs, len).CopyTo(packetContent.AsSpan(at));
        }
        else
        {
            packetContent = pgInfo.PageBody.AsSpan(offs, len).ToArray();
        }

        var firstPckInPg = pgInfo.IsFirstPckInPg;
        var firstPckOverall = pgInfo.Bi.FirstPage && firstPckInPg;

        var lastPckInPg = pgInfo.IsLastPckInPg;
        var lastPckOverall = pgInfo.Bi.LastPage && lastPckInPg;

        // Update the last read index.
        pgInfo.PacketIdx++;

        // Set stream_with_stuff to None so that future packet reads yield a page read first
        if (lastPckInPg)
        {
            _streamWithStuff = null;
        }

        return new OggPacket
        {
            Data = packetContent,
            FirstInPage = firstPckInPg,
            FirstInStream = firstPckOverall,
            LastInPage = lastPckInPg,
            LastInStream = lastPckOverall,
            AbsgpPage = pgInfo.Bi.Absgp,
            ChecksumPage = pgInfo.Bi.Checksum,
            StreamSerial = strSerial,
        };
    }

    /// <summary>Pushes a parsed Ogg page, updating the internal structures with its contents.</summary>
    public void PushPage(OggPage page)
    {
        var bi = page.Bi;
        if (_pageInfos.TryGetValue(page.StreamSerial, out var inf))
        {
            if (bi.FirstPage)
            {
                throw OggReadException.InvalidData();
            }

            if (bi.StartsWithContinued != inf.Bi.EndsWithContinued)
            {
                if (!_hasSeeked)
                {
                    throw OggReadException.InvalidData();
                }

                // If we have seeked, we are more tolerant here, and just drop the continued packet's content.
                inf.LastOverlapPck.Clear();
                if (bi.StartsWithContinued)
                {
                    bi.PacketPositions.RemoveAt(0);
                    if (page.PacketCount != 0)
                    {
                        // Decrease packet count by one. Normal case.
                        page.PacketCount--;
                    }
                    else
                    {
                        // If the packet count is 0, this means that we start and end with the same continued
                        // packet. So now as we ignore that packet, we must clear the ends_with_continued state.
                        bi.EndsWithContinued = false;
                    }
                }
            }
            else if (bi.StartsWithContinued)
            {
                // Remember the packet at the end so that it can be glued together once
                // we encounter the next segment with length < 255 (doesn't have to be in this page)
                var (offs, len) = inf.Bi.PacketPositions[inf.PacketIdx];
                if (len != inf.PageBody.Length)
                {
                    inf.LastOverlapPck.Add(inf.PageBody.AsSpan(offs, len).ToArray());
                }
                else
                {
                    // Little optimisation: don't copy if not necessary
                    inf.LastOverlapPck.Add(inf.PageBody);
                    inf.PageBody = [];
                }
            }

            inf.Bi = bi;
            inf.PacketIdx = 0;
            inf.PageBody = page.Body;
        }
        else
        {
            if (!_hasSeeked)
            {
                if (!bi.FirstPage || bi.StartsWithContinued)
                {
                    // If we haven't seeked, this is an error.
                    throw OggReadException.InvalidData();
                }
            }
            else if (bi.StartsWithContinued)
            {
                // Ignore the continued packet's content. This is a normal occurrence if we have just seeked.
                bi.PacketPositions.RemoveAt(0);
                if (page.PacketCount != 0)
                {
                    // Decrease packet count by one. Normal case.
                    page.PacketCount--;
                }
                else
                {
                    // We start and end with the same continued packet; as we ignore that packet,
                    // clear the ends_with_continued state as well.
                    bi.EndsWithContinued = false;
                }

                // Not actually needed, but good for consistency
                bi.StartsWithContinued = false;
            }

            _pageInfos[page.StreamSerial] = new PageInfo
            {
                Bi = bi,
                PacketIdx = 0,
                PageBody = page.Body,
            };
        }

        _streamWithStuff = page.PacketCount > 0 ? page.StreamSerial : null;
    }

    /// <summary>Resets the internal state after a seek.</summary>
    public void UpdateAfterSeek()
    {
        _streamWithStuff = null;
        _pageInfos = [];
        _hasSeeked = true;
    }
}

/// <summary>Upstream <c>ogg::PacketReader</c>: reads codec packets from an Ogg stream.</summary>
internal sealed class OggPacketReader
{
    private readonly Stream _rdr;
    private readonly OggBasePacketReader _basePckRdr = new();
    private bool _readSomePg;

    public OggPacketReader(Stream rdr)
    {
        _rdr = rdr;
    }

    /// <summary>The wrapped stream.</summary>
    public Stream Inner => _rdr;

    /// <summary>Reads a packet; null if the physical stream has ended.</summary>
    public OggPacket? ReadPacket()
    {
        // Read pages until we got a valid entire packet
        // (packets may span multiple pages, so reading one page doesn't always suffice)
        while (true)
        {
            if (_basePckRdr.ReadPacket() is { } pck)
            {
                return pck;
            }

            var page = ReadOggPage();
            if (page is null)
            {
                return null;
            }

            _basePckRdr.PushPage(page);
        }
    }

    /// <summary>Reads a packet; throws (an I/O error) if the physical stream has ended.</summary>
    public OggPacket ReadPacketExpected() =>
        ReadPacket() ?? throw OggReadException.ReadError(
            new EndOfStreamException("Expected ogg packet but found end of physical stream"));

    /// <summary>Resets the internal state by deleting all unread packets.</summary>
    public void DeleteUnreadPackets() => _basePckRdr.UpdateAfterSeek();

    /// <summary>
    /// Seeks to the first Ogg page that has an absgp greater or equal to <paramref name="posGoal"/>. In the case of
    /// continued packets, the seek may also end up at the last page before such a page that has a packet start.
    /// Returns whether the seek was successful.
    /// </summary>
    public bool SeekAbsgp(uint? streamSerial, ulong posGoal)
    {
        try
        {
            return SeekAbsgpCore(streamSerial, posGoal);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw OggReadException.ReadError(e);
        }
    }

    private bool SeekAbsgpCore(uint? streamSerial, ulong posGoal)
    {
        // Bisect seeking algo. Start by finding boundaries, e.g. at the start and end of the file, then bisect
        // those boundaries successively until a page is found.
        _rdr.Seek(0, SeekOrigin.Begin);
        if (ReadMatchSerial(streamSerial) is not { } begin)
        {
            return false;
        }

        var (beginPos, beginPg) = begin;

        // If the goal is the beginning, we are done.
        if (posGoal == 0)
        {
            return Found(beginPos);
        }

        // Seek to the end of the file to get the ending boundary
        // Upstream (ogg crate) note: the 200 KB is just a guessed number, any ideas to improve it?
        SeekBeforeEnd(200 * 1024);

        // pg_read_until_end_or_goal!
        long endPos;
        OggPage endPg;
        while (true)
        {
            if (ReadMatchSerial(streamSerial) is not { } next)
            {
                return false;
            }

            (endPos, endPg) = next;

            // If the absgp matches our goal, the seek process is done.
            if (endPg.Bi.Absgp == posGoal)
            {
                return Found(endPos);
            }

            // If we found a page past our goal, we already found a position that can serve as end post.
            if (endPg.Bi.Absgp > posGoal)
            {
                break;
            }

            // Stop the search if the stream has ended.
            if (endPg.Bi.LastPage)
            {
                return false;
            }
        }

        // Then perform the bisection
        while (true)
        {
            // Search is done if the two limits are the same page, or consecutive pages.
            if (unchecked(endPg.Bi.SequenceNum - beginPg.Bi.SequenceNum) <= 1)
            {
                return Found(endPos);
            }

            // Perform the bisection step
            var posToSeek = beginPos + (endPos - beginPos) / 2;
            _rdr.Seek(posToSeek, SeekOrigin.Begin);
            if (ReadMatchSerial(streamSerial) is not { } bisected)
            {
                return false;
            }

            var (pos, pg) = bisected;

            if (endPg.Bi.SequenceNum == pg.Bi.SequenceNum || beginPg.Bi.SequenceNum == pg.Bi.SequenceNum)
            {
                // The bisection seek doesn't bring us any further.
                // Switch to a linear seek to get the last details.
                var lastPacketEndPos = beginPos;
                _rdr.Seek(beginPos, SeekOrigin.Begin);
                while (true)
                {
                    var linearPos = _rdr.Position;
                    if (ReadOggPage() is not { } linearPg)
                    {
                        return false;
                    }

                    if (streamSerial is { } s && linearPg.StreamSerial != s)
                    {
                        // Continue the search if we encounter a page with a different stream serial
                    }
                    else if (linearPg.Bi.Absgp == ulong.MaxValue)
                    {
                        // ... or one with an absgp of -1.
                    }
                    else if (linearPg.Bi.Absgp >= posGoal)
                    {
                        // The page is found if the absgp is >= our goal
                        return Found(lastPacketEndPos);
                    }
                    else if (linearPg.HasPacketEnd)
                    {
                        lastPacketEndPos = linearPos;
                    }
                }
            }

            if (pg.Bi.Absgp >= posGoal)
            {
                endPos = pos;
                endPg = pg;
            }
            else
            {
                beginPos = pos;
                beginPg = pg;
            }
        }
    }

    /// <summary>The <c>found!</c> macro: seek to the page and flush the packet cache.</summary>
    private bool Found(long pos)
    {
        _rdr.Seek(pos, SeekOrigin.Begin);
        _basePckRdr.UpdateAfterSeek();
        return true;
    }

    /// <summary>The <c>pg_read_match_serial!</c> macro; null where the macro returns <c>Ok(false)</c>.</summary>
    private (long Pos, OggPage Page)? ReadMatchSerial(uint? streamSerial)
    {
        long? continuedPckStart = null;
        while (true)
        {
            var pos = _rdr.Position;
            var pg = ReadOggPage();
            if (pg is null)
            {
                return null;
            }

            if (streamSerial is { } s && pg.StreamSerial != s)
            {
                // Continue the search if we encounter a page with a different stream serial
                continue;
            }

            if (continuedPckStart is null)
            {
                if (pg.HasWholePacket)
                {
                    return (pos, pg);
                }

                if (pg.HasPacketStart)
                {
                    continuedPckStart = pos;
                }
            }
            else if (pg.HasPacketEnd)
            {
                // We have remembered a packet start, and have just encountered a packet end.
                // Return the position of the start with the info from the end (for the absgp).
                return (continuedPckStart.Value, pg);
            }
        }
    }

    private void SeekBeforeEnd(long offs)
    {
        var endPos = _rdr.Seek(0, SeekOrigin.End);
        var endPosToSeek = Math.Min(endPos, offs);
        _rdr.Seek(-endPosToSeek, SeekOrigin.End);
    }

    /// <summary>
    /// Parses and reads a new Ogg page. To support seeking this does not assume that the capture pattern is at the
    /// current reader position; it searches until it finds it.
    /// </summary>
    private OggPage? ReadOggPage()
    {
        try
        {
            var header = ReadUntilPgHeader();
            if (header is null)
            {
                return _readSomePg ? null : throw OggReadException.NoCapturePatternFound();
            }

            // PageParser::new
            if (header[4] != 0)
            {
                throw OggReadException.InvalidStreamStructVer();
            }

            var segments = new byte[header[26]];
            _rdr.ReadExactly(segments);

            var packetData = new byte[OggPage.BodySize(segments)];
            _rdr.ReadExactly(packetData);

            return OggPage.Parse(header, segments, packetData);
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ObjectDisposedException)
        {
            throw OggReadException.ReadError(e);
        }
    }

    /// <summary>
    /// Reads until the new page header and returns the 27 header bytes, recapturing (searching for "OggS") if no page
    /// header follows immediately. Null if the stream ended before a header.
    /// </summary>
    private byte[]? ReadUntilPgHeader()
    {
        var r = new UntilPageHeaderReader();
        var res = r.DoRead(_rdr);
        while (true)
        {
            switch (res)
            {
                case UntilPageHeaderResult.Eof:
                    return null;
                case UntilPageHeaderResult.Found:
                    // Keep track whether a page was read to distinguish non-Ogg files from Ogg files with
                    // trailing junk at read_ogg_page.
                    _readSomePg = true;
                    return r.IntoHeader();
                case UntilPageHeaderResult.ReadNeeded:
                    res = r.DoRead(_rdr);
                    break;
                default:
                    res = r.DoSeek(_rdr);
                    break;
            }
        }
    }

    private enum UntilPageHeaderResult
    {
        Eof,
        Found,
        ReadNeeded,
        SeekNeeded,
    }

    /// <summary>Upstream <c>UntilPageHeaderReader</c>.</summary>
    private sealed class UntilPageHeaderReader
    {
        private enum Mode
        {
            Searching,
            FoundWithNeeded,
            SeekNeeded,
            Found,
        }

        private readonly byte[] _retBuf = new byte[27];
        private readonly byte[] _buf = new byte[1024];
        private Mode _mode = Mode.Searching;
        private int _modeValue;

        // Capture pattern offset. Needed so that if we only partially recognized the capture pattern,
        // we later on only check the remaining part.
        private byte _cptOf;
        private long _readAmount;

        /// <summary>The offset of the last byte of the capture pattern, if found in <paramref name="arr"/>.</summary>
        private int? CheckArr(ReadOnlySpan<byte> arr)
        {
            for (var i = 0; i < arr.Length; i++)
            {
                switch (arr[i])
                {
                    case (byte)'O':
                        _cptOf = 1;
                        break;
                    case (byte)'g' when _cptOf is 1 or 2:
                        _cptOf++;
                        break;
                    case (byte)'S' when _cptOf == 3:
                        return i;
                    default:
                        _cptOf = 0;
                        break;
                }
            }

            return null;
        }

        public UntilPageHeaderResult DoRead(Stream rdr)
        {
            Span<byte> target;
            if (_readAmount < 27)
            {
                // Optimisation for the most likely case: the next page directly follows the current read position.
                target = _buf.AsSpan(0, (int)(27 - _readAmount));
            }
            else
            {
                switch (_mode)
                {
                    case Mode.Searching:
                        target = _buf;
                        break;
                    case Mode.FoundWithNeeded:
                        target = _buf.AsSpan(0, _modeValue);
                        break;
                    case Mode.SeekNeeded:
                        return UntilPageHeaderResult.SeekNeeded;
                    default:
                        return UntilPageHeaderResult.Found;
                }
            }

            var rdLen = rdr.Read(target);
            if (rdLen == 0)
            {
                // Reached EOF. Either there is no data but ogg data (legal end), or there is trailing garbage,
                // which players accept, so ignore it.
                return UntilPageHeaderResult.Eof;
            }

            _readAmount += rdLen;

            // 150 kb gives us a bit of safety: we can survive up to one page with a corrupted capture pattern
            // after having seeked right after a capture pattern of an earlier page.
            const int readAmountMax = 150 * 1024;
            if (_readAmount > readAmountMax)
            {
                // Exhaustive searching for the capture pattern has returned no ogg capture pattern.
                throw OggReadException.NoCapturePatternFound();
            }

            var rdBuf = target[..rdLen];

            int off;
            int needed;
            switch (_mode)
            {
                case Mode.Searching:
                    if (CheckArr(rdBuf) is not { } found)
                    {
                        return UntilPageHeaderResult.ReadNeeded;
                    }

                    _retBuf[0] = (byte)'O';
                    _retBuf[1] = (byte)'g';
                    _retBuf[2] = (byte)'g';
                    _retBuf[3] = (byte)'S';
                    off = found;
                    needed = 24;
                    break;
                case Mode.FoundWithNeeded:
                    off = 0;
                    needed = _modeValue;
                    break;
                default:
                    throw new InvalidOperationException("unimplemented UntilPageHeaderReader mode");
            }

            var fndBuf = rdBuf[off..];
            var copyAmount = Math.Min(needed, fndBuf.Length);
            var startFill = 27 - needed;
            fndBuf[..copyAmount].CopyTo(_retBuf.AsSpan(startFill, copyAmount));

            if (fndBuf.Length == needed)
            {
                // Capture pattern found!
                _mode = Mode.Found;
                return UntilPageHeaderResult.Found;
            }

            if (fndBuf.Length < needed)
            {
                // We still have to read some content.
                _mode = Mode.FoundWithNeeded;
                _modeValue = needed - copyAmount;
                return UntilPageHeaderResult.ReadNeeded;
            }

            // We have read too much content (exceeding the header).
            // Seek back so that we are at the position right after the header.
            _mode = Mode.SeekNeeded;
            _modeValue = needed - fndBuf.Length;
            return UntilPageHeaderResult.SeekNeeded;
        }

        public UntilPageHeaderResult DoSeek(Stream skr)
        {
            switch (_mode)
            {
                case Mode.Searching:
                case Mode.FoundWithNeeded:
                    return UntilPageHeaderResult.ReadNeeded;
                case Mode.SeekNeeded:
                    skr.Seek(_modeValue, SeekOrigin.Current);
                    _mode = Mode.Found;
                    return UntilPageHeaderResult.Found;
                default:
                    return UntilPageHeaderResult.Found;
            }
        }

        public byte[] IntoHeader() =>
            _mode == Mode.Found ? _retBuf : throw new InvalidOperationException("wrong mode");
    }
}

/// <summary>Upstream <c>ogg::PacketWriter</c> writing into an in-memory buffer (upstream <c>PacketWriter&lt;Vec&lt;u8&gt;&gt;</c>).</summary>
internal sealed class OggPacketWriter
{
    private sealed class CurrentPageValues
    {
        /// <summary>This page is the first one in the logical bitstream.</summary>
        public bool FirstPage = true;

        /// <summary>Page counter of the current page.</summary>
        public uint SequenceNum;

        /// <summary>Points to the first unwritten position in <see cref="CurPgLacing"/>.</summary>
        public byte SegmentCnt;

        public readonly byte[] CurPgLacing = new byte[255];

        /// <summary>The data and the absgp's of the packets.</summary>
        public readonly List<(byte[] Data, ulong Absgp)> CurPgData = new(255);

        /// <summary>Index in the last packet of the first byte that does not fit this page anymore.</summary>
        public int? PckThisOverflowIdx;

        /// <summary>Index in the first packet of the first byte not written in the previous page.</summary>
        public int? PckLastOverflowIdx;
    }

    private readonly MemoryStream _wtr = new();
    private readonly Dictionary<uint, CurrentPageValues> _pageVals = [];

    /// <summary>Whether no bytes are buffered (upstream <c>inner_mut().is_empty()</c>).</summary>
    public bool IsEmpty => _wtr.Length == 0;

    /// <summary>Takes the buffered bytes and clears the buffer (upstream <c>std::mem::take(wtr.inner_mut())</c>).</summary>
    public byte[] TakeData()
    {
        var data = _wtr.ToArray();
        _wtr.SetLength(0);
        return data;
    }

    /// <summary>Writes a packet.</summary>
    public void WritePacket(byte[] pckCont, uint serial, PacketWriteEndInfo inf, ulong absgp)
    {
        var isEndStream = inf == PacketWriteEndInfo.EndStream;
        if (!_pageVals.TryGetValue(serial, out var pg))
        {
            pg = new CurrentPageValues();
            _pageVals[serial] = pg;
        }

        var contLen = pckCont.Length;
        pg.CurPgData.Add((pckCont, absgp));

        var lastDataSegmentSize = (byte)(contLen % 255);
        var neededSegments = contLen / 255 + 1;
        var segmentInPageI = pg.SegmentCnt;
        var atPageEnd = false;
        for (var segmentI = 0; segmentI < neededSegments; segmentI++)
        {
            atPageEnd = false;

            // All segments but the last contain 255 bytes, the last one < 255 (including 0)
            pg.CurPgLacing[segmentInPageI] = segmentI + 1 < neededSegments ? (byte)255 : lastDataSegmentSize;
            pg.SegmentCnt = (byte)(segmentInPageI + 1);
            segmentInPageI = (byte)((segmentInPageI + 1) % 255);
            if (segmentInPageI == 0)
            {
                if (segmentI + 1 < neededSegments)
                {
                    // We have to flush a page, but we know there are more to come...
                    pg.PckThisOverflowIdx = (segmentI + 1) * 255;
                    WritePage(serial, pg, false);
                }
                else
                {
                    // We have to write a page end, and it's the very last we need to write
                    WritePage(serial, pg, isEndStream);

                    // Not actually required
                    pg.PckThisOverflowIdx = null;

                    // Required (it could have been Some(offs) before)
                    pg.PckLastOverflowIdx = null;
                }

                atPageEnd = true;
            }
        }

        if (inf != PacketWriteEndInfo.NormalPacket && !atPageEnd)
        {
            // Write a page end
            WritePage(serial, pg, isEndStream);
            pg.PckLastOverflowIdx = null;
        }
    }

    private void WritePage(uint serial, CurrentPageValues pg, bool lastPage)
    {
        // The page header with everything but the lacing values:
        var hdr = new byte[27];
        hdr[0] = 0x4f;
        hdr[1] = 0x67;
        hdr[2] = 0x67;
        hdr[3] = 0x53;
        hdr[4] = 0x00;
        byte flags = 0;
        if (pg.PckLastOverflowIdx is not null)
        {
            flags |= 0x01;
        }

        if (pg.FirstPage)
        {
            flags |= 0x02;
        }

        if (lastPage)
        {
            flags |= 0x04;
        }

        hdr[5] = flags;

        var pckData = pg.CurPgData;
        var lastFinishingPckAbsgp = ulong.MaxValue; // (-1i64) as u64
        for (var idx = 0; idx < pckData.Count; idx++)
        {
            if (!(idx + 1 == pckData.Count && pg.PckThisOverflowIdx is not null))
            {
                lastFinishingPckAbsgp = pckData[idx].Absgp;
            }
        }

        BinaryPrimitives.WriteUInt64LittleEndian(hdr.AsSpan(6), lastFinishingPckAbsgp);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(14), serial);
        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(18), pg.SequenceNum);

        // checksum (bytes 22..26), calculated later on
        hdr[26] = pg.SegmentCnt;

        var pgLacing = pg.CurPgLacing.AsSpan(0, pg.SegmentCnt);

        var hashCalculated = OggCrc.Update(0, hdr);
        hashCalculated = OggCrc.Update(hashCalculated, pgLacing);
        for (var idx = 0; idx < pckData.Count; idx++)
        {
            hashCalculated = OggCrc.Update(hashCalculated, PacketSlice(pg, idx));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(hdr.AsSpan(22), hashCalculated);

        // Now all is done, write the stuff!
        _wtr.Write(hdr);
        _wtr.Write(pgLacing);
        for (var idx = 0; idx < pckData.Count; idx++)
        {
            _wtr.Write(PacketSlice(pg, idx));
        }

        // Reset the page.
        pg.FirstPage = false;
        pg.SequenceNum++;
        pg.SegmentCnt = 0;

        // If we couldn't fully write the last packet, we need to keep it for the next page,
        // otherwise just clear everything.
        if (pg.PckThisOverflowIdx is not null)
        {
            var d = pg.CurPgData[^1];
            pg.CurPgData.Clear();
            pg.CurPgData.Add(d);
        }
        else
        {
            pg.CurPgData.Clear();
        }

        pg.PckLastOverflowIdx = pg.PckThisOverflowIdx;
        pg.PckThisOverflowIdx = null;
    }

    private static ReadOnlySpan<byte> PacketSlice(CurrentPageValues pg, int idx)
    {
        var pck = pg.CurPgData[idx].Data;
        var start = 0;
        if (idx == 0 && pg.PckLastOverflowIdx is { } lastIdx)
        {
            start = lastIdx;
        }

        var end = pck.Length;
        if (idx + 1 == pg.CurPgData.Count && pg.PckThisOverflowIdx is { } thisIdx)
        {
            end = thisIdx;
        }

        return pck.AsSpan(start, end - start);
    }
}
