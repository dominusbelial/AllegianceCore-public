using Allegiance.ClientCore.Session;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P1058 — what the hangar's "ready and launch" affordance may do right now.
///
/// Native rules this preserves (nothing here changes a game rule):
/// <list type="bullet">
/// <item>the ready click sends <c>PLAYER_READY</c> for the player's OWN ship
/// (<c>WinTrek/WinTrek.cpp:8192-8206</c>, <c>WinTrek/teamscreen.cpp:2750-2762</c>,
/// <c>clintlib/clintlib.h:333</c> — the flag itself lives on the local player info);</item>
/// <item>the launch is a <c>DOCKED</c> request the server only accepts once the mission is
/// <c>STAGE_STARTED</c> (<c>FedSrv/FedSrv.CPP:7031-7034</c>), so a ready click stays pending
/// for the mission start instead of being dropped;</item>
/// <item>the hangar's launch button needs the client's own ship
/// (<c>WinTrek/hangarscreen.cpp:145-157</c>: <c>trekClient.GetShip()</c>).</item>
/// </list>
///
/// Which ship id is "own" here matters: <c>JOINED_MISSION</c> already declares the player's
/// ship — <c>clintlib/appmsg.cpp:2150-2214</c> creates the local dummy ship
/// (<c>clintlib/clintlib.cpp:3860-3880</c>) and calls <c>pPlayerInfo-&gt;SetShip()</c>, so
/// <c>GetShipID()</c> works immediately — while <see cref="ClientWorld.MyShipId"/> (the
/// launch pose/cookie from <c>SET_CLUSTER</c>, <c>clintlib/appmsg.cpp:1009-1093</c>) arrives
/// later. Naming <c>JoinedShipId</c> when the pose has not arrived yet is therefore the
/// native identity, not a shortcut: the server re-stamps the relay with its own peer ship id
/// (<c>src/Allegiance.Server/Server.Dispatch.cs:1931-1957</c>).
///
/// The blocker is what the browser UI words and disables its button with, so a click that
/// cannot proceed is explained instead of silently returning.
/// </summary>
public static class HangarLaunch
{
    public enum Blocker
    {
        /// <summary>The ready/launch click can proceed.</summary>
        None,
        /// <summary>No owned session (closed, or retired by the join generator).</summary>
        NoSession,
        /// <summary>Between sockets: the auto-reconnect resume is re-dialing.</summary>
        Reconnecting,
        /// <summary>Neither JOINED_MISSION nor SET_CLUSTER has named a live own ship yet.</summary>
        NoShipAssigned,
    }

    public readonly record struct Readiness(Blocker Blocker, short? ShipId)
    {
        public bool CanLaunch => Blocker == Blocker.None && ShipId is not null;
    }

    /// <summary>Pure form (no world access) so the rule is testable on its own.</summary>
    public static Readiness Evaluate(bool hasSession, bool reconnecting, short? myShipId, short? joinedShipId, bool ownShipPresent)
    {
        if (!hasSession) return new(Blocker.NoSession, null);
        if (reconnecting) return new(Blocker.Reconnecting, null);
        short? own = myShipId ?? joinedShipId;
        return own is { } ship && ownShipPresent ? new(Blocker.None, ship) : new(Blocker.NoShipAssigned, null);
    }

    /// <summary>The page's adapter: the owned session's live world.</summary>
    public static Readiness Of(ConnectionSession? session)
    {
        if (session is null || session.IsDisposed) return new(Blocker.NoSession, null);
        short? mine = session.World.MyShipId;
        short? joined = session.World.JoinedShipId;
        short? own = mine ?? joined;
        return Evaluate(true, session.Reconnecting, mine, joined, own is { } id && session.World.Ships.ContainsKey(id));
    }
}
