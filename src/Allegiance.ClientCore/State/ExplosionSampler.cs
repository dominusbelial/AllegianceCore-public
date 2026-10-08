using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// One live fireball decal of an explosion at a given instant: which sprite it
/// is, which film and frame it shows, and its deterministic world-space offset /
/// rotation. Mirrors native <c>ExplosionGeo::AddExplosion</c>'s per-decal setup
/// (<c>effect/explosion.cpp:104-117</c>): each decal gets a random position
/// offset within <c>±radiusExplosion·0.5</c> and a random rotation
/// <c>random(0, 2π)</c>. The offset/angle are deterministic per sprite index so
/// a given blast renders identically every frame (native rolls them once at
/// spawn; here they are hashed from the index for the same stability without
/// storing per-sprite state).
/// </summary>
public readonly record struct FireballSprite(
    int SpriteIndex,
    int FilmIndex,
    int FrameIndex,
    Vector3 Offset,
    float Angle);

/// <summary>The expanding shock-wave state at an instant: <c>Bright = 1 − t²</c>
/// (0 at t=1 → fades out) and <c>Scale = t · radiusWave</c> (grows from 0 to the
/// peak radius over its 1-second life) — native <c>RenderShockwaves</c>,
/// <c>effect/explosion.cpp:149-175</c>.</summary>
public readonly record struct ShockwaveState(float Bright, float Scale);

/// <summary>The full visual state of one explosion at one instant — the fireball
/// decals to draw and, if this category has one, the shock-wave ring. Produced
/// by <see cref="ExplosionSampler.Sample"/> (pure, headless-testable).</summary>
public readonly record struct ExplosionSample(
    IReadOnlyList<FireballSprite> Fireballs,
    ShockwaveState? Shockwave);

/// <summary>
/// Pure translation of elapsed-seconds → explosion visual state, a faithful
/// port of the timing in <c>ExplosionGeo</c> (<c>effect/explosion.cpp</c>).
/// Kept free of MonoGame/wire types so every frame and duration rule is
/// unit-testable headless.
/// </summary>
public static class ExplosionSampler
{
    /// <summary>Native sprite playback rate — <c>frame = (int)(time * 20.0f)</c>
    /// (<c>effect/explosion.cpp:183</c>).</summary>
    public const float FramesPerSecond = 20f;

    /// <summary>Per-decal spawn stagger — decal i starts at <c>i * 0.25</c>s
    /// (<c>effect/explosion.cpp:113</c>).</summary>
    public const float SpriteStagger = 0.25f;

    /// <summary>
    /// Compute the visual state of an explosion <paramref name="elapsed"/>
    /// seconds after it started. Sprites not yet begun (stagger) or past their
    /// film's last frame are omitted; the shock wave, when present, is included
    /// only while <c>bright = 1 − t² &gt; 0</c> (its 1s life).
    /// </summary>
    public static ExplosionSample Sample(ExplosionPreset preset, float elapsed)
    {
        if (elapsed < 0f) elapsed = 0f;

        var fireballs = new List<FireballSprite>();
        for (int i = 0; i < preset.CountDecals; i++)
        {
            float start = i * SpriteStagger;
            float local = elapsed - start;
            if (local < 0f) continue;                      // not begun yet

            int filmIndex = i % preset.Films.Length;
            var film = preset.Films[filmIndex];
            int frame = (int)(local * FramesPerSecond);
            if (frame >= film.FrameCount) continue;        // film finished

            fireballs.Add(new FireballSprite(
                i, filmIndex, frame,
                OffsetFor(i, preset.RadiusExplosion * 0.5f),
                AngleFor(i)));
        }

        ShockwaveState? shock = null;
        if (preset.HasShockWave)
        {
            float t = elapsed;
            float bright = 1f - t * t;
            if (bright > 0f)
                shock = new ShockwaveState(bright, t * preset.RadiusShockWave);
        }

        return new ExplosionSample(fireballs, shock);
    }

    /// <summary>Deterministic per-sprite world offset within ±radius on each axis
    /// (a hash-derived point in the sphere, matching native
    /// <c>Vector::RandomPosition(radiusExplosion·0.5)</c>). Public so the P573/P579
    /// volumetric builder places its sub-volumes at the SAME offsets as the
    /// sprite path (native 1:1 parity).</summary>
    public static Vector3 OffsetFor(int index, float radius)
    {
        uint s = Mix((uint)(index * 2654435761u + 0x9E3779B9u));
        float x = UnitRandom(ref s) * 2f - 1f;
        float y = UnitRandom(ref s) * 2f - 1f;
        float z = UnitRandom(ref s) * 2f - 1f;
        return new Vector3(x * radius, y * radius, z * radius);
    }

    /// <summary>Deterministic per-sprite rotation in [0, 2π)
    /// (native <c>random(0, 2*pi)</c>). Public for the P573/P579 volumetric
    /// builder (same angular placement as the sprite path).</summary>
    public static float AngleFor(int index)
    {
        uint s = Mix((uint)(index * 40503u + 17u));
        return UnitRandom(ref s) * MathF.Tau;
    }

    private static uint Mix(uint x)
    {
        x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15;
        x *= 0x846ca68bu; x ^= x >> 16;
        return x;
    }

    private static float UnitRandom(ref uint s)
    {
        s ^= s << 13; s ^= s >> 17; s ^= s << 5;   // xorshift
        return s / 4294967296.0f;
    }
}
