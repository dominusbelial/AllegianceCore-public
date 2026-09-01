using Allegiance.Core.Wire;
// 'Server' and 'Bot' are ALSO namespace names (Allegiance.Server, Allegiance.Bot)
// reachable from this namespace, so the simple names are ambiguous with the
// namespaces. Aliased to distinct, unambiguous identifiers instead.
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// Proves the P40 deliverable at the wire level: the server emits an
/// <c>FM_S_SHIP_STATUS(90)</c> for a peer's OWN ship as part of the join burst
/// (right after <c>MISSION_STAGE(37)</c>), and a real loopback <see cref="FltBot"/>
/// decodes it to exactly the ship it was assigned (same ShipId + sector as its
/// <c>SET_CLUSTER(7)</c> assignment). This is the managed-server half of making
/// <c>ScoutExploreMap</c>'s primary sweep reachable — the native client's
/// handler is gated on <c>FindPlayer(shipID)</c> which this server seeds only
/// for the peer's own ship, so the own-ship status is the correctly-scoped
/// unit to prove.
/// </summary>
[Collection("LiveNetwork")]
public class ShipStatusServerTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Status Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xCAFEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 72000000000000000ul, DrmHash = "statustest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>After the peer's ship is assigned and the join burst has flushed,
    /// it must have received exactly one SHIP_STATUS for its OWN ship, carrying
    /// that ship's id and the sector it was placed in.</summary>
    [Fact]
    public void PeerReceivesItsOwnShipStatus_InJoinBurst()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();

        // Connect + logon like the console app; the server bursts on logon.
        bot.Connect(server.BoundPort, "status_probe");
        bot.SendLogonRequest(Request("status_probe"));

        // Wait until the ship is assigned AND the join burst (incl. SHIP_STATUS)
        // has been decoded by the client.
        int ticks = PumpUntil(server,
            () => bot.ShipAssignment is not null && bot.ShipStatuses.Count >= 1,
            4000);

        Assert.NotNull(bot.ShipAssignment);
        Assert.NotEmpty(bot.ShipStatuses);
        var status = bot.ShipStatuses[0];
        Assert.Equal(bot.ShipAssignment!.Ship.ShipId, status.ShipId);
        Assert.Equal(bot.ShipAssignment!.SectorId, status.SectorId);

        // Sanity: the decoded status is a healthy flying scout-less frame per the
        // established simplification (State=Flying, detected, not unknown).
        Assert.Equal(ShipStatusMessage.StateFlying, status.State);
        Assert.False(status.Unknown);
        Assert.True(status.Detected);
        Assert.Equal(-1, status.StationId);   // NA — no docking in this server
        Assert.Equal(-1, status.ParentId);

        // The server also recorded the sector so a future sector-crossing would
        // resend (not asserted here — no warp traversal exists yet).
        Assert.True(ticks > 0, "pump did not run");
    }

    /// <summary>The periodic refresh keeps feeding status frames after the join
    /// burst, so a client-side strategy that attaches late (after the burst) is
    /// guaranteed to receive one — the reason a single join-burst send is not
    /// enough (P40 live finding: ScoutExploreMap wires its MessageReceiver on
    /// attach, StrategyBase.cs:90, racing the burst).</summary>
    [Fact]
    public void ServerRefreshesOwnShipStatusPeriodically()
    {
        using var server = new Srv();
        server.Boot(Config());
        using var bot = new FltBot();

        bot.Connect(server.BoundPort, "refresh_probe");
        bot.SendLogonRequest(Request("refresh_probe"));
        int ticks = PumpUntil(server, () => bot.ShipAssignment is not null && bot.ShipStatuses.Count >= 1, 4000);
        Assert.True(ticks > 0);
        int firstCount = bot.ShipStatuses.Count;

        // Pump well past one refresh interval (40 ticks); more statuses arrive.
        PumpUntil(server, () => bot.ShipStatuses.Count >= firstCount + 2, 200);
        Assert.True(bot.ShipStatuses.Count > firstCount,
            $"expected refresh to add statuses; firstCount={firstCount}, now={bot.ShipStatuses.Count}");
    }
}
