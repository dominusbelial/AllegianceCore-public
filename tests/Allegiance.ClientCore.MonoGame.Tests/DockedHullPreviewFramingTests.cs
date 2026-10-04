using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P525 — pure tests for the docked hull preview framing helper
/// (<see cref="DockedHullPreviewFraming"/>). Every value is exercised with the
/// real measured data (docs/P305_roster/roster_variety.txt + the PCore001 hull
/// lengths), so the assertions hold for the actual compiled Meshy models the
/// preview draws — a fighter and a carrier bookend the flyable size range.
/// </summary>
public class DockedHullPreviewFramingTests
{
    // Real measured render radii (roster_variety.txt) and hull lengths
    // (PCore001, same values ShipRenderScaleTests uses).
    private const float FighterLength = 17.1f;   // bios-fighter (wc_gcfigx)
    private const float FighterRadius = 157.5f;
    private const float CarrierLength = 300.0f;  // bios-carrier (cap502)
    private const float CarrierRadius = 102.3f;

    [Fact]
    public void Normalise_scale_maps_every_hull_to_radius_one()
    {
        // The core invariant: after NormaliseScale the drawn merged-sphere
        // radius is exactly 1 for both the smallest and largest hull classes,
        // so the fixed eye at 2.4 frames them all identically. A carrier and a
        // fighter that previously differed ~10x in rendered size now coincide.
        Assert.Equal(1f, DockedHullPreviewFraming.NormaliseScale(FighterRadius) * FighterRadius, precision: 4);
        Assert.Equal(1f, DockedHullPreviewFraming.NormaliseScale(CarrierRadius) * CarrierRadius, precision: 4);
    }

    [Fact]
    public void Eye_sits_outside_the_unit_sphere()
    {
        // Native's SetGeo puts the eye at 2.4 x radius (load.cpp:927-933); with
        // the hull normalised to radius 1 the eye is 2.4 from the centre —
        // strictly outside the hull's bounding sphere, so the camera is never
        // embedded in the mesh.
        float eye = DockedHullPreviewFraming.EyeDistance(1f);
        Assert.True(DockedHullPreviewFraming.EyeOutsideSphere(eye, 1f),
            $"eye {eye} should sit outside the unit sphere");
        Assert.Equal(DockedHullPreviewFraming.EyeDistanceFactor, eye, precision: 5);
    }

    [Fact]
    public void Whole_hull_sphere_fits_inside_the_fifty_degree_frustum()
    {
        // From the eye at 2.4 the unit hull sphere subtends asin(1/2.4)=24.62°,
        // less than the 25° half-FOV (load.cpp:864 SetFOV(50)) — so the whole
        // sphere, and hence the whole ship, is inside the frustum in every
        // orientation. If the eye ever dropped inside the sphere this returns
        // 90° and reds.
        float ang = DockedHullPreviewFraming.AngularRadiusDegrees(
            DockedHullPreviewFraming.EyeDistance(1f), 1f);
        Assert.True(ang < DockedHullPreviewFraming.HalfFovDegrees,
            $"angular radius {ang:F2}° must be < half-FOV {DockedHullPreviewFraming.HalfFovDegrees}°");
    }

    [Fact]
    public void Old_one_over_length_formula_embeds_the_camera_in_a_small_hull()
    {
        // ⭐ The P525 bug: the old formula scaled by 1/HullLength. For the real
        // fighter (len 17.1, render radius 157.5) the drawn radius is
        // 157.5/17.1 ≈ 9.2 units while the eye sits at 2.4 — the camera is
        // INSIDE the hull ("looking from the insides"). This must FAIL the
        // outside-the-eye check, proving the fix is necessary.
        float oldScale = 1f / FighterLength;
        float oldDrawnRadius = oldScale * FighterRadius;
        bool oldOutside = DockedHullPreviewFraming.EyeOutsideSphere(
            DockedHullPreviewFraming.EyeDistance(1f), oldDrawnRadius);
        Assert.False(oldOutside,
            $"old 1/length framed a {oldDrawnRadius:F2}-unit fighter from an eye at 2.4 — " +
            "the camera should be inside the hull");
    }

    [Fact]
    public void New_radius_formula_keeps_the_eye_outside_the_same_hull()
    {
        // The same real fighter under the new formula draws at radius 1 and the
        // eye at 2.4 is outside it — the regression that the old formula's
        // failure (above) motivates.
        float scale = DockedHullPreviewFraming.NormaliseScale(FighterRadius);
        float drawnRadius = scale * FighterRadius;
        Assert.Equal(1f, drawnRadius, precision: 4);
        Assert.True(DockedHullPreviewFraming.EyeOutsideSphere(
            DockedHullPreviewFraming.EyeDistance(drawnRadius), drawnRadius));
    }

    [Fact]
    public void Nonpositive_radius_guards_to_no_scale()
    {
        // A missing/measure-less model must not corrupt the transform.
        Assert.Equal(1f, DockedHullPreviewFraming.NormaliseScale(0f));
        Assert.Equal(1f, DockedHullPreviewFraming.NormaliseScale(-5f));
    }

    [Fact]
    public void Fov_and_eye_constants_match_native()
    {
        Assert.Equal(50f, DockedHullPreviewFraming.FieldOfViewDegrees);
        Assert.Equal(2.4f, DockedHullPreviewFraming.EyeDistanceFactor);
    }
}
