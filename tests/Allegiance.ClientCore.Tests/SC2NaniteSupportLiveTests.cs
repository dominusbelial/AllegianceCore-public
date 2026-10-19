using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class SC2NaniteSupportLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void Research_x16_showcase_support_repairs_ships_and_stations_on_both_sides()
    {
        Require.Artwork();
        string? livePath = Environment.GetEnvironmentVariable("SC2_LIVE_LOG");
        using var liveFile = livePath is null ? null : new StreamWriter(livePath) { AutoFlush = true };
        using var liveLog = liveFile is null ? null : TextWriter.Synchronized(liveFile);
        var saved = Console.Out;
        if (liveLog is not null) Console.SetOut(liveLog);
        void Trace(string line) { output.WriteLine(line); liveLog?.WriteLine(line); }
        using var server = new Srv();
        var config = P733ResearchTests.Config(16);
        config.Showcase.FinaleTimeoutSeconds = 2400;
        config.Transport = new() { MaxConnectionsPerIp = 128 }; // 90 pilots plus probe/commanders share loopback.
        config.LiveSettings.AutoNextMatch.Enabled = false;
        server.Boot(config);
        using var stop = new CancellationTokenSource();
        var ticker = new Thread(() => { while (!stop.IsCancellationRequested) { server.TickSim(); Thread.Sleep(20); } }) { IsBackground = true };
        ticker.Start();
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","size_scale":3,"staging":true,"finale_after_minutes":1,"finale_side":"both","finale_min_relaunch_share":0,"finale_min_kills_last_5_minutes":1,"finale_bombers":6}""")!;
            args["target_sector"] = JsonSerializer.SerializeToElement(server.Missions[0].Sectors.First(s => s.Stations.Count == 0).Name);
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            var fleet = manager.ActiveBots();
            Assert.Equal(90, fleet.Count);
            foreach (short side in new short[] { 0, 1 })
            {
                Assert.Equal(45, fleet.Count(b => b.Side == side));
                Assert.Equal(5, fleet.Count(b => b.Side == side && b.Role.Equals("support", StringComparison.OrdinalIgnoreCase)));
            }
            var mission = server.Missions[0];
            var watch = Stopwatch.StartNew();
            bool Complete() => mission.Skills is { } skills && new short[] { 0, 1 }.All(side =>
                skills.Count(side, ShowcaseSkill.NaniteShip) >= 10 && skills.Count(side, ShowcaseSkill.NaniteStation) >= 10);
            while (!Complete() && watch.Elapsed.TotalSeconds < 1200 && mission.Stage != MissionStage.Over)
            {
                Trace($"wall={watch.Elapsed.TotalSeconds:F1} sim={server.SimNow:F1} " + string.Join(";", new short[] {0,1}.Select(s =>
                    $"side={s} ship={mission.Skills?.Count(s, ShowcaseSkill.NaniteShip)} station={mission.Skills?.Count(s, ShowcaseSkill.NaniteStation)}")));
                Trace("[support-sample] " + JsonSerializer.Serialize(manager.ActiveBots().Where(b => b.Role.Equals("support", StringComparison.OrdinalIgnoreCase))));
                Trace("[finale-sample] " + JsonSerializer.Serialize(manager.FinaleStatus()));
                Trace("[stations] " + string.Join(";", mission.Sectors.SelectMany(s => s.StationRuntimes)
                    .Select(s => $"{s.StationId} side={s.SideId} hp={s.ArmorFraction:F5} shield={s.ShieldFraction:F5}")));
                Thread.Sleep(5000);
            }
            mission.Skills?.Write(new TestOutputWriter(output));
            Assert.True(Complete(), "SC2 requires both real repair counters >= 10 on both sides");
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); Console.SetOut(saved); }
    }

    [SkippableFact]
    public void One_legally_purchased_support_pilot_restores_a_friendly_ship_then_own_station()
    {
        Require.Artwork();
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        var config = P733ResearchTests.Config(16); config.LiveSettings.AutoNextMatch.Enabled = false;
        server.Boot(config);
        using var targetPeer = P660NextMatchLiveTests.Connect(server, "SC2-friendly", 820201);
        using var stop = new CancellationTokenSource();
        var actions = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var ticker = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                while (actions.TryDequeue(out var action)) action();
                server.TickSim(); Thread.Sleep(20);
            }
        }) { IsBackground = true };
        ticker.Start();
        void Wait(Func<bool> done)
        {
            var watch = Stopwatch.StartNew();
            while (!done() && watch.Elapsed.TotalSeconds < 45) Thread.Sleep(20);
            Assert.True(done(), "SC2 controlled loopback condition timed out");
        }
        void OnSim(Action action)
        {
            using var done = new ManualResetEventSlim();
            actions.Enqueue(() => { try { action(); } finally { done.Set(); } });
            Assert.True(done.Wait(5000));
        }
        try
        {
            using var manager = new BotManager("127.0.0.1", server.BoundPort);
            var mission = server.Missions[0];
            short side = targetPeer.World.MySideId!.Value;
            var civilization = mission.Sides.Single(s => s.Id == side).Civilization;
            var core = mission.Core;
            var state = new ResearchState(new(core, civilization.CivilizationId));
            var loadout = TechLoadoutSelector.Select(core, civilization.CivilizationId, Showcase.HullRole.Scout,
                state.Techs, civilization.InitialStationTypeId, budget: 0)!;
            targetPeer.SendBuyLoadout(loadout.HullId, true, loadout.Parts);
            targetPeer.SendPlayerReady(true, targetPeer.World.MyShipId!.Value);
            var selected = TechLoadoutSelector.SelectSupport(core, civilization.CivilizationId, state.Techs, civilization.InitialStationTypeId, 0)!;
            var pilot = manager.SpawnBot(selected.HullId, selected.HullName, side, selected.Parts, "combat", role: "support");
            Assert.Null(pilot.Warning);
            Wait(() => mission.Stage == MissionStage.Started);
            var source = server.Ships.Single(s => s.Ship.Id == pilot.ShipId).Ship;
            var target = server.Ships.Single(s => s.Ship.Id == targetPeer.World.MyShipId).Ship;
            var home = mission.Sectors.SelectMany(s => s.StationRuntimes).First(s => s.SideId == side);
            Wait(() => source.StationId is null && target.StationId is null);
            // Training-style fixture placement/damage only: purchases, aiming,
            // controls, projectiles, healing and SC1 counters remain production.
            OnSim(() =>
            {
                target.Position = new(15000, 15000, 0); target.Velocity = new(0, 0, -5);
                typeof(Ship).GetField("_fraction", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(target, .5f);
                source.Position = target.Position + new Allegiance.Core.Collision.Vector3(0, 0, 120);
                source.Velocity = Allegiance.Core.Collision.Vector3.Zero;
                source.SetOrientation(Orientation.Identity);
            });
            targetPeer.CommandControls(new(0,0,0,0), 0);
            long shipBefore = mission.Skills!.Count(side, ShowcaseSkill.NaniteShip);
            float creditBefore = source.Repair;
            Wait(() => mission.Skills.Count(side, ShowcaseSkill.NaniteShip) >= shipBefore + 3 && target.Fraction > .7f);
            Assert.True(source.Repair > creditBefore);
            output.WriteLine($"[repair-trace] pilot={pilot.Name} ship={source.Id} side={side} kind=ship target={target.Id} hull=0.500->{target.Fraction:F5} nativeRepairCredit={source.Repair-creditBefore:F5} effectiveHits={mission.Skills.Count(side, ShowcaseSkill.NaniteShip)-shipBefore}");
            long stationBefore = mission.Skills.Count(side, ShowcaseSkill.NaniteStation);
            OnSim(() =>
            {
                typeof(Station).GetProperty(nameof(Station.ArmorFraction))!.SetValue(home, .5f);
                source.Position = home.Center + new Allegiance.Core.Collision.Vector3(0, 0, home.Type.Radius + 150);
                source.Velocity = Allegiance.Core.Collision.Vector3.Zero;
                source.SetOrientation(Orientation.Identity);
            });
            Wait(() => mission.Skills.Count(side, ShowcaseSkill.NaniteStation) >= stationBefore + 3 && home.ArmorFraction > .5f);
            output.WriteLine($"[repair-trace] pilot={pilot.Name} ship={source.Id} side={side} kind=station target={home.StationId} hull=0.500->{home.ArmorFraction:F5} effectiveHits={mission.Skills.Count(side, ShowcaseSkill.NaniteStation)-stationBefore}");
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { stop.Cancel(); Assert.True(ticker.Join(5000)); }
    }

    private sealed class TestOutputWriter(ITestOutputHelper output) : StringWriter
    {
        public override void WriteLine(string? value) => output.WriteLine(value ?? "");
    }
}
