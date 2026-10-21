using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Resolves which real original-game <c>.mdl</c> artwork backs each
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
    /// <see cref="MdlTextureDecoder"/>).</summary>
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
    /// only 32..126). HUD-6: labels now match the native centre-HUD strings
    /// (dialog.mdl:2173 THRUST / 2226 FUEL / 2278 AMMO / 2331 ENERGY /
    /// 2404 ARMOR / 2474 SHIELD).</summary>
    public static string LabelFor(GaugeKind kind) => kind switch
    {
        GaugeKind.Hull => "ARMOR",
        GaugeKind.Shield => "SHIELD",
        GaugeKind.Energy => "ENERGY",
        GaugeKind.Fuel => "FUEL",
        GaugeKind.Ammo => "AMMO",
        GaugeKind.Thrust => "THRUST",
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
/// HUD-14 — the real native classic-gauge layer <see cref="Texture2D"/>s,
/// decoded from the original <c>.mdl</c> artwork. Native composes each gauge
/// as a <c>GroupImage</c> of separate back / fill (revealed through a
/// <c>GaugeImageRect</c>) / spine / frame layers (dialog.mdl:2173-2516); this
/// type loads each layer under the stems in <see cref="ClassicGaugeTable"/>.
/// Built in <c>Game1.LoadContent</c> (needs a <see cref="GraphicsDevice"/>);
/// handed to <see cref="HudRenderer"/> which draws them with the native
/// reveal. Any missing layer degrades gracefully (the renderer falls back to a
/// procedural bar for a fully-missing gauge).
/// </summary>
public sealed class GaugeTextures
{
    private readonly Dictionary<GaugeKind, Texture2D> _back = [];
    private readonly Dictionary<GaugeKind, Texture2D> _fill = [];
    private readonly Dictionary<GaugeKind, Texture2D> _red = [];
    private readonly Dictionary<GaugeKind, Texture2D> _spine = [];
    private readonly Dictionary<GaugeKind, Texture2D> _frame = [];

    /// <summary>The native faint back layer for a kind, or null if missing.</summary>
    public Texture2D? Back(GaugeKind kind) => _back.TryGetValue(kind, out var t) ? t : null;
    /// <summary>The native fill layer (green for hull/shield, bright arc for
    /// the others) revealed through the source rect, or null if missing.</summary>
    public Texture2D? Fill(GaugeKind kind) => _fill.TryGetValue(kind, out var t) ? t : null;
    /// <summary>The native red damage-flash fill variant (hull/shield only),
    /// or null if missing or not applicable.</summary>
    public Texture2D? Red(GaugeKind kind) => _red.TryGetValue(kind, out var t) ? t : null;
    /// <summary>The native spine art (thrust/fuel/ammo/energy), or null.</summary>
    public Texture2D? Spine(GaugeKind kind) => _spine.TryGetValue(kind, out var t) ? t : null;
    /// <summary>The native CenterHudLeft/Right frame (hull/shield), or null.</summary>
    public Texture2D? Frame(GaugeKind kind) => _frame.TryGetValue(kind, out var t) ? t : null;

    /// <summary>True when every one of the six gauges decoded its FILL layer
    /// (without which a gauge has nothing to reveal).</summary>
    public bool AllLoaded
    {
        get
        {
            // HUD-14b — the kind list is cached once (this is called every frame
            // from HudRenderer, so Enum.GetValues each call would allocate).
            foreach (var kind in AllKinds)
                if (!_fill.ContainsKey(kind)) return false;
            return true;
        }
    }

    /// <summary>Cached once — never enumerate the enum per frame.</summary>
    private static readonly GaugeKind[] AllKinds = Enum.GetValues<GaugeKind>();

    /// <summary>The number of fill layers successfully loaded (diagnostics).</summary>
    public int LoadedCount => _fill.Count;

    /// <summary>Back-compat convenience: load from an on-disk artwork directory.</summary>
    public static GaugeTextures Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    /// <summary>
    /// Load every native gauge layer from <paramref name="assets"/> per
    /// <see cref="ClassicGaugeTable"/> (the art stems and citations live
    /// there). Missing/undecodable files are skipped with a logged warning —
    /// the HUD degrades gracefully rather than refusing to start.
    /// </summary>
    public static GaugeTextures Load(GraphicsDevice gd, IAssetSource assets)
    {
        var result = new GaugeTextures();
        foreach (var spec in ClassicGaugeTable.All)
        {
            Load(result._back, spec.Kind, spec.BackStem, assets, gd);
            Load(result._fill, spec.Kind, spec.FillStem, assets, gd);
            if (spec.RedStem is { } red) Load(result._red, spec.Kind, red, assets, gd);
            if (spec.SpineStem is { } spine) Load(result._spine, spec.Kind, spine, assets, gd);
            if (spec.FrameStem is { } frame) Load(result._frame, spec.Kind, frame, assets, gd);
        }
        return result;

        static void Load(Dictionary<GaugeKind, Texture2D> into, GaugeKind kind, string stem,
            IAssetSource assets, GraphicsDevice gd)
        {
            string rel = stem + ".mdl";
            try
            {
                if (!assets.Exists(rel))
                {
                    Console.WriteLine($"[monogame] gauge '{kind}' layer '{stem}' missing ({rel}) — fallback");
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
                into[kind] = surface;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] gauge '{kind}' layer '{stem}' failed to decode: {ex.Message} — fallback");
            }
        }
    }
}
