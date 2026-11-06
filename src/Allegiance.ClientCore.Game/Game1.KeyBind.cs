using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Allegiance.ClientCore.MonoGame.Rendering;
using XnaKeys = Microsoft.Xna.Framework.Input.Keys;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P625 - wires the in-game rebind popup (<see cref="KeyBindingController"/> +
/// <see cref="KeyBindingRenderer"/>) into Game1: the Controls open key (F6,
/// action <c>OpenKeyBindings</c>), per-frame popup input, and its top-most
/// draw. All mutations flow through <see cref="KeyBindingStore"/> so the help
/// overlay reflects a rebind immediately.
/// </summary>
public partial class Game1
{
    private KeyBindingController _keyBindController = null!;
    private KeyBindingRenderer _keyBindRenderer = null!;
    private bool _keyBindVisible;
    private bool _keyBindOpenKeyWasDown;
    private KeyboardState _keyBindPrevKs;
    /// <summary>P941 — the layout preset the virtual-joystick default was derived
    /// from, so a preset switch inside the popup re-applies it once.</summary>
    private InputLayout _keyBindLayoutSeen;

    private void InitKeyBindingUi(SpriteBatch sb)
    {
        // P941 — the scripted training-proof pilot (ALLEGIANCE_TRAIN_PROOF=1)
        // drives the pre-P941 port key set (raw SPACE to fire, W / LeftShift to
        // fly and boost), so a proof run pins the Port layout preset instead of
        // the fresh-profile native default. The native layout is exercised by the
        // P941 tests and by the ALLEGIANCE_P941_PROOF real-window pairs.
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_TRAIN_PROOF") == "1"
            && Rendering.KeyBindingStore.Instance.Map.Layout != InputLayout.Port)
        {
            Rendering.KeyBindingStore.Instance.Map.SetLayout(InputLayout.Port);
            // Persist it: the proof opens/closes the Controls popup, and Cancel
            // (native Close -> reload) must not fall back to the native preset.
            Rendering.KeyBindingStore.Instance.Save();
            Console.WriteLine("[keymap] training-proof run pins the Port layout preset");
        }

        // P757 — native OK-versus-Cancel persistence (trekinput.cpp:2436-2458,
        // 3005-3030): the popup mutates the LIVE map (so the help overlay and
        // the action lookups reflect a rebind immediately, like native's
        // in-memory map), but nothing is written until OK; Cancel (Esc) reloads
        // the persisted map and discards the pending changes.
        _keyBindController = new KeyBindingController(
            Rendering.KeyBindingStore.Instance.Map,
            onCommit: () => Rendering.KeyBindingStore.Instance.Save(),
            onDiscard: () => Rendering.KeyBindingStore.Instance.Reload());
        _keyBindRenderer = new KeyBindingRenderer(sb, _whitePixel, _hudSmall, _keyBindController);
        _keyBindPrevKs = Keyboard.GetState();

        // P941 — a fresh profile's native layout is the virtual-joystick mode
        // native's training text assumes ("Left click to fire your main weapon",
        // training/mission3.cpp:646): the mouse buttons follow
        // mouseButtonCommandMap (WinTrek.cpp:9289-9298 enables the virtual
        // joystick, Game1.MousePick.cs implements the same map). The pre-P941
        // port layout keeps the pointer default (a click picks; mouse flight on V
        // is opt-in), exactly as before.
        _keyBindLayoutSeen = _keyBindController.Map.Layout;
        _mouseFlightEnabled = _keyBindLayoutSeen == InputLayout.Native;
        IsMouseVisible = CommandCameraActive || _minimapExpanded || !_mouseFlightEnabled;
        Console.WriteLine("[keymap] Controls popup ready (open: "
            + Rendering.KeyBindingStore.Instance.Map.BindingOf("OpenKeyBindings").Label + ")"
            + $", layout {_keyBindController.LayoutName}, mouse flight {(_mouseFlightEnabled ? "ON" : "OFF")}");
    }

    private void DrawKeyBindingOverlay()
    {
        if (_keyBindVisible)
            _keyBindRenderer.Draw(GraphicsDevice.Viewport);
    }

    /// <summary>Toggle the Controls (rebind) popup. Opening it hides the help
    /// overlay; closing reveals whatever was beneath.</summary>
    private void ToggleKeyBindings()
    {
        if (!_keyBindVisible)
        {
            _keyBindVisible = true;
            _helpVisible = false;
            Console.WriteLine("[keymap] Controls open");
        }
        else
        {
            _keyBindVisible = false;
            _keyBindController.Close();
            Console.WriteLine("[keymap] Controls closed");
        }
    }

    /// <summary>Per-frame input while the Controls popup is visible. Edges are
    /// computed against the previous frame's keyboard state so a held key can't
    /// repeat an action (same discipline as every other in-flight toggle).
    /// P757 — native popup keys (trekinput.cpp:3005-3030): Space = OK
    /// (SaveMap + close), Esc = Cancel (reload + close), Enter = edit selection,
    /// Delete = delete mapping.</summary>
    private void UpdateKeyBindingPopup(KeyboardState ks)
    {
        bool up = Pressed(ks, XnaKeys.Up), down = Pressed(ks, XnaKeys.Down);
        bool enter = Pressed(ks, XnaKeys.Enter), r = Pressed(ks, XnaKeys.R);
        bool esc = Pressed(ks, XnaKeys.Escape), y = Pressed(ks, XnaKeys.Y), n = Pressed(ks, XnaKeys.N);
        bool space = Pressed(ks, XnaKeys.Space);
        bool preset = Pressed(ks, XnaKeys.P);

        switch (_keyBindController.Phase)
        {
            case KeyBindPhase.Browse:
                if (up) _keyBindController.MoveUp();
                else if (down) _keyBindController.MoveDown();
                if (preset) _keyBindController.CycleLayout();   // P941 — native / port preset
                if (space)
                {
                    // OnButtonOK — persist and close (trekinput.cpp:3025-3028).
                    _keyBindController.Commit();
                    _keyBindVisible = false;
                    Console.WriteLine("[keymap] Controls OK - bindings saved");
                }
                else if (enter) _keyBindController.SelectCurrent();
                else if (r) _keyBindController.RequestReset();
                else if (esc)
                {
                    // OnButtonCancel — discard and close (trekinput.cpp:3013-3015).
                    _keyBindController.Cancel();
                    if (_keyBindController.Phase == KeyBindPhase.Closed) _keyBindVisible = false;
                }
                break;

            case KeyBindPhase.Capture:
                if (esc) { _keyBindController.Cancel(); }
                else if (NewlyPressedNonModifier(ks) is { } k)
                {
                    bool ctrl = ks.IsKeyDown(XnaKeys.LeftControl) || ks.IsKeyDown(XnaKeys.RightControl);
                    bool alt = ks.IsKeyDown(XnaKeys.LeftAlt) || ks.IsKeyDown(XnaKeys.RightAlt);
                    bool shift = ks.IsKeyDown(XnaKeys.LeftShift) || ks.IsKeyDown(XnaKeys.RightShift);
                    _keyBindController.CaptureKey(k, ctrl, alt, shift);
                }
                break;

            case KeyBindPhase.Conflict:
                if (y) _keyBindController.Answer(true);
                else if (n || esc) _keyBindController.Answer(false);
                break;

            case KeyBindPhase.ConfirmReset:
                if (y) _keyBindController.Answer(true);
                else if (n || esc) _keyBindController.Answer(false);
                break;
        }

        _keyBindPrevKs = ks;

        // P941 — a preset switch inside the popup (P) re-applies the layout's
        // virtual-joystick default: native's map assumes the mouse as a virtual
        // joystick (training text "left click to fire"), the port preset keeps the
        // pointer default. Only a CHANGE is applied, so a V toggle during play is
        // never overridden by opening/closing the popup.
        var layoutNow = _keyBindController.Map.Layout;
        if (layoutNow != _keyBindLayoutSeen)
        {
            _keyBindLayoutSeen = layoutNow;
            _mouseFlightEnabled = layoutNow == InputLayout.Native;
            IsMouseVisible = CommandCameraActive || _minimapExpanded || !_mouseFlightEnabled;
            if (_mouseFlightEnabled) _mouseFlight.Enable(); else _mouseFlight.Reset();
            Console.WriteLine($"[keymap] preset {_keyBindController.LayoutName} -> mouse flight {(_mouseFlightEnabled ? "ON" : "OFF")}");
        }
    }

    private bool Pressed(KeyboardState ks, XnaKeys key)
        => ks.IsKeyDown(key) && !_keyBindPrevKs.IsKeyDown(key);

    /// <summary>Any key that went down THIS frame, excluding modifier keys (a
    /// modifier alone never rebinds). Returns null when nothing new was pressed.</summary>
    private XnaKeys? NewlyPressedNonModifier(KeyboardState ks)
    {
        foreach (XnaKeys k in System.Enum.GetValues<XnaKeys>())
        {
            if (k == XnaKeys.None) continue;
            if (k is XnaKeys.LeftShift or XnaKeys.RightShift or XnaKeys.LeftControl
                or XnaKeys.RightControl or XnaKeys.LeftAlt or XnaKeys.RightAlt) continue;
            if (ks.IsKeyDown(k) && !_keyBindPrevKs.IsKeyDown(k))
                return k;
        }
        return null;
    }

    // -- real-window auto-proof (ALLEGIANCE_KEYMAP_PROOF=1) -----------------

    private bool _keymapProofStarted;
    private bool _keymapProofDone;
    private double _keymapProofElapsed;
    private int _keymapProofStep;

    /// <summary>Scripted real-window proof of the P625b migration: the F6
    /// Controls popup rebinds ORDER-ACCEPT and MOUSE-FLIGHT (two of the commands
    /// that were hard-coded to Home/M at P625) to free keys, and the LIVE help
    /// overlay shows the new keys. Driven by ALLEGIANCE_KEYMAP_PROOF=1 so the
    /// real client window's backbuffer can be photographed (live-proof.sh).
    /// Sequence: (1) open the Controls popup; (2) rebind AcceptCommand ->
    /// Numpad1 and ToggleMouse -> Numpad2; (3) close and open the live help
    /// overlay. Each step requests a real backbuffer screenshot (flushed at Draw
    /// end).</summary>
    private void RunKeymapProof(double dtSec)
    {
        if (_keymapProofDone) return;
        _keymapProofElapsed += dtSec;

        if (!_keymapProofStarted)
        {
            _keymapProofStarted = true;
            _keymapProofElapsed = 0;
            ToggleKeyBindings();                 // open Controls (browse)
            _screenshotRequested = true;         // shot A: the Controls list
            Console.WriteLine("[keymap-proof] shot A: Controls open");
            _keymapProofStep = 1;
            return;
        }

        switch (_keymapProofStep)
        {
            case 1:
                if (_keymapProofElapsed > 2.0)
                {
                    // Rebind the two hard-coded commands: AcceptCommand (Home)
                    // -> Numpad1, ToggleMouse (M) -> Numpad2. Neither is a
                    // catalogue default, so no conflict.
                    while (_keyBindController.CurrentAction != "AcceptCommand")
                        _keyBindController.MoveDown();
                    _keyBindController.SelectCurrent();
                    _keyBindController.CaptureKey(XnaKeys.NumPad1, false, false, false);

                    while (_keyBindController.CurrentAction != "ToggleMouse")
                        _keyBindController.MoveDown();
                    _keyBindController.SelectCurrent();
                    _keyBindController.CaptureKey(XnaKeys.NumPad2, false, false, false);

                    // Prove the map now resolves the NEW keys (the exact
                    // predicate every handler uses), and log it for the report.
                    var map = Rendering.KeyBindingStore.Instance.Map;
                    Console.WriteLine($"[keymap-proof] AcceptCommand -> {map.BindingOf("AcceptCommand").Label}, "
                        + $"ToggleMouse -> {map.BindingOf("ToggleMouse").Label}");
                    _screenshotRequested = true; // shot B: Controls after rebinds
                    Console.WriteLine("[keymap-proof] shot B: AcceptCommand+Numpad1, ToggleMouse+Numpad2 (rebound)");
                    _keymapProofElapsed = 0;
                    _keymapProofStep = 2;
                }
                break;
            case 2:
                if (_keymapProofElapsed > 2.0)
                {
                    // Close Controls, open the LIVE help overlay (reflects both).
                    ToggleKeyBindings();
                    _helpVisible = true;
                    _screenshotRequested = true; // shot C: help after rebinds
                    Console.WriteLine("[keymap-proof] shot C: live help reflects Numpad1/Numpad2");
                    _keymapProofElapsed = 0;
                    _keymapProofStep = 3;
                }
                break;
            case 3:
                if (_keymapProofElapsed > 3.0)
                {
                    _keymapProofDone = true;
                    Console.WriteLine("[keymap-proof] done (three real frames requested)");
                }
                break;
        }
    }

    // -- P941 real-window proof (ALLEGIANCE_P941_PROOF=1) --------------------

    private bool _p941ProofStarted;
    private bool _p941ProofDone;
    private double _p941ProofElapsed;
    private double _p941AirborneSeconds;
    private double _p941WaitSeconds;
    private int _p941ProofStep;

    /// <summary>
    /// P941 — scripted real-window proof of the layout presets and the F1
    /// overview. Sequence: (1) open Controls (F6) on the fresh profile's native
    /// layout; (2) P cycles to the Port layout; (3) P cycles back, OK saves, close;
    /// (4) open the help overview on its CONTROLS page (the live key table);
    /// (5) the FLIGHT page (live prose). Each step queues a named real-backbuffer
    /// PNG (<c>--autoshot-dir</c>) whose name carries the HUD style, so
    /// live-proof.sh can capture the Classic and Modern pairs.
    /// </summary>
    private void RunP941Proof(double dtSec)
    {
        if (_p941ProofDone) return;
        // Wait for the hangar/loadout screen to clear (the live proof launches a
        // ship first) so the frames show the flight HUD behind the overlays. The
        // dock state arrives a frame or two after the mission starts, so require a
        // continuous airborne second before opening the Controls popup — opening
        // it earlier would compose-gate the docked auto-launch. A docked player
        // still gets the frames after 15 s (the overlays draw over the hangar).
        _p941WaitSeconds += dtSec;
        _p941AirborneSeconds = _dockedActive ? 0 : _p941AirborneSeconds + dtSec;
        if (!_p941ProofStarted && _p941AirborneSeconds < 1.0 && _p941WaitSeconds < 15.0) return;
        _p941ProofElapsed += dtSec;
        string style = _hudSettings.Style.ToString().ToLowerInvariant();

        void Shot(string name)
        {
            _autoShotName = $"p941_{name}_{style}";
            _autoShotScheduled = true;
            Console.WriteLine($"[p941-proof] shot {_autoShotName}");
        }

        if (!_p941ProofStarted)
        {
            _p941ProofStarted = true;
            _p941ProofElapsed = 0;
            ToggleKeyBindings();
            Shot("controls_native");
            Console.WriteLine($"[p941-proof] Controls open: {_keyBindController.LayoutName}, "
                + $"FireWeapon={_keyBindController.Map.LabelOf("FireWeapon")}, "
                + $"FireBooster={_keyBindController.Map.LabelOf("FireBooster")}");
            _p941ProofStep = 1;
            return;
        }

        switch (_p941ProofStep)
        {
            case 1:
                if (_p941ProofElapsed > 2.0)
                {
                    _keyBindController.CycleLayout();
                    Shot("controls_port");
                    Console.WriteLine($"[p941-proof] preset now {_keyBindController.LayoutName}, "
                        + $"RollLeft={_keyBindController.Map.LabelOf("RollLeft")}");
                    _p941ProofElapsed = 0;
                    _p941ProofStep = 2;
                }
                break;
            case 2:
                if (_p941ProofElapsed > 2.0)
                {
                    _keyBindController.CycleLayout();   // back to native
                    _keyBindController.Commit();        // OK: persist + close
                    _keyBindVisible = false;
                    _helpVisible = true;
                    _helpManual = new Rendering.HelpManualModel();
                    _helpManual.GoTo("hlpControls");
                    Shot("help_controls");
                    Console.WriteLine("[p941-proof] help overview: CONTROLS page (live key table)");
                    _p941ProofElapsed = 0;
                    _p941ProofStep = 3;
                }
                break;
            case 3:
                if (_p941ProofElapsed > 2.0)
                {
                    _helpManual.GoTo("hlpFlight");
                    Shot("help_flight");
                    Console.WriteLine("[p941-proof] help overview: FLIGHT page (live prose)");
                    _p941ProofElapsed = 0;
                    _p941ProofStep = 4;
                }
                break;
            case 4:
                if (_p941ProofElapsed > 3.0)
                {
                    _p941ProofDone = true;
                    Console.WriteLine("[p941-proof] done (four real frames queued)");
                }
                break;
        }
    }
}
