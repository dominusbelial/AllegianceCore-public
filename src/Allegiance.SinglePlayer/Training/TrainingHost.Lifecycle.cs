using System.Collections.Concurrent;
using System.Numerics;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

public readonly record struct TrainingLifecycleEvent(bool Death, Vector3 Focus, float Radius);

public sealed partial class TrainingHost
{
    private readonly ConcurrentQueue<TrainingLifecycleEvent> _lifecycleEvents = new();
    private Sector? _deadSector;

    public bool TryTakeLifecycleEvent(out TrainingLifecycleEvent trainingEvent) => _lifecycleEvents.TryDequeue(out trainingEvent);

    // Global native GetShip presence, independent of the trainee's replication interest.
    // training/trainingmission.cpp:180,198,294.
    public bool TrainingShipExists(int shipId)
    {
        lock (_simGate)
            return Mission.Sectors.SelectMany(s => s.Models).OfType<Ship>().Any(s => s.Id == shipId && !s.Dead);
    }

    private bool HandleTraineeDeath(Ship ship, IModelIgc? launcher)
    {
        if (!ReferenceEquals(ship, PlayerShip)) return false;
        if (_deadSector is not null) return true;
        _deadSector = ship.Sector;
        if (_deadSector is null) return true;
        // training/trainingmission.cpp:453-474. Native dereferences a null
        // launcher and divides by zero at coincident positions; use a finite
        // backward direction for those cases so a collision cannot end play.
        var focus = launcher switch { Model model => model.Position, Station station => station.Center, Asteroid asteroid => new Allegiance.Core.Collision.Vector3(asteroid.Data.Position[0], asteroid.Data.Position[1], asteroid.Data.Position[2]), _ => ship.Position };
        float radius = launcher switch
        {
            Ship attacker => attacker.Radius, Station station => station.Radius,
            Asteroid asteroid => asteroid.Radius, Probe probe => probe.ProbeType.Radius,
            Mine mine => mine.MineType.MineRadius, IOrdnance ordnance => ordnance.Radius,
            Chaff chaff => chaff.Radius, Treasure => Treasure.PickupRadius, _ => ship.Radius,
        };
        var delta = ship.Position - focus;
        float distance = delta.Length();
        var direction = distance > 0 ? delta / distance : -ship.Orientation.GetForward();
        float separation = 1.5f * (ship.Radius + radius);
        if (separation > distance) ship.Position = focus + direction * separation;
        ship.Velocity = direction * (ship.Velocity.Length() * 0.5f);
        _lifecycleEvents.Enqueue(new(true, ToNum(focus), radius));
        ship.Sector?.Remove(ship);
        ship.Sector = null;
        Console.WriteLine($"[training] trainee death: hull {ship.HullType.HullId} detached from {_deadSector.Id}");
        return true;
    }

    public void RestoreTrainee()
    {
        lock (_simGate)
        {
            if (_deadSector is not { } sector) return;
            PlayerShip.SetFraction(1);
            Mission.PlaceShip(PlayerShip, sector);
            _deadSector = null;
            _server.ReplicateTrainingRestore(PlayerShip, sector);
            Console.WriteLine($"[training] trainee restored: hull {PlayerShip.HullType.HullId}, sector {sector.Id}");
        }
    }

    private void HandleStationDeath(Station station)
    {
        // training/trainingmission.cpp:539-548: only the last friendly restart base.
        if (station.SideId != PlayerShip.SideId || !station.Type.HasCapability(DataStationTypeIGC.SabmRestart)) return;
        bool remains = Mission.Sectors.SelectMany(s => s.StationRuntimes).Any(s =>
            !ReferenceEquals(s, station) && !s.Dead && s.SideId == PlayerShip.SideId
            && s.Type.HasCapability(DataStationTypeIGC.SabmRestart));
        if (!remains) _lifecycleEvents.Enqueue(new(false, default, 0));
    }

