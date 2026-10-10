using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P84: <c>FM_CS_PLAYER_READY</c>(45) codec round-trip, plus the live relay
/// proof — the native client's Team Lobby "JOIN" control sends this and
/// waits for the server to relay it back before its own roster state (and
/// the screen built from it) updates at all (<c>appmsg.cpp:2297-2312</c>);
/// see <see cref="Wire.PlayerReady"/>'s own doc comment for the full trail.
/// </summary>
[Collection("LiveNetwork")]
public class PlayerReadyTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    [Fact]
    public void PlayerReady_round_trips_exactly()
    {
        var msg = new PlayerReady { FReady = true, ShipId = 8096 };
        var bytes = msg.Encode();
        Assert.Equal(8, bytes.Length);

        var d = PlayerReady.Decode(bytes);
        Assert.Equal(PlayerReady.Fmid, d.GetFmid());
        Assert.True(d.FReady);
        Assert.Equal((short)8096, d.ShipId);

        var msg2 = new PlayerReady { FReady = false, ShipId = -1 };
        var d2 = PlayerReady.Decode(msg2.Encode());
        Assert.False(d2.FReady);
        Assert.Equal((short)-1, d2.ShipId);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Ready Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 48u, DwCookie = 0x48u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 84100000000000000ul, DrmHash = "readytest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ The real regression: a real WinTrek client's Team Lobby JOIN
    /// click waits for the server to relay PLAYER_READY back before its own
    /// state updates -- before this fix the message was logged "unsupported"
    /// and dropped, so the click had no observable effect at all.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void PlayerReady_is_relayed_back_to_the_sender()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "ready_probe");
        bot.SendLogonRequest(Request("ready_probe"));
        PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        short shipId = bot.ShipAssignment!.Ship.ShipId;

        bot.SendPlayerReady(fReady: true, shipId: shipId);

        PumpUntil(server, () => bot.PlayerReadyEchoes.Count > 0, 4000);
        Assert.Single(bot.PlayerReadyEchoes);
        Assert.True(bot.PlayerReadyEchoes[0].FReady);
        Assert.Equal(shipId, bot.PlayerReadyEchoes[0].ShipId);
    }
}
