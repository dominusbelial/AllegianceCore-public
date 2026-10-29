using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P646 (GPT_SOL review finding H3) — ONE LOGON PER CONNECTION. Native asserts
/// an existing player never sends LOGONREQ (FedSrv.CPP:4832), but this port had
/// no such check: a seated peer re-sending a version-matching LOGONREQ got a
/// fresh ship id, a new _pendingSideSelect entry and, on seating, an overwritten
/// _peerShips entry that orphaned the first ship in its sector. A still-pending
/// peer's duplicate LOGONREQ must be idempotent (same ack, no new id).
/// </summary>
[Collection("LiveNetwork")]
public class LiveDuplicateLogonTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "DupLogon", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 847u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8490000000000000000ul, DrmHash = "dup",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short Seat(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, $"{name} did not seat");
        return bot.ShipAssignment!.Ship.ShipId;
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Concurrent_same_name_connections_receive_one_clear_refusal_and_one_seat()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        using var a = new FltBot { AutoSelectSideAfterLogon = false };
        using var b = new FltBot { AutoSelectSideAfterLogon = false };
        a.Connect(server.BoundPort, "mcp-probe");
        b.Connect(server.BoundPort, "MCP-PROBE");
        a.SendLogonRequest(Request("mcp-probe"));
        b.SendLogonRequest(Request("MCP-PROBE"));
        Assert.True(PumpUntil(server, () => a.Ack is not null && b.Ack is not null, 4000) < 4000);
        var accepted = a.Ack!.FValidated ? a : b;
        var refused = a.Ack.FValidated ? b : a;
        Assert.True(accepted.Ack!.FValidated);
        Assert.False(refused.Ack!.FValidated);
        Assert.False(refused.Ack.FRetry);
        Assert.Equal(0, refused.Ack.ShipID);
        Assert.Contains("character name already in use", refused.Ack.CharNameOrFailureReason);
        Assert.Equal(1, server.ConnectionScopedCacheCounts().PendingSideSelect);
        accepted.SendPositionReq(0);
        Assert.True(PumpUntil(server, () => accepted.ShipAssignment is not null, 4000) < 4000);
        Assert.Null(refused.ShipAssignment);
        Assert.Single(server.Ships);
        Assert.Equal(accepted.Ack.ShipID, server.Ships.Single().Ship.Id);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Seated_peer_duplicate_LOGONREQ_is_rejected_no_new_ship_no_new_id()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();
        short id = Seat(server, bot, "PDUP1");
        Assert.Equal(0, server.ConnectionScopedCacheCounts().PendingSideSelect);

        // Re-send a version-matching LOGONREQ on the same seated connection.
        bot.SendLogonRequest(Request("PDUP1"));
        PumpUntil(server, () => false, 40);

        // Still exactly one ship, same id, and NO new pending-side-select entry.
        Assert.Equal(0, server.ConnectionScopedCacheCounts().PendingSideSelect);
        Assert.Single(server.Ships);
        Assert.Equal(id, server.Ships.First().Ship.Id);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Pending_peer_duplicate_LOGONREQ_is_idempotent_same_ack_no_new_id()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();

        // Hold the peer at the post-connect side-select screen (no POSITIONREQ).
        // With P763's auto-seat off by default it stays pending, but this test
        // pumps only a few ticks anyway. The bot would normally auto-send
        // POSITIONREQ(NA) on ack, so disable that.
        bot.AutoSelectSideAfterLogon = false;
        bot.Connect(server.BoundPort, "PDUP2");
        bot.SendLogonRequest(Request("PDUP2"));
        int got = PumpUntil(server, () => bot.Ack is not null, 200);
        Assert.True(got < 200, "no logon ack");
        Assert.Equal(1, server.ConnectionScopedCacheCounts().PendingSideSelect);
        short firstShip = bot.Ack!.ShipID;

        // Duplicate LOGONREQ while pending: the SAME ack (same ship id), and the
        // pending entry is not replaced with a freshly-allocated id.
        bot.SendLogonRequest(Request("PDUP2"));
        PumpUntil(server, () => false, 40);
        Assert.Equal(1, server.ConnectionScopedCacheCounts().PendingSideSelect);
        Assert.Equal(firstShip, bot.Ack!.ShipID);
    }
}
