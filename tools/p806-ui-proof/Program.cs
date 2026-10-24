// P806 — REAL-WINDOW screenshots for the wave's screen work, driven through the
// production Game1 (no replacement renderer, no seeded rows composed by hand):
//
//   intro mode (--intro): the Options menu TREE — root, Sound Options submenu,
//   Game Options submenu and its Chat/Mute/Filter submenu (native
//   WinTrek.cpp:4429-4442, 4502-4590).
//
//   lobby mode (default): a REAL Allegiance.Lobby child process with three
//   MIXED games registered over its real HTTP API, then the client's server
//   browser (GET /api/servers) showing the eight-column sort header, the
//   FILTER DIALOG with real settings toggled and the details panel with the
//   published game metadata (native gamescreen.cpp:351-529, 858-898, 1308-1326).
//
//   ready mode (--ready): a real server with a NotStarted mission + a seated
//   client, so the team-lobby CHAT PANE (new in P806) is on screen. Chat lines
//   arrive through the production receive path (ClientWorld.Apply(CHATMESSAGE)),
//   and both HUD styles are captured (Classic + Modern).
//
// Run from the repository root:
//   dotnet run --project tools/p806-ui-proof -- docs/reports/P806_assets
using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "docs/reports/P806_assets");
bool introMode = args.Contains("--intro");
bool readyMode = args.Contains("--ready");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

if (introMode)
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        Intro = true, PlayerName = "P806 pilot", WindowWidth = 1280, WindowHeight = 720,
    });
    // Hold the splash until the menu shots are queued.
    typeof(Game1).GetField("_splashStartSeconds", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(game, 1e9);
    game.Content.RootDirectory = content;
    game.Components.Add(new OptionsProof(game, output));
    game.Run();
    return;
}

