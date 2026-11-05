using Allegiance.TestInfra;
using Allegiance.Tools.P928;

namespace Allegiance.Core.Tests;

public sealed class P928AllocationTests
{
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Live")]
    public void Crowded_sector_matches_pre_refactor_500_tick_golden(bool supplemental)
    {
        Require.Artwork();
        using var stream = typeof(P928AllocationTests).Assembly.GetManifestResourceStream(
            (supplemental ? "Allegiance.Core.Tests.Fixtures.P928SupplementalGolden.csv" : "Allegiance.Core.Tests.Fixtures.P928Golden.csv"))!;
        using var reader = new StreamReader(stream);
        var expected = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var actual = P928Trace.Run(supplemental);
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
            Assert.True(expected[i] == actual[i], $"Golden mismatch at line {i + 1}:\nexpected {expected[i]}\nactual   {actual[i]}");
        var counts = actual.Skip(2).Select(line => line.Split(',')).ToArray();
        Assert.True(counts.Sum(r => int.Parse(r[5])) > 0, "No collisions exercised");
        Assert.True(counts.Sum(r => int.Parse(r[6])) > 0, "No damage exercised");
        Assert.True(counts.Sum(r => int.Parse(r[7])) > 0, "No kills exercised");
        Assert.True(counts.Sum(r => int.Parse(r[8])) > 1000, "No sustained weapon fire");
    }
}
