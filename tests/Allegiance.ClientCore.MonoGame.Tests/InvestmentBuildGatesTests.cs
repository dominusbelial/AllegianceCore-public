using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P495 — pure coverage of <see cref="InvestmentBuildGates"/>, the client's
/// port of native <c>PartListItem::BuildSpotFoundForStationType</c> /
/// <c>FindBuilderForStationType</c> (WinTrek/loadout.cpp:213-240) and the
/// station-type gate they feed in <c>PurchasesPaneImpl::CanInvest</c>
/// (loadout.cpp:1124-1131). All operate on the client's real world data
/// (asteroid capabilities, building effects, side ship hulls) and the static
/// core (station type -&gt; construction drone -&gt; real hull, the P458/P459
/// mapping), with no graphics device.
/// </summary>
public class InvestmentBuildGatesTests
{
    private static DataAsteroidIGC Rock(short id, short caps) => new(
        Signature: 0f,
        Position: new float[3],
        Up: new float[3],
        Forward: new float[3],
        Rotation: new float[4],
        Ore: 0f, OreMax: 0f,
        AabmCapabilities: caps,
        AsteroidId: id,
        HitPoints: 0f, Radius: 1,
        ModelName: "", TextureName: "", IconName: "",
        ClusterId: id,   // any
        Name: $"rock{id}", Fraction: 0);

    private static DataStationTypeIGC Station(short stid, short ctorDroneId, short aabm) => new(
        Price: 0, TimeToBuild: 0u,
        ModelName: "", IconName: "", Name: $"st{stid}", Description: "",
        GroupId: 0, TtbmRequired: Array.Empty<byte>(), TtbmEffects: Array.Empty<byte>(),
        Signature: 0f, MaxArmorHitPoints: 0f, MaxShieldHitPoints: 0f,
        ArmorRegeneration: 0f, ShieldRegeneration: 0f, ScannerRange: 0f,
        Income: 0, Radius: 0f, TtbmLocal: Array.Empty<byte>(),
        StationTypeId: stid, SuccessorStationTypeId: 0,
        DefenseTypeArmor: 0, DefenseTypeShield: 0,
        SabmCapabilities: 0, AabmBuild: aabm, ClassId: 0,
        ConstructionDroneTypeId: ctorDroneId,
        ConstructorNeedRockSound: 0, ConstructorUnderAttackSound: 0, ConstructorDestroyedSound: 0,
        CompletionSound: 0, InteriorSound: 0, ExteriorSound: 0, InteriorAlertSound: 0,
        UnderAttackSound: 0, CriticalSound: 0, DestroyedSound: 0, CapturedSound: 0,
        EnemyCapturedSound: 0, EnemyDestroyedSound: 0,
        TextureName: "", BuilderName: "");

    private static DataDroneTypeIGC Drone(short droneTypeId, short hullId) => new(
        Price: 0, TimeToBuild: 0u, ModelName: "", IconName: "", Name: $"dr{droneTypeId}", Description: "",
        GroupId: 0, TtbmRequired: Array.Empty<byte>(), TtbmEffects: Array.Empty<byte>(),
        ShootSkill: 0f, MoveSkill: 0f, Bravery: 0f,
        PilotType: 6 /* Builder */, HullTypeId: hullId, DroneTypeId: droneTypeId, EtidLaid: -1);

    private static IReadOnlyDictionary<short, DataDroneTypeIGC> Drones(params DataDroneTypeIGC[] ds) =>
        ds.ToDictionary(d => d.DroneTypeId);

    // ── BuildSpotFoundForStationType (loadout.cpp:213-225) ────────────────

    [Fact]
    public void BuildSpotFound_True_WhenSomeRockHasTheCapability()
    {
        var rocks = new[] { Rock(1, DataAsteroidIGC.AabmBuildable), Rock(2, 0) };
        Assert.True(InvestmentBuildGates.BuildSpotFound(rocks, DataAsteroidIGC.AabmBuildable));
    }

    [Fact]
    public void BuildSpotFound_False_WhenNoRockMatches()
    {
        var rocks = new[] { Rock(1, 0), Rock(2, DataAsteroidIGC.AabmMineHe3) };
        Assert.False(InvestmentBuildGates.BuildSpotFound(rocks, DataAsteroidIGC.AabmBuildable));
        Assert.False(InvestmentBuildGates.BuildSpotFound(Array.Empty<DataAsteroidIGC>(), DataAsteroidIGC.AabmBuildable));
    }

