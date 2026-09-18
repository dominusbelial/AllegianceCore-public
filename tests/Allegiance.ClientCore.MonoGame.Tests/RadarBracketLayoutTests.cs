using Allegiance.ClientCore.MonoGame.Rendering;
using NumericsVec2 = System.Numerics.Vector2;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="RadarBracketLayout"/> — the P250 local-radar
/// on-screen/edge-clamp/fade contract. No graphics device (same discipline as
/// <see cref="StarmapLayoutTests"/>). They pin the two behaviours that make the
/// feature work: (1) an in-front object that projects inside the viewport is
/// drawn at its EXACT projected pixel, and (2) any object that projects outside
/// (or is behind the camera) has its marker clamped to the viewport edge in its
/// REAL direction — including the behind-camera case where the lateral sign is
/// what decides left-vs-right / top-vs-bottom (the eye-space direction native
/// clamps in WinTrek.cpp:7893-7923).
/// </summary>
public class RadarBracketLayoutTests
{
    private const float W = 900f, H = 640f, Inset = 14f;

    private static RadarBracketLayout.RadarSite Resolve(
        bool inFront, float ndcX, float ndcY, float dirX, float dirY) =>
        RadarBracketLayout.Resolve(inFront, ndcX, ndcY, dirX, dirY, W, H, Inset);

    [Fact]
    public void InFront_InsideViewport_ReturnsExactProjectedPixel()
    {
        // ndc (0.25, -0.25) → pixel (0.625*W, 0.375*H) in viewport px.
        var site = Resolve(inFront: true, ndcX: 0.25f, ndcY: -0.25f, dirX: 0.25f, dirY: -0.25f);
        Assert.Equal(RadarBracketLayout.RadarPlacement.OnScreen, site.Placement);
        AssertEqual((0.25f * 0.5f + 0.5f) * W, site.Point.X);
        AssertEqual((0.5f - -0.25f * 0.5f) * H, site.Point.Y);
    }

    [Fact]
    public void CentreOfScreen_IsOnScreen_AtExactMiddle()
    {
        var site = Resolve(inFront: true, ndcX: 0f, ndcY: 0f, dirX: 0f, dirY: 0f);
        Assert.Equal(RadarBracketLayout.RadarPlacement.OnScreen, site.Placement);
        AssertEqual(W / 2f, site.Point.X);
        AssertEqual(H / 2f, site.Point.Y);
    }

    [Fact]
    public void OffScreenRight_ClampsToRightEdge_InDirection()
    {
        // In front but way off the right edge: dominant direction is +x → right
        // edge at x = W - inset; y follows the slope.
        var site = Resolve(inFront: true, ndcX: 3.0f, ndcY: -0.6f, dirX: 3.0f, dirY: -0.6f);
        Assert.Equal(RadarBracketLayout.RadarPlacement.EdgeClamped, site.Placement);
        AssertEqual(W - Inset, site.Point.X);          // pinned to right edge
        Assert.InRange(site.Point.Y, Inset, H - Inset); // somewhere mid-right
    }

    [Fact]
    public void BehindLeft_ClampsToLeftEdge_NotRight()
    {
        // Object behind the camera and to the LEFT: dirX is NEGATIVE (left), so
        // the marker MUST pin the LEFT edge — the exact guarantee that separates
        // an eye-space direction clamp from a naive projection flip.
        var site = Resolve(inFront: false, ndcX: 0f, ndcY: 0f, dirX: -3.0f, dirY: -0.5f);
        Assert.Equal(RadarBracketLayout.RadarPlacement.EdgeClamped, site.Placement);
        AssertEqual(Inset, site.Point.X);              // left edge
        Assert.InRange(site.Point.Y, Inset, H - Inset);
    }

    [Fact]
    public void BehindAbove_ClampsToTopEdge()
    {
        // Behind and ABOVE the eye: dirY positive (+up) → top of screen.
        var site = Resolve(inFront: false, ndcX: 0f, ndcY: 0f, dirX: 0.3f, dirY: 2.0f);
        Assert.Equal(RadarBracketLayout.RadarPlacement.EdgeClamped, site.Placement);
        AssertEqual(Inset, site.Point.Y);              // top edge
        Assert.InRange(site.Point.X, Inset, W - Inset);
    }

    [Fact]
    public void DominantAxis_SelectsTheEdge()
    {
        // More right than up → right edge (x clamped, y interior).
        var right = Resolve(inFront: false, ndcX: 0f, ndcY: 0f, dirX: 2.0f, dirY: 0.5f);
        AssertEqual(W - Inset, right.Point.X);
        // More up than right → top edge (y clamped, x interior).
        var top = Resolve(inFront: false, ndcX: 0f, ndcY: 0f, dirX: 0.5f, dirY: 2.0f);
        AssertEqual(Inset, top.Point.Y);
    }

    [Fact]
    public void DegenerateCentreDirection_PinsSanely_WithinInsetRect()
    {
        // dirX=dirY=0 (object dead-opposite the view axis) → the clamp pins a
        // definite edge rather than emitting NaN; both coords stay in-bounds.
        var site = Resolve(inFront: false, ndcX: 0f, ndcY: 0f, dirX: 0f, dirY: 0f);
        Assert.Equal(RadarBracketLayout.RadarPlacement.EdgeClamped, site.Placement);
        Assert.InRange(site.Point.X, Inset, W - Inset);
        Assert.InRange(site.Point.Y, Inset, H - Inset);
    }

    [Fact]
    public void Opacity_FullInsideClose_ZeroAtFar_LinearBetween()
    {
        const float close = RadarBracketLayout.CloseRange;   // 250
        const float far = RadarBracketLayout.FarRange;       // 2000

        Assert.Equal(1f, RadarBracketLayout.Opacity(0f, close, far));
        Assert.Equal(1f, RadarBracketLayout.Opacity(close, close, far));
        Assert.Equal(0f, RadarBracketLayout.Opacity(far, close, far));
        Assert.Equal(0f, RadarBracketLayout.Opacity(far + 999f, close, far));

        float mid = RadarBracketLayout.Opacity((close + far) / 2f, close, far);
        Assert.InRange(mid, 0.45f, 0.55f);
    }

    [Fact]
    public void Opacity_MonotonicallyDecreasesWithDistance()
    {
        const float close = RadarBracketLayout.CloseRange;
        const float far = RadarBracketLayout.FarRange;
        float prev = 1f;
        for (float d = close; d <= far; d += (far - close) / 100f)
        {
            float o = RadarBracketLayout.Opacity(d, close, far);
            Assert.True(o <= prev + 1e-4f, $"opacity rose at d={d}");
            prev = o;
        }
    }

    private static void AssertEqual(float expected, float actual, float tol = 1e-3f)
        => Assert.True(MathF.Abs(expected - actual) <= tol,
            $"expected {expected}, got {actual}");
}
