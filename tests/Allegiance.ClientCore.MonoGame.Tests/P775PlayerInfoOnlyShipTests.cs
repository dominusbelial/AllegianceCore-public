using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P775 test 7 (targeting half) — after P775 every client holds ShipEntities
/// created by PLAYERINFO alone (hull 0, SectorId 0, no pose) for every enemy
/// ship and drone in every sector. The Step-0 render/target audit found no
/// leak (CanDrawInSector needs PoseSectorId + Latest; the real
/// GatherSectorCandidates needs Latest and the viewer's sector), and this test
/// pins the target-cycling half against the REAL production gather, including
/// the viewer sitting in sector 0 itself. The draw gate is pinned by
/// P775ClientPlayerInfoMomentsTests (CanDrawInSector for every sector).
/// </summary>
public sealed class P775PlayerInfoOnlyShipTests
{
    private static ServerShipUpdate Pose(short id, float x = 0)
    {
        var bytes = new byte[ServerShipUpdate.Width];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes, id);
        System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(12), x);
        var reader = new WireReader(bytes);
        return ServerShipUpdate.DecodeBody(ref reader);
    }

    private static ClientWorld World()
    {
        var world = new ClientWorld();
        // The viewer: a seated ship on side 0 in sector 7, with a real pose.
        world.Apply(new ExportShip { ShipId = 10, SideId = 0 });
        world.Apply(new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "me", PilotType = PlayerInfo.PilotTypePlayer, DtidDrone = PlayerInfo.NA });
        world.Apply(new JoinSide { ShipId = 10, SideId = 0 });
        world.Apply(new SetCluster { Ship = Pose(10, 0), SectorId = 7 });
        // A PLAYERINFO for an unseen enemy DRONE — the join-burst seed of a
        // drone in another sector (native fsmission.cpp:3958-3968).
        world.Apply(new PlayerInfo
        {
            ShipId = 20, ISide = 1, CharacterName = "Ship 20",
            PilotType = Ship.PilotTypeMiner, DtidDrone = 40,
        });
        // A PLAYERINFO for an unseen enemy HUMAN in another sector — the same
        // join-burst seed (P775 review: the team pane leak).
        world.Apply(new PlayerInfo
        {
            ShipId = 30, ISide = 1, CharacterName = "Foe",
            PilotType = PlayerInfo.PilotTypePlayer, DtidDrone = PlayerInfo.NA,
        });
        return world;
    }

    /// <summary>P775 review (3a) — an unseen enemy known only from PLAYERINFO
    /// (SectorId/StationId still the C# default 0) must not read DOCKED nor
    /// name cluster 0 in the team pane: native draws SectorName(NA) =
    /// "unknown" (teampane.cpp:1577-1583, 1901-1905) for a player whose ship
    /// status never landed (status reset in AddPlayerToSide,
    /// clintlib.cpp:4139). Cluster 0 is given a name here so a leak shows.</summary>
    [Fact]
    public void Playerinfo_only_enemy_reads_unknown_in_the_team_pane_not_docked()
    {
        var world = World();
        world.Apply(new ExportCluster { Cluster = new Allegiance.Core.DataClusterIGC(1, [0, 0, 1], 0xffffffff, 0, 0, 0, 0, 0, "Zero", "", "", 0, 0, 0, true, false) });
        world.Apply(new ExportCluster { Cluster = new Allegiance.Core.DataClusterIGC(1, [0, 0, 1], 0xffffffff, 0, 0, 7, 0, 0, "Home", "", "", 0, 0, 0, true, false) });
        Assert.Equal("Zero", world.Clusters[0].Name);
        world.MarkApplied();   // P774 — end of the received batch: the derived roster rebuilds here
        var foe = world.Ships[30];
        Assert.False(foe.StatusSectorKnown);

        var briefs = world.Ships.Values.ToDictionary(e => e.CharacterName,
            e => TeamPaneLayout.BriefOf(world, e, _ => ""));
        var lines = TeamPaneLayout.Build(world.ScoreRoster, [], briefs, null, null, null, false, "me");
        var foeLine = lines.Single(l => l.Kind == TeamPaneLayout.LineKind.Pilot && l.Text.Contains("Foe")).Text;
        Assert.DoesNotContain("DOCKED", foeLine);
        Assert.DoesNotContain("Zero", foeLine);
        Assert.Contains(TeamPaneLayout.UnknownSector, foeLine);
        var me = lines.Single(l => l.Kind == TeamPaneLayout.LineKind.Pilot && l.Text.Contains("me ")).Text;
        Assert.Contains("Home", me);   // a ship with real status still shows its sector

        // The SECTOR sort key reads the same "unknown" and no sector id.
        var row = TeamPaneLayout.SortRow(world, world.ScoreRoster.Single(e => e.CharacterName == "Foe"), _ => "");
        Assert.Equal(TeamPaneLayout.UnknownSector, row.SectorName);
        Assert.Equal(-1, row.SectorId);

        // Once a real status lands, the pane shows it.
        world.Apply(new ShipStatusMessage { ShipId = 30, HullId = 1, SectorId = 0, StationId = 5 });
        world.MarkApplied();
        briefs = world.Ships.Values.ToDictionary(e => e.CharacterName, e => TeamPaneLayout.BriefOf(world, e, _ => ""));
        lines = TeamPaneLayout.Build(world.ScoreRoster, [], briefs, null, null, null, false, "me");
        Assert.Contains("DOCKED", lines.Single(l => l.Kind == TeamPaneLayout.LineKind.Pilot && l.Text.Contains("Foe")).Text);
    }

    /// <summary>P775 review (3b) — the transit-swell edge records no baseline
    /// for a never-posed ship: its default SectorId 0 is not a sector it was
    /// in, so its first real status must not read as a transit out of
    /// cluster 0 (which would swell an aleph whose partner is in cluster 0).</summary>
    [Fact]
    public void Transit_edge_skips_a_ship_until_its_sector_is_known()
    {
        var world = World();
        var last = new Dictionary<short, short>();
        var drone = world.Ships[20];
        Assert.False(Game1.TryTransit(last, 20, drone, out _));
        Assert.False(last.ContainsKey(20));                  // no sector-0 baseline

        world.Apply(new ShipStatusMessage { ShipId = 20, HullId = 1, SectorId = 5, StationId = -1 });
        Assert.False(Game1.TryTransit(last, 20, drone, out _)); // first known sector = baseline, no transit
        Assert.Equal(5, last[20]);

        world.Apply(new ShipStatusMessage { ShipId = 20, HullId = 1, SectorId = 7, StationId = -1 });
        Assert.True(Game1.TryTransit(last, 20, drone, out short prev));
        Assert.Equal(5, prev);
    }

    [Fact]
    public void Playerinfo_only_ship_is_absent_from_target_candidates()
    {
        var world = World();
        var e = world.Ships[20];
        Assert.Null(e.Latest);
        Assert.Null(e.PoseSectorId);

        // The viewer's own sector…
        var inView = Game1.GatherSectorCandidates(world.Ships, world.Warps, world.Stations,
            world.Asteroids, me: 10, mySector: 7);
        Assert.DoesNotContain(inView, c => c.Id == 20);

        // …and sector 0 — a never-posed ship must not surface there either.
        var inSector0 = Game1.GatherSectorCandidates(world.Ships, world.Warps, world.Stations,
            world.Asteroids, me: 10, mySector: 0);
        Assert.DoesNotContain(inSector0, c => c.Id == 20);
    }
}
