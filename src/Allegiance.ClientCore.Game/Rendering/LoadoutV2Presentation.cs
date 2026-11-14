using System.Globalization;
using Allegiance.Core;
using ShopPage = Allegiance.ClientCore.MonoGame.Rendering.ShopModel.ShopPage;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Semantic ink, resolved to the existing HUD palettes by the renderer.</summary>
public enum LoadoutV2Ink { Normal, Muted, Accent, Better, Worse }

public readonly record struct LoadoutV2Text(string Text, LoadoutV2Ink Ink = LoadoutV2Ink.Normal);

/// <summary>One two-line display row. All text is built without a graphics device.</summary>
public sealed record LoadoutV2DisplayRow(LoadoutV2Text Main, IReadOnlyList<LoadoutV2Text> Detail,
    bool Selected = false, bool Focused = false);

/// <summary>
/// P1014: presentation snapshot over P1010/P1011. No purchases, mounting or input
/// handling. Native load.cpp:986-1054 supplies the hull labels; :176-205 supplies
/// turret occupants; :1497-1531 supplies affordability through ShopModel.
/// Signed deltas, estimates and colours are presentation only.
/// </summary>
public sealed class LoadoutV2Presentation
{
    public required string Title { get; init; }
    public required string Money { get; init; }
    public required IReadOnlyList<LoadoutV2DisplayRow> Hulls { get; init; }
    public required IReadOnlyList<LoadoutV2DisplayRow> Slots { get; init; }
    /// <summary>Includes the shared shop's empty-slot choice at index zero.</summary>
    public required IReadOnlyList<LoadoutV2DisplayRow> Offers { get; init; }
    public required IReadOnlyList<LoadoutV2DisplayRow> Stats { get; init; }
    public required IReadOnlyList<LoadoutV2Text> Price { get; init; }
    public required LoadoutV2Text Buy { get; init; }
    public required LoadoutV2Text Launch { get; init; }
    /// <summary>P1017: the status line — why Buy or Launch is refused, or what
    /// the server answered. Native wording (<see cref="ShopModel.StateText"/>,
    /// <see cref="ShopModel.PartsUnavailableText"/>).</summary>
    public LoadoutV2Text Status { get; init; } = new("");
    public int OpenSlot { get; init; } = -1;
    public int HullFocus { get; init; } = -1;
    public int SlotFocus { get; init; } = -1;
    public int OfferFocus { get; init; } = -1;
    /// <summary>P1015: the keyboard/pad-focused button, or null.</summary>
    public LoadoutV2Layout.Kind? ActionFocus { get; init; }
    /// <summary>P1015: the footer hint, built from the live key table.</summary>
    public string? Hint { get; init; }

    /// <summary>The production draw gate. Other docked panes keep their own routing.</summary>
    public static bool UseV2(VisualOptions visuals, bool dockedLoadoutView)
        => dockedLoadoutView && visuals.IsOn(VisualFlags.LoadoutV2Key);

    /// <summary>Native turret painting maps chosen-hull turret indices back to
    /// the current ship (WinTrek/load.cpp:183-185).</summary>
    public static int GunnerMount(int mount, int currentFixedWeapons, int chosenFixedWeapons)
        => mount + currentFixedWeapons - chosenFixedWeapons;

