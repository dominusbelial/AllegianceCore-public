using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P119 — bucket construction funding: the three codecs (<c>BUCKET_DONATE</c>
/// 64, <c>BUCKET_STATUS</c> 65, <c>CREATE_BUCKETS</c> 191), the wired
/// donation handler, the join-sync bucket-catalog broadcast, and the
/// completion-<em>detection</em> seam. Four live loopback proofs over a real
/// in-process server + real TCP bots (native citations in the Server /
/// <see cref="Bucket"/> docs):
///   (1) a small donation (< the bucket's remaining price) invests exactly the
///       donated amount and broadcasts BUCKET_STATUS side-wide (and NOT cross-side);
///   (2) an over-sized donation invests only what the bucket still needs —
///       reaching exactly its price, never over — and the excess is NOT
///       deducted from the treasury;
///   (3) a real join sync delivers CREATE_BUCKETS (zeroed tech masks) plus one
///       BUCKET_STATUS per side bucket;
///   (4) fully funding a bucket AND letting enough build-time pass fires the
///       completion seam — and nothing else happens (no spawned object, no
///       further broadcast).
/// The codecs round-trip byte-exact in Wire.BucketWireTests.
/// </summary>
[Collection("LiveNetwork")]
public class LiveBucketTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

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

    // ── (1) join sync: CREATE_BUCKETS + per-bucket BUCKET_STATUS ─────────

    /// <summary>⭐ Scenario 1 — joining a mission sends exactly one
    /// <c>CREATE_BUCKETS(191)</c> with both 50-byte tech masks zeroed (the
    /// port has no tech-tree infra, P117 finding) and one <c>BUCKET_STATUS(65)</c>
    /// per side bucket (the synthetic catalog seeds exactly one).</summary>
    [Fact]
    public void Join_broadcasts_create_buckets_and_per_bucket_status()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "joiner");
        bot.SendLogonRequest(Request("joiner"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "joiner did not seat");
        short sideId = SideOf(server, bot);

        int seen = PumpUntil(server, () => bot.CreateBucketsReceived.Count >= 1, 2000);
        Assert.True(seen < 2000, "CREATE_BUCKETS(191) never reached the joining bot");

        var cb = Assert.Single(bot.CreateBucketsReceived);
        Assert.All(cb.TtbmDevelopments, b => Assert.Equal(0, b));
        Assert.All(cb.TtbmInitial, b => Assert.Equal(0, b));

        var side = server.Missions[0].Sides.Single(s => s.Id == sideId);
        var bucket = Assert.Single(side.Buckets);
        int bsSeen = PumpUntil(server, () => bot.BucketStatuses.Count >= 1, 2000);
        Assert.True(bsSeen < 2000, "BUCKET_STATUS(65) never reached the joining bot");
        var bs = Assert.Single(bot.BucketStatuses);
        Assert.Equal(bucket.Id, bs.IBucket);
        Assert.Equal(side.Id, bs.SideId);
        Assert.Equal(0, bs.MoneyTotal);      // nothing invested yet
        Assert.Equal(0u, bs.TimeTotal);

        Console.WriteLine($"CREATEBUCKETS-LIVE: joiner on side {sideId} -> CREATE_BUCKETS(191) masks=0; BUCKET_STATUS(65) bucket {bucket.Id} money 0/{bucket.Price} (synthetic catalog, real hull {BucketHullId})");
    }

    // ── (2) small donation: invest exactly donated, broadcast side-wide ──

    /// <summary>⭐ Scenario 2 — a donation smaller than the bucket's remaining
    /// price invests exactly the donated amount, deducts exactly that from the
    /// treasury, and broadcasts <c>BUCKET_STATUS</c> to BOTH same-side peers and
    /// to NO peer on the other side.</summary>
    [Fact]
    public void Small_donation_funds_bucket_and_broadcasts_sidewide_not_cross_side()
    {
        using var server = new Srv();
        var sp = InjectThreeSpawn(server);
        server.Boot(Config());

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
        var bucket = Assert.Single(side.Buckets);
        side.Money = 100000;

        // Strictly smaller than the outstanding price (bucket starts unfunded),
        // so no over-fund capping participates — the honest small-donation case.
        int donate = (int)bucket.Price - 1;
        Assert.True(donate > 0, $"bucket price {bucket.Price} too small to donate below");

        funderBot.ResetBucketStatuses(); mateBot.ResetBucketStatuses(); outsiderBot.ResetBucketStatuses();
        funderBot.SendDonate(bucket.Id, donate);

        int seen = PumpUntil(server,
            () => funderBot.BucketStatuses.Count >= 1 && mateBot.BucketStatuses.Count >= 1, 3000);
        Assert.True(seen < 3000, "BUCKET_STATUS(65) did not reach both same-side peers");

        var fbs = Assert.Single(funderBot.BucketStatuses);
        var mbs = Assert.Single(mateBot.BucketStatuses);
        Assert.Equal(donate, fbs.MoneyTotal);
        Assert.Equal(bucket.Id, fbs.IBucket);
        Assert.Equal(funderSide, fbs.SideId);
        Assert.Equal(fbs.MoneyTotal, mbs.MoneyTotal);

        // Treasury + bucket invested both moved by exactly the donated amount.
        Assert.Equal(donate, bucket.InvestedMoney);
        Assert.Equal(100000L - donate, side.Money);

        // Negative: the other side's peer saw NO BUCKET_STATUS.
        int neg = PumpUntil(server, () => outsiderBot.BucketStatuses.Count >= 1, 1500);
        Assert.True(neg == 1500, "other-side peer should NOT receive BUCKET_STATUS");
        Assert.Empty(outsiderBot.BucketStatuses);

        Console.WriteLine($"BUCKETDONATE-LIVE: funder on side {funderSide} donated {donate} into bucket {bucket.Id} (price {bucket.Price}) -> invested {bucket.InvestedMoney}, treasury {side.Money}, BUCKET_STATUS to 2 same-side peers; outsider saw none");
    }

    // ── (3) over-sized donation: cap at price, refund the excess ─────────

    /// <summary>⭐ Scenario 3 — a donation larger than what the bucket still
    /// needs takes only the needed amount (the bucket reaches exactly its
    /// price, never over) and the excess is NOT deducted from the treasury
    /// (the native leftover-refund logic, bucketigc.h:284-287 /
    /// FedSrv.CPP:7151-7160, ported as "only spend what's needed").</summary>
    [Fact]
    public void Oversized_donation_caps_at_price_and_leaves_excess_in_treasury()
    {
        using var server = new Srv();
        var sp = InjectThreeSpawn(server);
        server.Boot(Config());

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
        var bucket = Assert.Single(side.Buckets);
        side.Money = 100000;

        int excess = 54321;
        int huge = (int)bucket.Price + excess;   // way over what's needed
        Assert.True(huge < side.Money, "donation must fit in the treasury");

        funderBot.ResetBucketStatuses();
        funderBot.SendDonate(bucket.Id, huge);
        int seen = PumpUntil(server, () => funderBot.BucketStatuses.Count >= 1, 3000);
        Assert.True(seen < 3000, "BUCKET_STATUS(65) never reached the funder");

        var bs = Assert.Single(funderBot.BucketStatuses);
        // Bucket reached EXACTLY its price — never over-funded.
        Assert.Equal(bucket.Price, bucket.InvestedMoney);
        Assert.Equal(bucket.Price, bs.MoneyTotal);
        // Only the needed amount left the treasury; the excess stayed in the pool.
        Assert.Equal(100000L - bucket.Price, side.Money);

        Console.WriteLine($"BUCKETCAP-LIVE: donated {huge} (price {bucket.Price}) -> invested exactly {bucket.InvestedMoney}, treasury {side.Money} (excess {excess} kept)");
    }

    // ── (4) full funding + build time → completion seam, nothing else ────

    /// <summary>⭐ Scenario 4 — fully funding a bucket AND letting enough build
    /// time pass fires the completion-<em>detection</em> seam exactly once, with
    /// nothing else happening: no spawned object (ship count unchanged), no
    /// further broadcast (the bucket emits no second BUCKET_STATUS). This is the
    /// task's explicit "detect but don't act yet" boundary.
    /// Uses UpdatesPerSecond=2 so each TickSim advances 0.5s of sim time,
    /// making the time-to-build traverse quick and deterministic.</summary>
    [Fact]
    public void Full_funding_plus_build_time_fires_completion_seam_only()
    {
        using var server = new Srv();
        server.Boot(Config(ups: 2));

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "builder");
        bot.SendLogonRequest(Request("builder"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "builder did not seat");
        short sideId = SideOf(server, bot);

        var side = server.Missions[0].Sides.Single(s => s.Id == sideId);
        var bucket = Assert.Single(side.Buckets);
        side.Money = 10_000_000;

        int shipsBefore = server.Ships.Count;
        bot.ResetBucketStatuses();          // drop the join-sync BUCKET_STATUS(money 0)

        // Fully fund in one (exact) donation.
        bot.SendDonate(bucket.Id, bucket.Price);
        int funded = PumpUntil(server, () => bot.BucketStatuses.Count >= 1 && bucket.InvestedMoney >= bucket.Price, 3000);
        Assert.True(funded < 3000, "bucket did not reach full funding");
        Assert.Equal(bucket.Price, bucket.InvestedMoney);
        Assert.Equal(bucket.Price, Assert.Single(bot.BucketStatuses).MoneyTotal);   // the funding broadcast
        bot.ResetBucketStatuses();          // forget the funding broadcast — completion must add NONE

        // Enough build time to complete: at 2 upd/s each tick = 0.5s, so need
        // TimeToBuild seconds -> 2*TimeToBuild ticks; add a generous margin.
        long ticksNeeded = 2L * bucket.TimeToBuild + 50;
        int done = PumpUntil(server, () => server.BucketCompletions.Count >= 1, (int)ticksNeeded);
        Assert.True(done < ticksNeeded, $"bucket {bucket.Id} did not complete after {ticksNeeded} ticks");

        // The completion seam fired exactly once (detection)…
        Assert.Single(server.BucketCompletions);
        Assert.Contains(bucket.Id, server.BucketCompletions);
        Assert.True(bucket.Completed);

        // …and NOTHING else happened: no object spawned, no further broadcast
        // (the completion emitted no BUCKET_STATUS — the list is still empty).
        Assert.Equal(shipsBefore, server.Ships.Count);
        Assert.Empty(bot.BucketStatuses);

        Console.WriteLine($"BUCKETCOMPLETE-LIVE: bucket {bucket.Id} fully funded ({bucket.InvestedMoney}/{bucket.Price}) + {bucket.TimeToBuild}s build -> completion seam fired once; no object spawned, no further broadcast");
    }
}
