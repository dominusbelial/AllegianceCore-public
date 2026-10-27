using System.Reflection;
using Allegiance.Core.Collision;
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
/// P197 — the per-player money &amp; auto-donate foundation: the per-connection
/// wallet (<see cref="Allegiance.Server.PlayerWallet"/> on <c>LiveShip</c>),
/// the default donate-to-leader on join, the native <c>CFSPlayer::SetAutoDonate</c>
/// cycle-resolution algorithm (chain redirect / mutual-donation unset / one-time
/// gift), the two-mode <c>AUTODONATE(112)</c> wire dispatch (set-own-target vs
/// the leader-only <c>amount==-1</c> toggle), <c>PAYDAY</c> crediting the resolved
/// donate target instead of the payer, and the <c>MUTINY</c> wallet-gift to the new
/// leader. Cycle-resolution unit cases drive the private
/// <c>Server.SetAutoDonate</c> directly via reflection (so the donation-confidence
/// election — which only fires on the live <c>AUTODONATE(112)</c> dispatch — cannot
/// perturb them); the dispatch/payday/mutiny cases run over real loopback wire.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class PlayerMoneyAutoDonateTests
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
            new Gm { Name = "Money Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 971u, DwCookie = 0x971u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9700000000000000000ul, DrmHash = "money",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

    private static int PumpTo(Srv server, double target, int max)
    {
        int n = 0;
        while (server.SimNow < target && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        Thread.Sleep(60);
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

    private static Side SideOf(Srv server, short sideId) =>
        server.Missions[0].Sides.First(s => s.Id == sideId);

    private static global::Allegiance.Server.PlayerWallet WalletOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet;

    /// <summary>Seat A as side0's leader+owner and B as a non-leader, all while
    /// the mission is STAGE_STARTED — so TryJoinSide's default donate-to-leader
    /// applies: B auto-donates to A, A donates to no one.</summary>
    private static (short aId, short bId, short side0) SeatLeaderAndMateStarted(Srv server, FltBot a, FltBot b, string na, string nb)
    {
        short aId = Seat(server, a, na);
        short side0 = server.Missions[0].Sides[0].Id;
        server.Missions[0].Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A first on empty side0 → leader
        short bId = Seat(server, b, nb);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));   // B onto occupied side0 → non-leader
        Flush(server);
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);
        return (aId, bId, side0);
    }

    // ── reflection bridge to the private Server.SetAutoDonate ──────────────
    // Direct calls exercise the CFSPlayer::SetAutoDonate algorithm in isolation;
    // native runs the donation-confidence election only in the live AUTODONATE(112)
    // dispatch (FedSrv.CPP:5560-5658), NOT inside SetAutoDonate, so bypassing the
    // dispatch here is the faithful way to unit-test the cycle resolution.

    private static MethodInfo SetAutoDonateMethod() =>
        typeof(Srv).GetMethod("SetAutoDonate", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void CallSetAutoDonate(Srv server, global::Allegiance.Server.PlayerWallet donorWallet, Ship? target, int amount, bool bSend)
    {
        var donorLive = server.Ships.First(s => ReferenceEquals(s.Wallet, donorWallet));
        SetAutoDonateMethod().Invoke(server, new object?[] { donorLive, target, amount, bSend });
    }

    // ── wire codec bytes ───────────────────────────────────────────────────

    private static ushort ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static short ReadI16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));
    private static int ReadI32(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4));

    /// <summary><c>FM_C_AUTODONATE(112)</c>: header(4) + sidDonateTo short@4 + 2 pad@6 +
    /// amount int32@8 = 12 B (default MSVC packing, Messages.h:738-741).</summary>
    [Fact]
    public void AutoDonate_client_codec_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)112, AutoDonate.Fmid);
        Assert.Equal(12, AutoDonate.FixedSize);
        var m = new AutoDonate { SidDonateTo = 507, Amount = -1 }.Encode();
        Assert.Equal(12, m.Length);
        Assert.Equal(12, ReadU16(m, 0));   // cbmsg
        Assert.Equal(112, ReadU16(m, 2));  // fmid
        Assert.Equal(507, ReadI16(m, 4));  // sidDonateTo @4
        Assert.Equal(0, ReadI16(m, 6));    // pad @6-7
        Assert.Equal(-1, ReadI32(m, 8));   // amount @8
        var d = AutoDonate.Decode(m);
        Assert.Equal((short)507, d.SidDonateTo);
        Assert.Equal(-1, d.Amount);
    }

    /// <summary><c>FM_S_AUTODONATE(116)</c>: header(4) + sidDonateBy short@4 + sidDonateTo
    /// short@6 + amount int32@8 = 12 B, no pad (Messages.h:756-759).</summary>
    [Fact]
    public void AutoDonate_server_codec_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)116, AutoDonateUpdate.Fmid);
        Assert.Equal(12, AutoDonateUpdate.FixedSize);
        var m = new AutoDonateUpdate { SidDonateBy = 511, SidDonateTo = -1, Amount = 640 }.Encode();
        Assert.Equal(12, m.Length);
        Assert.Equal(116, ReadU16(m, 2));  // fmid
        Assert.Equal(511, ReadI16(m, 4));  // sidDonateBy @4
        Assert.Equal(-1, ReadI16(m, 6));   // sidDonateTo @6 (NA)
        Assert.Equal(640, ReadI32(m, 8));  // amount @8
        var d = AutoDonateUpdate.Decode(m);
        Assert.Equal((short)511, d.SidDonateBy);
        Assert.Equal((short)-1, d.SidDonateTo);
        Assert.Equal(640, d.Amount);
    }

    // ── join default ───────────────────────────────────────────────────────

    /// <summary>⭐ A newly-joined non-leader defaults to auto-donating to the
    /// side's current leader; the leader themselves has no target (native
    /// AddPlayerToSide, fsmission.cpp:720-723).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Join_defaults_non_leader_to_donate_to_leader_leader_has_none()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMateStarted(server, a, b, "MJ1", "MJ2");

        Assert.Null(WalletOf(server, aId).AutoDonateTarget);                       // leader donates to no one
        Assert.Equal(ShipOf(server, aId), WalletOf(server, bId).AutoDonateTarget); // B → A
    }

    // ── SetAutoDonate cycle resolution (direct, no election tail) ──────────

    /// <summary>⭐ Chain redirect: A tries to donate to B, but B already donates
    /// to C ⇒ A ends up donating to C, not B (fsship.cpp:1322-1326).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Chain_redirect_a_to_b_where_b_donates_to_c_yields_a_to_c()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        short aId = Seat(server, a, "MC1");
        short side0 = server.Missions[0].Sides[0].Id;
        server.Missions[0].Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A leader
        short bId = Seat(server, b, "MC2");
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));
        short cId = Seat(server, c, "MC3");
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));
        Flush(server);

        // By default B and C both donate to A (leader). To make B a genuine
        // "donates to C" node, clear C's target, then point B at C.
        CallSetAutoDonate(server, WalletOf(server, cId), null, 0, bSend: false);
        CallSetAutoDonate(server, WalletOf(server, bId), ShipOf(server, cId), 0, bSend: false);

        // A tries to donate to B → chain-resolves to C.
        CallSetAutoDonate(server, WalletOf(server, aId), ShipOf(server, bId), 0, bSend: false);
        Assert.Equal(ShipOf(server, cId), WalletOf(server, aId).AutoDonateTarget);  // A → C, not B
        Assert.Equal(ShipOf(server, cId), WalletOf(server, bId).AutoDonateTarget);  // B unchanged
    }

    /// <summary>⭐ Mutual donation: A tries to donate to B, but B is already
    /// donating to A ⇒ B's donation to A is cleared first, then A donates to B
    /// (fsship.cpp:1313-1321).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mutual_donation_clears_target_first_then_donates()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMateStarted(server, a, b, "MU1", "MU2");

        // B already donates to A (join default). A tries to donate to B.
        CallSetAutoDonate(server, WalletOf(server, aId), ShipOf(server, bId), 0, bSend: false);

        Assert.Null(WalletOf(server, bId).AutoDonateTarget);                        // B's donation to A cleared first
        Assert.Equal(ShipOf(server, bId), WalletOf(server, aId).AutoDonateTarget);  // then A → B
    }

    /// <summary>⭐ A one-time gift transfers immediately and updates both wallets
    /// (fsship.cpp:1336-1338), driven over the real wire through the
    /// <c>AUTODONATE(112)</c> dispatch.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Autodonate_gift_transfers_wallets_over_wire()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMateStarted(server, a, b, "MG1", "MG2");

        WalletOf(server, bId).Money = 200;   // give B a balance to spend
        b.ResetAutoDonateUpdates();
        b.ResetSetMoneys();

        // B sends AUTODONATE(112) targeting the leader A with a 50 gift.
        b.SendAutoDonate(aId, 50);
        int got = PumpUntil(server, () =>
            WalletOf(server, aId).Money == 50 && WalletOf(server, bId).Money == 150
            && b.AutoDonateUpdates.Any(u => u.SidDonateBy == bId && u.SidDonateTo == aId && u.Amount == 50), 800);
        Assert.True(got < 800, $"gift did not land: A={WalletOf(server, aId).Money}, B={WalletOf(server, bId).Money}");

        // The relationship is re-announced to the side (AUTODONATE(116), sidDonateBy=B).
        Assert.Contains(b.AutoDonateUpdates, u => u.SidDonateBy == bId && u.SidDonateTo == aId && u.Amount == 50);
    }

    // ── leader-only toggle (wire) ──────────────────────────────────────────

    /// <summary>⭐ A leader-only <c>AUTODONATE(amount=-1)</c> toggle from a
    /// non-leader is refused; from the leader it toggles the named subordinate's
    /// donation on/off (FedSrv.CPP:5531-5558).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Leader_only_toggle_refused_for_non_leader_and_toggles_for_leader()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMateStarted(server, a, b, "MT1", "MT2");

        // Non-leader B attempts the amount=-1 toggle → refused, B's target unchanged.
        b.SendAutoDonate(aId, -1);
        int settled = PumpUntil(server, () => false, 40);
        Assert.True(settled < 999);
        Assert.Equal(ShipOf(server, aId), WalletOf(server, bId).AutoDonateTarget);   // still → A

        // Leader A toggles B OFF (B currently donates to A) → B's target cleared.
        a.SendAutoDonate(bId, -1);
        int off = PumpUntil(server, () => WalletOf(server, bId).AutoDonateTarget is null, 800);
        Assert.True(off < 800, "leader toggle-off did not clear the subordinate's donation");

        // Leader A toggles B ON → B now donates to A.
        a.SendAutoDonate(bId, -1);
        int on = PumpUntil(server, () => ReferenceEquals(WalletOf(server, bId).AutoDonateTarget, ShipOf(server, aId)), 800);
        Assert.True(on < 800, "leader toggle-on did not re-point the subordinate at the leader");
    }

    // ── PAYDAY credits the resolved donate target ──────────────────────────

    /// <summary>⭐ ⭐ <c>PAYDAY</c> now actually credits each player's resolved
    /// donate-target's wallet — not the paying player's own — a real
    /// must-fail-if-removed proof distinct from the P51 wire-cadence tests (which
    /// only checked the broadcast). B's share lands in A's wallet because B
    /// auto-donates to A (native DoPayday, fsmission.cpp:2310-2324).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Payday_credits_resolved_donate_target_not_the_payer()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMateStarted(server, a, b, "MP1", "MP2");
        var mission = server.Missions[0];

        // Both start broke; drive one full payday boundary (60s cadence).
        Assert.Equal(0, WalletOf(server, aId).Money);
        Assert.Equal(0, WalletOf(server, bId).Money);
        int pumped = PumpTo(server, 65.0, 3000);
        Assert.True(pumped < 3000, "did not reach the first payday boundary");

        long income = (long)(mission.IncomePerPayday * (1.0f + mission.Sides.Single(s => s.Id == side0).Civilization.IncomeMoney));
        int delta = (int)(income / 2);   // two players on the side

        // B donated his whole share to A ⇒ A pockets both shares, B pockets nothing.
        Assert.Equal(2 * delta, WalletOf(server, aId).Money);
        Assert.Equal(0, WalletOf(server, bId).Money);
    }

    // ── mutiny wallet gift (reuse the P187 harness) ────────────────────────


    /// <summary>P842 review (Claude): a donation-confidence leadership change runs
    /// native <c>CFSMission::SetLeader</c> (FedSrv.CPP:5607 → fsmission.cpp:1533-1553):
    /// the new leader goes to command wing 0 and the side hears SET_WINGID(wing 0,
    /// commanded) before SET_TEAM_LEADER.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Donation_confidence_leader_change_puts_the_new_leader_on_the_command_wing()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        using var d = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMateStarted(server, a, b, "DC1", "DC2");
        short cId = Seat(server, c, "DC3");
        Assert.True(server.TryJoinSide(ShipOf(server, cId), side0));
        short dId = Seat(server, d, "DC4");
        Assert.True(server.TryJoinSide(ShipOf(server, dId), side0));
        Flush(server);
        ShipOf(server, bId).WingId = 2;
        // The leader's toggle stops B donating (FedSrv.CPP:5548-5556), so donations
        // to B are not chain-redirected to A.
        a.SendAutoDonate(bId, -1);
        Assert.True(PumpUntil(server, () => WalletOf(server, bId).AutoDonateTarget is null, 400) < 400);
        a.ResetSetWingIdsReceived();

        c.SendAutoDonate(bId, 0);
        Flush(server);
        d.SendAutoDonate(bId, 0);   // now B has 2 donors, A only 1 → leadership passes to B
        int got = PumpUntil(server, () => SideOf(server, side0).LeaderShipId == bId
            && a.SetWingIds.Any(w => w.ShipID == bId && w.WingId == 0 && w.BCommanded), 800);
        Assert.True(got < 800, $"leader={SideOf(server, side0).LeaderShipId}, wing msgs={a.SetWingIds.Count}");
        Assert.Equal(0, ShipOf(server, bId).WingId);
    }

    /// <summary>⭐ A passing mutiny transfers the deposed leader's entire wallet
    /// to the new leader as a one-time gift and re-points the deposed leader's
    /// donations at the new leader, sending a compensating MONEY_CHANGE to the
    /// deposed leader alone (native MutinyBallot::OnPassed, fsmission.cpp:
    /// 6121-6136). NOTE (verified vs live source): native does NOT re-point every
    /// other donor — only the deposed leader is redirected; see PLAYER_MONEY_AUTODONATE_P197.md.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Mutiny_transfers_old_leader_wallet_to_new_leader_and_redirects_him()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMateStarted(server, a, b, "MX1", "MX2");

        WalletOf(server, aId).Money = 500;   // the deposed leader's personal nest egg
        Flush(server);
        a.ResetBallotSignals();
        a.ResetMoneyChanges();
        b.ResetBallotSignals();

        b.SendChat("#mutiny");
        int got = PumpUntil(server, () => b.Ballots.Count >= 1, 800);
        Assert.True(got < 800, "mutiny ballot not created");
        short ballotId = b.Ballots[0].BallotId;
        a.SendVote(ballotId, true);
        int transferred = PumpUntil(server,
            () => SideOf(server, side0).LeaderShipId == bId
               && WalletOf(server, bId).Money == 500
               && WalletOf(server, aId).Money == 0
               && a.MoneyChanges.Any(mc => mc.SidTo == aId && mc.DMoney == -500),
            800);
        Assert.True(transferred < 800, "mutiny did not transfer leadership + the wallet gift");

        // The deposed leader A now donates to B; the new leader B donates to no one.
        Assert.Equal(ShipOf(server, bId), WalletOf(server, aId).AutoDonateTarget);
        Assert.Null(WalletOf(server, bId).AutoDonateTarget);

        // A (alone) sees the compensating MONEY_CHANGE for his emptied wallet.
        Assert.Contains(a.MoneyChanges, mc => mc.SidTo == aId && mc.DMoney == -500);
    }

    // ── multi-mission scoping ──────────────────────────────────────────────

    private static Cfg ConfigTwoGames() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Money A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Money B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    /// <summary>Route peer <c>seq</c> to a designated mission (same pattern as
    /// P184/P186/P190): peer 0 → mission 0 / side 0; peer 1 → mission 1 / side 0
    /// (SAME numeric side id); peer 2 → mission 0 / side 1.</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq == 1 ? (short)0 : (short)(seq == 0 ? 0 : 1);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 780f, 882f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }

    /// <summary>⭐ Multi-mission scoping (the P178/P180/P184 must-not-regress bug
    /// class): an <c>AUTODONATE(116)</c> broadcast raised by a donation on mission
    /// A must never reach a peer on mission B, even when both missions share the
    /// same numeric side id (the AutoDonate broadcast is side-scoped WITHIN a
    /// mission — the P197 BroadcastAutoDonate filter is mission+side, never an
    /// unscoped sweep).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mission_autodonate_broadcast_scopes_per_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0
        using var b0 = new FltBot();   // mission B, side 0 (SAME numeric side id)
        using var a2 = new FltBot();   // mission A, side 1
        short a0Id = Seat(server, a0, "MW0");
        short b0Id = Seat(server, b0, "MW1");
        short a2Id = Seat(server, a2, "MW2");

        Mission mA = server.Missions[0];
        Mission mB = server.Missions[1];
        short mA0 = mA.Sides[0].Id;
        short mB0 = mB.Sides[0].Id;
        Assert.Equal(mA0, mB0);   // same numeric side id across missions

        // Mission A: a0 leader of side0, a2 joins as non-leader (side0). Mission B:
        // b0 leader of side0. All Started so the join-default donation applies.
        mA.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), mA0));
        Assert.True(server.TryJoinSide(ShipOf(server, a2Id), mA0));
        mB.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), mB0));
        Flush(server);

        a0.ResetAutoDonateUpdates();
        b0.ResetAutoDonateUpdates();
        a2.ResetAutoDonateUpdates();

        // Trigger an AUTODONATE(116) on mission A only: a2 gifts 40 to a0.
        WalletOf(server, a2Id).Money = 100;
        a2.SendAutoDonate(a0Id, 40);
        int got = PumpUntil(server,
            () => a2.AutoDonateUpdates.Any(u => u.SidDonateBy == a2Id && u.SidDonateTo == a0Id)
               && a0.AutoDonateUpdates.Any(u => u.SidDonateBy == a2Id && u.SidDonateTo == a0Id),
            800);
        Assert.True(got < 800, "mission-A AUTODONATE(116) did not reach its mission-A side peers");

        // b0 sits on mission B side0 (numeric id == mA0) and must see NOTHING of
        // mission A's donation broadcast — the scoping holds across the shared id.
        Assert.DoesNotContain(b0.AutoDonateUpdates, u => u.SidDonateBy == a2Id);
        Assert.Empty(b0.AutoDonateUpdates);
    }
}
