// Tests for core/src/token.rs (upstream has no #[cfg(test)] module).
using Librespot.Core;
using Xunit;

namespace Librespot.Tests.Core;

public class TokenTests
{
    [Fact]
    public void From_json_parses_keymaster_response()
    {
        const string json = """
            {"accessToken":"BQD-token","expiresIn":3600,"tokenType":"Bearer","scope":["streaming","user-read-private"],"extra":1}
            """;

        var before = DateTimeOffset.UtcNow;
        var token = Token.FromJson(json);

        Assert.Equal("BQD-token", token.AccessToken);
        Assert.Equal(TimeSpan.FromHours(1), token.ExpiresIn);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(["streaming", "user-read-private"], token.Scopes);
        Assert.InRange(token.Timestamp, before, DateTimeOffset.UtcNow);
        Assert.False(token.IsExpired);
    }

    [Theory]
    [InlineData("""{"expiresIn":3600,"tokenType":"Bearer","scope":[]}""")] // missing accessToken
    [InlineData("""{"accessToken":"x","expiresIn":-1,"tokenType":"Bearer","scope":[]}""")] // u64
    [InlineData("""{"access_token":"x","expires_in":1,"token_type":"Bearer","scope":[]}""")] // camelCase only
    [InlineData("not json")]
    [InlineData("null")]
    public void From_json_rejects_malformed_bodies(string json)
    {
        var e = Assert.ThrowsAny<LibrespotException>(() => Token.FromJson(json));
        Assert.Equal(ErrorKind.FailedPrecondition, e.Kind);
    }

    [Fact]
    public void Expiry_honours_the_ten_second_threshold()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(TimeSpan.FromSeconds(10), Token.ExpiryThreshold);

        Assert.False(new Token("t", TimeSpan.FromSeconds(60), "Bearer", [], now).IsExpired);
        // 60 s lifetime received 55 s ago: within the threshold, so expired
        Assert.True(new Token("t", TimeSpan.FromSeconds(60), "Bearer", [], now.AddSeconds(-55)).IsExpired);
        // shorter than the threshold: saturates to zero lifetime
        Assert.True(new Token("t", TimeSpan.FromSeconds(5), "Bearer", [], now.AddSeconds(-1)).IsExpired);
        Assert.True(new Token("t", TimeSpan.Zero, "Bearer", [], now.AddMilliseconds(-1)).IsExpired);
        // huge lifetimes do not overflow
        Assert.False(new Token("t", TimeSpan.MaxValue, "Bearer", [], now).IsExpired);
    }

    [Fact]
    public void Scopes()
    {
        var token = new Token("t", TimeSpan.FromHours(1), "Bearer", ["streaming", "user-read-private"], DateTimeOffset.UtcNow);

        Assert.True(token.InScope("streaming"));
        Assert.False(token.InScope("app-remote-control"));
        Assert.True(token.InScopes(["streaming", "user-read-private"]));
        Assert.True(token.InScopes([]));
        Assert.False(token.InScopes(["streaming", "app-remote-control"]));
    }

    [Fact]
    public async Task Token_provider_rejects_empty_client_id()
    {
        var session = new Session(SessionConfig.Default(), null);
        var e = await Assert.ThrowsAnyAsync<LibrespotException>(() => session.TokenProvider.GetTokenWithClientIdAsync("streaming", ""));
        Assert.Equal(ErrorKind.InvalidArgument, e.Kind);
        Assert.Equal("Client ID cannot be empty", e.Detail);
    }

    [Fact]
    public void Token_error_text()
    {
        var e = TokenException.Empty();
        Assert.Equal(TokenException.Reason.Empty, e.Cause);
        Assert.Equal(ErrorKind.Unavailable, e.Kind);
        Assert.Equal("no tokens available", e.Detail);
    }
}
