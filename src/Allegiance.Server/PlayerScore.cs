namespace Allegiance.Server;

/// <summary>
/// P193 — the in-memory, <b>connection-lifetime-only</b> per-player
/// score/rank foundation.
/// </summary>
/// <remarks>
/// <para><b>Deliberately NOT a port of native's full
/// <c>PlayerScoreObject</c></b> (the managed shape reconstructed in
/// <c>igcWrapper.h:2142-2200</c> exposes dozens of live-session counters —
/// kills by ship class, base kills/captures, warps/asteroids spotted, techs
/// recovered, repair contribution, connect/command time… — all feeding a
/// <c>CalculateScore</c> formula). This port implements none of those
/// counters. It carries only the two fields P194 (<c>GAME_OVER_PLAYERS</c>,
/// 63) and P195 (<c>RANDOMIZE_TEAMS</c>, 21) actually consume: <see cref="Rank"/>
/// and <see cref="Score"/>.</para>
///
/// <para><b>Non-persistence is deliberate and structural.</b> This port has no
/// account/login/identity layer (the <c>ALLSRV_STANDALONE</c>-equivalent
/// design documented in [[allegiance-team-lobby-plan]]), so nothing survives a
/// reconnect. One fresh instance is allocated per connection at logon (see
/// <c>Server.AssignShip</c>) and discarded when the peer is released
/// (<c>Server.ReleasePeer</c> → the <c>LiveShip</c> is removed from
/// <c>_peerShips</c>). Because native's persisted score object
/// (<c>GetPersistPlayerScore(civId)</c>) and live score object
/// (<c>GetScore()</c>) are read from different stores but this port has no
/// persistence at all, both collapse to this same in-memory value.</para>
///
/// <para><b>Rank seeding.</b> With no rank data anywhere (no accounts, no
/// historical games), every player's <see cref="Rank"/> starts at the neutral
/// default <c>0</c>. Consequently P195's "assign highest-rank-first"
/// side-assignment algorithm degrades to "assign in arrival/iteration order"
/// until something — deliberately <em>not</em> this task — ever changes a
/// player's <see cref="Rank"/>. This is a known Stage-1 limitation, not a bug
/// to silently work around.</para>
///
/// <para>Nothing in this port ever mutates these values from combat, mining,
/// or any other gameplay event. P194 and P195 only <em>read</em> them.</para>
/// </remarks>
public sealed class PlayerScore
{
    /// <summary>The player's rank index (native <c>GetRank()</c>, a short).
    /// Defaults to <c>0</c> — the neutral seed for every connection (see the
    /// rank-seeding note in the remarks).</summary>
    public short Rank { get; set; }

    /// <summary>The player's score (native <c>GetScore()</c>/<c>SetScore</c>,
    /// a float). Defaults to <c>0</c>.</summary>
    public float Score { get; set; }
}
