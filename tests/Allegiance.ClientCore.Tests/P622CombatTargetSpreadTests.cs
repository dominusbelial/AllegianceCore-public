using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;
using H = Allegiance.Tools.McpSpawner.BotTargetClass;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P622 — failing-first UNIT tests for the coordinated combat target CHOICE
/// (the anti-swarm spreading that replaces the per-bot nearest-enemy pick that
/// put 195 bots on one lone miner). All pure over synthetic geometry (no
/// server): the engager CAP, THREAT priority, the lone-miner detachment-vs-hold
/// rule, reassignment on death, and least-engaged spreading. On the pre-P622
/// code the <see cref="CombatTargetSelector"/> symbols do not exist, so these
/// fail at compile time (the repo's accepted failing-first form, see
/// P558NativeWingmanAttackTests); sabotaging a rule at runtime fails the
/// corresponding test too.
/// </summary>
public class P622CombatTargetSpreadTests
{
    // ── small builders ────────────────────────────────────────────────────

    private static BotTargetCandidate Cand(short id, H cls, bool threat = false, float dist = 100f)
        => new(id, new Vector3(dist, 0f, 0f), cls, threat) { Distance = dist };

    private static BotSelectionContext Ctx(Dictionary<short, int> engagers, int teammates = 30)
        => new(SelfShipId: 9900, MyCluster: 1052, teammates, engagers);

    private static readonly Dictionary<short, int> Empty = new();

    // ── 1. Engager cap respected (lone miner → small detachment, rest hold) ──

    /// <summary>A lone miner (cap 2) admits a 2-bot detachment; the third bot
    /// HOLDS instead of piling on — the exact P622 headline.</summary>
    [Fact]
    public void LoneMiner_CapTwo_ThenRestHold()
    {
        var miner = Cand(9001, H.MinerDrone);

        var b1 = CombatTargetSelector.Select(new[] { miner }, Ctx(Empty));
        Assert.False(b1.Hold); Assert.Equal((short)9001, b1.TargetId);

        var b2 = CombatTargetSelector.Select(new[] { miner },
            Ctx(new Dictionary<short, int> { [9001] = 1 }));
        Assert.False(b2.Hold); Assert.Equal((short)9001, b2.TargetId);

        // Third engager: the miner is at cap (2) → the bot HOLDS (loiters at the
        // staging point) instead of swarming.
        var b3 = CombatTargetSelector.Select(new[] { miner },
            Ctx(new Dictionary<short, int> { [9001] = CombatTargetSelector.CapMinerDrone }));
        Assert.True(b3.Hold);
        Assert.Null(b3.TargetId);
    }

    /// <summary>The miner cap is exactly 2 (1-2 per drone per the P622 scope).</summary>
    [Fact]
    public void Caps_MinerIsTwo_FighterThree_BomberFour_CapitalSix()
    {
        Assert.Equal(2, CombatTargetSelector.CapFor(H.MinerDrone));
        Assert.Equal(3, CombatTargetSelector.CapFor(H.Fighter));
        Assert.Equal(4, CombatTargetSelector.CapFor(H.Bomber));
        Assert.Equal(6, CombatTargetSelector.CapFor(H.Capital));
    }

    // ── 2. Threat priority ────────────────────────────────────────────────

    /// <summary>A ship flagged as a THREAT is engaged before any non-threat
    /// combat ship and before a nearer miner — even when it is farther away.</summary>
    [Fact]
    public void Threat_EngagedBeforeCombatAndMiners_EvenIfFarther()
    {
        var threat = Cand(9101, H.Fighter, threat: true, dist: 999f);
        var idleFighter = Cand(9102, H.Fighter, threat: false, dist: 50f);
        var miner = Cand(9001, H.MinerDrone, threat: false, dist: 10f);

        var ch = CombatTargetSelector.Select(new[] { miner, idleFighter, threat }, Ctx(Empty));
        Assert.False(ch.Hold);
        Assert.Equal((short)9101, ch.TargetId);   // the threat, not the nearer miner/fighter
    }

