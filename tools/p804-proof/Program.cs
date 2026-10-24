// P804 real-client proof. Drives the actual Game1 over TCP against a real local
// server and captures production 1920x1080 backbuffer frames for the client-side
// lead/aim solve this task implements:
//   (a) cockpit: the ballistic lead marker at the CLIENT-solved lead point plus
//       the centre-crosshair hit-probability ramp (indicator.cpp:283-362);
//   (b) chase:   the forward-projected aim point (indicator.cpp:246-276);
//   (c) chase with NO target: the grey forward-range marker (indicator.cpp:367-405).
// Run once per HUD skin:
//   dotnet run --project tools/p804-proof -c Release -- --style Classic --out <dir>
//   dotnet run --project tools/p804-proof -c Release -- --style Modern  --out <dir>
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

string output = Path.GetFullPath(Arg("--out", ".scratch/p804-capture"));
Directory.CreateDirectory(output);
string style = Arg("--style", "Classic");
bool roundRadar = Array.IndexOf(args, "--round-radar") >= 0;
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
string settingsPath = Path.Combine(output, "hud.json");
File.WriteAllText(settingsPath, $"{{\"Style\":\"{style}\",\"Size\":1,\"ColorBlindSafe\":false,\"RoundRadar\":{(roundRadar ? "true" : "false")},\"DamageWash\":true,\"ChatLines\":7}}");
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settingsPath);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "P804 proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
});
var mission = server.Missions[0];
mission.Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); }
});
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P804 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new P804Proof(game, server, mission, output, style));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}

