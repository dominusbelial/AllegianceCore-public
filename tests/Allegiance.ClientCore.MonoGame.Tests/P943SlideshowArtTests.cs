using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Training;
using Allegiance.Core;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P943 — the corpus's real slideshows. Every <c>tm_*.mdl</c> from the artwork
/// install (native namespace map, WinTrek.cpp:2473-2522) must parse to the same
/// slide count the port's caption table carries, and every slide must resolve to a
/// drawable image expression or a non-empty narration caption — never a black slide.
/// </summary>
[Collection("Training")]
[Trait("Requires", "Artwork")]
public sealed class P943SlideshowArtTests
{
    private static IAssetSource Artwork() =>
        new DirectoryAssetSource(ExternalPaths.Resolve(ExternalResourceKind.Artwork));

    private static SlideshowModel Slideshow(int mission, bool post, IAssetSource artwork)
    {
        string name = TrainingScreenModel.NameOf(mission);
        return post ? TrainingSlides.PostMission(mission, name, artwork)
                    : TrainingSlides.PreMission(mission, name, artwork);
    }

    [SkippableFact]
    public void Every_corpus_slideshow_resolves_every_slide_to_an_image_or_a_non_empty_caption()
    {
        Require.Artwork();
        var artwork = Artwork();
        var rows = SlideshowNamespaces.All();
        Assert.Equal(13, rows.Count);   // 6 pre + 7 post, mission 7 has neither

        foreach (var (mission, post, ns) in rows)
        {
            var slideshow = Slideshow(mission, post, artwork);
            var table = TrainingSlideData.Slides(mission, post);
            Assert.True(slideshow.Count > 0, $"{ns}: no slides parsed from the artwork");
            Assert.Equal(table.Length, slideshow.Count);   // the parser read every native pair
            Assert.Equal(ns, SlideshowNamespaces.For(mission, post));

            for (int i = 0; i < slideshow.Count; i++)
            {
                var slide = slideshow.Slides[i];
                Assert.Equal(table[i].SoundStem, slide.SoundStem);
                Assert.NotNull(slide.Art);   // P943: the slide image is in the install
                var layout = SlideshowLayout.Build(slide.Art, stem => SlideshowArt.ProbeSize(artwork, stem));
                Assert.Empty(layout.Missing);
                Assert.True(layout.HasContent || slide.Caption.Length > 0,
                    $"{ns}: slide {i + 1} has neither drawable art nor a caption");
                // Text slides carry their narration in the StringImage itself; the
                // rest carry it in the MDL comment block above the slide.
                if (i < table.Length && table[i].Caption.Length > 0)
                    Assert.False(string.IsNullOrWhiteSpace(slide.Caption), $"{ns}: slide {i + 1} lost its caption");
            }
        }
    }

    [SkippableFact]
    public void Basic_flight_postlude_resolves_the_hangar_bitmap_and_its_narration()
    {
        // P940's capture was a black screen with only the footer; tm_2_basic_flight_post.mdl
        // is (ImportImage("hangarbmp", false), tm_slide_2_post_01SoundId) with the narration
        // in the comments above the list.
        Require.Artwork();
        var artwork = Artwork();
        var slideshow = TrainingSlides.PostMission(2, TrainingScreenModel.NameOf(2), artwork);
        var slide = Assert.Single(slideshow.Slides);
        Assert.Equal("tm_slide_2_post_01", slide.SoundStem);
        var layout = SlideshowLayout.Build(slide.Art, stem => SlideshowArt.ProbeSize(artwork, stem));
        var image = Assert.Single(layout.Images);
        Assert.Equal("hangarbmp", image.Stem);
        Assert.False(image.ColorKey);
        Assert.Equal(800, image.Width);
        Assert.Equal(600, image.Height);
        Assert.Contains("Basic Weaponry", slide.Caption);
    }

    [SkippableFact]
    public void Composed_introduction_slide_places_the_circle_overlay_over_the_hangar()
    {
        // tm_1_introduction.mdl slide 1_04b: GroupImage([TranslateImage(circle_overlay,
        // Point(205, 375)), hangar_background]) — the overlay is colour-keyed and lands on
        // top of the hangar bitmap at its native point.
        Require.Artwork();
        var artwork = Artwork();
        var slideshow = TrainingSlides.Introduction(artwork);
        Assert.Equal(15, slideshow.Count);
        var slide = slideshow.Slides[4];
        Assert.Equal("tm_slide_1_04b", slide.SoundStem);
        var layout = SlideshowLayout.Build(slide.Art, stem => SlideshowArt.ProbeSize(artwork, stem));
        Assert.Equal(["hangarbmp", "tm_slide_circle_overlaybmp"],
            layout.Images.Select(i => i.Stem).ToArray());
        var overlay = layout.Images[^1];
        Assert.True(overlay.ColorKey);
        Assert.Equal(205, overlay.X);
        Assert.Equal(375, overlay.Y);
        Assert.Equal((140, 140), (overlay.Width, overlay.Height));
        Assert.Empty(layout.Missing);
    }

    [SkippableFact]
    public void Text_slides_keep_the_native_string_image_text()
    {
        // tm_4/tm_6/tm_8 pre-slides are StringImage slides: the narration IS the art
        // (tm_4_enemy_engagement.mdl:54-57 GroupImage([TranslateImage(text), background])).
        Require.Artwork();
        var artwork = Artwork();
        var slideshow = TrainingSlides.PreMission(4, TrainingScreenModel.NameOf(4), artwork);
        Assert.Equal(2, slideshow.Count);
        var layout = SlideshowLayout.Build(slideshow.Slides[0].Art, stem => SlideshowArt.ProbeSize(artwork, stem));
        var text = Assert.Single(layout.Texts);
        Assert.Equal(150, text.X);
        Assert.Equal(450, text.Y);
        Assert.Equal(500, text.Width);
        Assert.Contains("Helium3", text.Text);
        Assert.Empty(layout.Missing);
    }

    [SkippableFact]
    public void Slideshow_bitmap_sizes_match_the_native_screens()
    {
        // ImportImage keeps the authored pixel size (engine/modeler.cpp:723-735); the
        // hangar/loadout/command-view screens are 800×600 and the overlays are small.
        Require.Artwork();
        var artwork = Artwork();
        Assert.Equal((800, 600), SlideshowArt.ProbeSize(artwork, "hangarbmp"));
        Assert.Equal((800, 600), SlideshowArt.ProbeSize(artwork, "tm_slide_backgroundbmp"));
        Assert.Equal((800, 600), SlideshowArt.ProbeSize(artwork, "tm_loadout_backgroundbmp"));
        Assert.Equal((800, 600), SlideshowArt.ProbeSize(artwork, "tm_slide_5_01bmp"));
        Assert.Equal((140, 140), SlideshowArt.ProbeSize(artwork, "tm_slide_circle_overlaybmp"));
        Assert.Equal((340, 217), SlideshowArt.ProbeSize(artwork, "tm_teleportbmp"));
        Assert.Null(SlideshowArt.ProbeSize(artwork, "not_installed_bmp"));
    }
}
