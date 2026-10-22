// P781 — ripcord lead-up proof. Drives the real Game1 over TCP against a real
// local server (the hud20-proof pattern: production 1920x1080 backbuffer) and
// captures the native lead-up tell the port was missing:
//   Scene A (remote): an enemy ship ~300 u ahead of the player, turning (non-zero
//     turn rates) at speed, ripcords through the server's NPC path
//     (OnNpcRequestRipcord) to an anchor in a sector that is not its home.
//     External-target camera on it; captures at t ≈ 2.5 / 1.0 / 0.2 s before
//     transit + one frame after it vanished.
//   Scene C (aleph): a ship flying straight at the sector's nearest aleph
//     (native's per-frame SetTimeUntilAleph, WinTrek.cpp:7783-7802); captures at
//     ETA ≈ 2.5 / 1.0 / 0.2 s.
//   Scene B (own ship): the launched own ship, yawing + rolling at speed, presses
//     ripcord (the L-key's exact SendRipcordRequest); fixed-orientation external
//     camera so the hull spin is visible; captures at the same three times plus
//     one after the transit. SHIP_RESETs during the countdown MUST be 0 (the
//     server accepts the frozen-flight poses), the own pose MUST spin and the
//     spark count MUST grow.
// Per capture it prints:  [p781] scene=… t=… sparks_live=… ownYawDeg=… speed=…
//   --perf --ships N : N rippers in the view frustum with staggered, endlessly
//     re-armed countdowns (abort at ~0.3 s + re-request, so the ramps never end
//     and nobody teleports out of view), 600 frames after warm-up; prints
//     p50/p95/p99 frame ms, peak live spark count and GC bytes/frame on the game
//     thread. Compiles against the pre-P781 base too (P781 members are reached
//     by reflection only), so the same scene measures base vs branch.
//   --ships 0 gives the 0-ripper baseline.
// Usage (from the repo root; ALLEGIANCE_NOVSYNC=1 for --perf):
//   dotnet run --project tools/ripcord-proof -c Release -- --out <dir> [--perf] [--ships 50]
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

string output = Path.GetFullPath(Arg("--out", ".scratch/p781-proof"));
Directory.CreateDirectory(output);
bool perf = args.Contains("--perf");
int ships = int.Parse(Arg("--ships", perf ? "50" : "0"));

string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "P781 ripcord proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 3600 }]
});
var mission = server.Missions[0];
mission.Stage = MissionStage.Started;

// Synthetic ripcord anchors (LiveRipcordTests' injection pattern): a side-0
// anchor in 1054 and a side-1 anchor in 1055 — neither is any ship's home.
var anchorType = mission.Core.StationTypes.Values.First(t => t.HasCapability(DataStationTypeIGC.SabmRipcord)).StationTypeId;
short anchorId = 8300;
void AddAnchor(short sectorId, short sideId, string name)
{
    var sector = mission.Sectors.First(s => s.Id == sectorId);
    var data = new DataStationIGC(
        Position: [0f, 0f, 0f], Up: [0f, 0f, 1f], Forward: [1f, 0f, 0f], Rotation: [1f, 0f, 0f, 0f],
        ClusterId: sectorId, SideId: sideId, StationId: anchorId++, StationTypeId: anchorType,
        BpHull: 1, BpShield: 1, Name: name);
    mission.PlaceStation(new Station(data, mission.Core.StationTypes[data.StationTypeId]), sector);
}
AddAnchor(1054, 0, "P781 alpha anchor");
AddAnchor(1055, 1, "P781 bravo anchor");

