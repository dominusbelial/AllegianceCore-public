using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1042 — the pure geometry rules of the Jet screen chrome, shared by the renderer and the
/// tests so the 800×480 fit and the "same rectangles in green and amber" claims are
/// measured rather than asserted.
/// </summary>
public static class JetScreenChrome
{
    /// <summary>A selected/active plate's alpha.</summary>
    public const float PlateAlpha = 0.86f;
    /// <summary>An idle plate's alpha (still readable over bright art).</summary>
    public const float DimPlateAlpha = 0.62f;
    /// <summary>The corner-bracket arm length at 1.0 scale (further clamped by the box).</summary>
    public const float BracketArm = 9f;
    /// <summary>The smallest text scale the chrome accepts before it would have to clip.</summary>
    public const float MinimumTextScale = 0.1f;

    /// <summary>The band a frame/plate consumes on each side of a control box: the ink
    /// stroke plus the keyline allowance.</summary>
    public static float Band(JetVectorPainter painter) => painter.Stroke + painter.KeylineWidth;

    /// <summary>The box inside the chrome's frame. Never negative.</summary>
    public static RectangleF Inner(RectangleF box, float band)
        => new(box.X + band, box.Y + band, MathF.Max(0f, box.Width - 2f * band),
            MathF.Max(0f, box.Height - 2f * band));

    /// <summary>
    /// The text scale that keeps a run of <paramref name="ink"/> size inside
    /// <paramref name="box"/> after the frame band, capped at <paramref name="cap"/>
    /// (the layout's own row scale, so Jet never enlarges native text at 800×480).
    /// </summary>
    public static float FitScale(Vector2 ink, RectangleF box, float cap, float band)
    {
        var inner = Inner(box, band);
        float scale = MathF.Max(0f, cap);
        if (ink.X > 0f) scale = MathF.Min(scale, inner.Width / ink.X);
        if (ink.Y > 0f) scale = MathF.Min(scale, inner.Height / ink.Y);
        return MathF.Max(MinimumTextScale, scale);
    }

    /// <summary>Whether the fitted text really stays inside the plate (the audit's
    /// text-outside-plate rule expressed as a pure predicate).</summary>
    public static bool Fits(Vector2 ink, RectangleF box, float scale, float band)
    {
        var inner = Inner(box, band);
        return ink.X * scale <= inner.Width + 0.001f && ink.Y * scale <= inner.Height + 0.001f;
    }
}
