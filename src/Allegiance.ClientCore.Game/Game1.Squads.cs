using System.Text;
using Allegiance.ClientCore.MonoGame.Career;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P820a — the Squads sub-screen of the career screen, rendered from the
/// shared <see cref="SquadScreenModel"/> (the port of native
/// <c>WinTrek/SquadsScreen.cpp</c>) and driven over the lobby's squad HTTP API
/// (<see cref="SquadApiClient"/>). All behaviour and permission rules live in
/// the model; this slice only translates keyboard input into model transitions
/// and lobby calls.
/// </summary>
public sealed partial class Game1
{
    private void UpdateSquadScreen(KeyboardState ks, bool up, bool down, bool enter, bool esc, bool tab)
    {
        bool left = ks.IsKeyDown(XnaKeys.Left), right = ks.IsKeyDown(XnaKeys.Right);
        bool pgUp = ks.IsKeyDown(XnaKeys.PageUp), pgDn = ks.IsKeyDown(XnaKeys.PageDown);
        bool aKey = ks.IsKeyDown(XnaKeys.A), jKey = ks.IsKeyDown(XnaKeys.J), qKey = ks.IsKeyDown(XnaKeys.Q);
        bool rKey = ks.IsKeyDown(XnaKeys.R), pKey = ks.IsKeyDown(XnaKeys.P), dKey = ks.IsKeyDown(XnaKeys.D);
        bool tKey = ks.IsKeyDown(XnaKeys.T), eKey = ks.IsKeyDown(XnaKeys.E), nKey = ks.IsKeyDown(XnaKeys.N);
        bool sKey = ks.IsKeyDown(XnaKeys.S), lKey = ks.IsKeyDown(XnaKeys.L);
        bool back = ks.IsKeyDown(XnaKeys.Back);

        if (_careerLoad is { IsCompleted: false }) return;

        // Find (partial-name search) consumes all keys until Enter/Esc — native
        // SQUAD_PAGE_FIND (SquadsScreen.cpp:2216-2246).
        if (_squadSearching)
        {
            if (enter && !_squadEnterPrev)
            {
                _squadSearching = false;
                if (_squadSearchBuf.Length > 0) { _squads.SetActiveTab(SquadScreenModel.Tab.All); RunSquadOp(() => FindAndShowAsync(_squadSearchBuf.ToString())); }
                return;
            }
            if (esc && !_squadEscPrev) { _squadSearching = false; return; }
            if (back && _squadSearchBuf.Length > 0) _squadSearchBuf.Length--;
            foreach (var c in ks.GetPressedKeys())
            {
                char ch = KeyToChar(c, ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift));
                if (ch != '\0' && _squadSearchBuf.Length < SquadScreenModel.MaxName) _squadSearchBuf.Append(ch);
            }
            _squads.Status = "FIND: " + _squadSearchBuf + "_  (ENTER query / ESC cancel)";
            return;
        }

        // Create / edit dialog state consumes letters into the active field.
        if (_squads.Creating || _squads.Editing)
        {
            HandleSquadFieldTyping(ks, enter, esc, tab, back);
            return;
        }

        if (tab && !_squadTabPrev) { CycleCareerMode(1); return; }
        if (esc && !_squadEscPrev)
        {
            // Drop details first, then back to the main menu (native screen back).
            if (_squads.Details is not null && !_squadMemberMode) { _squads.IngestDetails(null!); _squadMemberMode = false; return; }
            _introView = IntroView.Main; _introIndex = 10; return;
        }

        // Sub-tab switch (All / My / Player).
        if (left && !_squadLeftPrev) { SwitchSquadTab(-1); return; }
        if (right && !_squadRightPrev) { SwitchSquadTab(+1); return; }

        // With details open, Up/Down move the member cursor for owner actions.
        if (_squadMemberMode && _squads.Details is not null)
        {
            if (up && !_squadUpPrev)
            {
                int n = _squads.Details.Members.Count;
                if (n > 0) _squads.SelectMember((_squads.SelectedMember + n - 1) % n);
            }
            if (down && !_squadDownPrev)
            {
                int n = _squads.Details.Members.Count;
                if (n > 0) _squads.SelectMember((_squads.SelectedMember + 1) % n);
            }
        }
        else
        {
            if (up && !_squadUpPrev) _squads.MoveCursor(-1);
            if (down && !_squadDownPrev) _squads.MoveCursor(+1);
        }

