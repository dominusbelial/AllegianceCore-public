using System.Globalization;

namespace Allegiance.SinglePlayer.Benchmark;

/// <summary>
/// P889 — the bounded, re-runnable machine calibration. It runs a short real
/// skirmish (spectator camera on the hottest fight) at the chosen size under a
/// few candidate sizings, scores each from client frame time + server tick +
/// bot decision rate, and saves the winner to this machine's sizing profile so
/// the server resolver uses it. Explicit config/env still wins.
/// </summary>
public sealed record SizingCalibrationCommand(
    SkirmishSize Size,
    double Seconds,
    string Output,
    bool ExitWhenComplete,
    bool SaveProfile,
    string[] Candidates)
{
    public const double DefaultSeconds = 40;
    /// <summary>Keep the whole calibration bounded: five candidates × 40 s of
    /// measured battle plus host startup is a few minutes, not an A/B batch.</summary>
    public const double MaxSeconds = 90;
    public const int MaxCandidates = 6;
    public static readonly string[] AllCandidates =
        ["resolver", "serial", "p887", "build+2", "pool", "bots-1"];

    /// <summary>The menu entry's default: the size the player has selected,
    /// bounded duration, save the profile, stay in the game afterwards.</summary>
    public static SizingCalibrationCommand DefaultFor(SkirmishSize size) =>
        new(size, DefaultSeconds, Path.Combine(PlayerConfigDirectory(), "sizing-calibration.json"),
            false, true, AllCandidates);

    public static SizingCalibrationCommand? Parse(string[] args)
    {
        int index = Array.IndexOf(args, "--calibrate");
        if (index < 0) return null;
        string sizeName = index + 1 < args.Length && !args[index + 1].StartsWith("--") ? args[++index] : "medium";
        var size = Enum.Parse<SkirmishSize>(sizeName, true);
        if (!Enum.IsDefined(size)) throw new ArgumentException("Unknown calibration size");
        double seconds = Number(args, "--calibrate-seconds", DefaultSeconds);
        if (!double.IsFinite(seconds) || seconds < 10 || seconds > MaxSeconds)
            throw new ArgumentException($"Calibration duration must be 10..{MaxSeconds:0} seconds");
        string output = Text(args, "--calibrate-output",
            Path.Combine(PlayerConfigDirectory(), "sizing-calibration.json"));
        var requested = Text(args, "--calibrate-candidates", string.Join(',', AllCandidates))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => AllCandidates.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Select(name => AllCandidates.First(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (requested.Length == 0) requested = ["resolver"];
        if (requested.Length > MaxCandidates) throw new ArgumentException("Too many calibration candidates");
        return new(size, seconds, output, args.Contains("--calibrate-exit"),
            !args.Contains("--calibrate-no-save"), requested);
    }

    private static double Number(string[] args, string flag, double fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == flag)
                return i + 1 < args.Length
                    ? double.Parse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture)
                    : throw new ArgumentException("Missing value for " + flag);
            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal))
                return double.Parse(args[i][(flag.Length + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture);
        }
        return fallback;
    }

    private static string Text(string[] args, string flag, string? fallback)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == flag) return i + 1 < args.Length ? args[i + 1] : throw new ArgumentException("Missing value for " + flag);
            if (args[i].StartsWith(flag + "=", StringComparison.Ordinal)) return args[i][(flag.Length + 1)..];
        }
        return fallback ?? throw new ArgumentException("Missing value for " + flag);
    }

    private static string PlayerConfigDirectory()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrEmpty(dir) ? "." : Path.Combine(dir, "AllegianceCore");
    }

    /// <summary>Strip the calibration flags before the ordinary client arg
    /// parser sees them (same pattern as the benchmark flags).</summary>
    public static string[] Strip(string[] args)
    {
        if (!args.Contains("--calibrate")) return args;
        var remaining = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--calibrate")
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--")) i++;
                continue;
            }
            if (a is "--calibrate-seconds" or "--calibrate-output" or "--calibrate-candidates") { i++; continue; }
            if (a is "--calibrate-exit" or "--calibrate-no-save") continue;
            if (a.StartsWith("--calibrate-seconds=", StringComparison.Ordinal)
                || a.StartsWith("--calibrate-output=", StringComparison.Ordinal)
                || a.StartsWith("--calibrate-candidates=", StringComparison.Ordinal)) continue;
            remaining.Add(a);
        }
        return remaining.ToArray();
    }
}

/// <summary>P889 — the calibration ranking function. Lower is better: each term
/// is normalised to the AIX-S/60 Hz budgets (frame p95 16.667 ms = one 60 Hz
/// frame, tick p95 50 ms = one 20 Hz tick, 20 Hz = the native bot pass rate),
/// so candidates rank without a machine-specific constant.</summary>
public static class SizingCalibrationScore
{
    public static double Cost(double frameP95, double tickP95, double decisionHz)
    {
        double frame = Math.Max(frameP95, 0.01) / 16.667;
        double tick = Math.Max(tickP95, 0.01) / 50.0;
        double bots = 20.0 / Math.Max(decisionHz, 0.25);
        return frame * tick * bots;
    }
}
