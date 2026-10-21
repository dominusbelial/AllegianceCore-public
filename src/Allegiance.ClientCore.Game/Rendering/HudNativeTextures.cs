using System.Text;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-10 — the remaining native <c>Artwork/dialog.mdl</c> bitmaps used by the
/// chat composer, command bar, money panel and lifepod endurance gauge. The
/// loader accepts both physical encodings the original artwork ships:
/// a binary RGB565 <c>.mdl</c> surface (<see cref="MdlTextureDecoder"/>) and a
/// tiny <c>ImportImageFromFile("&lt;stem&gt;.png", …)</c> script
/// (<c>creditboxbmp.mdl</c> is a PNG script). Missing art degrades gracefully
/// (the renderers fall back to their existing text/procedural presentation).
/// </summary>
public sealed partial class HudNativeTextures : IDisposable
{
    [GeneratedRegex("ImportImageFromFile\\s*\\(\\s*\"([^\"]+)\"\\s*,\\s*(?:true|false)\\s*\\)")]
    private static partial Regex ImageImport();

    public Texture2D? ChatMouse { get; private set; }
    public Texture2D? ChatText { get; private set; }
    public Texture2D? ChatCommand { get; private set; }
    public Texture2D? ChatShell { get; private set; }
    public Texture2D? SoftGauge { get; private set; }
    public Texture2D? CreditBox { get; private set; }
    public Texture2D? CommandBackground { get; private set; }
    public Texture2D? RecipientsBar { get; private set; }
    public Texture2D? VerbsBar { get; private set; }
    public Texture2D? WingCombo { get; private set; }
    public IReadOnlyDictionary<short, Texture2D> VerbIcons => _verbIcons;

    // HUD-15 — the console order-line glyphs. Native LastOrderImage translates
    // ImportImage3D("comacceptbmp", yellowText) / ImportImage3D("comqueuedbmp",
    // Color(1,0,0)) by Point(-21,-13) beside each order line
    // (Artwork/dialog.mdl:1605-1640, :1641-1652).
    /// <summary>Real <c>comacceptbmp.mdl</c> accepted-order glyph, or null.</summary>
    public Texture2D? CommandAcceptedIcon { get; private set; }
    /// <summary>Real <c>comqueuedbmp.mdl</c> queued-order glyph, or null.</summary>
    public Texture2D? CommandQueuedIcon { get; private set; }

    // HUD-17 — the native friendly detection-eye art. Native loads
    // AWF_FLIGHT_EYE_ICON ("eyebmp", WinTrek/artwork.h:85) once
    // (radarimage.cpp:226) and draws it additively in the radar label's left
    // column (radarimage.cpp:1316-1317,1333-1352).
    /// <summary>Redar friendly detection eye <c>eyebmp</c>, or null when the art
    /// is missing. Decoded as a luminance-alpha mask so the 16×16 JPEG (which has
    /// no alpha channel) does not draw as a black square in the alpha-blended HUD
    /// batch — see <see cref="TryDecode"/>'s luminanceAlpha arm.</summary>
    public Texture2D? RadarEyeIcon { get; private set; }

    // HUD-15 — the native radar command-icon arrays
    // (WinTrek/radarimage.cpp:228-235, m_psurfaceAccepted/m_psurfaceQueued).
    private readonly Dictionary<short, Texture2D> _radarAccepted = new();
    private readonly Dictionary<short, Texture2D> _radarQueued = new();

    /// <summary>Decoded accepted/queued command radar icons keyed by native
    /// CommandID (1 = c_cidAttack … 9 = c_cidBuild), or null when that slot's
    /// artwork is missing (radarimage.cpp:1248-1253 picks accepted vs queued).</summary>
    public Texture2D? RadarCommandIcon(short commandId, bool accepted) =>
        accepted
            ? _radarAccepted.GetValueOrDefault(commandId)
            : _radarQueued.GetValueOrDefault(commandId);

    private readonly Dictionary<short, Texture2D> _verbIcons = new();
    private bool _disposed;

    public static HudNativeTextures Load(GraphicsDevice gd, string artworkDir) =>
        Load(gd, new DirectoryAssetSource(artworkDir));

