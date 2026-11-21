using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

// Browser lifecycle adapter. Prediction and manual key sampling are source-shared
// Game1 partials; the platform owns only cadence, resident scene setup and DOM input.
public sealed partial class Game1
{
    private ConnectionSession? _flightSession;
    private ConnectionSession _session => _flightSession!;
    private ShopCatalog? _shopCatalog;
    private readonly OwnShipSimulator _ownSim = new();
    private readonly OwnShipServerClock _ownServerClock = new();
    private readonly ShipUpdateCadence _shipSendCadence = new();
    private readonly DamageFlashTracker _flashTracker = new();
    private byte _ownShipCookie;
    private bool _ownShipSendArmed, _ownShipEligible, _ownSimSeeded;
    private long _shipResetCount;
    private short _ownSimHullId = -1, _ownSimLastSectorId = -1;
    private float _ownSimAbThrust;
    private uint _ownSimFrozenAtTick;
    private double _nowSeconds, _flightElapsedSeconds;
    private long ClientNowMilliseconds => Environment.TickCount64;
    private CameraMode _cameraMode = CameraMode.Chase;
    private readonly ChaseCamera _browserChase = new(350, 90, nearPlane: 10, farPlane: 10000, fieldOfViewDegrees: 50);
    public void ToggleContactView() { Watch.Stop(); _cameraMode = _cameraMode == CameraMode.Chase ? CameraMode.ExternalFlyBy : CameraMode.Chase; }
    public bool ViewingContact => _cameraMode == CameraMode.ExternalFlyBy;
    // P1067 — the parked director b-roll camera ("Watch"): the desktop P580
    // director over a browser battle. The shared pure Renderer.Director owns the
    // editorial decisions; DirectorWatch owns the browser snapshot.
    // Lazily created so a headless Game1 image (RuntimeHelpers.GetUninitializedObject,
    // used by the browser key tests) never depends on a field initializer.
    private DirectorWatch? _watchField;
    private DirectorWatch Watch => _watchField ??= new();
    private int? _watchSeedOverride;
    public bool Watching => Watch.Active;
    public int WatchSeed => Watch.Seed;
    /// <summary>Dev-only deterministic seed (?dev=1&amp;director-seed=N), the
    /// browser equivalent of the desktop's ALLEGIANCE_DIRECTOR_SEED.</summary>
    public void ConfigureWatchSeed(int? seed) => _watchSeedOverride = seed;
    public void ToggleWatch()
    {
        // The planner copies the own-ship attitude (Orientation is a mutable
        // class) and drifts only its own copy.
        if (!Watch.Toggle(NextWatchSeed(), _ownSim is { } sim ? sim.Orientation : null))
            Console.WriteLine("[browser] watch OFF");
        else
        {
            _cameraMode = CameraMode.Chase;   // the director owns the camera
            Console.WriteLine($"[browser] watch ACTIVE seed={Watch.Seed}");
        }
    }
    private int NextWatchSeed() => _watchSeedOverride ?? unchecked((int)DateTime.Now.Ticks ^ 0x5EED);

