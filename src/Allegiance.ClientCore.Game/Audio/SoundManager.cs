using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using System.Linq;
using Allegiance.ClientCore.State;
using Allegiance.Core.Audio;
using System.Diagnostics;

namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P213 — real sound for the MonoGame POC, on MonoGame's own audio stack
/// (<see cref="SoundEffect"/> / <see cref="SoundEffectInstance"/>, NOT the
/// original's obsolete DirectSound3D <c>src/soundengine/</c> plumbing — that
/// is DirectX-specific and irrelevant to a cross-platform MonoGame client).
/// Owns every sound scoped to what is actually flyable in the POC today:
/// a looping main-thruster engine loop scaled by real throttle/speed, a quiet
/// cockpit ambient hum, one-shot autopilot engage/disengage voice chimes, and
/// an intermittent random-variant sector-ambience layer for the whole session
/// (see <see cref="UpdateAmbience"/>). P216 adds the mission-start countdown
/// announcer (a <c>countdownstart</c> callout at the countdown's opening, then
/// a per-second <c>countdown10</c>…<c>countdown1</c> voice over the last ten
/// seconds) and a one-shot game-over callout (<c>vo_ma_gameover</c>).
/// </summary>
/// <remarks>
/// <para><b>Real assets, from the port's artwork tree</b>
/// (<c>$ALLEGIANCE_ARTWORK_PATH</c>, the same tree the server already reads),
/// each resolving the original
/// client's <c>soundEngine</c> namespace in
/// <c>Artwork/sounddef.mdl</c>: the small-ship main-thruster interior loop is
/// <c>accelsmall.ogg</c> (<c>smallShipThrustInteriorSound</c>, sounddef.mdl),
/// the small-ship interior ambience is <c>sshipint.ogg</c>
/// (<c>smallShipInteriorSound</c>), and the engage/disengage chimes are
/// <c>vo_sal_autopilotengage.ogg</c>/<c>vo_sal_autopilotdisengage.ogg</c>
/// (cited at <c>WinTrek/trekigc.cpp:4880</c>). The sector ambience is the
/// <c>bioambient_1</c>…<c>bioambient_27</c> pool from
/// <c>BOStarbaseInternalSound</c> (<c>sounddef.mdl:1064-1083</c>, the POC's
/// Bios faction), ported as the real <c>IntermittentSound(10, false,
/// RandomSound([...]))</c> it is natively, not a single looped track — see
/// <see cref="UpdateAmbience"/> for why that distinction matters (an earlier
/// version of this manager looped just <c>bioambient_1</c>, which contains
/// spoken flavor-text and sounded like a stuck voice message on repeat,
/// confirmed live).</para>
/// <para><b>Graceful degradation.</b> Sound is cosmetic to the flyable POC, so
/// a host with no OpenAL/audio device must not take the game down: every audio
/// touchpoint is guarded so a failure logs once and leaves the manager inert
/// (the app keeps rendering/flying). This is a deliberate divergence from the
/// hard-exit-on-content-failure pattern used for the essential ship model in
/// <see cref="Game1"/>.</para>
/// <para><b>No positional 3D here.</b> These are cockpit/own-ship sounds; the
/// modern <see cref="SoundEffectInstance.Apply3D(AudioListener, AudioEmitter)"/>
/// equivalent is only needed for positioned world sounds (remote ships), which
/// are out of the POC's scope.</para>
/// </remarks>
public sealed class SoundManager : IDisposable, IUiSoundSink
{
    private readonly ContentManager _content;
    private readonly SerializedVoiceQueue _serializedVoices = new();
    private readonly Dictionary<string, SoundEffectInstance> _serializedActive = new();
    private readonly Dictionary<SoundEffect, NativeVoiceCue> _serializedTemplates = new();
    private readonly long _soundClockStart = Stopwatch.GetTimestamp();
    private double SoundNow => Stopwatch.GetElapsedTime(_soundClockStart).TotalSeconds;

    // Register existing SAL/VO callers too, so wire voices share native mutexes
    // with ship-status and station announcements from earlier sound waves.
    private SoundEffect LoadSound(string asset)
    {
        var sound = _content.Load<SoundEffect>(asset);
        string stem = asset[(asset.LastIndexOf('/') + 1)..];
        var cue = NativeVoiceCatalog.Definitions.Values.FirstOrDefault(c => c.Stem == stem && c.Mutex.Length > 0);
        if (cue is not null) _serializedTemplates.TryAdd(sound, cue);
        return sound;
    }

    public void PlayVoice(NativeVoiceCue cue)
    {
        if (!IsLoaded || !cue.Available) return;
        if (cue.Mutex.Length == 0)
        {
            var sound = LoadVoice(cue.Stem);
            if (sound is not null) StartOneShot(sound, SoundCategory.Warning, cue.Gain);
        }
        else if (_serializedVoices.Enqueue(cue, SoundNow) is { } next)
            StartSerializedVoice(next);
    }

    private void StartSerializedVoice(NativeVoiceCue cue)
    {
        // Failed loads/devices must release the mutex and allow the next line.
        while (true)
        {
            var sound = LoadVoice(cue.Stem);
            var instance = sound is null ? null : StartOneShot(sound, SoundCategory.Warning, cue.Gain);
            if (instance is not null)
            {
                _serializedActive[cue.Mutex] = instance;
                return;
            }
            var next = _serializedVoices.Complete(cue.Mutex, SoundNow);
            if (next is null) return;
            cue = next;
        }
    }

    /// <summary>Advance audio in every UI state, including lobby and docked
    /// screens where the ship's engine mixer is not updated.</summary>
    public void UpdateAudioFrame(float deltaSeconds)
    {
        if (!IsLoaded) return;
        UpdateSerializedVoices();
        ApplyVoiceBudget(deltaSeconds);
    }

    private void UpdateSerializedVoices()
    {
        foreach (var (mutex, instance) in _serializedActive.ToArray())
        {
            bool playing;
            try { playing = instance.State != SoundState.Stopped; }
            catch { playing = false; }
            if (playing) continue;
            _serializedActive.Remove(mutex);
            if (_serializedVoices.Complete(mutex, SoundNow) is { } next)
                StartSerializedVoice(next);
        }
    }

    // P721 — the own-ship thruster/interior/sidethrust loop beds, resolved by
    // hull class (native TrekClient::UpdateSounds interior/exterior block,
    // trekigc.cpp:1307-1355; sounddef.mdl:1012-1024, 1342-1347). Small ships use
    // accelsmall main-thruster / sshipint interior / adjust turn / accelsmall
    // sidethrust; capital ships use accelcapital thrust / lshipint interior /
    // accelsmall turn / accelcapital sidethrust. Interior and exterior rows share
    // the same underlying assets (native plays one loop instance and mutes the
    // other volume bus by camera view — see the Update/UpdateTurnThruster docs).
    private SoundEffectInstance? _engine;            // accelsmall — small main-thruster loop (SIM-0373/0379)
    private SoundEffectInstance? _engineCapital;     // accelcapital — capital main-thruster loop (SIM-0373/0379)
    private SoundEffectInstance? _cockpitHum;        // sshipint — small interior hum (SIM-0372)
    private SoundEffectInstance? _cockpitHumCapital; // lshipint — capital interior hum (SIM-0372)
    private SoundEffectInstance? _afterburner; // iafter — afterburner loop (interior only; no 3D emitter for other ships yet)
    private SoundEffectInstance? _turnThrusterCapital; // accelsmall — capital turn bed (SIM-0374/0380)
    private SoundEffectInstance? _sideThrustSmall;     // accelsmall — fighter sidethrust loop (SIM-0376/0382)
    private SoundEffectInstance? _sideThrustCapital;   // accelcapital — capital sidethrust loop (SIM-0377/0383)
    private SoundEffectInstance? _missileTone; // missilelock — P230 lock-acquired looping beep
    // P264 — the incoming-missile warning loop (missileLockSound =
    // LoopingSound(ImportWave("missilelockonme")), sounddef.mdl:364), played
    // while an enemy missile's target is the local ship, pitched 0.75..1.25 by
    // the best lock (trekigc.cpp:5065).
    private SoundEffectInstance? _missileWarning;
    private SoundEffectInstance? _boundsWarning;
    private SoundEffect? _boundsVoice;
    private SoundEffect? _crowdingVoice;
    private SoundEffect _apEngage = null!;    // vo_sal_autopilotengage
    private SoundEffect _apDisengage = null!; // vo_sal_autopilotdisengage
    private SoundEffect _countdownStart = null!;   // countdownstart
    private readonly Dictionary<int, SoundEffect> _countdownTicks = new(); // 10..1
    // P711 — coarse mission-clock milestone VO (SIM-0300..0303, WinTrek.cpp:
    // 11157-11169): vo_time_10min/5min/1min/30sec, keyed by the native
    // "seconds remaining" value (600/300/60/30).
    private readonly Dictionary<int, SoundEffect> _countdownMilestones = new();
    private SoundEffect _gameOver = null!;    // vo_ma_gameover
    // P578 — the eject/death/rescue one-shot feedback cues (sounddef.mdl:
    // newCriticalMsgSound=ImportWave("criticalmessage"),
    // newNonCriticalMsgSound=ImportWave("noncriticalmessage"),
    // rescuePlayerSound=ImportWave("hoover")), played exactly once per
    // critical/non-critical PostText line (trekigc.cpp:3009-3015, 3075-3081)
    // and per rescue (appmsg.cpp:325).
    private SoundEffect _criticalMsg = null!;
    private SoundEffect _nonCriticalMsg = null!;
    private SoundEffect _rescuePlayer = null!;
    private SoundEffect _jump = null!;      // jumpin (native jumpSound)
    private SoundEffect _fireSound = null!;   // pulselaser — weapon single-shot (P232)
    // P465 — per-weapon shot/burst waves, loaded lazily on first use and cached
    // by wave stem (the same pattern as _managedVoices). _weaponShotFailures remembers
    // stems that could not load so a doomed Content.Load is not retried every shot.
    private readonly Dictionary<string, SoundEffect> _weaponShots = new();
    private readonly HashSet<string> _weaponShotFailures = new(StringComparer.Ordinal);
    // P465 — per-mount burst loops during sustained fire (native's per-mount
    // m_vpBurstSound[mount], trekigc.cpp:999-1004), keyed by the firing mount id.
    private readonly Dictionary<int, SoundEffectInstance> _weaponBursts = new();
    private SoundEffect _projectileAmbient = null!; // erbeam — projectile in-flight loop (P232)
    private readonly Dictionary<ProjectileEntity, SoundEffectInstance> _projectileAmbients = new();
    // P237 — warp ambient loop (alephloop), one instance per warp within earshot,
    // keyed by the warp's real WarpId (the ClientWorld.Warps dictionary key).
    private SoundEffect _warpAmbient = null!;      // alephloop — alephSound, sounddef.mdl:385
    private readonly Dictionary<short, SoundEffectInstance> _warpAmbients = new();
    // P236 — missile launch one-shot (lrgslideout), in-flight ambient loop
    // (mmissile), and the three scale-tiered explosion one-shots.
    private SoundEffect _missileLaunch = null!;   // lrgslideout — HornetMissileLaunch, sounddef.mdl:1302
    private SoundEffect _missileAmbient = null!;  // mmissile — HornetMissileFlight, sounddef.mdl:1303
    private readonly Dictionary<ExplosionSoundTier, SoundEffect> _explosions = new(); // explosionshockwave/ef24/ef120
    private readonly Dictionary<short, SoundEffectInstance> _missileAmbients = new(); // keyed by MissileID (P234)
    // P260 — probe cues. Real single-default assets: the deploy one-shot is the
    // real dropobject wave (native deployProbeSound = dropobject, sounddef.mdl:369,
    // P755), and the ambient is the scanning-loop default (per-type ambientSound
    // unreachable client-side). See ProbeAmbientSoundCurve remarks.
    private SoundEffect _probeDeploy = null!;     // dropobject — deployProbeSound (sounddef.mdl:369)
    private SoundEffect _mineDeploy = null!;      // P717 — dropmine (deployMineSound, sounddef.mdl:368)
    private SoundEffect _chaffDeploy = null!;     // P755 — dropobject (deployChaffSound, sounddef.mdl:367)
    private SoundEffect _probeAmbient = null!;    // erbeam — scanning-loop default
    private readonly Dictionary<short, SoundEffectInstance> _probeAmbients = new(); // keyed by ProbeId
    // P267/P756 — hull maneuvering-thruster loops. Native keeps separate
    // interior and exterior instances (trekigc.cpp:1317-1319 and :1341-1343)
    // and mutes the wrong volume bus by camera view; the bundled PCore001
    // assets happen to be the same pair for both buses, but the instances are
    // kept distinct so custom cores with different interior/exterior sounds
    // are honored.
    private SoundEffectInstance? _turnThruster;                // adjust — smallShipTurnInteriorSound, sounddef id 300 (:1016)
    private SoundEffectInstance? _turnThrusterExterior;        // adjust — smallShipTurnExteriorSound, sounddef id 340 (:1017)
    private SoundEffectInstance? _turnThrusterExteriorCapital; // accelsmall — capitalShipTurnExteriorSound, sounddef id 360 (:1024)
    // P267 — hull exterior loop (shipout), audible in the chase/exterior view
    // (native's always-on exterior muted by camera mode).
    private SoundEffectInstance? _shipExterior;   // shipout — smallShipExteriorSound, sounddef id 180 (:1013)
    // P267 — station exterior ambient loop (shipout), one looped instance per
    // station within earshot, keyed by the station's real StationId
    // (ClientWorld.Stations key). Real default = the loop inside
    // ILStationExternalSound (sounddef id 380), see StationAmbientSoundCurve.
    private SoundEffect _stationAmbient = null!;      // shipout
    private readonly Dictionary<short, SoundEffectInstance> _stationAmbients = new();
    // P451 — id → asset-stem table for the base UNDER-ATTACK / CRITICAL voice
    // cues, covering EXACTLY the SoundIDs the real PCore001 station types decode
    // for UnderAttackSound / CriticalSound (dump: garrison=820, outpost=821,
    // shipyard=822, teleport=823, refinery=824, mine=824, expansion=825,
    // supremacy=826, tactical=827, palisade=829, research=830; criticals
    // 840-847). Each UnderAttackSound id maps to its station family's
    // `vo_attack_<family>` clip per sounddef.mdl's <family>AttackedSound
    // definitions (garrisonAttackedSound sounddef.mdl:1220, outpost :1221,
    // shipyard :1222, teleport :1223, refinery :1224, expansion :1225,
    // supremecy :1226, tactical :1227, mine :1228, palisade :1229,
    // research :1230); every CriticalSound maps to `garrisonCritical` =
    // ImportWave("vo_critical_station") (sounddef.mdl:1233). The PCore001 core
    // assigns mines the refinery id (824) and palisade/research the expansion
    // critical id (845) — core-data quirks faithfully reproduced by this
    // single-id table (id 824 → vo_attack_refinery, id 845 → vo_critical_station).
    // The server's defending-team alert sets ChatData.VoiceOver to exactly these
    // ids (FedSrv.CPP:12202-12210), so the chat arrives with the matching id and
    // this table is what lets the client play the clip.
    public static readonly Dictionary<short, string> StationAttackVoices = new()
    {
        [820] = "vo_attack_garrison",
        [821] = "vo_attack_outpost",
        [822] = "vo_attack_shipyard",
        [823] = "vo_attack_teleport",
        [824] = "vo_attack_refinery",
        [825] = "vo_attack_expansion",
        [826] = "vo_attack_supremecy",
        [827] = "vo_attack_tactical",
        [828] = "vo_attack_mine",
        [829] = "vo_attack_palisade",
        [830] = "vo_attack_research",
        [831] = "dn_vo_attack_transeiver",
        [840] = "vo_critical_station",
        [841] = "vo_critical_station",
        [842] = "vo_critical_station",
        [843] = "vo_critical_station",
        [844] = "vo_critical_station",
        [845] = "vo_critical_station",
        [846] = "vo_critical_station",
        [847] = "vo_critical_station",
        [848] = "vo_critical_station",
        [851] = "vo_critical_station",
    };

