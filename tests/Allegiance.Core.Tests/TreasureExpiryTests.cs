using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P586 — a treasure's lifetime expiry (<c>CtreasureIGC::Update</c> →
/// <c>KillTreasureEvent</c>, <c>treasureIGC.cpp:215-218</c>). Native drops a
/// killed ship's parts with a <b>60 s</b> lifespan and its ammo/fuel packs with
/// <b>30 s</b> (<c>FedSrv.CPP:11694,11709,11724</c>) and REMOVES each treasure
/// when <c>now ≥ time0 + lifespan</c> (broadcasting <c>DESTROY_TREASURE</c>).
/// The pre-P586 port's <c>Treasure.OnUpdate</c> was empty — no expiry — so
/// every drop lingered in its sector forever (Domingo: "at the end of combat it
/// ends with hundreds of tiny floating cubes representing the loot"). These
/// tests pin the expiry behaviour that fixes that pileup:
///   (1) a treasure whose sim time passes <c>ExpireAt</c> is removed from its
///       sector AND raises <see cref="IIgcSite.TreasureDestroyedEvent"/> (the
///       host seam the server wires to the whole-mission
///       <c>FM_S_DESTROY_TREASURE(30)</c> broadcast);
///   (2) a treasure still inside its lifetime is NOT expired;
///   (3) the <c>ExpireAt</c> bound is <c>construction-now + Lifespan</c>, the
///       port of <c>m_expire = time0 + lifespan</c> (treasureIGC.cpp:197).
/// Fails on pre-P586 code: <c>OnUpdate</c> was a no-op, so no removal and no
/// event fire, and there was no <c>ExpireAt</c>.
/// </summary>
public sealed class TreasureExpiryTests
{
    private sealed class CaptureSite : IIgcSite
    {
        public System.Collections.Generic.List<short> Destroyed { get; } = new();
        public void TreasureDestroyedEvent(Treasure t) => Destroyed.Add(t.Id);
    }

    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p => System.IO.Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        var file = IgcReader.ParseFile(path);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    private static Treasure PlaceTreasure(Mission m, Sector sector, short oid, double lifespan, double now)
    {
        var data = new DataTreasureIGC(
            P0: [0f, 0f, 0f], V0: [0f, 0f, 0f],
            Lifespan: (float)lifespan, Time0: 0u,
            ObjectId: oid, TreasureId: 900, ClusterId: sector.Id, Amount: 750,
            TreasureCode: TreasureCode.Cash, CreateNow: true);
        var t = new Treasure(oid, sector, data, now, m);
        sector.Add(t);
        return t;
    }

    [Fact]
    public void TreasurePastItsExpireAt_IsRemovedAndRaisesTreasureDestroyedEvent()
    {
        var site = new CaptureSite();
        var m = BuildStartedMission(site);
        var sector = m.Sectors.First();
        double baseNow = m.LastUpdate;

        var t = PlaceTreasure(m, sector, 7701, lifespan: 5.0, now: baseNow);
        Assert.Equal(baseNow + 5.0, t.ExpireAt, precision: 3);   // m_expire = time0 + lifespan
        Assert.Contains(t, sector.Models.OfType<Treasure>());

        // Advance well past the expiry instant: the treasure must be removed and
        // the host seam raised (so the server broadcasts DESTROY_TREASURE).
        m.Update(baseNow + 6.0);

        Assert.DoesNotContain(t, sector.Models.OfType<Treasure>());
        Assert.Contains(t.Id, site.Destroyed);
    }

    [Fact]
    public void TreasureWithinItsLifetime_IsNotExpired()
    {
        var m = BuildStartedMission(new CaptureSite());
        var sector = m.Sectors.First();
        double baseNow = m.LastUpdate;

        var t = PlaceTreasure(m, sector, 7711, lifespan: 5.0, now: baseNow);

        // Still comfortably inside the lifetime: treasure survives, no event.
        m.Update(baseNow + 2.0);

        Assert.Contains(t, sector.Models.OfType<Treasure>());
    }

    [Fact]
    public void ExpireAtIsConstructionNowPlusLifespan()
    {
        var m = BuildStartedMission(new CaptureSite());
        var sector = m.Sectors.First();
        double baseNow = m.LastUpdate;

        var t = PlaceTreasure(m, sector, 7721, lifespan: 30.0, now: baseNow);   // native ammo/fuel lifespan

        Assert.Equal(baseNow + 30.0, t.ExpireAt, precision: 3);
    }

    /// <summary>
    /// P586 item 6 measurement — the "hundreds of loot pickups left after
    /// combat" BEFORE/AFTER counts, measured against the real expiry code.
    /// Native drops a killed ship's parts with 60 s and its ammo/fuel with
    /// 30 s lifespans (FedSrv.CPP:11694,11709,11724) and removes each on
    /// expiry; the pre-P586 port never expired, so every drop persisted
    /// forever. Simulate a 15-ship field's loot (15 treasures: 10 parts @ 60 s
    /// + 5 ammo/fuel @ 30 s), tick 90 s, and assert the AFTER count (alive) is
    /// 0 and all 15 raised the destroy seam, versus the BEFORE count (all 15
    /// alive, pre-P586 behaviour) — the exact divergence this fix closes.
    /// </summary>
    [Fact]
    public void FifteenShipLootField_AllExpireByTheirNativeLifespans()
    {
        var site = new CaptureSite();
        var m = BuildStartedMission(site);
        var sector = m.Sectors.First();
        double baseNow = m.LastUpdate;

        // BEFORE (pre-P586): no expiry — every drop lingers.
        var all = new List<Treasure>();
        for (int i = 0; i < 15; i++)
        {
            double lifespan = i < 10 ? 60.0 : 30.0;   // 10 parts @60s, 5 ammo/fuel @30s
            all.Add(PlaceTreasure(m, sector, (short)(7800 + i), lifespan, baseNow));
        }
        int beforeCount = sector.Models.OfType<Treasure>().Count();
        Assert.Equal(15, beforeCount);   // BEFORE: all 15 alive (pileup)

        // AFTER (P586): advance past the longest lifespan — all expire.
        m.Update(baseNow + 90.0);

        int afterCount = sector.Models.OfType<Treasure>().Count();
        Assert.Equal(0, afterCount);                       // AFTER: nothing left
        Assert.Equal(15, site.Destroyed.Count);            // all 15 raised the destroy seam
        Assert.Equal(all.Select(t => t.Id).OrderBy(x => x),
                     site.Destroyed.OrderBy(x => x));
        Console.WriteLine($"[P586 loot] before={beforeCount} alive after 90s; after={afterCount} alive (15 expired via TreasureDestroyedEvent)");
    }

    private static Mission BuildStartedMission(IIgcSite site)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }
}
