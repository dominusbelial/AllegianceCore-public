using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-17 — the radar friendly detection eye's native artwork and left-column
/// layout (<c>WinTrek/radarimage.cpp:1257-1266,1281-1282,1333-1352</c>):
/// <c>AWF_FLIGHT_EYE_ICON</c> = <c>"eyebmp"</c> (<c>WinTrek/artwork.h:85</c>),
/// loaded at <c>radarimage.cpp:226</c>, drawn BELOW the command icon in the same
/// left column, tinted the full side colour. The file <c>eyebmp.mdl</c> is the
/// script <c>ImportImageFromFile("eye.png", true)</c> and <c>eye.png</c> is a
/// 16×16 JPEG — the art test below resolves and measures that without a
/// GraphicsDevice (the production loader streams the same file into
/// <c>Texture2D.FromStream</c>).
/// </summary>
public class Hud17DetectionEyeTests
{
    [Fact]
    public void Eye_takes_the_first_left_slot_or_sits_below_the_command_icon()
    {
        // Both 16×16 native surfaces, 2 px port gap (the HUD-15 icon gap).
        var both = RadarLabelLayout.LeftColumn(hasCommandIcon: true, commandSize: 16f,
            hasEye: true, eyeSize: 16f, gap: 2f);
        Assert.Equal(16f, both.Width);       // column is max(command, eye) wide
        Assert.Equal(18f, both.EyeTop);      // below the command icon
        Assert.Equal(34f, both.Height);      // 16 + gap + 16
        Assert.True(both.AddsLine);          // native lines += 1 for left-side art

        var eyeOnly = RadarLabelLayout.LeftColumn(hasCommandIcon: false, commandSize: 0f,
            hasEye: true, eyeSize: 16f, gap: 2f);
        Assert.Equal(16f, eyeOnly.Width);
        Assert.Equal(0f, eyeOnly.EyeTop);    // no command icon: eye takes the first slot
        Assert.Equal(16f, eyeOnly.Height);
        Assert.True(eyeOnly.AddsLine);

        var none = RadarLabelLayout.LeftColumn(false, 0f, false, 0f, 2f);
        Assert.Equal(0f, none.Width);
        Assert.False(none.AddsLine);

        // The scale follows the skin like every other native px offset.
        var modern = RadarLabelLayout.LeftColumn(true, 32f, true, 32f, 4f);
        Assert.Equal((32f, 36f, 68f), (modern.Width, modern.EyeTop, modern.Height));
    }

    [Fact]
    public void Native_eye_art_replaces_the_star_fallback_in_the_name_line()
    {
        var label = new RadarLabel(
            "Scout", "700", null, null, null, null, Eye: true, CommandId: 5, CommandAccepted: true);
        var cache = new RadarLabelCache();
        var contact = new HudContact(HudContactKind.Ship, new(400, 300), true, 1f,
            SideColor: null, Selected: false, BuildSpot: false,
            Name: "Scout", Range: 700, Shield: null, Hull: null, Fill: null,
            IsEnemy: false, IsSelf: false, IsSameSide: true,
            AcceptedOrder: false, QueuedOrder: false, Threat: false, ThreatFlash: false,
            Subject: false, CarriedFlag: false, CarriedArtifact: false, Ripcord: false,
            Highlight: false, NearestEnemy: false,
            CommandId: 5, CommandAccepted: true, DetectionEye: true);
        var view = cache.Resolve(0, contact, RadarMode.All, commandCamera: false);
        Assert.Equal(label.Eye, view.Label!.Value.Eye);

        // Fallback path (missing command AND eye art) keeps both text marks.
        Assert.Equal("A5 *Scout", view.NameLine);
        // Command art only.
        Assert.Equal("*Scout", view.IconNameLine);
        // Eye art only.
        Assert.Equal("A5 Scout", view.NoEyeNameLine);
        // Both native surfaces drawn: no A/Q prefix and no '*'.
        Assert.Equal("Scout", view.IconNoEyeNameLine);

        Assert.Equal("A5 *Scout", RadarLabelCache.SelectNameLine(view, commandArt: false, eyeArt: false));
        Assert.Equal("*Scout", RadarLabelCache.SelectNameLine(view, commandArt: true, eyeArt: false));
        Assert.Equal("A5 Scout", RadarLabelCache.SelectNameLine(view, commandArt: false, eyeArt: true));
        Assert.Equal("Scout", RadarLabelCache.SelectNameLine(view, commandArt: true, eyeArt: true));
        Assert.DoesNotContain('*', RadarLabelCache.SelectNameLine(view, commandArt: true, eyeArt: true) ?? string.Empty);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Eyebmp_resolves_to_the_native_16x16_image()
    {
        Require.Artwork();
        var assets = new DirectoryAssetSource(Path.Combine(
            ExternalPaths.Resolve(ExternalResourceKind.Artwork)));
        var image = HudNativeTextures.ResolveImportedImage(assets, "eyebmp");
        Assert.NotNull(image);
        Assert.Equal("eye.png", image!.Value.File);
        Assert.Equal((16, 16), (image.Value.Width, image.Value.Height));
    }
}
