using System.Globalization;
using Allegiance.Core.Model;

namespace Allegiance.Core.Strategy;

/// <summary>Runtime policy marker in the existing selected-plan briefing. No authored
/// plan or wire schema changes. The route and its coverage tail replay native gates.</summary>
public static class CivExecutionPlan
{
    public const double DefaultCoverageShare = .15;
    public const double MaximumCoverageShare = .25;
    public static void ValidateCoverageShare(double share)
    {
        if (!double.IsFinite(share) || share < 0 || share > MaximumCoverageShare)
            throw new InvalidDataException("bots.civStrategyCoverageShare must be between 0 and 0.25");
    }

    public static ResearchPlan Build(ResearchTree tree, PortfolioChoice choice, string coreName,
        double coverageShare = DefaultCoverageShare)
    {
        ValidateCoverageShare(coverageShare);
        var state = new ResearchState(tree);
        var steps = new List<ResearchStep>();
        foreach (var candidate in new[] { choice.Primary, choice.Counter }.OfType<PortfolioCandidate>())
            foreach (var key in candidate.Route.Steps)
                if (state.CanApply(tree.Find(key.Kind, key.Id))) Add(tree.Find(key.Kind, key.Id));
        // Remaining research/stations/drones are a legal coverage tail. Runtime
        // readiness is rechecked independently; a missing rock never blocks it.
        int opening = steps.Count;
        while (tree.Nodes.Where(state.CanApply).OrderBy(n => n.Kind == "develop" ? 0 : 1)
            .ThenBy(n => n.Price).ThenBy(n => n.Kind, StringComparer.Ordinal).ThenBy(n => n.Id).FirstOrDefault() is { } node)
            Add(node);
        var plan = new ResearchPlan { Core = coreName, CoreVersion = tree.Core.Version,
            CivId = tree.Civ.CivilizationId, Name = FormattableString.Invariant($"civ-strategy/{coverageShare:R}/{opening}/{choice.InputHash}"), Steps = steps };
        ResearchPlanCatalog.Validate(tree, coreName, plan);
        return plan;
        void Add(ResearchNode node)
        {
            var step = CivProfile.Step(tree, new(node.Kind, node.Id));
            state.Apply(step); steps.Add(step);
        }
    }

    public static bool TryRead(ResearchPlan? plan, out double coverageShare, out int opening)
    {
        coverageShare = 0; opening = 0;
        var fields = plan?.Name.Split('/');
        return fields is { Length: 4 } && fields[0] == "civ-strategy"
            && double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out coverageShare)
            && double.IsFinite(coverageShare) && coverageShare >= 0 && coverageShare <= MaximumCoverageShare
            && int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out opening)
            && opening >= 0 && opening <= plan!.Steps.Count
            && fields[3].Length == 64 && fields[3].All(Uri.IsHexDigit);
    }
}
