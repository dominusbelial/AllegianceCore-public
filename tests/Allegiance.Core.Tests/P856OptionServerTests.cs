using System.Net.Http.Json;
using System.Text.Json;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Lobby;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P856 — the production path of the two native create-game options:
/// <c>games[].allowFriendlyFire</c> / <c>games[].allowAlliedViz</c> (or the
/// <c>ALLEGIANCE_GAME_ALLOW_FRIENDLY_FIRE</c> /
/// <c>ALLEGIANCE_GAME_ALLOW_ALLIED_VIZ</c> environment overrides, or the
/// <c>--friendly-fire</c> / <c>--allied-viz</c> switches) → the assembled
/// mission's <see cref="Allegiance.Core.Model.Mission.AllowFriendlyFire"/> /
/// <see cref="Allegiance.Core.Model.Mission.AllowAlliedViz"/> → the
/// <c>MISSIONDEF(38)</c> bitfield block a joining client decodes, and the lobby
/// directory's game settings. Behaviour on and off is proven in
/// <see cref="P856FriendlyFireAlliedVizTests"/>.
/// </summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public sealed class P856OptionServerTests
{
    private static ServerConfig Config(bool friendlyFire, bool alliedViz) => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
        Games = [new()
        {
            Name = "P856", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
            SideNames = ["Alpha", "Bravo"], AutoStart = false,
            AllowFriendlyFire = friendlyFire, AllowAlliedViz = alliedViz,
        }],
    };

    private static void Pump(Srv server, Func<bool> done)
    {
        for (int i = 0; i < 5000 && !done(); i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.True(done(), "P856 loopback condition timed out");
    }

    [SkippableTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Configured_options_reach_the_mission_and_the_joining_client(bool friendlyFire, bool alliedViz)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        // Source-generated JSON roundtrip, so this exercises the real config
        // shape an operator writes, not a post-boot flag.
        var config = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(Config(friendlyFire, alliedViz), ServerJsonContext.Default.ServerConfig),
            ServerJsonContext.Default.ServerConfig)!;
        server.Boot(config);
        var mission = server.Missions[0];
        Assert.Equal(friendlyFire, mission.AllowFriendlyFire);
        Assert.Equal(alliedViz, mission.AllowAlliedViz);

        using var client = new ConnectionSession();
        client.Connect(server.BoundPort, "P856-options");
        client.SendLogonRequest(new()
        {
            CharacterName = "P856-options", FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", CrcFileList = -1,
            SteamAuthTicket = new byte[16], SteamID = 856001,
        });
        Pump(server, () => client.Ack is not null);
        client.SendPositionReq(0);
        Pump(server, () => { lock (client.Gate) return client.World.MissionBriefing is not null; });
        lock (client.Gate)
        {
            Assert.Equal(friendlyFire, client.World.MissionAllowFriendlyFire);
            Assert.Equal(alliedViz, client.World.MissionAllowAlliedViz);
            // The client's decoded MissionInfo snapshot keeps them across a
            // MISSION_STAGE refinement (they come only from MISSIONDEF).
            client.World.Apply(new MissionStageMessage { Stage = MissionStageMessage.StageStarting });
            Assert.Equal(friendlyFire, client.World.MissionAllowFriendlyFire);
            Assert.Equal(alliedViz, client.World.MissionAllowAlliedViz);
        }
        // The lobby directory projection carries the same two facts (P806
        // GameParams mirror of the native MissionInfo mission parameters).
        var lobby = LobbyGameParams.FromMission(mission);
        Assert.Equal(friendlyFire, lobby.AllowFriendlyFire);
        Assert.Equal(alliedViz, lobby.AllowAlliedViz);
        // Native defaults are off; a pre-P856 directory payload that omits the
        // two members therefore keeps publishing "off".
        Assert.False(new LobbyGameParams(true, 0x7fff, true, false, false, false, false,
            false, false, false, false, false, -1, 1000).AllowFriendlyFire);
    }

    /// <summary>A real heartbeat carries both options to a real in-process lobby
    /// and the public listing hands them back (the port's "mission info so
    /// clients see the setting"); a pre-P856 payload that omits them publishes
    /// native's off.</summary>
    [SkippableFact]
    public async Task Directory_publishes_both_option_switches_with_native_off_default()
    {
        using var app = new LobbyHarness();
        using var client = app.CreateClient();
        var game = new LobbyGameParams(
            AllowDevelopments: true, Lives: LobbyGameParams.UnlimitedLives, ScoresCount: true,
            ObjectModelCreated: true, InProgress: false, GoalConquest: true, GoalDeathmatch: false,
            GoalProsperity: false, GoalCountdown: false, GoalArtifacts: false, GoalFlags: false,
            GoalTerritory: false, MinRank: -1, MaxRank: 1000,
            AllowFriendlyFire: true, AllowAlliedViz: false);
        var http = new HttpClient(app.Server.CreateHandler())
        {
            BaseAddress = app.ClientOptions.BaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        var cfg = new LobbyConfig
        {
            Url = app.ClientOptions.BaseAddress.AbsoluteUri,
            DisplayName = "P856 Options Server",
            PublicAddress = "1.2.3.4",
        };
        using var hb = new LobbyHeartbeat(http, cfg, () => 1, "P856 Options", "PCore001.igc", "0.1.0", 5333,
            clock: null, livePlayerNames: null, liveGameParams: () => game);
        await hb.PumpAsync();

        var servers = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
        var mine = Assert.Single(servers!);
        Assert.NotNull(mine.Game);
        Assert.True(mine.Game!.AllowFriendlyFire);
        Assert.False(mine.Game.AllowAlliedViz);

        // Defaults: a payload without the two members is published as off.
        var defaults = new LobbyGameParams(true, LobbyGameParams.UnlimitedLives, true, true,
            false, true, false, false, false, false, false, false, -1, 1000);
        Assert.False(defaults.AllowFriendlyFire);
        Assert.False(defaults.AllowAlliedViz);
    }
}
