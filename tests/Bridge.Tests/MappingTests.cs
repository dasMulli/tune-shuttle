using System.Net;
using Airplay2.Sender.Discovery;
using Bridge.Contracts;
using Bridge.Service.Api;
using Bridge.Service.Store;
using Xunit;

namespace Bridge.Tests;

public class MappingTests
{
    // Receiver A's records (a third-party AirPlay 2 speaker, srcvers 366.0) as browsed on 2026-10-09 (SPEC-control.md 1.7).
    private static readonly string[] SpeakerATxt =
    [
        "deviceid=02:1A:2B:3C:4D:5E", "features=0x445F8A00,0x1C340", "flags=0x404", "model=Speaker A", "manufacturer=Example Audio Maker",
        "pi=02:1A:2B:3C:4D:5E", "gid=02:1A:2B:3C:4D:5E", "srcvers=366.0", "acl=0", "pk=" + new string('a', 64),
    ];

    private static readonly string[] SpeakerARaop = ["et=0,4", "cn=0,1", "md=0,1,2", "tp=UDP", "vs=366.0", "sf=0x404"];

    private static DiscoveredReceiver SpeakerA(params string[] extraTxt) => new(
        "02:1A:2B:3C:4D:5E",
        "Test Speaker ABCD",
        [IPAddress.Parse("fe80::1"), IPAddress.Parse("192.0.2.26")],
        7000,
        AirplayTxtRecord.Parse([.. SpeakerATxt.Where(e => !extraTxt.Any(x => x.Split('=')[0] == e.Split('=')[0])), .. extraTxt]),
        RaopTxtRecord.Parse(SpeakerARaop),
        new DateTimeOffset(2026, 10, 9, 12, 0, 0, 123, TimeSpan.FromHours(2)));

    [Theory]
    [InlineData(PairingState.Transient, "transient")]
    [InlineData(PairingState.PinRequired, "pin_required")]
    [InlineData(PairingState.Paired, "paired")]
    [InlineData(PairingState.Disabled, "disabled")]
    public void Pairing_state_maps_both_ways(PairingState state, string stored)
    {
        Assert.Equal(stored, state.ToStoreValue());
        Assert.Equal(state, Mapping.ToPairingState(stored));
        Assert.True(PairingStates.IsValid(stored));
    }

    [Fact]
    public void Unknown_pairing_state_is_rejected()
    {
        Assert.Throws<FormatException>(() => Mapping.ToPairingState("PAIRED"));
        Assert.False(PairingStates.IsValid("PAIRED"));
    }

    [Theory]
    [InlineData(PairingRequirement.Transient, false, PairingState.Transient)]
    [InlineData(PairingRequirement.Transient, true, PairingState.Paired)]
    [InlineData(PairingRequirement.Pin, false, PairingState.PinRequired)]
    [InlineData(PairingRequirement.Pin, true, PairingState.Paired)]
    [InlineData(PairingRequirement.Password, false, PairingState.PinRequired)]
    [InlineData(PairingRequirement.Password, true, PairingState.Paired)]
    [InlineData(PairingRequirement.Disabled, true, PairingState.Disabled)]
    public void Pairing_state_follows_requirement_and_stored_keys(PairingRequirement requirement, bool stored, PairingState expected) =>
        Assert.Equal(expected, Mapping.PairingStateFor(requirement, stored));

    [Fact]
    public void Discovery_merges_into_a_new_row()
    {
        var row = SpeakerA().ToReceiverRow(existing: null, hasStoredPairing: false);
        Assert.Equal("02:1A:2B:3C:4D:5E", row.Id);
        Assert.Equal("Test Speaker ABCD", row.Name);
        Assert.Equal("Speaker A", row.Model);
        Assert.Equal("Example Audio Maker", row.Manufacturer);
        Assert.Equal("192.0.2.26", row.LastAddress); // IPv4 preferred over the link-local IPv6 listed first
        Assert.Equal(7000, row.LastPort);
        Assert.Equal(0x0001C340445F8A00L, row.Features);
        Assert.Equal(0x404L, row.StatusFlags);
        Assert.Equal(32, row.PublicKey!.Length);
        Assert.Equal(PairingStates.Transient, row.PairingState);
        Assert.Equal(SqliteBridgeStore.DefaultLevel, row.Level);
        Assert.Equal(0, row.OffsetFrames);
        Assert.Null(row.Quirks);
        Assert.Equal("2026-10-09T10:00:00.123Z", row.LastSeen);
    }

