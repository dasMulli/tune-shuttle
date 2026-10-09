using Airplay2.Sender.Pairing;
using Airplay2.Sender.Tlv8;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Airplay2.Sender.Tests.Pairing;

/// <summary>The accessory side on its own: request validation and the pair_ap response conventions.</summary>
public class HapAccessoryPairingTests
{
    private static HapAccessoryPairing NewAccessory(int maxControllers = 16) =>
        new(new HapAccessoryOptions(ControllerIdentity.Generate(), "1234") { MaxControllers = maxControllers });

    [Fact]
    public void Pin_start_is_answered_with_an_empty_200()
    {
        var response = NewAccessory().HandleRequest(HapPairingEndpoints.PinStart, []);
        Assert.Equal(200, response.Status);
        Assert.Empty(response.Body);
        Assert.Null(response.Session);
    }

    [Fact]
    public void Unknown_paths_get_404_and_malformed_bodies_470()
    {
        var accessory = NewAccessory();
        Assert.Equal(404, accessory.HandleRequest("/pair-add", [0x06, 0x01, 0x01]).Status);
        Assert.Equal(470, accessory.HandleRequest(HapPairingEndpoints.PairSetup, [0x06, 0x05, 0x01]).Status);
    }

    [Fact]
    public void Messages_out_of_sequence_are_refused_with_470()
    {
        var accessory = NewAccessory();
        var m3 = new Tlv8Document().AddState(3).Add(TlvType.PublicKey, new byte[384]).Add(TlvType.Proof, new byte[64]);
        Assert.Equal(470, accessory.HandlePairSetup(m3).Status);
        Assert.Equal(470, accessory.HandlePairSetup(new Tlv8Document().AddState(5).Add(TlvType.EncryptedData, new byte[40])).Status);
        Assert.Equal(470, accessory.HandlePairVerify(new Tlv8Document().AddState(3).Add(TlvType.EncryptedData, new byte[40])).Status);
        Assert.Equal(470, accessory.HandlePairSetup(new Tlv8Document().AddState(7)).Status);
    }

    [Fact]
    public void M1_without_method_and_verify_M1_with_a_short_key_are_refused()
    {
        var accessory = NewAccessory();
        Assert.Equal(470, accessory.HandlePairSetup(new Tlv8Document().AddState(1)).Status);
        Assert.Equal(470, accessory.HandlePairVerify(new Tlv8Document().AddState(1).Add(TlvType.PublicKey, new byte[31])).Status);
    }

    [Fact]
    public void Transient_and_regular_M1_answer_with_salt_and_B()
    {
        var accessory = NewAccessory();
        var m2 = Tlv8Document.Decode(accessory.HandlePairSetup(new Tlv8Document().AddMethod(TlvMethod.PairSetup).AddState(1).Add(TlvType.Flags, (byte)TlvFlags.Transient)).Body);
        Assert.Equal(2, m2.State);
        Assert.Equal(16, m2.Get(TlvType.Salt)!.Length);
        Assert.InRange(m2.Get(TlvType.PublicKey)!.Length, 380, 384);

        // A four-byte big-endian flags value (airplay2-receiver's reading) is understood as well.
        var refusing = new HapAccessoryPairing(accessory.Options with { AllowTransient = false });
        Assert.Equal(470, refusing.HandlePairSetup(new Tlv8Document().AddMethod(TlvMethod.PairSetup).AddState(1).Add(TlvType.Flags, [0, 0, 0, 0x10])).Status);
        Assert.Equal(200, refusing.HandlePairSetup(new Tlv8Document().AddMethod(TlvMethod.PairSetup).AddState(1)).Status);
    }

    [Fact]
    public async Task Registry_full_answers_MaxPeers()
    {
        var accessory = NewAccessory(maxControllers: 1);
        var client = new HapPairingClient(NullLogger<HapPairingClient>.Instance);

        await using (var first = new InMemoryRtspConnection(accessory.ForConnection()))
        {
            var started = await client.StartPinPairingAsync(first, ControllerIdentity.Generate(), TlvMethod.PairSetup, CancellationToken.None);
            await client.FinishPinPairingAsync(started, "1234", CancellationToken.None);
        }

        await using var second = new InMemoryRtspConnection(accessory.ForConnection());
        var again = await client.StartPinPairingAsync(second, ControllerIdentity.Generate(), TlvMethod.PairSetup, CancellationToken.None);
        var ex = await Assert.ThrowsAsync<PairingException>(() => client.FinishPinPairingAsync(again, "1234", CancellationToken.None));
        Assert.Equal(TlvError.MaxPeers, ex.TlvError);
        Assert.Equal(PairingFailure.Protocol, ex.Failure);
        Assert.Equal(1, accessory.KnownControllers.Count);
    }

    [Fact]
    public void Views_share_the_registry_but_not_the_procedure_state()
    {
        var accessory = NewAccessory();
        var view = accessory.ForConnection();
        Assert.Same(accessory.KnownControllers, view.KnownControllers);
        Assert.Same(accessory.Options, view.Options);

        // M1 on one view, M3 on the other: the second has no SRP session.
        accessory.HandlePairSetup(new Tlv8Document().AddMethod(TlvMethod.PairSetup).AddState(1));
        Assert.Equal(470, view.HandlePairSetup(new Tlv8Document().AddState(3).Add(TlvType.PublicKey, new byte[384]).Add(TlvType.Proof, new byte[64])).Status);
    }

    [Fact]
    public void Registry_enforces_the_limit_for_new_controllers_and_copies_keys()
    {
        var registry = new HapControllerRegistry();
        var key = new byte[32];
        key[0] = 1;
        Assert.True(registry.TryAddOrUpdate("a", key, maxControllers: 1));
        Assert.False(registry.TryAddOrUpdate("b", key, maxControllers: 1));
        Assert.True(registry.TryAddOrUpdate("a", new byte[32], maxControllers: 1)); // a known controller re-pairs
        key[0] = 2;
        Assert.True(registry.TryGet("a", out var stored));
        Assert.Equal(new byte[32], stored);
        stored[0] = 3;
        Assert.True(registry.TryGet("a", out var again));
        Assert.Equal(0, again[0]);
        registry.Set("b", key);
        Assert.Equal(2, registry.Count);
        Assert.True(registry.Remove("b"));
        Assert.False(registry.TryGet("b", out _));
    }
}
