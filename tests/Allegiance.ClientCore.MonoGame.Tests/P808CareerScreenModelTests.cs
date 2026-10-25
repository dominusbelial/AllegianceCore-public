using Allegiance.ClientCore.MonoGame.Career;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P808b — screen-model tests for the character-info and leaderboard screens
/// (native WinTrek/CharInfoScreen.cpp / leaderboardscreen.cpp). Pure, no graphics
/// device: per-civ selection + edit toggle, and leaderboard sort/FindMe/search/
/// paging/ingest. These mirror the task's "client screen model tests".
/// </summary>
public sealed class P808CareerScreenModelTests
{
    // ---- Character-info screen (native CharInfoScreen.cpp:717-762,837-877) ----

    private static CareerDto.CareerInfo Info() => new("acc-1", "Alpha", "Hello", 150, new[]
    {
        new CareerDto.CareerCivStats(1, 2, 500, 1, 2, 100, 30, 3, 1, 1, 0, 1200),
        new CareerDto.CareerCivStats(2, 5, 3000, 0, 1, 150, 60, 8, 2, 2, 1, 5500),
    });

    [Fact]
    public void CharacterInfo_loads_civs_and_edits_description()
    {
        var m = new CharacterInfoModel();
        m.Load(Info());
        Assert.Equal("Alpha", m.Callsign);
        Assert.Equal("Hello", m.Description);
        Assert.Equal(new[] { "All", "Civ 1", "Civ 2" }, m.CivNames);

        // "All" row = sum of all civs (ClubMessages.h:79).
        var all = m.SelectedStats!;
        Assert.Equal(3500f, all.Score);
        Assert.Equal(11, all.Kills);
        Assert.Equal(150, all.Rating); // max

        // Per-civ selection (native OnCivChange).
        var civ1 = m.SelectCiv(1)!;
        Assert.Equal(1, civ1.Civ);
        Assert.Equal(500f, civ1.Score);
        Assert.Equal("Ensign", m.RankName); // CareerRanks.Name(2)

        // Edit/Apply toggle (native OnButtonEdit/OnButtonApply).
        Assert.False(m.Editing);
        m.BeginEdit();
        Assert.True(m.Editing);
        m.ApplyEdit();
        Assert.False(m.Editing);
    }

    [Fact]
    public void CharacterInfo_commit_description_adopts_text_and_leaves_edit_mode()
    {
        var m = new CharacterInfoModel();
        m.Load(Info());
        Assert.False(m.Editing);
        m.BeginEdit();
        Assert.True(m.Editing);
        m.CommitDescription("A new bio for Alpha");   // P808c OnButtonApply copy (CharInfoScreen.cpp:837-859)
        Assert.Equal("A new bio for Alpha", m.Description);
        Assert.False(m.Editing);
    }

    [Fact]
    public void Intro_main_menu_exposes_the_career_entry()
    {
        // P808c — the career screens are reached from the intro (the port's lobby
        // portal; native reaches them from the club lobby).
        Assert.Equal("Career", IntroScreenModel.MainLabels[^1]);
        Assert.Equal(IntroCommand.Career, IntroScreenModel.Activate(10));
    }

    // ---- Leaderboard screen (native leaderboardscreen.cpp) ----

    [Fact]
    public void Leaderboard_default_sort_is_score_and_position_click_is_ignored()
    {
        var m = new LeaderboardModel();
        Assert.Equal(CareerDto.LeaderBoardSort.Score, m.Sort()); // OverallRating default (leaderboardscreen.cpp:358)
        Assert.False(m.SelectSort(LeaderboardModel.StatsColumn.Position)); // ignored (leaderboardscreen.cpp:636-637)
        Assert.True(m.SelectSort(LeaderboardModel.StatsColumn.PlayerName));
        Assert.Equal(CareerDto.LeaderBoardSort.Name, m.Sort());
        Assert.True(m.SelectSort(LeaderboardModel.StatsColumn.BaseKills));
        Assert.Equal(CareerDto.LeaderBoardSort.BaseKills, m.Sort());
    }

