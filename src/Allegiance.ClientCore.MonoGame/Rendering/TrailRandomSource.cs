using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>The live-client <see cref="ITrailRandom"/> implementation, backed by
/// <see cref="System.Random"/>: uniform floats for the ejection/effect-size
/// jitter and a uniformly-distributed unit vector for the start-position jitter.
/// Separated from the pool so the pool stays pure and tests can inject a
/// deterministic stub.</summary>
public sealed class RandomTrailSource : ITrailRandom
{
    private readonly System.Random _rng;

    public RandomTrailSource() => _rng = new System.Random();

    /// <param name="seed">Optional fixed seed for reproducible runs.</param>
    public RandomTrailSource(int seed) => _rng = new System.Random(seed);

    public float Range(float min, float max) =>
        min + (float)_rng.NextDouble() * (max - min);

    public Vector3 Direction()
    {
        // Uniform distribution on the unit sphere: pick z in [-1,1] and an
        // azimuth in [0, 2π); r = √(1 − z²) keeps the point on the sphere.
        float z = 2f * (float)_rng.NextDouble() - 1f;
        float a = 2f * MathF.PI * (float)_rng.NextDouble();
        float r = MathF.Sqrt(MathF.Max(0f, 1f - z * z));
        return new Vector3(r * MathF.Cos(a), r * MathF.Sin(a), z);
    }
}
