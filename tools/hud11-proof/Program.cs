// HUD-11 real-client proof. Drives the actual Game1 over TCP against a real
// local server, then schedules real backbuffer screenshots for the HUD-11
// layout/behaviour items: chat + console text + clamped radar labels + the
// inventory pane at 1080p/1440p in both skins, the command bar wing combo, and
// the velocity direction marker while the ship is sliding.
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? ".scratch/hud11-capture");
Directory.CreateDirectory(output);
int width = ArgInt("--width", 1920), height = ArgInt("--height", 1080);
string style = Arg("--style", "Classic");
string scenario = Arg("--scenario", "layout");
string[] civs = Arg("--civs", "1,3").Split(',');
int[] civIds = civs.Select(int.Parse).ToArray();
// HUD-11 settings are read by a field initializer when Game1 is constructed, so
// the environment must be set before `new Game1`.
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
    Games = [new GameConfig { Name = "HUD-11 proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = civIds, SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 600 }]
});
server.Missions[0].Stage = MissionStage.Started;
// The stock map starts with a small initial tech set (2 hulls). For the
// inventory-pane layout shot we need the native weapon StatsBar rows, so grant
// every development bit before the client joins (its CREATE_BUCKETS burst then
// offers the full catalogue).
if (scenario != "velocity")
{
    var allTech = new Allegiance.Core.Model.TechTreeBitMask(Enumerable.Repeat((byte)0xFF, Allegiance.Core.Model.TechTreeBitMask.ByteCount).ToArray());
    foreach (var side in server.Missions[0].Sides) side.DevelopmentTechs = allTech;
}
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD11 pilot",
        WindowWidth = width, WindowHeight = height,
        AutoProof = new AutoProofPlan
        {
            AutoSide = 0, AutoReady = true, AutoLaunchRequested = true,
            // The velocity shot is a short coast in the stock scout; the layout
            // and inventory shots need the full Adv Fighter loadout rows.
            LaunchHull = scenario == "velocity" ? "" : "Adv Fighter",
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud11Proof(game, output, scenario, width, height, style));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}
int ArgInt(string name, int fallback) => int.TryParse(Arg(name, ""), out int v) ? v : fallback;