    /// <summary>P451 — resolve a chat's <c>voiceOver</c> SoundID to the base
    /// under-attack / critical asset stem, if it is one of the real station
    /// types' ids in <see cref="StationAttackVoices"/>. Static so the id→stem
    /// table is unit-testable against a real core without a graphics device.</summary>
    public static bool TryGetStationAttackStem(short voiceOver, out string stem)
    {
        if (StationAttackVoices.TryGetValue(voiceOver, out var s))
        {
            stem = s!;          // table values are always non-null stems
            return true;
        }
        stem = "";
        return false;
    }
    // P267 — development/tech completion chime one-shot. Real default =
    // vo_sal_researchcomplete (newResearchCompletedSound, sounddef id 940),
    // the classic "research completed" chime (dev CompletionSound).
    private SoundEffect _devComplete = null!;      // vo_sal_researchcomplete
    // P414 — the "ripcord aborted" VO cue (salRipcordAbortedSound =
    // ImportWave("vo_sal_ripcordaborted"), sounddef.mdl:351), played when the
    // local player's own ripcord countdown is cancelled.
    private SoundEffect _ripcordAborted = null!;   // vo_sal_ripcordaborted
    // P463 — the four projectile hit sounds (native ThingSite::RegisterHit,
    // trekigc.cpp:2365-2393; sounddef.mdl:250-264). The "my shield" and "my
    // hull" pools are the random 1-3 variants (HitMeShield1-3 / HitMeArmor1-3);
    // native's otherShieldHitSound IS myShieldHitSound at priority -1.6 (same
    // pool, quieter — the attenuation carries the cut), and otherHullHitSound
    // is the single ef120 (already loaded as the small explosion, reused here).
    private SoundEffect[] _shieldHit = Array.Empty<SoundEffect>();  // hitmeshield1-3
    private SoundEffect[] _hullHit = Array.Empty<SoundEffect>();    // hitmearmor1-3
    private SoundEffect _otherHullHit = null!;                      // ef120 (reused)
    private SoundEffect _collision = null!;                         // P463c — colisionlarge (collisionSound, sounddef.mdl:308)
    private SoundEffect _rockHit = null!;                           // P463c — hitrock (rockHitSound, sounddef.mdl:266)
    private SoundEffect _mouseover = null!;                         // P752c — mouseover (mouseoverSound, sounddef.mdl:322)
    private SoundEffect _chatKey = null!;                           // HUD-10 — chatKeySound (text, sounddef.mdl:324)
    private SoundEffect _paneSlide = null!;                         // HUD-10 — paneSlideSound (windowslides, sounddef.mdl:323)
    // P710 — wealth / treasure announcement one-shots (clintlib/appmsg.cpp).
    // Native plays pickUpPowerupSound/pickUpDevelopmentSound/pickUpCashSound on
    // a treasure pickup (appmsg.cpp:282-303) and paydaySound on each payday
    // (appmsg.cpp:1313). Real single-default assets from sounddef.mdl:279-281,294.
    private SoundEffect _payday = null!;              // payday — paydaySound
    private SoundEffect _pickupPowerup = null!;       // PowerupShield — pickUpPowerupSound
    private SoundEffect _pickupDevelopment = null!;   // PowerupDevelopment — pickUpDevelopmentSound (+ flag reuses it, NYI flag cue)
    private SoundEffect _pickupCash = null!;          // hoover — pickUpCashSound
    private SoundEffect _pickupPart = null!;          // P735 — hoover — pickUpPartSound (shipIGC.h:1307, sounddef.mdl:278)
    // P710 — incoming-message notification cues (trekigc.cpp:4536-4560). Real
    // waves from sounddef.mdl:297-301; newOfflineChatMsgSound reuses receivechat
    // at reduced gain (sounddef.mdl:299), so it shares _chatRegular's asset.
    private SoundEffect _chatCommand = null!;     // receivedobjective — newCommandMsgSound
    private SoundEffect _chatCommander = null!;   // receivedcomchat — newChatMsgFromCommanderSound
    private SoundEffect _chatPersonal = null!;    // receivepersonalchat — newPersonalMsgSound
    private SoundEffect _chatRegular = null!;     // receivechat — newChatMsgSound (+ offline at lower gain)
    // P711 — ship low-status / combat feedback one-shots (trekigc.cpp:1264-1275,
    // appmsg.cpp:970). Waves resolved from sounddef.mdl:371-374, 811-812, 348.
    private SoundEffect _hullLow = null!;         // vo_sal_hullcritical — hullLowSound (:374)
    private SoundEffect _ammoLow = null!;         // outofammo — ammoLowSound (:371)
    private SoundEffect _fuelLow = null!;         // fuellow — fuelLowSound (:372)
    private SoundEffect _energyLow = null!;       // vo_sal_lowenergy — energyLowSound (:373)
    private SoundEffect _noAmmo = null!;          // vo_sal_noammo — salNoAmmoSound (:812)
    private SoundEffect _noFuel = null!;          // vo_sal_nofuel — salNoFuelSound (:811)
    private SoundEffect _noRipcord = null!;       // vo_sal_noripcord — salNoRipcordSound (:348)
    private SoundEffect _shieldsOffline = null!;  // P717 — vo_sal_shieldsoffline — salShieldsOfflineSound (:791)
    private SoundEffect _shieldActivate = null!;  // P741 — shieldup — fastShieldActivate (sounddef.mdl:1310)
    private SoundEffect _shieldDeactivate = null!;// P741 — noshld — fastShieldDeactivate (sounddef.mdl:1311)
    private SoundEffect _cloakEngage = null!;     // P735 — vo_sal_cloakengage — salCloakEngageSound (:807)
    // P714 — objective & status announce one-shots (appmsg.cpp:1238,2965,3149,3156,3169).
    // Waves from sounddef.mdl:771,773,776,779,780.
    private SoundEffect _madeCommander = null!;   // vo_sal_madecommander — commanderSound (:779)
    private SoundEffect _madeInvestor = null!;    // vo_sal_madeinvestor — investorSound (:780)
    private SoundEffect _stolenEnemyFlag = null!; // vo_stolen_enemyflag — enemyFlagLostSound (:773)
    private SoundEffect _artifact = null!;        // vo_sal_artifact — artifactFoundSound (:776)
    private SoundEffect _stolenFlag = null!;      // vo_stolen_flag — flagLostSound (:771)
    // P719 — station/economy + loadout one-shots (parity wave 18). Waves from
    // sounddef.mdl:286,288,290,316,801,802,782,810,819 and groupweapons/ungroupweapons.
    private SoundEffect _docked = null!;          // landed — dockedSound (:316)
    private SoundEffect _welcomeHome = null!;     // vo_sal_welcomehome — salWelcomeHomeSound (:801)
    private SoundEffect _boarded = null!;         // vo_sal_playerboarded — boardSound (:782)
    private SoundEffect _groupWeapons = null!;    // groupweapons — groupWeaponsSound (:376)
    private SoundEffect _ungroupWeapons = null!;  // ungroupweapons — ungroupWeaponsSound (:377)
    private SoundEffect _mount = null!;           // mount — mountSound (:286)
    private SoundEffect _unmount = null!;         // unmount — unmountSound (:288)
    private SoundEffect _mapClick = null!;        // P755 — mousedown — mouseclickSound (sounddef.mdl:321)
    private SoundEffect _error = null!;           // P764 — ef03 — errorSound (sounddef.mdl:272)
    private SoundEffect _positiveClick = null!;   // P764 — accept — positiveButtonClickSound (sounddef.mdl:313)
    private SoundEffect _cargoFull = null!;       // vo_sal_cargofull — salCargoFullSound (:810)
    // P736 — presence / side-change salutation one-shots (parity wave 23).
    // Waves from sounddef.mdl:825-831 (vo_sal_recruitsarrived / vo_sal_mia /
    // vo_sal_quit / vo_sal_traitor / vo_sal_enemyjoiners / vo_sal_enemyleaves).
    // Native maps salQuitSound to the vo_sal_mia wave (sounddef.mdl:827-828);
    // this port uses the dedicated vo_sal_quit wave so the same-side "X has
    // quit" cue is distinguishable from the MIA cue the player hears.
    private SoundEffect _salQuit = null!;
    private SoundEffect _salChangeSides = null!;
    private SoundEffect _salMia = null!;
    private SoundEffect _salEnemyLeaves = null!;
    private SoundEffect _salRecruitsArrived = null!;
    private SoundEffect _salEnemyJoiners = null!;
    // P737 — own-ship reload one-shots: the five type-specific sal VOs
    // (vo_sal_reloading{ammo,fuel,missiles,chaff,dispenser}; sounddef.mdl:814-818)
    // plus the generic startReloadSound (SFX "mount", sounddef.mdl:289).
    private SoundEffect _salReloadingAmmo = null!;
    private SoundEffect _salReloadingFuel = null!;
    private SoundEffect _salReloadingMissiles = null!;
    private SoundEffect _salReloadingChaff = null!;
    private SoundEffect _salReloadingDispenser = null!;
    // P787a — own-ship launcher DEPLETED (reload-failure) VOs
    // (vo_sal_{missilesdepleted,chaffdepleted,dispenserempty}; sounddef.mdl:814-818
    // family; NativeVoiceCatalog IDs 1417-1419).
    private SoundEffect _salMissilesDepleted = null!;
    private SoundEffect _salChaffDepleted = null!;
    private SoundEffect _salDispenserEmpty = null!;
    private SoundEffect _drop = null!;            // drop — dropSound (:290)
    private SoundEffect _reloading = null!;       // vo_sal_reloading — salReloadingSound (:819)
    // P723 — in-flight command/targeting one-shots. Real waves from
    // sounddef.mdl:295,311,350.
    private SoundEffect _newTarget = null!;       // newtargetneutral — newTargetSound (:311)
    private SoundEffect _vectorLock = null!;      // vector — vectorLockSound (:295)
    private SoundEffect _shipCantRipcord = null!; // vo_sal_shipcantripcord — salShipCantRipcordSound (:350)
    // P711 — the ripcord-active loop (SIM-0368) and the mining loop (SIM-0370).
    // ripcordOnLoopSound = ImportWave("countdownStart") (sounddef.mdl:347), the
    // same countdown-tick wave native loops while a ripcord countdown runs;
    // miningSound = ThreeDSound(200, LoopingSound(miningsound)) (sounddef.mdl:379).
    private SoundEffectInstance? _ripcordLoop;    // countdownstart — ripcordOnLoopSound
    private SoundEffectInstance? _miningLoop;     // miningsound — miningSound
    // P373 — the one-shot "new contact" (first-sighting) cues, one per
    // enemy/friendly split. Real native mapping confirmed from sounddef.mdl:247-248
    // — note the native VARIABLE names are swapped vs their meaning: the sound
    // named `newShipSound` = ImportWave("newtarget") is played for an ENEMY
    // contact, and `newEnemySound` = ImportWave("newtargetenemy") is played for
    // a FRIENDLY/same-side contact (trekigc.cpp:2087-2094). We reproduce the
    // behavior the CODE produces (enemy→newtarget, friendly→newtargetenemy), not
    // the misleading variable-name reading.
    private SoundEffect _newContactEnemy = null!;    // newtarget — a hostile, non-allied ship first becomes visible
    private SoundEffect _newContactFriendly = null!; // newtargetenemy — a same-side/friendly ship first becomes visible
    // P374 — sector-threat alert cues. Eight distinct detection/threatened VO
    // stems, one per warning level (native c_vAlertSounds, trekigc.cpp:344-352;
    // real waves confirmed from sounddef.mdl:758-766), plus the sector-
    // secured/lost control cues (sectorSecuredSound/sectorLostSound,
    // sounddef.mdl:768-769). The alert dict is keyed by stem because levels
    // 13/14/15 all map to vo_sal_stationrisk — one asset serves all three.
    private readonly Dictionary<string, SoundEffect> _clusterAlertSounds = new(StringComparer.Ordinal);
    private SoundEffect _sectorSecured = null!;    // vo_sal_sectorsecured
    private SoundEffect _sectorLost = null!;       // vo_sal_sectorlost
    /// <summary>P374 — the nine distinct sector-threat alert stems loaded eagerly
    /// in <see cref="Load"/> (native c_vAlertSounds, sounddef.mdl:758-766;
    /// levels 13/14/15 share vo_sal_stationrisk).</summary>
    private static readonly string[] ClusterAlertStems =
    {
        "vo_detect_miner", "vo_detect_builder", "vo_sal_carrieratrisk",
        "vo_sal_bombersighted", "vo_sal_carrierdetected", "vo_sal_capitalsighted",
        "vo_sal_teleportsighted", "vo_sal_transportsighted", "vo_sal_stationrisk",
    };
    private bool _warned;
    private bool _disposed;

    // Sector ambience — native's BOStarbaseInternalSound (sounddef.mdl:1064) is
    // an IntermittentSound(10, /*multiple*/false, RandomSound([14 bioambient_*
    // variants])): a single one-shot picked at random roughly every
    // AmbiencePeriodSeconds, never two at once, never immediately repeating the
    // last pick. NOT one track looped continuously — an earlier version of this
    // manager collapsed it to LoopingSound(bioambient_1), and bioambient_1
    // happens to contain spoken flavor-text ("routine ... test report ...
    // mandatory"), so it played on an endless ~5.8s loop and sounded like a
    // stuck voice message (confirmed live). See AmbienceStems' remarks.
    private static readonly string[] AmbienceStems =
    {
        "bioambient_1", "bioambient_2", "bioambient_3", "bioambient_4", "bioambient_5",
        "bioambient_6", "bioambient_7", "bioambient_21", "bioambient_22", "bioambient_23",
        "bioambient_24", "bioambient_25", "bioambient_26", "bioambient_27",
    };
    private const float AmbiencePeriodSeconds = 10f; // native: IntermittentSound(10, ...)
    private SoundEffect[] _ambienceVariants = Array.Empty<SoundEffect>();
    // M-3 — the ambience one-shot lives in a ReplacingSlot so that replacing
    // it disposes the previous instance (instead of abandoning a stopped
    // instance undisposed every ~10s and accumulating leaked voice handles).
    private readonly ReplacingSlot<SoundEffectInstance> _ambienceSlot = new(i => Safe(i.Dispose));
    private int _ambienceLastIndex = -1;
    private readonly Random _rng = new();

    // ── P614 — native voice budget / priority / mutex-group enforcement ──────
    // Every loud world sound (projectile/missile ambients, weapon fire &
    // bursts, explosions, hits, collisions, probes, warps, stations) is tracked
    // here as a Voice. Each frame the pure <see cref="VoiceBudget"/> ranks all
    // audible voices by native dynamic priority
    // (soundengine/ds3dvirtualbuffer.cpp:384-386) and admits only the top
    // <see cref="_voiceBudgetCfg"/>.DesiredVoices subject to per-kind group
    // caps — the port of native's DS3D voice management
    // (soundengine/ds3dengine.cpp:360-466). Voices outside the budget are
    // muted (loops) or dropped (one-shots). Essential own-ship bed cues are
    // force-admitted so the cockpit bed is never starved.
    private sealed class Voice
    {
        public long Id;
        public SoundCategory Category;
        public SoundEffectInstance? Instance;
        public bool IsLoop;
        public bool Essential;      // always-admitted (never culled), still counted
        public float Loudness;      // target 0..1 pre-budget (from distance)
        public bool Audible;        // false when out of range / silent
        public bool Started;        // one-shot: was played at spawn
        public float Born;          // age clock value at registration
        public bool LastAdmitted;   // held an audible buffer last frame (drop detect)
    }

    private readonly List<Voice> _managedVoices = new();
    private long _nextVoiceId = 1;
    private readonly VoiceBudgetConfig _voiceBudgetCfg = new(DesiredVoices: 16, MaxVoices: 24);
    private float _voiceAgeClock;
    private float _masterVolume = 1f;
    private readonly Dictionary<SoundCategory, float> _categoryVolumes = new();
    // Soft-limiter ("battle ducking") state — port addition (native relied on
    // the voice budget alone); ducks master gain gently as the audible combat
    // voice count climbs toward the budget ceiling.
    private const int DuckThresholdVoices = 12;
    private const float DuckMinGain = 0.8f;
    // Drop-rate meter (drops/second), a 1s sliding window.
    private double _dropCount;
    private float _dropsPerSecond;
    // Live telemetry ring for the debug overlay / periodic log.
    private DateTime _lastTelemetryLog = DateTime.MinValue;
    private bool _logBudgetTelemetry = false;
    private static readonly TimeSpan TelemetryPeriod = TimeSpan.FromSeconds(5);

