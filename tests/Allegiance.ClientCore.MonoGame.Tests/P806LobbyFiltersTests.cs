using Allegiance.ClientCore.MonoGame;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P806 — the SERVER LIST's filters, sort columns and filter dialog against the
/// game metadata the directory now publishes (native <c>GameScreen</c>,
/// <c>WinTrek/gamescreen.cpp</c>: FilterDialogPopup :351-529,
/// GameScreenFilter::operator() :933-999, OnButtonBarGames :858-898,
/// UpdateGameList :1185-1238 and the per-column comparators :1023-1168).
///
/// <para>On master the eleven filter flags were DontCare-only placeholders and
/// columns 0/3/7 were not selectable; every test here pins a rule that needed the
/// listing's <see cref="GameParams"/>, so the file does not even compile against
/// master (no <c>ServerListing.Game</c>, no <c>LobbyFilterDialog</c>).</para>
/// </summary>
public sealed class P806LobbyFiltersTests
{
    private static GameParams Params(
        bool developments = true, int lives = GameParams.UnlimitedLives, bool scores = true,
        bool objectModel = true, bool inProgress = false, bool conquest = false,
        bool deathmatch = false, bool prosperity = false, bool countdown = false,
        bool artifacts = false, bool flags = false, bool territory = false,
        short minRank = -1, short maxRank = 1000)
        => new(developments, lives, scores, objectModel, inProgress, conquest, deathmatch,
            prosperity, countdown, artifacts, flags, territory, minRank, maxRank);

    private static ServerListing Listing(string name, GameParams? game = null, int players = 0,
        int maxPlayers = 16, string mission = "XXV_simple.igc", string core = "PCore001.igc",
        DateTimeOffset? registered = null) => new(
        Id: "id-" + name, Name: name, Address: "127.0.0.1", Port: 6777,
        PlayerCount: players, MaxPlayers: maxPlayers, MissionName: mission, CoreFile: core,
        Version: "0.1", RegisteredUtc: registered ?? DateTimeOffset.UnixEpoch,
        LastSeenUtc: DateTimeOffset.UnixEpoch, Game: game);

    // ── the eleven filters (GameScreenFilter::operator(), :933-999) ─────────

