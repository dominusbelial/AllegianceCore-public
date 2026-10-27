// P856 — REAL-WINDOW screenshots of the client's GAME DETAILS panel showing the
// two native create-game options (native "Allow Friendly Fire: Yes/No",
// WinTrek/newgamescreen.cpp:347) as published by a real Allegiance.Lobby child
// process. Both HUD styles are captured from the same shared details model.
//
// Run from the repository root (build the lobby first):
//   dotnet build src/Allegiance.Lobby
//   dotnet run --project tools/p856-options-proof -- docs/reports/P856_assets
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "docs/reports/P856_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
if (!Directory.Exists(Path.Combine(AppContext.BaseDirectory, "Content")))
    Directory.CreateSymbolicLink(Path.Combine(AppContext.BaseDirectory, "Content"), content);

// ── skirmish setup lane: the two new option rows, both HUD styles ──────────
if (args.Contains("--skirmish"))
{
    using var skirmishGame = new Game1(new Game1.LaunchOptions
    {
        Intro = true, PlayerName = "P856 skirmish pilot", WindowWidth = 1280, WindowHeight = 720,
    });
    typeof(Game1).GetField("_splashStartSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(skirmishGame, 1e9);
    skirmishGame.Content.RootDirectory = content;
    skirmishGame.Components.Add(new SkirmishRowsProof(skirmishGame, output));
    skirmishGame.Run();
    return;
}

int lobbyPort = 5178;
string lobbyDll = Path.GetFullPath("src/Allegiance.Lobby/bin/Debug/net10.0/Allegiance.Lobby.dll");
if (!File.Exists(lobbyDll))
    throw new FileNotFoundException($"build the lobby first (dotnet build src/Allegiance.Lobby): {lobbyDll}");

var lobby = Process.Start(new ProcessStartInfo("dotnet", $"\"{lobbyDll}\" --urls http://127.0.0.1:{lobbyPort}")
{
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
}) ?? throw new InvalidOperationException("could not start the lobby");
try
{
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{lobbyPort}") };
    var ready = false;
    for (int i = 0; i < 100 && !ready; i++)
    {
        try { ready = (await http.GetAsync("/healthz")).IsSuccessStatusCode; }
        catch (HttpRequestException) { await Task.Delay(200); }
    }
    if (!ready) throw new InvalidOperationException("lobby never became healthy");

    // One game with both P856 options ON and one with the native defaults OFF.
    async Task Register(string name, bool friendlyFire, bool alliedViz, int players)
    {
        var resp = await http.PostAsJsonAsync("/api/servers/register", new
        {
            name, address = "10.0.0.1", port = 6777, playerCount = players, maxPlayers = 16,
            missionName = name, coreFile = "PCore001.igc", version = "0.1.0",
            game = new
            {
                allowDevelopments = true, lives = 0x7fff, scoresCount = true,
                objectModelCreated = true, inProgress = players > 0,
                goalConquest = true, goalDeathmatch = false, goalProsperity = false,
                goalCountdown = false, goalArtifacts = false, goalFlags = false,
                goalTerritory = false, minRank = -1, maxRank = 1000,
                allowFriendlyFire = friendlyFire, allowAlliedViz = alliedViz,
            },
        });
        resp.EnsureSuccessStatusCode();
    }
    await Register("Options ON", friendlyFire: true, alliedViz: true, players: 4);
    await Register("Options OFF", friendlyFire: false, alliedViz: false, players: 0);

    using var game = new Game1(new Game1.LaunchOptions
    {
        PlayerName = "P856 browser pilot",
        LobbyUrl = $"http://127.0.0.1:{lobbyPort}",
        WindowWidth = 1280, WindowHeight = 720,
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new DetailsProof(game, output));
    game.Run();
}
finally
{
    try { lobby.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
}

/// <summary>The Single Player > Skirmish setup screen (the port's create-game
/// screen): rows "Friendly fire" and "Allied visibility" (P856) in both HUD
/// styles.</summary>
sealed class SkirmishRowsProof(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        Console.WriteLine("P856_SCREEN " + name);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P856 skirmish rows proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0:
                // Release the held splash so the production timeout reaches the intro.
                Set("_splashStartSeconds", Get<double>("_nowSeconds"));
                break;
            case 1:
                if (Get<object>("_state").ToString() != "Intro") { _step = 1; break; }
                // Deterministic defaults (a remembered profile could differ).
                Get<Allegiance.SinglePlayer.SkirmishScreenModel>("_skirmishScreen")
                    .ApplyRemembered(new Allegiance.SinglePlayer.SkirmishPrefs());
                var view = typeof(Game1).GetNestedType("IntroView", BindingFlags.NonPublic)!;
                typeof(Game1).GetField("_introView", Hidden)!.SetValue(game, Enum.Parse(view, "SkirmishSetup"));
                Get<HudSettings>("_hudSettings").Style = HudStyle.Classic;
                break;
            case 2:
                Shot("skirmish-setup-options-classic");
                break;
            case 3:
                Get<HudSettings>("_hudSettings").Style = HudStyle.Modern;
                break;
            case 4:
                Shot("skirmish-setup-options-modern");
                break;
            default:
                game.Exit();
                return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1.5;
    }
}

sealed class DetailsProof(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private object? Call(string name, params object[] args) => typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);
    private IReadOnlyList<ServerListing> Servers => Get<List<ServerListing>>("_servers");

    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        Console.WriteLine("P856_SCREEN " + name);
    }

    /// <summary>Select the listing whose mission name matches and open the panel.</summary>
    private void OpenDetails(string mission)
    {
        Call("RebuildVisibleRows");
        var visible = Get<List<int>>("_visibleRows");
        var servers = Servers;
        for (int i = 0; i < visible.Count; i++)
            if (servers[visible[i]].MissionName == mission) { Set("_selectedIndex", i); break; }
        Set("_detailsActive", true);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P856 details proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0:
                if (Servers.Count < 2) { _step = 0; break; }   // wait for the real fetch
                Get<HudSettings>("_hudSettings").Style = HudStyle.Classic;
                OpenDetails("Options ON");
                break;
            case 1:
                Shot("game-details-options-on-classic");
                break;
            case 2:
                Get<HudSettings>("_hudSettings").Style = HudStyle.Modern;
                break;
            case 3:
                Shot("game-details-options-on-modern");
                break;
            case 4:
                OpenDetails("Options OFF");
                break;
            case 5:
                Shot("game-details-options-off-modern");
                break;
            default:
                game.Exit();
                return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1.5;
    }
}
