namespace Allegiance.ClientCore.State;

/// <summary>
/// P267 — a one-shot sound event the client world has observed on the wire,
/// recorded for <c>Game1</c> to drain into the audio manager. Currently the
/// station destroyed/captured notices and the development-completion chime —
/// the real triggers native wires to <c>DestroyedSound</c>/<c>CapturedSound</c>
/// (<c>clintlib/appmsg.cpp:548-551, 674-677</c>) and a development's
/// <c>CompletionSound</c> (<c>appmsg.cpp:3326</c>).
/// </summary>
public enum ClientSoundEventKind
{
    /// <summary>CHATMESSAGE voice takes precedence over plain chat cues
    /// (trekigc.cpp:4530-4540). Id is voiceOver, Voice is its native template.</summary>
    ChatVoiceOver,
    /// <summary>An own/friendly station was destroyed (STATION_DESTROYED, 59).</summary>
    StationDestroyed,

    /// <summary>An enemy station was destroyed (STATION_DESTROYED, 59).</summary>
    StationEnemyDestroyed,

    /// <summary>An own/friendly station was captured (STATION_CAPTURE, 58).</summary>
    StationCaptured,

    /// <summary>An enemy station was captured (STATION_CAPTURE, 58).</summary>
    StationEnemyCaptured,

    /// <summary>A development completed (DEV_COMPLETED, 8).</summary>
    DevCompleted,

    /// <summary>P414 — this ship's ripcord countdown was aborted
    /// (RIPCORD_ABORTED, 187 — our cancel request or a mid-countdown legality
    /// failure). Queued only when the aborted ship is the local player's own,
    /// so Game1 plays the single "ripcord aborted" VO cue exactly once.</summary>
    RipcordAborted,

    /// <summary>P638 — this ship's teleport dock request was accepted
    /// (TELEPORT_ACK, 102, with a real station id): the player relocated to a
    /// station and native plays the <c>personalJumpSound</c> (appmsg.cpp:1901).
    /// Mapped to the <c>jumpin</c> one-shot, the same wave native's jumpSound
    /// plays.</summary>
    TeleportArrived,

    /// <summary>Native newCommandMsgSound, appmsg.cpp:1757.</summary>
    SectorHighlighted,

    /// <summary>POSITIONREQ: salPlayerWaitingSound (appmsg.cpp:2481).</summary>
    PlayerWaiting,

    // ── P710 (parity wave 13: simulation sounds) ─────────────────────────────
    // Wealth / treasure announcements (clintlib/appmsg.cpp). Every trigger is
    // the corresponding wire message the server already broadcasts.

    /// <summary>PAYDAY(114): <c>paydaySound</c> (appmsg.cpp:1313; wave
    /// <c>payday</c>, sounddef.mdl:294).</summary>
    PaydayReceived,

    /// <summary>ACQUIRE_TREASURE(162): <c>pickUpPowerupSound</c> for a
    /// <c>c_tcPowerup</c> pickup (appmsg.cpp:282; wave <c>PowerupShield</c>,
    /// sounddef.mdl:280).</summary>
    PickupPowerup,

    /// <summary>ACQUIRE_TREASURE(162): <c>pickUpDevelopmentSound</c> for a
    /// <c>c_tcDevelopment</c> pickup, and — NYI in native — a <c>c_tcFlag</c>
    /// pickup reuses the same development cue (appmsg.cpp:286,303; wave
    /// <c>PowerupDevelopment</c>, sounddef.mdl:279).</summary>
    PickupDevelopment,

    /// <summary>ACQUIRE_TREASURE(162): <c>pickUpCashSound</c> for a
    /// <c>c_tcCash</c> pickup (appmsg.cpp:291; wave <c>hoover</c>,
    /// sounddef.mdl:281).</summary>
    PickupCash,

    // Incoming-message notification cues (WinTrek/trekigc.cpp:4536-4560),
    // classified on the CHATMESSAGE(23) attributes.

    /// <summary><c>newCommandMsgSound</c> — a message with a real command id
    /// (trekigc.cpp:4540; wave <c>receivedobjective</c>, sounddef.mdl:300).</summary>
    ChatCommand,

    /// <summary><c>newChatMsgFromCommanderSound</c> — a personal message from
    /// the team leader (training-commander case, trekigc.cpp:4546) or a
    /// leader-sent general message (trekigc.cpp:4555; wave
    /// <c>receivedcomchat</c>, sounddef.mdl:298).</summary>
    ChatCommander,