    /// <summary>A station-threatening bomber/capital while a friendly station is
    /// co-located is a threat too (the task's "bombers heading for our stations").</summary>
    [Fact]
    public void Threat_BomberAgainstOurStationBeatsAnIdleFighter()
    {
        var bomber = Cand(9201, H.Bomber, threat: true, dist: 700f);
        var idle = Cand(9102, H.Fighter, threat: false, dist: 100f);
        var ch = CombatTargetSelector.Select(new[] { idle, bomber }, Ctx(Empty));
        Assert.Equal((short)9201, ch.TargetId);
    }

    // ── 3. Combat ships before miners; miners NEVER farmed while combat is present ──

    /// <summary>With an enemy fighter present, a miner is NOT engaged — strict
    /// priority (we don't loot a miner while the enemy fights us).</summary>
    [Fact]
    public void CombatPresent_MinerNotEngagedWhileFighterHasCapacity()
    {
        var fighter = Cand(9102, H.Fighter, dist: 200f);
        var miner = Cand(9001, H.MinerDrone, dist: 5f);
        var ch = CombatTargetSelector.Select(new[] { miner, fighter }, Ctx(Empty));
        Assert.Equal((short)9102, ch.TargetId);   // the fighter, never the miner
    }

    /// <summary>Even when every combat ship is over-capped, a bot does NOT fall
    /// through to farming a miner while combat is present — it holds (the fleet
    /// holds in reserve rather than disbanding onto a miner mid-fight).</summary>
    [Fact]
    public void CombatPresent_AllFightersOverCap_BotHoldsNotMiners()
    {
        var fighter = Cand(9102, H.Fighter, dist: 200f);
        var miner = Cand(9001, H.MinerDrone, dist: 5f);
        var ch = CombatTargetSelector.Select(new[] { miner, fighter },
            Ctx(new Dictionary<short, int> { [9102] = CombatTargetSelector.CapFighter }));
        Assert.True(ch.Hold);
    }

    // ── 4. Least-engaged spreading across distinct targets ────────────────

    /// <summary>Two fighters, cap 3 each: the second bot takes the OTHER fighter
    /// (fewer engagers) instead of piling on the first — real spreading.</summary>
    [Fact]
    public void Spread_SecondBotTakesLeastEngagedOtherTarget()
    {
        var f1 = Cand(9102, H.Fighter, dist: 100f);
        var f2 = Cand(9103, H.Fighter, dist: 120f);
        var all = new[] { f1, f2 };

        var b1 = CombatTargetSelector.Select(all, Ctx(Empty));
        Assert.False(b1.Hold);

        // After b1 took 9102, b2 must prefer the OTHER target (9103, 0 engagers).
        var b2 = CombatTargetSelector.Select(all, Ctx(new Dictionary<short, int> { [(short)b1.TargetId!.Value] = 1 }));
        Assert.False(b2.Hold);
        Assert.Equal((short)9103, b2.TargetId!.Value);
    }

    /// <summary>Tie-break: within the same (zero) engager level the NEAREST
    /// candidate wins (the native nearest cue is kept as the tie-break).</summary>
    [Fact]
    public void Spread_TieBreaksToNearestAtEqualEngagement()
    {
        var near = Cand(9102, H.Fighter, dist: 80f);
        var far = Cand(9103, H.Fighter, dist: 640f);
        var ch = CombatTargetSelector.Select(new[] { far, near }, Ctx(Empty));
        Assert.Equal((short)9102, ch.TargetId);
    }

    // ── 5. Reassignment on death ──────────────────────────────────────────

    /// <summary>A dead target (removed from the candidates — it is no longer a
    /// ship in the world) is never re-chosen; the bot picks a LIVING target
    /// instead of clinging to / holding on the corpse.</summary>
    [Fact]
    public void DeadTarget_Removed_CannotBeSelectedAndLivingTargetIsChosen()
    {
        short dead = 9700;                      // the mined-out / destroyed miner
        var survivors = new[]
        {
            Cand(9102, H.Fighter, dist: 100f),
            Cand(9103, H.Fighter, dist: 200f),
        };
        var ch = CombatTargetSelector.Select(survivors, Ctx(Empty));
        Assert.False(ch.Hold);
        Assert.NotNull(ch.TargetId);
        Assert.NotEqual(dead, ch.TargetId!.Value);   // never the dead ship
        Assert.Contains(ch.TargetId!.Value, new short[] { 9102, 9103 });
    }

    // ── 6. Classification (native hull cues) ──────────────────────────────

