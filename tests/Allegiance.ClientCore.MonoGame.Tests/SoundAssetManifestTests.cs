using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P752 merge — every literal <c>LoadSound("Sounds/…")</c> in SoundManager must
/// have its wave in <c>Content/Sounds</c> and a <c>Content.mgcb</c> entry. One
/// missing asset makes <c>Content.Load</c> throw inside <c>SoundManager.Load</c>,
/// whose catch turns all game audio off (P752c's <c>mouseover</c> did exactly that).
/// </summary>
public class SoundAssetManifestTests
{
    [Fact]
    public void Every_literal_LoadSound_asset_is_bundled()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));
        string source = File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", "Audio", "SoundManager.cs"));

        var stems = Regex.Matches(source, "LoadSound\\(\"Sounds/([^\"]+)\"\\)").Select(m => m.Groups[1].Value).Distinct().ToArray();
        Assert.NotEmpty(stems);
        var missing = stems.Where(stem =>
            !File.Exists(Path.Combine(content, "Sounds", stem + ".ogg")) ||
            !manifest.Contains($"/build:Sounds/{stem}.ogg")).ToArray();
        Assert.True(missing.Length == 0, "sounds not bundled: " + string.Join(", ", missing));
    }
}
