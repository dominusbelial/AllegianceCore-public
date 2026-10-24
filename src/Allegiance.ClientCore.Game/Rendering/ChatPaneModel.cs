namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P757 — the pure model of the in-flight chat pane's <b>history view</b>:
/// native <c>ChatListPaneImpl</c> (WinTrek/chatpane.cpp). Two native rule
/// families live here, both driven from real production state in
/// <c>Game1.FlightInput</c>/<c>Game1.Session</c>:
///
/// <para><b>Channel filtering</b> (chatpane.cpp:226-233, 299-337, 340-358).
/// Native keeps a channel list (<c>m_listChannels</c>) plus a
/// <c>m_bPlayerChatsOnly</c> flag. <c>ChatPassesFilter</c> drops a message when
/// it is "non-critical" — not from a player, not from an object model, not
/// <c>CHAT_INDIVIDUAL_NOFILTER</c>, while player-chats-only is on — and then
/// requires the message's channel to be one of the selected channels when any
/// are selected. <c>AddChannel</c>/<c>RemoveChannel</c>/<c>ResetChannels</c>
/// rebuild the whole list and snap it to the live edge with autoscroll on
/// (<c>UpdateContents</c>, chatpane.cpp:286-297). The native lobby team screen
/// drives exactly this API (teamscreen.cpp:3085-3101).</para>
///
/// <para><b>Autoscroll</b> (chatpane.cpp:167-198, 255-270, 272-283). Paging or
/// scrolling up turns autoscroll <i>off</i> and arms a ten-second timer that
/// scrolls back to the bottom and re-enables it. Paging/scrolling down turns it
/// back on as soon as the newest line is visible (<c>OnScroll</c>:
/// <c>IsItemVisibleByIdx(count-1)</c>). A newly appended item only scrolls the
/// pane when autoscroll is on (<c>AddChatItem</c>); a paused pane keeps showing
/// the same lines — in this offset-from-newest-edge representation that means
/// the offset grows with the log so the visible window content is preserved.</para>
///
/// <para>Kept free of MonoGame types so every rule is headless-testable (the
/// same discipline as <see cref="ChatScroll"/>/<see cref="ChatTargetLogic"/>).</para>
/// </summary>
public sealed class ChatPaneModel
{
    /// <summary>Native autoscroll re-enable timer: ten seconds
    /// (chatpane.cpp:181-183, <c>new TEventTarget&lt;...&gt;(this, ...,
    /// OnAutoscrollTimeout, GetWindow(), 10)</c>).</summary>
    public const double AutoscrollResumeSeconds = 10.0;

    private readonly HashSet<Allegiance.Core.Sim.ChatTarget> _channels = new();

    /// <summary>Native <c>m_bPlayerChatsOnly</c> (chatpane.cpp:128,146): drop
    /// non-critical messages (not from a player / object model). Default off in
    /// the port's pane: the port's chat log is also its console surface, and
    /// native routes the filtered chatter to <c>PostPlainText</c> instead —
    /// see the P757 report for the adaptation note.</summary>
    public bool PlayerChatsOnly { get; private set; }

    /// <summary>Native <c>m_bAutoscroll</c> — true while the pane follows the
    /// newest line.</summary>
    public bool Autoscroll { get; private set; } = true;

    /// <summary>The moment (caller's clock, seconds) the autoscroll timer
    /// fires, or <c>null</c> while disarmed.</summary>
    public double? AutoscrollResumeAt { get; private set; }

    /// <summary>Lines pulled from the newest edge (0 = live edge) — the P626
    /// offset, but now owned by this model so the autoscroll rules can drive
    /// it. Clamped against the filtered log length by every mutator.</summary>
    public int Offset { get; private set; }

    /// <summary>The selected channels (native <c>m_listChannels</c>); empty =
    /// no channel restriction.</summary>
    public IReadOnlyCollection<Allegiance.Core.Sim.ChatTarget> Channels => _channels;

    // ── channel filter (chatpane.cpp:299-358) ───────────────────────────────

