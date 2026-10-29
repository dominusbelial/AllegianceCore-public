using System;
using System.Collections.Generic;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame.Chat;

/// <summary>One directory-chat line as the lobby relay carries it. The directory
/// channel is a PORT EXTENSION: native has no lobby chat (its chat is in-mission
/// only, <c>Igc/igc.h:2731-2734</c>), so P871's cite of
/// <c>clintlib/appmsg.cpp:2640-2665</c> described the in-mission
/// <c>FM_CS_CHATMESSAGE</c> relay, not this.</summary>
public sealed record DirectoryChatLine(long Sequence, string Channel, string SenderId,
    string Sender, string Text, DateTimeOffset At);

/// <summary>
/// P871 / lobby::Lobby chat — the client's directory-chat window: a bounded log
/// of relayed lines plus the reader's mute list and the native echo/filter rules.
///
/// <para><b>Borrowed native semantics.</b> A player's own line is shown locally
/// when they send it and is never re-shown from the relay (native ignores its own
/// echo in <c>ReceiveChat</c>, <c>WinTrek/trekigc.cpp:4492</c>); a muted sender's
/// lines are dropped (same line: <c>ppi-&gt;GetMute()</c>, the per-player
/// <c>PlayerInfo::SetMute</c> of <c>clintlib/clintlib.h:282,326</c>). Lines are
/// formatted like the game pane (<see cref="Rendering.ChatLogFormat"/>) so the
/// directory channel appears in the same log in BOTH HUD styles (Classic and
/// Modern share it).</para>
/// </summary>
public sealed class DirectoryChatLog
{
    /// <summary>Maximum retained lines (the native chat pane's own window is
    /// smaller; the log keeps a little history for scrolling/reconnect).</summary>
    public const int MaxLines = 200;

    private readonly List<DirectoryChatLine> _lines = new();
    private readonly HashSet<string> _muted = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public string Channel { get; }

    /// <summary>The highest sequence consumed (the next read cursor).</summary>
    public long Cursor { get; private set; }

    public DirectoryChatLog(string channel = "directory") => Channel = channel;

    /// <summary>The visible log, oldest first.</summary>
    public IReadOnlyList<DirectoryChatLine> Lines
    {
        get { lock (_gate) return _lines.ToArray(); }
    }

    public IReadOnlyCollection<string> Muted
    {
        get { lock (_gate) return _muted.ToArray(); }
    }

    public bool IsMuted(string senderId) { lock (_gate) return _muted.Contains(senderId); }

    public void Mute(string senderId) { lock (_gate) _muted.Add(senderId); }
    public void Unmute(string senderId) { lock (_gate) _muted.Remove(senderId); }

    /// <summary>
    /// Accept a poll response: advance the cursor to the highest sequence and
    /// append the lines that pass the reader's rules. <paramref name="ownAccountId"/>
    /// suppresses the reader's own relay echo (native's sender check,
    /// <c>appmsg.cpp:2645-2647</c>); <paramref name="historyRebuild"/> keeps them
    /// for a reconnecting client rebuilding its log. Muted senders are always
    /// dropped. Returns the newly visible lines.
    /// </summary>
    public IReadOnlyList<DirectoryChatLine> Accept(IEnumerable<DirectoryChatLine> relayed,
        string? ownAccountId, bool historyRebuild = false)
    {
        var added = new List<DirectoryChatLine>();
        lock (_gate)
        {
            foreach (var line in relayed.OrderBy(l => l.Sequence))
            {
                if (line.Sequence <= Cursor) continue;
                Cursor = line.Sequence;
                if (_muted.Contains(line.SenderId)) continue;
                if (!historyRebuild && ownAccountId is not null && line.SenderId == ownAccountId) continue;
                _lines.Add(line);
                added.Add(line);
            }
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        }
        return added;
    }

    /// <summary>Append the reader's own sent line locally (native echoes the
    /// sender's own chat immediately, before/without the relay).</summary>
    public DirectoryChatLine AppendOwn(long sequence, string senderId, string sender, string text, DateTimeOffset at)
    {
        var line = new DirectoryChatLine(sequence, Channel, senderId, sender, text, at);
        lock (_gate)
        {
            _lines.Add(line);
            if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
            if (sequence > Cursor) Cursor = sequence;
        }
        return line;
    }

    /// <summary>The formatted pane line (the same sender/text convention the
    /// game chat log uses, so both HUD styles render it identically).</summary>
    public static string Format(DirectoryChatLine line) =>
        Rendering.ChatLogFormat.Format(line.Sender, line.Text, Allegiance.Core.Sim.ChatTarget.Everyone);
}
