using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Graphics;
using V = Allegiance.Core.Collision.Vector3;
using O = Allegiance.Core.Sim.Orientation;
using Srv = Allegiance.Server.Server;

// Real DesktopGL window, production HUD/audio and TCP session. Run from repo root:
// dotnet run --project tools/p629-proof -- <built Content directory>
using var proof = new Proof(args[0]);
proof.Run();

sealed class Proof : Game
{
    private readonly Srv _server = new();
    private readonly ConnectionSession _session = new();
    private readonly List<Ship> _population = [];
    private Ship _owner = null!;
    private Mission _mission = null!;
    private HudRenderer _hud = null!;
    private SoundManager _sound = null!;
    private SpriteBatch _batch = null!;
    private SpriteFont _font = null!;
    private Texture2D _white = null!;
    private double _elapsed;
    private double _tick;
    private int _phase;
    private int _loggedPhase = -1;

    public Proof(string content)
    {
        var graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1280, PreferredBackBufferHeight = 800 };
        Content.RootDirectory = Path.GetFullPath(content);
        Window.Title = "P629 live warnings";
        IsMouseVisible = true;
        _ = graphics;
    }

    protected override void LoadContent()
    {
        _batch = new SpriteBatch(GraphicsDevice);
        _font = Content.Load<SpriteFont>("HUD");
        _white = new Texture2D(GraphicsDevice, 1, 1); _white.SetData([Color.White]);
        _hud = new HudRenderer(_batch, _white, _font, _font);
        _sound = new SoundManager(Content); _sound.Load();
        if (!_sound.IsLoaded) throw new InvalidOperationException("Proof requires real audio");
        var spawn = new Func<StaticCore, Mission, short, (Ship, Sector)>((core, m, id) =>
        {
            var sector = m.Sectors.Single(s => s.Id == 1052);
            var hull = core.HullTypes.Values.First(h => !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                with { Thrust = 0, Length = 1, MaxWeapons = 0, MaxFixedWeapons = 0, Hardpoints = [] };
            _owner = new Ship(id, hull, new(0, 0, 2000), V.Zero, O.Identity) { SideId = 0 };
            m.PlaceShip(_owner, sector);
            for (short i = 0; i < 74; i++)
            {
                var ship = new Ship((short)(30000 + i), hull, new((i % 10 - 5) * 100, (i / 10 - 4) * 100, 0), V.Zero, O.Identity) { SideId = 0 };
                m.PlaceShip(ship, sector); _population.Add(ship);
            }
            return (_owner, sector);
        });
        typeof(Srv).GetField("ShipSpawner", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(_server, spawn);
        _server.Boot(new()
        {
            ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
            Games = [new() { Name = "P629 HUD/audio proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [25, 3], SideNames = ["A", "B"] }]
        });
        _mission = _server.Missions[0]; _mission.Stage = MissionStage.Started;
        _session.Connect(_server.BoundPort, "P629");
        _session.SendLogonRequest(new()
        {
            CharacterName = "P629", ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            FedsrvVer = LogonRequest.MsgVer, Time = 1, DwCookie = 1, CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "p629"
        });
        _session.SendPositionReq(0);
    }

    protected override void Update(GameTime gameTime)
    {
        double dt = gameTime.ElapsedGameTime.TotalSeconds; _elapsed += dt; _tick += dt;
        while (_tick >= 0.05) { _server.TickSim(); _tick -= 0.05; }
        if (_elapsed > 10 && _phase == 0 && _owner is not null)
        {
            var fc = _mission.Core.Constants.FloatConstants;
            _owner.Position = new(fc[1] * MathF.Sqrt(fc[2]) * 2, 0, 0); _phase = 1;
        }
        if (_elapsed > 20 && _phase == 1)
        {
            _owner!.Position = new(0, 0, 2000);
            foreach (var ship in _population) ship.Sector?.Remove(ship);
            _phase = 2;
        }
        lock (_session.Gate)
        {
            var state = _session.World.ShipWarnings;
            _sound.UpdateShipWarnings(state.Mask, state.TakeRisingEdges());
            var loop = (SoundEffectInstance)typeof(SoundManager).GetField("_boundsWarning", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_sound)!;
            if (_loggedPhase != _phase && (byte)state.Mask == (_phase == 0 ? 2 : _phase == 1 ? 3 : 0))
            {
                Console.WriteLine($"P629-WINDOW phase={_phase} warnings={state.Mask} hull={_owner!.Fraction:F4} loop={loop.State} audioLoaded={_sound.IsLoaded}");
                _loggedPhase = _phase;
            }
        }
        if (_elapsed > 25) Exit();
        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(7, 12, 22));
        _batch.Begin();
        _batch.DrawString(_font, "P629 / LIVE TCP / NATIVE SECTOR PENALTIES", new(20, 20), Color.White);
        _batch.DrawString(_font, _phase == 0 ? "75 players: sector crowding" : _phase == 1 ? "75 players + ship beyond the loaded core boundary" : "Returned inside; sector population removed; alarm stopped", new(20, 600), Color.LightGray);
        _batch.End();
        lock (_session.Gate)
        {
            var world = _session.World;
            if (world.MyShipId is { } id && world.Ships.TryGetValue(id, out var entity) && entity.Latest is { } u)
                _hud.Draw(new HudFrame(u.HullFraction, u.ShieldFraction, u.AmmoFraction, u.FuelFraction,
                    u.EnergyFraction, u.Power, u.Velocity.Length(), -1, false, false, false, 0, false, 0,
                    false, false, 0, 0, [], false, 0, WarningMask: world.ShipWarnings.Mask), CameraMode.Cockpit, GraphicsDevice.Viewport);
        }
        base.Draw(gameTime);
    }

    protected override void OnExiting(object sender, ExitingEventArgs args)
    {
        _session.Dispose(); _server.Dispose(); _sound.Dispose(); _batch.Dispose(); _white.Dispose();
        base.OnExiting(sender, args);
    }
}