    /// <summary>Native <c>ChatIsNonCriticalMessage</c> (chatpane.cpp:226-233):
    /// a message not from a player and not from an object model, on a channel
    /// other than <c>CHAT_INDIVIDUAL_NOFILTER</c>, while player-chats-only is
    /// on.</summary>
    public bool IsNonCritical(bool fromPlayer, bool fromObjectModel, Allegiance.Core.Sim.ChatTarget target)
        => !fromPlayer && !fromObjectModel
           && target != Allegiance.Core.Sim.ChatTarget.IndividualNoFilter
           && PlayerChatsOnly;

    /// <summary>Native <c>ChatPassesFilter</c> (chatpane.cpp:299-321): drop
    /// non-critical messages, then require the channel to be selected when any
    /// channel is selected.</summary>
    public bool PassesFilter(bool fromPlayer, bool fromObjectModel, Allegiance.Core.Sim.ChatTarget target)
    {
        if (IsNonCritical(fromPlayer, fromObjectModel, target))
            return false;
        if (_channels.Count == 0)
            return true;
        return _channels.Contains(target);
    }

    /// <summary>Native <c>AddChannel</c> (chatpane.cpp:340-344).</summary>
    public void AddChannel(Allegiance.Core.Sim.ChatTarget channel)
    {
        _channels.Add(channel);
        UpdateContents();
    }

    /// <summary>Native <c>RemoveChannel</c> (chatpane.cpp:346-350).</summary>
    public void RemoveChannel(Allegiance.Core.Sim.ChatTarget channel)
    {
        _channels.Remove(channel);
        UpdateContents();
    }

    /// <summary>Native <c>ResetChannels</c> (chatpane.cpp:352-355).</summary>
    public void ResetChannels()
    {
        _channels.Clear();
        UpdateContents();
    }

    /// <summary>
    /// P806 — the native LOBBY chat pane's channel rule: a chat-target button
    /// (<c>OnButtonBarChat</c>, <c>WinTrek/teamscreen.cpp:3082-3118</c>) resets the
    /// pane's channel list and adds the selected channel plus the three fixed
    /// ones — <c>{target, CHAT_INDIVIDUAL, CHAT_INDIVIDUAL_NOFILTER, CHAT_ADMIN}</c>
    /// (<c>:3088-3092</c> for Everyone, <c>:3097-3101</c> for Team, <c>:3108-3112</c>
    /// for Leaders). The Leaders button exists only for a team leader
    /// (<c>:3104-3116</c>: the whole case is skipped otherwise, leaving both the
    /// target and the pane's channels unchanged).
    /// <para>Returns whether the target was applied. This is the only native
    /// place a chat pane filters by channel; the in-flight console chat pane
    /// (same class) selects no channels at all and only runs the
    /// <see cref="PlayerChatsOnly"/> rule (chatpane.cpp:146, console.cpp:3439-3442).</para>
    /// </summary>
    public bool ApplyLobbyChatTarget(Allegiance.Core.Sim.ChatTarget target, bool isTeamLeader)
    {
        if (target == Allegiance.Core.Sim.ChatTarget.Leaders && !isTeamLeader)
            return false;
        ResetChannels();
        AddChannel(target);
        AddChannel(Allegiance.Core.Sim.ChatTarget.Individual);
        AddChannel(Allegiance.Core.Sim.ChatTarget.IndividualNoFilter);
        AddChannel(Allegiance.Core.Sim.ChatTarget.Admin);
        return true;
    }

    /// <summary>Toggle the player-chats-only flag (the native Game Options
    /// "Chat/Mute/Filter" family drives the same member). Rebuilds like the
    /// channel mutators.</summary>
    public void SetPlayerChatsOnly(bool on)
    {
        PlayerChatsOnly = on;
        UpdateContents();
    }

    /// <summary>Native <c>UpdateContents</c> (chatpane.cpp:286-297): rebuild
    /// the list, scroll to the bottom, autoscroll on.</summary>
    private void UpdateContents()
    {
        Offset = 0;
        AutoscrollOn();
    }

