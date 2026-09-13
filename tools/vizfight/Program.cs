using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
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
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    /// <summary>The spectator page's real source directory
    /// (<c>tools/vizfight/wwwroot/</c>), computed from the running
    /// assembly's own location rather than trusting the caller's working
    /// directory. <c>WebApplication.CreateBuilder</c>'s own default content
    /// root is <see cref="Directory.GetCurrentDirectory"/> — fine for
    /// <c>dotnet run</c> (whose CWD is the project directory) but wrong for
    /// launching the built DLL directly from anywhere else (the repo root,
    /// say — a natural thing to do), which silently 404s on <c>"/"</c> with
    /// no error, even though the page is right there. Confirmed live: 404
    /// with the SDK-driven Static Web Assets manifest lookup left to its own
    /// CWD-relative default, 200 once <c>WebRootPath</c> points here
    /// directly. Assumes the standard SDK-style build layout
    /// (<c>bin/&lt;Configuration&gt;/&lt;TFM&gt;/</c>, three levels below the
    /// project directory) — true for this project, not reconfigured.</summary>
    private static string ResolveWwwroot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"));

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
            // m2xspiderw.igc replaces XXV_simple.igc: same 13-cluster scale, but
            // XXV_simple has ZERO asteroids (confirmed via missiondump) while this
            // map has 134, real ore values, 44 warps fully resolved (0 dangling) --
            // needed so mining (P55/P56) has something real to demonstrate live.
            new Gm { Name = "Combat Demo", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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

    /// <summary>Every live model in the mission's sector model lists — the
    /// union of ships and all non-ship sim objects (projectiles, mines,
    /// missiles, chaff, treasures). The identical
    /// <c>Missions.SelectMany(m=&gt;m.Sectors).SelectMany(s=&gt;s.Models)</c>
    /// traversal as <see cref="AllShips"/>, but returning the raw
    /// <see cref="Model"/> sequence; the frame builders keep <c>ships</c>
    /// byte-identical and emit the rest through the P204 <c>models</c> array.
    /// </summary>
    private static IEnumerable<Model> AllModels(Srv server) =>
        server.Missions
            .SelectMany(m => m.Sectors)
            .SelectMany(s => s.Models);

    /// <summary>
    /// Serialize one non-ship model into the P204 <c>models</c> array entry.
    /// Every field is the real field the class actually carries (read, not
    /// guessed): <see cref="Projectile"/>/<see cref="Missile"/> expose
    /// <c>Position</c> + <c>Forward</c> and carry <b>no side field of their
    /// own</b>, so the side (for client colour-matching to the firing side) is
    /// derived from their <see cref="Projectile.Launcher"/> when it is a
    /// <see cref="Ship"/>; <see cref="Mine"/> carries <c>SideId</c> directly;
    /// <see cref="Chaff"/> needs only <c>Position</c> (short-lived, no
    /// meaningful orientation — <c>Chaff.cs:42</c> has no heading);
    /// <see cref="Treasure"/> exposes <c>Code</c> (so the client can tell a
    /// flag from cash/parts) and — for a flag treasure — <c>Data.TreasureId</c>
    /// <em>is</em> the owning side, the exact field P201's flag pickup reads
    /// (<c>Server.cs:8167</c>: <c>short oid = treasure.Data.TreasureId</c>).
    /// Only the kinds the brief enumerates are emitted; other model subtypes
    /// (e.g. <see cref="Probe"/>) are deliberately out of this task's scope.
    /// </summary>
    private static object SerializeModel(Model m) => m switch
    {
        Projectile p => new
        {
            id = p.Id, kind = "projectile",
            pos = ToArr(p.Position),
            fwd = ToArr(p.Forward),
            side = (p.Launcher as Ship)?.SideId ?? (short)-1,
        },
        Missile mi => new
        {
            id = mi.Id, kind = "missile",
            pos = ToArr(mi.Position),
            fwd = ToArr(mi.Forward),
            side = (mi.Launcher as Ship)?.SideId ?? (short)-1,
        },
        Mine mn => new
        {
            id = mn.Id, kind = "mine",
            pos = ToArr(mn.Position),
            side = mn.SideId ?? (short)-1,
        },
        Chaff c => new
        {
            id = c.Id, kind = "chaff",
            pos = ToArr(c.Position),
        },
        Treasure t => new
        {
            id = t.Id, kind = "treasure",
            pos = ToArr(t.Position),
            code = t.Code.ToString(),
            side = t.Data.TreasureId,   // a flag treasure's owning side (P201)
        },
        _ => throw new InvalidOperationException($"unserialized model kind {m.GetType().Name}"),
    };

    /// <summary>A fan-out hub between the sim thread (producer) and each
    /// connected WebSocket (consumer). One unbounded channel per client; the
    /// sim thread never blocks on a slow browser (TryWrite only), and each
    /// client's writer task drains at its own pace. Broadcast is the whole
    /// JSON frame as UTF-8. <see cref="Seeder"/> lets a mode feed one-time
    /// "catch-up" frames (P45's cached sector geometry) to each newly
    /// registered client so a spectator arriving after the lazy emission still
    /// gets the sectors it needs.</summary>
    private sealed class SpectatorHub
    {
        private readonly object _gate = new();
        private readonly List<Channel<byte[]>> _clients = [];

        /// <summary>Called synchronously inside <see cref="Register"/> for each
        /// newly-connected client, on the registration (WS) thread, so a fresh
        /// spectator catches up on one-time frames that were emitted before it
        /// arrived. Best-effort TryWrite — a slow/fresh browser still drains.</summary>
        public Action<Channel<byte[]>>? Seeder { get; set; }

        public Channel<byte[]> Register()
        {
            var ch = Channel.CreateUnbounded<byte[]>();
            lock (_gate) _clients.Add(ch);
            Seeder?.Invoke(ch);
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
        // P204 — every non-ship sim object in the world, tagged with a `kind`
        // discriminant (projectile/mine/missile/chaff/treasure) so the client
        // can pick a per-kind colour + geometry. Purely additive; `ships`
        // above is untouched and byte-for-byte identical to before.
        var models = AllModels(server)
            .Where(m => m is Projectile or Mine or Missile or Chaff or Treasure)
            .Select(SerializeModel)
            .ToArray();
        return JsonSerializer.Serialize(new { type = "tick", simTime = Round2(now), ships, models }, JsonOpts);
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
        // P45 explorer mode (any number of real peers, indefinitely, no weapons)
        // — a second spectator backend sharing the same WS/HTTP machinery.
        if (args.Length > 0 && args[0] == "explore")
        {
            int explorePort = WebPort;
            if (args.Length < 2 || !int.TryParse(args[1], out explorePort))
            {
                Console.Error.WriteLine("usage: vizfight explore [webPort]");
                Console.Error.WriteLine("  boots the Scout-exploration spectator backend: every real peer that joins");
                Console.Error.WriteLine("  gets a weapon-less Scout hull and streams per-tick position + sector geometry");
                Console.Error.WriteLine("  over a WebSocket. Waits for outside clients indefinitely (Ctrl-C to stop).");
                return 2;
            }
            return ExploreMain(explorePort);
        }

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
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby -- this tool commands bots directly, never sends PLAYER_READY (found and fixed while wiring in audio, P185-era)
        ushort bound = server.BoundPort;
        Console.WriteLine($"[viz] server transport listening on 127.0.0.1:{bound}");

        // ── the spectator hub + HTTP/WS host (Kestrel, minimal API) ──
        var hub = new SpectatorHub();
        // P204: expose every mission sector's static geometry (stations/
        // asteroids/warps) to the fight scenario too — the same
        // SectorMessageJson explore mode already sends (Program.cs:579), not
        // a second geometry format. Cached by sector id and replayed through
        // the Seeder to any client that registers after this one-time emit,
        // exactly as ExploreMain's sector cache behaves, so a spectator who
        // opens the page late still sees the fight's geometry.
        var sectorCache = new Dictionary<short, string>();
        hub.Seeder = ch =>
        {
            string[] cached;
            lock (sectorCache) cached = sectorCache.Values.ToArray();
            foreach (var json in cached)
                ch.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        };
        foreach (var s in server.Missions.SelectMany(m => m.Sectors))
        {
            var json = SectorMessageJson(s);
            lock (sectorCache) sectorCache[s.Id] = json;
            hub.Broadcast(json);
        }
        Console.WriteLine($"[viz] {sectorCache.Count} sector message(s) broadcast (stations/asteroids/warps)");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = ResolveWwwroot(),
        });
        builder.Logging.SetMinimumLevel(LogLevel.Error);   // silence framework chatter; our Console.WriteLine is the proof
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port));
        var app = builder.Build();
        app.UseWebSockets();
        app.UseDefaultFiles();                             // so "/" serves wwwroot/index.html (P43 advertised /)
        app.UseStaticFiles();                              // serves wwwroot/ (the spectator page)
        // Audio proof-of-concept: serve the real .ogg sound assets straight out
        // of ArtRoot (the same external, not-checked-into-git artwork tree every
        // other tool in this repo already reads from) under /audio, rather than
        // copying any file into wwwroot/the repo. Read-only, local-dev-only tool
        // — exposing the whole tree under one path is an accepted simplification
        // for this experiment, not a production posture.
        var audioContentTypes = new FileExtensionContentTypeProvider();
        audioContentTypes.Mappings[".ogg"] = "audio/ogg";   // default guess is video/ogg; be explicit
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(ArtRoot),
            RequestPath = "/audio",
            ContentTypeProvider = audioContentTypes,
        });
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

    // ── P45 explorer mode ────────────────────────────────────────────────

    /// <summary>Custom spawner for P45: every joining peer gets the Scout
    /// hull, <b>no weapon</b> (this is exploration, not combat — livecombat's
    /// Fight/MountWeapon logic is not reused at all), a round-robin
    /// <see cref="Ship.SideId"/> (P42's hard-won requirement: without one the
    /// server skips a real client's EXPORT(26) and the bot fails to hydrate
    /// before SET_CLUSTER), and a position/sector mirroring the default spawn
    /// (<c>SpawnDefaultShip</c>, Server.cs:1114-1121 — x staged by peer count
    /// in the mission's first sector). The seeding counter is ours (the
    /// ShipSpawner signature carries no peer-count), exactly as vizfight's own
    /// combat spawner counts placed ships — no lane/pairing concept here, just
    /// sequential joins.</summary>
    private static void InjectExplorerSpawn(Srv server)
    {
        var spawned = 0;
        var asteroidSeeded = new HashSet<short>(); // sectors that already got a synthetic rock
        server.ShipSpawner = (core, mission, id) =>
        {
            if (!core.HullTypes.Values.Any(h => h.Name.Contains("Scout")))
                throw new InvalidOperationException($"core {core.Version} has no Scout-named hull");
            var hull = core.HullTypes.Values.First(h => h.Name.Contains("Scout"));

            // ECONOMY-DEMO TUNING: every bot in this test harness is already
            // hardcoded to sideIndex=0 on the WOPR/native side
            // (MultiExploreTest.cs's own CreatePlayer call) -- the commander
            // needs to see the miner as one of its OWN side's ships, or
            // GetSide().GetShips() never finds it. A per-connection "first two
            // spawns" heuristic is NOT robust here: this counter is captured
            // once for the whole backend process's lifetime and never resets
            // between separate bot-swarm relaunches against the same running
            // backend, so it silently broke on the second relaunch. Simplest
            // correct fix: don't alternate sides at all for this spawner --
            // every bot lands on side 0, matching what the harness already
            // conceptually intends. (Real side-vs-side combat variety lives in
            // the *original* near-warp spawner this one replaced, if ever
            // needed again.)
            var side = mission.Sides.Count == 0 ? null : mission.Sides[0];

            // ECONOMY-DEMO TUNING (P51-P57 arc, live request: "there should be
            // a station and at least 1 asteroid" visible together, not spread
            // across sectors): spawn at THIS bot's own side's home station
            // instead of near a warp, so a docked-start bot (and the
            // commander's real dispatch) has something to see immediately.
            // Falls back to the near-warp spawn if the side has no station
            // (shouldn't happen on this map, but don't assume).
            var homeSector = side is null ? null
                : mission.Sectors.FirstOrDefault(s => s.Stations.Any(st => st.SideId == side.Id));
            var homeStation = homeSector?.Stations.FirstOrDefault(st => st.SideId == side!.Id);

            Sector sector;
            Vector3 pos;
            if (homeSector is not null && homeStation is { Position: { Length: 3 } sp })
            {
                sector = homeSector;
                var stationPos = new Vector3(sp[0], sp[1], sp[2]);
                var jitter = new Vector3((spawned % 4) * 30f - 45f, 0f, (spawned / 4) * 30f);
                pos = stationPos + jitter; // well inside DockRadius=200f -- bot starts docked

                // Inject one real He3 rock into this sector, once, ~400 units
                // from the station -- close enough to reach fast, far enough
                // to read as a separate object, not overlapping the station.
                if (asteroidSeeded.Add(sector.Id))
                {
                    var rockPos = stationPos + new Vector3(400f, 0f, 250f);
                    var data = new DataAsteroidIGC(
                        Signature: 1f, Position: [rockPos.X, rockPos.Y, rockPos.Z],
                        Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f], Rotation: [0f, 0f, 0f, 1f],
                        Ore: 6000f, OreMax: 6000f, AabmCapabilities: Asteroid.CapMineHe3,
                        AsteroidId: (short)(9000 + sector.Id), HitPoints: 1000f, Radius: 60,
                        ModelName: "he3rock", TextureName: "", IconName: "",
                        ClusterId: sector.Id, Name: "Demo He3 Deposit", Fraction: 0);
                    mission.PlaceAsteroid(new Asteroid(data), sector);
                    Console.WriteLine($"[explore] seeded a demo He3 asteroid in sector {sector.Id} '{sector.Name}', ~400 units from the station");
                }
            }
            else
            {
                // Fallback: original near-warp spawn (no station found for this side).
                sector = mission.Sectors[0];
                var warp = sector.Warps.First(w => w.Destination is not null);
                var wpos = new Vector3(warp.Warp.Position![0], warp.Warp.Position[1], warp.Warp.Position[2]);
                float[] f = warp.Warp.Forward is { Length: 3 } v && (v[0] * v[0] + v[1] * v[1] + v[2] * v[2]) > 0f
                    ? v : [1f, 0f, 0f];
                var dir = new Vector3(f[0], f[1], f[2]).Normalize();
                var jitter = new Vector3((spawned % 4) * 15f - 22.5f, 0f, (spawned / 4) * 15f);
                pos = wpos + dir * 150f + jitter;
            }

            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            if (side is not null)
            {
                ship.SideId = side.Id;               // P42: must assign SideId or real clients skip EXPORT(26)
                Console.WriteLine($"[explore] peer {spawned + 1} joined: ship {id}, hull {hull.Name}, side {side.Id}");
            }
            else
            {
                Console.WriteLine($"[explore] peer {spawned + 1} joined: ship {id}, hull {hull.Name}, side <none> (mission has no sides)");
            }
            mission.PlaceShip(ship, sector);
            spawned++;
            return (ship, sector);
        };
    }

    /// <summary>The one-time static-geometry message for a sector. Cached so a
    /// sector is serialized only the first time any ship is seen in it, never
    /// per tick. Reads only the sector's resolved warps (position + collision
    /// radius, the same fields P42's warptrial spawner read) and stations —
    /// nothing new is computed, and the sender keeps the sector open.
    /// destSector is -1 for a dangling edge (Reported, never fabricated).</summary>
    private static string SectorMessageJson(Sector s)
    {
        return JsonSerializer.Serialize(new
        {
            type = "sector",
            id = s.Id,
            name = s.Name,
            warps = s.Warps.Select(w => new
            {
                id = w.Warp.WarpId,
                pos = PosArr(w.Warp.Position),
                radius = (float)w.Warp.Radius,
                destSector = w.Destination?.Id ?? (short)-1,   // -1 for a dangle
            }),
            stations = s.Stations.Select(st => new
            {
                id = st.StationId,
                pos = PosArr(st.Position),
            }),
            // Position/radius are static (fine for this one-time geometry send,
            // same as warps/stations); oreFraction is a snapshot at first-sight
            // time, not live-updated as mining depletes it -- good enough to
            // show WHERE the rocks are, not a live depletion display.
            asteroids = s.Mineable.Select(a => new
            {
                id = a.Data.AsteroidId,
                pos = PosArr(a.Data.Position),
                radius = (float)a.Radius,
                oreFraction = a.OreFraction,
            }),
        }, JsonOpts);
    }

    private static float[] PosArr(float[]? v) =>
        v is { Length: 3 } p ? [p[0], p[1], p[2]] : [0f, 0f, 0f];

    /// <summary>P45's per-tick broadcast: the same ship fields as combat minus
    /// the impossibles (a weapon-less Scout can never fire, so there is no
    /// kill path — the P44 kill message is absent), plus the ship's live
    /// <see cref="Ship.Sector"/>.Id (per-tick-correct, maintained by P41's
    /// HitWarpEvent). hp stays — a Scout still carries a Fraction. The hull
    /// name was already in P43/P44's schema; it stays so the frontend never
    /// has to guess it's a Scout.</summary>
    private static string ExploreTickFrame(Srv server)
    {
        double now = server.SimNow;
        var ships = AllShips(server)
            .Select(s => new
            {
                id = s.Id,
                hull = s.HullType.Name,
                side = s.SideId ?? (short)-1,
                sector = s.Sector?.Id ?? (short)-1,
                pos = ToArr(s.Position),
                fwd = ToArr(s.Orientation.GetForward()),
                hp = (double)s.Fraction,
                alive = !s.Dead,
            })
            .ToArray();
        // P204 — same non-ship `models` array as the combat TickFrame, so
        // both scenarios export every live sim object consistently.
        var models = AllModels(server)
            .Where(m => m is Projectile or Mine or Missile or Chaff or Treasure)
            .Select(SerializeModel)
            .ToArray();
        return JsonSerializer.Serialize(new { type = "tick", simTime = Round2(now), ships, models }, JsonOpts);
    }

    /// <summary>Scan every ship this tick; lazily broadcast one-time sector
    /// geometry for any sector newly entered, caching the serialized message
    /// by sector id so it is never re-built or re-sent per tick no matter how
    /// many ships occupy the sector.</summary>
    private static void EmitNewSectors(Srv server, SpectatorHub hub, Dictionary<short, string> cache)
    {
        foreach (var sector in AllShips(server)
                     .Select(s => s.Sector)
                     .OfType<Sector>()
                     .DistinctBy(s => s.Id))
        {
            // Locked with the Seeder (client-connect) reader so the growing
            // cache can't race an enumerator on the WS thread.
            string json;
            lock (cache)
            {
                if (cache.ContainsKey(sector.Id)) continue;
                json = SectorMessageJson(sector);
                cache[sector.Id] = json;
            }
            hub.Broadcast(json);
        }
    }

    private static int ExploreMain(int port)
    {
        // Diagnostic: confirm the chosen hull really is a Scout before trusting
        // the stream (P40's IsCurrentShipAScout gate, ScoutExploreMap.cs:786)
        // — a wrong default here would silently drop every bot to ReturnToBase.
        var rCore = RepresentativeCore();
        var scout = rCore.HullTypes.Values.First(h => h.Name.Contains("Scout"));
        Console.WriteLine($"[explore] hull '{scout.Name}' (HullId {scout.HullId}) selected for all peers");

        using var server = new Srv();
        InjectExplorerSpawn(server);
        server.Boot(Config());
        ushort bound = server.BoundPort;
        Console.WriteLine($"[explore] server transport listening on 127.0.0.1:{bound}");

        var hub = new SpectatorHub();
        // One-time sector geometry, cached by sector id (value = serialized
        // message). Filled lazily by EmitNewSectors on the sim thread as ships
        // are first seen in a sector; flushed to each new client on connect so
        // a spectator arriving after the lazy emission still gets the geometry
        // (snapshot under the gate so the growing cache can't race an enumerator).
        var sectorCache = new Dictionary<short, string>();
        hub.Seeder = ch =>
        {
            string[] cached;
            lock (sectorCache) cached = sectorCache.Values.ToArray();
            foreach (var json in cached)
                ch.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = ResolveWwwroot(),
        });
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(port));
        var app = builder.Build();
        app.UseWebSockets();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.Map("/ws", (HttpContext c) => HandleWs(c, hub, c.RequestAborted));
        var appTask = app.RunAsync();
        Console.WriteLine($"[explore] spectator http://127.0.0.1:{port}/  ws://127.0.0.1:{port}/ws");
        Console.WriteLine("[explore] no bots spawned by this tool — waiting for OUTSIDE peers (real Wopr.exe from P46, or a loopback Bot for verification)");

        // P59: relay notable server moments (dock/undock/mine-start/mine-sell/
        // payday/warp crossings) to the spectator as {type="event"} messages.
        // Mirrors the combat kill broadcast's shape (a type discriminator +
        // simTime). Raised on the sim thread inside server.Tick() — the same
        // thread this loop ticks and broadcasts the ship frames on — so
        // hub.Broadcast is safe from here exactly as the kill site already is.
        server.Notable += text =>
        {
            var json = JsonSerializer.Serialize(
                new { type = "event", text, simTime = Round2(server.SimNow) },
                JsonOpts);
            hub.Broadcast(json);
        };

        // No fixed ship count, no end condition: accept however many peers join
        // (even zero for a while) and run until Ctrl-C'd.
        while (!Cancelled())
        {
            server.Tick();
            EmitNewSectors(server, hub, sectorCache);
            hub.Broadcast(ExploreTickFrame(server));
        }

        Console.WriteLine($"[explore] ran until cancelled; {sectorCache.Count} sector message(s) broadcast, websocket served at ws://127.0.0.1:{port}/ws");
        appTask.GetAwaiter().GetResult();
        return 0;
    }
}
