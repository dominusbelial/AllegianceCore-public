using System.Text;
using Allegiance.ClientCore.MonoGame.Career;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P808c — the career screens (character info + leaderboard) rendered in the
/// MonoGame client, reached from the intro (the port's lobby portal). All
/// behaviour lives in the shared P808b models (<see cref="CharacterInfoModel"/>
/// and <see cref="LeaderboardModel"/>); this slice wires them to the lobby's
/// career HTTP API (<see cref="CareerApiClient"/>) and drives them from the
/// keyboard, and <see cref="CareerScreenRenderer"/> presents the result in
/// both HUD styles.
/// </summary>
public sealed partial class Game1
{
    /// <summary>Which career sub-view is showing.</summary>
    private enum CareerMode { CharacterInfo, Leaderboard, Squads }

    private CareerApiClient? _careerClient;
    private string _careerOrigin = "";
    private readonly CharacterInfoModel _characterInfo = new();
    private readonly LeaderboardModel _leaderboard = new();
    private CareerMode _careerMode = CareerMode.CharacterInfo;
    private string _careerViewAccount = "";     // account whose info is shown
    private string _careerStatus = "";
    private Task? _careerLoad;
    private int _careerCursor;                  // leaderboard row cursor
    private bool _careerEditingDesc;
    private readonly StringBuilder _careerDescBuf = new();

    // P820a — the Squads sub-screen (native SquadsScreen.cpp), driven from the
    // lobby's squad HTTP API and presented by CareerScreenRenderer.
    private SquadApiClient? _squadClient;
    private readonly SquadScreenModel _squads = new();

    /// <summary>Enter the career screen (intro main menu "Career" item).</summary>
    private void EnterCareerScreen()
    {
        string origin = string.IsNullOrEmpty(_lobbyUrl) ? ServerListing.LobbyUrlDefault : _lobbyUrl;
        if (_careerClient is null || _careerOrigin != origin)
        {
            _careerClient?.Dispose();
            _careerClient = new CareerApiClient(origin);
            _careerOrigin = origin;
        }
        // Signed-in player's career is the initial profile; the bearer lets the
        // owner edit their own description (P808b) and drive squad actions (P820a).
        if (_identityClient?.Account is { } acct)
        {
            _careerClient.SetSession(_identityClient.SessionBearer);
            _careerViewAccount = acct.Id;
        }
        else
        {
            _careerViewAccount = "";
            _careerMode = CareerMode.Leaderboard;   // no self -> lead with the board
        }
        if (_squadClient is null || _careerOrigin != origin)
        {
            _squadClient?.Dispose();
            _squadClient = new SquadApiClient(origin);
        }
        _squadClient.SetSession(_identityClient?.SessionBearer);
        _squads.ViewerAccountId = _identityClient?.Account?.Id ?? "";
        _squads.ViewerCallsign = _identityClient?.Account?.Callsign ?? "";
        _squads.PlayerAccountId = _identityClient?.Account?.Id ?? "";
        _careerCursor = 0;
        _careerEditingDesc = false;
        _introView = IntroView.Career;
        Console.WriteLine("[career] entered career screen");
        ReloadCareer();
    }

    private void ReloadCareer()
    {
        if (_careerMode == CareerMode.Squads)
        {
            if (_squadClient is null) return;
            _careerStatus = "Loading squads...";
            _careerLoad = LoadSquadAsync();
            return;
        }
        if (_careerClient is null) return;
        _careerStatus = _careerMode == CareerMode.CharacterInfo ? "Loading character info..." : "Loading leaderboard...";
        _careerLoad = LoadCareerAsync();
    }

    private void CycleCareerMode(int dir)
    {
        int n = Enum.GetValues(typeof(CareerMode)).Length;
        int i = ((int)_careerMode + dir + n) % n;
        _careerMode = (CareerMode)i;
        ReloadCareer();
    }

