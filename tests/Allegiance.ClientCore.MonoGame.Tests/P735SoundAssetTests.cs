using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P735 — the sound waves backing the new own-ship state notification
/// one-shots are bundled for the real sound player: present in
/// <c>Content/Sounds</c> and registered in <c>Content.mgcb</c> (mirrors the
/// P711/P714/P717/P719/P721/P723 wave-bundled tests). Failing-first:
/// <c>vo_sal_cloakengage.ogg</c> did not exist before P735, so this test fails
/// on the pre-change tree; after the wave + mgcb entry land it passes.
/// </summary>
public class P735SoundAssetTests
{
    [Fact]
    public void NewStateNotificationWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave (native sounddef.mdl cites):
        //   vo_sal_cloakengage  — salCloakEngageSound (:807), SIM-0248
        var newWaves = new[]
        {
            "vo_sal_cloakengage",
        };

        foreach (var stem in newWaves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }

        // SIM-0268 (pickUpPartSound) rides the already-bundled hoover wave.
        Assert.True(new FileInfo(Path.Combine(content, "Sounds", "hoover.ogg")).Length > 0);
        Assert.Contains("/build:Sounds/hoover.ogg", manifest);
    }
}
