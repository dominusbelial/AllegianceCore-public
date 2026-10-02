using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using NumericsVec2 = System.Numerics.Vector2;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P372 — draws a small per-side "team tag" chevron just above each visible
/// ship's projected screen position, in that ship's REAL side colour. This is
/// the in-world, hull-attached side indicator for the main 3D view (the
/// companion to the side-coloured radar bracket in
/// <see cref="RadarBracketRenderer"/>): it lets a player tell a friendly ship
/// from an enemy one at a glance, glued to the actual hull, WITHOUT recolouring
/// the per-civ hull art (which is real and correct — P287/P351-356).
///
/// Native has NO per-side hull tint (a native ship's hull is pure civ art —
/// verified this task: no <c>GetSide()-&gt;GetColor()</c> is ever consumed in
/// the ship model render); native's ONLY per-side 3D-view indicator is its
/// per-side-coloured bracket/blip overlay (radarimage.cpp:682-685), of which
/// <see cref="RadarBracketRenderer"/> is the port. Because a full hull recolor
/// would clash with the real per-civ art, this additive chevron tag (rendered
/// over the 3D scene, always camera-facing) is the port's 3D-view side marker.
///
/// Like <see cref="RadarBracketRenderer"/>, it begins/ends its own
/// <see cref="SpriteBatch"/> session so it can be invoked over the 3D scene.
/// Only on-screen ships draw a tag (the edge-clamped off-screen case is already
/// covered by the side-coloured radar bracket); the tag sits a fixed screen
/// offset above the ship's projected centre so it reads as a floating hull tag
/// rather than overlapping the centred radar square.
/// </summary>
public sealed class ShipSideTagRenderer
{
    /// <summary>One placed tag: resolved viewport pixel (on-screen ship only)
    /// and the ship's real side colour.</summary>
    public readonly record struct ShipTag(NumericsVec2 Point, Color Colour);

    private readonly SpriteBatch _sb;
    private readonly Texture2D _white;

    /// <summary>Vertical screen-pixel offset of the tag above the ship's
    /// projected centre — high enough to clear the hull silhouette and the
    /// centred 8&times;8 radar square, small enough to stay clearly attached.</summary>
    private const float TagOffset = 24f;

    /// <summary>Horizontal half-width (screen px) of the chevron arms.</summary>
    private const float TagHalfWidth = 7f;

    /// <summary>Stroke thickness (screen px) of each chevron arm.</summary>
    private const float TagThickness = 2.5f;

    public ShipSideTagRenderer(SpriteBatch spriteBatch, Texture2D whitePixel)
    {
        _sb = spriteBatch;
        _white = whitePixel;
    }

    /// <summary>Draw every ship's side-coloured team tag for this frame.</summary>
    public void Draw(IReadOnlyList<ShipTag> tags)
    {
        if (tags.Count == 0)
            return;
        _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
        try
        {
            foreach (var t in tags)
                DrawTag(t);
        }
        finally
        {
            _sb.End();
        }
    }

    private void DrawTag(ShipTag t)
    {
        var p = new Vector2(t.Point.X, t.Point.Y - TagOffset);
        // Upward-pointing chevron "∧": two short strokes from the apex out.
        var apex = p;
        var leftArm = p + new Vector2(-TagHalfWidth, TagHalfWidth);
        var rightArm = p + new Vector2(TagHalfWidth, TagHalfWidth);
        DrawLine(apex, leftArm, TagThickness, t.Colour);
        DrawLine(apex, rightArm, TagThickness, t.Colour);
    }

    private void DrawLine(Vector2 a, Vector2 b, float thickness, Color colour)
    {
        Vector2 d = b - a;
        float len = d.Length();
        if (len < 0.001f)
            return;
        float angle = MathF.Atan2(d.Y, d.X);
        _sb.Draw(_white, a + d * 0.5f, null, colour, angle,
            new Vector2(0.5f, 0.5f), new Vector2(len, thickness), SpriteEffects.None, 0f);
    }
}
