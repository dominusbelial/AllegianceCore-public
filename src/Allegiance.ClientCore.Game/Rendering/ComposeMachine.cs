using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Pure, side-effect-free port of native's compose state machine
/// (<c>ConsoleDataImpl::ComposeState</c>, WinTrek/console.cpp:524-529) — the
/// five compose modes and the transitions between them. No MonoGame types, so
/// every transition is unit-testable without a graphics device (the
/// <see cref="ChatTargetLogic"/> / <see cref="ChatKeySound"/> discipline).
///
/// <para>Native states: NotComposing(0), MouseCommand(1), Chat(2), Command(3),
/// Shell(4), Quick(5) — the port's <see cref="NativeComposeState"/> mirrors the
/// same numbering. The machine covers exactly the transitions the native code
/// performs; every rule below cites its native site:</para>
///
/// <list type="bullet">
/// <item><c>StartChat</c> — <c>ConsoleImage::StartChat</c> opens Chat from
/// NotComposing (<c>StartComposing(c_csComposeChat)</c>) or force-moves any
/// composing state to Chat (<c>SetComposeState(c_csComposeChat)</c>);
/// console.cpp:2423-2431.</item>
/// <item><c>VerbButton</c> — <c>OnVerbButton</c> is ignored from Shell
/// (:1266) and otherwise starts or moves to Command (:1270-1273).</item>
/// <item><c>RecipientButton</c> — <c>OnRecipientButton</c> is ignored from
/// Shell (:1136); from NotComposing it STARTS a chat compose
/// (<c>StartComposing(c_csComposeChat, &amp;m_chsChat)</c>, :1185-1186); from
/// any other composing state it only retargets the current subject — the state
/// is kept.</item>
/// <item><c>PickShip</c> — <c>PickShip</c> starts MouseCommand when not
/// composing and then retargets (:1776-1790).</item>
/// <item><c>RectangleGroup</c> — the drag-rectangle selection starts
/// MouseCommand when not composing (:2128-2131) and then sets the Group
/// recipient.</item>
/// <item><c>OpenQuickChat</c> — <c>StartQuickChat</c> starts Quick from
/// NotComposing or force-moves a composing state to Quick
/// (console.cpp:1508-1532).</item>
/// <item><c>TypeBang</c> — <c>'!'</c> moves Chat → Shell but ONLY while the
/// typed buffer is empty (console.cpp:2388-2396); with text it is an ordinary
/// printable character.</item>
/// <item><c>Escape</c> — <c>VK_ESCAPE</c> moves Command → Chat (clearing the
/// typed text, :2375-2380) and quits every other state (:2381-2384).</item>
/// <item><c>EnterEmpty</c> — <c>VK_RETURN</c> with an empty buffer quits
/// (:2360-2363).</item>
/// <item><c>Send</c> — every send path (<c>SendChat</c> :1604-1621,
/// <c>SendQuickChat</c> :1568-1602) ends in <c>QuitComposing</c>
/// (:1618-1620), so any composing state returns to NotComposing.</item>
/// <item><c>Backquote</c> — <c>'~'</c>/<c>'`'</c> quits composing and opens
/// the main quick-chat menu (:2386-2387).</item>
/// <item><c>SwitchViewMode</c> — <c>OnSwitchViewMode</c> quits only the
/// MouseCommand state (:1031-1035).</item>
/// <item><c>CtrlVerb</c> — Ctrl+&lt;verb letter&gt; moves a keystroke-taking,
/// non-Command state with an empty buffer to Command (:2343-2357); the
/// keystroke-taking gate itself is <see cref="TakesKeystrokes"/>
/// (console.cpp:2335-2338: state &gt; MouseCommand).</item>
/// </list>
/// </summary>
public static class ComposeMachine
{
    /// <summary>The input events that drive compose-state transitions. Each
    /// member maps 1:1 onto a native entry point (see the type doc).</summary>
    public enum Trigger
    {
        StartChat,
        VerbButton,
        RecipientButton,
        PickShip,
        RectangleGroup,
        OpenQuickChat,
        TypeBang,
        Escape,
        EnterEmpty,
        Send,
        Backquote,
        SwitchViewMode,
        CtrlVerb,
    }

    /// <summary>Native <c>IsTakingKeystrokes</c> (console.cpp:2335-2338):
    /// only the states ABOVE MouseCommand accept typed characters.</summary>
    public static bool TakesKeystrokes(NativeComposeState state) => state > NativeComposeState.MouseCommand;