    /// <summary><c>newPersonalMsgSound</c> — an individual message addressed to
    /// the local pilot (trekigc.cpp:4548; wave <c>receivepersonalchat</c>,
    /// sounddef.mdl:301).</summary>
    ChatPersonal,

    /// <summary><c>newChatMsgSound</c> — a regular (non-command, non-personal)
    /// chat line (trekigc.cpp:4557; wave <c>receivechat</c>, sounddef.mdl:297).</summary>
    ChatRegular,

    /// <summary><c>newOfflineChatMsgSound</c> — a general chat line received
    /// while disconnected (trekigc.cpp:4560; wave <c>receivechat</c> at reduced
    /// gain, sounddef.mdl:299). Never reachable in this always-online port;
    /// present for native completeness.</summary>
    ChatOffline,

    // ── P711 (parity wave 14: simulation sounds, second bundle) ─────────────
    // Ship status / combat feedback cues from the client-sim sound region
    // (WinTrek/trekigc.cpp:1264-1293) and the ripcord-deny wire path. The four
    // low alarms and the two "no ammo / no fuel" attempt cues are one-shot
    // StartSound one-shots edge-triggered on the LOCAL ship's own streamed
    // fractions; the ripcord/mining cues are loop gates driven by
    // ClientWorld.RipcordActive / MiningAsteroidId (handled as loops in
    // SoundManager, not one-shot events — see Game1 per-frame drivers).

    /// <summary><c>hullLowSound</c> — local hull fraction fell from ≥.15 to
    /// <.15 (trekigc.cpp:1264; wave <c>vo_sal_hullcritical</c>, sounddef.mdl:374).</summary>
    HullLow,

    /// <summary><c>ammoLowSound</c> — local ammo fraction fell from ≥.15 to
    /// <.15 (trekigc.cpp:1266; wave <c>outofammo</c>, sounddef.mdl:371).</summary>
    AmmoLow,

    /// <summary><c>fuelLowSound</c> — local fuel fraction fell from ≥.15 to
    /// <.15 (trekigc.cpp:1268; wave <c>fuellow</c>, sounddef.mdl:372).</summary>
    FuelLow,

    /// <summary><c>energyLowSound</c> — local energy fraction fell from ≥.05
    /// to <.05 (trekigc.cpp:1270; wave <c>vo_sal_lowenergy</c>, sounddef.mdl:373).</summary>
    EnergyLow,

    /// <summary><c>salNoAmmoSound</c> — the pilot tried to fire an ammo weapon
    /// with insufficient ammunition, edge false→true (trekigc.cpp:1273; wave
    /// <c>vo_sal_noammo</c>, sounddef.mdl:812).</summary>
    NoAmmo,

    /// <summary><c>salNoFuelSound</c> — the pilot tried to use the afterburner
    /// with no fuel, edge false→true (trekigc.cpp:1275; wave
    /// <c>vo_sal_nofuel</c>, sounddef.mdl:811).</summary>
    NoFuel,

    /// <summary><c>salNoRipcordSound</c> — a ripcord was denied, either by the
    /// server (FM_S_RIPCORD_DENIED, appmsg.cpp:970) or by the local no-legal-
    /// anchor check (clintlib.h:1175); wave <c>vo_sal_noripcord</c>,
    /// sounddef.mdl:348.</summary>
    RipcordDenied,

    // ── P714 (parity wave 15: leadership / investment / objective / autonomy
    // feedback) ────────────────────────────────────────────────────────────
    // The 5 objective & status announce VOs are wire-driven one-shots on
    // fed messages the server already broadcasts (SET_TEAM_LEADER, AUTODONATE,
    // GAIN_FLAG). The 8 autonomy rows (autopilot engage/disengage, rescue/
    // teleport jump) were already built in earlier waves but sat stale
    // "missing" in the matrix — this wave verifies and reconciles them.

    /// <summary><c>commanderSound</c> — this player became the team leader
    /// (FM_S_SET_TEAM_LEADER announcing our own ship; appmsg.cpp:2965; wave
    /// <c>vo_sal_madecommander</c>, sounddef.mdl:779).</summary>
    CommanderElected,

    /// <summary><c>investorSound</c> — a teammate began donating income to us
    /// and the donor count rose to exactly 1 (FM_S_AUTODONATE, appmsg.cpp:1238;
    /// wave <c>vo_sal_madeinvestor</c>, sounddef.mdl:780).</summary>
    InvestorReceived,

