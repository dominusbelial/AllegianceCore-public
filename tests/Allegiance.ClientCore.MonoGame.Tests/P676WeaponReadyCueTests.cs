using System.Text.RegularExpressions;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P676WeaponReadyCueTests
{
    [SkippableFact]
    public void Ready_cue_matches_native_sound_ordinal_and_has_the_real_bundled_wave_and_pipeline_entry()
    {
        Require.NativeSource(); Require.Artwork();
        var native = ExternalPaths.Resolve(ExternalResourceKind.NativeSource);
        var sounds = Regex.Matches(File.ReadAllText(Path.Combine(native, "src", "Igc", "sounds.h")), @"(?m)^DEF(?:EMPTY)?SOUND\((\w+)\)");
        int ordinal = sounds.Select((s, i) => (s, i)).Single(v => v.s.Groups[1].Value == "mounted").i + 1001;
        Assert.Equal(WeaponReadyCue.SoundId, ordinal);
        var root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        var content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        var stem = WeaponReadyCue.Resolve((short)ordinal);
        Assert.Equal("unmount", stem);
        Assert.Contains($"/build:Sounds/{stem}.ogg", File.ReadAllText(Path.Combine(content, "Content.mgcb")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "unmount.ogg")),
            File.ReadAllBytes(Path.Combine(content, "Sounds", stem + ".ogg")));
        Assert.Null(WeaponReadyCue.Resolve(-1));
    }
}
