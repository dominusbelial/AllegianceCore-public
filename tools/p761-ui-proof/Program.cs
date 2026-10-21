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
using Allegiance.Core.Wire;
using Allegiance.Lobby;
using Allegiance.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

// P761 — real-client UI evidence for interface wave 10: the team pane's radio
// sort-header bar, the server browser's sort header + find-player + details,
// the ready-check's team configuration controls, and the character sheet's
// profile editor. Drives the real Game1 (+ the real Allegiance.Lobby app for
// the browser phase and the real Allegiance.Server for the in-game phases);
// nothing is seeded into the renderers and no screenshot is composed.
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P761_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

string phase = args.Length > 1 ? args[1] : "lobby";

// ── Phase A: the SERVER BROWSER against the real lobby directory ─────────
Uri lobbyUrl = new Uri("http://127.0.0.1:5000");
if (phase == "lobby")
{
    var builder = WebApplication.CreateBuilder();
    builder.WebHost.UseUrls("http://127.0.0.1:0");
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddSingleton<ServerRegistry>();
    var app = builder.Build();
    app.MapLobbyEndpoints();
    await app.StartAsync();
    lobbyUrl = new Uri(app.Urls.First(u => u.StartsWith("http://")));
    var registry = app.Services.GetRequiredService<ServerRegistry>();
    registry.Register(new RegisterRequest
    {
        Name = "Domino's Fortress", Address = "10.0.0.1", Port = 6073, PlayerCount = 3,
        MissionName = "Wing War", CoreFile = "PCore001.igc", Version = "0.1",
        // P761b — the directory answers find-player queries only for a server
        // that opted in to publishing its pilot names (default off).
        Players = ["PilotOne", "PilotTwo", "PilotThree"],
        PublishPlayers = true,
    });
    registry.Register(new RegisterRequest
    {
        Name = "Quiet Backwater", Address = "10.0.0.2", Port = 6073, PlayerCount = 0,
        MissionName = "Storm the Beaches", CoreFile = "PCore001.igc", Version = "0.1",
    });
    registry.Register(new RegisterRequest
    {
        Name = "Full House", Address = "10.0.0.3", Port = 6073, PlayerCount = 6,
        MissionName = "Conquest XXV", CoreFile = "ACore006.igc", Version = "0.2",
    });

    using var game = new Game1(new Game1.LaunchOptions
    {
        LobbyUrl = lobbyUrl.AbsoluteUri,
        WindowWidth = 1280, WindowHeight = 720,
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new P761LobbyProof(game, output));
    game.Run();
    await app.StopAsync();
}

// ── Phase B: the in-game screens against the real server ─────────────────
if (phase == "game")
{
    using var server = new Srv { SpawnInitialMinersAtBoot = false };
    server.Boot(new ServerConfig
    {
        ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
        UpdatesPerSecond = 20,
        Games = [new GameConfig { Name = "P761 UI proof", Map = "XXV_simple.igc", Teams = 2,
            CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }]
    });
    using var stop = new CancellationTokenSource();
    var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(5); } });
    ticker.Start();
    try
    {
        using var game = new Game1(new Game1.LaunchOptions
        {
            DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P761 UI pilot",
            WindowWidth = 1280, WindowHeight = 720,
            // Ready-check proof: seat and ready, but do NOT launch — the ready-
            // check overlay (with the P761 configuration controls) stays up.
            AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
        });
        game.Content.RootDirectory = content;
        game.Components.Add(new P761GameProof(game, output));
        game.Run();
    }
    finally { stop.Cancel(); ticker.Join(); }
}

Console.WriteLine("P761_UI_PROOF done");

internal static class Refl
{
    public const BindingFlags H = BindingFlags.Instance | BindingFlags.NonPublic;
    public static T Get<T>(object o, string name) => (T)typeof(Game1).GetField(name, H)!.GetValue(o)!;
    public static void Set(object o, string name, object? v) => typeof(Game1).GetField(name, H)!.SetValue(o, v);
    public static object? Call(object o, string name, params object?[] args) =>
        typeof(Game1).GetMethod(name, H | BindingFlags.Static)!.Invoke(o, args);
}

