using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P806 — the intro screen's OPTIONS MENU as a native menu tree
/// (<c>WinTrek::ShowOptionsMenu</c>, <c>WinTrek/WinTrek.cpp:4429-4442</c>, and
/// the submenus <c>GetSubMenu</c> builds at <c>:4502-4590</c>).
///
/// <para><b>Native shape.</b> The root menu has four entries; two of them open
/// SUBMENUS rather than toggling anything: <c>idmSoundOptions</c> ("Sound
/// Options", 'S') and <c>idmGameOptions</c> ("Game Options", 'G') both pass
/// <c>m_psubmenuEventSink</c> (WinTrek.cpp:4437-4440), so a click opens a nested
/// menu whose items are the real switches. The port previously ran one-press
/// toggles for Sound/Game instead — the row's behaviour gap. This model restores
/// the native tree:</para>
/// <code>
/// root : Graphics Options | Sound Options &gt; | Game Options &gt; | Map Keys and Controls
/// sound: Music on/off | Music Volume Up | Music Volume Down
/// game : Chat/Mute/Filter &gt; | Linear Controls | Dead Zone | Flip Y | Virtual Joystick | Map Keys and Controls
/// chat : Filter Chats To All | Mute/Unmute &lt;newest directory sender&gt;
/// mouse: Raise Mouse Sensitivity to NN % | Lower Mouse Sensitivity to NN %
/// </code>
///
/// <para><b>What each leaf drives.</b> Only items the port genuinely implements
/// are listed, and each maps to the port's real control rather than a stored
/// dummy: Music on/off and volume are the music planner (<c>Audio.Music</c>,
/// the native <c>idmToggleMusic</c>/<c>idmMusicVolumeUp/Down</c> items,
/// WinTrek.cpp:4492-4500); the Game Options items are the native
/// <c>idmToggleLinearControls</c> ('L'), <c>idmToggleLargeDeadZone</c> ('Z'),
/// <c>idmToggleFlipY</c> ('Y') and <c>idmToggleVirtualJoystick</c> ('J')
/// switches (WinTrek.cpp:4533-4540) over the same <c>MouseFlightController</c>
/// state the in-flight keys drive; "Map Keys and Controls" is native's
/// <c>idmConfigure</c> ('C', WinTrek.cpp:4544); and the Chat/Mute/Filter
/// submenu is native's <c>idmMuteFilterOptions</c> ('M', :4569) holding the
/// switch the port really has, "Filter Chats To All"
/// (<c>idmFilterChatsToAll</c>, :4572, the pane's <c>m_bPlayerChatsOnly</c>) plus
/// (P871b) native's per-player mute toggle (<c>idmContextMutePlayer</c> 'M',
/// :4310 → <c>WinTrek.cpp:3731-3734</c>) acting on the newest directory-chat
/// sender.
/// P871 restores native's Force Feedback switch
/// (<c>idmToggleEnableFeedback</c> 'E', :4599, default on) now that the port
/// drives a real rumble device. Native's era-specific items with no port
/// control — hardware acceleration, DirectSound8 usage, the separate SFX and
/// voice-over mixers and the mouse-acceleration/wheel-delay submenu — are
/// omitted rather than faked; the P806 report lists them.</para>
///
/// <para>Pure (no MonoGame types): the screen feeds a live
/// <see cref="OptionsState"/> snapshot and executes the returned
/// <see cref="OptionsCommand"/>.</para>
/// </summary>
public sealed class OptionsMenuModel
{
    /// <summary>P982 table pages fit their navigation hint in short windows.
    /// Existing pages keep their original spacing whenever it fits.</summary>
    public static (float FirstY, float RowStep) LayoutRows(float height, int count)
    {
        float step = MathF.Min(40f, MathF.Max(24f, (height - 174f) / Math.Max(1, count)));
        return (MathF.Min(height * 0.36f, height - count * step - 54f), step);
    }

    /// <summary>Which native menu is open.</summary>
    public enum Page { Root, Sound, Game, Chat, Mouse, Visuals }

