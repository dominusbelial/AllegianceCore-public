using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P501 — pure (MonoGame-free) touch hit-geometry for the two pre-flight
/// screens that were keyboard-only (the emulator blocker): SIDE-SELECT and the
/// READY-CHECK. Given a viewport and the side count, these compute generous
/// tap bands/zones that <c>Game1</c> hit-tests released touches against, and
/// <see cref="GamePadFlightRoles"/>/<c>PadNav</c> gives the equivalent gamepad
/// buttons. Decoupled from the (font-metric) text renderers so it is
/// headless-testable; positions are deliberately generous and centred so a thumb
/// tap lands on the right control on any phone/tablet.
/// </summary>
public static class PreFlightTaps
{
    /// <summary>The vertical span of one side-select row, as a fraction of
    /// height, scaled to be no smaller than <paramref name="minPx"/> so the band
    /// stays a comfortable touch target on small screens.</summary>
    public static float SideBandHeight(float h, float minPx = 64f) => MathF.Max(0.062f * h, minPx);

    /// <summary>Top Y of the side list region (below the title + hints).</summary>
    public static float SideListTop(float h) => 0.40f * h;

    /// <summary>The horizontal band width for a side row (centred), as a
    /// fraction of width.</summary>
    public static float SideBandWidth(float w) => 0.66f * w;

    /// <summary>
    /// Map a tap at (tx,ty) to the selectable-side row index it falls in, or −1
    /// when it misses the side list. Row <c>i</c> spans
    /// [<see cref="SideListTop"/>, +SideBandHeight) vertically and the central
    /// [<see cref="SideBandWidth"/>] horizontally. A positive result means the
    /// player tapped that side row (select + confirm).
    /// </summary>
    public static int SideBand(float w, float h, int sideCount, float tx, float ty)
    {
        if (sideCount <= 0) return -1;
        float bandH = SideBandHeight(h);
        float top = SideListTop(h);
        float left = (w - SideBandWidth(w)) / 2f;
        float right = left + SideBandWidth(w);

        if (tx < left || tx > right) return -1;
        int idx = (int)((ty - top) / bandH);
        if (idx < 0 || idx >= sideCount) return -1;
        // Also require the tap to actually be inside this row's vertical extent.
        float rowTop = top + idx * bandH;
        if (ty < rowTop || ty > rowTop + bandH) return -1;
        return idx;
    }

    /// <summary>The big READY / UNREADY toggle button (bottom-centre).</summary>
    public static TouchRect ReadyToggleRect(float w, float h) =>
        new(0.30f * w, 0.78f * h, 0.40f * w, 0.12f * h);

    /// <summary>The mission-owner FORCE-TEAM-READY button (bottom-right of the
    /// ready panel). Small; only the owner should see/use it.</summary>
    public static TouchRect ForceReadyRect(float w, float h) =>
        new(0.74f * w, 0.80f * h, 0.20f * w, 0.09f * h);
}
