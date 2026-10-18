using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P741 — the shield power-up/down waves backing the new own-ship shield-edge
/// one-shots are bundled for the real sound player: present in
/// <c>Content/Sounds</c> and registered in <c>Content.mgcb</c> (mirrors the
/// P711/P714/P717/P719/P721/P723/P735/P736/P737 wave-bundled tests).
/// Failing-first: neither <c>shieldup.ogg</c> nor <c>noshld.ogg</c> existed in
/// the repo before P741, so this test fails on the pre-change tree; after the
/// waves + mgcb entries land it passes.
/// </summary>
public class P741SoundAssetTests
{
    [Fact]
    public void ShieldPowerEdgeWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave (native sounddef.mdl cites):
        //   shieldup — fastShieldActivate  (sounddef.mdl:1310), SIM-0241
        //   noshld   — fastShieldDeactivate(sounddef.mdl:1311), SIM-0242
        var newWaves = new[] { "shieldup", "noshld" };

        foreach (var stem in newWaves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
