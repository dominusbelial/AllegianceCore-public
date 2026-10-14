using System;
using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P257 — pure layout for the F1 keybinding help overlay: distributes the
/// <see cref="KeyBindingTable"/> groups into BALANCED vertical columns so a
/// large, readable reference panel fits without overflow and without overlap.
///
/// Purity discipline mirrors <see cref="StarmapLayout"/>: no MonoGame types on
/// the surface — group *heights* are supplied by the caller (measured with the
/// real spritefont by the renderer) and this class decides only column
/// membership and vertical placement, which is the meaningful testable logic.
/// The renderer turns a column index into an x-offset using the measured column
/// width (text measurement is necessarily font-dependent and stays there).
///
/// Strategy: tallest groups first, each placed in the column with the LOWEST current
/// cumulative height (ties break to the leftmost). This yields evenly-sized
/// columns — a key legibility trait here, where wide two-column lines would
/// clip on narrow viewports but several balanced narrow columns fit — and is
/// deterministic and easy to reason about.
/// </summary>
public sealed class HelpOverlayLayout
{
    /// <summary>Vertical space per binding row (px).</summary>
    public const float RowHeight = 22f;

    /// <summary>Vertical gap reserved after each group's title (px).</summary>
    public const float TitleGap = 10f;

    /// <summary>Vertical gap between successive groups in a column (px).</summary>
    public const float GroupGap = 22f;

    /// <summary>Horizontal gap between columns (px).</summary>
    public const float ColumnGap = 24f;

    /// <summary>Inner margin around the panel content (px).</summary>
    public const float Padding = 22f;

    /// <summary>
    /// One group's resolved position: which column and the y of its title row.
    /// </summary>
    public readonly record struct Slot(int Column, float Y);

    /// <summary>
    /// Balance-assign each group (by its measured height) to a column and a
    /// vertical position, all inside the panel band [<paramref name="panelY"/>,
    /// <paramref name="panelY"/>+<paramref name="panelH"/>].
    ///
    /// Column count is derived so the table spreads into a comfortable number of
    /// narrow columns (driven by both the row total and the available panel
    /// height — never fewer than 1 nor more than 6), then each group is placed
    /// in the currently shortest column (leftmost wins ties), which keeps
    /// columns near-equal and guarantees each column's total fits the panel.
    /// </summary>
    /// <returns>One slot per input height, in the same order.</returns>
    public static Slot[] Layout(
        IReadOnlyList<float> groupHeights,
        float panelY,
        float panelH)
    {
        var slots = new Slot[groupHeights.Count];
        if (groupHeights.Count == 0)
            return slots;

        // Derive a target column count. Both signals aim to keep columns short
        // enough to read comfortably:
        //   - total rows / 10 → aim ~10 rows (2-3 groups) per column;
        //   - summed height / panel height → at minimum the columns must fit
        //     the panel, so never fewer than would overflow it.
        int totalRows = 0;
        float sumHeights = 0f;
        for (int i = 0; i < groupHeights.Count; i++)
        {
            sumHeights += groupHeights[i];
            // A group of height RowHeight maps to TitleGap+RowHeight+GroupGap of
            // footprint; approximate row count from the fixed row pitch.
            totalRows += (int)System.MathF.Ceiling(groupHeights[i] / RowHeight);
        }
        float sumWithGaps = sumHeights + (groupHeights.Count - 1) * GroupGap;
        float rowsForHeight = panelH > 0f ? sumWithGaps / panelH : 1f;
        int k = (int)System.MathF.Ceiling(System.MathF.Max(totalRows / 10f, rowsForHeight));
        k = System.Math.Clamp(k, 1, 6);

        // A large camera group placed late can overflow even the shortest
        // column. Pack tallest-first when balancing multiple columns; preserve
        // catalogue order within a single column and equal-height ties.
        var order = new List<int>();
        for (int i = 0; i < groupHeights.Count; i++) order.Add(i);
        if (k > 1) order.Sort((a, b) =>
        {
            int height = groupHeights[b].CompareTo(groupHeights[a]);
            return height != 0 ? height : a.CompareTo(b);
        });
        var columnHeights = new float[k];
        foreach (int i in order)
        {
            float h = groupHeights[i];

            // Pick the shortest column (leftmost wins ties).
            int col = 0;
            for (int c = 1; c < k; c++)
                if (columnHeights[c] < columnHeights[col]) col = c;

            slots[i] = new Slot(col, panelY + columnHeights[col]);
            columnHeights[col] += h + GroupGap;
        }

        return slots;
    }
}
