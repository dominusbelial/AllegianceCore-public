using Allegiance.ClientCore.MonoGame.Diagnostics;
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
    private bool _skirmishRememberedApplied, _skirmishSeededByBenchmark;
    private bool _observerProofWaitExit;
    private (long Generation, short Ship) _humanCommanderSeat = (-1, -1);
    private double _humanCommanderRetryAt;

    private void UpdateSkirmishMenu(KeyboardState keys, bool up, bool down, bool enter, bool esc)
    {
        // SP-6 — restore the player's remembered local skirmish choices (player.json) once,
        // unless a saved benchmark recommendation already seeded the screen this run.
        if (!_skirmishRememberedApplied)
        {
            _skirmishRememberedApplied = true;
            if (!_skirmishSeededByBenchmark && IntroPreferences.Load().Skirmish is { } remembered)
                _skirmishScreen.ApplyRemembered(remembered);
        }
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
                    string pilot = _pilotBuf.Length > 0 ? _pilotBuf.ToString() : _playerName;
                    _skirmishStart = Task.Run(() => SkirmishHost.StartAsync(choices, cancellationToken: token, commanderPilotName: pilot));
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
        if (!_observerAutoStarted
            && (Environment.GetEnvironmentVariable("ALLEGIANCE_SP5_PROOF") is { Length: > 0 }
                // P789 — the recorder on/off A-B auto-starts an observer skirmish
                // (size via ALLEGIANCE_SKIRMISH_SIZE) WITHOUT the SP-5 proof's
                // self-exit, so the client stays up for a fixed measurement window.
                || Environment.GetEnvironmentVariable("ALLEGIANCE_SKIRMISH_AUTO") is { Length: > 0 }))
        {
            _observerAutoStarted = true;
            _skirmishCancel = new();
            // SP-6 — ALLEGIANCE_SKIRMISH_SPEED lets a live proof show a chosen speed's status bar.
            var observerOptions = new SkirmishOptions { StartAsSpectator = true };
            if (Enum.TryParse<SkirmishSpeed>(Environment.GetEnvironmentVariable("ALLEGIANCE_SKIRMISH_SPEED"), ignoreCase: true, out var speed))
                observerOptions = observerOptions with { Speed = speed };
            // P789 — ALLEGIANCE_SKIRMISH_SIZE picks the battle size for the
            // proof (e.g. Epic for the recorder on/off A-B measurement).
            if (Enum.TryParse<SkirmishSize>(Environment.GetEnvironmentVariable("ALLEGIANCE_SKIRMISH_SIZE"), ignoreCase: true, out var proofSize))
                observerOptions = observerOptions with { Size = proofSize };
            _skirmishStart = SkirmishHost.StartAsync(observerOptions, cancellationToken: _skirmishCancel.Token);
        }
        if (_skirmishStart is { IsCompleted: true } start)
        {
            _skirmishStart = null;
            try
            {
                _skirmish = start.GetAwaiter().GetResult();
                if (_skirmishCancel!.IsCancellationRequested) { StopSkirmish(); return; }
                _skirmishSideRequested = false;
                _humanCommanderSeat = (-1, -1);
                // P789 — every skirmish records its performance; a Showcase-speed
                // battle is a Showcase match, everything else a normal one. The
                // benchmark flow starts its own recorder per measured run.
                if (_benchmark is null)
                    StartSessionRecording(_skirmish.Options.Speed == SkirmishSpeed.Showcase
                        ? SessionMatchKind.Showcase : SessionMatchKind.Normal);
                if (_benchmark is not null) _playerName = Allegiance.SinglePlayer.Benchmark.BenchmarkCommand.PilotName;
                else if (_pilotBuf.Length > 0) _playerName = _pilotBuf.ToString();
                var preferences = IntroPreferences.Load();
                if (_benchmark is null) { preferences.Pilot = _playerName; preferences.Skirmish = SkirmishPrefs.From(_skirmish.Options); preferences.Save(); }
                if (_benchmark is null && _skirmish.Options.StartAsSpectator) { ObserveBenchmarkSharingSession(); StopIntroScan(); EnterLocalSpectator(); return; }
                // Local ports are ephemeral; do not save them as a remembered multiplayer server.
                _returnToIntroOnFailure = true;
                StopIntroScan();
                BeginConnect("127.0.0.1", _skirmish.Port);
                _disconnectTransitioned = false;
                _readyConfirmedShip = null;
                _nextMatchGeneration = _session.World.MissionGeneration;
                _ownReady = true;
                if (_benchmark is null && _skirmish.Options.SpectatorDefault) { _directorActive = true; }
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
        // Keep the human's own payday when taking command (native opt-out,
        // FedSrv/FedSrv.CPP:5535-5544). Workers vote/donate via ordinary AUTODONATE.
        if (_skirmish?.Options.Commander == SkirmishCommander.You)
            lock (_session.Gate)
                if (_session.World.MyShipId is { } me && _session.World.MySideId == 0
                    && _humanCommanderSeat != (_session.World.MissionGeneration, me) && _nowSeconds >= _humanCommanderRetryAt)
                {
                    _session.SendAutoDonate(-1);
                    _humanCommanderRetryAt = _nowSeconds + 1;
                    if (_session.World.IsTeamLeader) _humanCommanderSeat = (_session.World.MissionGeneration, me);
                }
        if (_skirmish is not null && !LocalSpectating && _benchmark is null)
            lock (_session.Gate)
                if (_session.World.MyShipId is { } pilot && _session.World.Ships.TryGetValue(pilot, out var ship))
                    _skirmish.SetFocusSector(ship.SectorId);
        UpdateSeatChange();
        HandleSkirmishSeatKeys(keys);
        bool ctrl = keys.IsKeyDown(Keys.LeftControl) || keys.IsKeyDown(Keys.RightControl);
        bool pauseKey = keys.IsKeyDown(Keys.Pause) || (ctrl && keys.IsKeyDown(Keys.P));
        bool exitKey = ctrl && keys.IsKeyDown(Keys.F10);
        if (_skirmish is not null && _benchmark is null)
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
        // P789 — leaving a Showcase (or any match) early still latches the
        // recorder's summary once, off the frame path.
        CaptureSessionPerformance();
        _skirmishCancel?.Cancel();
        _session.Dispose();
        _skirmish?.Dispose();
        _skirmish = null;
        _localSpectating = false;
        _seatChange = null;
        _requestedSeat = null;
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
        if (_skirmish is null || _benchmark is not null || (LocalSpectating && (_helpVisible || _keyBindVisible || _creditsVisible))) return;
        string status = _skirmish.Failure ?? (_skirmish.Paused ? "PAUSED"
            : $"Skirmish [{_skirmish.Options.Speed} speed] - " + (_skirmish.Options.Commander == SkirmishCommander.You
                ? "You command | I invest | F3 command view" : "AI commander active"));
        if (_seatChange is not null) status = "Changing seat (waiting for a playable match)...";
        if (_skirmishError.Length > 0) status = _skirmishError;
        status += "   Ctrl+1 Join Alpha   Ctrl+2 Join Bravo   Ctrl+0 Spectate   Ctrl+P Pause   Ctrl+F10 End";
        _spriteBatch.Begin();
        _spriteBatch.Draw(_whitePixel, new Rectangle(0, 0, GraphicsDevice.Viewport.Width, LocalSpectating ? 126 : 30), new Color(0, 0, 0, 210));
        float scale = Math.Min(1f, (GraphicsDevice.Viewport.Width - 24) / _hudSmall.MeasureString(status).X);
        _spriteBatch.DrawString(_hudSmall, status, new Vector2(12, 6), _skirmish.Failure is null ? Color.Cyan : Color.OrangeRed, 0, Vector2.Zero, scale, SpriteEffects.None, 0);
        if (LocalSpectating && _observation is { } frame)
        {
            _spriteBatch.DrawString(_hudSmall, $"SPECTATING  {_skirmishCamera.Mode}  {TimeSpan.FromSeconds(frame.Seconds):hh\\:mm\\:ss}  {frame.Stage}  | V camera, PgUp/PgDn subject, F8 free, F9 action, F1 help", new Vector2(12, 30), Color.White);
            for (int i = 0; i < frame.Sides.Length; i++)
            {
                var side = frame.Sides[i];
                string research = side.Research.Length > 0 ? side.Research : "No active research";
                string line = $"{(side.Id == 0 ? "Alpha" : "Bravo")} ({side.Name})   Kills {side.Kills} / Deaths {side.Deaths}   {research}";
                if (line.Length > 155) line = line[..155];
                _spriteBatch.DrawString(_hudSmall, line, new Vector2(12, 52 + i * 22), i == 0 ? Color.Cyan : Color.Orange);
            }
            string skill = SkirmishSkillCounters(_skirmish.Stats);
            if (skill.Length > 0) _spriteBatch.DrawString(_hudSmall, skill, new Vector2(12, 98), Color.LightGreen);
        }
        _spriteBatch.End();
    }
}
