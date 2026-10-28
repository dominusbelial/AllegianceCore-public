using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// P853b — the team lobby's TEAM NAME edit, as pure state (no MonoGame): the
/// pre-game half of native's <c>TeamSettingsDialogPopup</c>
/// (<c>WinTrek/teamscreen.cpp:856-1104</c>).
///
/// <para><b>Native rule.</b> The team-settings button is shown only to the side's
/// own team leader (<c>m_pbuttonTeamSettings-&gt;SetHidden(!IsTeamLeader() ||
/// m_sideCurrent != GetSideID())</c>, <c>teamscreen.cpp:1592-1593</c>) and
/// disabled while team settings are locked (<c>:1594</c>). The dialog's name
/// edit pane holds <c>c_cbName - 1 = 24</c> characters (<c>:897</c>), is hidden in
/// a squad game in favour of the squad combo (<c>:960-965</c>), and its
/// <c>OnButtonOk</c> (<c>:1035-1082</c>) sends <c>FM_CS_SET_TEAM_INFO(15)</c> with
/// <c>squadID = NA</c> plus the typed name for a non-squad game (the squad id in
/// a squad game, <c>:1049-1058</c>). The server accepts the rename only while
/// <c>STAGE_NOTSTARTED &amp;&amp; !bLockTeamSettings</c> and from the side's seated
/// leader (<c>FedSrv/FedSrv.CPP:7430-7467</c>).</para>
///
/// <para><b>Port additions.</b> (1) The edit is an inline row on the team lobby
/// (ready-check) pane instead of a modal dialog — the message, the length cap and
/// the permission/stage rules are unchanged. (2) An empty or whitespace-only name
/// is refused with an inline message instead of being sent: native would send the
/// empty string and leave the side unnamed, which the port's pane cannot show
/// (P853's <c>SideNameOverride</c> ignores empty names). (3) A squad game hides
/// the name edit and shows the squad name read-only — the port has no client-side
/// squad membership list, so it must not send a squad re-point it cannot
/// represent (native's <c>squadComboPane</c> lists the player's memberships,
/// <c>teamscreen.cpp:993-1005</c>).</para>
/// </summary>
public static class TeamNameEdit
{
    /// <summary>Native <c>c_cbName - 1</c> = 24 characters
    /// (<c>WinTrek/teamscreen.cpp:895</c>, <c>_Utility/Utility.h:60,64</c>).</summary>
    public const int MaxLength = SetTeamInfo.SideNameCapacity - 1;

    /// <summary>The side's squad id when it has none — native <c>NA</c>, the
    /// value that selects the RENAME branch of <c>OnButtonOk</c>
    /// (<c>teamscreen.cpp:1049-1058</c>) and of the server handler
    /// (<c>FedSrv.CPP:7461</c>).</summary>
    public const int NoSquad = -1;

    /// <summary>The C→S message the edit produces.</summary>
    public readonly record struct Plan(short SideId, int SquadId, string Name);

    /// <summary>May the local player edit the side's name right now? Leader of the
    /// own side (<c>teamscreen.cpp:1592-1593</c>), team settings unlocked
    /// (<c>:1594</c>), mission not started (<c>FedSrv.CPP:7433-7436</c>) and not a
    /// squad game (<c>teamscreen.cpp:960-965</c>).</summary>
    public static bool CanEdit(ClientWorld world) =>
        world.IsTeamLeader
        && world.MySideId is >= 0
        && world.Mission is { Stage: (int)MissionStage.NotStarted } mission
        && !mission.LockTeamSettings
        && !mission.SquadGame;

    /// <summary>The reason the affordance is unavailable, when the local player IS
    /// the side's leader (so a leader sees why the row is inert instead of having
    /// the click vanish — the brief's "hide/disable it" rule). Null when the
    /// player may edit, or is not a leader at all (then the row simply shows the
    /// name).</summary>
    public static string? BlockedReason(ClientWorld world)
    {
        if (!world.IsTeamLeader || world.MySideId is not >= 0) return null;
        if (world.Mission is not { } mission) return "WAITING FOR MISSION...";
        if (mission.Stage >= (int)MissionStage.Started) return "GAME ALREADY STARTED";
        if (mission.LockTeamSettings) return "TEAM SETTINGS LOCKED";
        if (mission.SquadGame) return "SQUAD GAME - NAME COMES FROM THE SQUAD";
        return null;
    }

    /// <summary>The own side's current name: the live rename echo when the server
    /// has re-announced one (P853 <see cref="ClientWorld.SideNameOverride"/>),
    /// else the SIDE_ROSTER name, else <c>SIDE n</c>.</summary>
    public static string CurrentName(ClientWorld world)
    {
        if (world.MySideId is not { } side) return "NO SIDE";
        if (world.SideNameOverride(side) is { } renamed) return renamed;
        if (world.SideRoster is { } roster)
            foreach (var record in roster.Sides)
                if (record.SideId == side && record.Name.Length > 0) return record.Name;
        return "SIDE " + side;
    }

    /// <summary>Normalise typed input the way the edit control would: no control
    /// characters, no surrounding whitespace, at most <see cref="MaxLength"/>
    /// characters (native's <c>SetMaxLength(c_cbName - 1)</c>,
    /// <c>teamscreen.cpp:895</c>; the server truncates to the same 24 anyway,
    /// <c>fsmission.cpp:1569-1572</c>).</summary>
    public static string Sanitize(string typed)
    {
        if (string.IsNullOrEmpty(typed)) return "";
        var sb = new System.Text.StringBuilder(typed.Length);
        foreach (char c in typed)
            if (c >= 32 && c != 127) sb.Append(c);
        string cleaned = sb.ToString().Trim();
        return cleaned.Length <= MaxLength ? cleaned : cleaned[..MaxLength].TrimEnd();
    }

    /// <summary>Build the message for <paramref name="typed"/>. Returns false with
    /// a player-facing <paramref name="error"/> when the edit cannot be sent:
    /// not permitted (<see cref="CanEdit"/>), or the sanitised name is empty.</summary>
    public static bool TryPlan(ClientWorld world, string typed, out Plan plan, out string? error)
    {
        plan = default;
        if (!CanEdit(world))
        {
            error = BlockedReason(world) ?? "ONLY THE TEAM LEADER CAN RENAME THE SIDE";
            return false;
        }
        string name = Sanitize(typed);
        if (name.Length == 0)
        {
            error = "Type a team name first.";
            return false;
        }
        plan = new Plan(world.MySideId!.Value, NoSquad, name);
        error = null;
        return true;
    }
}
