using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.Training;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P785 — builds the <see cref="SlideshowModel"/>s for the training missions.
/// Native shows the pre-mission slideshow (tm_N_* namespace) before the mission and the
/// post-mission slideshow (tm_N_*_post) after it (WinTrek.cpp:2473-2517, OnButtonTrain
/// trainingscreen.cpp:182-198); the Introduction (TM1) IS the tm_1_introduction
/// post-slideshow — Training.cpp:85-89 starts no mission for it.
/// <para>P943 — when the artwork corpus is available the slides are read from the real
/// slideshow MDL (native namespace table <see cref="SlideshowNamespaces"/>), so every
/// slide carries its native image expression (<see cref="SlideshowImage"/>) and the
/// narration the file's comments hold; without the artwork (headless tests, a bundle
/// without the tm_* files) the port falls back to the <see cref="TrainingSlideData"/>
/// caption table.</para>
/// </summary>
public static class TrainingSlides
{
    /// <summary>The caption table only — the pre-P943 stand-in, used when a slideshow
    /// MDL is absent.</summary>
    private static SlideshowModel FromTable(int missionId, bool post, string title) => new(
        TrainingSlideData.Slides(missionId, post)
            .Select(s => new SlideshowModel.Slide(s.Caption, null, s.SoundStem))
            .ToArray(),
        title);

    private static SlideshowModel From(int missionId, bool post, string title, IAssetSource? artwork)
    {
        var table = TrainingSlideData.Slides(missionId, post);
        if (TryScript(missionId, post, artwork) is not { } script)
            return FromTable(missionId, post, title);

        var slides = new List<SlideshowModel.Slide>(script.Slides.Count);
        for (int i = 0; i < script.Slides.Count; i++)
        {
            var slide = script.Slides[i];
            string caption = SlideshowImage.FirstText(slide.Image) ?? slide.Narration;
            if (caption.Length == 0 && i < table.Length) caption = table[i].Caption;
            string sound = slide.SoundStem.Length > 0 ? slide.SoundStem
                : i < table.Length ? table[i].SoundStem : "";
            slides.Add(new SlideshowModel.Slide(caption, null, sound, slide.Image));
        }
        return new SlideshowModel(slides, title);
    }

    /// <summary>Load and parse the mission's slideshow MDL through the client's asset
    /// source, or null when the namespace/file is absent or unreadable.</summary>
    private static SlideshowScript? TryScript(int missionId, bool post, IAssetSource? artwork)
    {
        if (artwork is null || SlideshowNamespaces.For(missionId, post) is not { } ns) return null;
        string path = ns + ".mdl";
        try
        {
            if (!artwork.Exists(path)) return null;
            return SlideshowScript.TryParse(AssetSources.ReadAllText(artwork, path), ns);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[training] slideshow '{path}' unreadable: {ex.Message} — caption fallback");
            return null;
        }
    }

    /// <summary>The Introduction (TM1): the tm_1_introduction slideshow. Native has no
    /// Mission1 goal graph — Training::StartMission returns false for
    /// c_TM_1_Introduction (Training.cpp:85-89) and OnButtonTrain routes TM1 to
    /// ScreenIDPostTrainSlideshow (trainingscreen.cpp:182-185).</summary>
    public static SlideshowModel Introduction(IAssetSource? artwork = null) =>
        From(Tm1Introduction, post: true, "Introduction", artwork);

    /// <summary>Pre-mission slideshow (native tm_N_* namespace) — Dismiss starts the
    /// mission (TrainingSlideshow::Dismiss, TrainingSlideshow.cpp:30-50).</summary>
    public static SlideshowModel PreMission(int missionId, string name, IAssetSource? artwork = null) =>
        From(missionId, post: false, name, artwork);

    /// <summary>Post-mission slideshow (native tm_N_*_post namespace) — Dismiss returns
    /// to the training screen (PostTrainingSlideshow::Dismiss:25-29).</summary>
    public static SlideshowModel PostMission(int missionId, string name, IAssetSource? artwork = null) =>
        From(missionId, post: true, name, artwork);
}
