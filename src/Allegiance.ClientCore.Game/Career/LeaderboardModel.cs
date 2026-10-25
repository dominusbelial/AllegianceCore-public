namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Pure, unit-testable model of the leaderboard screen — the port of native
/// <c>WinTrek/leaderboardscreen.cpp</c>: column sort (<c>:633-708</c>),
/// search/FindMe/Top basis selection (<c>:435-519,554-561</c>), half-screen
/// paging around the basis (<c>:563-630</c>), and the returned-window ingest +
/// visible-row centering (<c>:710-863</c>). Free of MonoGame so every native
/// navigation path is testable headless.
/// </summary>
public sealed class LeaderboardModel
{
    public const int LinesPerScreen = 28;          // cLinesPerScreen (leaderboardscreen.cpp:47)
    public const int MaxReturned = LinesPerScreen * 3; // native asserts cEntries <= cLinesPerScreen*3 (:788-789)

    /// <summary>Native <c>StatsColumn</c> (<c>leaderboardscreen.cpp:59-76</c>).</summary>
    public enum StatsColumn
    {
        Position, PlayerName, Rank, OverallRating, CombatRating, GamesPlayed,
        TimePlayed, CommanderWins, Wins, Losses, Kills, Deaths, BaseKills, BaseCaptures, Max,
    }

    public short Civ { get; set; } = CareerDto.AllCivs;
    public StatsColumn SortColumn { get; private set; } = StatsColumn.OverallRating; // default checked (leaderboardscreen.cpp:358)

    private string _basis = "";          // m_strBasis ("" = top of list)
    private string? _basisAccountId;     // m_idBasis
    private CareerDto.LeaderboardEntry[] _entries = [];
    private CareerDto.LeaderboardEntry? _entryNumber1; // m_entryNumber1
    private int _offset;                 // m_nOffset
    private int _selected = -1;          // selection index into _entries

    public string OwnCallsign { get; set; } = "";   // m_szClubCharName (FindMe)
    public bool Fail { get; private set; }
    public string? FailReason { get; private set; }

    public string BasisName => _basis;
    public string? BasisAccountId => _basisAccountId;
    public CareerDto.LeaderboardEntry? EntryNumber1 => _entryNumber1;
    public IReadOnlyList<CareerDto.LeaderboardEntry> Entries => _entries;

    /// <summary>The <see cref="LeaderboardRequest"/> the renderer sends to the
    /// lobby — native <c>UpdateData</c>'s <c>LEADER_BOARD_QUERY</c> assembly
    /// (<c>leaderboardscreen.cpp:710-724</c>).</summary>
    public sealed record LeaderboardRequest(short Civ, CareerDto.LeaderBoardSort Sort, string? Basis, string? BasisAccountId);

    public LeaderboardRequest BuildQuery() => new(Civ, Sort(), _basis.Length == 0 ? null : _basis, _basisAccountId);

    /// <summary>Map a sort column to a <c>LeaderBoardSort</c> — native
    /// <c>ColumnToSort</c> (<c>leaderboardscreen.cpp:660-708</c>).</summary>
    public CareerDto.LeaderBoardSort Sort() => SortColumn switch
    {
        StatsColumn.PlayerName => CareerDto.LeaderBoardSort.Name,
        StatsColumn.Rank => CareerDto.LeaderBoardSort.Rank,
        StatsColumn.CombatRating => CareerDto.LeaderBoardSort.Rating,
        StatsColumn.TimePlayed => CareerDto.LeaderBoardSort.TimePlayed,
        StatsColumn.CommanderWins => CareerDto.LeaderBoardSort.CommanderWins,
        StatsColumn.GamesPlayed => CareerDto.LeaderBoardSort.GamesPlayed,
        StatsColumn.Wins => CareerDto.LeaderBoardSort.Wins,
        StatsColumn.Losses => CareerDto.LeaderBoardSort.Losses,
        StatsColumn.Kills => CareerDto.LeaderBoardSort.Kills,
        StatsColumn.Deaths => CareerDto.LeaderBoardSort.Deaths,
        StatsColumn.BaseKills => CareerDto.LeaderBoardSort.BaseKills,
        StatsColumn.BaseCaptures => CareerDto.LeaderBoardSort.BaseCaptures,
        _ => CareerDto.LeaderBoardSort.Score, // OverallRating (and Position/default)
    };

    /// <summary>Radio-toggle the sort column — native <c>OnButtonBarStats</c>
    /// (<c>leaderboardscreen.cpp:633-658</c>): clicking Position is ignored; the
    /// chosen column becomes the sole checked sort and the list is re-queried.
    /// </summary>
    public bool SelectSort(StatsColumn column)
    {
        if (column == StatsColumn.Position) return false;
        SortColumn = column;
        return true;
    }

    /// <summary>FindMe — set the basis to the signed-in player — native
    /// <c>OnButtonFindMe</c> (<c>leaderboardscreen.cpp:512-519</c>).</summary>
    public void FindMe()
    {
        _basis = OwnCallsign; _basisAccountId = null; _entries = []; _offset = 0; Fail = false;
    }