sealed class Hud11Proof(Game1 game, string output, string scenario, int width, int height, string style) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private int _look;
    private bool _captured;
    private static readonly float[] LookYaws = [0f, Allegiance.ClientCore.MonoGame.Rendering.HeadLook.Right, Allegiance.ClientCore.MonoGame.Rendering.HeadLook.FrontRight, Allegiance.ClientCore.MonoGame.Rendering.HeadLook.FrontLeft, Allegiance.ClientCore.MonoGame.Rendering.HeadLook.Left, MathF.PI, -Allegiance.ClientCore.MonoGame.Rendering.HeadLook.Right];
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args)
    {
        var method = typeof(Game1).GetMethod(name, Hidden) ?? throw new MissingMethodException(name);
        return method.Invoke(game, args);
    }
    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine($"HUD11_SCREEN {name} {width}x{height} {style}");
    }
    private ConnectionSession Session => Get<ConnectionSession>("_session");
    private ClientWorld World => Session.World;

    private void InjectConsoleOrders()
    {
        lock (Session.Gate)
        {
            var world = World;
            short? targetShip = null;
            foreach (var (id, ship) in world.Ships)
            {
                if (id == world.MyShipId || ship.IsDead) continue;
                targetShip = id; break;
            }
            short stationId = -1;
            foreach (var (id, _) in world.Stations) { stationId = id; break; }
            if (targetShip is { } tid)
                typeof(ClientWorld).GetField("_myQueuedCommand", Hidden)!.SetValue(world,
                    new QueuedCommandState(OrderChange.CidAttack, IgcObjectType.Ship, tid));
            if (stationId >= 0)
                typeof(ClientWorld).GetField("<MyAcceptedCommand>k__BackingField", Hidden)!.SetValue(world,
                    new QueuedCommandState(OrderChange.CidDefend, IgcObjectType.Station, stationId));
        }
    }

    private void PostChat()
    {
        lock (Session.Gate)
            for (int i = 1; i <= 11; i++)
                Session.World.PostSystemLine($"server: HUD-11 chat history line {i} - a full pane");
    }

    private Allegiance.Core.Collision.Vector3 _lastSlideVelocity;

    private void StageSlide()
    {
        var sim = Get<object>("_ownSim");
        var orientation = (Allegiance.Core.Sim.Orientation)sim.GetType().GetProperty("Orientation")!.GetValue(sim)!;
        var forward = orientation.GetForward();
        var right = orientation.GetRight();
        // 100 m/s along the nose plus a 55 m/s right side-slip (~29°).
        _lastSlideVelocity = new Allegiance.Core.Collision.Vector3(
            forward.X * 100f + right.X * 55f,
            forward.Y * 100f + right.Y * 55f,
            forward.Z * 100f + right.Z * 55f);
        sim.GetType().GetField("_velocity", Hidden)!.SetValue(sim, _lastSlideVelocity);
    }

    private bool MarkerClear(Vector2 p)
    {
        if (Vector2.Distance(p, new Vector2(width / 2f, height / 2f)) < 65f) return false;
        return p.X > 40 && p.X < width - 40 && p.Y > 24 && p.Y < height - 40;
    }

    private bool TopLeftLabel()
    {
        var contacts = Get<List<Allegiance.ClientCore.MonoGame.Rendering.HudContact>>("_drawRadarContacts");
        foreach (var contact in contacts)
            if (contact.Name is { Length: > 0 } && contact.Position.X < 420 && contact.Position.Y < 240)
                return true;
        return false;
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("HUD-11 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying || _watch.Elapsed.TotalSeconds < _next) return;
        if (!Get<bool>("_briefingDismissed"))
        {
            Call("DismissBriefing");
            _next = _watch.Elapsed.TotalSeconds + 0.4;
            return;
        }
        if (_captured) { game.Exit(); return; }
        switch (scenario)
        {
            case "velocity":
            {
                // Stage a real SIDE-SLIP: keep the ship's current facing but give
                // the local simulation a velocity ~30° off the nose (a slide).
                // The stock launch attitude/velocity otherwise points straight
                // down the reticle, which is why earlier shots never showed the
                // marker. The marker itself is the production
                // ComputeVelocityMarker -> directionbmp path rendered into a real
                // backbuffer; the slide is re-staged every frame so a server
                // correction cannot erase it before the capture.
                StageSlide();
                var marker = Get<Vector2?>("_velocityMarker");
                if (_step++ > 1 && marker is { } m && MarkerClear(m))
                {
                    Console.WriteLine($"HUD11_VELOCITY marker=({m.X:0},{m.Y:0}) centre=({width / 2f:0},{height / 2f:0}) staged_slip=({_lastSlideVelocity.X:0},{_lastSlideVelocity.Y:0},{_lastSlideVelocity.Z:0})");
                    Shot($"velocity-{width}x{height}-{style}"); _captured = true;
                    _next = _watch.Elapsed.TotalSeconds + 1.5;
                    return;
                }
                _next = _watch.Elapsed.TotalSeconds + 0.15;
                return;
            }
            case "command":
            {
                switch (_step++)
                {
                    case 0:
                        // Close any open console pane (the toggle pair leaves
                        // GameState off) before framing the command bar.
                        Call("ApplyConsoleMode", "ConModeGameState", true);
                        Call("ApplyConsoleMode", "ConModeGameState", true);
                        Set("_cameraMode", Allegiance.ClientCore.MonoGame.Rendering.CameraMode.Command);
                        Set("_commandCentered", false);
                        _next = _watch.Elapsed.TotalSeconds + 1.5;
                        return;
                    case 1:
                        Shot($"command-{width}x{height}-{style}"); _captured = true;
                        _next = _watch.Elapsed.TotalSeconds + 1.5;
                        return;
                    default:
                        game.Exit();
                        return;
                }
            }
            case "inventory":
            {
                switch (_step++)
                {
                    case 0:
                        Set("_throttle", 1f);
                        Call("ApplyConsoleMode", "ConModeInventory", true);
                        _next = _watch.Elapsed.TotalSeconds + 1.2;
                        return;
                    case 1:
                        Shot($"inventory-{width}x{height}-{style}"); _captured = true;
                        _next = _watch.Elapsed.TotalSeconds + 1.5;
                        return;
                    default:
                        game.Exit();
                        return;
                }
            }
            default:   // layout: console text + chat + clamped labels + inventory
            {
                switch (_step++)
                {
                    case 0:
                        Set("_throttle", 1f);
                        PostChat();
                        InjectConsoleOrders();
                        Call("ApplyConsoleMode", "ConModeInventory", true);
                        _next = _watch.Elapsed.TotalSeconds + 0.8;
                        return;
                    case 1:
                    case 2:
                    case 3:
                    case 4:
                    case 5:
                    case 6:
                    case 7:
                        if (!TopLeftLabel())
                        {
                            Set("_headLookYaw", LookYaws[_look++ % LookYaws.Length]);
                            _next = _watch.Elapsed.TotalSeconds + 0.5;
                            return;
                        }
                        goto default;
                    default:
                        var slides = Get<object>("_paneSlides");
                        float invSlide = (float)slides.GetType().GetMethod("Progress")!.Invoke(slides,
                            new object[] { Allegiance.ClientCore.MonoGame.Rendering.SlidingPane.Inventory, Get<double>("_nowSeconds") })!;
                        var vp = game.GraphicsDevice.Viewport;
                        Console.WriteLine($"HUD11_LAYOUT top_left_label={TopLeftLabel()} head_look={Get<float>("_headLookYaw"):0.00} contacts={Get<List<Allegiance.ClientCore.MonoGame.Rendering.HudContact>>("_drawRadarContacts").Count} inventory_slide={invSlide:0.00} viewport={vp.Width}x{vp.Height}");
                        Shot($"layout-{width}x{height}-{style}"); _captured = true;
                        Set("_headLookYaw", 0f);
                        _next = _watch.Elapsed.TotalSeconds + 1.5;
                        return;
                }
            }
        }
    }
}
