using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P549 — kill credit via the ported <see cref="DamageTrack"/>. Native credits a
/// death to the launcher that dealt the MOST total damage — the first,
/// highest-sorted bucket of the victim's DamageTrack (<c>shipIGC.cpp:1319-1331</c>)
/// — <b>not</b> the last hitter. These tests drive a real
/// <see cref="Ship.ReceiveDamage"/> on a started mission and assert the
/// <see cref="IIgcSite.KillShipEvent"/> launcher is the top damage dealer, plus a
/// direct unit proof of the track's sort/decay semantics.
/// </summary>
public sealed class KillCreditDamageTrackTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            if (core.HullTypes.Count > 0) return core;
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

    /// <summary>A hull with real hit-points and a damage cell &gt; 0 for the
    /// first non-trivial damage type, so the test's fractional amounts are
    /// computable and non-degenerate.</summary>
    private static readonly Lazy<(DataHullTypeIGC Hull, byte DamageType, float Dtm)> HullCell = new(() =>
    {
        var core = RepresentativeCore.Value;
        var hull = core.HullTypes.Values.Where(h => h.HitPoints >= 150f).OrderBy(h => h.HitPoints).First();
        for (byte dmg = 0; dmg < 20; dmg++)
        {
            float cell = core.Constants.DamageConstants[dmg][hull.DefenseType];
            if (cell > 0.01f) return (hull, dmg, cell);
        }
        throw new InvalidOperationException("no positive damage cell for the chosen hull");
    });

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(core, SomeMap.Value,
            [new SideSpec(0, "Team 1", civ.CivilizationId)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship Place(Mission m, Sector sector, short id, DataHullTypeIGC hull, short side)
    {
        var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = side };
        m.PlaceShip(ship, sector);
        return ship;
    }

    /// <summary>A tiny site that records ONLY the launcher passed to
    /// <see cref="IIgcSite.KillShipEvent"/> (plus whether it fired), delegating
    /// everything else to a <see cref="NullIgcSite"/> so the mission assembles
    /// and runs exactly as with any no-op site.</summary>
    private sealed class KillCaptureSite : IIgcSite
    {
        public readonly NullIgcSite Inner = new();
        public Sim.Model? LastKillLauncher;
        public int KillCount;
        public double KillNow;
        public void KillShipEvent(double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2)
        {
            KillCount++;
            KillNow = now;
            LastKillLauncher = launcher as Sim.Model;
        }
        public ThingSite CreateThingSite(IModelIgc pModel) => default!;
        public ClusterSite CreateClusterSite(IClusterIgc pCluster) => default!;
        // All other members are default-implemented on IIgcSite, so a real
        // mission runs identically to the no-op NullIgcSite.
    }

    // ══ 1 — the DamageTrack unit semantics (buckets, sort, decay) ════════════

    /// <summary>⭐ Direct unit proof of the track: <see cref="DamageTrack"/>
    /// accumulates a bucket per launcher and keeps them sorted descending so
    /// <see cref="DamageTrack.TopDealer"/> is always the highest total-damage
    /// model — even when the last-arriving damage came from a smaller dealer
    /// (the native sort, common.cpp:2624-2665).</summary>
    [Fact]
    public void DamageTrack_reports_the_top_total_damage_dealer_not_the_last_hitter()
    {
        var hull = HullCell.Value.Hull;
        var big = new Ship(901, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var small = new Ship(902, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        var track = new DamageTrack(now: 100.0);

        // Small dealer contributes LAST, but the big dealer leads the total.
        track.ApplyDamage(100.0, big, 80f);
        track.ApplyDamage(100.5, small, 30f);
        Assert.True(ReferenceEquals(track.TopDealer, big),
            $"top dealer must be the 80-damage model, got {(track.TopDealer as Ship)?.Id}");

        // Once the small dealer overtakes, it becomes the top dealer (sort keeps
        // the list ordered descending after every apply).
        track.ApplyDamage(101.0, small, 100f);
        Assert.True(ReferenceEquals(track.TopDealer, small),
            $"after overtaking, the small dealer must lead, got {(track.TopDealer as Ship)?.Id}");

        // Empty track → no dealer.
        var empty = new DamageTrack(0.0);
        Assert.Null(empty.TopDealer);
        Console.WriteLine("P549-DT: DamageTrack sorts buckets so TopDealer tracks the highest total-damage dealer (not the last hitter).");
    }

    // ══ 2 — integration: ReceiveDamage credits the top damage dealer ═══════

    /// <summary>⭐ The kill credit integration: attacker A deals a large
    /// non-lethal hit, attacker B lands the final (smaller) killing blow — the
    /// <see cref="IIgcSite.KillShipEvent"/> must be credited to A (top damage
    /// dealer), not B (last hitter). Before P549 the last hitter (B) was passed
    /// straight through, so this fails on the pre-fix code.</summary>
    [Fact]
    public void Kill_credit_goes_to_the_top_damage_dealer_not_the_last_hitter()
    {
        var site = new KillCaptureSite();
        var mission = NewStartedMission(site);
        var sector = mission.Sectors[0];
        var (hull, dmg, dtm) = HullCell.Value;

        var victim = Place(mission, sector, 701, hull, side: 2);
        var attackerA = Place(mission, sector, 702, hull, side: 1);   // top damage dealer
        var attackerB = Place(mission, sector, 703, hull, side: 1);   // last hitter (final, smaller blow)

        float maxHP = hull.HitPoints;
        float amountFor(float fraction) => fraction * maxHP / dtm;

        // A: a 60%-of-hull hit — survivable.
        var drA = victim.ReceiveDamage(dmg, amountFor(0.6f), mission.LastUpdate, victim.Position, attackerA.Position, attackerA);
        Assert.NotEqual(DamageResult.Killed, drA);
        Assert.True(!victim.Dead, "A's hit must leave the victim alive");

        // B: the final 50%-of-hull blow — combined 110%, kills.
        var drB = victim.ReceiveDamage(dmg, amountFor(0.5f), mission.LastUpdate, victim.Position, attackerB.Position, attackerB);
        Assert.Equal(DamageResult.Killed, drB);
        Assert.Equal(1, site.KillCount);

        // Credit goes to A (top total damage), not B (last hitter).
        Assert.True(ReferenceEquals(site.LastKillLauncher, attackerA),
            $"kill must be credited to the top damage dealer A, got {(site.LastKillLauncher as Ship)?.Id}");
        Console.WriteLine($"P549-CREDIT: A({amountFor(0.6f):F1}) + B({amountFor(0.5f):F1}) kill the victim; credit -> A (top dealer), not the last hitter B.");
    }

    /// <summary>⭐ Regression net — a single killer still receives the credit
    /// (the fallback path: a lone attacker is trivially the top dealer).</summary>
    [Fact]
    public void Lone_killer_still_gets_the_credit()
    {
        var site = new KillCaptureSite();
        var mission = NewStartedMission(site);
        var sector = mission.Sectors[0];
        var (hull, dmg, dtm) = HullCell.Value;

        var victim = Place(mission, sector, 801, hull, side: 2);
        var killer = Place(mission, sector, 802, hull, side: 1);
        float maxHP = hull.HitPoints;
        var dr = victim.ReceiveDamage(dmg, 1.5f * maxHP / dtm, mission.LastUpdate, victim.Position, killer.Position, killer);
        Assert.Equal(DamageResult.Killed, dr);
        Assert.True(ReferenceEquals(site.LastKillLauncher, killer), "the lone killer must be credited");
        Console.WriteLine("P549-CREDIT-SOLO: lone killer credited.");
    }
}
