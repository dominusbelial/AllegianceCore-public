using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Tools.McpSpawner;

// Read-only census of the real core, including per-dock research legality.
internal static class P821Catalogue
{
    internal static void Write(string directory)
    {
        var core = Catalog.RepresentativeCore();
        var census = new List<object>();
        foreach (var civ in core.Civilizations.Values.OrderBy(c => c.CivilizationId))
        {
            var tree = new ResearchTree(core, civ.CivilizationId);
            var state = new ResearchState(tree);
            var initial = state.Unlocks.Where(i => i.Kind == "hull")
                .Select(i => core.HullTypes[i.Id]).Where(h => Showcase.IsPlayerBuyable(core, h))
                .Select(h => new { h.HullId, h.Name, Role = TechLoadoutSelector.Classify(h).ToString(),
                    h.ScannerRange, h.Speed, h.HitPoints, h.MaxAmmo, h.MaxFixedWeapons, h.MaxWeapons,
                    Abilities = $"0x{(ushort)h.HabmCapabilities:X4}" }).ToArray();
            string file = Path.Combine("data/showcase-plans/PCore001", $"civ{civ.CivilizationId}-plan_a.json");
            var plan = File.Exists(file) ? ResearchPlanCatalog.Read(file) : tree.Generate("PCore001");
            var first = new Dictionary<string, object>();
            var firstAtHome = new Dictionary<string, object>();
            for (int step = 0; step <= plan.Steps.Count; step++)
            {
                if (step > 0) state.Apply(plan.Steps[step - 1]);
                foreach (var role in Enum.GetValues<Showcase.HullRole>())
                {
                    if (!firstAtHome.ContainsKey(role.ToString()) && TechLoadoutSelector.Select(core,
                        civ.CivilizationId, role, state.Techs, civ.InitialStationTypeId) is { } homeSelection)
                        firstAtHome[role.ToString()] = new { Step = step, homeSelection.HullId, homeSelection.HullName };
                    if (first.ContainsKey(role.ToString())) continue;
                    foreach (short dock in state.Stations.Order())
                    {
                        var selection = TechLoadoutSelector.Select(core, civ.CivilizationId, role, state.Techs, dock);
                        if (selection is null) continue;
                        first[role.ToString()] = new { Step = step,
                            Unlock = step == 0 ? "initial" : $"{plan.Steps[step - 1].Action} {plan.Steps[step - 1].Id} {plan.Steps[step - 1].Name}",
                            Dock = dock, selection.HullId, selection.HullName };
                        break;
                    }
                }
            }
            census.Add(new { civ.CivilizationId, civ.Name, Initial = initial,
                Plan = File.Exists(file) ? "plan_a" : "generated", First = first, FirstAtHome = firstAtHome });
        }
        File.WriteAllText(Path.Combine(directory, "catalogue.json"), JsonSerializer.Serialize(census, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "hull-roles.json"), JsonSerializer.Serialize(core.HullTypes.Values
            .OrderBy(h => h.HullId).Select(h => new { h.HullId, h.Name, Role = TechLoadoutSelector.Classify(h).ToString(),
                IsPod = h.HasCapability(DataHullTypeIGC.HabmLifepod) }),
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
