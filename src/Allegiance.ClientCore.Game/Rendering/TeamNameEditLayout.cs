using System.Collections.Generic;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P853b — the READY-CHECK (team lobby) overlay's TEAM NAME block: the own
/// side's live name, and — for that side's team leader, before the start, with
/// team settings unlocked and in a non-squad game — the edit affordance that
/// produces native's <c>FM_CS_SET_TEAM_INFO(15)</c> rename.
///
/// <para>Native: the name lives in the team-settings dialog's
/// <c>teamNameEditPane</c> (<c>WinTrek/teamscreen.cpp:887</c>,
/// <c>SetMaxLength(c_cbName - 1)</c> <c>:897</c>), the dialog is reached from a
/// button shown to the viewed side's leader only (<c>:1592-1593</c>) and
/// disabled while team settings are locked (<c>:1594</c>), and
/// <c>OnButtonOk</c> (<c>:1044-1075</c>) sends the typed name with
/// <c>squadID = NA</c> in a non-squad game. The state rules live in
/// <see cref="TeamNameEdit"/>; this file only lays the rows out.</para>
///
/// <para>Works in both HUD skins: the ready-check overlay is the port's single
/// shared panel (same sprite fonts, same colours — skin choice swaps the
/// in-flight HUD renderer, not this one), and every string is printable ASCII
/// (<see cref="ScoreboardLayout.AsciiSafe"/>).</para>
/// </summary>
public static class TeamNameEditLayout
{
    /// <summary>Build the rows appended to the ready-check overlay.
    /// <paramref name="editing"/> is the inline edit mode (typing into
    /// <paramref name="buffer"/>), <paramref name="error"/> the last refusal to
    /// show (the edit stays open so the player can fix it).</summary>
    public static IReadOnlyList<ReadyCheckLayout.Row> Build(ClientWorld world, bool editing,
        string buffer, string? error)
    {
        var rows = new List<ReadyCheckLayout.Row>();
        string current = AsciiSafe(TeamNameEdit.CurrentName(world));

        if (!editing)
        {
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Info, "TEAM NAME: " + current));
            if (TeamNameEdit.CanEdit(world))
                rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Hint, "T  CHANGE TEAM NAME"));
            else if (TeamNameEdit.BlockedReason(world) is { } why)
                // A leader sees WHY the row is inert (the server would silently
                // drop the message: FedSrv.CPP:7433-7436,7560).
                rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Muted, why));
            return rows;
        }

        // The edit line: the typed text with the caret, exactly what the native
        // edit pane shows. The buffer is already capped at 24 characters
        // (teamscreen.cpp:897), so it always fits char[c_cbName].
        rows.Add(new ReadyCheckLayout.Row(ReadyCheckLayout.Kind.Item,
            "TEAM NAME: " + AsciiSafe(buffer) + "_", Highlight: true));
        rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Hint,
            $"TYPE A NAME (MAX {TeamNameEdit.MaxLength})   ENTER APPLY   ESC CANCEL   BACKSPACE DELETE"));
        if (error is { Length: > 0 })
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Error, AsciiSafe(error)));
        return rows;
    }

    private static string AsciiSafe(string s) => ReadyCheckLayout.AsciiSafe(s);
}
