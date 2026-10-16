using System.Collections.Generic;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P295 — the V-key camera-mode cycle, retained as a convenience alongside
/// P675's discrete native ViewMissile/ViewChase actions and the port ViewTurret
/// extension. This helper walks the modes available in the pilot's current
/// state; native's discrete toggles are handled separately (WinTrek.cpp:9820-9864).
/// </summary>
/// <remarks>
/// Mode order is <c>Cockpit → Chase → ExternalTurret → ExternalMissile →
/// ExternalFlyBy → ExternalFlyByTarget → ExternalOrbit → ExternalTarget →
/// ExternalBase → ExternalStation</c>, chosen so that pressing <c>V</c> walks
/// outward from the two long-standing views into the P295 views, then the
/// P580 native external modes (each degrading gracefully to a chase when its
/// framing object is absent, matching native). <c>ExternalOverride</c> is NOT
/// in the cycle — native treats it as a scripted, time-boxed camera an event
/// drops you into (WinTrek.cpp:6480-6534, 7673-7678), never a user-toggled
/// view. The external-turret view is meaningful only while the pilot is
/// actually gunning on a carrier's turret (native's own
/// <c>TK_ViewTurret</c> is commented-out dead code — see
/// <see cref="CameraMode.ExternalTurret"/>'s remarks), so it is offered only
/// when <paramref name="isTurretGunner"/> is true and is otherwise skipped. The
/// external-missile view is always offered (like native, it degrades gracefully
/// to a chase view when no missile is flying).
/// </remarks>
public static class CameraCycler
{
    /// <summary>The fixed order the <c>V</c> key walks.</summary>
    private static readonly CameraMode[] Order =
    {
        CameraMode.Cockpit,
        CameraMode.Chase,
        CameraMode.ExternalTurret,
        CameraMode.ExternalMissile,
        CameraMode.ExternalFlyBy,
        CameraMode.ExternalFlyByTarget,
        CameraMode.ExternalOrbit,
        CameraMode.ExternalTarget,
        CameraMode.ExternalBase,
        CameraMode.ExternalStation,
    };

    /// <summary>
    /// The next mode reached by one <c>V</c> press from
    /// <paramref name="current"/>, given the pilot's state.
    /// </summary>
    /// <param name="current">The currently active mode.</param>
    /// <param name="isTurretGunner">Whether the pilot is currently boarded as a
    /// gunner on a carrier's turret (makes <see cref="CameraMode.ExternalTurret"/>
    /// available; otherwise that mode is skipped).</param>
    /// <returns>The next available mode, wrapping around to the first. If the
    /// current mode is no longer available (e.g. the pilot left the turret while
    /// in the external-turret view), the cycle restarts at the first available
    /// mode rather than getting stuck.</returns>
    public static CameraMode Next(CameraMode current, bool isTurretGunner)
    {
        // Build the availability-filtered order for the pilot's current state.
        var available = new List<CameraMode>(Order.Length);
        foreach (var mode in Order)
        {
            if (mode == CameraMode.ExternalTurret && !isTurretGunner)
                continue;
            available.Add(mode);
        }

        int index = available.IndexOf(current);
        if (index < 0)
            return available[0];                       // stale/unavailable current -> restart
        return available[(index + 1) % available.Count];
    }
}
