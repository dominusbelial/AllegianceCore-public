namespace Allegiance.Core.Rendering;

using Allegiance.Core.Sim;

/// <summary>
/// P589 — deterministic per-sector placement of the non-native space objects
/// that sit on the black sky as "paintings": moons (0-2, near the planet),
/// distant galaxies / small nebulae cut-outs (2-4), the deep-field galaxy
/// sprites (hundreds, log-distributed sizes) and the procedural Little Red
/// Dots. Everything is seeded from the cluster's <c>ClusterId</c> so a given
/// sector is stable across frames, launches and clients (Domingo: "same rules"
/// as the planets — object only, no star specks, cut out with feathered edges,
/// placed like a painting at a fixed direction on the sky shell).
///
/// All geometry is expressed as a <b>unit direction</b> (drawn at infinity,
/// no parallax, exactly like the planet poster, <c>efimage.cpp:515-563</c>)
/// plus an <b>angular half-size in degrees</b> — the renderers convert that to
/// world half-extent at their sky-shell distance
/// (<c>tan(halfDeg)·dist</c>), so the objects keep a fixed angular size
/// independent of FOV, mirroring the planet poster's
/// <c>PlanetAngularScale</c>. The only coupling to the planet is the moon's
/// angular size, which is a fraction of the planet's own half-angle (moons are
/// "smaller, a sensible angular offset" near the planet).
///
/// <b>Determinism:</b> a single SplitMix64 generator per seed (no dependence on
/// <c>System.Random</c>, whose algorithm is not contractual), so placement is
/// byte-identical for a given cluster id and reproducible in the headless
/// test-suite without any graphics device.
/// </summary>
public static class SpaceObjectMath
{
    // ------------------------------------------------------------------
    // Deterministic RNG (SplitMix64 — portable, not tied to System.Random).
    // ------------------------------------------------------------------
    public struct Rng
    {
        private ulong _state;

        public Rng(ulong seed) { _state = seed; }

        public ulong Next()
        {
            _state += 0x9E3779B97F4A7C15UL;
            ulong z = _state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        /// <summary>Uniform double in [0,1).</summary>
        public double NextDouble() => (Next() >> 11) * (1.0 / 9007199254740992.0);

        /// <summary>Uniform float in [min,max).</summary>
        public float Range(float min, float max) => (float)(min + NextDouble() * (max - min));

        /// <summary>Uniform int in [0, maxExclusive).</summary>
        public int Int(int maxExclusive) => maxExclusive <= 0 ? 0 : (int)(Next() % (uint)maxExclusive);
    }

    private static ulong Mix(short clusterId, ulong salt)
    {
        // Fold the cluster id and a per-feature salt into a well-mixed seed.
        ulong x = ((ulong)(ushort)clusterId << 48) ^ salt ^ 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }

    /// <summary>A uniformly random unit direction on the sphere.</summary>
    private static NativeSkyMath.Vector3Unit RandomDirection(ref Rng rng)
    {
        double theta = rng.NextDouble() * 2.0 * Math.PI;
        double cosPhi = 2.0 * rng.NextDouble() - 1.0;
        double sinPhi = Math.Sqrt(1.0 - cosPhi * cosPhi);
        return new NativeSkyMath.Vector3Unit(
            (float)(sinPhi * Math.Cos(theta)),
            (float)(sinPhi * Math.Sin(theta)),
            (float)cosPhi);
    }

    /// <summary>Rodrigues rotation of <paramref name="v"/> by <paramref name="angleRad"/>
    /// around a unit axis <paramref name="axis"/>.</summary>
    private static NativeSkyMath.Vector3Unit RotateAround(NativeSkyMath.Vector3Unit v, NativeSkyMath.Vector3Unit axis, float angleRad)
    {
        float c = MathF.Cos(angleRad), s = MathF.Sin(angleRad);
        float cx = axis.X, cy = axis.Y, cz = axis.Z;
        // cross(axis, v)
        float cxx = cy * v.Z - cz * v.Y;
        float cxy = cz * v.X - cx * v.Z;
        float cxz = cx * v.Y - cy * v.X;
        float dot = cx * v.X + cy * v.Y + cz * v.Z;
        return new NativeSkyMath.Vector3Unit(
            v.X * c + cxx * s + cx * dot * (1 - c),
            v.Y * c + cxy * s + cy * dot * (1 - c),
            v.Z * c + cxz * s + cz * dot * (1 - c));
    }

    private static NativeSkyMath.Vector3Unit Normalize(NativeSkyMath.Vector3Unit v)
    {
        float len = MathF.Sqrt(v.X * v.X + v.Y * v.Y + v.Z * v.Z);
        if (len < 1e-6f) return new NativeSkyMath.Vector3Unit(0, 0, 1);
        return new NativeSkyMath.Vector3Unit(v.X / len, v.Y / len, v.Z / len);
    }

