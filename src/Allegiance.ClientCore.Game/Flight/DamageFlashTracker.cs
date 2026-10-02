namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// Pure (MonoGame-free) logic for the combat damage flash on the local HUD —
/// a faithful C# port of native <c>WinTrekClient::DamageShipEvent</c> /
/// <c>CClientMfcView::SetTimeDamaged</c> / <c>GetFlash</c>
/// (WinTrek/trekigc.cpp:4270-4283, WinTrek/WinTrek.cpp:8922-8955), which arms a
/// 2-second on/off "flash" whenever the <em>local player's own ship</em> takes
/// real damage — now (P463) whether that damage lands on the hull OR is absorbed
/// by the shield (the shield-hit flash), matching native's <c>flAmount &gt; 0</c>
/// arm (trekigc.cpp:4281) which fires for shield-absorbed hits too.
///
/// This port has no discrete "you were just hit" wire event (the server-side
/// <c>IIgcSite.DamageShipEvent</c> seam is never raised by <c>Server.cs</c>), so
/// damage is inferred from the one cheap signal the client already receives every
/// tick: the local ship's real hull <em>and shield</em> fractions on the
/// <c>ServerShipUpdate</c> payload (the same fields already driving the HUD
/// bars). A hull value <em>or</em> shield value that <em>decreased</em> since the
/// previous frame is treated as "just took damage". Because only the local
/// ship's fractions are ever fed in here (<see cref="Game1"/> gates on
/// <c>world.MyShipId</c>), damage to any other ship structurally cannot trigger
/// the flash — matching native's <c>pShip == trekClient.GetShip()->GetSourceShip()</c>
/// gate (trekigc.cpp:4281).
/// </summary>
/// <remarks>
/// Timing model follows native exactly: on a hull decrease, <see cref="Arm"/>
/// records the hit instant; <see cref="IsFlashing"/> then returns true for the
/// first <see cref="FlashOn"/> seconds, false for the next <see cref="FlashOff"/>,
/// and so on until the whole <see cref="FlashingDuration"/> window expires.
/// Re-arming while still flashing preserves native's boundary-fudging
/// (<c>SetTimeDamaged</c>, WinTrek.cpp:8924-8932) — it snaps the recorded hit
/// instant forward to the most recent on/off boundary rather than unconditionally
/// restarting from "now", keeping the phase continuous and (because the re-arm
/// can fall before "now") bounding how much a rapid re-hit extends the window.
/// This faithful port was chosen over the task's permitted "just restart the 2s
/// window from now" simplification: it is barely more code, keeps every stage of
/// the flash aligned to the same 0.25s grid as native, and is trivially
/// deterministic to test.
///
/// Timeline is supplied by the caller as <paramref name="now"/> in seconds on a
/// monotonically-increasing clock (<see cref="Game1._nowSeconds"/>). A tracker
/// that has never taken a hit reports no flash, so the very first frame after
/// connecting (no "previous frame" baseline yet) cannot false-trigger.
/// </remarks>
public sealed class DamageFlashTracker
{
    /// <summary>Half-cycle the flash is lit. <c>c_dtFlashOn</c>, WinTrek.cpp:35.</summary>
    public const float FlashOn = 0.25f;

    /// <summary>Half-cycle the flash is dark. <c>c_dtFlashOff</c>, WinTrek.cpp:36.</summary>
    public const float FlashOff = 0.25f;

    /// <summary>Total window the flash cycles for after a hit.
    /// <c>c_dtFlashingDuration</c>, WinTrek.cpp:37.</summary>
    public const float FlashingDuration = 2.0f;

    /// <summary>
    /// Minimum hull OR shield decrease (as a 0..1 fraction) that counts as "took
    /// damage". Chosen well below any single weapon hit's fraction on either
    /// gauge (a scout gatling round is several percent of hull) yet ~10⁴× above
    /// the float precision of the transmitted fraction, so it rejects inter-frame
    /// rounding/jitter noise while never swallowing a real hit. Not a native
    /// constant — native fires off the discrete <c>flAmount &gt; 0</c> event
    /// (trekigc.cpp:4281); this threshold is this port's own noise guard for the
    /// hull/shield-decrease inference.
    /// </summary>
    public const float HullDiffEpsilon = 0.002f;

