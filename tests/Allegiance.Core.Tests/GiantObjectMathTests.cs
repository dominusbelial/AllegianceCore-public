using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P592 — the pure, headless-testable GIANT sky-object geometry
/// (<see cref="GiantObjectMath"/>) and configuration (<see cref="GiantObjectConfig"/>):
/// the curved sky-sphere patch (one bounded sheet, UV never wraps, the centre
/// sits on the patch's local "equator" so there is no pole pinch, all nodes
/// distinct, angular extent correct), the per-cluster placement (Terra owns a
/// giant, other clusters do not, direction resolved clear of sun/planet) and the
/// json config parsing (image/angle/direction/brightness). Brand-new modules, so
/// these pin the geometry contract they introduce.
/// </summary>
public class GiantObjectMathTests
{
    private const float Deg2Rad = MathF.PI / 180f;

    private static float AngleBetween(NativeSkyMath.Vector3Unit a, NativeSkyMath.Vector3Unit b)
    {
        float d = Math.Clamp(a.X * b.X + a.Y * b.Y + a.Z * b.Z, -1f, 1f);
        return MathF.Acos(d);
    }

    // ---- patch geometry -------------------------------------------------

    [Fact]
    public void Patch_HasExpectedGridSize()
    {
        var verts = GiantObjectMath.BuildPatch(new NativeSkyMath.Vector3Unit(1, 0, 0), 45f * Deg2Rad, 40);
        Assert.Equal(41 * 41, verts.Length);          // (segments+1)^2 nodes
    }

    [Fact]
    public void Patch_UvNeverWraps()
    {
        // Every u,v must stay inside [0,1] — a single bounded sheet, never a
        // full dome and never wrapping.
        var verts = GiantObjectMath.BuildPatch(new NativeSkyMath.Vector3Unit(0, 1, 0), 45f * Deg2Rad, 24);
        foreach (var v in verts)
        {
            Assert.InRange(v.U, 0f, 1f);
            Assert.InRange(v.V, 0f, 1f);
        }
    }

    [Fact]
    public void Patch_CentreMapsToTextureCentre_NotAPole()
    {
        // The grid node at (u,v)=(0.5,0.5) is the patch centre (i=j=segments/2).
        // With the centre on its local equator there is NO converging fan of UV
        // columns: the centre is one ordinary node and its neighbours are
        // distinct directions around it (no pole pinching).
        int seg = 20, n = seg + 1;
        var verts = GiantObjectMath.BuildPatch(new NativeSkyMath.Vector3Unit(0, 0, 1), 45f * Deg2Rad, seg);
        var centre = verts[(seg / 2) * n + (seg / 2)];
        Assert.Equal(0.5f, centre.U, 3);
        Assert.Equal(0.5f, centre.V, 3);
        Assert.True(AngleBetween(centre.Dir, new NativeSkyMath.Vector3Unit(0, 0, 1)) < 1e-3f,
            "centre node must point exactly at the patch centre");
    }

    [Fact]
    public void Patch_NoPolePinch_AllNodesDistinct()
    {
        // No two grid nodes may collapse to the same direction (a pole pinch
        // would make several UV nodes share one vertex). Tangent-plane lifting
        // is injective over the bounded square, so min separation > 0.
        var verts = GiantObjectMath.BuildPatch(new NativeSkyMath.Vector3Unit(0.623f, -0.511f, 0.725f), 45f * Deg2Rad, 16);
        float minSep = float.MaxValue;
        for (int i = 0; i < verts.Length; i++)
        {
            for (int j = i + 1; j < verts.Length; j++)
            {
                float sep = AngleBetween(verts[i].Dir, verts[j].Dir);
                if (sep < minSep) minSep = sep;
            }
        }
        Assert.True(minSep > 1e-4f, $"node collapse (pole pinch) detected, min sep={minSep}");
    }

    [Fact]
    public void Patch_AngularExtentMatchesHalfAngle()
    {
        // The patch spans 2*halfAngle across: edge-midpoint nodes sit at
        // ~halfAngle from the centre (tangent-plane lift), and nothing exceeds
        // ~atan(sqrt2*tan(half)) at the corners — all nodes within a small
        // tolerance of the expected cap.
        var verts = GiantObjectMath.BuildPatch(new NativeSkyMath.Vector3Unit(1, 0, 0), 45f * Deg2Rad, 20);
        float maxAng = 0f;
        foreach (var v in verts)
            maxAng = Math.Max(maxAng, AngleBetween(v.Dir, new NativeSkyMath.Vector3Unit(1, 0, 0)));
        float expectedCorner = MathF.Atan(MathF.Sqrt(2f) * MathF.Tan(45f * Deg2Rad));
        Assert.True(maxAng <= expectedCorner + 1e-3f,
            $"corner extent {maxAng} exceeds expected {expectedCorner}");
        // Edge midpoints should be at ~halfAngle from the centre.
        var edge = verts[0 * 21 + 10];  // i=0 (u=0), j=10 (v=0.5)
        float edgeAng = AngleBetween(edge.Dir, new NativeSkyMath.Vector3Unit(1, 0, 0));
        Assert.InRange(edgeAng, 44.5f * Deg2Rad, 45.5f * Deg2Rad);
    }

