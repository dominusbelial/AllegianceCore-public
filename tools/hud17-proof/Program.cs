// HUD-17 real-client proof. Drives the actual Game1 over TCP against a real
// local server and captures production backbuffer frames for the detection eye:
//   * the own-ship eye (radonbmp) shown when the RAW own-side ShipStatus.Detected
//     bit is true and absent when it is false (WinTrek/console.cpp:811,
//     Artwork/dialog.mdl:206-207,2526-2534);
//   * the radar friendly detection eye (eyebmp) on a same-side player-piloted
//     teammate contact, and absent when the bit is false
//     (WinTrek/radarimage.cpp:1006-1019,226,1333-1352).
// A real side-1 player ship (a passive bot peer) is the only enemy scanner:
// moving it onto (and away from) the pilot + side-0 bot teammate is a real
// server-side SeenBySide edge, and the server's HUD-17 shipDetection sweep
// re-sends the changed status.
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;
using FltBot = Allegiance.Bot.Bot;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? ".scratch/hud17-capture");
Directory.CreateDirectory(output);
string style = Arg("--style", "Classic");
string settingsPath = Path.Combine(output, "hud.json");
File.WriteAllText(settingsPath, $"{{\"Style\":\"{style}\",\"Size\":1,\"ColorBlindSafe\":false,\"RoundRadar\":false,\"DamageWash\":true,\"ChatLines\":7}}");
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settingsPath);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork),
    StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
    Games = [new GameConfig
    {
        Name = "HUD-17 detection proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800,
        MaxTeamImbalance = 0x7fff,   // seat the pilot + teammate on one side
    }]
});
var mission = server.Missions[0];
mission.Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var gate = new SimGate();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        if (!gate.Paused) server.TickSim();
        Thread.Sleep(5);
    }
});

// Two passive bot peers: a same-side PLAYER-piloted teammate (native
// GetPilotType() >= c_ptPlayer) and a side-1 player ship that is the enemy eye.
using var teammateBot = new FltBot();
using var enemyBot = new FltBot();
var teammateServerShip = SeatAndLaunch(teammateBot, "hud17_teammate", 99100000000000101ul);
short pilotSide = teammateServerShip.SideId ?? 0;
var enemyServerShip = SeatAndLaunch(enemyBot, "hud17_enemy", 99100000000000102ul);
if (enemyServerShip.SideId == pilotSide)
    throw new InvalidOperationException("the enemy bot must seat on the other side");
ticker.Start();   // only after the bot setup, so no two threads drive TickSim at once

Ship SeatAndLaunch(FltBot bot, string name, ulong steam)
{
    bot.Connect(server.BoundPort, name);
    bot.SendLogonRequest(Logon(name, steam));
    var seatWatch = Stopwatch.StartNew();
    while (bot.ShipAssignment is null && seatWatch.Elapsed.TotalSeconds < 20)
    {
        server.TickSim();
        Thread.Sleep(5);
    }
    if (bot.ShipAssignment is null) throw new TimeoutException(name + " never seated");
    short id = bot.ShipAssignment!.Ship.ShipId;
    var ship = mission.Sectors.SelectMany(s => s.Models).OfType<Ship>().First(s => s.Id == id);
    bot.SendBuyLoadout(ship.HullType.HullId, fLaunch: true);
    var launchWatch = Stopwatch.StartNew();
    while (ship.StationId is not null && launchWatch.Elapsed.TotalSeconds < 20)
    {
        server.TickSim();
        Thread.Sleep(5);
    }
    Console.WriteLine($"[hud17] bot ship {id} '{name}' side {ship.SideId} launched");
    return ship;
}

try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD17 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan
        {
            AutoSide = pilotSide, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "",
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud17Proof(game, mission, teammateServerShip, enemyServerShip, output, gate));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
}

static LogonRequest Logon(string name, ulong steam) => new()
{
    CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
    CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
    FedsrvVer = LogonRequest.MsgVer, Time = 955u, DwCookie = uint.MaxValue,
    CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
    SteamID = steam, DrmHash = "hud17proof",
};

sealed class SimGate { public volatile bool Paused; }

