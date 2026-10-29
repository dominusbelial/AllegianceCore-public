using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using PlayerScore = global::Allegiance.Server.PlayerScore;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P774 — a late joiner's first PLAYERINFO(10) seed carries the native
/// counters: nKills/nDeaths/nEjections from the ship's mission score
/// (<c>FedSrv.CPP:2085-2087</c>) and the rank in the NA
/// <c>rgPersistPlayerScores</c> entry (<c>:952</c> + <c>:2060-2071</c>;
/// read back by the client, clintlib.cpp:1019-1051), with dtidDrone = NA for
/// a human (<c>:2088-2090</c>, clintlib.h:343). The client then derives the
/// roster row from that seed alone — no port-only 209/219 frames.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P774PlayerInfoSeedLiveTests
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
            new Gm { Name = "P774 Seed", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 919u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 9100000000000000000ul, DrmHash = "p774seed",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static PlayerScore PlayerScoreOf(Srv server, FltBot bot) =>
        server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).PlayerScore;

    /// <summary>⭐ Two Bots seated; Ace's PlayerScore seeded K3/D2/E1/Rank 5;
    /// a third Bot seats late and its PlayerInfos entry for Ace carries
    /// 3/2/1, rank 5 via PersistScores, and dtidDrone NA.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Late_joiner_PLAYERINFO_seeds_real_kills_deaths_ejections_and_rank()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass ready-up

        using var a = new FltBot();
        using var b = new FltBot();
        a.Connect(server.BoundPort, "Ace");
        a.SendLogonRequest(Request("Ace"));
        b.Connect(server.BoundPort, "Bee");
        b.SendLogonRequest(Request("Bee"));
        int got = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "both bots must seat");

        // Seed Ace's real mission score (P194 convention: seed PlayerScore,
        // assert the wire). Native sends these through SendPlayerInfo
        // (FedSrv.CPP:2085-2087) and the NA persist entry (:952).
        var aceScore = PlayerScoreOf(server, a);
        aceScore.Kills = 3;
        aceScore.Deaths = 2;
        aceScore.Ejections = 1;
        aceScore.Rank = 5;

        using var late = new FltBot();
        late.Connect(server.BoundPort, "Cee");
        late.SendLogonRequest(Request("Cee"));
        got = PumpUntil(server, () => late.ShipAssignment is not null, 4000);
        Assert.True(got < 4000, "late bot must seat");

        short aceShipId = a.ShipAssignment!.Ship.ShipId;
        var aceInfo = late.PlayerInfos.FirstOrDefault(p => p.ShipId == aceShipId);
        Assert.NotNull(aceInfo);
        Assert.Equal((short)3, aceInfo.NKills);
        Assert.Equal((short)2, aceInfo.NDeaths);
        Assert.Equal((short)1, aceInfo.NEjections);       // P774 — the seed now carries ejections too
        var naEntry = Assert.Single(aceInfo.PersistScores);
        Assert.Equal(PlayerInfo.NA, naEntry.CivId);       // the NA entry (FedSrv.CPP:952)
        Assert.Equal((short)5, naEntry.Rank);             // rank travels the persist list, not rankID
        Assert.Equal((short)5, aceInfo.Rank);             // the client's read-back (clintlib.cpp:1042-1051)
        Assert.Equal((short)0, aceInfo.RankId);           // native rankID = 0 //NYI (FedSrv.CPP:2112)
        Assert.Equal(PlayerInfo.NA, aceInfo.DtidDrone);   // a human (FedSrv.CPP:2088-2090)
        Assert.True(aceInfo.IsHuman);

        Console.WriteLine($"P774-SEED: late joiner's PLAYERINFO for Ace: k={aceInfo.NKills} d={aceInfo.NDeaths} " +
                          $"e={aceInfo.NEjections} rank={aceInfo.Rank} (persist civ {naEntry.CivId}), dtidDrone={aceInfo.DtidDrone}");
    }
}
