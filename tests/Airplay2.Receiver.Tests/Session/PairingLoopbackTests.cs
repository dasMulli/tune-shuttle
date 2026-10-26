using Airplay2.Receiver.Pairing;
using Airplay2.Receiver.Session;
using Airplay2.Sender.Pairing;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Tlv8;
using Xunit;
using static Airplay2.Receiver.Tests.Session.ControlPlaneTestSupport;

namespace Airplay2.Receiver.Tests.Session;

/// <summary>
/// The pairing modes of SPEC-receiver.md R7 and 4.2 against the product control plane on loopback, driven by our own
/// <see cref="HapPairingClient"/> (SPEC-receiver.md 11.1): transient by default, password mode with persisted controllers.
/// </summary>
public class PairingLoopbackTests
{
    private const string Password = "loopback-secret";

    [Fact]
    public async Task Transient_pairing_encrypts_the_channel_and_unlocks_the_session_methods()
    {
        var (plane, _) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);

        // 4.1: unpaired, only /info, OPTIONS, fp-setup, auth-setup and the pairing POSTs are answered.
        Assert.Equal(470, (await sender.SendAsync(RtspRequest.Create("RECORD", sender.Uri))).StatusCode);
        Assert.Equal(200, (await sender.SendAsync(SessionRequests.Info(withQualifier: false))).StatusCode);

        await sender.PairTransientAsync();

        Assert.True(sender.Connection.IsEncrypted);
        Assert.Equal(PairingKind.Transient, sender.Pairing!.Kind);
        var record = await sender.SendAsync(SessionRequests.Record(sender.Uri));
        Assert.Equal(200, record.StatusCode);
        Assert.Equal("0", record.Header(RtspHeaders.AudioLatency));
        Assert.True(plane.Sessions.Single().IsPaired);
    }

    [Fact]
    public async Task Open_mode_regular_pair_setup_fails_at_m3_with_error_2_and_is_not_refused_with_470()
    {
        var (plane, _) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);

        // R7: M1 is accepted (nothing answered 470); nobody knows the random 8-digit PIN, so M3 fails with Error 2.
        var started = await sender.Client.StartPinPairingAsync(sender.Connection, ControllerIdentity.Generate(), TlvMethod.PairSetup, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<PairingException>(() => sender.Client.FinishPinPairingAsync(started, "1234", CancellationToken.None));

        Assert.Equal(PairingFailure.Authentication, failure.Failure);
        Assert.Equal(TlvError.Authentication, failure.TlvError);
    }

    [Fact]
    public async Task Password_mode_pairs_with_the_password_persists_the_controller_and_verifies_on_a_second_connection()
    {
        var store = new InMemoryControllerStore();
        var identity = ReceiverIdentity.Create();
        var controller = ControllerIdentity.Generate();
        PairingCredentials credentials;
        {
            var (plane, _) = await StartAsync(Options(Password, store, identity: identity));
            await using var _plane = plane;
            await using (var first = await TestSender.ConnectAsync(plane))
            {
                var started = await first.Client.StartPinPairingAsync(first.Connection, controller, TlvMethod.PairSetup, CancellationToken.None);
                credentials = await first.Client.FinishPinPairingAsync(started, Password, CancellationToken.None);
            }

            Assert.Equal(identity.DeviceId, credentials.AccessoryId);
            Assert.Equal(identity.PublicKey, credentials.AccessoryPublicKey);
            Assert.True(store.Contains(controller.Id)); // 4.2: written through to the store at M5

            await using var second = await TestSender.ConnectAsync(plane);
            var session = await second.Client.VerifyAsync(second.Connection, credentials, CancellationToken.None);
            second.Use(session);
            Assert.Equal(PairingKind.Verified, session.Kind);
            Assert.Equal(200, (await second.SendAsync(SessionRequests.Record(second.Uri))).StatusCode);
        }

        // After a restart the registry is seeded from the store (4.2), so pair-verify still works.
        var (restarted, _) = await StartAsync(Options(Password, store, identity: identity));
        await using var _restarted = restarted;
        await using var third = await TestSender.ConnectAsync(restarted);
        var again = await third.Client.VerifyAsync(third.Connection, credentials, CancellationToken.None);
        third.Use(again);
        Assert.Equal(200, (await third.SendAsync(SessionRequests.Record(third.Uri))).StatusCode);
    }

    [Fact]
    public async Task Password_mode_wrong_password_fails_with_error_2()
    {
        var store = new InMemoryControllerStore();
        var (plane, _) = await StartAsync(Options(Password, store));
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);

        var started = await sender.Client.StartPinPairingAsync(sender.Connection, ControllerIdentity.Generate(), TlvMethod.PairSetup, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<PairingException>(() => sender.Client.FinishPinPairingAsync(started, "not-the-password", CancellationToken.None));

        Assert.Equal(PairingFailure.Authentication, failure.Failure);
        Assert.Equal(TlvError.Authentication, failure.TlvError);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Password_mode_refuses_transient_m1_with_470()
    {
        var (plane, _) = await StartAsync(Options(Password));
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);

        var failure = await Assert.ThrowsAsync<PairingException>(sender.PairTransientAsync);

        Assert.Equal(PairingFailure.TransientRefused, failure.Failure);
        Assert.Equal(470, failure.StatusCode);
    }

    [Fact]
    public async Task Pair_verify_with_an_unknown_controller_is_rejected()
    {
        var (plane, _) = await StartAsync(Options(Password));
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);
        var identity = plane.Options.Identity;
        var stranger = new PairingCredentials(ControllerIdentity.Generate(), identity.DeviceId, identity.PublicKey);

        var failure = await Assert.ThrowsAsync<PairingException>(() => sender.Client.VerifyAsync(sender.Connection, stranger, CancellationToken.None));

        Assert.Equal(PairingFailure.CredentialsRejected, failure.Failure);
    }

    [Fact]
    public async Task Password_change_applies_to_new_connections()
    {
        var (plane, _) = await StartAsync();
        await using var _plane = plane;
        await plane.UpdatePasswordAsync(Password, CancellationToken.None);
        await using var sender = await TestSender.ConnectAsync(plane);

        var failure = await Assert.ThrowsAsync<PairingException>(sender.PairTransientAsync);

        Assert.Equal(470, failure.StatusCode);
    }

    [Fact]
    public async Task Auth_setup_and_options_are_answered_unpaired()
    {
        var (plane, _) = await StartAsync();
        await using var _plane = plane;
        await using var sender = await TestSender.ConnectAsync(plane);

        var auth = await sender.SendAsync(SessionRequests.AuthSetup(new byte[32]));
        var options = await sender.SendAsync(RtspRequest.Create("OPTIONS", "*"));

        Assert.Equal(200, auth.StatusCode);
        Assert.Equal(ControlSession.AuthSetupResponseSize, auth.Body.Length);
        Assert.Equal(200, options.StatusCode);
        Assert.Equal(ControlSession.PublicMethods, options.Header(RtspHeaders.Public));
    }
}
