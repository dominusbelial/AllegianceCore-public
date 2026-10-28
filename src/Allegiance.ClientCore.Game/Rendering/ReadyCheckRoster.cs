using System.Collections.Generic;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P282 — pure gathering of the READY-CHECK display inputs from the live
/// <see cref="ClientWorld"/> (no MonoGame types, headless-testable): the own
/// side's display name, the same-side player roster with each player's current
/// ready flag, and the own side's aggregate ready state. Mirrors how the other
/// overlays reduce the world into display data (see
/// <see cref="ScoreboardLayout"/>), but spans the three real ready sources:
/// <list type="bullet">
/// <item>the per-ship ready flags in <see cref="ClientWorld.Roster"/> (from the
/// real PLAYER_READY(45) relays the server sends to the whole side incl. the
/// sender, <c>Server.cs</c>);</item>
/// <item>same-side teammate ship entities in <see cref="ClientWorld.Ships"/>
/// (their EXPORTs, for names of players who have not toggled ready yet);</item>
/// <item>the per-side aggregate <c>TEAM_READY(46)</c> in
/// <see cref="ClientWorld.TeamReadyBySideId"/>.</item>
/// </list>
/// </summary>
public static class ReadyCheckRoster
{
    /// <summary>Gather the display inputs for the ready-check screen.
    /// <paramref name="ownReady"/> is the local player's own toggle (tracked by
    /// Game1), which seeds the self row even before the server's relay of the
    /// player's first toggle arrives. Aggregate ready is null until the first
    /// <c>TEAM_READY(46)</c> for the own side arrives.</summary>
    public static (string SideName, IReadOnlyList<ReadyCheckLayout.PlayerRow> Players, bool? AggregateReady)
        Gather(ClientWorld world, object gate, bool ownReady)
    {
        short mySide;
        short me;

        // Snapshot every world read under the session's Gate lock (the network
        // thread mutates world.Ships / Roster / SideRoster concurrently with the
        // render thread; enumerating them unlocked is a pre-existing crash that
        // P306's high-peer-load runs trigger readily — "Collection was
        // modified during enumeration" in ReadyCheckRoster.Gather). Build the
        // display list from the snapshot afterwards.
        string sideName;
        var ships = new List<(short Sid, short SideId, string Name)>();
        Dictionary<short, bool> roster;
        bool? agg;
        lock (gate)
        {
            mySide = world.MySideId ?? -1;
            me = world.MyShipId ?? -1;
            sideName = "SIDE " + mySide;
            if (world.SideRoster is { } sr)
            {
                foreach (var s in sr.Sides)
                {
                    if (s.SideId == mySide && s.Name.Length > 0) { sideName = s.Name; break; }
                }
            }
            // P853b — the server's live rename (SET_TEAM_INFO with squadID NA,
            // fsmission.cpp:1564-1600) wins over the creation-time SIDE_ROSTER
            // name, exactly as native applies it to the IGC side
            // (appmsg.cpp:2541-2550). Same source the in-flight team pane reads
            // (P853, TeamPaneLayout), so the lobby and the pane never disagree.
            if (mySide >= 0 && world.SideNameOverride(mySide) is { } renamed) sideName = renamed;
            foreach (var (sid, ent) in world.Ships)
                ships.Add((sid, ent.SideId, ent.CharacterName ?? ""));
            roster = new Dictionary<short, bool>(world.Roster.ReadyByShipId);
            agg = world.TeamReadyBySideId.TryGetValue(mySide, out var a) ? a : null;
        }

        var players = new List<ReadyCheckLayout.PlayerRow>();
        var seen = new HashSet<short>();

        // Same-side teammates whose ship entity the client knows (name + side).
        // Self's own entity (SET_CLUSTER) has no SideId, so self is added
        // explicitly below.
        foreach (var (sid, sid2, name) in ships)
        {
            // A known enemy's old ready relay must not reappear as an unnamed
            // teammate after a side change (teamscreen.cpp:413-425).
            seen.Add(sid);
            if (sid == me) continue;
            if (sid2 != mySide) continue;   // same-side only (teammate EXPORTs set SideId)
            bool ready = roster.TryGetValue(sid, out var r) && r;
            players.Add(new ReadyCheckLayout.PlayerRow(string.IsNullOrEmpty(name) ? "sid" + sid : name, ready, false));
        }

        // Self is always present, driven by the local toggle (and later by the
        // server's relay of the same flag).
        seen.Add(me);
        players.Add(new ReadyCheckLayout.PlayerRow("you", ownReady, true));

        // Any roster (PLAYER_READY-relay) entries with no matching ship entity
        // yet — show them by ship id so the roster never silently drops a player
        // who has toggled but whose export is still in flight.
        foreach (var (sid, r) in roster)
        {
            if (seen.Contains(sid)) continue;
            seen.Add(sid);
            players.Add(new ReadyCheckLayout.PlayerRow("sid" + sid, r, false));
        }

        return (sideName, players, agg);
    }

    /// <summary>P864d — the team lobby's member rows for the TEAM SCREEN plus the
    /// first pending join request on the LOCAL player's own side (the request the
    /// leader's Accept button answers, native <c>OnButtonAccept</c>,
    /// <c>WinTrek/teamscreen.cpp:2998-3013</c>). Call under the session gate, like
    /// <see cref="Gather"/>: the network thread mutates
    /// <c>world.Ships</c>/<c>PendingPositionRequests</c> concurrently. Names come
    /// from the normal player-info traffic (<see cref="ClientWorld.TeamLobbyMembers"/>);
    /// the requested side's NAME is resolved from the same roster the rest of the
    /// overlay uses (native's player-row status "Requesting",
    /// <c>teamscreen.cpp:495-497</c>).</summary>
    public static (IReadOnlyList<ReadyCheckLayout.LobbyRow> Rows, short? FirstOwnSideRequest)
        GatherLobby(ClientWorld world)
    {
        var rows = new List<ReadyCheckLayout.LobbyRow>();
        short? firstRequest = null;
        short mySide = world.MySideId ?? -1;
        bool leader = world.IsTeamLeader;
        foreach (var member in world.TeamLobbyMembers())
        {
            string? wanted = member.RequestingSideId is { } side ? SideNameOf(world, side) : null;
            rows.Add(new ReadyCheckLayout.LobbyRow(member.Name, member.IsSelf, wanted));
            if (leader && member.RequestingSideId == mySide && firstRequest is null)
                firstRequest = member.ShipId;
        }
        return (rows, firstRequest);
    }

    /// <summary>P864d — a side's live display name for the lobby list: the
    /// server's SET_TEAM_INFO rename when there is one (P853,
    /// <see cref="ClientWorld.SideNameOverride"/>), else the SIDE_ROSTER
    /// creation-time name, else the native <c>SIDE &lt;id&gt;</c> fallback.</summary>
    public static string SideNameOf(ClientWorld world, short sideId)
    {
        if (sideId >= 0 && world.SideNameOverride(sideId) is { } renamed) return renamed;
        if (world.SideRoster is { } roster)
            foreach (var side in roster.Sides)
                if (side.SideId == sideId && side.Name.Length > 0) return side.Name;
        return "SIDE " + sideId;
    }
}
