// HUD-10 real-client proof. Drives the actual Game1 over TCP against a real
// local server, then schedules real backbuffer screenshots while production
// code moves through the HUD-10 states (chat history/compose, investment money
// creditbox, game-state clock, command bar, boundary warning, real hull hit,
// ejected lifepod).
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--"))
    ?? ".scratch/hud10-capture");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "HUD-10 proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 600 }]
});
server.Missions[0].Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "HUD10 pilot",
        WindowWidth = 1920, WindowHeight = 1080,
        AutoProof = new AutoProofPlan
        {
            AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "",
            AutoSuicideAt = [24],
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new Hud10Proof(game, server, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class Hud10Proof(Game1 game, Srv server, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private SimVec _savedPosition;
    private bool _podShot;
    private bool _movedOutOfBounds;
    private int _flashShot;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args)
    {
        var method = typeof(Game1).GetMethod(name, Hidden)
            ?? throw new MissingMethodException(name);
        return method.Invoke(game, args);
    }

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("HUD10_SCREEN " + name);
    }

    private Ship? OwnServerShip()
    {
        var client = Get<ConnectionSession>("_session");
        short? id;
        lock (client.Gate) id = client.World.MyShipId;
        if (id is not { } shipId) return null;
        foreach (var mission in server.Missions)
            foreach (var sector in mission.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship ship && ship.Id == shipId) return ship;
        return null;
    }

    private void SetOwnEntityDead(bool dead)
    {
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            if (session.World.MyShipId is { } id && session.World.Ships.TryGetValue(id, out var entity))
                typeof(Allegiance.ClientCore.State.ShipEntity)
                    .GetProperty("IsDead", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(entity, dead);
        }
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("HUD-10 proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying || _watch.Elapsed.TotalSeconds < _next) return;
        var session = Get<ConnectionSession>("_session");
        switch (_step++)
        {
            case 0:
                lock (session.Gate)
                    for (int i = 1; i <= 10; i++)
                        session.World.PostSystemLine($"server: HUD-10 chat history line {i}");
                break;
            case 1:
                // Real production seam: Enter opens the composer; OnTextInput
                // appends characters (and plays chatKeySound).
                Call("HandleChatCompose", new Microsoft.Xna.Framework.Input.KeyboardState());
                Call("HandleChatCompose", new Microsoft.Xna.Framework.Input.KeyboardState(Microsoft.Xna.Framework.Input.Keys.Enter));
                if (!Get<bool>("_chatActive")) Set("_chatActive", true);
                Get<System.Text.StringBuilder>("_chatBuffer").Clear();
                foreach (var c in "wingman cover me")
                    Call("OnTextInput", null!, new Microsoft.Xna.Framework.TextInputEventArgs(c, Microsoft.Xna.Framework.Input.Keys.None));
                Shot("chat-compose-1920x1080");
                break;
            case 2:
                Call("SendChatLine");
                break;
            case 3:
                // Native moneyPane only appears under ofInvestment (F5).
                Set("_investActive", true);
                Call("HandleInvestmentScreen", new Microsoft.Xna.Framework.Input.KeyboardState(), false);
                Set("_investActive", true);
                break;
            case 4:
                Shot("investment-money-1920x1080");
                break;
            case 5:
                Set("_investActive", false);
                Call("ApplyConsoleMode", "ConModeGameState", true);
                break;
            case 6:
                // The screenshot is captured at the END of this frame, so the
                // pane must not be closed in the same case that schedules it.
                Shot("gamestate-clock-1920x1080");
                break;
            case 7:
                Call("ApplyConsoleMode", "ConModeGameState", true);
                Set("_cameraMode", Allegiance.ClientCore.MonoGame.Rendering.CameraMode.Command);
                Set("_commandCentered", false);
                break;
            case 8:
                Shot("command-bar-1920x1080");
                break;
            case 9:
                // Native DeathBannerPane is gated on IsGhost = deaths > mission
                // lives (shipIGC.h:2465-2469), which is not on the client wire;
                // this drives the port's own Dead flag so the native banner ART
                // and unscaled anchor can be captured.
                SetOwnEntityDead(true);
                Shot("death-banner-1920x1080");
                break;
            case 10:
                SetOwnEntityDead(false);
                Set("_cameraMode", Allegiance.ClientCore.MonoGame.Rendering.CameraMode.Cockpit);
                if (OwnServerShip() is { } own && own.Mission is { } mission)
                {
                    // Place the ship just past the native ellipsoid boundary
                    // (shipIGC.cpp:369-395: r2 > slot2*radius2). 2% outside is
                    // enough to set c_wmOutOfBounds with only tiny damage rate,
                    // so the ship survives the warning capture.
                    float lens = mission.Core.Constants.FloatConstants[0];
                    float radius = mission.Core.Constants.FloatConstants[1];
                    float bound = mission.Core.Constants.FloatConstants[2] * radius * radius;
                    _savedPosition = own.Position;
                    own.Position = new SimVec(MathF.Sqrt(bound * 1.02f) * MathF.Max(lens, 1e-6f), 0f, 0f);
                    _movedOutOfBounds = true;
                }
                break;
            case 11:
                Shot("boundary-warning-1920x1080");
                break;
            case 12:
                if (_movedOutOfBounds && OwnServerShip() is { } restored)
                {
                    restored.Position = _savedPosition;
                    _movedOutOfBounds = false;
                }
                break;
            case 13:
                if (OwnServerShip() is { } hit)
                    hit.ReceiveDamage(0, hit.EffectiveMaxHitPoints() * 0.25f, hit.Mission!.LastUpdate,
                        hit.Position, hit.Position, null);
                break;
            case 14:
            case 15:
            case 16:
            case 17:
                // 2.0 s window / 0.25 s on+off: four quick frames guarantee a
                // lit half-cycle is captured (the names record the frame).
                Shot($"damage-flash-{++_flashShot}-1920x1080");
                _next = _watch.Elapsed.TotalSeconds + 0.08;
                return;
            default:
                // Wait for the scripted suicide at t=24, then capture the
                // native eject-pod panel + endurance bar.
                if (_watch.Elapsed.TotalSeconds < 27) { _next = 27; return; }
                if (!_podShot)
                {
                    _podShot = true;
                    Shot("lifepod-1920x1080");
                    _next = _watch.Elapsed.TotalSeconds + 1;
                    return;
                }
                game.Exit();
                return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}
