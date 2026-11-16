using System;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1038 — the Jet F4 inventory pane presentation (P972 §4.3 second half, §5.6 all 19
/// rows, phase 3B). Pure: every value comes from the shared
/// <see cref="HudInventoryPane"/> rows that the native builder already resolved
/// (<c>WinTrek/inventoryimage.cpp:299-346</c>, <c>consoledata.cpp:684-985</c>), and every
/// stat string is the row's own <see cref="HudInventoryRow.StatsText"/> — split into the
/// Jet column vocabulary, never recomputed. The pane keeps the native anchor, the native
/// row pitch and the native 0.5 s slide (<see cref="InventoryPaneLayout"/>,
/// <c>Artwork/dialog.mdl:5387</c>).
/// </summary>
public static class JetInventoryPresentation
{
    /// <summary>Content width in native px units (× <see cref="InventoryPaneLayout.Scale"/>):
    /// wider than the native 214 px line, because the Jet table shows the §5.6 stat columns
    /// at a legible stroke width (P972 §4.5: "detailed stats remain one F4 pane away").</summary>
    public const float ContentWidth = 480f;

    /// <summary>Row pitch and end caps: the native pane's own values, so the pane occupies
    /// the same vertical band and slides the same 190 px.</summary>
    public const float RowHeight = InventoryPaneLayout.RowHeight;
    public const float CapHeight = InventoryPaneLayout.CapHeight;

    /// <summary>Native slide distance (px) the open animation travels
    /// (<c>dialog.mdl:5387</c>, 0.5 s) — the same offset the shared pane applies.</summary>
    public const float SlideDistance = 190f;

    /// <summary>Row-local column anchors (native px units × pane scale). The stat columns
    /// get the wider half of the line: the Jet stat vocabulary (RNG/DMG/ROF …) is longer
    /// than the native combined stats string.</summary>
    public const float KeyColumn = 8f;
    public const float MarkerColumn = 26f;
    public const float NameColumn = 38f;
    public const float QuantityColumn = 190f;
    public const float StateColumn = 224f;
    public const float StatsColumn = 282f;

    /// <summary>The row text is drawn at this fraction of the pane scale (a table font: the
    /// native row font at the full pane scale cannot fit the stat columns).</summary>
    public const float RowTextScale = 0.72f;

    /// <summary>Progress bar (MOUNT / ARM) width inside the state column.</summary>
    public const float ProgressBarWidth = 44f;
    public const float ProgressBarHeight = 8f;

    /// <summary>The compact layout floor (P1036/P1037 rule).</summary>
    public const float CompactHeight = 700f;

    public static bool Compact(float viewportHeight) => viewportHeight < CompactHeight;

    /// <summary>The pane scale: the native inventory scale, shared with
    /// <see cref="InventoryPaneLayout"/> so the anchor clamp and the slide match.</summary>
    public static float Scale(float viewportHeight) => InventoryPaneLayout.Scale(viewportHeight);

    public static float Width(float viewportWidth, float viewportHeight)
    {
        float scale = Scale(viewportHeight);
        float available = MathF.Max(0f, InventoryPaneLayout.RightX(viewportWidth, viewportHeight));
        return MathF.Min(ContentWidth * scale, available);
    }

    /// <summary>Screen x of the pane's left edge (right edge is the native
    /// <c>SideBottomRight(-36,…)</c> anchor).</summary>
    public static float Left(float viewportWidth, float viewportHeight)
        => InventoryPaneLayout.RightX(viewportWidth, viewportHeight) - Width(viewportWidth, viewportHeight);

    /// <summary>Screen y of the pane's bottom edge: the native clamp above the mini-map
    /// (<see cref="InventoryPaneLayout.BottomY"/>) plus the shared slide offset.</summary>
    public static float Bottom(float viewportWidth, float viewportHeight, float slide)
        => InventoryPaneLayout.BottomY(viewportWidth, viewportHeight)
           + (1f - Math.Clamp(slide, 0f, 1f)) * SlideDistance;

    public static float Height(int rowCount, float viewportHeight)
        => (CapHeight * 2f + RowHeight * rowCount) * Scale(viewportHeight);

    public static float Top(int rowCount, float viewportWidth, float viewportHeight, float slide)
        => Bottom(viewportWidth, viewportHeight, slide) - Height(rowCount, viewportHeight);

    /// <summary>The <c>SLOT / KEY / PART</c> header band inside the top cap.</summary>
    public static float HeaderTop(int rowCount, float viewportWidth, float viewportHeight, float slide)
        => Top(rowCount, viewportWidth, viewportHeight, slide) + CapHeight * 0.25f * Scale(viewportHeight);

    /// <summary>Screen y of row <paramref name="index"/>'s top edge.</summary>
    public static float RowTop(int rowCount, float viewportWidth, float viewportHeight, float slide, int index)
        => Top(rowCount, viewportWidth, viewportHeight, slide)
           + (CapHeight + RowHeight * index) * Scale(viewportHeight);

    /// <summary>One labelled stat run of a row (<c>RNG 800m</c>).</summary>
    public readonly record struct StatRun(string Label, string Value);

    /// <summary>
    /// Split a row's shared <see cref="HudInventoryRow.StatsText"/> into the Jet stat
    /// columns, in the native order. No value is computed here — the numbers are the
    /// shared string's own fields, re-labelled per P972 §5.6. Returns 0 for a row without
    /// stats (an empty slot, a cargo row, a chaff/mine/probe count that the quantity column
    /// already carries, or a mounting/arming row).
    /// </summary>
    public static int StatsRuns(in HudInventoryRow row, Span<StatRun> runs)
    {
        if (runs.Length == 0 || row.StatsText is not { Length: > 0 } stats) return 0;
        int count = 0;
        switch (row.Kind)
        {
            case HudInventoryRowKind.Weapon:
            {
                // Native "800m / 15 X 8": range / damage X rate.
                int slash = stats.IndexOf(" / ", StringComparison.Ordinal);
                if (slash < 0) { Push(runs, ref count, "RNG", stats); break; }
                Push(runs, ref count, "RNG", stats[..slash]);
                string rest = stats[(slash + 3)..];
                int cross = rest.IndexOf(" X ", StringComparison.Ordinal);
                if (cross < 0) { Push(runs, ref count, "DMG", rest); break; }
                Push(runs, ref count, "DMG", rest[..cross]);
                Push(runs, ref count, "ROF", rest[(cross + 3)..]);
                break;
            }
            case HudInventoryRowKind.Magazine:
            {
                // Native "<amount> / <range>m / <damage>": the amount is the QTY column.
                int first = stats.IndexOf(" / ", StringComparison.Ordinal);
                if (first < 0) break;
                string rest = stats[(first + 3)..];
                int second = rest.IndexOf(" / ", StringComparison.Ordinal);
                Push(runs, ref count, "RNG", second < 0 ? rest : rest[..second]);
                if (second >= 0) Push(runs, ref count, "DMG", rest[(second + 3)..]);
                break;
            }
            case HudInventoryRowKind.Shield:
            {
                // Native "<strength> / <recharge> sec".
                int slash = stats.IndexOf(" / ", StringComparison.Ordinal);
                if (slash < 0) { Push(runs, ref count, "SHD STR", stats); break; }
                Push(runs, ref count, "SHD STR", stats[..slash]);
                Push(runs, ref count, "RECH", Seconds(stats[(slash + 3)..]));
                break;
            }
            case HudInventoryRowKind.Afterburner:
            {
                // Native "<top speed>mps / <endurance>" (Inf. when the fuel never runs out).
                int slash = stats.IndexOf(" / ", StringComparison.Ordinal);
                if (slash < 0) { Push(runs, ref count, "AB SPD", stats); break; }
                Push(runs, ref count, "AB SPD", stats[..slash]);
                Push(runs, ref count, "AB END", stats[(slash + 3)..]);
                break;
            }
            case HudInventoryRowKind.Cloak:
            {
                // Native "<strength>% / <endurance> sec" or endurance only when the live
                // strength has not arrived yet (HudInventoryPaneBuilder).
                int slash = stats.IndexOf(" / ", StringComparison.Ordinal);
                if (slash < 0) { Push(runs, ref count, "CLK END", Seconds(stats)); break; }
                Push(runs, ref count, "CLK STR", stats[..slash]);
                Push(runs, ref count, "CLK END", Seconds(stats[(slash + 3)..]));
                break;
            }
        }
        return count;
    }

    /// <summary>The Jet state word of a row: mounting and arming keep their shared
    /// fractions, a launcher that is fully mounted and armed reads READY, and every other
    /// mounted row (ReadyState 3) is ready by the shared builder's own definition.</summary>
    public static string? StateWord(in HudInventoryRow row, out float? progressFraction)
    {
        progressFraction = null;
        if (row.IsEmpty) return "EMPTY";
        switch (row.ReadyState)
        {
            case 1:
                progressFraction = row.MountedFraction;
                return "MOUNT";
            case 2:
                progressFraction = row.ArmedFraction ?? row.MountedFraction;
                return "ARM";
            case 3:
                return row.Kind is HudInventoryRowKind.Magazine or HudInventoryRowKind.Dispenser
                    ? "READY" : null;
            default:
                return null;
        }
    }

    /// <summary>True when the row carries a state/progress column at all.</summary>
    public static bool HasState(in HudInventoryRow row) => StateWord(row, out _) is not null;

    /// <summary>Native <c>"3 sec"</c> → the Jet <c>"3 s"</c> column form (no value change).</summary>
    private static string Seconds(string value)
        => value.EndsWith(" sec", StringComparison.Ordinal)
            ? string.Concat(value.AsSpan(0, value.Length - 4), " s")
            : value;

    private static void Push(Span<StatRun> runs, ref int count, string label, string value)
    {
        if (count >= runs.Length || value.Length == 0) return;
        runs[count++] = new StatRun(label, value);
    }
}
