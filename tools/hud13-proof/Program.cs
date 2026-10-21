// HUD-13 real-client proof. Drives the actual Game1 over TCP against a real
// local server and captures production backbuffer frames that show radar
// overlay brackets sized from each contact's projected screen radius
// (radarimage.cpp:307-319): a NEAR enemy (large bracket, native enemybmp via
// the nearest-enemy mask), a MID selected enemy (targetedbmp) and a FAR enemy
// carrying a flag (small hasflagbmp bracket). Run once per HUD skin:
//   dotnet run --project tools/hud13-proof -- --style Classic --out <dir>
//   dotnet run --project tools/hud13-proof -- --style Modern  --out <dir>
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

string output = Path.GetFullPath(Arg("--out", ".scratch/hud13-capture"));
Directory.CreateDirectory(output);
string style = Arg("--style", "Classic");
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
// HUD settings are read by a field initializer when Game1 is constructed, so the
// environment must be set before `new Game1`.
string settingsPath = Path.Combine(output, "hud.json");
File.WriteAllText(settingsPath, $"{{\"Style\":\"{style}\",\"Size\":1,\"ColorBlindSafe\":false,\"RoundRadar\":false,\"DamageWash\":true,\"ChatLines\":7}}");
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settingsPath);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "HUD-13 proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
});
var mission = server.Missions[0];
mission.Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD13 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan
        {
            AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "",
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud13Proof(game, server, mission, output, style));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}

