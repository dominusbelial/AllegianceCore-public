// Real Game1, real TCP briefing from a server configured with native goals.
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Microsoft.Xna.Framework;
using Srv = Allegiance.Server.Server;

string output = Path.GetFullPath(args.FirstOrDefault() ?? "docs/reports/P832A_assets");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    Games = [new() { Name = "P832a goals", Map = "XXV_simple.igc", Teams = 2, CivIds = [25, 3],
        SideNames = ["Alpha", "Bravo"], AutoStart = false,
        Goals = new() { ConquestPercentage = 73, Flags = 4, Artifacts = 7, TeamKills = 21,
            GameLengthSeconds = 600, TeamMoney = 2.5f } }]
});
server.Missions[0].Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(50); } });
ticker.Start();
try
{
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P832a pilot",
        WindowWidth = 1600, WindowHeight = 1000,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true }
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new GoalProof(game, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

sealed class GoalProof(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double _next;
    private int _step;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    public override void Update(GameTime time)
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P832a goal briefing did not arrive");
        if (_watch.Elapsed.TotalSeconds < _next) return;
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            if (Get<object>("_state").ToString() != "Flying" || session.World.MissionBriefing is not { } info) return;
            if (info.ConquestPercent != 73 || info.FlagsGoal != 4 || info.ArtifactGoal != 7 || info.KillLimit != 21 || info.ProsperityMoney <= 0)
                throw new InvalidOperationException("Configured goal values did not arrive over TCP");
            switch (_step++)
            {
                case 0:
                    Get<HudSettings>("_hudSettings").Style = HudStyle.Classic;
                    typeof(Game1).GetMethod("ActivateHangar", Hidden)!.Invoke(game, [HangarAction.Launch]);
                    Console.WriteLine($"P832A_WIRE goals={session.World.MissionGoals} prosperityMoney={info.ProsperityMoney}");
                    break;
                case 1:
                    if (Get<bool>("_dockedActive"))
                    {
                        typeof(Game1).GetMethod("ActivateHangar", Hidden)!.Invoke(game, [HangarAction.Launch]);
                        _step = 1; break;
                    }
                    Set("_briefingDismissed", false);
                    break;
                case 2: Shot("briefing-goals-classic"); break;
                case 3: Get<HudSettings>("_hudSettings").Style = HudStyle.Modern; break;
                case 4: Shot("briefing-goals-modern"); break;
                default: game.Exit(); break;
            }
        }
        _next = _watch.Elapsed.TotalSeconds + 1;
    }
    private void Shot(string name)
    {
        Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
        Console.WriteLine("P832A_SCREEN " + name);
    }
}
