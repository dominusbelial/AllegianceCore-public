using Allegiance.ClientCore.Training;
using static Allegiance.ClientCore.Training.TrainingMissionIds;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P785 — builds the <see cref="SlideshowModel"/>s from <see cref="TrainingSlideData"/>
/// (the native slides extracted from the artwork corpus's tm_*.mdl <c>slides</c> lists).
/// Native shows the pre-mission slideshow (tm_N_* namespace) before the mission and the
/// post-mission slideshow (tm_N_*_post) after it (WinTrek.cpp:2473-2510, OnButtonTrain
/// trainingscreen.cpp:182-198); the Introduction (TM1) IS the tm_1_introduction
/// post-slideshow — Training.cpp:85-89 starts no mission for it.
/// </summary>
public static class TrainingSlides
{
    private static SlideshowModel From(int missionId, bool post, string title) => new(
        TrainingSlideData.Slides(missionId, post)
            .Select(s => new SlideshowModel.Slide(s.Caption, null, s.SoundStem))
            .ToArray(),
        title);

    /// <summary>The Introduction (TM1): the tm_1_introduction slideshow. Native has no
    /// Mission1 goal graph — Training::StartMission returns false for
    /// c_TM_1_Introduction (Training.cpp:85-89) and OnButtonTrain routes TM1 to
    /// ScreenIDPostTrainSlideshow (trainingscreen.cpp:182-185).</summary>
    public static SlideshowModel Introduction => From(Tm1Introduction, post: true, "Introduction");

    /// <summary>Pre-mission slideshow (native tm_N_* namespace) — Dismiss starts the
    /// mission (TrainingSlideshow::Dismiss, TrainingSlideshow.cpp:30-50).</summary>
    public static SlideshowModel PreMission(int missionId, string name) =>
        From(missionId, post: false, name);

    /// <summary>Post-mission slideshow (native tm_N_*_post namespace) — Dismiss returns
    /// to the training screen (PostTrainingSlideshow::Dismiss:25-29).</summary>
    public static SlideshowModel PostMission(int missionId, string name) =>
        From(missionId, post: true, name);
}
