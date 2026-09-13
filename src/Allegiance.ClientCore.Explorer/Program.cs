using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
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
///
/// <para><c>who</c> now lists every known ship with hull, loadout, and
/// distance-from-you, and a second ship — a companion on the same side,
/// auto-piloted by a tiny fixed script on its own background thread — makes that
/// listing show real multiplayer: a second, moving, loadout-carrying ship to
/// observe, exercising the P85 teammate-position broadcast and P86 teammate-
/// loadout visibility live in an interactive session, not just in the test
/// suite.</para>
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
        FedsrvVer = LogonRequest.MsgVer, Time = 930u, DwCookie = 0x33Fu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 79100000000000000ul, DrmHash = "explorer",
    };

    // ── companion teammate: a second, same-side, auto-piloted ship to observe ─
    //
    // Why we force same-side: the server's DEFAULT spawner round-robins peers
    // across sides (peer N → side N % teams), so with 2 teams the second
    // connection would land on the OPPOSITE side — and cross-side ships never
    // enter a peer's entity table at all (see TeammateMovementLiveTests), so a
    // default-spawn companion would make `who` still show ONE ship, defeating
    // this tool's whole reason to exist. The point here is "show me a real
    // second, moving, loadout-carrying ship" — which REQUIRES same-side
    // visibility. So we seed the server's ShipSpawner (the same reflection seam
    // the test suite already uses) to put both ships on side 0. There is no
    // same-side-guarantee hiding here: the default spawner simply cannot seat
    // two same-side peers, and observation needs it.
    private static void ForceCompanionSameSide(Srv server)
    {
        var field = typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("ShipSpawner field not found on Srv");
        int spawned = 0;
        Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner = (core, mission, id) =>
        {
            int idx = spawned++;
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var pos = new Vector3(idx * 560f, 740f, 820f);   // staggered, non-overlapping, sector 0
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
        field.SetValue(server, spawner);
    }

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    // A cheap real dispersion-0 weapon, same recipe the test suite uses
    // (FullSessionKillIntegrationTests.RealWeaponPart): the first weapon part
    // in the core by part id, a genuine non-hull-only loadout for the companion
    // — the thing whose id must land in OUR world's view of the companion.
    private static ExpandedPartData RealWeaponPart(StaticCore core) =>
        new(
            core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                .OrderBy(p => p.PartId).First().PartId,
            Amount: 999, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted);

    /// A tiny fixed autopilot for the companion: boost forward a few seconds,
    /// then forward-plus-turn-left, then straighten and coast — repeating
    /// forever. Pure wall-clock, no state machine — just "something to observe"
    /// whose position provably changes between two <c>who</c> calls a few
    /// seconds apart (the live P85 broadcast), not a canned snapshot.
    private static void CompanionAutopilot(ConnectionSession c, CancellationToken stop)
    {
        var start = DateTime.UtcNow;
        while (!stop.IsCancellationRequested)
        {
            double ms = (DateTime.UtcNow - start).TotalMilliseconds % 9700.0;
            int stateM = 0; float yaw = 0f;
            if (ms < 3700)               stateM |= ShipControlState.ForwardButton;                 // boost fwd
            else if (ms < 6700)          { stateM |= ShipControlState.ForwardButton; yaw = -0.8f; } // fwd + turn left
            /* else: straighten (yaw already 0) and coast (no thrust) */
            c.CommandControls(new ControlData(yaw, 0f, 0f, 0f), stateM);
            Thread.Sleep(300);
        }
    }

    // ── persistent control state (sticky, mirrors what the sim expects) ─────
    private static float _yaw, _pitch, _roll;
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
        ForceCompanionSameSide(server);   // both ships on side 0 (see helper's why)
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

        // Second, same-side, auto-piloted ship to observe. Connect it on the
        // same server, wait for its own assignment + hull, then buy a real
        // non-hull-only loadout — P86's fix means the companion's mounted parts
        // land in OUR world on the next export (visible in `who` as parts=[...]),
        // the live proof that fix reaches an interactive session, not just the
        // automated test suite.
        using var companion = new ConnectionSession();
        companion.Connect(bound, "explorer_companion");
        companion.SendLogonRequest(Request("explorer_companion"));
        const int MaxCompanionTicks = 8000;
        int cpt = 0;
        while (companion.World.MyShipId is null && cpt < MaxCompanionTicks) { server.TickSim(); cpt++; Thread.Sleep(1); }
        if (companion.World.MyShipId is null) { Console.Error.WriteLine("[explorer] FAILED: companion got no ship"); return 1; }
        short companionId = companion.World.MyShipId.Value;
        if (!companion.World.Ships.TryGetValue(companionId, out var ce) || ce.HullId == 0)
        { Console.Error.WriteLine("[explorer] FAILED: companion hull never learned"); return 1; }
        short companionHull = ce.HullId;
        var companionPart = RealWeaponPart(RepresentativeCore());
        companion.SendBuyLoadout(companionHull, fLaunch: false, parts: new[] { companionPart });
        Console.WriteLine($"[explorer] companion {companionId} online (hull {companionHull}), loadout requested");

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

        // Companion autopilot on its own thread (kept off the tick thread so a
        // multi-second coast can't stall the server's real-time drain).
        var companionThread = new Thread(() => CompanionAutopilot(companion, stopTicking.Token))
            { IsBackground = true, Name = "explorer-companion" };
        companionThread.Start();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopTicking.Cancel(); };

        Console.WriteLine("[explorer] connected. Type 'help' for commands, 'quit' to exit.");
        PrintStatus(session.World);

        RunRepl(session, stopTicking.Token);

        stopTicking.Cancel();
        tickThread.Join(TimeSpan.FromSeconds(2));
        companionThread.Join(TimeSpan.FromSeconds(1));
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
                case "stations":
                    PrintStations(session.World);
                    break;
                case "warps":
                    PrintWarps(session.World);
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
                case "roll":
                    ApplyRoll(session, rest);
                    break;
                case "strafe":
                    ApplyStrafe(session, rest);
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
              status | s            show current ship/world snapshot (incl. our up vector)
              who | ships           list every known ship (hull, loadout, distance from you)
              stations             list every known station (sector, distance from you)
              warps                list every known warp (sector, destination, distance from you)
              thrust <fwd|back|stop>  set forward/backward thrust (sticky until changed)
              turn <left|right|straight>  set yaw (sticky)
              pitch <up|down|level> set pitch (sticky)
              roll <left|right|level>  set roll (sticky; visible in status' up vector)
              strafe <left|right|up|down|stop>  set lateral/vertical manual thrust (two independent axes, sticky)
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

    private static void ApplyRoll(ConnectionSession session, string arg)
    {
        switch (arg.ToLowerInvariant())
        {
            case "left" or "l": _roll = -1f; break;
            case "right" or "r": _roll = 1f; break;
            case "level" or "center" or "0" or "": _roll = 0f; break;
            default:
                Console.WriteLine("[explorer] usage: roll <left|right|level>");
                return;
        }
        Push(session);
        Console.WriteLine($"[explorer] roll -> {_roll}");
    }

    // strafe shares the manual-control branch (`MoveButtonsMask`) with thrust,
    // so it must compose rather than clobber thrust/fire bits. Two independent
    // sub-axes, mirroring how ExecuteMove sums them: `left|right` (lateral,
    // ship-X) and `up|down` (vertical, ship-Y). Setting one sub-axis clears
    // only its opposite member and leaves the other sub-axis plus thrust and
    // fire untouched — `strafe left` then `strafe up` = left-and-up at once.
    private static void ApplyStrafe(ConnectionSession session, string arg)
    {
        const int LateralBits = ShipControlState.LeftButton | ShipControlState.RightButton;
        const int VerticalBits = ShipControlState.UpButton | ShipControlState.DownButton;
        switch (arg.ToLowerInvariant())
        {
            case "left" or "l":
                _stateM &= ~LateralBits;
                _stateM |= ShipControlState.LeftButton;
                break;
            case "right" or "r":
                _stateM &= ~LateralBits;
                _stateM |= ShipControlState.RightButton;
                break;
            case "up" or "u":
                _stateM &= ~VerticalBits;
                _stateM |= ShipControlState.UpButton;
                break;
            case "down" or "d":
                _stateM &= ~VerticalBits;
                _stateM |= ShipControlState.DownButton;
                break;
            case "stop" or "":
                _stateM &= ~(LateralBits | VerticalBits);
                break;
            default:
                Console.WriteLine("[explorer] usage: strafe <left|right|up|down|stop>");
                return;
        }
        Push(session);
        Console.WriteLine($"[explorer] strafe -> {arg}");
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
        session.CommandControls(new ControlData(_yaw, _pitch, _roll, 0f), _stateM);

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
        // Our own position (for distance). "ME" is shown via IsMine, not by
        // matching the name. Best-effort: unknown until we have a Latest.
        Vector3? me = w.MyShipId is { } mid && w.Ships.TryGetValue(mid, out var mine) ? mine.Latest?.Position : null;

        foreach (var (id, s) in w.Ships.OrderBy(kv => kv.Key))
        {
            string who = s.IsMine ? "ME" : (s.CharacterName.Length > 0 ? s.CharacterName : "?");
            string pos = s.Latest is { } u ? $"{u.Position.X:F0},{u.Position.Y:F0},{u.Position.Z:F0}" : "-";
            string dist = (me is { } mp && s.Latest is { } su) ? (su.Position - mp).Length().ToString("F1") : "-";
            string parts = s.Loadout is null ? "parts=?"
                       : (s.Loadout.Count == 0 ? "parts=hull-only"
                       : $"parts=[{string.Join(",", s.Loadout.Select(p => p.PartId))}]");
            Console.WriteLine($"  [{id}] {who,-10} side={s.SideId} sector={s.SectorId} hull={s.HullId} pos={pos} dist={dist} {parts} dead={s.IsDead}");
        }
    }

    // Distance from our own ship, same-sector-only. Across independent
    // per-sector coordinate spaces a number would be meaningless, so when the
    // object sits in a DIFFERENT sector (or we don't yet know our own), print
    // an honest placeholder instead of computing garbage. Style follows `who`'s
    // dist column (Vector3.Length of the position delta, F1).
    private static string DistanceLabel(Vector3? me, short? mySector, short objSector, float[]? objPos)
    {
        if (mySector is not { } sec || objSector != sec) return "(other sector)";
        if (objPos is not { Length: 3 } p) return "-";       // same sector, no position
        if (me is not { } mp) return "-";                    // same sector, no own position yet
        return (new Vector3(p[0], p[1], p[2]) - mp).Length().ToString("F1");
    }

    private static void PrintStations(ClientWorld w)
    {
        if (w.Stations.Count == 0) { Console.WriteLine("[explorer] no stations known"); return; }
        Vector3? me = w.MyShipId is { } mid && w.Ships.TryGetValue(mid, out var mine) ? mine.Latest?.Position : null;
        short? mySector = w.MyShipId is { } sid && w.Ships.TryGetValue(sid, out var entity) ? entity.SectorId : null;

        foreach (var (id, st) in w.Stations.OrderBy(kv => kv.Key))
            Console.WriteLine($"  [{id}] {st.Name} sector={st.ClusterId} dist={DistanceLabel(me, mySector, st.ClusterId, st.Position)}");
    }

    private static void PrintWarps(ClientWorld w)
    {
        if (w.Warps.Count == 0) { Console.WriteLine("[explorer] no warps known"); return; }
        Vector3? me = w.MyShipId is { } mid && w.Ships.TryGetValue(mid, out var mine) ? mine.Latest?.Position : null;
        short? mySector = w.MyShipId is { } sid && w.Ships.TryGetValue(sid, out var entity) ? entity.SectorId : null;

        foreach (var (id, wp) in w.Warps.OrderBy(kv => kv.Key))
            Console.WriteLine($"  [{id}] {wp.Name} sector={wp.ClusterId} dest={wp.DestinationId} dist={DistanceLabel(me, mySector, wp.ClusterId, wp.Position)}");
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
            $"up={(me?.Latest is { } o ? $"{o.Orientation.GetUp().X:F1},{o.Orientation.GetUp().Y:F1},{o.Orientation.GetUp().Z:F1}" : "-")} " +
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
