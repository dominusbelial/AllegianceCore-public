using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Collision;

namespace Allegiance.Tools.McpSpawner;

// P779b-1 — the MEDEVAC half of the rescue slice. The rescue itself is P780's
// server pass (a lifepod touching a c_eabmRescue/c_eabmRescueAny probe is
// rescued, Igc/shipIGC.cpp:896-909, ported at Sector.ResolveProbeRescueEvents).
// The pod TARGET rule is native parity (clintlib/appmsg.cpp:230-237): the eject
// autopilot goes to the nearest FRIENDLY station OR probe in ANY cluster whose
// capabilities include rescue/land. WHO carries and drops a rescue probe is NOT
// native AI — native has no AI for it (FedSrv's CFSDrone only launches/docks,
// fsship.h:577-621, fsship.cpp:1418-1460) — so this class's scout choice, drop
// gates and pacing are a SHOWCASE DESIGN choice, not parity.
internal sealed partial class BotManager
{
    /// <summary>P779b-1 — at most one medevac scout per side (design choice).</summary>
    internal const int MedevacScoutsPerSide = 1;

    /// <summary>P779b-1 — live own-side lifepods in the sector that justify a
    /// forward rescue probe (design threshold).</summary>
    internal const int MedevacMinOwnPods = 2;

    /// <summary>P779b-1 — own-side pods first observed in the sector during the
    /// last <see cref="MedevacRecentEjectSeconds"/> that justify one (design).</summary>
    internal const int MedevacMinRecentEjects = 3;

    /// <summary>P779b-1 — do not stack a rescue probe closer than this to an
    /// own-side one already in the sector (design spacing, metres).</summary>
    internal const float MedevacSpacing = 1500f;

    /// <summary>P779b-1 — the recent-eject observation window in seconds.</summary>
    internal const double MedevacRecentEjectSeconds = 60.0;

    /// <summary>P779b-1 — one bot's inputs to the per-side medevac pick. BotId is
    /// the bot's stable roster id (not its ship id, which the server re-allocates
    /// on a reseat), Role its showcase role, CanBuyRescueProbe whether its civ's
    /// observed techs allow a rescue-capable probe launcher
    /// (<see cref="TechLoadoutSelector.PickRescueProbeLauncher"/>).</summary>
    internal readonly record struct MedevacCandidate(int BotId, short Side, string Role, bool CanBuyRescueProbe);

    /// <summary>P779b-1 — the medevac scout assignment, PURE over the roster: for
    /// each side, the lowest <see cref="MedevacCandidate.BotId"/> among that
    /// side's scout-role bots whose civ can buy a rescue-capable probe launcher
    /// (at most <see cref="MedevacScoutsPerSide"/>). A side whose civ cannot
    /// (Ga'Taraan in PCore001) gets none. Stable for the same roster — a respawn
    /// that reuses the bot id cannot change the pick.</summary>
    internal static IReadOnlySet<int> PickMedevacScouts(IReadOnlyList<MedevacCandidate> bots)
    {
        var chosen = new HashSet<int>();
        foreach (short side in bots.Select(b => b.Side).Distinct().OrderBy(s => s))
            foreach (var candidate in bots
                         .Where(b => b.Side == side && IsScoutRole(b.Role) && b.CanBuyRescueProbe)
                         .OrderBy(b => b.BotId).Take(MedevacScoutsPerSide))
                chosen.Add(candidate.BotId);
        return chosen;
    }

    internal static bool IsScoutRole(string role)
        => role.Equals("scout", StringComparison.OrdinalIgnoreCase);

    /// <summary>P779b-1 — the medevac drop gate (design): all of (a) the sector
    /// holds no own-side station, (b) enough live own pods OR enough recently
    /// first-seen own pods, (c) no own-side rescue-capable probe within
    /// <see cref="MedevacSpacing"/>, (d) the launcher's load time / the 8 s floor
    /// has elapsed since the last rescue-probe drop.</summary>
    internal static bool ShouldDropMedevacProbe(bool ownStationInSector, int ownPodsInSector,
        int recentOwnEjects, bool ownRescueProbeNearby, double now, double lastRescueProbeAt, double loadTime)
        => !ownStationInSector
           && (ownPodsInSector >= MedevacMinOwnPods || recentOwnEjects >= MedevacMinRecentEjects)
           && !ownRescueProbeNearby
           && now - lastRescueProbeAt >= Math.Max(8, loadTime);

    /// <summary>P779b-1 — the current roster as medevac candidates (the stable
    /// bot id, side, role, and whether that bot's civ was OBSERVED able to buy a
    /// rescue launcher). Called under <c>_gate</c>.</summary>
    private List<MedevacCandidate> MedevacCandidates()
        => _bots.Where(b => !b.Inactive && !b.SpawnPending)
            .Select(b => new MedevacCandidate(b.BotId, b.Side, b.Role, b.CanBuyRescueProbe)).ToList();

