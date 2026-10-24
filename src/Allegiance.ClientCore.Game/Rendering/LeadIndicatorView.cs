using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P804 (was P266) — the per-frame lead-indicator descriptor that <c>Game1</c>
/// computes and passes to <see cref="HudRenderer.Draw"/>: the projected on-screen
/// centres of the predicted lead point and of the target itself (viewport px),
/// plus the client-computed range/hit-probability/capability flags and a
/// monotonic wall-clock for the subtle pulse animation. Plain data only.
/// <c>null</c> on the renderer side means "no lead in view — draw no reticle".
/// </summary>
/// <param name="LeadCenter">The projected on-screen centre of the predicted lead
/// world point (viewport px) — where the lead reticle is drawn, genuinely offset
/// from <see cref="TargetCenter"/> by the lateral lead.</param>
/// <param name="TargetCenter">The projected on-screen centre of the target's
/// current world position (viewport px) — a faint marker that lets the pilot (and
/// the visual proof) see how far the lead is from where the target is now.</param>
/// <param name="InRange">True when the solved intercept time is within the
/// weapon's lifespan (indicator.cpp:224-228); out-of-range renders the reticle
/// grey/dimmed (native's leadout surface swap).</param>
/// <param name="Green">The green→red hit-probability 0..1: cockpit = the native
/// screen-space <c>g</c> ramp (indicator.cpp:334-357), chase/missile = the
/// native forward-projected <c>bWillHit</c> ramp (:271). Render colour
/// <c>(1-g, g, 0)</c>.</param>
/// <param name="NowSeconds">Monotonic seconds feeding the pulse (deterministic
/// across frames of an offscreen capture).</param>
/// <param name="Granted">P804 — native's lead-reticle capability gate
/// (indicator.cpp:286-320: own hull <c>c_habmLeadIndicator</c>, or a friendly
/// relay with the target in scanner range), computed on the client. The cockpit
/// lead SURFACE draws only when granted; the centre aim crosshair does not need
/// it.</param>
/// <param name="Visible">P804 — the projected lead/aim direction landed inside
/// the viewport (native draws an off-rect point but it is clipped away, so this
/// is its visible equivalent). Gates both markers.</param>
/// <param name="TargetVisible">P804 — the projected target-now centre lies
/// inside the view; the port-extension target ring only draws when it does.</param>
public readonly record struct LeadIndicatorView(
    Vector2 LeadCenter,
    Vector2 TargetCenter,
    bool InRange,
    float Green,
    double NowSeconds,
    bool Granted = false,
    bool Visible = true,
    bool TargetVisible = true);
