using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>A predicted world-space pose for a remote ship at a render instant.</summary>
public readonly record struct PredictedPose(Vector3 Position, Orientation Orientation);

/// <summary>
/// Smooths the jittery, discrete <c>ServerShipUpdate</c> stream for a remote
/// ship into a continuous per-frame pose — the real technique from the
/// MIT-licensed MonoGame <c>NetworkPrediction</c> sample, ported (the
/// algorithm only; that sample's UDP/XNA specifics are irrelevant here).
///
/// <para><b>Linear extrapolation.</b> Between real updates the predicted pose
/// advances along the last known velocity: <c>pos(t) = anchor + vel·(t−t₀)</c>.</para>
///
/// <para><b>Correct-toward-data-without-snapping.</b> When a new real update
/// arrives at time <c>t</c>, the render anchor does <em>not</em> jump straight
/// to it (that is the snap that makes remote ships look teleporty). Instead a
/// correction blend begins from where the extrapolation <em>would</em> have put
/// us at <c>t</c> (<c>contPos</c>) toward the authoritative new value, with a
/// decaying blend factor <c>k = 1 − e^(−(t−t_blend)/interval)</c> that completes
/// over roughly one expected inter-update interval — then extrapolation resumes
/// from the adopted anchor.</para>
///
/// <para><b>Pure logic.</b> No MonoGame / <c>Microsoft.Xna.Framework</c> types
/// anywhere in this class — it works on <see cref="ServerShipUpdate"/> +
/// wall-clock timestamps and is driven entirely by synthetic data in tests.
/// <see cref="Predict"/> should be called with non-decreasing timestamps.</para>
/// </summary>
public sealed class RemoteShipPredictor
{
    /// <summary>The blend is considered complete once k exceeds this, after
    /// which extrapolation resumes from the adopted authoritative anchor.</summary>
    private const double CompleteThreshold = 0.995;

    private readonly double _interval;

    /// <summary>Expected interval between real updates, seconds. Derived from
    /// <c>ServerConfig.UpdatesPerSecond</c> (default 20 → 1/20 = 0.05 s) —
    /// passed in so tests can drive synthetic cadences without a magic number
    /// living in the sim class.</summary>
    public RemoteShipPredictor(double expectedIntervalSeconds)
    {
        if (expectedIntervalSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedIntervalSeconds), "interval must be > 0");
        _interval = expectedIntervalSeconds;
    }

    // Immutable display anchor (position/velocity/orientation valid at _anchorTime).
    private bool _hasAnchor;
    private Vector3 _anchorPos;
    private Vector3 _anchorVel;
    private Orientation _anchorOrient = new();
    private double _anchorTime;

    // Active correction blend (valid while _blending).
    private bool _blending;
    private double _blendStart;
    private Vector3 _blendFromPos;
    private Vector3 _blendFromVel;
    private Vector3 _blendToPos;
    private Vector3 _blendToVel;
    private Orientation _blendFromOrient = new();
    private Orientation _blendToOrient = new();

    /// <summary>
    /// Feed a newly-arrived real update observed at <paramref name="timestamp"/>.
    /// If this is the first update the anchor is seeded from it; otherwise a
    /// correction blend toward the new authoritative state is begun from the
    /// current extrapolation (see class doc). Deterministic regardless of when
    /// <see cref="Predict"/> has been called.
    /// </summary>
    public void Observe(ServerShipUpdate update, double timestamp)
    {
        if (!_hasAnchor)
        {
            _anchorPos = update.Position;
            _anchorVel = update.Velocity;
            _anchorOrient = update.Orientation.Copy();
            _anchorTime = timestamp;
            _hasAnchor = true;
            _blending = false;
            return;
        }

        // Where our extrapolation would place the ship at this instant.
        double dtCont = timestamp - _anchorTime;
        var contPos = _anchorPos + _anchorVel * (float)dtCont;

        _blending = true;
        _blendStart = timestamp;
        _blendFromPos = contPos;
        _blendFromVel = _anchorVel;
        _blendFromOrient = _anchorOrient.Copy();
        _blendToPos = update.Position;
        _blendToVel = update.Velocity;
        _blendToOrient = update.Orientation.Copy();

        // The anchor adopts the continuity position/velocity at this time, so
        // that once the blend converges extrapolation resumes seamlessly.
        _anchorPos = contPos;
        _anchorTime = timestamp;
    }

    /// <summary>Compute the smoothed pose to render at <paramref name="timestamp"/>.
    /// Null until the first real update has been observed.</summary>
    public PredictedPose? Predict(double timestamp)
    {
        if (!_hasAnchor) return null;

        Vector3 pos;
        Vector3 vel;
        Orientation orient;

        if (_blending)
        {
            double age = Math.Max(0.0, timestamp - _blendStart);
            double k = 1.0 - Math.Exp(-age / _interval);
            if (k >= CompleteThreshold)
            {
                // Blend complete: adopt the authoritative state and resume plain
                // extrapolation from the observation time.
                k = 1.0;
                _blending = false;
                _anchorPos = _blendToPos;
                _anchorVel = _blendToVel;
                _anchorOrient = _blendToOrient.Copy();
                _anchorTime = _blendStart;
            }
            pos = VecLerp(_blendFromPos, _blendToPos, k);
            vel = VecLerp(_blendFromVel, _blendToVel, k);
            orient = OrientLerp(_blendFromOrient, _blendToOrient, k);
            // Extrapolate onward from the blend's start instant.
            double dtAfter = Math.Max(0.0, timestamp - _blendStart);
            pos += vel * (float)dtAfter;
        }
        else
        {
            double dt = Math.Max(0.0, timestamp - _anchorTime);
            pos = _anchorPos + _anchorVel * (float)dt;
            vel = _anchorVel;
            orient = _anchorOrient;
        }

        return new PredictedPose(pos, orient.Copy());
    }

    private static Vector3 VecLerp(in Vector3 a, in Vector3 b, double t)
    {
        float k = (float)t;
        return new Vector3(
            a.X + (b.X - a.X) * k,
            a.Y + (b.Y - a.Y) * k,
            a.Z + (b.Z - a.Z) * k);
    }

    private static Orientation OrientLerp(in Orientation from, in Orientation to, double t)
    {
        var result = new Orientation();
        var f = VecLerp(from.GetForward(), to.GetForward(), t);
        var u = VecLerp(from.GetUp(), to.GetUp(), t);
        result.Set(f, u);
        return result;
    }
}
