using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P1014LoadoutRendererTests
{
    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Flag_defaults_on_and_routes_only_the_active_loadout(HudStyle style)
    {
        // Default ON since Domingo approved Loadout V2 (2026-11-16); OFF keeps the native layout.
        var options = new VisualOptions();
        Assert.True(LoadoutV2Presentation.UseV2(options, true));
        options.SetToggle(VisualFlags.LoadoutV2Key, true);
        Assert.True(LoadoutV2Presentation.UseV2(options, true));
        Assert.False(LoadoutV2Presentation.UseV2(options, false));
        options.SetToggle(VisualFlags.LoadoutV2Key, false);
        Assert.False(LoadoutV2Presentation.UseV2(options, true));
        Assert.NotEqual(LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Accent, style),
            LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Muted, style));
    }

    [Fact]
    public void Flag_uses_the_shared_profile_and_launch_override_table()
    {
        var profile = JsonSerializer.Deserialize<VisualOptions>("{\"hangar.loadoutV2\":true}")!;
        Assert.True(profile.IsOn(VisualFlags.LoadoutV2Key));
        var effective = VisualOptions.Resolve(profile, "hangar.loadoutV2=off", out var warnings);
        Assert.Empty(warnings);
        Assert.False(effective.IsOn(VisualFlags.LoadoutV2Key));
        Assert.True(profile.IsOn(VisualFlags.LoadoutV2Key));
        Assert.Contains("\"hangar.loadoutV2\":true", JsonSerializer.Serialize(profile));
        Assert.True(VisualFlags.Find(VisualFlags.LoadoutV2Key)!.DefaultOn);
    }

    private static (ShopModel Shop, LoadoutV2Model Model, ShopCatalog Catalog) Fixture()
    {
        var catalog = ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork))!;
        var core = catalog.Core!;
        var civ = core.Civilizations.Values.First(c => c.Name == "Gigacorp");
        var station = core.StationTypes[civ.InitialStationTypeId];
        var techs = TechTreeBitMask.FromBytes(civ.TtbmBaseTechs);
        var offered = catalog.FilteredFor(station.TtbmLocal, techs);
        var shop = new ShopModel(offered, offered.Hulls.First().HullId, [], 10000);
        shop.ApplyDefaultLoadout();
        // Reopen on the native default so the real docked snapshot is populated.
        shop = new ShopModel(offered, shop.EffectiveHullId, shop.ChosenParts.ToArray(), 10000);
        return (shop, new LoadoutV2Model(shop, catalog.Hulls,
            BucketCatalog.ReachableTechs(core, techs), station.TtbmLocal, techs), catalog);
    }

    private static ShipStatSheet Sheet(ShopModel shop, ShopCatalog catalog, bool current = false)
        => ShipStatSheet.Build(catalog.Hulls.First(h => h.HullId == (current ? shop.CurrentHullId : shop.EffectiveHullId)),
            (current ? shop.CurrentParts : shop.ChosenParts).Where(p => p.MountId < 0x80)
                .Select(p => new MountedStatPart(shop.PartById(p.PartId)!.Value, p.MountId)).ToArray(),
            lookup: new StaticCoreShipStatLookup(catalog.Core!));

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Real_hull_rows_mark_current_and_dim_locks_with_their_reason()
    {
        Require.Artwork();
        var (_, model, _) = Fixture();
        var current = model.HullRows.First(h => h.IsCurrent);
        var row = LoadoutV2Presentation.HullRow(current);
        Assert.Contains(current.Name, row.Main.Text);
        Assert.Contains("[current]", row.Main.Text);
        Assert.Contains(current.Class, row.Detail[0].Text);
        Assert.Contains("$", row.Main.Text);
        Assert.True(row.Selected);
        var locked = model.HullRows.First(h => !h.IsBuyable);
        row = LoadoutV2Presentation.HullRow(locked);
        Assert.Equal(LoadoutV2Ink.Muted, row.Main.Ink);
        Assert.Equal(locked.Class + " | Locked: " + locked.LockReason, row.Detail[0].Text);
        Assert.False(row.Selected);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Current_hull_outside_station_offers_keeps_its_real_display_record_only()
    {
        Require.Artwork();
        var (_, model, catalog) = Fixture();
        var hull = model.HullRows.First(h => !h.IsBuyable);
        var shop = new ShopModel(model.Shop.Catalog, hull.HullId, [], 0);
        var display = new LoadoutV2Model(shop, catalog.Hulls);
        Assert.Null(shop.EffectiveHull);
        Assert.Equal(hull.Name, display.EffectiveHull!.Value.Name);
        Assert.NotEmpty(display.SlotRows);
        Assert.Empty(display.OffersFor(display.SlotRows[0].Slot));
        Assert.DoesNotContain(shop.BuyableHulls, h => h.HullId == hull.HullId);
        Assert.False(shop.SelectHull(hull.HullId));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Turret_occupants_keep_their_relative_seat_when_fixed_mount_counts_change()
    {
        Require.Artwork();
        var (_, _, catalog) = Fixture();
        var current = catalog.Hulls.First(h => h.MaxWeapons > h.MaxFixedWeapons && h.MaxFixedWeapons > 0);
        var chosen = catalog.Hulls.First(h => h.MaxWeapons > h.MaxFixedWeapons && h.MaxFixedWeapons != current.MaxFixedWeapons);
        Assert.Equal((int)current.MaxFixedWeapons, LoadoutV2Presentation.GunnerMount(
            chosen.MaxFixedWeapons, current.MaxFixedWeapons, chosen.MaxFixedWeapons));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Turret_rows_show_real_mount_part_and_gunner_or_open_seat()
    {
        Require.Artwork();
        var (_, _, catalog) = Fixture();
        var turretHull = catalog.Hulls.First(h => h.MaxWeapons > h.MaxFixedWeapons);
        var shop = new ShopModel(catalog, turretHull.HullId, [], 10000);
        var model = new LoadoutV2Model(shop, turretGunner: mount => mount == turretHull.MaxFixedWeapons ? "Gunner" : null);
        var slot = model.SlotRows.First(s => s.IsTurret);
        var row = LoadoutV2Presentation.SlotRow(slot);
        Assert.StartsWith("TURRET", row.Main.Text);
        Assert.Contains(row.Detail, s => s.Text == "Gunner: Gunner");
        row = LoadoutV2Presentation.SlotRow(slot with { GunnerName = null });
        Assert.Contains(row.Detail, s => s.Text == "Gunner: <open>");
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(HudStyle.Classic, 1920, 1080)]
    [InlineData(HudStyle.Modern, 1920, 1080)]
    [InlineData(HudStyle.Classic, 1280, 720)]
    [InlineData(HudStyle.Modern, 1280, 720)]
    public void Native_stats_lead_estimates_and_existing_keys_drive_inline_rows(HudStyle style, int width, int height)
    {
        Require.Artwork();
        var (shop, model, catalog) = Fixture();
        var sheet = Sheet(shop, catalog);
        var frame = LoadoutV2Presentation.Build(model, sheet, sheet);
        Assert.Equal(new[] { "MAX SPEED", "ACCELERATION", "TURN RATE", "HIT POINTS", "ENERGY", "SCAN RANGE", "SIGNATURE", "DEFENSE CLASS" },
            frame.Stats.Take(8).Select(s => s.Main.Text));
        Assert.Contains(frame.Stats, s => s.Main.Text == "~ FUEL");
        Assert.All(frame.Stats.Take(8), s => Assert.Equal("--", s.Detail[1].Text));
        shop.ToggleTab();
        shop.Activate();
        Assert.True(shop.SlotOpen);
        frame = LoadoutV2Presentation.Build(model, sheet, sheet);
        Assert.Equal(0, frame.OpenSlot);
        Assert.StartsWith("<empty>", frame.Offers[0].Main.Text);
        Assert.Equal(shop.SlotOffers(shop.OpenSlot!.Value).Count + 1, frame.Offers.Count);
        Assert.True(frame.Offers[0].Focused);
        shop.MoveCursor(1);
        frame = LoadoutV2Presentation.Build(model, sheet, sheet);
        Assert.True(frame.Offers[1].Focused);
        Assert.Contains(frame.Offers[1].Main.Text, s => s == '$');
        var layout = frame.Layout(width, height, 1);
        Assert.Contains(layout.Elements, e => e.Kind == LoadoutV2Layout.Kind.Offer && e.Index == frame.OfferFocus);
        Assert.Contains(frame.Offers.SelectMany(r => r.Detail), d => d.Text.Contains("DAMAGE/SEC") || d.Text == "No stat change");
        Assert.NotEqual(LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Better, style),
            LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Worse, style));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Price_and_need_button_read_shared_shop_totals_without_mutation()
    {
        Require.Artwork();
        var (shop, model, catalog) = Fixture();
        var before = Sheet(shop, catalog, current: true);
        // A later tech state supplies a priced hull; starting stock is free.
        shop.RefreshOffers(catalog, 0);
        shop.SelectHull(catalog.Hulls.OrderByDescending(h => h.Price).First().HullId);
        shop.RefreshMoney(0);
        Assert.True(shop.BuyShortfall > 0);
        var chosen = shop.ChosenParts.ToArray();
        var frame = LoadoutV2Presentation.Build(model, before, Sheet(shop, catalog));
        Assert.StartsWith("Need $", frame.Buy.Text);
        Assert.Equal(LoadoutV2Ink.Worse, frame.Buy.Ink);
        Assert.Equal(LoadoutV2Ink.Muted, frame.Launch.Ink);
        Assert.StartsWith("Loadout $", frame.Price[0].Text);
        Assert.StartsWith("Refund $", frame.Price[1].Text);
        Assert.StartsWith("Net $", frame.Price[2].Text);
        Assert.StartsWith("Shortfall $", frame.Price[3].Text);
        Assert.Equal(chosen, shop.ChosenParts);
        Assert.Equal(0, shop.MyMoney);
        shop.RefreshMoney(int.MaxValue);
        frame = LoadoutV2Presentation.Build(model, before, Sheet(shop, catalog));
        Assert.Equal("Buy", frame.Buy.Text);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Delta_text_and_ink_follow_p1011_direction_in_both_directions()
    {
        Require.Artwork();
        var (_, _, catalog) = Fixture();
        var slow = ShipStatSheet.Build(catalog.Hulls.OrderBy(h => h.Speed).First()).Stat(ShipStatField.MaxSpeed);
        var fast = ShipStatSheet.Build(catalog.Hulls.OrderByDescending(h => h.Speed).First()).Stat(ShipStatField.MaxSpeed);
        var better = StatDelta.Compare(slow, fast);
        var worse = StatDelta.Compare(fast, slow);
        Assert.Equal(LoadoutV2Ink.Better, LoadoutV2Presentation.Ink(better));
        Assert.StartsWith("+", LoadoutV2Presentation.Delta(better));
        Assert.Equal(LoadoutV2Ink.Worse, LoadoutV2Presentation.Ink(worse));
        Assert.StartsWith("-", LoadoutV2Presentation.Delta(worse));
        Assert.Equal(LoadoutV2Ink.Muted, LoadoutV2Presentation.Ink(StatDelta.Compare(fast, fast)));
    }
}
