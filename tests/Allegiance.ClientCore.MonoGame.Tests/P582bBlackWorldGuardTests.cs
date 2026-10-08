using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Graphics;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P582b — the BLACK-WORLD render-target guard. On today's (P582) tree the
/// P579 volumetric HALF-RES pass did <c>gd.SetRenderTarget(halfRes) … draw …
/// gd.SetRenderTarget(null)</c> MID-FRAME, after the opaque scene was already
/// drawn, and the backbuffer defaulted to DiscardContents — so switching away
/// and back WIPED the opaque world (asteroids/stations vanish, ships survive).
/// P582 chased the wrong root cause (depth-RT feedback) and never reproduced
/// this; the real fix (option b, proven live on 2026-10-07) restores the ACTUAL
/// previous targets and makes every colour target PreserveContents.
/// <see cref="RenderTargetGuard.RestoreIsLossless"/> is the PURE rule the DEBUG
/// trip-wire applies: a scratch-target excursion is harmless iff the restored
/// target is REBOUND IDENTICALLY and PRESERVES ITS CONTENTS. A bare
/// binding-equality check can NOT see a DiscardContents wipe (the target is
/// rebound to the "right" target, just emptied) — hence the preserve clause is
/// mandatory. These pure-logic tests pin that rule; the class is absent on
/// today's tree → failing-first.
/// </summary>
public class P582bBlackWorldGuardTests
{
    [Fact]
    public void Old_pattern_restore_to_null_while_a_target_was_saved_is_not_lossless()
    {
        // The pre-fix bug: the pass left a real colour target (e.g. the
        // backbuffer-bound state) but restored to NULL. Not rebound identically
        // → the excursion wipes the scene. Must be flagged not-lossless.
        bool reboundIdentical = false;          // saved had a target, restored none
        bool preservesContents = true;          // even if it preserved, mismatch is fatal
        Assert.False(RenderTargetGuard.RestoreIsLossless(reboundIdentical, preservesContents),
            "restoring a different target than the one left is NOT lossless");
    }

    [Fact]
    public void DiscardContents_backbuffer_wipe_is_not_lossless_even_though_rebound_identically()
    {
        // The subtlest case: the pass restores to the SAME target (the backbuffer)
        // but the backbuffer is DiscardContents → the opaque scene is wiped even
        // though the binding matches. This is exactly the old bug in the normal
        // (non-bloom) path. A binding-equality check would MISS this; the
        // preserve-contents clause catches it.
        bool reboundIdentical = true;
        bool preservesContents = false;         // DiscardContents backbuffer
        Assert.False(RenderTargetGuard.RestoreIsLossless(reboundIdentical, preservesContents),
            "a DiscardContents target is NOT preserved across a scratch excursion");
    }

    [Fact]
    public void Fixed_pattern_identical_plus_preserve_is_lossless()
    {
        // Post-fix: rebound identically AND preserves contents → lossless.
        Assert.True(RenderTargetGuard.RestoreIsLossless(true, true));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void Any_missing_condition_is_not_lossless(bool identical, bool preserve)
    {
        Assert.False(RenderTargetGuard.RestoreIsLossless(identical, preserve));
    }

    [Fact]
    public void Lossless_is_exactly_both_conditions_true()
    {
        // The truth table is an AND — pins the rule precisely.
        for (int a = 0; a < 2; a++)
            for (int b = 0; b < 2; b++)
            {
                bool expect = a == 1 && b == 1;
                Assert.Equal(expect, RenderTargetGuard.RestoreIsLossless(a == 1, b == 1));
            }
    }

    [Fact]
    public void Bindings_equal_requires_same_length()
    {
        // Empty == empty (backbuffer); a length mismatch is unequal and is
        // decided BEFORE any RenderTarget dereference (safe headless).
        Assert.True(RenderTargetGuard.BindingsEqual(System.Array.Empty<RenderTargetBinding>(),
                                                    System.Array.Empty<RenderTargetBinding>()));
        Assert.False(RenderTargetGuard.BindingsEqual(System.Array.Empty<RenderTargetBinding>(),
                                                     new RenderTargetBinding[2]));
        Assert.False(RenderTargetGuard.BindingsEqual(new RenderTargetBinding[1],
                                                     System.Array.Empty<RenderTargetBinding>()));
    }
}
