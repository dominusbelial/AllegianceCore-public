namespace Allegiance.Core.Sim;

/// <summary>
/// P723 — the constant SoundIDs for the native drone / constructor chatter
/// voices and their audio stems. These are <b>compile-time enum constants</b>
/// in native (<c>DEFSOUND(...)</c> entries in <c>src/Igc/sounds.h</c> expanded
/// by the <c>enum { ... }</c> in <c>igc.h:455-456</c>, giving sequential
/// SoundID values from 0), not core data fields. The values below were
/// recomputed by counting every active <c>DEFSOUND</c>/<c>DEFEMPTYSOUND</c>
/// entry in file order (the same method P459 used for
/// <c>DroneWhereToSound = 327</c>; the port sends them verbatim as the wire
/// <c>ChatMessage.VoiceOver</c>, see <c>Server.Economy.BroadcastTeamChat</c>).
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
    // Constant drone-chatter SoundIDs (native Igc/sounds.h, enum order from 0).
    /// <summary>droneEnemyOnScopeSound = 322 (sounds.h:376) — "I've got enemies on my scope"
    /// → vo_miner_enemyonscope (sounddef.mdl:731).</summary>
    public const short DroneEnemyOnScopeSound = 322;
    /// <summary>droneTooMuchDamageSound = 326 (sounds.h:380) — "I don't get paid enough
    /// for this... returning to base" → vo_miner_dontgetpaid (sounddef.mdl:735).</summary>
    public const short DroneTooMuchDamageSound = 326;
    /// <summary>droneWhereToSound = 327 (sounds.h:381) — "No Helium in scan range" /
    /// "New miner requesting He3 asteroid." → vo_request_miner (sounddef.mdl:737).</summary>
    public const short DroneWhereToSound = 327;
    /// <summary>droneComingHomeEmptySound = 328 (sounds.h:382) — "Coming home w/o a full
    /// tank" → vo_sal_minerpartial (sounddef.mdl:738).</summary>
    public const short DroneComingHomeEmptySound = 328;
    /// <summary>droneWhereToLayMinefieldSound = 330 (sounds.h:384) — "New minefield
    /// requesting location." → vo_request_minefield (sounddef.mdl:740).</summary>
    public const short DroneWhereToLayMinefieldSound = 330;
    /// <summary>droneWhereToLayTowerSound = 331 (sounds.h:385) — "New tower requesting
    /// location." → vo_request_tower (sounddef.mdl:741).</summary>
    public const short DroneWhereToLayTowerSound = 331;
    /// <summary>droneInTransitSound = 336 (sounds.h:390) — "In transit"
    /// → vo_miner_intransit (sounddef.mdl:746).</summary>
    public const short DroneInTransitSound = 336;
    /// <summary>droneTooCrowdedSound = 341 (sounds.h:395) — "Enemy miners preventing me
    /// from mining" → vo_request_miner (sounddef.mdl:736 — the same wave native binds
    /// to droneTooCrowded and droneWhereTo).</summary>
    public const short DroneTooCrowdedSound = 341;
    /// <summary>constructorRunningSound = 342 (sounds.h:396) — "building underway"
    /// → vo_cover_builder (sounddef.mdl:753).</summary>
    public const short ConstructorRunningSound = 342;

    /// <summary>Resolve a drone/constructor chatter SoundID to its audio stem,
    /// or <c>null</c> when the id is not one of this family (falls through to
    /// the quick-chat / station-attack resolvers).</summary>
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
