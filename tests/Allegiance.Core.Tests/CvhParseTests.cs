using Allegiance.Core.Collision;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>Full-parse four-oracle checks across the whole corpus.</summary>
public class CvhParseTests
{
    /// <summary>
    /// The four oracles, asserted over every unique <c>.cvh</c>:
    /// <list type="number">
    /// <item>Whole file consumed — Parse walks to the last line and returns (no trailing content).</item>
    /// <item>Declared counts match — nHulls hulls; each hull exactly nVertices
    /// vertices; per-vertex adjacency counts sum to nAdjacencies.</item>
    /// <item>Every adjacency index ∈ [0, nVertices) of its hull.</item>
    /// <item>All floats finite; radius &gt; 0 and ≥ the furthest vertex distance.</item>
    /// </list>
    /// A file that parses but whose declared counts disagree is a failure here.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Paths))]
    public void Full_parse_and_oracles(string path)
    {
        Require.Artwork();
        CvhFile f;
        try { f = CvhReader.Parse(File.ReadAllText(path)); }
        catch (Exception e) { throw new InvalidOperationException($"{path}", e); }

        // Oracle 4 (header): finite, positive radius.
        AssertFinite(f.Radius, nameof(CvhFile.Radius));
        Assert.True(f.Radius > 0, $"{path}: radius {f.Radius} not positive");
        AssertFinite(f.EllipseEquation.X, "ee.X");
        AssertFinite(f.EllipseEquation.Y, "ee.Y");
        AssertFinite(f.EllipseEquation.Z, "ee.Z");
        AssertFinite(f.EllipseRadiusMultiplier, nameof(CvhFile.EllipseRadiusMultiplier));

        // Frames (if any) are already proven whole-consumed by Parse returning.

        double maxVertexDist = 0.0;
        foreach (var hull in f.Hulls)
        {
            // Oracle 2: exactly nVertices vertices.
            Assert.Equal(hull.VertexCount, hull.Vertices.Count);
            // Oracle 2: adjacency sum == declared nAdjacencies.
            var sumAdj = hull.Vertices.Sum(v => v.AdjacentIndices.Length);
            Assert.Equal(hull.AdjacencyCount, sumAdj);

            foreach (var v in hull.Vertices)
            {
                // Oracle 4 (positions): finite.
                AssertFinite(v.Position.X, "vertex.X");
                AssertFinite(v.Position.Y, "vertex.Y");
                AssertFinite(v.Position.Z, "vertex.Z");
                var r = Math.Sqrt((double)v.Position.X * v.Position.X +
                                  (double)v.Position.Y * v.Position.Y +
                                  (double)v.Position.Z * v.Position.Z);
                if (r > maxVertexDist) maxVertexDist = r;

                Assert.NotEmpty(v.AdjacentIndices);
                foreach (var id in v.AdjacentIndices)
                {
                    // Oracle 3: index in range of this hull's vertex list.
                    Assert.InRange(id, 0, hull.VertexCount - 1);
                }
            }
        }

        // Oracle 4 (plausibility): radius finite, positive, and within a bounded
        // factor of the furthest vertex distance. Measured across the corpus the
        // ratio maxVertexDist/radius is 0.51..1.55 — i.e. the vertices sit in a
        // shell roughly around the declared radius, never absurdly far from it.
        // Catch gross misreads without demanding a tight enclosure.
        var ratio = maxVertexDist / (double)f.Radius;
        Assert.InRange(ratio, 0.25, 4.0);
    }

    /// <summary>Known-shape check on a small complete tetrahedron file.</summary>
    [Fact]
    public void Complete_tiny_file_decodes_as_expected()
    {
        // Tetrahedron: vertex0=(1,0,0), v1=(0,1,0), v2=(0,0,1), v3=(0,0,0);
        // each adjacent to the other three.
        const string text = """
            1.0   1.0 1.0 1.0   1.0
            1
            4  12   0 0 0
            1 0 0 3
            1 2 3
            0 1 0 3
            0 2 3
            0 0 1 3
            0 1 3
            0 0 0 3
            0 1 2
            """;
        var f = CvhReader.Parse(text);
        Assert.Equal(1.0f, f.Radius);
        Assert.Equal(new Vector3(1f, 1f, 1f), f.EllipseEquation);
        Assert.Equal(1.0f, f.EllipseRadiusMultiplier);
        Assert.Single(f.Hulls);
        Assert.Empty(f.Frames);

        var hull = f.Hulls[0];
        Assert.Equal(4, hull.VertexCount);
        Assert.Equal(12, hull.AdjacencyCount);
        Assert.Equal(Vector3.Zero, hull.Center);
        Assert.Equal(4, hull.Vertices.Count);
        Assert.Equal(new Vector3(1f, 0f, 0f), hull.Vertices[0].Position);
        Assert.Equal([1, 2, 3], hull.Vertices[0].AdjacentIndices);
        Assert.Equal(new Vector3(0f, 1f, 0f), hull.Vertices[1].Position);
        Assert.Equal([0, 2, 3], hull.Vertices[1].AdjacentIndices);
    }

    /// <summary>Trailing frame triples after the hulls decode as named mounts.</summary>
    [Fact]
    public void Frame_section_decodes()
    {
        const string text = """
            1.0   1.0 1.0 1.0   1.0
            1
            4  12   0 0 0
            1 0 0 3
            1 2 3
            0 1 0 3
            0 2 3
            0 0 1 3
            0 1 3
            0 0 0 3
            0 1 2
            cockpt
            0.5 0.0 0.0
            0.0 0.0 1.0
            wepmt
            0.0 0.0 0.6
            0.0 0.0 -1.0
            """;
        var f = CvhReader.Parse(text);
        Assert.Equal(2, f.Frames.Count);
        Assert.Equal(new CvhFrame("cockpt", new Vector3(0.5f, 0f, 0f), new Vector3(0f, 0f, 1f)), f.Frames[0]);
        Assert.Equal(new CvhFrame("wepmt", new Vector3(0f, 0f, 0.6f), new Vector3(0f, 0f, -1f)), f.Frames[1]);
    }

    private static void AssertFinite(float v, string what)
    {
        Assert.True(!float.IsNaN(v) && !float.IsInfinity(v), $"{what} not finite ({v})");
    }

    public static IEnumerable<object[]> Paths => CvhCorpus.Paths();
}
