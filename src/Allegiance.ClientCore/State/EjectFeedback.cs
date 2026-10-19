using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P578 — pure text production for the eject/death/rescue feedback lines,
/// a headless port of native's <c>appmsg.cpp</c> KILL_SHIP
/// (<c>FM_S_KILL_SHIP(105)</c>, appmsg.cpp:2797-2844) and PLAYER_RESCUED
/// (<c>FM_S_PLAYER_RESCUED(16)</c>, appmsg.cpp:311-340) handlers. Every rule
/// below is cited to native line numbers. Kept PURE (no world reads — the
/// caller supplies the resolved names/side data) so each (self/other,
/// killed/ejected/forced, rescued-by/you-rescued) case is unit-testable
/// headlessly, exactly the failing-first table the task asks for.
/// <para>
/// Critical vs non-critical routing mirrors native's <c>PostText(bCritical,…)</c>
/// first argument: the cases involving ME (<c>shipID == shipID</c> of this
/// client, or the credited killer is me) are critical; the observer lines for
/// other players are non-critical (appmsg.cpp:2812 vs 2801-2810; rescue:
/// "You rescued %s." non-critical at :324, "%s rescued you." critical at :338).
/// </para>
/// </summary>
public static class EjectFeedback
{
    /// <summary>
    /// Produce the tip line for an incoming <c>KillShip</c> (id 105) from this
    /// client's perspective. <paramref name="victimName"/> is the victim ship's
    /// display name; <paramref name="killerName"/> the credited killer's name
    /// (null when <paramref name="killer"/> id is NA/-1, so no launcher exists).
    /// Returns null when the event needs no HUD line for this receiver (never —
    /// every KILL_SHIP yields exactly one line, but the method stays total for
    /// symmetry). The associated camera-swing request is produced separately via
    /// <see cref="WantsOverrideToKiller"/>.
    /// </summary>
    public static FeedbackLine ForKillShip(
        KillShip m, short myShipId, string victimName, string? killerName)
    {
        // The receiver's own ship is the victim (appmsg.cpp:2812, 2834).
        if (m.ShipId == myShipId)
        {
            string text = m.BDeathCredit
                ? (killerName is not null ? $"You were killed by {killerName}" : "You were killed")
                : (killerName is not null ? $"You were forced to eject by {killerName}" : "You ejected");
            return new FeedbackLine(text, IsCritical: true, AtSeconds: 0);
        }

        // The credited killer (IdCredit) is this receiver's own ship — native
        // compares pmodelKiller == m_ship (appmsg.cpp:2824-2829).
        if (m.TypeCredit == (short)IgcObjectType.Ship && m.IdCredit == myShipId)
        {
            string text = m.BDeathCredit
                ? $"You killed {victimName}"
                : $"You forced {victimName} to eject";
            return new FeedbackLine(text, IsCritical: true, AtSeconds: 0);
        }

        // Observer: some other player was killed/ejected (appmsg.cpp:2801-2810,
        // the non-launcher forms "was killed"/"ejected").
        string obs = m.BDeathCredit ? $"{victimName} was killed" : $"{victimName} ejected";
        return new FeedbackLine(obs, IsCritical: false, AtSeconds: 0);
    }

    /// <summary>
    /// Whether this receiver's own ship warrants the "gloat camera" swing to the
    /// credited killer — native calls <c>EjectPlayer(launcher)</c> only when
    /// <c>pship == m_ship</c> (the victim IS me: appmsg.cpp:2900-2902 for death;
    /// clintlib.cpp:3063 for eject) and a launcher id is present. The port emits
    /// a request when the victim is me and a ship killer has an id other than NA
    /// (<see cref="KillShip.IdCredit"/>). Its camera request addresses a ship id, so non-ship
    /// launchers cannot be resolved through the ship-id namespace.
    /// </summary>
    public static bool WantsOverrideToKiller(KillShip m, short myShipId)
        => m.ShipId == myShipId && m.TypeCredit == (short)IgcObjectType.Ship && m.IdCredit != -1;

    /// <summary>
    /// Produce the tip line for an incoming <c>PlayerRescued</c> (id 16) from
    /// this client's perspective (appmsg.cpp:311-340). Rescuer gets the
    /// non-critical "You rescued %s."; the rescued pilot gets the critical
    /// "%s rescued you.". Names supplied by the caller.
    /// </summary>
    public static FeedbackLine? ForPlayerRescued(
        PlayerRescued m, short myShipId, string rescuerName, string rescueeName)
    {
        if (m.ShipIdRescuer == myShipId)
            // "You rescued %s." non-critical + rescuePlayerSound (appmsg.cpp:324-325)
            return new FeedbackLine($"You rescued {rescueeName}.", IsCritical: false, AtSeconds: 0,
                Extra: FeedbackExtraSound.RescuePlayer);
        if (m.ShipIdRescuee == myShipId)
            // "%s rescued you." critical + jumpSound (appmsg.cpp:337-339)
            return new FeedbackLine($"{rescuerName} rescued you.", IsCritical: true, AtSeconds: 0,
                Extra: FeedbackExtraSound.Jump);
        return null;   // neither endpoint is me — no line for this receiver
    }
}
