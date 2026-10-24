using System.Collections.Generic;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P802 — the READY-CHECK overlay's TEAM CIVILIZATION block: the side's current
/// civ (name in the text rows, native <c>icon&lt;civ&gt;bmp</c> beside the panel)
/// and, for the side's team leader before the game starts, the selector over
/// every civilization the mission core offers plus <c>RANDOM</c>.
///
/// <para>Native: the picker is the TEAM SETTINGS dialog's civilization combo
/// (<c>WinTrek/teamscreen.cpp:860-1104</c>, item list <c>:898-916</c>), it sends
/// <c>FM_CS_CHANGE_TEAM_CIV(51)</c> from <c>OnCivChosen</c> (<c>:3049-3065</c>),
/// and the side's civ art is <c>icon&lt;civ.GetIconName()&gt;bmp</c>
/// (<c>:586-591</c>; the lobby watermark <c>:2479-2492</c>). The server admits
/// the change only from the side's leader at <c>STAGE_NOTSTARTED</c>
/// (<c>FedSrv/FedSrv.CPP:7344-7366</c>). The <c>RANDOM</c> entry is native's
/// <c>RANDOM_ID</c> random-faction option (<c>Igc/igc.h:501</c>,
/// <c>teamscreen.cpp:1007-1075</c>). Works in both HUD skins: the overlay uses
/// the same HUD sprite fonts in Classic and Modern (skin choice only swaps the
/// in-flight HUD renderer), and every string is printable ASCII
/// (<see cref="ScoreboardLayout.AsciiSafe"/>).
/// </summary>
public static class TeamCivPickerLayout
{
    /// <summary>Build the rows appended to the ready-check overlay. The current
    /// civ line is always shown; the selector rows only while
    /// <paramref name="open"/> AND the local player is allowed to pick
    /// (<see cref="TeamCivPicker.CanPick"/>), so a non-leader — or anyone after
    /// the start — sees the civ read-only and live-updating.</summary>
    public static IReadOnlyList<ReadyCheckLayout.Row> Build(ClientWorld world, bool open, int index)
    {
        var rows = new List<ReadyCheckLayout.Row>();
        bool canPick = TeamCivPicker.CanPick(world);
        rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Info,
            "TEAM CIV: " + AsciiSafe(TeamCivPicker.DisplayName(world))));

        if (!canPick)
        {
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Muted,
                "Only the team leader can change it, before the game starts."));
            return rows;
        }
        var options = TeamCivPicker.Options(world.MissionCore);
        if (options.Count == 0)
        {
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Muted, "WAITING FOR CIVILIZATION DATA..."));
            return rows;
        }
        if (!open)
        {
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Hint, "C  CHANGE TEAM CIVILIZATION"));
            return rows;
        }

        rows.Add(ReadyCheckLayout.Row.Blank());
        rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Title, "SELECT CIVILIZATION"));
        int sel = index < 0 ? 0 : index % options.Count;
        for (int i = 0; i < options.Count; i++)
        {
            bool isSel = i == sel;
            rows.Add(new ReadyCheckLayout.Row(ReadyCheckLayout.Kind.Item,
                $"{(isSel ? '>' : ' ')} {AsciiSafe(options[i].Name)}", Highlight: isSel));
        }
        rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Hint,
            "UP/DOWN SELECT   ENTER CONFIRM   C CLOSE"));
        return rows;
    }

    /// <summary>The picker's default cursor: the current civ, or the trailing
    /// <c>RANDOM</c> entry when the side already runs a random faction, or the
    /// first option when the current civ is not offered (a special civ).</summary>
    public static int InitialIndex(ClientWorld world)
    {
        var options = TeamCivPicker.Options(world.MissionCore);
        if (options.Count == 0) return 0;
        short? current = TeamCivPicker.CurrentCivId(world);
        if (world.MySideId is { } side && world.SideCivilizations.TryGetValue(side, out var civ) && civ.Random)
            return options.Count - 1;
        int index = TeamCivPicker.IndexOf(options, current);
        return index < 0 ? 0 : index;
    }

    private static string AsciiSafe(string s) => ReadyCheckLayout.AsciiSafe(s);
}