    private int _contactViewAngle;
    private static readonly Vector3[] ContactViewBackwards = [-Vector3.UnitZ, Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitX, -Vector3.UnitY, Vector3.UnitY];
    public void RotateContactView() => _contactViewAngle = (_contactViewAngle + 1) % ContactViewBackwards.Length;
    private bool _cloakToggled, _coastToggled, _groupFire = true;
    private int _selectedMount = 0;
    private float _throttle = -1;
    private KeyboardState _flightKeys, _lastFlightKeys;
    private int _pointerDx, _pointerDy;
    private bool _pointerLocked;
    private int _mouseButtons;
    private readonly JoystickFlightController _gamePadFlight = new();
    private bool _padRbWasDown, _padLbWasDown;
    private GamePadState? _browserPad;
    private GamePadState? CurrentPadState() => _browserPad;
    public void FlightGamepad(float[] axes, float[] buttons)
    {
        if (axes.Length != 4) return;
        float Axis(int i) => float.IsFinite(axes[i]) ? Math.Clamp(axes[i], -1, 1) : 0;
        float Button(int i) => i < buttons.Length && float.IsFinite(buttons[i]) ? Math.Clamp(buttons[i], 0, 1) : 0;
        // W3C standard slots -> MonoGame physical controls. Bindings remain in
        // the source-shared ApplyGamePadFlight, with no browser action table.
        var pressed = new List<Buttons>();
        if (Button(0) > .5f) pressed.Add(Buttons.A);
        if (Button(1) > .5f) pressed.Add(Buttons.B);
        if (Button(4) > .5f) pressed.Add(Buttons.LeftShoulder);
        if (Button(5) > .5f) pressed.Add(Buttons.RightShoulder);
        _browserPad = new GamePadState(new Vector2(Axis(0), Axis(1)), new Vector2(Axis(2), Axis(3)),
            Button(6), Button(7), pressed.ToArray());
    }
    public void FlightMouseButtons(int buttons) => _mouseButtons = buttons;
    private HudRenderer? _classicFlightHud;
    private ModernHudRenderer? _modernFlightHud;
    private NativeShipRenderer? _flightShips;
    private NativeStationRenderer? _flightStations;
    private NativeAsteroidRenderer? _flightRocks;
    private BlackSkyRenderer? _flightSky;
    private short _skySector = -1;
    private bool _flightLoaded;
    private readonly bool _ownShipPrediction = true;
    private float _autopilotDeltaSeconds;
    private int _autopilotMovementButtons;
    private AutopilotTarget? _browserAutoTarget;
    private MouseDeflection _browserAutoMouseBaseline;
    private ProjectileRenderer? _flightBolts;
    private WarpRenderer? _flightWarps;
    private BrowserHullUploads? _hullUploads;
    private readonly BrowserRockSpins _browserSpins = new();
    private DockedScreenRenderer? _flightDocked;
    private readonly HashSet<short> _seenShotLaunchers = new();
    private long _drawnProjectiles;
    private static SimVec Vec(float[] p) => new(p[0], p[1], p[2]);
    private void EngageAutopilotToward(AutopilotTarget target, string reason)
    {
        _browserAutoTarget = target; _browserAutoMouseBaseline = _mouseFlight.Current;
        Console.WriteLine("[browser] " + reason);
    }
    public void BeginFlight() => _throttle = FlightInput.DefaultThrottle;
    public void DockHome()
    {
        if (!Flying) return;
        var world = _session.World;
        var own = world.Ships[world.MyShipId!.Value];
        var target = world.Stations.Values.Where(s => s.ClusterId == own.SectorId && (s.SideId == world.MySideId || world.AreAllied(s.SideId, world.MySideId ?? -1))
            && (StationSabm(s.StationTypeId) & (DataStationTypeIGC.SabmLand | DataStationTypeIGC.SabmCapLand)) != 0)
            .OrderBy(s => (Vec(s.Position) - _ownSim.Position).LengthSquared()).Take(1).ToArray();
        if (target.Length != 0) EngageAutopilotToward(new(TargetKind.Station, target[0].StationId, target[0].Name), "Docking at " + target[0].Name);
    }
    public bool Flying => _flightSession?.World.MyShipId is { } id &&
        _flightSession.World.Ships.TryGetValue(id, out var ship) && ship.StationId < 0 && ship.Latest is not null;
    public long FlightFrames { get; private set; }
    public long AppliedResets => _shipResetCount;
    public int DrawnShips { get; private set; }
    private BattleShipScene? _battleScene;
    private readonly List<string> _visibleShipNames = new(32);
    private readonly List<HudContact> _flightContacts = new(32);
    private readonly HudSnapshotBuffers _flightHudBuffers = new();
    private readonly BoundingFrustum _rockFrustum = new(Matrix.Identity);
    private readonly Dictionary<short, DataAsteroidIGC[]> _sectorRocks = new();
    private long _rockMissionGeneration = -1;
    private int _rockCount = -1;
    public Action<string>? RequestBattlePack { get; set; }
    public int SectorShipCount { get; private set; }
    public int DrawnMeshes { get; private set; }
    public int DrawnStandIns { get; private set; }
    public int CulledRocks { get; private set; }
    public double ShipPassMilliseconds { get; private set; }
    /// <summary>P1069 — worst ship-pass time since flight start (the hull-upload
    /// hitch metric the harness reads).</summary>
    public double MaxShipPassMilliseconds { get; private set; }
    public string[] VisibleShipNames => _visibleShipNames.ToArray();
    private DataAsteroidIGC[] RocksInSector(ClientWorld world, short sector)
    {
        if (_rockMissionGeneration != world.MissionGeneration || _rockCount != world.Asteroids.Count)
        {
            if (_rockMissionGeneration != world.MissionGeneration) _browserSpins.Clear();
            _sectorRocks.Clear();
            foreach (var group in world.Asteroids.Values.GroupBy(r => r.ClusterId))
            {
                var rocks = group.ToArray(); _sectorRocks[group.Key] = rocks;
                foreach (var rock in rocks) _browserSpins.Register(rock);
            }
            _rockMissionGeneration = world.MissionGeneration; _rockCount = world.Asteroids.Count;
        }
        return _sectorRocks.GetValueOrDefault(sector) ?? [];
    }
    public object FlightMetrics => new {
        viewport = new { width = GraphicsDevice.Viewport.Width, height = GraphicsDevice.Viewport.Height },
        controls = new { yaw = _ownSim.Controls.Yaw, pitch = _ownSim.Controls.Pitch, throttle = _ownSim.Controls.Throttle, stateM = _ownSim.StateM },
        authoritative = _flightSession?.World.MyShipId is { } ownId && _flightSession.World.Ships.TryGetValue(ownId, out var ownShip) ? ownShip.Latest?.Position.ToString() : null,
        sectorId = _flightSession?.World.MyShipId is { } sid && _flightSession.World.Ships.TryGetValue(sid, out var sectorShip) ? (short?)sectorShip.SectorId : null,
        textures = SectorTextureMetrics, starmapOpen = StarmapOpen, selectedMapSector = SelectedMapSector,
        drawnRockSurfaces = _drawnRockSurfaces,
        sky = SectorSkyMetrics, scene = SceneMetrics,
        battle = new { sectorShips = SectorShipCount, contacts = _flightContacts.Count, meshes = DrawnMeshes,
            standIns = DrawnStandIns, hullIds = _battleScene?.Ships.Select(s => s.HullId).Distinct().ToArray(),
            culledRocks = CulledRocks, shipPassMs = ShipPassMilliseconds, maxShipPassMs = MaxShipPassMilliseconds,
            // P1069 — first-draw hitch accounting: how long a promoted hull waited
            // for its budgeted decode/upload, the queue depth and the last upload.
            firstDrawMs = _hullUploads?.LastFirstDrawMs ?? 0, maxFirstDrawMs = _hullUploads?.MaxFirstDrawMs ?? 0,
            hullUploadMs = _hullUploads?.LastUploadMs ?? 0, hullQueued = _hullUploads?.PendingCount ?? 0,
            hullUploaded = _hullUploads?.UploadedCount ?? 0 },
        // P1067 — Watch provenance: the seed makes a capture reproducible, the
        // planner state names the shot framed, and the subject categories come
        // from the same snapshot the camera used this frame.
        watch = new { active = Watch.Active, seed = Watch.Seed, kind = Watch.Planner.Kind.ToString(),
            shotChanges = Watch.Planner.ShotChanges, subjectIndex = Watch.Planner.SubjectIndex,
            subjects = Watch.Subjects.Count,
            categories = Watch.Subjects.GroupBy(s => s.Category.ToString()).ToDictionary(g => g.Key, g => g.Count()) },
        flying = Flying, frames = FlightFrames, drawnShips = DrawnShips,
        ships = VisibleShipNames, appliedResets = AppliedResets, position = _ownSim.Position.ToString(),
        selectedMount = _selectedMount, fixedMountCount = _flightSession is null ? 0 : FixedMountCount(),
        shotLaunchers = _seenShotLaunchers.ToArray(), drawnProjectiles = _drawnProjectiles, receivedProjectiles = _flightSession?.World.Diag.TotalReceived, autopilot = _browserAutoTarget?.Name, clockReady = _ownServerClock.Ready, seeded = _ownSimSeeded, pointerLocked = _pointerLocked };

