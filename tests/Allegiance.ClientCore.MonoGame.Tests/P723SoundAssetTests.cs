using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P723 — the new sound waves backing the in-flight command/targeting one-shots
/// and the drone-chatter voices are bundled for the real sound player: present
/// in <c>Content/Sounds</c> and registered in <c>Content.mgcb</c> (mirrors the
/// P711/P714/P717/P719/P721 wave-bundled tests). Failing-first: these six waves
/// did not exist before P723, so this test fails on the pre-change tree; after
/// the waves + mgcb entries land it passes.
/// </summary>
public class P723SoundAssetTests
{
    [Fact]
    public void NewOneShotAndChatterWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave (native sounddef.mdl cites):
        //   vo_request_miner         — droneWhereToSound / droneTooCrowdedSound (:737/:736)
        //   vo_request_minefield     — droneWhereToLayMinefieldSound (:740)
        //   vo_request_tower         — droneWhereToLayTowerSound (:741)
        //   newtargetneutral         — newTargetSound (:311)
        //   vector                   — vectorLockSound (:295)
        //   vo_sal_shipcantripcord   — salShipCantRipcordSound (:350)
        var newWaves = new[]
        {
            "vo_request_miner", "vo_request_minefield", "vo_request_tower",
            "newtargetneutral", "vector", "vo_sal_shipcantripcord",
        };

        foreach (var stem in newWaves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
