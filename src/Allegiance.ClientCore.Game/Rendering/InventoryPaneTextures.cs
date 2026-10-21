using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-7 — the native classic inventory-pane artwork: the CortUI classic
/// top/line/bottom backgrounds (<c>dialog.mdl:1757-1790</c>), the weapon /
/// equipment / turret icons (<c>invweaponiconbmp.mdl</c> etc.) and the native
/// mounting/arming bar fills (<c>mountingbarbmp.mdl</c> / <c>armingbarbmp.mdl</c>).
/// HUD-15 adds the real mounting-bar track (<c>mountingbarbkgndbmp</c>,
/// <c>Artwork/invdefault.mdl:43-52</c>).
/// Missing or undecodable files degrade to the procedural fallback, matching
/// the port's graceful-degradation convention.
/// </summary>
public sealed class InventoryPaneTextures
{
    private Texture2D? _top, _line, _bottom;
    private Texture2D? _weaponIcon, _equipmentIcon, _turretIcon;
    private Texture2D? _mountingBar, _armingBar;
    private Texture2D? _mountingBarBackground;
    private bool _disposed;

    public Texture2D? Top => _top;
    public Texture2D? Line => _line;
    public Texture2D? Bottom => _bottom;
    public Texture2D? WeaponIcon => _weaponIcon;
    public Texture2D? EquipmentIcon => _equipmentIcon;
    public Texture2D? TurretIcon => _turretIcon;
    public Texture2D? MountingBar => _mountingBar;
    public Texture2D? ArmingBar => _armingBar;

    /// <summary>Real <c>mountingbarbkgndbmp.mdl</c> (64×14) mounting-bar track —
    /// the word "Mounting" behind the fill (native <c>MountingBar =
    /// ImagePane(ImportImage("mountingbarbkgndbmp", true), [GaugePane…])</c>,
    /// <c>Artwork/invdefault.mdl:43-52</c>; the fill is the native classic
    /// <c>CortUI/classic/mountingbar.png</c>, which is the same art as
    /// <c>mountingbarbmp.mdl</c>). Null keeps the port's dark procedural track.</summary>
    public Texture2D? MountingBarBackground => _mountingBarBackground;

    /// <summary>True when the three pane backgrounds all loaded (the renderer
    /// then uses the real classic skin; otherwise it draws the procedural panel).</summary>
    public bool AllLoaded => _top is not null && _line is not null && _bottom is not null;

    private InventoryPaneTextures() { }

    public static InventoryPaneTextures Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    public static InventoryPaneTextures Load(GraphicsDevice gd, IAssetSource assets)
    {
        var result = new InventoryPaneTextures();
        try { result._top = LoadPng(gd, assets, "CortUI/classic/inventorytop.png"); } catch (Exception e) { Warn("top", e); }
        try { result._line = LoadPng(gd, assets, "CortUI/classic/inventorylinebkgnd.png"); } catch (Exception e) { Warn("line", e); }
        try { result._bottom = LoadPng(gd, assets, "CortUI/classic/inventorybottom.png"); } catch (Exception e) { Warn("bottom", e); }
        result._weaponIcon = LoadMdl(gd, assets, "invweaponiconbmp");
        result._equipmentIcon = LoadMdl(gd, assets, "invequipmenticonbmp");
        result._turretIcon = LoadMdl(gd, assets, "invturreticonbmp");
        // HUD-15 — the bar FILLS are opaque surfaces: their surround is the
        // native dark red (123,0,0), not black, so the luminance key that makes
        // the gauge art's black surround transparent would erase the whole bar
        // (relative luminance 36 <= the key threshold 40) and only the white
        // "Mounting" letters would survive. Keep the fill's pixels opaque and
        // let the GaugePane-style left reveal cover the track; the TRACK keeps
        // the keyed decode because its surround really is black.
        result._mountingBar = LoadMdlOpaque(gd, assets, "mountingbarbmp");
        result._armingBar = LoadMdlOpaque(gd, assets, "armingbarbmp");
        result._mountingBarBackground = LoadMdl(gd, assets, "mountingbarbkgndbmp");
        return result;
    }

    private static Texture2D? LoadPng(GraphicsDevice gd, IAssetSource assets, string rel)
    {
        if (!assets.Exists(rel)) return null;
        using var s = assets.OpenRead(rel);
        var tex = Texture2D.FromStream(gd, s);
        var data = new Color[tex.Width * tex.Height];
        tex.GetData(data);
        // Premultiply straight-alpha PNG for BlendState.AlphaBlend (the same
        // recipe as DistantObjectRenderer.LoadPremultipliedPng).
        for (int i = 0; i < data.Length; i++)
        {
            float a = data[i].A / 255f;
            data[i] = new Color((byte)(data[i].R * a), (byte)(data[i].G * a), (byte)(data[i].B * a), data[i].A);
        }
        tex.SetData(data);
        return tex;
    }

    /// <summary>Decode an <c>.mdl</c> surface as an opaque texture (no
    /// near-black key): the native mounting/arming bar fills own their dark red
    /// surround, which the keyed decode would drop.</summary>
    private static Texture2D? LoadMdlOpaque(GraphicsDevice gd, IAssetSource assets, string stem)
    {
        string rel = stem + ".mdl";
        if (!assets.Exists(rel)) return null;
        try
        {
            var tex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(assets, rel), flipVertical: false);
            var surface = new Texture2D(gd, tex.Width, tex.Height);
            var colors = new Color[tex.Width * tex.Height];
            for (int i = 0; i < colors.Length; i++)
            {
                int b = i * 4;
                colors[i] = new Color(tex.Rgba[b], tex.Rgba[b + 1], tex.Rgba[b + 2], (byte)255);
            }
            surface.SetData(colors);
            return surface;
        }
        catch (Exception e)
        {
            Warn(stem, e);
            return null;
        }
    }

    private static Texture2D? LoadMdl(GraphicsDevice gd, IAssetSource assets, string stem)
    {
        string rel = stem + ".mdl";
        if (!assets.Exists(rel)) return null;
        try
        {
            var tex = MdlTextureDecoder.Decode(AssetSources.ReadAllBytes(assets, rel), flipVertical: false);
            byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);
            var surface = new Texture2D(gd, tex.Width, tex.Height);
            var colors = new Color[tex.Width * tex.Height];
            for (int i = 0; i < colors.Length; i++)
            {
                int b = i * 4;
                colors[i] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
            }
            surface.SetData(colors);
            return surface;
        }
        catch (Exception e)
        {
            Warn(stem, e);
            return null;
        }
    }

    private static void Warn(string name, Exception e) =>
        Console.WriteLine($"[monogame] inventory-pane art '{name}' unavailable: {e.Message}");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _top?.Dispose(); _line?.Dispose(); _bottom?.Dispose();
        _weaponIcon?.Dispose(); _equipmentIcon?.Dispose(); _turretIcon?.Dispose();
        _mountingBar?.Dispose(); _armingBar?.Dispose();
        _mountingBarBackground?.Dispose();
    }
}
