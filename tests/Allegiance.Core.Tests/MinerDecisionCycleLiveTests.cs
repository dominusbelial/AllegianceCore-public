using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P156 — the miner decision cycle, live loopback proof of the first fully
/// autonomous, closed-loop economic behavior in this port. An idle
/// <see cref="IsNpc"/> ship spawned via <see cref="Srv.SpawnNpcShip"/> in a
/// sector containing a real minable asteroid and a real unload-capable friendly
/// station autonomously: (1) targets the nearest mineable asteroid
/// (<see cref="Ship.MiningAsteroidTarget"/> set, <see cref="Ship.PlanTarget"/>
/// kept null — mutual exclusion), travels to it, and mines it via the
/// pre-existing <see cref="Model.Sector.ResolveMining"/> (this task only moves
/// the ship into range; it does not build mining); (2) once the bag is full
/// (<see cref="Ship.Ore"/> &gt;= <see cref="Ship.MineCapacity"/>), clears the
/// stale mining target and retargets the nearest unload-capable friendly
/// station (<see cref="Ship.PlanTarget"/> set via the reused P149
/// <see cref="Ship.FindNearestFriendlyStation"/>); (3) travels there, docks,
/// and sells via the pre-existing <c>ServerHost</c> dock-sell path, crediting
/// the owning side's treasury by <c>ore × <see cref="Ship.OreCreditPerUnit"/></c>.
///
/// The harness mirrors P147/P155's live tests (booted in-process server + one
/// seated bot, no Windows VM); the mission is advanced with the server's own
/// deterministic <see cref="Srv.TickSim"/> (one 0.05 s chunk each).
/// </summary>
/// <remarks>
/// <para><b>Money accounting.</b> The ship sells a full bag
/// (<c>Ore == EffectiveMineCapacity</c> — the loaded core's
/// <c>FloatConstants[7]</c> × the side's GA, P477/P528, 90 × GA for PCore001),
/// so the expected treasury credit is <c>(long)(ore ×
/// EffectiveOreCreditPerUnit())</c>. The NPC and its station sit on
/// the <b>other</b> side from the one real player (the bootstrapping bot), so
/// the payday distribution finds zero players on the NPC's side and
/// <c>Server.OnPayday</c> (Server.cs:7010-7014) keeps the accrued proceeds in
/// that side's treasury — letting the assertion read the credit directly. The
/// assertion samples <c>side.Money</c> tick-by-tick and keys the expected credit
/// off the single tick where the ship's ore drops from positive to zero (the
/// dock-sell transition), isolating the sale's own credit from the mission's
/// periodic 60 s payday (<see cref="Model.Mission.SecondsBetweenPaydays"/>, which
/// also credits <c>side.Money</c>). The credit is asserted to be AT LEAST the
/// full expected proceeds; any larger excess would be a coincident periodic
/// payday and is not claimed by this test. In practice, with the cycle completing
/// well under one payday period, the sampled single-tick delta equals the
/// expected proceeds exactly.</para>
/// </remarks>
[Collection("LiveNetwork")]
public class MinerDecisionCycleLiveTests
{
    protected virtual bool NativeGotoPlanEnabled => false;
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private Cfg Config(int port = 0) => new()
    {
        Sim = new() { NativeGotoPlan = NativeGotoPlanEnabled },
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P156 Miner Cycle Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 98700000000000001ul, DrmHash = "p156miner",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // He3-capable rock (bit CapMineHe3, igc.h:761) — the same synthetic builder
    // MiningTests/MiningSellTests use, so ResolveMining treats it as a real pool.
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId, float hitPoints = 0f) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: oreMax, OreMax: oreMax,
        AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: hitPoints, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static float DistToRock(Ship s, Asteroid rock) =>
        (new Vector3(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]) - s.Position).Length();

    /// <summary>⭐ End-to-end: an idle NPC autonomously mines a full bag and
    /// sells it at a friendly station, crediting the side's treasury by the
    /// expected proceeds — the first fully autonomous economic loop in this port.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Idle_npc_autonomously_mines_full_bag_and_sells_at_friendly_station()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0;                 // the NPC's side (no players → treasury keeps the credit)
        Asteroid? rock = null;
        DataStationIGC station = default;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            // Friendly unload-capable station (real type from the core carrying
            // the SabmUnload bit) on the NPC's own side so it can dock & sell.
            var unloadType = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmUnload) != 0);
            station = new DataStationIGC(
                Position: [446f, 908f, 461f], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: npcSide, StationId: 8861,
                StationTypeId: (short)unloadType.StationTypeId, BpHull: 1, BpShield: 1, Name: "SELL");
            m.PlaceStation(new Station(station, unloadType), sector);

            // Minable He3 rock (XXV_simple has zero map asteroids, so this is the
            // only candidate ResolveMining / the NPC's search can see).
            rock = new Asteroid(MkAsteroid(7761, new Vector3(755f, 859f, 502f), oreMax: 500f, radius: 60, sector.Id));
            m.PlaceAsteroid(rock, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no initial miner to perturb precise assertions
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p156_miner");
        bot.SendLogonRequest(Request("p156_miner"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission);
        Assert.NotNull(rock);

        var side = mission!.Sides.First(s => s.Id == npcSide);
        var spawnPos = new Vector3(919f, 833f, 471f);

        // Spawn an idle NPC far enough from the rock (mining threshold ≈ rock
        // radius 60 + hull radius + padding 30 ≈ ≤115 u) that it MUST travel to
        // mine — proving the decision cycle's target, not luck.
        var npc = server.SpawnNpcShip(mission, sector!, npcSide, spawnPos);
        npc.MiningRateOverridePerSecond = 100f;   // P528 seam: fast native-scale fill for the live decision-cycle proof
        Assert.True(DistToRock(npc, rock!) > 140f, "NPC must spawn beyond mining range to prove travel");

        // ── Phase 1: idle NPC autonomously targets the mineable asteroid ────
        Pump(server, () => npc.MiningAsteroidTarget is not null, 600);
        Assert.NotNull(npc.MiningAsteroidTarget);
        Assert.Null(npc.PlanTarget);                    // mutual exclusion: mining XOR selling

        // … travels to it and begins mining (ResolveMining — pre-existing).
        Pump(server, () => npc.Ore >= 1f, 20000);
        Assert.True(npc.Ore >= 1f, $"NPC should have begun mining, got ore {npc.Ore}");
        float dRock0 = DistToRock(npc, rock!);
        float travelled = (spawnPos - npc.Position).Length();
        Assert.True(dRock0 < 140f, $"NPC must have flown to the rock: dist {dRock0:F1}");
        Assert.True(travelled > 40f, $"NPC must have physically moved from spawn: travelled {travelled:F1}");

        // … fills the bag to capacity.
        float cap = npc.EffectiveMineCapacity();   // P528: native/core capacity (was the flat 150)
        Pump(server, () => npc.Ore >= cap, 30000);
        Assert.True(npc.Ore >= cap - 0.5f, $"bag should be full, got {npc.Ore} (cap {cap})");

        // ── Phase 2: full bag → retarget the unload-capable sell station ─────
        Pump(server, () => npc.PlanTarget is not null && npc.MiningAsteroidTarget is null, 600);
        Assert.NotNull(npc.PlanTarget);
        Assert.Null(npc.MiningAsteroidTarget);          // mutual exclusion: mining XOR selling
        Assert.Equal(station.StationId, npc.PlanTarget.Value.StationId);

        // ── Phase 3: travel, dock, sell — credit side treasury by ore×rate ───
        long prevMoney = side.Money;
        float prevOre = npc.Ore;
        bool sold = false;
        int ti = 0;
        while (ti < 20000 && !sold)
        {
            server.TickSim();
            if (npc.Ore <= 0f && prevOre > 0f && npc.StationId is not null)
            {
                sold = true;
                long expected = (long)(npc.EffectiveOreCreditPerUnit() * prevOre);   // P528: native/core value × GA
                long delta = side.Money - prevMoney;
                Assert.True(delta >= expected,
                    $"sell must credit the treasury: expected ≥{expected}, sampled single-tick delta {delta}");
                Assert.Equal(0f, npc.Ore);
                Assert.NotNull(npc.StationId);
                Console.WriteLine($"P156-E2E: autonomous mine→sell round complete: sold {prevOre:F1} ore " +
                                  $"(capacity {cap}) at {npc.EffectiveOreCreditPerUnit():F1}/u = {expected} credits, " +
                                  $"sampled side-money delta {delta}; NPC docked at {npc.StationId}");
            }
            prevMoney = side.Money;
            prevOre = npc.Ore;
            ti++;
        }
        Assert.True(sold, "NPC should have docked and sold within the tick budget");
        Assert.True(side.Money > 0, "side treasury should have grown");
    }

    /// <summary>⭐ P386 — regression guard for the dock-reset fix. The single-cycle
    /// P156 test above stops at the first dock/sell; a stale PlanTarget left set
    /// to the sell station would let it pass while the ship degrades into undock →
    /// instant-redock on EVERY later cycle (exposed by P381's auto-undock). This
    /// runs the FULL autonomous loop at least 3 times — undock → travel → mine →
    /// travel → dock → sell → (P381 auto-undock) → … — and after each dock asserts
    /// the just-consumed plan slot is cleared (PlanTarget null, AcceptedTarget
    /// null) so the next cycle re-decides instead of re-docking in place.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Idle_npc_repeats_mine_sell_cycle_indefinitely_after_first_sell()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0;
        Asteroid? rock = null;
        DataStationIGC station = default;
        var stationPos = new Vector3(446f, 908f, 461f);
        var rockPos = new Vector3(755f, 859f, 502f);

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            var unloadType = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmUnload) != 0);
            station = new DataStationIGC(
                Position: [stationPos.X, stationPos.Y, stationPos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: npcSide, StationId: 8011,
                StationTypeId: (short)unloadType.StationTypeId, BpHull: 1, BpShield: 1, Name: "CYCLE");
            m.PlaceStation(new Station(station, unloadType), sector);

            // 5000-ore rock: three full bags (3×150) never starve it even without
            // the per-tick Regenerate refill. Generous HitPoints so the P254
            // departure graze (bag full, mining target cleared) cannot destroy it
            // mid-run — a real map rock has real HP; the default-HitPoints:0
            // synthetic rocks used by the single-cycle tests die on any hit.
            rock = new Asteroid(MkAsteroid(7711, rockPos, oreMax: 5000f, radius: 60, sector.Id, hitPoints: 100000f));
            m.PlaceAsteroid(rock, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.SpawnInitialMinersAtBoot = false;   // P382: pristine arena — no initial miner to perturb
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;   // bypass the ready-up lobby (P180)
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p386_cyc");
        bot.SendLogonRequest(Request("p386_cyc"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission);
        Assert.NotNull(rock);

        var npc = server.SpawnNpcShip(mission!, sector!, npcSide, new Vector3(909f, 793f, 439f));
        npc.MiningRateOverridePerSecond = 100f;   // P528 seam: fast native-scale fill so 3 cycles complete quickly
        Assert.True((npc.Position - rockPos).Length() > 140f, "NPC must spawn beyond mining range to prove travel");

        int sells = 0;
        const int MaxSells = 3;
        float cap = npc.EffectiveMineCapacity();   // P528: native/core capacity (was the flat 150)
        // Per-phase tick ceilings — generous (30k ticks = 1500 sim s) so travel +
        // the ~8s P75 launch cooldown + ~10s P381 docked cooldown per cycle never
        // strand a healthy run, yet a relapse (miner never re-mines) trips fast.
        while (sells < MaxSells)
        {
            // (a) reach the rock and start accruing ore. Cycle 1 begins undocked at
            //     spawn; later cycles begin docked, are released by P381's
            //     auto-undock, and the (now PlanTarget-clear) cycle re-picks the rock.
            int pa = Pump(server, () => npc.Ore >= 1f, 30000);
            Assert.True(npc.Ore >= 1f, $"cycle {sells + 1}: miner should have begun mining (sells so far {sells})");
            Assert.Null(npc.PlanTarget);                    // mining XOR selling — no stale sell plan

            // (b) fill the bag.
            int pb = Pump(server, () => npc.Ore >= cap, 30000);
            Assert.True(npc.Ore >= cap - 0.5f, $"cycle {sells + 1}: bag should fill (sells {sells})");

            // (c) travel to the station, dock, sell (ore → 0 at the dock transition).
            float prevOre = npc.Ore;
            bool soldCycle = false;
            int pc = 0;
            while (pc < 30000 && !soldCycle)
            {
                server.TickSim(); pc++;
                if (npc.Ore <= 0f && prevOre > 0f && npc.StationId is not null)
                {
                    soldCycle = true;
                    sells++;
                    // ⭐ P386 — the just-consumed return-to-sell plan slot MUST be
                    // cleared at the dock. This assertion is RED on the pristine
                    // (pre-fix) tree, where PlanTarget stays = the sell station.
                    Assert.Null(npc.PlanTarget);
                    Assert.Null(npc.AcceptedTarget);
                    Console.WriteLine($"P386-CYCLE {sells}/{MaxSells}: sold {prevOre:F1} ore; docked at {npc.StationId} — PlanTarget/AcceptedTarget cleared");
                }
                prevOre = npc.Ore;
            }
            Assert.True(soldCycle, $"cycle {sells + 1}: miner should have docked and sold (sells {sells})");

            // (d) release for the next cycle: P381 auto-undocks after ~10s docked.
            int pd = Pump(server, () => npc.StationId is null, 6000);
            Assert.Null(npc.StationId);
        }

        Assert.True(sells >= MaxSells, $"miner must repeat the full mine→sell cycle at least {MaxSells} times, got {sells}");
        Console.WriteLine($"P386-LIVE: miner repeated the full autonomous mine→sell cycle {sells} times");
    }

    /// <summary>⭐ P386 — the OTHER return-to-base slot (<see cref="Ship.AcceptedTarget"/>
    /// /<see cref="Ship.AcceptedCommandId"/>) must also be cleared at the dock. It is
    /// promoted into <see cref="Ship.PlanTarget"/> at Ship.cs:6001-6004 whenever the
    /// plan slot is empty; if it survived docking, the very next tick would re-promote
    /// it and recreate the infinite return-to-base loop even with the PlanTarget reset
    /// in place. Seeds a flying NPC with a pre-set AcceptedTarget (the aftermath of an
    /// ORDER_CHANGE return-to-base), lets it dock via the REAL
    /// Sector.ResolveDockCollisions → ServerHost.DockWithStationEvent path, and
    /// asserts BOTH slots (and their command ids) are cleared.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Accepted_return_to_base_slot_cleared_on_dock()
    {
        Require.Artwork();
        using var server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0;
        DataStationIGC station = default;
        var stationPos = new Vector3(396f, 788f, 391f);

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;

            var unloadType = core.StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmUnload) != 0);
            station = new DataStationIGC(
                Position: [stationPos.X, stationPos.Y, stationPos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
                Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: npcSide, StationId: 8021,
                StationTypeId: (short)unloadType.StationTypeId, BpHull: 1, BpShield: 1, Name: "RTB");
            m.PlaceStation(new Station(station, unloadType), sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, sector);
            return (ship, sector);
        };

        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "p386_rtb");
        bot.SendLogonRequest(Request("p386_rtb"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission);
        Assert.NotEqual(0, station.StationId);

        // Flying NPC parked at the station with a stale return-to-base order already
        // resolved onto it (the exact state ORDER_CHANGE leaves before it docks).
        var npc = server.SpawnNpcShip(mission!, sector!, npcSide, stationPos);
        npc.AcceptedTarget = station;
        npc.AcceptedCommandId = OrderChange.CidGoto;

        // Natural tick promotes Accepted → Plan and the dock pass latches the ship;
        // the (generous) ceiling absorbs the P75 8s launch cooldown for a fresh spawn.
        int pd = Pump(server, () => npc.StationId is not null, 8000);
        Assert.NotNull(npc.StationId);
        Assert.Null(npc.PlanTarget);               // ⭐ P386: cleared at the dock (RED on pristine)
        Assert.Null(npc.AcceptedTarget);           // ⭐ P386: the return-to-base slot also cleared
        Assert.Equal(OrderChange.CidNone, npc.PlanCommandId);
        Assert.Equal(OrderChange.CidNone, npc.AcceptedCommandId);
        Console.WriteLine($"P386-RTB: flying NPC docked at {npc.StationId}; PlanTarget + AcceptedTarget cleared (CidNone)");
    }
}

/// <summary>P817: unchanged economy/construction assertions with native travel enabled.</summary>
public sealed class P817NativeMinerDecisionCycleLiveTests : MinerDecisionCycleLiveTests
{
    protected override bool NativeGotoPlanEnabled => true;
}