    public static HudNativeTextures Load(GraphicsDevice gd, IAssetSource assets)
    {
        var t = new HudNativeTextures();
        t.ChatMouse = TryDecode(gd, assets, "chatmousebmp");
        t.ChatText = TryDecode(gd, assets, "chattextbmp");
        t.ChatCommand = TryDecode(gd, assets, "chatcommandbmp");
        t.ChatShell = TryDecode(gd, assets, "chatshellbmp");
        t.SoftGauge = TryDecode(gd, assets, "softgaugebmp");
        t.CreditBox = TryDecode(gd, assets, "creditboxbmp");
        t.CommandBackground = TryDecode(gd, assets, "commandbkgndbmp");
        t.RecipientsBar = TryDecode(gd, assets, "recipientsbmp");
        t.VerbsBar = TryDecode(gd, assets, "verbsbmp");
        t.WingCombo = TryDecode(gd, assets, "btnchangewing3bmp");
        // HUD-15 — the accepted/queued order glyphs (dialog.mdl:1605-1640).
        t.CommandAcceptedIcon = TryDecode(gd, assets, "comacceptbmp");
        t.CommandQueuedIcon = TryDecode(gd, assets, "comqueuedbmp");
        // HUD-17 — the radar friendly detection eye (WinTrek/artwork.h:85,
        // radarimage.cpp:226). Its artwork is eyebmp.mdl -> eye.png, a 16x16
        // 3-component JPEG, so it is decoded as a luminance-alpha mask: black
        // pixels contribute nothing (the visual half of native BlendModeAdd)
        // instead of painting an opaque black square under the HUD's normal
        // alpha blend.
        t.RadarEyeIcon = TryDecode(gd, assets, "eyebmp", luminanceAlpha: true);
        // HUD-15 — the radar command-icon arrays. Native CommandID order
        // c_cidAttack..c_cidBuild (Igc/common.cpp:41-52; the same 16x16 bitmaps
        // ComposingCommand's SwitchPane uses, dialog.mdl:122-141), loaded once
        // by the exact native loop (radarimage.cpp:228-235). The accepted
        // surfaces are shared with the command bar's verb icons instead of
        // decoding a second copy.
        for (short cid = RadarCommandIcons.FirstArtCommandId; cid < RadarCommandIcons.CommandIdCount; cid++)
        {
            if (RadarCommandIcons.ArtStem(cid, accepted: true) is { } accepted
                && TryDecode(gd, assets, accepted) is { } icon)
            {
                t._radarAccepted[cid] = icon;
                t._verbIcons[cid] = icon;
            }
            if (RadarCommandIcons.ArtStem(cid, accepted: false) is { } queued
                && TryDecode(gd, assets, queued) is { } queuedIcon)
                t._radarQueued[cid] = queuedIcon;
        }
        return t;
    }

    public Texture2D? ForState(NativeComposeState state) => state switch
    {
        NativeComposeState.MouseCommand => ChatMouse,
        NativeComposeState.Chat => ChatText,
        NativeComposeState.Command => ChatCommand,
        NativeComposeState.Shell => ChatShell,
        NativeComposeState.Quick => ChatMouse,
        _ => null,
    };

