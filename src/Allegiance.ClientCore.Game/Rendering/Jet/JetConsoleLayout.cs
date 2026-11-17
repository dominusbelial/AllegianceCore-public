namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1040 — the Jet console geometry (P972 §5.5 rows 1–18, phase 3D). Pure numbers only,
/// so the placement rules are pinned headlessly and the renderer cannot invent a box.
/// <para><b>Why Jet moves the console.</b> Native hangs the chat pane at absolute screen
/// points (<c>CommPane SideTop(-160,-126)</c>, <c>dialog.mdl:5394</c>) because its only
/// centre furniture is the gauge ring; Jet's heading tape owns the top-centre band
/// (<see cref="JetHudRenderer"/>'s <c>_headingPlate</c>), its caution panel the top-right
/// (<see cref="JetCautionModel"/>) and its stores block the lower-left
/// (<see cref="JetStoresPanel"/>). The console therefore occupies the two bands Jet leaves
/// free: the LEFT COLUMN (chat log, then the sector/turret/orders/mission/lifecycle rows)
/// and the BOTTOM-LEFT strip under the stores block (cargo/flight-parts, which keep their
/// native fixed offsets, plus the three ordinary tip slots). The fully measured boxes are
/// in the report §4.
/// </para>
/// </summary>
public static class JetConsoleLayout
{
    /// <summary>Left edge of the whole console family, standard-stroke units (× scale).
    /// The speed tape's numeric column ends at ≈76 units and its tick labels at ≈72, so the
    /// console starts clear of that instrument at every resolution (the P1040 review measured
    /// the old 20 px edge striking through the tape numerals); the stores block starts at 96
    /// units, so the column sits in the strip between them and ends above the block.</summary>
    public const float ConsoleLeft = 84f;

    /// <summary>The native bottom-left text gutter (20 px) is retained only as the reference
    /// the port's pre-P1040 lines used; the Jet console starts at <see cref="ConsoleLeft"/>.</summary>
    public const float NativeTextGutter = 20f;

    /// <summary>Native <c>ChatListPane</c> width (355 px, <c>dialog.mdl:180</c>), scaled for
    /// the window so the pane keeps the same glyph/box ratio as the rest of the Jet HUD.</summary>
    public const float ChatWidth = 355f;

    /// <summary>Native largeFont item height for the pane (14 px, <c>chatpane.cpp:374-380</c>);
    /// the Jet row pitch is the greater of this and the measured line height, so a taller
    /// substitute face can never merge two rows.</summary>
    public const float ChatRowHeight = 14f;

    /// <summary>Native item text inset inside the list pane (3 px, <c>chatpane.cpp:95</c>).</summary>
    public const float ChatPad = 3f;

    /// <summary>Vertical gap between two console-column rows beyond the measured line
    /// height. Two runs of one block must never intersect (<c>TextOverlapSame</c>).</summary>
    public const float RowGap = 3f;

    /// <summary>Gap between the chat pane's bottom edge and the console column.</summary>
    public const float ChatToColumn = 8f;

    /// <summary>
    /// P1040b — rows the LIFEPOD block occupies. The block is survival-critical, so the
    /// column RESERVES it before any other row is placed (P1040b defect 1: at 800×480 the
    /// single free row went to `SECTOR` and the pod's oxygen endurance and return binding
    /// were drawn nowhere).
    /// <para>Standard form (three rows): <c>LIFEPOD</c> / <c>OXY ▮ nn s</c> / <c>RETURN …</c>
    /// (native <c>EjectPodImage</c> identity + <c>GetEndurance</c> gauge, dialog.mdl:1922-1996,
    /// consoledata.cpp:534-545). Compact form (two rows): <c>LIFEPOD OXY ▮ nn s</c> (one row:
    /// the identity, the segmented endurance bar and the numeric endurance) and
    /// <c>RETURN …</c> — the stated compact form the brief allows.</para>
    /// </summary>
    public const int PodRowsStandard = 3;
    public const int PodRowsCompact = 2;

    /// <summary>The LIFEPOD block's rows at this viewport height.</summary>
    public static int PodRows(float viewportHeight)
        => viewportHeight < JetStoresPanel.CompactHeight ? PodRowsCompact : PodRowsStandard;

    /// <summary>Gap between a row's word and its value.</summary>
    public const float WordGap = 5f;

    /// <summary>Gap between the console column's right edge and the pitch ladder's numeral
    /// column (the ladder owns the flight-path band).</summary>
    public const float ColumnToLadder = 6f;

    /// <summary>The widest the console column may be drawn, as a fraction of the viewport
    /// height-independent width: it stops <see cref="JetStoresPanel.LadderColumn"/> short of
    /// the boresight and never crosses the centred vote banner's free span.</summary>
    public const float ColumnHalfWidth = 190f;

    /// <summary>The native cargo / flight-parts text offsets (unscaled px above the bottom,
    /// <c>Game1.InFlightUtility.cs:169</c> / <c>Game1.FlightParts.cs:324</c>). Jet draws the
    /// same two shared lines at the same offsets.</summary>
    public const float CargoLineInset = 132f;
    public const float FlightPartsInset = 158f;

