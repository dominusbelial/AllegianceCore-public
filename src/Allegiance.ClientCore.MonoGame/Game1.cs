using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// The playable MonoGame client. Connects to a real <see cref="Allegiance.Server"/>
/// over the real <see cref="ConnectionSession"/> wire path, then every frame:
/// samples the keyboard → translates to a <see cref="FlightCommand"/> →
/// <see cref="ConnectionSession.CommandControls"/>; drives a
/// <see cref="RemoteShipPredictor"/> per remote (teammate) ship from the real
/// update stream; and draws every known ship (own via raw position, others via
/// their predicted pose) from a chase camera behind the local ship.
/// </summary>
public sealed class Game1 : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly ConnectionSession _session;
    private readonly string _playerName;

    private XnaModel _model = null!;
    private ShipRenderer _shipRenderer = null!;
    private ChaseCamera _chaseCamera = null!;

    // Server tick interval: 1 / ServerConfig.UpdatesPerSecond (default 20).
    private const double UpdateIntervalSeconds = 1.0 / 20.0;

    // Monotonic render clock feeding the predictors.
    private double _nowSeconds;
    private readonly Dictionary<short, RemoteShipPredictor> _remotes = new();
    private readonly Dictionary<short, uint> _remoteLastTick = new();
    private readonly Dictionary<short, Flight.PredictedPose> _poses = new();

    // Minimal auto-start so the POC can actually fly (see Update): ready-up and
    // launch are one-shot, sent once the prerequisites are met.
    private bool _autoReadySent;
    private bool _autoLaunchSent;

    public Game1(ushort port, string playerName)
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 1280,
            PreferredBackBufferHeight = 720,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        _session = new ConnectionSession();
        _playerName = playerName;
        _port = port;
        IsMouseVisible = true;
        Window.Title = $"Allegiance ClientCore MonoGame ({playerName})";
        Content.RootDirectory = "Content";

        // The console may be redirected/absent on some hosts; keep failures loud.
        try { Window.AllowUserResizing = true; } catch { /* headless/unsupported */ }
    }

    private readonly ushort _port;

    protected override void Initialize()
    {
        base.Initialize();

        Console.WriteLine($"[monogame] connecting to 127.0.0.1:{_port} as '{_playerName}' ...");
        try
        {
            _session.Connect(_port, _playerName);
            _session.SendLogonRequest(Request(_playerName));
            var ack = _session.WaitForAck(5000);
            Console.WriteLine($"[monogame] logged on: serverTime={ack.TimeServer}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] FAILED to connect/log on: {ex.Message}");
            Exit();
        }
    }

    protected override void LoadContent()
    {
        base.LoadContent();
        try
        {
            _model = Content.Load<XnaModel>("Models/bios_scout_upscaled");
            _shipRenderer = new ShipRenderer(_model);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] FAILED to load ship model: {ex.Message}");
            Exit();
            return;
        }
        // near/far: with the camera-relative fix (ChaseCamera.Matrices), the
        // view-space distances involved are small (a nearby ship, not the
        // sim's absolute world coordinates), so a modest far plane is enough
        // — keeps the near:far ratio (and therefore depth-buffer precision)
        // healthy. distanceBehind/heightAbove are tuned so the whole ship
        // (its true rendered size after the FBX's baked ParentBone.Transform
        // scale — see Content.mgcb's Scale=1, not 20; the bone transform
        // already supplies the needed ~100x) fits in frame.
        _chaseCamera = new ChaseCamera(distanceBehind: 350f, heightAbove: 90f, nearPlane: 10f, farPlane: 3000f);
    }

    protected override void Update(GameTime gameTime)
    {
        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed
            || Keyboard.GetState().IsKeyDown(XnaKeys.Escape))
            Exit();

        _nowSeconds += gameTime.ElapsedGameTime.TotalSeconds;

        // Sample the keyboard and translate (pure logic) into a control command.
        var ks = Keyboard.GetState();
        var input = new FlightInputState(
            ThrottleForward: ks.IsKeyDown(XnaKeys.W),
            ThrottleBack:    ks.IsKeyDown(XnaKeys.S),
            StrafeLeft:      ks.IsKeyDown(XnaKeys.Q),
            StrafeRight:     ks.IsKeyDown(XnaKeys.E),
            PitchUp:         ks.IsKeyDown(XnaKeys.Up),
            PitchDown:       ks.IsKeyDown(XnaKeys.Down),
            YawLeft:         ks.IsKeyDown(XnaKeys.Left),
            YawRight:        ks.IsKeyDown(XnaKeys.Right),
            RollLeft:        ks.IsKeyDown(XnaKeys.A),
            RollRight:       ks.IsKeyDown(XnaKeys.D));
        var cmd = FlightInput.Translate(input);
        _session.CommandControls(cmd.Controls, cmd.StateM);

        // Recompute predicted poses for every remote (non-self) ship, feeding
        // each newly-arrived real update into its predictor.
        _poses.Clear();
        var world = _session.World;
        if (world.MyShipId is { } me)
        {
            // Own ship renders at its raw last-known position (no smoothing).
            if (world.Ships.TryGetValue(me, out var mine) && mine.Latest is { } mineUp)
                _poses[me] = new Flight.PredictedPose(mineUp.Position, mineUp.Orientation);
        }
        foreach (var (id, ship) in world.Ships)
        {
            if (id == world.MyShipId || ship.Latest is not { } up) continue;

            if (!_remotes.TryGetValue(id, out var pred))
            {
                pred = new RemoteShipPredictor(UpdateIntervalSeconds);
                _remotes[id] = pred;
            }
            if (!_remoteLastTick.TryGetValue(id, out uint lastTick) || lastTick != ship.LastUpdateTick)
            {
                pred.Observe(up, _nowSeconds);
                _remoteLastTick[id] = ship.LastUpdateTick;
            }
            if (pred.Predict(_nowSeconds) is { } pose)
                _poses[id] = pose;
        }

        // Minimal auto-start so the POC is actually playable. The server only
        // begins a mission once every OCCUPIED side is aggregate-ready (P180
        // TryAutoStart), and the local ship only flies once it has undocked
        // into a sector (the server's PerformUndock, gated on
        // FM_C_DOCKED stationID=NA). Both are things a real client does
        // through lobby/hangar buttons this POC deliberately omits, so we do
        // the minimum to reach flight: ready-up once we have a ship, then
        // undock once the mission is STAGE_STARTED. One-shot each (guard
        // flags above).
        if (world.MyShipId is { } pid)
        {
            if (!_autoReadySent)
            {
                _session.SendPlayerReady(true, pid);
                _autoReadySent = true;
                Console.WriteLine("[monogame] auto-ready sent");
            }
            if (!_autoLaunchSent
                && world.Mission is { } mi && mi.Stage == (int)MissionStage.Started)
            {
                _session.SendDocked();
                _autoLaunchSent = true;
                Console.WriteLine("[monogame] auto-undock sent");
            }
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Color.Black);

        var world = _session.World;
        if (world.MyShipId is { } me && _poses.TryGetValue(me, out var myPose))
        {
            var myPos = PoseConverter.ToXna(myPose.Position);
            var myRot = PoseConverter.ToXnaRotation(myPose.Orientation);
            var aspect = GraphicsDevice.Viewport.AspectRatio;
            // Camera-relative rendering (P207 fix — see ChaseCamera.Matrices'
            // remarks): the view matrix always looks from the origin, and every
            // ship's World matrix uses its position MINUS cameraEye, never its
            // true absolute sim position. Required on this GPU/driver — using
            // true absolute world coordinates (routinely in the hundreds to
            // thousands here) produced a uniformly blank frame despite every
            // matrix being independently verified correct.
            var (view, proj) = _chaseCamera.Matrices(myPos, myRot, aspect, out var cameraEye);

            foreach (var (id, pose) in _poses)
            {
                var xnaPos = PoseConverter.ToXna(pose.Position);
                var worldMat = PoseConverter.ToXnaWorldRelative(xnaPos, pose.Orientation, cameraEye);
                _shipRenderer.Draw(worldMat, view, proj);
            }
        }

        base.Draw(gameTime);
    }

    protected override void UnloadContent()
    {
        _session.Dispose();
        base.UnloadContent();
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name,
        ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 1234u,
        DwCookie = 0x33Fu,
        CrcFileList = -1,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 70000000000000000ul,
        DrmHash = "monogame",
    };
}
