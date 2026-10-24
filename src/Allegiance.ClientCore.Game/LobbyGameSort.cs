using System;
using System.Collections.Generic;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P806 — pure model of the SERVER LIST's column sort stack and game filters
/// (native <c>GameScreen</c>, <c>WinTrek/gamescreen.cpp</c>).
///
/// <para><b>Sort stack</b> (OnButtonBarGames, gamescreen.cpp:858-898): clicking
/// a header column makes it the LAST criterion of a stack — an earlier entry for
/// the same column is removed first — and its reverse flag comes from the
/// header's Checked2 state (checked2 → normal, unchecked → reversed; clicking
/// the current top column toggles its direction). UpdateGameList
/// (gamescreen.cpp:1185-1238) applies the stack in order, each pass a stable
/// sort, so the LAST criterion is the primary key and earlier criteria are its
/// tie-breakers; <see cref="Apply{T}"/> reproduces exactly that with stable
/// OrderBy passes applied in stack order. Reverse wraps the comparator
/// (<c>SortingList</c>/<c>ReverseCompare</c>, gamescreen.cpp:1172-1184).</para>
///
/// <para><b>Columns.</b> Native's eight (KGJV #114, gamescreen.cpp:1193-1237):
/// 0 status, 1 name, 2 running time, 3 skill, 4 server, 5 core, 6 players,
/// 7 settings. P806 makes all eight selectable: the directory listing now
/// carries the native <see cref="GameParams"/> the three previously missing
/// columns read — <c>InProgress</c>/slot state (status), <c>MinRank</c>/
/// <c>MaxRank</c> (skill) and the game-type settings (settings). The
/// comparators are the native ones, mapped to an ascending key (native compares
/// "true ⇒ the first value sorts AFTER the second", so an ascending key is the
/// inverse of the native predicate):
/// <list type="bullet">
/// <item>col 0 — <c>StatusCompare</c> (gamescreen.cpp:1023-1044): green before
/// yellow before red (<c>MissionStatusColor</c>, :1002-1021), in-progress games
/// first, object-model games after lobby games, started-stage games last. The
/// port has no squads, so native's squad tie-break is constant.</item>
/// <item>col 1 — <c>NameCompare</c> (:1046-1052), case-insensitive ascending.</item>
/// <item>col 2 — <c>RunningTimeCompare</c> (:1054-1065): in-progress games
/// first, then by start time ascending; waiting games compare equal.</item>
/// <item>col 3 — <c>SkillCompare</c> (:1067-1076): max rank then min rank
/// ascending. Native's predicate also returns true for two games whose rank
/// window is IDENTICAL (an inconsistent comparator — a native bug); a stable
/// ascending key is used instead (see the P806 report).</item>
/// <item>col 4 — <c>ServerCompare</c> (:1109-1116), case-insensitive ascending.</item>
/// <item>col 5 — <c>CoreCompare</c> (:1118-1125), case-insensitive ascending.</item>
/// <item>col 6 — <c>NumPlayersCompare</c> (:1127-1133) ascending.</item>
/// <item>col 7 — <c>SettingsCompare</c> (:1135-1168): the first matching
/// <c>GameType</c> index, DESCENDING (native returns true — "after" — when
/// <c>index(a) &lt; index(b)</c>), so higher indices sort first and a custom
/// (unmatched) game sorts at the top.</item>
/// </list></para>
///
/// <para><b>Filters.</b> Native's filter popup (FilterDialogPopup,
/// gamescreen.cpp:351-529; GameScreenFilter::operator(), gamescreen.cpp:933-999)
/// holds eleven Yes/No/DontCare flags — developments, lives, scores-count, zone,
/// conquest, deathmatch, prosperity, countdown, artifact, flags, territorial —
/// plus the game-name substring. All eleven now evaluate the published
/// <see cref="GameParams"/> exactly like the native predicate; the substring is
/// the port's existing <see cref="ServerListing.Matches"/> rule. ResetFilters
/// (gamescreen.cpp:900-927) additionally resets the sort stack to a single
/// reversed PLAYERS criterion (column 6, "the big numbers on top", WLP 2005).</para>
/// </summary>
public sealed class LobbyGameSort
{
    /// <summary>Native's Yes/No/DontCare combo values (InitializeYesNoDontCare,
    /// gamescreen.cpp:407-411; BooleanFilterState).</summary>
    public enum BooleanFilter { Yes, No, DontCare }