    /// <summary>Top of the first ordinary tip row, px above the viewport bottom (unscaled,
    /// below the cargo line's native offset so the three retained slots read as their own
    /// stack).</summary>
    public const float TipFirstInset = 108f;

    /// <summary>Pitch of the tip stack, px (unscaled; the retained slots are 11 px apart
    /// natively, <c>dialog.mdl:5347-5349</c>, and the Jet keyline needs a little more).</summary>
    public const float TipPitch = 19f;

    /// <summary>The compose panel's slide travel: at progress 0 it sits this many standard
    /// units below its home under the chat pane and rises into place over the shared 0.5 s
    /// (<see cref="PaneSlideTracker.NativeSlideSeconds"/>).</summary>
    public const float ComposeTravel = 18f;

    /// <summary>The right edge the console column may reach at this viewport.</summary>
    public static float ColumnRight(float viewportWidth, float scale)
    {
        float s = scale > 0f ? scale : 1f;
        float ladder = viewportWidth * 0.5f - JetStoresPanel.LadderColumn * s - ColumnToLadder * s;
        float half = viewportWidth * 0.5f - ColumnHalfWidth * s;
        return MathF.Min(ladder, half);
    }

    /// <summary>The widest a CENTRED banner (the vote line) may be while staying clear of the
    /// console column on the left.</summary>
    public static float CentredSpanWidth(float viewportWidth, float scale)
    {
        float s = scale > 0f ? scale : 1f;
        return MathF.Max(0f, 2f * (viewportWidth * 0.5f - (ColumnRight(viewportWidth, s) + ColumnGap(s))));
    }

    /// <summary>A gap in standard-stroke units.</summary>
    public static float ColumnGap(float scale) => 8f * (scale > 0f ? scale : 1f);

    /// <summary>The chat pane's box: the console edge, <paramref name="top"/> (below the
    /// heading furniture and the speed tape's caption), the native width and
    /// <paramref name="lines"/> rows of <paramref name="rowPitch"/> plus the native 8 px frame.
    /// The width is capped so the pane can never reach the pitch ladder's numeral column.</summary>
    public static RectangleF ChatPane(float scale, float top, int lines, float rowPitch,
        float viewportWidth, float ladderColumnLeft)
    {
        float s = scale > 0f ? scale : 1f;
        float left = ConsoleLeft * s;
        float width = MathF.Min(ChatWidth * s, MathF.Max(0f, ladderColumnLeft - ColumnToLadder * s - left));
        return new(left, top, width, 8f * s + rowPitch * Math.Max(0, lines));
    }

    /// <summary>Row pitch of the console column: the measured line height plus
    /// <see cref="RowGap"/>.</summary>
    public static float RowPitch(float textHeight, float scale)
        => textHeight + RowGap * (scale > 0f ? scale : 1f);

    /// <summary>
    /// P1040b — the height the LIFEPOD block reserves at the bottom of the console column:
    /// its rows plus the gap the column keeps to the chat/compose block. The column places
    /// the critical block FIRST (anchored to the band's bottom edge) and only then fills the
    /// rows above it, so the pod is drawn whenever the model says `PodVisible` and a lower
    /// row (sector/turret/orders/mission) is dropped instead.
    /// </summary>
    public static float PodBlockHeight(float viewportHeight, float textHeight, float scale)
        => PodRows(viewportHeight) * RowPitch(textHeight, scale);

    /// <summary>
    /// P1040b — the chat pane's DRAWN line count once the critical block is reserved: the
    /// configured count, reduced only as far as the block needs (never below one line). At
    /// 800×480 the log is a scrolling native window with the OLDER marker, so a shorter
    /// window loses no reading; the lifepod panel loses everything if it is not drawn.
    /// </summary>
    public static int ChatLinesWithReserve(int lines, float top, float columnBottom,
        float rowPitch, float scale, float reservedHeight)
    {
        int configured = Math.Max(1, lines);
        if (reservedHeight <= 0f) return configured;
        float s = scale > 0f ? scale : 1f;
        float room = columnBottom - top - ChatToColumn * s - reservedHeight - 8f * s;
        if (rowPitch <= 0f) return configured;
        int fit = (int)MathF.Floor((room + 0.01f) / rowPitch);
        return Math.Max(1, Math.Min(configured, fit));
    }

    /// <summary>The top of tip slot <paramref name="slot"/> (0 = newest), px from the top.</summary>
    public static float TipTop(float viewportHeight, int slot)
        => viewportHeight - TipFirstInset + TipPitch * slot;

    /// <summary>The cargo line's top (native fixed offset).</summary>
    public static float CargoTop(float viewportHeight) => viewportHeight - CargoLineInset;

    /// <summary>The flight-parts line's top (native fixed offset).</summary>
    public static float FlightPartsTop(float viewportHeight) => viewportHeight - FlightPartsInset;

