using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Explorer;

/// <summary>
/// Manual smoke-test convenience (analogous to AllegianceCore's
/// <c>tools/livecombat</c> and <c>tools/chatdemo</c>): boots a real in-process
/// <see cref="Srv"/>, connects a <see cref="ConnectionSession"/> over loopback,
/// logs on, commands forward thrust, and prints a periodic one-line snapshot of
/// the <see cref="ClientWorld"/> (ship count, my ship's sector, my position).
/// Nothing here is depended on by automated verification — it exists so a human
/// can eyeball the entity table filling from real wire traffic. Ctrl-C to stop.
/// </summary>
internal static class Program
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static Cfg Config(int port) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Explorer", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 770u, DwCookie = 0x2026u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 79000000000000000ul, DrmHash = "explorer",
    };

    private static int Main(string[] args)
    {
        int port = 0;
        if (args.Length > 0 && !int.TryParse(args[0], out port))
        {
            Console.Error.WriteLine("usage: explorer [port]   (0 = ephemeral)");
            Console.Error.WriteLine("  boots an in-process Allegiance server, connects a ConnectionSession,");
            Console.Error.WriteLine("  logs on, commands forward thrust, and prints a live world snapshot.");
            Console.Error.WriteLine($"  Requires the artwork tree at {ArtRoot}.");
            return 2;
        }

        using var server = new Srv();
        server.Boot(Config(port));
        ushort bound = server.BoundPort;
        Console.WriteLine($"[explorer] server listening on 127.0.0.1:{bound}");

        using var session = new ConnectionSession();
        session.Connect(bound, "explorer");
        session.SendLogonRequest(Request("explorer_probe"));
        session.WaitForAck(10_000);
        Console.WriteLine($"[explorer] logon acked, serverTime={session.Ack!.TimeServer}");

        // Wait for the SET_CLUSTER assignment, then command forward thrust.
        const int MaxTicks = 8000;
        int pump = 0;
        while (session.World.MyShipId is null && pump < MaxTicks) { server.TickSim(); pump++; Thread.Sleep(1); }
        if (session.World.MyShipId is null) { Console.Error.WriteLine("[explorer] FAILED: no ship assignment"); return 1; }

        session.CommandControls(new ControlData(0f, 0f, 0f, 1f), ShipControlState.ForwardButton);
        Console.WriteLine("[explorer] commanded full forward thrust");

        // Periodic one-line world snapshot until Ctrl-C / 20s.
        var start = DateTime.UtcNow;
        while (DateTime.UtcNow - start < TimeSpan.FromSeconds(20))
        {
            for (int k = 0; k < 25; k++) { server.TickSim(); Thread.Sleep(1); }
            Snapshot(session.World);
        }
        return 0;
    }

    private static void Snapshot(State.ClientWorld w)
    {
        var my = w.MyShipId;
        State.ShipEntity? me = my is { } id && w.Ships.TryGetValue(id, out var s) ? s : null;
        Console.WriteLine(
            $"[snapshot] ships={w.Ships.Count} " +
            $"myShip={(me?.ShipId.ToString() ?? "-")} " +
            $"sector={me?.SectorId.ToString() ?? "-"} " +
            $"pos={(me?.Latest is { } u ? $"{u.Position.X:F1},{u.Position.Y:F1},{u.Position.Z:F1}" : "-")} " +
            $"tick={me?.LastUpdateTick.ToString() ?? "-"}");
    }
}
