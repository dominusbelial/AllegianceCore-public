using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.SinglePlayer.Benchmark;

public sealed record Distribution(int Count, double Mean, double P50, double P95, double P99, double Worst)
{
    public static Distribution Of(IEnumerable<double> samples)
    {
        var sorted = samples.Where(v => double.IsFinite(v) && v >= 0).Order().ToArray();
        double P(double p) => sorted.Length == 0 ? 0 : sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * p) - 1)];
        return new(sorted.Length, sorted.Length == 0 ? 0 : sorted.Average(), P(.5), P(.95), P(.99), P(1));
    }
}
public sealed record BotCpu(string Name, double? DecisionCpuMs);
public sealed record WorkerCpu(int Pid, double CpuMs, long WorkingSetBytes);
public sealed record BenchmarkRun
{
    public int Repetition { get; init; }
    public bool Rendered { get; init; }
    public double WallSeconds { get; init; }
    public double? FpsAverage { get; init; }
    public double? FpsOnePercentLow { get; init; }
    public Distribution? FrameMs { get; init; }
    public double? CpuMs { get; init; }
    public double? GpuMs { get; init; }
    public Distribution? ServerTickMs { get; init; }
    public int BotCount { get; init; }
    public BotCpu[] PerBotCpu { get; init; } = [];
    public int WorkerCount { get; init; }
    public WorkerCpu[] Workers { get; init; } = [];
    public double? AmortizedCpuMsPerBotSecond { get; init; }
    public string BotCpuAttribution { get; init; } = "PerBotCpu measures each bot decision thread CPU; excludes shared receive/GC. Amortized CPU divides owned process CPU by bots and elapsed seconds, including process overhead.";
    public long WorkingSetBytes { get; init; }
    public long ManagedBytes { get; init; }
    public long AllocatedBytes { get; init; }
    public int[] GcCollections { get; init; } = [];
    public double GcPauseMs { get; init; }
    public long Shots { get; init; }
    public long Hits { get; init; }
    public int FrameCount => FrameStateHashes.Length > 0 ? FrameStateHashes.Length : FrameMs?.Count ?? 0;
    public string FrameStateDigest => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("", FrameStateHashes))));
    [JsonIgnore]
    public string[] FrameStateHashes { get; init; } = [];
}
public sealed record SettingReason(string Setting, string Value, string Reason);
public sealed record BenchmarkRecommendation
{
    public SkirmishSize BattleSize { get; init; }
    public SkirmishDifficulty Difficulty { get; init; } = SkirmishDifficulty.Normal;
    public double ResearchSpeed { get; init; } = 1;
    public SkirmishSpeed Speed { get; init; } = SkirmishSpeed.Normal;
    public bool SpectatorDefault { get; init; } = true;
    public bool Fsr { get; init; }
    public int FsrPreset { get; init; } = 1;
    public Allegiance.ClientCore.State.EffectsDetail EffectsQuality { get; init; } = Allegiance.ClientCore.State.EffectsDetail.Medium;
    public bool Bloom { get; init; }
    public bool HdPbr { get; init; } = true;
    public bool HdTextures { get; init; } = true;
    public int FrameCap { get; init; } = 60;
    public bool Vsync { get; init; }
    public float EffectsDensity { get; init; } = 1;
    public float ViewDistance { get; init; } = 6000;
    public SettingReason[] Reasons { get; init; } = [];
}
public sealed record TuneVerification(BenchmarkRecommendation Settings, BenchmarkRun Measurement, bool TargetMet);
public sealed record BenchmarkResult
{
    public int SchemaVersion { get; init; } = 1;
    public string Mode { get; init; } = "";
    public string Preset { get; init; } = "";
    public double DurationSeconds { get; init; }
    public string? ReplaySha256 { get; init; }
    public string Renderer { get; init; } = "Headless";
    public int Width { get; init; }
    public int Height { get; init; }
    public BenchmarkRecommendation? TestedSettings { get; init; }
    public int Seed { get; init; } = 729;
    public string Map { get; init; } = "m2xspiderw.igc";
    public int[] Civs { get; init; } = [25, 3];
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Machine { get; init; } = Environment.MachineName;
    public string Os { get; init; } = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
    public int LogicalCores { get; init; } = Environment.ProcessorCount;
    public BenchmarkRun[] Runs { get; init; } = [];
    public BenchmarkRun Median { get; init; } = new();
    public string MedianRule { get; init; } = "Scalar timing/FPS metrics are medians of three runs; resource/identity snapshot comes from the middle run by frame p99, server p95, or processing wall time";
    public string Bottleneck { get; init; } = "";
    public BenchmarkRecommendation Recommendation { get; init; } = new();
    public TuneVerification[] AutoTune { get; init; } = [];
    public string TargetStatus { get; init; } = "Not verified";
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, BenchmarkJson.Default.BenchmarkResult));
        File.Move(path + ".tmp", path, true);
    }
    public static BenchmarkRun SelectMedian(IEnumerable<BenchmarkRun> runs) => runs
        .OrderBy(r => r.FrameMs?.P99 ?? r.ServerTickMs?.P95 ?? r.WallSeconds).ElementAt(1);
    public static BenchmarkRun MedianOf(IEnumerable<BenchmarkRun> source)
    {
        var runs = source.ToArray();
        if (runs.Length != 3) throw new ArgumentException("Exactly three benchmark runs are required");
        double M(Func<BenchmarkRun, double> select) => runs.Select(select).Order().ElementAt(1);
        double? N(Func<BenchmarkRun, double?> select) => runs.All(r => select(r).HasValue) ? M(r => select(r)!.Value) : null;
        Distribution? D(Func<BenchmarkRun, Distribution?> select) => runs.All(r => select(r) is not null)
            ? new((int)M(r => select(r)!.Count), M(r => select(r)!.Mean), M(r => select(r)!.P50),
                M(r => select(r)!.P95), M(r => select(r)!.P99), M(r => select(r)!.Worst)) : null;
        return SelectMedian(runs) with
        {
            FpsAverage = N(r => r.FpsAverage), FpsOnePercentLow = N(r => r.FpsOnePercentLow),
            FrameMs = D(r => r.FrameMs), ServerTickMs = D(r => r.ServerTickMs), CpuMs = N(r => r.CpuMs), GpuMs = N(r => r.GpuMs),
            AmortizedCpuMsPerBotSecond = N(r => r.AmortizedCpuMsPerBotSecond)
        };
    }
}
public sealed record BenchmarkPreferences
{
    public bool OfferHandled { get; init; }
    public BenchmarkRecommendation? Applied { get; init; }
    public static string PathName => Environment.GetEnvironmentVariable("ALLEGIANCE_BENCHMARK_CONFIG")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AllegianceCore", "benchmark.json");
    public static BenchmarkPreferences Load(string? path = null)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(path ?? PathName), BenchmarkJson.Default.BenchmarkPreferences) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save(string? path = null)
    {
        path ??= PathName;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, BenchmarkJson.Default.BenchmarkPreferences));
        File.Move(path + ".tmp", path, true);
    }
}
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Allegiance.Server.ServerConfig))]
[JsonSerializable(typeof(BenchmarkResult))]
[JsonSerializable(typeof(BenchmarkPreferences))]
internal partial class BenchmarkJson : JsonSerializerContext;

