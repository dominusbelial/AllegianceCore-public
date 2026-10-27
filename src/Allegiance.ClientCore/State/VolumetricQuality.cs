namespace Allegiance.ClientCore.State;

using Allegiance.Core.Collision;

/// <summary>
/// P579/P834 — COVERAGE-based LOD for the raymarched volumes (P573's policy inverted).
///
/// The raymarch cost of a volume is its on-screen pixel COUNT × step COUNT. The
/// P573 policy gave MORE steps to BIGGER on-screen volumes (Ultra = nested,
/// 255+ steps over ~1 M pixels per bubble → 3–4 fps up close). Here the step
/// count goes by screen COVERAGE the other way: <b>big on-screen → FEWER, longer
/// steps; small on-screen → more steps is fine</b> (a tiny volume costs little
/// fill even at high step count). The per-frame budget
/// (<see cref="VolumeBudget.AllocateLevels(IReadOnlyList{VolumeBudget.VolumeCost}, float)"/>)
/// then caps the TOTAL pixel-steps across every live volume, degrading the
/// LARGEST-coverage (highest-cost) volumes first.
///
/// P834 — the thresholds are <b>fractions of the viewport height</b>, not
/// absolute pixels: a volume that covers the same slice of the screen costs the
/// same RELATIVE fill at any resolution, and a 4K/5K2K frame no longer shifts
/// every volume into a cheaper level just because its diameter in pixels grew.
/// A <see cref="Level.Minimal"/> (32-step) level is the floor so a
/// screen-filling / camera-inside volume really can be made cheap ("fewer,
/// longer steps").
///
/// Levels map to the compiled <c>Volumetric.fx</c> techniques; each step config
/// is a fixed, MojoShader-`rep`-friendly loop (&gt;255 steps use nested slices×255).
/// Pure + unit-testable.
/// </summary>
public static class VolumeLod
{
    /// <summary>Raymarch step levels, ordered by COST (cheapest first) — the
    /// budget's degradation ladder relies on that order.</summary>
    public enum Level { Minimal = 0, Low = 1, Med = 2, High = 3, Ultra = 4 }

    /// <summary>Number of compile-time levels (the <c>Volumetric.fx</c>
    /// techniques and the renderer's per-level buckets).</summary>
    public const int LevelCount = 5;

    /// <summary>Viewport height the P579 pixel thresholds were tuned at. The
    /// fraction thresholds below are exactly those pixels ÷ this height, and
    /// <see cref="ForPixelDiameter"/> keeps the old absolute-pixel entry point
    /// (interpreted at this reference height) for callers/tests.</summary>
    public const int ReferenceHeight = 1440;

    /// <summary>Coverage thresholds as a FRACTION of the viewport height that
    /// pick each level. INVERTED vs P573: the largest on-screen volumes land on
    /// Low/Minimal (fewest steps), the smallest on Ultra (most steps — cheap
    /// fill). P834: expressed as screen fractions so the policy is the same at
    /// 1080p, 1440p, 4K and 5K2K.</summary>
    public const float UltraMaxFraction = 56f / ReferenceHeight;    // <3.9 % of height  → Ultra (510)
    public const float HighMaxFraction = 160f / ReferenceHeight;    // <11.1 %           → High  (255)
    public const float MedMaxFraction = 420f / ReferenceHeight;     // <29.2 %           → Med   (128)
                                                                    // <100 %            → Low   (64)
    public const float MinimalMinFraction = 1.0f;                   // ≥100 % of height  → Minimal (32)

    /// <summary>The P579 thresholds in 1440p pixels (kept for the report and for
    /// callers that still think in the reference resolution).</summary>
    public const float UltraMaxPx = UltraMaxFraction * ReferenceHeight;
    public const float HighMaxPx = HighMaxFraction * ReferenceHeight;
    public const float MedMaxPx = MedMaxFraction * ReferenceHeight;

