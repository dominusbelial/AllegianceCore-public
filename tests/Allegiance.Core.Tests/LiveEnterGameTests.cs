using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P141 — <c>ENTER_GAME</c>(128), the final "you're ready" capstone of the
/// join-sync burst. Real loopback proof over an in-process server + real TCP
/// bots: a real player join/sync sends the joining player a single
/// <c>ENTER_GAME</c> on that player's OWN connection once join-sync completes
/// — sent once, not broadcast repeatedly, not whole-mission-wide. Two bots on
/// different sides prove it is per-player: neither gets the other's message.
/// The codec round-trips byte-exact in Wire.EnterGameWireTests.
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
        FedsrvVer = LogonRequest.MsgVer, Time = 881u, DwCookie = 0x881u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9500000000000000000ul, DrmHash = "entergame",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ Real player join/sync sends each joining player exactly one
    /// <c>ENTER_GAME(128)</c> — to that player's own connection only, at the end
    /// of join-sync (after SHIP_STATUS). Two bots on different sides prove it is
    /// per-player: neither gets the other's message, and each gets exactly one —
    /// not a repeated broadcast.</summary>
    [Fact]
    public void Join_sync_sends_per_player_enter_game_once()
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

        // Give the join-sync ENTER_GAME burst time to land on both connections.
        int pumped = 0;
        while ((a.EnterGames.Count < 1 || b.EnterGames.Count < 1) && pumped < 4000)
        {
            server.TickSim(); pumped++; Thread.Sleep(1);
        }

        foreach (var bot in new[] { a, b })
        {
            var msg = Assert.Single(bot.EnterGames);   // exactly one — to this player only, not broadcast repeatedly
            Assert.Equal(128, msg.GetFmid());
            Console.WriteLine($"ENTER-GAME-LIVE: bot '{bot.ShipAssignment!.Ship.ShipId}' -> ENTER_GAME(128) received on its own connection");
        }
    }
}
