using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Generates the procedural "rough rock" diffuse texture for asteroids — a
/// tileable, mottled gray/brown pattern built as a layered (fractal) value
/// noise, centred on the same gray-brown tone <see cref="AsteroidRenderer"/>
/// previously used as its flat <c>RockColor</c> diffuse ((0.52, 0.46, 0.40)).
/// Following the exact "generated, not loaded" convention already used by the
/// rock mesh itself (<see cref="RockMeshFactory"/>) and the starfield
/// (<see cref="Starfield"/>): there is deliberately no authored rock texture
/// in the project's artwork tree, so the diffuse is synthesized at runtime.
/// </summary>
/// <remarks>
/// Determinism is the contract, matching <see cref="RockMeshFactory"/>: every
/// lattice hash is pure integer arithmetic seeded from the fixed
/// <see cref="DefaultSeed"/>, with NO <c>System.Random</c> and NO time — so
/// two calls with the same arguments return byte-identical pixels (unit tests
/// assert this). The noise is also <b>tileable</b>: each octave's value-noise
/// lattice is periodic with the image size (corner lookups wrap via modulo on
/// an integer cell count), so the left/right and top/bottom edges match — the
/// whole rock is textured from one seamless sheet with no repeating-square
/// grid lines.
/// </remarks>
public static class RockTextureFactory
{
    /// <summary>Fixed seed so the texture is stable across runs (same
    /// run-to-run discipline as <see cref="RockMeshFactory.DefaultSeed"/>).</summary>
    public const int DefaultSeed = 517613;

    /// <summary>Default texture edge length in pixels (square). 256 is ample
    /// detail for a single shared rock diffuse while staying cheap to build.</summary>
    public const int DefaultSize = 256;

    // The rock's gray-brown tone this texture is centred on — the diffuse
    // <see cref="AsteroidRenderer"/> used flat before P253.
    private const float BaseR = 0.52f, BaseG = 0.46f, BaseB = 0.40f;

    // Gain applied to the raw [0,1) noise before shaping into color. The
    // multi-octave fbm concentrates tightly near 0.5 (averaging narrows the
    // distribution), so without a strong gain the mottle is almost invisible
    // (measured ~±8% luminance — barely distinguishable from the old flat
    // fill). A gain of 9 stretches it to a pronounced rough-rock swing
    // (~±32% brightness), clipped at t = ±1 so it never blows out.
    private const float ContrastGain = 9f;
    private const float BoostAmplitude = 0.326f;
    private const float WarmDrift = 0.054f, CoolDrift = 0.033f, BlueDrift = 0.066f;

    // Amplitude of an added per-pixel speckle (deterministic 2D hash) that
    // breaks up the value-noise's inherent smoothness so the diffuse carries
    // crisp rocky grain rather than reading as a soft cloudy wash.
    private const float GrainAmount = 0.021f;

