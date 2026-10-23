using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // Placement distances and pacing below are bot policy, not native limits.
    // Native human fire: FedSrv/FedSrv.CPP:5624-5779; mount/load timing:
    // Igc/dispenserigc.cpp:89-123. Read the accepted inventory, never the request.
    private static void MaybeUseDispensers(ActiveBot bot, ConnectionSession cs, double now)
    {
        var core = Catalog.RepresentativeCore();
        lock (cs.Gate)
        {
            var world = cs.World;
            if (!world.InGame || world.MyShipId != bot.ShipId
                || !world.Ships.TryGetValue(bot.ShipId, out var me) || me.StationId >= 0
                || me.ParentId >= 0 || me.Latest is not { } pose || Catalog.IsLifepodHull(me.HullId)) return;
            var position = pose.Position;
            short sector = me.SectorId;
            if (sector < 0) return;
            DataPartTypeIGC? Mounted(EquipmentType equipment)
            {
                foreach (var part in me.Loadout ?? [])
                {
                    if (part.MountId != 0 || part.Amount <= 0 || part.FractionMounted < ExpandedPartData.FullyMounted) continue;
                    DataPartTypeIGC type;
                    if (!core.PartTypes.TryGetValue(part.PartId, out type)
                        && !core.TryBuildExpendableLauncher(part.PartId, out type)) continue;
                    if (type.EquipmentType == equipment) return type;
                }
                return null;
            }

            // The incoming warning means an actual in-sector missile targets us,
            // including a zero-lock missile (WinTrek/trekigc.cpp:5028-5065).
            // Our own outgoing MissileLock is deliberately irrelevant.
            if (world.MissileTrack.IsTracked && Mounted(EquipmentType.ChaffLauncher) is { } counter
                && counter.Type is DataPartTypeIGC.ChaffLauncher c && core.ChaffTypes.TryGetValue(c.ExpendableTypeId, out var chaff)
                && now - bot.LastChaffAt >= Math.Max(.5, chaff.LoadTime))
            {
                cs.SendFireExpendable(EquipmentType.ChaffLauncher);
                bot.LastChaffAt = now;
                Console.WriteLine($"[sc4] chaff bot={bot.Name} side={bot.Side} sector={sector} part={counter.PartId} tracked=true lock={world.MissileTrack.BestLock:F3}");
            }

            if (Mounted(EquipmentType.Dispenser) is not { } dispenser) return;

            // P779b-1 — a rescue-capable probe launcher (c_eabmRescue /
            // c_eabmRescueAny, Igc/igc.h:781-782) is never a scouting probe; the
            // medevac scout drops it at its own position when the forward-rescue
            // gate holds (a design choice — native has no AI for this).
            if (dispenser.Type is DataPartTypeIGC.ProbeLauncher rescueLauncher
                && core.ProbeTypes.TryGetValue(rescueLauncher.ExpendableTypeId, out var rescueProbe)
                && (rescueProbe.EabmCapabilities & 0x6000) != 0)
            {
                MaybeDropMedevacProbe(bot, cs, now, sector, position, rescueProbe);
                return;
            }
            bool home = world.Stations.Values.Any(s => s.SideId == bot.Side && s.ClusterId == sector);
            bool nearAleph = world.Warps.Values.Any(w => w.ClusterId == sector
                && (Vec3(w.Position) - position).LengthSquared() < Showcase.WingmanMineRange * Showcase.WingmanMineRange);
            bool enemy = world.Ships.Values.Any(s => s.ShipId != bot.ShipId && s.SideId >= 0 && s.SideId != bot.Side
                && !s.IsNotVisible && s.SectorId == sector && s.Latest is not null
                && CombatTargetSelector.ClassifyTargetId(s.HullId) != BotTargetClass.MinerDrone);

            if (dispenser.Type is DataPartTypeIGC.ProbeLauncher p && core.ProbeTypes.TryGetValue(p.ExpendableTypeId, out var probe)
                && bot.Role.Equals("scout", StringComparison.OrdinalIgnoreCase) && probe.ScannerRange > 0 && probe.Lifespan > 0
                && (probe.EabmCapabilities & 0x6000) == 0 // rescue devices are not scouting probes (Igc/igc.h:781-782)
                && now - bot.LastProbeAt >= Math.Max(8, probe.LoadTime)
                && (!home || enemy) && (nearAleph || sector == bot.TargetSector || enemy))
            {
                float spacing = Math.Min(1500, probe.ScannerRange * .15f);
                // Entries disappear on native destruction/expiry, so replacements
                // follow actual probe lifetimes (Igc/probeigc.cpp:133,335-338).
                bool covered = world.Probes.Values.Any(p => p.SideId == bot.Side && p.ClusterId == sector
                    && core.ProbeTypes.TryGetValue(p.ProbetypeId, out var type) && type.ScannerRange > 0
                    && (type.EabmCapabilities & 0x6000) == 0
                    && (p.Position - position).LengthSquared() < spacing * spacing);
                if (!covered)
                {
                    cs.SendFireExpendable(EquipmentType.Dispenser);
                    bot.LastProbeAt = now;
                    Console.WriteLine($"[sc4] probe bot={bot.Name} side={bot.Side} sector={sector} part={dispenser.PartId} contested=true aleph={nearAleph} lifespan={probe.Lifespan}");
                }
            }
            else if (dispenser.Type is DataPartTypeIGC.MineLauncher m && core.MineTypes.TryGetValue(m.ExpendableTypeId, out var mine)
                && Showcase.UsesMines(bot.WingmanBehaviour) && now - bot.LastMineAt >= Math.Max(3, mine.LoadTime))
            {
                // Retain the native wingman proximity trigger, plus deliberate
                // human minelaying at an owned station sector's entry/exit aleph.
                // Human fire has no enemy-range gate (FedSrv/FedSrv.CPP:5624-5779).
                bool enemyNear = enemy && world.Ships.Values.Any(s => s.SideId >= 0 && s.SideId != bot.Side
                    && !s.IsNotVisible && s.SectorId == sector && s.Latest is { } target
                    && CombatTargetSelector.ClassifyTargetId(s.HullId) != BotTargetClass.MinerDrone
                    && Showcase.EnemyWithinMineRange((target.Position - position).Length()));
                bool defendedAleph = Showcase.PreLayMineAtAleph(home, nearAleph, mine.Lifespan);
                float spacing = Math.Max(100, mine.MineRadius * 2);
                bool covered = world.Mines.Values.Any(m => m.SideId == bot.Side && m.ClusterId == sector
                    && (m.Position - position).LengthSquared() < spacing * spacing);
                if ((defendedAleph || enemyNear) && !covered)
                {
                    cs.SendFireExpendable(EquipmentType.Dispenser);
                    bot.LastMineAt = now;
                    Console.WriteLine($"[sc4] mine bot={bot.Name} side={bot.Side} sector={sector} part={dispenser.PartId} defendedAleph={defendedAleph} enemyWithin2500={enemyNear} lifespan={mine.Lifespan}");
                }
            }
        }
    }
}
