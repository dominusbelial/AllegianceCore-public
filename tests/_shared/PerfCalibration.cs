using System.Diagnostics;

namespace Allegiance.TestInfra;

/// <summary>
/// P744 — hardware-relative perf budgets for the live perf-guard tests.
///
/// WHY: the P706 dispatch guard, the P668/P677/P679 crowded-broadcast loopback,
/// and the P705 two-match tech-mode proof all used ABSOLUTE wall-clock budgets
/// (50 ms dispatch, 30 s transport drains, fixed Wait() deadlines). Those are
/// only meaningful on the machine where they were tuned: on the UM690 mini
/// runner (Ryzen 9 6900HX, Docker) the same work takes longer wall time, so
/// every run failed there while passing on this workstation.
///
/// HOW: <see cref="ReferenceWorkload"/> is a fixed, deterministic batch of
/// scalar-FP / vector maths plus strided array churn — the same flavour of work
/// the guarded dispatch / sim hot paths do. This helper times it in the SAME
/// run (and therefore the SAME process, config and machine) as the guarded
/// measurement and turns it into a machine speed factor
///     factor = measured_reference / nominal_reference
/// where nominal_reference is what that workload takes on the reference machine
/// (this workstation, where the pre-P744 budgets were tuned and validated).
/// A machine N× slower measures N× on the reference too, so its scaled budgets
/// are identical to the reference machine's — it neither false-fails on
/// hardware nor gets a free pass from a loose budget. A genuine algorithmic
/// regression (extra work per dispatch tick, slower sim progression, a slower
/// transport pipeline) raises only the guarded measurement, never this
/// reference, so it still trips the (scaled) budget.
///
/// CONFIG ANCHORING: the reference is ordinary scalar-FP C#, so the JIT
/// optimises it between Debug and Release exactly the same way it does the
/// guarded dispatch path. The nominal anchor is therefore config-specific
/// (<see cref="NominalReferenceMs"/> is selected by #if DEBUG) so factor ≈ 1.0
/// on the reference workstation in BOTH configs, and the scaled budget keeps
/// its meaning in the config it is measured in.
///
/// TEST-ONLY REGRESSION DEMO: callers read <see cref="SimulateRegressionFactor"/>
/// (env P744_SIMULATE_REGRESSION&gt;1) and inflate ONLY their guarded measurement
/// by it — never this reference — so the budget is proven to still catch a real
/// slowdown without disturbing the calibration. See docs/reports/P744_PORTABLE_PERF_TESTS.md.
/// </summary>
public static class PerfCalibration
{
    /// <summary>Repeats of the reference batch; the FASTEST (least contaminated
    /// by a GC/scheduler hiccup) is used as the measurement.</summary>
    private const int Samples = 7;

    /// <summary>
    /// Wall time (ms) of <see cref="ReferenceWorkload"/> on the reference
    /// machine — this workstation, 2026-10-17 — in the matching build config.
    /// Measured once with the reference exactly as coded below; do not edit the
    /// workload without re-measuring both anchors.
    /// </summary>
#if DEBUG
    public const double NominalReferenceMs = 3.770;  // measured Debug, workstation
#else
    public const double NominalReferenceMs = 1.885;  // measured Release, workstation
#endif

    private static readonly Lazy<double> LazyFactor =
        new(MeasureFactor, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Machine speed factor vs the reference machine; ≈1.0 on the
    /// tuning workstation, &gt;1 on a slower runner, &lt;1 on a faster one.</summary>
    public static double CpuSpeedFactor => LazyFactor.Value;

    /// <summary>Fastest measured reference batch time (ms), for transparency.</summary>
    public static double RawReferenceMs { get; private set; }

    /// <summary>Scale an absolute budget (any unit) that was tuned on the
    /// reference machine to this machine.</summary>
    public static double Scale(double referenceBudget) => referenceBudget * CpuSpeedFactor;

    /// <summary>Parsed once from env P744_SIMULATE_REGRESSION (&gt;1 inflates only
    /// the guarded measurement; 1 or unset = no-op). Never affects the reference.</summary>
    public static double SimulateRegressionFactor { get; } = ParseRegression();

    private static double ParseRegression()
    {
        string? v = Environment.GetEnvironmentVariable("P744_SIMULATE_REGRESSION");
        return double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out double f) && f > 0
            ? f : 1.0;
    }

    private static double MeasureFactor()
    {
        double best = double.MaxValue;
        for (int s = 0; s < Samples; s++)
        {
            ReferenceWorkload();                       // pre-pass so it is hot
            var sw = Stopwatch.StartNew();
            ReferenceWorkload();
            sw.Stop();
            if (sw.Elapsed.TotalMilliseconds < best) best = sw.Elapsed.TotalMilliseconds;
        }
        RawReferenceMs = best;
        return best / NominalReferenceMs;
    }

    private const int BufLen = 2048;
    private const int Mask = BufLen - 1;
    private const int Reps = 625000;
    private static readonly float[] _Buf = new float[BufLen];
    private static int _Sink;

    /// <summary>Fixed, deterministic batch of cross-product-style float maths +
    /// strided array churn (the flavour of the convex-hull support/probe and
    /// sim hot paths). Fixed work per call, allocation-light, resists JIT
    /// elimination via observable array stores + a sink XOR.</summary>
    public static void ReferenceWorkload()
    {
        var buf = _Buf;
        for (int i = 0; i < buf.Length; i++) buf[i] = i * 0.028125f;
        float ax = 987.654f, ay = -123.457f, az = 555.666f;
        for (int rep = 0; rep < Reps; rep++)
        {
            int i = (rep * 5021) & Mask;
            float x = buf[i], y = buf[(i + 751) & Mask], z = buf[(i + 613) & Mask];
            // Cross product (the shape of the hull support/probe maths).
            float cx = ay * z - az * y;
            float cy = az * x - ax * z;
            float cz = ax * y - ay * x;
            ax += cx * 0.9073f + x * 0.0211f;
            ay += cy * 0.8841f + y * 0.0163f;
            az += cz * 0.8617f + z * 0.0197f;
            if ((rep & 63) == 0) ax -= buf[(i + 467) & Mask] * 0.571f;
            buf[(rep * 5189) & Mask] = ax + ay + az;
        }
        _Sink ^= BitConverter.SingleToInt32Bits(ax)
               ^ BitConverter.SingleToInt32Bits(ay)
               ^ BitConverter.SingleToInt32Bits(az);
    }
}
