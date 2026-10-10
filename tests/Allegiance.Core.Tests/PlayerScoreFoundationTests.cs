using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using PlayerScore = global::Allegiance.Server.PlayerScore;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P193 — the in-memory, <b>connection-lifetime-only</b> per-player
/// <c>PlayerScore</c> foundation (the type <see cref="PlayerScore"/> attached
/// to each <c>LiveShip</c> in <c>Server._peerShips</c>). Three honest proofs
/// that it is exactly the tiny non-persistent record the roadmap asks for and
/// nothing more:
///  (1) every newly-connected player's <c>PlayerScore</c> starts at the
///      neutral defaults <c>Rank=0, Score=0</c>;
///  (2) a <c>PlayerScore</c> is <b>discarded</b> (not preserved) when a player
///      disconnects and reconnects — the real proof this is genuinely
///      non-persistent, not an accidental persistence leak;
///  (3) two players in the same mission hold <b>independent</b>
///      <c>PlayerScore</c> instances (mutating one never touches the other).
/// No wire message, no broadcast, no dispatch — the structure is purely
/// in-memory; P194 (<c>GAME_OVER_PLAYERS</c>, 63) and P195
/// (<c>RANDOMIZE_TEAMS</c>, 21) wire it to real fedmsgs.
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public class PlayerScoreFoundationTests
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
            new Gm { Name = "PlayerScore Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 883u, DwCookie = 0x883u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8860000000000000000ul, DrmHash = "pscore",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>The <c>PlayerScore</c> backing a seated bot, located by its
    /// assigned ship id (robust to whatever spawn side/position the default
    /// spawner chose). Mutating the returned instance mutates the object the
    /// server's <c>LiveShip</c> holds (a reference), so mutations are
    /// observable through <c>server.Ships</c>.</summary>
    private static PlayerScore PlayerScoreOf(Srv server, FltBot bot) =>
        server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).PlayerScore;

    /// <summary>⭐ A newly-connected player's <c>PlayerScore</c> starts at the
    /// neutral defaults <c>Rank=0, Score=0</c> — there is no rank data anywhere
    /// to seed it with (no accounts, no historical games), so every connection
    /// begins from zero.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Newly_connected_player_score_starts_at_neutral_defaults()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using var a = new FltBot();
        using var b = new FltBot();
        a.Connect(server.BoundPort, "PA"); a.SendLogonRequest(Request("PA"));
        b.Connect(server.BoundPort, "PB"); b.SendLogonRequest(Request("PB"));
        int seat = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "both bots must seat");

        foreach (var bot in new[] { a, b })
        {
            var ps = PlayerScoreOf(server, bot);
            Assert.Equal((short)0, ps.Rank);
            Assert.Equal(0f, ps.Score);
        }

        Console.WriteLine("PSCORE-DEFAULT-LIVE: both seated players start Rank=0, Score=0");
    }

    /// <summary>⭐ The <b>non-persistence proof</b>: mutate a seated player's
    /// <c>PlayerScore</c> to non-zero, disconnect, then reconnect — the fresh
    /// connection gets a brand-new <c>PlayerScore</c> back at the neutral
    /// defaults. The disconnect/reconnect cycle discards (rather than preserves)
    /// the score, so this is genuinely connection-lifetime-only state, not an
    /// accidental persistence leak. (There is no account identity to carry a
    /// value across connections — reconnect simply allocates a new one.)</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Player_score_is_discarded_across_a_disconnect_and_reconnect()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using (var first = new FltBot())
        {
            first.Connect(server.BoundPort, "RECON"); first.SendLogonRequest(Request("RECON"));
            int got = PumpUntil(server, () => first.ShipAssignment is not null, 4000);
            Assert.True(got < 4000, "first connection must seat");
            short idA = first.ShipAssignment!.Ship.ShipId;

            var psA = PlayerScoreOf(server, first);
            psA.Rank = 42;
            psA.Score = 987.5f;

            first.Dispose();                       // abrupt drop → ReleasePeer removes the LiveShip
            int released = PumpUntil(server, () => !server.Ships.Any(s => s.Ship.Id == idA), 800);
            Assert.True(released < 800, "first peer must be released");
        }

        // Reconnect (same character name — but there is no identity layer, so
        // this is a brand-new connection and a brand-new PlayerScore).
        using var second = new FltBot();
        second.Connect(server.BoundPort, "RECON"); second.SendLogonRequest(Request("RECON"));
        int reseat = PumpUntil(server, () => second.ShipAssignment is not null, 4000);
        Assert.True(reseat < 4000, "reconnect must seat");

        var psB = PlayerScoreOf(server, second);
        Assert.Equal((short)0, psB.Rank);
        Assert.Equal(0f, psB.Score);

        Console.WriteLine("PSCORE-RECONNECT-LIVE: Rank 42/Score 987.5 discarded — reconnect sees Rank=0, Score=0");
    }

    /// <summary>⭐ Independence: two players in the same mission hold distinct
    /// <c>PlayerScore</c> instances. Mutating one player's rank/score leaves
    /// the other untouched — each connection owns its own object, there is no
    /// shared or server-global score record.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Two_players_have_independent_player_score_instances()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using var a = new FltBot();
        using var b = new FltBot();
        a.Connect(server.BoundPort, "PA"); a.SendLogonRequest(Request("PA"));
        b.Connect(server.BoundPort, "PB"); b.SendLogonRequest(Request("PB"));
        int seat = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "both bots must seat");

        var psA = PlayerScoreOf(server, a);
        var psB = PlayerScoreOf(server, b);

        Assert.NotSame(psA, psB);                  // distinct instances

        psA.Rank = 7;
        psA.Score = 555f;

        // The untouched player is unaffected.
        Assert.Equal((short)0, psB.Rank);
        Assert.Equal(0f, psB.Score);

        Console.WriteLine("PSCORE-INDEP-LIVE: mutating A (Rank=7, Score=555) leaves B at Rank=0, Score=0");
    }
}
