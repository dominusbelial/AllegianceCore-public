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
/// P294 — live loopback proof of the building-effect <b>creation</b> wire over
/// a real in-process server + real TCP bots, the port of native
/// <c>FedSrvSiteBase::CreateBuildingEffect</c>'s broadcast half
/// (<c>FedSrv.CPP:13733-13767</c>): when an in-progress building effect starts
/// (reusing P163's <see cref="Sector.StartBuildingEffect"/> mechanic) the server
/// broadcasts <c>EXPORT(26)/OT_buildingEffect</c> <b>whole-mission</b> — every
/// peer on every side receives the fresh construction's target asteroid id,
/// sector, radii and positions. The codec round-trips byte-exact in
/// BuildingEffectWireCodecsWireTests; the client-side table/prune is covered by
/// ClientWorldBuildingEffectTests. The DESTROYED(181) interruption half is
/// covered by LiveBuildingEffectWireTests.
/// </summary>
[Collection("LiveNetwork")]
public class LiveBuildingEffectStartWireTests
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
            new Gm { Name = "Build Start Wire Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 933u, DwCookie = 0x933u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9340000000000000000ul, DrmHash = "buildstartwire",
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
        var bots = new[] { "SA", "SB" }.Select(name => { var b = new FltBot(); b.Connect(server.BoundPort, name); b.SendLogonRequest(Request(name)); return b; }).ToArray();
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 6000);
        return bots;
    }

    /// <summary>A synthetic rock the building effect targets — the same record
    /// shape LiveBuildingEffectWireTests/BuildingConstructionLiveTests injects.</summary>
    private static DataAsteroidIGC MkAsteroid(short id, Vector3 pos, short radius, short sectorId, short aabmCap) => new(
        Signature: 1.0f,
        Position: [pos.X, pos.Y, pos.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
        Rotation: [0f, 0f, 0f, 1f], Ore: 0f, OreMax: 0f,
        AabmCapabilities: aabmCap,
        AsteroidId: id, HitPoints: 0f, Radius: radius,
        ModelName: "roid.mdl", TextureName: "", IconName: "",
        ClusterId: sectorId, Name: $"ROID_{id}", Fraction: 0);

    /// <summary>A standalone Builder ship (not tied to any bot) placed in the fly
    /// sector — the party that commits to the build. Mirrors
    /// LiveBuildingEffectWireTests.PlaceStandaloneBuilder.</summary>
    private static Ship PlaceStandaloneBuilder(Srv server)
    {
        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var builder = new Ship(9961, hull, new Vector3(-350f, -390f, -410f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(builder, fly);
        return builder;
    }

    /// <summary>⭐ Starting a build broadcasts <c>EXPORT(26)/OT_buildingEffect</c>
    /// whole-mission with the real target-asteroid data — every peer on every
    /// side receives the fresh construction's id/sector/radii/position, the port
    /// of native FedSrvSiteBase::CreateBuildingEffect (FedSrv.CPP:13733-13767).</summary>
    [Fact]
    public void Starting_a_build_broadcasts_ExportBuildingEffect_whole_mission()
    {
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby
        var bots = SeatObservers(server);
        Assert.Equal(2, bots.Length);

        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);

        var rockPos = new Vector3(701f, 852f, 693f);
        var stationType = mission.Core.StationTypes.Values.First();
        var rock = new Asteroid(MkAsteroid(7901, rockPos, 60, fly.Id, stationType.AabmBuild));
        mission.PlaceAsteroid(rock, fly);
        var builder = PlaceStandaloneBuilder(server);

        // ── start the build (records the creation for the next tick's drain) ──
        fly.StartBuildingEffect(builder, rock, stationType, mission.LastUpdate);
        foreach (var b in bots) b.ResetBuildingEffectExports();

        // Tick until every peer has seen the EXPORT(26)/OT_buildingEffect.
        int n = PumpUntil(server,
            () => bots.All(b => b.BuildingEffectExports.Count >= 1), 3000);
        Assert.True(n < 3000, "EXPORT(26)/OT_buildingEffect did not reach both peers (whole-mission)");

        // Whole-mission: both the same-side and the cross-side observer got the
        // same frame, carrying the construction's real data.
        foreach (var b in bots)
        {
            var exp = Assert.Single(b.BuildingEffectExports);
            Assert.Equal(ExportBuildingEffect.OtBuildingEffect, exp.ObjectType);
            Assert.Equal(rock.Data.AsteroidId, exp.Effect.AsteroidId);
            Assert.Equal(fly.Id, exp.Effect.ClusterId);
            Assert.Equal(rock.Data.Radius, exp.Effect.RadiusAsteroid, 1);
            Assert.Equal(stationType.Radius, exp.Effect.RadiusStation, 1);
            Assert.Equal(new Vector3(rockPos.X, rockPos.Y, rockPos.Z), exp.Effect.PositionStop);
            Assert.Equal(new Vector3(builder.Position.X, builder.Position.Y, builder.Position.Z), exp.Effect.PositionStart);
        }

        Console.WriteLine($"P294-LIVE-WIRE: builder {builder.Id} started build of '{stationType.Name}' at asteroid {rock.Data.AsteroidId} → EXPORT(26)/OT_buildingEffect({ExportBuildingEffect.OtBuildingEffect}) whole-mission to {bots.Length} peer(s) (incl. cross-side)");
    }
}
