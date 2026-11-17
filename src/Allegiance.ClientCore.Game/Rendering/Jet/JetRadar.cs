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

    /// <summary>Equal range bands drawn in the scope (native has no range scale; these are
    /// the Jet presentation's own bands over the frame's own maximum range).</summary>
    public const int RingCount = 4;

    /// <summary>Icons sit inside the rim: this fraction of the scope half-extent.</summary>
    public const float IconRadiusFraction = 0.88f;

    /// <summary>The scope of this frame: square in the bottom-right quadrant, above the
    /// minimap panel and left of the throttle tape's own band (<paramref name="rightLimit"/>
    /// is the left edge of that band's readout plate / IDLE-REV words, measured by the
    /// renderer; 0 when the tape is hidden).</summary>
    public static RectangleF Scope(float viewportWidth, float viewportHeight, float scale,
        float rightLimit = 0f)
    {
        float s = scale > 0f ? scale : 1f;
        float side = Math.Clamp(viewportHeight * SideFraction, MinSide * s, MaxSide * s);
        float right = viewportWidth - RightInset * s;
        if (rightLimit > 0f) right = MathF.Min(right, rightLimit - BottomGap * s);
        var (_, minimapY, _, _) = MinimapGeometry.CornerPanel(viewportWidth, viewportHeight);
        float bottom = minimapY - BottomGap * s;
        float left = MathF.Max(0f, right - side);
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

    /// <summary>
    /// The frame's range scale: the largest contact range present, rounded UP to a 1/2/5×10^n
    /// value, and never below <see cref="MinimumScopeRange"/>. No sensor range is invented —
    /// the bands simply cover the contacts the native rules already allowed.
    /// </summary>
    public const float MinimumScopeRange = 1000f;

    public static float ScopeRange(IReadOnlyList<HudContact> contacts)
    {
        float max = 0f;
        for (int i = 0; i < contacts.Count; i++)
            if (contacts[i].Range is { } range && range > max) max = range;
        return RoundRange(MathF.Max(max, MinimumScopeRange));
    }

    /// <summary>Round a metre value up to 1/2/5 × 10^n (the band labels stay readable).</summary>
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
