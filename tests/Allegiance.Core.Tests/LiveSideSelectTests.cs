using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P281 — the post-connect side-select flow: a joining peer is held in a
/// pending side-select state (no ship spawned, launch deferred) until it picks
/// a side via POSITIONREQ. Native has no timer; the port's auto-seat is an
/// opt-in adaptation (<c>ServerConfig.SideSelectAutoSeatSeconds</c>, P763). This
/// is the port of native's pre-game side choice (FM_C_POSITIONREQ, Messages.h:320),
/// surfaced as a real screen between connecting and flying. Verifies the
/// deferral, the explicit per-side choice, the roster broadcast the screen is
/// rendered from, and both the off (native) and on (30 s) auto-seat modes.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class LiveSideSelectTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(double? autoSeatSeconds = 30) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        // P763 — the side-select auto-seat is now an opt-in port adaptation;
        // these live tests keep the pre-P763 30 s behaviour on purpose.
        SideSelectAutoSeatSeconds = autoSeatSeconds,
        Games =
        [
            new Gm { Name = "SideSel", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Bios", "IronCo"], AutoStart = false },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 999u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9910000000000000000ul, DrmHash = "sel",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Side Side(Srv server, short sideId) =>
        server.Missions[0].Sides.First(s => s.Id == sideId);

    /// <summary>Connect a bot and hold it at the side-select screen (no auto
    /// side selection).</summary>
    private static void ConnectPending(Srv server, FltBot bot, string name)
    {
        bot.AutoSelectSideAfterLogon = false;
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        // Let the logon handshake + initial side roster land (but the bot must
        // stay pending — it never auto-sends POSITIONREQ).
        PumpUntil(server, () => bot.SideRosters.Count > 0, 200);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Peer_is_held_at_side_select_until_it_picks_a_side()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var b = new FltBot();

        ConnectPending(server, b, "SEL1");

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.Equal((short)0, side0);
        Assert.Equal((short)1, side1);

        // Deferral: no SET_CLUSTER yet, no ship registered server-side.
        Assert.Null(b.ShipAssignment);
        Assert.Empty(server.Ships);

        // The side-select screen is rendered from a real SIDE_ROSTER broadcast
        // (names + civ ids + counts), not hardcoded.
        var roster = Assert.Single(b.SideRosters);
        Assert.Equal(2, roster.Sides.Length);
        Assert.Equal("Bios", roster.Sides[0].Name);
        Assert.Equal("IronCo", roster.Sides[1].Name);
        Assert.Equal(1, roster.Sides[0].CivId);
        Assert.Equal(3, roster.Sides[1].CivId);
        Assert.All(roster.Sides, s => Assert.Equal(0, s.PlayerCount));

        // The peer picks side0 explicitly → now it is seated + launched on side0.
        b.SendPositionReq(side0);
        int got = PumpUntil(server, () => b.ShipAssignment is not null, 800);
        Assert.True(got < 800, "peer did not seat after picking a side");

        short bId = b.ShipAssignment!.Ship.ShipId;
        var ship = server.Ships.First(s => s.Ship.Id == bId).Ship;
        Assert.Equal(side0, ship.SideId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Two_peers_picking_different_sides_land_on_their_chosen_sides()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;

        ConnectPending(server, a, "SELA");
        ConnectPending(server, b, "SELB");

        a.SendPositionReq(side0);
        b.SendPositionReq(side1);

        int got = PumpUntil(server,
            () => a.ShipAssignment is not null && b.ShipAssignment is not null, 800);
        Assert.True(got < 800, "both peers did not seat after choosing");

        short aId = a.ShipAssignment!.Ship.ShipId;
        short bId = b.ShipAssignment!.Ship.ShipId;
        Assert.Equal(side0, server.Ships.First(s => s.Ship.Id == aId).Ship.SideId);
        Assert.Equal(side1, server.Ships.First(s => s.Ship.Id == bId).Ship.SideId);
        Assert.NotEqual(aId, bId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Silent_peer_is_auto_assigned_after_the_side_select_timeout()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var b = new FltBot();

        ConnectPending(server, b, "SELTIME");

        // Never picks a side. The opt-in 30 s auto-seat (@ 20 upd = 600
        // ticks) auto-assigns it (arrival order) and launches it, so it never
        // blocks the P180 auto-start nor hangs forever.
        int got = PumpUntil(server, () => b.ShipAssignment is not null, 800);
        Assert.True(got < 800, "silent peer was not auto-assigned after the timeout");

        short bId = b.ShipAssignment!.Ship.ShipId;
        var ship = server.Ships.First(s => s.Ship.Id == bId).Ship;
        Assert.NotNull(ship.SideId);   // auto-assigned to a real side
    }

    /// <summary>P763 — the auto-seat is opt-in: with the flag ON (30 s), a
    /// silent peer is seated at the configured deadline, not before it.
    /// P1021 — the deadline is judged on the server's own clock, one sim tick
    /// at a time, from the pending peer's recorded logon time: the tick before
    /// the seat is still short of 30 s and the seating tick is the first one
    /// at or past it. The client's receipt of the join burst is then checked
    /// as delivery only. (The old form read the sim clock when the bot's
    /// receive thread surfaced SET_CLUSTER; the fast pump runs ~40 sim-seconds
    /// per wall-second, so a 10–60 ms delivery read as 30.4–32.7 "seconds".)</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Flag_on_auto_seats_a_silent_peer_at_the_configured_30_seconds()
    {
        Require.Artwork();
        const double AutoSeatSeconds = 30.0;
        const int UpdatesPerSecond = 20;   // Config()
        const double Period = 1.0 / UpdatesPerSecond;
        using var server = new Srv();
        server.Boot(Config(AutoSeatSeconds));
        using var b = new FltBot();

        ConnectPending(server, b, "SELON30");
        // The instant the server's deadline is measured from (set when the
        // LOGONREQ was handled), not the later moment the roster reached us.
        double? pendingSince = server.TestPendingSideSelectConnectSimTime("SELON30");
        Assert.True(pendingSince.HasValue, "the silent peer is not pending side-select after logon");
        double connect = pendingSince!.Value;
        Assert.Null(b.ShipAssignment);
        Assert.Empty(server.Ships);

        // Step the server alone (no wall-clock pacing) until it seats the
        // peer, remembering the clock of the last tick that left it pending.
        double lastPendingNow = server.SimNow;
        int ticks = 0;
        const int MaxTicks = 800;   // 40 sim-seconds: a missing timer fails here
        while (server.Ships.Count == 0 && ticks < MaxTicks)
        {
            lastPendingNow = server.SimNow;
            server.TickSim();
            ticks++;
            if (server.Ships.Count == 0)
                Assert.Equal(connect, server.TestPendingSideSelectConnectSimTime("SELON30"));   // still pending, deadline not re-armed
        }
        Assert.True(server.Ships.Count > 0, $"silent peer was not auto-seated within {MaxTicks} ticks of the 30 s flag");
        double seatNow = server.SimNow;

        // Not before: the tick before the seat was short of the deadline (the
        // same double comparison AdvanceSideSelection makes), and that was the
        // immediately preceding tick. At: the seating tick is at or past it.
        // Together: seated on the first tick at/after connect + 30 s, i.e. in
        // [30, 30 + one tick).
        Assert.True(lastPendingNow - connect < AutoSeatSeconds,
            $"seated late: still pending at {lastPendingNow - connect:R} s after logon");
        Assert.True(seatNow - connect >= AutoSeatSeconds,
            $"seated early: {seatNow - connect:R} s after logon");
        Assert.Equal(Period, seatNow - lastPendingNow, 9);
        Assert.InRange(seatNow - connect, AutoSeatSeconds, AutoSeatSeconds + Period);
        Assert.Null(server.TestPendingSideSelectConnectSimTime("SELON30"));   // no longer pending

        // Exactly one ship, on a real side, seated by the timer (no POSITIONREQ
        // was ever sent).
        var seated = Assert.Single(server.Ships).Ship;
        Assert.NotNull(seated.SideId);

        // Delivery: the join burst reaches the silent client and names that
        // ship. This is transport latency, not the timer, so it is bounded in
        // wall-clock time and never read against the sim clock.
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (b.ShipAssignment is null && deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            server.TickSim();
            Thread.Sleep(1);
        }
        Assert.True(b.ShipAssignment is not null, "auto-seated peer never received its SET_CLUSTER");
        Assert.Equal(seated.Id, b.ShipAssignment!.Ship.ShipId);
        Assert.Single(server.Ships);
    }

    /// <summary>P763 — native has no timeout: with the flag off (null/absent,
    /// zero or negative), a silent peer stays pending past the old 31 s mark,
    /// while a peer that does send POSITIONREQ is still seated.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void Flag_off_keeps_a_silent_peer_pending_past_31_seconds_and_an_explicit_positionreq_still_seats_it(double? autoSeatSeconds)
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(autoSeatSeconds));
        using var b = new FltBot();

        ConnectPending(server, b, "SELOFF");
        double start = server.SimNow;

        int pumped = PumpUntil(server, () => server.SimNow - start >= 31.0, 800);
        Assert.True(pumped < 800, "server did not advance 31 simulation seconds");
        Assert.Null(b.ShipAssignment);
        Assert.Empty(server.Ships);

        // The native path is unaffected: a recorded POSITIONREQ choice seats it.
        short side0 = server.Missions[0].Sides[0].Id;
        b.SendPositionReq(side0);
        Assert.True(PumpUntil(server, () => b.ShipAssignment is not null, 800) < 800,
            "peer with an explicit POSITIONREQ was not seated with auto-seat off");
        short bId = b.ShipAssignment!.Ship.ShipId;
        Assert.Equal(side0, server.Ships.First(s => s.Ship.Id == bId).Ship.SideId);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mission_owner_can_randomize_teams_from_the_side_select_flow_but_non_owner_cannot()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var c = new FltBot();
        using var b = new FltBot();

        short side0 = server.Missions[0].Sides[0].Id;
        short side1 = server.Missions[0].Sides[1].Id;
        Assert.Equal(MissionStage.NotStarted, server.Missions[0].Stage);

        // A picks side0 -> first player there -> leader AND mission owner.
        ConnectPending(server, a, "RNDA");
        a.SendPositionReq(side0);
        Assert.True(PumpUntil(server, () => a.ShipAssignment is not null, 400) < 400, "A did not seat");
        short aId = a.ShipAssignment!.Ship.ShipId;
        Assert.Equal(side0, server.Ships.First(s => s.Ship.Id == aId).Ship.SideId);
        Assert.Equal(aId, Side(server, side0).LeaderShipId);
        Assert.Equal(side0, server.Missions[0].MissionOwnerSideId);

        // C picks side1 -> leader of side1 (not the owner: side0 owns the mission).
        ConnectPending(server, c, "RNDC");
        c.SendPositionReq(side1);
        Assert.True(PumpUntil(server, () => c.ShipAssignment is not null, 400) < 400, "C did not seat");
        short cId = c.ShipAssignment!.Ship.ShipId;
        Assert.Equal(side1, server.Ships.First(s => s.Ship.Id == cId).Ship.SideId);

        // B picks side1 (already has leader C) -> joins as a NON-leader.
        ConnectPending(server, b, "RNDB");
        b.SendPositionReq(side1);
        Assert.True(PumpUntil(server, () => b.ShipAssignment is not null, 400) < 400, "B did not seat");
        short bId = b.ShipAssignment!.Ship.ShipId;
        Assert.Equal(side1, server.Ships.First(s => s.Ship.Id == bId).Ship.SideId);

        // B is not the owner: its RANDOMIZE_TEAMS is refused -> nobody moves.
        b.SendRandomizeTeams();
        PumpUntil(server, () => false, 40);
        Assert.Equal(side1, server.Ships.First(s => s.Ship.Id == bId).Ship.SideId);

        // A is the mission owner: its RANDOMIZE_TEAMS rebalances -> the lone
        // non-leader B is moved to the lightest side (both leader sums equal, so
        // the first side wins), evidencing the trigger fired over the wire.
        // P864b — B must be READY to be re-seated (native AFK filter). Mark all
        // and pump until side-ready confirms application before the rebalance.
        a.SendPlayerReady(true, aId); b.SendPlayerReady(true, bId); c.SendPlayerReady(true, cId);
        Assert.True(PumpUntil(server, () => server.Missions[0].Sides.All(s => s.Ready), 2000) < 2000,
            "players did not reach side-ready before the rebalance");
        a.SendRandomizeTeams();
        Assert.True(PumpUntil(server, () => server.Ships.First(s => s.Ship.Id == bId).Ship.SideId == side0, 400) < 400,
            "owner RANDOMIZE_TEAMS did not rebalance the non-leader onto the lighter side");
        Assert.Equal(side0, server.Ships.First(s => s.Ship.Id == bId).Ship.SideId);
        Assert.Equal(side1, server.Ships.First(s => s.Ship.Id == cId).Ship.SideId);   // leaders never move
    }
}