using var stop = new CancellationTokenSource();
// Serialize every server touch: the ticker thread and the game thread (dummy
// spawning, ripcord requests) must never be inside the sim at the same time.
// Each TickSim advances the sim by one fixed period (1/UpdatesPerSecond), so
// pace it at REAL time: a free-running ticker would run the sim ~10x wall
// speed, the countdown would end before the client saw it, and every own
// SHIP_UPDATE would be out of date.
var ticker = new Thread(() =>
{
    var clock = Stopwatch.StartNew();
    long due = 0;
    const long periodMs = 1000 / 20;
    while (!stop.IsCancellationRequested)
    {
        long nowMs = clock.ElapsedMilliseconds;
        if (nowMs < due) { Thread.Sleep((int)Math.Min(5, due - nowMs)); continue; }
        due = Math.Max(due + periodMs, nowMs - 4 * periodMs);   // catch up at most 4 ticks after a stall
        lock (ProofBase.SimGate)
            if (!stop.IsCancellationRequested)
            {
                server.TickSim();
                ProofBase.TickHook?.Invoke();   // perf: re-arm rippers on the sim thread, off the measured game thread
            }
    }
});
ticker.Start();
int exit = 0;
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P781 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true },   // stay docked; we launch ourselves
    });
    game.Content.RootDirectory = content;
    ProofBase component = perf
        ? new RipcordPerf(game, server, mission, ships)
        : new RipcordProof(game, server, mission, output);
    game.Components.Add(component);
    game.Run();
    exit = component.Failed ? 1 : 0;
}
finally { stop.Cancel(); ticker.Join(); }
return exit;

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}

/// <summary>Shared reflection plumbing (Game1/Server internals are not
/// InternalsVisibleTo'd to the proof exes).</summary>
abstract class ProofBase : DrawableGameComponent
{
    internal static readonly object SimGate = new();
    internal static volatile Action? TickHook;

    protected const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    protected readonly Stopwatch Watch = Stopwatch.StartNew();
    protected readonly Srv Server;
    protected readonly Mission Mission;
    protected Ship? Own;
    public bool Failed { get; protected set; }

    protected ProofBase(Game1 game, Srv server, Mission mission) : base(game)
    {
        Server = server;
        Mission = mission;
    }

    protected T Get<T>(string name)
    {
        var field = typeof(Game1).GetField(name, Hidden);
        if (field is not null) return (T)field.GetValue(Game)!;
        var prop = typeof(Game1).GetProperty(name, Hidden) ?? throw new MissingMemberException(name);
        return (T)prop.GetValue(Game)!;
    }
    protected void Set(string name, object? value)
    {
        var field = typeof(Game1).GetField(name, Hidden);
        if (field is not null) { field.SetValue(Game, value); return; }
        var prop = typeof(Game1).GetProperty(name, Hidden) ?? throw new MissingMemberException(name);
        prop.SetValue(Game, value);
    }
    protected object? Call(string name, params object?[] args) =>
        (typeof(Game1).GetMethod(name, Hidden) ?? throw new MissingMethodException(name)).Invoke(Game, args);
    protected object? ServerCall(string name, params object?[] args)
    {
        var m = typeof(Srv).GetMethod(name, Hidden) ?? throw new MissingMethodException("Server." + name);
        lock (SimGate) return m.Invoke(Server, args);
    }

    protected ConnectionSession Session => Get<ConnectionSession>("_session");
    protected bool Docked => Get<bool>("_dockedActive");
    protected bool Flying => Get<object>("_state").ToString() == "Flying" && !Docked;
    protected double Now => Watch.Elapsed.TotalSeconds;
    protected static long ClientMs => Environment.TickCount64;   // == Game1.ClientNowMilliseconds outside a bench replay

    protected Ship? OwnServerShip()
    {
        var client = Session;
        short? id;
        lock (client.Gate) id = client.World.MyShipId;
        return id is { } shipId ? FindShip(shipId) : null;
    }

