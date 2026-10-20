using System.Diagnostics;
using System.Reflection;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P765 — the ~0.7 s game-over tick stall. Two root causes, both fixed:
/// (1) <c>AssembleGameMission</c> re-ran the full research-plan selection
/// (<c>ResearchPlanCatalog.Select</c>: re-reading + validating EVERY plan file
/// from disk) on every rotation — ~0.5 s of the stall — now memoized in
/// <c>SelectShowcasePlans</c>; (2) the P480 migration re-sent every migrated
/// peer's fresh-joiner join burst synchronously (superlinear in peers), now
/// spread across ticks in bounded batches. These tests pin the regression:
/// game-over still reaches every peer once before the reset, every peer still
/// migrates into the rotated (identical-content) mission, and the game-over
/// tick stays under budget.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P765GameOverStallTests(ITestOutputHelper output)
{
    private static ServerConfig Config() => new()
    {
        Port = 0,
        Showcase = new() { TechMode = "research", IncomeMultiplier = 500, ResearchTimeScale = 16 },
        SideSelectAutoSeatSeconds = 30, // P763: ConnectionSession peers never send POSITIONREQ
        LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
        Games = [new() { Name = "P765", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"],
            MaxTeamImbalance = 32767,
            MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 20, PlayerSectorSpecialAsteroids = 8 } }],
    };

    private static bool Read(ConnectionSession peer, Func<bool> predicate)
    { lock (peer.Gate) return predicate(); }

    private static void Pump(Srv server, Func<bool> done, int max = 9000)
    {
        for (int i = 0; i < max; i++)
        {
            if (done()) return;
            server.TickSim(); Thread.Sleep(1);
        }
        Assert.True(done(), "loopback condition timed out");
    }

    private static ConnectionSession Connect(Srv server, string name, ulong id)
    {
        var peer = new ConnectionSession();
        peer.Connect(server.BoundPort, name);
        peer.SendLogonRequest(new()
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = 465, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = id,
        });
        Pump(server, () => Read(peer, () => peer.World.MyShipId is not null && peer.World.MySideId is not null));
        return peer;
    }

    private static bool PendingRotationsEmpty(Srv server)
    {
        var list = (System.Collections.ICollection)typeof(Srv)
            .GetField("_pendingRotations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
        return list.Count == 0;
    }

    /// <summary>
    /// P765 regression: a game over still delivers GAME_OVER to EVERY peer once,
    /// rotates to a fresh (identical-content) NotStarted mission at the same
    /// index, and — despite the P765 batching — migrates every peer into the
    /// fresh mission. Uses 24 peers (> the 8-per-tick batch) so the spread-across-
    /// ticks path is actually exercised, and asserts the migration queue fully
    /// drains with zero sim-pass failures.
    /// </summary>
    [SkippableFact, Trait("Tier", "Live")]
    public void P765_GameOver_delivers_once_rotates_and_migrates_every_peer()
    {
        Require.Artwork();
        var saved = Console.Out;
        var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            const int peers = 24;
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(Config());
            var conns = new List<ConnectionSession>();
            for (int i = 0; i < peers; i++)
                conns.Add(Connect(server, $"P765-p{i:D3}", 46900ul + (ulong)i));
            var oldIds = conns.Select(c => c.World.MyShipId!.Value).ToArray();
            var old = server.Missions[0];
            old.Stage = MissionStage.Started;

            typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server, [server.Missions[0], (short)0, "P765 regression"]);

            var fresh = server.Missions[0];
            // Rotation is synchronous (the swap happens in the game-over tick);
            // the migration drains over the following ticks.
            Assert.NotSame(old, fresh);
            Assert.Equal(MissionStage.NotStarted, fresh.Stage);
            Assert.True(fresh.Sides.Count == 2, "fresh mission assembles both sides");

            // Every peer receives GAME_OVER, then is migrated into the fresh mission.
            Pump(server, () =>
                conns.All(c => Read(c, () => c.World.MyShipId != oldIds[conns.IndexOf(c)]
                    && c.World.GameOverResult is not null))
                && PendingRotationsEmpty(server));

            for (int i = 0; i < peers; i++)
            {
                var ship = server.Ships.Single(s => s.Ship.Id == conns[i].World.MyShipId).Ship;
                Assert.Same(fresh, ship.Mission);
                Assert.NotEqual(oldIds[i], ship.Id);
            }
            Assert.Equal(0, server.SimPassStageFailures);
            Assert.DoesNotContain("[simpass]", log.ToString());
            output.WriteLine($"peers={peers}; all {peers} peers received GAME_OVER and migrated into fresh mission #{server.Missions.ToList().IndexOf(fresh)}; exceptions=0");
        }
        finally { Console.SetOut(saved); }
    }

    /// <summary>
    /// P765 perf guard (Tier Perf): the game-over tick — every part that still
    /// runs on the tick (skills report, score finalize, GAME_OVER encode + send
    /// to every peer, the cached assembly/swap, and the first migration batch) —
    /// stays under a budget of 50 ms at a realistic loopback size, measured as
    /// the median of five fresh runs. Hardware-scaled via
    /// <see cref="PerfCalibration"/>. Pre-P765 this same tick measured ~0.55 s
    /// (assembly ~0.51 s + bursts) at these sizes.
    /// </summary>
    [SkippableTheory, Trait("Tier", "Perf")]
    [InlineData(48)]
    public void P765_GameOver_tick_stays_under_budget_at_size(int peers)
    {
        Require.Artwork();
        double budget = PerfCalibration.Scale(50.0);
        var samples = new List<double>();
        for (int run = 0; run < 5; run++)
        {
            var saved = Console.Out;
            var log = new StringWriter();
            Console.SetOut(log);
            using var server = new Srv();
            try
            {
                server.SpawnInitialMinersAtBoot = false;
                server.Boot(Config());
                var conns = new List<ConnectionSession>();
                for (int i = 0; i < peers; i++)
                    conns.Add(Connect(server, $"P765{peers}x{run}{i:D3}", 47200ul + (ulong)run * 997 + (ulong)i));
                server.Missions[0].Stage = MissionStage.Started;
                var watch = Stopwatch.StartNew();
                typeof(Srv).GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(server, [server.Missions[0], (short)0, "P765 perf"]);
                watch.Stop();
                samples.Add(watch.Elapsed.TotalMilliseconds);
                // Let the migration drain so the server disposes cleanly.
                Pump(server, () => PendingRotationsEmpty(server), 6000);
            }
            finally { Console.SetOut(saved); }
        }
        var sorted = samples.OrderBy(v => v).ToArray();
        double median = sorted[sorted.Length / 2];
        output.WriteLine($"peers={peers} samples=[{string.Join(", ", samples.Select(s => s.ToString("0.0")))}] median={median:0.0}ms budget={budget:0.0}ms");
        Assert.True(median < budget, $"game-over tick median {median:0.0}ms exceeded {budget:0.0}ms budget at {peers} peers");
    }
}
