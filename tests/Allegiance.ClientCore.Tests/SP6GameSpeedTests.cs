using Allegiance.ClientCore.MonoGame;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// SP-6 — single-player Game speed (Normal / Fast / Showcase). Each speed maps to the
/// rig's showcase income/research/finale config through <c>SkirmishOptions.CreateConfig</c>;
/// the setup row cycles and is remembered in the player config (player.json); the P729
/// benchmark suggests a speed with a reason.
/// </summary>
public sealed class SP6GameSpeedTests
{
    [Theory]
    [InlineData(SkirmishSpeed.Normal, 1, 1)]
    [InlineData(SkirmishSpeed.Fast, SkirmishOptions.FastIncomeMultiplier, SkirmishOptions.FastResearchTimeScale)]
    [InlineData(SkirmishSpeed.Showcase, 5000, 8)]
    public void Each_game_speed_maps_to_income_research_and_finale(SkirmishSpeed speed, double income, double research)
    {
        var config = new SkirmishOptions { Speed = speed }.CreateConfig();
        Assert.Equal(income, config.Showcase.IncomeMultiplier);
        Assert.Equal(research, config.Showcase.ResearchTimeScale);
        // P763 — Showcase keeps the pre-flag 30 s side-select auto-seat; Normal
        // and Fast leave it off so the server waits for a POSITIONREQ (native).
        Assert.Equal(speed == SkirmishSpeed.Showcase ? SkirmishOptions.ShowcaseSideSelectAutoSeatSeconds : null,
            config.SideSelectAutoSeatSeconds);
        // Normal and Fast/Showcase all use the rig's bounded 300 s finale timeout.
        Assert.Equal(SkirmishOptions.RigFinaleTimeoutSeconds, config.Showcase.FinaleTimeoutSeconds);
    }

    [Fact]
    public void Fast_and_Showcase_override_research_even_with_a_benchmark_override()
    {
        // ResearchSpeed is a legacy/benchmark override honoured under Normal only.
        Assert.Equal(9, new SkirmishOptions { Speed = SkirmishSpeed.Normal, ResearchSpeed = 9 }.CreateConfig().Showcase.ResearchTimeScale);
        Assert.Equal(SkirmishOptions.FastResearchTimeScale, new SkirmishOptions { Speed = SkirmishSpeed.Fast, ResearchSpeed = 9 }.CreateConfig().Showcase.ResearchTimeScale);
        Assert.Equal(8, new SkirmishOptions { Speed = SkirmishSpeed.Showcase, ResearchSpeed = 9 }.CreateConfig().Showcase.ResearchTimeScale);
    }

    [Fact]
    public void Game_speed_row_cycles_all_three_speeds_and_shows_in_rows()
    {
        var menu = new SkirmishScreenModel();
        menu.Move(6);                      // row 6 = Game speed (SP-6)
        Assert.Equal(SkirmishSpeed.Normal, menu.Options.Speed);
        menu.Change(1);
        Assert.Equal(SkirmishSpeed.Fast, menu.Options.Speed);
        Assert.Contains("Game speed: Fast", menu.Rows);
        menu.Change(1);
        Assert.Equal(SkirmishSpeed.Showcase, menu.Options.Speed);
        menu.Change(1);
        Assert.Equal(SkirmishSpeed.Normal, menu.Options.Speed);   // wraps
        Assert.Contains("Game speed: Normal", menu.Rows);
        Assert.Equal(SkirmishScreenModel.RowCount, menu.Rows.Length);
        // P799 — the Lives row inserted at index 8 shifted Start/Back down one.
        Assert.Equal(SkirmishScreenModel.StartRow, 9);
        Assert.Equal(SkirmishScreenModel.BackRow, 10);
    }

    [Fact]
    public void Speed_and_choices_persist_through_the_player_config_and_are_restored()
    {
        var chosen = new SkirmishScreenModel();
        chosen.Move(2); chosen.Change(1);               // row 2 = Size -> Medium
        chosen.Move(4); chosen.Change(1);               // row 6 = Game speed -> Fast
        var pref = SkirmishPrefs.From(chosen.Options);

        string path = Path.Combine(Path.GetTempPath(), "sp6-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var saved = new IntroPreferences { Skirmish = pref };
            saved.Save(path);
            var restored = IntroPreferences.Load(path).Skirmish;
            Assert.NotNull(restored);
            Assert.Equal(SkirmishSpeed.Fast, restored!.Speed);
            Assert.Equal(SkirmishSize.Medium, restored.Size);

            // The setup screen can pick the remembered choices back up.
            var menu = new SkirmishScreenModel();
            menu.ApplyRemembered(restored);
            Assert.Equal(SkirmishSpeed.Fast, menu.Options.Speed);
            Assert.Equal(SkirmishSize.Medium, menu.Options.Size);
            Assert.Contains("Game speed: Fast", menu.Rows);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Missing_or_empty_player_config_yields_default_speed()
    {
        Assert.Equal(SkirmishSpeed.Normal, new SkirmishOptions().Speed);
        var menu = new SkirmishScreenModel();
        Assert.Contains("Game speed: Normal", menu.Rows);
    }

    [Fact]
    public void Benchmark_suggests_fast_only_with_tick_headroom_and_always_gives_a_reason()
    {
        // No measured tick -> keep Normal; Showcase is never auto-applied.
        var calm = BenchmarkPolicy.Recommend(new() { Rendered = true, CpuMs = 5, GpuMs = 10, ServerTickMs = new Distribution(3, 8, 8, 12, 15, 18) }, SkirmishSize.Medium);
        Assert.Equal(SkirmishSpeed.Fast, calm.Speed);
        Assert.Contains(calm.Reasons, r => r.Setting == "Game speed" && r.Reason.Contains("tick p95"));

        var busy = BenchmarkPolicy.Recommend(new() { Rendered = true, CpuMs = 5, GpuMs = 10 }, SkirmishSize.Large);
        Assert.Equal(SkirmishSpeed.Normal, busy.Speed);
        Assert.Contains(busy.Reasons, r => r.Setting == "Game speed" && r.Reason.Contains("Showcase"));

        var applied = new SkirmishScreenModel(); applied.ApplyDefaults(calm);
        Assert.Equal(SkirmishSpeed.Fast, applied.Options.Speed);
    }
}