    public void AttachOnlineSession(ConnectionSession? session)
    {
        ReleaseFlightInput();
        Watch.Stop();
        ResetBrowserHangar();   // P1070 — a new (or detached) session has no dock state
        if (_flightSession is { } previous)
        {
            // Late packets may drain while LOGOFF is being flushed. Their world
            // must no longer call a renderer whose session field is detached.
            lock (previous.Gate)
            {
                previous.World.RemoteShipHullResolver = null;
                previous.World.LifepodHullResolver = static _ => null;
            }
        }
        // A session can retire before any renderer was loaded (including the
        // headless lifecycle fixture). The first actual scene initializes itself.
        if (_flightLoaded) ResetScene();
        _flightSession = session;
        if (session is null) return;
        _shopCatalog ??= ShopCatalog.TryLoad(_assets);
        _battleScene = new(_shopCatalog!.Core!.HullTypes);
        _sectorRocks.Clear(); _browserSpins.Clear(); _rockCount = -1; _rockMissionGeneration = -1;
        _ownSimSeeded = false; _ownSimHullId = -1; _ownSimLastSectorId = -1;
        MaxShipPassMilliseconds = 0;
        _ownSim.Freeze();
        _ownServerClock.Reset();
        _ownClockConnectionGeneration = -1; _ownClockInitialPingSent = false; _shipSendCadence.Reset();
        session.World.LifepodHullResolver = ResolveLifepodHull;
        session.World.RemoteShipHullResolver = ResolveRemoteHullStats;
    }
    private bool ActionDown(KeyboardState keys, string action) => KeyBindingStore.Instance.Map.IsDownMostSpecific(keys, action);
    public void FlightKeys(string[] codes)
    {
        _flightKeys = new KeyboardState(codes.Select(ParseKey).Where(k => k != Keys.None).ToArray());
    }
    private static Keys ParseKey(string code)
    {
        string name = code.StartsWith("Key", StringComparison.Ordinal) ? code[3..]
            : code.StartsWith("Digit", StringComparison.Ordinal) ? "D" + code[5..]
            : code.StartsWith("Arrow", StringComparison.Ordinal) ? code[5..]
            : code switch { "Backspace" => "Back", "ShiftLeft" => "LeftShift", "ShiftRight" => "RightShift", "ControlLeft" => "LeftControl", "ControlRight" => "RightControl", "AltLeft" => "LeftAlt", "AltRight" => "RightAlt", "Equal" => "OemPlus", "Minus" => "OemMinus", "Backquote" => "OemTilde", "Backslash" => "OemPipe", "IntlBackslash" => "OemBackslash", "BracketLeft" => "OemOpenBrackets", "BracketRight" => "OemCloseBrackets", _ => code };
        return Enum.TryParse<Keys>(name, out var key) ? key : Keys.None;
    }
    public void PointerMotion(int dx, int dy) { if (_pointerLocked) { _pointerDx += dx; _pointerDy += dy; } }
    public void PointerCapture(bool locked)
    {
        _pointerLocked = locked; _pointerDx = _pointerDy = 0;
        _mouseFlightEnabled = locked;
        if (locked) _mouseFlight.Enable(); else _mouseFlight.Reset();
    }
    public void ReleaseFlightInput()
    {
        _browserPad = null; _gamePadFlight.Reset(); _padRbWasDown = _padLbWasDown = false;
        _flightKeys = _lastFlightKeys = default;
        _throttle = -1; _mouseButtons = 0; _browserAutoTarget = null;
        PointerCapture(false);
        if (_flightSession is not null && Flying)
        {
            // Zero-duration physics steps divide by dt in the native turn law.
            // Publish neutral controls directly; the next ordinary frame steps them.
            if (_ownSim.HaveHull && !_ownSim.Frozen && _ownServerClock.Ready)
            {
                var payload = ClientShipUpdate.FromLocalPose(_ownServerClock,
                    _ownServerClock.ClientTimeSeconds(ClientNowMilliseconds), _ownSim.Position, _ownSim.Velocity,
                    _ownSim.Orientation, 0, ControlData.Neutral, _ownSim.TurnRates, _ownSim.Power);
                _session.SendShipPose(payload.Time, _ownShipCookie, payload);
            }
            _shipSendCadence.Reset();
        }
    }
    private void UpdateOnlineFlight(GameTime time)
    {
        if (_flightSession is null || time.ElapsedGameTime <= TimeSpan.Zero) return;
        _flightElapsedSeconds = time.ElapsedGameTime.TotalSeconds;
        _nowSeconds += _flightElapsedSeconds;
        _sceneGameSeconds += _flightElapsedSeconds * _session.World.TimeScale;
        PumpServerClock();
        var world = _session.World;
        if (world.MyShipId is not { } id || !world.Ships.TryGetValue(id, out var own) || own.Latest is not { } up) return;
        UpdateOwnShipSimulation(world, own, up);
        // P1067 — Watch needs a live flying ship: a docked hangar screen or a
        // death view is not b-roll, so the mode ends with the flight.
        if (Watch.Active && (!Flying || own.IsDead))
        {
            Watch.Stop();
            Console.WriteLine("[browser] watch OFF (not flying)");
        }
        if (!Flying) { _browserAutoTarget = null; return; }
        if (_browserAutoTarget is { Kind: TargetKind.Warp } warpTarget && world.Warps.TryGetValue(warpTarget.Id, out var oldWarp) && oldWarp.ClusterId != own.SectorId)
            _browserAutoTarget = null;
        var keys = _flightKeys;
        bool Edge(string action) => ActionDown(keys, action) && !ActionDown(_lastFlightKeys, action);
        // P1067 — desktop F2 (action "Director", Game1.Director.cs:514-523).
        if (Edge("Director")) ToggleWatch();
        // P1067 — any flight control returns the camera (Game1.Director.cs:557-571).
        if (Watch.CancelOnFlightInput(keys))
        {
            _cameraMode = CameraMode.Chase;
            Console.WriteLine("[browser] watch cancelled by flight input -> chase");
        }
        if (Edge("ThrottleUp")) _throttle = FlightInput.StepThrottleUp(_throttle);
        if (Edge("ThrottleDown")) _throttle = FlightInput.StepThrottleDown(_throttle);
        if (Edge("ThrottleZero")) _throttle = -1;
        if (Edge("ThrottleFull")) _throttle = 1;
        if (Edge("ToggleStarmap")) ToggleStarmap();
        if (Edge("ToggleGroupFire")) _groupFire = !_groupFire;
        // Same hull-limited shared cycling as desktop (clintlib.cpp:1935-1975).
        if (Edge("PrevWeapon")) _selectedMount = FlightInput.PrevMount(_selectedMount, FixedMountCount());
        if (Edge("NextWeapon")) _selectedMount = FlightInput.NextMount(_selectedMount, FixedMountCount());
        if (Edge("ToggleCloak")) _cloakToggled = !_cloakToggled;
        if (Edge("LockVector")) _coastToggled = !_coastToggled;
        var input = SampleManualFlightInput(keys, ActionDown(keys, "ThrustUp"), ActionDown(keys, "FireBooster"));
        input = input with { Fire = input.Fire || (_pointerLocked && (_mouseButtons & 1) != 0) };
        var command = FlightInput.Translate(input);
        if (_pointerLocked)
        {
            var deflection = _mouseFlight.Update(_pointerDx, _pointerDy);
            var (yaw, pitch) = MouseFlightController.MapResponse(deflection, _mouseFlight.Deadzone, _mouseFlight.LinearControls);
            // Same native TK_RollModifier routing as desktop (WinTrek.cpp:10828-10834).
            command = command with { Controls = command.Controls with
                { Yaw = input.RollModifier ? 0 : yaw, Pitch = pitch, Roll = input.RollModifier ? yaw : command.Controls.Roll } };
        }
        if (_browserPad is not null) command = ApplyGamePadFlight(command);
        _pointerDx = _pointerDy = 0;
        bool padTurnActive = _browserPad is { } pad &&
            (Math.Abs(pad.ThumbSticks.Left.X) > JoystickFlightController.DeadZone ||
             Math.Abs(pad.ThumbSticks.Right.X) > JoystickFlightController.DeadZone ||
             Math.Abs(pad.ThumbSticks.Right.Y) > JoystickFlightController.DeadZone);
        if (padTurnActive || input.ThrottleForward || input.ThrottleBack || input.StrafeLeft || input.StrafeRight || input.PitchUp || input.PitchDown || input.YawLeft || input.YawRight || input.RollLeft || input.RollRight
            || (_pointerLocked && _mouseFlight.DeflectionMovedPast(_browserAutoMouseBaseline, MouseFlightController.AutopilotDisengageThreshold)))
            _browserAutoTarget = null;
        if (_browserAutoTarget is { } target)
        {
            _autopilotDeltaSeconds = (float)_flightElapsedSeconds;
            _autopilotMovementButtons = 0;
            if (target.Kind == TargetKind.Station && TryComputeDockSteer(target, _ownSim.Position, _ownSim.Orientation, _ownSim.Velocity, out var dockControls))
                command = new(dockControls, _autopilotMovementButtons);
            else
            {
                SimVec? targetPosition = null; short? sector = null;
                if (target.Kind == TargetKind.Station && world.Stations.TryGetValue(target.Id, out var station)) { targetPosition = Vec(station.Position); sector = station.ClusterId; }
                if (target.Kind == TargetKind.Probe && world.Probes.TryGetValue(target.Id, out var probe)) { targetPosition = probe.Position; sector = probe.ClusterId; }
                if (target.Kind == TargetKind.Warp && world.Warps.TryGetValue(target.Id, out var warp)) { targetPosition = Vec(warp.Position); sector = warp.ClusterId; }
                if (target.Kind == TargetKind.Buoy && world.CommandBuoys.TryGetValue(target.Id, out var buoy)) { targetPosition = Vec(buoy.Position); sector = buoy.ClusterId; }
                var warps = world.Warps.Values.Where(w => world.Warps.ContainsKey(w.DestinationId)).Select(w => new StarmapGotoRouter.AllWarp(new System.Numerics.Vector3(w.Position[0], w.Position[1], w.Position[2]), w.ClusterId, world.Warps[w.DestinationId].ClusterId)).ToArray();
                if (TryResolveAutopilotGoal(own.SectorId, sector, targetPosition, warps, out var goal, out bool routed))
                    command = new(routed || target.Kind == TargetKind.Warp ? AutopilotSteering.Pursue(_ownSim.Position, _ownSim.Orientation, goal) : AutopilotSteering.SteerToward(_ownSim.Position, _ownSim.Orientation, goal, _ownSim.Velocity), 0);
                if (target.Kind is TargetKind.Warp or TargetKind.Buoy && _shopCatalog?.Core is { } core && core.HullTypes.TryGetValue(own.HullId, out var hull) &&
                    ClientAutopilotDodge.TryComputeDodgeGoal(world, id, own.SideId, _ownSim.Position, _ownSim.Velocity,
                        new(hull.Length, hull.Thrust, hull.Mass), true,
                        hullId => core.HullTypes.TryGetValue(hullId, out var other) ? other.Length : null,
                        stationId => core.StationTypes.TryGetValue(stationId, out var stationType) ? stationType.Radius : null,
                        null, out var dodgeGoal))
                    command = new(AutopilotSteering.Pursue(_ownSim.Position, _ownSim.Orientation, dodgeGoal), 0);
            }
        }
        _ownShipSendArmed = true;
        StepOwnShipSimulation(_flightElapsedSeconds, command.Controls, command.StateM);
        MaybeSendOwnShipUpdate();
        // P1069 — the frame's hull decode/upload budget runs in the update path,
        // never on the draw path; a hull observed last draw is uploaded here and
        // replaces its stand-in in this frame's ship pass.
        _hullUploads?.Step(ClientNowMilliseconds);
        _ = RocksInSector(world, own.SectorId); // Register discoveries only when the spatial set changes.
        _browserSpins.Advance(own.SectorId, _flightElapsedSeconds * world.TimeScale);
        _lastFlightKeys = keys;
    }
    /// <summary>P1069 — decode a hull's original mesh and upload its GPU
    /// buffers/texture (the exact NativeShipRenderer calls the draw path already
    /// caches). Runs off the draw path, one hull per frame. Returns false when
    /// the model is unreadable; the ship then keeps its stand-in.</summary>
    private bool UploadHull(short hullId)
    {
        if (_battleScene?.Hull(hullId) is not { } hull || string.IsNullOrEmpty(hull.ModelName)) return false;
        if (_flightShips!.MeshFor(hull.ModelName) is null) return false;
        if (_flightShips.TextureStemFor(hull.ModelName, hull.TextureName) is { } texture)
            _flightShips.TextureFor(texture);
        return true;
    }

