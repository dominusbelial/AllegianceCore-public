using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Per-asteroid spin state for the P252 client-side rotation. This is the
/// client-side mirror of how native <c>CasteroidIGC</c> consumes the wire's
/// rotation each tick — <c>CmodelIGC::Update</c>
/// (<c>src/Igc/modelIGC.cpp:85-99</c>) does
/// <c>o.PostRotate(m_rotation.axis(), m_rotation.angle() * dt)</c>, i.e. it
/// rotates the model about its (body-local) rotation axis by
/// <c>angle × dt</c>, where <c>DataAsteroidIGC.Rotation</c> is the 4-float
/// <c>axis(x,y,z) + angle</c> and the angle is an angular <b>rate</b> in
/// radians/second (native <c>Time::operator-</c> divides by <c>fResolution()=
/// 1000</c>, so <c>dt</c> is in seconds).
/// </summary>
/// <remarks>
/// One <see cref="AsteroidSpin"/> holds the accumulated rotation angle for a
/// <em>single</em> asteroid so each rock spins about its own axis at its own
/// real wire rate — the real <c>m2xspiderw.igc</c> data gives all 134 rocks
/// distinct rates (measured 0.002–0.196 rad/s, none zero), so they must NOT
/// spin in lockstep. <see cref="Game1"/> keys these by <c>AsteroidId</c>.
/// </remarks>
public sealed class AsteroidSpin
{
    /// <summary>The wire axis, normalized to unit length (native's
    /// <c>Orientation(axis, θ)</c> treats it as a unit axis; real map axes come
    /// in at magnitude ~1.0–1.6).</summary>
    public Vector3 Axis { get; }

    /// <summary>Angular spin rate in radians per second — the wire's real
    /// <c>angle</c> component of <c>DataAsteroidIGC.Rotation</c>.</summary>
    public float Rate { get; }

    /// <summary>Accumulated rotation about <see cref="Axis"/> in radians.
    /// Advanced once per frame by <see cref="Advance"/>.</summary>
    public float Angle { get; private set; }

    private const float Epsilon = 1e-6f;

    /// <summary>Fallback axis used only when the wire axis is degenerate
    /// (near-zero magnitude, which would make an axis-angle rotation
    /// undefined). Not reached by real <c>m2xspiderw</c> data (measured minimum
    /// axis magnitude ~1.005) — defensive only, and documented as such.</summary>
    private static readonly Vector3 DefaultAxis = new(0f, 1f, 0f);

    public AsteroidSpin(Vector3 axis, float rate)
    {
        var a = axis.Normalize();
        if (a.LengthSquared() < Epsilon)
            a = DefaultAxis;
        Axis = a;
        Rate = rate;
    }

    /// <summary>Build from the wire's raw <c>float[4]</c>: <c>[axisX, axisY,
    /// axisZ, angle]</c>. A malformed array degrades to a stationary rock (rate
    /// 0) rather than throwing — never expected from real map data.</summary>
    public static AsteroidSpin FromWire(float[]? rotation)
    {
        if (rotation is { Length: >= 4 })
            return new AsteroidSpin(
                new Vector3(rotation[0], rotation[1], rotation[2]),
                rotation[3]);
        return new AsteroidSpin(DefaultAxis, 0f);
    }

    /// <summary>Accumulate <paramref name="deltaSeconds"/> of spin about
    /// <see cref="Axis"/> — the <c>angle × dt</c> term of native's per-tick
    /// <c>PostRotate</c>. <paramref name="deltaSeconds"/> is real wall-clock
    /// elapsed time (the WarpRenderer precedent), matching how a client animates
    /// at its own frame rate.</summary>
    public void Advance(float deltaSeconds) => Angle += Rate * deltaSeconds;
}
