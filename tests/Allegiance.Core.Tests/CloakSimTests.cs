using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P269 — the cloak <em>simulation</em> mechanic (server-side only; no client
/// rendering — that is P270). Proves the four native behaviours from
/// <c>CcloakIGC</c>/<c>CshipIGC</c> against real corpus hull/scanner data in a
/// real started mission:
/// <list type="number">
/// <item><b>Activation</b> — a ship carrying an <c>ET_Cloak@0</c> part engages
/// the cloak (<see cref="Ship.IsCloaked"/>) when the <c>CloakActive</c> StateM
/// control bit is set (the port of <c>cloakActiveIGC</c>, igc.h:820), exactly
/// like every other toggled control bit (<c>ShipControlState</c>); the same
/// ship without the bit, or with the cloak on a non-zero mount (native
/// <c>fActive() = mountID == 0</c>, cloakigc.h:66-69), never cloaks.</item>
/// <item><b>Energy cost</b> — while the emitter is on, the cloak drains
/// <c>EnergyConsumption·dt</c> per tick (<c>cloakigc.cpp:89-103</c>), and a
/// short energy pool throttles the cloak's target strength quadratically
/// (the <c>r²</c> scaling, cloakigc.cpp:98-101).</item>
/// <item><b>Detection interaction</b> — cloaking multiplies the ship's effective
/// detection signature by its <see cref="Ship.Cloaking"/> visible-fraction
/// (<c>GetSignature() = base·GetCloaking()</c>, shipIGC.h:268-270), shrinking the
/// per-object <c>InScannerRange</c> radius (shipIGC.h:353) so a cloaked ship
/// becomes <b>undetectable to an enemy side even inside its normal detection
/// range</b> — a real, live <see cref="SideVisibilityState.SeenBySide"/> flip in
/// the P132 cross-side sweep.</item>
/// <item><b>Decloak</b> — clearing the bit ramps the ship back to full
/// visibility (<c>OffRate</c>) and it is seen again.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><b>Real bodies.</b> The scanner is a real <c>faohshcar</c> hull
/// (Length 300 → radius 150, ScannerRange 3600, Signature 1.5) and the cloaked
/// target a second real <c>faohshcar</c> on the enemy side (a faohshcar carries
/// real energy, so its cloak can reach full strength). Uncloaked detection
/// radius faohshcar→faohshcar = 150 + 150 + 3600·1.5
/// = 5700; with the test cloak fully engaged (MaxCloaking 0.994 → Cloaking 0.006,
/// cloak signature 0) the target's effective signature collapses to 1.5·0.006 ≈
/// 0.009 and the radius to ~332. Placing the pair 2000 units apart therefore
/// puts the target <em>inside</em> normal detection range yet <em>beyond</em>
/// the reduced cloaked radius — the discriminating geometry that proves the
/// mechanic, not a range tweak.</para>
/// <para><b>Cloak part.</b> A synthetic <see cref="DataPartTypeIGC"/> with a
/// <see cref="DataPartTypeIGC.Cloak"/> subtype and controlled sim-relevant values
/// (fast OnRate/OffRate so the ramp converges within a tick at the test's 0.25 s
/// step; MaxCloaking near 1 so the visible fraction collapses) — the same
/// controlled-value convention the sweep tests use to inject asteroids, and the
/// reader needs no corpus numbers to reproduce the arithmetic above. Ambient
/// noise is controlled with the same <see cref="CleanBase"/> push as the P132
/// sweep tests (base point 8000+ beyond every real asteroid and station, so the
/// test's own ship is the only same-sector scanner that can reach it).</para>
/// </remarks>
public class CloakSimTests
{
    private const short Side0 = 0;
    private const short Side1 = 1;

    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static DataHullTypeIGC Hull(string modelName) =>
        PayloadDecodeTests.Hulls().First(h => h.V.ModelName == modelName).V;

    /// <summary>A real faohshcar scanner hull (the P131 scanner); also the
    /// cloaked target (a faohshcar carries real energy so its cloak reaches full
    /// strength).</summary>
    private static readonly DataHullTypeIGC ScannerHull = Hull("faohshcar");

    /// <summary>
    /// A synthetic cloak part with controlled sim values. Fast OnRate/OffRate
    /// (≥ 4× the 0.25 s tick so the ramp converges in one tick), MaxCloaking 0.997
    /// (visible floor 0.003), modest energy draw (6/s → 1.5/tick, well under the
    /// scanner hull's energy so it never starves in a few ticks), zero part
    /// signature (so the base is exactly the hull's 0.5).
    /// </summary>
    private static readonly DataPartTypeIGC CloakPart = new(
        Price: 8800, TimeToBuild: 450, ModelName: "bcl1gncr.igc", IconName: "",
        Name: "Test Cloak", Description: "", GroupId: 0,
        TtbmRequired: [], TtbmEffects: [],
        Mass: 955f, Signature: 0.0f, PartId: 30101, SuccessorPartId: -1, PartMask: 801,
        InventoryLineMdl: "", Type: new DataPartTypeIGC.Cloak(
            EnergyConsumption: 6.0f, MaxCloaking: 0.997f, OnRate: 4.0f, OffRate: 4.0f,
            EngageSound: -1, DisengageSound: -1));

    /// <summary>
    /// The detection-test variant: near-zero draw so a small-hull target's energy
    /// pool (e.g. the dreghpod pod) never throttles the cloak before it reaches
    /// full strength — isolating the signature mechanic from the (separately
    /// tested) energy-throttle behaviour. Same geometry otherwise.
    /// </summary>
    private static readonly DataPartTypeIGC LowDrawCloakPart = new(
        Price: 8810, TimeToBuild: 440, ModelName: "bcl1gncr.igc", IconName: "",
        Name: "Test Cloak (low draw)", Description: "", GroupId: 0,
        TtbmRequired: [], TtbmEffects: [],
        Mass: 956f, Signature: 0.0f, PartId: 30201, SuccessorPartId: -1, PartMask: 802,
        InventoryLineMdl: "", Type: new DataPartTypeIGC.Cloak(
            EnergyConsumption: 0.005f, MaxCloaking: 0.994f, OnRate: 4.0f, OffRate: 4.0f,
            EngageSound: -1, DisengageSound: -1));

    /// <summary>A started mission with 2 real sides over a real map.</summary>
    private static Mission NewStartedMission()
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(2).ToList();
        IReadOnlyList<SideSpec> specs =
        [
            new SideSpec(0, "Team 1", civs[0].CivilizationId),
            new SideSpec(1, "Team 2", civs[1].CivilizationId),
        ];
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    /// <summary>Pick a sector and a base point 8000+ units beyond every real
    /// asteroid and station in it (the P132 <see cref="CrossSideVisibilitySweepTests"/>
    /// control).</summary>
    private static (Sector Sector, Vector3 Base) CleanBase(Mission m, float clearance = 8000f)
    {
        foreach (var sector in m.Sectors)
        {
            var content = new List<Vector3>();
            foreach (var a in sector.Asteroids)
                if (a.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var st in sector.Stations)
                if (st.Position is { Length: 3 } p) content.Add(new Vector3(p[0], p[1], p[2]));

            float cy = content.Count == 0 ? 0f : content.Average(v => v.Y);
            float cz = content.Count == 0 ? 0f : content.Average(v => v.Z);
            float maxX = content.Count == 0 ? 0f : content.Max(v => v.X);
            return (sector, new Vector3(maxX + clearance, cy, cz));
        }
        throw new InvalidOperationException("no sector available in corpus map");
    }

    private static Ship NewShip(short id, DataHullTypeIGC hull, short side, Vector3 pos) =>
        new(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };

    // ── 1. Activation ────────────────────────────────────────────────────────

    [Fact]
    public void Setting_the_cloak_bit_engages_the_cloak_on_a_mounted_cloak()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        var ship = NewShip(9701, ScannerHull, Side0, basePos);
        mission.PlaceShip(ship, sector);
        ship.MountPart(CloakPart, 1, mountId: 0, fractionMounted: 255);

        // No bit yet — nothing cloaks even though a cloak is mounted.
        mission.Update(0.25);
        Assert.False(ship.IsCloaked);
        Assert.Equal(1.0f, ship.Cloaking);

        // Set the CloakActive control bit (the toggle/held input path).
        ship.StateM |= ShipControlState.CloakActive;
        mission.Update(0.5);

        Assert.True(ship.IsCloaked, "the cloak must engage when the CloakActive bit is set");
        Assert.True(ship.Cloaking < 1.0f, "the visible fraction must drop while cloaked");

        Console.WriteLine($"CLOAK-ON: cloak bit set -> IsCloaked={ship.IsCloaked}, Cloaking={ship.Cloaking:F3}");
    }

    [Fact]
    public void Without_the_bit_or_without_a_cloak_the_ship_never_cloaks()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        // Case A: a cloak mounted but the bit never set.
        var a = NewShip(9711, ScannerHull, Side0, basePos);
        mission.PlaceShip(a, sector);
        a.MountPart(CloakPart, 1, mountId: 0, fractionMounted: 255);
        // Case B: the bit set but no cloak mounted.
        var b = NewShip(9712, ScannerHull, Side0, basePos + new Vector3(2000f, 0f, 0f));
        mission.PlaceShip(b, sector);
        b.StateM |= ShipControlState.CloakActive;

        mission.Update(0.25);
        mission.Update(0.5);

        Assert.False(a.IsCloaked, "a mounted cloak with the bit clear must stay uncloaked");
        Assert.Equal(1.0f, a.Cloaking);
        Assert.False(b.IsCloaked, "the bit with no cloak mounted must not cloak (native clears cloakActiveIGC, shipIGC.h:1839)");
        Assert.Equal(1.0f, b.Cloaking);

        Console.WriteLine("CLOAK-NONE: neither the bit-less cloak nor the cloak-less bit engages");
    }

    [Fact]
    public void A_cloak_on_a_non_zero_mount_never_engages()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        // Native CcloakIGC only acts from mount 0 (fActive() = mountID == 0,
        // cloakigc.h:66-69; Update asserts m_mountID == 0, cloakigc.cpp:62).
        var ship = NewShip(9721, ScannerHull, Side0, basePos);
        mission.PlaceShip(ship, sector);
        ship.MountPart(CloakPart, 1, mountId: 1, fractionMounted: 255);
        ship.StateM |= ShipControlState.CloakActive;

        mission.Update(0.25);
        mission.Update(0.5);

        Assert.False(ship.IsCloaked, "a cloak on mount 1 must not engage (mount 0 only)");
        Assert.Equal(1.0f, ship.Cloaking);

        Console.WriteLine("CLOAK-MOUNT: ET_Cloak on mount 1 with the bit set stays uncloaked (mount-0 only)");
    }

    // ── 2. Energy cost ───────────────────────────────────────────────────────

    [Fact]
    public void Cloaking_drains_energy_each_active_tick_and_not_when_idle()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        var ship = NewShip(9731, ScannerHull, Side0, basePos);
        mission.PlaceShip(ship, sector);
        ship.MountPart(CloakPart, 1, mountId: 0, fractionMounted: 255);

        float startEnergy = ship.Energy;

        // One idle tick: no drain without the bit.
        mission.Update(0.25);
        Assert.Equal(startEnergy, ship.Energy, precision: 3);

        // One active tick: 6/s · 0.25 s = 1.5 energy drained.
        ship.StateM |= ShipControlState.CloakActive;
        mission.Update(0.5);
        Assert.True(ship.Energy < startEnergy, "the cloak must drain energy while active");
        Assert.Equal(startEnergy - 1.5f, ship.Energy, precision: 2);

        Console.WriteLine($"CLOAK-ENERGY: idle holds {startEnergy:F1}; one active tick drains to {ship.Energy:F1} (expected {startEnergy - 1.5f:F1})");
    }

    [Fact]
    public void A_short_energy_pool_throttles_the_cloaks_target_strength()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        var ship = NewShip(9741, ScannerHull, Side0, basePos);
        mission.PlaceShip(ship, sector);
        ship.MountPart(CloakPart, 1, mountId: 0, fractionMounted: 255);

        // Starve the pool to 0.5. Needed this tick = 6 · 0.25 = 1.5, so
        // r = 0.5/1.5 = 1/3 and the target cloak strength is scaled by r² = 1/9:
        // throttled MaxCloaking ≈ 0.997/9 ≈ 0.111 -> Cloaking floor ≈ 0.889,
        // decisively above the full-floor 0.003 (cloakigc.cpp:96-102).
        ship.Energy = 0.5f;
        ship.StateM |= ShipControlState.CloakActive;
        mission.Update(0.25);
        mission.Update(0.5);

        Assert.True(ship.IsCloaked, "even a starving cloak engages");
        Assert.True(ship.Cloaking > 0.5f,
            $"a short pool must throttle the cloak (Cloaking={ship.Cloaking:F3}, expected > 0.5 not the ~0.003 full floor)");

        Console.WriteLine($"CLOAK-THROTTLE: 0.5 energy vs 1.5/tick -> Cloaking={ship.Cloaking:F3} (r² throttle, not full 0.003 floor)");
    }

    // ── 3. Detection interaction (the sweep) ─────────────────────────────────

    [Fact]
    public void A_cloaked_enemy_becomes_undetectable_within_normal_detection_range()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        // faohshcar (side 0) scanner, a SECOND faohshcar (side 1) target 2000 units
        // away. A faohshcar carries real energy, so its cloak can actually reach
        // full strength (a pod like dreghpod has ~0 energy and the native r²
        // energy-throttle never lets it — exercising the throttle, not the
        // signature mechanic, is the separate energy test). Uncloaked detection
        // radius faohshcar→faohshcar = 150 + 150 + 3600·1.5 = 5700, so 2000 is
        // comfortably inside; fully cloaked (Cloaking 0.006 → sig 0.009) the
        // radius collapses to ~332, so 2000 is beyond it.
        var scanner = NewShip(9751, ScannerHull, Side0, basePos);
        var target = NewShip(9752, ScannerHull, Side1, basePos + new Vector3(2000f, 0f, 0f));
        mission.PlaceShip(scanner, sector);
        mission.PlaceShip(target, sector);
        target.MountPart(LowDrawCloakPart, 1, mountId: 0, fractionMounted: 255);

        // (a) Uncloaked: the enemy is plainly visible at 2000 u.
        mission.Update(0.25);
        Assert.True(target.SeenBySide(Side0), "an uncloaked in-range enemy must be seen");

        // (b) Cloaked: the same geometry, now undetectable.
        target.StateM |= ShipControlState.CloakActive;
        mission.Update(0.5);
        Assert.True(target.IsCloaked);
        Assert.False(target.SeenBySide(Side0),
            "a cloaked enemy inside normal detection range must become undetectable (shrunk signature)");
        Assert.True(target.SeenBySide(Side1), "its own side still sees it");

        // (c) Decloak: clearing the bit ramps it back to visible.
        target.StateM &= ~ShipControlState.CloakActive;
        mission.Update(0.75);
        Assert.False(target.IsCloaked);
        Assert.Equal(1.0f, target.Cloaking);
        Assert.True(target.SeenBySide(Side0), "a decloaked in-range enemy must be seen again");

        Console.WriteLine($"CLOAK-DETECT: 2000 u pair -> seen (uncloaked) -> unseen (cloaked) -> seen (decloaked)");
    }

    [Fact]
    public void Decloaking_restores_full_visibility_within_one_ramp_period()
    {
        var mission = NewStartedMission();
        var (sector, basePos) = CleanBase(mission);

        var ship = NewShip(9761, ScannerHull, Side0, basePos);
        mission.PlaceShip(ship, sector);
        ship.MountPart(CloakPart, 1, mountId: 0, fractionMounted: 255);
        ship.StateM |= ShipControlState.CloakActive;
        mission.Update(0.25);
        Assert.True(ship.IsCloaked);

        ship.StateM &= ~ShipControlState.CloakActive;
        mission.Update(0.5);

        // OffRate 4/s · 0.25 s = 1.0 > MaxCloaking 0.997, so one tick fully decloaks.
        Assert.False(ship.IsCloaked);
        Assert.Equal(1.0f, ship.Cloaking, precision: 3);

        Console.WriteLine("CLOAK-OFF: clearing the bit fully restores visibility in one ramp period");
    }
}
