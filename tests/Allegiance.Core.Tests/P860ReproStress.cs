using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// TEMPORARY P860 reproduction harness (delete before finishing).
[Collection("LiveNetwork")]
public class P860ReproStress
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
            new Gm { Name = "Rotation Keep Players", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 941u, DwCookie = 0x941u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9430000000000000000ul, DrmHash = "keep",
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

    private static Ship ShipOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static string Describe(Mission m) => string.Join("; ", m.Sectors
        .SelectMany(s => s.StationRuntimes)
        .Select(rt => $"station {rt.StationId} '{rt.Data.Name}' side {rt.SideId} cluster {rt.Data.ClusterId} seen=[" +
            string.Join(",", m.Sides.Select(s => $"{s.Id}:{(rt.SeenBySide(s.Id) ? "yes" : "no")}")) + "]"));

    [Fact]
    public void Repro_loop()
    {
        Require.Artwork();
        // P860: in-process contention (thread-pool + GC churn), the fast tier's environment.
        using var stop = new CancellationTokenSource();
        var hogs = Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var rnd = new Random(i);
            var sink = new List<byte[]>();
            while (!stop.IsCancellationRequested)
            {
                sink.Add(new byte[8 * 1024]);
                if (sink.Count > 512) sink.Clear();
                Thread.SpinWait(rnd.Next(100, 2000));
            }
        })).ToArray();
        try
        {
        for (int iter = 0; iter < 200; iter++)
        {
            try { OneIteration(iter); }
            catch (Exception e)
            {
                Console.WriteLine($"=== ITER {iter} FAILED ===\n{e}");
                throw;
            }
        }
        }
        finally { stop.Cancel(); Task.WaitAll(hogs, 5000); }
    }

    private static void OneIteration(int iter)
    {
        using var server = new Srv();
        server.Boot(Config());

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        using var a0 = new FltBot();
        using var a1 = new FltBot();
        using var b0 = new FltBot();
        using var b1 = new FltBot();
        short a0Id = Seat(server, a0, "KA0");
        short a1Id = Seat(server, a1, "KA1");
        short b0Id = Seat(server, b0, "KB0");
        short b1Id = Seat(server, b1, "KB1");
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, a1Id), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), side1));
        Assert.True(server.TryJoinSide(ShipOf(server, b1Id), side1));

        var bots = new (FltBot Bot, short Side)[] { (a0, side0), (a1, side0), (b0, side1), (b1, side1) };
        var oldIds = bots.Select(x => x.Bot.ShipAssignment!.Ship.ShipId).ToHashSet();

        PumpUntil(server, () => false, 40);
        var oldMission = server.Missions[0];
        server.Missions[0].Stage = MissionStage.Started;

        foreach (var (b, _) in bots)
        {
            b.ResetGameOvers();
            b.ResetMissionStages();
            b.ResetWorldExports();
            b.ResetBallotSignals();
        }

        b0.SendChat("#draw");
        Assert.True(PumpUntil(server, () => b1.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Ship), 800) < 800, "offer ballot");
        var offerBallot = b1.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Ship);
        b1.SendVote(offerBallot.BallotId, true);

        Assert.True(PumpUntil(server, () =>
            a0.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side)
            && a1.Ballots.Any(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side), 800) < 800, "accept ballot");
        a0.SendVote(a0.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side).BallotId, true);
        a1.SendVote(a1.Ballots.Single(x => x.OtInitiator == (short)Allegiance.Core.IgcObjectType.Side).BallotId, true);

        Assert.True(PumpUntil(server, () => a0.GameOvers.Count >= 1, 800) < 800, "game over");
        Assert.NotSame(oldMission, server.Missions[0]);
        var newMission = server.Missions[0];
        Assert.Equal(MissionStage.NotStarted, newMission.Stage);
        if (iter == 0)
        {
            Console.WriteLine($"ITER0 new mission [{Describe(newMission)}]");
            Console.WriteLine($"ITER0 old mission [{Describe(oldMission)}]");
            Console.WriteLine($"ITER0 sides: new={string.Join(",", newMission.Sides.Select(s => s.Id))} old={string.Join(",", oldMission.Sides.Select(s => s.Id))}");
        }

        foreach (var (bot, side) in bots)
        {
            Assert.True(PumpUntil(server,
                () => bot.MissionStages.Any(s => s.Stage == (int)MissionStage.NotStarted)
                   && bot.ShipAssignment is not null && !oldIds.Contains(bot.ShipAssignment!.Ship.ShipId)
                   && bot.ClusterExports.Count >= newMission.Sectors.Count
                   && bot.StationExports.Count >= 1,
                2000) < 2000, $"bot {side} did not re-join");

            var botSide = server.Ships.First(x => x.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship.SideId;
            var freshStations = newMission.Sectors.SelectMany(x => x.StationRuntimes).ToArray();
            foreach (var st in bot.StationExports)
            {
                var rt = freshStations.FirstOrDefault(x => x.StationId == st.Station.StationId);
                if (rt is null || !rt.SeenBySide(botSide!.Value))
                    Assert.Fail($"iter {iter}: bot side {botSide} ship {bot.ShipAssignment?.Ship.ShipId}: station {st.Station.StationId} " +
                        $"('{st.Station.Name}' side {st.Station.SideId} cluster {st.Station.ClusterId}) exported but not seen. " +
                        $"new mission [{Describe(newMission)}] stage {newMission.Stage}; old mission [{Describe(oldMission)}]; " +
                        $"received: {string.Join(" | ", bot.StationExports.Select(e => $"{e.Station.StationId}/side {e.Station.SideId}@{e.Station.ClusterId}"))}");
            }
            Assert.Equal(side, botSide);
        }
    }
}