    // ------------------------------------------------------------------
    // Angular helpers
    // ------------------------------------------------------------------

    /// <summary>Angular half-size (degrees) to a world half-extent at shell
    /// distance <paramref name="dist"/> — <c>tan(halfDeg)·dist</c>, the same
    /// relation the planet poster uses (<c>PlanetPoster.PlanetAngularScale</c>,
    /// <c>efimage.cpp:515-563</c>).</summary>
    public static float HalfSizeFromHalfDegrees(float halfDeg, float dist)
        => MathF.Tan(MathHelperDeg2Rad(halfDeg)) * dist;

    /// <summary>Framework-neutral degrees→radians (no XNA in Core).</summary>
    public static float MathHelperDeg2Rad(float deg) => deg * MathF.PI / 180f;

    // ------------------------------------------------------------------
    // Moons
    // ------------------------------------------------------------------

    /// <summary>Number of moons for a cluster: 0-2, seeded by cluster id.</summary>
    public static int MoonCount(short clusterId)
    {
        var rng = new Rng(Mix(clusterId, 0x4D4F4F4E4D4F4F4EUL)); // "MOONMOON"
        return rng.Int(3); // 0, 1 or 2
    }

    /// <summary>A single moon: a direction near the planet, an angular
    /// half-size (degrees) as a fraction of the planet's own half-angle, and a
    /// texture index into the native moon set (moonbmp / moon7bmp /
    /// moonnebbmp). Also exposes the illumination fraction (day-side) so the
    /// renderer can tint the moon consistently with the sector light.</summary>
    public readonly record struct MoonPlacement(
        NativeSkyMath.Vector3Unit Direction,
        float AngularHalfDeg,
        int TextureIndex,
        float Illumination);

    /// <param name="planetDir">The cluster's planet unit direction.</param>
    /// <param name="planetHalfDeg">The planet's angular half-size in degrees
    /// (native <c>planetRadius</c> → half-angle).</param>
    /// <param name="textureCount">Number of available moon textures (&gt;0).</param>
    public static MoonPlacement PlaceMoon(short clusterId, int moonIndex, NativeSkyMath.Vector3Unit planetDir, float planetHalfDeg, int textureCount)
    {
        // Per-(cluster,moon) seed so two moons of one sector differ.
        var rng = new Rng(Mix(clusterId, 0xC0FFEE0000000000UL ^ ((ulong)(moonIndex + 1) * 0x9E3779B97F4A7C15UL)));

        // Offset the planet direction by a small deterministic angle around a
        // random axis perpendicular to it: 5..25 degrees.
        float offsetDeg = rng.Range(5f, 25f);
        var perp = Normalize(new NativeSkyMath.Vector3Unit(
            planetDir.Y - planetDir.Z, planetDir.Z - planetDir.X, planetDir.X - planetDir.Y));
        var dir = RotateAround(planetDir, perp, MathHelperDeg2Rad(offsetDeg));

        // Angular size: a fraction of the planet's half-angle (0.25..0.6).
        float frac = rng.Range(0.25f, 0.6f);
        float halfDeg = Math.Max(0.1f, planetHalfDeg * frac);

        // Texture index 0..textureCount-1.
        int tex = rng.Int(textureCount);

        // Illumination from the sector light: computed by the caller (needs
        // the light direction). Returned placeholder 1 until then.
        return new MoonPlacement(dir, halfDeg, tex, 1f);
    }

    /// <summary>Day-side illumination factor for a moon given the unit
    /// direction toward the sector's light: 0 (dark)..1 (fully lit), eased so a
    /// half moon is not pitch black. Lambert against the billboard normal, but
    /// clamped to [ambient,1] so the far side never vanishes.</summary>
    public static float MoonIllumination(NativeSkyMath.Vector3Unit moonDir, NativeSkyMath.Vector3Unit towardLight, float ambient = 0.25f)
    {
        float d = moonDir.X * towardLight.X + moonDir.Y * towardLight.Y + moonDir.Z * towardLight.Z;
        // clamp to [-1,1], map [-1,1]->[ambient,1]
        d = Math.Clamp(d, -1f, 1f);
        return ambient + (1f - ambient) * (0.5f + 0.5f * d);
    }

    // ------------------------------------------------------------------
    // Distant galaxies / small nebulae cut-outs
    // ------------------------------------------------------------------

    /// <summary>Number of distant objects for a cluster: 2..min(4, available).</summary>
    public static int DistantCount(short clusterId, int available)
    {
        if (available <= 0) return 0;
        var rng = new Rng(Mix(clusterId, 0xD1574E544D4F4749UL)); // "DISTNTOBJ"
        int n = 2 + rng.Int(3); // 2,3,4
        return Math.Min(n, available);
    }

