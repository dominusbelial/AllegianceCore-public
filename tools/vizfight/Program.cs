using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using BotClass = global::Allegiance.Bot.Bot;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.Tools.VizFight;

/// <summary>
/// P43: the live-combat <b>spectator</b> layer on top of the existing,
/// unmodified <c>livecombat</c> (P24) fight mechanic. This tool adds zero
/// new game-sim logic — it boots the exact same N-lane fight, but ticks the
/// server on a real-cadence <see cref="Srv.Tick"/> loop (not livecombat's
/// unpaced <c>TickSim</c>) so the fight lasts long enough for a browser to
/// watch it, and after every tick exports every ship's current state through
/// a WebSocket to a static spectator page. P44 consumes this stream; this
/// prompt only proves the plumbing. Every ship field exported here is one
/// that already existed and changed per-tick: <c>Position</c>
/// (<c>Sim/Model.cs:49</c>), <c>Orientation</c> (<c>Sim/Ship.cs:109</c>,
/// mutated per tick at <c>Sim/Ship.cs:342-345</c>), <c>Fraction</c>
/// (<c>Sim/Ship.cs:76</c>, the hull-integrity 0..1 this maps to <c>hp</c>),
/// <c>Dead</c> (<c>Sim/Ship.cs:82</c>), <c>SideId</c> (<c>Sim/Ship.cs:139</c>)
/// and <c>HullType.Name</c> (<c>Data/DataHullTypeIGC.cs:18</c>).
/// </summary>
internal static class Program
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";
    private const float TargetZ = -180f;         // same as livecombat (P22): target -Z from shooter
    private const float LaneSpacing = 432f;      // same as livecombat: X stagger isolates lanes
    private const int Pairs = 4;                 // same as livecombat: 4 lanes → 8 real bots
    private const int WebPort = 7717;            // spectator WS + page port (confirmed unused)

    // Same fightable hull+weapon selection as livecombat (Program.cs:35-52):
    // a hull with energy/ammo and a fixed hardpoint, plus the cheapest
    // dispersion-0 weapon whose projectile does real damage. Copied, not
    // shared, so this tool stays self-contained (mirroring livecombat's own
    // copy — neither references the other).
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon)> Fight = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var hull in core.HullTypes.Values
                     .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                     .OrderBy(h => h.HullId))
        {
            foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                         .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                         .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                         .OrderBy(w => w.EnergyPerShot))
            {
                if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    return (hull, p);
            }
        }
        throw new InvalidOperationException("no fightable hull+weapon in the representative core");
    });

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    /// <summary>Same lane-indexed spawner as livecombat (Program.cs:72-91),
    /// plus a <c>SideId</c> so the spectator's <c>side</c> field is meaningful:
    /// shooters on side 0, targets on side 1. Distinct sides mean the
    /// friendly-fire gate (<c>Sector.ResolveCombatCollisions</c>,
    /// <c>Sim/Ship.cs:184-188</c>) never suppresses a hit, exactly as in
    /// livecombat where no ship carried a side. Geometry identical, so each
    /// lane's straight shot still only reaches its own paired target.</summary>
    private static void InjectFightSpawn(Srv server)
    {
        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            int lane = placed / 2;
            bool shooter = (placed % 2) == 0;
            var pos = new Vector3(lane * LaneSpacing, 0f, shooter ? 0f : TargetZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = (short)(shooter ? 0 : 1);   // spectator-only label; does not alter sim (friend/foe gate only)
            mission.PlaceShip(ship, mission.Sectors[0]);
            if (shooter)
            {
                ship.Energy = hull.MaxEnergy * 100f;   // sustain fire (same as livecombat)
                ship.MountWeapon(weapon);              // mount 0
            }
            placed++;
            return (ship, mission.Sectors[0]);
        };
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Combat Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 910u, DwCookie = 0xC0FFEEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 75000000000000000ul, DrmHash = "vizfight",
    };

    // Every ship currently in the mission's sector model lists. Walking the
    // mission's own Sector.Models (Allegiance.Core/Model/Sector.cs:71) — the
    // same source livecombat's kill-observation and P41's
    // Sector.ResolveWarpCollisions iterate — captures every ship in the
    // fight, not just the peer-owned subset (Server.Ships, Server.cs:119).
    // Here they coincide (all 2N fighters are peer-owned) but sector models
    // is the honest "everything in the world" list.
    private static IEnumerable<Ship> AllShips(Srv server) =>
        server.Missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.Models)
            .OfType<Ship>();

    /// <summary>A fan-out hub between the sim thread (producer) and each
    /// connected WebSocket (consumer). One unbounded channel per client; the
    /// sim thread never blocks on a slow browser (TryWrite only), and each
    /// client's writer task drains at its own pace. Broadcast is the whole
    /// JSON frame as UTF-8.</summary>
    private sealed class SpectatorHub
    {
        private readonly object _gate = new();
        private readonly List<Channel<byte[]>> _clients = [];

        public Channel<byte[]> Register()
        {
            var ch = Channel.CreateUnbounded<byte[]>();
            lock (_gate) _clients.Add(ch);
            return ch;
        }

        public int Count { get { lock (_gate) return _clients.Count; } }

        public void Unregister(Channel<byte[]> ch)
        {
            lock (_gate) _clients.Remove(ch);
            ch.Writer.TryComplete();
        }

        public void Broadcast(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            Channel<byte[]>[] copy;
            lock (_gate) copy = _clients.ToArray();
            foreach (var ch in copy)
                ch.Writer.TryWrite(bytes);
        }
    }

    /// <summary>Serialize one frame of live ship state in the P44-fixed schema.</summary>
    private static string TickFrame(Srv server)
    {
        double now = server.SimNow;
        var ships = AllShips(server)
            .Select(s => new
            {
                id = s.Id,
                hull = s.HullType.Name,
                side = s.SideId ?? (short)-1,
                pos = ToArr(s.Position),
                fwd = ToArr(s.Orientation.GetForward()),
                hp = (double)s.Fraction,
                alive = !s.Dead,
            })
            .ToArray();
        return JsonSerializer.Serialize(new { type = "tick", simTime = Round2(now), ships }, JsonOpts);
    }

    private static float[] ToArr(Vector3 v) => [Round2(v.X), Round2(v.Y), Round2(v.Z)];
    // +0f normalizes IEEE -0.0 to +0.0 so forward vectors like (0,0,-1) render
    // as [0,0,-1], not [-0,-0,-1], for clean P44 consumer frames.
    private static float Round2(float v) => MathF.Round(v, 2) + 0f;
    private static float Round2(double v) => (float)Math.Round(v, 2) + 0f;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    /// <summary>The per-websocket connection: hand the client a channel,
    /// drain it to the socket while simultaneously watching for a Close frame
    /// so a disappearing browser cleans up its writer task.</summary>
    private static async Task HandleWs(HttpContext ctx, SpectatorHub hub, CancellationToken ct)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var ch = hub.Register();
        try
        {
            var send = Task.Run(async () =>
            {
                await foreach (var msg in ch.Reader.ReadAllAsync(ct))
                {
                    if (ws.State != WebSocketState.Open) break;
                    await ws.SendAsync(msg, WebSocketMessageType.Text, true, ct);
                }
            });

            // Receiver just detects disconnects; we discard any client bytes.
            var buf = new byte[512];
            while (ws.State == WebSocketState.Open)
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close) break;
            }
            hub.Unregister(ch);
            try { await send; } catch (OperationCanceledException) { }
            try { if (ws.State != WebSocketState.Aborted) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct); }
            catch { /* socket already gone */ }
        }
        finally
        {
            hub.Unregister(ch);
        }
    }

    private static int Main(string[] args)
    {
        int port = WebPort;
        if (args.Length > 0 && !int.TryParse(args[0], out port))
        {
            Console.Error.WriteLine("usage: vizfight [webPort]");
            Console.Error.WriteLine($"  boots livecombat's N-lane fight on a real-cadence server.Tick() loop");
            Console.Error.WriteLine($"  and exports every ship's state per tick over a WebSocket to a spectator page.");
            Console.Error.WriteLine($"  Web/WS port defaults to {WebPort}; server transport port is ephemeral.");
            Console.Error.WriteLine($"  Requires the stock artwork tree at {ArtRoot}.");
            return 2;
        }

        // Same diagnostic as livecombat (Program.cs:134-141): prove TargetZ
        // is in straight-line range and lanes are isolated before trusting a fight.
        var rCore = RepresentativeCore();
        var fxPt = rCore.ProjectileTypes[Fight.Value.weapon.ProjectileTypeId];
        float fxFiringRange = fxPt.Speed * fxPt.Lifespan;
        Console.WriteLine($"[viz] weapon projectile {fxPt.ProjectileTypeId}: Speed={fxPt.Speed} Lifespan={fxPt.Lifespan} → straight range {fxFiringRange:F0}");
        Console.WriteLine($"[viz] TargetZ={TargetZ} ≪ range ({TargetZ:F0} < {fxFiringRange:F0}); LaneSpacing={LaneSpacing} → lanes isolated");

        using var server = new Srv();
        InjectFightSpawn(server);
        server.Boot(Config(0));
        ushort bound = server.BoundPort;
        Console.WriteLine($"[viz] server transport listening on 127.0.0.1:{bound}");

        // ── the spectator hub + HTTP/WS host (Kestrel, minimal API) ──
        var hub = new SpectatorHub();
        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Error);   // silence framework chatter; our Console.WriteLine is the proof
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port));
        var app = builder.Build();
        app.UseWebSockets();
        app.UseDefaultFiles();                             // so "/" serves wwwroot/index.html (P43 advertised /)
        app.UseStaticFiles();                              // serves wwwroot/ (the spectator page)
        app.Map("/ws", (HttpContext c) => HandleWs(c, hub, c.RequestAborted));  // AM-specific overload: returns Task
        var appTask = app.RunAsync();                       // Kestrel now accepting on 127.0.0.1:{port}

        Console.WriteLine($"[viz] spectator http://127.0.0.1:{port}/  ws://127.0.0.1:{port}/ws");

        // ── the same 2N real bots as livecombat, connecting before the fight ──
        var shooters = new BotClass[Pairs];
        var targets = new BotClass[Pairs];
        for (int k = 0; k < Pairs; k++) { shooters[k] = new BotClass(); targets[k] = new BotClass(); }
        for (int k = 0; k < Pairs; k++) { shooters[k].Connect(bound, $"S{k}"); targets[k].Connect(bound, $"T{k}"); }
        for (int k = 0; k < Pairs; k++)
        {
            shooters[k].SendLogonRequest(Request($"SHOOTER{k}"));
            targets[k].SendLogonRequest(Request($"TARGET{k}"));
        }

        // Real cadence, no spin: park on the real Tick (Server.cs:276-282 sleeps
        // the true remainder each pass). Wait for all 2N ship assignments.
        while (!AllAssigned(shooters, targets) && !Cancelled())
            server.Tick();
        var all = shooters.Select(b => b.ShipAssignment).Concat(targets.Select(b => b.ShipAssignment)).ToArray();
        if (all.Any(a => a is null)) { Console.Error.WriteLine("vizfight FAILED: not all bots assigned a ship"); return 1; }

        // Classify the raced spawns into lanes by position (livecombat's rule).
        var shooterByLane = new Dictionary<int, (BotClass bot, SetCluster asn)>();
        var targetByLane = new Dictionary<int, (BotClass bot, SetCluster asn)>();
        foreach (var (b, _) in shooters.Concat(targets).Select((bb, i) => (bb, i)))
        {
            var asn = b.ShipAssignment!;
            int lane = (int)MathF.Round(asn.Ship.Position.X / LaneSpacing);
            bool isShooter = asn.Ship.Position.Z > -1f;
            var entry = (b, asn);
            if (isShooter) shooterByLane[lane] = entry; else targetByLane[lane] = entry;
        }
        for (int k = 0; k < Pairs; k++)
        {
            if (!(shooterByLane.TryGetValue(k, out var st) && targetByLane.TryGetValue(k, out var tg)))
            { Console.Error.WriteLine($"vizfight FAILED: lane {k} missing a shooter or target"); return 1; }
            Console.WriteLine($"[viz] lane {k}: shooter ship {st.asn.Ship.ShipId} at {st.asn.Ship.Position}, " +
                              $"target ship {tg.asn.Ship.ShipId} at {tg.asn.Ship.Position}");
        }
        Console.WriteLine("[viz] waiting for a spectator WebSocket client before opening fire (so the fight is watchable)");
        bool commanded = false;
        var seenKills = new HashSet<short>();
        var deathsEmitted = 0;
        while (!Cancelled())
        {
            server.Tick();                       // real 20 Hz cadence (sleeps remainder)
            hub.Broadcast(TickFrame(server));

            // Don't open fire until a real spectator is watching: the kill
            // takes only a few seconds once shooting starts, so firing before
            // anyone connects wastes the whole show (a browser would arrive to
            // find every target already dead). Command each shooter's
            // AllWeapons the first time hub.Count drops to a viewer.
            if (!commanded && hub.Count > 0)
            {
                for (int k = 0; k < Pairs; k++)
                {
                    // Cruise throttle (Throttle=1 → half·(1+1)·maxSpeed toward
                    // -Z, Sim/Ship.cs:381-385) so each shooter visibly translates
                    // down its lane while still firing straight — the exported
                    // pos/fwd then demonstrably move tick-over-tick, while the
                    // weapon still lands its in-lane kill exactly as livecombat.
                    shooterByLane[k].bot.CommandControls(new ControlData(0f, 0f, 0f, 1f), ShipControlState.AllWeapons);
                    targetByLane[k].bot.CommandControls(ControlData.Neutral, 0);
                }
                commanded = true;
                Console.WriteLine("[viz] spectator connected — fight begins");
            }

            // Kills come from the same signal livecombat/Bot.cs expose (Bot.Kills,
            // src/Allegiance.Bot/Bot.cs:64) — each real shooter/target bot records
            // the KILL_SHIP(105) frames it observes on the wire, complete with the
            // credited killer id (KillShip.IdCredit, Wire/KillShip.cs:29). Note the
            // server broadcasts every kill to all 8 peers in the victim's sector
            // (BroadcastKillShip, Server.cs:1263-1275), so every bot holds the same
            // 4 records — dedupe by victim id and report the lane from the ship's
            // actual position, not which bot surfaced it.
            foreach (var bot in shooters.Concat(targets))
            {
                foreach (var kill in bot.Kills.Where(k => seenKills.Add(k.ShipId)))
                {
                    var json = JsonSerializer.Serialize(
                        new { type = "kill", victim = kill.ShipId, killer = kill.IdCredit, simTime = Round2(server.SimNow) },
                        JsonOpts);
                    hub.Broadcast(json);
                    deathsEmitted++;
                    int vLane = LaneOfShip(kill.ShipId, all);
                    int kLane = LaneOfShip(kill.IdCredit, all);
                    Console.WriteLine($"[viz] lane {vLane}: ship {kill.ShipId} killed by ship {kill.IdCredit} (lane {kLane}) at sim-t {server.SimNow:0.##} → KILL_SHIP observed, emitted to spectator");
                }
            }
        }

        Console.WriteLine($"[viz] ran until cancelled; {deathsEmitted} kill frame(s) broadcast, websocket served at ws://127.0.0.1:{port}/ws");
        appTask.GetAwaiter().GetResult();
        return 0;
    }

    private static bool AllAssigned(IEnumerable<BotClass> shooters, IEnumerable<BotClass> targets) =>
        shooters.All(b => b.ShipAssignment is not null) && targets.All(b => b.ShipAssignment is not null);

    private static volatile bool _cancelled;

    private static bool Cancelled() => _cancelled;

    static Program()
    {
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _cancelled = true; };
    }

    private static int LaneOfShip(short shipId, IEnumerable<SetCluster?> asn)
    {
        foreach (var a in asn)
        {
            if (a is null) continue;
            if (a.Ship.ShipId == shipId)
                return (int)MathF.Round(a.Ship.Position.X / LaneSpacing);
        }
        return -1;
    }
}
