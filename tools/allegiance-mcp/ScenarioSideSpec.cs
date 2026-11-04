using System.Text.Json;
using Allegiance.Core.Model;

namespace Allegiance.Tools.McpSpawner;

internal sealed record ScenarioSideSpec(int Count, JsonElement? Fleet, int Tech)
{
    internal static ScenarioSideSpec[]? Read(IDictionary<string, JsonElement> args, int actualSides)
    {
        if (!args.TryGetValue("sides", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 2 or > 6 || value.GetArrayLength() != actualSides)
            throw new ArgumentException("spawn_scenario: sides must contain 2..6 entries and match the mission's actual side count");
        // Ambiguous requests should fail before creating any fleet slots.
        if (new[] { "count_a", "count_b", "fleet_a", "fleet_b", "fleet", "tech_a", "tech_b", "size", "size_scale" }.Any(args.ContainsKey))
            throw new ArgumentException("spawn_scenario: sides cannot be combined with legacy fleet/count/tech/size arguments");
        return value.EnumerateArray().Select((row, index) =>
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("count", out var count)
                || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int n) || n is < 1 or > 200)
                throw new ArgumentException($"spawn_scenario: sides[{index}].count must be an integer 1..200");
            int tech = 0;
            if (row.TryGetProperty("tech", out var t) && (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out tech) || tech < 0))
                throw new ArgumentException($"spawn_scenario: sides[{index}].tech must be a nonnegative integer (legacy, ignored)");
            JsonElement? fleet = null;
            if (row.TryGetProperty("fleet", out var f))
            {
                if (f.ValueKind != JsonValueKind.Array || f.GetArrayLength() == 0)
                    throw new ArgumentException($"spawn_scenario: sides[{index}].fleet must be a nonempty array");
                fleet = f.Clone();
            }
            return new ScenarioSideSpec(n, fleet, tech);
        }).ToArray();
    }
}

internal static partial class McpTools
{
    private static List<Showcase.FleetEntry> ResolveSideFleet(ScenarioSideSpec spec,
        IDictionary<string, JsonElement> args, StaticCore core, bool showcase, string label)
    {
        List<Showcase.FleetEntry> fleet;
        if (spec.Fleet is { } explicitFleet)
            fleet = ResolveFleetArg(new Dictionary<string, JsonElement> { ["fleet"] = explicitFleet }, "fleet", label, core)!;
        else if (showcase) fleet = Showcase.ComposeShowcaseFleetCount(core, spec.Count).ToList();
        else
        {
            var local = new Dictionary<string, JsonElement>(args) { ["size"] = JsonSerializer.SerializeToElement(spec.Count) };
            fleet = HomogeneousFleet(local, core, label);
        }
        if (fleet.Sum(e => e.Count) != spec.Count)
            throw new ArgumentException($"spawn_scenario: side {label} fleet counts must sum to count={spec.Count}");
        return fleet;
    }

    internal static void ValidateFinaleSides(ShowcaseFinale.Settings settings, IReadOnlyList<short> sides)
    {
        if (settings.Side == "both" && sides.Count != 2)
            throw new ArgumentException("spawn_scenario: finale_side both is a two-side alias; use all");
        if (short.TryParse(settings.Side, out short id) && !sides.Contains(id))
            throw new ArgumentException("spawn_scenario: finale_side must name a side in the actual mission");
    }
}
