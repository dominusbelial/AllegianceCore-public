using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Core.Sim;

/// <summary>
/// The <b>runtime</b> station — the minimal stateful counterpart to the
/// immutable static <see cref="DataStationIGC"/> wire record plus its resolved
/// <see cref="DataStationTypeIGC"/>. This is the station half of the same
/// static-vs-runtime split P53 established (the static <see cref="Data"/>
/// record in <c>Sector.Stations</c> plus runtime state here), mirroring how
/// <see cref="Asteroid"/> wraps <see cref="DataAsteroidIGC"/> (P102): the
/// static record carries identity/geometry, the static <b>type</b>
/// (<see cref="DataStationTypeIGC"/>, whose <c>MaxArmorHitPoints</c>,
/// <c>MaxShieldHitPoints</c>, <c>ShieldRegeneration</c>,
/// <c>ArmorRegeneration</c> and the two defense-type indices are all already
/// decoded — <c>DataStationTypeIGC.cs:32-35,42-43</c>) carries the capacity
/// data, and this object carries the live combat fractions
/// (<see cref="ArmorFraction"/> and <see cref="ShieldFraction"/>), damage intake
/// (<see cref="ReceiveDamage"/>), regen (<see cref="Regenerate"/>), and the
/// station's rotating launch slot / last-launch time (<see cref="Launch"/>).
/// </summary>
/// <remarks>
/// <para>Mirrors the <see cref="Asteroid"/> half of the split — and
/// deliberately is <b>not</b> a <see cref="Model"/>: a station does not join
/// the sector's <c>_models</c> loop (it is a static body; the combat pass adds
/// it a <c>isStatic</c> collider like a rock, P102). The static
/// <see cref="Data"/> record stays in <c>Sector.Stations</c> for all the
/// pre-existing docking / ripcord-anchor / undock seams unchanged; the runtime
/// <see cref="Station"/> lives in the sector's parallel runtime list
/// (<c>Sector._stationRuntimes</c>) so the combat pass can hit it and the
/// sector can tick its regen. This is the exact same split the asteroid made
/// (<c>Sector._mineable</c> vs <c>Sector._asteroids</c>).</para>
/// <para>Behaviour is ported from <c>CstationIGC</c>
/// (<c>src/Igc/stationIGC.cpp</c> / <c>stationIGC.h</c>) and every
/// simplification is stated openly (the same honest-simplification style as
/// <see cref="Ship"/> / <see cref="Asteroid"/> / <see cref="Probe"/>):
/// <list type="bullet">
/// <item><b>Capacity is data, never hardcoded.</b> <see cref="MaxArmor"/> /
/// <see cref="MaxShield"/> read the decoded type (<c>DataStationTypeIGC.cs:32-33</c>),
/// the defense indices read <c>DefenseTypeArmor</c>/<c>DefenseTypeShield</c>
/// (<c>DataStationTypeIGC.cs:42-43</c>), and the damage matrix is the same
/// mission-level <c>DataConstantsIGC</c> table <see cref="Ship.ReceiveDamage"/>
/// already reads (derived from the ordnance launcher's mission, exactly like
/// <see cref="Asteroid.ReceiveDamage"/>'s private lookup).</item>
/// <item><b>No experience multiplier.</b> Native scales a ship-launched hit by
/// <c>GetExperienceMultiplier()</c> (<c>stationIGC.cpp:142-145</c>); this port's
/// ships have no accumulated experience (Ship.cs:880-881), so the multiplier is
/// 1.0 — stated, not silently dropped.</item>
/// <item><b>Repair path retained structurally.</b> A negative <paramref name="amount"/>
/// (repair) restores the hull fraction capped at 1.0, per
/// <c>stationIGC.cpp:147-156</c> minus the ThingSite bookkeeping; no repair
/// source exists yet, so it is dead code in practice (same precedent as
/// <see cref="Ship.ReceiveDamage"/>'s repair branch, Ship.cs:906-914).</item>
/// <item><b>Invulnerability gates simplified.</b> The native early-return
/// (<c>stationIGC.cpp:126-133</c>) combines three conditions: the mission param
/// <c>bInvulnerableStations</c> (NOT decoded by this port — mission parameters
/// are not parsed here, matching how this port defaults away from native's
/// rarer mission-config toggles; the honest default is <b>stations are
/// vulnerable</b>), the <c>c_sabmPedestal</c> capability bit (a flag-pedestal
/// station is indestructible — implemented, <see cref="SabmPedestal"/>, the
/// same one-line bit addition P114 made for the hull <c>Habm</c> bits), and the
/// friendly-fire gate. <see cref="Mission.AllowFriendlyFire"/> controls the
/// native non-repair friendly/allied immunity (stationIGC.cpp:128-130).
/// The invulnerability combination returns DamageResult.NoDamage.</item>
/// <item><b>No hull-damage fallback when shield-less.</b> Confirmed reading the
/// native: when <c>dtmShield == 0</c> OR <c>MaxShieldHitPoints == 0</c> the
/// damage path falls to <c>dr = c_drNoDamage</c> (<c>stationIGC.cpp:203-204</c>)
/// — a station without shields takes <b>no damage at all</b>, not a
/// hull-damage fallback. Faithfully reproduced (<see cref="ReceiveDamage"/>).</item>
/// </list></para>
/// <para>The concrete <see cref="Data"/> record's <c>BpHull</c>/<c>BpShield</c>
/// bytes are <b>not</b> used to seed the fractions: native's <c>Export</c>
/// writes them as a lossy <c>(byte)m_hullFraction</c> cast
/// (<c>stationIGC.cpp:110-111</c>), so a full-health station serializes as
/// <c>1</c> (≈ 1/240), not as a meaningful percentage. Fractions therefore
/// start at <c>1.0f</c> (full), the same default the asteroid's
/// <see cref="Asteroid.Fraction"/> uses (asteroidigc.h:27).</para>
/// </remarks>
public sealed class Station : IStationIgc, ISideVisible
{
    private int _undockPosition;
    private double _lastLaunch = double.NegativeInfinity;

