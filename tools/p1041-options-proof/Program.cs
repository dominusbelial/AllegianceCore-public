// P1041 — real-backbuffer Options-screen proof: boots a local server and the real
// Game1 (offscreen SDL is enough for the backbuffer), leaves the graphics/HUD panel
// open and captures it from the P948 proof hook (which flushes AFTER the panel is
// drawn in EndDraw — the ordinary autoshot flush runs before it).
//
// Usage:
//   dotnet p1041-options-proof.dll --out <dir> --skin classic|modern|jet
//       [--palette green|amber] [--jet on|off] [--window 1920x1080] [--seconds 12]
//
// The hud.json it writes is the ONLY HUD input: Skin selects the skin, JetPalette the
// Jet variant. --jet on adds the client-only `--visuals hud.jetSkin=on` override.
// Every capture is a real presented frame of the real client at the requested size.
using System.Diagnostics;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

string Arg(string name, string fallback)
{
    for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
    return fallback;
}
string output = Path.GetFullPath(Arg("--out", ".scratch/p1041/options"));
string skin = Arg("--skin", "jet");
string palette = Arg("--palette", "green");
bool jet = Arg("--jet", "on") == "on";
string window = Arg("--window", "1920x1080");
int seconds = int.TryParse(Arg("--seconds", "12"), out var s) ? s : 12;
int split = window.IndexOf('x');
int width = int.Parse(window[..split]);
int height = int.Parse(window[(split + 1)..]);
Directory.CreateDirectory(output);
string settings = Path.Combine(output, "hud.json");
File.WriteAllText(settings,
    $"{{\"Skin\":\"{skin}\",\"JetPalette\":\"{palette}\",\"Size\":1,\"ControlHintSeen\":true}}");
Environment.SetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS", settings);
// The P1041 hook opens the panel on the Jet variant row and flushes a real frame
// from EndDraw, after the panel is on the backbuffer (the P948 mechanism).
Environment.SetEnvironmentVariable("ALLEGIANCE_P1041_PROOF", "1");
string content = FindContent();
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent) && Directory.Exists(content)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    Games = [new GameConfig { Name = "P1041 options proof", Map = "m2scatter.igc", Teams = 2,
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
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P1041 options pilot",
        LobbyUrl = "http://127.0.0.1:1", LobbyUrlExplicit = true,
        WindowWidth = width, WindowHeight = height,
        VisualsOverride = jet ? "hud.jetSkin=on" : null,
        AutoProof = new AutoProofPlan
        {
            AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "",
            AutoshotDir = output,
        }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new TimedExit(game, seconds));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }
Console.WriteLine($"[p1041-options] {skin}/{palette} jet={(jet ? "on" : "off")} {width}x{height} -> {output}");

static string FindContent()
{
    // The tool may run from its own bin directory: walk up to the repo root.
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        string candidate = Path.Combine(dir.FullName, "src", "Allegiance.ClientCore.MonoGame", "bin", "Debug", "net10.0", "Content");
        if (Directory.Exists(candidate)) return candidate;
    }
    return Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
}

sealed class TimedExit(Game1 game, int seconds) : GameComponent(game)
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public override void Update(GameTime gameTime)
    {
        if (_clock.Elapsed.TotalSeconds > seconds) game.Exit();
    }
}
