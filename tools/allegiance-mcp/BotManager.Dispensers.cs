using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // Placement distances and pacing below are bot policy, not native limits.
    // Native human fire: FedSrv/FedSrv.CPP:5624-5779; mount/load timing:
    // Igc/dispenserigc.cpp:89-123. Read the accepted inventory, never the request.
    // P877 — mine drops are deliberate human tactics: a PURSUED bot mines its
    // wake (Showcase.ShouldEscapeMine) and duty bots lay long-lived fields at
    // arrival alephs (Showcase.PreLayMineAtArrivalAleph). The native wingman
    // undirected drop (any enemy within 2500 u, shipIGC.cpp:2651-2665) is NOT
    // used as a bot trigger anymore — an enemy merely AHEAD gets no mines;
    // the pure rule stays in Showcase.EnemyWithinMineRange for NPC parity.
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
                WeaponCoverage.Global.RecordDispenserDrop(bot.ShipId, counter.PartId);   // P796
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
                MaybeDropMedevacProbe(bot, cs, now, sector, position, rescueProbe, dispenser.PartId);
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
                    WeaponCoverage.Global.RecordDispenserDrop(bot.ShipId, dispenser.PartId);   // P796
                    Console.WriteLine($"[sc4] probe bot={bot.Name} side={bot.Side} sector={sector} part={dispenser.PartId} contested=true aleph={nearAleph} lifespan={probe.Lifespan}");
                }
            }
            else if (dispenser.Type is DataPartTypeIGC.MineLauncher m && core.MineTypes.TryGetValue(m.ExpendableTypeId, out var mine)
                && Showcase.UsesMines(bot.WingmanBehaviour) && now - bot.LastMineAt >= Math.Max(3, mine.LoadTime))
            {
                // P877 — deliberate human minelaying only:
                //   1. escape mining: a PURSUER behind us, closing/holding,
                //      while we are fleeing/low/outmatched — drop in the wake
                //      (a dispenser drops at the ship's position, so moving
                //      forward leaves it on the pursuer's line);
                //   2. an own-station aleph (existing defended-aleph rule); or
                //   3. a duty bot's arrival-aleph field (long-lived mines only).
                // An enemy merely AHEAD no longer triggers a drop (the native
                // undirected wingman rule wastes mines on enemies we are
                // flying toward; it stays available as pure parity code).
                // Human fire has no enemy-range gate (FedSrv/FedSrv.CPP:5624-5779).
                (short PursuerId, float Distance, float Opening)? pursuit = FindPursuer(bot, world, position,
                    pose.Velocity, pose.Orientation, sector);
                bool escape = pursuit is not null && Showcase.ShouldEscapeMine(
                    pursuerBehind: true, pursuit.Value.Distance, pursuit.Value.Opening,
                    fleeing: now < bot.CombatEscapeUntil,
                    lowHull: pose.HullFraction <= Showcase.EscapeMineLowHullFraction,
                    lowShield: pose.ShieldFraction <= Showcase.EscapeMineLowShieldFraction,
                    outmatched: OutmatchedInSector(bot, world, sector));
                bool defendedAleph = Showcase.PreLayMineAtAleph(home, nearAleph, mine.Lifespan);
                bool arrivalField = bot.AlephFieldDuty && bot.AlephFieldWarpId >= 0
                    && bot.AlephFieldSector == sector
                    && world.Warps.TryGetValue(bot.AlephFieldWarpId, out var fieldWarp)
                    && fieldWarp.ClusterId == sector
                    && fieldWarp.Position is { Length: 3 } fp
                    && (Vec3(fp) - position).LengthSquared() < AlephFieldDropRange * AlephFieldDropRange
                    && Showcase.PreLayMineAtArrivalAleph(atArrivalAleph: true,
                        threatenedOrContested: true, mine.Lifespan);
                float spacing = Math.Max(100, mine.MineRadius * 2);
                bool covered = world.Mines.Values.Any(m => m.SideId == bot.Side && m.ClusterId == sector
                    && (m.Position - position).LengthSquared() < spacing * spacing);
                if ((defendedAleph || escape || arrivalField) && !covered)
                {
                    cs.SendFireExpendable(EquipmentType.Dispenser);
                    bot.LastMineAt = now;
                    WeaponCoverage.Global.RecordDispenserDrop(bot.ShipId, dispenser.PartId);   // P796
                    if (escape) WeaponCoverage.Global.RecordEscapeMineDrop(bot.ShipId,
                        pursuit!.Value.PursuerId, dispenser.PartId, m.ExpendableTypeId, mine.Lifespan);   // P877
                    else if (arrivalField) WeaponCoverage.Global.RecordAlephFieldDrop(bot.ShipId,
                        dispenser.PartId, m.ExpendableTypeId, mine.Lifespan, sector,
                        world.Ships.Values.Where(s => s.SideId >= 0 && s.SideId != bot.Side && !s.IsNotVisible
                            && s.SectorId == sector && !s.IsDead && s.Latest is not null
                            && CombatTargetSelector.ClassifyTargetId(s.HullId) != BotTargetClass.MinerDrone)
                            .Select(s => s.ShipId).ToList());   // P877
                    Console.WriteLine($"[sc4] mine bot={bot.Name} side={bot.Side} sector={sector} part={dispenser.PartId} defendedAleph={defendedAleph} escape={escape} pursuer={(escape ? pursuit!.Value.PursuerId : (short)-1)} alephField={arrivalField} warp={(arrivalField ? bot.AlephFieldWarpId : -1)} lifespan={mine.Lifespan}");
                }
            }
        }
    }

    /// <summary>P877 — the nearest enemy COMBAT actor behind us within the
    /// mine-useful range that is closing or holding (opening speed below the
    /// tolerance). null when nobody qualifies. Pure inputs; bot-policy rule
    /// (not native AI — native wingmen drop at ANY enemy within 2500 u,
    /// shipIGC.cpp:2651-2665).</summary>
    private static (short PursuerId, float Distance, float Opening)? FindPursuer(ActiveBot bot,
        ClientWorld world, Vector3 position, Vector3 botVel, Orientation orient, short sector)
    {
        Vector3 forward = orient.GetForward();
        (short, float, float)? best = null;
        foreach (var s in world.Ships.Values)
        {
            if (s.ShipId == bot.ShipId || s.SideId < 0 || s.SideId == bot.Side
                || s.IsNotVisible || s.SectorId != sector || s.Latest is not { } target
                || s.IsDead || CombatTargetSelector.ClassifyTargetId(s.HullId) == BotTargetClass.MinerDrone)
                continue;
            Vector3 toPursuer = target.Position - position;
            float distance = toPursuer.Length();
            if (!Showcase.IsPursuitBehind(forward, toPursuer)
                || !Showcase.EnemyWithinMineRange(distance)) continue;
            float opening = Showcase.PursuitOpeningSpeed(target.Position, target.Velocity,
                position, botVel);
            if (opening >= Showcase.PursuitOpeningTolerance) continue;   // falling behind — a wake drop is wasted
            if (best is { } cur && distance >= cur.Item2) continue;
            best = (s.ShipId, distance, opening);
        }
        return best;
    }

    /// <summary>P877 — "outmatched": more enemy combat actors than the local
    /// side can field in this sector (the bot itself counts; drones/pods
    /// excluded on both sides). Bot policy.</summary>
    private static bool OutmatchedInSector(ActiveBot bot, ClientWorld world, short sector)
    {
        int enemies = 0, friends = 1;                       // the bot itself is a friendly
        foreach (var s in world.Ships.Values)
        {
            if (s.SideId < 0 || s.IsNotVisible || s.SectorId != sector || s.IsDead || s.Latest is null
                || CombatTargetSelector.ClassifyTargetId(s.HullId) == BotTargetClass.MinerDrone)
                continue;
            if (s.SideId == bot.Side) { if (s.ShipId != bot.ShipId) friends++; }
            else enemies++;
        }
        return enemies > friends;
    }

    /// <summary>P877 — how close to the assigned field warp a duty bot must be
    /// before its drops build the field (about twice the standoff offset —
    /// the bot flies to the point and drops while approaching it).</summary>
    private const float AlephFieldDropRange = 2 * Showcase.AlephFieldOffset;
}