    [Fact]
    public void Patch_RotationTiltsTheTexture_NotThePatch()
    {
        var a = GiantObjectMath.BuildPatch(new NativeSkyMath.Vector3Unit(1, 0, 0), 40f * Deg2Rad, 8, 0f);
        var b = GiantObjectMath.BuildPatch(new NativeSkyMath.Vector3Unit(1, 0, 0), 40f * Deg2Rad, 8, 1.3f);
        int seg = 8, n = seg + 1;
        var ca = a[(seg / 2) * n + (seg / 2)];
        var cb = b[(seg / 2) * n + (seg / 2)];
        // Roll spins the texture ABOUT the patch centre: the centre direction is
        // unchanged, the angular footprint is unchanged, but a non-central UV
        // node's direction differs (the texture content is rolled).
        Assert.True(AngleBetween(ca.Dir, cb.Dir) < 1e-4f, "centre must be unchanged by roll");
        Assert.NotEqual(a[0].Dir, b[0].Dir);
        // Same footprint: identical angular extent to the centre.
        float extA = 0, extB = 0;
        foreach (var v in a) extA = Math.Max(extA, AngleBetween(v.Dir, ca.Dir));
        foreach (var v in b) extB = Math.Max(extB, AngleBetween(v.Dir, cb.Dir));
        Assert.True(MathF.Abs(extA - extB) < 1e-3f, "roll must not change the patch footprint");
    }

    // ---- per-cluster placement -------------------------------------------

    [Fact]
    public void Terra_OwnsAGiant_OthersDoNot()
    {
        const string json = """
        { "objects": [
            { "cluster_id": 1057, "sector": "Terra", "image": "milkyway_terra.png",
              "direction": [1, 0, 0], "brightness": 0.8 }
        ] }
        """;
        var cfg = GiantObjectConfig.Parse(json);
        Assert.True(cfg.ByCluster.ContainsKey(1057), "Terra (1057) must own the giant");
        Assert.False(cfg.ByCluster.ContainsKey(1052), "Alderan has none in this config");
    }

    [Fact]
    public void Config_ParsesImageAngleBrightnessAndDirection()
    {
        const string json = """
        { "span_deg": 90, "objects": [
            { "cluster_id": 777, "image": "g.png", "angle_deg": 75,
              "direction": [0, 1, 0], "brightness": 0.6321, "rot_deg": 12.5 }
        ] }
        """;
        var cfg = GiantObjectConfig.Parse(json);
        var s = cfg.ByCluster[777];
        Assert.Equal("g.png", s.Image);
        Assert.Equal(75f, s.AngleDeg);
        Assert.Equal(0.6321f, s.Brightness, 3);
        Assert.Equal(12.5f, s.RotationDeg, 3);
        Assert.Equal(0f, s.Direction.X, 3);
        Assert.Equal(1f, s.Direction.Y, 3);
        Assert.Equal(0f, s.Direction.Z, 3);
    }

    [Fact]
    public void Direction_IsKeptClearOfSunAndPlanet()
    {
        const string json = """
        { "objects": [
            { "cluster_id": 1, "image": "g.png", "direction": [0, 0, 1] }
        ] }
        """;
        var cfg = GiantObjectConfig.Parse(json);
        var s = cfg.ByCluster[1];
        // Obstacles RIGHT on top of the nominal direction: the resolved
        // direction must be pushed clear of both.
        var sun = new NativeSkyMath.Vector3Unit(0, 0, 1);
        var planet = new NativeSkyMath.Vector3Unit(0, 0, 1);
        var dir = GiantObjectConfig.ResolveDirection(s, sun, planet, minClearDeg: 25f);
        float minRad = 25f * Deg2Rad;
        Assert.True(AngleBetween(dir, sun) >= minRad - 1e-3f, "not clear of the sun");
        Assert.True(AngleBetween(dir, planet) >= minRad - 1e-3f, "not clear of the planet");
    }

    [Fact]
    public void Direction_Unchanged_WhenAlreadyClear()
    {
        const string json = """
        { "objects": [
            { "cluster_id": 2, "image": "g.png", "direction": [-1, 0, 0] }
        ] }
        """;
        var cfg = GiantObjectConfig.Parse(json);
        var s = cfg.ByCluster[2];
        var sun = new NativeSkyMath.Vector3Unit(1, 0, 0);   // opposite
        var planet = new NativeSkyMath.Vector3Unit(0, 1, 0); // perpendicular
        var dir = GiantObjectConfig.ResolveDirection(s, sun, planet, 25f);
        Assert.True(AngleBetween(dir, new NativeSkyMath.Vector3Unit(-1, 0, 0)) < 1e-4f,
            "a clear direction must not be moved");
    }
}
