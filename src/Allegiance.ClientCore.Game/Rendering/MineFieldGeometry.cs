using System;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P291 — the pure, headless-testable geometry of a native <c>MineFieldGeo</c>
/// (this port's client half of <c>src/effect/efgeo.cpp:15-87</c>). A deployed
/// mine is not a mesh; native renders it as a cloud of textured decals
/// (<c>MineFieldGeoImpl</c>): <c>GetCount</c> decals at uniformly-random
/// positions within a sphere of the mine's radius, each a camera-facing quad of
/// the real <c>fxmine</c> texture (see <see cref="MineModelSource"/>). This
/// class supplies the two numbers the renderer needs — how many decals and
/// where — deterministically so tests and repeated frames are reproducible.
/// </summary>
/// <remarks>
/// <para><b>Faithful count.</b> Native <c>GetCount(strength, radius) =
/// 1 + int(strength * radius * radius / 100.0f)</c> (<c>efgeo.cpp:21-24</c>) is
/// called with <c>strength = 1.0f</c> (<c>mineigc.cpp:103</c>), so
/// <see cref="OffsetCount"/> collapses to <c>1 + (int)(radius*radius/100)</c>.
/// A radius-100 mine yields 101 decals, radius-200 yields 401 — the dense
/// "mine field" look native intends.</para>
/// <para><b>Deterministic sampling (documented divergence).</b> Native uses the
/// global <c>Vector::RandomPosition(radius)</c> — a new random cloud on every
/// creation. This port instead derives a fixed per-mine seed (the mine's real
/// <see cref="Allegiance.ClientCore.State.MineEntity.MineId"/>) and samples the
/// uniform-in-ball distribution deterministically, so a given mine always
/// renders the same cloud (frame-stable, unit-testable) while still differing
/// between mines. <c>Vector::RandomPosition</c> is uniform within the sphere
/// (includes the interior, not just the surface), reproduced here by a uniform
/// direction on the unit sphere times a cube-root-biased radial distance.
/// </para>
/// </remarks>
public static class MineFieldGeometry
{
    /// <summary>The decal count for a mine of the given radius, faithful to
    /// native <c>GetCount(1.0f, radius)</c> — clamped to at least 1.</summary>
    public static int OffsetCount(float radius) => OffsetCount(1.0f, radius);

    /// <summary>
    /// P574 — the decal count for a mine of the given radius and CURRENT
    /// <paramref name="strength"/> (fraction), faithful to native
    /// <c>GetCount(strength, radius) = 1 + int(strength·radius²/100)</c>
    /// (<c>efgeo.cpp:21-24</c>) — clamped to at least 1. Native calls
    /// <c>SetMineStrength(m_fraction)</c> on every hit
    /// (<c>mineigc.h:142,180</c>), and <c>MineFieldGeoImpl::SetStrength</c>
    /// truncates the decal vector to this count by <c>m_vposition.SetCount(count)</c>
    /// (<c>efgeo.cpp:43-51</c>) — i.e. it <b>keeps the first N decals and drops
    /// the tail by index</b>. The renderer reproduces that by drawing only the
    /// first <c>OffsetCount(strength, radius)</c> decals.
    /// </summary>
    public static int OffsetCount(float strength, float radius)
    {
        float v = Math.Max(0f, strength) * radius * radius / 100.0f;
        if (v < 1.0f) return 1;
        int n = (int)v;
        return n + 1;
    }

    /// <summary>
    /// Fill <paramref name="dest"/> with <paramref name="dest"/>.Length offsets,
    /// each a uniformly-random point within the sphere of <paramref name="radius"/>
    /// centred on the mine, drawn deterministically from <paramref name="seed"/>
    /// (the mine's real id). Consumers add these offsets to the mine's world
    /// position to place the decals — the <c>MineFieldGeo</c> <c>m_vposition</c>.
    /// </summary>
    public static void SampleUniformInSphere(float radius, int seed, Span<Vector3> dest)
    {
        uint s = (uint)seed * 0x9E3779B9u ^ 0x1234ABCDu;
        for (int i = 0; i < dest.Length; i++)
        {
            // Uniform direction on the unit sphere (Marsaglia), then cube-root
            // radial for uniform volume density (native RandomPosition is
            // uniform in the ball).
            float u1 = Next01(ref s);
            float u2 = Next01(ref s);
            float theta = u1 * MathHelper.TwoPi;
            float z = 2f * u2 - 1f;
            float sr = MathF.Sqrt(Math.Max(0f, 1f - z * z));
            float dirX = sr * MathF.Cos(theta);
            float dirY = sr * MathF.Sin(theta);
            float r = radius * MathF.Cbrt(Next01(ref s));
            dest[i] = new Vector3(dirX * r, dirY * r, z * r);
        }
    }

    // Deterministic SplitMix32 xorshift fed from the seed.
    private static float Next01(ref uint state)
    {
        // SplitMix32 — tiny, well-distributed, fully deterministic.
        state += 0x9E3779B9u;
        uint z = state;
        z = (z ^ (z >> 16)) * 0x21f0aaadu;
        z = (z ^ (z >> 15)) * 0x735a2d97u;
        z ^= z >> 15;
        return (z & 0xFFFFFFu) / 16777216f; // 24 bits of entropy in [0,1)
    }
}
