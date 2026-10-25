using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.Server;
using Allegiance.Tools.McpSpawner;
using Srv = Allegiance.Server.Server;

internal static class P825Rotation
{
    internal static void Run(string output, int matches, int limit)
    {
        Directory.CreateDirectory(output);
        var progress = Console.Out;
        using var log = TextWriter.Synchronized(new StreamWriter(Path.Combine(output, "server.log")) { AutoFlush = true });
        Console.SetOut(log);
        try
        {
            string configPath = Path.Combine(output, "server.json");
            // Exercise the production JSON loader, including actual null entries.
            File.WriteAllText(configPath, """
                {
                  "port": 0, "consoleVerbose": true, "sideSelectAutoSeatSeconds": 30,
                  "showcase": { "techMode": "research", "incomeMultiplier": 5000,
                    "researchTimeScale": 8, "finaleTimeoutSeconds": 12 },
                  "liveSettings": { "timeDilation": "off", "autoNextMatch": { "enabled": true, "intermissionSeconds": 2 } },
                  "games": [{ "name": "P825 local rotation", "map": "m2xspiderw.igc", "teams": 2,
                    "civIds": [null, null], "civSeed": 825, "sideNames": ["Alpha", "Bravo"],
                    "gameLengthSeconds": 30, "showMap": true, "maxTeamImbalance": 32767,
                    "researchPlanDirectory": "data/showcase-plans", "researchPlans": { "0": "plan_a", "1": "plan_a" } }]
                }
                """);
            using var server = new Srv();
            server.Boot(ServerConfigLoader.Load(configPath, []));
            using var stop = new CancellationTokenSource();
            Exception? failure = null;
            var ticker = new Thread(() =>
            {
                try { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); } }
                catch (Exception ex) { failure = ex; }
            }) { IsBackground = true };
            ticker.Start();
            try
            {
                using var manager = new BotManager("127.0.0.1", server.BoundPort);
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
                    {"preset":"showcase","size_scale":1,"staging":true,"finale_side":"both",
                     "finale_after_minutes":0.001,"finale_min_relaunch_share":0,
                     "finale_min_kills_last_5_minutes":1,"finale_bombers":2,"loadout_mode":"varied"}
                    """)!;
                var spawn = McpTools.Handle("spawn_scenario", args, manager);
                if (spawn.IsError == true) throw new InvalidOperationException(JsonSerializer.Serialize(spawn));
                var clock = Stopwatch.StartNew();
                var mission = server.Missions[0];
                var results = new List<object>();
                var tuples = new HashSet<string>();
                while (results.Count < matches && clock.Elapsed.TotalSeconds < limit)
                {
                    if (failure is not null) throw new InvalidOperationException("Simulation failed", failure);
                    if (mission.Stage == MissionStage.Over && !ReferenceEquals(mission, server.Missions[0]))
                    {
                        string tuple = string.Join(",", mission.Sides.Select(s => s.Civilization.CivilizationId));
                        tuples.Add(tuple);
                        results.Add(new { Match = results.Count + 1, Civs = tuple, Wall = clock.Elapsed.TotalSeconds,
                            Plans = mission.ResearchPlans.Select(p => new { Side = p.Key, p.Value.CivId, p.Value.Name }),
                            Bots = manager.ActiveBots().Count, Failures = server.SimPassStageFailures });
                        progress.WriteLine($"P825 match {results.Count}/{matches}: civs={tuple}, simFailures={server.SimPassStageFailures}");
                        mission = server.Missions[0];
                    }
                    Thread.Sleep(25);
                }
                Thread.Sleep(500); // let GAME_OVER arrive on bot connections
                File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
                {
                    Completed = results.Count, DistinctMatchups = tuples.Count, Matches = results,
                    Failures = server.SimPassStageFailures, Finale = manager.FinaleStatus(),
                }, new JsonSerializerOptions { WriteIndented = true }));
                if (results.Count != matches || tuples.Count != matches || server.SimPassStageFailures != 0)
                    throw new InvalidOperationException($"Expected {matches} distinct clean consecutive games, completed {results.Count}, distinct {tuples.Count}");
            }
            finally { stop.Cancel(); if (!ticker.Join(5000)) throw new TimeoutException("Ticker shutdown"); }
        }
        finally { Console.SetOut(progress); }
    }
}
