using Allegiance.ClientCore.MonoGame.Flight;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P501 — pure (MonoGame-free) touch hit-geometry for the docked / hangar screen
/// (shop + teleport + investment sub-views), so a phone can launch, switch panes,
/// and buy with taps. The docked renderer computes its text positions from font
/// metrics inside Draw (no shared hit-rects exist), so this provides generous,
/// centred zones — the same pragmatic approach as <see cref="PreFlightTaps"/> —
/// that <c>Game1</c> hit-tests released touches against. Headless-testable.
/// </summary>
public static class DockedTouchZones
{
    /// <summary>The LAUNCH button (bottom-right of the docked screen): sends the
    /// same BUY_LOADOUT(fLaunch) the L key does.</summary>
    public static TouchRect LaunchRect(float w, float h) =>
        new(0.66f * w, 0.86f * h, 0.26f * w, 0.10f * h);

    /// <summary>The UNDOCK (no buy) button: sends the FM_C_DOCKED(NA) the U key
    /// does, leaving the hangar without shopping.</summary>
    public static TouchRect UndockRect(float w, float h) =>
        new(0.34f * w, 0.86f * h, 0.24f * w, 0.10f * h);

    /// <summary>Opens/closes the teleport pane (the T key).</summary>
    public static TouchRect TeleportRect(float w, float h) =>
        new(0.60f * w, 0.04f * h, 0.16f * w, 0.07f * h);

    /// <summary>Opens/closes the investment pane (the I key).</summary>
    public static TouchRect InvestRect(float w, float h) =>
        new(0.79f * w, 0.04f * h, 0.16f * w, 0.07f * h);

    /// <summary>Top Y of the docked row list region.</summary>
    public static float RowListTop(float h) => 0.30f * h;

    /// <summary>Height of one docked row band, no smaller than a touch target.</summary>
    public static float RowBandHeight(float h, float minPx = 52f) => MathF.Max(0.052f * h, minPx);

    /// <summary>Map a tap to the row index of a <paramref name="rowCount"/>-row
    /// centred list, or −1 when it misses. Used to move the shop/invest/teleport
    /// cursor onto the tapped row.</summary>
    public static int RowIndex(float w, float h, int rowCount, float tx, float ty)
    {
        if (rowCount <= 0) return -1;
        float bandH = RowBandHeight(h);
        float top = RowListTop(h);
        float left = 0.20f * w;
        float right = 0.80f * w;
        if (tx < left || tx > right) return -1;
        int idx = (int)((ty - top) / bandH);
        if (idx < 0 || idx >= rowCount) return -1;
        float rowTop = top + idx * bandH;
        return ty >= rowTop && ty <= rowTop + bandH ? idx : -1;
    }
}
