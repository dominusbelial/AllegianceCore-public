using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P526 — pure tests for the docked hull-preview motion: a single spin angle
/// advancing at native's loadout 1 rad/s about Y plus a constant π/8 X tilt
/// (WinTrek/load.cpp:877-887), and the preview's hull (which selects the model)
/// tracking the shop's effective hull. Replaces P519's cmExternalOrbit 3-axis
/// tumble tests.
/// </summary>
public class DockedHullOrbitTests
{
    /// <summary>⭐ P526 — the spin advances at native's exact loadout rate
    /// (load.cpp:883-885: AnimateRotateTransform angle = time × 1.0, i.e.
    /// 1 rad/s about Y), so after one second the spin angle grew by 1.0 rad.</summary>
    [Fact]
    public void Spin_advances_at_native_loadout_rate()
    {
        Assert.Equal(DockedHullOrbit.SpinRate, DockedHullOrbit.Advance(0f, 1.0), precision: 5);
    }

    /// <summary>⭐ P526 — over dt=0.5s the spin advances by half the per-second
    /// rate (frame-rate independence of the rotation speed).</summary>
    [Fact]
    public void Spin_advances_frame_rate_independently()
    {
        Assert.Equal(0.5f * DockedHullOrbit.SpinRate, DockedHullOrbit.Advance(0f, 0.5), precision: 5);
    }

    /// <summary>⭐ P526 — frame-rate independence also holds under repeated small
    /// steps: sixteen 0.0625 s frames compound to exactly one second of spin
    /// (no drift from chunky/fine stepping).</summary>
    [Fact]
    public void Spin_compounds_to_the_same_total_regardless_of_step_size()
    {
        float oneStep = DockedHullOrbit.Advance(0f, 1.0);
        float manySteps = 0f;
        for (int i = 0; i < 16; i++)
            manySteps = DockedHullOrbit.Advance(manySteps, 0.0625);
        Assert.Equal(oneStep, manySteps, precision: 4);
    }

    /// <summary>⭐ P526 — the X tilt is a constant π/8 (≈22.5°, load.cpp:886-887):
    /// it never changes as the spin advances, and equals native's fixed value.</summary>
    [Fact]
    public void Tilt_is_constant_over_time()
    {
        double expectedTilt = Math.PI / 8.0;
        double tilt = DockedHullOrbit.TiltRadians;
        Assert.True(Math.Abs(tilt - expectedTilt) < 1e-6,
            $"tilt {tilt:R} != π/8 {expectedTilt:R}");
        // Advancing the spin leaves the tilt untouched — the tilt is a constant,
        // not an accumulator (sample across a full revolution).
        float before = DockedHullOrbit.TiltRadians;
        DockedHullOrbit.Advance(0f, 2.0 * Math.PI);
        float after = DockedHullOrbit.TiltRadians;
        Assert.Equal(before, after);
    }

    /// <summary>⭐ P526 — the world rotation is native's
    /// TransformGeo(TransformGeo(geo, spinY), tiltX) composition: spin about Y
    /// FIRST, then the constant π/8 X tilt, i.e. XNA row-vector RotY(spin)·RotX(tilt).
    /// Order matters — swapping to RotX(tilt)·RotY(spin) yields a different matrix.</summary>
    [Fact]
    public void Composition_is_spin_about_y_then_fixed_tilt_about_x()
    {
        float spin = 1.234f;
        var expected = Matrix.CreateRotationY(spin) * Matrix.CreateRotationX(DockedHullOrbit.TiltRadians);
        var actual = DockedHullOrbit.World(spin);
        AssertMatrixNear(actual, expected, 1e-4f);

        // The tilt must be about X and applied after the spin: the opposite order
        // (tilt-first) is measurably different, proving the composition isn't
        // commutative and we picked the native one.
        var swapped = Matrix.CreateRotationX(DockedHullOrbit.TiltRadians) * Matrix.CreateRotationY(spin);
        Assert.NotEqual(0f, MaxAbsDiff(actual, swapped));
    }

    /// <summary>⭐ P526 — pitch and roll never accumulate: the local UP pole (the
    /// ship's spin axis) maps to a SINGLE fixed direction (0, cos t, sin t) for
    /// every spin angle — it never rocks or tumbles. The ship level-spins about
    /// that fixed axis (tilted π/8 toward the camera) at a constant tilt; the
    /// +Z component of the pole confirms the camera sees the ship's TOP, the
    /// natural hangar view (native load.cpp:886-887 uses +π/8).</summary>
    [Fact]
    public void No_pitch_or_roll_accumulate_constant_tilt_level_spin()
    {
        float[] spins = { 0f, (float)(Math.PI / 2), (float)Math.PI, (float)(3 * Math.PI / 2), (float)(2 * Math.PI) };
        var expectedTop = Vector3.TransformNormal(Vector3.Up,
            DockedHullOrbit.World(0f));           // the fixed spin-axis direction
        Assert.True(expectedTop.Z > 0f, "the tilt must show the ship's TOP (toward the camera)");

        foreach (float s in spins)
        {
            var top = Vector3.TransformNormal(Vector3.Up, DockedHullOrbit.World(s));

            // The pole is PINNED: identical for every spin -> fixed axis, so no
            // pitch or roll ever accumulates and the tilt stays constant.
            Assert.Equal(expectedTop.X, top.X, precision: 5);
            Assert.Equal(expectedTop.Y, top.Y, precision: 5);
            Assert.Equal(expectedTop.Z, top.Z, precision: 5);
            Assert.Equal(1f, top.Length(), precision: 4);   // rigid, no scaling
        }
    }

    /// <summary>⭐ P519 (kept) — the hull the preview renders is the shop's
    /// EFFECTIVE hull, so cycling/selecting a hull immediately swaps the preview's
    /// model (no hitch, models are already cached).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Hull_selection_drives_the_preview_hull_id()
    {
        Require.Artwork();
        var cat = ShopCatalog.TryLoad(
            ExternalPaths.Resolve(ExternalResourceKind.Artwork))
            ?? throw new InvalidOperationException("core not loadable");
        short current = cat.Hulls.OrderBy(h => h.HullId).First().HullId;
        short other = cat.Hulls.OrderBy(h => h.HullId).Last(h => h.HullId != current).HullId;

        var m = new ShopModel(cat, current, Array.Empty<ExpandedPartData>());
        Assert.Equal(current, m.EffectiveHullId);          // preview = the current hull

        m.CycleHullTo(other);
        Assert.Equal(other, m.EffectiveHullId);            // preview swaps to the chosen hull
    }

    private static void AssertMatrixNear(Matrix a, Matrix b, float tol)
    {
        Assert.True(MaxAbsDiff(a, b) <= tol,
            $"matrices differ by {MaxAbsDiff(a, b):G4} (> {tol:G4})");
    }

    private static float MaxAbsDiff(Matrix a, Matrix b)
    {
        float mx = 0f;
        foreach (var (x, y) in Enumerable.Zip(RowMajor(a), RowMajor(b)))
            mx = Math.Max(mx, Math.Abs(x - y));
        return mx;
    }

    private static float[] RowMajor(Matrix m)
    {
        return new[]
        {
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44,
        };
    }
}