    private async Task LoadCareerAsync()
    {
        try
        {
            if (_careerMode == CareerMode.CharacterInfo)
            {
                if (_careerViewAccount.Length == 0)
                {
                    _careerStatus = "No pilot selected - sign in (F2) or open a profile from the leaderboard.";
                    return;
                }
                var info = await _careerClient!.GetCareerAsync(_careerViewAccount).ConfigureAwait(false);
                if (info is null) { _careerStatus = "No career data for this pilot."; return; }
                _characterInfo.Load(info, id => _shopCatalog?.Core?.Civilizations is { } civs && civs.TryGetValue(id, out var civ) ? civ.Name : null);
                // P820a — character-info also lists the player's squads (CHARACTER_INFO_SQUAD).
                if (_squadClient is not null)
                {
                    try { _characterInfo.LoadSquads((await _squadClient.GetByPlayerAsync(_careerViewAccount).ConfigureAwait(false)).Select(s => (s.Id, s.Name))); }
                    catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { _characterInfo.LoadSquads([]); }
                }
            }
            else
            {
                var q = _leaderboard.BuildQuery();
                var resp = await _careerClient!.GetLeaderboardAsync(q.Civ, q.Sort, q.Basis, q.BasisAccountId).ConfigureAwait(false);
                if (resp is null) { _careerStatus = "Leaderboard unavailable."; return; }
                _leaderboard.Ingest(resp);
                int clamp = _leaderboard.VisibleRows().Count;
                if (_careerCursor >= clamp) _careerCursor = Math.Max(0, clamp - 1);
            }
            _careerStatus = "";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            _careerStatus = "Career service unreachable: " + e.Message;
        }
    }

    private void UpdateCareerScreen(KeyboardState ks, bool up, bool down, bool enter, bool esc, bool tab)
    {
        // Edge latches unique to the career screen.
        bool left = ks.IsKeyDown(XnaKeys.Left);
        bool right = ks.IsKeyDown(XnaKeys.Right);
        bool pageUp = ks.IsKeyDown(XnaKeys.PageUp);
        bool pageDown = ks.IsKeyDown(XnaKeys.PageDown);
        bool eKey = ks.IsKeyDown(XnaKeys.E);
        bool sKey = ks.IsKeyDown(XnaKeys.S);
        bool fKey = ks.IsKeyDown(XnaKeys.F);
        bool tKey = ks.IsKeyDown(XnaKeys.T);
        bool back = ks.IsKeyDown(XnaKeys.Back);

        if (_careerLoad is { IsCompleted: false }) return;

        // P820a — the squads sub-screen has its own keyboard handling.
        if (_careerMode == CareerMode.Squads) { UpdateSquadScreen(ks, up, down, enter, esc, tab); return; }

        // A pending search text entry consumes all keys until Enter/Esc.
        if (_careerSearching)
        {
            HandleCareerSearchTyping(ks, back, enter, esc);
            return;
        }

        if (_careerMode == CareerMode.CharacterInfo)
        {
            if (_careerEditingDesc)
            {
                HandleCareerDescTyping(ks, back, enter, esc);
                return;
            }
            if (left && !_careerLeftPrev && _characterInfo.CivNames.Count > 0)
            { _characterInfo.SelectCiv(Math.Max(0, _characterInfo.SelectedCiv - 1)); }
            if (right && !_careerRightPrev && _characterInfo.CivNames.Count > 0)
            { _characterInfo.SelectCiv(Math.Min(_characterInfo.CivNames.Count - 1, _characterInfo.SelectedCiv + 1)); }
            if (eKey && !_careerEPrev && _characterInfo.Callsign.Length > 0)
            { _careerDescBuf.Clear(); _careerDescBuf.Append(_characterInfo.Description); _characterInfo.BeginEdit(); _careerEditingDesc = true; }
            if (tab && !_careerTabPrev) CycleCareerMode(1);
            if (esc && !_careerEscPrev) { _introView = IntroView.Main; _introIndex = 10; return; }
        }
        else
        {
            var rows = _leaderboard.VisibleRows();
            if (up && !_careerUpPrev && rows.Count > 0) _careerCursor = (_careerCursor - 1 + rows.Count) % rows.Count;
            if (down && !_careerDownPrev && rows.Count > 0) _careerCursor = (_careerCursor + 1) % rows.Count;
            if (enter && !_careerEnterPrev && rows.Count > 0 && _careerCursor < rows.Count)
            {
                // Select the entry -> open its profile (native OnButtonPlayerProfile).
                _leaderboard.Select(_careerCursor);
                _careerViewAccount = _leaderboard.SelectedProfileAccountId ?? "";
                if (_careerViewAccount.Length > 0) { _careerMode = CareerMode.CharacterInfo; ReloadCareer(); }
            }
            if (sKey && !_careerSPrev) StartCareerSearch();
            if (fKey && !_careerFPrev) { _leaderboard.OwnCallsign = _identityClient?.Account?.Callsign ?? ""; _leaderboard.FindMe(); ReloadCareer(); }
            if (tKey && !_careerTPrev) { _leaderboard.Top(); ReloadCareer(); }
            if (pageUp && !_careerPgUpPrev && _leaderboard.PageUp()) ReloadCareer();
            if (pageDown && !_careerPgDnPrev && _leaderboard.PageDown()) ReloadCareer();
            if (left && !_careerLeftPrev) CycleSort(-1);
            if (right && !_careerRightPrev) CycleSort(+1);
            if (tab && !_careerTabPrev) CycleCareerMode(1);
            if (esc && !_careerEscPrev) { _introView = IntroView.Main; _introIndex = 10; return; }
        }

        _careerLeftPrev = left; _careerRightPrev = right; _careerPgUpPrev = pageUp; _careerPgDnPrev = pageDown;
        _careerEPrev = eKey; _careerSPrev = sKey; _careerFPrev = fKey; _careerTPrev = tKey;
        _careerUpPrev = up; _careerDownPrev = down; _careerEnterPrev = enter; _careerEscPrev = esc; _careerTabPrev = tab;
    }