    /// <param name="input">P1015: the V2 input router. When given, it owns the
    /// focus and the open picker is the view-model's; when null (P1014 callers)
    /// the old shop cursor is mirrored as before.</param>
    public static LoadoutV2Presentation Build(LoadoutV2Model model,
        ShipStatSheet? current, ShipStatSheet? pending, LoadoutV2Input? input = null, string? hint = null)
    {
        var shop = model.Shop;
        var hulls = model.HullRows;
        var slots = model.SlotRows;
        // P1015: with the router, the open picker is the view-model's and the
        // focus is the router's. Without it the old shop page/cursor is mirrored.
        var open = input is not null ? model.OpenSlot : shop.SlotOpen ? shop.OpenSlot : model.OpenSlot;
        int openIndex = FindSlot(slots, open);
        int slotFocus = input is not null
            ? input.Column == LoadoutV2Column.Slots && slots.Count > 0 ? input.SlotFocus : -1
            : shop.Page == ShopPage.SlotList ? FindSlot(slots, shop.SlotUnderCursor) : -1;
        int hullFocus = input is not null
            ? input.Column == LoadoutV2Column.Hulls && openIndex < 0 && hulls.Count > 0 ? input.HullFocus : -1
            : shop.Page == ShopPage.HullList ? shop.HullCursor : -1;
        int offerFocus = input is not null ? (openIndex >= 0 ? input.OfferFocus : -1)
            : shop.SlotOpen ? shop.Cursor : -1;
        var hullRows = hulls.Select((h, i) => HullRow(h, i == hullFocus)).ToArray();
        var slotRows = slots.Select((s, i) => SlotRow(s, i == openIndex, i == slotFocus)).ToArray();
        var offers = new List<LoadoutV2DisplayRow>();
        if (openIndex >= 0 && pending is not null && open is { } slot)
        {
            int cursor = offerFocus;
            offers.Add(OfferRow("<empty>", 0, !slots[openIndex].Filled, cursor == 0,
                pending, pending.WithPart(slot.Type, slot.Mount, null)));
            int i = 1;
            foreach (var offer in model.OffersFor(slot))
            {
                offers.Add(OfferRow(offer.Name + (offer.AlreadyMounted ? " [current]" : ""),
                    offer.Price, offer.IsChosen, cursor == i++, pending,
                    pending.WithPart(slot.Type, slot.Mount, offer.Part)));
            }
        }
        var stats = new List<LoadoutV2DisplayRow>();
        if (pending is not null)
        {
            var deltas = current is null ? [] : StatDelta.Between(current, pending);
            // Native hull rows always lead, in the native order. The compact
            // extras are explicitly marked; all part stats remain in offer deltas.
            var shown = pending.Stats.Where(s => s.Group == StatGroup.HullNative)
                .Concat(pending.Stats.Where(s => s.Key.Field is ShipStatField.ShieldStrength
                    or ShipStatField.Fuel or ShipStatField.AmmoCapacity or ShipStatField.EnergyRecharge));
            foreach (var stat in shown)
            {
                var delta = deltas.FirstOrDefault(d => d.Key == stat.Key);
                string label = (stat.IsEstimate ? "~ " : "") + stat.Label;
                stats.Add(new(new(label, stat.IsEstimate ? LoadoutV2Ink.Muted : LoadoutV2Ink.Normal),
                    [new(Value(stat)), new(Delta(delta), Ink(delta))]));
            }
            if (pending.Guns.Count > 0)
            {
                double now = pending.Guns.Sum(g => g.DamagePerSecond);
                double before = current?.Guns.Sum(g => g.DamagePerSecond) ?? now;
                stats.Add(new(new("~ WEAPON DPS", LoadoutV2Ink.Muted),
                    [new(Number(now)), new(Signed(now - before), now > before ? LoadoutV2Ink.Better
                        : now < before ? LoadoutV2Ink.Worse : LoadoutV2Ink.Muted)]));
            }
        }
        bool shortfall = shop.BuyShortfall > 0;
        // P1017: native button state (UpdateButtons, WinTrek/load.cpp:2139-2190).
        var state = shop.State;
        bool buyEnabled = shop.BuyEnabled, launchEnabled = shop.LaunchEnabled;
        return new()
        {
            Title = "LOADOUT / " + (model.EffectiveHull?.Name ?? "No hull"),
            Money = shop.MyMoney is { } money ? "MONEY " + Dollars(money) : "MONEY --",
            Hulls = hullRows, Slots = slotRows, Offers = offers, Stats = stats,
            OpenSlot = openIndex, HullFocus = hullFocus,
            SlotFocus = slotFocus, OfferFocus = offerFocus,
            ActionFocus = openIndex < 0 ? input?.FocusedAction : null, Hint = hint,
            Price = [new("Loadout " + Dollars(shop.NewLoadoutValue)),
                new("Refund " + Dollars(shop.CurrentShipValue), LoadoutV2Ink.Muted),
                new("Net " + Dollars(shop.Cost), shortfall ? LoadoutV2Ink.Worse : LoadoutV2Ink.Normal),
                new(shortfall ? "Shortfall " + Dollars(shop.BuyShortfall) : state == ShopModel.ShipState.Current ? "Owned" : "Ready",
                    shortfall ? LoadoutV2Ink.Worse : LoadoutV2Ink.Muted)],
            // A refusal (ShopModel.BlockReason) wins over the standing state text.
            Status = new(shop.BlockReason ?? ShopModel.StateText(state),
                shop.BlockReason is not null || state == ShopModel.ShipState.Unaffordable ? LoadoutV2Ink.Worse : LoadoutV2Ink.Muted),
            Buy = new(shortfall ? "Need " + Dollars(shop.BuyShortfall) : shop.BuyIsConfirmChange ? "Confirm change" : "Buy",
                !buyEnabled ? LoadoutV2Ink.Muted : shortfall ? LoadoutV2Ink.Worse : LoadoutV2Ink.Accent),
            Launch = new("Launch", launchEnabled ? LoadoutV2Ink.Accent : LoadoutV2Ink.Muted),
        };
    }