    // training/trainingmission.cpp:810-854. Both launchers and ordinary part
    // records resolve through the shipping static core; equipment is real IGC state.
    private static void EquipDefaultTrainingLoadout(Ship ship)
    {
        if (ship.Mission is not { } mission) return;
        var station = mission.Sectors.SelectMany(s => s.StationRuntimes).FirstOrDefault(s =>
            s.SideId == ship.SideId && !s.Dead && s.Type.HasCapability(DataStationTypeIGC.SabmLand));
        var techs = mission.Sides.First(s => s.Id == ship.SideId).DevelopmentTechs;
        DataPartTypeIGC? Part(short id) => mission.Core.PartTypes.TryGetValue(id, out var p)
            || mission.Core.TryBuildExpendableLauncher(id, out p) ? p : null;
        bool CanBuy(DataPartTypeIGC p) => station is null
            || TechTreeBitMask.RequiredTechsBuyable(p.TtbmRequired, station.Type.TtbmLocal, techs);
        long budget = ship.HullType.Price;
        bool Launcher(DataPartTypeIGC part) => part.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher;
        short Buy(DataPartTypeIGC part, int mount, bool onBudget = true)
        {
            short amount = short.MaxValue;
            long price = part.Price;
            if (onBudget && price != 0)
            {
                if (Launcher(part))
                {
                    amount = Ship.ClampPartAmount(ship.HullType, mission.Core, part, short.MaxValue);
                    amount = (short)Math.Min(amount, Math.Max(0, budget / price));
                    price *= amount;
                }
                else if (budget < price) amount = 0;
            }
            if (amount == 0) return 0;
            if (onBudget) budget -= price;
            ship.MountPart(part, amount, unchecked((byte)mount), ExpandedPartData.FullyMounted);
            return amount;
        }
        foreach (short id in ship.HullType.PreferredPartsTypes)
        {
            if (id < 0) break;
            if (Part(id) is not { } part || !CanBuy(part)) continue;
            if (station is not null) part = SuccessorUpgrade.PartSuccessor(mission.Core, part, station.Type.TtbmLocal, techs);
            int count = part.EquipmentType == EquipmentType.Weapon ? ship.HullType.MaxWeapons : 1;
            for (int mount = 0; mount < count; mount++)
                if (ship.HullType.CanMountPart(part, mount) && !ship.Parts.Any(p => p.EquipmentType == part.EquipmentType && p.MountId == mount))
                    Buy(part, mount, station is not null);
        }
        if (station is null)
        {
            short[] cargo = [24, 24, 25, 150, 150];
            for (int i = 0; i < cargo.Length; i++)
                if (Part(cargo[i]) is { } part)
                    ship.MountPart(part, short.MaxValue, unchecked((byte)(-i - 1)), ExpandedPartData.FullyMounted);
        }
        else
        {
            // clintlib/clintlib.h:2143-2297: local default cargo, in native -5..-1 order.
            DataPartTypeIGC? Mounted(EquipmentType et) => ship.Parts.FirstOrDefault(p => p.EquipmentType == et)?.Type;
            DataPartTypeIGC? Buyable(DataPartTypeIGC? part) => part is { } p && CanBuy(p) ? p : null;
            DataPartTypeIGC? Pack(byte kind) => mission.Core.PartTypes.Values.Where(p => p.Type is DataPartTypeIGC.Pack pack && pack.PackType == kind).Select(p => (DataPartTypeIGC?)p).FirstOrDefault();
            var ammo = ship.Parts.Any(p => p.Type.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: > 0 }) ? Buyable(Pack(0)) : null;
            var fuel = Mounted(EquipmentType.Afterburner) is not null ? Buyable(Pack(1)) : null;
            var missile = Buyable(Mounted(EquipmentType.Magazine));
            var dispenser = Buyable(Mounted(EquipmentType.Dispenser));
            if (dispenser?.PartMask == 2) dispenser = null; // native c_pbm2
            DataPartTypeIGC? nan = Part(91) is { } n ? SuccessorUpgrade.PartSuccessor(mission.Core, n, station.Type.TtbmLocal, techs) : null;
            bool combat = !(ship.HullType.DefenseType == 1 && ship.HullType.ScannerRange >= 2200);
            int cargo = -Ship.CargoCapacity;
            void Cargo(DataPartTypeIGC? part, bool onBudget = true)
            {
                if (part is { } p && cargo < 0) Buy(p, cargo++, onBudget);
            }
            if (nan is { } np && ship.HullType.CanMountPart(np, 0) && CanBuy(np)) Cargo(np);
            Cargo(ammo);
            if (ship.HullType.MaxWeapons > 2) Cargo(ammo);
            Cargo(fuel);
            if (combat) { Cargo(fuel); Cargo(dispenser); }
            else
            {
                if (missile is null) Cargo(ammo);
                var prox = Buyable(Part(59));
                if (prox is { } pp && ship.HullType.CanMountPart(pp, 0))
                {
                    pp = SuccessorUpgrade.PartSuccessor(mission.Core, pp, station.Type.TtbmLocal, techs);
                    Cargo(pp);
                    if (cargo < -2) Cargo(pp);
                }
                else if (Buyable(Part(65)) is { } plas && ship.HullType.CanMountPart(plas, 0))
                {
                    plas = SuccessorUpgrade.PartSuccessor(mission.Core, plas, station.Type.TtbmLocal, techs);
                    Cargo(plas); Cargo(plas);
                }
            }
            bool buyFuel = true;
            while (cargo < 0)
            {
                if (missile is not null) Cargo(missile, false);
                else if (dispenser is not null) Cargo(dispenser, false);
                else if (fuel is not null && buyFuel) { Cargo(fuel, false); buyFuel = ammo is null; }
                else if (ammo is not null) Cargo(ammo, false);
                else { Cargo(Buyable(Mounted(EquipmentType.ChaffLauncher)), false); break; }
            }
        }
        if (ship.Parts.Any(p => p.EquipmentType == EquipmentType.Shield && p.MountId == 0))
            ship.SetShieldFraction(1);
        ship.Ammo = ship.HullType.MaxAmmo;
        ship.Energy = ship.EffectiveMaxEnergy();
        ship.SetFuel(ship.HullType.MaxFuel);
    }
}
