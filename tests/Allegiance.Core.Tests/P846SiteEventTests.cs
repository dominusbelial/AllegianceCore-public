using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using PlayerScore = global::Allegiance.Server.PlayerScore;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P846 — certification tests for the <c>FedSrvSiteBase</c> site-event batch
/// (kill / damage / mining). Each test pins one native branch against its port:
/// <list type="number">
/// <item><c>TerminateModelEvent</c> (<c>FedSrv.CPP:11630-11632</c>) — the native
/// server body is EMPTY, so terminating a model notifies the site and produces no
/// further effect (no kill/asteroid broadcast).</item>
/// <item><c>KillShipEvent</c>'s partial-kill loop
/// (<c>FedSrv.CPP:11826-11861</c>) — every enemy damage bucket earns its damage
/// share of the victim-class counter, not only the credited killer.</item>
/// <item><c>DrainAsteroidEvent</c> (<c>FedSrv.CPP:12443-12452</c>) — its only
/// native caller is commented out (<c>asteroidigc.h:228-231</c>), so tapping the
/// last ore zeroes the rock and must NOT emit the event/announcement.</item>
/// <item><c>KillTreasureEvent</c>'s <c>STAGE_STARTED</c> gate
/// (<c>FedSrv.CPP:12495-12509</c>) — a treasure expiring before the mission is
/// STARTED is still terminated but never announced.</item>
/// </list>
/// The two live tests boot a real in-process server over loopback TCP (the
/// established <c>[Collection("LiveNetwork")]</c> harness), the unit tests use a
/// <see cref="RecordingIgcSite"/> mission.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P846SiteEventTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // ── shared corpus helpers (mirroring MiningTests / KillCreditDamageTrackTests) ──

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
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap.Value,
            [new SideSpec(0, "Team 1", civ), new SideSpec(1, "Team 2", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId) =>
        new(Signature: 1.0f,
            Position: [pos.X, pos.Y, pos.Z],
            Up: [0f, 1f, 0f],
            Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f],
            Ore: oreMax,
            OreMax: oreMax,
            AabmCapabilities: Asteroid.CapMineHe3,
            AsteroidId: id,
            HitPoints: 0f,
            Radius: radius,
            ModelName: "roid.mdl",
            TextureName: "",
            IconName: "",
            ClusterId: sectorId,
            Name: $"ROID_{id}",
            Fraction: 0);

    // ── 1. TerminateModelEvent — the native FedSrv body is empty ──────────────

    /// <summary>⭐ Native <c>FedSrvSiteBase::TerminateModelEvent</c> is an empty
    /// body (<c>FedSrv.CPP:11630-11632</c>): the server site does nothing when a
    /// model terminates. The port raises the seam once
    /// (<c>CmodelIGC::Terminate</c>, <c>modelIGC.cpp:55-61</c>) and performs no
    /// kill/asteroid/destruction announcement of its own.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Terminating_a_model_notifies_the_site_once_and_nothing_else()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors.First();
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(9100, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);

        Assert.True(sector.Terminate(ship), "the model must still be present to terminate");

        var terminations = recording.Calls.Where(c => c.StartsWith("TerminateModelEvent(")).ToArray();
        Assert.Single(terminations);
        // The native FedSrv body has no side effects beyond the callback itself.
        Assert.DoesNotContain(recording.Calls, c => c.StartsWith("KillShipEvent("));
        Assert.DoesNotContain(recording.Calls, c => c.StartsWith("KillAsteroidEvent("));
        Console.WriteLine($"P846-TERMINATE: {terminations[0]} (and no destruction announcement)");
    }

    // ── 2. KillShipEvent partial-kill shares ──────────────────────────────────

    private static readonly Lazy<(DataHullTypeIGC Hull, byte DamageType, float Dtm)> MinerCell = new(() =>
    {
        var core = RepresentativeCore.Value;
        var hull = core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmMiner) && h.HitPoints > 0);
        for (byte dmg = 0; dmg < 20; dmg++)
        {
            float cell = core.Constants.DamageConstants[dmg][hull.DefenseType];
            if (cell > 0.01f) return (hull, dmg, cell);
        }
        throw new InvalidOperationException("no positive damage cell for the miner hull");
    });

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P846 Site Events", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 846u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 84600000000000000ul, DrmHash = "p846siteevent",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship A = null!, B = null!;
        public FltBot ABot = null!, BBot = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            ABot?.Dispose();
            BBot?.Dispose();
            Server?.Dispose();
        }
    }

    /// <summary>Boot a 2-bot / 2-side live server; slot 0 seats side A, slot 1
    /// side B. The mission is forced to <see cref="MissionStage.Started"/>.</summary>
    private static Seated SeatTwo()
    {
        var seat = new Seated();
        seat.Server = new Srv();
        seat.Server.SpawnInitialMinersAtBoot = false;
        int placed = 0;
        var hull = RepresentativeCore.Value.HullTypes.Values.OrderBy(h => h.HitPoints).First();
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            var ship = new Ship(id, hull, new Vector3(0f, 0f, placed * 200f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var m in seat.Server.Missions) { m.Stage = MissionStage.Started; seat.Mission = m; }

        seat.ABot = new FltBot();
        seat.BBot = new FltBot();
        try
        {
            seat.ABot.Connect(seat.Server.BoundPort, "p846_a"); seat.ABot.SendLogonRequest(Request("p846_a"));
            seat.BBot.Connect(seat.Server.BoundPort, "p846_b"); seat.BBot.SendLogonRequest(Request("p846_b"));
            int got = PumpUntil(seat.Server, () => seat.ABot.ShipAssignment is not null && seat.BBot.ShipAssignment is not null, 4000);
            Assert.True(got < 4000, "both peers must seat");
            seat.A = seat.Server.Ships.First(s => s.Ship.Id == seat.ABot.ShipAssignment!.Ship.ShipId).Ship;
            seat.B = seat.Server.Ships.First(s => s.Ship.Id == seat.BBot.ShipAssignment!.Ship.ShipId).Ship;
            Assert.NotEqual(seat.A.SideId, seat.B.SideId);   // slot 0/1 round-robin
            return seat;
        }
        catch
        {
            seat.Dispose();
            throw;
        }
    }

    private static PlayerScore ScoreOf(Srv server, Ship ship) =>
        server.Ships.First(s => ReferenceEquals(s.Ship, ship)).PlayerScore;

    /// <summary>⭐ Native <c>KillShipEvent</c>'s partial-kill loop
    /// (<c>FedSrv.CPP:11826-11861</c>): when a ship dies, native sums every
    /// damage bucket whose side differs from the victim's and calls
    /// <c>KillShip(pship, bucketTotal / totalDamage)</c> for EACH such bucket. The
    /// credited killer therefore gets its damage SHARE of the victim-class
    /// counter, and the second enemy contributor gets its own share. Pre-P846 the
    /// port credited only the top dealer with a flat 1.0 (and the helper with 0),
    /// so these assertions fail on that code.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void KillShip_splits_victim_class_credit_by_damage_share()
    {
        Require.Artwork();
        using var seat = SeatTwo();
        var (hull, dmg, dtm) = MinerCell.Value;

        // Both peers attack the same victim: force B onto A's side so the victim
        // (opposite side) has two enemy contributors.
        seat.B.SideId = seat.A.SideId;
        short victimSide = seat.Mission.Sides.First(s => s.Id != seat.A.SideId).Id;
        var victim = new Ship(9200, hull, seat.A.Position + new Vector3(0f, 0f, -60f), Vector3.Zero, Orientation.Identity)
        { SideId = victimSide };
        seat.Mission.PlaceShip(victim, seat.Sector);

        float maxHP = victim.EffectiveMaxHitPoints();
        float amountFor(float fraction) => fraction * maxHP / dtm;
        float aDamage = amountFor(0.60f);
        float bDamage = amountFor(0.50f);

        // A: 60% (survivable). B: the 50% killing blow. A stays the top dealer.
        var first = victim.ReceiveDamage(dmg, aDamage, seat.Mission.LastUpdate, victim.Position, seat.A.Position, seat.A);
        Assert.NotEqual(DamageResult.Killed, first);
        var kill = victim.ReceiveDamage(dmg, bDamage, seat.Mission.LastUpdate, victim.Position, seat.B.Position, seat.B);
        Assert.Equal(DamageResult.Killed, kill);

        float total = aDamage + bDamage;
        float aShare = aDamage / total, bShare = bDamage / total;
        var aScore = ScoreOf(seat.Server, seat.A);
        var bScore = ScoreOf(seat.Server, seat.B);

        Assert.Equal(1, aScore.Kills);          // AddKill only for the credited (top) dealer
        Assert.Equal(0, bScore.Kills);
        Assert.Equal(aShare, aScore.MinerKills, precision: 3);
        Assert.Equal(bShare, bScore.MinerKills, precision: 3);
        Console.WriteLine($"P846-SHARE: A {aScore.MinerKills:0.###} + B {bScore.MinerKills:0.###} miner kills (shares of {(aDamage):F0}/{(bDamage):F0} damage)");
    }

    // ── 3. DrainAsteroidEvent — native's only caller is commented out ─────────

    /// <summary>⭐ Native <c>MineOre</c> zeroes the rock when the request exceeds
    /// what is left and the <c>DrainAsteroidEvent</c> call beside it is COMMENTED
    /// OUT (<c>asteroidigc.h:228-231</c>, "NYI -- re-enable if He3 does not
    /// regenerate"). The body at <c>FedSrv.CPP:12443-12452</c> (whole-mission
    /// <c>ASTEROID_DRAINED</c> + <c>SetOre(0)</c>) is therefore unreachable, so the
    /// port must zero the ore WITHOUT emitting the event.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Tapping_the_last_ore_zeroes_the_rock_without_DrainAsteroidEvent()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors.First();
        var spot = new Vector3(2000f, 2000f, 2000f);
        var rock = new Asteroid(MkAsteroid(7004, spot, oreMax: 20f, radius: 60, sectorId: sector.Id));
        mission.PlaceAsteroid(rock, sector);

        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var ship = new Ship(9201, hull, spot, Vector3.Zero, Orientation.Identity);
        ship.MiningRateOverridePerSecond = 100f;   // P528 test-only rate seam
        ship.MineCapacityOverrideUnits = 150f;
        mission.PlaceShip(ship, sector);

        for (int i = 0; i < 20; i++) mission.Update(mission.LastUpdate + 0.25);
        Assert.True(rock.OreRemaining < 5f, $"the small rock must be tapped, got {rock.OreRemaining}");

        Assert.DoesNotContain(recording.Calls, c => c.StartsWith("DrainAsteroidEvent("));
        Assert.Contains(recording.Calls, c => c.StartsWith("MineAsteroidEvent("));   // the live sibling still fires
        Console.WriteLine($"P846-DRAIN: rock tapped to {rock.OreRemaining:0.##}; no DrainAsteroidEvent (native NYI)");
    }

    // ── 4. KillTreasureEvent STAGE_STARTED gate ───────────────────────────────

    private static DataTreasureIGC TreasureData(short objectId, short sectorId, float lifespan) => new(
        P0: [0f, 0f, 0f], V0: [0f, 0f, 0f],
        Lifespan: lifespan, Time0: 0u,
        ObjectId: objectId, TreasureId: 950, ClusterId: sectorId, Amount: 500,
        TreasureCode: TreasureCode.Cash, CreateNow: true);

    /// <summary>⭐ <c>FedSrvSiteBase::KillTreasureEvent</c>
    /// (<c>FedSrv.CPP:12495-12509</c>) announces <c>DESTROY_TREASURE</c> only while
    /// the mission stage is <c>STAGE_STARTED</c>; the <c>Terminate()</c> outside
    /// that block always runs. The port's stage gate was missing, so a treasure
    /// expiring during the pre-start countdown was announced to every peer even
    /// though native stays silent.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Treasure_expiry_before_STAGE_STARTED_terminates_silently()
    {
        Require.Artwork();
        using var seat = SeatTwo();
        var mission = seat.Mission;
        var sector = seat.Sector;

        // Pre-start countdown: sectors tick (MissionStage.Starting) but native's
        // KillTreasureEvent gate is STAGE_STARTED only.
        mission.Stage = MissionStage.Starting;
        var early = new Treasure(9301, sector, TreasureData(9301, sector.Id, lifespan: 0.5f), mission.LastUpdate, mission);
        sector.Add(early);
        seat.ABot.ResetDestroyTreasures();
        seat.BBot.ResetDestroyTreasures();
        int gone = PumpUntil(seat.Server, () => !sector.Models.OfType<Treasure>().Contains(early), 4000);
        Assert.True(gone < 4000, "the pre-start treasure must still expire (Terminate runs outside the native gate)");
        Assert.DoesNotContain(seat.ABot.DestroyTreasures, m => m.TreasureId == 9301);
        Assert.DoesNotContain(seat.BBot.DestroyTreasures, m => m.TreasureId == 9301);

        // Started: the same expiry IS announced whole-mission.
        mission.Stage = MissionStage.Started;
        var live = new Treasure(9302, sector, TreasureData(9302, sector.Id, lifespan: 0.5f), mission.LastUpdate, mission);
        sector.Add(live);
        seat.ABot.ResetDestroyTreasures();
        seat.BBot.ResetDestroyTreasures();
        int told = PumpUntil(seat.Server,
            () => seat.ABot.DestroyTreasures.Any(m => m.TreasureId == 9302)
               && seat.BBot.DestroyTreasures.Any(m => m.TreasureId == 9302), 4000);
        Assert.True(told < 4000, "a STAGE_STARTED expiry must broadcast DESTROY_TREASURE to both sides");
        Console.WriteLine("P846-TREASURE: pre-start expiry silent, started expiry announced (STAGE_STARTED gate)");
    }
}
