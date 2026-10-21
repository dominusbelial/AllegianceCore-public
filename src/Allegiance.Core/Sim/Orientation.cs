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

    /// <summary>
    /// P636 — assign the full 3×3 basis directly from the nine row components
    /// (right, up, backward rows). Used to rebuild an orientation bit-for-bit from a
    /// <see cref="State.DecisionShip"/> snapshot copy so the aiming/steer laws see
    /// exactly the basis that was on the wire — unlike <see cref="Set(Vector3,Vector3)"/>
    /// (which re-derives and re-normalises the rows). Only the single snapshot
    /// publish path calls this; it performs no normalisation on purpose.
    /// </summary>
    public void SetBasis(float r00, float r01, float r02, float r10, float r11, float r12, float r20, float r21, float r22)
    {
        this.r00 = r00; this.r01 = r01; this.r02 = r02;
        this.r10 = r10; this.r11 = r11; this.r12 = r12;
        this.r20 = r20; this.r21 = r21; this.r22 = r22;
    }

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

    /// <summary>
    /// P569 — column-wise non-uniform scale of the basis: each matrix column
    /// <c>c</c> is scaled by <c>xyz[c]</c> — <c>Orientation::Scale(xyz)</c>
    /// (<c>orientation.h:174-179</c>). Used by the shield-flare cap placement to
    /// squash the unit cap onto the shield ellipsoid
    /// (<c>orient.Scale(ellipse / m_radius)</c>, thinggeo.cpp:1272).
    /// </summary>
    public void Scale(Vector3 xyz)
    {
        r00 *= xyz.X; r01 *= xyz.Y; r02 *= xyz.Z;
        r10 *= xyz.X; r11 *= xyz.Y; r12 *= xyz.Z;
        r20 *= xyz.X; r21 *= xyz.Y; r22 *= xyz.Z;
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

    /// <summary>
    /// Post-rotate about an arbitrary world axis — the port of
    /// <c>Orientation::PostRotate(axis, theta)</c> (<c>zlib/orientation.cpp:595-603</c>),
    /// which builds <c>Orientation o(axis, theta)</c> from
    /// <c>Matrix::SetRotate</c> (<c>zlib/matrix.cpp:408-452</c>, standard Rodrigues
    /// form) and post-multiplies (<c>this = this · o</c>). With the row-vector
    /// transform convention (<c>world = local · M</c>) a post-multiplication
    /// rotates each basis row of this matrix — the world image of each local
    /// axis — about the axis by <c>theta</c>, which is exactly Rodrigues'
    /// <c>v· = v·cosθ + (a×v)·sinθ + a(a·v)(1−cosθ)</c> applied per row.
    /// Rows are rotated from the pre-rotation values and the basis is
    /// re-orthonormalized afterwards (<c>CmodelIGC::Update</c> calls
    /// <c>Renormalize()</c> right after <c>PostRotate</c>,
    /// <c>Igc/modelIGC.cpp:95-97</c>). A zero-length axis or a zero angle is a
    /// no-op (native skips the whole block when <c>m_rotation.angle() == 0</c>,
    /// modelIGC.cpp:87; <c>Matrix::SetRotate</c> normalizes the axis itself).
    /// Allocation-free: everything is stack arithmetic on the nine cells.
    /// </summary>
    public void PostRotate(Vector3 axis, float theta)
    {
        float length = axis.Length();
        if (length == 0f || theta == 0f) return;
        float x = axis.X / length, y = axis.Y / length, z = axis.Z / length;
        float c = MathF.Cos(theta);
        float s = MathF.Sin(theta);
        float t = 1f - c;

        // Rodrigues terms shared by the three rows.
        float xx = x * x * t, yy = y * y * t, zz = z * z * t;
        float xy = x * y * t, xz = x * z * t, yz = y * z * t;
        float sx = s * x, sy = s * y, sz = s * z;

        float n00 = (xx + c) * r00 + (xy - sz) * r01 + (xz + sy) * r02;
        float n01 = (xy + sz) * r00 + (yy + c) * r01 + (yz - sx) * r02;
        float n02 = (xz - sy) * r00 + (yz + sx) * r01 + (zz + c) * r02;
        float n10 = (xx + c) * r10 + (xy - sz) * r11 + (xz + sy) * r12;
        float n11 = (xy + sz) * r10 + (yy + c) * r11 + (yz - sx) * r12;
        float n12 = (xz - sy) * r10 + (yz + sx) * r11 + (zz + c) * r12;
        float n20 = (xx + c) * r20 + (xy - sz) * r21 + (xz + sy) * r22;
        float n21 = (xy + sz) * r20 + (yy + c) * r21 + (yz - sx) * r22;
        float n22 = (xz - sy) * r20 + (yz + sx) * r21 + (zz + c) * r22;

        r00 = n00; r01 = n01; r02 = n02;
        r10 = n10; r11 = n11; r12 = n12;
        r20 = n20; r21 = n21; r22 = n22;
        Renormalize();
    }

    // orientation.cpp:392-415 cosX / cosX2: cosine (or signed squared cosine)
    // between a unit axis and an arbitrary-length vector; zero vector -> 0.
    private static float CosX(Vector3 axis, Vector3 xyz)
    {
        float dot = axis * xyz;
        float l2 = xyz.LengthSquared();
        return l2 != 0f ? dot / MathF.Sqrt(l2) : 0f;
    }

    private static float CosX2(Vector3 axis, Vector3 xyz)
    {
        float dot = axis * xyz;
        float l2 = xyz.LengthSquared();
        if (l2 == 0f) return 0f;
        return dot >= 0f ? dot * dot / l2 : -dot * dot / l2;
    }

    /// <summary><c>Orientation::CosUp</c> (orientation.cpp:416).</summary>
    public float CosUp(Vector3 v) => CosX(GetUp(), v);
    /// <summary><c>Orientation::CosRight</c> (orientation.cpp:421).</summary>
    public float CosRight(Vector3 v) => CosX(GetRight(), v);
    /// <summary><c>Orientation::CosUp2</c> (orientation.cpp:431).</summary>
    public float CosUp2(Vector3 v) => CosX2(GetUp(), v);
    /// <summary><c>Orientation::CosForward2</c> (orientation.cpp:441) — note the
    /// negation of the backward row.</summary>
    public float CosForward2(Vector3 v) => -CosX2(GetBackward(), v);

    /// <summary>
    /// MSL-1 — <c>Orientation::TurnTo(const Vector&amp; target)</c>
    /// (orientation.cpp:220-231): snap forward onto <paramref name="target"/>,
    /// keeping the current up unless the target is (anti-)parallel to it.
    /// </summary>
    public void TurnTo(Vector3 target)
    {
        float dot = CosUp2(target);
        Vector3 up = dot < -0.99f ? GetForward()
                   : dot > 0.99f ? -GetForward()
                   : GetUp();
        Set(target, up);
    }

    /// <summary>
    /// MSL-1 — <c>Orientation::TurnTo(const Vector&amp; target, float maxTurn)</c>
    /// (orientation.cpp:233-304): turn the forward axis toward
    /// <paramref name="target"/> (any length) by at most <paramref name="maxTurn"/>
    /// radians and return the remaining angle. Three native regimes:
    /// <list type="bullet">
    /// <item>target more than ~11.5° off the nose (cos² &lt; 0.96): if within
    /// <paramref name="maxTurn"/> snap (<see cref="TurnTo(Vector3)"/>); else if almost
    /// directly behind (dcos &lt; -0.999, :258-265) a plain <see cref="Yaw"/> by
    /// maxTurn — the cross-product axis is undefined there; otherwise
    /// <see cref="PostRotate"/> about <c>-(backward × target)</c> (:272-278);</item>
    /// <item>target near the nose: yaw/pitch from the right/up cosines, scaled
    /// to maxTurn (:283-300).</item>
    /// </list>
    /// Always ends with <see cref="Renormalize"/> (:303), so the basis stays
    /// orthonormal and <see cref="GetForward"/> stays unit length.
    /// </summary>
    public float TurnTo(Vector3 target, float maxTurn)
    {
        float remaining;
        float cosTurn2 = CosForward2(target);
        if (cosTurn2 < 0.96f)
        {
            double dcos = cosTurn2 < 0f ? -Math.Sqrt(-cosTurn2) : Math.Sqrt(cosTurn2);
            float angle = dcos <= -1.0 ? MathF.PI
                        : dcos >= 1.0 ? 0f
                        : (float)Math.Acos(dcos);
            if (angle <= maxTurn)
            {
                TurnTo(target);
                return 0f;
            }
            remaining = angle - maxTurn;
            if (dcos < -0.999)
                Yaw(maxTurn);
            else
                PostRotate(-Vector3.CrossProduct(GetBackward(), target), maxTurn);
        }
        else
        {
            float yaw = MathF.Acos(CosRight(target)) - 0.5f * MathF.PI;
            float pitch = 0.5f * MathF.PI - MathF.Acos(CosUp(target));
            float angle = MathF.Sqrt(yaw * yaw + pitch * pitch);
            if (angle <= maxTurn)
            {
                TurnTo(target);
                return 0f;
            }
            remaining = angle - maxTurn;
            float f = maxTurn / angle;
            Yaw(yaw * f);
            Pitch(pitch * f);
        }
        Renormalize();
        return remaining;
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
