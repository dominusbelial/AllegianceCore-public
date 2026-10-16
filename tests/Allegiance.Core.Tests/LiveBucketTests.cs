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
/// P119 + P198 — bucket construction funding: the three codecs (<c>BUCKET_DONATE</c>
/// 64, <c>BUCKET_STATUS</c> 65, <c>CREATE_BUCKETS</c> 191), the wired
/// donation handler, the join-sync bucket-catalog broadcast, and the
/// completion-<em>detection</em> seam. From P198 the donation's gate and debit
/// live on the donating <b>player's own wallet</b> (P197 <c>PlayerWallet.Money</c>),
/// not the side treasury — matching native <c>pfsPlayer-&gt;GetMoney()</c>
/// (FedSrv.CPP:7148,7160). Five live loopback proofs over a real
/// in-process server + real TCP bots (native citations in the Server /
/// <see cref="Bucket"/> docs):
///   (1) a small donation (< the bucket's remaining price) invests exactly the
///       donated amount, debits the donor's wallet, and broadcasts BUCKET_STATUS
///       side-wide (and NOT cross-side), plus a negative MONEY_CHANGE(60) to the
///       side with the donor as both sidTo/sidFrom;
///   (2) an over-sized donation invests only what the bucket still needs —
///       reaching exactly its price, never over — the excess staying in the
///       donor's wallet as an explicit positive refund MONEY_CHANGE(60);
///   (3) a real join sync delivers CREATE_BUCKETS (zeroed tech masks) plus one
///       BUCKET_STATUS per side bucket;
///   (3b) a player whose personal wallet is too thin is refused even when the
///       side treasury is large (the P198 gate's discriminating proof);
///   (4) fully funding a bucket AND letting enough build-time pass fires the
///       completion seam without spawning an object; P691 progress reaches the investment pane.
/// The codecs round-trip byte-exact in Wire.BucketWireTests.
/// </summary>
[Collection("LiveNetwork")]
public class LiveBucketTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // The synthetic catalog's drone hull (BucketCatalog.SyntheticDroneHullId):
    // hull 504, a real fighter-class hull in PCore001.igc (measured in P108,
    // not guessed).
    private const short BucketHullId = 504;

    private static Cfg Config(int ups = 20) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "Bucket Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 882u, DwCookie = 0x882u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8799000000000000000ul, DrmHash = "buckettest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    /// <summary>Three-ship spawn injection (P118 pattern): two funder hulls (504)
    /// on the first friendly station's side, an outsider on the OTHER side so we
    /// can assert the broadcast is side-scoped.</summary>
    private sealed class ThreeSpawn
    {
        public Ship Funder = null!, Outsider = null!;
    }

    private static ThreeSpawn InjectThreeSpawn(Srv server)
    {
        var sp = new ThreeSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var station = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
            var sector = mission.Sectors.First(s => s.Stations.Contains(station));
            short friendly = station.SideId;
            var otherSide = mission.Sides.First(s => s.Id != friendly);

            Ship ship;
            if (n <= 1)
            {
                ship = new Ship(id, core.HullTypes[BucketHullId], StationPos(station) + new Vector3(25f + 20f * n, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = friendly };
                sp.Funder ??= ship;
            }
            else
            {
                var lonerSector = mission.Sectors.First(s => !s.Stations.Any(st => st.SideId >= 0));
                ship = new Ship(id, core.HullTypes[BucketHullId], new Vector3(0f, 832f, 976f), Vector3.Zero, Orientation.Identity) { SideId = otherSide.Id };
                mission.PlaceShip(ship, lonerSector);
                sp.Outsider = ship;
                return (ship, lonerSector);
            }
            n++;
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        return sp;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>P551 — pick a bucket the side can GENUINELY buy (native
    /// <c>Side::CanBuy</c>, sideigc.h:224-281): the cheapest buyable, non
    /// <c>c_didTeamMoney</c> development bucket. The donation tests must fund a
    /// bucket the CanBuy gate admits — the synthetic ship-type bucket wraps hull
    /// 504 'Bomber', whose required techs are NOT covered by a starting
    /// Garrison's local techs + civ base techs (probed in P551), so it is now
    /// correctly refused; a buyable development is the honest case.</summary>
    private static Bucket BuyableBucket(Side side) =>
        side.Buckets
            .Where(b => b.ItemType == BucketItemType.BitDevelopment
                && b.Development is { } d && d.DevelopmentId != Mission.TeamMoneyDevelopmentId)
            .Where(b => side.CanBuy(b))
            .OrderBy(b => b.Price)
            .First();

    // ── (1) join sync: CREATE_BUCKETS + per-bucket BUCKET_STATUS ─────────

    /// <summary>⭐ Scenario 1 — joining a mission sends exactly one
    /// <c>CREATE_BUCKETS(191)</c> with both 50-byte tech masks zeroed (the
    /// port has no tech-tree infra, P117 finding) and one <c>BUCKET_STATUS(65)</c>
    /// per side bucket (the synthetic catalog seeds exactly one).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Join_broadcasts_create_buckets_and_per_bucket_status()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "joiner");
        bot.SendLogonRequest(Request("joiner"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "joiner did not seat");
        short sideId = SideOf(server, bot);

        int seen = PumpUntil(server, () => bot.CreateBucketsReceived.Count >= 1, 2000);
        Assert.True(seen < 2000, "CREATE_BUCKETS(191) never reached the joining bot");

        var cb = Assert.Single(bot.CreateBucketsReceived);
        // P189: CREATE_BUCKETS now carries the side's real development techs
        // (its civ's base techs) — superseding the P117-era all-zero masks.
        var side = server.Missions[0].Sides.Single(s => s.Id == sideId);
        Assert.Equal(side.DevelopmentTechs.ToBytes(), cb.TtbmDevelopments);
        Assert.Equal(side.DevelopmentTechs.ToBytes(), cb.TtbmInitial);

        // P189: the catalog is additive — one synthetic ship-type bucket plus one
        // development bucket per non-obsolete development. Locate the ship-type
        // bucket explicitly (the P119-era single-bucket assumption is gone).
        var bucket = side.Buckets.Single(b => b.ItemType == BucketItemType.BitShipType);
        int bsSeen = PumpUntil(server, () => bot.BucketStatuses.Any(s2 => s2.IBucket == bucket.Id), 2000);
        Assert.True(bsSeen < 2000, "BUCKET_STATUS(65) never reached the joining bot");
        var bs = bot.BucketStatuses.Single(s2 => s2.IBucket == bucket.Id);
        Assert.Equal(bucket.Id, bs.IBucket);
        Assert.Equal(side.Id, bs.SideId);
        Assert.Equal(0, bs.MoneyTotal);      // nothing invested yet
        Assert.Equal(0u, bs.TimeTotal);

        Console.WriteLine($"CREATEBUCKETS-LIVE: joiner on side {sideId} -> CREATE_BUCKETS(191) devMask({cb.TtbmDevelopments.Sum(b2 => System.Numerics.BitOperations.PopCount((uint)b2))} bits); BUCKET_STATUS(65) bucket {bucket.Id} money 0/{bucket.Price} (synthetic ship-type + {side.Buckets.Count(b2 => b2.ItemType == BucketItemType.BitDevelopment)} development buckets)");
    }

    // ── (2) small donation: invest exactly donated, broadcast side-wide ──

    /// <summary>⭐ Scenario 2 — a donation smaller than the bucket's remaining
    /// price invests exactly the donated amount, deducts exactly that from the
    /// treasury, and broadcasts <c>BUCKET_STATUS</c> to BOTH same-side peers and
    /// to NO peer on the other side.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Small_donation_funds_bucket_and_broadcasts_sidewide_not_cross_side()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectThreeSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        a.Connect(server.BoundPort, "AA"); a.SendLogonRequest(Request("AA"));
        b.Connect(server.BoundPort, "BB"); b.SendLogonRequest(Request("BB"));
        c.Connect(server.BoundPort, "CC"); c.SendLogonRequest(Request("CC"));
        int seat = PumpUntil(server,
            () => a.ShipAssignment is not null && b.ShipAssignment is not null && c.ShipAssignment is not null, 6000);
        Assert.True(seat < 6000, "all three bots must seat");

        FltBot funderBot = null!, mateBot = null!, outsiderBot = null!;
        foreach (var bot in new[] { a, b, c })
        {
            short id = bot.ShipAssignment!.Ship.ShipId;
            if (id == sp.Funder.Id) funderBot = bot;
            else if (id == sp.Outsider.Id) outsiderBot = bot;
            else mateBot = bot;
        }
        Assert.NotNull(funderBot);
        Assert.NotNull(mateBot);
        Assert.NotNull(outsiderBot);

        short funderSide = sp.Funder.SideId!.Value;
        // Sanity: the two funder/mate bots are on the same side as the funder's
        // spawned ship; the outsider is on the other side.
        Assert.Equal(funderSide, SideOf(server, mateBot));
        Assert.NotEqual(funderSide, SideOf(server, outsiderBot));

        var side = server.Missions[0].Sides.Single(s => s.Id == funderSide);
        var bucket = BuyableBucket(side);
        // P198: the donation now spends from the donating PLAYER's wallet, not
        // the side treasury. Fund the funder's wallet; leave the side treasury a
        // large sentinel that must be UNTOUCHED by this operation (proof the
        // spend source really moved off the pool).
        server.Ships.First(s => s.Ship.Id == sp.Funder.Id).Wallet.Money = 100000;
        side.Money = 888888;

        // Strictly smaller than the outstanding price (bucket starts unfunded),
        // so no over-fund capping participates — the honest small-donation case.
        int donate = (int)bucket.Price - 1;
        Assert.True(donate > 0, $"bucket price {bucket.Price} too small to donate below");
        Assert.True(donate <= 100000, "donation must fit in the funder's wallet");

        funderBot.ResetBucketStatuses(); mateBot.ResetBucketStatuses(); outsiderBot.ResetBucketStatuses();
        funderBot.ResetMoneyChanges(); mateBot.ResetMoneyChanges(); outsiderBot.ResetMoneyChanges();
        funderBot.SendDonate(bucket.Id, donate);

        int seen = PumpUntil(server,
            () => funderBot.BucketStatuses.Count >= 1 && mateBot.BucketStatuses.Count >= 1
                && funderBot.MoneyChanges.Any(mc => mc.DMoney == -donate && mc.SidFrom == sp.Funder.Id), 3000);
        Assert.True(seen < 3000, "BUCKET_STATUS(65) did not reach both same-side peers");

        var fbs = Assert.Single(funderBot.BucketStatuses, s => s.TimeTotal == 0);
        var mbs = Assert.Single(mateBot.BucketStatuses, s => s.TimeTotal == 0);
        Assert.Equal(donate, fbs.MoneyTotal);
        Assert.Equal(bucket.Id, fbs.IBucket);
        Assert.Equal(funderSide, fbs.SideId);
        Assert.Equal(fbs.MoneyTotal, mbs.MoneyTotal);

        // Bucket invested + the PLAYER's wallet both moved by exactly the donated
        // amount; the side treasury sentinel is UNTOUCHED (P198 spend-source move).
        Assert.Equal(donate, bucket.InvestedMoney);
        Assert.Equal(100000 - donate, server.Ships.First(s => s.Ship.Id == sp.Funder.Id).Wallet.Money);
        Assert.Equal(888888L, side.Money);

        // P198: the spend MONEY_CHANGE(60) — negative delta, sidTo == sidFrom ==
        // the donating player's ship (native FedSrv.CPP:7162-7168), reaching both
        // same-side peers (side-wide) and NO peer on the other side.
        Assert.Contains(funderBot.MoneyChanges, mc => mc.DMoney == -donate && mc.SidTo == sp.Funder.Id && mc.SidFrom == sp.Funder.Id);
        Assert.Contains(mateBot.MoneyChanges, mc => mc.DMoney == -donate && mc.SidTo == sp.Funder.Id && mc.SidFrom == sp.Funder.Id);
        Assert.DoesNotContain(outsiderBot.MoneyChanges, mc => mc.DMoney == -donate && mc.SidFrom == sp.Funder.Id);
        // Small donation has no excess, so no positive refund reaches anyone.
        Assert.DoesNotContain(funderBot.MoneyChanges, mc => mc.DMoney > 0);

        // Negative: the other side's peer saw NO BUCKET_STATUS.
        int neg = PumpUntil(server, () => outsiderBot.BucketStatuses.Count >= 1, 1500);
        Assert.True(neg == 1500, "other-side peer should NOT receive BUCKET_STATUS");
        Assert.Empty(outsiderBot.BucketStatuses);

        Console.WriteLine($"BUCKETDONATE-LIVE: funder on side {funderSide} donated {donate} into bucket {bucket.Id} (price {bucket.Price}) -> invested {bucket.InvestedMoney}, wallet {server.Ships.First(s => s.Ship.Id == sp.Funder.Id).Wallet.Money}, treasury {side.Money}; MONEY_CHANGE(-{donate}) to 2 same-side peers; outsider saw none");
    }

    // ── (3) over-sized donation: cap at price, refund the excess ─────────

    /// <summary>⭐ Scenario 3 — a donation larger than what the bucket still
    /// needs takes only the needed amount (the bucket reaches exactly its
    /// price, never over) and the excess is NOT deducted from the treasury
    /// (the native leftover-refund logic, bucketigc.h:284-287 /
    /// FedSrv.CPP:7151-7160, ported as "only spend what's needed").</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Oversized_donation_caps_at_price_and_leaves_excess_in_treasury()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectThreeSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        a.Connect(server.BoundPort, "AA"); a.SendLogonRequest(Request("AA"));
        b.Connect(server.BoundPort, "BB"); b.SendLogonRequest(Request("BB"));
        c.Connect(server.BoundPort, "CC"); c.SendLogonRequest(Request("CC"));
        int seat = PumpUntil(server,
            () => a.ShipAssignment is not null && b.ShipAssignment is not null && c.ShipAssignment is not null, 6000);
        Assert.True(seat < 6000, "all three bots must seat");

        FltBot funderBot = null!, outsiderBot = null!;
        foreach (var bot in new[] { a, b, c })
        {
            short id = bot.ShipAssignment!.Ship.ShipId;
            if (id == sp.Funder.Id) funderBot = bot;
            else if (id == sp.Outsider.Id) outsiderBot = bot;
        }
        Assert.NotNull(funderBot);
        Assert.NotNull(outsiderBot);

        short funderSide = sp.Funder.SideId!.Value;
        var side = server.Missions[0].Sides.Single(s => s.Id == funderSide);
        var bucket = BuyableBucket(side);
        // P198: fund the PLAYER's wallet; leave the side treasury an untouched
        // sentinel proving the excess stays in the donor's WALLET, not the pool.
        server.Ships.First(s => s.Ship.Id == sp.Funder.Id).Wallet.Money = 100000;
        side.Money = 666666;

        int excess = 54321;
        int huge = (int)bucket.Price + excess;   // way over what's needed
        Assert.True(huge <= 100000, "donation must fit in the funder's wallet");

        funderBot.ResetBucketStatuses(); outsiderBot.ResetBucketStatuses();
        funderBot.ResetMoneyChanges(); outsiderBot.ResetMoneyChanges();
        funderBot.SendDonate(bucket.Id, huge);
        int seen = PumpUntil(server,
            () => funderBot.BucketStatuses.Count >= 1
                && funderBot.MoneyChanges.Any(mc => mc.DMoney == -(int)bucket.Price && mc.SidFrom == sp.Funder.Id)
                && funderBot.MoneyChanges.Any(mc => mc.DMoney == excess && mc.SidFrom == -1), 3000);
        Assert.True(seen < 3000, "BUCKET_STATUS(65) / MONEY_CHANGE never reached the funder");

        var bs = Assert.Single(funderBot.BucketStatuses, s => s.TimeTotal == 0);
        // Bucket reached EXACTLY its price — never over-funded.
        Assert.Equal(bucket.Price, bucket.InvestedMoney);
        Assert.Equal(bucket.Price, bs.MoneyTotal);
        // Only the needed amount left the PLAYER's wallet; the excess (mnyRefund)
        // stayed in the wallet, returned as an explicit positive MONEY_CHANGE.
        Assert.Equal(100000 - (int)bucket.Price, server.Ships.First(s => s.Ship.Id == sp.Funder.Id).Wallet.Money);
        Assert.Equal(666666L, side.Money);

        // Spend MONEY_CHANGE: negative delta, sidTo == sidFrom == the donor's ship.
        Assert.Contains(funderBot.MoneyChanges, mc => mc.DMoney == -(int)bucket.Price && mc.SidTo == sp.Funder.Id && mc.SidFrom == sp.Funder.Id);
        // Refund MONEY_CHANGE: positive delta, sidTo == the donor's ship, sidFrom == NA — funder ONLY.
        Assert.Contains(funderBot.MoneyChanges, mc => mc.DMoney == excess && mc.SidTo == sp.Funder.Id && mc.SidFrom == -1);
        Assert.DoesNotContain(outsiderBot.MoneyChanges, mc => mc.DMoney == excess && mc.SidTo == sp.Funder.Id);

        Console.WriteLine($"BUCKETCAP-LIVE: donated {huge} (price {bucket.Price}) -> invested exactly {bucket.InvestedMoney}, wallet {server.Ships.First(s => s.Ship.Id == sp.Funder.Id).Wallet.Money} (excess {excess} kept in wallet), treasury {side.Money} untouched; MONEY_CHANGE(-{bucket.Price}) side-wide + refund MONEY_CHANGE(+{excess}) to funder only");
    }

    // ── (3b) P198: insufficient PLAYER wallet refused even if the side is rich ─

    /// <summary>⭐ P198 — the gate's discriminating proof: a player whose
    /// <b>personal wallet</b> is thinner than the offered amount is refused even
    /// when the side treasury is large. Under the old pooled gate
    /// (<c>Side.Money &gt;= moneyGiven</c>, P119) this donation would have been
    /// accepted and broadcast; under the P198 per-player gate
    /// (<c>pfsPlayer-&gt;GetMoney() &gt;= moneyGiven</c>, FedSrv.CPP:7148) it is
    /// silently dropped — no BUCKET_STATUS, no MONEY_CHANGE, and no wallet /
    /// side / bucket mutation.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Insufficient_player_wallet_refused_even_if_side_treasury_is_large()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: bypass the ready-up lobby

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "poorplayer");
        bot.SendLogonRequest(Request("poorplayer"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "bot did not seat");
        short sideId = SideOf(server, bot);
        short shipId = bot.ShipAssignment!.Ship.ShipId;

        var side = server.Missions[0].Sides.Single(s => s.Id == sideId);
        var bucket = side.Buckets.Single(b => b.ItemType == BucketItemType.BitShipType);
        // The PLAYER's wallet is thin…
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money = 10;
        // …but the side treasury is rich — the OLD pooled gate would have passed it.
        side.Money = 100_000;

        int donate = 5000;
        Assert.True(donate > 10, "donation must exceed the player's thin wallet");
        Assert.True(donate <= 100_000, "donation would fit comfortably in the side treasury (discriminating setup)");

        bot.ResetBucketStatuses(); bot.ResetMoneyChanges();
        int walletBefore = server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money;
        bot.SendDonate(bucket.Id, donate);

        // Nothing may happen: no BUCKET_STATUS broadcast, no MONEY_CHANGE, and the
        // bucket is not funded. If the OLD pooled gate were restored (Side.Money
        // covers the offer), this donation would be accepted and would broadcast a
        // BUCKET_STATUS — failing the seen==1500 / Empty assertions. That broadcast
        // absence is the discriminating must-fail-if-removed proof.
        int seen = PumpUntil(server,
            () => bot.BucketStatuses.Count >= 1
                || bot.MoneyChanges.Any(mc => mc.SidFrom == shipId && mc.DMoney != 0), 1500);
        Assert.True(seen == 1500, "a wallet-short donation must NOT spend or broadcast");
        Assert.Empty(bot.BucketStatuses);
        Assert.Empty(bot.MoneyChanges);
        // Bucket is provably untouched (payday cannot fund a bucket). The wallet is
        // NOT re-asserted exactly because P197 payday legitimately credits it during
        // the long idle pump — the sharp invariant is that the bucket is unfunded and
        // no donation broadcast ever escaped the server.
        Assert.Equal(0, bucket.InvestedMoney);
        // Supplementary: the wallet must never have been debited below what it held
        // when the attempt began (only a wrongful donation could shrink it; payday
        // only ever grows it). Not the primary discriminator — see above.
        Assert.True(server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money >= walletBefore,
            "refused donation must not debit the player's wallet");

        Console.WriteLine($"BUCKETREFUSE-P198: player {shipId} wallet {walletBefore} < donated {donate} (side treasury 100000) -> donation refused: no BUCKET_STATUS, no MONEY_CHANGE, bucket unfunded");
    }

    // ── (3c) P551: donation to a bucket the side CANNOT buy → refused, refunded ──

    /// <summary>⭐ P551 — the native <c>CanBuy</c> gate (FedSrv.CPP:7152): a
    /// donation to a bucket the side cannot buy is <b>refused</b> — no spend,
    /// no BUCKET_STATUS, no negative MONEY_CHANGE — and the full amount is
    /// returned as an explicit positive refund MONEY_CHANGE (FedSrv.CPP:7199-7208),
    /// leaving the player's wallet and the bucket unchanged. The synthetic
    /// ship-type bucket wraps hull 504 'Bomber', whose required techs a starting
    /// Garrison + civ base techs do NOT cover, so <see cref=\"Side.CanBuy\"/>
    /// correctly reports false (probed in P551). On the OLD (pre-CanBuy) port
    /// this donation invested into the bucket and broadcast a BUCKET_STATUS —
    /// the failing-first discriminator.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Donation_to_unbuyable_bucket_refused_and_full_refunded()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "cantbuy");
        bot.SendLogonRequest(Request("cantbuy"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "bot did not seat");
        short sideId = SideOf(server, bot);
        short donorId = bot.ShipAssignment!.Ship.ShipId;

        var side = server.Missions[0].Sides.Single(s => s.Id == sideId);
        // The ship-type bucket is genuinely unbuyable at start (native CanBuy)…
        var bucket = side.Buckets.Single(b => b.ItemType == BucketItemType.BitShipType);
        Assert.False(side.CanBuy(bucket), "premise: the ship-type bucket must be unbuyable for this test");
        // …while at least one buyable development exists (contrast).
        Assert.True(BuyableBucket(side) is not null, "premise: a buyable bucket must exist for contrast");

        int donate = 4000;
        server.Ships.First(s => s.Ship.Id == donorId).Wallet.Money = 50000;   // wallet covers the offer
        side.Money = 777777;                                                    // rich side treasury sentinel
        long walletBefore = server.Ships.First(s => s.Ship.Id == donorId).Wallet.Money;

        bot.ResetBucketStatuses(); bot.ResetMoneyChanges();
        bot.SendDonate(bucket.Id, donate);

        // The FULL refund MONEY_CHANGE is sent synchronously with the refusal —
        // wait for it with a SHORT pump (a long idle pump would let payday drain
        // the treasury sentinel, muddying the untouched assertion).
        int seen = PumpUntil(server, () => bot.MoneyChanges.Any(mc => mc.DMoney == donate && mc.SidFrom == -1), 1500);
        Assert.True(seen < 1500, "full-refund MONEY_CHANGE never reached the donor");

        // Sharp invariants: nothing invested, no spend MONEY_CHANGE, no
        // BUCKET_STATUS (native refuses the whole spend before any of those),
        // and the side treasury sentinel is untouched by the donation.
        Assert.DoesNotContain(bot.MoneyChanges, mc => mc.SidFrom == donorId && mc.DMoney < 0);
        Assert.Empty(bot.BucketStatuses);
        Assert.Equal(0, bucket.InvestedMoney);
        Assert.Equal(777777L, side.Money);

        // The FULL amount is refunded as an explicit positive MONEY_CHANGE
        // (sidFrom = NA), and the wallet was never debited (>= its starting
        // value — only a wrongful deduction could shrink it; payday only grows).
        var refund = Assert.Single(bot.MoneyChanges);
        Assert.Equal(donate, refund.DMoney);
        Assert.Equal(donorId, refund.SidTo);
        Assert.Equal((short)-1, refund.SidFrom);
        Assert.True(server.Ships.First(s => s.Ship.Id == donorId).Wallet.Money >= walletBefore,
            "refused donation must not debit the player's wallet");

        Console.WriteLine($"BUCKETCANBUY-LIVE: donor {donorId} donated {donate} into unbuyable bucket {bucket.Id} (CanBuy=false) -> nothing invested, no spend/no BUCKET_STATUS, full refund MONEY_CHANGE(+{donate}) sidFrom=NA; wallet >= {walletBefore}, side treasury {side.Money} untouched");
    }

    // ── (4) full funding + build time → completion seam and progress ────

    /// <summary>⭐ Scenario 4 — fully funding a bucket AND letting enough build
    /// time pass fires the completion-<em>detection</em> seam exactly once, with
    /// no spawned object (ship count unchanged). P691 also delivers integer
    /// progress changes through BUCKET_STATUS to the side's investment pane.
    /// Uses UpdatesPerSecond=2 so each TickSim advances 0.5s of sim time,
    /// making the time-to-build traverse quick and deterministic.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Full_funding_plus_build_time_fires_completion_seam_only()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config(ups: 2));
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "builder");
        bot.SendLogonRequest(Request("builder"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "builder did not seat");
        short sideId = SideOf(server, bot);

        var side = server.Missions[0].Sides.Single(s => s.Id == sideId);
        var bucket = BuyableBucket(side);
        // P198: fund the builder's PERSONAL wallet; the side treasury stays an
        // untouched sentinel (proving the full-price spend came from the wallet).
        short builderId = bot.ShipAssignment!.Ship.ShipId;
        server.Ships.First(s => s.Ship.Id == builderId).Wallet.Money = 10_000_000;
        side.Money = 555555;

        int shipsBefore = server.Ships.Count;
        bot.ResetBucketStatuses();          // drop the join-sync BUCKET_STATUS(money 0)

        // Fully fund in one (exact) donation.
        bot.SendDonate(bucket.Id, bucket.Price);
        int funded = PumpUntil(server, () => bot.BucketStatuses.Count >= 1 && bucket.InvestedMoney >= bucket.Price, 3000);
        Assert.True(funded < 3000, "bucket did not reach full funding");
        Assert.Equal(bucket.Price, bucket.InvestedMoney);
        Assert.Equal(bucket.Price, Assert.Single(bot.BucketStatuses, s => s.TimeTotal == 0).MoneyTotal);   // the funding broadcast
        // P198: the player's wallet paid the full price; the side treasury is untouched.
        Assert.Equal(10_000_000 - (int)bucket.Price, server.Ships.First(s => s.Ship.Id == builderId).Wallet.Money);
        Assert.Equal(555555L, side.Money);
        bot.ResetBucketStatuses();          // observe the subsequent progress snapshots

        // Enough build time to complete: at 2 upd/s each tick = 0.5s, so need
        // TimeToBuild seconds -> 2*TimeToBuild ticks; add a generous margin.
        long ticksNeeded = 2L * bucket.TimeToBuild + 50;
        int done = PumpUntil(server, () => server.BucketCompletions.Count >= 1, (int)ticksNeeded);
        Assert.True(done < ticksNeeded, $"bucket {bucket.Id} did not complete after {ticksNeeded} ticks");

        // The completion seam fired exactly once (detection)…
        Assert.Single(server.BucketCompletions);
        Assert.Contains(bucket.Id, server.BucketCompletions);
        Assert.True(bucket.Completed);

        // The synthetic bucket still spawns no object. The last progress
        // snapshot must carry the exact completed build time and funding.
        Assert.Equal(shipsBefore, server.Ships.Count);
        int delivered = PumpUntil(server, () => bot.BucketStatuses.Any(s => s.IBucket == bucket.Id
            && s.TimeTotal == bucket.TimeToBuild * 1000), 3000);
        Assert.True(delivered < 3000, "completed progress snapshot did not arrive");
        Assert.All(bot.BucketStatuses, s =>
        {
            Assert.Equal(bucket.Id, s.IBucket); Assert.Equal(bucket.Price, s.MoneyTotal);
            Assert.InRange(s.TimeTotal, 0u, bucket.TimeToBuild * 1000);
        });

        Console.WriteLine($"BUCKETCOMPLETE-LIVE: bucket {bucket.Id} fully funded ({bucket.InvestedMoney}/{bucket.Price}) + {bucket.TimeToBuild}s build -> completion seam fired once; no object spawned, progress delivered");
    }
}
