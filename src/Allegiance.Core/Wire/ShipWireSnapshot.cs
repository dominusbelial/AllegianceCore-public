using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Wire;

// Owned encoder data: Orientation is a private COPY; every other member is a value.
// Never put a Ship, Sector, Mission, a live collection, or a callback in this type.
internal sealed record ShipWireSnapshot(short Id, int State, ControlData Controls,
    uint Time, Vector3 Position, Orientation Orientation, Vector3 Velocity,
    float Yaw, float Pitch, float Roll, float Hull, float Shield,
    float Ammo, float Fuel, float Energy, float Ore)
{
    internal static ShipWireSnapshot Capture(short id, Ship ship, uint time,
        int extraStateBits = 0, bool carryEndurance = false)
    {
        float maxEnergy = ship.EffectiveMaxEnergy();
        return new(id, ship.StateM | extraStateBits, ship.Controls, time,
            ship.Position, ship.Orientation.Copy(), ship.Velocity,
            ship.TurnRates[0], ship.TurnRates[1], ship.TurnRates[2],
            ship.Fraction, ship.ShieldFraction,
            ship.HullType.MaxAmmo > 0 ? ship.Ammo / (float)ship.HullType.MaxAmmo : 0f,
            carryEndurance ? ship.LifepodEnduranceFraction(ship.Mission?.LastUpdate ?? 0) : 1f,
            maxEnergy > 0 ? ship.Energy / maxEnergy : 0f,
            ship.Ore / ship.EffectiveMineCapacity());
    }
}
