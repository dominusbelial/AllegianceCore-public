using System.Net.Http.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Lobby;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P806 — the game settings the server publishes to the directory for the
/// client's SERVER-LIST filters (native <c>MissionInfo</c>'s accessors,
/// <c>clintlib/clintlib.h:540-557</c>). Two halves:
/// <list type="bullet">
/// <item><see cref="LobbyGameParams.FromMission"/> maps the live mission's
/// settings (allow developments, lives, the P191/P302/P303 win flags,
/// countdown, stage) onto the native fields, using native
/// <c>MissionParams::Reset</c>'s values for the settings this port does not
/// model (scores count, object-model created, rank window);</item>
/// <item>a real heartbeat carries that object to a real in-process lobby and the
/// public listing hands it back.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P806LobbyGameParamsTests
{
    private static StaticCore Core()
    {
        var path = Corpus.Files.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("PCore001.igc", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("PCore001.igc not in corpus");
        return StaticCore.Load(IgcReader.ParseFile(path));
    }

    private static MissionMap DemoMap()
    {
        var path = Corpus.Files.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("XXV_simple.igc", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("XXV_simple.igc not in corpus");
        return MissionMap.Load(IgcReader.ParseFile(path));
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FromMission_maps_the_live_mission_settings_and_native_defaults()
    {
        Require.Artwork();
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, DemoMap(), [new SideSpec(0, "Team 1", civ)], new NullIgcSite());

        // Native MissionParams::Reset defaults for the port's fresh mission:
        // developments on (igc.h:1261), unlimited lives (:1291), no win condition
        // flags (the port opts in per mission), not in progress.
        var defaults = LobbyGameParams.FromMission(m);
        Assert.True(defaults.AllowDevelopments);
        Assert.Equal(LobbyGameParams.UnlimitedLives, defaults.Lives);
        Assert.Equal(0x7fff, defaults.Lives);
        Assert.True(defaults.ScoresCount);            // native Reset + GameType::Apply
        Assert.True(defaults.ObjectModelCreated);     // server-app-created mission (:1150)
        Assert.Equal(-1, defaults.MinRank);           // igc.h:1301
        Assert.Equal(1000, defaults.MaxRank);         // igc.h:1302
        Assert.False(defaults.InProgress);

        // The P191/P302/P303 mission flags map one-to-one onto the native goals.
        m.Goals.ConquestPercentage = 65;
        m.Goals.TeamKills = 1;
        m.Goals.TeamMoney = 1;
        m.Goals.Artifacts = 1;
        m.Goals.Flags = 1;
        m.Goals.TerritoryPercentage = 50;
        m.CountdownDuration = 600;
        m.Lives = 4;
        m.AllowDevelopments = false;
        m.Stage = MissionStage.Started;
        var live = LobbyGameParams.FromMission(m);
        Assert.True(live.GoalConquest);
        Assert.True(live.GoalDeathmatch);             // KillsEnabled ⇔ IsDeathMatchGame
        Assert.True(live.GoalProsperity);
        Assert.True(live.GoalArtifacts);
        Assert.True(live.GoalFlags);
        Assert.True(live.GoalTerritory);
        Assert.True(live.GoalCountdown);              // dtGameLength != 0
        Assert.False(live.AllowDevelopments);
        Assert.Equal(4, live.Lives);
        Assert.True(live.InProgress);                 // STAGE_STARTED
    }

    [Fact]
    public async Task Heartbeat_publishes_the_game_settings_to_a_real_directory()
    {
        using var app = new LobbyHarness();
        using var client = app.CreateClient();

        var game = new LobbyGameParams(
            AllowDevelopments: false,
            Lives: 2,
            ScoresCount: true,
            ObjectModelCreated: true,
            InProgress: true,
            GoalConquest: false,
            GoalDeathmatch: true,
            GoalProsperity: false,
            GoalCountdown: false,
            GoalArtifacts: false,
            GoalFlags: true,
            GoalTerritory: false,
            MinRank: 4,
            MaxRank: 12);

        var http = new HttpClient(app.Server.CreateHandler())
        {
            BaseAddress = app.ClientOptions.BaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        var cfg = new LobbyConfig
        {
            Url = app.ClientOptions.BaseAddress.AbsoluteUri,
            DisplayName = "P806 Settings Server",
            PublicAddress = "1.2.3.4",
        };
        using var hb = new LobbyHeartbeat(http, cfg, () => 3, "Wing War", "PCore001.igc", "0.1.0", 5333,
            clock: null, livePlayerNames: null, liveGameParams: () => game);
        try
        {
            await hb.PumpAsync();

            var servers = await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers");
            var mine = Assert.Single(servers!);
            Assert.NotNull(mine.Game);
            Assert.False(mine.Game!.AllowDevelopments);
            Assert.Equal(2, mine.Game.Lives);
            Assert.True(mine.Game.InProgress);
            Assert.True(mine.Game.GoalDeathmatch);
            Assert.True(mine.Game.GoalFlags);
            Assert.Equal(4, mine.Game.MinRank);
            Assert.Equal(12, mine.Game.MaxRank);
        }
        finally
        {
            hb.Dispose();
            http.Dispose();
        }
    }

    /// <summary>P866 / SIM-0537 — the rest of native <c>pfmLobbyMissionInfo</c>
    /// (<c>FedSrv/fsmission.cpp:3983-4096</c>) survives the HTTP directory
    /// end-to-end: player counts, team/cap numbers, slot availability, the armed
    /// countdown offset, squad game, eject pods, invulnerable stations and the
    /// playing squads. Before P866 only the P806 filter set was carried.</summary>
    [Fact]
    public async Task Heartbeat_publishes_the_remaining_native_mission_info_fields()
    {
        using var app = new LobbyHarness();
        using var client = app.CreateClient();

        var game = new LobbyGameParams(
            AllowDevelopments: true, Lives: 3, ScoresCount: true, ObjectModelCreated: true,
            InProgress: false, GoalConquest: true, GoalDeathmatch: false, GoalProsperity: false,
            GoalCountdown: false, GoalArtifacts: false, GoalFlags: false, GoalTerritory: false,
            MinRank: -1, MaxRank: 1000,
            AllowFriendlyFire: false, AllowAlliedViz: true,
            NumPlayers: 5, NumNoatPlayers: 2, Teams: 2, MaxPlayersPerGame: 20,
            MinPlayersPerTeam: 1, MaxPlayersPerTeam: 10, StartTimeOffsetMs: 12_500,
            CountdownStarted: true, SquadGame: true, EjectPods: true, InvulnerableStations: false,
            AnySlotsAvailable: true, GuaranteedSlotsAvailable: false,
            SquadIds: ["squad-alpha", "squad-bravo"]);

        var http = new HttpClient(app.Server.CreateHandler())
        {
            BaseAddress = app.ClientOptions.BaseAddress,
            Timeout = TimeSpan.FromSeconds(5),
        };
        var cfg = new LobbyConfig
        {
            Url = app.ClientOptions.BaseAddress.AbsoluteUri,
            DisplayName = "P866 Mission Info Server",
            PublicAddress = "1.2.3.4",
        };
        using var hb = new LobbyHeartbeat(http, cfg, () => 5, "Wing War", "PCore001.igc", "0.1.0", 5333,
            clock: null, livePlayerNames: null, liveGameParams: () => game);
        try
        {
            await hb.PumpAsync();

            var mine = Assert.Single((await client.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!);
            var g = mine.Game;
            Assert.NotNull(g);
            Assert.Equal(5, g!.NumPlayers);
            Assert.Equal(2, g.NumNoatPlayers);
            Assert.Equal((short)2, g.Teams);
            Assert.Equal(20, g.MaxPlayersPerGame);
            Assert.Equal((short)1, g.MinPlayersPerTeam);
            Assert.Equal((short)10, g.MaxPlayersPerTeam);
            Assert.Equal(12_500u, g.StartTimeOffsetMs);
            Assert.True(g.CountdownStarted);
            Assert.True(g.SquadGame);
            Assert.True(g.EjectPods);
            Assert.False(g.InvulnerableStations);
            Assert.True(g.AnySlotsAvailable);
            Assert.False(g.GuaranteedSlotsAvailable);
            Assert.Equal(["squad-alpha", "squad-bravo"], g.SquadIds!);
            Assert.True(g.AllowAlliedViz);
        }
        finally
        {
            hb.Dispose();
            http.Dispose();
        }
    }
}
