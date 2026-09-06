using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// Pure-domain tests for <see cref="ClientWorld.Roster"/> — the
/// current-state (entity-table) half of PLAYER_READY(45), contrasted against
/// <see cref="ClientWorld.Events.PlayerReadyEchoes"/> (the append-only history
/// half). No sockets, no server: messages are constructed directly and fed
/// through <see cref="ClientWorld.Apply"/>. These prove the task's core
/// distinction: the roster upserts a single ready flag per ship id (last write
/// wins — "is ship X ready NOW?"), while the echo log accumulates every toggle.
/// </summary>
public class ClientWorldRosterTests
{
    private static PlayerReady ReadyToggle(short shipId, bool fReady) => new()
    {
        FReady = fReady,
        ShipId = shipId,
    };

    [Fact]
    public void Roster_IsNeverNull_AndStartsEmpty()
    {
        var world = new ClientWorld();
        Assert.NotNull(world.Roster);
        Assert.NotNull(world.Roster.ReadyByShipId);
        Assert.Empty(world.Roster.ReadyByShipId);
    }

    [Fact]
    public void Roster_Upserts_LastReadyStateWins_WhileHistoryAccumulates()
    {
        var world = new ClientWorld();
        const short id = 770;

        // Toggle the same ship ready, then not-ready.
        world.Apply(ReadyToggle(id, fReady: true));
        world.Apply(ReadyToggle(id, fReady: false));

        // ROSTER = current state → the LAST toggle wins (now not-ready).
        Assert.Single(world.Roster.ReadyByShipId);
        Assert.False(world.Roster.ReadyByShipId[id]);

        // HISTORY = append-only → both toggles remain, in arrival order.
        Assert.Equal(2, world.Events.PlayerReadyEchoes.Count);
        Assert.True(world.Events.PlayerReadyEchoes[0].FReady);
        Assert.False(world.Events.PlayerReadyEchoes[1].FReady);
    }

    [Fact]
    public void Roster_KeepsDistinctEntries_PerShipId()
    {
        var world = new ClientWorld();
        world.Apply(ReadyToggle(10, fReady: true));
        world.Apply(ReadyToggle(20, fReady: false));
        world.Apply(ReadyToggle(10, fReady: false));

        Assert.Equal(2, world.Roster.ReadyByShipId.Count);   // two distinct ships
        Assert.False(world.Roster.ReadyByShipId[10]);        // ship 10 flipped to not-ready
        Assert.False(world.Roster.ReadyByShipId[20]);
        // History has all three, independently of the current-state map.
        Assert.Equal(3, world.Events.PlayerReadyEchoes.Count);
    }

    [Fact]
    public void Roster_TogglingReadyAgain_ReplacesFlag_WithoutAddingDuplicate()
    {
        var world = new ClientWorld();
        const short id = 880;

        world.Apply(ReadyToggle(id, fReady: true));
        world.Apply(ReadyToggle(id, fReady: true));

        // The map is keyed by id, so a repeated ready-true is ONE entry.
        Assert.Single(world.Roster.ReadyByShipId);
        Assert.True(world.Roster.ReadyByShipId[id]);
        // …but the history legitimately records both announces.
        Assert.Equal(2, world.Events.PlayerReadyEchoes.Count);
    }
}