/// <summary>Phase A — the lobby browser: sort header, find player, details.</summary>
sealed class P761LobbyProof(Game1 game, string output) : GameComponent(game)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _finishAfterFlush;
    private bool _findEnterSent;
    private static readonly ActionMap Map = new(InputActionCatalog.Definitions);

    private static readonly string[] SortSteps =
    {
        "lobby-sort-720p", "lobby-findplayer-720p", "lobby-found-720p", "lobby-details-720p",
    };

    private void Shot(string name)
    {
        Refl.Set(game, "_autoshotDir", output);
        Refl.Set(game, "_autoShotName", name);
        Refl.Set(game, "_autoShotScheduled", true);
        Console.WriteLine("P761_SCREEN " + name);
    }

    /// <summary>Drive the real <c>UpdateLobby</c> handler with a synthetic frame:
    /// the exact code path a physical key press takes (P752's proof idiom).</summary>
    private void Press(params Keys[] keys)
    {
        Refl.Call(game, "UpdateLobby", new KeyboardState(keys));
        Refl.Call(game, "UpdateLobby", default(KeyboardState));   // release the edge
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P761 lobby proof did not finish");
        if (_finishAfterFlush)
        {
            if (!(bool)typeof(Game1).GetField("_autoShotScheduled", Refl.H)!.GetValue(game)!)
                game.Exit();
            return;
        }

        bool fetched = Refl.Get<List<ServerListing>>(game, "_displayServers").Count >= 3;
        if (!fetched || _watch.Elapsed.TotalSeconds < _next) return;

        // Act on one tick, shoot on the NEXT (the autoshot flushes at that
        // frame's Draw end, so the captured state is the acted-on state).
        switch (_step++)
        {
            case 0:
                // The header cursor starts on GAME; click it → the game-name
                // sort becomes the stack's last (primary) criterion.
                Press(Keys.Space);
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                break;
            case 1: Shot(SortSteps[0]); _next = _watch.Elapsed.TotalSeconds + 0.5; break;
            case 2:
                Press(Keys.P);   // open the find-player popup
                Refl.Get<System.Text.StringBuilder>(game, "_findBuffer").Append("PilotTwo");
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                break;
            case 3: Shot(SortSteps[1]); _next = _watch.Elapsed.TotalSeconds + 0.5; break;
            case 4:
                // P761b — Enter asks the LOBBY about this one pilot
                // (/api/servers/findplayer); the reply selects the row. Wait for
                // the round trip to land (the popup closes on a hit).
                if (!_findEnterSent) { Press(Keys.Enter); _findEnterSent = true; }
                if (Refl.Get<bool>(game, "_findPlayerActive"))
                {
                    // Stay on this step until the lobby's reply lands.
                    if (_watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("find-player reply never landed");
                    _step--;
                    return;
                }
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                break;
            case 5: Shot(SortSteps[2]); _next = _watch.Elapsed.TotalSeconds + 0.5; break;
            case 6:
                Press(Keys.I);   // the details popup for the selected game
                _next = _watch.Elapsed.TotalSeconds + 0.5;
                break;
            case 7: Shot(SortSteps[3]); _finishAfterFlush = true; break;
        }
    }
}

