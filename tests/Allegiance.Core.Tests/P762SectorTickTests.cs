using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Core.Tests;

/// <summary>
/// P762 — SIM-0064 mine attrition ordering and SIM-0065 visibility/callback
/// cadence, both ported from <c>Igc/clusterIGC.cpp:322-357</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P762SectorTickTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() => StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));
    private static MissionMap Map() => MissionMap.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "XXV_simple.igc")));

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Games = [new Gm { Name = "P762 tick", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
    };

    /// <summary>
    /// SIM-0064 / P762b — mine damage applies in a Started tick, kills the
    /// victim and finalises it out of the sector. (The P762 "no move on the
    /// death tick" claim is withdrawn: this port's Move runs inside each
    /// ship's OnUpdate, before the collision and mine passes, so a mine-killed
    /// ship has already moved once this tick. Native's collision → mines → Move
    /// order is honoured only for the collision-before-mines half; see
    /// Sector.ResolveMineDetonations remarks and the P762b report.)
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mine_damage_kills_and_finalises_the_ship_in_a_started_tick()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];
        var hull = mission.Core.HullTypes.Values.First(h => h.Length > 0);
        var mineType = mission.Core.MineTypes.Values.Where(m => m.MineRadius > 0 && m.Power > 0)
            .OrderByDescending(m => m.Power).First();

        var launcher = new Ship(7001, hull, new Vector3(9000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var victim = new Ship(7002, hull, new Vector3(0, 0, 0), new Vector3(120, 0, 0), Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(launcher, sector);
        mission.PlaceShip(victim, sector);
        victim.SetFraction(1e-6f);   // any mine damage kills this tick

        double now = mission.LastUpdate;
        var mine = new Mine(7003, sector, mineType with { Lifespan = 1000 }, victim.Position, now - 5, launcher);
        sector.Add(mine);

        Assert.True(victim.Velocity.LengthSquared() > 1f, "the victim must be moving to satisfy the native speed gate");
        mission.Update(now + 0.05);

        Assert.True(victim.Dead, "mine damage must have killed the ship");
        Assert.DoesNotContain(victim, sector.Models);                // host finalisation removed it
        Console.WriteLine("P762-MINE-ORDER: victim killed by mine damage and finalised in the same tick");
    }

    /// <summary>
    /// SIM-0064 / P762b — native's <c>activeF</c> gate
    /// (<c>clusterIGC.cpp:255</c>): an inactive cluster applies no mine damage
    /// and does not advance the every-10th visibility pass.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Inactive_sector_skips_mine_damage_and_the_visibility_cadence()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];
        var hull = mission.Core.HullTypes.Values.First(h => h.Length > 0);
        var mineType = mission.Core.MineTypes.Values.Where(m => m.MineRadius > 0 && m.Power > 0)
            .OrderByDescending(m => m.Power).First();

        var launcher = new Ship(7201, hull, new Vector3(9000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var victim = new Ship(7202, hull, new Vector3(0, 0, 0), new Vector3(2, 0, 0), Orientation.Identity) { SideId = 1 };
        mission.PlaceShip(launcher, sector);
        mission.PlaceShip(victim, sector);
        victim.SetFraction(1e-6f);
        sector.Add(new Mine(7203, sector, mineType with { Lifespan = 1000 }, victim.Position,
            mission.LastUpdate - 5, launcher));

        sector.Active = false;
        for (int pass = 1; pass <= 20; pass++)
        {
            mission.Update(0.5 + pass * 0.1);
            Assert.False(sector.ClusterUpdateDue, "an inactive sector must not raise the cluster callback");
        }
        Assert.False(victim.Dead, "an inactive sector must not apply mine damage");

        // Re-activating the same sector restores both native behaviours. Put the
        // ship back in the field first: this port still runs each ship's Move in
        // an inactive sector, so it drifted away during the passes above.
        victim.Position = new Vector3(0, 0, 0);
        victim.Velocity = new Vector3(10, 0, 0);
        Assert.True(victim.Velocity.LengthSquared() > 1f);
        sector.Active = true;
        mission.Update(0.5 + 21 * 0.1);
        Assert.True(victim.Dead, "the active sector applies the accumulated mine hit");
        Assert.DoesNotContain(victim, sector.Models);
        Console.WriteLine("P762-ACTIVE-GATE: inactive sector skipped mines + visibility; active sector applied them");
    }

    /// <summary>
    /// SIM-0064 — the ship list is applied back to front, so a child's mine hit
    /// is applied before its parent's. A forward walk would let the parent's
    /// death retire the child's list entry first and skip its damage.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mine_damage_walks_ships_back_to_front_so_children_take_their_hit_before_parents_die()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        var mission = server.Missions[0];
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];
        var hull = mission.Core.HullTypes.Values.First(h => h.Length > 0);
        var mineType = mission.Core.MineTypes.Values.Where(m => m.MineRadius > 0 && m.Power > 0)
            .OrderByDescending(m => m.Power).First();

        var launcher = new Ship(7101, hull, new Vector3(9000, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        var parent = new Ship(7102, hull, new Vector3(0, 0, 0), new Vector3(120, 0, 0), Orientation.Identity) { SideId = 1 };
        var child = new Ship(7103, hull, new Vector3(0, 0, 0), new Vector3(120, 0, 0), Orientation.Identity) { SideId = 1, ParentShip = parent };
        mission.PlaceShip(launcher, sector);
        mission.PlaceShip(parent, sector);
        mission.PlaceShip(child, sector);       // children follow parents in the native ship list
        parent.SetFraction(1e-6f);
        child.SetFraction(1e-6f);

        double now = mission.LastUpdate;
        sector.Add(new Mine(7104, sector, mineType with { Lifespan = 1000 }, parent.Position, now - 5, launcher));
        mission.Update(now + 0.05);

        Assert.True(parent.Dead, "parent must take its mine hit");
        Assert.True(child.Dead, "child must take its own mine hit before the parent's death retires it");
        Assert.DoesNotContain(parent, sector.Models);
        Assert.DoesNotContain(child, sector.Models);
        Console.WriteLine("P762-MINE-BACKFRONT: parent and child were both damaged and finalised");
    }

    /// <summary>
    /// SIM-0065 — the visibility sweep and <c>ClusterUpdateEvent</c> run once
    /// per ten ACTIVE STARTED passes (clusterIGC.cpp:347-357,
    /// clusterIGC.h:22). Starting-stage passes do not advance the phase.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Visibility_and_cluster_callback_run_once_per_ten_active_started_passes()
    {
        Require.Artwork();
        var site = new RecordingIgcSite();
        var mission = Mission.Assemble(Core(), Map(),
            [new SideSpec(0, "A", 1), new SideSpec(1, "B", 3)], site, 0);
        int sectors = mission.Sectors.Count;
        Assert.True(sectors >= 1);

        // Starting passes must not advance the counter.
        mission.Stage = MissionStage.Starting;
        for (int i = 1; i <= 5; i++) mission.Update(i * 0.1);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("ClusterUpdateEvent("));

        mission.Stage = MissionStage.Started;
        for (int pass = 1; pass <= 20; pass++)
        {
            mission.Update(0.5 + pass * 0.1);
            // pass 1 is native pass 0 → due; passes 2..10 not; pass 11 due.
            bool expected = pass is 1 or 11;
            Assert.All(mission.Sectors, s => Assert.Equal(expected, s.ClusterUpdateDue));
        }
        // Exactly two callbacks per cluster over the 20 passes — one per due pass.
        var clusterCalls = site.Calls.Where(c => c.StartsWith("ClusterUpdateEvent(")).ToList();
        Assert.Equal(2 * sectors, clusterCalls.Count);
        Console.WriteLine($"P762-VIS-CADENCE: {clusterCalls.Count} callbacks for {sectors} sector(s) over 20 passes");
    }
}