    /// <summary><c>enemyFlagLostSound</c> — a teammate on our side picked up /
    /// is carrying an enemy (or neutral-non-lobby) flag (FM_S_GAIN_FLAG,
    /// appmsg.cpp:3149; wave <c>vo_stolen_enemyflag</c>, sounddef.mdl:773).</summary>
    EnemyFlagStolen,

    /// <summary><c>artifactFoundSound</c> — a teammate picked up a neutral
    /// artifact (FM_S_GAIN_FLAG with a lobby flag origin, appmsg.cpp:3156;
    /// wave <c>vo_sal_artifact</c>, sounddef.mdl:776).</summary>
    ArtifactFound,

    /// <summary><c>flagLostSound</c> — an enemy picked up / is carrying OUR
    /// side's flag (FM_S_GAIN_FLAG whose flag origin is our side,
    /// appmsg.cpp:3169; wave <c>vo_stolen_flag</c>, sounddef.mdl:771).</summary>
    OurFlagStolen,

    // ── P716 (parity wave 16: combat & physics feedback one-shots) ─────────
    // The own ship's warp TRANSIT (leave one sector / emerge in the next)
    // plays the <c>jumpSound</c> cue. Native fires it both on departure
    // (trekigc.cpp:3998, <c>pship->SetCluster</c>) and on arrival beside the
    // destination aleph (trekigc.cpp:4415); this pure-renderer client observes
    // the transit as the own ship's SET_CLUSTER landing in a new sector beside
    // its aleph (the P585 aleph-emergence edge), and plays the same
    // <c>jumpSound</c> (wave <c>jumpin</c>, SoundManager.PlayJump) once per
    // warp crossing. This is the same wave native's jumpSound plays.</summary>
    SectorJump,

    // ── P717 (parity wave 17: ordnance deploy & own-ship shield status) ────
    // The deploy one-shots ride the expendable-deploy wire (native
    // <c>BaseClient::FireExpendable</c>, clintlib.cpp:3245-3257) gated to the
    // LOCAL ship's own deploy (<c>pShip == m_ship</c>); the shield-offline
    // cue is a one-shot on the local ship's own shield fraction collapsing
    // (native CshieldIGC::ApplyDamage, shieldIGC.cpp:185-186).

    /// <summary><c>deployMineSound</c> — the LOCAL ship deployed a mine
    /// (clintlib.cpp:3253, <c>if (pShip == m_ship)</c>; wave <c>dropmine</c>,
    /// sounddef.mdl:368). Detected on the own ship's FM_S_EXPORT(26)/OT_mine
    /// when the mine's <c>LauncherId</c> is the local ship.</summary>
    MineDeployed,

    /// <summary><c>deployChaffSound</c> — the LOCAL ship deployed chaff
    /// (clintlib.cpp:3249, <c>if (pShip == m_ship)</c>; wave <c>dropobject</c>,
    /// sounddef.mdl:367). Detected on the own ship's FM_S_FIRE_EXPENDABLE(74)
    /// when the launcher is the local ship and the equipment type is chaff.</summary>
    DeployChaff,

    /// <summary><c>salShieldsOfflineSound</c> — the local ship's shield was
    /// knocked offline (overload damage, shieldIGC.cpp:186; wave
    /// <c>vo_sal_shieldsoffline</c>, sounddef.mdl:791). Detected on the local
    /// ship's own streamed <c>ShieldFraction</c> falling from online to ≈0.</summary>
    ShieldsOffline,

    // ── P735 (parity wave 22: own-ship state notification one-shots) ───────

    /// <summary><c>cloak engage</c> — the local ship's cloak became active
    /// (native CloakActive state bit false→true, Igc/shipIGC.cpp:1608; wave
    /// <c>vo_sal_cloakengage</c>, sounddef.mdl:807). A local one-shot on the
    /// own ship's streamed <c>StateM</c> CloakActive bit, exactly once per
    /// engagement.</summary>
    CloakEngaged,

    /// <summary><c>pickUpPartSound</c> — the local ship picked up a part
    /// (native server-side shipIGC.h:1307; wave <c>hoover</c>, sounddef.mdl:278).
    /// Native plays this at the acquiring ship via the server-side
    /// <c>PlayNotificationSound</c> (not the client ACQUIRE_TREASURE handler,
    /// appmsg.cpp:266-275, which is silent for <c>c_tcPart</c>); the port
    /// reproduces the player-facing cue on the same <c>c_tcPart</c> pickup.</summary>
    PickupPart,

