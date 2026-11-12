using System.Runtime.CompilerServices;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Strategy;

namespace Allegiance.Server;

public sealed partial class Server
{
    private sealed class CoverageCursor
    {
        internal readonly Dictionary<(short Side, string Mechanism), long> Counts = new();
    }
    private readonly ConditionalWeakTable<Mission, CoverageCursor> _coverageCursors = new();

    private void WriteCivCoverage(Mission mission, StrategyReceipts receipts, double now, bool final)
    {
        var cursor = _coverageCursors.GetValue(mission, _ => new());
        var rows = new List<StrategyCoverageEvent>();
        foreach (var (key, count) in receipts.Events.OrderBy(e => e.Key))
        {
            string stage = key.Kind == "launched" ? "launched" : key.Kind.StartsWith("completed:", StringComparison.Ordinal) ? "completed" : "granted";
            rows.Add(new(key.Side, stage, key.Kind, key.Id, count.Count));
        }
        foreach (var (key, damage) in receipts.Damage.OrderBy(e => e.Key))
            rows.Add(new(key.Side, "hit", key.Kind.ToString(), key.Source, damage.Count));
        foreach (var side in mission.Sides)
        {
            foreach (var skill in new[] { ShowcaseSkill.Guns, ShowcaseSkill.Missiles, ShowcaseSkill.Mines, ShowcaseSkill.Probes,
                ShowcaseSkill.Chaff, ShowcaseSkill.NaniteShip, ShowcaseSkill.NaniteStation, ShowcaseSkill.PodRescue,
                ShowcaseSkill.DroneBuild, ShowcaseSkill.Research })
            {
                string name = skill.ToString(); long count = mission.Skills?.Count(side.Id, skill) ?? 0;
                long prior = cursor.Counts.GetValueOrDefault((side.Id, name)); cursor.Counts[(side.Id, name)] = count;
                if (count > prior) rows.Add(new(side.Id, skill is ShowcaseSkill.Guns or ShowcaseSkill.Missiles or ShowcaseSkill.Mines
                    or ShowcaseSkill.Probes or ShowcaseSkill.Chaff ? "fired" : "completed", name, -1, count - prior));
            }
            foreach (var (name, count) in new[] { ("goal-flags", (long)side.Flags), ("goal-artifacts", side.Artifacts),
                ("goal-kills", side.Kills), ("goal-conquest", side.ConquestPercent) })
            {
                long prior = cursor.Counts.GetValueOrDefault((side.Id, name)); cursor.Counts[(side.Id, name)] = count;
                if (count > prior) rows.Add(new(side.Id, "completed", name, -1, count - prior));
            }
        }
        // The two dictionaries are bounded to 256 keys each; skills/goals add
        // at most 14 * six sides. Overflow remains explicit in the main window.
        var receipt = new StrategyCoverageWindow("window", mission.Skills?.Match ?? 0, mission.Cookie, now,
            receipts.Sequence, final, _config.Bots.CivStrategyCoverageShare, rows.ToArray(),
            receipts.Sequence == 1 ? mission.Sides.Select(s =>
            {
                var profile = CivProfile.Get(mission.Core, s.Civilization.CivilizationId, mission.Research);
                var available = profile.StartingPurchases.Select(p => new StrategyNode(p.Kind, p.Id))
                    .Concat(profile.Routes.Select(r => r.Target)).ToHashSet();
                var hulls = profile.Hulls.Where(h => available.Contains(new("hull", h.Id))
                    && mission.Core.HullTypes[h.Id].GroupId >= 0).Select(h => mission.Core.HullTypes[h.Id]).ToArray();
                var tree = new ResearchTree(mission.Core, profile.Civ, mission.Research);
                var parts = tree.Parts.Where(p => available.Contains(new("part", p.PartId)) && p.GroupId != byte.MaxValue).ToArray();
                var mechanisms = new List<string>();
                if (hulls.Length > 0) mechanisms.Add("hulls");
                if (parts.Any(p => p.Type is DataPartTypeIGC.Weapon)) mechanisms.Add("weapons");
                if (parts.Any(p => p.Type is DataPartTypeIGC.MissileLauncher)) mechanisms.Add("missiles");
                if (parts.Any(p => p.Type is DataPartTypeIGC.MineLauncher)) mechanisms.Add("mines");
                if (parts.Any(p => p.Type is DataPartTypeIGC.ProbeLauncher)) mechanisms.Add("probes");
                if (parts.Any(p => p.Type is DataPartTypeIGC.ChaffLauncher)) mechanisms.Add("chaff");
                if (parts.Any(p => p.Type is DataPartTypeIGC.Weapon w
                    && mission.Core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var r) && r.Power < 0)) mechanisms.Add("repair");
                if (hulls.Any(h => h.HasCapability(DataHullTypeIGC.HabmRescue)) || parts.Any(p => p.Type is DataPartTypeIGC.ProbeLauncher pr
                    && mission.Core.ProbeTypes.TryGetValue(pr.ExpendableTypeId, out var probe) && (probe.EabmCapabilities & 0x6000) != 0)) mechanisms.Add("rescue");
                if (hulls.Any(h => h.MaxWeapons > h.MaxFixedWeapons)) { mechanisms.Add("turrets"); mechanisms.Add("gunners"); }
                if (hulls.Any(h => h.HasCapability(DataHullTypeIGC.HabmBoard))) mechanisms.Add("boarding");
                if (hulls.Any(h => !h.HasCapability(DataHullTypeIGC.HabmNoRipcord))) mechanisms.Add("ripcord");
                if (mission.Goals.IsFlagsGame) mechanisms.Add("goal-flags");
                if (mission.Goals.IsArtifactsGame) mechanisms.Add("goal-artifacts");
                if (mission.Goals.IsConquestGame) mechanisms.Add("goal-conquest");
                if (mission.Goals.IsDeathMatchGame) mechanisms.Add("goal-kills");
                if (mission.Goals.IsProsperityGame) mechanisms.Add("goal-prosperity");
                return new StrategyCoverageReachable(s.Id, hulls.Select(h => h.HullId).ToArray(),
                    parts.Select(p => p.PartId).ToArray(), mechanisms.ToArray());
            }).ToArray() : [], receipts.OverflowEvents, receipts.OverflowAmount);
        Console.WriteLine("[civ-coverage] " + System.Text.Json.JsonSerializer.Serialize(receipt,
            StrategyReceiptJsonContext.Default.StrategyCoverageWindow));
    }
}
