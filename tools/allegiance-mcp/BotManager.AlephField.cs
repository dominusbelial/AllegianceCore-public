using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    // P877 — aleph-field duty. Human tactic (bot policy), not native AI: native
    // wingmen only drop a mine at any enemy within 2500 u (Igc/shipIGC.cpp:
    // 2651-2665) and never fly to an aleph to pre-lay a field. Only a fraction
    // of combat bots carry the duty (one in four combat slots, BotManager.cs
    // spawn branch) and the duty is DORMANT unless the bot actually mounted a
    // field-grade mine (lifespan ≥ Showcase.PreLaidMineMinLifespan — on the
    // real PCore001 corpus exactly the 600 s Caltrop, launcher part 830, which
    // only a side whose tech set contains its launcher bits can buy; the
    // showcase's civ3/civ25 plans never unlock it, so there is nothing for the
    // strategy to research on this core — the duty lights up for field-grade
    // sides like the Rixians, whose base techs carry the caltrop bits).
    // Server behaviour is untouched.

    /// <summary>Re-resolve the field warp at most this often (seconds): the
    /// enemy's arrival aleph only moves when the map graph itself changes.</summary>
    private const double AlephFieldResolveInterval = 10.0;

    /// <summary>Stand-off distance from the field point (units) at which the
    /// duty bot stops approaching and holds — the dispenser drop law
    /// (BotManager.Dispensers, <see cref="AlephFieldDropRange"/>) does the
    /// laying while it drifts across the point.</summary>
    private const float AlephFieldHoldDistance = 120f;

    /// <summary>P877 — drive an aleph-field duty bot: fly to the enemy's
    /// arrival aleph in its rendezvous sector (or a home-station aleph under
    /// threat when it has no rendezvous), hold at the
    /// <see cref="Showcase.PlaceInFrontOfArrivalAleph"/> standoff point and let
    /// <see cref="MaybeUseDispensers"/> lay the field. Returns false (normal
    /// drives continue) when the bot is not on duty, has no field-grade mine
    /// mounted, yields to a visible enemy while still outside drop range, or
    /// has not reached the field sector yet (normal transit machinery owns the
    /// trip).</summary>
    private bool TryDriveAlephField(ActiveBot bot, ConnectionSession cs, double now, short cur)
    {
        if (!bot.AlephFieldDuty || cur < 0) return false;
        var core = Catalog.RepresentativeCore();
        // Dormant duty: no dispenser, or nothing field-grade mounted.
        if (bot.MineDispenserPartId is not { } minePart || !Showcase.IsFieldGradeMine(core, minePart))
            return false;
        if (!ResolveAlephFieldTarget(bot, now)) return false;
        if (bot.AlephFieldSector != cur) return false;      // normal transit owns the trip
        // Yield to a live engagement while approaching: a field layer is not a
        // kamikaze. Once inside drop range the station-keeping law holds the
        // line and MaybeUseDispensers keeps laying (escape mining protects it).
        short? enemy = VisibleEnemyInSector(bot, cs, cur);
        if (!TryCsPose(cs, bot.ShipId, out var pos, out var _)) return false;
        if (bot.AlephFieldWarpId >= 0
            && Probe.World.Warps.TryGetValue(bot.AlephFieldWarpId, out var warp)
            && warp.Position is { Length: 3 } wp)
        {
            Vector3 alephPos = Vec3(wp);
            Vector3 fieldPoint = Showcase.PlaceInFrontOfArrivalAleph(alephPos, AlephFieldSectorCenter(bot.AlephFieldSector), Showcase.AlephFieldOffset);
            float dist = (fieldPoint - pos).Length();
            if (enemy is not null && dist > AlephFieldDropRange) return false;
            if (dist <= AlephFieldHoldDistance)
            {
                LoiterCombat(bot, now);
                return true;
            }
            CombatFlyTo(bot, fieldPoint, AlephFieldHoldDistance, 200f, 0);
            return true;
        }
        return false;
    }

    /// <summary>P877 — resolve (and cache on the bot) the warp whose mouth the
    /// duty bot mines: the enemy's arrival aleph in the bot's rendezvous
    /// sector, else a home-station aleph under threat. Re-tried on the
    /// <see cref="AlephFieldResolveInterval"/> pacing until something resolves.
    /// Returns true when <c>bot.AlephFieldWarpId</c> is set.</summary>
    private bool ResolveAlephFieldTarget(ActiveBot bot, double now)
    {
        if (bot.AlephFieldWarpId >= 0) return true;
        if (now - bot.AlephFieldResolvedAt < AlephFieldResolveInterval) return false;
        bot.AlephFieldResolvedAt = now;

        short ownSide = bot.Side;
        short? enemySide = FirstEnemySide(ownSide);
        short? ownHome = FindSideHomeSector(ownSide);

        // 1. The rendezvous: the enemy fleet's arrival aleph in the bot's own
        //    objective sector (P591d resolution over the probe's real world).
        if (enemySide is { } es && ownHome is { } home && bot.TargetSector is { } rz
            && FindEnemyArrivalWarp(rz, es, home) is { } arrival
            && WarpPos(arrival.WarpId) is { } pos)
        {
            bot.AlephFieldWarpId = arrival.WarpId;
            bot.AlephFieldSector = rz;
            Console.WriteLine($"[p877] aleph-field duty bot {bot.Name} side={ownSide} → enemy arrival warp {arrival.WarpId} (from sector {arrival.PenultimateSector}) in rendezvous {rz}");
            return true;
        }
        // 2. A home-station aleph under threat: enemy pressure in the home
        //    sector itself or in a sector directly adjacent to it.
        if (ownHome is { } h && enemySide is { } es2 && HomeAlephUnderThreat(h)
            && WarpIn(h, es2) is { } hw && hw > 0 && WarpPos(hw) is { } hpos)
        {
            bot.AlephFieldWarpId = hw;
            bot.AlephFieldSector = h;
            Console.WriteLine($"[p877] aleph-field duty bot {bot.Name} side={ownSide} → threatened home aleph {hw} in sector {h}");
            return true;
        }
        return false;
    }

    /// <summary>The observed position of a probe-world warp, or null.</summary>
    private Vector3? WarpPos(short warpId)
        => Probe.World.Warps.TryGetValue(warpId, out var w) && w.Position is { Length: 3 } p
            ? Vec3(p) : null;

    /// <summary>A warp in <paramref name="sector"/> — prefer one whose partner
    /// sits in the enemy's home sector when the enemy side is known, else the
    /// first outbound (cross-sector) warp. 0 when none resolves.</summary>
    private short WarpIn(short sector, short? enemySide)
    {
        short firstOutbound = 0, towardEnemy = 0;
        short? enemyHome = enemySide is { } es ? FindSideHomeSector(es) : null;
        foreach (var w in Probe.World.Warps.Values)
        {
            if (w.ClusterId != sector) continue;
            if (!Probe.World.Warps.TryGetValue(w.DestinationId, out var partner)) continue;
            if (partner.ClusterId == sector) continue;      // same-sector pairing is not an arrival
            if (firstOutbound == 0) firstOutbound = w.WarpId;
            if (enemyHome is { } eh && eh == partner.ClusterId) towardEnemy = w.WarpId;
        }
        return towardEnemy != 0 ? towardEnemy : firstOutbound;
    }

    /// <summary>P877 — whether a home sector is "under threat": a visible enemy
    /// combat ship in the home sector itself, or in any sector directly
    /// adjacent to it (the pressure a field is meant to blunt).</summary>
    private bool HomeAlephUnderThreat(short homeSector)
    {
        foreach (var s in Probe.World.Ships.Values)
        {
            if (s.SideId < 0 || s.SideId == ProbeSide || s.IsDead || s.Latest is null) continue;
            if (CombatTargetSelector.ClassifyTargetId(s.HullId) == BotTargetClass.MinerDrone) continue;
            if (s.SectorId == homeSector) return true;
        }
        if (Probe.World.Graph is not { } graph) return false;
        foreach (var edge in graph.Warps)
        {
            short other = edge.SourceClusterId == homeSector ? edge.DestClusterId
                : edge.DestClusterId == homeSector ? edge.SourceClusterId : (short)-1;
            if (other < 0) continue;
            foreach (var s in Probe.World.Ships.Values)
            {
                if (s.SideId < 0 || s.SideId == ProbeSide || s.IsDead || s.Latest is null) continue;
                if (s.SectorId != other) continue;
                if (CombatTargetSelector.ClassifyTargetId(s.HullId) == BotTargetClass.MinerDrone) continue;
                return true;
            }
        }
        return false;
    }

    /// <summary>The first side observed that is not <paramref name="ownSide"/>
    /// (stations first — they persist — then ships), or null when no other side
    /// is observed yet.</summary>
    private short? FirstEnemySide(short ownSide)
    {
        lock (Probe.Gate)
        {
            foreach (var st in Probe.World.Stations.Values)
                if (st.SideId >= 0 && st.SideId != ownSide) return st.SideId;
            foreach (var s in Probe.World.Ships.Values)
                if (s.SideId >= 0 && s.SideId != ownSide) return s.SideId;
        }
        return null;
    }

    /// <summary>P877 — the sector-centre proxy for
    /// <see cref="Showcase.PlaceInFrontOfArrivalAleph"/>: the centroid of the
    /// sector's observed warp and station positions (the aleph→centre direction
    /// only needs to point INTO the sector). Falls back to the origin.</summary>
    private Vector3 AlephFieldSectorCenter(short sector)
    {
        float x = 0, y = 0, z = 0; int n = 0;
        lock (Probe.Gate)
        {
            foreach (var w in Probe.World.Warps.Values)
            {
                if (w.ClusterId != sector || w.Position is not { Length: 3 } p) continue;
                x += p[0]; y += p[1]; z += p[2]; n++;
            }
            foreach (var st in Probe.World.Stations.Values)
            {
                if (st.ClusterId != sector || st.Position is not { Length: 3 } p) continue;
                x += p[0]; y += p[1]; z += p[2]; n++;
            }
        }
        return n == 0 ? Vector3.Zero : new Vector3(x / n, y / n, z / n);
    }
}
