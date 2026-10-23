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

[Collection("LiveNetwork")]
public sealed class P660NextMatchLiveTests(ITestOutputHelper output)
{
    private static double Scale(Srv server) => (double)typeof(Srv)
        .GetField("_timeScale", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;

    internal static void End(Srv server) => typeof(Srv)
        .GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(server, [server.Missions[0], (short)0, "P660 forced conquest"]);

    internal static ServerConfig Config(bool enabled, string dilation = "off", double intermission = 2) => new()
    {
        Port = 0,
        SideSelectAutoSeatSeconds = 30,   // P763: these ConnectionSession peers never send POSITIONREQ
        LiveSettings = new()
        {
            TimeDilation = dilation,
            AutoNextMatch = new() { Enabled = enabled, IntermissionSeconds = intermission },
            TestHarness = new() { Enabled = true, Money = 1_000_000, IgnoreTechGate = true },
        },
        // P797 harness option: the harness bots cannot scout yet (P798).
        Games = [new() { ShowMap = true, Name = "P660", Map = "m2xspiderw.igc", Teams = 2,
            CivIds = [25, 3], SideNames = ["Alpha", "Bravo"] }],
    };

    internal static bool Read(ConnectionSession peer, Func<bool> predicate)
    { lock (peer.Gate) return predicate(); }

    internal static void Pump(Srv server, Func<bool> done, int max = 5000)
    {
        for (int i = 0; i < max; i++)
        {
            if (done()) return;
            server.TickSim(); Thread.Sleep(1);
        }
        Assert.True(done(), "loopback condition timed out");
    }

    internal static ConnectionSession Connect(Srv server, string name, ulong id)
    {
        var peer = new ConnectionSession();
        peer.Connect(server.BoundPort, name);
        peer.SendLogonRequest(new()
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = 660, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = id,
        });
        Pump(server, () => Read(peer, () => peer.World.MyShipId is not null && peer.World.MySideId is not null));
        return peer;
    }

