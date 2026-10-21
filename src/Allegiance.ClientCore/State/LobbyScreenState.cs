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

    /// <summary>P761 — one row of the team lobby's configuration controls: the
    /// native button pane's Visible/Enabled/Checked triple per control
    /// (UpdateButtonStates, WinTrek/teamscreen.cpp:1554-1573).</summary>
    public readonly record struct TeamLobbyControl(bool Visible, bool Enabled, bool Checked);

    /// <summary>P761 — the full visibility/enable matrix of the TEAM LOBBY's
    /// configuration controls, exactly native's UpdateButtonStates
    /// (WinTrek/teamscreen.cpp:1554-1573):
    /// <list type="bullet">
    /// <item><b>Team Ready</b> — visible only to the leader of the side being
    /// viewed (<c>m_sideCurrent == GetSideID()</c>, :1558-1559); enabled only
    /// before the mission is in progress (:1560-1561); checked while in
    /// progress or force-ready (:1562-1563).</item>
    /// <item><b>Auto-Accept</b> — same visibility; enabled while not in
    /// progress AND the game-open lock is off (<c>bLockGameOpen</c>,
    /// :1564-1566); mirrors <c>SideAutoAccept</c> (:1566).</item>
    /// <item><b>Lock Lobby</b> — never hidden; enabled for the MISSION OWNER
    /// while the game-open lock is off (:1568-1569); mirrors
    /// <c>GetLockLobby()</c> (:1570).</item>
    /// <item><b>Lock Sides</b> — owner-only (:1572); mirrors
    /// <c>GetLockSides()</c> (:1573).</item>
    /// </list>
    /// <paramref name="viewedSideId"/> is the side whose pane is on screen
    /// (native's m_sideCurrent); the port's ready-check views the own side, so
    /// callers pass <see cref="ClientWorld.MySideId"/>.</summary>
    public static TeamLobbyControlsState BuildTeamControls(ClientWorld world, short? viewedSideId)
    {
        bool inProgress = world.Mission is { } m && m.Stage >= (int)MissionStage.Started;
        bool lockGameOpen = world.Mission?.LockGameOpen ?? false;
        bool leaderOfViewedSide = world.IsTeamLeader && viewedSideId is { } v && v == world.MySideId;

        bool teamReadyChecked = inProgress;
        if (!teamReadyChecked && viewedSideId is { } side
            && world.ForceReadyBySideId.TryGetValue(side, out var forced))
            teamReadyChecked = forced;

        bool autoAccept = viewedSideId is { } s2 && world.AutoAcceptBySideId.TryGetValue(s2, out var aa) && aa;

        bool owner = world.AmMissionOwner;
        return new TeamLobbyControlsState(
            TeamReady: new TeamLobbyControl(
                Visible: leaderOfViewedSide,
                Enabled: !inProgress && world.IsTeamLeader,
                Checked: teamReadyChecked),
            AutoAccept: new TeamLobbyControl(
                Visible: leaderOfViewedSide,
                Enabled: !inProgress && world.IsTeamLeader && !lockGameOpen,
                Checked: autoAccept),
            LockLobby: new TeamLobbyControl(
                Visible: true,
                Enabled: owner && !lockGameOpen,
                Checked: world.LobbyLocked),
            LockSides: new TeamLobbyControl(
                Visible: true,
                Enabled: owner,
                Checked: world.SidesLocked));
    }

    /// <summary>P761 — the matrix + the one-line-per-control display strings
    /// the ready-check overlay appends (the port's stand-in for native's
    /// button row). Controls that are hidden natively are omitted from the
    /// display entirely.</summary>
    public sealed record TeamLobbyControlsState(
        TeamLobbyControl TeamReady,
        TeamLobbyControl AutoAccept,
        TeamLobbyControl LockLobby,
        TeamLobbyControl LockSides)
    {
        /// <summary>The overlay lines: one per VISIBLE control, mirroring the
        /// native button row's states (checked → [x], off → [ ], disabled →
        /// suffix).</summary>
        public IReadOnlyList<string> DisplayLines()
        {
            var lines = new List<string>();
            void Add(string label, TeamLobbyControl c, string hint)
            {
                if (!c.Visible) return;
                lines.Add($"{(c.Checked ? "[x]" : "[ ]")} {label}{(c.Enabled ? "" : " (locked)")}  {hint}");
            }
            Add("TEAM READY", TeamReady, "O");
            Add("AUTO-ACCEPT", AutoAccept, "U");
            Add("LOCK LOBBY", LockLobby, "N");
            Add("LOCK SIDES", LockSides, "H");
            return lines;
        }
    }
}
