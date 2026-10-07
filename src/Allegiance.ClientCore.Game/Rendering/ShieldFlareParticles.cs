using System.Collections.Generic;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A single live shield-flare as the pool enumerates it for the
/// renderer. <see cref="Center"/> is the struck ship's world centre;
/// <see cref="Direction"/> is the (unit) outward hit direction
/// (<c>normalize(hitWorldPos − center)</c>, the wire's <c>Normal</c>);
/// <see cref="RemainingFrac"/> is <c>(lifetime − age)/lifetime</c> in (0, 1],
/// driving the bright fade. The remaining fields are the native
/// <c>ThingGeo::AddFlare</c> inputs, snapshotted at emit time so the curved cap
/// (P569) is frozen exactly as native freezes it in the <c>TimedGeo</c>:
/// <see cref="ShipOrientation"/> (m_orientation), <see cref="HitWorldPos"/>
/// (vecPosition), <see cref="Ellipse"/> (the shield ellipsoid),
/// <see cref="Radius"/> (m_radius = 0.5·Length) and <see cref="MeshRadius"/>
/// (m_radiusMesh).</summary>
public readonly record struct ShieldFlareView(
    Vector3 Center,
    Vector3 Direction,
    float RemainingFrac,
    Orientation ShipOrientation,
    Vector3 HitWorldPos,
    Vector3 Ellipse,
    float Radius,
    float MeshRadius);

/// <summary>
/// The pure, MonoGame-free flare pool for shield-absorbed hits (P463b, P569) —
/// a faithful port of native <c>ThingGeo::AddFlare</c>
/// (<c>effect/thinggeo.cpp:1243-1301</c>), the client cue that lights up the
/// struck ship's shield where a shield-absorbed hit lands. Native fires it from
/// <c>CshieldIGC::ApplyDamage</c> (<c>src/Igc/shieldIGC.cpp:170-171</c>): every
/// shield-absorbed strike (except a <c>c_dmgidNoFlare</c> type) calls
/// <c>m_ship-&gt;GetThingSite()-&gt;AddFlare(timeCollision, deltaP, 0,
/// GetEllipseEquation())</c>, which lands in <c>TrekIGC::ThingSiteImpl::AddFlare</c>
/// (<c>src/WinTrek/trekigc.cpp:1624-1640</c>) and there the client-side
/// <c>ThingGeo::AddFlare</c> places a bright additive lens cap on the shield.
/// </summary>
/// <remarks>
/// <para><b>Native geometry (thinggeo.cpp:1243-1305).</b> <c>AddFlare</c>
/// transforms the (negated) hit direction into the ship's local space
/// (<c>vecLocal = m_orientation.TimesInverse(-vecDirection)</c>, :1252), then —
/// when an ellipse equation is supplied — divides each local component by the
/// hit-test ellipsoid's per-axis extent (:1255-1258) so the direction points
/// from the centre out to the impact point on the ellipsoid, orients the cap
/// geo along it (<c>orient.Set(vector)</c>) and scales it by
/// <c>ellipse / m_radius</c> (:1259-1261) so the cap hugs the shield surface at
/// the hit. The geo is wrapped in a <c>TimedGeo</c> over
/// <b>0.75&#8209;s</b> (:1290-1301) and blended <c>BlendModeAdd</c> (:1297), so
/// the flare flashes on then fades off over 0.75&#8209;s. Admission is gated at
/// <c>GetFlareCount() &lt; 4</c> (trekigc.cpp:1626) — at most four live flares
/// per ship.</para>
/// <para><b>P569 change vs P545.</b> P545 approximated the shield as a sphere
/// and the renderer drew a flat tangent quad. P569 upgrades the pool to
/// snapshot the native <c>AddFlare</c> inputs per flare (ship orientation,
/// hit position, shield ellipse, radius, mesh radius — see
/// <see cref="ShieldFlareCapMath"/>) so the renderer can draw the REAL curved
/// <c>lens30</c> cap wrapped onto the shield ellipsoid. The per-axis ellipse
/// is the struck ship's native-mesh extent (<see cref="NativeShipResolver.MergedExtents"/>),
/// the client's proxy for native's hull hit-test <c>GetEllipseEquation()</c>;
/// it degrades to a sphere of <c>0.5·Length</c> when the native mesh is
/// unavailable. The <c>c_dmgidNoFlare</c> exemption and the
/// <c>GetFlareCount() &lt; 4</c> cap are reproduced (the latter per-ship here,
/// via <see cref="Emit"/>'s eviction of that target's oldest).</para>
/// </remarks>
public sealed class ShieldFlareParticles
{
    // ── Native constants (see class remarks for provenance) ──────────────
    /// <summary>Flare lifetime: native <c>TimedGeo(..., 0.75f)</c>
    /// (thinggeo.cpp:1304).</summary>
    public const float LifetimeSeconds = 0.75f;

