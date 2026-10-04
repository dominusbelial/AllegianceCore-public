using System;
using System.Collections.Generic;
using System.Linq;

namespace Allegiance.Core.Content;

/// <summary>
/// P520 — data-driven, per-asset orientation correction for the Meshy ship
/// models, applied ON TOP of the global <c>NoseAlignmentCorrection</c>.
///
/// <para><b>Why this exists.</b> Every Meshy ship comes from its own generation
/// job, so a model can come out yawed differently from its class siblings.
/// Domingo found one live: the IC Devastator's mesh looked like it needed to
/// spin 90° to the right. The geometry confirmed it — <c>ic-dev</c>'s GLB
/// bounding box has its long axis on Z (0.371 / 0.663 / 1.888) while every other
/// IC ship's long axis is on X (the engine-forward axis the global -90°-about-Y
/// correction expects). A −90°-about-Y bone rotation at load was confirmed live
/// by Domingo ("devastator looks better"), so this table seeds that exact value
/// and makes it real + general.</para>
///
/// <para><b>The fleet-forward convention.</b> After the global
/// <see cref="ShipRenderer"/> correction, a correctly-oriented ship's raw
/// geometry long axis lies on the engine X (forward) axis. This table records
/// the measured raw extents of every shipped model so the orientation can be
/// checked headlessly (no Content / GPU needed), flags any asset whose long axis
/// disagrees with the convention / its class siblings, and carries the yaw
/// (plus optional pitch/roll) override that fixes it.</para>
/// </summary>
public static class ModelOrientationOverrides
{
    /// <summary>Per-asset rotation to fold into the mesh's parent-bone transform
    /// once at load. Degrees; a positive yaw rotates the nose clockwise viewed
    /// from above (+Y up), matching MonoGame's right-handed convention.
    /// Keyed by the P287 asset name (<c>"&lt;civ&gt;-&lt;class&gt;"</c>).</summary>
    public readonly record struct Override(float YawDeg, float PitchDeg, float RollDeg);

    /// <summary>Dry override that does nothing (used so a caller can cheaply
    /// check "has an override" without special-casing identity).</summary>
    public static readonly Override Identity = new(0f, 0f, 0f);

    /// <summary>
    /// The seeded per-asset orientation table.
    /// <list type="bullet">
    ///   <item><b>ic-dev</b> — the IC Devastator (<c>tecdev</c> → asset
    ///   <c>ic-dev</c>): its Meshy mesh long axis is Z, yawed 90° versus its
    ///   class siblings (all X). −90° about Y was confirmed live by Domingo
    ///   (2026-10-03). Seeded here; the P520 audit found no other asset needing
    ///   an override (see the P520 report).</item>
    /// </list>
    /// </summary>
    private static readonly Dictionary<string, Override> Overrides = new(StringComparer.Ordinal)
    {
        ["ic-dev"] = new Override(-90f, 0f, 0f),
        // P520 audit — decisively Z-long (yawed 90° from the forward-X
        // convention), all sharing the same Z-long → -90°-about-Y fix Domingo
        // verified live on ic-dev. gt-dev is the SAME mesh as ic-dev (the "dev"
        // / tecdev role), identical extents. rix-con / rix-miner are equally
        // elongated on Z (ratio ≳3.4).
        ["gt-dev"] = new Override(-90f, 0f, 0f),
        ["rix-con"] = new Override(-90f, 0f, 0f),
        ["rix-miner"] = new Override(-90f, 0f, 0f),
        // P511 — dreg batch measured decisively Z-long (same ic-dev signature):
        // dreg-carrier x34.5/z190.6 (ratio 5.5) and dreg-corvette x111/z190
        // (elongation 0.58 < 0.72). Both get the verified -90°-about-Y fix.
        ["dreg-carrier"] = new Override(-90f, 0f, 0f),
        ["dreg-corvette"] = new Override(-90f, 0f, 0f),
    };

    /// <summary>Whether an asset carries a non-identity orientation override.</summary>
    public static bool HasOverride(string asset) =>
        Overrides.TryGetValue(asset, out var o) && o != Identity;

    /// <summary>Get an asset's override, or <see cref="Identity"/> when none.</summary>
    public static Override Get(string asset) =>
        Overrides.TryGetValue(asset, out var o) ? o : Identity;

