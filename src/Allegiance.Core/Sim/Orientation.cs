using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

/// <summary>
/// The ship's attitude — a faithful port of the C++ <c>class Orientation</c>
/// (<c>src/zlib/orientation.h</c>, <c>src/zlib/orientation.cpp</c>). Stores a
/// 3×3 matrix <c>m_r</c> whose <b>rows</b> are right, up, backward:
/// row 0 = right, row 1 = up, row 2 = backward (−forward). Ported as a class
/// with in-place mutators because the C++ mutates the orientation in place via
/// <c>Yaw/Pitch/Roll</c> and <c>Renormalize</c>; that shape is mirrored so the
/// per-step call sequence reads identically to the reference. Vectors are the
/// repo's single <see cref="Vector3"/> (Collision) as elsewhere in the sim.
/// </summary>
/// <remarks>
/// <para><b>Handedness (evidence):</b> <b>left-handed</b> when <c>forward</c> is
/// taken as the ship's nose. The stored rows are a proper orthonormal rotation
/// (determinant +1), so the triplet {right, up, backward} is right-handed
/// among itself — <c>right × up = backward</c> — but the ship's forward is the
/// <em>negation</em> of row 2, so {right, up, forward} has determinant −1. Two
/// concrete proofs: (a) the identity orientation has forward = <c>(0,0,-1)</c>,
/// not <c>(0,0,1)</c>; (b) <c>Set</c> computes <c>right = forward × up</c>
/// (<c>orientation.cpp:364</c>), which for identity yields <c>right =
/// (0,0,-1)×(0,1,0) = (1,0,0)</c> and then <c>right × up = (0,0,1) =
/// backward = −forward</c>. In a right-handed frame <c>right × up</c> would
/// equal <c>forward</c>; it does not. So a positive yaw (<c>Yaw(+θ)</c>) is a
/// left-handed rotation about +up, swinging the nose from −z toward −x.</para>
/// <para><b>Row/column multiplication order:</b> the stored matrix rows are
/// the local axes expressed in world coords (right, up, backward). Transforming
/// a local vector to world is <c>v * Orientation</c> (<c>orientation.h:190</c>,
/// = v × rows = world), and world→local is <c>Orientation.TimesInverse</c>
/// (<c>orientation.cpp:173</c>, = columns = v·mᵀ). Flight uses both: the
/// engine's local thrust is rotated into world with <c>*</c>, and the world
/// drag / desired velocity is brought into ship-local with
/// <c>TimesInverse</c>. Reproduced exactly.</para>
/// </remarks>
public sealed class Orientation
{
    // m_r, rows: [0]=right, [1]=up, [2]=backward. Fields named r<i><j>.
    private float r00, r01, r02;
    private float r10, r11, r12;
    private float r20, r21, r22;

    public Orientation() => Reset();

    public Orientation(Orientation other)
    {
        r00 = other.r00; r01 = other.r01; r02 = other.r02;
        r10 = other.r10; r11 = other.r11; r12 = other.r12;
        r20 = other.r20; r21 = other.r21; r22 = other.r22;
    }

    /// <summary>Deep copy, since mutations are in place.</summary>
    public Orientation Copy() => new(this);

    /// <summary>Identity — <c>Orientation::GetIdentity()</c>.</summary>
    public static Orientation Identity => new();

    /// <summary>—<c>m_r[2]</c> (<c>orientation.h:83</c>).</summary>
    public Vector3 GetForward() => new(-r20, -r21, -r22);

    /// <summary><c>m_r[2]</c> (<c>orientation.h:88</c>) — the velocity/thrust forward reference is actually backward.</summary>
    public Vector3 GetBackward() => new(r20, r21, r22);

    /// <summary><c>m_r[1]</c> (<c>orientation.h:94</c>).</summary>
    public Vector3 GetUp() => new(r10, r11, r12);

    /// <summary><c>m_r[0]</c> (<c>orientation.h:100</c>).</summary>
    public Vector3 GetRight() => new(r00, r01, r02);

