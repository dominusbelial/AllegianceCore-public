using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P268/P519 — pure (MonoGame-free) layout of the docked/hangar loadout screen.
/// Turns the <see cref="ShopModel"/> into ASCII-safe display rows that
/// <see cref="DockedScreenRenderer"/> draws. P519 reworked it around the two
/// tabs (HULLS / PARTS) and the slot-driven part lists: the PARTS tab lists only
/// the effective hull's real slots, and opening a slot lists only the parts that
/// fit that slot+mount. Same discipline as <see cref="ScoreboardLayout"/>: pure,
/// headless-testable, every string printable ASCII 32..126.
/// </summary>
public static class ShopLayout
{
    /// <summary>Presentation bucket for a row; the renderer styles by this.</summary>
    public enum Kind
    {
        Title,
        Hint,
        Blank,
        Section,
        Info,
        Item,
        Muted,
        HighlightFit,
        HighlightNoFit,
    }

    /// <summary>One display row. <paramref name="Greyed"/> marks an item the
    /// player can't afford; the renderer styles it muted.</summary>
    public readonly record struct Row(Kind Kind, string Text, bool Highlight = false, bool Greyed = false)
    {
        public static Row Of(Kind kind, string text) => new(kind, text);
        public static Row Blank() => new(Kind.Blank, "");
    }

    /// <summary>Build the display rows for the given shop model.</summary>
    public static IReadOnlyList<Row> Build(ShopModel m)
    {
        var cat = m.Catalog;
        var rows = new List<Row>();

        rows.Add(Row.Of(Kind.Title, "STATION DOCKED - LOADOUT"));
        rows.Add(Row.Of(Kind.Hint, "TAB/Q/E VIEW   UP/DOWN NAV   ENTER SELECT   B BACK   L LAUNCH   U UNDOCK   ESC HANGAR"));

        // ── Tab indicator ──────────────────────────────────────────────────
        rows.Add(Row.Blank());
        string tabLine = (m.Tab == ShopTab.Hulls ? "> [HULLS] " : "  [HULLS] ")
                       + (m.Tab == ShopTab.Parts ? "> [PARTS] " : "  [PARTS] ");
        rows.Add(new Row(Kind.Section, tabLine));

        // ── Current ship ───────────────────────────────────────────────────
        rows.Add(Row.Blank());
        rows.Add(Row.Of(Kind.Section, "CURRENT SHIP"));
        var curHull = FindHull(cat, m.CurrentHullId);
        rows.Add(Row.Of(Kind.Info, curHull is { } h
            ? $"hull: {AsciiSafe(h.Name)}   (${h.Price})"
            : $"hull: #{m.CurrentHullId}"));
        if (m.CurrentParts.Count == 0)
            rows.Add(Row.Of(Kind.Muted, "  (no parts mounted)"));
        else
            foreach (var p in m.CurrentParts)
            {
                var pt = LookupPart(cat, p.PartId);
                rows.Add(Row.Of(Kind.Info, pt is { } pc
                    ? $"  {AsciiSafe(pc.Name)}   (${pc.Price}) [{Cat(pt.Value)}]"
                    : $"  part #{p.PartId}"));
            }

        // ── Active view rows ───────────────────────────────────────────────
        rows.Add(Row.Blank());
        switch (m.Page)
        {
            case ShopModel.ShopPage.HullList:
                BuildHullList(rows, m);
                break;
            case ShopModel.ShopPage.SlotList:
                BuildSlotList(rows, m);
                break;
            case ShopModel.ShopPage.SlotParts:
                BuildSlotParts(rows, m);
                break;
        }

        // ── Pending loadout + price ────────────────────────────────────────
        rows.Add(Row.Blank());
        rows.Add(Row.Of(Kind.Section, "PENDING LOADOUT"));
        var pendHull = FindHull(cat, m.EffectiveHullId);
        string hullName = pendHull is { } ph ? AsciiSafe(ph.Name) : $"#{m.EffectiveHullId}";
        string partSummary = m.ChosenParts.Count == 0
            ? "(none)"
            : string.Join(", ", m.ChosenParts.Select(p => LookupPart(cat, p.PartId) is { } pc
                ? AsciiSafe(pc.Name) : $"#{p.PartId}"));
        rows.Add(Row.Of(Kind.Info, $"hull: {hullName}"));
        rows.Add(Row.Of(Kind.Info, $"parts: {partSummary}"));

        rows.Add(Row.Of(Kind.Section, "PRICE"));
        rows.Add(Row.Of(Kind.Info, $"new loadout value: ${m.NewLoadoutValue}"));
        rows.Add(Row.Of(Kind.Info, $"current ship value: ${m.CurrentShipValue}"));
        rows.Add(Row.Of(m.Cost < 0 ? Kind.HighlightFit : Kind.Info,
            m.Cost < 0 ? $"refund: ${-m.Cost}" : $"cost: ${m.Cost}"));
        if (m.AffordabilityKnown)
            rows.Add(Row.Of(m.CanAffordTotal ? Kind.Info : Kind.HighlightNoFit,
                $"money: ${m.MyMoney!.Value}  {(m.CanAffordTotal ? "AFFORDABLE" : "CANNOT AFFORD")}"));
        if (m.BlockReason is { } br)
            rows.Add(new Row(Kind.HighlightNoFit, $"  !! {br}"));

        if (m.WillSwapHull)
            foreach (var dp in m.ChosenPartsThatDontFitEffectiveHull)
            {
                var dpt = LookupPart(cat, dp.PartId);
                rows.Add(new Row(Kind.HighlightNoFit,
                    dpt is { } dc
                        ? $"  ! {AsciiSafe(dc.Name)} - no hardpoint on {hullName} - DROPPED on launch"
                        : $"  ! part #{dp.PartId} - no hardpoint on {hullName} - DROPPED on launch"));
            }

        return rows;
    }

