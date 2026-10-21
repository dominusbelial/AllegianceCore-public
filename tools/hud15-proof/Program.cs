// HUD-15 real-client proof. Drives the actual Game1 over TCP against a real
// local server and captures production backbuffer frames for the native
// command-icon artwork:
//   * the console order lines with comacceptbmp / comqueuedbmp
//     (Artwork/dialog.mdl:1605-1652, WinTrek/console.cpp:875-908);
//   * a radar contact carrying an accepted (and one a queued) order, drawn with
//     the native m_psurfaceAccepted/m_psurfaceQueued command surfaces
//     (WinTrek/radarimage.cpp:228-235,1248-1253);
//   * an inventory row mid-mount with the real mountingbarbkgndbmp track
//     (Artwork/invdefault.mdl:43-52, WinTrek/consoledata.cpp:1157).
// The orders travel the real paths: the server's QueueCommandForPlayer writes
// the queued slot, the client's bound ACCEPT key edge (Home) sends ORDER_CHANGE,
// and the server promotes + broadcasts the accepted slot.
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
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? ".scratch/hud15-capture");
Directory.CreateDirectory(output);
string style = Arg("--style", "Classic");
// The HUD style is read by a field initializer when Game1 is constructed, so the
// settings file must exist first (same pattern as tools/hud11-proof).
string settingsPath = Path.Combine(output, "hud.json");
File.WriteAllText(settingsPath, $"{{\"Style\":\"{style}\",\"Size\":1,\"ColorBlindSafe\":false,\"RoundRadar\":false,\"DamageWash\":true,\"ChatLines\":7}}");
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settingsPath);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "HUD-15 proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
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
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD15 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan
        {
            AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "",
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud15Proof(game, server, mission, output, gate));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}

/// <summary>Lets the inventory shot hold the sim still so the half-mounted
/// afterburner cannot finish mounting (UpdateAfterburner ramps it to 1 in well
/// under a second and broadcasts the completion) before the capture.</summary>
sealed class SimGate { public volatile bool Paused; }