    [Fact]
    public void Every_native_flag_filters_against_the_published_metadata()
    {
        // The native predicate order is gamescreen.cpp:933-997 (developments,
        // lives, scores, zone, then the seven winning conditions).
        var g = new LobbyGameFilters();

        // developments (:933-937)
        g.Reset(); g.Developments = LobbyGameSort.BooleanFilter.No;
        Assert.True(g.Apply(Listing("a", Params(developments: false))));
        Assert.False(g.Apply(Listing("b", Params(developments: true))));

        // lives — LimitedLives() ⇔ iLives != 0x7fff (:939-943)
        g.Reset(); g.Lives = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(lives: 2))));
        Assert.False(g.Apply(Listing("b", Params(lives: GameParams.UnlimitedLives))));

        // scores count (:945-949)
        g.Reset(); g.ScoresCount = LobbyGameSort.BooleanFilter.No;
        Assert.True(g.Apply(Listing("a", Params(scores: false))));
        Assert.False(g.Apply(Listing("b", Params(scores: true))));

        // zone — WasObjectModelCreated (:951-956)
        g.Reset(); g.Zone = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(objectModel: true))));
        Assert.False(g.Apply(Listing("b", Params(objectModel: false))));

        // the seven winning conditions (:958-997)
        g.Reset(); g.Conquest = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(conquest: true))));
        Assert.False(g.Apply(Listing("b", Params())));
        g.Reset(); g.Deathmatch = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(deathmatch: true))));
        Assert.False(g.Apply(Listing("b", Params())));
        g.Reset(); g.Prosperity = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(prosperity: true))));
        Assert.False(g.Apply(Listing("b", Params())));
        g.Reset(); g.Countdown = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(countdown: true))));
        Assert.False(g.Apply(Listing("b", Params())));
        g.Reset(); g.Artifact = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(artifacts: true))));
        Assert.False(g.Apply(Listing("b", Params())));
        g.Reset(); g.Flags = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(flags: true))));
        Assert.False(g.Apply(Listing("b", Params())));
        g.Reset(); g.Territorial = LobbyGameSort.BooleanFilter.Yes;
        Assert.True(g.Apply(Listing("a", Params(territory: true))));
        Assert.False(g.Apply(Listing("b", Params())));

        // ...and the No half excludes a listing that HAS the bit (native tests
        // `(filter == Yes) != value`, so No means "must not have it").
        g.Reset(); g.Territorial = LobbyGameSort.BooleanFilter.No;
        Assert.True(g.Apply(Listing("a", Params())));
        Assert.False(g.Apply(Listing("b", Params(territory: true))));
    }

    [Fact]
    public void DontCare_passes_and_unknown_metadata_cannot_satisfy_a_set_flag()
    {
        var g = new LobbyGameFilters();
        // DontCare on a listing with no metadata: nothing is asserted.
        Assert.True(g.Apply(Listing("old")));

        // A set flag on a listing that publishes nothing cannot be proven either
        // way, so the listing is hidden (the pre-P806 behaviour — when the
        // directory carried no metadata — was to keep every flag DontCare).
        g.Goals_AllYes();
        Assert.False(g.Apply(Listing("old")));
        Assert.True(g.Apply(Listing("new", Params(conquest: true, deathmatch: true, prosperity: true,
            countdown: true, artifacts: true, flags: true, territory: true))));
    }

    // ── the sort columns (UpdateGameList :1185-1238, comparators :1023-1168) ─

    [Fact]
    public void All_eight_native_columns_are_selectable_in_header_order()
    {
        Assert.Equal(
            new[] { 0, 1, 2, 3, 4, 5, 6, 7 },
            LobbyGameSort.Columns);
        Assert.Equal("STATUS", LobbyGameSort.ColumnLabel(LobbyGameSort.ColStatus));
        Assert.Equal("SETTINGS", LobbyGameSort.ColumnLabel(LobbyGameSort.ColSettings));
        Assert.Equal("GAME", LobbyGameSort.ColumnLabel(LobbyGameSort.ColName));
        Assert.Equal("SKILL", LobbyGameSort.ColumnLabel(LobbyGameSort.ColSkill));
    }

    [Fact]
    public void Status_column_puts_open_waiting_games_first_then_full_running_ones()
    {
        // MissionStatusColor (:1002-1021): green while a slot is open, red when
        // full; StatusCompare (:1023-1044) then orders in-progress games first and
        // started ones last. Native's ascending meaning: a sorts after b when its
        // status is higher, when it is NOT in progress, ...
        var openWaiting = Listing("open-waiting", Params(inProgress: false), players: 1);
        var openRunning = Listing("open-running", Params(inProgress: true), players: 1);
        var fullWaiting = Listing("full-waiting", Params(inProgress: false), players: 16);
        var fullRunning = Listing("full-running", Params(inProgress: true), players: 16);

        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColStatus);
        var ordered = sort.Apply(new[] { fullRunning, fullWaiting, openRunning, openWaiting },
            (l, col) => LobbyGameSort.ListingKey(l, col));

        // green (open) before red (full); inside a colour, running before waiting.
        Assert.Equal(
            new[] { "open-running", "open-waiting", "full-running", "full-waiting" },
            ordered.Select(l => l.Name));
    }

    [Fact]
    public void Running_time_column_orders_in_progress_games_by_start_and_waiting_games_together()
    {
        // RunningTimeCompare (:1054-1065): in-progress games first, by timeStart
        // ascending; waiting games all compare equal (stable, at the end).
        var t0 = DateTimeOffset.UnixEpoch;
        var a = Listing("wa1", Params(inProgress: false), registered: t0);
        var b = Listing("run-late", Params(inProgress: true), registered: t0.AddMinutes(5));
        var c = Listing("run-early", Params(inProgress: true), registered: t0.AddMinutes(1));
        var d = Listing("wa2", Params(inProgress: false), registered: t0.AddMinutes(9));

        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColRunningTime);
        var ordered = sort.Apply(new[] { a, b, c, d },
            (l, col) => LobbyGameSort.ListingKey(l, col));
        Assert.Equal(new[] { "run-early", "run-late", "wa1", "wa2" }, ordered.Select(l => l.Name));
    }

    [Fact]
    public void Skill_column_orders_by_max_rank_then_min_rank()
    {
        // SkillCompare (:1067-1076): max rank first, min rank as the tie-break.
        var a = Listing("max5", Params(minRank: 0, maxRank: 5));
        var b = Listing("max9-min3", Params(minRank: 3, maxRank: 9));
        var c = Listing("max9-min1", Params(minRank: 1, maxRank: 9));

        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColSkill);
        var ordered = sort.Apply(new[] { b, a, c }, (l, col) => LobbyGameSort.ListingKey(l, col));
        Assert.Equal(new[] { "max5", "max9-min1", "max9-min3" }, ordered.Select(l => l.Name));
    }

    [Fact]
    public void Settings_column_orders_by_native_game_type_index_descending()
    {
        // SettingsCompare (:1135-1168) returns true (a AFTER b) when
        // GameTypeIndex(a) < GameTypeIndex(b), so a higher index sorts first —
        // and a mission matching no preset (the list-end index) sorts first of all.
        var conquest = Listing("conquest", Params(conquest: true));        // index 0
        var territorial = Listing("territorial", Params(territory: true)); // index 4
        var custom = Listing("custom", Params());                          // index 6

        Assert.Equal(0, conquest.Game!.GameTypeIndex);
        Assert.Equal(4, territorial.Game!.GameTypeIndex);
        Assert.Equal(NativeGameTypes.Types.Length, custom.Game!.GameTypeIndex);

        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColSettings);
        var ordered = sort.Apply(new[] { conquest, territorial, custom },
            (l, col) => LobbyGameSort.ListingKey(l, col));
        Assert.Equal(new[] { "custom", "territorial", "conquest" }, ordered.Select(l => l.Name));
    }

    [Fact]
    public void Game_type_table_matches_native_gametypes_cpp()
    {
        // gametypes.cpp:118-176: Conquest, Death Match, Capture the Flag,
        // Artifacts, Territorial, Experimental, in that order.
        Assert.Equal(new[]
        {
            "Conquest", "Death Match", "Capture the Flag",
            "Artifacts", "Territorial", "Experimental",
        }, NativeGameTypes.Types.Select(t => t.Name));

        // A port game with no win condition matches none of them — a "custom" game.
        Assert.Equal("custom", GameParams.GameTypeName(Params()));
        Assert.Equal("Conquest", GameParams.GameTypeName(Params(conquest: true)));
        Assert.Equal("Territorial", GameParams.GameTypeName(Params(territory: true)));
        Assert.Equal("Artifacts", GameParams.GameTypeName(Params(artifacts: true)));
    }

    [Fact]
    public void ResetFilters_restores_players_reversed_and_clears_every_flag()
    {
        // ResetFilters (:900-927) — unchanged, re-pinned with real metadata.
        var g = new LobbyGameFilters
        {
            Developments = LobbyGameSort.BooleanFilter.Yes,
            Territorial = LobbyGameSort.BooleanFilter.No,
            NameSubstring = "war",
        };
        g.Reset();
        Assert.Equal(LobbyGameSort.BooleanFilter.DontCare, g.Developments);
        Assert.Equal(LobbyGameSort.BooleanFilter.DontCare, g.Territorial);
        Assert.Equal("", g.NameSubstring);

        var sort = new LobbyGameSort();
        sort.Click(LobbyGameSort.ColSettings);
        sort.ResetFilters();
        var c = Assert.Single(sort.Stack);
        Assert.Equal(LobbyGameSort.ColPlayers, c.Column);
        Assert.True(c.Reversed);
    }

    // ── the filter dialog (FilterDialogPopup :351-529) ─────────────────────

    [Fact]
    public void Filter_dialog_has_the_eleven_native_combos_and_seeds_from_the_screen()
    {
        Assert.Equal(11, LobbyFilterDialog.Labels.Length);
        Assert.Equal("DEVELOPMENTS", LobbyFilterDialog.Labels[0]);
        Assert.Equal("ZONE", LobbyFilterDialog.Labels[3]);
        Assert.Equal("TERRITORIAL", LobbyFilterDialog.Labels[10]);

        var filters = new LobbyGameFilters { Developments = LobbyGameSort.BooleanFilter.No };
        var dialog = new LobbyFilterDialog();
        dialog.Open(filters);
        Assert.True(dialog.Active);
        Assert.Equal(LobbyGameSort.BooleanFilter.No, dialog[0]);
        Assert.Equal(LobbyGameSort.BooleanFilter.DontCare, dialog[1]);
    }

    [Fact]
    public void Filter_dialog_cycles_yes_no_dontcare_and_commits_only_on_ok()
    {
        // InitializeYesNoDontCare (:407-411): Yes → No → DontCare → Yes.
        Assert.Equal(LobbyGameSort.BooleanFilter.No, LobbyFilterDialog.Next(LobbyGameSort.BooleanFilter.Yes));
        Assert.Equal(LobbyGameSort.BooleanFilter.DontCare, LobbyFilterDialog.Next(LobbyGameSort.BooleanFilter.No));
        Assert.Equal(LobbyGameSort.BooleanFilter.Yes, LobbyFilterDialog.Next(LobbyGameSort.BooleanFilter.DontCare));

        var filters = new LobbyGameFilters();
        var dialog = new LobbyFilterDialog();
        dialog.Open(filters);
        dialog.Cycle(4);                                    // conquest → Yes
        Assert.Equal(LobbyGameSort.BooleanFilter.Yes, dialog[4]);
        dialog.CycleBackward(4);                            // back to DontCare
        dialog.CycleBackward(4);                            // DontCare → No
        Assert.Equal(LobbyGameSort.BooleanFilter.No, dialog[4]);

        // Cancel closes and leaves the screen's filters untouched (:523-529).
        dialog.Cancel();
        Assert.False(dialog.Active);
        Assert.Equal(LobbyGameSort.BooleanFilter.DontCare, filters.Conquest);

        // OK copies every combo (:500-521).
        dialog.Open(filters);
        dialog.Cycle(4);
        dialog.Commit(filters);
        Assert.False(dialog.Active);
        Assert.Equal(LobbyGameSort.BooleanFilter.Yes, filters.Conquest);
    }

    [Fact]
    public void Filter_dialog_reports_its_rows_and_cursor()
    {
        var dialog = new LobbyFilterDialog();
        dialog.Open(new LobbyGameFilters());
        dialog.MoveRow(3);
        Assert.Equal(3, dialog.Row);
        var text = dialog.Text();
        Assert.Contains("DEVELOPMENTS", text);
        Assert.Contains("> ZONE", text);
        // Row clamping: no wrap past the last row.
        dialog.MoveRow(100);
        Assert.Equal(10, dialog.Row);
    }

    // ── details panel (native OnButtonDetails :1308-1326) ──────────────────

    [Fact]
    public void Details_lines_show_the_carried_game_metadata_and_never_pilot_names()
    {
        var l = Listing("domino", Params(inProgress: true, conquest: true), players: 2);
        var lines = ServerDirectorySearch.DetailsLines(l);
        Assert.Contains(lines, s => s.StartsWith("TYPE:") && s.Contains("Conquest"));
        Assert.Contains(lines, s => s.StartsWith("STATE:") && s.Contains("running"));
        Assert.Contains(lines, s => s.StartsWith("GOALS:") && s.Contains("conquest"));
        Assert.Contains(lines, s => s.Contains("DEVEL:   on"));
        Assert.Contains(lines, s => s.StartsWith("PLAYERS: 2"));
        Assert.DoesNotContain(lines, s => s.Contains("PILOTS"));

        // A pre-P806 listing (no metadata) keeps the old panel.
        var old = ServerDirectorySearch.DetailsLines(Listing("old"));
        Assert.DoesNotContain(old, s => s.StartsWith("TYPE:"));
    }
}

/// <summary>Small helpers shared by the P806 model tests.</summary>
internal static class P806FilterExtensions
{
    /// <summary>Every winning-condition flag set to Yes (a listing must match all).</summary>
    public static void Goals_AllYes(this LobbyGameFilters f)
    {
        f.Conquest = LobbyGameSort.BooleanFilter.Yes;
        f.Deathmatch = LobbyGameSort.BooleanFilter.Yes;
        f.Prosperity = LobbyGameSort.BooleanFilter.Yes;
        f.Countdown = LobbyGameSort.BooleanFilter.Yes;
        f.Artifact = LobbyGameSort.BooleanFilter.Yes;
        f.Flags = LobbyGameSort.BooleanFilter.Yes;
        f.Territorial = LobbyGameSort.BooleanFilter.Yes;
    }
}
