using System.Numerics;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// Geometric distance/heading conditions — the C# ports of <c>objectwithinradiuscondition</c>,
/// <c>objectmovingtowardscondition</c> and <c>objectpointingatcondition</c>.
/// </summary>

/// <summary>
/// TRUE when two objects are closer than a radius (accounting for their radii) — port of
/// <c>ObjectWithinRadiusCondition</c> (<c>objectwithinradiuscondition.cpp:22-114</c>). Faithful detail:
/// the radius threshold is initialised lazily on first evaluate — the target radius plus the object
/// radius are added to the requested radius, then squared (lines 104-113) — so the condition measures
/// edge-to-edge distance, not centre-to-centre.
/// </summary>
public sealed class ObjectWithinRadiusCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _object;
    private readonly TargetRef _target;
    private float _radiusSquared; // squared edge-to-edge threshold
    private bool _initialized;

    public ObjectWithinRadiusCondition(ITrainingSite site, TargetRef obj, TargetRef target, float radius)
    {
        _site = site;
        _object = obj;
        _target = target;
        _radiusSquared = radius;
        _initialized = false;
    }

    public override bool Evaluate()
    {
        if (!Initialized())
            return false;
        var o = _site.Resolve(_object);
        var t = _site.Resolve(_target);
        if (o is null || t is null)
            return false;
        float lengthSquared = Vector3.DistanceSquared(o.Position, t.Position);
        return lengthSquared <= _radiusSquared;
    }

    /// <summary><c>GetRadarRadius</c> (<c>objectwithinradiuscondition.cpp:88-96</c>).</summary>
    public float GetRadarRadius()
    {
        Initialized();
        if (_initialized)
            return MathF.Sqrt(_radiusSquared);
        var o = _site.Resolve(_object);
        return (o?.Radius ?? 0f) + _radiusSquared;
    }

    private bool Initialized()
    {
        if (!_initialized)
        {
            var o = _site.Resolve(_object);
            var t = _site.Resolve(_target);
            if (o is not null && t is not null)
            {
                // edge-to-edge: add both radii, then square
                _radiusSquared += o.Radius + t.Radius;
                _radiusSquared *= _radiusSquared;
                _initialized = true;
            }
        }
        return _initialized;
    }
}

/// <summary>
/// TRUE when the object is moving approximately towards the target — port of
/// <c>ObjectMovingTowardsCondition</c> (<c>objectmovingtowardscondition.cpp:35-66</c>). The object is
/// heading towards the target when the angle between its velocity vector and the look-at-target vector
/// is less than the angle subtended by 75% of the target's radius (<c>coverageRadius = radius * 0.75</c>,
/// lines 60-62). Requires speed above 0.01.
/// </summary>
public sealed class ObjectMovingTowardsCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _object;
    private readonly TargetRef _target;

    public ObjectMovingTowardsCondition(ITrainingSite site, TargetRef obj, TargetRef target)
    {
        _site = site;
        _object = obj;
        _target = target;
    }

    public override bool Evaluate()
    {
        var o = _site.Resolve(_object);
        var t = _site.Resolve(_target);
        if (o is null || t is null)
            return false;

        Vector3 forward = o.Velocity;
        float speed = forward.Length();
        if (speed > 0.01f)
        {
            Vector3 lookAtTarget = t.Position - o.Position;
            float distance = lookAtTarget.Length();
            lookAtTarget /= distance;
            forward /= speed;
            float cosLookAngle = Vector3.Dot(forward, lookAtTarget);
            if (cosLookAngle > 0.0f)
            {
                float lookAngle = MathF.Acos(Math.Clamp(cosLookAngle, -1f, 1f));
                float coverageRadius = t.Radius * 0.75f;
                float coverageAngle = MathF.Asin(Math.Clamp(coverageRadius / distance, -1f, 1f));
                return lookAngle < coverageAngle;
            }
        }
        return false;
    }
}

/// <summary>
/// TRUE when an object's forward vector points at a target — port of <c>ObjectPointingAtCondition</c>
/// (<c>objectpointingatcondition.cpp:56-100</c>). If <c>SetMinimumAngle</c> was called, compares the look
/// angle against that (converted from degrees); otherwise against the angle subtended by 75% of the
/// target's radius, floored at 3 degrees (lines 92-95). Supports an optional per-axis vector mask that
/// collapses axes to zero (lines 118-127) so the check can be done in a plane.
/// </summary>
public sealed class ObjectPointingAtCondition : Condition
{
    private readonly ITrainingSite _site;
    private readonly TargetRef _object;
    private readonly TargetRef _target;
    private Vector3 _vectorMask = new(Na, Na, Na);
    private float _minimumAngle = Na;

    private const float Na = -1e9f;

    public ObjectPointingAtCondition(ITrainingSite site, TargetRef obj, TargetRef target)
    {
        _site = site;
        _object = obj;
        _target = target;
    }

    public override bool Evaluate()
    {
        var o = _site.Resolve(_object);
        var t = _site.Resolve(_target);
        if (o is null || t is null)
            return false;

        Vector3 targetPosition = MaskedVector(t.Position);
        Vector3 objectPosition = MaskedVector(o.Position);
        Vector3 forward = Vector3.Normalize(MaskedVector(o.Forward));
        Vector3 lookAtTarget = targetPosition - objectPosition;
        float distance = lookAtTarget.Length();
        lookAtTarget /= distance;
        float cosLookAngle = Vector3.Dot(forward, lookAtTarget);
        if (cosLookAngle > 0.0f)
        {
            float lookAngle = MathF.Acos(Math.Clamp(cosLookAngle, -1f, 1f));
            if (_minimumAngle >= 0.0f)
            {
                float targetLookAngle = RadiansFromDegrees(_minimumAngle);
                return lookAngle < targetLookAngle;
            }
            else
            {
                float coverageRadius = t.Radius * 0.75f;
                float coverageAngle = MathF.Asin(Math.Clamp(coverageRadius / distance, -1f, 1f));
                float minimumCoverageAngle = RadiansFromDegrees(3.0f); // settled on 3 degrees
                float threshold = (coverageAngle < minimumCoverageAngle) ? minimumCoverageAngle : coverageAngle;
                return lookAngle < threshold;
            }
        }
        return false;
    }

    public void SetVectorMask(Vector3 mask) => _vectorMask = mask;

    public void ClearVectorMask() => _vectorMask = new Vector3(Na, Na, Na);

    public void SetMinimumAngle(float degrees) => _minimumAngle = degrees;

    private Vector3 MaskedVector(Vector3 v) => new(
        _vectorMask.X == Na ? v.X : _vectorMask.X,
        _vectorMask.Y == Na ? v.Y : _vectorMask.Y,
        _vectorMask.Z == Na ? v.Z : _vectorMask.Z);

    private static float RadiansFromDegrees(float deg) => deg * MathF.PI / 180f;
}
