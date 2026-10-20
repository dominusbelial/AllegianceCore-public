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

    /// <summary>Vertical band reserved for the manual's topic bar under the
    /// header (P757b) — the Controls columns must start below it.</summary>
    public const float TopicBarHeight = 30f;

    /// <summary>Vertical band reserved for the footer note so the last rows
    /// never draw over it (P757b).</summary>
    public const float FooterReserve = 32f;

    /// <summary>Column ceiling for the whole-group <see cref="Layout"/> (the
    /// historical six-column balance).</summary>
    public const int MaxColumns = 6;

    /// <summary>Column ceiling for the splittable Controls reference. The full
    /// 18-category catalogue at 720p needs about eleven columns once the tall
    /// targeting group is split.</summary>
    public const int MaxSectionColumns = 12;

    /// <summary>The usable content band: below the manual topic bar (when
    /// present) and above the footer note. Returning this from one place keeps
    /// the renderer's layout and its tests on the same rule.</summary>
    public static (float Top, float Height) ContentBand(float panelY, float panelH, bool topicBar)
    {
        float top = panelY + Padding + (topicBar ? TopicBarHeight : 0f);
        float bottom = panelY + panelH - Padding - FooterReserve;
        return (top, Math.Max(0f, bottom - top));
    }

    /// <summary>
    /// One group's resolved position: which column and the y of its title row.
    /// </summary>
    public readonly record struct Slot(int Column, float Y);

    /// <summary>One drawable slice of a group: the group index plus the entry
    /// range it shows. A tall group (the full target/order catalog) is split
    /// into several slices so no single column can exceed the panel height.</summary>
    public readonly record struct Section(int GroupIndex, int FirstEntry, int EntryCount, int Column, float Y)
    {
        public int EndEntry => FirstEntry + EntryCount;
        public float Height => RowHeight + TitleGap + EntryCount * RowHeight;
    }

    /// <summary>Split every group into palette-sized sections and balance the
    /// sections into columns. This is the Controls reference's layout: unlike
    /// <see cref="Layout"/> it can place one group's entries in several columns,
    /// which is what lets the 52-entry targeting group fit a 720p panel.</summary>
    public static Section[] LayoutSections(IReadOnlyList<int> entryCounts, float panelY, float panelH)
    {
        var blocks = new List<(int Group, int First, int Count)>();
        for (int g = 0; g < entryCounts.Count; g++)
        {
            int remaining = Math.Max(0, entryCounts[g]);
            int first = 0;
            if (remaining == 0)
            {
                blocks.Add((g, 0, 0));
                continue;
            }
            // A section's own footprint (title row + gap) must also fit; keep at
            // least one entry per section even on an absurdly short panel.
            int maxRows = Math.Max(1, (int)MathF.Floor((panelH - RowHeight - TitleGap) / RowHeight));
            while (remaining > 0)
            {
                int take = Math.Min(maxRows, remaining);
                blocks.Add((g, first, take));
                first += take;
                remaining -= take;
            }
        }

        var sections = new Section[blocks.Count];
        for (int k = 1; k <= MaxSectionColumns; k++)
        {
            var columnHeights = new float[k];
            var order = new List<int>();
            for (int i = 0; i < blocks.Count; i++) order.Add(i);
            if (k > 1) order.Sort((a, b) =>
            {
                float ha = RowHeight + TitleGap + blocks[a].Count * RowHeight;
                float hb = RowHeight + TitleGap + blocks[b].Count * RowHeight;
                int height = hb.CompareTo(ha);
                return height != 0 ? height : a.CompareTo(b);
            });

            foreach (int i in order)
            {
                var block = blocks[i];
                int col = 0;
                for (int c = 1; c < k; c++)
                    if (columnHeights[c] < columnHeights[col]) col = c;
                sections[i] = new Section(block.Group, block.First, block.Count, col, panelY + columnHeights[col]);
                columnHeights[col] += RowHeight + TitleGap + block.Count * RowHeight + GroupGap;
            }

            bool overflows = false;
            for (int c = 0; c < k; c++)
                if (columnHeights[c] - GroupGap > panelH) overflows = true;
            if (!overflows || k == MaxSectionColumns) return sections;
        }
        return sections;
    }

    /// <summary>
    /// Balance-assign each group (by its measured height) to a column and a
    /// vertical position, all inside the panel band [<paramref name="panelY"/>,
    /// <paramref name="panelY"/>+<paramref name="panelH"/>].
    ///
    /// Column count is derived so the table spreads into a comfortable number of
    /// narrow columns (driven by both the row total and the available panel
    /// height — never fewer than 1 nor more than <see cref="MaxColumns"/>), then
    /// each group is placed in the currently shortest column (leftmost wins
    /// ties), which keeps columns near-equal and guarantees each column's total
    /// fits the panel whenever that is possible within the column cap.
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
        k = System.Math.Clamp(k, 1, MaxColumns);

        // A large camera group placed late can overflow even the shortest
        // column. Pack tallest-first when balancing multiple columns; preserve
        // catalogue order within a single column and equal-height ties.
        // If the derived column count still cannot fit the band (e.g. 720p),
        // add columns until it does, up to the hard ceiling.
        var order = new List<int>();
        for (int i = 0; i < groupHeights.Count; i++) order.Add(i);

        while (true)
        {
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

            bool overflows = false;
            for (int c = 0; c < k; c++)
                if (columnHeights[c] - GroupGap > panelH) overflows = true;
            if (!overflows || k >= MaxColumns) return slots;
            k++;
        }
    }
}