    /// <summary>The action a leaf item performs (the screen executes it; the
    /// model stays static-data free).</summary>
    public enum OptionsCommand
    {
        GraphicsSettings,
        OpenSoundMenu,
        OpenGameMenu,
        OpenChatMenu,
        /// <summary>P948 — native's Game Options → "Mouse Options" submenu
        /// (<c>idmMouseOptions</c> 'Q', <c>WinTrek.cpp:4539</c>, items built at
        /// <c>:4606-4613</c>).</summary>
        OpenMouseMenu,
        MapKeysAndControls,
        ToggleMusic,
        MusicVolumeUp,
        MusicVolumeDown,
        Toggle3DSound,
        CycleAudioQuality,
        ToggleFilterChatsToAll,
        ToggleMuteChatSender,
        ToggleLinearControls,
        CycleDeadzone,
        ToggleFlipY,
        ToggleVirtualJoystick,
        ToggleForceFeedback,
        /// <summary>P948 — native <c>idmMouseSensUp</c> ('U',
        /// <c>WinTrek.cpp:4612</c>): +<c>c_fMouseSensDelta</c>, clamped, saved.</summary>
        MouseSensitivityUp,
        /// <summary>P948 — native <c>idmMouseSensDown</c> ('D', ':4613').</summary>
        MouseSensitivityDown,
        /// <summary>P979 — the port's Visuals page (visual-enhancement flags,
        /// always OFF by default; native has no counterpart).</summary>
        OpenVisualsMenu,
        /// <summary>P979 — flip the toggle whose row was activated
        /// (<see cref="VisualKeyAt"/> names it).</summary>
        ToggleVisualFlag,
        /// <summary>P979 — step the numeric flag whose row was activated by its
        /// table step (+).</summary>
        RaiseVisualRange,
        /// <summary>P979 — step the numeric flag down (−).</summary>
        LowerVisualRange,
    }

    /// <summary>One menu line: its label (state included, like native's
    /// <c>Get...MenuString()</c> helpers) and the command it issues
    /// (<c>null</c> only for a header line).</summary>
    public readonly record struct Row(string Label, OptionsCommand? Command, char Hotkey = '\0');

    /// <summary>The live switch states the labels render (and the toggles flip).</summary>
    public readonly record struct OptionsState(
        bool MusicOn,
        bool FilterChatsToAll,
        bool LinearControls,
        bool FlipY,
        bool VirtualJoystick,
        float Deadzone,
        bool ForceFeedback = true,
        // P893 — the positional-3D sound switch (port addition; native never had
        // a 3D toggle, so there is no native hotkey to mirror — 'P' is free).
        bool Sound3DOn = true,
        // P871b — the directory-chat mute target (the port extension's use of
        // native's context-menu mute, WinTrek.cpp:3731-3734,4310): the newest
        // relayed sender's display name, or null when the log is empty.
        string? ChatMuteTarget = null,
        bool ChatMuteTargetMuted = false,
        Audio.AudioQuality AudioQuality = Audio.AudioQuality.Medium,
        // P948 — the live, persisted mouse sensitivity (native MouseSensitivity,
        // WinTrek.cpp:2809) so the Mouse Options labels show the value they act on.
        float MouseSensitivity = Flight.MouseFlightController.DefaultMouseSensitivity,
        // P979 — the EFFECTIVE visual flags (profile `visuals` group + the
        // --visuals launch override). Null renders the table defaults (all OFF).
        VisualOptions? Visuals = null,
        // P989 — whether the float HDR scene path really exists on this device
        // (flag on AND the RGBA16F target + output transform proven). Null = not
        // known yet (e.g. the intro menu before a flight session), which shows the
        // row's dependency in its description but claims nothing about activity.
        bool? HdrSceneActive = null);

    /// <summary>The menu page currently showing (native's open submenu).</summary>
    public Page Current { get; private set; } = Page.Root;

    /// <summary>The highlighted row on the current page (settable so tests and the
    /// screen's pointer input can jump to a row).</summary>
    public int Row0 { get; set; }

    /// <summary>Back to the root menu with the first row highlighted (native
    /// re-opens the root every time the Options button is pressed).</summary>
    public void Reset()
    {
        Current = Page.Root;
        Row0 = 0;
    }

    /// <summary>Move the highlight inside the current page, wrapping.</summary>
    public void Move(int delta, OptionsState state)
    {
        int n = Rows(state).Count;
        if (n == 0) return;
        Row0 = ((Row0 + delta) % n + n) % n;
    }

