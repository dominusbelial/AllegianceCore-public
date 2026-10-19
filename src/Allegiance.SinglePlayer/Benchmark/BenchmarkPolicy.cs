namespace Allegiance.SinglePlayer.Benchmark;

public static class BenchmarkPolicy
{
    public static string Bottleneck(BenchmarkRun r)
    {
        double botCores = r.Workers.Sum(w => w.CpuMs) / Math.Max(1, r.WallSeconds * 1000);
        if (r.ServerTickMs?.P95 >= 50 || botCores > Math.Max(1, Environment.ProcessorCount - 2) * .6) return "CPU-bound by bots";
        if (r.GpuMs is { } gpu && gpu > 12 && gpu > (r.CpuMs ?? 0) * 1.1) return "GPU-bound";
        if (r.CpuMs > 16.667) return "CPU-bound by client";
        if (r.FpsOnePercentLow is < 60) return "Frame-time spikes; CPU/GPU means within budget";
        return r.Rendered ? (r.GpuMs is null ? "GPU timing unavailable; CPU/presentation limit unresolved" : "Within measured budget")
            : "Headless: graphics unmeasured";
    }
    public static bool MeetsTarget(BenchmarkRun r) => r.Rendered && r.FpsOnePercentLow >= 60
        && (r.ServerTickMs is null || r.ServerTickMs is { Count: > 0, P95: < 50 });
    public static BenchmarkRecommendation Recommend(BenchmarkRun r, SkirmishSize size, int width = 1280, int height = 720)
    {
        string bottleneck = Bottleneck(r);
        bool bots = bottleneck == "CPU-bound by bots";
        bool gpu = bottleneck == "GPU-bound";
        bool client = bottleneck == "CPU-bound by client" || bottleneck.StartsWith("Frame-time spikes", StringComparison.Ordinal);
        var chosen = bots ? (SkirmishSize)Math.Max(0, (int)size - 1) : size;
        string split = $"Measured CPU {r.CpuMs?.ToString("F2") ?? "unavailable"} ms / GPU {r.GpuMs?.ToString("F2") ?? "unavailable"} ms at {width}x{height}";
        var result = new BenchmarkRecommendation
        {
            BattleSize = chosen, Difficulty = bots ? SkirmishDifficulty.Easy : SkirmishDifficulty.Normal,
            Fsr = gpu, FsrPreset = gpu && width >= 2560 ? 2 : 1, Bloom = false,
            HdPbr = !gpu, HdTextures = !gpu, EffectsDensity = gpu || client ? .5f : 1,
            ViewDistance = client ? 4500 : 6000
        };
        return result with { Reasons = [
            new("Battle size", chosen.ToString(), bots ? $"Bots consume {r.Workers.Sum(w => w.CpuMs) / Math.Max(.001, r.WallSeconds):F0} CPU ms/s; server tick p95 {r.ServerTickMs?.P95:F2} ms; reduce one size" : r.BotCount == 0 ? "No live bots measured; provisional size, validate with skirmish" : "Measured server and bot budget; retain tested size"),
            new("Bot difficulty", result.Difficulty.ToString(), bots ? "Easy reduces combat pressure; fleet reduction is the CPU remedy" : r.BotCount == 0 ? "No live bots measured; Normal is a provisional default" : "Normal preserves the tested combat policy"),
            new("Research speed", "1x", "Keep normal economy pacing; acceleration increases concurrent fleet activity"),
            new("Spectator default", "On", "Use the measured scripted spectator workload (SP-5 preference)"),
            new("Resolution / FSR", result.Fsr ? (result.FsrPreset == 2 ? "Balanced (59%)" : "Quality (67%)") : "Native (100%)", split),
            new("Bloom", "Off", "Reserve GPU headroom for combat effects"),
            new("HD PBR", result.HdPbr ? "On" : "Off", split),
            new("Texture quality", result.HdTextures ? "HD" : "Native", gpu ? "Reduce HD residency and sampling cost" : "Retain HD textures within measured budget"),
            new("Frame cap / vsync", "60 / Off", "Target 60 FPS; avoid presentation wait hiding available headroom"),
            new("Effects density", result.EffectsDensity.ToString("F1", System.Globalization.CultureInfo.InvariantCulture), split),
            new("View distance", result.ViewDistance.ToString(System.Globalization.CultureInfo.InvariantCulture), client ? "Reduce client draw/visibility work" : "Retain tested 6000-unit view")
        ] };
    }
    public static BenchmarkRecommendation StepDown(BenchmarkRecommendation r)
    {
        var lower = r with
        {
            BattleSize = (SkirmishSize)Math.Max(0, (int)r.BattleSize - 1), Fsr = true, FsrPreset = Math.Min(3, r.FsrPreset + 1),
            Bloom = false, HdPbr = false, HdTextures = false, EffectsDensity = .25f, ViewDistance = 3500
        };
        string preset = lower.FsrPreset switch { 0 => "Ultra Quality (77%)", 1 => "Quality (67%)", 2 => "Balanced (59%)", _ => "Performance (50%)" };
        string? ChangedValue(string setting) => setting switch
        {
            "Battle size" => lower.BattleSize.ToString(), "Resolution / FSR" => preset,
            "Bloom" or "HD PBR" => "Off", "Texture quality" => "Native", "Effects density" => "0.25", "View distance" => "3500", _ => null
        };
        return lower with
        {
            Reasons = r.Reasons.Select(reason => ChangedValue(reason.Setting) is { } value
                ? reason with { Value = value, Reason = "Verification missed the target; use this lower setting" } : reason)
                .Append(new SettingReason("Auto-tune step", "One further reduction", "Verification missed 60 FPS 1% low or server tick p95 < 50 ms")).ToArray()
        };
    }
}
