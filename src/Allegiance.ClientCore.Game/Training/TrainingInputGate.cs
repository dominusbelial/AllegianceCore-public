using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Translate rebindable game actions before any ordinary handler sees
/// them (WinTrek/WinTrek.cpp:8753-8769). Held actions retain the key-down verdict.
///
/// P886 — a training prompt follows the NATIVE keymap (e.g. "Press the SPACEBAR"),
/// so the filter routes a press when EITHER the action's current binding OR its
/// native-default binding is down. The port rebinds some actions for normal flight
/// (SPACEBAR→FireWeapon, afterburner→LeftShift), so SPACEBAR alone no longer matched
/// TK_FireBooster and the M2/M3 "Press the SPACEBAR" gates could not advance in a real
/// window. Recognizing the native default restores the native key-press meaning for
/// training without touching the normal-flight bindings or the lesson scripts.</summary>
public sealed class TrainingInputGate(Func<int, bool> recordKey)
{
    private readonly HashSet<string> _held = [];
    private readonly HashSet<string> _rejected = [];
    private static readonly Dictionary<string, int> NativeKeys = InputActionCatalog.Definitions
        .Select(d => (d.Name, Match: System.Text.RegularExpressions.Regex.Match(d.Description, @"TK_\w+")))
        .Where(d => d.Match.Success && DefaultInputMap.TkNumber(d.Match.Value).HasValue)
        .ToDictionary(d => d.Name, d => DefaultInputMap.TkNumber(d.Match.Value)!.Value);

    /// <summary>The action's native-default physical key (the catalogue's 5th arg), where present —
    /// the key the training prompts describe even when normal flight rebinds the action.</summary>
    private static readonly Dictionary<string, Keys> NativeDefaultKeys = InputActionCatalog.Definitions
        .Select(d => (d.Name, Nd: d.NativeDefault))
        .Where(x => x.Nd is { } nd && nd.Key != Keys.None)
        .ToDictionary(x => x.Name, x => x.Nd!.Value.Key);

    public bool Approves(string action) => !_rejected.Contains(action);

    /// <summary>Whether the action's key is down, honouring the current binding OR the action's
    /// native-default key (training follows native prompts).</summary>
    private static bool IsPressed(ActionMap map, KeyboardState keys, string action)
    {
        if (map.IsDownMostSpecific(keys, action)) return true;
        return NativeDefaultKeys.TryGetValue(action, out var ndk) && keys.IsKeyDown(ndk);
    }

    public void Update(KeyboardState keys, ActionMap map)
    {
        var filter = map.ActionFilter;
        map.ActionFilter = null;
        try
        {
            foreach (var (action, nativeKey) in NativeKeys)
            {
                if (IsPressed(map, keys, action))
                {
                    if (_held.Add(action) && !recordKey(nativeKey)) _rejected.Add(action);
                }
                else { _held.Remove(action); _rejected.Remove(action); }
            }
        }
        finally { map.ActionFilter = filter; }
    }
}
