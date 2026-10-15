using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.MapGen;
using Allegiance.Core.Sim;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: tech-tree <core.igc> dump|validate|author|sweep [output-directory] [civ,civ[;civ,civ,...]|all]");
    return 1;
}
try
{
    var core = StaticCore.Load(IgcReader.ParseFile(args[0]));
    var coreName = Path.GetFileNameWithoutExtension(args[0]);
    var command = args[1];
    var output = args.ElementAtOrDefault(2) ?? (command == "dump" ? "docs/parity/tech-tree" : "data/showcase-plans");
    var civs = core.Civilizations.Keys.Order().ToArray();
    if (command == "dump")
    {
        Directory.CreateDirectory(output);
        foreach (var id in civs)
        {
            File.WriteAllText(Path.Combine(output, $"{coreName}-civ{id}.md"), new ResearchTree(core, id).Markdown(coreName));
            Console.WriteLine($"dumped civ{id} {core.Civilizations[id].Name}");
        }
    }
    else if (command == "author")
    {
        // Explicit civ selection is data supplied by the caller, never embedded in code.
        var selected = args.Length > 3 ? args[3].Split(',').Select(short.Parse).ToArray() : civs;
        Directory.CreateDirectory(Path.Combine(output, coreName));
        foreach (var id in selected)
            foreach (var name in new[] { "plan_a", "plan_b" })
            {
                var tree = new ResearchTree(core, id);
                var plan = tree.Generate(coreName, name, expensiveFirst: name == "plan_b");
                ResearchPlanCatalog.Validate(tree, coreName, plan);
                File.WriteAllText(Path.Combine(output, coreName, $"civ{id}-{name}.json"), JsonSerializer.Serialize(plan, ResearchPlanJsonContext.Default.ResearchPlan) + "\n");
            }
    }
    else if (command == "validate")
    {
        var authored = ResearchPlanCatalog.ValidateDirectory(core, coreName, output);
        foreach (var id in civs)
        {
            var tree = new ResearchTree(core, id);
            var plan = tree.Generate(coreName);
            ResearchPlanCatalog.Validate(tree, coreName, plan);
            Console.WriteLine($"civ{id} {tree.Civ.Name}: generated {plan.Steps.Count} steps; authored {authored.Count(p => p.CivId == id)}");
        }
        Console.WriteLine($"Validated {authored.Count} authored plans and {civs.Length} generated plans.");
    }
    else if (command == "sweep")
    {
        var tuples = args.Length > 3 && args[3] != "all"
            ? args[3].Split(';').Select(t => t.Split(',').Select(short.Parse).ToArray())
            : civs.SelectMany((a, i) => civs.Skip(i + 1).Select(b => new[] { a, b }));
        var lines = new List<string> { "# Headless matchup sweep", "",
            "Each tuple boots a generated mission. Unlimited funding/asteroid availability is assumed for prerequisite validation. No economy or combat is simulated. Costs and serial bucket time exclude constructor travel/building effects. Node completions and purchases are in the linked civ tree documents.", "",
            "| Civ tuple | Side | Civ | Plan | Steps | Total funding | Serial bucket seconds | Station completions (serial bucket seconds) | Hulls | Parts | Purchases |",
            "|---|---:|---|---|---:|---:|---:|---|---:|---:|---|" };
        foreach (var tuple in tuples)
        {
            var ids = tuple.Select(id => (int)id).ToArray();
            var map = MissionMapGenerator.Generate(new MapGenParams { Teams = ids.Length, CivIds = ids }, core, 0);
            var mission = Mission.Assemble(core, map, tuple.Select((id, side) => new SideSpec((short)side, $"side{side}", id)).ToArray(), new NullIgcSite());
            foreach (var side in mission.Sides)
            {
                var selected = ResearchPlanCatalog.Select(core, coreName, side.Civilization.CivilizationId, output);
                var tree = new ResearchTree(core, side.Civilization.CivilizationId);
                var state = new ResearchState(tree);
                long price = 0, seconds = 0;
                var tiers = new List<string>();
                foreach (var step in selected.Plan.Steps)
                {
                    state.Apply(step);
                    if (step.Action == "wait") { seconds += (long)step.WaitSeconds; continue; }
                    var node = tree.Find(step.Action, step.Id); price += node.Price; seconds += node.Seconds;
                    if (node.Kind == "station") tiers.Add($"{node.Name} ({node.Id}) @{seconds}s");
                }
                var purchases = state.Unlocks;
                lines.Add($"| {string.Join('/', tuple)} | {side.Id} | {side.Civilization.Name} | {selected.Plan.Name} | {selected.Plan.Steps.Count} | {price} | {seconds} | {string.Join(", ", tiers)} | {purchases.Count(u => u.Kind == "hull")} | {purchases.Count(u => u.Kind == "part")} | [catalog]({coreName}-civ{side.Civilization.CivilizationId}.md#reachable-purchases) |");
            }
        }
        Directory.CreateDirectory("docs/parity/tech-tree");
        File.WriteAllLines($"docs/parity/tech-tree/{coreName}-matchup-sweep.md", lines);
        Console.WriteLine($"wrote {lines.Count(line => line.StartsWith("| ") && !line.StartsWith("| Civ tuple"))} side rows");
    }
    else throw new InvalidDataException($"unknown command '{command}'");
    return 0;
}
catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
