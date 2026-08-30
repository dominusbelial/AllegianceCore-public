using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P25 live proof: real <see cref="FltBot"/> instances exchange a real
/// <c>FM_CS_CHATMESSAGE(23)</c> over loopback TCP, and the server relays it per
/// the two wired targets. Three bots seat round-robin onto two sides
/// (Server.cs:523-529: peer N → side N mod 2), so one side necessarily has two
/// members and the other one — that's the whole point: Everyone must reach all
/// three, Team must reach exactly the sender's teammates, and an unimplemented
/// target (Wing) must reach nobody (logged and dropped, not broadcast, not
/// thrown). Side membership is taken from the server's authoritative
/// <see cref="Srv.Ships"/> (shipId → SideId), not guessed.
/// </summary>
[Collection("LiveNetwork")]
public class LiveChatWireTests
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
            new Gm { Name = "Chat Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 918u, DwCookie = 0xDEADu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 74100000000000000ul, DrmHash = "livechat",
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

    [Fact]
    public void Real_bots_exchange_chat_Everyone_and_Team_over_the_wire()
    {
        using var server = new Srv();
        server.Boot(Config());
        ushort port = server.BoundPort;

        var bots = new FltBot[3];
        for (int i = 0; i < 3; i++)
        {
            bots[i] = new FltBot();
            bots[i].Connect(port, $"BOT{i}");
        }
        for (int i = 0; i < 3; i++) bots[i].SendLogonRequest(Request($"PLAYER{i}"));

        int ticks = PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
        Assert.True(ticks < 4000, "not all three bots were assigned a ship");

        var sides = SideById(server);
        var shipIds = bots.Select(b => b.ShipAssignment!.Ship.ShipId).ToArray();
        Assert.All(shipIds, id => Assert.NotNull(sides[id]));
        Assert.Equal(3, shipIds.Length);

        // Round-robin seating across two sides (Server.cs:523-529, by drain
        // order which races) yields exactly one side with two members and one
        // with one. Group by side so we never assume which side is the majority.
        var groups = bots.GroupBy(b => sides[b.ShipAssignment!.Ship.ShipId]).ToList();
        Assert.Equal(2, groups.Count);
        var majority = groups.First(g => g.Count() == 2).ToArray();   // the two teammates
        var minority = groups.First(g => g.Count() == 1).ToArray();   // the solo opponent
        var sender = majority[0];
        var teammates = majority;                                     // includes the sender
        var opponents = minority;

        // ── Phase 1: Everyone broadcast reaches all three ──
        foreach (var b in bots) b.ResetChatReceived();
        sender.SendChat("ALL HELLO", ChatTarget.Everyone);
        PumpUntil(server, () => bots.All(b => b.Chats.Any(c => c.Message == "ALL HELLO")), 2000);
        foreach (var b in bots)
            Assert.Contains(b.Chats, c => c.Message == "ALL HELLO" && c.SidSender == sender.ShipAssignment!.Ship.ShipId);

        // ── Phase 2: Team reaches teammates, not opponents ──
        // (No reset here: Phase 1's ALL HELLO must remain so the captured
        // evidence below shows Everyone and Team together; Phase 3 resets.)
        sender.SendChat("TEAM HI", ChatTarget.Team);
        // Delivery to the two teammates happens in the same tick the relay runs;
        // once they both see it, the opponent was likewise passed-over in that
        // same synchronous pass, so its emptiness is asserted immediately after.
        PumpUntil(server, () => teammates.All(t => t.Chats.Any(c => c.Message == "TEAM HI")), 2000);
        Assert.All(teammates, b => Assert.Contains(b.Chats, c => c.Message == "TEAM HI"));
        Assert.All(opponents, b => Assert.DoesNotContain(b.Chats, c => c.Message == "TEAM HI"));

        // ── verbatim live proof, captured before the Wing-reset wipes it ──
        Console.WriteLine($"LIVE-CHAT: sender ship {sender.ShipAssignment!.Ship.ShipId} side {sides[sender.ShipAssignment!.Ship.ShipId]}");
        foreach (var b in bots)
        {
            var ass = b.ShipAssignment!;
            Console.WriteLine($"LIVE-CHAT: bot ship {ass.Ship.ShipId} side {sides[ass.Ship.ShipId]} " +
                              $"everyone=[{string.Join(", ", b.Chats.Where(c => c.Message == "ALL HELLO").Select(_ => "ALL HELLO"))}] " +
                              $"team=[{string.Join(", ", b.Chats.Where(c => c.Message == "TEAM HI").Select(_ => "TEAM HI"))}]");
        }

        // ── Phase 3: an unimplemented target is logged and dropped, not broadcast ──
        foreach (var b in bots) b.ResetChatReceived();
        sender.SendChat("WING ONLY", ChatTarget.Wing);     // value 8 — not in this slice
        PumpUntil(server, () => false, 50);                // give the relay a beat to process
        Assert.All(bots, b => Assert.Empty(b.Chats));
        Console.WriteLine("LIVE-CHAT: Wing target dropped for all 3 — RESULT PASS");
    }
}