    private void StartCareerSearch()
    {
        _careerSearching = true;
        _careerSearchBuf.Clear();
        _careerStatus = "SEARCH: type a pilot name, ENTER to query, ESC to cancel";
    }

    private void HandleCareerSearchTyping(KeyboardState ks, bool back, bool enter, bool esc)
    {
        if (enter && !_careerEnterPrev)
        {
            _careerSearching = false;
            if (_careerSearchBuf.Length > 0) { _leaderboard.Search(_careerSearchBuf.ToString()); ReloadCareer(); }
            return;
        }
        if (esc && !_careerEscPrev) { _careerSearching = false; return; }
        if (back && _careerSearchBuf.Length > 0) _careerSearchBuf.Length--;
        foreach (var c in ks.GetPressedKeys())
        {
            char ch = KeyToChar(c, ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift));
            if (ch != '\0' && _careerSearchBuf.Length < 24) _careerSearchBuf.Append(ch);
        }
        _careerStatus = "SEARCH: " + _careerSearchBuf + "_  (ENTER query / ESC cancel)";
    }

    private void HandleCareerDescTyping(KeyboardState ks, bool back, bool enter, bool esc)
    {
        if (enter && !_careerEnterPrev)
        {
            string text = _careerDescBuf.ToString();
            bool saved = false;
            try { saved = _careerClient?.SetDescriptionAsync(_careerViewAccount, text).GetAwaiter().GetResult() == true; }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { /* unsigned client falls through */ }
            _characterInfo.CommitDescription(text);
            _careerEditingDesc = false;
            _careerStatus = saved ? "Profile description saved to the lobby." : "Saved locally (sign in to save to the lobby).";
            return;
        }
        if (esc && !_careerEscPrev)
        {
            _characterInfo.ApplyEdit();   // discard the edit
            _careerEditingDesc = false;
            return;
        }
        if (back && _careerDescBuf.Length > 0) _careerDescBuf.Length--;
        // Printable printable-ASCII letters/space into the description (≤256).
        foreach (var c in ks.GetPressedKeys())
        {
            char ch = KeyToChar(c, ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift));
            if (ch != '\0' && ch != '\n' && _careerDescBuf.Length < 256)
                _careerDescBuf.Append(ch);
        }
    }

    /// <summary>Cycle the leaderboard sort column (native OnButtonBarStats radios),
    /// skipping Position (which is ignored).</summary>
    private void CycleSort(int dir)
    {
        var cols = (LeaderboardModel.StatsColumn[])Enum.GetValues(typeof(LeaderboardModel.StatsColumn));
        int i = Array.IndexOf(cols, _leaderboard.SortColumn);
        for (int step = 0; step < cols.Length; step++)
        {
            i = (i + dir + cols.Length) % cols.Length;
            if (_leaderboard.SelectSort(cols[i])) { ReloadCareer(); return; }   // Position ignored
        }
    }

    private static char KeyToChar(XnaKeys key, bool shift) => key switch
    {
        XnaKeys.A => 'A', XnaKeys.B => 'B', XnaKeys.C => 'C', XnaKeys.D => 'D', XnaKeys.E => 'E',
        XnaKeys.F => 'F', XnaKeys.G => 'G', XnaKeys.H => 'H', XnaKeys.I => 'I', XnaKeys.J => 'J',
        XnaKeys.K => 'K', XnaKeys.L => 'L', XnaKeys.M => 'M', XnaKeys.N => 'N', XnaKeys.O => 'O',
        XnaKeys.P => 'P', XnaKeys.Q => 'Q', XnaKeys.R => 'R', XnaKeys.S => 'S', XnaKeys.T => 'T',
        XnaKeys.U => 'U', XnaKeys.V => 'V', XnaKeys.W => 'W', XnaKeys.X => 'X', XnaKeys.Y => 'Y',
        XnaKeys.Z => 'Z',
        XnaKeys.D0 => shift ? ')' : '0', XnaKeys.D1 => shift ? '!' : '1', XnaKeys.D2 => shift ? '@' : '2',
        XnaKeys.D3 => shift ? '#' : '3', XnaKeys.D4 => shift ? '$' : '4', XnaKeys.D5 => shift ? '%' : '5',
        XnaKeys.D6 => shift ? '^' : '6', XnaKeys.D7 => shift ? '&' : '7', XnaKeys.D8 => shift ? '*' : '8',
        XnaKeys.D9 => shift ? '(' : '9',
        XnaKeys.Space => ' ', XnaKeys.OemMinus => shift ? '_' : '-', XnaKeys.OemPlus => shift ? '+' : '=',
        XnaKeys.OemPeriod => shift ? '>' : '.', XnaKeys.OemComma => shift ? '<' : ',',
        XnaKeys.OemQuestion => shift ? '?' : '/', XnaKeys.OemSemicolon => shift ? ':' : ';',
        XnaKeys.OemQuotes => shift ? '"' : '\'', XnaKeys.OemOpenBrackets => shift ? '{' : '[',
        XnaKeys.OemCloseBrackets => shift ? '}' : ']', XnaKeys.OemPipe => shift ? '|' : '\\',
        XnaKeys.OemTilde => shift ? '~' : '`',
        _ => '\0',
    };

    // Edge-latch fields (declare at the end to keep the partial tidy).
    private bool _careerLeftPrev, _careerRightPrev, _careerPgUpPrev, _careerPgDnPrev;
    private bool _careerEPrev, _careerSPrev, _careerFPrev, _careerTPrev;
    private bool _careerUpPrev, _careerDownPrev, _careerEnterPrev, _careerEscPrev, _careerTabPrev;
    private bool _careerSearching;
    private readonly StringBuilder _careerSearchBuf = new();
}
