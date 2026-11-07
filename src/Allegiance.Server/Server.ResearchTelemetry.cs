using System.Runtime.CompilerServices;
using Allegiance.Core;
using Allegiance.Core.Model;

namespace Allegiance.Server;

public sealed partial class Server
{
    // Log-only, sim-thread-owned receipts. Weak mission keys let rotation retire
    // old worlds without retaining an unbounded history in a long-lived server.
    private sealed class ResearchReceipts
    {
        internal readonly Dictionary<short, (int Count, double First, double Last)> Sides = new();
    }
    private readonly ConditionalWeakTable<Mission, ResearchReceipts> _researchReceipts = new();

    private void RecordResearchCompletion(Mission mission, Side side, DataDevelopmentIGC development, double now)
    {
        var receipts = _researchReceipts.GetValue(mission, static _ => new ResearchReceipts());
        var prior = receipts.Sides.GetValueOrDefault(side.Id);
        receipts.Sides[side.Id] = (prior.Count + 1, prior.Count == 0 ? now : prior.First, now);
        Console.WriteLine(FormattableString.Invariant(
            $"[research-done] side {side.Id} civ {side.Civilization.CivilizationId} '{side.Civilization.Name}' tech {development.DevelopmentId} '{development.Name}' at sim-t {now:0.###}"));
    }

    private void WriteResearchSummary(Mission mission, long match)
    {
        _researchReceipts.TryGetValue(mission, out var receipts);
        foreach (var side in mission.Sides)
        {
            var tally = receipts?.Sides.GetValueOrDefault(side.Id) ?? default;
            string first = tally.Count == 0 ? "none" : tally.First.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string last = tally.Count == 0 ? "none" : tally.Last.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"[research] match {match} side {side.Id} civ {side.Civilization.CivilizationId}: {tally.Count} developments, first at {first}, last at {last}");
        }
    }
}
