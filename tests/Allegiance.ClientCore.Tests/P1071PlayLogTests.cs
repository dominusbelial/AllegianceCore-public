using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Server;

namespace Allegiance.ClientCore.Tests;

[Trait("Tier", "Fast")]
public sealed class P1071PlayLogTests
{
    private static PlaySessionEntry Session(DateTimeOffset? end = null) => new(DateTimeOffset.UtcNow.AddMinutes(-5),
        end ?? DateTimeOffset.UtcNow, 300, "Pilot-Fixture", "guest", "browser", "fixture-1",
        [new(0, 3, "Fixture Civ")], new(0, 3, "Fixture Civ"), 2, 1, 3, true);

    [Fact]
    public void Default_is_off_and_name_rules_are_shared_case_insensitive_prefixes()
    {
        var config = JsonSerializer.Deserialize("{}", ServerJsonContext.Default.ServerConfig)!;
        Assert.False(config.PlayLog.Enabled);
        foreach (var name in new[] { "MCP-b12", "mcp-probe-1", "Research-CMD0-12" })
        {
            Assert.True(config.PlayLog.IsBot(name));
            Assert.True(BotNameRules.IsBot(BotNameRules.Defaults(), name));
        }
        Assert.False(config.PlayLog.IsBot("Pilot-ABC"));
        Assert.True(BotNameRules.Matches("Exact", "EXACT"));
        Assert.False(BotNameRules.Matches("Exact", "Exact-more"));
    }

    [Theory]
    [InlineData("{\"queueCapacity\":0}")]
    [InlineData("{\"maxFileBytes\":1023}")]
    [InlineData("{\"path\":\"\"}")]
    [InlineData("{\"path\":\"plays.csv\"}")]
    [InlineData("{\"botNames\":null}")]
    [InlineData("{\"botNames\":[\"*\"]}")]
    [InlineData("{\"botNames\":[\"m*p*\"]}")]
    public void Bad_log_configuration_is_rejected(string json) => Assert.Throws<InvalidDataException>(() =>
        JsonSerializer.Deserialize(json, ServerJsonContext.Default.PlayLogConfig)!.Validate());

    [Fact]
    public void Writer_flushes_private_schema_and_prunes_old_months_without_touching_unrelated_files()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p1071-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = DateTimeOffset.UtcNow;
            for (int month = 1; month <= 13; month++)
                File.WriteAllText(Path.Combine(dir, $"plays-{now.AddMonths(-month):yyyy-MM}.jsonl"), "");
            File.WriteAllText(Path.Combine(dir, "unrelated-2020-01.jsonl"), "keep");
            using (var writer = new PlayLogWriter(new() { Path = Path.Combine(dir, "plays.jsonl") }))
            {
                Assert.True(writer.TryWrite(Session()));
                Assert.True(writer.TryWrite(new PlayMatchEntry("fixture-1", now.AddHours(-1), now, [], "SingleRing", 1, 10, 0)));
            }
            var files = Directory.GetFiles(dir, "plays-*.jsonl");
            Assert.Equal(12, files.Length);
            var rows = File.ReadAllLines(Path.Combine(dir, $"plays-{now:yyyy-MM}.jsonl"));
            Assert.Equal(2, rows.Length);
            using var session = JsonDocument.Parse(rows[0]);
            var fields = session.RootElement.EnumerateObject().Select(p => p.Name).Order().ToArray();
            Assert.Equal(new[] { "start", "end", "seconds", "callsign", "kind", "client", "matchId", "civs", "sideCiv", "kills", "deaths", "ejects", "launched" }.Order(), fields);
            Assert.Equal("match", JsonDocument.Parse(rows[1]).RootElement.GetProperty("type").GetString());
            Assert.Equal("keep", File.ReadAllText(Path.Combine(dir, "unrelated-2020-01.jsonl")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Bounded_queue_and_month_size_cap_drop_and_count_without_blocking_producer()
    {
        string dir = Path.Combine(Path.GetTempPath(), "p1071-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var writer = new PlayLogWriter(new() { Path = Path.Combine(dir, "plays.jsonl"), QueueCapacity = 1, MaxFileBytes = 1024 });
            var session = Session();
            for (int i = 0; i < 10000; i++) writer.TryWrite(session);
            writer.Dispose();
            Assert.True(writer.Dropped > 0);
            Assert.Equal(10000, writer.Written + writer.Dropped);
            Assert.All(Directory.GetFiles(dir), f => Assert.InRange(new FileInfo(f).Length, 0, 1024));
            Assert.All(Directory.GetFiles(dir).SelectMany(File.ReadAllLines), line => Assert.Equal("Pilot-Fixture",
                JsonDocument.Parse(line).RootElement.GetProperty("callsign").GetString()));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Disk_failure_is_counted_and_does_not_escape_to_the_server()
    {
        string file = Path.Combine(Path.GetTempPath(), "p1071-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "directory blocked by file");
        try
        {
            using var writer = new PlayLogWriter(new() { Path = Path.Combine(file, "plays.jsonl") });
            Assert.True(writer.TryWrite(Session()));
            writer.Dispose();
            Assert.Equal(1, writer.Failed);
            Assert.Equal(1, writer.Dropped);
        }
        finally { File.Delete(file); }
    }
}