    protected Ship? FindShip(short id)
    {
        lock (SimGate)
            return Mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).FirstOrDefault(s => s.Id == id);
    }

    protected int ShipResetCount => Convert.ToInt32(typeof(Game1).GetField("_shipResetCount", Hidden)!.GetValue(Game));
    protected int SparksLive => Get<WarpTransitParticles>("_warpParticles").ActiveCount;

    protected Allegiance.ClientCore.Flight.OwnShipSimulator OwnSim =>
        Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim");

    /// <summary>A ripcord-capable fighter hull with a real countdown.</summary>
    protected DataHullTypeIGC RipHull() => Mission.Core.HullTypes.Values
        .Where(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord) && h.RipcordSpeed > 0f
                    && !h.HasCapability(DataHullTypeIGC.HabmLifepod) && h.Length is > 8f and < 40f)
        .OrderBy(h => h.RipcordSpeed).First();

    /// <summary>Spawn a PilotTypePlayer dummy (the AI loop only drives
    /// pilotType &lt; c_ptPlayer, so its flight is exactly what we set).</summary>
    protected Ship SpawnDummy(short sideId, Sector sector, SimVec pos, SimVec vel, Orientation ori)
    {
        lock (SimGate)
        {
            var ship = new Ship(Mission.NextObjectId(), RipHull(), pos, vel, ori)
            { SideId = sideId, PilotType = Allegiance.Core.Sim.Ship.PilotTypePlayer };
            Mission.PlaceShip(ship, sector);
            // P775 — dummies bypass join; announce them like any newly created
            // player (FedSrv.CPP:2269) so the client keeps a PlayerInfo.
            typeof(Srv).GetMethod("AnnounceNewShip", Hidden)!.Invoke(Server, [ship]);
            return ship;
        }
    }

    /// <summary>Make a dummy turn at constant stick (yaw + roll) and keep its
    /// turn rates non-zero from the first tick.</summary>
    protected void Turn(Ship ship, float yaw, float pitch, float roll, float throttle)
    {
        lock (SimGate)
        {
            ship.Controls = new ControlData(Yaw: yaw, Pitch: pitch, Roll: roll, Throttle: throttle);
            var h = ship.HullType;
            ship.SetTurnRate(0, yaw * h.MaxTurnRates[0]);
            ship.SetTurnRate(1, pitch * h.MaxTurnRates[1]);
            ship.SetTurnRate(2, roll * h.MaxTurnRates[2]);
        }
    }

    protected void SelectTarget(short shipId)
    {
        var kindType = typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)!;
        var targetType = typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)!;
        object value = Activator.CreateInstance(targetType, Enum.Parse(kindType, "Ship"), shipId, $"Ship {shipId}", (short)-1, true)!;
        Set("_autoTarget", value);
    }

    /// <summary>Camera mode by name (the enum is internal to the game assembly's
    /// Rendering namespace but public, so parse it).</summary>
    protected void Camera(string mode)
    {
        var f = typeof(Game1).GetField("_cameraMode", Hidden)!;
        f.SetValue(Game, Enum.Parse(f.FieldType, mode));
        // Re-align the external camera to the ship on the next frame.
        typeof(Game1).GetField("_externalCamAlignedFor", Hidden)?.SetValue(Game, null);
    }

    protected void ExternalDistance(float d)
    {
        var zoom = Get<object>("_flightZoom");
        zoom.GetType().GetField("<ExternalDistance>k__BackingField", Hidden)!.SetValue(zoom, d);
    }

    protected static SimVec Norm(SimVec v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        return len <= 1e-6f ? new SimVec(0, 0, -1) : v / len;
    }

    protected static float Dot(SimVec a, SimVec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
}

/// <summary>The screenshot scenes (A remote, C aleph, B own ship).</summary>
sealed class RipcordProof : ProofBase
{
    private readonly string _out;
    public RipcordProof(Game1 game, Srv server, Mission mission, string output)
        : base(game, server, mission) => _out = output;

    private double _next;
    private int _step;
    private Ship? _enemy;
    private Ship? _alephShip;
    private SimVec _warpPos;
    private SimVec _ownForwardAtRip;
    private float _ripDur;
    private long _clientStartMs;
    private readonly (double T, string Tag)[] _marks = [(2.5, "t2.5s"), (1.0, "t1.0s"), (0.2, "t0.2s")];
    private int _markIndex;
    private int _resetsAtActivate;
    private int _maxSparksB;
    private int _firstSparksB = -1;
    private double _maxOwnYawDeg;
    private float[] _spinRates = new float[3];
    private double _deadline;

    private void Shot(string name)
    {
        Set("_autoshotDir", _out);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("P781_SCREEN " + name);
    }

    private void Fail(string why)
    {
        Console.WriteLine("[p781] FAIL: " + why);
        Failed = true;
        Game.Exit();
    }

    private float Duration(Ship ship) => ship.HullType.RipcordSpeed;   // XXV_simple default side GAs (c_gaRipcordTime = 1)

