using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P737 — the sound waves backing the new own-ship reload one-shots are
/// bundled for the real sound player: present in <c>Content/Sounds</c> and
/// registered in <c>Content.mgcb</c> (mirrors the P711/P714/P717/P719/P721/
/// P723/P735/P736 wave-bundled tests). Failing-first: none of the five
/// type-specific <c>vo_sal_reloading*</c> waves existed before P737, so this
/// test fails on the pre-change tree; after the waves + mgcb entries land it
/// passes. The generic <c>startReloadSound</c> reuses the pre-existing
/// <c>mount.ogg</c> (sounddef.mdl:289), already asserted by P719-era tests.
/// </summary>
public class P737SoundAssetTests
{
    [Fact]
    public void ReloadFeedbackWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave (native sounddef.mdl cites, lines 814-818):
        //   vo_sal_reloadingammo      — salReloadingAmmoSound     (:818), SIM-0263
        //   vo_sal_reloadingfuel      — salReloadingFuelSound     (:814), SIM-0265
        //   vo_sal_reloadingmissiles  — salReloadingMissilesSound (:816), SIM-0443
        //   vo_sal_reloadingchaff     — salReloadingChaffSound    (:815), SIM-0449
        //   vo_sal_reloadingdispenser — salReloadingDispenserSound(:817), SIM-0450
        var newWaves = new[]
        {
            "vo_sal_reloadingammo",
            "vo_sal_reloadingfuel",
            "vo_sal_reloadingmissiles",
            "vo_sal_reloadingchaff",
            "vo_sal_reloadingdispenser",
        };

        foreach (var stem in newWaves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
