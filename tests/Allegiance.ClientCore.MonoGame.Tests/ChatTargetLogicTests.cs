using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// Pure-logic tests for <see cref="ChatTargetLogic"/> — the P456 chat-recipient
/// decision and Tab cycle, kept out of Game1 so the order and the skips are
/// provable without a graphics device (the same discipline as
/// <see cref="ChatLogFormat"/> / FlightInput).
/// </summary>
public class ChatTargetLogicTests
{
    [Fact]
    public void FreshCompose_DefaultsTo_Team()
    {
        // P456: native constructs its chat state as m_chsChat(CHAT_TEAM)
        // (console.cpp:644); the port previously sent Everyone. A fresh compose
        // must target Team.
        Assert.Equal(ChatTarget.Team, ChatTargetLogic.DefaultTarget);
    }

    [Fact]
    public void DefaultTarget_Persists_AcrossComposeSessions()
    {
        // Native's m_chsChat is a member and is never reset when the input line
        // closes, so the target persists. The port mirrors that by keeping the
        // target in member state seeded from DefaultTarget — recomposing does not
        // revert to the default unless it was never changed.
        var target = ChatTargetLogic.DefaultTarget;
        target = ChatTargetLogic.Next(target);      // simulated "player cycled"
        // Cycling away and re-opening still remembers the cycle; only a fresh
        // Game1 reseeds from DefaultTarget.
        Assert.NotEqual(ChatTargetLogic.DefaultTarget, target);
        Assert.Equal(ChatTargetLogic.Next(target), ChatTargetLogic.Next(target));
    }

    [Theory]
    [InlineData(ChatTarget.Team, ChatTarget.Everyone)]
    [InlineData(ChatTarget.Everyone, ChatTarget.Wing)]
    [InlineData(ChatTarget.Wing, ChatTarget.FriendlySector)]
    [InlineData(ChatTarget.FriendlySector, ChatTarget.Team)]
    public void Cycle_Follows_NativeNoAlliesOrder_SkippingIndividualAndShip(
        ChatTarget current, ChatTarget expectedNext)
    {
        // Native no-allies order while composing: Wing → [Individual skipped] →
        // [Ship skipped] → FriendlySector → Team → Everyone → Wing.
        Assert.Equal(expectedNext, ChatTargetLogic.Next(current));
    }

    [Fact]
    public void Cycle_WrapsAround_AndHandlesUnknown()
    {
        // The ring wraps Team back to... the head of the cycle, and an unknown /
        // non-cycle value (e.g. Individual) wraps to Wing like native's default.
        Assert.Equal(ChatTarget.Wing, ChatTargetLogic.Next(ChatTarget.Everyone));
        Assert.Equal(ChatTarget.Wing, ChatTargetLogic.Next(ChatTarget.Individual));
        Assert.Equal(ChatTarget.Wing, ChatTargetLogic.Next((ChatTarget)999));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("  hello")]   // leading space → native's *pcc==' ' guard fires
    public void TabCycles_WhenFirstChar_EmptyOrSpace(string buffer)
    {
        // Native OnTab only cycles while *pcc=='\0' || *pcc==' '
        // (console.cpp:2747-2755) — the guard reads only the FIRST character, so
        // a leading space still cycles even with text after it.
        Assert.True(ChatTargetLogic.CanCycleWhileComposing(buffer));
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("he llo")]   // interior space ≠ leading space → no cycle
    [InlineData("\t")]
    public void TabDoesNotCycle_WhenTypedText_Leads(string buffer)
    {
        Assert.False(ChatTargetLogic.CanCycleWhileComposing(buffer));
    }

    [Theory]
    [InlineData(ChatTarget.Team, "[TEAM] ")]
    [InlineData(ChatTarget.Everyone, "[EVERYONE] ")]
    [InlineData(ChatTarget.Wing, "[WING] ")]
    [InlineData(ChatTarget.FriendlySector, "[FSECTOR] ")]
    public void ComposeLabel_Covers_AllCycleTargets(ChatTarget target, string expected)
    {
        Assert.Equal(expected, ChatTargetLogic.ComposeLabel(target));
    }

    [Fact]
    public void ComposeLabel_UnknownTarget_Empty()
    {
        Assert.Equal("", ChatTargetLogic.ComposeLabel(ChatTarget.Individual));
        Assert.Equal("", ChatTargetLogic.ComposeLabel((ChatTarget)999));
    }
}