    private void Print(string scene, double t, Ship? subject)
    {
        var sim = OwnSim;
        double yaw = 0;
        if (scene == "B" && _ownForwardAtRip != default)
        {
            var f = sim.Orientation.GetForward();
            yaw = Math.Acos(Math.Clamp(Dot(Norm(_ownForwardAtRip), Norm(f)), -1f, 1f)) * 180 / Math.PI;
            if (yaw > _maxOwnYawDeg) _maxOwnYawDeg = yaw;
        }
        float speed = scene == "B" ? sim.Velocity.Length() : subject?.Velocity.Length() ?? 0f;
        int emitters = Convert.ToInt32(typeof(Game1).GetField("_teleportEmitterCount", Hidden)?.GetValue(Game) ?? -1);
        bool ownRip, remRip;
        lock (Session.Gate)
        {
            ownRip = Session.World.RipcordActive;
            remRip = subject is not null && Session.World.TryGetRemoteRipcord(subject.Id, out _);
        }
        var rates = sim.TurnRates;
        Console.WriteLine($"[p781] scene={scene} t={t:0.00} sparks_live={SparksLive} emitters={emitters} ownYawDeg={yaw:0.0} " +
                          $"speed={speed:0.0} ownTurn=({rates[0]:0.00},{rates[1]:0.00},{rates[2]:0.00}) ownRip={ownRip} remRip={remRip} " +
                          $"resets={ShipResetCount} camera={Get<object>("_cameraMode")}");
    }

