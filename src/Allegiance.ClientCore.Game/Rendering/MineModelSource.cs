using System;
using System.Collections.Generic;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P291 — resolves the real visual asset for a deployed mine. The census
/// (this session) disproved the probe-style model-mesh assumption: <b>no mine
/// type in the corpus carries a <c>ModelName</c></b> (all 782 mine records
/// across every core have an empty <c>ModelName</c>). Native does not render a
/// mine as a mesh at all — <c>CmineIGC::Initialize</c> loads it with
/// <c>LoadMine(textureName, 1.0f, radius, iconName, ...)</c>
/// (<c>src/Igc/mineigc.cpp:103</c>), and the client's <c>LoadMine</c>
/// (<c>src/WinTrek/trekigc.cpp:1934-1957</c>) loads a <b>texture</b> surface
/// <c>textureName + "bmp"</c> into a <c>MineFieldGeo</c> decal field — there is
/// no geometry mesh. The one real texture across the corpus is <c>fxmine</c>
/// (asset <c>fxminebmp.mdl</c>, a 128×128 glow sprite), so this source parses
/// that real <c>.mdl</c> texture at runtime with
/// <see cref="MdlTextureDecoder"/> — the same runtime-native-asset pattern as
/// <see cref="MissileModelSource"/>/<see cref="ProbeModelSource"/>, applied to
/// the mine's real (texture) representation.</summary>
/// <remarks>
/// <para><b>Radius.</b> The renderer's decal field is scattered within a sphere
/// of the mine's type radius (<c>MineFieldGeo</c>, <c>efgeo.cpp:15-35</c>). The
/// wire carries the mine type id (<c>DataMineExport.MinetypeId</c>) but not the
/// radius, so <see cref="ResolveRadius"/> reads the real
/// <c>DataMineTypeIGC.MineRadius</c> from the static core (see
/// <see cref="Allegiance.ClientCore.State.MineEntity"/> for the C++ shadowing
/// proof that <c>MineRadius</c> — not the garbage base <c>Radius</c> — is the
/// operative value). Falls back to <see cref="DefaultRadius"/> when the type is
/// unknown or the core is absent (the degrade-not-crash convention).</para>
/// </remarks>
public static class MineModelSource
{
    /// <summary>The real <c>.mdl</c> texture stem: native loads
    /// <c>textureName + "bmp"</c> = <c>fxminebmp</c> (file <c>fxminebmp.mdl</c>;
    /// <c>trekigc.cpp:1946</c>).</summary>
    public const string SpriteStem = "fxminebmp";

    /// <summary>Fallback field radius (units) when a mine's type radius cannot
    /// be resolved — a mid representative of PCore001's real <c>MineRadius</c>
    /// values (75–250). Used so an unresolvable type still renders a sane-sized
    /// cloud rather than disappearing.</summary>
    public const float DefaultRadius = 100f;

    /// <summary>The configured artwork root (ALLEGIANCE_ARTWORK_PATH, falling
    /// back to the standard dev location) — the same resolution the missile /
    /// probe sources use; see <see cref="AssetSources.ResolveRoot"/>.</summary>
    public static string ResolveArtworkDir() => AssetSources.ResolveRoot();

    /// <summary>Parse the real mine sprite's <c>.mdl</c> texture once (from the
    /// default desktop artwork source). Throws if the artwork path is
    /// unavailable — the texture is required for rendering.</summary>
    public static MdlTexture LoadTexture() => LoadTexture(AssetSources.Artwork);

    /// <summary>Parse the real mine sprite's <c>.mdl</c> texture once from
    /// <paramref name="assets"/>. Throws if the asset is unavailable — the
    /// texture is required for rendering.</summary>
    public static MdlTexture LoadTexture(IAssetSource assets) =>
        MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(assets, SpriteStem + ".mdl"));

    /// <summary>Resolve the mine field radius for a mine of the given type, from
    /// the static core's <c>MineTypes</c> (<c>DataMineTypeIGC.MineRadius</c>).
    /// Null-safe: a missing table or unknown id yields <see cref="DefaultRadius"/>,
    /// matching the renderer's degrade-not-crash rule.</summary>
    public static float ResolveRadius(short minetypeId, IReadOnlyDictionary<short, DataMineTypeIGC>? mineTypes)
    {
        if (mineTypes is not null && mineTypes.TryGetValue(minetypeId, out var mt))
            return mt.MineRadius > 0f ? mt.MineRadius : DefaultRadius;
        return DefaultRadius;
    }
}
