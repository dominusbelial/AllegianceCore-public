using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

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
/// (<c>Ore == MineCapacity == 150</c>), so the expected treasury credit is
/// <c>(long)(150 × OreCreditPerUnit) = 750</c>. The NPC and its station sit on
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
public sealed class MinerDecisionCycleLiveTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
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
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
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
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: oreMax, OreMax: oreMax,
        AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static float DistToRock(Ship s, Asteroid rock) =>
        (new Vector3(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]) - s.Position).Length();

    /// <summary>⭐ End-to-end: an idle NPC autonomously mines a full bag and
    /// sells it at a friendly station, crediting the side's treasury by the
    /// expected proceeds — the first fully autonomous economic loop in this port.</summary>
    [Fact]
    public void Idle_npc_autonomously_mines_full_bag_and_sells_at_friendly_station()
    {
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

        server.Boot(Config());
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
        Pump(server, () => npc.Ore >= Ship.MineCapacity, 30000);
        Assert.True(npc.Ore >= Ship.MineCapacity - 0.5f, $"bag should be full, got {npc.Ore}");

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
                long expected = (long)(Ship.OreCreditPerUnit * prevOre);
                long delta = side.Money - prevMoney;
                Assert.True(delta >= expected,
                    $"sell must credit the treasury: expected ≥{expected}, sampled single-tick delta {delta}");
                Assert.Equal(0f, npc.Ore);
                Assert.NotNull(npc.StationId);
                Console.WriteLine($"P156-E2E: autonomous mine→sell round complete: sold {prevOre:F1} ore " +
                                  $"(capacity {Ship.MineCapacity}) at {Ship.OreCreditPerUnit}/u = {expected} credits, " +
                                  $"sampled side-money delta {delta}; NPC docked at {npc.StationId}");
            }
            prevMoney = side.Money;
            prevOre = npc.Ore;
            ti++;
        }
        Assert.True(sold, "NPC should have docked and sold within the tick budget");
        Assert.True(side.Money > 0, "side treasury should have grown");
    }
}
