using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>What the director is shooting right now.</summary>
public enum ShotKind
{
    Chase,        // chase the own ship
    Orbit,        // orbit the own ship (native cmExternalOrbit)
    FlyBy,        // native cmExternalFlyBy midpoint pass (you + subject)
    FlyByTarget,  // native cmExternalFlyByTarget generic external framing the subject
    Target,       // native cmExternalTarget
    Station,      // native cmExternalStation
    Pass,         // P565 InspectorFlyBy cinematic pass path along the subject
}

/// <summary>The semantic category of a potential film subject (P580 scope:
/// nearest explosion, engagement, the followed ship, an aleph transit, a station).</summary>
public enum SubjectCategory
{
    Explosion,
    Engagement,
    Ship,     // the followed ship
    Warp,     // aleph transit
    Station,
}

/// <summary>A potential film subject with its world position, category and the
/// caller's interest score (higher = more compelling). The caller (Game1)
/// gathers and scores these from the live world each frame.</summary>
public readonly record struct DirectorSubject(
    Vector3 Position, SubjectCategory Category, float Interest, string Label);

/// <summary>The per-frame world facts the director needs to decide. Pure and
/// free of the Game's world types so the planner is unit-testable.</summary>
public readonly record struct DirectorSnapshot(
    IReadOnlyList<DirectorSubject> Subjects,
    Vector3 OwnPosition,
    bool SectorChanged,     // warp or dock transition happened this frame
    bool Docked);           // the pilot is docked / a spectator

/// <summary>
/// P580 — the non-native "director" b-roll camera (opt-in, <c>--director</c> or a
/// key): an automatic cinematographer for recording showcases. It owns ONLY the
/// editorial decisions — which shot style to film, which subject to frame, how
/// long to dwell, and the smooth convergence of the rendered pose toward the
/// intended one — and is pure/GPU-free so the decision logic is unit-testable.
/// <para>
/// Behaviour, per the P580 brief: cycles automatically between the native modes
/// and the P565 fly-by path; picks interesting subjects (nearest explosion or
/// engagement, the followed ship, an aleph transit, a station); dwells ~4-8 s per
/// shot; transitions smoothly (no snapping); re-acquires a subject after a warp
/// or a dock (never stalls); and is seeded so a recording can be repeated.
/// Whether any flight input cancels it is decided by the caller (Game1) — this
/// class has no input knowledge.
/// </para>
/// <para>
/// Determinism: with the same seed, the same <see cref="DirectorSnapshot"/>
/// stream yields the same shot kinds, subject choices and dwell times, so a
/// director recording is reproducible shot-for-shot.
/// </para>
/// </summary>
public sealed class Director
{
    private Random _rng;
    private readonly float _minDwell, _maxDwell, _transitionTau;

    // current editorial state
    private ShotKind _kind = ShotKind.Orbit;
    private int _subjectIndex = -1;          // index into the most recent snapshot
    private double _timeInShot;              // seconds in the current shot
    private double _dwell;                   // seconds before the next shot

    // pose smoothing (the rig)
    private Vector3 _eye, _lookAt, _up;
    private bool _havePose;
    private float _hullLength = 60f;         // for orbit offset sizing

    /// <summary>Re-seed the director (deterministic recordings via
    /// <c>--director-seed</c> / ALLEGIANCE_DIRECTOR_SEED) and restart its editorial
    /// state so the same seed reproduces the same shot sequence.</summary>
    public void Reseed(int seed)
    {
        _rng = new Random(seed);
        _timeInShot = 0.0;
        _dwell = 4.0;
        _subjectIndex = -1;
        _havePose = false;
        ShotChanges = 0;
    }

    /// <summary>P580 — the "any flight input returns control instantly" rule
    /// (pure + testable): true when any flight control is currently pressed.
    /// The director is cinematic only; the moment the pilot touches the controls
    /// the director hands the camera back. The caller feeds the actual key state
    /// into this predicate.</summary>
    public static bool AnyFlightControl(
        bool forward, bool back, bool strafeLeft, bool strafeRight,
        bool thrustUp, bool thrustDown, bool turnLeft, bool turnRight)
        => forward || back || strafeLeft || strafeRight || thrustUp || thrustDown || turnLeft || turnRight;

    /// <summary>Number of shot changes issued so far (informational/testable).</summary>
    public int ShotChanges { get; private set; }

    public Director(int seed, float minDwell = 4f, float maxDwell = 8f, float transitionTau = 1.2f)
    {
        _rng = new Random(seed);
        _minDwell = minDwell;
        _maxDwell = maxDwell;
        _transitionTau = transitionTau;
        _dwell = 4.0;
    }

