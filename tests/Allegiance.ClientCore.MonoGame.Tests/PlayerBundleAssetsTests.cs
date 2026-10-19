using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class PlayerBundleAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p644-assets-" + Guid.NewGuid());
    public PlayerBundleAssetsTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void UnlistedOpenFailsAndRemainsRecordedIfLoaderSwallowsException()
    {
        File.WriteAllText(Path.Combine(_root, "new.mdl"), "new");
        var source = new ManifestAssetSource(new DirectoryAssetSource(_root), ["known.mdl"]);
        Assert.True(source.Exists("new.mdl"));
        Assert.Throws<InvalidDataException>(() => source.OpenRead("new.mdl"));
        Assert.Equal(new[] { "new.mdl" }, source.Violations);
    }

    [Fact]
    public void UnlistedOpenIsAViolationEvenWhenFileWasNotPackaged()
    {
        var source = new ManifestAssetSource(new DirectoryAssetSource(_root), []);
        Assert.Throws<InvalidDataException>(() => source.OpenRead("newly-required.mdl"));
        Assert.Contains("newly-required.mdl", source.Violations);
    }

    [Fact]
    public void ListedOpenWorksAndMissingOptionalProbeIsNotAViolation()
    {
        File.WriteAllText(Path.Combine(_root, "known.mdl"), "ok");
        var source = new ManifestAssetSource(new DirectoryAssetSource(_root), ["known.mdl"]);
        Assert.False(source.Exists("optional.mdl"));
        using var stream = source.OpenRead("known.mdl");
        Assert.Equal("ok", new StreamReader(stream).ReadToEnd());
        Assert.Empty(source.Violations);
    }

    [Fact]
    public void ListedFileOpensWhateverCaseTheDataUses()
    {
        // Native data comes from Windows: training_1.igc asks for "alephbmp" while the file is Alephbmp.mdl.
        File.WriteAllText(Path.Combine(_root, "Alephbmp.mdl"), "aleph");
        var source = new ManifestAssetSource(new DirectoryAssetSource(_root), ["Alephbmp.mdl"]);
        Assert.True(source.Exists("alephbmp.mdl"));
        using var stream = source.OpenRead("alephbmp.mdl");
        Assert.Equal("aleph", new StreamReader(stream).ReadToEnd());
        Assert.Empty(source.Violations);
    }

    [Fact]
    public void SkyLayerDoesNotRequireArtworkManifestMembership()
    {
        var art = new ManifestAssetSource(new DirectoryAssetSource(_root), []);
        string sky = Path.Combine(_root, "sky"); Directory.CreateDirectory(sky);
        File.WriteAllText(Path.Combine(sky, "sky.png"), "sky");
        var source = new LayeredAssetSource(new DirectoryAssetSource(sky), art);
        using var stream = source.OpenRead("sky.png");
        Assert.Equal("sky", new StreamReader(stream).ReadToEnd());
        Assert.Empty(art.Violations);
    }

    [Fact]
    public void PlayerConfigRoundTripsSpacedPilotAndConfigurableServer()
    {
        string path = Path.Combine(_root, "player.json");
        File.WriteAllText(path, "{\"Server\":\"game.example.org\",\"Port\":1234,\"Pilot\":\"My Pilot\"}");
        var config = PlayerConfig.Load(path);
        Assert.Equal("game.example.org", config.Server);
        Assert.Equal((ushort)1234, config.Port);
        Assert.Equal("My Pilot", config.Pilot);
        Assert.Equal(new PlayerConfig(), PlayerConfig.Load(Path.Combine(_root, "missing.json")));
    }

    [Fact]
    public void BenchmarkCollectorUsesNestedConfigAndEmptyDisablesSharing()
    {
        string path = Path.Combine(_root, "benchmark-player.json");
        File.WriteAllText(path, """{"benchmark":{"collectorUrl":"https://collector.example"}}""");
        Assert.Equal("https://collector.example", PlayerConfig.Load(path).Benchmark!.CollectorUrl);
        File.WriteAllText(path, """{"benchmark":{"collectorUrl":""}}""");
        Assert.Equal("", PlayerConfig.Load(path).Benchmark!.CollectorUrl);
        File.WriteAllText(path, "{}");
        Assert.Null(PlayerConfig.Load(path).Benchmark);
        foreach (string invalid in new[] { "false", "[]", "123", "{\"collectorUrl\":123}" })
        {
            File.WriteAllText(path, "{\"pilot\":\"example\",\"benchmark\":" + invalid + "}");
            Assert.Equal("", PlayerConfig.Load(path).Benchmark!.CollectorUrl);
            Assert.Equal("example", PlayerConfig.Load(path).Pilot);
        }
    }

    [Fact]
    public void MalformedPlayerConfigFailsClearly()
    {
        string path = Path.Combine(_root, "player.json"); File.WriteAllText(path, "{");
        Assert.Throws<System.Text.Json.JsonException>(() => PlayerConfig.Load(path));
    }

    [Fact]
    public void DefaultKeymapLivesInUserConfigAndExplicitTestDirectoryStillWorks()
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_KEYMAP") is { Length: > 0 }) return;
        Assert.Equal(Path.Combine(PlayerConfig.DirectoryPath, "keymap.json"), KeyBindingStore.ResolvePath());
        Assert.Equal(Path.Combine(_root, "keymap.json"), KeyBindingStore.ResolvePath(_root));
    }
}
