using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.QuickChat;

/// <summary>
/// Maps a quick-chat command's <c>targetType</c> mask (the mdl constant
/// expression like <c>ttFriendly | ttStation | ttNearest</c>) to the port's
/// real <see cref="ChatTarget"/>. Pure and testable.
/// </summary>
/// <remarks>
/// <para><b>Deliberate collapse to <see cref="ChatTarget.Team"/>.</b> Native's
/// quick-chat sends to its current chat recipient
/// (<c>m_pchsCurrent-&gt;m_ctRecipient</c>), and the <c>targetType</c> mask is
/// primarily used to <b>find the target model</b> for the attached command
/// (console.cpp:1543-1590) — not to choose a distinct wire <c>ChatTarget</c>
/// per line. The brief explicitly blesses this port collapsing target
/// semantics: individual-ship targeting and ability-mask gating are out of
/// scope, and <c>ChatTarget.Team</c> is the safe default "for anything not
/// cleanly mappable". Team is also the <i>correct</i> default here: many of
/// these lines are team intel (e.g. "Our base is under attack. Request
/// assistance.") that must not be broadcast to <see cref="ChatTarget.Everyone"/>.
/// </para>
/// <para>The mask is still parsed and stored on the command so this mapping is
/// a real, auditable transformation rather than an omission — but every mask
/// (including <c>0</c>) currently resolves to Team.</para>
/// </remarks>
public static class QuickChatTarget
{
    /// <summary>Resolve a <c>targetType</c> mask expression to the port's chat
    /// target. Always <see cref="ChatTarget.Team"/> — see the class remarks.</summary>
    public static ChatTarget Resolve(string ttMask) => ChatTarget.Team;
}
