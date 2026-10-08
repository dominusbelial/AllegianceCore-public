namespace Allegiance.ClientCore.State;

using Allegiance.Core.Collision;

/// <summary>
/// P579 — COVERAGE-based LOD for the raymarched volumes (P573's policy inverted).
///
/// The raymarch cost of a volume is its on-screen pixel COUNT × step COUNT. The
/// P573 policy gave MORE steps to BIGGER on-screen volumes (Ultra = nested,
/// 255+ steps over ~1 M pixels per bubble → 3–4 fps up close). Here the step
/// count goes by screen COVERAGE the other way: <b>big on-screen → FEWER, longer
/// steps; small on-screen → more steps is fine</b> (a tiny volume costs little
/// fill even at high step count). The per-frame budget
/// (<see cref="VolumeBudget.AllocateLevels"/>) then caps the TOTAL pixel-steps
/// across every live volume, degrading the LARGEST-coverage (highest-cost)
/// volumes first.
///
/// Levels map to the compiled <c>Volumetric.fx</c> techniques; each step config
/// is a fixed, MojoShader-`rep`-friendly loop (&gt;255 steps use nested slices×255).
/// Pure + unit-testable.
/// </summary>
public static class VolumeLod
{
    public enum Level { Low = 0, Med = 1, High = 2, Ultra = 3 }

    /// <summary>Coverage thresholds (projected-diameter screen px) that pick each
    /// level. INVERTED vs P573: the largest on-screen volumes land on Low (fewest
    /// steps), the smallest on Ultra (most steps — cheap fill).</summary>
    public const float UltraMaxPx = 56f;    // ≤56px diameter → Ultra (510 steps)
    public const float HighMaxPx = 160f;    // ≤160px        → High  (255 steps)
    public const float MedMaxPx = 420f;     // ≤420px        → Med   (128 steps)
                                            // &gt;420px (fills the frame) → Low (64 steps)

    /// <summary>Pick a LOD from a unit's on-screen diameter in pixels. Monotonic
    /// in COVERAGE: bigger on-screen → cheaper per pixel (fewer steps).</summary>
    public static Level ForPixelDiameter(float diameterPx)
    {
        if (diameterPx < UltraMaxPx) return Level.Ultra;
        if (diameterPx < HighMaxPx) return Level.High;
        if (diameterPx < MedMaxPx) return Level.Med;
        return Level.Low;
    }

    /// <summary>The raymarch loop configuration for a level: outer slices × inner
    /// steps (each &lt;= 255 so MojoShader emits valid ps_3_0 reps).</summary>
    public static (int Slices, int Inner) StepsFor(Level level) => level switch
    {
        Level.Low   => (1, 64),    // 64  steps  — big coverage
        Level.Med   => (1, 128),   // 128 steps
        Level.High  => (1, 255),   // 255 steps
        _           => (2, 255),   // 510 steps — small coverage
    };

    /// <summary>Total marched steps for a level.</summary>
    public static int TotalSteps(Level level)
    {
        var (s, i) = StepsFor(level);
        return s * i;
    }

    /// <summary>The <c>Volumetric.fx</c> technique name for a level.</summary>
    public static string TechniqueName(Level level) => level switch
    {
        Level.Low   => "RaymarchLow",
        Level.Med   => "RaymarchMed",
        Level.High  => "RaymarchHigh",
        _           => "RaymarchUltra",
    };
}

/// <summary>
/// P579 — the cap AND the per-frame pixel/step budget on simultaneously-active
/// raymarched volumes. Pure + unit-testable.
/// <list type="bullet">
/// <item><b><see cref="MaxVolumes"/></b> (P573) caps how many sub-volumes are
/// raymarched at all; past it the FARTHEST from the camera are retired first
/// (<see cref="TrimNearest"/>).</item>
/// <item><b><see cref="MaxPixelSteps"/></b> (P579) caps the TOTAL per-frame
/// pixel-steps (projected pixel area × raymarch steps) across every kept volume.
/// When breached, <see cref="AllocateLevels"/> degrades the LARGEST-coverage
/// (highest pixel-step) volumes toward fewer steps first — the degradation
/// order the task requires ("degrade the nearest/largest first").</item>
/// </list>
/// <see cref="MaxPixelSteps"/> is tuned on the real DesktopGL GPU (Intel
/// Arrow-Lake-S / Mesa iris at 2560×1440); see the P579 report.
/// </summary>
public static class VolumeBudget
{
    /// <summary>Maximum on-screen sub-volumes that are raymarched at once.</summary>
    public const int MaxVolumes = 40;

