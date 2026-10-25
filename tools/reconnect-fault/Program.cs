using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Tools.ReconnectFault;

/// <summary>
/// P813c fault injection — a local showcase that proves the reconnect grace under
/// real socket death: an in-process server (reconnect grace ON) + N real
/// <see cref="ConnectionSession"/> clients dialing THROUGH a kill-switch proxy,
/// then every bot socket is killed mid-flight and the run checks that all N bots
/// resumed the SAME seat inside the window.
///
/// <para>This is the "kill N bot sockets mid-battle" run the task asks for; the
/// 200v200 rig decision (whether the feature is KEPT) is made by Claude on the
/// M4 Max with <c>spawn_scenario</c> preset <c>showcase</c> — see the report's rig
/// recipe. Exit code 0 only when every kill round resumed every bot.</para>
///
/// <code>
///   dotnet run --project tools/reconnect-fault -c Release -- --bots 12 --grace 5 --rounds 2
/// </code>
/// </summary>
internal static class Program
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static int Main(string[] args)
    {
        int bots = Option(args, "--bots", 8);
        int rounds = Option(args, "--rounds", 2);
        double grace = DoubleOption(args, "--grace", 5);
        string outPath = Option(args, "--out", "");
        string namePrefix = "p813c-bot-";

        using var server = new Srv();
        server.ShipSpawner = (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.Where(h => h.MaxEnergy > 0 && h.Hardpoints.Length > 0).OrderBy(h => h.HullId).First();
            var pos = new Vector3(200f + 120f * (id % 24), 0f, 400f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            return (ship, mission.Sectors[0]);
        };
        server.Boot(new Cfg
        {
            ArtPath = ArtRoot,
            StaticCore = "PCore001.igc",
            Port = 0,
            UpdatesPerSecond = 20,
            SideSelectAutoSeatSeconds = 30,   // peers that never send POSITIONREQ still seat
            Reconnect = new global::Allegiance.Server.ReconnectConfig { Enabled = true, GraceSeconds = grace },
            Games = [new Gm { Name = "P813c Fault", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
        });
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var proxy = new KillSwitchProxy(server.BoundPort);
        var sessions = new List<ConnectionSession>();
        var ships = new List<short>();

        for (int i = 0; i < bots; i++)
        {
            string name = namePrefix + i;
            var s = new ConnectionSession();
            s.EnableAutoReconnect(grace);
            s.Connect(proxy.Port, name);
            s.SendLogonRequest(Request(name));
            sessions.Add(s);
        }
        if (!Wait(server, () => sessions.All(s => s.Ack is { FValidated: true }), 20000))
        {
            Console.WriteLine("[fault] FAIL: not every bot got a validated LOGONACK");
            return 1;
        }
        foreach (var s in sessions) s.SendPositionReq(0);
        if (!Wait(server, () => sessions.All(s => { lock (s.Gate) return s.World.MyShipId is not null; }), 30000))
        {
            Console.WriteLine("[fault] FAIL: not every bot was seated");
            return 1;
        }
        foreach (var s in sessions) { lock (s.Gate) ships.Add(s.World.MyShipId!.Value); }
        Console.WriteLine($"[fault] {sessions.Count} bots seated: [{string.Join(", ", ships)}] — serverReconnectEnabled={server.ReconnectGraceEnabled}");

        // Fly for a moment so the kills land "mid-battle" rather than at t=0.
        Pump(server, 20);
        foreach (var s in sessions) s.CommandControls(new ControlData(0.15f, 0f, 0f, 0.8f), 0);
        Pump(server, 20);

        var report = new List<RoundResult>();
        bool allGood = true;
        for (int round = 1; round <= rounds; round++)
        {
            foreach (var s in sessions) s.CommandControls(new ControlData(0.2f, 0f, 0f, 0.85f), 0);
            Pump(server, 10);
            proxy.DropAll();
            int before = sessions.Sum(s => s.ResumeSuccesses);
            bool allResumed = Wait(server, () =>
                sessions.All(s => !s.Reconnecting && !s.Lost)
                && sessions.All(s => s.ResumeSuccesses > 0), (int)(grace * 1000) + 6000);
            bool sameShips = true;
            for (int i = 0; i < sessions.Count; i++)
            {
                short? now;
                lock (sessions[i].Gate) now = sessions[i].World.MyShipId;
                if (now != ships[i]) sameShips = false;
                Pump(server, 1);
            }
            int resumed = sessions.Sum(s => s.ResumeSuccesses) - before;
            bool ok = allResumed && sameShips && resumed == sessions.Count;
            allGood &= ok;
            report.Add(new RoundResult(round, sessions.Count, resumed, sameShips, ok));
            Console.WriteLine($"[fault] round {round}: killed {sessions.Count} sockets — resumed {resumed}/{sessions.Count}, sameSeat={sameShips}, ok={ok}");
            Pump(server, 10);
        }

        var summary = new
        {
            task = "P813c",
            bots = sessions.Count,
            graceSeconds = grace,
            rounds = report,
            serverParkedTotal = server.ReconnectParkedTotal,
            serverResumedTotal = server.ReconnectResumedTotal,
            serverExpiredTotal = server.ReconnectExpiredTotal,
            serverRefusedTotal = server.ReconnectRefusedTotal,
            allGood,
        };
        string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json);
        if (outPath.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            File.WriteAllText(outPath, json + "\n");
            Console.WriteLine($"[fault] wrote {outPath}");
        }
        foreach (var s in sessions) s.Dispose();
        return allGood ? 0 : 1;
    }

    private sealed record RoundResult(int Round, int Bots, int Resumed, bool SameSeat, bool Ok);

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 813u, DwCookie = 0xC0FFEEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 8813000000000000002ul, DrmHash = "p813c-fault",
    };

    private static void Pump(Srv server, int ticks)
    {
        for (int i = 0; i < ticks; i++) { server.TickSim(); Thread.Sleep(5); }
    }

    /// <summary>Wait for <paramref name="done"/> while PUMPING the in-process
    /// server — the tool owns the sim thread, so a handshake only completes while
    /// it ticks (same rule the test suites follow).</summary>
    private static bool Wait(Srv server, Func<bool> done, int maxMs)
    {
        var deadline = Environment.TickCount64 + maxMs;
        while (Environment.TickCount64 < deadline)
        {
            server.TickSim();
            if (done()) return true;
            Thread.Sleep(5);
        }
        return done();
    }

    private static int Option(string[] args, string key, int fallback) =>
        int.TryParse(Value(args, key), out var v) ? v : fallback;

    private static double DoubleOption(string[] args, string key, double fallback) =>
        double.TryParse(Value(args, key), out var v) ? v : fallback;

    private static string Value(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == key) return args[i + 1];
        return "";
    }

    private static string Option(string[] args, string key, string fallback) =>
        Value(args, key) is { Length: > 0 } v ? v : fallback;

    /// <summary>One loopback port in front of the server; <see cref="DropAll"/>
    /// kills every socket (client AND upstream) while still accepting reconnects,
    /// which is exactly a WAN blip: the client sees EOF, the server sees the peer
    /// leave and parks the seat.</summary>
    private sealed class KillSwitchProxy : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly ushort _targetPort;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _gate = new();
        private readonly List<TcpClient> _live = new();

        public KillSwitchProxy(ushort targetPort)
        {
            _targetPort = targetPort;
            _listener.Start();
            Port = (ushort)((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoop();
        }

        public ushort Port { get; }

        private async Task AcceptLoop()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch (Exception) { return; }
                _ = Forward(client);
            }
        }

        private async Task Forward(TcpClient client)
        {
            var upstream = new TcpClient();
            try { await upstream.ConnectAsync(IPAddress.Loopback, _targetPort, _cts.Token); }
            catch (Exception) { client.Dispose(); return; }
            lock (_gate) { _live.Add(client); _live.Add(upstream); }
            client.NoDelay = true;
            upstream.NoDelay = true;
            await Task.WhenAll(Pump(client, upstream), Pump(upstream, client));
            try { client.Dispose(); } catch { }
            try { upstream.Dispose(); } catch { }
        }

        private async Task Pump(TcpClient from, TcpClient to)
        {
            var buffer = new byte[16 * 1024];
            try
            {
                var src = from.GetStream();
                var dst = to.GetStream();
                while (!_cts.IsCancellationRequested)
                {
                    int n = await src.ReadAsync(buffer, _cts.Token);
                    if (n <= 0) return;
                    await dst.WriteAsync(buffer.AsMemory(0, n), _cts.Token);
                    await dst.FlushAsync(_cts.Token);
                }
            }
            catch (Exception) { /* socket died — the fault we injected */ }
        }

        public void DropAll()
        {
            TcpClient[] live;
            lock (_gate) { live = _live.ToArray(); _live.Clear(); }
            foreach (var c in live)
            {
                try { c.Client.Dispose(); } catch { }
                try { c.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            DropAll();
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
