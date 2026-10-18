using Allegiance.SinglePlayer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly SkirmishScreenModel _skirmishScreen = new();
    private SkirmishHost? _skirmish;
    private Task<SkirmishHost>? _skirmishStart;
    private CancellationTokenSource? _skirmishCancel;
    private Task? _skirmishPause;
    private bool _skLeft, _skRight, _skPauseKey, _skExitKey;
    private string _skirmishError = "";
    private bool _skirmishSideRequested;

    private void UpdateSkirmishMenu(KeyboardState keys, bool up, bool down, bool enter, bool esc)
    {
        bool left = keys.IsKeyDown(Keys.Left), right = keys.IsKeyDown(Keys.Right);
        if (_skirmishStart is null)
        {
            if (up && !_iUp) _skirmishScreen.Move(-1);
            if (down && !_iDown) _skirmishScreen.Move(1);
            if (left && !_skLeft) _skirmishScreen.Change(-1);
            if (right && !_skRight) _skirmishScreen.Change(1);
            if (enter && !_iEnter)
            {
                if (_skirmishScreen.Selected == SkirmishScreenModel.StartRow)
                {
                    _skirmishError = "";
                    _skirmishCancel?.Dispose();
                    _skirmishCancel = new();
                    var choices = _skirmishScreen.Options;
                    var token = _skirmishCancel.Token;
                    _skirmishStart = Task.Run(() => SkirmishHost.StartAsync(choices, cancellationToken: token));
                }
                else if (_skirmishScreen.Selected == SkirmishScreenModel.BackRow) _introView = IntroView.Main;
                else _skirmishScreen.Change(1);
            }
        }
        if (esc && !_iEsc)
        {
            if (_skirmishStart is not null) _skirmishCancel?.Cancel();
            else { _introView = IntroView.Main; _introIndex = 0; }
        }
        _skLeft = left; _skRight = right;
    }

    private void UpdateSkirmish(KeyboardState keys)
    {
        if (_skirmishStart is { IsCompleted: true } start)
        {
            _skirmishStart = null;
            try
            {
                _skirmish = start.GetAwaiter().GetResult();
                if (_skirmishCancel!.IsCancellationRequested) { StopSkirmish(); return; }
                _skirmishSideRequested = false;
                if (_pilotBuf.Length > 0) _playerName = _pilotBuf.ToString();
                var preferences = IntroPreferences.Load();
                preferences.Pilot = _playerName;
                preferences.Save();
                // Local ports are ephemeral; do not save them as a remembered multiplayer server.
                _returnToIntroOnFailure = true;
                StopIntroScan();
                BeginConnect("127.0.0.1", _skirmish.Port);
                _disconnectTransitioned = false;
                _readyConfirmedShip = null;
                _nextMatchGeneration = _session.World.MissionGeneration;
                _ownReady = true;
            }
            catch (OperationCanceledException) { _skirmishError = "Skirmish cancelled."; }
            catch (Exception ex) { _skirmishError = "Could not start skirmish: " + ex.Message; }
        }
        if (_skirmishPause is { IsCompleted: true } pause)
        {
            _skirmishPause = null;
            try { pause.GetAwaiter().GetResult(); }
            catch (Exception ex) { _skirmishError = "Pause failed: " + ex.Message; }
        }
        bool ctrl = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        bool pauseKey = keys.IsKeyDown(Keys.Pause) || (ctrl && keys.IsKeyDown(Keys.P));
        bool exitKey = ctrl && keys.IsKeyDown(Keys.F10);
        if (_skirmish is not null)
        {
            if (pauseKey && !_skPauseKey) ToggleSkirmishPause();
            if (exitKey && !_skExitKey) StopSkirmish();
        }
        _skPauseKey = pauseKey; _skExitKey = exitKey;
    }

    private void ToggleSkirmishPause()
    {
        if (_skirmish is not null && _skirmishPause is null)
            _skirmishPause = _skirmish.SetPausedAsync(!_skirmish.Paused, _skirmishCancel?.Token ?? default);
    }

    private void StopSkirmish()
    {
        _skirmishCancel?.Cancel();
        _session.Dispose();
        _skirmish?.Dispose();
        _skirmish = null;
        _skirmishCancel?.Dispose();
        _skirmishCancel = null;
        _mainMenuVisible = false;
        _state = ClientState.Intro;
        _introView = IntroView.SinglePlayer;
        _skirmishError = "";
    }

    private void DisposeSkirmish()
    {
        _skirmishCancel?.Cancel();
        if (_skirmishStart is { } start)
        {
            try { start.GetAwaiter().GetResult().Dispose(); }
            catch (Exception) { /* Startup already disposes its partially constructed host. */ }
        }
        _skirmish?.Dispose();
        _skirmishCancel?.Dispose();
    }

    private void DrawSkirmishStatus()
    {
        if (_skirmish is null) return;
        string status = _skirmish.Failure ?? (_skirmish.Paused ? "PAUSED" : "Skirmish - AI commander active");
        if (_skirmishError.Length > 0) status = _skirmishError;
        status += "   Ctrl+P pause/resume   Ctrl+F10 end skirmish";
        _spriteBatch.Begin();
        _spriteBatch.Draw(_whitePixel, new Rectangle(0, 0, GraphicsDevice.Viewport.Width, 30), new Color(0, 0, 0, 210));
        _spriteBatch.DrawString(_hudSmall, status, new Vector2(12, 6), _skirmish.Failure is null ? Color.Cyan : Color.OrangeRed);
        _spriteBatch.End();
    }
}
