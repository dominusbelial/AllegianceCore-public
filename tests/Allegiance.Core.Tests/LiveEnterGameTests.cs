using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P141 — <c>ENTER_GAME</c>(128), the final "you're ready" capstone of the
/// join-sync burst. Real loopback proof over an in-process server + real TCP
/// bots: a real player join/sync defers ENTER_GAME while the mission is still
/// NotStarted (P180 — the join-sync send is suppressed until the game starts),
/// and the ready-up auto-start broadcasts a single ENTER_GAME to every seated
/// peer once both sides are ready. P180 rewrote this test from the old
/// "ENTER_GAME at join-sync unconditionally" behaviour to the new gated one:
/// each of two bots readies its side, the mission auto-starts, and each bot
/// receives exactly one ENTER_GAME — not before either readied.
/// </summary>
[Collection("LiveNetwork")]
public class LiveEnterGameTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "EnterGame Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 884u, DwCookie = 0x884u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9530000000000000000ul, DrmHash = "entergame",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ Each player marks ready; only then does the mission
    /// auto-start and each receives exactly one ENTER_GAME(128). Before both
    /// have readied, neither has one (join-sync defers it while NotStarted).</summary>
    [Fact]
    public void Join_ready_up_auto_start_sends_one_enter_game_to_each_bot()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var a = new FltBot();
        using var b = new FltBot();
        a.Connect(server.BoundPort, "AA"); a.SendLogonRequest(Request("AA"));
        b.Connect(server.BoundPort, "BB"); b.SendLogonRequest(Request("BB"));
        int seat = PumpUntil(server,
            () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "both bots must seat");

        // While the mission is NotStarted, no ENTER_GAME yet (join-sync deferred).
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.Empty(a.EnterGames);
        Assert.Empty(b.EnterGames);

        // One side ready alone is not enough; both must ready before auto-start.
        a.SendPlayerReady(true, a.ShipAssignment!.Ship.ShipId);
        int ra = PumpUntil(server, () => a.TeamReadys.Count >= 1, 800);
        Assert.True(ra < 800, "no TEAM_READY after A readied");
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);
        Assert.Empty(a.EnterGames);
        Assert.Empty(b.EnterGames);

        b.SendPlayerReady(true, b.ShipAssignment!.Ship.ShipId);
        int both = PumpUntil(server,
            () => server.Missions[0].Stage == MissionStage.Started
                  && a.EnterGames.Count >= 1 && b.EnterGames.Count >= 1, 800);
        Assert.True(both < 800, "auto-start did not deliver ENTER_GAME to both bots");

        foreach (var bot in new[] { a, b })
        {
            var msg = Assert.Single(bot.EnterGames);   // exactly one, via the transition broadcast
            Assert.Equal(128, msg.GetFmid());
            Console.WriteLine($"ENTER-GAME-LIVE: bot '{bot.ShipAssignment!.Ship.ShipId}' -> ENTER_GAME(128) received after ready-up auto-start");
        }
    }
}