    /// <summary>
    /// Native stationIGC.cpp:299-382: launch immediately from the next slot,
    /// with +/-5 jitter and a larger outward offset for rapid successive
    /// launches. There is no launch queue or temporary ship collision immunity.
    /// The caller moves sector membership and sends the launch state.
    /// </summary>
    public void Launch(Ship ship, HullLandingBayGeometry? geometry, double now, Random? random = null)
    {
        random ??= Random.Shared;
        const float maxOffset = 5;
        float Jitter() => (float)(random.NextDouble() * (2 * maxOffset) - maxOffset);
        var position = new Vector3(Jitter(), Jitter(), Jitter());
        var orientation = Orientation;
        float speed = ship.Mission!.Core.Constants.FloatConstants[4];
        float radius = ship.HullType.Length * .5f;
        var slots = geometry?.LaunchSlots;
        Vector3 forward;
        if (slots is not { Count: > 0 })
        {
            if (ship.PilotType < Ship.PilotTypePlayer && Type.HasCapability(DataStationTypeIGC.SabmRipcord))
            {
                // Vector::RandomDirection (zlib/vector.cpp:37-46).
                double z = random.NextDouble() * 2 - 1;
                double angle = random.NextDouble() * 2 * Math.PI;
                double r = Math.Sqrt(1 - z * z);
                forward = new Vector3((float)(r * Math.Cos(angle)), (float)(r * Math.Sin(angle)), (float)z);
            }
            else
            {
                // Preserve native's case 4 reset/fallthrough (including the
                // repeated right launch at the five-launch wrap).
                int direction = _undockPosition++;
                if (direction == 4) { _undockPosition = 0; direction = 0; }
                forward = direction switch
                {
                    1 => orientation.GetUp(), 2 => -orientation.GetRight(),
                    3 => -orientation.GetUp(), _ => orientation.GetRight()
                };
            }
            position += forward * (Radius + radius + speed * .5f + maxOffset);
        }
        else
        {
            var slot = slots[_undockPosition % slots.Count];
            position += slot.Position * orientation;
            forward = slot.Direction * orientation;
            position += forward * (radius + speed * (now - _lastLaunch <= .1 ? .85f : .5f) + maxOffset);
            _undockPosition = (_undockPosition + 1) % slots.Count;
        }
        ship.Orientation.Set(forward, orientation.GetForward());
        ship.Position = position + Center;
        ship.Velocity = forward * speed;
        for (int axis = 0; axis < 3; axis++) ship.SetTurnRate(axis, 0);
        ship.LastLaunchTime = now;
        _lastLaunch = now;
    }

    /// <summary>
    /// <c>c_sabmPedestal = 0x400</c> (<c>src/Igc/igc.h:758</c>) — the
    /// station-ability bit saying a station is a flag pedestal and therefore
    /// <b>indestructible</b> (<c>stationIGC.cpp:127</c>). Added here the same
    /// one-line way <see cref="DataStationTypeIGC.SabmRestart"/> /
    /// <see cref="DataStationTypeIGC.SabmRipcord"/> were exposed, and tested by
    /// <see cref="DataStationTypeIGC.HasCapability(SabmPedestal)"/>.
    /// </summary>
    public const short SabmPedestal = 0x400;

    /// <summary>The underlying static wire record (identity, geometry, side).</summary>
    public DataStationIGC Data { get; }

    /// <summary>The resolved static station <b>type</b> — the capacity data
    /// (max armor/shield, regen rates, defense types) the damage formula reads.</summary>
    public DataStationTypeIGC Type { get; }

    /// <summary>Wrap a static record and its resolved type as the runtime model.</summary>
    public Station(DataStationIGC data, DataStationTypeIGC type)
    {
        Data = data;
        Type = type;
        SideId = data.SideId;      // live side shadows the static record's (capture re-sides it)
    }

    /// <summary>The station's id (<see cref="DataStationIGC.StationId"/>).</summary>
    public short StationId => Data.StationId;