    /// <summary>Pick a LOD from a unit's on-screen diameter as a FRACTION of the
    /// viewport height. Monotonic in COVERAGE: bigger on-screen → cheaper per
    /// pixel (fewer steps). A volume that fills the screen height (or more —
    /// the camera is inside it) gets the 32-step floor.</summary>
    public static Level ForDiameterFraction(float diameterFraction)
    {
        if (diameterFraction < UltraMaxFraction) return Level.Ultra;
        if (diameterFraction < HighMaxFraction) return Level.High;
        if (diameterFraction < MedMaxFraction) return Level.Med;
        if (diameterFraction < MinimalMinFraction) return Level.Low;
        return Level.Minimal;
    }

    /// <summary>Pick a LOD from a unit's on-screen diameter in pixels at the
    /// P579 reference height (1440p). Kept for the original P579 API/tests;
    /// GPU code goes through <see cref="ForDiameterFraction"/> so the choice is
    /// resolution-independent.</summary>
    public static Level ForPixelDiameter(float diameterPx) => ForDiameterFraction(diameterPx / ReferenceHeight);

    /// <summary>The raymarch loop configuration for a level: outer slices × inner
    /// steps (each &lt;= 255 so MojoShader emits valid ps_3_0 reps).</summary>
    public static (int Slices, int Inner) StepsFor(Level level) => level switch
    {
        Level.Minimal => (1, 48),    // 48  steps — screen-filling / camera inside
        Level.Low     => (1, 64),    // 64  steps — big coverage
        Level.Med     => (1, 128),   // 128 steps
        Level.High    => (1, 255),   // 255 steps
        _             => (2, 255),   // 510 steps — small coverage
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
        Level.Minimal => "RaymarchMinimal",
        Level.Low     => "RaymarchLow",
        Level.Med     => "RaymarchMed",
        Level.High    => "RaymarchHigh",
        _             => "RaymarchUltra",
    };
}

/// <summary>
/// P834 — the resolution a volume's raymarch is rendered at, chosen by COVERAGE
/// (a fraction of the viewport height). P579 only had full-res and a HALF-res
/// pass for near volumes; half of a 4K/5K2K frame is still &gt; 1080p, so a
/// screen-filling blast there had no cheap level left. Quarter-res is the next
/// rung (16× less fill than full-res), composited up additively with the same
/// upsample path as the half-res pass.
/// </summary>
public enum VolumeRenderScale
{
    /// <summary>Native viewport resolution (small / far volumes).</summary>
    Full = 1,
    /// <summary>Half width × half height → ¼ the marched pixels.</summary>
    Half = 2,
    /// <summary>Quarter width × quarter height → 1/16 the marched pixels.</summary>
    Quarter = 4,
}

/// <summary>P834 — coverage thresholds that pick the render scale. Pure.</summary>
public static class VolumeScreenPolicy
{
    /// <summary>P579's half-res threshold (240 px at the 1440p reference) as a
    /// fraction of viewport height — so the same on-screen slice gets the same
    /// treatment at every resolution.</summary>
    public const float HalfResMinFraction = 240f / VolumeLod.ReferenceHeight;   // 16.7 %

    /// <summary>Screen-filling volumes go to quarter-res. 75 % of the viewport
    /// height is where the B50 measurements show half-res of a 4K/5K2K blast
    /// costing more than the whole rest of the frame (see the P834 report).</summary>
    public const float QuarterResMinFraction = 0.75f;

    /// <summary>Pick the render scale from a volume's projected diameter as a
    /// fraction of the viewport height.</summary>
    public static VolumeRenderScale ScaleForDiameterFraction(float diameterFraction)
        => ScaleForDiameterFraction(diameterFraction, HalfResMinFraction);

    /// <summary>Same, with an explicit half-res threshold fraction (the renderer
    /// honours the <c>ALLEGIANCE_VOL_HALFRESPX</c> proof override through it).</summary>
    public static VolumeRenderScale ScaleForDiameterFraction(float diameterFraction, float halfResMinFraction)
    {
        if (diameterFraction >= QuarterResMinFraction) return VolumeRenderScale.Quarter;
        if (diameterFraction >= halfResMinFraction) return VolumeRenderScale.Half;
        return VolumeRenderScale.Full;
    }

    /// <summary>Multiplier that turns a FULL-res pixel area into the marched area
    /// at <paramref name="scale"/> (¼ for half, 1/16 for quarter).</summary>
    public static float AreaFactor(VolumeRenderScale scale) => 1f / ((int)scale * (int)scale);

