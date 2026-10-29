using System.Numerics;

namespace Allegiance.ClientCore.Training;

/// <summary>
/// TR-2 — the real <c>turnToFace</c> ship-simulator math that the deferred
/// <c>ITrainingSite.TurnToFace</c> slot (TR-1 §Deferred) was waiting for. A faithful
/// port of the native helper <c>turnToFace(delta, dt, pship, controls, skill)</c>
/// (<c>src/Igc/common.cpp:152-252</c>), which the training <c>TurnToAction::Execute</c>
/// calls with the delta to the target and default skill 1.0
/// (<c>src/training/TurnToAction.cpp:61-62</c>), and then damps by 0.5
/// (TurnToAction.cpp:63-65). Pure over a minimal hull-kinematics struct so it is
/// unit-testable without a host; the host binding (TR-2) feeds it the ship's live
/// mass/torque/turn rates from the own-ship simulator.
/// </summary>
public static class TrainingTurnTo
{
    /// <summary>
    /// The hull kinematics <c>turnToFace</c> needs: torque multiplier, mass and the
    /// per-axis turn torque / max turn rate / current turn rate (indices are the
    /// c_axisYaw/Pitch/Roll order). These come from the ship's hull type and live
    /// sim state (<c>common.cpp:213-223, 229-230</c>).
    /// </summary>
    public readonly record struct TurnHull(
        float Mass, float TorqueMultiplier,
        float TurnTorqueYaw, float TurnTorquePitch,
        float MaxTurnRateYaw, float MaxTurnRatePitch,
        float CurrentTurnRateYaw, float CurrentTurnRatePitch);

    /// <summary>Compute the corrective (yaw, pitch, roll) control inputs to turn the ship
    /// toward the target — <c>turnToFace</c> (common.cpp:152-252) with skill 1.0. Roll is
    /// always 0 ("Ships never try to roll", common.cpp:163).</summary>
    public static (float Yaw, float Pitch, float Roll) Compute(
        Vector3 shipPosition, Vector3 targetPosition,
        Vector3 forward, Vector3 up, Vector3 right,
        float dt, TurnHull hull)
    {
        var delta = targetPosition - shipPosition;
        float dl = delta.Length();
        if (dl < 1e-6f)
            return (0f, 0f, 0f);

        Vector3 dNorm = delta / dl;
        float roll = 0f;

        float cosTurn = ClampDot(forward, dNorm);

        if (cosTurn <= -0.999f)
        {
            // Target almost directly behind — just yaw at max (common.cpp:168-176).
            return (1.0f, 0.0f, 0.0f);
        }

        float yaw, pitch;
        if (cosTurn < 0.98f)
        {
            // Target somewhere other than straight ahead (common.cpp:182-190). Native scales
            // the (twist·up)/(twist·right) projections by deltaAngle = acos(cosTurn) so the
            // correction is in the actual angular-error units (radians); the pre-P886b port
            // omitted the deltaAngle factor, under-commanding off-axis turns (a 90° error gave
            // a ~1.0 saturated-sine control instead of ~π/2), weakening the pointing-goal
            // convergence. Restore it.
            float deltaAngle = MathF.Acos(Math.Clamp(cosTurn, -1f, 1f));
            Vector3 backward = -forward;
            Vector3 twist = Normalize(Vector3.Cross(backward, delta));
            yaw = -(Dot(twist, up)) * deltaAngle;
            pitch = Dot(twist, right) * deltaAngle;
        }
        else
        {
            // Almost directly in front — get yaw/pitch off the right*up angles (common.cpp:196-198).
            yaw = MathF.Acos(ClampDot(right, dNorm)) - 0.5f * MathF.PI;
            pitch = MathF.Acos(ClampDot(up, dNorm)) - 0.5f * MathF.PI;
        }

        // Adjust for the drift due to our current turning rates (common.cpp:210-225). P886b —
        // cap the drift so it can NEVER reverse the correction's polarity: at the large, variable
        // frame deltas an offscreen render produces the ship can carry a high instantaneous turn
        // rate, and the (quadratic in turn-rate) drift term then exceeds the remaining angular
        // error, flipping the sign and launching the divergent oscillation the real-window pointing
        // goals intermittently hit (they never settle inside 1°). Clamping to zero (instead of
        // reversing) keeps native's predictive reduction when it helps and is unconditionally
        // stable at any frame rate; native only ever runs at a stable ~60 fps so never hits this.
        float tm = hull.TorqueMultiplier;
        float mass = hull.Mass;
        if (mass > 0f && tm != 0f)
        {
            float yawRate = hull.CurrentTurnRateYaw;
            float pitchRate = hull.CurrentTurnRatePitch;
            float rawYaw = yaw, rawPitch = pitch;
            yaw -= MathF.Abs(yawRate) * (0.5f * yawRate * mass / (tm * hull.TurnTorqueYaw));
            pitch -= MathF.Abs(pitchRate) * (0.5f * pitchRate * mass / (tm * hull.TurnTorquePitch));
            if (yaw * rawYaw < 0f) yaw = 0f;         // drift over-predicted → neutralize, don't reverse
            if (pitch * rawPitch < 0f) pitch = 0f;
        }

        // Convert the angular error into control magnitudes at max rate (common.cpp:227-248).
        float maxYaw = dt * hull.MaxTurnRateYaw;
        float maxPitch = dt * hull.MaxTurnRatePitch;
        float y = maxYaw > 0f ? yaw / maxYaw : 0f;
        float p = maxPitch > 0f ? pitch / maxPitch : 0f;
        float d2 = y * y + p * p;
        if (d2 > 1.0f)
        {
            float f = 1.0f / MathF.Sqrt(d2);
            y *= f;
            p *= f;
        }

        return (y, p, roll);
    }

    /// <summary>Clamped dot of two unit-ish vectors (native CosForward/CosRight/CosUp).</summary>
    private static float ClampDot(Vector3 a, Vector3 b) => Math.Clamp(Dot(a, b), -1f, 1f);

    private static float Dot(Vector3 a, Vector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    private static Vector3 Normalize(Vector3 v)
    {
        float l = v.Length();
        return l > 1e-6f ? v / l : v;
    }
}
