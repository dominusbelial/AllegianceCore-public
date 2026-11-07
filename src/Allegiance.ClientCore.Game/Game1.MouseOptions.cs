using Allegiance.ClientCore.MonoGame.Flight;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P948 — mouse-flight options: persist native's <c>FlipY</c> (invert mouse Y)
/// and <c>MouseSensitivity</c> in the player's <c>player.json</c>, load them at
/// startup, save on every change, expose them on the in-game settings panel
/// (Ctrl+F6, reachable in both the native and the port layout) and drive the
/// in-flight <c>InvertMouse</c> / <c>MouseSensitivityUp/Down</c> actions.
///
/// <para>Native stores both in its one preferences blob and exposes them in
/// Game Options — <c>idmToggleFlipY</c> 'Y' (<c>WinTrek.cpp:4536</c>,
/// <c>ToggleFlipY</c> <c>:5297-5305</c> saves <c>FlipY</c>) and the "Mouse
/// Options" submenu's Raise/Lower items (<c>:4612-4613</c>, <c>AdjustMouseSens</c>
/// <c>:5512-5531</c> saves <c>MouseSensitivity</c>). This port follows the same
/// single-store pattern: <see cref="IntroPreferences"/> (P731), beside Force
/// Feedback, the chat filter and the 3D-sound switch.</para>
/// </summary>
public sealed partial class Game1
{
    /// <summary>P948 — the <c>player.json</c> path this session reads/writes. Null
    /// means the standard per-user file (<see cref="IntroPreferences.FilePath"/>);
    /// tests and an isolated proof set it to a temp file so they never touch the
    /// operator's real profile.</summary>
    private string? _playerPrefsPath = null;

    // Transient HUD readout + auto-repeat state for the sensitivity keys. Created
    // lazily so a headless test harness that builds Game1 with
    // RuntimeHelpers.GetUninitializedObject and calls PrimeInFlightKeyLatches does
    // not dereference a null field.
    private MouseFlightNotice? _mouseFlightNotice;
    private KeyRepeat? _mouseSensRepeatUp;
    private KeyRepeat? _mouseSensRepeatDown;

    /// <summary>Startup: read <c>player.json</c> and apply the persisted mouse
    /// options to the live controller. Native does the same in
    /// <c>TrekResources::Initialize</c> (<c>WinTrek.cpp:2809-2811</c> sensitivity;
    /// <c>:3048</c> FlipY). <paramref name="path"/> is the test seam; production
    /// passes <c>null</c> for the standard per-user file.</summary>
    private void LoadMouseOptions(string? path = null)
    {
        var prefs = IntroPreferences.Load(path ?? _playerPrefsPath);
        MouseOptions.Apply(_mouseFlight, prefs);
        Console.WriteLine($"[mouse] options loaded: invert Y {(_mouseFlight.InvertPitch ? "ON" : "off")}, "
            + $"sensitivity {MouseOptions.Percent(_mouseFlight.MouseSensitivity)}");
    }

    /// <summary>Persist the live mouse options into the player's preferences file,
    /// keeping every other remembered field intact.</summary>
    private void SaveMousePreferences()
    {
        EnsureOptionsPrefs(_playerPrefsPath);
        MouseOptions.Capture(_mouseFlight, _optionsPrefs);
        _optionsPrefs.Save(_playerPrefsPath);
    }

    /// <summary>P948 — set invert-Y and save. The single path used by the
    /// in-flight key, the in-game settings row and the intro Game Options row, so
    /// they can never diverge. Like the controller property, this only affects
    /// FUTURE pitch deltas, never the stored deflection.</summary>
    private void SetInvertMouseY(bool inverted)
    {
        _mouseFlight.InvertPitch = inverted;
        SaveMousePreferences();
        Console.WriteLine($"[mouse] invert Y -> {(inverted ? "ON" : "off")} (saved)");
    }

    /// <summary>P948 — nudge the sensitivity by native's 0.01 step, clamp to
    /// [0.1, 2.0], save and show the value briefly on the HUD.</summary>
    private void StepMouseSensitivity(int steps)
    {
        float next = MouseOptions.Step(_mouseFlight.MouseSensitivity, steps);
        if (MathF.Abs(next - _mouseFlight.MouseSensitivity) < 1e-5f)
        {
            // Already at the end stop: still flash the readout so the player sees
            // why nothing moved (native's label says "(highest)"/"(lowest)").
            (_mouseFlightNotice ??= new MouseFlightNotice()).Show(MouseOptions.Notice(next));
            Console.WriteLine($"[mouse] sensitivity at end stop {MouseOptions.Percent(next)}");
            return;
        }
        _mouseFlight.MouseSensitivity = next;
        SaveMousePreferences();
        (_mouseFlightNotice ??= new MouseFlightNotice()).Show(MouseOptions.Notice(next));
        Console.WriteLine($"[mouse] sensitivity {(steps > 0 ? "UP" : "DOWN")} -> {next:0.00} "
            + $"({MouseOptions.Percent(next)}) (saved)");
    }

