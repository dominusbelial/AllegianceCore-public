using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

// P789 — real-client UI evidence for the one-Showcase merge: the game-over
// screen with the ADDITIVE performance panel (score, fps/1% low, frame-ms
// percentiles, time dilation, ships in view, bottleneck, Apply hint) beside
// the native tables. Drives the actual Game1 + a local server over TCP; the
// recorder runs for real during the flight, nothing is seeded into the panel.
//
// Usage: p789-ui-proof <outdir> <WxH> <classic|modern> <shotname>
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P789_assets");
string window = args.Length > 1 ? args[1] : "1920x1080";
string style = args.Length > 2 ? args[2] : "classic";
string shotName = args.Length > 3 ? args[3] : "gameover-perf";
int width = int.Parse(window.Split('x')[0]);
int height = int.Parse(window.Split('x')[1]);
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    SideSelectAutoSeatSeconds = 30,
    Games = [new GameConfig { Name = "P789 UI proof", Map = "XXV_simple.igc", Teams = 2,
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
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P789 proof pilot",
        WindowWidth = width, WindowHeight = height,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new P789UiProof(game, server, output, shotName, style));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class P789UiProof(Game1 game, Srv server, string output, string shotName, string style) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _pendingPanelShot;
    private bool _finishAfterFlush;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        Console.WriteLine("P789_SCREEN " + name);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P789 real-client UI proof did not finish");
        if (_finishAfterFlush)
        {
            if (!Get<bool>("_autoShotScheduled")) game.Exit();
            return;
        }
        // Panel poll is state-independent: after GAME_OVER the client sits in
        // the ReadyCheck intermission (the panel stays up through it).
        if (_pendingPanelShot)
        {
            var session = Get<ConnectionSession>("_session");
            bool hasResult, hasPanel;
            lock (session.Gate) hasResult = session.World.GameOverResult is not null;
            hasPanel = Get<object>("_perfPanel") is not null;
            if (hasResult && hasPanel)
            {
                Shot(shotName);
                _pendingPanelShot = false;
                _finishAfterFlush = true;
            }
            else if (_watch.Elapsed.TotalSeconds > 45) throw new TimeoutException($"game over panel missing (result={hasResult} panel={hasPanel})");
            return;
        }
        if (Get<object>("_state").ToString() != "Flying" || _watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0:
            {
                // One-shot: which printable ASCII glyphs does the HUD font lack?
                var font = (Microsoft.Xna.Framework.Graphics.SpriteFont)Get<object>("_hudSmall");
                var missing = new System.Text.StringBuilder();
                for (int c = 32; c <= 126; c++)
                {
                    try { font.MeasureString(((char)c).ToString()); }
                    catch (ArgumentException) { missing.Append((char)c); }
                }
                Console.WriteLine("P789 missing glyphs: [" + missing + "]");
                // Match the user's HUD skin for this run; the game-over panel is
                // identical either way, the surrounding HUD is not.
                ((HudSettings)Get<object>("_hudSettings")).Style =
                    style == "modern" ? HudStyle.Modern : HudStyle.Classic;
                _next = _watch.Elapsed.TotalSeconds + 4.0;   // let the recorder accrue ~4 s of real frames
                break;
            }
            case 1:
            {
                // The user toggled the panel on (the O key path); Showcase
                // matches default to shown — this remote proof match defaults
                // to hidden, so flip the same toggle the key flips.
                Set("_perfPanelShown", true);
                var session = Get<ConnectionSession>("_session");
                bool recording = Get<bool>("_sessionRecording");
                Console.WriteLine($"P789 recording={recording}");
                try
                {
                    // GAME_OVER(62) is queued and Stage flipped to Over before the
                    // auto-next-match rotation runs; a rotation tail race aborts
                    // AFTER the wire message is out, which is all this proof needs.
                    typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(server, [server.Missions[0], (short)0, "conquest"]);
                }
                catch (Exception ex) { Console.WriteLine("P789 rotation race (GAME_OVER already sent): " + ex.GetBaseException().Message); }
                _pendingPanelShot = true;
                break;
        }
    }
}
}
