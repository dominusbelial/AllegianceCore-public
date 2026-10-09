using Allegiance.Core.Wire;
using Allegiance.ClientCore.Flight;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P414 — the pure decision behind the ripcord key (TK_Ripcord), kept out of
/// Game1 so the key's semantics are headless-unit-testable exactly like the
/// flight-input translator. Mirrors the port's "plain data + a pure rule"
/// convention (<see cref="FlightInput"/>, <see cref="AutopilotSteering"/>).
/// </summary>
public static class RipcordKey
{
    /// <summary>
    /// Decide what <c>sidRipcord</c> the ripcord key should send for the current
    /// context — the same order <c>RequestRipcordCore</c> (native
    /// <c>FedSrv.CPP:13319-13378</c>) resolves them:
    /// <list type="number">
    /// <item><b>Active ripcord</b> → <see cref="Wire.RipcordRequest.NA"/> (cancel):
    /// pressing the key while a countdown is running aborts it (native treats a
    /// cancel as the same command, and this port's key doubles as the cancel
    /// key — see the P414 report).</item>
    /// <item><b>No active ripcord + a sector selected</b> → that sector id
    /// (initiate the countdown toward the starmap-selected destination,
    /// reusing <c>Game1._selectedSectorId</c> from P403).</item>
    /// <item><b>Neither</b> → <c>null</c>: no wire message. Nothing to cancel,
    /// no destination picked. The caller surfaces a "select a sector first"
    /// hint instead of sending a meaningless request (sending NA here would be
    /// a benign server no-op anyway, so the hint is strictly more informative).</item>
    /// </list>
    /// </summary>
    /// <param name="ripcordActive">True when the local player's own ship
    /// currently has a ripcord countdown (<see cref="State.ClientWorld.RipcordActive"/>).</param>
    /// <param name="selectedSectorId">The sector picked on the expanded starmap,
    /// or null when none is selected (<c>Game1._selectedSectorId</c>).</param>
    /// <returns>The <c>sidRipcord</c> to send, or <c>null</c> to send nothing.</returns>
    public static short? RequestFor(bool ripcordActive, short? selectedSectorId)
    {
        if (ripcordActive) return RipcordRequest.NA;
        return selectedSectorId;
    }
}
