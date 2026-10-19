using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class SC4ShowcaseLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Research_showcase_deploys_chaff_probes_and_defensive_mines_on_both_sides()
    {
        Require.Artwork();
        using var server = new Srv();
        var config = P733ResearchTests.Config(16);
        // All local pilots share loopback; the public-server default is 64/IP.
        config.Transport = new() { MaxConnectionsPerIp = 256 };
        server.Boot(config);
        var mission = server.Missions[0];
        var sync = new object();
        var seen = new HashSet<Model>(ReferenceEqualityComparer.Instance);
        var samples = new List<object>();
        var sampled = new Dictionary<(string, short), int>();
        var homeMines = new HashSet<short>();
        var contestedProbes = new HashSet<short>();
        Exception? tickFailure = null;
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    lock (sync)
                    {
                        server.TickSim();
                        foreach (var sector in mission.Sectors)
                        foreach (var model in sector.Models)
                        {
                            string kind; float lifetime; Ship? launcher;
                            switch (model)
                            {
                                case Mine mine: kind = "mine"; lifetime = mine.MineType.Lifespan; launcher = mine.Launcher as Ship; break;
                                case Probe probe: kind = "probe"; lifetime = probe.ProbeType.Lifespan; launcher = probe.Launcher as Ship; break;
                                case Chaff chaff: kind = "chaff"; lifetime = chaff.ChaffType.Lifespan; launcher = chaff.Launcher as Ship; break;
                                default: continue;
                            }
                            if (!seen.Add(model) || launcher?.SideId is not { } side) continue;
                            bool home = sector.StationRuntimes.Any(s => s.SideId == side);
                            float alephDistance = sector.Warps.Select(w => (new Vector3(w.Warp.Position[0], w.Warp.Position[1], w.Warp.Position[2])
                                - model.Position).Length()).DefaultIfEmpty(float.MaxValue).Min();
                            if (kind == "mine" && home && alephDistance < 3000) homeMines.Add(side);
                            if (kind == "probe" && !home) contestedProbes.Add(side);
                            var key = (kind, side);
                            sampled.TryGetValue(key, out int count);
                            if (count >= 20) continue;
                            sampled[key] = count + 1;
                            samples.Add(new { kind, side, sector = sector.Id, sim = server.SimNow, model = model.Id,
                                launcher = launcher.Id, lifetime, home, alephDistance,
                                position = new[] { model.Position.X, model.Position.Y, model.Position.Z } });
                        }
                    }
                    Thread.Sleep(20);
                }
            }
            catch (Exception e) { tickFailure = e; }
        }) { IsBackground = true };
        ticker.Start();
        var watch = Stopwatch.StartNew();
        long[][] counts = [];
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","count_a":60,"count_b":60,"target_sector":"1","staging":true,"finale_after_minutes":0}""")!;
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            bool enough = false;
            double nextProgress = 0;
            while (watch.Elapsed.TotalSeconds < 600 && tickFailure is null)
            {
                lock (sync)
                {
                    counts = new short[] { 0, 1 }.Select(side => new[] { ShowcaseSkill.Chaff, ShowcaseSkill.Probes, ShowcaseSkill.Mines }
                        .Select(skill => mission.Skills!.Count(side, skill)).ToArray()).ToArray();
                    enough = counts.All(c => c[0] >= 10 && c[1] >= 10 && c[2] >= 5)
                        && homeMines.Count == 2 && contestedProbes.Count == 2;
                }
                if (watch.Elapsed.TotalSeconds >= nextProgress)
                {
                    nextProgress = watch.Elapsed.TotalSeconds + 15;
                    if (Environment.GetEnvironmentVariable("SC4_PROOF_DIRECTORY") is { Length: > 0 } progressDirectory)
                    {
                        Directory.CreateDirectory(progressDirectory);
                        File.WriteAllText(Path.Combine(progressDirectory, "progress.json"), JsonSerializer.Serialize(new
                        { wallSeconds = watch.Elapsed.TotalSeconds, counts, active = manager.ActiveBots() }));
                    }
                }
                if (enough) break;
                Thread.Sleep(200);
            }
            lock (sync)
            {
                using var text = new StringWriter(); mission.Skills!.Write(text);
                output.WriteLine(text.ToString());
                var summary = new { wallSeconds = watch.Elapsed.TotalSeconds, simSeconds = server.SimNow,
                    mode = "research", researchScale = 16, combatPeersPerSide = 60,
                    counts, homeMines = homeMines.Order().ToArray(), contestedProbes = contestedProbes.Order().ToArray(),
                    samples, server.SimPassStageFailures, enough };
                string json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
                output.WriteLine(json);
                if (Environment.GetEnvironmentVariable("SC4_PROOF_DIRECTORY") is { Length: > 0 } directory)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "showcase-" + Guid.NewGuid().ToString("N") + ".json"), json);
                }
            }
            Assert.Null(tickFailure);
            Assert.True(enough, "SC-1 unscaled minimums: chaff >=10, probes >=10, mines >=5 on EACH side; plus actual models at defended alephs/contested sectors.");
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }
}
