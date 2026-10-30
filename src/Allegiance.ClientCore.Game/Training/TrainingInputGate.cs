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

    /// <summary>True when the action's NATIVE-default key (the key the native training prompts name,
    /// decoded from <c>defaultinputmap.mdl</c>) is physically down. Public so the client's own
    /// target-selection path (<c>SelectRemainderTarget</c>) can mirror the mission-routing recognition
    /// below — the P892b B-key fix: in the port's shipped default map <c>TargetEnemy</c> /
    /// <c>TargetFriendlyBase</c> are left <c>U()</c> (unbound; F/B are rebound to ToggleCloak /
    /// Scoreboard), so without honouring the native-default key a real-window B/F tap never reaches
    /// the client's <c>SelectRemainderTarget → CommitTargetForPlayer</c>, leaving M3 goal 12's
    /// <c>GetTargetCondition(OT_station,1010)</c> unsatisfied (WinTrek.cpp:8753-8769, 10149, 9024).</summary>
    public static bool NativeDefaultKeyDown(KeyboardState keys, string action)
        => NativeDefaultKeys.TryGetValue(action, out var k) && keys.IsKeyDown(k);

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