    /// <summary>
    /// P298 — the signed roll angle in <b>degrees</b> about the forward axis,
    /// relative to the ship's level attitude. The level reference is world-up
    /// (+Y; identity forward is <c>(0,0,-1)</c>, up <c>(0,1,0)</c>) projected
    /// into the plane perpendicular to the forward axis — the up the ship would
    /// have at zero roll for its current pitch/yaw. The roll is the signed angle
    /// between that reference and the actual up, measured about forward via the
    /// right vector: <c>atan2(right·levelUp, up·levelUp)</c>. Verified to recover
    /// exactly the <c>Roll(+θ)</c> argument (<c>orientation.cpp:562</c>) applied
    /// to a level ship. Degenerate when the forward axis is (anti-)parallel to
    /// world-up (vertical climb) — roll is undefined, so it reports level (0°).
    /// </summary>
    public float GetRollDegrees()
    {
        Vector3 f = GetForward();
        Vector3 up = GetUp();
        Vector3 right = GetRight();

        var worldUp = new Vector3(0f, 1f, 0f);
        float wuf = worldUp.X * f.X + worldUp.Y * f.Y + worldUp.Z * f.Z;
        var proj = new Vector3(worldUp.X - wuf * f.X, worldUp.Y - wuf * f.Y, worldUp.Z - wuf * f.Z);
        float plen = proj.Length();
        if (plen < 1e-4f)
            return 0f;   // forward (anti-)parallel to world-up — roll undefined
        proj = proj / plen;

        float cosr = up.X * proj.X + up.Y * proj.Y + up.Z * proj.Z;
        float sinr = right.X * proj.X + right.Y * proj.Y + right.Z * proj.Z;
        return MathF.Atan2(sinr, cosr) * 180f / MathF.PI;
    }

    public void SetForward(Vector3 v) { r20 = -v.X; r21 = -v.Y; r22 = -v.Z; }
    public void SetUp(Vector3 v) { r10 = v.X; r11 = v.Y; r12 = v.Z; }
    public void SetRight(Vector3 v) { r00 = v.X; r01 = v.Y; r02 = v.Z; }

    /// <summary><c>Orientation::Reset()</c> — identity matrix (<c>orientation.cpp:307</c>).</summary>
    public void Reset()
    {
        r01 = r02 = r10 = r12 = r20 = r21 = 0f;
        r00 = r11 = r22 = 1f;
    }

    /// <summary>
    /// Build an orthonormal matrix from a forward and up axis —
    /// <c>Orientation::Set(forwardAxis, upAxis)</c> (<c>orientation.cpp:358-390</c>).
    /// right = forward × up, then up′ = right × forward, all normalized. This is
    /// the routine <c>Renormalize()</c> delegates to, so it both constructs and
    /// repairs.
    /// </summary>
    public void Set(Vector3 forwardAxis, Vector3 upAxis)
    {
        Vector3 rightAxis = Vector3.CrossProduct(forwardAxis, upAxis);   // forward × up = right in this left-handed frame (see class doc)

        float lRight = rightAxis.Length();
        float lForward = forwardAxis.Length();

        SetUp(Vector3.CrossProduct(rightAxis, forwardAxis));
        float lUp = GetUp().Length();

        if (lRight != 0f && lUp != 0f && lForward != 0f)
        {
            SetForward(forwardAxis / lForward);
            SetUp(GetUp() / lUp);
            SetRight(rightAxis / lRight);
        }
        else
        {
            Reset();
        }
    }

    /// <summary>Re-orthonormalize: forward and up are re-derived from the current (drifted) matrix (<c>orientation.h:156</c>).</summary>
    public void Renormalize() => Set(GetForward(), GetUp());

