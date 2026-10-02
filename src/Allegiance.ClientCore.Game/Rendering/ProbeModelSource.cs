using System;
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
/// 64–786 tris each). The MonoGame client's <see cref="ClientCore.State.ProbeEntity"/>
/// carries no probe-type id (the rendering scope adds no state plumbing), so
/// the renderer uses a <b>single real demo model</b> shared by every deployed
/// probe — the same graceful single-fallback convention P288's missile census
/// led to (most missile types fall back to one native <c>mis05.mdl</c> demo).
/// <c>utl02</c> is the canonical base "Probe" (six real probe types, all
/// radius 4, 280 tris of real geometry), a faithful representative of what a
/// player deploys.</para>
/// </summary>
public static class ProbeModelSource
{
    /// <summary>The real <c>.mdl</c> stem for the canonical base Probe
    /// (PCore001 expendable types 72/73/850/861/862/1002, all Radius = 4).
    /// This is the single native model shared by every deployed probe.</summary>
    public const string DemoModelStem = "utl02";

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
}