    /// <summary>Every asset that carries a non-identity override.</summary>
    public static IReadOnlyCollection<string> OverriddenAssets =>
        Overrides.Where(kv => kv.Value != Identity).Select(kv => kv.Key).ToArray();

    /// <summary>
    /// Measured raw bounding-box extents (max−min per axis) of each shipped
    /// model, in the bone-restored render frame (raw vertex positions
    /// transformed by each mesh's <c>ParentBone.Transform</c> — the frame the
    /// renderer actually draws, which preserves the GLB-authored long-axis
    /// direction). Units are the content pipeline's real hull units (~100–190).
    /// Captured from the shipped FBX content by <c>roster-capture orient</c>;
    /// embedded so the audit is headless and reproducible. Keys are the 72 P287
    /// <c>kind=="ship"</c> asset names.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, (float X, float Y, float Z)> Extents =
        new Dictionary<string, (float, float, float)>(StringComparer.Ordinal)
        {
            ["belters-bomber"] = (189.782f, 47.353f, 125.426f),
            ["belters-fig"] = (190.070f, 52.460f, 163.932f),
            ["belters-scout"] = (189.969f, 90.284f, 167.238f),
            ["bios-bomber"] = (189.056f, 39.931f, 83.811f),
            ["bios-carrier"] = (190.267f, 50.227f, 83.001f),
            ["bios-constructor"] = (190.439f, 34.660f, 54.217f),
            ["bios-corvette"] = (189.521f, 37.333f, 88.864f),
            ["bios-cruiser"] = (190.623f, 71.701f, 101.200f),
            ["bios-destroyer"] = (190.055f, 83.156f, 38.414f),
            ["bios-devastator"] = (190.357f, 65.417f, 61.299f),
            ["bios-ejectpod"] = (145.219f, 108.002f, 190.246f),
            ["bios-fighter"] = (190.101f, 38.123f, 72.326f),
            ["bios-interceptor"] = (190.250f, 58.094f, 106.952f),
            ["bios-miner"] = (190.216f, 39.129f, 101.226f),
            ["bios-stealthfighter"] = (190.375f, 28.869f, 86.560f),
            ["bios-trooptransport"] = (190.357f, 110.767f, 74.330f),
            ["dreg-bbr2"] = (137.058f, 86.592f, 189.949f),
            ["dreg-carrier"] = (34.500f, 56.516f, 190.578f),
            ["dreg-corvette"] = (110.977f, 108.285f, 190.314f),
            ["dreg-fig2"] = (177.982f, 91.355f, 189.926f),
            ["dreg-frigate"] = (188.246f, 58.787f, 109.460f),
            ["dreg-scout2"] = (189.780f, 86.781f, 113.399f),
            ["dreg-stealthfighter"] = (190.166f, 85.116f, 64.838f),
            ["dreg-trooptransport"] = (190.358f, 40.780f, 63.234f),
            ["giga-bomber"] = (190.118f, 109.586f, 65.002f),
            ["giga-fighter"] = (189.902f, 79.462f, 80.997f),
            ["giga-interceptor"] = (190.305f, 32.980f, 109.299f),
            ["giga-scout"] = (185.110f, 153.675f, 190.012f),
            ["gt-ass"] = (189.521f, 37.333f, 88.864f),
            ["gt-bomber"] = (190.319f, 42.976f, 108.651f),
            ["gt-carrier"] = (190.267f, 50.227f, 83.001f),
            ["gt-con"] = (190.574f, 37.479f, 69.185f),
            ["gt-cruiser"] = (190.623f, 71.701f, 101.200f),
            ["gt-dev"] = (37.364f, 66.361f, 188.862f),
            ["gt-fighter"] = (174.468f, 97.573f, 190.341f),
            ["gt-hod"] = (190.502f, 47.890f, 65.943f),
            ["gt-int"] = (171.650f, 113.333f, 190.332f),
            ["gt-miner"] = (190.676f, 51.355f, 106.632f),
            ["gt-mustang"] = (190.130f, 64.891f, 154.157f),
            ["gt-pod"] = (190.596f, 84.434f, 78.661f),
            ["gt-rd"] = (190.214f, 42.820f, 114.388f),
            ["gt-scout"] = (190.449f, 56.898f, 64.948f),
            ["gt-sf"] = (190.501f, 46.307f, 139.597f),
            ["gt-tt"] = (190.357f, 110.767f, 74.330f),
            ["ic-bomber"] = (188.830f, 56.072f, 102.066f),
            ["ic-carrier"] = (190.267f, 50.227f, 83.001f),
            ["ic-constructor"] = (190.439f, 34.660f, 54.217f),
            ["ic-corvette"] = (189.521f, 37.333f, 88.864f),
            ["ic-cruiser"] = (190.623f, 71.701f, 101.200f),
            ["ic-dev"] = (37.364f, 66.361f, 188.862f),
            ["ic-devastator"] = (190.357f, 65.417f, 61.299f),
            ["ic-ejectpod"] = (145.219f, 108.002f, 190.246f),
            ["ic-fighter"] = (190.295f, 71.295f, 137.087f),
            ["ic-frigate"] = (190.281f, 48.522f, 62.587f),
            ["ic-int"] = (190.213f, 57.886f, 171.317f),
            ["ic-interceptor"] = (190.340f, 58.900f, 185.256f),
            ["ic-miner"] = (190.216f, 39.129f, 101.226f),
            ["ic-scout"] = (152.729f, 143.714f, 190.168f),
            ["ic-stealthfighter"] = (190.212f, 70.921f, 102.983f),
            ["ic-trooptransport"] = (190.357f, 110.767f, 74.330f),
            ["oh-bomber"] = (190.075f, 37.167f, 114.236f),
            ["oh-carrier"] = (190.183f, 41.860f, 66.566f),
            ["oh-corvette"] = (189.224f, 29.974f, 61.592f),
            ["oh-cruiser"] = (190.235f, 66.159f, 76.660f),
            ["oh-devastator"] = (190.154f, 27.139f, 63.973f),
            ["oh-fighter"] = (190.066f, 39.836f, 104.478f),
            ["oh-int"] = (190.292f, 36.940f, 107.609f),
            ["oh-interceptor"] = (190.570f, 37.359f, 108.267f),
            ["oh-scout"] = (190.385f, 58.341f, 57.163f),
            ["oh-stealthfighter"] = (190.202f, 169.093f, 172.450f),
            ["rix-ass"] = (189.760f, 85.587f, 159.197f),
            ["rix-bomber"] = (140.627f, 44.621f, 189.902f),
            ["rix-carrier"] = (189.821f, 73.079f, 120.755f),
            ["rix-con"] = (55.173f, 54.483f, 189.680f),
            ["rix-cruiser"] = (93.404f, 189.876f, 146.778f),
            ["rix-fighter"] = (142.983f, 44.829f, 189.860f),
            ["rix-int"] = (178.773f, 51.188f, 189.689f),
            ["rix-miner"] = (45.685f, 46.665f, 189.894f),
            ["rix-pod"] = (169.028f, 154.469f, 189.943f),
            ["rix-scout"] = (160.838f, 116.494f, 189.950f),
            ["rix-sf"] = (190.466f, 58.621f, 178.279f),
            ["rix-tt"] = (189.880f, 46.130f, 126.631f),
            ["tf-bomber"] = (190.252f, 59.251f, 170.113f),
            ["tf-carrier"] = (190.201f, 35.147f, 81.178f),
            ["tf-cruiser"] = (190.547f, 67.995f, 86.433f),
            ["tf-fig"] = (190.679f, 46.423f, 139.312f),
            ["tf-int"] = (186.013f, 37.831f, 190.713f),
            ["tf-scout"] = (190.574f, 47.815f, 137.380f),
            ["tf-stealthfighter"] = (190.478f, 57.815f, 142.273f),
            ["tf-trooptransport"] = (190.340f, 47.822f, 95.218f),
        };