    /// <summary>Native per-ship admission cap <c>GetFlareCount() &lt; 4</c>
    /// (trekigc.cpp:1626).</summary>
    public const int MaxFlaresPerShip = 4;

    /// <summary>Fallback for the struck ship's render radius — used only when
    /// the caller has no hull length or wire radius (≤ 0), so the flare still
    /// renders rather than collapsing to size 0. Mirrors
    /// <see cref="HullHitSparkParticles.ShipRadiusDefault"/>.</summary>
    public const float ShipRadiusDefault = 50f;

    /// <summary>Default fixed pool capacity (well above any realistic
    /// simultaneous flare load; each flare is 0.75&#8209;s and capped at four
    /// per ship).</summary>
    public const int DefaultCapacity = 64;

    private struct Slot
    {
        public short Target;
        public Vector3 Center;
        public Vector3 Direction;
        public Orientation ShipOrientation;
        public Vector3 HitWorldPos;
        public Vector3 Ellipse;
        public float Radius;
        public float MeshRadius;
        public float AgeSec;
    }

    private readonly List<Slot> _slots;

    /// <summary>Construct an empty pool.</summary>
    public ShieldFlareParticles()
    {
        _slots = new List<Slot>(DefaultCapacity);
    }

    /// <summary>Upper bound on <see cref="ActiveCount"/>.</summary>
    public int Capacity => DefaultCapacity;

    /// <summary>Number of currently live flares in the pool.</summary>
    public int ActiveCount => _slots.Count;

