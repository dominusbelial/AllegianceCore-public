using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P692NotificationCueTests
{
    [SkippableFact]
    public void Teleport_feedback_uses_native_critical_and_noncritical_ordinals_and_bundled_assets()
    {
        Require.NativeSource();
        var source = Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.NativeSource), "src", "Igc", "sounds.h");
        var names = Regex.Matches(File.ReadAllText(source), @"(?m)^DEF(?:EMPTY)?SOUND\((\w+)\)")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(NotificationCue.Critical, names.IndexOf("newCriticalMsg") + 1001);
        Assert.Equal(NotificationCue.NonCritical, names.IndexOf("newNonCriticalMsg") + 1001);
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        var content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        foreach (var stem in new[] { "criticalmessage", "noncriticalmessage" })
        {
            Assert.True(File.Exists(Path.Combine(content, "Sounds", stem + ".ogg")));
            Assert.Contains($"/build:Sounds/{stem}.ogg", File.ReadAllText(Path.Combine(content, "Content.mgcb")));
        }
    }
}
