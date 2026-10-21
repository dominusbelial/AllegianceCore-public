using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-6 — the real original-game bitmaps behind the own-ship centre-HUD status
/// icons. Native <c>Artwork/dialog.mdl:2527-2594</c> (the classic
/// <c>DefaultCenterHudOn</c> group) binds each icon to a bitmap:
/// <list type="bullet">
///   <item>Detection eye → <c>radonbmp</c> (dialog.mdl:2527).</item>
///   <item>Cloak active → <c>concloakbmp</c> (dialog.mdl:2542).</item>
///   <item>Kill bonus → <c>killcounterbmp</c> (dialog.mdl:2565).</item>
///   <item>Carried artifact → <c>captureflaglbmp</c> (dialog.mdl:2569).</item>
///   <item>Carried flag → <c>captureflagrbmp</c> (dialog.mdl:2583).</item>
///   <item>Autopilot → <c>CortUI/common/conautopilot.png</c> (dialog.mdl:2151).</item>
///   <item>Vector lock → <c>CortUI/common/convectorlock.png</c> (dialog.mdl:2140).</item>
/// </list>
/// MDL assets decode through the established <see cref="MdlTextureDecoder"/>
/// pipeline (same as <see cref="GaugeTextures"/>/<see cref="LockTextures"/>);
/// the two CortUI PNGs (the art the native dialog actually references for
/// autopilot/vector lock) load through MonoGame's PNG reader. Missing assets
/// degrade gracefully to the classic renderer's text/procedural fallback —
/// never refuse to start.
/// </summary>
public sealed class StatusTextures : IDisposable
{
    /// <summary>Real radonbmp.mdl detection eye, or null.</summary>
    public Texture2D? Eye { get; private set; }

    /// <summary>Real concloakbmp.mdl cloak icon, or null.</summary>
    public Texture2D? Cloak { get; private set; }

    /// <summary>Real killcounterbmp.mdl kill-bonus counter, or null.</summary>
    public Texture2D? KillCounter { get; private set; }

    /// <summary>Real captureflagrbmp.mdl carried-flag icon, or null.</summary>
    public Texture2D? Flag { get; private set; }

    /// <summary>Real captureflaglbmp.mdl carried-artifact icon, or null.</summary>
    public Texture2D? Artifact { get; private set; }

    /// <summary>Real CortUI/common/conautopilot.png autopilot icon, or null.</summary>
    public Texture2D? Autopilot { get; private set; }

    /// <summary>Real CortUI/common/convectorlock.png vector-lock icon, or null.</summary>
    public Texture2D? VectorLock { get; private set; }

    // HUD-12 — the radar overlay bracket surfaces native draws with
    // DrawExpandedBlip (radarimage.cpp:219-230,343-357; artwork names in
    // WinTrek/artwork.h:73-76). Each is a flat 2-D bitmap, decoded exactly like
    // the status icons above.
    /// <summary>Real <c>hasflagbmp.mdl</c> carried-flag bracket art, or null.</summary>
    public Texture2D? RadarFlag { get; private set; }
    /// <summary>Real <c>hasartifactbmp.mdl</c> carried-artifact bracket art, or null.</summary>
    public Texture2D? RadarArtifact { get; private set; }
    /// <summary>Real <c>subjectbmp.mdl</c> command-subject bracket art, or null.</summary>
    public Texture2D? RadarSubject { get; private set; }
    /// <summary>Real <c>ripiconbmp.mdl</c> (PNG-backed) ripcord bracket art, or null.</summary>
    public Texture2D? RadarRipcord { get; private set; }
    /// <summary>Real <c>lastfiredbmp.mdl</c> threat bracket art, or null.</summary>
    public Texture2D? RadarThreat { get; private set; }

    // HUD-12 — the two native console badges (dialog.mdl:5056,5107).
    /// <summary>Real <c>livesbmp.mdl</c> lives badge, or null.</summary>
    public Texture2D? LivesBadge { get; private set; }
    /// <summary>Real <c>hudKillsbmp.mdl</c> kills badge, or null.</summary>
    public Texture2D? KillsBadge { get; private set; }

    /// <summary>Every status icon decoded successfully.</summary>
    public bool AllLoaded =>
        Eye is not null && Cloak is not null && KillCounter is not null &&
        Flag is not null && Artifact is not null &&
        Autopilot is not null && VectorLock is not null;

    private bool _disposed;

