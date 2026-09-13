using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Live integration proof for the newly-ported outbound half of
/// <c>FM_CS_CHATBUOY(108)</c> — <see cref="ConnectionSession.SendChatBuoy"/>,
/// the client mirror of <c>Allegiance.Bot.Bot.SendChatBuoy</c>. The read-only
/// server's own <c>LiveChatBuoyWireTests</c> already proves the server-side
/// relay end-to-end with a real <c>Bot</c>; this task's job is proving the
/// CLIENT can send it. We do exactly that: a real <see cref="ConnectionSession"/>
/// sends a real CHATBUOY over loopback TCP, the real server relays it (the
/// shared <c>SelectChatTargets</c>), and the RECEIVER's own
/// <see cref="ClientWorld.Events.ChatBuoys"/> gains the buoy chat with the
/// real sender ship id and the full embedded <c>DataBuoyIGC db</c> payload
/// intact — not merely that it encodes without throwing.
/// </summary>
[Collection("LiveNetwork")]
public class ChatBuoyLiveTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    private static Cfg Config(string name = "ChatBuoy Relay", int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 932u, DwCookie = 0xABC0u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 86600000000000000ul, DrmHash = "buoylive",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A realistic buoy export: a mine/miner-dispatch target marker a
    /// commander would address, with non-trivial floats so the wire round-trip
    /// is unambiguous. Mirrors <c>LiveChatBuoyWireTests.TestBuoy</c>.</summary>
    private static readonly DataBuoyIGC TestBuoy = new(
        [100.0f, -200.5f, 300.75f], ClusterId: 13, Type: 1, BuoyId: 9771, Visible: true);

    /// <summary>⭐ The client send: an Everyone-targeted CHATBUOY is relayed by
    /// the real server and lands in the RECEIVER's <see cref="ClientWorld.EventLog.ChatBuoys"/> —
    /// with the real sender ship id, the real payload bytes, and the full buoy
    /// block (numeric fields) intact. Everyone-targeting also echoes to the
    /// sender itself, matching <see cref="SendChat"/>'s behaviour.</summary>
    [Fact]
    public void SendChatBuoy_RelayedToPeers_LandsInReceiversChatBuoys()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var sender = new ConnectionSession();
        using var receiver = new ConnectionSession();
        sender.Connect(server.BoundPort, "buoy_sender");
        receiver.Connect(server.BoundPort, "buoy_receiver");
        sender.SendLogonRequest(Request("buoy_sender"));
        receiver.SendLogonRequest(Request("buoy_receiver"));

        PumpUntil(server, () => sender.World.MyShipId is not null && receiver.World.MyShipId is not null, 4000);
        Assert.NotNull(sender.World.MyShipId);
        Assert.NotNull(receiver.World.MyShipId);
        short senderId = sender.World.MyShipId!.Value;

        sender.SendChatBuoy("MINER HOLD GRID 5", ChatTarget.Everyone, 0, TestBuoy);

        int pump = PumpUntil(server,
            () => receiver.World.Events.ChatBuoys.Any(c => c.Message == "MINER HOLD GRID 5"),
            4000);
        Assert.True(pump < 4000, "buoy chat never reached the receiver's EventLog.ChatBuoys");

        var got = receiver.World.Events.ChatBuoys.Single(c => c.Message == "MINER HOLD GRID 5");
        Assert.Equal("MINER HOLD GRID 5", got.Message);
        Assert.Equal(senderId, got.Cd.SidSender);   // relayed with the real sender ship id

        // The full embedded buoy block round-tripped byte-equivalent (the server
        // relays CHATBUOY verbatim). Position is a float[] in a record struct
        // (reference-compared by Equal), so assert the numeric fields individually
        // like the sibling LiveChatBuoyWireTests does.
        Assert.Equal(TestBuoy.Position, got.Db.Position);
        Assert.Equal(TestBuoy.ClusterId, got.Db.ClusterId);
        Assert.Equal(TestBuoy.Type, got.Db.Type);
        Assert.Equal(TestBuoy.BuoyId, got.Db.BuoyId);
        Assert.Equal(TestBuoy.Visible, got.Db.Visible);

        // Everyone-targeting is also echoed to the sender itself, like SendChat.
        int echo = PumpUntil(server,
            () => sender.World.Events.ChatBuoys.Any(c => c.Message == "MINER HOLD GRID 5"), 4000);
        Assert.True(echo < 4000, "sender's own buoy echo never landed in its EventLog");
    }

    /// <summary>An Individual-addressed CHATBUOY reaches only the addressed
    /// peer's <see cref="ClientWorld.EventLog.ChatBuoys"/>, preserving the
    /// routing semantics <c>SelectChatTargets</c> already proven server-side —
    /// the client now contributes the send half of that end-to-end.</summary>
    [Fact]
    public void SendChatBuoy_IndividualTarget_AddressesOnlyReceiver()
    {
        using var server = new Srv();
        // Round-robin two peers across two teams would sometimes seat them on
        // OPPOSITE sides, where SelectChatTargets deliberately drops an
        // Individual. Seed the same friendly-station spawner used by
        // PlayerReadyLiveTests so both peers reliably share a side — the
        // cross-side-drop routing is already proven by the read-only server's
        // LiveChatBuoyWireTests; here we exercise the ADDRESSED same-side case.
        var field = typeof(Srv).GetField("ShipSpawner", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ShipSpawner field not found on Srv");
        field.SetValue(server, (StaticCore core, Mission mission, short shipId) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var st = mission.Sectors.SelectMany(s => s.Stations).First(s => s.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(st));
            var ship = new Ship(shipId, hull, new Vector3(50f, 0f, 0f), Vector3.Zero, Orientation.Identity)
            {
                SideId = st.SideId,
            };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        });
        server.Boot(Config("ChatBuoy Individual"));

        using var sender = new ConnectionSession();
        using var receiver = new ConnectionSession();
        sender.Connect(server.BoundPort, "ind_sender");
        receiver.Connect(server.BoundPort, "ind_receiver");
        sender.SendLogonRequest(Request("ind_sender"));
        receiver.SendLogonRequest(Request("ind_receiver"));

        PumpUntil(server, () => sender.World.MyShipId is not null && receiver.World.MyShipId is not null, 4000);
        short receiverId = receiver.World.MyShipId!.Value;

        sender.SendChatBuoy("GO MINE RHO", ChatTarget.Individual, receiverId, TestBuoy);

        int pump = PumpUntil(server,
            () => receiver.World.Events.ChatBuoys.Any(c => c.Message == "GO MINE RHO"),
            4000);
        Assert.True(pump < 4000, "individual buoy never reached the addressed receiver");
        var got = receiver.World.Events.ChatBuoys.Single(c => c.Message == "GO MINE RHO");
        Assert.Equal(receiverId, got.OidRecipient);

        // The server does NOT echo an Individual relay back to the sender — it
        // was already delivered to the addressed peer only.
        Assert.DoesNotContain(sender.World.Events.ChatBuoys, c => c.Message == "GO MINE RHO");
    }
}