    /// <summary>The current shot kind (testable).</summary>
    public ShotKind Kind => _kind;

    /// <summary>Index of the current subject into the snapshot passed to
    /// <see cref="Update"/> (testable; -1 = no subject, chasing the ship).</summary>
    public int SubjectIndex => _subjectIndex;

    /// <summary>Total accumulated dwell of the current shot (testable).</summary>
    public double TimeInShot => _timeInShot;

    /// <summary>
    /// Advance the director by <paramref name="dt"/> and produce the rendered
    /// pose for this frame. Pure in the sense that it only reads
    /// <paramref name="snap"/> and its own state.
    /// </summary>
    /// <param name="shipBackward">The free external camera's backward axis
    /// (the ship's backward at mode-entry, spun by orbit), used as the backing
    /// direction of the fixed-offset modes.</param>
    /// <param name="ownRadius">The own ship's real rendered radius (0.5 × Length).</param>
    /// <param name="hullLength">The own ship's real HullType length (orbit sizing).</param>
    /// <param name="aspect">Viewport aspect ratio.</param>
    public (Matrix View, Matrix Proj, Vector3 Eye, bool ShotChanged) Update(
        double dt, DirectorSnapshot snap, Vector3 shipBackward, float ownRadius, float hullLength, float aspect)
    {
        _hullLength = hullLength > 0f ? hullLength : _hullLength;

        // Surviving warps/docking: a sector change forces a re-acquisition now
        // (there may be nothing worth filming where we just were), and while
        // docked we keep filming (framing the station) rather than stalling.
        if (snap.SectorChanged)
            _timeInShot = _dwell + 1.0;   // force a fresh pick this frame

        _timeInShot += dt;
        bool shotChanged = false;
        if (_timeInShot >= _dwell || _subjectIndex < 0 || _subjectIndex >= snap.Subjects.Count)
        {
            PickShot(snap, ref shotChanged);
        }

        var (targetEye, targetLook, targetUp) = BuildTarget(snap, shipBackward, ownRadius);

        // Smooth convergence (no snapping): exponentially blend the rendered
        // pose toward the target so a shot change flies the camera to the new
        // framing rather than cutting.
        if (!_havePose)
        {
            _eye = targetEye; _lookAt = targetLook; _up = targetUp;
            _havePose = true;
        }
        else
        {
            float k = 1f - MathF.Exp(-(float)dt / Math.Max(_transitionTau, 1e-3f));
            _eye = Vector3.Lerp(_eye, targetEye, k);
            _lookAt = Vector3.Lerp(_lookAt, targetLook, k);
            _up = Vector3.Lerp(_up, targetUp, k);
            if (_up.LengthSquared() < 1e-6f) _up = Vector3.Up;
            _up.Normalize();
        }

        var view = Matrix.CreateLookAt(Vector3.Zero, _lookAt - _eye, _up);
        var proj = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.ToRadians(ExternalCameras.DefaultFovDegrees), aspect,
            ExternalCameras.NearPlane, ExternalCameras.FarPlane);
        return (view, proj, _eye, shotChanged);
    }

    private void PickShot(DirectorSnapshot snap, ref bool shotChanged)
    {
        _timeInShot = 0.0;
        _dwell = _minDwell + _rng.NextDouble() * (_maxDwell - _minDwell);

        // While docked we film the station (native cmExternalStation framing).
        if (snap.Docked && snap.Subjects.Count > 0)
        {
            _subjectIndex = IndexOfBest(snap, SubjectCategory.Station);
            _kind = _subjectIndex >= 0 ? ShotKind.Station : ShotKind.Orbit;
            if (_subjectIndex < 0) _subjectIndex = 0;
        }
        else
        {
            // Cycle through the aerial shot styles (never identical twice in a
            // row): Chase, Orbit, FlyBy, Target, Pass, then back.
            _kind = NextKind(_kind);
            _subjectIndex = PickSubject(snap);
        }

        ShotChanges++;
        shotChanged = true;
    }

    private int PickSubject(DirectorSnapshot snap)
    {
        if (snap.Subjects.Count == 0)
            return -1;   // nothing to film — chase the ship

        // Weighted-random subject choice favouring higher interest, but always
        // able to pick any candidate so the same seed's recording varies.
        float total = 0f;
        foreach (var s in snap.Subjects) total += MathF.Max(0.005f, s.Interest);
        double roll = _rng.NextDouble() * total;
        float acc = 0f;
        for (int i = 0; i < snap.Subjects.Count; i++)
        {
            acc += MathF.Max(0.005f, snap.Subjects[i].Interest);
            if (roll <= acc) return i;
        }
        return snap.Subjects.Count - 1;
    }

    private static int IndexOfBest(DirectorSnapshot snap, SubjectCategory cat)
    {
        int best = -1; float bestI = float.NegativeInfinity;
        for (int i = 0; i < snap.Subjects.Count; i++)
        {
            if (snap.Subjects[i].Category == cat && snap.Subjects[i].Interest > bestI)
            { bestI = snap.Subjects[i].Interest; best = i; }
        }
        return best;
    }

    private static ShotKind NextKind(ShotKind current) => current switch
    {
        ShotKind.Chase => ShotKind.Orbit,
        ShotKind.Orbit => ShotKind.FlyBy,
        ShotKind.FlyBy => ShotKind.Target,
        ShotKind.Target => ShotKind.Pass,
        ShotKind.Pass => ShotKind.Chase,
        _ => ShotKind.FlyBy,
    };

    private (Vector3 Eye, Vector3 Look, Vector3 Up) BuildTarget(
        DirectorSnapshot snap, Vector3 shipBackward, float ownRadius)
    {
        var own = snap.OwnPosition;
        var backward = SafeNormalize(shipBackward, -Vector3.UnitZ);
        var subject = _subjectIndex >= 0 && _subjectIndex < snap.Subjects.Count
            ? snap.Subjects[_subjectIndex] : (DirectorSubject?)null;

        switch (_kind)
        {
            case ShotKind.Chase:
            {
                // Approximate native ComputeExternalChase: behind and above, look at ship.
                var up = ExternalCameras.WorldUp(backward);
                var eye = own - backward * Math.Max(ownRadius * 8f, 90f) + up * Math.Max(ownRadius * 2.5f, 30f);
                return (eye, own, up);
            }
            case ShotKind.Orbit:
            {
                var ev = ExternalCameras.Orbit(own, backward, _hullLength);
                return (ev.Eye, ev.LookAt, ev.Up);
            }
            case ShotKind.FlyBy:
            {
                if (subject is { } s)
                {
                    var ev = ExternalCameras.FlyBy(s.Position, own, backward, ownRadius, 0f);
                    return (ev.Eye, ev.LookAt, ev.Up);
                }
                var g = ExternalCameras.GenericExternal(own, backward, ownRadius * 8f, ownRadius);
                return (g.Eye, g.LookAt, g.Up);
            }
            case ShotKind.Target:
            {
                if (subject is { } t)
                {
                    var ev = ExternalCameras.Target(t.Position, backward, subjectRadius(t), 0f);
                    return (ev.Eye, ev.LookAt, ev.Up);
                }
                var tg = ExternalCameras.GenericExternal(own, backward, ownRadius * 8f, ownRadius);
                return (tg.Eye, tg.LookAt, tg.Up);
            }
            case ShotKind.FlyByTarget:
            case ShotKind.Station:
            {
                if (subject is { } s2)
                {
                    var ev = _kind == ShotKind.Station
                        ? ExternalCameras.Station(own, s2.Position, ownRadius)
                        : ExternalCameras.Target(s2.Position, backward, subjectRadius(s2), 0f);
                    return (ev.Eye, ev.LookAt, ev.Up);
                }
                var sg = ExternalCameras.GenericExternal(own, backward, ownRadius * 8f, ownRadius);
                return (sg.Eye, sg.LookAt, sg.Up);
            }
            default: // Pass — the P565 cinematic pass path along the subject
            {
                if (subject is { } p)
                {
                    var fb = new InspectorFlyBy
                    {
                        Target = p.Position,
                        Axis = -backward,          // travel along the backward axis
                        ApproachDistance = MathF.Max(ownRadius * 12f, 300f),
                        PassOffset = MathF.Max(ownRadius * 3f, 60f),
                        Side = InspectorFlyBy.PassSide.Right,
                        DurationSeconds = (float)_dwell,
                        Loop = true,
                        LookAtTarget = true,
                    };
                    var eye = fb.EyeAtT((float)Math.Min(1.0, _timeInShot / _dwell));
                    return (eye, p.Position, Vector3.Up);
                }
                var pg = ExternalCameras.GenericExternal(own, backward, ownRadius * 8f, ownRadius);
                return (pg.Eye, pg.LookAt, pg.Up);
            }
        }
    }

    private static float subjectRadius(DirectorSubject s) => 30f;   // default framing radius

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fb)
    {
        if (v.LengthSquared() < 1e-8f) return fb;
        v.Normalize();
        return v;
    }
}
