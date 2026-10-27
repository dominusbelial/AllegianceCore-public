using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

public sealed class P727SkirmishOptionsTests
{
    [Theory]
    [InlineData("conquest")] [InlineData("territory")] [InlineData("kills")]
    [InlineData("flags")] [InlineData("artifacts")] [InlineData("prosperity")] [InlineData("countdown")]
    public void Goal_presets_reach_skirmish_config_and_remembered_options(string preset)
    {
        var goals = Allegiance.Core.Model.MissionGoals.Preset(preset);
        var options = new SkirmishOptions { Goals = goals };
        Assert.Equal(goals, options.CreateConfig().Games[0].ResolveGoals());
        var restored = SkirmishPrefs.From(options).ToOptions();
        Assert.Equal(goals, restored.CreateConfig().Games[0].ResolveGoals());
        Assert.NotSame(goals, restored.Goals);
    }

    [Theory]
    [InlineData("pilot  ", "pilot")]
    [InlineData("pilotΩ", "pilot?")]
    [InlineData("Gä", "Gä")]
    [InlineData("1234567890123456789012345", "123456789012345678901234")]
    public void Human_pilot_identity_matches_native_wire_name(string input, string wire) =>
        Assert.Equal(wire, SkirmishHost.WirePilotName(input));

    [Theory]
    [InlineData(SkirmishSize.Small, 5)]
    [InlineData(SkirmishSize.Medium, 15)]
    [InlineData(SkirmishSize.Large, 30)]
    [InlineData(SkirmishSize.Epic, 60)]
    public void Config_reserves_seats_disables_rank_balance_and_stays_local(SkirmishSize size, int teamSize)
    {
        foreach (var race in SkirmishOptions.Civilizations)
        {
            var options = new SkirmishOptions { Size = size, PlayerCiv = race.Id, EnemyCiv = race.Id };
            var config = options.CreateConfig();
            Assert.Equal(teamSize, options.TeamSize);
            Assert.Equal(0, config.Port);
            Assert.True(config.Transport!.MaxConnectionsPerIp >= teamSize * 2 + 20);
            Assert.True(config.Transport.MaxConnections >= config.Transport.MaxConnectionsPerIp);
            Assert.Equal("127.0.0.1", config.BindAddress);
            Assert.False(config.Discovery!.Enabled);
            Assert.Equal("auto", config.LiveSettings.TimeDilation);
            Assert.True(config.LiveSettings.AutoNextMatch.Enabled);
            Assert.Equal(0x7fff, config.Games[0].MaxTeamImbalance);
            Assert.True(config.Games[0].MaxPlayersPerTeam > teamSize);
            Assert.Equal(new[] { race.Id, race.Id }, config.Games[0].CivIds);
            Assert.Equal("research", config.Showcase.TechMode);
            Assert.False(config.LiveSettings.TestHarness!.Enabled);
            Assert.Equal(size == SkirmishSize.Epic, options.Warning.Length > 0);
        }
        Assert.Equal(10, SkirmishOptions.Civilizations.Length);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)] [InlineData(8)] [InlineData(16)] [InlineData(128)]
    public void Bot_compute_budget_is_bounded_and_reserves_two_cores_when_available(int cores)
    {
        var budget = SkirmishBudget.ForCores(cores);
        Assert.InRange(budget.Workers, 1, 4);
        Assert.InRange(budget.CoresPerWorker, 1, Math.Max(1, cores - 2));
        Assert.True(budget.Workers * budget.CoresPerWorker <= Math.Max(1, cores - 2));
        Assert.Equal(budget.CoresPerWorker, BotManager.ComputeParallelism(_ => (budget.CoresPerWorker * 4).ToString(), budget.CoresPerWorker));
        Assert.Equal(-1, BotManager.ComputeParallelism(_ => null, cores));
    }

    [Fact]
    public void Menu_cycles_every_race_size_tech_and_difficulty()
    {
        var menu = new SkirmishScreenModel();
        var ids = new HashSet<int>();
        for (int i = 0; i < 10; i++) { ids.Add(menu.Options.PlayerCiv); menu.Change(1); }
        Assert.Equal(10, ids.Count);
        menu.Move(1); menu.Change(-1);
        Assert.Equal(2, menu.Options.EnemyCiv);
        menu.Move(1); menu.Change(-1);
        // P789 — Grand joined the size cycle: two left steps from Small reach Epic.
        Assert.Equal(SkirmishSize.Grand, menu.Options.Size);
        menu.Change(-1);
        Assert.Equal(SkirmishSize.Epic, menu.Options.Size);
        menu.Move(1); menu.Change(1);
        Assert.Equal("unlocked", menu.Options.TechMode);
        Assert.True(menu.Options.CreateConfig().LiveSettings.TestHarness!.IgnoreTechGate);
        menu.Move(1); menu.Change(1);
        Assert.Equal(SkirmishDifficulty.Hard, menu.Options.Difficulty);
        menu.Move(1); menu.Change(1);
        Assert.Equal(SkirmishCommander.You, menu.Options.Commander);
        Assert.Contains("Commander: You", menu.Rows);
        menu.Change(-1);
        Assert.Equal(SkirmishCommander.AI, menu.Options.Commander);
        Assert.Equal(SkirmishScreenModel.RowCount, menu.Rows.Length);
        Assert.Equal((1f, 0d, 1f), new SkirmishOptions().Knobs);
        Assert.True(new SkirmishOptions { Difficulty = SkirmishDifficulty.Easy }.Knobs.Reaction > 0);
    }

    [SkippableTheory]
    [InlineData(4, 5)] [InlineData(14, 15)] [InlineData(29, 30)] [InlineData(59, 60)]
    public void Showcase_exact_counts_survive_worker_partitioning(int sideA, int sideB)
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        Assert.Equal(sideA, Showcase.ComposeShowcaseFleetCount(core, sideA).Sum(s => s.Count));
        Assert.Equal(sideB, Showcase.ComposeShowcaseFleetCount(core, sideB).Sum(s => s.Count));
        Assert.Equal(sideA, WorkerPartition.Split(sideA, 4).Sum());
        Assert.Equal(sideB, WorkerPartition.Split(sideB, 4).Sum());
    }

    [Fact]
    public void Pause_capability_is_local_and_zero_freezes_the_pilot_clock()
    {
        using var dedicated = new Srv();
        Assert.Throws<InvalidOperationException>(() => dedicated.SetSinglePlayerPaused(true));
        var clock = new OwnShipServerClock();
        clock.AddSample(10000, 1000);
        clock.SetTimeScale(0, 1500);
        Assert.Equal(10500u, clock.ServerTimeMs(9000));
        clock.SetTimeScale(.5, 9000);
        Assert.Equal(11000u, clock.ServerTimeMs(10000));
    }

    private sealed class PauseWorker : IWorker
    {
        internal readonly List<bool> Requests = [];
        public int Index => 0;
        public int Pid => 727;
        public bool Alive => true;
        public string? Failure => null;
        public Task<WorkerReply> CallAsync(string method, JsonElement parameters, CancellationToken ct)
        {
            Assert.Equal("__pause", method);
            Requests.Add(parameters.GetBoolean());
            return Task.FromResult(new WorkerReply(false, JsonSerializer.SerializeToElement(true)));
        }
        public void Dispose() { }
    }

    [Fact]
    public async Task Pause_also_freezes_supervisor_defense_and_coordination_time()
    {
        var worker = new PauseWorker();
        using var supervisor = new WorkerSupervisor([worker], coordinate: false);
        await supervisor.PauseAsync(true, default);
        double pausedAt = supervisor.CoordinationSeconds;
        await Task.Delay(100);
        Assert.Equal(pausedAt, supervisor.CoordinationSeconds);
        await supervisor.PauseAsync(false, default);
        await Task.Delay(20);
        Assert.True(supervisor.CoordinationSeconds > pausedAt);
        Assert.Equal(new[] { true, false }, worker.Requests);
    }

    [Fact]
    public void Driver_supports_single_worker_supervision_and_owner_tracking()
    {
        var options = DriverOptions.Parse(["127.0.0.1", "1234", "--skirmish", "4/5", "--workers", "1", "--parent", "123"]);
        Assert.Equal("4/5", options.Skirmish);
        Assert.Equal(123, options.Parent);
        Assert.Equal(new WorkerPartition(0, 1), DriverOptions.Parse(["127.0.0.1", "1234", "--worker", "1/1"]).Worker);
        Assert.Equal(new BotDifficulty(1, 0, 1), BotDifficulty.FromEnvironment());
    }
}

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]   // whole local matches (38-215 s); TT-1 tiering after the P727/SP-5 merge
public sealed class P727SkirmishLiveTests(ITestOutputHelper output)
{
    private static async Task Until(Func<bool> done, int seconds = 30)
    {
        var watch = Stopwatch.StartNew();
        while (!done() && watch.Elapsed.TotalSeconds < seconds) await Task.Delay(50);
        Assert.True(done(), "Skirmish condition timed out");
    }

