using System;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P589b — the moon lit-circular-disc generator (<see cref="MoonRenderer.GenerateLitDisc"/>).
///
/// Root cause of "moons render as SQUARE rock-texture tiles" (Domingo live,
/// 2026-10-08): the native <c>moonbmp</c>/<c>moon7bmp</c>/<c>moonnebbmp</c>
/// art is a SURFACE texture for the 3D <c>moon.mdl</c> sphere, so drawing it as
/// a flat billboard left the square corners opaque. The fix clips the texture
/// to a circular disc (alpha → 0 at the silhouette rim — no square edge at any
/// size) and bakes a Lambert terminator from the sector light direction (day
/// side lit, night side at an ambient floor — a crescent phase like a small
/// planet).
///
/// Headless (no GraphicsDevice): drives the pure buffer generator with a
/// synthetic square source texture, so these are pure math invariants.
/// </summary>
public class P589bMoonDiscTests
{
    private const int N = 64;

    /// <summary>A plain full-alpha white square "surface texture" (the worst
    /// case for a billboard — a solid square), premultiplied white.</summary>
    private static Color[] SolidWhite()
    {
        var cols = new Color[N * N];
        Array.Fill(cols, new Color(255, 255, 255, 255));
        return cols;
    }

    private static int Idx(int x, int y) => y * N + x;

    [Fact]
    public void Corners_are_transparent_not_square()
    {
        var rgba = MoonRenderer.GenerateLitDisc(
            SolidWhite(), N, N,
            right: Vector3.UnitX, up: Vector3.UnitY, facing: Vector3.UnitZ,
            lightWorld: Vector3.UnitZ);   // light straight at the disc
        // The four corners of the bounding square must be fully transparent
        // (r2 >= 1 -> outside the inscribed disc) — this is what kills the
        // square-tile look. A raw flat billboard would have had alpha 255 here.
        Assert.Equal(0, rgba[4 * Idx(0, 0) + 3]);
        Assert.Equal(0, rgba[4 * Idx(N - 1, 0) + 3]);
        Assert.Equal(0, rgba[4 * Idx(0, N - 1) + 3]);
        Assert.Equal(0, rgba[4 * Idx(N - 1, N - 1) + 3]);
    }

    [Fact]
    public void Centre_facing_the_light_is_full_and_bright()
    {
        var rgba = MoonRenderer.GenerateLitDisc(
            SolidWhite(), N, N,
            right: Vector3.UnitX, up: Vector3.UnitY, facing: Vector3.UnitZ,
            lightWorld: Vector3.UnitZ);
        // Centre texel: disc interior (geo=1), normal == facing == light ->
        // Lambert 1 -> shade ~1 -> premultiplied white at full alpha. (The
        // colour lands at 254-255 from normalisation rounding.)
        int i = 4 * Idx(N / 2, N / 2);
        Assert.Equal(255, rgba[i + 3]);
        Assert.InRange(rgba[i + 0], 250, 256);
        Assert.InRange(rgba[i + 1], 250, 256);
        Assert.InRange(rgba[i + 2], 250, 256);
    }

    [Fact]
    public void Side_light_produces_a_terminator_crescent()
    {
        // Light from the +X side (a horizon light, like the sector's sun).
        var rgba = MoonRenderer.GenerateLitDisc(
            SolidWhite(), N, N,
            right: Vector3.UnitX, up: Vector3.UnitY, facing: Vector3.UnitZ,
            lightWorld: Vector3.UnitX, ambient: 0.25f);
        // Two texels equidistant from the centre (equal rim geometry), one on
        // the +X (day) half and one on the -X (night) half. dx~+0.27 / dx~-0.23.
        int rx = 40, lx = 24, ry = N / 2;
        int ri = 4 * Idx(rx, ry);
        int li = 4 * Idx(lx, ry);
        float rightB = rgba[ri + 0] / 255f;   // premultiplied red == brightness
        float leftB = rgba[li + 0] / 255f;
        // Day side markedly above the ambient floor; night side at the floor.
        Assert.True(rightB > 0.42f, $"day side too dim: {rightB:F2}");
        Assert.InRange(leftB, 0.0f, 0.34f);
        Assert.True(rightB - leftB > 0.15f, "no terminator separation");
    }

    [Fact]
    public void Disc_edge_fades_before_the_square_corners()
    {
        var rgba = MoonRenderer.GenerateLitDisc(
            SolidWhite(), N, N,
            right: Vector3.UnitX, up: Vector3.UnitY, facing: Vector3.UnitZ,
            lightWorld: Vector3.UnitZ);
        // The side-edge texel (just inside the inscribed disc, r~0.986) has a
        // near-zero rim alpha — a soft circular edge, not a hard square side.
        int edge = 4 * Idx(N - 1, N / 2);
        Assert.InRange(rgba[edge + 3], 0, 45);
        // And the actual corners of the bounding square are already exact 0.
        Assert.Equal(0, rgba[4 * Idx(N - 1, 0) + 3]);
        Assert.Equal(0, rgba[4 * Idx(0, N - 1) + 3]);
    }
}