    /// <summary>The owning side id (<see cref="DataStationIGC.SideId"/>). Native
    /// stations always carry a side; the friendly-fire gate compares this to the
    /// launcher's <see cref="Ship.SideId"/>. <b>Mutable:</b> STATION_CAPTURE
    /// re-sides the station (native <c>pstation-&gt;SetSide(pside)</c>,
    /// <c>fsship.cpp:631</c>) — the port keeps the live side here (initialized
    /// from the static record) and re-sides it in <see cref="Capture"/>, while
    /// <c>Sector.ResolveDockCollisions</c> mirrors the change into the static
    /// <c>_stations</c> record so the docking / friendly-fire seams agree.</summary>
    public short SideId { get; private set; }

    /// <summary>
    /// P475 — the station's mission, the back-ref that lets
    /// <see cref="SideGlobalAttributes"/> resolve the owning side's global
    /// attribute set (the station parallel of <see cref="Ship.Mission"/>). Set
    /// by <see cref="Mission.Assemble"/> for every production runtime station
    /// and by <see cref="Mission.PlaceStation"/> for the synthetic/test path. A
    /// station without a resolvable mission (e.g. one constructed directly in a
    /// unit test) reads identity — the honest neutral P446 established.
    /// </summary>
    public Mission? Mission { get; set; }

    /// <summary>The station's sector id (<see cref="DataStationIGC.ClusterId"/>).</summary>
    public short ClusterId => Data.ClusterId;

    /// <summary>Max armor hit points — the decoded <c>MaxArmorHitPoints</c>
    /// (<c>DataStationTypeIGC.cs:32</c>), never hardcoded.</summary>
    public float MaxArmor => Type.MaxArmorHitPoints;

    /// <summary>Max shield hit points — the decoded <c>MaxShieldHitPoints</c>
    /// (<c>DataStationTypeIGC.cs:33</c>).</summary>
    public float MaxShield => Type.MaxShieldHitPoints;

    /// <summary>Whether the station type carries a shield at all
    /// (<c>MaxShieldHitPoints != 0</c>) — the native shield gate at
    /// <c>stationIGC.cpp:161</c>.</summary>
    public bool HasShield => MaxShield != 0.0f;

    /// <summary>P475 — effective max armor hit points = the decoded
    /// <c>MaxArmorHitPoints</c> × the owning side's <c>c_gaMaxArmorStation</c>
    /// factor — the port of <c>MyStationType::GetMaxArmorHitPoints</c>
    /// (<c>stationIGC.cpp:594-597</c>; <c>c_gaMaxArmorStation</c> = igc.h:591).
    /// Feeds the damage/repair hull-fraction denominator
    /// (<c>stationIGC.cpp:137</c>). The hull fraction is stored state, so a GA
    /// change rescales the absolute maximum without touching the fraction —
    /// exactly native.</summary>
    public float EffectiveMaxArmor() => MaxArmor * SideGlobalAttributes().Get(GlobalAttributeSet.MaxArmorStation);

    /// <summary>P475 — effective max shield hit points = the decoded
    /// <c>MaxShieldHitPoints</c> × the owning side's <c>c_gaMaxShieldStation</c>
    /// factor — the port of <c>MyStationType::GetMaxShieldHitPoints</c>
    /// (<c>stationIGC.cpp:603-610</c>; <c>c_gaMaxShieldStation</c> = igc.h:594).
    /// Feeds the shield-absorption divisor (<c>stationIGC.cpp:171</c>) and the
    /// shield-regen fraction gain (<c>stationIGC.h:203</c>).</summary>
    public float EffectiveMaxShield() => MaxShield * SideGlobalAttributes().Get(GlobalAttributeSet.MaxShieldStation);

    /// <summary>P475 — effective armor regeneration rate = the decoded
    /// <c>ArmorRegeneration</c> × the owning side's
    /// <c>c_gaArmorRegenerationStation</c> factor — the port of
    /// <c>MyStationType::GetArmorRegeneration</c> (<c>stationIGC.cpp:614-618</c>;
    /// <c>c_gaArmorRegenerationStation</c> = igc.h:592).</summary>
    public float EffectiveArmorRegen() => Type.ArmorRegeneration * SideGlobalAttributes().Get(GlobalAttributeSet.ArmorRegenerationStation);

    /// <summary>P475 — effective shield regeneration rate = the decoded
    /// <c>ShieldRegeneration</c> × the owning side's
    /// <c>c_gaShieldRegenerationStation</c> factor — the port of
    /// <c>MyStationType::GetShieldRegeneration</c> (<c>stationIGC.cpp:619-623</c>;
    /// <c>c_gaShieldRegenerationStation</c> = igc.h:595).</summary>
    public float EffectiveShieldRegen() => Type.ShieldRegeneration * SideGlobalAttributes().Get(GlobalAttributeSet.ShieldRegenerationStation);

    /// <summary>P476 — effective scanner range = the decoded
    /// <c>Type.ScannerRange</c> × the owning side's <c>c_gaScanRange</c> factor —
    /// the port of <c>MyStationType::GetScannerRange</c> (<c>stationIGC.cpp:591-595</c>;
    /// <c>c_gaScanRange</c> = igc.h:601). This is the scan range the station
    /// contributes to the sector's cross-side visibility sweep, driving its
    /// detection radius exactly as native's station <c>InScannerRange</c> does
    /// (<c>stationIGC.h:318</c>: <c>m_myStationType.GetScannerRange() ·
    /// targetSignature</c>).</summary>
    public float EffectiveScannerRange() => Type.ScannerRange * SideGlobalAttributes().Get(GlobalAttributeSet.ScanRange);

