namespace Allegiance.Server;

/// <summary>
/// P193 — the in-memory, <b>connection-lifetime-only</b> per-player
/// score/rank foundation. P215 (Stage 1 of the player-scoring plan) extends
/// it with the real kill/death counters and the Elo-style combat rating that
/// combat now actually feeds.
/// </summary>
/// <remarks>
/// <para><b>Deliberately NOT a port of native's full
/// <c>PlayerScoreObject</c></b> (the managed shape reconstructed in
/// <c>igcWrapper.h:2142-2200</c> exposes dozens of live-session counters —
/// kills by ship class, base kills/captures, warps/asteroids spotted, techs
/// recovered, repair contribution, connect/command time… — all feeding a
/// <c>CalculateScore</c> formula). This port implements none of those
/// counters except the three P215's combat slice populates: <see cref="Kills"/>,
/// <see cref="Deaths"/> and <see cref="CombatRating"/> — the Stage-1 slice
/// that finally makes this object reflect real gameplay. It otherwise carries
/// only the two fields P194 (<c>GAME_OVER_PLAYERS</c>, 63) and P195
/// (<c>RANDOMIZE_TEAMS</c>, 21) actually consume: <see cref="Rank"/>
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
/// <para><b>Stage-1 combat mutation (P215).</b> As of P215 the claim that
/// "nothing ever mutates these values from combat" is <em>false</em> for
/// exactly three of them: <see cref="Server.BroadcastKillShip"/> calls
/// <see cref="AddKill"/> on a player's <see cref="Kills"/>, <see cref="AddDeath"/>
/// on the victim's <see cref="Deaths"/>, and <see cref="AdjustCombatRating"/>
/// on both, following native's <c>FedSrv.CPP</c> kill handling. <see cref="Score"/>
/// and <see cref="Rank"/> remain <b>unmutated</b> until Stage 2/Stage 3 of the
/// scoring plan respectively.</para>
/// </remarks>
public sealed class PlayerScore
{
    /// <summary>Index of <c>c_fcidRatingAdd</c> (28) in the core's
    /// <c>DataConstantsIGC.FloatConstants</c> array (<c>src/Igc/igc.h:92</c>).
    /// PCore001.igc bakes <c>1.25f</c> there (<c>c_crAdd</c>,
    /// <c>missionigc.cpp:2719</c>).</summary>
    internal const int CombatRatingAddIndex = 28;

    /// <summary>Index of <c>c_fcidRatingDivide</c> (29) in the core's
    /// <c>DataConstantsIGC.FloatConstants</c> array (<c>src/Igc/igc.h:93</c>).
    /// PCore001.igc bakes <c>100.0f</c> there (<c>c_crDivide</c>,
    /// <c>missionigc.cpp:2720</c>).</summary>
    internal const int CombatRatingDivideIndex = 29;

    /// <summary>The player's rank index (native <c>GetRank()</c>, a short).
    /// Defaults to <c>0</c> — the neutral seed for every connection (see the
    /// rank-seeding note in the remarks). Unmutated in Stage 1 (P215).</summary>
    public short Rank { get; set; }

    /// <summary>The player's score (native <c>GetScore()</c>/<c>SetScore</c>,
    /// a float). Defaults to <c>0</c>. Deliberately unmutated in Stage 1 —
    /// the full <c>CalculateScore</c> formula is Stage 2.</summary>
    public float Score { get; set; }

    /// <summary>The player's kill count (native <c>GetKills()</c>/<c>AddKill()</c>,
    /// <c>m_cKills</c>, <c>igc.h:5940</c>). Incremented by
    /// <see cref="Server.BroadcastKillShip"/> for every real cross-side kill
    /// where the killer is a player-owned ship. Defaults to <c>0</c>.</summary>
    public int Kills { get; set; }

    /// <summary>The player's death count (native <c>GetDeaths()</c>/<c>AddDeath()</c>,
    /// <c>m_cDeaths</c>, <c>igc.h:5942</c>). Incremented by
    /// <see cref="Server.BroadcastKillShip"/> on every real death of a
    /// player-owned ship (including suicide/environmental deaths — native
    /// counts those, <c>FedSrv.CPP:12026-12032</c>). Defaults to <c>0</c>.</summary>
    public int Deaths { get; set; }

    /// <summary>The player's Elo-style combat rating (native
    /// <c>GetCombatRating()</c>/<c>SetCombatRating()</c>,
    /// <c>m_fCombatRating</c>). Defaults to the native neutral seed
    /// <c>0.0f</c> (<c>igc.h:5510</c> constructor initializer, and the
    /// <c>PersistPlayerScoreObject::Reset()</c> value at <c>igc.h:5453</c>).
    /// Updated on every real two-player kill via
    /// <see cref="AdjustCombatRating"/>.</summary>
    public float CombatRating { get; set; }

    /// <summary>Increment <see cref="Kills"/> — the port of native
    /// <c>PlayerScoreObject::AddKill()</c> (<c>m_cKills++</c>, <c>igc.h:5819</c>).</summary>
    public void AddKill() => Kills++;

    /// <summary>Increment <see cref="Deaths"/> — the port of native
    /// <c>PlayerScoreObject::AddDeath()</c> (<c>m_cDeaths++</c>, <c>igc.h:5837</c>).</summary>
    public void AddDeath() => Deaths++;

    /// <summary>
    /// P215 — port of native <c>PlayerScoreObject::AdjustCombatRating</c>
    /// (<c>src/Igc/common.cpp:2706-2717</c>), the Elo-style skill-rating update
    /// applied on every real kill. Pure, testable math operating on the two
    /// participating <see cref="PlayerScore"/> objects.
    /// </summary>
    /// <remarks>
    /// The native formula is:
    /// <c>k = (ratingAdd + killeeRating - killerRating) / ratingDivide;
    /// killerRating = killerRating·(1−k) + k;  killeeRating = killeeRating·(1−k)</c>.
    /// The two tuning constants are the IGC core-file float constants
    /// <c>c_fcidRatingAdd</c>=28 and <c>c_fcidRatingDivide</c>=29
    /// (<c>igc.h:92-93</c>), read from the loaded core by
    /// <see cref="Server.BroadcastKillShip"/> and passed in here — keeping this
    /// method free of any core/mission dependency so the math is independently
    /// testable.
    /// </remarks>
    /// <param name="ratingAdd">The <c>c_fcidRatingAdd</c> core constant.</param>
    /// <param name="ratingDivide">The <c>c_fcidRatingDivide</c> core constant.</param>
    /// <param name="killer">The killer's score object (mutated upward on average).</param>
    /// <param name="killee">The killee's score object (mutated downward on average).</param>
    public static void AdjustCombatRating(
        float ratingAdd, float ratingDivide, PlayerScore killer, PlayerScore killee)
    {
        float k = (ratingAdd + killee.CombatRating - killer.CombatRating) / ratingDivide;
        killer.CombatRating = killer.CombatRating * (1.0f - k) + k;
        killee.CombatRating = killee.CombatRating * (1.0f - k);
    }
}
