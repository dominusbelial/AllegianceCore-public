using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>P982 opt-in shell, independently designed from the motion-debris idea.
/// Native jump threshold: effect/debrisgeo.cpp:98. Native levels:
/// WinTrek/WinTrek.cpp:4633-4653. DebrisGeo itself is deliberately untouched.</summary>
public sealed class SubtleDust(int seed = 982)
{
    public const float InnerRadius = 15f;
    public const float OuterRadius = 80f;
    public const float FullBrightnessRadius = 40f;
    private readonly Random _random = new(seed);
    private readonly List<SubtleDustGrain> _grains = new();
    private Vector3 _lastEye;
    private bool _ready;
    public IReadOnlyList<SubtleDustGrain> Grains => _grains;
    public int Refills { get; private set; }

    /// <summary>Existing density is metres per grain; lower means more. The
    /// budget remains bounded for arbitrary positive --debris values.</summary>
    public static int Budget(float density) => !float.IsFinite(density) || density <= 0 ? 0
        : Math.Clamp((int)MathF.Round(240f / MathF.Max(density, 0.8f)), 1, 300);

    public static float DistanceFade(float distance) => distance < InnerRadius ? 0f
        : Math.Clamp((OuterRadius - distance) / (OuterRadius - FullBrightnessRadius), 0f, 1f);

    public void Reset() => _ready = false;

    public void Step(Vector3 eye, float density, bool cameraCut = false)
    {
        int count = Budget(density);
        bool refill = !_ready || cameraCut || Vector3.DistanceSquared(eye, _lastEye) > DebrisGeo.JumpDistance * DebrisGeo.JumpDistance;
        if (count == 0) { _grains.Clear(); _ready = false; return; }
        if (refill || count != _grains.Count)
        {
            _grains.Clear();
            for (int i = 0; i < count; i++)
            {
                // Uniform volume in the hollow sphere, never within the inner radius.
                float radius = MathF.Cbrt(MathHelper.Lerp(InnerRadius * InnerRadius * InnerRadius,
                    OuterRadius * OuterRadius * OuterRadius, Next()));
                _grains.Add(Create(eye, radius));
            }
            Refills++;
        }
        else if (eye != _lastEye)
        {
            for (int i = 0; i < count; i++)
            {
                float distance = Vector3.Distance(_grains[i].Position, eye);
                if (distance < InnerRadius || distance > OuterRadius)
                    _grains[i] = Create(eye, OuterRadius);
            }
        }
        _lastEye = eye;
        _ready = true;
    }

    private float Next() => (float)_random.NextDouble();
    private SubtleDustGrain Create(Vector3 eye, float radius)
    {
        float z = Next() * 2f - 1f, angle = Next() * MathHelper.TwoPi;
        float ring = MathF.Sqrt(MathF.Max(0, 1f - z * z));
        var direction = new Vector3(ring * MathF.Cos(angle), ring * MathF.Sin(angle), z);
        return new SubtleDustGrain(eye + direction * radius, MathHelper.Lerp(0.025f, 0.045f, Next()),
            MathHelper.Lerp(0.16f, 0.28f, Next()));
    }
}

public readonly record struct SubtleDustGrain(Vector3 Position, float HalfExtent, float Brightness);
