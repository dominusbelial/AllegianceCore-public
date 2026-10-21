using System.Linq;
using System;
using System.IO;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-15 — the native command-icon artwork that replaced the port's text
/// stand-ins:
/// <list type="bullet">
/// <item>the radar accepted/queued command surfaces
/// (<c>WinTrek/radarimage.cpp:228-235,1248-1253,1305-1310</c>, table
/// <c>Igc/common.cpp:41-52</c>);</item>
/// <item>the console order-line glyphs <c>comacceptbmp</c>/<c>comqueuedbmp</c>
/// (<c>Artwork/dialog.mdl:1605-1652</c>, <c>WinTrek/console.cpp:875-908</c>);</item>
/// <item>the inventory mounting-bar track <c>mountingbarbkgndbmp</c>
/// (<c>Artwork/invdefault.mdl:43-52</c>, <c>WinTrek/consoledata.cpp:1157</c>).</item>
/// </list>
/// The mapping/fallback/layout rules are pure; one SkippableFact additionally
/// checks the real artwork tree decodes to the declared sizes.
/// </summary>
public class Hud15CommandIconTests
{
    // ── Command-id → icon mapping (Igc/common.cpp:41-52) ────────────────────

    private static readonly (short Cid, string Accepted, string Queued)[] Table =
    {
        (1, "ackillbmp", "qukillbmp"),       // c_cidAttack
        (2, "accptbmp", "qucptbmp"),         // c_cidCapture
        (3, "acdefendbmp", "qudefendbmp"),   // c_cidDefend
        (4, "acpickupbmp", "qupickupbmp"),   // c_cidPickup
        (5, "acgotobmp", "qugotobmp"),       // c_cidGoto
        (6, "acrepairbmp", "qurepairbmp"),   // c_cidRepair
        (7, "acjoinbmp", "qujoinbmp"),       // c_cidJoin
        (8, "acminebmp", "quminebmp"),       // c_cidMine
        (9, "acbuildbmp", "qubuildbmp"),     // c_cidBuild
    };

    [Fact]
    public void Radar_command_icons_map_every_native_command_id_to_its_surface()
    {
        foreach (var (cid, accepted, queued) in Table)
        {
            Assert.Equal(accepted, RadarCommandIcons.ArtStem(cid, accepted: true));
            Assert.Equal(queued, RadarCommandIcons.ArtStem(cid, accepted: false));
        }
        Assert.Equal((short)1, RadarCommandIcons.FirstArtCommandId);   // loop starts at c_cidAttack
        Assert.Equal((short)10, RadarCommandIcons.CommandIdCount);     // c_cidMax
    }

    [Fact]
    public void Radar_command_icon_mapping_stops_where_the_native_load_loop_stops()
    {
        // The native loop starts at c_cidAttack (radarimage.cpp:228), so
        // c_cidDefault's table entries (acdefaultbmp/qudefaultbmp) are never
        // loaded into the radar arrays and the A/Q prefix stays for id 0.
        Assert.Null(RadarCommandIcons.ArtStem(RadarCommandIcons.DefaultCommandId, accepted: true));
        Assert.Null(RadarCommandIcons.ArtStem(RadarCommandIcons.DefaultCommandId, accepted: false));
        // Out of the c_cidMax range / the NA sentinel.
        Assert.Null(RadarCommandIcons.ArtStem(10, accepted: true));
        Assert.Null(RadarCommandIcons.ArtStem(-1, accepted: true));
        Assert.Null(RadarCommandIcons.ArtStem(-2, accepted: false));
    }

    [Fact]
    public void Radar_command_icon_needs_the_decoded_art_or_the_text_prefix_stays()
    {
        // Art present: every mapped command draws the native icon.
        foreach (var (cid, _, _) in Table)
            Assert.True(RadarCommandIcons.UseIcon(cid, artLoaded: true));
        // Missing/undecodable art keeps the A<id>/Q<id> fallback.
        Assert.False(RadarCommandIcons.UseIcon(5, artLoaded: false));
        // No command slot, or an id native never loads art for.
        Assert.False(RadarCommandIcons.UseIcon(null, artLoaded: true));
        Assert.False(RadarCommandIcons.UseIcon(0, artLoaded: true));
        Assert.False(RadarCommandIcons.UseIcon(11, artLoaded: true));
    }