    /// <summary>
    /// Emit one native <c>AddFlare</c> for a shield-absorbed hit
    /// (thinggeo.cpp:1243-1301): snapshot the struck ship's pose and shield
    /// ellipsoid so the renderer can place a curved <c>lens30</c> cap wrapped
    /// onto the shield (P569). Honors the native per-ship cap of
    /// <see cref="MaxFlaresPerShip"/> by evicting that target's oldest flare
    /// when full (trekigc.cpp:1626).
    /// </summary>
    /// <param name="target">The struck ship's object id (per-ship cap key).</param>
    /// <param name="shipCenter">The struck ship's world centre.</param>
    /// <param name="shipOrientation">The struck ship's world orientation
    /// (native <c>m_orientation</c>).</param>
    /// <param name="hitWorldPos">The world hit position (native <c>deltaP</c>
    /// as an absolute point; the relative vector <c>hitWorldPos − shipCenter</c>
    /// drives <c>vecLocal</c>).</param>
    /// <param name="shieldEllipse">The struck ship's shield ellipsoid per-axis
    /// extents (native <c>GetEllipseEquation()</c>; the client uses the native
    /// mesh per-axis extents, else the <c>0.5·Length</c> sphere).</param>
    /// <param name="shieldRadius">The struck ship's collision radius
    /// (<c>0.5 × HullType.Length</c>; native <c>m_radius</c>); ≤ 0 falls back
    /// to <see cref="ShipRadiusDefault"/>.</param>
    /// <param name="meshRadius">The struck ship's mesh-bounding radius (native
    /// <c>m_radiusMesh</c>); ≤ 0 falls back to <paramref name="shieldRadius"/>.</param>
    public void Emit(
        short target,
        Vector3 shipCenter,
        Orientation shipOrientation,
        Vector3 hitWorldPos,
        Vector3 shieldEllipse,
        float shieldRadius,
        float meshRadius)
    {
        float radius = shieldRadius > 0f ? shieldRadius : ShipRadiusDefault;
        if (meshRadius <= 0f) meshRadius = radius;

        var dir = hitWorldPos - shipCenter;
        float lenSq = dir.LengthSquared();
        var dirUnit = lenSq > 1e-12f ? dir.Normalize() : new Vector3(1f, 0f, 0f);

        // Ellipse must be usable (all > 0); a zero/invalid axis degrades to the
        // radius so the cap math never divides by zero and stays spherical.
        var ellipse = new Vector3(
            shieldEllipse.X > 0f ? shieldEllipse.X : radius,
            shieldEllipse.Y > 0f ? shieldEllipse.Y : radius,
            shieldEllipse.Z > 0f ? shieldEllipse.Z : radius);

        // Native per-ship admission cap: while GetFlareCount() < 4 (trekigc.cpp:1626).
        int same = 0;
        for (int i = 0; i < _slots.Count; i++)
            if (_slots[i].Target == target) same++;
        if (same >= MaxFlaresPerShip)
            RemoveOldestFor(target);

        // Pool capacity guard (evict the global oldest when full).
        if (_slots.Count >= DefaultCapacity)
            RemoveOldest();

        _slots.Add(new Slot
        {
            Target = target,
            Center = shipCenter,
            Direction = dirUnit,
            ShipOrientation = shipOrientation ?? Orientation.Identity,
            HitWorldPos = hitWorldPos,
            Ellipse = ellipse,
            Radius = radius,
            MeshRadius = meshRadius,
            AgeSec = 0f,
        });
    }

    /// <summary>
    /// Age every live flare one frame and expire those past the native 0.75&#8209;s
    /// lifetime (<c>TimedGeo</c>, thinggeo.cpp:1304). Flares are static on the
    /// shield (no translation over their short life), so only the age moves.
    /// </summary>
    public void Update(float dtSec)
    {
        for (int i = _slots.Count - 1; i >= 0; i--)
        {
            var s = _slots[i];
            s.AgeSec += dtSec;
            if (s.AgeSec >= LifetimeSeconds)
                _slots.RemoveAt(i);
            else
                _slots[i] = s;
        }
    }

    /// <summary>Enumerate the live flares oldest-first with the remaining-life
    /// fraction for the bright fade.</summary>
    public IEnumerable<ShieldFlareView> Enumerate()
    {
        foreach (var s in _slots)
        {
            float rem = (LifetimeSeconds - s.AgeSec) / LifetimeSeconds;
            if (rem < 0f) rem = 0f;
            yield return new ShieldFlareView(
                s.Center, s.Direction, rem, s.ShipOrientation, s.HitWorldPos, s.Ellipse, s.Radius, s.MeshRadius);
        }
    }

    /// <summary>Drop every live flare (used by tests to start clean).</summary>
    public void Reset() => _slots.Clear();

    /// <summary>Remove the pool's oldest slot overall.</summary>
    private void RemoveOldest()
    {
        if (_slots.Count == 0) return;
        _slots.RemoveAt(0);
    }

    /// <summary>Remove the oldest slot belonging to <paramref name="target"/>
    /// (to honour the per-ship cap).</summary>
    private void RemoveOldestFor(short target)
    {
        for (int i = 0; i < _slots.Count; i++)
        {
            if (_slots[i].Target == target)
            {
                _slots.RemoveAt(i);
                return;
            }
        }
    }
}
