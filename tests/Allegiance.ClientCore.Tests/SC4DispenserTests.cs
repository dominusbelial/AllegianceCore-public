using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Collision;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class SC4LoadoutTests
{
    [SkippableTheory]
    [InlineData((short)25)] [InlineData((short)3)]
    public void Starting_research_fleet_has_legal_probe_scouts_and_mine_carrying_fallbacks(short civ)
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        var state = new ResearchState(new(core, civ));
        var shop = new BotManager.LoadoutShop(civ, state.Tree.Civ.InitialStationTypeId, state.Techs, 1_000_000);
        var scout = BotManager.PickTechLoadout(shop, Showcase.HullRole.Scout)!;
        var fighter = BotManager.PickTechLoadout(shop, Showcase.HullRole.Fighter, defensiveMines: true)!;
        Assert.True(TechLoadoutSelector.Legal(core, civ, shop.Techs, shop.DockType, scout.HullId, scout.Parts));
        Assert.True(TechLoadoutSelector.Legal(core, civ, shop.Techs, shop.DockType, fighter.HullId, fighter.Parts));
        Assert.Contains(scout.Parts, p => TechLoadoutSelector.Part(core, p.PartId).Type is DataPartTypeIGC.ProbeLauncher probe
            && core.ProbeTypes[probe.ExpendableTypeId].ScannerRange > 0);
        Assert.Contains(fighter.Parts, p => Showcase.IsMineLauncher(core, p.PartId));
        Assert.Contains("defensive mine duty", fighter.Reason);
        Assert.Null(BotManager.PickTechLoadout(shop with { Budget = -1 }, Showcase.HullRole.Fighter, defensiveMines: true));
        // Removing the mine prerequisite makes the same policy fall back legally.
        var minePart = fighter.Parts.Single(p => Showcase.IsMineLauncher(core, p.PartId));
        var required = TechTreeBitMask.FromBytes(TechLoadoutSelector.Part(core, minePart.PartId).TtbmRequired);
        var bytes = shop.Techs.ToBytes(); var need = required.ToBytes();
        for (int i = 0; i < bytes.Length; i++) bytes[i] &= (byte)~need[i];
        var locked = BotManager.PickTechLoadout(shop with { Techs = TechTreeBitMask.FromBytes(bytes) }, Showcase.HullRole.Fighter, defensiveMines: true);
        if (locked is not null) Assert.DoesNotContain(locked.Parts, p => p.PartId == minePart.PartId);
    }
}

public sealed partial class BotFixRegressionTests
{
    private static void SC4Inventory(ConnectionSession cs, params ExpandedPartData[] parts)
    {
        cs.World.Apply(new EnterGame());
        cs.World.Apply(new BuyLoadoutAck { HullId = 410, Parts = parts });
        cs.World.PartTypeResolver = id => Catalog.RepresentativeCore().TryBuildExpendableLauncher(id, out var p) ? p : null;
    }
    private static void SC4Use(object bot, ConnectionSession cs, double now) => Call("MaybeUseDispensers", null, bot, cs, now);

    [SkippableFact]
    public void SC4_Chaff_uses_incoming_warning_actual_inventory_and_pacing()
    {
        Require.Artwork(); using var cs = Session(); var bot = Bot(cs);
        var core = Catalog.RepresentativeCore();
        short id = core.Launchers.Keys.First(id => core.TryBuildExpendableLauncher(id, out var p) && p.Type is DataPartTypeIGC.ChaffLauncher);
        var equipped = new ExpandedPartData(id, 3, 0, ExpandedPartData.FullyMounted);
        SC4Inventory(cs, equipped);
        cs.World.Apply(MissileLockUpdate.From(1, 9, 1, Vector3.Zero, "test")); // outgoing lock cannot trigger defense
        SC4Use(bot, cs, 10); Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastChaffAt"));
        cs.World.Apply(MissileTrackWarningUpdate.From(true, 0)); // native warning also arms at zero lock
        SC4Use(bot, cs, 10); Assert.Equal(10, Get<double>(bot, "LastChaffAt"));
        SC4Use(bot, cs, 10.1); Assert.Equal(10, Get<double>(bot, "LastChaffAt"));
        foreach (var rejected in new[] { equipped with { Amount = 0 }, equipped with { MountId = 255 }, equipped with { FractionMounted = 0 } })
        {
            SC4Inventory(cs, rejected); SC4Use(bot, cs, 100);
            Assert.Equal(10, Get<double>(bot, "LastChaffAt"));
        }
        SC4Inventory(cs); Set(bot, "Parts", new[] { equipped }); // stale requested parts do not count
        SC4Use(bot, cs, 100); Assert.Equal(10, Get<double>(bot, "LastChaffAt"));
        SC4Inventory(cs, equipped); cs.World.Apply(new DockedNotify { StationId = 3 });
        SC4Use(bot, cs, 100); Assert.Equal(10, Get<double>(bot, "LastChaffAt"));
        Seat(cs.World); SC4Inventory(cs, equipped); cs.World.Apply(MissileTrackWarningUpdate.From(false, 0));
        SC4Use(bot, cs, 100); Assert.Equal(10, Get<double>(bot, "LastChaffAt"));
        cs.World.Apply(MissileTrackWarningUpdate.From(true, 1));
        SC4Use(bot, cs, 100); Assert.Equal(100, Get<double>(bot, "LastChaffAt"));
    }