    // ── P1040b: the bottom band and the controls-hint slots ─────────────────

    /// <summary>Gap the bottom band and the hint keep from any other element.</summary>
    public const float BandGap = 4f;

    /// <summary>
    /// P1040b — the right edge of a BOTTOM-BAND row (cargo / flight-parts / tips / the
    /// controls hint) at <paramref name="rowTop"/>: the viewport edge gutter, trimmed to the
    /// first fixed furniture box that the row's own ink band would cross. The bottom band runs
    /// below the stores block and below the ladder's last rung at both required resolutions,
    /// so in practice only the ladder's <c>PITCH</c> caption and the radar's bottom edge can
    /// bound it — and a row is never allowed to reach either (the P1040 defect cut the reading
    /// to `WEAPONS: G...` because the band was bounded by the *console column* at the ladder's
    /// numeral edge, 152 px, instead of the band it actually occupies).
    /// </summary>
    public static float BandRight(ReadOnlySpan<RectangleF> furniture, float left, float rowTop,
        float rowHeight, float viewportWidth, float scale)
    {
        float s = scale > 0f ? scale : 1f;
        float right = viewportWidth - JetReadoutPlacement.Gutter * s;
        for (int i = 0; i < furniture.Length; i++)
        {
            RectangleF box = furniture[i];
            if (box.Height <= 0f || box.Width <= 0f) continue;
            if (box.X + box.Width <= left) continue;                    // furniture to the left
            if (box.Y >= rowTop + rowHeight || box.Y + box.Height <= rowTop) continue; // other rows
            right = MathF.Min(right, box.X - BandGap * s);
        }
        return MathF.Max(left, right);
    }

    /// <summary>
    /// P1040b — the controls-hint slots, in the order they are tried. The hint is a
    /// one-line tutorial band, so it goes where a band of that kind belongs and never across
    /// the pitch ladder, the chat pane or a live readout:
    /// <list type="number">
    /// <item><b>0 — below the ordinary tip stack</b> (the bottom band's free row),</item>
    /// <item><b>1 — above the chat pane's marker row</b> (the band the native hint's
    /// unscaled anchor lands in at 1080p),</item>
    /// <item><b>2 — the bottom-right band under the radar scope</b>.</item>
    /// </list>
    /// Each candidate is a full-width centred row; the caller accepts the first one whose box
    /// is clear of every obstacle it has registered this frame
    /// (<see cref="IsClear(ReadOnlySpan{RectangleF}, RectangleF)"/>) and fits the viewport, and
    /// otherwise takes the least-covered one — a blocked hint is then counted by the audit,
    /// never silently laid over the ladder.
    /// </summary>
    public static RectangleF HintSlot(int slot, float viewportWidth, float viewportHeight,
        float scale, float width, float height, RectangleF chatPane, float tipBlockBottom,
        float rowPitch, RectangleF radarScope)
    {
        float s = scale > 0f ? scale : 1f;
        float gutter = JetReadoutPlacement.Gutter * s;
        float cx = viewportWidth * 0.5f;
        float y = slot switch
        {
            0 => tipBlockBottom + BandGap * s,
            1 => chatPane.Y - 2f * rowPitch - BandGap * s,
            _ => radarScope.Height > 0f
                ? radarScope.Y + radarScope.Height + BandGap * s
                : viewportHeight - TipFirstInset * 0.5f,
        };
        float x = cx - width * 0.5f;
        if (slot == 2 && radarScope.Height > 0f)
            x = MathF.Max(x, radarScope.X);
        y = Math.Clamp(y, gutter, MathF.Max(gutter, viewportHeight - gutter - height));
        x = Math.Clamp(x, gutter, MathF.Max(gutter, viewportWidth - gutter - width));
        return new RectangleF(x, y, width, height);
    }

    /// <summary>True when <paramref name="box"/> touches none of <paramref name="obstacles"/>.
    /// Two boxes that merely share an edge are not an overlap: the box is inset by
    /// <see cref="ClearanceTolerance"/> so a layout that stops exactly at a furniture edge
    /// (the tip band's own first row under the <c>PITCH</c> caption) is accepted.</summary>
    public static bool IsClear(ReadOnlySpan<RectangleF> obstacles, RectangleF box)
        => Cover(obstacles, box) <= 0f;

    /// <summary>The area <paramref name="box"/> shares with the obstacles, edges that merely
    /// touch excluded.</summary>
    public static float Cover(ReadOnlySpan<RectangleF> obstacles, RectangleF box)
    {
        const float t = ClearanceTolerance;
        var inner = new RectangleF(box.X + t, box.Y + t,
            MathF.Max(0f, box.Width - 2f * t), MathF.Max(0f, box.Height - 2f * t));
        float cover = 0f;
        for (int i = 0; i < obstacles.Length; i++)
            cover += JetReadoutPlacement.OverlapArea(obstacles[i], inner);
        return cover;
    }

    /// <summary>The half-pixel tolerance a slot's clearance test keeps.</summary>
    public const float ClearanceTolerance = 0.5f;
}
