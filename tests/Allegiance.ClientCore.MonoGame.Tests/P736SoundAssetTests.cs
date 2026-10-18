using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P736 — the sound waves backing the new presence / side-change salutation
/// one-shots are bundled for the real sound player: present in
/// <c>Content/Sounds</c> and registered in <c>Content.mgcb</c> (mirrors the
/// P711/P714/P717/P719/P721/P723/P735 wave-bundled tests). Failing-first:
/// none of these six waves existed before P736, so this test fails on the
/// pre-change tree; after the waves + mgcb entries land it passes.
/// </summary>
public class P736SoundAssetTests
{
    [Fact]
    public void PresenceSalutationWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave (native sounddef.mdl cites, lines 825-831):
        //   vo_sal_recruitsarrived — salRecruitsArrivedSound (:825), SIM-0464
        //   vo_sal_mia             — salMIASound             (:826), SIM-0463
        //   vo_sal_quit            — salQuitSound            (:827-828), SIM-0462
        //   vo_sal_traitor         — salChangeSidesSound     (:829), SIM-0461
        //   vo_sal_enemyjoiners    — salEnemyJoinersSound    (:830), SIM-0465
        //   vo_sal_enemyleaves     — salEnemyLeavesSound     (:831), SIM-0468
        var newWaves = new[]
        {
            "vo_sal_recruitsarrived",
            "vo_sal_mia",
            "vo_sal_quit",
            "vo_sal_traitor",
            "vo_sal_enemyjoiners",
            "vo_sal_enemyleaves",
        };

        foreach (var stem in newWaves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
