using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.SinglePlayer;

public sealed partial class TrainingHost
{
    /// <summary>Local BuyLoadout: purchase, completion notification, then optional
    /// launch, all before returning (clintlib/clintlib.cpp:1731-1744).
    /// The client holds its session gate while this holds the simulation gate.</summary>
    public void BuyLoadout(short hullId, IReadOnlyList<ExpandedPartData> parts, bool launch,
        Action<Ship> purchaseCompleted, Action<Ship> launched)
    {
        // Export the requested loadout before replacing any live equipment.
        var exported = parts.ToArray();
        lock (_simGate)
        {
            var ship = PlayerShip;
            var station = ship.StationId is { } id ? Mission.GetStation(id) : null;
            // Native only asserts here; the hangar can still be open when its station
            // dies, so a stale Buy/Launch click is a logged no-op, not a client crash.
            if (station is null || station.Dead)
            {
                Console.Error.WriteLine("[training] loadout purchase ignored: the trainee is not docked at a live station");
                return;
            }
            using (ship.BeginLoadoutUpdate())
                PurchaseShipLoadout(ship, station, hullId, exported);

            // Native deliberately ignores PurchaseShipLoadout's result and
            // reports true even when a requested hull/part cannot be supplied.
            purchaseCompleted(ship);
            if (!launch) return;

            var sector = Mission.Sectors.First(s => s.Id == station.Data.ClusterId);
            ship.StationId = null;
            // SetStation(NULL) -> ChangeStation repairs/refuels only AFTER
            // OnPurchaseCompleted (WinTrek/trekigc.cpp:2870-2883).
            ship.RepairAndRefuel(station.Type);
            station.Launch(ship, sector.StationLandingBays?.LaunchGeometry(station.Type), Mission.LastUpdate);
            Mission.PlaceShip(ship, sector);
            launched(ship);
        }
    }

    // Igc/shipIGC.h:989-1111. Local training has no wallet transaction or
    // server BUY_LOADOUT validation. Station availability and successors still
    // apply; changing the hull may fail before any parts are detached.
    private void PurchaseShipLoadout(Ship ship, Station station, short hullId, ExpandedPartData[] parts)
    {
        var techs = Mission.Sides.First(s => s.Id == ship.SideId).DevelopmentTechs;
        bool CanBuy(byte[] required) => TechTreeBitMask.RequiredTechsBuyable(required, station.Type.TtbmLocal, techs);
        DataPartTypeIGC? Part(short id) => Mission.Core.PartTypes.TryGetValue(id, out var part)
            || Mission.Core.TryBuildExpendableLauncher(id, out part) ? part : null;
        var hull = ship.HullType;
        if (hullId != hull.HullId)
        {
            if (!Mission.Core.HullTypes.TryGetValue(hullId, out hull) || !CanBuy(hull.TtbmRequired)) return;
            hull = SuccessorUpgrade.HullSuccessor(Mission.Core, hull, station.Type.TtbmLocal, techs);
        }
        var old = ship.Parts.Concat(ship.CargoHold.OfType<MountedPart>()).ToList();
        ship.ClearLoadout();
        if (hull.HullId != ship.HullType.HullId)
        {
            ship.ChangeHull(hull);
            // SetBaseHullType resets these pools (shipIGC.cpp:1428-1437).
            ship.Energy = 0;
            ship.Ammo = 0;
            ship.SetFuel(0);
            ship.Ore = 0;
            ship.SetFraction(1);
        }
        foreach (var requested in parts)
        {
            int mount = unchecked((sbyte)requested.MountId);
            if (Part(requested.PartId) is not { } part || !hull.CanMountPart(part, mount)
                || (mount < 0 ? ship.CargoHold[-mount - 1] is not null
                    : ship.Parts.Any(p => p.EquipmentType == part.EquipmentType && p.MountId == mount))) continue;
            short amount = requested.Amount;
            if (CanBuy(part.TtbmRequired))
                part = SuccessorUpgrade.PartSuccessor(Mission.Core, part, station.Type.TtbmLocal, techs);
            else
            {
                // Reuse unavailable equipment; launchers scavenge old stacks
                // (shipIGC.h:1043-1091). Training's all-tech sides normally buy.
                int index = old.FindIndex(p => p.PartId == part.PartId);
                if (index < 0) continue;
                amount = old[index].Amount;
                old.RemoveAt(index);
                if (part.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser or EquipmentType.ChaffLauncher
                    && amount < requested.Amount)
                {
                    short initial = amount;
                    for (int i = 0; i < old.Count;)
                    {
                        if (old[i].PartId != part.PartId) { i++; continue; }
                        // Native uses the original ammo for each donor. Preserve
                        // that behavior rather than inventing a stacking rule.
                        int combined = initial + old[i].Amount;
                        amount = (short)Math.Min(combined, requested.Amount);
                        old.RemoveAt(i);
                    }
                }
            }
            ship.MountPart(part, amount, requested.MountId, ExpandedPartData.FullyMounted);
        }
    }
}
