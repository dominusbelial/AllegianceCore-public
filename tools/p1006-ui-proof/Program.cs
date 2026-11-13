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
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

// Real Game1, production input handlers and backbuffer captures. The caller
// supplies a fresh XDG_CONFIG_HOME, artwork, and SDL offscreen or an X display.
// p1006-ui-proof <output> <classic|modern> <intro|flight> [--baseline]
string output = Path.GetFullPath(args[0]);
string style = args[1];
bool intro = args[2] == "intro", baseline = args.Contains("--baseline");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent) && Directory.Exists(content))
    Directory.CreateSymbolicLink(localContent, content);
using var server = intro ? null : new Srv { SpawnInitialMinersAtBoot = false };
if (server is not null)
{
    server.Boot(new ServerConfig
    {
        ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
        SideSelectAutoSeatSeconds = 30,
        Games = [new() { Name = "P1006 UI proof", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxTeamImbalance = 0x7fff }],
    });
    server.Missions[0].Stage = MissionStage.Started;
}
using var game = new Game1(new Game1.LaunchOptions
{
    Intro = intro, PlayerName = intro ? "" : "P1006 pilot",
    DirectHost = intro ? null : "127.0.0.1", DirectPort = server?.BoundPort ?? 0,
    WindowWidth = 800, WindowHeight = 480,
    AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true },
});
game.Content.RootDirectory = content;
object simGate = new();
using var stop = new CancellationTokenSource();
var ticker = server is null ? null : new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        lock (simGate) server.TickSim();
        Thread.Sleep(20);
    }
});
ticker?.Start();
var proof = new Proof(game, output, style, intro, baseline, simGate);
game.Components.Add(proof);
try { game.Run(); }
finally { stop.Cancel(); ticker?.Join(); }
return proof.Passed ? 0 : 1;