    [Fact]
    public void Radar_label_cache_exposes_the_bare_name_for_the_icon_path()
    {
        // Accepted order on a friendly contact: the fallback line keeps the A/Q
        // prefix, the icon path uses the native label text alone
        // (radarimage.cpp:1199-1208 draws the icon beside the name).
        var label = new RadarLabel(
            "Scout", "700", null, null, null, null, false, CommandId: 5, CommandAccepted: true);
        Assert.Equal("A5 Scout", RadarLabelCache.FormatNameLine(label));
        Assert.Equal("Scout", RadarLabelCache.FormatNameLine(label, includeCommand: false));

        var queued = label with { CommandAccepted = false };
        Assert.Equal("Q5 Scout", RadarLabelCache.FormatNameLine(queued));
        Assert.Equal("Scout", RadarLabelCache.FormatNameLine(queued, includeCommand: false));

        // The cached view carries both strings: the icon path reuses the same
        // bare instance every frame (no per-frame string), and a contact with no
        // command slot reuses one line for both paths.
        var cache = new RadarLabelCache();
        var contact = new HudContact(HudContactKind.Ship, new(400, 300), true, 1f,
            SideColor: null, Selected: false, BuildSpot: false,
            Name: "Scout", Range: 700, Shield: .6f, Hull: .8f, Fill: null,
            IsEnemy: false, IsSelf: false, IsSameSide: true,
            AcceptedOrder: true, QueuedOrder: false, Threat: false, ThreatFlash: false,
            Subject: false, CarriedFlag: false, CarriedArtifact: false, Ripcord: false,
            Highlight: false, NearestEnemy: false,
            CommandId: 5, CommandAccepted: true);
        var first = cache.Resolve(0, contact, RadarMode.All, commandCamera: false);
        Assert.Equal("A5 Scout", first.NameLine);
        Assert.Equal("Scout", first.IconNameLine);
        var moved = contact with { Position = new HudPoint(500, 320), Opacity = .5f };
        var second = cache.Resolve(0, moved, RadarMode.All, commandCamera: false);
        Assert.Same(first.IconNameLine, second.IconNameLine);

        var plain = contact with { AcceptedOrder = false, CommandId = null, CommandAccepted = false };
        var plainView = cache.Resolve(1, plain, RadarMode.All, commandCamera: false);
        Assert.Equal("Scout", plainView.NameLine);
        Assert.Same(plainView.NameLine, plainView.IconNameLine);
    }

    // ── Console order-line glyph layout (dialog.mdl:1605-1652) ──────────────

    [Fact]
    public void Order_line_glyphs_sit_at_the_native_accepted_and_queued_offsets()
    {
        // Pane origin 50 (SideTopLeft(50,-37), dialog.mdl:5451); the rows are at
        // Point(7,-20)/(7,-40) and each glyph is TranslateImage(..., Point(-21,-13))
        // inside its 16x16 bitmap (dialog.mdl:1605-1640,1641-1652).
        var queued = HudConsoleFormat.OrderIconTopLeft(accepted: false, scale: 1f);
        var accepted = HudConsoleFormat.OrderIconTopLeft(accepted: true, scale: 1f);
        Assert.Equal((36f, 54f), queued);       // 50+7-21 = 36; 37+20+13-16 = 54
        Assert.Equal((36f, 74f), accepted);     // 37+40+13-16 = 74
        Assert.Equal(16f, HudConsoleFormat.OrderIconSize);
        Assert.Equal(36f, HudConsoleFormat.OrderIconLeft);
        // Both glyphs sit just above their native line top (13 - 16 = -3).
        Assert.Equal(HudConsoleFormat.OrderLabelTop - 3f + HudConsoleFormat.OrderQueuedBelowLabel, queued.Y);
        Assert.Equal(HudConsoleFormat.OrderLabelTop - 3f + HudConsoleFormat.OrderAcceptedBelowLabel, accepted.Y);

        // FONT-1c fix 4 — the TEXT rows use the same native +20/+40 px
        // (dialog.mdl:1647-1648), so each glyph's top stays exactly 3 px above
        // its own line's text top; the old measured line-height (~13/25 px)
        // left the arrow/check floating off the text.
        float labelTop = HudConsoleFormat.OrderLabelTop;
        float queuedText = HudConsoleFormat.OrderQueuedTop(labelTop, 1f);
        float acceptedText = HudConsoleFormat.OrderAcceptedTop(labelTop, 1f);
        Assert.Equal(labelTop + 20f, queuedText);
        Assert.Equal(labelTop + 40f, acceptedText);
        Assert.Equal(queuedText - 3f, queued.Y);
        Assert.Equal(acceptedText - 3f, accepted.Y);
        // The same offsets scale with the pane, like the glyphs.
        Assert.Equal((72f, 108f), HudConsoleFormat.OrderIconTopLeft(accepted: false, scale: 2f));
        Assert.Equal((72f, 148f), HudConsoleFormat.OrderIconTopLeft(accepted: true, scale: 2f));
        Assert.Equal(HudConsoleFormat.OrderQueuedTop(HudConsoleFormat.OrderLabelTop * 2f, 2f) - 3f * 2f,
            HudConsoleFormat.OrderIconTopLeft(accepted: false, scale: 2f).Y);
    }

