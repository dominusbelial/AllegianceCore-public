using System.Numerics;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P886b — regression for the real-window pointing-goal flake (M2 goal 2.6, M3 goals 5.3/5.5/7/8):
/// the port's <c>TurnToFace</c> autoproof intermittently failed to converge to &lt;1° in an offscreen
/// render. Root causes, both fixed in <see cref="TrainingTurnTo.Compute"/>:
/// <list type="number">
/// <item><b>Missing deltaAngle factor</b> — native's off-axis branch scales the twist-axis
///   projection by <c>deltaAngle = acos(cosTurn)</c> (Igc/common.cpp:182-190) so the correction is
///   in angular-error radians; the port omitted it, so a ~90° error commanded a ~1.0 saturated-sine
///   turn instead of ~π/2, under-powering off-axis corrective turns and stalling convergence.</item>
/// <item><b>Drift-adjust sign reversal</b> — native's current-turn-rate prediction
///   (common.cpp:205-225) is quadratic in the instantaneous turn rate; at the large, variable frame
///   deltas an offscreen render produces, the ship can carry a high turn rate whose drift term
///   exceeds the remaining angular error, flipping the correction and launching a divergent
///   oscillation. Native only runs at a stable ~60 fps so never sees the large-dt regime. The fix
///   caps the drift so it can never reverse the correction's polarity (unconditionally contractive).
/// </list>
/// These tests drive the PRODUCTION <see cref="TrainingTurnTo.Compute"/> with defined geometry and
/// fail before the fixes.
/// </summary>
public sealed class P886bTurnToFaceStabilityTests
{
    private const float Mass = 100000f;
    private const float TorqueMultiplier = 1f;
    private const float TurnTorque = 100000000f;   // 1e8 → drift at max rate ≈ 0.1° (benign)
    private const float MaxTurnRate = 2f;

    private static TrainingTurnTo.TurnHull Hull(float yawRate = 0f, float pitchRate = 0f) => new(
        Mass: Mass,
        TorqueMultiplier: TorqueMultiplier,
        TurnTorqueYaw: TurnTorque,
        TurnTorquePitch: TurnTorque,
        MaxTurnRateYaw: MaxTurnRate,
        MaxTurnRatePitch: MaxTurnRate,
        CurrentTurnRateYaw: yawRate,
        CurrentTurnRatePitch: pitchRate);

    [Fact]
    public void Training_action_bits_match_the_ship_state_layout()
    {
        // P886b — real-input firing: the training GetStateBitsCondition/constraints gate the bit
        // the ship's StateM ACTUALLY carries (ShipControlState layout). A mismatch (previously
        // AfterburnerButton was 512/native while the ship uses 1024) strips the player's genuine
        // input through ApproveActions' allowable mask, freezing the afterburner gate. Guards the
        // alignment; the weapons/missile/cloak bits already match.
        Assert.Equal(ShipControlState.AfterburnerButton, TrainingMissionIds.AfterburnerButton);
        Assert.Equal(ShipControlState.WeaponsMask, TrainingMissionIds.WeaponsMask);
        Assert.Equal(ShipControlState.MissileFire, TrainingMissionIds.MissileFire);
        Assert.Equal(ShipControlState.CloakActive, TrainingMissionIds.CloakActive);
    }

    [Fact]
    public void Large_angle_correction_is_scaled_by_the_actual_angular_error()
    {
        // Native scales the off-axis yaw/pitch by deltaAngle = acos(cosTurn) (Igc/common.cpp:182-190);
        // the pre-fix port omitted the deltaAngle factor, so a 90° error produced a raw yaw of ~1.0
        // (saturated sine) instead of ~π/2. Use a large dt so maxYaw = dt·maxRate is big enough that
        // the raw correction is NOT clamped to ±1, exposing the scale: with dt=1.0, maxYaw=2.0, so a
        // right target (negative yaw) yields ≈ -π/2/2 = -0.785 with the fix vs ≈ -0.5 without.
        var pos = Vector3.Zero;
        var target = new Vector3(0f, 0f, 100f);   // 90° to the right of forward (+X)
        var (yaw, _, _) = TrainingTurnTo.Compute(pos, target, new Vector3(1f, 0f, 0f),
            new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 1f), 1.0f, Hull());
        Assert.True(yaw < -0.7f, $"90° off-axis error should command ≈ -0.785 (got yaw={yaw:F2})");
    }

    [Fact]
    public void Drift_adjustment_never_reverses_the_correction_direction()
    {
        // With a large current turn rate (the offscreen large-dt regime), the native drift term can
        // exceed the raw error and flip the correction's sign — the divergent-oscillation mechanism.
        // The fix caps the drift so the demanded turn never points the wrong way.
        var pos = Vector3.Zero;
        var target = new Vector3(0f, 0f, 100f);   // to the right of +X forward
        var fwd = new Vector3(1f, 0f, 0f);
        var up = new Vector3(0f, 1f, 0f);
        var right = new Vector3(0f, 0f, 1f);

        float y0 = TrainingTurnTo.Compute(pos, target, fwd, up, right, 0.050f, Hull(yawRate: 0f)).Yaw;
        Assert.True(y0 < 0f, $"sanitize: right target must command a right (negative) turn (got {y0:F2})");

        // Aggressively already turning right (negative rate) at a very high rate, so the drift term
        // would exceed the error and flip the correction without the clamp.
        float yHigh = TrainingTurnTo.Compute(pos, target, fwd, up, right, 0.090f,
            Hull(yawRate: -6f)).Yaw;
        Assert.True(yHigh <= 0f,
            $"drift must not reverse a right turn into a left command (got yHigh={yHigh:F2} from y0={y0:F2})");
    }
}