    /// <summary>A single distant object: a unit direction, an angular half-size
    /// (degrees), the sprite index into the distant-object library, and a
    /// brightness in [0,1] (dim so it never competes with ships/HUD).</summary>
    public readonly record struct ObjectPlacement(
        NativeSkyMath.Vector3Unit Direction,
        float AngularHalfDeg,
        int SpriteIndex,
        float Brightness);

    public static ObjectPlacement PlaceDistant(short clusterId, int objIndex, int available)
    {
        var rng = new Rng(Mix(clusterId, 0xD1574E544F424A45UL ^ ((ulong)(objIndex + 1) * 0x9E3779B97F4A7C15UL)));
        var dir = RandomDirection(ref rng);
        // Planet-like size: half-angle ~1.0..3.5 degrees (comparable to the
        // native planet's 2.5-5°, but a touch smaller so it reads secondary).
        float halfDeg = rng.Range(1.0f, 3.5f);
        int sprite = rng.Int(available);
        // Dim: 0.35..0.85 so it reads but never outshines the ship/HUD.
        float bright = rng.Range(0.35f, 0.85f);
        return new ObjectPlacement(dir, halfDeg, sprite, bright);
    }

    // ------------------------------------------------------------------
    // Deep-field galaxy field (hundreds, batched)
    // ------------------------------------------------------------------

    /// <summary>An instance of the deep-field galaxy field: a unit direction, an
    /// angular half-size (degrees, log-distributed from a couple of pixels up to
    /// a small disc well below planet size), a random roll, the sprite index and
    /// a dim brightness.</summary>
    public readonly record struct GalaxyInstance(
        NativeSkyMath.Vector3Unit Direction,
        float AngularHalfDeg,
        float RotationRad,
        int SpriteIndex,
        float Brightness);

    /// <param name="count">Number of galaxies to place (hundreds).</param>
    /// <param name="spriteCount">Library size (&gt;0).</param>
    public static GalaxyInstance[] PlaceGalaxies(short clusterId, int count, int spriteCount)
    {
        if (count <= 0 || spriteCount <= 0) return [];
        var rng = new Rng(Mix(clusterId, 0x47414C584659454CUL)); // "GALXYFIEL"
        var arr = new GalaxyInstance[count];
        // Log-distributed half-angle between ~0.018° (≈2 px at 2560) and ~1.1°
        // (a small disc, well below the planet's 2.5-5°).
        const float lo = 0.016f, hi = 1.2f;
        double lnLo = Math.Log(lo), lnHi = Math.Log(hi);
        for (int i = 0; i < count; i++)
        {
            double t = rng.NextDouble();
            float halfDeg = (float)Math.Exp(lnLo + t * (lnHi - lnLo));
            var dir = RandomDirection(ref rng);
            float rot = rng.Range(0f, MathF.PI * 2f);
            int sprite = rng.Int(spriteCount);
            // Dimmer than distant objects: 0.18..0.5.
            float bright = rng.Range(0.18f, 0.5f);
            arr[i] = new GalaxyInstance(dir, halfDeg, rot, sprite, bright);
        }
        return arr;
    }

    // ------------------------------------------------------------------
    // Little Red Dots (JWST early-universe objects, procedural)
    // ------------------------------------------------------------------

    /// <summary>A Little Red Dot: a tiny (≈1-4 px at 2560x1440) very red,
    /// compact object with a bright core and faint halo. Expressed as a unit
    /// direction, an angular half-size (degrees), a hue (0=deep red …
    /// 1=orange-red) and a brightness.</summary>
    public readonly record struct LittleRedDot(
        NativeSkyMath.Vector3Unit Direction,
        float AngularHalfDeg,
        float Hue,       // 0..1 mapped into red->orange-red
        float Brightness);

    /// <param name="count">A few dozen.</param>
    public static LittleRedDot[] PlaceLittleRedDots(short clusterId, int count)
    {
        if (count <= 0) return [];
        var rng = new Rng(Mix(clusterId, 0x524544444F545353UL)); // "REDDOTSS"
        var arr = new LittleRedDot[count];
        // ≈1-4 px at 2560x1440 → half-angle ~0.007°..0.035° (nominal 2560-wide
        // FOV; the renderer draws at a fixed shell so the object stays tiny).
        const float lo = 0.0065f, hi = 0.034f;
        for (int i = 0; i < count; i++)
        {
            double t = rng.NextDouble();
            float halfDeg = (float)Math.Exp(Math.Log(lo) + t * (Math.Log(hi) - Math.Log(lo)));
            var dir = RandomDirection(ref rng);
            // Hue: deep red (0.985, i.e. ~hue 354°) to orange-red (0.045, ~16°).
            // Stored as a normalized 0..1 in the red band.
            float hue = rng.Range(0.975f, 1.042f);
            float bright = rng.Range(0.55f, 0.95f);
            arr[i] = new LittleRedDot(dir, halfDeg, hue, bright);
        }
        return arr;
    }
}