    /// <summary>
    /// Construct an orientation matrix from a single forward axis — the
    /// single-argument <c>Orientation::Set(const Vector&amp; forwardAxis)</c>
    /// (<c>orientation.cpp:314-356</c>) the eject branch uses
    /// (<c>FedSrv.CPP:11987,11995,11998</c>). Derives a perpendicular up axis
    /// from a fixed reference, falling back when the forward is near the
    /// x-axis, then delegates to <see cref="Set(Vector3,Vector3)"/>. Zero
    /// forward resets to identity (matching the C++ <c>Reset()</c>,
    /// orientation.cpp:350).
    /// </summary>
    public void Set(Vector3 forwardAxis)
    {
        float forwardLength2 = forwardAxis.LengthSquared();

        if (forwardLength2 != 0.0f)
        {
            // Reference axes from orientation.cpp:325-326.
            var xAxis = new Vector3(-1.0f, 0.0f, 0.0f);
            var yAxis = new Vector3(0.0f, -1.0f, 0.0f);

            Vector3 upAxis = Vector3.CrossProduct(xAxis, forwardAxis);
            if (upAxis.LengthSquared() < forwardLength2 * 0.01f)
                upAxis = Vector3.CrossProduct(yAxis, forwardAxis);

            Set(forwardAxis, upAxis);
        }
        else
        {
            Reset();
        }
    }

    /// <summary>World→local transform — <c>Orientation::TimesInverse</c> (<c>orientation.cpp:173</c>).</summary>
    public Vector3 TimesInverse(Vector3 v) =>
        new(
            v.X * r00 + v.Y * r01 + v.Z * r02,
            v.X * r10 + v.Y * r11 + v.Z * r12,
            v.X * r20 + v.Y * r21 + v.Z * r22);

    /// <summary>Local→world transform — <c>Vector * Orientation</c> (<c>orientation.h:190</c>).</summary>
    public Vector3 Times(Vector3 v) =>
        new(
            v.X * r00 + v.Y * r10 + v.Z * r20,
            v.X * r01 + v.Y * r11 + v.Z * r21,
            v.X * r02 + v.Y * r12 + v.Z * r22);

    /// <summary>Local→world; convenience for <c>local * this</c>.</summary>
    public static Vector3 operator *(Vector3 local, Orientation o) => o.Times(local);

    /// <summary><c>Orientation::Yaw(theta)</c> (<c>orientation.cpp:538</c>) — pre-multiplied left rotation about +y, exact terms.</summary>
    public void Yaw(float theta)
    {
        float c = MathF.Cos(theta);
        float s = MathF.Sin(theta);
        // Row 1 is unchanged. RHS fully evaluated before assignment, so the
        // six mutated cells all read the pre-rotation values (no aliasing).
        (r00, r01, r02, r20, r21, r22) = (
            c * r00 - s * r20, c * r01 - s * r21, c * r02 - s * r22,
            c * r20 + s * r00, c * r21 + s * r01, c * r22 + s * r02);
    }

    /// <summary><c>Orientation::Pitch(theta)</c> (<c>orientation.cpp:514</c>) — pre-multiplied rotation about +x.</summary>
    public void Pitch(float theta)
    {
        float c = MathF.Cos(theta);
        float s = MathF.Sin(theta);
        float n10 = c * r10 + s * r20, n11 = c * r11 + s * r21, n12 = c * r12 + s * r22;
        float n20 = c * r20 - s * r10, n21 = c * r21 - s * r11, n22 = c * r22 - s * r12;
        (r10, r11, r12) = (n10, n11, n12);
        (r20, r21, r22) = (n20, n21, n22);
    }

    /// <summary><c>Orientation::Roll(theta)</c> (<c>orientation.cpp:562</c>) — pre-multiplied rotation about +z.</summary>
    public void Roll(float theta)
    {
        float c = MathF.Cos(theta);
        float s = MathF.Sin(theta);
        float n00 = c * r00 + s * r10, n01 = c * r01 + s * r11, n02 = c * r02 + s * r12;
        float n10 = c * r10 - s * r00, n11 = c * r11 - s * r01, n12 = c * r12 - s * r02;
        (r00, r01, r02) = (n00, n01, n02);
        (r10, r11, r12) = (n10, n11, n12);
    }
}
