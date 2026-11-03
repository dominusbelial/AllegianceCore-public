using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

public sealed partial class TrainingHost
{
    public int BuoyConsumerCount(int buoyId)
    {
        lock (_simGate) return Mission.GetBuoy((short)buoyId)?.ConsumerCount ?? 0;
    }
    // Igc/buoyigc.h:76-88; training/CreateDroneAction.cpp:204-207.
    public void AddBuoyConsumer(int buoyId)
    {
        lock (_simGate) Mission.GetBuoy((short)buoyId)?.AddConsumer();
    }

    /// <summary>Native GetVisibleF snapshot for either HUD, under the simulation
    /// lock (Igc/buoyigc.h:67-69; buoyigc.cpp:39-53).</summary>
    public void CopyVisibleBuoys(short sectorId, List<Buoy.Visual> destination)
    {
        lock (_simGate)
        {
            destination.Clear();
            foreach (var buoy in Mission.GetBuoys())
                if (buoy.Visible && buoy.Data.ClusterId == sectorId)
                    destination.Add(new(buoy.Id, sectorId, buoy.Position, buoy.Radius));
        }
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

    // Igc/shipIGC.cpp:4053-4114. Native generates a waypoint in front of a
    // warp; armed towers spread in a randomly pitched/yawed range shell.
    private void ArmTrainingLayer(Ship ship, Model target)
    {
        if (target is Buoy buoy)
        {
            ship.SetLayerBuild(new Ship.LayerBuildGoal(buoy.Data.ClusterId, buoy.Position));
            return;
        }
        if (target is not EquipmentTarget { Body: ResolvedWarp warp }) return;
        var orientation = Orientation.Identity;
        orientation.Set(ToCore(new(warp.Warp.Forward[0], warp.Warp.Forward[1], warp.Warp.Forward[2])));
        var position = warp.Position;
        if (Mission.Core.ProbeTypes.TryGetValue(ship.LayerLaidExpendableTypeId, out var tower))
        {
            float range = Mission.Core.ProjectileTypes.TryGetValue(tower.ProjectileTypeId, out var projectile)
                ? projectile.Lifespan * projectile.Speed * .8f : tower.ScannerRange * .7f;
            range += Random.Shared.NextSingle() * .1f;
            float pitch, yaw;
            do { pitch = Random.Shared.NextSingle() * MathF.PI / 2; yaw = Random.Shared.NextSingle() * MathF.PI / 2; }
            while (pitch < MathF.PI / 4 && yaw < MathF.PI / 4);
            orientation.Pitch(Random.Shared.Next(2) == 0 ? -pitch : pitch);
            orientation.Yaw(Random.Shared.Next(2) == 0 ? -yaw : yaw);
            position += orientation.GetForward() * range;
        }
        else if (Mission.Core.MineTypes.TryGetValue(ship.LayerLaidExpendableTypeId, out var mine))
            position += orientation.GetForward() * mine.MineRadius;
        else return;
        ship.SetLayerBuild(new Ship.LayerBuildGoal(warp.Warp.ClusterId, position));
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
        SaveTrainingLoadout(ship); // training/CreateDroneAction.cpp:192
    }
}
