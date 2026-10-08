using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P582 — the depth-render-target unbind guard (<see cref="DepthTextureBinding"/>).
/// Root cause of "--fx volumetric/displaced world goes black once explosions
/// start": a renderer that SAMPLES the linear-depth target
/// (<see cref="VfxPostProcessor.DepthRT"/>) — VolumetricRenderer,
/// SoftParticleRenderer (and everything routed through it: enhanced smoke,
/// engine/missile trails, damage debris) — bound it as a GL texture and never
/// unbound it. Next frame the depth pass calls SetRenderTarget(DepthRT) on a
/// target still bound as a sampler → an OpenGL texture/framebuffer feedback
/// loop that breaks the depth pass, and a broken depth pass hides the opaque
/// world (asteroids/stations vanish; ships/effects/alephs/HUD survive).
///
/// These tests are headless (no GraphicsDevice) — the pure slot-mutation core
/// is driven against a fake slot array, proving the guard actually clears the
/// depth target from every unit it occupies. On the pre-P582 tree this class
/// does not exist (failing-first precondition, same convention as P573).
/// </summary>
public class P582DepthUnbindTests
{
    // A few sentinels standing in for GL texture objects (no device needed).
    private static readonly object DepthRT = new();
    private static readonly object Fire = new();
    private static readonly object Smoke = new();

    private static object?[] Slots() => new object?[DepthTextureBinding.MaxTextureSlots];

    [Fact]
    public void Depth_bound_on_two_slots_is_reported_and_cleared_from_both()
    {
        var slots = Slots();
        slots[0] = DepthRT;                 // bound as the depth sampler
        slots[1] = Fire;                    // an unrelated live texture
        slots[7] = DepthRT;                 // the depth target also on a second unit
        slots[3] = Smoke;

        // Detect both, leave everything else intact.
        Assert.Equal(new[] { 0, 7 }, DepthTextureBinding.SlotsHolding(i => slots[i], DepthRT));

        int cleared = DepthTextureBinding.UnbindSlots(i => slots[i], (i, v) => slots[i] = v, DepthRT);
        Assert.Equal(2, cleared);

        Assert.Null(slots[0]);
        Assert.Null(slots[7]);
        Assert.Same(Fire, slots[1]);        // unrelated textures untouched
        Assert.Same(Smoke, slots[3]);
        Assert.False(DepthTextureBinding.AnySlotHolds(i => slots[i], DepthRT),
            "after unbind the depth render target must not be bound on any unit");
    }

    [Fact]
    public void Already_unbound_target_clears_zero_slots()
    {
        var slots = Slots();
        slots[0] = Fire;
        slots[1] = Smoke;

        Assert.Equal(0, DepthTextureBinding.UnbindSlots(i => slots[i], (i, v) => slots[i] = v, DepthRT));
        Assert.Same(Fire, slots[0]);
        Assert.Same(Smoke, slots[1]);
    }

    [Fact]
    public void AnySlotHolds_is_false_when_never_bound_and_true_when_bound()
    {
        var slots = Slots();
        Assert.False(DepthTextureBinding.AnySlotHolds(i => slots[i], DepthRT));
        slots[9] = DepthRT;
        Assert.True(DepthTextureBinding.AnySlotHolds(i => slots[i], DepthRT));
    }

    [Fact]
    public void Unbinding_one_target_leaves_a_different_duplicate_bound()
    {
        // Two DIFFERENT targets sharing slots: clearing one must not disturb
        // the other (each depth-sampling renderer unbinds only what it bound).
        var other = new object();
        var slots = Slots();
        slots[0] = DepthRT;
        slots[1] = other;
        slots[4] = DepthRT;

        DepthTextureBinding.UnbindSlots(i => slots[i], (i, v) => slots[i] = v, DepthRT);
        Assert.False(DepthTextureBinding.AnySlotHolds(i => slots[i], DepthRT));
        Assert.Same(other, slots[1]);       // the other target is untouched
    }

    /// <summary>Sanity: the device-bound overload would iterate every unit and
    /// never read outside the fixed slot budget (which is what protects a real
    /// GraphicsDevice's TextureCollection that lacks a Count).</summary>
    [Fact]
    public void Slot_scan_stays_within_the_texture_unit_budget()
    {
        Assert.InRange(DepthTextureBinding.MaxTextureSlots, 8, 64);
        // The pure scan must only ever index [0, MaxTextureSlots).
        var indexes = new List<int>();
        DepthTextureBinding.SlotsHolding(i => { indexes.Add(i); return null; }, DepthRT);
        Assert.Equal(DepthTextureBinding.MaxTextureSlots, indexes.Count);
        Assert.All(indexes, i => Assert.InRange(i, 0, DepthTextureBinding.MaxTextureSlots - 1));
        Assert.Equal(indexes.Distinct().Count(), indexes.Count);   // every unit visited once
    }
}