    /// <summary>The rectangle's pixel count at the given scale (for diagnostics).</summary>
    public static float PixelsAtScale(VolumeRenderScale scale, int width, int height)
        => width * (float)height * AreaFactor(scale);
}

/// <summary>
/// P579/P834 — the cap AND the per-frame pixel/step budget on simultaneously-active
/// raymarched volumes. Pure + unit-testable.
/// <list type="bullet">
/// <item><b><see cref="MaxVolumes"/></b> (P573) caps how many sub-volumes are
/// raymarched at all; past it the FARTHEST from the camera are retired first
/// (<see cref="TrimNearest"/>).</item>
/// <item><b><see cref="MaxPixelStepsFor"/></b> (P579, re-tuned P834) caps the
/// TOTAL per-frame pixel-steps (projected pixel area × raymarch steps, at the
/// volume's own render scale) across every kept volume. When breached,
/// <see cref="AllocateLevels(IReadOnlyList{VolumeCost}, float)"/> degrades the
/// LARGEST-coverage (highest pixel-step) volumes toward fewer steps first — the
/// degradation order the task requires ("degrade the nearest/largest first").</item>
/// </list>
/// <para><b>P834 resolution awareness.</b> There are two independent fill-rate
/// levers: how many pixels a volume marches (<see cref="VolumeRenderScale"/>,
/// picked by screen coverage) and how many steps per pixel (this budget). The
/// budget is expressed per SCREEN PIXEL so a 5K2K frame — 3× the pixels of the
/// 1440p frame P579 tuned on — gets a proportionally larger budget instead of
/// silently being unable to honour it. The rate is measured, not guessed: on
/// this workstation's Intel Arc Pro B50 the Low raymarch sustains ≈ 24.5 G
/// pixel-steps/s (measured, see the P834 report), so
/// <see cref="PixelStepsPerViewportPixel"/> = 18 spends ≈ 8 ms at 5K2K
/// (18 × 11.06 M = 199 M pixel-steps) leaving the rest of the 16.7 ms frame to
/// the scene — the stateable "screen-filling 4K/5K2K blast stays ≥ 60 fps".
/// <see cref="MaxPixelSteps"/> (the P579 1440p absolute value) is kept as the
/// floor so small viewports do not get an overly strict budget.</para>
/// </summary>
public static class VolumeBudget
{
    /// <summary>Maximum on-screen sub-volumes that are raymarched at once.</summary>
    public const int MaxVolumes = 40;

    /// <summary>P579's per-frame pixel-step budget at the 1440p reference
    /// viewport (42 M, tuned on the old Arrow-Lake iGPU). Kept as the FLOOR of
    /// <see cref="MaxPixelStepsFor"/> so ≤1440p viewports keep at least the
    /// documented behaviour; the resolution-aware value takes over above it.</summary>
    public const float MaxPixelSteps = 42_000_000f;

    /// <summary>P834 — per-frame pixel-steps allowed per SCREEN pixel. Measured
    /// on the Intel Arc Pro B50 (xe/Mesa 26) at ≈24.5 G pixel-steps/s; 18
    /// pixel-steps per screen pixel ≈ 8 ms of the 16.7 ms frame at any
    /// resolution, which is the volumetric share of a screen-filling 4K/5K2K
    /// blast. A slower GPU is covered by the adaptive
    /// <see cref="VolumeBudgetController"/> (it scales this budget down from
    /// measured frame time).</summary>
    public const float PixelStepsPerViewportPixel = 18f;

    /// <summary>The per-frame pixel-step budget for a viewport: the measured
    /// per-screen-pixel rate × the viewport's pixels, never below the P579
    /// 1440p absolute value. Scale by viewport pixels = the task's
    /// "resolution-aware budget".</summary>
    public static float MaxPixelStepsFor(int width, int height)
    {
        if (width <= 0 || height <= 0) return MaxPixelSteps;
        return Math.Max(MaxPixelSteps, PixelStepsPerViewportPixel * width * height);
    }

