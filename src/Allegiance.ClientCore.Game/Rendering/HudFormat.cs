namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Pure, side-effect-free HUD formatting/colour logic. No MonoGame /
/// <c>Microsoft.Xna.Framework</c> types anywhere in the public surface — the
/// colour result is the plain <see cref="HudColor"/> record, the string helpers
/// return <c>string</c> — so this whole class is unit-testable without a
/// graphics device, mirroring the <see cref="Flight"/> namespace's discipline.
/// </summary>
public static class HudFormat
{
    /// <summary>Replace any character outside printable ASCII (and newline) with
    /// '?'. HUD-5b — returns the SAME instance when the text is already clean
    /// (every ship name, range and label is), so the per-frame label path does
    /// not allocate a copy per string the way <c>new string(text.Select(...))</c>
    /// did at 107v107.</summary>
    public static string Printable(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= ' ' and <= '~' or '\n') continue;
            return Sanitize(text);
        }
        return text;
    }

    private static string Sanitize(string text)
    {
        var chars = new char[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            chars[i] = c is >= ' ' and <= '~' or '\n' ? c : '?';
        }
        return new string(chars);
    }

    /// <summary>Clamp a 0..1 fraction into [0,1] (defensive against a stray
    /// wire value; <c>ServerShipUpdate</c>'s percent decode already clamps, but
    /// the colour ramp must never extrapolate outside the intended range).</summary>
    public static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    /// <summary>
    /// The status colour for a 0..1 quantity, ramping continuously from danger
    /// red at 0 through warning amber at <paramref name="warnBelow"/> to the
    /// HUD's teal-green accent at 1 — the classic "green is good, red is bad"
    /// semantic expressed as a smooth gradient (no banding, no hard switch).
    /// </summary>
    /// <param name="fraction">The 0..1 quantity to colour.</param>
    /// <param name="warnBelow">The fraction at which the ramp passes through
    /// amber (the onset of "warning"). Must lie in (0,1); endpoints collapse
    /// the ramp gracefully rather than dividing by zero.</param>
    public static HudColor StatusColor(float fraction, float warnBelow = 0.5f)
    {
        float f = Clamp01(fraction);
        var danger = new HudColor(1f, 0.18f, 0.15f);      // 0 → red
        var warn = new HudColor(1f, 0.72f, 0.09f);        // warnBelow → amber
        var ok = HudColor.TealAccent;                     // 1 → teal-green

        if (f <= warnBelow)
        {
            float t = warnBelow <= 0f ? 0f : f / warnBelow;
            return HudColor.Lerp(danger, warn, t);
        }
        float t2 = warnBelow >= 1f ? 0f : (f - warnBelow) / (1f - warnBelow);
        return HudColor.Lerp(warn, ok, t2);
    }

    /// <summary>
    /// Format a raw 0..1 fraction as a whole-number percentage string in
    /// 0..100 (e.g. <c>0.856</c> → <c>"86"</c>). The '%' glyph is left to the
    /// caller so labels compose freely.
    /// </summary>
    public static string FormatPercent(float fraction)
    {
        int pct = (int)Math.Round(Clamp01(fraction) * 100f);
        return pct.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Map the throttle axis -1 (idle) .. +1 (full forward) to a 0..100
    /// display percentage. <c>-1</c> → 0, <c>0</c> → 50, <c>+1</c> → 100.
    /// Out-of-range inputs are clamped to the axis span.
    /// </summary>
    public static int ThrottlePercent(float throttle)
    {
        float t = Math.Clamp(throttle, -1f, 1f);
        return (int)Math.Round((t + 1f) / 2f * 100f);
    }

    /// <summary>
    /// Format a speed in world units/sec as a whole-number string. Speeds here
    /// are genuine magnitudes (often hundreds), so an integer readout is the
    /// honest, readable form — fractional digit noise would just be decoration.
    /// </summary>
    public static string FormatSpeed(float unitsPerSecond) =>
        MathF.Round(MathF.Max(0f, unitsPerSecond)).ToString(
            System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Native <c>NumberString</c> (<c>engine/modeler.cpp:2008</c>):
    /// <c>(int)(v + 0.5f)</c> — truncation toward zero, so the target pane's
    /// unseen-speed sentinel <c>-1</c> displays as <c>"0"</c> and exact halves
    /// round up (2.5 → 3, unlike .NET's banker's rounding).
    /// </summary>
    public static string FormatNativeNumber(float v) =>
        ((int)(v + 0.5f)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// P442 — format a player's money the way native presents it: a dollar
    /// sign followed by the plain decimal integer (native formats money with
    /// <c>wsprintf(cbTemp, "%d", pplayer->GetMoney())</c>,
    /// <c>WinTrek/teampane.cpp:208,226</c>, and the payday/income text lines as
    /// <c>"$%d"</c>, <c>appmsg.cpp:1317-1321</c> — no cents, no grouping).
    /// <c>MyMoney</c> is an int (the native 32-bit wire width), so the formatter
    /// takes a plain int. Negative is never produced by the server in practice,
    /// but a defensive "-$n" is honest rather than masking corruption.
    /// </summary>
    public static string FormatMoney(int money)
    {
        string body = money.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (money < 0) return "-$" + body[1..];
        return "$" + body;
    }
}
