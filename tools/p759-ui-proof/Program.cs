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
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Srv = Allegiance.Server.Server;

// P759 real-client UI proof. Drives the real Game1 + a real loopback server over
// TCP; no replacement renderer, seeded screen rows, or screenshot composition.
string output = Path.GetFullPath(args.FirstOrDefault() ?? "docs/reports/P759_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);

using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "P759 UI proof", Map = "XXV_simple.igc", Teams = 2,
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
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P759 UI pilot",
        WindowWidth = 1280, WindowHeight = 720,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new UiProof(game, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class UiProof(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private readonly List<UiProofShot> _shots = [];
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private void Activate(HangarAction action) => typeof(Game1).GetMethod("ActivateHangar", Hidden)!.Invoke(game, [action]);
    private static object State(string name) => Enum.Parse(typeof(Game1).GetNestedType("ClientState", BindingFlags.NonPublic)!, name);

    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            _shots.Add(new UiProofShot(name, session.World.MyShipId,
                session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var s) ? s.StationId : (short)-1,
                Get<HangarAction>("_hangarHover").ToString()));
        }
        Console.WriteLine("P759_SCREEN " + name);
    }

    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 120) throw new TimeoutException("P759 real-client UI proof did not finish");
        if (_watch.Elapsed.TotalSeconds < _next) return;

        switch (_step++)
        {
            case 0:
                // Wait until docked so the hangar button row is drawn.
                if (!Get<bool>("_dockedActive") || Get<object>("_state").ToString() != "Flying") { _step = 0; return; }
                Mouse.SetPosition(200, 663);   // hover the Buy button (Launch=0, Buy=1)
                break;
            case 1: Shot("hangar-buy-default-720p"); break;

            case 2:
                // Launch into flight, then reveal the LOD slider overlay.
                Activate(HangarAction.Launch);
                break;
            case 3:
                if (Get<bool>("_dockedActive")) { _step = 2; return; }
                Get<LodControl>("_lodControl").Visible = true;
                // P759b — the LOD-slider capture must not have the briefing
                // overlay up (it auto-showed on join): latch the dismissal.
                Set("_briefingDismissed", true);
                break;
            case 4: Shot("lod-slider-720p"); break;

            case 5:
                // Show the civ-generated mission briefing over the live world,
                // with the LOD slider hidden again.
                Set("_briefingDismissed", false);
                Get<LodControl>("_lodControl").Visible = false;
                var session = Get<ConnectionSession>("_session");
                lock (session.Gate)
                {
                    session.World.Apply(new SetBriefingText { Text = "Serpentis", FGenerateCivBriefing = true });
                    session.World.Apply(new MissionBriefingInfo
                    {
                        SideName = "Alpha", CivName = "Iron Coalition", CivIconName = "ironleague",
                        BriefingText = "Serpentis",
                        CivList = ["Iron Coalition", "Belters"], NoWin = false,
                        Conquest = true, ConquestPercent = 65,
                        Artifacts = true, ArtifactGoal = 4,
                    });
                }
                break;
            case 6: Shot("briefing-civ-720p"); break;

            case 7:
                // Connect-failure screen (the production DrawConnectFailed path).
                Set("_state", State("ConnectFailed"));
                Set("_connectFailureReason", "Could not join 127.0.0.1:9 - connection refused");
                break;
            case 8: Shot("connect-failed-720p"); break;

            default:
                File.WriteAllText(Path.Combine(output, "client-proof.json"), JsonSerializer.Serialize(_shots, UiProofJsonContext.Default.Shots));
                game.Exit(); return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}

internal sealed record UiProofShot(string Name, short? Ship, short Station, string Hover);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<UiProofShot>), TypeInfoPropertyName = "Shots")]
internal partial class UiProofJsonContext : JsonSerializerContext;