    /// <summary>
    /// P475 — resolve this station's owning <see cref="Model.Side"/>'s global
    /// attribute set, or identity when the station has no mission or the
    /// mission cannot resolve the side. This is the port of native
    /// <c>m_pstation-&gt;GetSide()-&gt;GetGlobalAttributeSet()</c>
    /// (<c>stationIGC.cpp:596,607,617,622</c>); a native station always has a
    /// side, but this port's sim permits stations without a resolved mission,
    /// so identity (all 1.0) is the honest neutral — the same contract P446's
    /// <see cref="Ship.SideGlobalAttributes"/> establishes. Cheap: a manual
    /// <c>foreach</c> over <see cref="Mission.Sides"/> with no LINQ/delegate
    /// allocation, safe on the per-tick regen path. The lookup is scoped to
    /// <see cref="Mission"/> because side ids repeat across world missions, and
    /// reads the live <see cref="SideId"/> — so on capture (<see cref="Capture"/>)
    /// the station immediately switches to the new owner's attributes, matching
    /// native (after <c>pstation-&gt;SetSide</c>, <c>fsship.cpp:634</c>).
    /// </summary>
    private GlobalAttributeSet SideGlobalAttributes()
    {
        // Station.SideId is a non-nullable short (always a value; native stations
        // always carry a side), so the identity cases are: no resolvable mission,
        // or a side id absent from the mission's sides.
        if (Mission is not { } mission)
            return GlobalAttributeSet.Identity;
        foreach (var side in mission.Sides)
            if (side.Id == SideId)
                return side.GlobalAttributes;
        return GlobalAttributeSet.Identity;
    }

    /// <summary>The physical collision radius — the decoded
    /// <c>DataStationTypeIGC.Radius</c>, which native assigns the station's
    /// collision sphere from (<c>stationIGC.cpp:249</c>:
    /// <c>SetRadius(m_myStationType.GetRadius())</c>).</summary>
    public float Radius => Type.Radius;

    /// <summary>The per-side visibility state of this station — the port's
    /// host of the native <c>ThingSiteImpl::m_rgSideVisibility</c> (the station
    /// carries <c>c_mtSeenBySide | c_mtScanner</c>, stationIGC.h:177). Written by
    /// the sector's per-tick cross-side visibility sweep
    /// (<see cref="Sector.ResolveCrossSideVisibility"/>, P132) and read through
    /// <see cref="SeenBySide"/> / <see cref="GetCurrentEye"/>. A station in a
    /// sector that is never swept reads all-false.</summary>
    public SideVisibilityState SideVisibility { get; } = new();

    /// <summary>Whether <paramref name="sideId"/> can currently see this
    /// station (<c>modelIGC.h:110-114</c>) — reads the live
    /// <see cref="SideVisibilityState"/> the sweep maintains.</summary>
    public bool SeenBySide(short sideId) => SideVisibility.SeenBySide(sideId);

    /// <summary>Whether <paramref name="sideId"/> is currently the eye on this
    /// station (<c>modelIGC.h:120-124</c>) — reads the live
    /// <see cref="SideVisibilityState"/> the sweep maintains.</summary>
    public bool GetCurrentEye(short sideId) => SideVisibility.GetCurrentEye(sideId);

    /// <summary>The station's centre, derived from its static record
    /// (<see cref="DataStationIGC.Position"/>); the combat pass uses it as the
    /// isStatic collider origin and <c>position2</c> of a hit.</summary>
    public Vector3 Center =>
        Data.Position is { Length: 3 } p ? new Vector3(p[0], p[1], p[2]) : Vector3.Zero;

    /// <summary>The station's world attitude, built from its static record's
    /// <c>Forward</c>/<c>Up</c> axes (<c>DataStationIGC</c>), the orientation
    /// the <see cref="InGarage"/> geometric dock test transforms the bay planes
    /// by (native <c>CstationIGC::GetOrientation()</c>, <c>stationIGC.cpp:410-411</c>).</summary>
    public Orientation Orientation
    {
        get
        {
            var o = new Orientation();
            if (Data.Forward is { Length: 3 } f && Data.Up is { Length: 3 } u)
                o.Set(new Vector3(f[0], f[1], f[2]), new Vector3(u[0], u[1], u[2]));
            return o;
        }
    }