    /// <summary>Global master gain (0..1), applied to every budgeted voice and
    /// the own-ship bed (a P614 port addition — native had no master slider,
    /// only per-template GainSound).</summary>
    public float MasterVolume { get => _masterVolume; set => _masterVolume = Math.Clamp(value, 0f, 1f); }

    /// <summary>Set a per-category mix volume (0..1) — port addition (native
    /// baked Gain into each template; the port exposes a knob for category
    /// balance).</summary>
    public void SetCategoryVolume(SoundCategory category, float volume) =>
        _categoryVolumes[category] = Math.Clamp(volume, 0f, 1f);

    public float CategoryVolume(SoundCategory category) =>
        _categoryVolumes.TryGetValue(category, out var v) ? v : 1f;

    /// <summary>Toggle the periodic voice-budget telemetry console line
    /// (active voices by category + drops/sec). Off by default for quiet logs;
    /// the P614 live proof enables it.</summary>
    public bool LogBudgetTelemetry
    {
        get => _logBudgetTelemetry;
        set { _logBudgetTelemetry = value; _lastTelemetryLog = DateTime.MinValue; }
    }

    private long Track(SoundEffectInstance inst, SoundCategory cat, bool isLoop,
        bool essential, float loudness, bool audible, bool started)
    {
        var id = _nextVoiceId++;
        _managedVoices.Add(new Voice
        {
            Id = id, Category = cat, Instance = inst, IsLoop = isLoop,
            Essential = essential, Loudness = loudness, Audible = audible,
            Started = started, Born = _voiceAgeClock,
        });
        return id;
    }

    private Voice? VoiceFor(SoundEffectInstance inst) =>
        _managedVoices.FirstOrDefault(v => ReferenceEquals(v.Instance, inst));

    /// <summary>Store a tracked loop's per-frame loudness/audibility (from its
    /// distance) WITHOUT touching the instance — the budget settles the actual
    /// volume each frame.</summary>
    private void SetTrackedLoudness(SoundEffectInstance inst, float loudness)
    {
        if (VoiceFor(inst) is { } v)
        {
            v.Loudness = loudness;
            v.Audible = loudness > 0f;
        }
    }

    private void ForgetVoice(SoundEffectInstance inst)
    {
        if (VoiceFor(inst) is { } v) _managedVoices.Remove(v);
    }

    /// <summary>Play a budget-tracked one-shot: it starts immediately (so a
    /// transient like an explosion is always heard) and is then subject to the
    /// per-frame budget — if a higher-priority/louder storm takes the budget it
    /// is stopped and released (native discards/steals, ds3dengine.cpp:460-466).</summary>
    private void PlayOneShot(SoundEffect sfx, SoundCategory cat, float volume)
    {
        if (_serializedTemplates.TryGetValue(sfx, out var cue))
            PlayVoice(cue with { GainDb = cue.GainDb + 20 * Math.Log10(Math.Max(volume, float.Epsilon)) });
        else
            StartOneShot(sfx, cat, volume);
    }

    private SoundEffectInstance? StartOneShot(SoundEffect sfx, SoundCategory cat, float volume)
    {
        SoundEffectInstance? inst = null;
        try
        {
            inst = sfx.CreateInstance();
            inst.Volume = Math.Clamp(volume * CategoryVolume(cat) * _masterVolume, 0f, 1f);
            inst.Play();
            Track(inst, cat, isLoop: false, essential: false,
                loudness: volume, audible: volume > 0f, started: true);
            return inst;
        }
        catch { if (inst is not null) Safe(inst.Dispose); }
        return null;
    }

    /// <summary>Play an own-ship / voice-announcement one-shot through the
    /// budget at the highest (Warning) priority so it is never drowned by the
    /// combat bed (native serialises these with MutexSal, sounddef.mdl:71-79).</summary>
    private void PlayWarnOneShot(SoundEffect sfx)
    {
        if (IsLoaded) PlayOneShot(sfx, SoundCategory.Warning, 1f);
    }

    private float SoftLimitGain(int audibleNonEssential)
    {
        // Gentle "battle ducking" soft limiter — PORT ADDITION (flag below):
        // as the audible combat-voice count nears the budget ceiling, ease
        // master gain down toward DuckMinGain so the mix never hard-clips.
        if (audibleNonEssential <= DuckThresholdVoices) return 1f;
        float over = audibleNonEssential - DuckThresholdVoices;
        float span = _voiceBudgetCfg.DesiredVoices - DuckThresholdVoices;
        float t = Math.Clamp(over / span, 0f, 1f);
        return 1f - (1f - DuckMinGain) * t;
    }

    private void CountDrop() { _dropCount++; }

    private void UpdateDropRate(float dt)
    {
        _dropsPerSecond = (float)(_dropCount / Math.Max(dt, 1e-4));
        // slide window every 1s
        _dropCount = 0;
    }

    /// <summary>Snapshot of live audio telemetry for the P614 debug overlay /
    /// log: audible (budget-admitted) and active-by-category counts plus the
    /// instantaneous drop rate.</summary>
    public AudioTelemetry GetTelemetry()
    {
        var byCat = Enum.GetValues<SoundCategory>()
            .Select(c => (Category: SoundCategories.Label(c),
                          Active: _managedVoices.Count(v => v.LastAdmitted && v.Category == c)))
            .ToList();
        int audible = _managedVoices.Count(v => v.LastAdmitted);
        int essential = _managedVoices.Count(v => v.Essential && v.LastAdmitted);
        return new AudioTelemetry(
            DateTime.UtcNow, audible, essential, _dropsPerSecond, byCat);
    }

    /// <summary>Run one voice-budget pass: prune finished one-shots, rank and
    /// admit the top voices, mute/drop the rest, settle loop volumes, and emit
    /// the periodic telemetry line. Called every frame from <see cref="Update"/>.</summary>
    private void ApplyVoiceBudget(float dt)
    {
        // Prune finished one-shots (a finished loop is only stopped by its owner).
        for (int i = _managedVoices.Count - 1; i >= 0; i--)
        {
            var v = _managedVoices[i];
            if (v.IsLoop) continue;
            var inst = v.Instance;
            if (inst is null || inst.State == SoundState.Stopped)
            {
                inst?.Dispose();
                _managedVoices.RemoveAt(i);
            }
        }

        // Rank all audible voices; admit the top budget, subject to group caps.
        var candidates = new List<BudgetSound>(_managedVoices.Count);
        foreach (var v in _managedVoices)
            candidates.Add(new BudgetSound(v.Id, v.Category, v.Loudness, v.Audible,
                _voiceAgeClock - v.Born, v.LastAdmitted && v.Started));
        var decision = VoiceBudget.Decide(candidates, _voiceBudgetCfg);
        var admitted = new HashSet<long>(decision.AdmittedIds);

        // Count drops (previously-audible voices now outside the budget) and
        // derive the battle-duck gain from the admitted non-essential count.
        int nonEssential = _managedVoices.Count(v => admitted.Contains(v.Id) && !v.Essential);
        float duck = SoftLimitGain(nonEssential);
        foreach (var v in _managedVoices)
        {
            bool now = v.Essential || admitted.Contains(v.Id);
            if (v.LastAdmitted && !now && v.Started) CountDrop();
            v.LastAdmitted = now;
        }

        // Settle each voice: admitted loops play at their budgeted volume,
        // dropped loops are muted (kept for reactivation), dropped one-shots
        // are stopped and released (native steals the buffer, ds3dengine.cpp:460-466).
        foreach (var v in _managedVoices)
        {
            bool ad = v.Essential || admitted.Contains(v.Id);
            if (!v.IsLoop)
            {
                if (!ad) { if (v.Instance is { } o) { if (o.State == SoundState.Playing) Safe(o.Stop); Safe(o.Dispose); } }
                continue;
            }
            if (v.Instance is not { } inst) continue;
            if (ad)
            {
                float g = v.Loudness * CategoryVolume(v.Category) * _masterVolume * duck;
                Safe(() => inst.Volume = Math.Clamp(g, 0f, 1f));
                if (inst.State != SoundState.Playing) Safe(inst.Play);
            }
            else
            {
                Safe(() => inst.Volume = 0f);   // muted, still registered for reactivation
            }
        }

        _voiceAgeClock += dt;
        UpdateDropRate(dt);

        if (_logBudgetTelemetry &&
            (DateTime.UtcNow - _lastTelemetryLog) >= TelemetryPeriod)
        {
            _lastTelemetryLog = DateTime.UtcNow;
            var t = GetTelemetry();
            var cats = string.Join(", ", t.ByCategory.Where(c => c.Active > 0)
                .Select(c => $"{c.Category}={c.Active}"));
            Console.WriteLine(
                $"[monogame] audio budget: audible={t.AudibleVoices}/{_voiceBudgetCfg.DesiredVoices} " +
                $"(essential {t.EssentialVoices}) drops/s={t.DropsPerSecond:F1} | {cats}");
        }
    }


    /// <summary>P230 — the missile-lock beep's mix volume (native applies
    /// <c>GainSound(SFXGain, …)</c> to <c>missileToneSound</c>, sounddef.mdl:365);
    /// moderate so it reads as a cockpit "jet-fighter beep" without drowning the
    /// engine/ambience bed.</summary>
    private const float MissileToneVolume = 0.55f;

    // P264 — the incoming-missile warning loop's mix volume (native applies
    // <c>GainSound(SFXGain, LoopingSound(ImportWave("missilelockonme")))</c>,
    // sounddef.mdl:364); a touch louder than the own-lock beep since it warns of
    // imminent harm, but still a bed cue, not a sting.
    private const float MissileWarningVolume = 0.6f;
    // P721 — sidethrust-loop bed volume (SIM-0376/0377/0382/0383). Native plays
    // the sidethrust loop at GainSound(-25, ThreeDSound(100,...)) interior and
    // GainSound(SFXGain, ThreeDSound(100,...)) exterior (sounddef.mdl:1344-1347),
    // gated by the internal/external volume bus per camera view; the port uses
    // one shared loop at a fixed moderate bed volume for both views.
    private const float SideThrustVolume = 0.7f;

    // P217 — quick-chat voice lines, loaded lazily on first play and cached
    // thereafter (only the handful actually voiced in a session are ever
    // resident, not all 288 shipped voices). _voiceFailures remembers assets
    // that could not load (missing/not-in-content) so we don't re-attempt a
    // doomed Content.Load every frame; the caption still displays either way.
    private readonly Dictionary<string, SoundEffect> _voices = new();
    private readonly HashSet<string> _voiceFailures = new(StringComparer.Ordinal);

    /// <summary>True when every core cue loaded and the manager is usable.
    /// If this is false the manager is inert (no audio device / a load failure)
    /// and callers' updates are no-ops.</summary>
    public bool IsLoaded { get; private set; }

    public SoundManager(ContentManager content) => _content = content;