    [Fact]
    public void Discovery_keeps_user_columns_of_the_existing_row()
    {
        var existing = StoreTests.Receiver("02:1A:2B:3C:4D:5E", "Old name", level: 12) with { OffsetFrames = 88, Quirks = "{}", PairingState = PairingStates.Paired };
        var row = SpeakerA("flags=0x604").ToReceiverRow(existing, hasStoredPairing: true); // bit 9: PIN required
        Assert.Equal("Test Speaker ABCD", row.Name);
        Assert.Equal(12, row.Level);
        Assert.Equal(88, row.OffsetFrames);
        Assert.Equal("{}", row.Quirks);
        Assert.Equal(PairingStates.Paired, row.PairingState);

        Assert.Equal(PairingStates.PinRequired, SpeakerA("flags=0x604").ToReceiverRow(existing, hasStoredPairing: false).PairingState);
        Assert.Equal(PairingStates.Disabled, SpeakerA("acl=1").ToReceiverRow(existing, hasStoredPairing: true).PairingState);
    }

    [Fact]
    public void Online_receiver_dto_uses_discovery()
    {
        var discovered = SpeakerA();
        var row = discovered.ToReceiverRow(null, false) with { Level = 15, LastAddress = "10.0.0.9" };
        var dto = row.ToDto(discovered, ReceiverConnectionState.Connected, groupId: "G", error: null);

        Assert.True(dto.Online);
        Assert.Equal("192.0.2.26", dto.Address);
        Assert.Equal(7000, dto.Port);
        Assert.Equal(PairingState.Transient, dto.Pairing);
        Assert.Equal(ReceiverConnectionState.Connected, dto.State);
        Assert.Equal(15, dto.Level);
        Assert.Equal("G", dto.GroupId);
        var caps = dto.Capabilities;
        Assert.True(caps.AirPlay2);
        Assert.True(caps.Transient);
        Assert.False(caps.Pin);
        Assert.True(caps.Buffered);
        Assert.True(caps.Ptp);
        Assert.True(caps.Alac);
        Assert.True(caps.Text);
        Assert.True(caps.Artwork);
        Assert.True(caps.Progress);
        Assert.Equal("0x445F8A00,0x1C340", caps.Features);
        Assert.Equal("0x404", caps.Flags);
    }

    [Fact]
    public void Offline_receiver_dto_uses_the_stored_row()
    {
        var discovered = SpeakerA();
        var row = discovered.ToReceiverRow(null, false);
        var offline = row.ToDto(null, ReceiverConnectionState.Offline);
        var online = row.ToDto(discovered, ReceiverConnectionState.Available);

        Assert.False(offline.Online);
        Assert.Equal(row.LastAddress, offline.Address);
        Assert.Equal(row.LastPort, offline.Port);
        Assert.Null(offline.GroupId);
        // Without the RAOP record the metadata kinds still follow from the feature bits 15-17.
        Assert.Equal(online.Capabilities, offline.Capabilities);
    }

    [Fact]
    public void Receiver_dto_maps_back_to_the_row()
    {
        var row = SpeakerA().ToReceiverRow(null, false) with { Features = long.MinValue | 0x200, Quirks = """{"a":1}""", OffsetFrames = -5 };
        var dto = row.ToDto(null, ReceiverConnectionState.Offline);
        Assert.Equal(row, dto.ToRow(row) with { PublicKey = row.PublicKey });
        Assert.Same(row.PublicKey, dto.ToRow(row).PublicKey);

        var fresh = dto.ToRow(null);
        Assert.Null(fresh.PublicKey);
        Assert.Null(fresh.Quirks);
        Assert.Equal(row.Features, fresh.Features);
    }

