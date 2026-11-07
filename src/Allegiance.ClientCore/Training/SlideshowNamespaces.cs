namespace Allegiance.ClientCore.Training;

/// <summary>
/// P943 — the native slideshow namespaces. WinTrek routes ScreenIDTrainSlideshow
/// (<c>WinTrek/WinTrek.cpp:2473-2493</c>) and ScreenIDPostTrainSlideshow
/// (<c>WinTrek/WinTrek.cpp:2495-2517</c>) to the <c>tm_*</c> MDL named by the
/// mission number; a blank entry means that mission has no slideshow of that kind
/// (mission 7 has none; the post table's 9 and 10 are blank / reuse tm_6).
/// </summary>
public static class SlideshowNamespaces
{
    /// <summary>Native pre-mission namespace table (WinTrek.cpp:2478-2489).</summary>
    private static readonly string?[] Pre =
    {
        null,                       // 0 — unused
        null,                       // 1 — Introduction has no pre slideshow
        "tm_2_basic_flight",
        "tm_3_basic_weaponry",
        "tm_4_enemy_engagement",
        "tm_5_command_view",
        "tm_6_practice_arena",
        null,                       // 7 — never used (native blank entry)
        "tm_8_nanite",
    };

    /// <summary>Native post-mission namespace table (WinTrek.cpp:2500-2513).</summary>
    private static readonly string?[] Post =
    {
        null,                       // 0 — unused
        "tm_1_introduction",        // 1 — the Introduction IS the post slideshow
        "tm_2_basic_flight_post",
        "tm_3_basic_weaponry_post",
        "tm_4_enemy_engagement_post",
        "tm_5_command_view_post",
        "tm_6_practice_arena_post",
        null,                       // 7 — never used (native blank entry)
        "tm_8_nanite_post",
        null,                       // 9 — before the 2017 FreeFlight entry
        "tm_6_practice_arena_post", // 10 — FreeFlight reuses the tm_6 post slides
    };

    /// <summary>The MDL namespace for a mission's pre/post slideshow, or null when
    /// native has none.</summary>
    public static string? For(int missionId, bool post)
    {
        var table = post ? Post : Pre;
        return missionId >= 0 && missionId < table.Length ? table[missionId] : null;
    }

    /// <summary>Every (mission, post, namespace) the corpus ships, in native order —
    /// the inventory the artwork test walks.</summary>
    public static IReadOnlyList<(int Mission, bool Post, string Namespace)> All()
    {
        var rows = new List<(int, bool, string)>();
        for (int m = 1; m <= 8; m++)
        {
            if (For(m, post: false) is { } pre) rows.Add((m, false, pre));
            if (For(m, post: true) is { } post) rows.Add((m, true, post));
        }
        return rows;
    }
}