    /// <summary>
    /// Build the raw RGBA pixel buffer for the rock diffuse (size × size,
    /// row-major, alpha 255). Pure — takes no <see cref="GraphicsDevice"/>, so
    /// it is unit-testable without a device and the caller uploads it to a
    /// <see cref="Texture2D"/> however it likes (see
    /// <see cref="AsteroidRenderer"/>).
    /// </summary>
    /// <param name="size">Square edge length in pixels (must be &gt; 0).</param>
    /// <param name="seed">Fixed seed for a stable pattern (defaults to
    /// <see cref="DefaultSeed"/>).</param>
    public static Color[] GeneratePixels(int size = DefaultSize, int seed = DefaultSeed)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "size must be > 0");

        var pixels = new Color[size * size];
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                // Seven value-noise octaves (frequencies 1..64 cells across the
                // image), each tileable, summed with decreasing weight → a
                // fractal fbm in [0, 1): broad grey-brown blotches from the low
                // octaves + finer rocky grain from the high ones.
                // Layer the smooth fbm mottle with a per-pixel speckle (both
                // deterministic) so the diffuse has real rocky grain, not just
                // soft cloud-like blotches.
                float n = Fbm(seed, px, py, size)
                    + (Hash2(seed + 509287, px, py) - 0.5f) * GrainAmount;
                n = Math.Clamp(n, 0f, 1f);

                // Centre and stretch the noise around the rock's base tone:
                // t = (n − 0.5)·gain ∈ [−1, 1] after clamping. boost scales
                // brightness ~±35% so the mottle visibly breaks up the old flat
                // fill (without the gain the fbm sits so close to its mean that
                // the pattern would read as a flat fill — see ContrastGain).
                float t = Math.Clamp((n - 0.5f) * ContrastGain, -1f, 1f);
                float boost = 1f + t * BoostAmplitude;

                // Gentle chromatic drift with brightness — darker pockets lean
                // cooler/grayer, brighter patches warmer/rustier — so the rock
                // reads as natural stone, not a flat gray wash.
                float r = BaseR * boost * (1f + t * WarmDrift);
                float g = BaseG * boost * (1f - t * CoolDrift);
                float b = BaseB * boost * (1f - t * BlueDrift);

                byte rb = (byte)Math.Clamp(MathF.Round(r * 255f), 0f, 255f);
                byte gb = (byte)Math.Clamp(MathF.Round(g * 255f), 0f, 255f);
                byte bb = (byte)Math.Clamp(MathF.Round(b * 255f), 0f, 255f);
                pixels[py * size + px] = new Color(rb, gb, bb, (byte)255);
            }
        }
        return pixels;
    }

    /// <summary>Fractal Brownian motion: weighted sum of tileable value-noise
    /// octaves, normalised to [0, 1). Octaves run f = 1,2,…,64 with weights
    /// decaying by ~0.731 each step — spanning broad grey-brown blotches down
    /// to fine mid-scale grain, so the diffuse carries detail at every size the
    /// low-poly rock mesh can plausibly resolve.</summary>
    private static float Fbm(int seed, int px, int py, int size)
    {
        float sum = 0f, ampSum = 0f, amp = 1f;
        for (int f = 1; f <= 64; f <<= 1)
        {
            sum += ValueNoise(seed, f, px, py, size) * amp;
            ampSum += amp;
            amp *= 0.731f;
        }
        return sum / ampSum;
    }

    /// <summary>Tileable value noise: a lattice of <c>cells</c>×<c>cells</c>
    /// random values spanning the image, smoothly interpolated. Corner lookups
    /// wrap (via <c>% cells</c>) so the pattern is periodic with the image —
    /// left/right and top/bottom edges match for seamless tiling.</summary>
    private static float ValueNoise(int seed, int cells, int px, int py, int size)
    {
        float nx = (float)px / size * cells;   // continuous lattice coordinate, [0, cells)
        float ny = (float)py / size * cells;
        int xi = (int)nx, yi = (int)ny;
        if (xi >= cells) xi = cells - 1;       // guard against float rounding at the last pixel
        if (yi >= cells) yi = cells - 1;
        float fx = nx - xi, fy = ny - yi;
        float sx = SmoothStep(fx), sy = SmoothStep(fy);

        float a = Hash2(seed, xi % cells, yi % cells);
        float b = Hash2(seed, (xi + 1) % cells, yi % cells);
        float c = Hash2(seed, xi % cells, (yi + 1) % cells);
        float d = Hash2(seed, (xi + 1) % cells, (yi + 1) % cells);

        float top = Lerp(a, b, sx);
        float bot = Lerp(c, d, sx);
        return Lerp(top, bot, sy);
    }

    private static float SmoothStep(float t) => t * t * (3f - 2f * t);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Deterministic integer hash of (seed, x, y) mapped to [0, 1) —
    /// pure integer arithmetic, no <c>System.Random</c>, no time (same
    /// discipline as <see cref="RockMeshFactory"/>'s <c>HashUnit</c>).</summary>
    private static float Hash2(int seed, int x, int y)
    {
        uint h = unchecked((uint)seed * 374761393u + (uint)x * 668265263u + (uint)y * 974634319u);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0x00FFFFFF) / 16777216f;   // 24 bits → [0, 1)
    }
}
