using System.Text.Json;
using System.Text.Json.Nodes;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P987 — replay compact INPUT snapshots recorded by real seeded 2-side and
/// 3-side loopback showcases. This is deliberately not a whole-match golden:
/// every record independently invokes the same pure decision core as the bot.
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class P987RecordedBotDecisionCorpusTests
{
    private const string CorpusFile = "recorded.json";
    private static readonly string CorpusDir = Path.Combine(
        Path.GetDirectoryName(typeof(P987RecordedBotDecisionCorpusTests).Assembly.Location)!,
        "..", "..", "..", "GoldenCorpus", "BotDecisions");
    private static string CorpusPath => Path.Combine(CorpusDir, CorpusFile);
    private static bool UpdateMode => Environment.GetEnvironmentVariable("UPDATE_CORPUS") == "1";

    [SkippableFact]
    public void Recorded_inputs_replay_to_the_same_outputs()
    {
        Require.Artwork();
        if (UpdateMode) RegenerateFromRawRecordings();
        var records = Load();
        int replayed = 0;
        foreach (var node in records)
        {
            var record = node!.AsObject();
            string family = record["family"]!.GetValue<string>();
            string decision = record["decision"]!.GetValue<string>();
            string id = record["id"]!.GetValue<string>();
            var expected = record["outputs"]!;
            JsonNode actual;
            try
            {
                actual = BotDecisionReplay.Replay(family, decision, record["inputs"]!.AsObject());
            }
            catch (Exception ex)
            {
                Assert.Fail($"{id}: replay threw {ex.GetType().Name}: {ex.Message}\nInputs: {record["inputs"]!.ToJsonString(BotDecisionRecorder.Indented)}");
                return;
            }
            string expectedText = expected.ToJsonString(BotDecisionRecorder.Compact);
            string actualText = actual!.ToJsonString(BotDecisionRecorder.Compact);
            if (expectedText != actualText)
                Assert.Fail($"Recorded bot decision drift: {id}\n"
                    + $"Inputs: {record["inputs"]!.ToJsonString(BotDecisionRecorder.Indented)}\n"
                    + BotDecisionRecorder.Diff(expected.ToJsonString(BotDecisionRecorder.Indented),
                        actual.ToJsonString(BotDecisionRecorder.Indented)));
            replayed++;
        }
        Assert.True(replayed > 0, "The recorded corpus must contain real-match decisions");
    }

    [SkippableFact]
    public void Corpus_is_bounded_stable_and_covers_both_real_match_shapes()
    {
        Require.Artwork();
        Assert.True(File.Exists(CorpusPath), $"Missing {CorpusPath}; run the P987 re-record script with UPDATE_CORPUS=1");
        Assert.True(new FileInfo(CorpusPath).Length <= 2 * 1024 * 1024,
            $"Recorded corpus exceeds 2 MiB: {new FileInfo(CorpusPath).Length} bytes");
        var records = Load();
        Assert.All(records, node =>
        {
            var record = node!.AsObject();
            Assert.Equal(BotDecisionRecorder.FormatVersion, record["version"]!.GetValue<int>());
            string id = record["id"]!.GetValue<string>();
            string scenario = record["scenario"]!.GetValue<string>();
            string family = record["family"]!.GetValue<string>();
            string decision = record["decision"]!.GetValue<string>();
            Assert.Matches($"^{scenario}/{family}\\.{decision}/[0-9a-f]{{16}}$", id);
            Assert.Null(record["source"]);
            Assert.Null(record["timestamp"]);
        });
        var scenarios = records.Select(r => r!["scenario"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("seeded-2side", scenarios);
        Assert.Contains("seeded-3side", scenarios);
        var decisions = records.Select(r => $"{r!["family"]!.GetValue<string>()}.{r["decision"]!.GetValue<string>()}")
            .ToHashSet(StringComparer.Ordinal);
        string[] required =
        [
            "CombatTarget.Select", "Loadout.Select", "Defense.Assignment", "ResearchCommander.ConstructionRock",
            "ResearchCommander.ConstructorOrder", "ResearchCommander.Donation", "Route.NextHop",
            "Medevac.PickMedevacScouts", "Medevac.DropGate", "Finale.TriggerSides", "Finale.VolleyRequirement",
            "Finale.ShouldRelease", "Lifecycle.PodTransition", "Lifecycle.MissionRefresh",
        ];
        foreach (string decision in required)
            Assert.Contains(decision, decisions);
    }

    private static JsonArray Load()
    {
        Assert.True(File.Exists(CorpusPath), $"Missing {CorpusPath}; run the P987 re-record script with UPDATE_CORPUS=1");
        return JsonNode.Parse(File.ReadAllText(CorpusPath))!.AsArray();
    }

    private static void RegenerateFromRawRecordings()
    {
        string variable = Environment.GetEnvironmentVariable("P987_RAW_PATHS")
            ?? throw new InvalidOperationException("UPDATE_CORPUS=1 needs P987_RAW_PATHS (JSONL files or directories separated by ';')");
        var files = new List<string>();
        foreach (string entry in variable.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(entry)) files.AddRange(Directory.GetFiles(entry, "*.jsonl", SearchOption.TopDirectoryOnly));
            else if (File.Exists(entry)) files.Add(entry);
            else throw new FileNotFoundException("P987 raw recording does not exist", entry);
        }
        Assert.NotEmpty(files);
        JsonArray corpus = BotDecisionRecorder.BuildCorpus(files);
        Assert.NotEmpty(corpus);
        Directory.CreateDirectory(CorpusDir);
        File.WriteAllText(CorpusPath, corpus.ToJsonString(BotDecisionRecorder.Indented) + "\n");
        Console.WriteLine($"P987 corpus regenerated: {corpus.Count} records, {new FileInfo(CorpusPath).Length} bytes, {files.Count} raw files");
    }
}
