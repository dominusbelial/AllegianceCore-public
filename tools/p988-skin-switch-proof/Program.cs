// P988: actual Game1 + loopback server, production keys/options/frame boundary.
// No alternate drawing. Captures use Game1's own real-backbuffer autoshot.
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.FirstOrDefault() ?? ".scratch/p988/switch-proof");
bool restart = args.Contains("--restart");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
string settings = Path.Combine(output, "hud.json");
if (!restart) new HudSettings { Skin = HudSkinIds.Classic, ControlHintSeen = true }.Save(settings);
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settings);
KeyBindingStore.Instance.Map.ResetToDefaults();

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new Allegiance.Server.ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    Games = [new Allegiance.Server.GameConfig { Name = "P988 switch proof", Map = "m2scatter.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], GameLengthSeconds = 1800 }]
});
server.Missions[0].Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); }
});
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P988 pilot",
        LobbyUrl = "http://127.0.0.1:1", LobbyUrlExplicit = true,
        WindowWidth = 800, WindowHeight = 480,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new SwitchProof(game, output, restart));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class SwitchProof(Game1 game, string output, bool restart) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step;
    private double _next;
    private object? _target;
    private bool _settled;
    private readonly ConsolePaneState _panes = new(false, true, true, TeamConsoleSize.Hidden);
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] values) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, values);
    private HudSkinSwitcher Skins => Get<HudSkinSwitcher>("_hudSkins");
    private void KeysDown(params Keys[] keys) => Set("_introProofKeys", keys);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("P988: " + message);
    }
    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        Console.WriteLine($"P988_SCREEN {name} active={Skins.Active.Definition.Id}");
    }
    private void StateUnchanged()
    {
        Check(Get<ConsolePaneState>("_consolePanes") == _panes, "pane state changed");
        Check(Equals(_target, typeof(Game1).GetField("_autoTarget", Hidden)!.GetValue(game)), "target changed");
        Check(Get<bool>("_chatActive"), "chat compose closed");
        Check(Get<StringBuilder>("_chatBuffer").ToString() == "message survives skin switching", "chat text changed");
    }
    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("P988 switch proof did not finish");
        if (Get<object>("_state").ToString() != "Flying" || Get<bool>("_dockedActive")) return;
        if (_watch.Elapsed.TotalSeconds < _next) return;
        _next = _watch.Elapsed.TotalSeconds + .6;
        switch (_step++)
        {
            case 0:
                Call("DismissBriefing");
                if (!Get<Allegiance.ClientCore.Flight.OwnShipSimulator>("_ownSim").HaveHull)
                { _step = 0; return; }
                if (!_settled) { _settled = true; _step = 0; _next += 2; return; }
                Check(Skins.Active.Definition.Id == HudSkinIds.Classic, "startup/restart did not restore Classic");
                Shot(restart ? "restart-classic" : "01-classic-before");
                break;
            case 1:
                if (restart) { Console.WriteLine("P988_RESTART_PASS"); game.Exit(); return; }
                KeysDown(Keys.LeftShift, Keys.F12);
                break;
            case 2:
                Check(Skins.Active.Definition.Id == HudSkinIds.Modern, "Shift+F12 did not select Modern");
                Check(!Get<bool>("_creditsVisible"), "Shift+F12 also opened Credits");
                Shot("02-modern-after");
                break;
            case 3:
                Check(Skins.Active.Definition.Id == HudSkinIds.Modern, "held chord fired repeatedly");
                KeysDown();
                Set("_consolePanes", _panes); Set("_minimapExpanded", true);
                var session = Get<ConnectionSession>("_session");
                lock (session.Gate)
                {
                    var station = session.World.Stations.Values.First();
                    var targetType = typeof(Game1).GetNestedType("AutopilotTarget", BindingFlags.NonPublic)!;
                    var kindType = typeof(Game1).GetNestedType("TargetKind", BindingFlags.NonPublic)!;
                    _target = Activator.CreateInstance(targetType, Enum.Parse(kindType, "Station"), station.StationId,
                        station.Name, (short)-1, true);
                    Set("_autoTarget", _target);
                }
                Call("SetComposeState", NativeComposeState.Chat);
                Get<StringBuilder>("_chatBuffer").Append("message survives skin switching");
                break;
            case 4:
                Shot("03-modern-panes-before");
                KeysDown(Keys.LeftShift, Keys.F12); // compose must consume this edge
                break;
            case 5:
                StateUnchanged();
                Check(Skins.Active.Definition.Id == HudSkinIds.Modern, "compose failed to gate cycle");
                KeysDown();
                Set("_graphicsSettingsVisible", true); Set("_graphicsRow", 6);
                Call("UpdateGraphicsSettings", new KeyboardState(Keys.Right));
                Check(Skins.Active.Definition.Id == HudSkinIds.Modern, "options switched before frame boundary");
                Set("_graphicsSettingsVisible", false);
                break;
            case 6:
                StateUnchanged();
                Check(Skins.Active.Definition.Id == HudSkinIds.Classic, "options did not restore Classic");
                Shot("04-classic-panes-after");
                break;
            case 7:
                Check(!Get<bool>("_autoShotScheduled"), "capture did not flush");
                Check(HudSettings.Load(HudSettings.DefaultPath, true).Skin == HudSkinIds.Classic, "selection was not saved");
                Console.WriteLine("P988_SWITCH_PASS Classic -> Modern -> Classic; held/compose/options/target/panes preserved");
                game.Exit();
                break;
        }
    }
}
