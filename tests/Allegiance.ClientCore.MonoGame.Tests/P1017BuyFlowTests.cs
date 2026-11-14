using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1017 — the pure half of the loadout Buy / Launch flow on real PCore001
/// data: native button states and wording (<c>WinTrek/load.cpp:2139-2190</c>,
/// <c>loadoutpane.mdl:48-57</c>), the Buy / Launch decisions
/// (<c>load.cpp:1498-1531</c>), what the shop does with the server's answer
/// (<c>load.cpp:2193-2210</c>) and the price of free spawn hardware.
/// </summary>
public sealed class P1017BuyFlowTests
{
    private const short LxyScout = 92;      // $500
    private const short HvyScout = 85;      // $0
    private const short Sanctuary = 2;      // dispenser, $100 each

    private static ShopCatalog Catalog()
    {
        var all = new TechTreeBitMask(Enumerable.Repeat((byte)0xFF, TechTreeBitMask.ByteCount).ToArray());
        return ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork))!.FilteredFor(Array.Empty<byte>(), all);
    }

    /// <summary>Docked in a Hvy Scout with its default loadout.</summary>
    private static ShopModel Docked(ShopCatalog catalog, int? money, short hull = HvyScout)
    {
        var seed = new ShopModel(catalog, hull, [], money);
        seed.ApplyDefaultLoadout();
        return new ShopModel(catalog, hull, seed.ChosenParts.ToArray(), money);
    }

    private static LoadoutV2Presentation Screen(ShopModel shop, ShopCatalog catalog) =>
        LoadoutV2Presentation.Build(new LoadoutV2Model(shop, catalog.Hulls), null, null);

    // ── native states and wording ────────────────────────────────────────────

    [SkippableFact, Trait("Requires", "Artwork")]
    public void The_four_native_states_drive_the_status_line_and_the_buttons()
    {
        Require.Artwork();
        var catalog = Catalog();
        var shop = Docked(catalog, money: 100);

        // 0 — the ship you have.
        Assert.Equal(ShopModel.ShipState.Current, shop.State);
        var screen = Screen(shop, catalog);
        Assert.Equal("This is your current loadout.", screen.Status.Text);
        Assert.Equal(LoadoutV2Ink.Muted, screen.Status.Ink);
        Assert.Equal(LoadoutV2Ink.Muted, screen.Buy.Ink);            // Buy disabled (load.cpp:2181)
        Assert.Equal(LoadoutV2Ink.Accent, screen.Launch.Ink);        // Launch enabled (load.cpp:2180)
        Assert.False(shop.BuyEnabled);
        Assert.True(shop.LaunchEnabled);

        // 3 — not affordable.
        shop.CycleHullTo(LxyScout);
        long need = shop.Cost - 100;
        Assert.True(need > 0);
        Assert.Equal(ShopModel.ShipState.Unaffordable, shop.State);
        screen = Screen(shop, catalog);
        Assert.Equal("You can't afford this loadout.", screen.Status.Text);
        Assert.Equal(LoadoutV2Ink.Worse, screen.Status.Ink);
        Assert.Equal($"Need ${need:N0}", screen.Buy.Text);
        Assert.Equal(LoadoutV2Ink.Worse, screen.Buy.Ink);            // live: a press asks the team
        Assert.Equal(LoadoutV2Ink.Muted, screen.Launch.Ink);
        Assert.False(shop.LaunchEnabled);
        Assert.StartsWith("Shortfall $", screen.Price[3].Text);

        // …and nobody on the team can cover it: Buy is dead too (load.cpp:2152-2166).
        shop.RefreshTeamMoney(need - 1);
        Assert.False(shop.BuyEnabled);
        Assert.Equal(LoadoutV2Ink.Muted, Screen(shop, catalog).Buy.Ink);
        shop.RefreshTeamMoney(need);
        Assert.True(shop.BuyEnabled);

        // 2 — affordable.
        shop.RefreshMoney(1_000_000);
        Assert.Equal(ShopModel.ShipState.Affordable, shop.State);
        screen = Screen(shop, catalog);
        Assert.Equal("Press 'buy' to purchase this loadout.", screen.Status.Text);
        Assert.Equal("Buy", screen.Buy.Text);
        Assert.Equal(LoadoutV2Ink.Accent, screen.Buy.Ink);
        Assert.Equal(LoadoutV2Ink.Accent, screen.Launch.Ink);

        // 1 — a ship worth nothing: native swaps Buy for "Confirm change" (load.cpp:2184-2190).
        var priced = Docked(catalog, money: 0, hull: LxyScout);
        priced.CycleHullTo(HvyScout);
        foreach (var slot in priced.Slots.ToArray()) priced.ClearSlot(slot);
        Assert.Equal(0, priced.ValueOf(priced.EffectiveHullId, priced.ChosenParts));
        Assert.Equal(ShopModel.ShipState.Free, priced.State);
        screen = Screen(priced, catalog);
        Assert.Equal("Press 'confirm change' to select this loadout.", screen.Status.Text);
        Assert.Equal("Confirm change", screen.Buy.Text);
        Assert.Equal(LoadoutV2Ink.Accent, screen.Buy.Ink);
    }

    [Theory]
    [InlineData(HudStyle.Classic)]
    [InlineData(HudStyle.Modern)]
    public void Refusal_ink_is_distinct_from_the_normal_and_disabled_inks_in_both_styles(HudStyle style)
    {
        var worse = LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Worse, style);
        Assert.NotEqual(worse, LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Muted, style));
        Assert.NotEqual(worse, LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Accent, style));
        Assert.NotEqual(worse, LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Normal, style));
        Assert.NotEqual(LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Muted, style), LoadoutV2Renderer.ColorFor(LoadoutV2Ink.Accent, style));
    }

    // ── Buy / Launch decisions ───────────────────────────────────────────────

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Launch_buys_and_launches_when_affordable_and_sends_nothing_when_not()
    {
        Require.Artwork();
        var catalog = Catalog();
        var flow = new LoadoutBuyFlow();
        var shop = Docked(catalog, money: 100);
        shop.CycleHullTo(LxyScout);

        Assert.Equal(default, flow.Launch(shop));                    // load.cpp:1500: no else branch
        Assert.StartsWith("You can't afford this loadout.", shop.BlockReason);
        Assert.StartsWith("You can't afford this loadout.", Screen(shop, catalog).Status.Text);

        shop.RefreshMoney((int)shop.Cost);                           // the money arrives: the line goes
        Assert.Null(shop.BlockReason);
        var order = flow.Launch(shop);
        Assert.Equal(LoadoutBuyFlow.Step.Purchase, order.Step);
        Assert.True(order.Launch);
        Assert.Equal(LxyScout, order.HullId);
        Assert.Equal(shop.ChosenParts, order.Parts);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Buy_asks_the_team_when_short_and_buys_without_launch_otherwise()
    {
        Require.Artwork();
        var catalog = Catalog();
        var store = new ShopCustomLoadouts();
        var flow = new LoadoutBuyFlow();
        var seed = new ShopModel(catalog, HvyScout, [], 100);
        seed.ApplyDefaultLoadout();
        var shop = new ShopModel(catalog, HvyScout, seed.ChosenParts.ToArray(), 100, store);

        // The ship you already have: nothing (V2), the old screen still sends.
        Assert.Equal(default, flow.Buy(shop, nativeGate: true));
        Assert.Equal(LoadoutBuyFlow.Step.Purchase, flow.Buy(shop, nativeGate: false).Step);

        shop.CycleHullTo(LxyScout);
        long need = shop.Cost - 100;
        var ask = flow.Buy(shop, nativeGate: true);
        Assert.Equal(LoadoutBuyFlow.Step.RequestMoney, ask.Step);    // load.cpp:1513-1520
        Assert.Equal(need, ask.Amount);
        Assert.Equal(LxyScout, ask.HullId);                          // hidFor
        Assert.Equal($"You can't afford this loadout. Asked your team for ${need:N0}.", shop.BlockReason);
        Assert.False(store.Has(LxyScout, 0));                        // nothing bought, nothing saved

        shop.RefreshMoney(1_000_000);
        Assert.Null(shop.BlockReason);
        var order = flow.Buy(shop, nativeGate: true);
        Assert.Equal(LoadoutBuyFlow.Step.Purchase, order.Step);
        Assert.False(order.Launch);
        Assert.Equal(shop.ChosenParts, order.Parts);
        Assert.True(store.Has(LxyScout, 0));                         // SaveCustomLoadout(m_pship, 0), load.cpp:1524-1527
    }

    // ── the server's answer ──────────────────────────────────────────────────

    [SkippableFact, Trait("Requires", "Artwork")]
    public void A_completed_buy_becomes_the_docked_ship_and_keeps_the_pending_loadout()
    {
        Require.Artwork();
        var catalog = Catalog();
        var shop = Docked(catalog, money: 10_000);
        shop.CycleHullTo(LxyScout);
        var bought = shop.ChosenParts.ToArray();
        long cost = shop.Cost;

        shop.PurchaseCompleted(LxyScout, bought, [], complete: true);

        Assert.Equal(LxyScout, shop.CurrentHullId);
        Assert.Equal(bought, shop.CurrentParts);
        Assert.Equal(bought, shop.ChosenParts);
        Assert.False(shop.WillSwapHull);
        Assert.True(shop.AlreadyOwn);
        Assert.Equal(0, shop.Cost);
        Assert.Equal(cost, shop.CurrentShipValue);
        Assert.Null(shop.BlockReason);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void An_incomplete_buy_shows_the_native_message_and_copies_the_real_ship()
    {
        Require.Artwork();
        var catalog = Catalog();
        var shop = Docked(catalog, money: 10_000);
        shop.CycleHullTo(LxyScout);
        ShopModelTests.MountViaSlots(shop, Sanctuary);
        var granted = shop.ChosenParts.Where(p => p.PartId != Sanctuary).ToArray();

        shop.PurchaseCompleted(LxyScout, granted, [], complete: false);

        Assert.Equal("Some of the parts you requested are no longer available", shop.BlockReason);
        Assert.Equal(granted, shop.ChosenParts);                     // m_pship = CopyCurrentShip(), load.cpp:2201-2204
        Assert.DoesNotContain(shop.ChosenParts, p => p.PartId == Sanctuary);
        Assert.True(shop.AlreadyOwn);
        // The line stays while money moves, and goes with the next edit.
        shop.RefreshMoney(5);
        Assert.NotNull(shop.BlockReason);
        shop.CycleHull(true);
        Assert.Null(shop.BlockReason);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void A_buy_refused_for_money_reverts_to_the_unchanged_ship()
    {
        Require.Artwork();
        var catalog = Catalog();
        var shop = Docked(catalog, money: 10_000);
        var docked = shop.CurrentParts.ToArray();
        shop.CycleHullTo(LxyScout);

        shop.PurchaseCompleted(HvyScout, docked, [], complete: false, refusedForMoney: true);

        Assert.Equal("You can't afford this loadout.", shop.BlockReason);
        Assert.Equal(HvyScout, shop.EffectiveHullId);
        Assert.Equal(docked, shop.ChosenParts);
        Assert.Equal(0, shop.Cost);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void An_unasked_sync_moves_an_untouched_loadout_and_leaves_an_edited_one()
    {
        Require.Artwork();
        var catalog = Catalog();
        var full = Docked(catalog, money: 0).CurrentParts.ToArray();
        Assert.NotEmpty(full);

        // The screen opened before the dock sync arrived: bare hull, untouched.
        var untouched = new ShopModel(catalog, HvyScout, [], 0);
        untouched.PurchaseCompleted(HvyScout, full, [], complete: true);
        Assert.Equal(full, untouched.ChosenParts);
        Assert.True(untouched.AlreadyOwn);

        // The player already picked another hull: the choice is kept.
        var edited = new ShopModel(catalog, HvyScout, [], 0);
        edited.CycleHullTo(LxyScout);
        var chosen = edited.ChosenParts.ToArray();
        edited.PurchaseCompleted(HvyScout, full, [], complete: true);
        Assert.Equal(LxyScout, edited.EffectiveHullId);
        Assert.Equal(chosen, edited.ChosenParts);
        Assert.Equal(full, edited.CurrentParts);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void The_flow_reads_the_answer_to_its_own_request_from_the_ledger()
    {
        Require.Artwork();
        var catalog = Catalog();
        var shop = Docked(catalog, money: 10_000);
        var docked = shop.CurrentParts.ToArray();
        var ledger = new OwnHardwareLedger();
        ledger.Acknowledged(new BuyLoadoutAck { HullId = HvyScout, Parts = docked });   // the dock sync
        var flow = new LoadoutBuyFlow();
        flow.Attach(ledger);
        Assert.False(flow.Sync(shop, HvyScout, docked, null, ledger, 10_000).Changed);

        // A launch the server answers without launching: refused, lock released.
        shop.CycleHullTo(LxyScout);
        var order = flow.Launch(shop);
        ledger.RequestSent(HvyScout);
        ledger.Acknowledged(new BuyLoadoutAck { HullId = HvyScout, Parts = docked, FBoughtEverything = false, FLaunch = false });
        var outcome = flow.Sync(shop, HvyScout, docked, null, ledger, 10_000);
        Assert.Equal(new LoadoutBuyFlow.Outcome(true, Answered: true, Complete: false, LaunchRefused: true), outcome);
        Assert.Equal(ShopModel.PartsUnavailableText, shop.BlockReason);   // not money: the wallet covered it
        Assert.Equal(HvyScout, shop.EffectiveHullId);

        // The same refusal with a wallet that no longer covers the cost.
        shop.CycleHullTo(LxyScout);
        order = flow.Buy(shop, nativeGate: true);
        Assert.Equal(LoadoutBuyFlow.Step.Purchase, order.Step);
        ledger.RequestSent(HvyScout);
        ledger.Acknowledged(new BuyLoadoutAck { HullId = HvyScout, Parts = docked });
        outcome = flow.Sync(shop, HvyScout, docked, null, ledger, 10);
        Assert.False(outcome.Complete);
        Assert.False(outcome.LaunchRefused);
        Assert.Equal("You can't afford this loadout.", shop.BlockReason);

        // A granted hull-only buy is complete although the flag is false for it.
        shop.RefreshMoney(10_000);
        shop.CycleHullTo(LxyScout);
        foreach (var slot in shop.Slots.ToArray()) shop.ClearSlot(slot);
        order = flow.Buy(shop, nativeGate: true);
        Assert.Empty(order.Parts);
        ledger.RequestSent(HvyScout);
        ledger.Acknowledged(new BuyLoadoutAck { HullId = LxyScout, Parts = [], FBoughtEverything = false });
        outcome = flow.Sync(shop, LxyScout, [], null, ledger, 9_500);
        Assert.True(outcome.Complete);
        Assert.Null(shop.BlockReason);
        Assert.Equal(LxyScout, shop.CurrentHullId);
        Assert.True(ledger.HullPaid(LxyScout));
        Assert.Equal(500, shop.CurrentShipValue);
    }

    // ── free spawn hardware ──────────────────────────────────────────────────

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Free_spawn_hardware_is_priced_as_the_server_prices_it()
    {
        Require.Artwork();
        var catalog = Catalog();
        ExpandedPartData[] kit = [new(Sanctuary, 3, 0, ExpandedPartData.FullyMounted)];

        // A $500 hull with 3 x $100 Sanctuary, none of it paid for.
        var shop = new ShopModel(catalog, LxyScout, kit, 0, currentHullPaid: false,
            currentFreeParts: [new FreeHardware.Unit(Sanctuary, 3)]);
        Assert.Equal(800, shop.ValueOf(LxyScout, kit));
        Assert.Equal(0, shop.CurrentShipValue);                      // refund: nothing
        Assert.Equal(0, shop.NewLoadoutValue);                       // keeping it: nothing
        Assert.Equal(0, shop.Cost);
        Assert.True(shop.LaunchEnabled);

        // Dropping the free part changes nothing; trading the hull in refunds nothing.
        shop.ClearSlot(new SlotDef(EquipmentType.Dispenser, 0, false));
        Assert.Equal(0, shop.Cost);
        shop.CycleHullTo(HvyScout);
        Assert.True(shop.Cost >= 0);

        // The same ship, paid for: worth its list value.
        var paid = new ShopModel(catalog, LxyScout, kit, 0);
        Assert.Equal(800, paid.CurrentShipValue);
        Assert.Equal(0, paid.Cost);
        paid.CycleHullTo(HvyScout);
        Assert.True(paid.Cost <= -800 + paid.ValueOf(HvyScout, paid.ChosenParts));

        // A free hull kept, a paid part on it: only the part counts.
        var mixed = new ShopModel(catalog, LxyScout, kit, 0, currentHullPaid: false);
        Assert.Equal(300, mixed.CurrentShipValue);
        Assert.Equal(300, mixed.NewLoadoutValue);
        mixed.ClearSlot(new SlotDef(EquipmentType.Dispenser, 0, false));
        Assert.Equal(-300, mixed.Cost);
    }
}
