using System;
using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering.Jet;

/// <summary>
/// P1039 — the pure geometry of the Jet radar (P972 §4.4 RWR, §5.4 rows 1–27, phase 3C).
/// The scope is an own-relative polar instrument: a contact is placed by its
/// <see cref="HudContact.RelativeBearingRadians"/> (0 = dead ahead, positive = to the ship's
/// left) and its range, on four equal range bands. A null bearing falls back to the ordinary
/// PROJECTED scope — the contact's existing native projection mapped into the same rectangle —
/// so a direction is never invented.
/// <para><b>Square vs round.</b> Both variants occupy the SAME measured rectangle and share
/// every contact position; the shared round-radar option
/// (<see cref="HudSettings.RoundRadar"/>, native <c>m_bRoundRadar</c>,
/// WinTrek.cpp:7726,7849-7906) only changes the border: a square tactical scope when it is
/// off, an inscribed circular RWR when it is on (radius = half the rectangle, the native
/// inscribed-circle rule).</para>
/// <para><b>Same contact set.</b> Nothing here filters: the renderer walks the model's
/// contact list, which the snapshot builder already reduced by the native per-kind LOD rules
/// (<see cref="RadarLod"/>) and the same nearest-enemy/subject/order overlays. The scope
/// grants no 360° knowledge beyond those contacts.</para>
/// </summary>
public static class JetRadar
{
    /// <summary>The scope square's side: this fraction of the viewport height.</summary>
    public const float SideFraction = 0.32f;

    /// <summary>Physical floor/ceiling of the side at a HUD scale of 1 (× scale).</summary>
    public const float MinSide = 110f;
    public const float MaxSide = 360f;

    /// <summary>Gap from the scope's right edge to the throttle tape's label column.</summary>
    public const float RightInset = 80f;

    /// <summary>Gap between the scope's bottom edge and the minimap panel.</summary>
    public const float BottomGap = 6f;

    /// <summary>
    /// P1039b — the compact-window floor, the same 700 px rule the tapes, the ladder and the
    /// stores block use (<see cref="JetStoresPanel.CompactHeight"/>): below it the HUD is the
    /// 800×480 layout.
    /// </summary>
    public const float CompactHeight = 700f;

    /// <summary>
    /// P1039b — the compact scope's side as a fraction of the viewport height (0.30 → 144 px at
    /// 800×480). P972 §4.5 shrinks the RWR at the small window; the whole instrument (frame,
    /// four bands, every contact class) is kept, only the label blocks are decluttered.
    /// </summary>
    public const float CompactSideFraction = 0.30f;

    /// <summary>
    /// P1039b — the compact scope's reserved CORNER ZONE: its left edge never crosses this
    /// fraction of the viewport width (0.66). At 800×480 the flight-path column — the pitch
    /// ladder's right numeral column (≈55 % of the width) and the <c>ACC</c>/<c>CUR</c> order
    /// word (≈64 %) — ends inside this margin, so the scope cannot reach the boresight area.
    /// </summary>
    public const float CompactLeftFraction = 0.66f;

    /// <summary>
    /// P1039b — the compact scope's physical floor (px, ×scale): four bands must still resolve
    /// as four bands. If the corner zone cannot hold it the left margin gives way, never the
    /// instrument's structure.
    /// </summary>
    public const float CompactMinSide = 64f;

    /// <summary>Equal range bands drawn in the scope (native has no range scale; these are
    /// the Jet presentation's own bands over the frame's own maximum range).</summary>
    public const int RingCount = 4;

    /// <summary>Icons sit inside the rim: this fraction of the scope half-extent.</summary>
    public const float IconRadiusFraction = 0.88f;

