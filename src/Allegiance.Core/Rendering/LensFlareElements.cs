namespace Allegiance.Core.Rendering;

/// <summary>
/// P589 — the native lens-flare element table, ported verbatim from
/// <c>effect/efimage.cpp:601-613</c> (<c>g_lensFlareData</c>) and the surface
/// mapping of <c>effect/efart.h:8-10</c> +
/// <c>effect/efimage.cpp:643-656</c> (the three <c>AWF_EFFECT_LENS_FLARE_*</c>
/// images).
///
/// Native layout (elements ordered along the line through the screen centre —
/// position <c>m_pos</c> is a signed multiplier of the on-screen offset of the
/// projected light direction, negative = mirrored across the centre):
/// <code>
/// (surface 2 = END_RING f106bmp, pos -1.00, scale 1.50, White)
/// (surface 0 = MID_RING f104bmp, pos -0.75, scale 1.00, Blue)
/// (surface 0 = MID_RING f104bmp, pos -0.50, scale 0.50, Blue)
/// (surface 0 = MID_RING f104bmp, pos -0.25, scale 0.25, Blue)
/// (surface 0 = MID_RING f104bmp, pos  0.25, scale 0.25, Blue)
/// (surface 0 = MID_RING f104bmp, pos  0.50, scale 0.50, Blue)
/// (surface 0 = MID_RING f104bmp, pos  0.75, scale 1.00, Blue)
/// (surface 1 = STAR_AURA f105bmp, pos  1.00, scale 1.50, White)
/// </code>
/// The <c>STAR_AURA</c> element at pos +1.00 sits exactly at the projected
/// light direction — that is the visible <b>sun aura</b> (each sector's light
/// source), and it is the only element native rotates (by
/// <c>(screenX+screenY)/200</c> radians, <c>efimage.cpp:740-743</c>). The
/// whole flare is drawn additively with flat shading
/// (<c>efimage.cpp:724-726</c>).
/// </summary>
public static class LensFlareElements
{
    /// <summary>The three surfaces in native's <c>m_psurfaces</c> order:
    /// 0 = mid ring (<c>f104bmp</c>), 1 = star aura (<c>f105bmp</c>),
    /// 2 = end ring (<c>f106bmp</c>). Indexed by <see cref="Element.Index"/>.</summary>
    public static readonly string[] SurfaceArt =
    [
        "f104bmp",  // AWF_EFFECT_LENS_FLARE_MID_RING
        "f105bmp",  // AWF_EFFECT_LENS_FLARE_STAR_AURA
        "f106bmp",  // AWF_EFFECT_LENS_FLARE_END_RING
    ];

    /// <summary>One native flare element.</summary>
    public readonly record struct Element(int Index, float Pos, float Scale, bool White);

    /// <summary>The exact native <c>g_lensFlareData</c> table
    /// (<c>efimage.cpp:601-613</c>).</summary>
    public static readonly Element[] Table =
    [
        new(Index: 2, Pos: -1.00f, Scale: 1.50f, White: true),
        new(Index: 0, Pos: -0.75f, Scale: 1.00f, White: false),
        new(Index: 0, Pos: -0.50f, Scale: 0.50f, White: false),
        new(Index: 0, Pos: -0.25f, Scale: 0.25f, White: false),
        new(Index: 0, Pos:  0.25f, Scale: 0.25f, White: false),
        new(Index: 0, Pos:  0.50f, Scale: 0.50f, White: false),
        new(Index: 0, Pos:  0.75f, Scale: 1.00f, White: false),
        new(Index: 1, Pos:  1.00f, Scale: 1.50f, White: true),
    ];
}