    /// <summary>
    /// Load every cue from content and prepare the looping instances. Does not
    /// start anything (the ambient loop starts on the first <see cref="Update"/>
    /// once the session is actually running, keeping pre-connect silence).
    /// Any failure degrades the manager to inert and logs it, rather than
    /// throwing into the game's load path.
    /// </summary>
    public void Load()
    {
        try
        {
            _engine = Loop($"Sounds/{ShipLoopBeds.FighterMainThruster}");
            _engineCapital = Loop($"Sounds/{ShipLoopBeds.CapitalMainThruster}");       // P721 — capital main-thruster bed
            _cockpitHum = Loop($"Sounds/{ShipLoopBeds.FighterInterior}");
            _cockpitHumCapital = Loop($"Sounds/{ShipLoopBeds.CapitalInterior}");       // P721 — capital interior bed
            _afterburner = Loop($"Sounds/{ShipLoopBeds.Afterburner}");
            _missileTone = Loop("Sounds/missilelock");   // P230 — lock-acquired beep (missileToneSound = ImportWave("missilelock"), sounddef.mdl:365; sound id missileTone, sounds.h:135)
            _missileWarning = Loop("Sounds/missilelockonme"); // P264 — incoming-missile warning loop (missileLockSound = LoopingSound(ImportWave("missilelockonme")), sounddef.mdl:364)
            _boundsWarning = Loop($"Sounds/{ShipWarningState.LoopSound}");
            _boundsVoice = LoadSound($"Sounds/{ShipWarningState.BoundsVoice}");
            _crowdingVoice = LoadSound($"Sounds/{ShipWarningState.CrowdingVoice}");
            _ambienceVariants = AmbienceStems.Select(s => LoadSound($"Sounds/{s}")).ToArray();
            _apEngage = LoadSound("Sounds/vo_sal_autopilotengage");
            _apDisengage = LoadSound("Sounds/vo_sal_autopilotdisengage");
            _countdownStart = LoadSound("Sounds/countdownstart");
            for (int n = 1; n <= CountdownSound.MaxTickSecond; n++)
                _countdownTicks[n] = LoadSound($"Sounds/countdown{n}");
            // P711 — coarse mission-clock milestone VO (SIM-0300..0303).
            _countdownMilestones[600] = LoadSound("Sounds/vo_time_10min");
            _countdownMilestones[300] = LoadSound("Sounds/vo_time_5min");
            _countdownMilestones[60] = LoadSound("Sounds/vo_time_1min");
            _countdownMilestones[30] = LoadSound("Sounds/vo_time_30sec");
            _gameOver = LoadSound("Sounds/vo_ma_gameover");
            _fireSound = LoadSound("Sounds/pulselaser");   // P232 — weapon single-shot (pulselaserShotSound, sounddef.mdl)
            _projectileAmbient = LoadSound("Sounds/erbeam");// P232 — projectile in-flight loop (er_beam, sounddef.mdl)
            _warpAmbient = LoadSound("Sounds/alephloop");  // P237 — warp ambient loop (alephSound, sounddef.mdl:385)
            // P236 — missile cues. Real single-default assets (per-type ids are
            // unreachable client-side, see MissileAmbientSoundCurve remarks).
            _missileLaunch = LoadSound("Sounds/lrgslideout"); // HornetMissileLaunch, sounddef.mdl:1302
            _missileAmbient = LoadSound("Sounds/mmissile");   // HornetMissileFlight, sounddef.mdl:1303
            _explosions[ExplosionSoundTier.Large] = LoadSound("Sounds/explosionshockwave"); // largeExplosionSound, sounddef.mdl:268
            _explosions[ExplosionSoundTier.Medium] = LoadSound("Sounds/ef24");               // mediumExplosionSound, sounddef.mdl:269
            _explosions[ExplosionSoundTier.Small] = LoadSound("Sounds/ef120");               // smallExplosionSound, sounddef.mdl:270
            // P463 — the four hit-sound cues (see the field docs): the two
            // random 1-3 pools (HitMeShield1-3 / HitMeArmor1-3) and the reused
            // ef120 for otherHull (the small explosion already loaded above).
            _shieldHit = new[]
            {
                LoadSound("Sounds/hitmeshield1"),
                LoadSound("Sounds/hitmeshield2"),
                LoadSound("Sounds/hitmeshield3"),
            };
            _hullHit = new[]
            {
                LoadSound("Sounds/hitmearmor1"),
                LoadSound("Sounds/hitmearmor2"),
                LoadSound("Sounds/hitmearmor3"),
            };
            _otherHullHit = _explosions[ExplosionSoundTier.Small];   // reuse — same asset, one Content.Load
            // P463c — collision + rock-hit one-shots (collisionSound=colisionlarge,
            // rockHitSound=hitrock; sounddef.mdl:308,266). Single assets each.
            _collision = LoadSound("Sounds/colisionlarge");
            _rockHit = LoadSound("Sounds/hitrock");
            _mouseover = LoadSound("Sounds/mouseover");   // P752c — UI hover cue (mouseoverSound, sounddef.mdl:322)
            _chatKey = LoadSound("Sounds/text");          // HUD-10 — chatKeySound (sounddef.mdl:324)
            _paneSlide = LoadSound("Sounds/windowslides");// HUD-10 — paneSlideSound (sounddef.mdl:323)
            // P260 — probe cues (single-real-default; see ProbeAmbientSoundCurve remarks).
            _probeDeploy = LoadSound("Sounds/dropobject");  // P755 — deployProbeSound (sounddef.mdl:369)
            _mineDeploy = LoadSound("Sounds/dropmine");     // P717 — deployMineSound (sounddef.mdl:368)
            _chaffDeploy = LoadSound("Sounds/dropobject");  // P755 — deployChaffSound (sounddef.mdl:367)
            _probeAmbient = LoadSound("Sounds/erbeam");      // scanning-loop default
            // P267/P756 — hull maneuvering-thruster turn loops (interior and
            // exterior twins, trekigc.cpp:1317-1319 / :1341-1343), hull exterior
            // loop (shipout), station exterior ambient loop (shipout), and the
            // tech completion chime (vo_sal_researchcomplete). See the field docs.
            _turnThruster = Loop($"Sounds/{ShipLoopBeds.FighterTurn}");
            _turnThrusterCapital = Loop($"Sounds/{ShipLoopBeds.CapitalTurn}"); // P721 — capital turn bed (sounddef.mdl:1023)
            _turnThrusterExterior = Loop($"Sounds/{ShipLoopBeds.FighterTurn}");
            _turnThrusterExteriorCapital = Loop($"Sounds/{ShipLoopBeds.CapitalTurn}");
            _sideThrustSmall = Loop($"Sounds/{ShipLoopBeds.FighterSideThrust}"); // P721 — fighter sidethrust loop (SIM-0376/0382)
            _sideThrustCapital = Loop($"Sounds/{ShipLoopBeds.CapitalSideThrust}"); // P721 — capital sidethrust loop (SIM-0377/0383)
            _shipExterior = Loop("Sounds/shipout");
            _stationAmbient = LoadSound("Sounds/shipout");
            _devComplete = LoadSound("Sounds/vo_sal_researchcomplete");
            _ripcordAborted = LoadSound("Sounds/vo_sal_ripcordaborted"); // P414
            // P578 — eject/death/rescue feedback cues (sounddef.mdl).
            _criticalMsg = LoadSound("Sounds/criticalmessage");
            _nonCriticalMsg = LoadSound("Sounds/noncriticalmessage");
            _rescuePlayer = LoadSound("Sounds/hoover");
            _jump = LoadSound("Sounds/jumpin");
            // P710 — wealth announcements (payday + treasure pickups). Waves
            // resolved from sounddef.mdl:279-281,294 (payday, PowerupShield,
            // PowerupDevelopment, hoover).
            _payday = LoadSound("Sounds/payday");
            _pickupPowerup = LoadSound("Sounds/powerupshield");
            _pickupDevelopment = LoadSound("Sounds/powerupdevelopment");
            _pickupCash = LoadSound("Sounds/hoover");
            _pickupPart = LoadSound("Sounds/hoover"); // P735 — pickUpPartSound (shipIGC.h:1307)
            // P710 — incoming-message notification cues. Waves from
            // sounddef.mdl:297-301 (receivedobjective, receivedcomchat,
            // receivepersonalchat, receivechat).
            _chatCommand = LoadSound("Sounds/receivedobjective");
            _chatCommander = LoadSound("Sounds/receivedcomchat");
            _chatPersonal = LoadSound("Sounds/receivepersonalchat");
            _chatRegular = LoadSound("Sounds/receivechat");
            // P711 — ship low-status / combat feedback one-shots. Waves from
            // sounddef.mdl:371-374, 811-812, 348.
            _hullLow = LoadSound("Sounds/vo_sal_hullcritical");
            _ammoLow = LoadSound("Sounds/outofammo");
            _fuelLow = LoadSound("Sounds/fuellow");
            _energyLow = LoadSound("Sounds/vo_sal_lowenergy");
            _noAmmo = LoadSound("Sounds/vo_sal_noammo");
            _noFuel = LoadSound("Sounds/vo_sal_nofuel");
            _noRipcord = LoadSound("Sounds/vo_sal_noripcord");
            _shieldsOffline = LoadSound("Sounds/vo_sal_shieldsoffline"); // P717 — salShieldsOfflineSound (:791)
            _shieldActivate = LoadSound("Sounds/shieldup");      // P741 — fastShieldActivate (sounddef.mdl:1310)
            _shieldDeactivate = LoadSound("Sounds/noshld");      // P741 — fastShieldDeactivate (sounddef.mdl:1311)
            _cloakEngage = LoadSound("Sounds/vo_sal_cloakengage"); // P735 — salCloakEngageSound (:807)
            // P714 — objective & status announce one-shots. Waves from
            // sounddef.mdl:771,773,776,779,780.
            _madeCommander = LoadSound("Sounds/vo_sal_madecommander");
            _madeInvestor = LoadSound("Sounds/vo_sal_madeinvestor");
            _stolenEnemyFlag = LoadSound("Sounds/vo_stolen_enemyflag");
            _artifact = LoadSound("Sounds/vo_sal_artifact");
            _stolenFlag = LoadSound("Sounds/vo_stolen_flag");
            // P719 — station/economy + loadout one-shots (parity wave 18).
            _docked = LoadSound("Sounds/landed");
            _welcomeHome = LoadSound("Sounds/vo_sal_welcomehome");
            _boarded = LoadSound("Sounds/vo_sal_playerboarded");
            _groupWeapons = LoadSound("Sounds/groupweapons");
            _ungroupWeapons = LoadSound("Sounds/ungroupweapons");
            _mount = LoadSound("Sounds/mount");
            _unmount = LoadSound("Sounds/unmount");
            _mapClick = LoadSound("Sounds/mousedown");      // P755 — mouseclickSound (sounddef.mdl:321)
            _error = LoadSound("Sounds/ef03");              // P764 — errorSound (sounddef.mdl:272)
            _positiveClick = LoadSound("Sounds/accept");    // P764 — positiveButtonClickSound (sounddef.mdl:313)
            _cargoFull = LoadSound("Sounds/vo_sal_cargofull");
            // P736 — presence / side-change salutation one-shots. Waves
            // sounddef.mdl:825-831 (recruitsarrived / mia / quit / traitor /
            // enemyjoiners / enemyleaves). salQuitSound is vo_sal_mia in native
            // (sounddef.mdl:827-828); this port plays vo_sal_quit for the plain
            // quit so the two cues are distinguishable.
            _salRecruitsArrived = LoadSound("Sounds/vo_sal_recruitsarrived");
            _salMia = LoadSound("Sounds/vo_sal_mia");
            _salQuit = LoadSound("Sounds/vo_sal_quit");
            _salChangeSides = LoadSound("Sounds/vo_sal_traitor");
            _salEnemyJoiners = LoadSound("Sounds/vo_sal_enemyjoiners");
            _salEnemyLeaves = LoadSound("Sounds/vo_sal_enemyleaves");
            // P737 — own-ship reload type-specific VOs (vo_sal_reloading{ammo,
            // fuel,missiles,chaff,dispenser}; sounddef.mdl:814-818). The generic
            // startReloadSound reuses the already-loaded "mount" wave (_mount,
            // sounddef.mdl:289).
            _salReloadingAmmo = LoadSound("Sounds/vo_sal_reloadingammo");
            _salReloadingFuel = LoadSound("Sounds/vo_sal_reloadingfuel");
            _salReloadingMissiles = LoadSound("Sounds/vo_sal_reloadingmissiles");
            _salReloadingChaff = LoadSound("Sounds/vo_sal_reloadingchaff");
            _salReloadingDispenser = LoadSound("Sounds/vo_sal_reloadingdispenser");
            // P787a — launcher depleted VOs (vo_sal_{missilesdepleted,
            // chaffdepleted,dispenserempty}; NativeVoiceCatalog 1417-1419).
            _salMissilesDepleted = LoadSound("Sounds/vo_sal_missilesdepleted");
            _salChaffDepleted = LoadSound("Sounds/vo_sal_chaffdepleted");
            _salDispenserEmpty = LoadSound("Sounds/vo_sal_dispenserempty");
            _drop = LoadSound("Sounds/drop");
            _reloading = LoadSound("Sounds/vo_sal_reloading");
            _newTarget = LoadSound("Sounds/newtargetneutral");
            _vectorLock = LoadSound("Sounds/vector");
            _shipCantRipcord = LoadSound("Sounds/vo_sal_shipcantripcord");
            // P711 — ripcord-active + mining loops (two looped instances created
            // here, gated on/off by Game1's per-frame drivers; see
            // UpdateRipcordLoop / UpdateMiningLoop).
            _ripcordLoop = Loop("Sounds/countdownstart");
            _miningLoop = Loop("Sounds/miningsound");
            // P373 — first-sighting cues (see field docs for the confirmed
            // native enemy/friendly mapping).
            _newContactEnemy = LoadSound("Sounds/newtarget");
            _newContactFriendly = LoadSound("Sounds/newtargetenemy");
            // P374 — sector-threat alert cues. The eight detection/threatened
            // VO stems from native's c_vAlertSounds (sounddef.mdl:758-766) plus
            // the sector-secured/lost control cues (768-769).
            foreach (var stem in ClusterAlertStems)
                _clusterAlertSounds[stem] = LoadSound($"Sounds/{stem}");
            _sectorSecured = LoadSound($"Sounds/{Allegiance.Core.ClusterWarnings.SecuredSoundStem}");
            _sectorLost = LoadSound($"Sounds/{Allegiance.Core.ClusterWarnings.LostSoundStem}");
            IsLoaded = true;
            Console.WriteLine("[monogame] audio: loaded engine/cockpit/ambience/autopilot/countdown/gameover/fire/projectile/missile/warp/probe/hull/station/dev/new-contact/sector-threat cues");
        }
        catch (Exception ex)
        {
            WarnOnce($"audio failed to load, disabling sound: {ex.Message}");
            IsLoaded = false;
        }
    }

    /// <summary>
    /// Drive the real sound mix for the current frame from live state: the
    /// engine loop's volume/pitch follow <see cref="EngineSoundCurve"/> on the
    /// ship's real throttle and speed; the cockpit hum is audible only in
    /// cockpit camera mode; the sector ambience is a random one-shot bioambient
    /// variant, triggered intermittently (see <see cref="UpdateAmbience"/>).
    /// </summary>
    /// <param name="throttle">Real <c>Controls.Throttle</c> (−1..+1).</param>
    /// <param name="speed">Real <c>Velocity.Length()</c>.</param>
    /// <param name="cockpitMode">Whether the cockpit camera is active
    /// (<see cref="Rendering.CameraMode.Cockpit"/>).</param>
    /// <param name="deltaSeconds">Real elapsed time since the last <see cref="Update"/>
    /// call (<c>GameTime.ElapsedGameTime</c>), driving the ambience trigger.</param>
    /// <param name="afterburnerActive">True when <c>ShipControlState.AfterburnerButton</c>
    /// is set on the local ship's <c>StateM</c> (P220 — TK_FireBooster/Shift).</param>
    /// <param name="deltaSeconds">Real elapsed time since the last <see cref="Update"/>
    /// call (<c>GameTime.ElapsedGameTime</c>), driving the ambience trigger.</param>
    /// <param name="missileLocked">True when the local ship has FULL missile lock
    /// (P230) — the continuous <c>missilelock</c> beep plays only while locked
    /// (native <c>PlaySoundIf(m_pMissileToneSound, missileToneSound, ..., bIsPlayersShip
    /// &amp;&amp; HasMissileLock())</c>, <c>trekigc.cpp:1288-1290</c>, where
    /// <c>HasMissileLock() == (GetLock() == 1.0f)</c>, <c>trekigc.cpp:876-883</c>).</param>
    /// <param name="capitalHull">True when the local ship is a capital (non-fighter)
    /// hull — resolves the main-thruster and interior beds to the capital assets
    /// (<c>accelcapital</c>/<c>lshipint</c>; native's per-hull-type
    /// GetMainThrusterInteriorSound/GetInteriorSound, trekigc.cpp:1312-1314).</param>
    /// <param name="sidethrustActive">True when a lateral/backward thrust button is
    /// held (native <c>PlaySidethrust()</c> = backward|left|right, trekigc.cpp:953-956) —
    /// the sidethrust loop bed (SIM-0376/0377/0382/0383) plays while strafing/braking.</param>
    public void Update(float throttle, float speed, bool cockpitMode, bool afterburnerActive, float deltaSeconds, bool missileLocked = false, bool capitalHull = false, bool sidethrustActive = false)
    {
        if (!IsLoaded) return;

        UpdateAmbience(deltaSeconds);

        // P721 — the main-thruster and interior beds are resolved by hull class
        // (small accelsmall/sshipint vs capital accelcapital/lshipint), mirroring
        // native's per-hull-type GetMainThrusterInteriorSound/GetInteriorSound
        // (trekigc.cpp:1312-1314).
        var engine = capitalHull ? _engineCapital : _engine;
        if (engine is { } eng)
        {
            var (vol, pitch) = EngineSoundCurve.Mix(throttle, speed, EngineSoundCurve.MaxSpeedDefault);
            // Cockpit/chase distinction (optional, cheap): a slightly fuller
            // tone from the cockpit (you hear the hull around you), a touch
            // duller in chase. Not required by scope, but nice.
            float cockPitBoost = cockpitMode ? 0.08f : 0f;
            SetLoopVolume(eng, Math.Clamp(vol + cockPitBoost, 0f, 1f) * _masterVolume);
            Safe(() => eng.Pitch = pitch);
            EnsurePlaying(eng);
        }

        var hum = capitalHull ? _cockpitHumCapital : _cockpitHum;
        if (hum is { } h)
        {
            if (cockpitMode) EnsurePlaying(h);
            else if (h.State == SoundState.Playing) Safe(h.Stop);
        }

        if (_afterburner is { } afterburner)
        {
            if (afterburnerActive) EnsurePlaying(afterburner);
            else if (afterburner.State == SoundState.Playing) Safe(afterburner.Stop);
        }

        // P721 — the sidethrust loop bed (SIM-0376/0377/0382/0383): plays while a
        // lateral/backward thrust button is held (native PlaySidethrust(),
        // trekigc.cpp:953-956), asset resolved by hull class (accelsmall fighter /
        // accelcapital capital, sounddef.mdl:1342-1347). Native reuses ONE loop
        // instance for the interior and exterior rows, muting the other volume
        // bus by camera view; the port plays the single shared bed at a fixed
        // moderate volume for both views.
        var side = capitalHull ? _sideThrustCapital : _sideThrustSmall;
        if (side is { } s)
        {
            if (sidethrustActive)
            {
                SetLoopVolume(s, SideThrustVolume * _masterVolume);
                EnsurePlaying(s);
            }
            else if (s.State == SoundState.Playing)
            {
                Safe(s.Stop);
            }
        }

        // P230 — the missile-lock beep: a continuous loop while at FULL lock,
        // stopped otherwise (native's PlaySoundIf gate, trekigc.cpp:1289).
        if (_missileTone is { } tone)
        {
            if (missileLocked)
            {
                SetLoopVolume(tone, MissileToneVolume * _masterVolume);
                EnsurePlaying(tone);
            }
            else if (tone.State == SoundState.Playing)
            {
                Safe(tone.Stop);
            }
        }
    }

    /// <summary>Play the autopilot-engage voice chime (one-shot). No-op when
    /// audio is not loaded.</summary>
    private SoundEffect? _commandMessage;
    private bool _commandMessageAttempted;
    public void PlayCommandMessage()
    {
        if (!IsLoaded) return;
        if (!_commandMessageAttempted)
        {
            _commandMessageAttempted = true;
            try { _commandMessage = LoadSound("Sounds/receivedobjective"); }
            catch (ContentLoadException) { }
        }
        if (_commandMessage is { } sound) PlayWarnOneShot(sound);
    }

    /// <summary>WinTrek.cpp:10658; sounddef.mdl:304 (acceptCommandSound).</summary>
    public void PlayCommandAccepted()
    {
        if (IsLoaded && WeaponWaveByStem("accept") is { } sound) PlayWarnOneShot(sound);
    }

    public void PlayAutopilotEngage()
    {
        PlayWarnOneShot(_apEngage);
    }

    /// <summary>P752c — native <c>mouseoverSound</c> (sectormap.cpp:1442): the
    /// starmap hover cue when the pointer lands on a new cluster. One-shot.</summary>
    public void PlayMouseOver() => PlayWarnOneShot(_mouseover);

    /// <summary>P764 — native <c>errorSound</c> (sounddef.mdl:272), the sector
    /// map's invalid teleport-target feedback (sectormap.cpp:1362/1367).</summary>
    public void PlayError() => PlayWarnOneShot(_error);

    /// <summary>P764 — native <c>positiveButtonClickSound</c> (sounddef.mdl:313),
    /// the affirmative UI action cue (WinTrek.cpp:9269, teampane.cpp:1177,
    /// load.cpp:2235).</summary>
    public void PlayPositiveClick() => PlayWarnOneShot(_positiveClick);

