using Allegiance.Core;
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
/// P494 (P488b) — the dock AUTO-RESTOCK: when a player-piloted ship docks it
/// (1) has its expendable pools refilled (ammo / fuel / energy / hull / shield)
/// and (2) has each mounted part upgraded to its side-best successor as the
/// side out-techs it, charged P441-style. These are LIVE loopback proofs over a
/// real in-process server + a real TCP bot, exactly like DockDockingTests (the
/// dock itself) and BuyLoadoutPricingLiveTests (the P441 pricing the charge
/// composes):
///   (1) a docked ship's drained pools are restored to full on the dock event;
///   (2) after the side researches a successor's tech, docking replaces the
///       mounted part with the successor AND debits the wallet the P441 ship-
///       value differential, side-wide MONEY_CHANGE — while BEFORE the research
///       the same dock leaves the part (and the wallet) untouched.
///
/// The successor part chain is discovered from the LIVE PCore001 core at run
/// time (the PW-Lancer-style weapon families, same discovery technique as
/// SuccessorUpgradeTests), so a core re-version that renumbers parts stays
/// green. The ship is spawned OUTSIDE the garrison's dock radius so the test
/// controls exactly when the dock transition fires (move inside the radius →
/// pump → the sim dock pass raises the event); the second dock is driven by
/// clearing <see cref="Ship.StationId"/> (the latch) and pumping again — the
/// pass re-docks a same-side ship still inside the radius.
///
/// FAIL-FIRST: both tests are red when RestockOnDock is not wired into
/// OnShipDocked (the pools stay drained / the mounted part is never replaced
/// and the wallet is never debited).
/// </summary>
[Collection("LiveNetwork")]
public sealed class DockAutoRestockLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int ups = 20) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "P494 Dock Restock", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 561u, DwCookie = 0x561u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 5660000000000000000ul, DrmHash = "dckrestock",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Vector3 StationPos(DataStationIGC st) =>
        new(st.Position[0], st.Position[1], st.Position[2]);

    /// <summary>Spawn the bot's ship OUTSIDE the first friendly garrison's dock
    /// radius, on the garrison's side — so the test, not the boot sequence,
    /// decides when the dock event fires (move into the radius, then pump).
    /// Mirrors DockDockingTests' <c>SpawnAt</c> shape (<c>(Ship, Sector)</c>,
    /// placement inside).</summary>
    private static (Ship Ship, Sector Sector) OutsideGarrison(Mission mission, short shipId)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        var sector = mission.Sectors.First(s => s.Stations.Contains(garrison));
        var ship = new Ship(shipId, hull, StationPos(garrison) + new Vector3(Sector.DockRadius + 50f, 0f, 0f),
                            Vector3.Zero, Orientation.Identity);
        ship.SideId = garrison.SideId;
        mission.PlaceShip(ship, sector);
        return (ship, sector);
    }

    /// <summary>Resolve the docked garrison + its side for the bot's ship's
    /// mission (the first real-sided station), the P441 context helper.</summary>
    private static (DataStationIGC Station, DataStationTypeIGC St, Side Side) GarrisonOf(Mission mission)
    {
        var garrison = mission.Sectors.SelectMany(s => s.Stations).First(st => st.SideId >= 0);
        if (!mission.Core.StationTypes.TryGetValue(garrison.StationTypeId, out var st))
            throw new InvalidOperationException("garrison station type unresolvable");
        var side = mission.Sides.First(s => s.Id == garrison.SideId);
        return (garrison, st, side);
    }

    /// <summary>Discover a real PCore001 part chain where, under the side's
    /// <summary>Discover a real PCore001 part chain where, under the side's
    /// <em>current</em> tech (building | base), the successor is NOT yet buyable
    /// (so the pre-research dock upgrades nothing) but once the successor's tech
    /// is added it IS (so the post-research dock upgrades to the leaf). The two
    /// sources of a mounted part are scanned: the part-types table AND the real
    /// launchers (core.Launchers built into their mountable form).</summary>
    private static (DataPartTypeIGC Base, DataPartTypeIGC Leaf) FindUpgradeChain(Mission mission, Side side)
    {
        var core = mission.Core;
        var bases = core.PartTypes.Values.Where(p => p.SuccessorPartId >= 0).ToList();
        foreach (var launcherId in core.Launchers.Keys)
            if (core.TryBuildExpendableLauncher(launcherId, out var lp) && lp.SuccessorPartId >= 0)
                bases.Add(lp);

        foreach (var basePt in bases)
        {
            var beforeTech = side.BuildingTechs.Or(TechTreeBitMask.FromBytes(basePt.TtbmRequired));
            if (SuccessorUpgrade.PartSuccessor(core, basePt, null, beforeTech).PartId != basePt.PartId)
                continue;   // would already upgrade pre-research — not our test

            var afterTech = beforeTech;
            var leaf = basePt;
            var seen = new HashSet<short> { basePt.PartId };
            short? next = basePt.SuccessorPartId;
            while (next is { } id && (core.PartTypes.TryGetValue(id, out var cand) || core.TryBuildExpendableLauncher(id, out cand)) && seen.Add(id))
            {
                afterTech = afterTech.Or(TechTreeBitMask.FromBytes(cand.TtbmRequired));
                leaf = cand;
                next = cand.SuccessorPartId;
            }
            if (leaf.PartId == basePt.PartId) continue;
            if (SuccessorUpgrade.PartSuccessor(core, basePt, null, afterTech).PartId != leaf.PartId) continue;

            Console.WriteLine($"P494-CHAIN: '{basePt.Name}' ({basePt.PartId}, {basePt.Price}) -> '{leaf.Name}' ({leaf.PartId}, {leaf.Price})");
            return (basePt, leaf);
        }
        throw new InvalidOperationException($"no part-upgrade chain found in PCore001 (bases={bases.Count})");
    }

    /// <summary>A real PCore001 PAID launcher (built from core.Launchers) — the
    /// re-buy of a paid desired part is what makes the dock restock's P441
    /// charge nonzero on this core (all part SUCCESSORS are free — measured,
    /// see the report — so a successor upgrade alone charges 0).</summary>
    private static DataPartTypeIGC PaidLauncher(StaticCore core)
    {
        foreach (var launcherId in core.Launchers.Keys)
            if (core.TryBuildExpendableLauncher(launcherId, out var lp) && lp.Price > 0)
                return lp;
        throw new InvalidOperationException("PCore001 has no paid launcher");
    }

    private static Ship LiveShip(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Ship;

    private static void Fund(Srv server, short shipId, int amount) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money = amount;

    private static int WalletOf(Srv server, short shipId) =>
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.Money;

    /// <summary>⭐ A docked ship's drained expendable pools are restored to full
    /// on the dock transition — port of native's dock/carrier pool reset
    /// (SetAmmo(SHRT_MAX)/SetFuel(FLT_MAX)/SetEnergy(MaxEnergy)/SetFraction(1.0),
    /// FedSrv.CPP:12947-12955), running on OnShipDocked. Red on unfixed code:
    /// the drained pools stay drained.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docked_ship_get_its_expendables_refilled()
    {
        Require.Artwork();
        using var server = new Srv();
        DataStationIGC garrison = default;
        server.ShipSpawner = (core, mission, shipId) => OutsideGarrison(mission, shipId);
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "refill_probe");
        bot.SendLogonRequest(Request("refill_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        garrison = GarrisonOf(LiveShip(server, shipId).Mission!).Station;

        // The ship is outside the dock radius (control point) — not yet docked.
        var ship = LiveShip(server, shipId);
        Assert.Null(ship.StationId);

        // Drain every pool; nothing has docked yet, so they stay drained.
        ship.Ammo = 0;
        ship.SetFuel(0f);
        ship.Energy = 0f;
        ship.SetFraction(0.25f);
        ship.SetShieldFraction(0f);
        short maxAmmo = ship.HullType.MaxAmmo;
        short maxFuel = (short)ship.HullType.MaxFuel;
        float maxEnergy = ship.EffectiveMaxEnergy();

        // Cross the dock radius → the next tick's dock pass raises the dock
        // event → RestockOnDock refills the pools.
        ship.Position = StationPos(garrison) + new Vector3(15f, 0f, 0f);
        PumpUntil(server, () => LiveShip(server, shipId).StationId is not null, 200);

        var after = LiveShip(server, shipId);
        Assert.NotNull(after.StationId);
        Assert.Equal(maxAmmo, after.Ammo);                              // SetAmmo(SHRT_MAX)
        Assert.Equal(maxFuel, after.Fuel);                              // SetFuel(FLT_MAX)
        Assert.Equal(maxEnergy, after.Energy, 3);                       // SetEnergy(MaxEnergy)
        Assert.Equal(1.0f, after.Fraction, 3);                          // SetFraction(1.0)
        Assert.Equal(1.0f, after.ShieldFraction, 3);                    // pshield->SetFraction(1.0)
    }

    /// <summary>⭐ Before the side researches a successor's tech, docking leaves
    /// the mounted part (and the wallet) untouched; once the side researches it,
    /// docking replaces the mounted part with the successor and debits the P441
    /// ship-value differential side-wide. Red on unfixed code: the part is never
    /// replaced and the wallet is never debited.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docking_upgrades_mounted_part_to_successor_and_charges_after_research()
    {
        Require.Artwork();
        using var server = new Srv();
        DataStationIGC garrison = default;
        server.ShipSpawner = (core, mission, shipId) => OutsideGarrison(mission, shipId);
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "upgrade_probe");
        bot.SendLogonRequest(Request("upgrade_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        var ship = LiveShip(server, shipId);
        garrison = GarrisonOf(ship.Mission!).Station;
        var mission = ship.Mission!;
        var side = mission.Sides.First(s => s.Id == ship.SideId);
        var (basePt, leaf) = FindUpgradeChain(mission, side);
        // H-07: a Miner cannot accept this weapon upgrade; use real compatible hardpoints.
        ship.ChangeHull(mission.Core.HullTypes.Values.First(h => h.CanMountPart(basePt, 0) && h.CanMountPart(leaf, 0)));

        // Mount ONLY the base part (clear any spawner default) and seed the
        // side's tech to exactly the base's requirement. Wallet funded well above
        // any coming charge so an affordable upgrade is never spuriously skipped.
        ship.ClearLoadout();
        var mounted = ship.MountPart(basePt, 1, 0, byte.MaxValue);
        Assert.NotNull(mounted);
        side.DevelopmentTechs = TechTreeBitMask.FromBytes(basePt.TtbmRequired);
        int wallet0 = 1_000_000;
        Fund(server, shipId, wallet0);

        // Dock #1 — the side has NOT researched the successor: the mounted part
        // must stay the base and the wallet must be untouched (P441 differential
        // 0). This is the fail-first "before the research" baseline.
        ship.Position = StationPos(garrison) + new Vector3(15f, 0f, 0f);
        PumpUntil(server, () => LiveShip(server, shipId).StationId is not null, 200);
        Assert.Equal(basePt.PartId, LiveShip(server, shipId).Parts.Single().Type.PartId);
        Assert.Equal(wallet0, WalletOf(server, shipId));

        // Research the successor (the "side researches an upgrade"): OR the whole
        // chain's required tech into the side's development techs.
        var researched = side.DevelopmentTechs;
        var walk = basePt; var seen = new HashSet<short> { basePt.PartId };
        short? nxt = basePt.SuccessorPartId;
        while (nxt is { } id && mission.Core.PartTypes.TryGetValue(id, out var cand) && seen.Add(id))
        {
            researched = researched.Or(TechTreeBitMask.FromBytes(cand.TtbmRequired));
            walk = cand; nxt = cand.SuccessorPartId;
        }
        side.DevelopmentTechs = researched;

        // Undock (drop the latch) and pump — the same-side ship is still inside
        // the radius, so the dock pass fires a SECOND dock event, now under the
        // researched tech.
        LiveShip(server, shipId).StationId = null;
        PumpUntil(server, () => LiveShip(server, shipId).StationId is not null, 200);

        // The mounted part is upgraded to the furthest-buyable successor.
        var after = LiveShip(server, shipId);
        var afterPart = after.Parts.Single();
        Assert.Equal(leaf.PartId, afterPart.Type.PartId);               // upgraded to the successor
        Assert.Equal(leaf.Name, afterPart.Type.Name);

        // CHARGE — P441's ship-value differential. PCore001's part-successor
        // chains ALL preserve their price exactly (measured: every successor in
        // the 105 base+launcher chains is price-identical to its base, i.e. free),
        // so the faithful native restock charge here is exactly the differential,
        // which is 0 — the wallet is untouched and no MONEY_CHANGE fires. The
        // companion test below proves the charge path produces a REAL debit when
        // a paid desired part is re-bought.
        Assert.Equal(wallet0, WalletOf(server, shipId));   // free successor → 0 charge
        Assert.DoesNotContain(bot.MoneyChanges, mc => mc.SidTo == shipId && mc.DMoney < 0);
    }

    /// <summary>⭐ A real, positive dock-restock CHARGE: when the player's
    /// desired loadout holds a paid launcher the ship no longer carries, docking
    /// re-buys it (upgraded to its successor) and debits the P441 ship-value
    /// differential side-wide. This is the half of the brief's "charges the
    /// native amount" that the free PCore001 part-successors alone cannot show —
    /// a paid DESIRED re-buy is what moves money. Red on unfixed code: the paid
    /// launcher is never re-mounted and the wallet is never debited.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Docking_rebuys_paid_desired_launcher_and_charges_the_native_amount()
    {
        Require.Artwork();
        using var server = new Srv();
        DataStationIGC garrison = default;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            // H-07 needs a real torpedo-compatible capital hull. P550's
            // CapLand home supplies a legal, deterministic capital dock.
            var home = P550TestDockHome.CapLandHome(mission, mission.Sides[0].Id);
            var sector = mission.Sectors.First(s => s.Stations.Contains(home));
            var launcher = PaidLauncher(core);
            var hull = core.HullTypes.Values.First(h => h.CanMountPart(launcher, 0));
            var ship = new Ship(shipId, hull, StationPos(home) + new Vector3(Sector.DockRadius + 50f, 0, 0),
                Vector3.Zero, Orientation.Identity) { SideId = home.SideId };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var bot = new FltBot();
        bot.Connect(server.BoundPort, "pay_probe");
        bot.SendLogonRequest(Request("pay_probe"));
        short shipId = bot.ShipAssignment is null
            ? (PumpUntil(server, () => bot.ShipAssignment is not null, 4000), bot.ShipAssignment!.Ship.ShipId).Item2
            : bot.ShipAssignment.Ship.ShipId;

        var ship = LiveShip(server, shipId);
        garrison = ship.Sector!.Stations.Single(st => st.Name.StartsWith("CAPHOME", StringComparison.Ordinal));
        var mission = ship.Mission!;

        // The player's DESIRED loadout is a real paid launcher they bought; the
        // ship currently carries NO parts (the launcher is missing). Funding the
        // wallet comfortably above the launcher's price.
        var paid = PaidLauncher(mission.Core);
        ship.ChangeHull(mission.Core.HullTypes.Values.First(h => h.CanMountPart(paid, 0)));
        ship.ClearLoadout();
        server.Ships.First(s => s.Ship.Id == shipId).Wallet.DesiredLoadout = new List<DataPartTypeIGC> { paid };
        int wallet0 = paid.Price * Ship.ClampPartAmount(ship.HullType, mission.Core, paid, short.MaxValue) + 10_000;
        Fund(server, shipId, wallet0);

        // Dock → the restock re-buys the missing paid launcher (upgraded to its
        // same-priced successor) as a mounted part → ShipValue rises by its price
        // → the player is debited that exact P441 differential.
        ship.Position = StationPos(garrison) + new Vector3(15f, 0f, 0f);
        PumpUntil(server, () => LiveShip(server, shipId).StationId is not null, 200);

        var after = LiveShip(server, shipId);
        Assert.NotNull(after.StationId);
        Assert.Equal(paid.PartId, after.Parts.Single().Type.PartId);        // re-bought the desired launcher
        int cost = paid.Price * after.Parts.Single().Amount; // H-08: native launcher price is per round.
        Assert.Equal(wallet0 - cost, WalletOf(server, shipId));        // charged the native amount

        // The side-wide MONEY_CHANGE(60) reached the peer (BroadcastMoneyChangeSideWide).
        PumpUntil(server,
            () => bot.MoneyChanges.Any(mc => mc.SidTo == shipId && mc.DMoney == -cost), 4000);
        Assert.Contains(bot.MoneyChanges, mc => mc.SidTo == shipId && mc.DMoney == -cost);
    }
}