    public override void Update(GameTime gameTime)
    {
        if (Now > 300) { Fail("timeout"); return; }

        // Scene B pre-activation: hold the pilot's stick-equivalent turn rates
        // (yaw + roll at speed) on the local sim every frame until the client
        // decodes RIPCORD_ACTIVATE. The harness cannot inject keyboard/mouse
        // input, so the turn rates a held stick would have built up are seeded
        // directly; from the activation edge on, ONLY the production frozen-flight
        // rule keeps them.
        if (_step == 31 || (_step == 32 && _clientStartMs == 0))
        {
            bool active;
            lock (Session.Gate) active = Session.World.RipcordActive;
            var sim = OwnSim;
            if (!active)
            {
                var o = sim.Orientation;
                sim.Seed(sim.Position, Norm(o.GetForward()) * 70f, o, _spinRates);
            }
        }
        // Scene B: track the max spark count every frame.
        if (_step is 32 or 33)
        {
            int s = SparksLive;
            if (s > _maxSparksB) _maxSparksB = s;
        }

        if (Now < _next) return;
        switch (_step)
        {
            case 0:   // docked → launch through the real hangar verb
                if (!Docked) { _next = Now + 0.5; return; }
                Own = OwnServerShip();
                if (Own is null) { _next = Now + 0.5; return; }
                Call("DismissBriefing");
                Call("LaunchFromHangar", Session.World.Ships[Own.Id]);
                Console.WriteLine($"[p781] launch requested for own ship {Own.Id}");
                _next = Now + 2.0;
                _step = 1;
                return;

            case 1:   // flying: make sure the own hull can ripcord, then build Scene A
                if (!Flying) { _next = Now + 0.5; return; }
                Own = OwnServerShip();
                if (Own?.Sector is not { } sector) { _next = Now + 0.5; return; }
                if (Own.HullType.RipcordSpeed <= 0f || Own.HullType.HasCapability(DataHullTypeIGC.HabmNoRipcord))
                {
                    lock (SimGate) Own.ChangeHull(RipHull());
                    Console.WriteLine($"[p781] own hull -> '{Own.HullType.Name}' (ripcord-capable)");
                }
                Console.WriteLine($"[p781] own ship {Own.Id} flying in sector {sector.Id}, hull '{Own.HullType.Name}' ripcordSpeed={Own.HullType.RipcordSpeed} len={Own.HullType.Length}");
                {
                    var fwd = Norm(Own.Orientation.GetForward());
                    var up = Norm(Own.Orientation.GetUp());
                    var pos = Own.Position + fwd * 300f + up * 40f;
                    _enemy = SpawnDummy(1, sector, pos, fwd * 40f, Own.Orientation);
                    Turn(_enemy, yaw: 0.6f, pitch: 0f, roll: 0.8f, throttle: -1f);
                    Console.WriteLine($"[p781] scene A enemy {_enemy.Id} at {pos} sector {sector.Id}");
                }
                _deadline = Now + 8;
                _next = Now + 1.0;
                _step = 2;
                return;

            case 2:   // wait until the client really has the enemy, then frame + ripcord it
                {
                    bool seen;
                    lock (Session.Gate)
                        seen = Session.World.Ships.TryGetValue(_enemy!.Id, out var e) && !e.IsGone && e.Latest is not null;
                    if (!seen)
                    {
                        if (Now > _deadline) { Fail("client never received the scene-A enemy"); return; }
                        _next = Now + 0.25;
                        return;
                    }
                    SelectTarget(_enemy!.Id);
                    Camera("ExternalTarget");
                    ExternalDistance(70f);
                    Set("_hideHud", true);   // the target box would cover the spray
                    ServerCall("OnNpcRequestRipcord", _enemy, Mission.Sectors.First(s => s.Id == 1055));
                    _ripDur = Duration(_enemy!);
                    _clientStartMs = 0;
                    _markIndex = 0;
                    _deadline = Now + _ripDur + 6;
                    Console.WriteLine($"[p781] scene A: enemy {_enemy!.Id} ripcord -> 1055 (duration {_ripDur:0.00}s)");
                    _next = Now + 0.02;
                    _step = 3;
                    return;
                }

            case 3:   // Scene A marks (client countdown) and the vanish
                {
                    if (Now > _deadline) { Fail("scene A did not complete"); return; }
                    long start;
                    bool has;
                    lock (Session.Gate) has = Session.World.TryGetRemoteRipcord(_enemy!.Id, out start);
                    if (has && _clientStartMs == 0) _clientStartMs = start;
                    if (_clientStartMs == 0) { _next = Now + 0.01; return; }
                    double t = _ripDur - (ClientMs - _clientStartMs) / 1000.0;
                    if (_markIndex < _marks.Length)
                    {
                        if (t <= _marks[_markIndex].T)
                        {
                            Shot($"p781-A{_markIndex + 1}-remote-{_marks[_markIndex].Tag}");
                            Print("A", t, _enemy);
                            _markIndex++;
                        }
                        _next = Now + 0.01;
                        return;
                    }
                    bool gone;
                    lock (Session.Gate)
                        gone = !Session.World.Ships.TryGetValue(_enemy!.Id, out var e) || e.IsGone;
                    if (gone)
                    {
                        _next = Now + 0.3;   // let the last sparks age a little
                        _step = 4;
                    }
                    else _next = Now + 0.02;
                    return;
                }

            case 4:
                Shot("p781-A4-remote-after-vanish");
                Print("A", -1, _enemy);
                _next = Now + 1.0;
                _step = 10;
                return;

            case 10:  // Scene C — a ship flying straight at the nearest aleph
                {
                    Own = OwnServerShip();
                    var sec = Own!.Sector!;
                    var warps = Session.World.Warps.Values.Where(w => w.ClusterId == sec.Id).ToList();
                    if (warps.Count == 0) { Fail("no aleph in the own sector"); return; }
                    var w = warps.OrderBy(w => (new SimVec(w.Position[0], w.Position[1], w.Position[2]) - Own.Position).Length()).First();
                    _warpPos = new SimVec(w.Position[0], w.Position[1], w.Position[2]);
                    // Start 5 s out at 90 u/s, off to the side of the line from
                    // the own ship so the camera (behind it) sees the approach.
                    var toWarp = Norm(_warpPos - Own.Position);
                    var start = _warpPos - toWarp * 450f;
                    var o = new Orientation();
                    o.Set(toWarp);
                    // Side 1 like scene A: a side-0 dummy first reaches the client
                    // without a hull and hits the pre-existing P607 stand-in
                    // NRE (ShipRenderer.cs:163) — see the P781 report.
                    _alephShip = SpawnDummy(1, sec, start, toWarp * 90f, o);
                    lock (SimGate) _alephShip.Controls = new ControlData(0f, 0f, 0f, Throttle: 1f);
                    lock (SimGate) _alephShip.Velocity = toWarp * 90f;
                    Console.WriteLine($"[p781] scene C: ship {_alephShip.Id} at {start} heading for warp {w.WarpId} at {_warpPos}");
                    _deadline = Now + 12;
                    _markIndex = 0;
                    _next = Now + 0.3;
                    _step = 11;
                    return;
                }

            case 11:  // wait for the client to have it, then frame it
                {
                    bool seen;
                    lock (Session.Gate)
                        seen = Session.World.Ships.TryGetValue(_alephShip!.Id, out var e) && !e.IsGone && e.Latest is not null;
                    if (!seen) { if (Now > _deadline) { Fail("client never received the aleph ship"); return; } _next = Now + 0.1; return; }
                    SelectTarget(_alephShip!.Id);
                    Camera("ExternalTarget");
                    ExternalDistance(70f);
                    Set("_hideHud", true);   // the target box would cover the spray
                    _step = 12;
                    _next = Now + 0.02;
                    return;
                }

            case 12:  // ETA marks (client-side: the pose the client renders + its velocity)
                {
                    if (Now > _deadline) { Fail("scene C did not complete"); return; }
                    SimVec p, v;
                    lock (Session.Gate)
                    {
                        if (!Session.World.Ships.TryGetValue(_alephShip!.Id, out var e) || e.IsGone || e.Latest is null)
                        {
                            Console.WriteLine("[p781] scene C ship left the view (transited)");
                            _step = 20; _next = Now + 0.8; return;
                        }
                        p = e.Latest.Position; v = e.Latest.Velocity;
                    }
                    var d = _warpPos - p;
                    float range = d.Length();
                    float closing = Dot(v, d / range);
                    float eta = closing > 0 ? range / closing : -1f;
                    if (_markIndex < _marks.Length && eta > 0 && eta <= _marks[_markIndex].T)
                    {
                        Shot($"p781-C{_markIndex + 1}-aleph-eta{_marks[_markIndex].Tag[1..]}");
                        Print("C", eta, _alephShip);
                        _markIndex++;
                    }
                    if (_markIndex >= _marks.Length) { _step = 20; _next = Now + 1.5; return; }
                    _next = Now + 0.01;
                    return;
                }

            case 20:  // Scene B — the own ship spins at speed in a fixed-orientation external view
                {
                    Own = OwnServerShip();
                    var h = Own!.HullType;
                    _spinRates = [0.45f * h.MaxTurnRates[0], 0f, 0.6f * h.MaxTurnRates[2]];
                    Set("_autoTarget", null);
                    Camera("External");
                    ExternalDistance(75f);
                    Set("_hideHud", true);
                    Console.WriteLine($"[p781] scene B: own spin rates yaw={_spinRates[0]:0.00} roll={_spinRates[2]:0.00} rad/s (hull max {h.MaxTurnRates[0]:0.00}/{h.MaxTurnRates[2]:0.00})");
                    _step = 30;
                    _next = Now + 0.2;
                    return;
                }

            case 30:  // seed the spin for 1.5 s (the 'held stick'), then press ripcord
                _step = 31;
                _next = Now + 1.5;
                return;

            case 31:
                {
                    _resetsAtActivate = ShipResetCount;
                    Session.SendRipcordRequest(1054);   // the L-key's exact outbound call
                    _ripDur = Duration(Own!);
                    _clientStartMs = 0;
                    _markIndex = 0;
                    _deadline = Now + _ripDur + 8;
                    Console.WriteLine($"[p781] scene B: own ripcord -> 1054 (duration {_ripDur:0.00}s), resets so far {_resetsAtActivate}");
                    _step = 32;
                    _next = Now;
                    return;
                }

            case 32:
                {
                    if (Now > _deadline) { Fail("scene B did not complete"); return; }
                    long? start;
                    lock (Session.Gate) start = Session.World.RipcordActive ? Session.World.RipcordActiveSinceLocalMs : null;
                    if (_clientStartMs == 0)
                    {
                        // Wait for the RIPCORD_ACTIVATE decode — then the frozen model is on.
                        if (start is not { } s0) { _next = Now + 0.005; return; }
                        _clientStartMs = s0;
                        _ownForwardAtRip = OwnSim.Orientation.GetForward();
                        _resetsAtActivate = ShipResetCount;
                        var r = OwnSim.TurnRates;
                        Console.WriteLine($"[p781] scene B: ACTIVATE decoded; own turn rates at activation ({r[0]:0.000},{r[1]:0.000},{r[2]:0.000}) speed {OwnSim.Velocity.Length():0.0}");
                    }
                    double t = _ripDur - (ClientMs - _clientStartMs) / 1000.0;
                    if (_firstSparksB < 0 && t <= 2.9) _firstSparksB = SparksLive;
                    if (_markIndex < _marks.Length)
                    {
                        if (t <= _marks[_markIndex].T)
                        {
                            Shot($"p781-B{_markIndex + 1}-own-{_marks[_markIndex].Tag}");
                            Print("B", t, null);
                            _markIndex++;
                        }
                        _next = Now + 0.01;
                        return;
                    }
                    // Transit: the own ship's server sector becomes 1054.
                    if (Own!.Sector?.Id == 1054)
                    {
                        int resets = ShipResetCount - _resetsAtActivate;
                        Console.WriteLine($"[p781] scene B: transit landed; SHIP_RESETs during the countdown = {resets} (must be 0); max sparks_live = {_maxSparksB}; max ownYawDeg = {_maxOwnYawDeg:0.0}");
                        if (resets != 0) { Fail($"{resets} SHIP_RESET(s) during the own ripcord"); return; }
                        if (_maxOwnYawDeg < 5) { Fail($"own pose never spun (max {_maxOwnYawDeg:0.0} deg)"); return; }
                        if (_maxSparksB <= 0) { Fail("no lead-up sparks on the own ship"); return; }
                        _step = 33;
                        _next = Now + 0.6;
                        return;
                    }
                    _next = Now + 0.01;
                    return;
                }

            case 33:
                Shot("p781-B4-own-after-transit");
                Print("B", -1, null);
                lock (Session.Gate)
                    Console.WriteLine($"[p781] scene B after transit: client RipcordActive={Session.World.RipcordActive} ownSector={Session.World.Ships[Own!.Id].SectorId}");
                _step = 99;
                _next = Now + 1.0;
                return;

            default:
                Console.WriteLine("[p781] proof complete");
                Game.Exit();
                return;
        }
    }
}