    /// <summary>The axis (0=X forward, 1=Y up, 2=Z side) the extents elongate
    /// along. Deterministic tie-break toward X so a perfect cube reports
    /// forward.</summary>
    public static int LongAxisOf(float x, float y, float z)
    {
        if (x >= y && x >= z) return 0;
        if (y >= z) return 1;
        return 2;
    }

    /// <summary>Decisiveness of the long-axis call: ratio of the second-largest
    /// extent to the largest. Near 1 (≳0.72) the two biggest axes are too close
    /// to tell nose-from-tail / axis by numbers alone — those need a visual
    /// check (the P520 contact sheet). Low → the axis is unambiguous.</summary>
    public static float ElongationRatio(float x, float y, float z)
    {
        var s = new[] { x, y, z }.OrderByDescending(v => v).ToArray();
        if (s[0] <= 1e-6f) return 1f;
        return Math.Clamp(s[1] / s[0], 0f, 1f);
    }

    /// <summary>An asset is "axis-definitive" (numbers alone can settle its
    /// long axis) when its two biggest extents are clearly separated.</summary>
    public static bool IsDefinitive(float x, float y, float z) => ElongationRatio(x, y, z) < 0.72f;

    /// <summary>
    /// Per-asset audit verdict. An asset is <see cref="StatusFlagged"/> when its
    /// long axis disagrees with the fleet-forward X convention AND it has no
    /// corrective override AND its axis call is definitive (not near-square).
    /// <see cref="StatusAmbiguous"/> = axis too near-square to settle by numbers
    /// (flagged for a visual check, not hard-failed). <see cref="StatusOk"/> =
    /// aligned, or covered by a corrective override.
    /// </summary>
    public enum Status { Ok, Flagged, Ambiguous }

