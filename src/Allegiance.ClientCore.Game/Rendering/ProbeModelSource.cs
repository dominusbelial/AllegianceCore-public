using System;
using System.Collections.Generic;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P290 — resolves which real probe model the renderer loads.
///
/// <para><b>Runtime native-.mdl, not a content-pipeline asset.</b> P287's art
/// import brought in only ships (72) and missiles (3) — there is no probe asset
/// in <c>MODELS_MANIFEST_P287.json</c>, so the deployed-probe visual must come
/// from parsing a real native <c>.mdl</c> file at runtime, exactly like
/// <see cref="MissileModelSource"/> (<c>MdlFile.Parse</c> +
/// <c>ALLEGIANCE_ARTWORK_PATH</c>).</para>
///
/// <para><b>Census result (this session).</b> PCore001.igc has 53 probe types
/// across <b>17 distinct <c>ModelName</c>s, and all 17 real <c>.mdl</c> files
/// are present in the artwork tree and parse cleanly</b> (68–866 verts /
/// 64–786 tris each). The original renderer used a single demo model. HD-3 now uses each entity's
/// <c>ProbetypeId</c> to load its own model and per-mesh atlas materials on demand;
/// this helper retains the canonical fallback for missing type/model data.
/// <c>utl02</c> is the canonical base "Probe" (six real probe types, all
/// radius 4, 280 tris of real geometry), a faithful representative of what a
/// player deploys.</para>
/// </summary>
public static class ProbeModelSource
{
    /// <summary>The real <c>.mdl</c> stem for the canonical base Probe
    /// (PCore001 expendable types 72/73/850/861/862/1002, all Radius = 4).
    /// Used when a deployed probe's own type/model cannot be resolved.</summary>
    public const string DemoModelStem = "utl02";

    /// <summary>
    /// P615 — the demo probe mesh's native texture unit stem. <c>utl02.mdl</c>'s
    /// imported texture namespace is <c>utl02bmp</c> (a real on-disk unit);
    /// all 53 PCore001 probe types carry an EMPTY <c>textureName</c>, so native
    /// paints the probe from this model-own import (probeigc.cpp:112-113 +
    /// thinggeo.cpp:1038-1043). Used by <see cref="ProbeRenderer.AppliesTexture"/>.
    /// </summary>
    public const string DemoTextureStem = "utl02bmp";

    /// <summary>The configured artwork root (ALLEGIANCE_ARTWORK_PATH, falling
    /// back to the standard dev location) — the same resolution the missile
    /// source uses; see <see cref="AssetSources.ResolveRoot"/>.</summary>
    public static string ResolveArtworkDir() => AssetSources.ResolveRoot();

    /// <summary>Parse the demo probe's real <c>.mdl</c> once (from the default
    /// desktop artwork source). Throws if the artwork path is unavailable — the
    /// model is required for rendering.</summary>
    public static MdlFile LoadDemoModel() => LoadDemoModel(AssetSources.Artwork);

    /// <summary>Parse the demo probe's real <c>.mdl</c> once from
    /// <paramref name="assets"/>. Throws if the asset is unavailable — the
    /// model is required for rendering.</summary>
    public static MdlFile LoadDemoModel(IAssetSource assets) =>
        MdlFile.Parse(AssetSources.ReadAllBytes(assets, DemoModelStem + ".mdl"));

    /// <summary>
    /// P615 — the native probe texture-stem resolution, ported from native
    /// <c>CprobeIGC::Initialize</c>'s <c>Load(0, modelName, textureName,
    /// iconName, …)</c> (<c>src/Igc/probeigc.cpp:112-113</c>): an explicit
    /// probe-type <c>textureName</c> override wins, else the model's OWN
    /// imported texture namespace (<c>m_pgeoMesh-&gt;FindTexture()</c>,
    /// <c>thinggeo.cpp:1038-1043</c> / <c>xfile.cpp:577</c>) — the P593 rule,
    /// never a <c>&lt;modelName&gt;bmp</c> guess. Decoded from PCore001, ALL
    /// 53 probe types carry an <em>empty</em> <c>textureName</c>, so native
    /// paints every probe from its model's own namespace — for the demo
    /// canonical <c>utl02</c> mesh that is <c>utl02bmp</c> (a real unit on
    /// disk). <paramref name="fileExists"/> is the case-insensitive on-disk
    /// stem probe.
    /// </summary>
    public static string? ResolveTextureStemFor(
        MdlFile mdl, string? overrideTextureName, string modelName, Func<string, bool> fileExists)
        => NativeShipResolver.ResolveTextureStem(mdl, overrideTextureName, modelName, fileExists);

    /// <summary>
    /// P615 — resolve the exact on-disk texture stem for the shared demo probe
    /// model (<see cref="DemoModelStem"/>), case-insensitively, or null when
    /// none resolves (the renderer keeps its lit-solid fallback, never an
    /// invisible probe). <c>utl02.mdl</c>'s imported namespace is
    /// <c>utl02bmp</c>, a real on-disk unit.
    /// </summary>
    public static string? ResolveDemoTextureStem(IAssetSource assets)
    {
        var mdl = LoadDemoModel(assets);
        var disk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in assets.Enumerate("."))
            if (f.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase))
                disk.Add(Path.GetFileNameWithoutExtension(f));
        return ResolveTextureStemFor(mdl, null, DemoModelStem, disk.Contains);
    }
}
