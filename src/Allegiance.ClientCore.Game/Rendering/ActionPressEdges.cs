namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>Per-action press state; observe even while UI suppresses a command
/// so keys held through composition or flight entry cannot fire later.</summary>
public sealed class ActionPressEdges
{
    private readonly HashSet<string> _held = new();
    public bool Observe(string action, bool down, bool suppressed = false)
    {
        bool fresh = down && _held.Add(action);
        if (!down) _held.Remove(action);
        return fresh && !suppressed;
    }
    public void Prime(IEnumerable<(string Action, bool Down)> actions)
    {
        _held.Clear();
        foreach (var (action, down) in actions) if (down) _held.Add(action);
    }
}