    [SkippableFact]
    public void SC4_Probe_scout_covers_contested_sector_and_replaces_destroyed_coverage()
    {
        Require.Artwork(); using var cs = Session(); var bot = Bot(cs);
        SC4Inventory(cs, new ExpandedPartData(81, 2, 0, 240)); Set(bot, "TargetSector", (short?)1);
        SC4Use(bot, cs, 10); Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastProbeAt"));
        Set(bot, "Role", "Scout");
        cs.World.Apply(new ExportProbe { Probe = new() { ProbeId = 90, ProbetypeId = 22, SideId = 0, ClusterId = 1, P0 = Vector3.Zero } });
        SC4Use(bot, cs, 10); Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastProbeAt"));
        cs.World.Apply(new ProbeDestroyed { ProbeId = 90 });
        SC4Use(bot, cs, 10); Assert.Equal(10, Get<double>(bot, "LastProbeAt"));
        SC4Use(bot, cs, 11); Assert.Equal(10, Get<double>(bot, "LastProbeAt"));
        cs.World.Apply(new ExportStation { Station = Station(100, 0, 1) });
        SC4Use(bot, cs, 30); Assert.Equal(10, Get<double>(bot, "LastProbeAt")); // quiet home is not contested
        DefenseEnemy(cs.World);
        SC4Use(bot, cs, 30); Assert.Equal(30, Get<double>(bot, "LastProbeAt"));
        SC4Inventory(cs, new ExpandedPartData(19, 2, 0, 240)); // rescue is not a scanner probe
        SC4Use(bot, cs, 100); Assert.Equal(30, Get<double>(bot, "LastProbeAt"));
        Call("ResetNewLife", null, bot, 101.0, true);
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastProbeAt"));
        Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastChaffAt"));
    }

    [SkippableFact]
    public void SC4_Mines_defend_owned_alephs_and_retain_strict_native_enemy_range()
    {
        Require.Artwork(); using var cs = Session(); var bot = Bot(cs);
        SC4Inventory(cs, new ExpandedPartData(59, 5, 0, 240));
        Set(bot, "WingmanBehaviour", Showcase.WbbmUseMines);
        SC4Use(bot, cs, 10); Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));
        cs.World.Apply(new ExportWarp { Warp = new(20, 21, 60, "", "", "gate", [500, 0, 0], [0, 0, -1], [0, 0, 0, 1], 0, 1) });
        SC4Use(bot, cs, 10); Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));
        cs.World.Apply(new ExportStation { Station = Station(100, 0, 1) });
        // Prox Mine 1 (part 59, 15 s) is not pre-laid at an empty owned aleph; only a
        // long-lived field is (Caltrop, part 830, 600 s — Showcase.PreLayMineAtAleph).
        SC4Use(bot, cs, 10); Assert.Equal(double.NegativeInfinity, Get<double>(bot, "LastMineAt"));
        SC4Inventory(cs, new ExpandedPartData(830, 5, 0, 240));
        SC4Use(bot, cs, 10); Assert.Equal(10, Get<double>(bot, "LastMineAt"));
        SC4Use(bot, cs, 10.5); Assert.Equal(10, Get<double>(bot, "LastMineAt"));
        cs.World.Apply(new ExportMine { Mine = new() { MineId = 91, SideId = 0, ClusterId = 1, P0 = Vector3.Zero } });
        SC4Use(bot, cs, 20); Assert.Equal(10, Get<double>(bot, "LastMineAt"));
        cs.World.Apply(new MineDestroyed { MineId = 91 });
        Set(bot, "WingmanBehaviour", (short)0);
        SC4Use(bot, cs, 20); Assert.Equal(10, Get<double>(bot, "LastMineAt"));
        Set(bot, "WingmanBehaviour", Showcase.WbbmUseMines);
        cs.World.Apply(new ExportStation { Station = Station(100, 1, 1) });   // same id → sector no longer home
        // P877 — an enemy NOT astern (abeam/ahead of the identity nose −Z) no
        // longer triggers the native undirected drop; the 2500 u range check is
        // retained only inside the astern pursuit decision.
        DefenseEnemy(cs.World, x: 2500);
        SC4Use(bot, cs, 20); Assert.Equal(10, Get<double>(bot, "LastMineAt"));
        // P877 — a PURSUER dead astern (+Z) within range does drop (escape
        // mining; the fixture pose's zero hull fraction passes the danger gate).
        AsternEnemy(cs.World, 9, 2499);
        SC4Use(bot, cs, 20); Assert.Equal(20, Get<double>(bot, "LastMineAt"));
    }

    /// <summary>P877 — an enemy ship dead astern of the identity-orientation
    /// fixture bot (nose −Z, so astern is +Z), holding velocity.</summary>
    private static void AsternEnemy(ClientWorld world, short id, float z)
    {
        world.Apply(new ExportShip { ShipId = id, SideId = 1, HullId = 15 });
        world.Apply(new SingleShipUpdate { OidTarget = id, Ship = ServerShipUpdate.FromValues(id, 0,
            new ControlData(0, 0, 0, 0), 100u, new Vector3(0, 0, z), Orientation.Identity,
            Vector3.Zero, [0f, 0f, 0f], 1f, 1f, 1f, 1f, 1f, 0f, 1f) });
        world.Apply(new ShipStatusMessage { ShipId = id, HullId = 15, SectorId = 1, StationId = -1 });
    }
}