    /// <summary>Search by (possibly partial) name — native <c>OnButtonSearch</c>
    /// (<c>leaderboardscreen.cpp:435-449</c>).</summary>
    public void Search(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _basis = name; _basisAccountId = null; _entries = []; _offset = 0; Fail = false;
    }

    /// <summary>Jump to the top of the list — native <c>OnButtonTop</c>
    /// (<c>leaderboardscreen.cpp:554-561</c>).</summary>
    public void Top() { _basis = ""; _basisAccountId = null; _entries = []; _offset = 0; Fail = false; }

    /// <summary>Select an entry (native <c>OnEvent(IItemEvent)</c>,
    /// <c>leaderboardscreen.cpp:493-503</c>): it becomes the profile/basis target.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < _entries.Length)
        {
            _selected = index;
            _basis = _entries[index].Callsign;
            _basisAccountId = _entries[index].AccountId;
        }
    }

    /// <summary>The profile target after selecting an entry (native
    /// <c>OnButtonPlayerProfile</c>, <c>leaderboardscreen.cpp:521-529</c>).</summary>
    public string? SelectedProfileAccountId => _selected >= 0 && _selected < _entries.Length ? _entries[_selected].AccountId : null;

    public bool CanPageUp => CalcPageUpBasis() < _offset;
    public bool CanPageDown => CalcPageDownBasis() >= _offset + LinesPerScreen;

    /// <summary>Page up by re-centering the basis on the row a half-screen above
    /// the current window — native <c>CalcPageUpBasis</c>/<c>OnButtonPageUp</c>
    /// (<c>leaderboardscreen.cpp:563-588</c>). Returns whether the basis moved
    /// (i.e. a re-query is warranted).</summary>
    public bool PageUp()
    {
        if (!CanPageUp || _entries.Length == 0) return false;
        int index = CalcPageUpBasis();
        SetBasisFromEntry(index);
        return true;
    }

    /// <summary>Page down — native <c>CalcPageDownBasis</c>/<c>OnButtonPageDown</c>
    /// (<c>leaderboardscreen.cpp:590-615</c>).</summary>
    public bool PageDown()
    {
        if (!CanPageDown || _entries.Length == 0) return false;
        int index = CalcPageDownBasis();
        SetBasisFromEntry(index);
        return true;
    }

    /// <summary>Ingest a leaderboard response — native
    /// <c>HandleLeaderBoardMessage</c> (<c>FM_S_LEADER_BOARD_LIST</c>,
    /// <c>leaderboardscreen.cpp:784-830</c>): adopt the window, resolve the basis,
    /// and center the visible rows (native <c>UpdateListBoxes</c>, :835-863).</summary>
    public void Ingest(CareerDto.LeaderboardResponse response)
    {
        Fail = response.FailReason is not null;
        FailReason = response.FailReason;
        if (Fail) return;
        _entries = response.Entries.ToArray();
        _entryNumber1 = response.EntryNumber1;

        int indexBasis;
        if (_basis.Length == 0)
        {
            // Top: the basis becomes the first returned player (native :797-801).
            indexBasis = _entries.Length > 0 ? 0 : -1;
        }
        else
        {
            indexBasis = OffsetOfCharacter(_basis);
            // Prefix search fallback (native OffsetOfCharacterPrefix, :744-756).
            if (indexBasis < 0) indexBasis = OffsetOfCharacterPrefix(_basis);
        }
        if (indexBasis < 0 || _entries.Length == 0)
        {
            _offset = 0; _selected = -1; return;
        }
        _basis = _entries[indexBasis].Callsign;
        _basisAccountId = _entries[indexBasis].AccountId;
        _selected = indexBasis;
        _offset = Math.Max(0, Math.Min(_entries.Length - LinesPerScreen, indexBasis - (LinesPerScreen - 1) / 2));
    }

    /// <summary>The visible window (native <c>UpdateListBoxes</c>, :850-853).</summary>
    public IReadOnlyList<CareerDto.LeaderboardEntry> VisibleRows()
    {
        var rows = new List<CareerDto.LeaderboardEntry>();
        for (int i = 0; i < LinesPerScreen && _offset + i < _entries.Length; i++)
            rows.Add(_entries[_offset + i]);
        return rows;
    }

    private int CalcPageUpBasis() => _entries.Length <= 0 ? _offset : Math.Max(0, _offset - LinesPerScreen + LinesPerScreen / 2);
    private int CalcPageDownBasis() => _entries.Length <= 0 ? _offset : Math.Min(_entries.Length - 1, _offset + LinesPerScreen + LinesPerScreen / 2);

    private void SetBasisFromEntry(int index)
    {
        _basis = _entries[index].Callsign;
        _basisAccountId = _entries[index].AccountId;
        _selected = index;
    }

    private int OffsetOfCharacter(string name) => Array.FindIndex(_entries, e => string.Equals(e.Callsign, name, StringComparison.OrdinalIgnoreCase));
    private int OffsetOfCharacterPrefix(string prefix)
    {
        for (int i = 0; i < _entries.Length; i++)
            if (_entries[i].Callsign.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }
}
