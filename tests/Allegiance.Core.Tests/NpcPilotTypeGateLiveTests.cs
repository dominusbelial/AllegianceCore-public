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
/// P160 — the <see cref="Ship.PilotType"/> gate. An NPC's real pilot type
/// (derived by <see cref="Srv.SpawnNpcShip"/> from the spawned drone-type's
/// definition in <c>Mission.Core.DroneTypes</c>) decides whether the
/// miner-specific decision cycle in <see cref="Ship.OnUpdate"/> runs at all.
/// These live loopback tests prove the gate actually works, not just that it
/// compiles:
/// <list type="bullet">
/// <item>A <b>non-Miner</b> NPC (a real Wingman drone type from PCore001,
/// <c>PilotType == Wingman (2)</c>) spawned beside a minable He3 rock ticks for
/// well past the idle-miner target-pick latency and does <b>nothing</b>: no
/// <see cref="Ship.MiningAsteroidTarget"/>, no <see cref="Ship.PlanTarget"/>,
/// no movement, <see cref="Ship.Ore"/> stays 0 — the faithful stand-by
/// matching native's <c>PickDefaultOrder</c> fallthrough for a type with no
/// case (<c>Igc/shipIGC.h:2458-2459</c>: <c>fGaveOrder</c> stays false →
/// <c>SetCommand(c_cmdAccepted, NULL, c_cidNone)</c>).</item>
/// <item>A <b>Miner</b> NPC spawned identically (no drone-type id → the
/// <see cref="Srv.SpawnNpcShip"/> <see cref="Ship.PilotTypeMiner"/> fallback)
/// in the same setup <em>does</em> pick the rock — the positive control proving
/// the gate discriminates and is behavior-preserving for the miner case.</item>
/// </list>
///
/// The harness mirrors P156's live tests (booted in-process server + one seated
/// bot, no Windows VM); the mission advances with the server's own
/// deterministic <see cref="Srv.TickSim"/> (one 0.05 s chunk each).
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcPilotTypeGateLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

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
            new Gm { Name = "P160 Pilot Type Gate Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 993u, DwCookie = 0x993u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88700000000000001ul, DrmHash = "p160gate",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    // He3-capable rock (bit CapMineHe3, igc.h:761) — same synthetic builder the
    // P156 miner test uses, so a Miner NPC WOULD target it.
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, float oreMax, short radius, short sectorId) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: oreMax, OreMax: oreMax,
        AabmCapabilities: Asteroid.CapMineHe3,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    // Shared boot: one seeded player seats a ship into the mission, giving the
    // test its Mission/Sector handles; a minable rock is placed in the sector;
    // the real Wingman drone type id (PilotType==2) is captured from the core.
    // Returns (mission, sector, npcSide, wingmanDroneTypeId, rock).
    private static (Mission, Sector, short, short, Asteroid) Boot(out Srv server)
    {
        server = new Srv();
        Mission? mission = null;
        Sector? sector = null;
        short npcSide = 0;
        short wingDroneId = 0;
        Asteroid? rock = null;

        server.ShipSpawner = (_, m, id) =>
        {
            mission = m;
            sector = m.Sectors[0];
            var core = m.Core;
            short peerSide = m.Sides[0].Id;
            npcSide = m.Sides.First(s => s.Id != peerSide).Id;   // the OTHER side — no players

            // A REAL Wingman drone type straight from the core (PilotType==2,
            // e.g. PCore001's id 40 'Wingman'). Looked up dynamically so the
            // test doesn't hard-code a core-version-specific id.
            var wing = core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeWingman);
            wingDroneId = wing.Key;

            // Minable He3 rock (XXV_simple has zero map asteroids, so this is the
            // only candidate a Miner NPC's search can see).
            rock = new Asteroid(MkAsteroid(7861, new Vector3(660f, 850f, 505f), oreMax: 500f, radius: 60, sector.Id));
            m.PlaceAsteroid(rock, sector);

            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            // Park the seated bot in the LAST sector (out of the sector under
            // test) so it stays a live player keeping the mission Started under
            // P178 without becoming an enemy target for the Wingman NPC's
            // sector-local search — the same separate-sector pattern the other
            // Wingman live tests use for their negative control.
            var peerSector = m.Sectors[m.Sectors.Count - 1];
            var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = peerSide };
            m.PlaceShip(ship, peerSector);
            return (ship, peerSector);
        };

        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        // P178: keep the bot SEATED (no `using` → not disposed at Boot return)
        // so the mission stays Started — the server's win-by-elimination now
        // (correctly) ends a mission when its last player leaves. The server
        // closes this socket at teardown.
        var bot = new FltBot();
        bot.Connect(server.BoundPort, "p160_gate");
        bot.SendLogonRequest(Request("p160_gate"));
        Pump(server, () => mission is not null && bot.ShipAssignment is not null, 4000);
        Assert.NotNull(mission);
        Assert.NotNull(rock);
        Assert.True(wingDroneId != 0, "core must expose a Wingman pilot-type drone");

        return (mission!, sector!, npcSide, wingDroneId, rock!);
    }

    /// <summary>⭐ Negative: a real non-Miner (Wingman) NPC stands by — no target,
    /// no movement, no ore — even with a minable rock sitting in its sector.</summary>
    [Fact]
    public void Non_miner_idle_npc_stands_by_despite_minable_asteroid()
    {
        var (mission, sector, npcSide, wingDroneId, rock) = Boot(out var server);
        using var _ = server;

        // Spawn far enough from the rock that an idle MINER would have to travel
        // to pick it (mining threshold ≈ radius 60 + hull radius + padding ≈ ≤115u).
        var spawnPos = new Vector3(919f, 640f, 455f);
        var npc = server.SpawnNpcShip(mission, sector, npcSide, spawnPos, wingDroneId);
        Assert.Equal(Ship.PilotTypeWingman, npc.PilotType);   // gate premise: really a Wingman

        // Tick well past the idle-miner target-pick latency (P156's Phase 1 pumps
        // at most 600 ticks for an idle miner to pick a rock; 1000 is safely beyond).
        Pump(server, () => npc.MiningAsteroidTarget is not null, 1000);

        Assert.Null(npc.MiningAsteroidTarget);   // never chose to mine
        Assert.Null(npc.PlanTarget);             // no station trip either (fresh, undamaged → no take-cover)
        Assert.Equal(0f, npc.Ore);               // no ore accrued
        float travelled = (spawnPos - npc.Position).Length();
        Assert.True(travelled < 1f, $"non-miner must not move from spawn: travelled {travelled:F3}");
        Console.WriteLine($"P160-NEG: Wingman NPC ({npc.Id}, pilotType {npc.PilotType}) stood by " +
                          $"beside a minable rock: no target, ore {npc.Ore}, travelled {travelled:F3}");
    }

    /// <summary>⭐ Positive control: an identically-idle MINER NPC in the same
    /// setup still picks the rock — proving the gate is behavior-preserving for
    /// the miner case (the only case P156-P159 exercised).</summary>
    [Fact]
    public void Miner_idle_npc_still_picks_the_rock()
    {
        var (mission, sector, npcSide, _, _) = Boot(out var server);
        using var _ = server;

        // No drone-type id → SpawnNpcShip falls back to Ship.PilotTypeMiner,
        // exactly as every pre-P160 call site resolves.
        var spawnPos = new Vector3(921f, 644f, 452f);
        var npc = server.SpawnNpcShip(mission, sector, npcSide, spawnPos);
        Assert.Equal(Ship.PilotTypeMiner, npc.PilotType);

        Pump(server, () => npc.MiningAsteroidTarget is not null, 700);
        Assert.NotNull(npc.MiningAsteroidTarget);   // the miner decision cycle still runs

        // Target-set happens on the tick the decision fires, before any physics;
        // keep pumping so the ship actually travels toward the rock.
        Pump(server, () => (spawnPos - npc.Position).Length() > 40f, 6000);
        float travelled = (spawnPos - npc.Position).Length();
        Assert.True(travelled > 40f, $"miner must travel toward the rock: travelled {travelled:F1}");
        Console.WriteLine($"P160-POS: Miner NPC ({npc.Id}) picked rock {npc.MiningAsteroidTarget?.Data.AsteroidId} " +
                          $"and travelled {travelled:F1}");
    }
}
