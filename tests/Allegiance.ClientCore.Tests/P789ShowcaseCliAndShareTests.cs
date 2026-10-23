using System.Text.Json;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;

namespace Allegiance.ClientCore.Tests;

/// <summary>P789 — the CLI keeps working (--benchmark parse unchanged) and the
/// share payload bumps to schema 2 with the Showcase score, v1 still valid.</summary>
public class P789ShowcaseCliAndShareTests
{
    [Fact]
    public void BenchmarkCli_ParseIsUnchanged()
    {
        var command = BenchmarkCommand.Parse(["--benchmark", "skirmish:epic", "--benchmark-seconds", "60", "--benchmark-headless", "--benchmark-output", "/tmp/b.json"])!;
        Assert.Equal(SkirmishSize.Epic, command.Size);
        Assert.Equal(60, command.Seconds);
        Assert.True(command.Headless);
        Assert.Equal("/tmp/b.json", command.Output);
        Assert.Null(BenchmarkCommand.Parse([]));
        Assert.Throws<ArgumentException>(() => BenchmarkCommand.Parse(["--benchmark", "bogus"]));
        Assert.Throws<ArgumentException>(() => BenchmarkCommand.Parse(["--benchmark", "skirmish:5"]));
    }

    [Fact]
    public void FixedShowcase_IsSeed729_Small_150s_ShowcaseSpeed_OnlyThatIsRanked()
    {
        var tune = BenchmarkCommand.Parse(["--benchmark", "skirmish:small"])!;
        Assert.True(tune.IsFixedShowcase);
        Assert.Equal(729, tune.Options.Seed);
        Assert.Equal(SkirmishSize.Small, tune.Size);
        Assert.Equal(150, tune.Seconds);
        Assert.Equal(SkirmishSpeed.Showcase, tune.Options.Speed);
        Assert.False(tune.Options.StartAsSpectator);

        Assert.False(BenchmarkCommand.Parse(["--benchmark", "skirmish:large"])!.IsFixedShowcase);
        Assert.False(BenchmarkCommand.Parse(["--benchmark", "skirmish:small", "--benchmark-seconds", "30"])!.IsFixedShowcase);
        Assert.False(BenchmarkCommand.Parse(["--benchmark", "replay"])!.IsFixedShowcase);
    }

    [Fact]
    public void SharePayload_Schema2_CarriesScoreAndMatchKind()
    {
        var result = new BenchmarkResult
        {
            Mode = "skirmish", Preset = "Small", DurationSeconds = 150,
            Score = 1200, MatchKind = "showcase-fixed",
            Runs = [new BenchmarkRun { Repetition = 1, Rendered = true, FrameMs = Distribution.Of([16.7]) }],
            Median = new BenchmarkRun { Repetition = 2, Rendered = true, FrameMs = Distribution.Of([16.7]) },
        };
        string json = SharePayload.From(result, new ShareHardware("cpu", 4, 8, "gpu", 8, "driver", 32, "Linux", "1", 1920, 1080)).ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(1200, doc.RootElement.GetProperty("result").GetProperty("score").GetInt32());
        Assert.Equal("showcase-fixed", doc.RootElement.GetProperty("result").GetProperty("matchKind").GetString());
    }

    [Fact]
    public void SharePayload_UnmeasuredScore_SerializesNull_LikeV1PlusOptionals()
    {
        var result = new BenchmarkResult { Mode = "skirmish", Preset = "Small", DurationSeconds = 150, Score = null, MatchKind = "unranked" };
        string json = SharePayload.From(result, new ShareHardware(null, null, 8, null, null, null, null, "Linux", "1", 1280, 720)).ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("result").GetProperty("score").ValueKind);
        Assert.Equal("unranked", doc.RootElement.GetProperty("result").GetProperty("matchKind").GetString());
        // A v1 payload (schema 1, no score fields at all) is unchanged client-side history:
        // the schema keeps optional fields out of `required`, so v1 stays valid.
    }
}
