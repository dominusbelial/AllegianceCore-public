using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

/// <summary>
/// A transient "what just happened" log — chat, kills, paydays, player-ready
/// relays — mirroring the flat, arrival-ordered, bounded <c>List&lt;T&gt;</c>
/// pattern in AllegianceCore's own <c>Allegiance.Bot.Bot.cs</c> for its
/// <c>_chats</c>/<c>_chatBuoys</c>/<c>_paydays</c>/<c>_kills</c>. The README
/// draws the entity-vs-event distinction this type embodies: ships/clusters/
/// stations are persistent <b>entity-table state</b> keyed by id and upserted,
/// whereas chat/kill/payday are <b>events</b> with no durable identity to key
/// on — purely what just happened, in arrival order.
///
/// Unlike the entity table there is deliberately <em>no upsert semantics</em>:
/// every arrival appends to its list. The lists are bounded at <see cref="Bound"/>
/// so the log is a rolling window, not an unbounded memory leak — a sane cap for
/// a real game session that can accumulate hundreds of chat lines / payday ticks
/// per hour.
///
/// This type is mutation-free from a caller's perspective: <see cref="ClientWorld"/>
/// (same assembly, same lock domain) calls the internal <c>Record</c> methods on
/// the receive path, and readers snapshot the <c>IReadOnlyList</c> properties.
/// </summary>
public sealed class EventLog
{
    /// <summary>Rolling-window cap: after this many arrivals the oldest entry
    /// is evicted (front of the list) so memory stays bounded. Applies per
    /// list.</summary>
    public const int Bound = 500;

    private readonly List<ChatMessage> _chats = new();
    private readonly List<ChatBuoy> _chatBuoys = new();
    private readonly List<Payday> _paydays = new();
    private readonly List<KillShip> _kills = new();
    private readonly List<PlayerReady> _playerReadyEchoes = new();

    /// <summary>Chat relayed by the server (CHATMESSAGE, 23), in arrival order.</summary>
    public IReadOnlyList<ChatMessage> Chats => _chats;

    /// <summary>Chat targeting a buoy object (CHATBUOY, 108), in arrival order.</summary>
    public IReadOnlyList<ChatBuoy> ChatBuoys => _chatBuoys;

    /// <summary>Per-side payday payouts (PAYDAY, 114), in arrival order.</summary>
    public IReadOnlyList<Payday> Paydays => _paydays;

    /// <summary>Ship deaths (KILL_SHIP, 105), in arrival order.</summary>
    public IReadOnlyList<KillShip> Kills => _kills;

    /// <summary>The server's relay of a player-ready announce (PLAYER_READY, 45),
    /// in arrival order — echoes of the sender's own or a teammate's ready-state
    /// toggle.</summary>
    public IReadOnlyList<PlayerReady> PlayerReadyEchoes => _playerReadyEchoes;

    // ── P321 — drop all accumulated events (called from ClientWorld.Reset on a
    // fresh Connect so a retry starts from a genuinely clean world).
    internal void Clear()
    {
        _chats.Clear();
        _chatBuoys.Clear();
        _paydays.Clear();
        _kills.Clear();
        _playerReadyEchoes.Clear();
    }

    // ── internal append sites, driven by ClientWorld.Apply on the receive thread ──
    internal void Record(ChatMessage m) => Append(_chats, m);
    internal void Record(ChatBuoy m) => Append(_chatBuoys, m);
    internal void Record(Payday m) => Append(_paydays, m);
    internal void Record(KillShip m) => Append(_kills, m);
    internal void Record(PlayerReady m) => Append(_playerReadyEchoes, m);

    private static void Append<T>(List<T> list, T item)
    {
        list.Add(item);
        if (list.Count > Bound) list.RemoveAt(0);
    }
}
