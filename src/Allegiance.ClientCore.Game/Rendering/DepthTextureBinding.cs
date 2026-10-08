using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P582 — the shared guard for renderers that SAMPLE the linear-depth render
/// target (the soft-particle <c>DepthRT</c>). Root cause of the
/// "--fx volumetric / displaced world goes black once explosions start"
/// bug: <see cref="VolumetricRenderer"/> and <see cref="SoftParticleRenderer"/>
/// bound <c>DepthRT</c> as a texture (a GL sampler unit) and never unbound it.
/// Next frame the linear-depth pass calls
/// <c>SetRenderTarget(<see cref="VfxPostProcessor.DepthRT"/>)</c> to render
/// INTO that same texture while it is still bound as a sampler — an OpenGL
/// texture/framebuffer feedback loop (incomplete FBO / undefined behaviour in
/// MonoGame DesktopGL) that breaks the depth pass, and a broken depth pass
/// hides the opaque world (asteroids/stations), leaving only ships/effects/
/// alephs/HUD. (P547/P559 established that a broken depth pass hides geometry.)
///
/// Every renderer that samples the depth target must call
/// <see cref="UnbindDepth"/> after its draw so the next frame's depth pass
/// renders into an unbound target. <see cref="AssertDepthNotBoundAsTexture"/>
/// is the DEBUG-only trip-wire placed at the two
/// <c>SetRenderTarget(DepthRT)</c> sites to catch any renderer that forgets.
///
/// The slot-mutation core (<see cref="SlotsHolding"/>/<see cref="UnbindSlots"/>/
/// <see cref="AnySlotHolds"/>) is written against getter/setter delegates so it
/// is unit-testable headless (no GraphicsDevice); the
/// <see cref="UnbindDepth(GraphicsDevice, Texture)"/> overload binds it to the
/// real <c>GraphicsDevice.Textures</c> collection.
/// </summary>
public static class DepthTextureBinding
{
    /// <summary>Number of texture units MonoGame exposes (DesktopGL).
    /// The device-bound scan clamps to <c>gd.Textures.Count</c>; the pure
    /// helpers honour this as their upper bound.</summary>
    public const int MaxTextureSlots = 16;

    /// <summary>The sampler state restored on a slot after the depth texture is
    /// unbound. A neutral, widely-used default; any subsequent draw that needs
    /// a specific sampler sets its own via <c>Effect.Apply</c>.</summary>
    public static readonly SamplerState RestoredSampler = SamplerState.LinearClamp;

    /// <summary>Pure: which texture slots currently hold <paramref name="depth"/>
    /// (compared by reference). Returns the slot indices; empty when unbound.
    /// Written against <c>object</c> so it is testable headless (a real
    /// <c>Texture</c> needs a GraphicsDevice); the device-bound overloads cast
    /// the real <c>gd.Textures[i]</c> values to it.</summary>
    public static List<int> SlotsHolding(Func<int, object?> get, object depth)
    {
        var slots = new List<int>();
        for (int i = 0; i < MaxTextureSlots; i++)
            if (get(i) is { } t && ReferenceEquals(t, depth))
                slots.Add(i);
        return slots;
    }

    /// <summary>Pure: is <paramref name="depth"/> bound on ANY texture slot?</summary>
    public static bool AnySlotHolds(Func<int, object?> get, object depth)
    {
        for (int i = 0; i < MaxTextureSlots; i++)
            if (get(i) is { } t && ReferenceEquals(t, depth))
                return true;
        return false;
    }

    /// <summary>Pure: clear <paramref name="depth"/> from every texture slot that
    /// holds it. Returns the number of slots cleared (0 when already unbound).</summary>
    public static int UnbindSlots(Func<int, object?> get, Action<int, object?> set, object depth)
    {
        int cleared = 0;
        for (int i = 0; i < MaxTextureSlots; i++)
            if (get(i) is { } t && ReferenceEquals(t, depth))
            {
                set(i, null);
                cleared++;
            }
        return cleared;
    }

    /// <summary>
    /// Device-bound: after a pass that sampled <paramref name="depth"/> (the
    /// linear-depth render target), clear it from every texture unit it is
    /// bound on and restore the samplers it changed — so the next frame's
    /// depth pass can <c>SetRenderTarget(depth)</c> without a feedback loop.
    /// No-op when <paramref name="depth"/> is null (nothing to unbind) or
    /// already unbound.
    /// </summary>
    public static void UnbindDepth(GraphicsDevice gd, Texture? depth)
    {
        // TEMP failing-first demo: P582_NO_UNBIND=1 disables the unbind.
        if (System.Environment.GetEnvironmentVariable("P582_NO_UNBIND") is { Length: > 0 }) return;
        if (depth is null) return;
        int n = MaxTextureSlots;   // TextureCollection has no Count; indexer guards range
        for (int i = 0; i < n; i++)
        {
            if (gd.Textures[i] is { } t && ReferenceEquals(t, depth))
            {
                gd.Textures[i] = null;                 // unbind the sampler unit
                gd.SamplerStates[i] = RestoredSampler; // restore the sampler it changed
            }
        }
    }

    /// <summary>
    /// DEBUG-only trip-wire for the depth-pass <c>SetRenderTarget</c> sites:
    /// fires (writes a loud trace line) if <paramref name="depth"/> is still
    /// bound as a texture on any unit at the moment it is about to be rendered
    /// INTO — i.e. a feedback loop a renderer forgot to prevent. Compiled out
    /// in Release (zero overhead there); in Debug it logs and continues so a
    /// live proof cannot be aborted by it, only loudly flagged.
    /// </summary>
    public static void AssertDepthNotBoundAsTexture(GraphicsDevice gd, Texture? depth, string context)
    {
#if DEBUG
        if (depth is null) return;
        int n = MaxTextureSlots;   // TextureCollection has no Count; indexer guards range
        for (int i = 0; i < n; i++)
        {
            if (gd.Textures[i] is { } t && ReferenceEquals(t, depth))
            {
                Console.WriteLine(
                    $"[P582] DEPTH FEEDBACK LOOP: {context} — depth render target still bound as " +
                    $"a texture on unit {i}; an earlier depth-sampling renderer forgot to unbind it. " +
                    "World geometry (asteroids/stations) will vanish.");
                return; // one loud report is enough
            }
        }
#endif
    }
}
