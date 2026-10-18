using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

public class TierConventionTests
{
    [Fact]
    public void Default_is_fast_and_preserves_orthogonal_traits()
    {
        var traits = new Dictionary<string, List<string>> { ["Requires"] = ["MeshyPack", "Gpu"], ["Lane"] = ["LiveTiming"] };
        TierDiscoverer.EnsureTier(traits, "fixture");
        Assert.Equal(["Fast"], traits["Tier"]);
        Assert.Equal(["MeshyPack", "Gpu"], traits["Requires"]);
        Assert.Equal(["LiveTiming"], traits["Lane"]);
    }

    [Theory]
    [InlineData("Fast")]
    [InlineData("Live")]
    [InlineData("Perf")]
    public void Explicit_tier_is_preserved(string tier)
    {
        var traits = new Dictionary<string, List<string>> { ["Tier"] = [tier] };
        TierDiscoverer.EnsureTier(traits, "fixture");
        Assert.Equal([tier], traits["Tier"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Fast,Live")]
    [InlineData("Fast,Fast")]
    [InlineData("fast")]
    public void Empty_duplicate_and_invalid_tiers_fail_discovery(string tierList)
    {
        var traits = new Dictionary<string, List<string>> { ["Tier"] = tierList.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList() };
        Assert.Contains("fixture", Assert.Throws<InvalidOperationException>(() => TierDiscoverer.EnsureTier(traits, "fixture")).Message);
    }
}
