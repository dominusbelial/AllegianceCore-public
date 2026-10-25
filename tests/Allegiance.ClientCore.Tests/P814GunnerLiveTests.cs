using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>P814 — live: real MCP bots board teammates' turret hulls and the SERVER
/// fires their turrets. Server truth, not the bots' own trigger counters:
/// <see cref="Weapon.TurretRoundsFired"/> counts projectiles a weapon created while a
/// gunner was bound (the path the P803 rebind fix made work).</summary>
[Trait("Tier", "Live")]
public sealed class P814GunnerLiveTests(ITestOutputHelper output)
{
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Showcase_bots_board_turret_hulls_and_the_server_fires_their_turrets()
    {
        Require.Artwork();
        // Bot gunners are opt-in (default off); this test exercises them.
        var previous = Environment.GetEnvironmentVariable("ALLEGIANCE_GUNNERS");
        Environment.SetEnvironmentVariable("ALLEGIANCE_GUNNERS", "on");
        GunnerPolicy.ResetCache();
        try { RunGunnerShowcase(); }
        finally
        {
            Environment.SetEnvironmentVariable("ALLEGIANCE_GUNNERS", previous);
            GunnerPolicy.ResetCache();
        }
    }

    private void RunGunnerShowcase()
    {
        var config = P660NextMatchLiveTests.Config(false);
        config.LiveSettings.TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true };
        config.LiveSettings.TestUnlock = new() { Enabled = true, AllTech = true, Money = 1_000_000 };
        var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        server.Missions[0].Stage = MissionStage.Started;
        using var _ = server;

        int boardedPeak = 0, dockedPeak = 0, podsSeen = 0;
        var dockedIds = new HashSet<short>();
        long rounds = 0;
        var spawn = Task.Run(() =>
        {
            var manager = new BotManager("127.0.0.1", server.BoundPort);
            var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                """{"preset":"showcase","size_scale":1,"target_sector":"Alderan","staging":false,"finale_after_minutes":0}""")!;
            var result = McpTools.Handle("spawn_scenario", args, manager);
            Assert.False(result.IsError ?? false, JsonSerializer.Serialize(result));
            return manager;
        });
        var watch = Stopwatch.StartNew();
        BotManager? mgr = null;
        while (watch.Elapsed.TotalSeconds < 900)
        {
            server.TickSim();
            Thread.Sleep(5);
            if (mgr is null && spawn.IsCompleted) mgr = spawn.GetAwaiter().GetResult();
            int boarded = server.Ships.Count(l => l.Ship.ParentShip is not null && l.Ship.TurretId is not null);
            boardedPeak = Math.Max(boardedPeak, boarded);
            int docked = server.Ships.Count(l => l.Ship.StationId is not null);
            dockedPeak = Math.Max(dockedPeak, docked);
            foreach (var l in server.Ships) if (l.Ship.StationId is not null) dockedIds.Add(l.Ship.Id);
            podsSeen = Math.Max(podsSeen, server.Ships.Count(l => l.Ship.HullType.HasCapability(Allegiance.Core.DataHullTypeIGC.HabmLifepod)));
            rounds = server.Ships.Sum(l => (long)l.Ship.Weapons.Sum(w => w.TurretRoundsFired));
            if (boardedPeak > 0 && rounds >= 5) break;
        }
        output.WriteLine($"boarded gunners (peak) = {boardedPeak}; turret rounds fired by the server = {rounds}; docked peak={dockedPeak} distinct docked ships={dockedIds.Count} pods peak={podsSeen}; t={watch.Elapsed.TotalSeconds:F0}s");
        if (mgr is not null)
            output.WriteLine("roles: " + string.Join(", ", mgr.ActiveBots().GroupBy(b => b.Role).Select(g => $"{g.Key}={g.Count()}")));
        var turretHullIds = server.Ships.Where(l => l.Ship.HullType.MaxWeapons > l.Ship.HullType.MaxFixedWeapons).Select(l => l.Ship.HullType.Name).ToList();
        output.WriteLine($"ships now flying turret hulls: {turretHullIds.Count} [{string.Join(",", turretHullIds.Distinct())}]");
        output.WriteLine("hulls now: " + string.Join(", ", server.Ships.GroupBy(l => l.Ship.HullType.Name).Select(g => $"{g.Key}={g.Count()}")));
        mgr?.Dispose();
        Assert.True(boardedPeak > 0, "no bot boarded a turret seat");
        Assert.True(rounds >= 5, $"bot gunners boarded but the server fired only {rounds} turret round(s)");
    }
}