        // Page / sort on the All tab (native Prev/Next/Top, :1766-1845).
        if (_squads.ActiveTab == SquadScreenModel.Tab.All)
        {
            if (pgUp && !_squadPgUpPrev) { if (_squads.Page(-1) is { } b) RunSquadOp(() => LoadAllAsync(b)); }
            if (pgDn && !_squadPgDnPrev) { if (_squads.Page(+1) is { } b2) RunSquadOp(() => LoadAllAsync(b2)); }
        }

        if (enter && !_squadEnterPrev)
        {
            if (_squads.SelectedRow is { } sel) { _squads.IngestDetails(null!); _squadMemberMode = false; RunSquadOp(() => LoadDetailsAsync(sel.Id)); }
            return;
        }

        // Actions.
        if (sKey && !_squadSPrev) { _squadSearching = true; _squadSearchBuf.Clear(); _squads.Status = "FIND: type a squad name, ENTER to query"; return; }
        if (nKey && !_squadNPrev) { if (_squads.CanCreate) { _squadMemberMode = false; _squads.BeginCreate(); SetSquadField(0); } return; }
        if (lKey && !_squadLPrev && _squads.SelectedRow is { } lsel) { RunSquadOp(() => LoadLogAsync(lsel.Id)); return; }

        if (eKey && !_squadEPrev && _squads.CanEdit && _squads.Details is { } d) { _squadMemberMode = false; _squads.BeginEdit(); SetSquadField(2); return; }

        // Member-targeted owner actions (accept/reject/boot/promote/demote/transfer).
        if (_squadMemberMode && _squads.Details is { } det && _squads.SelectedMember >= 0 && _squads.SelectedMember < det.Members.Count)
        {
            string mid = det.Members[_squads.SelectedMember].AccountId;
            string sid = det.Id;
            if (aKey && !_squadAPrev && _squads.CanAccept) { _squads.Status = "Accepting..."; RunSquadOp(() => DoActionAsync(() => _squadClient!.AcceptAsync(sid, mid))); return; }
            if (rKey && !_squadRPrev && _squads.CanReject) { _squads.Status = "Removing..."; RunSquadOp(() => DoActionAsync(() => _squadClient!.RejectAsync(sid, mid))); return; }
            if (pKey && !_squadPPrev && _squads.CanPromote) { _squads.Status = "Promoting..."; RunSquadOp(() => DoActionAsync(() => _squadClient!.PromoteAsync(sid, mid))); return; }
            if (dKey && !_squadDPrev && _squads.CanDemote) { _squads.Status = "Demoting..."; RunSquadOp(() => DoActionAsync(() => _squadClient!.DemoteAsync(sid, mid))); return; }
            if (tKey && !_squadTPrev && _squads.CanTransfer) { _squads.Status = "Transferring..."; RunSquadOp(() => DoActionAsync(() => _squadClient!.TransferAsync(sid, mid))); return; }
        }

        // Join / cancel-join / quit act on the selected squad itself.
        if (jKey && !_squadJPrev)
        {
            if (_squads.CanRequestJoin && _squads.Details is { } dj) { _squads.Status = "Requesting..."; RunSquadOp(() => DoActionAsync(() => _squadClient!.JoinAsync(dj.Id))); }
            else if (_squads.CanCancelJoin && _squads.Details is { } dc) { _squads.Status = "Cancelling..."; RunSquadOp(() => DoActionAsync(() => _squadClient!.CancelJoinAsync(dc.Id))); }
            return;
        }
        if (qKey && !_squadQPrev && _squads.CanQuit && _squads.Details is { } dq) { _squads.Status = "Quitting..."; RunSquadOp(() => DoActionAsync(() => _squadClient!.QuitAsync(dq.Id))); return; }

