using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P476 — the visibility half of the side-global-attribute (GA) wiring:
/// <c>c_gaScanRange</c> (11) boosts a SCANNER side's detection range and
/// <c>c_gaSignature</c> (12) attenuates a TARGET side's signature, through three
/// new effective getters (<see cref="Ship.EffectiveScannerRange"/>,
/// <see cref="Station.EffectiveScannerRange"/>,
/// <see cref="Probe.EffectiveScannerRange"/>) consumed by the sector's
/// cross-side visibility sweep (<c>Sector.ResolveCrossSideVisibility</c>).
/// Native wraps the scan range in the GA at the <c>MyHullType</c>/
/// <c>MyStationType</c>/probe getters (<c>shipIGC.cpp:4558</c>,
/// <c>stationIGC.cpp:591</c>, <c>probeigc.h:165</c>) and divides the detection
/// margin by the TARGET side's <c>c_gaSignature</c> (<c>shipIGC.h:357</c>,
/// <c>stationIGC.h:322</c>, <c>probeigc.h:177</c>).
/// <para>Each test is data-derived from the real PCore001 static core and
/// compares the GA-adjusted outcome to the identity (1.0) baseline to isolate
/// the multiplier; every behavioural test failed on the pre-P476 tree first
/// (recorded in the P476 report) because the sweep ignored both attributes.</para>
/// </summary>
public sealed class P476SideGaVisibilityTests
{
    private const short Side0 = 0;
    private const short Side1 = 1;

