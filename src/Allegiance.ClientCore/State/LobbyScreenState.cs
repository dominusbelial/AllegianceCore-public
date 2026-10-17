using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.State;

/// <summary>Shared lobby decisions. Read under ConnectionSession.Gate.</summary>
public static class LobbyScreenState
{
    // WinTrek/teamscreen.cpp:2962-2973: remain in the picker until actually
    // admitted to an active team. An existing ship alone is not admission.
    public static bool CanLeaveSideSelection(ClientWorld world, bool afterElimination) =>
        world.MyShipId is not null && (!afterElimination ||
            world.MySideId is >= 0 && world.IsSideActive(world.MySideId.Value));

    // WinTrek/teamscreen.cpp:1554-1559,2773-2793: every team's commander may
    // toggle team ready; clicking it also clears the commander's AFK flag.
    public readonly record struct ForceReadyAction(short ShipId, short SideId, bool ForceReady, bool ReadySelf);

    public static ForceReadyAction? ToggleTeamReady(ClientWorld world, bool ownReady)
    {
        if (!world.IsTeamLeader || world.MyShipId is not { } ship ||
            world.MySideId is not >= 0 || world.Mission is null ||
            world.Mission.Stage >= (int)MissionStage.Started)
            return null;
        short side = world.MySideId.Value;
        bool forced = world.ForceReadyBySideId.TryGetValue(side, out var f) && f;
        return new(ship, side, !forced, !ownReady);
    }
}