    /// <summary>A miner / builder / pod / drone hull classifies as non-combat
    /// MinerDrone (c_habmMiner igc.h:740, c_habmBuilder :741, c_habmLifepod
    /// :728, P470 drone GroupId -1); a bomber (c_habmThreatToStation igc.h:5390)
    /// as Bomber; a carrier/heavy as Capital; else Fighter.</summary>
    [Theory]
    [InlineData("Miner Mk1", 0, 0x4000, H.MinerDrone)]        // c_habmMiner
    [InlineData("Builder", 0, unchecked((int)0x8000), H.MinerDrone)] // c_habmBuilder
    [InlineData("Lifepod", 0, 0x04, H.MinerDrone)]            // c_habmLifepod
    [InlineData("Rixian Drone", -1, 0, H.MinerDrone)]         // P470 drone (GroupId -1)
    [InlineData("SB Bomber", 0, 0x200, H.Bomber)]             // c_habmThreatToStation
    [InlineData("Assault Bomber", 0, 0, H.Bomber)]            // name cue
    [InlineData("Carrier", 0, 0x400, H.Capital)]              // c_habmCarrier
    [InlineData("Hvy Destroyer", 0, 0, H.Capital)]            // name cue
    [InlineData("Patrol Fighter", 0, 0x80, H.Fighter)]        // c_habmFighter
    public void Classify_RealHullCues(string name, int group, int habm, H expect)
    {
        var hull = Hull(name, (sbyte)group, (short)habm, (short)(100 + name.Length));
        Assert.Equal(expect, CombatTargetSelector.ClassifyTarget(hull));
    }

    // ── 7. Approach spacing (P622 §4) ─────────────────────────────────────

    /// <summary>A solo engager (rank 1) gets NO lateral offset; higher ranks get
    /// a deterministic, distinct lateral fan so N bots on the SAME target don't
    /// converge into one column.</summary>
    [Fact]
    public void OrbitOffset_SoloEngagerZero_HigherRanksDistinctLateral()
    {
        var los = new Vector3(100f, 0f, 0f);   // target straight ahead (+X)
        Assert.True(CombatTargetSelector.EngagementOrbitOffset(1, los, 45f).Length() < 1e-4f,
            "a solo engager (rank 1) must get no lateral offset");
        var o2 = CombatTargetSelector.EngagementOrbitOffset(2, los, 45f);
        var o3 = CombatTargetSelector.EngagementOrbitOffset(3, los, 45f);
        // Lateral fan: each offset is horizontal (Z in the XZ plane, Y == 0) and
        // points sideways, not along the line of sight.
        Assert.Equal(0f, o2.Y, 4); Assert.Equal(0f, o3.Y, 4);
        Assert.True(o2.Length() > 1e-3f && o3.Length() > 1e-3f, "ranks >1 must fan laterally");
        Assert.True((o2 - o3).Length() > 1e-3f, "different ranks must use distinct directions");
        // Magnitude honoured.
        Assert.Equal(45f, o2.Length(), 2);
    }

    // ── helper: build a minimal DataHullTypeIGC ──────────────────────────

    private static DataHullTypeIGC Hull(string name, sbyte group, short habm, short hullId)
        => new(
            Price: 0, TimeToBuild: 0, ModelName: name, IconName: "", Name: name, Description: "",
            GroupId: group, TtbmRequired: [], TtbmEffects: [],
            Mass: 1f, Signature: 1f, Speed: 100f, MaxTurnRates: [0f, 0f, 0f],
            TurnTorques: [0f, 0f, 0f], Thrust: 1f, SideMultiplier: 1f, BackMultiplier: 1f,
            ScannerRange: 2000f, MaxFuel: 100f, Ecm: 0f, Length: 100f, MaxEnergy: 100f,
            RechargeRate: 10f, RipcordSpeed: 200f, RipcordCost: 1f, MaxAmmo: 1,
            HullId: hullId, SuccessorHullId: -1, MaxWeapons: 1, MaxFixedWeapons: 1,
            HitPoints: 100f, HardpointOffset: 540, DefenseType: 0, CapacityMagazine: 0,
            CapacityDispenser: 0, CapacityChaffLauncher: 0, PreferredPartsTypes: [-1],
            HabmCapabilities: habm, TextureName: "", PmEquipment: new short[8],
            InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
            MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
            ManuveringThrusterExteriorSound: 0, Hardpoints: []);
}
