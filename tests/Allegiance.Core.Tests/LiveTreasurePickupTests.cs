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
/// P123 — live loopback proofs of the cash-treasure pickup mechanic over a
/// real in-process server + real TCP bots:
///   (1) a rescue-capable player ship overlapping a real cash treasure picks it
///       up: the picking side's pooled <see cref="Side.Money"/> rises by the
///       treasure's amount, <c>MONEY_CHANGE(60)</c> broadcasts side-wide (to
///       every peer on the picking side, and to NO peer on the other side),
///       <c>DESTROY_TREASURE(30)</c> broadcasts whole-mission (every peer on
///       every side), and the treasure is removed from its sector (gone on the
///       next tick);
///   (2) a NON-rescue-capable ship overlapping the same treasure does NOT pick
///       it up — no credit, no MONEY_CHANGE, no DESTROY_TREASURE, treasure
///       still present;
///   (3) a real join-sync delivers <c>TREASURE_SETS(89)</c> carrying the real
///       decoded <c>StaticCore.TreasureSets</c> catalog (chance blob omitted).
/// The codecs round-trip byte-exact in Wire.TreasureWireTests.
/// </summary>
[Collection("LiveNetwork")]
public class LiveTreasurePickupTests
{
    private const string ArtRoot = "/home/domingo/Development/Artwork";

    // XXV_simple (measured, P111 report): 1052 = no-station fly sector.
    private const short FlySector = 1052;

    // A far offset so the bystander/outsider never accidentally overlap the
    // treasure while still sharing the same (no-station) sector.
    private static readonly Vector3 FarAway = new(1000f, 990f, 985f);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Treasure Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 931u, DwCookie = 0x931u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 9320000000000000000ul, DrmHash = "treasure",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>A real cash amount from the decoded PCore001 catalog: the first
    /// <c>Cash</c> entry's <c>treasureID</c> (the dollar value, 500..10000 per
    /// the DataTreasureSetIGC corpus validation) — a measured amount, not a
    /// fabricated one.</summary>
    private static short CashAmount(Srv server)
    {
        var mission = server.Missions[0];
        foreach (var ts in mission.Core.TreasureSets.Values)
            foreach (var e in ts.Entries)
                if (e.Code == TreasureCode.Cash)
                    return (short)e.TreasureId;
        throw new InvalidOperationException("no Cash entry in PCore001 treasure catalog");
    }

    private sealed class PickupSpawn
    {
        public Ship Pickup = null!, Bystander = null!, Outsider = null!;
        public short Amount;
    }

