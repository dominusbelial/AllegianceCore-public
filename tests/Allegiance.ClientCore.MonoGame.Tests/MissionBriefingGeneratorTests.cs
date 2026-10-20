using System;
using System.IO;
using System.Text;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P759b — pure tests for <see cref="MissionBriefingGenerator"/>, the port of
/// native GetCivMissionBriefing + GetMissionTip (teamscreen.cpp:732-826). The
/// briefing text is NOT invented: every word comes from the artwork corpus's
/// <c>missionbrief.mdl</c> civ strings (native <c>GetCivString</c> concatenates
/// <c>GetCivilization()-&gt;GetIconName()</c> before each key,
/// teamscreen.cpp:636-639), with the NoWin vs Default selection, the
/// secondary-objective lines and their numbers, and the random Tip pool all
/// pinned below against the real file. No graphics device.
/// </summary>
public class MissionBriefingGeneratorTests
{
    private static MissionBriefingInfo Base() => new()
    {
        SideName = "Alpha",
        CivName = "Iron Coalition",
        CivIconName = "ironleague",
        BriefingText = "Sector 1",
        CivList = ["Iron Coalition", "Belters"],
        NoWin = true,
    };

    private static MissionBriefingStrings RealStrings()
    {
        Require.Artwork();
        string path = Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "missionbrief.mdl");
        return MissionBriefingStrings.Parse(File.ReadAllText(path, Encoding.Latin1));
    }

    // ---- mdl reader -------------------------------------------------------

    [SkippableFact]
    public void Missionbrief_mdl_tip_pool_is_40_and_every_tip_comes_from_the_file()
    {
        var strings = RealStrings();

        // Native GetMissionTip: numTips = "40", rand() % numTips + 1 → Tip1..Tip40
        // (teamscreen.cpp:820-826). The file carries a commented //Tip11 line
        // (the old "Phoenix bombers" text) that the mdl loader never sees.
        Assert.Equal(40, strings.NumTips);
        var tips = strings.Tips;
        Assert.Equal(40, tips.Count);
        for (int i = 0; i < 40; i++)
            Assert.False(string.IsNullOrWhiteSpace(tips[i]), $"Tip{i + 1} must come from the file");
        Assert.DoesNotContain(tips, t => t.Contains("Phoenix bombers"));
        Assert.Equal("Use Shift+F6 when commanding to see how long your teammates have been in base.", tips[10]);
    }

    [SkippableFact]
    public void Parse_skips_comment_lines_and_unescapes_mdl_values()
    {
        var strings = MissionBriefingStrings.Parse(
            """
            // a comment line
            numTips = "2";
            //Tip1 = "commented out";
            Tip1 = "line one\nsecond \"quoted\" part";
            Tip2 = "50% off";   // trailing comment
            pane = ImagePane(x);
            """);
        Assert.Equal(2, strings.NumTips);
        Assert.Equal(2, strings.Tips.Count);
        Assert.Equal("line one\nsecond \"quoted\" part", strings.Tips[0]);
        Assert.Equal("50% off", strings.Tips[1]);
    }

    // ---- NoWin vs Default selection (teamscreen.cpp:742-750) --------------

    [SkippableFact]
    public void NoWin_uses_the_mdl_NoWin_text_formatted_with_side_briefing_and_civ_list()
    {
        var (_, _, body) = MissionBriefingGenerator.Build(Base(), tipNumber: 1, strings: RealStrings());

        // ironleagueNoWin = "Your squadron %s have been assigned to %s. Hostile
        // %s units nearby. Kill them all. Order will prevail. Good hunting!"
        Assert.Contains("Your squadron Alpha have been assigned to Sector 1. Hostile Belters units nearby. Kill them all. Order will prevail. Good hunting!", body);
    }

    [SkippableFact]
    public void Winning_game_uses_the_mdl_Default_text()
    {
        var info = Base();
        info.NoWin = false;
        var (_, _, body) = MissionBriefingGenerator.Build(info, tipNumber: 1, strings: RealStrings());

        // ironleagueDefault = "Your squadron %s have been assigned to %s.
        // Hostile %s units nearby. Locate them, then destroy or capture all of
        // their bases. Order will prevail. Good hunting!"
        Assert.Contains("Your squadron Alpha have been assigned to Sector 1. Hostile Belters units nearby. Locate them, then destroy or capture all of their bases. Order will prevail. Good hunting!", body);
        Assert.DoesNotContain("Kill them all", body);
    }

    [SkippableFact]
    public void Civ_list_excludes_the_own_civ_and_dedups_like_GetCivListString()
    {
        var info = Base();
        info.CivList = ["Iron Coalition", "Belters", "Belters", "Bios"];
        var (_, _, body) = MissionBriefingGenerator.Build(info, tipNumber: 1, strings: RealStrings());
        // GetCivListString joins "A, B and C" (teamscreen.cpp:641-669).
        Assert.Contains("Hostile Belters and Bios units nearby", body);
        info.CivList = ["Iron Coalition", "Belters", "Bios", "Gigacorp"];
        (_, _, body) = MissionBriefingGenerator.Build(info, tipNumber: 1, strings: RealStrings());
        Assert.Contains("Hostile Belters, Bios and Gigacorp units nearby", body);
    }

    [Fact]
    public void WrapWords_keeps_the_native_line_breaks()
    {
        var lines = MissionBriefingLayout.WrapWords("Main text.\n\nOther objectives include:\n *  Control 65%",
            w => w.Length, 1f, 1000f);
        Assert.Equal(new[] { "Main text.", "", "Other objectives include:", "*  Control 65%" }.Select(l => l.Replace("*  ", "* ")),
            lines.Select(l => l.Replace("*  ", "* ")));
    }

    // ---- Secondary objectives (teamscreen.cpp:752-793) --------------------

    [SkippableFact]
    public void Each_enabled_win_condition_adds_its_mdl_line_with_the_formatted_number()
    {
        var info = Base();
        info.NoWin = false;
        info.Conquest = true; info.ConquestPercent = 65;
        info.Artifacts = true; info.ArtifactGoal = 4;
        info.Flags = true; info.FlagsGoal = 3;
        info.CountdownSeconds = 5400;   // (int)(GetCountDownTime() / 60) → 90 minutes
        info.Deathmatch = true; info.KillLimit = 25;
        info.Prosperity = true; info.ProsperityMoney = 300;
        var (_, _, body) = MissionBriefingGenerator.Build(info, tipNumber: 1, strings: RealStrings());

        Assert.Contains("\n\nOther objectives include:\n", body);   // ironleagueSecondaryObjectives
        Assert.Contains("Control 65% of the bases.", body);          // Conquest %d%%
        Assert.Contains("Capture and return 4 of the artifacts.", body);
        Assert.Contains("Capture and return 3 of the enemy flags.", body);
        Assert.Contains("Eliminate as many enemies as possible within 90 minutes.", body);
        Assert.Contains("Destroy 25 enemy ships.", body);
        Assert.Contains("Invest in \"Reinforcement\", costing 300 credits", body);
    }

    [SkippableFact]
    public void Conquest_at_100_percent_and_zero_countdown_add_no_lines()
    {
        var info = Base();
        info.NoWin = false;
        info.Conquest = true; info.ConquestPercent = 100;   // native skips (teamscreen.cpp:754-756)
        info.CountdownSeconds = 0;
        var (_, _, body) = MissionBriefingGenerator.Build(info, tipNumber: 1, strings: RealStrings());

        Assert.DoesNotContain("Other objectives include:", body);
        Assert.DoesNotContain("Control", body);
        Assert.DoesNotContain("within", body);
    }

    // ---- Title / String / Tip (teamscreen.cpp:796-806, 820-826) -----------

    [SkippableFact]
    public void Title_and_objective_come_from_the_mdl_civ_strings()
    {
        var (title, objective, _) = MissionBriefingGenerator.Build(Base(), tipNumber: 1, strings: RealStrings());
        Assert.Equal("Iron Coalition", title);          // ironleagueTitle
        Assert.Equal("Tactical Briefing", objective);   // ironleagueString
    }

    [SkippableFact]
    public void Tip_number_selects_that_mdl_tip_and_wraps_into_the_pool()
    {
        var strings = RealStrings();
        var (_, _, body11) = MissionBriefingGenerator.Build(Base(), tipNumber: 11, strings: strings);
        Assert.Contains("\n\nTip: Use Shift+F6 when commanding to see how long your teammates have been in base.", body11);
        // Out-of-domain numbers wrap into the 1..numTips pool, never miss.
        var (_, _, body41) = MissionBriefingGenerator.Build(Base(), tipNumber: 41, strings: strings);
        Assert.Contains("\n\nTip: " + strings.Tips[0], body41);
    }

    [SkippableFact]
    public void Build_without_explicit_strings_loads_the_artwork_mdl()
    {
        Require.Artwork();
        var (title, objective, body) = MissionBriefingGenerator.Build(Base(), tipNumber: 1);
        Assert.Equal("Iron Coalition", title);
        Assert.Equal("Tactical Briefing", objective);
        Assert.Contains("Kill them all. Order will prevail.", body);
    }

    // ---- FormatPrintf: the %s/%d/%% subset the mdl strings use ------------

    [Fact]
    public void FormatPrintf_substitutes_in_order_and_keeps_literal_percents()
    {
        Assert.Equal("Control 65% of the bases.",
            MissionBriefingGenerator.FormatPrintf("Control %d%% of the bases.", 65));
        Assert.Equal("a-b-c",
            MissionBriefingGenerator.FormatPrintf("%s-%s-%s", "a", "b", "c"));
        Assert.Equal("%d%",
            MissionBriefingGenerator.FormatPrintf("%%d%%", 1));   // C: %% consumes both percents
    }
}