    /// <summary>
    /// Port of <c>CstationIGC::InGarage(IshipIGC* pship, const Vector&amp; position)</c>
    /// (<c>src/Igc/stationIGC.cpp:385-436</c>) — the geometric "is this ship in
    /// my landing bay" test the native dock dispatch runs on a station
    /// (<c>shipIGC.cpp:843,853</c>). Structurally identical to the carrier's
    /// <see cref="Ship.InGarage(Ship, float, HullLandingBayGeometry)"/> — this
    /// port deliberately REUSES that geometric predicate (the same
    /// slot-plane/GetMinExtreme half-space test) rather than the station's
    /// crude 200-unit dock-sphere; the only differences from the carrier form
    /// are native-faithful: the test takes a <em>fixed</em> world
    /// <paramref name="position"/> (the station is static, so there is no
    /// carrier velocity term, <c>stationIGC.cpp:404</c>), the bay geometry
    /// comes from the station <em>type's</em> own model
    /// (<see cref="Collision.StationLandingBayCache"/> /
    /// <see cref="Collision.HullLandingBay.ReconstructStation"/>,
    /// <c>stationtypeigc.cpp:77-101</c>), and <see cref="Orientation"/> is the
    /// station's. A bay with no land slots is <c>false</c> — the native
    /// <c>landSlots-1 &lt; 0</c> no-bay case (<c>stationIGC.cpp:388-390</c>).
    /// The lander's extreme point is the <c>0.5 × HullType.Length</c> sphere,
    /// the same documented simplification <see cref="Ship.InGarage"/> makes.
    /// </summary>
    public bool InGarage(Ship lander, Vector3 position, HullLandingBayGeometry bay)
    {
        if (bay.LandSlots.Count == 0) return false;         // native: landSlots-1 < 0 → bInside stays false

        var dp = position - Center;                          // stationIGC.cpp:404 (station static → no velocity term)
        var landerRadius = 0.5f * lander.HullType.Length;    // documented sphere simplification
        var orientationStation = Orientation;                // stationIGC.cpp:410

        int slots = lander.HullType.HasCapability(DataHullTypeIGC.HabmFighter)
            ? bay.LandSlots.Count : bay.CapLandSlots;
        for (var i = slots - 1; i >= 0; i--)   // stationIGC.cpp:390,413 (slot loop, descending)
        {
            var slot = bay.LandSlots[i];
            var j = slot.Planes.Count - 1;
            do                                               // stationIGC.cpp:415 (plane loop, descending)
            {
                var plane = slot.Planes[j];
                var direction = plane.Direction * orientationStation;   // stationIGC.cpp:417
                var point = plane.Position * orientationStation;        // stationIGC.cpp:418
                var pMin = dp - direction * landerRadius;               // sphere GetMinExtreme, stationIGC.cpp:423
                if ((pMin - point) * direction < 0.0f)                  // stationIGC.cpp:424
                    break;
            } while (j-- > 0);

            if (j == -1)                                     // all planes passed → bInside=true (stationIGC.cpp:434-438)
                return true;
        }

        return false;
    }

    /// <summary>The station's current hull (armor) health fraction — the direct-hit
    /// health pool, written by <see cref="ReceiveDamage"/> and clamped to <c>0</c>
    /// on destruction, and regrown by <see cref="Regenerate"/> toward 1.0
    /// (<c>CstationIGC</c>'s <c>m_hullFraction</c>, stationIGC.cpp:149,187).</summary>
    public float ArmorFraction { get; private set; } = 1.0f;

    /// <summary>The station's current shield health fraction — the second live
    /// per-instance pool, absorbing hits before the armor and regrowing toward
    /// 1.0 (<c>CstationIGC</c>'s <c>m_shieldFraction</c>, stationIGC.cpp:176,200).
    /// A shield-less station type keeps this at 1.0 untouched.</summary>
    public float ShieldFraction { get; private set; } = 1.0f;

    /// <summary>True once the station is destroyed (<c>ArmorFraction ≤ 0</c>),
    /// mirroring <see cref="Asteroid.Dead"/> / <see cref="Ship.Dead"/> — the
    /// combat pass reads it the same way it reads <c>asteroid.Dead</c> to avoid
    /// re-destroying an already-dead station in the same step.</summary>
    public bool Dead => ArmorFraction <= 0.0f;

    /// <summary>P451 — the sim-clock timestamp (seconds) of the last time the
    /// host issued a damage alert for this station, backing the native
    /// per-station 10-second report throttle
    /// (<c>FedSrvSiteBase::DamageStationEvent</c>,
    /// <c>(g.timeNow - station-&gt;GetLastDamageReport()) &gt; 10.0f</c>,
    /// <c>FedSrv.CPP:12190</c>), mirroring <c>CstationIGC</c>'s
    /// <c>m_lastDamageReport</c> (<c>stationIGC.h:142</c>). Initialized to
    /// negative infinity so the <em>first</em> hit always passes the throttle
    /// (native reaches this state because stations are created minutes into a
    /// server's life, long after <c>g.timeNow</c> passed 10 s).</summary>
    public double LastDamageReport { get; set; } = double.NegativeInfinity;

