using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Explorer;

/// <summary>
/// An interactive, line-based text client driven entirely by <see cref="ClientWorld"/>:
/// boots a real in-process <see cref="Srv"/>, connects a real <see cref="ConnectionSession"/>
/// over loopback, logs on, then hands control to a command REPL read from stdin
/// while a background thread keeps ticking the server in real time underneath it
/// (the world keeps moving between commands, same as a real client). This
/// replaces the old fixed 20-second forward-thrust demo — it is the "prove the
/// plan paid off" artifact: a way to actually fly, fight, and chat through this
/// library's own wire model, with zero dependency on WinTrek's DirectX/lavapipe
/// stack.
/// </summary>
/// <remarks>
/// Line-based commands, not raw keystrokes: a plain <c>Console.ReadKey</c>
/// key-down loop can't detect key-release in a portable way (no key-up events
/// off a terminal), so continuous "hold W to thrust" isn't something a console
/// app can honestly offer. Line commands instead set persistent control state
/// (matching how the underlying sim already treats <c>ControlData</c>/<c>StateM</c>
/// as sticky until changed — see <c>Ship.ExecuteMove</c>) — "thrust fwd" keeps
/// thrusting forward until you say "thrust stop" or something else changes it.
/// This also makes the tool scriptable: commands can be piped in over stdin,
/// which is how this file is smoke-verified without a real terminal attached.
/// </remarks>
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

    // ── persistent control state (sticky, mirrors what the sim expects) ─────
    private static float _yaw, _pitch;
    private static int _stateM;
    private static bool _readyRequested;

    private static int Main(string[] args)
    {
        int port = 0;
        string playerName = "explorer";
        if (args.Length > 0 && !int.TryParse(args[0], out port))
        {
            Console.Error.WriteLine("usage: explorer [port] [name]   (port 0 = ephemeral)");
            Console.Error.WriteLine("  boots an in-process Allegiance server, connects a ConnectionSession,");
            Console.Error.WriteLine("  logs on, then hands control to an interactive command REPL.");
            Console.Error.WriteLine($"  Requires the artwork tree at {ArtRoot}. Type 'help' once connected.");
            return 2;
        }
        if (args.Length > 1) playerName = args[1];

        using var server = new Srv();
        server.Boot(Config(port));
        ushort bound = server.BoundPort;
        Console.WriteLine($"[explorer] server listening on 127.0.0.1:{bound}");

        using var session = new ConnectionSession();
        session.Connect(bound, playerName);
        session.SendLogonRequest(Request(playerName));

        // The server only drains its transport inside a tick, so pump it until
        // the logon ACK (or rejection) lands, then until we have a ship.
        const int MaxAckTicks = 4000;
        int ackTicks = 0;
        while (session.Ack is null && ackTicks < MaxAckTicks) { server.TickSim(); ackTicks++; Thread.Sleep(1); }
        if (session.Ack is null) { Console.Error.WriteLine("[explorer] FAILED: no LOGONACK"); return 1; }
        Console.WriteLine($"[explorer] logon acked, serverTime={session.Ack!.TimeServer}");

        const int MaxShipTicks = 8000;
        int pump = 0;
        while (session.World.MyShipId is null && pump < MaxShipTicks) { server.TickSim(); pump++; Thread.Sleep(1); }
        if (session.World.MyShipId is null) { Console.Error.WriteLine("[explorer] FAILED: no ship assignment"); return 1; }
        Console.WriteLine($"[explorer] assigned ship {session.World.MyShipId}");

        // Keep the world moving in real time while the REPL blocks on input —
        // a real client's server connection isn't paused while you're typing.
        using var stopTicking = new CancellationTokenSource();
        var tickThread = new Thread(() =>
        {
            while (!stopTicking.IsCancellationRequested)
            {
                server.TickSim();
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "explorer-tick" };
        tickThread.Start();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopTicking.Cancel(); };

        Console.WriteLine("[explorer] connected. Type 'help' for commands, 'quit' to exit.");
        PrintStatus(session.World);

        RunRepl(session, stopTicking.Token);

        stopTicking.Cancel();
        tickThread.Join(TimeSpan.FromSeconds(2));
        return 0;
    }

    private static void RunRepl(ConnectionSession session, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            Console.Write("> ");
            string? line = Console.In.ReadLine();
            if (line is null) break;   // EOF (piped script finished, or stdin closed)
            line = line.Trim();
            if (line.Length == 0) continue;

            int sp = line.IndexOf(' ');
            string cmd = (sp < 0 ? line : line[..sp]).ToLowerInvariant();
            string rest = sp < 0 ? "" : line[(sp + 1)..].Trim();

            switch (cmd)
            {
                case "help" or "?":
                    PrintHelp();
                    break;
                case "status" or "s":
                    PrintStatus(session.World);
                    break;
                case "who" or "ships":
                    PrintShips(session.World);
                    break;
                case "thrust":
                    ApplyThrust(session, rest);
                    break;
                case "turn":
                    ApplyTurn(session, rest);
                    break;
                case "pitch":
                    ApplyPitch(session, rest);
                    break;
                case "fire":
                    ApplyFire(session, rest);
                    break;
                case "ready":
                    ToggleReady(session);
                    break;
                case "launch":
                    Launch(session);
                    break;
                case "undock":
                    session.SendDocked();
                    Console.WriteLine("[explorer] sent undock");
                    break;
                case "chat":
                    if (rest.Length == 0) { Console.WriteLine("[explorer] usage: chat <message>"); break; }
                    session.SendChat(rest);
                    Console.WriteLine($"[explorer] sent chat: {rest}");
                    break;
                case "quit" or "exit" or "q":
                    return;
                default:
                    Console.WriteLine($"[explorer] unknown command '{cmd}' (try 'help')");
                    break;
            }
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            commands:
              status | s            show current ship/world snapshot
              who | ships           list every known ship in the entity table
              thrust <fwd|back|stop>  set forward/backward thrust (sticky until changed)
              turn <left|right|straight>  set yaw (sticky)
              pitch <up|down|level> set pitch (sticky)
              fire <on|off>         toggle all-weapons fire
              ready                 toggle FM_CS_PLAYER_READY
              launch                buy current hull's loadout and launch/undock
              undock                send a pure undock announce
              chat <message>        send an Everyone chat message
              quit | exit | q       disconnect and exit
            """);
    }

    private static void ApplyThrust(ConnectionSession session, string arg)
    {
        const int ThrustBits = ShipControlState.ForwardButton | ShipControlState.BackwardButton;
        _stateM &= ~ThrustBits;
        switch (arg.ToLowerInvariant())
        {
            case "fwd" or "forward" or "f":
                _stateM |= ShipControlState.ForwardButton;
                break;
            case "back" or "backward" or "b":
                _stateM |= ShipControlState.BackwardButton;
                break;
            case "stop" or "":
                break;   // bits already cleared above
            default:
                Console.WriteLine("[explorer] usage: thrust <fwd|back|stop>");
                return;
        }
        Push(session);
        Console.WriteLine($"[explorer] thrust -> {arg}");
    }

    private static void ApplyTurn(ConnectionSession session, string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "left" or "l": _yaw = -1f; break;
            case "right" or "r": _yaw = 1f; break;
            case "straight" or "center" or "0" or "": _yaw = 0f; break;
            default:
                Console.WriteLine("[explorer] usage: turn <left|right|straight>");
                return;
        }
        Push(session);
        Console.WriteLine($"[explorer] yaw -> {_yaw}");
    }

    private static void ApplyPitch(ConnectionSession session, string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "up" or "u": _pitch = 1f; break;
            case "down" or "d": _pitch = -1f; break;
            case "level" or "center" or "0" or "": _pitch = 0f; break;
            default:
                Console.WriteLine("[explorer] usage: pitch <up|down|level>");
                return;
        }
        Push(session);
        Console.WriteLine($"[explorer] pitch -> {_pitch}");
    }

    private static void ApplyFire(ConnectionSession session, string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "on" or "": _stateM |= ShipControlState.AllWeapons; break;
            case "off": _stateM &= ~ShipControlState.AllWeapons; break;
            default:
                Console.WriteLine("[explorer] usage: fire <on|off>");
                return;
        }
        Push(session);
        Console.WriteLine($"[explorer] fire -> {((_stateM & ShipControlState.AllWeapons) != 0 ? "on" : "off")}");
    }

    private static void Push(ConnectionSession session) =>
        session.CommandControls(new ControlData(_yaw, _pitch, 0f, 0f), _stateM);

    private static void ToggleReady(ConnectionSession session)
    {
        _readyRequested = !_readyRequested;
        short myId = session.World.MyShipId ?? -1;
        session.SendPlayerReady(_readyRequested, myId);
        Console.WriteLine($"[explorer] sent ready={_readyRequested} for ship {myId}");
    }

    private static void Launch(ConnectionSession session)
    {
        short myId = session.World.MyShipId ?? -1;
        if (myId < 0 || !session.World.Ships.TryGetValue(myId, out var mine))
        {
            Console.WriteLine("[explorer] no own ship known yet");
            return;
        }
        session.SendBuyLoadout(mine.HullId, fLaunch: true);
        Console.WriteLine($"[explorer] sent buy-loadout (hull {mine.HullId}, launch)");
    }

    private static void PrintShips(ClientWorld w)
    {
        if (w.Ships.Count == 0) { Console.WriteLine("[explorer] no ships known"); return; }
        foreach (var (id, s) in w.Ships.OrderBy(kv => kv.Key))
        {
            string who = s.IsMine ? "ME" : (s.CharacterName.Length > 0 ? s.CharacterName : "?");
            string pos = s.Latest is { } u ? $"{u.Position.X:F0},{u.Position.Y:F0},{u.Position.Z:F0}" : "-";
            Console.WriteLine($"  [{id}] {who,-10} side={s.SideId} sector={s.SectorId} pos={pos} dead={s.IsDead}");
        }
    }

    private static void PrintStatus(ClientWorld w)
    {
        var my = w.MyShipId;
        ShipEntity? me = my is { } id && w.Ships.TryGetValue(id, out var s) ? s : null;
        int ready = w.Roster.ReadyByShipId.Count(kv => kv.Value);
        Console.WriteLine(
            $"[status] ships={w.Ships.Count} " +
            $"myShip={(me?.ShipId.ToString() ?? "-")} " +
            $"dead={(me?.IsDead == true)} " +
            $"sector={me?.SectorId.ToString() ?? "-"} " +
            $"pos={(me?.Latest is { } u ? $"{u.Position.X:F1},{u.Position.Y:F1},{u.Position.Z:F1}" : "-")} " +
            $"vel={(me?.Latest is { } u2 ? $"{u2.Velocity.Length():F1}" : "-")} " +
            $"hull={(me?.Latest is { } u3 ? $"{u3.HullFraction * 100:F0}%" : "-")} " +
            $"energy={(me?.Latest is { } u4 ? $"{u4.EnergyFraction * 100:F0}%" : "-")} " +
            $"tick={me?.LastUpdateTick.ToString() ?? "-"} " +
            $"world(C={w.Clusters.Count} St={w.Stations.Count} W={w.Warps.Count} A={w.Asteroids.Count}) " +
            $"roster(size={w.Roster.ReadyByShipId.Count} ready={ready}) " +
            $"events(chats={w.Events.Chats.Count} buoys={w.Events.ChatBuoys.Count} kills={w.Events.Kills.Count} paydays={w.Events.Paydays.Count}) " +
            $"mission={(w.Mission is { } m ? $"{m.StaticCoreFile}:{m.Stage}" : "-")}");
    }
}
