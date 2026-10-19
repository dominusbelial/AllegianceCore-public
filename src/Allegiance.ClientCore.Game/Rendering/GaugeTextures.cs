using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Resolves which real original-game <c>.mdl</c> gauge artwork backs each
/// <see cref="GaugeKind"/>, and where that artwork lives. Mirrors the port's
/// established runtime-asset convention (see
/// <see cref="MissileModelSource.ResolveArtworkDir"/>): real <c>.mdl</c> files
/// are read straight from the <c>ALLEGIANCE_ARTWORK_PATH</c> tree at startup,
/// not copied into Content and compiled.
/// </summary>
public static class GaugeTextureSource
{
    /// <summary>The root-level <c>.mdl</c> gauge stem for each kind — the
    /// authentic original-game segmented-arc gauge art (decoded via
    /// <see cref="MdlTextureDecoder"/>), chosen over the community CortUI skins
    /// because these ARE the retro "Casio-watch" segmented meters the pilot
    /// asked for.</summary>
    public static string MdlStemFor(GaugeKind kind) => kind switch
    {
        GaugeKind.Hull => "HullGaugebmp",
        GaugeKind.Shield => "ShieldGaugebmp",
        GaugeKind.Energy => "EnergyGaugebmp",
        GaugeKind.Fuel => "FuelGaugebmp",
        GaugeKind.Ammo => "AmmoGaugebmp",
        GaugeKind.Thrust => "ThrustGaugebmp",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Short pure-ASCII on-screen label for a gauge (HUD font bakes
    /// only 32..126).</summary>
    public static string LabelFor(GaugeKind kind) => kind switch
    {
        GaugeKind.Hull => "HULL",
        GaugeKind.Shield => "SHLD",
        GaugeKind.Energy => "ENGY",
        GaugeKind.Fuel => "FUEL",
        GaugeKind.Ammo => "AMMO",
        GaugeKind.Thrust => "THR",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The configured artwork root (same convention as the missile
    /// model loader) where the root-level <c>*Gaugebmp.mdl</c> files live —
    /// see <see cref="Assets.AssetSources.ResolveRoot"/>.</summary>
    public static string ResolveArtworkDir() => Assets.AssetSources.ResolveRoot();

    /// <summary>Full path to a gauge's real <c>.mdl</c> file (diagnostic helper).</summary>
    public static string PathFor(GaugeKind kind) =>
        System.IO.Path.Combine(ResolveArtworkDir(), MdlStemFor(kind) + ".mdl");
}

/// <summary>
/// The six real gauge <see cref="Texture2D"/>s decoded from the original
/// <c>.mdl</c> artwork, plus their premultiplied transparent-surround form.
/// Built in <c>Game1.LoadContent</c> (needs a <see cref="GraphicsDevice"/>);
/// handed to <see cref="HudRenderer"/> which draws them with a bottom-up
/// clipped reveal driven by live telemetry.
/// </summary>
public sealed class GaugeTextures
{
    private readonly Dictionary<GaugeKind, Texture2D> _byKind = [];
    private readonly Dictionary<GaugeKind, Texture2D> _damage = [];

    /// <summary>True when every one of the six gauges decoded and loaded.</summary>
    public bool AllLoaded => _byKind.Count == 6;

    /// <summary>The loaded texture for a kind, or null if that file failed to
    /// decode (the renderer falls back to procedural bars for a missing kind).</summary>
    public Texture2D? For(GaugeKind kind) =>
        _byKind.TryGetValue(kind, out var tex) ? tex : null;

    /// <summary>Native red damage artwork (dialog.mdl:2416-2506). Fall back
    /// to the normal gauge when a reduced artwork pack lacks the variant.</summary>
    public Texture2D? For(GaugeKind kind, bool damageFlash) =>
        damageFlash && _damage.TryGetValue(kind, out var tex) ? tex : For(kind);

    /// <summary>Count of gauges successfully loaded (diagnostics).</summary>
    public int LoadedCount => _byKind.Count;

    /// <summary>Back-compat convenience: load from an on-disk artwork directory.</summary>
    public static GaugeTextures Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    /// <summary>
    /// Load all six gauges from <paramref name="assets"/>. Missing/undecodable
    /// files are skipped with a logged warning — the HUD degrades gracefully to
    /// procedural bars rather than refusing to start, mirroring the port's
    /// "graceful degradation" audio/client convention.
    /// </summary>
    public static GaugeTextures Load(GraphicsDevice gd, IAssetSource assets)
    {
        var result = new GaugeTextures();
        foreach (GaugeKind kind in Enum.GetValues<GaugeKind>())
        {
            Load(kind, GaugeTextureSource.MdlStemFor(kind), false);
        }
        Load(GaugeKind.Hull, "hullgaugeredbmp", true);
        Load(GaugeKind.Shield, "shieldgaugeredbmp", true);
        return result;

        void Load(GaugeKind kind, string stem, bool damage)
        {
            string rel = stem + ".mdl";
            try
            {
                if (!assets.Exists(rel))
                {
                    Console.WriteLine($"[monogame] gauge '{kind}' missing ({rel}) — procedural fallback");
                    return;
                }
                var tex = MdlTextureDecoder.Decode(
                    AssetSources.ReadAllBytes(assets, rel),
                    // 2-D HUD art is blitted screen-space by native (Context::DrawImage,
                    // upper-left origin, no flip), so keep file row 0 at the top.
                    flipVertical: false);
                byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);
                var surface = new Texture2D(gd, tex.Width, tex.Height);
                var colors = new Color[tex.Width * tex.Height];
                for (int i = 0; i < colors.Length; i++)
                {
                    int b = i * 4;
                    colors[i] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
                }
                surface.SetData(colors);
                (damage ? result._damage : result._byKind)[kind] = surface;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] gauge '{kind}' failed to decode: {ex.Message} — procedural fallback");
            }
        }
    }
}
