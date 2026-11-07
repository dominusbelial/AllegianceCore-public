using System.Numerics;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.Training;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using CV = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Collection("Training")]
public sealed class P931TrainingPilotTests(ITestOutputHelper output)
{
    [Fact]
    public void Proof_profile_routes_up_thrust_without_consuming_native_accept_command()
    {
        var map = TrainingProofPilot.CreateInputMap();
        var keys = new Microsoft.Xna.Framework.Input.KeyboardState(map.BindingOf("ThrustUp").Key);
        Assert.True(map.IsDownMostSpecific(keys, "ThrustUp"));
        Assert.False(Game1.YieldsToTrainingTarget(keys, "ThrustUp", training: true));
        Assert.False(TrainingInputGate.NativeDefaultKeyDown(keys, "AcceptCommand"));
        Assert.True(TrainingInputGate.NativeDefaultKeyDown(new(Microsoft.Xna.Framework.Input.Keys.Insert), "AcceptCommand"));
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(.15f)] [InlineData(.5f)] [InlineData(1f)] [InlineData(2.3f)] [InlineData(0f)]
    public void Digital_thrust_follows_an_oblique_formation_across_slow_frames(float sample)
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.EnemyEngagement);
        var ship = host.PlayerShip;
        var stats = new Allegiance.ClientCore.Flight.ShipDeadReckoner.HullStats(ship.EffectiveThrust(), ship.EffectiveMaxSpeed(), ship.Mass,
            ship.HullType.SideMultiplier, ship.HullType.BackMultiplier,
            ship.EffectiveMaxTurnRate(0), ship.EffectiveMaxTurnRate(1), ship.EffectiveMaxTurnRate(2),
            ship.EffectiveTurnTorque(0), ship.EffectiveTurnTorque(1), ship.EffectiveTurnTorque(2));
        float Follow(bool predictive)
        {
            var own = new Allegiance.ClientCore.Flight.OwnShipSimulator();
            var forecast = new Allegiance.ClientCore.Flight.OwnShipSimulator();
            own.SetHull(stats, 0); forecast.SetHull(stats, 0);
            own.Seed(CV.Zero, CV.Zero, new Orientation(), [0, 0, 0]);
            var travel = new Vector3(30, 10, -55);
            float total = 0; int count = 0, frame = 0;
            for (float time = 0; time < 40;)
            {
                float dt = sample > 0 ? sample : new[] { .15f, 2.3f, .5f, 1.7f, .25f }[frame++ % 5];
                var formationError = travel * time - SV(own.Position);
                var desired = TrainingProofPilot.FollowVelocity(formationError, travel, 0, 60, dt,
                    ship.EffectiveThrust() / ship.Mass * MathF.Min(ship.HullType.SideMultiplier, ship.HullType.BackMultiplier) * .5f);
                int movement;
                if (predictive)
                {
                    var before = own.Velocity;
                    movement = TrainingProofPilot.ChooseThrust(own, forecast, desired, dt, coast: true);
                    Assert.Equal(before, own.Velocity); // prediction has no actor/own-simulator side effect
                }
                else
                {
                    movement = 0;
                    var error = desired - SV(own.Velocity);
                    var orientation = own.Orientation;
                    void Axis(Vector3 axis, int positive, int negative)
                    {
                        float amount = Vector3.Dot(error, axis);
                        if (MathF.Abs(amount) > 6) movement |= amount > 0 ? positive : negative;
                    }
                    Axis(SV(orientation.GetForward()), ShipControlState.ForwardButton, ShipControlState.BackwardButton);
                    Axis(SV(orientation.GetRight()), ShipControlState.RightButton, ShipControlState.LeftButton);
                    Axis(SV(orientation.GetUp()), ShipControlState.UpButton, ShipControlState.DownButton);
                }
                own.Step(dt, new(0, 0, 0, -1), movement | ShipControlState.CoastButton);
                time += dt;
                if (time >= 8) { total += Vector3.DistanceSquared(travel * time, SV(own.Position)); count++; }
            }
            return MathF.Sqrt(total / count);
        }
        float current = Follow(true), previous = Follow(false);
        output.WriteLine($"M4 formation sample={sample:F3}s forecast position RMS={current:F2}; fixed-deadband RMS={previous:F2}");
        Assert.InRange(current, 0, 150);
        Assert.True(current < previous * .95f, "Forecasted real key inputs must improve on the measured fixed-deadband stall/overshoot");
    }

    [Theory]
    [InlineData(.016667f)]
    [InlineData(.15f)]
    [InlineData(.3f)]
    [InlineData(.45f)]
    [InlineData(0f)]
    public void Mouse_pilot_tracks_a_crossing_miner_with_real_torque_and_sample_delays(float sample)
    {
        // The same moving scene is sampled at 60 Hz and at the slow gate's
        // 2–7 Hz. Each input is held through the full interval, using the real
        // integrating mouse, response curve and torque-limited flight model.
        float improved = Track(sample, movingSightLine: true);
        float previous = Track(sample, movingSightLine: false);
        output.WriteLine($"Sample={sample:F6}s moving-sight-line mean={improved:F4}deg previous={previous:F4}deg");
        Assert.InRange(improved, 0, .65f);
        Assert.True(improved < previous * .6f,
            $"Moving sight-line tracking {improved:F3} degrees; old pilot {previous:F3} degrees");
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(4, .15f)] [InlineData(4, .3f)] [InlineData(4, .5f)] [InlineData(4, 0f)]
    [InlineData(8, .15f)] [InlineData(8, .3f)] [InlineData(8, .5f)] [InlineData(8, 0f)]
    public void Actual_training_hulls_track_a_nearby_crossing_contact(int mission, float sample)
    {
        Require.Artwork();
        using var host = TrainingHost.Start(mission == 4 ? TrainingHost.EnemyEngagement : TrainingHost.Nanite);
        float improved = Track(sample, true, host.PlayerShip, mission == 4 ? .5f : 1);
        float previous = Track(sample, false, host.PlayerShip);
        output.WriteLine($"M{mission} sample={sample:F6}s improved={improved:F4}deg previous={previous:F4}deg");
        Assert.InRange(improved, 0, 1.5f);
        Assert.True(improved < previous * .8f, "The actual hull must improve on the retained controller");
    }

    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(.15f)] [InlineData(.5f)] [InlineData(1f)] [InlineData(2f)] [InlineData(0f)]
    public void Cloaked_approach_brakes_before_colliding_with_the_mining_contact(float sample)
    {
        Require.Artwork();
        using var host = TrainingHost.Start(TrainingHost.EnemyEngagement);
        var ship = host.PlayerShip;
        float Approach(bool bounded)
        {
            var sim = new Allegiance.ClientCore.Flight.OwnShipSimulator();
            sim.SetHull(new(ship.EffectiveThrust(), ship.EffectiveMaxSpeed(), ship.Mass,
                ship.HullType.SideMultiplier, ship.HullType.BackMultiplier,
                ship.EffectiveMaxTurnRate(0), ship.EffectiveMaxTurnRate(1), ship.EffectiveMaxTurnRate(2),
                ship.EffectiveTurnTorque(0), ship.EffectiveTurnTorque(1), ship.EffectiveTurnTorque(2)), 0);
            sim.Seed(new(0, 0, 3000), CV.Zero, new Orientation(), [0, 0, 0]);
            float minimum = 3000;
            int frame = 0;
            for (float time = 0; time < 100;)
            {
                float dt = sample > 0 ? sample : new[] { .15f, 2.5f, .5f, 1.7f, .25f }[frame++ % 5];
                var delta = -SV(sim.Position);
                float braking = ship.EffectiveThrust() / ship.Mass
                    * MathF.Min(ship.HullType.BackMultiplier, ship.HullType.SideMultiplier) * .5f;
                var desired = bounded ? TrainingProofPilot.FollowVelocity(delta, Vector3.Zero, 150, 250, dt, braking)
                    : Vector3.Normalize(delta) * Math.Clamp((delta.Length() - 150) * .35f, -250, 250);
                float error = Vector3.Dot(desired - SV(sim.Velocity), SV(sim.Orientation.GetForward()));
                int state = bounded ? 0 : ShipControlState.CoastButton;
                if (error > 12) state |= ShipControlState.ForwardButton;
                else if (error < -12 && (!bounded || sim.Velocity * sim.Orientation.GetForward() <= 5)) state |= ShipControlState.BackwardButton;
                sim.Step(dt, new(0, 0, 0, -1), state);
                minimum = MathF.Min(minimum, sim.Position.Z);
                time += dt;
            }
            return minimum;
        }
        float safe = Approach(true), previous = Approach(false);
        output.WriteLine($"M4 approach sample={sample:F3}s minimum range={safe:F2}; old={previous:F2}");
        Assert.True(safe > 100, "Do not collide with the miner and trigger its shield-damage retreat before firing");
        if (sample >= 2) Assert.True(previous < 100, "The retained slow-frame collision must reproduce");
    }

    private static float Track(float sample, bool movingSightLine, Ship? ship = null, float correctionScale = 1)
    {
        var orientation = new Orientation();
        var velocity = CV.Zero;
        float[] rates = [0, 0, 0];
        var mouse = new MouseFlightController();
        float totalError = 0;
        int count = 0;
        int frame = 0;
        for (float time = 0; time < 25;)
        {
            float dt = sample > 0 ? sample : new[] { .016667f, .15f, .45f, .3f, .05f }[frame++ % 5];
            var motion = ship is null ? new Vector3(90, 0, 0) : new Vector3(70, 40, 0);
            var target = new Vector3(-300, 40, ship is null ? -550 : -180) + time * motion;
            float mass = ship?.Mass ?? 1000, maxSpeed = ship?.EffectiveMaxSpeed() ?? 200;
            float yawTorque = ship?.EffectiveTurnTorque(0) ?? 2000, pitchTorque = ship?.EffectiveTurnTorque(1) ?? 2000;
            float yawMax = ship?.EffectiveMaxTurnRate(0) ?? 1, pitchMax = ship?.EffectiveMaxTurnRate(1) ?? 1;
            var hull = new TrainingTurnTo.TurnHull(mass, ShipKinematics.GetTorqueMultiplier(velocity, maxSpeed),
                yawTorque, pitchTorque, yawMax, pitchMax, rates[0], rates[1]);
            var forward = SV(orientation.GetForward());
            var up = SV(orientation.GetUp());
            var right = SV(orientation.GetRight());
            (float Yaw, float Pitch) turn;
            if (movingSightLine)
                turn = TrainingProofPilot.Aim(target, motion, forward, up, right, dt, hull, correctionScale);
            else
            {
                var old = TrainingTurnTo.Compute(Vector3.Zero, target, forward, up, right, MathF.Max(.1f, MathF.Min(.25f, dt)), hull);
                turn = (old.Yaw, old.Pitch);
            }
            float Stick(float control) => control == 0 ? 0 : MathF.CopySign(mouse.Deadzone
                + (1 - mouse.Deadzone) * MathF.Sqrt(MathF.Abs(control)), control);
            // P948 — divide by the EFFECTIVE per-pixel scale (image factor x the
            // persisted sensitivity knob), exactly like the production training
            // pilot (Game1.TrainingProof*), so the requested pixel delta really
            // produces the intended stick deflection after the default changed
            // from the old port-neutral 1.0 to native's 0.6.
            float dx = MathF.Round((mouse.Current.Yaw - Stick(turn.Yaw)) / mouse.PixelScale);
            float dy = MathF.Round((mouse.Current.Pitch - Stick(turn.Pitch)) / mouse.PixelScale);
            var deflection = mouse.Update(dx, dy);
            var mapped = MouseFlightController.MapResponse(deflection, mouse.Deadzone, mouse.LinearControls);
            var inputs = new ShipKinematics.Inputs(new(mapped.Yaw, mapped.Pitch, 0, -1), 0,
                ship?.EffectiveThrust() ?? 1000, maxSpeed, mass, 1, 1, yawMax, pitchMax, 1,
                yawTorque, pitchTorque, 2000, 0, 0, false);
            int steps = (int)MathF.Ceiling(dt * 60);
            for (int step = 0; step < steps; step++)
                ShipKinematics.Step(inputs, ref velocity, orientation, rates, dt / steps);
            time += dt;
            if (time < 3) continue;
            var nextTarget = target + motion * dt;
            totalError += MathF.Acos(Math.Clamp(Vector3.Dot(SV(orientation.GetForward()), Vector3.Normalize(nextTarget)), -1, 1)) * 180 / MathF.PI;
            count++;
        }
        return totalError / count;
    }

    private static Vector3 SV(CV value) => new(value.X, value.Y, value.Z);
}
