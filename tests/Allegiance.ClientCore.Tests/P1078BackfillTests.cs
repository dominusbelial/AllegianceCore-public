using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

[Trait("Tier", "Fast")]
public sealed class P1078BackfillTests
{
    [Fact]
    public void Active_human_displaces_bot_afk_refills_after_grace_and_waking_cancels_deficit()
    {
        var policy = new BackfillSeats(5);
        var bots = Enumerable.Range(0, 5).Select(i => new BackfillPilot($"mcp-{i}", 0, true, i == 0, "fighter", true)).ToArray();
        var idle = new BackfillPilot("Pilot", 0, false, false, "human", false, Ready: false);
        Assert.Null(policy.Decide(0, [.. bots, idle], 0));
        Assert.Equal(new(0, 1, 5, "quit", "mcp-1"), policy.Decide(0, [.. bots, idle with { Ready = true }], 1));
        var four = bots.Where(p => p.Name != "mcp-1").ToArray();
        Assert.Null(policy.Decide(0, [.. four, idle with { Ready = true }], 2));
        Assert.Null(policy.Decide(0, [.. four, idle], 3));
        Assert.Null(policy.Decide(0, [.. four, idle], 32.99));
        Assert.Equal(new(0, 0, 4, "join"), policy.Decide(0, [.. four, idle], 33));
        Assert.Null(policy.Decide(0, [.. four, idle with { Ready = true }], 34));
        // A bot's own readiness never masquerades as a human seat or drops it.
        Assert.Null(new BackfillSeats(5).Decide(0, bots.Select(p => p with { Ready = false }).ToArray(), 0));
        Assert.Null(new BackfillSeats(5).Decide(0, [.. bots, idle with { Side = -2, Ready = true }], 0));
    }
}
