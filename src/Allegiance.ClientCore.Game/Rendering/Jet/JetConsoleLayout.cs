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

    /// <summary>The top of tip slot <paramref name="slot"/> (0 = newest), px from the top.</summary>
    public static float TipTop(float viewportHeight, int slot)
        => viewportHeight - TipFirstInset + TipPitch * slot;

    /// <summary>The cargo line's top (native fixed offset).</summary>
    public static float CargoTop(float viewportHeight) => viewportHeight - CargoLineInset;

    /// <summary>The flight-parts line's top (native fixed offset).</summary>
    public static float FlightPartsTop(float viewportHeight) => viewportHeight - FlightPartsInset;
}
