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
/// P164 — live loopback proof of the <c>BUILDINGEFFECT_DESTROYED(181)</c> wire
/// over a real in-process server + real TCP bots, the port of native
/// <c>FedSrvSiteBase::KillBuildingEffectEvent</c> (<c>FedSrv.CPP:12484-12492</c>):
///   (1) an in-progress building effect (started via
///       <see cref="Sector.StartBuildingEffect"/>, reusing P163's mechanic) whose
///       Builder is killed mid-build is interrupted, and the interruption
///       broadcasts <c>BUILDINGEFFECT_DESTROYED(181)</c> <b>whole-mission</b> —
///       every peer on every side receives the interrupted build's target
///       asteroid id.
/// The codec round-trips byte-exact in BuildingEffectDestroyedWireTests (the
/// TreasureWireTests-style codec unit test).
/// </summary>
[Collection("LiveNetwork")]
public class LiveBuildingEffectWireTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple (measured, P111 report): 1052 = no-station fly sector.
    private const short FlySector = 1052;

    // Far offset so observer peers never interact with the rock/build while
    // still sharing the same (no-station) sector.
    private static readonly Vector3 FarAway = new(900f, 890f, 885f);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Build Wire Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 933u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9340000000000000000ul, DrmHash = "buildwire",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Two observer bots (one per side) seated far away; their ships are
    /// the only ones the spawner makes, parked at <see cref="FarAway"/> so they
    /// never interfere with the rock/build in the fly sector.</summary>
    private static FltBot[] SeatObservers(Srv server)
    {
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            var ship = new Ship(shipId, baseHull with { HabmCapabilities = 0 }, FarAway, Vector3.Zero, Orientation.Identity)
            { SideId = (short)n };          // bot 0 on side 0, bot 1 on side 1
            n++;
            mission.PlaceShip(ship, fly);
            return (ship, fly);
        };
        var bots = new[] { "OA", "OB" }.Select(name => { var b = new FltBot(); b.Connect(server.BoundPort, name); b.SendLogonRequest(Request(name)); return b; }).ToArray();
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 6000);
        return bots;
    }

    /// <summary>A synthetic rock the building effect targets — the same record
    /// shape BuildingConstructionLiveTests injects (carries the station's
    /// AabmBuild so the P280 type gate accepts it; not He3-minable so the
    /// mining pass ignores it; only the building-effect resolver cares).</summary>
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    /// <summary>A standalone Builder ship (not tied to any bot) placed in the fly
    /// sector — the party that commits to the build and is then killed to
    /// interrupt it. Mirrors the standalone injection BuildingConstructionLiveTests
    /// uses, so killing it never touches a seated observer.</summary>
    private static Ship PlaceStandaloneBuilder(Srv server)
    {
        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var builder = new Ship(9961, hull, new Vector3(-350f, -390f, -410f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(builder, fly);
        return builder;
    }

    /// <summary>Kill a ship the way the port's combat path kills one: a lethal
    /// <see cref="Ship.ReceiveDamage"/> driving <see cref="Ship.Fraction"/> to 0
    /// (marking <see cref="Ship.Dead"/>) via a real projectile damage type whose
    /// damage-matrix cell for the hull's defense type is positive. Copied from
    /// BuildingConstructionLiveTests.KillShip.</summary>
    private static void KillShip(Ship ship, Mission mission)
    {
        var dtm = mission.Core.Constants.DamageConstants;
        foreach (var pt in mission.Core.ProjectileTypes.Values)
        {
            if (pt.Power <= 0f) continue;
            int dmg = pt.DamageType, def = ship.HullType.DefenseType;
            if (dmg < 0 || dmg >= DataConstantsIGC.DmgidMax || def < 0 || def >= DataConstantsIGC.DefidMax) continue;
            if (dtm[dmg][def] > 0f)
            {
                ship.ReceiveDamage(pt.DamageType, float.MaxValue, mission.LastUpdate, ship.Position, ship.Position, launcher: null);
                return;
            }
        }
        throw new InvalidOperationException($"no projectile damage type with a positive cell for hull defense type {ship.HullType.DefenseType}");
    }

    /// <summary>⭐ An interrupted build (Builder killed mid-build) broadcasts
    /// <c>BUILDINGEFFECT_DESTROYED(181)</c> whole-mission with the target
    /// asteroid's id — every peer on every side, mirroring how
    /// <c>DESTROY_TREASURE(30)</c> reaches all three peers in
    /// LiveTreasurePickupTests.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Interrupted_build_broadcasts_BuildingEffectDestroyed_whole_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby
        var bots = SeatObservers(server);
        Assert.Equal(2, bots.Length);

        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);

        // A rock to build on, and a standalone Builder to build it.
        var rockPos = new Vector3(701f, 852f, 693f);
        var stationType = mission.Core.StationTypes.Values.First();
        var rock = new Asteroid(MkAsteroid(7901, rockPos, 60, fly.Id, stationType.AabmBuild));
        mission.PlaceAsteroid(rock, fly);
        var builder = PlaceStandaloneBuilder(server);

        // ── start the build, then kill the Builder before it completes ──────
        fly.StartBuildingEffect(builder, rock, stationType, mission.LastUpdate);
        Assert.Contains(builder, fly.Models);
        Assert.Contains(rock, fly.Mineable);

        KillShip(builder, mission);
        Assert.True(builder.Dead);                                        // the interruption trigger

        foreach (var b in bots) b.ResetBuildingEffectDestroyeds();

        // Tick until every peer has seen BUILDINGEFFECT_DESTROYED(181).
        int n = PumpUntil(server,
            () => bots.All(b => b.BuildingEffectDestroyeds.Count >= 1), 3000);
        Assert.True(n < 3000, "BUILDINGEFFECT_DESTROYED(181) did not reach both peers (whole-mission)");

        // Whole-mission: both the same-side and the cross-side observer got the
        // same frame, carrying the interrupted build's target asteroid id.
        foreach (var b in bots)
        {
            var bed = Assert.Single(b.BuildingEffectDestroyeds);
            Assert.Equal(rock.Data.AsteroidId, bed.AsteroidId);
        }

        Console.WriteLine($"P164-LIVE-WIRE: builder {builder.Id} killed mid-build → BUILDINGEFFECT_DESTROYED(181) asteroid {rock.Data.AsteroidId} whole-mission to {bots.Length} peer(s) (incl. cross-side)");
    }
}
