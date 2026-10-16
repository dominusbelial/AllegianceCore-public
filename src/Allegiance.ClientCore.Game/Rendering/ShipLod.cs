using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P628 — per-ship rendering level-of-detail tiers and the metric that selects
/// them. This module is <b>pure</b> (no GraphicsDevice) so every rule is
/// headless-testable; the GPU work lives in
/// <see cref="ShipLodSpriteRenderer"/>.
///
/// <para><b>The metric (native fidelity).</b> Native selects a ship's LOD from
/// the object's <b>projected on-screen pixel radius</b> — its bounding-sphere
/// radius projected through the camera to a pixel extent —
/// <c>Context::GetImageRadius(pos, radius)</c>
/// (<c>~/Development/Allegiance/src/engine/context.cpp:1858-1871</c>), which
/// <c>ThingGeoImpl::Render</c> feeds straight into <c>SetLOD(screenRadius *
/// lodBias)</c> (<c>effect/thinggeo.cpp:1618-1624</c>, bias default 1,
/// <c>thinggeo.cpp:1674</c>). Our
/// <see cref="ProjectedPixelRadius"/> reproduces that pixel-extent metric
/// analytically for a sphere (identical for a centred object; the native
/// routine is not exactly analytically invertible off-axis).</para>
///
/// <para><b>Tiers.</b> The port introduces a modern <b>far tier</b> below a
/// configurable pixel threshold where a ship becomes a cheap, lit, side-tinted
/// sprite (see scope). Above it the ship keeps its full model (native .mdl, or
/// Meshy fallback), and within that tier native's own <c>LODGeo</c> levels
/// (<see cref="NativeLodLevelFor"/>) reduce triangle count by screen size.</para>
///
/// <para><b>Classification.</b> Native's LOD-as-design is <b>intended
/// design</b> (level-of-detail culling is the feature); the fixed level
/// thresholds tuned for 2000s GPUs are an <b>era constraint</b> (fixed pixel
/// thresholds). The port keeps the intent (select detail by projected pixel
/// radius) but makes the far-tier threshold a tunable constant sized for
/// today's GPUs instead of silently copying a 2002 choice.
/// <see cref="ShipLodProfile"/> / <see cref="FromEnv"/>.</para>
/// </summary>
public enum ShipLodTier
{
    /// <summary>Full model (native .mdl with its own LODGeo levels, or the
    /// Meshy fallback), engine trails / motion ribbons / nozzle glow drawn.</summary>
    Full,
    /// <summary>Cheap lit side-tinted sprite; no trails or ribbons at this tier.</summary>
    Far,
}

/// <summary>P628 — the configurable LOD thresholds (per-class env overridable;
/// see <see cref="FromEnv"/>).</summary>
public sealed class ShipLodProfile
{
    /// <summary>Projected pixel radius (native GetImageRadius units) at or below
    /// which a currently-Full ship drops to the Far tier. Default 7 px.</summary>
    public float FarPixelRadius = 7f;

    /// <summary>Projected pixel radius at or above which a currently-Far ship
    /// rises back to Full — the hysteresis ceiling (must exceed
    /// <see cref="FarPixelRadius"/>). Default 12 px.</summary>
    public float EnterFullPixelRadius = 12f;

    /// <summary>Pixel width of the crossfade band below the switch, over which
    /// the far sprite fades in while the full model fades out (no popping).
    /// Default 5 px.</summary>
    public float FadeBandPixelRadius = 5f;

    /// <summary>
    /// Parse a profile from the environment (absent/unparseable → defaults).
    /// <c>ALLEGIANCE_LOD_FAR_PX</c>, <c>ALLEGIANCE_LOD_ENTER_PX</c>,
    /// <c>ALLEGIANCE_LOD_FADE_PX</c>. Clamps the hysteresis ceiling above the
    /// far threshold so the tier never degenerates.
    /// </summary>
    public static ShipLodProfile FromEnv()
    {
        var p = new ShipLodProfile();
        p.FarPixelRadius = Env("ALLEGIANCE_LOD_FAR_PX", p.FarPixelRadius);
        p.EnterFullPixelRadius = Env("ALLEGIANCE_LOD_ENTER_PX", p.EnterFullPixelRadius);
        p.FadeBandPixelRadius = Env("ALLEGIANCE_LOD_FADE_PX", p.FadeBandPixelRadius);
        if (p.EnterFullPixelRadius <= p.FarPixelRadius)
            p.EnterFullPixelRadius = p.FarPixelRadius + 1f;
        if (p.FadeBandPixelRadius < 0f) p.FadeBandPixelRadius = 0f;
        return p;
    }

    private static float Env(string name, float def)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return float.TryParse(v, out float f) ? f : def;
    }
}

