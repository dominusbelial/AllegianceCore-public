namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P624 — the help-overlay header line, generated from the LIVE bindings of the
/// <c>Help</c> and <c>OpenKeyBindings</c> actions (previously a hard-coded
/// "KEYBINDINGS (H TOGGLE - F6 CONTROLS / REBIND)" in HelpOverlayRenderer.cs:126
/// that went stale when P623 moved Help to F1 and kept "H"). Because it is
/// derived from the action map each draw, a rebind updates the header at once.
/// Pure so the derivation is unit-tested without a graphics device.
/// </summary>
public static class HelpHeader
{
    /// <summary>Build the centred header line from the two actions' current key
    /// labels (already ASCII — see KeyBinding.Label), uppercased to match the
    /// panel's all-caps header style.</summary>
    public static string Line(string helpKeyLabel, string openKeyBindingsKeyLabel)
    {
        string h = helpKeyLabel.ToUpperInvariant();
        string r = openKeyBindingsKeyLabel.ToUpperInvariant();
        return $"KEYBINDINGS  ({h} TOGGLE  -  {r} CONTROLS / REBIND)";
    }
}
