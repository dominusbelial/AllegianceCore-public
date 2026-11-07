using System.Collections.Generic;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P945 — the native hangar screen (<c>hangar.mdl</c>). Headless checks over the
/// real artwork install: every stem resolves with its cached native size, the
/// layout maps every authored pane point/rectangle at 4:3, 16:9 and ultrawide
/// viewports, the pointer hit test is the draw rectangle for every verb, the
/// hover table is the native string table, the in-turret badge is the
/// <c>BoardState == 2</c> switch, the launch animation follows the native 0.7 s
/// ramp, and missing art falls back instead of blanking the screen.
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class P945HangarArtTests
{
    private static IAssetSource Artwork() =>
        new DirectoryAssetSource(ExternalPaths.Resolve(ExternalResourceKind.Artwork));

    // ── 1. artwork resolves with its native size ─────────────────────────

    [SkippableFact]
    public void Every_hangar_art_stem_resolves_with_its_native_size()
    {
        Require.Artwork();
        var artwork = Artwork();

        Assert.True(HangarArt.IsInstalled(artwork));
        Assert.Equal("hangarbmp.mdl", HangarArt.ResolvePath(artwork, HangarScreen.BackgroundStem));
        Assert.Equal((800, 600), HangarArt.ProbeSize(artwork, HangarScreen.BackgroundStem));
        Assert.Equal((172, 30), HangarArt.ProbeSize(artwork, HangarScreen.InTurretStem));

        // Every button stem resolves case-insensitively (hangar.mdl asks for
        // "btnquitbmp"; the artwork file is BtnQuitbmp.mdl) and its strip is
        // exactly two faces tall, so the pane is width × (height / 2).
        foreach (var control in HangarScreen.Controls)
        {
            var strip = HangarArt.ProbeSize(artwork, control.Stem);
            Assert.True(strip is not null, $"missing hangar art '{control.Stem}'");
            var (width, height) = strip!.Value;
            if (control.Action == HangarAction.Launch)
            {
                // The launch pane is a FrameImage: its size is the animation
                // background (animlaunch.mdl), not animlaunchbmp's 4×4 sheet.
                var launch = HangarArt.ProbeLaunch(artwork)!.Value;
                Assert.Equal(HangarScreen.NativeSize[control.Action], (launch.Width, launch.Height));
                Assert.Equal(16, launch.Frames);
                Assert.Equal((130 * 4, 214 * 4), strip.Value);   // the sheet holds the same 16 frames
                continue;
            }
            Assert.Equal(HangarScreen.NativeSize[control.Action],
                (width, height / HangarScreen.FaceCount));
        }
        Assert.Equal("BtnQuitbmp.mdl", HangarArt.ResolvePath(artwork, "btnquitbmp"));
        Assert.Equal("hangarinturretbmp.mdl", HangarArt.ResolvePath(artwork, "hangarinturretbmp"));
    }

    [SkippableFact]
    public void Launch_animation_has_16_native_frames_in_the_sheet_order()
    {
        Require.Artwork();
        var artwork = Artwork();
        var launch = HangarArt.ProbeLaunch(artwork);
        Assert.NotNull(launch);
        Assert.Equal((130, 214, 16), launch!.Value);

        // The FrameImage deltas decode to exactly animlaunchbmp's 4×4 sheet in
        // row-major order — the frame count and order the native namespace
        // declares (animlaunch.mdl: FrameImage(nFrame = 16), frame 0 = the
        // background). Byte-exact because both decode with the same RGB565
        // rounding.
        var frames = MdlFrameImage.Decode(AssetSources.ReadAllBytes(artwork, "animlaunch.mdl"));
        Assert.Equal(16, frames.FrameCount);
        Assert.Equal(0f, frames.InitialFrame);
        var sheet = MdlTextureDecoder.Decode(
            AssetSources.ReadAllBytes(artwork, "animlaunchbmp.mdl"), flipVertical: false);
        Assert.Equal((520, 856), (sheet.Width, sheet.Height));

        for (int i = 0; i < frames.FrameCount; i++)
        {
            int cellX = (i % 4) * 130;
            int cellY = (i / 4) * 214;
            var frame = frames.Frames[i];
            Assert.Equal((130, 214), (frame.Width, frame.Height));
            for (int y = 0; y < 214; y += 17)                 // sampled rows keep the test fast
                for (int x = 0; x < 130; x++)
                {
                    int frameIndex = (y * 130 + x) * 4;
                    int sheetIndex = ((cellY + y) * 520 + cellX + x) * 4;
                    Assert.Equal(sheet.Rgba.AsSpan(sheetIndex, 4).ToArray(),
                        frame.Rgba.AsSpan(frameIndex, 4).ToArray());
                }
        }
    }

    // ── 2. native 800×600 layout ──────────────────────────────────────────

    [Theory]
    [InlineData(800, 600)]     // exact native 4:3
    [InlineData(1920, 1080)]   // 16:9 — the shipped default
    [InlineData(2560, 1080)]   // 21:9 ultrawide
    public void Native_layout_maps_every_point_and_button_rectangle(int width, int height)
    {
        var t = HangarScreen.ScreenTransform.For(width, height);
        Assert.Equal(MathF.Min(width / 800f, height / 600f), t.Scale, 4);
        Assert.Equal((width - 800 * t.Scale) / 2f, t.OffsetX, 3);
        Assert.Equal((height - 600 * t.Scale) / 2f, t.OffsetY, 3);

        // The background fills the scaled native screen, centred.
        var screen = t.Map(0, 0, 800, 600);
        Assert.Equal((int)MathF.Round(t.OffsetX), screen.X);
        Assert.Equal((int)MathF.Round(t.OffsetY), screen.Y);
        Assert.Equal((int)MathF.Round(800 * t.Scale), screen.Width);
        Assert.Equal((int)MathF.Round(600 * t.Scale), screen.Height);

        // Every native control lands at its authored point (hangar.mdl:87-102).
        foreach (var control in HangarScreen.Controls)
        {
            var size = HangarScreen.NativeSize[control.Action];
            var rect = HangarScreen.Rect(width, height, control);
            Assert.Equal((int)MathF.Round(t.OffsetX + control.X * t.Scale), rect.X);
            Assert.Equal((int)MathF.Round(t.OffsetY + control.Y * t.Scale), rect.Y);
            Assert.Equal(Math.Max(1, (int)MathF.Round(size.Width * t.Scale)), rect.Width);
            Assert.Equal(Math.Max(1, (int)MathF.Round(size.Height * t.Scale)), rect.Height);
            Assert.Equal(rect, HangarControls.Bounds(width, height, control.Action));
        }

        // Y is TOP-DOWN: the launch pane sits in the upper half (the hangar door),
        // the exit button near the bottom, the hover strip below it.
        var launch = HangarScreen.Rect(width, height, HangarAction.Launch);
        var exit = HangarScreen.Rect(width, height, HangarAction.Exit);
        var hover = HangarScreen.HoverTextRect(width, height);
        Assert.True(launch.Bottom < exit.Top);
        Assert.True(exit.Bottom < hover.Top);
        Assert.Equal((int)MathF.Round(t.OffsetX + 100 * t.Scale), hover.X);
        Assert.Equal((int)MathF.Round(t.OffsetY + 561 * t.Scale), hover.Y);
        Assert.Equal(Math.Max(1, (int)MathF.Round(600 * t.Scale)), hover.Width);

        var turret = HangarScreen.InTurretRect(width, height);
        Assert.Equal((int)MathF.Round(t.OffsetX + 320 * t.Scale), turret.X);
        Assert.Equal((int)MathF.Round(t.OffsetY + 450 * t.Scale), turret.Y);
    }

    // ── 3. hit-test == draw rectangle ─────────────────────────────────────

    [Theory]
    [InlineData(800, 600)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1080)]
    public void Hit_test_is_the_draw_rectangle_for_every_action(int width, int height)
    {
        var actions = HangarControls.Actions(true).ToArray();
        Assert.Equal(10, actions.Length);
        foreach (var action in actions)
        {
            var rect = HangarScreen.Rect(width, height, action);
            Assert.False(rect.IsEmpty);
            Assert.Equal(action, HangarScreen.Hit(width, height, rect.Center.X, rect.Center.Y, true));
            // Every corner/edge sample hits the same verb, i.e. the whole drawn
            // rectangle is clickable.
            foreach (var (x, y) in new[]
                     {
                         (rect.Left, rect.Top), (rect.Right - 1, rect.Top),
                         (rect.Left, rect.Bottom - 1), (rect.Right - 1, rect.Bottom - 1),
                     })
                Assert.Equal(action, HangarScreen.Hit(width, height, x, y, true));
            // No verb's rectangle overlaps another's.
            Assert.DoesNotContain(actions.Where(a => a != action),
                a => rect.Intersects(HangarScreen.Rect(width, height, a)));
        }
        Assert.Equal(HangarAction.None, HangarScreen.Hit(width, height, 0, 0, true));
        // The development gate hides invest exactly like native.
        var invest = HangarScreen.Rect(width, height, HangarAction.Invest);
        Assert.Equal(HangarAction.None,
            HangarScreen.Hit(width, height, invest.Center.X, invest.Center.Y, false));
    }

    [Theory]
    [InlineData(1920, 1080)]   // side letterbox
    [InlineData(800, 600)]     // exact 4:3: no margin at all
    public void Port_only_verbs_sit_outside_the_native_art_when_there_is_a_margin(int width, int height)
    {
        // Buy/Default have no native hangar button. They must stay clickable and
        // must not cover the 800×600 artwork unless the screen fills the window.
        var t = HangarScreen.ScreenTransform.For(width, height);
        var screen = t.Map(0, 0, 800, 600);
        foreach (var second in new[] { false, true })
        {
            var rect = HangarScreen.PortVerbRect(width, height, second);
            Assert.False(rect.IsEmpty);
            bool inScreen = screen.Contains(rect);
            bool hasMargin = width > (int)MathF.Round(800 * t.Scale) || height > (int)MathF.Round(600 * t.Scale);
            Assert.Equal(hasMargin, !inScreen);
            Assert.True(rect.Right <= width && rect.Bottom <= height);
        }
        Assert.False(HangarScreen.PortVerbRect(width, height, false)
            .Intersects(HangarScreen.PortVerbRect(width, height, true)));
    }

    // ── 4. hover text ─────────────────────────────────────────────────────

    [Fact]
    public void Hover_ids_map_to_the_native_string_table()
    {
        Assert.Equal("", HangarControls.Feedback(HangarAction.None));
        Assert.Equal("Launch ship", HangarControls.Feedback(HangarAction.Launch));
        Assert.Equal("Command screen", HangarControls.Feedback(HangarAction.Command));
        Assert.Equal("Ship's loadout", HangarControls.Feedback(HangarAction.Loadout));
        Assert.Equal("Team investments", HangarControls.Feedback(HangarAction.Invest));
        Assert.Equal("Help", HangarControls.Feedback(HangarAction.Help));
        Assert.Equal("Teleport to another station or Board another ship",
            HangarControls.Feedback(HangarAction.Teleport));
        Assert.Equal("Information about the teams playing", HangarControls.Feedback(HangarAction.Team));
        Assert.Equal("Exit the game.", HangarControls.Feedback(HangarAction.Exit));
        // The port-only verbs keep the port's wording (native has no such button).
        Assert.NotEmpty(HangarControls.Feedback(HangarAction.Buy));
        Assert.NotEmpty(HangarControls.Feedback(HangarAction.Default));
        // Native hover style: 0.75 grey, hugeFont's 23 px cell height.
        Assert.Equal(new Color(0.75f, 0.75f, 0.75f), HangarScreen.HoverTextColor);
        Assert.Equal(23, HangarScreen.HoverTextHeight);
    }

    [SkippableFact]
    public void Native_hover_strings_fit_the_600px_hugefont_pane()
    {
        Require.Artwork();
        // StringPane clips at the pane width; hugeFont's huge 23 px cells still
        // leave every native string inside 600 px (widest: the teleport line).
        var font = NativeHugeFontMetrics.Load(Artwork());
        Assert.Equal(23, font.Height);   // hugeFont's native cell height
        foreach (var (action, text) in HangarScreen.HoverText)
            if (action != HangarAction.Buy && action != HangarAction.Default)
                Assert.True(NativeHugeFontMetrics.MeasureWidth(font, text) <= HangarScreen.HoverTextPaneWidth,
                    $"{action}: '{text}' is {NativeHugeFontMetrics.MeasureWidth(font, text)} px wide");
    }

    // ── 5. in-turret badge ────────────────────────────────────────────────

    [Fact]
    public void In_turret_badge_is_drawn_only_for_board_state_two()
    {
        Assert.False(HangarScreen.ShowsInTurretBadge(null));
        Assert.False(HangarScreen.ShowsInTurretBadge(0));   // docked, not boarded
        Assert.False(HangarScreen.ShowsInTurretBadge(1));   // observer of a carrier
        Assert.True(HangarScreen.ShowsInTurretBadge(2));    // turret gunner
    }

    // ── 6. launch animation semantics ─────────────────────────────────────

    [Fact]
    public void Launch_animation_ramps_in_0_7s_and_back_out_like_native()
    {
        var animation = new HangarLaunchAnimation();
        animation.Update(inside: false, now: 0f);
        Assert.Equal(0, animation.Frame(16));               // idle: closed door

        animation.Update(inside: true, now: 10f);           // pointer enters
        Assert.Equal(0, animation.Frame(16));
        animation.Update(inside: true, now: 10.35f);        // half way
        Assert.Equal(0.5f, animation.Value, 3);
        Assert.Equal(8, animation.Frame(16));
        animation.Update(inside: true, now: 10.7f);         // fully open, clamped
        Assert.Equal(1f, animation.Value, 4);
        Assert.Equal(15, animation.Frame(16));
        animation.Update(inside: true, now: 20f);
        Assert.Equal(1f, animation.Value, 4);               // no loop, no finish

        animation.Update(inside: false, now: 20f);          // pointer leaves: ramp back
        Assert.Equal(1f, animation.Value, 4);
        animation.Update(inside: false, now: 20.35f);
        Assert.Equal(0.5f, animation.Value, 3);
        animation.Update(inside: false, now: 20.7f);
        Assert.Equal(0f, animation.Value, 4);
        Assert.Equal(0, animation.Frame(16));

        // Frame mapping is native's bound((int)(value * nFrame), 0, nFrame - 1).
        Assert.Equal(0, new HangarLaunchAnimation().Frame(0));
        Assert.Equal(0, animation.Frame(1));
    }

    [Fact]
    public void Button_strips_draw_the_normal_face_then_the_inside_face()
    {
        // ButtonFaceUp | ButtonFaceInside (5) -> CountBits = 2 rows: top = normal,
        // bottom = mouse-inside (engine/button.cpp:108-198, 240-256).
        Assert.Equal(2, HangarScreen.FaceCount);
        Assert.Equal(0, HangarScreen.FaceRow(inside: false));
        Assert.Equal(1, HangarScreen.FaceRow(inside: true));
    }

    // ── 7. missing-art fallback ───────────────────────────────────────────

    [SkippableFact]
    public void Missing_art_selection_falls_back_to_the_text_row_hangar()
    {
        // The real install can draw the native screen...
        Require.Artwork();
        Assert.True(HangarArt.IsInstalled(Artwork()));

        // ...an empty source cannot, and reports every stem as absent (no throw).
        var empty = new EmptyAssetSource();
        Assert.False(HangarArt.IsInstalled(empty));
        Assert.Null(HangarArt.ResolvePath(empty, HangarScreen.BackgroundStem));
        Assert.Null(HangarArt.ProbeSize(empty, HangarScreen.BackgroundStem));
        Assert.Null(HangarArt.ProbeLaunch(empty));
        foreach (var control in HangarScreen.Controls)
            Assert.Null(HangarArt.ResolvePath(empty, control.Stem));
    }

    /// <summary>
    /// The native <c>hugeFont</c> metrics straight out of <c>font.mdl</c>: each
    /// <c>ImportFont</c> is a serialised <c>FontImpl</c> — DWORD height, DWORD
    /// width, DWORD dataSize, <c>CharData[256]</c> (12 bytes each: WinPoint cell
    /// size + glyph offset) then the glyph bits (<c>engine/font.cpp:353-366</c>,
    /// <c>engine/font.cpp:29-50</c>). The payload carries a 4-byte writer tail, so
    /// the next object starts 4 bytes after <c>dataSize</c>; parsing all 14 fonts
    /// this way lands exactly on EOF and reproduces the cell heights
    /// <see cref="NativeFontCatalog.NativeCellHeight"/> documents. Guards the
    /// 23 px cell height the hangar's hover text is scaled to, and that the
    /// native strings fit the 600 px pane.
    /// </summary>
    private static class NativeHugeFontMetrics
    {
        internal readonly record struct Metrics(int Height, int[] Widths);

        public static Metrics Load(IAssetSource assets)
        {
            var data = AssetSources.ReadAllBytes(assets, "font.mdl");
            int pos = 24;
            int namespaces = ReadU32(data, 8), imports = ReadU32(data, 12);
            int exports = ReadU32(data, 16), definitions = ReadU32(data, 20);
            for (int i = 0; i < namespaces; i++) pos = SkipCString(data, pos);
            for (int i = 0; i < imports; i++)
            {
                pos += 4;
                pos = SkipCString(data, pos);
            }
            var exportNames = new string[exports];
            for (int i = 0; i < exports; i++) (exportNames[i], pos) = ReadCString(data, pos);

            for (int i = 0; i < exports + definitions; i++)
            {
                pos += 4;                                       // object index
                Assert.Equal(9, ReadU32(data, pos));            // ObjectImport
                pos += 4;
                pos += 4;                                       // import index (0 = ImportFont)
                Assert.Equal(7, ReadU32(data, pos));            // ObjectBinary
                pos += 4;
                int height = ReadU32(data, pos);
                int dataSize = ReadU32(data, pos + 8);
                pos += 12;
                var widths = new int[256];
                for (int c = 0; c < 256; c++)
                {
                    widths[c] = ReadU32(data, pos);              // WinPoint.x = advance
                    pos += 12;
                }
                pos += dataSize + 4;                             // glyph bits + writer tail
                if (exportNames[i] == "hugeFont") return new Metrics(height, widths);
            }
            throw new InvalidDataException("font.mdl has no hugeFont");
        }

        public static int MeasureWidth(Metrics font, string text)
        {
            int width = 0;
            foreach (char c in text) width += font.Widths[c];
            return width;
        }

        private static (string Value, int Next) ReadCString(byte[] data, int pos)
        {
            int end = System.Array.IndexOf(data, (byte)0, pos);
            string value = System.Text.Encoding.Latin1.GetString(data, pos, end - pos);
            int length = end - pos + 1;
            return (value, pos + ((length + 3) & ~3));
        }

        private static int SkipCString(byte[] data, int pos) => ReadCString(data, pos).Next;

        private static int ReadU32(byte[] data, int pos) =>
            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(pos));
    }

    private sealed class EmptyAssetSource : IAssetSource
    {
        public bool Exists(string relativePath) => false;
        public Stream OpenRead(string relativePath) =>
            throw new FileNotFoundException(relativePath);
        public IEnumerable<string> Enumerate(string directoryRelativePath) => [];
    }
}
