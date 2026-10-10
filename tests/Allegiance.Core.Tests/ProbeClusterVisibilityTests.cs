using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P260 — the probe-visibility exception on <see cref="Side.CanSeeCluster"/>:
/// a deployed probe is a real, damageable <see cref="Probe"/> asset, so a live
/// probe owned by a side reveals the cluster it physically occupies (the
/// presence / distance-zero case), and the reveal lasts <b>exactly as long as
/// the probe survives</b> — clearing the moment the probe is destroyed or its
/// lifespan expires. Driven over the real
/// <see cref="Mission.Assemble"/>/<see cref="Mission.PlaceShip"/>/<see cref="Ship.DeployProbe"/>
/// path with real corpus sectors and probe types, controlling
/// <see cref="Side.VisibilityRange"/> so every branch is exercised
/// deterministically.
/// </summary>
/// <remarks>
/// <para>Sourcing: the on-disk native tree has no <c>CFSSide::CanSeeCluster</c>
/// (P259 established this). The real modern-FA probe mechanic is
/// <c>CprobeIGC::InScannerRange</c> (<c>src/Igc/probeigc.h:163-191</c>) — a
/// per-object scanner <b>confined to its own cluster</b> and <b>limited by its
/// real ScannerRange</b> from the probe's deployed position (plus line-of-sight):
/// the reveal is radar-limited, <b>not</b> a blanket cluster-wide uncover. At the
/// port's cluster abstraction that manifests as the presence case tested here: a
/// live owned probe grants visibility of the cluster it sits in while it lives.
/// The destructive path is real (<c>Probe.ReceiveDamage</c>,
/// <c>probeigc.h:65-146</c>; <c>Probe.OnUpdate</c> expiry,
/// <c>probeigc.cpp:333-337</c>), so a destroyed/expired probe leaves the sector
/// and the live query drops it immediately.</para>
/// <para>These tests drive the real <see cref="Ship.DeployProbe"/> path (a scout
/// flies in, deploys a probe, leaves — leaving only the probe in the cluster),
/// matching how a recon probe is actually used.</para>
/// </remarks>
public class ProbeClusterVisibilityTests
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

    private static readonly DataProbeTypeIGC RealProbeType =
        RepresentativeCore.Value.ProbeTypes.Values.OrderBy(t => t.ExpendableTypeId).First();

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

    private static (Sector A, Sector B, Sector C) ThreeDistinct(Mission m)
    {
        var empty = m.Sectors.Where(s => s.StationRuntimes.Count == 0).ToList();
        if (empty.Count < 3)
            throw new InvalidOperationException("test needs >= 3 station-free sectors");
        return (empty[0], empty[1], empty[2]);
    }

    /// <summary>Deploy a real probe from a scout in <paramref name="sector"/>,
    /// then have the scout leave so <em>only the probe</em> remains in the
    /// sector (a scout flies in, drops a recon probe, departs). Returns the
    /// probe.</summary>
    private static Probe ReconProbe(Ship scout, Sector sector)
    {
        var probe = scout.DeployProbe(RealProbeType, scout.Mission!.LastUpdate);
        sector.Remove(scout);   // the scout departs; the probe stays
        return probe;
    }

    // ── 1. A live owned probe makes its own cluster visible (presence) ──────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Live_owned_probe_reveals_its_cluster_after_the_scout_departs()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];
        side0.VisibilityRange = 0f;   // presence-only: only a probe physically in b counts

        // Scout is at home (a); b is far and holds nothing owned → unseen.
        var scout = NewShip(8106, Side0, a);
        mission.PlaceShip(scout, a);
        Assert.False(side0.CanSeeCluster(b.Id), "cluster is unseen before any probe is there");

        // The scout travels to b, deploys a probe, and departs — leaving only
        // the probe in b (the recon-probe use of a deployed probe).
        a.Remove(scout);
        mission.PlaceShip(scout, b);
        var probe = ReconProbe(scout, b);

        Assert.False(b.Models.Any(m => m is Ship), "no ship left in b — the probe is the only owned asset");
        Assert.True(b.Models.Contains(probe), "the probe lives in b");

        // The probe makes b visible even though the side has no ship/station in b.
        Assert.True(side0.CanSeeCluster(b.Id), "a live owned probe in b makes b visible");
        Assert.False(side0.CanSeeCluster(a.Id), "no owned asset in a at zero range");
    }

    // ── 2. The reveal clears when the probe is destroyed ────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_reveal_clears_when_the_probe_is_destroyed()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];
        side0.VisibilityRange = 0f;

        var scout = NewShip(8107, Side0, b);
        mission.PlaceShip(scout, b);
        var probe = ReconProbe(scout, b);

        Assert.True(side0.CanSeeCluster(b.Id), "b visible while the probe lives");

        // Destroy the probe through the real ReceiveDamage path (massive damage).
        var result = probe.ReceiveDamage(
            type: 0, amount: 1e9f, now: mission.LastUpdate,
            position1: probe.Position, position2: probe.Position, launcher: null);

        Assert.Equal(DamageResult.Killed, result);
        Assert.True(probe.Dead);
        Assert.False(b.Models.Contains(probe), "a destroyed probe leaves the sector");

        // With the probe gone and no other owned asset in b, the reveal clears.
        Assert.False(side0.CanSeeCluster(b.Id), "destroying the probe revokes visibility of b");
    }

    // ── 3. The reveal clears when the probe's lifespan expires ──────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_reveal_clears_when_the_probe_expires()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];
        side0.VisibilityRange = 0f;

        // A short-lived probe type (a copy of the real one with a tiny lifespan)
        // so the expiry tick is reached without simulating thousands of seconds.
        var shortLived = RealProbeType with { Lifespan = 5f };
        var scout = NewShip(8108, Side0, b);
        mission.PlaceShip(scout, b);
        var probe = new Probe(9100, b, shortLived, Vector3.Zero, mission.LastUpdate, Side0, scout);
        b.Add(probe);
        b.Remove(scout);   // scout departs; only the short-lived probe remains

        Assert.True(side0.CanSeeCluster(b.Id), "b visible while the probe is alive");

        // Advance mission time in small increments past the probe's expiry
        // (the probe's OnUpdate removes it from the sector once now >= TimeExpire).
        double t = mission.LastUpdate;
        double target = probe.TimeExpire + 0.25;
        while (t < target)
        {
            t += 0.5;
            mission.Update(t);
        }

        Assert.False(b.Models.Contains(probe), "an expired probe removes itself from the sector");
        Assert.False(side0.CanSeeCluster(b.Id), "expiring the probe revokes visibility of b");
    }

    // ── 4. A probe only grants visibility to its owning side ────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_reveal_is_scoped_to_the_owning_side()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];
        var side1 = mission.Sides[Side1];
        side0.VisibilityRange = 0f;
        side1.VisibilityRange = 0f;

        var scout = NewShip(8109, Side0, b);
        mission.PlaceShip(scout, b);
        ReconProbe(scout, b);

        Assert.True(side0.CanSeeCluster(b.Id), "the owning side sees its probe's cluster");
        Assert.False(side1.CanSeeCluster(b.Id), "another side does not benefit from side0's probe");
    }

    // ── 5. Periodic refresh adopts the probe reveal into the visible set ────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Periodic_refresh_adopts_the_probe_reveal()
    {
        Require.Artwork();
        var mission = NewStartedMission(2);
        var (a, b, _) = ThreeDistinct(mission);
        var side0 = mission.Sides[Side0];
        side0.VisibilityRange = 0f;

        var scout = NewShip(8110, Side0, b);
        mission.PlaceShip(scout, b);
        ReconProbe(scout, b);

        mission.RefreshSideVisibility();

        Assert.True(side0.VisibleClusters.Contains(b.Id), "refreshed set includes the probe's cluster");
    }
}
