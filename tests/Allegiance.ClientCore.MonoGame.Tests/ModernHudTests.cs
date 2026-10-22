using Allegiance.TestInfra;
using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class ModernHudTests
{
    [Fact]
    public void Preferences_migrate_existing_profiles_and_keep_explicit_style_on_restart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hud2-settings-" + Guid.NewGuid());
        string path = Path.Combine(directory, "hud.json");
        try
        {
            Assert.Equal(HudStyle.Modern, HudSettings.Load(path, false).Style);
            Assert.Equal(HudStyle.Classic, HudSettings.Load(path, true).Style);
            var settings = HudSettings.Load(path, true);
            settings.Style = HudStyle.Modern; settings.Size = 1.25f; settings.ColorBlindSafe = true;
            settings.Save(path);
            var restored = HudSettings.Load(path, true);
            Assert.Equal(HudStyle.Modern, restored.Style); Assert.Equal(1.25f, restored.Size); Assert.True(restored.ColorBlindSafe);
            settings.Size = float.NaN; settings.Style = (HudStyle)99; settings.Save(path);
            restored = HudSettings.Load(path, false);
            Assert.Equal(1, restored.Size); Assert.Equal(HudStyle.Classic, restored.Style);
            File.WriteAllText(path, "broken json"); Assert.Equal(HudStyle.Classic, HudSettings.Load(path, true).Style);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact]
    public void Projected_labels_yield_to_reserved_instruments_and_notices_yield_to_labels()
    {
        var layout = new HudTextLayout(); layout.Reset(1920, 1080);
        var energy = new Rectangle(1100, 300, 110, 150); layout.Reserve(energy);
        var target = layout.Place("Target name", new(250, 32), new(1080, 325));
        var notice = layout.Place("NO ENEMY FIGHTER IN SECTOR", new(480, 32), new(target.X, target.Y));
        Assert.False(target.Intersects(energy)); Assert.False(notice.Intersects(target)); Assert.False(notice.Intersects(energy));
        var label = layout.Place("ENGY", new(70, 32), new(1120, 410), instrument: true);
        Assert.True(label.Intersects(energy)); Assert.False(label.Intersects(target)); Assert.False(label.Intersects(notice));
    }
    [Fact]
    public void Safe_palette_keeps_neutral_and_team_relationships_distinguishable()
    {
        Assert.NotEqual(HudPalette.Relationship(true), HudPalette.Relationship(false));
        Assert.NotEqual(HudPalette.Relationship(null), HudPalette.Relationship(false));
        Assert.NotEqual(HudPalette.Side(Color.Red), HudPalette.Side(Color.Blue));
    }
    [Trait("Requires", "Gpu")]
    [SkippableFact]
    [Trait("Tier", "Live")]   // renders every HUD-1 scenario in both skins (~11 s)
    public async Task Both_skins_render_Hud1_scenarios_without_overlapping_text_and_modern_draws_all_visible_elements()
    {
        string repo = Repo();
        string configuration = AppContext.BaseDirectory.Contains("/Release/") ? "Release" : "Debug";
        string output = Path.Combine(Path.GetTempPath(), "HUD2-render-" + Guid.NewGuid());
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(repo, "tools/hud2-proof/bin", configuration, "net10.0/hud2-proof.dll"));
        start.ArgumentList.Add(output); start.ArgumentList.Add("--test");
        start.Environment["SDL_VIDEODRIVER"] = "offscreen"; start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("HUD2 GL render check timed out; evidence " + output); }
        string text = await stdout + await stderr;
        Require.GraphicsDevice(text);
        Assert.True(process.ExitCode == 0, text + " Evidence " + output);
        Assert.Contains("HUD2_RENDER_PASS", text);
    }
    private static string Repo()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AllegianceCore.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("repo");
    }
}
