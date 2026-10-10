using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P259 — the baseline per-side cluster visibility capability
/// (<see cref="Side.VisibilityRange"/> / <see cref="Side.CanSeeCluster"/> +
/// the periodic <see cref="Mission.RefreshSideVisibility"/> pass). A cluster is
/// visible to a side iff the side owns a live ship or station in a cluster that
/// is within the side's <see cref="Side.VisibilityRange"/> of it on the strategic
/// map (screen distance); presence in the cluster itself is the distance-zero
/// case. These tests drive the real <see cref="Mission.Assemble"/>/<see cref="Mission.PlaceShip"/>
/// path over real corpus sectors, controlling the range explicitly so every
/// branch (presence, within-range, out-of-range, other-side ownership, periodic
/// re-evaluation over real ticks) is exercised deterministically.
/// </summary>
/// <remarks>
/// The strategic-map screen proxy (see the P259 writeup) makes the cross-cluster
/// distance test tractable: the test reads the real sectors' screen coordinates
/// and sets the side's <see cref="Side.VisibilityRange"/> to the exact
/// screen-distance between chosen sectors, so "exactly in range" and "clearly
/// out of range" are both guaranteed from real corpus data.
/// </remarks>
public class SideClusterVisibilityTests
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
            if (map.Clusters.Count < 3) continue;
            return map;
        }
        throw new InvalidOperationException("no multi-cluster map found in corpus");
    });

    private static DataHullTypeIGC Hull(string modelName) =>
        PayloadDecodeTests.Hulls().First(h => h.V.ModelName == modelName).V;

    private static readonly DataHullTypeIGC ScoutHull = Hull("fig22");

    /// <summary>A started mission with <paramref name="sides"/> real sides over a real map.</summary>
    private static Mission NewStartedMission(int sides)
    {
        var civs = RepresentativeCore.Value.Civilizations.Values
            .OrderBy(c => c.CivilizationId).Take(sides).ToList();
        IReadOnlyList<SideSpec> specs = [];
        for (int i = 0; i < civs.Count; i++)
            specs = specs.Append(new SideSpec((short)i, $"Team {i + 1}", civs[i].CivilizationId)).ToList();
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, specs, new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship NewShip(short id, short side, Sector sector) =>
        new(id, ScoutHull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side };

    /// <summary>Strategic-map screen distance between two sectors.</summary>
    private static float ScreenDist(Sector a, Sector b)
    {
        float dx = a.Cluster.ScreenX - b.Cluster.ScreenX;
        float dy = a.Cluster.ScreenY - b.Cluster.ScreenY;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Pick three distinct sectors that are <b>empty of real stations</b> (no
    /// runtime station at all). Real corpus sectors often carry a station owned by
    /// a real side (e.g. in the representative map, Nadir/1051 has a side-0
    /// station and Rib'zki/1062 a side-1 one), and such stations would grant that
    /// side incidental presence in our presence tests. Empty sectors isolate each
    /// test's own placed ship/station as the only possible source of visibility.
    /// </summary>
    private static (Sector A, Sector B, Sector C) ThreeDistinct(Mission m)
    {
        var empty = m.Sectors.Where(s => s.StationRuntimes.Count == 0).ToList();
        if (empty.Count < 3)
            throw new InvalidOperationException("test needs >= 3 station-free sectors");
        return (empty[0], empty[1], empty[2]);
    }

    // ── 1. Physical presence grants visibility at distance zero ─────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Physically_present_cluster_is_visible_at_zero_range()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];

        var ship = NewShip(8101, Side0, a);
        mission.PlaceShip(ship, a);
        side0.VisibilityRange = 0f;   // only distance-zero (presence) counts

        Assert.True(side0.CanSeeCluster(a.Id), "a side physically in a cluster always sees it");
        Assert.False(side0.CanSeeCluster(b.Id), "a cluster with no owned asset at zero range is unseen");
    }

    // ── 2. An owned asset reveals a cluster within strategic range (not present) ──

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Owned_asset_reveals_cluster_within_strategic_range_even_when_not_present()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];

        var ship = NewShip(8102, Side0, a);
        mission.PlaceShip(ship, a);

        // Exactly in range: b is within VisibilityRange of a's screen position.
        side0.VisibilityRange = ScreenDist(a, b);
        Assert.True(side0.CanSeeCluster(b.Id), "an owned asset in a neighbour reveals b when b is within range");
        Assert.True(side0.CanSeeCluster(a.Id), "presence in a is still visible");

        // Now drop the range to zero: only presence counts, so b falls out.
        side0.VisibilityRange = 0f;
        Assert.False(side0.CanSeeCluster(b.Id), "at zero range a not-present cluster is hidden");
        Assert.True(side0.CanSeeCluster(a.Id), "presence never hides");
    }

    // ── 3. Nothing owned within range is false; other sides' assets don't count ──

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cluster_is_unseen_when_nothing_owned_is_within_range()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];
        var side1 = mission.Sides[Side1];
        side0.VisibilityRange = 0f;   // presence-only, so side0's real stations elsewhere don't leak

        // Side0 owns nothing in the empty test sectors: sees none of them.
        Assert.False(side0.CanSeeCluster(a.Id), "side with no owned asset in a sees nothing there");
        Assert.False(side0.CanSeeCluster(b.Id), "side with no owned asset in b sees nothing there");

        // A Side1-owned ship in a does NOT grant Side0 visibility of a or b.
        var enemy = NewShip(8103, Side1, a);
        mission.PlaceShip(enemy, a);
        Assert.False(side0.CanSeeCluster(a.Id), "another side's presence does not leak to side0");
        Assert.False(side0.CanSeeCluster(b.Id));

        // But it does reveal b to side1 when b is within side1's range.
        side1.VisibilityRange = ScreenDist(a, b);
        Assert.True(side1.CanSeeCluster(b.Id), "side1's own asset reveals b to side1 only");
    }

    // ── 4. Periodic refresh populates the visible-cluster set ────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Periodic_refresh_populates_the_visible_cluster_set()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];

        var ship = NewShip(8104, Side0, a);
        mission.PlaceShip(ship, a);
        side0.VisibilityRange = 0f;

        mission.RefreshSideVisibility();

        Assert.True(side0.VisibleClusters.Contains(a.Id), "side's own cluster lands in the refreshed set");
        Assert.False(side0.VisibleClusters.Contains(b.Id), "out-of-range cluster is absent from the refreshed set");
    }

    // ── 5. Re-evaluates as a ship moves between clusters over real ticks ─────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Visible_clusters_reevaluate_as_a_ship_moves_between_clusters_over_real_ticks()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];
        side0.VisibilityRange = 0f;   // presence-only, so movement is the whole story

        var ship = NewShip(8105, Side0, a);
        mission.PlaceShip(ship, a);

        // Tick past the first periodic refresh (cadence = Mission.VisibilityRefreshTicks).
        for (int i = 1; i <= Mission.VisibilityRefreshTicks; i++)
            mission.Update(i * 0.25);

        Assert.True(side0.VisibleClusters.Contains(a.Id), "after the first refresh the ship's cluster is visible");
        Assert.False(side0.VisibleClusters.Contains(b.Id));

        // Move the ship to b (leaves a's model list, joins b's) over real ticks.
        a.Remove(ship);
        mission.PlaceShip(ship, b);
        Assert.True(side0.CanSeeCluster(b.Id), "live query flips immediately after the move");
        Assert.False(side0.CanSeeCluster(a.Id));

        // Tick past the next refresh; the cached set follows.
        int start = Mission.VisibilityRefreshTicks + 1;
        for (int i = start; i <= 2 * Mission.VisibilityRefreshTicks; i++)
            mission.Update(i * 0.25);

        Assert.False(side0.VisibleClusters.Contains(a.Id), "cached set forgets the vacated cluster");
        Assert.True(side0.VisibleClusters.Contains(b.Id), "cached set adopts the new cluster");
    }

    // ── 6. An owned station grants visibility (presence via station) ─────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Owned_station_in_a_cluster_grants_the_side_visibility()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];

        var type = RepresentativeCore.Value.StationTypes.Values.OrderBy(t => t.StationTypeId).First();
        var stationData = new DataStationIGC(
            Position: [0f, 0f, 0f],
            Up: [0f, 1f, 0f],
            Forward: [0f, 0f, 1f],
            Rotation: [0f, 0f, 0f],
            ClusterId: a.Id,
            SideId: Side0,
            StationId: 9501,
            StationTypeId: type.StationTypeId,
            BpHull: 1,
            BpShield: 1,
            Name: "P259-Test");
        var station = new Station(stationData, type);
        mission.PlaceStation(station, a);
        side0.VisibilityRange = 0f;

        Assert.True(side0.CanSeeCluster(a.Id), "an owned station in a cluster makes it visible");
        Assert.False(side0.CanSeeCluster(b.Id), "no station in b at zero range");
    }
}
