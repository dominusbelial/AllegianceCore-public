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
        // P949 — the scripted training-proof pilot (ALLEGIANCE_TRAIN_PROOF=1)
        // drives the keyboard Port key set (raw SPACE to fire, W / LeftShift to
        // fly and boost). Since P949 that IS the fresh-profile default, so the
        // pin below is normally a no-op; it stays as a guard for a profile that
        // saved the "Classic Allegiance" preset earlier.
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_TRAIN_PROOF") == "1"
            && Rendering.KeyBindingStore.Instance.Map.Layout != InputLayout.Port)
        {
            Rendering.KeyBindingStore.Instance.Map.SetLayout(InputLayout.Port);
            // Persist it: the proof opens/closes the Controls popup, and Cancel
            // (native Close -> reload) must not fall back to another preset.
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

        // P941/P949 — the virtual-joystick default follows the LAYOUT: native's
        // stock map flies with the mouse (native's startup toggles the virtual
        // joystick on, LoadPreference("VirtualJoystick", TRUE) ->
        // ToggleVirtualJoystick(), WinTrek.cpp:3311-3312; its training text says
        // "left click to fire", training/mission3.cpp:646), while the Port layout
        // — the fresh-profile default since P949 — keeps the pointer: left click
        // picks, mouse flight is the opt-in ToggleMouse key.
        _keyBindLayoutSeen = _keyBindController.Map.Layout;
        _mouseFlightEnabled = InputLayouts.MouseFlightByDefault(_keyBindLayoutSeen);
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
            _mouseFlightEnabled = InputLayouts.MouseFlightByDefault(layoutNow);
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

    // -- P949 one-time fresh-profile keys hint (ALLEGIANCE_P949_PROOF=1) ------

    private ControlHint? _controlHint;
    private KeyboardState _controlHintPrevKs;

    /// <summary>
    /// P949 — the one-time hint for a fresh profile, first time in flight:
    /// <c>Keys: W thrust | Up/Down/Left/Right steer | F6 controls (Classic
    /// Allegiance keys available)</c>, built from the LIVE labels. It is shown
    /// once per profile: dismissed by any key press or after 60 s (the pure
    /// <see cref="ControlHint"/> rules), and never again once
    /// <see cref="HudSettings.ControlHintSeen"/> is persisted or the player has a
    /// keymap (F6 OK saves one -> <see cref="KeyBindingStore.HasKeymap"/>).
    /// </summary>
    private void UpdateControlHint(KeyboardState ks, double dtSeconds)
    {
        var store = KeyBindingStore.Instance;
        if (_controlHint is null)
        {
            if (!ControlHint.ShouldShow(store.FreshProfile, _hudSettings.ControlHintSeen)) return;
            if (_state != ClientState.Flying || _dockedActive) return;
            // "First time in FLIGHT": the pre-launch mission briefing owns the
            // screen until it is dismissed/expires, and its panel covers the
            // hint band — start the 60 s only once the pilot can actually fly.
            if (IsBriefingVisible(out _)) return;
            string? text = ControlHint.TextFor(store.Map);
            if (text is null) return;
            _controlHint = new ControlHint(text);
            _controlHintPrevKs = ks;
            Console.WriteLine($"[keymap] fresh-profile keys hint shown ({InputLayouts.Name(store.Map.Layout)}): {text}");
            return;
        }

        if (!_controlHint.Active) return;
        bool anyKey = AnyNewKeyPressed(ks, _controlHintPrevKs);
        _controlHintPrevKs = ks;
        bool ended = _controlHint.Update(dtSeconds, anyKey);
        // A keymap written while the hint was up (F6 OK) suppresses it too.
        if (!ended && !store.FreshProfile) ended = _controlHint.Dismiss("saved");
        if (!ended) return;

        _hudSettings.ControlHintSeen = true;
        SaveHudSettingsQuietly();
        Console.WriteLine($"[keymap] fresh-profile keys hint dismissed ({_controlHint.DismissReason}) and marked seen");
    }

    /// <summary>True when any key went down since the previous frame ("any key"
    /// dismisses the P949 hint; modifiers count, exactly as native treats any
    /// input as "the player is flying").</summary>
    private static bool AnyNewKeyPressed(KeyboardState now, KeyboardState previous)
    {
        foreach (var key in now.GetPressedKeys())
            if (!previous.IsKeyDown(key)) return true;
        return false;
    }

    // -- P949 real-window proof (ALLEGIANCE_P949_PROOF=1) ---------------------

    private Keys[]? _p949ProofKeys;
    private bool _p949ProofStarted;
    private bool _p949ProofDone;
    private double _p949ProofElapsed;
    private double _p949ProofAirborneSeconds;
    private double _p949ProofWaitSeconds;
    private int _p949ProofStep;
    private string _p949ProofControlsBefore = "";

    /// <summary>
    /// P949 — scripted real-window proof of the fresh-profile default: (1) the
    /// keys hint appears over the fresh profile's Port layout in flight; (2) a
    /// real held W + LEFT/UP key press steers and thrusts (the production input
    /// path logs the resolved actions and the control vector it sends), which
    /// also dismisses the hint ("until any key"); (3) F6 shows the preset labels
    /// ("PORT LAYOUT", then "CLASSIC ALLEGIANCE (STOCK MAP: MOUSE/JOYSTICK
    /// STEERING)"). Every step queues a named real-backbuffer PNG via
    /// <c>--autoshot-dir</c>; the name carries the HUD style so the Classic and
    /// Modern pairs can be captured.
    /// </summary>
    private void RunP949Proof(double dtSec)
    {
        if (_p949ProofDone) return;
        _p949ProofWaitSeconds += dtSec;
        _p949ProofAirborneSeconds = _dockedActive ? 0 : _p949ProofAirborneSeconds + dtSec;
        if (!_p949ProofStarted)
        {
            if (_p949ProofAirborneSeconds < 1.0 && _p949ProofWaitSeconds < 45.0) return;
            // A player closes native's pre-launch briefing with Enter (P271
            // DismissBriefing is that path); the proof does the same so the hint
            // band is free and the camera is actually in flight when it fires.
            if (IsBriefingVisible(out var briefRemaining))
            {
                DismissBriefing();
                Console.WriteLine($"[p949-proof] dismissed the mission briefing ({briefRemaining?.ToString() ?? "?"}s left)");
                return;
            }
        }
        _p949ProofElapsed += dtSec;
        string style = _hudSettings.Style.ToString().ToLowerInvariant();
        var map = KeyBindingStore.Instance.Map;

        void Shot(string name)
        {
            _autoShotName = $"p949_{name}_{style}";
            _autoShotScheduled = true;
            Console.WriteLine($"[p949-proof] shot {_autoShotName}");
        }

        if (!_p949ProofStarted)
        {
            _p949ProofStarted = true;
            _p949ProofElapsed = 0;
            Shot("hint");
            Console.WriteLine($"[p949-proof] in flight: layout={InputLayouts.Name(map.Layout)}, "
                + $"hint='{_controlHint?.VisibleText ?? "(none)"}', "
                + $"ThrustForward={map.LabelOf("ThrustForward")}, "
                + $"steer={map.LabelOf("PitchUp")}/{map.LabelOf("PitchDown")}/"
                + $"{map.LabelOf("YawLeft")}/{map.LabelOf("YawRight")}, "
                + $"mouse flight {(_mouseFlightEnabled ? "ON" : "OFF")}");
            _p949ProofStep = 1;
            return;
        }

        switch (_p949ProofStep)
        {
            case 1:
                if (_p949ProofElapsed > 2.0)
                {
                    // Hold the Port layout's thrust + steer keys through the real
                    // input seam: W = ThrustForward, LEFT = YawLeft, UP = PitchUp.
                    _p949ProofControlsBefore = DescribeSentControls();
                    _p949ProofKeys = [XnaKeys.W, XnaKeys.Left, XnaKeys.Up];
                    Console.WriteLine("[p949-proof] holding W + LEFT + UP (keyboard thrust/steer)");
                    _p949ProofElapsed = 0;
                    _p949ProofStep = 2;
                }
                break;
            case 2:
                if (_p949ProofElapsed > 1.5)
                {
                    var ks = ReadProofKeyboard();
                    Console.WriteLine("[p949-proof] resolved actions: "
                        + $"ThrustForward={map.IsDown(ks, "ThrustForward")}, "
                        + $"YawLeft={map.IsDown(ks, "YawLeft")}, "
                        + $"PitchUp={map.IsDown(ks, "PitchUp")}; "
                        + $"controls before [{_p949ProofControlsBefore}] after [{DescribeSentControls()}]; "
                        + $"hint now '{_controlHint?.VisibleText ?? "(dismissed)"}'");
                    Shot("flight_keys");
                    _p949ProofElapsed = 0;
                    _p949ProofStep = 3;
                }
                break;
            case 3:
                if (_p949ProofElapsed > 1.5)
                {
                    _p949ProofKeys = null;   // release
                    ToggleKeyBindings();     // F6 Controls on the fresh preset
                    Shot("controls_" + (_keyBindController.Map.Layout == InputLayout.Native ? "classic" : "port"));
                    Console.WriteLine($"[p949-proof] F6 Controls: preset '{_keyBindController.LayoutName}'");
                    _p949ProofElapsed = 0;
                    _p949ProofStep = 4;
                }
                break;
            case 4:
                if (_p949ProofElapsed > 2.0)
                {
                    _keyBindController.CycleLayout();   // P -> the Classic Allegiance preset
                    Shot("controls_" + (_keyBindController.Map.Layout == InputLayout.Native ? "classic" : "port"));
                    Console.WriteLine($"[p949-proof] F6 Controls: preset '{_keyBindController.LayoutName}'");
                    _p949ProofElapsed = 0;
                    _p949ProofStep = 5;
                }
                break;
            case 5:
                if (_p949ProofElapsed > 2.0)
                {
                    _keyBindController.Cancel();
                    _keyBindVisible = false;
                    _p949ProofDone = true;
                    Console.WriteLine("[p949-proof] done (four real frames queued)");
                }
                break;
        }
    }

    /// <summary>The control vector the pilot is currently SENDING (yaw/pitch/
    /// throttle), formatted for the P949 proof log.</summary>
    private string DescribeSentControls() =>
        $"yaw={_lastSentControls.Yaw:0.00} pitch={_lastSentControls.Pitch:0.00} "
        + $"throttle={_lastSentControls.Throttle:0.00}";

    private bool _p941ProofStarted;
    private bool _p941ProofDone;
    private double _p941ProofElapsed;
    private double _p941AirborneSeconds;
    private double _p941WaitSeconds;
    private int _p941ProofStep;

    /// <summary>
    /// P941 (updated by P949) — scripted real-window proof of the layout presets
    /// and the F1 overview. Sequence: (1) open Controls (F6) on the fresh
    /// profile's default preset — the Port layout since P949; (2) P cycles to the
    /// other preset ("Classic Allegiance", native's stock map); (3) P cycles back,
    /// OK saves, close; (4) open the help overview on its CONTROLS page (the live
    /// key table); (5) the FLIGHT page (live prose). Each step queues a named
    /// real-backbuffer PNG (<c>--autoshot-dir</c>) whose name carries the layout
    /// and HUD style, so live-proof.sh can capture the Classic and Modern pairs.
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

        static string PresetSlug(InputLayout layout) =>
            layout == InputLayout.Native ? "classic" : "port";

        if (!_p941ProofStarted)
        {
            _p941ProofStarted = true;
            _p941ProofElapsed = 0;
            ToggleKeyBindings();
            Shot("controls_" + PresetSlug(_keyBindController.Map.Layout));
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
                    Shot("controls_" + PresetSlug(_keyBindController.Map.Layout));
                    Console.WriteLine($"[p941-proof] preset now {_keyBindController.LayoutName}, "
                        + $"RollLeft={_keyBindController.Map.LabelOf("RollLeft")}");
                    _p941ProofElapsed = 0;
                    _p941ProofStep = 2;
                }
                break;
            case 2:
                if (_p941ProofElapsed > 2.0)
                {
                    _keyBindController.CycleLayout();   // back to the fresh preset
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