    /// <summary>
    /// Port of <c>CstationIGC::ReceiveDamage</c> (<c>stationIGC.cpp:116-208</c>)
    /// — the station's damage entry point, called by
    /// <see cref="Model.Sector.ResolveCombatCollisions"/> exactly as a ship's or
    /// an asteroid's is. A station is hit through the same generic collision
    /// dispatch: <c>Load</c> gives it the <c>c_mtStatic | c_mtDamagable |
    /// c_mtHitable | c_mtSeenBySide | c_mtPredictable | c_mtScanner</c> mask
    /// (<c>stationIGC.cpp:240-247</c>), and the native projectile/missile
    /// <c>HandleCollision</c> excludes only <c>OT_mine</c>
    /// (<c>projectileIGC.cpp:166-169</c>) — nothing special-cases
    /// <c>OT_station</c>, so a station is a valid direct-hit target of any
    /// ordnance, the same conclusion P102 reached for <see cref="Asteroid"/>.
    /// <list type="bullet">
    /// <item><b>Shield-absorbs-then-overflows-to-armor.</b> The structure is the
    /// station's own variation of <see cref="Ship.ReceiveDamage"/> (P87), with
    /// the station-specific static fields. Shield stage:
    /// <c>absorbed = amount · dtmShield / maxShield</c>; while
    /// <c>ShieldFraction ≥ absorbed</c> the whole hit is absorbed
    /// (<c>stationIGC.cpp:174-177</c>). When the shield depletes
    /// (<c>stationIGC.cpp:178-201</c>): the overflow is converted to hull damage
    /// by the exact native ratio <c>dmg = (absorbedOverflow · maxShield ·
    /// dtmArmor) / (maxArmor · dtmShield)</c> (<c>stationIGC.cpp:186</c>) — the
    /// precise formula the tests assert — and the shield clamps to 0.</item>
    /// <item><b>Friendly fire follows the mission toggle and allies.</b> See the class
    /// remarks: the native invulnerability early-return
    /// (<c>stationIGC.cpp:126-133</c>) collapses to pedestal
    /// (<see cref="SabmPedestal"/>) + same-side. A friendly hit returns
    /// <c>NoDamage</c>; the ordnance still terminates (native always kills the
    /// projectile on impact, <c>projectileIGC.cpp:229-230</c>).</item>
    /// <item><b>Death bookkeeping lives in the caller.</b> Exactly like
    /// <see cref="Asteroid.ReceiveDamage"/>, this method returns
    /// <c>DamageResult.Killed</c> on the <b>first</b> crossing of
    /// <c>ArmorFraction ≤ 0</c> (the native <c>dr = c_drKilled</c> +
    /// <c>KillStationEvent</c> at <c>stationIGC.cpp:189-194</c>); the combat pass
    /// — which has the <see cref="Sector"/> (for removal) and the ordnance
    /// launcher's <see cref="Mission"/> (for the <see cref="IIgcSite.KillStationEvent"/>
    /// broadcast + vacate) — performs that bookkeeping. A second call past death
    /// returns <c>HullDamage</c> with no repeated kill (native
    /// <c>dr</c>-stays-default, <c>stationIGC.cpp:191-194</c>).</item>
    /// </list>
    /// </summary>
    public DamageResult ReceiveDamage(
        byte type,
        float amount,
        double now,
        Vector3 position1,
        Vector3 position2,
        Model? launcher)
    {
        // Invulnerability gates (stationIGC.cpp:126-133). bInvulnerableStations
        // (a mission param) is NOT decoded in this port → default = vulnerable
        // (stated plainly in the class remarks). What remains: a flag-pedestal
        // station, and the mission's friendly-fire/alliance damage gate.
        if (Type.HasCapability(SabmPedestal))
            return DamageResult.NoDamage;

        // P451 — raise the station-damage seam for every real damaging hit. Native
        // nests the call inside the shield branch of CstationIGC::ReceiveDamage
        // (stationIGC.cpp:168-169); we raise it for ANY non-repair, non-pedestal hit
        // (reaching the site through the launcher's mission, the same bridge the
        // sector uses for KillStationEvent) so BOTH the host's cross-side team alert
        // AND its same-side "Watch your fire" branch are reachable — the same set of
        // outcomes native produces (with friendly fire enabled) from this seam. The
        // host applies every real gate (amount>=1, no c_dmgidNoWarn, per-station 10s
        // throttle, cross/same-side) in FedSrvSiteBase::DamageStationEvent
        // (FedSrv.CPP:12183-12233); the fraction it reads is this PRE-hit value
        // because the raise precedes the shield/hull mutation below, exactly as
        // native (line 169 before line 171).
        if (amount >= 0.0f && launcher is Ship als && als.Mission is { } am)
            am.Site.DamageStationEvent(this, launcher, type, amount);

        // Native stationIGC.cpp:128-130: repair bypasses friendly immunity.
        if (BlastDamage.Suppressed(Mission, SideId, launcher, amount))
            return DamageResult.NoDamage;

        float maxArmor = EffectiveMaxArmor();                      // stationIGC.cpp:137 (GA-scaled)
        float dtmArmor = DamageConstant(type, Type.DefenseTypeArmor, launcher);  // stationIGC.cpp:138

        // P278 — the experience (Kill Bonus) multiplier on a STATION target,
        // native stationIGC.cpp:142-145 ("Student 8/3/2022 applying experience
        // modifier (kb) earlier to apply to repairs"): the launcher ship's
        // ExperienceMultiplier scales both damage (positive) and nanite repair
        // (negative) output. Previously noted N/A — now ported with P278.
        if (launcher is Ship ls)
            amount *= ls.ExperienceMultiplier;

        DamageResult dr;

        if (amount < 0.0f)
        {
            // Repair path (stationIGC.cpp:147-156): heals hull fraction, clipped
            // at 1.0 (can't over-repair). No repair source exists yet — dead code
            // in practice, retained for faithfulness (Ship.cs:906-914 precedent).
            ArmorFraction -= amount * dtmArmor / maxArmor;
            if (ArmorFraction > 1.0f) ArmorFraction = 1.0f;
            dr = DamageResult.NoDamage;
        }
        else
        {
            float dtmShield = DamageConstant(type, Type.DefenseTypeShield, launcher);  // stationIGC.cpp:159

            // Shield gate (stationIGC.cpp:161): only when the type has a shield
            // defense matrix entry AND a non-zero max shield pool.
            if (dtmShield != 0.0f && HasShield)
            {
                dr = DamageResult.ShieldDamage;                     // stationIGC.cpp:163

                float maxShield = EffectiveMaxShield();            // stationIGC.cpp:171 (GA-scaled)
                float absorbed = amount * dtmShield / maxShield;    // stationIGC.cpp:172

                if (ShieldFraction >= absorbed)
                {
                    ShieldFraction -= absorbed;                     // stationIGC.cpp:174-177 (absorb entirely)
                }
                else
                {
                    if (dtmArmor != 0.0f)                           // stationIGC.cpp:180
                    {
                        dr = DamageResult.HullDamage;               // stationIGC.cpp:182
                        absorbed -= ShieldFraction;                 // stationIGC.cpp:183 (the overflow past the shield)
                        if (ArmorFraction > 0.0f)                   // stationIGC.cpp:184
                        {
                            // The exact native overflow ratio (stationIGC.cpp:186):
                            //   dmg = (absorbedOverflow * maxShield * dtmArmor) / (maxArmor * dtmShield)
                            float dmg = (absorbed * maxShield * dtmArmor) / (maxArmor * dtmShield);
                            ArmorFraction -= dmg;                   // stationIGC.cpp:187

                            if (ArmorFraction <= 0.0f)              // stationIGC.cpp:189
                            {
                                ArmorFraction = 0.0f;               // stationIGC.cpp:191
                                dr = DamageResult.Killed;           // stationIGC.cpp:193 (first crossing → caller destroys+broadcasts)
                            }
                            // else: ThingSite->AddDamage visual (stationIGC.cpp:195-196)
                            // is native rendering debris, not ported.
                        }
                    }
                    ShieldFraction = 0.0f;                          // stationIGC.cpp:200 (shield always bottoms out on overflow)
                }
            }
            else
                dr = DamageResult.NoDamage;                         // stationIGC.cpp:203-204 (no shield → NO damage at all, confirmed)
        }

        return dr;
    }

