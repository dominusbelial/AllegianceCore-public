using System.Collections;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;

namespace Allegiance.ClientCore.Tests;

public sealed partial class BotFixRegressionTests
{
    [SkippableFact]
    public void P927_Three_blind_sides_register_targetless_finales_without_sharing_enemy_stations()
    {
        Require.Artwork();
        using var a = Session(true); using var b = Session(true); using var c = Session(true);
        var sessions = new[] { a, b, c };
        var bots = sessions.Select((cs, i) => Bot(cs, (short)(i + 1))).ToArray();
        for (short side = 0; side < 3; side++)
        {
            P896World(sessions[side].World, (short)(side + 1), side, 1, [P896Garrison((short)(10 + side), side, 1)]);
            Set(bots[side], "Side", side); Set(bots[side], "Name", $"p927-{side}");
        }
        var manager = Manager(bots);
        Set(manager, "<Probe>k__BackingField", a);
        a.World.Apply(new SideRoster { Sides = [new(0, 0, 0, "A"), new(1, 1, 0, "B"), new(2, 2, 0, "C")] });
        manager.ConfigureFinale(new(Side: "all"), ["p927-0", "p927-1", "p927-2"]);
        Assert.Null(Get<object?>(manager, "_pendingFinales"));
        var run = Get<object>(bots[0], "Finale");
        var plans = Get<IDictionary>(run, "Plans"); Assert.Equal(3, plans.Count);
        for (short side = 0; side < 3; side++) Assert.Equal((short)-1, Get<short>(plans[side]!, "TargetId"));
        a.World.Apply(new ExportStation { Station = P896Garrison(21, 1, 4) });
        a.World.Apply(new ExportStation { Station = P896Garrison(22, 2, 2) });
        var typedBots = Array.CreateInstance(BotType, bots.Length);
        for (int i = 0; i < bots.Length; i++) typedBots.SetValue(bots[i], i);
        Call("ResolveFinaleTargets", manager, run, typedBots);
        Assert.Equal((short)22, Get<short>(plans[(short)0]!, "TargetId"));
        Assert.Equal((short)-1, Get<short>(plans[(short)1]!, "TargetId"));
        a.World.Apply(new SideInactive { SideId = 2, BActive = false });
        Call("ResolveFinaleTargets", manager, run, typedBots);
        Assert.Equal((short)21, Get<short>(plans[(short)0]!, "TargetId"));
    }
}