    // ── P719 (parity wave 18: station/economy + weapons/ordnance loadout) ──
    // One-shot announce/feedback cues in the station/economy and loadout areas
    // (sim-rules-matrix sounds area). Wire/state-driven where the client
    // observes the trigger; Game1-command-driven for the in-flight loadout /
    // group-fire actions native plays from WinTrek.cpp. All are one-shots on
    // the established ClientSoundEvent → Game1.Audio → SoundManager path.

    /// <summary><c>dockedSound</c> — the local ship docked at a station
    /// (trekigc.cpp:2859, <c>PlaySoundEffect(dockedSound)</c> on entering the
    /// hangar; wave <c>landed</c>, sounddef.mdl:316). Edge-triggered on the
    /// local ship's own StationId going from none to a real station.</summary>
    Docked,

    /// <summary><c>salWelcomeHomeSound</c> — the local ship docked at a
    /// friendly station (trekigc.cpp:2860, alongside <c>dockedSound</c>; wave
    /// <c>vo_sal_welcomehome</c>, sounddef.mdl:801). Same dock edge.</summary>
    WelcomeHome,

    /// <summary><c>boardSound</c> — a player boarded the LOCAL ship as a turret
    /// or observer (appmsg.cpp:2071, <c>if (pship == m_ship &&
    /// m_ship->GetCluster() == NULL)</c>; wave <c>vo_sal_playerboarded</c>,
    /// sounddef.mdl:782). Detected on a ship whose ParentId becomes the local
    /// ship's id.</summary>
    Boarded,

    // ── P736 (parity wave 23: presence / side-change salutations) ──────────
    // Native fires the presence salutation VOs from the CLIENT's
    // RemovePlayerFromSide / AddPlayerToSide (clintlib/clintlib.cpp) when the
    // client processes the server's player-quit and player-join side events.
    // The port's server already broadcasts the underlying wire for the reachable
    // cases (JOIN_SIDE on every side join; QUIT_MISSION on a LinkDead disconnect;
    // QUIT_SIDE on the quit-half of a same-mission side switch), so these are
    // reproduced as client one-shots on those messages, gated exactly as native
    // gates them (human player, mission started, connected, side relationship).

    /// <summary><c>salQuitSound</c> — a same-side player quit the mission
    /// (RemovePlayerFromSide, clintlib.cpp:3952-3991 for the boot/quit reasons;
    /// wave <c>vo_sal_quit</c>, sounddef.mdl:828). Note native maps salQuitSound
    /// to the <c>vo_sal_mia</c> wave (sounddef.mdl:827-828); the port uses the
    /// dedicated <c>vo_sal_quit</c> wave when the quitting player's reason is a
    /// plain quit rather than MIA.</summary>
    SalQuit,

    /// <summary><c>salChangeSidesSound</c> — a same-side player is switching
    /// sides (QUIT_SIDE with QSR_SwitchingSides, clintlib.cpp:3975; wave
    /// <c>vo_sal_traitor</c>, sounddef.mdl:829).</summary>
    SalChangeSides,

    /// <summary><c>salMIASound</c> — a same-side player went MIA (a LinkDead
    /// disconnect, QUIT_MISSION default/LinkDead case, clintlib.cpp:3999; wave
    /// <c>vo_sal_mia</c>, sounddef.mdl:826).</summary>
    SalMia,

    /// <summary><c>salEnemyLeavesSound</c> — a player on an enemy (non-allied)
    /// side left the mission (RemovePlayerFromSide other-side branch,
    /// clintlib.cpp:4006-4009; wave <c>vo_sal_enemyleaves</c>, sounddef.mdl:831).</summary>
    SalEnemyLeaves,

    /// <summary><c>salRecruitsArrivedSound</c> — a human player joined OUR side
    /// (AddPlayerToSide, clintlib.cpp:4155; wave <c>vo_sal_recruitsarrived</c>,
    /// sounddef.mdl:825).</summary>
    SalRecruitsArrived,

    /// <summary><c>salEnemyJoinersSound</c> — a human player joined an enemy
    /// side (AddPlayerToSide, clintlib.cpp:4162; wave <c>vo_sal_enemyjoiners</c>,
    /// sounddef.mdl:830).</summary>
    SalEnemyJoiners,

