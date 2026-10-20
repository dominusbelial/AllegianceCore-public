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
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

// P752 — real-client UI evidence for the interface wave 7 (P752) visible
// changes: the expanded/collapsed sector-info pane, the team pane's selected
// pilot row, and the game-over side/player tables. This drives the actual
// Game1 + server over TCP; nothing is seeded into the renderer and no
// screenshot is composed.
string output = Path.GetFullPath(args.FirstOrDefault() ?? "docs/reports/P752_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "P752 UI proof", Map = "XXV_simple.igc", Teams = 2,
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
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P752 UI pilot",
        WindowWidth = 1280, WindowHeight = 720,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new P752UiProof(game, server, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class P752UiProof(Game1 game, Srv server, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _pendingGameOverShot;
    private bool _finishAfterFlush;
    private readonly List<P752Shot> _shots = [];

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    private void Shot(string name)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var w = session.World;
            _shots.Add(new P752Shot(name, w.MyShipId, w.MySideId, Get<object>("_state").ToString() ?? "?",
                w.GameOverResult?.SideWinner));
        }
        Console.WriteLine("P752_SCREEN " + name);
    }

    private void Finish()
    {
        File.WriteAllText(Path.Combine(output, "client-proof.json"),
            JsonSerializer.Serialize(_shots, P752JsonContext.Default.Shots));
        game.Exit();
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P752 real-client UI proof did not finish");

        // Wait for the final autoshot to actually flush to disk (it is written
        // at Draw end) before exiting, otherwise the game-over PNG is lost.
        if (_finishAfterFlush)
        {
            if (!Get<bool>("_autoShotScheduled")) Finish();
            return;
        }

        // Game-over capture is state-independent: the panel stays up through the
        // P439/P480 rotation into the ReadyCheck intermission, so poll for the
        // real GAME_OVER(62) receipt and shoot the instant it lands.
        if (_pendingGameOverShot)
        {
            var session = Get<ConnectionSession>("_session");
            bool hasResult;
            lock (session.Gate) hasResult = session.World.GameOverResult is not null;
            if (hasResult)
            {
                Shot("gameover-tables-720p");
                _pendingGameOverShot = false;
                _finishAfterFlush = true;
            }
            return;
        }

        if (Get<object>("_state").ToString() != "Flying" || _watch.Elapsed.TotalSeconds < _next) return;

        switch (_step++)
        {
            case 0:
            {
                short? ownSector = null;
                var session = Get<ConnectionSession>("_session");
                lock (session.Gate)
                {
                    var w = session.World;
                    if (w.MyShipId is { } me && w.Ships.TryGetValue(me, out var own))
                        ownSector = own.SectorId;
                }
                Set("_minimapExpanded", true);
                Set("_sectorInfoExpanded", true);
                Set("_selectedSectorId", ownSector);
                break;
            }
            case 1: Shot("sectorinfo-expanded-720p"); break;
            case 2: Set("_sectorInfoExpanded", false); break;
            case 3: Shot("sectorinfo-collapsed-720p"); break;
            case 4:
            {
                Set("_minimapExpanded", false);
                Set("_teamPaneVisible", true);
                var controls = Get<object>("_teamPaneControls");
                var map = KeyBindingStore.Instance.Map;
                var ks = new KeyboardState(Keys.PageDown);
                var session = Get<ConnectionSession>("_session");
                lock (session.Gate)
                    controls.GetType()
                        .GetMethod("Step", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                        .Invoke(controls, [ks, map, session.World, true, false]);
                break;
            }
            case 5: Shot("teampane-selection-720p"); break;
            case 6:
                typeof(Srv)
                    .GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(server, [server.Missions[0], (short)0, "conquest"]);
                _pendingGameOverShot = true;
                break;
            default:
                Finish();
                return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1.0;
    }
}

internal sealed record P752Shot(string Name, short? Ship, short? Side, string State, short? Winner);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<P752Shot>), TypeInfoPropertyName = "Shots")]
internal partial class P752JsonContext : JsonSerializerContext;
