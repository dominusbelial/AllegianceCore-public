using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

// P774b — real-client UI evidence for the in-mission roster display fix: the
// LEADERBOARD (B) and CHARACTER SHEET (P) overlays at 1920×1080, driven through
// the real Game1 against the real Allegiance.Server. Nothing is seeded into the
// renderers: the roster rows come from PLAYERINFO(10) + KILL_SHIP(105) exactly
// as in flight (P774), and the two staged kills are real server-side
// ReceiveDamage deaths (the ScoreRosterLiveTests fixture).
//
//   dotnet run --project tools/p774b-ui-proof -- <outputDir> <shotTag>
//
// Writes <outputDir>/<tag>_scoreboard_1080p.png and <tag>_pilotinfo_1080p.png.
string output = Path.GetFullPath(args.Length > 0 ? args[0] : ".scratch/p774b-shots");
string tag = args.Length > 1 ? args[1] : "p774b";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent) && !File.Exists(Path.Combine(localContent, "HUD.xnb")))
    Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "P774b UI proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
});
server.Missions[0].Stage = MissionStage.Started;

// Three real peer sessions so the client's roster has several rows.
static ConnectionSession Seat(Srv srv, string name)
{
    var session = new ConnectionSession();
    session.Connect(srv.BoundPort, name);
    session.SendLogonRequest(new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, CrcFileList = -1 });
    var deadline = DateTime.UtcNow.AddSeconds(30);
    while (session.Ack is null && DateTime.UtcNow < deadline) { srv.TickSim(); Thread.Sleep(2); }
    session.SendPositionReq(-1);
    deadline = DateTime.UtcNow.AddSeconds(30);
    bool seated = false;
    while (!seated && DateTime.UtcNow < deadline)
    {
        lock (session.Gate) seated = session.World.MyShipId is not null;
        srv.TickSim(); Thread.Sleep(2);
    }
    return session;
}

var a = Seat(server, "Ace");
var b = Seat(server, "Bee");
var c = Seat(server, "Cee");

