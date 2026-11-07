using Allegiance.ClientCore.Training;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private sealed record TrainingPreferences(HudStyle Style, string Keymap, Func<string, bool>? Filter,
        CameraMode Camera, RadarMode Radar, ConsolePaneState Panes, TimeSpan FrameCap);
    private TrainingPreferences? _trainingPreferences;
    private bool _trainingQuitConfirm;
    private bool _trainingLaunchRequested;

    // training/trainingmission.cpp:74-80,119-128. The port supports both HUD
    // styles during lessons; preserve the user's style and keymap at every exit.
    internal static readonly TimeSpan TrainingMaxElapsed = TimeSpan.FromSeconds(5);

    private void SaveTrainingPreferences()
    {
        if (_trainingPreferences is not null) return;
        var map = KeyBindingStore.Instance.Map;
        _trainingPreferences = new(_hudSettings.Style, map.ToJson(), map.ActionFilter,
            _cameraMode, _radarMode, _consolePanes, MaxElapsedTime);
        // Native shipping Time::Now retains the complete elapsed interval
        // (zlib/ztime.h:75-77). The framework's 500 ms cap otherwise discards
        // time before TrainingClock and local flight can advance with the host.
        // Bounded, not unlimited: with a fixed timestep MonoGame replays Updates to
        // catch up, and a minimised window or a sleep must not unleash an unbounded
        // burst (native has no catch-up loop). 5 s covers the gate's 2.9 s frames.
        MaxElapsedTime = TrainingMaxElapsed;
        KeyBindingStore.Instance.Reload();
    }

    // All lesson exits use this idempotent owner cleanup, including failed joins,
    // wire departure, connection loss, window close, and explicit quit.
    private void ReleaseTraining()
    {
        if (_trainingSession is null && _embeddedHost is null && _trainingPreferences is null
            && _trainingSlideshow is null) return;
        ClearTrainingSounds();
        _slideSound?.Stop();
        _slideSound?.Dispose();
        _slideSound = null;
        _trainingSlideshow = null;
        _trainingSession?.Terminate();
        _trainingSession?.DetachClientHooks();
        TrainingOwnState.Reset();
        TrainingOverlay.Current = 0;
        TrainingController.EndMission();
        var map = KeyBindingStore.Instance.Map;
        map.ActionFilter = null;
        if (_trainingPreferences is { } saved)
        {
            MaxElapsedTime = saved.FrameCap;
            _hudSettings.Style = saved.Style;
            map.ResetToDefaults();
            map.ApplyJson(saved.Keymap);
            map.ActionFilter = saved.Filter;
            _cameraMode = saved.Camera;
            _radarMode = saved.Radar;
            TrainingRadarLod.Current = (int)saved.Radar;
            _consolePanes = saved.Panes;
        }
        else _consolePanes = default;
        _trainingPreferences = null;
        _trainingInputGate = null;
        _trainingDeathCameraActive = _trainingQuitConfirm = false;
        _overrideTarget = null;
        _overrideUntil = 0;
        _minimapExpanded = _consolePanes.Nav;
        _teamPaneVisible = _consolePanes.Team != TeamConsoleSize.Hidden;
        _investActive = _teleportActive = _mainMenuVisible = _helpVisible = _keyBindVisible = false;
        _trainingCommandPane = true;
        _trainingAutoJoin = _trainingSideRequested = false;
        _dockedActive = _hangarOnly = _hangarQuitConfirm = false;
        lock (_session.Gate) _session.World.ClearTrainingViewCluster();
        _embeddedHost?.Dispose();
        _embeddedHost = null;
        _trainingSession = null;
        _sound?.ReleaseSlideSounds();
    }

    // WinTrek.cpp:3559-3569,3601-3604: quitting a slideshow or lesson asks
    // the same question; cancel leaves it running, confirm skips the postlude.
    private bool RequestTrainingQuit()
    {
        if (_trainingSession is null && _trainingSlideshow is null) return false;
        _trainingQuitConfirm = true;
        return true;
    }

    private void ConfirmTrainingQuit(bool confirm)
    {
        if (!_trainingQuitConfirm) return;
        _trainingQuitConfirm = false;
        if (!confirm) return;
        _trainingSkipPostSlideshow = true;
        ReleaseTraining();
        _session.Dispose();
        ReturnToTrainingScreen();
    }

    // console.cpp:1111-1131 suppresses the ordinary team/hangar departure flow.
    // A real transport/session exit must still release the local host.
    private bool ExitTrainingSession()
    {
        if (_trainingSession is null && _embeddedHost is null) return false;
        _trainingSkipPostSlideshow = true;
        ReleaseTraining();
        _session.Dispose();
        ReturnToTrainingScreen();
        return true;
    }

    private bool UpdateTrainingQuitInput(Microsoft.Xna.Framework.Input.KeyboardState keys)
    {
        if (!_trainingQuitConfirm) return false;
        // Use unfiltered UI keys: the lesson may have disabled every flight key.
        bool confirm = keys.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Enter);
        bool cancel = keys.IsKeyDown(Microsoft.Xna.Framework.Input.Keys.Escape);
        if ((confirm && !_enterWasDown) || (cancel && !_escWasDown))
        {
            // The popup owns this key-down. Keep its held key from becoming a
            // new slideshow/chooser key-down after the popup changes the view
            // (Slideshow.cpp:179-183; WinTrek.cpp:3601-3606).
            _slideInputHeld = true;
            _iEnter = confirm;
            _iEsc = cancel;
            ConfirmTrainingQuit(confirm && !cancel);
        }
        _enterWasDown = confirm;
        _escWasDown = cancel;
        return true;
    }

    internal static bool ApprovedTrainingToggle(bool active, int bit)
    {
        // WinTrek.cpp:9462,9471: approve the state AFTER XOR at the toggle site.
        int state = active ? 0 : bit;
        if (TrainingController.IsTraining) state = TrainingController.ApproveActions(state);
        return (state & bit) != 0;
    }
}
