using Allegiance.Core;
using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// HUD-8b — the red order turn arrow reads native <c>c_cmdCurrent</c> (the
/// current TARGET COMMAND, <c>SetTarget</c> WinTrek.cpp:8823-8852 and
/// indicator.cpp:411-457), NOT the client-local autopilot target
/// (<c>Game1._autoTarget</c>). These pin the pure mapping a selected target
/// becomes when it is written into the current-command slot.
/// </summary>
public class Hud8bCurrentCommandTests
{
    [Fact]
    public void CurrentCommandState_maps_a_ship_selection_into_the_cmd_current_slot()
    {
        var current = Game1.CurrentCommandState(new Game1.AutopilotTarget(Game1.TargetKind.Ship, 42, "bob"));

        Assert.NotNull(current);
        Assert.Equal(IgcObjectType.Ship, current.Value.TargetType);
        Assert.Equal((short)42, current.Value.TargetId);
        // A plain T-selection carries no concrete action yet (native SetTarget's
        // c_cidDefault → GetDefaultOrder fallback, WinTrek.cpp:8827-8833).
        Assert.Equal(OrderChange.CidNone, current.Value.CommandId);
    }

    [Fact]
    public void CurrentCommandState_maps_every_orderable_kind_and_preserves_the_action()
    {
        Assert.Equal(IgcObjectType.Station, Game1.CurrentCommandState(
            new Game1.AutopilotTarget(Game1.TargetKind.Station, 7, "gar", OrderChange.CidGoto))!.Value.TargetType);
        Assert.Equal(IgcObjectType.Asteroid, Game1.CurrentCommandState(
            new Game1.AutopilotTarget(Game1.TargetKind.Asteroid, 8, "rock", OrderChange.CidBuild))!.Value.TargetType);
        Assert.Equal(IgcObjectType.Warp, Game1.CurrentCommandState(
            new Game1.AutopilotTarget(Game1.TargetKind.Warp, 9, "aleph"))!.Value.TargetType);
        Assert.Equal(IgcObjectType.Probe, Game1.CurrentCommandState(
            new Game1.AutopilotTarget(Game1.TargetKind.Probe, 10, "probe"))!.Value.TargetType);
        Assert.Equal(IgcObjectType.Treasure, Game1.CurrentCommandState(
            new Game1.AutopilotTarget(Game1.TargetKind.Treasure, 11, "treasure"))!.Value.TargetType);

        // A command-selected target keeps its concrete action id.
        var attack = Game1.CurrentCommandState(new Game1.AutopilotTarget(Game1.TargetKind.Ship, 12, "enemy", OrderChange.CidAttack));
        Assert.Equal(OrderChange.CidAttack, attack!.Value.CommandId);
    }

    [Fact]
    public void CurrentCommandState_returns_null_for_null_or_unmappable_targets()
    {
        Assert.Null(Game1.CurrentCommandState(null));
        Assert.Null(Game1.CurrentCommandState(new Game1.AutopilotTarget(Game1.TargetKind.Cluster, 1, "sector")));
    }
}