    [Fact]
    public void Ripcord_box_is_the_native_150_wide_justify_center_string()
    {
        // Native RipcordCountdownImage = StringImage(JustifyCenter, 150, ...)
        // at SideCenter(-75,15) (dialog.mdl:5145-5151, 5429): the box's top-left
        // is centre-75 and its centre is the viewport centre line.
        Assert.Equal(150f, HudConsoleFormat.RipcordWidth);
        Assert.Equal(75f, HudConsoleFormat.RipcordLeft);
        Assert.Equal(15f, HudConsoleFormat.RipcordTop);
    }

    [Theory]
    [InlineData(1920, 1f)]
    [InlineData(2560, 1.5f)]
    public void Ripcord_text_is_centred_on_the_viewport_centre_line(int width, float scale)
    {
        // A real SpriteFont measurement (no GraphicsDevice): 11 px advance per
        // glyph, so "Ripcord in: 9" measures 13*11 = 143 px at scale 1.
        var chars = Enumerable.Range(32, 95).Select(c => (char)c).ToList();
        var font = new Microsoft.Xna.Framework.Graphics.SpriteFont(null!,
            chars.Select(_ => new Microsoft.Xna.Framework.Rectangle(0, 0, 11, 23)).ToList(),
            chars.Select(_ => new Microsoft.Xna.Framework.Rectangle(0, 0, 11, 23)).ToList(), chars,
            23, 0f, chars.Select(_ => new Microsoft.Xna.Framework.Vector3(0, 11, 0)).ToList(), '?');
        float textWidth = font.MeasureString("Ripcord in: 9").X * scale;
        Assert.Equal(143f * scale, textWidth, 3);
        float left = HudConsoleFormat.RipcordTextLeft(width, textWidth, scale);
        // JustifyCenter: (150*s - text)/2 inside the box at centre-75*s.
        Assert.Equal(width / 2f - 71.5f * scale, left, 3);
        Assert.Equal(width / 2f, left + textWidth / 2f, 3);
        // Not the pre-FONT-1c left-aligned box edge.
        Assert.NotEqual(width / 2f - HudConsoleFormat.RipcordLeft * scale, left);
    }