    /// <summary>Native column ids (KGJV #114).</summary>
    public const int ColStatus = 0, ColName = 1, ColRunningTime = 2, ColSkill = 3,
        ColServer = 4, ColCore = 5, ColPlayers = 6, ColSettings = 7;

    /// <summary>Every native column, in header order (P806 — all eight are
    /// selectable because the directory now carries their inputs).</summary>
    public static readonly int[] Columns =
    {
        ColStatus, ColName, ColRunningTime, ColSkill, ColServer, ColCore, ColPlayers, ColSettings,
    };

    /// <summary>Pre-P806 name, kept for existing call sites/tests: the columns the
    /// port can express. P806 makes that all eight.</summary>
    public static readonly int[] ExpressibleColumns = Columns;

    public static string ColumnLabel(int column) => column switch
    {
        ColStatus => "STATUS",
        ColName => "GAME",
        ColRunningTime => "RUNNING",
        ColSkill => "SKILL",
        ColServer => "SERVER",
        ColCore => "CORE",
        ColPlayers => "PLAYERS",
        ColSettings => "SETTINGS",
        _ => "?",
    };

    /// <summary>One stack entry: native column id + reverse flag
    /// (m_vuSorts/m_vbReversedSorts).</summary>
    public readonly record struct Criterion(int Column, bool Reversed);

    private readonly List<Criterion> _stack = new();

    /// <summary>The current sort stack, first (oldest) to last (primary).</summary>
    public IReadOnlyList<Criterion> Stack => _stack;

    /// <summary>OnButtonBarGames (gamescreen.cpp:858-898): the clicked column
    /// becomes the LAST criterion — any earlier entry for it is removed — and
    /// its reverse flag toggles when it was already the (reversed-ness
    /// tracked by) top entry's Checked2 state: native checks the header's
    /// Checked2 for THIS column (RefreshButtonBarGames sets Checked on the top
    /// criterion when reversed, Checked2 otherwise), so clicking the current
    /// top column flips its direction and clicking any other column starts it
    /// unreversed.</summary>
    public void Click(int column)
    {
        // RefreshButtonBarGames paints the TOP criterion Checked when reversed
        // and Checked2 otherwise (gamescreen.cpp:846-856); OnButtonBarGames reads
        // THIS column's Checked2 — set only while it is the top criterion in its
        // NORMAL state — so re-clicking the top column flips its direction and a
        // fresh column starts unreversed.
        bool wasTopNormal = _stack.Count > 0
            && _stack[^1].Column == column && !_stack[^1].Reversed;
        _stack.RemoveAll(c => c.Column == column);
        _stack.Add(new Criterion(column, Reversed: wasTopNormal));
    }

    /// <summary>ResetFilters (gamescreen.cpp:900-927): every filter to DontCare,
    /// the name substring cleared, and the sort stack reset to ONE criterion —
    /// players (column 6), reversed (the big numbers on top).</summary>
    public void ResetFilters() => ApplyReset(this);

    /// <summary>Which header column the header cursor sits on (the port's
    /// keyboard stand-in for native's mouse click).</summary>
    public int CursorIndex { get; set; }

    /// <summary>The header column currently under the cursor.</summary>
    public int CursorColumn => Columns[Math.Clamp(CursorIndex, 0, Columns.Length - 1)];

    /// <summary>Move the header cursor (Left/Right), clamped to the expressible
    /// columns.</summary>
    public void MoveCursor(int delta) =>
        CursorIndex = Math.Clamp(CursorIndex + delta, 0, Columns.Length - 1);

    /// <summary>Apply the stack to <paramref name="items"/>: one stable ordered
    /// pass per criterion in stack order (UpdateGameList's sequential
    /// SortingList wraps, so the LAST criterion dominates and earlier ones
    /// break its ties). The key selector maps an item to the comparable value
    /// of a native column.</summary>
    public IReadOnlyList<T> Apply<T>(IReadOnlyList<T> items, Func<T, int, IComparable> keyOf)
    {
        IEnumerable<T> current = items;
        foreach (var (column, reversed) in _stack)
        {
            current = reversed
                ? current.OrderByDescending(x => keyOf(x, column))
                : current.OrderBy(x => keyOf(x, column));
        }
        return current.ToList();
    }