    /// <summary>HUD-10 — native <c>chatKeySound</c> (<c>text</c> wave,
    /// sounddef.mdl:324): the chat-compose key click. Native plays it from
    /// <c>StartChat</c> (console.cpp:2423), every printable key while composing
    /// (console.cpp:2337) and a verb-bar press (console.cpp:1268).</summary>
    public void PlayChatKey()
    {
        if (IsLoaded) PlayOneShot(_chatKey, SoundCategory.Interface, 1f);
    }

    /// <summary>HUD-10 — native <c>paneSlideSound</c> (<c>windowslides</c> wave,
    /// sounddef.mdl:323) on an overlay-mask change
    /// (<c>ConsoleImage::SetOverlayFlags</c>, console.cpp:3272-3274).</summary>
    public void PlayPaneSlide()
    {
        if (IsLoaded) PlayOneShot(_paneSlide, SoundCategory.Interface, 1f);
    }

    /// <summary>Play the autopilot-disengage voice chime (one-shot). No-op
    /// when audio is not loaded.</summary>
    public void PlayAutopilotDisengage()
    {
        PlayWarnOneShot(_apDisengage);
    }

    /// <summary>Play the countdown-start callout (one-shot) — P216 voices the
    /// opening of the mission-start briefing rather than leaving the first
    /// seconds silent (see <see cref="CountdownSound"/>). No-op when audio is
    /// not loaded.</summary>
    public void PlayCountdownStart()
    {
        PlayWarnOneShot(_countdownStart);
    }

    /// <summary>Play the per-second countdown tick for <paramref name="second"/>
    /// seconds remaining (one-shot, 1..<see cref="CountdownSound.MaxTickSecond"/>)
    /// — <c>countdown10</c>…<c>countdown1</c>. Out-of-range seconds are a silent
    /// no-op (the caller decides eligibility via
    /// <see cref="CountdownSound.TickAssetFor"/>); this method only speaks the
    /// matching asset it already holds. No-op when audio is not loaded.</summary>
    public void PlayCountdownSecond(int second, float gain = 1f)
    {
        if (!IsLoaded || !_countdownTicks.ContainsKey(second)) return;
        // Same wave, different native templates: ripcord has -7 dB extra,
        // priority 10 and a .3-second timeout (sounddef.mdl:332-362).
        string name = (gain == RipcordCountdownSound.Gain ? "ripcord" : "countdown") + second + "Sound";
        var cue = NativeVoiceCatalog.Definitions[name];
        if (gain != 1f && gain != RipcordCountdownSound.Gain)
            cue = cue with { GainDb = cue.GainDb + 20 * Math.Log10(Math.Max(gain, float.Epsilon)) };
        PlayVoice(cue);
    }

    /// <summary>P711 — play the coarse mission-clock milestone VO
    /// (<c>vo_time_10min</c>/<c>5min</c>/<c>1min</c>/<c>30sec</c>), native
    /// <c>countdown10min/5min/1min/30Sound</c> (WinTrek.cpp:11157-11169;
    /// SIM-0300..0303). <paramref name="assetStem"/> is a
    /// <c>vo_time_*</c> stem from <see cref="CountdownSound.MilestoneAssetFor"/>.
    /// No-op when not loaded or the stem is unrecognised.</summary>
    public void PlayCountdownMilestone(string assetStem)
    {
        if (!IsLoaded) return;
        int sec = assetStem switch
        {
            "vo_time_10min" => 600,
            "vo_time_5min" => 300,
            "vo_time_1min" => 60,
            "vo_time_30sec" => 30,
            _ => -1,
        };
        if (sec > 0 && _countdownMilestones.TryGetValue(sec, out var sfx))
            PlayWarnOneShot(sfx);
    }

    /// <summary>Play the game-over callout (one-shot) — <c>vo_ma_gameover</c>.
    /// No-op when audio is not loaded.</summary>
    public void PlayGameOver()
    {
        PlayWarnOneShot(_gameOver);
    }

    /// <summary>P578 — play the critical-message cue (<c>criticalmessage</c>,
    /// native <c>newCriticalMsgSound</c>) for a critical PostText line
    /// (trekigc.cpp:3009,3075). No-op when audio is not loaded.</summary>
    public void PlayCriticalMessage()
    {
        PlayWarnOneShot(_criticalMsg);
    }

    /// <summary>P578 — play the non-critical-message cue
    /// (<c>noncriticalmessage</c>, native <c>newNonCriticalMsgSound</c>) for a
    /// non-critical PostText line (trekigc.cpp:3014,3080). No-op when audio is
    /// not loaded.</summary>
    public void PlayNonCriticalMessage()
    {
        PlayWarnOneShot(_nonCriticalMsg);
    }

    /// <summary>P578 — play the rescue cue (<c>hoover</c>, native
    /// <c>rescuePlayerSound</c>) when this player rescues a lifepod
    /// (appmsg.cpp:325). No-op when audio is not loaded.</summary>
    public void PlayRescue()
    {
        PlayWarnOneShot(_rescuePlayer);
    }

    /// <summary>P578 — play the jump cue (<c>jumpin</c>, native
    /// <c>jumpSound</c>) when this player is rescued back to base
    /// (appmsg.cpp:339). No-op when audio is not loaded.</summary>
    public void PlayJump()
    {
        PlayWarnOneShot(_jump);
    }

    /// <summary>P755 — play the teleport-pane board-a-ship cue (<c>jumpin</c>,
    /// native <c>personalJumpSound</c>, sounddef.mdl:274) when the teleport pane
    /// boards a ship (teleportpane.cpp:319-320, the OT_ship branch of
    /// <c>OnButtonTeleport</c>). Same wave as <see cref="PlayJump"/>, distinct
    /// native trigger site. No-op when audio is not loaded.</summary>
    public void PlayPersonalJump()
    {
        PlayWarnOneShot(_jump);
    }

    // ── P710 — wealth announcements (clintlib/appmsg.cpp) ────────────────────
    /// <summary>Play the payday cue (<c>payday</c>, native <c>paydaySound</c>,
    /// appmsg.cpp:1313) on each PAYDAY broadcast. No-op when audio is not
    /// loaded.</summary>
    public void PlayPayday() => PlayWarnOneShot(_payday);

    /// <summary>Play the powerup-pickup cue (<c>PowerupShield</c>, native
    /// <c>pickUpPowerupSound</c>, appmsg.cpp:282). No-op when not loaded.</summary>
    public void PlayPickupPowerup() => PlayWarnOneShot(_pickupPowerup);

    /// <summary>Play the development-pickup cue (<c>PowerupDevelopment</c>,
    /// native <c>pickUpDevelopmentSound</c>, appmsg.cpp:286 — also reused for a
    /// flag pickup, :303, whose dedicated flag cue is NYI in native). No-op
    /// when not loaded.</summary>
    public void PlayPickupDevelopment() => PlayWarnOneShot(_pickupDevelopment);

    /// <summary>Play the cash-pickup cue (<c>hoover</c>, native
    /// <c>pickUpCashSound</c>, appmsg.cpp:291). No-op when not loaded.</summary>
    public void PlayPickupCash() => PlayWarnOneShot(_pickupCash);

    /// <summary>P735 — play the part-pickup cue (<c>hoover</c>, native
    /// <c>pickUpPartSound</c>, shipIGC.h:1307, sounddef.mdl:278). No-op when
    /// not loaded.</summary>
    public void PlayPickupPart() => PlayWarnOneShot(_pickupPart);

    // ── P710 — incoming-message notification cues (trekigc.cpp:4536-4560) ────
    /// <summary>Play the objective/command cue (<c>receivedobjective</c>, native
    /// <c>newCommandMsgSound</c>, trekigc.cpp:4540). No-op when not loaded.</summary>
    public void PlayChatCommand() => PlayWarnOneShot(_chatCommand);

    /// <summary>Play the commander cue (<c>receivedcomchat</c>, native
    /// <c>newChatMsgFromCommanderSound</c>, trekigc.cpp:4546/4555). No-op when
    /// not loaded.</summary>
    public void PlayChatCommander() => PlayWarnOneShot(_chatCommander);

    /// <summary>Play the personal cue (<c>receivepersonalchat</c>, native
    /// <c>newPersonalMsgSound</c>, trekigc.cpp:4548). No-op when not loaded.</summary>
    public void PlayChatPersonal() => PlayWarnOneShot(_chatPersonal);

    /// <summary>Play the regular-chat cue (<c>receivechat</c>, native
    /// <c>newChatMsgSound</c>, trekigc.cpp:4557; the offline variant at :4560
    /// reuses the same wave at reduced gain, so <paramref name="offline"/>
    /// drops the gain by 10 dB to match <c>GainSound(-10, …)</c>,
    /// sounddef.mdl:299). No-op when not loaded.</summary>
    public void PlayChatRegular(bool offline = false)
    {
        if (!IsLoaded) return;
        PlayOneShot(_chatRegular, SoundCategory.Warning, offline ? 0.318f : 1f); // -10 dB ≈ 0.316 gain
    }

    // ── P711 — ship low-status / combat feedback one-shots ─────────────────
    // (trekigc.cpp:1264-1275 one-shots; appmsg.cpp:970 ripcord-deny). All are
    // own-ship announce VO, so they ride the Warning-priority voice path like
    // the other cockpit one-shots (MutexSal, sounddef.mdl:71-79).

    /// <summary>Play the hull-critical cue (<c>vo_sal_hullcritical</c>, native
    /// <c>hullLowSound</c>, trekigc.cpp:1264). No-op when not loaded.</summary>
    public void PlayHullLow() => PlayWarnOneShot(_hullLow);

    /// <summary>Play the ammo-low cue (<c>outofammo</c>, native
    /// <c>ammoLowSound</c>, trekigc.cpp:1266). No-op when not loaded.</summary>
    public void PlayAmmoLow() => PlayWarnOneShot(_ammoLow);

    /// <summary>Play the fuel-low cue (<c>fuellow</c>, native
    /// <c>fuelLowSound</c>, trekigc.cpp:1268). No-op when not loaded.</summary>
    public void PlayFuelLow() => PlayWarnOneShot(_fuelLow);

    /// <summary>Play the energy-low cue (<c>vo_sal_lowenergy</c>, native
    /// <c>energyLowSound</c>, trekigc.cpp:1270). No-op when not loaded.</summary>
    public void PlayEnergyLow() => PlayWarnOneShot(_energyLow);

    /// <summary>Play the no-ammo attempt cue (<c>vo_sal_noammo</c>, native
    /// <c>salNoAmmoSound</c>, trekigc.cpp:1273). No-op when not loaded.</summary>
    public void PlayNoAmmo() => PlayWarnOneShot(_noAmmo);

    /// <summary>Play the no-fuel attempt cue (<c>vo_sal_nofuel</c>, native
    /// <c>salNoFuelSound</c>, trekigc.cpp:1275). No-op when not loaded.</summary>
    public void PlayNoFuel() => PlayWarnOneShot(_noFuel);

    /// <summary>Play the ripcord-denied cue (<c>vo_sal_noripcord</c>, native
    /// <c>salNoRipcordSound</c>, appmsg.cpp:970 / clintlib.h:1175). No-op when
    /// not loaded.</summary>
    public void PlayNoRipcord() => PlayWarnOneShot(_noRipcord);

    /// <summary>P717 — play the shields-offline cue (<c>vo_sal_shieldsoffline</c>,
    /// native <c>salShieldsOfflineSound</c>, shieldIGC.cpp:186). No-op when not
    /// loaded.</summary>
    public void PlayShieldsOffline() => PlayWarnOneShot(_shieldsOffline);
    public void PlayShieldActivate() => PlayWarnOneShot(_shieldActivate);
    public void PlayShieldDeactivate() => PlayWarnOneShot(_shieldDeactivate);

    /// <summary>P735 — play the cloak-engage cue (<c>vo_sal_cloakengage</c>,
    /// native <c>salCloakEngageSound</c>, shipIGC.cpp:1608). No-op when not
    /// loaded.</summary>
    public void PlayCloakEngage() => PlayWarnOneShot(_cloakEngage);

    /// <summary>P714 — play the "you became team leader" cue (<c>vo_sal_madecommander</c>,
    /// native <c>commanderSound</c>, appmsg.cpp:2965). No-op when not loaded.</summary>
    public void PlayMadeCommander() => PlayWarnOneShot(_madeCommander);

    /// <summary>P714 — play the "you gained your first investor" cue
    /// (<c>vo_sal_madeinvestor</c>, native <c>investorSound</c>, appmsg.cpp:1238).
    /// No-op when not loaded.</summary>
    public void PlayMadeInvestor() => PlayWarnOneShot(_madeInvestor);

    /// <summary>P714 — play the enemy-flag-stolen cue (<c>vo_stolen_enemyflag</c>,
    /// native <c>enemyFlagLostSound</c>, appmsg.cpp:3149). No-op when not loaded.</summary>
    public void PlayStolenEnemyFlag() => PlayWarnOneShot(_stolenEnemyFlag);

    /// <summary>P714 — play the artifact-found cue (<c>vo_sal_artifact</c>, native
    /// <c>artifactFoundSound</c>, appmsg.cpp:3156). No-op when not loaded.</summary>
    public void PlayArtifactFound() => PlayWarnOneShot(_artifact);

    /// <summary>P714 — play the own-flag-stolen cue (<c>vo_stolen_flag</c>, native
    /// <c>flagLostSound</c>, appmsg.cpp:3169). No-op when not loaded.</summary>
    public void PlayStolenFlag() => PlayWarnOneShot(_stolenFlag);

    /// <summary>P719 — play the docked cue (<c>landed</c>, native
    /// <c>dockedSound</c>, trekigc.cpp:2859). No-op when not loaded.</summary>
    public void PlayDocked() => PlayWarnOneShot(_docked);

    /// <summary>P719 — play the welcome-home VO (<c>vo_sal_welcomehome</c>,
    /// native <c>salWelcomeHomeSound</c>, trekigc.cpp:2860). No-op when not
    /// loaded.</summary>
    public void PlayWelcomeHome() => PlayWarnOneShot(_welcomeHome);

    /// <summary>P719 — play the boarded cue (<c>vo_sal_playerboarded</c>, native
    /// <c>boardSound</c>, appmsg.cpp:2071). No-op when not loaded.</summary>
    public void PlayBoarded() => PlayWarnOneShot(_boarded);

    /// <summary>P719 — play the group-weapons cue (<c>groupweapons</c>, native
    /// <c>groupWeaponsSound</c>, WinTrek.cpp:9053,9529). No-op when not loaded.</summary>
    public void PlayGroupWeapons() => PlayWarnOneShot(_groupWeapons);

    /// <summary>P719 — play the ungroup-weapons cue (<c>ungroupweapons</c>, native
    /// <c>ungroupWeaponsSound</c>, WinTrek.cpp:9047,9531). No-op when not loaded.</summary>
    public void PlayUngroupWeapons() => PlayWarnOneShot(_ungroupWeapons);

    /// <summary>P719 — play the group/ungroup-weapons cue for the group-fire
    /// toggle (<c>groupweapons</c> when <paramref name="grouped"/>, else
    /// <c>ungroupweapons</c>; native <c>groupWeaponsSound</c>/
    /// <c>ungroupWeaponsSound</c>, WinTrek.cpp:9053,9047,9529,9531). No-op when
    /// not loaded.</summary>
    public void PlayGroupFire(bool grouped) =>
        PlayWarnOneShot(grouped ? _groupWeapons : _ungroupWeapons);

    /// <summary>P719 — play the mount cue (<c>mount</c>, native <c>mountSound</c>,
    /// WinTrek.cpp:9098). No-op when not loaded.</summary>
    public void PlayMountPart() => PlayWarnOneShot(_mount);

    /// <summary>P755 — play the sector-map selection click (<c>mousedown</c>,
    /// native <c>mouseclickSound</c>, sounddef.mdl:321) when a map click lands on
    /// a cluster (sectormap.cpp:1523). No-op when not loaded.</summary>
    public void PlayMapClick() => PlayWarnOneShot(_mapClick);

    /// <summary>P719 — play the unmount cue (<c>unmount</c>, native
    /// <c>unmountSound</c>, WinTrek.cpp:9102). No-op when not loaded.</summary>
    public void PlayUnmountPart() => PlayWarnOneShot(_unmount);

    /// <summary>P719 — dispatch a mount/unmount one-shot from an in-flight part
    /// action (native mountSound / unmountSound, WinTrek.cpp:9098,9102).</summary>
    public void PlayWarnPartSound(Allegiance.Core.FlightPartAction action)
    {
        if (action == Allegiance.Core.FlightPartAction.Unmount) PlayUnmountPart();
        else PlayMountPart();
    }

