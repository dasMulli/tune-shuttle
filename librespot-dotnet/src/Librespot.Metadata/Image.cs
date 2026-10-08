// Port of metadata/src/image.rs.
using Librespot.Core;
using ImageSizeEnum = Spotify.Metadata.Image.Types.Size;
using ProtoImage = Spotify.Metadata.Image;
using ProtoImageGroup = Spotify.Metadata.ImageGroup;
using ProtoPictureSize = Spotify.Playlist4.Proto.PictureSize;
using ProtoTranscodedPicture = SpotifyPlaylistAnnotate3.Proto.TranscodedPicture;

namespace Librespot.Metadata;

/// <summary>
/// Upstream <c>pub use protocol::metadata::image::Size as ImageSize</c>. C# has no re-export of a nested enum, so
/// this class only gives the name a home; the enum itself is <c>Spotify.Metadata.Image.Types.Size</c>.
/// </summary>
public static class ImageSize
{
    /// <summary>The <c>DEFAULT</c> size.</summary>
    public static ImageSizeEnum Default => ImageSizeEnum.Default;
}

/// <summary>An image file reference.</summary>
/// <param name="Id">The image file id.</param>
/// <param name="Size">The size class.</param>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
public sealed record Image(FileId Id, ImageSizeEnum Size, int Width, int Height)
{
    /// <summary>Upstream <c>From&lt;&amp;ImageMessage&gt;</c>.</summary>
    public static Image From(ProtoImage image) =>
        new(FileId.From(image), Util.EnumOrDefault(image.Size, ImageSizeEnum.Default), image.Width, image.Height);
}

/// <summary>Upstream <c>Images(Vec&lt;Image&gt;)</c>.</summary>
public sealed class Images : List<Image>
{
    /// <summary>Creates an empty list.</summary>
    public Images()
    {
    }

    /// <summary>Wraps the given images.</summary>
    public Images(IEnumerable<Image> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_from_repeated!(ImageMessage, Images)</c>.</summary>
    public Images(IEnumerable<ProtoImage> messages) : base(messages.Select(Image.From))
    {
    }

    /// <summary>Upstream <c>From&lt;&amp;ImageGroup&gt; for Images</c>.</summary>
    public Images(ProtoImageGroup imageGroup) : this(imageGroup.Image)
    {
    }
}

/// <summary>A named picture size of a playlist picture.</summary>
/// <param name="TargetName">The size name.</param>
/// <param name="Url">The picture URL.</param>
public sealed record PictureSize(string TargetName, string Url)
{
    /// <summary>Upstream <c>From&lt;&amp;PictureSizeMessage&gt;</c>.</summary>
    public static PictureSize From(ProtoPictureSize size) => new(size.TargetName, size.Url);
}

/// <summary>Upstream <c>PictureSizes(Vec&lt;PictureSize&gt;)</c>.</summary>
public sealed class PictureSizes : List<PictureSize>
{
    /// <summary>Creates an empty list.</summary>
    public PictureSizes()
    {
    }

    /// <summary>Wraps the given sizes.</summary>
    public PictureSizes(IEnumerable<PictureSize> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_from_repeated!(PictureSizeMessage, PictureSizes)</c>.</summary>
    public PictureSizes(IEnumerable<ProtoPictureSize> messages) : base(messages.Select(PictureSize.From))
    {
    }
}

/// <summary>A transcoded variant of a playlist annotation picture.</summary>
/// <param name="TargetName">The variant name.</param>
/// <param name="Uri">The picture URI.</param>
public sealed record TranscodedPicture(string TargetName, SpotifyUri Uri)
{
    /// <summary>Upstream <c>TryFrom&lt;&amp;TranscodedPictureMessage&gt;</c>.</summary>
    /// <exception cref="LibrespotException">The URI could not be converted.</exception>
    public static TranscodedPicture From(ProtoTranscodedPicture picture) =>
        new(picture.TargetName, SpotifyUri.From(picture));
}

/// <summary>Upstream <c>TranscodedPictures(Vec&lt;TranscodedPicture&gt;)</c>.</summary>
public sealed class TranscodedPictures : List<TranscodedPicture>
{
    /// <summary>Creates an empty list.</summary>
    public TranscodedPictures()
    {
    }

    /// <summary>Wraps the given pictures.</summary>
    public TranscodedPictures(IEnumerable<TranscodedPicture> items) : base(items)
    {
    }

    /// <summary>Upstream <c>impl_try_from_repeated!(TranscodedPictureMessage, TranscodedPictures)</c>.</summary>
    /// <exception cref="LibrespotException">An element failed to convert.</exception>
    public TranscodedPictures(IEnumerable<ProtoTranscodedPicture> messages) : base(messages.Select(TranscodedPicture.From))
    {
    }
}