    /// <summary>Per-frame pixel-step budget across all raymarched volumes.
    /// Nominal step counts are 64..510; at 2560×1440 a full-screen fighter blast
    /// (≈1.0 M px, 64 steps) ≈ 64 M pixel-steps alone, so the budget clips the
    /// largest near volumes hard and lets the far ones breathe. Value tuned on
    /// this GPU so a screen-filling blast + a handful of neighbours stays at or
    /// above 60 fps (see the P579 report).</summary>
    public const float MaxPixelSteps = 42_000_000f;

    /// <summary>
    /// Enforce the cap: keep the <see cref="MaxVolumes"/> units closest to
    /// <paramref name="eye"/>, dropping the rest (returned true when the input
    /// had to be trimmed — the caller logs the degradation once).
    /// </summary>
    public static List<(VolumetricSubUnit Unit, float DistSq)> TrimNearest(
        List<VolumetricSubUnit> units, Vector3 eye)
    {
        var ranked = new List<(VolumetricSubUnit, float)>(units.Count);
        foreach (var u in units)
        {
            float dx = u.Center.X - eye.X, dy = u.Center.Y - eye.Y, dz = u.Center.Z - eye.Z;
            ranked.Add((u, dx * dx + dy * dy + dz * dz));
        }
        if (ranked.Count > MaxVolumes)
        {
            ranked.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            ranked.RemoveRange(MaxVolumes, ranked.Count - MaxVolumes);
        }
        return ranked;
    }

    /// <summary>
    /// Allocate a raymarch level to each kept volume so the TOTAL per-frame
    /// pixel-steps (Σ projected area × steps) stays within <paramref name="budget"/>.
    /// <paramref name="volumes"/> is a parallel list of (projected diameter px,
    /// projected pixel AREA) per volume; each volume's NOMINAL level comes first
    /// from <see cref="VolumeLod.ForPixelDiameter"/>, then the LARGEST-coverage
    /// volume (highest nominal pixel-steps) is degraded one level at a time until
    /// the budget holds. Returns the effective level per volume (same order).
    /// </summary>
    public static VolumeLod.Level[] AllocateLevels(
        IReadOnlyList<(float DiameterPx, float AreaPx)> volumes, float budget)
    {
        int n = volumes.Count;
        var levels = new VolumeLod.Level[n];
        var costs = new float[n];
        float total = 0f;

        for (int i = 0; i < n; i++)
        {
            levels[i] = VolumeLod.ForPixelDiameter(volumes[i].DiameterPx);
            costs[i] = volumes[i].AreaPx * VolumeLod.TotalSteps(levels[i]);
            total += costs[i];
        }
        if (total <= budget) return levels;

        // Greedy: repeatedly drop the HIGHEST-cost volume that can still go lower.
        // Recompute its cost each time; loop until under budget or nothing left.
        for (int guard = 0; guard < n * 4 + 1; guard++)
        {
            if (total <= budget) break;
            int pick = -1;
            float pickCost = -1f;
            for (int i = 0; i < n; i++)
            {
                if (levels[i] == VolumeLod.Level.Low) continue;          // already cheapest
                float c = costs[i];
                if (c > pickCost) { pickCost = c; pick = i; }
            }
            if (pick < 0) break;                                         // all at Low
            total -= costs[pick];
            levels[pick] = levels[pick] - 1;                             // Level order = cost order
            costs[pick] = volumes[pick].AreaPx * VolumeLod.TotalSteps(levels[pick]);
            total += costs[pick];
        }
        return levels;
    }
}
