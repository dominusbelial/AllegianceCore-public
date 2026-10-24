using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// HUD-18 — one frame's inputs for the client-side "is the selected target in
/// the selected weapon's range" decision. Gathered by <c>Game1.ComputeTargetPane</c>
/// from the same data the own-shot echo uses (own sim pose + server velocity,
/// the selected mount's catalogue weapon, the target's resolved pose/radius),
/// so <see cref="TargetWeaponRange.IsInRange"/> stays pure and allocation-free.
/// </summary>
public readonly record struct TargetWeaponRangeInput(
    // Target cluster equals the shooter's cluster (native
    // targetModel->GetCluster() == pcluster, indicator.cpp:188) plus the port's
    // visibility stand-in for pshipSource->CanSee(targetModel) (:190-192).
    bool SameCluster,
    bool CanSee,
    // Native w — the shooter has a weapon in the solved mount
    // (indicator.cpp:194-212,214).
    bool HasWeapon,
    // Native cmExternalChase || cmExternalMissile (indicator.cpp:214-217).
    bool ChaseCamera,
    // Native (targetModel->GetSide() == pside) ||
    // AlliedSides(targetModel->GetSide(), pside); a side-less model (asteroid,
    // aleph) is not friendly (indicator.cpp:217).
    bool FriendlyOrAllied,
    // w->GetProjectileType()->GetPower(); negative means a healing weapon
    // (indicator.cpp:217).
    float ProjectilePower,
    bool AbsoluteF,
    // w->GetAmmoPerShot() != 0 — selects the c_gaSpeedAmmo scaling
    // (Igc/common.cpp:141).
    bool AmmoPerShot,
    // typeData->energyPerShot != 0 — selects the c_gaLifespanEnergy scaling
    // (Igc/weaponIGC.h:165-171).
    bool EnergyWeapon,
    float ProjectileSpeed,
    float ProjectileLifespan,
    float SpeedAmmoGa,
    float LifespanEnergyGa,
    Vector3 MyPosition,
    Orientation MyOrientation,
    Vector3 MyVelocity,
    // The weapon's local mount position (w->GetPosition()), applied as
    // Position + MountPosition * Orientation (Igc/common.cpp:137).
    Vector3 MountPosition,
    float TargetRadius,
    Vector3 TargetPosition,
    Vector3 TargetVelocity);

/// <summary>
/// HUD-18 — the native in-range flag of the target pane's turret-range icon,
/// ported from <c>WinTrek/indicator.cpp:183-228</c> (the part of
/// <c>RenderLeadIndicator</c> that runs <b>before</b> the lead-reticle
/// capability gate at <c>:286-320</c>) plus <c>solveForLead</c>
/// (<c>Igc/common.cpp:121-149</c>) and <c>GetLifespan</c>
/// (<c>Igc/weaponIGC.h:165-171</c>).
///
/// <para>Native computes this on the client for the player's own
/// <c>c_cmdCurrent</c> target of <b>any</b> model type, with the
/// <b>selected</b> weapon (<c>trekClient.GetWeapon()</c>,
/// <c>clintlib/clintlib.cpp:1585-1588</c>), and calls
/// <c>SetInRange(bIisInRange)</c> unconditionally at <c>:407</c>. The port
/// therefore does not consult the server lead message or any
/// <c>c_habmLeadIndicator</c>/relay capability here — only the reticle itself
/// stays server-driven.</para>
///
/// <para>Side-less targets count as not friendly, so a damaging weapon is in
/// range on an asteroid/aleph and a healing weapon never is. Negative
/// <c>Power</c> = healing (<c>Igc/weaponIGC.h</c> / projectile power sign).</para>
/// </summary>
public static class TargetWeaponRange
{
    /// <summary>
    /// The in-range decision. Mirrors, in native order:
    /// <list type="number">
    /// <item><c>if (targetModel &amp;&amp; cluster == pcluster &amp;&amp; CanSee)</c>
    /// (indicator.cpp:188-192) — here both cluster and visibility arrive as
    /// <see cref="TargetWeaponRangeInput.SameCluster"/>/<see cref="TargetWeaponRangeInput.CanSee"/>;</item>
    /// <item><c>if (w &amp;&amp; (chase || missile || friendly == (power &lt; 0)))</c>
    /// (indicator.cpp:207,214-218);</item>
    /// <item><c>speed = pt-&gt;GetSpeed() * (ammoPerShot ? GA(c_gaSpeedAmmo) : 1)</c>
    /// (common.cpp:141), <c>lifespan = pt-&gt;GetLifespan() * (energyPerShot != 0 ?
    /// GA(c_gaLifespanEnergy) : 1)</c> (weaponIGC.h:165-171);</item>
    /// <item><c>t = solveForLead(...)</c>, in-range when <c>t &lt;= lifespan</c>
    /// (indicator.cpp:221-228).</item>
    /// </list>
    /// The native no-solution sentinel (<c>FLT_MAX / 500000000</c>,
    /// common.cpp:86) is far above any real lifespan, so it reads as out of
    /// range exactly as in native.
    /// <para>P804 — the gate and the solve are the SAME code the lead marker and
    /// aim predictor use (<see cref="ClientLead.Solve"/>), so the pane's flag and
    /// the reticle cannot disagree.</para>
    /// </summary>
    public static bool IsInRange(in TargetWeaponRangeInput input)
        => ClientLead.Solve(input).InRange;
}
