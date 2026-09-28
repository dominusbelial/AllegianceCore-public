namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// P416 — the pure decision behind the Promote-Turret seat keys
/// (TK_PromoteTurret1-4), kept out of Game1 so the key→mount mapping is
/// headless-unit-testable exactly like the flight-input translator and
/// <see cref="RipcordKey"/>. Mirrors the port's "plain data + a pure rule"
/// convention.
///
/// Native semantics (traced in P416_TURRET_SEAT_KEYS.md): the PromoteTurret
/// keys are the <b>captain</b> hand-off keys — a commanding ship promotes one
/// of its OWN boarded turret gunners into the command seat, demoting itself
/// into the exact turret mount the gunner vacated (<c>WinTrek.cpp:9583-9601</c>
/// builds <c>FM_C_PROMOTE(190)</c> with
/// <c>mountidPromoted = (key − TK_PromoteTurret1) + GetMaxFixedWeapons()</c>;
/// the server's <c>HandlePromote</c> / <c>Ship.Promote</c>, P140, performs the
/// in-place role swap). The mount id is indexed from the hull's fixed-weapon
/// count, so seat <c>N</c> (1-based) lives at hardpoint <c>MaxFixedWeapons + N−1</c>.
/// </summary>
public static class PromoteTurretKey
{
    /// <summary>
    /// Map a Promote-Turret seat number (1-based, the key's ordinal 1..4 in
    /// native's scheme) to the real turret-mount id on the ship's own hull.
    /// Returns <c>null</c> when the seat falls outside the hull's turret-slot
    /// range <c>[MaxFixedWeapons, MaxWeapons)</c> — i.e. the ship being flown
    /// has no such turret to promote from (a fighter with no turrets, or a
    /// seat past the last mount), in which case the caller sends nothing
    /// (native's own guard: <c>if (turret &lt; pht-&gt;GetMaxWeapons())</c>,
    /// <c>WinTrek.cpp:9593</c>).
    /// </summary>
    /// <param name="seat">1-based seat number (key 1 → seat 1).</param>
    /// <param name="maxFixedWeapons">The own hull's <c>MaxFixedWeapons</c> —
    /// the first turret hardpoint index.</param>
    /// <param name="maxWeapons">The own hull's <c>MaxWeapons</c> — exclusive
    /// upper bound of the turret range.</param>
    public static short? MountForSeat(int seat, short maxFixedWeapons, short maxWeapons)
    {
        if (seat < 1) return null;
        int mount = (seat - 1) + maxFixedWeapons;
        if (mount < maxFixedWeapons || mount >= maxWeapons) return null;
        return (short)mount;
    }
}
