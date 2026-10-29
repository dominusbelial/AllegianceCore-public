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
/// P189 — the tech-tree Stage 1 slice: the <see cref="TechTreeBitMask"/> and
/// <see cref="GlobalAttributeSet"/> value types, per-side
/// <see cref="Side.DevelopmentTechs"/>/<see cref="Side.GlobalAttributes"/>
/// seeded from the civ, the additive development-bucket catalog, and the
/// development-completion wire chain (<c>DEV_COMPLETED(8)</c> /
/// <c>SIDE_TECH_CHANGE(75)</c> to the side's own group, gated on Stage ==
/// Started; <c>SIDE_ATTRIBUTE_CHANGE(76)</c> whole-mission, unconditional) —
/// the port of native <c>FedSrvSiteBase::DevelopmentCompleted</c>
/// (<c>FedSrv.CPP:11259-11276</c>) with the 
/// <c>GlobalAttributeSet::Apply</c> multiply semantics (<c>igc.h:935-946</c>).
/// Real corpus data only: the developments used here are verified against
/// PCore001.igc —
///   dev 135 'GA: Ship Acceleration 1': non-tech-only (gas[1]=1.1), 1 effect
///     bit <em>not</em> in either test civ's base techs (⇒ introduces a new
///     bit and fires SIDE_TECH_CHANGE);
///   dev 2 'Upgrade Supremacy': tech-only, 1 effect bit not in base techs;
///   dev 22 'Patroller': tech-only, effect bit already a subset of civ 1's
///     base techs (⇒ obsolete for civ 1, per IsObsolete).
/// </summary>
[Collection("LiveNetwork")]
public class TechTreeDevelopmentLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int ups = 1) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "Tech Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static Cfg ConfigTwoGames(int ups = 1) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "Tech A", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
            new Gm { Name = "Tech B", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Gamma", "Delta"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 909u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8840000000000000000ul, DrmHash = "techtree",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Connect + logon + pump one bot to a seated ship, one at a time, so
    /// the ShipSpawner's invocation order (hence the routed mission/side) is
    /// deterministic rather than dependent on asynchronous seating races.</summary>
    private static void SeatOne(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
    }

    private static short SideOf(Srv server, FltBot bot)
    {
        var shipId = bot.ShipAssignment!.Ship.ShipId;
        return server.Ships.Single(s => s.Ship.Id == shipId).Ship.SideId!.Value;
    }

    /// <summary>Seat peers 0/1 on mission-0 side 0 and peer 2 on mission-0 side 1
    /// — so a dev completing on side 0 has a same-side mate and a same-mission
    /// cross-side observer (outsider) to distinguish the side-group broadcasts
    /// (DEV_COMPLETED / SIDE_TECH_CHANGE) from the whole-mission one
    /// (SIDE_ATTRIBUTE_CHANGE).</summary>
    private static void InjectSingleMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            short sideIdx = (short)(seq < 2 ? 0 : 1);
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 750f, 910f), Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[sideIdx].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            seq++;
            return (ship, mission.Sectors[0]);
        };
    }

    /// <summary>Route peer 0 → mission 0 / side 0, peer 1 → mission 0 / side 1,
    /// peer 2 → mission 1 / side 0, so a dev completing on mission-0 side-0 has
    /// a cross-side observer in the SAME mission (mission-0 side-1) and a peer
    /// in a DIFFERENT mission (mission 1) — distinguishing the whole-mission
    /// broadcast (both mission-0 peers) from cross-mission leak (mission-1
    /// peer must stay silent).</summary>
    private static void InjectMultiMissionSpawn(Srv server)
    {
        int seq = 0;
        server.ShipSpawner = (core, _, shipId) =>
        {
            var m = seq == 2 ? server.Missions[1] : server.Missions[0];
            short sideIdx = seq == 1 ? (short)1 : (short)0;
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            var ship = new Ship(shipId, hull, new Vector3(300f, 750f, 910f), Vector3.Zero, Orientation.Identity);
            ship.SideId = m.Sides[sideIdx].Id;
            m.PlaceShip(ship, m.Sectors[0]);
            seq++;
            return (ship, m.Sectors[0]);
        };
    }

    /// <summary>Find the development bucket wrapping <paramref name="devId"/> on
    /// the given side (the catalog appends one BitDevelopment bucket per
    /// non-obsolete development, in dev-id order).</summary>
    private static Bucket DevBucket(Side side, short devId) =>
        side.Buckets.Single(b => b.ItemType == BucketItemType.BitDevelopment
                                 && b.Development is { } dd && dd.DevelopmentId == devId);

    /// <summary>P551 — the native <c>Side::CanBuy</c> gate (FedSrv.CPP:7152)
    /// only admits funding a development whose required techs the side already
    /// holds. A real player researches prerequisites first; these tests simulate
    /// that by OR-ing the bucket's development required-techs into the side's
    /// <see cref="Side.DevelopmentTechs"/> before funding, so the donation is
    /// admitted exactly as it would be for a researched dev.</summary>
    private static void GrantPrereq(Side side, Bucket bucket)
    {
        if (bucket.Development is { } dv)
            side.DevelopmentTechs = side.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(dv.TtbmRequired));
    }

    /// <summary>Fully fund <paramref name="bucket"/> via the <paramref name="funder"/>
    /// bot and pump the sim until the side's bucket catalog shows it completed
    /// (recorded in <c>Server.BucketCompletions</c>).</summary>
    private static void FundAndComplete(Srv server, FltBot funder, Side side, Bucket bucket)
    {
        // P198: fund the donating PLAYER's wallet (the donation gate moved off the
        // side treasury, HandleBucketDonate FedSrv.CPP:7148/7160).
        server.Ships.First(s => s.Ship.Id == funder.ShipAssignment!.Ship.ShipId).Wallet.Money = 10_000_000;
        funder.ResetDevCompleteds(); funder.ResetSideTechChanges(); funder.ResetSideAttributeChanges();
        funder.SendDonate(bucket.Id, bucket.Price);
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(bucket.Id), 600);
        Assert.True(done < 600, $"development bucket {bucket.Id} did not complete after {600} ticks");
    }

    // ── (1) the full completion chain on a non-tech-only development ──────

    /// <summary>⭐ The main must-fail-if-removed proof: completing dev 135
    /// ('GA: Ship Acceleration 1', non-tech-only, gas[1]=1.1) applies its effect
    /// bit to <see cref="Side.DevelopmentTechs"/>, multiplies
    /// <see cref="Side.GlobalAttributes"/>[1] by 1.1 (the native multiply),
    /// broadcasts <c>DEV_COMPLETED(8)</c> and <c>SIDE_TECH_CHANGE(75)</c> to the
    /// side's OWN connections only, and broadcasts <c>SIDE_ATTRIBUTE_CHANGE(76)</c>
    /// to the WHOLE mission (both sides).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completed_development_applies_techs_and_gas_and_broadcasts_scoped_correctly()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        SeatOne(server, a, "FA");   // seq 0 → side 0
        SeatOne(server, b, "FB");   // seq 1 → side 0
        SeatOne(server, c, "FC");   // seq 2 → side 1

        FltBot funder = a, mate = b, outsider = c;
        short funderSide = SideOf(server, funder);
        Assert.Equal(funderSide, SideOf(server, mate));      // same side
        Assert.NotEqual(funderSide, SideOf(server, outsider)); // other side, same mission

        var side = server.Missions[0].Sides.Single(s => s.Id == funderSide);
        var dev = server.Missions[0].Core.Developments[135];
        var bucket = DevBucket(side, dev.DevelopmentId);
        GrantPrereq(side, bucket);   // P551: the side must be able to buy the dev to fund it (CanBuy)
        int initialBits = side.DevelopmentTechs.PopCount;
        float baseGas1 = side.Civilization.GasBaseAttributes[1];
        float expectedGas1 = baseGas1 * dev.Gas[1];    // the native multiply, gas[1]=1.1

        FundAndComplete(server, funder, side, bucket);

        // Tech applied: side gained exactly dev 135's one new effect bit.
        Assert.Equal(initialBits + 1, side.DevelopmentTechs.PopCount);

        // Gas applied (multiplication): [1] scaled by the dev's 1.1.
        Assert.Equal(expectedGas1, side.GlobalAttributes.Get(1), precision: 5);

        // Flush the wire so the async transports deliver before asserting.
        int rx = PumpUntil(server,
            () => funder.DevCompleteds.Count >= 1 && mate.DevCompleteds.Count >= 1 &&
                  funder.SideTechChanges.Count >= 1 && mate.SideTechChanges.Count >= 1 &&
                  funder.SideAttributeChanges.Count >= 1 && mate.SideAttributeChanges.Count >= 1 &&
                  outsider.SideAttributeChanges.Count >= 1, 400);
        Assert.True(rx < 400, "development broadcasts did not reach the expected peers");

        // DEV_COMPLETED + SIDE_TECH_CHANGE to the side's OWN group only…
        var dc = Assert.Single(funder.DevCompleteds);
        Assert.Equal(dev.DevelopmentId, dc.DevId);
        Assert.Equal(dc.DevId, Assert.Single(mate.DevCompleteds).DevId);
        var stc = Assert.Single(funder.SideTechChanges);
        Assert.Equal(side.Id, stc.SideId);
        Assert.Equal(side.DevelopmentTechs.ToBytes(), stc.TtbmDevelopments);
        Assert.Equal(stc.SideId, Assert.Single(mate.SideTechChanges).SideId);
        Assert.Empty(outsider.DevCompleteds);          // side group: outsider excluded
        Assert.Empty(outsider.SideTechChanges);

        // …but SIDE_ATTRIBUTE_CHANGE goes to the WHOLE mission (all three peers).
        Assert.Single(funder.SideAttributeChanges);
        Assert.Single(mate.SideAttributeChanges);
        Assert.Single(outsider.SideAttributeChanges);
        foreach (var sac in new[] { Assert.Single(funder.SideAttributeChanges), Assert.Single(mate.SideAttributeChanges), Assert.Single(outsider.SideAttributeChanges) })
        {
            Assert.Equal(side.Id, sac.SideId);
            Assert.Equal(expectedGas1, sac.GasAttributes[1], precision: 5);
        }

        Console.WriteLine($"DEVTREE-LIVE: dev {dev.DevelopmentId} '{dev.Name}' complete on side {side.Id} -> +1 tech bit, gas[1] {baseGas1:F3}->{expectedGas1:F3}; DEV_COMPLETED(8)+SIDE_TECH_CHANGE(75) to 2 same-side peers (outsider excluded); SIDE_ATTRIBUTE_CHANGE(76) to all 3 mission peers");
    }

    // ── (2) the SIDE_TECH_CHANGE stage gate ───────────────────────────────

    /// <summary>⭐ Regression against the native <c>Stage == STAGE_STARTED</c>
    /// gate: while the mission is <c>Starting</c> (not yet <c>Started</c>), a
    /// completed development applies its techs and broadcasts
    /// <c>DEV_COMPLETED</c> but <em>not</em> <c>SIDE_TECH_CHANGE</c>; once the
    /// stage is <c>Started</c>, the very next completion does send
    /// <c>SIDE_TECH_CHANGE</c>.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side_tech_change_suppressed_before_start_and_sent_after()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        SeatOne(server, bot, "gate");
        short sideId = SideOf(server, bot);
        var side = server.Missions[0].Sides.Single(s => s.Id == sideId);
        var mission = server.Missions[0];

        // Dev 2 (tech-only). Funding requires Stage == Started (the donation gate,
        // HandleBucketDonate FedSrv.CPP:7139), so fund WHILE Started and let the
        // donation be processed (pumped) BEFORE dropping to Starting, so the
        // 120 s build completes while Stage != Started — exercising the
        // SIDE_TECH_CHANGE stage gate.
        var dev2 = mission.Core.Developments[2];
        var b2 = DevBucket(side, dev2.DevelopmentId);
        GrantPrereq(side, b2);   // P551: unlock dev 2 so the side can buy/fund it
        server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Wallet.Money = 10_000_000;  // P198: fund the PLAYER's wallet
        bot.ResetDevCompleteds(); bot.ResetSideTechChanges(); bot.ResetSideAttributeChanges();
        bot.SendDonate(b2.Id, b2.Price);
        int funded = PumpUntil(server, () => b2.InvestedMoney >= b2.Price, 200);
        Assert.True(funded < 200, "dev bucket did not fund while Started");
        mission.Stage = MissionStage.Starting;      // flip only after the donation landed
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(b2.Id), 600);
        Assert.True(done < 600, "dev bucket did not complete while Starting");
        // DEV_COMPLETED is unconditional → delivered; SIDE_TECH_CHANGE is suppressed.
        int rx = PumpUntil(server, () => bot.DevCompleteds.Count >= 1, 400);
        Assert.True(rx < 400, "DEV_COMPLETED not received while Starting");
        Assert.Single(bot.DevCompleteds);
        Assert.Equal(dev2.DevelopmentId, bot.DevCompleteds[0].DevId);
        Assert.Empty(bot.SideTechChanges);          // gate: stage != Started
        Assert.Empty(bot.SideAttributeChanges);     // dev 2 is tech-only → no attribute delta
        int bitsAfterSuppressed = side.DevelopmentTechs.PopCount;

        // Flip to Started; dev 3 (tech-only) now broadcasts SIDE_TECH_CHANGE.
        mission.Stage = MissionStage.Started;
        var dev3 = mission.Core.Developments[3];
        var b3 = DevBucket(side, dev3.DevelopmentId);
        GrantPrereq(side, b3);   // P551: unlock dev 3 so the side can buy/fund it
        server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Wallet.Money = 10_000_000;  // P198: (re)fund the PLAYER's wallet before the second donation
        bot.ResetDevCompleteds(); bot.ResetSideTechChanges(); bot.ResetSideAttributeChanges();
        bot.SendDonate(b3.Id, b3.Price);
        int funded2 = PumpUntil(server, () => b3.InvestedMoney >= b3.Price, 200);
        Assert.True(funded2 < 200, $"dev bucket {b3.Id} did not fund while Started");
        int done2 = PumpUntil(server, () => server.BucketCompletions.Contains(b3.Id), 600);
        Assert.True(done2 < 600, $"dev bucket {b3.Id} did not complete while Started (invested {b3.InvestedMoney}/{b3.Price}, ms {b3.InvestedMilliseconds})");
        int rx2 = PumpUntil(server, () => bot.SideTechChanges.Count >= 1, 400);
        Assert.True(rx2 < 400, "SIDE_TECH_CHANGE not sent once Started");
        var stc = Assert.Single(bot.SideTechChanges);
        Assert.Equal(side.Id, stc.SideId);
        Assert.True(side.DevelopmentTechs.PopCount > bitsAfterSuppressed);
        Assert.Equal(side.DevelopmentTechs.ToBytes(), stc.TtbmDevelopments);

        Console.WriteLine($"DEVGATE-LIVE: dev completed while Starting -> DEV_COMPLETED only, SIDE_TECH_CHANGE suppressed; after Started -> SIDE_TECH_CHANGE(75) side {side.Id} ({side.DevelopmentTechs.PopCount} bits)");
    }

    // ── (3) obsolete developments are never funded/broadcast ──────────────

    /// <summary>⭐ A tech-only development whose effect techs the side already
    /// fully holds (obsolete per <see cref="DevelopmentLogic.IsObsolete"/>) is
    /// <b>omitted</b> from the side's bucket catalog (so it can never be funded
    /// or broadcast anything). If its effects become known later, native AdjustBuckets
    /// marks it complete without DevelopmentCompleted (sideigc.h:629-633): no
    /// DEV_COMPLETED, SIDE_TECH_CHANGE or SIDE_ATTRIBUTE_CHANGE is emitted.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Obsolete_development_is_never_reapplied_or_rebroadcast()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectSingleMissionSpawn(server);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        // Side 0 is civ 1 (Dreghklar), for which dev 22 'Patroller' is obsolete.
        var mission = server.Missions[0];
        var side = mission.Sides[0];
        Assert.Equal((short)1, side.Civilization.CivilizationId);

        // Catalog omits the obsolete dev 22 but keeps the non-obsolete dev 2.
        Assert.Throws<InvalidOperationException>(() => DevBucket(side, 22));
        _ = DevBucket(side, 2);

        // Live "second time": pre-seed dev 2's effect bit into the side's techs,
        // then let native AdjustBuckets reconcile it. Known research is a no-op completion,
        // not a second paid purchase or DevelopmentCompleted event (sideigc.h:629-633).
        using var bot = new FltBot();
        SeatOne(server, bot, "obsolete");
        short botSideId = SideOf(server, bot);
        var botSide = mission.Sides.Single(s => s.Id == botSideId);
        var dev2 = mission.Core.Developments[2];
        var b2 = DevBucket(botSide, dev2.DevelopmentId);

        GrantPrereq(botSide, b2);   // P551: unlock dev 2 so the side can buy/fund it
        botSide.DevelopmentTechs = botSide.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(dev2.TtbmEffects));
        int bitsBefore = botSide.DevelopmentTechs.PopCount;
        int rx = PumpUntil(server, () => b2.Completed && bot.BucketStatuses.Any(b => b.IBucket == b2.Id
            && b.MoneyTotal == b2.Price && b.TimeTotal == (long)b2.TimeToBuild * 1000), 400);
        Assert.True(rx < 400, "known development was not marked complete and synchronized");
        Assert.Equal(bitsBefore, botSide.DevelopmentTechs.PopCount);
        Assert.Equal(b2.Price, b2.InvestedMoney);
        Assert.Equal((long)b2.TimeToBuild * 1000, b2.InvestedMilliseconds);
        Assert.DoesNotContain(b2.Id, server.BucketCompletions);
        Assert.Empty(bot.DevCompleteds);
        Assert.Empty(bot.SideTechChanges);
        Assert.Empty(bot.SideAttributeChanges);

        Console.WriteLine("DEVOBSOLETE-LIVE: known dev 2 marked complete without purchase, completion effects or completion sounds (native AdjustBuckets)");
    }

    // ── (4) TechTreeBitMask / GlobalAttributeSet unit correctness ─────────

    /// <summary>Hand-constructed masks: <see cref="TechTreeBitMask.Or"/> and
    /// <see cref="TechTreeBitMask.IsSubsetOf"/> must match the native
    /// <c>operator |=</c> / <c>operator &lt;=</c> semantics (<c>mask.h:235-241,300-313</c>).</summary>
    [Fact]
    public void TechTreeBitMask_or_and_subset_match_native_semantics()
    {
        static TechTreeBitMask M(params (int byteIdx, byte val)[] bytes)
        {
            var b = new byte[TechTreeBitMask.ByteCount];
            foreach (var (i, v) in bytes) b[i] = v;
            return TechTreeBitMask.FromBytes(b);
        }

        var a = M((0, 0x01));
        var b = M((0, 0x02), (3, 0x80));
        var ab = a.Or(b);
        Assert.Equal(0x03, ab.AsSpan()[0]);
        Assert.Equal(0x80, ab.AsSpan()[3]);
        Assert.Equal(0x00, ab.AsSpan()[1]);

        // Subset: a ⊆ ab; ab is NOT ⊆ a; disjoint masks are not subsets of each other.
        Assert.True(a.IsSubsetOf(ab));
        Assert.False(ab.IsSubsetOf(a));
        Assert.False(a.IsSubsetOf(b));
        Assert.False(b.IsSubsetOf(a));
        // Empty ⊆ everything; equality is a subset both ways.
        Assert.True(TechTreeBitMask.Empty.IsSubsetOf(ab));
        Assert.True(a.IsSubsetOf(a));
        Assert.True(ab.IsSubsetOf(ab));
        // IsSubsetOf must care about ALL 50 bytes (a bit in byte 3 must block it).
        Assert.False(M((0, 0x01)).IsSubsetOf(M((3, 0x80))));
        // Or is immutable: operands unchanged.
        Assert.Equal(0x01, a.AsSpan()[0]);
        Assert.Equal(0x02, b.AsSpan()[0]);
        // Equality.
        Assert.Equal(M((0, 0x05), (49, 0xFF)), M((0, 0x05), (49, 0xFF)));
        Assert.NotEqual(a, b);
    }

    /// <summary><see cref="GlobalAttributeSet.Apply"/> must be a per-element
    /// <b>multiply</b> (the native <c>igc.h:935-946</c> semantics), not an add.</summary>
    [Fact]
    public void GlobalAttributeSet_apply_multiplies_elements()
    {
        var base_ = GlobalAttributeSet.FromArray(new float[]
            { 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f });
        var delta = GlobalAttributeSet.FromArray(new float[]
            { 1f, 1.1f, 1f, 2f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f });
        var r = base_.Apply(delta);
        Assert.Equal(1.1f, r.Get(1));
        Assert.Equal(2f, r.Get(3));
        Assert.Equal(1f, r.Get(0));      // unchanged multiplicatively
        // Base unchanged (immutability).
        Assert.Equal(1f, base_.Get(1));
        Assert.Equal(1f, base_.Get(3));
        // Cumulative: apply again multiplies.
        var r2 = r.Apply(delta);
        Assert.Equal(1.21f, r2.Get(1));
    }

    // ── (5) whole-mission scoping of SIDE_ATTRIBUTE_CHANGE across missions ─

    /// <summary>⭐ Multi-mission: completing a dev on mission A broadcasts
    /// <c>SIDE_ATTRIBUTE_CHANGE</c> to BOTH sides of mission A (whole mission)
    /// but to NO peer of mission B.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Side_attribute_change_broadcasts_whole_mission_but_not_across_missions()
    {
        Require.Artwork();
        using var server = new Srv();
        InjectMultiMissionSpawn(server);
        server.Boot(ConfigTwoGames());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var a = new FltBot();
        using var b = new FltBot();
        using var c = new FltBot();
        SeatOne(server, a, "MA");   // seq 0 → mission A / side 0
        SeatOne(server, b, "MB");   // seq 1 → mission A / side 1
        SeatOne(server, c, "MC");   // seq 2 → mission B / side 0

        var mA = server.Missions[0];
        var mB = server.Missions[1];
        var sideA = mA.Sides[0];
        var dev = mA.Core.Developments[135];
        var bucket = DevBucket(sideA, dev.DevelopmentId);
        GrantPrereq(sideA, bucket);   // P551: unlock dev 135 so the side can buy/fund it

        FundAndComplete(server, a, sideA, bucket);

        // Whole mission A: both its sides' peers got SIDE_ATTRIBUTE_CHANGE…
        int rx = PumpUntil(server,
            () => a.SideAttributeChanges.Count >= 1 && b.SideAttributeChanges.Count >= 1, 400);
        Assert.True(rx < 400, "mission-A peers did not receive SIDE_ATTRIBUTE_CHANGE");
        Assert.Single(a.SideAttributeChanges);
        Assert.Single(b.SideAttributeChanges);
        Assert.Equal(sideA.Id, Assert.Single(a.SideAttributeChanges).SideId);
        Assert.Equal(sideA.Id, Assert.Single(b.SideAttributeChanges).SideId);
        // …but mission B's peer saw none.
        int neg = PumpUntil(server, () => c.SideAttributeChanges.Count >= 1, 1200);
        Assert.True(neg == 1200, "mission-B peer should NOT receive SIDE_ATTRIBUTE_CHANGE");
        Assert.Empty(c.SideAttributeChanges);
        Assert.Empty(c.DevCompleteds);   // mission-B peer got no DEV_COMPLETED either
        // Mission B's side gas/techs are untouched.
        Assert.Equal(mB.Sides[0].Civilization.GasBaseAttributes[1], mB.Sides[0].GlobalAttributes.Get(1));
        Assert.Equal(mB.Research.StartingTechs(mB.Sides[0].Civilization).ToBytes(), mB.Sides[0].DevelopmentTechs.ToBytes());

        Console.WriteLine($"DEVMULTI-LIVE: dev complete on mission-A side {sideA.Id} -> SIDE_ATTRIBUTE_CHANGE(76) to 2 mission-A peers (both sides), 0 mission-B peers");
    }
}