    private static Texture2D? TryDecode(GraphicsDevice gd, IAssetSource assets, string stem, bool luminanceAlpha = false)
    {
        string rel = stem + ".mdl";
        try
        {
            if (!assets.Exists(rel))
            {
                Console.WriteLine($"[monogame] hud-10 art '{stem}' missing ({rel}) — fallback");
                return null;
            }
            byte[] data = AssetSources.ReadAllBytes(assets, rel);
            if (data.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data)
                == MdlTextureDecoder.MdlMagic)
            {
                var decoded = MdlTextureDecoder.Decode(data, flipVertical: false);
                byte[] rgba = MdlTextureDecoder.ToPremultipliedRgba(decoded);
                var surface = new Texture2D(gd, decoded.Width, decoded.Height);
                var colors = new Color[decoded.Width * decoded.Height];
                for (int i = 0; i < colors.Length; i++)
                {
                    int b = i * 4;
                    colors[i] = new Color(rgba[b], rgba[b + 1], rgba[b + 2], rgba[b + 3]);
                }
                surface.SetData(colors);
                Console.WriteLine($"[monogame] hud-10 art '{stem}': {decoded.Width}x{decoded.Height} '{rel}'");
                return surface;
            }

            var match = ImageImport().Match(Encoding.UTF8.GetString(data));
            if (!match.Success)
                throw new InvalidDataException("unsupported image namespace");
            using var stream = assets.OpenRead(match.Groups[1].Value);
            using var source = Texture2D.FromStream(gd, stream);
            var pixels = new Color[source.Width * source.Height];
            source.GetData(pixels);
            // 2-D HUD art keeps PNG row 0 at the top.
            for (int i = 0; i < pixels.Length; i++)
            {
                float a = pixels[i].A / 255f;
                if (luminanceAlpha)
                {
                    // HUD-17 — native draws eyebmp with BlendModeAdd
                    // (radarimage.cpp:1316): texel RGB is ADDED to the
                    // destination, so the JPEG's black background contributes
                    // nothing. The HUD batch is premultiplied AlphaBlend
                    // (One, InvSrcAlpha); with alpha 0 that is src*tint + dst,
                    // i.e. exactly additive, keeping the art's own hue.
                    pixels[i] = new Color(pixels[i].R, pixels[i].G, pixels[i].B, (byte)0);
                }
                else
                {
                    pixels[i] = new Color((byte)(pixels[i].R * a), (byte)(pixels[i].G * a), (byte)(pixels[i].B * a), pixels[i].A);
                }
            }
            var png = new Texture2D(gd, source.Width, source.Height);
            png.SetData(pixels);
            Console.WriteLine($"[monogame] hud-10 art '{stem}': {source.Width}x{source.Height} '{match.Groups[1].Value}'");
            return png;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[monogame] hud-10 art '{stem}' failed: {ex.Message} — fallback");
            return null;
        }
    }

    /// <summary>HUD-17 — resolve an <c>ImportImageFromFile("&lt;file&gt;", …)</c>
    /// script and report the imported image's native pixel size WITHOUT a
    /// GraphicsDevice, so the artwork tree can be verified headlessly
    /// ("eyebmp resolves to a 16×16 texture"). The production loader uses the
    /// same script match and then <c>Texture2D.FromStream</c>, which decodes the
    /// identical container, so the reported size is the texture size.</summary>
    internal static ImportedImage? ResolveImportedImage(IAssetSource assets, string stem)
    {
        try
        {
            string rel = stem + ".mdl";
            if (!assets.Exists(rel)) return null;
            byte[] script = AssetSources.ReadAllBytes(assets, rel);
            if (script.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(script)
                == MdlTextureDecoder.MdlMagic)
                return null;                       // a binary .mdl, not an image import
            var match = ImageImport().Match(Encoding.UTF8.GetString(script));
            if (!match.Success) return null;
            string file = match.Groups[1].Value;
            if (!assets.Exists(file)) return null;
            byte[] image = AssetSources.ReadAllBytes(assets, file);
            return ProbeImageSize(image) is { } size ? new ImportedImage(file, size.Width, size.Height) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>HUD-17 — the imported image plus its container-declared pixel
    /// size (PNG IHDR or JPEG SOF). Pure; used by the headless art test.</summary>
    internal readonly record struct ImportedImage(string File, int Width, int Height);

    /// <summary>Read a PNG's IHDR or a JPEG's SOF dimensions from the encoded
    /// bytes without decoding pixels. Returns null for other containers.</summary>
    internal static (int Width, int Height)? ProbeImageSize(ReadOnlySpan<byte> data)
    {
        // PNG signature + IHDR: width/height are big-endian at offsets 16/20.
        if (data.Length >= 24 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.Slice(16)),
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.Slice(20)));
        // JPEG: walk markers to the first Start-Of-Frame (SOF0/1/2/3/5/6/7/9/10/11/13/14/15).
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 9 <= data.Length)
        {
            if (data[pos] != 0xFF) { pos++; continue; }
            byte marker = data[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { pos += 2; continue; }
            if (marker == 0xDA) return null;                 // scan started before any SOF: malformed
            int length = (data[pos + 2] << 8) | data[pos + 3];
            if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7
                or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
            {
                int height = (data[pos + 5] << 8) | data[pos + 6];
                int width = (data[pos + 7] << 8) | data[pos + 8];
                return (width, height);
            }
            if (length < 2) return null;
            pos += 2 + length;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in new[] { ChatMouse, ChatText, ChatCommand, ChatShell, SoftGauge, CreditBox,
                     CommandBackground, RecipientsBar, VerbsBar, WingCombo, CommandAcceptedIcon, CommandQueuedIcon,
                     RadarEyeIcon })
            t?.Dispose();
        // _verbIcons aliases the accepted radar icons, so dispose each distinct
        // texture once.
        foreach (var t in _radarAccepted.Values.Distinct()) t.Dispose();
        foreach (var t in _radarQueued.Values) t.Dispose();
        _verbIcons.Clear();
        _radarAccepted.Clear();
        _radarQueued.Clear();
        ChatMouse = ChatText = ChatCommand = ChatShell = SoftGauge = CreditBox = null;
        CommandBackground = RecipientsBar = VerbsBar = WingCombo = null;
        CommandAcceptedIcon = CommandQueuedIcon = null;
        RadarEyeIcon = null;
    }
}
