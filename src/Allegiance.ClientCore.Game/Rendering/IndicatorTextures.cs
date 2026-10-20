using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Resolves the real original-game flight-indicator <c>.mdl</c> bitmaps named in
/// <c>WinTrek/artwork.h:78-84</c> and drawn by <c>WinTrek/indicator.cpp</c>:
/// the cockpit centre reticle (<c>directioninbmp</c>), the velocity-direction
/// marker (<c>directionbmp</c>), the aim hit-probability crosshair
/// (<c>centeroutbmp</c>) and the two ballistic-lead surfaces
/// (<c>leadinbmp</c>/<c>leadoutbmp</c>). Decoded through the same
/// <see cref="MdlTextureDecoder"/> pipeline as <see cref="GaugeTextures"/> and
/// <see cref="LockTextures"/>.
/// </summary>
public static class IndicatorTextureSource
{
    /// <summary>Cockpit centre reticle — native <c>directioninbmp</c>,
    /// <c>indicator.cpp:150</c> (<c>artwork.h:81</c>).</summary>
    public const string ReticleMdl = "directioninbmp";

    /// <summary>Velocity direction marker — native <c>directionbmp</c>,
    /// <c>indicator.cpp:145</c> (<c>artwork.h:82</c>).</summary>
    public const string VelocityMarkerMdl = "directionbmp";

    /// <summary>Aim hit-probability crosshair — native <c>centeroutbmp</c>,
    /// <c>indicator.cpp:362</c> (<c>artwork.h:84</c>).</summary>
    public const string CrosshairOutMdl = "centeroutbmp";

    /// <summary>In-range ballistic-lead surface — native <c>leadinbmp</c>,
    /// <c>indicator.cpp:328</c> (<c>artwork.h:78</c>).</summary>
    public const string LeadInMdl = "leadinbmp";

    /// <summary>Out-of-range ballistic-lead surface — native <c>leadoutbmp</c>,
    /// <c>indicator.cpp:328</c> (<c>artwork.h:79</c>).</summary>
    public const string LeadOutMdl = "leadoutbmp";

    /// <summary>The configured artwork root (same convention as the missile
    /// model loader / <see cref="GaugeTextureSource"/>).</summary>
    public static string ResolveArtworkDir() => Assets.AssetSources.ResolveRoot();
}

/// <summary>
/// The five real flight-indicator <see cref="Texture2D"/>s decoded from the
/// original <c>.mdl</c> artwork, in the transparent-surround form the HUD
/// renders (black surround keyed to alpha 0, exactly like
/// <see cref="GaugeTextures"/>/<see cref="LockTextures"/>). Built in
/// <c>Game1.LoadContent</c>; handed to <see cref="HudRenderer"/>. Missing or
/// undecodable assets degrade gracefully to the existing procedural fallbacks.
/// </summary>
public sealed class IndicatorTextures : IDisposable
{
    private Texture2D? _reticle;
    private Texture2D? _velocityMarker;
    private Texture2D? _crosshairOut;
    private Texture2D? _leadIn;
    private Texture2D? _leadOut;
    private bool _disposed;

    /// <summary>Native cockpit centre reticle (<c>directioninbmp</c>).</summary>
    public Texture2D? Reticle => _reticle;

    /// <summary>Native velocity direction marker (<c>directionbmp</c>).</summary>
    public Texture2D? VelocityMarker => _velocityMarker;

    /// <summary>Native aim hit-probability crosshair (<c>centeroutbmp</c>).</summary>
    public Texture2D? CrosshairOut => _crosshairOut;

    /// <summary>Native in-range ballistic-lead surface (<c>leadinbmp</c>).</summary>
    public Texture2D? LeadIn => _leadIn;

    /// <summary>Native out-of-range ballistic-lead surface (<c>leadoutbmp</c>).</summary>
    public Texture2D? LeadOut => _leadOut;

    /// <summary>Release every decoded texture.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _reticle?.Dispose();
        _velocityMarker?.Dispose();
        _crosshairOut?.Dispose();
        _leadIn?.Dispose();
        _leadOut?.Dispose();
        _reticle = _velocityMarker = _crosshairOut = _leadIn = _leadOut = null;
    }

    /// <summary>Back-compat convenience: load from an on-disk artwork directory.</summary>
    public static IndicatorTextures Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    /// <summary>
    /// Decode the five real indicator bitmaps from <paramref name="assets"/>.
    /// Missing/undecodable files are skipped with a logged warning — the HUD
    /// degrades gracefully to its procedural shapes rather than refusing to
    /// start, mirroring <see cref="GaugeTextures"/> and <see cref="LockTextures"/>.
    /// </summary>
    public static IndicatorTextures Load(GraphicsDevice gd, IAssetSource assets)
    {
        var result = new IndicatorTextures();
        Load(IndicatorTextureSource.ReticleMdl, t => result._reticle = t);
        Load(IndicatorTextureSource.VelocityMarkerMdl, t => result._velocityMarker = t);
        Load(IndicatorTextureSource.CrosshairOutMdl, t => result._crosshairOut = t);
        Load(IndicatorTextureSource.LeadInMdl, t => result._leadIn = t);
        Load(IndicatorTextureSource.LeadOutMdl, t => result._leadOut = t);
        return result;

        void Load(string stem, Action<Texture2D> store)
        {
            string rel = stem + ".mdl";
            try
            {
                if (!assets.Exists(rel))
                {
                    Console.WriteLine($"[monogame] indicator '{stem}' missing ({rel}) — procedural fallback");
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
                for (int c = 0; c < colors.Length; c++)
                {
                    int b = c * 4;
                    colors[c] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
                }
                surface.SetData(colors);
                store(surface);
                Console.WriteLine($"[monogame] indicator '{stem}': {tex.Width}x{tex.Height} '{rel}'");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[monogame] indicator '{stem}' failed to decode: {ex.Message} — procedural fallback");
            }
        }
    }
}