sealed class Proof(Game1 game, string output, string style, bool intro, bool baseline, object gate) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private short _hull;
    public bool Passed { get; private set; }
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);
    private void Keys(params Keys[] keys) => Set("_introProofKeys", keys);
    private void ActionKey(string action)
    {
        var binding = (KeyBinding)Call("KeyOf", action)!;
        var keys = new List<Keys> { binding.Key };
        if ((binding.Modifiers & InputModifiers.Shift) != 0) keys.Add(Microsoft.Xna.Framework.Input.Keys.LeftShift);
        if ((binding.Modifiers & InputModifiers.Control) != 0) keys.Add(Microsoft.Xna.Framework.Input.Keys.LeftControl);
        if ((binding.Modifiers & InputModifiers.Alt) != 0) keys.Add(Microsoft.Xna.Framework.Input.Keys.LeftAlt);
        Keys(keys.ToArray());
    }
    private void Require(bool condition, string message)
    {
        Console.WriteLine($"P1006_CHECK {message}: {condition}");
        if (!baseline && !condition) throw new InvalidOperationException(message);
    }
    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", $"{name}-{style}");
        Set("_autoShotScheduled", true);
    }
    private void Pointer(HangarAction action, bool pressed)
    {
        var vp = game.GraphicsDevice.Viewport;
        var rect = HangarScreen.Rect(vp.Width, vp.Height, action);
        var mouse = new MouseState(rect.Center.X, rect.Center.Y, 0,
            pressed ? ButtonState.Pressed : ButtonState.Released, ButtonState.Released,
            ButtonState.Released, ButtonState.Released, ButtonState.Released);
        Set("_hangarScriptMouse", mouse);
        Mouse.SetPosition(rect.Center.X, rect.Center.Y);
    }
    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 180) throw new TimeoutException("P1006 UI proof");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        Get<HudSettings>("_hudSettings").Style = style == "classic" ? HudStyle.Classic : HudStyle.Modern;
        lock (gate)
        {
            if (intro) Intro(); else Flight();
        }
    }
    private void Advance() { _step++; _next = _watch.Elapsed.TotalSeconds + .7; }
    private void Finish()
    {
        Passed = true;
        Console.WriteLine($"P1006_PASS {style} {(intro ? "intro" : "flight")} baseline={baseline}");
        game.Exit();
    }
    private void Intro()
    {
        switch (_step)
        {
            case 0:
                if (Get<object>("_state").ToString() != "Intro") return;
                Require(Get<System.Text.StringBuilder>("_pilotBuf").Length == 0, "fresh profile");
                Shot("fresh-intro"); Advance(); break;
            case 1:
                // Replaying the startup splash makes the Escape-to-skip edge
                // reproducible without relying on the first frame's timing.
                var field = typeof(Game1).GetField("_state", Hidden)!;
                field.SetValue(game, Enum.Parse(field.FieldType, "Splash"));
                Set("_splashStartSeconds", 1e9);
                Keys(Microsoft.Xna.Framework.Input.Keys.Escape); Advance(); break;
            case 2:
                Keys();
                Require(Get<object>("_state").ToString() == "Intro", "Escape skips splash");
                Require(!Get<bool>("_mainMenuVisible"), "splash skip leaves no second menu");
                Shot("intro-after-splash-escape"); Advance(); break;
            case 3:
                // Dismiss the fresh profile's training suggestion with its
                // ordinary Escape, then test the root intro's explicit popup.
                Set("_mainMenuVisible", false);
                Keys(Microsoft.Xna.Framework.Input.Keys.Escape); Advance(); break;
            case 4:
                Keys();
                Require(!Get<bool>("_mainMenuVisible"), "first-run suggestion dismisses without a second menu");
                Shot("intro-after-suggestion"); Advance(); break;
            case 5:
                Set("_mainMenuVisible", false); // close the baseline's second accidental menu
                Keys(Microsoft.Xna.Framework.Input.Keys.Escape); Advance(); break;
            case 6:
                Keys(); Shot("intro-explicit-escape-menu"); Advance(); break;
            case 7:
                Keys(Microsoft.Xna.Framework.Input.Keys.Down); Advance(); break;
            case 8:
                Keys();
                Require(Get<int>("_introIndex") == 0, "popup owns Down; intro selection unchanged");
                Advance(); break;
            default: Finish(); break;
        }
    }
    private void Flight()
    {
        var session = Get<ConnectionSession>("_session");
        switch (_step)
        {
            case 0:
                if (!Get<bool>("_dockedActive") || Get<ShopModel?>("_shopModel") is null
                    || string.IsNullOrEmpty(session.World.BriefingText)) return;
                Require(!(bool)Call("IsBriefingVisible", new object[] { null! })!,
                    "late join to started game has no stale briefing");
                Shot("hangar"); Advance(); break;
            case 1:
                Keys(Microsoft.Xna.Framework.Input.Keys.F3); Advance(); break;
            case 2:
                Keys();
                Require(Get<CameraMode>("_cameraMode") == CameraMode.Command, "F3 enters command");
                Shot("command-docked"); Advance(); break;
            case 3:
                Pointer(HangarAction.Loadout, true); Advance(); break;
            case 4:
                Pointer(HangarAction.Loadout, false);
                Require(Get<CameraMode>("_cameraMode") == CameraMode.Command, "hidden loadout click leaves command active");
                Shot("command-after-hidden-click"); Advance(); break;
            case 5:
                // Restore command in baseline so the key check is independent.
                Set("_cameraMode", CameraMode.Command);
                _hull = Get<ShopModel>("_shopModel").EffectiveHullId;
                Keys(Microsoft.Xna.Framework.Input.Keys.E); Advance(); break;
            case 6:
                Keys();
                Require(Get<ShopModel>("_shopModel").EffectiveHullId == _hull, "hidden hull-cycle key does nothing");
                // F4 is explicitly allowed in native: docked command -> loadout.
                Keys(Microsoft.Xna.Framework.Input.Keys.F4); Advance(); break;
            case 7:
                Keys();
                Require(Get<CameraMode>("_cameraMode") == CameraMode.Cockpit && !Get<bool>("_hangarOnly"), "F4 explicitly opens docked loadout");
                Shot("explicit-loadout"); Advance(); break;
            case 8:
                ActionKey("ConModeCombat"); Advance(); break;
            case 9:
                // Return to hangar and open investment during the launch request.
                // A modal frame must not defer authoritative undock cleanup.
                Call("ActivateHangar", HangarAction.Launch);
                ActionKey("ToggleInvestmentPane"); Advance(); break;
            case 10:
                if (session.World.MyShipId is not { } id || session.World.Ships[id].StationId >= 0) return;
                Keys();
                Require(!Get<bool>("_dockedActive"), "launch retires hangar despite investment");
                Require(Get<ShopModel?>("_shopModel") is null, "launch retires shop");
                Shot("launched-with-investment"); Advance(); break;
            case 11:
                ActionKey("ToggleInvestmentPane"); Advance(); break;
            case 12:
                Keys(); Shot("launched-flight"); Advance(); break;
            case 13:
                Keys(Microsoft.Xna.Framework.Input.Keys.F3); Advance(); break;
            case 14:
                Keys(); Shot("command-in-space"); Advance(); break;
            case 15:
                Pointer(HangarAction.Loadout, true);
                Keys(Microsoft.Xna.Framework.Input.Keys.L); Advance(); break;
            case 16:
                Pointer(HangarAction.Loadout, false); Keys();
                Require(Get<CameraMode>("_cameraMode") == CameraMode.Command && !Get<bool>("_dockedActive"),
                    "in-space click and shop-launch binding leave command/hangar state unchanged");
                Shot("command-in-space-after-input"); Advance(); break;
            default: Finish(); break;
        }
    }
}