    /// <summary>Three-ship spawn injection: the pickup ship (hull cloned with
    /// exactly <paramref name="pickupCaps"/>) on side 0 parked at
    /// <paramref name="pickupPos"/>, a same-side bystander (side 0, no pickup
    /// caps, far away) and a cross-side outsider (side 1, far away). Roles are
    /// captured on the <see cref="PickupSpawn"/> as they are built.</summary>
    private static PickupSpawn InjectSpawn(Srv server, short pickupCaps, Vector3 pickupPos)
    {
        var sp = new PickupSpawn();
        int n = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var fly = mission.Sectors.First(s => s.Id == FlySector);
            var baseHull = core.HullTypes.Values.OrderBy(h => h.HullId).First(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord));
            Ship ship;
            if (n == 0)
                ship = new Ship(shipId, baseHull with { HabmCapabilities = pickupCaps }, pickupPos, Vector3.Zero, Orientation.Identity) { SideId = 0 };
            else if (n == 1)
                ship = new Ship(shipId, baseHull with { HabmCapabilities = 0 }, FarAway, Vector3.Zero, Orientation.Identity) { SideId = 0 };
            else
                ship = new Ship(shipId, baseHull with { HabmCapabilities = 0 }, FarAway, Vector3.Zero, Orientation.Identity) { SideId = 1 };
            n++;
            mission.PlaceShip(ship, fly);
            if (n == 1) sp.Pickup = ship;
            else if (n == 2) sp.Bystander = ship;
            else sp.Outsider = ship;
            return (ship, fly);
        };
        return sp;
    }

    /// <summary>Place a real cash treasure in the fly sector at
    /// <paramref name="pos"/>, with the corpus-measured cash amount and a long
    /// lifespan (this slice never expires treasures). Returns it so the test can
    /// assert its presence/absence in the sector.</summary>
    private static Treasure PlaceCashTreasure(Srv server, Vector3 pos, short amount, short oid)
    {
        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        var data = new DataTreasureIGC(
            P0: [pos.X, pos.Y, pos.Z],
            V0: [0f, 0f, 0f],
            Lifespan: 9999f,
            Time0: 0u,
            ObjectId: oid,
            TreasureId: amount,       // native sets amount = treasureID for cash (treasureIGC.cpp:91-92)
            ClusterId: FlySector,
            Amount: amount,
            TreasureCode: TreasureCode.Cash,
            CreateNow: true);
        var t = new Treasure(oid, fly, data, 0.0);
        fly.Add(t);
        return t;
    }

    private static bool SectorHasTreasure(Srv server, short oid) =>
        server.Missions[0].Sectors.First(s => s.Id == FlySector).Models.OfType<Treasure>().Any(t => t.Id == oid);

    private static FltBot[] Seat(Srv server, params string[] names)
    {
        var bots = names.Select(name => { var b = new FltBot(); b.Connect(server.BoundPort, name); b.SendLogonRequest(Request(name)); return b; }).ToArray();
        PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 6000);
        return bots;
    }

    // ── Scenario 1: rescue-capable ship picks up a cash treasure ───────────

    [Fact]
    public void Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it()
    {
        using var server = new Srv();
        var sp = InjectSpawn(server, DataHullTypeIGC.HabmRescue, Vector3.Zero);
        server.Boot(Config());

        var bots = Seat(server, "PA", "PB", "PO");

        short amount = CashAmount(server);
        sp.Amount = amount;
        var treasure = PlaceCashTreasure(server, sp.Pickup.Position, amount, 9271);
        Assert.True(SectorHasTreasure(server, treasure.Id), "treasure should be present before pickup");

        foreach (var b in bots) { b.ResetMoneyChanges(); b.ResetDestroyTreasures(); }
        long moneyBefore = server.Missions[0].Sides.Single(s => s.Id == 0).Money;

        int n = PumpUntil(server,
            () => bots[0].DestroyTreasures.Count >= 1 && bots[1].DestroyTreasures.Count >= 1 && bots[2].DestroyTreasures.Count >= 1, 3000);
        Assert.True(n < 3000, "DESTROY_TREASURE(30) did not reach all three peers (whole-mission)");

        // Treasure actually removed from the sector (gone on the next tick).
        Assert.False(SectorHasTreasure(server, treasure.Id), "treasure should be gone after pickup");

        // Pooled side credit = the treasure's real cash amount.
        long moneyAfter = server.Missions[0].Sides.Single(s => s.Id == 0).Money;
        Assert.Equal(amount, moneyAfter - moneyBefore);

        // MONEY_CHANGE(60): side-wide — both same-side peers, NA sidFrom, sidTo = pickup ship.
        foreach (var sameSide in new[] { bots[0], bots[1] })
        {
            var mc = Assert.Single(sameSide.MoneyChanges);
            Assert.Equal(amount, mc.DMoney);
            Assert.Equal(sp.Pickup.Id, mc.SidTo);
            Assert.Equal((short)-1, mc.SidFrom);   // NA
        }
        // The cross-side peer gets DESTROY_TREASURE (whole-mission) but NO MONEY_CHANGE (side-wide).
        Assert.Empty(bots[2].MoneyChanges);

        // DESTROY_TREASURE(30): whole-mission — all three peers got the same frame.
        foreach (var b in bots)
        {
            var dt = Assert.Single(b.DestroyTreasures);
            Assert.Equal(treasure.Id, dt.TreasureId);
            Assert.Equal(FlySector, dt.SectorId);
        }

        Console.WriteLine($"TREASURE-LIVE: ship {sp.Pickup.Id} (rescue) picked cash amount={amount} -> side0 money {moneyBefore}+{amount}={moneyAfter}; MONEY_CHANGE(60) to 2 same-side peers (sidFrom=NA), DESTROY_TREASURE(30) to all 3 peers; treasure {treasure.Id} removed");
    }

    // ── Scenario 2: non-rescue ship does NOT pick up ──────────────────────

    [Fact]
    public void Non_rescue_ship_overlapping_cash_treasure_leaves_it_untouched()
    {
        using var server = new Srv();
        var sp = InjectSpawn(server, 0 /* no capabilities — not rescue-capable */, Vector3.Zero);
        server.Boot(Config());

        Seat(server, "NR", "NB");

        short amount = CashAmount(server);
        sp.Amount = amount;
        var treasure = PlaceCashTreasure(server, sp.Pickup.Position, amount, 9280);
        Assert.True(SectorHasTreasure(server, treasure.Id), "treasure should be present");

        long moneyBefore = server.Missions[0].Sides.Single(s => s.Id == 0).Money;

        // Several ticks with the (non-rescue) ship overlapping the treasure.
        for (int i = 0; i < 5; i++) server.TickSim();

        // No pickup: money unchanged, no MONEY_CHANGE, no DESTROY_TREASURE, treasure still present.
        Assert.Equal(moneyBefore, server.Missions[0].Sides.Single(s => s.Id == 0).Money);
        Assert.True(SectorHasTreasure(server, treasure.Id), "treasure should still be present");

        Console.WriteLine($"TREASURE-LIVE-NEG: non-rescue ship over cash amount={amount} -> no credit (side0 still {moneyBefore}), no MONEY_CHANGE, no DESTROY_TREASURE; treasure {treasure.Id} still present");
    }

    // ── Scenario 3: join-sync TREASURE_SETS catalog ───────────────────────

    [Fact]
    public void Joining_a_mission_receives_treasure_sets_with_the_real_catalog()
    {
        using var server = new Srv();
        server.Boot(Config());

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "SYNC");
        bot.SendLogonRequest(Request("SYNC"));
        int seat = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(seat < 4000, "joiner did not seat");

        int seen = PumpUntil(server, () => bot.TreasureSetsReceived.Count >= 1, 3000);
        Assert.True(seen < 3000, "TREASURE_SETS(89) never reached the joining bot");

        var ts = Assert.Single(bot.TreasureSetsReceived);
        var catalog = server.Missions[0].Core.TreasureSets;
        Assert.Equal(catalog.Count, ts.Sets.Length);

        // Spot-check that the wire records mirror the decoded catalog (chance
        // blob omitted — the Count field is the entry count, chance not shipped).
        foreach (var rec in ts.Sets)
        {
            Assert.True(catalog.TryGetValue(rec.TreasureSetId, out var cs), $"set {rec.TreasureSetId} missing from catalog");
            Assert.Equal(cs.Name, rec.Name);
            Assert.Equal(cs.Entries.Length, rec.Count);
            Assert.Equal(cs.BZoneOnly, rec.BZoneOnly);
        }

        Console.WriteLine($"TREASURESETS-LIVE: joiner -> TREASURE_SETS(89) {ts.Sets.Length} set(s) mirroring StaticCore.TreasureSets ({catalog.Count})");
    }
}
