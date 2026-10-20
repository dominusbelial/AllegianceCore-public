using System;
using System.Collections.Generic;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P487 — pure (MonoGame-free) layout of the docked <b>investment</b> pane. Turns
/// an <see cref="InvestmentPaneModel"/> into the same ASCII-safe
/// <see cref="ShopLayout.Row"/> stream <see cref="DockedScreenRenderer"/> already
/// draws, so the investment pane reuses the docked shop's rendering style and
/// viewport discipline with no new renderer. Rows are grouped by bucket kind
/// (developments, drones &amp; ships, stations), each showing name, price,
/// invested / progress percentage, and remaining build time — the fields native's
/// <c>PurchasesPaneImpl</c> paints (loadout.cpp:276-360).
/// </summary>
public static class InvestmentLayout
{
    /// <summary>Build the display rows for the given investment model.</summary>
    public static IReadOnlyList<ShopLayout.Row> Build(InvestmentPaneModel m)
    {
        var rows = new List<ShopLayout.Row>();

        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Title, "TEAM INVESTMENT"));
        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Hint,
            "UP/DOWN NAV  SPACE INVEST  R PARTIAL  S SHOW-ALL  H HIDE-COMPLETED  [/] TABS  ESC/B CLOSE"));
        rows.Add(ShopLayout.Row.Blank());

        if (!m.MissionInProgress || !m.HasSide)
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.HighlightNoFit,
                "INVESTMENT CLOSED - need an in-progress mission and a team side"));

        if (m.MyMoney is { } mon)
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Info,
                $"money: ${mon}"));
        else
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted, "money: unknown"));

        rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Info,
            $"show-all: {(m.ShowAll ? "ON" : "OFF")}   hide-completed: {(m.HideCompleted ? "ON" : "OFF")}   max drones/type: {m.MaxDronesPerTeam}"));

        rows.Add(ShopLayout.Row.Blank());

        if (m.SelectedGroup is not null)
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Section, string.Join(" | ", m.Groups.Select(g =>
                g == m.SelectedGroup ? $"[{InvestmentPaneModel.GroupNames[g]}]"
                    : m.GroupEnabled(g) ? InvestmentPaneModel.GroupNames[g] : $"({InvestmentPaneModel.GroupNames[g]})"))));
        var items = m.Items;
        BucketItemType? lastGroup = null;
        for (int i = 0; i < items.Count; i++)
        {
            var b = items[i];
            if (lastGroup is not { } lg || lg != b.ItemType)
            {
                rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Section, GroupLabel(b.ItemType)));
                lastGroup = b.ItemType;
            }
            rows.Add(ItemRow(m, b, i));
        }

        if (items.Count == 0)
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Muted,
                "(no fundable buckets - press S to SHOW-ALL, or H to unhide completed)"));

        return rows;
    }

    // Native PartInfoPaneImpl selection: loadout.cpp:1376-1409,1453-1499.
    public static IReadOnlyList<ShopLayout.Row> Detail(InvestmentPaneModel m)
    {
        if (m.Current is not { } b) return [];
        var rows = new List<ShopLayout.Row>
        {
            ShopLayout.Row.Of(ShopLayout.Kind.Title, AsciiSafe(b.Name)),
            ShopLayout.Row.Of(ShopLayout.Kind.Info, $"Cost: ${b.Price}   Time: {b.TimeToBuildSec} Sec."),
            ShopLayout.Row.Of(ShopLayout.Kind.Info, $"Funded: {m.PercentBought(b)}%   Remaining: ${m.RemainingMoney(b)} / {m.RemainingTimeMs(b) / 1000}s"),
            ShopLayout.Row.Of(ShopLayout.Kind.Info, m.Completed(b) ? "Complete" : m.CanInvest(b) ? "Ready to invest" : "Investment unavailable")
        };
        // Bound each description line so the detail pane remains usable at 720p.
        string text = AsciiSafe(b.Description);
        while (text.Length > 0)
        {
            int length = Math.Min(42, text.Length);
            if (length < text.Length && text.LastIndexOf(' ', length - 1, length) is > 0 and var split) length = split;
            rows.Add(ShopLayout.Row.Of(ShopLayout.Kind.Info, text[..length]));
            text = text[length..].TrimStart();
        }
        return rows;
    }

    private static ShopLayout.Row ItemRow(InvestmentPaneModel m, InvestmentBucket b, int index)
    {
        char marker = index == m.Cursor ? '>' : m.Completed(b) ? '*' : ' ';
        string kind = KindTag(b.ItemType);
        bool investable = m.CanInvest(b);
        long timeLeftSec = m.RemainingTimeMs(b) / 1000;
        string text =
            $"{marker} {AsciiSafe(b.Name),-26} ${b.Price,-7} {m.PercentBought(b),3}%  " +
            $"${m.MoneyOf(b),6}/{b.Price,-6} {timeLeftSec,4}s [{kind}]";
        // A visible-but-uninvestable row (e.g. shown via SHOW-ALL) is greyed; the
        // most common reasons (completed, drone cap, no money) are not spelled out
        // per-row here to keep the list scannable — native greys the Invest button.
        if (!investable)
            text += " (not investable)";
        return new ShopLayout.Row(ShopLayout.Kind.Item, text,
            Highlight: index == m.Cursor, Greyed: !investable);
    }

    /// <summary>Section header for a bucket kind group.</summary>
    private static string GroupLabel(BucketItemType type) => type switch
    {
        BucketItemType.BitDevelopment => "DEVELOPMENTS",
        BucketItemType.BitDroneType => "DRONES",
        BucketItemType.BitShipType => "SHIPS & DRONES",
        BucketItemType.BitStationType => "STATIONS",
        _ => "STRUCTURES",
    };

    /// <summary>The per-row [KIND] tag.</summary>
    private static string KindTag(BucketItemType type) => type switch
    {
        BucketItemType.BitDevelopment => "DEV",
        BucketItemType.BitDroneType => "DRONE",
        BucketItemType.BitShipType => "SHIP",
        BucketItemType.BitStationType => "STATION",
        _ => "BUCKET",
    };

    /// <summary>Strip to printable ASCII (the HUD spritefonts bake ASCII only).</summary>
    private static string AsciiSafe(string s) => ShopLayout.AsciiSafe(s);
}
