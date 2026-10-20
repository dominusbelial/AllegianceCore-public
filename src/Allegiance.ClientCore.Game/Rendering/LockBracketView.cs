using Allegiance.ClientCore.State;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P230 — the per-frame lock-target-box descriptor that <c>Game1</c> computes
/// and passes to <see cref="HudRenderer.Draw"/>: the target's projected on-screen
/// centre (in viewport px) plus the current lock state and a monotonic wall-clock
/// seconds for the pulse/spin animation. Plain data only. <c>null</c> on the
/// renderer side means "no lock target in view — draw no box".
/// </summary>
/// <param name="Center">The lock target's projected on-screen centre, viewport px.</param>
/// <param name="State">The current lock state (None/Partial/Full).</param>
/// <param name="NowSeconds">Monotonic seconds feeding the pulse/spin (deterministic
/// across frames of an offscreen capture).</param>
/// <param name="Fraction">HUD-4 — the continuous 0..1 magazine lock value
/// (native <c>magazine-&gt;GetLock()</c>, radarimage.cpp:325-332): partial radius
/// grows by 40×(1−lock) and full uses the locked surface.</param>
/// <param name="ScreenRadius">HUD-4 — the projected target radius in px
/// (native radiusBracket, radarimage.cpp:296-311); the lock sprite's base
/// half-span. Null when the radius cannot be resolved (the renderer keeps the
/// established fixed base size).</param>
public readonly record struct LockBracketView(Vector2 Center, MissileLockFeedback.LockState State, double NowSeconds, float Fraction = 1, float? ScreenRadius = null);