sealed class Hud13Proof(Game1 game, Srv server, Mission mission, string output, string style) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own;
    private Ship? _nearEnemy, _midEnemy, _farEnemy;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args)
    {
        var method = typeof(Game1).GetMethod(name, Hidden)
            ?? throw new MissingMethodException(name);
        return method.Invoke(game, args);
    }
    private void ServerCall(string name, params object?[] args)
    {
        var method = typeof(Srv).GetMethod(name, Hidden)
            ?? throw new MissingMethodException("Server." + name);
        method.Invoke(server, args);
    }

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("HUD13_SCREEN " + name);
    }

    private Ship? OwnServerShip()
    {
        var client = Get<ConnectionSession>("_session");
        short? id;
        lock (client.Gate) id = client.World.MyShipId;
        return id is { } shipId
            ? mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).FirstOrDefault(s => s.Id == shipId)
            : null;
    }

    private static SimVec Norm(SimVec v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len <= 1e-6f ? new SimVec(0, 0, -1) : new SimVec(v.X / len, v.Y / len, v.Z / len);
    }

    private static SimVec Cross(SimVec a, SimVec b)
        => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);

    // Fixed camera-relative placement recipe: (forward, lateral, up) in metres.
    private static readonly (float Forward, float Lateral, float Up)[] Slots =
    [
        (170f, -110f, 45f),      // near: large projected radius
        (900f, 210f, 90f),       // mid: medium (the selected target)
        (2100f, -380f, 210f),    // far: close to the native minimum radius
    ];
    private bool _placeRelative;

    private void SpawnContacts()
    {
        _own = OwnServerShip();
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        short enemySide = mission.Sides.FirstOrDefault(s => s.Id != mySide)?.Id ?? mySide;
        var spawnMethod = typeof(Srv).GetMethod("SpawnNpcShip", Hidden)
            ?? throw new MissingMethodException("Server.SpawnNpcShip");
        Ship spawn(SimVec p) => (Ship)spawnMethod.Invoke(server, [mission, sector, enemySide, p, null])!;

        PlaceEnemies(out var first);
        _nearEnemy = spawn(first[0]);
        _midEnemy = spawn(first[1]);
        _farEnemy = spawn(first[2]);
        _placeRelative = true;
        foreach (var sh in new[] { _nearEnemy, _midEnemy, _farEnemy })
            Console.WriteLine($"[hud13] spawned enemy {sh.Id} hull '{sh.HullType.Name}' radius={sh.Radius} side={sh.SideId} pos={sh.Position.X:0},{sh.Position.Y:0},{sh.Position.Z:0}");
    }

    /// <summary>Re-place the three enemies relative to the CLIENT's own-ship sim
    /// pose — the pose the cockpit camera and the radar projection both read
    /// (P508/P527), not the raw server pose. Called again every frame until the
    /// capture: the un-flown client ship keeps drifting/turning slightly, and
    /// fixed world positions would slide out of the view between the spawn and
    /// the screenshot.</summary>
    private void PlaceEnemies(out SimVec[] positions)
    {
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        SimVec ownPos = sim.HaveHull && _own is not null ? sim.Position : _own?.Position ?? default;
        var orientation = sim.HaveHull ? sim.Orientation : _own?.Orientation ?? Orientation.Identity;
        SimVec forward = Norm(orientation.GetForward());
        // The cockpit camera's own basis: screen right = the ship's right axis,
        // screen up = the ship's up axis (no roll here, so this is exact). Using
        // cross products instead maps "up" onto the world axis the ship is least
        // aligned with, which is NOT the camera's up.
        SimVec right = Norm(orientation.GetRight());
        SimVec up = Norm(orientation.GetUp());
        SimVec P((float Forward, float Lateral, float Up) slot)
            => new(ownPos.X + forward.X * slot.Forward + right.X * slot.Lateral + up.X * slot.Up,
                   ownPos.Y + forward.Y * slot.Forward + right.Y * slot.Lateral + up.Y * slot.Up,
                   ownPos.Z + forward.Z * slot.Forward + right.Z * slot.Lateral + up.Z * slot.Up);
        positions = [P(Slots[0]), P(Slots[1]), P(Slots[2])];
    }

    private readonly float[] _zeroTurnRates = new float[3];

    /// <summary>Hold the own ship at a fixed pose every frame (the un-flown client
    /// ship otherwise drifts and slowly turns, so contacts placed once slide out
    /// of the view), then re-place the three enemies relative to that pose. The
    /// server ship is pinned to the same pose so a SHIP_RESET can never snap the
    /// camera away.</summary>
    private void KeepEnemiesPlaced()
    {
        if (!_placeRelative || _own is null || _nearEnemy is null || _midEnemy is null || _farEnemy is null) return;
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        if (!sim.HaveHull) return;
        // Identity = nose along -Z (the same convention the launch pose uses).
        sim.Seed(sim.Position, default, Orientation.Identity, _zeroTurnRates);
        _own.Position = sim.Position;
        _own.Velocity = default;
        _own.SetOrientation(Orientation.Identity);
        PlaceEnemies(out var p);
        _nearEnemy.Position = p[0];
        _midEnemy.Position = p[1];
        _farEnemy.Position = p[2];
    }

    private void SelectTarget(Ship target)
    {
        // The player's own T/G selection field (_autoTarget, Game1.Targeting.cs):
        // set through the real private record so the radar's c_maskTarget contact
        // is exactly the one the production selection path would flag.
        var kindType = typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("Game1.TargetKind");
        var targetType = typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)
            ?? throw new MissingMemberException("Game1.AutopilotTarget");
        object kind = Enum.Parse(kindType, "Ship");
        object value = Activator.CreateInstance(targetType,
            kind, target.Id, $"Ship {target.Id}", (short)-1, true)!;
        Set("_autoTarget", value);
        Console.WriteLine($"[hud13] selected target {target.Id}");
    }

    /// <summary>Hold the own ship still at the pose the client renders: pin the
    /// server ship to the client's local-sim pose (server authority sanity checks
    /// then see no divergence, so no SHIP_RESET snaps the camera) and freeze the
    /// local sim so the camera does not keep flying during the capture.</summary>
    private void PinOwnShip()
    {
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        if (!sim.HaveHull) return;
        if (_own is not null)
        {
            _own.Position = sim.Position;
            _own.Velocity = default;
        }
        sim.Freeze();
        Console.WriteLine($"[hud13] pinned own ship at {sim.Position.X:0},{sim.Position.Y:0},{sim.Position.Z:0}");
    }

    private void DumpContactPoses()
    {
        var session = Get<ConnectionSession>("_session");
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        lock (session.Gate)
        {
            var w = session.World;
            Console.WriteLine($"[hud13] client ownSim={sim.Position.X:0},{sim.Position.Y:0},{sim.Position.Z:0} myShip={w.MyShipId}");
            var cam = Get<CockpitCamera>("_cockpitCamera");
            var mode = Get<object>("_cameraMode").ToString();
            var (view, projection) = cam.Matrices(
                PoseConverter.ToXna(sim.Position), PoseConverter.ToXnaRotation(sim.Orientation),
                1920f / 1080f, out var eye);
            Console.WriteLine($"[hud13] cameraMode={mode} eye={eye.X:0},{eye.Y:0},{eye.Z:0} "
                + $"fwd={sim.Orientation.GetForward().X:0.00},{sim.Orientation.GetForward().Y:0.00},{sim.Orientation.GetForward().Z:0.00}");
            foreach (var s in new[] { _nearEnemy, _midEnemy, _farEnemy })
            {
                if (s is null) continue;
                string client = w.Ships.TryGetValue(s.Id, out var cs) && cs.Latest is { } up
                    ? $"client={up.Position.X:0},{up.Position.Y:0},{up.Position.Z:0} sector={cs.SectorId} vis={!cs.IsNotVisible} unk={cs.Unknown} side={cs.SideId}"
                    : "client=absent";
                string predicted = w.Ships.TryGetValue(s.Id, out var ps) && ps.Latest is { } pup
                    ? RadarProjection.Project(PoseConverter.ToXna(pup.Position), eye, view, projection, 1920f, 1080f, 14f).Point.ToString()
                    : "-";
                Console.WriteLine($"[hud13] ship {s.Id} server={s.Position.X:0},{s.Position.Y:0},{s.Position.Z:0} {client} projected={predicted}");
            }
        }
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 140) throw new TimeoutException("HUD-13 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying) return;
        KeepEnemiesPlaced();
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0:
                // The launched ship keeps flying (no input), so hold the pose the
                // cockpit camera + radar project from before placing contacts
                // relative to it; otherwise they drift out of view between the
                // spawn and the capture. Wait until the local sim has a hull.
                Call("DismissBriefing");
                if (!Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim").HaveHull)
                { _step = 0; return; }
                PinOwnShip();
                _next = _watch.Elapsed.TotalSeconds + 0.4;
                return;
            case 1:
                SpawnContacts();
                _next = _watch.Elapsed.TotalSeconds + 1.5;   // let the cross-side exports settle
                return;
            case 2:
                PinOwnShip();                                 // re-pin: no drift for the capture
                if (_own is null || _nearEnemy is null || _midEnemy is null || _farEnemy is null)
                { _step = 1; return; }
                // Real GAIN_FLAG broadcasts give the NEAR and FAR enemies the same
                // carried-flag mask (hasflagbmp bracket art), so the screenshot
                // compares the SAME native surface at two projected radii.
                ServerCall("BroadcastGainFlag", mission, (short)0, _nearEnemy.Id, false, (short)0);
                ServerCall("BroadcastGainFlag", mission, (short)0, _farEnemy.Id, false, (short)0);
                // The MID enemy is the player's selection ⇒ native targetedbmp.
                SelectTarget(_midEnemy);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                return;
            case 3:
                DumpContactPoses();
                Shot($"radar-bracket-radius-{style.ToLowerInvariant()}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                return;
            default:
                game.Exit();
                return;
        }
    }
}
