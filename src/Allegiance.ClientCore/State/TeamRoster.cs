using System.Collections.Generic;

namespace Allegiance.ClientCore.State;

/// <summary>
/// The current team-lobby roster — "who is ready right now", keyed by ship id.
/// This is the <b>current-state</b> half of PLAYER_READY(45), distinct from the
/// arrival-ordered history in <see cref="EventLog.PlayerReadyEchoes"/>: the
/// history tells you <em>that</em> a ready announce happened (and when); this
/// map answers the question a lobby UI (or a headless test) actually asks —
/// is ship X <em>currently</em> marked ready?
///
/// Semantic contrast drawn in the README: entity-table state is upserted by id
/// (current state), whereas the event log is append-only arrival-order state.
/// <see cref="ReadyByShipId"/> follows the former: applying a ready toggle for
/// a ship id upserts that ship's ready flag (LAST write wins), it never appends.
/// This mirrors how the native client's Team Lobby works — the server relays a
/// ready announce ("sent to everyone on team"), the client-side handler updates
/// the single roster entry for that ship (<c>PlayerInfo::Update</c>,
/// <c>m_fmPlayerInfo.fReady = ...</c>) rather than logging each toggle.
///
/// Mutation happens only on <see cref="ClientWorld"/>'s receive path (same
/// assembly, same lock domain); readers snapshot the
/// <see cref="IReadOnlyDictionary{TKey,TValue}"/> without locking, consistent
/// with how the rest of <see cref="ClientWorld"/> exposes its tables.
/// </summary>
public sealed class TeamRoster
{
    private readonly Dictionary<short, bool> _readyByShipId = new();

    /// <summary>Ship id → whether that ship is <em>currently</em> marked ready.
    /// Empty until the first PLAYER_READY relay lands; a later toggle for the
    /// same ship id replaces the flag (last write wins — current state, not
    /// history).</summary>
    public IReadOnlyDictionary<short, bool> ReadyByShipId => _readyByShipId;

    // ── internal write site, driven by ClientWorld.Apply on the receive thread ──
    internal void Set(short shipId, bool fReady) => _readyByShipId[shipId] = fReady;
}
