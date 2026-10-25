using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P818 — the pure wire plan for one composed line, the port of native
/// <c>ConsoleDataImpl::SendChatInternal</c>'s send loop (WinTrek/console.cpp:1448-1507):
/// a <see cref="ChatTarget.Group"/> recipient sends ONE chat per subject, the
/// first as <c>CHAT_GROUP</c> and every later one as <c>CHAT_GROUP_NOECHO</c>
/// (console.cpp:1470-1491), so the sender's own chat history gains exactly one
/// copy of the line (clintlib/clintlib.cpp:2463-2470 echoes every send except
/// <c>CHAT_GROUP_NOECHO</c>). Every other target sends once.
///
/// <para>No new wire fields: <c>CHAT_GROUP</c>/<c>CHAT_GROUP_NOECHO</c> already
/// exist in <see cref="ChatTarget"/> and the server rewrites them to
/// <c>CHAT_INDIVIDUAL</c> on the message's <c>oidRecipient</c>
/// (FedSrv.CPP:3531-3536; ported in <c>Server.Lobby.SelectChatTargets</c> and
/// <c>Server.Callbacks.SendSiteChat</c>).</para>
///
/// <para>Native also refuses to put an Individual chat addressed to yourself on
/// the wire (<c>ctRecipient != CHAT_INDIVIDUAL || oidRecipient !=
/// m_ship-&gt;GetObjectID()</c>, clintlib.cpp:2422-2426): a chat to yourself is
/// a LOCAL line only. <see cref="Plan"/> reports that as
/// <see cref="Send.LocalOnly"/> plus a local echo.</para>
/// </summary>
public static class ChatSendPlan
{
    /// <summary>One chat message the composer must send (or echo locally).
    /// <paramref name="EchoLocally"/> is native's
    /// <c>ctRecipient != CHAT_GROUP_NOECHO</c> local <c>ReceiveChat</c>; the
    /// port only needs it where its server does not relay the line back to the
    /// sender (an Individual/Group line addressed to someone else).</summary>
    public readonly record struct Send(ChatTarget Target, short OidRecipient, bool LocalOnly, bool EchoLocally);

    /// <summary>Build the send list for one composed line.
    /// <paramref name="subjects"/> is the compose's ship list (native
    /// <c>m_shipsSubject</c>); <paramref name="oidRecipient"/> is the single
    /// recipient id the caller resolved for the channel targets (own ship for
    /// Individual, wing id for Wing, side id for Team, cluster id for
    /// FriendlySector/AllSector).</summary>
    public static IReadOnlyList<Send> Plan(ChatTarget target, IReadOnlyList<short> subjects,
        short oidRecipient, short selfId)
    {
        if (target == ChatTarget.Group)
        {
            bool selfIsSubject = selfId >= 0 && subjects.Contains(selfId);
            var sends = new List<Send>(subjects.Count);
            for (int i = 0; i < subjects.Count; i++)
            {
                ChatTarget ct = i == 0 ? ChatTarget.Group : ChatTarget.GroupNoEcho;
                // The first message is the echoing one (native's local echo).
                // When the player's own ship is a subject the server relays
                // that copy back instead, so no local echo is needed — either
                // way the sender's history ends up with exactly one line.
                bool echo = i == 0 && !selfIsSubject;
                sends.Add(new Send(ct, subjects[i], LocalOnly: false, EchoLocally: echo));
            }
            return sends;
        }

        if (target == ChatTarget.Individual)
        {
            bool toSelf = selfId >= 0 && oidRecipient == selfId;
            return [new Send(target, oidRecipient, LocalOnly: toSelf, EchoLocally: true)];
        }

        // Everyone/Team/Allies/Wing/FriendlySector/AllSector: one chat; the
        // port's server relays the sender's own copy back, so no local echo.
        return [new Send(target, oidRecipient, LocalOnly: false, EchoLocally: false)];
    }
}
