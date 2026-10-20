using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Current phase of the P625 rebind popup (mirrors native's
/// InputMapPopup interaction: browse the command list, pick a command, press the
/// new key, resolve a conflict by swapping).</summary>
public enum KeyBindPhase
{
    /// <summary>List visible; arrow keys move the cursor, Enter picks a command.</summary>
    Browse,
    /// <summary>Selected command is highlighted; the next key press becomes its binding.</summary>
    Capture,
    /// <summary>A conflict was found; the player chooses to swap (Y) or cancel (N).</summary>
    Conflict,
    /// <summary>Confirm before resetting every binding to default (Y/N).</summary>
    ConfirmReset,
    /// <summary>Popup closed.</summary>
    Closed,
}

/// <summary>
/// P625 - pure state machine for the in-game rebind popup (ported from native's
/// InputMapPopup, WinTrek/trekinput.cpp:1631+, with its conflict-detection and
/// swap offer). It owns no drawing; <see cref="KeyBindingRenderer"/> renders it
/// and Game1 feeds it raw key presses. Fully headless-testable.
/// </summary>
public sealed class KeyBindingController
{
    private readonly ActionMap _map;
    private readonly Action? _onCommit;
    private readonly Action? _onDiscard;
    private readonly string[] _actions;

    public KeyBindPhase Phase { get; private set; } = KeyBindPhase.Browse;
    public int SelectedIndex { get; private set; }
    public string Message { get; private set; } = "";
    /// <summary>During Capture, the action awaiting a new key.</summary>
    public string? CapturingAction { get; private set; }
    /// <summary>During Conflict, the action that already owns the key.</summary>
    public string? ConflictOwner { get; private set; }
    public KeyBinding? PendingChord { get; private set; }

    /// <summary>Flat action list in catalogue (display) order; every element is
    /// a distinct rebindable action the popup navigates.</summary>
    public IReadOnlyList<string> Actions => _actions;

    /// <summary>Currently selected action name.</summary>
    public string CurrentAction => _actions[SelectedIndex];

    /// <summary>P757 — native OK-versus-Cancel persistence (trekinput.cpp:2436-2458,
    /// 3005-3030): mutations inside the popup change the in-memory map but are
    /// <b>not</b> persisted; <see cref="Commit"/> (native <c>OnButtonOK</c> →
    /// <c>SaveMap</c>) persists, and <see cref="Close"/> (native <c>Close</c> →
    /// reload <c>INPUTMAP_FILE</c>, else the default map) discards them.
    /// <paramref name="onCommit"/> saves the map; <paramref name="onDiscard"/>
    /// reloads it. Tests may leave both null (an isolated map with no store).</summary>
    public KeyBindingController(ActionMap map, Action? onCommit = null, Action? onDiscard = null)
    {
        _map = map;
        _onCommit = onCommit;
        _onDiscard = onDiscard;
        _actions = map.Definitions
            .OrderBy(d => IndexOfCategory(d.Category))
            .ThenBy(d => map.BindingOf(d.Name).Label)
            .Select(d => d.Name)
            .ToArray();
    }

    private static int IndexOfCategory(string cat)
    {
        int i = Array.IndexOf(InputActionCatalog.CategoryOrder, cat);
        return i >= 0 ? i : int.MaxValue;
    }

    public ActionMap Map => _map;

    /// <summary>The definition of a given action (for labels/descriptions).</summary>
    public InputActionDef DefinitionOf(string action)
        => _map.Definitions.First(d => d.Name == action);

    // -- navigation ----------------------------------------------------------

    public void MoveUp() { if (SelectedIndex > 0) SelectedIndex--; Message = ""; }
    public void MoveDown() { if (SelectedIndex < _actions.Length - 1) SelectedIndex++; Message = ""; }

    // -- browse actions ------------------------------------------------------

    /// <summary>Pick the highlighted command and enter Capture mode (native: you
    /// select a command, then press the new key).</summary>
    public void SelectCurrent()
    {
        if (Phase != KeyBindPhase.Browse) return;
        CapturingAction = CurrentAction;
        PendingChord = null;
        ConflictOwner = null;
        Message = $"Press a new key for {CurrentAction} (Esc to cancel)...";
        Phase = KeyBindPhase.Capture;
    }

    /// <summary>P757 — native <c>OnButtonOK</c> (trekinput.cpp:2446-2450):
    /// <c>SaveMap(INPUTMAP_FILE)</c> then close. Persists the popup's pending
    /// mutations; nothing else ever writes the file.</summary>
    public void Commit()
    {
        _onCommit?.Invoke();
        Message = "";
        Phase = KeyBindPhase.Closed;
    }

    /// <summary>Cancel the whole popup — native <c>Close()</c>
    /// (trekinput.cpp:2436-2444): reload the persisted map (the default map when
    /// the file is unreadable), discarding every unsaved mutation. Esc from
    /// Browse routes here (native <c>OnKey</c> VK_ESCAPE → <c>OnButtonCancel</c>,
    /// trekinput.cpp:3013-3015).</summary>
    public void Close()
    {
        _onDiscard?.Invoke();
        Phase = KeyBindPhase.Closed;
        Message = "";
    }

    /// <summary>Begin the reset-to-defaults confirmation (R from Browse).</summary>
    public void RequestReset()
    {
        if (Phase != KeyBindPhase.Browse) return;
        Message = "Reset ALL key bindings to defaults? (Y = yes, N = no)";
        Phase = KeyBindPhase.ConfirmReset;
    }

    // -- capture -------------------------------------------------------------

    /// <summary>A key was pressed during Capture: build its chord (honouring any
    /// currently-held Ctrl/Alt/Shift) and try to bind it, entering Conflict mode
    /// if another action owns the key (native's swap offer).</summary>
    public void CaptureKey(Keys key, bool ctrl, bool alt, bool shift)
    {
        if (Phase != KeyBindPhase.Capture || CapturingAction == null) return;

        // Modifier keys alone don't rebind anything.
        if (key is Keys.LeftShift or Keys.RightShift or Keys.LeftControl
            or Keys.RightControl or Keys.LeftAlt or Keys.RightAlt)
            return;

        var mods = InputModifiers.None;
        if (ctrl) mods |= InputModifiers.Control;
        if (alt) mods |= InputModifiers.Alt;
        if (shift) mods |= InputModifiers.Shift;
        var chord = new KeyBinding(key, mods);
        PendingChord = chord;

        string? conflict = _map.SetBinding(CapturingAction, chord);
        if (conflict != null)
        {
            ConflictOwner = conflict;
            Message = $"'{chord.Label}' is used by {conflict}. Swap? (Y = swap, N = cancel)";
            Phase = KeyBindPhase.Conflict;
        }
        else
        {
            // P757 — pending until OK: native SaveMap only fires on OnButtonOK
            // (trekinput.cpp:2446-2450); the live map carries the change but the
            // file is untouched until Commit (or rolled back by Close).
            Message = $"{CapturingAction} -> {chord.Label} (Space = OK, Esc = cancel)";
            CapturingAction = null;
            PendingChord = null;
            Phase = KeyBindPhase.Browse;
        }
    }

    /// <summary>Answer a prompt (Conflict or ConfirmReset) with Y/N.</summary>
    public void Answer(bool yes)
    {
        if (Phase == KeyBindPhase.Conflict && CapturingAction != null && ConflictOwner != null && PendingChord is { } chord)
        {
            if (yes)
            {
                _map.SwapBindings(CapturingAction, ConflictOwner);
                Message = $"Swapped {CapturingAction} <-> {ConflictOwner} (Space = OK, Esc = cancel)";
            }
            else
            {
                _map.SetBinding(CapturingAction, _map.BindingOf(ConflictOwner)); // revert to prior binding
                Message = "Cancelled - binding unchanged.";
            }
            CapturingAction = null;
            ConflictOwner = null;
            PendingChord = null;
            Phase = KeyBindPhase.Browse;
        }
        else if (Phase == KeyBindPhase.ConfirmReset)
        {
            if (yes)
            {
                _map.ResetToDefaults();
                Message = "All key bindings reset to defaults (Space = OK, Esc = cancel).";
            }
            else
            {
                Message = "Reset cancelled.";
            }
            Phase = KeyBindPhase.Browse;
        }
    }

    /// <summary>Esc acts contextually: from Capture/Conflict back to Browse;
    /// from Browse it closes the popup.</summary>
    public void Cancel()
    {
        switch (Phase)
        {
            case KeyBindPhase.Capture:
                Message = "Cancelled - binding unchanged.";
                CapturingAction = null; PendingChord = null; ConflictOwner = null;
                Phase = KeyBindPhase.Browse;
                break;
            case KeyBindPhase.Conflict:
                // Treat Esc like "N".
                Answer(false);
                break;
            case KeyBindPhase.ConfirmReset:
                Message = "Reset cancelled.";
                Phase = KeyBindPhase.Browse;
                break;
            case KeyBindPhase.Browse:
                Close();
                break;
        }
    }
}
