namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P731 — pure tests for <see cref="IntroPreferences"/> (the player.json
/// remembered-values model): round-trip, the recent-server list, tolerant load,
/// and the P726-compatible field shape. Written to a temp path (no AppData
/// write). No graphics device.
/// </summary>
public class IntroPreferencesTests
{
    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "p731-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Ordinary_preference_saves_preserve_the_benchmark_collector_setting()
    {
        string path = Path.Combine(Path.GetTempPath(), "bs1-prefs-" + Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, """{"pilot":"example","benchmark":{"collectorUrl":"https://collector.example"}}""");
            var prefs = IntroPreferences.Load(path);
            prefs.Pilot = "updated"; prefs.ShowStartupTrainingSuggestion = false;
            prefs.Save(path);
            Assert.Equal("https://collector.example", PlayerConfig.Load(path).Benchmark!.CollectorUrl);
            Assert.Equal("updated", IntroPreferences.Load(path).Pilot);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Invalid_optional_collector_preserves_other_player_preferences()
    {
        string path = TempPath();
        try
        {
            File.WriteAllText(path, """{"pilot":"example","benchmark":{"collectorUrl":123}}""");
            var prefs = IntroPreferences.Load(path);
            Assert.Equal("example", prefs.Pilot);
            Assert.Equal("", prefs.Benchmark!.CollectorUrl);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Round_trip_preserves_fields_and_recent_list()
    {
        var p = new IntroPreferences
        {
            Server = "10.0.0.7",
            Port = 2500,
            Pilot = "Naomi",
            Lobby = "http://10.0.0.9:5000",
            Recent = new List<string> { "10.0.0.7:2500", "192.168.1.50:2326" },
        };
        var path = TempPath();
        try
        {
            p.Save(path);
            var back = IntroPreferences.Load(path);
            Assert.Equal("10.0.0.7", back.Server);
            Assert.Equal(2500, back.Port);
            Assert.Equal("Naomi", back.Pilot);
            Assert.Equal("http://10.0.0.9:5000", back.Lobby);
            Assert.Equal(new[] { "10.0.0.7:2500", "192.168.1.50:2326" }, back.Recent);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_loads_defaults_without_throwing()
    {
        var p = IntroPreferences.Load(Path.Combine(Path.GetTempPath(), "p731-definitely-absent-" + Guid.NewGuid().ToString("N")));
        Assert.Equal("pilot", p.Pilot);
        Assert.Equal(6073, p.Port);
        Assert.Empty(p.Recent);
    }

    [Fact]
    public void Corrupt_file_loads_defaults_without_throwing()
    {
        var path = TempPath();
        File.WriteAllText(path, "<<< not json");
        try
        {
            var p = IntroPreferences.Load(path);
            Assert.Equal("pilot", p.Pilot);
            Assert.Empty(p.Recent);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Remember_records_newest_first_deduped_and_capped()
    {
        var p = new IntroPreferences();
        for (int i = 0; i < 12; i++)
            p.Remember("10.0.0." + i, 2326);
        Assert.Equal(IntroPreferences.MaxRecent, p.Recent.Count);
        // newest first
        Assert.Equal("10.0.0.11:2326", p.Recent[0]);
        Assert.Equal("10.0.0.4:2326", p.Recent[IntroPreferences.MaxRecent - 1]);
        // re-remembering moves to front, no duplicate
        p.Remember("10.0.0.5", 2326);
        Assert.Equal(IntroPreferences.MaxRecent, p.Recent.Count);
        Assert.Equal("10.0.0.5:2326", p.Recent[0]);
        Assert.Equal(1, p.Recent.Count(r => r == "10.0.0.5:2326"));
        // last server remembered
        Assert.Equal("10.0.0.5", p.Server);
        Assert.Equal(2326, p.Port);
    }

    [Fact]
    public void File_path_lives_outside_the_install()
    {
        Assert.Contains("AllegianceCore", IntroPreferences.DirectoryPath);
        Assert.EndsWith("player.json", IntroPreferences.FilePath);
    }
}