    /// <summary>The current page's rows, labels rendered from
    /// <paramref name="state"/> (native <c>Get...MenuString</c>).</summary>
    public IReadOnlyList<Row> Rows(OptionsState state)
    {
        switch (Current)
        {
            case Page.Sound:
                return new[]
                {
                    new Row("Music: " + OnOff(state.MusicOn), OptionsCommand.ToggleMusic, 'M'),
                    new Row("Music Volume Up", OptionsCommand.MusicVolumeUp, 'U'),
                    new Row("Music Volume Down", OptionsCommand.MusicVolumeDown, 'D'),
                    // P893 — the positional-3D switch. Native had no such option
                    // (its sound menu is quality/hardware only), so this is a port
                    // addition for A/B-ing the 3D mix by ear.
                    new Row("Positional 3D Sound: " + OnOff(state.Sound3DOn), OptionsCommand.Toggle3DSound, 'P'),
                    new Row("Sound Quality: " + Audio.AudioQualityPresets.Normalize(state.AudioQuality)
                        + " (" + Audio.AudioQualityPresets.Budget(state.AudioQuality).DesiredVoices
                        + " voices)", OptionsCommand.CycleAudioQuality, 'Q'),
                };
            case Page.Game:
                return new[]
                {
                    new Row("Chat/Mute/Filter >", OptionsCommand.OpenChatMenu, 'M'),
                    new Row("Linear Controls: " + OnOff(state.LinearControls), OptionsCommand.ToggleLinearControls, 'L'),
                    new Row("Mouse Dead Zone: " + DeadzoneLabel(state.Deadzone), OptionsCommand.CycleDeadzone, 'Z'),
                    new Row("Flip Y: " + OnOff(state.FlipY), OptionsCommand.ToggleFlipY, 'Y'),
                    new Row("Virtual Joystick: " + OnOff(state.VirtualJoystick), OptionsCommand.ToggleVirtualJoystick, 'J'),
                    new Row("Force Feedback: " + OnOff(state.ForceFeedback), OptionsCommand.ToggleForceFeedback, 'E'),
                    // P948 — native's Mouse Options submenu (idmMouseOptions 'Q',
                    // WinTrek.cpp:4539) holding the Raise/Lower sensitivity items
                    // (:4612-4613). This is the persisted MouseSensitivity value.
                    new Row("Mouse Options >", OptionsCommand.OpenMouseMenu, 'Q'),
                    new Row("Map Keys and Controls", OptionsCommand.MapKeysAndControls, 'C'),
                };
            case Page.Chat:
                return new[]
                {
                    // Native's label is the ACTION: "Filter Chats Sent To All" while
                    // on, "Don't Filter Chats Sent To All" while off (WinTrek.cpp:5664-5667).
                    new Row(state.FilterChatsToAll
                            ? "Don't Filter Chats Sent To All"
                            : "Filter Chats Sent To All",
                        OptionsCommand.ToggleFilterChatsToAll, 'A'),
                    // P871b — native's per-player mute, whose only native UI is the
                    // player context menu's 'M' item (idmContextMutePlayer,
                    // WinTrek.cpp:3731-3734,4310) toggling PlayerInfo::SetMute
                    // (clintlib.h:326). The port has no context menu on a chat line,
                    // so the same toggle acts on the newest directory sender — the
                    // port extension's chat panel. Without it the lobby mute call had
                    // no UI at all.
                    new Row(state.ChatMuteTarget is { Length: > 0 } target
                            ? (state.ChatMuteTargetMuted ? "Unmute " : "Mute ") + target
                            : "Mute (no directory chat yet)",
                        OptionsCommand.ToggleMuteChatSender, 'M'),
                };
            case Page.Mouse:
                // P948 — native's Mouse Options submenu: step and end stops are
                // GetMouseSensMenuString's exact wording (WinTrek.cpp:5599-5615).
                return new[]
                {
                    new Row(Flight.MouseOptions.StepLabel(state.MouseSensitivity, +1),
                        OptionsCommand.MouseSensitivityUp, 'U'),
                    new Row(Flight.MouseOptions.StepLabel(state.MouseSensitivity, -1),
                        OptionsCommand.MouseSensitivityDown, 'D'),
                };
            case Page.Visuals:
                // P979 — generated from the flag TABLE, not hand-written per flag:
                // a toggle row, or a value row plus raise/lower for a numeric flag.
                return VisualPageRows(state).Select(entry => entry.Row).ToArray();
            default:
                return new[]
                {
                    new Row("Graphics Options", OptionsCommand.GraphicsSettings, 'O'),
                    new Row("Sound Options >", OptionsCommand.OpenSoundMenu, 'S'),
                    new Row("Game Options >", OptionsCommand.OpenGameMenu, 'G'),
                    // P979 — the port's Visuals page (visual enhancements, each a
                    // flag defaulting OFF; native has no counterpart).
                    new Row("Visuals >", OptionsCommand.OpenVisualsMenu, 'V'),
                    new Row("Map Keys and Controls", OptionsCommand.MapKeysAndControls, 'C'),
                };
        }
    }