        // Record the edge-latches (after the action branches consumed them).
        LatchSquadEdges(up, down, enter, esc, tab, left, right, pgUp, pgDn,
            aKey, jKey, qKey, rKey, pKey, dKey, tKey, eKey, nKey, sKey, lKey);
    }

    private void HandleSquadFieldTyping(KeyboardState ks, bool enter, bool esc, bool tab, bool back)
    {
        int max = _squadField switch { 0 => SquadScreenModel.MaxName, 1 => SquadScreenModel.MaxTag, 2 => SquadScreenModel.MaxDescription, _ => SquadScreenModel.MaxUrl };
        if (enter && !_squadEnterPrev)
        {
            if (_squads.Creating)
            {
                var req = _squads.FinishCreate();
                if (req is not null) RunSquadOp(() => CreateSquadAsync(req));
            }
            else
            {
                var req = _squads.FinishEdit();
                if (req is not null && _squads.Details is { } d) RunSquadOp(() => EditSquadAsync(d.Id, req));
            }
            return;
        }
        if (esc && !_squadEscPrev)
        {
            if (_squads.Creating) _squads.CancelCreate();
            else _squads.CancelEdit();
            return;
        }
        if (tab && !_squadTabPrev)
        {
            ApplySquadBuf();                 // commit current field
            _squadField = (_squadField + 1) % 4;
            LoadSquadFieldIntoBuf();
            _squads.Status = EditingFieldHint();
            return;
        }
        if (back && _squadBuf.Length > 0) _squadBuf.Length--;
        foreach (var c in ks.GetPressedKeys())
        {
            char ch = KeyToChar(c, ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift));
            if (ch != '\0' && ch != '\n' && _squadBuf.Length < max) _squadBuf.Append(ch);
        }
        ApplySquadBuf();
        _squads.Status = EditingFieldHint();
    }

    private void SetSquadField(int field)
    {
        _squadField = field;
        LoadSquadFieldIntoBuf();
        _squads.Status = EditingFieldHint();
    }

    private void LoadSquadFieldIntoBuf()
    {
        _squadBuf.Clear();
        _squadBuf.Append(_squadField switch
        {
            0 => _squads.NameBuf, 1 => _squads.TagBuf, 2 => _squads.DescBuf, _ => _squads.UrlBuf,
        });
    }

    private string EditingFieldHint() => (_squads.Creating ? "CREATE " : "EDIT ") + (_squadField switch
    {
        0 => "NAME", 1 => "TAG", 2 => "DESCRIPTION", _ => "URL",
    }) + ": " + _squadBuf + "_  (TAB next field / ENTER apply / ESC cancel)";

    // ---- Lobby round-trips ----

    private async Task LoadSquadAsync()
    {
        try
        {
            var m = _squads;
            if (m.ActiveTab == SquadScreenModel.Tab.My)
            {
                var mine = await _squadClient!.GetMineAsync().ConfigureAwait(false);
                if (mine is null) { m.Status = "Sign in (F2) to see your squads."; return; }
                m.IngestMine(mine);
            }
            else if (m.ActiveTab == SquadScreenModel.Tab.Player)
            {
                m.IngestPlayer(await _squadClient!.GetByPlayerAsync(m.PlayerAccountId).ConfigureAwait(false));
            }
            else
            {
                m.IngestAll(await _squadClient!.GetAllAsync(m.SortColumn, m.BasisId).ConfigureAwait(false), m.BasisId);
            }
            m.Status = "";
            _careerStatus = m.Status;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _squads.Status = "Squad service unreachable: " + e.Message;
            _careerStatus = _squads.Status;
        }
    }

    private async Task LoadAllAsync(string basisId)
    {
        try { _squads.IngestAll(await _squadClient!.GetAllAsync(_squads.SortColumn, basisId).ConfigureAwait(false), basisId); _squads.Status = ""; }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException) { _squads.Status = "Page failed: " + e.Message; }
    }

    private async Task LoadDetailsAsync(string id)
    {
        try
        {
            var d = await _squadClient!.GetDetailsAsync(id).ConfigureAwait(false);
            if (d is null) { _squads.Status = "Squad not found."; return; }
            _squads.IngestDetails(d);
            _squadMemberMode = true;
            _squads.Status = d.Members.Count > 0 ? $"Viewing {d.Name} ({d.Members.Count} members). A accept  R reject/boot  P promote  D demote  T transfer" : $"Viewing {d.Name} — no members yet.";
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException) { _squads.Status = "Details unavailable: " + e.Message; }
    }

    private async Task LoadLogAsync(string id)
    {
        try { _squads.IngestLog(await _squadClient!.GetLogAsync(id).ConfigureAwait(false) ?? []); _squads.Status = "Ownership log:"; }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException) { _squads.Status = "Log unavailable: " + e.Message; }
    }

    private async Task CreateSquadAsync(SquadDto.CreateSquadRequest req)
    {
        var r = await _squadClient!.CreateAsync(req).ConfigureAwait(false);
        _squads.Status = r is { Ok: true } ? "Squad created." : r?.Error ?? "Create failed.";
        ReloadCareer();
    }

    private async Task EditSquadAsync(string id, SquadDto.EditSquadRequest req)
    {
        var r = await _squadClient!.EditAsync(id, req).ConfigureAwait(false);
        _squads.Status = r is { Ok: true } ? "Squad updated." : r?.Error ?? "Edit failed.";
        if (r is { Ok: true }) await LoadDetailsAsync(id).ConfigureAwait(false);
    }

    private async Task DoActionAsync(Func<Task<SquadDto.SquadActionResult?>> act)
    {
        var r = await act().ConfigureAwait(false);
        _squads.Status = r?.Error ?? "Done.";
        // Refresh details (roles/membership changed) and the active list.
        if (_squads.Details is { } d) await LoadDetailsAsync(d.Id).ConfigureAwait(false);
        ReloadCareer();
    }

    private async Task FindAndShowAsync(string q)
    {
        try { _squads.IngestAll(await _squadClient!.FindAsync(q).ConfigureAwait(false), null); _squads.Status = "Find results for '" + q + "'."; }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or TaskCanceledException) { _squads.Status = "Find failed: " + e.Message; }
    }

    private void SwitchSquadTab(int dir)
    {
        int n = Enum.GetValues(typeof(SquadScreenModel.Tab)).Length;
        int i = ((int)_squads.ActiveTab + dir + n) % n;
        _squads.SetActiveTab((SquadScreenModel.Tab)i);
        _squadMemberMode = false;
        RunSquadOp(LoadSquadAsync);
    }

    private void RunSquadOp(Func<Task> op)
    {
        _careerStatus = _squads.Status;
        _careerLoad = op();
    }

    private void ApplySquadBuf()
    {
        if (_squads.Creating || _squads.Editing)
        {
            switch (_squadField)
            {
                case 0: _squads.NameBuf = _squadBuf.ToString(); break;
                case 1: _squads.TagBuf = _squadBuf.ToString(); break;
                case 2: _squads.DescBuf = _squadBuf.ToString(); break;
                default: _squads.UrlBuf = _squadBuf.ToString(); break;
            }
        }
    }

    private void LatchSquadEdges(bool up, bool down, bool enter, bool esc, bool tab, bool left, bool right,
        bool pgUp, bool pgDn, bool aKey, bool jKey, bool qKey, bool rKey, bool pKey, bool dKey, bool tKey,
        bool eKey, bool nKey, bool sKey, bool lKey)
    {
        _squadUpPrev = up; _squadDownPrev = down; _squadEnterPrev = enter; _squadEscPrev = esc; _squadTabPrev = tab;
        _squadLeftPrev = left; _squadRightPrev = right; _squadPgUpPrev = pgUp; _squadPgDnPrev = pgDn;
        _squadAPrev = aKey; _squadJPrev = jKey; _squadQPrev = qKey; _squadRPrev = rKey; _squadPPrev = pKey;
        _squadDPrev = dKey; _squadTPrev = tKey; _squadEPrev = eKey; _squadNPrev = nKey; _squadSPrev = sKey; _squadLPrev = lKey;
    }

    private readonly StringBuilder _squadSearchBuf = new();
    private readonly StringBuilder _squadBuf = new();
    private bool _squadSearching, _squadMemberMode;
    private int _squadField;
    private bool _squadUpPrev, _squadDownPrev, _squadEnterPrev, _squadEscPrev, _squadTabPrev;
    private bool _squadLeftPrev, _squadRightPrev, _squadPgUpPrev, _squadPgDnPrev;
    private bool _squadAPrev, _squadJPrev, _squadQPrev, _squadRPrev, _squadPPrev, _squadDPrev, _squadTPrev;
    private bool _squadEPrev, _squadNPrev, _squadSPrev, _squadLPrev;
}
