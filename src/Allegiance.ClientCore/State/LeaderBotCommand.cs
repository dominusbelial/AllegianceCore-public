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
/// selected target must be a <b>bot</b>. HUD-20 taught every mission ship a
/// PLAYERINFO (native seeds drones too, FedSrv.CPP:2129-2134), so the old
/// <c>ShipEntity.Player == null</c> discriminator no longer identifies a bot:
/// use native's own <c>dtidDrone</c> (NA = human, a drone type id = drone,
/// FedSrv.CPP:2088-2091) — the same rule the rig's
/// <c>BotManager.IsDrone</c> now uses — ON THE COMMANDER'S OWN SIDE. Anything else yields null — a non-leader sees nothing at all, not a
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
        // Deliberately `IsDead`, NOT `IsNotVisible` (P370): a DOCKED bot is
        // "gone" from view (IsGone=true) but must STAY commandable here — the
        // whole point of this resolver is to offer LAUNCH for a docked
        // teammate-bot. Only a corpse (or unknown) is truly un-commandable.
        if (ship.IsMine || ship.IsDead || ship.Latest is null) return null; // self / corpse / unknown
        // A human PLAYERINFO carries dtidDrone == NA (FedSrv.CPP:2088-2091);
        // a drone carries its type id. A ship whose seed has not landed yet is
        // treated as a bot, preserving pre-HUD-20 behaviour for that window.
        if (ship.Player is { DtidDrone: Allegiance.Core.Wire.PlayerInfo.NA }) return null;   // a real peer, not a bot
        if (world.MySideId is not { } mySide || ship.SideId != mySide) return null; // same-side teammate only
        bool docked = ship.StationId != -1;
        string label = docked
            ? $"LAUNCH {ship.CharacterName}  [X]"
            : $"DOCK {ship.CharacterName}  [X]";
        return new LeaderBotCommand(ship.ShipId, docked, label);
    }
}