/// <summary>Measured OS counters for this client/server process and owned bot processes.</summary>
public sealed class BenchmarkCounters
{
    private readonly long _start = Stopwatch.GetTimestamp(), _alloc = GC.GetTotalAllocatedBytes(false);
    private readonly TimeSpan _pause = GC.GetTotalPauseDuration();
    private readonly int[] _gc = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
    private readonly Dictionary<int, double> _cpu = new();
    private readonly Dictionary<string, double> _botCpu;
    private static Dictionary<string, double> BotSnapshot(JsonElement stats)
    {
        var result = new Dictionary<string, double>();
        if (stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty("perBotCpu", out var bots) && bots.ValueKind == JsonValueKind.Array)
            foreach (var bot in bots.EnumerateArray())
                if (bot.TryGetProperty("decisionCpuMs", out var ms) && ms.ValueKind == JsonValueKind.Number)
                    result[bot.GetProperty("name").GetString()!] = ms.GetDouble();
        return result;
    }
    public BenchmarkCounters(IEnumerable<int>? processes = null, JsonElement stats = default)
    {
        _botCpu = BotSnapshot(stats);
        foreach (int pid in processes ?? [])
            try { using var p = Process.GetProcessById(pid); _cpu[pid] = p.TotalProcessorTime.TotalMilliseconds; }
            catch (ArgumentException) { }
    }
    public BenchmarkRun Finish(int repetition, SkirmishHost? host = null)
    {
        double seconds = Stopwatch.GetElapsedTime(_start).TotalSeconds;
        var workers = new List<WorkerCpu>();
        foreach (var (pid, before) in _cpu)
            try { using var p = Process.GetProcessById(pid); workers.Add(new(pid, Math.Max(0, p.TotalProcessorTime.TotalMilliseconds - before), p.WorkingSet64)); }
            catch (ArgumentException) { }
        using var self = Process.GetCurrentProcess();
        int bots = host?.Options.TeamSize * 2 - 1 ?? 0;
        if (host?.Stats is { ValueKind: JsonValueKind.Object } stats && stats.TryGetProperty("botCount", out var actualBots))
            bots = actualBots.GetInt32();
        return new()
        {
            Repetition = repetition, WallSeconds = seconds, ServerTickMs = host is null ? null : Distribution.Of(host.TickSamples),
            BotCount = bots, PerBotCpu = host is null ? [] : BotSnapshot(host.Stats).Select(b => new BotCpu(b.Key, Math.Max(0, b.Value - _botCpu.GetValueOrDefault(b.Key)))).ToArray(), WorkerCount = host?.Budget.Workers ?? 0, Workers = workers.ToArray(),
            AmortizedCpuMsPerBotSecond = bots > 0 ? workers.Sum(w => w.CpuMs) / seconds / bots : null,
            WorkingSetBytes = self.WorkingSet64 + workers.Sum(w => w.WorkingSetBytes), ManagedBytes = GC.GetTotalMemory(false),
            AllocatedBytes = GC.GetTotalAllocatedBytes(false) - _alloc,
            GcCollections = Enumerable.Range(0, 3).Select(g => GC.CollectionCount(g) - _gc[g]).ToArray(),
            GcPauseMs = (GC.GetTotalPauseDuration() - _pause).TotalMilliseconds
        };
    }
}
