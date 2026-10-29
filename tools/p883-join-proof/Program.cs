// Real-window proof: two mission rows on one address, production selected-game
// join, and the selected world's team screen. Run under xvfb-run -a.
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Games;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P883_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string link = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(link)) Directory.CreateSymbolicLink(link, content);
using var server = new Srv();
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0, MaxGames = 2,
    Games = Enumerable.Range(0, 2).Select(i => new GameConfig
    {
        Name = i == 0 ? "Configured game" : "Chosen second game", Map = "XXV_simple.igc", Teams = i + 2,
        CivIds = Enumerable.Repeat(-1, i + 2).ToArray(),
        SideNames = Enumerable.Range(1, i + 2).Select(n => $"Game {i + 1} team {n}").ToArray(),
    }).ToList(),
});
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => server.Loop(stop.Token)); ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    { Intro = true, PlayerName = "P883Pilot", WindowWidth = 1280, WindowHeight = 720 });
    game.Content.RootDirectory = content;
    game.Components.Add(args.Contains("--capacity")
        ? new CapacityProof(game, server, output)
        : new JoinProof(game, server, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class JoinProof(Game1 game, Srv server, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step;
    private double _next;
    private T Get<T>(string field) => (T)typeof(Game1).GetField(field, Hidden)!.GetValue(game)!;
    private void Set(string field, object value) => typeof(Game1).GetField(field, Hidden)!.SetValue(game, value);
    private object? Call(string method, params object[] args) => typeof(Game1).GetMethod(method, Hidden)!.Invoke(game, args);
    private void Style(HudStyle style) => Get<HudSettings>("_hudSettings").Style = style;
    private void Shot(string name)
    { Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true); }
    public override void Update(GameTime time)
    {
        // Autoshot uploads a temporary texture. Force the UI-only frame to rebind
        // its font after that capture instead of inheriting the GL texture binding.
        game.GraphicsDevice.Textures[0] = null;
        if (Get<bool>("_autoShotScheduled")) return;
        if (_watch.Elapsed.TotalSeconds > 45) throw new TimeoutException("P883 window proof timeout");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step)
        {
            case 0:
                if (Get<object>("_state").ToString() != "Intro") return;
                Call("StopIntroScan");
                Set("_introView", Enum.Parse(typeof(Game1).GetNestedType("IntroView", BindingFlags.NonPublic)!, "FindServer"));
                var found = Get<List<DiscoveredServer>>("_introFound"); found.Clear();
                foreach (var mission in server.LiveMissions)
                    found.Add(new(mission.Name, "127.0.0.1", server.BoundPort, DiscoverySource.Lobby, 0,
                        mission.Name, mission.PlayerCount, 20, "P883", "", MissionCookie: mission.Cookie));
                // The list sorts by name; Chosen second game sorts first.
                Set("_introFoundIndex", 0); Style(HudStyle.Classic); break;
            case 1: Shot("games"); break;
            case 2: Style(HudStyle.Modern); break;
            case 3: break; // style-independent; keep only one game-list image
            case 4: Call("JoinIntroFound"); break;
            case 5:
                var session = Get<ConnectionSession>("_session");
                if (session.World.SideRoster?.Sides.Length != 3) return;
                if (Get<uint>("_connectMissionCookie") != server.Missions[1].Cookie)
                    throw new InvalidOperationException("selected game cookie was lost");
                Style(HudStyle.Classic); break;
            case 6: Shot("selected-teams"); break;
            case 7: Style(HudStyle.Modern); break;
            case 8: break; // style-independent; keep only one team-screen image
            case 9: game.Exit(); return;
        }
        _step++; _next = _watch.Elapsed.TotalSeconds + 1.0;
    }
}

// P883b: re-capture only the changed Create Game screen. Output is scratch;
// retain games.png after comparing Classic and Modern pixels byte for byte.
sealed class CapacityProof(Game1 game, Srv server, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private int _step;
    private double _next;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    public override void Update(GameTime time)
    {
        game.GraphicsDevice.Textures[0] = null;
        if (Get<bool>("_autoShotScheduled")) return;
        if (_watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("P883b capacity proof timeout");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step)
        {
            case 0:
                if (Get<object>("_state").ToString() != "Intro") return;
                typeof(Game1).GetMethod("StopIntroScan", Hidden)!.Invoke(game, null);
                Set("_introView", Enum.Parse(typeof(Game1).GetNestedType("IntroView", BindingFlags.NonPublic)!, "CreateGame"));
                var wizard = Get<CreateGameWizard>("_createGame");
                wizard.ApplyCatalog(new CreateGameWizard.Catalog(
                    [new("local", "Local dedicated server", "127.0.0.1", server.BoundPort,
                        "PCore001.igc", "Configured game", server.LiveMissions.Count, 2)],
                    ["PCore001.igc"], ["XXV_simple.igc"]));
                wizard.TypeName("New player game");
                if (wizard.TryCreate() is not null) throw new InvalidOperationException("full server accepted creation");
                Get<HudSettings>("_hudSettings").Style = HudStyle.Classic;
                break;
            case 1:
                Set("_autoshotDir", output); Set("_autoShotName", "capacity-classic"); Set("_autoShotScheduled", true);
                break;
            case 2: Get<HudSettings>("_hudSettings").Style = HudStyle.Modern; break;
            case 3:
                Set("_autoshotDir", output); Set("_autoShotName", "games"); Set("_autoShotScheduled", true);
                break;
            case 4:
                if (!File.ReadAllBytes(Path.Combine(output, "capacity-classic.png")).SequenceEqual(
                    File.ReadAllBytes(Path.Combine(output, "games.png"))))
                    throw new InvalidOperationException("capacity screen differs by HUD style");
                Console.WriteLine("[p883b-proof] Classic and Modern capacity captures are byte-identical");
                game.Exit(); return;
        }
        _step++; _next = _watch.Elapsed.TotalSeconds + 1;
    }
}
