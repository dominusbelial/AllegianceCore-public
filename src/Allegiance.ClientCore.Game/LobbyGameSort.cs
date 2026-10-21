using System;
using System.Collections.Generic;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P761 — pure model of the SERVER LIST's column sort stack and game filters
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
/// 7 settings. The port's server records (<see cref="ServerListing"/>) can
/// express five of them:</para>
/// <list type="bullet">
/// <item>1 name — the game/mission name (MissionName; native NameCompare is
/// case-insensitive, gamescreen.cpp:1046-1052);</item>
/// <item>2 running time — the mission's start time (RegisteredUtc; native
/// RunningTimeCompare orders in-progress games by timeStart ascending,
/// gamescreen.cpp:1054-1065);</item>
/// <item>4 server — the server display name (Name; ServerCompare,
/// gamescreen.cpp:1109-1116);</item>
/// <item>5 core — the static-core file (CoreFile; CoreCompare,
/// gamescreen.cpp:1118-1125);</item>
/// <item>6 players — the player count (PlayerCount; NumPlayersCompare,
/// gamescreen.cpp:1127-1133).</item>
/// </list>
/// <para>Columns 0 (status), 3 (skill/max-rank range) and 7 (game-type
/// settings) read mission metadata the lobby directory's
/// <c>ServerListingDto</c> does not carry — they are not selectable here
/// (listed in the P761 report).</para>
///
/// <para><b>Filters.</b> Native's filter popup (FilterDialogPopup,
/// gamescreen.cpp:351-529; GameScreenFilter::operator(), gamescreen.cpp:933-999)
/// holds eleven Yes/No/DontCare flags — developments, lives, scores-count, zone,
/// conquest, deathmatch, prosperity, countdown, artifact, flags, territorial —
/// plus the game-name substring. Only the substring can be evaluated against a
/// <see cref="ServerListing"/> (it is the port's existing
/// <see cref="ServerListing.Matches"/> rule); the eleven boolean flags read
/// mission metadata the directory does not carry, so the port's filter model
/// keeps their DontCare semantics (reset sets them all back) but they exclude
/// nothing. ResetFilters (gamescreen.cpp:900-927) additionally resets the sort
/// stack to a single reversed PLAYERS criterion (column 6, "the big numbers on
/// top", WLP 2005).</para>
/// </summary>
public sealed class LobbyGameSort
{
    /// <summary>Native's Yes/No/DontCare combo values (InitializeYesNoDontCare,
    /// gamescreen.cpp:407-411; BooleanFilterState).</summary>
    public enum BooleanFilter { Yes, No, DontCare }

    /// <summary>Native column ids (KGJV #114).</summary>
    public const int ColStatus = 0, ColName = 1, ColRunningTime = 2, ColSkill = 3,
        ColServer = 4, ColCore = 5, ColPlayers = 6, ColSettings = 7;

    /// <summary>The columns the port's server records can express, in header
    /// order (the rest of native's eight are not selectable — see remarks).</summary>
    public static readonly int[] ExpressibleColumns =
    {
        ColName, ColRunningTime, ColServer, ColCore, ColPlayers,
    };

    public static string ColumnLabel(int column) => column switch
    {
        ColName => "GAME",
        ColRunningTime => "RUNNING",
        ColServer => "SERVER",
        ColCore => "CORE",
        ColPlayers => "PLAYERS",
        ColStatus => "STATUS",
        ColSkill => "SKILL",
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
    public int CursorColumn => ExpressibleColumns[Math.Clamp(CursorIndex, 0, ExpressibleColumns.Length - 1)];

    /// <summary>Move the header cursor (Left/Right), clamped to the expressible
    /// columns.</summary>
    public void MoveCursor(int delta) =>
        CursorIndex = Math.Clamp(CursorIndex + delta, 0, ExpressibleColumns.Length - 1);

    /// <summary>Apply the stack to <paramref name="items"/>: one stable ordered
    /// pass per criterion in stack order (UpdateGameList's sequential
    /// SortingList wraps, so the LAST criterion dominates and earlier ones
    /// break its ties). The key selector maps an item to the comparable value
    /// of a native column; unexpressible columns sort equal (stable).</summary>
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
    /// expressible columns (native comparators, see the type remarks).</summary>
    public static IComparable ListingKey(ServerListing listing, int column) => column switch
    {
        ColName => listing.MissionName ?? string.Empty,
        ColRunningTime => listing.RegisteredUtc.UtcTicks,
        ColServer => listing.Name ?? string.Empty,
        ColCore => listing.CoreFile ?? string.Empty,
        ColPlayers => listing.PlayerCount,
        _ => 0,   // unexpressible column: sorts equal (stable pass)
    };

    internal static void ApplyReset(LobbyGameSort sort)
    {
        sort._stack.Clear();
        sort._stack.Add(new Criterion(ColPlayers, Reversed: true));
    }

    /// <summary>The header-bar text: the expressible columns, the cursor cell
    /// bracketed, stack members marked with their direction (^ ascending,
    /// v reversed) — the port's keyboard stand-in for native's radio-checked
    /// header (RefreshButtonBarGames, gamescreen.cpp:846-856).</summary>
    public string HeaderText()
    {
        var cells = ExpressibleColumns.Select((column, i) =>
        {
            string label = ColumnLabel(column);
            string cell = i == CursorIndex ? $"[{label}]" : label;
            var crit = _stack.FirstOrDefault(s => s.Column == column);
            if (crit != default && _stack.Contains(crit))
                cell += crit.Reversed ? " v" : " ^";
            return cell;
        });
        return string.Join("   ", cells) + "   (LEFT/RIGHT choose, SPACE sort)";
    }

    /// <summary>One-line description of the active stack (for the log).</summary>
    public string Label() => Stack.Count == 0
        ? "(unsorted)"
        : string.Join(" then ", Stack.Select(c =>
            $"{ColumnLabel(c.Column)}{(c.Reversed ? " reversed" : "")}"));
}

/// <summary>
/// P761 — the port's stand-in for native's GameScreenFilter state
/// (FilterDialogPopup, gamescreen.cpp:351-529): the eleven Yes/No/DontCare
/// flags plus the game-name substring, all defaulting to
/// <see cref="LobbyGameSort.BooleanFilter.DontCare"/>/empty exactly like
/// ResetFilters (gamescreen.cpp:900-914) does.
///
/// <para><b>Honest scope note.</b> The predicate
/// (GameScreenFilter::operator(), gamescreen.cpp:933-999) reads mission
/// metadata — AllowDevelopments/LimitedLives/ScoresCount/WasObjectModelCreated
/// and the seven goal flags — that the lobby directory's
/// <see cref="ServerListing"/> does not carry. <see cref="Apply"/> therefore
/// evaluates ONLY the name substring (via <see cref="ServerListing.Matches"/>,
/// the port's existing faithful port of the native substring rule); the eleven
/// flags keep their DontCare default and exclude nothing (a DontCare flag also
/// excludes nothing natively). They exist in the model so a future directory
/// that carries the metadata can flip them without reshaping the type; the
/// inexpressible set is listed in the P761 report.</para>
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

    /// <summary>The port-expressible slice of native's filter predicate: the
    /// game-name substring (gamescreen.cpp:997-999 → ServerListing.Matches).
    /// The eleven boolean flags are DontCare-only until the directory carries
    /// their metadata (see the type remarks).</summary>
    public bool Apply(ServerListing listing) => listing.Matches(NameSubstring);
}
