using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P268 — pure (MonoGame-free) layout of the docked/hangar loadout screen.
/// Turns the <see cref="ShopModel"/> into ASCII-safe display rows that
/// <see cref="DockedScreenRenderer"/> draws. Same discipline as
/// <see cref="ScoreboardLayout"/>/<see cref="HelpOverlayLayout"/>: pure,
/// headless-testable, every string printable ASCII 32..126 (the HUD spritefonts
/// bake ASCII only — P214/p217 lesson).
///
/// <para>Row content mirrors the real native shop row (WinTrek/loadout.cpp): a
/// name, a price ("$ N", loadout.cpp:289), and a category line — here folded
/// into the row's trailing tag instead of a second subtitle line, since we are
/// a single-column ASCII list rather than a pixel-painted pane.</para>
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

    /// <summary>One display row.</summary>
    public readonly record struct Row(Kind Kind, string Text, bool Highlight = false)
    {
        public static Row Of(Kind kind, string text) => new(kind, text);
        public static Row Blank() => new(Kind.Blank, "");
    }

    /// <summary>Build the display rows for the given shop model.</summary>
    public static IReadOnlyList<Row> Build(ShopModel m)
    {
        var cat = m.Catalog;
        var rows = new List<Row>();

        rows.Add(Row.Of(Kind.Title, "STATION DOCKED - HANGAR LOADOUT"));
        rows.Add(ShopLayout.Row.Of(Kind.Hint, "UP/DOWN NAV   SPACE SELECT   L LAUNCH   U UNDOCK   T TELEPORT   ,/. BUILD TARGET"));
        rows.Add(Row.Blank());

        // ── Current ship ────────────────────────────────────────────────
        rows.Add(Row.Of(Kind.Section, "CURRENT SHIP"));
        var curHull = cat.Hulls.FirstOrDefault(h => h.HullId == m.CurrentHullId);
        rows.Add(Row.Of(Kind.Info, curHull is { } h
            ? $"hull: {AsciiSafe(h.Name)}   (${h.Price})"
            : $"hull: #{m.CurrentHullId}"));
        if (m.CurrentParts.Count == 0)
        {
            rows.Add(Row.Of(Kind.Muted, "  (no parts mounted)"));
        }
        else
        {
            foreach (var p in m.CurrentParts)
            {
                var pt = LookupPart(cat, p.PartId);
                rows.Add(Row.Of(Kind.Info, pt is { } pc
                    ? $"  {AsciiSafe(pc.Name)}   (${pc.Price}) [{Cat(pt.Value)}]"
                    : $"  part #{p.PartId}"));
            }
        }
        rows.Add(Row.Blank());

        // ── Buyable shop list ───────────────────────────────────────────
        rows.Add(Row.Of(Kind.Section, "SHOP (buyable hulls, parts & missile launchers)"));

        for (int i = 0; i < cat.Hulls.Count; i++)
        {
            var hl = cat.Hulls[i];
            char marker = CursorOnItem(m, i) ? '>' : MarkHull(m, hl);
            rows.Add(new Row(Kind.Item, $"{marker} {AsciiSafe(hl.Name),-28} ${hl.Price,-6} [HULL]",
                Highlight: m.Cursor == i));
        }
        for (int i = 0; i < cat.Parts.Count; i++)
        {
            var pt = cat.Parts[i];
            int flat = cat.Hulls.Count + i;
            char marker = CursorOnItem(m, flat) ? '>' : MarkPart(m, pt);
            rows.Add(new Row(Kind.Item, $"{marker} {AsciiSafe(pt.Name),-28} ${pt.Price,-6} [{Cat(pt)}]",
                Highlight: m.Cursor == flat));
        }
        // P434 — the real missile launchers sit AFTER hulls and parts as their
        // own rows. Their display name is the real launcher name (from the
        // missile type's LauncherName via FromLauncher) and the ammo column
        // shows the launcher's REAL carried ammo (DataLauncherTypeIGC.Amount) —
        // the native shop has no price datum for a launcher, so the price
        // column sensibly reads its ammo count instead of a fabricated $ value.
        if (cat.Launchers is { Count: > 0 } ls)
        {
            for (int i = 0; i < ls.Count; i++)
            {
                var lo = ls[i];
                int flat = cat.Hulls.Count + cat.Parts.Count + i;
                char marker = CursorOnItem(m, flat) ? '>' : MarkLauncher(m, lo);
                rows.Add(new Row(Kind.Item,
                    $"{marker} {AsciiSafe(lo.Part.Name),-28} $n/a {lo.Ammo}rds [LAUNCHER]",
                    Highlight: m.Cursor == flat));
            }
        }
        rows.Add(Row.Blank());

        // ── Pending loadout ─────────────────────────────────────────────
        rows.Add(Row.Of(Kind.Section, "PENDING LOADOUT"));
        var pendHull = cat.Hulls.FirstOrDefault(h => h.HullId == m.EffectiveHullId);
        string hullName = pendHull is { } ph ? AsciiSafe(ph.Name) : $"#{m.EffectiveHullId}";
        string partSummary = m.ChosenParts.Count == 0
            ? "(none - keeps current loadout)"
            : string.Join(", ", m.ChosenParts.Select(p => LookupPart(cat, p.PartId) is { } pc
                ? AsciiSafe(pc.Name)
                : $"#{p.PartId}"));
        rows.Add(Row.Of(Kind.Info, $"hull: {hullName}"));
        rows.Add(Row.Of(Kind.Info, $"parts: {partSummary}"));

        // P335 — when a genuinely different hull is chosen, the server (P334)
        // drops any requested part the new hull can't mount (CanMount). Warn on
        // each such chosen part so the pending loadout never promises parts the
        // server will silently discard. Gated on a real swap: a same-hull
        // loadout is free-mounted and nothing is dropped.
        if (m.WillSwapHull)
        {
            foreach (var dp in m.ChosenPartsThatDontFitEffectiveHull)
            {
                var dpt = LookupPart(cat, dp.PartId);
                rows.Add(new Row(Kind.HighlightNoFit,
                    dpt is { } dc
                        ? $"  ! {AsciiSafe(dc.Name)} - no hardpoint on {hullName} - DROPPED on launch"
                        : $"  ! part #{dp.PartId} - no hardpoint on {hullName} - DROPPED on launch"));
            }
        }

        // P285 — Builder-target chooser, shown only when the effective hull is
        // a Builder/Constructor-class hull: a small cycle-through list of the
        // station types this Builder may construct (tech-gated the same way the
        // shop's hull/part offer is). The chosen type is sent to the server on
        // launch, which sets the ship's Ship.BuilderStationType.
        if (m.IsBuilderActive)
        {
            rows.Add(Row.Blank());
            rows.Add(Row.Of(Kind.Section, "BUILDER TARGET"));
            if (m.BuilderTargets.Count == 0)
            {
                rows.Add(Row.Of(Kind.Muted, "  (no station type offered for this builder here)"));
            }
            else if (m.CurrentBuilderTarget is { } bt)
            {
                rows.Add(Row.Of(Kind.HighlightFit, $"  -> {AsciiSafe(bt.Name),-22}   ,/. CYCLES"));
            }
            else
            {
                rows.Add(Row.Of(Kind.Muted, "  (none chosen - cycle with ,/.)"));
            }
        }

        return rows;
    }

    private static bool CursorOnItem(ShopModel m, int flatIndex) => m.Cursor == flatIndex;

    private static char MarkHull(ShopModel m, DataHullTypeIGC hull) =>
        m.ChosenHullId == hull.HullId ? '*' :
        hull.HullId == m.CurrentHullId ? '#' : ' ';

    private static char MarkPart(ShopModel m, DataPartTypeIGC part) =>
        m.ChosenParts.Any(p => p.PartId == part.PartId) ? '+' : ' ';

    private static char MarkLauncher(ShopModel m, LauncherOffer lo) =>
        m.ChosenParts.Any(p => p.PartId == lo.Part.PartId) ? '+' : ' ';

    private static DataPartTypeIGC? LookupPart(ShopCatalog cat, short partId)
    {
        foreach (var p in cat.Parts)
            if (p.PartId == partId) return p;
        // P434 — a mounted/chosen launcher resolves through the real launcher
        // table so the current-ship / pending-loadout lines and the P335 fit
        // warnings name a bought launcher sensibly (not "part #id").
        if (cat.Launchers is { } ls)
            foreach (var l in ls)
                if (l.Part.PartId == partId) return l.Part;
        return null;
    }

    /// <summary>A short ASCII category tag for a part's equipment type.</summary>
    public static string Cat(DataPartTypeIGC part) => AsciiSafe(part.EquipmentType.ToString());

    /// <summary>Strip characters outside printable ASCII 32..126 (see
    /// <see cref="ScoreboardLayout.AsciiSafe"/>).</summary>
    public static string AsciiSafe(string s) => ScoreboardLayout.AsciiSafe(s);
}