    /// <summary>
    /// The scope of this frame: square in the bottom-right quadrant, above the
    /// minimap panel and left of the throttle tape's own band (<paramref name="rightLimit"/>
    /// is the left edge of that band's readout plate / IDLE-REV words, measured by the
    /// renderer; 0 when the tape is hidden).
    /// <para><b>P1039b — the compact corner zone.</b> Below <see cref="CompactHeight"/> the
    /// square is also capped by <see cref="CompactSideFraction"/> of the viewport height and
    /// its left edge is held at or right of <see cref="CompactLeftFraction"/> of the width
    /// (relaxing only if that would push it under <see cref="CompactMinSide"/>), and its top
    /// edge stays below <paramref name="topLimit"/> (the measured bottom of the master-caution
    /// panel). The result is the bottom-right corner band: right of the flight-path column,
    /// left of the throttle tape/plate, above the sector mini-map and below the caution panel.
    /// P1039's uncapped 0.32·h square at 800×480 was 153 px and landed next to the centre,
    /// touching the 80 % throttle plate and swallowing the <c>ACC</c> order word.</para>
    /// </summary>
    public static RectangleF Scope(float viewportWidth, float viewportHeight, float scale,
        float rightLimit = 0f, float topLimit = 0f)
    {
        float s = scale > 0f ? scale : 1f;
        float side = Math.Clamp(viewportHeight * SideFraction, MinSide * s, MaxSide * s);
        float right = viewportWidth - RightInset * s;
        if (rightLimit > 0f) right = MathF.Min(right, rightLimit - BottomGap * s);
        var (_, minimapY, _, _) = MinimapGeometry.CornerPanel(viewportWidth, viewportHeight);
        float bottom = minimapY - BottomGap * s;
        float leftLimit = 0f;
        if (viewportHeight < CompactHeight)
        {
            side = MathF.Min(side, viewportHeight * CompactSideFraction);
            leftLimit = viewportWidth * CompactLeftFraction;
            // Never rise into the master-caution panel (it is the higher-priority element).
            if (topLimit > 0f) side = MathF.Min(side, MathF.Max(0f, bottom - (topLimit + BottomGap * s)));
            if (right - leftLimit < side)
            {
                // The corner zone is too narrow for the instrument: keep the four bands and let
                // the left margin give way (the stated floor), never the band structure.
                side = MathF.Max(MathF.Min(side, CompactMinSide * s), MathF.Min(side, right - leftLimit));
            }
        }
        float left = MathF.Max(leftLimit, right - side);
        float top = MathF.Max(0f, bottom - side);
        return new RectangleF(left, top, MathF.Max(0f, right - left), MathF.Max(0f, bottom - top));
    }

    /// <summary>The scope's centre.</summary>
    public static Vector2 Centre(RectangleF scope)
        => new(scope.X + scope.Width * 0.5f, scope.Y + scope.Height * 0.5f);

    /// <summary>The square scope's usable half-extent (the icon field).</summary>
    public static float HalfExtent(RectangleF scope)
        => MathF.Min(scope.Width, scope.Height) * 0.5f * IconRadiusFraction;

    /// <summary>The circular variant's radius: the inscribed circle (native's inscribed
    /// rule), reduced to the icon field.</summary>
    public static float CircleRadius(RectangleF scope)
        => MathF.Min(scope.Width, scope.Height) * 0.5f * IconRadiusFraction;

    /// <summary>The lowest scope scale the bands may use (a nearly empty frame still reads as an
    /// instrument, and <see cref="RangeFraction"/> pins anything beyond the rim).</summary>
    public const float MinimumScopeRange = 1000f;

    /// <summary>
    /// The frame's range scale: four equal bands whose STEP is the farthest contact range ÷ 4
    /// rounded UP to the 1/1.5/2/2.5/3/4/5/6/8 ×10ⁿ series, and never below
    /// <see cref="MinimumScopeRange"/>. No sensor range is invented — the bands simply cover the
    /// contacts the native rules already allowed.
    /// <para><b>P1039b — why the step, not the whole scale.</b> P1039 rounded the MAX range up to
    /// the next 1/2/5 ×10ⁿ value, so a frame whose farthest contact was at 2,740 m got a 5,000 m
    /// outer ring: every contact sat inside the inner 55 % of the instrument and 45 % of the
    /// radius was empty sky. Native has no range scale at all (blips are placed by the native
    /// projection, <c>radarimage.cpp:286-427</c>, not by a metres fraction), and neither Classic
    /// nor Modern draws rings, so the number series is presentation-only and can be chosen for
    /// legibility: rounding the BAND keeps the outer ring within 1.6× of the farthest contact and
    /// the captions stay short round numbers (800 / 1600 / 2400 / 3200 for that frame). The
    /// crowding P1039 showed was therefore a Jet mapping defect, not a native range.</para>
    /// </summary>
    public static float ScopeRange(IReadOnlyList<HudContact> contacts)
    {
        float max = 0f;
        for (int i = 0; i < contacts.Count; i++)
            if (contacts[i].Range is { } range && range > max) max = range;
        return ScaleFor(MathF.Max(max, MinimumScopeRange));
    }