    [Fact]
    public void Group_maps_with_member_names_states_and_order()
    {
        var receivers = new Dictionary<string, ReceiverRow>
        {
            ["R1"] = StoreTests.Receiver("R1", "Kitchen"),
            ["R2"] = StoreTests.Receiver("R2", "Bath"),
        };
        var group = new GroupRow("G1", "Down", 70, [new GroupMemberRow("G1", "R2", 30, 5), new GroupMemberRow("G1", "R1", 60, 1), new GroupMemberRow("G1", "GONE", 10, 9)]);

        var dto = group.ToDto(receivers, active: true, id => id == "R1" ? ReceiverConnectionState.Streaming : ReceiverConnectionState.Available);
        Assert.Equal("G1", dto.Id);
        Assert.Equal("Down", dto.Name);
        Assert.Equal(70, dto.MasterLevel);
        Assert.True(dto.Active);
        Assert.Equal(
            [
                new GroupMemberDto("R1", "Kitchen", 60, ReceiverConnectionState.Streaming),
                new GroupMemberDto("R2", "Bath", 30, ReceiverConnectionState.Available),
                new GroupMemberDto("GONE", "GONE", 10, ReceiverConnectionState.Available),
            ],
            dto.Members);

        var back = dto.ToRow();
        Assert.Equal(["R1", "R2", "GONE"], back.Members.Select(m => m.ReceiverId));
        Assert.Equal([0, 1, 2], back.Members.Select(m => m.Position));
        Assert.All(back.Members, m => Assert.Equal("G1", m.GroupId));
        Assert.Equal(ReceiverConnectionState.Offline, Assert.Single(group.ToDto(receivers, false).Members, m => m.ReceiverId == "R2").State);
    }

    [Fact]
    public void Output_dto_for_group_receiver_and_nothing()
    {
        var receivers = new Dictionary<string, ReceiverRow> { ["R1"] = StoreTests.Receiver("R1", "Kitchen", level: 42) };
        var group = new GroupRow("G1", "Down", 70, [new GroupMemberRow("G1", "R1", 60, 0)]);

        var forGroup = Mapping.ToOutputDto(group, null, receivers);
        Assert.Equal("G1", forGroup.GroupId);
        Assert.Null(forGroup.ReceiverId);
        Assert.Equal(70, forGroup.MasterLevel);
        Assert.Equal(60, Assert.Single(forGroup.Members).Level);

        var forReceiver = Mapping.ToOutputDto(null, receivers["R1"], receivers, _ => ReceiverConnectionState.Connected);
        Assert.Null(forReceiver.GroupId);
        Assert.Equal("R1", forReceiver.ReceiverId);
        Assert.Equal(42, forReceiver.MasterLevel);
        Assert.Equal(new GroupMemberDto("R1", "Kitchen", 42, ReceiverConnectionState.Connected), Assert.Single(forReceiver.Members));

        var none = Mapping.ToOutputDto(null, null, receivers);
        Assert.Null(none.GroupId);
        Assert.Null(none.ReceiverId);
        Assert.Empty(none.Members);
    }

    [Fact]
    public async Task Mapped_rows_persist_through_the_store()
    {
        await using var store = new SqliteBridgeStore(":memory:", Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteBridgeStore>.Instance);
        await store.InitializeAsync(CancellationToken.None);
        var row = SpeakerA().ToReceiverRow(null, false);
        await store.UpsertReceiverAsync(row, CancellationToken.None);
        var loaded = await store.GetReceiverAsync(row.Id, CancellationToken.None);
        Assert.Equal(row with { PublicKey = null }, loaded! with { PublicKey = null });
        Assert.Equal(row.PublicKey, loaded.PublicKey);
    }
}
