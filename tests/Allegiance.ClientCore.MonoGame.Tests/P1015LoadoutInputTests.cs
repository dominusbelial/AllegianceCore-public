using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework.Input;
using Kind = Allegiance.ClientCore.MonoGame.Rendering.LoadoutV2Layout.Kind;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1015 — input sequences through the pure Loadout V2 router: events in, the
/// shared <see cref="LoadoutV2Model"/>/<see cref="ShopModel"/> out, on real
/// PCore001 data. The geometry is the same for both HUD styles, so each
/// sequence runs for both (and for the proof's two window sizes).
/// </summary>
public class P1015LoadoutInputTests
{
    public static IEnumerable<object[]> Screens() =>
        from style in new[] { HudStyle.Classic, HudStyle.Modern }
        from size in new[] { (1280, 720), (1920, 1080) }
        select new object[] { style, size.Item1, size.Item2 };

    /// <summary>The screen exactly as Game1 drives it: one router, a model over
    /// one shop, and the layout rebuilt from the presentation after every step.</summary>
    private sealed class Rig
    {
        public readonly ShopModel Shop;
        public readonly LoadoutV2Model Model;
        public readonly ShopCatalog Catalog;
        public readonly LoadoutV2Input Input = new();
        public readonly ActionMap Map = new(InputActionCatalog.Definitions);
        public LoadoutV2Layout Layout = null!;
        public LoadoutV2Presentation Frame = null!;
        private readonly int _width, _height;
        private readonly float _scale;
        private int _wheel;

        public Rig(HudStyle style, int width, int height, float scale = 1f, int money = 10000)
        {
            _ = style; // geometry and routing are style-independent by design (P1013)
            (_width, _height, _scale) = (width, height, scale);
            Catalog = ShopCatalog.TryLoad(ExternalPaths.Resolve(ExternalResourceKind.Artwork))!;
            var core = Catalog.Core!;
            var civ = core.Civilizations.Values.First(c => c.Name == "Gigacorp");
            var station = core.StationTypes[civ.InitialStationTypeId];
            // The side's whole reachable tree: every hull class is on sale, and
            // hulls only another station type sells stay locked.
            var techs = BucketCatalog.ReachableTechs(core, TechTreeBitMask.FromBytes(civ.TtbmBaseTechs));
            var offered = Catalog.FilteredFor(station.TtbmLocal, techs);
            var first = new ShopModel(offered, offered.Hulls.First().HullId, [], money);
            first.ApplyDefaultLoadout();
            Shop = new ShopModel(offered, first.EffectiveHullId, first.ChosenParts.ToArray(), money);
            Model = new LoadoutV2Model(Shop, Catalog.Hulls,
                BucketCatalog.ReachableTechs(core, TechTreeBitMask.FromBytes(civ.TtbmBaseTechs)),
                station.TtbmLocal, TechTreeBitMask.FromBytes(civ.TtbmBaseTechs));
            Step();
        }

        public void Draw()
        {
            Frame = LoadoutV2Presentation.Build(Model, null, Sheet(), Input, LoadoutV2Input.Hint(Map.LabelOf));
            Layout = Frame.Layout(_width, _height, _scale, Input);
        }

        private ShipStatSheet? Sheet()
        {
            var hull = Catalog.Hulls.FirstOrDefault(h => h.HullId == Shop.EffectiveHullId);
            if (hull.Name is null) return null;
            return ShipStatSheet.Build(hull, Shop.ChosenParts.Where(p => p.MountId < 0x80)
                .Select(p => new MountedStatPart(Shop.PartById(p.PartId)!.Value, p.MountId)).ToArray(),
                lookup: new StaticCoreShipStatLookup(Catalog.Core!));
        }

        public LoadoutV2Result Step(LoadoutV2Keys keys = default, (float X, float Y)? at = null,
            bool pressed = false, int wheel = 0, bool live = true)
        {
            _wheel += wheel;
            var pointer = new LoadoutV2Pointer(at?.X ?? -10, at?.Y ?? -10, pressed, _wheel);
            var result = Input.Step(Model, Layout, live, keys, pointer);
            Draw();
            return result;
        }

        /// <summary>Press and release through the real key table.</summary>
        public LoadoutV2Request Key(params Keys[] keys)
        {
            var state = new KeyboardState(keys);
            bool control = keys.Contains(Keys.LeftControl);
            var request = Step(LoadoutV2Input.Keys(a => Map.IsDownMostSpecific(state, a), control)).Request;
            Step();
            return request;
        }

        public LoadoutV2Request Pad(LoadoutV2Pad pad)
        {
            var request = Step(LoadoutV2Input.Keys(_ => false, false, pad)).Request;
            Step();
            return request;
        }

