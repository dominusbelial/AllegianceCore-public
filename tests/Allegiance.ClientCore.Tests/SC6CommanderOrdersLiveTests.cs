using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class SC6CommanderOrdersLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Research_showcase_issues_accepts_and_executes_attack_and_repair_on_both_sides()
    {
        Require.Artwork();
        string? logPath = Environment.GetEnvironmentVariable("SC6_LIVE_LOG");
        using var file = logPath is null ? null : TextWriter.Synchronized(new StreamWriter(logPath) { AutoFlush = true });
        var saved = Console.Out;
        if (file is not null) Console.SetOut(file);
        void Trace(string line) { output.WriteLine(line); file?.WriteLine(line); }
        using var server = new Srv();
        var config = P733ResearchTests.Config(16);
        config.Showcase.FinaleTimeoutSeconds = 2400;
        config.Transport = new() { MaxConnectionsPerIp = 128 };
        config.LiveSettings.AutoNextMatch.Enabled = false;
        server.Boot(config);
        var gate = new object();
        using var stop = new CancellationTokenSource();
        Exception? tickError = null;
        var ticker = new Thread(() =>
        {
            try { while (!stop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(20); } }
            catch (Exception ex) { tickError = ex; }
        }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","size_scale":1,"staging":true,"finale_after_minutes":1,"finale_side":"both","finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1,"finale_bombers":3}""")!;
            args["target_sector"] = JsonSerializer.SerializeToElement(server.Missions[0].Sectors.First(s => s.Stations.Count == 0).Name);
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            var mission = server.Missions[0];
            var traces = new HashSet<short>();
            var watch = Stopwatch.StartNew();
            double nextSample = 0;
            bool Complete() => new short[] { 0, 1 }.All(side => mission.Skills!.Count(side, ShowcaseSkill.OrderAttack) >= 5
                && mission.Skills.Count(side, ShowcaseSkill.OrderRepair) >= 2
                && mission.Skills.Count(side, ShowcaseSkill.NaniteShip) + mission.Skills.Count(side, ShowcaseSkill.NaniteStation) > 0)
                && traces.Contains(OrderChange.CidAttack) && traces.Contains(OrderChange.CidRepair);
            while (watch.Elapsed.TotalSeconds < 600)
            {
                Assert.Null(tickError);
                var orders = manager.CommanderOrders();
                lock (gate)
                {
                    foreach (var order in orders.Where(o => o.Command is OrderChange.CidAttack or OrderChange.CidRepair
                        && o.Target == o.TargetId && o.Command is { } cid && !traces.Contains(cid)))
                    {
                        var source = server.Ships.FirstOrDefault(s => s.Ship.Id == order.Ship)?.Ship;
                        if (source is null || source.AcceptedShipCommandId != order.Command || source.Sector is null) continue;
                        var round = source.Sector.Models.OfType<Projectile>().FirstOrDefault(p => p.Launcher == source
                            && (order.Command == OrderChange.CidRepair ? p.ProjectileType.Power < 0 : p.ProjectileType.Power > 0));
                        if (round is null) continue;
                        traces.Add(order.Command!.Value);
                        Trace("[SC6-order-trace] " + JsonSerializer.Serialize(new
                        {
                            wall = watch.Elapsed.TotalSeconds, sim = server.SimNow, source.SideId,
                            recipient = source.Id, order.Command, order.TargetType, order.TargetId, executingTarget = order.Target,
                            accepted = source.AcceptedShipCommandId, queued = source.QueuedCommandId,
                            source.StateM, projectile = round.Id, power = round.ProjectileType.Power, source.Repair
                        }));
                    }
                    if (watch.Elapsed.TotalSeconds >= nextSample || Complete())
                    {
                        Trace("[SC6-skills] " + JsonSerializer.Serialize(new { wall = watch.Elapsed.TotalSeconds, sim = server.SimNow,
                            sides = new short[] { 0, 1 }.Select(side => new { side,
                                attack = mission.Skills!.Count(side, ShowcaseSkill.OrderAttack),
                                repair = mission.Skills.Count(side, ShowcaseSkill.OrderRepair),
                                naniteShip = mission.Skills.Count(side, ShowcaseSkill.NaniteShip),
                                naniteStation = mission.Skills.Count(side, ShowcaseSkill.NaniteStation) }).ToArray() }));
                        nextSample = watch.Elapsed.TotalSeconds + 10;
                    }
                    if (Complete() || mission.Stage == MissionStage.Over) break;
                }
                Thread.Sleep(50);
            }
            Assert.True(Complete(), "SC6 needs attack >=5, repair >=2 on each side and actual projectile execution for both order kinds");
            Assert.Equal(0, server.SimPassStageFailures);
            Assert.False(config.LiveSettings.TestHarness?.Enabled ?? false);
            Assert.False(config.LiveSettings.TestHarness?.IgnoreTechGate ?? false);
            Assert.False(config.LiveSettings.TestUnlock?.Enabled ?? false);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); Console.SetOut(saved); }
    }
}
