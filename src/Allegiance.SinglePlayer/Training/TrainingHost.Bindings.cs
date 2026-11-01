using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

public sealed partial class TrainingHost
{
    private readonly Dictionary<int, int> _buoyConsumers = new();
    private readonly Dictionary<(int Ship, int Command), int?> _buoySlots = new();
    public int BuoyConsumerCount(int buoyId) => _buoyConsumers.GetValueOrDefault(buoyId);
    // Igc/buoyigc.h:76-88; training/CreateDroneAction.cpp:204-207.
    public void AddBuoyConsumer(int buoyId)
    {
        lock (_simGate) _buoyConsumers[buoyId] = BuoyConsumerCount(buoyId) + 1;
    }

    // training/TurnToAction.cpp:57-65; Igc/common.cpp:152-252, using the requested source ship.
    public (float Yaw, float Pitch, float Roll) TurnToFace(int shipId, System.Numerics.Vector3 target, float dt)
    {
        lock (_simGate)
        {
            if (ResolveShip(shipId) is not { } ship) return default;
            var hull = new Allegiance.ClientCore.Training.TrainingTurnTo.TurnHull(
                ship.Mass, ShipKinematics.GetTorqueMultiplier(ship.Velocity, ship.EffectiveMaxSpeed()),
                ship.EffectiveTurnTorque(ControlData.AxisYaw), ship.EffectiveTurnTorque(ControlData.AxisPitch),
                ship.EffectiveMaxTurnRate(ControlData.AxisYaw), ship.EffectiveMaxTurnRate(ControlData.AxisPitch),
                ship.TurnRates[ControlData.AxisYaw], ship.TurnRates[ControlData.AxisPitch]);
            return Allegiance.ClientCore.Training.TrainingTurnTo.Compute(ToNum(ship.Position), target,
                ToNum(ship.Orientation.GetForward()), ToNum(ship.Orientation.GetUp()), ToNum(ship.Orientation.GetRight()), dt, hull);
        }
    }

    // training/CreateDroneAction.cpp:65-72: copy roll as well as forward.
    public void CopyDroneOrientation(int shipId, int sourceId)
    {
        lock (_simGate)
            if (ResolveShip(shipId) is { } ship && ResolveShip(sourceId) is { } source)
                ship.SetOrientation(source.Orientation);
    }

    // training/CreateDroneAction.cpp:86-89: station base data takes precedence.
    public void ConfigureExpendable(int shipId, int typeId)
    {
        lock (_simGate)
            if (ResolveShip(shipId) is { BuilderStationType: null } ship)
                ship.LayerLaidExpendableTypeId = (short)typeId;
    }

    // training/CreateDroneAction.cpp:91-198. Run after the native default loadout.
    private void ApplyDroneTechLevel(Ship ship, int level)
    {
        if (level == 1) return;
        int tech = Math.Abs(level);
        DataPartTypeIGC? Part(short id) => Mission.Core.PartTypes.TryGetValue(id, out var p)
            || Mission.Core.TryBuildExpendableLauncher(id, out p) ? p : null;
        void Replace(MountedPart old, short id, short amount = 1)
        {
            ship.TryRemovePart(old.EquipmentType, old.MountId, out _);
            if (Part(id) is { } part)
                ship.MountPart(part, amount, old.MountId, ExpandedPartData.FullyMounted);
        }
        foreach (var weapon in ship.Parts.Where(p => p.EquipmentType == EquipmentType.Weapon).ToArray())
        {
            if (level < 0 && Part(91) is { } nan && ship.HullType.CanMountPart(nan, 0))
            {
                Replace(weapon, level == -1 ? (short)91 : level == -2 ? (short)190 : (short)223);
                break;
            }
            if (tech > 1 && weapon.PartId == 33) Replace(weapon, tech == 2 ? (short)71 : (short)72);
            else if (tech > 1 && weapon.PartId == 120) Replace(weapon, tech == 2 ? (short)121 : (short)122);
            else if (level < 1 && weapon.PartId == 200 && weapon.MountId > 0)
                ship.TryRemovePart(EquipmentType.Weapon, weapon.MountId, out _);
        }
        foreach (var part in ship.Parts.ToArray())
        {
            if (tech <= 1) continue;
            short replacement = part.PartId switch
            {
                23 => tech == 2 ? (short)27 : (short)29,
                153 => tech == 2 ? (short)154 : (short)156,
                150 => tech == 2 ? (short)103 : (short)105,
                157 => tech == 2 ? (short)158 : (short)601,
                _ => -1,
            };
            if (replacement >= 0) Replace(part, replacement, part.EquipmentType == EquipmentType.Magazine ? (short)127 : (short)1);
        }
        if (level < 0 && Part(59) is { } prox && ship.HullType.CanMountPart(prox, 0))
        {
            // Native leaves pprox uninitialized below -3; no shipped recipe uses that input.
            if (level < -3) throw new ArgumentOutOfRangeException(nameof(level));
            prox = Part(level == -1 ? (short)59 : level == -2 ? (short)58 : (short)57)!.Value;
            ship.TryRemovePart(EquipmentType.Dispenser, 0, out _);
            ship.MountPart(prox, 127, 0, ExpandedPartData.FullyMounted);
            for (int mount = -Ship.CargoCapacity; mount < 0; mount++)
            {
                byte slot = unchecked((byte)mount);
                if (ship.CargoHold[Ship.CargoSlotForMount(mount)] is { } old)
                    ship.TryRemoveCargoPart(mount, out _);
                ship.MountPart(prox, 127, slot, ExpandedPartData.FullyMounted);
            }
        }
    }
}
