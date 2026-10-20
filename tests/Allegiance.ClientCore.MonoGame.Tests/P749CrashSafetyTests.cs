using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.SinglePlayer;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>P749 — player crash safety: the crash log, guarded asset fallback,
/// the training-mode missing-core guard, and the bundle asset-closure check.</summary>
public sealed class P749CrashSafetyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p749-" + Guid.NewGuid());
    public P749CrashSafetyTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        CrashLog.DataDirectoryOverride = null;   // never leak into other tests
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void CrashHandlerWritesReportFileWithHeaderAndRingBuffer()
    {
        CrashLog.DataDirectoryOverride = Path.Combine(_root, "data");
        CrashLog.SetMode("intro");
        CrashLog.SetGpu("FakeRaster GTX-Test");
        CrashLog.PushLineForTest("[warp] P749 ring line before the crash\n");

        string path = CrashLog.WriteCrash(new InvalidDataException("boom — fake crash"), "unit-test-handler");

        Assert.True(File.Exists(path), $"expected crash report at {path}");
        Assert.StartsWith(Path.Combine(_root, "data", "logs", "crash-"), path);
        string text = File.ReadAllText(path);
        Assert.Contains("AllegianceCore crash report", text);
        Assert.Contains("Version:", text);
        Assert.Contains("Commit:", text);
        Assert.Contains("OS:", text);
        Assert.Contains("GPU: FakeRaster GTX-Test", text);
        Assert.Contains("Mode: intro", text);
        Assert.Contains("boom — fake crash", text);
        Assert.Contains("P749 ring line before the crash", text);   // ring buffer included
    }

    [Fact]
    public void MissingOptionalAssetFallsBackInsteadOfThrowing()
    {
        // Present on disk but ABSENT from the player manifest — the exact
        // ManifestAssetSource fault that killed the macOS first run. The guarded
        // read must return null (fallback), never throw.
        File.WriteAllText(Path.Combine(_root, "Alephbmp.mdl"), "aleph-surface");
        var unlisted = new ManifestAssetSource(new DirectoryAssetSource(_root), []);
        Assert.Null(WarpRenderer.TryReadOptional(unlisted, "alephbmp.mdl"));

        // Genuinely absent file — also null, not an exception.
        var plain = new DirectoryAssetSource(_root);
        Assert.Null(WarpRenderer.TryReadOptional(plain, "nothere.mdl"));

        // Listed + present still loads normally.
        var listed = new ManifestAssetSource(new DirectoryAssetSource(_root), ["Alephbmp.mdl"]);
        Assert.NotNull(WarpRenderer.TryReadOptional(listed, "alephbmp.mdl"));
    }

    [Fact]
    public void ModeWithMissingCoreReturnsToMenuWithMessage()
    {
        // A throwing training-host start (missing core/map) must not propagate:
        // it yields no host and a clear player-facing message.
        var (host, error) = Game1.TryStartTraining(
            TrainingHost.BasicFlight, _ => throw new InvalidDataException("static core 'PCore014.igc' not found"));
        Assert.Null(host);
        Assert.NotNull(error);
        Assert.Contains("Training files are missing from this install: training_1.igc", error);
        Assert.Contains("Reinstall or report this log", error);
    }

    [Fact]
    public void BundleCheckCatchesDeliberatelyRemovedTrainingMap()
    {
        // Simulate the packaged bundle with every mode file present EXCEPT a
        // deliberately removed training map (training_1.igc). The bundle
        // asset-closure check must fail on it.
        foreach (string f in new[] { "PCore014.igc", "training_3.igc", "training_5.igc",
                                     "training_6.igc", "training_nan.igc", "m2xspiderw.igc" })
            File.WriteAllText(Path.Combine(_root, f), "dummy");
        var manifest = new ManifestAssetSource(new DirectoryAssetSource(_root),
            new[] { "PCore014.igc", "training_3.igc", "training_5.igc",
                    "training_6.igc", "training_nan.igc", "m2xspiderw.igc" });
        var recorder = new RecordingAssetSource(manifest);

        var ex = Assert.Throws<InvalidDataException>(() => ModeAssetClosure.TouchModeAssetClosure(recorder));
        Assert.Contains("training_1.igc", ex.Message);
    }

    [Fact]
    public void BundleCheckPassesWhenEveryModeFileIsPresent()
    {
        // Presence gate (and the map parse) pass when all seven files exist —
        // using the real corpus so the closure parses, skipping if unavailable.
        if (!Allegiance.Core.ExternalPaths.IsAvailable(Allegiance.Core.ExternalResourceKind.Artwork)) return;
        var artwork = AssetSources.ResolveRoot();
        var recorder = new RecordingAssetSource(new DirectoryAssetSource(artwork));
        // Must not throw: PCore014 + all five training maps + m2xspiderw parse and
        // every referenced asset resolves (they are all in the corpus).
        ModeAssetClosure.TouchModeAssetClosure(recorder);
        Assert.True(recorder.OpenedCount > 0);
    }
}
