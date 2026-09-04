using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P67 live proof: a real <see cref="FltBot"/> sends a real
/// <c>FM_CS_CHATBUOY(108)</c> over loopback TCP and the server relays it via
/// the <b>same</b> <c>SelectChatTargets</c> the CHATMESSAGE relay uses (the
/// method's parameter is now the shared <see cref="IChatRoutingTarget"/>), so
/// routing here is expected to match ChatMessage's tested behaviour exactly.
/// Three bots seat round-robin onto two sides (Server.cs:523-529) → one side
/// has two members (the same-side pair) and one the solo opponent, the same
/// arrangement LiveChatWireTests uses. The "exact original bytes" assertion is
/// a re-encode comparison: the recipient's decoded CHATBUOY re-encodes to the
/// sender's original bytes because the server forwards <c>m</c> verbatim
/// (<c>Server.cs</c> CHATBUOY branch, mirroring ChatMessage's relay and PING's
/// echo).
/// </summary>
[Collection("LiveNetwork")]
public class LiveChatBuoyWireTests
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
            new Gm { Name = "ChatBuoy Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 918u, DwCookie = 0xDEADu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 74100000000000000ul, DrmHash = "livechatbuoy",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>shipId → SideId from the server's authoritative live-ship table.</summary>
    private static Dictionary<short, short?> SideById(Srv server) =>
        server.Ships.ToDictionary(kv => kv.Ship.Id, kv => kv.Ship.SideId);

    private static readonly DataBuoyIGC TestBuoy = new(
        [100.0f, -200.5f, 300.75f], ClusterId: 13, Type: 1, BuoyId: 8150, Visible: true);

    [Fact]
    public void Real_bots_exchange_chatbuoy_Individual_Team_Everyone_over_the_wire()
    {
        using var server = new Srv();
        server.Boot(Config());
        ushort port = server.BoundPort;

        var bots = new FltBot[3];
        for (int i = 0; i < 3; i++) { bots[i] = new FltBot(); bots[i].Connect(port, $"BOT{i}"); }
        for (int i = 0; i < 3; i++) bots[i].SendLogonRequest(Request($"PLAYER{i}"));

        int ticks = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        Assert.True(ticks < 4000, "not all three bots were assigned a ship");

        var sides = SideById(server);
        var groups = bots.GroupBy(b => sides[b.ShipAssignment!.Ship.ShipId]).ToList();
        Assert.Equal(2, groups.Count);
        var majority = groups.First(g => g.Count() == 2).ToArray();   // the two teammates
        var minority = groups.First(g => g.Count() == 1).ToArray();   // the solo opponent
        var sender = majority[0];
        var teammate = majority[1];
        short senderId = sender.ShipAssignment!.Ship.ShipId;
        short teammateId = teammate.ShipAssignment!.Ship.ShipId;
        short opponentId = minority[0].ShipAssignment!.Ship.ShipId;
        short senderSide = sides[senderId]!.Value;

        bool Seen(FltBot b, string m) => b.ChatBuoys.Any(c => c.Message == m);

        // ── Phase 1: Individual addressed to the same-side teammate is relayed
        // (byte-exact) to ONLY that ship ──
        var sent = new ChatBuoy
        {
            Message = "BM HOLD STATION",
            Cd = new ChatData { SidSender = senderId, ChatTarget = (int)ChatTarget.Individual, OidRecipient = teammateId,
                                VoiceOver = 0, CommandID = 0, BObjectModel = false },   // matches SendChatBuoy's defaults below
            Db = TestBuoy,
        };
        foreach (var b in bots) b.ResetChatBuoyReceived();
        sender.SendChatBuoy("BM HOLD STATION", ChatTarget.Individual, teammateId, TestBuoy);
        PumpUntil(server, () => Seen(teammate, "BM HOLD STATION"), 2000);

        Assert.Contains(teammate.ChatBuoys, c => c.Message == "BM HOLD STATION");
        // Exact original bytes: the recipient's decoded message re-encodes to
        // the sender's own original encoding (verbatim relay preserves bytes).
        var got = teammate.ChatBuoys.Single(c => c.Message == "BM HOLD STATION");
        Assert.Equal(sent.Encode(), got.Encode());
        Assert.Equal(senderId, got.Cd.SidSender);
        Assert.Equal(teammateId, got.OidRecipient);
        // Position is a float[] (compared by reference in a record struct), so
        // assert the numeric fields individually rather than Equal(TestBuoy, got.Db).
        Assert.Equal(TestBuoy.Position, got.Db.Position);
        Assert.Equal(TestBuoy.ClusterId, got.Db.ClusterId);
        Assert.Equal(TestBuoy.Type, got.Db.Type);
        Assert.Equal(TestBuoy.BuoyId, got.Db.BuoyId);
        Assert.Equal(TestBuoy.Visible, got.Db.Visible);
        // Nobody else (the other side, and the sender itself is not re-echoed by
        // the relay — it was already delivered to the addressed peer only).
        Assert.All(bots.Where(b => b != teammate), b => Assert.DoesNotContain(b.ChatBuoys, c => c.Message == "BM HOLD STATION"));
        Console.WriteLine($"LIVE-CHATBUOY: Individual({teammateId}) reached only the addressed same-side ship side {senderSide} — byte-exact — RESULT PASS");

        // ── Phase 2: Team reaches both teammates, not the opponent ──
        foreach (var b in bots) b.ResetChatBuoyReceived();
        sender.SendChatBuoy("BM TEAM GO", ChatTarget.Team, 0, TestBuoy);
        PumpUntil(server, () => majority.All(b => Seen(b, "BM TEAM GO")), 2000);
        Assert.All(majority, b => Assert.Contains(b.ChatBuoys, c => c.Message == "BM TEAM GO" && c.Cd.SidSender == senderId));
        Assert.All(minority, b => Assert.DoesNotContain(b.ChatBuoys, c => c.Message == "BM TEAM GO"));

        // ── Phase 3: Everyone reaches all three ──
        foreach (var b in bots) b.ResetChatBuoyReceived();
        sender.SendChatBuoy("BM ALL ECHO", ChatTarget.Everyone, 0, TestBuoy);
        PumpUntil(server, () => bots.All(b => Seen(b, "BM ALL ECHO")), 2000);
        foreach (var b in bots)
            Assert.Contains(b.ChatBuoys, c => c.Message == "BM ALL ECHO" && c.Cd.SidSender == senderId);

        // ── Phase 4: Individual addressed cross-side is dropped for everyone ──
        foreach (var b in bots) b.ResetChatBuoyReceived();
        sender.SendChatBuoy("BM TO ENEMY", ChatTarget.Individual, opponentId, TestBuoy);
        PumpUntil(server, () => false, 50);    // let the relay process it
        Assert.All(bots, b => Assert.DoesNotContain(b.ChatBuoys, c => c.Message == "BM TO ENEMY"));
        Console.WriteLine("LIVE-CHATBUOY: cross-side Individual dropped for all 3 — RESULT PASS");
    }
}
