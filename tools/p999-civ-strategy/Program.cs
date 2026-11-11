using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Tools.CivStrategy;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length < 1)
{
    Console.Error.WriteLine("usage: p999-civ-strategy <output-directory> [core.igc] [seed] [opponent-civ-list]");
    return 1;
}
var output = args[0];
var file = args.ElementAtOrDefault(1) ?? Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc");
var seed = args.Length > 2 ? long.Parse(args[2]) : 999;
var opponentCivs = args.Length > 3 ? args[3].Split(',').Select(short.Parse).ToArray() : null;
var core = StaticCore.Load(IgcReader.ParseFile(file));
var name = Path.GetFileNameWithoutExtension(file);
Directory.CreateDirectory(output);
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
var civs = core.Civilizations.Values.OrderBy(c => c.CivilizationId).Select(c =>
{
    var tree = new ResearchTree(core, c.CivilizationId);
    var baseline = tree.Generate(name, prioritizeResearch: true);
    var state = new ResearchState(tree);
    var initial = state.Unlocks;
    foreach (var step in baseline.Steps) state.Apply(step);
    return new { civ = c, initial, final = state.Unlocks, nodes = tree.Nodes,
        canonical = tree.Generate(name), baseline,
        strategies = Enumerable.Range(0, 3).Select(variant =>
            CivStrategyScorer.Generate(tree, name, seed, variant,
                opponentCivs?.Where(id => id != c.CivilizationId).ToArray())).ToArray() };
}).ToArray();
File.WriteAllText(Path.Combine(output, "core.json"), JsonSerializer.Serialize(new
{
    core = name, core.Version, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), seed, opponentCivs,
    civs, hulls = core.HullTypes.Values.OrderBy(h => h.HullId),
    parts = new ResearchTree(core, core.Civilizations.Keys.Min()).Parts.Select(p => new
    {
        p.PartId, p.Name, p.Description, p.Price, p.Mass, p.PartMask, p.GroupId, p.SuccessorPartId,
        equipment = p.EquipmentType.ToString(), p.TtbmRequired,
        stats = JsonSerializer.SerializeToElement(p.Type, p.Type.GetType(), json)
    }),
    stations = core.StationTypes.Values.OrderBy(s => s.StationTypeId),
    drones = core.DroneTypes.Values.OrderBy(d => d.DroneTypeId),
    projectiles = core.ProjectileTypes.Values.OrderBy(p => p.ProjectileTypeId),
    missiles = core.MissileTypes.Values.OrderBy(m => m.ExpendableTypeId)
}, json) + "\n");
Console.WriteLine($"{name}: {civs.Length} civilizations; core version {core.Version}; seed {seed}; offline only.");
return 0;
