using System.Collections.Generic;
using Allegiance.Core.Collision;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// One ambient-dust grain the camera-relative field holds: its fixed world
/// position, the native bluish-white colour (<c>r∈[0.5,0.75], g,b∈[0.75,1.0]</c>,
/// debrisgeo.cpp:149-154) and the wall-clock it was born at (native
/// <c>m_startTime</c>). Grains do not move — the whole sense-of-speed cue comes
/// from the camera streaming past them — so a grain is fully described by these
/// three values and its current brightness is derived from its age via
/// <see cref="DebrisGeo.Brightness"/>.
/// </summary>
public readonly record struct DebrisPoint(SimVec Position, SimVec Colour, float StartTime);

/// <summary>
/// P449a — the pure, MonoGame-free port of native ambient space dust
/// (<c>DebrisGeo</c>, <c>effect/debrisgeo.cpp:17-205</c>): a camera-relative
/// point field that gives the player a sense of speed and drift in open space.
/// Kept as a separate small class with the math fully unit-testable (the port's
/// established convention for <see cref="DamageDebrisParticles"/> /
/// <see cref="WarpTransitParticles"/>), it reproduces native's evaluate step
/// exactly: every <c>distancePerParticle</c> of camera travel it spawns a
/// bluish-white point ahead of the camera in a forward cone whose lateral radius
/// is <c>rfocus·distance</c> (<c>rfocus = 1/focus</c>), fades each point by
/// <c>1 − 0.25·t²</c> over 2 s, culls points that fade or travel beyond 500 m,
/// skips the near-camera 20 m shell at render time, and clears the whole field on
/// a jump (<c>&gt; 100 m</c> camera travel in one step — native's rule, which a
/// warp/teleport/sector change always exceeds).
/// </summary>
/// <remarks>
/// <para><b>Native relationship (debrisgeo.cpp).</b> Each <c>Evaluate()</c>:</para>
/// <list type="bullet">
/// <item>measures how far the camera moved since the last step (<c>length</c>);</item>
/// <item>computes a pseudo-speed <c>m_speed = Δt / length</c> (reciprocal speed —
/// <em>the comment at :90 says "more stars at lower speed" but the code as written
/// makes <c>distancePerParticle</c> smaller — denser — when <c>m_speed</c> is
/// small, i.e. fast; this port reproduces the CODE, not the comment</em>);</item>
/// <item>sets <c>distancePerParticle = density − (0.3 − 0.004·m_speed)</c> when
/// <c>m_speed &lt; 75</c>, else <c>density</c> (:92-96);</item>
/// <item>clears the list when <c>length &gt; 100</c> or <c>distancePerParticle == 0</c>
/// (a jump, or dust turned off — the native "LANS" comment at :102) (:98-106);</item>
/// <item>else culls points whose brightness fell to ≤ 0 or whose distance² &gt; 250000
/// (&gt; 500 m) (:112-126);</item>
/// <item>accumulates <c>length</c> into <c>m_distanceTravelledSinceLastParticle</c>
/// and, while it exceeds <c>distancePerParticle</c>, spawns a point
/// (:128-157): <c>pos = position + dist·forward + c·right + s·up</c> with
/// <c>dist = maxDistance·√random</c>, <c>radius = rfocus·dist·√random</c>,
/// <c>c = radius·cos(θ)</c>, <c>s = radius·sin(θ)</c>, <c>θ ∈ [0, 2π)</c> —
/// a forward cone whose widest grains sit at roughly the view edge.</item>
/// </list>
/// <para><b>Render side</b> (in <see cref="AmbientDustRenderer"/>): a point is drawn
/// as a camera-facing decal at <c>scale = 0.1/focus</c> coloured
/// <c>brightness·colour</c>, skipped when its distance² ≤ 400 (within 20 m),
/// debrisgeo.cpp:163-190. Because the decal scale carries a <c>1/focus</c> and the
/// perspective divide also carries a <c>focus</c>, a grain's on-screen angular
/// size is <em>independent</em> of the camera FOV — exactly why native can bake
/// <c>focus</c> into both the placement cone and the decal size without retuning
/// per FOV.</para>
/// </remarks>
public sealed class DebrisGeo
{
    // ── Native constants (see class remarks for file:line provenance) ─────
    /// <summary><c>maxDistance = 100</c> — the farthest a grain spawns ahead of
    /// the camera (debrisgeo.cpp:14).</summary>
    public const float MaxDistance = 100f;