    private static bool Exited(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            // Linux may briefly retain a dead adopted child as a zombie.
            if (OperatingSystem.IsLinux() && File.Exists($"/proc/{id}/stat")
                && File.ReadAllText($"/proc/{id}/stat").Split(')')[1].TrimStart().StartsWith('Z')) return true;
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
    }

    [SkippableFact]
    public async Task Local_start_battle_pause_resume_next_match_stop_leaves_no_processes()
    {
        Require.Artwork();
        int[] processes;
        ushort port;
        using (var host = await SkirmishHost.StartAsync(new() { TechMode = "unlocked" }))
        {
            processes = host.BotProcessIds; port = host.Port;
            Assert.NotEmpty(processes);
            var play = await SkirmishSmoke.PlayAsync(host, TimeSpan.FromSeconds(60));
            output.WriteLine(JsonSerializer.Serialize(play));
            Assert.True(play.Running);
            Assert.True(play.BotsAlive);
            Assert.Equal(9, play.Bots);
            Assert.True(play.PilotDistance > 1);
            await Until(() => host.Stats.TryGetProperty("combat", out var combat)
                && combat.GetProperty("shotsFired").GetDouble() > 0 && combat.GetProperty("shipHits").GetDouble() > 0, 150);
            output.WriteLine(host.Stats.GetRawText());
            await Until(() => host.OnSimulation(server => server.Ships.All(s => s.Ship.Id != play.PilotShipId)));
            await host.SetPausedAsync(true);
            double time = host.SimulationSeconds;
            var poses = host.OnSimulation(server => server.Ships.Select(s => (s.Ship.Id, s.Ship.Position, s.Ship.Velocity)).ToArray());
            var stats = host.Stats.GetProperty("combat").GetProperty("shotsFired").GetDouble();
            // Cross the native 30s/6-ping keepalive horizon while paused.
            await Task.Delay(40000);
            Assert.Equal(time, host.SimulationSeconds);
            Assert.Equal(poses, host.OnSimulation(server => server.Ships.Select(s => (s.Ship.Id, s.Ship.Position, s.Ship.Velocity)).ToArray()));
            Assert.True(host.Paused);
            Assert.True(host.BotsAlive);
            Assert.Equal(9, host.Stats.GetProperty("botCount").GetInt32());
            // An in-flight health sample may include the last pre-pause decision; settle then compare.
            var settled = host.Stats.GetProperty("combat").GetProperty("shotsFired").GetDouble();
            await Task.Delay(2500);
            Assert.Equal(settled, host.Stats.GetProperty("combat").GetProperty("shotsFired").GetDouble());
            Assert.True(settled >= stats);
            await host.SetPausedAsync(false);
            await Until(() => host.SimulationSeconds > time + .5);
            var old = host.OnSimulation(server => server.Missions[0]);
            host.OnSimulation(server => { P660NextMatchLiveTests.End(server); return true; });
            await Until(() => host.OnSimulation(server => !ReferenceEquals(old, server.Missions[0]) && server.Missions[0].Stage == MissionStage.Started), 90);
            Assert.True(host.BotsAlive);
            Assert.Equal(0, host.OnSimulation(server => server.SimPassStageFailures));
        }
        await Until(() => processes.All(Exited));
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start(); // Embedded transport released its listener as well.
    }

