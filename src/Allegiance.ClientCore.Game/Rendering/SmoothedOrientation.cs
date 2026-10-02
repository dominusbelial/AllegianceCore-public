using System.Numerics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// A MonoGame-free exponential lag applied to a <em>full</em> 3D rotation
/// (yaw, pitch <b>and roll</b>) — the pure, unit-testable core of the chase
/// camera's turn smoothing. Fed the ship's full orientation every frame it
/// returns that orientation delayed by a small, frame-rate-independent amount:
/// the longer the elapsed time relative to the time constant, the closer it
/// snaps toward the target, so a steady ship settles quickly while a fast
/// maneuver trails briefly behind.
/// </summary>
/// <remarks>
/// This is the port's deliberately simplified analogue of native
/// <c>ComputeExternalChase</c> (<c>WinTrek.cpp:6880-6944</c>), the real
/// function behind the chase camera (<c>cmExternalChase</c>). Native keeps a
/// ring buffer of recent per-axis turn-rate samples and integrates a genuine
/// <em>historical</em> angular displacement (yaw/pitch/roll) over a short
/// delayed window, then applies that displacement to the ship's FULL
/// orientation — which it starts from as <c>pShip-&gt;GetOrientation()</c>,
/// i.e. the ship's true, rolling orientation, never a world-up-stabilized one.
/// An exponential slerp toward the ship's current full orientation is much
/// simpler but faithful in spirit: same "brief elastic delay," same eventual
/// result that the camera ends up rigidly attached to the ship's real
/// (rolling) orientation. Crucially, because the lag operates on the WHOLE
/// rotation — including roll — it preserves a roll the way native does; it
/// never erases it into a world-up frame.
/// </remarks>
public sealed class SmoothedOrientation
{
    private readonly float _tauSeconds;
    private Quaternion? _smoothed;

    /// <param name="tauSeconds">Exponential time constant, in seconds. At 0
    /// (or negative) the smoother snaps to the target on every call — no lag
    /// at all.</param>
    public SmoothedOrientation(float tauSeconds) => _tauSeconds = tauSeconds;

    /// <summary>
    /// Advance the lag by <paramref name="deltaSeconds"/> toward
    /// <paramref name="target"/>, returning the smoothed orientation.
    /// The first call (no prior orientation) — or a zero/negative time
    /// constant — snaps straight to <paramref name="target"/> with no lag.
    /// Assumes <paramref name="target"/> is a unit quaternion (every result of
    /// MonoGame's <c>Quaternion.CreateFromRotationMatrix</c> is).
    /// </summary>
    public Quaternion Update(Quaternion target, float deltaSeconds)
    {
        if (_smoothed is not { } prev || _tauSeconds <= 0f)
        {
            _smoothed = Normalize(target);
            return _smoothed.Value;
        }

        // t is the same frame-rate-independent blend factor the camera's old
        // forward-only lag used (1 - exp(-dt/tau)); applying it in slerp space
        // is the quaternion analogue of that linear lerp, extended to the full
        // orientation. Quaternion.Slerp also takes the shortest arc, so an
        // equivalent rotation expressed the "other way round" (e.g. -350° vs
        // +10°) is approached via the near path, never the long way around.
        float t = 1f - MathF.Exp(-deltaSeconds / _tauSeconds);
        _smoothed = Normalize(Quaternion.Slerp(prev, target, t));
        return _smoothed.Value;
    }

    /// <summary>
    /// Normalize a quaternion, guarding against the only degenerate input that
    /// would otherwise poison it: an all-zero quaternion, which has no axis and
    /// normalizing would divide by zero. Falls back to the identity rotation.
    /// </summary>
    private static Quaternion Normalize(Quaternion q)
    {
        float lenSq = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        if (lenSq < 1e-12f)
            return Quaternion.Identity;
        float inv = 1f / MathF.Sqrt(lenSq);
        return new Quaternion(q.X * inv, q.Y * inv, q.Z * inv, q.W * inv);
    }
}
