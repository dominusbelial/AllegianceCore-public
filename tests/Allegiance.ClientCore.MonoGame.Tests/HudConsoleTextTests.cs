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
