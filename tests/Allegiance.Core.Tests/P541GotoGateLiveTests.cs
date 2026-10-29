using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P541 — the server-side goto gate. The player's own ship is flown by the CLIENT
/// (P508 local sim), so a server-side GotoPlan would fight the local sim
/// (measured by the P541 probe: a native-pose client's accepted static poses pin
/// the server's gotoing ship at spawn, never reaching the target). The fix: the
/// server autopilots a sender's ship ONLY when it is a legacy/bot client that does
/// not fly itself (the P403 behaviour); a real native client — identified by
/// sending real-time+pose SHIP_UPDATEs — is never re-routed server-side, because
/// it steers its own ship toward the clicked sector via its own client autopilot.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P541GotoGateLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P541 Gate", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 546u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 97900000000000546ul, DrmHash = "p541gate",
    };

    private static int Pump(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void BootWithBot(Srv server, out FltBot bot, out Ship botShip, out Sector sectorA, out Sector sectorB)
    {
        Sector? a = null, b = null;
        server.ShipSpawner = (_, m, id) =>
        {
            foreach (var sec in m.Sectors)
            {
                var w = sec.Warps.FirstOrDefault(x => x.Destination is not null && !ReferenceEquals(x.Destination, sec) && x.Partner() is not null);
                if (w is not null) { a = sec; b = w.Destination; break; }
            }
            var hull = m.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(id, hull, new Vector3(500f, 620f, 830f), Vector3.Zero, Orientation.Identity);
            m.PlaceShip(ship, a!);
            return (ship, a!);
        };
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        FltBot local = new();
        local.Connect(server.BoundPort, "p541_gate");
        local.SendLogonRequest(Request("p541_gate"));
        Pump(server, () => local.ShipAssignment is not null, 4000);
        bot = local;
        botShip = server.Ships.Single(kv => kv.Ship.Id == local.ShipAssignment!.Ship.ShipId).Ship;
        sectorA = a!; sectorB = b!;
    }

    /// <summary>⭐ A NATIVE client (sends real-time+pose SHIP_UPDATEs — P508) is
    /// NEVER gotoed server-side: it flies its own ship, so the server must not
    /// autopilot it too (that fight is what broke the old path).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Native_client_self_cluster_goto_does_NOT_set_server_GotoWaypoint()
    {
        Require.Artwork();
        using var server = new Srv();
        BootWithBot(server, out var bot, out var botShip, out var sectorA, out var sectorB);

        // Mark the peer as a native client: send one real-time+pose update with the
        // SET_CLUSTER cookie (exactly what the P508 client sends once flying).
        byte cookie = bot.ShipAssignment!.Cookie;
        uint nowMs = (uint)(server.SimNow * 1000.0);
        bot.SendOwnShipPose(nowMs, cookie, botShip.Position, Vector3.Zero, Orientation.Identity,
            ControlData.Neutral, 0, new float[3]);
        Pump(server, () => false, 20);

        // Issue a self-addressed c_buoyCluster goto to sector B.
        var db = new DataBuoyIGC([0f, 0f, 0f], ClusterId: sectorB!.Id, Type: DataBuoyIGC.CBuoyCluster, BuoyId: 5441, Visible: false);
        bot.SendChatBuoy("goto", ChatTarget.Individual, botShip.Id, db);
        Pump(server, () => false, 80);

        // The server must NOT have set a GotoWaypoint — the client flies itself.
        Assert.Null(botShip.GotoWaypoint);
        Console.WriteLine("P541-GATE-NATIVE: a native-pose client's self cluster goto sets NO server GotoWaypoint (client flies its own ship) — RESULT PASS");
    }

    /// <summary>A legacy/bot client (no native poses — it does not fly itself) keeps
    /// the P403 server-side goto: the server autopilots the ship to the target.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Legacy_bot_self_cluster_goto_STILL_sets_server_GotoWaypoint()
    {
        Require.Artwork();
        using var server = new Srv();
        BootWithBot(server, out var bot, out var botShip, out _, out var sectorB);

        // No native pose sent → the peer stays a legacy bot (NativeClient=false).
        var db = new DataBuoyIGC([0f, 0f, 0f], ClusterId: sectorB!.Id, Type: DataBuoyIGC.CBuoyCluster, BuoyId: 5482, Visible: false);
        bot.SendChatBuoy("goto", ChatTarget.Individual, botShip.Id, db);

        int setTicks = Pump(server, () => botShip.GotoWaypoint is { } gw && gw.ClusterId == sectorB.Id, 2000);
        Assert.True(setTicks < 2000, "legacy bot's self cluster goto never set the server GotoWaypoint (P403 regression)");
        Assert.Equal(GotoKind.Cluster, botShip.GotoWaypoint!.Value.Kind);
        Console.WriteLine("P541-GATE-LEGACY: a legacy bot's self cluster goto still sets the server GotoWaypoint (P403 preserved) — RESULT PASS");
    }
}
