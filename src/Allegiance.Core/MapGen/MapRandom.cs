namespace Allegiance.Core.MapGen;

/// <summary>
/// P811 — the small RNG surface the procedural map generator draws on. It is
/// swappable so a <see cref="MapGenParams.Seed"/> yields a byte-identical
/// mission map (<see cref="SeededMapRandom"/>) while an unseeded run keeps
/// drawing from the ambient process RNG (<see cref="SharedMapRandom"/>),
/// matching native's model: the map maker seeds its RNG once per run from the
/// mission's random seed (<c>srand(iSeed)</c>, and every subsequent draw —
/// cluster decorations, asteroid placement, Brawl's scatter count — goes
/// through that one stream (<c>rand()</c>), so a fixed seed reproduces the
/// whole generated map.
/// </summary>
public interface IMapRandom
{
    /// <summary>A non-negative random int in [0, int.MaxValue) — <c>rand()</c>.</summary>
    int Next();

    /// <summary>A random int in <c>[min, max)</c> — the C# <see cref="System.Random.Next(int,int)"/>
    /// contract, used everywhere the port draws native <c>randomInt(lo, hi)</c>
    /// (which is inclusive; callers pass <c>hi+1</c> to mirror it).</summary>
    int Next(int min, int max);

    /// <summary>A random double in [0, 1) — <c>(double)rand()/RAND_MAX</c>.</summary>
    double NextDouble();
}

/// <summary>Draws from the ambient <see cref="Random.Shared"/>, i.e. the
/// generator's historic (non-reproducible) behaviour when no seed is set.</summary>
public sealed class SharedMapRandom : IMapRandom
{
    public static readonly SharedMapRandom Instance = new();

    private SharedMapRandom() { }

    public int Next() => Random.Shared.Next();
    public int Next(int min, int max) => Random.Shared.Next(min, max);
    public double NextDouble() => Random.Shared.NextDouble();
}

/// <summary>A <see cref="System.Random"/> fixed to one seed: the same seed
/// reproduces the identical stream of draws (and therefore the identical
/// generated map) on any run and any platform — the basis of
/// <c>--map-seed N</c> reproducibility.</summary>
public sealed class SeededMapRandom : IMapRandom
{
    private readonly System.Random _rng;

    public SeededMapRandom(int seed) => _rng = new System.Random(seed);

    public int Next() => _rng.Next();
    public int Next(int min, int max) => _rng.Next(min, max);
    public double NextDouble() => _rng.NextDouble();
}

/// <summary>Factory for the map generator's RNG: a set seed produces a
/// deterministic stream, an absent seed keeps the shared (non-reproducible)
/// stream.</summary>
public static class MapRandomFactory
{
    public static IMapRandom FromSeed(int? seed) =>
        seed is { } s ? new SeededMapRandom(s) : SharedMapRandom.Instance;
}
