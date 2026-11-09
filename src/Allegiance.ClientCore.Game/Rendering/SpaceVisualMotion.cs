using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>One sky draw. Glimpses carry the same visual choices but opt out of
/// advancing history, so destination views cannot leak into flight rotation tails.</summary>
public readonly record struct SpaceVisualFrame(VisualOptions? Options, float DeltaSeconds, bool Cut, long Sequence = 0, bool AdvanceHistory = true)
{
    public bool SimStars => Options?.IsOn(VisualFlags.SimStarsKey) == true;
    public bool SimGlare => Options?.IsOn(VisualFlags.SimGlareKey) == true;
    public bool Streaks => Options?.IsOn(VisualFlags.StarStreaksKey) == true;
}

/// <summary>Shared reset rule for dust and star histories, including camera
/// mode, sector, sky tier, teleports and discontinuous time/view changes.</summary>
public sealed class SpaceVisualMotion
{
    private long _sequence;
    private bool _ready;
    private double _seconds;
    private long _identity;
    private Matrix _view;
    private Vector3 _eye;

    public SpaceVisualFrame Step(VisualOptions options, Matrix view, Vector3 eye, double seconds, long identity)
    {
        float dt = (float)(seconds - _seconds);
        bool cut = !_ready || identity != _identity || dt < 0 || dt > 0.25f
            || Vector3.DistanceSquared(eye, _eye) > DebrisGeo.JumpDistance * DebrisGeo.JumpDistance
            || Vector3.Dot(view.Forward, _view.Forward) < 0.7071068f
            || Vector3.Dot(view.Up, _view.Up) < 0.7071068f;
        _ready = true; _seconds = seconds; _identity = identity; _view = view; _eye = eye;
        return new SpaceVisualFrame(options, Math.Clamp(dt, 0, 0.25f), cut, ++_sequence);
    }
}

/// <summary>Direction-only star geometry and rate-normalized rotation tails.
/// No scene/HUD pixels are ever sampled or blurred.</summary>
public static class SimStarMath
{
    public const int DefaultCount = 500;
    public const int BlackTierCount = 4200;
    public const float ExposureSeconds = 1f / 60f;
    public const float MaxTailPixelsAt540 = 8f;

    /// <summary>Native StarImage expands the sector count fourfold
    /// (effect/starimage.cpp:34); the sim layer then applies its sparse cap.</summary>
    public static int SectorCount(short authoredCount) => Math.Clamp(authoredCount * 4, 0, DefaultCount);

    public static VertexStar[] Generate(uint seed, int count)
    {
        var random = new Random(unchecked((int)seed));
        var stars = new VertexStar[Math.Clamp(count, 0, DefaultCount)];
        for (int i = 0; i < stars.Length; i++)
        {
            float y = (float)random.NextDouble() * 2f - 1f;
            float theta = (float)random.NextDouble() * MathHelper.TwoPi;
            float ring = MathF.Sqrt(MathF.Max(0, 1f - y * y));
            byte brightness = (byte)random.Next(24, 161);
            stars[i] = new VertexStar(new Vector3(ring * MathF.Cos(theta), y, ring * MathF.Sin(theta)),
                new Color(brightness, brightness, brightness));
        }
        return stars;
    }

    public static Matrix RotationOnly(Matrix view)
    {
        view.Translation = Vector3.Zero;
        return view;
    }

    public static bool Project(Vector3 direction, Matrix view, Matrix projection, Vector2 viewport, out Vector2 pixel)
    {
        var clip = Vector4.Transform(new Vector4(direction, 0), RotationOnly(view) * projection);
        pixel = default;
        if (clip.W <= 0.00001f) return false;
        pixel = new Vector2((clip.X / clip.W + 1f) * viewport.X * 0.5f,
            (1f - clip.Y / clip.W) * viewport.Y * 0.5f);
        return float.IsFinite(pixel.X) && float.IsFinite(pixel.Y);
    }

    /// <summary>Reconstruct a fixed exposure's previous direction using the
    /// measured angular rate. Quaternion power removes projection nonlinearity
    /// from the 30/60/144 Hz comparison (including roll).</summary>
    public static Matrix ExposureView(Matrix previous, Matrix current, float dt)
    {
        var before = Quaternion.CreateFromRotationMatrix(RotationOnly(previous));
        var now = Quaternion.CreateFromRotationMatrix(RotationOnly(current));
        var delta = Quaternion.Normalize(Quaternion.Inverse(now) * before);
        if (delta.W < 0) delta = new Quaternion(-delta.X, -delta.Y, -delta.Z, -delta.W);
        var axis = new Vector3(delta.X, delta.Y, delta.Z);
        float angle = 2f * MathF.Atan2(axis.Length(), delta.W);
        if (axis.LengthSquared() < 1e-12f || dt <= 0) return RotationOnly(current);
        axis.Normalize();
        var exposed = Quaternion.CreateFromAxisAngle(axis, angle * ExposureSeconds / dt);
        return Matrix.CreateFromQuaternion(Quaternion.Normalize(now * exposed));
    }

    public static Vector2 Tail(Vector3 direction, Matrix current, Matrix exposure, Matrix projection, Vector2 viewport)
    {
        if (!Project(direction, current, projection, viewport, out var head)
            || !Project(direction, exposure, projection, viewport, out var previous)) return Vector2.Zero;
        var tail = previous - head;
        float length = tail.Length(), cap = MaxTailPixelsAt540 * viewport.Y / 540f;
        return length < 0.05f ? Vector2.Zero : tail * MathF.Min(1, cap / length);
    }
}

public readonly record struct VertexStar(Vector3 Direction, Color Color);
