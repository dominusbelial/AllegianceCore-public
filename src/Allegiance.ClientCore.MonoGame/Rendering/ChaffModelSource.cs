using System;
using System.Collections.Generic;
using System.IO;
using Allegiance.Core;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P292 — resolves the real visual for a deployed chaff decoy. The census (this
/// session) confirmed the native visual is <b>not a mesh</b>: every chaff type
/// in the corpus (625 records across all static cores) has an empty
/// <c>ModelName</c>, and native <c>CchaffIGC::Initialize</c> loads the chaff
/// with a single <c>LoadDecal(m_pChaffTypeData->textureName, NULL, Color(1,1,1,1),
/// false, 1.0f, ...)</c> (<c>src/Igc/chaffigc.cpp:42-46</c>) — i.e. one
/// camera-facing <b>decal</b> of the chaff type's texture, not a solid model and
/// not a decal cloud (that is the <em>mine</em>'s <c>MineFieldGeo</c>; chaff is
/// a single sprite). The client's <c>LoadDecal</c> resolves the asset as
/// <c>textureName + "bmp"</c> (<c>src/WinTrek/trekigc.cpp:1786</c>). The two
/// real textures across the corpus are <c>fxmine</c> (573 chaff records) and
/// <c>cc_cmtex</c> (52) → assets <c>fxminebmp.mdl</c> / <c>cc_cmtexbmp.mdl</c>,
/// both present in Artwork and parsed at runtime with
/// <see cref="MdlTextureDecoder"/> — the same runtime-native-asset pattern as
/// <see cref="MineModelSource"/>.
/// </summary>
/// <remarks>
/// <para><b>What the wire carries vs. what must be resolved.</b> The
/// CREATE_CHAFF(109) codec carries only <c>p0</c>, <c>v0</c>, <c>time0</c> and
/// the chaff type id (<c>etid</c>) — no texture, radius or lifespan (those live
/// in the static core). This source resolves them from the core's
/// <c>ChaffTypes</c> table (<c>DataChaffTypeIGC.TextureName</c> /
/// <c>.Radius</c> / <c>.Lifespan</c>), with the documented
/// degrade-not-crash fallbacks (see <see cref="Allegiance.ClientCore.State.ChaffEntity"/>).
/// The demo PCore001 defines 6 chaff types, all <c>fxmine</c>, radius 5,
/// lifespan 2s.</para>
/// </remarks>
public static class ChaffModelSource
{
    /// <summary>The real <c>.mdl</c> texture stems a chaff can carry, resolved
    /// from the census: native loads <c>textureName + "bmp"</c> =
    /// <c>fxminebmp</c> or <c>cc_cmtexbmp</c>. Both assets exist in Artwork.</summary>
    public static readonly IReadOnlyList<string> RealStems = new[] { "fxminebmp", "cc_cmtexbmp" };

    /// <summary>Fallback texture stem when a chaff's type cannot be resolved —
    /// <c>fxmine</c>, the dominant real chaff texture (573/625 records) and the
    /// one every PCore001 chaff uses.</summary>
    public const string DefaultStem = "fxminebmp";

    /// <summary>The configured artwork root (ALLEGIANCE_ARTWORK_PATH, falling
    /// back to the standard dev location) — the same resolution the missile /
    /// probe / mine sources use.</summary>
    public static string ResolveArtworkDir() => MissileModelSource.ResolveArtworkDir();

    /// <summary>Parse a real chaff sprite's <c>.mdl</c> texture. Throws if the
    /// artwork path is unavailable or the stem is not a real asset — the texture
    /// is required for rendering.</summary>
    public static MdlTexture LoadTexture(string stem) =>
        MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(ResolveArtworkDir(), stem + ".mdl")));

    /// <summary>Resolve the texture stem for a chaff of the given type, from the
    /// static core's <c>ChaffTypes</c> (<c>DataChaffTypeIGC.TextureName</c> +
    /// <c>"bmp"</c>, the native <c>trekigc.cpp:1786</c> asset resolution). Falls
    /// back to <see cref="DefaultStem"/> when the type is unknown, the table
    /// absent, or the resolved stem is not a real asset.</summary>
    public static string ResolveStem(short etid, IReadOnlyDictionary<short, DataChaffTypeIGC>? chaffTypes)
    {
        if (chaffTypes is not null && chaffTypes.TryGetValue(etid, out var ct) && ct.TextureName is { Length: > 0 })
        {
            var stem = ct.TextureName + "bmp";
            foreach (var real in RealStems)
                if (real.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    return real;   // canonical real stem
        }
        return DefaultStem;
    }

    /// <summary>Resolve the chaff type's lifespan (seconds) from the static core,
    /// falling back to <see cref="Allegiance.ClientCore.State.ChaffEntity.DefaultLifespan"/>
    /// when the type is unknown or the table absent.</summary>
    public static float ResolveLifespan(short etid, IReadOnlyDictionary<short, DataChaffTypeIGC>? chaffTypes)
    {
        if (chaffTypes is not null && chaffTypes.TryGetValue(etid, out var ct) && ct.Lifespan > 0f)
            return ct.Lifespan;
        return Allegiance.ClientCore.State.ChaffEntity.DefaultLifespan;
    }

    /// <summary>Resolve the chaff type's radius ceiling (units) from the static
    /// core, falling back to <see cref="Allegiance.ClientCore.State.ChaffEntity.DefaultRadiusCeiling"/>
    /// when the type is unknown or the table absent.</summary>
    public static float ResolveRadiusCeiling(short etid, IReadOnlyDictionary<short, DataChaffTypeIGC>? chaffTypes)
    {
        if (chaffTypes is not null && chaffTypes.TryGetValue(etid, out var ct) && ct.Radius > 0f)
            return ct.Radius;
        return Allegiance.ClientCore.State.ChaffEntity.DefaultRadiusCeiling;
    }
}
