using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P930MatchCapTests(ITestOutputHelper output)
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object? Call(Srv server, string method, params object[] args) =>
        typeof(Srv).GetMethod(method, Hidden)!.Invoke(server, args);

    internal static ServerConfig Config() => new()
    {
        Port = 0,
        Broadcast = new() { ImmutableSnapshot = true, EncodeOnce = true, ParallelBuild = true, ShipUpdateModel = "native" },
        Transport = new() { Mode = "pool" },
        Games = [new() { Name = "P930", Map = "m2xspiderw.igc", Teams = 2,
            CivIds = [25, 3], SideNames = ["Alpha", "Bravo"], SkirmishPreset = true, AutoStart = false }]
    };

    [Fact]
    public void Source_generated_defaults_and_explicit_cap_roundtrip()
    {
        Assert.Equal(3600, JsonSerializer.Deserialize("{}", ServerJsonContext.Default.ServerConfig)!.Showcase.MatchCapSeconds);
        foreach (double seconds in new double[] { 0, 60, 90.5, 3600, 86400 })
        {
            var config = new ServerConfig { Showcase = new() { MatchCapSeconds = seconds } };
            config.Showcase.Validate();
            var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig), ServerJsonContext.Default.ServerConfig)!;
            Assert.Equal(seconds, copy.Showcase.MatchCapSeconds);
        }
    }

    [Theory]
    [InlineData(-1)][InlineData(.1)][InlineData(59.99)][InlineData(86401)]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(double.NegativeInfinity)]
    public void Invalid_cap_fails_validation(double seconds) => Assert.Contains("showcase.matchCapSeconds",
        Assert.Throws<InvalidDataException>(() => new ShowcaseConfig { MatchCapSeconds = seconds }.Validate()).Message);

    [SkippableTheory]
    [InlineData(true, 3600, 0, 3600)]
    [InlineData(false, 3600, 0, 0)]
    [InlineData(false, 3600, 7200, 7200)]
    [InlineData(true, 0, 0, 0)]
    [InlineData(true, 0, 7200, 7200)]
    [InlineData(true, 3600, 90, 90)]
    [InlineData(true, 3600, 7200, 3600)]
    public void Only_presets_receive_cap_and_shorter_explicit_countdown_wins(bool preset, double cap, double duration, float expected)
    {
        Require.Artwork();
        var config = Config(); config.Games[0].SkirmishPreset = preset;
        config.Games[0].GameLengthSeconds = duration;
        config.Showcase.MatchCapSeconds = cap;
        using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
        Assert.Equal(expected, server.Missions[0].Goals.GameLengthSeconds);
        Assert.Equal(duration, config.Games[0].GameLengthSeconds);
        Assert.Equal(0, config.Games[0].Goals.GameLengthSeconds);
    }

    [SkippableFact]
    public void Player_created_and_LAN_worlds_keep_human_countdown()
    {
        Require.Artwork();
        var config = Config();
        config.Games[0].GameLengthSeconds = 7200;
        using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
        var mission = (Mission)Call(server, "AssembleGameMission", config.Games[0], server.Missions[0].Core,
            typeof(Srv).GetField("_landingBays", Hidden)!.GetValue(server)!,
            typeof(Srv).GetField("_weaponMuzzles", Hidden)!.GetValue(server)!,
            typeof(Srv).GetField("_stationLandingBays", Hidden)!.GetValue(server)!,
            typeof(Srv).GetField("_collisionHulls", Hidden)!.GetValue(server)!, true)!;
        Assert.False(mission.IsShowcasePreset);
        Assert.Equal(7200, mission.Goals.GameLengthSeconds);
        server.Missions[0].PlayerCreated = true;
        server.Missions[0].Stage = MissionStage.Started;
        P660NextMatchLiveTests.End(server);
        Assert.True(server.Missions[0].PlayerCreated);
        Assert.False(server.Missions[0].IsShowcasePreset);
        Assert.Equal(7200, server.Missions[0].Goals.GameLengthSeconds);
        // LAN is the other ordinary-game exclusion in the shared assembly predicate.
        using var lan = new Srv { RequireLanListeners = true, SpawnInitialMinersAtBoot = false };
        lan.Boot(Config());
        Assert.False(lan.Missions[0].IsShowcasePreset);
        Assert.False(lan.Missions[0].Goals.IsCountdownGame);
    }

    [SkippableTheory]
    [InlineData(false, 2)][InlineData(true, 2)][InlineData(true, 3)]
    public void Fresh_rebuilds_map_goal_and_civ_rotations_reapply_cap(bool rotateMaps, int teams)
    {
        Require.Artwork();
        var config = Config(); var game = config.Games[0];
        game.Teams = teams; game.CivIds = new[] { 25, 3, 18 }.Take(teams).ToArray();
        game.SideNames = Enumerable.Range(0, teams).Select(i => $"Team{i}").ToArray();
        game.CivRotation = "alternate";
        config.Showcase.MatchCapSeconds = 120;
        config.Showcase.GoalRotation = new() { Entries = [
            new() { Name = "short", Goals = new() { GameLengthSeconds = 90 } },
            new() { Name = "long", Goals = new() { GameLengthSeconds = 600 } },
            new() { Name = "kills", Goals = new() { TeamKills = 10 } }] };
        if (teams == 3) game.MapGen = new() { Type = "SingleRing", Seed = 930 };
        if (rotateMaps) config.Showcase.MapRotation = new() { Seed = 930, Entries = teams == 2
            ? [new() { Map = "m2xspiderw.igc" }, new() { MapGen = new() { Type = "SingleRing" } }]
            : [new() { MapGen = new() { Type = "SingleRing" } }, new() { MapGen = new() { Type = "PinWheel" } }] };
        using var log = new StringWriter(); var saved = Console.Out; Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            for (int match = 0; match < 4; match++)
            {
                var mission = server.Missions[0];
                Assert.Equal(match % 3 == 0 ? 90 : 120, mission.Goals.GameLengthSeconds);
                Assert.Equal(teams, mission.Sides.Count);
                Assert.Equal(Enumerable.Range(0, teams).Select(side => game.CivIds[(side + match) % teams]),
                    mission.Sides.Select(side => (int)side.Civilization.CivilizationId));
                Assert.Contains($"cap={mission.Goals.GameLengthSeconds}", log.ToString().Split('\n').Last(l => l.StartsWith("[matchup]")));
                mission.Stage = MissionStage.Started;
                P660NextMatchLiveTests.End(server);
                Assert.NotSame(mission, server.Missions[0]);
                Assert.Equal(0, server.Missions[0].CountdownStart);
            }
            Assert.Equal(600, config.Showcase.GoalRotation.Entries[1].Goals.GameLengthSeconds);
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableTheory]
    [InlineData(60, 0, 20, 130)]
    [InlineData(0, 0, 300, 410)]
    [InlineData(0, 60, 300, 160)]
    public void Earlier_finale_and_disabled_cap_keep_their_deadlines(double cap, double authored, double timeout, double expected)
    {
        Require.Artwork();
        var config = Config(); config.Showcase.TechMode = "research";
        config.Showcase.MatchCapSeconds = cap; config.Showcase.FinaleTimeoutSeconds = timeout;
        config.Games[0].GameLengthSeconds = authored;
        using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
        var mission = server.Missions[0];
        var ship = new Ship(29000, mission.Core.HullTypes.Values.First(), default, default, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, mission.Sectors[0]);
        var peer = new ConnectionId(930);
        server.TestSeedPeers([(peer, ship, mission.Sectors[0])]);
        mission.Stage = MissionStage.Started; mission.CountdownStart = 100;
        typeof(Srv).GetField("_simNow", Hidden)!.SetValue(server, 110d);
        Call(server, "ArmResearchFinale", peer, new ShowcaseFinaleRequest { Winner = 0, Epoch = Guid.ParseExact(mission.ResearchEpoch, "N") });
        var finales = (Dictionary<Mission, (double Deadline, short ArmedBy)>)typeof(Srv).GetField("_researchFinales", Hidden)!.GetValue(server)!;
        Assert.Equal(expected, finales[mission].Deadline);
        if (expected == 130)
        {
            mission.Sides[1].Kills = 5;
            var packets = new List<byte[]>();
            server._testSendCapture = (_, packet) => packets.AddRange(FedPacket.Unpack(packet));
            typeof(Srv).GetField("_simNow", Hidden)!.SetValue(server, expected);
            Call(server, "AdvanceResearchFinales", expected);
            server.FlushBroadcastSnapshots();
            var over = GameOver.Decode(Assert.Single(packets, m => FedMessageHeader.Read(m).Fmid == GameOver.Fmid));
            Assert.Equal((short)1, over.SideWinner); // request side does not choose the winner
            Assert.Equal(MissionStage.Over, mission.Stage);
        }
    }

    [SkippableTheory]
    [InlineData(false, false)][InlineData(true, false)][InlineData(true, true)]
    public void Late_finale_clamps_to_cap_and_same_tick_emits_one_result(bool armFinale, bool tie)
    {
        Require.Artwork();
        var config = Config(); config.Showcase.TechMode = "research";
        config.Showcase.MatchCapSeconds = 60; config.Showcase.FinaleTimeoutSeconds = 300;
        using var log = new StringWriter(); var saved = Console.Out; Console.SetOut(log);
        try
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false }; server.Boot(config);
            var mission = server.Missions[0]; mission.Skills = null;
            var ship = new Ship(29000, mission.Core.HullTypes.Values.First(), default, default, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, mission.Sectors[0]);
            var peer = new ConnectionId(930);
            server.TestSeedPeers([(peer, ship, mission.Sectors[0])]);
            var messages = new List<byte[]>();
            server._testSendCapture = (_, packet) => messages.AddRange(FedPacket.Unpack(packet));
            mission.Stage = MissionStage.Started; mission.CountdownStart = 100;
            if (!tie) mission.Sides[1].Kills = 5;
            typeof(Srv).GetField("_simNow", Hidden)!.SetValue(server, 150d);
            if (armFinale)
            {
                Call(server, "ArmResearchFinale", peer, new ShowcaseFinaleRequest { Winner = 0, Epoch = Guid.ParseExact(mission.ResearchEpoch, "N") });
                var finales = (Dictionary<Mission, (double Deadline, short ArmedBy)>)typeof(Srv).GetField("_researchFinales", Hidden)!.GetValue(server)!;
                Assert.Equal(160, finales[mission].Deadline);
                Call(server, "AdvanceResearchFinales", 160d);
                Call(server, "AdvancePeriodicRules", 160d);
                Assert.Equal(MissionStage.Started, mission.Stage); // native strict > timeout
            }
            typeof(Srv).GetField("_simNow", Hidden)!.SetValue(server, 160.05);
            Call(server, "AdvanceResearchFinales", 160.05);
            Call(server, "AdvancePeriodicRules", 160.05);
            Call(server, "AdvanceResearchFinales", 160.05);
            Call(server, "AdvancePeriodicRules", 160.05);
            server.FlushBroadcastSnapshots();
            Assert.Equal(MissionStage.Over, mission.Stage);
            var over = GameOver.Decode(Assert.Single(messages, m => FedMessageHeader.Read(m).Fmid == GameOver.Fmid));
            Assert.Equal(tie ? GameOver.NA : (short)1, over.SideWinner);
            string receipt = Assert.Single(log.ToString().Split('\n'), l => l.StartsWith("[finale] cap winner"));
            Assert.Contains($"side={(tie ? -1 : 1)} by={(tie ? "draw" : "kills")}", receipt);
            Assert.Contains("elapsed=60.05", receipt);
            Assert.Single(log.ToString().Split('\n'), l => l.StartsWith("[finale] deadline winner"));
            Assert.Single(log.ToString().Split('\n'), l => l.StartsWith("[gameover] mission"));
            output.WriteLine(receipt);
        }
        finally { Console.SetOut(saved); }
    }
}
