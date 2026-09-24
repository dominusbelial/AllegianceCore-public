namespace Allegiance.ClientCore.State;

/// <summary>
/// P366 — the leader-only bot dock/launch command, plus the pure resolver that
/// decides when it is offered. The MonoGame HUD shows <see cref="Label"/> and
/// the <c>X</c>-key handler acts on <see cref="TargetShipId"/>/<see cref="TargetDocked"/>;
/// the resolver is a pure function of <see cref="ClientWorld"/> + the selected
/// ship so it is headlessly testable and the live proof can drive it directly.
/// </summary>
public readonly record struct LeaderBotCommand(short TargetShipId, bool TargetDocked, string Label);

/// <summary>
/// P366 — decide whether the local player may command the selected ship to dock
/// / launch. Every gate mirrors native (WinTrek.cpp:4349-4368): the local player
/// must be the elected team leader (<see cref="ClientWorld.IsTeamLeader"/>), the
/// selected target must be a <b>bot</b> (no real peer's PLAYERINFO has ever been
/// attached to it — <c>ShipEntity.Player == null</c>, the same discriminator
/// native's <c>GetPilotType() &lt; c_ptPlayer</c> expresses) ON THE COMMANDER'S
/// OWN SIDE. Anything else yields null — a non-leader sees nothing at all, not a
/// disabled button. The label reflects the bot's real docked state (from its
/// SHIP_STATUS): "DOCK" while flying, "LAUNCH" while docked — the single
/// toggling context item native presents.</summary>
public static class LeaderBotCommandResolver
{
    public static LeaderBotCommand? Resolve(ClientWorld world, short? selectedShipId)
    {
        if (!world.IsTeamLeader) return null;                       // leader-only (native gate)
        if (selectedShipId is not { } id) return null;              // a ship must be selected
        if (!world.Ships.TryGetValue(id, out var ship)) return null; // still known
        if (ship.IsMine || ship.IsDead || ship.Latest is null) return null; // self / corpse / unknown
        if (ship.Player is not null) return null;                   // a real peer, not a bot
        if (world.MySideId is not { } mySide || ship.SideId != mySide) return null; // same-side teammate only
        bool docked = ship.StationId != -1;
        string label = docked
            ? $"LAUNCH {ship.CharacterName}  [X]"
            : $"DOCK {ship.CharacterName}  [X]";
        return new LeaderBotCommand(ship.ShipId, docked, label);
    }
}
