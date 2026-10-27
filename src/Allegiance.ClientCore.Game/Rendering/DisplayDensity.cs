using System.Diagnostics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P833 — detect the OS desktop scale factor (the display's physical density)
/// that drives the ONE Modern-HUD scale (<see cref="ModernHudScale"/>). The
/// desktop's own UI text is sized by this quantity, so the Modern HUD tracks it:
/// on a 1.0 desktop the HUD is byte-for-byte what it was pre-P833; on a
/// fractional-scaled desktop (e.g. GNOME 1.25 on a 40&quot; 5K2K) the HUD scales with
/// the desktop UI instead of inflating with window height.
/// </summary>
public static class DisplayDensity
{
    /// <summary>Env override: lets a pilot or a proof run force the density the
    /// HUD assumes (also how live-proof simulates a fractional desktop).</summary>
    public const string EnvOverride = "ALLEGIANCE_HUD_DISPLAY_SCALE";

    /// <summary>
    /// The OS desktop scale factor (1.0 = reference). Never throws: every source
    /// is guarded, and any failure falls back to 1.0 so the client always boots.
    /// </summary>
    public static float DetectDesktopScale()
    {
        try
        {
            var env = System.Environment.GetEnvironmentVariable(EnvOverride);
            if (TryParsePositive(env, out var fromEnv))
                return fromEnv;

            // GNOME: the UI scale is integer scaling-factor × text-scaling-factor
            // (both 1.0/1 by default). Grab whichever reports >1.
            if (QueryGsettings("org.gnome.desktop.interface", "scaling-factor") is { } intScale && intScale > 1)
                return intScale;
            if (QueryGsettings("org.gnome.desktop.interface", "text-scaling-factor") is { } textScale && textScale > 1.005f)
                return textScale;
        }
        catch
        {
            // Detection is best-effort; never let it stop the client.
        }
        return 1f;
    }

    private static bool TryParsePositive(string? s, out float value)
    {
        value = 1f;
        if (string.IsNullOrWhiteSpace(s)) return false;
        if (!float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        if (!float.IsFinite(v) || v <= 0f) return false;
        value = v;
        return true;
    }

    /// <summary>Returns the numeric value of a GNOME dconf key, or null if it is
    /// not queryable (no session / key missing / parse failed).</summary>
    private static float? QueryGsettings(string schema, string key)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "gsettings",
                    ArgumentList = { "get", schema, key },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                }
            };
            if (!proc.Start()) return null;
            if (!proc.WaitForExit(1500)) { try { proc.Kill(); } catch { /* ignore */ } return null; }
            var raw = proc.StandardOutput.ReadToEnd().Trim();
            return ParseGsettingsValue(raw);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>dconf "uint32 1" / "1" / "1.25" → 1 / 1.25; "0" → 0 (means
    /// "automatic/unchanged" for the integer scaling factor, which we treat as 1).</summary>
    private static float? ParseGsettingsValue(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // gsettings prints uint/int values like "uint32 1" or "1".
        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var num = tokens.Length > 0 ? tokens[^1] : raw;
        if (!float.TryParse(num, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var v)) return null;
        return float.IsFinite(v) ? v : null;
    }
}
