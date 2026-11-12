using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Strategy;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    internal static bool CivStationDamageHull(ResearchPlan? plan, DataHullTypeIGC hull, short grantedHull,
        IReadOnlyList<ExpandedPartData>? grantedParts, DataStationTypeIGC target)
        => CivExecutionPlan.TryRead(plan, out _, out _) && hull.HasCapability(DataHullTypeIGC.HabmThreatToStation)
            && grantedHull == hull.HullId && grantedParts is { Count: > 0 }
            && ShowcaseFinale.Volley(Catalog.RepresentativeCore(), grantedParts, target).Hull > 0;

    private static bool CivStationDutyHull(ActiveBot bot, Allegiance.ClientCore.Session.ConnectionSession peer,
        DataHullTypeIGC hull, short sector)
    {
        lock (peer.Gate)
        {
            if (!CivExecutionPlan.TryRead(peer.World.ResearchBriefing?.Plan, out _, out _)) return false;
            var core = Catalog.RepresentativeCore();
            return peer.World.Stations.Values.Any(s => s.SideId >= 0 && s.SideId != bot.Side && s.ClusterId == sector
                && core.StationTypes.TryGetValue(s.StationTypeId, out var target)
                && CivStationDamageHull(peer.World.ResearchBriefing!.Plan, hull, bot.Granted.HullId, bot.Granted.Parts, target));
        }
    }

    internal static CivLoadoutPolicy.Input CivLoadoutInput(LoadoutShop shop, DataStationTypeIGC? target = null)
    {
        var core = Catalog.RepresentativeCore();
        return new(core, CivProfile.Get(core, shop.Civ, shop.Settings),
            (shop.Opponents ?? []).Where(core.Civilizations.ContainsKey).Select(c => CivProfile.Get(core, c, shop.Settings)).ToArray(),
            shop.Techs, shop.Attributes ?? GlobalAttributeSet.FromArray(core.Civilizations[shop.Civ].GasBaseAttributes),
            shop.DockType, shop.Budget, target ?? shop.Target, Observed: shop.ObservedTargets, TargetAttributes: shop.TargetAttributes);
    }

    private static TechLoadoutSelector.Selection? PickCivLoadout(LoadoutShop shop, Showcase.HullRole role,
        DataStationTypeIGC? target, double coverageShare, bool support = false)
    {
        var duty = support ? CivLoadoutPolicy.Duty.Support : role switch
        {
            Showcase.HullRole.Bomber => CivLoadoutPolicy.Duty.Strike,
            Showcase.HullRole.Scout => CivLoadoutPolicy.Duty.Scout,
            Showcase.HullRole.Interceptor => CivLoadoutPolicy.Duty.Recon,
            _ => CivLoadoutPolicy.Duty.Fighter,
        };
        var input = CivLoadoutInput(shop, target);
        var candidates = CivLoadoutPolicy.Candidates(input, duty);
        if (candidates.Count == 0) return null; // retain legal recovery/finale fallback
        if (support) return candidates[0].Loadout; // never trade repair hardware for exploration
        return CivCoverage.Select(shop, coverageShare, candidates, role);
    }

    private readonly Dictionary<short, (string Plan, int Count, string[] Duties)> _civDutyPlans = new();
    internal void RegisterCivSlot(string name, int ordinal, int count)
    {
        lock (_gate)
            if (_bots.FirstOrDefault(b => b.Name == name) is { } bot)
            { bot.CivFleetOrdinal = ordinal; bot.CivFleetSize = count; }
    }
    private void RefreshCivDuty(ActiveBot bot, Allegiance.ClientCore.Session.ConnectionSession peer)
    {
        var brief = peer.World.ResearchBriefing;
        if (bot.CivFleetOrdinal < 0 || brief is null || bot.CivDutyPlan == brief.Epoch + "/" + brief.Plan.Name
            || !CivExecutionPlan.TryRead(brief.Plan, out _, out int opening)) return;
        (string Plan, int Count, string[] Duties) cached;
        lock (_civDutyPlans)
        {
            if (!_civDutyPlans.TryGetValue(bot.Side, out cached) || cached.Plan != brief.Plan.Name || cached.Count != bot.CivFleetSize)
            {
                var core = Catalog.RepresentativeCore(); var profile = CivProfile.Get(core, brief.Plan.CivId, brief.Settings);
                var opponents = peer.World.SideRoster?.Sides.Where(s => s.SideId >= 0 && s.SideId != bot.Side)
                    .Select(s => CivProfile.Get(core, s.CivId, brief.Settings)).ToArray() ?? [];
                var spikes = CivPortfolio.Evaluate(new ResearchTree(core, profile.Civ, brief.Settings),
                    brief.Plan.Steps.Take(opening).Select(s => new StrategyNode(s.Action, s.Id)), opponents, new());
                var duties = ComposeCivFleet(core, profile, spikes, bot.CivFleetSize)
                    .SelectMany(e => Enumerable.Repeat(Showcase.ClassifyHull(core.HullTypes[e.HullId]).ToString(), e.Count)).ToArray();
                double repair = spikes.Where(s => s.Duty == "rescue-repair").Select(s => s.Power).DefaultIfEmpty().Max();
                double fighters = spikes.Where(s => s.Duty == "anti-ship").Select(s => s.Power).DefaultIfEmpty().Max();
                double share = Math.Clamp(.10 + .05 * repair / Math.Max(1, repair + fighters), .10, .15);
                for (int i = 0; i < duties.Length; i++)
                    if (duties[i] == nameof(Showcase.HullRole.Fighter)
                        && CivSupportSeat(i, duties.Length, share, Showcase.HullRole.Fighter)) duties[i] = "support";
                _civDutyPlans[bot.Side] = cached = (brief.Plan.Name, bot.CivFleetSize, duties);
            }
        }
        if (bot.CivFleetOrdinal >= cached.Duties.Length) return;
        bot.Role = peer.World.MissionGoals.IsConquestGame && bot.CivFleetOrdinal == 1 ? "boarder" : cached.Duties[bot.CivFleetOrdinal];
        bot.CivDutyPlan = brief.Epoch + "/" + brief.Plan.Name;
        bot.CivOriginalRole = null; bot.CivCoverageDuty = null; bot.CivRipcordFrom = -1;
        bot.DefensiveMineDuty = bot.Role.Equals("fighter", StringComparison.OrdinalIgnoreCase) && bot.CivFleetOrdinal % 3 == 0;
        bot.AlephFieldDuty = (bot.Role.Equals("fighter", StringComparison.OrdinalIgnoreCase)
            || bot.Role.Equals("scout", StringComparison.OrdinalIgnoreCase)) && bot.CivFleetOrdinal % 4 == 0;
        bot.CanBuyRescueProbe = TechLoadoutSelector.PickRescueProbeLauncher(Catalog.RepresentativeCore(),
            peer.World.DevelopmentTechsFor(bot.Side) ?? Allegiance.Core.Model.TechTreeBitMask.Empty) is not null;
        bot.MedevacScout = false; // ordinary roster reconciliation re-elects the scout
    }

    private readonly Dictionary<short, double> _civSupportShares = new();
    // Fighters are the flexible line. Taking a scout/recon seat here silently
    // reduces discovery capacity before medevac and coverage choose their duties.
    internal static bool CivSupportSeat(int ordinal, int count, double share, Showcase.HullRole role)
        => role == Showcase.HullRole.Fighter && ordinal >= 0
            && ordinal < Math.Min(Math.Max(0, count - 1), (int)Math.Ceiling(count * share));
    internal bool? CivSupportSlot(short side, int ordinal, int count, Showcase.HullRole role = Showcase.HullRole.Fighter)
        => _civSupportShares.TryGetValue(side, out double share)
            ? CivSupportSeat(ordinal, count, share, role) : null;

    // ON only: other sides are read through their OWN commander sessions. No
    // server objects, opponent positions or enemy construction targets enter.
    internal IReadOnlyList<Showcase.FleetEntry> CivFleet(short side, int count,
        IReadOnlyList<Showcase.FleetEntry> fallback)
    {
        lock (Probe.Gate)
            if (!CivExecutionPlan.TryRead(Probe.World.ResearchBriefing?.Plan, out _, out _)) return fallback;
        var peer = EnsureCommander(side);
        lock (peer.Gate)
        {
            if (peer.World.ResearchBriefing is not { } brief || !CivExecutionPlan.TryRead(brief.Plan, out _, out int opening)) return fallback;
            var core = Catalog.RepresentativeCore(); var profile = CivProfile.Get(core, brief.Plan.CivId, brief.Settings);
            var tree = new ResearchTree(core, profile.Civ, brief.Settings);
            var enemies = peer.World.SideRoster?.Sides.Where(s => s.SideId >= 0 && s.SideId != side)
                .Select(s => CivProfile.Get(core, s.CivId, brief.Settings)).ToArray() ?? [];
            var spikes = CivPortfolio.Evaluate(tree, brief.Plan.Steps.Take(opening).Select(s => new StrategyNode(s.Action, s.Id)), enemies, new());
            double support = spikes.Where(s => s.Duty == "rescue-repair").Select(s => s.Power).DefaultIfEmpty().Max();
            double fighters = spikes.Where(s => s.Duty == "anti-ship").Select(s => s.Power).DefaultIfEmpty().Max();
            _civSupportShares[side] = Math.Clamp(.10 + .05 * support / Math.Max(1, support + fighters), .10, .15);
            return ComposeCivFleet(core, profile, spikes, count);
        }
    }

    internal static IReadOnlyList<Showcase.FleetEntry> ComposeCivFleet(StaticCore core, CivProfile profile,
        IReadOnlyList<DeliveredSpike> spikes, int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        // Duties get persistent seats even before the desired spike unlocks.
        // P778 small fleets retain a strike pilot; its real grant still decides
        // bomber eligibility, so it fights with available equipment while waiting.
        var reachable = profile.StartingPurchases.Where(p => p.Kind == "hull").Select(p => p.Id)
            .Concat(profile.Routes.Where(r => r.Target.Kind == "hull").Select(r => r.Target.Id)).ToHashSet();
        var hulls = profile.Hulls.Where(h => reachable.Contains(h.Id) && h.FixedGuns > 0
            && Showcase.IsPlayerBuyable(core, core.HullTypes[h.Id])).ToArray();
        var reference = core.HullTypes.Values.Where(h => h.MaxFixedWeapons > 0 && Showcase.IsPlayerBuyable(core, h)).ToArray();
        if (reference.Length == 0) return [];
        double sensorReference = reference.Select(h => (double)h.ScannerRange * h.Speed).Order().ElementAt(reference.Length / 2);
        double mobilityReference = reference.Select(h => h.Speed * h.Thrust / Math.Max(1d, h.Mass)).Order().ElementAt(reference.Length / 2);
        double sensors = hulls.Select(h => (double)h.Scan * h.Speed * profile.Attributes[GlobalAttributeSet.ScanRange]
            * profile.Attributes[GlobalAttributeSet.MaxSpeed]).DefaultIfEmpty().Max();
        double mobility = hulls.Select(h => h.Speed * h.Thrust / Math.Max(1d, h.Mass)
            * profile.Attributes[GlobalAttributeSet.MaxSpeed] * profile.Attributes[GlobalAttributeSet.Thrust]).DefaultIfEmpty().Max();
        double scoutShare = Math.Clamp(.14 * Math.Sqrt(sensors / Math.Max(1, sensorReference)), .10, .24);
        double reconShare = Math.Clamp(.12 * Math.Sqrt(mobility / Math.Max(1, mobilityReference)), .05, .20);
        int scouts = count >= 3 ? Math.Max(1, (int)Math.Round(count * scoutShare)) : 0;
        double basePower = spikes.Where(s => s.Duty == "anti-base").Select(s => s.Power).DefaultIfEmpty().Max();
        double shipPower = spikes.Where(s => s.Duty == "anti-ship").Select(s => s.Power).DefaultIfEmpty().Max();
        double strikeShare = Math.Clamp(basePower / Math.Max(1, basePower + shipPower) * .35, .14, .30);
        int strike = count >= 4 ? Math.Max(1, (int)Math.Round(count * strikeShare)) : 0;
        // SC7's existing wave policy needs three concentrated strike pilots to
        // outlast recovery. One affordable seat is not a delivered power spike.
        // Keep a scout and two other seats; smaller/unreachable fleets defer it.
        if (basePower > 0 && count >= ResearchTactics.StrikeGroupSize + 3)
            strike = Math.Max(strike, ResearchTactics.StrikeGroupSize);
        int reconRoom = count - scouts - strike - 2;
        int recon = count >= 7 && reconRoom > 0 ? Math.Clamp((int)Math.Round(count * reconShare), 1, reconRoom) : 0;
        var duties = new[] { (Showcase.HullRole.Fighter, count - scouts - strike - recon),
            (Showcase.HullRole.Scout, scouts), (Showcase.HullRole.Bomber, strike), (Showcase.HullRole.Interceptor, recon) };
        var entries = new List<Showcase.FleetEntry>();
        foreach (var (role, slots) in duties.Where(d => d.Item2 > 0))
        {
            // These are role seeds only; the actual purchase is selected at the
            // pilot's live dock. Keep recognizable seeds for existing route duties.
            var hull = Showcase.RepresentativeHullOfRole(core, role)
                ?? core.HullTypes.Values.Where(h => profile.Hulls.Any(p => p.Id == h.HullId)
                    && Showcase.IsPlayerBuyable(core, h)).OrderBy(h => h.HullId).FirstOrDefault();
            if (hull.Hardpoints is null) continue;
            entries.Add(new(hull.HullId, hull.Name, slots, 0));
        }
        return entries;
    }
}