/// <summary>Phase B — in-game: team pane sort header, ready-check controls,
/// profile editor.</summary>
sealed class P761GameProof(Game1 game, string output) : GameComponent(game)
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _finishAfterFlush;

    private void Shot(string name)
    {
        Refl.Set(game, "_autoshotDir", output);
        Refl.Set(game, "_autoShotName", name);
        Refl.Set(game, "_autoShotScheduled", true);
        Console.WriteLine("P761_SCREEN " + name);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 150) throw new TimeoutException("P761 game proof did not finish");
        if (_finishAfterFlush)
        {
            if (!(bool)typeof(Game1).GetField("_autoShotScheduled", Refl.H)!.GetValue(game)!)
                game.Exit();
            return;
        }

        string state = Refl.Get<object>(game, "_state").ToString() ?? "?";
        if (_watch.Elapsed.TotalSeconds < _next) return;
        var session = Refl.Get<ConnectionSession>(game, "_session");

        switch (_step++)
        {
            case 0 when state == "ReadyCheck":
            {
                // Wait until seated AND commanding (sole pilot of the side).
                lock (session.Gate)
                    if (!session.World.IsTeamLeader) { _step--; return; }
                // Toggle AUTO-ACCEPT through the real handler (U): the server's
                // authoritative echo flips the checkbox before the shot.
                Refl.Call(game, "UpdateReadyCheck", new KeyboardState(Keys.U));
                Refl.Call(game, "UpdateReadyCheck", default(KeyboardState));
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            }
            case 1 when state == "ReadyCheck":
                Shot("readycheck-controls-720p");
                _next = _watch.Elapsed.TotalSeconds + 1.0;
                break;
            case 2:
            {
                if (state != "Flying") { _step--; return; }   // waits for the launch
                var sessionW = session.World;
                lock (session.Gate)
                {
                    bool docked = sessionW.MyShipId is { } me
                        && sessionW.Ships.TryGetValue(me, out var own) && own.StationId != -1;
                    if (docked)
                    {
                        // The autoproof seat leaves us in the hangar — launch
                        // through the REAL docked-screen handler (ShopLaunch/L).
                        Refl.Call(game, "UpdateFlying", new KeyboardState(Keys.L), gameTime);
                        _step--;   // retry next tick until actually in space
                        return;
                    }
                }
                Refl.Set(game, "_teamPaneVisible", true);
                // P761c — issue a real station goto through the real wire path so
                // the row's OBJECTIVE cell shows the relayed text the sort reads
                // (and the own ship's header uses MyAcceptedCommand, teampane.cpp:
                // 1660-1676). Nothing is seeded into the renderer.
                lock (session.Gate)
                {
                    if (sessionW.MyShipId is { } me && sessionW.Stations.Count > 0)
                    {
                        var station = sessionW.Stations.Values.First();
                        session.SendOrderChange(me, OrderChange.CidGoto, OrderChange.CmdAccepted,
                            IgcObjectType.Station, station.StationId);
                    }
                }
                // Advance the radio header via the real controller (P752's
                // Step idiom): Leader → NAME.
                var controls = Refl.Get<object>(game, "_teamPaneControls");
                var ks = new KeyboardState(Keys.S, Keys.LeftControl);
                var map = KeyBindingStore.Instance.Map;
                controls.GetType().GetMethod("Step", Refl.H | BindingFlags.Public)!
                    .Invoke(controls, [ks, map, session.World, true, false, null]);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            }
            case 3:
                Shot("teampane-sort-720p");
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            case 4:
                // The character sheet with the profile editor mid-edit (the
                // team pane closes so the sheet is not covered by it).
                Refl.Set(game, "_teamPaneVisible", false);
                Refl.Set(game, "_pilotInfoVisible", true);
                var profile = Refl.Get<object>(game, "_pilotProfile");
                profile.GetType().GetMethod("BeginEdit", Refl.H | BindingFlags.Public)!.Invoke(profile, null);
                foreach (var c in "flying PCore001 since the parity waves")
                    profile.GetType().GetMethod("Append", Refl.H | BindingFlags.Public)!.Invoke(profile, [c]);
                _next = _watch.Elapsed.TotalSeconds + 0.8;
                break;
            case 5:
                Shot("pilotinfo-profile-720p");
                _finishAfterFlush = true;
                break;
            default:
                _step--;
                return;
        }
    }
}

internal sealed record P761Shot(string Name, short? Ship, short? Side, string State, short? Winner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<P761Shot>), TypeInfoPropertyName = "Shots")]
internal partial class P761JsonContext : JsonSerializerContext;