    /// <summary>P463 — the same noise guard as <see cref="HullDiffEpsilon"/>,
    /// applied to the shield fraction: a shield drop that small is jitter, not a
    /// hit; anything larger is a real shield-absorbed hit that deserves the
    /// flash (native arms on any <c>flAmount &gt; 0</c>, trekigc.cpp:4281).</summary>
    public const float ShieldDiffEpsilon = 0.002f;

    // Native m_timeLastDamage (WinTrek.h) — the instant the flash window began.
    // NegativeInfinity = "never taken a hit", so IsFlashing() is false before
    // the first Arm and Arm() correctly treats the first hit as a cold start.
    private float _timeLastDamage = float.NegativeInfinity;

    private float _lastHullFraction;
    private float _lastShieldFraction;
    private bool _hasBaseline;

    /// <summary>
    /// Feed the local ship's hull <em>and shield</em> fractions once per frame.
    /// Detects a decrease in either since the previous call (and thus damage),
    /// (re)arms the flash window via <see cref="Arm"/>, records the new
    /// baselines, and returns whether the HUD should be flashing at
    /// <paramref name="now"/>. The first call establishes the baselines without
    /// flashing (no "previous frame" to compare yet).
    /// </summary>
    public bool Update(float now, float currentHullFraction, float currentShieldFraction)
    {
        if (_hasBaseline
            && (HullDecreased(_lastHullFraction, currentHullFraction)
                || ShieldDecreased(_lastShieldFraction, currentShieldFraction)))
            Arm(now);

        _lastHullFraction = currentHullFraction;
        _lastShieldFraction = currentShieldFraction;
        _hasBaseline = true;
        return IsFlashing(now);
    }

    /// <summary>
    /// Whether the hull fell by more than <see cref="HullDiffEpsilon"/> between
    /// two consecutive samples. A <see langword="null"/> previous sample (no
    /// baseline yet) never reports a decrease — the first-frame guard.
    /// </summary>
    public static bool HullDecreased(float? previous, float current) =>
        previous is { } p && current < p - HullDiffEpsilon;

    /// <summary>
    /// P463 — whether the shield fell by more than <see cref="ShieldDiffEpsilon"/>
    /// between two consecutive samples (a shield-absorbed hit): the flash's new
    /// shield arm. The same first-frame guard and noise threshold as
    /// <see cref="HullDecreased"/>.
    /// </summary>
    public static bool ShieldDecreased(float? previous, float current) =>
        previous is { } p && current < p - ShieldDiffEpsilon;

    /// <summary>
    /// Record that the local ship just took damage at <paramref name="now"/>,
    /// starting (or extending) the flash window — the port of native
    /// <c>SetTimeDamaged</c> (WinTrek.cpp:8922-8935).
    /// </summary>
    public void Arm(float now)
    {
        if (now <= _timeLastDamage + FlashingDuration)
        {
            // Already hit and flashing — fudge the recorded hit instant forward
            // to the most recent on/off boundary (native keeps the phase on the
            // same 0.25s grid rather than snapping to "now").
            float dt = now - _timeLastDamage;
            float n = MathF.Floor(dt / (FlashOn + FlashOff));
            _timeLastDamage += n * (FlashOn + FlashOff);
        }
        else
        {
            _timeLastDamage = now; // start flashing now
        }
    }

    /// <summary>
    /// Whether the flash is currently lit at <paramref name="now"/> — the port of
    /// native <c>GetFlash</c> (WinTrek.cpp:8937-8955): within the flash window,
    /// lit for <see cref="FlashOn"/> then dark for <see cref="FlashOff"/>,
    /// repeating until the window expires.
    /// </summary>
    public bool IsFlashing(float now)
    {
        if (now > _timeLastDamage + FlashingDuration)
            return false;

        float dt = now - _timeLastDamage;
        float n = MathF.Floor(dt / (FlashOn + FlashOff));
        float timeStartFlash = _timeLastDamage + n * (FlashOn + FlashOff);
        return now <= timeStartFlash + FlashOn;
    }
}
