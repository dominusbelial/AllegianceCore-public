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
/// and the two command views) are a named, sizable future follow-up — out of
/// scope here.
/// </para>
/// Toggled with <c>V</c> in <see cref="Game1"/>, which cycles through the modes
/// available to the pilot's current state (the external-turret view is offered
/// only while actually gunning).
/// </summary>
public enum CameraMode
{
    Chase,
    Cockpit,

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
