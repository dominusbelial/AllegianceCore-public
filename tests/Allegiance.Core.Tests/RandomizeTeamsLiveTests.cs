using System.Collections;
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
/// P195 — <c>RANDOMIZE_TEAMS</c>(21), the mission owner's "balance the teams"
/// trigger (not actually random: a highest-rank-first greedy reassignment to
/// whichever side currently has the lowest rank sum). Real loopback proof over
/// an in-process server + real TCP bots. The rebalance is the Stage-1
/// substitute the brief specifies — direct one-pass redistribution across the
/// existing real sides (this port has no <c>SIDE_TEAMLOBBY</c> to flush to):
/// leaders never move and anchor their side's rank sum, every non-leader is
/// picked highest-rank-first and seated onto the currently-lowest-rank-sum
/// side, pending join requests (P184) are cleared first, and reassignment
/// broadcasts ride the existing per-player JOIN_SIDE/QUIT_SIDE announcements
/// (nothing new is invented).
/// </summary>
[Collection("LiveNetwork")]
public class RandomizeTeamsLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        // P864b — AutoStart=false so marking every bot READY (the rebalance's
        // AFK filter) does not auto-start the mission out of NotStarted before
        // the owner's RANDOMIZE_TEAMS (which the dispatch refuses once started).
        Games =
        [
            new Gm { Name = "RT Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false },
        ],
    };

    private static Cfg ConfigTwoGames() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "RT A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"], AutoStart = false },
            new Gm { Name = "RT B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"], AutoStart = false },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 884u, DwCookie = 0x884u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8870000000000000000ul, DrmHash = "rt195",
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

    private static Ship ShipOf(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).Ship;

    private static PlayerScore PlayerScoreOf(Srv server, short id) =>
        server.Ships.First(s => s.Ship.Id == id).PlayerScore;

    private static short SideOf(Srv server, short id) =>
        ShipOf(server, id).SideId!.Value;

    private static Mission Mission0(Srv server) => server.Missions[0];

    /// <summary>The current rank-sum of a real side (the port's
    /// <c>GetSideRankSum</c> reading): sum of every seated ship's
    /// <c>max(Rank,1)</c> on that side, mission A.</summary>
    private static int RankSum(Srv server, short sideId)
    {
        int s = 0;
        foreach (var live in server.Ships)
            if (ReferenceEquals(live.Ship.Mission, Mission0(server)) && live.Ship.SideId == sideId)
                s += Math.Max(live.PlayerScore.Rank, (short)1);
        return s;
    }

    private static List<short> ShipsOnSide(Srv server, Mission m, short sideId) =>
        server.Ships.Where(l => ReferenceEquals(l.Ship.Mission, m) && l.Ship.SideId == sideId)
                    .Select(l => l.Ship.Id).ToList();

    /// <summary>Spawn every peer UNSEATED (SideId=null) on mission 0, so the
    /// test controls seating entirely via TryJoinSide (the default spawner
    /// round-robins peers onto sides and never sets a leader, which would make
    /// the "seat into an empty side first" leader-establishment non-deterministic).</summary>
    private static void SetUnseatedSpawner(Srv server)
    {
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = server.Missions[0];
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(400f, 740f, 930f), Vector3.Zero, Orientation.Identity);
            ship.SideId = null;                 // unseated — the test drives TryJoinSide
            m.PlaceShip(ship, m.Sectors[0]);
            return (ship, m.Sectors[0]);
        };
    }

    private static int PendingJoinCount(Srv server)
    {
        var f = typeof(Srv).GetField("_pendingJoinRequests",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (IDictionary)f.GetValue(server)!;
        return dict.Count;
    }

    // ── codec ────────────────────────────────────────────────────────────

    /// <summary>RANDOMIZE_TEAMS(21) is a zero-field client trigger: header
    /// only, cbmsg = 4. Exact bytes pinned, mirroring the sibling StartGame /
    /// Suicide zero-payload codecs.</summary>
    [Fact]
    public void RandomizeTeams_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)21, RandomizeTeams.Fmid);
        Assert.Equal(4, RandomizeTeams.FixedSize);

        byte[] b = new RandomizeTeams().Encode();
        Assert.Equal(4, b.Length);
        Assert.Equal(4, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b));   // cbmsg
        Assert.Equal(21, System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(2))); // fmid

        var d = RandomizeTeams.Decode(b);
        Assert.Equal((ushort)21, d.GetFmid());
    }

    // ── the main rebalance ───────────────────────────────────────────────

    /// <summary>⭐ The quantitative must-fail-if-removed proof. Two sides,
    /// uneven populations, varied <c>PlayerScore.Rank</c>. Owners/leaders A
    /// (rank 5, side α) and B (rank 1, side β) never move and anchor their
    /// sides' rank sums; non-leaders C(10), D(9), E(8) are picked
    /// highest-rank-first and seated onto the currently-lowest-rank-sum side:
    ///   pool order 10,9,8 · running sums {α:5, β:1}
    ///   C→β (β:11), D→α (α:14), E→β (β:19)
    /// Final rank sums α=14, β=19 (mean 16.5 — both within one player's worth
    /// of the mean; imbalance 5), versus the pre-rebalance 24/9. Removing the
    /// rebalance leaves the ships on 24/9 and every assertion here fails.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rebalance_distributes_non_leaders_to_balance_rank_sums()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot();
        using var c = new FltBot(); using var d = new FltBot(); using var e = new FltBot();
        short aId = Seat(server, a, "RA"); short bId = Seat(server, b, "RB");
        short cId = Seat(server, c, "RC"); short dId = Seat(server, d, "RD"); short eId = Seat(server, e, "RE");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        Assert.Equal(2, m.Sides.Count);

        // Establish leadership: A leads+owns side0, B leads side1, C/D/E non-leaders.
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader + mission owner
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B leader of side1
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, dId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, eId), side1));
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(bId, m.Sides.First(s => s.Id == side1).LeaderShipId);

        // Seed the P193 ranks.
        PlayerScoreOf(server, aId).Rank = 5;
        PlayerScoreOf(server, bId).Rank = 1;
        PlayerScoreOf(server, cId).Rank = 10;
        PlayerScoreOf(server, dId).Rank = 9;
        PlayerScoreOf(server, eId).Rank = 8;

        // P864b — native RandomizeSides re-seats only READY (non-AFK) players
        // (fsmission.cpp:5235-5241, GetReady()). Mark EVERY bot ready (leaders
        // are never moved regardless) and pump until the server has applied the
        // readiness (both sides' aggregate Ready) so the rebalance observes the
        // ready state — a plain "send then randomize" races the TCP datagrams.
        a.SendPlayerReady(true, aId); b.SendPlayerReady(true, bId);
        c.SendPlayerReady(true, cId); d.SendPlayerReady(true, dId); e.SendPlayerReady(true, eId);
        int rd = PumpUntil(server, () => m.Sides.All(s => s.Ready), 2000);
        Assert.True(rd < 2000, "players did not reach side-ready before the rebalance");

        // Pre-rebalance: side0 = A5+C10+D9 = 24, side1 = B1+E8 = 9 — imbalanced.
        Assert.Equal(24, RankSum(server, side0));
        Assert.Equal(9, RankSum(server, side1));

        // The owner (A) triggers the rebalance over the real wire.
        a.SendRandomizeTeams();
        int settled = PumpUntil(server,
            () => RankSum(server, side0) == 14 && RankSum(server, side1) == 19, 2000);
        Assert.True(settled < 2000, "rebalance did not reach the balanced 14/19 split");

        // Exact greedy outcome: side0 = {A,D} (5+9=14), side1 = {B,C,E} (1+10+8=19).
        Assert.Equal(new HashSet<short> { aId, dId }, ShipsOnSide(server, m, side0).ToHashSet());
        Assert.Equal(new HashSet<short> { bId, cId, eId }, ShipsOnSide(server, m, side1).ToHashSet());

        // Leaders never moved; A remains mission owner.
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(bId, m.Sides.First(s => s.Id == side1).LeaderShipId);
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(side1, SideOf(server, bId));
        Assert.Equal(side0, m.MissionOwnerSideId);

        // Balanced bound: each side within one player's worth of the mean (16.5),
        // and the imbalance (5) is below the largest rank (10).
        Assert.Equal(33, RankSum(server, side0) + RankSum(server, side1));
        Assert.True(Math.Abs(RankSum(server, side0) - 16.5) <= 10);
        Assert.True(Math.Abs(RankSum(server, side1) - 16.5) <= 10);
    }

    /// <summary>⭐ A team leader is never reassigned during a rebalance — even
    /// when its rank would make its side the constant lowest-rank target, the
    /// leader is excluded from the pool and stays put (native fsmission.cpp:
    /// 5161-5182 never moves <c>GetLeader(sideID)</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Team_leaders_are_never_reassigned_during_rebalance()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot();
        using var c = new FltBot(); using var d = new FltBot();
        short aId = Seat(server, a, "LA"); short bId = Seat(server, b, "LB");
        short cId = Seat(server, c, "LC"); short dId = Seat(server, d, "LD");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader + owner
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B leader
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, dId), side1));

        // A is a huge-rank leader; non-leaders C/D balance around the leaders.
        PlayerScoreOf(server, aId).Rank = 999;
        PlayerScoreOf(server, bId).Rank = 989;
        PlayerScoreOf(server, cId).Rank = 960;
        PlayerScoreOf(server, dId).Rank = 850;

        // P864b — non-leaders must be READY to be re-seated after the flush.
        // Mark all (leaders harmless) and pump until the side-ready aggregate
        // confirms the ready datagrams were applied before the rebalance.
        a.SendPlayerReady(true, aId); b.SendPlayerReady(true, bId);
        c.SendPlayerReady(true, cId); d.SendPlayerReady(true, dId);
        int rd = PumpUntil(server, () => m.Sides.All(s => s.Ready), 2000);
        Assert.True(rd < 2000, "players did not reach side-ready before the rebalance");

        a.SendRandomizeTeams();
        int settled = PumpUntil(server, () => ShipsOnSide(server, m, side0).Contains(cId) == false, 2000);
        // The two non-leaders are split onto the two sides, but A and B stay put.
        Assert.True(settled < 2000, "rebalance did not move the non-leader C off side0");
        Assert.Equal(side1, SideOf(server, cId));   // C was actually reassigned (non-vacuous)
        Assert.Equal(side0, SideOf(server, dId));   // D actually reassigned too
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(side1, SideOf(server, bId));
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(bId, m.Sides.First(s => s.Id == side1).LeaderShipId);
        Assert.Equal(side0, m.MissionOwnerSideId);
        // The side occupied by the leaders has exactly one non-leader moved in
        // (i.e. neither leader moved, both sides still occupied by their leader).
        Assert.Contains(ShipsOnSide(server, m, side0), s => s == aId);
        Assert.Contains(ShipsOnSide(server, m, side1), s => s == bId);
    }

    /// <summary>⭐ Pending join requests (P184) for the mission are cleared as
    /// part of the rebalance (native fsmission.cpp:5183-5196) — a mass
    /// reassignment must not leave a stale pending request behind.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rebalance_clears_pending_join_requests()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot();
        short aId = Seat(server, a, "PA"); short bId = Seat(server, b, "PB");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader + owner
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B non-leader on side1
        // P841: native InitSide seeds rgfAutoAccept=true (fsmission.cpp:58); the
        // leader turns it off via AUTO_ACCEPT(52) so the request pends.
        m.Sides[0].AutoAccept = false;

        // B requests side0 (led by A, auto-accept off) → recorded as pending.
        b.SendPositionReq(side0);
        int reqGot = PumpUntil(server, () => PendingJoinCount(server) >= 1, 800);
        Assert.True(reqGot < 800, "no pending join request recorded");
        Assert.Equal(1, PendingJoinCount(server));

        // Owner A triggers the rebalance → the pending request is cleared.
        a.SendRandomizeTeams();
        int cleared = PumpUntil(server, () => PendingJoinCount(server) == 0, 2000);
        Assert.True(cleared < 2000, "rebalance did not clear the pending join request");
        Assert.Equal(0, PendingJoinCount(server));
    }

    // ── dispatch gates ───────────────────────────────────────────────────

    /// <summary>⭐ Only the mission owner may trigger a rebalance: a non-owner
    /// sending RANDOMIZE_TEAMS is refused (sides unchanged); the owner's send
    /// afterwards is honoured (FedSrv.CPP:6965-6967,
    /// <c>pfsMission->GetOwner() != pfsPlayer</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Randomize_teams_refused_when_not_mission_owner()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot();
        using var c = new FltBot();
        short aId = Seat(server, a, "OA"); short bId = Seat(server, b, "OB"); short cId = Seat(server, c, "OC");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A = owner
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B = leader, NOT owner
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));   // C non-leader
        PlayerScoreOf(server, aId).Rank = 5;
        PlayerScoreOf(server, bId).Rank = 1;
        PlayerScoreOf(server, cId).Rank = 10;
        Assert.Equal(15, RankSum(server, side0));
        Assert.Equal(1, RankSum(server, side1));
        // P864b — the non-leader C must be READY to be re-seated by the owner's
        // later (honoured) rebalance. Mark all (leaders harmless) and pump until
        // the side-ready aggregate confirms the ready datagrams were applied.
        a.SendPlayerReady(true, aId); b.SendPlayerReady(true, bId); c.SendPlayerReady(true, cId);
        int rd = PumpUntil(server, () => m.Sides.All(s => s.Ready), 2000);
        Assert.True(rd < 2000, "players did not reach side-ready before the rebalance");

        // B (a side leader but not the mission owner) tries → refused, nothing moves.
        b.SendRandomizeTeams();
        PumpUntil(server, () => false, 60);   // give any wrongful action time to occur
        Assert.Equal(15, RankSum(server, side0));
        Assert.Equal(1, RankSum(server, side1));
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(side1, SideOf(server, bId));
        Assert.Equal(side0, SideOf(server, cId));

        // Owner A's send afterwards IS honoured (proves the wire + dispatch path).
        a.SendRandomizeTeams();
        int ok = PumpUntil(server, () => RankSum(server, side0) != 15, 2000);
        Assert.True(ok < 2000, "owner rebalance after the refusal did not run");
    }

    /// <summary>⭐ RANDOMIZE_TEAMS is refused once the mission is not
    /// STAGE_NOTSTARTED (FedSrv.CPP:6968-6970), matching the other lobby-era
    /// owner triggers.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Randomize_teams_refused_when_mission_started()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var c = new FltBot();
        short aId = Seat(server, a, "SA"); short cId = Seat(server, c, "SC");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A owner+leader
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side1));
        PlayerScoreOf(server, aId).Rank = 5;
        PlayerScoreOf(server, cId).Rank = 10;
        Assert.Equal(5, RankSum(server, side0));
        Assert.Equal(10, RankSum(server, side1));

        m.Stage = MissionStage.Started;   // no longer NotStarted
        a.SendRandomizeTeams();
        PumpUntil(server, () => false, 60);   // give any wrongful action time to occur
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(side1, SideOf(server, cId));
        Assert.Equal(5, RankSum(server, side0));
        Assert.Equal(10, RankSum(server, side1));
    }

    /// <summary>⭐ The LockSides-bypass decision: with <c>Mission.LockSides</c>
    /// true, the owner's RANDOMIZE_TEAMS still proceeds. Native's
    /// RandomizeSides never consults bLockSides (SetLockSides is even commented
    /// out there, fsmission.cpp:5258-5259), and this is a privileged
    /// owner-only mass reassignment, not the ordinary player-initiated
    /// side-switch the lock guards.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Randomize_teams_bypasses_lock_sides()
    {
        Require.Artwork();
        using var server = new Srv();
        SetUnseatedSpawner(server);
        server.Boot(Config());

        using var a = new FltBot(); using var b = new FltBot();
        using var c = new FltBot(); using var d = new FltBot(); using var e = new FltBot();
        short aId = Seat(server, a, "XA"); short bId = Seat(server, b, "XB");
        short cId = Seat(server, c, "XC"); short dId = Seat(server, d, "XD"); short eId = Seat(server, e, "XE");

        var m = Mission0(server);
        short side0 = m.Sides[0].Id, side1 = m.Sides[1].Id;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A owner+leader side0
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B leader side1
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, dId), side0));
        Assert.True(server.TryJoinSide(ShipOf(server, eId), side1));
        PlayerScoreOf(server, aId).Rank = 5;  PlayerScoreOf(server, bId).Rank = 1;
        PlayerScoreOf(server, cId).Rank = 10; PlayerScoreOf(server, dId).Rank = 9; PlayerScoreOf(server, eId).Rank = 8;
        // P864b — non-leaders must be READY to be re-seated after the flush;
        // mark all and pump until the side-ready aggregate confirms application.
        a.SendPlayerReady(true, aId); b.SendPlayerReady(true, bId);
        c.SendPlayerReady(true, cId); d.SendPlayerReady(true, dId); e.SendPlayerReady(true, eId);
        int rd = PumpUntil(server, () => m.Sides.All(s => s.Ready), 2000);
        Assert.True(rd < 2000, "players did not reach side-ready before the rebalance");
        Assert.Equal(24, RankSum(server, side0));
        Assert.Equal(9, RankSum(server, side1));

        m.LockSides = true;               // sides locked
        Assert.True(m.LockSides);
        a.SendRandomizeTeams();
        // Locked, yet the rebalance proceeds to the exact balanced 14/19 split.
        int done = PumpUntil(server,
            () => RankSum(server, side0) == 14 && RankSum(server, side1) == 19, 2000);
        Assert.True(done < 2000, "owner RANDOMIZE_TEAMS must bypass LockSides and rebalance");
        Assert.Equal(side0, SideOf(server, aId));
        Assert.Equal(side1, SideOf(server, bId));
        Assert.Equal(aId, m.Sides.First(s => s.Id == side0).LeaderShipId);
        Assert.Equal(bId, m.Sides.First(s => s.Id == side1).LeaderShipId);
    }

    /// <summary>⭐ Multi-mission scoping: a rebalance in mission A never
    /// touches mission B's sides — same-class proof as the P180/P191
    /// per-mission broadcast tests. A rebalances to 14/19; mission B's leader
    /// and non-leader stay exactly where they were.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mission_rebalance_scopes_per_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            // seq: A0,A1,C,D -> mission A (0/1); LB,CB -> mission B (4/5).
            var m = seq < 4 ? server.Missions[0] : server.Missions[1];
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 710f, 905f), Vector3.Zero, Orientation.Identity);
            ship.SideId = null;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot(); using var a1 = new FltBot();
        using var cd = new FltBot(); using var ce = new FltBot();
        using var lb = new FltBot(); using var cb = new FltBot();
        short a0Id = Seat(server, a0, "MA0"); short a1Id = Seat(server, a1, "MA1");
        short cdId = Seat(server, cd, "MCD"); short ceId = Seat(server, ce, "MCE");
        short lbId = Seat(server, lb, "MLB"); short cbId = Seat(server, cb, "MCB");

        var mA = server.Missions[0]; var mB = server.Missions[1];
        short aS0 = mA.Sides[0].Id, aS1 = mA.Sides[1].Id;
        short bS0 = mB.Sides[0].Id, bS1 = mB.Sides[1].Id;

        // Mission A: A0 owner/leader of side0, A1 leader of side1, CD/CE non-leaders.
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), aS0));
        Assert.True(server.TryJoinSide(ShipOf(server, a1Id), aS1));
        Assert.True(server.TryJoinSide(ShipOf(server, cdId), aS0));
        Assert.True(server.TryJoinSide(ShipOf(server, ceId), aS1));
        // Mission B: LB leader of side0, CB non-leader on side1.
        Assert.True(server.TryJoinSide(ShipOf(server, lbId), bS0));
        Assert.True(server.TryJoinSide(ShipOf(server, cbId), bS1));

        PlayerScoreOf(server, a0Id).Rank = 5;  PlayerScoreOf(server, a1Id).Rank = 1;
        PlayerScoreOf(server, cdId).Rank = 10; PlayerScoreOf(server, ceId).Rank = 8;
        PlayerScoreOf(server, lbId).Rank = 3;  PlayerScoreOf(server, cbId).Rank = 976;
        // P864b — mission A's non-leaders must be READY to be re-seated; mark
        // mission A's players ready and pump until A's side-ready confirms it.
        a0.SendPlayerReady(true, a0Id); a1.SendPlayerReady(true, a1Id);
        cd.SendPlayerReady(true, cdId); ce.SendPlayerReady(true, ceId);
        int rdA = PumpUntil(server, () => mA.Sides.All(s => s.Ready), 2000);
        Assert.True(rdA < 2000, "mission A did not reach side-ready before its rebalance");

        // Mission B baseline (must stay exactly this after A's rebalance).
        int bBefore = MissionRankSum(server, mB);

        // Owner of mission A triggers a rebalance in A.
        a0.SendRandomizeTeams();
        int settled = PumpUntil(server,
            () => ShipsOnSide(server, mA, aS1).Contains(cdId), 2000);

        // Mission A rebalanced: CD(10) went to A's lowest side (aS1, sum 1).
        Assert.True(settled < 2000, "mission A rebalance did not seat CD on aS1");
        Assert.Contains(cdId, ShipsOnSide(server, mA, aS1));
        Assert.Equal(a0Id, mA.Sides.First(s => s.Id == aS0).LeaderShipId);
        Assert.Equal(a1Id, mA.Sides.First(s => s.Id == aS1).LeaderShipId);

        // Mission B completely untouched: leader on bS0, non-leader on bS1,
        // total rank sum unchanged.
        Assert.Equal(lbId, mB.Sides.First(s => s.Id == bS0).LeaderShipId);
        Assert.Equal(bS0, SideOf(server, lbId));
        Assert.Equal(bS1, SideOf(server, cbId));
        Assert.Equal(bBefore, MissionRankSum(server, mB));
        Assert.Equal(979, MissionRankSum(server, mB));   // 3 + 976, both floors≥1
    }

    /// <summary>The total rank sum of every seated player in <paramref name="m"/>
    /// (used to prove a mission's sides are untouched by another mission's
    /// rebalance).</summary>
    private static int MissionRankSum(Srv server, Mission m)
    {
        int s = 0;
        foreach (var live in server.Ships)
            if (ReferenceEquals(live.Ship.Mission, m))
                s += Math.Max(live.PlayerScore.Rank, (short)1);
        return s;
    }
}
