using Allegiance.Core.Collision;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P266 — the client's snapshot of the local ship's predictive LEAD AIM
/// indicator, delivered by the server on the owner-only
/// <c>LEAD_INDICATOR_UPDATE(210)</c> message (see
/// <c>Allegiance.Core.Wire.LeadIndicatorUpdate</c>). Plain data only, so it is
/// trivially constructible in tests; <see cref="ClientWorld"/> owns the single
/// current instance and the MonoGame client reads it each frame to render the
/// lead reticle at the projected lead world point. The lead is solved
/// server-side from the real <c>solveForLead</c> physics and gated by the
/// <c>c_habmLeadIndicator</c> hull capability / friendly-relay scanner, so a
/// peer simply receives <see cref="HasLead"/>=false (no reticle) when no lead
/// is granted — see the wire codec's doc for the full A-vs-B rationale.
/// </summary>
/// <param name="HasLead">True when a lead indicator is currently granted (the
/// local ship has a ship command target in sector, a mounted weapon, and the
/// hull/relay gate passed).</param>
/// <param name="InRange">True when the solved intercept time is within the
/// mounted weapon's lifespan (native's <c>bIisInRange</c>, indicator.cpp:224);
/// out-of-range renders the reticle grey/dimmed.</param>
/// <param name="Green">The green→red hit-probability 0..1 (native's <c>g</c> ramp,
/// indicator.cpp:341-357, as an angular equivalent). Render colour
/// <c>(1-g, g, 0)</c>.</param>
/// <param name="LeadWorldPos">The predicted lead WORLD point: along the solved aim
/// direction at the target's range — where the reticle is drawn, genuinely offset
/// from the target's own centre by the lateral lead.</param>
/// <param name="TargetPos">The target's CURRENT world position (server-authoritative;
/// cross-side ships are not broadcast to this peer). Used to project the target
/// marker and quantify the on-screen lead offset.</param>
public readonly record struct LeadIndicatorState(
    bool HasLead,
    bool InRange,
    float Green,
    Vector3 LeadWorldPos,
    Vector3 TargetPos)
{
    /// <summary>The canonical "no lead indicator" snapshot.</summary>
    public static readonly LeadIndicatorState None = new(false, false, 0f, Vector3.Zero, Vector3.Zero);
}
