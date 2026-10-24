using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>P787b — the manned-turret turn cue's pure rules (SIM-0348).
/// Native loops the hull hardpoint's <c>turnSound</c> while the turret's gunner
/// is turning (<c>PlaySoundIf(m_vpTurretSound[mount],
/// m_pship-&gt;GetHullType()-&gt;GetHardpointData(mount).turnSound, …,
/// pweapon &amp;&amp; pweapon-&gt;GetGunner() &amp;&amp; (pweapon-&gt;fActive()
/// || pweapon-&gt;GetGunner() == trekClient.GetShip()) &amp;&amp;
/// TurnRate(pweapon-&gt;GetGunner()) &gt; 0.01)</c>, WinTrek/trekigc.cpp:988-996),
/// where <c>TurnRate(ship)</c> is the sum of the squares of the gunner's yaw/
/// pitch/roll control axes (trekigc.cpp:807-813). The port implements the
/// LOCAL-gunner half (gunner == own ship): the gunner's own controls are the
/// look axes the turret integrates. Native's own comment concedes remote
/// gunners' turret motion is only known while firing; the port's boarding wire
/// carries no remote turret state at all, so that half stays silent here (same
/// limit, documented in the P787b report).</summary>
public static class TurretTurnCue
{
    /// <summary>Native <c>TurnRate(pship) &gt; 0.01</c> (trekigc.cpp:813-816,
    /// applied at :996): the gunner is moving the turret.</summary>
    public static bool ShouldLoop(float yaw, float pitch, float roll) =>
        yaw * yaw + pitch * pitch + roll * roll > 0.01f;

    /// <summary>The hardpoint turnSound the port's gunner loop plays. The
    /// boarding wire carries no turret mount index, so the cue resolves the
    /// carrier hull's first non-fixed hardpoint with a non-NA turnSound —
    /// gunners man turrets, which are the non-fixed weapon hardpoints (native
    /// TeleportPaneModel's TurretSlots = MaxWeapons − MaxFixedWeapons,
    /// teleportpane.cpp:390). 0 (silence) when the hull is unknown or carries
    /// no turret hardpoint sound.</summary>
    public static short FirstTurretTurnSound(DataHullTypeIGC hull)
    {
        foreach (var hp in hull.Hardpoints)
            if (!hp.Fixed && hp.TurnSound != 0)
                return hp.TurnSound;
        return 0;
    }
}
