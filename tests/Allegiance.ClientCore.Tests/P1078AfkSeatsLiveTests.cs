using Srv = Allegiance.Server.Server;
using Allegiance.Core.Identity;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork"), Trait("Tier", "Live")]
public sealed class P1078AfkSeatsLiveTests(ITestOutputHelper output)
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public async Task Real_five_seat_backfill_keeps_idle_human_out_and_yields_when_input_returns()
    {
        Require.Artwork();
        var config = Config();
        config.Games[0].AutoStart = true;
        config.Games[0].StartCountdownSeconds = 1;
        config.Games[0].CivIds = [25, 25];
        config.BenchmarkSeed = 1078;
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        var gate = new object();
        using var tickStop = new CancellationTokenSource();
        Exception? tickError = null;
        var ticker = new Thread(() =>
        {
            try { while (!tickStop.IsCancellationRequested) { lock (gate) server.TickSim(); Thread.Sleep(25); } }
            catch (Exception error) { tickError = error; }
        });
        ticker.Start();
        try
        {
            using var manager = new BotManager("localhost", server.BoundPort, backfill: true);
            using var driverStop = new CancellationTokenSource();
            var driving = new BackfillDriver(manager, 5).RunAsync(driverStop.Token);
            BackfillPilot[] Roster() => manager.BackfillRoster(BotNameRules.Defaults()).Pilots;
            async Task Until(Func<bool> done, int seconds = 40)
            {
                long deadline = Environment.TickCount64 + seconds * 1000;
                while (!done() && Environment.TickCount64 < deadline)
                {
                    Assert.Null(tickError);
                    Assert.False(driving.IsFaulted, driving.Exception?.ToString());
                    await Task.Delay(50);
                }
                Assert.True(done(), "backfill deadline: " + System.Text.Json.JsonSerializer.Serialize(Roster()));
            }
            try
            {
                await Until(() => new short[] { 0, 1 }.All(side => Roster().Count(p => p.Side == side && p.Bot) == 5));
                using var idle = new ConnectionSession();
                idle.Connect(server.BoundPort, "Pilot-idle-backfill");
                idle.SendLogonRequest(new() { CharacterName = "Pilot-idle-backfill", FedsrvVer = LogonRequest.MsgVer, Time = 1 });
                await Until(() => idle.Ack is { FValidated: true });
                // Both clocks are explicit: exercise the native 90-second NOAT
                // rule without waiting 90 wall seconds in every isolated rerun.
                // The server still uses its real opt-in 30-s simulation deadline.
                var activity = new PlayerActivity();
                idle.SendPlayerReady(activity.Update(0, true, false, true)!.Value, idle.Ack!.ShipID);
                await Until(() => HasReady(idle, idle.Ack.ShipID, true));
                idle.SendPlayerReady(activity.Update(90.001, true, true, false)!.Value, idle.Ack.ShipID);
                await Until(() => HasReady(idle, idle.Ack.ShipID, false));
                double joined;
                lock (gate) joined = server.TestPendingSideSelectConnectSimTime("Pilot-idle-backfill")!.Value;
                await Until(() => { lock (gate) return server.SimNow > joined + 31; });
                Assert.Null(idle.World.MyShipId);
                long generation = manager.BackfillRoster(BotNameRules.Defaults()).Generation;
                lock (gate) P660NextMatchLiveTests.End(server);
                await Until(() => manager.BackfillRoster(BotNameRules.Defaults()).Generation > generation
                    && new short[] { 0, 1 }.All(side => Roster().Count(p => p.Side == side && p.Bot) == 5));
                Assert.Null(idle.World.MyShipId);
                Assert.True(idle.Connected);
                output.WriteLine("idle native clock=90.001s; auto-seat=30 sim seconds; next match: 5 bots per side, idle peer connected on NOAT");

                // A page's mouse movement supplies this same activity=true.
                idle.SendPlayerReady(activity.Update(91, true, false, true)!.Value, idle.Ack.ShipID);
                idle.SendPositionReq(0);
                await Until(() => Roster().Count(p => p.Side == 0 && p.Bot) == 4
                    && Roster().Any(p => p.Name == "Pilot-idle-backfill" && p.Ready && p.Side == 0));
                output.WriteLine("input returned: human ready and seated; actual BackfillDriver withdrew one combat bot");
                short id = idle.World.MyShipId!.Value;
                idle.SendPlayerReady(activity.Update(271.001, false, true, false)!.Value, id);
                await Until(() => Roster().Any(p => p.Name == "Pilot-idle-backfill" && !p.Ready));
                await Until(() => Roster().Count(p => p.Side == 0 && p.Bot) == 5);
                generation = manager.BackfillRoster(BotNameRules.Defaults()).Generation;
                lock (gate) P660NextMatchLiveTests.End(server);
                await Until(() => manager.BackfillRoster(BotNameRules.Defaults()).Generation > generation
                    && Read(idle, () => idle.World.MySideId is < 0)
                    && Roster().Count(p => p.Side == 0 && p.Bot) == 5);
                Assert.True(idle.Connected);
                output.WriteLine("seated 180.001s inactivity: actual 30s backfill grace refilled; next boundary released never-launched human to lobby");
                Assert.Equal(0, server.SimPassStageFailures);
            }
            finally
            {
                driverStop.Cancel();
                try { await driving; } catch (OperationCanceledException) { }
            }
        }
        finally { tickStop.Cancel(); Assert.True(ticker.Join(10000)); }
    }

    private static ServerConfig Config(double? autoSeat = 30)
    {
        var config = P660NextMatchLiveTests.Config(true, intermission: 2);
        config.SideSelectAutoSeatSeconds = autoSeat;
        config.Games[0].AutoStart = false;
        config.Games[0].MaxTeamImbalance = 10;
        return config;
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(null, false, true)]
    [InlineData(0.0, false, true)]
    [InlineData(30.0, false, false)]
    [InlineData(30.0, true, true)]
    public void Already_unseated_carry_forward_filters_afk_only_when_automatic_seating_is_opted_in(
        double? autoSeat, bool ready, bool seated)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(autoSeat));
        using var owner = Join(server, "Pilot-owner", 0);
        using var idle = Join(server, "Pilot-noat", 0);
        Ready(server, idle, ready);
        // Native owner's FlushSides keeps the leader and moves the other player
        // to NOAT without resetting AFK (fsmission.cpp:5097-5117,1144).
        owner.SendRandomizeTeams();
        Pump(server, () => Read(idle, () => idle.World.MySideId == ClientWorld.SideTeamLobby));
        long generation = idle.World.MissionGeneration;
        P660NextMatchLiveTests.End(server);
        Pump(server, () => Read(idle, () => idle.World.MissionGeneration > generation
            && idle.World.MyShipId is not null && idle.World.MySideId is not null));
        Assert.Equal(seated, idle.World.MySideId >= 0);
        Pump(server, () => HasReady(idle, idle.World.MyShipId!.Value, ready));
        Assert.True(idle.Connected);
    }

    private static ConnectionSession Join(Srv server, string name, short? side = null)
    {
        var peer = new ConnectionSession();
        peer.Connect(server.BoundPort, name);
        peer.SendLogonRequest(new() { CharacterName = name, FedsrvVer = LogonRequest.MsgVer, Time = 1 });
        Pump(server, () => peer.Ack is not null);
        Assert.True(peer.Ack!.FValidated);
        if (side is { } s)
        {
            peer.SendPositionReq(s);
            Pump(server, () => Read(peer, () => peer.World.MyShipId is not null && peer.World.MySideId == s));
        }
        return peer;
    }

    private static bool Read(ConnectionSession peer, Func<bool> read) => P660NextMatchLiveTests.Read(peer, read);
    private static void Pump(Srv server, Func<bool> done) => P660NextMatchLiveTests.Pump(server, done);
    private static bool HasReady(ConnectionSession peer, short id, bool ready) => Read(peer,
        () => peer.World.Roster.ReadyByShipId.TryGetValue(id, out bool r) && r == ready);
    private static void Ready(Srv server, ConnectionSession peer, bool ready)
    {
        short id = peer.World.MyShipId ?? peer.Ack!.ShipID;
        peer.SendPlayerReady(ready, id);
        Pump(server, () => HasReady(peer, id, ready));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Pending_afk_survives_deadline_rotation_and_explicit_request_then_wake_seats()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        using var idle = Join(server, "Pilot-idle");
        var activity = new PlayerActivity();
        Ready(server, idle, activity.Update(0, true, false, true)!.Value);
        Assert.False(activity.Update(90.001, true, true, false));
        Ready(server, idle, false); // deterministic native wall clock, before 30 sim seconds
        double joined = server.TestPendingSideSelectConnectSimTime("Pilot-idle")!.Value;
        Pump(server, () => server.SimNow > joined + 31);
        Assert.Null(idle.World.MyShipId);
        var first = server.Missions[0];
        P660NextMatchLiveTests.End(server);
        Assert.NotSame(first, server.Missions[0]);
        for (int i = 0; i < 20; i++) server.TickSim();
        Assert.Null(idle.World.MyShipId);
        Assert.True(idle.Connected);
        // Explicit POSITIONREQ bypasses AFK, including an explicit auto-pick.
        idle.SendPositionReq(-1);
        Pump(server, () => Read(idle, () => idle.World.MySideId is >= 0 && idle.World.MyShipId is not null));
        Pump(server, () => idle.World.MyShipId is { } id && HasReady(idle, id, false));
        Assert.False(idle.World.Roster.ReadyByShipId[idle.World.MyShipId!.Value]);
        output.WriteLine("pending PLAYER_READY(false): 90.001 wall seconds; >31 sim seconds; rotation skipped; explicit auto-pick accepted while AFK");

        using var waking = Join(server, "Pilot-wake");
        Ready(server, waking, false);
        double wakeJoined = server.TestPendingSideSelectConnectSimTime("Pilot-wake")!.Value;
        Pump(server, () => server.SimNow > wakeJoined + 31);
        Assert.Null(waking.World.MyShipId);
        Ready(server, waking, true);
        Pump(server, () => Read(waking, () => waking.World.MySideId is >= 0));
        Assert.True(waking.Connected);
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(30, false, false, false)]
    [InlineData(30, true, false, true)]
    [InlineData(30, false, true, true)]
    [InlineData(0, false, false, true)]
    public void Boundary_releases_only_opt_in_afk_never_launched_players(double autoSeat, bool launched, bool flying, bool keepsSide)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(autoSeat));
        using var idle = Join(server, "Pilot-boundary", 0);
        var ship = server.Ships.Single().Ship;
        Assert.NotNull(ship.StationId);
        if (launched) ship.LastLaunchTime = server.SimNow; // a previously launched, now docked player
        if (flying) ship.StationId = null;
        Ready(server, idle, false);
        long generation = idle.World.MissionGeneration;
        P660NextMatchLiveTests.End(server);
        Pump(server, () => Read(idle, () => idle.World.MissionGeneration > generation && idle.World.MyShipId is not null));
        Assert.Equal(keepsSide ? (short)0 : ClientWorld.SideTeamLobby, idle.World.MySideId);
        Pump(server, () => idle.World.MyShipId is { } id && HasReady(idle, id, false));
        Assert.False(idle.World.Roster.ReadyByShipId[idle.World.MyShipId!.Value]);
        Assert.True(idle.Connected);
        if (!keepsSide)
        {
            idle.SendPositionReq(0); // existing lobby ship may choose without clearing AFK
            Pump(server, () => Read(idle, () => idle.World.MySideId == 0));
            Assert.False(idle.World.Roster.ReadyByShipId[idle.World.MyShipId.Value]);
        }
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Opponent_and_late_probe_receive_false_and_true_and_backfill_uses_current_ready()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config());
        using var human = Join(server, "Pilot-roster", 0);
        Ready(server, human, false);
        short humanId = human.World.MyShipId!.Value;
        using var probe = Join(server, "mcp-probe", 1);
        Pump(server, () => HasReady(probe, humanId, false));
        using var pending = Join(server, "Pilot-pending-probe");
        Pump(server, () => HasReady(pending, humanId, false));
        // Do not let a client forge another player's AFK flag.
        probe.SendPlayerReady(true, humanId);
        Pump(server, () => HasReady(probe, probe.World.MyShipId!.Value, true));
        Assert.False(probe.World.Roster.ReadyByShipId[humanId]);
        Ready(server, human, true);
        Pump(server, () => HasReady(probe, humanId, true) && HasReady(pending, humanId, true));
        Assert.Equal(0, server.SimPassStageFailures);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Default_auto_seat_off_leaves_active_and_afk_pending_but_allows_explicit_side()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(Config(null));
        using var idle = Join(server, "Pilot-default");
        Ready(server, idle, false);
        Ready(server, idle, true);
        double joined = server.TestPendingSideSelectConnectSimTime("Pilot-default")!.Value;
        Pump(server, () => server.SimNow > joined + 31);
        Assert.Null(idle.World.MyShipId);
        idle.SendPositionReq(0);
        Pump(server, () => Read(idle, () => idle.World.MySideId == 0));
    }
}
