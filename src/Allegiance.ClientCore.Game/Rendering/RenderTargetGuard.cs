using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P582b — guard rail for the black-world fix. The P579 volumetric HALF-RES
/// pass switches the render target away from the active colour target (the
/// backbuffer, the SceneRT bloom target, or the capture target) and back,
/// MID-FRAME, after the opaque scene is already drawn. With MonoGame's default
/// <c>RenderTargetUsage.DiscardContents</c> that switch-and-restore WIPES the
/// opaque scene (sky/asteroids/stations vanish; ships drawn later survive) —
/// the exact P547/P559 failure, reintroduced by P579. Two defences:
///
///  1. <see cref="RestoreIsLossless"/> — the PURE rule: a scratch-target
///     excursion is harmless only when the restored target EQUALS the one we
///     left AND that target preserves its contents (backbuffer via
///     PresentationParameters.RenderTargetUsage, a colour RT via
///     RenderTarget2D.RenderTargetUsage). This is the authoritative test for
///     the wipe: a binding-equality check alone can NOT see a DiscardContents
///     wipe (the target is rebound to the "right" target, just emptied).
///  2. <see cref="AssertColourTargetRestored"/> — DEBUG-only trip-wire
///     (compiled out in Release, like P582's feedback-loop trip-wire) that
///     calls <see cref="RestoreIsLossless"/> at the restore site and writes a
///     loud trace line if the rule is violated.
/// </summary>
public static class RenderTargetGuard
{
    /// <summary>
    /// Pure rule: is restoring from a scratch target lossless? Lossless iff the
    /// restored target binding matches the saved one AND the restored target
    /// preserves its contents (so nothing already drawn is discarded). The two
    /// booleans are derived by the caller (rendering-context-specific, not
    /// headless-testable), the AND-logic is what the failing-first test pins.
    /// </summary>
    public static bool RestoreIsLossless(bool targetReboundIdentical, bool targetPreservesContents)
        => targetReboundIdentical && targetPreservesContents;

    /// <summary>True when the restored binding is the same target set as saved
    /// (empty binding == the backbuffer).</summary>
    public static bool BindingsEqual(RenderTargetBinding[] a, RenderTargetBinding[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i].RenderTarget != b[i].RenderTarget) return false;
        return true;
    }

    /// <summary>
    /// True when the given binding set preserves its contents: an EMPTY binding
    /// means the backbuffer (preserved iff the presentation parameters say so);
    /// a colour RT is preserved iff its own RenderTargetUsage is PreserveContents.
    /// </summary>
    public static bool BindingPreservesContents(GraphicsDevice gd, RenderTargetBinding[] binding)
    {
        if (binding.Length == 0)
            return gd.PresentationParameters.RenderTargetUsage == RenderTargetUsage.PreserveContents;
        foreach (var b in binding)
            if (b.RenderTarget is not RenderTarget2D rt
                || rt.RenderTargetUsage != RenderTargetUsage.PreserveContents)
                return false;
        return true;
    }

    /// <summary>
    /// DEBUG-only trip-wire for the P579 half-res pass: after restoring the
    /// saved render targets, verify the restore is lossless (identical binding
    /// AND preserving contents). Fires a loud trace line when violated —
    /// exactly the "render target changed between the opaque colour pass and the
    /// HUD" regression class. Compiled out entirely in Release.
    /// </summary>
    public static void AssertColourTargetRestored(
        GraphicsDevice gd, RenderTargetBinding[] savedTargets, string ctx)
    {
#if DEBUG
        var restored = gd.GetRenderTargets();
        bool equal = BindingsEqual(savedTargets, restored);
        bool preserve = BindingPreservesContents(gd, restored);
        if (!RestoreIsLossless(equal, preserve))
        {
            var msg = $"[RenderTargetGuard] {ctx}: volatile colour-target excursion NOT lossless " +
                      $"(reboundIdentical={equal} preservesContents={preserve}; saved={savedTargets.Length} target(s), " +
                      $"restored={restored.Length} target(s)). Opaque scene MAY have been wiped.";
            System.Console.WriteLine(msg);
            System.Diagnostics.Debug.WriteLine(msg);
        }
#endif
    }

    /// <summary>
    /// HUD-21b — trip-wire overload for a caller that KNOWS the colour target it
    /// is restoring (<c>null</c> = the backbuffer). Same losslessness rule, but it
    /// does not require the caller to snapshot <c>GetRenderTargets()</c> (a fresh
    /// array per call) on the steady, per-frame path. The verification read is
    /// DEBUG-only, exactly like the array overload (compiled out in Release).
    /// </summary>
    public static void AssertColourTargetRestored(
        GraphicsDevice gd, RenderTarget2D? expected, string ctx)
    {
#if DEBUG
        var restored = gd.GetRenderTargets();
        bool equal = expected is null
            ? restored.Length == 0
            : restored.Length == 1 && ReferenceEquals(restored[0].RenderTarget, expected);
        bool preserve = BindingPreservesContents(gd, restored);
        if (!RestoreIsLossless(equal, preserve))
        {
            var msg = $"[RenderTargetGuard] {ctx}: volatile colour-target excursion NOT lossless " +
                      $"(reboundIdentical={equal} preservesContents={preserve}; expected=" +
                      (expected is null ? "backbuffer" : expected.Name) +
                      $", restored={restored.Length} target(s)). Opaque scene MAY have been wiped.";
            System.Console.WriteLine(msg);
            System.Diagnostics.Debug.WriteLine(msg);
        }
#endif
    }
}