    public LoadoutV2Layout Layout(float width, float height, float scale)
    {
        var initial = LoadoutV2Layout.Compute(width, height, scale, Hulls.Count, Slots.Count,
            Offers.Count, OpenSlot, statCount: Stats.Count);
        int hull = Math.Max(0, HullFocus - initial.HullCapacity / 2);
        int slot = Math.Max(0, SlotFocus - initial.SlotCapacity / 2);
        int offer = Math.Max(0, OfferFocus - initial.OfferCapacity / 2);
        return LoadoutV2Layout.Compute(width, height, scale, Hulls.Count, Slots.Count,
            Offers.Count, OpenSlot, hull, slot, offer, Stats.Count);
    }

    /// <summary>P1015: lay out at the input router's scroll offsets (wheel and
    /// focus), keeping the clamped offsets in the router.</summary>
    public LoadoutV2Layout Layout(float width, float height, float scale, LoadoutV2Input input)
        => input.Arrange(width, height, scale, Hulls.Count, Slots.Count, Offers.Count, OpenSlot, Stats.Count);

    public static LoadoutV2DisplayRow HullRow(LoadoutV2HullRow hull, bool focused = false)
    {
        bool locked = !hull.IsBuyable;
        var ink = locked || !hull.Affordable ? LoadoutV2Ink.Muted : LoadoutV2Ink.Normal;
        return new(new(hull.Name + (hull.IsCurrent ? " [current]" : "") + "  " + Dollars(hull.Price), ink),
            [new(locked ? hull.Class + " | Locked: " + hull.LockReason : hull.Class, LoadoutV2Ink.Muted)], hull.IsChosen, focused);
    }

    public static LoadoutV2DisplayRow SlotRow(LoadoutV2SlotRow slot, bool open = false, bool focused = false)
        => new(new(slot.Label + (open ? " [-]" : " [+]"), open ? LoadoutV2Ink.Accent : LoadoutV2Ink.Normal),
            [new((slot.ChosenPartName ?? "<empty>") + (slot.Amount is > 1
                && slot.Slot.Type is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher
                    ? " x" + slot.Amount : "")),
                .. slot.IsTurret ? new[] { new LoadoutV2Text("Gunner: " + (slot.GunnerName ?? "<open>"), LoadoutV2Ink.Muted) } : []],
            open, focused);

    private static LoadoutV2DisplayRow OfferRow(string name, int price, bool selected, bool focused,
        ShipStatSheet before, ShipStatSheet after)
    {
        var changed = StatDelta.Between(before, after).Where(d => d.Outcome != StatOutcome.Same)
            .OrderBy(d => d.Key.Scope == StatScope.Slot ? 0 : 1).ToArray();
        // Keep every number from P1011 available in the pure row. The renderer
        // fits the inline summary to the row's text budget.
        var details = changed.Select(d => new LoadoutV2Text(
            (after.Find(d.Key)?.IsEstimate == true ? "~ " : "") + d.Label + " " + Delta(d), Ink(d))).ToArray();
        if (details.Length == 0) details = [new("No stat change", LoadoutV2Ink.Muted)];
        return new(new(name + "  " + Dollars(price)), details, selected, focused);
    }

    private static int FindSlot(IReadOnlyList<LoadoutV2SlotRow> rows, SlotDef? slot)
    {
        for (int i = 0; i < rows.Count; i++) if (rows[i].Slot == slot) return i;
        return -1;
    }

    public static LoadoutV2Ink Ink(StatDelta delta) => delta.Outcome switch
    {
        StatOutcome.Better => LoadoutV2Ink.Better,
        StatOutcome.Worse => LoadoutV2Ink.Worse,
        _ => LoadoutV2Ink.Muted,
    };

    public static string Delta(StatDelta delta) => delta.Outcome switch
    {
        StatOutcome.Same => "--",
        StatOutcome.Added => "new " + (delta.NewText ?? Number(delta.NewValue)),
        StatOutcome.Removed => "removed",
        StatOutcome.Changed => delta.NewText ?? "changed",
        _ => Signed(delta.Difference),
    };

    public static string Value(ShipStat stat) => (stat.Text ?? Number(stat.Value))
        + (stat.Unit.Length == 0 ? "" : " " + stat.Unit.Replace("²", "2").Replace("°", "deg"));
    private static string Number(double? value) => value is { } v && double.IsFinite(v)
        ? v.ToString("0.##", CultureInfo.InvariantCulture) : "--";
    private static string Signed(double value) => Math.Abs(value) < 1e-6 ? "--"
        : value.ToString("+0.##;-0.##", CultureInfo.InvariantCulture);
    private static string Dollars(long value) => "$" + value.ToString("N0", CultureInfo.InvariantCulture);
}
