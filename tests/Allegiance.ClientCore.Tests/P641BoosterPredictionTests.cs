using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.ClientCore.Tests;

public class P641BoosterPredictionTests
{
    [Theory]
    [InlineData(255, 100, .05f)] [InlineData(0, 100, .05f)] [InlineData(255, 0, .05f)]
    [InlineData(240, 100, .05f)] [InlineData(239, 100, .05f)] [InlineData(0, 100, .001f)]
    public void Latched_boost_release_and_removal_match_authoritative_flight(byte mounted, float fuel, float dt)
    {
        var hull = default(DataHullTypeIGC) with { Mass = 100, Speed = 100, Thrust = 1000,
            MaxFuel = 100, MaxEnergy = 100, HitPoints = 100, MaxTurnRates = [1, 1, 1], TurnTorques = [100, 100, 100],
            BackMultiplier = 1, SideMultiplier = 1, Hardpoints = [], PmEquipment = [] };
        var type = new DataPartTypeIGC.Afterburner(.0001f, 2000, 3, 2, 0, 0);
        var part = default(DataPartTypeIGC) with { PartId = 20, Type = type };
        var server = new Ship(1, hull, default, default, Orientation.Identity);
        server.MountPart(part, 1, 0, mounted);
        server.SetFuel(fuel);
        var client = new OwnShipSimulator();
        client.SetHull(ShipDeadReckoner.HullStats.FromHull(hull), type.MaxThrust);
        client.SetAfterburner(type, mounted, fuel, 1);
        client.Seed(default, default, Orientation.Identity, [0, 0, 0]);
        int ticks = (int)MathF.Round(9 / dt);
        int release = (int)MathF.Round(5 / dt);
        for (int i = 1; i <= ticks; i++)
        {
            int bits = i <= release ? ShipControlState.AfterburnerButton : 0;
            server.Controls = ControlData.Neutral;
            server.StateM = bits;
            server.Update(i * (double)dt);
            client.Step(dt, ControlData.Neutral, bits);
            Assert.True((server.Position - client.Position).Length() < .05f, $"tick {i}: server {server.Position}, own {client.Position}");
        }
        Assert.Equal(0, client.Power);
        server.TryRemovePart(EquipmentType.Afterburner, 0, out _);
        client.SetAfterburner(null, 255, 0, 1);
        for (int i = ticks + 1; i <= ticks + 20; i++)
        {
            server.StateM = ShipControlState.AfterburnerButton;
            server.Update(i * (double)dt);
            client.Step(dt, ControlData.Neutral, server.StateM);
        }
        Assert.True((server.Position - client.Position).Length() < .05f);
    }

    [Theory]
    [InlineData(0f)] [InlineData(0.00001f)]
    public void P666_Loaded_zero_and_small_mount_rates_do_not_become_the_default(float rate)
    {
        var hull = default(DataHullTypeIGC) with { Mass = 100, Speed = 100, Thrust = 1000, BackMultiplier = 1, SideMultiplier = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1] };
        var sim = new OwnShipSimulator();
        sim.SetHull(ShipDeadReckoner.HullStats.FromHull(hull), 2000);
        sim.SetAfterburner(new DataPartTypeIGC.Afterburner(0, 2000, 3, 2, 0, 0), 0, 100, rate);
        sim.Seed(default, default, Orientation.Identity, [0, 0, 0]);
        sim.Step(10, ControlData.Neutral with { Throttle = -1 }, ShipControlState.AfterburnerButton);
        Assert.Equal(0, sim.Power);
    }

    [Fact]
    public void P666_Thrust_GA_changes_acceleration_but_not_fuel_cadence()
    {
        var hull = default(DataHullTypeIGC) with { Mass = 100, Speed = 100, Thrust = 1000,
            BackMultiplier = 1, SideMultiplier = 1, MaxTurnRates = [1, 1, 1], TurnTorques = [1, 1, 1] };
        var type = new DataPartTypeIGC.Afterburner(.01f, 2000, 3, 2, 0, 0);
        OwnShipSimulator Configure(float factor)
        {
            var sim = new OwnShipSimulator();
            sim.SetHull(ShipDeadReckoner.HullStats.FromHull(hull), type.MaxThrust * factor);
            sim.SetAfterburner(type, 240, 1, 1, thrustFactor: factor);
            sim.Seed(default, default, Orientation.Identity, [0, 0, 0]);
            return sim;
        }
        var slow = Configure(.5f); var fast = Configure(2f);
        for (int tick = 1; tick <= 6; tick++)
        {
            slow.Step(.05f, ControlData.Neutral, ShipControlState.AfterburnerButton);
            fast.Step(.05f, ControlData.Neutral, ShipControlState.AfterburnerButton);
            Assert.Equal(slow.Power, fast.Power);
        }
        Assert.Equal(0, slow.Power); // both exhaust the same raw-thrust fuel budget
        Assert.True(fast.Position.Z < slow.Position.Z);
    }

}
