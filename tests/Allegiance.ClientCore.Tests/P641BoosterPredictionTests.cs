using Allegiance.ClientCore.Flight;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Xunit;

namespace Allegiance.ClientCore.Tests;

public class P641BoosterPredictionTests
{
    [Theory]
    [InlineData(255, 100)] [InlineData(0, 100)] [InlineData(255, 0)]
    public void Latched_boost_release_and_removal_match_authoritative_flight(byte mounted, float fuel)
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
        for (int i = 1; i <= 180; i++)
        {
            int bits = i <= 100 ? ShipControlState.AfterburnerButton : 0;
            server.Controls = ControlData.Neutral;
            server.StateM = bits;
            server.Update(i * .05);
            client.Step(.05f, ControlData.Neutral, bits);
            Assert.True((server.Position - client.Position).Length() < .05f, $"tick {i}: server {server.Position}, own {client.Position}");
        }
        Assert.Equal(0, client.Power);
        server.TryRemovePart(EquipmentType.Afterburner, 0, out _);
        client.SetAfterburner(null, 255, 0, 1);
        for (int i = 181; i <= 200; i++)
        {
            server.StateM = ShipControlState.AfterburnerButton;
            server.Update(i * .05);
            client.Step(.05f, ControlData.Neutral, server.StateM);
        }
        Assert.True((server.Position - client.Position).Length() < .05f);
    }
}
