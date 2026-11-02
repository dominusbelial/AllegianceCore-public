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

// Actual Game1 + loopback server + real station blast damage + periodic deadline.
// Usage: p914-ui-proof <output-directory> <classic|modern>
string output = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/reports/P914_assets");
string style = args.Length > 1 ? args[1] : "modern";
Directory.CreateDirectory(output);
string content = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
string localContent = Path.Combine(AppContext.BaseDirectory, "Content");
if (!Directory.Exists(localContent)) Directory.CreateSymbolicLink(localContent, content);
using var server = new Srv { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = ExternalPaths.Resolve(ExternalResourceKind.Artwork), StaticCore = "PCore001.igc", Port = 0,
    SideSelectAutoSeatSeconds = 30,
    Showcase = new() { StationKillsDecideDeadline = true },
    Games = [new() { Name = "P914 preset proof", Map = "XXV_simple.igc", Teams = 2,
        CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], SkirmishPreset = true }]
});
server.Missions[0].Stage = MissionStage.Started;
using var game = new Game1(new Game1.LaunchOptions
{
    DirectHost = "127.0.0.1", DirectPort = server.BoundPort, PlayerName = "P914 proof pilot",
    WindowWidth = 1280, WindowHeight = 720,
    AutoProof = new AutoProofPlan { AutoSide = 0, AutoReady = true, AutoLaunchRequested = true, LaunchHull = "" }
});
game.Content.RootDirectory = content;
object simGate = new();
using var stop = new CancellationTokenSource();
var ticker = new Thread(() =>
{
    while (!stop.IsCancellationRequested)
    {
        lock (simGate) server.TickSim();
        Thread.Sleep(50);
    }
});
ticker.Start();
game.Components.Add(new Proof(game, server, simGate, output, style));
try { game.Run(); }
finally { stop.Cancel(); ticker.Join(); }

sealed class Proof(Game1 game, Srv server, object simGate, string output, string style) : GameComponent(game)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private double? _flightAt, _resultAt;
    private bool _armed, _shot;
    private string? _lastState;
    private T Get<T>(string name) => (T)typeof(Game1).GetField(name, Hidden)!.GetValue(game)!;
    private void Set(string name, object value) => typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);

    public override void Update(GameTime gameTime)
    {
        lock (simGate) Advance();
    }

    private void Advance()
    {
        if (_watch.Elapsed.TotalSeconds > 90) throw new TimeoutException("P914 real-client proof timed out");
        if (_shot)
        {
            if (!Get<bool>("_autoShotScheduled")) game.Exit();
            return;
        }
        var session = Get<ConnectionSession?>("_session");
        if (session is null) return;
        lock (session.Gate)
        {
            if (session.World.GameOverResult is { } result)
            {
                if (!result.GameoverMessage.Contains("Station kills: Alpha=2, Bravo=0") || result.SideWinner != 0)
                    throw new InvalidOperationException("P914 wire result was not the expected station-kill victory");
                // Let the client finish rotation and warm its font atlas before capture.
                _resultAt ??= _watch.Elapsed.TotalSeconds;
                if (_watch.Elapsed.TotalSeconds - _resultAt < 1) return;
                Set("_autoshotDir", output);
                Set("_autoShotName", "deadline-" + style);
                Set("_autoShotScheduled", true);
                _shot = true;
                return;
            }
        }
        string? state = Get<object>("_state").ToString();
        if (state != _lastState) { Console.WriteLine($"[p914-proof] state={state}"); _lastState = state; }
        if (state != "Flying") return;
        ((HudSettings)Get<object>("_hudSettings")).Style = style == "classic" ? HudStyle.Classic : HudStyle.Modern;
        _flightAt ??= _watch.Elapsed.TotalSeconds;
        if (_armed || _watch.Elapsed.TotalSeconds - _flightAt < 3) return;
        _armed = true;
        Console.WriteLine("[p914-proof] arming deadline");
        var mission = server.Missions[0];
        var sector = mission.Sectors[0];
        var launcher = new Probe(29000, sector, mission.Core.ProbeTypes.Values.First(),
            new Allegiance.Core.Collision.Vector3(100000, 100000, 0), mission.LastUpdate, 0);
        var type = mission.Core.StationTypes.Values.First(t => t.MaxArmorHitPoints > 0 && t.MaxShieldHitPoints > 0
            && !t.HasCapability(DataStationTypeIGC.SabmPedestal)) with { SabmCapabilities = 0 };
        byte damage = mission.Core.ProjectileTypes.Values.First(p => p.Power > 0
            && mission.Core.Constants.DamageConstants[p.DamageType & 0x1f][type.DefenseTypeShield] > 0
            && mission.Core.Constants.DamageConstants[p.DamageType & 0x1f][type.DefenseTypeArmor] > 0).DamageType;
        for (short i = 0; i < 2; i++)
        {
            var station = new Station(new DataStationIGC([100000 + i * 10000, 100000, 0], [0, 1, 0], [1, 0, 0],
                [0, 0, 0, 1], sector.Id, 1, (short)(30000 + i), type.StationTypeId, 255, 255, "Proof garrison"), type);
            mission.PlaceStation(station, sector);
            sector.ResolveExplosion(new Explosion(damage, 1e9f, 100, sector.LastUpdate, station.Center, launcher), sector.LastUpdate);
        }
        if (mission.StationKills(0) != 2) throw new InvalidOperationException("P914 blasts did not destroy two stations");
        mission.Sides[0].Kills = 901; mission.Sides[1].Kills = 978;
        mission.CountdownStart = mission.LastUpdate;
        mission.CountdownDuration = 1;
    }
}
