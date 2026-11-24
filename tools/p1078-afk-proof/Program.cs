// Actual Game1 windows and production team-pane renderers, both styles. The idle
// peer uses the shared native activity policy and real PLAYER_READY over TCP.
using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.LiveProof;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Microsoft.Xna.Framework;

string output = Path.GetFullPath(args.FirstOrDefault() ?? ".scratch/p1078/captures");
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
using var server = new Allegiance.Server.Server();
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Require(ExternalResourceKind.Artwork), Port = 0,
    SideSelectAutoSeatSeconds = 30,
    Games = [new() { Name = "AFK proof", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
        SideNames = ["Alpha", "Bravo"], AutoStart = false, InitialMinersPerTeam = 0, MaxTeamImbalance = 10 }],
});
server.Missions[0].Stage = MissionStage.Started;
using var stop = new CancellationTokenSource();
var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(25); } });
ticker.Start();
try
{
    using var idle = new ConnectionSession();
    idle.Connect(server.BoundPort, "IdlePilot");
    idle.SendLogonRequest(new() { CharacterName = "IdlePilot", FedsrvVer = LogonRequest.MsgVer, Time = 1 });
    Wait(() => idle.Ack is { FValidated: true });
    idle.SendPositionReq(0);
    Wait(() => idle.World.MyShipId is not null);
    var activity = new PlayerActivity();
    activity.Update(0, false, true, true);
    idle.SendPlayerReady(activity.Update(180.001, false, true, false)!.Value, idle.World.MyShipId!.Value);
    Wait(() => idle.World.Roster.ReadyByShipId.TryGetValue(idle.World.MyShipId.Value, out bool ready) && !ready);
    using var game = new Game1(new Game1.LaunchOptions
    {
        DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "ActivePilot",
        WindowWidth = 1600, WindowHeight = 900,
        AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" },
    });
    game.Content.RootDirectory = content;
    game.Components.Add(new AfkCapture(game, output));
    game.Run();
}
finally { stop.Cancel(); ticker.Join(); }

static void Wait(Func<bool> done)
{
    long deadline = Environment.TickCount64 + 30000;
    while (!done() && Environment.TickCount64 < deadline) Thread.Sleep(10);
    if (!done()) throw new TimeoutException("AFK proof wire deadline");
}

sealed class AfkCapture(Game1 game, string output) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private int _step;
    private double _next;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    public override void Update(GameTime time)
    {
        if (_clock.Elapsed.TotalSeconds > 120) throw new TimeoutException("AFK capture deadline");
        if (_clock.Elapsed.TotalSeconds < _next || Get<object>("_state").ToString() != "Flying") return;
        if (Get<bool>("_autoShotScheduled")) return;
        var session = Get<ConnectionSession>("_session");
        lock (session.Gate)
        {
            var world = session.World;
            var idle = world.Ships.Values.FirstOrDefault(s => s.CharacterName == "IdlePilot");
            if (idle is null || !world.Roster.ReadyByShipId.TryGetValue(idle.ShipId, out bool ready) || ready) return;
            var briefs = world.Ships.Values.Where(s => s.CharacterName is not null)
                .ToDictionary(s => s.CharacterName!, s => TeamPaneLayout.BriefOf(world, s, _ => ""));
            var lines = TeamPaneLayout.Build(world.ScoreRoster, world.RankTable, briefs, null, null, null, false, "ActivePilot");
            if (!lines.Any(l => l.Text.Contains("IdlePilot") && l.Text.Contains("[AFK]")))
                throw new InvalidOperationException("Real roster must contain IdlePilot [AFK]");
        }
        Set("_teamPaneVisible", true);
        if (_step is 0 or 2)
        {
            Get<HudSettings>("_hudSettings").Style = _step == 0 ? HudStyle.Classic : HudStyle.Modern;
            _step++; _next = _clock.Elapsed.TotalSeconds + 1; return;
        }
        if (_step is 1 or 3)
        {
            string name = _step == 1 ? "afk-classic" : "afk-modern";
            Set("_autoshotDir", output); Set("_autoShotName", name); Set("_autoShotScheduled", true);
            Console.WriteLine($"P1078_CAPTURE {name}: live IdlePilot [AFK]");
            _step++; _next = _clock.Elapsed.TotalSeconds + 1; return;
        }
        Console.WriteLine("P1078_AFK_ROSTER_PASS styles=Classic,Modern wire=PLAYER_READY idleClock=180.001");
        game.Exit();
    }
}
