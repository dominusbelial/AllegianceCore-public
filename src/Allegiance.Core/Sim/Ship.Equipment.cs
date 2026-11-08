using Allegiance.Core.Collision;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Sim;

public sealed partial class Ship
{
    /// <summary>
    /// P956 — the port of native <c>CreateDrone</c>'s preferred-loadout fill
    /// (<c>Igc/common.cpp:2462-2480</c>): every drone is created with its
    /// hull's <c>GetPreferredPartTypes()</c> mounted, ignoring price and
    /// availability ("included in the cost of the drone").
    /// <list type="number">
    /// <item><b>Order</b> — the hull's preferred-part list is walked in
    /// record order; <c>NA</c> (-1) terminates it (<see
    /// cref="DataHullTypeIGC.PreferredPartsTypes"/>,
    /// <c>c_cMaxPreferredPartTypes = 14</c>, <c>Igc/igc.h</c>).</item>
    /// <item><b>Slot rule</b> — a weapon (<c>ET_Weapon</c>) fills up to
    /// <c>GetMaxWeapons()</c> sockets, every other equipment type only mount 0
    /// (<c>Igc/common.cpp:2466-2470</c>).</item>
    /// <item><b>Free-slot gate</b> — mount only where
    /// <c>GetMountedPart(et, i) == NULL</c> and <c>pht-&gt;CanMount(ppt, i)</c>
    /// (<c>Igc/common.cpp:2478</c>; <c>Igc/shipIGC.cpp:1551-1555</c>,
    /// <c>Igc/hullTypeIGC.h:199-213</c>).</item>
    /// <item><b>Amount</b> — <c>0x7fff</c> = "as much as fits": the port's
    /// <see cref="MountPart"/> clamps pack/launcher quantities exactly like
    /// native <c>SetAmount</c> (<c>packigc.h:102-123</c>,
    /// <c>launchertypeigc.h:130-145</c>) and leaves single-item equipment
    /// untouched.</item>
    /// <item><b>Readiness</b> — the spawned drone is the native post-undock
    /// state: every native spawn docks the new drone and immediately undocks it
    /// (<c>FedSrv.CPP:11354-11356</c>, <c>:11528-11529</c>), and the undock runs
    /// <c>RepairAndRefuel</c>, which fully mounts every part and fills the shield
    /// (<c>Igc/stationIGC.cpp:453-461,468-472</c>; call at
    /// <c>FedSrv.CPP:13189</c>). Parts are therefore mounted at full fraction,
    /// not the 0 that <c>CreateAndAddPart</c> leaves them at before that launch
    /// repair (<c>Igc/shipIGC.cpp:1567</c>).</item>
    /// </list>
    /// No successor resolution and no station <c>CanBuy</c> check — those belong
    /// to the dock upgrade path (<c>FedSrv.CPP:11583-11602</c>), not to drone
    /// creation. Returns the number of parts actually mounted.
    /// </summary>
    public int MountPreferredParts()
    {
        if (Mission?.Core is not { } core) return 0;
        var hull = _hull;                                   // ship->GetBaseHullType()
        int mounted = 0;
        // One loadout publication for the whole fill, like native's queued
        // per-purchase loadout (FedSrv/fsship.cpp:708-735).
        using (BeginLoadoutUpdate())
        {
            foreach (var rawPartId in hull.PreferredPartsTypes)
            {
                if (rawPartId == -1) break;                 // NA terminates the list
                if (!core.PartTypes.TryGetValue(rawPartId, out var ppt)
                    && !core.TryBuildExpendableLauncher(rawPartId, out ppt)) continue;
                var et = ppt.EquipmentType;
                int mountMax = et == EquipmentType.Weapon ? hull.MaxWeapons : 1;
                for (int i = 0; i < mountMax; i++)
                {
                    // GetMountedPart(et, i) == NULL && pht->CanMount(ppt, i)
                    if (_parts.Any(p => p.EquipmentType == et && p.MountId == i)) continue;
                    if (!hull.CanMountPart(ppt, i)) continue;
                    // CreateAndAddPart(ppt, i, 0x7fff) — amount clamps inside;
                    // full mount = the launch RepairAndRefuel state.
                    if (MountPart(ppt, 0x7fff, (byte)i, ExpandedPartData.FullyMounted) is not null) mounted++;
                }
            }
        }
        return mounted;
    }

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
                // CmineTypeIGC::GetRadius returns the derived DataMineTypeIGC
                // radius (Igc/minetypeigc.h:104-106), not the unused base field.
                var mine = new Mine(mission.NextObjectId(), sector, t, DropPosition(t.MineRadius), LastUpdate + 3, this);
                mine.InitClock(now); sector.Add(mine); model = mine;
                mission.Skills?.Record(SideId, Allegiance.Core.Model.ShowcaseSkill.Mines);
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
                mission.Skills?.Record(SideId, Allegiance.Core.Model.ShowcaseSkill.Probes);
                mission.Site.ProbeCreatedEvent(probe); break;
            }
            default: return null;
        }
        part.TimeLoaded = now + DispenserLoadTime(part)!.Value;
        short remaining = (short)(part.Amount - 1);
        SetPartAmount(part, remaining); // FedSrv/FedSrv.CPP:5778-5780
        // P787a — native's fire-emptied expendable reload (clintlib.cpp:3382-3422):
        // a chaff/mine/probe fired the launcher to exactly zero → auto-reload.
        if (remaining == 0) TryAutoReloadLauncher(now, part.EquipmentType);
        return new(model, spoofed);
    }
}
