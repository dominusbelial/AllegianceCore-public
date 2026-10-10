using Allegiance.Core.Collision;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>Header-field checks across the whole corpus.</summary>
public class CvhHeaderTests
{
    /// <summary>
    /// Every unique <c>.cvh</c> opens (header parses) with a sane header:
    /// radius finite and positive, ellipse-equation terms and erm finite.
    /// (Full structural oracles live in <see cref="CvhParseTests"/>.)
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [MemberData(nameof(Paths))]
    public void Header_fields_are_sane(string path)
    {
        Require.Artwork();
        var f = CvhReader.Parse(File.ReadAllText(path));

        AssertFinite(f.Radius, nameof(CvhFile.Radius));
        AssertFinite(f.EllipseEquation.X, "ee.X");
        AssertFinite(f.EllipseEquation.Y, "ee.Y");
        AssertFinite(f.EllipseEquation.Z, "ee.Z");
        AssertFinite(f.EllipseRadiusMultiplier, nameof(CvhFile.EllipseRadiusMultiplier));
        Assert.True(f.Radius > 0, $"{path}: radius {f.Radius} not positive");
    }

    private static void AssertFinite(float v, string what)
    {
        Assert.True(!float.IsNaN(v) && !float.IsInfinity(v), $"{what} not finite ({v})");
    }

    public static IEnumerable<object[]> Paths => CvhCorpus.Paths();
}
