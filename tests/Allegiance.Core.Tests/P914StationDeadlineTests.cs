using System.Reflection;
using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P914StationDeadlineTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static ServerConfig Config(bool preset, bool enabled) => new()
    {
        Port = 0, ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc",
        Showcase = new() { StationKillsDecideDeadline = enabled },
        Games = [new() { Name = "P914", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
            SideNames = ["Alpha", "Bravo"], AutoStart = false, SkirmishPreset = preset }]
    };

    private static Ship Launcher(Mission mission, short side = 0)
    {
        var ship = new Ship((short)(29000 + side), mission.Core.HullTypes.Values.First(),
            new Vector3(20000, 20000, 20000), Vector3.Zero, Orientation.Identity) { SideId = side };
        mission.PlaceShip(ship, mission.Sectors[0]);
        return ship;
    }

    private static Station Target(Mission mission, short id, short side = 1)
    {
        var type = mission.Core.StationTypes.Values.First(t => t.MaxArmorHitPoints > 0 && t.MaxShieldHitPoints > 0
            && !t.HasCapability(DataStationTypeIGC.SabmPedestal)) with { SabmCapabilities = 0 };
        var station = new Station(new DataStationIGC([100000, 100000, 0], [0, 1, 0], [1, 0, 0],
            [0, 0, 0, 1], mission.Sectors[0].Id, side, id, type.StationTypeId, 255, 255, "P914 base"), type);
        mission.PlaceStation(station, mission.Sectors[0]);
        return station;
    }

    private static byte LethalType(Mission mission, Station station) => mission.Core.ProjectileTypes.Values.First(p =>
        p.Power > 0 && mission.Core.Constants.DamageConstants[p.DamageType & 0x1f][station.Type.DefenseTypeShield] > 0
        && mission.Core.Constants.DamageConstants[p.DamageType & 0x1f][station.Type.DefenseTypeArmor] > 0).DamageType;

    private static void Destroy(Mission mission, Station station, Allegiance.Core.Sim.Model? launcher)
    {
        Assert.Equal(DamageResult.Killed, station.ReceiveDamage(LethalType(mission, station), 1e9f,
            mission.LastUpdate, station.Center, station.Center, launcher));
        mission.Sectors[0].RemoveStation(station);
    }

    [Theory]
    [InlineData("{}", false)]
    [InlineData("{\"showcase\":{}}", false)]
    [InlineData("{\"showcase\":{\"stationKillsDecideDeadline\":true}}", true)]
    public void Source_generated_config_defaults_roundtrip_and_validate(string json, bool enabled)
    {
        var config = JsonSerializer.Deserialize(json, ServerJsonContext.Default.ServerConfig)!;
        config.ResolveShowcaseMode();
        Assert.Equal(enabled, config.Showcase.StationKillsDecideDeadline);
        var copy = JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig),
            ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal(enabled, copy.Showcase.StationKillsDecideDeadline);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(
            "{\"showcase\":{\"stationKillsDecideDeadline\":\"true\"}}", ServerJsonContext.Default.ServerConfig));
    }

    // Boot, real damage, periodic countdown, GAME_OVER/CHATMESSAGE encoding and rotation.
    // This recreates b59's 901-978 shape with two station kills by the losing ship-kill side.
    [SkippableTheory]
    [InlineData(false, false, 0, 1)]
    [InlineData(false, true, 0, 1)]
    [InlineData(true, false, 2, 1)]
    [InlineData(true, true, 2, 0)]
    [InlineData(false, true, 2, 1)]
    [InlineData(true, true, 0, 1)]
    [InlineData(true, true, 1, 1)]
    [InlineData(true, true, 3, -1)]
    [InlineData(true, true, 4, -1)]
    [InlineData(true, true, 5, 0)]
    public void Real_deadline_preserves_off_and_ordinary_paths_and_falls_through_ties(
        bool preset, bool enabled, int shape, int expectedWinner)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = Config(preset, enabled);
        server.Boot(JsonSerializer.Deserialize(JsonSerializer.Serialize(config, ServerJsonContext.Default.ServerConfig),
            ServerJsonContext.Default.ServerConfig)!);
        var mission = server.Missions[0];
        Assert.Equal(preset, mission.IsShowcasePreset);
        mission.Skills = null;
        var launcher = Launcher(mission);
        var other = Launcher(mission, 1);
        server.TestSeedPeers([(new ConnectionId(914), launcher, mission.Sectors[0])]);
        var messages = new List<byte[]>();
        var notable = new List<string>();
        server.Notable += notable.Add;
        server._testSendCapture = (_, packet) => messages.AddRange(FedPacket.Unpack(packet));
        // shape 0: zero; 1: tied nonzero; 2: b59; 3: zero and tied ship stats.
        if (shape is 1 or 2 or 4 or 5)
        {
            Destroy(mission, Target(mission, 30000), launcher);
            if (shape == 2) Destroy(mission, Target(mission, 30001), launcher);
            else Destroy(mission, Target(mission, 30001, 0), other);
        }
        mission.Sides[0].Kills = 901;
        mission.Sides[1].Kills = (short)(shape >= 3 ? 901 : 978);
        if (shape == 5) mission.Sides[1].Deaths = 1;
        mission.Stage = MissionStage.Started;
        mission.CountdownDuration = 1;
        mission.CountdownStart = mission.LastUpdate;
        string values = "0:kills=901/deaths=0,1:kills=" + (shape == 3 ? "901" : "978") + "/deaths=" + (shape == 5 ? "1" : "0");
        if (!enabled || !preset)
        {
            Assert.Equal("kills", mission.EvaluateCountdown(mission.LastUpdate, true, enabled).Criterion);
            Assert.Equal(values, mission.CountdownValues("kills"));
        }
        string[] summary = mission.StationKillSummary();
        string stationValues = mission.CountdownValues("station-kills");
        for (int i = 0; i < 30 && mission.Stage != MissionStage.Over; i++) server.TickSim();
        server.FlushBroadcastSnapshots();
        Assert.Equal(MissionStage.Over, mission.Stage);
        var result = GameOver.Decode(Assert.Single(messages, m => FedMessageHeader.Read(m).Fmid == GameOver.Fmid));
        Assert.Equal((short)expectedWinner, result.SideWinner);
        string criterion = preset && enabled && shape == 2 ? "station-kills" : "kills";
        string reason = expectedWinner < 0 ? "The clock has stopped. The game is a draw."
            : $"The clock has stopped. The winner: {(expectedWinner == 0 ? "Alpha" : "Bravo")} by {criterion}";
        Assert.Equal(reason, result.GameoverMessage.Split('\n')[0]);
        var feed = messages.Where(m => FedMessageHeader.Read(m).Fmid == ChatMessage.Fmid)
            .Select(m => ChatMessage.Decode(m).Message).ToArray();
        if (preset)
        {
            Assert.Equal(summary, result.GameoverMessage.Split('\n')[1..]);
            Assert.All(summary, line => Assert.Contains(line, feed));
            Assert.All(summary, line => Assert.Contains("[finale] " + line, notable));
            Assert.Equal(mission.StationKills(0), result.Sides[0].CBaseKills);
        }
        else
        {
            Assert.Equal(reason, result.GameoverMessage);
            Assert.DoesNotContain(notable, line => line.StartsWith("[finale] Station kills:") || line.StartsWith("[finale] Enemy base shattered:"));
            Assert.DoesNotContain(feed, line => line.StartsWith("Station kills:") || line.StartsWith("Enemy base shattered:"));
        }
        Assert.NotSame(mission, server.Missions[0]);
        Assert.Equal(0, server.Missions[0].StationKills(0));
        Assert.Equal(0, server.Missions[0].UncreditedStationDestructions);
        (int a, int b) stations = shape == 2 ? (2, 0) : shape is 1 or 4 or 5 ? (1, 1) : (0, 0);
        string[] ships = values.Split(',');
        Assert.Equal($"0:stations={stations.a}/{ships[0][2..]},1:stations={stations.b}/{ships[1][2..]}", stationValues);
    }

    [SkippableFact]
    public void Research_finale_uses_same_rule_and_logs_the_deciding_values()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = Config(false, true);
        config.Showcase.TechMode = "research";
        config.Showcase.FinaleTimeoutSeconds = 1;
        server.Boot(config);
        var mission = server.Missions[0];
        Assert.True(mission.IsShowcasePreset);
        var launcher = Launcher(mission);
        var conn = new ConnectionId(914);
        server.TestSeedPeers([(conn, launcher, mission.Sectors[0])]);
        mission.Stage = MissionStage.Started;
        mission.Sides[0].Kills = 901; mission.Sides[1].Kills = 978;
        Destroy(mission, Target(mission, 30000), launcher);
        Destroy(mission, Target(mission, 30001), launcher);
        var packets = new List<byte[]>();
        server._testSendCapture = (_, p) => packets.AddRange(FedPacket.Unpack(p));
        typeof(Srv).GetMethod("ArmResearchFinale", Hidden)!.Invoke(server,
            [conn, new ShowcaseFinaleRequest { Epoch = Guid.Parse(mission.ResearchEpoch), Winner = 0 }]);
        // Advance via the real tick and expiry path (not a fabricated countdown result).
        for (int i = 0; i < 30 && mission.Stage != MissionStage.Over; i++) server.TickSim();
        server.FlushBroadcastSnapshots();
        var result = GameOver.Decode(Assert.Single(packets, p => FedMessageHeader.Read(p).Fmid == GameOver.Fmid));
        Assert.Equal((short)0, result.SideWinner);
        Assert.StartsWith("The clock has stopped. The winner: Alpha by station-kills\n", result.GameoverMessage);
        Assert.Contains(packets, p => FedMessageHeader.Read(p).Fmid == ChatMessage.Fmid
            && ChatMessage.Decode(p).Message == "Station kills: Alpha=2, Bravo=0");
    }

    [SkippableFact]
    public void Player_created_world_does_not_inherit_the_operators_rotating_preset()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = Config(false, true);
        config.Showcase.GoalRotation = new();
        server.Boot(config);
        Assert.True(server.Missions[0].IsShowcasePreset);
        var parameters = new MissionParamsMsg
        {
            StrGameName = "P914 player world", SzIgcStaticFile = "PCore001.igc", NTems = 2,
            NTotalMaxPlayersPerGame = 32, NMaxPlayersPerTeam = 12,
            ILives = MissionDef.UnlimitedLives, IGoalConquestPercentage = 100, IGoalTerritoryPercentage = 100
        };
        var mission = server.ProvisionPlayerGame(new ClaimedGame("P914", "local", "PCore001.igc",
            "XXV_simple.igc", "P914 player world", Convert.ToBase64String(parameters.Encode()), "Claimed"));
        Assert.NotNull(mission);
        Assert.True(mission.PlayerCreated);
        Assert.False(mission.IsShowcasePreset);
        Destroy(mission, Target(mission, 30000), Launcher(mission));
        mission.Stage = MissionStage.Started;
        mission.Sides[0].Kills = 901; mission.Sides[1].Kills = 978;
        var result = mission.EvaluateCountdown(mission.LastUpdate, true, true);
        Assert.Equal("kills", result.Criterion);
        Assert.Same(mission.Sides[1], result.Winner);
    }

    [SkippableFact]
    public void Count_includes_nonship_and_retired_launchers_tracks_collapse_excludes_capture_and_deduplicates()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(true, true));
        var mission = server.Missions[0];
        mission.Skills = null;
        var ship = Launcher(mission);
        var captured = Target(mission, 30000);
        captured.Capture(0);
        mission.Sectors[0].RemoveStation(captured);
        Assert.Equal(0, mission.StationKills(0));
        var probe = new Probe(29002, mission.Sectors[0], mission.Core.ProbeTypes.Values.First(), Vector3.Zero, 0, 0);
        var target = Target(mission, 30001);
        Destroy(mission, target, probe);
        Assert.Equal(1, mission.StationKills(0));
        target.ReceiveDamage(LethalType(mission, target), 1e9f, 0, target.Center, target.Center, probe);
        mission.Site.KillStationEvent(target, probe, 1e9f); // host callback must not double count
        Assert.Equal(1, mission.StationKills(0));
        // A missile retains its launcher object after that ship leaves the registry.
        var missileTarget = Target(mission, 30002);
        var sector = mission.Sectors[0];
        byte damage = LethalType(mission, missileTarget);
        var missileType = mission.Core.MissileTypes.Values.First() with
        { Power = 1e9f, BlastPower = 1e9f, BlastRadius = 100, DamageType = damage,
            Acceleration = 0, ReadyTime = 0, Lifespan = 10, Radius = 1, EabmCapabilities = 0 };
        var missile = new Missile(29003, sector, missileType,
            missileTarget.Center + new Vector3(0, 0, -4 * missileTarget.Radius), new Vector3(0, 0, 50 * missileTarget.Radius),
            new Vector3(0, 0, 1), sector.LastUpdate, ship);
        sector.Add(missile);
        sector.Remove(ship);
        Assert.DoesNotContain(ship, sector.Models);
        for (int i = 0; i < 3 && !missileTarget.Dead; i++) sector.Update(sector.LastUpdate + .05);
        Assert.True(missileTarget.Dead, $"missile present={sector.Models.Contains(missile)} pos={missile.Position} target={missileTarget.Center} radius={missileTarget.Radius} shield={missileTarget.ShieldFraction} hull={missileTarget.ArmorFraction}");
        Assert.DoesNotContain(missileTarget, sector.StationRuntimes);
        Assert.Equal(2, mission.StationKills(0)); // direct impact + splash count once
        Destroy(mission, Target(mission, 30003), null); // collapse / ownerless blast
        Assert.Equal(1, mission.UncreditedStationDestructions);
        var ownerless = new Missile(29004, mission.Sectors[0], mission.Core.MissileTypes.Values.First(),
            Vector3.Zero, Vector3.Zero, new Vector3(0, 0, 1), 0);
        Destroy(mission, Target(mission, 30004), ownerless);
        Assert.Equal(2, mission.UncreditedStationDestructions);
        mission.AllowFriendlyFire = true;
        Destroy(mission, Target(mission, 30005, 0), probe);
        Assert.Equal(3, mission.UncreditedStationDestructions);
        Assert.Equal(2, mission.StationKills(0));
    }

    [SkippableTheory]
    [InlineData("conquest")] [InlineData("territory")] [InlineData("prosperity")]
    [InlineData("artifacts")] [InlineData("flags")]
    public void Native_goal_criteria_still_precede_station_kills(string goal)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(true, true));
        var mission = server.Missions[0];
        Destroy(mission, Target(mission, 30000), Launcher(mission));
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.StationRuntimes.ToArray()) sector.RemoveStation(station);
        mission.Stage = MissionStage.Started;
        var side = mission.Sides[1];
        switch (goal)
        {
            case "conquest":
                mission.Goals.ConquestPercentage = 100;
                var type = mission.Core.StationTypes.Values.First() with { SabmCapabilities = DataStationTypeIGC.SabmFlag };
                mission.PlaceStation(new Station(new DataStationIGC([0, 0, 0], [0, 1, 0], [1, 0, 0],
                    [0, 0, 0, 1], mission.Sectors[0].Id, 1, 30002, type.StationTypeId, 255, 255, "Flag base"), type), mission.Sectors[0]);
                break;
            case "territory": mission.Goals.TerritoryPercentage = 50; side.TerritoryCount = 1; break;
            case "prosperity":
                mission.Goals.TeamMoney = 1;
                side.Buckets.First(b => b.Development?.DevelopmentId == Mission.TeamMoneyDevelopmentId).SetTimeAndMoney(1, 1);
                break;
            case "artifacts": mission.Goals.Artifacts = 10; side.Artifacts = 1; break;
            case "flags": mission.Goals.Flags = 10; side.Flags = 1; break;
        }
        var result = mission.EvaluateCountdown(mission.LastUpdate, true, true);
        Assert.Equal(goal, result.Criterion);
        Assert.Same(side, result.Winner);
    }
}