    private static void BuildHullList(List<Row> rows, ShopModel m)
    {
        rows.Add(Row.Of(Kind.Hint, "Q/PREV  E/NEXT  cycle hulls   ENTER pick   (flat list = native hull menu)"));
        var cat = m.Catalog;
        for (int i = 0; i < m.BuyableHulls.Count; i++)
        {
            var hl = m.BuyableHulls[i];
            char marker = i == m.Cursor ? '>' : MarkHull(m, hl);
            rows.Add(new Row(Kind.Item, $"{marker} {AsciiSafe(hl.Name),-28} ${hl.Price,-6} [HULL]",
                Highlight: i == m.Cursor, Greyed: m.HullRowGreyed(hl)));
        }
        if (m.BuyableHulls.Count == 0)
            rows.Add(Row.Of(Kind.Muted, "  (no buyable hulls at this station)"));
    }

    private static void BuildSlotList(List<Row> rows, ShopModel m)
    {
        var cat = m.Catalog;
        var pendHull = FindHull(cat, m.EffectiveHullId);
        string hullName = pendHull is { } ph ? AsciiSafe(ph.Name) : $"#{m.EffectiveHullId}";
        rows.Add(Row.Of(Kind.Hint, $"slots for: {hullName}   ENTER open slot, Q/E switch to HULLS"));
        if (m.Slots.Count == 0)
        {
            rows.Add(Row.Of(Kind.Muted, "  (this hull has no equipment slots)"));
            return;
        }
        for (int i = 0; i < m.Slots.Count; i++)
        {
            var slot = m.Slots[i];
            var inSlot = m.PartInSlot(slot);
            string filled = inSlot is { } ep
                ? LookupPart(cat, ep.PartId) is { } fp ? AsciiSafe(fp.Name) : $"part #{ep.PartId}"
                : "(empty)";
            char marker = i == m.Cursor ? '>' : (inSlot is null ? ' ' : '+');
            string kind = slot.IsWeapon ? (slot.IsTurret ? "TURRET" : "GUN") : CatType(slot.Type);
            rows.Add(new Row(Kind.Item, $"{marker} {slot.Label,-14} {filled,-28} [{kind}]",
                Highlight: i == m.Cursor));
        }
    }

    private static void BuildSlotParts(List<Row> rows, ShopModel m)
    {
        var cat = m.Catalog;
        var slot = m.OpenSlot;
        rows.Add(Row.Of(Kind.Hint, slot is { } s
            ? $"slot: {s.Label}   ENTER mount part, LAST ROW = make empty, B back"
            : "parts"));
        if (slot is null) return;
        var offers = m.OpenSlotParts;
        for (int i = 0; i < offers.Count; i++)
        {
            var o = offers[i];
            char marker = i == m.Cursor ? '>' : (m.SlotFilled(slot.Value) && o.Part.PartId == m.PartInSlot(slot.Value)?.PartId ? '+' : ' ');
            string tag = o.IsLauncher ? $"[{KindTag(o)}] {o.Amount}rds" : $"[{CatType(o.Part.EquipmentType)}]";
            rows.Add(new Row(Kind.Item, $"{marker} {AsciiSafe(o.Part.Name),-28} ${o.Part.Price,-6} {tag}",
                Highlight: i == m.Cursor, Greyed: m.PartRowGreyed(o.Part.PartId, o.Part.Price)));
        }
        if (offers.Count == 0)
            rows.Add(Row.Of(Kind.Muted, "  (no parts fit this slot)"));
        // The trailing "(make slot empty)" row.
        char clearMarker = offers.Count == m.Cursor ? '>' : ' ';
        rows.Add(new Row(Kind.Item, $"{clearMarker} - MAKE SLOT EMPTY -", Highlight: offers.Count == m.Cursor));
    }

    private static DataHullTypeIGC? FindHull(ShopCatalog cat, short hullId)
    {
        foreach (var h in cat.Hulls)
            if (h.HullId == hullId) return h;
        return null;
    }

    private static char MarkHull(ShopModel m, DataHullTypeIGC hull) =>
        m.ChosenHullId == hull.HullId ? '*' :
        hull.HullId == m.CurrentHullId ? '#' : ' ';

    private static DataPartTypeIGC? LookupPart(ShopCatalog cat, short partId)
    {
        foreach (var p in cat.Parts)
            if (p.PartId == partId) return p;
        if (cat.Launchers is { } ls)
            foreach (var l in ls)
                if (l.Part.PartId == partId) return l.Part;
        return null;
    }

    /// <summary>A short ASCII category tag for a slot type.</summary>
    public static string CatType(EquipmentType et) => AsciiSafe(et.ToString());

    /// <summary>A short ASCII category tag for a part's equipment type.</summary>
    public static string Cat(DataPartTypeIGC part) => AsciiSafe(part.EquipmentType.ToString());

    private static string KindTag(SlotPartOffer o)
    {
        // Recover the launcher kind from the catalogue offer for labelling.
        return o.Part.EquipmentType switch
        {
            EquipmentType.Magazine => "MISSILE",
            EquipmentType.Dispenser => "DISPOSABLE",
            EquipmentType.ChaffLauncher => "CHAFF",
            _ => "PART",
        };
    }

    /// <summary>Strip characters outside printable ASCII 32..126.</summary>
    public static string AsciiSafe(string s) => ScoreboardLayout.AsciiSafe(s);
}
