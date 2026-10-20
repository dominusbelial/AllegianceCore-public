// Run from the repository root. This drives the actual Game1 (and, for the
// flight shots, a real ephemeral server) over TCP; no replacement renderer,
// seeded screen rows, or screenshot composition.
//
// Flight mode (default): chat pane (compose line + channel filter) and the
// help manual overlay. Intro mode (--intro): the splash screen, the intro menu
// (with the new Replay Intro + Help buttons), the Options menu, the Credits
// popup and the intro Help (manual) view.
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "docs/reports/P757_assets");
bool introMode = args.Contains("--intro");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

if (introMode)
{
    // The intro lane: no server. Native boots splash -> intro; the proof drives
    // the real menu handlers (ActivateIntroMain) for Options/Credits/Help.
    using var game = new Game1(new Game1.LaunchOptions
    {
        Intro = true, PlayerName = "P757 pilot",
        WindowWidth = 1280, WindowHeight = 720,
    });
    // Hold the splash past its 0.01 s timeout until the first shot is queued
    // (components run after Game1's own Update, which would otherwise advance
    // to the intro before any Draw sees the splash).
    typeof(Game1).GetField("_splashStartSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(game, 1e9);
    game.Content.RootDirectory = content;
    game.Components.Add(new IntroProof(game, output));
    game.Run();
    return;
}

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "P757 UI proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
});
server.Missions[0].Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P757 UI pilot",
        WindowWidth = 1280, WindowHeight = 720,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new FlightProof(game, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

// ── flight lane: chat pane + help manual ────────────────────────────────────
sealed class FlightProof(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);
    private ChatPaneModel Pane => Get<ChatPaneModel>("_chatPane");

    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        Console.WriteLine("P757_SCREEN " + name);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P757 flight proof did not finish");
        bool flying = Get<object>("_state").ToString() == "Flying" && !Get<bool>("_dockedActive");
        if (!flying || _watch.Elapsed.TotalSeconds < _next) return;
        var session = Get<ConnectionSession>("_session");
        switch (_step++)
        {
            case 0:
                // Real log content: enough system lines to make the pane
                // genuinely scrollable (a pause marker needs more than a page),
                // plus the player's own Team line sent below.
                lock (session.Gate)
                {
                    for (int i = 1; i <= 12; i++)
                        session.World.PostSystemLine($"server: flight proof log line {i}");
                }
                break;
            case 1:
                // Open the composer through the REAL production seam and type.
                // The shot is scheduled in the SAME component update (components
                // run after Game1.Update and before Draw), so live keyboard state
                // cannot close the composer before the frame is captured.
                Call("HandleChatCompose", new KeyboardState());
                Call("HandleChatCompose", new KeyboardState(Keys.Enter));
                // A held live Enter can turn that edge into a send; force the
                // composer open after the seam call so this frame still shows it.
                if (!Get<bool>("_chatActive")) Set("_chatActive", true);
                Get<System.Text.StringBuilder>("_chatBuffer").Clear();
                foreach (var c in "wingman cover me")
                    Call("OnTextInput", null!, new Microsoft.Xna.Framework.TextInputEventArgs(c, Keys.None));
                Shot("chat-compose-720p");   // native UpdateComposedChat: recipient + typed + "|"
                break;
            case 2:
                // Send a real Team line through the production send path. Set the
                // composer state explicitly so a held live Enter cannot empty it
                // first; SendChatLine still owns moderation + CHATMESSAGE(23).
                Set("_chatActive", true);
                var buffer = Get<System.Text.StringBuilder>("_chatBuffer");
                buffer.Clear().Append("wingman cover me");
                Call("SendChatLine");
                break;
            case 3:
                // Cycle the channel filter (Alt+C edge's production seam) to the
                // channel mode: the Everyone system lines drop out, the Team
                // chat stays (native ChatPassesFilter).
                Set("_chatPaneFilterMode", 2);
                Call("ApplyChatPaneFilterMode");
                Shot("chat-filter-channel-720p");
                break;
            case 4:
                Set("_chatPaneFilterMode", 0);
                Call("ApplyChatPaneFilterMode");
                Pane.PageUp((int)Call("FilteredChatCount")!, ChatRenderer.MaxVisible, 0);
                Shot("chat-filter-paused-720p");   // autoscroll paused + (OLDER) hint
                break;
            case 5:
                Pane.ScrollToBottom();
                Set("_helpVisible", true);         // H overlay opens the manual at hlpStart
                Set("_helpManual", new HelpManualModel());
                Shot("help-manual-start-720p");
                break;
            case 6:
                Get<HelpManualModel>("_helpManual").GoTo("hlpControls");
                Shot("help-manual-controls-720p"); // the generated key reference inside the manual
                break;
            case 7:
                Get<HelpManualModel>("_helpManual").Back();
                Shot("help-manual-back-720p");
                break;
            default:
                game.Exit(); return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}

// ── intro lane: splash, menu, options, credits, help ────────────────────────
sealed class IntroProof(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        Console.WriteLine("P757_SCREEN " + name);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 60) throw new TimeoutException("P757 intro proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0:
                // The splash is held past its 0.01 s timeout until the shot has
                // flushed (the flush lands on a later frame's Draw end).
                Shot("splash-720p");   // native splash: title + loading
                break;
            case 1:
                // Release the held timeout: the next Update advances to the
                // intro (the transition under proof, splashscreen.cpp:34-46).
                Set("_splashStartSeconds", Get<double>("_nowSeconds"));
                break;
            case 2:
                if (Get<object>("_state").ToString() != "Intro")
                    throw new InvalidOperationException("splash did not time out to the intro");
                Set("_introIndex", 6);   // Replay Intro (native introButtonPane)
                break;
            case 3:
                Shot("intro-menu-720p");
                break;
            case 4:
                Set("_introIndex", 4);   // Options (native optionsButtonPane)
                Call("ActivateIntroMain");
                break;
            case 5:
                Shot("intro-options-720p");
                break;
            case 6:
                Set("_introView", Enum.Parse(Get<object>("_introView").GetType(), "Main"));
                Set("_introIndex", 5);   // Credits (native creditsButtonPane)
                Call("ActivateIntroMain");
                break;
            case 7:
                Shot("intro-credits-720p");
                break;
            case 8:
                Set("_introView", Enum.Parse(Get<object>("_introView").GetType(), "Main"));
                Set("_introIndex", 7);   // Help (native helpButtonPane)
                Call("ActivateIntroMain");
                break;
            case 9:
                Shot("intro-help-720p");
                break;
            case 10:
                // Drive the Replay Intro handler for real: intro -> splash.
                Set("_introView", Enum.Parse(Get<object>("_introView").GetType(), "Main"));
                Set("_introIndex", 6);
                Call("ActivateIntroMain");
                if (Get<object>("_state").ToString() != "Splash")
                    throw new InvalidOperationException("Replay Intro did not enter the splash state");
                Set("_splashStartSeconds", 1e9);   // hold for the shot
                break;
            case 11:
                Shot("intro-replay-splash-720p");
                Set("_splashStartSeconds", Get<double>("_nowSeconds"));
                break;
            case 12:
                if (Get<object>("_state").ToString() != "Intro")
                    throw new InvalidOperationException("replay splash did not time out back to the intro");
                break;
            default:
                game.Exit(); return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}
