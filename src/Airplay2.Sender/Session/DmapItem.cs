namespace Airplay2.Sender.Session;

/// <summary>
/// The decoded <c>mlit</c> item of a DMAP metadata body (SPEC-receiver.md 4.8, SPEC-control.md 5.2): the tags
/// <see cref="DmapEncoder"/> writes, read back; unknown tags are kept by their four-letter code.
/// </summary>
/// <param name="Title"><c>minm</c>.</param>
/// <param name="Artist"><c>asar</c>.</param>
/// <param name="Album"><c>asal</c>.</param>
/// <param name="Duration"><c>astm</c> (milliseconds on the wire).</param>
/// <param name="TrackNumber"><c>astn</c>.</param>
/// <param name="DiscNumber"><c>asdn</c>.</param>
/// <param name="Genre"><c>asgn</c>.</param>
/// <param name="OtherTags">Every other tag by its code with its raw value (for example <c>asal</c>'s siblings <c>ascp</c>, <c>asgn</c>); empty when none.</param>
public sealed record DmapItem(
    string? Title,
    string? Artist,
    string? Album,
    TimeSpan? Duration,
    int? TrackNumber = null,
    int? DiscNumber = null,
    string? Genre = null,
    IReadOnlyDictionary<string, byte[]>? OtherTags = null)
{
    /// <summary>The item as the <see cref="TrackMetadata"/> the now-playing path forwards to the receivers (SPEC-receiver.md R10, 5.5).</summary>
    public TrackMetadata ToTrackMetadata() => new(Title, Artist, Album, Duration, TrackNumber, DiscNumber, Genre);
}