    [Fact]
    public void BuildSpotFound_IgnoresUnrelatedCapabilityBits()
    {
        // A rock with ONLY the mine bit does not count as a build spot for a
        // build-only mask; capability matching is exact on the requested bit.
        var rocks = new[] { Rock(1, DataAsteroidIGC.AabmMineHe3) };
        Assert.False(InvestmentBuildGates.BuildSpotFound(rocks, DataAsteroidIGC.AabmBuildable));
    }

    // ── ConstructionHullFor (P458 station→construction-drone→real hull) ───

    [Fact]
    public void ConstructionHullFor_ResolvesViaConstructionDroneTypeId()
    {
        var st = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var drones = Drones(Drone(6, hullId: 12));
        Assert.Equal((short)12, InvestmentBuildGates.ConstructionHullFor(st, drones));
    }

    [Fact]
    public void ConstructionHullFor_Null_WhenConstructionDroneUnknown()
    {
        var st = Station(4, ctorDroneId: 777, aabm: DataAsteroidIGC.AabmBuildable);
        Assert.Null(InvestmentBuildGates.ConstructionHullFor(st, Drones(Drone(6, hullId: 12))));
        Assert.Null(InvestmentBuildGates.ConstructionHullFor(st, null));
    }

    // ── BuilderExists (FindBuilderForStationType, loadout.cpp:227-240) ────

    [Fact]
    public void BuilderExists_NoConstructorOnSide_False()
    {
        var st = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var drones = Drones(Drone(6, hullId: 12));
        // Side has no ships at all, or ships of other hulls — no constructor.
        Assert.False(InvestmentBuildGates.BuilderExists([Rock(9, DataAsteroidIGC.AabmBuildable)], new short[] { 900 },
            new short[] { }, st, drones));
        Assert.False(InvestmentBuildGates.BuilderExists([Rock(9, DataAsteroidIGC.AabmBuildable)], new short[] { 900 },
            new short[] { 502 }, st, drones));
    }

    [Fact]
    public void BuilderExists_ConstructorPresentButNothingBeingBuilt_False()
    {
        var st = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var drones = Drones(Drone(6, hullId: 12));
        // Constructor (hull 12) on the side, but no active construction anywhere.
        Assert.False(InvestmentBuildGates.BuilderExists([Rock(9, DataAsteroidIGC.AabmBuildable)], new short[] { },
            new short[] { 12 }, st, drones));
    }

    [Fact]
    public void BuilderExists_ConstructorPlusActiveBuildOnCompatibleRock_True()
    {
        var st = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var drones = Drones(Drone(6, hullId: 12));
        // Constructor (hull 12) + an active construction at rock 9 (buildable).
        Assert.True(InvestmentBuildGates.BuilderExists([Rock(9, DataAsteroidIGC.AabmBuildable)], new short[] { 9 },
            new short[] { 12 }, st, drones));
    }

    [Fact]
    public void BuilderExists_ActiveBuildOnIncompatibleRock_False()
    {
        var st = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var drones = Drones(Drone(6, hullId: 12));
        // A same-hull constructor (12) IS on the side, but the active
        // construction is on a non-buildable rock — i.e. that constructor is
        // building a DIFFERENT station type. The port must not falsely refuse
        // THIS station type (the same-hull multi-station-type protection).
        var rocks = new[] { Rock(9, DataAsteroidIGC.AabmMineHe3) };
        Assert.False(InvestmentBuildGates.BuilderExists(rocks, new short[] { 9 }, new short[] { 12 }, st, drones));
    }

    [Fact]
    public void BuilderExists_UnknownConstructionDrone_False()
    {
        var st = Station(4, ctorDroneId: 888, aabm: DataAsteroidIGC.AabmBuildable);
        // Can't resolve the construction hull — be permissive (treat as no builder).
        Assert.False(InvestmentBuildGates.BuilderExists([Rock(9, DataAsteroidIGC.AabmBuildable)], new short[] { 9 },
            new short[] { 12 }, st, Drones(Drone(6, hullId: 12))));
    }

