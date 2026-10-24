using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P804 — the CLIENT-side ballistic lead solve, the shared Core
/// <see cref="LeadSolver"/> fed with the client's own IGC state. Native solves
/// the lead on the client in <c>RenderLeadIndicator</c>
/// (<c>WinTrek/indicator.cpp:183-360</c>): it reads the current command target,
/// the selected weapon, the local ship's pose/velocity and the target's
/// pose/velocity/radius, then calls <c>solveForLead</c>
/// (<c>Igc/common.cpp:121-149</c>). The port's MonoGame client now holds all of
/// those (the P786 wire work gave it the target pose/velocity and the hull
/// catalogue), so the server's P266 lead stream is gone and this is the only
/// lead path.
///
/// <para><b>One implementation.</b> The arithmetic is
/// <see cref="LeadSolver"/> — the same code the server's wingman AI runs through
/// <see cref="Ship.SolveForLead"/> — so the HUD's marker and the AI's aim can
/// never drift (native shares <c>solveForLead</c> the same way,
/// <c>Igc/shipIGC.cpp:2464,2617</c>). This type adds only the HUD-relevant
/// outputs native reads from the same solve:</para>
/// <list type="bullet">
/// <item>the cockpit lead surface's in-range flag, <c>t &lt;= w-&gt;GetLifespan()</c>
/// (<c>indicator.cpp:224-228</c>);</item>
/// <item>the chase/missile forward-projected aim point,
/// <c>trackingPosition = shipPosition + shipAimDirection * fDistanceBetweenShips</c>
/// (<c>indicator.cpp:246-265</c>);</item>
/// <item>the chase/missile hit test
/// <c>fA = t * pt-&gt;GetSpeed()</c>, <c>fH = fA / (vecLeadPosition ·
/// shipAimDirection)</c>, <c>bWillHit = fH² − fA² &lt;= fTargetRadius²</c> and
/// <c>green = bWillHit ? 1 − (fH² − fA²)/r² : 0</c>
/// (<c>indicator.cpp:249-276</c>).</item>
/// </list>
///
/// <para><b>Weapon inputs.</b> The caller supplies native's <c>w</c> — the
/// weapon on the SELECTED fixed mount (<c>trekClient.GetWeapon()</c> =
/// <c>GetMountedPart(ET_Weapon, m_selectedWeapon)</c>,
/// <c>clintlib/clintlib.cpp:1585-1588</c>), plus the side global-attribute
/// factors native folds in: <c>c_gaSpeedAmmo</c> on the solve speed
/// (<c>common.cpp:141</c>) and <c>c_gaLifespanEnergy</c> on the lifespan
/// (<c>Igc/weaponIGC.h:165-171</c>).</para>
///
/// <para><b>Pure and allocation-free.</b> Structs and value math only, so the
/// per-frame HUD path allocates nothing (the HUD discipline) and the rules are
/// headless-testable.</para>
/// </summary>
public static class ClientLead
{
    /// <summary>The resolved lead plus the native HUD quantities derived from
    /// it. <see cref="GatePassed"/> is false when native's outer gate fails
    /// (no target / wrong cluster / not seen / no weapon / healing-vs-side
    /// mismatch), in which case every other member is the default and native
    /// draws no aim indicator at all in the cockpit (and the grey no-target
    /// range marker in chase/missile, <c>indicator.cpp:367-405</c>).
    /// <see cref="Solved"/> distinguishes a real intercept from
    /// <c>solveForImpact</c>'s no-solution sentinel — native still takes the
    /// indicator branch (and draws the grey out-of-range aim point in
    /// chase/missile) but the cockpit has no lead direction to project.</summary>
    public readonly record struct Result(
        bool GatePassed,
        bool Solved,
        float Time,
        Vector3 Direction,
        bool InRange,
        float Lifespan,
        float SolveSpeed,
        float RawSpeed,
        float DistanceToTarget,
        Vector3 Forward,
        Vector3 TrackingPoint,
        bool WillHit,
        float Green);

