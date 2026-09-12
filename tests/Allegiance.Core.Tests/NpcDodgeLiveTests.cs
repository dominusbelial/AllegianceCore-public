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
/// P168 — Stage 1 NPC collision avoidance (<c>Dodge</c>) live loopback proof,
/// via the real in-process server. Establishes the two behaviours the stage
/// promises:
///   1. A traveling NPC on a direct collision course with an asteroid in its
///      own sector VISIBLY STEERS AWAY — its closest approach to the rock stays
///      clear of the rock's surface (it does not plow through the centre) and,
///      once the threat is no longer imminent, it RESUMES progress toward its
///      real objective (the station it was heading to).
///   2. With no imminent obstacle — even when a non-threatening rock is present
///      in the same sector — the NPC's behaviour is UNCHANGED: it takes a
///      near-direct line to its target and arrives, proving the new universal
///      pre-empting check adds zero behaviour change when nothing is imminent.
/// </summary>
[Collection("LiveNetwork")]
public sealed class NpcDodgeLiveTests
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
            new Gm { Name = "NPC Dodge Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 994u, DwCookie = 0x994u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99100000000000009ul, DrmHash = "p168dodge",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static DataStationIGC Target(Sector sector, short side, short id, float x, float y, float z)
    {
        var type = Core().StationTypes.Values.First(t => (t.SabmCapabilities & DataStationTypeIGC.SabmLand) != 0);
        return new DataStationIGC(
            Position: [x, y, z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sector.Id, SideId: side, StationId: id,
            StationTypeId: (short)type.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
    }

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 100f, OreMax: 100f,
        AabmCapabilities: 0,           // no mining capability needed — it is a pure obstacle here
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    private static float DistToPoint(Ship s, Vector3 p) => (p - s.Position).Length();

    /// <summary>Signed perpendicular distance of <paramref name="pt"/> from the
    /// infinite line through <paramref name="a"/>→<paramref name="b"/>.</summary>
    private static float LineDeviation(Vector3 pt, Vector3 a, Vector3 b)
    {
        Vector3 ab = b - a;
        float len = ab.Length();
        if (len < 1e-4f) return (pt - a).Length();
        return Vector3.CrossProduct(ab, pt - a).Length() / len;
    }

    /// <summary>Boot the real server; the ShipSpawner captures the mission and
    /// first sector, and a real bot connection forces the spawner to run (the
    /// same harness MinerDecisionCycle / NpcAutopilotLiveTests use).</summary>
    private sealed class Seat : IDisposable
    {
        public Srv Server = null!;
        public Mission Mission = null!;
        public Sector Sector = null!;
        public short PeerSide;
        public FltBot Bot = null!;

        public static Seat New()
        {
            var seat = new Seat();
            seat.Server = new Srv();
            seat.Server.ShipSpawner = (_, m, id) =>
            {
                seat.Mission = m;
                seat.Sector = m.Sectors[0];
                seat.PeerSide = m.Sides[0].Id;
                var hull = m.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
                var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = seat.PeerSide };
                m.PlaceShip(ship, seat.Sector);
                return (ship, seat.Sector);
            };
            seat.Server.Boot(Config());
            var bot = new FltBot();
            try
            {
                bot.Connect(seat.Server.BoundPort, "p168_dodge");
                bot.SendLogonRequest(Request("p168_dodge"));
                Pump(seat.Server, () => bot.ShipAssignment is not null, 4000);
                Assert.True(bot.ShipAssignment is not null, "bot must be assigned a ship");
                seat.Bot = bot;
                return seat;
            }
            catch
            {
                bot.Dispose();
                seat.Server.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            Bot?.Dispose();
            Server?.Dispose();
        }
    }

    /// <summary>⭐ Scope-4 obstacle divergence. A Miner NPC with a same-sector
    /// station PlanTarget flies toward it; a static asteroid sits dead on the
    /// straight line between spawn and station. The new dodge check makes it
    /// VISIBLY steer away — its closest approach to the rock stays clear of the
    /// rock's surface (rather than plowing through the centre) — and once the
    /// rock is no longer imminent it RESUMES toward the station.</summary>
    [Fact]
    public void Traveling_npc_steers_around_an_asteroid_on_its_path_and_resumes()
    {
        using var seat = Seat.New();
        var sector = seat.Sector;
        var npcSide = seat.PeerSide;   // same side as the peer so the peer is a friendly-skip, leaving ONLY the rock as an obstacle

        var a = new Vector3(980f, 932f, 956f);          // spawn
        var b = new Vector3(160f, 989f, 998f);          // station target
        var mid = new Vector3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

        var station = Target(sector, npcSide, id: 4411, x: b.X, y: b.Y, z: b.Z);
        var rock = new Asteroid(MkAsteroid(4412, mid, radius: 60, sectorId: sector.Id));
        seat.Mission.PlaceAsteroid(rock, sector);

        var npc = seat.Server.SpawnNpcShip(seat.Mission, sector, npcSide, a);
        npc.PlanTarget = station;
        npc.PlanCommandId = OrderChange.CidGoto;

        float shipHalfLen = 0.5f * npc.HullType.Length;
        float accel = npc.Mass > 0 ? npc.HullType.Thrust / npc.Mass : 0f;
        Vector3 rockCenter = new(rock.Data.Position[0], rock.Data.Position[1], rock.Data.Position[2]);

        Assert.True((a - mid).Length() > 300f, "spawn must be well short of the rock so there is real travel");
        Assert.True(DistToPoint(npc, rockCenter) > 300f, "must start beyond the obstacle");
        // Self-documenting: the rock sits dead on the straight line A→B, so a
        // straight flight WOULD plow through its centre (deviation ~0) — any
        // large measured clearance is therefore genuine deflection by the dodge.
        Assert.True(LineDeviation(mid, a, b) < 5f, "rock must be on the straight line A→B for the test to be meaningful");

        float minRockDist = float.MaxValue;
        float minStationDist = float.MaxValue;
        for (int i = 0; i < 12000; i++)
        {
            seat.Server.TickSim();
            minRockDist = MathF.Min(minRockDist, DistToPoint(npc, rockCenter));
            minStationDist = MathF.Min(minStationDist, DistToPoint(npc, new Vector3(b.X, b.Y, b.Z)));
        }
        float finalStation = DistToPoint(npc, new Vector3(b.X, b.Y, b.Z));

        Console.WriteLine($"P168-DODGE: accel={accel:F1} shipHalfLen={shipHalfLen:F1} rockR=60 " +
                          $"minRockDist={minRockDist:F1} minStationDist={minStationDist:F1} finalStation={finalStation:F1}");

        // Did not plow through the rock: closest approach to the CENTRE stays
        // comfortably outside the rock's own surface (which is at 60 from centre).
        Assert.True(minRockDist > 60f + shipHalfLen + 10f,
            $"must keep clear of the rock, not plow through: minRockDist={minRockDist:F1}");
        Assert.True(minRockDist > 60f, "must not reach the rock's centre at all");

        // Resumed progress after clearing the obstacle: still closing on / reached the station.
        Assert.True(finalStation < 300f,
            $"must resume toward the station after clearing the rock: finalStation={finalStation:F1}");
        Console.WriteLine("P168-DODGE-RESUME: NPC rounded the asteroid and continued to its station objective.");
    }

    /// <summary>⭐ Scope-4 no-obstacle unaffected. A Miner NPC with a same-sector
    /// station PlanTarget and a NON-threatening rock present in the same sector
    /// (well off the straight line, so never on a collision course) takes a
    /// near-direct line to the station and arrives — proving the universal dodge
    /// check adds zero behaviour change when nothing is imminent.</summary>
    [Fact]
    public void Traveling_npc_with_no_imminent_obstacle_is_unaffected_and_arrives()
    {
        using var seat = Seat.New();
        var sector = seat.Sector;
        var npcSide = seat.PeerSide;

        var a = new Vector3(948f, 926f, 953f);
        var b = new Vector3(250f, 983f, 981f);

        var station = Target(sector, npcSide, id: 4451, x: b.X, y: b.Y, z: b.Z);
        // A rock IS in the same sector (so the dodge scan has candidates) but far
        // off the A→B line — never an imminent collision.
        var farRock = new Asteroid(MkAsteroid(4452, new Vector3(720f, 530f, 870f), radius: 60, sectorId: sector.Id));
        seat.Mission.PlaceAsteroid(farRock, sector);

        var npc = seat.Server.SpawnNpcShip(seat.Mission, sector, npcSide, a);
        npc.PlanTarget = station;
        npc.PlanCommandId = OrderChange.CidGoto;

        float maxDev = 0f;
        for (int i = 0; i < 7000; i++)
        {
            seat.Server.TickSim();
            maxDev = MathF.Max(maxDev, LineDeviation(npc.Position, a, b));
        }
        float finalStation = DistToPoint(npc, new Vector3(b.X, b.Y, b.Z));

        Console.WriteLine($"P168-NOOBSTACLE: maxLineDeviation={maxDev:F1} finalStation={finalStation:F1}");

        Assert.True(finalStation < 220f,
            $"must arrive near the station with no obstacle: finalStation={finalStation:F1}");
        Assert.True(maxDev < 200f,
            $"no imminent obstacle → near-direct line (no dodge detour): maxLineDeviation={maxDev:F1}");
        Console.WriteLine("P168-NOOBSTACLE-OK: NPC flew a near-direct line to its target; dodge added nothing.");
    }

    /// <summary>P168 fixup — a SAME-SIDE ship parked dead on the path IS dodged.
    /// Native's real <c>Ignore()</c> (<c>Igc/common.cpp:1642-1690</c>), read
    /// closely: a drone does <em>not</em> generally ignore its own side (only a
    /// narrow symmetric tie-break does, not ported — an accepted, stated
    /// simplification) — same-side ships are real obstacles a drone must avoid
    /// (formations/escorts). The obstacle here is a plain non-NPC <see cref="Ship"/>
    /// placed directly (never runs any decision cycle, so it truly stays put) —
    /// not a spawned Miner, which would immediately start hunting for asteroids
    /// on its own and defeat the "stationary obstacle" setup.</summary>
    [Fact]
    public void Traveling_npc_steers_around_a_same_side_ship_on_its_path_and_resumes()
    {
        using var seat = Seat.New();
        var sector = seat.Sector;
        var npcSide = seat.PeerSide;

        var a = new Vector3(980f, 932f, 956f);
        var b = new Vector3(160f, 989f, 998f);
        var mid = new Vector3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

        var station = Target(sector, npcSide, id: 4471, x: b.X, y: b.Y, z: b.Z);

        // The obstacle: a plain, non-NPC ship on the SAME side, parked dead on
        // the A→B line. IsNpc defaults to false, so it never runs any decision
        // cycle (Ship.cs P148) and simply sits still for the whole test.
        var friendlyHull = seat.Mission.Core.HullTypes.Values.First();
        var friendly = new Ship(9001, friendlyHull, mid, Vector3.Zero, Orientation.Identity) { SideId = npcSide };
        seat.Mission.PlaceShip(friendly, sector);

        var npc = seat.Server.SpawnNpcShip(seat.Mission, sector, npcSide, a);
        npc.PlanTarget = station;
        npc.PlanCommandId = OrderChange.CidGoto;

        float shipHalfLen = 0.5f * npc.HullType.Length;
        float friendlyRadius = 0.5f * friendlyHull.Length;

        Assert.True(LineDeviation(mid, a, b) < 5f, "friendly ship must be on the straight line A→B for the test to be meaningful");

        float minFriendlyDist = float.MaxValue;
        for (int i = 0; i < 12000; i++)
        {
            seat.Server.TickSim();
            minFriendlyDist = MathF.Min(minFriendlyDist, DistToPoint(npc, mid));
        }
        float finalStation = DistToPoint(npc, new Vector3(b.X, b.Y, b.Z));

        Console.WriteLine($"P168-FRIENDLY-DODGE: minFriendlyDist={minFriendlyDist:F1} finalStation={finalStation:F1}");

        Assert.True(minFriendlyDist > friendlyRadius + shipHalfLen + 10f,
            $"must dodge the same-side ship, not plow through it: minFriendlyDist={minFriendlyDist:F1}");
        Assert.True(finalStation < 300f,
            $"must resume toward the station after clearing the friendly: finalStation={finalStation:F1}");
    }

    /// <summary>P168 fixup — an ENEMY-side ship parked dead on the path is NOT
    /// dodged. Native's <c>Ignore()</c>: a drone (<c>pilotType &lt; c_ptPlayer</c>)
    /// always ignores a different-side ship — <c>"trucker rules of road: we're
    /// heavier"</c> (<c>common.cpp:1679-1680</c>). Proven here by the mirror
    /// assertion: the NPC's path deviation stays small (no detour around the
    /// enemy), matching the no-obstacle case's own bound.</summary>
    [Fact]
    public void Traveling_npc_does_not_dodge_an_enemy_side_ship_on_its_path()
    {
        using var seat = Seat.New();
        var sector = seat.Sector;
        var npcSide = seat.PeerSide;
        short enemySide = seat.Mission.Sides.First(s => s.Id != npcSide).Id;

        var a = new Vector3(980f, 932f, 956f);
        var b = new Vector3(160f, 989f, 998f);
        var mid = new Vector3((a.X + b.X) / 2, (a.Y + b.Y) / 2, (a.Z + b.Z) / 2);

        var station = Target(sector, npcSide, id: 4481, x: b.X, y: b.Y, z: b.Z);

        var enemyHull = seat.Mission.Core.HullTypes.Values.First();
        var enemy = new Ship(9002, enemyHull, mid, Vector3.Zero, Orientation.Identity) { SideId = enemySide };
        seat.Mission.PlaceShip(enemy, sector);

        var npc = seat.Server.SpawnNpcShip(seat.Mission, sector, npcSide, a);
        npc.PlanTarget = station;
        npc.PlanCommandId = OrderChange.CidGoto;

        Assert.True(LineDeviation(mid, a, b) < 5f, "enemy ship must be on the straight line A→B for the test to be meaningful");

        float maxDev = 0f;
        for (int i = 0; i < 12000; i++)
        {
            seat.Server.TickSim();
            maxDev = MathF.Max(maxDev, LineDeviation(npc.Position, a, b));
        }
        float finalStation = DistToPoint(npc, new Vector3(b.X, b.Y, b.Z));

        Console.WriteLine($"P168-ENEMY-NODODGE: maxLineDeviation={maxDev:F1} finalStation={finalStation:F1}");

        Assert.True(finalStation < 300f, $"must still reach the station: finalStation={finalStation:F1}");
        Assert.True(maxDev < 200f,
            $"an enemy ship must NOT be dodged (native: drones ignore different-side ships) — maxLineDeviation={maxDev:F1}");
    }
}
