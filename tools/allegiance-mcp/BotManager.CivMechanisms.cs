using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Strategy;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // Apply P707's existing movement pulse to ON flight goals as well as
    // close-band attacks. A rearward dodge/avoidance goal can otherwise brake
    // indefinitely before the attack controller gets another turn.
    private static bool CivFlightLiveness(ActiveBot bot, ConnectionSession cs, ref int state)
    {
        lock (cs.Gate)
        {
            if (!CivExecutionPlan.TryRead(cs.World.ResearchBriefing?.Plan, out _, out _)) return false;
            double now = bot.LastAiDecision;
            if (bot.TargetId is null || bot.IsPod || !double.IsFinite(now)
                || !cs.World.Ships.TryGetValue(bot.ShipId, out var own) || own.IsDead
                || own.StationId >= 0 || own.ParentId >= 0 || own.Latest is not { } pose) return true;
            if (pose.Velocity.LengthSquared() < IdleSpeedMps * IdleSpeedMps
                && now - bot.LastMoveAt >= CombatStationarySeconds)
                bot.CombatEscapeUntil = now + CombatEscapeHoldSeconds;
            if (now < bot.CombatEscapeUntil)
                state = (state & ~(ShipControlState.LeftButton | ShipControlState.RightButton))
                    | ((bot.ShipId & 1) == 0 ? ShipControlState.LeftButton : ShipControlState.RightButton);
            return true;
        }
    }

    private static void ApplyCivCoverageDuty(ActiveBot bot, string? duty)
    {
        if (duty is null)
        {
            if (bot.CivOriginalRole is { } original)
            {
                if (!bot.FinaleBomberActive && !bot.FinaleBomberReserved) bot.Role = original;
                bot.DefensiveMineDuty = bot.CivOriginalMine; bot.AlephFieldDuty = bot.CivOriginalAleph;
            }
            bot.CivOriginalRole = null; bot.CivCoverageDuty = null;
            return;
        }
        if (bot.CivOriginalRole is null)
        {
            bot.CivOriginalRole = bot.Role; bot.CivOriginalMine = bot.DefensiveMineDuty; bot.CivOriginalAleph = bot.AlephFieldDuty;
        }
        bot.CivCoverageDuty = duty;
        bot.CivCoverageFlew = false; bot.CivCrewPurchaseDeferred = false; bot.CivCrewDockLaunchPending = false;
        bot.Role = duty switch
        {
            "repair" => "support", "mines" or "probes" or "rescue" => "Scout", "turret" => "Heavy",
            "boarding" => "boarder", _ => bot.CivOriginalRole,
        };
        bot.DefensiveMineDuty = duty == "mines";
        bot.AlephFieldDuty = duty == "mines";
    }

    private static GunnerPolicy.Tuning GunnerTuneFor(ActiveBot bot)
    {
        var standard = GunnerPolicy.Effective;
        return !standard.Enable && bot.CivCoverageDuty is "turret" or "gunner" && CivCoverage.CrewAllowed()
            ? standard with { Enable = true, GatherSeconds = 8, BoardParkSeconds = 3, MaxGunnersPerCarrier = 1 } : standard;
    }

    // Coverage never creates an anchor or reads server topology/intelligence.
    // The ordinary native request owns legality, danger, countdown and completion.
    private static void MaybeCivRipcord(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (bot.CivCoverageDuty != "ripcord" || bot.FinaleBomberActive || bot.FinaleBomberReserved || bot.IsPod
            || bot.OrderedCommand is not null || !CivExecutionPlan.TryRead(cs.World.ResearchBriefing?.Plan, out _, out _)) return;
        lock (cs.Gate)
        {
            var w = cs.World;
            if (!w.Ships.TryGetValue(bot.ShipId, out var me) || me.StationId >= 0 || me.ParentId >= 0
                || me.Latest is not { } pose || IsObjectiveCargo(cs, me.CarriedFlagSideId)) return;
            if (bot.CivRipcordFrom >= 0 && me.SectorId != bot.CivRipcordFrom)
            { bot.CivCoverageDuty = null; bot.CivRipcordFrom = -1; return; }
            if (w.RipcordActive || now - bot.CivRipcordAt < 30) return;
            if (w.Ships.Values.Any(s => s.SideId >= 0 && s.SideId != bot.Side && !s.IsDead && !s.IsNotVisible
                && s.SectorId == me.SectorId && s.Latest is not null)) return;
            var home = w.Stations.Values.Where(s => s.SideId == bot.Side && s.ClusterId != me.SectorId)
                .OrderBy(s => s.StationId).FirstOrDefault();
            if (home.Position is null) return;
            cs.SendRipcordRequest(home.ClusterId);
            bot.CivRipcordAt = now; bot.CivRipcordFrom = me.SectorId;
            bot.TargetSector = home.ClusterId; bot.TargetId = null;
            // Keep flying to a real friendly objective during the native spin.
            Console.WriteLine($"[civ-mechanism] cookie={w.Mission?.Cookie} side={bot.Side} pilot={bot.ShipId} stage=requested mechanism=ripcord sector={home.ClusterId}");
        }
    }
}