    [SkippableTheory]
    [InlineData(false, "off")]
    [InlineData(false, "auto")]
    [InlineData(true, "off")]
    [InlineData(true, "auto")]
    public void Late_game_over_rebuild_ready_countdown_started_has_no_clock_jump(bool enabled, string dilation)
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(Config(enabled, dilation, 8));
            using var a = Connect(server, "P660-human-A", 66001);
            using var b = Connect(server, "P660-human-B", 66002);
            var oldIds = new[] { a.World.MyShipId!.Value, b.World.MyShipId!.Value };
            var sides = new[] { a.World.MySideId!.Value, b.World.MySideId!.Value };
            Assert.NotEqual(sides[0], sides[1]);
            // Simulate a late game on the exact clash41 clock. NotStarted still
            // ticks normally; the old game ends before we tick its ships again.
            const double late = 2636.1478201210366;
            typeof(Srv).GetField("_simNow", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(server, late);
            server.Missions[0].Update(late);
            if (dilation == "auto")
            {
                var observe = typeof(Srv).GetMethod("ObserveTimeDilation", BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (int i = 0; i < 20; i++) observe.Invoke(server, [0.2, 1.0]);
                Assert.True(Scale(server) < 1, "auto dilation must actually slow sim time");
            }
            // Preserve every ready flag to reproduce the original migration
            // starting before the first fresh-mission Update (disabled policy).
            var flags = (System.Collections.IDictionary)typeof(Srv)
                .GetField("_playerReady", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            var peers = (System.Collections.IDictionary)typeof(Srv)
                .GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            foreach (var key in peers.Keys) flags[key] = true;
            var old = server.Missions[0]; old.Stage = MissionStage.Started;
            var watch = Stopwatch.StartNew();
            End(server);
            var fresh = server.Missions[0];
            Assert.NotSame(old, fresh);
            Assert.Equal(late, fresh.LastUpdate);
            Assert.All(fresh.Sectors, sector => Assert.Equal(late, sector.LastUpdate));
            Assert.Equal(enabled ? MissionStage.NotStarted : MissionStage.Starting, fresh.Stage);
            Pump(server, () => Read(a, () => a.World.MyShipId != oldIds[0]
                && (!enabled || a.World.GameOverResult is not null && a.World.IsIntermission))
                && Read(b, () => b.World.MyShipId != oldIds[1]
                    && (!enabled || b.World.IsIntermission && b.World.GameOverResult is not null)));
            double intermissionAt = server.SimNow;
            if (enabled)
            {
                Assert.True(Read(a, () => a.World.CountdownRemainingSeconds() is > 0 and <= 8));
                Assert.True(Read(b, () => b.World.IsIntermission && b.World.GameOverResult is not null));
                // Even ready packets from both clients must not skip the timer.
                a.SendPlayerReady(true, a.World.MyShipId!.Value);
                b.SendPlayerReady(true, b.World.MyShipId!.Value);
                for (int i = 0; i < 10; i++) { server.TickSim(); Thread.Sleep(1); }
                Assert.Equal(MissionStage.NotStarted, fresh.Stage);
                Pump(server, () => fresh.Stage == MissionStage.Starting);
                Assert.InRange(server.SimNow - late, 8, 8.06);
            }
            double briefingAt = server.SimNow;
            Pump(server, () => Read(a, () => a.World.Mission?.Stage == (int)MissionStage.Starting
                && a.World.GameOverResult is null && a.World.CountdownRemainingSeconds() is > 0 and <= 15));
            Pump(server, () => fresh.Stage == MissionStage.Started);
            Assert.InRange(server.SimNow - briefingAt, 14.94, 15.06);
            // A subsequent real update must also work; stage changes alone
            // used to hide the simpass exception behind its isolation barrier.
            for (int i = 0; i < 10; i++) server.TickSim();
            Assert.Equal(server.SimNow, fresh.LastUpdate);
            Pump(server, () => Read(a, () => a.World.Mission?.Stage == (int)MissionStage.Started)
                && Read(b, () => b.World.Mission?.Stage == (int)MissionStage.Started));
            for (int i = 0; i < 2; i++)
            {
                var peer = i == 0 ? a : b;
                Assert.True(peer.Connected);
                Assert.Equal(sides[i], peer.World.MySideId);
                var ship = server.Ships.Single(s => s.Ship.Id == peer.World.MyShipId).Ship;
                Assert.Same(fresh, ship.Mission);
                Assert.NotEqual(oldIds[i], ship.Id);
            }
            Assert.Equal(0, server.SimPassStageFailures);
            Assert.DoesNotContain("[simpass]", log.ToString());
            output.WriteLine($"enabled={enabled} dilation={dilation} scale={Scale(server):F3}; "
                + $"freshClock={late:R}; intermission observed at +{intermissionAt - late:F3}s; "
                + $"briefing at +{briefingAt - late:F3}s; started at +{server.SimNow - late:F3}s; wall={watch.Elapsed.TotalSeconds:F3}s; exceptions=0");
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unready_players_wait_when_disabled_and_auto_start_when_enabled(bool enabled)
    {
        Require.Artwork();
        using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config(enabled));
        using var a = Connect(server, "P660-unready-A", 66011);
        using var b = Connect(server, "P660-unready-B", 66012);
        server.Missions[0].Stage = MissionStage.Started;
        End(server);
        for (int i = 0; i < 100; i++) { server.TickSim(); Thread.Sleep(1); }
        Assert.Equal(enabled ? MissionStage.Starting : MissionStage.NotStarted, server.Missions[0].Stage);
        if (!enabled)
        {
            Assert.Null(a.World.CountdownStartLocalMs);
            a.SendPlayerReady(true, a.World.MyShipId!.Value);
            b.SendPlayerReady(true, b.World.MyShipId!.Value);
        }
        Pump(server, () => server.Missions[0].Stage == MissionStage.Started);
        Assert.True(a.Connected && b.Connected);
    }

    [SkippableFact]
    public void Two_consecutive_matches_deliver_both_players_results_and_reseat_every_peer()
    {
        Require.Artwork();
        using var server = new Srv();
        server.SpawnInitialMinersAtBoot = false;
        server.Boot(Config(true));
        using var a = Connect(server, "P660-two-A", 66031);
        using var b = Connect(server, "P660-two-B", 66032);
        var peers = new[] { a, b };
        var sides = peers.Select(p => p.World.MySideId).ToArray();
        server.Missions[0].Stage = MissionStage.Started;
        for (int round = 0; round < 2; round++)
        {
            var old = server.Missions[0];
            var oldIds = peers.Select(p => p.World.MyShipId).ToArray();
            foreach (var live in server.Ships) live.PlayerScore.AddRescue();
            End(server);
            Pump(server, () => peers.All(p => Read(p, () => p.World.IsIntermission
                && p.World.GameOverResult is not null && p.World.GameOverPlayersList.Count == 2
                && p.World.GameOverPlayersList.All(e => e.Rescues == 1))));
            foreach (var peer in peers)
                Assert.True(Read(peer, () => peer.World.GameOverPlayersList.Select(e => e.CharacterName).Order()
                    .SequenceEqual(new[] { "P660-two-A", "P660-two-B" })));
            Pump(server, () => server.Missions[0].Stage == MissionStage.Started
                && peers.All(p => Read(p, () => p.World.Mission?.Stage == (int)MissionStage.Started
                    && p.World.GameOverResult is null)));
            Assert.NotSame(old, server.Missions[0]);
            for (int i = 0; i < peers.Length; i++)
            {
                Assert.True(peers[i].Connected);
                Assert.NotEqual(oldIds[i], peers[i].World.MyShipId);
                Assert.Equal(sides[i], peers[i].World.MySideId);
                Assert.Same(server.Missions[0], server.Ships.Single(l => l.Ship.Id == peers[i].World.MyShipId).Ship.Mission);
            }
        }
        Assert.Equal(0, server.SimPassStageFailures);
    }
}
