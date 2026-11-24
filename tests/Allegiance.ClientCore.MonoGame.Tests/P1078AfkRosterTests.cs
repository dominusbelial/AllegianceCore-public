using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P1078AfkRosterTests
{
    [Fact]
    public void Whole_mission_ready_relays_drive_the_shared_classic_and_modern_roster()
    {
        using var world = new ClientWorld();
        world.Apply(new PlayerInfo { ShipId = 1, ISide = 0, CharacterName = "ActivePilot" });
        world.Apply(new PlayerInfo { ShipId = 2, ISide = 1, CharacterName = "IdlePilot" });
        world.MarkApplied();
        string Row()
        {
            var briefs = world.Ships.Values.ToDictionary(s => s.CharacterName!,
                s => TeamPaneLayout.BriefOf(world, s, _ => ""));
            return TeamPaneLayout.Build(world.ScoreRoster, world.RankTable, briefs, null, null,
                null, false, "ActivePilot").Single(l => l.Text.Contains("IdlePilot")).Text;
        }
        Assert.DoesNotContain("[AFK]", Row()); // unknown is not an AFK claim
        world.Apply(new PlayerReady { ShipId = 2, FReady = false });
        Assert.Contains("[AFK]", Row());
        world.Apply(new PlayerReady { ShipId = 2, FReady = true });
        Assert.DoesNotContain("[AFK]", Row());
        Assert.Equal(2, world.Events.PlayerReadyEchoes.Count);
    }
}