    /// <summary>
    /// P979 — the Visuals page, built from <see cref="VisualFlags.Definitions"/>
    /// so a new flag needs only a table row. Each entry keeps the flag key its
    /// row edits (<see cref="VisualKeyAt"/>), because the three visual commands
    /// are generic: the screen applies them to the row's key.
    /// </summary>
    private IReadOnlyList<(Row Row, string? Key)> VisualPageRows(OptionsState state)
    {
        var rows = new List<(Row Row, string? Key)>();
        foreach (var definition in VisualFlags.Definitions)
        {
            if (definition.Kind == VisualFlagKind.Range)
            {
                float value = state.Visuals?.Value(definition.Key) ?? definition.DefaultValue;
                rows.Add((new Row(
                    $"{definition.Label}: {VisualOptions.Format(value)} m - {definition.Description}", null), definition.Key));
                rows.Add((new Row(RangeStepLabel(definition, value, +1), OptionsCommand.RaiseVisualRange, 'U'), definition.Key));
                rows.Add((new Row(RangeStepLabel(definition, value, -1), OptionsCommand.LowerVisualRange, 'D'), definition.Key));
            }
            else
            {
                // P989 — a flag that DEPENDS on another one (sim palette / ACES need
                // the HDR scene path) says so on its row once the device has decided:
                // "inactive" is the truthful state when the dependency is off or fell
                // back to 8-bit, and the enhancement really does nothing then.
                string note = definition.RequiresKey is { } requires
                    && state.HdrSceneActive == false
                        ? " (inactive: needs " + (VisualFlags.Find(requires)?.Label ?? requires) + ")"
                        : "";
                rows.Add((new Row(
                    $"{definition.Label}: {VisualOptions.DescribeValue(definition, state.Visuals)} - {definition.Description}{note}",
                    OptionsCommand.ToggleVisualFlag, definition.Hotkey), definition.Key));
            }
        }
        return rows;
    }

    /// <summary>P979 — the flag key the row at <paramref name="row"/> edits on the
    /// Visuals page (null when the page is not showing or the row is empty).</summary>
    public string? VisualKeyAt(int row, OptionsState state)
    {
        if (Current != Page.Visuals) return null;
        var rows = VisualPageRows(state);
        if (rows.Count == 0) return null;
        return rows[System.Math.Clamp(row, 0, rows.Count - 1)].Key;
    }

    private static string RangeStepLabel(VisualFlag definition, float value, int direction)
    {
        float step = definition.Step > 0f ? definition.Step : 1000f;
        float next = System.Math.Clamp(value + direction * step, definition.MinValue, definition.MaxValue);
        bool atStop = direction > 0 ? next <= value : next >= value;
        if (atStop)
            return (direction > 0 ? "Raise " : "Lower ") + definition.Label
                + (direction > 0 ? " (highest)" : " (lowest)");
        return (direction > 0 ? "Raise " : "Lower ") + definition.Label + " to " + VisualOptions.Format(next) + " m";
    }

    /// <summary>Activate the highlighted row: a submenu opens (and the highlight
    /// resets to its first row), a leaf returns its command.</summary>
    public OptionsCommand? Activate(OptionsState state)
    {
        var rows = Rows(state);
        if (rows.Count == 0) return null;
        var row = rows[System.Math.Clamp(Row0, 0, rows.Count - 1)];
        if (row.Command is not { } cmd) return null;
        switch (cmd)
        {
            case OptionsCommand.OpenSoundMenu: Current = Page.Sound; Row0 = 0; return null;
            case OptionsCommand.OpenGameMenu: Current = Page.Game; Row0 = 0; return null;
            case OptionsCommand.OpenChatMenu: Current = Page.Chat; Row0 = 0; return null;
            case OptionsCommand.OpenMouseMenu: Current = Page.Mouse; Row0 = 0; return null;
            case OptionsCommand.OpenVisualsMenu: Current = Page.Visuals; Row0 = 0; return null;
            default: return cmd;
        }
    }

    /// <summary>Find a row by its native hotkey on the current page.</summary>
    public OptionsCommand? CommandForHotkey(char key, OptionsState state)
    {
        if (key == '\0') return null;
        var rows = Rows(state);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Hotkey == char.ToUpperInvariant(key) && rows[i].Command is { } cmd)
            {
                // Generic visual commands use the selected row to resolve their
                // table key. A hotkey must select the row it actually names.
                if (cmd is OptionsCommand.ToggleVisualFlag or OptionsCommand.RaiseVisualRange or OptionsCommand.LowerVisualRange)
                    Row0 = i;
                return cmd;
            }
        return null;
    }

    /// <summary>ESC/Back: leave a submenu for the root; returns false when
    /// already at the root (the screen then closes the menu, native's
    /// OnCancel).</summary>
    public bool Back()
    {
        if (Current == Page.Root) return false;
        Current = Page.Root;
        Row0 = 0;
        return true;
    }

    /// <summary>The native "on"/"off" wording for a switch.</summary>
    public static string OnOff(bool on) => on ? "on" : "off";

    /// <summary>The deadzone label native shows in
    /// <c>GetDeadzoneMenuString()</c> (WinTrek.cpp:4567 area): the port's
    /// presets are Smallest/Small/Large, described in percent like the in-flight
    /// log line.</summary>
    public static string DeadzoneLabel(float deadzone) =>
        ((int)System.MathF.Round(deadzone * 100f)).ToString(CultureInfo.InvariantCulture) + "%";
}
