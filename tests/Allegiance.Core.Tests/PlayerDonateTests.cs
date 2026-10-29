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
/// P203 — <c>PLAYER_DONATE(67)</c>: the manual, direct player-to-player gift
/// (port of native <c>FedSrv.CPP:7213-7262</c>). This is distinct from
/// <c>AUTODONATE(112)</c>'s standing relationship: it is a one-shot gift to any
/// same-side or allied-side (P190 <c>AllyGroup</c>) ship, spending from the
/// P197 per-player wallet. A real transfer debits the donor, credits the
/// recipient, and broadcasts <c>MONEY_CHANGE(60)</c> to the sender's side group
/// always (plus the recipient's side group when the gift crossed sides); any
/// refusal bounces back a no-op <c>MONEY_CHANGE</c> (sidFrom = NA) to the sender
/// alone with no wallet movement.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class PlayerDonateTests
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
            new Gm { Name = "Donate", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    // A 4-team mission — required so P190's alliance can form without native's
    // "clear ALL when one group swallows every side" rule dissolving it
    // (fsmission.cpp:5786-5792 needs a solo side left over).
    private static Cfg ConfigFourTeams() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "DonateAlly", Map = "XXV_simple.igc", Teams = 4, CivIds = [1, 3, 2, 1], SideNames = ["Alpha", "Bravo", "Charlie", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 913u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9100000000000000000ul, DrmHash = "pd",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Flush(Srv server) => PumpUntil(server, () => false, 40);

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

    private static Side Side(Srv server, Mission m, short sideId) =>
        m.Sides.First(s => s.Id == sideId);

    private static global::Allegiance.Server.PlayerWallet WalletOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet;

    private static void LeadEmptySide(Srv server, FltBot bot, short shipId, short sideId)
        => Assert.True(server.TryJoinSide(ShipOf(server, shipId), sideId));

    /// <summary>Seat A as side0's leader+owner and B as a non-leader on the
    /// same side, all while the mission is STAGE_STARTED (the donate gate).</summary>
    private static (short aId, short bId, short side0) SeatLeaderAndMateStarted(Srv server, FltBot a, FltBot b, string na, string nb)
    {
        short aId = Seat(server, a, na);
        short side0 = server.Missions[0].Sides[0].Id;
        server.Missions[0].Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A first on empty side0 → leader
        short bId = Seat(server, b, nb);
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side0));   // B onto occupied side0 → non-leader
        Flush(server);
        Assert.Equal(aId, Side(server, server.Missions[0], side0).LeaderShipId);
        return (aId, bId, side0);
    }

    // ── wire codec bytes ───────────────────────────────────────────────────

    private static ushort ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static short ReadI16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));
    private static int ReadI32(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4));

    /// <summary><c>FM_C_PLAYER_DONATE(67)</c>: header(4) + moneyGiven int32@4 +
    /// shipID short@8 + 2 pad@10 = 12 B (default MSVC packing — same shape as
    /// SET_MONEY; PLAYER_DONATE sits outside Messages.h's pack(1) regions).</summary>
    [Fact]
    public void PlayerDonate_client_codec_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)67, PlayerDonate.Fmid);
        Assert.Equal(12, PlayerDonate.FixedSize);
        var m = new PlayerDonate { MoneyGiven = 900001, ShipID = 513 }.Encode();
        Assert.Equal(12, m.Length);
        Assert.Equal(12, ReadU16(m, 0));   // cbmsg
        Assert.Equal(67, ReadU16(m, 2));   // fmid
        Assert.Equal(900001, ReadI32(m, 4));  // moneyGiven @4
        Assert.Equal(513, ReadI16(m, 8));  // shipID @8
        Assert.Equal(0, ReadI16(m, 10));   // pad @10-11
        var d = PlayerDonate.Decode(m);
        Assert.Equal(900001, d.MoneyGiven);
        Assert.Equal((short)513, d.ShipID);
    }

    // ── same-side happy path ───────────────────────────────────────────────

    /// <summary>⭐ A same-side donation with sufficient funds transfers the money
    /// (donor debited, recipient credited) and broadcasts MONEY_CHANGE to the
    /// donor's side group (both peers on that side observe the same
    /// sidTo=recipient / sidFrom=donor / dMoney frame).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_side_donation_transfers_and_broadcasts_to_side()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, side0) = SeatLeaderAndMateStarted(server, a, b, "PD10", "PD11");

        WalletOf(server, aId).Money = 100;   // donor
        WalletOf(server, bId).Money = 0;     // recipient
        Flush(server);
        a.ResetMoneyChanges();
        b.ResetMoneyChanges();

        a.SendPlayerDonate(bId, 40);
        int got = PumpUntil(server,
            () => WalletOf(server, aId).Money == 60 && WalletOf(server, bId).Money == 40
               && a.MoneyChanges.Any(mc => mc.SidTo == bId && mc.SidFrom == aId && mc.DMoney == 40)
               && b.MoneyChanges.Any(mc => mc.SidTo == bId && mc.SidFrom == aId && mc.DMoney == 40),
            800);
        Assert.True(got < 800, $"same-side gift did not land: A={WalletOf(server, aId).Money}, B={WalletOf(server, bId).Money}");

        // The donor's side group broadcast reached both peers on side0.
        Assert.Contains(a.MoneyChanges, mc => mc.SidTo == bId && mc.SidFrom == aId && mc.DMoney == 40);
        Assert.Contains(b.MoneyChanges, mc => mc.SidTo == bId && mc.SidFrom == aId && mc.DMoney == 40);
    }

    // ── allied cross-side ──────────────────────────────────────────────────

    /// <summary>⭐ An allied cross-side donation (sides 0 and 1 in one P190
    /// alliance group) transfers the money AND broadcasts MONEY_CHANGE to BOTH
    /// sides' peer groups: the sender's side sees it and the recipient's side
    /// sees it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_cross_side_donation_transfers_and_broadcasts_both_sides()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(ConfigFourTeams());
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PD20");
        short bId = Seat(server, b, "PD21");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        LeadEmptySide(server, a, aId, side0);   // A: side0 leader + mission owner
        LeadEmptySide(server, b, bId, side1);   // B: side1 leader
        Flush(server);

        // Form the alliance while the mission is still NotStarted (native
        // CHANGE_ALLIANCE is NotStarted-only, FedSrv.CPP:7474).
        a.SendChangeAlliance(side0, side1);
        int formed = PumpUntil(server,
            () => Side(server, m, side0).AllyGroup == 0 && Side(server, m, side1).AllyGroup == 0, 800);
        Assert.True(formed < 800, "P190 alliance side0+side1 did not form");
        Assert.Equal((sbyte)0, Side(server, m, side0).AllyGroup);
        Assert.Equal((sbyte)0, Side(server, m, side1).AllyGroup);

        // PLAYER_DONATE requires STAGE_STARTED.
        m.Stage = MissionStage.Started;
        Flush(server);

        WalletOf(server, aId).Money = 100;   // donor on side0
        WalletOf(server, bId).Money = 0;     // recipient on allied side1
        Flush(server);
        a.ResetMoneyChanges();
        b.ResetMoneyChanges();

        a.SendPlayerDonate(bId, 40);
        int got = PumpUntil(server,
            () => WalletOf(server, aId).Money == 60 && WalletOf(server, bId).Money == 40
               && a.MoneyChanges.Any(mc => mc.SidTo == bId && mc.SidFrom == aId && mc.DMoney == 40)
               && b.MoneyChanges.Any(mc => mc.SidTo == bId && mc.SidFrom == aId && mc.DMoney == 40),
            800);
        Assert.True(got < 800, $"allied cross-side gift did not land: A={WalletOf(server, aId).Money}, B={WalletOf(server, bId).Money}");

        // BOTH side groups got the frame — the sender's own side (a) and the
        // recipient's side (b).
        Assert.Contains(a.MoneyChanges, mc => mc.SidTo == bId && mc.SidFrom == aId && mc.DMoney == 40);
        Assert.Contains(b.MoneyChanges, mc => mc.SidTo == bId && mc.SidFrom == aId && mc.DMoney == 40);
    }

    // ── refusals / bounce-backs ────────────────────────────────────────────

    /// <summary>⭐ A donation to a non-allied, different-side ship is refused as
    /// a no-op bounce-back: no wallet changes, and a MONEY_CHANGE echo (sidFrom
    /// = NA) reaches ONLY the sender — the would-be recipient sees nothing.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_allied_different_side_donation_bounces_back_no_op()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());   // 2 teams, no alliance
        using var a = new FltBot();
        using var b = new FltBot();
        short aId = Seat(server, a, "PD30");
        short bId = Seat(server, b, "PD31");

        Mission m = server.Missions[0];
        short side0 = m.Sides[0].Id;
        short side1 = m.Sides[1].Id;
        m.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A on side0
        Assert.True(server.TryJoinSide(ShipOf(server, bId), side1));   // B on side1 (DIFFERENT, not allied)
        Flush(server);
        Assert.Null(Side(server, m, side0).AllyGroup);
        Assert.Null(Side(server, m, side1).AllyGroup);

        WalletOf(server, aId).Money = 100;
        WalletOf(server, bId).Money = 0;
        Flush(server);
        a.ResetMoneyChanges();
        b.ResetMoneyChanges();

        a.SendPlayerDonate(bId, 40);
        int got = PumpUntil(server,
            () => a.MoneyChanges.Any(mc => mc.SidTo == aId && mc.SidFrom == -1 && mc.DMoney == 40), 800);
        Assert.True(got < 800, "non-allied donation did not bounce back an echo to the sender");

        // No wallet movement (no-op), echo only to the sender with sidFrom=NA.
        Assert.Equal(100, WalletOf(server, aId).Money);
        Assert.Equal(0, WalletOf(server, bId).Money);
        Assert.Contains(a.MoneyChanges, mc => mc.SidTo == aId && mc.SidFrom == -1 && mc.DMoney == 40);
        Assert.Empty(b.MoneyChanges);   // the intended recipient never heard about it
    }

    /// <summary>⭐ A donation exceeding the sender's own wallet balance is
    /// refused (log-and-ignore — the initial hard gate): no forward at all, no
    /// echo, no wallet movement (native TrapHackBoot, FedSrv.CPP:7221).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Over_wallet_donation_refused_no_forward()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, bId, _) = SeatLeaderAndMateStarted(server, a, b, "PD40", "PD41");

        WalletOf(server, aId).Money = 10;   // less than the 40 requested
        WalletOf(server, bId).Money = 0;
        Flush(server);
        a.ResetMoneyChanges();
        b.ResetMoneyChanges();

        a.SendPlayerDonate(bId, 40);
        Flush(server);

        // Refused outright — neither an echo nor a transfer nor a broadcast.
        Assert.Equal(10, WalletOf(server, aId).Money);
        Assert.Equal(0, WalletOf(server, bId).Money);
        Assert.Empty(a.MoneyChanges);
        Assert.Empty(b.MoneyChanges);
    }

    /// <summary>⭐ A donation to a nonexistent ship id is a no-op bounce-back to
    /// the sender alone (native FedSrv.CPP:7223 + 7247-7256): no wallet change,
    /// a MONEY_CHANGE echo with sidFrom=NA and sidTo = the sender's own ship.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Nonexistent_target_bounces_back_to_sender_alone()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        short aId = Seat(server, a, "PD50");
        short side0 = server.Missions[0].Sides[0].Id;
        server.Missions[0].Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));
        Flush(server);

        WalletOf(server, aId).Money = 100;
        Flush(server);
        a.ResetMoneyChanges();

        a.SendPlayerDonate(9999, 40);   // no such ship
        int got = PumpUntil(server,
            () => a.MoneyChanges.Any(mc => mc.SidTo == aId && mc.SidFrom == -1 && mc.DMoney == 40), 800);
        Assert.True(got < 800, "nonexistent target did not bounce back an echo to the sender");

        Assert.Equal(100, WalletOf(server, aId).Money);   // no movement
        Assert.Contains(a.MoneyChanges, mc => mc.SidTo == aId && mc.SidFrom == -1 && mc.DMoney == 40);
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
            new Gm { Name = "Donate A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Donate B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    /// <summary>Route peer <c>seq</c> to a designated mission/side: peer 0 →
    /// mission A side 0; peer 1 → mission B side 0 (SAME numeric side id);
    /// peer 2 → mission A side 0 (the same-side recipient for the donor).</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 1 ? server.Missions[1] : server.Missions[0];
            short sideIdx = 0;   // every spawn on side 0 of its mission
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 780f, 882f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }

    /// <summary>⭐ Multi-mission scoping (the P178/P180/P184/P190 must-not-regress
    /// bug class): a PLAYER_DONATE's MONEY_CHANGE broadcast on mission A must
    /// never reach a peer on mission B sharing the same numeric side id.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mission_donation_broadcast_scopes_per_mission()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0 (donor)
        using var b0 = new FltBot();   // mission B, side 0 (SAME numeric side id)
        using var a2 = new FltBot();   // mission A, side 0 (same-side recipient)
        short a0Id = Seat(server, a0, "PD60");
        short b0Id = Seat(server, b0, "PD61");
        short a2Id = Seat(server, a2, "PD62");

        Mission mA = server.Missions[0];
        Mission mB = server.Missions[1];
        short mA0 = mA.Sides[0].Id;
        short mB0 = mB.Sides[0].Id;
        Assert.Equal(mA0, mB0);   // same numeric side id across missions

        mA.Stage = MissionStage.Started;
        mB.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), mA0));
        Assert.True(server.TryJoinSide(ShipOf(server, a2Id), mA0));
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), mB0));
        Flush(server);

        WalletOf(server, a0Id).Money = 100;
        Flush(server);
        a0.ResetMoneyChanges();
        b0.ResetMoneyChanges();
        a2.ResetMoneyChanges();

        // Same-side gift on mission A (a0 → a2), broadcasting to mission A side 0.
        a0.SendPlayerDonate(a2Id, 40);
        int got = PumpUntil(server,
            () => WalletOf(server, a0Id).Money == 60 && WalletOf(server, a2Id).Money == 40
               && a0.MoneyChanges.Any(mc => mc.SidTo == a2Id && mc.SidFrom == a0Id && mc.DMoney == 40)
               && a2.MoneyChanges.Any(mc => mc.SidTo == a2Id && mc.SidFrom == a0Id && mc.DMoney == 40),
            800);
        Assert.True(got < 800, "mission-A donation did not transfer and broadcast to mission-A side0 peers");

        Assert.Contains(a0.MoneyChanges, mc => mc.SidTo == a2Id && mc.SidFrom == a0Id && mc.DMoney == 40);
        Assert.Contains(a2.MoneyChanges, mc => mc.SidTo == a2Id && mc.SidFrom == a0Id && mc.DMoney == 40);

        // b0 sits on mission B side0 (numeric id == mA0) and must see NOTHING of
        // mission A's donation broadcast — the scoping holds across the shared id.
        Assert.Empty(b0.MoneyChanges);
    }
}
