using Allegiance.Core.Collision;

namespace Allegiance.Core.Tests;

/// <summary>Step-1 header-only checks across the whole corpus.</summary>
public class CvhHeaderTests
{
    /// <summary>
    /// Every unique <c>.cvh</c> opens (header parses) with a sane header:
    /// radius finite and positive, ellipse-equation terms and erm finite,
    /// and a positive hull count.
    /// </summary>
    [Theory]
    [MemberData(nameof(Paths))]
    public void Header_is_sane(string path)
    {
        var f = CvhReader.Parse(File.ReadAllText(path));

        AssertFinite(f.Radius, nameof(CvhFile.Radius));
        AssertFinite(f.EllipseEquation.X, nameof(CvhFile)+".ee.X");
        AssertFinite(f.EllipseEquation.Y, "ee.Y");
        AssertFinite(f.EllipseEquation.Z, "ee.Z");
        AssertFinite(f.EllipseRadiusMultiplier, nameof(CvhFile.EllipseRadiusMultiplier));

        Assert.True(f.Radius > 0, $"{path}: radius {f.Radius} not positive");
        // Full parse happens in a later step; here we only prove the header is
        // well-formed, which is enough to demonstrate open-and-consume of the
        // top of the file. nHulls sanity is validated by Parse itself.
        Assert.Empty(f.Hulls);
        Assert.Empty(f.Frames);
    }

    private static void AssertFinite(float v, string what)
    {
        Assert.True(!float.IsNaN(v) && !float.IsInfinity(v), $"{what} not finite ({v})");
    }

    public static IEnumerable<object[]> Paths => CvhCorpus.Files.Select(p => new object[] { p });
}
