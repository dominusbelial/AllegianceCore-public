using Allegiance.SinglePlayer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly HostLanScreenModel _hostLanScreen = new();
    private readonly System.Text.StringBuilder _lanJoinPassword = new();
    private bool _editingLanPassword;
    private bool _internetPasswordTabWasDown;

    private bool HandleLobbyPassword(KeyboardState keys, bool backspace)
    {
        bool toggle = ActionDown(keys, "LobbyPassword");
        if (toggle && !_internetPasswordTabWasDown && !_filterActive && !_findPlayerActive)
            _editingLanPassword = !_editingLanPassword;
        _internetPasswordTabWasDown = toggle;
        if (!_editingLanPassword) return false;
        if (backspace && !_backWasDown && _lanJoinPassword.Length > 0) _lanJoinPassword.Length--;
        _backWasDown = backspace;
        bool done = ActionDown(keys, "LobbyJoin");
        // Finishing text entry must not also join on the next held-key frame.
        _enterWasDown = done;
        if (done) _editingLanPassword = false;
        return true;
    }

    private void UpdateHostLan(KeyboardState keys, bool up, bool down, bool enter, bool esc)
    {
        if (_skirmishStart is null)
        {
            if (up && !_iUp) _hostLanScreen.Move(-1);
            if (down && !_iDown) _hostLanScreen.Move(1);
            if (keys.IsKeyDown(Keys.Left) && !_iLeft) _hostLanScreen.Change(-1);
            if (keys.IsKeyDown(Keys.Right) && !_iRight) _hostLanScreen.Change(1);
            if (keys.IsKeyDown(Keys.Back) && !_iBack) _hostLanScreen.Backspace();
            if (enter && !_iEnter)
            {
                if (_hostLanScreen.Selected == HostLanScreenModel.BackRow) _introView = IntroView.Main;
                else if (_hostLanScreen.Selected == HostLanScreenModel.StartRow)
                {
                    try
                    {
                        var choices = _hostLanScreen.CreateOptions();
                        _skirmishError = "";
                        _skirmishCancel?.Dispose();
                        _skirmishCancel = new();
                        var token = _skirmishCancel.Token;
                        string pilot = _pilotBuf.Length > 0 ? _pilotBuf.ToString() : _playerName;
                        _skirmishStart = Task.Run(() => SkirmishHost.StartAsync(choices, cancellationToken: token, commanderPilotName: pilot));
                    }
                    catch (ArgumentException ex) { _skirmishError = ex.Message; }
                }
                else _hostLanScreen.Change(1);
            }
        }
        if (esc && !_iEsc)
        {
            if (_skirmishStart is not null) _skirmishCancel?.Cancel();
            else _introView = IntroView.Main;
        }
    }

    private void DrawIntroHostLan(float w, float h, Color title, Color dim, Color text)
    {
        bool internet = _hostLanScreen.Options.HostInternet;
        DrawIntroTitle(w, internet ? "Host internet game" : "Host LAN game", internet
            ? "Friends join through Play Online on the same lobby" : "Friends join through Play LAN / Find Server or Direct Connect", title, dim);
        var rows = _hostLanScreen.Rows;
        for (int i = 0; i < rows.Length; i++)
            _spriteBatch.DrawString(_hudSmall, (i == _hostLanScreen.Selected ? "> " : "  ") + rows[i],
                new Vector2(48, 150 + i * 32), i == _hostLanScreen.Selected ? title : text);
        string[] help =
        [
            _skirmishStart is not null ? "Starting host... ESC cancels" : _skirmishError,
            "Bots keep their seats. Friends join beside them, within native team capacity and balance limits.",
            internet ? "Lobby: " + _hostLanScreen.Options.InternetLobbyUrl : "Share your PC's IPv4 address and the game port shown above. Live addresses and count appear in game.",
            internet ? "The lobby must enable player hosting. Allow the game's UDP traffic through your firewall." : HostLanScreenModel.NetworkHelp,
            internet ? "Direct connection unavailable? Forward TCP/UDP or use Tailscale. Host quitting ends the game." : HostLanScreenModel.InternetHelp,
            "TYPE name / port / password   UP/DOWN select   LEFT/RIGHT change   ENTER choose   ESC back"
        ];
        for (int i = 0; i < help.Length; i++)
            _spriteBatch.DrawString(_hudSmall, help[i], new Vector2(32, h - 178 + i * 27), i == 0 ? Color.Orange : dim,
                0, Vector2.Zero, Math.Min(1f, (w - 64) / Math.Max(1, _hudSmall.MeasureString(help[i]).X)), Microsoft.Xna.Framework.Graphics.SpriteEffects.None, 0);
    }
}