    /// <summary>P948 — the in-flight mouse-option keys, extracted from
    /// <c>UpdateFlying</c> so the edge/repeat/save rules are directly testable:
    /// <c>InvertMouse</c> flips+SAVES invert-Y; <c>MouseSensitivityUp/Down</c>
    /// step 0.01, auto-repeat while held (see <see cref="KeyRepeat"/>) and save.
    /// Both stay <c>composing</c>-gated so typing in chat never changes them.</summary>
    private void UpdateMouseOptionKeys(KeyboardState ks, bool composing, float dtSeconds)
    {
        bool iInvert = ActionDown(ks, "InvertMouse");
        if (!composing && iInvert && !_iKeyWasDown)
            SetInvertMouseY(!_mouseFlight.InvertPitch);
        _iKeyWasDown = iInvert;

        bool sensDown = !composing && ActionDown(ks, "MouseSensitivityDown");
        bool sensUp = !composing && ActionDown(ks, "MouseSensitivityUp");
        int steps = 0;
        if ((_mouseSensRepeatDown ??= new KeyRepeat()).Update(sensDown, dtSeconds)) steps--;
        if ((_mouseSensRepeatUp ??= new KeyRepeat()).Update(sensUp, dtSeconds)) steps++;
        if (steps != 0) StepMouseSensitivity(steps);
    }

    /// <summary>Age the transient mouse-option readout. Called every frame from
    /// <c>Update</c> (not only in flight), so a value changed on the intro Options
    /// screen expires there instead of greeting the pilot on the first flight
    /// frame.</summary>
    private void UpdateMouseOptionNotice(float dtSeconds) => _mouseFlightNotice?.Tick(dtSeconds);

    /// <summary>The transient mouse-option readout for the HUD frame, or null when
    /// it has faded (or the HUD is not showing a flight frame).</summary>
    private Rendering.MouseOptionNotice? MouseOptionNoticeView =>
        _mouseFlightNotice is { Visible: true } notice
            ? new Rendering.MouseOptionNotice(notice.Text, notice.Opacity)
            : null;

    // -- real-window proof (ALLEGIANCE_P948_PROOF=1) --------------------------

    private bool _p948ProofStarted;
    private bool _p948ProofDone;
    private double _p948ProofElapsed;
    private double _p948ProofAirborne;
    private double _p948ProofWait;
    private int _p948ProofStep;
    private bool _p948ShotPending;

    /// <summary>
    /// P948 — scripted real-window proof: open the in-game settings panel
    /// (Ctrl+F6) on the two new mouse rows and capture a real backbuffer frame for
    /// each, so the Classic and Modern pairs show the toggle and the sensitivity
    /// value. The panel is drawn in <c>EndDraw</c>, AFTER the ordinary autoshot
    /// flush in <c>Draw</c>, so this proof latches the shot and flushes it from
    /// <see cref="FlushP948ProofShot"/> right after the panel is drawn.
    /// </summary>
    private void RunP948Proof(double dtSec)
    {
        if (_p948ProofDone) return;
        // Only a real flight frame counts (the pre-launch ReadyCheck and the
        // hangar are docked/hangar states where the style pair would be invisible).
        _p948ProofAirborne = _state == ClientState.Flying && !_dockedActive
            ? _p948ProofAirborne + dtSec : 0;
        _p948ProofWait += dtSec;
        if (!_p948ProofStarted && _p948ProofAirborne < 1.0 && _p948ProofWait < 100.0) return;
        _p948ProofElapsed += dtSec;
        string style = _hudSettings.Style.ToString().ToLowerInvariant();

        void Shot(string name)
        {
            _autoShotName = $"p948_{name}_{style}";
            _p948ShotPending = true;
            Console.WriteLine($"[p948-proof] shot {_autoShotName}");
        }

        if (!_p948ProofStarted)
        {
            _p948ProofStarted = true;
            _p948ProofElapsed = 0;
            _graphicsSettingsVisible = true;
            _graphicsRow = GraphicsRowInvertMouseY;
            _graphicsFirstRow = 0;
            Shot("options_inverty");
            Console.WriteLine($"[p948-proof] settings panel open row {_graphicsRow} "
                + $"(invert Y {(_mouseFlight.InvertPitch ? "ON" : "off")}, "
                + $"sensitivity {MouseOptions.Percent(_mouseFlight.MouseSensitivity)})");
            _p948ProofStep = 1;
            return;
        }

        switch (_p948ProofStep)
        {
            case 1:
                if (_p948ProofElapsed > 2.0)
                {
                    // Left/Right on the invert row: prove the panel toggle flips and
                    // saves the same live value the flight key drives.
                    _graphicsRow = GraphicsRowInvertMouseY;
                    UpdateGraphicsSettings(new KeyboardState(Keys.Right));
                    _graphicsPrevious = new KeyboardState(Keys.Right);
                    Shot("options_inverty_on");
                    Console.WriteLine($"[p948-proof] invert Y after panel toggle: {_mouseFlight.InvertPitch}");
                    _p948ProofElapsed = 0;
                    _p948ProofStep = 2;
                }
                break;
            case 2:
                if (_p948ProofElapsed > 2.0)
                {
                    _graphicsRow = GraphicsRowMouseSensitivity;
                    Shot("options_sensitivity");
                    Console.WriteLine($"[p948-proof] sensitivity row {_graphicsRow}: "
                        + $"{MouseOptions.Percent(_mouseFlight.MouseSensitivity)}");
                    _p948ProofElapsed = 0;
                    _p948ProofStep = 3;
                }
                break;
            case 3:
                if (_p948ProofElapsed > 2.0)
                {
                    _graphicsSettingsVisible = false;
                    _p948ProofDone = true;
                    Console.WriteLine("[p948-proof] done (three real frames queued)");
                }
                break;
        }
    }

    /// <summary>Called from <c>EndDraw</c> after the settings panel is drawn: flush
    /// the latched P948 frame so it really contains the panel.</summary>
    private void FlushP948ProofShot()
    {
        if (!_p948ShotPending) return;
        _p948ShotPending = false;
        _autoShotScheduled = true;
        FlushScheduledAutoshot();
    }
}
