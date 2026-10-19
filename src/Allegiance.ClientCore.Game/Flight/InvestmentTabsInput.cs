using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>Selectable native purchase groups (loadout.cpp:777-785,1042-1056).
/// Observe suppressed edges so held keys cannot change tabs when chat closes.</summary>
public sealed class InvestmentTabsInput
{
    private readonly ActionPressEdges _edges = new();
    public int Step(KeyboardState keys, ActionMap map, bool allowed, bool previousShoulder = false, bool nextShoulder = false)
    {
        bool previous = _edges.Observe("previous", map.IsDownMostSpecific(keys, "InvestPreviousTab") || previousShoulder, !allowed);
        bool next = _edges.Observe("next", map.IsDownMostSpecific(keys, "InvestNextTab") || nextShoulder, !allowed);
        return previous ? -1 : next ? 1 : 0;
    }
}
