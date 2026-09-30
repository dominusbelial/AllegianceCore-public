using System.Collections.Generic;
using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>A single live shield-flare as the pool enumerates it for the
/// renderer. <see cref="Center"/> is the flare's world centre — sitting ON the
/// struck ship's shield bubble toward the hit (<c>shipCentre + unitDir ·
/// shieldRadius</c>); <see cref="Direction"/> is the (unit) outward hit
/// direction; <see cref="Size"/> is the quad's half-extent in world units
/// (proportional to the ship's real render scale); <see cref="RemainingFrac"/>
/// is <c>(lifetime − age)/lifetime</c> in (0, 1], driving the bright fade.</summary>
public readonly record struct ShieldFlareView(
    Vector3 Center,
    Vector3 Direction,
    float Size,
    float RemainingFrac);

/// <summary>
/// The pure, MonoGame-free flare pool for shield-absorbed hits (P463b) — a
/// faithful port of native <c>ThingGeo::AddFlare</c>
/// (<c>effect/thinggeo.cpp:1243-1301</c>), the client cue that lights up the
/// struck ship's shield bubble where a shield-absorbed hit lands. Native fires
/// it from <c>CshieldIGC::ApplyDamage</c> (<c>src/Igc/shieldIGC.cpp:170-171</c>):
/// every shield-absorbed strike (except a <c>c_dmgidNoFlare</c> type) calls
/// <c>m_ship-&gt;GetThingSite()-&gt;AddFlare(timeCollision, deltaP, 0,
/// GetEllipseEquation())</c>, which lands in <c>TrekIGC::ThingSiteImpl::AddFlare</c>
/// (<c>src/WinTrek/trekigc.cpp:1624-1640</c>) and there the client-side
/// <c>ThingGeo::AddFlare</c> places a bright additive lens on the shield.
/// </summary>
/// <remarks>
/// <para><b>Native geometry (thinggeo.cpp:1243-1301).</b> <c>AddFlare</c>
/// transforms the (negated) hit direction into the ship's local space
/// (<c>vecLocal = m_orientation.TimesInverse(-vecDirection)</c>, :1252), then —
/// when an ellipse equation is supplied — divides each local component by the
/// hit-test ellipsoid's per-axis extent (:1255-1258) so the direction points
/// from the centre out to the impact point on the ellipsoid, orients the flare
/// geo along it (<c>orient.Set(vector)</c>) and scales it by
/// <c>ellipse / m_radius</c> (:1259-1261) so the flare sits on the shield
/// surface at the hit. The geo is wrapped in a <c>TimedGeo</c> over
/// <b>0.75&#8209;s</b> (:1290-1301) and blended <c>BlendModeAdd</c> (:1297), so
/// the flare flashes on then fades off over 0.75&#8209;s. Admission is gated at
/// <c>GetFlareCount() &lt; 4</c> (trekigc.cpp:1626) — at most four live flares
/// per ship.</para>
/// <para><b>Deliberate port substitutions (all documented).</b> (1) The client
/// has no static per-axis hull hit-test extents to build the true ellipsoid, so
/// the port approximates the shield as a <b>sphere</b> of the ship's real
/// render scale — <c>0.5 × HullType.Length</c> (the same P333
/// <c>_hullLengthById</c> measure the whole renderer uses) — and places the
/// flare at <c>centre + unitDir · radius</c> on that sphere toward the hit
/// (the wire's <c>Normal</c> is the outward hit direction). (2) The native
/// flare is centred on the ship and scaled to roughly the ship's radius
/// (mesh-bounding-radius scale, :1289); a surface-centered soft quad of half
/// extent <see cref="SizeFactor"/> × radius reads as the same "shield bubble
/// lights up at the hit" cue and stays legible against the hull, so that is
/// the ported shape. (3) The arbitrary perpendicular orientation axes native
/// picks for the billboard are irrelevant to a camera-facing sprite, so the
/// port tracks only the surface point + outward direction. The <c>c_dmgidNoFlare</c>
/// exemption and the <c>GetFlareCount() &lt; 4</c> cap are reproduced (the latter
/// per-ship here, via <see cref="Emit"/>'s eviction of that target's oldest).</para>
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

    /// <summary>Flare quad half-extent as a fraction of the ship's shield
    /// radius — the ported shape of native's mesh-radius-scale lens
    /// (thinggeo.cpp:1289).</summary>
    public const float SizeFactor = 0.5f;

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
        public float Size;
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
    /// (thinggeo.cpp:1243-1301): place a flare on the struck ship's (sphere-
    /// approximated) shield toward <paramref name="hitDirection"/>, sized from
    /// <paramref name="shieldRadius"/> (the ship's real render scale). Honors
    /// the native per-ship cap of <see cref="MaxFlaresPerShip"/> by evicting
    /// that target's oldest flare when full (trekigc.cpp:1626).
    /// </summary>
    /// <param name="target">The struck ship's object id (per-ship cap key).</param>
    /// <param name="shipCenter">The struck ship's world centre.</param>
    /// <param name="hitDirection">The outward hit direction (native <c>deltaP</c>;
    /// from the wire, the world hit normal <c>normalize(position1 − position2)</c>).</param>
    /// <param name="shieldRadius">The struck ship's real render scale
    /// (<c>0.5 × HullType.Length</c>); ≤ 0 falls back to
    /// <see cref="ShipRadiusDefault"/>.</param>
    public void Emit(short target, Vector3 shipCenter, Vector3 hitDirection, float shieldRadius)
    {
        float radius = shieldRadius > 0f ? shieldRadius : ShipRadiusDefault;

        float lenSq = hitDirection.LengthSquared();
        var dir = lenSq > 1e-12f ? hitDirection.Normalize() : new Vector3(1f, 0f, 0f);

        var center = shipCenter + (dir * radius);

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
            Center = center,
            Direction = dir,
            Size = SizeFactor * radius,
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
            yield return new ShieldFlareView(s.Center, s.Direction, s.Size, rem);
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
