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
/// P458 — investing in a station type produces an AI constructor drone (the
/// port of native <c>FedSrvSiteBase::StationTypeCompleted</c>,
/// <c>FedSrv.CPP:11343-11372</c>). A real station-type bucket, once fully
/// funded (the P189 <c>FundAndComplete</c> loopback pattern), must:
/// <list type="number">
/// <item>spawn a NEW Builder-pilot NPC on that side flying the construction
/// drone type's <b>real</b> hull (native <c>CreateStockDrone</c> uses
/// <c>pdronetype-&gt;GetHullTypeID()</c>, FedSrv.CPP:2228) with the completed
/// station type as its <c>BuilderStationType</c> base data;</item>
/// <item>send the side-team chat "New constructor requesting asteroid." with
/// the station type's <c>ConstructorNeedRockSound</c> as the voice id
/// (FedSrv.CPP:11369-11370);</item>
/// <item><b>not</b> auto-build — the constructor waits (native's "Don't
/// auto-build" rewrite, FedSrv.CPP:11358-11366);</item>
/// <item>and finally START construction when a player issues it a P425
/// <c>BUILD</c> order at a compatible asteroid.</item>
/// </list>
/// Also pins the <see cref="Srv.SpawnNpcShip"/> real-hull contract directly
/// (a drone type with a known <c>HullTypeId</c> → the spawned ship has that
/// hull).
/// </summary>
[Collection("LiveNetwork")]
public sealed class StationBucketSpawnsConstructorLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int ups = 20) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "P458 Station Bucket", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 881u, DwCookie = 0x881u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 88600000000000001ul, DrmHash = "p458bucket",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void SeatOne(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
    }

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    /// <summary>Deterministically route the single funder peer to mission-0
    /// side 0 (the owning side we invest a station bucket on).</summary>
    private static void InjectSingleMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 750f, 910f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            seq++;
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>Fully fund a bucket (money + build time) via the donor bot,
    /// pumping until the server records the completion (P189 pattern).</summary>
    private static void FundAndComplete(Srv server, FltBot funder, Bucket bucket, int budget)
    {
        server.Ships.First(s => s.Ship.Id == funder.ShipAssignment!.Ship.ShipId).Wallet.Money = 50_000_000;
        funder.ResetChatReceived();
        funder.SendDonate(bucket.Id, bucket.Price);
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(bucket.Id), budget);
        Assert.True(done < budget, $"station-type bucket {bucket.Id} did not complete after {budget} ticks");
    }

    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    /// <summary>
    /// ⭐ The main loopback proof: funding a real station-type bucket completes
    /// the whole <c>StationTypeCompleted</c> arc — a waiting Builder constructor
    /// spawns (real hull + base data), the team hears "New constructor
    /// requesting asteroid.", it does NOT build on its own, and a P425 BUILD
    /// order at a compatible asteroid starts the timed construction.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completing_station_type_bucket_spawns_waiting_constructor_and_build_order_builds()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        SeatOne(server, bot, "P458C");
        short sideId = SideOf(server, bot);
        var mission = server.Missions[0];
        var side = mission.Sides.Single(s => s.Id == sideId);

        // Catalog eligibility includes future tiers. Native path seeding makes
        // those visible at boot; fund a station the side can actually buy now.
        var stBucket = side.Buckets.First(b => b.ItemType == BucketItemType.BitStationType && side.CanBuy(b));
        var stationType = stBucket.StationType!.Value;
        short conDroneId = stationType.ConstructionDroneTypeId;
        var conDrone = mission.Core.DroneTypes[conDroneId];
        Assert.Equal(Ship.PilotTypeBuilder, conDrone.PilotType);   // scope 4: a Builder-pilot construction drone
        Console.WriteLine($"[P458] investing in station type {stationType.StationTypeId} '{stationType.Name}' (conDrone {conDroneId}, needRockSound {stationType.ConstructorNeedRockSound})");

        FundAndComplete(server, bot, stBucket, budget: 8000);

        // ── (1) the constructor spawned: a NEW Builder NPC on the side, with
        //    the construction drone type's real hull and the station type as
        //    its base data. ────────────────────────────────────────────────────
        Ship con = GetConstructor(server, sideId);
        Assert.Equal(conDrone.HullTypeId, con.HullType.HullId);       // real construction hull (native CreateStockDrone)
        Assert.Equal(stationType.StationTypeId, con.BuilderStationType!.Value.StationTypeId);
        Assert.True(con.WaitsForBuildOrder);                           // native "Don't auto-build"
        Console.WriteLine($"[P458] constructor ship {con.Id} spawned: pilotType {con.PilotType}, hull {con.HullType.HullId} '{con.HullType.Name}', BuilderStationType '{con.BuilderStationType.Value.Name}' in sector {con.Sector?.Name}/{con.Sector?.Id}");

        // ── (2) the team chat arrived with the need-rock sound as voice. ─────
        int rx = PumpUntil(server, () => bot.Chats.Any(c => c.Message.Contains("New constructor requesting asteroid.")), 800);
        Assert.True(rx < 800, "team did not receive the 'New constructor requesting asteroid.' chat");
        var needRock = bot.Chats.Single(c => c.Message.Contains("New constructor requesting asteroid."));
        Assert.Equal((int)ChatTarget.Team, needRock.ChatTarget);
        Assert.Equal(stationType.ConstructorNeedRockSound, needRock.VoiceOver);
        Console.WriteLine($"[P458] team chat '{needRock.Message}' (voice {needRock.VoiceOver}) received");

        // ── (3) no auto-build: pump well past the idle-decision latency; the
        //    waiting constructor must neither start a build itself nor register
        //    one in its sector. ────────────────────────────────────────────────
        int nb = PumpUntil(server, () => false, 200);        // ~10 sim-s of idle
        Assert.False(con.IsBuilding);
        Assert.DoesNotContain(mission.Sectors.SelectMany(s => s.BuildingEffects), e => ReferenceEquals(e.Builder, con));
        Console.WriteLine($"[P458] constructor did NOT auto-build over {nb} ticks (WaitsForBuildOrder honoured)");

        // ── (4) a P425 BUILD order at a compatible rock starts construction. ──
        var conSector = con.Sector!;
        var rp = new Vector3(con.Position.X + 400f, con.Position.Y, con.Position.Z);
        var rock = new Asteroid(MkAsteroid((short)(6000 + sideId), rp, 60, conSector.Id, stationType.AabmBuild));
        mission.PlaceAsteroid(rock, conSector);

        bot.SendOrderChange(con.Id, OrderChange.CidBuild, OrderChange.CmdAccepted, IgcObjectType.Asteroid, rock.Data.AsteroidId);
        int bc = PumpUntil(server, () => conSector.BuildingEffects.Count > 0, 2000);
        Assert.True(bc < 2000, "P425 Build order at a matching rock did not start construction");
        var effect = Assert.Single(conSector.BuildingEffects);
        Assert.Same(rock, effect.Target);
        Assert.Equal(stationType.StationTypeId, effect.StationType.StationTypeId);
        Assert.True(con.IsBuilding);
        Console.WriteLine($"[P458] Build order accepted → construction of '{stationType.Name}' started at asteroid {rock.Data.AsteroidId}");
    }

    /// <summary>
    /// ⭐ The <see cref="Srv.SpawnNpcShip"/> real-hull contract: spawning a
    /// drone type with a known <c>HullTypeId</c> yields that hull (native
    /// <c>CreateStockDrone</c> GetHullTypeID), while a no-drone-type spawn keeps
    /// the lowest-hull fallback.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SpawnNpcShip_uses_the_drone_types_real_hull()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var mission = server.Missions[0];
        var sector = mission.Sectors[0];
        short sideId = mission.Sides[0].Id;

        // A drone type with a definite hull id in PCore001 ('.Miner' dr 1 → hull 3).
        var minerDt = mission.Core.DroneTypes.OrderBy(kv => kv.Key).First(kv => kv.Value.PilotType == Ship.PilotTypeMiner);
        var mined = server.SpawnNpcShip(mission, sector, sideId, new Vector3(100f, 100f, 100f), minerDt.Key);
        Assert.Equal(minerDt.Value.HullTypeId, mined.HullType.HullId);
        Assert.Equal(Ship.PilotTypeMiner, mined.PilotType);

        // A Builder construction drone (dr 5 → hull 9).
        var bldDt = mission.Core.DroneTypes.First(kv => kv.Value.PilotType == Ship.PilotTypeBuilder);
        var built = server.SpawnNpcShip(mission, sector, sideId, new Vector3(120f, 100f, 100f), bldDt.Key);
        Assert.Equal(bldDt.Value.HullTypeId, built.HullType.HullId);
        Assert.Equal(Ship.PilotTypeBuilder, built.PilotType);

        // No drone type → the lowest-hull-id fallback (behavior-preserving).
        var lowest = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var fallback = server.SpawnNpcShip(mission, sector, sideId, new Vector3(140f, 100f, 100f));
        Assert.Equal(lowest.HullId, fallback.HullType.HullId);
        Assert.Equal(Ship.PilotTypeMiner, fallback.PilotType);

        Console.WriteLine($"[P458-HULL] SpawnNpcShip: miner dr {minerDt.Key}→hull {mined.HullType.HullId}, builder dr {bldDt.Key}→hull {built.HullType.HullId}, no-drone→lowest {fallback.HullType.HullId}");
    }

    private static Ship GetConstructor(Srv server, short sideId)
    {
        var cons = server.Missions[0].Sectors
            .SelectMany(s => s.Models.OfType<Ship>())
            .Where(s => s.IsNpc && s.SideId == sideId && s.PilotType == Ship.PilotTypeBuilder && s.BuilderStationType is not null)
            .ToList();
        return Assert.Single(cons);
    }
}
