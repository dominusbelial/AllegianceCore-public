using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed class P832eMasterDecisionTraceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void No_conquest_decisions_match_dispatch_master_for_100_ticks(bool artifacts)
    {
        var trace = new StringBuilder();
        for (int tick = 0; tick < 100; tick++)
        {
            var candidates = Enumerable.Range(0, 200).Select(i => new FlagCandidate(832, 0, $"pilot-{i:D3}",
                (short)i, i < 20, tick % 4 == 0 && i == 0, tick % 3 == 0 ? (short)901 : null,
                (i + tick) % 200, Artifacts: artifacts, Flags: !artifacts));
            trace.Append(JsonSerializer.Serialize(BotManager.PlanFlagDuties(candidates))).Append('\n');
        }
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace.ToString())));
        // Recorded from unmodified dispatch master 47d1ac8a, same 100 frames.
        Assert.Equal(artifacts
            ? "F1E6E61CF1FFC555CFB0ED888A4ADE7C8EB616C0AA4BFEE86438DE0F2D483BFB"
            : "B1C3CAAEBEFB6E0DA70EFE29A49F304F3F6CA5C2286B5C7053CE90F6B9A57C8D", digest);
    }
}
