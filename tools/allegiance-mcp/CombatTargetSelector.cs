using System.Collections.Generic;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P622 — the coordinated combat target CHOICE for a bot, extracted so it is
/// pure and unit-testable (the P610/P558 decision-helper pattern). It decides
/// WHICH different-side ship a bot should engage from a set of co-located,
/// visible candidates, honouring:
///   • an ENGAGER CAP per target by hull class (a lone miner gets a small
///     detachment, not the whole fleet — the anti-swarm that P622 exists for);
///   • THREAT PRIORITY: ships attacking us / heading for our stations first,
///     then combat ships, then miners/drones — a lone miner, when no enemy
///     combat ship is present, gets its small detachment and the REST HOLD
///     (the selector returns <see cref="BotTargetChoice.Holding"/>) instead of
///     swarming.
/// The P431 COMMANDED ship-target is not decided here: the caller hands that
/// to <see cref="BotManager"/>'s ordered path, which always wins.
///
/// NATIVE FIDELITY: native wingmen pick a target via nearest-different-side
/// (<c>FindTargetShip(..., c_ttEnemy|c_ttNearest)</c>, shipIGC.cpp:2030-2057)
/// with NO anti-swarm cap and NO target-value tiers — native disperses by HUMAN
/// wingman/commander discipline, which a test-harness bot population has no
/// commander for. So there is nothing to port for the cap/tiers; this is a
/// MODERN DESIGN for our bots (see "Native behaviour classified" in
/// P622_BOT_TARGET_SPREADING.md). The nearest-enemy fallback WITHIN a tier is
/// native's rule (the P558/native attack law it feeds is unchanged).
///
/// Modern alternative considered: a full multi-agent task-assignment (Hungarian
/// algorithm / auction) gives optimal spreading, but for a 200v200 harness a
/// greedy per-tick least-engaged pick is O(n²) worst case, deterministic,
/// lock-free in the single-threaded Loop, and visibly reaches the same
/// steady-state (even coverage, no over-pile). The Hungarian optimum was not
/// worth the O(n³) cost and tuning sensitivity here.
/// </summary>
public static class CombatTargetSelector
{
    // ── Engager caps per target hull class (P622 §Scope 2) ────────────────
    // A fighter gets 3-4 engagers, a drone/miner 1-2, bombers/capital more.
    // These are the anti-swarm bounds: a bot never piles onto a target that
    // already has `Cap` engagers assigned in the current pass.
    public const int CapMinerDrone = 2;
    public const int CapFighter = 3;
    public const int CapBomber = 4;
    public const int CapCapital = 6;

    /// <summary>The engager cap for a target hull class.</summary>
    public static int CapFor(BotTargetClass c) => c switch
    {
        BotTargetClass.MinerDrone => CapMinerDrone,
        BotTargetClass.Bomber => CapBomber,
        BotTargetClass.Capital => CapCapital,
        _ => CapFighter,
    };

    /// <summary>Classify a real hull into a combat-target class for P622's
    /// cap/tier decision. Native cues: a miner/builder/pod/drone is
    /// non-combat (c_habmMiner igc.h:740, c_habmBuilder :741, c_habmLifepod
    /// :728; P470 drone hulls have GroupId == -1); a bomber carries
    /// c_habmThreatToStation (igc.h:5390, native's own bomber classifier); a
    /// capital carries c_habmCarrier or is a heavy/gunship/destroyer name.
    /// Everything else that is a combat ship is a Fighter.</summary>
    public static BotTargetClass ClassifyTarget(DataHullTypeIGC h)
    {
        if (h.HasCapability(DataHullTypeIGC.HabmMiner)
            || h.HasCapability(DataHullTypeIGC.HabmBuilder)
            || h.HasCapability(DataHullTypeIGC.HabmLifepod)
            || h.GroupId < 0)                                   // P470 drone hull
            return BotTargetClass.MinerDrone;
        string n = h.Name;
        if (n.Contains("bomber", System.StringComparison.OrdinalIgnoreCase)
            || h.HasCapability(DataHullTypeIGC.HabmThreatToStation))
            return BotTargetClass.Bomber;
        if (n.Contains("heavy", System.StringComparison.OrdinalIgnoreCase)
            || n.Contains("gunship", System.StringComparison.OrdinalIgnoreCase)
            || n.Contains("capital", System.StringComparison.OrdinalIgnoreCase)
            || n.Contains("destroyer", System.StringComparison.OrdinalIgnoreCase)
            || h.HasCapability(DataHullTypeIGC.HabmCarrier))
            return BotTargetClass.Capital;
        return BotTargetClass.Fighter;
    }

