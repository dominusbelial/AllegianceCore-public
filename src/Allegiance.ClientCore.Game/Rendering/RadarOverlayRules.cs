using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-12 — pure native rules for the radar overlay masks that were previously
/// unset because the port had no data source: command-chat subjects, drone
/// ripcord, and friendly ripcord seconds. Inputs are the same raw readings
/// <c>WinTrek/radarimage.cpp</c> uses, so the rules are unit-testable without a
/// graphics device or a live server.
/// </summary>
public static class RadarOverlayRules
{
    /// <summary><c>droneRipMaskIGC = 1024·coastButtonIGC = 0x1000</c>
    /// (<c>src/Igc/igc.h:821</c>; <see cref="ShipControlState.DroneRip"/>).</summary>
    public const int DroneRipMask = ShipControlState.DroneRip;

    /// <summary>Native pilot type <c>c_ptPlayer = 10</c> (<c>src/Igc/igc.h:685</c>).</summary>
    public const byte PilotTypePlayer = 10;

    /// <summary>Native <c>c_maskFlag</c> (<c>radarimage.cpp:643-645</c>): the
    /// model's <c>GetFlag()</c> is a real side id — not NA (-1, no flag) and not
    /// the <c>SIDE_TEAMLOBBY</c> artifact sentinel (-2).</summary>
    public static bool CarriedFlag(short carriedFlagSideId) =>
        carriedFlagSideId != -1 && carriedFlagSideId != -2;

    /// <summary>Native <c>c_maskArtifact</c> (<c>radarimage.cpp:643-645</c>):
    /// <c>GetFlag() == SIDE_TEAMLOBBY (-2)</c> marks the carried artifact.</summary>
    public static bool CarriedArtifact(short carriedFlagSideId) => carriedFlagSideId == -2;

    /// <summary>The native <c>GetPilotType()</c> reading the drone-rip gate uses.
    /// This client learns player identity from <c>PLAYERINFO(10)</c> only; a ship
    /// with no PlayerInfo is a server-spawned NPC, whose pilot type is below
    /// <c>c_ptPlayer</c> (drones/miners/builders/layers).</summary>
    public static byte PilotTypeOf(Core.Wire.PlayerInfo? player) =>
        player?.PilotType ?? 0;

    /// <summary>Native <c>c_maskRip</c> gate (<c>radarimage.cpp:999-1005</c>):
    /// a same-side ship whose <c>StateM</c> carries <c>droneRipMaskIGC</c> and
    /// whose <c>GetPilotType() &lt; c_ptPlayer</c> (a drone, miner, builder …;
    /// player-piloted ships are excluded). <paramref name="stateM"/> is the
    /// compact state mask already on the wire.</summary>
    public static bool DroneRipcord(int stateM, bool sameSide, byte pilotType) =>
        sameSide && (stateM & DroneRipMask) != 0 && pilotType < PilotTypePlayer;

    /// <summary>Native friendly ripcord seconds (<c>radarimage.cpp:1015-1030</c>):
    /// for a different same-side ship with an active ripcord,
    /// <c>(int)GetRipcordTimeLeft() + 1</c>, capped at 99999. Returns null when
    /// the ship is not a valid subject (self, other side, no countdown) or the
    /// countdown has already elapsed. <paramref name="durationSeconds"/> is the
    /// ship's own hull <c>RipcordSpeed</c> divided by its side's
    /// <c>c_gaRipcordTime</c> — exactly what the native client's remote handler
    /// seeds with <c>ResetRipcordTimeLeft()</c> (<c>clintlib/appmsg.cpp:966-969</c>,
    /// <c>shipIGC.cpp:4665</c>).</summary>
    public static int? FriendlyRipcordSeconds(bool isSelf, bool sameSide, bool active,
        long startedLocalMs, long nowLocalMs, float durationSeconds)
    {
        if (isSelf || !sameSide || !active || durationSeconds <= 0f) return null;
        float left = durationSeconds - (nowLocalMs - startedLocalMs) / 1000f;
        if (left < 0f) return null;
        int seconds = (int)left + 1;
        return seconds > 99999 ? 99999 : seconds;
    }

    /// <summary>Native subject brackets exist only when range labels are OFF —
    /// the command camera (<c>radarimage.cpp:570-574</c>: <c>psubjects</c> is
    /// assigned only in the <c>else</c> branch of <c>if (bRangeLabels)</c>) —
    /// and only while a chat is being composed.</summary>
    public static bool SubjectsApply(bool commandCamera, bool composing) =>
        !commandCamera ? false : composing;

    /// <summary>
    /// Native <c>c_maskSubject</c> (<c>radarimage.cpp:572-574,651</c>): a ship
    /// is a subject iff it is in the console's current chat-subject list
    /// (<c>GetConsoleImage()-&gt;GetSubjects()</c>, <c>console.cpp:2327-2334</c>),
    /// which is non-null only while a chat is being composed and whose contents
    /// are the recipients selected by <c>SetRecipient</c>
    /// (<c>console.cpp:269-455</c>). The port models those per-target rules:
    /// <list type="bullet">
    ///   <item><c>CHAT_TEAM</c> / <c>CHAT_ALLIES</c> — every ship on the
    ///   player's own side (<c>console.cpp:336-346</c>; the allied branch in
    ///   native walks the same pside ship list).</item>
    ///   <item><c>CHAT_EVERYONE</c> — every ship (<c>console.cpp:368-371</c>).</item>
    ///   <item><c>CHAT_WING</c> — own-side ships with the selected wing id
    ///   (<c>console.cpp:359-366,405-413</c>).</item>
    ///   <item><c>CHAT_FRIENDLY_SECTOR</c> — own-side ships last seen in the
    ///   chat cluster (<c>console.cpp:348-358,393-403</c>); the radar snapshot is
    ///   already sector-scoped, so <paramref name="sameSector"/> carries that.</item>
    ///   <item><c>CHAT_INDIVIDUAL</c>/<c>CHAT_GROUP</c> — not tracked by the
    ///   port's composer yet, so they contribute no subjects (never a fabricated
    ///   one).</item>
    /// </list>
    /// </summary>
    public static bool IsSubject(ChatTarget target, bool composing, bool sameSide,
        bool sameSector, short shipWingId, short myWingId)
    {
        if (!composing) return false;   // GetSubjects() is NULL unless composing (console.cpp:2327-2334)
        return target switch
        {
            ChatTarget.Team or ChatTarget.Allies => sameSide,
            ChatTarget.Everyone => true,
            ChatTarget.Wing => sameSide && shipWingId == myWingId,
            ChatTarget.FriendlySector => sameSide && sameSector,
            _ => false,   // Admin/Leaders/Individual/Group: no subject list modelled
        };
    }
}