if (readyMode)
{
    using var server = new Srv { SpawnInitialMinersAtBoot = false };
    server.Boot(new Allegiance.Server.ServerConfig
    {
        ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,
        Games = [new Allegiance.Server.GameConfig { Name = "P806 team lobby", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
    });
    // Deliberately NOT started: the client stays on the READY CHECK (team lobby).
    using var stop = new CancellationTokenSource();
    var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
    ticker.Start();
    try
    {
        using var game = new Game1(new Game1.LaunchOptions
        {
            DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P806 lobby pilot",
            WindowWidth = 1280, WindowHeight = 720,
            AutoProof = new Allegiance.ClientCore.MonoGame.LiveProof.AutoProofPlan
            {
                AutoSide = 0, AutoReady = false, AutoLaunchRequested = false,
            },
        });
        game.Content.RootDirectory = content;
        game.Components.Add(new ReadyChatProof(game, output));
        game.Run();
    }
    finally { stop.Cancel(); ticker.Join(); }
    return;
}

// ── lobby browser lane: real lobby process + registered mixed games ─────────
int lobbyPort = 5177;
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

    // Three MIXED games, exactly the metadata the native list filters on.
    async Task Register(string name, string mission, int players, int max, object game)
    {
        var resp = await http.PostAsJsonAsync("/api/servers/register", new
        {
            name, address = "10.0.0.1", port = 6777, playerCount = players, maxPlayers = max,
            missionName = mission, coreFile = "PCore001.igc", version = "0.1.0", game,
        });
        resp.EnsureSuccessStatusCode();
    }
    await Register("alpha", "Conquest Arena", 8, 8, new
    {
        allowDevelopments = true, lives = 0x7fff, scoresCount = true, objectModelCreated = true,
        inProgress = true, goalConquest = true, goalDeathmatch = false, goalProsperity = false,
        goalCountdown = false, goalArtifacts = false, goalFlags = false, goalTerritory = false,
        minRank = -1, maxRank = 1000,
    });
    await Register("bravo", "Territory War", 2, 20, new
    {
        allowDevelopments = true, lives = 0x7fff, scoresCount = true, objectModelCreated = true,
        inProgress = false, goalConquest = false, goalDeathmatch = false, goalProsperity = false,
        goalCountdown = false, goalArtifacts = false, goalFlags = false, goalTerritory = true,
        minRank = 3, maxRank = 9,
    });
    await Register("charlie", "Flag Run", 0, 10, new
    {
        allowDevelopments = false, lives = 3, scoresCount = true, objectModelCreated = true,
        inProgress = false, goalConquest = false, goalDeathmatch = false, goalProsperity = false,
        goalCountdown = true, goalArtifacts = false, goalFlags = true, goalTerritory = false,
        minRank = -1, maxRank = 1000,
    });

    using var game = new Game1(new Game1.LaunchOptions
    {
        PlayerName = "P806 browser pilot",
        LobbyUrl = $"http://127.0.0.1:{lobbyPort}",
        WindowWidth = 1280, WindowHeight = 720,
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new BrowserProof(game, output));
    game.Run();
}
finally
{
    try { lobby.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
}

// ═══════════════════════════════════════════════════════════════════════════
// Intro options menu tree
// ═══════════════════════════════════════════════════════════════════════════
sealed class OptionsProof(Game1 game, string output) : GameComponent(game)
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
        Console.WriteLine("P806_SCREEN " + name);
    }

    private OptionsMenuModel Menu => Get<OptionsMenuModel>("_optionsMenu");
    private OptionsMenuModel.OptionsState State() =>
        (OptionsMenuModel.OptionsState)Call("CurrentOptionsState")!;
    private void OpenSubmenu(int row)
    {
        Menu.Row0 = row;
        Menu.Activate(State());                      // the handler's submenu descent
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P806 intro options proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0:
                // Splash is held; release it so the intro (and its Options entry)
                // is reached through the production timeout.
                Set("_splashStartSeconds", Get<double>("_nowSeconds"));
                break;
            case 1:
                if (Get<object>("_state").ToString() != "Intro")
                    throw new InvalidOperationException("splash did not time out to the intro");
                Set("_introIndex", 4);              // Options (native optionsButtonPane)
                Call("ActivateIntroMain");
                break;
            case 2:
                Shot("options-root-720p");           // four native groups
                break;
            case 3:
                OpenSubmenu(row: 1);                 // Sound Options >
                break;
            case 4:
                Shot("options-sound-submenu-720p");
                break;
            case 5:
                Menu.Back();                         // back to the root (native OnCancel)
                OpenSubmenu(row: 2);                 // Game Options >
                break;
            case 6:
                Shot("options-game-submenu-720p");
                break;
            case 7:
                // Game Options → Chat/Mute/Filter > (nested one level deeper).
                Menu.Row0 = 0;
                Menu.Activate(State());
                break;
            case 8:
                Shot("options-chat-submenu-720p");
                break;
            default:
                game.Exit();
                return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Server browser (real lobby, real fetch)
// ═══════════════════════════════════════════════════════════════════════════
sealed class BrowserProof(Game1 game, string output) : GameComponent(game)
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
        Console.WriteLine("P806_SCREEN " + name);
    }

    private IReadOnlyList<ServerListing> Servers => Get<List<ServerListing>>("_servers");

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P806 browser proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0:
                if (Servers.Count < 3)
                {
                    _step = 0;                       // wait for the real fetch
                    break;
                }
                // Sort by status then open the details panel on the running game.
                var sort = Get<LobbyGameSort>("_lobbySort");
                sort.CursorIndex = 0;                // STATUS column
                sort.Click(LobbyGameSort.ColStatus);
                Call("RebuildVisibleRows");
                Set("_detailsActive", true);
                break;
            case 1:
                Shot("server-list-header-details-720p");
                break;
            case 2:
                // The native FilterDialogPopup with real settings selected.
                var dialog = Get<LobbyFilterDialog>("_filterDialog");
                dialog.Open(Get<LobbyGameFilters>("_gameFilters"));
                dialog.MoveRow(4);
                dialog.Cycle(4);                     // conquest → Yes
                dialog.MoveRow(10);
                dialog.CycleBackward(10);            // territorial → No
                break;
            case 3:
                Shot("server-list-filter-dialog-720p");
                break;
            case 4:
                // Commit through the production path so the filtered list is drawn.
                var dialog2 = Get<LobbyFilterDialog>("_filterDialog");
                dialog2.Commit(Get<LobbyGameFilters>("_gameFilters"));
                Call("RebuildVisibleRows");
                break;
            case 5:
                Shot("server-list-filtered-720p");
                break;
            default:
                game.Exit();
                return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}

// ═══════════════════════════════════════════════════════════════════════════
// Team-lobby chat pane, both HUD styles
// ═══════════════════════════════════════════════════════════════════════════
sealed class ReadyChatProof(Game1 game, string output) : GameComponent(game)
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
        Console.WriteLine("P806_SCREEN " + name);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P806 ready-chat proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        var session = Get<ConnectionSession>("_session");
        var world = session.World;
        switch (_step++)
        {
            case 0:
                if (Get<object>("_state").ToString() != "ReadyCheck" || world.MyShipId is not { } me)
                {
                    _step = 0;                       // wait for the seat
                    break;
                }
                // Real receive path: the server relay is a CHATMESSAGE apply.
                world.Apply(new ChatMessage { Message = "all pilots: form up", ChatTarget = (int)ChatTarget.Everyone, SidSender = me });
                world.Apply(new ChatMessage { Message = "alpha team: ready up", ChatTarget = (int)ChatTarget.Team, SidSender = me });
                world.Apply(new ChatMessage { Message = "private: check your loadout", ChatTarget = (int)ChatTarget.Individual, OidRecipient = me, SidSender = me });
                world.Apply(new ChatMessage { Message = "admin: server restarts in 5", ChatTarget = (int)ChatTarget.Admin, SidSender = me });
                Call("ApplyChatPaneFilter");     // native lobby channel set (Everyone)
                break;
            case 1:
                // Everyone chat is filtered by the native default
                // (m_bFilterChatsToAll); the pane shows the Individual + Admin
                // channels of the native lobby set.
                Shot("lobby-chat-classic-720p");
                break;
            case 2:
                // Native chat-target button -> Team: re-applies the channel set
                // [Team, Individual, IndividualNoFilter, Admin] and switches to
                // the Modern HUD style for the second capture.
                Set("_lobbyChatTarget", ChatTarget.Team);
                Call("ApplyChatPaneFilter");
                var settings = Get<Allegiance.ClientCore.MonoGame.Rendering.HudSettings>("_hudSettings");
                settings.Style = Allegiance.ClientCore.MonoGame.Rendering.HudStyle.Modern;
                break;
            case 3:
                Shot("lobby-chat-modern-720p");
                break;
            default:
                game.Exit();
                return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}