    /// <summary>One volume's cost inputs. <paramref name="DiameterFraction"/> is
    /// its projected diameter / viewport height (the resolution-independent
    /// coverage), <paramref name="AreaPx"/> its projected area already CLAMPED to
    /// the viewport (a volume can never cover more pixels than the frame), and
    /// <paramref name="Scale"/> the render scale chosen from coverage.</summary>
    public readonly record struct VolumeCost(float DiameterFraction, float AreaPx, VolumeRenderScale Scale = VolumeRenderScale.Full);

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

    /// <summary>P579 entry point: (projected diameter px, projected area px²) at
    /// the 1440p reference height, full-res. Kept so the P579 model tests and
    /// any resolution-agnostic caller keep working unchanged.</summary>
    public static VolumeLod.Level[] AllocateLevels(
        IReadOnlyList<(float DiameterPx, float AreaPx)> volumes, float budget)
    {
        int n = volumes.Count;
        var costs = new VolumeCost[n];
        for (int i = 0; i < n; i++)
            costs[i] = new VolumeCost(volumes[i].DiameterPx / VolumeLod.ReferenceHeight, volumes[i].AreaPx);
        return AllocateLevels(costs, budget);
    }

    /// <summary>
    /// Allocate a raymarch level to each kept volume so the TOTAL per-frame
    /// pixel-steps (Σ area/scale² × steps) stays within <paramref name="budget"/>.
    /// Each volume's NOMINAL level comes first from
    /// <see cref="VolumeLod.ForDiameterFraction"/>; then the LARGEST-coverage
    /// volume (highest nominal pixel-steps) is degraded one level at a time
    /// until the budget holds, down to the <see cref="VolumeLod.Level.Minimal"/>
    /// floor. Returns the effective level per volume (same order).
    /// </summary>
    public static VolumeLod.Level[] AllocateLevels(IReadOnlyList<VolumeCost> volumes, float budget)
    {
        int n = volumes.Count;
        var levels = new VolumeLod.Level[n];
        var costs = new float[n];
        float total = 0f;

        for (int i = 0; i < n; i++)
        {
            levels[i] = VolumeLod.ForDiameterFraction(volumes[i].DiameterFraction);
            costs[i] = MarchedArea(volumes[i]) * VolumeLod.TotalSteps(levels[i]);
            total += costs[i];
        }
        if (total <= budget) return levels;

        // Greedy: repeatedly drop the HIGHEST-cost volume that can still go lower.
        // Recompute its cost each time; loop until under budget or nothing left.
        for (int guard = 0; guard < n * VolumeLod.LevelCount + 1; guard++)
        {
            if (total <= budget) break;
            int pick = -1;
            float pickCost = -1f;
            for (int i = 0; i < n; i++)
            {
                if (levels[i] == VolumeLod.Level.Minimal) continue;      // already cheapest
                float c = costs[i];
                if (c > pickCost) { pickCost = c; pick = i; }
            }
            if (pick < 0) break;                                         // all at Minimal
            total -= costs[pick];
            levels[pick] = levels[pick] - 1;                             // Level order = cost order
            costs[pick] = MarchedArea(volumes[pick]) * VolumeLod.TotalSteps(levels[pick]);
            total += costs[pick];
        }
        return levels;
    }

    /// <summary>A volume's projected pixel area at its render scale — the pixels
    /// the raymarch actually shades.</summary>
    public static float MarchedArea(VolumeCost v) => v.AreaPx * VolumeScreenPolicy.AreaFactor(v.Scale);

    /// <summary>Total nominal pixel-steps of an allocation (diagnostics/tests:
    /// the harness logs it next to the measured GPU time).</summary>
    public static float TotalPixelSteps(IReadOnlyList<VolumeCost> volumes, IReadOnlyList<VolumeLod.Level> levels)
    {
        float total = 0f;
        for (int i = 0; i < volumes.Count && i < levels.Count; i++)
            total += MarchedArea(volumes[i]) * VolumeLod.TotalSteps(levels[i]);
        return total;
    }
}

