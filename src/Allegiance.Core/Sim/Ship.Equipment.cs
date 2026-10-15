using Allegiance.Core.Collision;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{
    // Igc/dispenserigc.h:72-76,148-152: mount activation/load reset uses
    // the loaded mount rate plus the actual expendable's load time.
    private float? DispenserLoadTime(MountedPart part) => part.Type.Type switch
    {
        DataPartTypeIGC.ChaffLauncher p when Mission?.Core.ChaffTypes.TryGetValue(p.ExpendableTypeId, out var t) == true => t.LoadTime,
        DataPartTypeIGC.MineLauncher p when Mission?.Core.MineTypes.TryGetValue(p.ExpendableTypeId, out var t) == true => t.LoadTime,
        DataPartTypeIGC.ProbeLauncher p when Mission?.Core.ProbeTypes.TryGetValue(p.ExpendableTypeId, out var t) == true => t.LoadTime,
        _ => null
    };

    private void UpdateDispensers(double now)
    {
        // Capture both before calling out: callbacks can replace the loadout.
        var chaff = _parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.ChaffLauncher && p.MountId == 0);
        var dispenser = _parts.FirstOrDefault(p => p.EquipmentType == EquipmentType.Dispenser && p.MountId == 0);
        if (chaff is not null) UpdateDispenser(chaff, now);
        if (dispenser is not null) UpdateDispenser(dispenser, now);
    }

    // Igc/dispenserigc.cpp:84-123: readiness is advanced by AdvanceFlightMounts;
    // only chaff ignores an active ripcord. Equality at timeLoaded cannot fire.
    private void UpdateDispenser(MountedPart part, double now)
    {
        if (StationId is not null || ParentShip is not null || Sector is null || !_parts.Contains(part) ||
            part.MountId != 0 || part.MountedFraction < 1 || part.Amount <= 0 || now <= part.TimeLoaded ||
            DispenserLoadTime(part) is not { } loadTime) return;
        bool chaff = part.EquipmentType == EquipmentType.ChaffLauncher;
        int mask = chaff ? ShipControlState.ChaffFire : ShipControlState.MineFire;
        if ((StateM & mask) == 0 || (!chaff && RipcordModel is not null)) return;
        part.TimeLoaded = now + loadTime; // arm before callback, including reentrant callers
        Mission?.Site.FireExpendable(this, part, now);
    }

    public sealed record ExpendableDeployment(Model Model, IReadOnlyList<Missile> Spoofed);

    /// <summary>Authoritative FireExpendable callback body. Explicit wire requests
    /// use the same operation after their existing authority checks; local held
    /// input goes through UpdateDispenser's mount/load/ripcord predicates.</summary>
    public ExpendableDeployment? FireDispenser(MountedPart part, double now, Random? random = null)
    {
        if (Mission is not { } mission || Sector is not { } sector || StationId is not null || ParentShip is not null ||
            !_parts.Contains(part) || part.MountId != 0 || part.Amount <= 0) return null;
        Model model;
        IReadOnlyList<Missile> spoofed = [];
        // FedSrv/FedSrv.CPP:5646-5774. A stationary layer drops behind its hull;
        // a moving layer drops against its velocity. Raw Deploy* helpers retain
        // their explicit position-at-ship API for authored/test placements.
        Vector3 DropPosition(float radius)
        {
            float offset = radius + .5f * HullType.Length + 5;
            return Position + (Velocity.LengthSquared() < 1
                ? Orientation.GetBackward() * offset : Velocity * (-offset / Velocity.Length()));
        }
        switch (part.Type.Type)
        {
            case DataPartTypeIGC.ChaffLauncher p when mission.Core.ChaffTypes.TryGetValue(p.ExpendableTypeId, out var t):
            {
                // FedSrv/FedSrv.CPP:5656-5708; hullTypeIGC.cpp:83-86.
                var result = DeployChaff(t, now, random, useHullEmission: true);
                model = result.Chaff; spoofed = result.Spoofed; break;
            }
            case DataPartTypeIGC.MineLauncher p when mission.Core.MineTypes.TryGetValue(p.ExpendableTypeId, out var t):
            {
                var mine = new Mine(mission.NextObjectId(), sector, t, DropPosition(t.Radius), LastUpdate + 3, this);
                mine.InitClock(now); sector.Add(mine); model = mine;
                mission.Site.MineCreatedEvent(mine); break;
            }
            case DataPartTypeIGC.ProbeLauncher p when mission.Core.ProbeTypes.TryGetValue(p.ExpendableTypeId, out var t):
            {
                var position = DropPosition(t.Radius);
                // FedSrv/FedSrv.CPP:4668-4700: first overlapping station, then
                // warp; zero separation is intentionally unchanged; no rocks.
                bool Displace(Vector3 center, float radius)
                {
                    var delta = position - center;
                    float length2 = delta.LengthSquared(), total = radius + t.Radius;
                    if (length2 == 0 || length2 >= total * total) return false;
                    position = center + delta * (total / MathF.Sqrt(length2)); return true;
                }
                bool displaced = sector.StationRuntimes.Any(s => Displace(s.Center, s.Radius));
                if (!displaced)
                    foreach (var warp in sector.Warps)
                        if (Displace(new(warp.Warp.Position[0], warp.Warp.Position[1], warp.Warp.Position[2]), warp.Warp.Radius)) break;
                var probe = new Probe(mission.NextObjectId(), sector, t, position, LastUpdate, SideId, this, createdAt: now)
                    { Target = (t.EabmCapabilities & 0x1000) != 0 ? CommandTarget : null };
                sector.Add(probe); model = probe;
                mission.Site.ProbeCreatedEvent(probe); break;
            }
            default: return null;
        }
        part.TimeLoaded = now + DispenserLoadTime(part)!.Value;
        SetPartAmount(part, (short)(part.Amount - 1)); // FedSrv/FedSrv.CPP:5778-5780
        return new(model, spoofed);
    }
}