    [SkippableFact]
    public async Task Research_skirmish_uses_both_commanders_and_selected_civ_plans()
    {
        Require.Artwork();
        using var host = await SkirmishHost.StartAsync(new() { PlayerCiv = 101, EnemyCiv = 35, TechMode = "research" });
        await Until(() => host.Stats.ValueKind == JsonValueKind.Object
            && host.Stats.GetProperty("workers").EnumerateArray().Any(w =>
                w.GetProperty("report").TryGetProperty("research", out var research) && research.GetArrayLength() == 2));
        Assert.True(host.BotsAlive);
        Assert.Equal(new[] { 101, 35 }, host.OnSimulation(server => server.Missions[0].Sides.Select(side => (int)side.Civilization.CivilizationId).ToArray()));
        await host.SetPausedAsync(true);
        double time = host.SimulationSeconds;
        var researchBefore = host.Stats.GetProperty("workers")[0].GetProperty("report").GetProperty("research");
        Assert.Equal(new short[] { 0, 1 }, researchBefore.EnumerateArray().Select(r => r.GetProperty("Side").GetInt16()));
        await Task.Delay(2500);
        Assert.Equal(time, host.SimulationSeconds);
        await host.SetPausedAsync(false);
        await Until(() => host.SimulationSeconds > time);
    }