// Park every mission ship in one sector so every peer sees every enemy (the
// HUD-20 cross-side EXPORT + PLAYERINFO seed fires for each (peer, ship) pair).
// server.Ships is internal (InternalsVisibleTo covers only the test projects),
// so the LiveShip records are reached by reflection — a proof-harness seam.
var liveShips = ((System.Collections.IEnumerable)typeof(Srv)
    .GetProperty("Ships", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!)
    .Cast<object>().ToList();
static Ship ShipOf(object liveShip) =>
    (Ship)liveShip.GetType().GetProperty("Ship")!.GetValue(liveShip)!;
var mission = server.Missions[0];
var hub = liveShips.Select(ShipOf).First(sh => sh.Mission == mission && sh.SideId is not null).Sector!;
foreach (var ls in liveShips)
{
    var sh = ShipOf(ls);
    if (sh.Mission == mission && !ReferenceEquals(sh.Sector, hub))
        mission.PlaceShip(sh, hub);
}

// One real kill: Ace destroys Bee (a real death — the victim is moved off its
// lifepod-backed side so lethal hull damage is a death, not an eject; the
// ScoreRosterLiveTests fixture). The server counts Kills/Deaths and KILL_SHIP
// relays them; a joiner's PLAYERINFO seed carries the counters too.
static Ship ShipForSession(Srv srv, ConnectionSession s)
{
    short? myShipId;
    lock (s.Gate) myShipId = s.World.MyShipId;
    var liveShips2 = ((System.Collections.IEnumerable)typeof(Srv)
        .GetProperty("Ships", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(srv)!)
        .Cast<object>().ToList();
    return liveShips2.Select(ls => (Ship)ls.GetType().GetProperty("Ship")!.GetValue(ls)!)
        .First(sh => sh.Id == myShipId);
}
static float PlayerScoreOf(Srv srv, Ship sh, string field)
{
    var live = ((System.Collections.IEnumerable)typeof(Srv)
        .GetProperty("Ships", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(srv)!)
        .Cast<object>().First(ls => ReferenceEquals(ls.GetType().GetProperty("Ship")!.GetValue(ls), sh));
    var ps = live.GetType().GetProperty("PlayerScore")!.GetValue(live)!;
    return Convert.ToSingle(ps.GetType().GetProperty(field)!.GetValue(ps)!);
}
var attacker = ShipForSession(server, a);
var victim = ShipForSession(server, b);
short victimRealSide = victim.SideId!.Value;
victim.SideId = 774;
victim.ReceiveDamage(0, 1e9f, server.SimNow, victim.Position, new Allegiance.Core.Collision.Vector3(0, 0, 0), attacker);
victim.SideId = victimRealSide;
Console.WriteLine($"P774B_PROOF staged kill: attackerScore.Kills={PlayerScoreOf(server, attacker, "Kills")} victimScore.Deaths={PlayerScoreOf(server, victim, "Deaths")}");

using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P774b pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new Allegiance.ClientCore.MonoGame.LiveProof.AutoProofPlan
        {
            AutoSide = 1, AutoReady = true, AutoLaunchRequested = true, LaunchHull = ""
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new P774bGameProof(game, output, tag));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

Console.WriteLine("P774B_PROOF done");

internal static class Refl
{
    public const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    public static T Get<T>(object o, string name) => (T)typeof(Game1).GetField(name, H)!.GetValue(o)!;
    public static void Set(object o, string name, object? v) => typeof(Game1).GetField(name, H)!.SetValue(o, v);
    public static object? Call(object o, string name, params object?[] args) =>
        typeof(Game1).GetMethod(name, H | BindingFlags.Public)!.Invoke(o, args);
}

/// <summary>Waits for the Flying state, then photographs the two roster
/// overlays through the real backbuffer autoshot path (the P761 idiom).</summary>
sealed class P774bGameProof(Game1 game, string output, string tag) : GameComponent(game)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _finishAfterFlush;

    private void Shot(string name)
    {
        Refl.Set(game, "_autoshotDir", output);
        Refl.Set(game, "_autoShotName", name);
        Refl.Set(game, "_autoShotScheduled", true);
        Console.WriteLine("P774B_SCREEN " + name);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 240) throw new TimeoutException("P774b game proof did not finish");
        if (_finishAfterFlush)
        {
            if (!(bool)typeof(Game1).GetField("_autoShotScheduled", Refl.H)!.GetValue(game)!)
                game.Exit();
            return;
        }

        string state = Refl.Get<object>(game, "_state").ToString() ?? "?";
        if (_watch.Elapsed.TotalSeconds < _next) return;

        switch (_step++)
        {
            case 0:
            {
                if (state != "Flying") { _step--; return; }   // waits for the launch
                var session = Refl.Get<ConnectionSession>(game, "_session");
                lock (session.Gate)
                {
                    bool docked = session.World.MyShipId is { } me
                        && session.World.Ships.TryGetValue(me, out var own) && own.StationId != -1;
                    if (docked)
                    {
                        // The autoproof seat leaves us in the hangar — launch
                        // through the REAL docked-screen handler (ShopLaunch/L).
                        Refl.Call(game, "UpdateFlying", new KeyboardState(Keys.L), gameTime);
                        _step--;   // retry next tick until actually in space
                        return;
                    }
                }
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            }
            case 1:
                // The scoreboard (B) — forced open through the same visible
                // flag the real B toggle flips.
                Refl.Set(game, "_scoreboardVisible", true);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 2:
                Shot(tag + "_scoreboard_1080p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 3:
                // Swap to the character sheet (P).
                Refl.Set(game, "_scoreboardVisible", false);
                Refl.Set(game, "_pilotInfoVisible", true);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 4:
                Shot(tag + "_pilotinfo_1080p");
                _finishAfterFlush = true;
                break;
            default:
                _step--;
                return;
        }
    }
}