    /// <summary>P1069 — the native aft-nozzle world offset (P566) for a hull whose
    /// mesh is uploaded. Null for a stand-in or a model with no authored
    /// rocket/thrust/smoke frame → the shared radius-based aft-centre anchor.</summary>
    private SimVec? EngineAftOffset(short hullId, DataHullTypeIGC? hull, Orientation orientation)
    {
        if (hull is not { } known || string.IsNullOrEmpty(known.ModelName) || _hullUploads?.Uploaded(hullId) != true) return null;
        var geo = _flightShips!.MeshFor(known.ModelName);
        if (geo is null) return null;
        float scale = NativeShipResolver.NativeScaleForLength(known.Length, geo.MeshRadius);
        return NativeScene.AftWorldOffset(_flightShips.FramesFor(known.ModelName), scale, orientation);
    }

    private void LoadFlightRenderers()
    {
        if (_flightLoaded) return;
        _flightShips = new NativeShipRenderer(GraphicsDevice, _assets) { HdTexturesEnabled = false };
        // P1069 — decode/upload a newly promoted hull's mesh off the draw path,
        // one hull per frame; the ship keeps its stand-in until then.
        _hullUploads = new BrowserHullUploads(
            hullId => _battleScene?.MeshBytesReady(hullId, (Allegiance.ClientCore.Browser.Assets.StagedAssetSource)_assets) == true,
            UploadHull);
        _flightStations = new NativeStationRenderer(GraphicsDevice, _assets);
        _flightRocks = new NativeAsteroidRenderer(GraphicsDevice, _assets);

        _flightBolts = new ProjectileRenderer(GraphicsDevice);
        _flightWarps = new WarpRenderer(GraphicsDevice, _assets);
        _flightDocked = new DockedScreenRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig);
        var gauges = GaugeTextures.Load(GraphicsDevice, _assets);
        var nativeFonts = new NativeFonts(Enum.GetValues<NativeFont>().ToDictionary(font => font,
            font => (Content.Load<SpriteFont>(NativeFontCatalog.Spec(font).Asset1x), Content.Load<SpriteFont>(NativeFontCatalog.Spec(font).Asset2x))));
        var locks = LockTextures.Load(GraphicsDevice, _assets);
        var status = StatusTextures.Load(GraphicsDevice, _assets);
        var indicators = IndicatorTextures.Load(GraphicsDevice, _assets);
        var target = TargetPaneTextures.Load(GraphicsDevice, _assets);
        var inventory = InventoryPaneTextures.Load(GraphicsDevice, _assets);
        var nativeHud = HudNativeTextures.Load(GraphicsDevice, _assets);
        _classicFlightHud = new HudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig, gauges, locks, status, indicators, target, inventory,
            damageWash: _hudSettings.DamageWash, nativeHudTextures: nativeHud, gaugePercentage: _hudSettings.GaugePercentage,
            nativeFonts: nativeFonts, selectionBracket: _hudSettings.SelectionBracketOn, joystickMesh: JoystickMeshTexture.Create(GraphicsDevice));
        _modernFlightHud = new ModernHudRenderer(_spriteBatch, _whitePixel, _hudSmall, _hudBig, _hudSettings,
            Content.Load<SpriteFont>("HUDModern"), nativeArt: status, nativeHudTextures: nativeHud);
        _flightLoaded = true;
    }
    private static Vector3 V(SimVec value) => new(value.X, value.Y, value.Z);
    private static Vector3 V(float[] value) => new(value[0], value[1], value[2]);
    /// <summary>P1067 — advance the Watch director for this frame and return the
    /// camera that replaces the chase view. Mirrors the desktop
    /// <c>UpdateDirectorPose</c> (Game1.Director.cs:363-374): a gentle free
    /// orientation drift, the browser snapshot, then the shared pure planner.</summary>
    private (Matrix View, Matrix Proj, Vector3 Eye) UpdateWatchPose(Vector3 position, float aspect, float dt, float hullLength)
    {
        var snapshot = Watch.BuildSnapshot(_session.World, position);
        var (view, proj, eye, _) = Watch.Update(dt, snapshot, hullLength * .5f, hullLength, aspect);
        return (view, proj, eye);
    }
    private void DrawOnlineFlight()
    {
        LoadFlightRenderers();
        var world = _session.World;
        if (world.MyShipId is not { } id || !world.Ships.TryGetValue(id, out var own) || own.Latest is not { } up) return;
        if (own.StationId >= 0)
        {
            // P1070 — the docked screen is the desktop hangar / loadout screen inside
            // the game window (the browser copy of Game1.Hangar.cs/Game1.LoadoutV2.cs).
            DrawBrowserHangar();
            return;
        }
        // Undocked: the hangar model only updates from DrawBrowserHangar, so leave the dock
        // here. Without this a launched pilot kept the old shop, and the next dock (respawn,
        // re-dock) skipped EnterDock and showed that stale shop (desktop: Game1.Panes.cs:127-206
        // builds a fresh shop on every dock).
        if (_hangarField is { Active: true } leftDock) leftDock.Reset();
        var orientation = _ownSim.Orientation;
        var position = V(_ownSim.Position);
        var forward = V(orientation.GetForward()); var above = V(orientation.GetUp());
        var ownHull = _battleScene?.Hull(own.HullId);
        float length = ownHull?.Length ?? 1;
        var (view, projection) = _browserChase.Matrices(position, PoseConverter.ToXnaRotation(orientation),
            GraphicsDevice.Viewport.AspectRatio, (float)_flightElapsedSeconds, out var eye, shipRadius: length * .5f);
        if (Watch.Active)
        {
            (view, projection, eye) = UpdateWatchPose(position, GraphicsDevice.Viewport.AspectRatio, (float)_flightElapsedSeconds, length);
        }
        else if (_cameraMode == CameraMode.ExternalFlyBy)
        {
            var viewContacts = world.ShipsInSector(own.SectorId).Where(s => s.ShipId != id && s.CanDrawInSector(own.SectorId) && s.StationId < 0)
                .OrderBy(s => (s.Latest!.Position - _ownSim.Position).LengthSquared()).Take(1).ToArray();
            if (viewContacts.Length != 0)
            {
                var camera = ExternalCameras.FlyBy(V(viewContacts[0].Latest!.Position), position,
                    ContactViewBackwards[_contactViewAngle], length * .5f, 0);
                eye = camera.Eye; view = camera.View();
            }
        }
        if (_viewRock && !Watch.Active)
        {
            var rocks = RocksInSector(world, own.SectorId)
                .OrderBy(r => (Vec(r.Position) - _ownSim.Position).LengthSquared()).Take(1).ToArray();
            if (rocks.Length != 0)
            {
                var rock = rocks[0];
                var camera = ExternalCameras.Target(V(rock.Position), ContactViewBackwards[_contactViewAngle], rock.Radius, 0);
                eye = camera.Eye; view = camera.View();
            }
        }
        _rockFrustum.Matrix = view * projection;
        BeginScene(world, own, view, projection, eye);
        long shipPassStart = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawnShips = DrawnMeshes = DrawnStandIns = 0;
        _visibleShipNames.Clear(); _flightContacts.Clear();
        _battleScene!.Gather(world, own.SectorId, RequestBattlePack);
        SectorShipCount = world.ShipsInSector(own.SectorId).Count;
        long nowMs = ClientNowMilliseconds;
        foreach (var ship in _battleScene.Ships)
        {
            var pose = ship.Latest!;
            var hull = _battleScene.Hull(ship.HullId);
            var p = ship.ShipId == id ? _ownSim.Position : pose.Position;
            var o = ship.ShipId == id ? orientation : pose.Orientation;
            if (ship.ShipId != id && world.TryGetRenderPose(ship.ShipId, world.NowSeconds(), out var remotePosition, out var remoteOrientation))
            { p = remotePosition; o = remoteOrientation; }
            // Contact first: asset failure must never suppress a visible ship.
            var site = RadarProjection.Project(V(p), eye, view, projection, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height, 12, _hudSettings.RoundRadar);
            HudColor? sideColor = world.TryGetSideColor(ship.SideId, out float red, out float green, out float blue) ? new HudColor(red, green, blue) : null;
            _flightContacts.Add(new(HudContactKind.Ship, new(site.Point.X, site.Point.Y), site.Placement == RadarBracketLayout.RadarPlacement.OnScreen,
                1, sideColor, false, false, Name: ship.CharacterName, Range: (int)((p - _ownSim.Position).Length() + .5f),
                Shield: pose.ShieldFraction, Hull: pose.HullFraction, IsSelf: ship.ShipId == id, IsSameSide: ship.SideId == own.SideId,
                IsEnemy: ship.SideId != own.SideId && !world.AreAllied(own.SideId, ship.SideId)));
            var matrix = PoseConverter.ToXnaWorldRelative(V(p), o, eye);
            // P1069 — queue a newly promoted hull (once) and keep its stand-in
            // until the budgeted decode/upload has completed.
            _hullUploads!.Observe(ship.HullId, nowMs);
            // P1069 — the engine effects for every drawn roster ship, stand-ins
            // included: exhaust/glow/ribbon at the radius-based aft anchor until
            // the native mesh (and its authored nozzle frame) is resident.
            _sceneEffects.GatherEngines(world, ship, p, ship.ShipId == id ? _ownSim.Velocity : pose.Velocity,
                o, hull, (float)_flightElapsedSeconds, EngineAftOffset(ship.HullId, hull, o));
            if (_hullUploads.Uploaded(ship.HullId) && hull is { } h && _flightShips!.MeshFor(h.ModelName) is { } mesh)
            {
                string? texture = _flightShips.TextureStemFor(h.ModelName, h.TextureName);
                _flightShips.Draw(mesh, texture is null ? null : _flightShips.TextureFor(texture), matrix, view, projection,
                    NativeScene.Opacity(CloakVisual.IsCloaked(pose.StateM), _nowSeconds),
                    NativeShipResolver.NativeScaleForLength(h.Length, mesh.MeshRadius), _sceneLighting, h.ModelName, texture, false);
                DrawnMeshes++;
                _hullUploads.NoteDrawn(ship.HullId, nowMs);
            }
            else
            {
                float side = BattleShipScene.StandInSide(hull?.Length ?? 2);
                _flightBolts!.Draw(Matrix.CreateScale(side) * matrix,
                    sideColor is { } color ? new Vector3(color.R, color.G, color.B) : new Vector3(.6f, .8f, 1), view, projection,
                    NativeScene.Opacity(CloakVisual.IsCloaked(pose.StateM), _nowSeconds));
                DrawnStandIns++;
            }
            if (BattleShipScene.RockInView(V(p) - eye, (hull?.Length ?? 2) * .5f, _rockFrustum))
                _sceneEffects.GatherShip(world, ship, p, ship.ShipId == id ? _ownSim.Velocity : pose.Velocity,
                    o, hull, (float)_flightElapsedSeconds, nowMs, _flightWarps);
            DrawnShips++; _visibleShipNames.Add(ship.CharacterName ?? $"Ship {ship.ShipId}");
        }
        // P1069 — ribbons for ships that left the drawn roster vanish at once
        // (desktop _motionTrail.PruneUntouched after the pose gather).
        _sceneEffects.Motion.PruneUntouched();
        ShipPassMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(shipPassStart).TotalMilliseconds;
        if (ShipPassMilliseconds > MaxShipPassMilliseconds) MaxShipPassMilliseconds = ShipPassMilliseconds;
        foreach (var station in world.Stations.Values)
        {
            if (station.ClusterId != own.SectorId || _shopCatalog?.StationTypes?.GetValueOrDefault(station.StationTypeId) is not { } type) continue;
            if (_flightStations!.MeshFor(type.ModelName) is not { } mesh) continue;
            string? texture = _flightStations.TextureStemFor(type.ModelName, type.TextureName);
            var facing = _stationFacing; facing.Reset();
            if (station.Forward is { Length: 3 } sf && station.Up is { Length: 3 } su) facing.Set(Vec(sf), Vec(su));
            var matrix = Matrix.CreateWorld(V(station.Position) - eye, V(facing.GetForward()), V(facing.GetUp()));
            _flightStations.Draw(mesh, texture is null ? null : _flightStations.TextureFor(texture), matrix, view, projection,
                (type.Radius / Math.Max(.001f, mesh.MeshRadius)), _sceneLighting, type.ModelName, texture);
            if (BattleShipScene.RockInView(V(station.Position) - eye, type.Radius, _rockFrustum))
                NativeScene.AppendStationLights(_flightStations, type.ModelName, V(station.Position),
                    PoseConverter.ToXnaRotation(facing), type.Radius, NativeStationResolver.ScaleForRadius(type.Radius, mesh.MeshRadius),
                    eye, view, projection, GraphicsDevice.Viewport.Width, (float)_nowSeconds, _sceneEffects.Strobes);
        }
        _drawnRockSurfaces.Clear(); CulledRocks = 0;
        _rockFrustum.Matrix = view * projection;
        foreach (var rock in RocksInSector(world, own.SectorId))
        {
            if (!BattleShipScene.RockInView(V(rock.Position) - eye, rock.Radius, _rockFrustum)) { CulledRocks++; continue; }
            if (_flightRocks!.GeometryFor(rock.ModelName) is not { } mesh) continue;
            string? texture = _flightRocks.TextureStemFor(rock.ModelName, rock.TextureName);
            var rotation = _browserSpins.Rotation(rock.AsteroidId);
            var surface = texture is null ? null : _flightRocks.TextureFor(texture);
            if (texture is not null && !_drawnRockSurfaces.ContainsKey(texture)) _drawnRockSurfaces[texture] = [surface?.Width ?? 0, surface?.Height ?? 0];
            _flightRocks.Draw(mesh, surface, rotation * Matrix.CreateTranslation(V(rock.Position) - eye), view, projection,
                rock.Radius / Math.Max(.001f, mesh.MeshRadius), _sceneLighting, rock.ModelName, texture);
        }
        foreach (var warp in _sceneEffects.Warps)
        {
            var facing = _warpFacing; facing.Set(Vec(warp.Forward));
            var matrix = PoseConverter.ToXnaRotation(facing) * Matrix.CreateScale(warp.Radius * WarpRenderer.VisualScaleMultiplier)
                * Matrix.CreateTranslation(V(warp.Position) - eye);
            _flightWarps!.Draw(matrix, view, projection, _flightElapsedSeconds, warp.Radius * WarpRenderer.VisualScaleMultiplier, warp.TextureName, warp.WarpId, funnelBoost: 1f + (WarpRenderer.PeakFunnelBoost - 1f) * _sceneEffects.PulseStrength(warp.WarpId));
        }
        world.PruneExpiredProjectiles(ClientNowMilliseconds);
        foreach (var bolt in world.Projectiles)
        {
            _seenShotLaunchers.Add(bolt.LauncherId);
            var p = bolt.PositionAt(ClientNowMilliseconds);
            var direction = bolt.Velocity.LengthSquared() > 1e-6f ? bolt.Velocity.Normalize() : bolt.Forward;
            var style = Allegiance.Core.Visual.ProjectileStyle.For(bolt.ProjectileTypeId, _shopCatalog?.ProjectileTypes);
            var lengthBolt = Math.Clamp(bolt.Velocity.Length() * ProjectileVisibility.BoltLengthPerUnitSeconds, ProjectileVisibility.MinBoltLength, ProjectileVisibility.MaxBoltLength);
            var matrix = Matrix.CreateScale(style.Width, style.Width, lengthBolt) * Matrix.CreateWorld(V(p) - eye, V(direction), Vector3.Up);
            _flightBolts!.Draw(matrix, new(style.R, style.G, style.B), view, projection);
            _drawnProjectiles++;
        }
        DrawSceneExtras(world, own, view, projection, eye);
        float warpFlash = _sceneEffects.EndFrame(own, _ownSim.Position, _ownSim.Velocity, orientation, length * .5f);
        var frame = new HudFrame(up.HullFraction, up.ShieldFraction, up.AmmoFraction, up.FuelFraction, up.EnergyFraction,
            _ownSim.Power, _ownSim.Velocity.Length(), _throttle, (_ownSim.StateM & ShipControlState.AfterburnerButton) != 0,
            (_ownSim.StateM & ShipControlState.WeaponsMask) != 0, _groupFire, _selectedMount, false, warpFlash, false, _cloakToggled,
            world.KillBonusPercent, up.Orientation.GetRollDegrees(), [], false, 0, world.MyMoney, TurnYaw: _ownSim.Controls.Yaw, TurnPitch: _ownSim.Controls.Pitch, AutopilotEngaged: _browserAutoTarget is not null, WarningMask: world.ShipWarnings.Mask, TimeDilationPercent: (float)world.TimeScale * 100);
        var hud = _hudSettings.Style == HudStyle.Classic ? _classicFlightHud! : _modernFlightHud!;
        hud.Draw(HudModelAdapter.Create(frame, _cameraMode, contacts: _flightContacts, buffers: _flightHudBuffers,
            cameraLabel: Watch.Active ? DirectorWatch.HudLabel : null), GraphicsDevice.Viewport);
        DrawBrowserStarmap(own.SectorId);
        FlightFrames++;
    }
}