    // ── P737 (parity wave 24: own-ship reload & loadout feedback) ─────────
    // Native fires the reload one-shots from the fire/expendable auto-reload
    // regions when a fire empties a pool and a spare pack is drained from cargo
    // (FireMissile clintlib.cpp:3216-3225; FireExpendable clintlib.cpp:3394-3417;
    // SetAmmo/SetFuel shipIGC.h:605-650). This pure-renderer client observes a
    // SUCCESSFUL reload as the own ship's FM_S_RELOAD(129) relay — the port's
    // server broadcasts the applied cargo→pool transfers sector-wide on success
    // (Server.Combat.BroadcastAutoReload; failures are never relayed). Each
    // relay entry names the consumed cargo slot, which the client classifies by
    // its part type (mirroring Ship.TryApplyReloadEntry) to pick the
    // type-specific cue; every successful reload also plays the generic
    // startReloadSound. All gated to the local ship (native plays these only
    // for <c>pShip == m_ship</c>).

    /// <summary><c>salReloadingAmmoSound</c> — the local ship's ammo pool was
    /// auto-refilled from a spare ammo pack (shipIGC.h:616; wave
    /// <c>vo_sal_reloadingammo</c>, sounddef.mdl:818).</summary>
    SalReloadingAmmo,

    /// <summary><c>salReloadingFuelSound</c> — the local ship's fuel pool was
    /// auto-refilled from a spare fuel pack (shipIGC.h:646; wave
    /// <c>vo_sal_reloadingfuel</c>, sounddef.mdl:814).</summary>
    SalReloadingFuel,

    /// <summary><c>salReloadingMissilesSound</c> — the local ship's missile
    /// launcher was reloaded from a spare missile pack (clintlib.cpp:3221; wave
    /// <c>vo_sal_reloadingmissiles</c>, sounddef.mdl:816).</summary>
    SalReloadingMissiles,

    /// <summary><c>salReloadingChaffSound</c> — the local ship's chaff launcher
    /// was reloaded from a spare chaff pack (clintlib.cpp:3400; wave
    /// <c>vo_sal_reloadingchaff</c>, sounddef.mdl:815).</summary>
    SalReloadingChaff,

    /// <summary><c>salReloadingDispenserSound</c> — the local ship's mine/probe
    /// dispenser was reloaded from a spare pack (clintlib.cpp:3403 default; wave
    /// <c>vo_sal_reloadingdispenser</c>, sounddef.mdl:817).</summary>
    SalReloadingDispenser,

    /// <summary><c>startReloadSound</c> — the generic "reloading..." cue played
    /// on EVERY successful reload of any pool/launcher (clintlib.cpp:3219,3404;
    /// shipIGC.h:617,647; wave <c>mount</c>, sounddef.mdl:289).</summary>
    StartReload,

    // ── P741 (parity wave 25: own-ship shield power edges + quit reasons) ──
    // Native fires the shield power-up/down cues from CshieldIGC on the OWN
    // shield object (shieldIGC.cpp:94,185): the power-up cue when the shield
    // regenerates across the 5% threshold, the power-down cue when overload
    // damage knocks it past the downed threshold (alongside the shields-offline
    // VO). This pure-renderer client observes the LOCAL ship's own streamed
    // <c>ShieldFraction</c> (the same source SIM-0243 uses) and edge-detects the
    // two native thresholds, exactly once per transition.

    /// <summary><c>m_typeData->activateSound</c> — the local ship's shield
    /// regenerated from below 5% to 5% or more (shieldIGC.cpp:94; wave
    /// <c>shieldup</c> — fastShieldActivate, sounddef.mdl:1310).</summary>
    ShieldActivate,

    /// <summary><c>m_typeData->deactivateSound</c> — the local ship's shield was
    /// knocked offline by overload damage, alongside <see cref="ShieldsOffline"/>
    /// (shieldIGC.cpp:185; wave <c>noshld</c> — fastShieldDeactivate,
    /// sounddef.mdl:1311).</summary>
    ShieldDeactivate,

    /// <summary>Port last-base objective transition; native voFoundEnemyBase (Igc/sounds.h:199).</summary>
    LastEnemyBase,
}

/// <summary>A single observed sound event.</summary>
/// <param name="Kind"><see cref="ClientSoundEventKind"/>.</param>
/// <param name="Id">The subject's id — the station id for station events, the
/// development id for a dev completion.</param>
/// <param name="StationTypeId">Station type at receipt time, before destruction
/// removes the station or subsequent exports replace it. Other events use NA.</param>
public readonly record struct ClientSoundEvent(ClientSoundEventKind Kind, short Id, short StationTypeId = -1,
    Allegiance.Core.Audio.NativeVoiceCue? Voice = null);
