using Librespot.Core;
using Librespot.Core.Dealer;
using Xunit;

namespace Librespot.Tests.Core;

// Upstream core/src/dealer/maps.rs and split_uri (dealer/mod.rs) have no tests; these pin the upstream semantics.
public class DealerMapsTests
{
    private static string[] Split(string uri) => Dealer.SplitUri(uri)!;

    [Theory]
    [InlineData("hm://pusher/v1/connections/", new[] { "hm", "pusher", "v1", "connections" })]
    [InlineData("hm://connect-state/v1/cluster", new[] { "hm", "connect-state", "v1", "cluster" })]
    [InlineData("spotify:user:attributes:update", new[] { "spotify", "user", "attributes", "update" })]
    [InlineData("social-connect/v2/session_update", new[] { "", "social-connect", "v2", "session_update" })]
    [InlineData("hm://a//b///", new[] { "hm", "a", "", "b" })]
    [InlineData("hm://", new[] { "hm", "" })]
    public void SplitUri_matches_upstream(string uri, string[] expected)
    {
        Assert.Equal(expected, Split(uri));
    }

    [Theory]
    [InlineData("no-separator")]
    [InlineData("spotify")]
    [InlineData("")]
    public void SplitUri_rejects_uris_without_separator(string uri)
    {
        Assert.Null(Dealer.SplitUri(uri));
    }

    [Fact]
    public void HandlerMap_leaf_handles_everything_below_it()
    {
        var map = new HandlerMap<string>();
        map.Insert(Split("hm://connect-state/v1/player/command"), "h");

        Assert.Equal("h", map.Get(Split("hm://connect-state/v1/player/command")));
        Assert.Equal("h", map.Get(Split("hm://connect-state/v1/player/command/deeper/still")));
        Assert.Null(map.Get(Split("hm://connect-state/v1/player")));
        Assert.Null(map.Get(Split("hm://connect-state/v1/other")));
    }

    [Fact]
    public void HandlerMap_rejects_overlapping_handlers()
    {
        var map = new HandlerMap<string>();
        map.Insert(Split("hm://a/b"), "ab");

        // below an existing leaf
        var below = Assert.Throws<HandlerMapException>(() => map.Insert(Split("hm://a/b/c"), "abc"));
        Assert.Equal(ErrorKind.Aborted, below.Kind);
        Assert.Equal("request was already handled", below.Detail);

        // same path
        Assert.Throws<HandlerMapException>(() => map.Insert(Split("hm://a/b"), "again"));

        // above an existing leaf (branch with children)
        Assert.Throws<HandlerMapException>(() => map.Insert(Split("hm://a"), "a"));

        // sibling is fine
        map.Insert(Split("hm://a/c"), "ac");
        Assert.Equal("ac", map.Get(Split("hm://a/c/x")));
    }

    [Fact]
    public void HandlerMap_remove_prunes_empty_branches()
    {
        var map = new HandlerMap<string>();
        map.Insert(Split("hm://a/b/c"), "abc");

        Assert.Null(map.Remove(Split("hm://a/x")));
        Assert.Equal("abc", map.Remove(Split("hm://a/b/c")));
        Assert.Null(map.Get(Split("hm://a/b/c")));
        Assert.False(map.Contains("hm"));

        // the path is free again
        map.Insert(Split("hm://a"), "a");
        Assert.Equal("a", map.Get(Split("hm://a/b/c")));
    }

    [Fact]
    public void HandlerMap_contains_only_checks_the_first_level_key()
    {
        var map = new HandlerMap<string>();
        map.Insert(Split("hm://a/b"), "ab");

        // upstream quirk: `contains(uri)` compares the whole string against the root's children
        Assert.True(map.Contains("hm"));
        Assert.False(map.Contains("hm://a/b"));
    }

    [Fact]
    public void SubscriberMap_delivers_by_prefix_and_reports_handled()
    {
        var map = new SubscriberMap<string>();
        map.Insert(Split("hm://pusher/v1/connections/"), "pusher");
        map.Insert(Split("hm://connect-state/v1/cluster"), "cluster1");
        map.Insert(Split("hm://connect-state/v1/cluster"), "cluster2");

        var seen = new List<string>();
        Assert.True(map.Retain(Split("hm://pusher/v1/connections/abc%3D"), s =>
        {
            seen.Add(s);
            return true;
        }));
        Assert.Equal(new[] { "pusher" }, seen);

        seen.Clear();
        Assert.True(map.Retain(Split("hm://connect-state/v1/cluster"), s =>
        {
            seen.Add(s);
            return true;
        }));
        Assert.Equal(new[] { "cluster1", "cluster2" }, seen);

        seen.Clear();
        Assert.False(map.Retain(Split("hm://connect-state/v1/connect/volume"), s =>
        {
            seen.Add(s);
            return true;
        }));
        Assert.Empty(seen);

        // a shorter path does not reach subscribers below it
        Assert.False(map.Retain(Split("hm://connect-state/v1"), _ => true));
    }

    [Fact]
    public void SubscriberMap_retain_drops_rejected_subscribers_and_prunes()
    {
        var map = new SubscriberMap<string>();
        map.Insert(Split("hm://a/b"), "keep");
        map.Insert(Split("hm://a/b"), "drop");

        Assert.True(map.Retain(Split("hm://a/b"), s => s == "keep"));
        Assert.Equal(new[] { "keep" }, map.Values());

        // dropping the last subscriber prunes the branch; the call still counts as handled
        Assert.True(map.Retain(Split("hm://a/b"), _ => false));
        Assert.True(map.IsEmpty);
        Assert.False(map.Retain(Split("hm://a/b"), _ => true));
    }

    [Fact]
    public void SubscriberMap_retain_short_circuits_like_upstream()
    {
        var map = new SubscriberMap<string>();
        map.Insert(Split("hm://a"), "shallow");
        map.Insert(Split("hm://a/b"), "deep");

        var seen = new List<string>();
        Assert.True(map.Retain(Split("hm://a/b"), s =>
        {
            seen.Add(s);
            return true;
        }));

        // `handled_by_any || y.retain(..)`: the deeper subscriber is skipped once a shallower one was called
        Assert.Equal(new[] { "shallow" }, seen);
    }

    [Fact]
    public void SubscriberMap_contains_matches_upstream()
    {
        var map = new SubscriberMap<string>();
        Assert.False(map.Contains(Split("hm://a")));

        map.Insert(Split("hm://a/b"), "ab");
        Assert.True(map.Contains(Split("hm://a/b")));
        Assert.True(map.Contains(Split("hm://a/b/c")));
        Assert.False(map.Contains(Split("hm://a/c")));
        // the path ends above a subscriber: contained because the node is not empty
        Assert.True(map.Contains(Split("hm://a")));
    }
}