sealed class P804Proof(Game1 game, Srv server, Mission mission, string output, string style)
    : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private const float EnemyDistance = 450f;
    private const float EnemyLateralSpeed = 140f;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private Ship? _own, _enemy;
    private readonly float[] _zeroTurnRates = new float[3];
    private SimVec _posePos, _simVelocity;
    private readonly Orientation _poseOrientation = Orientation.Identity;
    private bool _poseSet, _loadoutForced;
    private short _enemyId = -1;
    private SimVec? _enemyWorld;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] callArgs) =>
        (typeof(Game1).GetMethod(name, Hidden) ?? throw new MissingMethodException(name)).Invoke(game, callArgs);
    private void ServerCall(string name, params object?[] callArgs) =>
        (typeof(Srv).GetMethod(name, Hidden) ?? throw new MissingMethodException("Server." + name)).Invoke(server, callArgs);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("P804_SCREEN " + name);
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

    private SimVec Relative(float forward, float lateral, float up)
    {
        SimVec f = Norm(_poseOrientation.GetForward());
        SimVec r = Norm(_poseOrientation.GetRight());
        SimVec u = Norm(_poseOrientation.GetUp());
        return new SimVec(
            _posePos.X + f.X * forward + r.X * lateral + u.X * up,
            _posePos.Y + f.Y * forward + r.Y * lateral + u.Y * up,
            _posePos.Z + f.Z * forward + r.Z * lateral + u.Z * up);
    }

    private object MakeTarget(string kind, short id, string name)
    {
        var kindType = typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)!;
        var targetType = typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)!;
        object k = Enum.Parse(kindType, kind);
        return Activator.CreateInstance(targetType, k, id, name, (short)-1, true)!;
    }

    private void KeepPose()
    {
        if (!_poseSet || _own is null) return;
        var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
        if (!sim.HaveHull) return;
        sim.Seed(_posePos, _simVelocity, _poseOrientation, _zeroTurnRates);
        sim.Freeze();
        _own.Position = _posePos;
        _own.Velocity = default;
        _own.SetOrientation(_poseOrientation);
        if (_enemy is not null)
        {
            // A real engine-ticked enemy pinned in place but with a LATERAL
            // velocity, so the client's lead solve has real target motion (the
            // same trick HUD-18 used for its static cases). It is placed along
            // the ACTUAL eased camera forward so it projects near the view centre
            // (the ship attitude and the cockpit camera can differ); once the
            // behind-camera step runs the enemy keeps its frozen WORLD position.
            if (_enemyWorld is { } frozen) _enemy.Position = frozen;
            else
            {
                var cam = Get<CameraPose>("_camDisplay");
                var fwd = Vector3.Transform(Vector3.Forward, cam.Orient);
                var pos = cam.Eye + fwd * EnemyDistance;
                _enemy.Position = new SimVec(pos.X, pos.Y, pos.Z);
            }
            _enemy.Velocity = new SimVec(EnemyLateralSpeed, 0f, 0f);
            _enemy.SetOrientation(_poseOrientation);
        }
    }

    /// <summary>Force a lead-capable hull (Adv Stl Fighter, hull 17, has
    /// c_habmLeadIndicator 0x800) plus a known absolute-energy weapon (Pulse
    /// Laser: 1250 m/s, 0.5 s lifespan, power +5) through the real
    /// ChangeHull/ClearLoadout/MountPart path so the client receives
    /// LOADOUT_CHANGE and resolves the same weapon spec native's GetWeapon does.
    /// </summary>
    private void ForceLoadout()
    {
        if (_loadoutForced || _own is null) return;
        var hull = mission.Core.HullTypes[17];
        _own.ChangeHull(hull);
        var part = mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Weapon w && w.ProjectileTypeId == 66);
        _own.ClearLoadout();
        _own.MountPart(part, 100, 0, Allegiance.Core.Wire.ExpandedPartData.FullyMounted);
        _loadoutForced = true;
        Console.WriteLine($"[p804] forced hull {hull.HullId} '{hull.Name}' leadCap={hull.HasCapability(DataHullTypeIGC.HabmLeadIndicator)}"
            + $" + weapon part {part.PartId} '{part.Name}' (projectile 66: 1250 m/s, 0.5 s)");
    }

    private void SpawnEnemy()
    {
        _own ??= OwnServerShip();
        if (_own?.Sector is not { } sector || _own.SideId is not { } mySide) return;
        short enemySide = mission.Sides.FirstOrDefault(s => s.Id != mySide)?.Id ?? mySide;
        short id = (short)typeof(Srv).GetMethod("AllocateShipId", Hidden)!.Invoke(server, null)!;
        object tuple = typeof(Srv).GetMethod("SpawnDefaultShip", Hidden)!
            .Invoke(server, [mission, id, (short?)enemySide])!;
        _enemy = (Ship)tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        var home = (Sector)tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
        home.Remove(_enemy);
        mission.PlaceShip(_enemy, sector);
        _enemy.Position = Relative(EnemyDistance, 0f, 0f);
        _enemy.Velocity = new SimVec(EnemyLateralSpeed, 0f, 0f);
        _enemyId = _enemy.Id;
        Console.WriteLine($"[p804] enemy {_enemy.Id} hull '{_enemy.HullType.Name}' {EnemyDistance} m ahead,"
            + $" lateral velocity {EnemyLateralSpeed} m/s");
    }

    private void SelectTarget()
    {
        if (_enemyId < 0) return;
        // Native SetTarget: commit through the real ORDER_CHANGE path so
        // MyCurrentCommand (c_cmdCurrent) is the target the HUD solves against.
        Call("CommitTargetForPlayer", MakeTarget("Ship", _enemyId, $"Ship {_enemyId}"));
        Console.WriteLine($"[p804] target committed to ship {_enemyId}");
    }

    private void Dump(string tag)
    {
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var world = session.World;
            var cmd = world.MyCurrentCommand;
            var lead = Get<LeadIndicatorView?>("_leadIndicator");
            string leadText = lead is { } lv
                ? FormattableString.Invariant(
                    $"leadPx=({lv.LeadCenter.X:0},{lv.LeadCenter.Y:0}) targetPx=({lv.TargetCenter.X:0},{lv.TargetCenter.Y:0}) granted={lv.Granted} inRange={lv.InRange} green={lv.Green:0.00} visible={lv.Visible} targetVisible={lv.TargetVisible}")
                : "lead=null";
            var chase = Get<Vector2?>("_chaseAim");
            Console.WriteLine($"[p804] {tag}: cmd={(cmd is { } c ? $"{c.TargetType}:{c.TargetId}" : "none")}"
                + $" camera={Get<object>("_cameraMode")} {leadText}"
                + $" chaseAim={(chase is { } ca ? $"({ca.X:0},{ca.Y:0})" : "null")}");
        }
    }

    /// <summary>Per-frame cost of the client lead solve, measured on the real
    /// Release build (the same call Game1 makes once per frame).</summary>
    private static void MeasureSolveCost()
    {
        var orientation = Orientation.Identity;
        var input = new TargetWeaponRangeInput(
            SameCluster: true, CanSee: true, HasWeapon: true, ChaseCamera: false,
            FriendlyOrAllied: false, ProjectilePower: 5f, AbsoluteF: true,
            AmmoPerShot: false, EnergyWeapon: true, ProjectileSpeed: 1250f,
            ProjectileLifespan: 0.5f, SpeedAmmoGa: 1f, LifespanEnergyGa: 1f,
            MyPosition: default, MyOrientation: orientation, MyVelocity: default,
            MountPosition: new SimVec(2f, -1f, 0f),
            TargetRadius: 9.5f, TargetPosition: new SimVec(0f, 0f, -450f),
            TargetVelocity: new SimVec(EnemyLateralSpeed, 0f, 0f));
        for (int i = 0; i < 1000; i++) ClientLead.Solve(input);
        const int n = 200_000;
        var watch = Stopwatch.StartNew();
        float sink = 0f;
        for (int i = 0; i < n; i++) sink += ClientLead.Solve(input).Green;
        watch.Stop();
        Console.WriteLine(FormattableString.Invariant(
            $"[p804] frame cost: {watch.Elapsed.TotalNanoseconds / n:0} ns per ClientLead.Solve (Release, sink={sink:0.000})"));
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 240) throw new TimeoutException("P804 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying) return;
        KeepPose();
        if (_watch.Elapsed.TotalSeconds < _next) return;
        string suffix = style.ToLowerInvariant();
        switch (_step++)
        {
            case 0:
                Call("DismissBriefing");
                var sim = Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");
                if (!sim.HaveHull) { _step = 0; return; }
                _posePos = sim.Position;
                _poseOrientation.Set(sim.Orientation.GetForward(), sim.Orientation.GetUp());
                _poseSet = true;
                _simVelocity = default;
                _own ??= OwnServerShip();
                ForceLoadout();
                // Let the cockpit camera finish easing onto the ship's attitude
                // before the controlled scene is placed.
                _next = _watch.Elapsed.TotalSeconds + 3.0;
                return;
            case 1:
                SpawnEnemy();
                _next = _watch.Elapsed.TotalSeconds + 1.5;
                return;
            case 2:
                SelectTarget();
                _next = _watch.Elapsed.TotalSeconds + 1.6;
                return;
            case 3:
                Set("_cameraMode", CameraMode.Cockpit);
                Dump("cockpit-lead");
                Shot($"p804-a-cockpit-lead-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            case 4:
                Set("_cameraMode", CameraMode.Chase);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            case 5:
                Dump("chase-lead-aim");
                Shot($"p804-b-chase-lead-aim-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            case 6:
                // Clear the current command target -> native's no-target
                // chase marker (indicator.cpp:367-405).
                var session = Get<ConnectionSession>("_session");
                lock (session.Gate) session.World.SetCurrentCommand(null);
                Set("_autoTarget", null);
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            case 7:
                Dump("chase-no-target");
                Shot($"p804-c-chase-no-target-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            case 8:
                // Turn the ship 180° so the selected target and its radar blip
                // are BEHIND the camera: the lead direction is behind the eye
                // (TransformDirectionToImage false -> no cockpit marker,
                // indicator.cpp:265,283) and the radar blip is edge-clamped in
                // its real eye-space direction (WinTrek.cpp:7883-7921).
                // Freeze the enemy in world space, then turn the ship around.
                _enemyWorld = _enemy is { } frozenEnemy ? frozenEnemy.Position : Relative(EnemyDistance, 0f, 0f);
                _poseOrientation.Set(
                    Norm(new SimVec(-_poseOrientation.GetForward().X,
                        -_poseOrientation.GetForward().Y,
                        -_poseOrientation.GetForward().Z)),
                    _poseOrientation.GetUp());
                SelectTarget();
                Set("_cameraMode", CameraMode.Cockpit);
                _next = _watch.Elapsed.TotalSeconds + 1.2;
                return;
            case 9:
                Dump("behind-camera");
                Shot($"p804-d-behind-camera-radar-{suffix}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                return;
            case 10:
                MeasureSolveCost();
                game.Exit();
                return;
            default:
                game.Exit();
                return;
        }
    }
}
