using System.IO;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P714 — the wave assets backing the objective/status announce VOs and the
/// reconciled autonomy cues are bundled for the real sound player: present in
/// <c>Content/Sounds</c> and registered in <c>Content.mgcb</c> (mirrors the
/// P711 milestone wave-bundled test, P680CountdownSoundTests).
/// </summary>
public class P714AnnounceSoundTests
{
    [Fact]
    public void AnnounceAndAutonomyWaves_AreBundledForTheRealSoundPlayer()
    {
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        // New in this wave — objective/status announce VOs:
        // vo_sal_madecommander (commanderSound, appmsg.cpp:2965),
        // vo_sal_madeinvestor (investorSound, appmsg.cpp:1238),
        // vo_stolen_enemyflag (enemyFlagLostSound, appmsg.cpp:3149),
        // vo_sal_artifact (artifactFoundSound, appmsg.cpp:3156),
        // vo_stolen_flag (flagLostSound, appmsg.cpp:3169).
        var newWaves = new[]
        {
            "vo_sal_madecommander", "vo_sal_madeinvestor",
            "vo_stolen_enemyflag", "vo_sal_artifact", "vo_stolen_flag",
        };

        // Reconciled in this wave (already built by earlier waves, matrix was
        // stale): vo_sal_autopilotengage/vo_sal_autopilotdisengage
        // (PlayAutopilotEngage/Disengage) and jumpin (PlayJump, rescue/teleport).
        var reconciledWaves = new[]
        {
            "vo_sal_autopilotengage", "vo_sal_autopilotdisengage", "jumpin",
        };

        foreach (var stem in newWaves.Concat(reconciledWaves))
        {
            Assert.True(new FileInfo(Path.Combine(content, "Sounds", stem + ".ogg")).Length > 0,
                $"expected {stem}.ogg present in Content/Sounds");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);
        }
    }
}
