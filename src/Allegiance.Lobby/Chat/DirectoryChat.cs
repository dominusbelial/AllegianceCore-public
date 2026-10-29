using System.Collections.Concurrent;

namespace Allegiance.Lobby.Chat;

/// <summary>
/// P871 / lobby::Lobby chat — the directory-wide chat relay, the HTTP-lobby
/// analogue of native's <c>FM_CS_CHATMESSAGE</c> relay
/// (<c>clintlib/appmsg.cpp:2640-2665</c>): one authenticated sender, a channel,
/// a monotonic sequence, and the line relayed to every reader of that channel.
///
/// <para><b>Native semantics preserved.</b> The sender is identified by their
/// account (native's <c>cd.sidSender</c>); the sender's own echo is not delivered
/// back as a relay (native's <c>if (pfmChat-&gt;cd.sidSender !=
/// m_ship-&gt;GetObjectID())</c> — here the POST response is the acknowledgement and
/// readers are told to skip their own lines). Muting is per-account and by
/// sender, so a muted player's lines never reach the muter (native's chat-pane
/// mute semantics), and lines are capped/trimmed like the native chat buffer.
/// </para>
///
/// <para>In-memory by design, like <see cref="ServerRegistry"/>: the directory
/// chat is ephemeral session chatter, not a durable record. Time is injected so
/// tests can age/expire entries.</para>
/// </summary>
public sealed class DirectoryChat
{
    /// <summary>The directory-wide channel every client sees.</summary>
    public const string DirectoryChannel = "directory";

    public const int MaxTextLength = 240;
    public const int MaxHistory = 500;
    private const int MaxChannels = 512;
    private const int MaxMutedPerAccount = 256;

    private readonly object _gate = new();
    private readonly List<ChatLine> _lines = new();
    private readonly HashSet<string> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _mutes = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _sequence;

    public DirectoryChat(TimeProvider time)
    {
        _time = time;
        _channels.Add(DirectoryChannel);
    }

    /// <summary>One relayed line.</summary>
    public sealed record ChatLine(long Sequence, DateTimeOffset AtUtc, string Channel,
        string SenderAccountId, string Sender, string Text);

    /// <summary>Channel names are constrained to the directory channel or a
    /// per-server channel, so the store's channel set is bounded.</summary>
    public static bool IsValidChannel(string? channel) =>
        channel is DirectoryChannel
        || (channel is { Length: > 7 and <= 80 } && channel.StartsWith("server:", StringComparison.Ordinal)
            && channel.AsSpan(7).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-".AsSpan()) < 0);

    /// <summary>Post a line. Returns the accepted line (with its sequence — the
    /// relay feedback native's ack carries), or null with a reason when the
    /// message is empty/too long, the channel is unknown, or the sender is
    /// muted by nobody (mutes affect delivery, not posting).</summary>
    public ChatLine? Post(string accountId, string sender, string channel, string text, out string? error)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountId);
        error = null;
        string body = (text ?? "").Trim();
        if (body.Length == 0) { error = "message is required"; return null; }
        if (body.Length > MaxTextLength) body = body[..MaxTextLength];
        if (!IsValidChannel(channel)) { error = "unknown channel"; return null; }
        if (string.IsNullOrWhiteSpace(sender)) { error = "sender identity required"; return null; }
        sender = sender.Trim();
        if (sender.Length > 24) sender = sender[..24];

        lock (_gate)
        {
            if (_channels.Count >= MaxChannels && !_channels.Contains(channel))
            {
                error = "too many channels";
                return null;
            }
            _channels.Add(channel);
            var line = new ChatLine(++_sequence, _time.GetUtcNow(), channel, accountId, sender, body);
            _lines.Add(line);
            if (_lines.Count > MaxHistory) _lines.RemoveRange(0, _lines.Count - MaxHistory);
            return line;
        }
    }

    /// <summary>Lines on <paramref name="channel"/> after
    /// <paramref name="sinceSequence"/>, excluding senders
    /// <paramref name="readerAccountId"/> muted. The reader's own lines are
    /// omitted from the relay (native's sender-echo suppression).</summary>
    public IReadOnlyList<ChatLine> Read(string channel, long sinceSequence, string? readerAccountId)
    {
        lock (_gate)
        {
            var muted = readerAccountId is not null && _mutes.TryGetValue(readerAccountId, out var set) ? set : null;
            return _lines
                .Where(l => l.Channel == channel && l.Sequence > sinceSequence)
                // The reader's own lines are acknowledged by the POST response;
                // the live relay never echoes them (native's sidSender check). A
                // history read (since=0) still returns them so a reconnecting
                // client can rebuild the log.
                .Where(l => sinceSequence == 0 || l.SenderAccountId != readerAccountId)
                .Where(l => muted is null || !muted.Contains(l.SenderAccountId))
                .ToList();
        }
    }

    /// <summary>Mute a sender for the reader (native chat-pane mute). Returns
    /// false for a self-mute or an empty identity.</summary>
    public bool Mute(string readerAccountId, string mutedAccountId)
    {
        if (string.IsNullOrEmpty(readerAccountId) || string.IsNullOrEmpty(mutedAccountId)) return false;
        if (readerAccountId == mutedAccountId) return false;
        lock (_gate)
        {
            if (!_mutes.TryGetValue(readerAccountId, out var set))
                _mutes[readerAccountId] = set = new HashSet<string>(StringComparer.Ordinal);
            if (set.Count >= MaxMutedPerAccount) return false;
            set.Add(mutedAccountId);
            return true;
        }
    }

    /// <summary>Unmute a sender.</summary>
    public bool Unmute(string readerAccountId, string mutedAccountId)
    {
        lock (_gate)
            return _mutes.TryGetValue(readerAccountId, out var set) && set.Remove(mutedAccountId);
    }

    /// <summary>The reader's muted account ids.</summary>
    public IReadOnlyCollection<string> Muted(string readerAccountId)
    {
        lock (_gate)
            return _mutes.TryGetValue(readerAccountId, out var set) ? set.ToArray() : Array.Empty<string>();
    }

    /// <summary>Known channel names (directory first, then per-server).</summary>
    public IReadOnlyList<string> Channels()
    {
        lock (_gate) return _channels.OrderBy(c => c, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Current highest sequence (the relay feedback cursor).</summary>
    public long Sequence { get { lock (_gate) return _sequence; } }
}