    /// <summary>
    /// Run native's <c>RenderLeadIndicator</c> gate + solve from
    /// <paramref name="input"/> (the same input the target pane's in-range flag
    /// uses, so the two can never disagree).
    /// </summary>
    public static Result Solve(in TargetWeaponRangeInput input)
    {
        // indicator.cpp:188-192 — target present, same cluster, CanSee.
        if (!input.SameCluster || !input.CanSee || !input.HasWeapon)
            return default;

        // indicator.cpp:214-218 — only show the indicator for a healing weapon
        // on a friend or a damaging weapon on a non-friend; the chase/missile
        // cameras skip the test entirely.
        if (!input.ChaseCamera && input.FriendlyOrAllied != (input.ProjectilePower < 0f))
            return default;

        // common.cpp:141 — c_gaSpeedAmmo scales an ammo weapon's round speed.
        float solveSpeed = input.ProjectileSpeed * (input.AmmoPerShot ? input.SpeedAmmoGa : 1f);
        // weaponIGC.h:165-171 — GetLifespan() folds c_gaLifespanEnergy.
        float lifespan = input.ProjectileLifespan;
        if (input.EnergyWeapon)
            lifespan *= input.LifespanEnergyGa;

        // common.cpp:121-149 — the shared Core solve (muzzle offset rotated by
        // the shooter's orientation, exactly `weapon->GetPosition() *
        // myOrientation`).
        var lead = LeadSolver.Solve(new LeadSolver.Input(
            ShooterPosition: input.MyPosition,
            ShooterVelocity: input.MyVelocity,
            TargetPosition: input.TargetPosition,
            TargetVelocity: input.TargetVelocity,
            MuzzleOffsetWorld: input.MountPosition * input.MyOrientation,
            ProjectileSpeed: solveSpeed,
            Absolute: input.AbsoluteF,
            TargetRadius: input.TargetRadius));

        bool inRange = lead.Time <= lifespan;                      // indicator.cpp:224-228

        Vector3 forward = input.MyOrientation.GetForward();
        float distance = (input.TargetPosition - input.MyPosition).Length();   // indicator.cpp:259
        Vector3 tracking = input.MyPosition + forward * distance;              // :263-264

        // :256-276 — the forward-projected hit test. fA uses the RAW projectile
        // speed (pt->GetSpeed(); no c_gaSpeedAmmo here), the cosine comes from
        // the solved lead direction against the ship's nose.
        float fA = lead.Time * input.ProjectileSpeed;
        float fHA;
        if (lead.Solved)
        {
            Vector3 d = lead.Direction;
            float len = MathF.Sqrt(d.LengthSquared());
            if (len > 1e-9f) d /= len;
            float cos = d.X * forward.X + d.Y * forward.Y + d.Z * forward.Z;
            float fH = fA / cos;                       // native float division, inf when cos==0
            fHA = fH * fH - fA * fA;
        }
        else
        {
            // No solution → native's huge sentinel time: fA is enormous, so the
            // miss distance is too. Never a hit.
            fHA = float.PositiveInfinity;
        }

        float radius = input.TargetRadius;
        float radiusSquared = radius * radius;
        bool willHit = fHA <= radiusSquared;
        float green = 0f;
        if (willHit && radiusSquared > 0f)
            green = 1f - fHA / radiusSquared;          // indicator.cpp:271
        green = Math.Clamp(green, 0f, 1f);

        return new Result(
            GatePassed: true,
            Solved: lead.Solved,
            Time: lead.Time,
            Direction: lead.Direction,
            InRange: inRange,
            Lifespan: lifespan,
            SolveSpeed: solveSpeed,
            RawSpeed: input.ProjectileSpeed,
            DistanceToTarget: distance,
            Forward: forward,
            TrackingPoint: tracking,
            WillHit: willHit,
            Green: green);
    }

    /// <summary>
    /// The cockpit centre-crosshair hit probability <c>g</c>
    /// (<c>indicator.cpp:334-357</c>): native measures the screen-space distance
    /// <c>l</c> between the view centre and the projected lead point against the
    /// target's projected screen radius <c>r</c>
    /// (<c>r = targetModel-&gt;GetThingSite()-&gt;GetScreenRadius()</c>) and applies
    /// <c>f = l²/r²</c>, then <c>g = (f&lt;1) ? 1-0.75·f : (f&lt;2) ? 0.5-f/4 : 0</c>.
    /// A zero/unknown radius yields the native red end (division by zero → f
    /// infinite → g = 0) rather than a NaN.
    /// </summary>
    public static float CockpitGreen(float leadX, float leadY, float centerX, float centerY, float targetScreenRadius)
    {
        float dx = centerX - leadX;
        float dy = centerY - leadY;
        float l2 = dx * dx + dy * dy;
        if (targetScreenRadius <= 1e-6f) return 0f;
        float f = l2 / (targetScreenRadius * targetScreenRadius);
        if (f < 2.0f)
        {
            if (f < 1.0f) return 1.0f - f * 0.75f;     // :344-345
            return 0.5f - f / 4.0f;                    // :346
        }
        return 0.0f;                                   // :347-348
    }
}