    /// <summary>The next compose state after <paramref name="trigger"/> fires
    /// in <paramref name="state"/>. <paramref name="bufferEmpty"/> mirrors
    /// native's <c>m_strTypedText.IsEmpty()</c> for the triggers that read it
    /// (TypeBang, Enter, CtrlVerb — console.cpp:2343-2396).</summary>
    public static NativeComposeState Next(NativeComposeState state, Trigger trigger, bool bufferEmpty = true)
    {
        switch (trigger)
        {
            case Trigger.StartChat:
                // console.cpp:2423-2431 — StartComposing from NotComposing,
                // SetComposeState(Chat) otherwise.
                return NativeComposeState.Chat;

            case Trigger.VerbButton:
                // console.cpp:1264-1279 — ignored from Shell; otherwise
                // StartComposing(Command) or SetComposeState(Command).
                return state == NativeComposeState.Shell ? state : NativeComposeState.Command;

            case Trigger.RecipientButton:
                // console.cpp:1134-1192 — ignored from Shell; from
                // NotComposing it starts a CHAT compose; otherwise the state
                // is kept (only the recipient changes).
                if (state == NativeComposeState.Shell) return state;
                return state == NativeComposeState.NotComposing ? NativeComposeState.Chat : state;

            case Trigger.PickShip:
                // console.cpp:1776-1790 — StartComposing(MouseCommand) when
                // not composing; otherwise only the recipient changes.
                return state == NativeComposeState.NotComposing ? NativeComposeState.MouseCommand : state;

            case Trigger.RectangleGroup:
                // console.cpp:2128-2131 — same start rule as PickShip.
                return state == NativeComposeState.NotComposing ? NativeComposeState.MouseCommand : state;

            case Trigger.OpenQuickChat:
                // console.cpp:1508-1532 — StartComposing(Quick) from
                // NotComposing, SetComposeState(Quick) otherwise.
                return NativeComposeState.Quick;

            case Trigger.TypeBang:
                // console.cpp:2388-2396 — '!' moves Chat → Shell only while
                // the typed text is empty; with text it stays a printable.
                return state == NativeComposeState.Chat && bufferEmpty ? NativeComposeState.Shell : state;

            case Trigger.Escape:
                // console.cpp:2375-2385 — Command steps BACK to Chat (the
                // typed text is cleared); every other state quits.
                return state == NativeComposeState.Command ? NativeComposeState.Chat : NativeComposeState.NotComposing;

            case Trigger.EnterEmpty:
                // console.cpp:2360-2363 — an empty buffer on Enter quits.
                return NativeComposeState.NotComposing;

            case Trigger.Send:
                // console.cpp:1604-1621 — SendChat (all variants) ends in
                // QuitComposing (:1640-1649).
                return NativeComposeState.NotComposing;

            case Trigger.Backquote:
                // console.cpp:2386-2387 — quit composing, open quick chat.
                return NativeComposeState.NotComposing;

            case Trigger.SwitchViewMode:
                // console.cpp:1031-1035 — only MouseCommand is quit.
                return state == NativeComposeState.MouseCommand ? NativeComposeState.NotComposing : state;

            case Trigger.CtrlVerb:
                // console.cpp:2343-2357 — Ctrl+<verb letter> moves a
                // keystroke-taking, non-Command state with an empty buffer to
                // Command. (The keystroke gate itself is TakesKeystrokes.)
                return TakesKeystrokes(state) && state != NativeComposeState.Command && bufferEmpty
                    ? NativeComposeState.Command : state;

            default:
                return state;
        }
    }
}

/// <summary>
/// Pure port of native's shell-command compose mode: the <c>'!'</c>-prefixed
/// compose whose Enter does NOT send chat but runs
/// <c>BaseClient::ParseShellCommand</c> (console.cpp:1612-1614;
/// clintlib/clintlib.cpp:2277-2311). Native ships exactly two shell commands —
/// <c>!mute &lt;player&gt;</c> and <c>!unmute &lt;player&gt;</c>, resolved by
/// CHARACTER NAME through <c>FindPlayer</c> (clintlib.cpp:2282,2290) — and an
/// unrecognised line is silently dropped (the <c>bCommand</c> result is
/// ignored and the text is never sent as chat). The port's mute store is
/// <c>ClientWorld.SetPilotMuted</c> (the ModerationModel's target, keyed by
/// ship id), so the parse resolves names against the world roster.
/// </summary>
public static class ShellCommands
{
    /// <summary>The outcome of one shell line. <see cref="Handled"/> is false
    /// for unrecognised text (dropped, never sent as chat — clintlib.cpp:2310).</summary>
    public readonly record struct ShellResult(bool Handled, short ShipId, bool Mute, string Name);

    /// <summary>Parse one composed shell line. Call under the session gate;
    /// matching is case-insensitive on the verb (native <c>_strnicmp</c>,
    /// clintlib.cpp:2281,2288) and exact-but-case-insensitive on the player
    /// name (native <c>FindPlayer</c> compares names case-insensitively,
    /// clintlib.cpp:624-640). <paramref name="ships"/> is the world roster;
    /// unknown names yield Handled=false like native's null
    /// <c>PlayerInfo*</c> no-op.</summary>
    public static ShellResult Parse(string line, IReadOnlyDictionary<short, Allegiance.ClientCore.State.ShipEntity> ships)
    {
        string trimmed = line.TrimStart();
        bool mute;
        if (trimmed.StartsWith("!mute ", StringComparison.OrdinalIgnoreCase)) mute = true;
        else if (trimmed.StartsWith("!unmute ", StringComparison.OrdinalIgnoreCase)) mute = false;
        else return default;

        string name = trimmed[(mute ? "!mute ".Length : "!unmute ".Length)..].Trim();
        foreach (var (id, ship) in ships)
        {
            if (!string.IsNullOrEmpty(ship.CharacterName)
                && string.Equals(ship.CharacterName, name, StringComparison.OrdinalIgnoreCase))
                return new ShellResult(true, id, mute, ship.CharacterName);
        }
        return default;
    }
}
