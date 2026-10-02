using System;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P292 — the pure, headless-testable animation math of a native chaff decoy
/// (<c>src/Igc/chaffigc.cpp</c>). A deployed chaff grows its rendered radius
/// from a <c>1.0</c> floor up to its type's radius ceiling over its lifespan,
/// then expires. Native <c>CchaffIGC::Update</c> computes
/// <c>r = radius · (1 − dtLeft/lifespan)</c> floored at <c>1.0</c>
/// (<c>chaffigc.cpp:82-83</c>) where <c>dtLeft = timeExpire − now</c>; expressed
/// in the client's local-age terms that is
/// <c>max(1, radiusCeiling · age/lifespan)</c>. The coasting position is on the
/// entity itself (<see cref="Allegiance.ClientCore.State.ChaffEntity.PositionAt"/>);
/// this class owns only the size curve (and the native spin rate, which the
/// renderer applies as a screen-plane roll about the view axis).
/// </summary>
public static class ChaffAnimation
{
    /// <summary>Native chaff spin rate, radians/second — the model's
    /// <c>Rotation(Vector(0,0,1), 2.75f)</c> (<c>chaffigc.cpp:55</c>).</summary>
    public const float SpinRadiansPerSecond = 2.75f;

    /// <summary>The minimum rendered radius, units (native floors the growth at
    /// <c>1.0</c>, <c>chaffigc.cpp:50,83</c>).</summary>
    public const float MinimumRadius = 1.0f;

    /// <summary>
    /// The chaff's current rendered radius after <paramref name="ageSec"/>
    /// seconds of a lifespan of <paramref name="lifespan"/> seconds growing
    /// toward <paramref name="radiusCeiling"/> — faithful to native's
    /// <c>max(1, radius·(1 − dtLeft/lifespan))</c> (<c>chaffigc.cpp:82-83</c>).
    /// A non-positive age yields the floor; a negative lifespan (degenerate
    /// data) is guarded to return the ceiling's growth only when meaningful.
    /// </summary>
    public static float CurrentRadius(float ageSec, float lifespan, float radiusCeiling)
    {
        if (ageSec <= 0f || lifespan <= 0f) return MinimumRadius;
        float r = radiusCeiling * (ageSec / lifespan);
        return r > MinimumRadius ? r : MinimumRadius;
    }

    /// <summary>The chaff's screen-plane roll angle (radians) after
    /// <paramref name="ageSec"/> seconds — its native spin about the view axis
    /// (<c>SpinRadiansPerSecond</c>, <c>chaffigc.cpp:55</c>).</summary>
    public static float RollAngle(float ageSec) => ageSec * SpinRadiansPerSecond;
}
