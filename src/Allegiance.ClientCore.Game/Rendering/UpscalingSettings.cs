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
    public bool DynamicResolution { get; set; }
    public float TargetFrameMs { get; set; } = 16.667f;
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
        if (!Enum.IsDefined(Upscaling)) Upscaling = UpscalingMode.Off;
        if (!Enum.IsDefined(Preset)) Preset = FsrPreset.Quality;
        Sharpness = float.IsFinite(Sharpness) ? Math.Clamp(Sharpness, 0, 1) : 0.5f;
        TargetFrameMs = float.IsFinite(TargetFrameMs) ? Math.Clamp(TargetFrameMs, 8, 100) : 16.667f;
    }
    public static string DefaultPath => Environment.GetEnvironmentVariable("ALLEGIANCE_GRAPHICS_SETTINGS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Allegiance", "graphics.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    public static UpscalingSettings Load(string path)
    {
        try { var s = JsonSerializer.Deserialize<UpscalingSettings>(File.ReadAllText(path), Json) ?? new(); s.Normalize(); return s; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
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