    private static readonly Lazy<StaticCore> Core = new(() =>
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

    private static readonly Lazy<MissionMap> Map = new(() =>
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

    /// <summary>A real faohshcar scanner hull (radius 150, scanner range 3600).</summary>
    private static readonly DataHullTypeIGC ScannerHull = Hull("faohshcar");

    /// <summary>A real dreghpod target hull (radius = ½·Length = 2, signature 0.5).</summary>
    private static readonly DataHullTypeIGC TargetHull = Hull("dreghpod");

    private static float TargetRadius => 0.5f * TargetHull.Length;

    /// <summary>An all-1.0 (identity) GlobalAttributeSet with the given cells set.</summary>
    private static GlobalAttributeSet SideGas(params (int idx, float val)[] factors)
    {
        var arr = new float[GlobalAttributeSet.Count];
        Array.Fill(arr, 1f);
        foreach (var (idx, val) in factors) arr[idx] = val;
        return new GlobalAttributeSet(arr);
    }

    /// <summary>A started two-side mission whose side GAs are fully replaced with
    /// identity-plus-<paramref name="side0Factors"/>/<paramref name="side1Factors"/>
    /// (the P475/P467b recipe), so the applied factors are the ONLY GA difference —
    /// the real civ base is overridden, isolating the multiplier exactly.</summary>
    private static Mission NewMission(
        (int idx, float val)[] side0Factors,
        (int idx, float val)[] side1Factors)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(Core.Value, Map.Value,
            [new SideSpec(0, "Team 1", civ.CivilizationId), new SideSpec(1, "Enemy", civ.CivilizationId)],
            new NullIgcSite());
        m.Stage = MissionStage.Started;
        m.Sides[0].GlobalAttributes = SideGas(side0Factors);
        m.Sides[1].GlobalAttributes = SideGas(side1Factors);
        return m;
    }

    /// <summary>A sector and a base point 8000+ units beyond every real asteroid and
    /// station in it, so the test's own scanner is the only same-sector scanner that
    /// can reach the base (even GA-boosted), and no real rock occludes the short ray.</summary>
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

    // ── End-to-end through a REAL PCore001 development ──────────────────────

    /// <summary>Real dev 157 'GA: Ship Sensors 1' changes this task's attribute
    /// (gas[c_gaScanRange=11] = 1.1). Accumulate its Gas onto a real side the way
    /// the server does on dev completion (Server.Economy.cs:978-979:
    /// <c>side.GlobalAttributes = side.GlobalAttributes.Apply(FromArray(dev.Gas))</c>)
    /// and show a real ship on that side's EffectiveScannerRange scales by it —
    /// the dev → side-GA → accessor chain end-to-end with real core data.</summary>
    [Fact]
    public void Real_sensor_dev_gas_accumulates_and_scales_effective_scan_range()
    {
        // Civ 1 (Dreghklar), the same civ the P446 live E2E seats its bot on.
        var civ = Core.Value.Civilizations[1];
        var m = Mission.Assemble(Core.Value, Map.Value,
            [new SideSpec(0, "Team 1", civ.CivilizationId), new SideSpec(1, "Enemy", civ.CivilizationId)],
            new NullIgcSite());
        m.Stage = MissionStage.Started;

        var dev = Core.Value.Developments[157];
        Assert.True(dev.Name.Contains("Sensors"), $"dev 157 unexpected: '{dev.Name}'");
        Assert.Equal(1.1f, dev.Gas[GlobalAttributeSet.ScanRange], 5);

        float before = m.Sides[0].GlobalAttributes.Get(GlobalAttributeSet.ScanRange);   // civ base

        // The exact accumulation line the server runs on dev completion
        // (Server.Economy.cs:978-979).
        m.Sides[0].GlobalAttributes = m.Sides[0].GlobalAttributes.Apply(GlobalAttributeSet.FromArray(dev.Gas));

        Assert.Equal(before * 1.1f, m.Sides[0].GlobalAttributes.Get(GlobalAttributeSet.ScanRange), 5);
        Console.WriteLine($"P476-E2E: real dev 157 '{dev.Name}' gas[11]=1.1 -> side GA[11] {before:F3} -> {m.Sides[0].GlobalAttributes.Get(GlobalAttributeSet.ScanRange):F3}");

        var (sector, basePos) = CleanBase(m);
        var ship = NewShip(7901, ScannerHull, Side0, basePos);
        m.PlaceShip(ship, sector);
        Assert.Equal(ScannerHull.ScannerRange * m.Sides[0].GlobalAttributes.Get(GlobalAttributeSet.ScanRange),
                     ship.EffectiveScannerRange(), 3);
        Console.WriteLine($"P476-E2E: ship EffectiveScannerRange = {ship.EffectiveScannerRange():F1} (raw {ScannerHull.ScannerRange})");
    }

    private static Station MakeStation(DataStationTypeIGC type, short side)
    {
        var spot = new Vector3(0f, 0f, -500f);
        var data = new DataStationIGC(
            Position: [spot.X, spot.Y, spot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: 0, SideId: side, StationId: 9899,
            StationTypeId: type.StationTypeId, BpHull: 1, BpShield: 1, Name: "BASE_9899");
        return new Station(data, type);
    }

    // ── Plumbing: index constants + identity-is-raw ─────────────────────────

    [Fact]
    public void ScanRange_and_Signature_indices_match_native_and_sideless_is_raw()
    {
        Assert.Equal(11, GlobalAttributeSet.ScanRange);    // c_gaScanRange, igc.h:601
        Assert.Equal(12, GlobalAttributeSet.Signature);    // c_gaSignature, igc.h:602

        // Sideless / unresolvable-side reads return the RAW value (identity 1.0) —
        // the honest neutral the P446/P467/P475 accessors established.
        var sideless = new Ship(7001, ScannerHull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        Assert.Equal(ScannerHull.ScannerRange, sideless.EffectiveScannerRange());

        var stationType = Core.Value.StationTypes.Values.First();
        Assert.Equal(stationType.ScannerRange, MakeStation(stationType, Side0).EffectiveScannerRange());

        var probeType = Core.Value.ProbeTypes.Values.First();
        var m2 = NewMission([], []);
        var (sec2, bp2) = CleanBase(m2);
        var launcherless = new Probe(7002, sec2, probeType, bp2, m2.LastUpdate, Side0);   // no launcher → identity
        Assert.Equal(probeType.ScannerRange, launcherless.EffectiveScannerRange());
    }

    // ── Accessor unit tests (per consumer: synthetic multiplier → scaled value) ──

    [Fact]
    public void Ship_EffectiveScannerRange_scales_by_side_ga()
    {
        var m = NewMission([(GlobalAttributeSet.ScanRange, 1.5f)], []);
        var (sector, basePos) = CleanBase(m);
        var ship = NewShip(7101, ScannerHull, Side0, basePos);
        m.PlaceShip(ship, sector);

        Assert.Equal(ScannerHull.ScannerRange * 1.5f, ship.EffectiveScannerRange(), 3);
    }

    [Fact]
    public void Station_EffectiveScannerRange_scales_by_side_ga()
    {
        var m = NewMission([(GlobalAttributeSet.ScanRange, 1.5f)], []);
        var (sector, _) = CleanBase(m);
        var st = MakeStation(Core.Value.StationTypes.Values.First(), Side0);
        m.PlaceStation(st, sector);

        Assert.Equal(st.Type.ScannerRange * 1.5f, st.EffectiveScannerRange(), 3);
    }

    [Fact]
    public void Probe_EffectiveScannerRange_scales_by_side_ga()
    {
        var m = NewMission([(GlobalAttributeSet.ScanRange, 1.5f)], []);
        var (sector, basePos) = CleanBase(m);
        var scout = NewShip(7301, ScannerHull, Side0, basePos);
        m.PlaceShip(scout, sector);                       // gives scout.Mission (the probe's GA route)
        var probeType = Core.Value.ProbeTypes.Values.First();
        var probe = new Probe(7302, sector, probeType, basePos, m.LastUpdate, Side0, scout);

        Assert.Equal(probeType.ScannerRange * 1.5f, probe.EffectiveScannerRange(), 3);
    }

    // ── Behavioural: the sweep now honours both attributes (fail-first proofs) ──

    /// <summary>faohshcar→dreghpod raw detection radius = 150 + 2 + 3600·0.5 = 1952.
    /// With the scanner side's c_gaScanRange = 1.5 it becomes 150 + 2 + 5400·0.5 = 2852.
    /// A target at 2400 (1952 &lt; 2400 &lt; 2852) is beyond the raw radius but inside the
    /// boosted one — pre-P476 (raw) it was NOT seen, post it IS.</summary>
    [Fact]
    public void Ship_scanRange_ga_extends_the_sweep_detection_radius()
    {
        var m = NewMission([(GlobalAttributeSet.ScanRange, 1.5f)], []);
        var (sector, basePos) = CleanBase(m);

        var scanner = NewShip(7501, ScannerHull, Side0, basePos);
        var target = NewShip(7502, TargetHull, Side1, basePos + new Vector3(2400f, 0f, 0f));
        m.PlaceShip(scanner, sector);
        m.PlaceShip(target, sector);

        m.Update(0.25);   // first pass runs the sweep

        Assert.True(target.SeenBySide(Side0),
            "a side-0 scanner boosted to 2852 must see a side-1 target 2400 away (raw 1952 could not)");
        Console.WriteLine($"P476-SCAN: faohshcar side-0 GA[11]=1.5 -> target at 2400 (raw 1952, boosted 2852) SeenBySide(0)=true");
    }

    /// <summary>The target side's c_gaSignature = 2.0 halves the dreghpod's effective
    /// detection signature (0.5 → 0.25): radius drops from 1952 to 1052. A target at
    /// 1500 (1052 &lt; 1500 &lt; 1952) is inside the raw radius but outside the attenuated one —
    /// pre-P476 (target GA ignored) it WAS seen, post it is NOT.</summary>
    [Fact]
    public void Target_signature_ga_resistance_shrinks_the_sweep_detection_radius()
    {
        var m = NewMission([], [(GlobalAttributeSet.Signature, 2.0f)]);
        var (sector, basePos) = CleanBase(m);

        var scanner = NewShip(7601, ScannerHull, Side0, basePos);
        var target = NewShip(7602, TargetHull, Side1, basePos + new Vector3(1500f, 0f, 0f));
        m.PlaceShip(scanner, sector);
        m.PlaceShip(target, sector);

        m.Update(0.25);

        Assert.False(target.SeenBySide(Side0),
            "a side-1 target with GA[12]=2.0 (attenuated radius 1052) must NOT be seen 1500 away (raw 1952 would)");
        Assert.True(target.SeenBySide(Side1), "own side always visible");
        Console.WriteLine($"P476-SIG: dreghpod side-1 GA[12]=2.0 -> target at 1500 (raw 1952, attenuated 1052) SeenBySide(0)=false");
    }

    /// <summary>The probe's boosted scan range also applies in the sweep: with the
    /// probe's side at c_gaScanRange = 1.5, a target beyond the probe's RAW radius but
    /// inside its BOOSTED radius becomes seen (probe is the only side-0 scanner).</summary>
    [Fact]
    public void Probe_scanRange_ga_extends_detection_in_the_sweep()
    {
        var m = NewMission([(GlobalAttributeSet.ScanRange, 1.5f)], []);
        var (sector, basePos) = CleanBase(m);

        // Give the probe a Ship launcher (so its side GA resolves), then remove the
        // scout so the probe is the ONLY side-0 scanner (ProbeClusterVisibilityTests'
        // own b.Add(probe); b.Remove(scout) trick).
        var scout = NewShip(7701, ScannerHull, Side0, basePos);
        m.PlaceShip(scout, sector);
        var pt = Core.Value.ProbeTypes.Values.First();
        float rawR = pt.Radius + TargetRadius + pt.ScannerRange * TargetHull.Signature;
        float boostedR = pt.Radius + TargetRadius + pt.ScannerRange * 1.5f * TargetHull.Signature;
        float d = (rawR + boostedR) / 2f;
        var probe = new Probe(7702, sector, pt, basePos, m.LastUpdate, Side0, scout);
        sector.Add(probe);
        sector.Remove(scout);

        var target = NewShip(7703, TargetHull, Side1, basePos + new Vector3(d, 0f, 0f));
        m.PlaceShip(target, sector);

        m.Update(0.25);

        Assert.True(rawR < d && d < boostedR, "test geometry: target must sit between raw and boosted probe radii");
        Assert.True(target.SeenBySide(Side0),
            $"probe (only side-0 scanner) boosted to range {boostedR:F1} must see target {d:F1} away (raw {rawR:F1} could not)");
        Console.WriteLine($"P476-PROBE: probe side-0 GA[11]=1.5 (raw {rawR:F0}, boosted {boostedR:F0}) -> target at {d:F0} SeenBySide(0)=true");
    }
}