        public LoadoutV2Request Click(LoadoutV2Layout.Element element)
        {
            var at = (element.Bounds.CenterX, element.Bounds.CenterY);
            Step(at: at);
            var request = Step(at: at, pressed: true).Request;
            Step(at: at);
            return request;
        }

        public LoadoutV2Layout.Element Find(Kind kind, int index = -1) =>
            Layout.Elements.First(e => e.Kind == kind && (index < 0 || e.Index == index));

        public bool Visible(Kind kind, int index) => Layout.Elements.Any(e => e.Kind == kind && e.Index == index);

        /// <summary>Select the buyable hull with the most slot rows.</summary>
        public void PickBiggestHull()
        {
            var twin = new ShopModel(Shop.Catalog, Shop.CurrentHullId, [], 10000);
            var view = new LoadoutV2Model(twin, Catalog.Hulls);
            short best = Shop.EffectiveHullId; int rows = -1;
            foreach (var hull in twin.BuyableHulls)
            {
                twin.SelectHull(hull.HullId);
                if (view.SlotRows.Count > rows) (best, rows) = (hull.HullId, view.SlotRows.Count);
            }
            Assert.True(Model.SelectHull(best));
            Step();
        }
    }

    // ── mouse ───────────────────────────────────────────────────────────────

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Clicking_a_hull_row_selects_it_and_a_locked_row_is_refused(HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        var rows = rig.Model.HullRows;
        int other = Enumerable.Range(0, rows.Count).First(i => rows[i].IsBuyable && !rows[i].IsChosen
            && rig.Visible(Kind.Hull, i));
        Assert.Equal(LoadoutV2Request.None, rig.Click(rig.Find(Kind.Hull, other)));
        Assert.Equal(rows[other].HullId, rig.Shop.EffectiveHullId);
        Assert.Equal(LoadoutV2Column.Hulls, rig.Input.Column);
        Assert.Equal(other, rig.Input.HullFocus);
        Assert.True(rig.Frame.Hulls[other].Selected && rig.Frame.Hulls[other].Focused);

        rows = rig.Model.HullRows;
        int locked = Enumerable.Range(0, rows.Count).First(i => !rows[i].IsBuyable);
        // Wheel the hull column until the locked row is drawn, then click it.
        var hullColumn = (rig.Layout.HullList.CenterX, rig.Layout.HullList.CenterY);
        for (int i = 0; i < rows.Count && !rig.Visible(Kind.Hull, locked); i++) rig.Step(at: hullColumn, wheel: -120);
        rig.Click(rig.Find(Kind.Hull, locked));
        Assert.Equal(rows[other].HullId, rig.Shop.EffectiveHullId);
        Assert.Contains("locked", rig.Model.BlockReason);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Clicking_a_slot_opens_its_picker_inline_clicking_again_closes_and_an_offer_mounts(
        HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        int index = Enumerable.Range(0, rig.Model.SlotRows.Count)
            .First(i => rig.Model.OffersFor(rig.Model.SlotRows[i].Slot).Count > 1);
        var slot = rig.Model.SlotRows[index].Slot;

        rig.Click(rig.Find(Kind.Slot, index));
        Assert.Equal(slot, rig.Model.OpenSlot);
        Assert.Equal(index, rig.Layout.ExpandedSlot);
        Assert.Contains(rig.Layout.Elements, e => e.Kind == Kind.Offer && e.SlotIndex == index);
        // The old three-page shop is not what opened it.
        Assert.False(rig.Shop.SlotOpen);
        Assert.Equal(ShopModel.ShopPage.HullList, rig.Shop.Page);

        rig.Click(rig.Find(Kind.Slot, index));
        Assert.Null(rig.Model.OpenSlot);
        Assert.DoesNotContain(rig.Layout.Elements, e => e.Kind == Kind.Offer);

        rig.Click(rig.Find(Kind.Slot, index));
        var offers = rig.Model.Offers;
        int pick = Enumerable.Range(0, offers.Count).First(i => !offers[i].IsChosen);
        // Picker row 0 is the empty row; offer i is row i + 1.
        rig.Click(rig.Find(Kind.Offer, pick + 1));
        Assert.Equal(offers[pick].PartId, rig.Shop.PartInSlot(slot)?.PartId);
        Assert.Null(rig.Model.OpenSlot);

        rig.Click(rig.Find(Kind.Slot, index));
        rig.Click(rig.Find(Kind.Offer, 0));
        Assert.Null(rig.Shop.PartInSlot(slot));
        Assert.Null(rig.Model.OpenSlot);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Buttons_default_and_revert_act_on_the_model_and_buy_launch_are_handed_to_game1(
        HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        var docked = rig.Shop.ChosenParts.ToArray();
        int index = Enumerable.Range(0, rig.Model.SlotRows.Count).First(i => rig.Model.SlotRows[i].Filled);
        rig.Click(rig.Find(Kind.Slot, index));
        rig.Click(rig.Find(Kind.Offer, 0));
        Assert.NotEqual(docked.Length, rig.Shop.ChosenParts.Count);

        Assert.Equal(LoadoutV2Request.None, rig.Click(rig.Find(Kind.Revert)));
        Assert.Equal(docked, rig.Shop.ChosenParts);

        rig.Click(rig.Find(Kind.Slot, index));
        rig.Click(rig.Find(Kind.Offer, 0));
        var twin = new ShopModel(rig.Shop.Catalog, rig.Shop.CurrentHullId, docked, 10000);
        twin.ApplyDefaultLoadout();
        Assert.Equal(LoadoutV2Request.None, rig.Click(rig.Find(Kind.Default)));
        Assert.Equal(twin.ChosenParts, rig.Shop.ChosenParts);

        Assert.Equal(LoadoutV2Request.Buy, rig.Click(rig.Find(Kind.Buy)));
        Assert.Equal(LoadoutV2Request.Launch, rig.Click(rig.Find(Kind.Launch)));
        // Neither button changed the pending loadout itself.
        Assert.Equal(twin.ChosenParts, rig.Shop.ChosenParts);
        Assert.Equal(Kind.Launch, rig.Frame.ActionFocus);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Wheel_scrolls_only_the_column_under_the_pointer_and_never_past_its_end(
        HudStyle style, int width, int height)
    {
        Require.Artwork();
        // A large requested UI scale gives the shortest lists the layout allows.
        var rig = new Rig(style, width, height, scale: 4f);
        Assert.True(rig.Layout.MaxHullScroll > 0, "the real hull list overflows its column");
        var hulls = (rig.Layout.HullList.CenterX, rig.Layout.HullList.CenterY);
        int start = rig.Layout.HullScroll;
        rig.Step(at: hulls, wheel: -120);
        Assert.Equal(Math.Min(start + 1, rig.Layout.MaxHullScroll), rig.Layout.HullScroll);
        Assert.Equal(0, rig.Layout.SlotScroll);
        for (int i = 0; i < 200; i++) rig.Step(at: hulls, wheel: -360);
        Assert.Equal(rig.Layout.MaxHullScroll, rig.Layout.HullScroll);
        Assert.Equal(rig.Layout.MaxHullScroll, rig.Input.HullScroll);
        // No hidden travel: one notch back moves one row at once.
        rig.Step(at: hulls, wheel: 120);
        Assert.Equal(rig.Layout.MaxHullScroll - 1, rig.Layout.HullScroll);
        for (int i = 0; i < 200; i++) rig.Step(at: hulls, wheel: 120);
        Assert.Equal(0, rig.Layout.HullScroll);
        // Wheel over the ship preview scrolls nothing.
        rig.Step(at: (rig.Layout.ShipPreview.CenterX, rig.Layout.ShipPreview.CenterY), wheel: -120);
        Assert.Equal((0, 0, 0), (rig.Layout.HullScroll, rig.Layout.SlotScroll, rig.Layout.OfferScroll));
        // Hull rows drawn are the scrolled model indices.
        rig.Step(at: hulls, wheel: -240);
        Assert.Equal(2, rig.Layout.Elements.First(e => e.Kind == Kind.Hull).Index);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Wheel_over_the_picker_scrolls_offers_and_leaving_the_owner_closes_the_picker_first(
        HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height, scale: 4f);
        rig.PickBiggestHull();
        var slots = rig.Model.SlotRows;
        Assert.True(slots.Count > rig.Layout.SlotCapacity, $"{slots.Count} slot rows overflow {rig.Layout.SlotCapacity}");
        // An early slot: the closed list can still scroll once its picker is gone.
        int closedMax = slots.Count - rig.Layout.SlotCapacity;
        int index = closedMax >= 2 && rig.Model.OffersFor(slots[1].Slot).Count > 0 ? 1 : 0;
        rig.Click(rig.Find(Kind.Slot, index));
        Assert.NotNull(rig.Model.OpenSlot);

        if (rig.Layout.MaxOfferScroll > 0)
        {
            var picker = (rig.Layout.OfferViewport.CenterX, rig.Layout.OfferViewport.CenterY);
            int slotScroll = rig.Layout.SlotScroll;
            for (int i = 0; i < 50; i++) rig.Step(at: picker, wheel: 120);
            Assert.Equal(0, rig.Layout.OfferScroll);
            rig.Step(at: picker, wheel: -120);
            Assert.Equal(1, rig.Layout.OfferScroll);
            Assert.Equal(slotScroll, rig.Layout.SlotScroll);
            Assert.NotNull(rig.Model.OpenSlot);
            for (int i = 0; i < 50; i++) rig.Step(at: picker, wheel: -120);
            Assert.Equal(rig.Layout.MaxOfferScroll, rig.Layout.OfferScroll);
        }

        // Inside the range that keeps owner + picker visible the picker stays.
        var column = (rig.Layout.SlotList.Right - 4, rig.Layout.SlotList.Y + 4);
        Assert.False(rig.Layout.OfferViewport.Contains(column.Item1, column.Item2));
        for (int guard = 0; guard < 100 && rig.Layout.SlotScroll < rig.Layout.MaxSlotScroll; guard++)
        {
            rig.Step(at: column, wheel: -120);
            Assert.NotNull(rig.Model.OpenSlot);
            Assert.True(rig.Visible(Kind.Slot, index));
        }
        // One more notch would scroll the owner away: the picker closes first.
        rig.Step(at: column, wheel: -120);
        Assert.Null(rig.Model.OpenSlot);
        Assert.DoesNotContain(rig.Layout.Elements, e => e.Kind == Kind.Offer);
        Assert.Equal(0, rig.Layout.OfferScroll);
        for (int i = 0; i < 50; i++) rig.Step(at: column, wheel: -120);
        Assert.Equal(slots.Count - rig.Layout.SlotCapacity, rig.Layout.SlotScroll);
        Assert.True(rig.Visible(Kind.Slot, slots.Count - 1));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Wheel_at_the_end_of_a_list_that_fits_keeps_the_picker_open(HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        Assert.True(rig.Model.SlotRows.Count <= rig.Layout.SlotCapacity);
        rig.Click(rig.Find(Kind.Slot, 0));
        var column = (rig.Layout.SlotList.Right - 4, rig.Layout.SlotList.Y + 4);
        for (int i = 0; i < 5; i++) rig.Step(at: column, wheel: -120);
        for (int i = 0; i < 5; i++) rig.Step(at: column, wheel: 120);
        Assert.NotNull(rig.Model.OpenSlot);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Hover_follows_the_pointer_cues_once_per_target_and_ignores_plain_panels(
        HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        var slot = rig.Find(Kind.Slot, 0);
        var at = (slot.Bounds.CenterX, slot.Bounds.CenterY);
        Assert.True(rig.Step(at: at).HoverCue);
        Assert.Equal(slot, rig.Input.Hover);
        Assert.Equal(slot, rig.Input.HoverIn(rig.Layout));
        Assert.False(rig.Step(at: at).HoverCue);
        var title = rig.Find(Kind.Title);
        Assert.False(rig.Step(at: (title.Bounds.CenterX, title.Bounds.CenterY)).HoverCue);
        Assert.Null(rig.Input.Hover);
        var buy = rig.Find(Kind.Buy);
        Assert.True(rig.Step(at: (buy.Bounds.CenterX, buy.Bounds.CenterY)).HoverCue);
        // Hovering never changes the loadout or the focus.
        Assert.Null(rig.Model.OpenSlot);
        Assert.Equal(LoadoutV2Column.Hulls, rig.Input.Column);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void A_touch_tap_uses_the_same_drawn_targets_as_a_click(HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        var slot = rig.Find(Kind.Slot, 0);
        Assert.Equal(LoadoutV2Request.None, rig.Input.Tap(rig.Model, rig.Layout, slot.Bounds.CenterX, slot.Bounds.CenterY));
        Assert.Equal(rig.Model.SlotRows[0].Slot, rig.Model.OpenSlot);
        rig.Draw();
        var launch = rig.Find(Kind.Launch);
        Assert.Equal(LoadoutV2Request.Launch,
            rig.Input.Tap(rig.Model, rig.Layout, launch.Bounds.CenterX, launch.Bounds.CenterY));
        // A tap between targets does nothing (no enlarged invisible bands).
        Assert.Equal(LoadoutV2Request.None, rig.Input.Tap(rig.Model, rig.Layout, 2, 2));
    }

    // ── keyboard ────────────────────────────────────────────────────────────

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Keys_focus_columns_move_open_choose_and_escape_closes_before_leaving(
        HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        int chosen = rig.Input.HullFocus;
        Assert.True(rig.Model.HullRows[chosen].IsChosen);
        Assert.True(rig.Frame.Hulls[chosen].Focused);

        // 2 focuses the slot column; arrows move; Enter opens the picker.
        rig.Key(Keys.D2);
        Assert.Equal(LoadoutV2Column.Slots, rig.Input.Column);
        Assert.Equal(-1, rig.Frame.HullFocus);
        int index = Enumerable.Range(0, rig.Model.SlotRows.Count)
            .First(i => rig.Model.OffersFor(rig.Model.SlotRows[i].Slot).Count > 1);
        for (int i = 0; i < index; i++) rig.Key(Keys.Down);
        Assert.Equal(index, rig.Input.SlotFocus);
        Assert.True(rig.Frame.Slots[index].Focused);
        var slot = rig.Model.SlotRows[index].Slot;
        rig.Key(Keys.Enter);
        Assert.Equal(slot, rig.Model.OpenSlot);
        // The picker opens on the part the slot holds.
        var offers = rig.Model.Offers;
        int held = Enumerable.Range(0, offers.Count).First(i => offers[i].IsChosen);
        Assert.Equal(held + 1, rig.Input.OfferFocus);
        Assert.True(rig.Frame.Offers[held + 1].Focused);

        // Arrows now move inside the picker, clamped at both ends.
        for (int i = 0; i < offers.Count + 3; i++) rig.Key(Keys.Up);
        Assert.Equal(0, rig.Input.OfferFocus);
        for (int i = 0; i < offers.Count + 3; i++) rig.Key(Keys.Down);
        Assert.Equal(offers.Count, rig.Input.OfferFocus);
        Assert.True(rig.Visible(Kind.Offer, offers.Count));
        Assert.Equal(index, rig.Input.SlotFocus);

        // Back (B) closes the picker and stays on the screen; only then it leaves.
        Assert.Equal(LoadoutV2Request.None, rig.Key(Keys.B));
        Assert.Null(rig.Model.OpenSlot);
        Assert.Equal(offers[held].PartId, rig.Shop.PartInSlot(slot)?.PartId);
        Assert.Equal(LoadoutV2Request.Leave, rig.Key(Keys.B));

        // Enter opens again, a different offer is chosen with Space.
        rig.Key(Keys.Enter);
        int pick = Enumerable.Range(0, offers.Count).First(i => i != held);
        while (rig.Input.OfferFocus != pick + 1) rig.Key(rig.Input.OfferFocus > pick + 1 ? Keys.Up : Keys.Down);
        rig.Key(Keys.Space);
        Assert.Equal(offers[pick].PartId, rig.Shop.PartInSlot(slot)?.PartId);
        Assert.Null(rig.Model.OpenSlot);

        // Escape's own step (Game1's Escape chain) closes an open picker, once.
        rig.Key(Keys.Enter);
        Assert.True(rig.Input.ClosePicker(rig.Model));
        Assert.False(rig.Input.ClosePicker(rig.Model));

        // 1 returns to the hulls, closing any picker on the way.
        rig.Key(Keys.Enter);
        rig.Key(Keys.D1);
        Assert.Null(rig.Model.OpenSlot);
        Assert.Equal(LoadoutV2Column.Hulls, rig.Input.Column);
        int next = Enumerable.Range(0, rig.Model.HullRows.Count)
            .First(i => rig.Model.HullRows[i].IsBuyable && !rig.Model.HullRows[i].IsChosen);
        while (rig.Input.HullFocus != next) rig.Key(rig.Input.HullFocus > next ? Keys.Up : Keys.Down);
        Assert.NotEqual(rig.Model.HullRows[next].HullId, rig.Shop.EffectiveHullId);
        rig.Key(Keys.Enter);
        Assert.Equal(rig.Model.HullRows[next].HullId, rig.Shop.EffectiveHullId);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Left_right_and_q_e_walk_the_native_hull_order_and_d_applies_the_native_default(
        HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        // The same shop rule the old Q/E keys call (native GetNextHullType).
        var twin = new ShopModel(rig.Shop.Catalog, rig.Shop.CurrentHullId, rig.Shop.CurrentParts.ToArray(), 10000);
        foreach (var (key, forward) in new[] { (Keys.Right, true), (Keys.Right, true), (Keys.E, true),
                     (Keys.Left, false), (Keys.Q, false), (Keys.Left, false), (Keys.Left, false) })
        {
            rig.Key(key);
            twin.CycleHull(forward);
            Assert.Equal(twin.EffectiveHullId, rig.Shop.EffectiveHullId);
            Assert.Equal(twin.ChosenParts, rig.Shop.ChosenParts);
            Assert.True(rig.Model.HullRows[rig.Input.HullFocus].IsChosen);
            Assert.True(rig.Visible(Kind.Hull, rig.Input.HullFocus));
        }
        // The arrows cycle hulls from the slot column and from an open picker too.
        rig.Key(Keys.D2);
        rig.Key(Keys.Enter);
        Assert.NotNull(rig.Model.OpenSlot);
        rig.Key(Keys.Right);
        twin.CycleHull(true);
        Assert.Equal(twin.EffectiveHullId, rig.Shop.EffectiveHullId);
        Assert.Null(rig.Model.OpenSlot);

        // D = native OnButtonDefault through ShopModel.ApplyDefaultLoadout.
        int filled = Enumerable.Range(0, rig.Model.SlotRows.Count).First(i => rig.Model.SlotRows[i].Filled);
        rig.Model.ClearSlot(rig.Model.SlotRows[filled].Slot);
        twin.ApplyDefaultLoadout();
        Assert.NotEqual(twin.ChosenParts, rig.Shop.ChosenParts);
        rig.Key(Keys.D);
        Assert.Equal(twin.ChosenParts, rig.Shop.ChosenParts);
        // Ctrl+D / Ctrl+1 are not the plain verbs (Ctrl+digit saves a custom loadout).
        rig.Model.ClearSlot(rig.Model.SlotRows[filled].Slot);
        rig.Key(Keys.LeftControl, Keys.D);
        rig.Key(Keys.LeftControl, Keys.D1);
        Assert.NotEqual(twin.ChosenParts, rig.Shop.ChosenParts);
        Assert.Equal(LoadoutV2Column.Slots, rig.Input.Column);
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Tab_reaches_the_buttons_and_l_launches_through_game1(HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        rig.Key(Keys.Tab);
        Assert.Equal(LoadoutV2Column.Slots, rig.Input.Column);
        rig.Key(Keys.Tab);
        Assert.Equal(LoadoutV2Column.Actions, rig.Input.Column);
        Assert.Equal(Kind.Default, rig.Frame.ActionFocus);
        short hull = rig.Shop.EffectiveHullId;
        rig.Key(Keys.Right); rig.Key(Keys.Right);
        Assert.Equal(Kind.Buy, rig.Frame.ActionFocus);
        Assert.Equal(hull, rig.Shop.EffectiveHullId); // arrows walk the button row here
        Assert.Equal(LoadoutV2Request.Buy, rig.Key(Keys.Enter));
        rig.Key(Keys.Down); rig.Key(Keys.Down);
        Assert.Equal(Kind.Launch, rig.Frame.ActionFocus);
        Assert.Equal(LoadoutV2Request.Launch, rig.Key(Keys.Space));
        rig.Key(Keys.Left); rig.Key(Keys.Left); rig.Key(Keys.Up); rig.Key(Keys.Up);
        Assert.Equal(Kind.Default, rig.Frame.ActionFocus);
        rig.Key(Keys.E); // the dedicated hull verbs still work from the button row
        Assert.NotEqual(hull, rig.Shop.EffectiveHullId);
        rig.Key(Keys.Tab);
        Assert.Equal(LoadoutV2Column.Hulls, rig.Input.Column);
        Assert.Null(rig.Frame.ActionFocus);
        Assert.Equal(LoadoutV2Request.Launch, rig.Key(Keys.L));
    }

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Keyboard_focus_scrolls_its_row_into_view(HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height, scale: 4f);
        int count = rig.Model.HullRows.Count;
        Assert.True(count > rig.Layout.HullCapacity);
        for (int i = 0; i < count + 2; i++)
        {
            rig.Key(Keys.Down);
            Assert.True(rig.Visible(Kind.Hull, rig.Input.HullFocus));
        }
        Assert.Equal(count - 1, rig.Input.HullFocus);
        for (int i = 0; i < count + 2; i++)
        {
            rig.Key(Keys.Up);
            Assert.True(rig.Visible(Kind.Hull, rig.Input.HullFocus));
        }
        Assert.Equal(0, rig.Input.HullFocus);

        rig.PickBiggestHull();
        rig.Key(Keys.D2);
        int slots = rig.Model.SlotRows.Count;
        for (int i = 0; i < slots + 2; i++)
        {
            rig.Key(Keys.Down);
            Assert.True(rig.Visible(Kind.Slot, rig.Input.SlotFocus));
        }
        Assert.Equal(slots - 1, rig.Input.SlotFocus);
        // Opening the last slot keeps the owner and its whole picker on screen.
        rig.Key(Keys.Enter);
        Assert.True(rig.Visible(Kind.Slot, slots - 1));
        Assert.Equal(rig.Layout.OfferCapacity, rig.Layout.Elements.Count(e => e.Kind == Kind.Offer));
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void A_held_key_or_button_acts_once_and_nothing_reacts_while_the_screen_is_gated()
    {
        Require.Artwork();
        var rig = new Rig(HudStyle.Modern, 1280, 720);
        short hull = rig.Shop.EffectiveHullId;
        var next = new LoadoutV2Keys(NextHull: true);
        rig.Step(next);
        short after = rig.Shop.EffectiveHullId;
        Assert.NotEqual(hull, after);
        for (int i = 0; i < 5; i++) rig.Step(next);
        Assert.Equal(after, rig.Shop.EffectiveHullId);
        rig.Step();

        // Gated (command screen, chat entry, a popup): keys, click and wheel do nothing.
        var slot = rig.Find(Kind.Slot, 0);
        var at = (slot.Bounds.CenterX, slot.Bounds.CenterY);
        var everything = new LoadoutV2Keys(true, true, true, true, true, true, true, true, true, true, true, true, true);
        var gated = rig.Step(everything, at, pressed: true, wheel: -480, live: false);
        Assert.Equal(default, gated);
        Assert.Null(rig.Input.Hover);
        Assert.Null(rig.Input.HoverIn(rig.Layout));
        Assert.Equal(after, rig.Shop.EffectiveHullId);
        Assert.Null(rig.Model.OpenSlot);
        Assert.Equal(LoadoutV2Column.Hulls, rig.Input.Column);
        Assert.Equal(0, rig.Layout.SlotScroll + rig.Layout.HullScroll);

        // The gate lifts with everything still held: no stale press, no stored wheel.
        var lifted = rig.Step(everything, at, pressed: true);
        Assert.Equal(LoadoutV2Request.None, lifted.Request);
        Assert.Equal(after, rig.Shop.EffectiveHullId);
        Assert.Null(rig.Model.OpenSlot);
        Assert.Equal(0, rig.Layout.SlotScroll + rig.Layout.HullScroll);
        // A fresh press after release works again.
        rig.Step(at: at);
        rig.Step(at: at, pressed: true);
        Assert.NotNull(rig.Model.OpenSlot);
    }

    // ── controller ──────────────────────────────────────────────────────────

    [SkippableTheory, Trait("Requires", "Artwork"), MemberData(nameof(Screens))]
    public void Pad_dpad_a_b_shoulders_x_and_y_play_the_whole_screen(HudStyle style, int width, int height)
    {
        Require.Artwork();
        var rig = new Rig(style, width, height);
        var twin = new ShopModel(rig.Shop.Catalog, rig.Shop.CurrentHullId, rig.Shop.CurrentParts.ToArray(), 10000);

        // Shoulders and D-pad left/right = previous/next hull.
        rig.Pad(new(RightShoulder: true)); twin.CycleHull(true);
        Assert.Equal(twin.EffectiveHullId, rig.Shop.EffectiveHullId);
        rig.Pad(new(Right: true)); twin.CycleHull(true);
        Assert.Equal(twin.EffectiveHullId, rig.Shop.EffectiveHullId);
        rig.Pad(new(LeftShoulder: true)); twin.CycleHull(false);
        rig.Pad(new(Left: true)); twin.CycleHull(false);
        Assert.Equal(twin.EffectiveHullId, rig.Shop.EffectiveHullId);

        // D-pad down + A selects a hull row.
        int start = rig.Input.HullFocus;
        rig.Pad(new(Down: true));
        Assert.Equal(start + 1, rig.Input.HullFocus);
        rig.Pad(new(A: true));
        Assert.Equal(rig.Model.HullRows[start + 1].HullId, rig.Shop.EffectiveHullId);

        // X steps to the slots; A opens; D-pad moves; A chooses; B closes then leaves.
        rig.Pad(new(X: true));
        Assert.Equal(LoadoutV2Column.Slots, rig.Input.Column);
        int index = Enumerable.Range(0, rig.Model.SlotRows.Count)
            .First(i => rig.Model.OffersFor(rig.Model.SlotRows[i].Slot).Count > 0);
        for (int i = 0; i < index; i++) rig.Pad(new(Down: true));
        var slot = rig.Model.SlotRows[index].Slot;
        rig.Pad(new(A: true));
        Assert.Equal(slot, rig.Model.OpenSlot);
        while (rig.Input.OfferFocus > 0) rig.Pad(new(Up: true));
        rig.Pad(new(A: true));
        Assert.Null(rig.Shop.PartInSlot(slot));
        Assert.Null(rig.Model.OpenSlot);
        rig.Pad(new(A: true));
        rig.Pad(new(Down: true));
        Assert.Equal(LoadoutV2Request.None, rig.Pad(new(B: true)));
        Assert.Null(rig.Model.OpenSlot);
        Assert.Null(rig.Shop.PartInSlot(slot));
        Assert.Equal(LoadoutV2Request.Leave, rig.Pad(new(B: true)));

        // X again reaches the buttons: Default, Revert, Buy, Launch by D-pad + A.
        rig.Pad(new(X: true));
        Assert.Equal(Kind.Default, rig.Frame.ActionFocus);
        twin = new ShopModel(rig.Shop.Catalog, rig.Shop.CurrentHullId, rig.Shop.CurrentParts.ToArray(), 10000);
        twin.SelectHull(rig.Shop.EffectiveHullId);
        twin.ApplyDefaultLoadout();
        rig.Pad(new(A: true));
        Assert.Equal(twin.ChosenParts, rig.Shop.ChosenParts);
        rig.Pad(new(Right: true));
        rig.Pad(new(A: true));
        Assert.Equal(rig.Shop.CurrentHullId, rig.Shop.EffectiveHullId);
        Assert.Equal(rig.Shop.CurrentParts, rig.Shop.ChosenParts);
        // Round the focus ring: the button focus is remembered (Revert), Buy is next.
        rig.Pad(new(X: true)); rig.Pad(new(X: true)); rig.Pad(new(X: true));
        Assert.Equal(Kind.Revert, rig.Frame.ActionFocus);
        rig.Pad(new(Right: true));
        Assert.Equal(LoadoutV2Request.Buy, rig.Pad(new(A: true)));
        // Y launches from anywhere, as on the old docked screen.
        Assert.Equal(LoadoutV2Request.Launch, rig.Pad(new(Y: true)));
    }

    // ── key table ───────────────────────────────────────────────────────────

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Focus_and_default_keys_are_rebindable_and_the_footer_hint_follows_the_table()
    {
        Require.Artwork();
        var rig = new Rig(HudStyle.Classic, 1920, 1080);
        Assert.Equal("1/2 hulls/slots  |  Tab next  |  Up/Down move  |  Left/Right hull  |  Space choose"
            + "  |  D default  |  B back  |  L launch", rig.Frame.Hint);
        Assert.Null(rig.Map.SetBinding(LoadoutV2Input.FocusSlotsAction, new(Keys.F, InputModifiers.None), force: true));
        Assert.Null(rig.Map.SetBinding(LoadoutV2Input.DefaultAction, new(Keys.G, InputModifiers.None), force: true));
        rig.Key(Keys.D2);
        Assert.Equal(LoadoutV2Column.Hulls, rig.Input.Column);
        rig.Key(Keys.F);
        Assert.Equal(LoadoutV2Column.Slots, rig.Input.Column);
        int filled = Enumerable.Range(0, rig.Model.SlotRows.Count).First(i => rig.Model.SlotRows[i].Filled);
        var before = rig.Shop.ChosenParts.ToArray();
        rig.Model.ClearSlot(rig.Model.SlotRows[filled].Slot);
        rig.Key(Keys.D);
        Assert.NotEqual(before, rig.Shop.ChosenParts);
        rig.Key(Keys.G);
        Assert.Equal(before, rig.Shop.ChosenParts);
        Assert.StartsWith("1/F hulls/slots", rig.Frame.Hint);
        Assert.Contains("G default", rig.Frame.Hint);
    }

    [Fact]
    public void New_actions_live_in_the_key_table_for_both_layouts()
    {
        foreach (var layout in new[] { InputLayout.Port, InputLayout.Native })
        {
            var map = new ActionMap(InputActionCatalog.Definitions, layout);
            Assert.Equal(new KeyBinding(Keys.D1, InputModifiers.None), map.BindingOf(LoadoutV2Input.FocusHullsAction));
            Assert.Equal(new KeyBinding(Keys.D2, InputModifiers.None), map.BindingOf(LoadoutV2Input.FocusSlotsAction));
            Assert.Equal(new KeyBinding(Keys.D, InputModifiers.None), map.BindingOf(LoadoutV2Input.DefaultAction));
            string hint = LoadoutV2Input.Hint(map.LabelOf);
            foreach (var word in new[] { "hulls/slots", "next", "move", "hull", "choose", "default", "back", "launch" })
                Assert.Contains(" " + word, hint);
        }
        // An unbound verb drops out of the hint instead of showing an empty key.
        Assert.DoesNotContain("default", LoadoutV2Input.Hint(a => a == LoadoutV2Input.DefaultAction ? "" : "K"));
    }

    [Fact]
    public void Arrange_returns_and_keeps_the_layouts_clamped_offsets()
    {
        var input = new LoadoutV2Input();
        var layout = input.Arrange(1280, 720, 1f, hullRows: 40, slotRows: 6, offerRows: 0, openSlot: -1, statCount: 8);
        Assert.Equal((0, 0, 0), (layout.HullScroll, layout.SlotScroll, layout.OfferScroll));
        Assert.Equal((input.HullScroll, input.SlotScroll, input.OfferScroll),
            (layout.HullScroll, layout.SlotScroll, layout.OfferScroll));
        // Not live and no model: nothing moves, nothing throws, nothing is hovered.
        Assert.Equal(default, input.Step(null, layout, true, new LoadoutV2Keys(Down: true),
            new LoadoutV2Pointer(10, 10, true, 120)));
        Assert.Null(input.Hover);
    }
}