/// <summary>The --perf scene: N rippers inside the view frustum with endlessly
/// re-armed, staggered countdowns; 600 measured frames after warm-up.</summary>
sealed class RipcordPerf : ProofBase
{
    private readonly int _ships;
    public RipcordPerf(Game1 game, Srv server, Mission mission, int ships)
        : base(game, server, mission) => _ships = ships;

    private double _next;
    private int _step;
    private readonly List<Ship> _rippers = [];
    private readonly List<double> _armedAt = [];
    private readonly double[] _frameMs = new double[600];
    private int _frames;
    private long _lastTicks;
    private long _gcStart;
    private int _peakSparks;
    private double _warmEnd;
    private Sector? _target;
    private WarpTransitParticles? _pool;

    /// <summary>Frame time = Draw-to-Draw wall time (one sample per rendered
    /// frame; the component's Draw runs after Game1's own Draw).</summary>
    public override void Draw(GameTime gameTime)
    {
        long ticks = Stopwatch.GetTimestamp();
        if (_step == 4 && _frames < 600)
        {
            if (_lastTicks != 0)
                _frameMs[_frames++] = (ticks - _lastTicks) * 1000.0 / Stopwatch.Frequency;
            int live = _pool!.ActiveCount;
            if (live > _peakSparks) _peakSparks = live;
        }
        _lastTicks = ticks;
    }

