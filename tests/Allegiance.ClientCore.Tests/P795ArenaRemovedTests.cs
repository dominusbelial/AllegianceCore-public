using System.Runtime.CompilerServices;
using System.Text.Json;
using Allegiance.Tools.McpSpawner;
using ModelContextProtocol.Protocol;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P795 — the MCP arena battle-start mode was removed (Domingo, 2026-10-22:
/// a 200v200 arena collapsed into a home-sector death loop). A caller that still
/// sends <c>mode:"arena"</c> or the legacy <c>arena:true</c> flag must get a
/// clear tool error naming the removal and pointing at <c>march</c> — never a
/// silent fallback to march.
/// </summary>
public sealed class P795ArenaRemovedTests
{
    /// <summary>The guard runs before any bot/server work, so an uninitialised
    /// manager is enough (no wire traffic, no live server).</summary>
    private static BotManager Manager() =>
        (BotManager)RuntimeHelpers.GetUninitializedObject(typeof(BotManager));

    private static string CallError(string json)
    {
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = McpTools.Handle("spawn_scenario", args, Manager());
        Assert.True(result.IsError ?? false, "an arena request must be a tool error, not a scenario");
        return string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    [Theory]
    // Homogeneous path (size + hull) and the mixed-fleet/showcase dispatch path.
    [InlineData("""{"size":2,"target_sector":"Alderan","mode":"arena"}""")]
    [InlineData("""{"size":2,"target_sector":"Alderan","arena":true}""")]
    [InlineData("""{"size":2,"target_sector":"Alderan","mode":"ARENA"}""")]
    [InlineData("""{"preset":"showcase","size_scale":1,"target_sector":"Alderan","mode":"arena"}""")]
    [InlineData("""{"preset":"showcase","size_scale":1,"target_sector":"Alderan","arena":true}""")]
    [InlineData("""{"fleet":[{"hull":"Scout","count":1}],"target_sector":"Alderan","arena":true}""")]
    public void Arena_request_returns_the_removal_error(string json)
    {
        var text = CallError(json);
        Assert.Contains("arena mode was removed in P795", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("march", text, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"mode":"march"}""")]
    [InlineData("""{"arena":false}""")]
    [InlineData("""{}""")]
    public void March_request_is_not_rejected_by_the_removal_guard(string json)
    {
        // No `size`/fleet: the scenario path rejects it for that reason before any
        // server work, which also proves the arena guard did not swallow march.
        var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        var result = McpTools.Handle("spawn_scenario", args, Manager());
        var text = string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
        Assert.DoesNotContain("arena mode was removed", text, StringComparison.OrdinalIgnoreCase);
    }
}
