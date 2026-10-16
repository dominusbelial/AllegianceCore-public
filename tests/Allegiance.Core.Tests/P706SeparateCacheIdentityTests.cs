using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P706 — the per-body separation cache (<c>Separate.ProbeCache.GetB</c>) is a
/// pure memoisation of the 27 body-2 <c>GetMaxExtreme</c> support vertices,
/// which depend only on the collider's hull/shape/scale/radius and the FIXED
/// probe directions (never its orientation). This test proves it changes
/// nothing: for a wide sweep of real hulls, attitudes, positions and masses,
/// <c>SeparateColliders</c> fed a cache produces byte-for-byte identical
/// positions and normal to the uncached path. It fails if anyone "helps" the
/// cache by approximating the support (e.g. substituting a bounding sphere).
/// </summary>
public sealed class P706SeparateCacheIdentityTests
{
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cached_separate_is_byte_identical_to_uncached()
    {
        Require.Artwork();
        var hulls = new[] { "fig07", "faohshbmb", "ss24a" }.Select(name =>
            CvhReader.Parse(File.ReadAllText(Path.Combine(TestArtRoot.Value, name + ".cvh")))).ToArray();
        var rng = new Random(706);
        for (int iter = 0; iter < 500; iter++)
        {
            var oa = RandomOrientation(rng);
            var ob = RandomOrientation(rng);
            var ha = hulls[rng.Next(hulls.Length)];
            var hb = hulls[rng.Next(hulls.Length)];
            var a = new Collider(0, RandVec(rng, 200), RandVec(rng, 120), oa, ha, 30, isStatic: false, HitShape.Sphere);
            var b = new Collider(1, RandVec(rng, 200), iter % 3 == 0 ? Vector3.Zero : RandVec(rng, 120), ob, hb, 30, isStatic: iter % 4 == 0, HitShape.Sphere);
            // Ships resolve against the concrete convex-hull sub-shape selected
            // by detection (hts = 0 here: all three hulls are single-sub-hull).
            const int hts = 0;
            float t = (float)rng.NextDouble() * .020f;
            bool equal = iter % 2 == 0;

            var uncached = Separate.SeparateColliders(a, b, t, equal, hts, hts);
            var cached = Separate.SeparateColliders(a, b, t, equal, hts, hts, new Separate.ProbeCache());

            Assert.Equal(uncached.Position1, cached.Position1);
            Assert.Equal(uncached.Position2, cached.Position2);
            Assert.Equal(uncached.Normal, cached.Normal);
        }
    }

    private static Vector3 RandVec(Random rng, float span) => new(
        (float)rng.NextDouble() * span - span / 2,
        (float)rng.NextDouble() * span / 3 - span / 6,
        (float)rng.NextDouble() * span / 3 - span / 6);

    private static Orientation RandomOrientation(Random rng)
    {
        double u = rng.NextDouble() * 2.0 - 1.0;
        double phi = rng.NextDouble() * 2.0 * Math.PI;
        double r = Math.Sqrt(Math.Max(0.0, 1.0 - u * u));
        var fwd = new Vector3((float)(r * Math.Cos(phi)), (float)(r * Math.Sin(phi)), (float)u);
        var up = new Vector3((float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1, (float)rng.NextDouble() * 2 - 1);
        var o = new Orientation();
        o.Set(fwd, up);
        return o;
    }
}