/// <summary>P628 — pure tier-selection / projection / fade logic.</summary>
public static class ShipLod
{
    /// <summary>
    /// The projected on-screen pixel radius of a sphere of
    /// <paramref name="worldRadius"/> world units at the camera-relative centre
    /// <paramref name="eyeRel"/> (world minus camera eye — the P612b
    /// camera-relative convention), under the given view/projection, given the
    /// backbuffer height in pixels. Reproduces native
    /// <c>GetImageRadius(pos, radius)</c> (context.cpp:1858-1871) analytically:
    /// <c>pixels = radius · (vh/2) · (1/tan(fov/2)) / distance</c>, using
    /// <c>proj.M22 = 1/tan(fov/2)</c> (MonoGame's
    /// <c>CreatePerspectiveFieldOfView</c>). NaN/short-circuits yield a large
    /// value so an unprojectable ship never spuriously drops to Far.
    /// </summary>
    public static float ProjectedPixelRadius(
        Vector3 eyeRel, float worldRadius, Matrix view, Matrix projection, float viewportHeight)
    {
        if (worldRadius <= 0f || viewportHeight <= 0f)
            return float.MaxValue;

        var viewPos = Vector3.Transform(eyeRel, view);
        float dist = viewPos.Length();
        if (dist <= 1e-4f || float.IsNaN(dist))
            return float.MaxValue;

        float scale = (0.5f * viewportHeight) * projection.M22;   // px per world-unit-per-unit-distance
        float px = worldRadius * scale / dist;
        return float.IsFinite(px) ? Math.Max(px, 0f) : float.MaxValue;
    }

    /// <summary>
    /// The continuous crossfade weight toward the Far tier, in [0,1]:
    /// 0 = full model only (close), 1 = sprite only (far), interpolated
    /// smoothly over the band between <see cref="ShipLodProfile.FarPixelRadius"/>
    /// and <see cref="ShipLodProfile.EnterFullPixelRadius"/> so there is no pop.
    /// </summary>
    public static float FarAmount(float projectedPx, in ShipLodProfile p)
    {
        float lo = p.FarPixelRadius;
        float hi = p.EnterFullPixelRadius;
        if (hi <= lo) hi = lo + 0.001f;
        float t = Math.Clamp((projectedPx - lo) / (hi - lo), 0f, 1f);
        float s = t * t * (3f - 2f * t);   // smoothstep(lo, hi, px)
        return 1f - s;                     // far at low px, full at high px
    }

    /// <summary>
    /// The sprite's fade-in alpha (crossfade complement to the model fade),
    /// shaped by <see cref="FarAmount"/> and <see cref="ShipLodProfile.FadeBandPixelRadius"/>
    /// so the sprite reaches full opacity only <c>FadeBandPixelRadius</c> px below
    /// the far threshold. Returns 0 when <paramref name="farAmount"/> is 0.
    /// </summary>
    public static float SpriteAlpha(float farAmount, float projectedPx, in ShipLodProfile p)
    {
        if (farAmount <= 0f) return 0f;
        if (p.FadeBandPixelRadius <= 0f) return farAmount;
        float below = p.FarPixelRadius - projectedPx;   // >0 once past the switch
        float t = Math.Clamp(below / p.FadeBandPixelRadius, 0f, 1f);
        return farAmount * t;
    }

    /// <summary>
    /// The model-side fade alpha during the crossfade band: keeps the full
    /// model drawn (at reduced opacity) while the sprite fades in, so the two
    /// never pop. Full model at alpha 1 when <paramref name="farAmount"/> is 0.
    /// </summary>
    public static float ModelAlpha(float farAmount, in ShipLodProfile p)
        => 1f - farAmount;

    /// <summary>
    /// Hysteresis gate for the discrete tier (used to suppress trails/ribbons
    /// at the far tier and to stabilise the toggle). A Full ship stays Full
    /// until it shrinks at or below <see cref="ShipLodProfile.FarPixelRadius"/>;
    /// a Far ship stays Far until it grows at or above
    /// <see cref="ShipLodProfile.EnterFullPixelRadius"/>. Returns the new state.
    /// </summary>
    public static bool NextFarState(float projectedPx, bool currentlyFar, in ShipLodProfile p)
    {
        if (currentlyFar)
            return projectedPx < p.EnterFullPixelRadius;   // stay Far until big enough
        return projectedPx <= p.FarPixelRadius;            // become Far when small enough
    }

    /// <summary>
    /// P628 native LOD level selection — the port of native <c>LODGeoImpl::Render</c>
    /// (geometry.cpp:1760-1773) fed by <c>SetLOD(screenRadius*lodBias)</c>
    /// (thinggeo.cpp:1624). <paramref name="levelPixelRadii"/> are the baked
    /// LODGeo pixel thresholds, <b>descending</b> (finest→coarsest level
    /// ordering, as the loader emits them — the artist-authored
    /// <c>LODGeo(geo, [(pixels, geo)])</c> list). Returns the 1-based coarse
    /// level to use (0 = base/finest mesh), choosing the coarsest level whose
    /// threshold is still <c>&gt;=</c> the projected pixel radius, exactly like
    /// native breaks out of its threshold loop. If <paramref name="screenRadiusPx"/>
    /// is enormous (never far enough), 0 (base) is returned.
    /// </summary>
    public static int NativeLodLevelFor(float screenRadiusPx, IReadOnlyList<float> levelPixelRadii)
    {
        int level = 0;
        for (int i = 0; i < levelPixelRadii.Count; i++)
        {
            if (screenRadiusPx <= levelPixelRadii[i]) level = i + 1;
            else break;
        }
        return level;
    }
}
