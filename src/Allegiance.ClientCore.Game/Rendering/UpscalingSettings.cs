using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame.Rendering;

public enum UpscalingMode { Off, FSR1 }
public enum FsrPreset { UltraQuality, Quality, Balanced, Performance }

/// <summary>Modern client feature; independent of the native/enhanced VFX tier.</summary>
public sealed class UpscalingSettings
{
    public UpscalingMode Upscaling { get; set; }
    public FsrPreset Preset { get; set; } = FsrPreset.Quality;
    /// <summary>0 = no sharpening, 1 = strongest RCAS lobe.</summary>
    public float Sharpness { get; set; } = 0.5f;
    public bool HdTextures { get; set; } = true;
    /// <summary>HD-5 — experimental HD lighting (PBR). Default OFF: with HD
    /// textures on and PBR off the client uses the new HD classic default (sharp
    /// pack basecolor with the native lighting); PBR is opt-in and tuned in HD-6.
    /// Named <c>HdPbr</c> so P729's benchmark recommendations share the field.</summary>
    public bool HdPbr { get; set; }
    public Allegiance.ClientCore.State.EffectsDetail Effects { get; set; } = Allegiance.ClientCore.State.EffectsDetail.Medium;
    public bool DynamicResolution { get; set; }
    public float TargetFrameMs { get; set; } = 16.667f;
    /// <summary>Native <c>Debris</c> preference (<c>LoadPreference("Debris", "1.0")</c>,
    /// WinTrek.cpp:3093; saved by the menu cycle, :4633-4653). 1.5 Low, 1.0 Medium,
    /// 0.8 High, 0 Off.</summary>
    public float Debris { get; set; } = 1.0f;
    public static float Scale(FsrPreset preset) => preset switch
    {
        FsrPreset.UltraQuality => 1f / 1.3f,
        FsrPreset.Quality => 1f / 1.5f,
        FsrPreset.Balanced => 1f / 1.7f,
        FsrPreset.Performance => 0.5f,
        _ => 1f / 1.5f
    };
    public static (int Width, int Height) Dimensions(int width, int height, float scale)
        => (Math.Max(1, (int)MathF.Round(Math.Max(1, width) * Math.Clamp(float.IsFinite(scale) ? scale : 1, 0.5f, 1), MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)MathF.Round(Math.Max(1, height) * Math.Clamp(float.IsFinite(scale) ? scale : 1, 0.5f, 1), MidpointRounding.AwayFromZero)));
    public void Normalize()
    {
        if (!Enum.IsDefined(Effects)) Effects = Allegiance.ClientCore.State.EffectsDetail.Medium;
        if (!Enum.IsDefined(Upscaling)) Upscaling = UpscalingMode.Off;
        if (!Enum.IsDefined(Preset)) Preset = FsrPreset.Quality;
        Sharpness = float.IsFinite(Sharpness) ? Math.Clamp(Sharpness, 0, 1) : 0.5f;
        TargetFrameMs = float.IsFinite(TargetFrameMs) ? Math.Clamp(TargetFrameMs, 8, 100) : 16.667f;
        Debris = float.IsFinite(Debris) && Debris >= 0 ? Debris : 1.0f;
    }
    public static string DefaultPath => Environment.GetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Allegiance", "graphics.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static UpscalingSettings Load(string path)
    {
        try
        {
            string raw = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<UpscalingSettings>(raw, Json) ?? new();
            // HD-5 — migrate a legacy saved HdPbr (the old benchmark-driven "HD PBR"
            // toggle, formerly default-on) onto the new experimental flag. Only when
            // this file predates the flag (no HdPbr field): a player who previously
            // applied an HdPbr-on benchmark recommendation keeps PBR on; everyone
            // else gets the new experimental default (off).
            if (!ContainsJsonProperty(raw, "HdPbr"))
                s.HdPbr = LegacyAppliedHdPbr();
            s.Normalize();
            return s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    private static bool ContainsJsonProperty(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out _);
    }

    /// <summary>The HdPbr a player most recently applied through the benchmark
    /// recommendations (the only place the old HD-PBR toggle was persisted before
    /// HD-5). Absent prefs => the new default (off). Never throws on load noise.</summary>
    private static bool LegacyAppliedHdPbr()
    {
        try { return Allegiance.SinglePlayer.Benchmark.BenchmarkPreferences.Load().Applied?.HdPbr ?? false; }
        catch { return false; }
    }
    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

/// <summary>GPU-budget controller: EWMA, 8% dead band, 30-frame dwell, bounded steps.
/// Missing GPU samples leave the scale alone; CPU/present waits must not shrink the scene.</summary>
public sealed class DynamicResolutionController
{
    public float CurrentScale { get; private set; } = 1;
    private double _average;
    private int _frames;
    public void Reset(float scale) { CurrentScale = scale; _average = 0; _frames = 0; }
    public float Observe(double gpuMs, float targetMs, float maximum)
    {
        if (!double.IsFinite(gpuMs) || gpuMs <= 0) return CurrentScale;
        _average = _average == 0 ? gpuMs : _average * 0.9 + gpuMs * 0.1;
        if (++_frames < 30) return CurrentScale;
        _frames = 0;
        float step = _average > targetMs * 1.08 ? -0.025f : _average < targetMs * 0.92 ? 0.025f : 0;
        CurrentScale = Math.Clamp(CurrentScale + step, 0.5f, maximum);
        return CurrentScale;
    }
}
