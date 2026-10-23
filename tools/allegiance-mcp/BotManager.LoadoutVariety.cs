using Allegiance.Core;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P796 — per-side loadout variety state. The purchase path
/// (<see cref="BotManager"/> LoadoutShop → <see cref="TechLoadoutSelector"/>)
/// asks this store for a deterministic <see cref="TechLoadoutSelector.VarietyState"/>
/// snapshot (least-recently-used hull/part counts + a per-side purchase
/// ordinal) and reports each varied selection back so the next purchase
/// rotates to the next legal option. One store per process — the harness
/// invariant is one BotManager per process (see the P636 snapshot note).
/// The store itself carries no civ names or ids: keys are real side/hull/part
/// ids from the live shop.
/// </summary>
internal static class LoadoutVariety
{
    private static readonly object Sync = new();
    private static readonly Dictionary<(short Side, short HullId), int> HullUse = new();
    private static readonly Dictionary<(short Side, short PartId), int> PartUse = new();
    private static readonly Dictionary<short, long> Ordinals = new();
    private static long _seed = DefaultSeed;

    /// <summary>Reproducible default run seed (the task id) — a run can override
    /// it via the spawn arg; identical seeds produce identical pure-selector
    /// choices for identical usage snapshots.</summary>
    internal const long DefaultSeed = 796;

    internal static long Seed { get { lock (Sync) return _seed; } }

    internal static void Configure(long seed)
    {
        lock (Sync) _seed = seed <= 0 ? DefaultSeed : seed;
    }

    /// <summary>Snapshot the side's usage and take the next deterministic
    /// ordinal for this purchase. Pure-selector inputs; the caller feeds the
    /// result straight into <see cref="TechLoadoutSelector.SelectVaried"/>.</summary>
    internal static TechLoadoutSelector.VarietyState Begin(short side)
    {
        lock (Sync)
        {
            long ord = Ordinals.GetValueOrDefault(side);
            Ordinals[side] = ord + 1;
            return new(side, _seed, ord,
                HullUse.Where(kv => kv.Key.Side == side).ToDictionary(kv => kv.Key.HullId, kv => kv.Value),
                PartUse.Where(kv => kv.Key.Side == side).ToDictionary(kv => kv.Key.PartId, kv => kv.Value));
        }
    }

    /// <summary>Record the hull and every part of an accepted varied loadout so
    /// the side's least-recently-used rotation spreads across all of them.</summary>
    internal static void Record(short side, TechLoadoutSelector.Selection selection)
    {
        lock (Sync)
        {
            var hullKey = (side, (short)selection.HullId);
            HullUse[hullKey] = HullUse.GetValueOrDefault(hullKey) + 1;
            foreach (var p in selection.Parts)
            {
                var partKey = (side, (short)p.PartId);
                PartUse[partKey] = PartUse.GetValueOrDefault(partKey) + 1;
            }
        }
    }

    /// <summary>Test isolation: clear all usage and ordinals.</summary>
    internal static void Reset()
    {
        lock (Sync)
        {
            HullUse.Clear();
            PartUse.Clear();
            Ordinals.Clear();
            _seed = DefaultSeed;
        }
    }
}
