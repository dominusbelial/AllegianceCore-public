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

    // P759 — MISSION_BRIEFING_INFO(993) join-sync storage + wire round-trip.
    [Fact]
    public void MissionBriefingInfo_StoresCivBriefingInputs()
    {
        var world = new ClientWorld();
        Assert.Null(world.MissionBriefing);

        var info = new MissionBriefingInfo
        {
            SideName = "Alpha",
            CivName = "Iron Coalition",
            BriefingText = "Sector 1",
            CivList = ["Iron Coalition", "Belters"],
            NoWin = false,
            Conquest = true,
            ConquestPercent = 65,
        };
        world.Apply(info);

        Assert.NotNull(world.MissionBriefing);
        Assert.Equal("Alpha", world.MissionBriefing!.SideName);
        Assert.Equal("Iron Coalition", world.MissionBriefing.CivName);
        Assert.True(world.MissionBriefing.Conquest);
        Assert.Equal(65, world.MissionBriefing.ConquestPercent);
    }

    [Fact]
    public void MissionBriefingInfo_RoundTripsThroughItsRealCodec()
    {
        var msg = new MissionBriefingInfo
        {
            SideName = "Bravo",
            CivName = "Belters",
            BriefingText = "Home",
            CivList = ["Belters", "Rixian"],
            NoWin = false,
            Artifacts = true,
            ArtifactGoal = 4,
            Deathmatch = true,
            KillLimit = 25,
        };
        var decoded = MissionBriefingInfo.Decode(msg.Encode());

        Assert.Equal("Bravo", decoded.SideName);
        Assert.Equal("Belters", decoded.CivName);
        Assert.True(decoded.Artifacts);
        Assert.Equal(4, decoded.ArtifactGoal);
        Assert.True(decoded.Deathmatch);
        Assert.Equal(25, decoded.KillLimit);
    }
}
