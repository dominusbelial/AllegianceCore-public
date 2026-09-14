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
/// update stream; and draws a static <see cref="Rendering.Starfield"/> plus
/// every known ship (own via raw position, others via their predicted pose)
/// from a chase camera behind the local ship.
/// </summary>
public sealed class Game1 : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly ConnectionSession _session;
    private readonly string _playerName;

    private XnaModel _model = null!;
    private ShipRenderer _shipRenderer = null!;
    private ChaseCamera _chaseCamera = null!;
    private Starfield _starNear = null!;
    private Starfield _starMid = null!;
    private Starfield _starFar = null!;

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

    // Virtual-joystick mouse flight (P210). Enabled/disabled with M, mirroring
    // the original's TK_ToggleMouse: hiding the OS cursor while active, showing
    // it again when off, and resetting the stored deflection on toggle-off (the
    // original's SetEnabled(false) zeroes m_ppnumber[0..2]). While active the
    // controller's (yaw, pitch) REPLACES arrow-key yaw/pitch; roll/throttle stay
    // purely keyboard, untouched.
    private readonly MouseFlightController _mouseFlight = new();
    private bool _mouseFlightEnabled;
    private bool _mKeyWasDown; // edge-detect the M toggle so holding it doesn't re-fire

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
        // already supplies the needed ~100x) fits in frame. farPlane bumped
        // from the original 3000 to 6000 to give the Starfield below more
        // visible range (near:far ratio 600:1 with near=10 — still healthy,
        // well short of the ~8000:1 ratio P207 found actually risky).
        // fieldOfViewDegrees/turnSmoothingTauSeconds: live playtest feedback
        // was that flying "felt very 2D" even with roll/pitch/yaw genuinely
        // working — the default 45° FOV plus a perfectly-locked camera gave
        // no sense of depth or turning. See ChaseCamera's constructor remarks.
        _chaseCamera = new ChaseCamera(distanceBehind: 350f, heightAbove: 90f, nearPlane: 10f, farPlane: 6000f,
            fieldOfViewDegrees: 75f, turnSmoothingTauSeconds: 0.18f);

        // Three static point-starfield layers at different distance bands so
        // forward motion has real parallax depth, not just one flat backdrop
        // — see Starfield's own remarks for why this was needed (the chase
        // camera alone gives no visual reference for straight-line travel;
        // a single distance band gave only weak, uniform parallax). Centered
        // on the origin with bands wide enough to cover this mission's
        // typical spawn coordinates (observed in the hundreds to low
        // thousands) without needing to wait for the real ship position,
        // which LoadContent runs before receiving anyway. Nearer layers are
        // brighter and sparser (fast, sharp parallax); farther layers are
        // dimmer and denser (slow, backdrop-like) — real depth cues on top
        // of the parallax-rate difference itself.
        _starNear = new Starfield(GraphicsDevice, center: Vector3.Zero, innerRadius: 1200f, outerRadius: 2200f,
            count: 500, seed: 111, minBrightness: 220, maxBrightness: 255);
        _starMid = new Starfield(GraphicsDevice, center: Vector3.Zero, innerRadius: 2200f, outerRadius: 3800f,
            count: 1200, seed: 222, minBrightness: 160, maxBrightness: 220);
        _starFar = new Starfield(GraphicsDevice, center: Vector3.Zero, innerRadius: 3800f, outerRadius: 6000f,
            count: 2500, seed: 333, minBrightness: 90, maxBrightness: 160);
    }

    protected override void Update(GameTime gameTime)
    {
        if (GamePad.GetState(PlayerIndex.One).Buttons.Back == ButtonState.Pressed
            || Keyboard.GetState().IsKeyDown(XnaKeys.Escape))
            Exit();

        _nowSeconds += gameTime.ElapsedGameTime.TotalSeconds;

        // Sample the keyboard once; the M toggle needs the sampled state and the
        // per-frame flight input builds off the same snapshot.
        var ks = Keyboard.GetState();

        // Virtual-joystick mouse-flight toggle (M), edge-triggered to mirror the
        // original's TK_ToggleMouse key (not a held-until-repeat behaviour).
        bool mMouse = ks.IsKeyDown(XnaKeys.M);
        if (mMouse && !_mKeyWasDown)
        {
            _mouseFlightEnabled = !_mouseFlightEnabled;
            IsMouseVisible = !_mouseFlightEnabled;
            if (_mouseFlightEnabled)
                _mouseFlight.Enable();      // arm the just-enabled skip
            else
                _mouseFlight.Reset();       // zero the stored deflection (SetEnabled(false))
            Console.WriteLine($"[monogame] mouse flight {( _mouseFlightEnabled ? "ON" : "OFF" )}");
        }
        _mKeyWasDown = mMouse;

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

        // While mouse-flight is active, the virtual joystick's integrated
        // (yaw, pitch) REPLACES the arrow-key yaw/pitch; roll and throttle (and
        // the movement buttons in StateM) remain exactly what FlightInput
        // computed from the keyboard. The raw delta is the cursor offset from
        // the window centre — the modern equivalent of the original's fixed
        // (320, 240) — and the cursor is warped back to that centre afterwards
        // so each frame's reading is the delta since the last one.
        if (_mouseFlightEnabled)
        {
            var ms = Mouse.GetState();
            int centerX = GraphicsDevice.Viewport.Width / 2;
            int centerY = GraphicsDevice.Viewport.Height / 2;

            var defl = _mouseFlight.Update(ms.X - centerX, ms.Y - centerY);
            cmd = cmd with
            {
                Controls = cmd.Controls with { Yaw = defl.Yaw, Pitch = defl.Pitch },
            };

            Mouse.SetPosition(centerX, centerY);
        }

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
            var (view, proj) = _chaseCamera.Matrices(myPos, myRot, aspect, (float)gameTime.ElapsedGameTime.TotalSeconds, out var cameraEye);

            _starFar.Draw(view, proj, cameraEye);
            _starMid.Draw(view, proj, cameraEye);
            _starNear.Draw(view, proj, cameraEye);

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
