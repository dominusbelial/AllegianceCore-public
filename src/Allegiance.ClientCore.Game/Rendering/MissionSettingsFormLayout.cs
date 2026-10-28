using System.Collections.Generic;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P853b — the READY-CHECK (pre-game lobby) overlay's GAME SETTINGS block: the
/// mission owner's live edit form over the fields P853's
/// <c>FM_CS_MISSIONPARAMS(113)</c> accepts.
///
/// <para>Native: the create-game/settings screen
/// (<c>WinTrek/newgamescreen.cpp</c>) is a full screen of combos and checkboxes;
/// its <c>OnButtonCreate</c> (<c>:1034</c>) validates with
/// <c>Invalid(true)</c> (<c>:1041</c>) and shows the returned string in a
/// message box, sending nothing on failure (<c>:1117-1120</c>). The port keeps
/// native's combo value lists (cited where they are defined, on
/// <see cref="MissionSettingsForm"/>) and the same validation string, but lays
/// the fields out as one cursor row per setting — the established port pane idiom
/// (see <see cref="TeamCivPickerLayout"/>) — instead of a separate screen. The
/// rules and the message are <see cref="MissionSettingsForm"/>'s; this file only
/// draws them.</para>
///
/// <para>Works in both HUD skins: the ready-check overlay is the port's single
/// shared panel (Classic and Modern differ in the in-flight HUD, not here) and
/// every string is printable ASCII (<see cref="ScoreboardLayout.AsciiSafe"/>).</para>
/// </summary>
public static class MissionSettingsFormLayout
{
    /// <summary>Build the rows appended to the ready-check overlay.
    /// <paramref name="form"/> is null when the local player is not the mission
    /// owner (then nothing is shown at all — native hides the settings button,
    /// <c>teamscreen.cpp:1586-1589</c>). <paramref name="error"/> is the last
    /// refusal (the <c>Invalid(true)</c> string) to display under the field
    /// list.</summary>
    public static IReadOnlyList<ReadyCheckLayout.Row> Build(ClientWorld world, MissionSettingsForm? form, string? error)
    {
        var rows = new List<ReadyCheckLayout.Row>();
        if (form is null)
        {
            // Owner-only affordance: a non-owner sees no hint at all.
            return rows;
        }

        rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Title, "GAME SETTINGS"));
        rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Hint,
            "UP/DOWN SELECT   LEFT/RIGHT CHANGE   ENTER SEND   G CLOSE"));

        for (int i = 0; i < MissionSettingsForm.Fields.Length; i++)
        {
            var field = MissionSettingsForm.Fields[i];
            bool selected = i == form.Cursor;
            string text = $"{(selected ? '>' : ' ')} {AsciiSafe(MissionSettingsForm.Label(field)),-20} " +
                          AsciiSafe(form.Format(field));
            rows.Add(new ReadyCheckLayout.Row(ReadyCheckLayout.Kind.Item, text, Highlight: selected));
        }

        if (error is { Length: > 0 })
            rows.Add(ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Error, AsciiSafe(error)));
        return rows;
    }

    /// <summary>The collapsed one-line hint shown while the form is closed (the
    /// port's stand-in for native's settings button).</summary>
    public static IReadOnlyList<ReadyCheckLayout.Row> Hint()
        => [ReadyCheckLayout.Row.Of(ReadyCheckLayout.Kind.Hint, "G  GAME SETTINGS (MISSION OWNER)")];

    private static string AsciiSafe(string s) => ReadyCheckLayout.AsciiSafe(s);
}