    /// <summary><c>length &gt; 100</c> counts as a jump and clears the field
    /// (debrisgeo.cpp:98) — a warp/teleport/sector change always exceeds this.</summary>
    public const float JumpDistance = 100f;

    /// <summary>A grain is culled once its squared distance from the camera
    /// exceeds 250000, i.e. beyond 500 m (debrisgeo.cpp:121).</summary>
    public const float CullDistanceSquared = 250000f;

    /// <summary>The <c>m_speed &lt; 75</c> threshold that decides whether the
    /// density adjustment applies (debrisgeo.cpp:92).</summary>
    public const float SpeedHigh = 75f;

    /// <summary>The constant term <c>0.3</c> in the density adjustment
    /// (debrisgeo.cpp:93).</summary>
    public const float DensityFlat = 0.3f;

    /// <summary>The slope <c>0.004</c> in the density adjustment
    /// (debrisgeo.cpp:93).</summary>
    public const float DensitySlope = 0.004f;

    /// <summary>Grains within this squared distance of the camera (≤ 400, i.e.
    /// within 20 m) are skipped at render time (debrisgeo.cpp:177).</summary>
    public const float NearCameraSkipSquared = 400f;

    private readonly float _density;      // native m_userDistancePerParticle (0 = off)
    private readonly ITrailRandom _rng;   // native random(a,b) seam
    private readonly List<DebrisPoint> _points = new();

    private float _timeLast;
    private float _posLX, _posLY, _posLZ;
    private float _accumulated;           // native m_distanceTravelledSinceLastParticle
    private float _speed;                 // native m_speed
    private float _distancePerParticle;   // native distancePerParticle

    /// <summary>Construct an empty field. <paramref name="density"/> mirrors
    /// native's user <c>Debris</c> preference (default 1.0; 0 disables it, and
    /// the native jump-clear path treats 0 as off). <paramref name="rng"/> is
    /// the randomness seam (native <c>random(a,b)</c>); <paramref name="initialPosition"/>
    /// / <paramref name="initialTime"/> seed the "last step" state so the first
    /// <see cref="Step"/> measures against them (native seeds them from the
    /// camera at construction, debrisgeo.cpp:49-50).</summary>
    public DebrisGeo(float density, ITrailRandom rng,
        SimVec initialPosition = default, float initialTime = 0f)
    {
        _density = density;
        _rng = rng;
        _posLX = initialPosition.X;
        _posLY = initialPosition.Y;
        _posLZ = initialPosition.Z;
        _timeLast = initialTime;
    }

    /// <summary>Number of live grains in the field.</summary>
    public int Count => _points.Count;

    /// <summary>The native pseudo-speed <c>m_speed = Δt / length</c> computed by
    /// the most recent <see cref="Step"/> (surfaced for diagnostics/tests).</summary>
    public float Speed => _speed;

    /// <summary>The <c>distancePerParticle</c> the most recent <see cref="Step"/>
    /// arrived at (native debrisgeo.cpp:92-96) — the metres of camera travel per
    /// newly-spawned grain. Surfaced so tests can drive exact spawn counts.</summary>
    public float CurrentDistancePerParticle => _distancePerParticle;

    /// <summary>The live grains, in spawn order (oldest first).</summary>
    public IReadOnlyList<DebrisPoint> Points => _points;

    /// <summary>Native brightness fade <c>1 − 0.25·t²</c> (debrisgeo.cpp:118,174):
    /// 1 at birth, 0.75 at 1 s, 0 at 2 s. The renderer tints each grain by this.</summary>
    public static float Brightness(float ageSec) => 1f - 0.25f * ageSec * ageSec;

    /// <summary>Native cull predicate (debrisgeo.cpp:121): a grain is removed when
    /// it has faded (brightness ≤ 0, i.e. aged ≥ 2 s) or drifted beyond 500 m.</summary>
    public static bool ShouldCull(float distanceSquared, float ageSec)
        => Brightness(ageSec) <= 0f || distanceSquared > CullDistanceSquared;

