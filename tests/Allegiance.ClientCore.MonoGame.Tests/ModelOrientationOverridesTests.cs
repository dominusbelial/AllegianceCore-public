using System.Linq;
using Allegiance.Core.Content;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P520 — the per-asset ship orientation audit. Verifies the failing-first /
/// passes-after contract: the raw geometric audit FLAGS <c>ic-dev</c> (and the
/// other decisively-Z-long Meshy meshes) before a corrective override exists,
/// and the full audit is CLEAN once the override table covers every raw flag.
/// Runs headless (embedded measured extents, no Content / GPU).
/// </summary>
public class ModelOrientationOverridesTests
{
    // Reference extents from the P520 measurement (render frame, real hull units).
    private const float IcDevX = 37.364f, IcDevY = 66.361f, IcDevZ = 188.862f;   // long axis Z
    private const float IcDevastatorX = 190.357f, IcDevastatorY = 65.417f, IcDevastatorZ = 61.299f; // long axis X

    [Fact]
    public void Audit_FlagsIcDev_BeforeOverride()
    {
        // FAILING-FIRST: with ic-dev's long axis on Z and NO override applied,
        // the audit hard-flags it — exactly what a human would see as "needs to
        // spin 90 degrees to the right".
        var raw = ModelOrientationOverrides.ClassifyRaw("ic-dev", IcDevX, IcDevY, IcDevZ);
        Assert.Equal(ModelOrientationOverrides.Status.Flagged, raw.Status);
        Assert.Equal(2, raw.Axis); // Z

        // The same is true for the other audit-found, decisively-Z-long meshes.
        var raws = new[]
        {
            ModelOrientationOverrides.ClassifyRaw("ic-dev", 37.364f, 66.361f, 188.862f),
            ModelOrientationOverrides.ClassifyRaw("gt-dev", 37.364f, 66.361f, 188.862f),
            ModelOrientationOverrides.ClassifyRaw("rix-con", 55.173f, 54.483f, 189.680f),
            ModelOrientationOverrides.ClassifyRaw("rix-miner", 45.685f, 46.665f, 189.894f),
        };
        Assert.All(raws, r => Assert.Equal(ModelOrientationOverrides.Status.Flagged, r.Status));
    }

    [Fact]
    public void Audit_IsClean_AfterOverride()
    {
        // PASSES-AFTER: every raw-flagged asset carries a corrective override, so
        // the full audit reports no hard flags and the fleet-forward invariant holds.
        Assert.True(ModelOrientationOverrides.OverridesMustCoverEveryRawFlag(),
            "every raw-flagged (decisively non-forward) asset must have an override");

        var rawFlagged = ModelOrientationOverrides.RawFlaggedAssets().ToHashSet();
        foreach (var a in ModelOrientationOverrides.OverriddenAssets)
            Assert.Contains(a, rawFlagged);   // every override addresses a real flag

        // No asset in the shipped fleet remains hard-flagged once overrides are on.
        Assert.DoesNotContain(ModelOrientationOverrides.Audit(),
            e => e.Status == ModelOrientationOverrides.Status.Flagged);
    }

    [Fact]
    public void IcDev_Override_IsVerifiedNegative90Yaw()
    {
        var o = ModelOrientationOverrides.Get("ic-dev");
        Assert.Equal(-90f, o.YawDeg);
        Assert.Equal(0f, o.PitchDeg);
        Assert.Equal(0f, o.RollDeg);
        Assert.True(ModelOrientationOverrides.HasOverride("ic-dev"));
    }

    [Fact]
    public void OverrideSet_IsExactlyTheAuditFoundFour()
    {
        // Guard against accidental/stray overrides drifting in: only the P520
        // audit-verified set may carry a corrective yaw.
        Assert.Equal(
            new[] { "gt-dev", "ic-dev", "rix-con", "rix-miner" },
            ModelOrientationOverrides.OverriddenAssets.OrderBy(a => a).ToArray());
    }

    [Fact]
    public void KnownCorrect_IsForward_And_NearSquare_IsAmbiguous()
    {
        // A correctly-oriented X-long ship is Ok...
        var ok = ModelOrientationOverrides.ClassifyRaw("ic-devastator", IcDevastatorX, IcDevastatorY, IcDevastatorZ);
        Assert.Equal(ModelOrientationOverrides.Status.Ok, ok.Status);
        Assert.Equal(0, ok.Axis); // X

        // ...and a near-square Z-leaning hull (ejectpod) is Ambiguous (needs a
        // visual check, not an auto-flag) — the numbers can't settle it.
        var amb = ModelOrientationOverrides.ClassifyRaw("ic-ejectpod", 145.219f, 108.002f, 190.246f);
        Assert.Equal(ModelOrientationOverrides.Status.Ambiguous, amb.Status);
    }

    [Fact]
    public void Extents_CoversEveryShippedShipAsset()
    {
        // The embedded extent table must cover all 72 P287 ship assets so the
        // audit cannot silently miss a model.
        Assert.Equal(HullModelResolver.ShipAssets.Count, ModelOrientationOverrides.Extents.Count);
        Assert.True(HullModelResolver.ShipAssets.All(a => ModelOrientationOverrides.Extents.ContainsKey(a)));
    }
}