    // ── autoscroll (chatpane.cpp:167-198, 255-283) ──────────────────────────

    /// <summary>Native <c>AutoscrollOn</c>: autoscroll on, timer disarmed.</summary>
    private void AutoscrollOn()
    {
        Autoscroll = true;
        AutoscrollResumeAt = null;
    }

    /// <summary>Native <c>AutoscrollOff</c>: autoscroll off, (re-)arm the
    /// ten-second resume timer.</summary>
    private void AutoscrollOff(double nowSeconds)
    {
        Autoscroll = false;
        AutoscrollResumeAt = nowSeconds + AutoscrollResumeSeconds;
    }

    /// <summary>Native <c>OnAutoscrollTimeout</c>: scroll back to the bottom
    /// and re-enable autoscroll. Call every frame with the caller's clock.</summary>
    public void Tick(double nowSeconds)
    {
        if (!Autoscroll && AutoscrollResumeAt is { } at && nowSeconds >= at)
        {
            Offset = 0;
            AutoscrollOn();
        }
    }

    /// <summary>Whether the pane sits at the live edge (newest line visible) —
    /// native <c>IsItemVisibleByIdx(count-1)</c> (chatpane.cpp:265-267, 275-279).</summary>
    public static bool IsAtBottom(int offset, int count, int visible)
        => ChatScroll.Clamp(offset, count, visible) == 0;

    /// <summary>Native <c>PageUp</c> (chatpane.cpp:255-259): page older, then
    /// autoscroll off (arming the resume timer).</summary>
    public void PageUp(int count, int visible, double nowSeconds)
    {
        Offset = ChatScroll.PageOlder(Offset, count, visible);
        AutoscrollOff(nowSeconds);
    }

    /// <summary>Native <c>PageDown</c> (chatpane.cpp:261-270): page newer, then
    /// autoscroll on only when the newest line became visible.</summary>
    public void PageDown(int count, int visible, double nowSeconds)
    {
        Offset = ChatScroll.PageNewer(Offset, count, visible);
        OnScroll(count, visible, nowSeconds);
    }

    /// <summary>Native <c>OnScroll</c> (chatpane.cpp:272-283): any scroll event
    /// re-enables autoscroll at the bottom, pauses it (with the timer) away
    /// from the bottom. Used by the port's one-line step keys (the TK_Prev/
    /// NextCommMsg adaptation — native leaves those unbound but its wheel
    /// events route through the same PageUp/PageDown scroll path).</summary>
    public void ScrollBy(int delta, int count, int visible, double nowSeconds)
    {
        Offset = ChatScroll.Clamp(Offset + delta, count, visible);
        OnScroll(count, visible, nowSeconds);
    }

    private void OnScroll(int count, int visible, double nowSeconds)
    {
        if (IsAtBottom(Offset, count, visible))
            AutoscrollOn();
        else
            AutoscrollOff(nowSeconds);
    }

    /// <summary>Native <c>AddChatItem</c>'s scroll half (chatpane.cpp:323-339):
    /// a new line scrolls the pane only while autoscroll is on. A paused pane
    /// keeps showing the same lines, so the offset from the newest edge grows
    /// by the number of appended lines (clamped, matching the native list pane
    /// keeping its scroll position).</summary>
    public void OnMessagesAppended(int appended, int count, int visible)
    {
        if (Autoscroll)
        {
            Offset = 0;
            return;
        }
        if (appended > 0)
            Offset = ChatScroll.Clamp(Offset + appended, count, visible);
    }

    /// <summary>Re-anchor after the log shrank (e.g. the world cleared) so the
    /// offset never points past the oldest reachable line.</summary>
    public void Reclamp(int count, int visible)
        => Offset = ChatScroll.Clamp(Offset, count, visible);

    /// <summary>Native <c>m_pListPane-&gt;ScrollBottom()</c> (chatpane.cpp:196,
    /// 294-296): jump to the live edge and follow it.</summary>
    public void ScrollToBottom()
    {
        Offset = 0;
        AutoscrollOn();
    }
}
