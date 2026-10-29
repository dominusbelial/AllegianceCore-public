using Allegiance.ClientCore.MonoGame.Games;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P871 / lobby::Create game — the intro screen's NEW GAME wizard (partial
/// <see cref="Game1"/>). Native's server-list screen opened the
/// <c>NewGameScreen</c> form, filled it from the lobby's static-core catalog and
/// sent <c>CREATE_MISSION_REQ</c> to the lobby (<c>lobby/messageslc.h:54-59</c>,
/// <c>lobby/client.cpp:390-465</c>); the port's lobby is HTTP, so the catalog and
/// the request ride <see cref="LobbyGameClient"/>.
///
/// <para><b>The client is sent there (P871b).</b> Native's flow ends with the
/// client joining the server that created its mission: the lobby's ACK means the
/// server is provisioning the world, and the requester connects to it. P871
/// stopped at "Game requested (id)" and left the player on the form. Now an ACK
/// hands the chosen server's address to the ordinary join path
/// (<c>JoinFromIntro</c>), so the wizard ends in the new mission.</para>
/// </summary>
public sealed partial class Game1
{
    private readonly CreateGameWizard _createGame = new();
    private readonly object _createGameLock = new();
    private LobbyGameClient? _gameClient;
    private bool _createGameLoading;
    /// <summary>P871b — one-shot latch for the ALLEGIANCE_PROOF_CREATE_GAME hook.</summary>
    private bool _createGameProofSeeded;
    private string _createGameError = "";
    /// <summary>P871b — the server the lobby accepted a create request for; the
    /// game thread consumes it and joins (native: the requester connects to the
    /// server that created its mission). Null until an ACK arrives.</summary>
    private (string Host, int Port)? _createGameJoinPending;

    private void EnterCreateGame()
    {
        _introView = IntroView.CreateGame;
        _createGameError = "";
        _createGame.Status = "";
        if (_createGame.Options.Servers.Count > 0) return;
        _gameClient ??= new LobbyGameClient(_http, _lobbyUrl, () => _identityClient?.SessionBearer);
        if (_createGameLoading) return;
        _createGameLoading = true;
        var client = _gameClient;
        _ = client.GetCatalogAsync().ContinueWith(t =>
        {
            lock (_createGameLock)
            {
                if (t.Result is { } catalog) _createGame.ApplyCatalog(catalog);
                else _createGameError = "Lobby catalog unavailable.";
                _createGameLoading = false;
            }
        }, TaskScheduler.Default);
    }

    private void UpdateCreateGame(KeyboardState ks, bool up, bool down, bool enter, bool esc)
    {
        // P871b — an ACK from the previous frame? The server is provisioning the
        // mission we asked for, so join it (native ends the create flow by
        // connecting to the chosen server, lobby/client.cpp:445-465).
        if (_createGameJoinPending is { } join)
        {
            _createGameJoinPending = null;
            Console.WriteLine($"[create] lobby accepted — joining {join.Host}:{join.Port} (the server provisioning this game)");
            JoinFromIntro(join.Host, join.Port);
            return;
        }
        if (up && !_iUp) _createGame.Move(-1);
        if (down && !_iDown) _createGame.Move(1);
        if (ks.IsKeyDown(XnaKeys.Left) && !_iLeft) _createGame.Adjust(-1);
        if (ks.IsKeyDown(XnaKeys.Right) && !_iRight) _createGame.Adjust(1);
        if (ks.IsKeyDown(XnaKeys.Back) && !_iBack && _createGame.Cursor == CreateGameWizard.Field.Name)
            _createGame.Backspace();
        if (esc && !_iEsc) { _introView = IntroView.FindServer; _introIndex = 0; }
        if (!(enter && !_iEnter)) return;
        if (_createGame.Cursor != CreateGameWizard.Field.Create) return;
        if (_createGame.TryCreate() is not { } request) return;
        _gameClient ??= new LobbyGameClient(_http, _lobbyUrl, () => _identityClient?.SessionBearer);
        var client = _gameClient;
        var target = _createGame.SelectedServer;
        _ = Task.Run(async () =>
        {
            var (id, error) = await client.CreateAsync(request).ConfigureAwait(false);
            lock (_createGameLock)
            {
                _createGame.Acknowledge(id, error);
                if (id is not null && target is not null)
                    _createGameJoinPending = (target.Address, target.Port);
            }
        });
    }

    private void DrawCreateGame(float w, float h, Color titleCol, Color dimCol, Color textCol)
    {
        DrawIntroTitle(w, "Create Game", "Server, core, map and game name  -  the lobby creates the mission", titleCol, dimCol);
        CreateGameWizard.Catalog options;
        lock (_createGameLock) options = _createGame.Options;
        var rows = new (string Label, CreateGameWizard.Field Field)[]
        {
            ("SERVER: " + (_createGame.SelectedServer?.Name ?? (_createGameLoading ? "loading…" : "none")), CreateGameWizard.Field.Server),
            ("CORE:   " + (_createGame.SelectedCore ?? "-"), CreateGameWizard.Field.Core),
            ("MAP:    " + (_createGame.SelectedMap ?? (_createGameLoading ? "loading…" : "none")), CreateGameWizard.Field.Map),
            ("GAME NAME: " + (_createGame.GameName.Length > 0 ? _createGame.GameName : "<type a name>"), CreateGameWizard.Field.Name),
            ("CREATE GAME", CreateGameWizard.Field.Create),
        };
        float y = h * 0.32f;
        foreach (var (label, field) in rows)
        {
            bool selected = _createGame.Cursor == field;
            if (selected)
                _spriteBatch.Draw(_whitePixel, new Rectangle(50, (int)y - 4, (int)(w - 100f), 30),
                    new Color(0.08f, 0.35f, 0.33f, 0.55f));
            _spriteBatch.DrawString(_hudSmall, (selected ? "> " : "  ") + label,
                new Vector2(64f, y), selected ? titleCol : textCol);
            y += 36f;
        }
        string status = _createGameError.Length > 0 ? _createGameError : _createGame.Status;
        if (status.Length > 0)
            _spriteBatch.DrawString(_hudSmall, status, new Vector2(64f, y + 12f), new Color(1f, 0.72f, 0.35f));
        var hint = _hudSmall.MeasureString("UP/DOWN row   LEFT/RIGHT choose   TYPE name   ENTER create   ESC back");
        _spriteBatch.DrawString(_hudSmall, "UP/DOWN row   LEFT/RIGHT choose   TYPE name   ENTER create   ESC back",
            new Vector2((w - hint.X) / 2f, h - 40f), dimCol);
    }
}