    /// <summary>P719 — play the cargo-full VO (<c>vo_sal_cargofull</c>, native
    /// <c>salCargoFullSound</c>, WinTrek.cpp:9114). No-op when not loaded.</summary>
    public void PlayCargoFull() => PlayWarnOneShot(_cargoFull);

    // ── P736 — presence / side-change salutation one-shots (parity wave 23) ──
    // Six client-side cues mirroring native's RemovePlayerFromSide /
    // AddPlayerToSide presence salutations (clintlib.cpp:3867-4172). All are
    // one-shots on the same ClientSoundEvent path, played through the voice-
    // budgeted warn-one-shot lane (MutexSal grouping).

    /// <summary>P736 — play the same-side quit salutation (<c>vo_sal_quit</c>;
    /// native <c>salQuitSound</c>, clintlib.cpp:3952-3991).</summary>
    public void PlaySalQuit() => PlayWarnOneShot(_salQuit);

    /// <summary>P736 — play the side-change salutation (<c>vo_sal_traitor</c>;
    /// native <c>salChangeSidesSound</c>, clintlib.cpp:3975, sounddef.mdl:829).</summary>
    public void PlaySalChangeSides() => PlayWarnOneShot(_salChangeSides);

    /// <summary>P736 — play the MIA salutation (<c>vo_sal_mia</c>; native
    /// <c>salMIASound</c>, clintlib.cpp:3999, sounddef.mdl:826).</summary>
    public void PlaySalMia() => PlayWarnOneShot(_salMia);

    /// <summary>P736 — play the enemy-leaves salutation (<c>vo_sal_enemyleaves</c>;
    /// native <c>salEnemyLeavesSound</c>, clintlib.cpp:4006-4009,
    /// sounddef.mdl:831).</summary>
    public void PlaySalEnemyLeaves() => PlayWarnOneShot(_salEnemyLeaves);

    /// <summary>P736 — play the recruits-arrived salutation
    /// (<c>vo_sal_recruitsarrived</c>; native <c>salRecruitsArrivedSound</c>,
    /// clintlib.cpp:4155, sounddef.mdl:825).</summary>
    public void PlaySalRecruitsArrived() => PlayWarnOneShot(_salRecruitsArrived);

    /// <summary>P736 — play the enemy-joiners salutation
    /// (<c>vo_sal_enemyjoiners</c>; native <c>salEnemyJoinersSound</c>,
    /// clintlib.cpp:4162, sounddef.mdl:830).</summary>
    public void PlaySalEnemyJoiners() => PlayWarnOneShot(_salEnemyJoiners);

    // ── P737 — own-ship reload one-shots (SIM-0263..0266, 0443/0444/0449/
    // 0450/0451). Native plays the type-specific sal + the generic
    // startReloadSound (SFX "mount") on every successful reload; the port
    // observes the own-ship RELOAD(129) relay (see ClientWorld). The five
    // sal VOs are own-ship announces on the Warning path like every other
    // sal; startReloadSound reuses the already-loaded "mount" wave as an
    // action SFX.

    /// <summary>P737 — reloading ammo (<c>vo_sal_reloadingammo</c>;
    /// salReloadingAmmoSound, shipIGC.h:617, sounddef.mdl:818).</summary>
    public void PlaySalReloadingAmmo() => PlayWarnOneShot(_salReloadingAmmo);

    /// <summary>P737 — reloading fuel (<c>vo_sal_reloadingfuel</c>;
    /// salReloadingFuelSound, shipIGC.h:647, sounddef.mdl:814).</summary>
    public void PlaySalReloadingFuel() => PlayWarnOneShot(_salReloadingFuel);

    /// <summary>P737 — reloading missiles (<c>vo_sal_reloadingmissiles</c>;
    /// salReloadingMissilesSound, clintlib.cpp:3221, sounddef.mdl:816).</summary>
    public void PlaySalReloadingMissiles() => PlayWarnOneShot(_salReloadingMissiles);

    /// <summary>P737 — reloading chaff (<c>vo_sal_reloadingchaff</c>;
    /// salReloadingChaffSound, clintlib.cpp:3400, sounddef.mdl:815).</summary>
    public void PlaySalReloadingChaff() => PlayWarnOneShot(_salReloadingChaff);

    /// <summary>P737 — reloading dispenser (<c>vo_sal_reloadingdispenser</c>;
    /// salReloadingDispenserSound, clintlib.cpp:3403, sounddef.mdl:817).</summary>
    public void PlaySalReloadingDispenser() => PlayWarnOneShot(_salReloadingDispenser);

    /// <summary>P787a — missiles depleted (<c>vo_sal_missilesdepleted</c>;
    /// salMissilesDepletedSound, clintlib.cpp:3224).</summary>
    public void PlaySalMissilesDepleted() => PlayWarnOneShot(_salMissilesDepleted);

    /// <summary>P787a — chaff depleted (<c>vo_sal_chaffdepleted</c>;
    /// salChaffDepletedSound, clintlib.cpp:3412).</summary>
    public void PlaySalChaffDepleted() => PlayWarnOneShot(_salChaffDepleted);

    /// <summary>P787a — dispenser empty (<c>vo_sal_dispenserempty</c>;
    /// salDispenserEmptySound, clintlib.cpp:3417).</summary>
    public void PlaySalDispenserEmpty() => PlayWarnOneShot(_salDispenserEmpty);

    /// <summary>P737 — the generic reload-start SFX (<c>mount</c>; native
    /// startReloadSound, clintlib.cpp:3219,3404 / shipIGC.h:617,647,
    /// sounddef.mdl:289), played on every successful reload of any
    /// pool/launcher. Reuses the already-loaded <c>_mount</c> wave.</summary>
    public void PlayStartReload()
    {
        if (IsLoaded) PlayOneShot(_mount, SoundCategory.WeaponFire, 1f);
    }

    /// <summary>P719 — play the drop cue (<c>drop</c>, native <c>dropSound</c>,
    /// WinTrek.cpp:9719). No-op when not loaded.</summary>
    public void PlayDropPart() => PlayWarnOneShot(_drop);

    /// <summary>P719 — play the reload VO (<c>vo_sal_reloading</c>, native
    /// <c>salReloadingSound</c>, WinTrek.cpp:9693). No-op when not loaded.</summary>
    public void PlayReloading() => PlayWarnOneShot(_reloading);

    /// <summary>P723 — play the one-shot \"new target\" cue (native
    /// <c>newTargetSound</c> = ImportWave(\"newtargetneutral\"),
    /// sounddef.mdl:311) whenever the pilot selects a NEW target
    /// (native SetTarget, WinTrek.cpp:8827 — <c>GetCommandTarget(c_cmdCurrent)
    /// != pmodel</c>). Distinct from the P373 first-sighting new-contact cue
    /// (newtarget/newtargetenemy); this is the neutral \"you retargeted\" blip.
    /// No-op when audio is not loaded.</summary>
    public void PlayNewTarget() => PlayWarnOneShot(_newTarget);

    /// <summary>P723 — play the one-shot vector-lock cue (native
    /// <c>vectorLockSound</c> = ImportWave(\"vector\"), sounddef.mdl:295) on the
    /// LockVector key press, native TK_LockVector (WinTrek.cpp:9463). No-op when
    /// audio is not loaded.</summary>
    public void PlayVectorLock() => PlayWarnOneShot(_vectorLock);

    /// <summary>P723 — play the one-shot \"can't ripcord\" VO (native
    /// <c>salShipCantRipcordSound</c> = ImportWave(\"vo_sal_shipcantripcord\"),
    /// sounddef.mdl:350) when the ripcord key is pressed on a hull that
    /// structurally cannot ripcord (native TK_Ripcord,
    /// WinTrek.cpp:9484 — <c>GetBaseHullType()->HasCapability(c_habmNoRipcord)</c>).
    /// No-op when audio is not loaded.</summary>
    public void PlayShipCantRipcord() => PlayWarnOneShot(_shipCantRipcord);

    /// <summary>P711 — gate the ripcord-active loop (<c>countdownstart</c>,
    /// native <c>ripcordOnLoopSound</c>, trekigc.cpp:1287): a looping one-shot
    /// instance plays while <paramref name="active"/> is true and stops
    /// otherwise — native's <c>PlaySoundIf(m_pRipcordingSound, ...)</c> keeps
    /// the sound started for as long as the local ship's ripcord countdown
    /// runs. No-op when not loaded.</summary>
    public void UpdateRipcordLoop(bool active)
    {
        if (!IsLoaded || _ripcordLoop is not { } loop) return;
        if (active) { SetLoopVolume(loop, 0.8f * _masterVolume); EnsurePlaying(loop); }
        else if (loop.State == SoundState.Playing) Safe(loop.Stop);
    }

    /// <summary>P711 — gate the mining loop (<c>miningsound</c>, native
    /// <c>miningSound</c>, trekigc.cpp:1293): plays while the local ship is
    /// actively mining a rock and stops otherwise. No-op when not loaded.</summary>
    public void UpdateMiningLoop(bool mining)
    {
        if (!IsLoaded || _miningLoop is not { } loop) return;
        if (mining) { SetLoopVolume(loop, 0.7f * _masterVolume); EnsurePlaying(loop); }
        else if (loop.State == SoundState.Playing) Safe(loop.Stop);
    }


    /// <summary>P232/P465 — play the per-weapon single-shot cue (one-shot) for the
    /// given weapon's <c>singleShotSound</c> id, resolved to its wave via
    /// <see cref="WeaponSoundMap"/> (a specific real weapon, e.g. id 531 →
    /// <c>dn_ptvulcan1shoot.ogg</c>). Any unlisted id — including the reserved 0
    /// of the non-firing targeting aid — falls back to <c>pulselaser</c>. The
    /// wave is loaded lazily on first use and cached by stem. Fired once per
    /// local shot, mirroring native's <c>fFiringShot()</c> →
    /// <c>StartSound(GetSingleShotSound(), …)</c> path (trekigc.cpp:1005-1012).
    /// No-op when audio is not loaded.</summary>
    public void PlayWeaponFire(short singleShotSoundId)
    {
        if (!IsLoaded) return;
        var sfx = WeaponWaveByStem(WeaponSoundMap.SingleShotStem(singleShotSoundId));
        if (sfx is not null) PlayOneShot(sfx, SoundCategory.WeaponFire, 1f);
    }

    /// <summary>P232 — play the fallback weapon-fire one-shot (<c>pulselaser</c>),
    /// kept for the non-own projectile ambient path (other ships' bolts near the
    /// listener) which carries no weapon id — P465's out-of-scope "other ships"
    /// cue stays on the single default.</summary>
    public void PlayWeaponFire()
    {
        if (IsLoaded) PlayOneShot(_fireSound, SoundCategory.WeaponFire, 1f);
    }

    /// <summary>P465 — begin (or keep) a mount's sustained-fire burst loop: a
    /// looping <c>SoundEffectInstance</c> of the weapon's <c>burstSound</c> wave,
    /// keyed by the firing mount id. Native keeps one such per-mount loop while
    /// <c>fFiringBurst</c> is true (trekigc.cpp:999-1004); this POC's own-shot
    /// echo drives it for the local ship's held fire. Starting an already-playing
    /// mount is a no-op. Falls back to <c>pulselaser</c> for an unlisted id.
    /// No-op when audio is not loaded.</summary>
    public void StartWeaponBurst(int mount, short burstSoundId)
    {
        if (!IsLoaded || _weaponBursts.ContainsKey(mount)) return;
        var sfx = WeaponWaveByStem(WeaponSoundMap.BurstStem(burstSoundId));
        if (sfx is null) return;
        var inst = sfx.CreateInstance();
        inst.IsLooped = true;
        Safe(inst.Play);
        _weaponBursts[mount] = inst;
        Track(inst, SoundCategory.WeaponBurst, isLoop: true, essential: false,
            loudness: 1f, audible: true, started: true);
    }

    /// <summary>P465 — stop (and release) a mount's burst loop. Safe to call on a
    /// mount with no tracked loop. No-op when audio is not loaded.</summary>
    public void StopWeaponBurst(int mount)
    {
        if (!IsLoaded) return;
        if (_weaponBursts.Remove(mount, out var inst))
        {
            ForgetVoice(inst);
            if (inst.State == SoundState.Playing) Safe(inst.Stop);
            Safe(inst.Dispose);
        }
    }

    /// <summary>P465 — stop every mount's burst loop (all fire released, or teardown).</summary>
    public void StopAllWeaponBursts()
    {
        foreach (var mount in _weaponBursts.Keys.ToList())
            StopWeaponBurst(mount);
    }

    /// <summary>Resolve a weapon wave stem to a loaded <see cref="SoundEffect"/>:
    /// the fallback stem returns the always-loaded <c>_fireSound</c>; any other
    /// stem is loaded lazily and cached (failed loads are remembered so a missing
    /// asset is attempted once, then stays silent — mirroring <see cref="_managedVoices"/>).</summary>
    private SoundEffect? WeaponWaveByStem(string stem)
    {
        if (stem == WeaponSoundMap.FallbackStem) return _fireSound;
        if (_weaponShotFailures.Contains(stem)) return null;
        if (_weaponShots.TryGetValue(stem, out var sfx)) return sfx;
        try
        {
            var loaded = LoadSound($"Sounds/{stem}");
            _weaponShots[stem] = loaded;
            return loaded;
        }
        catch (Exception ex)
        {
            WarnOnce($"weapon sound asset unavailable ({stem}): {ex.Message}");
            _weaponShotFailures.Add(stem);
            return null;
        }
    }

    /// <summary>P232 — begin a projectile's in-flight ambient loop (a looped
    /// <c>erbeam.ogg</c> instance keyed by the projectile entity's reference
    /// identity), seeded at the given volume. Starts it only if one is not
    /// already playing for that projectile. No-op when audio is not loaded.</summary>
    public void StartProjectileAmbient(ProjectileEntity key, float volume)
    {
        if (!IsLoaded || key is null || _projectileAmbients.ContainsKey(key)) return;
        var inst = _projectileAmbient.CreateInstance();
        inst.IsLooped = true;
        SetLoopVolume(inst, volume);
        Safe(inst.Play);
        _projectileAmbients[key] = inst;
        Track(inst, SoundCategory.ProjectileAmbient, isLoop: true, essential: false,
            loudness: volume, audible: volume > 0f, started: true);
    }

    /// <summary>P232 — refresh a live projectile's ambient loop volume for the
    /// current listener distance (0..1). No-op if no ambient is tracked for
    /// that projectile or audio is not loaded.</summary>
    public void UpdateProjectileAmbient(ProjectileEntity key, float volume)
    {
        if (!IsLoaded || key is null) return;
        if (_projectileAmbients.TryGetValue(key, out var inst))
            SetTrackedLoudness(inst, volume);
    }

    /// <summary>P232 — stop (and release) a projectile's ambient loop. Called
    /// when the projectile expires or leaves audible range; safe to call on a
    /// projectile with no tracked ambient. No-op when audio is not loaded.</summary>
    public void StopProjectileAmbient(ProjectileEntity key)
    {
        if (!IsLoaded || key is null) return;
        if (_projectileAmbients.Remove(key, out var inst))
        {
            ForgetVoice(inst);
            if (inst.State == SoundState.Playing) Safe(inst.Stop);
            Safe(inst.Dispose);
        }
    }

    /// <summary>P237 — begin a warp's ambient loop (a looped <c>alephloop.ogg</c>
    /// instance keyed by the warp's real <c>WarpId</c>), seeded at the given
    /// volume. Starts it only if one is not already playing for that warp. No-op
    /// when audio is not loaded.</summary>
    public void StartWarpAmbient(short warpId, float volume)
    {
        if (!IsLoaded || _warpAmbients.ContainsKey(warpId)) return;
        var inst = _warpAmbient.CreateInstance();
        inst.IsLooped = true;
        SetLoopVolume(inst, volume);
        Safe(inst.Play);
        _warpAmbients[warpId] = inst;
        Track(inst, SoundCategory.WorldAmbient, isLoop: true, essential: false,
            loudness: volume, audible: volume > 0f, started: true);
    }