    /// <summary>The standard <see cref="ServerListing"/> key mapping for the
    /// native columns (see the type remarks for each comparator).</summary>
    public static IComparable ListingKey(ServerListing listing, int column) => column switch
    {
        // StatusCompare — MissionStatusColor then InProgress/WasObjectModelCreated/
        // stage, packed ascending (all fields are small).
        ColStatus => StatusKey(listing),
        ColName => listing.MissionName ?? string.Empty,
        // RunningTimeCompare — in-progress first, then start time; waiting games
        // compare equal (long.MaxValue keeps them together at the end).
        ColRunningTime => listing.Game?.InProgress == true
            ? listing.RegisteredUtc.UtcTicks
            : long.MaxValue,
        // SkillCompare — max rank then min rank, ascending.
        ColSkill => listing.Game is { } sg
            ? ((long)sg.MaxRank << 16) | (ushort)sg.MinRank
            : long.MinValue,
        ColServer => listing.Name ?? string.Empty,
        ColCore => listing.CoreFile ?? string.Empty,
        ColPlayers => listing.PlayerCount,
        // SettingsCompare — descending GameTypeIndex (native: index(a) < index(b)
        // means a sorts AFTER b), so negate. An unknown game is the list-end
        // index and therefore sorts first, exactly like native.
        ColSettings => listing.Game is { } pg ? -pg.GameTypeIndex : int.MaxValue,
        _ => 0,
    };

    /// <summary>Native <c>StatusCompare</c> (gamescreen.cpp:1023-1044) as an
    /// ascending key. Port simplifications, both native-constant: no squads
    /// (HasPlayerSquad is false for every listing) and no guaranteed-slot state
    /// (native's yellow branch is commented out, gamescreen.cpp:1008-1012).</summary>
    private static long StatusKey(ServerListing listing)
    {
        // MissionStatusColor: green while any slot is available, else red.
        // MaxPlayers == 0 means "unknown/unbounded" on this directory (every slot
        // open), the same reading FindGames applies to a 0 cap.
        int status = listing.MaxPlayers > 0 && listing.PlayerCount >= listing.MaxPlayers ? 1 : 0;
        bool inProgress = listing.Game?.InProgress == true;
        bool objectModel = listing.Game?.ObjectModelCreated == true;
        // Native's final tie-break is (a.stage == STARTED) && (b.stage != STARTED):
        // a started game sorts after a non-started one. The published bit is
        // InProgress, which starts at Starting — use it as the stage proxy.
        return ((long)status << 24)
             | ((inProgress ? 0L : 1L) << 16)
             | ((objectModel ? 1L : 0L) << 8)
             | (inProgress ? 1L : 0L);
    }

    internal static void ApplyReset(LobbyGameSort sort)
    {
        sort._stack.Clear();
        sort._stack.Add(new Criterion(ColPlayers, Reversed: true));
    }

    /// <summary>The header-bar text: the columns, the cursor cell bracketed,
    /// stack members marked with their direction (^ ascending, v reversed) —
    /// the port's keyboard stand-in for native's radio-checked header
    /// (RefreshButtonBarGames, gamescreen.cpp:846-856).</summary>
    public string HeaderText()
    {
        var cells = Columns.Select((column, i) =>
        {
            string label = ColumnLabel(column);
            string cell = i == CursorIndex ? $"[{label}]" : label;
            var crit = _stack.FirstOrDefault(s => s.Column == column);
            if (crit != default && _stack.Contains(crit))
                cell += crit.Reversed ? " v" : " ^";
            return cell;
        });
        return string.Join(" ", cells) + "   (LEFT/RIGHT choose, SPACE sort)";
    }

    /// <summary>One-line description of the active stack (for the log).</summary>
    public string Label() => Stack.Count == 0
        ? "(unsorted)"
        : string.Join(" then ", Stack.Select(c =>
            $"{ColumnLabel(c.Column)}{(c.Reversed ? " reversed" : "")}"));
}

