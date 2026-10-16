using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

public class P675MissionDiscoveryTests
{
    internal static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(id, 0, default, 0, Vector3.Zero,
        Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);
    private static void Feed(ConnectionSession session, params IFedMessage[] messages) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server, FedPacket.Pack(messages.Select(m => m.Encode()).ToArray()));
    private static ExportCluster Sector(short id, string name) => new()
        { Cluster = new DataClusterIGC(1, [0, 0, 1], 0, 0, 0, id, 0, 0, name, "", "", 0, 0, 0, true, false) };
    private static ClientWorld World(ConnectionSession session)
    {
        Feed(session, new SetCluster { SectorId = 1, Ship = Pose(10) },
            new ExportShip { ShipId = -1, SideId = 0 },
            new ExportShip { ShipId = 10, SideId = 0, CharacterName = "me" },
            new ExportShip { ShipId = 11, SideId = 0, CharacterName = "Scout" },
            new ExportShip { ShipId = 12, SideId = 1, CharacterName = "Ally" },
            new ChangeAlliances { Allies = [0, 0, -1, -1, -1, -1] }, Sector(1, "Alpha"), Sector(2, "Beta"),
            new ExportStation { Station = new DataStationIGC([0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 0], 1, 2, 20, 0, 255, 255, "Garrison") },
            new ExportStation { Station = new DataStationIGC([0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0, 0], 1, 1, 21, 0, 255, 255, "Outpost") },
            new ExportProbe { Probe = new DataProbeExport { ProbeId = 30, SideId = 0, ClusterId = 1 } },
            new ExportWarp { Warp = new DataWarpIGC(40, 41, 100, "", "", "Aleph", [0, 0, 0], [0, 0, 1], [0, 0, 0], 1, 1) },
            new ExportWarp { Warp = new DataWarpIGC(41, 40, 100, "", "", "Aleph", [0, 0, 0], [0, 0, 1], [0, 0, 0], 1, 2) },
            new ExportAsteroid { Asteroid = new DataAsteroidIGC(1, [0, 0, 0], [0, 1, 0], [0, 0, 1], [0, 0, 0], 1, 1, 0x20, 50, 100, 100, "", "", "", 2, "Si1", 255) });
        return session.World;
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Join_dispatch_loads_shared_core_and_assigns_identity_before_launch_then_playerinfo_sets_side()
    {
        Require.Artwork();
        using var a = new ConnectionSession(); using var b = new ConnectionSession();
        var definition = new MissionDef { SzIGCStaticFile = "PCore001", DwCookie = 123 };
        Feed(a, definition, new JoinedMission { ShipId = 10, DwCookie = 123 });
        Feed(b, definition, new JoinedMission { ShipId = 11, DwCookie = 123 });
        Assert.Equal<short?>(10, a.World.JoinedShipId);
        Assert.Null(a.World.MyShipId); // membership is not a launch assignment
        Assert.Null(a.World.MySideId);
        Assert.Null(a.World.Ships[10].Latest);
        Assert.True(a.World.Ships[10].IsMine);
        Assert.Equal(-1, a.World.Ships[10].Player!.ISide);
        Assert.Equal(123u, a.World.JoinedMissionCookie);
        Assert.NotEmpty(a.World.MissionCore!.HullTypes);
        Assert.Same(a.World.MissionCore, b.World.MissionCore);
        Assert.DoesNotContain(JoinedMission.Fmid, a.UnknownFmids);
        Feed(a, new PlayerInfo { ShipId = 10, ISide = 2, CharacterName = "Pilot" });
        Assert.Equal<short?>(2, a.World.MySideId);
        Assert.Equal(2, a.World.Ships[10].SideId);
        Assert.Equal("Pilot", a.World.Ships[10].Player!.CharacterName);
        Feed(a, new SetCluster { Ship = Pose(10), SectorId = 1 });
        Assert.Equal<short?>(10, a.World.MyShipId);
        Assert.NotNull(a.World.Ships[10].Latest);
        a.World.Reset();
        Assert.Null(a.World.MissionCore); Assert.Null(a.World.JoinedMissionCookie); Assert.Null(a.World.JoinedShipId);
    }

    [Fact]
    public void Failed_core_rejects_join_without_assigning_identity_or_consuming_later_packet_messages()
    {
        using var session = new ConnectionSession();
        session.World.MissionCoreResolver = _ => null;
        Feed(session, new MissionDef { SzIGCStaticFile = "missing" }, new JoinedMission { ShipId = 10 },
            new PlayerInfo { ShipId = 99, CharacterName = "must not apply" });
        Assert.True(session.Lost);
        Assert.NotNull(session.World.JoinError);
        Assert.Null(session.World.MyShipId);
        Assert.Empty(session.World.Ships);
    }

    [Theory]
    [InlineData(0, 10, "You've")]
    [InlineData(0, 11, "Scout has")]
    [InlineData(0, 12, "Ally (Ally) has")]
    [InlineData(1, 20, "Allied Garrison has")]
    [InlineData(4, 30, "One of your team's probes has")]
    public void Discovery_formats_native_spotter_and_enemy_station_critical_feedback(short type, short id, string prefix)
    {
        using var session = new ConnectionSession(); var world = World(session);
        Feed(session, new ObjectSpotted { OtObject = 1, OidObject = 20, OtSpotter = type, OidSpotter = id });
        world.TakeSoundEvents(); // independent audio drains must preserve PostText feedback
        var feedback = Assert.Single(world.TakeFeedbackLines());
        Assert.Equal(prefix + " discovered an enemy Garrison in sector Alpha", feedback.Text);
        Assert.True(feedback.IsCritical);
        Assert.Contains(world.Events.Chats, c => c.Message == feedback.Text);
        Assert.DoesNotContain(ObjectSpotted.Fmid, session.UnknownFmids);
        Assert.Empty(world.TakeFeedbackLines());
    }

    [Fact]
    public void Allied_discovery_is_noncritical_and_nonzero_wing_suppresses_other_critical_discoveries()
    {
        using var session = new ConnectionSession(); var world = World(session);
        Feed(session, new ObjectSpotted { OtObject = 1, OidObject = 21, OtSpotter = 0, OidSpotter = 10 });
        var allied = Assert.Single(world.TakeFeedbackLines());
        Assert.Equal("You've discovered an allied Outpost in sector Alpha", allied.Text); Assert.False(allied.IsCritical);
        Feed(session, new SetWingId { ShipID = 10, WingId = 3 },
            new ObjectSpotted { OtObject = 7, OidObject = 40, OtSpotter = 0, OidSpotter = 11 },
            new ObjectSpotted { OtObject = 5, OidObject = 50, OtSpotter = 0, OidSpotter = 11 });
        var lines = world.TakeFeedbackLines();
        Assert.Equal("Scout has discovered an aleph from sector Alpha to sector Beta", lines[0].Text);
        Assert.Equal("Scout has discovered a Silicon asteroid in sector Beta", lines[1].Text);
        Assert.All(lines, l => Assert.False(l.IsCritical));
        Assert.Equal(1, world.Ships[10].SectorId); // receipt never moves the viewer
    }

    [Fact]
    public void Discovery_ignores_missing_entities_and_lobby_and_bounds_pending_feedback()
    {
        using var session = new ConnectionSession();
        Feed(session, new ObjectSpotted { OtObject = 1, OidObject = 20, OtSpotter = 0, OidSpotter = 10 });
        Assert.Empty(session.World.FeedbackLines);
        var world = World(session);
        Feed(session, new ObjectSpotted { OtObject = 1, OidObject = 999, OtSpotter = 0, OidSpotter = 10 },
            new ObjectSpotted { OtObject = 1, OidObject = 20, OtSpotter = 0, OidSpotter = 999 });
        Assert.Empty(world.FeedbackLines);
        for (int i = 0; i < 100; i++) world.Apply(new ObjectSpotted { OtObject = 1, OidObject = 20, OtSpotter = 0, OidSpotter = 10 });
        Assert.Equal(32, world.FeedbackLines.Count);
        world.Reset(); Assert.Empty(world.FeedbackLines);
    }

    [Fact]
    public async Task Session_gate_serializes_discovery_receive_and_reader_drains()
    {
        using var session = new ConnectionSession(); World(session);
        var writer = Task.Run(() => { for (int i = 0; i < 100; i++) Feed(session,
            new ObjectSpotted { OtObject = 1, OidObject = 20, OtSpotter = 0, OidSpotter = 11 }); });
        var reader = Task.Run(() => { for (int i = 0; i < 100; i++) lock (session.Gate)
            { Assert.InRange(session.World.TakeFeedbackLines().Count, 0, 32); foreach (var _ in session.World.Ships) { } } });
        await Task.WhenAll(writer, reader);
        Assert.DoesNotContain(ObjectSpotted.Fmid, session.UnknownFmids);
    }
}
