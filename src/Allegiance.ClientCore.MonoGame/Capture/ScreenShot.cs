using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Capture;

/// <summary>
/// P417 — the standalone in-game screenshot feature (native TK_ScrnShot, "Take a
/// screen shot"). Purely client-side render-target capture: read the current
/// backbuffer and write it to a timestamped PNG. No server/wire involvement.
///
/// The filename mirrors native's "named by the user's local time"
/// (WinTrek.cpp:4079-4092) — a sorted, collision-resistant stamp is friendlier
/// than native's raw "year-month-day_hour-minute-second" so multiple rapid shots
/// don't overwrite. Encoding reuses <see cref="CapturePngSaver.Encode"/> (the
/// same pure RGBA→PNG encoder the headless video capture path uses), so the two
/// capture features share one proven encoder.
/// </summary>
public static class ScreenShot
{
    /// <summary>The subdirectory (under the working directory) screenshots are
    /// written to. Relative so the game never needs write permission outside its
    /// own tree; created on first use.</summary>
    public const string Subdirectory = "screenshots";

    /// <summary>Build the full output path for a shot taken at <paramref name="now"/>
    /// (local time) under <paramref name="workingDir"/>.</summary>
    public static string BuildPath(DateTime now, string? workingDir = null)
        => Path.Combine(workingDir ?? Directory.GetCurrentDirectory(),
                        Subdirectory,
                        $"shot_{now:yyyy-MM-dd_HH-mm-ss_fff}.png");

    /// <summary>
    /// Capture the current backbuffer to a timestamped PNG and return the path
    /// written. Must be called on the graphics thread with a live device (e.g. at
    /// the end of Draw), after the frame has been rendered but before Present —
    /// that is the moment the backbuffer holds the real frame.
    /// </summary>
    public static string CaptureBackbuffer(GraphicsDevice device, string? workingDir = null)
    {
        int w = device.PresentationParameters.BackBufferWidth;
        int h = device.PresentationParameters.BackBufferHeight;
        var px = new Color[w * h];
        device.GetBackBufferData<Color>(px);
        string path = BuildPath(DateTime.Now, workingDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, CapturePngSaver.Encode(px, w, h));
        return path;
    }
}
