// P762b — real-client evidence for the sector-map overlay fixes. This drives
// the actual Game1 DesktopGL client against a real ephemeral loopback server
// over TCP (exactly the P752/P757 proof-tool shape); it is NOT the
// minimap-proof harness and no renderer state is seeded.
//
// It opens the expanded starmap, then exercises the two native overlay paths:
//   * the CHAT cluster (trekClient.GetChatCluster(), set through the real
//     FM_C_VIEW_CLUSTER request/reply) gets sectorhighlightbmp;
//   * a commander HIGHLIGHT_CLUSTER (sent through the real
//     FM_CS_HIGHLIGHT_CLUSTER relay) gets the flashing cyan sectorEmpty outline
//     on the off-flash phase.
// Two captures ~0.45 s apart straddle the ~400 ms flash phase so one of them
// shows the cyan outline deterministically.
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "docs/reports/P762B_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "P762b map proof", Map = "XXV_simple.igc", Teams = 2,
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
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P762b map pilot",
        WindowWidth = 1280, WindowHeight = 720,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new MapProof(game, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class MapProof(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private bool _finishAfterFlush;
    private short _chatSector = -1, _highlightSector = -1, _selectedSector = -1;

    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object? value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    private void Shot(string name, GameTime gameTime)
    {
        Set("_autoshotDir", output);
        Set("_autoShotName", name);
        Set("_autoShotScheduled", true);
        double ms = gameTime.TotalGameTime.TotalMilliseconds;
        bool flashOn = (((long)ms / 400) & 1) == 0;
        Console.WriteLine($"P762B_SCREEN {name} gameTimeMs={ms:0} flashOn={flashOn}");
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P762b map proof did not finish");
        if (_finishAfterFlush)
        {
            if (!Get<bool>("_autoShotScheduled")) game.Exit();
            return;
        }
        if (Get<object>("_state").ToString() != "Flying" || _watch.Elapsed.TotalSeconds < _next) return;

        var session = Get<ConnectionSession>("_session");
        switch (_step++)
        {
            case 0:
            {
                // Open the real expanded starmap and pick three distinct sectors
                // from the live graph: the chat cluster, a commander-highlighted
                // cluster, and the selected cluster.
                short own;
                lock (session.Gate)
                {
                    own = session.World.Ships[session.World.MyShipId!.Value].SectorId;
                }
                var sectorIds = new List<short>();
                lock (session.Gate)
                {
                    if (session.World.Graph is { } g)
                        foreach (var ce in g.Clusters) sectorIds.Add(ce.Id);
                }
                _chatSector = sectorIds.FirstOrDefault(id => id != own, own);
                _highlightSector = sectorIds.FirstOrDefault(id => id != own && id != _chatSector, own);
                _selectedSector = sectorIds.FirstOrDefault(id => id != own && id != _chatSector && id != _highlightSector, own);

                Set("_minimapExpanded", true);
                Set("_sectorInfoExpanded", false);
                Set("_briefingDismissed", true);
                Set("_selectedSectorId", _selectedSector);
                Set("_starmapHoverSector", (short?)null);
                // Real wire paths: FM_C_VIEW_CLUSTER sets GetChatCluster(), and
                // FM_CS_HIGHLIGHT_CLUSTER is relayed back to this same-side peer.
                session.SendViewClusterRequest(_chatSector);
                session.SendHighlightCluster(_highlightSector, true);
                Console.WriteLine($"P762B-MAP own={own} chat={_chatSector} highlight={_highlightSector} selected={_selectedSector}");
                break;
            }
            case 1:
            {
                bool viewed;
                lock (session.Gate) viewed = session.World.ViewedClusterId == _chatSector;
                if (!viewed) { _step--; break; }   // wait for the real reply
                Shot("sector-map-highlight-a-720p", gameTime);
                break;
            }
            case 2: Shot("sector-map-highlight-b-720p", gameTime); break;   // opposite flash phase
            case 3: Shot("sector-map-highlight-c-720p", gameTime); break;
            case 4: Shot("sector-map-highlight-d-720p", gameTime); break;
            default:
                _finishAfterFlush = true;
                return;
        }
        _next = _watch.Elapsed.TotalSeconds + (_step >= 2 ? 0.21 : 1.0);
    }
}