    /// <summary>
    /// One native <c>Evaluate()</c> (debrisgeo.cpp:55-161). <paramref name="position"/>
    /// is the active camera's eye (<c>pcamera-&gt;GetPosition()</c>), <paramref name="forward"/>
    /// / <paramref name="right"/> / <paramref name="up"/> its orientation axes, <paramref name="focus"/>
    /// the camera focal length (<c>pcamera-&gt;GetFocus()</c>; this port passes
    /// the projection's <c>M22 = cot(vFOV/2)</c>), and <paramref name="time"/> the
    /// clock (<c>GetTime()</c>). Because it is driven purely by the active camera's
    /// eye + basis each frame, it follows whatever pose the camera uses —
    /// including the P447b predicted render pose — in every flight camera mode.
    /// </summary>
    public void Step(SimVec position, SimVec forward, SimVec right, SimVec up, float focus, float time)
    {
        // Native treats distancePerParticle == 0 as "off" (debrisgeo.cpp:102) —
        // but with a zero user density the adjusted distancePerParticle is
        // negative (-0.3 + 0.004·m_speed), which would make the spawn `while`
        // loop unbounded. Guard the field entirely off for density ≤ 0 instead
        // (the brief's "0 = off"), keeping it empty every step.
        if (_density <= 0f)
        {
            _points.Clear();
            _accumulated = 0f;
            _timeLast = time;
            _posLX = position.X; _posLY = position.Y; _posLZ = position.Z;
            return;
        }

        float dx = position.X - _posLX, dy = position.Y - _posLY, dz = position.Z - _posLZ;
        float length = MathF.Sqrt(dx * dx + dy * dy + dz * dz);

        // debrisgeo.cpp:82-87 — the reciprocal pseudo-speed (see class remarks on
        // the density-adjustment direction).
        _speed = length != 0f ? (time - _timeLast) / length : 0f;
        _timeLast = time;

        // debrisgeo.cpp:92-96 — denser (smaller spacing) when m_speed is small.
        _distancePerParticle = _speed < SpeedHigh
            ? _density - (DensityFlat - DensitySlope * _speed)
            : _density;

        if (length > JumpDistance || _distancePerParticle == 0f)
        {
            // debrisgeo.cpp:98-106 — jumped (warp/teleport/sector change) or off.
            _points.Clear();
            _accumulated = 0f;
            length = 0f;
        }
        else
        {
            // debrisgeo.cpp:112-126 — cull faded / beyond-500 m grains.
            for (int i = _points.Count - 1; i >= 0; i--)
            {
                var p = _points[i];
                float qx = p.Position.X - position.X, qy = p.Position.Y - position.Y, qz = p.Position.Z - position.Z;
                float dsq = qx * qx + qy * qy + qz * qz;
                if (ShouldCull(dsq, time - p.StartTime))
                    _points.RemoveAt(i);
            }

            // debrisgeo.cpp:128-157 — spawn one grain per distancePerParticle travelled.
            float rfocus = 1f / focus;
            _accumulated += length;
            while (_accumulated > _distancePerParticle)
            {
                float angle = _rng.Range(0f, 2f * MathF.PI);
                float dist = MaxDistance * MathF.Sqrt(_rng.Range(0f, 1f));
                float radius = rfocus * dist * MathF.Sqrt(_rng.Range(0f, 1f));
                float c = radius * MathF.Cos(angle);
                float s = radius * MathF.Sin(angle);

                SimVec pos = position + (dist * forward) + (c * right) + (s * up);
                SimVec colour = new(
                    _rng.Range(0.50f, 0.75f),
                    _rng.Range(0.75f, 1f),
                    _rng.Range(0.75f, 1f));

                _points.Add(new DebrisPoint(pos, colour, time));
                _accumulated -= _distancePerParticle;
            }
        }

        _posLX = position.X;
        _posLY = position.Y;
        _posLZ = position.Z;
    }

    /// <summary>Drop every live grain and reset the distance accumulator (native's
    /// jump-clear is triggered by a &gt; 100 m step; this is the explicit, immediate
    /// form used by the warp/sector-change hook and tests).</summary>
    public void Clear()
    {
        _points.Clear();
        _accumulated = 0f;
    }
}
