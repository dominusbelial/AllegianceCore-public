using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;
using SimVec = Allegiance.Core.Collision.Vector3;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P415 — the generalized friend/foe/hull-role filtered nearest-targeting family
/// (the port of native's Targeting commands, trekinput.cpp:1531-1600 →
/// <c>FindTarget</c> src/Igc/common.cpp:470 with <c>FindableModel</c>
/// common.cpp:254-353). Unlike the generic T-key (P371 sector + P256 frustum +
/// priority-tier picking), the filtered family is a single parameterized filter
/// (<see cref="Game1.TargetRelation"/> + <see cref="Game1.TargetFilterSpec"/>)
/// that separates candidates by SIDE relation and optionally narrows by
/// hull/station ROLE ability bits, then picks the NEAREST matching candidate (or
/// cycles through matching-only candidates, wrapping).
///
/// These tests pin the contract the brief demands:
/// <list type="bullet">
/// <item>The filter separates enemy vs. friendly vs. self candidates by side
/// (and never offers an unsided stranger as an enemy).</item>
/// <item>A role bitmask narrows to only candidates whose ability bits include
/// that role (the native abmAbilities bit test).</item>
/// <item>"Nearest" picks the closest MATCHING candidate (a nearer non-matching
/// candidate is never offered), and the cycle steps through matching candidates
/// only and wraps.</item>
/// <item>Station-only / major-base-only narrow to stations of the right side /
/// victory-objective role.</item>
/// </list>
/// The gather-classification half (that <see cref="Game1.GatherSectorCandidates"/>
/// fills <c>SideId</c>/<c>RoleBits</c> from the SAME ship/station wire data the
/// T-key already reads) is pinned through the real
/// <see cref="ClientWorld.Apply(IFedMessage)"/> wire-apply path.
/// </summary>
public class FilteredTargetingTests
{
    private static Game1.CandidateObject Ship(short id, short? side, short role, SimVec pos) =>
        new(pos, Game1.TargetKind.Ship, id, $"ship{id}", side, role);

    private static Game1.CandidateObject Station(short id, short? side, short role, SimVec pos) =>
        new(pos, Game1.TargetKind.Station, id, $"sta{id}", side, role);

    private static Game1.TargetFilterSpec Spec(Game1.TargetRelation r, short role = 0, bool station = false, bool major = false)
        => new(r, role, station, major);

    // ── relation separation ────────────────────────────────────────────────

