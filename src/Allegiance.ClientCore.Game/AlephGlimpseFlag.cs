using System;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P607 — the on/off tri-state launcher-value parser for the aleph
/// destination-glimpse / transit-pulse flag (<c>--aleph-glimpse</c>). Shared by
/// <c>Program.cs</c>'s arg loop and the unit tests so the parse rules are
/// provable headless. The glimpse+transit-flare feature is ON by default
/// (P607 — Domingo wants the enemy-crossing flare visible out of the box);
/// <c>--aleph-glimpse off</c> disables only the glimpse/funnel-boost/flare
/// extras — the native arrival-star swell for every visible transit stays on
/// either way (see <c>Game1.Rendering.DrainAlephTransitSignals</c>).
/// </summary>
public static class AlephGlimpseFlag
{
    /// <summary>The P607 default: ON.</summary>
    public const bool DefaultOn = true;

    /// <summary>
    /// Resolve a <c>--aleph-glimpse</c> value to a bool: "on" → true, "off" →
    /// false, anything else (unrecognised or absent) → null (the caller warns
    /// and uses <see cref="DefaultOn"/>). Case-insensitive, matching every
    /// other client on/off flag.
    /// </summary>
    public static bool? Parse(string? v)
    {
        if (string.Equals(v, "on", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(v, "off", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
    }
}