    /// <summary>Convenience overload reading from an on-disk artwork directory.</summary>
    public static StatusTextures Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    /// <summary>Decode the seven status icons from <paramref name="assets"/>.
    /// Each asset is optional and degrades gracefully.</summary>
    public static StatusTextures Load(GraphicsDevice gd, IAssetSource assets)
    {
        var result = new StatusTextures();
        result.Eye = TryDecodeMdl(gd, assets, "radonbmp");
        result.Cloak = TryDecodeMdl(gd, assets, "concloakbmp");
        result.KillCounter = TryDecodeMdl(gd, assets, "killcounterbmp");
        result.Flag = TryDecodeMdl(gd, assets, "captureflagRbmp");
        result.Artifact = TryDecodeMdl(gd, assets, "captureflagLbmp");
        result.Autopilot = TryDecodePng(gd, assets, "CortUI/common/conautopilot.png");
        result.VectorLock = TryDecodePng(gd, assets, "CortUI/common/convectorlock.png");
        // HUD-12 — the radar bracket + console badge art (flat 2-D bitmaps).
        result.RadarFlag = TryDecodeMdl(gd, assets, "hasflagbmp");
        result.RadarArtifact = TryDecodeMdl(gd, assets, "hasartifactbmp");
        result.RadarSubject = TryDecodeMdl(gd, assets, "subjectbmp");
        // ripiconbmp.mdl is an ImportImageFromFile("ripicon.png", true) script,
        // not a binary RGB565 surface; load the PNG it names directly (the native
        // AWF_FLIGHT_RIP_ICON stem is still the source of truth, artwork.h:76).
        result.RadarRipcord = TryDecodePng(gd, assets, "ripicon.png");
        result.RadarThreat = TryDecodeMdl(gd, assets, "lastfiredbmp");
        result.LivesBadge = TryDecodeMdl(gd, assets, "livesbmp");
        result.KillsBadge = TryDecodeMdl(gd, assets, "hudKillsbmp");
        return result;
    }

    private static Texture2D? TryDecodeMdl(GraphicsDevice gd, IAssetSource assets, string stem)
    {
        string rel = stem + ".mdl";
        try
        {
            if (!assets.Exists(rel))
            {
                Console.WriteLine($"[monogame] status art '{stem}' missing ({rel}) — text fallback");
                return null;
            }
            var tex = MdlTextureDecoder.Decode(
                AssetSources.ReadAllBytes(assets, rel),
                // 2-D HUD art is blitted screen-space by native (Context::DrawImage,
                // upper-left origin, no flip), so keep file row 0 at the top.
                flipVertical: false);
            byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(tex);
            var colors = new Microsoft.Xna.Framework.Color[tex.Width * tex.Height];
            for (int c = 0; c < colors.Length; c++)
            {
                int b = c * 4;
                colors[c] = new Microsoft.Xna.Framework.Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
            }
            var surface = new Texture2D(gd, tex.Width, tex.Height);
            surface.SetData(colors);
            Console.WriteLine($"[monogame] status art '{stem}': {tex.Width}x{tex.Height} '{rel}'");
            return surface;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] status art '{stem}' failed to decode: {ex.Message} — text fallback");
            return null;
        }
    }

    private static Texture2D? TryDecodePng(GraphicsDevice gd, IAssetSource assets, string rel)
    {
        try
        {
            if (!assets.Exists(rel))
            {
                Console.WriteLine($"[monogame] status art '{rel}' missing — text fallback");
                return null;
            }
            using var stream = assets.OpenRead(rel);
            var surface = Texture2D.FromStream(gd, stream);
            Console.WriteLine($"[monogame] status art '{rel}': {surface.Width}x{surface.Height}");
            return surface;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] status art '{rel}' failed to decode: {ex.Message} — text fallback");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Eye?.Dispose(); Cloak?.Dispose(); KillCounter?.Dispose();
        Flag?.Dispose(); Artifact?.Dispose(); Autopilot?.Dispose(); VectorLock?.Dispose();
        RadarFlag?.Dispose(); RadarArtifact?.Dispose(); RadarSubject?.Dispose();
        RadarRipcord?.Dispose(); RadarThreat?.Dispose(); LivesBadge?.Dispose(); KillsBadge?.Dispose();
        Eye = Cloak = KillCounter = Flag = Artifact = Autopilot = VectorLock = null;
        RadarFlag = RadarArtifact = RadarSubject = RadarRipcord = RadarThreat = null;
        LivesBadge = KillsBadge = null;
    }
}
