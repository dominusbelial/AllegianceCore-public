using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// Pure, side-effect-free chat-target decision logic for the compose line —
/// no MonoGame types, so it is unit-testable without a graphics device
/// (the same discipline as <see cref="ChatLogFormat"/> / the Flight helpers).
/// Owns: the default recipient a fresh compose sends to, the Tab cycle that
/// steps the recipient while the buffer is empty, and the bracketed label the
/// compose line draws next to the caret so the player sees who will receive
/// the message.
///
/// P456: the port previously hardcoded <see cref="ChatTarget.Everyone"/> at the
/// send site; native defaults a fresh compose to Team. This helper is the port
/// of native's <c>CycleChatTarget</c> (console.cpp:2631-2745) in the no-allies
/// branch (the port has no alliance system), reduced to the steps that apply
/// while composing a chat.
/// </summary>
public static class ChatTargetLogic
{
    /// <summary>
    /// The recipient a fresh compose session starts on. Native constructs its
    /// chat state as <c>m_chsChat(CHAT_TEAM)</c> (console.cpp:644) — Team is the
    /// default, and it persists across compose sessions because it is a member
    /// (<c>m_chsChat</c>), never reset when the input line closes. The port
    /// matches that: the owning <c>Game1</c> keeps one <c>_chatTarget</c> member
    /// initialised to this value and never clears it between composes.
    /// </summary>
    public const ChatTarget DefaultTarget = ChatTarget.Team;

    /// <summary>
    /// The compose-time cycle order. Native's no-allies
    /// <c>CycleChatTarget</c> walks Wing → (Individual — skipped while
    /// composing) → (Ship — skipped unless the player has child ships) →
    /// FriendlySector → Team → Everyone → back to Wing. Individual is always
    /// skipped here because the port only cycles while composing a chat, exactly
    /// the condition under which native falls through past it. Ship is always
    /// skipped because the client's ship model exposes no child/turret list (see
    /// the P456 report). FriendlySector is always a landing state (native also
    /// jumps past it when the friendly sector is empty — a per-frame occupancy
    /// check this stateless ring deliberately omits).
    /// </summary>
    private static readonly ChatTarget[] Cycle =
    [
        ChatTarget.Wing,
        ChatTarget.FriendlySector,
        ChatTarget.Team,
        ChatTarget.Everyone,
    ];

    /// <summary>
    /// Step one recipient forward in the compose cycle. Unknown values wrap to
    /// the head of the ring (Wing), matching native's <c>default</c> case.
    /// </summary>
    public static ChatTarget Next(ChatTarget current)
    {
        int idx = Array.IndexOf(Cycle, current);
        int next = idx < 0 ? 0 : (idx + 1) % Cycle.Length;
        return Cycle[next];
    }

    /// <summary>
    /// P806 — the LOBBY chat-target cycle: native's three chat buttons
    /// (<c>OnButtonBarChat</c>, teamscreen.cpp:3082-3118) are Everyone, Team and
    /// Leaders; the Leaders step only exists for the side's team leader (the
    /// native button is skipped otherwise), so a non-leader cycles the first two.
    /// The port's team lobby (ReadyCheck) uses Tab as the keyboard stand-in for
    /// those buttons, mirroring the flight compose cycle's key.
    /// </summary>
    public static ChatTarget NextLobby(ChatTarget current, bool isTeamLeader)
    {
        if (current == ChatTarget.Everyone) return ChatTarget.Team;
        if (isTeamLeader) return current == ChatTarget.Team ? ChatTarget.Leaders : ChatTarget.Everyone;
        return ChatTarget.Everyone;
    }

    /// <summary>
    /// Whether Tab may cycle the recipient mid-compose. Native only cycles when
    /// the typed buffer is empty or holds a single leading space
    /// (<c>*pcc == '\0' || *pcc == ' '</c>, console.cpp:2747-2755); typed text
    /// means name completion instead (out of scope — P456b). The compose buffer
    /// is ASCII-only, so a leading space check mirrors the native guard.
    /// </summary>
    public static bool CanCycleWhileComposing(string buffer) =>
        buffer.Length == 0 || buffer[0] == ' ';

    /// <summary>
    /// The bracketed label drawn before the compose-line caret so the player can
    /// see who will receive the message (native shows no label; this is an
    /// additive visibility aid). Only the cycle targets have labels; anything
    /// unrecognised renders empty rather than crashing.
    /// </summary>
    public static string ComposeLabel(ChatTarget target) => target switch
    {
        ChatTarget.Team           => "[TEAM] ",
        ChatTarget.Everyone       => "[EVERYONE] ",
        ChatTarget.Wing           => "[WING] ",
        ChatTarget.FriendlySector => "[FSECTOR] ",
        _                         => "",
    };

    /// <summary>P784 — the compose-line recipient label with the RESOLVED
    /// recipient name. Native <c>UpdateComposedChat</c> fills the compose
    /// line's ChatRecipient with the actual name — the side name for Team, the
    /// wing name for Wing, the cluster name for FriendlySector, the ship name
    /// for Individual, "All" for Everyone (console.cpp:1299-1338) — not a
    /// generic channel tag. The port keeps its bracketed aid but resolves the
    /// name when the world knows it; unresolved names fall back to the plain
    /// <see cref="ComposeLabel"/> tag. Wing uses the native wing names
    /// (<c>c_pszWingName</c>, Igc/common.cpp:23-33).</summary>
    public static string ResolvedLabel(ChatTarget target, string? sideName, string? wingName,
        string? sectorName, string? shipName) => target switch
    {
        ChatTarget.Team           => sideName is null ? "[TEAM] " : $"[TEAM {sideName}] ",
        ChatTarget.Everyone       => "[ALL] ",
        ChatTarget.Wing           => wingName is null ? "[WING] " : $"[WING {wingName}] ",
        ChatTarget.FriendlySector => sectorName is null ? "[FSECTOR] " : $"[{sectorName}] ",
        ChatTarget.Individual     => shipName is null ? "[ME] " : $"[{shipName}] ",
        _                         => ComposeLabel(target),
    };
}
