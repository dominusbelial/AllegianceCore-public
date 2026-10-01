using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;
using System.Linq;
using Allegiance.ClientCore.State;

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
/// (<c>ALLEGIANCE_ARTWORK_PATH</c>, <c>/home/domingo/Development/Artwork</c>,
/// the same tree the server already reads), each resolving the original
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
public sealed class SoundManager : IDisposable
{
    private readonly ContentManager _content;

    private SoundEffectInstance? _engine;     // accelsmall — main-thruster loop
    private SoundEffectInstance? _cockpitHum; // sshipint — cockpit interior hum
    private SoundEffectInstance? _afterburner; // iafter — afterburner loop (interior only; no 3D emitter for other ships yet)
    private SoundEffectInstance? _missileTone; // missilelock — P230 lock-acquired looping beep
    // P264 — the incoming-missile warning loop (missileLockSound =
    // LoopingSound(ImportWave("missilelockonme")), sounddef.mdl:364), played
    // while an enemy missile's target is the local ship, pitched 0.75..1.25 by
    // the best lock (trekigc.cpp:5065).
    private SoundEffectInstance? _missileWarning;
    private SoundEffect _apEngage = null!;    // vo_sal_autopilotengage
    private SoundEffect _apDisengage = null!; // vo_sal_autopilotdisengage
    private SoundEffect _countdownStart = null!;   // countdownstart
    private readonly Dictionary<int, SoundEffect> _countdownTicks = new(); // 10..1
    private SoundEffect _gameOver = null!;    // vo_ma_gameover
    private SoundEffect _fireSound = null!;   // pulselaser — weapon single-shot (P232)
    // P465 — per-weapon shot/burst waves, loaded lazily on first use and cached
    // by wave stem (the same pattern as _voices). _weaponShotFailures remembers
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
    // P260 — probe cues. Real single-default assets: deploy reuses the generic
    // expendable-launch asset (native deployProbeSound = dropobject is reused for
    // chaff/mine/probe alike, clintlib.cpp:3245-3257; the wave is absent here), and
    // the ambient is the scanning-loop default (per-type ambientSound unreachable
    // client-side). See ProbeAmbientSoundCurve remarks.
    private SoundEffect _probeDeploy = null!;     // lrgslideout — generic expendable deploy (deployProbeSound=dropobject stand-in)
    private SoundEffect _probeAmbient = null!;    // erbeam — scanning-loop default
    private readonly Dictionary<short, SoundEffectInstance> _probeAmbients = new(); // keyed by ProbeId
    // P267 — hull maneuvering-thruster loop (adjust), the local ship's turn
    // sound; and the hull exterior loop (shipout), audible in the chase/
    // exterior view (native's always-on exterior muted by camera mode).
    private SoundEffectInstance? _turnThruster;   // adjust — smallShipTurnInteriorSound, sounddef id 300 (:1016)
    private SoundEffectInstance? _shipExterior;   // shipout — smallShipExteriorSound, sounddef id 180 (:1013)
    // P267 — station exterior ambient loop (shipout), one looped instance per
    // station within earshot, keyed by the station's real StationId
    // (ClientWorld.Stations key). Real default = the loop inside
    // ILStationExternalSound (sounddef id 380), see StationAmbientSoundCurve.
    private SoundEffect _stationAmbient = null!;      // shipout
    private readonly Dictionary<short, SoundEffectInstance> _stationAmbients = new();
    // P267 — station event one-shots. Real per-station-class VO defaults from
    // the demo core's garrison family (sounddef ids 860-927):
    // vo_destroy_garrison / vo_capture_garrison / vo_destroy_enemygarrison /
    // vo_capture_enemygarrison. Single-real-defaults (no core client-side).
    private SoundEffect _stationDestroyed = null!;        // vo_destroy_garrison (garrisonDestroyedSound, id 860)
    private SoundEffect _stationEnemyDestroyed = null!;   // vo_destroy_enemygarrison (garrisonEnemyDestroyedSound, id 900)
    private SoundEffect _stationCaptured = null!;         // vo_capture_garrison (garrisonCapturedSound, id 880)
    private SoundEffect _stationEnemyCaptured = null!;    // vo_capture_enemygarrison (garrisonEnemyCapturedSound, id 920)
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
            _engine = Loop("Sounds/accelsmall");
            _cockpitHum = Loop("Sounds/sshipint");
            _afterburner = Loop("Sounds/iafter");
            _missileTone = Loop("Sounds/missilelock");   // P230 — lock-acquired beep (missileToneSound = ImportWave("missilelock"), sounddef.mdl:365; sound id missileTone, sounds.h:135)
            _missileWarning = Loop("Sounds/missilelockonme"); // P264 — incoming-missile warning loop (missileLockSound = LoopingSound(ImportWave("missilelockonme")), sounddef.mdl:364)
            _ambienceVariants = AmbienceStems.Select(s => _content.Load<SoundEffect>($"Sounds/{s}")).ToArray();
            _apEngage = _content.Load<SoundEffect>("Sounds/vo_sal_autopilotengage");
            _apDisengage = _content.Load<SoundEffect>("Sounds/vo_sal_autopilotdisengage");
            _countdownStart = _content.Load<SoundEffect>("Sounds/countdownstart");
            for (int n = 1; n <= CountdownSound.MaxTickSecond; n++)
                _countdownTicks[n] = _content.Load<SoundEffect>($"Sounds/countdown{n}");
            _gameOver = _content.Load<SoundEffect>("Sounds/vo_ma_gameover");
            _fireSound = _content.Load<SoundEffect>("Sounds/pulselaser");   // P232 — weapon single-shot (pulselaserShotSound, sounddef.mdl)
            _projectileAmbient = _content.Load<SoundEffect>("Sounds/erbeam");// P232 — projectile in-flight loop (er_beam, sounddef.mdl)
            _warpAmbient = _content.Load<SoundEffect>("Sounds/alephloop");  // P237 — warp ambient loop (alephSound, sounddef.mdl:385)
            // P236 — missile cues. Real single-default assets (per-type ids are
            // unreachable client-side, see MissileAmbientSoundCurve remarks).
            _missileLaunch = _content.Load<SoundEffect>("Sounds/lrgslideout"); // HornetMissileLaunch, sounddef.mdl:1302
            _missileAmbient = _content.Load<SoundEffect>("Sounds/mmissile");   // HornetMissileFlight, sounddef.mdl:1303
            _explosions[ExplosionSoundTier.Large] = _content.Load<SoundEffect>("Sounds/explosionshockwave"); // largeExplosionSound, sounddef.mdl:268
            _explosions[ExplosionSoundTier.Medium] = _content.Load<SoundEffect>("Sounds/ef24");               // mediumExplosionSound, sounddef.mdl:269
            _explosions[ExplosionSoundTier.Small] = _content.Load<SoundEffect>("Sounds/ef120");               // smallExplosionSound, sounddef.mdl:270
            // P463 — the four hit-sound cues (see the field docs): the two
            // random 1-3 pools (HitMeShield1-3 / HitMeArmor1-3) and the reused
            // ef120 for otherHull (the small explosion already loaded above).
            _shieldHit = new[]
            {
                _content.Load<SoundEffect>("Sounds/hitmeshield1"),
                _content.Load<SoundEffect>("Sounds/hitmeshield2"),
                _content.Load<SoundEffect>("Sounds/hitmeshield3"),
            };
            _hullHit = new[]
            {
                _content.Load<SoundEffect>("Sounds/hitmearmor1"),
                _content.Load<SoundEffect>("Sounds/hitmearmor2"),
                _content.Load<SoundEffect>("Sounds/hitmearmor3"),
            };
            _otherHullHit = _explosions[ExplosionSoundTier.Small];   // reuse — same asset, one Content.Load
            // P260 — probe cues (single-real-default; see ProbeAmbientSoundCurve remarks).
            _probeDeploy = _content.Load<SoundEffect>("Sounds/lrgslideout");  // generic expendable deploy (deployProbeSound=dropobject stand-in)
            _probeAmbient = _content.Load<SoundEffect>("Sounds/erbeam");      // scanning-loop default
            // P267 — hull maneuvering-thruster turn loop (adjust), hull exterior
            // loop (shipout), station exterior ambient loop (shipout), the four
            // station event one-shots (garrison VO defaults), and the tech
            // completion chime (vo_sal_researchcomplete). See the field docs.
            _turnThruster = Loop("Sounds/adjust");
            _shipExterior = Loop("Sounds/shipout");
            _stationAmbient = _content.Load<SoundEffect>("Sounds/shipout");
            _stationDestroyed = _content.Load<SoundEffect>("Sounds/vo_destroy_garrison");
            _stationEnemyDestroyed = _content.Load<SoundEffect>("Sounds/vo_destroy_enemygarrison");
            _stationCaptured = _content.Load<SoundEffect>("Sounds/vo_capture_garrison");
            _stationEnemyCaptured = _content.Load<SoundEffect>("Sounds/vo_capture_enemygarrison");
            _devComplete = _content.Load<SoundEffect>("Sounds/vo_sal_researchcomplete");
            _ripcordAborted = _content.Load<SoundEffect>("Sounds/vo_sal_ripcordaborted"); // P414
            // P373 — first-sighting cues (see field docs for the confirmed
            // native enemy/friendly mapping).
            _newContactEnemy = _content.Load<SoundEffect>("Sounds/newtarget");
            _newContactFriendly = _content.Load<SoundEffect>("Sounds/newtargetenemy");
            // P374 — sector-threat alert cues. The eight detection/threatened
            // VO stems from native's c_vAlertSounds (sounddef.mdl:758-766) plus
            // the sector-secured/lost control cues (768-769).
            foreach (var stem in ClusterAlertStems)
                _clusterAlertSounds[stem] = _content.Load<SoundEffect>($"Sounds/{stem}");
            _sectorSecured = _content.Load<SoundEffect>($"Sounds/{Allegiance.Core.ClusterWarnings.SecuredSoundStem}");
            _sectorLost = _content.Load<SoundEffect>($"Sounds/{Allegiance.Core.ClusterWarnings.LostSoundStem}");
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
    public void Update(float throttle, float speed, bool cockpitMode, bool afterburnerActive, float deltaSeconds, bool missileLocked = false)
    {
        if (!IsLoaded) return;

        UpdateAmbience(deltaSeconds);

        if (_engine is { } engine)
        {
            var (vol, pitch) = EngineSoundCurve.Mix(throttle, speed, EngineSoundCurve.MaxSpeedDefault);
            // Cockpit/chase distinction (optional, cheap): a slightly fuller
            // tone from the cockpit (you hear the hull around you), a touch
            // duller in chase. Not required by scope, but nice.
            float cockPitBoost = cockpitMode ? 0.08f : 0f;
            SetLoopVolume(engine, Math.Clamp(vol + cockPitBoost, 0f, 1f));
            Safe(() => engine.Pitch = pitch);
            EnsurePlaying(engine);
        }

        if (_cockpitHum is { } hum)
        {
            if (cockpitMode) EnsurePlaying(hum);
            else if (hum.State == SoundState.Playing) Safe(hum.Stop);
        }

        if (_afterburner is { } afterburner)
        {
            if (afterburnerActive) EnsurePlaying(afterburner);
            else if (afterburner.State == SoundState.Playing) Safe(afterburner.Stop);
        }

        // P230 — the missile-lock beep: a continuous loop while at FULL lock,
        // stopped otherwise (native's PlaySoundIf gate, trekigc.cpp:1289).
        if (_missileTone is { } tone)
        {
            if (missileLocked)
            {
                SetLoopVolume(tone, MissileToneVolume);
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
    public void PlayAutopilotEngage()
    {
        if (IsLoaded) Safe(() => _apEngage.Play());
    }

    /// <summary>Play the autopilot-disengage voice chime (one-shot). No-op
    /// when audio is not loaded.</summary>
    public void PlayAutopilotDisengage()
    {
        if (IsLoaded) Safe(() => _apDisengage.Play());
    }

    /// <summary>Play the countdown-start callout (one-shot) — P216 voices the
    /// opening of the mission-start briefing rather than leaving the first
    /// seconds silent (see <see cref="CountdownSound"/>). No-op when audio is
    /// not loaded.</summary>
    public void PlayCountdownStart()
    {
        if (IsLoaded) Safe(() => _countdownStart.Play());
    }

    /// <summary>Play the per-second countdown tick for <paramref name="second"/>
    /// seconds remaining (one-shot, 1..<see cref="CountdownSound.MaxTickSecond"/>)
    /// — <c>countdown10</c>…<c>countdown1</c>. Out-of-range seconds are a silent
    /// no-op (the caller decides eligibility via
    /// <see cref="CountdownSound.TickAssetFor"/>); this method only speaks the
    /// matching asset it already holds. No-op when audio is not loaded.</summary>
    public void PlayCountdownSecond(int second)
    {
        if (IsLoaded && _countdownTicks.TryGetValue(second, out var sfx))
            Safe(() => sfx.Play());
    }

    /// <summary>Play the game-over callout (one-shot) — <c>vo_ma_gameover</c>.
    /// No-op when audio is not loaded.</summary>
    public void PlayGameOver()
    {
        if (IsLoaded) Safe(() => _gameOver.Play());
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
        if (sfx is not null) Safe(() => sfx.Play());
    }

    /// <summary>P232 — play the fallback weapon-fire one-shot (<c>pulselaser</c>),
    /// kept for the non-own projectile ambient path (other ships' bolts near the
    /// listener) which carries no weapon id — P465's out-of-scope "other ships"
    /// cue stays on the single default.</summary>
    public void PlayWeaponFire()
    {
        if (IsLoaded) Safe(() => _fireSound.Play());
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
    }

    /// <summary>P465 — stop (and release) a mount's burst loop. Safe to call on a
    /// mount with no tracked loop. No-op when audio is not loaded.</summary>
    public void StopWeaponBurst(int mount)
    {
        if (!IsLoaded) return;
        if (_weaponBursts.Remove(mount, out var inst))
        {
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
    /// asset is attempted once, then stays silent — mirroring <see cref="_voices"/>).</summary>
    private SoundEffect? WeaponWaveByStem(string stem)
    {
        if (stem == WeaponSoundMap.FallbackStem) return _fireSound;
        if (_weaponShotFailures.Contains(stem)) return null;
        if (_weaponShots.TryGetValue(stem, out var sfx)) return sfx;
        try
        {
            var loaded = _content.Load<SoundEffect>($"Sounds/{stem}");
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
    }

    /// <summary>P232 — refresh a live projectile's ambient loop volume for the
    /// current listener distance (0..1). No-op if no ambient is tracked for
    /// that projectile or audio is not loaded.</summary>
    public void UpdateProjectileAmbient(ProjectileEntity key, float volume)
    {
        if (!IsLoaded || key is null) return;
        if (_projectileAmbients.TryGetValue(key, out var inst))
            SetLoopVolume(inst, volume);
    }

    /// <summary>P232 — stop (and release) a projectile's ambient loop. Called
    /// when the projectile expires or leaves audible range; safe to call on a
    /// projectile with no tracked ambient. No-op when audio is not loaded.</summary>
    public void StopProjectileAmbient(ProjectileEntity key)
    {
        if (!IsLoaded || key is null) return;
        if (_projectileAmbients.Remove(key, out var inst))
        {
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
    }

    /// <summary>P237 — refresh a live warp's ambient loop volume for the current
    /// listener distance (0..1) — the "increases as you get close" swell. No-op if
    /// no ambient is tracked for that warp or audio is not loaded.</summary>
    public void UpdateWarpAmbient(short warpId, float volume)
    {
        if (!IsLoaded) return;
        if (_warpAmbients.TryGetValue(warpId, out var inst))
            SetLoopVolume(inst, volume);
    }

    /// <summary>P237 — stop (and release) a warp's ambient loop. Called when the
    /// warp leaves audible range or the listener transits away; safe to call on a
    /// warp with no tracked ambient. No-op when audio is not loaded.</summary>
    public void StopWarpAmbient(short warpId)
    {
        if (!IsLoaded) return;
        if (_warpAmbients.Remove(warpId, out var inst))
        {
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
        if (IsLoaded) Safe(() => _missileLaunch.Play());
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
    }

    /// <summary>P236 — refresh a live missile's ambient loop volume for the current
    /// listener distance (0..1). No-op if no ambient is tracked for that missile or
    /// audio is not loaded.</summary>
    public void UpdateMissileAmbient(short missileId, float volume)
    {
        if (!IsLoaded) return;
        if (_missileAmbients.TryGetValue(missileId, out var inst))
            SetLoopVolume(inst, volume);
    }

    /// <summary>P236 — stop (and release) a missile's ambient loop. Called when the
    /// missile leaves audible range or is destroyed; safe to call on a missile with
    /// no tracked ambient. No-op when audio is not loaded.</summary>
    public void StopMissileAmbient(short missileId)
    {
        if (!IsLoaded) return;
        if (_missileAmbients.Remove(missileId, out var inst))
        {
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
                SetLoopVolume(w, MissileWarningVolume);
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
            Safe(() => sfx.Play(Math.Clamp(volume, 0f, 1f), 0f, 0f));
    }

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
            default:
                return;
        }
        if (sfx is null) return;
        Safe(() => sfx.Play(Math.Clamp(volume, 0f, 1f), 0f, 0f));
    }

    /// <summary>P260 — play the probe deploy one-shot (the generic expendable
    /// launch asset; native <c>deployProbeSound = dropobject</c>, played for the
    /// local ship's own drop at <c>clintlib.cpp:3256-3257</c>). Non-positional
    /// cockpit-range one-shot (no 3D emitter yet), mirroring
    /// <see cref="PlayMissileLaunch"/>. No-op when audio is not loaded.</summary>
    public void PlayProbeDeploy()
    {
        if (IsLoaded) Safe(() => _probeDeploy.Play());
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
    }

    /// <summary>P260 — refresh a live probe's ambient loop volume for the current
    /// listener distance (0..1). No-op if no ambient is tracked for that probe or
    /// audio is not loaded.</summary>
    public void UpdateProbeAmbient(short probeId, float volume)
    {
        if (!IsLoaded) return;
        if (_probeAmbients.TryGetValue(probeId, out var inst))
            SetLoopVolume(inst, volume);
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
            if (inst.State == SoundState.Playing) Safe(inst.Stop);
            Safe(inst.Dispose);
        }
    }

    /// <summary>P267 — drive the local ship's maneuvering-thruster turn loop
    /// (<c>adjust</c>) from its current turn level (0..1, see
    /// <see cref="TurnThrusterSound"/>). Audible only while turning
    /// (<see cref="TurnThrusterSound.IsAudible"/>), with pitch rising with the
    /// level (native <c>SetPitch(0.50f + m_fTurnSoundLevel/3)</c>,
    /// trekigc.cpp:1167-1169). No-op when audio is not loaded.</summary>
    public void UpdateTurnThruster(float turnLevel)
    {
        if (!IsLoaded || _turnThruster is not { } t) return;
        if (TurnThrusterSound.IsAudible(turnLevel))
        {
            SetLoopVolume(t, 0.8f);
            Safe(() => t.Pitch = TurnThrusterSound.PitchFor(turnLevel));
            if (t.State != SoundState.Playing) Safe(t.Play);
        }
        else if (t.State == SoundState.Playing)
        {
            Safe(t.Stop);
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
            SetLoopVolume(ext, 0.35f);
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
    }

    /// <summary>P267 — refresh a live station's exterior ambient loop volume for
    /// the current listener distance (0..1). No-op if no ambient is tracked for
    /// that station or audio is not loaded.</summary>
    public void UpdateStationAmbient(short stationId, float volume)
    {
        if (!IsLoaded) return;
        if (_stationAmbients.TryGetValue(stationId, out var inst))
            SetLoopVolume(inst, volume);
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
            if (inst.State == SoundState.Playing) Safe(inst.Stop);
            Safe(inst.Dispose);
        }
    }

    /// <summary>P267 — play a station event one-shot (destroyed / captured, with
    /// the own vs enemy variant) at non-positional volume. Driven by the real
    /// <c>STATION_DESTROYED</c>/<c>STATION_CAPTURE</c> wire events (see
    /// <see cref="StationEventSoundKind"/>); mirrors native
    /// <c>appmsg.cpp:548-551</c> (destroyed) and <c>:674-677</c> (captured).
    /// No-op when audio is not loaded.</summary>
    public void PlayStationEvent(StationEventSoundKind kind)
    {
        if (!IsLoaded) return;
        var sfx = kind switch
        {
            StationEventSoundKind.Destroyed => _stationDestroyed,
            StationEventSoundKind.EnemyDestroyed => _stationEnemyDestroyed,
            StationEventSoundKind.Captured => _stationCaptured,
            StationEventSoundKind.EnemyCaptured => _stationEnemyCaptured,
            _ => null,
        };
        if (sfx is not null) Safe(() => sfx.Play());
    }

    /// <summary>P267 — play the development/tech-completion chime (one-shot)
    /// <c>vo_sal_researchcomplete</c>. Driven by the real <c>DEV_COMPLETED(8)</c>
    /// wire event (native <c>appmsg.cpp:3326</c>). No-op when audio is not
    /// loaded.</summary>
    public void PlayDevComplete()
    {
        if (IsLoaded) Safe(() => _devComplete.Play());
    }

    /// <summary>P414 — play the one-shot "ripcord aborted" VO cue
    /// (<c>vo_sal_ripcordaborted.ogg</c>, native <c>salRipcordAbortedSound</c>,
    /// sounddef.mdl:351). Driven by the real <c>RIPCORD_ABORTED(187)</c> wire
    /// event for the local player's own ship (see ClientWorld's Apply handler).
    /// No-op when audio is not loaded.</summary>
    public void PlayRipcordAborted()
    {
        if (IsLoaded) Safe(() => _ripcordAborted.Play());
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
        Safe(() => (isEnemy ? _newContactEnemy : _newContactFriendly).Play());
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
        if (cue.Secured) { Safe(() => _sectorSecured.Play()); return; }
        if (cue.Lost) { Safe(() => _sectorLost.Play()); return; }
        if (cue.Warning <= Allegiance.Core.ClusterWarnings.NoThreat) return;
        string stem = Allegiance.Core.ClusterWarnings.AlertSoundStem(cue.Warning);
        if (stem.Length == 0) return;
        if (_clusterAlertSounds.TryGetValue(stem, out var sfx))
            Safe(() => sfx.Play());
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
        if (!IsLoaded || string.IsNullOrEmpty(assetStem))
            return;
        if (_voiceFailures.Contains(assetStem))
            return;
        if (_voices.TryGetValue(assetStem, out var sfx))
        {
            Safe(() => sfx.Play());
            return;
        }
        try
        {
            var loaded = _content.Load<SoundEffect>($"Sounds/{assetStem}");
            _voices[assetStem] = loaded;
            Safe(() => loaded.Play());
        }
        catch (Exception ex)
        {
            WarnOnce($"quick-chat voice asset unavailable ({assetStem}): {ex.Message}");
            _voiceFailures.Add(assetStem);
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
        var sfx = _content.Load<SoundEffect>(asset);
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
        if (_disposed) return;
        _disposed = true;
        try
        {
            _engine?.Dispose();
            _cockpitHum?.Dispose();
            _ambienceSlot.Clear();
            _afterburner?.Dispose();
            _missileTone?.Dispose();
            _missileWarning?.Dispose();
            foreach (var sfx in _ambienceVariants) sfx.Dispose();
            _apEngage?.Dispose();
            _apDisengage?.Dispose();
            _countdownStart?.Dispose();
            foreach (var sfx in _countdownTicks.Values) sfx.Dispose();
            _gameOver?.Dispose();
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
            foreach (var inst in _missileAmbients.Values) inst.Dispose();
            _probeDeploy?.Dispose();
            _probeAmbient?.Dispose();
            foreach (var inst in _probeAmbients.Values) inst.Dispose();
            _turnThruster?.Dispose();
            _shipExterior?.Dispose();
            _stationAmbient?.Dispose();
            foreach (var inst in _stationAmbients.Values) inst.Dispose();
            _stationDestroyed?.Dispose();
            _stationEnemyDestroyed?.Dispose();
            _stationCaptured?.Dispose();
            _stationEnemyCaptured?.Dispose();
            _devComplete?.Dispose();
            _ripcordAborted?.Dispose();   // P414
            _newContactEnemy?.Dispose();       // P373
            _newContactFriendly?.Dispose();    // P373
            foreach (var sfx in _voices.Values) sfx.Dispose();
        }
        catch { /* disposal must not throw */ }
    }
}
