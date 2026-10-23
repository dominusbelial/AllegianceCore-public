using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.SinglePlayer.Benchmark;

// This is deliberately separate from BenchmarkResult: that local diagnostic contains names.
public sealed record ShareHardware(string? CpuModel, int? CpuCores, int CpuThreads, string? GpuModel,
    double? GpuVramGb, string? GpuDriver, double? RamGb, string Os, string OsVersion, int Width, int Height)
{
    public static ShareHardware Capture(string? gpuModel, string? gpuDriver, int width, int height)
    {
        string? cpu = null;
        int? cores = null;
        double? ram = null, vram = null;
        try
        {
            if (OperatingSystem.IsLinux())
            {
                var lines = File.ReadAllLines("/proc/cpuinfo");
                cpu = lines.FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim();
                // Count unique physical package/core pairs (cpu cores alone is per package).
                var pairs = new HashSet<string>(); string? package = null, core = null;
                foreach (var line in lines.Append(""))
                {
                    if (line.StartsWith("physical id", StringComparison.Ordinal)) package = line.Split(':', 2)[1].Trim();
                    if (line.StartsWith("core id", StringComparison.Ordinal)) core = line.Split(':', 2)[1].Trim();
                    if (line.Length == 0) { if (package is not null && core is not null) pairs.Add(package + ":" + core); package = core = null; }
                }
                if (pairs.Count > 0) cores = pairs.Count;
                string? mem = File.ReadLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
                if (mem is not null && double.TryParse(mem.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1], out double kb)) ram = kb / 1048576d;
                // Only numeric capacity, never device paths/serials or PCI identifiers.
                foreach (string card in Directory.EnumerateDirectories("/sys/class/drm", "card*"))
                    if (double.TryParse(ReadOptional(Path.Combine(card, "device/mem_info_vram_total")), out double bytes))
                        vram = Math.Max(vram ?? 0, bytes / 1073741824d);
            }
            else if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                cpu = key?.GetValue("ProcessorNameString") as string ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
                uint length = 0;
                GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref length);
                if (length > 0 && length < 16777216)
                {
                    IntPtr buffer = Marshal.AllocHGlobal((int)length);
                    try
                    {
                        if (GetLogicalProcessorInformationEx(0, buffer, ref length))
                        {
                            int count = 0, offset = 0;
                            while (offset + 8 <= length)
                            {
                                int size = Marshal.ReadInt32(buffer, offset + 4);
                                if (size < 8 || offset + size > length) break;
                                count++; offset += size;
                            }
                            if (count > 0) cores = count;
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref memory)) ram = memory.TotalPhysical / 1073741824d;
            }
            else if (OperatingSystem.IsMacOS())
            {
                cpu = MacString("machdep.cpu.brand_string");
                if (long.TryParse(MacNumber("hw.physicalcpu"), out long physical)) cores = (int)physical;
                if (long.TryParse(MacNumber("hw.memsize"), out long bytes)) ram = bytes / 1073741824d;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException) { }
        return new(Clean(cpu), cores, Environment.ProcessorCount, Clean(gpuModel), vram, Clean(gpuDriver), ram,
            OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsLinux() ? "Linux" : "Other",
            Environment.OSVersion.Version.ToString(), width, height);
    }
    private static string? MacString(string key)
    {
        nuint size = 0;
        if (Sysctl(key, IntPtr.Zero, ref size, IntPtr.Zero, 0) != 0 || size == 0 || size > 4096) return null;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try { return Sysctl(key, buffer, ref size, IntPtr.Zero, 0) == 0 ? Marshal.PtrToStringUTF8(buffer) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string? MacNumber(string key)
    {
        nuint size = 8; IntPtr buffer = Marshal.AllocHGlobal(8);
        try { return Sysctl(key, buffer, ref size, IntPtr.Zero, 0) == 0 ? (size == 4 ? Marshal.ReadInt32(buffer) : Marshal.ReadInt64(buffer)).ToString(System.Globalization.CultureInfo.InvariantCulture) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    [DllImport("libSystem.B.dylib", EntryPoint = "sysctlbyname")]
    private static extern int Sysctl(string name, IntPtr output, ref nuint size, IntPtr input, nuint inputSize);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
    private static string? ReadOptional(string path) => File.Exists(path) ? File.ReadAllText(path) : null;
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : new string(value.Where(c => !char.IsControl(c)).Take(200).ToArray());
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended; }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}
public sealed record ShareSettings(int BattleSize, int Difficulty, double ResearchSpeed, bool SpectatorDefault,
    bool Fsr, int FsrPreset, bool Bloom, bool HdPbr, bool HdTextures, int FrameCap, bool Vsync, float EffectsDensity, float ViewDistance, int EffectsQuality = 1)
{
    public static ShareSettings From(BenchmarkRecommendation s) => new((int)s.BattleSize, (int)s.Difficulty, s.ResearchSpeed,
        s.SpectatorDefault, s.Fsr, s.FsrPreset, s.Bloom, s.HdPbr, s.HdTextures, s.FrameCap, s.Vsync, s.EffectsDensity, s.ViewDistance, (int)s.EffectsQuality);
    public string[] ChangedFrom(ShareSettings suggested)
    {
        var actual = JsonSerializer.SerializeToElement(this, ShareJson.Default.ShareSettings);
        var before = JsonSerializer.SerializeToElement(suggested, ShareJson.Default.ShareSettings);
        return actual.EnumerateObject().Where(p => p.Value.GetRawText() != before.GetProperty(p.Name).GetRawText()).Select(p => p.Name).ToArray();
    }
}
public sealed record ShareRun(int Repetition, bool Rendered, double WallSeconds, double? FpsAverage, double? FpsOnePercentLow,
    Distribution? FrameMs, double? CpuMs, double? GpuMs, Distribution? ServerTickMs, int BotCount,
    double?[] BotDecisionCpuMs, double? AmortizedCpuMsPerBotSecond)
{
    public static ShareRun From(BenchmarkRun r) => new(r.Repetition, r.Rendered, r.WallSeconds, r.FpsAverage, r.FpsOnePercentLow,
        r.FrameMs, r.CpuMs, r.GpuMs, r.ServerTickMs, r.BotCount, r.PerBotCpu.Select(b => b.DecisionCpuMs).ToArray(), r.AmortizedCpuMsPerBotSecond);
}
public sealed record ShareTune(ShareSettings Settings, ShareRun Measurement, bool TargetMet);
public sealed record ShareResult(string Mode, string Preset, double DurationSeconds, string Bottleneck,
    ShareRun[] Runs, ShareRun Median, ShareSettings? SettingsUsed, ShareSettings SettingsSuggested, ShareTune[] AutoTune,
    // P789 — schema 2: the Showcase score and the match kind. Both optional so
    // every v1 payload stays valid; the collector accepts 1 and 2.
    int? Score = null, string? MatchKind = null, string? ScoreNote = null);
public sealed record SharePayload(int Schema, Guid EventId, Guid? InstallId, DateTimeOffset Timestamp,
    string GameVersion, string Commit, ShareHardware Hardware, ShareResult Result)
{
    public const int CurrentSchema = 2;
    public static SharePayload From(BenchmarkResult r, ShareHardware hardware) => new(CurrentSchema, Guid.NewGuid(), null, r.Timestamp,
        typeof(BenchmarkResult).Assembly.GetName().Version?.ToString() ?? "unknown",
        CommitFromInformationalVersion(typeof(BenchmarkResult).Assembly.GetCustomAttributesData()
            .FirstOrDefault(a => a.AttributeType == typeof(System.Reflection.AssemblyInformationalVersionAttribute))?.ConstructorArguments[0].Value?.ToString()),
        hardware, new(r.Mode, r.Preset, r.DurationSeconds, r.Bottleneck, r.Runs.Select(ShareRun.From).ToArray(), ShareRun.From(r.Median),
            r.TestedSettings is null ? null : ShareSettings.From(r.TestedSettings), ShareSettings.From(r.Recommendation), r.AutoTune.Select(t => new ShareTune(ShareSettings.From(t.Settings), ShareRun.From(t.Measurement), t.TargetMet)).ToArray(),
            r.Score, r.MatchKind, r.ScoreNote));
    internal static string CommitFromInformationalVersion(string? version)
    {
        string? candidate = version?.Split('+').ElementAtOrDefault(1);
        // Custom build metadata can contain branch names or local paths. Only a hash may leave the client.
        return candidate is { Length: >= 7 and <= 64 } && candidate.All(char.IsAsciiHexDigit)
            ? candidate.ToLowerInvariant() : "unknown";
    }
    public string ToJson() => JsonSerializer.Serialize(this, ShareJson.Default.SharePayload);
}
public sealed record KeptPayload(int Schema, Guid EventId, Guid InstallId, Guid ResultId, DateTimeOffset Timestamp,
    bool Kept, string[] ChangedSettings);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SharePayload))]
[JsonSerializable(typeof(KeptPayload))]
[JsonSerializable(typeof(ShareSettings))]
[JsonSerializable(typeof(SharingState))]
internal partial class ShareJson : JsonSerializerContext;
