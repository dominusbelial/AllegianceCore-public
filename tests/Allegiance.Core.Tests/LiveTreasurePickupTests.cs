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
/// P123 — live loopback proofs of the cash-treasure pickup mechanic over a
/// real in-process server + real TCP bots:
///   (1) a rescue-capable player ship overlapping a real cash treasure picks it
///       up: the picking player's WALLET (or autodonate target's — P551) rises
///       by the treasure's amount, <c>MONEY_CHANGE(60)</c> broadcasts side-wide (to
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
    private static readonly string ArtRoot = TestArtRoot.Value;

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

    /// <summary>Place a real powerup treasure in the fly sector: its
    /// <c>TreasureId</c> is the <c>PowerupCode</c> bitmap (native
    /// <c>treasure-&gt;GetTreasureID()</c>), its amount is NA (native
    /// <c>dt.amount = NA</c>, missionigc.cpp:4188).</summary>
    private static Treasure PlacePowerupTreasure(Srv server, Vector3 pos, short bits, short oid)
    {
        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        var data = new DataTreasureIGC(
            P0: [pos.X, pos.Y, pos.Z],
            V0: [0f, 0f, 0f],
            Lifespan: 9999f,
            Time0: 0u,
            ObjectId: oid,
            TreasureId: bits,        // PowerupCode bitmap (native GetTreasureID)
            ClusterId: FlySector,
            Amount: -1,               // NA — native dt.amount = NA (missionigc.cpp:4188)
            TreasureCode: TreasureCode.Powerup,
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

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Rescue_ship_picking_cash_treasure_credits_side_broadcasts_moneychange_and_destroys_it()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectSpawn(server, DataHullTypeIGC.HabmRescue, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bots = Seat(server, "PA", "PB", "PO");
        Assert.All(bots, b => Assert.NotNull(b.ShipAssignment));
        // Spawn roles follow server join order, which can differ from Connect order.
        using var pickerBot = bots.Single(b => b.ShipAssignment!.Ship.ShipId == sp.Pickup.Id);
        using var mateBot = bots.Single(b => b.ShipAssignment!.Ship.ShipId == sp.Bystander.Id);
        using var outsiderBot = bots.Single(b => b.ShipAssignment!.Ship.ShipId == sp.Outsider.Id);

        short amount = CashAmount(server);
        sp.Amount = amount;
        var treasure = PlaceCashTreasure(server, sp.Pickup.Position, amount, 9271);
        Assert.True(SectorHasTreasure(server, treasure.Id), "treasure should be present before pickup");

        foreach (var b in bots) { b.ResetMoneyChanges(); b.ResetDestroyTreasures(); }
        // P551: a PLAYER picking up cash is credited on their own WALLET (or
        // their autodonate target's), NOT the side pot (FedSrv.CPP:13592-13611).
        var side = server.Missions[0].Sides.Single(s => s.Id == 0);
        long sideBefore = side.Money;
        long walletBefore = server.Ships.First(s => s.Ship.Id == sp.Pickup.Id).Wallet.Money;

        int n = PumpUntil(server, () => !SectorHasTreasure(server, treasure.Id), 3000);
        Assert.True(n < 3000, "cash treasure was not picked up");
        // Removal and another message's receipt do not synchronize MONEY_CHANGE.
        // Hold the pickup tick fixed until every asserted recipient decodes it.
        Assert.True(SpinWait.SpinUntil(() => bots.All(b => b.DestroyTreasures.Any(d => d.TreasureId == treasure.Id))
            && new[] { pickerBot, mateBot }.All(b => b.MoneyChanges.Any(m => m.SidTo == sp.Pickup.Id
                && m.DMoney == amount)), TimeSpan.FromSeconds(30)),
            "all peers must decode destruction and both same-side peers must decode cash credit");

        // Treasure actually removed from the sector (gone on the next tick).
        Assert.False(SectorHasTreasure(server, treasure.Id), "treasure should be gone after pickup");

        // P551: the PICKER's WALLET rises by the treasure's real cash amount;
        // the side pool is UNTOUCHED (this is the client/server mismatch fix).
        long walletAfter = server.Ships.First(s => s.Ship.Id == sp.Pickup.Id).Wallet.Money;
        Assert.Equal(amount, walletAfter - walletBefore);
        Assert.Equal(sideBefore, side.Money);

        // MONEY_CHANGE(60): side-wide — both same-side peers, NA sidFrom, sidTo = pickup ship.
        foreach (var sameSide in new[] { pickerBot, mateBot })
        {
            var mc = Assert.Single(sameSide.MoneyChanges);
            Assert.Equal(amount, mc.DMoney);
            Assert.Equal(sp.Pickup.Id, mc.SidTo);
            Assert.Equal((short)-1, mc.SidFrom);   // NA
        }
        // The cross-side peer gets DESTROY_TREASURE (whole-mission) but NO MONEY_CHANGE (side-wide).
        Assert.Empty(outsiderBot.MoneyChanges);

        // DESTROY_TREASURE(30): whole-mission — all three peers got the same frame.
        foreach (var b in bots)
        {
            var dt = Assert.Single(b.DestroyTreasures);
            Assert.Equal(treasure.Id, dt.TreasureId);
            Assert.Equal(FlySector, dt.SectorId);
        }

        Console.WriteLine($"TREASURE-LIVE: ship {sp.Pickup.Id} (rescue) picked cash amount={amount} -> PICKER WALLET {walletBefore}+{amount}={walletAfter} (side pool unchanged at {side.Money}); MONEY_CHANGE(60) to 2 same-side peers (sidFrom=NA), DESTROY_TREASURE(30) to all 3 peers; treasure {treasure.Id} removed");
    }

    // ── Scenario 1c: P758 — ACQUIRE_TREASURE(162) carries native fields ────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Powerup_and_cash_pickups_send_ACQUIRE_TREASURE_with_native_fields_to_the_picker_only()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectSpawn(server, DataHullTypeIGC.HabmRescue, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

        var bots = Seat(server, "PA", "PB", "PO");
        using var pickerBot = bots.Single(b => b.ShipAssignment!.Ship.ShipId == sp.Pickup.Id);
        using var mateBot = bots.Single(b => b.ShipAssignment!.Ship.ShipId == sp.Bystander.Id);
        using var outsiderBot = bots.Single(b => b.ShipAssignment!.Ship.ShipId == sp.Outsider.Id);

        // Powerup: native sends treasure->GetTreasureID() — the PowerupCode
        // bitmap — and treasure->GetAmount() (NA = -1, missionigc.cpp:4188),
        // to the picker's OWN connection only (FedSrv.CPP:13622-13641).
        short bits = (short)(PowerupCode.Fuel | PowerupCode.Ammo);
        var powerup = PlacePowerupTreasure(server, sp.Pickup.Position, bits, 9400);
        foreach (var b in bots) b.ResetAcquireTreasures();
        int n = PumpUntil(server, () => pickerBot.AcquireTreasures.Count >= 1 && !SectorHasTreasure(server, powerup.Id), 3000);
        Assert.True(n < 3000, "powerup ACQUIRE_TREASURE not received in time");
        var at = Assert.Single(pickerBot.AcquireTreasures);
        Assert.Equal((byte)TreasureCode.Powerup, at.TreasureCode);
        Assert.Equal(bits, at.TreasureId);
        Assert.Equal((short)-1, at.Amount);
        Assert.Empty(mateBot.AcquireTreasures);      // not side-wide, not whole-mission
        Assert.Empty(outsiderBot.AcquireTreasures);

        // Cash: native sets amount = treasureID for cash (treasureIGC.cpp:91-92),
        // so both fields are the dollar value, sent to the picker only.
        short amount = CashAmount(server);
        var cash = PlaceCashTreasure(server, sp.Pickup.Position, amount, 9401);
        foreach (var b in bots) b.ResetAcquireTreasures();
        n = PumpUntil(server, () => pickerBot.AcquireTreasures.Count >= 1 && !SectorHasTreasure(server, cash.Id), 3000);
        Assert.True(n < 3000, "cash ACQUIRE_TREASURE not received in time");
        at = Assert.Single(pickerBot.AcquireTreasures);
        Assert.Equal((byte)TreasureCode.Cash, at.TreasureCode);
        Assert.Equal(amount, at.TreasureId);
        Assert.Equal(amount, at.Amount);
        Assert.Empty(mateBot.AcquireTreasures);
        Assert.Empty(outsiderBot.AcquireTreasures);

        Console.WriteLine($"TREASURE-ACQUIRE-LIVE: powerup bits=0x{bits:X2} -> ACQUIRE_TREASURE(162) code=Powerup id=0x{bits:X2} amount=-1; cash {amount} -> code=Cash id={amount} amount={amount}; picker-only both times");
    }

    // ── Scenario 1b: P551 — autodonate target receives the cash credit ─────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cash_treasure_credited_to_autodonate_target_wallet_and_moneychange_agrees()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectSpawn(server, DataHullTypeIGC.HabmRescue, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        // Seat the three bots FIRST — the ShipSpawner only creates sp.Pickup /
        // sp.Bystander / sp.Outsider once a player connects, so we can only
        // resolve their LiveShips after seating.
        using var a = new FltBot(); using var b = new FltBot(); using var c = new FltBot();
        a.Connect(server.BoundPort, "TAA"); a.SendLogonRequest(Request("TAA"));
        b.Connect(server.BoundPort, "TBB"); b.SendLogonRequest(Request("TBB"));
        c.Connect(server.BoundPort, "TCC"); c.SendLogonRequest(Request("TCC"));
        int seatAll = PumpUntil(server, () => a.ShipAssignment is not null && b.ShipAssignment is not null && c.ShipAssignment is not null, 6000);
        Assert.True(seatAll < 6000, "three bots must seat");

        // P551 (FedSrv.CPP:13594-13596): the picker's AUTODONATE target receives
        // the cash, not the picker. Point the picker's autodonate at the
        // same-side bystander.
        var pickerLive = server.Ships.First(s => s.Ship.Id == sp.Pickup.Id);
        var targetLive = server.Ships.First(s => s.Ship.Id == sp.Bystander.Id);
        pickerLive.Wallet.AutoDonateTarget = targetLive.Ship;

        short amount = CashAmount(server);
        sp.Amount = amount;
        var treasure = PlaceCashTreasure(server, sp.Pickup.Position, amount, 9310);
        Assert.True(SectorHasTreasure(server, treasure.Id), "treasure present before pickup");

        foreach (var bt in new[] { a, b, c }) { bt.ResetMoneyChanges(); bt.ResetDestroyTreasures(); }

        long targetWalletBefore = targetLive.Wallet.Money;
        long pickerWalletBefore = pickerLive.Wallet.Money;

        var sameSideBots = new[] { a, b, c }.Where(bt => bt.ShipAssignment!.Ship.ShipId != sp.Outsider.Id).ToArray();
        int n = PumpUntil(server, () => !SectorHasTreasure(server, treasure.Id), 3000);
        Assert.True(n < 3000, "cash treasure was not picked up");
        Assert.True(SpinWait.SpinUntil(() => new[] { a, b, c }.All(bt => bt.DestroyTreasures.Any(d => d.TreasureId == treasure.Id))
            && sameSideBots.All(bt => bt.MoneyChanges.Any(m => m.SidTo == sp.Bystander.Id
                && m.DMoney == amount)), TimeSpan.FromSeconds(30)),
            "all peers must decode destruction and both same-side peers must decode autodonated cash");
        Assert.False(SectorHasTreasure(server, treasure.Id), "treasure gone after pickup");

        // The AUTODONATE TARGET's wallet rises by the amount; the PICKER's does NOT.
        Assert.Equal(amount, targetLive.Wallet.Money - targetWalletBefore);
        Assert.Equal(pickerWalletBefore, pickerLive.Wallet.Money);

        // MONEY_CHANGE sidTo = the autodonate TARGET (FedSrv.CPP:13605), not the
        // picker — and the wire agrees with the server state.
        var pickerBot = new[] { a, b, c }.First(bt => bt.ShipAssignment!.Ship.ShipId == sp.Pickup.Id);
        foreach (var sameSide in new[] { pickerBot, new[] { a, b, c }.First(bt => bt.ShipAssignment!.Ship.ShipId == sp.Bystander.Id) })
        {
            var mc = Assert.Single(sameSide.MoneyChanges);
            Assert.Equal(amount, mc.DMoney);
            Assert.Equal(sp.Bystander.Id, mc.SidTo);
            Assert.Equal((short)-1, mc.SidFrom);
        }
        // Cross-side peer: no MONEY_CHANGE.
        var outsiderBot = new[] { a, b, c }.First(bt => bt.ShipAssignment!.Ship.ShipId == sp.Outsider.Id);
        Assert.Empty(outsiderBot.MoneyChanges);

        Console.WriteLine($"TREASURE-AUTODONATE-LIVE: picker {sp.Pickup.Id} picked cash {amount} with autodonate -> TARGET {sp.Bystander.Id} wallet {targetWalletBefore}+{amount}={targetLive.Wallet.Money}, picker wallet unchanged; MONEY_CHANGE sidTo=target, NA sidFrom, wire agrees with server");
    }

    // ── Scenario 2: non-rescue ship does NOT pick up ──────────────────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_rescue_ship_overlapping_cash_treasure_leaves_it_untouched()
    {
        Require.Artwork();
        using var server = new Srv();
        var sp = InjectSpawn(server, 0 /* no capabilities — not rescue-capable */, Vector3.Zero);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

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

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Joining_a_mission_receives_treasure_sets_with_the_real_catalog()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable/started mission; bypass the ready-up lobby

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
