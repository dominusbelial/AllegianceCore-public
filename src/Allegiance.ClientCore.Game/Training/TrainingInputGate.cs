using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Translate rebindable game actions before any ordinary handler sees
/// them (WinTrek/WinTrek.cpp:8753-8769). Held actions retain the key-down verdict.</summary>
public sealed class TrainingInputGate(Func<int, bool> recordKey)
{
    private readonly HashSet<string> _held = [];
    private readonly HashSet<string> _rejected = [];
    private static readonly Dictionary<string, int> NativeKeys = InputActionCatalog.Definitions
        .Select(d => (d.Name, Match: System.Text.RegularExpressions.Regex.Match(d.Description, @"TK_\w+")))
        .Where(d => d.Match.Success && DefaultInputMap.TkNumber(d.Match.Value).HasValue)
        .ToDictionary(d => d.Name, d => DefaultInputMap.TkNumber(d.Match.Value)!.Value);

    public bool Approves(string action) => !_rejected.Contains(action);

    public void Update(KeyboardState keys, ActionMap map)
    {
        var filter = map.ActionFilter;
        map.ActionFilter = null;
        try
        {
            foreach (var (action, nativeKey) in NativeKeys)
            {
                if (map.IsDownMostSpecific(keys, action))
                {
                    if (_held.Add(action) && !recordKey(nativeKey)) _rejected.Add(action);
                }
                else { _held.Remove(action); _rejected.Remove(action); }
            }
        }
        finally { map.ActionFilter = filter; }
    }
}
