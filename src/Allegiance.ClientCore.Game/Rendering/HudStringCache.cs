using System.Globalization;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-12b — value-keyed string cache for the HUD draw path. The HUD runs every
/// frame (200v200 is a design target), and C# interpolated strings allocate a new
/// <see cref="string"/> on every call even when the displayed integer never
/// changed: <c>CLOAK 22%</c>, <c>5 lives</c>, <c>4 kills</c>, the eight
/// command-gauge percentages and the command summary's fixed rows. Each helper
/// below formats once per distinct integer value and then hands back the SAME
/// string instance, exactly like <see cref="RadarLabelModel.RangeText"/> caches
/// the per-contact range digits (HUD-5b). Renderer-only text; no state.
/// </summary>
public static class HudStringCache
{
    /// <summary>Bound each dictionary so a pathological mission cannot grow it
    /// without limit (money/target names are open-ended). Clearing on overflow
    /// only costs one frame of formatting.</summary>
    private const int Limit = 2048;
    private const string DrainedLegacy = "CLOAK DRAINED";

    private static readonly Dictionary<int, string> _lives = new();
    private static readonly Dictionary<int, string> _kills = new();
    private static readonly Dictionary<int, string> _cloak = new();
    private static readonly Dictionary<int, string> _cloakDrained = new();
    private static readonly Dictionary<int, string> _percent = new();
    private static readonly Dictionary<int, string> _signature = new();
    private static readonly Dictionary<int, string> _killBonusLine = new();
    private static readonly Dictionary<int, string> _killBonusBadge = new();
    private static readonly Dictionary<int, string> _killBonusNumber = new();
    private static readonly Dictionary<int, string> _money = new();
    private static readonly Dictionary<(int Mps, string Unit), string> _speed = new();
    private static readonly Dictionary<string, string> _shipSummary = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _targetSummary = new(StringComparer.Ordinal);
    private static readonly Dictionary<int, string> _targetRange = new();
    private static readonly Dictionary<int, string> _targetSpeed = new();
    private static readonly Dictionary<string, string> _turretType = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _location = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _tti = new(StringComparer.Ordinal);

    /// <summary>Native whole-number formatting (<c>"0"</c>): round half away
    /// from zero, like <c>string.Format</c>.</summary>
    private static int Round(float value) => (int)MathF.Round(value, MidpointRounding.AwayFromZero);

    private static string Get<TKey>(Dictionary<TKey, string> cache, TKey key,
        Func<TKey, string> format) where TKey : notnull
    {
        if (cache.TryGetValue(key, out var cached)) return cached;
        var text = format(key);
        if (cache.Count >= Limit) cache.Clear();
        cache[key] = text;
        return text;
    }

    /// <summary>Native lives badge text (<c>&lt;n&gt; lives</c>, dialog.mdl:5070-5080).</summary>
    public static string Lives(int lives) => Get(_lives, lives, static n => $"{n} lives");

    /// <summary>Native kills badge text (<c>&lt;n&gt; kills</c>, dialog.mdl:5107-5120).</summary>
    public static string Kills(int kills) => Get(_kills, kills, static n => $"{n} kills");

    /// <summary><c>CLOAK &lt;n&gt;%</c> (native strength 100·(1−GetCloaking()),
    /// consoledata.cpp:480-489).</summary>
    public static string Cloak(float strengthPercent) =>
        Get(_cloak, Round(strengthPercent), static p => $"CLOAK {p}%");

    /// <summary><c>CLOAK &lt;n&gt;% DRAINED</c> (energy ≤ .2, dialog.mdl:651-656).</summary>
    public static string CloakDrained(float strengthPercent) =>
        Get(_cloakDrained, Round(strengthPercent), static p => $"CLOAK {p}% DRAINED");

    /// <summary>The own-status cloak line native would show. Native marks the
    /// gauge drained at energy ≤ .2 (dialog.mdl:651-656); before the first
    /// <c>OwnShipCloakUpdate(220)</c> the continuous fraction is unknown, so the
    /// legacy <c>CLOAK DRAINED</c> label must still appear from the state bit
    /// rather than the row disappearing (HUD-12b regression fix).</summary>
    public static string? CloakStatusText(bool drained, float? strengthPercent)
    {
        if (strengthPercent is { } strength)
            return drained ? CloakDrained(strength) : Cloak(strength);
        return drained ? DrainedLegacy : null;
    }

