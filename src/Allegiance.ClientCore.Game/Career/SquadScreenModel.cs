namespace Allegiance.ClientCore.MonoGame.Career;

/// <summary>
/// Pure, unit-testable model of the squads screen — the port of native
/// <c>WinTrek/SquadsScreen.cpp</c>: the All / My / DudeX(a player) tabs with
/// paging, sort and find (<c>:2317-2423</c>, <c>:960-1122</c>, <c>:2216-2246</c>),
/// the details/members view (<c>:1332-1463</c>), create/edit state
/// (<c>:2017-2207</c>), and — critically — the exact permission rules that
/// enable each action button (<c>RefreshButtonStates</c>, <c>:1623-1670</c>).
/// Free of MonoGame and HTTP so every navigation and permission path is
/// testable headless; <c>Game1</c> feeds it data from the lobby
/// (<see cref="SquadApiClient"/>) and the renderer presents it in both styles.
/// </summary>
public sealed class SquadScreenModel
{
    public const int LinesPerScreen = 21;          // c_cSquadsShown (SquadsScreen.cpp:25)
    public const int MaxName = 24;
    public const int MaxDescription = 510;
    public const int MaxUrl = 255;
    public const int MaxTag = 8;

    public enum Tab { All, My, Player }

    public Tab ActiveTab { get; private set; } = Tab.All;

    /// <summary>The signed-in player (native s_pActivePlayer); used for the
    /// My tab, the "self" checks, and owner detection.</summary>
    public string ViewerAccountId { get; set; } = "";
    public string ViewerCallsign { get; set; } = "";

    /// <summary>The DudeX player whose squads the Player tab shows
    /// (native s_pDudeXPlayer, SquadsScreen.cpp:382-392).</summary>
    public string PlayerAccountId { get; set; } = "";

    public SquadDto.SquadSortColumn SortColumn { get; private set; } = SquadDto.SquadSortColumn.Name;

    private SquadDto.SquadSummary[] _all = [], _mine = [], _player = [];
    private int _cursor;                    // selected row index within the active list
    private string? _basisId;               // All-tab paging basis (native m_idBasis)

    public IReadOnlyList<SquadDto.SquadSummary> AllRows => _all;
    public IReadOnlyList<SquadDto.SquadSummary> MyRows => _mine;
    public IReadOnlyList<SquadDto.SquadSummary> PlayerRows => _player;
    public int Cursor => _cursor;
    public string? BasisId => _basisId;

    /// <summary>Details of the selected squad (native m_psquadSelected details).</summary>
    public SquadDto.SquadDetails? Details { get; private set; }
    /// <summary>Ownership/audit log of the selected squad (native m_listStrOwnershipLog).</summary>
    public IReadOnlyList<SquadDto.SquadLogEntry> Log { get; private set; } = [];
    public int SelectedMember { get; private set; } = -1;

    // Create/edit state (native m_psquadBeingMade / edit mode, :3164-3207).
    public bool Creating { get; private set; }
    public bool Editing { get; private set; }
    public string NameBuf { get; set; } = "";
    public string TagBuf { get; set; } = "";
    public string DescBuf { get; set; } = "";
    public string UrlBuf { get; set; } = "";
    public short Civ { get; set; } = -1;   // -1 = any civ (native CivID2String -1=ANY)

    /// <summary>Transient status / message-box text (native SQUAD_TEXT_MESSAGE,
    /// Inc/ClubMessages.h:154-156 and local feedback, SquadsScreen.cpp:1550-1561).</summary>
    public string Status { get; set; } = "";

    public SquadDto.SquadSummary? SelectedRow => ActiveList().Count > 0 && _cursor < ActiveList().Count ? ActiveList()[_cursor] : null;
    public SquadDto.SquadSummary? SelectedSummary => Details is null ? null : new SquadDto.SquadSummary(Details.Id, Details.Name, Details.Tag, Details.Civ, 0, 0, 0, 0, Details.Members.Count);
    private IReadOnlyList<SquadDto.SquadSummary> ActiveList() => ActiveTab switch
    {
        Tab.My => _mine,
        Tab.Player => _player,
        _ => _all,
    };

