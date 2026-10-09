// Tests for core/src/login5.rs (upstream has no #[cfg(test)] module).
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Google.Protobuf;
using Librespot.Core;
using Spotify.Login5.V3;
using Spotify.Login5.V3.ChallengeTypes;
using Spotify.Login5.V3.Credentials;
using Xunit;

namespace Librespot.Tests.Core;

public class Login5Tests
{
    // Same vector as UtilTests (computed with an independent Python port of `solve_hash_cash`).
    private static readonly byte[] LoginContext = Convert.FromHexString("6c6962726573706f742d68617368636173682d637478");
    private static readonly byte[] Prefix = Convert.FromHexString("00112233445566778899aabbccddeeff");
    private const string ExpectedSuffix = "6cce5c2f8b9ec1ef0000000000000010";

    private static LoginResponse ChallengeResponse(params Challenge[] challenges)
    {
        var response = new LoginResponse
        {
            LoginContext = ByteString.CopyFrom(LoginContext),
            Challenges = new Challenges(),
        };
        response.Challenges.Challenges_.Add(challenges);
        return response;
    }

    [Fact]
    public void Handle_challenges_solves_hashcash_and_copies_login_context()
    {
        var request = new LoginRequest { StoredCredential = new StoredCredential { Username = "user" } };
        var response = ChallengeResponse(
            new Challenge(), // empty: skipped
            new Challenge { Hashcash = new HashcashChallenge { Prefix = ByteString.CopyFrom(Prefix), Length = 10 } });

        Login5Manager.HandleChallenges(request, response);

        Assert.Equal(LoginContext, request.LoginContext.ToByteArray());
        var solution = Assert.Single(request.ChallengeSolutions.Solutions);
        Assert.Equal(ChallengeSolution.SolutionOneofCase.Hashcash, solution.SolutionCase);
        Assert.Equal(ExpectedSuffix, Convert.ToHexStringLower(solution.Hashcash.Suffix.Span));
        Assert.NotNull(solution.Hashcash.Duration);
        Assert.True(solution.Hashcash.Duration.Seconds >= 0);
        Assert.InRange(solution.Hashcash.Duration.Nanos, 0, 999_999_999);

        // the suffix really solves the challenge
        var md = SHA1.HashData([.. Prefix, .. solution.Hashcash.Suffix.ToByteArray()]);
        Assert.True(BitOperations.TrailingZeroCount(BinaryPrimitives.ReadInt64BigEndian(md.AsSpan(12, 8))) >= 10);

        // the login method is untouched
        Assert.Equal("user", request.StoredCredential.Username);
    }

    [Fact]
    public void Handle_challenges_appends_to_existing_solutions()
    {
        var request = new LoginRequest();
        var response = ChallengeResponse(
            new Challenge { Hashcash = new HashcashChallenge { Prefix = ByteString.CopyFrom(Prefix), Length = 10 } });

        Login5Manager.HandleChallenges(request, response);
        Login5Manager.HandleChallenges(request, response);

        Assert.Equal(2, request.ChallengeSolutions.Solutions.Count);
    }

    [Fact]
    public void Code_challenge_is_unsupported()
    {
        var request = new LoginRequest();
        var response = ChallengeResponse(new Challenge { Code = new CodeChallenge { CodeLength = 6 } });

        var e = Assert.Throws<Login5Exception>(() => Login5Manager.HandleChallenges(request, response));
        Assert.Equal(Login5Exception.Reason.CodeChallenge, e.Cause);
        Assert.Equal(ErrorKind.Unimplemented, e.Kind);
        Assert.Equal("Code challenge is not supported", e.Detail);
    }

    [Fact]
    public void Error_texts_and_kinds_match_upstream()
    {
        var faulty = Login5Exception.FaultyRequest(LoginError.InvalidCredentials);
        Assert.Equal(ErrorKind.FailedPrecondition, faulty.Kind);
        Assert.Equal("Login request was denied: INVALID_CREDENTIALS", faulty.Detail);
        Assert.Equal(LoginError.InvalidCredentials, faulty.LoginError);

        var retries = Login5Exception.RetriesFailed(Login5Manager.MaxLoginTries);
        Assert.Equal(ErrorKind.FailedPrecondition, retries.Kind);
        Assert.Equal("Couldn't successfully authenticate after 3 times", retries.Detail);

        Assert.Equal(ErrorKind.Unavailable, Login5Exception.NoStoredCredentials().Kind);
        Assert.Equal("Tried to acquire token without stored credentials", Login5Exception.NoStoredCredentials().Detail);
        Assert.Equal(ErrorKind.Unavailable, Login5Exception.OnlyForMobile().Kind);
        Assert.Equal("Login via login5 is only allowed for android or ios", Login5Exception.OnlyForMobile().Detail);
    }

    [Fact]
    public void Token_from_login()
    {
        var token = Login5Manager.TokenFromLogin("abc", 1800);
        Assert.Equal("abc", token.AccessToken);
        Assert.Equal(TimeSpan.FromSeconds(1800), token.ExpiresIn);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Empty(token.Scopes);

        // a negative lifetime cannot be converted to u64 upstream and falls back to one hour
        Assert.Equal(TimeSpan.FromHours(1), Login5Manager.TokenFromLogin("abc", -5).ExpiresIn);
    }

    [Fact]
    public async Task Auth_token_requires_stored_credentials()
    {
        var session = new Session(SessionConfig.Default(), null);
        var e = await Assert.ThrowsAsync<Login5Exception>(() => session.Login5.AuthTokenAsync());
        Assert.Equal(Login5Exception.Reason.NoStoredCredentials, e.Cause);
    }

    [Fact]
    public async Task Login_is_only_for_mobile()
    {
        if (Platform.Os is "android" or "ios")
        {
            return;
        }

        var session = new Session(SessionConfig.Default(), null);
        var e = await Assert.ThrowsAsync<Login5Exception>(() => session.Login5.LoginAsync("id", "password"));
        Assert.Equal(Login5Exception.Reason.OnlyForMobile, e.Cause);
    }
}
