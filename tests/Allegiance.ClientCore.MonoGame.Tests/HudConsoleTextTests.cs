using System.IO;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class HudConsoleTextTests
{
    [Theory]
    [InlineData((short)0, "goto")]
    [InlineData((short)1, "attack")]
    [InlineData((short)2, "capture")]
    [InlineData((short)3, "defend")]
    [InlineData((short)4, "pickup")]
    [InlineData((short)5, "goto")]
    [InlineData((short)6, "repair")]
    [InlineData((short)7, "join")]
    [InlineData((short)8, "mine")]
    [InlineData((short)9, "build")]
    [InlineData((short)-1, "")]
    public void Command_verbs_match_native_cd_all_commands(short commandId, string verb)
        => Assert.Equal(verb, HudConsoleFormat.Verb(commandId));

    [Theory]
    [InlineData((short)1, "Garrison", "attack Garrison")]
    [InlineData((short)3, null, "defend")]
    [InlineData((short)-1, "Garrison", "")]
    public void Order_text_is_verb_plus_target_name(short commandId, string? target, string expected)
        => Assert.Equal(expected, HudConsoleFormat.OrderText(commandId, target));

    [Theory]
    [InlineData(0, "Pilot", "")]
    [InlineData(1, "Pilot", "Observer of Pilot")]
    [InlineData(2, "Pilot", "Turret of Pilot")]
    public void Board_label_matches_native_wording(int boardState, string carrier, string expected)
        => Assert.Equal(expected, HudConsoleFormat.BoardLabel(boardState, carrier));

    [Fact]
    public void Native_slot_and_duration_constants_are_pinned()
    {
        Assert.Equal(3, HudConsoleFormat.OrdinaryTipSlots);
        Assert.Equal(4, HudConsoleFormat.CriticalTipSlots);
        Assert.Equal(7.5, HudConsoleFormat.CriticalTipDurationSeconds);
    }

    [Fact]
    public void Console_anchors_use_the_top_edge_and_native_label_below_offsets()
    {
        // dialog.mdl origin is lower-left with y up (dialog.mdl:5259-5268), so
        // -37/-75 are 37/75 px below the TOP; the label sits first and the
        // value/name is 14 px below it (dialog.mdl:1473-1485, :1508-1567).
        Assert.Equal(37f, HudConsoleFormat.SectorLabelTop);
        Assert.Equal(14f, HudConsoleFormat.SectorNameBelowLabel);
        Assert.Equal(75f, HudConsoleFormat.TurretLabelTop);
        Assert.Equal(37f, HudConsoleFormat.OrderLabelTop);
        Assert.Equal(37f, HudConsoleFormat.OrderLabelX);
        Assert.Equal(57f, HudConsoleFormat.OrderTextX);
        Assert.Equal(20f, HudConsoleFormat.OrderQueuedBelowLabel);
        Assert.Equal(40f, HudConsoleFormat.OrderAcceptedBelowLabel);
        Assert.Equal(75f, HudConsoleFormat.RipcordLeft);
        Assert.Equal(15f, HudConsoleFormat.RipcordTop);
        Assert.Equal(150f, HudConsoleFormat.RipcordWidth);
        // FONT-1c — native Point(18/15,20) is the badge text TOP, 20 px above
        // the art's lower edge (dialog.mdl:5070-5080, 5107-5120). The lives art
        // is 22 px and the kills art 25 px in the decoded MDLs, so the 14 px
        // glyph box sits inside both badges.
        float livesTop = HudConsoleFormat.BadgeArtTop(HudConsoleFormat.LivesBadgeAnchor, 22f);
        float killsTop = HudConsoleFormat.BadgeArtTop(HudConsoleFormat.KillsBadgeAnchor, 25f);
        Assert.Equal(97f - 20f, HudConsoleFormat.BadgeTextTop(livesTop, 22f, 1f));
        Assert.Equal(177f - 20f, HudConsoleFormat.BadgeTextTop(killsTop, 25f, 1f));
        Assert.True(HudConsoleFormat.BadgeTextTop(livesTop, 22f, 1f) >= livesTop);
        Assert.True(HudConsoleFormat.BadgeTextTop(livesTop, 22f, 1f) + 14f <= livesTop + 22f);
    }

    [Fact]
    public void Badge_art_lower_left_sits_on_the_native_side_top_left_end_point()
    {
        // FONT-1c review — dialog.mdl:5441-5445 (LivesImage/KillsImage,
        // SideTopLeft, start Point(45,-97/-177), END Point(40,-97/-177)); a shown
        // overlay sits at its end point (console.cpp:3089-3096). The untranslated
        // ImportImage's y-up bounds are Rect(Point(0,0),size)
        // (engine/image.cpp:184-190), so its LOWER-left is (40, top-97/177).
        Assert.Equal(40f, HudConsoleFormat.BadgeLeft);
        Assert.Equal(97f, HudConsoleFormat.LivesBadgeAnchor);
        Assert.Equal(177f, HudConsoleFormat.KillsBadgeAnchor);
        // Decoded livesbmp 89x22, hudkillsbmp 88x25: the art's BOTTOM is the anchor.
        Assert.Equal(75f, HudConsoleFormat.BadgeArtTop(97f, 22f));
        Assert.Equal(152f, HudConsoleFormat.BadgeArtTop(177f, 25f));
        Assert.Equal(97f, HudConsoleFormat.BadgeArtTop(97f, 22f) + 22f);

        // The renderer must route both badges through the anchor + BadgeArtTop
        // (the pre-fix code drew the art TOP at 97/177 and x=45).
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string hud = File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", "Rendering", "HudRenderer.cs"));
        Assert.DoesNotContain("45f, 97f", hud);
        Assert.DoesNotContain("45f, 177f", hud);
        Assert.Contains("HudConsoleFormat.BadgeArtTop(anchorY, artHeight)", hud);
        Assert.Contains("float x = HudConsoleFormat.BadgeLeft;", hud);
    }

    [Theory]
    [InlineData(0, 0, 50f)]
    [InlineData(7, 0, 134f)]
    [InlineData(2, 1, 60f)]
    public void Critical_tip_top_is_50_plus_12_chat_lines_minus_14_per_slot(int chatLines, int slot, float expected)
        => Assert.Equal(expected, HudConsoleFormat.CriticalTipTop(chatLines, slot));

    [Theory]
    [InlineData(0, 85f)]
    [InlineData(7, 169f)]
    public void Vote_banner_top_is_85_plus_12_chat_lines(int chatLines, float expected)
        => Assert.Equal(expected, HudConsoleFormat.VoteTop(chatLines));

    [Fact]
    public void Ripcord_label_is_null_when_inactive_or_start_time_unknown_and_uses_native_rounding()
    {
        Assert.Null(HudConsoleFormat.RipcordLabel(false, 1000L, 9.0f));
        Assert.Null(HudConsoleFormat.RipcordLabel(true, null, 9.0f));
        Assert.Equal("Ripcord in: 10", HudConsoleFormat.RipcordLabel(true, 1000L, 9.0f));
        Assert.Equal("Ripcord in: 10", HudConsoleFormat.RipcordLabel(true, 1000L, 9.2f));
        Assert.Equal("Ripcord in: 10", HudConsoleFormat.RipcordLabel(true, 1000L, 9.8f));
        Assert.Equal("Ripcord in: 1", HudConsoleFormat.RipcordLabel(true, 1000L, 0.0f));
    }
}
