using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P721 — the two new loop-bed waves backing the per-hull thruster/interior/
/// sidethrust loops are bundled for the real sound player: present in
/// <c>Content/Sounds</c> and registered in <c>Content.mgcb</c> (mirrors the
/// P711/P714/P717/P719 wave-bundled tests). Failing-first: accelcapital.ogg and
/// lshipint.ogg did not exist before P721, so this test fails on the pre-change
/// tree; after the waves + mgcb entries land it passes.
/// </summary>
public class P721ThrusterLoopSoundTests
{
    [Fact]
    public void ThrusterAndInteriorLoopWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave:
        //   accelcapital  — capitalShipThrustInteriorSound (sounddef.mdl:1021) / CapSidethrustInteriorSound (:1346)
        //   lshipint      — capitalShipInteriorSound (sounddef.mdl:1019)
        var newWaves = new[] { "accelcapital", "lshipint" };

        foreach (var stem in newWaves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