    public override void Update(GameTime gameTime)
    {
        if (Now > 400) { Console.WriteLine("[p781-perf] timeout"); Failed = true; Game.Exit(); return; }


        if (Now < _next) return;
        switch (_step)
        {
            case 0:
                if (!Docked) { _next = Now + 0.5; return; }
                Own = OwnServerShip();
                if (Own is null) { _next = Now + 0.5; return; }
                Call("DismissBriefing");
                Call("LaunchFromHangar", Session.World.Ships[Own.Id]);
                _next = Now + 2.5;
                _step = 1;
                return;

            case 1:
                {
                    if (!Flying) { _next = Now + 0.5; return; }
                    Own = OwnServerShip();
                    if (Own?.Sector is not { } sector) { _next = Now + 0.5; return; }
                    _target = Mission.Sectors.First(s => s.Id == 1055);   // the side-1 anchor
                    var fwd = Norm(Own.Orientation.GetForward());
                    var up = Norm(Own.Orientation.GetUp());
                    var right = Norm(new SimVec(fwd.Y * up.Z - fwd.Z * up.Y, fwd.Z * up.X - fwd.X * up.Z, fwd.X * up.Y - fwd.Y * up.X));
                    for (int i = 0; i < _ships; i++)
                    {
                        float lateral = ((i % 10) - 4.5f) * 40f;
                        float vertical = (((i / 10) % 5) - 2f) * 35f;
                        float depth = 700f + (i / 10) * 40f;
                        var pos = Own.Position + fwd * depth + right * lateral + up * vertical;
                        // Side 1 (enemy): a side-0 dummy hits the pre-existing
                        // P607 stand-in NRE (see the P781 report).
                        var ship = SpawnDummy(1, sector, pos, SimVec.Zero, Own.Orientation);
                        Turn(ship, yaw: 0.5f, pitch: 0f, roll: 0.7f, throttle: -1f);
                        _rippers.Add(ship);
                        _armedAt.Add(-1);
                    }
                    // Cockpit view looking at the block of rippers; throttle to
                    // stop so the own ship coasts to rest instead of flying
                    // through the block during the measurement.
                    Camera("Cockpit");
                    Set("_throttle", -1f);
                    Console.WriteLine($"[p781-perf] {_ships} rippers spawned ahead of the own ship");
                    _next = Now + 1.5;
                    _step = 2;
                    return;
                }

            case 2:   // staggered first activations over one countdown length
                {
                    float dur = _rippers.Count > 0 ? _rippers[0].HullType.RipcordSpeed : 1f;
                    // First activations spread across one countdown, so the
                    // re-armed ramps stay out of phase for the whole run.
                    for (int i = 0; i < _rippers.Count; i++)
                        _armedAt[i] = Now + dur * i / Math.Max(1, _rippers.Count);
                    _warmEnd = Now + dur + 4.0;
                    Console.WriteLine($"[p781-perf] rippers armed (countdown {dur:0.00}s, staggered); warming up 6 s");
                    _pool = Get<WarpTransitParticles>("_warpParticles");
                    if (_ships > 0) TickHook = Rearm;   // runs inside SimGate on the ticker thread
                    _step = 3;
                    _next = _warmEnd;
                    return;
                }

            case 3:
                _gcStart = GC.GetAllocatedBytesForCurrentThread();
                _frames = 0;
                _lastTicks = 0;
                _peakSparks = 0;
                _step = 4;
                return;

            case 4:
                if (_frames >= 600)
                {
                    long alloc = GC.GetAllocatedBytesForCurrentThread() - _gcStart;
                    var sorted = (double[])_frameMs.Clone();
                    Array.Sort(sorted);
                    double p50 = sorted[299], p95 = sorted[569], p99 = sorted[593];
                    int emitters = Convert.ToInt32(typeof(Game1).GetField("_teleportEmitterCount", Hidden)?.GetValue(Game) ?? -1);
                    Console.WriteLine($"[p781-perf] ships={_ships} frames=600 p50={p50:0.00}ms p95={p95:0.00}ms p99={p99:0.00}ms " +
                                      $"mean={_frameMs.Average():0.00}ms peak_sparks_live={_peakSparks} emitters_last={emitters} " +
                                      $"gc_bytes_total={alloc} gc_bytes_per_frame={alloc / 600.0:0.0}");
                    TickHook = null;
                    _step = 5;
                    Game.Exit();
                    return;
                }
                return;
        }
    }

    /// <summary>Keep every ripper inside an endless countdown: when its server
    /// countdown is about to land (≤ 0.3 s), abort it (NA → RIPCORD_ABORTED) and
    /// request again (fresh RIPCORD_ACTIVATE) — so nobody teleports out of view
    /// and the ramp phases stay spread.</summary>
    private void Rearm()
    {
        for (int i = 0; i < _rippers.Count; i++)
        {
            if (Now < _armedAt[i]) continue;   // not yet due for its first activation
            var s = _rippers[i];
            float left = s.IsRipcordActive ? s.RipcordTimeLeft : -1f;
            if (left < 0f || left <= 0.3f) ArmFresh(i);
        }
    }

    private void ArmFresh(int i)
    {
        var s = _rippers[i];
        var m = typeof(Srv).GetMethod("OnNpcRequestRipcord", Hidden)!;
        if (s.IsRipcordActive) m.Invoke(Server, [s, null]);
        m.Invoke(Server, [s, _target]);
    }
}