sealed class Hud17Proof(Game1 game, Mission mission, Ship teammateServerShip, Ship enemyServerShip,
    string output, SimGate gate) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly MethodInfo MoveShipMethod = typeof(Srv)
        .GetMethod("MoveShipToSector", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new MissingMethodException("Server.MoveShipToSector");
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own;
    private SimVec _basePos;
    private SimVec _baseFwd;
    private bool _enemyNear = true;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) =>
        (typeof(Game1).GetMethod(name, Any) ?? throw new MissingMethodException(name)).Invoke(game, args);
    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("HUD17_SCREEN " + name);
    }

    private ConnectionSession Session => Get<ConnectionSession>("_session");
    private ClientWorld World => Session.World;

    private Ship? OwnServerShip()
    {
        short? id;
        lock (Session.Gate) id = World.MyShipId;
        return id is { } shipId ? FindShip(shipId) : null;
    }

    private Ship? FindShip(short id) => teammateServerShip.Mission!.Sectors
        .SelectMany(s => s.Models).OfType<Ship>().FirstOrDefault(s => s.Id == id);

    private bool ClientDetected(short id)
    {
        lock (Session.Gate)
            return World.Ships.TryGetValue(id, out var e) && e.Detected;
    }

    private bool ClientHasPlayer(short id)
    {
        lock (Session.Gate)
            return World.Ships.TryGetValue(id, out var e) && e.Player is { } p
                && p.PilotType >= PlayerInfo.PilotTypePlayer;
    }

    private bool ClientHasRadarPose(short id)
    {
        lock (Session.Gate)
        {
            if (World.MyShipId is not { } mine
                || !World.Ships.TryGetValue(mine, out var own)
                || !World.Ships.TryGetValue(id, out var e)) return false;
            return e.Latest is not null && e.SectorId == own.SectorId && !e.IsNotVisible;
        }
    }

    private void Stage(bool enemyNear)
    {
        if (_own is null) return;
        // Pin the client ship too: the real client keeps flying otherwise, which
        // both defeats the staged geometry and can ram the staged contacts.
        _own.Position = _basePos;
        _own.Velocity = SimVec.Zero;
        _own.Controls = ControlData.Neutral;
        var fwd = _baseFwd;
        // Both bots were seated in their OWN home sectors; the scanner cluster
        // check is part of InScannerRange (stationIGC.h:311-313), so move them
        // into the pilot's sector for the staged edge.
        if (_own.Sector is { } sector
            && (!ReferenceEquals(teammateServerShip.Sector, sector)
                || !ReferenceEquals(enemyServerShip.Sector, sector)))
        {
            // Sector membership is a real list mutation; do it with the sim
            // thread parked so no collection is read while it changes.
            gate.Paused = true;
            Thread.Sleep(8);
            if (!ReferenceEquals(teammateServerShip.Sector, sector))
                MoveShipMethod.Invoke(null, [teammateServerShip, sector, false]);
            if (!ReferenceEquals(enemyServerShip.Sector, sector))
                MoveShipMethod.Invoke(null, [enemyServerShip, sector, false]);
            gate.Paused = false;
        }
        // The teammate sits inside radar-label range and inside the enemy ship's
        // scanner radius while the enemy is near.
        teammateServerShip.Position = _own.Position + fwd * 420f;
        teammateServerShip.Velocity = SimVec.Zero;
        teammateServerShip.Controls = ControlData.Neutral;
        // The enemy eye stays in the pilot's sector when near. When it must not
        // see the pair, move it OUT OF THE SECTOR (a real MoveShipToSector) rather
        // than flinging it across the map: this leaves the pilot/teammate radar
        // geometry untouched, so the label block stays put and only the eye bit
        // changes. (InScannerRange requires the same cluster, shipIGC.h:341-364.)
        var enemySector = enemyNear
            ? _own.Sector
            : mission.Sectors.FirstOrDefault(s => !ReferenceEquals(s, _own.Sector));
        if (enemySector is not null && !ReferenceEquals(enemyServerShip.Sector, enemySector))
        {
            gate.Paused = true;
            Thread.Sleep(8);
            MoveShipMethod.Invoke(null, [enemyServerShip, enemySector, false]);
            gate.Paused = false;
        }
        enemyServerShip.Position = enemyNear
            ? _own.Position + fwd * 180f
            : SimVec.Zero;
        enemyServerShip.Velocity = SimVec.Zero;
        enemyServerShip.Controls = ControlData.Neutral;
    }

    private void Log(string when)
    {
        short? ownId;
        lock (Session.Gate) ownId = World.MyShipId;
        bool ownDetected = ownId is { } oid && ClientDetected(oid);
        bool mateDetected = ClientDetected(teammateServerShip.Id);
        bool matePlayer = ClientHasPlayer(teammateServerShip.Id);
        bool seen = _own?.SeenBySide(enemyServerShip.SideId ?? -1) == true;
        lock (Session.Gate)
            Console.WriteLine($"[hud17] {when}: own={ownId} ownDetected={ownDetected} " +
                $"teammate={teammateServerShip.Id} teammateDetected={mateDetected} teammatePlayerInfo={matePlayer} " +
                $"serverSeenByEnemy={seen}");
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("HUD-17 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        Stage(_enemyNear);
        Set("_throttle", 0f);
        Set("_cameraMode", CameraMode.Cockpit);
        if (!flying || _watch.Elapsed.TotalSeconds < _next) return;
        Console.WriteLine($"[hud17] t={_watch.Elapsed.TotalSeconds:0.00} step {_step}");
        switch (_step++)
        {
            case 0:
                _own = OwnServerShip();
                if (_own is null) { _step = 0; return; }
                _basePos = _own.Position;
                _baseFwd = _own.Orientation.GetForward();
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                return;
            case 1:
                Call("DismissBriefing");
                _enemyNear = true;    // the enemy eye starts on the pair
                _next = _watch.Elapsed.TotalSeconds + 6.0;   // settle pose + the status edge
                return;
            case 2:
                Log("eyed wait");
                if (ClientDetected(_own!.Id) && ClientDetected(teammateServerShip.Id)
                    && ClientHasPlayer(teammateServerShip.Id)
                    && ClientHasRadarPose(teammateServerShip.Id))
                {
                    Log("eyed");
                    Shot("own-eyed-1920x1080");
                    _next = _watch.Elapsed.TotalSeconds + 0.8;
                    return;
                }
                _step = 2;
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                return;
            case 3:
                Log("radar eyed");
                Shot("radar-teammate-eye-1920x1080");
                // Move the only enemy scanner far away. The pilot and teammate do
                // NOT move, so the radar label block stays in the same place; only
                // the eye state changes.
                _enemyNear = false;
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                return;
            case 4:
                Log("not-eyed wait");
                if (!ClientDetected(_own!.Id) && !ClientDetected(teammateServerShip.Id))
                {
                    // Radar frame first, while the pilot/teammate radar geometry is
                    // still the eyed pair's; own-HUD frame second.
                    Log("radar not eyed");
                    Shot("radar-teammate-no-eye-1920x1080");
                    _next = _watch.Elapsed.TotalSeconds + 0.6;
                    return;
                }
                _step = 4;
                _next = _watch.Elapsed.TotalSeconds + 0.2;
                return;
            case 5:
                Log("not eyed");
                Shot("own-not-eyed-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 1.2;
                return;
            default:
                game.Exit();
                return;
        }
    }
}