    /// <summary>P237 — refresh a live warp's ambient loop volume for the current
    /// listener distance (0..1) — the "increases as you get close" swell. No-op if
    /// no ambient is tracked for that warp or audio is not loaded.</summary>
    public void UpdateWarpAmbient(short warpId, float volume)
    {
        if (!IsLoaded) return;
        if (_warpAmbients.TryGetValue(warpId, out var inst))
            SetTrackedLoudness(inst, volume);
    }

    /// <summary>P237 — stop (and release) a warp's ambient loop. Called when the
    /// warp leaves audible range or the listener transits away; safe to call on a
    /// warp with no tracked ambient. No-op when audio is not loaded.</summary>
    public void StopWarpAmbient(short warpId)
    {
        if (!IsLoaded) return;
        if (_warpAmbients.Remove(warpId, out var inst))
        {
            ForgetVoice(inst);
            if (inst.State == SoundState.Playing) Safe(inst.Stop);
            Safe(inst.Dispose);
        }
    }

    /// <summary>P236 — play the missile-launch one-shot (<c>lrgslideout.ogg</c>),
    /// positioned at the firing ship (native
    /// <c>PlaySoundEffect(pMagazine->GetMissileType()->GetLaunchSound(), pShip)</c>,
    /// clintlib.cpp:3103). In this POC the local ship is the firer, so it is a
    /// non-positional cockpit-range one-shot (no 3D emitter yet). No-op when audio
    /// is not loaded.</summary>
    public void PlayMissileLaunch()
    {
        if (IsLoaded) PlayOneShot(_missileLaunch, SoundCategory.WeaponFire, 1f);
    }

    /// <summary>P236 — begin a missile's in-flight ambient loop (a looped
    /// <c>mmissile.ogg</c> instance keyed by the missile's real <c>MissileID</c>),
    /// seeded at the given volume. Starts it only if one is not already playing for
    /// that missile. No-op when audio is not loaded.</summary>
    public void StartMissileAmbient(short missileId, float volume)
    {
        if (!IsLoaded || _missileAmbients.ContainsKey(missileId)) return;
        var inst = _missileAmbient.CreateInstance();
        inst.IsLooped = true;
        SetLoopVolume(inst, volume);
        Safe(inst.Play);
        _missileAmbients[missileId] = inst;
        Track(inst, SoundCategory.MissileAmbient, isLoop: true, essential: false,
            loudness: volume, audible: volume > 0f, started: true);
    }

    /// <summary>P236 — refresh a live missile's ambient loop volume for the current
    /// listener distance (0..1). No-op if no ambient is tracked for that missile or
    /// audio is not loaded.</summary>
    public void UpdateMissileAmbient(short missileId, float volume)
    {
        if (!IsLoaded) return;
        if (_missileAmbients.TryGetValue(missileId, out var inst))
            SetTrackedLoudness(inst, volume);
    }

    /// <summary>P236 — stop (and release) a missile's ambient loop. Called when the
    /// missile leaves audible range or is destroyed; safe to call on a missile with
    /// no tracked ambient. No-op when audio is not loaded.</summary>
    public void StopMissileAmbient(short missileId)
    {
        if (!IsLoaded) return;
        if (_missileAmbients.Remove(missileId, out var inst))
        {
            ForgetVoice(inst);
            if (inst.State == SoundState.Playing) Safe(inst.Stop);
            Safe(inst.Dispose);
        }
    }

    /// <summary>P264 — apply a frame's directive for the incoming-missile warning
    /// loop (<c>missilelockonme</c>), from the pure
    /// <see cref="MissileWarningController"/>. <see cref="MissileWarningAction.Arm"/>
    /// starts the loop at the current best-lock pitch; <see cref="MissileWarningAction.Pitch"/>
    /// refreshes the pitch while it stays armed (native <c>SetPitch(0.75 + fBestLock/2)</c>,
    /// trekigc.cpp:5065); <see cref="MissileWarningAction.Disarm"/> stops it. No-op
    /// when audio is not loaded.</summary>
    public void UpdateMissileWarning(MissileWarningCommand cmd)
    {
        if (!IsLoaded || _missileWarning is not { } w) return;
        switch (cmd.Action)
        {
            case MissileWarningAction.Arm:
                if (w.State != SoundState.Playing) Safe(w.Play);
                Safe(() => w.Pitch = MissileWarningController.PitchFor(cmd.BestLock));
                SetLoopVolume(w, MissileWarningVolume * _masterVolume);
                break;
            case MissileWarningAction.Pitch:
                Safe(() => w.Pitch = MissileWarningController.PitchFor(cmd.BestLock));
                break;
            case MissileWarningAction.Disarm:
                if (w.State == SoundState.Playing) Safe(w.Stop);
                break;
        }
    }

    /// <summary>P236 — play the correctly-tiered missile-explosion one-shot
    /// (<c>explosionshockwave</c>/<c>ef24</c>/<c>ef120</c>.ogg) at
    /// <paramref name="volume"/> (distance-scaled), for a detonation at the given
    /// scale tier (native trekigc.cpp:97-101). Uses MonoGame's self-managed
    /// one-shot overload (<see cref="SoundEffect.Play(float, float, float)"/>), which
    /// creates + auto-releases the instance on completion — no manual tracking for a
    /// fire-and-forget boom. No-op when audio is not loaded or the tier has no asset.</summary>
    public void PlayMissileExplosion(ExplosionSoundTier tier, float volume)
    {
        if (!IsLoaded) return;
        if (_explosions.TryGetValue(tier, out var sfx))
            PlayOneShot(sfx, ExplosionTierCategory(tier), volume);
    }

    /// <summary>Map an explosion tier to its budget category — native tiers
    /// large/medium/small (largeExplosionSound sounddef.mdl:268,
    /// mediumExplosionSound :269, smallExplosionSound :270) map onto the
    /// P614 cut-through ladder big→ship→detonation.</summary>
    private static SoundCategory ExplosionTierCategory(ExplosionSoundTier tier) => tier switch
    {
        ExplosionSoundTier.Large => SoundCategory.ExplosionBig,
        ExplosionSoundTier.Medium => SoundCategory.ExplosionShip,
        _ => SoundCategory.ExplosionDetonation,
    };

    /// <summary>P574 — play the correctly-tiered world-explosion one-shot
    /// (<c>explosionshockwave</c>/<c>ef24</c>/<c>ef120</c>.ogg) at
    /// <paramref name="volume"/> (distance-scaled) for ANY world explosion
    /// (ship/station/asteroid/mine blast) in the player's cluster — the native
    /// tiered positional sound for every <c>AddExplosion</c>
    /// (<c>trekigc.cpp:96-101</c>). Semantically identical to
    /// <see cref="PlayMissileExplosion"/> (same tiered assets); kept as a
    /// distinct entry point so the caller's intent (a world blast, not a missile
    /// detonation) is explicit and auditable. No-op when audio is not loaded or
    /// the tier has no asset.</summary>
    public void PlayWorldExplosion(ExplosionSoundTier tier, float volume) => PlayMissileExplosion(tier, volume);

    /// <summary>P463 — play one of the four projectile hit one-shots (native
    /// <c>ThingSite::RegisterHit</c> choosing myShield/myHull/otherShield/otherHull,
    /// trekigc.cpp:2373-2385) at <paramref name="volume"/> (distance-scaled for
    /// "other" cues). My-shield and other-shield share the hitmeshield1-3 pool and
    /// my-hull the hitmearmor1-3 pool (native's otherShieldHitSound IS
    /// myShieldHitSound at priority −1.6, sounddef.mdl:262); other-hull is the
    /// single ef120. The variant is picked uniformly at random among the 1-3
    /// pool, exactly native's <c>RandomSound([...])</c>. The caller has already
    /// applied native's once-per-target-per-frame guard
    /// (<c>m_bPlayedHitSoundThisFrame</c>) in <see cref="HitSoundSelector.Select"/>.
    /// Self-managed one-shot (auto-release on completion); no-op when audio is not
    /// loaded or the kind has no asset.</summary>
    public void PlayHitSound(HitSoundKind kind, float volume)
    {
        if (!IsLoaded) return;
        SoundEffect? sfx;
        switch (kind)
        {
            case HitSoundKind.MyShield:
            case HitSoundKind.OtherShield:
                if (_shieldHit.Length == 0) return;
                sfx = _shieldHit[_rng.Next(_shieldHit.Length)];
                break;
            case HitSoundKind.MyHull:
                if (_hullHit.Length == 0) return;
                sfx = _hullHit[_rng.Next(_hullHit.Length)];
                break;
            case HitSoundKind.OtherHull:
                sfx = _otherHullHit;
                break;
            case HitSoundKind.Rock:
                // P463c — a chipped asteroid plays rockHitSound (hitrock,
                // sounddef.mdl:266) instead of a hull/shield cue.
                sfx = _rockHit;
                break;
            default:
                return;
        }
        if (sfx is null) return;
        PlayOneShot(sfx, kind is HitSoundKind.MyShield or HitSoundKind.MyHull
            ? SoundCategory.HitMine : SoundCategory.HitOther, volume);
    }

    /// <summary>P463c — play the ship-collision one-shot (<c>collisionSound</c> =
    /// <c>colisionlarge</c>, sounddef.mdl:308) at <paramref name="volume"/>
    /// (distance-scaled by the caller). Self-managed one-shot (auto-release on
    /// completion); no-op when audio is not loaded.</summary>
    public void PlayCollisionSound(float volume)
    {
        if (!IsLoaded || _collision is null) return;
        PlayOneShot(_collision, SoundCategory.HitOther, volume);
    }

    /// <summary>P463c — play the rock-hit one-shot (<c>rockHitSound</c> =
    /// <c>hitrock</c>, sounddef.mdl:266) at <paramref name="volume"/>
    /// (distance-scaled by the caller). Native plays it inside the asteroid's
    /// <c>ReceiveDamage</c> when a round actually chips the rock
    /// (asteroidigc.h:154-155); the caller triggers it from the existing P364
    /// asteroid-hit signal (target id NA). Self-managed one-shot; no-op when
    /// audio is not loaded.</summary>
    public void PlayRockHitSound(float volume)
    {
        if (!IsLoaded || _rockHit is null) return;
        PlayOneShot(_rockHit, SoundCategory.HitOther, volume);
    }

    /// <summary>P260 — play the probe deploy one-shot (the generic expendable
    /// launch asset; native <c>deployProbeSound = dropobject</c>, played for the
    /// local ship's own drop at <c>clintlib.cpp:3256-3257</c>). Non-positional
    /// cockpit-range one-shot (no 3D emitter yet), mirroring
    /// <see cref="PlayMissileLaunch"/>. No-op when audio is not loaded.</summary>
    public void PlayProbeDeploy()
    {
        if (IsLoaded) PlayOneShot(_probeDeploy, SoundCategory.WeaponFire, 1f);
    }

    /// <summary>P717 — play the mine-deploy one-shot (<c>dropmine.ogg</c>;
    /// native <c>deployMineSound</c>, sounddef.mdl:368, played for the local
    /// ship's own drop at <c>clintlib.cpp:3253</c>). Non-positional cockpit-range
    /// one-shot (no 3D emitter yet), mirroring <see cref="PlayProbeDeploy"/>.
    /// No-op when audio is not loaded.</summary>
    public void PlayMineDeploy()
    {
        if (IsLoaded) PlayOneShot(_mineDeploy, SoundCategory.WeaponFire, 1f);
    }

    /// <summary>P755 — play the chaff-deploy one-shot (<c>dropobject.ogg</c>;
    /// native <c>deployChaffSound</c>, sounddef.mdl:367, played for the local
    /// ship's own drop at <c>clintlib.cpp:3249</c>). Non-positional cockpit-range
    /// one-shot, mirroring <see cref="PlayMineDeploy"/>. No-op when audio is not
    /// loaded.</summary>
    public void PlayChaffDeploy()
    {
        if (IsLoaded) PlayOneShot(_chaffDeploy, SoundCategory.WeaponFire, 1f);
    }

    /// <summary>P260 — begin a probe's continuous scanning ambient loop (a looped
    /// instance keyed by the probe's real <c>ProbeId</c>), seeded at the given
    /// volume. Starts it only if one is not already playing for that probe.
    /// No-op when audio is not loaded.</summary>
    public void StartProbeAmbient(short probeId, float volume)
    {
        if (!IsLoaded || _probeAmbients.ContainsKey(probeId)) return;
        var inst = _probeAmbient.CreateInstance();
        inst.IsLooped = true;
        SetLoopVolume(inst, volume);
        Safe(inst.Play);
        _probeAmbients[probeId] = inst;
        Track(inst, SoundCategory.WorldAmbient, isLoop: true, essential: false,
            loudness: volume, audible: volume > 0f, started: true);
    }

    /// <summary>P260 — refresh a live probe's ambient loop volume for the current
    /// listener distance (0..1). No-op if no ambient is tracked for that probe or
    /// audio is not loaded.</summary>
    public void UpdateProbeAmbient(short probeId, float volume)
    {
        if (!IsLoaded) return;
        if (_probeAmbients.TryGetValue(probeId, out var inst))
            SetTrackedLoudness(inst, volume);
    }

    /// <summary>P260 — stop (and release) a probe's scanning ambient loop. Called
    /// when the probe leaves audible range, the player leaves its sector, or the
    /// probe is destroyed/expired; safe to call on a probe with no tracked
    /// ambient. No-op when audio is not loaded.</summary>
    public void StopProbeAmbient(short probeId)
    {
        if (!IsLoaded) return;
        if (_probeAmbients.Remove(probeId, out var inst))
        {
            ForgetVoice(inst);
            if (inst.State == SoundState.Playing) Safe(inst.Stop);
            Safe(inst.Dispose);
        }
    }

    /// <summary>P267/P756 — drive the local ship's maneuvering-thruster
    /// INTERIOR turn loop from its current turn level (0..1, see
    /// <see cref="TurnThrusterSound"/>). Audible only while turning
    /// (<see cref="TurnThrusterSound.IsAudible"/>) and only in the cockpit
    /// camera view, with pitch rising with the level (native
    /// <c>SetPitch(0.50f + m_fTurnSoundLevel/3)</c>, trekigc.cpp:1167-1169).
    /// The bed is resolved from the hull's <c>ManuveringThrusterInteriorSound</c>
    /// SoundID when supplied, falling back to the fighter/capital class defaults.
    /// No-op when audio is not loaded.</summary>
    public void UpdateTurnThruster(float turnLevel, bool capitalHull = false, bool cockpitView = true, short interiorSoundId = 0)
    {
        if (!IsLoaded) return;
        string stem = HullLoopSoundMap.TurnStem(interiorSoundId) ?? ShipLoopBeds.Turn(capitalHull);
        var t = stem == ShipLoopBeds.CapitalTurn ? _turnThrusterCapital : _turnThruster;
        if (t is not { } th) return;
        if (cockpitView && TurnThrusterSound.IsAudible(turnLevel))
        {
            SetLoopVolume(th, 0.8f * _masterVolume);
            Safe(() => th.Pitch = TurnThrusterSound.PitchFor(turnLevel));
            if (th.State != SoundState.Playing) Safe(th.Play);
        }
        else if (th.State == SoundState.Playing)
        {
            Safe(th.Stop);
        }
    }

    /// <summary>P756 — drive the local ship's maneuvering-thruster EXTERIOR
    /// turn loop from its current turn level (0..1). Native plays this on the
    /// hull's world sound source while turning in exterior/chase view
    /// (<c>WinTrek/trekigc.cpp:1341-1343</c>,
    /// <c>GetManuveringThrusterExteriorSound()</c>). The bed is resolved from
    /// the supplied <c>ManuveringThrusterExteriorSound</c> SoundID, falling back
    /// to the fighter/capital class defaults. No-op when audio is not loaded.</summary>
    public void UpdateTurnThrusterExterior(float turnLevel, bool exteriorView, bool capitalHull = false, short exteriorSoundId = 0)
    {
        if (!IsLoaded) return;
        string stem = ShipLoopBeds.TurnExterior(exteriorSoundId, capitalHull);
        var t = stem == ShipLoopBeds.CapitalTurn ? _turnThrusterExteriorCapital : _turnThrusterExterior;
        if (t is not { } th) return;
        if (exteriorView && TurnThrusterSound.IsAudible(turnLevel))
        {
            SetLoopVolume(th, 0.8f * _masterVolume);
            Safe(() => th.Pitch = TurnThrusterSound.PitchFor(turnLevel));
            if (th.State != SoundState.Playing) Safe(th.Play);
        }
        else if (th.State == SoundState.Playing)
        {
            Safe(th.Stop);
        }
    }