    // ── CanBuildStation (native CanInvest station gate, loadout.cpp:1124-1131) ──

    [Fact]
    public void CanBuildStation_NoBuildSpot_Refused()
    {
        var st = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var drones = Drones(Drone(6, hullId: 12));
        // Only a mine rock exists — no buildable rock anywhere.
        Assert.False(InvestmentBuildGates.CanBuildStation([Rock(1, DataAsteroidIGC.AabmMineHe3)],
            new short[] { }, new short[] { }, st, drones));
    }

    [Fact]
    public void CanBuildStation_BuilderExists_Refused()
    {
        var st = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var drones = Drones(Drone(6, hullId: 12));
        var rocks = new[] { Rock(9, DataAsteroidIGC.AabmBuildable) };
        Assert.False(InvestmentBuildGates.CanBuildStation(rocks, new short[] { 9 }, new short[] { 12 }, st, drones));
    }

    [Fact]
    public void CanBuildStation_SpotAndNoBuilder_Allowed()
    {
        var st = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var drones = Drones(Drone(6, hullId: 12));
        var rocks = new[] { Rock(9, DataAsteroidIGC.AabmBuildable) };
        Assert.True(InvestmentBuildGates.CanBuildStation(rocks, new short[] { }, new short[] { }, st, drones));
    }

    [Fact]
    public void CanBuildStation_FeedingTheModel_ReflectsInCanInvest()
    {
        var st4 = Station(4, ctorDroneId: 6, aabm: DataAsteroidIGC.AabmBuildable);
        var st5 = Station(5, ctorDroneId: 889, aabm: DataAsteroidIGC.AabmMineHe3);
        var byId = new Dictionary<short, DataStationTypeIGC> { [4] = st4, [5] = st5 };
        var drones = Drones(Drone(6, hullId: 12));
        var rocks = new[] { Rock(9, DataAsteroidIGC.AabmBuildable) };

        // Station 4 (buildable rock exists, no builder) is investable.
        var allowed = new InvestmentPaneModel(
            [new InvestmentBucket(303, BucketItemType.BitStationType, "Outpost", 8000, 60, -1, -1, -1, 4)],
            null, missionInProgress: true, hasSide: true, myMoney: 5000,
            stationCanBuild: id => InvestmentBuildGates.CanBuildStation(rocks, new short[] { }, new short[] { }, byId[id], drones));
        Assert.True(allowed.CanInvest(allowed.Items[0]));

        // Station 5 needs a mine-rock build spot that doesn't exist → refused
        // by the SAME gate the host feeds the model.
        var refused = new InvestmentPaneModel(
            [new InvestmentBucket(304, BucketItemType.BitStationType, "Refinery", 8000, 60, -1, -1, -1, 5)],
            null, missionInProgress: true, hasSide: true, myMoney: 5000,
            stationCanBuild: id => InvestmentBuildGates.CanBuildStation(rocks, new short[] { }, new short[] { }, byId[id], drones));
        Assert.False(refused.CanInvest(refused.Items[0]));
    }

    // ── MatchingAsteroidIds (native SetInvestAsteroid, loadout.cpp:1396-1409) ──

    [Fact]
    public void MatchingAsteroidIds_ReturnsExactlyTheCapableRocks()
    {
        var rocks = new[]
        {
            Rock(1, DataAsteroidIGC.AabmBuildable),
            Rock(2, DataAsteroidIGC.AabmBuildable | DataAsteroidIGC.AabmMineHe3),
            Rock(3, DataAsteroidIGC.AabmMineHe3),
            Rock(4, 0),
        };
        var matching = InvestmentBuildGates.MatchingAsteroidIds(rocks, DataAsteroidIGC.AabmBuildable).ToHashSet();
        Assert.Equal(new short[] { 1, 2 }.ToHashSet(), matching);
    }

    [Fact]
    public void MatchingAsteroidIds_Empty_WhenNoneMatch()
    {
        var rocks = new[] { Rock(1, DataAsteroidIGC.AabmMineHe3) };
        Assert.Empty(InvestmentBuildGates.MatchingAsteroidIds(rocks, DataAsteroidIGC.AabmBuildable));
    }
}
