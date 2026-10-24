using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private static int CloakApproach(ActiveBot bot, ConnectionSession cs, int state, bool approaching = true)
    {
        var core = Catalog.RepresentativeCore();
        if (DecisionSnapshot.TryFind(SnapshotFor(cs).Ships, bot.ShipId, out var own)
            && core.HullTypes.TryGetValue(own.HullId, out var hull)
            && Showcase.ClassifyHull(hull) is not (Showcase.HullRole.Scout or Showcase.HullRole.Bomber))
        {
            bot.CloakHeld = false;
            return state & ~ShipControlState.CloakActive;
        }
        // P801 follow-up (rig 221 batch 23b: 13 of 15 lone wolves released uncloaked, 4 visible bombers
        // released cloaked). In the finale target sector the visible element stays visible — it draws the
        // defenders — and a lone wolf holds uncloaked so its energy is full, cloaking only once its wave
        // is committed. Transit to the target sector keeps the ordinary cloak policy.
        if (bot.FinaleBomberActive && bot.Finale is { } run && CsSector(cs) is var sector and >= 0)
            lock (run.Sync)
                if (run.Plans.TryGetValue(bot.Side, out var plan) && plan.KilledAt is null
                    && !FinaleCloakAllowed(sector == plan.Sector, bot.FinaleLoneWolf,
                        plan.GoTime is not null && plan.GoMembers.Contains(bot.ShipId)))
                    approaching = false;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me)) return state & ~ShipControlState.CloakActive;
            int result = CloakPolicy.Apply(core, me.HullId, me.Loadout,
                me.Latest?.EnergyFraction ?? 0, !me.IsDead && me.StationId < 0 && me.ParentId < 0 && me.SectorId >= 0,
                cs.World.RipcordActive, approaching, bot.CloakHeld, state);
            bool held = (result & ShipControlState.CloakActive) != 0;
            if (held != bot.CloakHeld)
                Console.WriteLine($"[cloak-policy] {bot.Name} ship={bot.ShipId} side={bot.Side} hull={me.HullId} sector={me.SectorId} target={bot.TargetId} cloak={held} energy={me.Latest?.EnergyFraction:F2} state=0x{result:X}");
            bot.CloakHeld = held;
            return result;
        }
    }
}
