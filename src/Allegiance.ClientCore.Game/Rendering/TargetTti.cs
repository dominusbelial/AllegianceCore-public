using System.Globalization;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-18 — the native GlassHUD "time to intercept" text
/// (<c>Artwork/dialog.mdl:1280-1325</c>, drawn by <c>GHLargeTargetTTI</c>
/// <c>:1327-1355</c>). Native computes it only in the GlassHUD layout; the port
/// has no GlassHUD skin, so its Modern layout — the alternate target pane — is
/// where this string is presented. The classic <c>DefaultNormalTargetDisplay</c>
/// (<c>dialog.mdl:4217-4242</c>) has no TTI natively.
///
/// <para>The arithmetic is native's, not a ballistic solve:</para>
/// <code>
/// allseconds = GetRange(Target) / (GetSpeed(Me) - GetSpeed(Target))
/// abs        = |allseconds|
/// mod        = fmod(abs, 60)              ; engine/valuetransform.cpp:122-125 -> zlib/zmath.h:105-112
/// minutes    = (abs - mod) / 60
/// seconds    = min(mod, 59)
/// </code>
/// <para><c>GetSpeed</c> is the scalar velocity length (<c>|v|</c>), 0 for a
/// static model and -1 when not <c>CanSee</c>
/// (<c>WinTrek/consoledata.cpp:136-150</c>). <c>GetRange</c> is -1 when the
/// target is not in the player's cluster (<c>:182-199</c>), and native feeds
/// that -1 straight into the division — the tiny-value result this method
/// reproduces.</para>
///
/// <para>Strings: <c>NumberString</c> = <c>(int)(v + 0.5)</c>
/// (<c>engine/modeler.cpp:2006-2009</c>); <c>SwitchString</c> is an exact
/// float match (<c>engine/modeler.cpp:1369-1465</c>). The seconds field is
/// zero-padded when <c>max(9.4999, seconds) == 9.4999</c>; a non-positive
/// <c>allseconds</c> wraps the whole m:ss in parentheses; an exactly zero speed
/// difference or <c>|allseconds| &gt;= 1800</c> reads "infinite".</para>
/// </summary>
public static class TargetTti
{
    /// <summary>Native's literal for the two infinite branches (dialog.mdl:1319,1325).</summary>
    public const string Infinite = "infinite";

    /// <summary>
    /// HUD-18 — the m:ss strings native can produce: <c>abs &lt; 1800</c> bounds
    /// minutes to 0..29 and <c>min(mod,59)</c> bounds seconds to 0..59, so the
    /// cache is inherently tiny. Keyed by the ROUNDED (int) values that actually
    /// appear in the string, so a cached hit allocates nothing.
    /// </summary>
    private static readonly Dictionary<(int Minutes, int Seconds, bool PadZero, bool Parenthesised), string> _cache = new();

    /// <summary>
    /// <c>GHTTI</c> (<c>dialog.mdl:1323-1325</c>).
    /// </summary>
    /// <param name="range">Native <c>GetRange(Target)</c>: metres in the same
    /// cluster, else <c>-1</c> (<c>consoledata.cpp:182-199</c>).</param>
    /// <param name="mySpeed">Native <c>GetSpeed(Me)</c>: the own ship's
    /// <c>|velocity|</c>.</param>
    /// <param name="targetSpeed">Native <c>GetSpeed(Target)</c>: <c>|velocity|</c>
    /// for a visible dynamic model, <c>0</c> for a static one, <c>-1</c> when not
    /// seen (<c>consoledata.cpp:136-150</c>).</param>
    public static string Text(float range, float mySpeed, float targetSpeed)
    {
        // GHTTI = SwitchString(GetSpeed(Me) - GetSpeed(Target), <inner>, [(0, "infinite")])
        float speedDifference = mySpeed - targetSpeed;
        if (speedDifference == 0f)
            return Infinite;

        // GHTTI_allseconds = Divide(GetRange(Target), speedDifference)
        float allSeconds = range / speedDifference;

        // GHTTI_allseconds_Abs = Max(-allseconds, allseconds)
        float abs = MathF.Abs(allSeconds);

        // Inner SwitchString(Min(abs, 1800), posneg, [(1800, "infinite")]).
        if (MathF.Min(abs, 1800f) == 1800f)
            return Infinite;

        // GHTTI_Mod = Mod(abs, 60); GHTTI_Minutes = (abs - mod) / 60;
        // GHTTI_Seconds = Min(mod, 59).
        float mod = abs % 60f;
        float minutes = (abs - mod) / 60f;
        float seconds = MathF.Min(mod, 59f);

        // GHTTI_SecondString = SwitchString(Max(9.4999, seconds),
        //     ":" + NumberString(seconds), [(9.4999, ":0" + NumberString(seconds))])
        bool padZero = MathF.Max(9.4999f, seconds) == 9.4999f;

        // GHTTI_posneg = SwitchString(Max(0, allseconds), string, [(0, "(" + string + ")")])
        bool parenthesised = MathF.Max(0f, allSeconds) == 0f;

        int minuteText = (int)(minutes + 0.5f);   // NumberString(GHTTI_Minutes)
        int secondText = (int)(seconds + 0.5f);   // NumberString(GHTTI_Seconds)
        var key = (minuteText, secondText, padZero, parenthesised);
        // The cache is process-wide; a lock keeps concurrent callers (parallel
        // test classes, a second client instance) from corrupting the
        // Dictionary during a resize. Uncontended on the render thread.
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            string body = minuteText.ToString(CultureInfo.InvariantCulture)
                + ":" + (padZero ? "0" : "")
                + secondText.ToString(CultureInfo.InvariantCulture);
            string text = parenthesised ? "(" + body + ")" : body;
            _cache[key] = text;
            return text;
        }
    }
}