    [SkippableFact]
    public async Task Worker_death_is_reported_to_the_game()
    {
        Require.Artwork();
        using var host = await SkirmishHost.StartAsync(new() { TechMode = "unlocked" });
        using var worker = Process.GetProcessById(host.BotProcessIds[1]);
        worker.Kill();
        await Until(() => host.Failure is not null);
        Assert.False(host.BotsAlive);
        Assert.Contains("worker", host.Failure!, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableTheory]
    [InlineData(2)]
    [InlineData(8)]
    public async Task Killing_game_owner_reaps_supervisor_and_all_workers(int cores)
    {
        Require.Artwork();
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string proof = Path.Combine(root, "tools/skirmish-proof/bin", configuration, "net10.0/skirmish-proof.dll");
        string inventory = Path.Combine(Path.GetTempPath(), "p727-owner-" + Guid.NewGuid().ToString("N") + ".json");
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { proof, "--skirmish-owner-proof", inventory }) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_PROCESSOR_COUNT"] = cores.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var owner = Process.Start(start)!;
        var stdout = owner.StandardOutput.ReadToEndAsync(); var stderr = owner.StandardError.ReadToEndAsync();
        int[] children = [];
        try
        {
            await Until(() => File.Exists(inventory) || owner.HasExited, 180);
            Assert.False(owner.HasExited, await (owner.HasExited ? stderr : Task.FromResult("")));
            var data = JsonDocument.Parse(await File.ReadAllTextAsync(inventory));
            children = data.RootElement.GetProperty("pids").Deserialize<int[]>()!;
            Assert.True(children.Length >= 2);
            Assert.All(children, id => Assert.False(Exited(id)));
            owner.Kill(); // Deliberately do not use Kill(entireProcessTree: true).
            await owner.WaitForExitAsync();
            await Until(() => children.All(Exited), 30);
        }
        finally
        {
            if (!owner.HasExited) owner.Kill(entireProcessTree: true);
            foreach (int child in children.Where(id => !Exited(id)))
                try { using var process = Process.GetProcessById(child); process.Kill(entireProcessTree: true); } catch (ArgumentException) { }
            File.Delete(inventory);
            output.WriteLine(await stdout); output.WriteLine(await stderr);
        }
    }
}
