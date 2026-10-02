using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P266 — the per-frame lead-indicator descriptor that <c>Game1</c> computes and
/// passes to <see cref="HudRenderer.Draw"/>: the projected on-screen centres of
/// the predicted lead point and of the target itself (viewport px), plus the
/// server-computed range/hit-probability flags and a monotonic wall-clock for the
/// subtle pulse animation. Plain data only. <c>null</c> on the renderer side
/// means "no lead in view — draw no reticle".
/// </summary>
/// <param name="LeadCenter">The projected on-screen centre of the predicted lead
/// world point (viewport px) — where the lead reticle is drawn, genuinely offset
/// from <see cref="TargetCenter"/> by the lateral lead.</param>
/// <param name="TargetCenter">The projected on-screen centre of the target's
/// current world position (viewport px) — a faint marker that lets the pilot (and
/// the visual proof) see how far the lead is from where the target is now.</param>
/// <param name="InRange">True when the intercept is within the weapon's lifespan;
/// out-of-range renders the reticle grey/dimmed (native's surface swap).</param>
/// <param name="Green">The green→red hit-probability 0..1 (native's <c>g</c> ramp);
/// render colour <c>(1-g, g, 0)</c>.</param>
/// <param name="NowSeconds">Monotonic seconds feeding the pulse (deterministic
/// across frames of an offscreen capture).</param>
public readonly record struct LeadIndicatorView(
    Vector2 LeadCenter,
    Vector2 TargetCenter,
    bool InRange,
    float Green,
    double NowSeconds);
