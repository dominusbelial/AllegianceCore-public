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

// Run from the repository root. This drives the actual Game1 and server over TCP;
// no replacement renderer, seeded screen rows, or screenshot composition.
string output = Path.GetFullPath(args.FirstOrDefault() ?? "docs/reports/P748_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    UpdatesPerSecond = 20,
    Games = [new GameConfig { Name = "P748 UI proof", Map = "XXV_simple.igc", Teams = 2,
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
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P748 UI pilot",
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
    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var model = Get<InvestmentPaneModel?>("_investModel");
            _shots.Add(new UiProofShot(name, session.World.MyShipId, session.World.Ships[session.World.MyShipId!.Value].StationId,
                Get<HangarAction>("_hangarHover").ToString(), model?.SelectedGroup,
                model?.Items.Select(b => new UiProofBucket(b.Id, b.Name, b.GroupId)).ToArray()));
        }
        Console.WriteLine("P748_SCREEN " + name);
    }
    public override void Update(GameTime gameTime)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P748 real-client UI proof did not finish");
        if (!Get<bool>("_dockedActive") || Get<object>("_state").ToString() != "Flying" || _watch.Elapsed.TotalSeconds < _next) return;
        switch (_step++)
        {
            case 0: Mouse.SetPosition(95, 660); break;
            case 1: Shot("hangar-hover-720p"); break;
            case 2: Activate(HangarAction.Invest); Set("_investShowAll", true); break;
            case 3: Shot("investment-starbase-720p"); break;
            case 4: Set("_investGroup", 1); Set("_investCursor", 0); break;
            case 5: Shot("investment-garrison-720p"); break;
            case 6: Activate(HangarAction.Command); break;
            case 7: Shot("hangar-command-720p"); break;
            case 8: Activate(HangarAction.Loadout); Activate(HangarAction.Help); break;
            case 9: Shot("hangar-help-720p"); break;
            case 10: Set("_helpVisible", false); Activate(HangarAction.Team); break;
            case 11: Shot("hangar-team-720p"); break;
            case 12: Activate(HangarAction.Team); Activate(HangarAction.Exit); break;
            case 13: Shot("hangar-quit-720p"); break;
            default:
                File.WriteAllText(Path.Combine(output, "client-proof.json"), JsonSerializer.Serialize(_shots, UiProofJsonContext.Default.Shots));
                game.Exit(); return;
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
}

internal sealed record UiProofBucket(short Id, string Name, int GroupId);
internal sealed record UiProofShot(string Name, short? Ship, short Station, string Hover, int? Group, UiProofBucket[]? Buckets);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(List<UiProofShot>), TypeInfoPropertyName = "Shots")]
internal partial class UiProofJsonContext : JsonSerializerContext;
