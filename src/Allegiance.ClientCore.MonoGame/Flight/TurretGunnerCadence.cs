namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>The action a turret-gunner control frame should take this tick.</summary>
public enum TurretSendAction
{
    /// <summary>Not yet time to send (inside the control cadence window).</summary>
    None,

    /// <summary>Send <c>ACTIVE_TURRET_UPDATE(99)</c> — the fire button is held
    /// and a valid server-clock estimate is available to stamp the update.</summary>
    Active,

    /// <summary>Send <c>INACTIVE_TURRET_UPDATE(100)</c> — either not firing, or
    /// the clock is not yet known (an INACTIVE carries no timestamp, so it is
    /// always safe).</summary>
    Inactive,
}

/// <summary>
/// P464 — the pure decision heart of the turret-gunner send loop: WHEN to emit
/// a turret update and WHICH one. Native's <c>BaseClient::SendUpdate</c> sends
/// exactly one turret update every <c>c_dsecUpdateClient</c> (0.15 s) for a
/// parented (boarded) ship, choosing ACTIVE while <c>stateM &amp; weaponsMaskIGC</c>
/// else INACTIVE (<c>clintlib.cpp:1870-1905</c>). This reproduces that cadence
/// and branch so both the live <c>Game1</c> controller and the loopback live
/// test share one production timing rule (the former drives it from real input,
/// the latter from a simulated firing hold).
/// </summary>
public sealed class TurretGunnerCadence
{
    /// <summary>Control cadence in seconds — native <c>c_dsecUpdateClient</c>
    /// (<c>Messages.h:226</c>).</summary>
    public const double CadenceSeconds = 0.15;

    private double _nextSendAtSeconds = double.MinValue;

    /// <summary>
    /// Decide the send action for this tick. Called every flight frame while
    /// gunning; returns <see cref="TurretSendAction.None"/> until
    /// <see cref="CadenceSeconds"/> has elapsed since the last emission, then
    /// ACTIVE while the fire button is held (and a server clock is available —
    /// an ACTIVE update's timestamp must lie within the server's ±1.5 s gate),
    /// else INACTIVE (which carries no timestamp and is therefore always safe).
    /// </summary>
    /// <param name="nowSeconds">Monotonic local seconds (<see cref="Game1._nowSeconds"/>).</param>
    /// <param name="fireHeld">Whether the gunner's fire button is held this
    /// tick (native's <c>stateM &amp; weaponsMaskIGC</c>).</param>
    /// <param name="clockReady">Whether a server-clock estimate is available to
    /// stamp an ACTIVE update.</param>
    public TurretSendAction Update(double nowSeconds, bool fireHeld, bool clockReady)
    {
        if (nowSeconds < _nextSendAtSeconds)
            return TurretSendAction.None;
        _nextSendAtSeconds = nowSeconds + CadenceSeconds;
        return fireHeld && clockReady ? TurretSendAction.Active : TurretSendAction.Inactive;
    }

    /// <summary>Reset the cadence (e.g. when leaving the turret), so the next
    /// board starts sending immediately rather than waiting out a stale window.</summary>
    public void Reset() => _nextSendAtSeconds = double.MinValue;
}
