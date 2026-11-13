using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Identity;
using Allegiance.Core.Wire;
using Allegiance.Core.Sim;
using Allegiance.Lobby;
using Allegiance.Server;
using Allegiance.TestInfra;
using Microsoft.Extensions.Time.Testing;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P1004LobbyListingTests
{
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task Empty_running_password_game_publishes_200_slots_through_real_directory_and_refreshes()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Games = [new GameConfig { Name = "Guarded", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxPlayersPerTeam = 100, Password = "fixture" }],
        });
        var mission = Assert.Single(server.Missions);
        using var app = new LobbyHarness();
        using var http = app.CreateClient();
        using var heartbeat = new LobbyHeartbeat(http, new LobbyConfig { DisplayName = "P1004", PublicAddress = "127.0.0.1" },
            () => server.LivePlayerCount, "Guarded", "PCore001.igc", "test", server.BoundPort,
            liveGameParams: () => server.LiveGameParams) { LiveMissions = () => server.LiveMissions };
        string? listingId = null;
        foreach (var stage in new[] { MissionStage.NotStarted, MissionStage.Starting, MissionStage.Started })
        {
            mission.Stage = stage;
            PublishSnapshot(server);
            await heartbeat.PumpAsync();
            var listing = Assert.Single((await http.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!);
            listingId ??= listing.Id;
            Assert.Equal(listingId, listing.Id);
            Assert.Equal(200, listing.MaxPlayers);
            Assert.Equal(0, listing.PlayerCount);
            Assert.True(listing.PasswordRequired);
            var game = Assert.Single(listing.Missions!).Game!;
            Assert.Equal(200, game.MaxPlayersPerGame);
            Assert.Equal((short)100, game.MaxPlayersPerTeam);
            Assert.True(game.AnySlotsAvailable);
            Assert.True(game.GuaranteedSlotsAvailable);
            Assert.True(listing.Game!.AnySlotsAvailable);
        }
        // Native scan gates: a locked/finished game is unavailable even at 0/200.
        mission.LockLobby = true;
        Assert.False(server.TestLobbyParams().AnySlotsAvailable);
        mission.LockLobby = false;
        foreach (var side in mission.Sides) side.Active = false;
        Assert.False(server.TestLobbyParams().AnySlotsAvailable);
        foreach (var side in mission.Sides) side.Active = true;
        // Occupancy is not enough: each running side also needs a restart base.
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.StationRuntimes) typeof(Station).GetProperty(nameof(Station.SideId))!.SetValue(station, (short)-1);
        Assert.False(server.TestLobbyParams().AnySlotsAvailable);
        mission.Stage = MissionStage.NotStarted;
        Assert.True(server.TestLobbyParams().AnySlotsAvailable); // no-base gate applies only once starting
        mission.Stage = MissionStage.Over;
        Assert.False(server.TestLobbyParams().GuaranteedSlotsAvailable);
        PublishSnapshot(server);
        await heartbeat.PumpAsync();
        Assert.False(Assert.Single((await http.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!).Game!.AnySlotsAvailable);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Full_teams_still_allow_entry_to_the_team_lobby_and_refuse_a_full_side_on_the_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(new ServerConfig
        {
            ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
            Games = [new GameConfig { Name = "Full", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], MaxPlayersPerTeam = 1 }],
        });
        using var first = Enter(server, "First");
        first.SendPositionReq(0);
        Pump(server, () => first.World.MyShipId is not null);
        using var second = Enter(server, "Second");
        second.SendPositionReq(1);
        Pump(server, () => second.World.MyShipId is not null);
        Assert.False(server.TestLobbyParams().AnySlotsAvailable);
        Assert.False(server.TestLobbyParams().GuaranteedSlotsAvailable);
        // Native's displayed cap is 2, but its total admission limit remains
        // 0x7fff: FedSrv/FedSrv.CPP:634-646. A red row is still selectable.
        using var third = Enter(server, "Waiting");
        Assert.NotNull(third.World.SideRoster);
        third.SendPositionReq(0);
        Pump(server, () => third.LastPositionRefusal is not null);
        Assert.Equal(DelPositionReqReason.TeamFull, third.LastPositionRefusal!.Reason);
        Assert.Null(third.World.MyShipId);
    }

    private static ConnectionSession Enter(Srv server, string name)
    {
        var session = new ConnectionSession();
        try
        {
            session.Connect(server.BoundPort, name);
            session.SendLogonRequest(new LogonRequest { CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
                DwCookie = server.Missions[0].Cookie, CrcFileList = -1 });
            Pump(server, () => session.World.SideRoster is not null);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    private static void Pump(Srv server, Func<bool> done)
    {
        for (int i = 0; i < 4000; i++)
        {
            server.TickSim();
            if (done()) return;
            Thread.Sleep(1);
        }
        Assert.True(done(), "local join did not answer");
    }

    [Fact]
    public async Task Heartbeat_capacity_aggregates_missions_and_supports_legacy_single_game_publisher()
    {
        using var app = new LobbyHarness();
        using var http = app.CreateClient();
        var first = new LobbyGameParams(true, 0x7fff, true, true, false, true, false, false, false, false, false, false, -1, 1000,
            MaxPlayersPerGame: 200, AnySlotsAvailable: true, GuaranteedSlotsAvailable: true);
        var second = first with { MaxPlayersPerGame = 12 };
        using var heartbeat = new LobbyHeartbeat(http, new LobbyConfig { DisplayName = "P1004", PublicAddress = "127.0.0.1" },
            () => 0, "First", "Core", "test", 6073, liveGameParams: () => first)
        {
            LiveMissions = () => [new LobbyMissionListing(1, "First", 0, first), new LobbyMissionListing(2, "Second", 0, second)],
        };
        await heartbeat.PumpAsync();
        Assert.Equal(212, Assert.Single((await http.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!).MaxPlayers);
        heartbeat.LiveMissions = null;
        await heartbeat.PumpAsync();
        Assert.Equal(200, Assert.Single((await http.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!).MaxPlayers);
        heartbeat.LiveMissions = () => [];
        await heartbeat.PumpAsync();
        Assert.Equal(0, Assert.Single((await http.GetFromJsonAsync<List<ServerListingDto>>("/api/servers"))!).MaxPlayers);
    }

    private static void PublishSnapshot(Srv server) => typeof(Srv)
        .GetMethod("RefreshLobbyMissionSnapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(server, null);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unapproved_or_unavailable_enrollment_backs_off_to_one_attempt_an_hour(bool unavailable)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var handler = new EnrollmentHandler { Unavailable = unavailable };
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        using var heartbeat = new LobbyHeartbeat(http, new LobbyConfig { EnrollmentFile = ".scratch/p1004/unused-enrollment.json" },
            () => 0, "Game", "Core", "test", 6073, clock);
        await heartbeat.PumpAsync();
        Assert.Equal(1, handler.Starts);
        foreach (int minutes in new[] { 5, 10, 20, 40, 60, 60 })
        {
            if (!unavailable)
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                await heartbeat.PumpAsync(); // expired grant schedules a delayed fresh request
            }
            int attempts = handler.Starts;
            clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            await heartbeat.PumpAsync();
            Assert.Equal(attempts, handler.Starts);
            clock.Advance(TimeSpan.FromSeconds(1));
            await heartbeat.PumpAsync();
            Assert.Equal(attempts + 1, handler.Starts);
        }
        Assert.True(handler.Registers >= 13, "unverified registration continues throughout enrollment backoff");
    }

    private sealed class EnrollmentHandler : HttpMessageHandler
    {
        public bool Unavailable { get; init; }
        public int Starts { get; private set; }
        public int Registers { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response;
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/api/identity/device":
                    Starts++;
                    response = Unavailable ? new(HttpStatusCode.ServiceUnavailable) : new(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(new DeviceAuthorization("device", "code", "http://localhost/identity.html", 300, 5)),
                    };
                    break;
                case "/api/identity/device/poll":
                    response = new(HttpStatusCode.OK) { Content = JsonContent.Create(new DeviceResult("expired")) };
                    break;
                default:
                    Registers++;
                    response = new(HttpStatusCode.OK) { Content = JsonContent.Create(new LobbyRegisterResponse("id", "token")) };
                    break;
            }
            return Task.FromResult(response);
        }
    }
}
