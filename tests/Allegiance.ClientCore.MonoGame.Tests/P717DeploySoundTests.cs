using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P717 — the wave assets backing the ordnance-deploy one-shot and the
/// shields-offline VO are bundled for the real sound player: present in
/// <c>Content/Sounds</c> and registered in <c>Content.mgcb</c> (mirrors the
/// P711 milestone wave-bundled test, P680CountdownSoundTests, and P714).
/// </summary>
public class P717DeploySoundTests
{
    [Fact]
    public void DeployAndShieldOfflineWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave:
        //   dropmine            — deployMineSound (sounddef.mdl:368) — mine deploy one-shot (SIM-0447)
        //   vo_sal_shieldsoffline — salShieldsOfflineSound (sounddef.mdl:791) — shields-offline VO (SIM-0243)
        var newWaves = new[]
        {
            "dropmine",
            "vo_sal_shieldsoffline",
        };

        foreach (var stem in newWaves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
