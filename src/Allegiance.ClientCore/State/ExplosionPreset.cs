using System.Linq;

namespace Allegiance.ClientCore.State;

/// <summary>
/// One sprite "film" that backs an explosion category — the real artwork strip
/// stem (<c>expNN</c>) and how many square animation frames that strip decodes
/// to. Mirrors native <c>AnimatedImage(Number*, Surface*, nRows=0, nCols=0)</c>
/// (<c>engine/image.cpp:588-625</c>): with <c>nRows=0</c> the strip is sliced
/// into <c>width/height</c> square frames, and each frame animates at 20&nbsp;fps
/// in <c>ExplosionGeo::RenderExplosions</c> (<c>effect/explosion.cpp:180-202</c>,
/// <c>frame = (int)(time * 20.0f)</c>).
/// </summary>
/// <param name="Stem">The artwork strip stem, e.g. <c>exp25</c> → file
/// <c>exp25bmp.mdl</c>.</param>
/// <param name="FrameCount">Square frames the strip decodes to
/// (<c>width / height</c>). This must equal what the runtime decoder measures
/// from the real <c>.mdl</c> (a MonoGame test asserts the agreement).</param>
public sealed record ExplosionSpriteFilm(string Stem, int FrameCount);

/// <summary>
/// The complete visual preset for one native explosion category — the fixed
/// numbers of the <c>WinTrekClient::AddExplosion</c> switch
/// (<c>WinTrek/trekigc.cpp:95-177</c>) plus the sprite films that category
/// cycles through (<c>trekigc.cpp:2530-2560</c>). Pure data — no MonoGame or
/// wire types — so both the headless <see cref="ExplosionSampler"/> and the
/// MonoGame renderer can share it.
/// </summary>
/// <param name="CountDecals">How many fireball sprites the blast spawns
/// (<c>count</c> in the native switch).</param>
/// <param name="RadiusExplosion">Fireball decal radius, world units
/// (<c>radiusExplosion</c>).</param>
/// <param name="RadiusShockWave">Peak shock-wave radius, world units
/// (<c>radiusWave</c>); 0 = no shock wave.</param>
/// <param name="ShockR">Shock-wave tint red (0..1), from the native
/// <c>Color(r/255, g/255, b/255)</c>.</param>
/// <param name="Films">The sprite films cycled across the decals
/// (<c>m_vpimageExplosion[type]</c>).</param>
public sealed record ExplosionPreset(
    int CountDecals,
    float RadiusExplosion,
    float RadiusShockWave,
    float ShockR,
    float ShockG,
    float ShockB,
    ExplosionSpriteFilm[] Films)
{
    /// <summary>True when the category has a shock wave (native passes a
    /// non-null <c>pimageShockwave</c>).</summary>
    public bool HasShockWave => RadiusShockWave > 0f;

    /// <summary>
    /// Total visual lifetime in seconds, exactly as native ages the sprites:
    /// decal <c>i</c> starts at <c>i * 0.25</c>s
    /// (<c>effect/explosion.cpp:113</c>) and runs through the frames of the
    /// film <c>films[i % films.Length]</c> at 20&nbsp;fps; the shock wave, when
    /// present, fades quadratically from t=0 to t=1
    /// (<c>effect/explosion.cpp:151-157</c>, <c>bright = 1 − t²</c>). The total
    /// is the latest sprite end (the shock wave, running in parallel from t=0,
    /// is always ≤ this or ≤ 1s).
    /// </summary>
    public float TotalDurationSeconds
    {
        get
        {
            float maxEnd = 0f;
            for (int i = 0; i < CountDecals; i++)
            {
                var film = Films[i % Films.Length];
                float start = i * 0.25f;
                float end = start + film.FrameCount / 20f;
                if (end > maxEnd) maxEnd = end;
            }
            if (HasShockWave && 1f > maxEnd) maxEnd = 1f;
            return maxEnd;
        }
    }
}

/// <summary>
/// The seven native explosion presets keyed by <see cref="Allegiance.Core.Sim.ExplosionType"/>,
/// byte-for-byte the <c>AddExplosion</c> switch table (<c>trekigc.cpp:95-177</c>)
/// combined with the per-type sprite-film assignments (<c>trekigc.cpp:2530-2560</c>).
/// See each preset for its native category.
/// </summary>
public static class ExplosionPresets
{
    private static readonly ExplosionSpriteFilm F16 = new("exp24", 16);
    private static readonly ExplosionSpriteFilm F20 = new("exp20", 20);
    private static readonly ExplosionSpriteFilm F22 = new("exp22", 30);
    private static readonly ExplosionSpriteFilm F23 = new("exp23", 38);
    private static readonly ExplosionSpriteFilm F25 = new("exp25", 32);

    public static ExplosionPreset For(int type) => type switch
    {
        // c_etMine (1): count 4, radiusExplosion 25, radiusWave 50, tint
        // (154,130,190)/255 — trekigc.cpp:107-114.
        Allegiance.Core.Sim.ExplosionType.Mine => new(4, 25f, 50f, 154f / 255f, 130f / 255f, 190f / 255f, [F16]),
        // c_etMissile (2): count 6, radius 1 (the "ignore the scale passed in"
        // hack), no shock wave — trekigc.cpp:115-119.
        Allegiance.Core.Sim.ExplosionType.Missile => new(6, 1f, 0f, 0f, 0f, 0f, [F20]),
        // c_etSmallShip (3): count 12, radius 35, wave 70, tint (200,130,50) — :121-128.
        Allegiance.Core.Sim.ExplosionType.SmallShip => new(12, 35f, 70f, 200f / 255f, 130f / 255f, 50f / 255f, [F25, F22]),
        // c_etBigShip (4): count 16, radius 165, wave 250, tint (200,130,50) — :130-137.
        Allegiance.Core.Sim.ExplosionType.BigShip => new(16, 165f, 250f, 200f / 255f, 130f / 255f, 50f / 255f, [F25, F22]),
        // c_etSmallStation (5): count 16, radius 100, wave 800, tint (220,130,50) — :139-146.
        Allegiance.Core.Sim.ExplosionType.SmallStation => new(16, 100f, 800f, 220f / 255f, 130f / 255f, 50f / 255f, [F25, F23]),
        // c_etAsteroid (6): count 5, radius 150, no shock wave — :148-151.
        Allegiance.Core.Sim.ExplosionType.Asteroid => new(5, 150f, 0f, 0f, 0f, 0f, [F16]),
        // c_etLargeStation (7): count 24, radius 350, wave 1000, tint (200,130,50) — :153-162.
        Allegiance.Core.Sim.ExplosionType.LargeStation => new(24, 350f, 1000f, 200f / 255f, 130f / 255f, 50f / 255f, [F25, F23]),
        _ => throw new ArgumentOutOfRangeException(nameof(type), $"unknown explosion type {type}"),
    };

    /// <summary>True when <paramref name="type"/> names one of the seven native
    /// categories (so a malformed wire type degrades to "no explosion" rather
    /// than throwing in the receive path).</summary>
    public static bool IsKnown(int type) =>
        type >= Allegiance.Core.Sim.ExplosionType.Mine &&
        type <= Allegiance.Core.Sim.ExplosionType.LargeStation;
}