/// <summary>
/// P834 — GPU-agnostic adaptive safety net for the volumetric fill budget.
///
/// <see cref="VolumeBudget.PixelStepsPerViewportPixel"/> is tuned on one GPU
/// (this workstation's Arc Pro B50). A slower GPU, a heavier scene or an OS
/// compositor with less headroom must not turn the volumetric tier into a 20 fps
/// slideshow, and the budget alone cannot help when a single screen-filling
/// volume already costs more than the budget. So the renderer multiplies its
/// budget by a bounded, smoothly-adapting scale driven purely by MEASURED frame
/// time (no GPU queries, no vendor checks — "adapt from measured frame time"):
///
/// <list type="bullet">
/// <item>Sustained frame time above <see cref="OverBudgetRatio"/> × target →
/// lower <see cref="Scale"/> by <see cref="StepDown"/> per window.</item>
/// <item>Comfortably below <see cref="UnderBudgetRatio"/> × target → raise it by
/// <see cref="StepUp"/> per window (slower than the drop, so it settles).</item>
/// <item>Clamped to [<see cref="MinScale"/>, 1]; never grows past the
/// measured-on-B50 value and never falls below a quarter of it.</item>
/// </list>
///
/// A 60 Hz vsync lock sits at 16.7 ms, i.e. <see cref="OverBudgetRatio"/> ×
/// target ≈ 19.2 ms, so a frame that HOLDS vsync never triggers a drop (the
/// P579 lesson: the 80 ms tier fallback only ever fired on genuine overload).
/// Pure + unit-testable (the renderer just calls <see cref="Observe(double)"/>).
/// </summary>
public sealed class VolumeBudgetController
{
    /// <summary>Target frame time (60 fps).</summary>
    public const double TargetFrameMs = 1000.0 / 60.0;

    /// <summary>A window's average above this fraction of the target counts as
    /// over budget (1.15 → 19.2 ms at 60 fps; vsync-locked frames are ~16.7 ms).</summary>
    public const double OverBudgetRatio = 1.15;

    /// <summary>Below this fraction of the target the controller recovers headroom
    /// (0.85 → 14.2 ms; a vsync-locked 60 fps frame is NOT "under").</summary>
    public const double UnderBudgetRatio = 0.85;

    /// <summary>Bounded adaptation floor: a quarter of the B50-tuned budget.</summary>
    public const float MinScale = 0.25f;

    /// <summary>Per-window decrement/increment of the budget scale (bounded AND
    /// smooth: 1.0 → 0.25 takes ~13 windows ≈ 3.2 s; recovery is slower).</summary>
    public const float StepDown = 0.06f;
    public const float StepUp = 0.02f;

    /// <summary>Averaging window in seconds.</summary>
    public const double WindowSeconds = 0.25;

    private double _sumMs, _windowSeconds;
    private int _windowFrames;

    /// <summary>Current multiplier applied to the per-viewport budget, in
    /// [<see cref="MinScale"/>, 1].</summary>
    public float Scale { get; private set; } = 1f;

    /// <summary>Number of windows that adjusted the scale (diagnostics/tests).</summary>
    public int Adjustments { get; private set; }

    /// <summary>Feed one frame's measured wall/frame time (ms). Returns true when
    /// the scale changed on this call.</summary>
    public bool Observe(double frameMs)
    {
        if (!double.IsFinite(frameMs) || frameMs <= 0) return false;
        _sumMs += frameMs;
        _windowFrames++;
        _windowSeconds += frameMs / 1000.0;
        if (_windowSeconds < WindowSeconds) return false;

        double avg = _sumMs / Math.Max(1, _windowFrames);
        _sumMs = 0; _windowSeconds = 0; _windowFrames = 0;
        return ApplyWindow(avg);
    }

    /// <summary>Apply one averaging window directly (ms) — the pure core used by
    /// <see cref="Observe"/> and by tests.</summary>
    public bool ApplyWindow(double averageFrameMs)
    {
        if (!double.IsFinite(averageFrameMs) || averageFrameMs <= 0) return false;
        float before = Scale;
        if (averageFrameMs > TargetFrameMs * OverBudgetRatio)
            Scale = Math.Max(MinScale, Scale - StepDown);
        else if (averageFrameMs < TargetFrameMs * UnderBudgetRatio)
            Scale = Math.Min(1f, Scale + StepUp);
        if (Scale == before) return false;
        Adjustments++;
        return true;
    }

    /// <summary>Reset to the B50-tuned value (diagnostics/tests).</summary>
    public void Reset() { Scale = 1f; _sumMs = 0; _windowSeconds = 0; _windowFrames = 0; }
}