    /// <summary>The combat-target class of a ship hull id, resolved against the
    /// representative core. Resolves an unknown/unmapped hull to
    /// <see cref="BotTargetClass.Fighter"/> (safe default).</summary>
    public static BotTargetClass ClassifyTargetId(short hullId)
    {
        var core = Catalog.RepresentativeCore();
        return core.HullTypes.TryGetValue(hullId, out var h)
            ? ClassifyTarget(h)
            : BotTargetClass.Fighter;
    }

    /// <summary>The classes a ship takes to be a combat threat to this side
    /// (bomber/capital can threaten stations per c_habmThreatToStation).</summary>
    public static bool IsStationThreatening(BotTargetClass c) =>
        c == BotTargetClass.Bomber || c == BotTargetClass.Capital;

    // ── Pure selection ────────────────────────────────────────────────────

    /// <summary>
    /// P622 — pick the best target for one bot. <see langword="pure"/>/static so
    /// it is unit-provable with synthetic geometry (no server). Callers pre-filter
    /// <paramref name="candidates"/> to DIFFERENT-side, co-located (same cluster),
    /// VISIBLE ships. Decision (documented in the P622 report):
    /// <list type="number">
    ///   <item><b>Threats</b> first — any candidate flagged
    ///   <see cref="BotTargetCandidate.IsThreat"/> (attacking us / heading for our
    ///   stations).</item>
    ///   <item>then <b>combat ships</b> (Fighter/Bomber/Capital);</item>
    ///   <item>then <b>miners/drones</b> — but ONLY when no enemy combat ship is
    ///   in the cluster (the lone-miner regime): the target's cap admits a small
    ///   detachment and every other bot returns <see cref="BotTargetChoice.Holding"/>
    ///   (loiter at the staging point) instead of swarming.</item>
    /// </list>
    /// Within a tier it picks the LEAST-ENGAGED eligible candidate (fewest current
    /// engagers from <see cref="BotSelectionContext.EngagerCounts"/>), tie-broken
    /// by NEAREST — so bots spread across distinct targets instead of piling. A
    /// candidate whose cap is already full in this pass is skipped, falling
    /// through to the next tier; if nothing is eligible the bot HOLDS.
    /// </summary>
    public static BotTargetChoice Select(
        IReadOnlyList<BotTargetCandidate> candidates,
        BotSelectionContext ctx)
    {
        if (candidates.Count == 0)
            return BotTargetChoice.Holding();

        // Lone-miner regime: no enemy COMBAT ship in the cluster → only a
        // miner/drone detachment engages; the rest hold. With combat ships
        // present, miners/drones are NOT eligible targets (strict priority —
        // we do not farm a miner while the enemy fleet is fighting us).
        bool hasCombat = false;
        foreach (var c in candidates)
            if (c.Class != BotTargetClass.MinerDrone) { hasCombat = true; break; }

        // Tier pools (in priority order).
        var threats = new List<BotTargetCandidate>();
        var combat = new List<BotTargetCandidate>();
        var miners = new List<BotTargetCandidate>();

        foreach (var c in candidates)
        {
            if (c.IsThreat) { threats.Add(c); continue; }
            if (c.Class != BotTargetClass.MinerDrone) { combat.Add(c); continue; }
            if (!hasCombat) miners.Add(c);   // miners only eligible in the lone regime
        }

        var pick = Pick(threats, ctx);
        if (pick is { } th) return th;
        pick = Pick(combat, ctx);
        if (pick is { } cm) return cm;
        pick = Pick(miners, ctx);
        if (pick is { } mn) return mn;

        return BotTargetChoice.Holding();
    }

