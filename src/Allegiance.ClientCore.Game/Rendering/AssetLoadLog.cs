using System;
using System.Diagnostics;
using System.Globalization;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P862 — one console line for a slow native-asset load ("this decode+upload cost
/// 240 ms"), the evidence a stall investigation needs without a profiler attached.
/// The native caches are lazy, so an asset's cost always lands inside whichever
/// frame first needed it; naming the asset and the milliseconds is what turns "the
/// warp into Alderan stutters" into "three 2048² rock textures, 240 ms each".
///
/// <para>Threshold-gated (<c>ALLEGIANCE_ASSET_LOG_MS</c>, default 40 ms, 0 disables)
/// so ordinary play stays quiet: rock/ship/planet textures are 8.4 MB RGB565 files
/// that decode into 16.7 MB RGBA and upload a 16.7 MB GPU surface, so a first load
/// is routinely hundreds of milliseconds on a cold cache — and almost never on a
/// warm one.</para>
/// </summary>
public static class AssetLoadLog
{
    private static readonly double ThresholdMs = ReadThreshold();

    /// <summary>The client's current frame id (set once at startup) so a slow load
    /// can be tied to the frame it stalled. Null in tools/tests.</summary>
    public static Func<long>? FrameStamp;

    /// <summary>True when a load of <paramref name="ms"/> is worth a line.</summary>
    public static bool ShouldLog(double ms) => ms >= ThresholdMs;

    /// <summary>Log one finished asset load when it is slow enough to matter.</summary>
    public static void Note(string kind, string stem, double ms, string? detail = null)
    {
        if (!ShouldLog(ms)) return;
        long frame = FrameStamp?.Invoke() ?? 0;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[asset] frame {frame}: {kind} '{stem}'{(detail is null ? "" : " " + detail)} loaded in {ms:F1} ms"));
    }

    private static double ReadThreshold()
    {
        var raw = Environment.GetEnvironmentVariable("ALLEGIANCE_ASSET_LOG_MS");
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && ms >= 0)
            return ms;
        return 40.0;
    }
}
