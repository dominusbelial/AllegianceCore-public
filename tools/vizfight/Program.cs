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
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using BotClass = global::Allegiance.Bot.Bot;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.Core.Content;

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
    private static string ResolveWwwroot()
    {
        // A published (e.g. Docker) layout is flat: wwwroot sits right next to
        // the assembly. A local `dotnet run`/`bin/Release/net10.0` dev layout
        // needs the old three-levels-up walk back to the project root instead.
        var flat = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(flat)) return flat;
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot"));
    }

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
        UpdatesPerSecond = 60, // 20 default -> 30 -> 60, each step confirmed live as a
                               // further improvement on the acceleration-teleport
                               // artifact (raw/unsmoothed rendering, see the
                               // RemoteShipPredictor removal a8b54af/d77057b) — 60Hz
                               // held clean through 100/150/200-bot stress tests with
                               // no CPU jump over 30Hz in this environment. Only
                               // changed for this dev-testing tool; ServerConfig's own
                               // default (20) is untouched pending a real bandwidth/CPU
                               // profiling pass (see allegiance-monogame-client-backlog
                               // wiki page).
        Games =
        [
            // m2xspiderw.igc replaces XXV_simple.igc: same 13-cluster scale, but
            // XXV_simple has ZERO asteroids (confirmed via missiondump) while this
            // map has 134, real ore values, 44 warps fully resolved (0 dangling) --
            // needed so mining (P55/P56) has something real to demonstrate live.
            new Gm { Name = "Combat Demo", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    /// <summary>Showcase-specific config: the same m2xspiderw map (same static
    /// geometry path P204 already renders) but with THREE teams so the real
    /// alliance mechanic can actually form one — the server's
    /// <c>UpdateAlliances</c> only builds alliances when ≥3 active sides exist
    /// (Server.cs:5453), so the two-team <see cref="Config"/> can never ally.
    /// Alpha and Bravo ally diplomatically; the skirmish is Alpha vs Gamma so
    /// the friend/foe gate still permits a real kill.</summary>
    private static Cfg ShowcaseConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Showcase", Map = "m2xspiderw.igc", Teams = 3,
                     CivIds = [1, 3, 4], SideNames = ["Alpha", "Bravo", "Gamma"] },
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

        // P205 showcase mode — a third, independent entry point: a scripted,
        // multi-phase trailer scenario (ready-up -> launch -> combat -> flag
        // capture -> economy -> leadership vote -> docking) run through the same
        // real server + real Bot peers + same WS/HTTP machinery. Additive: does
        // not touch Main()/ExploreMain().
        if (args.Length > 0 && args[0] == "showcase")
        {
            int showcasePort = WebPort;
            if (args.Length < 2 || !int.TryParse(args[1], out showcasePort))
            {
                Console.Error.WriteLine("usage: vizfight showcase [webPort]");
                Console.Error.WriteLine("  boots the scripted trailer scenario (real server, real Bot peers):");
                Console.Error.WriteLine("  ready-up countdown, launch w/ real loadout, formation flight, skirmish w/ a");
                Console.Error.WriteLine("  real kill, a real flag pickup + secure-by-docking, real economy donations,");
                Console.Error.WriteLine("  a real leadership (mutiny) vote, and a docking beat -- narrated per beat over");
                Console.Error.WriteLine("  the WebSocket `event` channel for the spectator page.");
                return 2;
            }
            return ShowcaseMain(showcasePort);
        }

        // P305 roster mode — a fourth entry point: a real 9-faction server with
        // one real Bot per distinct P287 ship model (72 bots), each launched via
        // the real SendBuyLoadout wire path and parked in a faction-grouped grid
        // in one test sector, so a single screenshot can show the whole roster.
        if (args.Length > 0 && args[0] == "roster")
        {
            int rosterPort = WebPort;
            if (args.Length < 2 || !int.TryParse(args[1], out rosterPort))
            {
                Console.Error.WriteLine("usage: vizfight roster [webPort] [civ]");
                Console.Error.WriteLine("  boots a 9-faction server + one real Bot per distinct ship model (72 in PCore001),");
                Console.Error.WriteLine("  each launched via SendBuyLoadout and parked in a faction-grouped grid. Writes");
                Console.Error.WriteLine("  roster_dump.json (asset/civ/hull/ship-id/grid-pos per bot) for the capture harness,");
                Console.Error.WriteLine("  and serves the spectator page over a WebSocket. Ctrl-C to stop.");
                Console.Error.WriteLine("  optional [civ] (e.g. 'bios') restricts the bot spawn to just that faction's");
                Console.Error.WriteLine("  hulls — the server still seats all 9 factions, only the bot roster is filtered.");
                return 2;
            }
            string? civFilter = args.Length > 2 ? args[2] : null;
            return RosterMain(rosterPort, civFilter);
        }

        // P338 proof mode — a sixth entry point: a single-team scenario for the
        // display-constant live-proof captures (chase framing of a small fighter
        // OR large carrier viewer, a real warp beside the ship, a missile
        // exhaust trail, damage-debris smoke). Prints TRANSPORT_PORT=N.
        if (args.Length > 0 && args[0] == "proof")
        {
            int proofPort = WebPort;
            if (args.Length < 2 || !int.TryParse(args[1], out proofPort))
            {
                Console.Error.WriteLine("usage: vizfight proof [webPort] [viewerHullName]");
                Console.Error.WriteLine("  boots a single-team server + two companion bots (missile gunner + damaged victim),");
                Console.Error.WriteLine("  parks the viewer next to a real warp, prints TRANSPORT_PORT=N, and runs until Ctrl-C.");
                Console.Error.WriteLine("  viewerHullName (optional substring, e.g. Fighter/Carrier) picks the viewer's hull.");
                return 2;
            }
            string? vh = args.Length > 2 ? args[2] : null;
            return ProofMain(proofPort, vh);
        }

        // P306 stress mode — a fifth entry point: boot the real server at a
        // given tick rate (updatesPerSecond), spawn a given bot count on a
        // SINGLE team (side 0) in a grid formation, and command them all to
        // cruise continuously for a fixed wall-clock window (so the motion is
        // sustained long enough to capture real footage), logging real per-tick
        // timing + overruns. Unlike the fight/showcase modes there is no PvP:
        // all bots share one side so the MonoGame capture client (which only
        // ever learns SAME-side ships over the wire) can render ALL of them
        // moving — the exact "how smooth is N ships on screen" scale this task
        // measures. Reuses the same real server + real Bot peers machinery as
        // every other mode; this is not a second spawn path.
        if (args.Length > 0 && args[0] == "stress")
        {
            if (args.Length < 4 || !int.TryParse(args[1], out var stressWebPort)
                || !int.TryParse(args[2], out var stressBots)
                || !int.TryParse(args[3], out var stressUps))
            {
                Console.Error.WriteLine("usage: vizfight stress <webPort> <bots> <ups> [runtimeSec] [transportPort]");
                Console.Error.WriteLine("  boots the real server at <ups> upd/s, spawns <bots> real Bot peers on one");
                Console.Error.WriteLine("  team in a grid, commands them to cruise continuously for [runtimeSec]");
                Console.Error.WriteLine("  (default 90) wall seconds, then exits printing real per-tick timing.");
                Console.Error.WriteLine("  transportPort 0 (default) = ephemeral; the bound port is printed as TRANSPORT_PORT=N.");
                return 2;
            }
            double runtime = args.Length > 4 && double.TryParse(args[4], out var rt) ? rt : 90.0;
            int tp = args.Length > 5 && int.TryParse(args[5], out var tpv) ? tpv : 0;
            return StressMain(stressWebPort, stressBots, stressUps, runtime, tp);
        }

        // P418 attach mode — a seventh entry point: a REAL spectator CLIENT.
        // Unlike every other mode (which boots its own private in-process
        // server), this mode connects to an EXTERNAL, already-running server
        // over the real wire protocol (ConnectionSession), tracks its ClientWorld,
        // and republishes what it actually receives to the unchanged spectator
        // page — the same server a real MonoGame client is connected to, so the
        // browser and the client show the same real game. Real bots are then
        // choreographed against that same server through wire-only commands.
        if (args.Length > 0 && args[0] == "attach")
        {
            if (args.Length < 4 || !int.TryParse(args[2], out var attachServerPort)
                || !int.TryParse(args[3], out var attachWebPort))
            {
                Console.Error.WriteLine("usage: vizfight attach <host> <port> <webPort> [bots] [enemyBots]");
                Console.Error.WriteLine("  connects a real spectator client (ConnectionSession) to <host>:<port>,");
                Console.Error.WriteLine("  republishes the live ClientWorld over a WebSocket to the spectator page");
                Console.Error.WriteLine("  at <webPort>, and choreographs <bots> (default 8) real Bot peers against");
                Console.Error.WriteLine("  that same server through wire-only commands (join, ready, launch, fly).");
                Console.Error.WriteLine("  [enemyBots], if given, sets the opposing-side contingent size directly");
                Console.Error.WriteLine("  (default: bots/6, clamped 2-6) — pass it equal to [bots] for an even split.");
                Console.Error.WriteLine("  The page shows the SAME real ships/positions as a real MonoGame client");
                Console.Error.WriteLine("  connected to the same host:port. Ctrl-C to stop.");
                return 2;
            }
            // Demo default: a legible bigger fleet, raised meaningfully from
            // P418's 8 and matching the count verified clean in P419's live run
            // (24 bots + spectator + a real MonoGame client, 0 server overruns).
            // Arbitrary higher counts are allowed — the same arg is the
            // load-test knob (see the P419 report).
            int attachBots = args.Length > 4 && int.TryParse(args[4], out var ab) ? ab : 24;
            int? attachEnemyBots = args.Length > 5 && int.TryParse(args[5], out var aeb) ? aeb : null;
            return AttachMain(args[1], attachServerPort, attachWebPort, attachBots, attachEnemyBots);
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
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
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

    // ── P306 stress mode ─────────────────────────────────────────────────

    private const float StressSpacing = 165f;   // grid cell size (units) — compact so even a 150-ship fleet stays legible in the overview shot
    private const double StressWeaveAmp = 0.075; // yaw oscillation amplitude (rad) — a gentle sway, real motion without dispersing the fleet
    private const double StressWeaveFreq = 0.9;  // yaw oscillation frequency (rad/s sim)
    private const float StressThrottle = 0.55f;  // cruise throttle — visible forward motion, low turn radius so the grid holds together

    /// <summary>P306: grid formation spawner — every joining peer (each stress
    /// bot, plus the MonoGame capture client when it connects) gets the same
    /// fightable hull on a SINGLE team (side 0) at a centered grid position.
    /// Single team is deliberate: the capture client only ever learns
    /// SAME-side ships over the wire (Server.BroadcastShipStates relays
    /// same-side teammates only, and this port's client has no
    /// HEAVY_SHIPS_UPDATE handler), so putting every ship on the client's
    /// team is the only way the client renders ALL N ships moving.</summary>
    private static int _stressPlaced;
    private static int _stressCols = 8;
    private static void InjectStressSpawn(Srv server)
    {
        _stressPlaced = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var (hull, weapon) = Fight.Value;
            int row = _stressPlaced / _stressCols;
            int col = _stressPlaced % _stressCols;
            var pos = new Vector3(
                (col - (_stressCols - 1) / 2f) * StressSpacing,
                0f,
                row * StressSpacing);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = 0;                                   // all one team — see InjectStressSpawn doc
            ship.Energy = hull.MaxEnergy * 100f;               // sustain any incidental fire
            mission.PlaceShip(ship, mission.Sectors[0]);
            _stressPlaced++;
            return (ship, mission.Sectors[0]);
        };
    }

    private static Cfg StressConfig(int ups, int transportPort) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = transportPort,
        UpdatesPerSecond = ups,   // the run's tick rate — real fixed cadence 1000/ups ms
        Games =
        [
            new Gm { Name = "Stress", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static int StressMain(int webPort, int botCount, int ups, double runtimeSec, int transportPort)
    {
        if (botCount < 1) { Console.Error.WriteLine("[stress] bots must be >= 1"); return 2; }
        if (botCount % 2 != 0) botCount++;   // keep the fight-hull pairing arithmetic simple; harmless
        _stressCols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(botCount * 1.7)));
        Console.WriteLine($"[stress] P306 stress run: bots={botCount} ups={ups} runtime={runtimeSec:0.##}s transportPort={transportPort}");

        using var server = new Srv();
        InjectStressSpawn(server);
        server.Boot(StressConfig(ups, transportPort));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;  // bypass the ready-up lobby (P180 convention)
        ushort bound = server.BoundPort;
        Console.WriteLine($"[stress] TRANSPORT_PORT={bound}");   // parseable by the runner
        Console.WriteLine($"[stress] server transport listening on 127.0.0.1:{bound}");

        // ── connect + log on all N real Bot peers ──
        var bots = new BotClass[botCount];
        for (int i = 0; i < botCount; i++)
        {
            bots[i] = new BotClass();
            bots[i].Connect(bound, $"STRS{i}");
            bots[i].SendLogonRequest(Request($"STRESSBOT{i}"));
        }

        // Real cadence: park on server.Tick() until every bot has a ship.
        int guard = 0;
        while (bots.Any(b => b.ShipAssignment is null) && !Cancelled() && guard++ < 20_000)
            server.Tick();
        if (bots.Any(b => b.ShipAssignment is null))
        {
            Console.Error.WriteLine($"[stress] FAILED: only {bots.Count(b => b.ShipAssignment is not null)}/{botCount} bots assigned a ship");
            return 1;
        }
        Console.WriteLine($"[stress] all {botCount} bots assigned; beginning {runtimeSec:0.##}s of continuous formation cruise");

        // ── tick + command loop: continuous motion for the whole window ──
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tickCosts = new List<double>();
        double startWall = sw.Elapsed.TotalSeconds;
        long simTicks = 0;
        while (!Cancelled())
        {
            double tBefore = sw.Elapsed.TotalSeconds;
            server.Tick();
            double tAfter = sw.Elapsed.TotalSeconds;
            tickCosts.Add(tAfter - tBefore);
            simTicks++;

            // Continuous weaving cruise: each bot banks sinusoidally so the
            // fleet snakes forward instead of flying as one rigid block.
            double sim = server.SimNow;
            for (int i = 0; i < botCount; i++)
            {
                double phase = (i % 7) * 0.9;               // de-phase columns so motion isn't monolithic
                double yaw = Math.Sin(sim * StressWeaveFreq + phase) * StressWeaveAmp;
                bots[i].CommandControls(new ControlData((float)yaw, 0f, 0f, StressThrottle), 0);
            }

            // P319 — occasional quick-chat chatter: the client's render loop walks
            // Events.Chats every frame (ProcessIncomingQuickChatVoices), so sustained
            // chat arrivals alongside the fleet EXPORT/SHIP_STATUS stream make that
            // indexed walk run under live concurrent append/eviction churn — the
            // exact H-2 condition this task fixed. One line per ~11 ticks (~2-3/s).
            if (simTicks % 11 == 0)
            {
                var chatter = bots[(int)(simTicks / 11) % botCount];
                chatter.SendChat($"STRS chatter #{simTicks}", voiceOver: (short)(simTicks % 30));
            }

            if (tAfter - startWall >= runtimeSec)
                break;
        }
        sw.Stop();

        // ── real tick-timing report ──
        double elapsed = sw.Elapsed.TotalSeconds;
        double mean = tickCosts.Count > 0 ? tickCosts.Average() : 0;
        double min = tickCosts.Count > 0 ? tickCosts.Min() : 0;
        double max = tickCosts.Count > 0 ? tickCosts.Max() : 0;
        double variance = tickCosts.Count > 1 ? tickCosts.Average(t => (t - mean) * (t - mean)) : 0;
        double sd = Math.Sqrt(variance);
        double expected = 1.0 / ups;
        long overruns = tickCosts.Count(t => t > expected * 1.05);   // pass exceeded its cadence budget
        Console.WriteLine($"[stress] done: {simTicks} ticks over {elapsed:0.###}s wall");
        Console.WriteLine($"[stress] tick real-cost  min={min * 1000:0.###}ms avg={mean * 1000:0.###}ms max={max * 1000:0.###}ms sd={sd * 1000:0.###}ms  (expected period {expected * 1000:0.###}ms @ {ups}ups)");
        Console.WriteLine($"[stress] overruns (> {expected * 1000:0.###}ms) = {overruns} / {tickCosts.Count} ticks ({100.0 * overruns / Math.Max(1, tickCosts.Count):0.##}%)");
        Console.WriteLine($"[stress] achieved {simTicks / Math.Max(0.0001, elapsed):0.##} ticks/s wall");
        return 0;
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
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
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

    // ── P305 roster mode ───────────────────────────────────────────────────
    //
    // The "full roster" spectator backend: a real server with NINE factions (one
    // side per P287 civ, each side's CivilizationId set so the MonoGame client's
    // per-ship civ resolution is real), and one real Bot per distinct ship model
    // from RosterPlanner.Plan (72 in PCore001). Every bot is seated on its
    // faction's side, parked in a faction-grouped grid in the first sector, and
    // LAUNCHED via the real SendBuyLoadout(fLaunch:true) wire path — the same
    // hangar-shop "Launch" message a real player sends. The point (P305): the
    // roster is the real distinct ship models, not the 447 hull-stat variants
    // that mostly repeat the same models under different names.

    /// <summary>Roster-specific config: all nine P287 factions as sides, side id
    /// == the faction's column index in <see cref="RosterPlanner.FactionOrder"/>.</summary>
    private static Cfg RosterConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "Roster",
                Map = "m2xspiderw.igc",
                Teams = RosterPlanner.FactionOrder.Length,
                CivIds = RosterPlanner.FactionOrder.Select(f => (int)f.CivId).ToArray(),
                SideNames = RosterPlanner.FactionOrder.Select(f => Capitalize(f.Civ)).ToArray(),
            },
        ],
    };

    private static string Capitalize(string s) =>
        s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>Custom spawner for P305: the Nth connecting peer (0-based) gets
    /// the Nth <see cref="RosterEntry"/> — its real hull, its faction's side, its
    /// grid slot — placed directly in sector 0. Peers beyond the planned roster
    /// (a viewing MonoGame client, say) get a throwaway Scout hull so the
    /// spawner never indexes past the plan.</summary>
    private static void InjectRosterSpawn(Srv server, IReadOnlyList<RosterEntry> entries)
    {
        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            Sector sector = mission.Sectors[0];
            if (placed >= entries.Count)
            {
                // Viewer/extra peer (P347): a Scout seated on side 0 (bios).
                // Post-P346/P347 the MonoGame client decodes the cross-side
                // visibility sync (cross-side EXPORT + HEAVY_SHIPS_UPDATE), so the
                // viewer renders EVERY faction's parked hulls — no longer limited to
                // same-side bios teammates (that was the pre-P346 assumption, retired
                // here). It is therefore parked SOUTH of the whole grid (nose -Z, so
                // the chase camera looks north across all 9 faction columns at once)
                // for the "render all hulls" roster picture.
                var scout = core.HullTypes.Values.First(h => h.Name.Contains("Scout"));
                var s = new Ship(id, scout, new Vector3(0f, 0f, 1500f), Vector3.Zero, Orientation.Identity);
                s.SideId = 0;
                mission.PlaceShip(s, sector);
                placed++;
                return (s, sector);
            }
            var e = entries[placed];
            var hull = core.HullTypes[e.HullId];
            var pos = new Vector3(e.WorldX, 0f, e.WorldZ);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = (short)e.Column;          // side id == FactionOrder index == grid column
            mission.PlaceShip(ship, sector);
            Console.WriteLine($"[roster] peer {placed + 1}/{entries.Count}: ship {id} side {e.Column} '{e.Civ}' hull {e.HullId} ({e.ModelName}) -> {e.Asset} @ ({e.WorldX:F0},0,{e.WorldZ:F0})");
            placed++;
            return (ship, sector);
        };
    }

    /// <summary>
    /// P338 — a single-team proof scenario for live-verifying the retuned
    /// display constants in the real MonoGame client. Mirrors the roster
    /// machinery (real Server, real Bot peers, real SendBuyLoadout launch) but
    /// with ONE side so every companion is visible to the viewer (this port's
    /// only cross-peer visibility path), a controllable VIEWER hull (small
    /// fighter / large carrier), the viewer optionally parked next to a real map
    /// warp (warp-vs-ship scale shot), and damaged companion ships (damage-debris
    /// smoke). Prints TRANSPORT_PORT=N so a launcher script can drive the MonoGame
    /// chase capture. (A missile-exhaust-trail live shot was attempted but is not
    /// producible: PCore001.igc contains no MissileLauncher part, so no missile can
    /// be fired in this harness — that proof is handled analytically.)
    /// </summary>
    private static Cfg ProofConfig() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 60,
        Games =
        [
            // m2xspiderw carries 44 real warps in the map, so the warp-vs-ship
            // scale shot uses a REAL warp (no injected geometry).
            new Gm { Name = "Retune Proof", Map = "m2xspiderw.igc", Teams = 1, CivIds = [1], SideNames = ["Alpha"] },
        ],
    };

    private static DataHullTypeIGC FindProofHull(StaticCore core, string? want)
    {
        // A name substring selects the viewer hull (small fighter vs large carrier).
        if (!string.IsNullOrWhiteSpace(want))
        {
            DataHullTypeIGC? found = null;
            foreach (var h in core.HullTypes.Values)
            {
                if (h.Name.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; break; }
            }
            if (found is { } f) return f;
            Console.WriteLine($"[proof] hull name '{want}' not found — falling back to Scout");
        }
        return core.HullTypes.Values.First(h => h.Name.Contains("Scout"));
    }

    private static void InjectProofSpawn(Srv server, string? viewerHullName)
    {
        // P338 env knobs so positions can be tuned without a rebuild:
        //   P338_DIST   = viewer distance from the chosen warp along +Z (0 = park in
        //                 OPEN space, far from any warp, for clean ship-framing shots)
        //   P338_VICTIM = victim offset ahead(+Z-) / side(+X) of the viewer (units)
        //   P338_VICHULL= victim hull name substring (default "Fighter"; use a big
        //                 hull e.g. "Carrier" for a clearly visible damage plume)
        //   P338_GUNHULL= gunner hull name substring (default "Fighter")
        string distEnv = Environment.GetEnvironmentVariable("P338_DIST") ?? "500";
        string vicEnv = Environment.GetEnvironmentVariable("P338_VICTIM") ?? "70,70";
        string vicHullEnv = Environment.GetEnvironmentVariable("P338_VICHULL") ?? "Fighter";
        string gunHullEnv = Environment.GetEnvironmentVariable("P338_GUNHULL") ?? "Fighter";
        if (!float.TryParse(distEnv, out float warpDist)) warpDist = 500f;
        var vicParts = vicEnv.Split(',');
        float vicSide = 70f, vicAhead = 70f;
        if (vicParts.Length >= 1) float.TryParse(vicParts[0], out vicSide);
        if (vicParts.Length >= 2) float.TryParse(vicParts[1], out vicAhead);

        var placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            Sector sector = mission.Sectors[0];
            ResolvedWarp? nearby = sector.Warps.FirstOrDefault(w => w.Destination is not null)
                                   ?? sector.Warps.FirstOrDefault();
            Vector3 warpAnchor = nearby is null
                ? new Vector3(0f, 0f, -600f)
                : new Vector3(nearby.Warp.Position![0], nearby.Warp.Position[1], nearby.Warp.Position[2]);
            // Viewer parked a moderate distance from the gate mouth (nose -Z, so
            // the chase camera behind at +Z looks -Z toward the warp): the warp
            // reads as a clear BACKGROUND object next to the ship (scale shot),
            // not so close it floods the frame. P338_DIST=0 → open space (no warp
            // in the frame) for a clean ship-framing shot.
            Vector3 viewerAt = warpDist > 0f
                ? warpAnchor + new Vector3(0f, 0f, warpDist)
                : new Vector3(-500f, 0f, 0f);

            if (placed == 0)
            {
                // Companion A ("gunner"): same-side, hull from P338_GUNHULL (a
                // Carrier gives a big visible smoke plume when damaged), parked
                // ahead-right of the viewer.
                var hull = FindProofHull(core, gunHullEnv);
                var s = new Ship(id, hull, viewerAt + new Vector3(vicSide, 0f, -vicAhead), Vector3.Zero, Orientation.Identity);
                s.SideId = 0;
                mission.PlaceShip(s, sector);
                Console.WriteLine($"[proof] peer gunner ship {id} hull {hull.Name} (Length {hull.Length:0.#})");
                placed++;
                return (s, sector);
            }
            if (placed == 1)
            {
                // Companion B ("victim"): same-side, hull from P338_VICHULL, parked
                // ahead-LEFT (mirror) of the viewer so the two companions flank it.
                var vhull = FindProofHull(core, vicHullEnv);
                var s = new Ship(id, vhull, viewerAt + new Vector3(-vicSide, 0f, -vicAhead), Vector3.Zero, Orientation.Identity);
                s.SideId = 0;
                mission.PlaceShip(s, sector);
                Console.WriteLine($"[proof] peer victim ship {id} hull {vhull.Name} (Length {vhull.Length:0.#})");
                placed++;
                return (s, sector);
            }
            // The viewer (last to connect — the MonoGame capture client) gets the
            // requested hull parked next to the warp, on side 0.
            var vh = FindProofHull(core, viewerHullName);
            var v = new Ship(id, vh, viewerAt, Vector3.Zero, Orientation.Identity);
            v.SideId = 0;
            mission.PlaceShip(v, sector);
            Console.WriteLine($"[proof] VIEWER ship {id} hull {vh.Name} (Length {vh.Length:0.#}) @ ({viewerAt.X:0.#},0,{viewerAt.Z:0.#})");
            placed++;
            return (v, sector);
        };
    }

    private static int ProofMain(int port, string? viewerHullName)
    {
        var core = RepresentativeCore();
        DataHullTypeIGC vh = FindProofHull(core, viewerHullName);
        Console.WriteLine($"[proof] viewer hull will be '{vh.Name}' (HullId {vh.HullId}, Length {vh.Length:0.#}, render radius {vh.Length / 2f:0.#})");

        using var server = new Srv();
        InjectProofSpawn(server, viewerHullName);
        server.Boot(ProofConfig());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up (P180 convention)
        ushort bound = server.BoundPort;
        Console.WriteLine($"TRANSPORT_PORT={bound}");

        // Connect two real companion bots (gunner + victim); the viewer connects later.
        var gun = new BotClass(); gun.Connect(bound, "PGUN");
        var vic = new BotClass(); vic.Connect(bound, "PVIC");
        gun.SendLogonRequest(Request("PROOFGUNNER"));
        vic.SendLogonRequest(Request("PROOFVICTIM"));

        // Seat the companions through the real wire path.
        for (int t = 0; t < 360 && (gun.ShipAssignment is null || vic.ShipAssignment is null) && !Cancelled(); t++)
            server.Tick();
        var live = AllShips(server).ToList();
        Ship? gunShip = live.FirstOrDefault(s => gun.ShipAssignment is not null && s.Id == gun.ShipAssignment.Ship.ShipId);
        Ship? vicShip = live.FirstOrDefault(s => vic.ShipAssignment is not null && s.Id == vic.ShipAssignment.Ship.ShipId);
        if (gunShip is null || vicShip is null)
        {
            Console.Error.WriteLine("vizfight proof FAILED: companions not seated");
            return 1;
        }
        gun.SendBuyLoadout(gunShip.HullType.HullId, fLaunch: true);
        vic.SendBuyLoadout(vicShip.HullType.HullId, fLaunch: true);
        for (int t = 0; t < 60; t++) server.Tick();

        // Damage every live companion's hull so the client streams damage-debris
        // smoke. The wire ServerShipUpdate reads Ship.Fraction each broadcast, so
        // a low fraction surfaces to the viewer and the client emits debris.
        foreach (var s in AllShips(server)) s.SetFraction(0.40f);
        Console.WriteLine("[proof] all companion hull fractions set to 0.40 (debris)");

        // A STATIONARY ship's fraction change alone won't be re-broadcast (the
        // dirty-check suppresses re-sends for unmoving ships — P332), so the
        // viewer never learns it is damaged. Command both companions to cruise
        // gently so their positions change each tick and the updated 0.4 fraction
        // (and the resulting debris stream) reaches the viewer.
        var cruiseThread = new Thread(() =>
        {
            var stop = DateTime.UtcNow.AddMinutes(5);
            while (DateTime.UtcNow < stop && !Cancelled())
            {
                try { gun.CommandControls(new ControlData(-0.04f, 0f, 0f, 0.10f)); }
                catch { }
                try { vic.CommandControls(new ControlData(0.04f, 0f, 0f, 0.10f)); }
                catch { }
                Thread.Sleep(400);
            }
        }) { IsBackground = true };
        cruiseThread.Start();

        // Tick until cancelled (Ctrl-C) — the launcher runs a timed chase capture.
        Console.WriteLine("[proof] running — Ctrl-C to stop");
        while (!Cancelled()) { server.Tick(); Thread.Sleep(5); }
        return 0;
    }

    private static int RosterMain(int port, string? civFilter = null)
    {
        var rCore = RepresentativeCore();
        var entries = RosterPlanner.Plan(rCore.HullTypes.Values);
        Console.WriteLine($"[roster] planned {entries.Count} distinct ship models across " +
                          $"{entries.Select(e => e.Civ).Distinct().Count()} factions:");
        foreach (var g in entries.GroupBy(e => e.Civ).OrderBy(g => g.Key))
            Console.WriteLine($"[roster]   {g.Key,-8} {g.Count(),2} models");

        if (!string.IsNullOrEmpty(civFilter))
        {
            var filtered = entries.Where(e => string.Equals(e.Civ, civFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Count == 0)
            {
                Console.Error.WriteLine($"vizfight roster FAILED: no faction '{civFilter}' among " +
                                        $"[{string.Join(", ", entries.Select(e => e.Civ).Distinct().OrderBy(c => c))}]");
                return 1;
            }
            entries = filtered;
            Console.WriteLine($"[roster] filtered to civ '{civFilter}': {entries.Count} bot(s), one per hull");
        }

        if (entries.Count == 0)
        {
            Console.Error.WriteLine("vizfight roster FAILED: no reachable ship assets");
            return 1;
        }

        using var server = new Srv();
        InjectRosterSpawn(server, entries);
        server.Boot(RosterConfig());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass the ready-up lobby (P180), same as Main
        ushort bound = server.BoundPort;
        Console.WriteLine($"[roster] server transport listening on 127.0.0.1:{bound}");

        // ── spectator hub + HTTP/WS host (serve the roster to a browser too) ──
        var hub = new SpectatorHub();
        var sectorCache = new Dictionary<short, string>();
        hub.Seeder = ch =>
        {
            string[] cached;
            lock (sectorCache) cached = sectorCache.Values.ToArray();
            foreach (var json in cached) ch.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        };
        foreach (var s in server.Missions.SelectMany(m => m.Sectors))
        {
            var json = SectorMessageJson(s);
            lock (sectorCache) sectorCache[s.Id] = json;
            hub.Broadcast(json);
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = ResolveWwwroot() });
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        var app = builder.Build();
        app.UseWebSockets();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.Map("/ws", (HttpContext c) => HandleWs(c, hub, c.RequestAborted));
        var appTask = app.RunAsync();
        Console.WriteLine($"[roster] spectator http://127.0.0.1:{port}/  ws://127.0.0.1:{port}/ws");

        // ── connect the real bots, one per planned roster entry ──
        var bots = new BotClass[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            bots[i] = new BotClass();
            bots[i].Connect(bound, $"R{i:D3}");
        }
        for (int i = 0; i < entries.Count; i++)
            bots[i].SendLogonRequest(Request($"ROSTER{i:D3}"));
        while (bots.Any(b => b.ShipAssignment is null) && !Cancelled())
            server.Tick();
        if (bots.Any(b => b.ShipAssignment is null))
        {
            Console.Error.WriteLine("vizfight roster FAILED: not all bots assigned a ship");
            return 1;
        }

        // Launch every bot through the REAL SendBuyLoadout(fLaunch:true) path.
        for (int i = 0; i < entries.Count; i++)
        {
            bots[i].SendBuyLoadout(entries[i].HullId, fLaunch: true);
            Console.WriteLine($"[roster] bot {i:D3} launches hull {entries[i].HullId} ({entries[i].DisplayName}) via BUY_LOADOUT");
        }

        // Let the launches settle so the wire round-trips land before the dump.
        for (int t = 0; t < 30; t++) server.Tick();

        // ── durable roster dump for the capture harness (asset/civ/hull/ship/grid) ──
        var dumpPath = Path.Combine(Directory.GetCurrentDirectory(), "roster_dump.json");
        var rows = new List<object>();
        var liveShips = AllShips(server).ToDictionary(s => s.Id);
        for (int i = 0; i < entries.Count; i++)
        {
            short shipId = bots[i].ShipAssignment!.Ship.ShipId;
            var live = liveShips.TryGetValue(shipId, out var ls) ? ls : null;
            rows.Add(new
            {
                asset = entries[i].Asset,
                civ = entries[i].Civ,
                civClass = entries[i].CivClass,
                hullId = entries[i].HullId,
                modelName = entries[i].ModelName,
                displayName = entries[i].DisplayName,
                column = entries[i].Column,
                row = entries[i].Row,
                sideId = (short)entries[i].Column,
                shipId,
                worldX = live?.Position.X ?? entries[i].WorldX,
                worldZ = live?.Position.Z ?? entries[i].WorldZ,
            });
        }
        File.WriteAllText(dumpPath, JsonSerializer.Serialize(rows, JsonOpts));
        Console.WriteLine($"[roster] wrote roster_dump.json ({rows.Count} entries) to {dumpPath}");

        Console.WriteLine("[roster] full roster parked; waiting for a spectator before holding the grid (Ctrl-C to stop)");
        bool commanded = false;
        while (!Cancelled())
        {
            server.Tick();
            hub.Broadcast(ExploreTickFrame(server));
            // Park all bots: keep them on the ground so the grid holds still.
            if (!commanded && hub.Count > 0)
            {
                foreach (var b in bots) b.CommandControls(ControlData.Neutral);
                commanded = true;
                Console.WriteLine("[roster] spectator connected — grid frozen (neutral controls)");
            }
        }

        Console.WriteLine($"[roster] ran until cancelled; roster_dump.json at {dumpPath}");
        appTask.GetAwaiter().GetResult();
        return 0;
    }

    // ── P205 showcase mode ────────────────────────────────────────────────
    //
    // A scripted, multi-phase trailer scenario driven through the SAME real
    // in-process server + real Bot peers + same SpectatorHub/WS/HTTP machinery
    // as Main()/ExploreMain() (P205 constraint: no second server, no second web
    // host, no new server-side gameplay). Every beat uses an existing, shipped
    // Bot/Server API — nothing here invents server behaviour:
    //   ready-up   SendPlayerReady / SendStartGame (P180/P188)
    //   launch     SendBuyLoadout(fLaunch:true) with the ship's real parts
    //   combat     movement CommandControls + SendFire-expendable-free AllWeapons
    //   flag       physical Treasure pickup (P201 proximity) + secure-by-dock
    //   economy    SendPlayerDonate (P203) -> real MONEY_CHANGE
    //   vote       "#mutiny" chat -> real ballot -> SendVote (P187)
    //   dock       flying into the own-side station's DockRadius auto-docks
    // Non-visual beats (alliance/economy/vote) get an `event` narration line
    // with REAL data (broadcast arrays, MONEY_CHANGE deltas, leadership ids),
    // because P204 only made ships + physical objects visible on the page.
    //
    // Staging: the whole arc runs in side 0's home sector (Ganymede, which
    // hosts side 0's Garrison station — verified via missiondump). Positions
    // are discovered at runtime off the real station, not hardcoded to this
    // map, so the same code works on any m2xspiderw-style map.

    private enum ScRole { Attacker0, Attacker1, Carrier, Ally, Target0, Target1 }

    /// <summary>One showcased peer: its real Bot, its server ship id, its
    /// intended side/hull and spawn offset (relative to the arena's side-0
    /// station). Hulls/loadouts are real core entries (fightable fighter +
    /// rescue-capable scout); <see cref="WeaponPart"/> carries the OUTER part
    /// (with its PartId) of the mounted fighter gun so a real BUY_LOADOUT can
    /// re-mount it — the server's ApplyLoadout clears the loadout first, so a
    /// part-less launch would permanently disarm the fighter.</summary>
    private sealed class ScBot
    {
        public BotClass Bot = null!;
        public string Name = "";
        public short ShipId;
        public ScRole Role;
        public short SideId;                 // intended side (set via TryJoinSide, not the spawner)
        public DataHullTypeIGC Hull;
        public DataPartTypeIGC? WeaponPart;  // outer part for fighters (PartId for the launch)
        public Vector3 Offset;
        public bool Launched;
    }

    /// <summary>The showcase's runtime-discovered arena: side 0's home sector
    /// and station, plus derived choreography anchors (formation rally, combat
    /// front, enemy-flag position) — all computed off the real map, exactly as
    /// Main() discovers its own sectors instead of assuming ids.</summary>
    private sealed class ScLayout
    {
        public Sector Arena = null!;
        public Vector3 StationPos;
        public Vector3 Rally;       // formation / skirmish anchor (far from the station — ships
                                    // auto-dock within Sector.DockRadius, so the fighting must
                                    // stay well outside it)
        public Vector3 Front0;      // attackers' firing line
        public Vector3 Front1;      // targets' firing line
        public Vector3 FlagPos;     // where Gamma's flag treasure is placed (far side)
        public Vector3 CarrierApron;// carrier's holding point (also far from the station)
        public readonly List<ScBot> Bots = new();

        public void Discover(Srv server)
        {
            if (Arena is not null) return;
            var mission = server.Missions[0];
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId == 0);
            Arena = mission.Sectors.First(s => s.Stations.Contains(station));
            StationPos = new Vector3(station.Position[0], station.Position[1], station.Position[2]);
            Rally = StationPos + new Vector3(680f, 0f, 320f);
            Front0 = Rally + new Vector3(-60f, 0f, 60f);    // ~650u from the station
            Front1 = Rally + new Vector3(140f, 0f, -80f);   // ~790u from the station
            FlagPos = StationPos + new Vector3(-720f, 0f, -300f);
            CarrierApron = StationPos + new Vector3(-560f, 0f, -200f);
            Console.WriteLine($"[showcase] arena sector {Arena.Id} '{Arena.Name}' at station {station.StationId} " +
                              $"({StationPos.X:F0},{StationPos.Y:F0},{StationPos.Z:F0}); rally {Rally}; flag @ {FlagPos}");
        }
    }

    private enum ScPhase
    {
        WaitSpectator, AllianceSend, AllianceNarrate, ReadyUp, WaitingStarted,
        Launch, Formation, Skirmish, FlagPickup, FlagSecure, Economy, Vote, Finale, Held,
    }

    private sealed class ScState
    {
        public ScPhase Phase = ScPhase.WaitSpectator;
        public double PhaseEnter = -1;      // sim-time the current phase began (for timeouts)
        public bool FlagPlaced;
        public bool CarrierHasFlag;         // the carrier was observed holding the flag (for the secure beat)
        public bool EconomySent;
        public bool EconomyInit;
        public double EconomySentAt = -1;
        public int DonationAmount;
        public long EconomySideMoney;       // side-0 treasury at economy-phase entry (to narrate the real payday)
        public bool PaydayNarrated;
        public bool DroneSpawned;
        public short NpcDroneId;            // the NPC Gamma miner the Alpha fighters kill (real KILL_SHIP)
        public short? SecuredSideFlags;     // side-0 flag count after secure (real data for the event)
        public readonly HashSet<short> SeenKills = new();
        public bool MutinyVoted;
        public short MutinyBallotId = -1;
        public int FinaleShots;
    }

    /// <summary>Broadcast one `event` line to the spectator (the P59/P204 event
    /// channel — the only way the page can see a non-physical beat happen).</summary>
    private static void ShowcaseEvent(Srv server, SpectatorHub hub, string text)
    {
        var json = JsonSerializer.Serialize(new { type = "event", text, simTime = Round2(server.SimNow) }, JsonOpts);
        hub.Broadcast(json);
        Console.WriteLine($"[showcase][t {server.SimNow:0.0}] event: {text}");
    }

    private static Ship? ScShip(Srv server, short shipId) =>
        AllShips(server).FirstOrDefault(s => s.Id == shipId);

    /// <summary>Drive one bot toward <paramref name="target"/> using the exact
    /// shared face-and-thrust steering law the sim's own NPC autopilot uses
    /// (<c>Ship.SteerToward</c>, Ship.cs:3720-3761): continuous cross-product
    /// turn signal in ship-local space, a two-zone throttle ramp, dead-centre
    /// kill-throttle on arrival. <paramref name="stateM"/> fires mounted
    /// weapons when set (AllWeapons). Sends a fresh SHIP_UPDATE every tick so
    /// the server reacts to the changing geometry.</summary>
    private static void FlyTo(Srv server, ScBot sc, Vector3 target, float arrival, float brake, int stateM = 0)
    {
        var ship = ScShip(server, sc.ShipId);
        if (ship is null || ship.Dead) return;
        var toTarget = target - ship.Position;
        float dist = toTarget.Length();
        if (dist <= 1e-3f)
        {
            sc.Bot.CommandControls(ControlData.Neutral, stateM);
            return;
        }
        var dir = toTarget / dist;
        var body = ship.Orientation.TimesInverse(Vector3.CrossProduct(ship.Orientation.GetForward(), dir));
        float yaw = Math.Clamp(2.0f * body.Y, -1f, 1f);
        float pitch = Math.Clamp(2.0f * -body.X, -1f, 1f);
        float distFactor = dist >= brake ? 1f : Math.Clamp((dist - arrival) / (brake - arrival), 0f, 1f);
        float align = Math.Clamp(-ship.Orientation.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * align - 1f, -1f, 1f);
        sc.Bot.CommandControls(new ControlData(yaw, pitch, 0f, throttle), stateM);
    }

    private static bool ScNear(Vector3 a, Vector3 b, float tol) => (a - b).Length() <= tol;

    /// <summary>Custom spawner: place each connecting peer by role (ordered by
    /// connection sequence, same lane-indexing idea as Main's InjectFightSpawn).
    /// Side is deliberately LEFT NULL here — real side membership is granted
    /// afterwards via <c>server.TryJoinSide</c> so leadership/ownership emerge
    /// from the real join flow (P183), not from a spawner shortcut.</summary>
    private static void InjectShowcaseSpawn(Srv server, ScLayout layout)
    {
        int placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            layout.Discover(server);
            var role = (ScRole)placed;
            var (hull, weaponPart) = ScHullAndWeapon(core, role);
            var offset = role switch
            {
                ScRole.Attacker0 => new Vector3(520f, 0f, 360f),
                ScRole.Attacker1 => new Vector3(580f, 0f, 380f),
                ScRole.Carrier   => new Vector3(-620f, 0f, -240f),
                ScRole.Ally      => new Vector3(120f, 0f, -340f),
                ScRole.Target0   => new Vector3(760f, 0f, 250f),
                _                => new Vector3(810f, 0f, 220f),
            };
            var pos = layout.StationPos + offset;
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity);
            mission.PlaceShip(ship, layout.Arena);
            // Attackers get energy + the fightable weapon (Main's proven pattern);
            // no other role needs a gun (carrier needs its rescue hull, targets and
            // the allied wingman are sitting ducks / onlookers).
            if (role is ScRole.Attacker0 or ScRole.Attacker1)
            {
                ship.Energy = hull.MaxEnergy * 100f;
                if (weaponPart is { } wp)
                    ship.MountWeapon((DataPartTypeIGC.Weapon)wp.Type);
            }
            var sc = layout.Bots[placed];
            sc.ShipId = id;
            sc.Hull = hull;
            sc.WeaponPart = weaponPart;
            sc.Offset = offset;
            Console.WriteLine($"[showcase] peer {placed + 1} {role} spawned: ship {id}, hull {hull.Name} @ {pos}");
            placed++;
            return (ship, layout.Arena);
        };
    }

    /// <summary>Pick a real hull (and, for fighters, the OUTER weapon part) per
    /// role: the fightable fighter from <see cref="Fight"/> for combatants, and a
    /// Scout-class hull granted <c>HabmRescue</c> for the flag carrier (the
    /// rescue capability the P201 pickup pass requires — the same pattern the
    /// LiveGainFlagTests pickup harness uses). The returned
    /// <see cref="DataPartTypeIGC"/> carries the weapon's <c>PartId</c>, so the
    /// launch beat can re-mount it after the server's loadout clear.</summary>
    private static (DataHullTypeIGC hull, DataPartTypeIGC? weaponPart) ScHullAndWeapon(StaticCore core, ScRole role)
    {
        var fight = Fight.Value;
        if (role == ScRole.Carrier)
        {
            if (!core.HullTypes.Values.Any(h => h.Name.Contains("Scout")))
                throw new InvalidOperationException($"core {core.Version} has no Scout-named hull for the flag carrier");
            var scout = core.HullTypes.Values.First(h => h.Name.Contains("Scout"));
            scout = scout with { HabmCapabilities = (short)(scout.HabmCapabilities | DataHullTypeIGC.HabmRescue) };
            return (scout, null);
        }
        // Resolve the OUTER part that carries the Fight weapon's PartId (Fight.Value
        // only keeps the inner Weapon type). Match on the same discriminating fields
        // Fight uses (dispersion 0, same projectile, fits the hull's energy).
        DataPartTypeIGC? part = null;
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon))
        {
            if (p.Type is DataPartTypeIGC.Weapon w && w.ProjectileTypeId == fight.weapon.ProjectileTypeId
                && w.Dispersion == 0f && w.EnergyPerShot <= fight.hull.MaxEnergy)
            {
                part = p;
                break;
            }
        }
        if (part is null)
            throw new InvalidOperationException("cannot resolve the fight weapon's outer part id");
        return (fight.hull, part);
    }

    private static int ShowcaseMain(int port)
    {
        var rCore = RepresentativeCore();
        Console.WriteLine($"[showcase] hulls: fighter '{Fight.Value.hull.Name}' (HullId {Fight.Value.hull.HullId}); " +
                          $"carrier scout '{rCore.HullTypes.Values.First(h => h.Name.Contains("Scout")).Name}'");

        using var server = new Srv();
        var roles = new[] { ScRole.Attacker0, ScRole.Attacker1, ScRole.Carrier, ScRole.Ally, ScRole.Target0, ScRole.Target1 };
        var names = new[] { "A1", "A2", "CV", "B1", "G1", "G2" };
        var layout = new ScLayout();
        for (int i = 0; i < roles.Length; i++)
            layout.Bots.Add(new ScBot { Role = roles[i], Name = names[i] });
        InjectShowcaseSpawn(server, layout);
        server.Boot(ShowcaseConfig());
        layout.Discover(server);            // arena known now that the mission exists
        ushort bound = server.BoundPort;
        Console.WriteLine($"[showcase] server transport listening on 127.0.0.1:{bound}");

        // ── spectator hub + HTTP/WS host (identical to the other two modes) ──
        var hub = new SpectatorHub();
        var sectorCache = new Dictionary<short, string>();
        hub.Seeder = ch =>
        {
            string[] cached;
            lock (sectorCache) cached = sectorCache.Values.ToArray();
            foreach (var json in cached) ch.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        };
        foreach (var s in server.Missions.SelectMany(m => m.Sectors))
        {
            var json = SectorMessageJson(s);
            lock (sectorCache) sectorCache[s.Id] = json;
            hub.Broadcast(json);
        }
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = ResolveWwwroot() });
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(port));
        var app = builder.Build();
        app.UseWebSockets();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.Map("/ws", (HttpContext c) => HandleWs(c, hub, c.RequestAborted));
        var appTask = app.RunAsync();
        Console.WriteLine($"[showcase] spectator http://127.0.0.1:{port}/  ws://127.0.0.1:{port}/ws");

        // Relay the server's own Notable moments (dock/undock/mine/payday) too.
        server.Notable += text => hub.Broadcast(JsonSerializer.Serialize(
            new { type = "event", text, simTime = Round2(server.SimNow) }, JsonOpts));

        // ── connect the 5 real bots, in role order (determines TryJoinSide order) ──
        for (int i = 0; i < roles.Length; i++)
        {
            var b = new BotClass();
            b.Connect(bound, $"SC{i}");
            b.SendLogonRequest(Request(names[i]));
            layout.Bots[i].Bot = b;
            layout.Bots[i].Name = names[i];
        }
        while (!layout.Bots.All(sc => sc.Bot.ShipAssignment is not null) && !Cancelled())
            server.Tick();
        if (layout.Bots.Any(sc => sc.Bot.ShipAssignment is null))
        {
            Console.Error.WriteLine("vizfight showcase FAILED: not all bots assigned a ship");
            return 1;
        }
        // Capture the real ship ids the server handed out.
        for (int i = 0; i < layout.Bots.Count; i++)
            layout.Bots[i].ShipId = layout.Bots[i].Bot.ShipAssignment!.Ship.ShipId;

        // Real side membership + leadership/ownership via the REAL join flow.
        var mission = server.Missions[0];
        short side0 = mission.Sides[0].Id, side1 = mission.Sides[1].Id, side2 = mission.Sides[2].Id;
        foreach (var sc in layout.Bots)
        {
            sc.SideId = sc.Role switch
            {
                ScRole.Ally => side1,
                ScRole.Target0 or ScRole.Target1 => side2,
                _ => side0,
            };
            var live = AllShips(server).First(s => s.Id == sc.ShipId);
            if (!server.TryJoinSide(live, sc.SideId))
            {
                Console.Error.WriteLine($"vizfight showcase FAILED: TryJoinSide ship {sc.ShipId} -> side {sc.SideId}");
                return 1;
            }
            Console.WriteLine($"[showcase] {sc.Name} ship {sc.ShipId} seated on side {sc.SideId} " +
                              $"(leader {mission.Sides.First(s => s.Id == sc.SideId).LeaderShipId}, " +
                              $"owner-side {mission.MissionOwnerSideId})");
        }

        var state = new ScState();
        var owner = layout.Bots.First(sc => sc.Role == ScRole.Attacker0);   // first on side0 -> leader+owner
        Console.WriteLine($"[showcase] mission owner ship {owner.ShipId} (side {owner.SideId}); waiting for a spectator before the show begins");

        // ── the scripted arc (driven by real sim ticks, not wall-clock sleeps) ──
        while (!Cancelled())
        {
            server.Tick();
            hub.Broadcast(TickFrame(server));
            StepShowcase(server, hub, layout, state, owner, side0, side1, side2);
            BroadcastShowcaseKills(server, hub, layout, state);
        }

        Console.WriteLine($"[showcase] ran until cancelled; websocket served at ws://127.0.0.1:{port}/ws");
        appTask.GetAwaiter().GetResult();
        return 0;
    }

    /// <summary>The per-tick phase machine. All pacing is in sim time (real
    /// tick cadence), with physical beats driven by real observations (flag
    /// picked up, docked, leadership transferred), never by cooked data.</summary>
    private static void StepShowcase(Srv server, SpectatorHub hub, ScLayout layout, ScState st,
                                     ScBot owner, short side0, short side1, short side2)
    {
        double now = server.SimNow;
        var mission = server.Missions[0];

        switch (st.Phase)
        {
            case ScPhase.WaitSpectator:
            {
                if (st.PhaseEnter < 0) st.PhaseEnter = now;
                // Hold until a real viewer connects so the whole arc is watchable
                // (Main waits for a spectator before opening fire for the same reason).
                if (hub.Count > 0)
                {
                    ShowcaseEvent(server, hub, "Showcase online — six pilots muster at Ganymede garrison; three factions enter the arena.");
                    st.Phase = ScPhase.AllianceSend;
                    st.PhaseEnter = now;
                }
                else if (now - st.PhaseEnter > 30)
                {
                    ShowcaseEvent(server, hub, "No spectator connected; proceeding anyway.");
                    st.Phase = ScPhase.AllianceSend;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.AllianceSend:
            {
                // Pre-start only (mission is NotStarted here): the mission owner
                // allies Bravo (side1) to Alpha (side0) through the real
                // CHANGE_ALLIANCE (P190). With 3 active teams the server can
                // actually build the alliance group (Server.cs:5453).
                owner.Bot.SendChangeAlliance(sideId: side1, sideAlly: side0);
                st.Phase = ScPhase.AllianceNarrate;
                st.PhaseEnter = now;
                break;
            }
            case ScPhase.AllianceNarrate:
            {
                // Wait a moment for the CHANGE_ALLIANCES(197) broadcast to land so
                // the narration quotes the REAL alliance array, not a guess.
                if (st.PhaseEnter < 0) st.PhaseEnter = now;
                var br = owner.Bot.AllianceBroadcasts.LastOrDefault();
                if (br is not null || now - st.PhaseEnter > 5)
                {
                    if (br is not null)
                    {
                        // Real CHANGE_ALLIANCES array: Allies[i] = side i's ally group, -1 = none.
                        var a0 = br.Allies.Length > side0 ? (int)br.Allies[side0] : -1;
                        var a1 = br.Allies.Length > side1 ? (int)br.Allies[side1] : -1;
                        var a2 = br.Allies.Length > side2 ? (int)br.Allies[side2] : -1;
                        ShowcaseEvent(server, hub, $"Diplomacy: the commander allies Bravo to Alpha — real CHANGE_ALLIANCES group {{{a0},{a1},{a2}}} " +
                                                  $"({(a0 == a1 && a0 >= 0 ? "a pact holds; Gamma stands apart" : "no pact formed")}).");
                    }
                    else
                    {
                        ShowcaseEvent(server, hub, "Diplomacy: commander attempts an alliance, but no CHANGE_ALLIANCES reply arrived.");
                    }
                    st.Phase = ScPhase.ReadyUp;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.ReadyUp:
            {
                foreach (var sc in layout.Bots)
                    sc.Bot.SendPlayerReady(fReady: true, shipId: sc.ShipId);
                ShowcaseEvent(server, hub, "All six pilots report ready — launch countdown armed.");
                st.Phase = ScPhase.WaitingStarted;
                st.PhaseEnter = now;
                break;
            }
            case ScPhase.WaitingStarted:
            {
                // Real countdown (P180 auto-start once every occupied side is
                // ready, or the owner's manual START_GAME) → Stage crosses Started.
                if (st.PhaseEnter < 0) st.PhaseEnter = now;
                if (mission.Stage == MissionStage.Started)
                {
                    ShowcaseEvent(server, hub, $"Mission underway — {mission.Sides.Count} sides, real economy live.");
                    st.Phase = ScPhase.Launch;
                    st.PhaseEnter = now;
                }
                else if (now - st.PhaseEnter > 40)
                {
                    ShowcaseEvent(server, hub, "Countdown stalled (stage never Started) — continuing for the demo.");
                    st.Phase = ScPhase.Launch;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.Launch:
            {
                // Real launch: BUY_LOADOUT(fLaunch:true). For a fighter the parts
                // list carries the mounted gun's real PartId so the server's
                // ApplyLoadout clear+remount keeps it armed; other roles launch
                // hull-only (their empty parts clear nothing they need).
                foreach (var sc in layout.Bots)
                {
                    var ship = ScShip(server, sc.ShipId);
                    if (ship is null || sc.Launched) continue;
                    List<ExpandedPartData> parts;
                    if (sc.WeaponPart is { } wp)
                        parts = [new ExpandedPartData(wp.PartId, 1, 0, 255)];
                    else
                        parts = ship.Parts.Select(p => p.ToExpandedPartData()).ToList();
                    sc.Bot.SendBuyLoadout(sc.Hull.HullId, fLaunch: true, parts: parts);
                    sc.Launched = true;
                    Console.WriteLine($"[showcase] {sc.Name} launched with {parts.Count} part(s)");
                }
                if (layout.Bots.All(sc => sc.Launched))
                {
                    ShowcaseEvent(server, hub, "Fleet launches — fighters lift off with full loadouts for formation.");
                    st.Phase = ScPhase.Formation;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.Formation:
            {
                if (st.PhaseEnter < 0) st.PhaseEnter = now;
                foreach (var sc in layout.Bots)
                {
                    if (sc.Role is ScRole.Target0 or ScRole.Target1)
                        FlyTo(server, sc, layout.Front1, 25f, 200f);          // Gamma picket holds its line
                    else if (sc.Role == ScRole.Carrier)
                        FlyTo(server, sc, layout.CarrierApron, 60f, 250f);    // carrier holds on the far side
                    else if (sc.Role == ScRole.Ally)
                        FlyTo(server, sc, layout.StationPos + new Vector3(160f, 0f, -360f), 40f, 220f);  // Bravo wingman holds
                    else
                        FlyTo(server, sc, layout.Front0, 25f, 200f);          // attackers form up
                }
                bool formed = layout.Bots.All(sc =>
                {
                    var target = sc.Role is ScRole.Target0 or ScRole.Target1 ? layout.Front1
                        : sc.Role == ScRole.Carrier ? layout.CarrierApron
                        : sc.Role == ScRole.Ally ? layout.StationPos + new Vector3(160f, 0f, -360f) : layout.Front0;
                    var ship = ScShip(server, sc.ShipId);
                    return ship is null || ScNear(ship.Position, target, 90f);
                });
                if (formed)
                {
                    ShowcaseEvent(server, hub, "Formation set — Alpha fighters advance toward the Gamma picket line.");
                    st.Phase = ScPhase.Skirmish;
                    st.PhaseEnter = now;
                }
                else if (now - st.PhaseEnter > 25)
                {
                    ShowcaseEvent(server, hub, "Formation achieved (timer) — engaging.");
                    st.Phase = ScPhase.Skirmish;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.Skirmish:
            {
                if (st.PhaseEnter < 0) st.PhaseEnter = now;
                // A real kill needs a victim that DOESN'T eject: player ships eject
                // to lifepods (EJECT, no KILL_SHIP), but a destroyed NPC/drone
                // broadcasts a real KILL_SHIP(105). Spawn one ownerless Gamma miner
                // drone for the Alpha fighters to shoot down.
                if (!st.DroneSpawned)
                {
                    var drone = server.SpawnNpcShip(mission, layout.Arena, side2,
                                                    layout.Front1 + new Vector3(0f, 0f, -50f));
                    st.NpcDroneId = drone.Id;
                    st.DroneSpawned = true;
                    ShowcaseEvent(server, hub, "Alpha forward scouts catch a lone Gamma miner drifting on the picket line.");
                }
                var droneShip = ScShip(server, st.NpcDroneId);
                foreach (var sc in layout.Bots.Where(x => x.Role is ScRole.Attacker0 or ScRole.Attacker1))
                {
                    var target = droneShip is { Dead: false } ? droneShip.Position : layout.Front1;
                    FlyTo(server, sc, target, 30f, 300f, stateM: ShipControlState.AllWeapons);
                }
                // Targets hold position; the kill surfaces via Bot.Kills (KILL_SHIP
                // from the drone's destruction — the same source Main uses).
                if (st.SeenKills.Count >= 1 || now - st.PhaseEnter > 35)
                {
                    if (st.SeenKills.Count >= 1)
                        ShowcaseEvent(server, hub, "The Gamma miner is down — skirmish won, objectives turn to the flag.");
                    st.Phase = ScPhase.FlagPickup;
                    st.PhaseEnter = now;
                    foreach (var sc in layout.Bots.Where(x => x.Role is ScRole.Attacker0 or ScRole.Attacker1))
                        sc.Bot.CommandControls(ControlData.Neutral);   // cease fire / hold
                }
                break;
            }
            case ScPhase.FlagPickup:
            {
                if (!st.FlagPlaced)
                {
                    // Place Gamma's REAL flag treasure in the arena (the P201
                    // PlaceFlagTreasure shape), on the far side of the station so
                    // the carrier's pickup run is separate from the fighting.
                    PlaceShowcaseFlag(server, layout);
                    st.FlagPlaced = true;
                    ShowcaseEvent(server, hub, $"Carrier CV detaches — Gamma's flag is spotted at {layout.FlagPos}.");
                }
                var cv = layout.Bots.First(x => x.Role == ScRole.Carrier);
                var cvShip = ScShip(server, cv.ShipId);
                FlyTo(server, cv, layout.FlagPos, 3f, 220f);
                if (cvShip is { Flag: not null })
                {
                    st.CarrierHasFlag = true;
                    ShowcaseEvent(server, hub, $"Flag seized in flight — ship {cv.ShipId} now carries side {cvShip.Flag}'s colours (real GAIN_FLAG pickup).");
                    st.Phase = ScPhase.FlagSecure;
                    st.PhaseEnter = now;
                }
                else if (now - st.PhaseEnter > 45)
                {
                    ShowcaseEvent(server, hub, "Flag pickup timed out — proceeding (carrier returning).");
                    st.Phase = ScPhase.FlagSecure;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.FlagSecure:
            {
                var cv = layout.Bots.First(x => x.Role == ScRole.Carrier);
                var cvShip = ScShip(server, cv.ShipId);
                // Return to just inside the own-side station's DockRadius → the real
                // dock pass trips, OnShipDocked secures the flag (P201), GAIN_FLAG(NA).
                var dockTarget = layout.StationPos + new Vector3(50f, 0f, 50f);
                if (cvShip is { StationId: null })
                    FlyTo(server, cv, dockTarget, 60f, 250f);
                // Secure only when the carrier actually carried a flag AND is now
                // docked — never on a plain (flag-less) dock, so we can't narrate
                // a fake secure.
                if (st.CarrierHasFlag && cvShip is { StationId: not null })
                {
                    var sflags = mission.Sides.First(s => s.Id == side0).Flags;
                    st.SecuredSideFlags = sflags;
                    ShowcaseEvent(server, hub, $"Carrier docks at the garrison and secures the flag — Alpha now holds {sflags} flag(s) (real secure-on-dock, GAIN_FLAG NA).");
                    st.Phase = ScPhase.Economy;
                    st.PhaseEnter = now;
                }
                else if (now - st.PhaseEnter > 45)
                {
                    ShowcaseEvent(server, hub, "Dock/secure timed out — proceeding.");
                    st.Phase = ScPhase.Economy;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.Economy:
            {
                // Initialise once even though the transition into Economy already
                // set PhaseEnter (FlagSecure does) — so the resupply event, the
                // payday baseline, and the donation-send timer are all anchored here.
                if (!st.EconomyInit)
                {
                    st.EconomyInit = true;
                    st.PhaseEnter = now;
                    // The dock/resupply beat doubles up here: the carrier is docked
                    // after securing the flag, holding while the side's payday lands.
                    ShowcaseEvent(server, hub, "Carrier resupplies at the garrison; the side treasury awaits its first payday.");
                    st.EconomySideMoney = mission.Sides.First(s => s.Id == side0).Money;
                }
                var donor = layout.Bots.First(x => x.Role == ScRole.Attacker1);
                var recip = layout.Bots.First(x => x.Role == ScRole.Attacker0);
                // Narration hook: when the automatic payday (every 60s: IncomePerPayday *
                // civ income -> side.Money, then split to wallets) actually lands, quote
                // the REAL treasury delta before donating from the now-funded wallet.
                var alpha = mission.Sides.First(s => s.Id == side0);
                if (!st.PaydayNarrated && alpha.Money > st.EconomySideMoney)
                {
                    st.PaydayNarrated = true;
                    ShowcaseEvent(server, hub, $"Galactic payday — Alpha treasury climbs to {alpha.Money} credits (real income disbursed to pilot wallets).");
                }
                if (!st.EconomySent)
                {
                    var wallet = server.Ships.First(s => s.Ship.Id == donor.ShipId).Wallet.Money;
                    if (wallet > 10)
                    {
                        st.DonationAmount = Math.Min(1000, wallet - 20);
                        donor.Bot.ResetMoneyChanges();
                        donor.Bot.SendPlayerDonate(recip.ShipId, st.DonationAmount);
                        st.EconomySent = true;
                        st.EconomySentAt = now;
                        Console.WriteLine($"[showcase] economy: {donor.Name} (wallet {wallet}) donates {st.DonationAmount} to {recip.Name}");
                    }
                }
                else
                {
                    // Poll for the REAL MONEY_CHANGE the server broadcast back; the
                    // 12s fallback counts from the donation SEND (not phase entry, which
                    // includes the payday wait) so it can't starve the real-data line.
                    var mc = donor.Bot.MoneyChanges.FirstOrDefault();
                    if (mc is not null)
                    {
                        ShowcaseEvent(server, hub, $"Finance: pilot donates {st.DonationAmount} credits to ship {recip.ShipId} — " +
                                                   $"real MONEY_CHANGE dMoney={mc.DMoney} (sidTo={mc.SidTo}).");
                        st.Phase = ScPhase.Vote;
                        st.PhaseEnter = now;
                    }
                    else if (st.EconomySentAt >= 0 && now - st.EconomySentAt > 12)
                    {
                        ShowcaseEvent(server, hub, $"Finance: pilot transfers {st.DonationAmount} credits to ship {recip.ShipId} (donation sent).");
                        st.Phase = ScPhase.Vote;
                        st.PhaseEnter = now;
                    }
                }
                // Hard stop so the demo can never wedge here (first payday is ~60s post-start).
                if (now - st.PhaseEnter > 100)
                {
                    ShowcaseEvent(server, hub, "Economy beat closed (treasury never funded in time) — continuing.");
                    st.Phase = ScPhase.Vote;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.Vote:
            {
                if (st.PhaseEnter < 0) st.PhaseEnter = now;
                var initiator = layout.Bots.First(x => x.Role == ScRole.Attacker1);   // non-leader on side0
                if (st.MutinyBallotId < 0)
                {
                    initiator.Bot.SendChat("#mutiny");
                    var bal = initiator.Bot.Ballots.FirstOrDefault();
                    if (bal is not null) st.MutinyBallotId = (short)bal.BallotId;
                }
                else if (!st.MutinyVoted)
                {
                    // Eligible side-0 voters are the non-initiators (the leader and
                    // the carrier); the proposer's own vote is refused by the server
                    // (MutinyBallot excludes the initiator — live-observed in the
                    // first pass). All of them voting yes passes the ballot and
                    // transfers command to the initiator.
                    foreach (var sc in layout.Bots.Where(x => x.SideId == side0 && x.ShipId != initiator.ShipId))
                        sc.Bot.SendVote(st.MutinyBallotId, agree: true);
                    st.MutinyVoted = true;
                }
                var side = mission.Sides.First(s => s.Id == side0);
                if (side.LeaderShipId == initiator.ShipId)
                {
                    ShowcaseEvent(server, hub, $"Leadership vote passes — command of side {side0} transfers to ship {initiator.ShipId} (real mutiny ballot, SET_TEAM_LEADER).");
                    st.Phase = ScPhase.Finale;
                    st.PhaseEnter = now;
                }
                else if (now - st.PhaseEnter > 25)
                {
                    ShowcaseEvent(server, hub, $"Vote resolved (leader now ship {side.LeaderShipId}) — moving to the finale.");
                    st.Phase = ScPhase.Finale;
                    st.PhaseEnter = now;
                }
                break;
            }
            case ScPhase.Finale:
            {
                if (st.PhaseEnter < 0) st.PhaseEnter = now;
                // A short closing engagement: the new leader's fighter re-engages
                // any surviving picket, then the fleet holds as the camera lingers.
                var survivors = layout.Bots.Where(x => x.Role is ScRole.Target0 or ScRole.Target1 &&
                                                       ScShip(server, x.ShipId) is { Dead: false } s).Select(x => ScShip(server, x.ShipId)!.Position).ToList();
                if (survivors.Count > 0 && st.FinaleShots < 400)
                {
                    foreach (var sc in layout.Bots.Where(x => x.Role is ScRole.Attacker0 or ScRole.Attacker1))
                        FlyTo(server, sc, survivors[0], 30f, 300f, stateM: ShipControlState.AllWeapons);
                    st.FinaleShots++;
                }
                else
                {
                    foreach (var sc in layout.Bots.Where(x => x.Role is ScRole.Attacker0 or ScRole.Attacker1))
                        sc.Bot.CommandControls(ControlData.Neutral);
                    if (st.Phase != ScPhase.Held)
                    {
                        ShowcaseEvent(server, hub, "Finale — the fleet stands watch over Ganymede; the trailer fades out.");
                        st.Phase = ScPhase.Held;
                    }
                }
                break;
            }
            case ScPhase.Held:
            {
                foreach (var sc in layout.Bots.Where(x => x.Role is ScRole.Attacker0 or ScRole.Attacker1))
                    sc.Bot.CommandControls(ControlData.Neutral);
                break;
            }
        }
    }

    /// <summary>Place side 1's flag treasure in the arena at <c>layout.FlagPos</c>,
    /// the exact P201 <c>PlaceFlagTreasure</c> seeding shape (TreasureId = the
    /// ORIGINAL side whose flag this is) adapted to the showcase's real
    /// discovered sector/side ids — a long-lived, stationary treasure so the
    /// carrier has to fly to it.</summary>
    private static void PlaceShowcaseFlag(Srv server, ScLayout layout)
    {
        var mission = server.Missions[0];
        var fly = layout.Arena;
        short enemySide = layout.Bots.First(x => x.Role == ScRole.Target0).SideId;   // side 1
        var pos = layout.FlagPos;
        var data = new DataTreasureIGC(
            P0: [pos.X, pos.Y, pos.Z],
            V0: [0f, 0f, 0f],
            Lifespan: 9999f,
            Time0: 0u,
            ObjectId: 8420,
            TreasureId: enemySide,          // side 1's flag — a side-0 carrier may pick it up
            ClusterId: fly.Id,
            Amount: 0,
            TreasureCode: TreasureCode.Flag,
            CreateNow: true);
        var t = new Treasure(8420, fly, data, 0.0);
        fly.Add(t);
        Console.WriteLine($"[showcase] placed side {enemySide} flag treasure oid 8420 at {pos}");
    }

    /// <summary>Narrate every real kill the same way Main does (Bot.Kills,
    /// KILL_SHIP on the wire), deduped by victim ship id.</summary>
    private static void BroadcastShowcaseKills(Srv server, SpectatorHub hub, ScLayout layout, ScState st)
    {
        foreach (var bot in layout.Bots.Select(x => x.Bot))
        {
            foreach (var kill in bot.Kills.Where(k => st.SeenKills.Add(k.ShipId)))
            {
                hub.Broadcast(JsonSerializer.Serialize(
                    new { type = "kill", victim = kill.ShipId, killer = kill.IdCredit, simTime = Round2(server.SimNow) }, JsonOpts));
                var vName = layout.Bots.FirstOrDefault(b => b.ShipId == kill.ShipId)?.Name ?? $"#{kill.ShipId}";
                var kName = layout.Bots.FirstOrDefault(b => b.ShipId == kill.IdCredit)?.Name ?? $"#{kill.IdCredit}";
                ShowcaseEvent(server, hub, $"Combat: {kName} downs {vName} — real KILL_SHIP observed.");
            }
        }
    }

    // ── P418 attach mode ───────────────────────────────────────────────────
    //
    // A REAL spectator client instead of a private in-process server. Every
    // other vizfight mode boots its own Server and choreographs bots it owns
    // outright. attach is different: it connects to an EXTERNAL, already-
    // running server (the exact host:port a real MonoGame client is connected
    // to), seats a throwaway spectator ship, tracks the server's live
    // ClientWorld over the real wire, and republishes what it actually
    // receives into the UNCHANGED wwwroot spectator page. Real Bot peers are
    // then choreographed against that SAME server through wire-only commands —
    // the only ones a real player's client has: seat (POSITIONREQ), ready
    // (PLAYER_READY), launch (BUY_LOADOUT), movement/fire (SHIP_UPDATE with
    // AllWeapons). Because the spectator
    // and the MonoGame client are both ordinary clients in the same starting
    // cluster, the page and the client render the SAME real ships, positions
    // and kills on the wire.
    //
    // Honest scope vs the self-hosted showcase (details in P418 report):
    //  - Opposing-side combat is NOT shown across two wings: an opposing-side
    //    ship undocks into ITS OWN side's home sector (a different cluster than
    //    the spectator), so it would be invisible to both the spectator and a
    //    side-matched MonoGame client. The whole fleet therefore seats on the
    //    spectator's side and stays in one cluster, where BOTH views see it.
    //    Same-side hits are suppressed by the friendly-fire gate, so the
    //    visible action is real motion + projectile-spray (real wire-only
    //    events), not kills. A genuine KILL_SHIP path is still wired through
    //    SpectatorFrame and proven by the SpectatorFrameLiveTests unit test
    //    (a real kill crossed the wire there).
    //  - NPC-drone kill + flag-treasure placement are DROPPED — they require
    //    direct in-process Server access a real external client cannot
    //    exercise (SpawnNpcShip / injecting a flag Treasure into a sector).
    //  - Alliance/vote/donate beats are not driven here (they need a reacting
    //    party and add little to the side-by-side "same game" proof).

    private enum AttachPhase { Connecting, ReadyUp, WaitStarted, Launch, Flight, Regroup, Mining, DockCycle, Combat, Held }

    private sealed class AttachBot
    {
        public BotClass Bot = null!;
        public string Name = "";
        public short ShipId;
        public short SideId;
        public bool IsAttack;
        public bool Launched;
        public bool EverSeen;                 // the spectator's ClientWorld has shown this ship at least once
        public double LastRelaunchAt = double.NegativeInfinity;
        public double WeavePhase;
        public double WeaveFreq;
        public double WeaveAmp;
    }

    /// <summary>
    /// P420 — a real opposing-side peer for the cross-side rendezvous. Unlike a
    /// friendly <see cref="AttachBot"/> (whose pose the choreography reads from
    /// the spectator's single shared <c>ClientWorld</c>), each enemy is its own
    /// <see cref="ConnectionSession"/> — so the choreography can read the ship's
    /// real pose <em>and</em> its own cluster's warp geometry while it transits
    /// clusters the spectator never sees (the spectator only ever sees
    /// same-side ships anywhere plus cross-side ships in its OWN cluster). The
    /// full map is exported to every peer, so the enemy's world also carries the
    /// complete warp connectivity needed to route itself warp-by-warp.
    /// </summary>
    private sealed class AttachEnemyBot
    {
        public ConnectionSession Cs = null!;
        public string Name = "";
        public short ShipId;
        public short SideId;
        public bool Launched;
        public bool Arrived;                  // reached the spectator's cluster
        public short? TargetId;               // the friendly ship this enemy is engaging
        public double LastTargetAt = double.NegativeInfinity;
        public double LastBoostAt = double.NegativeInfinity;
        public double LastCloakAt = double.NegativeInfinity;
        public double LastRelaunchAt = double.NegativeInfinity;
        public double LastNarrateAt = double.NegativeInfinity;
        public List<short> Path = new();
        public short PathStart = -1;
        public short PathGoal = -1;
        public short LastCluster = -1;
        public Dictionary<short, short>? WarpDest;
        public double WeavePhase;
        public double WeaveFreq;
        public double WeaveAmp;
    }

    private static Dictionary<short, string> HullNameMap()
    {
        var core = RepresentativeCore();
        return core.HullTypes.Values.ToDictionary(h => h.HullId, h => h.Name);
    }

    /// <summary>Resolve the OUTER weapon part id for the fight weapon (the
    /// same discriminating-match showcase's ScHullAndWeapon uses), so an
    /// attacker's BUY_LOADOUT re-mounts a real gun after the server's
    /// ApplyLoadout clears the loadout.</summary>
    private static short? ResolveOuterWeaponPartId(StaticCore core, DataPartTypeIGC.Weapon weapon, DataHullTypeIGC hull)
    {
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon))
        {
            if (p.Type is DataPartTypeIGC.Weapon w && w.ProjectileTypeId == weapon.ProjectileTypeId
                && w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                return p.PartId;
        }
        return null;
    }

    /// <summary>Poll <paramref name="done"/> on a ~20ms cadence for up to
    /// <paramref name="maxMs"/>, sleeping (the external server ticks itself —
    /// there is no local Server to pump). Returns true once done.</summary>
    private static bool WaitUntil(Func<bool> done, int maxMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (!done() && DateTime.UtcNow < deadline && !Cancelled())
            Thread.Sleep(20);
        return done();
    }

    /// <summary>Broadcast one tick frame + any new kills + lazily-discovered
    /// sector geometry from the spectator's live ClientWorld (all under the
    /// session gate). Mirrors the other modes' per-tick broadcast, sourced
    /// from the wire instead of an owned Srv.</summary>
    private static void BroadcastAttach(
        ConnectionSession cam, SpectatorHub hub, Dictionary<short, string> hullNames,
        Dictionary<short, string> sectorCache, ref long modelCounter, HashSet<short> seenKills, double simTime,
        List<AttachEnemyBot> enemies, short enemyHullId)
    {
        // Real self-pose for every enemy peer, gathered from EACH ONE'S OWN
        // ClientWorld (the spectator can't see cross-side ships outside its own
        // cluster — same reason DriveEnemyContingent reads EnemyCluster this
        // way) — folded into the SAME tick frame as SpectatorFrame.ExtraShip
        // entries, so the enemy contingent is actually rendered, not just
        // counted in the sectorCensus below.
        var extraShips = new List<SpectatorFrame.ExtraShip>(enemies.Count);
        var enemySectors = new Dictionary<short, short>(enemies.Count);
        foreach (var en in enemies)
        {
            short c = EnemyCluster(en.Cs);
            enemySectors[en.ShipId] = c;
            if (c < 0 || !TryGetEnemyPose(en.Cs, en.ShipId, out var epos, out var eorient)) continue;
            bool alive;
            lock (en.Cs.Gate) alive = en.Cs.World.Ships.TryGetValue(en.ShipId, out var ee) && !ee.IsDead;
            Vector3? fwd;
            try { fwd = eorient.GetForward(); } catch { fwd = null; }
            extraShips.Add(new SpectatorFrame.ExtraShip(en.ShipId, enemyHullId, en.SideId, epos, fwd, alive ? 1f : 0f, alive, c));
        }

        // Real per-sector ship census. Combat-mode ships carry no `sector`
        // field on the wire (the frontend's own explore/combat classifier
        // relies on that absence, so the per-ship TickFrame schema can't
        // change), so this is broadcast as a separate message the frontend
        // uses ONLY for its sector-population display. Friendly-side ships
        // (bots + spectator + any real player) are read from the spectator's
        // own ClientWorld, which sees every same-side ship's real, live
        // SectorId (SET_CLUSTER-stamped) regardless of cluster. Enemy peers
        // are cross-side and invisible to the spectator outside its own
        // cluster, so each is counted from ITS OWN ClientWorld's own-ship
        // SectorId instead (the same source DriveEnemyContingent's own
        // EnemyCluster already trusts for routing).
        var census = new Dictionary<string, int>();
        void Count(short sectorId)
        {
            if (sectorId < 0) return;
            var key = sectorId.ToString();
            census[key] = census.GetValueOrDefault(key) + 1;
        }

        lock (cam.Gate)
        {
            // An enemy that has crossed INTO the spectator's cluster becomes
            // visible to the spectator's own ClientWorld — the same ship then
            // exists BOTH as a cam.World.Ships entry and as an ExtraShip /
            // enemySectors entry. Counting both double-counts it (observed
            // live post-rendezvous: census said 46 for 31 real ships), and
            // emitting both puts two same-id entries in one tick frame (the
            // frontend dedupes by id, but the wire shape is wrong). Prefer
            // the spectator-visible entry: drop those enemies from the extra
            // list and count each enemy from its own session ONLY when the
            // spectator can't see it.
            extraShips.RemoveAll(x => cam.World.Ships.ContainsKey(x.Id));
            foreach (var (shipId, c) in enemySectors)
                if (!cam.World.Ships.ContainsKey(shipId)) Count(c);
            // Prune the transient entity lists the consumer normally prunes
            // (Game1 calls these every frame), so they stay bounded.
            long nowLocal = Environment.TickCount64;
            cam.World.PruneExpiredProjectiles(nowLocal);
            cam.World.PruneExpiredMissiles(nowLocal);
            cam.World.PruneExpiredChaff(nowLocal, null);
            cam.World.PruneExpiredExplosions(nowLocal);
            cam.World.TakeNewHullHits();   // hull-hit sparks aren't in the spectator models; drain to bound the list

            // Lazily emit one-time sector geometry for clusters the world has
            // described (the spectator's own starting cluster + any it travels).
            foreach (var cid in cam.World.Clusters.Keys)
            {
                if (sectorCache.ContainsKey(cid)) continue;
                var geo = SpectatorFrame.BuildSectorFrame(cam.World, cid, id =>
                    cam.World.Clusters.TryGetValue(id, out var dc) ? dc.Name : $"c{id}");
                if (geo is not null) { sectorCache[cid] = geo; hub.Broadcast(geo); }
            }

            // Drain real KILL_SHIP events off the wire into `kill` frames.
            foreach (var k in cam.World.Events.Kills)
            {
                if (!seenKills.Add(k.ShipId)) continue;
                hub.Broadcast(SpectatorFrame.BuildKillFrame(k, simTime));
                Console.WriteLine($"[attach][t {simTime:0.0}] kill: ship {k.ShipId} killed by {k.IdCredit} (real KILL_SHIP)");
            }

            hub.Broadcast(SpectatorFrame.BuildTickFrame(cam.World, id => hullNames.GetValueOrDefault(id, ""), simTime, ref modelCounter, extraShips));

            foreach (var e in cam.World.Ships.Values)
                if (e.Latest is not null) Count(e.SectorId);
        }
        // (enemies invisible to the spectator were already counted inside the
        // lock, from their own sessions' self-pose — see above.)
        hub.Broadcast(JsonSerializer.Serialize(new { type = "sectorCensus", counts = census }, JsonOpts));
    }

    private static void AttachEvent(SpectatorHub hub, string text, double simTime)
    {
        hub.Broadcast(JsonSerializer.Serialize(new { type = "event", text, simTime = Round2(simTime) }, JsonOpts));
        Console.WriteLine($"[attach][t {simTime:0.0}] event: {text}");
    }

    /// <summary>Read a bot's LIVE position+orientation from the spectator's real
    /// ClientWorld (the server-reported pose for the same-side fleet), under the
    /// session gate. False if the spectator has no pose for that ship yet.</summary>
    private static bool TryGetBotPose(ConnectionSession cam, short shipId, out Vector3 pos, out Orientation orient)
    {
        lock (cam.Gate)
        {
            if (cam.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
            {
                pos = lu.Position;
                orient = lu.Orientation;
                return true;
            }
        }
        pos = Vector3.Zero;
        orient = Orientation.Identity;
        return false;
    }

    // ── P420: cross-side rendezvous helpers ────────────────────────────────
    //
    // The opposing contingent is a set of real opposing-side peers, each its
    // own ConnectionSession so the choreography can read its pose + its own
    // cluster's warp geometry while it transits clusters the spectator never
    // sees. Everything here is read from the REAL world the server sent the
    // peer — no synthetic state.

    /// <summary>Read an enemy peer's LIVE pose from ITS OWN ClientWorld (the
    /// spectator cannot see cross-side ships in other clusters, so the peer's
    /// own world is the only truthful source while it transits).</summary>
    private static bool TryGetEnemyPose(ConnectionSession cs, short shipId, out Vector3 pos, out Orientation orient)
    {
        lock (cs.Gate)
        {
            if (cs.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
            {
                pos = lu.Position;
                orient = lu.Orientation;
                return true;
            }
        }
        pos = Vector3.Zero;
        orient = Orientation.Identity;
        return false;
    }

    /// <summary>The cluster an enemy peer's own client is currently in (its
    /// own ship's sector, re-stamped by the server's SET_CLUSTER per transit).</summary>
    private static short EnemyCluster(ConnectionSession cs)
    {
        lock (cs.Gate)
        {
            if (cs.World.MyShipId is { } me && cs.World.Ships.TryGetValue(me, out var e))
                return e.SectorId;
        }
        return -1;
    }

    /// <summary>Build the warpId → destination-cluster map from an enemy peer's
    /// real world geometry. The server exports EVERY warp of every sector to
    /// every peer, and each warp's <c>DestinationId</c> names its partner warp —
    /// so the partner's cluster is the destination sector. This is the exact,
    /// complete routing table a peer has at its disposal.</summary>
    private static bool TryBuildWarpDestMap(ConnectionSession cs, out Dictionary<short, short> map)
    {
        map = new Dictionary<short, short>();
        lock (cs.Gate)
        {
            foreach (var w in cs.World.Warps.Values)
                if (cs.World.Warps.TryGetValue(w.DestinationId, out var partner))
                    map[w.WarpId] = partner.ClusterId;
        }
        return map.Count > 0;
    }

    /// <summary>The position of the warp in cluster <paramref name="cur"/> that
    /// leads to cluster <paramref name="next"/> (real DataWarpIGC geometry from
    /// the peer's world, routed via the warp-dest map).</summary>
    private static bool TryFindWarpTo(ConnectionSession cs, short cur, short next, Dictionary<short, short> warpDest, out Vector3 pos)
    {
        lock (cs.Gate)
        {
            foreach (var w in cs.World.Warps.Values)
            {
                if (w.ClusterId != cur) continue;
                if (warpDest.TryGetValue(w.WarpId, out var d) && d == next)
                {
                    pos = Vec3(w.Position);
                    return true;
                }
            }
        }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>Pick a real opposing side id for the enemy contingent — any
    /// mission side other than the spectator's (falls back to the other of
    /// {0,1}, and -1 when no opposing side exists, e.g. a teams:1 server).</summary>
    private static short PickEnemySide(ConnectionSession cam, short fleetSide)
    {
        lock (cam.Gate)
        {
            if (cam.World.SideRoster is { Sides.Length: > 0 } sr)
            {
                foreach (var s in sr.Sides)
                    if (s.SideId != fleetSide) return s.SideId;
            }
            if (fleetSide == 0) return 1;
            if (fleetSide > 0) return 0;
        }
        return -1;
    }

    /// <summary>
    /// P420 — the full-inventory loadout: a real weapon + a real afterburner
    /// (P412 fuel/thrust) + a real cloak (P413) + a real shield, each mounted at
    /// its own EquipmentType's slot 0. All four are real, buyable part records
    /// in PCore001 (verified by dump: Weapon/Afterburner/Cloak/Shield all
    /// present). A missile launcher and a chaff dispenser are deliberately NOT
    /// requested: PCore001.igc contains no <c>ET_Magazine</c> or
    /// <c>ET_ChaffLauncher</c> part record, so those two can never be granted by
    /// the server's authoritative <c>ApplyLoadout</c> (<c>mission.Core.PartTypes</c>
    /// has no such id to resolve) — a wire-only loadout simply cannot equip
    /// them. Because every attach bot seats on the fight hull (the first
    /// "Scout" by id == Fight.Value.hull), the loadout is hull-stable, so the
    /// server's hull-change-only <c>CanMountPart</c> gate never fires and every
    /// requested part mounts (the afterburner/cloak mount internally, not to a
    /// weapon hardpoint).
    /// </summary>
    private static List<ExpandedPartData> FullInventoryParts(StaticCore core, short? weaponPartId)
    {
        static short? First(StaticCore c, EquipmentType et) =>
            c.PartTypes.Values.Where(p => p.EquipmentType == et)
             .OrderBy(p => p.PartId).Select(p => p.PartId).Cast<short?>().FirstOrDefault();

        var parts = new List<ExpandedPartData>();
        if (weaponPartId is { } w) parts.Add(new ExpandedPartData(w, 1, 0, 255));
        if (First(core, EquipmentType.Afterburner) is { } ab) parts.Add(new ExpandedPartData(ab, 1, 0, 255));
        if (First(core, EquipmentType.Cloak) is { } ck) parts.Add(new ExpandedPartData(ck, 1, 0, 255));
        if (First(core, EquipmentType.Shield) is { } sh) parts.Add(new ExpandedPartData(sh, 1, 0, 255));
        return parts;
    }

    /// <summary>Point a real peer at a specific ship across sides: the P153/P154
    /// SetTarget action over ORDER_CHANGE(93) with <c>command = CmdCurrent</c>,
    /// which the server resolves cross-side and writes into the target ship's
    /// <see cref="Ship.CommandTarget"/> (the missile-lock / gun-target slot).
    /// Safe to repeat; the server treats a repeat as a re-set.</summary>
    private static void AcquireCrossSideTarget(ConnectionSession cs, short myShipId, short enemyId)
        => cs.SendOrderChange(myShipId, OrderChange.CmdQueued, OrderChange.CmdCurrent, IgcObjectType.Ship, enemyId);


    /// <summary>Drive one attach bot toward <paramref name="target"/> using the
    /// exact shared face-and-thrust steering law the sim's own NPC autopilot
    /// uses (<c>Ship.SteerToward</c>, Ship.cs:4911-4952) — the same law
    /// <see cref="AttachLeashSteer"/> validates and the self-hosted <c>FlyTo</c>
    /// uses, but reading the bot's pose from the spectator's live ClientWorld
    /// (an external-server attach has no owned <c>Srv</c>). Sends a fresh
    /// SHIP_UPDATE every tick so the server reacts to the changing geometry.
    /// <paramref name="stateM"/> fires mounted weapons when set (AllWeapons).</summary>
    private static void AttachFlyTo(ConnectionSession cam, AttachBot ab, Vector3 target, float arrival, float brake, int stateM)
    {
        if (!TryGetBotPose(cam, ab.ShipId, out var pos, out var orient)) return;
        var toTarget = target - pos;
        float dist = toTarget.Length();
        if (dist <= 1e-3f)
        {
            ab.Bot.CommandControls(ControlData.Neutral, stateM);
            return;
        }
        var dir = toTarget / dist;
        var body = orient.TimesInverse(Vector3.CrossProduct(orient.GetForward(), dir));
        float yaw = Math.Clamp(2.0f * body.Y, -1f, 1f);
        float pitch = Math.Clamp(2.0f * -body.X, -1f, 1f);
        float distFactor = dist >= brake ? 1f : Math.Clamp((dist - arrival) / (brake - arrival), 0f, 1f);
        float align = Math.Clamp(-orient.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * align - 1f, -1f, 1f);
        ab.Bot.CommandControls(new ControlData(yaw, pitch, 0f, throttle), stateM);
    }

    private static Vector3 Vec3(float[] v) => v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    /// <summary>Compute the P419 bounded-flight anchor + leash from the REAL
    /// world at launch time (not guessed blind): the anchor is the fleet's
    /// centroid right after undock; the leash is sized off the fleet's own
    /// cluster geometry (warps/stations/asteroids) so the fleet provably stays
    /// inside the area the web page / MonoGame client already frame — the
    /// camera fits the focused sector's geometry bounding sphere and zooms to
    /// radius×2.4 (wwwroot/index.html <c>reframeCamera</c>). Returns the
    /// fleet's sector id (the spectator's, since the whole fleet shares its
    /// home cluster).</summary>
    private static (Vector3 anchor, float leash, short sector) ComputeFleetLeash(ConnectionSession cam, List<AttachBot> bots)
    {
        short sector = -1;
        var pts = new List<Vector3>();
        lock (cam.Gate)
        {
            if (cam.World.MyShipId is { } me && cam.World.Ships.TryGetValue(me, out var sp))
                sector = sp.SectorId;
            foreach (var ab in bots)
            {
                if (cam.World.Ships.TryGetValue(ab.ShipId, out var e) && e.Latest is { } lu)
                {
                    pts.Add(lu.Position);
                    if (sector < 0) sector = e.SectorId;
                }
            }
        }
        if (pts.Count == 0) return (Vector3.Zero, 1600f, sector);
        var anchor = new Vector3(
            pts.Average(p => p.X), pts.Average(p => p.Y), pts.Average(p => p.Z));

        // Stay inside the fleet's own cluster footprint (its rendered view).
        float foot = 0f;
        lock (cam.Gate)
        {
            foreach (var st in cam.World.Stations.Values)
                if (st.ClusterId == sector) foot = MathF.Max(foot, (Vec3(st.Position) - anchor).Length());
            foreach (var a in cam.World.Asteroids.Values)
                if (a.ClusterId == sector) foot = MathF.Max(foot, (Vec3(a.Position) - anchor).Length());
            foreach (var w in cam.World.Warps.Values)
                if (w.ClusterId == sector) foot = MathF.Max(foot, (Vec3(w.Position) - anchor).Length());
        }
        // 0.85× the footprint keeps every ship comfortably inside the sector
        // view with margin; clamp to a sane band so a degenerate geometry never
        // yields an absurd radius. Default 1600 if no geometry has arrived yet.
        float leash = foot > 1f ? Math.Clamp(foot * 0.85f, 500f, 4800f) : 1600f;
        return (anchor, leash, sector);
    }

    /// <summary>The home station: the station in the fleet's sector nearest the
    /// anchor — where the fleet undocked from, and where DockCycle flies them
    /// back into for the real dock-then-relaunch beat.</summary>
    private static bool TryFindHomeStation(ConnectionSession cam, short sector, Vector3 anchor, out Vector3 pos)
    {
        DataStationIGC? best = null;
        float bestD = float.MaxValue;
        lock (cam.Gate)
        {
            foreach (var st in cam.World.Stations.Values)
            {
                if (st.ClusterId != sector) continue;
                float d = (Vec3(st.Position) - anchor).Length();
                if (d < bestD) { bestD = d; best = st; }
            }
        }
        if (best is { } b) { pos = Vec3(b.Position); return true; }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>The mining target: the nearest MINABLE (real He3/gold-bearing)
    /// asteroid in the fleet's sector to the anchor — a real DataAsteroidIGC
    /// position from the world state, not a synthetic point.</summary>
    private static bool TryFindMiningTarget(ConnectionSession cam, short sector, Vector3 anchor, out short id, out Vector3 pos)
    {
        DataAsteroidIGC? best = null;
        float bestD = float.MaxValue;
        lock (cam.Gate)
        {
            foreach (var a in cam.World.Asteroids.Values)
            {
                if (a.ClusterId != sector) continue;
                if ((a.AabmCapabilities & DataAsteroidIGC.AabmMineHe3) == 0
                    && (a.AabmCapabilities & DataAsteroidIGC.AabmMineGold) == 0) continue;
                float d = (Vec3(a.Position) - anchor).Length();
                if (d < bestD) { bestD = d; best = a; }
            }
        }
        if (best is { } b) { id = b.AsteroidId; pos = Vec3(b.Position); return true; }
        id = -1;
        pos = Vector3.Zero;
        return false;
    }


    /// <summary>Fly one enemy peer toward <paramref name="target"/> using the
    /// same shared face-and-thrust autopilot law as <see cref="AttachFlyTo"/>, but
    /// reading the pose from the peer's OWN ClientWorld (the spectator cannot see
    /// cross-side ships in other clusters while the enemy transits).</summary>
    private static void AttachEnemyFlyTo(ConnectionSession cs, AttachEnemyBot en, Vector3 target, float arrival, float brake, int stateM)
    {
        if (!TryGetEnemyPose(cs, en.ShipId, out var pos, out var orient)) return;
        var toTarget = target - pos;
        float dist = toTarget.Length();
        if (dist <= 1e-3f) { en.Cs.CommandControls(ControlData.Neutral, stateM); return; }
        var dir = toTarget / dist;
        var body = orient.TimesInverse(Vector3.CrossProduct(orient.GetForward(), dir));
        float yaw = Math.Clamp(2.0f * body.Y, -1f, 1f);
        float pitch = Math.Clamp(2.0f * -body.X, -1f, 1f);
        float distFactor = dist >= brake ? 1f : Math.Clamp((dist - arrival) / (brake - arrival), 0f, 1f);
        float align = Math.Clamp(-orient.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * align - 1f, -1f, 1f);
        en.Cs.CommandControls(new ControlData(yaw, pitch, 0f, throttle), stateM);
    }

    /// <summary>Recompute an enemy peer's cluster path only when needed: keep
    /// the existing one as long as the peer is still on it and the goal is
    /// unchanged, else re-BFS from its current cluster.</summary>
    private static void EnsureEnemyPath(AttachEnemyBot en, short cur, short goal)
    {
        if (en.PathGoal == goal && en.Path.Count > 0 && en.Path.Contains(cur)) return;
        lock (en.Cs.Gate)
        {
            var g = en.Cs.World.Graph;
            if (g is null) return;
            en.Path = g.FindShortestPath(cur, goal);
        }
        en.PathStart = cur;
        en.PathGoal = goal;
    }

    /// <summary>The next cluster on the enemy's path after <paramref name="cur"/>
    /// (the first hop whose source is the current cluster); <c>cur</c> itself
    /// when it is the goal.</summary>
    private static short EnemyNextHop(AttachEnemyBot en, short cur)
    {
        for (int i = 1; i < en.Path.Count; i++)
            if (en.Path[i - 1] == cur) return en.Path[i];
        return cur;
    }

    /// <summary>A bounded in-place weave for an enemy with nothing specific to
    /// fly to (transit stall, waiting on combat, loiter after rendezvous).</summary>
    private static void LoiterEnemy(AttachEnemyBot en, double now)
    {
        if (!TryGetEnemyPose(en.Cs, en.ShipId, out _, out _)) return;
        double yaw = Math.Sin(now * en.WeaveFreq + en.WeavePhase) * en.WeaveAmp;
        double throttle = 0.5 + 0.2 * Math.Sin(now * en.WeaveFreq * 0.7 + en.WeavePhase * 1.3);
        en.Cs.CommandControls(new ControlData((float)yaw, 0f, 0f, (float)throttle), 0);
    }

    /// <summary>Engagement behaviour for a rendezvoused enemy: pick the nearest
    /// co-located friendly in its OWN world, point itself at it (real cross-side
    /// ORDER_CHANGE target acquisition), and hold AllWeapons so its real guns
    /// deal cross-side damage — with periodic afterburner boost passes and an
    /// evasive cloak beat, the full inventory it carries. Real gunfire only; a
    /// missile salvo is intentionally not requested here (see FullInventoryParts).</summary>
    private static void EngageEnemy(ConnectionSession cam, AttachEnemyBot en, short fleetSide, double now)
    {
        if (!TryGetEnemyPose(en.Cs, en.ShipId, out var myPos, out _)) return;
        short myCluster = EnemyCluster(en.Cs);
        short best = -1;
        float bestD = float.MaxValue;
        lock (en.Cs.Gate)
        {
            foreach (var ship in en.Cs.World.Ships.Values)
            {
                if (ship.ShipId == en.ShipId || ship.IsNotVisible) continue;
                if (ship.SideId != fleetSide) continue;     // friendly only
                if (ship.SectorId != myCluster) continue;   // co-located
                if (ship.Latest is not { } lu) continue;
                float d = (lu.Position - myPos).Length();
                if (d < bestD) { bestD = d; best = ship.ShipId; }
            }
        }
        if (best < 0) { LoiterEnemy(en, now); return; }
        en.TargetId = best;
        if (now - en.LastTargetAt > 3) { AcquireCrossSideTarget(en.Cs, en.ShipId, best); en.LastTargetAt = now; }
        Vector3 tp = Vector3.Zero;
        lock (en.Cs.Gate)
            if (en.Cs.World.Ships.TryGetValue(best, out var te) && te.Latest is { } tlu) tp = tlu.Position;

        bool boost = now - en.LastBoostAt < 4;
        if (now - en.LastBoostAt > 11) { en.LastBoostAt = now; boost = true; }
        bool cloak = now - en.LastCloakAt < 4;
        if (now - en.LastCloakAt > 18) { en.LastCloakAt = now; cloak = true; }
        int stateM = ShipControlState.AllWeapons
                     | (boost ? ShipControlState.AfterburnerButton : 0)
                     | (cloak ? ShipControlState.CloakActive : 0);
        AttachEnemyFlyTo(en.Cs, en, tp, 40f, 220f, stateM);
    }

    /// <summary>
    /// P420 — drive the opposing contingent one tick: launch each enemy at its
    /// OWN home base (a different starting cluster than the spectator's), then
    /// route it warp-by-warp along the real BFS shortest path
    /// (<see cref="ClusterGraph.FindShortestPath"/>) to the spectator's cluster,
    /// narrating each real crossing. Once arrived, engage during a combat window
    /// or loiter otherwise. All positions/geometry are read from each peer's own
    /// live world — the truthful source while it is in clusters the spectator
    /// never sees.
    /// </summary>
    private static void DriveEnemyContingent(
        ConnectionSession cam, SpectatorHub hub, List<AttachEnemyBot> enemies,
        StaticCore core, short? weaponPartId, DataHullTypeIGC fightHull,
        short targetCluster, bool inCombat, double now)
    {
        if (enemies.Count == 0) return;
        foreach (var en in enemies)
        {
            try
            {
                if (!en.Cs.World.MyShipId.HasValue) continue;      // not seated yet
                en.ShipId = en.Cs.World.MyShipId.Value;
                if (!en.Launched)
                {
                    en.Cs.SendBuyLoadout(fightHull.HullId, fLaunch: true,
                        parts: FullInventoryParts(core, weaponPartId));
                    en.Launched = true;
                    Console.WriteLine($"[attach] {en.Name} launched at its OWN home base (opposing side {en.SideId}) with full inventory (weapon/afterburner/cloak/shield)");
                    continue;
                }

                short cur = EnemyCluster(en.Cs);
                if (cur < 0)
                {
                    if (now - en.LastNarrateAt > 20)
                    {
                        en.LastNarrateAt = now;
                        Console.WriteLine($"[attach] {en.Name} own ship pose not in its ClientWorld yet (MyShipId={(en.Cs.World.MyShipId?.ToString() ?? "null")}) — waiting");
                    }
                    continue;
                }

                if (cur == targetCluster)
                {
                    if (!en.Arrived)
                    {
                        en.Arrived = true;
                        AttachEvent(hub, $"Rendezvous — {en.Name} crossed real warps into the spectator's cluster (id {cur}) and is now co-located with the friendly fleet.", now);
                    }
                }

                if (!en.Arrived)
                {
                    EnsureEnemyPath(en, cur, targetCluster);
                    if (en.Path.Count == 0)
                    {
                        // Diagnostic (throttled): why can't we route? Distinguish a
                        // missing ClusterInfo graph (older/CLUSTERINFO-less server)
                        // from a genuinely disconnected topology.
                        if (now - en.LastNarrateAt > 20)
                        {
                            en.LastNarrateAt = now;
                            bool graphOk;
                            lock (en.Cs.Gate) graphOk = en.Cs.World.Graph is not null;
                            Console.WriteLine($"[attach] {en.Name} awaiting routable topology: cluster {cur}, target {targetCluster}, ClusterInfo graph {(graphOk ? "present" : "ABSENT (server sent no CLUSTERINFO)")} — loitering");
                        }
                        LoiterEnemy(en, now);
                        continue;
                    }
                    short next = EnemyNextHop(en, cur);
                    if (next == cur) { en.Arrived = true; en.LastCluster = cur; continue; }
                    en.WarpDest ??= TryBuildWarpDestMap(en.Cs, out var m) ? m : null;
                    if (en.WarpDest is { } wd && TryFindWarpTo(en.Cs, cur, next, wd, out var warpPos))
                    {
                        AttachEnemyFlyTo(en.Cs, en, warpPos, 200f, 450f, 0);
                    }
                    else
                    {
                        // Diagnostic (throttled): routing but no warp found — is
                        // the peer's world missing warp geometry, or is the hop's
                        // partner unresolvable?
                        if (now - en.LastNarrateAt > 20)
                        {
                            en.LastNarrateAt = now;
                            int nWarps;
                            lock (en.Cs.Gate) nWarps = en.Cs.World.Warps.Count;
                            Console.WriteLine($"[attach] {en.Name} in cluster {cur} routing to {next} but no warp found (world has {nWarps} warp(s), warpDest {(en.WarpDest is { } w2 ? w2.Count + " edges" : "null")}) — loitering");
                        }
                        LoiterEnemy(en, now);
                    }

                    if (en.LastCluster != cur && cur != en.PathStart && now - en.LastNarrateAt > 4)
                    {
                        en.LastNarrateAt = now;
                        int idx = en.Path.IndexOf(cur);
                        int warpsLeft = Math.Max(0, (en.Path.Count - 1) - idx);
                        AttachEvent(hub, $"Transit — {en.Name} crosses into sector id {cur} — {warpsLeft} warp(s) remaining to the rendezvous.", now);
                    }
                    en.LastCluster = cur;
                }
                else
                {
                    if (inCombat) EngageEnemy(cam, en, cam.World.MySideId ?? -1, now);
                    else LoiterEnemy(en, now);
                }
            }
            catch (Exception e) { Console.WriteLine($"[attach] {en.Name} drive error: {e.Message}"); }
        }
    }

    /// <summary>Combat behaviour for the friendly fleet: every gun-carrier
    /// acquires the nearest co-located enemy in the spectator's world (which now
    /// sees the opposing contingent cross-side) and fires AllWeapons at it with
    /// afterburner boost passes — real cross-side gunfire that can produce real
    /// damage/kills. Non-attackers stay in the bounded leash with a periodic
    /// cloak evasive beat.</summary>
    private static void DriveFriendlyCombat(
        ConnectionSession cam, List<AttachBot> bots, Vector3 anchor, float leashRadius, short enemySide, double now)
    {
        foreach (var ab in bots.Where(b => b.IsAttack))
        {
            try
            {
                if (!TryGetBotPose(cam, ab.ShipId, out var pos, out _)) continue;
                short sector = -1;
                lock (cam.Gate) if (cam.World.Ships.TryGetValue(ab.ShipId, out var se)) sector = se.SectorId;
                short target = -1; float bd = float.MaxValue;
                lock (cam.Gate)
                {
                    foreach (var ship in cam.World.Ships.Values)
                    {
                        if (ship.ShipId == ab.ShipId || ship.IsNotVisible) continue;
                        if (ship.SideId != enemySide) continue;
                        if (ship.SectorId != sector) continue;
                        if (ship.Latest is not { } lu) continue;
                        float d = (lu.Position - pos).Length();
                        if (d < bd) { bd = d; target = ship.ShipId; }
                    }
                }
                if (target < 0) continue;
                if (now % 3 < 0.5)
                    ab.Bot.SendOrderChange(ab.ShipId, OrderChange.CmdQueued, OrderChange.CmdCurrent, IgcObjectType.Ship, target);
                Vector3 tp = Vector3.Zero;
                lock (cam.Gate)
                    if (cam.World.Ships.TryGetValue(target, out var te) && te.Latest is { } tlu) tp = tlu.Position;
                bool boost = now % 11 < 4;
                AttachFlyTo(cam, ab, tp, 40f, 220f,
                    ShipControlState.AllWeapons | (boost ? ShipControlState.AfterburnerButton : 0));
            }
            catch { }
        }
        foreach (var ab in bots.Where(b => !b.IsAttack))
        {
            try
            {
                if (!TryGetBotPose(cam, ab.ShipId, out var pos, out var orient)) continue;
                bool cloak = now % 18 < 4;
                double yaw = Math.Sin(now * ab.WeaveFreq + ab.WeavePhase) * ab.WeaveAmp;
                var (y2, t2) = AttachLeashSteer.Steer(pos, anchor, leashRadius, orient, (float)yaw, 0.5f);
                ab.Bot.CommandControls(new ControlData(y2, 0f, 0f, t2), cloak ? ShipControlState.CloakActive : 0);
            }
            catch { }
        }
    }

    private static int AttachMain(string host, int port, int webPort, int botCount, int? enemyBotCountOverride = null)
    {
        var hullNames = HullNameMap();
        var fight = Fight.Value;
        var core = RepresentativeCore();
        short? weaponPartId = ResolveOuterWeaponPartId(core, fight.weapon, fight.hull);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double SimNow() => sw.Elapsed.TotalSeconds;

        // ── spectator hub + HTTP/WS host (identical web machinery to the other
        //    modes; the frontend is unchanged and reusable as-is) ──
        var hub = new SpectatorHub();
        var sectorCache = new Dictionary<short, string>();
        hub.Seeder = ch =>
        {
            string[] cached;
            lock (sectorCache) cached = sectorCache.Values.ToArray();
            foreach (var json in cached) ch.Writer.TryWrite(Encoding.UTF8.GetBytes(json));
        };
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = ResolveWwwroot() });
        builder.Logging.SetMinimumLevel(LogLevel.Error);
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(webPort));
        var app = builder.Build();
        app.UseWebSockets();
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.Map("/ws", (HttpContext c) => HandleWs(c, hub, c.RequestAborted));
        var appTask = app.RunAsync();
        Console.WriteLine($"[attach] spectator page http://127.0.0.1:{webPort}/  ws://127.0.0.1:{webPort}/ws");

        // ── the spectator CLIENT: a real ConnectionSession peer ──
        using var cam = new ConnectionSession();
        cam.Connect((ushort)port, "AttachCam", host);
        cam.SendLogonRequest(Request("ATTACH_CAM"));
        Console.WriteLine($"[attach] spectator connecting to {host}:{port}");
        if (!WaitUntil(() => cam.Ack is not null, 12000) || cam.Ack is not { FValidated: true })
        {
            Console.Error.WriteLine("[attach] spectator LOGONACK never validated");
            return 1;
        }
        Console.WriteLine($"[attach] spectator LOGONACK validated");

        // Seat the spectator: prefer side 0 (any side works; the server spawns
        // every side's ships in the same starting cluster), else auto-pick.
        short camSide = -1;
        lock (cam.Gate)
        {
            if (cam.World.SideRoster is { Sides.Length: > 0 } sr)
                camSide = sr.Sides[0].SideId;
        }
        cam.SendPositionReq(camSide);
        if (!WaitUntil(() => cam.World.MyShipId is not null, 15000))
        {
            Console.Error.WriteLine("[attach] spectator never seated (no ship assigned)");
            return 1;
        }
        Console.WriteLine($"[attach] spectator seated: ship {cam.World.MyShipId} side {cam.World.MySideId ?? -1}");

        // ── the choreographed real Bot peers (wire-only, on the same server) ──
        // ALL bots seat on the spectator's own side. This is deliberate and
        // documented: an opposing-side ship undocks into ITS OWN side's home
        // sector, which is a DIFFERENT cluster than the spectator's — and a
        // client only ever sees same-side ships (anywhere) plus its own
        // cluster (cross-side 176). So to make every choreographed ship appear
        // in BOTH the web view and the MonoGame client's view at once, the
        // whole fleet must share one cluster — seating everyone on the
        // spectator's side puts them all in that side's home sector with the
        // spectator. (Trade-off: the friendly-fire gate then suppresses
        // same-side hits, so "combat" is projectile-spray + chaff, not kills —
        // see the class doc.) Half the bots carry a real gun and hold
        // AllWeapons to spray visible bolts; the rest are escorts that weave.
        short fleetSide = cam.World.MySideId ?? (camSide >= 0 ? camSide : (short)0);
        int n = Math.Max(2, botCount);
        var bots = new List<AttachBot>();
        for (int i = 0; i < n; i++)
        {
            var b = new BotClass { AutoSelectSideAfterLogon = false };
            string name = $"VZB{i:D2}";
            b.Connect((ushort)port, name, host);
            b.SendLogonRequest(Request(name));
            bots.Add(new AttachBot
            {
                Bot = b, Name = name,
                SideId = fleetSide,               // the spectator's side (all in one cluster)
                IsAttack = (i % 2) == 0,
                // Legible stagger for a BIGGER fleet: spawn positions are
                // server-controlled (a wire-only seat can't place a bot), so
                // legibility comes from giving each bot a distinct, non-lockstep
                // weave — prime-spaced phases and a wider freq/amp band than
                // P418's tiny (i%7)/(i%5)/(i%4) so a larger fleet never degrades
                // into synchronized ship-soup.
                WeavePhase = (i * 0.93303299) % (2 * Math.PI),   // ~golden-ratio spacing, no common divisor
                WeaveFreq = 0.046 + (i % 13) * 0.0062,
                WeaveAmp = 0.024 + ((i * 7) % 11) * 0.011,
            });
        }
        if (!WaitUntil(() => bots.All(x => x.Bot.Ack is { FValidated: true }), 12000))
        {
            Console.Error.WriteLine($"[attach] only {bots.Count(x => x.Bot.Ack is { FValidated: true })}/{n} bots logged on");
            return 1;
        }
        foreach (var ab in bots)
            ab.Bot.SendPositionReq(ab.SideId);
        if (!WaitUntil(() => bots.All(x => x.Bot.ShipAssignment is not null), 15000))
        {
            Console.Error.WriteLine($"[attach] only {bots.Count(x => x.Bot.ShipAssignment is not null)}/{n} bots seated");
            return 1;
        }
        foreach (var ab in bots)
            ab.ShipId = ab.Bot.ShipAssignment!.Ship.ShipId;
        Console.WriteLine($"[attach] all {n} bots seated: " + string.Join(", ", bots.Select(b => $"{b.Name}#{b.ShipId}(s{b.SideId})")));

        AttachEvent(hub, $"Attach live — spectator + {n} real bots on {host}:{port}, all ordinary clients of the same server.", SimNow());

        // ── P420: the opposing contingent (a real second side) ────────────────
        // Seed a small contingent of real peers on the OPPOSING side. They seat
        // there, ready up, launch at their OWN home base (a different starting
        // cluster than the spectator's — their side's home sector), then fly
        // real warps along the BFS shortest path to rendezvous in the
        // spectator's cluster. Each is its own ConnectionSession so the
        // choreography can read its pose + own-cluster geometry while it
        // transits clusters the spectator never sees. A second side is the
        // precondition for REAL cross-side missiles (Ship.UpdateMissileLock
        // hard-refuses a same-side lock target) and, because cross-side hits are
        // no longer suppressed by the friendly-fire gate, for REAL combat.
        var enemies = new List<AttachEnemyBot>();
        short enemySide = PickEnemySide(cam, fleetSide);
        int enemyCount = enemyBotCountOverride ?? Math.Clamp(Math.Max(2, botCount / 6), 2, 6);
        double lastCombatAt = -60.0;
        if (enemySide >= 0)
        {
            AttachEvent(hub,
                $"Rendezvous setup — seeding {enemyCount} real enemy peer(s) on opposing side {enemySide}; they will launch at their own home base and fly real warps here.", SimNow());
            for (int i = 0; i < enemyCount; i++)
            {
                var ecs = new ConnectionSession();
                string ename = $"VZA{i:D2}";
                ecs.Connect((ushort)port, ename, host);
                ecs.SendLogonRequest(Request(ename));
                enemies.Add(new AttachEnemyBot
                {
                    Cs = ecs,
                    Name = ename,
                    SideId = enemySide,
                    WeavePhase = (i * 0.9510565163) % (2 * Math.PI),
                    WeaveFreq = 0.041 + (i % 7) * 0.007,
                    WeaveAmp = 0.026 + (i % 5) * 0.014,
                });
            }
            if (!WaitUntil(() => enemies.All(x => x.Cs.Ack is { FValidated: true }), 12000))
                Console.Error.WriteLine($"[attach] only {enemies.Count(x => x.Cs.Ack is { FValidated: true })}/{enemies.Count} enemy peers logged on");
            foreach (var en in enemies) en.Cs.SendPositionReq(en.SideId);
            if (!WaitUntil(() => enemies.All(x => x.Cs.World.MyShipId is not null), 15000))
                Console.Error.WriteLine("[attach] not all enemy peers seated");
            foreach (var en in enemies) en.ShipId = en.Cs.World.MyShipId!.Value;
            Console.WriteLine("[attach] enemy contingent seated on opposing side " + enemySide + ": "
                + string.Join(", ", enemies.Select(e => $"{e.Name}#{e.ShipId}")));
        }
        else
        {
            Console.WriteLine("[attach] no opposing side available (single-team server?) — skipping the enemy contingent and cross-side combat.");
        }

        // ── choreography state machine + per-tick broadcast loop ──
        var phase = AttachPhase.Connecting;
        double phaseEnter = SimNow();
        long modelCounter = 0;
        var seenKills = new HashSet<short>();
        var lastNavEvent = -10.0;

        // P419 bounded-flight state: the fixed fleet anchor + leash radius (both
        // derived from the REAL launch/world geometry, not guessed), the real
        // mining-asteroid target, and the home station the dock beat uses.
        Vector3 anchor = Vector3.Zero;
        float leashRadius = 1600f;
        bool leashReady = false;
        short fleetSector = -1;
        bool miningAssigned = false;
        short miningAsteroidId = -1;
        Vector3 miningTarget = Vector3.Zero;
        int miningBots = Math.Min(2, bots.Count);
        bool homeStationKnown = false;
        Vector3 homeStationPos = Vector3.Zero;

        while (!Cancelled())
        {
            Thread.Sleep(50);   // ~20 Hz broadcast cadence (real cadence, not busy-spin)

            BroadcastAttach(cam, hub, hullNames, sectorCache, ref modelCounter, seenKills, SimNow(), enemies, fight.hull.HullId);

            double now = SimNow();

            // P420 — drive the opposing contingent every tick: launch at their
            // OWN home base, transit real warps to rendezvous in the spectator's
            // cluster, then engage during a combat window (or loiter otherwise).
            // Guarded: navigation waits until the spectator's cluster (fleetSector)
            // is known, so the enemy simply loiters at home until then.
            bool inCombat = phase == AttachPhase.Combat;
            DriveEnemyContingent(cam, hub, enemies, core, weaponPartId, fight.hull, fleetSector, inCombat, now);
            int arrivedEnemies = enemies.Count(e => e.Arrived);

            switch (phase)
            {
                case AttachPhase.Connecting:
                    if (bots.All(x => x.Bot.ShipAssignment is not null))
                    {
                        // Real ready-up: PLAYER_READY for the spectator + every bot
                        // (the spectator's own side must be aggregate-ready for the
                        // auto-start). The spectator readies its own ship too.
                        short camId = cam.World.MyShipId!.Value;
                        cam.SendPlayerReady(fReady: true, shipId: camId);
                        foreach (var ab in bots)
                            ab.Bot.SendPlayerReady(fReady: true, shipId: ab.ShipId);
                        // P420 — the opposing contingent must ready its own side
                        // too, else the server's aggregate auto-start may never fire.
                        foreach (var en in enemies)
                            en.Cs.SendPlayerReady(fReady: true, shipId: en.ShipId);
                        phase = AttachPhase.ReadyUp;
                        phaseEnter = now;
                        Console.WriteLine($"[attach] ready-ups sent (spectator {camId} + {n} bots + {enemies.Count} enemy peers)");
                    }
                    break;

                case AttachPhase.ReadyUp:
                    // Wait for the real auto-start countdown → Stage Started.
                    bool started = false;
                    lock (cam.Gate) started = cam.World.Mission is { Stage: var s } && s >= MissionStageMessage.StageStarted;
                    if (started)
                    {
                        AttachEvent(hub, "Mission underway — real server auto-start reached Started.", now);
                        phase = AttachPhase.Launch;
                        phaseEnter = now;
                    }
                    else if (now - phaseEnter > 45)
                    {
                        AttachEvent(hub, "Auto-start stalled; continuing the demo in the lobby.", now);
                        phase = AttachPhase.Launch;
                        phaseEnter = now;
                    }
                    break;

                case AttachPhase.Launch:
                    // Real launch: BUY_LOADOUT(fLaunch:true). Every bot carries
                    // the full inventory (weapon + afterburner + cloak + shield,
                    // P420) so the whole fleet genuinely uses everything.
                    foreach (var ab in bots.Where(x => !x.Launched))
                    {
                        List<ExpandedPartData> parts = FullInventoryParts(core, weaponPartId);
                        try
                        {
                            ab.Bot.SendBuyLoadout(fight.hull.HullId, fLaunch: true, parts: parts);
                            ab.Launched = true;
                            Console.WriteLine($"[attach] {ab.Name} launching ({parts.Count} part(s))");
                        }
                        catch (Exception e) { Console.WriteLine($"[attach] {ab.Name} launch failed: {e.Message}"); }
                    }
                    if (bots.All(x => x.Launched))
                    {
                        AttachEvent(hub, "Fleet launches — bots undock into the starting cluster under real wire commands.", now);
                        phase = AttachPhase.Flight;
                        phaseEnter = now;
                    }
                    else if (now - phaseEnter > 20)
                    {
                        AttachEvent(hub, "Launch in progress; moving to flight.", now);
                        phase = AttachPhase.Flight;
                        phaseEnter = now;
                    }
                    break;

                case AttachPhase.Flight:
                    // P419 bounded flight: the continuous real-motion beat is now
                    // wrapped in a real leash. Establish the fleet anchor + leash
                    // ONCE from the real launch/world geometry, then drive each bot
                    // with the ambient weave INSIDE the radius and a proportional
                    // steer-back-to-anchor term PAST it (see AttachLeashSteer), so
                    // the fleet no longer "goes to infinity".
                    if (!leashReady)
                    {
                        (anchor, leashRadius, fleetSector) = ComputeFleetLeash(cam, bots);
                        leashReady = true;
                        AttachEvent(hub,
                            $"Bounded flight set — fleet anchor ({anchor.X:0},{anchor.Y:0},{anchor.Z:0}), leash radius {leashRadius:0}u (stays inside the starting-cluster view).", now);
                    }
                    for (int i = 0; i < bots.Count; i++)
                    {
                        var ab = bots[i];
                        try
                        {
                            if (!TryGetBotPose(cam, ab.ShipId, out var pos, out var orient)) continue;
                            double dir = ab.IsAttack ? 1.0 : -1.0;
                            double weaveYaw = dir * Math.Sin(now * ab.WeaveFreq + ab.WeavePhase) * ab.WeaveAmp;
                            double weaveThrottle = 0.55 + 0.25 * Math.Sin(now * ab.WeaveFreq * 0.7 + ab.WeavePhase * 1.3);
                            var (yaw, throttle) = AttachLeashSteer.Steer(
                                pos, anchor, leashRadius, orient, (float)weaveYaw, (float)weaveThrottle);
                            ab.Bot.CommandControls(new ControlData(yaw, 0f, 0f, throttle),
                                ab.IsAttack ? ShipControlState.AllWeapons : 0);
                        }
                        catch { }
                    }
                    // Narrate real world density every ~15s.
                    if (now - lastNavEvent > 15)
                    {
                        lastNavEvent = now;
                        int live = 0;
                        lock (cam.Gate) live = cam.World.Ships.Values.Count(e => !e.IsDead);
                        AttachEvent(hub, $"Flight — {live} real ship(s) now visible to the spectator's client on the wire.", now);
                    }
                    // A real ~30-45s flight window, then progress the sequence.
                    if (now - phaseEnter > 40)
                    {
                        AttachEvent(hub, "Flight window complete — regrouping the fleet toward the home anchor.", now);
                        phase = AttachPhase.Regroup;
                        phaseEnter = now;
                    }
                    break;

                case AttachPhase.Regroup:
                    // A visible formation change: every bot converges on the anchor
                    // (the same face-and-thrust law the sim's own autopilot uses),
                    // reversing the outward weave drift in an observable re-group.
                    {
                        bool allNear = true;
                        for (int i = 0; i < bots.Count; i++)
                        {
                            var ab = bots[i];
                            try
                            {
                                if (TryGetBotPose(cam, ab.ShipId, out var pos, out _) && (anchor - pos).Length() > 120f)
                                    allNear = false;
                                AttachFlyTo(cam, ab, anchor, 60f, 220f, ab.IsAttack ? ShipControlState.AllWeapons : 0);
                            }
                            catch { }
                        }
                        if (allNear || now - phaseEnter > 18)
                        {
                            AttachEvent(hub, "Regroup complete — fleet reconverged on the home position.", now);
                            // Re-arm the mining target discovery so each Mining
                            // entry re-resolves and re-narrates (not just cycle 1).
                            miningAssigned = false;
                            phase = AttachPhase.Mining;
                            phaseEnter = now;
                        }
                    }
                    break;

                case AttachPhase.Mining:
                    // A real mining pass: the first few bots traverse to the
                    // nearest REAL minable asteroid (DataAsteroidIGC position from
                    // the live world state); the escorts loiter on a bounded weave
                    // around the anchor. Wire-only movement toward real ore — no
                    // fabricated beat.
                    if (!miningAssigned)
                    {
                        miningAssigned = true;
                        if (TryFindMiningTarget(cam, fleetSector, anchor, out miningAsteroidId, out miningTarget))
                            AttachEvent(hub,
                                $"Mining pass — {miningBots} miner(s) traversing to real asteroid #{miningAsteroidId} at ({miningTarget.X:0},{miningTarget.Y:0},{miningTarget.Z:0}).", now);
                        else
                            AttachEvent(hub, "No minable asteroid in the fleet's view — holding the mining pass.", now);
                    }
                    for (int i = 0; i < bots.Count; i++)
                    {
                        var ab = bots[i];
                        try
                        {
                            if (miningAsteroidId >= 0 && i < miningBots)
                            {
                                AttachFlyTo(cam, ab, miningTarget, 40f, 200f, 0);
                            }
                            else
                            {
                                // Escorts: bounded loiter around the anchor.
                                if (!TryGetBotPose(cam, ab.ShipId, out var pos, out var orient)) continue;
                                double yaw = Math.Sin(now * ab.WeaveFreq + ab.WeavePhase) * ab.WeaveAmp;
                                var (y2, t2) = AttachLeashSteer.Steer(pos, anchor, leashRadius, orient, (float)yaw, 0.5f);
                                ab.Bot.CommandControls(new ControlData(y2, 0f, 0f, t2), 0);
                            }
                        }
                        catch { }
                    }
                    if (now - phaseEnter > 24)
                    {
                        AttachEvent(hub, "Mining pass complete — diverting the fleet to the home station to dock for relaunch.", now);
                        phase = AttachPhase.DockCycle;
                        phaseEnter = now;
                    }
                    break;

                case AttachPhase.DockCycle:
                    // Real dock-then-relaunch: fly the fleet into the home station
                    // so the server's own DockWithStationEvent latches each one
                    // (Sector.ResolveDockCollisions — any same-side ship within
                    // DockRadius 200 docks), which both views see as the ship
                    // leaving (SHIP_DELETE SDR_DOCKED), then relaunch through the
                    // exact same real SendBuyLoadout(fLaunch:true) path as Launch.
                    if (!homeStationKnown)
                    {
                        homeStationKnown = TryFindHomeStation(cam, fleetSector, anchor, out homeStationPos);
                        if (homeStationKnown)
                            AttachEvent(hub, $"Dock beat — home station located at ({homeStationPos.X:0},{homeStationPos.Y:0},{homeStationPos.Z:0}); diving to dock.", now);
                    }
                    for (int i = 0; i < bots.Count; i++)
                    {
                        var ab = bots[i];
                        try
                        {
                            AttachFlyTo(cam, ab, homeStationKnown ? homeStationPos : anchor, 12f, 120f, 0);
                        }
                        catch { }
                    }
                    int docked = 0;
                    lock (cam.Gate)
                        foreach (var ab in bots)
                            if (cam.World.Ships.TryGetValue(ab.ShipId, out var e) && e.IsGone) docked++;
                    if (docked >= Math.Max(1, bots.Count / 2) || now - phaseEnter > 34)
                    {
                        if (docked > 0)
                            AttachEvent(hub, $"{docked}/{bots.Count} ship(s) docked at the home station — relaunching the fleet.", now);
                        foreach (var ab in bots)
                        {
                            try
                            {
                                ab.Bot.SendBuyLoadout(fight.hull.HullId, fLaunch: true,
                                    parts: FullInventoryParts(core, weaponPartId));
                                Console.WriteLine($"[attach] {ab.Name} relaunching after dock");
                            }
                            catch { }
                        }
                        AttachEvent(hub, "Fleet relaunched — returning to bounded flight for the next cycle.", now);
                        phase = AttachPhase.Flight;
                        phaseEnter = now;
                    }
                    break;

                case AttachPhase.Held:
                    break;

                case AttachPhase.Combat:
                    // P420 — a real cross-side engagement: the opposing contingent
                    // has rendezvoused in the spectator's cluster, so friendlies
                    // and enemies are genuinely co-located and cross-side hits are
                    // real (no friendly-fire suppression). Attackers acquire the
                    // nearest enemy, fire AllWeapons with afterburner boost passes;
                    // defenders loiter on the leash with a periodic cloak beat; the
                    // enemies (DriveEnemyContingent -> EngageEnemy) do the same
                    // against the fleet. Real combat, honest results: whoever takes
                    // real damage takes it; kills happen only if the server's real
                    // combat+eject mechanics let them.
                    DriveFriendlyCombat(cam, bots, anchor, leashRadius, enemySide, now);
                    if (now - phaseEnter > 40)
                    {
                        AttachEvent(hub, "Combat window complete — disengaging; the fleet returns to bounded flight (opposing contingent loiters in the cluster).", now);
                        phase = AttachPhase.Flight;
                        phaseEnter = now;
                        leashReady = true;   // keep the established anchor/leash
                    }
                    break;
            }

            // P420 — begin a real cross-side combat window once the opposing
            // contingent has genuinely rendezvoused in the spectator's cluster
            // and the fleet is airborne (never mid-dock). Cooldown prevents a
            // back-to-back re-engage after a window ends.
            if (phase is AttachPhase.Flight or AttachPhase.Regroup or AttachPhase.Mining
                && arrivedEnemies > 0 && now - lastCombatAt > 75)
            {
                AttachEvent(hub, "Rendezvous complete — opposing contingent co-located with the fleet. Cross-side engagement window begins.", now);
                phase = AttachPhase.Combat;
                phaseEnter = now;
                lastCombatAt = now;
            }

            // Keep the show alive, guarded: only re-launch a bot whose ship was
            // genuinely seen on the wire and then vanished (eject / death), never
            // more than ~once per 12s per bot — so a ship that is merely
            // not-yet-visible can never hammer the server with loadouts. Excluded
            // during DockCycle, where vanishing is the intended dock latch (the
            // cycle relaunches explicitly) and an eager auto-relaunch would fight it.
            if (phase is AttachPhase.Flight or AttachPhase.Regroup or AttachPhase.Mining)
            {
                foreach (var ab in bots)
                {
                    if (ab.Bot.ShipAssignment is null) continue;
                    bool visible = false;
                    lock (cam.Gate)
                    {
                        if (cam.World.Ships.TryGetValue(ab.ShipId, out var e) && !e.IsNotVisible)
                            visible = true;
                    }
                    if (visible) { ab.EverSeen = true; continue; }
                    if (ab.EverSeen && now - ab.LastRelaunchAt > 12)
                    {
                        ab.LastRelaunchAt = now;
                        try
                        {
                            ab.Bot.SendBuyLoadout(fight.hull.HullId, fLaunch: true,
                                parts: FullInventoryParts(core, weaponPartId));
                            Console.WriteLine($"[attach] {ab.Name} re-launching (ship no longer visible)");
                        }
                        catch { }
                    }
                }
            }
        }

        Console.WriteLine($"[attach] ran until cancelled; spectator page served at ws://127.0.0.1:{webPort}/ws");
        appTask.GetAwaiter().GetResult();
        return 0;
    }
}
