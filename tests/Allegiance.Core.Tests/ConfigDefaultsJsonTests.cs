using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Allegiance.Server;
using Allegiance.Lobby;
using Allegiance.TestInfra;
using Microsoft.Extensions.Configuration;

namespace Allegiance.Core.Tests;

/// <summary>
/// clash44 (2026-10-15): the rig's server.json has no per-game "treasure" section,
/// and P663's <c>GameConfig.Treasure { get; init; } = new()</c> came back null from
/// the source-generated JSON reader (init-only properties are assigned in the
/// generated object initializer, so an absent field overwrites the default with
/// null) — the server crash-looped at boot in AssembleGameMission. Every earlier
/// test built configs in code, so none loaded JSON. These load the real loader.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ConfigDefaultsJsonTests
{
    [Fact]
    public void Every_source_generated_context_preserves_all_defaults_recursively() =>
        JsonDefaultsGuard.ContextsIn(typeof(ServerConfig).Assembly);

    [Fact]
    public void Lobby_json_and_configuration_binding_preserve_defaults()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        JsonDefaultsGuard.Graph(options.GetTypeInfo(typeof(LobbySettings)));
        JsonDefaultsGuard.Graph(options.GetTypeInfo(typeof(RegisterRequest)));
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(new MemoryStream("{\"Lobby\":{}}"u8.ToArray())).Build();
        var settings = new LobbySettings();
        configuration.GetSection("Lobby").Bind(settings);
        JsonDefaultsGuard.Equal(new LobbySettings(), settings, "Lobby binding {}");
        using var absent = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(new MemoryStream("{}"u8.ToArray())).Build();
        absent.GetSection("Lobby").Bind(settings);
        JsonDefaultsGuard.Equal(new LobbySettings(), settings, "Lobby binding absent");
    }

    [Theory]
    [InlineData("{\"games\":[{\"name\":\"T\",\"map\":\"m2xspiderw.igc\",\"teams\":2,\"civIds\":[25,3],\"sideNames\":[\"A\",\"B\"]}]}")]
    [InlineData("{\"games\":[{\"name\":\"T\",\"map\":\"m2xspiderw.igc\",\"teams\":2,\"civIds\":[1,3],\"sideNames\":[\"A\",\"B\"],\"treasure\":{}}]}")]
    public void Game_without_or_with_empty_treasure_section_keeps_a_usable_default(string json)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            var g = Assert.Single(ServerConfigLoader.Load(path, []).Games);
            Assert.NotNull(g.Treasure);
            JsonDefaultsGuard.Equal(new Allegiance.Core.Model.TreasureParameters(), g.Treasure, "Game.Treasure");
            Assert.True(g.AllowDevelopments);
            Assert.Equal(100, g.MaxPlayersPerTeam);
            Assert.Equal(32766, g.MaxTeamImbalance);
            g.Treasure.Validate();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Absent_sections_keep_their_defaults()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "{}");
            var c = ServerConfigLoader.Load(path, []);
            Assert.NotNull(c.Sim);
            Assert.NotNull(c.LiveSettings);
        }
        finally { File.Delete(path); }
    }
    [SkippableFact]
    public void Rig_style_json_boots_with_effective_defaults()
    {
        Require.Artwork();
        var art = JsonSerializer.Serialize(Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork));
        var json = $$$"""
            {"artPath":{{{art}}},"staticCore":"PCore001.igc","port":0,
             "games":[{"name":"Test Mission","map":"m2xspiderw.igc","teams":2,"civIds":[25,3],"sideNames":["Alpha","Bravo"]}],
             "lobby":{"url":"http://lobby:8080","heartbeatIntervalSeconds":25},
             "transport":{"mode":"pool","maxConnectionsPerIp":512},"sim":{"crowdingPenalty":"off"},
             "liveSettings":{"timeDilation":"auto"}}
            """;
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, json);
            using var server = new Allegiance.Server.Server();
            server.Boot(path, []);
            Assert.NotEqual((ushort)0, server.BoundPort);
            var c = server.Config;
            var g = Assert.Single(c.Games);
            var mission = Assert.Single(server.Missions);
            Assert.True(g.AllowDevelopments);
            Assert.True(mission.AllowDevelopments);
            Assert.Equal(100, g.MaxPlayersPerTeam);
            Assert.Equal(32766, g.MaxTeamImbalance);
            JsonDefaultsGuard.Equal(new Allegiance.Core.Model.TreasureParameters(), mission.TreasureParameters, "boot treasure");
            Assert.Same(g.Treasure, mission.TreasureParameters);
            Assert.Equal(25, c.Lobby!.HeartbeatIntervalSeconds);
            Assert.Equal("", c.Lobby.DisplayName);
            Assert.Equal("", c.Lobby.PublicAddress);
            Assert.Equal("pool", c.Transport!.Mode);
            Assert.Equal(512, c.Transport.MaxConnectionsPerIp);
            Assert.False(mission.CrowdingPenaltyEnabled);
            Assert.Equal("auto", c.LiveSettings.TimeDilation);
            Assert.True(c.LiveSettings.AutoNextMatch.Enabled);
            Assert.Equal(30, c.LiveSettings.AutoNextMatch.IntermissionSeconds);
            // Exercise the native payday gate with the values from JSON. With
            // either lost AllowDevelopments initializer no side receives income.
            mission.Stage = Allegiance.Core.Sim.MissionStage.Started;
            mission.Update(1);
            var balances = mission.Sides.Select(side => side.Money).ToArray();
            mission.Update(61);
            for (int i = 0; i < mission.Sides.Count; i++)
                Assert.True(mission.Sides[i].Money > balances[i], "default developments must accrue payday income");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Side_select_auto_seat_defaults_off_and_round_trips_through_the_aot_context()
    {
        // P763 — absent = null = native (wait for POSITIONREQ); a configured
        // value survives the source-generated, AOT-safe ServerJsonContext.
        var absent = JsonSerializer.Deserialize("{}", ServerJsonContext.Default.ServerConfig)!;
        Assert.Null(absent.SideSelectAutoSeatSeconds);

        var zero = JsonSerializer.Deserialize("""{"sideSelectAutoSeatSeconds":0}""", ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal(0, zero.SideSelectAutoSeatSeconds);

        var configured = JsonSerializer.Deserialize("""{"sideSelectAutoSeatSeconds":30}""", ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal(30, configured.SideSelectAutoSeatSeconds);

        string json = JsonSerializer.Serialize(configured, ServerJsonContext.Default.ServerConfig);
        Assert.Contains("\"sideSelectAutoSeatSeconds\":30", json);
        var roundTripped = JsonSerializer.Deserialize(json, ServerJsonContext.Default.ServerConfig)!;
        Assert.Equal(30, roundTripped.SideSelectAutoSeatSeconds);
    }

    [Fact]
    public void Explicit_false_zero_and_nullable_overrides_still_bind()
    {
        var c = JsonSerializer.Deserialize("""
            {"games":[{"allowDevelopments":false,"maxPlayersPerTeam":0,"maxTeamImbalance":32767,
              "treasure":{"allowDevelopments":false,"playerSectorTreasureRate":0,"neutralSectorTreasureRate":0,
                "playerStartSet":0,"neutralStartSet":-1,"playerRegenerateSet":-1,"neutralRegenerateSet":0},
              "mapGen":{"playerSectorMineableAsteroids":0,"flagsGame":false}}],
             "lobby":{"heartbeatIntervalSeconds":0}}
            """, ServerJsonContext.Default.ServerConfig)!;
        var g = Assert.Single(c.Games);
        Assert.False(g.AllowDevelopments);
        Assert.Equal(0, g.MaxPlayersPerTeam);
        Assert.Equal(32767, g.MaxTeamImbalance);
        Assert.False(g.Treasure.AllowDevelopments);
        Assert.Equal(0f, g.Treasure.PlayerSectorTreasureRate);
        Assert.Equal(0f, g.Treasure.NeutralSectorTreasureRate);
        Assert.Equal((short)0, g.Treasure.PlayerStartSet);
        Assert.Equal((short)-1, g.Treasure.NeutralStartSet);
        Assert.Equal((short)-1, g.Treasure.PlayerRegenerateSet);
        Assert.Equal((short)0, g.Treasure.NeutralRegenerateSet);
        Assert.Equal(0, g.MapGen!.PlayerSectorMineableAsteroids);
        Assert.False(g.MapGen.FlagsGame);
        Assert.Equal(0, c.Lobby!.HeartbeatIntervalSeconds);
    }
}
