using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P755 — the wave assets backing this wave's cues are bundled for the real
/// sound player: present in <c>Content/Sounds</c> and registered in
/// <c>Content.mgcb</c> (same discipline as P717DeploySoundTests / P737).
/// </summary>
public class P755SoundAssetTests
{
    [Fact]
    public void ChaffProbeAndMapClickWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        //   dropobject — deployChaffSound (sounddef.mdl:367, SIM-0446) and, since
        //               P755, the real deployProbeSound asset (sounddef.mdl:369, SIM-0448)
        //   mousedown  — mouseclickSound (sounddef.mdl:321) — sector-map cluster click (SIM-0331)
        var waves = new[]
        {
            "dropobject",
            "mousedown",
        };

        foreach (var stem in waves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
