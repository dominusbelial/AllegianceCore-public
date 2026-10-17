namespace Allegiance.Core.Sim;

/// <summary>
/// Native drone / constructor chatter SoundIDs and their audio stems.
/// The enum starts after lastDynamicSound = 1000, including removed entries
/// (~/Development/Allegiance/src/Igc/igc.h:456-464). P738 reconciles the P723
/// resolver and P725 DroneChatEvent producers with P732's native voice catalog.
/// The stems are the real waves native binds to each sound
/// (sounddef.mdl:731-753).
///
/// The server broadcasts these as TEAM CHAT carrying the SoundID as the
/// <c>VoiceOver</c>; the client resolves the id back to a clip so the chatter
/// is audible. A row in the parity matrix is <c>implemented</c> only when its
/// server trigger actually exists; several of these constants are resolved
/// here so the client is ready the moment the corresponding chat is sent.
/// </summary>
public static class DroneChatterCue
{
    // Constant drone-chatter SoundIDs (native Igc/sounds.h, enum order from 1001).
    /// <summary>droneEnemyOnScopeSound = 1323 (sounds.h:376) — "I've got enemies on my scope"
    /// → vo_miner_enemyonscope (sounddef.mdl:731).</summary>
    public const short DroneEnemyOnScopeSound = 1323;
    /// <summary>droneTooMuchDamageSound = 1327 (sounds.h:380) — "I don't get paid enough
    /// for this... returning to base" → vo_miner_dontgetpaid (sounddef.mdl:735).</summary>
    public const short DroneTooMuchDamageSound = 1327;
    /// <summary>droneWhereToSound = 1328 (sounds.h:381) — "No Helium in scan range" /
    /// "New miner requesting He3 asteroid." → vo_request_miner (sounddef.mdl:737).</summary>
    public const short DroneWhereToSound = 1328;
    /// <summary>droneComingHomeEmptySound = 1329 (sounds.h:382) — "Coming home w/o a full
    /// tank" → vo_sal_minerpartial (sounddef.mdl:738).</summary>
    public const short DroneComingHomeEmptySound = 1329;
    /// <summary>droneWhereToLayMinefieldSound = 1331 (sounds.h:384) — "New minefield
    /// requesting location." → vo_request_minefield (sounddef.mdl:740).</summary>
    public const short DroneWhereToLayMinefieldSound = 1331;
    /// <summary>droneWhereToLayTowerSound = 1332 (sounds.h:385) — "New tower requesting
    /// location." → vo_request_tower (sounddef.mdl:741).</summary>
    public const short DroneWhereToLayTowerSound = 1332;
    /// <summary>droneInTransitSound = 1337 (sounds.h:390) — "In transit"
    /// → vo_miner_intransit (sounddef.mdl:746).</summary>
    public const short DroneInTransitSound = 1337;
    /// <summary>droneTooCrowdedSound = 1342 (sounds.h:395) — "Enemy miners preventing me
    /// from mining" → vo_request_miner (sounddef.mdl:736 — the same wave native binds
    /// to droneTooCrowded and droneWhereTo).</summary>
    public const short DroneTooCrowdedSound = 1342;
    /// <summary>constructorRunningSound = 1343 (sounds.h:396) — "building underway"
    /// → vo_cover_builder (sounddef.mdl:753).</summary>
    public const short ConstructorRunningSound = 1343;

    /// <summary>Resolve a drone/constructor chatter SoundID to its audio stem,
    /// or <c>null</c> when the id is not one of this family (other families use the
    /// shared native voice catalog).</summary>
    public static string? Resolve(short soundId) => soundId switch
    {
        DroneEnemyOnScopeSound => "vo_miner_enemyonscope",
        DroneTooMuchDamageSound => "vo_miner_dontgetpaid",
        DroneWhereToSound => "vo_request_miner",
        DroneComingHomeEmptySound => "vo_sal_minerpartial",
        DroneWhereToLayMinefieldSound => "vo_request_minefield",
        DroneWhereToLayTowerSound => "vo_request_tower",
        DroneInTransitSound => "vo_miner_intransit",
        DroneTooCrowdedSound => "vo_request_miner",
        ConstructorRunningSound => "vo_cover_builder",
        _ => null,
    };
}
