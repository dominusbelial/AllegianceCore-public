using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Server;
using Allegiance.Tools.McpSpawner;

// Local 30v30 TCP march. No rig access, staging, or injected ship movement.
var art = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
    ?? throw new InvalidOperationException("Set ALLEGIANCE_ARTWORK_PATH");
var secret = Guid.NewGuid().ToString("N");
Environment.SetEnvironmentVariable("ALLEGIANCE_PRIVILEGE_SECRET", secret);
using var server = new Server { SpawnInitialMinersAtBoot = false };
server.Boot(new ServerConfig
{
    ArtPath = art, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
    LiveSettings = new LiveSettingsConfig
    {
        TestHarness = new TestHarnessConfig { Enabled = true, Secret = secret, Money = 1000000, IgnoreTechGate = true },
        AdminCheats = new AdminCheatsConfig { Enabled = true, Secret = secret, AllowNames = ["mcp-*"] },
    },
    Games = [new GameConfig { Name = "P655 local march", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] }],
});
using var stop = new CancellationTokenSource();
long corrections = 0;
Exception? tickFailure = null;
var commands = new ConcurrentQueue<Action>();
var ticker = new Thread(() =>
{
    try
    {
        while (!stop.IsCancellationRequested)
        {
            while (commands.TryDequeue(out var action)) action();
            server.TickSim();
            Thread.Sleep(5);
        }
    }
    catch (Exception e) { tickFailure = e; }
}) { IsBackground = true };
ticker.Start();
try
{
    using var manager = new BotManager("127.0.0.1", server.BoundPort);
    if (!manager.ServerReady) throw new InvalidOperationException("MCP probe did not seat");
    var sides = manager.ValidSides();
    var sector = server.Missions[0].Sectors.First(s => s.Stations.Any(st => st.SideId == sides[0])).Id;
    var hull = Catalog.DefaultFighterHull();
    var parts = Catalog.NativePreferredLoadout(hull.Id, hull.Name, 0).Parts;
    var bots = new HashSet<short>();
    for (int i = 0; i < 30; i++)
        foreach (var side in sides.Take(2)) bots.Add(manager.SpawnBot(hull.Id, hull.Name, side, parts, "combat", sector, useStaging: false).ShipId);
    var reached = new HashSet<short>();
    var until = DateTime.UtcNow.AddSeconds(120);
    while (DateTime.UtcNow < until && tickFailure is null)
    {
        foreach (var bot in manager.DiagnoseWorlds()) if (bot.Sector == sector && bots.Contains(bot.ShipId)) reached.Add(bot.ShipId);
        if (reached.Count == 60 && manager.GetCombatSummary().Kills > 0) break;
        Thread.Sleep(500);
    }
    using var sampled = new ManualResetEventSlim();
    commands.Enqueue(() =>
    {
        // Sample monotonic counters on their owning simulation thread.
        var peers = (System.Collections.IDictionary)typeof(Server).GetField("_peerShipUpdate", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(server)!;
        foreach (var state in peers.Values) corrections += (long)state!.GetType().GetField("TotalCorrections")!.GetValue(state)!;
        sampled.Set();
    });
    if (!sampled.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("ticker stopped", tickFailure);
    var combat = manager.GetCombatSummary();
    var result = new { Bots = bots.Count, Reached = reached.Count, combat.Kills, combat.ShotsFired, combat.ShipHits, Corrections = corrections, TickFailure = tickFailure?.ToString() };
    File.WriteAllText(args.FirstOrDefault() ?? "/tmp/p655-live.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("P655_RESULT " + JsonSerializer.Serialize(result));
    return bots.Count == 60 && reached.Count == 60 && combat.Kills > 0 && corrections == 0 && tickFailure is null ? 0 : 1;
}
finally { stop.Cancel(); ticker.Join(10000); }