    // ── Real artwork (skips on a clean clone without the art tree) ──────────

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Every_mapped_command_surface_and_glyph_decodes_at_its_native_size()
    {
        Require.Artwork();
        string art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        foreach (var (cid, accepted, queued) in Table)
        {
            Assert.True(RadarCommandIcons.UseIcon(cid, artLoaded: true));
            var a = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(art, accepted + ".mdl")),
                flipVertical: false);
            var q = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(art, queued + ".mdl")),
                flipVertical: false);
            // 16x16 in ComposingCommand's SwitchPane and the radar label column.
            Assert.Equal((16, 16), (a.Width, a.Height));
            Assert.Equal((16, 16), (q.Width, q.Height));
        }
        foreach (var stem in new[] { "comacceptbmp", "comqueuedbmp" })
        {
            var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(art, stem + ".mdl")),
                flipVertical: false);
            Assert.Equal((16, 16), (tex.Width, tex.Height));
        }
        // The mounting-bar track is the native 64x14 ImagePane surface
        // (Artwork/invdefault.mdl:43-52) the 64x14 fill sits in.
        var track = MdlTextureDecoder.Decode(
            File.ReadAllBytes(Path.Combine(art, "mountingbarbkgndbmp.mdl")), flipVertical: false);
        Assert.Equal((64, 14), (track.Width, track.Height));
    }

    // ── Wiring guards (no graphics device in unit tests) ───────────────────

    private static string Src(string relative)
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", relative));
    }

    [Fact]
    public void Renderers_draw_the_native_glyphs_and_fall_back_to_the_text_lines()
    {
        // Radar: the label block draws the mapped surface only when it decoded,
        // otherwise keeps the A/Q name line.
        var hud = Src(Path.Combine("Rendering", "HudRenderer.cs"));
        Assert.Contains("_nativeHud?.RadarCommandIcon((short)labelCommand, label.CommandAccepted)", hud);
        Assert.Contains("RadarCommandIcons.UseIcon(label.CommandId, commandIcon is not null)", hud);
        Assert.Contains("RadarLabelCache.SelectNameLine(view,", hud);
        // Console order lines: both skins draw the accepted/queued glyphs.
        Assert.Contains("DrawOrderLineGlyph(accepted: false, scale, red)", hud);
        Assert.Contains("DrawOrderLineGlyph(accepted: true, scale, yellow)", hud);
        var modern = Src(Path.Combine("Rendering", "ModernHudRenderer.cs"));
        Assert.Contains("DrawOrderLineGlyph(accepted: false, _scale, red)", modern);
        Assert.Contains("DrawOrderLineGlyph(accepted: true, _scale, yellow)", modern);
        // Inventory: the real mounting track replaces the dark rect in classic.
        Assert.Contains("art?.MountingBarBackground is { } track", hud);
        Assert.Contains("FillRect(bx, by, bw, bh, new Color(0f, 0f, 0f, 0.85f))", hud);
        // The loader decodes both order glyphs, the radar arrays and the track.
        var textures = Src(Path.Combine("Rendering", "HudNativeTextures.cs"));
        Assert.Contains("TryDecode(gd, assets, \"comacceptbmp\")", textures);
        Assert.Contains("TryDecode(gd, assets, \"comqueuedbmp\")", textures);
        Assert.Contains("RadarCommandIcons.ArtStem(cid, accepted: true)", textures);
        Assert.Contains("RadarCommandIcons.ArtStem(cid, accepted: false)", textures);
        var inventory = Src(Path.Combine("Rendering", "InventoryPaneTextures.cs"));
        Assert.Contains("LoadMdl(gd, assets, \"mountingbarbkgndbmp\")", inventory);
        // No per-frame string building on the icon path: the glyph draw only
        // formats a rectangle, never an interpolated string.
        foreach (var body in new[] { Method(hud, "void DrawRadarLabelBlock("), Method(hud, "void DrawOrderLineGlyph(") })
            Assert.DoesNotMatch(new Regex("\\$\""), body);
    }

    [Fact]
    public void Console_tip_slots_sanitise_non_ascii_before_the_font_measures_them()
    {
        // The HUD fonts carry printable ASCII; the ordinary tip row measures its
        // text directly, so the queued-order announcement's em dash made
        // SpriteFont.MeasureString throw mid-frame in the real client. HUD-15
        // sanitises at the slot write (Game1.Feedback.cs PushTip).
        var feedback = Src("Game1.Feedback.cs");
        Assert.Contains("HudFormat.Printable(text)", Method(feedback, "void PushTip("));
        // The queued-order announcement calls PushOrdinaryTip directly.
        Assert.Contains("HudFormat.Printable(text)", Method(feedback, "void PushOrdinaryTip("));
        Assert.Equal("queued order announced: commandID=5 ? press [Home] to accept",
            HudFormat.Printable("queued order announced: commandID=5 \u2014 press [Home] to accept"));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced " + signature);
    }
}
