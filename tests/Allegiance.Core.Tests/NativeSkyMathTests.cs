using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P576 — faithful native sky math. These pin the exact MSVC-CRT <c>rand()</c>
/// sequence and the native star/planet rules so the per-sector sky is
/// byte-identical to native (cited: effect/starimage.cpp, zlib/vector.cpp,
/// zlib/zmath.h, Igc/clusterIGC.cpp, _Utility/Utility.h).
/// </summary>
public class NativeSkyMathTests
{
    // ---- MSVC CRT rand() fidelity --------------------------------------
    [Fact]
    public void MsCrtRand_MatchesKnownSequence_SeedOne()
    {
        // MSVC rand(): next = next*214013+2531011 (32-bit), return (next>>16)&0x7fff.
        // seed 1: next = 2745024 -> (2745024>>16)=41 -> 41.
        NativeSkyMath.MsCrtRand.Srand(1);
        Assert.Equal(41, NativeSkyMath.MsCrtRand.Rand());
    }

    [Fact]
    public void MsCrtRand_DeterministicForSameSeed()
    {
        var a = Sequence(12345, 8);
        var b = Sequence(12345, 8);
        Assert.Equal(a, b);
    }

    [Fact]
    public void MsCrtRand_DifferentSeed_DifferentSequence()
    {
        Assert.NotEqual(Sequence(100, 8), Sequence(101, 8));
    }

    private static int[] Sequence(uint seed, int n)
    {
        NativeSkyMath.MsCrtRand.Srand(seed);
        var r = new int[n];
        for (int i = 0; i < n; i++) r[i] = NativeSkyMath.MsCrtRand.Rand();
        return r;
    }

    // ---- Star generation (StarImage::SetCount, starimage.cpp:32-53) -----
    [Fact]
    public void GenerateStars_CountIsFourTimesNStars()
    {
        var stars = NativeSkyMath.GenerateStars(7u, (short)50);
        Assert.Equal(200, stars.Length);          // nStars * 4
    }

    [Fact]
    public void GenerateStars_DeterministicSameSeed()
    {
        var a = NativeSkyMath.GenerateStars(4242u, (short)37);
        var b = NativeSkyMath.GenerateStars(4242u, (short)37);
        Assert.Equal(a, b);                        // record equality, incl. brightness
    }

    [Fact]
    public void GenerateStars_DifferentSeed_DifferentField()
    {
        var a = NativeSkyMath.GenerateStars(4242u, (short)37);
        var b = NativeSkyMath.GenerateStars(4243u, (short)37);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void GenerateStars_DirectionsAreUnitVectors()
    {
        foreach (var s in NativeSkyMath.GenerateStars(99u, (short)120))
        {
            float len = MathF.Sqrt(s.DirX * s.DirX + s.DirY * s.DirY + s.DirZ * s.DirZ);
            Assert.InRange(len, 0.999f, 1.001f);   // RandomDirection is unit
        }
    }

    [Fact]
    public void GenerateStars_BrightnessWithinRange()
    {
        foreach (var s in NativeSkyMath.GenerateStars(99u, (short)120))
            Assert.InRange(s.Brightness, 0.25f, 1.0f);   // random(0.25, 1)
    }

    // ---- Planet direction (clusterIGC.cpp:44-57, BytePercentage /240) ----
    [Fact]
    public void PlanetDirection_EquatorPrimeMeridian_FacesPositiveX()
    {
        // sinLat=120/240-0.5 = 0 -> cosLat=1; lon=0/240*2π=0 -> (1,0,0)
        var d = NativeSkyMath.PlanetDirection(120, 0);
        Assert.InRange(d.X, 0.999f, 1.001f);
        Assert.InRange(d.Y, -0.001f, 0.001f);
        Assert.InRange(d.Z, -0.001f, 0.001f);
    }

    [Fact]
    public void PlanetDirection_QuarterLongitude_FacesPositiveY()
    {
        // lon = 60/240 * 2π = π/2 -> (cos,sin)=(0,1); sinLat 120 -> cosLat 1.
        var d = NativeSkyMath.PlanetDirection(120, 60);
        Assert.InRange(d.X, -0.001f, 0.001f);
        Assert.InRange(d.Y, 0.999f, 1.001f);
    }

    [Fact]
    public void PlanetDirection_NorthPole_FacesPositiveZ()
    {
        // sinLat = 240/240 - 0.5 = 0.5 -> z=0.5; cosLat = sqrt(0.75).
        var d = NativeSkyMath.PlanetDirection(240, 0);
        Assert.InRange(d.Z, 0.499f, 0.501f);
        float horiz = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        Assert.InRange(horiz, 0.865f, 0.868f);   // sqrt(3)/2
    }

    [Fact]
    public void PlanetDirection_IsUnit()
    {
        var d = NativeSkyMath.PlanetDirection(90, 200);
        float len = MathF.Sqrt(d.X * d.X + d.Y * d.Y + d.Z * d.Z);
        Assert.InRange(len, 0.999f, 1.001f);
    }
}
