using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.Training;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P943 — the training slideshow MDL reader. Native loads each mission's
/// <c>tm_*</c> namespace and shows the <c>slides</c> list of (image, SoundID) pairs
/// (WinTrek/Slideshow.cpp:96-107); the artwork carries the narration in the
/// comments above each slide. These tests pin the operator subset the slideshows
/// use — <c>ImportImage</c>, <c>GroupImage</c>, <c>TranslateImage</c>,
/// <c>StringImage</c>, <c>Point</c> — and the native comment fallbacks.
/// </summary>
[Collection("Training")]
public class P943SlideshowScriptTests
{
    private const string Fixture = """
        use "SlideshowData";
        use "sounddef";
        use "font";

        slide_background = ImportImage("tm_slide_backgroundbmp", false);
        circle_overlay = ImportImage ("tm_slide_circle_overlaybmp", true);
        slide_text_position = Point(150, 450);
        slide_text_1 = "Line one.\n\nLine two.";
        slide_text_1_image = StringImage(JustifyLeft, 500, largeFont, white, slide_text_1);

        slides =
        [
            // tm_slide_1_01
            // The first narration sentence,
            // wrapped over two comment lines.
            (slide_background, tm_slide_1_01SoundId),

            // tm_slide_1_02
            (GroupImage ([TranslateImage (circle_overlay, Point (205, 375)), slide_background]), tm_slide_1_02SoundId),

            // tm_slide_1_03
            (GroupImage ([TranslateImage (slide_text_1_image, slide_text_position), slide_background]), tm_slide_1_03SoundId)
        ];
        """;

    private static (int Width, int Height)? Size(string stem) => stem switch
    {
        "tm_slide_backgroundbmp" => (800, 600),
        "tm_slide_circle_overlaybmp" => (140, 140),
        _ => null,
    };

    private static SlideshowScript Parsed()
    {
        var script = SlideshowScript.TryParse(Fixture, "tm_test");
        Assert.NotNull(script);
        return script!;
    }

    [Fact]
    public void Import_image_carries_its_stem_and_colour_key()
    {
        // ImportImage(stem, colourKey) — engine/modeler.cpp:691-706.
        var script = Parsed();
        Assert.Equal("tm_slide_1_01", script.Slides[0].SoundStem);
        Assert.Equal(new SlideshowImage.Import("tm_slide_backgroundbmp", false), script.Slides[0].Image);
        Assert.Contains("tm_slide_circle_overlaybmp", script.ImportStems);
    }

    [Fact]
    public void Group_image_paints_its_first_child_last_so_it_ends_on_top()
    {
        // GroupImage([…]) renders in reverse (engine/modeler.cpp:1544-1562 pushes each
        // child to the front; engine/image.cpp:878-935 renders front-to-back): the
        // first child is drawn LAST — on top of the background.
        var slide = SlideshowLayout.Build(Parsed().Slides[1].Image, Size);
        Assert.Equal(2, slide.Images.Count);
        Assert.Equal("tm_slide_backgroundbmp", slide.Images[0].Stem);      // painted first
        Assert.Equal("tm_slide_circle_overlaybmp", slide.Images[1].Stem);  // on top
        Assert.True(slide.Images[1].ColorKey, "the overlay is imported with colorKey=true");
        Assert.Empty(slide.Missing);
    }

    [Fact]
    public void Translate_image_offsets_its_child_to_the_native_point()
    {
        // TranslateImage(image, Point(x, y)): the point is the child's lower-left in
        // the native y-up space (engine/image.cpp:828-845).
        var slide = SlideshowLayout.Build(Parsed().Slides[1].Image, Size);
        var overlay = slide.Images.Single(i => i.Stem == "tm_slide_circle_overlaybmp");
        Assert.Equal(205, overlay.X);
        Assert.Equal(375, overlay.Y);
        Assert.Equal(140, overlay.Width);
        Assert.Equal(140, overlay.Height);
        var background = slide.Images.Single(i => i.Stem == "tm_slide_backgroundbmp");
        Assert.Equal(0, background.X);
        Assert.Equal(0, background.Y);
    }

