using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-3 — the real original-game bottom target-pane bitmaps referenced by
/// <c>Artwork/dialog.mdl:4040-4242</c>: the two damage gauges, the in-range
/// icon, and the oval/word decoration. Same decode pipeline as
/// <see cref="GaugeTextures"/>/<see cref="LockTextures"/>; missing artwork
/// degrades to the procedural/text fallback rather than blocking flight.
/// </summary>
public sealed class TargetPaneTextures : IDisposable
{
    public const string HullMdl = "TargetHullGaugebmp";
    public const string ShieldMdl = "TargetShieldGaugebmp";
    public const string RangeMdl = "turretrangebmp";
    public const string OvalMdl = "TargetOvalbmp";
    public const string OvalColorMdl = "targetovalcolorbmp";
    public const string OvalWord1Mdl = "targetovalword1bmp";
    public const string OvalWord2Mdl = "targetovalword2bmp";
    // HUD-20 — native TargetExperienceOnImage's art, translated Point(25,-21)
    // inside a group at pane-local Point(280,110) (Artwork/dialog.mdl:4192-4206,
    // :4232). The file is Artwork/TargetExperiencebmp.mdl — capital T; Linux
    // asset lookup is case sensitive, so this literal must match exactly.
    public const string TargetExperienceMdl = "TargetExperiencebmp";

    // Native GaugeImageRect source rectangles inside the 128x128 gauge bitmaps
    // (Artwork/dialog.mdl:4123-4135).
    public static readonly Rectangle HullRect = new(44, 26, 83, 101);
    public static readonly Rectangle ShieldRect = new(45, 26, 84, 101);

    private Texture2D? _hull;
    private Texture2D? _shield;
    private Texture2D? _range;
    private Texture2D? _oval;
    private Texture2D? _ovalColor;
    private Texture2D? _ovalWord1;
    private Texture2D? _ovalWord2;
    private Texture2D? _targetExperience;
    private bool _disposed;

    public Texture2D? Hull => _hull;
    public Texture2D? Shield => _shield;
    public Texture2D? Range => _range;
    public Texture2D? Oval => _oval;
    public Texture2D? OvalColor => _ovalColor;
    public Texture2D? OvalWord1 => _ovalWord1;
    public Texture2D? OvalWord2 => _ovalWord2;
    public Texture2D? TargetExperience => _targetExperience;

    public bool AllGaugesLoaded => _hull is not null && _shield is not null;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hull?.Dispose(); _hull = null;
        _shield?.Dispose(); _shield = null;
        _range?.Dispose(); _range = null;
        _oval?.Dispose(); _oval = null;
        _ovalColor?.Dispose(); _ovalColor = null;
        _ovalWord1?.Dispose(); _ovalWord1 = null;
        _ovalWord2?.Dispose(); _ovalWord2 = null;
        _targetExperience?.Dispose(); _targetExperience = null;
    }

    /// <summary>Back-compat convenience: load from an on-disk artwork directory.</summary>
    public static TargetPaneTextures Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    /// <summary>
    /// Decode the native target-pane bitmaps. Missing/undecodable files are
    /// skipped with a logged warning (the pane falls back to text/procedural
    /// shapes) — the port's established graceful-degradation convention.
    /// </summary>
    public static TargetPaneTextures Load(GraphicsDevice gd, IAssetSource assets)
    {
        var result = new TargetPaneTextures();
        LoadOne(HullMdl, t => result._hull = t);
        LoadOne(ShieldMdl, t => result._shield = t);
        LoadOne(RangeMdl, t => result._range = t);
        LoadOne(OvalMdl, t => result._oval = t);
        LoadOne(OvalColorMdl, t => result._ovalColor = t);
        LoadOne(OvalWord1Mdl, t => result._ovalWord1 = t);
        LoadOne(OvalWord2Mdl, t => result._ovalWord2 = t);
        LoadOne(TargetExperienceMdl, t => result._targetExperience = t);
        return result;

        void LoadOne(string stem, Action<Texture2D> set)
        {
            string rel = stem + ".mdl";
            try
            {
                if (!assets.Exists(rel))
                {
                    Console.WriteLine($"[monogame] target-pane '{stem}' missing ({rel}) — text/procedural fallback");
                    return;
                }
                var decoded = MdlTextureDecoder.Decode(
                    AssetSources.ReadAllBytes(assets, rel),
                    // 2-D HUD art is blitted screen-space (upper-left origin, no flip).
                    flipVertical: false);
                byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(decoded);
                var surface = new Texture2D(gd, decoded.Width, decoded.Height);
                var colors = new Color[decoded.Width * decoded.Height];
                for (int i = 0; i < colors.Length; i++)
                {
                    int b = i * 4;
                    colors[i] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
                }
                surface.SetData(colors);
                set(surface);
                Console.WriteLine($"[monogame] target-pane '{stem}': {decoded.Width}x{decoded.Height} '{rel}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] target-pane '{stem}' failed to decode: {ex.Message} — text/procedural fallback");
            }
        }
    }
}
