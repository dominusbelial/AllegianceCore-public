using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P578 — the real original-game <c>.mdl</c> bitmaps backing the eject/death
/// HUD feedback: the eject-pod display panel (<c>conejectpoddisplaybmp.mdl</c>,
/// the classic panel the native <c>EjectPodImage</c> uses, dialog.mdl:1926-1960)
/// and the death banner (<c>deathbannerbmp.mdl</c>, the ghost banner,
/// dialog.mdl:5438-5439). Each is decoded via the port's established
/// <see cref="MdlTextureDecoder"/> pipeline (same as <see cref="GaugeTextures"/>/
/// <see cref="LockTextures"/>). Missing/undecodable assets degrade gracefully to
/// the procedural fallbacks — never refuse to start.
/// </summary>
public sealed class FeedbackTextures : IDisposable
{
    /// <summary>Real conejectpoddisplaybmp.mdl, or null if unavailable
    /// (procedural pod-panel fallback).</summary>
    public Texture2D? EjectPodPanel { get; private set; }

    /// <summary>Real deathbannerbmp.mdl, or null if unavailable (procedural
    /// banner fallback).</summary>
    public Texture2D? DeathBanner { get; private set; }

    /// <summary>Real ejectpodbmp.mdl (the in-HUD eject icon), or null.</summary>
    public Texture2D? EjectPodIcon { get; private set; }

    private bool _disposed;

    /// <summary>Convenience overload reading from an on-disk artwork directory.</summary>
    public static FeedbackTextures Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    /// <summary>Decode the feedback bitmaps from <paramref name="assets"/>,
    /// keying each dark surround to transparent (same convention as the lock
    /// sprites). Each asset is optional.</summary>
    public static FeedbackTextures Load(GraphicsDevice gd, IAssetSource assets)
    {
        var result = new FeedbackTextures();
        result.EjectPodPanel = TryDecode(gd, assets, "conejectpoddisplaybmp");
        result.DeathBanner = TryDecode(gd, assets, "deathbannerbmp");
        result.EjectPodIcon = TryDecode(gd, assets, "ejectpodbmp");
        return result;
    }

    private static Texture2D? TryDecode(GraphicsDevice gd, IAssetSource assets, string stem)
    {
        string rel = stem + ".mdl";
        try
        {
            if (!assets.Exists(rel))
            {
                Console.WriteLine($"[monogame] feedback art '{stem}' missing ({rel}) — procedural fallback");
                return null;
            }
            var tex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(assets, rel));
            byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);
            var colors = new Color[tex.Width * tex.Height];
            for (int c = 0; c < colors.Length; c++)
            {
                int b = c * 4;
                colors[c] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
            }
            var surface = new Texture2D(gd, tex.Width, tex.Height);
            surface.SetData(colors);
            Console.WriteLine($"[monogame] feedback art '{stem}': {tex.Width}x{tex.Height} '{rel}'");
            return surface;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] feedback art '{stem}' failed to decode: {ex.Message} — procedural fallback");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        EjectPodPanel?.Dispose();
        DeathBanner?.Dispose();
        EjectPodIcon?.Dispose();
        EjectPodPanel = DeathBanner = EjectPodIcon = null;
    }
}
