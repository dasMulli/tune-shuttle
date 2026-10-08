// Tests for metadata/src/audio/item.rs (availability and cover logic), restriction.rs and error.rs.
using Librespot.Core;
using Librespot.Metadata;
using Librespot.Metadata.Audio;
using Xunit;
using static Librespot.Tests.Metadata.MetadataFixtures;
using ProtoImage = Spotify.Metadata.Image;
using ProtoRestriction = Spotify.Metadata.Restriction;

namespace Librespot.Tests.Metadata;

public class ItemTests
{
    private static UserData User(string country, string? catalogue = null)
    {
        var attributes = new UserAttributes();
        if (catalogue is not null)
        {
            attributes["catalogue"] = catalogue;
        }

        return new UserData { Country = country, Attributes = attributes };
    }

    private static Restrictions Restrict(string catalogue, string? allowed = null, string? forbidden = null)
    {
        var message = new ProtoRestriction { CatalogueStr = { catalogue } };
        if (allowed is not null)
        {
            message.CountriesAllowed = allowed;
        }

        if (forbidden is not null)
        {
            message.CountriesForbidden = forbidden;
        }

        return new Restrictions([message]);
    }

    [Fact]
    public void TrackRestrictedForCountryXX()
    {
        var track = Track.Parse(TrackMessage(), new SpotifyUri.Track(SpotifyId.FromBase62(TrackBase62)));

        Assert.Equal(UnavailabilityReason.Blacklisted, AudioItem.AvailableForUser(User("XX"), track.Availability, track.Restrictions));
        Assert.Null(AudioItem.AvailableForUser(User("DE"), track.Availability, track.Restrictions));
        // The restriction only applies to the "premium" catalogue.
        Assert.Null(AudioItem.AvailableForUser(User("XX", "free"), track.Availability, track.Restrictions));
    }

    [Fact]
    public void Whitelist()
    {
        var restrictions = Restrict("premium", allowed: "DEFRXX");
        Assert.Null(AudioItem.AllowedForUser(User("XX"), restrictions));
        Assert.Equal(UnavailabilityReason.NotWhitelisted, AudioItem.AllowedForUser(User("US"), restrictions));
    }

    [Fact]
    public void FirstMatchingRestrictionDecides()
    {
        var restrictions = new Restrictions(Restrict("premium", allowed: "XX").Concat(Restrict("premium", forbidden: "XX")));
        Assert.Null(AudioItem.AllowedForUser(User("XX"), restrictions));
    }

    [Fact]
    public void RestrictionWithoutCountryListsIsIgnored()
    {
        var restrictions = new Restrictions(Restrict("premium").Concat(Restrict("premium", forbidden: "XX")));
        Assert.Equal(UnavailabilityReason.Blacklisted, AudioItem.AllowedForUser(User("XX"), restrictions));
    }

    [Fact]
    public void Embargo()
    {
        var past = new Availability(["premium"], Date.FromTimestampMs(0));
        var future = new Availability(["premium"], Date.From(DateTimeOffset.UtcNow.AddDays(30)));

        Assert.Null(AudioItem.Available([]));
        Assert.Null(AudioItem.Available([past]));
        Assert.Null(AudioItem.Available([future, past]));
        Assert.Equal(UnavailabilityReason.Embargo, AudioItem.Available([future]));

        // Embargo is checked before the restrictions.
        Assert.Equal(UnavailabilityReason.Embargo, AudioItem.AvailableForUser(User("XX"), [future], Restrict("premium", forbidden: "XX")));
    }

    [Fact]
    public void CoversSortedWidestFirstWithTemplate()
    {
        var images = new Images(
        [
            new ProtoImage { FileId = Bytes(20, 1), Width = 64, Height = 64, Size = ProtoImage.Types.Size.Small },
            new ProtoImage { FileId = Bytes(20, 2), Width = 640, Height = 640, Size = ProtoImage.Types.Size.Large },
            new ProtoImage { FileId = Bytes(20, 3), Width = 640, Height = 600, Size = ProtoImage.Types.Size.Xlarge },
        ]);

        var covers = AudioItem.GetCovers(images, "https://i.scdn.co/image/{file_id}");

        Assert.Equal([640, 640, 64], covers.Select(c => c.Width));
        // Stable sort: equal widths keep their order.
        Assert.Equal([640, 600, 64], covers.Select(c => c.Height));
        Assert.Equal("https://i.scdn.co/image/" + Convert.ToHexStringLower(Bytes(20, 2).ToByteArray()), covers[0].Url);
        Assert.Equal(ProtoImage.Types.Size.Large, covers[0].Size);
    }

    [Fact]
    public void ParseCountryCodes()
    {
        Assert.Equal(["DE", "FR", "XX"], Restriction.ParseCountryCodes("DEFRXX"));
        Assert.Empty(Restriction.ParseCountryCodes(""));
    }

    [Fact]
    public void RestrictionWithoutTypeDefaultsToStreaming()
    {
        var restriction = Restriction.From(new ProtoRestriction { Catalogue = { ProtoRestriction.Types.Catalogue.Subscription, (ProtoRestriction.Types.Catalogue)99 } });
        Assert.Equal(ProtoRestriction.Types.Type.Streaming, restriction.RestrictionType);
        Assert.Equal([ProtoRestriction.Types.Catalogue.Subscription, ProtoRestriction.Types.Catalogue.Ad], restriction.Catalogues);
        Assert.Null(restriction.CountriesAllowed);
        Assert.Null(restriction.CountriesForbidden);
    }

    [Fact]
    public void MetadataErrorsAreUnavailable()
    {
        Assert.Equal("Service unavailable { audio item duration can not be: -1 }", MetadataException.InvalidDuration(-1).Message);
        foreach (var e in new[]
                 {
                     MetadataException.Empty(), MetadataException.NonPlayable(), MetadataException.InvalidDuration(0),
                     MetadataException.ExplicitContentFiltered(),
                 })
        {
            Assert.Equal(ErrorKind.Unavailable, e.Kind);
        }

        Assert.Equal("empty response", MetadataException.Empty().Detail);
        Assert.Equal("audio item is non-playable when it should be", MetadataException.NonPlayable().Detail);
        Assert.Equal("audio item duration can not be: -1", MetadataException.InvalidDuration(-1).Detail);
        Assert.Equal("track is marked as explicit, which client setting forbids", MetadataException.ExplicitContentFiltered().Detail);
    }

    [Fact]
    public async Task GetFileRejectsNonPlayableUris()
    {
        var local = SpotifyUri.FromUri("spotify:local:Artist:Album:Title:180");
        var e = await Assert.ThrowsAsync<MetadataException>(() => AudioItem.GetFileAsync(new Session(SessionConfig.Default(), null), local));
        Assert.Equal(MetadataException.Reason.NonPlayable, e.Cause);
    }
}
