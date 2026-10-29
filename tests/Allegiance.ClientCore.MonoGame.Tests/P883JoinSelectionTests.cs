using System.Net;
using System.Text;
using Allegiance.ClientCore.MonoGame.Discovery;
using Allegiance.ClientCore.MonoGame.Games;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P883JoinSelectionTests
{
    [Fact]
    public void Directory_games_at_one_address_keep_distinct_cookies_and_rows()
    {
        var server = new ServerListing("host", "Dedicated", "127.0.0.1", 6073, 4, 20, "First", "PCore001", "1",
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            Missions: [new(11, "First", 3, null), new(22, "Second", 1, null)]);
        var games = ServerListing.ExpandMissions([server]).ToArray();
        Assert.Equal(new uint[] { 11, 22 }, games.Select(g => g.MissionCookie));
        Assert.Equal(new[] { "First", "Second" }, games.Select(g => g.Name));
        Assert.Equal(new[] { 3, 1 }, games.Select(g => g.PlayerCount));
        var discovered = games.Select(g => new DiscoveredServer(g.Name, g.Address, g.Port, DiscoverySource.Lobby,
            0, g.MissionName, g.PlayerCount, g.MaxPlayers, g.Version, "", MissionCookie: g.MissionCookie)).ToArray();
        Assert.NotEqual(discovered[0].HostPort, discovered[1].HostPort);
        Assert.Equal(2, IntroFindServerList.FilterAndSort(discovered).Count);
        Assert.Equal(uint.MaxValue, ServerListing.ExpandMissions([server with { Missions = null }]).Single().MissionCookie);
    }

    [Theory]
    [InlineData(HudStyle.Classic)] [InlineData(HudStyle.Modern)]
    public void Mission_refusal_is_visible_before_any_ship_assignment(HudStyle style)
    {
        _ = new HudSettings { Style = style }; // both renderers consume the same shared notices
        using var world = new ClientWorld();
        world.Apply(new DelPositionReq { ShipId = 0, SideId = -1, Reason = DelPositionReqReason.NoMission });
        Assert.Contains("The mission you were trying to join has ended.", LobbyStatusLayout.Build(world));
        world.Apply(new DelPositionReq { ShipId = 0, SideId = -1, Reason = DelPositionReqReason.ServerPaused });
        Assert.Contains(LobbyStatusLayout.RequestFailureText(DelPositionReqReason.ServerPaused), LobbyStatusLayout.Build(world));
    }

    private sealed class ReadyLobby : HttpMessageHandler
    {
        public int Polls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Polls++;
            string json = Polls == 1 ? """[{"id":"created","gameName":"New","state":"Claimed"}]"""
                : """[{"id":"other","state":"Ready","cookie":1},{"id":"created","state":"Ready","cookie":883}]""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Creator_waits_for_its_own_ready_cookie_instead_of_joining_on_queue_ack()
    {
        var handler = new ReadyLobby(); using var http = new HttpClient(handler);
        var client = new LobbyGameClient(http, "http://127.0.0.1:5000");
        Assert.Equal(883u, await client.WaitForMissionAsync("created"));
        Assert.Equal(2, handler.Polls);
    }
}