    public void SetActiveTab(Tab tab)
    {
        ActiveTab = tab;
        _cursor = Math.Min(_cursor, Math.Max(0, ActiveList().Count - 1));
        if (tab == Tab.All) _basisId ??= SelectedRow?.Id;
        if (tab == Tab.My || tab == Tab.Player) _basisId = null;
        // Opening on My/Player clears details of an All-tab selection per native tab switch.
        if (tab != Tab.All) { Details = null; Log = []; SelectedMember = -1; }
    }

    // ---- Ingest (native decode of server pages, SquadsScreen.cpp:1163-1330) ----
    public void IngestAll(SquadDto.SquadSummary[] rows, string? basisId)
    {
        _all = rows;
        if (basisId is not null) _basisId = basisId;
        _cursor = 0;
    }
    public void IngestMine(SquadDto.SquadSummary[] rows)
    {
        _mine = rows;
        if (ActiveTab == Tab.My) _cursor = 0;
    }
    public void IngestPlayer(SquadDto.SquadSummary[] rows)
    {
        _player = rows;
        if (ActiveTab == Tab.Player) _cursor = 0;
    }

    public void SelectRow(int index)
    {
        var list = ActiveList();
        if (index < 0 || index >= list.Count) return;
        _cursor = index;
        if (ActiveTab == Tab.All) _basisId = list[index].Id;
        // A fresh (non-programmatic) click clears details; Game1 re-fetches them.
        Details = null; Log = []; SelectedMember = -1;
    }

    public void MoveCursor(int delta)
    {
        var list = ActiveList();
        if (list.Count == 0) return;
        _cursor = (_cursor + delta + list.Count) % list.Count;
        if (ActiveTab == Tab.All) _basisId = list[_cursor].Id;
        // Native selects the squad as soon as its row is picked (OnSquadSelected,
        // SquadsScreen.cpp:1684,3142-3156), so details never describe another row.
        if (Details is { } d && d.Id != list[_cursor].Id) { Details = null; Log = []; SelectedMember = -1; }
    }

    /// <summary>Native Prev/Next on the All tab move a page (±(c_cSquadsShown-1))
    /// and re-query around a new basis (SquadsScreen.cpp:1788-1845). Returns the
    /// basis to request, or null if no movement applies.</summary>
    public string? Page(int dir)
    {
        if (ActiveTab != Tab.All || _all.Length == 0) return null;
        int target = Math.Clamp(_cursor + dir * (LinesPerScreen - 1), 0, _all.Length - 1);
        if (target == _cursor) return null;
        _cursor = target;
        _basisId = _all[target].Id;
        return _basisId;
    }

    public void SelectSort(SquadDto.SquadSortColumn column)
    {
        if (column is SquadDto.SquadSortColumn.Unknown) return;
        SortColumn = column;
        if (ActiveTab == Tab.All && SelectedRow is { } sel) _basisId = sel.Id;
    }

    public void IngestDetails(SquadDto.SquadDetails details)
    {
        Details = details;
        SelectedMember = -1;
    }
    public void IngestLog(SquadDto.SquadLogEntry[] log) => Log = log;
    public void SelectMember(int index)
    {
        if (Details is null || index < 0 || index >= Details.Members.Count) { SelectedMember = -1; return; }
        SelectedMember = index;
    }

