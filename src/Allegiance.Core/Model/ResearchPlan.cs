using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.Core.Model;

public sealed class ResearchStep
{
    /// <summary>develop, station (fund constructor, then build), drone, wait.</summary>
    public string Action { get; set; } = "";
    public short Id { get; set; }
    public string Name { get; set; } = "";
    public short? AsteroidClass { get; set; }
    public double WaitSeconds { get; set; }
    public byte[] RequiredTechs { get; set; } = new byte[50];
    public List<TechIdentity> ExpectedUnlocks { get; set; } = [];
}
public sealed class ResearchPlan
{
    public int SchemaVersion { get; set; } = 1;
    public string Core { get; set; } = "";
    public int CoreVersion { get; set; }
    public short CivId { get; set; }
    public string Name { get; set; } = "";
    public List<ResearchStep> Steps { get; set; } = [];
}
public static class ResearchPlanCatalog
{
    public static JsonSerializerOptions Json => ResearchPlanJsonContext.Default.Options;
    public static ResearchPlan Read(string file) => JsonSerializer.Deserialize(File.ReadAllText(file), ResearchPlanJsonContext.Default.ResearchPlan)
        ?? throw new InvalidDataException($"empty research plan: {file}");
    public static void Validate(ResearchTree tree, string coreName, ResearchPlan plan)
    {
        if (plan.SchemaVersion != 1 || plan.Core != coreName || plan.CoreVersion != tree.Core.Version
            || plan.CivId != tree.Civ.CivilizationId || string.IsNullOrWhiteSpace(plan.Name))
            throw new InvalidDataException($"plan '{plan.Name}': schema/core/version/civ mismatch (loaded {coreName}/{tree.Core.Version}/civ{tree.Civ.CivilizationId})");
        var state = new ResearchState(tree);
        for (int i = 0; i < plan.Steps.Count; i++)
            try { state.Apply(plan.Steps[i]); }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or KeyNotFoundException)
            { throw new InvalidDataException($"plan '{plan.Name}' civ{plan.CivId} step {i + 1}: {ex.Message}", ex); }
    }
    public static IReadOnlyList<ResearchPlan> ValidateDirectory(StaticCore core, string coreName, string directory,
        ResearchSettings? settings = null)
    {
        var folder = Path.Combine(directory, coreName);
        if (!Directory.Exists(folder)) return [];
        var plans = new List<ResearchPlan>();
        foreach (var file in Directory.GetFiles(folder, "*.json").Order())
        {
            try
            {
                var p = Read(file);
                Validate(new ResearchTree(core, p.CivId, settings), coreName, p);
                if (plans.Any(other => other.CivId == p.CivId && other.Name == p.Name))
                    throw new InvalidDataException($"duplicate plan civ{p.CivId}/{p.Name}");
                plans.Add(p);
            }
            catch (Exception ex) { throw new InvalidDataException($"{file}: {ex.Message}", ex); }
        }
        return plans;
    }
    public sealed record Selection(ResearchPlan Plan, string Message);
    public static Selection Select(StaticCore core, string coreName, short civId, string directory,
        string selection = "auto", ResearchSettings? settings = null)
    {
        var tree = new ResearchTree(core, civId, settings);
        // Every authored file is checked, including files for other civs: stale data fails at boot.
        var plans = ValidateDirectory(core, coreName, directory).Where(p => p.CivId == civId).ToArray();
        ResearchPlan plan;
        string message;
        if (selection == "generated" || selection == "auto" && plans.Length == 0)
        {
            plan = tree.Generate(coreName);
            message = $"civ {civId} '{tree.Civ.Name}': {(plans.Length == 0 ? "no authored plan; " : "")}using generated fallback ({plan.Steps.Count} steps)";
        }
        else
        {
            plan = (selection == "auto" ? plans.OrderBy(p => p.Name).FirstOrDefault() : plans.SingleOrDefault(p => p.Name == selection))
                ?? throw new InvalidDataException($"civ {civId} has no research plan '{selection}' in {directory}/{coreName}");
            message = $"civ {civId} '{tree.Civ.Name}': {plan.Name} ({plan.Steps.Count} steps)";
        }
        Validate(tree, coreName, plan);
        return new(plan, message);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResearchPlan))]
public partial class ResearchPlanJsonContext : JsonSerializerContext;
