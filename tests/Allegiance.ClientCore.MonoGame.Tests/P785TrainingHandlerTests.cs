using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.Training;
using static Allegiance.ClientCore.Training.TrainingMissionIds;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P785 — production-handler tests: the real Game1 handlers driven headlessly (the
/// P761 uninitialized-Game1 idiom) — the training screen's back-to-intro destination
/// (trainingscreen.cpp:160-164), the Introduction mission launch (TM1 → the
/// tm_1_introduction slideshow, trainingscreen.cpp:182-185), the post-slideshow
/// dismiss destination (PostTrainingSlideshow.cpp:25-29) and the 30 s training
/// critical-tip lifetime in the real feedback path (console.cpp:913-931).
/// </summary>
[Collection("Training")]
public class P785TrainingHandlerTests
{
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Type IntroViewType =
        typeof(Game1).GetNestedType("IntroView", BindingFlags.NonPublic)!;

    private static void Set(Game1 game, string name, object? value) =>
        typeof(Game1).GetField(name, Hidden)!.SetValue(game, value);
    private static object? Get(Game1 game, string name) =>
        typeof(Game1).GetField(name, Hidden)!.GetValue(game);
    private static object Call(Game1 game, string name, params object?[] args)
    {
        var method = typeof(Game1).GetMethods(Hidden)
            .First(m => m.Name == name && m.GetParameters().Length == args.Length);
        return method.Invoke(game, args)!;
    }

    private static object IntroView(string name) => Enum.Parse(IntroViewType, name);
    private static string ViewOf(Game1 game) => ((Enum)Get(game, "_introView")!).ToString();

    private static Game1 Headless()
    {
        var game = (Game1)RuntimeHelpers.GetUninitializedObject(typeof(Game1));
        GC.SuppressFinalize(game);
        Set(game, "_session", new ConnectionSession());
        return game;
    }

    private static Microsoft.Xna.Framework.Input.KeyboardState KeysOnly(params XnaKeys[] keys) => new(keys);

    [Fact]
    public void Training_screen_escape_returns_to_the_intro_screen_not_the_chooser()
    {
        // native OnButtonBack (trainingscreen.cpp:160-164): screen(ScreenIDIntroScreen).
        // The port previously bounced to the SinglePlayer chooser — the row's gap.
        var game = Headless();
        Set(game, "_trainingScreen", new TrainingScreenModel());
        Set(game, "_introView", IntroView("Training"));
        Call(game, "UpdateTrainingScreen", KeysOnly(), false, false, false, true);   // esc edge
        Assert.Equal("Main", ViewOf(game));
    }

    [Fact]
    public void Training_screen_enter_on_introduction_opens_the_tm1_slideshow()
    {
        // trainingscreen.cpp:182-185 — TM1 routes to ScreenIDPostTrainSlideshow (the
        // tm_1_introduction slides); Training.cpp:85-89 starts no mission for it.
        var game = Headless();
        Set(game, "_trainingScreen", new TrainingScreenModel());
        Set(game, "_introView", IntroView("Training"));
        Call(game, "UpdateTrainingScreen", KeysOnly(), false, false, true, false);   // enter edge
        var slideshow = (SlideshowModel?)Get(game, "_trainingSlideshow");
        Assert.NotNull(slideshow);
        Assert.True(slideshow!.IsInSlideShow, "the TM1 slideshow must be showing");
        Assert.True((bool)Get(game, "_trainingSlideshowIsPost")!,
            "TM1 uses the post-slideshow screen (ScreenIDPostTrainSlideshow)");
        Assert.Equal("TrainingSlideshow", ViewOf(game));
        Assert.Equal("Introduction", slideshow.Title);
        Assert.Equal(15, slideshow.Count);
    }

    [Fact]
    public void Slideshow_keys_advance_and_the_post_slideshow_dismisses_to_the_training_screen()
    {
        // Slideshow::OnKey (Slideshow.cpp:179-183) advances; PostTrainingSlideshow::
        // Dismiss (PostTrainingSlideshow.cpp:25-29) returns to ScreenIDTrainScreen.
        var game = Headless();
        var slides = new SlideshowModel(new[]
        {
            new SlideshowModel.Slide("one", null, "s1"),
            new SlideshowModel.Slide("two", null, "s2"),
        }, "Test");
        Call(game, "BeginTrainingSlideshow", slides, true, Tm2BasicFlight);
        Assert.Equal(0, ((SlideshowModel)Get(game, "_trainingSlideshow")!).Index);

        // The key that opened the slideshow is still down: no skip (native advances on
        // a key-DOWN event, Slideshow.cpp:179-183).
        Call(game, "UpdateTrainingSlideshow", KeysOnly(XnaKeys.Enter));
        Assert.Equal(0, ((SlideshowModel)Get(game, "_trainingSlideshow")!).Index);
        Call(game, "UpdateTrainingSlideshow", new Microsoft.Xna.Framework.Input.KeyboardState());

        Call(game, "UpdateTrainingSlideshow", KeysOnly(XnaKeys.Space));
        Assert.Equal(1, ((SlideshowModel)Get(game, "_trainingSlideshow")!).Index);
        Call(game, "UpdateTrainingSlideshow", KeysOnly(XnaKeys.Space));   // held: still slide 2
        Assert.Equal(1, ((SlideshowModel)Get(game, "_trainingSlideshow")!).Index);
        Call(game, "UpdateTrainingSlideshow", new Microsoft.Xna.Framework.Input.KeyboardState());

        Call(game, "UpdateTrainingSlideshow", KeysOnly(XnaKeys.Space));
        Assert.Null(Get(game, "_trainingSlideshow"));   // dismissed
        Assert.Equal("Training", ViewOf(game));         // ScreenIDTrainScreen
    }

    [Fact]
    public void Modern_hud_renderer_inherits_the_training_callout_draw()
    {
        // Both-styles rule: DrawTrainingCallout is defined ONCE on HudRenderer and
        // NOT overridden by ModernHudRenderer, so Classic and Modern both draw the
        // callouts (Modern reuses the classic readouts per its design). Fails on
        // master (no DrawTrainingCallout anywhere).
        var method = typeof(HudRenderer).GetMethod("DrawTrainingCallout",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.Equal(typeof(HudRenderer), method!.DeclaringType);
        // Modern resolves the same HudRenderer-declared method (no override): both
        // styles run the shared implementation.
        var onModern = typeof(ModernHudRenderer).GetMethod("DrawTrainingCallout",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(onModern);
        Assert.Equal(typeof(HudRenderer), onModern!.DeclaringType);
    }

    [Fact]
    public void Critical_tips_live_30s_while_a_training_mission_runs_and_7_5s_otherwise()
    {
        // console.cpp:913-931 — fTipTextDuration is evaluated per frame against
        // Training::IsTraining(); the port's UpdateFeedback prunes the same way.
        var game = Headless();
        Set(game, "_criticalTips", new List<(string Text, double SetAt)> { ("LEARN THE GAUGES", 0.0) });
        Set(game, "_nowSeconds", 10.0);   // 10 s after the tip was set

        TrainingController.BeginMission();   // g_pMission != 0
        try
        {
            Call(game, "UpdateFeedback");
            var during = (List<(string, double)>)Get(game, "_criticalTips")!;
            Assert.True(during.Count == 1,
                "a 10 s old critical tip must survive while training runs (30 s lifetime)");

            Set(game, "_nowSeconds", 31.0);
            Call(game, "UpdateFeedback");
            var expired = (List<(string, double)>)Get(game, "_criticalTips")!;
            Assert.True(expired.Count == 0, "a 31 s old critical tip must expire even during training");
        }
        finally { TrainingController.EndMission(); }

        Set(game, "_criticalTips", new List<(string, double)> { ("LEARN THE GAUGES", 0.0) });
        Set(game, "_nowSeconds", 8.0);
        Call(game, "UpdateFeedback");
        var outside = (List<(string, double)>)Get(game, "_criticalTips")!;
        Assert.True(outside.Count == 0, "outside training the 7.5 s lifetime applies");
    }
}
