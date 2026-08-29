namespace Allegiance.Core.Collision;

/// <summary>
/// A 3-vector, mirroring the game's <c>Vector</c> (<c>src/zlib/vector.h</c>).
/// Defined locally so the collider model does not depend on the fixed-byte
/// payload structs, and — since this is the repo's single vector type (README:
/// "Vector positions reuse the existing Collision.Vector3") — enriched with the
/// float arithmetic the flight model reads, faithful to <c>Vector::</c> the
/// component-wise ops. Division-by-scalar multiplies by the reciprocal exactly
/// as <c>Vector::operator/</c> does (<c>vector.h:158-162</c>). Kept a
/// <c>float</c> triple throughout, matching the C++.
/// </summary>
public readonly record struct Vector3(float X, float Y, float Z)
{
    /// <summary>The origin <c>(0,0,0)</c> — <c>Vector::GetZero()</c>.</summary>
    public static Vector3 Zero => new(0f, 0f, 0f);

    public float LengthSquared() => X * X + Y * Y + Z * Z;
    public float Length() => MathF.Sqrt(LengthSquared());

    /// <summary><c>Vector::Normalize()</c> (<c>vector.h:97</c>). Zero length yields the zero vector (the C++ raises a fatal error; we keep the harmless return since no flight path hits it).</summary>
    public Vector3 Normalize()
    {
        float l = LengthSquared();
        if (l == 1f) return this;
        if (l == 0f) return Zero;
        return this / MathF.Sqrt(l);
    }

    public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vector3 operator -(Vector3 a) => new(-a.X, -a.Y, -a.Z);
    public static Vector3 operator *(Vector3 a, float s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vector3 operator *(float s, Vector3 a) => a * s;

    /// <summary>Division by scalar via the reciprocal — <c>Vector::operator/</c> (<c>vector.h:158-162</c>).</summary>
    public static Vector3 operator /(Vector3 a, float s)
    {
        float divisor = 1f / s;
        return new Vector3(a.X * divisor, a.Y * divisor, a.Z * divisor);
    }

    /// <summary>Dot product — <c>Vector::operator*(Vector, Vector)</c> (<c>vector.h:164</c>).</summary>
    public static float operator *(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>Cross product (<c>vector.cpp:62</c>).</summary>
    public static Vector3 CrossProduct(Vector3 a, Vector3 b) =>
        new(
            a.Y * b.Z - b.Y * a.Z,
            a.Z * b.X - b.Z * a.X,
            a.X * b.Y - b.X * a.Y);
}