    [Fact]
    public void Leaderboard_basis_selection_findme_search_top()
    {
        var m = new LeaderboardModel { OwnCallsign = "Me" };

        m.Top();
        Assert.Null(m.BuildQuery().Basis);      // top of list (leaderboardscreen.cpp:554-561)

        m.Search("Alice");
        Assert.Equal("Alice", m.BuildQuery().Basis);

        m.FindMe();
        Assert.Equal("Me", m.BuildQuery().Basis); // OnButtonFindMe (leaderboardscreen.cpp:512-519)

        m.SelectSort(LeaderboardModel.StatsColumn.Kills);
        Assert.Equal(CareerDto.LeaderBoardSort.Kills, m.BuildQuery().Sort);
    }

    [Fact]
    public void Leaderboard_ingest_centers_rows_and_selects_profile()
    {
        var entries = new List<CareerDto.LeaderboardEntry>();
        for (int i = 0; i < 30; i++)
            entries.Add(Entry(i));
        var m = new LeaderboardModel { OwnCallsign = "Player15" };
        m.FindMe();                                        // basis = Player15
        m.Ingest(new CareerDto.LeaderboardResponse(entries, entries[0], 16, null));

        Assert.Equal(30, m.Entries.Count);
        Assert.Equal(entries[0], m.EntryNumber1);
        Assert.Equal("Player15", m.BasisName);             // basis resolved (leaderboardscreen.cpp:818-826)
        Assert.Equal("acc-15", m.SelectedProfileAccountId); // profile nav target (account id)
        Assert.Equal(28, m.VisibleRows().Count);           // cLinesPerScreen rows
        // Basis (index 15) is centred: offset = max(0, min(30-28, 15-13)) = 2.
        Assert.Equal("Player2", m.VisibleRows()[0].Callsign);
    }

    [Fact]
    public void Leaderboard_page_up_moves_basis_when_above_the_window()
    {
        var entries = new List<CareerDto.LeaderboardEntry>();
        for (int i = 0; i < 30; i++) entries.Add(Entry(i));
        var m = new LeaderboardModel { OwnCallsign = "Player15" };
        m.FindMe();                                        // basis = Player15 → offset 2 after ingest
        m.Ingest(new CareerDto.LeaderboardResponse(entries, entries[0], 16, null));

        Assert.True(m.CanPageUp);                          // rows above the window exist
        Assert.True(m.PageUp());                           // CalcPageUpBasis = 0 (leaderboardscreen.cpp:563-569)
        Assert.Equal("Player0", m.BasisName);              // OnButtonPageUp (:576-588)
    }

    [Fact]
    public void Leaderboard_page_down_moves_basis_when_rows_remain_below()
    {
        var entries = new List<CareerDto.LeaderboardEntry>();
        for (int i = 0; i < 60; i++) entries.Add(Entry(i));
        var m = new LeaderboardModel();
        m.Top();                                           // basis = first player
        m.Ingest(new CareerDto.LeaderboardResponse(entries, entries[0], 1, null));

        Assert.True(m.CanPageDown);                        // rows below the window remain
        string before = m.BasisName;                       // Player0
        Assert.True(m.PageDown());                         // CalcPageDownBasis = 42 (:590-596)
        Assert.NotEqual(before, m.BasisName);              // OnButtonPageDown (:603-615)
    }

    private static CareerDto.LeaderboardEntry Entry(int i) => new(
        AccountId: "acc-" + i, Callsign: "Player" + i, Position: i + 1,
        TotalScore: 1000 - i, MinutesPlayed: 10 + i, BaseKills: 0, BaseCaptures: 0,
        Kills: i, Deaths: 0, Rank: 0, Rating: 0, Wins: 0, Losses: 0, GamesPlayed: 1,
        CommanderWins: 0, CommanderLosses: 0);

    [Fact]
    public void All_civilizations_line_shows_the_lobby_rank_not_zero()
    {
        var info = new CareerDto.CareerInfo("acct", "ArchonAlpha", "", 900,
        [
            new CareerDto.CareerCivStats(1, 2, 500, 1, 2, 100, 30, 3, 1, 1, 0, 1200),
            new CareerDto.CareerCivStats(2, 5, 3000, 0, 1, 150, 60, 8, 2, 2, 1, 5500),
        ], AllCivsRank: 4);
        var model = new CharacterInfoModel();
        model.Load(info, id => id == 1 ? "Iron Coalition" : null);
        Assert.Equal("Lieutenant", model.RankName);              // the "All" line: rank 4
        Assert.Equal(new[] { CharacterInfoModel.AllCivsLabel, "Iron Coalition", "Civ 2" }, model.CivNames);
    }
}
