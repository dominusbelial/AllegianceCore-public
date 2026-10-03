namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P508 — the pure decision heart of the flight <c>SHIP_UPDATE(24)</c> send loop:
/// WHEN to emit a native update. Native's <c>BaseClient::SendUpdate</c> sends
/// exactly one ship update every <c>c_dsecUpdateClient</c> (0.15 s,
/// <c>Messages.h:226</c>, about 6.7 Hz), and — with <c>smSendUpdateNow</c>
/// commented out natively — does NOT send on control changes, only on that cadence.
/// This reproduces the cadence so the live <c>Game1</c> controller and a loopback
/// live test share one production timing rule. Each emission carries the controls
/// in effect at that instant, exactly as native.
/// </summary>
public sealed class ShipUpdateCadence
{
    /// <summary>Control cadence in seconds — native <c>c_dsecUpdateClient</c>
    /// (<c>Messages.h:226</c>).</summary>
    public const double CadenceSeconds = 0.15;

    private double _nextSendAtSeconds = double.MinValue;

    /// <summary>
    /// Whether a native SHIP_UPDATE is due this tick. Returns true once every
    /// <see cref="CadenceSeconds"/> (starting immediately after
    /// <see cref="Reset"/>). The caller still supplies the ship pose / controls at
    /// the time it sends.
    /// </summary>
    /// <param name="nowSeconds">Monotonic local seconds (<c>Game1._nowSeconds</c>).</param>
    public bool Update(double nowSeconds)
    {
        if (nowSeconds < _nextSendAtSeconds)
            return false;
        _nextSendAtSeconds = nowSeconds + CadenceSeconds;
        return true;
    }

    /// <summary>Reset the cadence (e.g. leaving a turret / resuming flight), so the
    /// next send fires immediately rather than waiting out a stale window.</summary>
    public void Reset() => _nextSendAtSeconds = double.MinValue;
}