    [Fact]
    public void String_image_hangs_below_its_point_and_wraps_at_its_width()
    {
        // StringImage bounds are Rect(0, -ysize, width, 0) — the block hangs BELOW its
        // translate point and wraps at its width (engine/image.cpp:341-400).
        var slide = SlideshowLayout.Build(Parsed().Slides[2].Image, Size);
        var text = Assert.Single(slide.Texts);
        Assert.Equal(150, text.X);
        Assert.Equal(450, text.Y);
        Assert.Equal(500, text.Width);
        Assert.Equal("Line one.\n\nLine two.", text.Text);
        Assert.True(text.LeftJustify);
    }

    [Fact]
    public void Slide_narration_comes_from_the_comment_block_above_the_slide()
    {
        var script = Parsed();
        Assert.Equal("The first narration sentence, wrapped over two comment lines.",
            script.Slides[0].Narration);
        Assert.Equal("", script.Slides[1].Narration);   // no comments above slide 2
    }

    [Fact]
    public void An_unresolved_bitmap_is_reported_once_and_yields_no_draw_operation()
    {
        // Native's LoadImage returns NULL for missing art (engine/modeler.cpp:2463-2492);
        // the port reports it so the screen can fall back to the narration caption.
        var script = SlideshowScript.TryParse("""
            slides = [ (ImportImage("notinstalledbmp", false), tm_slide_9_01SoundId) ];
            """, "tm_missing")!;
        var slide = SlideshowLayout.Build(script.Slides[0].Image, _ => null);
        Assert.Empty(slide.Images);
        Assert.False(slide.HasContent);
        Assert.Equal(["notinstalledbmp"], slide.Missing);
    }

    [Fact]
    public void Every_post_mission_slide_carries_its_native_narration()
    {
        // tm_2/3/4 post MDLs carry the narration in comments above the slides list
        // (tm_2_basic_flight_post.mdl:8-11, tm_3_basic_weaponry_post.mdl:8-12,
        // tm_4_enemy_engagement_post.mdl:8-16). The port used to ship them empty, which
        // is the black postlude P940 captured.
        var post = new[]
        {
            TrainingSlideData.Slides(Tm2BasicFlight, true),
            TrainingSlideData.Slides(Tm3BasicWeaponry, true),
            TrainingSlideData.Slides(Tm4EnemyEngagement, true),
        };
        foreach (var slides in post)
            foreach (var (caption, _) in slides)
                Assert.False(string.IsNullOrWhiteSpace(caption),
                    "every native post-mission slide must carry its narration");
        Assert.Contains("Basic Weaponry", TrainingSlideData.Slides(Tm2BasicFlight, true)[0].Caption);
        Assert.Contains("Enemy Engagement", TrainingSlideData.Slides(Tm3BasicWeaponry, true)[0].Caption);
        Assert.Contains("Command View", TrainingSlideData.Slides(Tm4EnemyEngagement, true)[0].Caption);
    }

    [Fact]
    public void Every_native_slideshow_namespace_is_listed()
    {
        // WinTrek.cpp:2473-2522 — mission 7 has no slideshow; 9/10 are the FreeFlight
        // additions (blank / tm_6 reuse).
        Assert.Equal("tm_1_introduction", SlideshowNamespaces.For(Tm1Introduction, post: true));
        Assert.Equal("tm_2_basic_flight", SlideshowNamespaces.For(Tm2BasicFlight, post: false));
        Assert.Equal("tm_2_basic_flight_post", SlideshowNamespaces.For(Tm2BasicFlight, post: true));
        Assert.Null(SlideshowNamespaces.For(7, post: false));
        Assert.Null(SlideshowNamespaces.For(7, post: true));
        Assert.Equal("tm_6_practice_arena_post", SlideshowNamespaces.For(10, post: true));
        Assert.Equal(13, SlideshowNamespaces.All().Count);
    }
}