    /// <summary>⭐ Enemy keeps only DIFFERENT-side ships; same-side and unsided
    /// candidates are never enemies, and self is never a target.</summary>
    [Fact]
    public void Enemy_filter_keeps_only_other_side_ships_and_excludes_self()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(1, 1, 0, new SimVec(0, 0, 10)),    // same side (friendly) — excluded
            Ship(2, 2, 0, new SimVec(0, 0, 20)),    // enemy
            Ship(3, 3, 0, new SimVec(0, 0, 30)),    // enemy
            Ship(4, null, 0, new SimVec(0, 0, 40)), // no side — excluded (unknown)
            Ship(99, 1, 0, new SimVec(0, 0, 5)),    // self — excluded
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy));
        Assert.Equal(new short[] { 2, 3 }, got.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ Friendly keeps only SAME-side ships and excludes self.</summary>
    [Fact]
    public void Friendly_filter_keeps_only_same_side_ships_and_excludes_self()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(1, 1, 0, new SimVec(0, 0, 10)),    // friendly
            Ship(2, 2, 0, new SimVec(0, 0, 20)),    // enemy — excluded
            Ship(5, 1, 0, new SimVec(0, 0, 15)),    // friendly (farther)
            Ship(99, 1, 0, new SimVec(0, 0, 5)),    // self — excluded
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Friendly));
        Assert.Equal(new short[] { 1, 5 }, got.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ Self relation returns the player's own ship.</summary>
    [Fact]
    public void Self_filter_returns_only_the_local_ship()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(1, 1, 0, new SimVec(0, 0, 10)),
            Ship(99, 1, 0, new SimVec(0, 0, 5)),
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Self));
        Assert.Equal(new short[] { 99 }, got.Select(x => x.Id).ToArray());
    }

    // ── role narrowing ─────────────────────────────────────────────────────

    /// <summary>⭐ A role bitmask narrows to only candidates whose ability bits
    /// include that role — the native abmAbilities bit test (common.cpp:291-321).
    /// Here Enemy+Miner keeps the miner but drops the fighter, even though the
    /// fighter is CLOSER.</summary>
    [Fact]
    public void Role_bitmask_narrows_to_that_role_even_when_a_wrong_role_is_closer()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(1, 2, DataHullTypeIGC.HabmFighter, new SimVec(0, 0, 10)),  // fighter, close — dropped
            Ship(2, 2, DataHullTypeIGC.HabmMiner, new SimVec(0, 0, 200)),   // miner, far — kept
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy, role: DataHullTypeIGC.HabmMiner));
        Assert.Equal(new short[] { 2 }, got.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ The role filter can separate two roles on the SAME side: a
    /// friendly-miner filter keeps the miner, drops the friendly fighter.</summary>
    [Fact]
    public void Role_bitmask_applies_to_friendly_too()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(1, 1, DataHullTypeIGC.HabmFighter, new SimVec(0, 0, 10)),
            Ship(2, 1, DataHullTypeIGC.HabmBuilder, new SimVec(0, 0, 20)),   // constructor
            Ship(3, 1, DataHullTypeIGC.HabmMiner, new SimVec(0, 0, 30)),     // miner
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Friendly, role: DataHullTypeIGC.HabmMiner));
        Assert.Equal(new short[] { 3 }, got.Select(x => x.Id).ToArray());
    }

    // ── nearest / cycle ────────────────────────────────────────────────────

    /// <summary>⭐ Nearest picks the closest MATCHING candidate, ignoring a
    /// non-matching (here: friendly) candidate that is physically nearer.</summary>
    [Fact]
    public void Nearest_picks_closest_matching_ignoring_a_nearer_wrong_relation()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(90, 1, 0, new SimVec(0, 0, 5)),     // friendly, VERY close — not an enemy
            Ship(2, 2, 0, new SimVec(0, 0, 50)),     // enemy, far
            Ship(3, 2, 0, new SimVec(0, 0, 60)),     // enemy, farthest
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy));
        // The enemy list is [2 (dist 50), 3 (dist 60)]; nearest = 2, never the
        // closer friendly 90.
        Assert.Equal(new short[] { 2, 3 }, got.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ The cycle steps through MATCHING candidates only (never the
    /// nearer friendly) and wraps around to the start.</summary>
    [Fact]
    public void Cycle_steps_through_matching_only_and_wraps()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(90, 1, 0, new SimVec(0, 0, 5)),     // friendly, closest overall — never eligible
            Ship(2, 2, 0, new SimVec(0, 0, 50)),     // enemy
            Ship(3, 2, 0, new SimVec(0, 0, 60)),     // enemy
        };
        var enemies = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy));
        Assert.Equal(new short[] { 2, 3 }, enemies.Select(x => x.Id).ToArray());

        // Walk the cycle: start empty -> nearest (2); then 3; then wrap to 2.
        Assert.Equal((short)2, Game1.StepCycle(enemies, null).Id);
        Assert.Equal((short)3, Game1.StepCycle(enemies, enemies[0]).Id);
        Assert.Equal((short)2, Game1.StepCycle(enemies, enemies[1]).Id); // wrapped
        // A current selection that is NOT in the matching set falls back to nearest.
        Assert.Equal((short)2, Game1.StepCycle(enemies, new Game1.AutopilotTarget(Game1.TargetKind.Ship, 90, "ship90")).Id);
    }

    // ── station-only / major-base-only ─────────────────────────────────────

    /// <summary>⭐ Station-only + enemy keeps only enemy STATIONS, never enemy
    /// ships (native picks a single model list by type mask).</summary>
    [Fact]
    public void Enemy_base_filter_keeps_only_enemy_stations()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Ship(2, 2, 0, new SimVec(0, 0, 20)),                       // enemy ship — dropped
            Station(11, 2, 0, new SimVec(0, 0, 30)),                   // enemy base
            Station(12, 1, 0, new SimVec(0, 0, 10)),                   // friendly base — dropped
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy, station: true));
        Assert.Equal(new short[] { 11 }, got.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ Friendly + station-only keeps only same-side stations.</summary>
    [Fact]
    public void Friendly_base_filter_keeps_only_friendly_stations()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Station(12, 1, 0, new SimVec(0, 0, 10)),   // friendly base
            Station(11, 2, 0, new SimVec(0, 0, 5)),    // enemy base (closer) — dropped
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Friendly, station: true));
        Assert.Equal(new short[] { 12 }, got.Select(x => x.Id).ToArray());
    }

    /// <summary>⭐ Major-base-only narrows stations to victory-objective types
    /// (native c_sabmFlag), still side-filtered.</summary>
    [Fact]
    public void Major_base_filter_keeps_only_flag_stations_of_the_right_side()
    {
        var objs = new List<Game1.CandidateObject>
        {
            Station(21, 2, DataStationTypeIGC.SabmFlag, new SimVec(0, 0, 20)),  // enemy major base — kept
            Station(22, 2, DataStationTypeIGC.SabmLand, new SimVec(0, 0, 10)),  // enemy minor base — dropped
            Station(23, 1, DataStationTypeIGC.SabmFlag, new SimVec(0, 0, 5)),   // friendly major — dropped
        };
        var got = Game1.FilterCandidates(objs, mySideId: 1, selfId: 99, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy, station: true, major: true));
        Assert.Equal(new short[] { 21 }, got.Select(x => x.Id).ToArray());
    }

    // ── gather classification feeding the filter (real wire apply) ─────────

    /// <summary>⭐ End-to-end: GatherSectorCandidates fills SideId + RoleBits from
    /// the SAME wire data the T-key reads (EXPORT for ship side/identity,
    /// EXPORT_OBJECT for station side, ShopCatalog-style role resolvers for hull/
    /// station ability bits), and the filter then separates enemy from friendly —
    /// proving the "no new data source" claim.</summary>
    [Fact]
    public void Gather_and_filter_separate_sides_from_real_wire_data()
    {
        const short Sector = 901, Me = 100;
        var w = new ClientWorld();
        w.Apply(new SetCluster { SectorId = Sector, Ship = PoseFor(Me) });
        // Own-ship dummy export establishes the player's OWN side (side 1).
        w.Apply(new ExportShip { ShipId = -1, SideId = 1 });

        // Teammate (side 1) + enemy (side 2), both in our sector, each with a
        // real EXPORT identity + a pose + a sector.
        SeedSideShip(w, 201, 1, hullId: 801);
        SeedSideShip(w, 202, 2, hullId: 802);

        // Stations on both sides in our sector.
        w.Apply(new ExportStation { Station = StationWire(401, Sector, sideId: 1) });
        w.Apply(new ExportStation { Station = StationWire(402, Sector, sideId: 2) });

        // Role resolvers mirror the client's ShopCatalog (hull 801 = fighter,
        // hull 802 = miner; station type 3 = major base).
        short ShipRole(short hullId) => hullId == 801 ? DataHullTypeIGC.HabmFighter
                                      : hullId == 802 ? DataHullTypeIGC.HabmMiner
                                      : (short)0;
        short StRole(short typeId) => typeId == 3 ? DataStationTypeIGC.SabmFlag : (short)0;

        var objs = Game1.GatherSectorCandidates(w.Ships, w.Warps, w.Stations, w.Asteroids,
                                                Me, Sector, ShipRole, StRole);

        // Enemy ships = the side-2 ship only; friendly ships = the side-1 ship only.
        var enemies = Game1.FilterCandidates(objs, mySideId: 1, selfId: Me, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy));
        Assert.Equal(new short[] { 202 }, enemies.Select(x => x.Id).ToArray());

        var friends = Game1.FilterCandidates(objs, mySideId: 1, selfId: Me, SimVec.Zero,
            Spec(Game1.TargetRelation.Friendly));
        Assert.Equal(new short[] { 201 }, friends.Select(x => x.Id).ToArray());

        // Enemy role (miner) keeps only the enemy miner, not the enemy... only
        // side-2 ships are enemies; among them hull 802 is the miner.
        var enemyMiners = Game1.FilterCandidates(objs, mySideId: 1, selfId: Me, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy, role: DataHullTypeIGC.HabmMiner));
        Assert.Equal(new short[] { 202 }, enemyMiners.Select(x => x.Id).ToArray());

        // Bases by side: enemy base = station 402; friendly base = station 401.
        var enemyBases = Game1.FilterCandidates(objs, mySideId: 1, selfId: Me, SimVec.Zero,
            Spec(Game1.TargetRelation.Enemy, station: true));
        Assert.Equal(new short[] { 402 }, enemyBases.Select(x => x.Id).ToArray());

        var friendlyBases = Game1.FilterCandidates(objs, mySideId: 1, selfId: Me, SimVec.Zero,
            Spec(Game1.TargetRelation.Friendly, station: true));
        Assert.Equal(new short[] { 401 }, friendlyBases.Select(x => x.Id).ToArray());
    }

    // ── fixtures ───────────────────────────────────────────────────────────

    private static ServerShipUpdate PoseFor(short id) =>
        ServerShipUpdate.FromValues(
            id, stateM: 0, new ControlData(0f, 0f, 0f, 0f), time: 1u,
            SimVec.Zero, Orientation.Identity, SimVec.Zero,
            turnRates: new[] { 0f, 0f, 0f },
            hullFrac: 1f, shieldFrac: 1f, ammoFrac: 1f, fuelFrac: 1f,
            energyFrac: 1f, oreFrac: 0f, power: 0f);

    /// <summary>Seed a ship with a real EXPORT identity (side + hull + HasRealIdentity),
    /// a pose and its sector — so GatherSectorCandidates classifies it by side.</summary>
    private static void SeedSideShip(ClientWorld w, short id, short side, short hullId)
    {
        w.Apply(new SingleShipUpdate { OidTarget = id, Ship = PoseFor(id) });
        w.Apply(new ShipStatusMessage { ShipId = id, SectorId = 901 });
        w.Apply(new ExportShip { ShipId = id, SideId = side, HullId = hullId, CharacterName = $"pilot{id}" });
    }

    private static DataStationIGC StationWire(short id, short cluster, short sideId) => new(
        Position: new[] { (float)id, 0f, 0f },
        Up: new[] { 0f, 1f, 0f }, Forward: new[] { 0f, 0f, 1f }, Rotation: new[] { 0f, 0f, 0f },
        ClusterId: cluster, SideId: sideId, StationId: id, StationTypeId: 3,
        BpHull: 10, BpShield: 10, Name: $"sta{id}");
}
