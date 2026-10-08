namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// The active view mode for the local pilot.
/// <para>
/// <see cref="Chase"/> is the original third-person view (and remains the
/// default at startup, so anyone not pressing the toggle key sees exactly the
/// pre-P211 behaviour); <see cref="Cockpit"/> is the first-person view added in
/// P211, where you sit inside the ship looking along its real forward direction
/// and your own hull is (correctly) not drawn.
/// </para>
/// <para>
/// P295 adds the two most valuable of native's other view modes (native's real
/// <c>CameraMode</c> enum has 14 values — <c>WinTrek/Treki.h:108-126</c> — this
/// port previously had just these two):
/// <see cref="ExternalTurret"/>, a dedicated view that follows the ship whose
/// turret you have boarded as a gunner; and <see cref="ExternalMissile"/>, a
/// view that watches a just-fired missile in flight (native's
/// <c>cmExternalMissile</c>).
/// </para>
/// <para>
/// The remaining ten native modes (<c>cmExternalTarget</c>,
/// <c>cmExternalBase</c>, <c>cmExternalOrbit</c>, <c>cmExternalStation</c>,
/// <c>cmExternalFlyBy</c>, <c>cmExternalFlyByTarget</c>, <c>cmExternalOverride</c>
/// and the two command views) were a named future follow-up when P295 wrote
/// this. P580 ports seven of them — Target, Base, Orbit, Station, FlyBy,
/// FlyByTarget and Override — each with native placement maths and native
/// default keys (the two command views remain out of scope).
/// </para>
/// Toggled with <c>V</c> in <see cref="Game1"/>, which cycles through the modes
/// available to the pilot's current state (the external-turret view is offered
/// only while actually gunning), and individually with native's default F-key
/// bindings (see <see cref="CameraCycler"/>).
/// </summary>
public enum CameraMode
{
Chase,
Cockpit,

/// <summary>
/// P580 — <c>cmExternalFlyBy</c> (native default <c>TK_ViewFlyby</c>, igc.h:254,
/// bound to F5): aim the camera at the midpoint of you and the current target,
/// back off along the camera's backward axis by 1.25 × max(dTarget, dMe) with
/// the native perpendicular-side fudge (WinTrek.cpp:7137-7164). Falls back to
/// a chase view when there is no current target (native's degenerate case,
/// WinTrek.cpp:7165-7170).
/// </summary>
ExternalFlyBy,

/// <summary>
/// P580 — native declares <c>cmExternalFlyByTarget</c> but the camera switch has
/// no <c>case</c> for it, so it falls to the generic external branch (a fixed
/// distance behind the freely-rotating external camera; WinTrek.cpp:7315-7320
/// default). This port reproduces that: a generic external view framing the
/// current target with the free external camera.
/// </summary>
ExternalFlyByTarget,

/// <summary>
/// P580 — <c>cmExternalOrbit</c> (native default <c>TK_ViewExternalOrbit</c>,
/// igc.h:262, bound to F4): the free external camera rotates at fixed rates
/// (<c>Pitch(dt·0.35), Yaw(dt·0.2), Roll(dt·0.5)</c>) around the ship at offset
/// <c>5 + 2.5 × HullLength</c> (WinTrek.cpp:7187-7196).
/// </summary>
ExternalOrbit,

/// <summary>
/// P580 — <c>cmExternalTarget</c> (native default <c>TK_ViewTarget</c>, igc.h:256,
/// bound to F7): frames the current target at offset <c>5 + 2 × targetRadius</c>
/// along the free external camera's backward (WinTrek.cpp:7099-7111). Requires a
/// current target; falls back to generic external (WinTrek.cpp:7112).
/// </summary>
ExternalTarget,

/// <summary>
/// P580 — <c>cmExternalBase</c> (native default <c>TK_ViewBase</c>, igc.h:258,
/// bound to F10): native declares it but has no <c>case</c> in the camera switch
/// (WinTrek.cpp:7315-7320 default), so it is the generic external view — here
/// framing the home base if one is known, else the generic external view of the
/// ship.
/// </summary>
ExternalBase,

/// <summary>
/// P580 — <c>cmExternalStation</c> (native default <c>TK_ViewExternalStation</c>,
/// igc.h:263, bound to F11): tracks the current target (a station in the same
/// cluster you can see) with native's TrackCamera at <c>5 + 2.5 × shipRadius</c>;
/// falls back to a chase view with no visible station target
/// (WinTrek.cpp:7209-7227).
/// </summary>
ExternalStation,

/// <summary>
/// P580 — <c>cmExternalOverride</c>: the scripted camera an event (launch, the
/// P578-style eject swing to the killer, cinematic) drops you into for a few
/// seconds, tracking a fixed world position with TrackCamera
/// (WinTrek.cpp:7199-7206, 6480-6534) then returning to a normal mode after
/// <c>m_timeOverrideStop</c> (WinTrek.cpp:7673-7678). The port reuses this for
/// the P578 eject swing and the director's cut-in shots.
/// </summary>
ExternalOverride,


    /// <summary>
    /// P295 — the external turret view: an outward camera that follows the ship
    /// the local pilot has boarded as a gunner. Native declares this mode
    /// (<c>cmExternalTurret</c>, Treki.h:122) and a toggle key for it
    /// (<c>TK_ViewTurret</c>, igc.h:258), but the native handling is commented
    /// out ("This camera mode is not actually handled by the viewing code",
    /// WinTrek.cpp:9856-9863) and no <c>case cmExternalTurret</c> exists in the
    /// camera-update switch (it falls to the <c>default</c>/external branch).
    /// This port therefore defines the third-person behavior itself (clearly
    /// labeled): an external chase view centred on the carrier — the ship whose
    /// turret the pilot occupies (the port's existing turret-boarding state,
    /// <c>ShipEntity.ParentId</c> ≠ NA). Offered in the <c>V</c> cycle only
    /// while the pilot is actually a gunner.
    /// </summary>
    ExternalTurret,

    /// <summary>
    /// P295 — the external missile view: watches a just-fired missile in flight,
    /// the port of native's <c>cmExternalMissile</c>. Native activates it with a
    /// dedicated view key (<c>TK_ViewMissile</c>, igc.h:255 /
    /// WinTrek.cpp:9820-9824) and each frame tracks the pilot's last-fired
    /// missile via <c>TrackCamera</c> (WinTrek.cpp:7237-7268, 6848-6878),
    /// keeping both the missile and its target (or the ship it was fired from)
    /// in frame, falling back to a chase view when no missile is flying
    /// (WinTrek.cpp:7259-7268). This port's client has no missile target on the
    /// wire, so it uses the fallback branch: it frames the latest live missile
    /// in the pilot's sector alongside the local ship.
    /// </summary>
    ExternalMissile,
}
