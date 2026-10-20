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
        // Native CommandID order c_cidAttack..c_cidBuild (Igc/common.cpp:41-52),
        // each 16x16 in ComposingCommand's SwitchPane (dialog.mdl:122-141).
        string[] icons = ["ackillbmp", "accptbmp", "acdefendbmp", "acpickupbmp", "acgotobmp",
            "acrepairbmp", "acjoinbmp", "acminebmp", "acbuildbmp"];
        for (short i = 0; i < icons.Length; i++)
            if (TryDecode(gd, assets, icons[i]) is { } icon) t._verbIcons[(short)(i + 1)] = icon;
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

    private static Texture2D? TryDecode(GraphicsDevice gd, IAssetSource assets, string stem)
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
                pixels[i] = new Color((byte)(pixels[i].R * a), (byte)(pixels[i].G * a), (byte)(pixels[i].B * a), pixels[i].A);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var t in new[] { ChatMouse, ChatText, ChatCommand, ChatShell, SoftGauge, CreditBox,
                     CommandBackground, RecipientsBar, VerbsBar, WingCombo })
            t?.Dispose();
        foreach (var t in _verbIcons.Values) t.Dispose();
        _verbIcons.Clear();
        ChatMouse = ChatText = ChatCommand = ChatShell = SoftGauge = CreditBox = null;
        CommandBackground = RecipientsBar = VerbsBar = WingCombo = null;
    }
}
