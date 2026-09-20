using Allegiance.ClientCore.State;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P298 — the client-side surface of the <see cref="OwnShipKillBonusUpdate"/>
/// (212) owner-only stream: the renderer-only <see cref="ClientWorld"/> stores
/// the local ship's live Kill Bonus (P278) and hands it to the HUD frame. Pure
/// Apply tests (no server).
/// </summary>
public class ClientWorldKillBonusTests
{
    /// <summary>⭐ Applying the owner-only stream sets both the percent readout
    /// and the streak count, and overwrites wholesale each tick (the server
    /// always sends the current value, even 0).</summary>
    [Fact]
    public void Apply_sets_kill_bonus_and_overwrites_each_tick()
    {
        var world = new ClientWorld();
        Assert.Equal(0f, world.KillBonusPercent);
        Assert.Equal((short)0, world.KillBonusStreak);

        world.Apply(new OwnShipKillBonusUpdate { KillBonusPercent = 16.666666f, Streak = 2 });
        Assert.Equal(16.666666f, world.KillBonusPercent, 3);
        Assert.Equal((short)2, world.KillBonusStreak);

        // A later, different value overwrites — the stream is a snapshot, not a deltas.
        world.Apply(new OwnShipKillBonusUpdate { KillBonusPercent = 25f, Streak = 4 });
        Assert.Equal(25f, world.KillBonusPercent, 3);
        Assert.Equal((short)4, world.KillBonusStreak);

        // Back to 0 (a reset / fresh ship) is delivered faithfully, not ignored.
        world.Apply(new OwnShipKillBonusUpdate { KillBonusPercent = 0f, Streak = 0 });
        Assert.Equal(0f, world.KillBonusPercent, 3);
        Assert.Equal((short)0, world.KillBonusStreak);
    }

    /// <summary>⭐ A fresh <see cref="ClientWorld"/> starts at 0% / 0 streak —
    /// an honest "no bonus yet" readout, so the HUD gauge is truthful from boot.</summary>
    [Fact]
    public void Fresh_world_defaults_to_no_bonus()
    {
        var world = new ClientWorld();
        Assert.Equal(0f, world.KillBonusPercent);
        Assert.Equal((short)0, world.KillBonusStreak);
    }
}
