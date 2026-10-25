using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;

// Local-only, real sockets, ordinary purchases, native fog and native map.
// Pass --swap for the same positions with the civilizations exchanged.
var directory = Path.GetFullPath(Option("--output", ".scratch/p819/local"));
Directory.CreateDirectory(directory);
if (args.Contains("--catalogue")) { P821Catalogue.Write(directory); return; }
bool swap = args.Contains("--swap");
int matches = int.Parse(Option("--matches", "1"));
int tickMs = int.Parse(Option("--tick-ms", "20"));
int limit = int.Parse(Option("--seconds", "600"));
int scale = int.Parse(Option("--size-scale", "1"));
int finaleSeconds = int.Parse(Option("--finale-seconds", "300"));
double finaleMinutes = double.Parse(Option("--finale-minutes", "0.5"), System.Globalization.CultureInfo.InvariantCulture);
int gameSeconds = int.Parse(Option("--game-seconds", "1800"));
var progress = Console.Out;
for (int match = 1; match <= matches; match++)
{
    string label = $"{(swap ? "swapped" : "normal")}-{match}";
    using var writer = TextWriter.Synchronized(new StreamWriter(Path.Combine(directory, label + ".log")) { AutoFlush = true });
    Console.SetOut(writer);
    try
    {
        using var server = new Srv();
        server.Boot(new()
        {
            Port = 0, SideSelectAutoSeatSeconds = 30, ConsoleVerbose = true,
            Showcase = new() { TechMode = "research", IncomeMultiplier = 5000, ResearchTimeScale = 8,
                FinaleTimeoutSeconds = finaleSeconds },
            LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
            Games = [new() { Name = "P819 local", Map = "m2xspiderw.igc", Teams = 2,
                CivIds = swap ? [3, 25] : [25, 3], SideNames = ["Alpha", "Bravo"],
                GameLengthSeconds = gameSeconds,
                MaxTeamImbalance = 32767, MaxPlayersPerTeam = 40,
                ResearchPlanDirectory = Path.GetFullPath("data/showcase-plans"),
                ResearchPlans = new() { [0] = "plan_a", [1] = "plan_a" } }],
        });
        using var stop = new CancellationTokenSource();
        Exception? tickFailure = null;
        var ticker = new Thread(() =>
        {
            try { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(tickMs); } }
            catch (Exception ex) { tickFailure = ex; }
        }) { IsBackground = true };
        ticker.Start();
        var watch = Stopwatch.StartNew();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var scenario = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","staging":true,"finale_side":"both","finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1,"finale_bombers":8,"loadout_mode":"varied"}""")!;
            scenario["size_scale"] = JsonSerializer.SerializeToElement(scale);
            scenario["finale_after_minutes"] = JsonSerializer.SerializeToElement(finaleMinutes);
            var spawn = McpTools.Handle("spawn_scenario", scenario, manager);
            if (spawn.IsError == true) throw new InvalidOperationException(JsonSerializer.Serialize(spawn));
            var mission = server.Missions[0];
            File.WriteAllText(Path.Combine(directory, label + "-geometry.json"), JsonSerializer.Serialize(
                mission.Sectors.Select(s => new { s.Id, s.Name,
                    Rocks = s.Mineable.Select(a => new { a.Data.AsteroidId, a.Data.Name, a.Data.Position, a.CanMineHe3, a.OreRemaining }),
                    Stations = s.Stations.Select(st => new { st.SideId, st.StationId, st.Position }),
                    Warps = s.Warps.Select(w => new { w.Warp.WarpId, w.Warp.Position, To = w.Destination?.Id }) }), JsonOptions()));
            double next = 0;
            while (watch.Elapsed.TotalSeconds < limit && mission.Stage != MissionStage.Over)
            {
                if (tickFailure is not null) throw new InvalidOperationException("Simulation failed", tickFailure);
                if (watch.Elapsed.TotalSeconds >= next)
                {
                    var bots = manager.ActiveBots();
                    writer.WriteLine("[p819-snapshot] " + JsonSerializer.Serialize(new { Wall = watch.Elapsed.TotalSeconds,
                        Sim = server.SimNow, Finale = manager.FinaleStatus(), Bots = bots,
                        Orders = manager.CommanderOrders(),
                        Miners = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).Where(sh => sh.PilotType == Ship.PilotTypeMiner && !sh.Dead).Select(sh => new { sh.Id, sh.SideId, Sector = sh.Sector?.Name, sh.Ore, Target = sh.MiningAsteroidTarget?.Data.AsteroidId, TargetSector = sh.MiningAsteroidTarget?.Data.ClusterId, sh.Position }),
                        Actual = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).Where(sh => !sh.Dead).Select(sh => new { sh.Id, sh.SideId, Hull = sh.HullType.HullId, Role = Showcase.ClassifyHull(sh.HullType).ToString(), sh.StationId }) }));
                    progress.WriteLine($"{label}: wall={watch.Elapsed.TotalSeconds:F0}s sim={server.SimNow:F0}s "
                        + string.Join(" ", mission.Sides.Select(s => $"side{s.Id}={s.Kills}/{s.Deaths}")));
                    next += 30;
                }
                Thread.Sleep(250);
            }
            Thread.Sleep(1000); // receive the actual GAME_OVER on the bot connections
            var result = new { Label = label, Scale = scale, FinaleSeconds = finaleSeconds, FinaleMinutes = finaleMinutes,
                GameSeconds = gameSeconds,
                Civs = mission.Sides.Select(s => s.Civilization.CivilizationId),
                Miners = mission.Sectors.SelectMany(s => s.Models.OfType<Ship>()).Where(sh => sh.PilotType == Ship.PilotTypeMiner && !sh.Dead).GroupBy(sh => sh.SideId).Select(g => new { Side = g.Key, Alive = g.Count() }),
                Stage = mission.Stage.ToString(), Wall = watch.Elapsed.TotalSeconds, Sim = server.SimNow,
                Sides = mission.Sides.Select(s => new { s.Id, s.Kills, s.Deaths }), Finale = manager.FinaleStatus(),
                Failures = server.SimPassStageFailures };
            File.WriteAllText(Path.Combine(directory, label + "-result.json"), JsonSerializer.Serialize(result, JsonOptions()));
            progress.WriteLine(JsonSerializer.Serialize(result));
            if (mission.Stage != MissionStage.Over || server.SimPassStageFailures != 0)
                throw new InvalidOperationException("Local match did not finish cleanly");
        }
        finally { stop.Cancel(); if (!ticker.Join(5000)) throw new TimeoutException("Ticker shutdown"); }
    }
    finally { Console.SetOut(progress); }
}
string Option(string name, string fallback)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 ? args[i + 1] : fallback;
}
static JsonSerializerOptions JsonOptions() => new() { WriteIndented = true };