    /// <summary>Among <paramref name="pool"/> choose the LEAST-ENGAGED eligible
    /// candidate (engager count &lt; its class cap), tie-breaking by nearest
    /// distance. null when the pool is empty or every member is over-cap.</summary>
    private static BotTargetChoice? Pick(List<BotTargetCandidate> pool, BotSelectionContext ctx)
    {
        BotTargetCandidate? best = null;
        int bestEngagers = int.MaxValue;
        float bestD = float.MaxValue;
        foreach (var c in pool)
        {
            int eng = ctx.EngagerCounts.TryGetValue(c.Id, out var e) ? e : 0;
            if (eng >= CapFor(c.Class)) continue;      // over-cap → not eligible
            if (eng < bestEngagers || (eng == bestEngagers && c.Distance < bestD))
            {
                best = c; bestEngagers = eng; bestD = c.Distance;
            }
        }
        return best is { } b
            ? new BotTargetChoice(b.Id, false, b.Class)
            : null;
    }

    // ── P622 §Scope 4 — engagement approach spacing ───────────────────────

    /// <summary>
    /// P622 — a deterministic PER-ENGAGER lateral offset so N bots closing on
    /// the SAME target fan out (laterally in the world horizontal plane) instead
    /// of converging into one column and grinding against the rock/asteroid the
    /// target sits on. Mirrors P610's <c>EntryAimPoint</c> idea (deterministic
    /// per-ship disc spread) but for combat approach. The offset is a rotation
    /// of the line-of-sight vector's horizontal projection by an angle that
    /// depends on the engager's 1-based rank, then scaled to
    /// <paramref name="magnitude"/> units, added to the target position. Pure +
    /// unit-tested. Zero for a solo engager (rank 1 ⇒ no offset).
    /// </summary>
    public static Vector3 EngagementOrbitOffset(int engagerRank, Vector3 los, float magnitude)
    {
        if (engagerRank <= 1 || magnitude <= 0f) return Vector3.Zero;
        // Horizontal (XZ) projection of the line of sight.
        float lx = los.X, lz = los.Z;
        float len = MathF.Sqrt(lx * lx + lz * lz);
        if (len < 1e-6f) return Vector3.Zero;
        // A perpendicular to the LOS (swap + negate), rotated around vertical by
        // a rank-dependent slice so engagers sit on distinct azimuths around the
        // target instead of converging on one column.
        float px = (-lz / len);
        float pz = (lx / len);
        float ang = (engagerRank - 1) * (MathF.PI / 3f);   // 60° slices per rank
        float cos = MathF.Cos(ang), sin = MathF.Sin(ang);
        float rx = px * cos - pz * sin;
        float rz = px * sin + pz * cos;
        return new Vector3(rx * magnitude, 0f, rz * magnitude);
    }
}

/// <summary>The combat-target class of a hull for P622's cap/tier decision.</summary>
public enum BotTargetClass
{
    /// <summary>Non-combat targets: miners, builders, lifepods, drone-only hulls.
    /// Lowest priority (only engaged as a small detachment in the lone regime)
    /// and the tightest engager cap.</summary>
    MinerDrone,
    /// <summary>Fighters/interceptors/scouts/gunships — the normal combat ship.</summary>
    Fighter,
    /// <summary>Bombers (c_habmThreatToStation) — higher value, more engagers.</summary>
    Bomber,
    /// <summary>Capitals/carriers/heavy gunships/destroyers — most engagers.</summary>
    Capital,
}

/// <summary>One co-located, visible, different-side candidate target for a bot
/// to potentially engage.</summary>
public readonly record struct BotTargetCandidate(
    short Id, Vector3 Position, BotTargetClass Class, bool IsThreat)
{
    /// <summary>Straight-line distance from the selecting bot (used as the
    /// tie-break).</summary>
    public float Distance { get; init; }
}

/// <summary>The context a bot's target choice is made in (single-threaded
/// per-tick coordination state).</summary>
public readonly record struct BotSelectionContext(
    short SelfShipId,
    short MyCluster,
    int TeammatesInCluster,
    IReadOnlyDictionary<short, int> EngagerCounts);

/// <summary>The outcome of a bot's target choice.</summary>
public readonly record struct BotTargetChoice(short? TargetId, bool Hold, BotTargetClass ChosenClass)
{
    public static BotTargetChoice Holding() => new(null, true, BotTargetClass.MinerDrone);
}
