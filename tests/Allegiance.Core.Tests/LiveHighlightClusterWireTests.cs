using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P142 live proof: a real <see cref="FltBot"/> sends a real
/// <c>FM_CS_HIGHLIGHT_CLUSTER(199)</c> over loopback TCP and the server relays
/// it (verbatim) to the sender's own side group — the same pure-relay shape as
/// the native handler (<c>FedSrv.CPP:7369-7375</c>: no validation, forward the
/// original bytes to <c>CFSSide::FromIGC(pfsPlayer-&gt;GetSide())-&gt;GetGroup()</c>).
/// Three bots seat round-robin onto two sides (Server.cs:523-529) → one side
/// has two members (the same-side pair) and one the solo opponent, the same
/// arrangement LiveChatBuoyWireTests uses. The "exact original bytes"
/// assertion is a re-encode comparison: the recipient's decoded HIGHLIGHT
/// re-encodes to the sender's original bytes because the server forwards
/// <c>m</c> verbatim (<c>Server.cs</c> HIGHLIGHT_CLUSTER branch).
/// </summary>
[Collection("LiveNetwork")]
public class LiveHighlightClusterWireTests
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
            new Gm { Name = "Highlight Demo", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 920u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 85200000000000000ul, DrmHash = "livehighlight",
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
    public void Real_bots_relay_sector_highlight_to_same_side_only_over_the_wire()
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
        short senderSide = sides[senderId]!.Value;

        bool Seen(FltBot b, short cluster, bool hl) =>
            b.Highlights.Any(h => h.ClusterId == cluster && h.Highlight == hl);

        // ── Phase 1: sender highlights cluster 13 — the same-side teammate
        // receives it byte-exact, the other side does not ──
        foreach (var b in bots) b.ResetHighlightsReceived();
        var sent = new HighlightCluster { ClusterId = 13, Highlight = true };
        sender.SendHighlightCluster(13, true);
        PumpUntil(server, () => Seen(teammate, 13, true), 2000);

        Assert.Contains(teammate.Highlights, h => h.ClusterId == 13 && h.Highlight == true);
        // Exact original bytes: the recipient's decoded message re-encodes to
        // the sender's own original encoding (verbatim relay preserves bytes).
        var got = teammate.Highlights.Single(h => h.ClusterId == 13);
        Assert.Equal(sent.Encode(), got.Encode());
        Assert.Equal(13, got.ClusterId);
        Assert.True(got.Highlight);
        // The different-side peer must NOT receive it.
        Assert.All(minority, b => Assert.DoesNotContain(b.Highlights, h => h.ClusterId == 13));
        Console.WriteLine($"LIVE-HIGHLIGHT: cluster 13 highlight reached only same-side ship side {senderSide} — byte-exact — RESULT PASS");

        // ── Phase 2: sender CLEARS the highlight on cluster 7 — the bool value
        // itself relays (false ≠ absence), still only same-side ──
        foreach (var b in bots) b.ResetHighlightsReceived();
        var sentClear = new HighlightCluster { ClusterId = 7, Highlight = false };
        sender.SendHighlightCluster(7, false);
        PumpUntil(server, () => Seen(teammate, 7, false), 2000);

        Assert.Contains(teammate.Highlights, h => h.ClusterId == 7 && h.Highlight == false);
        var gotClear = teammate.Highlights.Single(h => h.ClusterId == 7);
        Assert.Equal(sentClear.Encode(), gotClear.Encode());
        Assert.Equal(7, gotClear.ClusterId);
        Assert.False(gotClear.Highlight);
        Assert.All(minority, b => Assert.DoesNotContain(b.Highlights, h => h.ClusterId == 7));
        Console.WriteLine("LIVE-HIGHLIGHT: cluster 7 clear reached only same-side — bool value relayed — RESULT PASS");
    }
}