    public readonly record struct Entry(
        string Asset, float X, float Y, float Z, int Axis, bool Definitive, Status Status);

    /// <summary>Build an audit <see cref="Entry"/> for one asset from its
    /// measured extents (single-asset form of the classification in
    /// <see cref="Audit"/>).</summary>
    public static Entry EntryFrom(string asset, float x, float y, float z)
    {
        var e = ClassifyRaw(asset, x, y, z);
        // A corrective override downgrades a raw flag to Ok (the fix covers it).
        if (e.Status == Status.Flagged && HasOverride(asset))
            return e with { Status = Status.Ok };
        return e;
    }

    /// <summary>Classify one asset's extents IGNORING the override table — the
    /// raw geometric verdict. This is what the audit would report for an asset
    /// with no corrective override yet (the failing-first state that flags a
    /// newly-landed rotated Meshy model), and is the value
    /// <see cref="OverridesMustCoverEveryRawFlag"/> guards against drifting.</summary>
    public static Entry ClassifyRaw(string asset, float x, float y, float z)
    {
        int axis = LongAxisOf(x, y, z);
        bool def = IsDefinitive(x, y, z);
        Status st = axis == 0 ? Status.Ok : !def ? Status.Ambiguous : Status.Flagged;
        return new Entry(asset, x, y, z, axis, def, st);
    }

    /// <summary>Audit every embedded extent against the forward-X convention and
    /// the override table. Pure / headless (no Content, no GPU).</summary>
    public static IReadOnlyList<Entry> Audit()
    {
        var result = new List<Entry>();
        foreach (var (asset, e) in Extents.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            result.Add(EntryFrom(asset, e.X, e.Y, e.Z));
        return result;
    }

    /// <summary>Every asset the RAW audit hard-flags (long axis not forward,
    /// decisive) REGARDLESS of overrides — i.e. every asset that needs a
    /// corrective override or is currently broken. Must be a subset of
    /// <see cref="OverriddenAssets"/> for the fleet to be clean.</summary>
    public static IReadOnlyList<string> RawFlaggedAssets() =>
        Extents.Select(kv => ClassifyRaw(kv.Key, kv.Value.X, kv.Value.Y, kv.Value.Z))
               .Where(e => e.Status == Status.Flagged).Select(e => e.Asset).ToList();

    /// <summary>True iff every raw-flagged asset (decisively non-forward, no
    /// excuse) carries a corrective override — i.e. nothing slipped through the
    /// audit unfixed. This is the P520 invariant the failing-first test asserts
    /// (it was false before ic-dev / gt-dev / rix-con / rix-miner were seeded,
    /// true after).</summary>
    public static bool OverridesMustCoverEveryRawFlag()
    {
        foreach (var asset in RawFlaggedAssets())
            if (!HasOverride(asset))
                return false;
        return true;
    }
}