    /// <summary>
    /// P1039b — the outer ring for a farthest contact at <paramref name="metres"/>: four times a
    /// band step from the 1/1.5/2/2.5/3/4/5/6/8 ×10ⁿ series, so the outer ring never exceeds
    /// 1.6× the farthest contact and every caption is a round multiple of the step.
    /// </summary>
    public static float ScaleFor(float metres)
    {
        float range = metres > 0f ? metres : MinimumScopeRange;
        float step = range / RingCount;
        float magnitude = MathF.Pow(10f, MathF.Floor(MathF.Log10(step)));
        float scaled = step / magnitude;
        float nice = scaled <= 1f ? 1f
            : scaled <= 1.5f ? 1.5f
            : scaled <= 2f ? 2f
            : scaled <= 2.5f ? 2.5f
            : scaled <= 3f ? 3f
            : scaled <= 4f ? 4f
            : scaled <= 5f ? 5f
            : scaled <= 6f ? 6f
            : scaled <= 8f ? 8f
            : 10f;
        return nice * magnitude * RingCount;
    }

    /// <summary>Round a metre value up to 1/2/5 × 10ⁿ (kept for the single-value captions and
    /// the tests that pin the series; the scope scale itself uses <see cref="ScaleFor"/>).</summary>
    public static float RoundRange(float metres)
    {
        if (metres <= 0f) return MinimumScopeRange;
        float step = MathF.Pow(10f, MathF.Floor(MathF.Log10(metres)));
        float scaled = metres / step;
        float rounded = scaled <= 1f ? 1f : scaled <= 2f ? 2f : scaled <= 5f ? 5f : 10f;
        return rounded * step;
    }

    /// <summary>A range fraction inside the scope (clamped at 1: anything at or beyond the
    /// outer band is pinned to the rim, exactly like an off-screen native blip).</summary>
    public static float RangeFraction(int? range, float scopeRange)
        => scopeRange <= 0f ? 0f : Math.Clamp((range ?? 0) / scopeRange, 0f, 1f);

    /// <summary>
    /// The scope point of one contact. A bearing places it on the polar field (0 = up, positive
    /// = left, matching the heading tape's sense); a null bearing falls back to the contact's
    /// ordinary projected viewport point, mapped linearly into the same rectangle — the native
    /// projected scope, never a fabricated direction. The own ship sits at the centre (its
    /// range is zero).
    /// </summary>
    public static Vector2 ContactPoint(RectangleF scope, in HudContact contact,
        float viewportWidth, float viewportHeight, float scopeRange)
    {
        Vector2 centre = Centre(scope);
        if (contact.IsSelf) return centre;
        float half = MathF.Min(scope.Width, scope.Height) * 0.5f;
        if (contact.RelativeBearingRadians is { } bearing)
        {
            float fraction = RangeFraction(contact.Range, scopeRange) * IconRadiusFraction;
            // Screen: bearing 0 = up (−y), positive (left) = −x.
            float sin = MathF.Sin(bearing), cos = MathF.Cos(bearing);
            return new(centre.X - sin * half * fraction, centre.Y - cos * half * fraction);
        }
        // Fallback: the ordinary projected scope. The native projection is in viewport px;
        // map the viewport rectangle onto the scope rectangle so the contact keeps its
        // direction and relative distance without inventing one.
        if (viewportWidth <= 0f || viewportHeight <= 0f) return centre;
        float nx = (contact.Position.X / viewportWidth - 0.5f) * 2f;
        float ny = (contact.Position.Y / viewportHeight - 0.5f) * 2f;
        return new(centre.X + nx * half * IconRadiusFraction,
            centre.Y + ny * half * IconRadiusFraction);
    }

    /// <summary>The ring radius of band <paramref name="ring"/> (1…<see cref="RingCount"/>).</summary>
    public static float RingRadius(RectangleF scope, int ring, bool round)
    {
        float fraction = Math.Clamp(ring, 0, RingCount) / (float)RingCount;
        return (round ? CircleRadius(scope) : HalfExtent(scope)) * fraction;
    }

    /// <summary>The range a band stands for, in metres.</summary>
    public static int RingRange(int ring, float scopeRange)
        => (int)MathF.Round(scopeRange * Math.Clamp(ring, 0, RingCount) / RingCount);
}