/// <summary>
/// P806 — the port's stand-in for native's GameScreenFilter state
/// (FilterDialogPopup, gamescreen.cpp:351-529): the eleven Yes/No/DontCare
/// flags plus the game-name substring, all defaulting to
/// <see cref="LobbyGameSort.BooleanFilter.DontCare"/>/empty exactly like
/// ResetFilters (gamescreen.cpp:900-914) does.
///
/// <para><b>Evaluation.</b> <see cref="Apply"/> is the native predicate
/// (GameScreenFilter::operator(), gamescreen.cpp:933-999): a DontCare flag
/// excludes nothing; a Yes/No flag must agree with the listing's published
/// <see cref="GameParams"/>. A listing that publishes no settings
/// (<see cref="ServerListing.Game"/> null) cannot prove either side, so a
/// set flag hides it — the honest reading of "the directory has no
/// metadata" and the reason the pre-P806 port could only carry the flags as
/// placeholders. The name substring is <see cref="ServerListing.Matches"/>.</para>
/// </summary>
public sealed class LobbyGameFilters
{
    public LobbyGameSort.BooleanFilter Developments { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Lives { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter ScoresCount { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Zone { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Conquest { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Deathmatch { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Prosperity { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Countdown { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Artifact { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Flags { get; set; } = LobbyGameSort.BooleanFilter.DontCare;
    public LobbyGameSort.BooleanFilter Territorial { get; set; } = LobbyGameSort.BooleanFilter.DontCare;

    /// <summary>The game-name substring (m_strGameNameSubstring). Case handled
    /// by <see cref="ServerListing.Matches"/>.</summary>
    public string NameSubstring { get; set; } = "";

    /// <summary>ResetFilters (gamescreen.cpp:900-914): every flag DontCare, the
    /// substring emptied.</summary>
    public void Reset()
    {
        Developments = Lives = ScoresCount = Zone = LobbyGameSort.BooleanFilter.DontCare;
        Conquest = Deathmatch = Prosperity = Countdown = LobbyGameSort.BooleanFilter.DontCare;
        Artifact = Flags = Territorial = LobbyGameSort.BooleanFilter.DontCare;
        NameSubstring = "";
    }

    /// <summary>GameScreenFilter::operator() (gamescreen.cpp:933-999): every
    /// flag that is not DontCare must agree with the listing's settings, then
    /// the name substring must be contained.</summary>
    public bool Apply(ServerListing listing)
    {
        // gamescreen.cpp:933-937 — developments.
        if (!Agrees(Developments, listing.Game?.AllowDevelopments)) return false;
        // :939-943 — lives (LimitedLives() ⇔ iLives != 0x7fff).
        if (!Agrees(Lives, listing.Game?.LimitedLives)) return false;
        // :945-949 — scores count.
        if (!Agrees(ScoresCount, listing.Game?.ScoresCount)) return false;
        // :951-956 — the native "Zone" column = WasObjectModelCreated().
        if (!Agrees(Zone, listing.Game?.ObjectModelCreated)) return false;
        // :958-997 — the seven winning conditions.
        if (!Agrees(Conquest, listing.Game?.GoalConquest)) return false;
        if (!Agrees(Deathmatch, listing.Game?.GoalDeathmatch)) return false;
        if (!Agrees(Prosperity, listing.Game?.GoalProsperity)) return false;
        if (!Agrees(Countdown, listing.Game?.GoalCountdown)) return false;
        if (!Agrees(Artifact, listing.Game?.GoalArtifacts)) return false;
        if (!Agrees(Flags, listing.Game?.GoalFlags)) return false;
        if (!Agrees(Territorial, listing.Game?.GoalTerritory)) return false;
        // :997-999 — the game-name substring.
        return listing.Matches(NameSubstring);
    }

    /// <summary>One native flag test: DontCare passes; Yes/No require the
    /// published value to be known and equal. Unknown metadata fails a set flag.</summary>
    public static bool Agrees(LobbyGameSort.BooleanFilter filter, bool? value) => filter switch
    {
        LobbyGameSort.BooleanFilter.DontCare => true,
        LobbyGameSort.BooleanFilter.Yes => value == true,
        LobbyGameSort.BooleanFilter.No => value == false,
        _ => true,
    };
}

/// <summary>
/// P806 — the server list's FILTER DIALOG (native <c>FilterDialogPopup</c>,
/// <c>WinTrek/gamescreen.cpp:351-529</c>): the eleven Yes/No/DontCare combos in
/// native's order, edited on a working copy and committed on OK
/// (<c>OnButtonOK</c> :500-521 copies every combo into the screen's members and
/// calls <c>UpdateGameList</c>); Cancel (:523-529) closes without touching them.
/// The game-name edit box is the port's existing F filter line, which sets
/// <see cref="LobbyGameFilters.NameSubstring"/> live — the same rule
/// (gamescreen.cpp:997-999), so the dialog itself only carries the eleven flags.
/// Pure model: the screen owns input + drawing.
/// </summary>
public sealed class LobbyFilterDialog
{
    /// <summary>The flag rows in native popup construction order
    /// (gamescreen.cpp:355-400, 407-411).</summary>
    public static readonly string[] Labels =
    {
        "DEVELOPMENTS", "LIVES", "SCORES COUNT", "ZONE",
        "CONQUEST", "DEATHMATCH", "PROSPERITY", "COUNTDOWN",
        "ARTIFACT", "FLAGS", "TERRITORIAL",
    };

    /// <summary>Number of rows (native's eleven combos).</summary>
    public int Count => Labels.Length;

    /// <summary>The row the cursor sits on.</summary>
    public int Row { get; set; }

    /// <summary>True while the popup owns the keyboard.</summary>
    public bool Active { get; private set; }

    private readonly LobbyGameSort.BooleanFilter[] _draft = new LobbyGameSort.BooleanFilter[Labels.Length];

    /// <summary>The working-copy value of a row (native m_pcomboXxx->GetSelection()
    /// before OK).</summary>
    public LobbyGameSort.BooleanFilter this[int row] => _draft[row];

    /// <summary>The next value in native's combo order (Yes, No, DontCare —
    /// InitializeYesNoDontCare, gamescreen.cpp:407-411).</summary>
    public static LobbyGameSort.BooleanFilter Next(LobbyGameSort.BooleanFilter v) => v switch
    {
        LobbyGameSort.BooleanFilter.Yes => LobbyGameSort.BooleanFilter.No,
        LobbyGameSort.BooleanFilter.No => LobbyGameSort.BooleanFilter.DontCare,
        _ => LobbyGameSort.BooleanFilter.Yes,
    };

    /// <summary>FilterDialogPopup::SetContainer (gamescreen.cpp:449-487): seed
    /// every combo from the screen's current flags and focus the first row.</summary>
    public void Open(LobbyGameFilters filters)
    {
        _draft[0] = filters.Developments;
        _draft[1] = filters.Lives;
        _draft[2] = filters.ScoresCount;
        _draft[3] = filters.Zone;
        _draft[4] = filters.Conquest;
        _draft[5] = filters.Deathmatch;
        _draft[6] = filters.Prosperity;
        _draft[7] = filters.Countdown;
        _draft[8] = filters.Artifact;
        _draft[9] = filters.Flags;
        _draft[10] = filters.Territorial;
        Row = 0;
        Active = true;
    }

    /// <summary>Move the row cursor (clamped, like the combo list).</summary>
    public void MoveRow(int delta) => Row = Math.Clamp(Row + delta, 0, Labels.Length - 1);

    /// <summary>Cycle a row's value forward (the combo's selection).</summary>
    public void Cycle(int row) => _draft[Math.Clamp(row, 0, Labels.Length - 1)] =
        Next(_draft[Math.Clamp(row, 0, Labels.Length - 1)]);

    /// <summary>Cycle a row's value backward (Yes→DontCare→No→Yes).</summary>
    public void CycleBackward(int row)
    {
        int i = Math.Clamp(row, 0, Labels.Length - 1);
        _draft[i] = _draft[i] switch
        {
            LobbyGameSort.BooleanFilter.Yes => LobbyGameSort.BooleanFilter.DontCare,
            LobbyGameSort.BooleanFilter.No => LobbyGameSort.BooleanFilter.Yes,
            _ => LobbyGameSort.BooleanFilter.No,
        };
    }

    /// <summary>OnButtonOK (gamescreen.cpp:500-521): close and copy every combo
    /// into the screen's filter state.</summary>
    public void Commit(LobbyGameFilters filters)
    {
        filters.Developments = _draft[0];
        filters.Lives = _draft[1];
        filters.ScoresCount = _draft[2];
        filters.Zone = _draft[3];
        filters.Conquest = _draft[4];
        filters.Deathmatch = _draft[5];
        filters.Prosperity = _draft[6];
        filters.Countdown = _draft[7];
        filters.Artifact = _draft[8];
        filters.Flags = _draft[9];
        filters.Territorial = _draft[10];
        Active = false;
    }

    /// <summary>OnButtonCancel (gamescreen.cpp:523-529): close, keep the draft
    /// discarded (the screen's flags are untouched).</summary>
    public void Cancel() => Active = false;

    /// <summary>The popup text: one row per combo, the cursor row bracketed.</summary>
    public string Text()
    {
        var lines = new List<string>(Labels.Length + 2) { "FILTER GAMES" };
        for (int i = 0; i < Labels.Length; i++)
        {
            string mark = i == Row ? ">" : " ";
            lines.Add($"{mark} {Labels[i],-14} {_draft[i]}");
        }
        lines.Add("UP/DOWN row  LEFT/RIGHT value  ENTER ok  ESC cancel");
        return string.Join('\n', lines);
    }
}