    /// <summary>
    /// The sim-side of a hostile capture (the <b>state</b> half of the port of
    /// <c>CFSShip::CaptureStation</c>, <c>fsship.cpp:577-641</c>, driven by the
    /// sector dock pass's capture branch — <see cref="Model.Sector.ResolveDockCollisions"/>).
    /// It applies the post-capture <b>damage fudge</b> (halve the current hull
    /// fraction, then set the shield to the &quot;heroic engineer&quot;
    /// regen-derived starting value) and re-sides the station to
    /// <paramref name="newSideId"/>. The <b>wire</b> half (the whole-mission
    /// <c>STATION_CAPTURE</c> broadcast) is performed by the host's
    /// <see cref="Model.Sector"/>-raised <c>CaptureStationEvent</c> seam, which
    /// the sector invokes <em>before</em> this method so the server can read
    /// <c>sidOld</c> from the still-enemy station (see the sector branch).
    /// <list type="bullet">
    /// <item><b>Post-capture damage fudge</b> (<c>fsship.cpp:582</c>):
    /// <c>SetFraction(GetFraction() * 0.5f)</c> — real mechanical behaviour
    /// (&quot;all those guns inside damage the hull&quot;), not a
    /// simplification. Armor fraction is exactly halved from its current value.</item>
    /// <item><b>Heroic-engineer shield boost</b> (<c>fsship.cpp:589-596</c>):
    /// when the station type has a non-zero shield pool, the shield starts at
    /// <c>regenRatio · 7.0f</c> where <c>regenRatio = ShieldRegeneration /
    /// MaxShieldHitPoints</c> (≈ 0.1 → 0.7 for the shipped cores); a shield-less
    /// station starts at 0. No <c>AddBaseCapture</c> / score / Steam
    /// bookkeeping — the port's <see cref="Model.Side"/> tracks no base-capture
    /// counter and there is no score/Steam system (confirmed N/A).</item>
    /// </list>
    /// </summary>
    /// <param name="newSideId">The capturing ship's side id — the station's new owner.</param>
    public void Capture(short newSideId)
    {
        // Post-capture damage fudge (fsship.cpp:582): halve the current hull fraction.
        ArmorFraction *= 0.5f;

        // "Heroic engineer" regen-derived starting shield value (fsship.cpp:589-596).
        // Native computes regenRatio from GetStationType() (the GA wrapper:
        // GetShieldRegeneration()/GetMaxShieldHitPoints(), stationIGC.cpp:607,622)
        // BEFORE SetSide (fsship.cpp:634), so both numerator and denominator use
        // the OLD owner's GA — SideId is still the pre-capture value here, so
        // EffectiveShieldRegen()/EffectiveMaxShield() read the old owner too.
        float heroicEngineerShieldBoost = 0.0f;
        if (MaxShield != 0.0f)                     // prevent divide-by-zero (fsship.cpp:590)
        {
            float regenRatio = EffectiveShieldRegen() / EffectiveMaxShield();   // fsship.cpp:592 (old-owner GA)
            heroicEngineerShieldBoost = regenRatio * 7.0f;             // fsship.cpp:593
        }
        ShieldFraction = heroicEngineerShieldBoost;                    // fsship.cpp:596

        // Re-side the station to the capturing side (fsship.cpp:631: SetSide(pside)).
        SideId = newSideId;
    }