    /// <summary>P779b-1 — recompute the per-side medevac flag from the roster.
    /// Cheap (N bots) and deterministic; called after spawn/removal and once per
    /// second so a removed medevac bot hands the job to the next scout.</summary>
    private void RefreshMedevacScouts()
    {
        List<MedevacCandidate> candidates;
        lock (_gate) candidates = MedevacCandidates();
        var chosen = PickMedevacScouts(candidates);
        lock (_gate)
            foreach (var bot in _bots)
                bot.MedevacScout = chosen.Contains(bot.BotId);
    }

    /// <summary>P779b-1 — log the "no medevac scout for this side" fact once per
    /// side, when a scout's real shop techs expose no buyable rescue launcher
    /// (Ga'Taraan today). A design showcase note, not a native rule.</summary>
    private void LogNoMedevacScout(short side, short civ)
    {
        lock (_medevacNoTechLogged)
        {
            if (!_medevacNoTechLogged.Add(side)) return;
            Console.WriteLine($"[p779b] no medevac scout for side {side}: civ {civ} has no buyable rescue-capable probe launcher");
        }
    }

    private readonly HashSet<short> _medevacNoTechLogged = new();

    /// <summary>P779b-1 — once-per-second roster refresh cadence (a removed
    /// medevac bot hands the job on; a scout's role change is picked up).</summary>
    private double _nextMedevacRefresh;

    /// <summary>P779b-1 — own-side lifepod observation in one sector: how many
    /// are flying there now, and how many were FIRST seen there within
    /// <see cref="MedevacRecentEjectSeconds"/>. The bot already sees every
    /// lifepod in <c>cs.World.Ships</c>; the first-seen stamp is the harness's
    /// eject signal (no native eject log reaches a client). Called under
    /// cs.Gate.</summary>
    private static (int Pods, int RecentEjects) ObserveOwnPods(ActiveBot bot, ClientWorld world,
        short sector, double now)
    {
        int pods = 0;
        foreach (var ship in world.Ships.Values)
        {
            if (ship.SideId != bot.Side || ship.SectorId != sector || ship.IsNotVisible
                || ship.StationId >= 0 || ship.Latest is null) continue;
            if (!Catalog.IsLifepodHull(ship.HullId)) continue;
            pods++;
            if (!bot.MedevacPods.ContainsKey(ship.ShipId)) bot.MedevacPods[ship.ShipId] = (sector, now);
        }
        foreach (var (id, seen) in bot.MedevacPods.Where(kv => now - kv.Value.At > MedevacRecentEjectSeconds).ToArray())
            bot.MedevacPods.Remove(id);
        int recent = bot.MedevacPods.Values.Count(v => v.Sector == sector);
        return (pods, recent);
    }

    /// <summary>P779b-1 — the medevac drop: while flying a scout that carries a
    /// rescue-capable probe launcher, drop one at the bot's own position when the
    /// gate holds (like the scouting branch, no bay approach). Returns true when
    /// this tick spent the dispenser, so the scouting/mine branches stay out.
    /// Called under cs.Gate.</summary>
    private static bool MaybeDropMedevacProbe(ActiveBot bot, ConnectionSession cs, double now,
        short sector, Vector3 position, DataProbeTypeIGC probe, short launcherPartId)
    {
        if (!bot.MedevacScout) return false;
        var world = cs.World;
        bool ownStation = world.Stations.Values.Any(s => s.SideId == bot.Side && s.ClusterId == sector);
        var (pods, ejects) = ObserveOwnPods(bot, world, sector, now);
        bool covered = world.Probes.Values.Any(p => p.SideId == bot.Side && p.ClusterId == sector
            && Catalog.RepresentativeCore().ProbeTypes.TryGetValue(p.ProbetypeId, out var type)
            && (type.EabmCapabilities & 0x6000) != 0
            && (p.Position - position).LengthSquared() < MedevacSpacing * MedevacSpacing);
        if (!ShouldDropMedevacProbe(ownStation, pods, ejects, covered, now, bot.LastRescueProbeAt, probe.LoadTime))
            return false;
        cs.SendFireExpendable(EquipmentType.Dispenser);
        bot.LastRescueProbeAt = now;
        bot.RescueProbesDropped++;
        WeaponCoverage.Global.RecordDispenserDrop(bot.ShipId, launcherPartId);   // P796
        Console.WriteLine($"[p779b] medevac bot={bot.Name} side={bot.Side} sector={sector} pods={pods} ejects60={ejects} probe={probe.ExpendableTypeId}");
        return true;
    }
}