    /// <summary>A gauge percentage (<c>&lt;n&gt;%</c>).</summary>
    public static string Percent(float fraction) =>
        Get(_percent, Round(Math.Clamp(fraction, 0f, 1f) * 100f), static p => $"{p}%");

    /// <summary>Native <c>SIG: &lt;n&gt;%</c> (dialog.mdl:2595-2602).</summary>
    public static string Signature(float percent) =>
        Get(_signature, Round(percent), static p => $"SIG: {p}%");

    /// <summary>Command-summary kill-bonus row (<c>Kill bonus: &lt;n&gt;%</c>).</summary>
    public static string KillBonusLine(float percent) =>
        Get(_killBonusLine, Round(percent), static p => $"Kill bonus: {p}%");

    /// <summary>Own-status <c>KB &lt;n&gt;%</c> fallback when the native badge is missing.</summary>
    public static string KillBonusBadge(float percent) =>
        Get(_killBonusBadge, Round(percent), static p => $"KB {p}%");

    /// <summary>The integer inside the native kill-counter bitmap.</summary>
    public static string KillBonusNumber(float percent) =>
        Get(_killBonusNumber, Round(percent), static p => p.ToString(CultureInfo.InvariantCulture));

    /// <summary><c>&lt;n&gt; cr</c> (native money is a plain <c>%d</c>,
    /// teampane.cpp:208,226).</summary>
    public static string Money(int credits) =>
        Get(_money, credits, static m => $"{m} cr");

    /// <summary>Command-summary speed row; <paramref name="unit"/> is the port's
    /// "mps" (model.SpeedUnit).</summary>
    public static string Speed(float metresPerSecond, string unit) =>
        Get(_speed, (Mps: Round(MathF.Max(0f, metresPerSecond)), Unit: unit),
            static k => $"Speed: {k.Mps} {k.Unit}");

    /// <summary>P805 (row 492) — the command-summary TARGET group's range row
    /// (<c>cortTargetRange</c>, dialog.mdl:4615-4628: native concatenates the
    /// rounded metres with "m" with NO space, or prints the sector name when
    /// GetRange is -1; the label "Target range:" is drawn separately).</summary>
    public static string TargetRange(float metres) =>
        Get(_targetRange, Round(MathF.Max(0f, metres)), static m => $"{m}m");

    /// <summary>P805 (row 492) — the command-summary TARGET group's speed row
    /// (<c>cortTargetSpeed</c>, dialog.mdl:4630-4656: native concatenates the
    /// rounded metres-per-second with " mps").</summary>
    public static string TargetSpeed(float metresPerSecond) =>
        Get(_targetSpeed, Round(MathF.Max(0f, metresPerSecond)), static v => $"{v} mps");

    /// <summary>Command-summary ship type row (<c>Ship: &lt;type&gt;</c>).</summary>
    public static string ShipSummary(string typeName) =>
        Get(_shipSummary, typeName, static t => $"Ship: {t}");

    /// <summary>Command-summary selected-target row.</summary>
    public static string TargetSummary(string name) =>
        Get(_targetSummary, name, static n => $"Target: {n}");

    /// <summary>HUD-18 — native <c>TargetRangeString</c>'s sector fallback
    /// (<c>"LOCATION:" + GetSectorName(Target)</c>, <c>Artwork/dialog.mdl:4053-4060</c>;
    /// note native has NO space after the colon). Cached by the sector name so
    /// the per-frame draw path reuses one instance.</summary>
    public static string Location(string sectorName) =>
        Get(_location, sectorName, static n => "LOCATION:" + n);

    /// <summary>HUD-18 — the Modern pane's <c>"TTI: " + GHTTI</c> line
    /// (<c>Artwork/dialog.mdl:1348-1353</c>). <paramref name="value"/> is the
    /// already-cached m:ss/infinite string from <see cref="TargetTti.Text"/>.</summary>
    public static string Tti(string value) =>
        Get(_tti, value, static v => "TTI: " + v);

    /// <summary>HUD-12b — the turret suffix on the source-ship type
    /// (<c>&lt;type&gt; (turret)</c>). Cached by the hull name so the
    /// per-frame snapshot build stops concatenating a new string.</summary>
    public static string TurretShipType(string typeName) =>
        Get(_turretType, typeName, static t => t + " (turret)");
}