    /// <summary>P267 — drive the local ship's hull exterior loop (<c>shipout</c>),
    /// the native <c>ExteriorSound</c> for an in-flight ship (trekigc.cpp:1337,
    /// always-on, muted only when viewing the interior). In this POC it is
    /// audible in the chase/exterior camera view and silenced in the cockpit
    /// (mirroring how the interior hum behaves in the opposite view). No-op when
    /// audio is not loaded.</summary>
    public void UpdateShipExterior(bool exteriorView)
    {
        if (!IsLoaded || _shipExterior is not { } ext) return;
        if (exteriorView)
        {
            SetLoopVolume(ext, 0.35f * _masterVolume);
            if (ext.State != SoundState.Playing) Safe(ext.Play);
        }
        else if (ext.State == SoundState.Playing)
        {
            Safe(ext.Stop);
        }
    }

    /// <summary>P267 — begin a station's exterior ambient loop (a looped
    /// <c>shipout</c> instance keyed by the station's real <c>StationId</c>),
    /// seeded at the given volume. Starts it only if one is not already playing
    /// for that station. No-op when audio is not loaded.</summary>
    public void StartStationAmbient(short stationId, float volume)
    {
        if (!IsLoaded || _stationAmbients.ContainsKey(stationId)) return;
        var inst = _stationAmbient.CreateInstance();
        inst.IsLooped = true;
        SetLoopVolume(inst, volume);
        Safe(inst.Play);
        _stationAmbients[stationId] = inst;
        Track(inst, SoundCategory.WorldAmbient, isLoop: true, essential: false,
            loudness: volume, audible: volume > 0f, started: true);
    }

    /// <summary>P267 — refresh a live station's exterior ambient loop volume for
    /// the current listener distance (0..1). No-op if no ambient is tracked for
    /// that station or audio is not loaded.</summary>
    public void UpdateStationAmbient(short stationId, float volume)
    {
        if (!IsLoaded) return;
        if (_stationAmbients.TryGetValue(stationId, out var inst))
            SetTrackedLoudness(inst, volume);
    }

    /// <summary>P267 — stop (and release) a station's exterior ambient loop.
    /// Called when the station leaves audible range, the listener leaves its
    /// sector, or the station is destroyed; safe to call on a station with no
    /// tracked ambient. No-op when audio is not loaded.</summary>
    public void StopStationAmbient(short stationId)
    {
        if (!IsLoaded) return;
        if (_stationAmbients.Remove(stationId, out var inst))
        {
            ForgetVoice(inst);
            if (inst.State == SoundState.Playing) Safe(inst.Stop);
            Safe(inst.Dispose);
        }
    }

    /// <summary>Play the core-selected station cue, even after the station has
    /// been removed. Native clintlib/appmsg.cpp:548-551,674-677. Uses the same
    /// cached, budgeted warning voice path as other station announcements.</summary>
    public void PlayStationEvent(Allegiance.ClientCore.State.ClientSoundEvent ev,
        IReadOnlyDictionary<short, Allegiance.Core.DataStationTypeIGC>? stationTypes)
    {
        if (StationEventSoundMap.TryResolve(ev, stationTypes, out var stem))
            PlayVoice(stem);
    }

    /// <summary>P267 — play the development/tech-completion chime (one-shot)
    /// <c>vo_sal_researchcomplete</c>. Driven by the real <c>DEV_COMPLETED(8)</c>
    /// wire event (native <c>appmsg.cpp:3326</c>). No-op when audio is not
    /// loaded.</summary>
    public void PlayDevComplete()
    {
        PlayWarnOneShot(_devComplete);
    }

    /// <summary>P414 — play the one-shot "ripcord aborted" VO cue
    /// (<c>vo_sal_ripcordaborted.ogg</c>, native <c>salRipcordAbortedSound</c>,
    /// sounddef.mdl:351). Driven by the real <c>RIPCORD_ABORTED(187)</c> wire
    /// event for the local player's own ship (see ClientWorld's Apply handler).
    /// No-op when audio is not loaded.</summary>
    public void PlayRipcordAborted()
    {
        PlayWarnOneShot(_ripcordAborted);
    }

    /// <summary>P373 — play the one-shot "new contact" (first-sighting) cue
    /// (<c>newtarget.ogg</c> for an enemy, <c>newtargetenemy.ogg</c> for a
    /// friendly/same-side ship) — the two real, distinct native cues confirmed
    /// from sounddef.mdl:247-248 and trekigc.cpp:2087-2094. Driven by the real
    /// first-sighting edge from <c>ClientWorld</c> (drained via
    /// <c>TakeNewContactCues</c>), so it fires exactly once per genuinely new
    /// contact, never on routine pose refreshes. No-op when audio is not loaded.</summary>
    public void PlayNewContact(bool isEnemy)
    {
        if (!IsLoaded) return;
        PlayWarnOneShot(isEnemy ? _newContactEnemy : _newContactFriendly);
    }

    /// <summary>P374 — play the one-shot sector-threat cue (native
    /// <c>ClusterSiteImpl::UpdateClusterWarnings</c>, trekigc.cpp:344-381): the
    /// sector-secured / sector-lost control callout, or the per-warning-level
    /// alert voice (c_vAlertSounds). Precedence matches native's reaction order
    /// (control transitions first, then the level alert); a single cue is
    /// almost always exactly one of these. No-op when audio is not loaded.</summary>
    public void PlaySectorAlert(ClientClusterWarningCue cue)
    {
        if (!IsLoaded) return;
        if (cue.Secured) { PlayWarnOneShot(_sectorSecured); return; }
        if (cue.Lost) { PlayWarnOneShot(_sectorLost); return; }
        if (cue.Warning <= Allegiance.Core.ClusterWarnings.NoThreat) return;
        string stem = Allegiance.Core.ClusterWarnings.AlertSoundStem(cue.Warning);
        if (stem.Length == 0) return;
        if (_clusterAlertSounds.TryGetValue(stem, out var sfx))
            PlayWarnOneShot(sfx);
    }

    /// <summary>WinTrek.cpp:8659-8666 voices on each rising bit;
    /// trekigc.cpp:1285-1286 loops while either warning remains active.</summary>
    public void UpdateShipWarnings(Allegiance.Core.Sim.ShipWarningMask mask, Allegiance.Core.Sim.ShipWarningMask rising)
    {
        if (!IsLoaded || _boundsWarning is not { } loop) return;
        if ((rising & Allegiance.Core.Sim.ShipWarningMask.OutOfBounds) != 0 && _boundsVoice is { } bounds)
            PlayWarnOneShot(bounds);
        if ((rising & Allegiance.Core.Sim.ShipWarningMask.CrowdedSector) != 0 && _crowdingVoice is { } crowd)
            PlayWarnOneShot(crowd);
        if (mask != Allegiance.Core.Sim.ShipWarningMask.None)
        {
            SetLoopVolume(loop, 0.8f * _masterVolume);
            EnsurePlaying(loop);
        }
        else if (loop.State == SoundState.Playing) Safe(loop.Stop);
    }

    /// <summary>
    /// Play a quick-chat voice line (P217) by its real asset stem (e.g.
    /// <c>vo_player_attack</c>) — a one-shot <c>Sounds/{stem}</c> cue, loaded
    /// lazily on first play and cached. A stem that is empty (no resolvable
    /// wave), not in content (the two quick-chat voices whose <c>.ogg</c> does
    /// not exist in the artwork tree), or that fails to load is logged once and
    /// stays silent — the caption text still displays; only the audio is
    /// skipped. No-op when the manager is inert.
    /// </summary>
    public void PlayVoice(string assetStem)
    {
        var cue = NativeVoiceCatalog.Definitions.Values.FirstOrDefault(c => c.Stem == assetStem);
        if (cue is not null)
            PlayVoice(cue);
        else if (IsLoaded && LoadVoice(assetStem) is { } sound)
            PlayWarnOneShot(sound);
    }

    private SoundEffect? LoadVoice(string assetStem)
    {
        if (!IsLoaded || string.IsNullOrEmpty(assetStem))
            return null;
        if (_voiceFailures.Contains(assetStem))
            return null;
        if (_voices.TryGetValue(assetStem, out var sfx))
            return sfx;
        try
        {
            var loaded = LoadSound($"Sounds/{assetStem}");
            _voices[assetStem] = loaded;
            return loaded;
        }
        catch (Exception ex)
        {
            WarnOnce($"quick-chat voice asset unavailable ({assetStem}): {ex.Message}");
            _voiceFailures.Add(assetStem);
            return null;
        }
    }

    /// <summary>
    /// Port of native's <c>IntermittentSoundTemplate::IntermittentInstance::Update</c>
    /// (<c>soundtemplates.cpp:1476-1519</c>) for the single-sound
    /// (<c>bMultipleSounds=false</c>) case: while a previous ambience one-shot is
    /// still playing, do nothing. Otherwise each call has a per-frame chance of
    /// starting a new one, using the same exponential-hazard formula as native
    /// (<c>fProbability = 1 − 2^(−dt/period)</c>, soundtemplates.cpp:1504) so that,
    /// averaged over <see cref="AmbiencePeriodSeconds"/>, a new random variant
    /// starts roughly once per period regardless of frame rate. The variant
    /// itself is picked uniformly at random, excluding the immediately-previous
    /// pick (native's <c>RandomSoundTemplate</c> avoidance, soundtemplates.cpp:1417-1422).
    /// </summary>
    private void UpdateAmbience(float deltaSeconds)
    {
        if (_ambienceVariants.Length == 0) return;
        if (_ambienceSlot.Current is { State: SoundState.Playing }) return;

        double probability = 1.0 - Math.Pow(2.0, -deltaSeconds / AmbiencePeriodSeconds);
        if (_rng.NextDouble() >= probability) return;

        int index = _rng.Next(_ambienceVariants.Length);
        if (_ambienceVariants.Length > 2 && index == _ambienceLastIndex)
            index = (index + 1) % _ambienceVariants.Length;
        _ambienceLastIndex = index;

        var inst = _ambienceVariants[index].CreateInstance();
        inst.Volume = 0.22f;   // low, unobtrusive background bed (native: GainSound(-7, ...))
        Safe(inst.Play);
        // M-3 — slot replacement disposes the previous (now-stopped) instance
        // rather than leaking it.
        _ambienceSlot.Replace(inst);
    }

    private SoundEffectInstance Loop(string asset)
    {
        var sfx = LoadSound(asset);
        var inst = sfx.CreateInstance();
        inst.IsLooped = true;
        return inst;
    }

    private static void SetLoopVolume(SoundEffectInstance inst, float volume) =>
        Safe(() => inst.Volume = Math.Clamp(volume, 0f, 1f));

    private static void EnsurePlaying(SoundEffectInstance inst)
    {
        if (inst.State != SoundState.Playing) Safe(inst.Play);
    }

    /// <summary>Wrap an audio call so a missing/broken device degrades instead
    /// of throwing into the game loop; logs the first failure only.</summary>
    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception)
        {
            // logged once by the load path / first-call path; stay silent here
            // to avoid spam — the manager is already inert at this point.
        }
    }

    private void WarnOnce(string message)
    {
        if (_warned) return;
        _warned = true;
        Console.WriteLine($"[monogame] {message}");
    }

    public void Dispose()
    {
        _serializedVoices.Clear();
        _serializedActive.Clear();
        if (_disposed) return;
        _disposed = true;
        try
        {
            _engine?.Dispose();
            _engineCapital?.Dispose();           // P721
            _cockpitHum?.Dispose();
            _cockpitHumCapital?.Dispose();       // P721
            _ambienceSlot.Clear();
            _afterburner?.Dispose();
            _missileTone?.Dispose();
            _missileWarning?.Dispose();
            _boundsWarning?.Dispose();
            _boundsVoice?.Dispose();
            _crowdingVoice?.Dispose();
            foreach (var sfx in _ambienceVariants) sfx.Dispose();
            _commandMessage?.Dispose();
            _apEngage?.Dispose();
            _apDisengage?.Dispose();
            _countdownStart?.Dispose();
            foreach (var sfx in _countdownTicks.Values) sfx.Dispose();
            foreach (var sfx in _countdownMilestones.Values) sfx.Dispose();   // P711
            _gameOver?.Dispose();
            _criticalMsg?.Dispose();        // P578
            _nonCriticalMsg?.Dispose();     // P578
            _rescuePlayer?.Dispose();       // P578
            _jump?.Dispose();               // P578
            _fireSound?.Dispose();
            foreach (var inst in _weaponBursts.Values) inst.Dispose();   // P465
            foreach (var sfx in _weaponShots.Values) sfx.Dispose();      // P465
            _projectileAmbient?.Dispose();
            foreach (var inst in _projectileAmbients.Values) inst.Dispose();
            _warpAmbient?.Dispose();
            foreach (var inst in _warpAmbients.Values) inst.Dispose();
            _missileLaunch?.Dispose();
            _missileAmbient?.Dispose();
            foreach (var sfx in _explosions.Values) sfx.Dispose();
            foreach (var sfx in _shieldHit) sfx.Dispose();   // P463 (otherHullHit reuses the ef120 above)
            foreach (var sfx in _hullHit) sfx.Dispose();     // P463
            _collision?.Dispose();   // P463c
            _rockHit?.Dispose();     // P463c
            foreach (var inst in _missileAmbients.Values) inst.Dispose();
            _probeDeploy?.Dispose();
            _mineDeploy?.Dispose();  // P717
            _chaffDeploy?.Dispose(); // P755
            _probeAmbient?.Dispose();
            foreach (var inst in _probeAmbients.Values) inst.Dispose();
            _turnThruster?.Dispose();
            _turnThrusterCapital?.Dispose();     // P721
            _turnThrusterExterior?.Dispose();    // P756
            _turnThrusterExteriorCapital?.Dispose(); // P756
            _sideThrustSmall?.Dispose();         // P721
            _sideThrustCapital?.Dispose();       // P721
            _shipExterior?.Dispose();
            _stationAmbient?.Dispose();
            foreach (var inst in _stationAmbients.Values) inst.Dispose();
            _devComplete?.Dispose();
            _ripcordAborted?.Dispose();   // P414
            _hullLow?.Dispose();          // P711
            _ammoLow?.Dispose();          // P711
            _fuelLow?.Dispose();          // P711
            _energyLow?.Dispose();        // P711
            _noAmmo?.Dispose();           // P711
            _noFuel?.Dispose();           // P711
            _noRipcord?.Dispose();        // P711
            _shieldsOffline?.Dispose();   // P717
            _shieldActivate?.Dispose();   // P741
            _shieldDeactivate?.Dispose(); // P741
            _cloakEngage?.Dispose();      // P735
            _ripcordLoop?.Dispose();      // P711
            _miningLoop?.Dispose();       // P711
            _newContactEnemy?.Dispose();       // P373
            _newContactFriendly?.Dispose();    // P373
            _docked?.Dispose();                // P719
            _welcomeHome?.Dispose();           // P719
            _boarded?.Dispose();               // P719
            _groupWeapons?.Dispose();          // P719
            _ungroupWeapons?.Dispose();        // P719
            _mount?.Dispose();                 // P719
            _unmount?.Dispose();               // P719
            _mapClick?.Dispose();              // P755
            _cargoFull?.Dispose();             // P719
            // P736 — presence / side-change salutations.
            _salQuit?.Dispose();
            _salChangeSides?.Dispose();
            _salMia?.Dispose();
            _salEnemyLeaves?.Dispose();
            _salRecruitsArrived?.Dispose();
            _salEnemyJoiners?.Dispose();
            _salReloadingAmmo?.Dispose();        // P737
            _salReloadingFuel?.Dispose();        // P737
            _salReloadingMissiles?.Dispose();    // P737
            _salReloadingChaff?.Dispose();       // P737
            _salReloadingDispenser?.Dispose();   // P737
            _salMissilesDepleted?.Dispose();     // P787a
            _salChaffDepleted?.Dispose();        // P787a
            _salDispenserEmpty?.Dispose();       // P787a
            _drop?.Dispose();                  // P719
            _reloading?.Dispose();             // P719
            foreach (var sfx in _voices.Values) sfx.Dispose();
            // P614 — dispose any still-tracked one-shot instances not owned by
            // a dictionary above (loops are all covered by their dicts).
            foreach (var v in _managedVoices)
            {
                if (!v.IsLoop && v.Instance is { } one) one.Dispose();
            }
        }
        catch { /* disposal must not throw */ }
    }
}
