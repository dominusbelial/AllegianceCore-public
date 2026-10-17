using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P719 — the wave assets backing the station/economy and loadout one-shot
/// cues are bundled for the real sound player: present in <c>Content/Sounds</c>
/// and registered in <c>Content.mgcb</c> (mirrors the P711/P714/P717 wave-bundled
/// tests). The AudioModule (SoundManager) that consumes these stems lives in
/// Allegiance.ClientCore.Game, so this MonoGame-layer test asserts the content
/// pipeline actually carries each new wave to the runtime.
/// </summary>
public class P719StationEconomySoundTests
{
    [Fact]
    public void StationEconomyAndLoadoutWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave:
        //   landed                — dockedSound (sounddef.mdl:316)          — docked cue (SIM-0388)
        //   vo_sal_welcomehome    — salWelcomeHomeSound (:801)             — welcome-home VO (SIM-0389)
        //   vo_sal_playerboarded  — boardSound (:782)                      — boarded cue (SIM-0432)
        //   groupweapons          — groupWeaponsSound (:376)               — group-fire cue (SIM-0281/0288)
        //   ungroupweapons        — ungroupWeaponsSound (:377)             — ungroup-fire cue (SIM-0280/0289)
        //   mount                 — mountSound (:286)                      — mount cue (SIM-0282)
        //   unmount               — unmountSound (:288)                    — unmount cue (SIM-0283)
        //   vo_sal_cargofull      — salCargoFullSound (:810)               — cargo-full VO (SIM-0284)
        //   drop                  — dropSound (:290)                       — drop cue (SIM-0291)
        //   vo_sal_reloading      — salReloadingSound (:819)               — reload VO (SIM-0290)
        var newWaves = new[]
        {
            "landed",
            "vo_sal_welcomehome",
            "vo_sal_playerboarded",
            "groupweapons",
            "ungroupweapons",
            "mount",
            "unmount",
            "vo_sal_cargofull",
            "drop",
            "vo_sal_reloading",
        };

        foreach (var stem in newWaves)
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
