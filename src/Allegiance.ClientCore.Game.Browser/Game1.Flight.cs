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
    public void ToggleContactView() => _cameraMode = _cameraMode == CameraMode.Chase ? CameraMode.ExternalFlyBy : CameraMode.Chase;
    public bool ViewingContact => _cameraMode == CameraMode.ExternalFlyBy;
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
    private NativeSkyRenderer? _flightSky;
    private short _skySector = -1;
    private bool _flightLoaded;
    private readonly bool _ownShipPrediction = true;
    private float _autopilotDeltaSeconds;
    private int _autopilotMovementButtons;
    private AutopilotTarget? _browserAutoTarget;
    private MouseDeflection _browserAutoMouseBaseline;
    private ProjectileRenderer? _flightBolts;
    private WarpRenderer? _flightWarps;
    private readonly Dictionary<short, AsteroidSpin> _browserSpins = new();
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
    public string[] VisibleShipNames { get; private set; } = [];
    public object FlightMetrics => new {
        viewport = new { width = GraphicsDevice.Viewport.Width, height = GraphicsDevice.Viewport.Height },
        controls = new { yaw = _ownSim.Controls.Yaw, pitch = _ownSim.Controls.Pitch, throttle = _ownSim.Controls.Throttle, stateM = _ownSim.StateM },
        authoritative = _flightSession?.World.MyShipId is { } ownId && _flightSession.World.Ships.TryGetValue(ownId, out var ownShip) ? ownShip.Latest?.Position.ToString() : null, flying = Flying, frames = FlightFrames, drawnShips = DrawnShips,
        ships = VisibleShipNames, appliedResets = AppliedResets, position = _ownSim.Position.ToString(),
        selectedMount = _selectedMount, fixedMountCount = _flightSession is null ? 0 : FixedMountCount(),
        shotLaunchers = _seenShotLaunchers.ToArray(), drawnProjectiles = _drawnProjectiles, receivedProjectiles = _flightSession?.World.Diag.TotalReceived, autopilot = _browserAutoTarget?.Name, clockReady = _ownServerClock.Ready, seeded = _ownSimSeeded, pointerLocked = _pointerLocked };

    public void AttachOnlineSession(ConnectionSession? session)
    {
        ReleaseFlightInput();
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
        _flightSession = session;
        if (session is null) return;
        _shopCatalog ??= ShopCatalog.TryLoad(_assets);
        _ownSimSeeded = false; _ownSimHullId = -1; _ownSimLastSectorId = -1;
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
        PumpServerClock();
        var world = _session.World;
        if (world.MyShipId is not { } id || !world.Ships.TryGetValue(id, out var own) || own.Latest is not { } up) return;
        UpdateOwnShipSimulation(world, own, up);
        if (!Flying) { _browserAutoTarget = null; return; }
        var keys = _flightKeys;
        bool Edge(string action) => ActionDown(keys, action) && !ActionDown(_lastFlightKeys, action);
        if (Edge("ThrottleUp")) _throttle = FlightInput.StepThrottleUp(_throttle);
        if (Edge("ThrottleDown")) _throttle = FlightInput.StepThrottleDown(_throttle);
        if (Edge("ThrottleZero")) _throttle = -1;
        if (Edge("ThrottleFull")) _throttle = 1;
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
                var warps = world.Warps.Values.Where(w => world.Warps.ContainsKey(w.DestinationId)).Select(w => new StarmapGotoRouter.AllWarp(new System.Numerics.Vector3(w.Position[0], w.Position[1], w.Position[2]), w.ClusterId, world.Warps[w.DestinationId].ClusterId)).ToArray();
                if (TryResolveAutopilotGoal(own.SectorId, sector, targetPosition, warps, out var goal, out bool routed))
                    command = new(routed ? AutopilotSteering.Pursue(_ownSim.Position, _ownSim.Orientation, goal) : AutopilotSteering.SteerToward(_ownSim.Position, _ownSim.Orientation, goal, _ownSim.Velocity), 0);
            }
        }
        _ownShipSendArmed = true;
        StepOwnShipSimulation(_flightElapsedSeconds, command.Controls, command.StateM);
        MaybeSendOwnShipUpdate();
        foreach (var rock in world.Asteroids.Values.Where(a => a.ClusterId == own.SectorId))
        {
            if (!_browserSpins.TryGetValue(rock.AsteroidId, out var spin)) _browserSpins[rock.AsteroidId] = spin = AsteroidSpin.FromWire(rock.Rotation);
            spin.Advance((float)(_flightElapsedSeconds * world.TimeScale));
        }
        _lastFlightKeys = keys;
    }
    private void LoadFlightRenderers()
    {
        if (_flightLoaded) return;
        _flightShips = new NativeShipRenderer(GraphicsDevice, _assets) { HdTexturesEnabled = false };
        _flightStations = new NativeStationRenderer(GraphicsDevice, _assets);
        _flightRocks = new NativeAsteroidRenderer(GraphicsDevice, _assets);
        _flightSky = new NativeSkyRenderer(GraphicsDevice, _assets);
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
    private void DrawOnlineFlight()
    {
        LoadFlightRenderers();
        var world = _session.World;
        if (world.MyShipId is not { } id || !world.Ships.TryGetValue(id, out var own) || own.Latest is not { } up) return;
        if (own.StationId >= 0)
        {
            _flightDocked!.Draw(GraphicsDevice.Viewport, [new(ShopLayout.Kind.Title, "HANGAR"), new(ShopLayout.Kind.Section, own.CharacterName),
                new(ShopLayout.Kind.Info, "Station " + own.StationId), new(ShopLayout.Kind.Hint, "Use Ready and launch to fly")], money: world.MyMoney);
            return;
        }
        var orientation = _ownSim.Orientation;
        var position = V(_ownSim.Position);
        var forward = V(orientation.GetForward()); var above = V(orientation.GetUp());
        var ownHull = _shopCatalog?.Hulls.FirstOrDefault(h => h.HullId == own.HullId);
        float length = ownHull?.Length ?? 1;
        var (view, projection) = _browserChase.Matrices(position, PoseConverter.ToXnaRotation(orientation),
            GraphicsDevice.Viewport.AspectRatio, (float)_flightElapsedSeconds, out var eye, shipRadius: length * .5f);
        if (_cameraMode == CameraMode.ExternalFlyBy)
        {
            var viewContacts = world.Ships.Values.Where(s => s.ShipId != id && s.SectorId == own.SectorId && s.StationId < 0 && !s.IsNotVisible && s.Latest is not null)
                .OrderBy(s => (s.Latest!.Position - _ownSim.Position).LengthSquared()).Take(1).ToArray();
            if (viewContacts.Length != 0)
            {
                var camera = ExternalCameras.FlyBy(V(viewContacts[0].Latest!.Position), position,
                    ContactViewBackwards[_contactViewAngle], length * .5f, 0);
                eye = camera.Eye; view = camera.View();
            }
        }
        if (world.Clusters.TryGetValue(own.SectorId, out var cluster) && _skySector != own.SectorId)
        { _flightSky!.SetCluster(cluster); _skySector = own.SectorId; }
        _flightSky!.Draw(view, projection, eye);
        DrawnShips = 0;
        var names = new List<string>();
        var contacts = new List<HudContact>();
        foreach (var ship in world.Ships.Values)
        {
            if (ship.SectorId != own.SectorId || ship.StationId >= 0 || ship.IsNotVisible || ship.Latest is not { } pose) continue;
            var hull = _shopCatalog?.Hulls.FirstOrDefault(h => h.HullId == ship.HullId);
            if (hull is not { } h || string.IsNullOrEmpty(h.ModelName)) continue;
            if (_flightShips!.MeshFor(h.ModelName) is not { } mesh) continue;
            var p = ship.ShipId == id ? _ownSim.Position : pose.Position;
            var o = ship.ShipId == id ? orientation : pose.Orientation;
            if (ship.ShipId != id && world.TryGetRenderPose(ship.ShipId, world.NowSeconds(), out var remotePosition, out var remoteOrientation))
            { p = remotePosition; o = remoteOrientation; }
            string? texture = _flightShips.TextureStemFor(h.ModelName, h.TextureName);
            var matrix = PoseConverter.ToXnaWorldRelative(V(p), o, eye);
            _flightShips.Draw(mesh, texture is null ? null : _flightShips.TextureFor(texture), matrix, view, projection,
                1, NativeShipResolver.NativeScaleForLength(h.Length, mesh.MeshRadius), null, h.ModelName, texture, false);
            DrawnShips++; names.Add(ship.CharacterName ?? $"Ship {ship.ShipId}");
            var site = RadarProjection.Project(V(p), eye, view, projection, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height, 12, _hudSettings.RoundRadar);
            HudColor? sideColor = world.TryGetSideColor(ship.SideId, out float red, out float green, out float blue) ? new HudColor(red, green, blue) : null;
            contacts.Add(new(HudContactKind.Ship, new(site.Point.X, site.Point.Y), site.Placement == RadarBracketLayout.RadarPlacement.OnScreen,
                1, sideColor, false, false, Name: ship.CharacterName, Range: (int)((p - _ownSim.Position).Length() + .5f),
                Shield: pose.ShieldFraction, Hull: pose.HullFraction, IsSelf: ship.ShipId == id, IsSameSide: ship.SideId == own.SideId,
                IsEnemy: ship.SideId != own.SideId && !world.AreAllied(own.SideId, ship.SideId)));
        }
        VisibleShipNames = names.ToArray();
        foreach (var station in world.Stations.Values)
        {
            if (station.ClusterId != own.SectorId || _shopCatalog?.StationTypes?.GetValueOrDefault(station.StationTypeId) is not { } type) continue;
            if (_flightStations!.MeshFor(type.ModelName) is not { } mesh) continue;
            string? texture = _flightStations.TextureStemFor(type.ModelName, type.TextureName);
            var facing = new Orientation();
            if (station.Forward is { Length: 3 } sf && station.Up is { Length: 3 } su) facing.Set(Vec(sf), Vec(su));
            var matrix = Matrix.CreateWorld(V(station.Position) - eye, V(facing.GetForward()), V(facing.GetUp()));
            _flightStations.Draw(mesh, texture is null ? null : _flightStations.TextureFor(texture), matrix, view, projection,
                (type.Radius / Math.Max(.001f, mesh.MeshRadius)), null, type.ModelName, texture);
        }
        foreach (var rock in world.Asteroids.Values)
        {
            if (rock.ClusterId != own.SectorId) continue;
            if (_flightRocks!.GeometryFor(rock.ModelName) is not { } mesh) continue;
            string? texture = _flightRocks.TextureStemFor(rock.ModelName, rock.TextureName);
            var spin = _browserSpins.GetValueOrDefault(rock.AsteroidId);
            var rotation = spin is null ? Matrix.Identity : Matrix.CreateFromAxisAngle(V(spin.Axis), spin.Angle);
            _flightRocks.Draw(mesh, texture is null ? null : _flightRocks.TextureFor(texture), rotation * Matrix.CreateTranslation(V(rock.Position) - eye), view, projection,
                rock.Radius / Math.Max(.001f, mesh.MeshRadius), null, rock.ModelName, texture);
        }
        foreach (var warp in world.Warps.Values.Where(w => w.ClusterId == own.SectorId))
        {
            var facing = new Orientation(); facing.Set(Vec(warp.Forward));
            var matrix = PoseConverter.ToXnaRotation(facing) * Matrix.CreateScale(warp.Radius * WarpRenderer.VisualScaleMultiplier)
                * Matrix.CreateTranslation(V(warp.Position) - eye);
            _flightWarps!.Draw(matrix, view, projection, _flightElapsedSeconds, warp.Radius * WarpRenderer.VisualScaleMultiplier, warp.TextureName, warp.WarpId);
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
        var frame = new HudFrame(up.HullFraction, up.ShieldFraction, up.AmmoFraction, up.FuelFraction, up.EnergyFraction,
            _ownSim.Power, _ownSim.Velocity.Length(), _throttle, (_ownSim.StateM & ShipControlState.AfterburnerButton) != 0,
            (_ownSim.StateM & ShipControlState.WeaponsMask) != 0, _groupFire, _selectedMount, false, 0, false, _cloakToggled,
            world.KillBonusPercent, up.Orientation.GetRollDegrees(), [], false, 0, world.MyMoney, TurnYaw: _ownSim.Controls.Yaw, TurnPitch: _ownSim.Controls.Pitch, AutopilotEngaged: _browserAutoTarget is not null, WarningMask: world.ShipWarnings.Mask, TimeDilationPercent: (float)world.TimeScale * 100);
        var hud = _hudSettings.Style == HudStyle.Classic ? _classicFlightHud! : _modernFlightHud!;
        hud.Draw(HudModelAdapter.Create(frame, _cameraMode, contacts: contacts), GraphicsDevice.Viewport);
        FlightFrames++;
    }
}
