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
    /// <summary>P1024 (R1 F12) — channel-table cap. Only channels that still have a
    /// line in the bounded history keep a slot (see <see cref="PruneEmptyChannelsLocked"/>),
    /// so this cap can never be permanently filled by made-up names.</summary>
    public const int MaxChannels = 512;
    /// <summary>P1024 (R1 F12) — mutes per account. The muted id must be a
    /// well-formed account id (<see cref="IsAccountId"/>), so a list is 256 × 32
    /// characters at worst.</summary>
    public const int MaxMutedPerAccount = 256;
    /// <summary>P1024 (R1 F12) — total muted ids across every account, and the
    /// bound on how many accounts may hold a list at all. Together they bound the
    /// mute table's memory for the life of the process (no pruning is possible:
    /// a mute has no expiry in native either).</summary>
    public const int MaxMuteEntries = 8192;
    public const int MaxMutedAccounts = 512;
    private const int AccountIdLength = 32;

    private readonly object _gate = new();
    private readonly List<ChatLine> _lines = new();
    private readonly HashSet<string> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _mutes = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private long _sequence;
    private int _muteEntries;

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

    /// <summary>P1024 (R1 F12) — a mute target (and a mute list's owner) is an
    /// account id, which this lobby mints as a 32-digit GUID (<c>Guid.ToString("N")</c>,
    /// <c>IdentityStore</c>), never an arbitrary string. Native's mute was a boolean
    /// flag on a real player entry in the roster (<c>WinTrek/WinTrek.cpp:3731-3734</c>,
    /// <c>WinTrek/trekigc.cpp:4492-4495</c>), not free-form text.</summary>
    public static bool IsAccountId(string? id) => id is { Length: AccountIdLength } && id.All(Uri.IsHexDigit);

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
            // P1024 (R1 F12) — a channel keeps a slot only while it still has a line
            // in the bounded history; made-up names cannot own the table for good.
            PruneEmptyChannelsLocked();
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

    /// <summary>
    /// P1024 (R1 F12) — drop channels that no longer carry a line. Because the line
    /// history is capped at <see cref="MaxHistory"/> (500), at most that many channels
    /// can still hold a line, so at least <c>MaxChannels - MaxHistory</c> slots are
    /// always free: one account posting to hundreds of made-up <c>server:…</c> names
    /// can never lock a real server's channel out of the table. History is untouched —
    /// only empty names leave.
    /// </summary>
    private void PruneEmptyChannelsLocked()
    {
        if (_channels.Count < MaxChannels) return;
        var live = new HashSet<string>(StringComparer.Ordinal) { DirectoryChannel };
        foreach (var line in _lines) live.Add(line.Channel);
        _channels.RemoveWhere(c => !live.Contains(c));
    }

    /// <summary>Lines on <paramref name="channel"/> after
    /// <paramref name="sinceSequence"/>, excluding senders
    /// <paramref name="readerAccountId"/> muted. The reader's own lines are
    /// omitted from the relay (native's sender-echo suppression).</summary>
    public IReadOnlyList<ChatLine> Read(string channel, long sinceSequence, string? readerAccountId)
    {
        lock (_gate)
        {
            var muted = readerAccountId is not null && _mutes.TryGetValue(readerAccountId.ToLowerInvariant(), out var set) ? set : null;
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
    /// false for a self-mute, an empty identity, or an id that is not a well-formed
    /// account id (P1024 / R1 F12: the id is stored for the life of the process, so
    /// only real 32-digit account ids are accepted), and once the per-account or the
    /// directory-wide mute bound is reached.</summary>
    public bool Mute(string readerAccountId, string mutedAccountId)
    {
        if (!IsAccountId(readerAccountId) || !IsAccountId(mutedAccountId)) return false;
        string reader = readerAccountId.ToLowerInvariant();
        string muted = mutedAccountId.ToLowerInvariant();
        if (reader == muted) return false;
        lock (_gate)
        {
            if (!_mutes.TryGetValue(reader, out var set))
            {
                if (_mutes.Count >= MaxMutedAccounts || _muteEntries >= MaxMuteEntries) return false;
                _mutes[reader] = set = new HashSet<string>(StringComparer.Ordinal);
            }
            if (set.Contains(muted)) return true;
            if (set.Count >= MaxMutedPerAccount || _muteEntries >= MaxMuteEntries) return false;
            set.Add(muted);
            _muteEntries++;
            return true;
        }
    }

    /// <summary>Unmute a sender. Only a well-formed account id can be on a list,
    /// so anything else is not a mute this reader holds.</summary>
    public bool Unmute(string readerAccountId, string mutedAccountId)
    {
        if (!IsAccountId(readerAccountId) || !IsAccountId(mutedAccountId)) return false;
        lock (_gate)
        {
            if (!_mutes.TryGetValue(readerAccountId.ToLowerInvariant(), out var set)
                || !set.Remove(mutedAccountId.ToLowerInvariant())) return false;
            _muteEntries--;
            return true;
        }
    }

    /// <summary>The reader's muted account ids.</summary>
    public IReadOnlyCollection<string> Muted(string readerAccountId)
    {
        lock (_gate)
            return _mutes.TryGetValue(readerAccountId.ToLowerInvariant(), out var set) ? set.ToArray() : Array.Empty<string>();
    }

    /// <summary>Known channel names (directory first, then per-server).</summary>
    public IReadOnlyList<string> Channels()
    {
        lock (_gate) return _channels.OrderBy(c => c, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Current highest sequence (the relay feedback cursor).</summary>
    public long Sequence { get { lock (_gate) return _sequence; } }
}
