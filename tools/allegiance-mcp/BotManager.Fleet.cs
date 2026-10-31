using System.Text.Json;

namespace Allegiance.Tools.McpSpawner;

internal sealed record FleetSlot(long Scenario, string Key, short Side, string? Name, string Status);

internal sealed partial class BotManager
{
    private readonly Dictionary<(long, string), FleetSlot> _fleetSlots = new();

    // Keys are side position + ordinal, never a sector, hull, civ or old side id.
    internal SpawnBatch CreateScenarioBatch(IReadOnlyList<SpawnBatch.Slot> slots,
        IDictionary<string, JsonElement> args, short sideA, short sideB)
    {
        long scenario = args.TryGetValue("__fleet_id", out var id) ? id.GetInt64() : 0;
        HashSet<string>? allowed = args.TryGetValue("__fleet_slots", out var keys)
            ? keys.EnumerateArray().Select(k => k.GetString()!).ToHashSet() : null;
        var ordinals = new Dictionary<short, int>();
        var selected = new List<SpawnBatch.Slot>();
        foreach (var slot in slots)
        {
            int ordinal = ordinals.GetValueOrDefault(slot.Side);
            ordinals[slot.Side] = ordinal + 1;
            string key = $"{(slot.Side == sideA ? "a" : "b")}:{ordinal}";
            if (allowed is not null && !allowed.Contains(key)) continue;
            if (scenario != 0)
                lock (_gate) _fleetSlots[(scenario, key)] = new(scenario, key, slot.Side, null, "failed");
            selected.Add(slot with { Spawn = () =>
            {
                var result = slot.Spawn();
                if (scenario != 0)
                    lock (_gate) _fleetSlots[(scenario, key)] = new(scenario, key, slot.Side, result.Name, "active");
                return result;
            }});
        }
        return new(selected);
    }

    internal FleetSlot[] ScenarioSlots()
    {
        lock (_gate)
            return _fleetSlots.Values.Select(slot => slot.Name is null ? slot : slot with
            {
                Status = _bots.Any(b => b.Name == slot.Name && IsParticipating(b)) ? "active"
                    : _removedBots.LastOrDefault(b => b.Name == slot.Name)?.Reason ?? "connection lost"
            }).ToArray();
    }
}
