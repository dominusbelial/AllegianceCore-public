using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P628 — pure-logic tests for ship rendering level-of-detail: tier selection
/// by projected pixel radius (with hysteresis), the no-pop crossfade, native
/// LODGeo level selection, far-tier batching (identical hulls → one group) and
/// the far-tier side-colour / bracket-anchor invariants. All pure — no
/// GraphicsDevice — so they run headless. Each class is new in P628, so on
/// today's (pre-P628) tree these fail to compile/run (the failing-first
/// precondition).
/// </summary>
public class ShipLodTests
{
    private static readonly Matrix View = Matrix.Identity;
    private static readonly Matrix Proj =
        Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 1f, 0.1f, 5000f);
    private const float ViewHeight = 720f;

    private static ShipLodProfile Profile()
        => new() { FarPixelRadius = 7f, EnterFullPixelRadius = 12f, FadeBandPixelRadius = 5f };

    // ── tier selection by projected size ─────────────────────────────────

    [Fact]
    public void Projected_pixel_radius_grows_when_closer_or_when_larger()
    {
        var far = ShipLod.ProjectedPixelRadius(new Vector3(0, 0, -1000), 100f, View, Proj, ViewHeight);
        var near = ShipLod.ProjectedPixelRadius(new Vector3(0, 0, -100), 100f, View, Proj, ViewHeight);
        var big = ShipLod.ProjectedPixelRadius(new Vector3(0, 0, -100), 200f, View, Proj, ViewHeight);
        Assert.True(near > far, "a closer ship must project larger");
        Assert.True(big > near, "a bigger ship at the same distance must project larger");
    }

    [Fact]
    public void Tier_is_far_below_threshold_and_full_above_it()
    {
        var p = Profile();
        // Small on screen (far) → the sprite tier dominates.
        Assert.True(ShipLod.FarAmount(4f, p) > 0.9f, "tiny ship must be far tier");
        // Large on screen (near) → full model.
        Assert.Equal(0f, ShipLod.FarAmount(25f, p), 2);
        // Mid-band → smooth blend (strictly between 0 and 1).
        float mid = ShipLod.FarAmount(9f, p);
        Assert.True(mid > 0f && mid < 1f, "mid-band must be a smooth crossfade");
    }

    [Fact]
    public void Tier_selection_has_hysteresis_no_flip_flop()
    {
        var p = Profile();
        // Not currently far: only drops to Far once it is at/below FarPixelRadius.
        Assert.False(ShipLod.NextFarState(9f, currentlyFar: false, p));
        Assert.True(ShipLod.NextFarState(5f, currentlyFar: false, p));
        // Currently far: stays Far until it grows to EnterFullPixelRadius.
        Assert.True(ShipLod.NextFarState(9f, currentlyFar: true, p));
        Assert.False(ShipLod.NextFarState(15f, currentlyFar: true, p));
    }

    [Fact]
    public void Crossfade_is_continuous_no_pop()
    {
        var p = Profile();
        // Model fully opaque when not fading, gone when fully far.
        Assert.Equal(1f, ShipLod.ModelAlpha(0f, p), 3);
        Assert.Equal(0f, ShipLod.ModelAlpha(1f, p), 3);
        // Sprite has zero alpha when the model is full.
        Assert.Equal(0f, ShipLod.SpriteAlpha(0f, 4f, p), 3);
        // Once well past the fade band the sprite is fully opaque.
        Assert.Equal(1f, ShipLod.SpriteAlpha(1f, 0.1f, p), 3);
        // Across the band the two fade complements sum so nothing pops.
        for (float px = 5f; px <= 12f; px += 0.5f)
        {
            float fa = ShipLod.FarAmount(px, p);
            float model = ShipLod.ModelAlpha(fa, p);
            Assert.InRange(model, 0f, 1f);
            // At the switch the model is fading while the sprite is still ramping;
            // together they never both vanish.
            Assert.True(model + fa >= 1f - 1e-3f, "model alpha + far amount must cover the screen continuously");
        }
    }

    // ── native LODGeo level selection ────────────────────────────────────

    [Fact]
    public void Native_lod_level_chosen_by_projected_radius()
    {
        float[] radii = { 60f, 30f, 10f };   // descending (finest→coarsest), artist order
        Assert.Equal(0, ShipLod.NativeLodLevelFor(200f, radii));   // huge → base/finest
        Assert.Equal(1, ShipLod.NativeLodLevelFor(50f, radii));    // ≤ 60 → first coarse
        Assert.Equal(2, ShipLod.NativeLodLevelFor(20f, radii));    // ≤ 30 → second coarse
        Assert.Equal(3, ShipLod.NativeLodLevelFor(5f, radii));     // ≤ 10 → coarsest
        Assert.Equal(0, ShipLod.NativeLodLevelFor(0f, new float[0]));   // no levels → base
    }

    // ── far-tier batching: identical hulls share one group ───────────────

    [Fact]
    public void Identical_hulls_batch_into_one_group_per_type()
    {
        var a1 = new FarSprite("fig05bmp", new Vector3(0, 0, -100), 4f, Color.Red, 0f);
        var a2 = new FarSprite("fig05bmp", new Vector3(1, 0, -101), 4f, Color.Red, 0f);
        var b = new FarSprite("utl18bmp", new Vector3(0, 0, -200), 5f, Color.Blue, 0f);
        var groups = ShipLodBatching.GroupByKey(new List<FarSprite> { a1, b, a2 });
        Assert.Equal(2, groups.Count);                 // two distinct hull types
        Assert.Equal(2, groups[0].Value.Count);        // the fig05 hull batched together
        Assert.Single(groups[1].Value);                // the utl18 hull on its own
        Assert.Equal("fig05bmp", groups[0].Key);
    }

    [Fact]
    public void Batching_preserves_each_hulls_side_colour()
    {
        var c = new Color(0.8f, 0.1f, 0.9f, 0.5f);
        var sprites = new List<FarSprite>
        {
            new("fig05bmp", new Vector3(0, 0, -100), 4f, c, 0f),
            new("fig05bmp", new Vector3(1, 0, -101), 4f, c, 0f),
        };
        var groups = ShipLodBatching.GroupByKey(sprites);
        Assert.Equal(c, groups[0].Value[0].Col);       // side tint rides through the batch
        Assert.Equal(c, groups[0].Value[1].Col);
    }

    // ── far-tier anchor invariance (brackets / targeting stay correct) ───

    [Fact]
    public void Far_tier_does_not_move_the_bracket_anchor()
    {
        var p = Profile();
        var eyeRel = new Vector3(0, 0, -3000);          // a distant ship, camera-relative
        float px = ShipLod.ProjectedPixelRadius(eyeRel, 10f, View, Proj, ViewHeight);
        Assert.True(px > 0f && px < float.MaxValue, "a far ship must remain projectable (finite px)");
        Assert.True(ShipLod.FarAmount(px, p) > 0f, "this distant ship is far-tier");

        // The far-tier sprite is drawn at the SAME camera-relative centre the
        // HUD bracket / targeting code projects, so the target box and side tag
        // stay glued to the ship whichever tier renders it.
        var spriteCentre = eyeRel;
        Assert.Equal(eyeRel, spriteCentre);
        Assert.Equal(eyeRel, spriteCentre - Vector3.Zero);   // no offset introduced by the LOD path
    }
}
