using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P130 — <c>RANK_INFO</c>(13), the per-player join-sync rank-name table
/// broadcast. Real loopback proof over an in-process server + real TCP bots:
/// joining a mission sends the joining player a single <c>RANK_INFO</c>
/// carrying the REAL rank table parsed from <c>&lt;artwork&gt;/ranks.txt</c>
/// (the actual 40-entry corpus — header "40", entries 0..39), addressed to
/// that player's own connection only — not broadcast repeatedly, not
/// whole-mission-wide. Every entry carries <c>civ = -1</c> and
/// <c>requiredRanking = 0</c>, faithfully matching native's standalone
/// <c>LoadRankInfo</c> <c>#else</c> branch (FedSrv.CPP:140-141) — not an
/// invented simplification. The codec round-trips byte-exact in
/// Wire.RankInfoWireTests.
/// </summary>
[Collection("LiveNetwork")]
public class LiveRankTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";
    private const string RanksFile = ArtRoot + "/ranks.txt";

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Rank Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 880u, DwCookie = 0x880u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9400000000000000000ul, DrmHash = "rank",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Independently read the real file the way the port's loader is
    /// defined to (count header then count lines) — an oracle for the exact
    /// received table, not a re-derivation of the loader itself.</summary>
    private static List<(int Rank, string Name)> ReadRealFile()
    {
        var lines = File.ReadAllLines(RanksFile);
        int count = int.Parse(lines[0].Trim());
        var entries = new List<(int, string)>(count);
        for (int i = 1; i <= count; i++)
        {
            var parts = lines[i].Split(',', 2);
            entries.Add((int.Parse(parts[0]), parts[1]));
        }
        return entries;
    }

    /// <summary>⭐ Real player join/sync sends each joining player exactly one
    /// <c>RANK_INFO(13)</c> — to that player's own connection only — whose
    /// table is the REAL, correctly-parsed ranks.txt content: 40 entries
    /// (header count governs; the trailing duplicate line is dead data, not
    /// loaded), rank 0 "Newbie" first, rank 39 "Master 9" last, and
    /// <c>civ = -1</c> / <c>requiredRanking = 0</c> for EVERY entry (the native
    /// standalone branch, not a simplification). Two bots on different sides
    /// prove it is per-player: neither gets the other's message, and each gets
    /// exactly one — not a repeated broadcast.</summary>
    [Fact]
    public void Join_sync_sends_per_player_real_rank_table()
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

        // Give the join-sync rank burst time to land on both connections.
        int pumped = 0;
        while ((a.RankInfos.Count < 1 || b.RankInfos.Count < 1) && pumped < 4000)
        {
            server.TickSim(); pumped++; Thread.Sleep(1);
        }

        var realEntries = ReadRealFile();
        Assert.True(realEntries.Count > 0, "setup: real ranks.txt must be non-empty");

        foreach (var bot in new[] { a, b })
        {
            var msg = Assert.Single(bot.RankInfos);   // exactly one — to this player only, not broadcast repeatedly
            Assert.Equal(13, msg.GetFmid());

            // Real table: header count governs (40); trailing duplicate line not loaded.
            Assert.Equal(realEntries.Count, msg.Ranks.Length);
            Console.WriteLine($"RANK-LIVE: bot '{bot.ShipAssignment!.Ship.ShipId}' -> RANK_INFO(13) {msg.Ranks.Length} rank(s)");

            // Faithful real entries: rank-order + name match the file, in file order.
            for (int i = 0; i < realEntries.Count; i++)
            {
                Assert.Equal(realEntries[i].Rank, msg.Ranks[i].Rank);
                Assert.Equal(realEntries[i].Name, msg.Ranks[i].RankName);
            }

            // Spot-check the two explicitly-named real entries from the brief.
            Assert.Equal("Newbie", msg.Ranks[0].RankName);
            Assert.Equal(0, msg.Ranks[0].Rank);
            var last = msg.Ranks[^1];
            Assert.Equal("Master 9", last.RankName);
            Assert.Equal(realEntries[^1].Rank, last.Rank);

            // Native standalone branch: civ=-1 and requiredRanking=0 for EVERY entry.
            for (int i = 0; i < msg.Ranks.Length; i++)
            {
                Assert.Equal(-1, msg.Ranks[i].Civ);
                Assert.Equal(0, msg.Ranks[i].RequiredRanking);
            }
        }
    }
}
