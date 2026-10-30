using System.Collections.Generic;
using System.Globalization;
using System.Linq;

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
/// control — sound quality/hardware, DirectSound8 usage, the separate SFX and
/// voice-over mixers and the mouse-acceleration/wheel-delay submenu — are
/// omitted rather than faked; the P806 report lists them.</para>
///
/// <para>Pure (no MonoGame types): the screen feeds a live
/// <see cref="OptionsState"/> snapshot and executes the returned
/// <see cref="OptionsCommand"/>.</para>
/// </summary>
public sealed class OptionsMenuModel
{
    /// <summary>Which native menu is open.</summary>
    public enum Page { Root, Sound, Game, Chat }

    /// <summary>The action a leaf item performs (the screen executes it; the
    /// model stays static-data free).</summary>
    public enum OptionsCommand
    {
        GraphicsSettings,
        OpenSoundMenu,
        OpenGameMenu,
        OpenChatMenu,
        MapKeysAndControls,
        ToggleMusic,
        MusicVolumeUp,
        MusicVolumeDown,
        Toggle3DSound,
        ToggleFilterChatsToAll,
        ToggleMuteChatSender,
        ToggleLinearControls,
        CycleDeadzone,
        ToggleFlipY,
        ToggleVirtualJoystick,
        ToggleForceFeedback,
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
        bool ChatMuteTargetMuted = false);

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
            default:
                return new[]
                {
                    new Row("Graphics Options", OptionsCommand.GraphicsSettings, 'O'),
                    new Row("Sound Options >", OptionsCommand.OpenSoundMenu, 'S'),
                    new Row("Game Options >", OptionsCommand.OpenGameMenu, 'G'),
                    new Row("Map Keys and Controls", OptionsCommand.MapKeysAndControls, 'C'),
                };
        }
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
            default: return cmd;
        }
    }

    /// <summary>Find a row by its native hotkey on the current page.</summary>
    public OptionsCommand? CommandForHotkey(char key, OptionsState state)
    {
        if (key == '\0') return null;
        foreach (var row in Rows(state))
            if (row.Hotkey == char.ToUpperInvariant(key) && row.Command is { } cmd)
                return cmd;
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