    // ---- Create / edit ----
    public void BeginCreate()
    {
        Creating = true; Editing = false;
        NameBuf = ""; TagBuf = ""; DescBuf = ""; UrlBuf = ""; Civ = -1;
        Status = "Enter the squad name, tag, description and URL; press Apply.";
    }
    public void CancelCreate() { Creating = false; Status = "Squad creation cancelled."; }
    public SquadDto.CreateSquadRequest? FinishCreate()
    {
        if (Creating && NameBuf.Trim().Length > 0)
        {
            Creating = false;
            return new SquadDto.CreateSquadRequest(NameBuf.Trim(), TagBuf.Trim(), DescBuf.Trim(), UrlBuf.Trim(), Civ);
        }
        Status = "You must enter the \"squad name\".";
        return null;
    }
    public void BeginEdit()
    {
        if (Details is null) return;
        Editing = true; Creating = false;
        NameBuf = Details.Name; DescBuf = Details.Description; UrlBuf = Details.Url; Civ = Details.Civ;
        Status = "Edit description, URL and faction; press Apply.";
    }
    public void CancelEdit() { Editing = false; }
    public SquadDto.EditSquadRequest? FinishEdit()
    {
        if (!Editing) return null;
        Editing = false;
        return new SquadDto.EditSquadRequest(DescBuf.Trim(), UrlBuf.Trim(), Civ);
    }

    // ---- Permission rules (native RefreshButtonStates, SquadsScreen.cpp:1623-1670) ----
    private SquadDto.SquadMember? SelfMember => Details?.Members.FirstOrDefault(m => string.Equals(m.AccountId, ViewerAccountId, StringComparison.Ordinal));
    public SquadDto.SquadRole? ViewerRole => SelfMember?.Role;
    private bool IsOwner => ViewerRole == SquadDto.SquadRole.Leader;
    private bool ViewerIsMember => ViewerRole is SquadDto.SquadRole.Leader or SquadDto.SquadRole.AssistantLeader or SquadDto.SquadRole.Member;
    private SquadDto.SquadMember? SelectedMem => Details is { } d && SelectedMember >= 0 && SelectedMember < d.Members.Count ? d.Members[SelectedMember] : null;
    private bool SelectedIsSelf => SelectedMem is { } m && string.Equals(m.AccountId, ViewerAccountId, StringComparison.Ordinal);
    private static bool Active(SquadDto.SquadRole r) => r is SquadDto.SquadRole.Leader or SquadDto.SquadRole.AssistantLeader or SquadDto.SquadRole.Member;

    public bool CanCreate => !Creating && !Editing;
    public bool CanRequestJoin => Details is not null && ViewerRole is null; // neither member nor pending (:1640-1641)
    public bool CanCancelJoin => ViewerRole == SquadDto.SquadRole.Pending;                                // pending (:1642)
    public bool CanQuit => Details is not null && ViewerIsMember;                                         // member incl. leader (:1643)
    public bool CanAccept => IsOwner && !SelectedIsSelf && SelectedMem?.Role == SquadDto.SquadRole.Pending;              // :1653
    public bool CanReject => IsOwner && !SelectedIsSelf && SelectedMem is { } m && (m.Role == SquadDto.SquadRole.Pending || Active(m.Role)); // :1652 (reject pending / boot member)
    public bool CanPromote => IsOwner && !SelectedIsSelf && SelectedMem?.Role == SquadDto.SquadRole.Member;              // :1654
    public bool CanDemote => IsOwner && !SelectedIsSelf && SelectedMem?.Role == SquadDto.SquadRole.AssistantLeader;      // :1655
    public bool CanTransfer => IsOwner && !SelectedIsSelf && SelectedMem is { } t && Active(t.Role);                    // :1657
    public bool CanEdit => IsOwner;                                                                                     // :1656/:1663
    public bool CanLog => SelectedRow is not null || Details is not null;                                              // :3159-3162

    public string RoleName(SquadDto.SquadRole r) => r switch
    {
        SquadDto.SquadRole.Leader => "Leader",
        SquadDto.SquadRole.AssistantLeader => "ASL",
        SquadDto.SquadRole.Member => "Member",
        _ => "Pending",
    };
}