    /// <summary>
    /// Regrow the station's health pools — the port of <c>CstationIGC::Update</c>'s
    /// regen block (<c>stationIGC.h:189-213</c>). The native sequencing is
    /// reproduced exactly: <b>armor regenerates first</b> (<c>stationIGC.h:195-200</c>);
    /// only when the armor is already full does the shield regenerate
    /// (<c>stationIGC.h:201-208</c>), gated on a non-zero shield pool. Rates are
    /// the decoded <c>ArmorRegeneration</c> / <c>ShieldRegeneration</c>
    /// (<c>DataStationTypeIGC.cs:34-35</c>) — scaled by the owning side's
    /// <c>c_gaArmorRegenerationStation</c> / <c>c_gaShieldRegenerationStation</c>,
    /// and divided by the GA-scaled maxima, exactly as native
    /// (<c>stationIGC.h:197</c>: <c>dt·GetArmorRegeneration()/GetMaxArmorHitPoints()</c>;
    /// <c>stationIGC.h:207</c>: <c>dt·GetShieldRegeneration()/GetMaxShieldHitPoints()</c>).
    /// <paramref name="dt"/> is the elapsed sim seconds for this tick; both
    /// fractions clamp at 1.0 so floating error never overshoots (defensive, as
    /// Asteroid.Regenerate). Because the GA multiplies both the numerator and
    /// the denominator of the regen ratio, a raised max raises the time to
    /// re-fill while a raised regen shortens it — the native behaviour.
    /// </summary>
    public void Regenerate(float dt)
    {
        if (dt <= 0.0f) return;
        if (ArmorFraction != 1.0f)
        {
            if (MaxArmor > 0.0f)
            {
                ArmorFraction += dt * EffectiveArmorRegen() / EffectiveMaxArmor();   // stationIGC.h:197 (GA-scaled)
                if (ArmorFraction > 1.0f) ArmorFraction = 1.0f;             // stationIGC.h:198-199
            }
        }
        else if (ShieldFraction != 1.0f && HasShield)
        {
            ShieldFraction += dt * EffectiveShieldRegen() / EffectiveMaxShield();     // stationIGC.h:207 (GA-scaled)
            if (ShieldFraction > 1.0f) ShieldFraction = 1.0f;
        }
    }

    /// <summary>
    /// <c>GetDamageConstant(type, defenseType)</c> (<c>stationIGC.cpp:138,159</c>):
    /// the decoded damage matrix <c>DamageConstants[dmgid][defid]</c> from the
    /// mission's <see cref="DataConstantsIGC"/>, indexed by the station <b>type's</b>
    /// defense indices (<c>DefenseTypeArmor</c>/<c>DefenseTypeShield</c>). The
    /// <see cref="Mission"/>/<see cref="StaticCore"/> is derived from the ordnance
    /// launcher the same way the call-site reaches the host seam — the launcher is
    /// a <see cref="Ship"/>, its <see cref="Ship.Mission"/>.core. Returns 1.0
    /// (neutral) when there is no Ship launcher/mission or the indexes lie outside
    /// the 20×20 matrix, so a stray damage type can never amplify/dilute a hit
    /// into nonsense. Data-driven from <see cref="DataConstantsIGC"/>, not hardcoded
    /// — the same shape as <see cref="Ship.DamageConstant(byte, byte)"/> and
    /// <see cref="Asteroid"/>.DamageConstant (composition over inheritance; the
    /// station keeps its own copy like Asteroid does).
    /// </summary>
    private float DamageConstant(byte type, byte defenseType, Model? launcher)
    {
        var core = Mission?.Core ?? (launcher as Ship)?.Mission?.Core;
        if (core is null) return 1.0f;
        var dm = core.Constants.DamageConstants;
        int dmg = type & BlastDamage.Mask, def = defenseType; // missionigc.h:155
        if (dmg < 0 || dmg >= DataConstantsIGC.DmgidMax || def < 0 || def >= DataConstantsIGC.DefidMax)
            return 1.0f;
        return MathF.Max(dm[dmg][def], 0.0f);
    }
}
