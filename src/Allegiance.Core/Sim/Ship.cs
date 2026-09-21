using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Sim;

/// <summary>
/// A standalone ship — the flight-model slice. Holds the kinematic state the
/// C++ <c>CshipIGC</c> exposes (position, velocity, orientation, controls,
/// turn rates, state bits) and implements <see cref="ExecuteMove"/>, the port
/// of <c>CshipIGC::ExecuteShipMove(Time, Time, Vector*, Orientation*)</c>
/// (<c>src/Igc/shipIGC.cpp:2852-3054</c>). Deliberately <b>not</b> wired into
/// <c>Sector</c>/<c>Mission</c> — integration is a separate slice.
/// </summary>
/// <remarks>
/// What is and is not modelled (faithfulness notes — every deviation is a
/// simplification that this slice deliberately does not attempt, documented
/// here so the port is honest):
/// <list type="bullet">
/// <item><b>No ripcord.</b> The C++ guards both the turn-rate ramp and the
/// engine-thrust block with <c>if (!m_pmodelRipcord)</c> (<c>shipIGC.cpp:2885,
/// 2962</c>); this ship has no ripcord path, so both blocks always run. Drag is
/// still computed (and subtracted) regardless, exactly as the C++ does even
/// when ripcording.</item>
/// <item><b>No afterburner part.</b> The C++ folds a mounted afterburner's
/// thrust into <c>thrustRatio</c> and drag (<c>shipIGC.cpp:2942-2959</c>).
/// This ship mounts nothing, so <c>thrustRatio == 0</c> and no afterburner
/// drag is added. The <c>afterburnerButtonIGC</c> bit is still honoured as a
/// gate (it changes which thrust branch runs even with no AB mounted), so the
/// full decision tree is reproduced.</item>
/// <item><b>No <c>engineVector</c> field.</b> It is computed as a local and
/// immediately folded into velocity, matching the observable behaviour.</item>
/// </list>
/// </remarks>
public sealed class Ship : Model, IShipIgc
{
    private DataHullTypeIGC _hull;
    private readonly float[] _turnRates = new float[3];
    private Orientation _orientation;

    // Fractional hull integrity — the C++ m_fraction (shipIGC.cpp:1247). 1.0
    // = full hull, 0.0 = dead. Damage subtracts leakage/maxHP from it; death
    // occurs when it reaches ≤ 0.
    private float _fraction = 1.0f;

    // Shield integrity fraction — the C++ CshieldIGC::m_fraction
    // (shieldIGC.h:160). 1.0 = fully charged, 0.0 = fully depleted, and ≤ 0.1
    // (the "down" threshold c_fcidDownedShield, missionigc.cpp:2690,2745) means
    // the shield absorbs nothing. Defaults to 0 (a ship with no shield mounted
    // has no shield capacity, matching the honest pre-shield 0% wire value).
    private float _shieldFraction = 0.0f;

    // P278 — Kill Bonus: a live per-ship kill-streak counter that scales this
    // ship's OUTGOING weapon damage AND repair output. This is the port of
    // native CshipIGC::m_experience (shipIGC.h:2610) + GetExperienceMultiplier()
    // (shipIGC.h:1812-1817). It is deliberately a DISTINCT mechanic from
    // PlayerScore.CombatRating (the P215 Elo-style session rating) — this is a
    // combat-damage modifier, that is a persistent-feel stat. Scope is PER-SHIP
    // exactly as native (the field lives on the ship object, not the score).
    //
    // SEED DIVERGENCE (documented): native's ctor calls SetExperience(1.0f)
    // (shipIGC.cpp:52), which would give a brand-new ship an immediate 10%
    // damage tier (multiplier 1.10). This port deliberately seeds the counter
    // at 0 — the kill count — matching the wiki's stated "starting at 10 AFTER
    // your first kill" and the task's "1 kill → 10 ... reset to 0" semantics,
    // and preserving the port's established exact-damage test baseline (a 1.0
    // seed would give every fresh weapon shot 1.1× and break ~24 existing
    // tests). The FORMULA (1 + 0.5·c/(c+4)), the +1-per-kill increment, and the
    // reset-on-real-death are ported faithfully from native.
    private float _experience = 0.0f;

    /// <summary>
    /// P278 — the raw live kill-streak counter (native <c>m_experience</c>,
    /// <c>shipIGC.h:2610</c>). Starts at <c>0</c> for a fresh ship (the wiki's
    /// "first kill → 10"; see the SEED DIVERGENCE note above), +1 per eligible
    /// cross-side non-allied kill (<see cref="AddKillBonus"/>, native
    /// <c>AddExperience</c> <c>shipIGC.h:1829</c>), reset to <c>0</c> when this
    /// ship is genuinely killed (<see cref="ResetKillBonus"/>, native
    /// <c>SetExperience(0.0f)</c> on a real death, <c>FedSrv.CPP:11783-11787</c>).
    /// Distinct from <see cref="PlayerScore"/>.<c>CombatRating</c>.
    /// </summary>
    public float KillBonus => _experience;

    /// <summary>
    /// P278 — the KB percentage for this streak: <c>50·c/(c+4)</c> capped at
    /// 50 (the wiki's "KB = 50 × kills/(kills+4)", derived from native's
    /// <c>GetExperienceMultiplier</c>). Starts at 0 for a fresh ship and
    /// reaches 10 at the first kill (<c>c=1</c>), asymptotically approaching —
    /// never exceeding — 50.
    /// </summary>
    public float KillBonusPercent => Math.Clamp(50.0f * _experience / (_experience + 4.0f), 0f, 50f);

    /// <summary>
    /// P278 — native <c>GetExperienceMultiplier</c> (<c>shipIGC.h:1812-1817</c>):
    /// <c>1 + 0.5·c/(c+4)</c>, asymptotically capping at <c>1.5</c> (= +50%, the
    /// KB cap). Equivalent to <c>1 + KillBonusPercent/100</c>. This is the
    /// factor <see cref="ReceiveDamage"/> applies to the launcher's outgoing
    /// damage/repair amount (native <c>shipIGC.cpp:1238-1242</c>,
    /// <c>stationIGC.cpp:142-145</c>).
    /// </summary>
    public float ExperienceMultiplier => 1.0f + 0.5f * _experience / (_experience + 4.0f);

    /// <summary>P278 — native <c>AddExperience</c> (<c>shipIGC.h:1829</c>):
    /// <c>m_experience += 1.0f</c>. The server calls this on an eligible kill
    /// credit (native <c>FedSrv.CPP:11824</c>).</summary>
    public void AddKillBonus() => _experience += 1.0f;

    /// <summary>P278 — native <c>SetExperience(0.0f)</c> on a real death
    /// (<c>FedSrv.CPP:11783-11787</c>): resets this ship's kill streak to 0
    /// when it is genuinely killed (e.g. the pilot's eject pod is destroyed —
    /// the AFS reset trigger). An ejection does NOT reset it.</summary>
    public void ResetKillBonus() => _experience = 0.0f;

    // P299 — the launcher's nanite-gun repair credit (native CshipIGC::m_repair,
    // shipIGC.h:1882-1884 "Xynth: amount of nanning performed by ship as a
    // fraction of hull repaired"). Units: fraction of a hull repaired. Awarded
    // in ReceiveDamage's amount<0 repair branch (shipIGC.cpp:1264) when a ship
    // repairs a friendly (same-side) hull, and DOUBLED when the target's base
    // hull type carries c_habmThreatToStation ("double points for nanning a
    // bomber/htt", shipIGC.cpp:1262-1263). This is the "repair credit" the P299
    // 2x bonus applies to; it is NOT the hull-restoration amount itself (that
    // is never doubled — native heals a bomber at the same rate it heals a
    // fighter; only the launcher's credited points double).
    private float _repair = 0.0f;

    /// <summary>
    /// P299 — this ship's cumulative nanite <b>repair credit</b>: the port of
    /// native <c>CshipIGC::m_repair</c> (<c>shipIGC.h:1882</c>), the "amount of
    /// nanning performed by this ship as a fraction of hull repaired". Zero for
    /// a ship that has never repaired anyone. Distinct from the hull-restore
    /// amount in <see cref="ReceiveDamage"/>'s repair branch: this is the
    /// launcher-side statistic that <see cref="AddRepair"/> accumulates and that
    /// <see cref="DataHullTypeIGC.HabmThreatToStation"/> doubles.
    /// </summary>
    public float Repair => _repair;

    /// <summary>P299 — native <c>AddRepair(float)</c> (<c>shipIGC.h:1882-1884</c>):
    /// <c>m_repair += repair</c>. Called by <see cref="ReceiveDamage"/>'s
    /// <c>amount &lt; 0</c> branch to credit the launcher for nanite-repairing a
    /// friendly hull (<c>shipIGC.cpp:1264</c>).</summary>
    public void AddRepair(float repair) => _repair += repair;

    /// <summary>Construct with a hull and initial kinematic state. Turn rates start at 0 (like the C++ constructor, <c>shipIGC.cpp:120-121</c>).</summary>
    public Ship(short shipId, DataHullTypeIGC hull, Vector3 position, Vector3 velocity, Orientation orientation)
        : base(shipId, position)
    {
        _hull = hull;
        Velocity = velocity;
        _orientation = orientation ?? throw new ArgumentNullException(nameof(orientation));
        Controls = ControlData.Neutral;
        StateM = 0;
        Energy = hull.MaxEnergy;   // pool starts full (the C++ recharges from this)
        Ammo = hull.MaxAmmo;       // magazine starts full
    }

    /// <summary>The hull's <c>Mass</c> — the "wet" mass the movement math uses.</summary>
    public float Mass => _hull.Mass;

    /// <summary>The hull's <c>DataHullTypeIGC</c> — the raw static-core record this ship is built on. (Named <c>HullType</c>, not <c>Hull</c>, to avoid clashing with <see cref="Model.Hull"/>, the collision hull.)</summary>
    public DataHullTypeIGC HullType => _hull;

    // ── P279 — the drifting Lifepod's oxygen countdown ──────────────────────
    // Native constant ids/values: c_fcidLifepodEndurance = 31 (igc.h:96),
    // c_fLifepodEndurance = 300.0f (missionigc.cpp:2724), baked into
    // m_constants.floatConstants[31] (missionigc.cpp:2775). The port reads the
    // LOADED core's FloatConstants[31] (like P215 reads indices 28/29) rather
    // than hardcoding the wiki number — this is the single authoritative value.
    private const int LifepodEnduranceConstantIndex = 31;
    private const double DefaultLifepodEnduranceSeconds = 300.0;

    /// <summary>P279 — the sim-time deadline at which this ship (if it is a
    /// drifting Lifepod) runs out of oxygen and self-destructs. The direct port
    /// of native <c>CshipIGC::m_timeToDie</c> (<c>shipIGC.h:2607</c>), set on
    /// hull-swap to a lifepod (<c>shipIGC.cpp:1407-1408</c>:
    /// <c>m_timeToDie = GetLastUpdate() + LifepodEndurance</c>) and checked in
    /// <c>Update</c> (<c>shipIGC.cpp:361-363</c>). Null when the ship is not
    /// (currently) a lifepod hull. Consumed in <see cref="OnUpdate"/>; set in
    /// <see cref="ChangeHull"/>.</summary>
    public double? LifepodTimeToDie { get; private set; }

    /// <summary>P279 — test seam: overrides the oxygen-endurance seconds used
    /// when the hull becomes a Lifepod (default: the loaded core's baked
    /// <c>FloatConstants[31]</c> = 300 s for PCore001). Lets a live/unit test
    /// compress the countdown to prove the expiry→destroy→respawn mechanism
    /// without waiting the real five minutes. Mirrors how the port reads other
    /// core constants (indices 28/29, PlayerScore.cs:57/63).</summary>
    public double? LifepodEnduranceOverrideSeconds { get; set; }

    /// <summary>
    /// Replace the ship's base hull — the port of
    /// <c>CshipIGC::SetBaseHullType</c> (<c>shipIGC.cpp:1352</c>) used by the
    /// death-respawn slice (P105) to turn a dead player's ship into their
    /// side's Lifepod hull (<c>FedSrv.CPP:12052</c> <c>pship-&gt;SetBaseHullType(pht)</c>).
    /// Matches the subset of the native function that matters here: it
    /// <b>does not</b> reset the hull fraction (a lifepod is still the dead
    /// ship; native <c>SetBaseHullType</c> never touches <c>m_fraction</c>),
    /// and this port has no damage-track / turreted-child / garaged-parts
    /// machinery to clear. Zeroes <c>StateM</c> like the native
    /// <c>SetStateM(0)</c> (<c>shipIGC.cpp:1356</c>). Writing
    /// <see cref="_hull"/> in place flows <see cref="HullType"/>, <see cref="Mass"/>
    /// and the downstream damage / status readers automatically.
    /// </summary>
    public void ChangeHull(DataHullTypeIGC hull)
    {
        _hull = hull;
        StateM = 0;   // SetStateM(0), shipIGC.cpp:1356

        // P279 — arm/clear the drifting Lifepod's oxygen countdown. Native sets
        // m_timeToDie = GetLastUpdate() + LifepodEndurance on ANY hull-swap to a
        // lifepod (shipIGC.cpp:1407-1408) and clears it on the way off; the port
        // mirrors both. Deadline anchors to the mission's current sim time (==
        // now when this runs on the sim thread from the kill seam). Endurance is
        // the loaded core's FloatConstants[c_fcidLifepodEndurance = 31]
        // (300.0f for PCore001, missionigc.cpp:2724/2775), unless the P279 test
        // seam overrides it.
        if (hull.HasCapability(DataHullTypeIGC.HabmLifepod))
        {
            double endurance = LifepodEnduranceOverrideSeconds
                ?? (Mission?.Core.Constants.FloatConstants is { } fc
                    && fc.Length > LifepodEnduranceConstantIndex
                    ? fc[LifepodEnduranceConstantIndex]
                    : DefaultLifepodEnduranceSeconds);
            LifepodTimeToDie = (Mission?.LastUpdate ?? 0.0) + endurance;
        }
        else
        {
            LifepodTimeToDie = null;
        }

        // P173: raise the host seam so the server can broadcast a real
        // FM_S_LOADOUT_CHANGE(96) — the whole current loadout (native raises
        // c_lcHullChange, shipIGC.cpp:1455; NULL part, matching native).
        Mission?.Site.LoadoutChangeEvent(this, null!, LoadoutChange.HullChange);
    }

    /// <summary>The ship's energy pool. Seeded to <c>Hull.MaxEnergy</c> at construction.</summary>
    public float Energy { get; set; }

    /// <summary>The ship's ammo count (magazine rounds for projectile weapons). Seeded to <c>Hull.MaxAmmo</c> at construction.</summary>
    public short Ammo { get; set; }

    /// <summary>
    /// P269 — the ship's cloaking <b>visible fraction</b>: 1.0 = fully visible,
    /// falling toward <c>1 − MaxCloaking</c> as the mounted cloak engages. The
    /// direct port of the C++ <c>CshipIGC::GetCloaking()</c> value
    /// (<c>shipIGC.h:269</c>), which the cloak writes each tick as
    /// <c>SetCloaking(1 − m_cloaking)</c> (<c>cloakigc.cpp:121</c>) and which
    /// <c>GetSignature()</c> multiplies the base signature by
    /// (<c>shipIGC.h:268-270</c>) — the mechanism by which a cloaked ship shrinks
    /// its detection radius (<c>InScannerRange</c>, <c>shipIGC.h:353</c>). Starts
    /// at 1.0 (visible), matching the C++ constructor <c>m_cloaking(1.0f)</c>
    /// (<c>shipIGC.cpp:36</c>).
    /// </summary>
    public float Cloaking { get; private set; } = 1.0f;

    /// <summary>
    /// P269 — true while the mounted cloak's emitter is engaged (the C++
    /// <c>CcloakIGC::m_fActive</c>, <c>cloakigc.h:161</c>): the cloak has begun
    /// drawing energy and reducing the ship's signature. Distinct from "fully
    /// invisible" — <see cref="Cloaking"/> ramps smoothly toward its floor
    /// (<c>cloakigc.cpp:85-122</c>), so a freshly-engaged cloak is still partly
    /// visible. Read by tests and by <see cref="UpdateCloak"/>; detection uses
    /// <see cref="Cloaking"/> via <see cref="EffectiveSignature"/>.
    /// </summary>
    public bool IsCloaked => _cloakEmitterActive;

    // ── P269 cloak-part runtime (the C++ CcloakIGC member state) ────────────
    // Native keeps this on a per-mounted CcloakIGC object (cloakigc.h:157-161);
    // this port realises at most the singular ET_Cloak@0 slot, so the two
    // CcloakIGC members fold onto the ship, sourced from the mounted part each
    // tick (UpdateCloak). _cloakEmitterActive = CcloakIGC::m_fActive (the
    // emitter is on); _cloakLevel = CcloakIGC::m_cloaking (0 → MaxCloaking), the
    // ramp state the ship's Cloaking = 1 − _cloakLevel is derived from.
    private bool _cloakEmitterActive;
    private float _cloakLevel;

    /// <summary>
    /// P269 — the ship's effective detection signature, the port of
    /// <c>CshipIGC::GetSignature()</c> = <c>baseSignature · GetCloaking()</c>
    /// (<c>shipIGC.h:268-270</c>). The base is the hull signature plus the cloak
    /// part's own signature contribution while its emitter is on (the
    /// <c>ChangeSignature(+typeData-&gt;signature)</c> of
    /// <c>CcloakIGC::Activate</c>, <c>cloakigc.h:76</c> — the same
    /// active-emitter signature every active part adds, cf. shield/afterburner/
    /// weapon), all scaled by <see cref="Cloaking"/>. Fed to
    /// <see cref="Model.Sector.ResolveCrossSideVisibility"/> so the per-object
    /// detection sweep (<c>VisibilityMath.DetectionRadius = scannerRadius +
    /// targetRadius + scannerRange · targetSignature</c>) uses a reduced
    /// signature for a cloaked ship — exactly the native <c>m = scannerRange ·
    /// target.GetSignature()</c> (shipIGC.h:353) that makes a fully-cloaked ship
    /// effectively undetectable within ordinary detection range.
    /// </summary>
    public float EffectiveSignature =>
        (HullType.Signature + (_cloakEmitterActive && _mountedCloak is { } ck ? ck.Type.Signature : 0.0f))
        * Cloaking;

    /// <summary>
    /// Fractional hull integrity (<c>m_fraction</c>): 1.0 = full hull, 0.0 =
    /// dead. Damage reduces it by <c>leakage · dtmArmor / maxHP</c>
    /// (<c>shipIGC.cpp:1298</c>); death is the moment it reaches &lt;= 0. Read
    /// by <see cref="ServerShipUpdate"/> so the wire hull fraction reflects
    /// real damage taken.
    /// </summary>
    public float Fraction => _fraction;

    /// <summary>The mounted shield's charge fraction (0..1); 0 when no shield is
    /// mounted. Port of the C++ <c>CshieldIGC::GetFraction()</c>
    /// (<c>shieldIGC.h:122-125</c>). Written by <see cref="ReceiveDamage"/>
    /// (absorption) and read by <see cref="Wire.ServerShipUpdate.FromModel"/>
    /// so the wire byte 41 reflects real shield state.</summary>
    public float ShieldFraction => _shieldFraction;

    /// <summary>Absolute hull hit points = <c>Fraction · HullType.HitPoints</c>.</summary>
    public float HitPoints => _fraction * _hull.HitPoints;

    /// <summary>True once the hull is dead (<c>m_fraction &lt;= 0</c>).</summary>
    public bool Dead => _fraction <= 0.0f;

    /// <summary>
    /// Set the fractional hull integrity — the port of
    /// <c>CshipIGC::SetFraction(float)</c> (<c>shipIGC.h:311-318</c>): a direct
    /// write of <c>m_fraction</c> (the native also mirrors into
    /// <c>m_fractionLastOrder</c> and calls <c>RemoveDamage</c>, neither of which
    /// this port models — fraction is the sole hull-integrity state here, exactly
    /// how <see cref="RepairAndRefuel"/> and the respawn slice already mutate
    /// <c>_fraction</c>). Used by the eject seam to restore a surviving eject pod
    /// to a non-dead state (see <c>Server.EjectSurvivor</c>); also the natural
    /// unit-test seam for <see cref="Dead"/>.
    /// </summary>
    public void SetFraction(float value) => _fraction = value;

    /// <summary>The sector this ship was placed into, set by <c>Mission.PlaceShip</c> — the analogue of the C++ <c>GetCluster()</c>, which the weapon reads to place a projectile (<c>weaponIGC.cpp:177</c>). <c>null</c> before placement. The warp-transit slice reassigns this (<c>cfsship.cpp::SetCluster</c> route) so a ship that crosses an aleph genuinely changes sector.</summary>
    public Sector? Sector { get; set; }

    /// <summary>
    /// Sim-time of the ship's most recent warp transit, or
    /// <c>NegativeInfinity</c> if it has never crossed. The port of the C++
    /// <c>CFSShip::m_warpState</c> debounce (<c>fsship.cpp:74</c>): native
    /// blocks a second crossing while <c>m_warpState != warpReady</c> and only
    /// re-arms ~2 s after a client update (<c>fsship.cpp:1185-1191</c>). Here a
    /// cooldown window (<see cref="Sector.WarpCooldownSeconds"/>, kept on the
    /// ship so it survives a sector transfer) stands in for the full state
    /// machine — a documented simplification for this server's simplified
    /// scope (single authoritative tick, no client-update re-arm cycle).
    /// Owned and written by the host's warp transit on the sim thread.
    /// </summary>
    public double LastWarpTime { get; set; } = double.NegativeInfinity;

    /// <summary>
    /// Sim-time of the ship's most recent undock, or <c>NegativeInfinity</c>
    /// if it has never launched. Mirrors <see cref="LastWarpTime"/>'s own
    /// debounce shape for the analogous problem on the dock side: without
    /// this, <see cref="Model.Sector.ResolveDockCollisions"/> re-latches a
    /// freshly-undocked ship the moment it drifts back inside
    /// <see cref="Model.Sector.DockRadius"/> — confirmed live (P74/P75):
    /// a ship reorienting toward a distant autopilot target can wander
    /// within that radius for a real, non-trivial stretch of sim-time
    /// before it settles onto a committed heading, regardless of how it was
    /// launched. Checked against
    /// <see cref="Model.Sector.LaunchCooldownSeconds"/>. Owned and written
    /// by the undock handler on the sim thread.
    /// </summary>
    public double LastLaunchTime { get; set; } = double.NegativeInfinity;

    /// <summary>
    /// P117 — the carrier's round-robin launch-slot counter, the port of
    /// <c>CshipIGC::m_nextLaunchSlot</c> (<c>shipIGC.h:2168-2172</c>). Each
    /// <see cref="GetLaunchSlot"/> advances it modulo the carrier's real launch
    /// slot count, so consecutive landings distribute across the bay's
    /// <c>launchN</c> frames instead of always reusing slot 0.
    /// </summary>
    public int NextLaunchSlot { get; set; }

    /// <summary>
    /// P117 — the port of <c>CshipIGC::GetLaunchSlot()</c>
    /// (<c>shipIGC.h:2168-2172</c>): advance the carrier's round-robin counter
    /// mod the bay's launch-slot count and return it. Caller must supply a
    /// positive <paramref name="launchSlotCount"/> (guarded by the two branch
    /// structure of the native relaunch, <c>FedSrv.CPP:12970-13000</c>); a
    /// zero count falls back to <c>0</c> rather than dividing by zero.
    /// </summary>
    public short GetLaunchSlot(int launchSlotCount)
    {
        NextLaunchSlot = launchSlotCount > 0 ? (NextLaunchSlot + 1) % launchSlotCount : 0;
        return (short)NextLaunchSlot;
    }

    /// <summary>The weapons mounted on this ship, in mount order. Driven by
    /// <see cref="OnUpdate"/> so they fire only within the authoritative tick.
    /// Realised from the ship's <see cref="Mission"/> core at mount time. This is
    /// the functional subset of <see cref="Parts"/> (every realized weapon is also
    /// in <see cref="Parts"/>, carrying its <see cref="MountedPart.FunctionalWeapon"/>).</summary>
    public IReadOnlyList<Weapon> Weapons => _weapons;

    private readonly List<Weapon> _weapons = [];

    /// <summary>
    /// Every part mounted on this ship, in mount order — the generalized
    /// loadout (see <see cref="MountPart"/>). Superset of <see cref="Weapons"/>:
    /// <c>Weapon</c> subtypes that realized a projectile appear here (their
    /// <see cref="MountedPart.FunctionalWeapon"/> non-null) <em>and</em> in
    /// <see cref="Weapons"/>; the other subtypes
    /// (shield/cloak/afterburner/pack) appear here only, as inert bookkeeping
    /// with a null <see cref="MountedPart.FunctionalWeapon"/> (representable +
    /// round-trippable, not yet functional — Phase 2). This is what the server
    /// echoes back in a <c>BUY_LOADOUT_ACK</c> tail.</summary>
    public IReadOnlyList<MountedPart> Parts => _parts;

    private readonly List<MountedPart> _parts = [];

    /// <summary>
    /// P192 — the ship's cargo hold: a fixed-capacity array of spare parts,
    /// separate from the equipped-mount list <see cref="_parts"/>. The port of
    /// native <c>IpartIGC* m_mountedCargos[c_maxCargo]</c>
    /// (<c>Igc/shipIGC.h:2567</c>), addressed by <b>negative</b> <c>Mount</c>
    /// values <c>-1..-c_maxCargo</c>. Capacity is <see cref="CargoCapacity"/> =
    /// <c>c_maxCargo</c> = 5 (<c>Igc/igc.h:845</c>; "fuel, ammo, missile,
    /// chaff, dispenser" — one cargo slot per reloadable category,
    /// <c>FedSrv.CPP:6523</c>). A cargo-held part's <see cref="MountedPart.MountId"/>
    /// is a placeholder 0 (the slot is implicit in the array position) — this
    /// port's <c>MountId</c> is unsigned and cannot represent native's negative
    /// cargo mount, so the translation to/from native's negative wire convention
    /// happens only at the <c>SWAP_PART</c>/<c>RELOAD</c> codec boundary
    /// (<see cref="TrySwapPartWithCargo"/> / <see cref="TryApplyReloadEntry"/>).
    /// </summary>
    public const int CargoCapacity = 5;   // c_maxCargo (Igc/igc.h:845)

    private readonly MountedPart?[] _cargo = new MountedPart?[CargoCapacity];

    /// <summary>The cargo hold slots (index 0..<see cref="CargoCapacity"/>-1),
    /// each a spare part or <c>null</c> when empty. Read-only — cargo is
    /// mutated only through <see cref="TrySwapPartWithCargo"/> and the RELOAD
    /// entry application (<see cref="TryApplyReloadEntry"/>), matching the
    /// native surface (no direct cargo writes on the wire).</summary>
    public IReadOnlyList<MountedPart?> CargoHold => _cargo;

    /// <summary>Map a native wire mount (negative in
    /// <c>[-c_maxCargo, -1]</c> = <c>[-5, -1]</c>) to the zero-based cargo slot
    /// index (<c>0..CargoCapacity-1</c>), matching native's
    /// <c>&amp;m_mountedCargos[mountID + c_maxCargo]</c>
    /// (<c>Igc/shipIGC.cpp:1524</c>): <c>-1</c> → 0 … <c>-5</c> → 4.</summary>
    public static int CargoSlotForMount(int mount) => -mount - 1;


    public Vector3 Velocity { get; set; }

    /// <summary>The ship's current attitude. Mutated in place by
    /// <see cref="ExecuteMove"/> / <see cref="ExecuteTurretMove"/> (the C++
    /// <c>Orientation</c> is a class with in-place mutators, so the reference
    /// returned here can be rotated directly); <b>replaced</b> wholesale by
    /// <see cref="SetOrientation"/> (the C++ <c>SetOrientation</c>, which the
    /// turret snapshot catch-up uses to install a freshly-integrated
    /// orientation).</summary>
    public Orientation Orientation => _orientation;

    /// <summary><c>SetOrientation(Orientation)</c> — replace the ship's whole
    /// attitude with a new orientation object. Used by the turret-gunner
    /// snapshot catch-up (<c>shipIGC.cpp:3341,3386</c>
    /// <c>SetOrientation(orientation)</c>) after <see cref="ExecuteTurretMove"/>
    /// integrated a decoded orientation forward to the present. Mirrors the
    /// C++ setter that swaps the orientation rather than mutating in place.</summary>
    public void SetOrientation(Orientation o) => _orientation = o ?? throw new ArgumentNullException(nameof(o));

    public ControlData Controls { get; set; }

    /// <inheritdoc/>
    public override Vector3 CollisionVelocity => Velocity;

    /// <inheritdoc/>
    public override Orientation CollisionOrientation => Orientation;

    /// <summary>State bitmask; the buttons live in <see cref="ShipControlState"/>.</summary>
    public int StateM { get; set; }

    /// <summary>
    /// The mission this ship was placed into, set by
    /// <c>Mission.PlaceShip</c> — the analogue of the C++ <c>GetMyMission()</c>
    /// (<c>shipIGC.cpp</c>). <c>null</c> before placement; the combat slice
    /// (weapons/projectiles/receive-damage) reads it to reach the mission's
    /// static core and host site.
    /// </summary>
    public Mission? Mission { get; set; }

    /// <summary>
    /// The side this ship belongs to, if any — the analogue of the C++
    /// <c>GetSide()</c> affiliation that the friendly-fire gate reads
    /// (<c>shipIGC.cpp:1222-1228</c>). Type matches <see cref="SideSpec.Id"/>.
    /// <c>null</c> before a side is assigned: a ship built outside the live
    /// spawn path (e.g. the flight/bot slices or a unit test) has no side and
    /// therefore fights every other ship. The live server assigns one at spawn
    /// (<see cref="Allegiance.Server.Server.SpawnDefaultShip"/>).
    /// </summary>
    public short? SideId { get; set; }

    /// <summary>
    /// The id of the station this ship is docked at, or <c>null</c> while flying
    /// free — the analogue of the C++ <c>IstationIGC* GetStation()</c> pointer
    /// (<c>shipIGC.h</c>) that <c>FedSrvSiteBase::DockWithStationEvent</c> sets
    /// (<c>FedSrv.CPP:13107-13110</c> → <c>CFSShip::ShipStatusDocked</c>,
    /// <c>fsship.cpp:377</c> sets <c>StationID</c>) and that the undock handler
    /// clears (<c>FedSrv.CPP:7040-7041</c> gates on <c>GetStation() != NULL</c>,
    /// <c>7050-7052</c> <c>SetStation(NULL)</c> for <c>stationID == NA</c>).
    /// <c>null</c> when not docked, i.e. docked ⇔ a value is present.
    /// <list type="bullet">
    /// <item><b>Why a nullable <c>short?</c>, not a <see cref="StateM"/> bit</b>:
    /// <see cref="StateM"/> is the C++ <c>ShipControlStateIGC</c> button bitmask
    /// (<c>igc.h:803-835</c>), sent verbatim as the control word on the wire
    /// (<c>ServerShipUpdate.FromModel</c>, <c>ServerShipUpdate.cs:106</c>) and
    /// consumed by the movement/weapon slices. Its value <c>2</c> already means
    /// <c>SelectedWeaponTwo</c> (<c>ShipControlState.cs:34</c>, inside
    /// <c>SelectedWeaponMask == 3</c>), so folding a "docked" bit into
    /// <c>StateM</c> would both collide with weapon selection and leak a control
    /// flag onto the wire as if it were a button. The wire <em>ShipState</em>
    /// enum that actually carries <c>c_ssDocked=2</c> is a separate 6-bit field
    /// in <c>ShipStatusMessage</c> (<c>ShipStatus.cs:40,62</c>) with no
    /// <c>StateM</c> home — this property is where it is derived from. This
    /// follows <see cref="SideId"/>'s nullable-short convention for "an id that
    /// may or may not be set"; the wire value <c>StateDocked == 2</c> is reused
    /// as the emitted <see cref="ShipStatusMessage.State"/> when docked (task's
    /// "reuse, don't invent" instruction honoured via the derived wire state,
    /// not via a colliding <see cref="StateM"/> bit).</item>
    /// <item><b>Latched until undock:</b> the dock state transition (setting /
    /// clearing this property) is exactly what this task scopes; a docked ship
    /// is not given docking-bay physics (it keeps flying on <see cref="OnUpdate"/>),
    /// but the sector pass skips already-docked ships so the state holds until
    /// an <c>FM_C_DOCKED</c>(48) with <c>stationID == NA</c> clears it.</item>
    /// </list>
    /// Owned and written by the host's dock seam (<see cref="IIgcSite.DockWithStationEvent"/>)
    /// and the server's <c>FM_C_DOCKED</c> undock handler, on the sim thread.
    /// </summary>
    public short? StationId { get; set; }

    /// <summary>
    /// The ship this one is boarded on (riding as a turret gunner or
    /// observer), or <c>null</c> while flying free — the analogue of the C++
    /// <c>m_pshipParent</c> (<c>shipIGC.h:2561</c>) exposed as <c>GetParentShip()</c>
    /// (<c>shipIGC.h:1397-1400</c>). Written by the server's boarding/leaving
    /// handler (P108) together with <see cref="ChildShips"/>/<see cref="TurretId"/>
    /// so the parent↔child invariants hold: a boarded ship is a member of
    /// <c>ParentShip.ChildShips</c>, and vice-versa. This is occupancy state
    /// only — the turret <b>aim/fire</b> binding (the C++ <c>SetTurretID</c> →
    /// <c>pw->SetGunner(this)</c> steps at <c>shipIGC.h:1405-1437</c>) is a
    /// separate, later task (P109) and deliberately does not live here.
    /// </summary>
    public Ship? ParentShip { get; set; }

    /// <summary>
    /// The turret-slot (hardpoint) index this ship occupies on
    /// <see cref="ParentShip"/>, or <c>null</c> (= NA) when aboard as an
    /// <b>observer</b>. Port of the C++ <c>m_turretID</c> (<c>shipIGC.h:2561</c>),
    /// read by <c>GetTurretID()</c> (<c>shipIGC.h:1401-1404</c>) and asserted in
    /// <c>[MaxFixedWeapons, MaxWeapons)</c> by the gunner movement code
    /// (<c>shipIGC.cpp:1653-1654</c>). When non-null this ship is a turret
    /// gunner, not an observer. Set together with <see cref="ParentShip"/> by the
    /// server handler; the search that picks the slot reproduces the C++
    /// turret-slot walk (<c>FedSrv.CPP:2347-2402</c>).
    /// </summary>
    public short? TurretId { get; set; }

    /// <summary>
    /// P146 — the wing-group-membership tag on this ship (<c>SET_WINGID</c>),
    /// port of the C++ <c>m_wingID</c> (<c>shipIGC.h:2637</c>, exposed as
    /// <c>GetWingID()</c>/<c>SetWingID()</c>, <c>shipIGC.h:1517-1522</c>) read
    /// and written by the <c>FM_CS_SET_WINGID</c> handler. <c>WingID</c> is a
    /// <c>short</c> (a <c>typedef ObjectID</c> alias, <c>igc.h:477,504</c>) with
    /// the bound <c>c_widMax = 10</c> (<c>igc.h:505</c>); the UI-facing wing
    /// names are <c>"command".."echo"</c> (<c>common.cpp:23-35</c>). Following
    /// this port's precedent for small per-ship tags hung directly on
    /// <see cref="Ship"/> (alongside <see cref="SideId"/>/<see cref="StationId"/>/
    /// <see cref="TurretId"/>). <b>Default = 0</b>: native constructs the ship
    /// with <c>m_wingID(NA)</c> = −1 (<c>shipIGC.cpp:47</c>) and places a joined
    /// ship at wing 0 <c>"command"</c> (<c>fsmission.cpp:634</c>,
    /// <c>SetWingID(0)</c>); a plain <c>0</c> default is this port's applied
    /// choice (the wire only carries <c>[0, WidMax)</c>), stated explicitly
    /// rather than inferred from the native handler. Only meaningful for
    /// simulation as a per-ship tag — the wire relay is driven entirely by the
    /// server handler.
    /// </summary>
    public short WingId { get; set; } = 0;

    /// <summary>
    /// P148 — whether this ship is an NPC/drone rather than a real connected
    /// player's ship. The port's explicit marker for native's per-ship
    /// <c>pilotType</c> field (<c>GetPilotType()</c>, <c>shipIGC.h</c>) — the
    /// <c>ORDER_CHANGE</c> stay-docked gate is native's
    /// <c>GetPilotType() &lt; c_ptPlayer</c> (<c>FedSrv.CPP:7285</c>), i.e. only
    /// a drone, never a real player's own ship, gets the special docked
    /// handling. Chosen over the structurally-equivalent "has no owning
    /// <c>_peerShips</c> entry" test (reasoning in ORDER_CHANGE_P148.md): an
    /// explicit flag is faithful to native's own <c>pilotType</c> member,
    /// immune to incidental wiring (a peer could theoretically be registered
    /// differently, or an NPC later gain a peer surrogate), and reads
    /// unambiguously at the single call site. <b>Default <c>false</c></b> (a
    /// real player ship). Set <c>true</c> only by
    /// <c>Server.SpawnNpcShip</c> (<c>init</c>-only so it cannot be flipped
    /// after the spawn site).
    /// </summary>
    public bool IsNpc { get; init; } = false;

    /// <summary>
    /// P160 — this port's pilot-type concept, mirroring native's
    /// <c>PilotType</c> enum byte-for-byte (<c>src/Wopr/Wopr/Constants/
    /// PilotType.cs</c>, the pre-existing enum in the native tree's own C#
    /// interop layer): Miner=0, Wingman=2, Layer=5, Builder=6, Carrier=9,
    /// Player=10, CheatPlayer=11. Stored as a raw <c>byte</c> — the same type
    /// as <c>PlayerInfo.PilotType</c> (<c>Wire/PlayerInfo.cs:139</c>, const
    /// <c>PilotTypePlayer=10</c> at <c>:126</c>) and as
    /// <c>DataDroneTypeIGC.PilotType</c> (<c>Data/DataDroneTypeIGC.cs:33</c>,
    /// decoded from the real core file at byte offset 376, <c>:68</c> — the
    /// already-parsed source of truth the NPC spawn site derives this from)
    /// rather than a C# enum, matching this port's existing
    /// primitive-with-named-constants convention (<c>PlayerInfo</c>). Native's
    /// raw enum also carries values outside the seven canonical ones (e.g. 1 =
    /// rescue/recon present in PCore001), so the field legally holds any byte;
    /// only the seven canonical values are named here.
    /// </summary>
    public const byte PilotTypeMiner = 0;
    public const byte PilotTypeWingman = 2;
    public const byte PilotTypeLayer = 5;
    public const byte PilotTypeBuilder = 6;
    public const byte PilotTypeCarrier = 9;
    public const byte PilotTypePlayer = 10;
    public const byte PilotTypeCheatPlayer = 11;

    /// <summary>
    /// P160 — the ship's pilot type. For a real player ship this stays at the
    /// default <see cref="PilotTypePlayer"/>(10), the correct implicit type of
    /// a real pilot — the same value <c>PlayerInfo.PilotType</c> carries
    /// (<c>Wire/PlayerInfo.cs:126,139</c>). Real player ships are never spawned
    /// through <c>Server.SpawnNpcShip</c> (confirmed: <see cref="IsNpc"/> is
    /// always explicitly <c>true</c> there), so no separate sentinel is needed
    /// — Player is the correct default. <c>Server.SpawnNpcShip</c> overwrites
    /// it (via <c>init</c>) with the spawned drone-type's real pilot type.
    /// <b>Default <c>Player</c>(10)</b>; <c>init</c>-only so it cannot be
    /// flipped after the spawn site (same rationale as <see cref="IsNpc"/>).
    /// </summary>
    public byte PilotType { get; init; } = PilotTypePlayer;

    /// <summary>
    /// P148 — the commanded "stay docked" latch. Port of native's
    /// <c>m_stayDocked</c> (<c>shipIGC.h</c>) written by
    /// <c>SetStayDocked(true)</c> when a docked drone is told
    /// <c>c_cidDoNothing</c> (<c>FedSrv.CPP:7292</c>). Only ever set on an
    /// <see cref="IsNpc"/> ship by the <c>ORDER_CHANGE</c> stay-docked handler.
    /// <b>Default <c>false</c></b>; for a real player ship this flag is
    /// meaningless (and never touched — the <see cref="IsNpc"/> gate guarantees
    /// it), so the P148 peer-own-ship test asserts it stays <c>false</c>.
    /// </summary>
    public bool StayDocked { get; set; }

    /// <summary>
    /// P145 — a pending part-treasure offer awaiting the client's
    /// <c>FM_C_TREASURE_ACK</c>(163) decision, or <c>null</c> when none is
    /// buffered. The port of the native <c>CFSPlayer</c> treasure-buffer
    /// (<c>SetTreasureData(oid, amount)</c> / <c>GetTreasureObjectID()</c> /
    /// <c>GetTreasureAmount()</c>, set in <c>HitTreasureEvent</c>
    /// <c>FedSrv.CPP:13608</c>, cleared at the end of the TREASURE_ACK handler
    /// <c>FedSrv.CPP:6148</c>). Following this port's precedent for per-player
    /// transient state hung directly on <see cref="Ship"/> (alongside
    /// <see cref="SideId"/>/<see cref="StationId"/>/<see cref="TurretId"/>/
    /// <see cref="ParentShip"/>): <c>null</c> is the native <c>NA</c> sentinel.
    /// <list type="bullet">
    /// <item><b><c>PartTypeId</c></b> is the offered part <em>type</em> id
    /// (native's <c>oid</c> = the treasure's part-type id, <c>FedSrv.CPP:13608</c>;
    /// for a part treasure that is <c>Treasure.Data.TreasureId</c>).</item>
    /// <item><b><c>Amount</c></b> is the offered quantity (native's buffered
    /// <c>amount</c>, <c>FedSrv.CPP:13608</c>).</item>
    /// <item><b>One offer at a time.</b> <c>HitTreasureEvent</c> only buffers +
    /// offers when the buffer is empty (<c>pfsPlayer-&gt;GetTreasureObjectID() ==
    /// NA</c>, <c>FedSrv.CPP:13604-13607</c>); a second touch while one is pending
    /// is silently dropped (though the touched treasure is still destroyed).
    /// Written on the sim thread by the host's pickup drain and cleared by the
    /// server's TREASURE_ACK handler.</item>
    /// </list>
    /// </summary>
    public (short PartTypeId, short Amount)? PendingTreasureOffer { get; set; }

    /// <summary>
    /// P201 — the side whose flag this ship is carrying, or <c>null</c> while
    /// not carrying one — the port of the native per-ship <c>m_sidFlag</c>
    /// (<c>shipIGC.h</c>, read by <c>GetFlag()</c> and written by the
    /// <c>c_tcFlag</c> case of <c>IshipIGC::HitTreasure</c>,
    /// <c>shipIGC.h:1339-1353</c>: <c>m_sidFlag = objectID</c> where
    /// <c>objectID</c> is the ORIGINAL side whose flag this is). A ship can
    /// carry at most one flag at a time (<c>m_sidFlag</c> starts <c>NA</c>).
    /// <c>null</c> is this port's <c>NA</c> sentinel, matching the
    /// <see cref="SideId"/>/<see cref="StationId"/> nullable-short convention
    /// for \"an id that may or may not be set\". Set on a qualifying flag
    /// treasure pickup (the port's <c>HitTreasureEvent</c> flag leg,
    /// <c>Sector.ResolveTreasurePickups</c> → <c>Server.PerformTreasurePickup</c>)
    /// and cleared when the carrier secures the flag by docking at its own
    /// side's station (the native <c>pship-&gt;SetFlag(NA)</c>,
    /// <c>FedSrv.CPP:13149</c>, in <c>Server.OnShipDocked</c>).</summary>
    public short? Flag { get; set; }

    /// <summary>
    /// P111 — an active ripcord destination: the anchored model object a
    /// request selected, and the sector that object physically occupies. This
    /// is the port's stand-in for the C++ <c>m_pmodelRipcord</c> pointer
    /// (<c>shipIGC.h:2590</c>) — the two attributes the wire/state machine
    /// actually read off it are its object type/id (for the
    /// <c>RIPCORD_ACTIVATE</c> fields, <c>FedSrv.CPP:13362-13363</c>) and its
    /// owning cluster (for the re-target <c>GetCluster()</c> comparison,
    /// <c>FedSrv.CPP:13341-13342</c>), so the pointer is collapsed to those
    /// three scalars. A <c>null</c> <see cref="RipcordModel"/> means no active
    /// ripcord (<c>fRipcordActive() == false</c>, <c>shipIGC.h:1525-1528</c>).
    /// Anchors are a friendly <c>c_sabmRipcord</c> station or a friendly
    /// ripcord-target ship — see <c>RequestRipcord</c>'s
    /// <c>FindRipcordModel</c> in <c>Server.cs</c> for the qualification rules.
    /// </summary>
    public readonly record struct RipcordTarget(short Ot, short Oid, short ClusterId);

    private RipcordTarget? _ripcordModel;
    private float _dtRipcordCountdown;

    /// <summary><c>GetRipcordModel()</c> (<c>shipIGC.h:1832-1835</c>) — the
    /// active ripcord target, or <c>null</c> when idle.</summary>
    public RipcordTarget? RipcordModel => _ripcordModel;

    /// <summary><c>fRipcordActive()</c> (<c>shipIGC.h:1525-1528</c>) — true
    /// while a countdown is (nominally) running.</summary>
    public bool IsRipcordActive => _ripcordModel is not null;

    /// <summary><c>SetRipcordModel()</c> (<c>shipIGC.h:1836-1849</c>). The
    /// native also turns off cloak and adjusts ripcord debt on ship-anchor
    /// swaps; this port has neither cloak nor debt accounting, so the model
    /// pointer assignment alone is the faithful subset.</summary>
    public void SetRipcordModel(RipcordTarget? model) => _ripcordModel = model;

    /// <summary><c>GetRipcordTimeLeft()</c> (<c>shipIGC.h:1530-1535</c>) — the
    /// remaining countdown, seeded by <see cref="ResetRipcordTimeLeft"/>.</summary>
    public float RipcordTimeLeft => _dtRipcordCountdown;

    /// <summary><c>ResetRipcordTimeLeft()</c> (<c>shipIGC.h:1536-1540</c>) —
    /// restarts the countdown at the hull's ripcord speed (the duration of a
    /// full ripcord, <c>constants[RipcordTime]=10</c> s folded into the hull's
    /// <c>RipcordSpeed</c> field). A re-target re-seeds it exactly like this.</summary>
    public void ResetRipcordTimeLeft() => _dtRipcordCountdown = _hull.RipcordSpeed;

    /// <summary>
    /// P112/P113 — advance the ripcord countdown by one sim chunk (<c>dt</c>).
    /// This is the per-tick countdown arm of the native ship update block
    /// (<c>shipIGC.cpp:277-294</c>): while the anchor stays legal the timer
    /// ticks down by the chunk length. Reaching zero is detected by the caller
    /// (<c>Sector.ResolveRipcordCountdowns</c>), which hands the ripper to the
    /// host's <c>UseRipcord</c> (the teleport, P113) instead of leaving it
    /// active; this method only clamps the scalar at zero (the native's "no
    /// teleport landed" no-op shape) for the one tick the hand-off is in
    /// flight.
    /// </summary>
    public void AdvanceRipcordCountdown(float dt)
    {
        _dtRipcordCountdown -= dt;
        if (_dtRipcordCountdown <= 0.0f)
            _dtRipcordCountdown = 0.0f;
    }

    /// <summary><c>GetRipcordSpeed()</c> (<c>shipIGC.h:103</c>) — the hull's
    /// ripcord-speed field, the countdown length reset by
    /// <see cref="ResetRipcordTimeLeft"/>.</summary>
    public float RipcordSpeed => _hull.RipcordSpeed;

    /// <summary>
    /// Port of <c>CshipIGC::InGarage(IshipIGC* pship, float tCollision)</c>
    /// (<c>src/Igc/shipIGC.cpp:4382-4443</c>) — the geometric "is this ship in
    /// my landing bay" test the <c>RELAUNCH_SHIP</c> collision dispatch runs on
    /// a carrier (<c>shipIGC.cpp:1038-1044</c>, gated by the carrier /
    /// land-on-carrier capability bits). This ship is the carrier; <paramref
    /// name="lander"/> is the ship attempting to land.
    /// </summary>
    /// <remarks>
    /// Faithful port of the native control flow and arithmetic: for each
    /// land-slot, for each of its land-planes, the plane's local direction and
    /// position are transformed by the carrier's orientation, then the lander's
    /// extreme point in that direction is tested against the half-space
    /// <c>(pMin - point) · direction &lt; 0</c>. A slot "passes" (returns true)
    /// only if <b>no</b> plane is crossed, i.e. the lander sits inside the
    /// intersection of the bay's bounding half-spaces — the native
    /// <c>bInside</c> falls out only when <c>j == -1</c>.
    /// <para>
    /// The plane geometry comes from the carrier's own collision model: see
    /// <see cref="HullLandingBay.Reconstruct"/>, which pulls the
    /// <c>garageNA</c> frames out of this ship's <see cref="Model.Hull"/>
    /// (<c>.cvh</c>) — the same source native <c>HullTypeIGC::Initialize</c>
    /// uses (<c>hullTypeIGC.cpp:178-212</c>). If the carrier has no collision
    /// hull attached (native <c>HitTest::Load</c> failed), there is no
    /// geometry and the predicate is <c>false</c>.
    /// </para>
    /// <para>
    /// <b>Explicit, sourced simplification:</b> the native test drives the
    /// lander's real oriented hit-test extremes, <c>pht-&gt;GetMinExtreme(...)</c>
    /// (<c>shipIGC.cpp:4421</c>). This port's extreme primitive for a ship is
    /// the <c>0.5 × HullType.Length</c> sphere established by P113's ripcord
    /// teleport margin (and <c>ServerHost</c>'s spawn offsets) — so the lander's
    /// extreme point is taken as its swept sphere centre minus
    /// <c>direction × 0.5·Length</c>, exactly the sphere branch of
    /// <see cref="Collider.GetMinExtreme"/> (<c>Collider.cs:193</c>). The port
    /// <i>does</i> already possess real oriented extremes
    /// (<see cref="Collider.GetMinExtreme(int, Vector3, Orientation, Vector3)"/>),
    /// so upgrading this to full hitbox geometry later is a drop-in; the sphere
    /// keeps the foundation slice testable against bare kinematic ships.
    /// </para>
    /// </remarks>
    public bool InGarage(Ship lander, float tCollision)
    {
        if (Hull is null) return false;                          // no model → no bay (native HitTest::Load failed)
        return InGarage(lander, tCollision, HullLandingBay.Reconstruct(Hull, HullType.Length));
    }

    /// <summary>
    /// P116 — overload for the per-tick carrier-landing pass
    /// (<see cref="Model.Sector.ResolveCarrierLandings"/>): consumes a bay
    /// reconstructed <em>once</em> per carrier hull model-name
    /// (<see cref="Collision.HullLandingBayCache"/>) instead of rebuilding it
    /// from the <c>.cvh</c> on every call. Semantically identical to the primary
    /// overload (both share the same geometric body below); a bay with no land
    /// slots is <c>false</c> — the native <c>landSlots-1 &lt; 0</c> no-bay case.
    /// </summary>
    public bool InGarage(Ship lander, float tCollision, HullLandingBayGeometry bay)
    {
        if (bay.LandSlots.Count == 0) return false;              // native: landSlots-1 < 0 → bInside stays false

        var dp = (lander.Position - Position)
               + (lander.Velocity - Velocity) * tCollision;     // shipIGC.cpp:4407-4408
        var landerRadius = 0.5f * lander.HullType.Length;        // documented sphere simplification
        var orientationStation = Orientation;                    // GetOrientation(), shipIGC.cpp:4404

        for (var i = bay.LandSlots.Count - 1; i >= 0; i--)       // shipIGC.cpp:4386,4410 (slot loop, descending)
        {
            var slot = bay.LandSlots[i];
            var j = slot.Planes.Count - 1;
            do                                                   // shipIGC.cpp:4413 (plane loop, descending)
            {
                var plane = slot.Planes[j];
                var direction = plane.Direction * orientationStation;  // shipIGC.cpp:4415
                var point = plane.Position * orientationStation;       // shipIGC.cpp:4416
                var pMin = dp - direction * landerRadius;              // sphere GetMinExtreme, shipIGC.cpp:4421
                if ((pMin - point) * direction < 0.0f)                 // shipIGC.cpp:4422
                    break;
            } while (j-- > 0);

            if (j == -1)                                     // all planes passed → bInside=true (shipIGC.cpp:4432-4436)
                return true;
        }

        return false;
    }

    /// <summary>
    /// The ship's current command target (<c>m_commandTargets[c_cmdCurrent]</c>,
    /// <c>shipIGC.h:2624</c>) exposed as a nullable model — the subset of the
    /// C++ command-target array this arc needs. The native blast-power weapon
    /// aim block reads the <b>gunner's</b> command target when a gunner is
    /// assigned, else the carrier ship's (<c>weaponIGC.cpp:228-231</c>); that
    /// is exactly what <see cref="Weapon.FireWeapon"/> selects here. Settable
    /// (<c>SetCommandTarget</c>, <c>shipIGC.cpp:2106</c>) so a test or future
    /// control path can give a turret gunner a real target to fire at.
    /// <c>null</c> means "no current target" (native returns NULL for an
    /// unset slot) — the common case, under which the leading path is inert.
    /// </summary>
    public Model? CommandTarget { get; set; }

    /// <summary>
    /// P219 — the per-ship staleness counter that drives the heavy/light
    /// ship-update budget (<c>BroadcastShipsUpdate</c>). This is the port of
    /// native's <c>CFSShip::m_deviation</c> (<c>fsship.h:65-66,231</c>), read
    /// by <c>CmpShipDeviation</c> (<c>FedSrv.CPP:3614-3621</c>) to sort the
    /// ship-update candidates most-stale-first and mutated every
    /// <c>AddUpdate</c> pass (<c>FedSrv.CPP:3937-3950</c>): a ship that is
    /// served a full update this tick has its deviation <b>reset to 0</b>;
    /// a ship that is skipped has it <b>incremented</b> (native
    /// <c>+random(0,0.25)+0.70/+1.00</c>; this port uses a deterministic fixed
    /// <c>+1.00</c> — see the P219 writeup). Despite the header comment's talk
    /// of "actual position vs interpolated position", the real native field is
    /// a pure staleness counter — a fair round-robin queue, not a physics
    /// metric — and is ported as such.
    /// <para><b>Shared, not per-viewer:</b> like native, this lives on the
    /// ship object itself and is mutated by whoever's per-player assembly loop
    /// touches it that tick — peer A serving this ship resets the same value
    /// peer B later reads that same tick. This cross-viewer coupling is a real
    /// simplification native has, and is replicated faithfully.</para>
    /// Starts at <c>0.0f</c> (a fresh ship has never waited).</summary>
    public float Deviation { get; set; }

    /// <summary>
    /// P150 — the <c>c_cmdAccepted</c> command slot's target
    /// (<c>m_commandTargets[c_cmdAccepted]</c>, <c>shipIGC.h:2631</c>), as the
    /// <c>ORDER_CHANGE</c> "return to base" branch stores it
    /// (<c>SetCommand(pfmOC-&gt;command, pmodel, pfmOC-&gt;commandID)</c>,
    /// <c>FedSrv.CPP:7330</c> with the real client sending <c>c_cmdAccepted</c>,
    /// <c>WinTrek.cpp:3778, 8907</c>). Typed <see cref="DataStationIGC"/> (not
    /// <see cref="Model"/> like <see cref="CommandTarget"/>) because in this
    /// port stations are <c>DataStationIGC</c> record structs held in
    /// <c>Sector.Stations</c> — not <see cref="Model"/>s — and the slot's only
    /// producer in this branch is <see cref="FindNearestFriendlyStation"/>.
    /// <c>null</c> = slot unset (native's <c>NULL</c> target).
    /// </summary>
    public DataStationIGC? AcceptedTarget { get; set; }

    /// <summary>The command id paired with <see cref="AcceptedTarget"/>
    /// (<c>m_commandIDs[c_cmdAccepted]</c>). Defaults to <see cref="OrderChange.CidNone"/>
    /// (<c>c_cidNone = -1</c>) — an unset slot carries no command id.</summary>
    public short AcceptedCommandId { get; set; } = OrderChange.CidNone;

    /// <summary>
    /// P154 — the <b>real-player</b> <c>c_cmdAccepted</c> command slot's target
    /// (<c>m_commandTargets[c_cmdAccepted]</c>, <c>shipIGC.h:2631</c>), as the
    /// generic non-NPC fallback stores it (<c>SetCommand(pfmOC-&gt;command,
    /// pmodel, pfmOC-&gt;commandID)</c>, <c>FedSrv.CPP:7335-7339</c>). The real
    /// client's <c>SetAccepted</c> menu-order action always passes the <b>same
    /// model as <see cref="SetTarget"/></b> (<c>WinTrek.cpp:10681-10682</c> —
    /// a <c>CommandTarget</c>-style model, a ship in the common case), so this
    /// slot is typed <see cref="Model"/> (mirroring <see cref="CommandTarget"/>'s
    /// shape), <b>distinct</b> from the NPC-specific
    /// <see cref="AcceptedTarget"/>/<see cref="AcceptedCommandId"/>
    /// (<c>DataStationIGC</c>?-, P150): the two producers of the same native
    /// array index are semantically unrelated (an NPC's return-to-base station
    /// vs a real player's menu-accepted order target), so this port models them
    /// as separate typed properties rather than forcing one awkwardly-typed
    /// shared slot. <c>null</c> = slot unset (native's <c>NULL</c> target).
    /// Autopilot consequences of accepting an order are dead code in native
    /// (WinTrek.cpp:8884-8895) and out of scope — only the state storage is
    /// modeled here.
    /// </summary>
    public Model? AcceptedShipTarget { get; set; }

    /// <summary>The command id paired with <see cref="AcceptedShipTarget"/>
    /// (<c>m_commandIDs[c_cmdAccepted]</c> for the real-player case, written by
    /// the P154 generic fallback from <c>pfmOC-&gt;commandID</c>,
    /// <c>FedSrv.CPP:7338</c>). Defaults to <see cref="OrderChange.CidNone"/>
    /// (<c>c_cidNone = -1</c>) — an unset slot carries no command id.</summary>
    public short AcceptedShipCommandId { get; set; } = OrderChange.CidNone;

    /// <summary>
    /// P150 — the <c>c_cmdQueued</c> command slot's target
    /// (<c>m_commandTargets[c_cmdQueued]</c>, <c>shipIGC.h:2631</c>). The
    /// "return to base" branch unconditionally clears this slot
    /// (<c>SetCommand(c_cmdQueued, NULL, c_cidNone)</c>, <c>FedSrv.CPP:7322</c>)
    /// to forget any previously remembered sector; it is stored faithfully even
    /// though nothing in this task's scope reads it back — a future
    /// <c>PickDefaultOrder</c> task will. Same <see cref="DataStationIGC"/>
    /// typing rationale as <see cref="AcceptedTarget"/>.
    /// </summary>
    public DataStationIGC? QueuedTarget { get; set; }

    /// <summary>The command id paired with <see cref="QueuedTarget"/>
    /// (<c>m_commandIDs[c_cmdQueued]</c>). Defaults to <see cref="OrderChange.CidNone"/>.</summary>
    public short QueuedCommandId { get; set; } = OrderChange.CidNone;

    /// <summary>
    /// P151 — the <c>c_cmdPlan</c> command slot's target
    /// (<c>m_commandTargets[c_cmdPlan]</c>, <c>shipIGC.h:2631</c>), the third
    /// and final slot of the native 4-slot array. This port's server-side
    /// autonomous take-cover branch writes it
    /// (<c>SetCommand(c_cmdPlan, pstationToHide, c_cidGoto)</c>,
    /// <c>shipIGC.h:2240</c>) so a damaged NPC records its flee-to-safety
    /// intent; the simplified legality check that decides whether the ship
    /// "has a valid plan" is <c>PlanTarget is not null</c> (this port's
    /// command vocabulary is only <c>Goto</c>/<c>DoNothing</c>/<c>None</c> —
    /// native's full pilot-type-branching <c>LegalCommand</c> at
    /// <c>shipIGC.h:1559-1597</c> is overkill here; see NPC_TAKE_COVER_P151.md).
    /// Same <see cref="DataStationIGC"/> typing rationale as
    /// <see cref="AcceptedTarget"/>.</summary>
    public DataStationIGC? PlanTarget { get; set; }

    /// <summary>The command id paired with <see cref="PlanTarget"/>
    /// (<c>m_commandIDs[c_cmdPlan]</c>). Defaults to <see cref="OrderChange.CidNone"/>
    /// (<c>c_cidNone = -1</c>) — an unset slot carries no command id.</summary>
    public short PlanCommandId { get; set; } = OrderChange.CidNone;

    /// <summary>
    /// P156 — the asteroid this <see cref="IsNpc"/> ship is currently heading
    /// to mine, the second purpose-specific Plan-slot analog alongside the
    /// station-typed <see cref="PlanTarget"/> (P151). Native expresses "an NPC
    /// is either heading to mine or heading to sell, never both" as the single
    /// <c>c_cmdPlan</c> pointer (one <c>GotoPlan</c> target at a time,
    /// <c>shipIGC.cpp:1955</c> <c>SetAutoPilot()</c> toward the mine target);
    /// this port splits that one semantic slot into two typed fields exactly as
    /// P154 split <see cref="AcceptedShipTarget"/> from the NPC
    /// <see cref="AcceptedTarget"/> — a new purpose-specific slot rather than
    /// retyping an existing one. The two are kept mutually exclusive by the
    /// decision cycle (setting one clears the other, matching native's single
    /// pointer), and <see cref="ApplyNpcAutopilot"/> steers toward whichever is
    /// set (station wins defensively if both ever held). <c>null</c> = no
    /// current mining objective. This field is <b>read-only</b> to the
    /// economics — <see cref="Model.Sector.ResolveMining"/> accumulates
    /// <see cref="Ore"/> purely geometrically when the ship parks beside a
    /// <c>CanMineHe3</c> rock and the sell path on dock converts it; this slot
    /// only records the decision to go there.
    /// </summary>
    public Asteroid? MiningAsteroidTarget { get; set; }

    /// <summary>
    /// P254 — the specific <see cref="Asteroid"/> this ship is <em>currently</em>
    /// working, written each tick by <see cref="Model.Sector.ResolveMining"/> to
    /// the rock it actually accrued from and reset to <c>null</c> on any tick
    /// where no rock qualifies. Deliberately distinct from
    /// <see cref="MiningAsteroidTarget"/> (P156), which is the NPC's <em>plan</em>
    /// to travel to a rock: this field records the rock being drilled <em>right
    /// now</em> (near-zero velocity + in proximity, the exact geometric gate the
    /// mining pass enforces), the state the ship-asteroid collision resolver
    /// (P254) consults so an actively-mining ship is not bounced off the very rock
    /// it is drilling — the port of the native drilling-builder exclusion
    /// (<c>shipIGC.cpp:872-884</c>). A ship colliding with a <em>different</em>
    /// rock than the one it is mining still bounces normally. <c>null</c> = not
    /// currently mining any rock this tick.</summary>
    public Asteroid? MiningAsteroid { get; set; }

    /// <summary>
    /// P161 — the idle <see cref="PilotTypeLayer"/> NPC's chosen low-contention
    /// asteroid, the port of the native <c>PickDefaultOrder</c> <c>c_ptLayer</c>
    /// case's <c>SetCommand(c_cmdAccepted, pmodel, c_cidGoto)</c>
    /// (<c>Igc/shipIGC.h:2430</c>). A dedicated asteroid slot following the
    /// <see cref="MiningAsteroidTarget"/> (P156) precedent — its own purpose-
    /// specific slot, <em>not</em> a reuse of the miner's — so a Layer's
    /// decision cycle and a Miner's never share state: each pilot type keeps
    /// its own target slot and the two cycles never interfere. Populated by
    /// <see cref="DecideLayerDefaultOrder"/> when the ship is idle and undocked;
    /// never cleared — a Layer keeps traveling to (and then parked at) its
    /// chosen rock, matching native's bounded default-order behavior (it does
    /// not deploy anything there; see NPC_LAYER_DEFAULT_ORDER_P161.md).
    /// <see cref="ApplyNpcAutopilot"/> steers toward it exactly like a
    /// <see cref="MiningAsteroidTarget"/>. <c>null</c> = no current layer
    /// objective.</summary>
    public Asteroid? LayerAsteroidTarget { get; set; }

    /// <summary>
    /// P162 — the <see cref="PilotTypeWingman"/> NPC's current enemy to attack,
    /// the port of the native Wingman default order's
    /// <c>SetCommand(c_cmdAccepted, pmodel, c_cidAttack)</c>
    /// (<c>Igc/shipIGC.h:2447</c>, the <c>c_ptWingman</c> case of
    /// <c>PickDefaultOrder</c>). A dedicated <c>Ship?</c> slot following the
    /// per-purpose-slot precedent of <see cref="MiningAsteroidTarget"/>
    /// (P156)/<see cref="LayerAsteroidTarget"/> (P161) — a Wingman's combat
    /// cycle never shares state with the Miner's or Layer's. Populated by
    /// <see cref="DecideWingmanDefaultOrder"/> when the ship is idle and
    /// undocked and an enemy is in its current sector; <see cref="OnUpdate"/>'s
    /// combat leg (<see cref="UpdateWingmanCombat"/>) steers toward the
    /// target's <b>live</b> position each tick and clears it when the target
    /// dies, docks, or leaves the ship's current sector (no cross-sector
    /// pursuit — matching native's same-sector-only <c>FindTarget</c>, which
    /// passes no <c>c_ttAnyCluster</c>, <c>shipIGC.h:2441-2443</c>).
    /// <c>null</c> = no current combat objective.</summary>
    public Ship? AttackTarget { get; set; }

    /// <summary>
    /// P166 — the ally ship (the "charge") this <see cref="PilotTypeWingman"/>
    /// NPC is assigned to protect, the port of the native <c>c_cidDefend</c>
    /// Accepted-slot assignment (<c>c_cmdAccepted == c_cidDefend</c>, target =
    /// the charge; <c>Igc/shipIGC.cpp:2373-2407</c>). A dedicated <c>Ship?</c>
    /// slot distinct from <see cref="AttackTarget"/> (P162): a defending
    /// Wingman still uses <see cref="AttackTarget"/> for whichever enemy it is
    /// currently engaging — <see cref="DefendTarget"/> names the ship it guards,
    /// <em>not</em> the threat. Populated directly (by a test, or a future wire
    /// handler — see NPC_WINGMAN_DEFEND_P166.md); never set by
    /// <see cref="DecideWingmanDefaultOrder"/> (a defending Wingman's target
    /// life is owned by <see cref="UpdateWingmanDefend"/>, which bypasses the
    /// plain idle nearest-enemy default order so it never abandons its post to
    /// chase a distant foe). While set, the per-tick
    /// <see cref="UpdateWingmanDefend"/> decides whether to keep the current
    /// <see cref="AttackTarget"/>, switch to a closer threat near the charge,
    /// or fall back to escorting; when <see cref="AttackTarget"/> is null the
    /// Wingman escorts — <see cref="ApplyNpcAutopilot"/> steers toward this
    /// slot's live position. <c>null</c> = not assigned to protect anyone.</summary>
    public Ship? DefendTarget { get; set; }

    /// <summary>
    /// P165 — the station type this <see cref="PilotTypeBuilder"/> NPC is
    /// configured to construct. This port's honest stand-in for native's
    /// <c>m_pbaseData</c>/<c>SetBaseData</c> (<c>Igc/shipIGC.h:2050-2065</c>) —
    /// the station type a Builder is assigned to build at configuration time.
    /// Native derives it from the drone's build-ability bitmask
    /// (<c>m_abmOrders</c>, <c>((IstationTypeIGC*)pbase)-&gt;GetBuildAABM()</c>,
    /// <c>shipIGC.h:2053-2064</c>); this port has no drone-config table wired
    /// for that derivation, so the type is supplied <b>directly</b> (at spawn,
    /// or by the test), not discovered — the stated applied simplification
    /// (see NPC_BUILDER_DEFAULT_ORDER_P165.md). Read by
    /// <see cref="DecideBuilderDefaultOrder"/> instead of being discovered; a
    /// <c>null</c> value means the Builder has nothing to build and stands by
    /// (the no-target no-op precedent every other decision cycle uses).
    /// </summary>
    public DataStationTypeIGC? BuilderStationType { get; set; }

    /// <summary>
    /// P149 — the narrowed port of the native <c>FindTarget(pship,
    /// c_ttFriendly|c_ttStation|c_ttNearest|c_ttAnyCluster, NULL, NULL, NULL,
    /// NULL, c_sabmRepair)</c> search (<c>src/Igc/common.cpp:470-486</c>),
    /// <b>minus</b> <c>c_ttAnyCluster</c>: the nearest friendly station in this
    /// ship's own sector whose station type carries the given ability bit, or
    /// <c>null</c> when none matches. This is the search primitive the
    /// <c>ORDER_CHANGE</c> "return to base" branch consumes — and, by passing a
    /// different ability bit (e.g. <c>c_sabmCapLand</c> for the native carrier
    /// case), any other station ability, one argument change, not a new
    /// function.
    /// </summary>
    /// <remarks>
    /// <para>Ports exactly the path the one real call site uses
    /// (<c>common.cpp:494-556</c>): iterate the ship's sector's station list,
    /// evaluate <c>FindableModel</c> per candidate, and keep the nearest by
    /// squared Euclidean distance (<c>dp.LengthSquared()</c>,
    /// <c>common.cpp:575-578</c>, strict-&lt; replace at <c>617</c>). The three
    /// filter conditions are the <c>FindableModel</c> steps
    /// (<c>common.cpp:254-353</c>) specialised to the <c>OT_station</c> case:</para>
    /// <list type="number">
    /// <item><b>Same side</b> — <c>station.SideId == SideId</c> is the
    /// <c>sidebits &amp;amp; c_ttFriendly</c> test. No alliance tier: this port
    /// has no alliance system, so <c>pside == pHisSide</c> is the whole story
    /// (<c>common.cpp:269-273</c>). A ship with no <see cref="SideId"/> finds
    /// nothing — native's null-<c>pside</c> path falls to <c>c_ttNeutral</c>,
    /// which never matches <c>c_ttFriendly</c>.</item>
    /// <item><b>Resolvable station type</b> — resolved through
    /// <c>mission.Core.StationTypes</c> exactly like <c>MountCombatWeapon</c>
    /// resolves hull/part types (<c>Server.cs:5244</c>). An unresolvable type
    /// has no station-type record to query for a capability, so it is skipped —
    /// the <c>GetStationType()-&gt;HasCapability</c> call
    /// (<c>common.cpp:321</c>) needs the type object to exist.</item>
    /// <item><b>Ability bit</b> — <see cref="DataStationTypeIGC.HasCapability"/>
    /// (<c>DataStationTypeIGC.cs:100</c>, the
    /// <c>StationTypeIGC::HasCapability</c> bit test,
    /// <c>stationtypeigc.h:168-170</c>).</item>
    /// </list>
    /// <para><b>Accepted, documented gap vs native.</b> The native call passes
    /// <c>c_ttAnyCluster</c>, so a local miss recurses through a warp-graph BFS
    /// into neighbouring sectors (<c>common.cpp:648-741</c>, the recursive
    /// <c>FindTarget</c> at <c>729</c>). That cross- cluster generality is
    /// deliberately out of scope (P149); a drone whose own sector has no
    /// qualifying station finds <c>null</c> (the early return at
    /// <c>common.cpp:648-652</c> when <c>c_ttAnyCluster</c> is unset), stated
    /// plainly here rather than silently narrowed.</para>
    /// </remarks>
    /// <param name="abilityBit">A station-ability bit (a <c>sabmCapabilities</c>
    /// mask value), e.g. <c>0x40</c> = <c>c_sabmRepair</c>
    /// (<c>Wopr/Wopr/Constants/StationAbilityBitMask.cs:16</c>) — the general
    /// form native's own <c>abmAbilities</c> parameter takes.</param>
    /// <returns>The nearest friendly station in this ship's sector carrying the
    /// ability, or <c>null</c> if the ship has no sector/side/mission or no
    /// station qualifies.</returns>
    public DataStationIGC? FindNearestFriendlyStation(short abilityBit)
        => FindNearestFriendlyStationIn(Sector, abilityBit);

    private DataStationIGC? FindNearestFriendlyStationIn(Sector? sector, short abilityBit)
    {
        var core = Mission?.Core;
        if (sector is null || core is null) return null;
        if (SideId is not { } sideId) return null;   // no side → c_ttNeutral, never matches c_ttFriendly

        DataStationIGC? best = null;
        float bestDistanceSquared = float.MaxValue;
        foreach (var station in sector.Stations)
        {
            if (station.SideId != sideId) continue;                                                      // c_ttFriendly (common.cpp:269-273)
            if (!core.StationTypes.TryGetValue(station.StationTypeId, out var type)) continue;           // no type → no capability to test
            if (!type.HasCapability(abilityBit)) continue;                                               // FindableModel OT_station (common.cpp:321)

            float dx = station.Position[0] - Position.X;
            float dy = station.Position[1] - Position.Y;
            float dz = station.Position[2] - Position.Z;
            float d2 = dx * dx + dy * dy + dz * dz;                                                      // dp.LengthSquared() (common.cpp:575-578)
            if (d2 < bestDistanceSquared)
            {
                bestDistanceSquared = d2;
                best = station;
            }
        }
        return best;
    }

    /// <summary>
    /// P158 — the cross-cluster widening of <see cref="FindNearestFriendlyStation"/>:
    /// the nearest friendly <c>abilityBit</c>-capable station reachable from the
    /// ship's own sector via the sector-warp graph, walking outward in
    /// breadth-first order and returning the first qualifying sector's nearest
    /// station. This is the "nearest across sectors" bar the task adopts:
    /// <b>fewest hops, not true geodesic distance</b> — the same simplification
    /// <see cref="Model.Sector.FindPathToward"/> already establishes for
    /// pathfinding (the distances across sector coordinate frames are not
    /// comparable anyway), so BFS visit order is the honest, consistent bar.
    /// It closes the P149 gap the same-sector method states openly
    /// (<c>common.cpp:648-741</c>, the recursive <c>c_ttAnyCluster</c>
    /// <c>FindTarget</c> the native call passes).
    /// </summary>
    /// <remarks>
    /// <para><b>Hop 0 is the ship's own sector.</b> The BFS seeds with
    /// <see cref="Sector"/> and calls the per-sector core
    /// (<see cref="FindNearestFriendlyStationIn"/>) on it first, so a
    /// same-sector qualifying station is always returned before any
    /// cross-sector one — identical to the existing same-sector search for the
    /// common case, and what <see cref="DecideMiningOrSelling"/> relies on to
    /// prefer mining/selling locally when possible.</para>
    /// <para><b>Traverses the same graph as <see cref="Model.Sector.FindPathToward"/>.</b>
    /// Walks <see cref="Model.Sector.Warps"/>/<see cref="ResolvedWarp.Destination"/>
    /// (no second graph mechanism), each sector visited at most once
    /// (<see cref="HashSet{T}"/> by reference identity), so the search is O(E)
    /// on the small per-sector warp lists of the real corpus. Dangling edges
    /// (<c>Destination</c> <c>null</c>) are never traversed — the same rule
    /// <c>FindPathToward</c> applies (<c>Sector.cs:370</c>).</para>
    /// <para><b>No contention awareness</b> — unchanged from P156's accepted gap.</para>
    /// </remarks>
    /// <param name="abilityBit">A station-ability bit, as in
    /// <see cref="FindNearestFriendlyStation"/>.</param>
    /// <returns>The nearest friendly station carrying the ability in the ship's
    /// own sector, else the first one found in a wider sector by hop-count, or
    /// <c>null</c> if the ship has no sector/side/mission or the whole reachable
    /// graph has no qualifying station.</returns>
    public DataStationIGC? FindNearestFriendlyStationAnyCluster(short abilityBit)
    {
        if (Sector is not { } start) return null;
        var visited = new HashSet<Sector> { start };
        var frontier = new Queue<Sector>();
        frontier.Enqueue(start);
        while (frontier.TryDequeue(out var sector))
        {
            if (FindNearestFriendlyStationIn(sector, abilityBit) is { } found) return found;
            foreach (var w in sector.Warps)
                if (w.Destination is { } dest && visited.Add(dest))
                    frontier.Enqueue(dest);
        }
        return null;
    }

    /// <summary>
    /// P156 — the nearest mineable asteroid in this ship's own sector, or
    /// <c>null</c> when none qualifies. This is the asteroid-search mirror of
    /// <see cref="FindNearestFriendlyStation"/> (P149) — same structure (iterate
    /// this sector's candidates, keep the nearest by squared Euclidean distance,
    /// <c>common.cpp:575-578</c> strict-&lt; replace) — but over
    /// <see cref="Model.Sector.Mineable"/> (<c>Sector.cs:61</c>, already public)
    /// instead of <c>Sector.Stations</c>, and <b>without</b> the side/friendly
    /// filter: asteroids are unowned (confirmed by <c>ResolveMining</c>'s own
    /// doc comment, <c>Sector.cs:1999-2000</c>: "Asteroids are unowned (unlike
    /// stations) so there is no friendly-only gate at all"), so the search is
    /// one filter simpler than the station search. Same-cluster only by
    /// construction — it scans the ship's own sector, so any result is
    /// necessarily in the same cluster the ship is in (the same P149 gap:
    /// no cross-cluster <c>c_ttAnyCluster</c> recursion, <c>common.cpp:648-652</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Filters</b> — the two it keeps are exactly the two
    /// <see cref="Model.Sector.ResolveMining"/> itself already gates on,
    /// reused rather than reinvented:</para>
    /// <list type="number">
    /// <item><b>He3 gate</b> — <see cref="Asteroid.CanMineHe3"/>
    /// (<c>Asteroid.cs:93</c>, <c>shipIGC.cpp:1955</c>).</item>
    /// <item><b>Has a position</b> — <see cref="Asteroid.Data"/>'s
    /// <c>Position</c> is a <c>float[3]</c>; an asteroid with no usable position
    /// (not <c>{ Length: 3 }</c>) is skipped, the same guard
    /// <c>ResolveMining</c> uses (<c>Sector.cs:2044</c>).</item>
    /// </list>
    /// <para><b>No contention awareness</b> (accepted gap, per P156 scope):
    /// native's fair-share/least-targeted multi-miner logic
    /// (<c>shipIGC.cpp:2153-2202</c>) is not ported — this search has no idea
    /// which rocks other NPCs are already mining, so multiple miners may pick
    /// the same rock. Stated plainly, not silently hidden.</para>
    /// </remarks>
    /// <returns>The nearest <see cref="Asteroid.CanMineHe3"/> asteroid in this
    /// ship's sector with a usable position, or <c>null</c> if the ship has no
    /// sector or no such asteroid exists.</returns>
    public Asteroid? FindNearestMinableAsteroid()
        => FindNearestMinableAsteroidIn(Sector);

    private Asteroid? FindNearestMinableAsteroidIn(Sector? sector)
    {
        if (sector is null) return null;

        Asteroid? best = null;
        float bestDistanceSquared = float.MaxValue;
        foreach (var ast in sector.Mineable)
        {
            if (!ast.CanMineHe3) continue;                             // He3 gate (shipIGC.cpp:1955)
            if (ast.Data.Position is not { Length: 3 } p) continue;    // no position: can't measure (Sector.cs:2044)
            float dx = p[0] - Position.X;
            float dy = p[1] - Position.Y;
            float dz = p[2] - Position.Z;
            float d2 = dx * dx + dy * dy + dz * dz;                    // dp.LengthSquared() (common.cpp:575-578)
            if (d2 < bestDistanceSquared)
            {
                bestDistanceSquared = d2;
                best = ast;
            }
        }
        return best;
    }

    /// <summary>
    /// P158 — the cross-cluster widening of <see cref="FindNearestMinableAsteroid"/>:
    /// the nearest <see cref="Asteroid.CanMineHe3"/> asteroid reachable from the
    /// ship's own sector via the sector-warp graph, walking outward in
    /// breadth-first order and returning the first qualifying sector's nearest
    /// rock. Mirrors <see cref="FindNearestFriendlyStationAnyCluster"/> exactly
    /// (and shares the same "fewest hops, not geodesic distance" bar, so BFS
    /// visit order is the honest comparator across sector coordinate frames),
    /// minus the side/friendly filter — asteroids are unowned. Closes the P156
    /// gap the same-sector method states openly (no <c>c_ttAnyCluster</c>
    /// recursion, <c>common.cpp:648-652</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Hop 0 is the ship's own sector.</b> The BFS seeds with
    /// <see cref="Sector"/> and calls the per-sector core
    /// (<see cref="FindNearestMinableAsteroidIn"/>) on it first, so a same-sector
    /// mineable rock is always returned before any cross-sector one — identical
    /// to the existing same-sector search for the common case, and what
    /// <see cref="DecideMiningOrSelling"/> relies on to prefer mining locally
    /// when possible.</para>
    /// <para><b>Traverses the same graph as <see cref="Model.Sector.FindPathToward"/>.</b>
    /// Walks <see cref="Model.Sector.Warps"/>/<see cref="ResolvedWarp.Destination"/>,
    /// each sector visited at most once by reference identity (O(E)); dangling
    /// edges (<c>Destination</c> <c>null</c>) are never traversed
    /// (<c>Sector.cs:370</c>).</para>
    /// </remarks>
    /// <returns>The nearest mineable asteroid in the ship's own sector, else the
    /// first one found in a wider sector by hop-count, or <c>null</c> if the ship
    /// has no sector or the whole reachable graph has no mineable rock.</returns>
    public Asteroid? FindNearestMinableAsteroidAnyCluster()
    {
        if (Sector is not { } start) return null;
        var visited = new HashSet<Sector> { start };
        var frontier = new Queue<Sector>();
        frontier.Enqueue(start);
        while (frontier.TryDequeue(out var sector))
        {
            if (FindNearestMinableAsteroidIn(sector) is { } found) return found;
            foreach (var w in sector.Warps)
                if (w.Destination is { } dest && visited.Add(dest))
                    frontier.Enqueue(dest);
        }
        return null;
    }

    /// <summary>
    /// P161 — the "least-targeted, any-cluster" asteroid search for the idle
    /// <see cref="PilotTypeLayer"/> default order: the port of native
    /// <c>FindTarget(this, c_ttNeutral | c_ttAsteroid | c_ttNearest |
    /// c_ttLeastTargeted | c_ttAnyCluster | c_ttCowardly, NULL, pcluster,
    /// &amp;position, NULL, 0)</c> (<c>Igc/shipIGC.h:2422-2426</c>). Among the
    /// reachable neutral asteroids it returns the one with the fewest same-side
    /// NPC contenders already heading to it for the Layer purpose; first-found
    /// wins a tie.
    /// </summary>
    /// <remarks>
    /// <para><b>Any neutral asteroid, not specifically He3.</b> Native's mask is
    /// <c>c_ttNeutral | c_ttAsteroid</c> — any unowned asteroid. This port's
    /// asteroids are all unowned (the established finding P149/P156 document for
    /// <see cref="FindNearestMinableAsteroidIn"/>), so the search enumerates
    /// every runtime asteroid (<see cref="Model.Sector.Mineable"/>) in a
    /// reachable sector, with no <see cref="Asteroid.CanMineHe3"/> filter —
    /// deliberately broader than the miner search.</para>
    /// <para><b>Any-cluster</b> — walks the same sector-warp graph as
    /// <see cref="FindNearestFriendlyStationAnyCluster"/> /
    /// <see cref="FindNearestMinableAsteroidAnyCluster"/>
    /// (<see cref="Model.Sector.Warps"/> / <see cref="ResolvedWarp.Destination"/>,
    /// no second graph walk), hop 0 = own sector searched first. "Fewest
    /// contenders" is the <c>c_ttLeastTargeted</c> objective; the strict-<c>&lt;</c>
    /// replace keeps the <em>first-found</em> asteroid on a tie — the same
    /// first-found precedent P159 uses, and the port's honest stand-in for
    /// native's <c>(n == nTargeting &amp;&amp; d &lt; distance)</c> nearer-on-tie
    /// replace (<c>common.cpp:607</c>): because the own sector is searched before
    /// any other, and asteroids are enumerated in insertion order, first-found
    /// already prefers nearer rocks.</para>
    /// <para><b>Applied simplification vs native's counting rule.</b> Native
    /// counts every same-side/allied NPC whose <em>accepted</em> command target
    /// is this asteroid with a command id <c>&gt;= c_cidMine</c>
    /// (<c>common.cpp:591-605</c>), sweeping mission-wide over
    /// <c>pside-&gt;GetMission()-&gt;GetShips()</c>. This port has no
    /// <c>CidMine</c>/<c>CidBuild</c> command constants and nothing ever sets a
    /// ship's <c>AcceptedTarget</c> to an asteroid with a mine-class command, so
    /// that contention datum has no representation here to count. The closest
    /// faithful analogue, applied here: count the same-side NPCs (any sector)
    /// whose <see cref="LayerAsteroidTarget"/> is the same asteroid reference —
    /// other Layers already committed to the same rock for the same purpose.
    /// Stated plainly in NPC_LAYER_DEFAULT_ORDER_P161.md.</para>
    /// <para><b>Mission-wide enumeration.</b> No single <c>Mission.Ships</c>
    /// accessor exists, so it is built from <c>Mission.Sectors</c> plus each
    /// sector's <see cref="Model.Sector.GetShipUpdateCandidates()"/> — the same
    /// accessor P159 reused for same-sector arbitration, here generalized across
    /// all sectors (a legitimate dual-purpose read; no parallel <c>_models</c>
    /// walk is added). The counting rule only considers undocked NPCs, which is
    /// exactly <c>GetShipUpdateCandidates</c>'s physical-membership selection.
    /// The deciding ship itself is idle here (its <see cref="LayerAsteroidTarget"/>
    /// is null), so it contributes no contender to any count.</para>
    /// </remarks>
    private Asteroid? FindLeastTargetedAsteroidAnyCluster(short? requiredAabm = null)
    {
        if (Sector is not { } start) return null;
        if (SideId is not { } sideId) return null;   // native counts only same-side/allied drones (common.cpp:596)
        if (Mission is not { } mission) return null;

        // Mission-wide same-side NPC enumeration for the contention count, built
        // once: each undocked NPC on this side and the LayerAsteroidTarget (if
        // any) it currently holds. Reuses GetShipUpdateCandidates() per sector.
        var contenders = new List<(Ship Ship, Asteroid? Target)>();
        foreach (var sector in mission.Sectors)
            foreach (var ship in sector.GetShipUpdateCandidates())
                if (ship.IsNpc && ship.SideId == sideId)   // c_ttFriendly analogue: same side only (no alliance system)
                    contenders.Add((ship, ship.LayerAsteroidTarget));

        Asteroid? best = null;
        int bestContention = int.MaxValue;
        var visited = new HashSet<Sector> { start };
        var frontier = new Queue<Sector>();
        frontier.Enqueue(start);
        while (frontier.TryDequeue(out var sector))
        {
            foreach (var ast in sector.Mineable)
            {
                if (ast.Data.Position is not { Length: 3 }) continue;   // no position: can't steer to it (Sector.cs:2044 guard)
                // P280 — optional build-type filter: the Builder passes its
                // assigned station type's AabmBuild so only rocks of the
                // matching type are contenders (native FindTarget(m_abmOrders),
                // shipIGC.h:2393-2397). The Layer passes null and filters
                // nothing (native passes 0 for c_ptLayer, shipIGC.h:2422-2426).
                if (requiredAabm is { } req && !ast.Data.HasCapability(req)) continue;

                int n = 0;
                foreach (var (_, t) in contenders)
                    if (ReferenceEquals(t, ast)) n++;                  // same rock = identical reference (P159 precedent)

                if (n < bestContention) { bestContention = n; best = ast; }   // strict < : first-found wins a tie (common.cpp:607 shape)
            }
            foreach (var w in sector.Warps)
                if (w.Destination is { } dest && visited.Add(dest))
                    frontier.Enqueue(dest);
        }
        return best;
    }

    /// <summary>
    /// P162 — the nearest <b>enemy</b> ship in this ship's own sector, or
    /// <c>null</c> when none qualifies. This is the Wingman default order's
    /// target search, the port of the native Wingman <c>PickDefaultOrder</c>
    /// case's <c>FindTarget(this, c_ttEnemy | c_ttShip | c_ttNearest, NULL,
    /// pcluster, &amp;position, NULL, 0)</c> (<c>Igc/shipIGC.h:2441-2443</c>).
    /// <b>Same-sector only</b> — native passes <b>no</b> <c>c_ttAnyCluster</c>,
    /// so unlike the Miner's/Layer's searches a Wingman never travels to find a
    /// fight; it only reacts to enemies already in its current sector (a
    /// fleeing enemy that warps out is simply lost, target cleared, stand by).
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><b>"Enemy" = a different <see cref="SideId"/>.</b> This port has no
    /// alliance system (the established finding P159 states for its own
    /// same-side filtering, and P149 documents for the friendly-station
    /// search's <c>c_ttFriendly</c> gate), so the whole enemy test is
    /// <c>ship.SideId != this.SideId</c> — the <c>pside != pHisSide</c> /
    /// <c>!AlliedSides</c> story (<c>common.cpp:269-273</c>) with no alliance
    /// tier to add. A ship with no <see cref="SideId"/> is skipped (native's
    /// null-<c>pside</c> path never matches <c>c_ttEnemy</c> against a side
    /// it can't identify).</item>
    /// <item><b>Undocked, alive, not self.</b> Enumerates
    /// <see cref="Model.Sector.GetShipUpdateCandidates()"/> — the port's
    /// established per-sector ship-membership accessor (used by P138/P159/P161),
    /// which already selects undocked, non-boarded, non-dead ships
    /// (<c>Sector.cs:290-302</c>) — then applies the side inequality and a
    /// self-exclusion (a ship is never its own enemy). The "not self" filter
    /// is genuinely needed here (unlike the asteroid searches) because the
    /// enumeration includes the deciding ship itself.</item>
    /// <item><b>Nearest by squared distance.</b> Strict-<c>&lt;</c> replace on
    /// squared Euclidean distance (<c>dp.LengthSquared()</c>, <c>common.cpp:
    /// 575-578</c>, strict-<c>&lt;</c> at <c>617</c>) — the same nearest rule the
    /// station/asteroid searches reuse; first-enumerated wins a distance tie.
    /// No contention/fair-share logic: unlike two miners sharing a rock, two
    /// Wingmen may both pick the same enemy (native has no such arbitration for
    /// <c>c_cidAttack</c> either — not built, stated not assumed).</item>
    /// </list>
    /// </remarks>
    /// <returns>The nearest undocked, alive ship with a different (non-null)
    /// <see cref="SideId"/> in this ship's sector, excluding this ship itself,
    /// or <c>null</c> if the ship has no sector/side or no such ship exists.</returns>
    private Ship? FindNearestEnemyShip()
    {
        if (Sector is not { } sector) return null;
        if (SideId is not { } sideId) return null;   // no side → can't classify anyone as friendly/enemy (common.cpp:269-273)

        Ship? best = null;
        float bestDistanceSquared = float.MaxValue;
        foreach (var ship in sector.GetShipUpdateCandidates())
        {
            if (ReferenceEquals(ship, this)) continue;                 // a ship is never its own enemy
            if (ship.SideId is not { } otherSide) continue;            // no side → c_ttNeutral, never c_ttEnemy
            if (otherSide == sideId) continue;                         // c_ttEnemy: different side (no alliance system)

            float dx = ship.Position.X - Position.X;
            float dy = ship.Position.Y - Position.Y;
            float dz = ship.Position.Z - Position.Z;
            float d2 = dx * dx + dy * dy + dz * dz;                    // dp.LengthSquared() (common.cpp:575-578)
            if (d2 < bestDistanceSquared)
            {
                bestDistanceSquared = d2;
                best = ship;
            }
        }
        return best;
    }

    /// <summary>
    /// P166 — the charge-centered enemy search, the port of native
    /// <c>FindTargetShip(this, &amp;chargePos, dist, c_ttEnemy | c_ttNearest, -2)</c>
    /// (<c>Igc/shipIGC.cpp:2384,2690</c>): the nearest enemy ship within
    /// <paramref name="radius"/> world units of an <b>arbitrary</b> center
    /// position <paramref name="center"/> (the charge's <b>live</b> position in
    /// the defend logic, not the Wingman's own), excluding this ship. "Enemy" is
    /// the identical different-<see cref="SideId"/> test
    /// <see cref="FindNearestEnemyShip"/> uses — the two share the same
    /// classification (<c>c_ttEnemy</c>, no alliance system), same-sector only
    /// (both walk <see cref="Model.Sector.GetShipUpdateCandidates"/>, so they
    /// can only see ships of <paramref name="center"/>'s sector; in the defend
    /// logic the charge is guaranteed same-sector — see
    /// <see cref="UpdateWingmanDefend"/>).
    /// <para><b>Radius-in / distance-out</b>, mirroring native's pass-by-value
    /// mutable <c>dist</c>: <paramref name="radius"/> is both the max search
    /// radius on entry and the distance to the returned target on success (used
    /// by the caller's <c>dist &lt; (distToProtectee - 1000)</c> comparison). On
    /// failure (no enemy within the bound) <paramref name="radius"/> is left
    /// unchanged and the method returns <c>null</c>.</para>
    /// <para><b>Why a sibling method, not an optional param on
    /// <see cref="FindNearestEnemyShip"/>.</b> The defend search needs <em>two</em>
    /// behaviors P162's method lacks — a radius bound and the found-distance
    /// output — and its signature must stay byte-for-byte unchanged for the P162
    /// call (<c>FindNearestEnemyShip()</c> is a hot-path, behavior-sensitive
    /// call). Folding center + radius + distance-out into optional params would
    /// overload one method with three orthogonal concerns and risk the existing
    /// call; a focused sibling that centers on an arbitrary position (radius in,
    /// distance out) matches native's <c>FindTargetShip</c> signature directly
    /// and leaves P162 untouched.</para>
    /// </summary>
    private Ship? FindNearestEnemyNear(Vector3 center, ref float radius)
    {
        if (SideId is not { } sideId) return null;   // no side → can't classify anyone as friendly/enemy (common.cpp:269-273)
        if (Sector is not { } sector) return null;   // no sector → nothing to search (defend gate guarantees charge is same-sector)

        Ship? best = null;
        float bestDistanceSquared = float.MaxValue;
        float radiusSquared = radius * radius;
        foreach (var ship in sector.GetShipUpdateCandidates())
        {
            if (ReferenceEquals(ship, this)) continue;                 // a ship is never its own enemy
            if (ship.SideId is not { } otherSide) continue;            // no side → c_ttNeutral, never c_ttEnemy
            if (otherSide == sideId) continue;                         // c_ttEnemy: different side (no alliance system)

            float dx = ship.Position.X - center.X;
            float dy = ship.Position.Y - center.Y;
            float dz = ship.Position.Z - center.Z;
            float d2 = dx * dx + dy * dy + dz * dz;                    // dp.LengthSquared() (common.cpp:575-578)
            if (d2 <= radiusSquared && d2 < bestDistanceSquared)
            {
                bestDistanceSquared = d2;
                best = ship;
            }
        }
        if (best is not null)
            radius = MathF.Sqrt(bestDistanceSquared);                  // dist-out: actual distance to the nearest enemy within the bound
        return best;
    }

    /// <summary>
    /// P151 — the port of the native <c>GetLocalStationToHide</c>
    /// (<c>src/Igc/shipIGC.h:2181-2212</c>): the station a damaged ship should
    /// flee to. Tries the nearest friendly <c>c_sabmLand</c> station in this
    /// ship's own sector; only if none exists <b>and</b> the shield is badly
    /// depleted (<c>&lt; 0.8</c>) does it fall back to the nearest friendly
    /// <c>c_sabmRipcord</c> station (<c>shipIGC.h:2185-2191</c>). Both searches
    /// are the P149 primitive, same-cluster only (<c>c_ttFriendly |
    /// c_ttStation | c_ttNearest</c>, no <c>c_ttAnyCluster</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Accepted simplification — no secret-location buoy
    /// substitution.</b> Native, on finding a ripcord-capable station,
    /// substitutes a fake waypoint buoy if any side doesn't know the teleport's
    /// real location (<c>shipIGC.h:2192-2210</c>). This port has no
    /// fog-of-war/<c>SeenBySide</c> model that substitution depends on, so the
    /// real ripcord station is returned directly — stated plainly, not silently
    /// narrowed (see NPC_TAKE_COVER_P151.md).</para>
    /// </remarks>
    /// <returns>The hiding station, or <c>null</c> when nothing qualifies
    /// (a friendly land station, or a friendly ripcord station while the
    /// shield is below 0.8).</returns>
    public DataStationIGC? TryFindHidingStation()
    {
        var land = FindNearestFriendlyStation(DataStationTypeIGC.SabmLand);   // shipIGC.h:2182-2184
        if (land is not null) return land;

        if (ShieldFraction < 0.8f)                                              // shipIGC.h:2188
            return FindNearestFriendlyStation(DataStationTypeIGC.SabmRipcord); // shipIGC.h:2189-2191

        return null;
    }

    /// <summary>
    /// P151 — the "take cover at a local station if too damaged" decision, the
    /// port of the self-contained sub-block at the head of native
    /// <c>PickDefaultOrder</c>'s <c>c_ptMiner</c> case
    /// (<c>src/Igc/shipIGC.h:2230-2247</c>). Evaluated by
    /// <see cref="OnUpdate"/> for <see cref="IsNpc"/> ships (real player ships
    /// never evaluate this), when no valid plan is pending. On firing it sets
    /// <see cref="PlanTarget"/>/<see cref="PlanCommandId"/> to the hiding
    /// station and returns <c>true</c>.
    /// </summary>
    /// <remarks>
    /// <para>The trigger (native <c>shipIGC.cpp:1934-1945</c>) first promotes a
    /// legal <c>Accepted</c> command into <c>Plan</c> before falling back to
    /// <c>PickDefaultOrder</c>; both legality checks here are the simplified
    /// <c>Target is not null</c> (see <see cref="PlanTarget"/>). This method is
    /// the <c>PickDefaultOrder</c> fallback half only — the <c>Accepted</c>
    /// promotion lives in <see cref="OnUpdate"/>.</para>
    /// <para>Ports the three conditions exactly: only when the hull is damaged
    /// (<c>GetFraction() &lt; 1.0f</c>) and not docked (<c>!bDocked</c>, this
    /// port's docked check is <see cref="StationId"/> not null), and one of
    /// shield-depleted (<c>== 0</c>), moderately-damaged-both
    /// (<c>shield &lt; 0.5 &amp;&amp; hull &lt; 0.7</c>), or hull-critical
    /// (<c>&lt; 0.5</c>) (<c>shipIGC.h:2230-2236</c>). The native's
    /// <c>ExportFractions</c>/<c>CompactShipFractions</c> roundtrip is this
    /// port's own already-decoded <see cref="Fraction"/>/<see cref="ShieldFraction"/>
    /// float fields — reused directly.</para>
    /// </remarks>
    /// <returns><c>true</c> if a hiding station was found and the Plan slot was
    /// set (the native <c>fGaveOrder = true</c> effect).</returns>
    public bool TryTakeCover()
    {
        if (PlanTarget is not null) return false;      // simplified LegalCommand(Plan): a valid plan suppresses re-deciding
        if (Fraction >= 1.0f) return false;            // GetFraction() < 1.0f (shipIGC.h:2230)
        if (StationId is not null) return false;       // !bDocked (shipIGC.h:2230)

        bool hide = ShieldFraction == 0.0f
                 || (ShieldFraction < 0.5f && Fraction < 0.7f)   // shipIGC.h:2233-2234
                 || Fraction < 0.5f;                              // shipIGC.h:2235
        if (!hide) return false;

        if (TryFindHidingStation() is { } station)
        {
            PlanTarget = station;
            PlanCommandId = OrderChange.CidGoto;       // SetCommand(c_cmdPlan, ..., c_cidGoto), shipIGC.h:2240
            return true;
        }
        return false;
    }

    /// <summary>
    /// The ships currently boarded on this one (its turret gunners and
    /// observers), read-only view of the native <c>m_shipsChildren</c>
    /// (<c>GetChildShips</c>, <c>shipIGC.h:1465-1468</c>). Maintained by
    /// <see cref="AddChildShip"/>/<see cref="RemoveChildShip"/>; the server's
    /// boarding handler calls them so the C++ invariant holds — every ship in
    /// this list has <c>this == child.ParentShip</c>. A ship boarded on another
    /// therefore appears in exactly one parent's child list. In the native this
    /// list also backs the turret-occupancy search
    /// (<c>FedSrv.CPP:2370-2377</c> walks it for occupied <c>TurretID</c>s) and
    /// the defensive detach-on-board loop (<c>FedSrv.CPP:2415-2420</c>).
    /// </summary>
    public IReadOnlyList<Ship> ChildShips => _childShips;

    private readonly List<Ship> _childShips = [];

    /// <summary>Add a child to <see cref="ChildShips"/> — the port of the C++
    /// <c>AddChildShip</c> (<c>shipIGC.h:1470-1474</c>). Idempotent (never
    /// duplicates an already-present ship) so a re-board cannot corrupt the
    /// list.</summary>
    public void AddChildShip(Ship child)
    {
        if (!_childShips.Contains(child))
        {
            _childShips.Add(child);
            // P173: raise on the PARENT ship (this) whose passenger list grew,
            // matching native's LoadoutChangeEvent(pshipParent, NULL,
            // c_lcAddPassenger) at shipIGC.cpp:3668.
            Mission?.Site.LoadoutChangeEvent(this, null!, LoadoutChange.AddPassenger);
        }
    }

    /// <summary>Remove a child from <see cref="ChildShips"/> — the port of the
    /// C++ <c>DeleteChildShip</c> (<c>shipIGC.h:1475-1479</c>). No-op if the
    /// ship is not in the list, so a double-leave cannot throw.</summary>
    public void RemoveChildShip(Ship child)
    {
        if (_childShips.Remove(child))
            // P173: raise on the parent (this) whose passenger list shrank
            // (native's LoadoutChangeEvent(pshipParent, NULL,
            // c_lcRemovePassenger), shipIGC.cpp:3670).
            Mission?.Site.LoadoutChangeEvent(this, null!, LoadoutChange.RemovePassenger);
    }

    /// <summary>
    /// P140 — the real role-swap port of <c>CshipIGC::Promote</c>
    /// (<c>src/Igc/shipIGC.cpp:3509-3591</c>): the boarded gunner (<c>this</c>)
    /// takes over its carrier's command seat in place, <b>not</b> by
    /// destruction — the carrier is demoted into the exact turret mount the
    /// gunner just vacated. Invoked by the server's <c>FM_C_PROMOTE(190)</c>
    /// handler once the requesting player's ship has been validated to own a
    /// boarded child at the requested turret mount.
    /// <para>
    /// Sequence (each step cites the native line):
    /// <list type="bullet">
    /// <item><b>Gunner leaves the parent</b> — <c>SetParentShip(NULL)</c>
    /// (<c>shipIGC.cpp:3513</c>): remove from the parent's child list, clear the
    /// back-pointers, and release the parent's turret weapon-gunner binding at
    /// the vacated slot (the native <c>SetParentShip(NULL)</c> → <c>SetGunner(NULL)</c>,
    /// <c>shipIGC.cpp:3652</c>, reusing the P109 binding kept by
    /// <see cref="Weapon.SetGunner"/>).</item>
    /// <item><b>Take the parent's hull</b> — <c>SetBaseHullType(pshipParent-&gt;GetBaseHullType())</c>
    /// (<c>shipIGC.cpp:3515</c>) via <see cref="ChangeHull"/>.</item>
    /// <item><b>Copy the parent's inventory</b> — <c>CreateAndAddPart</c> per
    /// part preserving <c>GetMountID</c>/<c>GetAmount</c> and
    /// <c>SetMountedFraction</c> (<c>shipIGC.cpp:3519-3527</c>), then
    /// <c>SetAmmo</c>/<c>SetFuel</c>/<c>SetEnergy</c>
    /// (<c>shipIGC.cpp:3530-3532</c>). Reuses this port's
    /// <see cref="MountPart"/> (which preserves mount/amount/fraction and
    /// realizes weapon subtypes). The gunner's own loadout is empty by
    /// construction (boarding <c>ClearLoadout</c>-ed it, so no prior parts are
    /// displaced). <b>Fuel is a documented no-op:</b> this port has no fuel
    /// pool on <see cref="Ship"/> (only <see cref="Ammo"/>/<see cref="Energy"/>;
    /// <c>DataHullTypeIGC.MaxFuel</c> is static-core data, never tracked at
    /// runtime) — native <c>SetFuel</c> has no counterpart here, matching the
    /// P61-era decision to seed only ammo+energy in the constructor.</item>
    /// <item><b>Transfer every sibling gunner</b> — <c>while (psl =
    /// pships-&gt;first()) { ... pship-&gt;SetParentShip(this);
    /// pship-&gt;SetTurretID(turretID); }</c>
    /// (<c>shipIGC.cpp:3534-3546</c>). Each surviving child keeps its own turret
    /// mount id; its weapon-gunner binding is re-established against this
    /// ship's freshly-mounted weapon at that mount (the native
    /// <c>SetTurretID</c> → <c>SetGunner</c> effect, <c>shipIGC.h:1405-1437</c>).
    /// Observers (<c>TurretId</c> null) transfer with no binding.</item>
    /// <item><b>Adopt the parent's kinematics + state</b> — position, velocity,
    /// orientation (<c>shipIGC.cpp:3550-3552</c>), hull fraction
    /// <c>SetFraction</c> (<c>shipIGC.cpp:3554</c>), and the mounted shield's
    /// fraction (<c>shipIGC.cpp:3555-3563</c>). No fuel transfer here (as
    /// above); no damage-track bookkeeping (this port has none).</item>
    /// <item><b>Demote the old parent</b> — <c>pshipParent-&gt;SetParentShip(this);
    /// pshipParent-&gt;SetTurretID(turretid)</c>
    /// (<c>shipIGC.cpp:3567-3568</c>): the former commander rides the vacated
    /// mount. Its loadout is cleared per this port's "no parts allowed on a
    /// turret" convention (native terminates them in <c>SetParentShip</c> with a
    /// NULL old parent, <c>shipIGC.cpp:3619-3624</c>; the port's
    /// <see cref="ClearLoadout"/> — the same mechanism the P108
    /// <c>PerformBoard</c> uses). Its hull type is left in place, matching the
    /// port's boarding convention that gunners retain their hull type (P108
    /// <c>PerformBoard</c> never nulls the boarder's hull).</item>
    /// </list>
    /// The native's final model-list link-data swap (<c>shipIGC.cpp:3570-3580</c>)
    /// is <b>not needed here</b>: native keeps its sector models in a
    /// doubly-linked list where swapping the two ships' link payloads preserves
    /// identity-by-enumeration-slot, whereas this port's sector collection is a
    /// <see cref="Model.Sector"/> <c>List&lt;Model&gt;</c> keyed by object
    /// <see cref="Model.Id"/> (insertion order, no slot-identity coupling) — so
    /// updating each ship's own <c>ParentShip</c>/<c>TurretId</c>/hull fields
    /// suffices; both ships remain in the sector and enumerate identically
    /// either way. See <c>PROMOTE_P140.md</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">This ship is not boarded
    /// (<see cref="ParentShip"/> null) or has no turret mount
    /// (<see cref="TurretId"/> null) — a promote is only valid for a genuine
    /// boarded turret gunner.</exception>
    public void Promote()
    {
        var parent = ParentShip ?? throw new InvalidOperationException("cannot promote a ship with no parent");
        var turretid = TurretId ?? throw new InvalidOperationException("cannot promote an observer (no turret mount)");

        // 1. Gunner leaves the parent (SetParentShip(NULL), shipIGC.cpp:3513).
        parent.RemoveChildShip(this);
        ParentShip = null;
        TurretId = null;
        var oldParentWeapon = parent.Weapons.FirstOrDefault(w => w.MountId == turretid);
        oldParentWeapon?.SetGunner(null);   // SetParentShip(NULL)→SetGunner(NULL), shipIGC.cpp:3652

        // 2. Take the parent's hull (SetBaseHullType, shipIGC.cpp:3515).
        ChangeHull(parent.HullType);

        // 3. Copy the parent's inventory (shipIGC.cpp:3519-3532); fuel is N/A (see doc).
        foreach (var p in parent.Parts.ToArray())
            MountPart(p.Type, p.Amount, p.MountId, p.FractionMounted);
        Ammo = parent.Ammo;
        Energy = parent.Energy;

        // 4. Transfer every remaining gunner/observer (shipIGC.cpp:3534-3546).
        foreach (var c in parent.ChildShips.ToArray())
        {
            var cmount = c.TurretId;
            parent.RemoveChildShip(c);          // native DeleteChildShip on old parent (SetParentShip, shipIGC.cpp:3677-3689)
            c.ParentShip = this;
            AddChildShip(c);
            if (cmount is { } slot)
            {
                var w = Weapons.FirstOrDefault(x => x.MountId == slot);
                w?.SetGunner(c);            // SetTurretID→SetGunner, shipIGC.h:1405-1437
            }
        }

        // 5. Adopt the parent's kinematics + hull/shield state (shipIGC.cpp:3550-3563).
        Position = parent.Position;
        Velocity = parent.Velocity;
        SetOrientation(parent.Orientation);
        _fraction = parent.Fraction;
        _shieldFraction = parent.ShieldFraction;

        // 6. Demote the old parent into the vacated turret slot
        //    (shipIGC.cpp:3567-3568).
        parent.ParentShip = this;
        AddChildShip(parent);
        parent.TurretId = turretid;
        parent.ClearLoadout();              // "no parts allowed on a turret", shipIGC.cpp:3619-3624
        var vacatedWeapon = Weapons.FirstOrDefault(w => w.MountId == turretid);
        vacatedWeapon?.SetGunner(parent);
    }

    /// <summary>
    /// Amount of mined ore this ship currently carries, in arbitrary ore units.
    /// Starts at 0 (plain value, not nullable — unlike <see cref=\"StationId\"/>,
    /// a cargo quantity has no meaningful \"absent\" state, 0 already encodes
    /// empty). Accumulated per tick by the sector's mining pass
    /// (<see cref=\"Model.Sector.ResolveMining\"/>) when the ship is adjacent
    /// to and stopped beside a minable asteroid, up to the flat
    /// <see cref=\"MineCapacity\"/> constant (standing in for the per-side
    /// attribute-scaled capacity, out of this task's scope). This mirrors the
    /// C++ <c>m_fOre</c> (<c>shipIGC.h:2634</c>), which the ship-fractions wire
    /// slot carries (<c>ServerShipUpdate</c> byte 45 = <c>Ore/MineCapacity</c>,
    /// <c>shipIGC.h:1125,1148</c> <c>SetOre(capacity, current)</c>) and that the
    /// native sells back for credits via a <c>PaydayEvent</c> — the P56 sell
    /// step converts it to money at a friendly dock
    /// (<c>ServerHost.DockWithStationEvent</c>), which credits the owning
    /// side's treasury and zeroes this cargo.
    /// Written by the mining pass on the sim thread.
    /// </summary>
    public float Ore { get; set; }

    /// <summary>
    /// Flat ore-carried capacity constant, standing in for the per-side
    /// <c>GlobalAttributeSet</c>-scaled capacity that the native engine derives
    /// from tech-level attributes (out of this task's scope). A literal,
    /// chosen small enough that a synthetic test reaches the cap in a handful
    /// of <see cref=\"Model.Sector.MiningRate\"/> ticks — labelled a
    /// simplification, not a verified native value (the real default lives in
    /// core data this port does not decode). Saturating this value pauses
    /// mining (<see cref=\"Model.Sector.ResolveMining\"/> continues only while
    /// <c>Ore &lt; MineCapacity</c>), matching the native capacity clamp.
    /// </summary>
    public const float MineCapacity = 150.0f;

    /// <summary>
    /// Flat credits-per-unit-of-ore this server pays when a ship sells its
    /// cargo at a friendly dock (<see cref="Model.Sector"/> ↔
    /// <c>ServerHost.DockWithStationEvent</c>, P56). Stands in for the native
    /// <c>c_fcidValueHe3</c> multiplied by the per-side attribute-scaled
    /// <c>c_gaMiningYield</c> (<c>shipIGC.cpp:1969-1990</c>:
    /// <c>m_fOre * value * yield</c>). Like <see cref="MineCapacity"/>, a
    /// literal labelled a simplification, not a verified native value (the real
    /// defaults live in core data / per-side GlobalAttributeSets this port does
    /// not decode). Chosen so <c>ore × constant</c> yields an easily-asserted
    /// integer credit total in tests. The per-side yield scaling is out of
    /// scope; every friendly dock pays the same flat rate.
    /// </summary>
    public const float OreCreditPerUnit = 5.0f;

    /// <summary>
    /// True while this ship is actively mining (has mined at least one tick
    /// against a rock and not yet detached). Latched by the mining pass on the
    /// first satisfied tick and never auto-cleared — there is no server-side
    /// concept of an un-mine command, and travel leaves proximity naturally
    /// (the pass simply stops accruing; the flag records the transition so the
    /// start-mining log fires once per engagement, matching the transition-only
    /// log the task asks for).
    /// </summary>
    public bool IsMining { get; set; }

    /// <summary>
    /// P165 — true while this ship has an active <see cref="BuildingEffect"/>
    /// registered against it (it is committed to constructing a station). A
    /// ship-side flag distinct from the sector's own
    /// <see cref="Model.Sector.BuildingEffects"/> list: nothing on the ship
    /// otherwise knows it is mid-build, and the Builder decision cycle needs
    /// that to avoid re-deciding (and re-registering a build) every idle tick.
    /// Mirroring <see cref="IsMining"/>'s shape: latched <c>true</c> by
    /// <see cref="Model.Sector.StartBuildingEffect"/> when it registers the
    /// effect for this builder, and cleared <c>false</c> in <b>both</b> of
    /// <see cref="Model.Sector.ResolveBuildingEffects"/>'s terminal branches —
    /// the interruption abort (Builder died, build dropped) and
    /// <see cref="Model.Sector.CompleteBuild"/> (build finished, the self-
    /// sacrificing transaction removes the Builder from the sector). Default
    /// <c>false</c> (a ship that has never started a build).</summary>
    public bool IsBuilding { get; set; }

    /// <summary>
    /// Mount a weapon on this ship and realise it against this ship's mission
    /// core (the analogue of the C++ <c>CweaponIGC::Initialize</c> resolving
    /// <c>m_projectileType</c> from the mission's core,
    /// <c>weaponIGC.cpp:45</c>). Requires a <see cref="Mission"/> (the ship must
    /// be placed first) and a <c>Weapon</c> part type; the referenced projectile
    /// type must exist in the core. The weapon is constructed <b>ready</b>
    /// (fully mounted), so the combat slice can fire without first spinning a
    /// mounting state machine. This is the combat/spawn entry point, kept as
    /// its pre-P61 self: it realizes into <see cref="Weapons"/> only (no
    /// <see cref="Parts"/> registration, since its argument is the bare weapon
    /// subtype and <see cref="DataPartTypeIGC.PartId"/> lives on the outer
    /// record). The <em>loadout</em> path that needs round-tripping calls
    /// <see cref="MountPart"/> with the full record instead — see that method.
    /// </summary>
    /// <exception cref="InvalidOperationException">The ship is not placed, or the weapon's projectile type is absent from the core.</exception>
    public Weapon MountWeapon(DataPartTypeIGC.Weapon type, short mountId = 0)
    {
        var mission = Mission
            ?? throw new InvalidOperationException("cannot mount a weapon on a ship that has not been placed (no mission to resolve its projectile type)");
        if (!mission.Core.ProjectileTypes.TryGetValue(type.ProjectileTypeId, out var projectileType))
            throw new InvalidOperationException(
                $"weapon '{type.ProjectileTypeId}' names projectile type {type.ProjectileTypeId} absent from core {mission.Core.Version}");
        var weapon = new Weapon(this, type, projectileType, mountId);
        _weapons.Add(weapon);
        return weapon;
    }

    /// <summary>
    /// Mount an arbitrary part subtype on this ship and remember it in the
    /// generalized loadout (<see cref="Parts"/>). This is the loadout-path
    /// successor to the weapons-only <see cref="MountWeapon"/> (which it
    /// generalises: every weapon mounted here is realized in-sim just as
    /// <c>MountWeapon</c> realizes them) and covers every subtype:
    /// <list type="bullet">
    /// <item><b>Weapon</b> — realized in-sim: resolves the projectile against the
    /// mission core (needs a placed ship), constructs a <see cref="Weapon"/>,
    /// adds it to <see cref="Weapons"/>, and sets <see cref="MountedPart.FunctionalWeapon"/>.
    /// If the ship isn't placed or the projectile is absent from the core, the part
    /// is still recorded inertly (functional weapon null) — this slice never drops a
    /// requested part silently, it only downgrades its in-sim behavior.</item>
    /// <item><b>Cloak</b> — realized in-sim since P269: a mounted <c>ET_Cloak@0</c>
    /// part is picked up by <see cref="Ship.UpdateCloak"/>, which activates it from
    /// the <c>CloakActive</c> control bit, drains energy, and reduces
    /// <see cref="Ship.EffectiveSignature"/> (cloaking). Shield / Afterburner /
    /// Pack remain <b>inert bookkeeping</b> — recorded on the requested slot with
    /// the requested quantities so the loadout round-trips, but no simulation
    /// effect (no damage mitigation / thrust bonus / cargo) — those are Phase 2 of
    /// the roadmap.</item>
    /// </list>
    /// Returns the mounted-part record (always recorded, even when a weapon
    /// couldn't be realized). Callers that need the realized weapon should read
    /// <see cref="MountedPart.FunctionalWeapon"/> and handle a null when the
    /// subtype isn't weapon or realization failed.
    /// P314 — the wire's 1-byte <c>nParts</c> field (see
    /// <see cref="ExportShip.Encode"/>, which throws past 255) caps a ship's
    /// mounted-part count at <see cref="byte.MaxValue"/>. Enforced HERE as the
    /// port's single upstream choke point — <see cref="_parts"/> only ever grows
    /// through this method, so no ship can reach 256 parts via any path
    /// (BUY_LOADOUT's ApplyLoadout, TREASURE_ACK, Promote's inventory copy,
    /// cargo re-equip) and trip the Encode throw. A refused mount returns
    /// <c>null</c>; callers ignore the value, so the refusal is silent unless a
    /// caller chooses to log it.
    /// </summary>
    /// <returns>The mounted-part record, or <c>null</c> if the mount was refused
    /// because the ship is already at the wire's 255-part ceiling. Always recorded
    /// (even when a weapon couldn't be realized) unless refused.</returns>
    public MountedPart? MountPart(DataPartTypeIGC type, short amount, byte mountId, byte fractionMounted)
    {
        if (_parts.Count >= byte.MaxValue)
            return null;

        var part = new MountedPart
        {
            PartId = type.PartId,
            Amount = amount,
            MountId = mountId,
            FractionMounted = fractionMounted,
            Type = type,
        };

        // Realize a weapon subtype against the mission core (best-effort).
        if (type.Type is DataPartTypeIGC.Weapon w && Mission is { } mission)
        {
            if (mission.Core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var projectileType))
            {
                var weapon = new Weapon(this, w, projectileType, mountId);
                _weapons.Add(weapon);
                part.FunctionalWeapon = weapon;
            }
        }

        _parts.Add(part);

        // CshieldIGC::Arm (shieldIGC.h:99-103): mounting a Shield part charges
        // it to fully up (m_fraction = 1.0f). The shield is now functional
        // (Phase 2 of the roadmap) rather than inert bookkeeping, so the
        // charge fraction reflects the just-mounted fresh shield.
        if (type.Type is DataPartTypeIGC.Shield)
            _shieldFraction = 1.0f;

        // P173: raise the host seam so the server can broadcast a real
        // FM_S_LOADOUT_CHANGE(96) carrying the current whole loadout (this
        // port collapses native's Added/Mounted distinction — both AddPart and
        // SetMountID boil down to a MountPart here — to one Mounted raise,
        // a stated simplification, not a silent gap).
        Mission?.Site.LoadoutChangeEvent(this, part, LoadoutChange.Mounted);

        return part;
    }

    /// <summary>
    /// P143 — the port of <c>CshipIGC::GetMountedPart</c>-based swap/move behind
    /// the native <c>FM_CS_SWAP_PART(34)</c> handler (<c>FedSrv.CPP:6485-6495</c>).
    /// Moves the mounted part at <c>(etOld, mountOld)</c> to <c>mountNew</c>;
    /// if a <b>different</b> part of the <b>same</b> <c>etOld</c> type already
    /// occupies <c>mountNew</c>, the two exchange slots (native swaps them via
    /// <c>ppart-&gt;SetMountID(c_mountNA)</c> → <c>ppartNew-&gt;SetMountID(mountOld)</c>
    /// → <c>ppart-&gt;SetMountID(mountNew)</c>, <c>FedSrv.CPP:6491-6495</c>; a
    /// list-based store has no mount-index collision, so the temporary NA hop
    /// is unnecessary and both parts are repointed directly). Otherwise it is a
    /// plain move — only the moved part's <c>MountId</c> changes.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Return value mirrors the native broadcast condition.</b> Returns
    /// <c>true</c> iff a part was found at the source mount (native broadcasts
    /// whenever <c>GetMountedPart(etOld, mountOld)</c> is non-null,
    /// <c>FedSrv.CPP:6485-6498</c>); <c>false</c> when the source is empty — the
    /// native's silent no-op (no error, no broadcast, no ack).</item>
    /// <item><b>Same-slot request</b> (<c>mountOld == mountNew</c>) matches the
    /// source (and the same part again as occupant) → treated as a found
    /// no-op: returns <c>true</c> and changes nothing, exactly as native's
    /// sequence nets out to (<c>FedSrv.CPP:6485-6495</c>), so the handler still
    /// broadcasts the echo.</item>
    /// <item><b>Realized weapons stay in sync.</b> <see cref="MountedPart"/>
    /// is <c>init</c>-only, so the slot change is expressed by replacing the
    /// list entry; if the moved part is a realized weapon its
    /// <see cref="Weapon"/> <c>MountId</c> is updated too (<see cref="Weapon.SetMountId"/>)
    /// so firing/turret-binding read the new slot.</item>
    /// </list>
    /// </remarks>
    /// <returns><c>true</c> if a part was found at <c>mountOld</c> (a swap, a
    /// plain move, or a same-slot no-op all count — each warrants the echo
    /// broadcast); <c>false</c> if the source mount holds nothing.</returns>
    public bool TrySwapPart(EquipmentType etOld, byte mountOld, byte mountNew)
    {
        var src = _parts.FirstOrDefault(p => p.EquipmentType == etOld && p.MountId == mountOld);
        if (src is null) return false;

        // GetMountedPart(etOld, mountNew) — the SAME etOld, not a separate type
        // (FedSrv.CPP:6488): only a part of the same equipment type can occupy
        // and trade the destination slot.
        var dst = _parts.FirstOrDefault(p => p.EquipmentType == etOld && p.MountId == mountNew);
        if (dst is not null && !ReferenceEquals(dst, src))
        {
            RepointMount(src, mountNew);   // ppart->SetMountID(mountNew)
            RepointMount(dst, mountOld);   // ppartNew->SetMountID(mountOld)
        }
        else
        {
            RepointMount(src, mountNew);   // plain move (or same-slot no-op)
        }
        return true;
    }

    /// <summary>
    /// P192 — the <b>cargo-aware</b> generalization of <see cref="TrySwapPart"/>,
    /// backing native's <c>FM_CS_SWAP_PART(34)</c> handler when either mount is a
    /// negative cargo-bay slot (<c>FedSrv.CPP:6459-6501</c>; native's
    /// <c>GetMountedPart</c>/<c>SetMountID</c> route negative mounts to the
    /// <c>m_mountedCargos[]</c> array, <c>shipIGC.cpp:1520-1548</c>). Moves the
    /// part at <c>(etOld, mountOld)</c> to <c>mountNew</c>, swapping it with a
    /// different same-<c>EquipmentType</c> part already at <c>mountNew</c> if
    /// present, across the equipped-mount list (<c>_parts</c>, non-negative
    /// mounts) <em>and</em> the cargo hold (<c>_cargo</c>, negative mounts) — the
    /// same same-type/slot-trade semantics <see cref="TrySwapPart"/> already has,
    /// generalized so the source/destination can each be an equipped slot or a
    /// cargo slot. This is the real native mechanism for filling/emptying the
    /// cargo hold (a prerequisite for <c>RELOAD</c>(129) to have anything to
    /// operate on).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Return value mirrors <see cref="TrySwapPart"/>'s.</b> <c>true</c>
    /// iff a part was found at the source mount (the native broadcast condition);
    /// <c>false</c> on an empty source — the silent no-op.</item>
    /// <item><b>Mounts may mix signs.</b> All four combinations work (equipped→
    /// equipped, equipped→cargo, cargo→equipped, cargo→cargo), so a part moved
    /// into cargo earlier can round-trip back to an equipped slot.</item>
    /// <item><b>Cargo parts carry <see cref="MountedPart.MountId"/> 0.</b> The
    /// cargo slot is implicit in the array position (<see cref="CargoHold"/>);
    /// re-equipping a cargo part places it at the requested positive mount via
    /// <see cref="MountPart"/> (which re-realizes a weapon subtype).</item>
    /// </list>
    /// </remarks>
    /// <returns><c>true</c> if a part was found at <c>mountOld</c> (a swap, plain
    /// move, or same-slot no-op); <c>false</c> if the source holds nothing.</returns>
    public bool TrySwapPartWithCargo(EquipmentType etOld, int mountOld, int mountNew)
    {
        var src = ResolvePart(etOld, mountOld);
        if (src is null) return false;                       // source empty → silent no-op

        var dst = ResolvePart(etOld, mountNew);              // GetMountedPart(etOld, mountNew)
        MountedPart? dstSwap = dst is not null && !ReferenceEquals(dst, src) ? dst : null;

        RemoveFromHome(src, mountOld);                       // ppart->SetMountID(c_mountNA) prep
        if (dstSwap is not null) RemoveFromHome(dstSwap, mountNew);
        PlaceIntoHome(src, mountNew);                        // ppart->SetMountID(mountNew)
        if (dstSwap is not null) PlaceIntoHome(dstSwap, mountOld);  // ppartNew->SetMountID(mountOld)
        return true;
    }

    /// <summary>Locate a part by <c>(equipmentType, wireMount)</c>: a non-negative
    /// mount scans the equipped list, a negative mount indexes the cargo hold
    /// (native <c>PartLocation</c>, <c>shipIGC.cpp:1516-1549</c>).</summary>
    private MountedPart? ResolvePart(EquipmentType et, int mount)
        => mount < 0 ? _cargo[CargoSlotForMount(mount)]
                     : _parts.FirstOrDefault(p => p.EquipmentType == et && p.MountId == mount);

    /// <summary>Remove a part from its current home (equipped list + realized
    /// weapon, or the cargo slot), mirroring the detach half of a mount move.</summary>
    private void RemoveFromHome(MountedPart part, int mount)
    {
        if (mount < 0)
        {
            _cargo[CargoSlotForMount(mount)] = null;
            return;
        }
        _parts.Remove(part);
        if (part.FunctionalWeapon is { } w)
            _weapons.Remove(w);
    }

    /// <summary>Place a part into its new home: a negative mount stores it in a
    /// cargo slot (as a placeholder-<see cref="MountedPart.MountId"/> 0 copy);
    /// a non-negative mount equips it via <see cref="MountPart"/> (realizing a
    /// weapon subtype), the native <c>SetMountID</c> positive path.</summary>
    private void PlaceIntoHome(MountedPart part, int mount)
    {
        if (mount < 0)
        {
            _cargo[CargoSlotForMount(mount)] = AsCargoPart(part);
            return;
        }
        MountPart(part.Type, part.Amount, (byte)mount, part.FractionMounted);
    }

    /// <summary>A <see cref="MountedPart"/> as a cargo-hold entry: same wire
    /// quantities, <c>MountId</c> forced to the placeholder 0 (slot implicit in
    /// the array position), no realized weapon (a cargo weapon is inert until
    /// re-equipped).</summary>
    private static MountedPart AsCargoPart(MountedPart p) => new()
    {
        PartId = p.PartId,
        Amount = p.Amount,
        MountId = 0,
        FractionMounted = p.FractionMounted,
        Type = p.Type,
        FunctionalWeapon = null,
    };

    /// <summary>
    /// P192 — apply one <c>RELOAD(129)</c> entry to this ship's cargo hold, the
    /// port of one iteration of native's <c>FM_CS_RELOAD</c> loop
    /// (<c>FedSrv.CPP:6529-6643</c>). Returns <c>false</c> for a malformed entry
    /// (native's <c>bBoot = true</c>, <c>FedSrv.CPP:6531-6643</c>); the caller
    /// decides the divergent response (this port softens the hard
    /// <c>TrapHackBoot</c> disconnect to a log-and-stop, matching its established
    /// convention — see <c>CARGO_BAY_RELOAD_P192.md</c>). <b>Callers must not
    /// roll back earlier-in-batch transfers</b> when a later entry fails —
    /// native's <c>break</c> leaves them applied, a faithful quirk.
    /// </summary>
    /// <remarks>
    /// Faithful to native's per-entry body: <c>OT_pack</c> (ammo/fuel) drains the
    /// pack into the ship's live pool — ammo into <see cref="Ammo"/> + resetting
    /// every ammo-consuming weapon's mounted-fraction to 0 (<c>FedSrv.CPP:6570-
    /// 6582</c>); fuel into the ship's fuel pool, which this port deliberately
    /// does not track (<c>Ship.cs</c>'s documented no-op, so the fuel amount is
    /// ignored but the afterburner's mounted-fraction is still reset,
    /// <c>FedSrv.CPP:6583-6591</c>) — and a launcher (missile/chaff/dispenser;
    /// only <c>ChaffLauncher</c> is constructible in this port) swaps its
    /// <c>Amount</c> into the equipped same-type launcher at mount 0
    /// (<c>FedSrv.CPP:6593-6640</c>). <c>amountTransfered == -1</c> (native
    /// <c>NA</c>) terminates the cargo item outright rather than transferring a
    /// quantity. <c>ResetTimeLoaded()</c> (<c>FedSrv.CPP:6638</c>) is N/A: this
    /// port tracks no per-launcher "time since last reload" field.
    /// </remarks>
    /// <returns><c>true</c> if the entry was well-formed and applied; <c>false</c>
    /// if it is malformed (the native <c>bBoot</c> condition).</returns>
    public bool TryApplyReloadEntry(sbyte mount, short amountTransfered)
    {
        // (mount >= 0) || (mount < -c_maxCargo) → boot (FedSrv.CPP:6531).
        if (mount >= 0 || mount < -CargoCapacity) return false;

        int slot = CargoSlotForMount(mount);
        var part = _cargo[slot];
        if (part is null) return false;                  // GetMountedPart(NA, mount) null → boot

        if (part.Type.Type is DataPartTypeIGC.Pack pack)
        {
            byte packType = pack.PackType;
            short amount = part.Amount;

            if (amountTransfered == -1)                  // NA → terminate the whole pack
            {
                _cargo[slot] = null;                     // ppack->Terminate()
            }
            else
            {
                if (amountTransfered < 0 || amountTransfered >= amount) return false;  // boot
                SetCargoPartAmount(slot, (short)(amount - amountTransfered));
                amount = amountTransfered;
            }

            if (packType == cPackAmmo)                   // c_packAmmo = 0 (Igc/igc.h:549)
            {
                Ammo += amount;                          // SetAmmo(ammo + amount)
                ResetAmmoWeaponFractions();              // FedSrv.CPP:6574-6581
            }
            else                                         // c_packFuel = 1 (Igc/igc.h:550)
            {
                // SetFuel is a documented no-op (no fuel pool in this port);
                // still reset the afterburner's mounted-fraction (FedSrv.CPP:6588-6590).
                ResetEquippedFraction(EquipmentType.Afterburner);
            }
            return true;
        }

        // Launcher branch (FedSrv.CPP:6593-6640).
        if (!IsLauncherType(part.Type.Type)) return false;   // !IsLauncher(type) → boot

        var launcherType = part.EquipmentType;
        var mounted = _parts.FirstOrDefault(p => p.EquipmentType == launcherType && p.MountId == 0);

        if (amountTransfered == -1)                      // NA
        {
            if (mounted is not null && mounted.Amount != 0) return false;   // boot (FedSrv.CPP:6607)
            if (mounted is not null)                     // terminate the equipped launcher
            {
                _parts.Remove(mounted);
                if (mounted.FunctionalWeapon is { } w) _weapons.Remove(w);
            }
            _cargo[slot] = null;                         // cargo launcher leaves cargo...
            MountPart(part.Type, part.Amount, 0, part.FractionMounted);  // ...and mounts at 0 (SetMountID(0))
            return true;
        }
        else
        {
            short amount = part.Amount;
            if (mounted is null || amountTransfered <= 0 || amountTransfered > amount) return false;  // boot
            if (amount == amountTransfered) _cargo[slot] = null;            // terminate cargo launcher
            else SetCargoPartAmount(slot, (short)(amount - amountTransfered));
            SetPartAmount(mounted, (short)(mounted.Amount + amountTransfered));  // add to equipped
            ResetEquippedFraction(launcherType);         // mounted-fraction reset (FedSrv.CPP:6637)
            // ResetTimeLoaded() N/A — no per-launcher reload-time tracker here.
            return true;
        }
    }

    /// <summary>Whether a part subtype is a launcher (native
    /// <c>IlauncherIGC::IsLauncher</c>: magazine/dispenser/chaff-launcher).
    /// Only <c>ChaffLauncher</c> is constructible in this port (the
    /// <c>DataLauncherTypeIGC</c> subtypes have no corpus part-record to
    /// decode); the magazine/dispenser cases are enumerated for completeness.</summary>
    private static bool IsLauncherType(DataPartTypeIGC.Subtype t)
        => t is DataPartTypeIGC.ChaffLauncher
        || t.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser;

    /// <summary>Reset the mounted-fraction of a single equipped part
    /// (<c>et</c>, mount 0) to 0, the native <c>ppart-&gt;SetMountedFraction(0.0f)</c>
    /// on the afterburner (fuel pack) and equipped launcher (launcher swap) reload
    /// paths (<c>FedSrv.CPP:6590,6637</c>). A reload resets the load delay, so the
    /// wire <c>fractionMounted</c> byte is cleared to 0.</summary>
    private void ResetEquippedFraction(EquipmentType et)
    {
        for (int i = 0; i < _parts.Count; i++)
        {
            var p = _parts[i];
            if (p.EquipmentType == et && p.MountId == 0 && p.FractionMounted != 0)
                _parts[i] = WithFraction(p, 0);
        }
    }

    /// <summary>Reset the mounted-fraction to 0 of every mounted ammo-consuming
    /// weapon — the native "disable all mounted weapons that use ammo"
    /// (<c>FedSrv.CPP:6574-6581</c>), a reload-delay reset after an ammo-pack
    /// transfer into <see cref="Ammo"/>.</summary>
    private void ResetAmmoWeaponFractions()
    {
        for (int i = 0; i < _parts.Count; i++)
        {
            var p = _parts[i];
            if (p.Type.Type is DataPartTypeIGC.Weapon wp && wp.CAmmoPerShot != 0 && p.FractionMounted != 0)
                _parts[i] = WithFraction(p, 0);
        }
    }

    /// <summary>Rewrite a <see cref="MountedPart"/>'s <c>FractionMounted</c> wire
    /// byte via list-entry replacement (the properties are <c>init</c>-only).</summary>
    private static MountedPart WithFraction(MountedPart p, byte fractionMounted) => new()
    {
        PartId = p.PartId,
        Amount = p.Amount,
        MountId = p.MountId,
        FractionMounted = fractionMounted,
        Type = p.Type,
        FunctionalWeapon = p.FunctionalWeapon,
    };

    /// <summary>Rewrite a <see cref="MountedPart"/>'s <c>Amount</c> via
    /// list-entry replacement (the properties are <c>init</c>-only), returning
    /// the replacement for direct assignment — the <c>Amount</c>-analogue of
    /// <see cref="WithFraction"/> (used by the launch reload's pack top-up,
    /// <see cref="RepairAndRefuel"/>).</summary>
    private static MountedPart WithAmount(MountedPart p, short amount) => new()
    {
        PartId = p.PartId,
        Amount = amount,
        MountId = p.MountId,
        FractionMounted = p.FractionMounted,
        Type = p.Type,
        FunctionalWeapon = p.FunctionalWeapon,
    };

    /// <summary>Rewrite a cargo slot's part <c>Amount</c> via list-entry
    /// replacement (native <c>ppack-&gt;SetAmount</c> / <c>plauncher-&gt;SetAmount</c>).</summary>
    public void SetCargoPartAmount(int slot, short amount)
    {
        if (slot < 0 || slot >= _cargo.Length || _cargo[slot] is not { } p) return;
        _cargo[slot] = new MountedPart
        {
            PartId = p.PartId,
            Amount = amount,
            MountId = p.MountId,
            FractionMounted = p.FractionMounted,
            Type = p.Type,
            FunctionalWeapon = p.FunctionalWeapon,
        };
    }

    /// <summary>Empty a cargo slot (native <c>ppart-&gt;Terminate()</c> on a
    /// fully-drained/NA cargo item, <c>FedSrv.CPP:6555,6614,6632</c>).</summary>
    public void ClearCargoSlot(int slot)
    {
        if (slot >= 0 && slot < _cargo.Length) _cargo[slot] = null;
    }

    /// <summary>The native <c>c_packAmmo</c> pack type (<c>Igc/igc.h:549</c>).</summary>
    private const byte cPackAmmo = 0;
    /// <summary>The native <c>c_packFuel</c> pack type (<c>Igc/igc.h:550</c>).</summary>
    private const byte cPackFuel = 1;

    /// <summary>Rebind an existing <see cref="MountedPart"/> to a new mount slot
    /// by replacing its list entry (the properties are <c>init</c>-only) and
    /// pushing the new slot into any realized <see cref="Weapon"/>.</summary>
    private void RepointMount(MountedPart part, byte mountId)
    {
        int idx = _parts.IndexOf(part);
        if (idx < 0) return;   // defensive; every caller passes a live member
        _parts[idx] = new MountedPart
        {
            PartId = part.PartId,
            Amount = part.Amount,
            MountId = mountId,
            FractionMounted = part.FractionMounted,
            Type = part.Type,
            FunctionalWeapon = part.FunctionalWeapon,
        };
        part.FunctionalWeapon?.SetMountId(mountId);
    }

    /// <summary>
    /// P145 — rewrite an existing <see cref="MountedPart"/>'s <c>Amount</c>
    /// (e.g. merging a picked part-treasure quantity into an already-mounted
    /// same-type part, native <c>ppart-&gt;SetAmount(ppart-&gt;GetAmount() + amount)</c>,
    /// <c>FedSrv.CPP:6102-6103</c>) by replacing the list entry — the properties
    /// are <c>init</c>-only, so the same list-entry-replacement idiom
    /// <see cref="RepointMount"/> uses. The realized weapon (if any) is carried
    /// over untouched; only the wire <c>Amount</c> changes.
    /// </summary>
    public void SetPartAmount(MountedPart part, short amount)
    {
        int idx = _parts.IndexOf(part);
        if (idx < 0) return;   // defensive; every caller passes a live member
        _parts[idx] = new MountedPart
        {
            PartId = part.PartId,
            Amount = amount,
            MountId = part.MountId,
            FractionMounted = part.FractionMounted,
            Type = part.Type,
            FunctionalWeapon = part.FunctionalWeapon,
        };
    }

    /// <summary>
    /// P144 — the port of the <c>GetMountedPart</c>-based part resolution +
    /// <c>ppart-&gt;Terminate()</c> removal behind the native
    /// <c>FM_CS_DROP_PART(33)</c> handler (<c>FedSrv.CPP:6321-6327</c>): locate
    /// the mounted part at <c>(et, mount)</c> and <b>remove it from the ship's
    /// loadout entirely</b> — unlike <c>SWAP_PART</c> (a reposition,
    /// <see cref="TrySwapPart"/>) this is a removal, and unlike the re-point of
    /// <see cref="RepointMount"/> it leaves <c>_parts</c> altogether (the
    /// drop's <c>ppart-&gt;Terminate()</c>), so the same
    /// <c>_parts.FirstOrDefault(p =&gt; p.EquipmentType == et &amp;&amp;
    /// p.MountId == mount)</c> lookup <see cref="TrySwapPart"/> uses is reused
    /// rather than duplicated.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Returns false when the source mount is empty</b> — native's
    /// silent no-op (no error, no broadcast, no treasure,
    /// <c>FedSrv.CPP:6322</c>), the same contract
    /// <see cref="TrySwapPart"/>'s return value establishes.</item>
    /// <item><b>Termination of a realized weapon.</b> Native's
    /// <c>ppart-&gt;Terminate()</c> destroys the mounted part object; this
    /// port's analogue is dropping the part from <c>_parts</c> <em>and</em>,
    /// when it carried a realized in-sim <see cref="Weapon"/>, dropping that
    /// from <see cref="Weapons"/> too so it can no longer fire (the functional
    /// weapon is ship-local — never added to a sector — so no sector-side
    /// cleanup is needed; cf. <see cref="MountPart"/>).</item>
    /// </list>
    /// </remarks>
    /// <returns><c>true</c> if a part was found and removed (the caller may
    /// then read its <see cref="MountedPart.PartId"/>/<c>Amount</c>/<c>Type</c>
    /// to build the treasure); <c>false</c> if the mount held nothing.</returns>
    public bool TryRemovePart(EquipmentType et, byte mount, out MountedPart dropped)
    {
        var part = _parts.FirstOrDefault(p => p.EquipmentType == et && p.MountId == mount);
        if (part is null)
        {
            dropped = null!;
            return false;
        }

        _parts.Remove(part);
        if (part.FunctionalWeapon is { } w)
            _weapons.Remove(w);   // ppart->Terminate(): the realized weapon leaves too
        dropped = part;

        // P173: raise the host seam so the server can broadcast a real
        // FM_S_LOADOUT_CHANGE(96) reflecting the loadout without this part
        // (native raises c_lcRemoved, shipIGC.cpp:1510, passing the part).
        Mission?.Site.LoadoutChangeEvent(this, part, LoadoutChange.Removed);
        return true;
    }

    /// <summary>
    /// P144 — create the part-type treasure a <c>DROP_PART</c> leaves in space,
    /// the port of the third <c>CreateTreasure(..., IpartIGC*, ...)</c> overload
    /// (<c>FedSrv.CPP:4380-4396</c>) delegating to the second overload
    /// (<c>FedSrv.CPP:4366-4378</c>) delegating to the <c>DataTreasureIGC</c>
    /// builder (<c>FedSrv.CPP:4324-4362</c>). Computes the real treasure math,
    /// builds the <see cref="DataTreasureIGC"/>, constructs the
    /// <see cref="Treasure"/> model, and places it in the ship's sector
    /// (<c>sector.Add</c>). The caller is responsible for sending the resulting
    /// <c>FM_S_TREASURE(28)</c> notice.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><b>Call only for a non-skipped drop.</b> The native DROP_PART
    /// handler guards creation with <c>((type != OT_pack) &amp;&amp;
    /// !IlauncherIGC::IsLauncher(type)) || (ppart-&gt;GetAmount() != 0)</c>
    /// (<c>FedSrv.CPP:6323</c>); the launcher branch collapses away in this
    /// port (no magazine/dispenser/chaff-launcher part subtype exists in
    /// <see cref="DataPartTypeIGC"/>'s set — only Weapon/Shield/Cloak/
    /// Afterburner/Pack), so the effective gate is <em>skip iff Pack &amp;&amp;
    /// Amount == 0</em>. The handler applies that; this method assumes it has
    /// been.</item>
    /// <item><b>Radius</b> = the port's established
    /// <c>0.5 × HullType.Length</c> ship-radius convention (analogue of native
    /// <c>pship-&gt;GetRadius()</c>, <c>FedSrv.CPP:4330</c>) — the same sphere
    /// P113's ripcord / P116's landing probes use (<c>Ship.cs:411-415</c>).</item>
    /// <item><b>Velocity:</b> <c>v0 = direction × 25.0f + ship velocity</c>
    /// (<c>dv = 25.0f</c> is the DROP_PART call's argument,
    /// <c>FedSrv.CPP:6326</c>). A real value is computed and stored on the wire
    /// for fidelity, but — per the task's explicit scope and <see cref="Treasure"/>
    ///'s own remarks — <b>no velocity integration</b> is added to the sim: the
    /// treasure rests statically at <c>P0</c> exactly as cash treasures do. The
    /// deceleration-to-stop of <c>treasureIGC.cpp:95-116</c> is a known,
    /// accepted gap.</item>
    /// <item><b>Amount:</b> set to the dropped part's own <c>Amount</c> per this
    /// task's spec (the verification demands the treasure's
    /// <c>TreasureId</c>/<c>Amount</c> match the dropped part). This is a
    /// deliberate simplification of native, which for non-consumable types
    /// (weapon/shield/cloak/afterburner) falls into the third overload's
    /// <c>default: amount = 0</c> branch and reserves the consumed quantity only
    /// for magazine/dispenser/pack — consumables this port does not represent
    /// yet. Documented here, not silently dropped.</item>
    /// </list>
    /// </remarks>
    /// <param name="part">The just-removed mounted part (already out of the
    /// ship's <c>_parts</c>).</param>
    /// <param name="now">Current sim time (seeds the treasure's clock).</param>
    /// <param name="time0">Wire <c>time0</c> — the server tick index, this
    /// port's uint-Time convention (cf. <c>ProjectileInstance</c> stamps its
    /// create-time as <c>(uint)TickIndex</c>, Server.cs:4676).</param>
    public Treasure CreateDropTreasure(MountedPart part, double now, uint time0) =>
        CreateDropTreasure(part.Type, part.Amount, now, time0);

    /// <summary>
    /// P145 — the same drop-treasure creation as the
    /// <see cref="CreateDropTreasure(MountedPart,double,uint)"/> overload, but
    /// built from a part <em>type</em> record + quantity rather than a mounted
    /// part instance. This is exactly the shape the <c>FM_C_TREASURE_ACK</c>
    /// decline branch needs (native <c>CreateTreasure(g.timeNow, pfsPlayer-&gt;
    /// GetIGCShip(), amount, ppt, GetPosition(), 25.0f, 120.0f)</c>,
    /// <c>FedSrv.CPP:6127-6129</c>): after declining, the buffered part-type id +
    /// amount are re-created as a fresh treasure at the ship's current position.
    /// Because native's DROP_PART and decline call <em>the same</em>
    /// <c>CreateTreasure</c> overload (identical dv=25 / lifespan=120 args), the
    /// geometry here is identical to the DROP_PART case: <c>P0</c> = ship
    /// position + <c>(0.5·hull.Length + 10)·</c>random-unit-direction, <c>V0</c> =
    /// that direction × 25 + ship velocity — the re-created treasure lands
    /// "roughly at the ship's current position", exactly as the P145
    /// verification demands.
    /// </summary>
    /// <param name="type">The offered part's resolved static-core type record
    /// (its <see cref="DataPartTypeIGC.PartId"/> becomes the treasure's
    /// <c>TreasureId</c>).</param>
    /// <param name="amount">The buffered offered quantity (the treasure's
    /// <c>Amount</c>).</param>
    /// <param name="now">Current sim time (seeds the treasure's clock).</param>
    /// <param name="time0">Wire <c>time0</c> — the server tick index, this
    /// port's uint-Time convention.</param>
    public Treasure CreateDropTreasure(DataPartTypeIGC type, short amount, double now, uint time0)
    {
        var sector = Sector
            ?? throw new InvalidOperationException("a placed ship must be in a sector before it can drop a part treasure");
        var mission = Mission
            ?? throw new InvalidOperationException("cannot drop a part treasure on a ship that has not been placed (no mission/core)");

        Vector3 direction = RandomDirection();
        float shipRadius = 0.5f * HullType.Length;   // GetRadius() analogue (FedSrv.CPP:4330)

        // p0 = position + (radius + 10)·direction ; v0 = direction·25 + velocity
        // (FedSrv.CPP:4329-4333). Lifespan 120.0f and dv 25.0f are the DROP_PART
        // handler's call arguments (FedSrv.CPP:6326), reused verbatim by the
        // TREASURE_ACK decline (FedSrv.CPP:6127-6129).
        Vector3 p0 = Position + direction * (shipRadius + 10.0f);
        Vector3 v0 = direction * 25.0f + Velocity;

        var data = new DataTreasureIGC(
            P0: [p0.X, p0.Y, p0.Z],
            V0: [v0.X, v0.Y, v0.Z],
            Lifespan: 120.0f,
            Time0: time0,
            ObjectId: mission.NextObjectId(),          // GenerateNewTreasureID() (FedSrv.CPP:4326)
            TreasureId: type.PartId,                   // ppt->GetObjectID() (FedSrv.CPP:4369)
            ClusterId: sector.Id,                      // pcluster->GetObjectID() (FedSrv.CPP:4335)
            Amount: amount,
            TreasureCode: TreasureCode.Part,           // c_tcPart (FedSrv.CPP:4367)
            CreateNow: false);                         // createNow = false (FedSrv.CPP:4372)

        var treasure = new Treasure(data.ObjectId, sector, data, now);
        sector.Add(treasure);
        return treasure;
    }

    /// <summary>A uniform random unit vector — the port of <c>Vector::RandomDirection()</c>
    /// (<c>zlib/vector.h</c>, used at <c>FedSrv.CPP:4329</c>), sampled on the
    /// unit sphere via the equirectangular (latitude/longitude) construction.</summary>
    private static Vector3 RandomDirection()
    {
        double u = Random.Shared.NextDouble() * 2.0 - 1.0;   // z ∈ [-1, 1]
        double phi = Random.Shared.NextDouble() * 2.0 * Math.PI;
        double r = Math.Sqrt(Math.Max(0.0, 1.0 - u * u));
        return new Vector3((float)(r * Math.Cos(phi)), (float)(r * Math.Sin(phi)), (float)u);
    }

    /// <summary>
    /// Fire a missile directly at mission time <paramref name="now"/>, creating a
    /// real <see cref="Missile"/> model in the ship's sector along the ship's
    /// current <see cref="Orientation.GetForward()"/> at
    /// <c>missileType.InitialSpeed</c> plus the ship's own velocity (the same
    /// "inherit the launcher's velocity" convention <c>Weapon.FireWeapon</c> uses
    /// for projectiles — <c>Weapon.cs:213-215</c>, <c>velocity = speed·forward;
    /// if (!absoluteF) velocity += myVelocity</c>). Spawn position is the ship's
    /// centre (no hull mesh ⇒ no muzzle offset, the same simplifying assumption
    /// <c>Weapon.cs</c> documents), pushed forward by the ship's movement over
    /// the interval <c>now − LastUpdate</c> exactly like <c>Weapon.cs:217</c>.
    /// Raises the host seam's <see cref="IIgcSite.MissileCreatedEvent"/> right
    /// after <c>sector.Add(missile)</c> so the server can broadcast a real
    /// <c>FM_CS_FIRE_MISSILE(70)</c>.
    /// </summary>
    /// <remarks>
    /// <b>Directly-callable only</b> — this method exists to be called directly
    /// (by a test now, by a future dispatch path later). It is <em>not</em>
    /// wired into live <c>ControlData</c>/<c>StateM</c> dispatch because there is
    /// no "fire missile" input bit in <see cref=\"ShipControlState\"/> today
    /// (confirmed: the only declared bits are the thrust buttons, afterburner,
    /// and the weapon-selection mask — <c>ShipControlState.cs:13-44</c>); adding
    /// such a control-input bit is a separate, later task. Nor does it consult a
    /// launcher loadout: as <see cref=\"DataPartTypeIGC\"/> documents, there is
    /// no <c>EquipmentType.Launcher</c> case (only
    /// <c>ChaffLauncher/Weapon/Magazine/Dispenser/Shield/Cloak/Pack/Afterburner</c>,
    /// <c>DataPartTypeIGC.cs:9-19</c>) — a launcher is a separate <c>OT_partType</c>
    /// size-class record (<c>DataLauncherTypeIGC.cs:6-14</c>), so the
    /// <c>Ship.Parts</c>/<c>MountPart</c> mounting system cannot represent a
    /// mounted launcher at all today. The caller supplies the missile type
    /// directly, mirroring <c>Ship.MountWeapon</c>'s own precedent
    /// (<c>Ship.cs:301-311</c>).
    /// <para>P179 — an optional <paramref name="target"/> is threaded straight
    /// through to the <see cref="Missile"/> constructor. When provided, the
    /// missile homes (turn-rate-limited steering + real acceleration, P179);
    /// when omitted (every pre-existing call site) it flies the identical
    /// straight-line course as before — purely additive.</para>
    /// <para>P181 — an optional <paramref name="lockFraction"/> (default <c>1f</c>,
    /// preserving Stage 1's exact <c>m_lock ≡ 1.0</c> behavior at every
    /// pre-existing call site) is threaded to the <see cref="Missile"/>
    /// constructor, where it scales the target velocity fed to the homing lead
    /// solve — the <see cref="Ship.UpdateMissileLock"/>-produced lock quality
    /// a caller may supply once the buildup mechanic exists.</para>
    /// </remarks>
    public Missile FireMissile(DataMissileTypeIGC missileType, double now, Model? target = null, float lockFraction = 1f)
    {
        var sector = Sector
            ?? throw new InvalidOperationException("a placed ship must be in a sector before it can fire a missile");
        var mission = Mission
            ?? throw new InvalidOperationException("cannot fire a missile on a ship that has not been placed (no mission/core/site)");

        Vector3 forward = Orientation.GetForward();
        Vector3 velocity = missileType.InitialSpeed * forward + Velocity;   // inherit launcher velocity (Weapon.cs:213-215)
        Vector3 position = Position + Velocity * (float)(now - LastUpdate); // muzzle at origin (no hull mesh, Weapon.cs:217)

        var missile = new Missile(mission.NextObjectId(), sector, missileType, position, velocity, forward, now, this, target, lockFraction);
        sector.Add(missile);

        // P90: raise the host seam so the server can broadcast the real
        // FM_CS_FIRE_MISSILE(70) about this fresh missile (a no-op default for a
        // headless host). Follows Weapon.cs:232-237's ProjectileCreatedEvent use
        // of Mission?.Site — the authoritative mission's site.
        mission.Site.MissileCreatedEvent(missile);
        return missile;
    }

    /// <summary>
    /// P181 — the current lock quality (0..1) of this ship's missile lock
    /// against <see cref="CommandTarget"/>, the port of the native magazine's
    /// <c>m_lock</c> (<c>Igc/magazineigc.cpp:104-176</c>). <c>0</c> by default,
    /// written only by <see cref="UpdateMissileLock"/> and consumed by the
    /// caller as the <c>lockFraction</c> it supplies to
    /// <see cref="FireMissile"/>. Every validity gate failure resets it to
    /// <c>0</c> (matching native's "any gate failure resets to the
    /// <c>m_lock=0.0f</c> already set before the gate",
    /// <c>magazineigc.cpp:140-141</c>).
    /// </summary>
    public float MissileLock { get; private set; }

    /// <summary>
    /// P181 — advance this ship's missile lock quality by one sim chunk
    /// (<paramref name="dt"/> seconds) against the current
    /// <see cref="CommandTarget"/>, porting native <c>CmagazineIGC::Update</c>'s
    /// lock-build block (<c>Igc/magazineigc.cpp:104-176</c>) as a
    /// directly-callable capability. <b>Not wired</b> into any control-input /
    /// live-fire path (there is no <c>missileFireIGC</c>-equivalent bit in
    /// <see cref="ShipControlState"/>, and the native state bit is documented as
    /// not transmitted in the wire mask, <c>Igc/igc.h:829-834</c>) — exactly the
    /// "caller supplies the type directly" precedent of
    /// <see cref="FireMissile"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>Flow (in native order):</b> ripcord active ⇒ lock <c>0</c>; no
    /// command target ⇒ lock <c>0</c>; the validity gate (same sector, hostile
    /// side, damageable) ⇒ lock <c>0</c> on any failure; the range gate ⇒ lock
    /// <c>0</c> on failure; a <see cref="DataMissileTypeIGC.MaxLock"/> of <c>0</c>
    /// ⇒ instant full lock <c>1f</c> (some missile types need no lock at all,
    /// <c>magazineigc.cpp:145-146</c>); otherwise the real angle-off-bow
    /// buildup/decay (see the derivation in <c>docs/MISSILE_LOCK_P181.md</c>),
    /// clamped to <c>[0,1]</c>.</para>
    /// <para><b>Explicit, verified omissions</b> (each confirmed to have no
    /// equivalent in this port, per the P181 task brief): the <c>CanSee</c> gate
    /// (no visibility/sensor model — every valid target is treated as visible),
    /// the <c>AlliedSides</c> half of the hostility test (no alliance system —
    /// hostile is a bare <c>SideId</c> inequality, the P159/P162 idiom), and the
    /// warp-bomb targeting branch (no warp-bomb capability flag is decoded on
    /// <see cref="DataMissileTypeIGC"/> — only the "damageable, non-warp" target
    /// case applies; the port's separate <c>WarpBomb</c> feature is unrelated).
    /// The ECM multiplier, by contrast, IS applied: the brief asserted "no ECM
    /// field anywhere in this port", but <see cref="DataHullTypeIGC.Ecm"/> is in
    /// fact decoded (Data/DataHullTypeIGC.cs:33,220), so native's
    /// <c>lockTime·ECM</c> denominator for a ship target is ported faithfully
    /// (see <c>docs/MISSILE_LOCK_P181.md</c>). A lock target must be a living
    /// <see cref="Ship"/> with a <see cref="SideId"/> differing from this ship's
    /// — the only <see cref="Model"/> kind with both a side and a sector
    /// membership in this port, and the natural missile-lock target (native's
    /// primary case, and the ECM-bearing <c>OT_ship</c> branch of the buildup
    /// formula).</para>
    /// </remarks>
    public void UpdateMissileLock(DataMissileTypeIGC missileType, double dt)
    {
        // 1. Mid-ripcord ⇒ lock resets to zero (magazineigc.cpp:104-105).
        if (IsRipcordActive) { MissileLock = 0f; return; }

        // 2. Resolve the target = the command target; no target ⇒ no lock
        //    (magazineigc.cpp:108, gate failure ⇒ m_lock=0).
        if (CommandTarget is not Ship target) { MissileLock = 0f; return; }

        // 3. Validity gate (magazineigc.cpp:127-132): target in the same
        //    sector/cluster (this ship's own sector's model-list membership —
        //    the same primitive Stage 1's Missile.cs homing code uses via
        //    `_sector.Models.Contains(t)`, Missile.cs:191), on a hostile side
        //    (bare SideId inequality; no alliance system), and damageable (a
        //    living ship — the combat-pass "can take damage" notion, which a
        //    dead ship has left). CanSee and AlliedSides omitted (no port
        //    equivalents, above).
        if (Sector is not { } sector || !sector.Models.Contains(target)) { MissileLock = 0f; return; }
        if (SideId is not { } sideId) { MissileLock = 0f; return; }
        if (target.SideId is not { } otherSide || otherSide == sideId || target.Dead) { MissileLock = 0f; return; }

        // 4. Range gate (magazineigc.cpp:134-143): 1.0 < |dP|² < maxRange² where
        //    maxRange = InitialSpeed·Lifespan + 0.5·Acceleration·Lifespan².
        float lifespan = missileType.Lifespan;
        float maxRange = missileType.InitialSpeed * lifespan + 0.5f * missileType.Acceleration * lifespan * lifespan;
        Vector3 dP = target.Position - Position;
        float l2 = dP.LengthSquared();
        if (!(l2 > 1.0f && l2 < maxRange * maxRange)) { MissileLock = 0f; return; }

        // 5. Some missile types need no lock at all (magazineigc.cpp:145-146).
        if (missileType.MaxLock == 0f) { MissileLock = 1f; return; }

        // 6. Real angle-off-bow buildup/decay (magazineigc.cpp:148-167),
        //    including the target's ECM in the denominator (see below).
        //    angleOffBow is the angle between the direction to the target
        //    and the ship's nose:
        //    dot = -(normalized dP · GetBackward()), clamped to [-1,1] before
        //    acos. (GetBackward() = −GetForward(), so −(dP·backward) = dP·forward.)
        if (missileType.LockAngle <= 0f) { MissileLock = 0f; return; }   // defensive: div-by-zero guard, unreachable in real data
        Vector3 dir = dP.Normalize();
        double dot = -(dir * Orientation.GetBackward());
        float angleOffBow = dot >= 1.0 ? 0.0f
                          : dot <= -1.0 ? MathF.PI
                          : MathF.Acos((float)dot);

        float lockTime = missileType.LockTime;
        float lockAngle = missileType.LockAngle;
        float v = (lockAngle - angleOffBow) / lockAngle;
        if (v < 0.0f) v *= lockTime;
        // Native scales the denominator by the TARGET SHIP's hull ECM
        // (magazineigc.cpp:165-167, the OT_ship branch — the only target kind
        // this port locks onto). DataHullTypeIGC.Ecm IS decoded in this port
        // (Data/DataHullTypeIGC.cs:33,220) — contrary to the P181 brief's "no
        // ECM field" claim — so the multiplier is applied faithfully. (This is
        // the one verified divergence from the brief's omissions; see
        // docs/MISSILE_LOCK_P181.md.) A non-positive ECM (degenerate data) is
        // treated as 1 to avoid dividing by zero.
        float ecm = target.HullType.Ecm > 0f ? target.HullType.Ecm : 1f;
        MissileLock = Math.Clamp(MissileLock + v * (float)dt / (lockTime * ecm), 0f, 1f);
    }

    /// <summary>
    /// P226 — the per-tick lock-buildup half of <see cref="OnUpdate"/>'s
    /// missile armament leg: for a mounted missile launcher
    /// (<see cref="DataPartTypeIGC.MissileLauncher"/>, <c>ET_Magazine</c>@0)
    /// with ammo remaining, advance <see cref="MissileLock"/> one chunk toward
    /// <see cref="CommandTarget"/> via <see cref="UpdateMissileLock"/>. This is
    /// what makes the server's lock authoritative (native builds lock in the
    /// magazine's own per-tick <c>Update</c>, magazineigc.cpp:104-176) — both
    /// the held-bit OnUpdate path and the wire <c>FIRE_MISSILE(70)</c> handler
    /// fire against the lock built here. No launcher / no ammo / unknown
    /// missile type ⇒ nothing to do.
    /// </summary>
    private void UpdateMountedMissileLock(float dt)
    {
        if (Mission is not { } mission) return;
        var launcher = _parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
        if (launcher is not { Type.Type: DataPartTypeIGC.MissileLauncher ml }) return;
        if (launcher.Amount <= 0) return;
        if (!mission.Core.MissileTypes.TryGetValue(ml.ExpendableTypeId, out var missileType)) return;
        UpdateMissileLock(missileType, dt);
    }

    /// <summary>
    /// P226 — attempt to fire the mounted missile launcher (<c>ET_Magazine</c>@0).
    /// Public so the server's inbound <c>FM_CS_FIRE_MISSILE(70)</c> handler can call
    /// it directly (the same direct-callable precedent as <see cref="FireMissile"/> /
    /// <see cref="UpdateMissileLock"/>). Shared by the two real launch triggers:
    /// <see cref="OnUpdate"/>'s held-bit
    /// leg (missileFireIGC, the magazineigc.cpp:111 NPC/local auto-fire) and the
    /// server's inbound <c>FM_CS_FIRE_MISSILE(70)</c> handler
    /// (FedSrv.CPP:5413-5507, the player path). Gates, in order: a mounted
    /// <see cref="DataPartTypeIGC.MissileLauncher"/>, ammo remaining, and lock
    /// sufficiency — a <c>MaxLock == 0</c> type needs none
    /// (<see cref="UpdateMissileLock"/>'s own gate 5 already snaps it to
    /// <c>1f</c>), otherwise the lock must have reached full (<c>&gt;= 1f</c>),
    /// the same threshold that method enforces, not a new one. On a successful
    /// launch the lock is consumed (reset to <c>0</c>, native's
    /// <c>SetLock(0.0f)</c> after fire, FedSrv.CPP:5507) so it must rebuild
    /// before the next shot — the natural fire-rate limiter — and the launcher's
    /// <c>Amount</c> decrements. Returns whether a missile was actually created.
    /// </summary>
    public bool TryFireMissile(double now)
    {
        if (Mission is not { } mission) return false;
        var launcher = _parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
        if (launcher is not { Type.Type: DataPartTypeIGC.MissileLauncher ml }) return false;
        if (launcher.Amount <= 0) return false;
        if (!mission.Core.MissileTypes.TryGetValue(ml.ExpendableTypeId, out var missileType)) return false;

        bool lockReady = missileType.MaxLock == 0f || MissileLock >= 1f;
        if (!lockReady) return false;

        FireMissile(missileType, now, CommandTarget, MissileLock);
        MissileLock = 0f;                                              // lock consumed (FedSrv.CPP:5507)
        SetPartAmount(launcher, (short)(launcher.Amount - 1));         // ammo decrement (FedSrv.CPP:5744-5745)
        return true;
    }

    /// <summary>
    /// Deploy a mine directly at mission time <paramref name="now"/>, creating a
    /// real <see cref="Mine"/> model in the ship's sector at the ship's
    /// <b>current</b> position (<c>Position</c>). A mine has no velocity and no
    /// heading — unlike <see cref="FireMissile"/> there is no muzzle offset, no
    /// forward vector and no inherited velocity, exactly the native
    /// <c>CmineIGC</c> (<c>SetMass(0.0f)</c> + <c>c_mtStatic</c>,
    /// <c>src/Igc/mineigc.cpp:103-104,116</c>; <c>DataMineBase</c> carries only
    /// a position <c>p0</c>, <c>src/Igc/igc.h:2549-2554</c>) — so the mine sits
    /// where it is placed until it expires.
    /// </summary>
    /// <remarks>
    /// <b>Directly-callable only</b> — this method exists to be called directly
    /// (by a test now, by a future dispatch path later). It is <em>not</em> wired
    /// into live <c>ControlData</c>/<c>StateM</c> dispatch, the same explicit
    /// deferral P90 established for <see cref="FireMissile"/>: there is no
    /// \"deploy mine\" control-input bit in <see cref="ShipControlState"/> today.
    /// Nor does it consult a mounted dispenser: like
    /// <see cref="DataPartTypeIGC.EquipmentType"/>'s situation for missile
    /// launchers, there is <em>still</em> no <c>mountable</c> mine-dispenser
    /// equipment subtype — the <c>Dispenser(3)</c> enum member
    /// (<c>DataPartTypeIGC.cs:14</c>) is not a mountable launcher, and the
    /// actual dispenser/magazine/chaff launcher records are compact 24-byte
    /// <c>DataLauncherTypeIGC</c> records (whose doc explicitly names
    /// <c>magazine</c>/<c>dispenser</c>/<c>chaff</c> as the expendables they
    /// cover, <c>DataLauncherTypeIGC.cs:16-18</c>) that the <c>Ship.Parts</c>/
    /// <c>MountPart</c> mounting system cannot represent (no <c>Launcher</c> case
    /// in <c>EquipmentType</c>). So the caller supplies the mine type directly —
    /// the identical resolution <see cref="FireMissile"/> documents for missiles
    /// (<c>Sim/Ship.cs:390-401</c>).
    /// </remarks>
    public Mine DeployMine(DataMineTypeIGC mineType, double now)
    {
        var sector = Sector
            ?? throw new InvalidOperationException("a placed ship must be in a sector before it can deploy a mine");
        var mission = Mission
            ?? throw new InvalidOperationException("cannot deploy a mine on a ship that has not been placed (no mission/core/site)");

        // No offset, no inherited velocity — the mine sits exactly at the ship's
        // current position (DataMineBase.p0 is a lone position, igc.h:2549-2554).
        var mine = new Mine(mission.NextObjectId(), sector, mineType, Position, now, this);
        sector.Add(mine);

        // P94: raise the host seam so the server can broadcast the real
        // FM_S_EXPORT(26)/OT_mine about this fresh mine (a no-op default for a
        // headless host). Follows FireMissile's use of Mission?.Site (Ship.cs:421)
        // — the authoritative mission's site.
        mission.Site.MineCreatedEvent(mine);
        return mine;
    }

    /// <summary>
    /// Deploy a probe at this ship's current position. The probe inherits the
    /// deploying ship's <see cref="SideId"/> (the deliberate improvement over
    /// <see cref="Mine"/>'s P94 construction, which took no side) and sits
    /// exactly where the ship is, no target — matching the wire's own no-target
    /// case (<c>ExportProbe.FromProbe</c> leaves <c>otTarget</c>/<c>oidTarget</c>
    /// at NA/-1, as <c>CprobeIGC::Export</c> does for a targetless probe,
    /// <c>probeigc.cpp:558-562</c>). A probe has no velocity/forward: it is
    /// <c>c_mtStatic</c> with <c>SetMass(0.0f)</c>
    /// (<c>probeigc.cpp:115,142</c>), so no inherited motion is carried over —
    /// the identical <see cref="DeployMine"/> simplification
    /// (<c>DataMineBase.p0</c> is a lone position, <c>igc.h:2549-2554</c>;
    /// <c>DataProbeBase</c> likewise, <c>igc.h:2598-2604</c>).
    /// </summary>
    /// <remarks>
    /// <b>Directly-callable only</b> — this method exists to be called directly
    /// (by a test now, by a future dispatch path later). It is <em>not</em>
    /// wired into live <c>ControlData</c>/<c>StateM</c> dispatch, the same
    /// explicit deferral every prior deploy/fire method in this arc makes
    /// (<c>FireMissile</c>, <c>DeployMine</c>): there is no &quot;deploy
    /// probe&quot; control-input bit in <see cref="ShipControlState"/> today.
    /// As with <see cref="DeployMine"/> and missiles, the caller supplies the
    /// probe type directly — there is no mountable probe-dispenser equipment
    /// subtype (see <c>DeployMine</c>'s remark about <c>DataLauncherTypeIGC</c>
    /// covering <c>magazine</c>/<c>dispenser</c>/<c>chaff</c>), so no dispenser
    /// is consulted.
    /// </remarks>
    public Probe DeployProbe(DataProbeTypeIGC probeType, double now)
    {
        var sector = Sector
            ?? throw new InvalidOperationException("a placed ship must be in a sector before it can deploy a probe");
        var mission = Mission
            ?? throw new InvalidOperationException("cannot deploy a probe on a ship that has not been placed (no mission/core/site)");

        // No offset, no inherited velocity — the probe sits exactly at the
        // ship's current position (DataProbeBase.p0 is a lone position,
        // igc.h:2598-2604). Side carried from the deploying ship's own side
        // (mirroring DataProbeExport.sideID), the deliberate day-one improvement
        // over P94's side-less Mine.
        var probe = new Probe(mission.NextObjectId(), sector, probeType, Position, now, SideId, this);
        sector.Add(probe);

        // Raise the host seam so the server can broadcast the real
        // FM_S_EXPORT(26)/OT_probe about this fresh probe (a no-op default for a
        // headless host). Follows DeployMine's use of Mission?.Site
        // (Ship.cs:472) — the authoritative mission's site.
        mission.Site.ProbeCreatedEvent(probe);
        return probe;
    }

    /// <summary>
    /// P182 — deploy a chaff decoy at this ship's current position, then run
    /// the native missile-retarget ("spoof") loop against every missile in this
    /// ship's cluster whose <see cref="Missile.Target"/> is <em>this</em> ship.
    /// Returns the new <see cref="Chaff"/> object plus the list of missiles that
    /// were successfully spoofed onto it, so the caller (a future
    /// <c>FIRE_EXPENDABLE</c> dispatch) can raise the right broadcasts. The chaff
    /// coasts at the ship's <b>plain</b> <see cref="Velocity"/> and is dropped at
    /// the ship's <b>plain</b> <see cref="Position"/> — deliberate simplifications,
    /// stated rather than silent: native offsets the drop by a per-hull
    /// <c>ChaffPosition</c> (<c>FedSrv.CPP:5680</c>: <c>p0 = myPosition +
    /// GetBaseHullType()->GetChaffPosition() * myOrientation</c>) and kicks the
    /// velocity up by <c>GetUp()·5</c> (<c>FedSrv.CPP:5681</c>); neither field nor
    /// the <c>Up</c> decomposition is decoded/modeled in this port (the singleplayer
    /// branch of native itself drops at plain position, <c>clintlib.cpp:3276</c>), so
    /// the port uses the ship's own position and velocity. <b>Directly-callable
    /// only</b> — it exists to be called directly (by a test now, by the server
    /// dispatch later); it is <em>not</em> wired into live
    /// <c>ControlData</c>/<c>StateM</c> dispatch, the same Layer-1 deferral every
    /// prior fire/deploy method in this arc makes (<c>FireMissile</c>,
    /// <c>DeployMine</c>, <c>DeployProbe</c>).
    /// </summary>
    /// <remarks>
    /// The retarget resolution is the port of the <c>OT_chaffType</c> branch of the
    /// native <c>FM_C_FIRE_EXPENDABLE</c> handler (<c>FedSrv.CPP:5672-5711</c>). For
    /// each missile in the cluster whose target is this ship:
    /// <code>
    ///   chaff   = chaffType.ChaffStrength
    ///   missile = m.MissileType.ChaffResistance
    ///   cm = chaff * missile
    ///   f  = (chaff &gt; missile) ? (cm − 0.5·missile·missile) : (0.5·chaff·chaff)
    ///   spoofed = random(0, cm) &lt;= f        // FedSrv.CPP:5688-5707
    /// </code>
    /// ("equivalent to <c>random(0, chaff) &gt; random(0, missile)</c>",
    /// <c>FedSrv.CPP:5690</c>). On success the missile's <see cref="Missile.Target"/>
    /// is reassigned to the fresh chaff (the P179 homing field), and the missile is
    /// added to the returned <see cref="DeployChaffResult.Spoofed"/> list so the
    /// caller can broadcast the <c>FM_S_MISSILE_SPOOFED(110)</c>. A missile whose
    /// <c>ChaffResistance</c> is <c>0</c> always spoofs (cm = 0 ⇒
    /// <c>random(0,0)=0 &lt;= f</c>), which is how the tests force a deterministic
    /// outcome; <paramref name="rng"/> (default <see cref="Random.Shared"/>) is the
    /// injectable uniform source for the roll, mirroring <see cref="Weapon"/>'s
    /// injectable dispersion RNG (<c>Sim/Weapon.cs:52,72,378</c>). The spoof does not
    /// re-derive an orientation or accelerate the chaff; and per the task's scope
    /// boundary, a chaff object that later expires or is left behind needs no
    /// special-casing here — <see cref="Missile.OnUpdate"/>'s existing
    /// per-tick <c>!_sector.Models.Contains(t)</c> target-drop (P179,
    /// <c>Missile.cs:205-208</c>) already handles a chaff target that leaves the
    /// cluster.
    /// </remarks>
    public DeployChaffResult DeployChaff(DataChaffTypeIGC chaffType, double now, Random? rng = null)
    {
        var sector = Sector
            ?? throw new InvalidOperationException("a placed ship must be in a sector before it can deploy chaff");
        var mission = Mission
            ?? throw new InvalidOperationException("cannot deploy chaff on a ship that has not been placed (no mission/core/site)");

        var chaff = new Chaff(mission.NextObjectId(), sector, chaffType, Position, Velocity, now, this);
        sector.Add(chaff);

        var r = rng ?? Random.Shared;
        var spoofed = new List<Missile>();
        foreach (var m in sector.Models.OfType<Missile>())
        {
            if (!ReferenceEquals(m.Target, this)) continue;   // only missiles aimed at ME get confused (FedSrv.CPP:5697-5698)
            float chaffStrength = chaffType.ChaffStrength;
            float resistance = m.MissileType.ChaffResistance;
            float cm = chaffStrength * resistance;
            float f = chaffStrength > resistance
                ? cm - 0.5f * resistance * resistance
                : 0.5f * chaffStrength * chaffStrength;
            float roll = (float)(r.NextDouble() * cm);
            if (roll <= f)
            {
                m.Target = chaff;          // pmissile->SetTarget(c) (FedSrv.CPP:5705)
                spoofed.Add(m);
            }
        }

        return new DeployChaffResult(chaff, spoofed);
    }

    /// <summary>
    /// P182 — the outcome of <see cref="DeployChaff"/>: the freshly-deployed
    /// <see cref="Chaff"/> model plus the ordered list of missiles whose target
    /// was reassigned onto it (empty when nothing was spoofed). The caller builds
    /// the <c>FM_S_CREATE_CHAFF(109)</c> broadcast from <see cref="Chaff"/> and one
    /// <c>FM_S_MISSILE_SPOOFED(110)</c> per <see cref="Spoofed"/> entry.
    /// </summary>
    public sealed record DeployChaffResult(Chaff Chaff, IReadOnlyList<Missile> Spoofed);

    /// <summary>The three current angular rates (yaw, pitch, roll), rad/s. Indexed by <see cref="ControlData"/> axis constants.</summary>
    public IReadOnlyList<float> TurnRates => _turnRates;

    /// <summary>
    /// <c>CshipIGC::SetCurrentTurnRate(axis, rate)</c> — set one current
    /// turn rate outright (rad/s). Added for P107's eject: the native eject
    /// branch spins the fresh lifepod about its roll axis
    /// (<c>pship-&gt;SetCurrentTurnRate(c_axisRoll, pi*2)</c>,
    /// <c>FedSrv.CPP:12008</c> — only <b>roll</b> is touched; yaw/pitch keep
    /// their pre-eject values, exactly matching the native which sets no other
    /// axis). <c>axis</c> is a <see cref="ControlData"/> axis constant (0=yaw,
    /// 1=pitch, 2=roll).
    /// </summary>
    public void SetTurnRate(int axis, float rate) => _turnRates[axis] = rate;

    /// <summary>Remove every mounted part (functional weapons and inert alike),
    /// so a subsequently applied <c>BUY_LOADOUT</c> replaces rather than
    /// appends to the previous configuration — the native
    /// <c>ProcessShipLoadout</c> rebuilds the ship's mounts wholesale.</summary>
    public void ClearLoadout()
    {
        _weapons.Clear();
        _parts.Clear();
        Array.Clear(_cargo);   // P192: a rebuilt loadout empties the cargo hold too
        // Clearing the loadout removes any mounted shield, so the charge
        // fraction returns to 0 (no shield mounted ⇒ no shield capacity), the
        // same invariant a fresh ship has.
        _shieldFraction = 0.0f;

        // P173: raise the host seam so the server can broadcast a real
        // FM_S_LOADOUT_CHANGE(96). Exactly one Removed raise for the whole
        // clear (mirrors native ProcessShipLoadout triggering one
        // QueueLoadoutChange, not one event per part); NULL part.
        Mission?.Site.LoadoutChangeEvent(this, null!, LoadoutChange.Removed);
    }


    /// <summary>
    /// <c>CshipIGC::ReceiveDamage</c> (<c>shipIGC.cpp:1212-1350</c>): apply
    /// <paramref name="amount"/> of <paramref name="type"/> damage to this
    /// ship's hull at mission time <paramref name="now"/>. The reference
    /// signature is preserved so the projectile's
    /// <c>HandleCollision</c> (<c>projectileIGC.cpp:185-190</c>) maps 1:1.
    /// Returns a <see cref="DamageResult"/> (a projectile checks
    /// <c>dr &lt;= c_drHullDamage</c> to decide whether impact visibly damaged
    /// the hull, <c>projectileIGC.cpp:193</c>).
    /// </summary>
    /// <remarks>
    /// Faithful to the C++ within this slice's stated scope, with every
    /// deviation explicit:
    /// <list type="bullet">
    /// <item><b>Friendly-fire gate implemented.</b> The reference returns
    /// <c>c_drNoDamage</c> when the launcher is friendly and friendly-fire is
    /// off (<c>shipIGC.cpp:1222-1228</c>). Our ships now carry an optional
    /// <see cref="SideId"/>, and the sector collision pass enforces the gate:
    /// a projectile never damages another ship whose <see cref="SideId"/>
    /// equals the launcher's (<c>Sector.ResolveCombatCollisions</c>,
    /// <c>src/Allegiance.Core/Model/Sector.cs:190-195</c>). Friendly fire is
    /// always off — there is no toggle in this codebase to turn it on. The
    /// self-launcher exclusion (a ship cannot damage itself) remains, handled
    /// separately just above (<c>Sector.cs:189</c>); both sit at the same
    /// pass, and this method itself is unchanged — a same-side hit simply
    /// never reaches it.</item>
    /// <item><b>Shields ported (P87).</b> A mounted <c>Shield</c> part
    /// (<c>EquipmentType.Shield</c>) now participates in damage absorption:
    /// <c>leakage = ApplyShieldDamage(...)</c> (ported <c>CshieldIGC::ApplyDamage</c>,
    /// <c>shieldIGC.cpp:140-195</c>), otherwise the no-shield branch
    /// (<c>leakage = amount</c>, <c>shipIGC.cpp:1287-1291</c>) is unchanged and
    /// still routes all damage to the hull. No shield regeneration (out of this
    /// slice's scope): a shield that has absorbed stays down until re-armed by a
    /// fresh <see cref=\"MountPart\"/>. See <c>SHIELD_DAMAGE_P87.md</c>.</item>
    /// <item><b>No damage track.</b> The bookkeeping bucket tally
    /// (<c>common.cpp:2592</c>) only decides <em>who</em> gets kill credit at
    /// death (<c>shipIGC.cpp:1320-1332</c>); with a single identifiable launcher
    /// and no need to split credit, it is omitted (README).</item>
    /// <item><b>Kill Bonus multiplier applied (P278).</b> The launcher ship's
    /// <see cref="ExperienceMultiplier"/> scales the amount ONCE before the
    /// repair/damage branch — native applies the "experience modifier (kb)"
    /// earlier so it reaches repairs too (<c>shipIGC.cpp:1238-1242</c>, the
    /// <c>Student 8/3/2022 ... to apply to repairs</c> placement). Both weapon
    /// damage (positive) and nanite repair (negative) output scale by it.</item>
    /// <item><b>Repair path retained structurally.</b> A negative <paramref name="amount"/>
    /// (repair) restores hull fraction capped at 1.0, per
    /// <c>shipIGC.cpp:1244-1267</c> minus the repair-stat awarding; no repair
    /// source exists yet, so it is dead code in practice.</item>
    /// </list>
    /// The armor factor <c>dtmArmor</c> is <b>not</b> hardcoded: it is the
    /// decoded damage matrix <c>DamageConstants[type][hull.DefenseType]</c>
    /// from the mission's <see cref="DataConstantsIGC"/> (the C++
    /// <c>GetDamageConstant(type, defenseType)</c>, <c>shipIGC.cpp:1233</c>).
    /// </remarks>
    public DamageResult ReceiveDamage(
        byte type,
        float amount,
        double now,
        Vector3 position1,
        Vector3 position2,
        Model? launcher)
    {
        float maxHP = _hull.HitPoints;
        float dtmArmor = DamageConstant(type);

        DamageResult dr;
        float leakage;

        // P278 — native shipIGC.cpp:1238-1242: the LAUNCHER's Kill Bonus
        // (experience) multiplier scales the round's amount, applied ONCE
        // before the repair/damage branch so it reaches BOTH weapon damage
        // (positive) and nanite repair (negative) output. A non-ship launcher
        // (missile blast, environmental) applies no multiplier, exactly native.
        if (launcher is Ship ls)
            amount *= ls.ExperienceMultiplier;

        if (amount < 0.0f)
        {
            // Repair path (shipIGC.cpp:1244-1267), minus the ThingSite noise.
            // Restores hull fraction, clipped at 1.0 (can't over-repair).
            _fraction -= amount * dtmArmor / maxHP;

            // The fraction this round actually restored — the P299 credit basis.
            // Native shrinks `amount` to "the amount that had effect for stat"
            // when over-repairing (shipIGC.cpp:1250-1251) so the credited stat
            // matches the real repair; we derive the same clipped value directly
            // from the fraction delta instead of rewriting `amount`.
            float restored = -amount * dtmArmor / maxHP;
            if (_fraction > 1.0f)
            {
                restored -= _fraction - 1.0f;   // drop the over-repair tail
                _fraction = 1.0f;
            }
            leakage = 0.0f;
            dr = DamageResult.NoDamage;

            // P299 — award the launcher's nanite repair credit (the port of
            // shipIGC.cpp:1257-1266, the "points" the 2x bonus doubles). Native
            // gates on OT_ship AND friendly-or-allied (pside == launcher side ||
            // AlliedSides); this port has no alliance/AlliedSides concept, so the
            // allied leg collapses to same-side (the established P107/P112/P115/
            // P203 precedent). Double the credit when the TARGET's base hull type
            // carries c_habmThreatToStation — "double points for nanning a
            // bomber/htt" (shipIGC.cpp:1262-1263). The hull-restore amount above
            // is NOT doubled; only this launcher-side credit is.
            if (launcher is Ship friendlyLauncher
                && SideId is { } mySide && mySide == friendlyLauncher.SideId)
            {
                float credit = restored;
                if (HullType.HasCapability(DataHullTypeIGC.HabmThreatToStation))
                    credit *= 2.0f;
                friendlyLauncher.AddRepair(credit);
            }
        }
        else
        {
            dr = DamageResult.ShieldDamage;

            // m_damageTrack->ApplyDamage(...) omitted (credit only; see remarks).

            // Shields: when a real Shield part is mounted, the ported
            // CshieldIGC::ApplyDamage (shieldIGC.cpp:140-195) decides how much
            // leaks to the hull; otherwise the no-shield branch (shipIGC.cpp:1287-1291)
            // routes ALL damage through — the ship's existing pre-shield
            // behavior, unchanged.
            var shieldPart = _parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Shield);
            leakage = shieldPart is not null && shieldPart.Type.Type is DataPartTypeIGC.Shield shieldType
                ? ApplyShieldDamage(type, amount, shieldType.MaxStrength, shieldType.DefenseType)
                : amount;

            float oldFraction = _fraction;
            if (leakage > 0.0f)
            {
                leakage *= dtmArmor;
                _fraction -= leakage / maxHP;
                dr = DamageResult.HullDamage;
            }

            if (_fraction > 0.0f)
            {
                Mission?.Site.DamageShipEvent(now, this, launcher, type, amount, leakage, position1, position2);
            }
            else
            {
                _fraction = 0.0f;
                if (oldFraction > 0.0f)   // Only send the death message once (shipIGC.cpp:1317)
                {
                    Mission?.Site.KillShipEvent(now, this, launcher, amount, position1, position2);
                    dr = DamageResult.Killed;
                }
            }
        }

        return dr;
    }

    /// <summary>
    /// <c>CstationIGC::RepairAndRefuel</c> (<c>stationIGC.cpp:449-489</c>) — the
    /// instant full-restore a ship gets when it <b>launches</b> from a station,
    /// ported faithfully. Native calls it in <c>FedSrvSiteBase::ChangeStation</c>'s
    /// undock branch (<c>FedSrv.CPP:13189-13191</c>): <c>pstationNew == NULL</c>
    /// i.e. at LAUNCH, not the moment of docking — so a docked-but-not-yet-
    /// relaunched ship keeps whatever damage it came in with, and the restore is
    /// complete the instant it flies again. It is <b>instant</b> by nature (a
    /// flat set-to-full, not a gradual repair-over-time).
    /// <para>Steps, exactly matching native, with the port's honest deviations
    /// stated:</para>
    /// <list type="number">
    /// <item><b>Fully mount every part</b> — <c>SetMountedFraction(1.0f)</c> on
    /// each (native <c>stationIGC.cpp:453-456</c>). The wire <c>fractionMounted</c>
    /// byte is rewritten to 255 (= <c>BytePercentage</c> 1.0, <c>_Utility/
    /// Utility.h:1311-1327</c>), the same list-entry-replacement idiom
    /// <see cref="SetPartAmount"/> uses.</item>
    /// <item><b>Hull → 1.0</b> — <c>SetFraction(1.0f)</c>
    /// (<c>stationIGC.cpp:457-460</c>), but <b>only if the station type carries
    /// the <c>c_sabmRepair</c> capability</b> (<see cref="DataStationTypeIGC.SabmRepair"/>).</item>
    /// <item><b>Shield → 1.0</b> — unconditional, if a shield is mounted
    /// (<c>stationIGC.cpp:461-465</c>).</item>
    /// <item><b>Afterburner deactivated</b> (<c>stationIGC.cpp:466-469</c>) — a
    /// no-op here: afterburners are inert bookkeeping in this port (see
    /// <see cref="MountedPart"/>), with no activation state to clear.</item>
    /// <item><b>Ammo + fuel → max</b> (<c>stationIGC.cpp:470-485</c>), but only
    /// if the station type carries the <c>c_sabmReload</c> capability
    /// (<see cref="DataStationTypeIGC.SabmReload"/>). <see cref="Ammo"/> →
    /// <see cref="DataHullTypeIGC.MaxAmmo"/>; every mounted pack's
    /// <c>Amount</c> → maxAmmo (ammo pack) / maxFuel (fuel pack), per
    /// <c>ppack-&gt;SetAmount(...)</c>. Fuel is a documented no-op in this port:
    /// there is no runtime fuel pool (only <see cref="Ammo"/>/<see cref="Energy"/>;
    /// <see cref="DataHullTypeIGC.MaxFuel"/> is static-core data, never tracked
    /// at runtime — see the <see cref=\"MountPart\"/>/reload notes), so the fuel
    /// half of the reload reduces to the pack-amount bookkeeping only.</item>
    /// <item><b>Energy → max</b> — <c>SetEnergy(pht-&gt;GetMaxEnergy())</c>,
    /// unconditional (<c>stationIGC.cpp:487-488</c>).</item>
    /// </list>
    /// Invoked by the server's shared undock core (<c>Server.UndockShipState</c>)
    /// with the <b>docked</b> station's type resolved from the mission core —
    /// the station the ship is launching from, exactly native's
    /// <c>pstationOld-&gt;RepairAndRefuel(pship)</c>.
    /// </summary>
    public void RepairAndRefuel(DataStationTypeIGC stationType)
    {
        // 1. Fully mount every part (SetMountedFraction(1.0f), stationIGC.cpp:453-456).
        const byte fullyMounted = byte.MaxValue;      // BytePercentage 1.0 → 255
        for (int i = 0; i < _parts.Count; i++)
        {
            var p = _parts[i];
            if (p.FractionMounted != fullyMounted)
                _parts[i] = WithFraction(p, fullyMounted);
        }

        // 2. Hull → 1.0, gated on the station's c_sabmRepair capability.
        if (stationType.HasCapability(DataStationTypeIGC.SabmRepair))
            _fraction = 1.0f;

        // 3. Shield → 1.0, unconditional if a shield is mounted.
        if (_parts.Any(p => p.EquipmentType == EquipmentType.Shield))
            _shieldFraction = 1.0f;

        // 4. Afterburner deactivated — no-op (inert bookkeeping, see class doc).

        // 5. Ammo + fuel → max, gated on the station's c_sabmReload capability.
        if (stationType.HasCapability(DataStationTypeIGC.SabmReload))
        {
            short maxAmmo = _hull.MaxAmmo;
            short maxFuel = (short)_hull.MaxFuel;      // short(GetMaxFuel()), stationIGC.cpp:473

            Ammo = maxAmmo;                            // SetAmmo(maxAmmo)
            // SetFuel(maxFuel): no runtime fuel pool in this port (documented no-op).

            // ppack->SetAmount(packType == c_packAmmo ? maxAmmo : maxFuel) on
            // every mounted pack (stationIGC.cpp:476-483).
            for (int i = 0; i < _parts.Count; i++)
            {
                var p = _parts[i];
                if (p.Type.Type is DataPartTypeIGC.Pack pk)
                {
                    short full = pk.PackType == cPackAmmo ? maxAmmo : maxFuel;
                    if (p.Amount != full) _parts[i] = WithAmount(p, full);
                }
            }
        }

        // 6. Energy → max, unconditional (SetEnergy(GetMaxEnergy()), stationIGC.cpp:487-488).
        Energy = _hull.MaxEnergy;
    }

    /// (<c>shipIGC.cpp:1233</c>): the decoded damage matrix
    /// <c>DamageConstants[dmgid][defid]</c>. Returns 1.0 (neutral) when the
    /// ship has no mission (unplaced) or the indexes lie outside the 20×20
    /// matrix, so a stray damage type can never amplify/dilute a hit into
    /// nonsense. Not a hardcoded constant — it is data-driven from
    /// <see cref="DataConstantsIGC"/>.
    /// </summary>
    private float DamageConstant(byte type, byte defenseType)
    {
        var core = Mission?.Core;
        if (core is null) return 1.0f;
        var dm = core.Constants.DamageConstants;
        int dmg = type, def = defenseType;
        if (dmg < 0 || dmg >= DataConstantsIGC.DmgidMax || def < 0 || def >= DataConstantsIGC.DefidMax)
            return 1.0f;
        return MathF.Max(dm[dmg][def], 0.0f);
    }

    /// <summary>The hull's own <see cref="DamageConstant(byte,byte)"/> for the
    /// ship's hull defense type — the C++ <c>GetDamageConstant(type, hull
    /// defenseType)</c> (<c>shipIGC.cpp:1233</c>) computed once per hit.</summary>
    private float DamageConstant(byte type) => DamageConstant(type, _hull.DefenseType);

    /// <summary>Port of <c>CshieldIGC::ApplyDamage</c> (<c>shieldIGC.cpp:140-195</c>),
    /// the shield's own absorption math. <paramref name="amount"/> is the raw,
    /// un-multiplied incoming damage (the caller passes its unmultiplied
    /// <c>amount</c>, <c>shipIGC.cpp:1285</c>); inside, <c>ApplyDamage</c>
    /// multiplies by the <b>shield's</b> damage constant <c>dtm</c>
    /// (<c>shieldIGC.cpp:145-146</c>), does its absorption in armor-multiplied
    /// units, then divides back out by <c>dtm</c> (<c>shieldIGC.cpp:163,182</c>)
    /// so the caller's own <c>leakage *= dtmArmor</c> (<c>shipIGC.cpp:1297</c>)
    /// re-applies the <b>hull's</b> constant exactly once. That multiplication
    /// order is the crux: the shield's <c>dtm</c> cancels inside, the hull's
    /// <c>dtmArmor</c> stays outside. The side-level global attribute
    /// multipliers (<c>c_gaMaxShieldShip</c>, <c>shieldIGC.h:112-117</c>; the
    /// <c>c_gaShieldRegenerationShip</c> term in <c>GetRegeneration</c>,
    /// <c>shieldIGC.h:106-111</c>) are not ported in this codebase and are
    /// treated as 1.0 here (same honest-simplification style as the payday /
    /// income constants).</summary>
    private float ApplyShieldDamage(byte type, float amount, float maxStrength, byte defenseType)
    {
        float dtm = DamageConstant(type, defenseType);   // shieldIGC.cpp:145 (the SHIELD's defense type, not the hull's)
        amount *= dtm;                                    // shieldIGC.cpp:146

        float leakage;
        if (amount > 0.0f)
        {
            float absorbed = amount / maxStrength;        // shieldIGC.cpp:155-156
            const float down = 0.1f;                       // c_fcidDownedShield — mission-init override of the core's DataConstantsIGC slot (missionigc.cpp:2690,2745); hardcoded literal (see report, P87).

            if (_shieldFraction <= down)                   // shieldIGC.cpp:160-164
            {
                // Previously knocked down (fraction ≤ 0.1): the shield absorbs
                // nothing, all damage leaks. amount/dt un-multiplies the armor
                // the shield applied internally, returning the original raw
                // amount — so the caller re-applies the hull's dtmArmor exactly
                // once, byte-for-byte the same as the no-shield path. This is
                // the "as if unshielded" behaviour tests must assert.
                leakage = amount / dtm;
            }
            else
            {
                float useful = _shieldFraction - down;     // shieldIGC.cpp:167
                if (absorbed <= useful)                    // shieldIGC.cpp:173-177
                    leakage = 0.0f;                        // shield is strong enough to stop the entire hit
                else                                       // shieldIGC.cpp:178-187
                    leakage = (absorbed - useful) * maxStrength / dtm;
            }

            // CshieldIGC::SetFraction (shieldIGC.h:126-147): subtract the
            // absorbed fraction and clamp to [0,1]. (The hit-shape toggle —
            // SetShape ellipse↔convex — is native collision/rendering
            // bookkeeping with no analogue in this port; skipped.)
            _shieldFraction = Math.Clamp(_shieldFraction - absorbed, 0f, 1f);
        }
        else
            leakage = 0.0f;                                // shieldIGC.cpp:191-192

        return leakage;
    }


    /// <summary>Copies the turn rates into <paramref name="dest"/> and returns it (avoids exposing the backing array for mutation at a distance).</summary>
    public float[] GetTurnRates(float[] dest)
    {
        Array.Copy(_turnRates, dest, 3);
        return dest;
    }

    /// <summary>
    /// Advance the ship's kinematics from <paramref name="timeStart"/> to
    /// <paramref name="timeStop"/> given the current controls — the port of
    /// <c>CshipIGC::ExecuteShipMove(timeStart, timeStop, &amp;vel, &amp;ori)</c>
    /// (<c>shipIGC.cpp:2852-3054</c>). Mutates <see cref="Velocity"/> and
    /// <see cref="Orientation"/>. <b>Does not move <see cref="Position"/></b> —
    /// the C++ does not either; integrating <c>Δposition = velocity·ΔT</c> is
    /// the mission/cluster slice.
    /// </summary>
    /// <param name="timeStart">Start instant (seconds).</param>
    /// <param name="timeStop">End instant (seconds); must exceed <paramref name="timeStart"/> (the C++ <c>assert(dT &gt; 0)</c>).</param>
    public void ExecuteMove(double timeStart, double timeStop)
    {
        if (!(timeStop > timeStart))
            throw new ArgumentOutOfRangeException(nameof(timeStop), "ExecuteMove requires timeStop > timeStart");

        float dT = (float)(timeStop - timeStart);
        float thrust = _hull.Thrust;
        float thrust2 = thrust * thrust;
        float thrustToVelocity = dT / Mass;          // Newtons → Δv; shipIGC.cpp:2868

        // ── manoeuvre: ramp turn rates toward the desired, clipped by torque ─
        // (shipIGC.cpp:2887-2910; no ripcord, so this block always runs)
        float l = Controls.Yaw * Controls.Yaw + Controls.Pitch * Controls.Pitch + Controls.Roll * Controls.Roll;
        l = l > 1f ? 1f / MathF.Sqrt(l) : 1f;         // constrain desired rate to a sphere, not a box

        float tm = GetTorqueMultiplier() * thrustToVelocity;
        for (int i = 0; i < 3; i++)
        {
            float desiredRate = Controls.TurnAxis(i) * l * _hull.MaxTurnRates[i];
            float maxDelta = tm * _hull.TurnTorques[i];
            if (desiredRate < _turnRates[i] - maxDelta)
                _turnRates[i] -= maxDelta;
            else if (desiredRate > _turnRates[i] + maxDelta)
                _turnRates[i] += maxDelta;
            else
                _turnRates[i] = desiredRate;
        }

        Orientation.Yaw(_turnRates[ControlData.AxisYaw] * dT);
        Orientation.Pitch(-_turnRates[ControlData.AxisPitch] * dT);
        Orientation.Roll(_turnRates[ControlData.AxisRoll] * dT);
        Orientation.Renormalize();

        var backward = Orientation.GetBackward();
        float speed = Velocity.Length();
        float maxSpeed = _hull.Speed;

        // Drag = thrust required to produce the velocity change caused by drag.
        // Computed with double intermediates exactly as the C++ casts
        // (shipIGC.cpp:2930-2934).
        double f = Math.Exp((-thrust) * (double)thrustToVelocity / maxSpeed);
        var drag = Velocity * (float)((1.0 - f) / thrustToVelocity);

        bool afterF = (StateM & ShipControlState.AfterburnerButton) != 0;

        Vector3 localThrust;
        if ((StateM & ShipControlState.MoveButtonsMask) != 0)
        {
            // Manual control: whichever direction buttons are pressed, at full thrust.
            int x = ((StateM & ShipControlState.LeftButton) != 0 ? -1 : 0)
                  + ((StateM & ShipControlState.RightButton) != 0 ? 1 : 0);
            int y = ((StateM & ShipControlState.DownButton) != 0 ? -1 : 0)
                  + ((StateM & ShipControlState.UpButton) != 0 ? 1 : 0);
            int z = ((StateM & ShipControlState.BackwardButton) != 0 ? 1 : 0)
                  + ((StateM & ShipControlState.ForwardButton) != 0 ? -1 : 0);
            localThrust = (x != 0 || y != 0 || z != 0)
                ? new Vector3(thrust * x, thrust * y, thrust * z)
                : Vector3.Zero;
        }
        else if ((StateM & ShipControlState.CoastButton) != 0 && !afterF)
        {
            // Vector lock: kill lateral velocity (bring drag into ship-local).
            localThrust = Orientation.TimesInverse(drag);
        }
        else
        {
            // Throttle cruise: aim at a target speed and cancel drag.
            float negDesiredSpeed = afterF
                ? maxSpeed * (-1.0f - 0.0f)                          // thrustRatio == 0 (no AB)
                : (-0.5f * (1.0f + Controls.Throttle)) * ((speed > maxSpeed) ? speed : maxSpeed);
            var desiredVelocity = backward * negDesiredSpeed;
            localThrust = Orientation.TimesInverse((desiredVelocity - Velocity) / thrustToVelocity + drag);
        }

        // Clip the engine vector to the engine's available thrust.
        // (shipIGC.cpp:3008-3033)
        float sm = _hull.SideMultiplier;
        float backMult = _hull.BackMultiplier;
        var scaledThrust = new Vector3(
            localThrust.X / sm,
            localThrust.Y / sm,
            localThrust.Z <= 0f ? localThrust.Z : localThrust.Z / backMult);
        float r2 = scaledThrust.LengthSquared();

        Vector3 engineVector;
        if (r2 == 0f)
            engineVector = Vector3.Zero;
        else if (r2 <= thrust2)
            engineVector = localThrust * Orientation;
        else
            engineVector = (localThrust * Orientation) * (thrust / MathF.Sqrt(r2));

        Velocity += thrustToVelocity * (engineVector - drag);
    }

    /// <summary>
    /// <c>CshipIGC::ExecuteTurretMove(timeStart, timeStop, &amp;pOrientation)</c>
    /// (<c>shipIGC.cpp:1618-1702</c>): advance the gunner's facing from
    /// <paramref name="timeStart"/> to <paramref name="timeStop"/> according to
    /// <em>this</em> ship's controls/turn-rates, writing the result into
    /// <paramref name="pOrientation"/> in place (the native signature takes the
    /// orientation by pointer and mutates it — this is a real turret-gunner
    /// move, not a full ship move). The gunner never translates, only turns.
    /// <list type="bullet">
    /// <item>Controls constrained to a sphere (not a box), exactly as
    /// <c>ExecuteShipMove</c> does (<c>shipIGC.cpp:1627-1634</c>).</item>
    /// <item>Slew rate scales with throttle between 7.5° and 75°/s
    /// (<c>c_minRate</c>/<c>c_maxRate</c>, <c>shipIGC.cpp:1640-1643</c>).</item>
    /// <item>Turn rates set outright (not ramped) to
    /// <c>controls·l·slewVelocity</c> (<c>shipIGC.cpp:1645-1646</c>).</item>
    /// <item>The turret-limit clamp block (hardpoint <c>minDot</c>) is
    /// <c>/* commented out */</c> in the native (<c>shipIGC.cpp:1656-1697</c>),
    /// leaving only the re-normalize — reproduced here (the turret limit is
    /// native-cosmetic and its mesh data is absent in this headless port).</item>
    /// </list>
    /// Called from both real sites native has: (1) the
    /// <c>ACTIVE_TURRET_UPDATE(99)</c> snapshot catch-up
    /// (<c>shipIGC.cpp:3333-3338,3378-3383</c>) and (2) the carrier ship's own
    /// per-tick prediction (<c>shipIGC.cpp:316-339</c>) — they share this one
    /// implementation, never duplicating the math.
    /// </summary>
    /// <param name="timeStart">Start instant (seconds).</param>
    /// <param name="timeStop">End instant (seconds); native allows turret time
    /// to run forwards <em>or</em> backwards, so no <c>timeStop &gt; timeStart</c>
    /// assertion here.</param>
    /// <param name="pOrientation">The orientation to integrate in place — the
    /// gunner's current facing on entry, the predicted facing on exit. Mutated
    /// (yaw/pitch/roll applied, then re-orthonormalized).</param>
    public void ExecuteTurretMove(double timeStart, double timeStop, Orientation pOrientation)
    {
        float dT = (float)(timeStop - timeStart);

        // Constrain desired yaw/pitch/roll to a circle, not a box
        // (shipIGC.cpp:1627-1634).
        float l = Controls.Yaw * Controls.Yaw
                + Controls.Pitch * Controls.Pitch
                + Controls.Roll * Controls.Roll;
        l = l > 1f ? 1f / MathF.Sqrt(l) : 1f;

        // Slew velocity scales with throttle (shipIGC.cpp:1640-1643).
        const float cMinRate = 7.5f * MathF.PI / 180.0f;
        const float cMaxRate = 75.0f * MathF.PI / 180.0f;
        float slewVelocity = (cMinRate + 0.5f * (cMaxRate - cMinRate))
                           + (0.5f * (cMaxRate - cMinRate)) * Controls.Throttle;

        for (int i = 0; i < 3; i++)
            _turnRates[i] = Controls.TurnAxis(i) * l * slewVelocity;

        pOrientation.Yaw(_turnRates[ControlData.AxisYaw] * dT);
        pOrientation.Pitch(-_turnRates[ControlData.AxisPitch] * dT);
        pOrientation.Roll(_turnRates[ControlData.AxisRoll] * dT);
        pOrientation.Renormalize();
    }

    /// <summary>
    /// <c>CshipIGC::GetTorqueMultiplier()</c> (<c>shipIGC.h:542-549</c>): 0.50
    /// at rest rising toward 2.50 as speed approaches/exceeds max, using
    /// <c>f = |v|/maxSpeed</c>. Read from the current velocity (at the moment
    /// the ramp runs) exactly as the reference reads <c>GetVelocity()</c>.
    /// </summary>
    private float GetTorqueMultiplier()
    {
        const float c = 0.50f;
        float fraction = Velocity.Length() / _hull.Speed;
        return c + (1.0f - c) * 2.0f * fraction / (fraction + 1.0f);
    }

    /// <summary>P155 — arrival radius (units). Inside this distance from the
    /// autopilot target the ship holds <c>Throttle = -1</c> (kill throttle),
    /// decelerating to settle near the target instead of thrusting onward. A
    /// fixed reasonable margin (well below every spawn-target separation the
    /// P155 tests exercise, and large relative to a station's footprint); it is
    /// a simplified arrival heuristic, <b>not</b> native's closed-form braking
    /// solve — see NPC_AUTOPILOT_P155.md.</summary>
    private const float AutopilotArrivalRadius = 60f;

    /// <summary>P155 — brake-start distance (units). While the ship is farther
    /// than this from the target it commands full forward throttle
    /// (<c>+1</c>); between here and <see cref="AutopilotArrivalRadius"/> the
    /// throttle ramps linearly from <c>+1</c> down to <c>-1</c>, so the ship
    /// begins shedding speed well before arrival rather than slamming the
    /// brakes at the last instant (which would overshoot and oscillate). Chosen
    /// generously relative to typical hull cruise speeds for a smooth settle.
    /// Same accepted-simplification caveat as the arrival radius.</summary>
    private const float AutopilotBrakeStartDistance = 420f;

    /// <summary>P168 — distance (units) from the ship's current position to the
    /// temporary "away" goal point produced when an imminent collision is
    /// detected. Native's <c>Dodge</c> emits raw strafe-thrust buttons; this
    /// port has no strafe physics, so (per the arc's established strategy) the
    /// avoidance vector is turned into a plain world-space goal point fed to the
    /// same <see cref="SteerToward"/> primitive every other goal uses. Chosen
    /// just past <see cref="AutopilotBrakeStartDistance"/> so the escape target
    /// sits at the top of the throttle ramp (full forward once the nose aligns)
    /// — a firm, unambiguous "break away" cue rather than a timid nearby point.
    /// The distance itself is immaterial to correctness (only the <em>direction</em>
    /// away from the threat matters); the value is a convenient magnitude.</summary>
    private const float DodgeGoalDistance = 520f;

    /// <summary>P168 — fallback imminence horizon (seconds) used only when the
    /// hull reports a non-positive acceleration (<c>Thrust / Mass</c> ≤ 0), which
    /// would make native's acceleration-based default threshold degenerate. The
    /// default threshold is native's <c>t² ≤ 4·r/acceleration</c>
    /// (<c>common.cpp:1749-1751</c>); this constant stands in for that formula
    /// only in the degenerate case, so the check can never divide by zero.</summary>
    private const float DodgeFallbackTMax = 3f;

    /// <summary>P155 — turn gain for the proportional face-toward law. The turn
    /// command is the target-relative rotation axis (forward × directionToTarget,
    /// continuous even when the target is astern) expressed in ship-local space,
    /// scaled by this gain and clamped to <c>[−1,1]</c>: a target ~30° off the
    /// nose drives a full turn command, smaller offsets a gentler proportional
    /// one. Aggressiveness is additionally limited for free by the hull's real
    /// <c>TurnTorques</c> (the manoeuvre ramp at <c>Ship.cs:1790-1804</c>).</summary>
    private const float AutopilotTurnGain = 2.0f;

    // P162 — Wingman combat-approach constants. The combat leg of
    // ApplyNpcAutopilot steers toward the enemy's LIVE position through the
    // SAME face+thrust computation as every other goal, but with a different
    // arrival condition: "close enough to fire" (a standoff inside weapon
    // range) rather than "stopped on the point" (AutopilotArrivalRadius).
    //
    //   MaxEngagementStandoff caps how far the Wingman holds from its target
    //     so a very-long-range weapon doesn't park it at extreme range where
    //     a small aim error misses entirely; the effective standoff is
    //     min(0.7 · weaponRange, this), floored at AutopilotArrivalRadius so a
    //     weapon-less Wingman still behaves like a normal arrival.
    //   FireFacingCosine is the CosForward-style dot-product bar (forward·dir
    //     = −local Z, the same metric the P155 turn law already uses) for
    //     "facing the target closely enough to fire".
    private const float WingmanMaxEngagementStandoff = 350f;
    private const float WingmanFireFacingCosine = 0.90f;

    // P169 — native's plain-Wingman shoot skill (Igc/shipIGC.cpp:2359,
    // fShootSkill = 0.75f; only overridden for c_ptCheatPlayer, shipIGC.cpp:
    // 2366-2369, which this port has no analogue for and needs none). Inside
    // the lead solve it SCALES THE TARGET'S VELOCITY DOWN (common.cpp:137:
    // hisVelocity * skill), i.e. a lower skill leads less — a 0.75 Wingman
    // accounts for only 75% of the target's motion, so its shots trail the
    // ideal intercept slightly. Named+cited following the
    // WingmanFireFacingCosine precedent (P162) for a tuned constant.
    private const float WingmanShootSkill = 0.75f;   // shipIGC.cpp:2359

    // P166 — the native c_cidDefend re-evaluation thresholds, ported with their
    // exact native values (Igc/shipIGC.cpp:2373-2407). Distance in world units,
    // speeds in u/s. WingmanDefendAbandonDistance/EvaluateDistance govern when a
    // defending Wingman gives up the chase and returns to its charge;
    // WingmanDefendScanRadius + WingmanDefendSwitchMargin drive the "switch to a
    // closer threat near the charge" search; WingmanDefendAbandonRunawaySpeed is
    // the "just running away" threshold (only −30 belongs to the defend block —
    // the −20/−50 the brief loosely groups with it are the EXCLUDED relative-
    // speed maneuvering of the larger combat block, shipIGC.cpp:2494-2502, not
    // the defend logic, so only −30 is ported here).
    private const float WingmanDefendAbandonDistance = 3500f;       // shipIGC.cpp:2380
    private const float WingmanDefendEvaluateDistance = 2200f;      // shipIGC.cpp:2382
    private const float WingmanDefendScanRadius = 2500f;            // shipIGC.cpp:2383
    private const float WingmanDefendSwitchMargin = 1000f;          // shipIGC.cpp:2385
    private const float WingmanDefendAbandonRunawaySpeed = -30f;    // shipIGC.cpp:2395
    private const float WingmanDefendAcquireRadius = 1500f;         // shipIGC.cpp:2689 (the c_cidDefend plan-slot scan)

    // P171 — native's Wingman relative-speed range maintenance (band 1 of the
    // closing/maneuvering block, Igc/shipIGC.cpp:2492-2512). relativeSpeed is
    // positive when CLOSING (ownDirSpeed − targetDirSpeed, shipIGC.cpp:2479-
    // 2481): beyond +WingmanClosingSpeedThreshold the Wingman backs off
    // (backwardButtonIGC → Throttle −1, :2499-2502); below −threshold it
    // presses in (forwardButtonIGC → Throttle +1, :2494-2498); in between it
    // matches the target's along-forward speed via the graduated throttle
    // formula (:2507-2509). Only this single in-the-heat band is ported
    // (Stage-1 reduction): the approach / long-range / give-up-re-target bands
    // and the afterburner / criticalMovement outputs are explicitly deferred.
    // Named+cited following the WingmanFireFacingCosine precedent (P162).
    private const float WingmanClosingSpeedThreshold = 20f;         // shipIGC.cpp:2494,2499 (±20)

    // P157 — transition-guarded logging for the cross-cluster autopilot leg
    // (<see cref="CrossClusterWarpGoal"/>). ApplyNpcAutopilot runs every tick,
    // so each of these is a one-shot per episode: they log the departure toward
    // a warp and the no-path stand-by exactly once each (and re-arm the other),
    // preventing a 20 Hz log flood while still surfacing every distinct outcome
    // a sustained journey or a permanently-unreachable target produces.
    private bool _npcCrossClusterDepartureLogged;
    private bool _npcCrossClusterNoPathLogged;

    // P172 — NPC ripcord-as-travel (stage 1): the retry latch mirroring native's
    // m_pclusterRequestRipcord / m_timeRequestRipcord (shipIGC.cpp:4153-4172,
    // 2059-2080). When a friendly ripcord anchor is reachable for the current
    // cross-cluster goal but IsSafeToRipcord() fails, the ripcord is deferred
    // (this tick is NOT spent warp-pathing — the ripcord path owns it) and
    // re-attempted after <see cref="RipcordRetryInterval"/> seconds, rather than
    // falling straight through to P157/158 warp-pathing. Latched because
    // ApplyNpcAutopilot re-evaluates every tick; without the latch the unsafe
    // refusal would just re-fire each tick instead of pacing at native's 5 s.
    private Sector? _npcPendingRipcordTarget;
    private double _npcRipcordRetryAt = double.NegativeInfinity;
    private const double RipcordRetryInterval = 5.0;   // shipIGC.cpp:4166, :2067 ("5.0f")
    // Transition guards for one-shot logging (same pattern as the
    // _npcCrossCluster*Logged pair): exactly one line per distinct outcome.
    private bool _npcRipcordInitiatedLogged;
    private bool _npcRipcordUnsafeLogged;

    /// <summary>
    /// P155 — the simplified same-cluster "face and thrust" autopilot: computes
    /// a <see cref="ControlData"/> that steers an <see cref="IsNpc"/> ship
    /// toward <see cref="PlanTarget"/>'s position and assigns it to
    /// <see cref="Controls"/> <em>before</em> <see cref="ExecuteMove"/> reads it
    /// each tick. Real player ships (<see cref="IsNpc"/> false) return early —
    /// their <see cref="Controls"/> stay exclusively wire-driven.
    /// <para>This is a deliberate radical simplification of native
    /// <c>GotoPlan::SetControls</c> (<c>src/Igc/common.cpp:1830-2000+</c>), a
    /// physics-optimal autopilot deriving braking distance from a closed-form
    /// solution of the ship's own drag differential equation, warp-chasing
    /// across clusters, and dodging. The only physics reused here is the port's
    /// existing throttle-cruise branch of <see cref="ExecuteMove"/>
    /// (<c>Ship.cs:1844-1850</c>) — a plain <c>Controls.Throttle</c> scalar
    /// already converts into real drag-cancelling thrust. The yaw/pitch are
    /// simple proportional turn-toward commands whose rate is naturally limited
    /// by the hull's real <c>TurnTorques</c> via the manoeuvre-ramp block
    /// earlier in <c>ExecuteMove</c> (<c>Ship.cs:1790-1804</c>). Cross-cluster
    /// chasing, <c>FindPath</c>, dodge behaviour and warp waypoints are all out
    /// of scope (same-cluster only).</para>
    /// <para><b>Same-cluster gate.</b> Moves only when <see cref="Sector"/>'s id
    /// (its <c>ClusterId</c>, <c>Sector.cs:33</c>) equals the target's
    /// <c>ClusterId</c>; otherwise does nothing (does not guess) — the same
    /// honest gap this session already established for <c>FindTarget</c>'s
    /// <c>c_ttAnyCluster</c> exclusion.</para>
    /// <para><b>Face.</b> The turn command is the target-relative rotation axis
    /// (forward × directionToTarget) expressed in ship-local space with
    /// <c>Orientation.TimesInverse</c>: its up-component drives yaw, its
    /// right-component drives pitch. Unlike a scalar bearing, this cross-product
    /// signal is continuous through the target-astern case (a dead-behind target
    /// gives ~0, breaking the symmetry to a definite turn as the ship drifts)
    /// rather than flipping between full-left/full-right and stalling the nose.
    /// Signs match how <c>ExecuteMove</c> applies the axes (<c>Yaw(+rate·dT)</c>,
    /// <c>Pitch(−rate·dT)</c>, <c>Ship.cs:1806-1808</c>; this port's left-handed
    /// frame, <c>Orientation.cs:16-35</c>).</para>
    /// <para><b>Thrust.</b> A two-zone distance law, not native's closed-form
    /// braking solve: full forward (<c>+1</c>) while farther than
    /// <see cref="AutopilotBrakeStartDistance"/> and well-aligned, then a
    /// distance+alignment-scaled ramp down to <c>-1</c> (native's "kill
    /// throttle" intent near a goal) across to
    /// <see cref="AutopilotArrivalRadius"/>, holding <c>-1</c> within the
    /// arrival radius so the ship decelerates and settles rather than thrashes.
    /// Power is additionally cut while the nose is far off the target, letting a
    /// slow-turning hull tighten its turn instead of orbiting at full speed —
    /// the "turn then burn" simplification of native's velocity-managed
    /// <c>GotoPlan</c>. An accepted simplification — a drone may overshoot or
    /// wiggle slightly near arrival compared to native's exact stop.</para>
    /// </summary>
    private void ApplyNpcAutopilot(double now)
    {
        if (!IsNpc) return;                                   // real player Controls are wire-only (P155 scope)
        if (Sector is not { } sector) return;

        // P168 — Stage 1 universal collision avoidance (native Dodge), a
        // PRE-EMPTING check that runs ahead of every goal-resolution branch for
        // every pilot type (incl. idle), mirroring native's call-site precedence
        // (Dodge fires before the pilot-type per-tick movement logic at
        // common.cpp:1920,1932 / shipIGC.cpp:2334). When an imminent collision
        // is found, the tick's steering is overridden with the temporary away
        // goal and the rest of the chain is skipped; otherwise we fall through
        // to the normal goal resolution unchanged.
        if (TryComputeDodgeGoal(out var dodgeGoal))
        {
            SteerToward(dodgeGoal, AutopilotArrivalRadius, AutopilotBrakeStartDistance);
            return;
        }

        // P156/P161/P162 — resolve the current navigation goal to a single
        // plain world-space position: the station PlanTarget (P151/P150), the
        // asteroid MiningAsteroidTarget (P156), the Layer's LayerAsteroidTarget
        // (P161), or the Wingman's AttackTarget (P162). All four feed the SAME
        // steering computation (<see cref="SteerToward"/>) — no fourth copy of
        // the steering code. The targets are mutually exclusive by construction
        // (a ship is one pilot type, so only the slot for its type is ever
        // populated; PlanTarget wins defensively if both were somehow set).
        Vector3? goal = null;
        float arrivalRadius = AutopilotArrivalRadius;
        float brakeStartDistance = AutopilotBrakeStartDistance;
        // P171 — when the Wingman is ENGAGED (within its engagement standoff)
        // this carries the relative-speed range-maintenance throttle that
        // replaces the fixed standoff-hold THROTTLE; null means no override
        // (the distance ramp below stands, e.g. the approach phase).
        float? rangeThrottle = null;
        if (PlanTarget is { } st && st.Position is { Length: 3 } sp)
        {
            if (sector.Id == st.ClusterId)
                goal = new Vector3(sp[0], sp[1], sp[2]);      // same-cluster gate (Sector.cs:33) — direct flight
            // P172 — when the PlanTarget is cross-cluster, the ripcord pre-check
            // runs first: if a friendly ripcord station is reachable in/around the
            // target cluster, the NPC ripcords (initiated now if safe, deferred &
            // retried in 5 s if not) instead of warp-pathing. Only when no anchor
            // is reachable does it fall through to the existing P157 warp leg.
            else if (!TryRipcordOrDefer(st.ClusterId, now, $"station {st.StationId} '{st.Name}'"))
                goal = CrossClusterWarpGoal(st.ClusterId, $"station {st.StationId} '{st.Name}'");  // P157 — steer toward the first warp toward the target sector
        }
        else if (MiningAsteroidTarget is { } rock && rock.Data.Position is { Length: 3 } rp)
        {
            if (sector.Id == rock.Data.ClusterId)
                goal = new Vector3(rp[0], rp[1], rp[2]);      // same-cluster — direct flight
            else if (!TryRipcordOrDefer(rock.Data.ClusterId, now, $"asteroid {rock.Data.AsteroidId} '{rock.Data.Name}'"))
                goal = CrossClusterWarpGoal(rock.Data.ClusterId, $"asteroid {rock.Data.AsteroidId} '{rock.Data.Name}'");  // P158 — fixed: was steering at a foreign-sector raw position as if local
        }
        // P161 — the Layer default order steers toward its chosen low-contention
        // asteroid through the SAME resolution: same-cluster direct flight, or the
        // identical cross-cluster warp-goal leg when the rock is another sector.
        // No new steering logic — LayerAsteroidTarget is just a third goal source
        // feeding the one shared face+thrust computation below.
        else if (LayerAsteroidTarget is { } lar && lar.Data.Position is { Length: 3 } lap)
        {
            if (sector.Id == lar.Data.ClusterId)
                goal = new Vector3(lap[0], lap[1], lap[2]);      // same-cluster — direct flight
            // P172 — the same ripcord pre-check ahead of the Layer's cross-cluster
            // warp leg (native's single c_cmdPlan trigger is pilot-type agnostic).
            else if (!TryRipcordOrDefer(lar.Data.ClusterId, now, $"layer-asteroid {lar.Data.AsteroidId} '{lar.Data.Name}'"))
                goal = CrossClusterWarpGoal(lar.Data.ClusterId, $"layer-asteroid {lar.Data.AsteroidId} '{lar.Data.Name}'");
        }
        // P162 — the Wingman's combat approach. Steers toward the enemy's LIVE
        // current position (re-read fresh every tick, exactly like the other
        // goals — the shared primitive needs no re-aim logic for a moving
        // target), same-sector only by construction (a stale cross-sector target
        // is cleared in UpdateWingmanCombat). The arrival condition is swapped
        // from "stopped on the point" (AutopilotArrivalRadius) to "close enough
        // to fire": a standoff inside the mounted weapon's effective range, so
        // the Wingman closes to engagement range and holds there, nose-on,
        // rather than trying to stop exactly on top of the enemy. This is the
        // stated, honest replacement for the static-point brake heuristic — no
        // orbit/strafe geometry is invented.
        else if (AttackTarget is { } enemy && enemy.Sector == sector)
        {
            goal = enemy.Position;
            float range = EstimateWeaponRange();
            float standoff = Math.Clamp(range * 0.7f, AutopilotArrivalRadius, WingmanMaxEngagementStandoff);
            arrivalRadius = standoff;
            brakeStartDistance = Math.Max(AutopilotBrakeStartDistance, standoff + 200f);

            // P171 — dynamic range maintenance. Once the Wingman is ENGAGED
            // (has reached its engagement standoff — the in-the-heat condition),
            // the fixed standoff-hold THROTTLE is replaced by native's
            // relative-speed response (band 1, shipIGC.cpp:2492-2512): back off
            // when the target is closing too fast, press in when it pulls away,
            // else match its speed. Facing is still SteerToward's — only the
            // throttle is overridden below, so the nose keeps tracking the
            // enemy. Beyond the standoff the ship is still APPROACHING, so the
            // unchanged distance ramp closes distance exactly as before
            // (P162/P166/P169 pursuit preserved).
            float dist = (enemy.Position - Position).Length();
            rangeThrottle = dist <= standoff ? ComputeWingmanRangeThrottle(enemy) : null;
        }
        // P166 — the defending Wingman's escort fallback (Scope item 5). When the
        // Wingman is assigned to protect a charge (DefendTarget set) but is not
        // currently engaging anyone (AttackTarget null — UpdateWingmanDefend
        // cleared it on return-to-escort, or it never acquired a threat), steer
        // toward the charge's LIVE position through the SAME shared
        // SteerToward primitive — plain "stop on the point" arrival, exactly
        // like every stationary goal branch; no new steering code. The combat
        // approach branch above takes precedence whenever AttackTarget is set,
        // so an actively-engaging defender pursues rather than escorts. Same-
        // sector only: a cross-sector charge is not reachable in this port
        // (UpdateWingmanDefend disengages it, and this branch's
        // `charge.Sector == sector` gate then yields no goal — the ship holds
        // position, the faithful same-sector stand-by).
        else if (DefendTarget is { } charge && AttackTarget is null && charge.Sector == sector)
        {
            goal = charge.Position;
        }

        if (goal is not { } targetPos) return;                // no decided destination → nothing to steer toward

        SteerToward(targetPos, arrivalRadius, brakeStartDistance);

        // P171 — override the fixed standoff-hold THROTTLE with the dynamic
        // relative-speed range-maintenance value when engaged (facing from
        // SteerToward above is untouched).
        if (rangeThrottle is { } rt)
            Controls = Controls with { Throttle = rt };
    }

    /// <summary>
    /// P168 — Stage 1 NPC collision avoidance (<c>Dodge</c>,
    /// <c>src/Igc/common.cpp:1693-1791</c>), reduced to this port's single
    /// steering primitive. Scans the ship's own sector for an imminent
    /// collision with another ship or an asteroid, and if one exists returns
    /// (<c>true</c>) a temporary world-space <em>away</em> goal point for
    /// <see cref="SteerToward"/> to steer toward, replacing whatever the ship's
    /// real objective is for that tick. Returns <c>false</c> (no override) when
    /// nothing is imminent, leaving <see cref="ApplyNpcAutopilot"/>'s normal
    /// goal-resolution chain untouched.
    /// <para><b>Faithful pieces.</b> The imminence <em>timing</em> question is
    /// answered exactly as native does: <see cref="SolveForImpact"/> (a faithful
    /// port of <c>solveForImpact</c>, <c>common.cpp:59-119</c>) gives the
    /// time-to-closest-approach for each candidate, and the collision is judged
    /// imminent by native's default acceleration-based bound
    /// <c>t² ≤ 4·r/acceleration</c> (<c>common.cpp:1749-1751</c>), with
    /// <c>acceleration = Thrust / Mass</c> (<c>common.cpp:1707</c>). The winning
    /// obstacle's closest-approach "away" vector is computed identically to
    /// native's <c>ca</c> (<c>common.cpp:1761-1783</c>), including the
    /// dead-center orthogonal fallback.</para>
    /// <para><b>Radical simplification (the arc's stated strategy).</b> Native
    /// feeds <c>ca</c> into <c>getDirection</c> (<c>common.cpp:1575-1636</c>) to
    /// produce omnidirectional <em>strafe-thrust buttons</em>; this port has no
    /// strafe physics, so <c>getDirection</c> is <em>not</em> ported. Instead the
    /// normalized away vector is turned into a plain goal point
    /// (<c>Position + away·<see cref="DodgeGoalDistance"/></c>) for the same
    /// nose-pointing <see cref="SteerToward"/> every other goal uses — exactly
    /// the apply-this-arc's-strategy treatment given to <c>GotoPlan</c> and the
    /// Wingman combat block before it.</para>
    /// <para><b>Candidate scope (stated, not silent).</b> Same-sector only
    /// (native scans <c>pcluster-&gt;GetShips()</c>/<c>GetPickableModels()</c>):
    /// <b>(1) other ships</b> via <see cref="Model.Sector.GetShipUpdateCandidates()"/>
    /// (the established per-sector ship enumeration), excluding <em>enemy</em>
    /// ships — native's <c>Ignore()</c> (<c>common.cpp:1642-1690</c>), read
    /// closely: for a <b>same-side</b> candidate, <c>ignore</c> stays
    /// <c>false</c> in the general case (a drone <em>does</em> dodge its own
    /// side — only a narrow symmetric tie-break, lower object id + both drones
    /// + neither mining nor ripcording, skips it, to stop two drones deadlocking
    /// by each dodging the other forever, <c>:1661-1667</c> — not ported here,
    /// an accepted further simplification, stated not silent); for a
    /// <b>different-side</b> candidate, a drone (<c>pilotType &lt; c_ptPlayer</c>)
    /// always ignores it — <c>"trucker rules of road: we're heavier"</c>
    /// (<c>:1679-1680</c>). So the faithful port excludes <em>enemies</em>, not
    /// friendlies, from the scan — a drone dodges its own formation but is
    /// willing to plow through/past an enemy; and <b>(2) asteroids</b> via
    /// <see cref="Model.Sector.Mineable"/> (this
    /// port's runtime rock list — one entry per static rock, so it is the full
    /// asteroid set, not just He3-capable ones); and <b>(3) stations</b> via
    /// <see cref="Model.Sector.StationRuntimes"/> (the runtime station models,
    /// each with its real collision radius <see cref="Station.Radius"/> and
    /// world position <see cref="Station.Center"/>). <b>Why stations are now
    /// scanned (P170).</b> Native's <em>default</em> Dodge scan is every
    /// pickable model in the cluster (<c>pcluster-&gt;GetPickableModels()</c>,
    /// <c>common.cpp:1920,1932,2003</c>) — ships, asteroids, stations alike —
    /// and only narrows to ships-only (<c>common.cpp:2003</c>:
    /// <c>c_gpmDodgeShips</c>) during specific fine-maneuver waypoint phases
    /// (docking/warp-entry) of <c>GotoPlan</c>'s phased system, which this
    /// port's flat single-goal steering has no equivalent for (genuinely N/A,
    /// not a gap). So the faithful general case is "dodge everything," and the
    /// P168 stage's exclusion of stations was a stated departure from that
    /// default; P170 corrects it. Stations are static like asteroids, so their
    /// imminence check reuses the identical <c>−Velocity</c> relative-motion
    /// shape — no new math. The ship's <em>current navigation goal</em> — the
    /// very object it intends to reach — is <b>ignored</b> in every category,
    /// matching native's active-pursuit
    /// <c>pmodelSkip</c> (<c>common.cpp:2003</c>): the ship/asteroid goals
    /// <c>AttackTarget</c>/<c>DefendTarget</c>/<c>MiningAsteroidTarget</c>/
    /// <c>LayerAsteroidTarget</c>, and — new in P170 — the station-typed
    /// <c>PlanTarget</c> of Miner-selling / Layer / Builder-adjacent flows;
    /// without these a miner would dodge the asteroid it is converging on, or
    /// an NPC the very station it is docking at, and never arrive.</para>
    /// </summary>
    private bool TryComputeDodgeGoal(out Vector3 goal)
    {
        goal = default;
        if (Sector is not { } sector) return false;

        float myRadius = 0.5f * HullType.Length + 1.0f;        // GetRadius()+1 (common.cpp:1706)
        float accel = Mass > 0f ? HullType.Thrust / Mass : 0f; // Thrust/Mass (common.cpp:1707)

        // Candidates the ship is deliberately heading toward must not count as
        // obstacles (native pmodelSkip, common.cpp:2003); also never self.
        bool ignoreShip(Ship s) => ReferenceEquals(s, this)
            || ReferenceEquals(s, AttackTarget) || ReferenceEquals(s, DefendTarget);
        bool ignoreAsteroid(Asteroid a) => ReferenceEquals(a, MiningAsteroidTarget)
            || ReferenceEquals(a, LayerAsteroidTarget);
        // P170 — a station that is the ship's OWN docking/approach goal must not
        // become its own dodge obstacle (the station-typed PlanTarget of
        // Miner-selling / Layer / Builder-adjacent flows, P151). Without this an
        // NPC would dodge the very station it is docking at and never arrive —
        // the exact failure mode the P168 doc comment warns about for the
        // asteroid/ship case, now closed for stations too. PlanTarget is the
        // static DataStationIGC record, so the comparison is against each
        // runtime Station's .Data (its backing static record).
        bool ignoreStation(Station st) => st.Data == PlanTarget;

        Ship? collideShip = null;
        Asteroid? collideAsteroid = null;
        Station? collideStation = null;
        float tCollide = float.MaxValue;

        // (1) Other ships in this sector (same-cluster only, like native's
        // pcluster->GetShips()). Skip ENEMIES — native's Ignore() (common.cpp:
        // 1642-1690): a drone dodges its own side (the common case, formations/
        // escorts) but never bothers dodging a different-side ship ("trucker
        // rules of road: we're heavier", :1679-1680). The narrow same-side
        // symmetric tie-break (:1661-1667) is not ported (accepted, stated
        // simplification — two drones may now both dodge each other at once
        // in that rare case, rather than one yielding).
        foreach (var other in sector.GetShipUpdateCandidates())
        {
            if (ignoreShip(other)) continue;
            if (SideId is not { } ms || other.SideId is not { } os || os != ms) continue;   // enemy/sideless skip — only dodge a same, non-null side (native's mySide==hisSide fallthrough)
            float r = 0.5f * other.HullType.Length + myRadius;   // other.GetRadius() + myRadius (common.cpp:1731)
            Vector3 rel = other.Position - Position;
            float d2 = rel.LengthSquared();
            float t = d2 > r * r ? SolveForImpact(rel, other.Velocity - Velocity, 0f, r, out _) : 0f;   // :1736-1742
            if (t < tCollide && IsImminent(t, r, accel))
            {
                tCollide = t;
                collideShip = other;
            }
        }

        // (2) Asteroids (sector.Mineable — every runtime rock, all static so
        // deltaV is just −our velocity). Dead rocks are removed anyway, but
        // belt-and-suspenders like the rescue pass.
        foreach (var rock in sector.Mineable)
        {
            if (ignoreAsteroid(rock) || rock.Dead) continue;
            if (rock.Data.Position is not { Length: 3 } p) continue;
            var pos = new Vector3(p[0], p[1], p[2]);
            float r = rock.Radius + myRadius;
            Vector3 rel = pos - Position;
            float d2 = rel.LengthSquared();
            float t = d2 > r * r ? SolveForImpact(rel, -Velocity, 0f, r, out _) : 0f;
            if (t < tCollide && IsImminent(t, r, accel))
            {
                tCollide = t;
                collideAsteroid = rock;
            }
        }

        // (3) Stations in this sector (sector.StationRuntimes — the runtime
        // Station objects, the faithful parallel of sector.Mineable for
        // asteroids; each carries its real collision radius from the resolved
        // type via Station.Radius and its world position via Station.Center,
        // with Station.Data exposing the backing static DataStationIGC for the
        // PlanTarget exclusion above). Native's DEFAULT Dodge scan is every
        // pickable model in the cluster (pcluster->GetPickableModels(),
        // common.cpp:1920/1932/2003) — stations included; the ships-only
        // narrowing (common.cpp:2003's c_gpmDodgeShips) is tied to GotoPlan's
        // phased fine-maneuver waypoints (docking/warp-entry), which this port's
        // flat single-goal steering has no equivalent for, so the faithful
        // general case is "dodge everything." Stations are static like rocks,
        // so deltaV is just −our velocity — the identical relative-motion shape
        // as the asteroid branch, no new math. Dead stations are dropped by
        // RemoveStation anyway; belt-and-suspenders like the rock/ship passes.
        foreach (var station in sector.StationRuntimes)
        {
            if (ignoreStation(station) || station.Dead) continue;
            float r = station.Radius + myRadius;
            Vector3 rel = station.Center - Position;
            float d2 = rel.LengthSquared();
            float t = d2 > r * r ? SolveForImpact(rel, -Velocity, 0f, r, out _) : 0f;
            if (t < tCollide && IsImminent(t, r, accel))
            {
                tCollide = t;
                collideStation = station;
            }
        }

        if (collideShip is null && collideAsteroid is null && collideStation is null) return false;   // nothing imminent (common.cpp:1790)

        // Compute the winner's position & velocity to derive the away vector.
        Vector3 collidePos = collideShip is not null ? collideShip.Position
            : collideAsteroid is not null
                ? new Vector3(collideAsteroid.Data.Position[0], collideAsteroid.Data.Position[1], collideAsteroid.Data.Position[2])
                : collideStation!.Center;
        Vector3 collideVel = collideShip is not null ? collideShip.Velocity : Vector3.Zero;

        // Away vector: native's ca (common.cpp:1761-1783).
        Vector3 dp = Position - collidePos;
        Vector3 ca;
        if (tCollide == 0f)
            ca = dp;                                                // already inside → straight away from center (:1767-1768)
        else
        {
            Vector3 dv = Velocity - collideVel;
            float ldv2 = dv.LengthSquared();
            ca = ldv2 > 0.1f ? dp - dv * ((dp * dv) / ldv2) : dp;   // closest-approach offset (:1774-1776)
            if (ca.LengthSquared() < 0.1f)
                ca = OrthogonalVector(dp);                          // dead-centre aim → orthogonal (:1777-1782)
        }

        float caLen = ca.Length();
        if (caLen < 1e-3f) return false;                            // defensive: degenerate away vector
        goal = Position + ca * (DodgeGoalDistance / caLen);
        return true;
    }

    /// <summary>
    /// P168 — the imminence gate for <see cref="TryComputeDodgeGoal"/>: native's
    /// default (<c>tMax &lt; 0</c>) threshold <c>t² ≤ 4·r/acceleration</c>
    /// (<c>common.cpp:1749-1751</c>), the form used at the idle/generic Dodge
    /// call sites this universal check mirrors (<c>common.cpp:1920,1932</c>;
    /// <c>shipIGC.cpp:2334</c>). <paramref name="r"/> is the combined radius
    /// (obstacle radius + ship radius), <paramref name="accel"/> the ship's
    /// <c>Thrust/Mass</c>. Falls back to <see cref="DodgeFallbackTMax"/> only for
    /// a degenerate non-positive acceleration (never divides by zero).
    /// </summary>
    private static bool IsImminent(float t, float r, float accel)
        => accel > 0f ? t * t <= 4f * r / accel : t <= DodgeFallbackTMax;

    /// <summary>
    /// P168 — faithful port of the native quadratic solve for time-to-closest-
    /// approach, <c>solveForImpact</c> (<c>src/Igc/common.cpp:59-119</c>). Returns
    /// the time <c>t ≥ 0</c> at which the projectile/separation
    /// <c>deltaP + t·deltaV</c> first reaches distance <c>radius</c> from the
    /// target, or a huge value when no such future encounter exists (moving
    /// apart / already separating). <paramref name="direction"/> receives the
    /// aiming direction when a solution exists (native sets it only in the
    /// solved branches); this port's <c>Dodge</c> call discards it, but the
    /// utility is general — the same math native's <c>solveForLead</c> reuses
    /// for shot prediction (<c>common.cpp:121-149</c>).
    /// <para><b>Formula</b> (<c>common.cpp:82-108</c>):
    /// <c>c = deltaP² − radius²</c> (≤ 0 ⇒ already overlapping ⇒ <c>t = 0</c>);
    /// <c>a = deltaV² − speed²</c>; <c>b = 2(deltaV·deltaP − radius·speed)</c>;
    /// the smallest positive root of <c>a·t² + b·t + c = 0</c> is
    /// <c>t = (b + √(b²−4ac)) / (−2a)</c>. The no-solution branch
    /// (<c>common.cpp:87-92</c>) returns the same huge sentinel (the mmf
    /// <c>FLT_MAX / 500000000</c> divisor). Native's inner <c>b24ac&lt;0 → 0.1f</c>
    /// clamp (<c>common.cpp:97-100</c>) is unreachable — the negative-discriminant
    /// case is already diverted by the outer guard — and is reproduced as dead
    /// code for fidelity.</para>
    /// <para><b>Tiny C# deviation.</b> C# <c>out</c> requires <c>direction</c> be
    /// assigned on every path, whereas native leaves <c>*direction</c> unset in
    /// the no-solution branch; here it is initialized to <see cref="Vector3.Zero"/>
    /// and simply not overwritten there — no behaviour difference for any caller.</para>
    /// </summary>
    internal static float SolveForImpact(Vector3 deltaP, Vector3 deltaV, float speed, float radius, out Vector3 direction)
    {
        direction = default;
        float c = deltaP * deltaP - radius * radius;
        if (c <= 0.0f)                              // already overlapping → t=0 (common.cpp:68-73)
        {
            direction = deltaP.Normalize();
            return 0.0f;
        }

        float a = deltaV * deltaV - speed * speed;                 // :82
        float b = 2.0f * (deltaV * deltaP - radius * speed);       // :83
        float b24ac = b * b - 4.0f * a * c;                        // :85

        if (a == 0.0f || (a > 0.0f && b > 0.0f) || b24ac < 0.0f)   // no valid solution (:87-92)
            return float.MaxValue / 500000000.0f;                  // mmf podpickup divisor

        // b24ac >= 0 here (the negative case is diverted above; native's :97-100
        // clamp is unreachable). Smallest positive root (common.cpp:103).
        float t = (b + MathF.Sqrt(b24ac)) / (-2.0f * a);
        if (t > 0.0f)
        {
            direction = (deltaP + t * deltaV) / (t * speed + radius);   // :108
            return t;
        }

        direction = deltaP.Normalize();                // round-off guard (common.cpp:110-114)
        return 0.0f;
    }

    /// <summary>
    /// P169 — the Wingman's aim-leading intercept solve, a faithful port of
    /// native <c>solveForLead</c> (<c>src/Igc/common.cpp:121-149</c>) layered
    /// directly on <see cref="SolveForImpact"/> (P168's faithful port of
    /// <c>solveForImpact</c>, <c>common.cpp:59-119</c>). Predicts where to
    /// point the mounted weapon so a round fired at its projectile's speed
    /// intercepts <paramref name="target"/> as it moves — the "lead" aim point
    /// — rather than aiming at the target's current position. Returns the
    /// time <c>t ≥ 0</c> to intercept and fills <paramref name="direction"/>
    /// with the world-space unit aim direction.
    /// <para><b>Faithful formula</b> (<c>common.cpp:132-148</c>):
    /// the shooter's position is <see cref="Ship.Position"/> directly — the
    /// muzzle-offset term <c>weapon-&gt;GetPosition() * myOrientation</c>
    /// (<c>common.cpp:143</c>) has no analogue here (no hull mesh), the same
    /// omission the existing fire path already accepts at <c>Weapon.cs:230</c>;
    /// the target's velocity is skill-scaled, <c>hisVelocity * skill</c>
    /// (<c>common.cpp:137</c>); the projectile speed is
    /// <paramref name="weapon"/>'s <see cref="DataProjectileTypeIGC.Speed"/>
    /// (<c>common.cpp:141</c>, with the <c>c_gaSpeedAmmo</c> boost omitted —
    /// no side-economy attribute in this port, the identical omission accepted
    /// at <c>Weapon.cs:233</c>); and <see cref="DataProjectileTypeIGC.AbsoluteF"/>
    /// selects which velocity term feeds the solve — the (skill-scaled) target
    /// velocity alone for an absolute projectile, else that velocity relative
    /// to the shooter's own (<c>common.cpp:144-146</c>). The target's radius is
    /// this port's <c>0.5 × HullType.Length</c> ship-radius convention
    /// (GetRadius() analogue, <c>Ship.cs:1873</c>).</para>
    /// <para><b>Why this port solves instead of reading the fired round.</b>
    /// The weapon's own physics (<c>Weapon.FireWeapon</c>) already reads
    /// <c>ProjectileType.Speed</c>/<c>AbsoluteF</c> for the actual shot
    /// (<c>Weapon.cs:233-234</c>); this method reads the SAME two fields for the
    /// predict, keeping the fire-aim and the fire-physics on one projectile
    /// definition. No weapon means no lead to solve — the caller
    /// (<see cref="UpdateWingmanCombat"/>) guards on a mounted weapon first.</para>
    /// </summary>
    private float SolveForLead(Ship target, Weapon weapon, float skill, out Vector3 direction)
    {
        Vector3 myPosition = Position;                               // + muzzle offset (none: no hull mesh), cf Weapon.cs:230
        Vector3 myVelocity = Velocity;
        Vector3 hisPosition = target.Position;
        Vector3 hisVelocity = target.Velocity * skill;               // common.cpp:137 (skill scales the TARGET's motion)

        DataProjectileTypeIGC pt = weapon.ProjectileType;
        float speed = pt.Speed;                                      // common.cpp:141; c_gaSpeedAmmo omitted (no side), cf Weapon.cs:233
        Vector3 deltaV = pt.AbsoluteF ? hisVelocity : hisVelocity - myVelocity;   // common.cpp:144-146

        return SolveForImpact(hisPosition - myPosition, deltaV, speed,
                              0.5f * target.HullType.Length, out direction);      // target.GetRadius() analogue (Ship.cs:1873)
    }

    /// <summary>
    /// P266 — the public, server-facing accessor over the private
    /// <see cref="SolveForLead(Ship, Weapon, float, out Vector3)"/> (P169's faithful
    /// port of native <c>solveForLead</c>, <c>common.cpp:121-149</c>), exposing the
    /// solved lead aim DIRECTION and intercept TIME <c>t</c> to
    /// <c>Server.BroadcastLeadIndicator</c> so the server can stream the predictive
    /// lead reticle to the owner-only wire (the renderer-only MonoGame client has no
    /// static core to solve it locally). Returns the time-to-intercept <c>t ≥ 0</c>;
    /// the lead aim direction is written to <paramref name="direction"/>. A
    /// <paramref name="skill"/> of 1.0 leaves the target's velocity unscaled (the
    /// player-pilot case); &lt;1 underestimates the target's motion (the wingman
    /// shooting-accuracy model, <c>SolveForLead</c>'s own doc).</summary>
    public float SolveLead(Ship target, Weapon weapon, float skill, out Vector3 direction)
        => SolveForLead(target, weapon, skill, out direction);

    /// <summary>
    /// P266 — the lead indicator's green→red hit-probability <c>g ∈ [0,1]</c>,
    /// ported from the native client's cockpit-view crosshair colour ramp
    /// (<c>WinTrek/indicator.cpp:341-357</c>). Native colours the centre
    /// crosshair from the screen-space distance ratio <c>f = l²/r²</c>, where
    /// <c>l</c> is the on-screen separation between the lead point and the fixed
    /// reticle and <c>r</c> the target's on-screen radius, applying the piecewise
    /// <c>g = (f&lt;1) ? 1-0.75f : (f&lt;2) ? 0.5-f/4 : 0</c>. This port has no
    /// screen-space quantities server-side, so it computes the ANGULAR
    /// equivalent (algebraically the same ramp): <c>θ</c> is the angle between the
    /// ship's current forward aim (the weapon's firing solution — fixed guns fire
    /// straight along the hull) and the solved lead direction, and
    /// <paramref name="targetAngularRadius"/> is the angle the target subtends at
    /// the shooter (<c>atan2(targetRadius, distance)</c>). <c>f =
    /// (θ/targetAngularRadius)²</c> feeds the identical piecewise. The result maps
    /// to the render colour <c>(1-g, g, 0)</c>: full green at a perfectly-aligned
    /// aim, shifting through amber to red as the aim drifts more than ~1× the
    /// target's angular size off the lead point (the brief's "green→red based on
    /// angular deviation between the lead point and the firing solution").</summary>
    public static float LeadHitProbability(Vector3 shipForward, Vector3 leadDirection, float targetAngularRadius)
    {
        float ll = leadDirection.LengthSquared();
        if (ll <= 1e-9f) return 0f;                       // no solved direction — nothing aligned
        leadDirection /= MathF.Sqrt(ll);
        if (targetAngularRadius <= 1e-9f) return 1f;      // target dead ahead at zero separation

        float cos = shipForward.X * leadDirection.X
                  + shipForward.Y * leadDirection.Y
                  + shipForward.Z * leadDirection.Z;      // no Vector3.Dot static (port convention)
        cos = Math.Clamp(cos, -1f, 1f);
        float theta = MathF.Acos(cos);
        float f = theta / targetAngularRadius;
        f *= f;
        if (f < 2.0f)
        {
            if (f < 1.0f) return 1.0f - 0.75f * f;        // indicator.cpp:344-345
            return 0.5f - f / 4.0f;                       // indicator.cpp:346
        }
        return 0.0f;                                      // indicator.cpp:348
    }

    /// <summary>
    /// P168 — port of <c>Vector::GetOrthogonalVector</c>
    /// (<c>src/zlib/vector.cpp:6-35</c>): a vector perpendicular to <paramref name="v"/>,
    /// picking the largest-magnitude component axis as the pivot exactly as
    /// native does (so it never divides by the zero axis). Used only as the
    /// dead-centre fallback for the dodge away-vector (<c>common.cpp:1777-1782</c>).
    /// </summary>
    private static Vector3 OrthogonalVector(Vector3 v)
    {
        float ax = MathF.Abs(v.X), ay = MathF.Abs(v.Y), az = MathF.Abs(v.Z);
        if (ax > ay)
        {
            if (ax > az) return new Vector3(-v.Z - v.Y * v.Z / v.X, v.Z, v.X);   // x biggest (vector.cpp:18)
        }
        else
        {
            if (ay > az) return new Vector3(v.Y, -v.X - v.Z * v.X / v.Y, v.X);   // y biggest (vector.cpp:26)
        }
        return new Vector3(v.Y, v.Z, -v.Y - v.X * v.Y / v.Z);                    // z biggest (vector.cpp:34)
    }

    /// <summary>
    /// P155 — the shared "face and thrust" steering primitive reused by every
    /// goal of <see cref="ApplyNpcAutopilot"/> (extracted intact from the
    /// original inline body in P162 so the combat leg can vary only the
    /// arrival/brake radii, never the steering law). Computes a
    /// <see cref="ControlData"/> that points the nose at <paramref name="targetPos"/>
    /// and throttles appropriately, assigning it to <see cref="Controls"/>
    /// <em>before</em> <see cref="ExecuteMove"/> reads it each tick.
    /// <paramref name="arrivalRadius"/> is the "close enough to stop" distance
    /// — <see cref="AutopilotArrivalRadius"/> for a stationary goal, a
    /// fire-range standoff for the Wingman's combat approach — and
    /// <paramref name="brakeStartDistance"/> where the two-zone ramp begins
    /// shedding speed.
    /// </summary>
    private void SteerToward(Vector3 targetPos, float arrivalRadius, float brakeStartDistance)
    {
        Vector3 toTarget = targetPos - Position;
        float distance = toTarget.Length();
        if (distance <= 1e-3f)
        {
            Controls = Controls with { Throttle = -1f };      // dead-centre: hold the kill-throttle
            return;
        }

        // Face: the signed rotation axis that turns the ship's nose onto the
        // target is forward × directionToTarget. Expressed in ship-local space
        // (Orientation.TimesInverse), its up-component drives yaw and its
        // right-component drives pitch. Unlike a scalar bearing (atan2), this
        // cross-product signal is CONTINUOUS through the target-astern case — a
        // dead-behind target gives ~0 (breaking the symmetry to a definite turn
        // as the ship drifts) instead of flipping between full-left/full-right
        // each tick, so the nose never stalls pointing the wrong way. Yaw/pitch
        // signs are chosen to match how ExecuteMove applies the turn axes
        // (Yaw(+rate·dT), Pitch(−rate·dT), Ship.cs:1806-1808; this port's
        // left-handed frame, Orientation.cs:16-35).
        Vector3 dir = toTarget / distance;
        Vector3 body = Orientation.TimesInverse(Vector3.CrossProduct(Orientation.GetForward(), dir));
        float yaw = Math.Clamp(AutopilotTurnGain * body.Y, -1f, 1f);
        float pitch = Math.Clamp(AutopilotTurnGain * -body.X, -1f, 1f);

        // Thrust: a two-zone distance ramp (simplified arrival, not closed-form)
        // scaled by how well the nose is aligned with the target
        // (forward·dir = −local.Z). Cutting power while misaligned lets the
        // ship slow and turn tightly (a small hull's turn radius is
        // ~v/ω — at full speed it cannot track the target and would orbit it
        // forever instead of converging); aligned, it accelerates. This is the
        // "turn then burn" simplification of native's velocity-managed
        // GotoPlan, stated plainly rather than silently hidden.
        float distFactor = distance >= brakeStartDistance
            ? 1f
            : Math.Clamp((distance - arrivalRadius) / (brakeStartDistance - arrivalRadius), 0f, 1f);
        float alignment = Math.Clamp(-Orientation.TimesInverse(dir).Z, 0f, 1f);   // forward·dir
        float throttle = Math.Clamp(2f * distFactor * alignment - 1f, -1f, 1f);

        Controls = Controls with { Yaw = yaw, Pitch = pitch, Roll = 0f, Throttle = throttle };
    }

    /// <summary>
    /// P162 — the greatest effective range, in world units, that any currently
    /// mounted weapon can reach: the maximum over the ship's weapons of
    /// <c>projectileSpeed · projectileLifespan</c> (the furthest a round travels
    /// before despawning, <see cref="Weapon.GetLifespan"/> ·
    /// <see cref="Weapon.ProjectileType"/>'s <c>Speed</c>), or <c>0</c> when no
    /// weapon is mounted. Used to gate the Wingman's fire decision (fire only
    /// while the target is reachable) and to size the combat approach standoff
    /// so the ship settles inside range. Confirmed against the real data:
    /// <c>DataProjectileTypeIGC</c> carries both <c>Speed</c> and <c>Lifespan</c>
    /// (DataProjectileTypeIGC.cs:22-23) and <c>Weapon.FireWeapon</c> flies a
    /// round at <c>Speed</c> for <c>Lifespan</c> (Weapon.cs:232-233), so
    /// <c>Speed · Lifespan</c> is the honest reach — not a guessed constant.
    /// </summary>
    private float EstimateWeaponRange()
    {
        float max = 0f;
        foreach (var w in _weapons)
        {
            float reach = w.GetLifespan() * w.ProjectileType.Speed;
            if (reach > max) max = reach;
        }
        return max;
    }

    /// <summary>
    /// P171 — relative closing speed of <paramref name="target"/> as seen along
    /// THIS ship's own forward: <c>ownDirSpeed − targetDirSpeed</c>, each the
    /// <c>CosForward(v)·|v|</c> projection native computes
    /// (<c>shipIGC.cpp:2479-2481</c>). Here <c>forward·v = −Orientation.TimesInverse(v).Z</c>
    /// (the same CosForward-style dot product the P155 turn law and
    /// <see cref="SteerToward"/> already use, <c>Ship.cs:147-151, 3119</c>) —
    /// no new projection primitive. Positive means closing on the target,
    /// negative falling behind (matching the sign conventions of the native
    /// thresholds in <see cref="UpdateWingmanCombat"/>'s maneuvering block).
    /// </summary>
    private float ComputeRelativeSpeed(Ship target)
    {
        float ownDirSpeed = -Orientation.TimesInverse(Velocity).Z;              // shipIGC.cpp:2479
        float targetDirSpeed = -Orientation.TimesInverse(target.Velocity).Z;    // shipIGC.cpp:2480
        return ownDirSpeed - targetDirSpeed;                                    // shipIGC.cpp:2481
    }

    /// <summary>
    /// P171 — the reduced single-band range-maintenance throttle, the port of
    /// native's in-the-heat band (<c>shipIGC.cpp:2492-2512</c>). A strongly
    /// negative <see cref="ComputeRelativeSpeed"/> (falling behind / target
    /// pulling away) presses in with full forward throttle (<c>+1</c> =
    /// native's <c>forwardButtonIGC</c>); a strongly positive one (closing too
    /// fast / target inbound) backs off with full brake (<c>−1</c> = native's
    /// <c>backwardButtonIGC</c>); in between it matches the target's along-
    /// forward speed with native's graduated formula (:2507-2509) exactly.
    /// <para>The native block ALSO sets move-button bits alongside these
    /// throttles; in this port the only longitudinal control is the
    /// <c>Controls.Throttle</c> axis (<see cref="ExecuteMove"/>'s throttle-cruise
    /// branch, <c>Ship.cs:2379-2385</c>: <c>+1</c> = max forward, <c>−1</c> =
    /// brake/stop), so <c>forwardButtonIGC → +1</c> and
    /// <c>backwardButtonIGC → −1</c> is the exact translation — the buttons and
    /// the graduated axis are mutually exclusive longitudinal commands in
    /// native, never issued together (:2495 vs :2511), exactly as one
    /// <c>Throttle</c> scalar expresses either.</para>
    /// <para>Deliberately excluded (Stage-1 reduction, see writeup): the other
    /// three distance/time bands (approach, long-range close, give-up/re-target),
    /// the afterburner kicks, and the <c>criticalMovement</c> flag.</para>
    /// </summary>
    private float ComputeWingmanRangeThrottle(Ship target)
    {
        float relativeSpeed = ComputeRelativeSpeed(target);                     // shipIGC.cpp:2479-2481 (positive = closing)

        if (relativeSpeed < -WingmanClosingSpeedThreshold)
            return 1f;        // forwardButtonIGC → press in (:2494-2498)
        if (relativeSpeed > WingmanClosingSpeedThreshold)
            return -1f;       // backwardButtonIGC → back off (:2499-2502)

        // match speed — the graduated throttle (:2504-2512), ported exactly
        float targetDirSpeed = -Orientation.TimesInverse(target.Velocity).Z;    // shipIGC.cpp:2480
        float myMaxSpeed = HullType.Speed;
        return targetDirSpeed >= myMaxSpeed
            ? 1f
            : targetDirSpeed <= 0f
                ? -1f
                : 2f * targetDirSpeed / myMaxSpeed - 1f;                        // shipIGC.cpp:2507-2509
    }


    /// <summary>
    /// P172 — the NPC ripcord-as-travel decision, wired ahead of every
    /// cross-cluster <see cref="CrossClusterWarpGoal"/> call site in
    /// <see cref="ApplyNpcAutopilot"/> (station / mining-asteroid / layer-asteroid
    /// branches — native's single <c>c_cmdPlan</c> trigger is pilot-type
    /// agnostic). Decides whether the current cross-cluster goal is travelled by
    /// <b>ripcord</b> instead of warp-pathing. Returns <c>true</c> when the
    /// ripcord path owns this tick (initiated now, or active already, or deferred
    /// awaiting a safety retry), in which case the caller must <b>not</b> warp-
    /// path; returns <c>false</c> when no ripcord anchor is reachable, in which
    /// case the caller falls through to the existing P157/158 warp-pathing
    /// unchanged. Mirrors native's call-site-2 structure (<c>shipIGC.cpp:4153-4172</c>),
    /// which runs <c>bShouldUseRipcord</c>/<c>IsSafeToRipcord</c> ahead of the
    /// plan-driven GotoPlan warp movement.</para>
    /// <para><b>Composition with warp-pathing (confirmed).</b> Ripcord takes
    /// priority whenever a valid friendly ripcord anchor is reachable in/around
    /// the target cluster — that is the whole point of native's
    /// <c>bShouldUseRipcord(pclusterTarget)</c> gate at <c>:4154</c>; the
    /// warp-path only engages in the <c>else</c> (no-anchor) case. This task
    /// adds the alternative, it does not touch the existing warp leg.</para>
    /// <para><b>Unsafe retry (simplified, stated).</b> Native's unsafe branch
    /// (<c>:4162-4166</c>) stores a pending ripcord and re-tests 5&nbsp;s later
    /// (<c>:2066-2067</c>) while GotoPlan simultaneously warp-paths. This port
    /// deliberately simplifies to "the ripcord path owns the tick": when an
    /// anchor is reachable but <c>IsSafeToRipcord()</c> fails, the ship defers
    /// (holds, not warp-pathing) and re-attempts after
    /// <see cref="RipcordRetryInterval"/> — guaranteeing the cross-cluster
    /// journey happens by ripcord, never racing a physical warp transit. Stated
    /// as the applied simplification in NPC_RIPCORD_TRAVEL_P172.md.</para>
    /// </summary>
    private bool TryRipcordOrDefer(short targetClusterId, double now, string what)
    {
        if (Mission is not { } mission) return false;
        var target = mission.Sectors.FirstOrDefault(s => s.Id == targetClusterId);
        if (target is null) return false;                 // unknown sector — no ripcord possible; caller warp-paths

        // Already ripcording to exactly this cluster: the active countdown owns
        // the travel; never fight it with warp-steering (avoids a physical warp
        // transit racing the ripcord teleport).
        if (IsRipcordActive && RipcordModel is { } rm && rm.ClusterId == target.Id)
        {
            _npcPendingRipcordTarget = null;
            return true;
        }

        // Native :4154 — bShouldUseRipcord(pclusterTarget): is a friendly ripcord
        // station reachable in/around the target cluster? None reachable → the
        // ripcord alternative is unavailable; return false so the caller falls
        // through to the untouched P157/158 warp-pathing.
        if (FindRipcordAnchorCluster(target) is null)
        {
            _npcPendingRipcordTarget = null;
            return false;
        }

        // Inside a prior unsafe refusal's 5 s retry window → keep deferring (no
        // warp-path, no repeated ripcord attempt) until the window lapses, then
        // re-test. Mirrors native's latched m_timeRequestRipcord cadence.
        if (now < _npcRipcordRetryAt)
        {
            _npcPendingRipcordTarget = target;
            return true;
        }

        if (IsSafeToRipcord())
        {
            _npcPendingRipcordTarget = null;
            _npcRipcordRetryAt = double.NegativeInfinity;
            mission.Site.RequestRipcord(this, target);    // IIgcSite seam (native pigc->RequestRipcord, :4161)
            if (!_npcRipcordInitiatedLogged)
            {
                Console.WriteLine($"[npc] ship {Id} (side {SideId}) — cross-cluster {what} in sector '{target.Name}' ({target.Id}): ripcord anchor reachable & safe → initiating ripcord");
                _npcRipcordInitiatedLogged = true;
                _npcRipcordUnsafeLogged = false;
            }
        }
        else
        {
            _npcPendingRipcordTarget = target;
            _npcRipcordRetryAt = now + RipcordRetryInterval;   // 5 s retry (native :4166, :2067)
            if (!_npcRipcordUnsafeLogged)
            {
                Console.WriteLine($"[npc] ship {Id} (side {SideId}) — cross-cluster {what} in sector '{target.Name}' ({target.Id}): ripcord anchor reachable but UNSAFE → defer & retry in {RipcordRetryInterval}s");
                _npcRipcordUnsafeLogged = true;
                _npcRipcordInitiatedLogged = false;
            }
        }
        return true;   // the ripcord path owns this tick (initiated or deferred)
    }

    /// <summary>
    /// P172 — the port of native <c>CshipIGC::bShouldUseRipcord</c>
    /// (<c>shipIGC.cpp:4231-~4390</c>) narrowed to the <b>pilot-type-agnostic
    /// station-anchor core</b> (this task's scope): a breadth-first walk outward
    /// from <paramref name="target"/> through the warp graph, returning the first
    /// cluster (target itself or a neighbour) that hosts a <b>friendly</b>
    /// <c>c_sabmRipcord</c>-capable station, or <c>null</c> when none is reachable
    /// within the search bound. Returns the <em>anchor cluster</em> (scope item 2
    /// "return the anchor cluster if found").
    /// <para><b>Real-vs-drone split (confirmed by the gate, not assumed).</b>
    /// Native wraps the ship/probe-anchor tiers — allied <c>GetRipcordCluster</c>
    /// ships (<c>:4238-4286</c>) and <c>pcluster->GetProbes()</c> ripcord probes
    /// (<c>:4316-4333</c>) — in <c>if (m_pilotType &gt;= c_ptPlayer)</c>. A drone
    /// (<c>pilotType &lt; c_ptPlayer</c>) only ever considers the <c>FindTarget(
    /// this, c_ttFriendly | c_ttStation, NULL, pcluster, NULL, NULL,
    /// c_sabmRipcord)</c> station test (<c>:4307-4311</c>) plus the pure warp-graph
    /// BFS that repeatedly applies it. This task ports exactly that station core
    /// and no ship/probe branch (explicitly out of scope).</para>
    /// <para><b>BFS shape.</b> Mirrors <see cref="Model.Sector.FindPathToward"/>'s
    /// plain unweighted traversal (and native's own two-list bracket walk,
    /// <c>:4361-4381</c>) as a level-order sweep, bounded by native's two
    /// termination conditions: no further frontier (<c>:4372</c>) or the search
    /// stepping no closer than the ship (<c>dShip-- &lt;= 1</c>, <c>:4373</c>),
    /// where <c>dShip</c> is the warp-hop distance from the ship to the target
    /// (<c>GetDistance</c>, <c>:4236</c>).</para>
    /// </summary>
    private Sector? FindRipcordAnchorCluster(Sector target)
    {
        if (HullType.HasCapability(DataHullTypeIGC.HabmNoRipcord))
            return null;                                  // :4232-4233 (no ripcord capability)
        if (Sector is not { } shipSector || ReferenceEquals(target, shipSector))
            return null;                                  // :4234 (already there → nothing to ripcord across)
        if (Mission is not { } mission) return null;

        int dShip = WarpHopDistance(shipSector, target);  // GetDistance(this, target, shipSector), :4236

        var visited = new HashSet<Sector> { target };
        var level = new List<Sector> { target };          // native pops one cluster per iteration and swaps
                                                          // the two warp lists when the current distance bracket
                                                          // empties (:4361-4381); a level-order sweep is equivalent.
        while (level.Count > 0)
        {
            foreach (var c in level)
                if (HasFriendlyRipcordStation(c))
                    return c;                             // FindTarget friendly station, :4307-4311

            var next = new List<Sector>();
            foreach (var c in level)
                foreach (var w in c.Warps)
                    if (w.Destination is { } dest && visited.Add(dest))
                        next.Add(dest);

            if (next.Count == 0) return null;             // no place left to search (:4372)
            dShip--;
            if (dShip <= 1) return null;                  // no place closer than the ship (:4373)
            level = next;
        }
        return null;
    }

    /// <summary>P172 — does <paramref name="c"/> hold a station on this ship's own
    /// side whose station type is <c>c_sabmRipcord</c>-capable? The faithful,
    /// concrete port of native's <c>FindTarget(this, c_ttFriendly | c_ttStation,
    /// NULL, pcluster, NULL, NULL, c_sabmRipcord)</c> (<c>shipIGC.cpp:4307-4311</c>)
    /// — the same friendly-station tier <c>Server.FindRipcordModel</c> (P111)
    /// uses for the actual anchor resolution (<c>Server.cs:4932-4938</c>).</summary>
    private bool HasFriendlyRipcordStation(Sector c)
    {
        if (SideId is not { } side || Mission is not { } mission) return false;
        var core = mission.Core;
        foreach (var st in c.Stations)
        {
            if (st.SideId != side) continue;
            if (core.StationTypes.TryGetValue(st.StationTypeId, out var stype) &&
                stype.HasCapability(DataStationTypeIGC.SabmRipcord))
                return true;
        }
        return false;
    }

    /// <summary>P172 — warp-graph hop distance from <paramref name="a"/> to
    /// <paramref name="b"/> (the port of <c>GetDistance(this, pcluster,
    /// pclusterShip)</c>, <c>shipIGC.cpp:4236</c>): 0 if the same sector, else the
    /// number of warps in a shortest path, or <see cref="int.MaxValue"/> when the
    /// two are disconnected (the search then terminates purely on the empty-
    /// frontier condition). Plain unweighted BFS, same shape as
    /// <see cref="Model.Sector.FindPathToward"/>.</summary>
    private static int WarpHopDistance(Sector a, Sector b)
    {
        if (ReferenceEquals(a, b)) return 0;
        var visited = new HashSet<Sector> { a };
        var frontier = new Queue<(Sector, int)>();
        foreach (var w in a.Warps)
            if (w.Destination is { } d && visited.Add(d))
                frontier.Enqueue((d, 1));
        while (frontier.TryDequeue(out var cur))
        {
            if (ReferenceEquals(cur.Item1, b)) return cur.Item2;
            foreach (var w in cur.Item1.Warps)
                if (w.Destination is { } d && visited.Add(d))
                    frontier.Enqueue((d, cur.Item2 + 1));
        }
        return int.MaxValue;
    }

    /// <summary>
    /// P172 — the port of native <c>CshipIGC::IsSafeToRipcord</c>
    /// (<c>shipIGC.cpp:4200-4229</c>), the pure predicate "may I begin a ripcord
    /// spin-up without colliding with anything in my drift path?" Native computes
    /// how far the ship will drift during the ripcord's spin-up under the same
    /// <c>dV/dT = −kV</c> drag the port's own P155 <c>GotoPlan</c> doc discusses,
    /// folds it into an <em>effective horizon</em> <c>tMax = 1 + s/v0</c>, and
    /// runs <c>Dodge(this, NULL, &amp;state, false, tMax)</c>, returning
    /// <c>state == 0</c> (no avoidance needed) as "safe".
    /// <para><b>Formula</b> (<c>:4204-4219</c>): stopped (<c>speed² &lt; 1</c>)
    /// is safe outright; otherwise <c>tRipcord = GetRipcordSpeed()</c> — the
    /// hull's ripcord-speed field used as the <em>full spin-up duration</em>,
    /// matching <see cref="RipcordSpeed"/>'s interpretation (this port's
    /// <c>Ship.cs:494-497</c>), <em>not</em> the live countdown
    /// (<see cref="RipcordTimeLeft"/>) — <c>v0 = |velocity|</c>,
    /// <c>k = thrust / (vMax·mass)</c>, <c>s = v0·(1−e^(−k·tRipcord))/k</c>
    /// (the drifted distance), <c>tMax = 1 + s/v0</c> ("a bit of a fudge",
    /// <c>:4220</c>). The ship is safe iff no scan candidate has a closest-approach
    /// time <c>t ≤ tMax</c> — <c>Dodge</c>'s positive-<c>tMax</c> branch
    /// (<c>common.cpp:1749</c>).</para>
    /// <para><b>Scan reuse.</b> The imminence question is answered with the SAME
    /// utility <see cref="TryComputeDodgeGoal"/> (P168) uses —
    /// <see cref="SolveForImpact"/> — over the same candidate universe (same-side
    /// ships, asteroids, stations), with the same relative-motion shapes
    /// (<c>other.Velocity − Velocity</c> for a ship, <c>−Velocity</c> for a
    /// static asteroid/station). Native passes <c>pmodelIgnore = NULL</c> to this
    /// particular <c>Dodge</c> call (<c>:4222</c>), so the ship's own nav target
    /// is deliberately NOT excluded here (unlike the P168/P170 waypoint dodges
    /// that pass <c>pmodelSkip</c>).</para>
    /// </summary>
    public bool IsSafeToRipcord()
    {
        float speed2 = Velocity.LengthSquared();
        if (speed2 < 1.0f) return true;                   // stopped → safe (:4204-4206)

        float tRipcord = RipcordSpeed;                    // GetRipcordSpeed() — the full spin-up time (:4208)
        float v0 = MathF.Sqrt(speed2);
        float vMax = HullType.Speed;                      // GetMaxSpeed()
        float thrust = HullType.Thrust;                   // GetThrust()
        float mass = Mass;                                // GetMass()
        float k = thrust / (vMax * mass);                 // dV/dT = −kV (drag), :4213
        float s = v0 * (1.0f - MathF.Exp(-k * tRipcord)) / k;   // drift distance, :4216
        float tMax = 1.0f + s / v0;                       // effective horizon, "a bit of a fudge" (:4220)

        return !AnyHazardWithin(tMax);                    // state == 0 ⇔ nothing imminent within tMax (:4221-4228)
    }

    /// <summary>P172 — the collision scan for <see cref="IsSafeToRipcord"/>: true
    /// iff any scan candidate (same-side ship, asteroid, or station) in the ship's
    /// own sector has a closest-approach time <c>t ≤ tMax</c> — the port's
    /// concrete realisation of native <c>Dodge(...)</c>'s positive-<c>tMax</c>
    /// branch (<c>common.cpp:1749</c>) over this port's established dodge universe
    /// (the P168/P170 candidate sets). Returns on the first hit; <c>false</c>
    /// (no hazard) means safe to ripcord.</summary>
    private bool AnyHazardWithin(float tMax)
    {
        if (Sector is not { } sector) return false;
        float myRadius = 0.5f * HullType.Length + 1.0f;   // GetRadius()+1 (common.cpp:1706)

        foreach (var other in sector.GetShipUpdateCandidates())
        {
            if (ReferenceEquals(other, this)) continue;
            if (SideId is not { } ms || other.SideId is not { } os || os != ms) continue;   // same-side only (native Ignore)
            float r = 0.5f * other.HullType.Length + myRadius;
            Vector3 rel = other.Position - Position;
            float d2 = rel.LengthSquared();
            float t = d2 > r * r ? SolveForImpact(rel, other.Velocity - Velocity, 0f, r, out _) : 0f;   // :1736-1742
            if (t <= tMax) return true;
        }
        foreach (var rock in sector.Mineable)
        {
            if (rock.Dead) continue;
            if (rock.Data.Position is not { Length: 3 } p) continue;
            var pos = new Vector3(p[0], p[1], p[2]);
            float r = rock.Radius + myRadius;
            Vector3 rel = pos - Position;
            float d2 = rel.LengthSquared();
            float t = d2 > r * r ? SolveForImpact(rel, -Velocity, 0f, r, out _) : 0f;
            if (t <= tMax) return true;
        }
        foreach (var station in sector.StationRuntimes)
        {
            if (station.Dead) continue;
            float r = station.Radius + myRadius;
            Vector3 rel = station.Center - Position;
            float d2 = rel.LengthSquared();
            float t = d2 > r * r ? SolveForImpact(rel, -Velocity, 0f, r, out _) : 0f;
            if (t <= tMax) return true;
        }
        return false;
    }


    /// <summary>
    /// P157/P158 — the cross-cluster leg of <see cref="ApplyNpcAutopilot"/>'s
    /// goal resolution. When the decided target's <c>ClusterId</c> (a station's
    /// <see cref="DataStationIGC.ClusterId"/> or an asteroid's
    /// <see cref="DataAsteroidIGC.ClusterId"/>) differs from the ship's own
    /// <see cref="Sector"/>, resolve the target's <see cref="Sector"/> and
    /// path-find (<see cref="Model.Sector.FindPathToward"/>) the next warp on
    /// the way, returning that warp's world position as the steering goal;
    /// <see cref="Model.Sector.ResolveWarpCollisions"/> then transits the ship
    /// through it automatically once it arrives (Sector.cs:1653) — no new
    /// transit code (the session's "mechanics already built" finding). Because
    /// the goal is re-resolved every tick, a ship that warps mid-journey
    /// naturally re-paths from its new sector on the next tick; once it reaches
    /// the target's sector the same-cluster branch in the caller takes over and
    /// steers directly at the target. P157/P158 scope: the per-tick re-evaluation
    /// <em>is</em> the replan, no separate cache is built.
    /// <para><b>Generalized in P158.</b> P157 built this hardcoded to
    /// <see cref="DataStationIGC"/> (though it only ever read
    /// <c>st.ClusterId</c> — the station's own position is irrelevant here, the
    /// method returns the <em>warp's</em> position, not the final destination's),
    /// so P158 parametrizes it on the bare <c>targetClusterId</c> plus a log
    /// descriptor <c>what</c>, letting the <b>asteroid</b> branch of
    /// <see cref="ApplyNpcAutopilot"/> reuse the identical cross-cluster leg it
    /// previously lacked entirely — that branch had no cross-cluster check or
    /// path-finding at all and would otherwise have steered at a foreign
    /// sector's raw position coordinates as if they were local (a real, silent
    /// bug that only the now-shipped cross-cluster asteroid search would ever
    /// trigger).</para>
    /// </summary>
    /// <remarks>
    /// <para><b>Fallbacks are silent stand-bys, never guesses.</b> If the
    /// mission is unbound, the target sector is absent from it, or no warp path
    /// exists, the method returns <c>null</c> — the caller then leaves the ship
    /// with no goal and does nothing this tick, exactly the pre-P157 no-goal
    /// behavior; the next tick simply tries again (a permanently-unreachable
    /// target yields one logged line, not a thrash).</para>
    /// </remarks>
    private Vector3? CrossClusterWarpGoal(short targetClusterId, string what)
    {
        if (Mission is not { } mission)
        {
            LogNoPath($"no mission — cannot resolve target sector {targetClusterId}");
            return null;
        }

        // The target sector is resolved through this port's existing
        // sectors-by-id lookup (Mission.Sectors searched by Sector.Id — the
        // same shape Server.cs:2748 / Mission.cs:366 use); no new index added.
        var target = mission.Sectors.FirstOrDefault(s => s.Id == targetClusterId);
        if (target is null)
        {
            LogNoPath($"{what} is in sector {targetClusterId}, not present in the mission");
            return null;
        }

        if (Sector is not { } sector || sector.FindPathToward(target) is not { } warp)
        {
            LogNoPath($"no warp path from sector '{Sector?.Name}' to '{target.Name}' ({targetClusterId}) for {what}");
            return null;
        }
        if (warp.Warp.Position is not { Length: 3 } wp)
        {
            LogNoPath($"warp {warp.Warp.WarpId} toward '{target.Name}' has no position");
            return null;
        }

        // A steerable warp goal exists: log the departure once so a sustained
        // cross-cluster journey yields a single line, not one per tick.
        if (!_npcCrossClusterDepartureLogged)
        {
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) — cross-cluster {what} is in sector " +
                              $"'{target.Name}' ({targetClusterId}); steering toward warp {warp.Warp.WarpId} at ({wp[0]:0},{wp[1]:0},{wp[2]:0})");
            _npcCrossClusterDepartureLogged = true;
        }
        _npcCrossClusterNoPathLogged = false;     // re-arm the stand-by log — we found a way this tick
        return new Vector3(wp[0], wp[1], wp[2]);
    }

    /// <summary>P157 — log a cross-cluster stand-by exactly once (transition-guarded
    /// by <see cref="_npcCrossClusterNoPathLogged"/>), so a permanently unreachable
    /// target reports a single clean line instead of a per-tick flood or a silent
    /// stall. Returns the caller's <c>null</c> goal.</summary>
    private void LogNoPath(string why)
    {
        if (!_npcCrossClusterNoPathLogged)
        {
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) — cross-cluster PlanTarget: {why}; standing by (clean no-op, no crash/no thrash)");
            _npcCrossClusterNoPathLogged = true;
        }
        _npcCrossClusterDepartureLogged = false;  // leaving cross-cluster steering
    }

    /// <summary>
    /// P156 — the miner decision cycle, the decision-only remainder of the
    /// autonomous NPC economy loop (everything it dispatches — movement
    /// <see cref="ApplyNpcAutopilot"/>, mining <see cref="Model.Sector.ResolveMining"/>,
    /// selling <c>ServerHost</c>'s dock-sell path — already exists; this method
    /// only picks which of the two already-working halves the ship walks toward
    /// right now). Called for an idle, undocked <see cref="IsNpc"/> ship: when
    /// the bag is not full (<see cref="Ore"/> &lt; <see cref="MineCapacity"/>)
    /// it targets the nearest mineable asteroid
    /// (<see cref="FindNearestMinableAsteroidAnyCluster"/> — widened in P158 to
    /// search other sectors when its own has no rock); when the bag is full
    /// (<see cref="Ore"/> &gt;= <see cref="MineCapacity"/>) it targets the
    /// nearest friendly <see cref="DataStationTypeIGC.SabmUnload"/>-capable
    /// station (<see cref="FindNearestFriendlyStationAnyCluster"/>, likewise
    /// widened — no second station search). Both widenings search the ship's
    /// own sector first (hop 0), so the common local case behaves exactly as
    /// before; the cross-cluster fallback only fires when the own sector has
    /// nothing, closing the loop the P149/P156 same-sector-only searches left
    /// open.
    /// </summary>
    /// <remarks>
    /// <para><b>Mutual exclusion</b> — native models "heading to mine OR
    /// heading to sell" as the single <c>c_cmdPlan</c> pointer, so whenever this
    /// cycle sets one target it clears the other (<see cref="MiningAsteroidTarget"/>
    /// ↔ <see cref="PlanTarget"/>): an NPC is never heading to both at once.</para>
    /// <para><b>Applies to every <see cref="IsNpc"/> ship.</b> This port has no
    /// distinct pilot-type dispatch (<see cref="IsNpc"/> is the single
    /// archetype marker, P148), so the "miner" decision cycle is simply what
    /// every NPC in this port does when idle — the established simplification,
    /// not a new one this task invents.</para>
    /// <para><b>Stale mining target.</b> A <see cref="MiningAsteroidTarget"/> is
    /// obsolete once the bag fills; the caller clears it when full so this cycle
    /// can switch to the sell leg (otherwise the ship would hover at the rock
    /// forever — <see cref="ResolveMining"/> stops accruing at capacity).</para>
    /// </remarks>
    private void DecideMiningOrSelling()
    {
        if (Ore < MineCapacity)
        {
            if (FindNearestMinableAsteroidAnyCluster() is { } rock && rock.Data.Position is { Length: 3 } rp)
            {
                MiningAsteroidTarget = rock;
                PlanTarget = null;                         // mutual exclusion: mining XOR selling (native single c_cmdPlan)
                Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle, ore {Ore:0}/{MineCapacity:0} < cap → mining asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' at ({rp[0]:0},{rp[1]:0},{rp[2]:0})");
            }
            else
            {
                // No mineable rock reachable: stand by (do not spin — the
                // next tick retries naturally since the ship still has no target).
                Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle, ore {Ore:0}/{MineCapacity:0} — no mineable asteroid reachable from sector '{Sector?.Name}', standing by");
            }
        }
        else
        {
            if (FindNearestFriendlyStationAnyCluster(DataStationTypeIGC.SabmUnload) is { } station)
            {
                PlanTarget = station;
                PlanCommandId = OrderChange.CidGoto;       // SetCommand(c_cmdPlan, ..., c_cidGoto) — same tag as take-cover (shipIGC.h:2240)
                MiningAsteroidTarget = null;               // mutual exclusion: mining XOR selling (native single c_cmdPlan)
                Console.WriteLine($"[npc] ship {Id} (side {SideId}) full, ore {Ore:0}/{MineCapacity:0} ≥ cap → selling at station {station.StationId} '{station.Name}' (SabmUnload)");
            }
            else
            {
                // Bag full but no unload-capable friendly station reachable:
                // stand by (do not spin — the next tick retries naturally).
                Console.WriteLine($"[npc] ship {Id} (side {SideId}) full, ore {Ore:0}/{MineCapacity:0} — no unload-capable friendly station reachable from sector '{Sector?.Name}', standing by");
            }
        }
    }

    /// <summary>
    /// P161 — the <see cref="PilotTypeLayer"/> default-order decision: for an
    /// idle, undocked Layer NPC, pick the least-targeted reachable neutral
    /// asteroid (<see cref="FindLeastTargetedAsteroidAnyCluster"/>) and commit
    /// to it via <see cref="LayerAsteroidTarget"/>, which
    /// <see cref="ApplyNpcAutopilot"/> then steers toward. This is the port of
    /// native <c>PickDefaultOrder</c>'s <c>c_ptLayer</c> case
    /// (<c>Igc/shipIGC.h:2417-2434</c>): <c>FindTarget</c> for the least-targeted
    /// neutral asteroid then <c>SetCommand(c_cmdAccepted, pmodel, c_cidGoto)</c>.
    /// It deliberately does <b>not</b> deploy anything once there — matching
    /// native's bounded default-order behavior (the actual mine/probe-laying is
    /// a separate, later deployable-object system; see the explicit exclusion in
    /// NPC_LAYER_DEFAULT_ORDER_P161.md).
    /// </summary>
    /// <remarks>
    /// <para><b>Idle-undocked gate, not native's <c>bDocked</c> gate.</b> Native
    /// breaks out unless <c>bDocked</c> (<c>shipIGC.h:2419-2420</c>) — Layer
    /// ships there are spawned docked and get their first order on launch. This
    /// port's <see cref="Server.SpawnNpcShip"/> places ships flying directly in
    /// a sector (no docking concept at spawn), so the already-established port
    /// convention — the Miner cycle decides when idle and undocked
    /// (<see cref="DecideMiningOrSelling"/>'s
    /// <c>MiningAsteroidTarget is null &amp;&amp; StationId is null</c> gate) —
    /// is followed here: decide when <see cref="LayerAsteroidTarget"/> is null
    /// and undocked (<see cref="StationId"/> null). A deliberate, consistent
    /// deviation from native's literal <c>bDocked</c> gate, stated not silently
    /// dropped.</para>
    /// <para><b>Separation from the Miner cycle.</b> Sets only
    /// <see cref="LayerAsteroidTarget"/> — never <see cref="MiningAsteroidTarget"/>
    /// — so a Layer and a Miner on the same side never contend over the same
    /// target slot (native's single <c>c_cmdPlan</c> pointer distinction is
    /// reflected here as separate per-purpose slots).</para>
    /// </remarks>
    private void DecideLayerDefaultOrder()
    {
        if (FindLeastTargetedAsteroidAnyCluster() is { } rock && rock.Data.Position is { Length: 3 } rp)
        {
            LayerAsteroidTarget = rock;
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle layer (pilotType {PilotType}) → least-targeted asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' at ({rp[0]:0},{rp[1]:0},{rp[2]:0})");
        }
        else
        {
            // No reachable neutral asteroid: stand by (do not spin — the next
            // tick retries naturally since the ship still has no target), the
            // same no-op the Miner cycle's no-rock branch uses.
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle layer — no reachable neutral asteroid from sector '{Sector?.Name}', standing by");
        }
    }

    /// <summary>
    /// P162 — the <see cref="PilotTypeWingman"/> default-order decision: for an
    /// idle, undocked Wingman NPC, pick the nearest enemy ship in its current
    /// sector (<see cref="FindNearestEnemyShip"/>) and commit to it via
    /// <see cref="AttackTarget"/>. This is the port of native
    /// <c>PickDefaultOrder</c>'s <c>c_ptWingman</c> case
    /// (<c>Igc/shipIGC.h:2436-2455</c>): skip the training/co-op
    /// <c>HandlePickDefaultOrder</c> hook (this port has no training-mission
    /// system), go straight to the <c>FindTarget(this, c_ttEnemy | c_ttShip |
    /// c_ttNearest, NULL, pcluster, ...)</c> fallback, and
    /// <c>SetCommand(c_cmdAccepted, pmodel, c_cidAttack)</c>
    /// (<c>shipIGC.h:2441-2447</c>).
    /// </summary>
    /// <remarks>
    /// <para><b>Idle-undocked gate.</b> Follows the already-established port
    /// convention — decide only when fully idle (<see cref="AttackTarget"/>
    /// null) and undocked (<see cref="StationId"/> null) — the same shape the
    /// Miner (<see cref="DecideMiningOrSelling"/>) and Layer
    /// (<see cref="DecideLayerDefaultOrder"/>) cycles use, and a deliberate,
    /// consistent stand-in for native's <c>bDocked</c> gate (this port's NPCs
    /// spawn flying, not docked). Once it commits to an enemy it is no longer
    /// idle, so it does not churn.</para>
    /// <para><b>Same-sector only.</b> <see cref="FindNearestEnemyShip"/> never
    /// leaves the ship's own sector — native passes no <c>c_ttAnyCluster</c>
    /// here (unlike the Miner/Layer searches), so a Wingman does not travel to
    /// find a fight; it only attacks enemies already sharing its sector. A
    /// fleeing enemy that warps out is simply lost (target cleared in
    /// <see cref="UpdateWingmanCombat"/>, stand by).</para>
    /// <para><b>Stated simplifications vs the native combat block.</b> Native's
    /// per-tick combat maneuvering (<c>shipIGC.cpp:2353-2787+</c>) is a large,
    /// sophisticated system — <c>Dodge()</c>, randomized <c>fShootSkill</c>/
    /// <c>fTurnSkill</c> skill tiers, the <c>c_cidDefend</c> protect-an-ally
    /// re-evaluation loop, <c>CanSee</c> sight gating, aim-leading — none of
    /// which this task ports. This Wingman always fires (at a fixed facing/range
    /// condition) with no dodging, no skill randomness, no defend logic, no
    /// shot prediction, and no cross-sector pursuit. Each is an explicit,
    /// separately-scoped exclusion; see NPC_WINGMAN_ATTACK_FOUNDATION_P162.md.</para>
    /// </remarks>
    private void DecideWingmanDefaultOrder()
    {
        if (FindNearestEnemyShip() is { } enemy)
        {
            AttackTarget = enemy;
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle wingman (pilotType {PilotType}) → attacking enemy ship {enemy.Id} (side {enemy.SideId}) at ({enemy.Position.X:0},{enemy.Position.Y:0},{enemy.Position.Z:0})");
        }
        else
        {
            // No enemy in this sector: stand by (do not spin — the next tick
            // retries naturally since the ship still has no target), the same
            // no-op the Miner's/Layer's no-target branches use.
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle wingman — no enemy ship in sector '{Sector?.Name}', standing by");
        }
    }

    /// <summary>
    /// P165 — the <see cref="PilotTypeBuilder"/> default-order decision: for an
    /// idle, undocked Builder NPC with a configured
    /// <see cref="BuilderStationType"/>, pick the least-targeted reachable
    /// neutral asteroid (<see cref="FindLeastTargetedAsteroidAnyCluster"/>) and
    /// actually <b>start a build</b> on it via
    /// <see cref="Model.Sector.StartBuildingEffect"/>. This is the port of
    /// native <c>PickDefaultOrder</c>'s <c>c_ptBuilder</c> case
    /// (<c>Igc/shipIGC.h:2388-2415</c>): <c>FindTarget</c> for the least-
    /// targeted neutral asteroid then <c>SetCommand(c_cmdAccepted, pmodel,
    /// c_cidBuild)</c>. Unlike the Layer case (which only travels to the rock),
    /// a Builder commits to an actual construction — the loop this task closes,
    /// letting Stage 1's (P163) mechanic trigger autonomously instead of by a
    /// test calling <see cref="Model.Sector.StartBuildingEffect"/> directly.
    /// </summary>
    /// <remarks>
    /// <para><b>Identical search to the Layer case, reused not reimplemented.</b>
    /// Native's <c>c_ptBuilder</c> (<c>shipIGC.h:2388-2415</c>) and
    /// <c>c_ptLayer</c> (<c>shipIGC.h:2417-2434</c>) cases share the exact
    /// <c>FindTarget</c> shape — <c>c_ttNeutral | c_ttAsteroid | c_ttNearest |
    /// c_ttLeastTargeted | c_ttAnyCluster | c_ttCowardly</c> — differing only in
    /// the trailing ability-bitmask argument (<c>m_abmOrders</c> for Builder, a
    /// a plain <c>0</c> for Layer). P280 added the optional
    /// <c>requiredAabm</c> mask to
    /// <see cref="FindLeastTargetedAsteroidAnyCluster"/> so the Builder filters
    /// contenders to rocks matching its station type's
    /// <see cref="DataStationTypeIGC.AabmBuild"/> (native
    /// <c>FindTarget(m_abmOrders)</c>, shipIGC.h:2393-2397), while the Layer
    /// passes <c>null</c> and stays unfiltered (native's <c>0</c>).</para>
    /// <para><b>Configured station type, not discovered.</b> Decides only when
    /// <see cref="BuilderStationType"/> is non-null; if it is null the Builder
    /// has nothing to build and stands by (logged), matching the no-target
    /// no-op precedent of <see cref="DecideMiningOrSelling"/> /
    /// <see cref="DecideLayerDefaultOrder"/> / <see cref="DecideWingmanDefaultOrder"/>.</para>
    /// <para><b>Idle-undocked gate, mirroring the other cycles.</b> Decides only
    /// when not already building (<c>!<see cref="IsBuilding"/></c>) and undocked
    /// (<see cref="StationId"/> null) — the same shape the Miner
    /// (<see cref="DecideMiningOrSelling"/>) / Layer (<see cref="DecideLayerDefaultOrder"/>) /
    /// Wingman (<see cref="DecideWingmanDefaultOrder"/>) cycles use, and a
    /// deliberate, consistent stand-in for native's <c>bDocked</c> gate (this
    /// port's NPCs spawn flying, not docked). Once a build is registered
    /// <see cref="IsBuilding"/> becomes <c>true</c>, so the ship is no longer
    /// idle and does not churn.</para>
    /// <para><b>Edge case explicitly out of scope.</b> Native's
    /// <c>m_abmOrders == c_aabmBuildable → c_cidGoto</c> special case
    /// (<c>shipIGC.h:2390,2403</c>) has no clean port given the ability-mask
    /// simplification above; a Builder here always starts a real build when it
    /// finds a target, never a plain Goto.</para>
    /// </remarks>
    private void DecideBuilderDefaultOrder()
    {
        if (BuilderStationType is not { } stationType)
        {
            // No configured station type: nothing to build — stand by (do not
            // spin; the next tick retries naturally since IsBuilding is false),
            // the same no-op the other decision cycles' no-config branches use.
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle builder (pilotType {PilotType}) — no configured BuilderStationType, standing by");
            return;
        }
        // A build registers against the ship's sector; a placed (ticking) ship
        // is always in one, but be explicit/defensive like the other cycles.
        if (Sector is not { } sector)
        {
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle builder — no sector, cannot build, standing by");
            return;
        }

        if (FindLeastTargetedAsteroidAnyCluster(stationType.AabmBuild) is { } rock && rock.Data.Position is { Length: 3 } rp)
        {
            sector.StartBuildingEffect(this, rock, stationType, sector.LastUpdate);
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle builder (pilotType {PilotType}) → starting build of '{stationType.Name}' (type {stationType.StationTypeId}) at asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' at ({rp[0]:0},{rp[1]:0},{rp[2]:0})");
        }
        else
        {
            // No reachable neutral asteroid: stand by (do not spin — the next
            // tick retries naturally since the ship still has no build), the
            // same no-op the Miner's/Layer's no-rock branches use.
            Console.WriteLine($"[npc] ship {Id} (side {SideId}) idle builder — no reachable neutral asteroid from sector '{Sector?.Name}', standing by");
        }
    }

    /// <summary>
    /// P162 — the Wingman's per-tick combat leg, the counterpart of the
    /// decision in <see cref="DecideWingmanDefaultOrder"/>: (1) validate the
    /// current <see cref="AttackTarget"/> and clear it when it is no longer a
    /// valid same-sector enemy, and (2) decide whether to hold the trigger and
    /// set/clear the <c>AllWeapons</c> <see cref="StateM"/> bit that the
    /// already-built <see cref="Weapon.Update"/>/<see cref="Weapon.FireWeapon"/>
    /// mechanic checks (<see cref="Weapon.IsWeaponSelected"/>,
    /// Weapon.cs:363-375). Runs every tick <em>before</em> the weapon loop in
    /// <see cref="OnUpdate"/> so the fire decision is in place for that tick's
    /// fire. Called only for <see cref="IsNpc"/> <see cref="PilotTypeWingman"/>
    /// ships.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><b>Target validity — cleared, never pursued across sectors.</b> The
    /// target is dropped when it dies, docks (<see cref="StationId"/> non-null),
    /// leaves this ship's sector, or is no longer a different-sided enemy — the
    /// scope's same-sector-only contract (no cross-sector pursuit, matching
    /// native's same-sector-only selection). When cleared, the trigger is
    /// released and <see cref="DecideWingmanDefaultOrder"/> will re-pick on the
    /// next idle tick.</item>
    /// <item><b>The fire decision reuses the already-working mechanic.</b>
    /// Nothing here modifies <c>Weapon.cs</c>: the weapon already fires whenever
    /// the mounting ship's <see cref="StateM"/> carries the weapon-selection bit
    /// (<c>AllWeapons</c>, Weapon.cs:372) — the exact input a real player's
    /// client streams. This leg only sets that bit when the target is alive,
    /// same-sector, within the mounted weapon's effective range
    /// (<see cref="EstimateWeaponRange"/>), and the nose is within
    /// <see cref="WingmanFireFacingCosine"/> of the target — the
    /// CosForward-style <c>forward·dir</c> dot-product the P155 turn law already
    /// uses (<c>−Orientation.TimesInverse(dir).Z</c>, Ship.cs:2545/2588) — and
    /// clears it when any condition stops holding. No aim-leading: fire at the
    /// target's current position, no velocity-lead calculation.</item>
    /// </list>
    /// </remarks>
    private void UpdateWingmanCombat()
    {
        // (a) Release the trigger when there is no target or it is no longer a
        //     valid same-sector enemy. Keep the release unconditional at the
        //     top so a stale target can never leave the bit latched.
        if (AttackTarget is not { } target
            || target.Dead
            || target.StationId is not null
            || target.Sector != Sector
            || SideId is null || target.SideId is null || target.SideId == SideId)
        {
            if (AttackTarget is not null)
            {
                Console.WriteLine($"[npc] ship {Id} — dropping attack target {AttackTarget.Id}: " +
                                  $"{(AttackTarget.Dead ? "dead" : AttackTarget.StationId is not null ? "docked" : AttackTarget.Sector != Sector ? "left sector" : "no longer an enemy")}");
                AttackTarget = null;
            }
            StateM &= ~ShipControlState.AllWeapons;
            return;
        }

        // (b) Fire decision: within weapon range AND facing the AIM direction
        // within tolerance → select all weapons; otherwise release.
        float distance = (target.Position - Position).Length();

        // Aim direction for the fire check. Native LEADS the shot:
        // solveForLead() predicts where to point so the round intercepts the
        // moving target (shipIGC.cpp:2463-2468), and the fire gate is the
        // angle to that AIM POINT, not the target's raw current position
        // (turnToFace(direction), shipIGC.cpp:2470-2476). Mirror that here:
        // when a weapon is mounted, aim at the lead direction; only with NO
        // mounted weapon does this fall back to P162's facing-the-raw-position
        // check (no lead to solve for an unmounted ship — the same empty-
        // weapons safety EstimateWeaponRange already exercises for range).
        // Crucially this changes ONLY the fire gate's aim reference; the
        // pursuit steering (SteerToward, ApplyNpcAutopilot) still flies at the
        // target's LIVE position to close/keep range — the honest, stated
        // decoupling (native turns the WHOLE ship to the lead vector via
        // turnToFace; this port keeps approach steering as-is and only
        // re-checks facing against the lead direction for the shoot decision).
        Vector3 aim = target.Position - Position;             // raw-position fallback (P162, weapon-less)
        if (Weapons.Count > 0)
        {
            // Native leads with the FIRST mounted weapon (m_mountedWeapons[0],
            // shipIGC.cpp:2353,2466) — mirror that. A solved direction is a
            // unit vector; the no-intercept branch leaves it Zero
            // (SolveForImpact returns its mmf sentinel and direction = default,
            // Ship.cs:2919), in which case we fall back to raw facing rather
            // than aim at the zero vector (native's *direction is left UNSET in
            // that branch, common.cpp:91 — undefined behaviour this port
            // declines to reproduce; a target no longer reachable is no longer
            // worth leading at anyway).
            SolveForLead(target, Weapons[0], WingmanShootSkill, out Vector3 leadDir);
            if (leadDir.LengthSquared() > 1e-6f)
                aim = leadDir;
        }

        float toLen = aim.Length();
        float facing = toLen > 1e-6f
            ? -Orientation.TimesInverse(aim / toLen).Z      // forward·dir (P155 metric)
            : 1f;                                                 // dead-centre: "facing"
        if (distance <= EstimateWeaponRange() && facing >= WingmanFireFacingCosine)
            StateM |= ShipControlState.AllWeapons;
        else
            StateM &= ~ShipControlState.AllWeapons;
    }

    /// <summary>
    /// P166 — the defending Wingman's per-tick decision leg, the counterpart of
    /// <see cref="DecideWingmanDefaultOrder"/> for a ship whose
    /// <see cref="DefendTarget"/> is set (the port of native's <c>c_cidDefend</c>
    /// logic, <c>Igc/shipIGC.cpp:2373-2407</c>, plus its companion acquisition
    /// block <c>shipIGC.cpp:2680-2705</c>). Owns the whole defend life cycle:
    /// when the Wingman has no valid <see cref="AttackTarget"/> it escorts the
    /// charge and scans for an enemy within 1500 of the charge to engage
    /// (Part A — acquisition); when it is attacking, it re-evaluates every tick
    /// whether to keep the current target, switch to a closer threat near the
    /// charge, or fall back to escorting (Part B — the cited re-evaluation
    /// block). Mutates only <see cref="AttackTarget"/>; the pursuit/fire
    /// mechanics stay entirely P162's <see cref="UpdateWingmanCombat"/>
    /// (unchanged).
    /// </summary>
    /// <remarks>
    /// <list type="number">
    /// <item><b>Every-tick re-evaluation (200-tick-cooldown simplification).</b>
    /// Native gates the block behind <c>m_checkCooldown = 200</c>
    /// (<c>shipIGC.cpp:2406</c>) — a pure performance optimization, not a
    /// behavioral requirement. This port re-evaluates every tick (its established
    /// per-tick <c>OnUpdate</c> cadence); checking more often only ever produces
    /// the same-or-better decision, never a worse one. Accepted, harmless.</item>
    /// <item><b>Charge-centered search.</b> Both scans center on the charge's
    /// LIVE position (<see cref="FindNearestEnemyNear"/>), not the Wingman's own
    /// — matching native's <c>FindTargetShip(..., &amp;chargePos, dist,
    /// c_ttEnemy|c_ttNearest, -2)</c> (<c>shipIGC.cpp:2384,2690</c>).</item>
    /// <item><b>Cross-sector charge → disengage.</b> Native's
    /// <c>GetCluster() != charge->GetCluster()</c> → shouldReturn
    /// (<c>shipIGC.cpp:2376-2377</c>). This port is same-sector-only (P162), and
    /// in this task's tests the charge always sits in the Wingman's sector, so it
    /// never fires there; the guard is kept defensively — if it ever does, the
    /// attack target is cleared and, the charge being unreachable across sectors,
    /// the Wingman holds position (the <see cref="ApplyNpcAutopilot"/> escort
    /// branch requires <c>charge.Sector == Sector</c>). Stated plainly, not
    /// silently dropped.</item>
    /// <item><b>"Just running away" reference.</b> Native measures the chased
    /// target's velocity along <c>Orientate(chargePos − c_cmdCurrentPos)</c>
    /// (<c>shipIGC.cpp:2393-2395</c>); <c>c_cmdCurrent</c> is the Wingman's own
    /// current-position reference in this context, so the port uses
    /// <c>normalize(charge.Position − Position)</c> — the component of the
    /// target's velocity toward the charge. Below −30 the target is fleeing the
    /// guarded area → return to escort.</item>
    /// </list>
    /// </remarks>
    private void UpdateWingmanDefend()
    {
        if (DefendTarget is not { } charge) return;

        // Cross-sector charge: disengage. (Native: GetCluster() !=
        // charge->GetCluster() → shouldReturn, shipIGC.cpp:2376-2377. Same-sector
        // only in this port, so with the charge unreachable the disengaged
        // Wingman holds position — the ApplyNpcAutopilot escort branch requires
        // charge.Sector == Sector, yielding no goal.)
        if (charge.Sector != Sector)
        {
            if (AttackTarget is not null)
            {
                Console.WriteLine($"[npc] ship {Id} — defending {charge.Id} left sector; disengaging target {AttackTarget.Id}");
                AttackTarget = null;
            }
            return;
        }

        // Escorting (no valid current attack target): try to acquire a threat
        // within WingmanDefendAcquireRadius of the charge. This is native's
        // c_cidDefend plan-slot scan (shipIGC.cpp:2689-2695) — when the Wingman
        // is guarding (plan == c_cidDefend) it hunts enemies near the charge,
        // not near itself.
        if (AttackTarget is not { } target
            || target.Dead
            || target.StationId is not null
            || target.Sector != Sector
            || SideId is null || target.SideId is null || target.SideId == SideId)
        {
            if (AttackTarget is not null)
            {
                Console.WriteLine($"[npc] ship {Id} — defending {charge.Id}; dropped stale attack target {AttackTarget.Id}");
                AttackTarget = null;
            }
            float dist = WingmanDefendAcquireRadius;                 // 1500 (shipIGC.cpp:2689)
            if (FindNearestEnemyNear(charge.Position, ref dist) is { } threat)
            {
                AttackTarget = threat;                               // SetCommand(c_cmdPlan, pTarget, c_cidAttack) (:2695)
                Console.WriteLine($"[npc] ship {Id} — escorting {charge.Id}; engaging threat {threat.Id} at {dist:0}u");
            }
            return;
        }

        // Attacking a valid enemy — the native c_cidDefend re-evaluation
        // (shipIGC.cpp:2373-2407). Decide each tick: keep chasing, switch to a
        // closer threat near the charge, or fall back to escorting.
        bool shouldReturn = false;
        float distToProtectee = (charge.Position - target.Position).Length();   // (:2379)
        if (distToProtectee > WingmanDefendAbandonDistance)                      // > 3500 → (:2380)
        {
            shouldReturn = true;
        }
        else if (distToProtectee > WingmanDefendEvaluateDistance)                // > 2200 → (:2382)
        {
            float dist = WingmanDefendScanRadius;                                // 2500 search radius, in/out (:2383)
            if (FindNearestEnemyNear(charge.Position, ref dist) is { } closer
                && dist < (distToProtectee - WingmanDefendSwitchMargin))         // dist < distToProtectee − 1000 (:2385)
            {
                AttackTarget = closer;                                           // switch to the closer threat (:2389)
                Console.WriteLine($"[npc] ship {Id} — defending {charge.Id}; switching to closer threat {closer.Id} at {dist:0}u");
            }
            else
            {
                // don't follow if he is just running away (:2391-2396)
                Vector3 toCharge = charge.Position - Position;
                float len = toCharge.Length();
                Vector3 dir = len > 1e-6f ? toCharge / len : Vector3.Zero;
                float targetDirSpeed = dir * target.Velocity;                    // oToDefendee.CosForward(vel)·|vel| (:2393-2394)
                if (targetDirSpeed < WingmanDefendAbandonRunawaySpeed)           // < −30 → (:2395)
                    shouldReturn = true;
            }
        }
        if (shouldReturn)
        {
            // Fall back to escorting (:2400-2404: SetCommand(c_cmdPlan, charge,
            // c_cidDefend)). Clearing AttackTarget IS the port's "return to
            // escort": the next ApplyNpcAutopilot resolves no combat goal and the
            // DefendTarget branch steers toward the charge instead.
            Console.WriteLine($"[npc] ship {Id} — defending {charge.Id}; returning to escort (gave up chasing {target.Id})");
            AttackTarget = null;
        }
    }

    /// <summary>
    /// P159 — real-time fair-share arbitration among same-side miners targeting
    /// the exact same asteroid, the port of the "Special case up front: miners
    /// mine" block of native <c>CshipIGC::PlotShipMove</c>
    /// (<c>Igc/shipIGC.cpp:2088-2204</c>, which runs every tick a miner holds
    /// <c>wantsToMineMaskIGC</c>). For a ship with a non-null
    /// <see cref="MiningAsteroidTarget"/>, it surveys every other undocked NPC
    /// ship in the same sector sharing the <em>identical</em>
    /// <see cref="MiningAsteroidTarget"/> reference; <b>same-side</b>
    /// contenders (<see cref="SideId"/> match) are arbitrated by least
    /// <see cref="Ore"/>, while a <b>cross-side</b> contender (a different
    /// <see cref="SideId"/> — an enemy, this port's no-alliance convention)
    /// triggers the P167 lockout that evicts <em>every</em> contender on both
    /// sides. For the same-side case: when more than one exists, the contender
    /// currently carrying the least <see cref="Ore"/> keeps the rock and every
    /// other contender has its <see cref="MiningAsteroidTarget"/> cleared and
    /// is redirected to the nearest friendly
    /// <see cref="DataStationTypeIGC.SabmUnload"/> station — the exact same
    /// "go sell" redirect the full-bag branch of
    /// <see cref="DecideMiningOrSelling"/> uses — to dispose of whatever it
    /// already holds.
    /// </summary>
    /// <remarks>
    /// <para><b>Real-time arbitration, not avoidance-at-selection-time.</b>
    /// Matching native, two same-side NPCs may still both initially pick the
    /// same rock (<see cref="DecideMiningOrSelling"/> has no search-time
    /// collision avoidance either); the fairness is enforced continuously
    /// afterward — whichever contender currently has the least ore keeps
    /// mining, every other is peeled off to go sell. Because it runs <b>every
    /// tick</b> a ship holds a target (see the call site in
    /// <see cref="OnUpdate"/>), a later-arriving poorer contender is honoured
    /// as soon as its presence is observable, not only at the instant the
    /// target was picked.</para>
    /// <para><b>Winner grant is implicit.</b> <see cref="Model.Sector.ResolveMining"/>
    /// accrues ore purely geometrically for any ship parked beside a
    /// <c>CanMineHe3</c> rock, so "keeping the right to mine" is simply the
    /// loser being removed from the rock — the native's explicit
    /// <c>pshipMin-&gt;SetStateBits(miningMaskIGC|wantsToMineMaskIGC,
    /// miningMaskIGC)</c> (<c>shipIGC.cpp:2185</c>) has no separate mask to set
    /// here, so the winner needs no write beyond retaining its
    /// <see cref="MiningAsteroidTarget"/>.</para>
    /// <para><b>Redirect-to-sell, not to a second rock.</b> A displaced
    /// contender's <see cref="MiningAsteroidTarget"/> is cleared and it is sent
    /// toward the nearest friendly unload station — the native's
    /// <c>SetStateBits(miningMaskIGC|wantsToMineMaskIGC,0)</c> then
    /// <c>FindTarget(..., c_sabmUnload|c_sabmLand)</c> +
    /// <c>SetCommand(c_cmdPlan, pmodel, c_cidGoto)</c>
    /// (<c>shipIGC.cpp:2156-2168</c>). It does <b>not</b> look for a different
    /// asteroid. If no unload-capable station is reachable it stands by (the
    /// native <c>SetCommand(c_cmdAccepted, NULL, c_cidNone)</c> else-branch,
    /// <c>shipIGC.cpp:2174-2178</c>), logged and non-crashing exactly like the
    /// stand-by in <see cref="DecideMiningOrSelling"/>.</para>
    /// <para><b>Tie-break.</b> "Least ore" uses the native's strict
    /// <c>fOre &lt; fOreMin</c> replace (<c>shipIGC.cpp:2121</c>): on an ore
    /// tie the <em>first</em> contender encountered in
    /// <see cref="Model.Sector.GetShipUpdateCandidates"/> order (stable
    /// <c>_models</c> insertion order) keeps the rock. Matched here; no reason
    /// to deviate.</para>
    /// <para><b>Cross-side lockout (P167).</b> Native's <c>nEnemy &gt; 0</c> branch
    /// (<c>shipIGC.cpp:2187-2203</c>) is now implemented: when a ship with a
    /// different <see cref="SideId"/> — an "enemy", this port's established
    /// no-alliance convention — also wants the exact same rock, this method
    /// skips the same-side least-ore/redirect-to-sell resolution entirely and
    /// evicts <em>every</em> contender on <em>both</em> sides: each has its
    /// <see cref="MiningAsteroidTarget"/> cleared with <b>no</b> station
    /// redirect (unlike the same-side loser path, which redirects to sell).
    /// Native's eviction loop has no side filter and never calls the unload
    /// redirect; evicted ships simply stand idle and their own next tick's
    /// <see cref="DecideMiningOrSelling"/> re-decides from the cleared state,
    /// possibly re-picking the same or a different rock if nothing else
    /// changed (the honest, faithful outcome). P159's own report named this
    /// branch and deferred it; P167 closes that gap. Real (non-NPC) player
    /// ships are likewise never arbitrated — the call site is gated on
    /// <see cref="IsNpc"/> — matching native's <c>c_ptMiner</c> +
    /// <c>SetAutopilot(true)</c> gate (<c>shipIGC.cpp:2061</c>, 2088).</para>
    /// </remarks>
    private void ArbitrateAsteroidContention()
    {
        if (Sector is not { } sector) return;
        if (MiningAsteroidTarget is not { } rock) return;
        if (SideId is not { } sideId) return;

        // Physically-present undocked ships in this sector. Reused deliberately
        // rather than adding a new accessor: GetShipUpdateCandidates() is the
        // faithful port of native's pcluster->GetShips() membership (undocked,
        // non-boarded, non-dead — Sector.cs:290-302), which is exactly the set
        // native's own arbitration loop iterates (shipIGC.cpp:2106). The name
        // is network-flavoured but the selection is cluster-physical membership,
        // a legitimate dual-purpose read, so no parallel _models walk is added.
        var ships = sector.GetShipUpdateCandidates();

        // Count same-side contenders sharing the EXACT asteroid reference (this
        // port's MiningAsteroidTarget is an Asteroid reference — Ship.cs:666 —
        // so "the same rock" means the identical object, mirroring native's
        // pointer comparison m_commandTargets[c_cmdPlan] equality,
        // shipIGC.cpp:2113). Track the contender with the least ore (strict <,
        // shipIGC.cpp:2121: first-found wins a tie). THIS ship is counted like
        // any other, so if it is the richest contender it redirects itself.
        Ship? pshipMin = null;
        float fOreMin = float.MaxValue;
        int nFriendly = 0;
        int nEnemy = 0;
        foreach (var ship in ships)
        {
            if (ship.MiningAsteroidTarget != rock) continue;   // same target pointer (shipIGC.cpp:2113)
            if (ship.SideId != sideId)                         // a DIFFERENT side = enemy — no alliance system in this port (shipIGC.cpp:2115)
            {
                nEnemy++;                                      // native counts these separately (nEnemy, shipIGC.cpp:2117)
                continue;                                      // an enemy is never a least-ore candidate for the same-side grant
            }
            nFriendly++;
            if (ship.Ore < fOreMin) { fOreMin = ship.Ore; pshipMin = ship; }
        }

        // P167 — cross-side lockout, native's nEnemy > 0 branch (shipIGC.cpp:2187-2203).
        // When an ENEMY-side ship also wants this exact rock, NOBODY on either side
        // gets to mine it: skip the same-side least-ore/redirect-to-sell resolution
        // entirely and evict EVERY contender (both sides) sharing this asteroid
        // reference — clear MiningAsteroidTarget on each with NO station redirect
        // (unlike the same-side loser path just above). Native's loop has no side
        // filter (pship is any cluster ship wanting this rock) and never calls the
        // FindNearestFriendlyStationAnyCluster sell-redirect; evicted ships simply go
        // idle and their own next tick's DecideMiningOrSelling naturally re-decides
        // from the cleared state — possibly re-picking the same or a different rock
        // if nothing else changed (the honest, faithful outcome, not a bug).
        if (nEnemy > 0)
        {
            foreach (var ship in ships)
            {
                if (ship.MiningAsteroidTarget != rock) continue;   // every contender, both sides — no side filter (shipIGC.cpp:2191-2203)
                ship.MiningAsteroidTarget = null;                  // SetStateBits(miningMask|wantsToMine,0) — nobody wins at all (shipIGC.cpp:2191)
                Console.WriteLine($"[npc] ship {ship.Id} (side {ship.SideId}) denied asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' — enemy contender present, cross-side lockout evicts every contender, no redirect");
            }
            return;                                                // never falls through to the same-side least-ore grant
        }

        if (nFriendly <= 1) return;   // nobody else wants this rock — keep mining, nothing to arbitrate

        // More than one same-side contender: only pshipMin (least ore) keeps the
        // right to mine; every other loser is cleared of the mining target and
        // redirected to go sell (shipIGC.cpp:2137-2185).
        foreach (var ship in ships)
        {
            if (ship == pshipMin) continue;                    // the winner keeps mining
            if (ship.MiningAsteroidTarget != rock) continue;
            if (ship.SideId != sideId) continue;

            ship.MiningAsteroidTarget = null;                  // SetStateBits(miningMask|wantsToMine,0), shipIGC.cpp:2156
            var station = ship.FindNearestFriendlyStationAnyCluster(DataStationTypeIGC.SabmUnload);
            if (station is { } st)
            {
                ship.PlanTarget = st;                          // SetCommand(c_cmdPlan, station, c_cidGoto), shipIGC.cpp:2168
                ship.PlanCommandId = OrderChange.CidGoto;
                Console.WriteLine($"[npc] ship {ship.Id} (side {ship.SideId}) displaced from asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' by same-side lower-ore miner {pshipMin?.Id} — going to sell at unload station {st.StationId} '{st.Name}'");
            }
            else
            {
                // No unload-capable friendly station reachable: stand by (native
                // SetCommand(c_cmdAccepted, NULL, c_cidNone), shipIGC.cpp:2174-2178),
                // matching DecideMiningOrSelling's no-station stand-by.
                ship.PlanTarget = null;
                ship.PlanCommandId = OrderChange.CidNone;
                Console.WriteLine($"[npc] ship {ship.Id} (side {ship.SideId}) displaced from asteroid {rock.Data.AsteroidId} '{rock.Data.Name}' — no unload station reachable, standing by");
            }
        }
    }

    /// <summary>
    /// The sector-tick hook (called via <see cref="Model.Update"/> once per
    /// chunk). Runs the flight model for the elapsed <c>dt</c> —
    /// <see cref="ExecuteMove"/> — then integrates the C++-owned position step
    /// <c>Δposition = velocity·ΔT</c>, which <c>ExecuteShipMove</c> deliberately
    /// does <b>not</b> do (<c>CalculateShip</c>, <c>shipIGC.cpp:3273-3286</c>;
    /// the mission/chunk driver owns it in the reference). Integrating here is
    /// the minimal honest spot: the whole <c>Move()</c> pass lives in the
    /// out-of-scope collision block (<c>clusterIGC.cpp:338-346</c>), and without
    /// a position step a sector tick would not move a ship at all. Physics is
    /// unchanged — <c>ExecuteMove</c> is called verbatim with the same elapsed
    /// time it would receive as a standalone move, so velocity/orientation match
    /// a direct call exactly (the measured split-vs-single divergence is the
    /// mission cadence, not this wrapper).
    /// </summary>
    /// <summary>
    /// P269 — the mounted ET_Cloak@0 part (the only slot a cloak engages from:
    /// native <c>CcloakIGC::fActive() = (m_mountID == 0)</c>, cloakigc.h:66-69,
    /// and Update asserts <c>m_mountID == 0</c>, cloakigc.cpp:62). Null when none
    /// is mounted, so both the signature offset and the per-tick cloak sim no-op
    /// on a ship that never carried a cloak.
    /// </summary>
    private MountedPart? _mountedCloak =>
        _parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Cloak && p.MountId == 0);

    /// <summary>
    /// P269 — the per-tick cloak state machine, the port of <c>CcloakIGC::Update</c>
    /// (<c>cloakigc.cpp:59-123</c>), driven from <see cref="OnUpdate"/> exactly
    /// where native <c>CshipIGC::Update</c> calls its mounted parts' Update.
    /// Faithful to the native sequence: activation is a held/toggled
    /// <c>cloakActiveIGC</c> state bit gated by "not docked, not boarded, not
    /// ripcording" (the SetStateM guards, shipIGC.cpp:1595-1601); while the
    /// emitter is on it drains <c>EnergyConsumption·dt</c> energy, throttling the
    /// cloak's target strength quadratically as energy runs short, and ramps the
    /// ship's visible fraction toward <c>1 − MaxCloaking</c> at <c>OnRate</c>
    /// (decloaking back at <c>OffRate</c>), deactivating fully once it ramps to
    /// zero.
    /// </summary>
    private void UpdateCloak(float dt)
    {
        var cloak = _mountedCloak;
        if (cloak is null || cloak.Type.Type is not DataPartTypeIGC.Cloak type)
        {
            // No usable cloak — if we were cloaked, restore fully visible
            // (native clears cloakActiveIGC when the cloak is unmounted,
            // shipIGC.h:1839-1840).
            if (_cloakEmitterActive)
            {
                _cloakEmitterActive = false;
                _cloakLevel = 0.0f;
                Cloaking = 1.0f;
            }
            return;
        }

        // bActive = the cloak-active control bit AND fit-to-cloak (native
        // cloakigc.cpp:80-81: (stateM & cloakActiveIGC) && mountedFraction >= 1).
        // The mountedFraction>=1 term collapses to "the part is present" (the
        // loadout buys parts mounted-ready; the native mounting ramp is a
        // separate notification-only concern not ported here). The docked/parent/
        // ripcord gates mirror SetStateM (shipIGC.cpp:1595-1601) refusing the bit
        // while docked/boarded/ripped.
        bool bActive = (StateM & ShipControlState.CloakActive) != 0
                       && StationId is null
                       && ParentShip is null
                       && !IsRipcordActive;
        if (bActive)
            ActivateCloak(type);

        if (_cloakEmitterActive)
        {
            // maxCloaking = the cloak's ceiling while requested, 0 while decloaking.
            float maxCloaking = bActive ? type.MaxCloaking : 0.0f;

            // Energy drain (cloakigc.cpp:89-103): every active tick costs
            // EnergyConsumption·dt; a short pool throttles the cloak's target
            // strength by r² (r = available/needed) and floors the pool at 0.
            float energyNeeded = type.EnergyConsumption * dt;
            float energy = Energy;
            if (energy >= energyNeeded)
                energy -= energyNeeded;
            else
            {
                float r = energy / energyNeeded;
                maxCloaking *= r * r;
                energy = 0.0f;
            }
            Energy = energy;

            // Ramp toward the target cloak strength (cloakigc.cpp:105-116).
            if (maxCloaking > _cloakLevel)
            {
                _cloakLevel += dt * type.OnRate;
                if (_cloakLevel > maxCloaking)
                    _cloakLevel = maxCloaking;
            }
            else
            {
                _cloakLevel -= dt * type.OffRate;
                if (_cloakLevel < maxCloaking)
                    _cloakLevel = maxCloaking;
            }

            // Fully decloaked -> release the emitter (cloakigc.cpp:118-119);
            // otherwise publish the visible fraction (cloakigc.cpp:121).
            if (!bActive && _cloakLevel == 0.0f)
                DeactivateCloak();
            else
                Cloaking = 1.0f - _cloakLevel;
        }
    }

    /// <summary>P269 — engage the cloak emitter: mark it active and reset the
    /// ramp to <c>0</c> (native <c>Activate</c> also adds the cloak's signature
    /// via <c>ChangeSignature(+signature)</c>, cloakigc.h:76 — that contribution
    /// is realised lazily in <see cref="EffectiveSignature"/> rather than as a
    /// mutable base, which is observationally identical for the port's read-only
    /// signature surface). The native asserted invariant <c>GetCloaking() == 1.0</c>
    /// (cloakigc.h:80) holds here because the ramp starts from 0.</summary>
    private void ActivateCloak(DataPartTypeIGC.Cloak type)
    {
        if (_cloakEmitterActive) return;
        _cloakEmitterActive = true;
        _cloakLevel = 0.0f;
        Cloaking = 1.0f;
    }

    /// <summary>P269 — release the cloak emitter: restore full visibility
    /// (native <c>SetCloaking(1.0)</c>, cloakigc.h:92; the matching
    /// <c>ChangeSignature(−signature)</c>, cloakigc.h:89, drops out of
    /// <see cref="EffectiveSignature"/> when <see cref="_cloakEmitterActive"/>
    /// clears).</summary>
    private void DeactivateCloak()
    {
        _cloakEmitterActive = false;
        _cloakLevel = 0.0f;
        Cloaking = 1.0f;
    }

    protected override void OnUpdate(double now, double dt)
    {
        // P279 — the drifting Lifepod's oxygen timer. Direct port of native
        // shipIGC.cpp:361-363: `if (lifepod hull && (now > m_timeToDie))
        // KillShipEvent(now, this, NULL, 1.0f, GetPosition(), Vector::GetZero())`.
        // Re-enters the ESTABLISHED death path (BroadcastKillShip →
        // bLifepod ⇒ bKilled ⇒ KILL_SHIP + SHIP_DELETE + the P105 respawn at a
        // friendly station) — the oxygen expiry is not a new mechanism, it just
        // reuses the existing destroy+respawn the port already has. Runs before
        // the flight/weapon steps so an oxygen-dead pod doesn't also manoeuvre
        // this tick.
        //
        // The `StationId is null` conjunct is the port's "timer-cancel" for scope
        // item 4: once the pod is DOCKED at a base — by being rescued (the
        // PerformRescue path sets StationId) or as the death-respawned lifepod —
        // it is safe and oxygen no longer burns. A rescued pod's countdown is
        // thereby implicitly cancelled the instant it docks; it cannot re-fire
        // while berthed.
        if (HullType.HasCapability(DataHullTypeIGC.HabmLifepod) &&
            StationId is null &&
            LifepodTimeToDie is { } deadline &&
            now > deadline)
        {
            Console.WriteLine($"[oxygen] lifepod ship {Id} oxygen exhausted at sim-t {now:R} (deadline {deadline:R}) — KillShipEvent(launcher NULL, amount 1.0) -> destroy + respawn");
            Mission?.Site.KillShipEvent(now, this, null, 1.0f, Position, Vector3.Zero);
            // Fire-once guard: even if this death fails to relocate/dock the pod
            // (e.g. no friendly station anywhere — BroadcastKillShip's early
            // return leaves it in the sector), never re-trigger every tick.
            LifepodTimeToDie = double.PositiveInfinity;
            return;   // the pod is being destroyed/respawned this tick — stop
        }

        // P155 — compute Controls for IsNpc ships with a reachable PlanTarget
        // BEFORE the flight model reads them (ExecuteMove derives all motion
        // from Controls). Real player ships (IsNpc false) are never touched.
        ApplyNpcAutopilot(now);

        // P162 — the Wingman's per-tick combat leg (validate AttackTarget, then
        // decide whether to hold the trigger). Must run BEFORE the weapon loop
        // further down so this tick's fire-selection bit is already in StateM
        // when Weapon.Update reads IsWeaponSelected. Gates to Wingman NPCs only
        // — a real player ship's StateM is wire-owned and must never be touched.
        //
        // P166 — a defending Wingman's target life is owned by
        // UpdateWingmanDefend, run FIRST so this tick's fire decision in
        // UpdateWingmanCombat reflects any just-made acquire/switch/return-to-
        // escort (a returned-to-escort Wingman releases the trigger; a switched
        // one aims at the new threat).
        if (IsNpc && PilotType == PilotTypeWingman)
        {
            if (DefendTarget is not null)
                UpdateWingmanDefend();
            UpdateWingmanCombat();
        }

        ExecuteMove(now - dt, now);   // flight kinematics for exactly this chunk
        Position += Velocity * (float)dt;   // integrate Δpos = velocity·ΔT

        // P269 — the per-tick cloak sim (CcloakIGC::Update, cloakigc.cpp:59),
        // run within the ship's Update exactly where native calls its mounted
        // parts' Update, after the movement step so the tick's controls are
        // settled. Derives activation from the CloakActive StateM bit, drains
        // energy, and ramps Cloaking (feeding EffectiveSignature → the sector's
        // cross-side detection sweep). No-op on a ship with no ET_Cloak@0 part.
        UpdateCloak((float)dt);

        // Drive mounted weapons (CshipIGC::Update calls its parts' Update,
        // which for a weapon is CweaponIGC::Update → FireWeapon). Runs after the
        // movement step so the muzzle state reflects the tick's end; the weapon
        // fires only if selected, off-cooldown, and with enough energy/ammo.
        // With no weapons mounted (the flight/bot slices) this is a no-op.
        //
        // The turret-gunner prediction (the second of native's two
        // ExecuteTurretMove call sites — the carrier ship's own tick,
        // shipIGC.cpp:327-343) lives here too: for each turret weapon with a
        // gunner, advance the gunner's orientation forward over the same
        // elapsed chunk, exactly as native does inside CshipIGC::Update
        // (shipIGC.cpp:336-338: copy, ExecuteTurretMove(lastUpdate,now,&o),
        // SetOrientation). Between real ACTIVE_TURRET_UPDATE(99) snapshots
        // this keeps the turret aiming continuously rather than freezing until
        // the gunner's next network update. Only turret mounts
        // ([MaxFixedWeapons, MaxWeapons)) can have a gunner; fixed weapons
        // never do (native asserts w->GetGunner() null on that range,
        // shipIGC.cpp:322), so the loop keys off the weapon's mount index.
        foreach (var weapon in _weapons)
        {
            if (weapon.MountId >= _hull.MaxFixedWeapons && weapon.Gunner is { } gunner)
            {
                Orientation o = gunner.Orientation.Copy();       // Orientation o = pshipGunner->GetOrientation()
                gunner.ExecuteTurretMove(now - dt, now, o);      // pshipGunner->ExecuteTurretMove(lastUpdate, now, &o)
                gunner.SetOrientation(o);                        // pshipGunner->SetOrientation(o)
            }
            weapon.Update(now);
        }

        // P226 — the per-tick missile armament leg (the lock half of native
        // CmagazineIGC::Update, magazineigc.cpp:104-176): for a mounted missile
        // launcher (ET_Magazine@0) with ammo, build/decay MissileLock toward
        // CommandTarget every tick. Then honour the held missile-fire bit
        // (missileFireIGC) by attempting a launch when lock+ammo allow — the
        // NPC/local auto-fire lever of magazineigc.cpp:111. Real PLAYER ships
        // instead trigger through the inbound FM_CS_FIRE_MISSILE(70) handler
        // (Server.HandleFireMissile), which runs the same fire gate; the 16-bit
        // wire stateM cannot carry this high-word bit (igc.h:829,2457-2465), so
        // it is never set on a player ship's wire-owned StateM here. This leg
        // runs for every ship that carries the bit in its local StateM (an NPC
        // driver or a test) and is inert otherwise — it does NOT touch the
        // wingman's weapon-only UpdateWingmanCombat combat leg.
        UpdateMountedMissileLock((float)dt);
        if ((StateM & ShipControlState.MissileFire) != 0)
            TryFireMissile(now);

        // P151 — NPC self-preservation (the per-tick trigger at
        // shipIGC.cpp:1934-1945): when the Plan slot holds no legal command,
        // first promote a legal Accepted command into it (SetCommand(c_cmdPlan,
        // ...), shipIGC.cpp:1939 — so a fresh "return to base" order is honored,
        // not instantly overridden), else let a damaged NPC pick take-cover.
        // Legality is the simplified "Target is not null" check (this port's
        // command vocabulary is only Goto/DoNothing/None; native's full
        // pilot-type-branching LegalCommand, shipIGC.h:1559-1617, is out of
        // scope — see NPC_TAKE_COVER_P151.md). Real player ships (IsNpc false)
        // never evaluate take-cover.
        if (PlanTarget is null)
        {
            if (AcceptedTarget is { } accepted)                    // LegalCommand(Accepted), shipIGC.cpp:1936
            {
                PlanTarget = accepted;
                PlanCommandId = AcceptedCommandId;                 // SetCommand(c_cmdPlan, target, cid), shipIGC.cpp:1939
            }
            else if (IsNpc)
            {
                // P160 — the mining/selling/contention machinery is
                // Miner-specific. Only a ship whose drone-type pilot type is
                // Miner runs the autonomous miner decision cycle; every other
                // pilot type (Wingman/Layer/Builder/Carrier, or any
                // not-yet-implemented type) simply does nothing autonomous —
                // the faithful stand-by matching native's PickDefaultOrder
                // fallthrough for a type with no case: fGaveOrder stays false →
                // SetCommand(c_cmdAccepted, NULL, c_cidNone), shipIGC.h:2458-2459.
                // MiningAsteroidTarget stays null for a non-miner, so the
                // take-cover gate below is unaffected.
                if (PilotType == PilotTypeMiner)
                {
                    // P156 — the miner decision cycle. A mining target is stale once
                    // the bag is full (ResolveMining stops accruing at capacity), so
                    // clear it to let the cycle switch to the sell leg.
                    if (Ore >= MineCapacity && MiningAsteroidTarget is not null)
                        MiningAsteroidTarget = null;

                    // Decide mine-or-sell only when fully idle (no station Plan and
                    // no active mining target) and undocked. Once it sets a target,
                    // later ticks are no longer idle, so it does not churn.
                    if (MiningAsteroidTarget is null && StationId is null)
                        DecideMiningOrSelling();
                }
                else if (PilotType == PilotTypeLayer)
                {
                    // P161 — the Layer default-order decision, the port of native
                    // PickDefaultOrder's c_ptLayer case (shipIGC.h:2417-2434).
                    // Like the Miner cycle above, decide only when fully idle (no
                    // Layer target) and undocked. Once it commits to a rock it is
                    // no longer idle, so it does not churn; it travels to and
                    // parks at the lowest-contention asteroid (does not deploy).
                    // Separate slot from the miner's, so the two cycles never
                    // interfere even for ships on the same side.
                    if (LayerAsteroidTarget is null && StationId is null)
                        DecideLayerDefaultOrder();
                }
                else if (PilotType == PilotTypeWingman)
                {
                    // P162 — the Wingman default-order decision, the port of native
                    // PickDefaultOrder's c_ptWingman case (shipIGC.h:2436-2455).
                    // Like the Miner/Layer cycles above, decide only when fully
                    // idle (no AttackTarget) and undocked. Once it commits to an
                    // enemy it is no longer idle, so it does not churn; it pursues
                    // and attacks that enemy (the per-tick fire/steering legs run
                    // earlier in this tick — UpdateWingmanCombat and the combat
                    // goal in ApplyNpcAutopilot). Separate slot from the
                    // miner's/layer's, so the three cycles never interfere even
                    // for ships on the same side.
                    //
                    // P166 — a DEFENDING Wingman (DefendTarget set) is bypassed
                    // here: its target life is owned by the per-tick
                    // UpdateWingmanDefend leg (run earlier in this tick), which
                    // acquires threats near the charge, never the plain nearest-
                    // to-itself default order — otherwise the idle re-pick would
                    // make it abandon its post and chase a distant enemy, and
                    // would instantly re-engage the very foe UpdateWingmanDefend
                    // just broke off from to escort the charge.
                    if (DefendTarget is null && AttackTarget is null && StationId is null)
                        DecideWingmanDefaultOrder();
                }
                else if (PilotType == PilotTypeBuilder)
                {
                    // P165 — the Builder default-order decision, the port of
                    // native PickDefaultOrder's c_ptBuilder case
                    // (shipIGC.h:2388-2415). Like the Miner/Layer/Wingman cycles
                    // above, decide only when not already building (IsBuilding —
                    // a committed build makes the ship non-idle) and undocked.
                    // Once it registers a build it is no longer idle (the
                    // mechanic sets IsBuilding), so it does not churn; the 40 s
                    // construction runs to completion in Sector's per-tick
                    // resolver (StartBuildingEffect / ResolveBuildingEffects).
                    if (!IsBuilding && StationId is null)
                        DecideBuilderDefaultOrder();
                }

                // P151 take-cover: only when STILL fully idle after the decision
                // cycle (a just-picked mining/layer trip isn't instantly
                // preempted), preserving mutual exclusion. Ordering is
                // decision-first — if both a damaged-ship hide and an objective
                // could apply, the objective wins (P156 scope: no priority system
                // engineered; damage interrupting a trip is an accepted, unhandled
                // gap).
                //
                // P160 — deliberately NOT gated on PilotType: native's take-cover
                // applies to any damaged NPC regardless of pilot type
                // (TryTakeCover, shipIGC.h:2230-2236; this port's own call site
                // was already outside the miner-only scope). Only the
                // mining/selling and layer blocks above are pilot-type-specific.
                //
                // P161 — the idle gate now also demands a null LayerAsteroidTarget
                // so a Layer's just-chosen rock isn't instantly preempted by
                // take-cover, exactly as a Miner's isn't. (Harmless for a Miner —
                // its LayerAsteroidTarget is always null.)
                //
                // P162 — the same protection extends to a Wingman's just-chosen
                // AttackTarget: a ship actively pursuing an enemy is not "idle"
                // and is not diverted into take-cover by it. (Harmless for the
                // others — their AttackTarget is always null.)
                //
                // P165 — likewise extended to a Builder's IsBuilding: a ship
                // mid-construction is not "idle" either. Without this, a
                // damaged Builder could fly off to take cover while its
                // 40-second build silently continues in Sector's resolver
                // (ResolveBuildingEffects only checks Builder.Dead, not
                // position/Controls) — a real inconsistency with the pattern
                // every other busy pilot type already follows here. (Harmless
                // for the others — their IsBuilding is always false.)
                //
                // P166 — and a defending Wingman's DefendTarget: a Wingman
                // escorting its charge (even with no AttackTarget at that
                // instant) is not "idle" and must not be diverted into take-
                // cover, any more than an actively-engaging one. (Harmless for
                // the others — their DefendTarget is always null.)
                if (MiningAsteroidTarget is null && LayerAsteroidTarget is null && AttackTarget is null && DefendTarget is null && !IsBuilding && PlanTarget is null)
                    TryTakeCover();                                // PickDefaultOrder take-cover, shipIGC.cpp:1943
            }
        }

        // P159 — real-time same-side asteroid contention arbitration. Runs EVERY
        // tick for an IsNpc ship holding a mining target (NOT gated on the idle
        // check that gates DecideMiningOrSelling above): once a ship has picked
        // a rock it is never idle again (MiningAsteroidTarget non-null), so a
        // decision-time-only check would never re-arbitrate a later-arriving
        // poorer contender. Native's block runs unconditionally every tick
        // wantsToMineMaskIGC is set (shipIGC.cpp:2088-2091) — the same per-tick
        // lifetime, placed here so this tick's already-decided state is the one
        // arbitrated and the loser's sell redirect is the final word.
        // P160 — additionally gated on Miner: only a miner holds a mining target
        // (the decision cycle that sets it is Miner-gated above), so the gate is
        // self-documenting rather than load-bearing, but makes the exclusion
        // explicit for a non-miner's never-populated MiningAsteroidTarget.
        if (IsNpc && PilotType == PilotTypeMiner && MiningAsteroidTarget is not null)
            ArbitrateAsteroidContention();
    }
}
