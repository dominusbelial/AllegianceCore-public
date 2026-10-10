using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using SimModel = Allegiance.Core.Sim.Model;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P138 — isolated predicates for <see cref="Sector.GetShipUpdateCandidates"/>,
/// the per-cluster candidate-list builder that will feed the
/// LIGHT/HEAVY_SHIPS_UPDATE (175/176) broadcast (P139). Predicate style, no
/// sim-loop wiring (foundation slice): each test drives the real
/// <see cref="Sector.GetShipUpdateCandidates"/> method directly against a real
/// <see cref="Sector"/> and asserts which ships are selected.
/// <para>
/// The load-bearing correction this arc made (see
/// <c>SHIP_UPDATE_CANDIDATE_LIST_P138.md</c> and the method's own doc) is that
/// the native candidate selection for this message family is
/// <b>cluster-membership only</b> — <c>pcluster-&gt;GetShips()</c>,
/// FedSrv.CPP:3727 — with <b>no <c>SeenBySide</c> check anywhere</b>
/// (FedSrv.CPP:3738-4216). Test 1 pins that: a foreign ship whose
/// <c>SeenBySide</c> is false is still a candidate.
/// </para>
/// </summary>
public class ShipUpdateCandidateListTests
{
    private static readonly Lazy<DataHullTypeIGC> RepresentativeHull = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            var ordered = core.HullTypes.Values.OrderBy(h => h.HullId).ToList();
            if (ordered.Count == 0) continue;
            return ordered[0];
        }
        throw new InvalidOperationException("no static core with a hull found in corpus");
    });

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

    private static DataProbeTypeIGC RealProbeType()
    {
        var t = RepresentativeCore.Value.ProbeTypes.Values
            .Where(pt => pt.Lifespan >= 1.0f)
            .OrderBy(pt => pt.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && RepresentativeCore.Value.ProbeTypes.Count > 0)
            throw new InvalidOperationException("representative core has no probe with lifespan >= 1");
        return t;
    }

    private static Sector NewSector() => new(SomeMap.Value.Clusters[0]);

    private static Ship MakeShip(short id, short? sideId = null) => new(
        id, RepresentativeHull.Value, Vector3.Zero, Vector3.Zero, Orientation.Identity)
    { SideId = sideId };

    /// <summary>⭐ The correction: candidate selection is <b>cluster-membership,
    /// not <c>SeenBySide</c></b>. Ships of <b>any</b> side physically in the
    /// sector are candidates — a foreign ship whose <c>SeenBySide(owner)</c> is
    /// <b>false</b> (nothing in this isolated sector has scanned anything, so
    /// every cross-side flag is false) still appears in the list. This is what
    /// separates this message family from the SeenBySide-gated
    /// STATIONS/ASTEROIDS/PROBES updates (P133-135).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Foreign_ships_with_unseen_SeenBySide_flags_are_still_candidates()
    {
        Require.Artwork();
        var sector = NewSector();
        var own = MakeShip(1001, sideId: 0);
        var foreign = MakeShip(1002, sideId: 1);   // a different side
        sector.Add(own);
        sector.Add(foreign);

        // Prove the foreign ship is NOT visible to side 0 (no sweep ran in this
        // isolated sector) yet is nonetheless a candidate — the load-bearing
        // absence of a SeenBySide gate.
        Assert.False(foreign.SeenBySide(0));

        var candidates = sector.GetShipUpdateCandidates();
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, s => ReferenceEquals(s, own));
        Assert.Contains(candidates, s => ReferenceEquals(s, foreign));
    }

    /// <summary>⭐ All ships in the sector (any side) are candidates: a small
    /// mixed-side squadron is selected wholesale, in stable membership order —
    /// matching native's cluster-wide <c>rgShips</c> (FedSrv.CPP:3727-3776)
    /// before it qsorts by deviation (deferred to the budget task).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void All_non_boarded_flying_ships_are_candidates_in_stable_order()
    {
        Require.Artwork();
        var sector = NewSector();
        var a = MakeShip(2001, sideId: 0);
        var b = MakeShip(2002, sideId: 1);
        var c = MakeShip(2003, sideId: 0);
        sector.Add(a);
        sector.Add(b);
        sector.Add(c);

        var once = sector.GetShipUpdateCandidates();
        var twice = sector.GetShipUpdateCandidates();

        Assert.Equal(3, once.Count);
        Assert.Equal([a, b, c], once);           // _models insertion order
        Assert.Equal(once, twice);               // deterministic given the same sector
    }

    /// <summary>⭐ A boarded ship (a turret gunner or observer riding on a
    /// <c>ParentShip</c>) is <b>not</b> a candidate — native routes these to the
    /// turret relay lists, not to <c>rgShips</c> (FedSrv.CPP:3779).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Boarded_ships_are_excluded()
    {
        Require.Artwork();
        var sector = NewSector();
        var carrier = MakeShip(3001);
        var gunner = MakeShip(3002);
        gunner.ParentShip = carrier;   // boarded on carrier
        sector.Add(carrier);
        sector.Add(gunner);

        var candidates = sector.GetShipUpdateCandidates();
        Assert.Single(candidates);
        Assert.Contains(candidates, s => ReferenceEquals(s, carrier));
        Assert.DoesNotContain(candidates, s => ReferenceEquals(s, gunner));
    }

    /// <summary>⭐ A docked ship is not physically in the cluster and is not a
    /// candidate (native ships absent from <c>cluster-&gt;GetShips()</c> while
    /// docked). Docking is tracked via <see cref="Ship.StationId"/>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_ships_are_excluded()
    {
        Require.Artwork();
        var sector = NewSector();
        var docked = MakeShip(4001);
        docked.StationId = 990;      // latched at a station
        var flying = MakeShip(4002);
        sector.Add(docked);
        sector.Add(flying);

        var candidates = sector.GetShipUpdateCandidates();
        Assert.Single(candidates);
        Assert.Contains(candidates, s => ReferenceEquals(s, flying));
        Assert.DoesNotContain(candidates, s => ReferenceEquals(s, docked));
    }

    /// <summary>⭐ A dead ship is not a candidate (a defensive belt-and-suspenders
    /// alongside the death-contract removal — dead ships leave the sector's
    /// <c>_models</c> anyway, Sector.cs:1105, and the rescue pass likewise guards
    /// <c>!Dead</c>, Sector.cs:592-597). Killed via the real damage path with no
    /// mission core, so the hull armor multiplier is 1.0 and the shield is 0
    /// (knocked-down) — every point leaks to hull.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Dead_ships_are_excluded()
    {
        Require.Artwork();
        var sector = NewSector();
        var dead = MakeShip(5001);
        // No Mission → DamageConstant returns 1.0; no shield (fraction 0, ≤ the
        // 0.1 down threshold) → leakage = amount. Huge amount ⟹ hull fraction ≤ 0.
        dead.ReceiveDamage(type: 0, amount: 1e9f, now: 0, Vector3.Zero, Vector3.Zero, launcher: null);
        Assert.True(dead.Dead, "the test's kill must actually register as dead");
        var alive = MakeShip(5002);
        sector.Add(dead);
        sector.Add(alive);

        var candidates = sector.GetShipUpdateCandidates();
        Assert.Single(candidates);
        Assert.Contains(candidates, s => ReferenceEquals(s, alive));
        Assert.DoesNotContain(candidates, s => ReferenceEquals(s, dead));
    }

    /// <summary>⭐ Non-ship models in the sector (here a <see cref="Probe"/>) are
    /// <b>not</b> ship-update candidates — only <see cref="Ship"/> instances are.
    /// And the returned list never duplicates an entry.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_ship_models_are_excluded_and_no_duplicates()
    {
        Require.Artwork();
        var sector = NewSector();
        var shipA = MakeShip(6001);
        var shipB = MakeShip(6002);
        var probe = new Probe(6999, sector, RealProbeType(), new Vector3(7f, -13f, 901f),
            now: 410.0, sideId: 1, launcher: null);
        sector.Add(shipA);
        sector.Add(probe);
        sector.Add(shipB);

        var candidates = sector.GetShipUpdateCandidates();
        Assert.All(candidates, s => Assert.IsType<Ship>(s));      // only ships
        Assert.Equal(2, candidates.Count);                         // probe excluded, no dup
        Assert.Equal([shipA, shipB], candidates);                  // ships still in stable order
    }
}