sealed class Hud15Proof(Game1 game, Srv server, Mission mission, string output, SimGate gate) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own;
    private readonly List<Ship> _friends = [];
    private readonly List<Ship> _enemies = [];

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) =>
        (typeof(Game1).GetMethod(name, Any) ?? throw new MissingMethodException(name)).Invoke(game, args);
    private object? ServerCall(string name, params object?[] args) =>
        (typeof(Srv).GetMethod(name, Any) ?? throw new MissingMethodException("Server." + name)).Invoke(server, args!);
    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("HUD15_SCREEN " + name);
    }
    private Ship? FindShip(short id) => mission.Sectors.SelectMany(s => s.Models.OfType<Ship>())
        .FirstOrDefault(s => s.Id == id);

    private Ship? OwnServerShip()
    {
        var client = Get<ConnectionSession>("_session");
        short? id;
        lock (client.Gate) id = client.World.MyShipId;
        return id is { } shipId ? FindShip(shipId) : null;
    }

    private void SpawnContacts()
    {
        _own = OwnServerShip();
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        short enemySide = mission.Sides.FirstOrDefault(s => s.Id != mySide)?.Id ?? mySide;
        var spawnMethod = typeof(Srv).GetMethod("SpawnNpcShip", Hidden)
            ?? throw new MissingMethodException("Server.SpawnNpcShip");
        Ship spawn(Mission m, Sector s, short side, SimVec p) =>
            (Ship)spawnMethod.Invoke(server, [m, s, side, p, null])!;
        // Spread the contacts across the command camera's three-quarter view so
        // every radar label block lands in its own screen quadrant: two forward
        // friends (one takes the accepted order) and two behind enemies (the
        // first takes the queued attack order).
        // World-axis placement: the command camera is a fixed three-quarter
        // world view, so world axes spread the labels across the frame.
        SimVec AtWorld(float x, float z) => _own.Position + new SimVec(x, 0f, z);
        _friends.Add(spawn(mission, sector, mySide, AtWorld(1400f, 700f)));
        _friends.Add(spawn(mission, sector, mySide, AtWorld(-1400f, -700f)));
        _enemies.Add(spawn(mission, sector, enemySide, AtWorld(700f, -700f)));
        _enemies.Add(spawn(mission, sector, enemySide, AtWorld(-700f, 700f)));
        // The launched scout coasts at ~160 m/s with no drag; over the proof's
        // (fast) sim clock it would leave the staged contacts kilometres behind.
        // Hold it still so the radar frame keeps the same geometry.
        _own.Velocity = SimVec.Zero;
        _own.Controls = ControlData.Neutral;
        foreach (var friend in _friends)
            Console.WriteLine($"[hud15] friend {friend.Id} hull '{friend.HullType.Name}'");
        Console.WriteLine($"[hud15] spawned {_friends.Count} friends + {_enemies.Count} enemies around ship {_own.Id} side {mySide}");
    }

    private ClientWorld World => Get<ConnectionSession>("_session").World;
    private ConnectionSession Session => Get<ConnectionSession>("_session");

    private void LogOrders(string when)
    {
        lock (Session.Gate)
        {
            string accepted = World.MyAcceptedCommand is { } a
                ? $"{a.CommandId} {a.TargetType}/{a.TargetId}" : "none";
            string queued = World.MyQueuedCommand is { } q
                ? $"{q.CommandId} {q.TargetType}/{q.TargetId}" : "none";
            Console.WriteLine($"[hud15] {when}: accepted={accepted} queued={queued}");
        }
    }

    private void LogLoadout(string when)
    {
        lock (Session.Gate)
        {
            if (World.MyShipId is not { } me || !World.Ships.TryGetValue(me, out var own)) return;
            if (own.Loadout is not { } parts) { Console.WriteLine($"[hud15] {when}: no client loadout"); return; }
            foreach (var p in parts)
                Console.WriteLine($"[hud15] {when}: part {p.PartId} mount {p.MountId} fraction {p.FractionMounted}/{ExpandedPartData.FullyMounted} " +
                    $"({p.FractionMounted / (float)ExpandedPartData.FullyMounted:0.00})");
        }
    }

    /// <summary>HUD-15 — the auto-launched hull carries no parts, so the pane
    /// would have no rows. Mount one ready weapon plus one half-mounted
    /// afterburner through the production mount API (RaiseLoadoutChange →
    /// LOADOUT_CHANGE(96)). Afterburners are the one subtype
    /// AdvanceFlightMounts never ramps (shipIGC afterburner skip), so the
    /// mounting row stays mid-progress for the capture instead of racing to
    /// ready.</summary>
    private void StageInventory()
    {
        if (_own is null) return;
        var types = mission.Core.PartTypes.Values
            .Where(p => _own.HullType.CanMountPart(p, 0))
            .ToList();
        var weapons = types.Where(p => p.EquipmentType == EquipmentType.Weapon).ToList();
        var afterburners = types.Where(p => p.EquipmentType == EquipmentType.Afterburner).ToList();
        if (weapons.Count > 0)
        {
            var mounted = _own.MountPart(weapons[0], 1, 0, ExpandedPartData.FullyMounted);
            Console.WriteLine($"[hud15] mounted ready '{weapons[0].Name}' fraction={mounted?.FractionMounted}");
        }
        if (afterburners.Count > 0)
        {
            var mounting = _own.MountPart(afterburners[0], 1, 0, 120);
            Console.WriteLine($"[hud15] mounted '{afterburners[0].Name}' mid-mount fraction={mounting?.FractionMounted} " +
                $"mountedFraction={mounting?.MountedFraction:0.00}");
        }
        else Console.WriteLine("[hud15] core has no afterburner this hull can mount");
    }

    private void LogContacts(string when)
    {
        var contacts = Get<List<HudContact>>("_drawRadarContacts");
        foreach (var c in contacts)
            Console.WriteLine($"[hud15] {when} contact '{c.Name}' at ({c.Position.X:0},{c.Position.Y:0}) " +
                $"range={(c.Range is { } r ? r.ToString() : "-")} command={(c.CommandId is { } id ? (c.CommandAccepted ? "accepted " : "queued ") + id : "none")}");
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("HUD-15 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        // Neutral throttle + zeroed server velocity keeps the launched scout from
        // coasting away from the staged contacts (the auto-launch sets full
        // throttle, and the sim clock runs faster than wall time here).
        if (_own is not null) { _own.Velocity = SimVec.Zero; _own.Controls = ControlData.Neutral; }
        Set("_throttle", 0f);
        if (!flying || _watch.Elapsed.TotalSeconds < _next) return;
        Console.WriteLine($"[hud15] t={_watch.Elapsed.TotalSeconds:0.00} step {_step}");
        switch (_step++)
        {
            case 0:
                SpawnContacts();
                _next = _watch.Elapsed.TotalSeconds + 2.0;
                return;
            case 1:
                Call("DismissBriefing");
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                return;
            case 2:
                if (_own is null || _friends.Count < 2 || _enemies.Count < 2) { _step = 0; return; }
                // The real server writer for a queued order on a real player's
                // ship (Server.ShipCommands.cs QueueCommandForPlayer →
                // QUEUED_COMMAND_UPDATE(210)).
                ServerCall("QueueCommandForPlayer", _own, OrderChange.CidGoto, _friends[0], (IgcObjectType)(-1), (short)-1, (short)-1);
                _next = _watch.Elapsed.TotalSeconds + 0.7;
                return;
            case 3:
                LogOrders("queued goto");
                // Release then press the bound ACCEPT key so the production
                // action-edge path (CommandActionInput → SendOrderChange) fires.
                Call("HandleCommandActionKeys", new KeyboardState(), false);
                _next = _watch.Elapsed.TotalSeconds + 0.1;
                return;
            case 4:
                Call("HandleCommandActionKeys", new KeyboardState(Keys.Home), false);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            case 5:
                LogOrders("after accept");
                // A second order stays queued (real server writer, enemy target).
                ServerCall("QueueCommandForPlayer", _own, OrderChange.CidAttack, _enemies[0], (IgcObjectType)(-1), (short)-1, (short)-1);
                _next = _watch.Elapsed.TotalSeconds + 0.7;
                return;
            case 6:
                // The native queued slot auto-rejects after 15 s (WinTrek.cpp:7686);
                // the proof's sim clock runs faster than wall time, so refresh the
                // order right before each capture instead of racing the timer.
                LogOrders("accepted + queued");
                Set("_cameraMode", CameraMode.Cockpit);
                ServerCall("QueueCommandForPlayer", _own, OrderChange.CidAttack, _enemies[0], (IgcObjectType)(-1), (short)-1, (short)-1);
                _next = _watch.Elapsed.TotalSeconds + 0.2;
                return;
            case 7:
                LogOrders("orders shot");
                Shot("orders-accepted-queued-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                return;
            case 8:
                // Frame the accepted-order contact: move it (and the queued
                // enemy) ahead of the cockpit camera so their native command
                // icons and labels sit mid-screen, and refresh the native 15 s
                // queued slot right before the capture.
                if (_own is not null)
                {
                    SimVec forward = _own.Orientation.GetForward(), right = _own.Orientation.GetRight();
                    _friends[0].Position = _own.Position + forward * 900f + right * 300f;
                    _enemies[0].Position = _own.Position + forward * 1200f - right * 400f;
                    _own.Velocity = SimVec.Zero;
                }
                Set("_cameraMode", CameraMode.Cockpit);
                ServerCall("QueueCommandForPlayer", _own, OrderChange.CidAttack, _enemies[0], (IgcObjectType)(-1), (short)-1, (short)-1);
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                return;
            case 9:
                LogContacts("radar frame");
                Console.WriteLine("[hud15] radar frame with the accepted/queued native command icons");
                Shot("radar-accepted-command-icon-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                return;
            case 10:
                // F4 inventory in flight, with a real half-mounted part. Freeze
                // the sim first: the afterburner mount ramp would otherwise
                // finish (and broadcast) before the capture.
                gate.Paused = true;
                Call("ApplyConsoleMode", "ConModeInventory", true);
                StageInventory();
                _next = _watch.Elapsed.TotalSeconds + 0.35;
                return;
            case 11:
                LogLoadout("mid-mount");
                Shot("inventory-mounting-track-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            default:
                game.Exit();
                return;
        }
    }
}
