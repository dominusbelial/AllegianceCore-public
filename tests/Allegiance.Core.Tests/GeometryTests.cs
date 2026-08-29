using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// Verifies <see cref="Vector3"/> and <see cref="Orientation"/> against the
/// C++ <c>Vector</c>/<c>Orientation</c> (<c>src/zlib/vector.cpp</c>,
/// <c>src/zlib/orientation.cpp</c>): the arithmetic, the right-handedness, the
/// row layout (right/up/backward) and the local↔world transforms the flight
/// model relies on.
/// </summary>
public class GeometryTests
{
    // ── Vector3 ────────────────────────────────────────────────────────────

    [Fact]
    public void Vector_length_and_ops_match_the_reference()
    {
        var a = new Vector3(3f, -4f, 0f);
        Assert.Equal(25f, a.LengthSquared());
        Assert.Equal(5f, a.Length());

        var b = new Vector3(1f, 2f, 3f);
        Assert.Equal(new Vector3(4f, -2f, 3f), a + b);
        Assert.Equal(new Vector3(2f, -6f, -3f), a - b);
        Assert.Equal(new Vector3(3f, -4f, 0f) * 2f, a * 2f);
        Assert.Equal(new Vector3(3f, -4f, 0f) * 2f, 2f * a);
        Assert.Equal(3 * 1f + -4 * 2f + 0 * 3f, a * b);          // dot
        Assert.Equal(-3f, (-a).X);                              // unary minus
    }

    [Fact]
    public void Division_scales_by_reciprocal_exactly_like_cpp()
    {
        // C++ operator/ does `divisor = 1/s;  v * divisor` (vector.h:160),
        // NOT v/s — bit-identical division must reproduce that multiply form.
        var v = new Vector3(1f, 2f, 3f) / 0.0075f;
        var cppForm = new Vector3(1f, 2f, 3f) * (1f / 0.0075f);
        Assert.Equal(cppForm, v);
    }

    [Fact]
    public void Cross_product_is_right_handed()
    {
        var x = new Vector3(1f, 0f, 0f);
        var y = new Vector3(0f, 1f, 0f);
        Assert.Equal(new Vector3(0f, 0f, 1f), Vector3.CrossProduct(x, y)); // x × y = z
        Assert.Equal(new Vector3(0f, 0f, -1f), Vector3.CrossProduct(y, x)); // anti-commutes
    }

    // ── Orientation ────────────────────────────────────────────────────────

    [Fact]
    public void Identity_matrix_layout_is_right_up_backward()
    {
        var o = Orientation.Identity;
        Assert.Equal(new Vector3(1f, 0f, 0f), o.GetRight());   // row 0
        Assert.Equal(new Vector3(0f, 1f, 0f), o.GetUp());      // row 1
        Assert.Equal(new Vector3(0f, 0f, 1f), o.GetBackward());// row 2 = −forward
        Assert.Equal(new Vector3(0f, 0f, -1f), o.GetForward());// forward is −row 2
    }

    [Fact]
    public void Yaw_rotates_about_positive_up()
    {
        // Yaw(+θ) is a left rotation about +y (orientation.cpp:538). Starting
        // facing −z, the right/up/backward axes must show a right-handed yaw
        // (forward swings from −z toward −x as θ grows).
        var o = Orientation.Identity;
        o.Yaw(MathF.PI / 2f);
        var back = o.GetBackward();
        // Backward rotates: row 2 becomes (sinθ·1, 0, cosθ·1)? From the matrix
        // with identity start, Yaw(θ): r20=0→s·r00=s, r21=0, r22=1→c·1=c.
        // So backward = (sinθ, 0, cosθ). For θ=+π/2: (1,0,0) — the +x (right)
        // axis. Equivalently the nose (−backward = forward) swings from −z to −x.
        Assert.InRange(o.GetBackward().X, 0.999f, 1.001f);
        Assert.InRange(Math.Abs(o.GetBackward().Y), 0f, 1e-6f);
        Assert.InRange(o.GetBackward().Z, -0.002f, 0.002f);
    }

    [Fact]
    public void Rotation_matrices_stay_orthonormal_under_arbitrary_sequence()
    {
        // Many alternating rotations + Renormalize must keep the matrix an
        // orthonormal right-handed triad (det = +1, rows unit & mutually orthogonal).
        var o = Orientation.Identity;
        var rnd = new Random(12345);
        for (int i = 0; i < 1000; i++)
        {
            o.Yaw((float)(rnd.NextDouble() * 6.28 - 3.14));
            o.Pitch((float)(rnd.NextDouble() * 6.28 - 3.14));
            o.Roll((float)(rnd.NextDouble() * 6.28 - 3.14));
            o.Renormalize();
            AssertOrthonormal(o);
        }
    }

    [Fact]
    public void Times_inverse_round_trips_local_and_world()
    {
        // Arbitrary rotated orientation.
        var o = Orientation.Identity;
        o.Yaw(0.773f);
        o.Pitch(-0.912f);
        o.Roll(1.771f);
        o.Renormalize();

        var world = new Vector3(11f, -7f, 3f);
        var local = o.TimesInverse(world);
        var backToWorld = o.Times(local);
        // Floating-point associativity means this need not be bit-exact; a
        // tight tolerance proves the pair is an inverse transform.
        Assert.Equal(world.X, backToWorld.X, 4);
        Assert.Equal(world.Y, backToWorld.Y, 4);
        Assert.Equal(world.Z, backToWorld.Z, 4);
    }

    private static void AssertOrthonormal(Orientation o)
    {
        var r = o.GetRight();
        var u = o.GetUp();
        var b = o.GetBackward();
        var f = o.GetForward();

        Assert.Equal(1f, r.Length(), 5);
        Assert.Equal(1f, u.Length(), 5);
        Assert.Equal(1f, b.Length(), 5);

        // Orthogonal pairs.
        Assert.Equal(0f, r * u, 5);
        Assert.Equal(0f, u * b, 5);
        Assert.Equal(0f, b * r, 5);

        // Forward is the negation of the stored backward row (identity nose = −z).
        Assert.Equal(-b.X, f.X, 5);
        Assert.Equal(-b.Y, f.Y, 5);
        Assert.Equal(-b.Z, f.Z, 5);

        // Left-handed triad: right × up = backward (= −forward). Evidence:
        // identity forward is (0,0,-1) and right×up lands on row 2 (+backward).
        // Precision 4: the length of a cross product of near-unit rows carries
        // iteration drift (~1e-7); the DIRECTION is what proves handedness.
        var rCrossU = Vector3.CrossProduct(r, u);
        Assert.Equal(b.X, rCrossU.X, 4);
        Assert.Equal(b.Y, rCrossU.Y, 4);
        Assert.Equal(b.Z, rCrossU.Z, 4);
    }
}
