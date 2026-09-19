using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P271 — tests for the <c>ClientWorld</c> handler that stores the
/// <c>SET_BRIEFING_TEXT(9)</c> mission briefing the server sends on join. The
/// MonoGame briefing popup renders <see cref="ClientWorld.BriefingText"/>.
/// </summary>
public class ClientWorldBriefingTests
{
    [Fact]
    public void SetBriefingText_StoresTextAndFlag()
    {
        var world = new ClientWorld();
        Assert.Equal("", world.BriefingText);

        world.Apply(new SetBriefingText { Text = "Ragnarok", FGenerateCivBriefing = true });

        Assert.Equal("Ragnarok", world.BriefingText);
        Assert.True(world.BriefingFGenerateCivBriefing);
    }

    [Fact]
    public void SetBriefingText_StoresFalseBranch()
    {
        var world = new ClientWorld();
        world.Apply(new SetBriefingText { Text = "custom story text", FGenerateCivBriefing = false });

        Assert.Equal("custom story text", world.BriefingText);
        Assert.False(world.BriefingFGenerateCivBriefing);
    }

    [Fact]
    public void SetBriefingText_EmptyLeavesBlank()
    {
        var world = new ClientWorld();
        world.Apply(new SetBriefingText { Text = "", FGenerateCivBriefing = true });
        Assert.Equal("", world.BriefingText);
    }

    [Fact]
    public void SetBriefingText_DecodesTheActualWireShape()
    {
        // Guard against field-name drift: the encoded message must round-trip
        // through the real Decode with the exact Text and flag (the handler
        // reads those two members).
        var msg = new SetBriefingText { Text = "Serpentis", FGenerateCivBriefing = true };
        var decoded = SetBriefingText.Decode(msg.Encode());

        Assert.Equal("Serpentis", decoded.Text);
        Assert.True(decoded.FGenerateCivBriefing);
    }
}
