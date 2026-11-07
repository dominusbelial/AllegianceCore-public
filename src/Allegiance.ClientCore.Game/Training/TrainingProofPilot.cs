using System.Numerics;
using Allegiance.ClientCore.Training;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Input-pilot policy only. These functions choose mouse and key inputs;
/// the shipping input handlers and unmodified flight model still apply them.</summary>
public static class TrainingProofPilot
{
    /// <summary>Choose real digital thrust buttons using a separate flight forecast.
    /// Native manual thrust also applies drag to the other velocity components;
    /// evaluate complete combinations over this frame rather than overshooting
    /// a fixed deadband when a software draw takes a second or more.</summary>
    public static int ChooseThrust(Allegiance.ClientCore.Flight.OwnShipSimulator own,
        Allegiance.ClientCore.Flight.OwnShipSimulator forecast, Vector3 desired, float frameSeconds, bool coast)
    {
        int basis = own.StateM & ~(Allegiance.Core.Sim.ShipControlState.MoveButtonsMask
            | Allegiance.Core.Sim.ShipControlState.CoastButton);
        if (coast) basis |= Allegiance.Core.Sim.ShipControlState.CoastButton;
        var controls = own.Controls with { Throttle = -1 };
        var orientation = own.Orientation;
        var turnRates = own.TurnRates;
        float bestCost = float.PositiveInfinity;
        int best = 0;
        // Put neutral first so exact ties preserve matched velocity.
        int[] forward = [0, Allegiance.Core.Sim.ShipControlState.ForwardButton, Allegiance.Core.Sim.ShipControlState.BackwardButton];
        int[] side = [0, Allegiance.Core.Sim.ShipControlState.RightButton, Allegiance.Core.Sim.ShipControlState.LeftButton];
        int[] vertical = [0, Allegiance.Core.Sim.ShipControlState.UpButton, Allegiance.Core.Sim.ShipControlState.DownButton];
        foreach (int f in forward)
            foreach (int s in side)
                foreach (int u in vertical)
                {
                    int movement = f | s | u;
                    forecast.Seed(own.Position, own.Velocity, orientation, turnRates);
                    forecast.Step(frameSeconds, controls, basis | movement);
                    var velocity = forecast.Velocity;
                    float cost = Vector3.DistanceSquared(desired, new(velocity.X, velocity.Y, velocity.Z));
                    if (cost < bestCost) { bestCost = cost; best = movement; }
                }
        return best;
    }

    /// <summary>A proof-local user profile whose six thrust actions remain reachable during training.</summary>
    public static Rendering.ActionMap CreateInputMap()
    {
        // P941/P949 — the scripted proofs drive the keyboard Port key set, which is
        // also the fresh-profile default since P949; pin the preset explicitly so
        // the profile cannot depend on a saved preference.
        var map = new Rendering.ActionMap(Rendering.InputActionCatalog.Definitions, Rendering.InputLayout.Port);
        string? conflict = map.SetBinding("ThrustUp", new(Microsoft.Xna.Framework.Input.Keys.NumPad8, Rendering.InputModifiers.None));
        if (conflict is not null) throw new InvalidOperationException($"Proof Up binding conflicts with {conflict}");
        return map;
    }

    public static (float Yaw, float Pitch) Aim(Vector3 delta, Vector3 relativeVelocity,
        Vector3 forward, Vector3 up, Vector3 right, float sampleSeconds, TrainingTurnTo.TurnHull hull, float correctionScale = 1)
    {
        float distance = delta.Length();
        if (distance < 1) return (0, 0);
        var direction = delta / distance;
        float horizon = MathF.Max(.3f, sampleSeconds);
        if (Vector3.Dot(direction, forward) < .98f)
        {
            var turn = TrainingTurnTo.Compute(Vector3.Zero, delta, forward, up, right, horizon, hull);
            return (turn.Yaw, turn.Pitch);
        }
        // Track the moving sight line rather than repeatedly stopping on its
        // previous position. Differentiating the unit direction removes radial
        // motion. The correction's time horizon scales with the actual sample
        // interval, so a late frame cannot command a high-rate overshoot.
        var derivative = (relativeVelocity - direction * Vector3.Dot(relativeVelocity, direction)) / distance;
        float yaw = (-MathF.Asin(Math.Clamp(Vector3.Dot(right, direction), -1, 1)) * correctionScale / horizon
            - Vector3.Dot(right, derivative)) / hull.MaxTurnRateYaw;
        float pitch = (-MathF.Asin(Math.Clamp(Vector3.Dot(up, direction), -1, 1)) * correctionScale / horizon
            - Vector3.Dot(up, derivative)) / hull.MaxTurnRatePitch;
        float length = MathF.Sqrt(yaw * yaw + pitch * pitch);
        return length > 1 ? (yaw / length, pitch / length) : (yaw, pitch);
    }

    public static Vector3 FollowVelocity(Vector3 delta, Vector3 targetVelocity, float distance, float maxClosing,
        float sampleSeconds, float brakingAcceleration)
    {
        float length = delta.Length();
        if (length < 1) return targetVelocity;
        float gap = length - distance;
        // Reserve both the stopping distance and one delayed sample before
        // the next correction. A proportional speed alone brakes too late
        // when a draw holds the previous thrust for a second or more.
        float delay = MathF.Max(.5f, sampleSeconds * 1.5f);
        float ad = brakingAcceleration * delay;
        float limit = MathF.Min(maxClosing, MathF.Sqrt(ad * ad + 2 * brakingAcceleration * MathF.Abs(gap)) - ad);
        return targetVelocity + delta / length * Math.Clamp(gap * .35f, -limit, limit);
    }
}
