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
/// P199 — <c>FM_CS_REQUEST_MONEY(182)</c>: a player who wants to buy something
/// (a specific hull, named in the message) but lacks the funds asks whoever
/// actually holds the team's money to send it. Port of FedSrv.CPP:5361-5406.
/// The handler NEVER transfers money — it only FORWARDS the request (with
/// <c>shipidRequest</c> filled in) to exactly one recipient connection: the
/// sender's own auto-donate target when that player's wallet holds at least the
/// requested amount, otherwise the richest <em>other</em> teammate on the
/// sender's side; silently dropped if no recipient resolves. These cases run
/// over real loopback wire and observe the forward via a Bot accumulator.
/// [Collection("LiveNetwork")] keeps this suite sequential with the others.
/// </summary>
[Collection("LiveNetwork")]
public class RequestMoneyTests
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
        FedsrvVer = LogonRequest.MsgVer, Time = 972u, DwCookie = 0x972u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9730000000000000000ul, DrmHash = "rmoney",
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

    private static Side SideOf(Srv server, short sideId) =>
        server.Missions[0].Sides.First(s => s.Id == sideId);

    private static global::Allegiance.Server.PlayerWallet WalletOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet;

    /// <summary>Seat A as side0's leader+owner and the rest as non-leaders, all
    /// while the mission is STAGE_STARTED — so every non-leader's default
    /// donate-to-leader applies (each defaults to donating to A).</summary>
    private static (short aId, short[] mates, short side0) SeatLeaderAndMates(Srv server, FltBot a, params (FltBot Bot, string Name)[] mates)
    {
        short aId = Seat(server, a, mates.Length == 0 ? "RQ" : "RQ0");
        short side0 = server.Missions[0].Sides[0].Id;
        server.Missions[0].Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, aId), side0));   // A first on empty side0 → leader
        var mateIds = new short[mates.Length];
        for (int i = 0; i < mates.Length; i++)
        {
            short mId = Seat(server, mates[i].Bot, mates[i].Name);
            Assert.True(server.TryJoinSide(ShipOf(server, mId), side0)); // onto occupied side0 → non-leader
            mateIds[i] = mId;
        }
        Flush(server);
        Assert.Equal(aId, SideOf(server, side0).LeaderShipId);
        return (aId, mateIds, side0);
    }

    // ── wire codec bytes ───────────────────────────────────────────────────

    private static ushort ReadU16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(o, 2));
    private static short ReadI16(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o, 2));
    private static int ReadI32(byte[] b, int o) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o, 4));

    /// <summary><c>FM_CS_REQUEST_MONEY(182)</c>: header(4) + amount int32@4 +
    /// shipidRequest short@8 + hidFor short@10 = 12 B, no padding (default MSVC
    /// packing, Messages.h:934-938 — amount first so the two shorts follow
    /// contiguously and 12 is already 4-aligned).</summary>
    [Fact]
    public void RequestMoney_codec_round_trips_exact_bytes()
    {
        Assert.Equal((ushort)182, RequestMoney.Fmid);
        Assert.Equal(12, RequestMoney.FixedSize);
        var m = new RequestMoney { Amount = 6300, ShipIdRequest = 508, HidFor = 502 }.Encode();
        Assert.Equal(12, m.Length);
        Assert.Equal(12, ReadU16(m, 0));   // cbmsg
        Assert.Equal(182, ReadU16(m, 2));  // fmid
        Assert.Equal(6300, ReadI32(m, 4)); // amount @4
        Assert.Equal(508, ReadI16(m, 8));  // shipidRequest @8
        Assert.Equal(502, ReadI16(m, 10)); // hidFor @10
        var d = RequestMoney.Decode(m);
        Assert.Equal(6300, d.Amount);
        Assert.Equal((short)508, d.ShipIdRequest);
        Assert.Equal((short)502, d.HidFor);
    }

    // ── AD-target preference over richest-other ────────────────────────────

    /// <summary>⭐ The request is forwarded to the sender's OWN auto-donate
    /// target when that player's wallet holds enough — even when a DIFFERENT
    /// teammate is richer (FedSrv.CPP:5375-5396). Discriminator: a naive
    /// always-richest implementation would forward to the 500-credit teammate C
    /// instead of the AD target A; the real handler must pick A.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Request_goes_to_auto_donate_target_when_it_holds_enough_even_if_not_richest()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        var (aId, mates, side0) = SeatLeaderAndMates(server, a, (b, "RQ1"), (c, "RQ2"));
        short bId = mates[0], cId = mates[1];

        // b's auto-donate target = a (the leader). a holds 100 (>= requested 50),
        // but c is far richer (500). Correct → forward to a, NOT c.
        WalletOf(server, aId).Money = 100;
        WalletOf(server, cId).Money = 500;
        b.ResetRequestMoneys();
        a.ResetRequestMoneys();
        c.ResetRequestMoneys();

        b.SendRequestMoney(50, 509);
        int got = PumpUntil(server,
            () => a.RequestMoneys.Any(r => r.Amount == 50 && r.HidFor == 509 && r.ShipIdRequest == bId),
            800);
        Assert.True(got < 800, $"request did not reach the AD target: A={a.RequestMoneys.Count}, C={c.RequestMoneys.Count}");

        Assert.Contains(a.RequestMoneys, r => r.Amount == 50 && r.HidFor == 509 && r.ShipIdRequest == bId);
        Assert.Empty(c.RequestMoneys);          // the richer teammate must NOT get it
        Assert.Empty(b.RequestMoneys);          // the requester doesn't get their own request back
    }

    // ── richest other teammate (AD target absent / insufficient) ───────────

    /// <summary>⭐ When the sender's auto-donate target does not hold enough,
    /// the request is forwarded to the RICHEST OTHER connected teammate instead
    /// (FedSrv.CPP:5378-5395). Here a (the AD target) holds only 10 &lt; 50, so
    /// c (700) receives the forward, not a.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Request_goes_to_richest_other_teammate_when_auto_donate_target_lacks_funds()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        var (aId, mates, side0) = SeatLeaderAndMates(server, a, (b, "RF1"), (c, "RF2"));
        short bId = mates[0], cId = mates[1];

        // b's AD target = a, but a holds only 10 < 50 → richest-other search.
        // c holds 700 > a's 10 → c is picked.
        WalletOf(server, aId).Money = 10;
        WalletOf(server, cId).Money = 700;
        b.ResetRequestMoneys();
        a.ResetRequestMoneys();
        c.ResetRequestMoneys();

        b.SendRequestMoney(50, 510);
        int got = PumpUntil(server,
            () => c.RequestMoneys.Any(r => r.Amount == 50 && r.HidFor == 510 && r.ShipIdRequest == bId),
            800);
        Assert.True(got < 800, $"request did not reach the richest teammate: C={c.RequestMoneys.Count}, A={a.RequestMoneys.Count}");

        Assert.Contains(c.RequestMoneys, r => r.Amount == 50 && r.HidFor == 510 && r.ShipIdRequest == bId);
        Assert.Empty(a.RequestMoneys);   // AD target lacked funds → must NOT receive
        Assert.Empty(b.RequestMoneys);
    }

    /// <summary>⭐ Tie-break: when two non-sender teammates tie for the richest
    /// wallet, exactly ONE of them receives the forward (native's <c>&gt;</c>
    /// comparison picks the first to reach the max — no broadcast, no
    /// double-forward, no last-write).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Tied_richest_teammates_forward_to_exactly_one()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        using var d = new FltBot();
        var (aId, mates, side0) = SeatLeaderAndMates(server, a, (b, "RT1"), (c, "RT2"), (d, "RT3"));
        short bId = mates[0], cId = mates[1], dId = mates[2];

        // b's AD target = a (holds 10 < 50). c and d both hold 60 (tie for max).
        WalletOf(server, aId).Money = 10;
        WalletOf(server, cId).Money = 60;
        WalletOf(server, dId).Money = 60;
        b.ResetRequestMoneys();
        a.ResetRequestMoneys();
        c.ResetRequestMoneys();
        d.ResetRequestMoneys();

        b.SendRequestMoney(50, 515);
        int got = PumpUntil(server,
            () => c.RequestMoneys.Any(r => r.Amount == 50) || d.RequestMoneys.Any(r => r.Amount == 50),
            800);
        Assert.True(got < 800, "request did not reach either tied-richest teammate");

        int recipients = (c.RequestMoneys.Any(r => r.Amount == 50) ? 1 : 0)
                       + (d.RequestMoneys.Any(r => r.Amount == 50) ? 1 : 0);
        Assert.Equal(1, recipients);   // exactly one of the tied pair, never both
        Assert.Empty(a.RequestMoneys);
        Assert.Empty(b.RequestMoneys);
    }

    // ── solo side silently dropped ─────────────────────────────────────────

    /// <summary>⭐ A player alone on their side (no teammates, and the leader has
    /// no auto-donate target) has no resolvable recipient, so the request is
    /// silently dropped — nothing is forwarded to anyone, including back to the
    /// requester (FedSrv.CPP:5398 has no else).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Solo_player_request_is_silently_dropped()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        var (aId, _, side0) = SeatLeaderAndMates(server, a);   // A alone, leader

        WalletOf(server, aId).Money = 500;   // even holding plenty, no one to ask
        a.ResetRequestMoneys();

        a.SendRequestMoney(100, 522);
        int settled = PumpUntil(server, () => false, 80);
        Assert.True(settled < 899);

        Assert.Empty(a.RequestMoneys);   // silently dropped, no echo to self
    }

    // ── amount <= 0 refused ────────────────────────────────────────────────

    /// <summary>⭐ A non-positive requested amount is refused (log-and-ignore,
    /// no forward) even when a recipient with funds exists — native's
    /// TrapHackBoot (FedSrv.CPP:5368) hardened to this port's log-and-stop.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_positive_amount_is_refused_no_forward()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        using var a = new FltBot();
        using var b = new FltBot();
        var (aId, mates, side0) = SeatLeaderAndMates(server, a, (b, "RN1"));
        short bId = mates[0];

        WalletOf(server, aId).Money = 100;   // a would normally qualify
        a.ResetRequestMoneys();
        b.ResetRequestMoneys();

        b.SendRequestMoney(0, 530);     // 0 ≤ 0 → refused
        b.SendRequestMoney(-5, 531);    // negative → refused
        int settled = PumpUntil(server, () => false, 80);
        Assert.True(settled < 899);

        Assert.Empty(a.RequestMoneys);   // a (the would-be recipient) sees nothing
        Assert.Empty(b.RequestMoneys);
    }

    // ── multi-mission / multi-side scoping ─────────────────────────────────

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
    /// P184/P186/P190/P197): peer 0 → mission 0 / side 0; peer 1 → mission 1 /
    /// side 0 (SAME numeric side id); peers 2+ → mission 0 / side 1.</summary>
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

    /// <summary>⭐ Multi-mission/multi-side scoping (the P178/P180/P184
    /// must-not-regress bug class): a request raised on mission A side0 is
    /// forwarded to its one recipient on mission A side0 and must NEVER reach a
    /// player on a DIFFERENT mission (even one sharing the same numeric side id)
    /// nor a player on a DIFFERENT side of the same mission.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Multi_mission_request_forward_scopes_per_mission_and_side()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        Assert.Equal(2, server.Missions.Count);

        using var a0 = new FltBot();   // mission A, side 0
        using var b0 = new FltBot();   // mission B, side 0 (SAME numeric side id)
        using var a2 = new FltBot();   // mission A, side 0 (requester)
        using var a4 = new FltBot();   // mission A, side 1 (different side of A)
        short a0Id = Seat(server, a0, "MM0");
        short b0Id = Seat(server, b0, "MM1");
        short a2Id = Seat(server, a2, "MM2");
        short a4Id = Seat(server, a4, "MM3");

        Mission mA = server.Missions[0];
        Mission mB = server.Missions[1];
        short mA0 = mA.Sides[0].Id;
        short mB0 = mB.Sides[0].Id;
        Assert.Equal(mA0, mB0);   // same numeric side id across missions

        mA.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, a0Id), mA0));   // a0 leader
        Assert.True(server.TryJoinSide(ShipOf(server, a2Id), mA0));   // a2 non-leader → donates to a0
        mB.Stage = MissionStage.Started;
        Assert.True(server.TryJoinSide(ShipOf(server, b0Id), mB0));   // b0 leader of its side0
        Flush(server);

        // a0 holds enough → a2's request must go to a0 (on mA side0) only.
        WalletOf(server, a0Id).Money = 500;
        a0.ResetRequestMoneys();
        b0.ResetRequestMoneys();
        a2.ResetRequestMoneys();
        a4.ResetRequestMoneys();

        a2.SendRequestMoney(100, 545);
        int got = PumpUntil(server,
            () => a0.RequestMoneys.Any(r => r.Amount == 100 && r.HidFor == 545 && r.ShipIdRequest == a2Id),
            800);
        Assert.True(got < 800, "mission-A request did not reach its mission-A side0 recipient");

        Assert.Contains(a0.RequestMoneys, r => r.Amount == 100 && r.HidFor == 545 && r.ShipIdRequest == a2Id);
        Assert.Empty(b0.RequestMoneys);   // mission B (shared numeric side id) sees nothing
        Assert.Empty(a4.RequestMoneys);   // mission A, different side, sees nothing
        Assert.Empty(a2.RequestMoneys);   // requester doesn't get their own request back
    }
}
