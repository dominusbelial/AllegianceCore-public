using System.Text;

namespace Allegiance.Core.Tests;

/// <summary>
/// Runs <see cref="Model.CrossRefValidator.Validate"/> across the whole corpus
/// and (for the README) writes an aggregated survey to <c>/tmp/crossref_survey.txt</c>.
/// </summary>
public class CrossRefSurveyTests
{
    private static readonly string OutPath = "/tmp/crossref_survey.txt";

    [Fact]
    public void Survey_aggregates_and_writes_report()
    {
        var sb = new StringBuilder();

        void Example(Dictionary<string, List<string>> sink, string kind, string detail)
        {
            if (!sink.TryGetValue(kind, out var list)) sink[kind] = list = new();
            if (list.Count < 8) list.Add(detail);
        }

        // --- All 99 cores: internal references (definitive) ---
        var coreInternal = new Dictionary<string, int>();
        var resolvedRefs = new Dictionary<string, int>(); // kind -> refs that resolved
        var examples = new Dictionary<string, List<string>>();
        var cores = Corpus.Files.Select(IgcReader.ParseFile)
            .Where(f => f.Wrapper == IgcWrapper.VersionAndDataSize).ToList();
        var allCores = cores.Select(Model.StaticCore.Load).ToList();
        foreach (var c in allCores)
        {
            // Resolved-successor baseline: proves the successor keying works and
            // only specific refs dangle (a systematic offset bug would dangle uniformly).
            foreach (var (_, h) in c.HullTypes)
                if (h.SuccessorHullId != -1) resolvedRefs["hull-successor"] = resolvedRefs.GetValueOrDefault("hull-successor") + 1;
            foreach (var (_, p) in c.PartTypes)
                if (p.SuccessorPartId != -1) resolvedRefs["part-successor"] = resolvedRefs.GetValueOrDefault("part-successor") + 1;
            foreach (var (_, l) in c.Launchers)
                if (l.SuccessorPartId != -1) resolvedRefs["launcher-successor"] = resolvedRefs.GetValueOrDefault("launcher-successor") + 1;

            foreach (var iss in Model.CrossRefValidator.Validate(c))
            {
                coreInternal[iss.Kind] = coreInternal.GetValueOrDefault(iss.Kind) + 1;
                Example(examples, iss.Kind, iss.Detail);
            }
        }

        // --- All 111 maps: internal refs (definitive) + the map->core station-type
        // link (the only core link we decode), counted in total and against EVERY core. ---
        var mapInternal = new Dictionary<string, int>();
        var stationTypeUniverse = new HashSet<short>(allCores.SelectMany(c => c.StationTypes.Keys));
        var stationTotal = 0;
        var stationUnresolvedAnywhere = 0;
        var stationExamples = new List<string>();

        var maps = Corpus.Files.Select(IgcReader.ParseFile)
            .Where(f => f.Wrapper == IgcWrapper.DataSize).ToList();
        foreach (var f in maps)
        {
            var m = Model.MissionMap.Load(f);
            foreach (var s in m.Stations) stationTotal++; // total map->core references
            foreach (var iss in Model.CrossRefValidator.Validate(allCores[0], m))
            {
                if (iss.Kind == "station-type")
                {
                    var id = ExtractStationTypeId(iss.Detail);
                    if (id is { } stid && !stationTypeUniverse.Contains(stid))
                    {
                        stationUnresolvedAnywhere++;
                        if (stationExamples.Count < 8) stationExamples.Add(iss.Detail);
                    }
                    continue;
                }
                mapInternal[iss.Kind] = mapInternal.GetValueOrDefault(iss.Kind) + 1;
                Example(examples, iss.Kind, iss.Detail);
            }
        }

        sb.AppendLine("=== core-internal issues (99 cores) ===");
        foreach (var kv in coreInternal.OrderBy(x => x.Key))
            sb.AppendLine($"{kv.Key}: {kv.Value}");
        sb.AppendLine();
        sb.AppendLine("=== non-NA successor references examined (baseline) ===");
        foreach (var kv in resolvedRefs.OrderBy(x => x.Key))
            sb.AppendLine($"{kv.Key}: {kv.Value}");
        sb.AppendLine("=== core-internal example details ===");
        foreach (var kv in examples.OrderBy(x => x.Key))
        {
            sb.AppendLine($"[{kv.Key}]");
            foreach (var ex in kv.Value) sb.AppendLine($"    {ex}");
        }
        sb.AppendLine();
        sb.AppendLine("=== map-internal issues (111 maps) ===");
        foreach (var kv in mapInternal.OrderBy(x => x.Key))
            sb.AppendLine($"{kv.Key}: {kv.Value}");
        sb.AppendLine();
        sb.AppendLine("=== map->core station-type refs ===");
        sb.AppendLine($"stations referencing a core type: {stationTotal}");
        sb.AppendLine($"station types unresolved against EVERY core: {stationUnresolvedAnywhere}");
        foreach (var ex in stationExamples) sb.AppendLine($"    {ex}");
        File.WriteAllText(OutPath, sb.ToString());

        // Lock the surveyed facts. These are the 2026-08-28 measured findings
        // (see README §"Cross-reference survey"): successor-chain dangles are
        // genuine data, maps are internally clean, every map->core station link
        // resolves against at least one core. Change the README when these move.
        Assert.Equal(31, coreInternal["hull-successor"]);
        Assert.Equal(3, coreInternal["part-successor"]);
        Assert.Empty(mapInternal);
        Assert.Equal(289, stationTotal);
        Assert.Equal(0, stationUnresolvedAnywhere);
    }

    private static short? ExtractStationTypeId(string detail)
    {
        var m = System.Text.RegularExpressions.Regex.Match(detail, @"station type (-?\d+)");
        return short.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }
}
