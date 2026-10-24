namespace Allegiance.Server;

/// <summary>
/// P193 — the in-memory, <b>connection-lifetime-only</b> per-player
/// score/rank foundation. P215 (Stage 1 of the player-scoring plan) extended
/// it with the real kill/death counters and the Elo-style combat rating; P583
/// (Stage 2) completes it into a faithful port of native's full
/// <c>PlayerScoreObject</c>: every live-session counter native tracks and the
/// complete <c>CalculateScore</c> formula (<c>Igc/common.cpp:2667-2695</c>,
/// <c>igc.h:5502-5966</c>).
/// </summary>
/// <remarks>
/// <para><b>One instance per connection, not persisted.</b> This port has no
/// account/login/identity layer, so nothing survives a reconnect — one fresh
/// instance is allocated per connection at logon (see
/// <c>Server.AssignShip</c>, <c>Server.Lobby.cs:2537</c>) and discarded when
/// the peer is released (<c>Server.ReleasePeer</c> → the <c>LiveShip</c> is
/// removed from <c>_peerShips</c>). Native's persisted score object
/// (<c>GetPersistPlayerScore(civId)</c>) and live score object
/// (<c>GetScore()</c>) collapse to this same in-memory value (no persistence —
/// the P583 doc says persistent stats DB / rank progression across games is
/// out of scope).</para>
///
/// <para><b>Stage-2 counters (P583).</b> All of native
/// <c>PlayerScoreObject</c>'s live counters are now present — kills by victim
/// class (miner / builder / layer / carrier / player, <c>m_cMinerKills</c>…
/// <c>igc.h:5924-5928</c>), base kills/captures (<c>m_cBaseKills</c>/
/// <c>m_cBaseCaptures</c>), warps/asteroids/high-value targets spotted
/// (<c>m_cWarpsSpotted</c>/<c>m_cAsteroidsSpotted</c>/
/// <c>m_cHighValueTargetsSpotted</c>), techs recovered (<c>m_cTechsRecovered</c>),
/// artifacts and flags (<c>m_cArtifacts</c>/<c>m_cFlags</c>), rescues
/// (<c>m_cRescues</c>), repair (<c>m_cRepair</c>), ejections (<c>m_cEjections</c>),
/// time played (<c>m_dtPlayed</c>), win (<c>m_bWin</c>) and rank ratio
/// (<c>m_rankRatio</c>) — plus the pre-existing Kills/Deaths/CombatRating. Each
/// counter is fed from the port's existing events (see the wiring in
/// <c>Server.Combat.cs</c>, <c>Server.Docking.cs</c>, <c>Server.Lobby.cs</c>);
/// the ones no event feeds yet are listed as gaps in
/// <c>docs/reports/P583_NATIVE_SCORING.md</c>, never invented.</para>
///
/// <para><b>Constants come from the loaded core, not hard-coded.</b>
/// <see cref="CalculateScore"/> reads its point values from the mission's
/// core <c>FloatConstants</c> array (indices <c>c_fcidPointsWarp</c>=15 …
/// <c>c_fcidPointsRescues</c>=27, <c>igc.h:77-90</c>; the PCore001 shipped
/// defaults live at <c>missionigc.cpp:2704-2716</c> as <c>c_pointsWarp</c>…).
/// The caller passes the array in (the same testability seam
/// <see cref="AdjustCombatRating"/> already establishes), so the math is pure
/// and independently testable against hand-computed native values.</para>
/// </remarks>
public sealed class PlayerScore
{
    /// <summary>Index of <c>c_fcidPointsWarp</c> (15) in the core's
    /// <c>DataConstantsIGC.FloatConstants</c> array (<c>src/Igc/igc.h:77</c>).</summary>
    internal const int PointsWarpIndex = 15;
    /// <summary>Index of <c>c_fcidPointsAsteroid</c> (16) (<c>igc.h:78</c>).</summary>
    internal const int PointsAsteroidIndex = 16;
    /// <summary>Index of <c>c_fcidPointsTech</c> (17) (<c>igc.h:79</c>).</summary>
    internal const int PointsTechIndex = 17;
    /// <summary>Index of <c>c_fcidPointsMiner</c> (18) (<c>igc.h:80</c>).</summary>
    internal const int PointsMinerIndex = 18;
    /// <summary>Index of <c>c_fcidPointsBuilder</c> (19) (<c>igc.h:81</c>).</summary>
    internal const int PointsBuilderIndex = 19;
    /// <summary>Index of <c>c_fcidPointsLayer</c> (20) (<c>igc.h:82</c>).</summary>
    internal const int PointsLayerIndex = 20;
    /// <summary>Index of <c>c_fcidPointsCarrier</c> (21) (<c>igc.h:83</c>).</summary>
    internal const int PointsCarrierIndex = 21;
    /// <summary>Index of <c>c_fcidPointsPlayer</c> (22) (<c>igc.h:84</c>).</summary>
    internal const int PointsPlayerIndex = 22;
    /// <summary>Index of <c>c_fcidPointsBaseKill</c> (23) (<c>igc.h:85</c>).</summary>
    internal const int PointsBaseKillIndex = 23;
    /// <summary>Index of <c>c_fcidPointsBaseCapture</c> (24) (<c>igc.h:86</c>).</summary>
    internal const int PointsBaseCaptureIndex = 24;
    /// <summary>Index of <c>c_fcidPointsFlags</c> (25) (<c>igc.h:88</c>).</summary>
    internal const int PointsFlagsIndex = 25;
    /// <summary>Index of <c>c_fcidPointsArtifacts</c> (26) (<c>igc.h:89</c>).</summary>
    internal const int PointsArtifactsIndex = 26;
    /// <summary>Index of <c>c_fcidPointsRescues</c> (27) (<c>igc.h:90</c>).</summary>
    internal const int PointsRescuesIndex = 27;
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

    /// <summary>
    /// The victim classes native <c>PlayerScoreObject::KillShip</c> buckets a
    /// kill into (<c>igc.h:5675-5706</c>, switched on
    /// <c>pship-&gt;GetPilotType()</c>: <c>c_ptMiner</c>=0,
    /// <c>c_ptBuilder</c>=6, <c>c_ptLayer</c>=5, <c>c_ptCarrier</c>=9,
    /// default → player). Used to pick which class counter
    /// <see cref="KillShip"/> increments.
    /// </summary>
    public enum VictimClass
    {
        /// <summary>A mining hull (<c>c_ptMiner</c>).</summary>
        Miner,
        /// <summary>A builder / constructor hull (<c>c_ptBuilder</c>).</summary>
        Builder,
        /// <summary>A layer hull (<c>c_ptLayer</c>).</summary>
        Layer,
        /// <summary>A carrier hull (<c>c_ptCarrier</c>).</summary>
        Carrier,
        /// <summary>Everything else — fighters, scouts, interceptors, bombers,
        /// HTTs (native's <c>default</c> branch).</summary>
        Player,
    }

    /// <summary>The player's rank index (native <c>GetRank()</c>, a short).
    /// Defaults to <c>0</c> — the neutral seed for every connection. Seeded
    /// from nowhere (no persistence); a rank of 0 counts as 1 in the
    /// rank-ratio computation exactly like native's
    /// <c>GetSideRankSum</c> (<c>iTempRank &lt; 1 ? 1 : iTempRank</c>,
    /// <c>fsmission.cpp:1365</c>).</summary>
    public short Rank { get; set; }

    /// <summary>The player's score (native <c>GetScore()</c>/<c>SetScore</c>,
    /// a float). Populated by <see cref="CalculateScore"/> at game over
    /// (and refreshed in the periodic roster).</summary>
    public float Score { get; set; }

    /// <summary>The player's kill count (native <c>GetKills()</c>/<c>AddKill()</c>,
    /// <c>m_cKills</c>, <c>igc.h:5940</c>). Incremented by
    /// <see cref="BroadcastKillShip"/> for every real cross-side kill
    /// where the killer is a player-owned ship.</summary>
    public int Kills { get; set; }

    /// <summary>The player's death count (native <c>GetDeaths()</c>/<c>AddDeath()</c>,
    /// <c>m_cDeaths</c>, <c>igc.h:5942</c>). Incremented on every real death of a
    /// player-owned ship (including suicide/environmental deaths — native counts
    /// those, <c>FedSrv.CPP:12026-12032</c>).</summary>
    public int Deaths { get; set; }

    /// <summary>The player's ejection count (native <c>GetEjections()</c>/
    /// <c>AddEjection()</c>, <c>m_cEjections</c>, <c>igc.h:5943</c>).
    /// Incremented when a player ship ejects into a lifepod instead of dying
    /// (<c>FedSrv.CPP:11919-11921</c>).</summary>
    public int Ejections { get; set; }

    /// <summary>The player's rescue count (native <c>GetRescues()</c>/
    /// <c>AddRescue()</c>, <c>m_cRescues</c>, <c>igc.h:5938</c>).
    /// Incremented when this player's ship rescues a friendly lifepod
    /// (<c>FedSrv.CPP:13039</c>, <c>RescueShipEvent</c>) — worth
    /// <c>c_fcidPointsRescues</c> (5 on PCore15).</summary>
    public int Rescues { get; set; }

    /// <summary>P752 — the player's kill-<b>assist</b> count (native
    /// <c>PlayerScoreObject::GetAssists()</c>/<c>AddAssist()</c>,
    /// <c>m_cAssists</c>, <c>igc.h:5822-5830</c>). Credited to the first enemy,
    /// non-allied, player-piloted ship on the victim's damage track AFTER the
    /// top (kill-credit) bucket — native
    /// <c>FedSrvSiteBase::KillShipEvent</c>'s assist walk
    /// (<c>FedSrv.CPP:11637-11670</c>) — and carried to the game-over table
    /// via <c>GameOverScoreObject::GetRecentAssists</c>
    /// (<c>igc.h:6102-6105</c>, <c>gameoverscreen.cpp:160-164</c>).</summary>
    public int Assists { get; set; }

    /// <summary>P752 — increment <see cref="Assists"/> — the port of native
    /// <c>PlayerScoreObject::AddAssist()</c> (<c>m_cAssists++</c>,
    /// <c>igc.h:5826-5829</c>).</summary>
    public void AddAssist() => Assists++;

    /// <summary>The player's tech-recovery count (native
    /// <c>GetTechsRecovered()</c>/<c>AddTechsRecovered()</c>,
    /// <c>m_cTechsRecovered</c>, <c>igc.h:5934</c>). Incremented when docking
    /// applies newly developed techs (<c>FedSrv.CPP:13224-13226</c>).</summary>
    public int TechsRecovered { get; set; }

    /// <summary>The player's secured-artifact count (native <c>GetArtifacts()</c>/
    /// <c>AddArtifact()</c>, <c>m_cArtifacts</c>, <c>igc.h:5936</c>).
    /// Incremented when securing the team-lobby artifact
    /// (<c>FedSrv.CPP:13116-13118</c>).</summary>
    public int Artifacts { get; set; }

    /// <summary>The player's secured-flag count (native <c>GetFlags()</c>/
    /// <c>AddFlag()</c>, <c>m_cFlags</c>, <c>igc.h:5935</c>). Incremented when
    /// securing an enemy flag (<c>FedSrv.CPP:13125-13127</c>).</summary>
    public int Flags { get; set; }

    /// <summary>The player's warp-spot count (native <c>GetWarpsSpotted()</c>/
    /// <c>SpotWarp()</c>, <c>m_cWarpsSpotted</c>, <c>igc.h:5922</c>).
    /// Incremented when first spotting a warp (<c>FedSrv.CPP:11103-11104</c>).</summary>
    public int WarpsSpotted { get; set; }

    /// <summary>The player's special-asteroid-spot count (native
    /// <c>GetAsteroidsSpotted()</c>/<c>SpotSpecialAsteroid()</c>,
    /// <c>m_cAsteroidsSpotted</c>, <c>igc.h:5923</c>). Incremented when first
    /// spotting a special asteroid (<c>FedSrv.CPP:11108</c>).</summary>
    public int AsteroidsSpotted { get; set; }

    /// <summary>The player's high-value-target-spot count (native
    /// <c>GetTargetsSpotted()</c>/<c>AddTargetSpot()</c>,
    /// <c>m_cHighValueTargetsSpotted</c>, <c>igc.h:5931</c>). Incremented when
    /// first spotting a high-value enemy (miner/builder/capital/carrier/etc.,
    /// <c>FedSrv.CPP:10956-10982</c>). Worth <c>min(count,5)·4</c> points.</summary>
    public int HighValueTargetsSpotted { get; set; }

    /// <summary>Kills of mining hulls (native <c>m_cMinerKills</c>,
    /// <c>igc.h:5924</c>, a float so partial credits sum).</summary>
    public float MinerKills { get; set; }
    /// <summary>Kills of builder hulls (native <c>m_cBuilderKills</c>,
    /// <c>igc.h:5925</c>).</summary>
    public float BuilderKills { get; set; }
    /// <summary>Kills of layer hulls (native <c>m_cLayerKills</c>,
    /// <c>igc.h:5926</c>).</summary>
    public float LayerKills { get; set; }
    /// <summary>Kills of carrier hulls (native <c>m_cCarrierKills</c>,
    /// <c>igc.h:5927</c>).</summary>
    public float CarrierKills { get; set; }
    /// <summary>Kills of ordinary (player) hulls (native <c>m_cPlayerKills</c>,
    /// <c>igc.h:5928</c>).</summary>
    public float PlayerKills { get; set; }

    /// <summary>Base kills (native <c>GetBaseKills()</c>/<c>KillBase()</c>,
    /// <c>m_cBaseKills</c>, <c>igc.h:5929</c>). A pilot destroys a base with
    /// <see cref="KillBase"/> +1.0; a turret-gunner child gets +0.5
    /// (<c>FedSrv.CPP:12252-12265</c>).</summary>
    public float BaseKills { get; set; }
    /// <summary>Base captures (native <c>GetBaseCaptures()</c>/<c>CaptureBase()</c>,
    /// <c>m_cBaseCaptures</c>, <c>igc.h:5930</c>). A pilot captures a base with
    /// <see cref="CaptureBase"/> +1.0; a child gets +0.5
    /// (<c>fsship.cpp:600-613</c>).</summary>
    public float BaseCaptures { get; set; }

    /// <summary>The player's accumulated repair contribution (native
    /// <c>GetRepair()</c>/<c>SetRepair()</c>, <c>m_cRepair</c>, <c>igc.h:5932</c>,
    /// a float). Copied from the ship's own <c>Repair</c> accumulator (P299,
    /// <c>Ship.cs</c>) — worth <c>·5</c> points.</summary>
    public float Repair { get; set; }

    /// <summary>Time played (native <c>GetTimePlayed()</c>, <c>m_dtPlayed</c>,
    /// <c>igc.h:5947</c>, a float of seconds). Accumulated across
    /// <c>Connect</c>/<c>Disconnect</c>; the <c>kMax</c> diminishing-returns
    /// divisor in <see cref="CalculateScore"/> is <c>m_dtPlayed / (15 min)</c>.</summary>
    public float TimePlayed { get; set; }

    /// <summary>The rank ratio applied to the score (native <c>m_rankRatio</c>,
    /// <c>igc.h:5953</c>). Defaults to <c>1.0f</c> (<c>igc.h:5571</c>); set at
    /// game over to the opposing side's rank sum divided by this side's
    /// (<c>fsmission.cpp:3196-3207</c>). Multiplies the final score.</summary>
    public float RankRatio { get; set; } = 1.0f;

    /// <summary>Whether this player's side won the game (native <c>m_bWin</c>,
    /// <c>igc.h:5959</c>). When true, <see cref="CalculateScore"/> doubles the
    /// score (<c>common.cpp:2691-2692</c>).</summary>
    public bool Win { get; set; }

    /// <summary>The player's Elo-style combat rating (native
    /// <c>GetCombatRating()</c>/<c>SetCombatRating()</c>,
    /// <c>m_fCombatRating</c>). Defaults to the native neutral seed
    /// <c>0.0f</c> (<c>igc.h:5510</c>). Updated on every real two-player kill
    /// via <see cref="AdjustCombatRating"/>.</summary>
    public float CombatRating { get; set; }

    /// <summary>Increment <see cref="Kills"/> — the port of native
    /// <c>PlayerScoreObject::AddKill()</c> (<c>m_cKills++</c>, <c>igc.h:5819</c>).</summary>
    public void AddKill() => Kills++;

    /// <summary>Increment <see cref="Deaths"/> — the port of native
    /// <c>PlayerScoreObject::AddDeath()</c> (<c>m_cDeaths++</c>, <c>igc.h:5837</c>).</summary>
    public void AddDeath() => Deaths++;

    /// <summary>Increment <see cref="Ejections"/> — the port of native
    /// <c>PlayerScoreObject::AddEjection()</c> (<c>m_cEjections++</c>,
    /// <c>igc.h:5849</c>).</summary>
    public void AddEjection() => Ejections++;

    /// <summary>Increment <see cref="Rescues"/> — the port of native
    /// <c>PlayerScoreObject::AddRescue()</c> (<c>m_cRescues++</c>, <c>igc.h:5805</c>).</summary>
    public void AddRescue() => Rescues++;

    /// <summary>Increment <see cref="TechsRecovered"/> — the port of native
    /// <c>PlayerScoreObject::AddTechsRecovered()</c> (<c>m_cTechsRecovered++</c>,
    /// <c>igc.h:5780</c>).</summary>
    public void AddTechsRecovered() => TechsRecovered++;

    /// <summary>Increment <see cref="Artifacts"/> — the port of native
    /// <c>PlayerScoreObject::AddArtifact()</c> (<c>m_cArtifacts++</c>, <c>igc.h:5800</c>).</summary>
    public void AddArtifact() => Artifacts++;

    /// <summary>Increment <see cref="Flags"/> — the port of native
    /// <c>PlayerScoreObject::AddFlag()</c> (<c>m_cFlags++</c>, <c>igc.h:5790</c>).</summary>
    public void AddFlag() => Flags++;

    /// <summary>Increment <see cref="WarpsSpotted"/> — the port of native
    /// <c>PlayerScoreObject::SpotWarp()</c> (<c>m_cWarpsSpotted++</c>, <c>igc.h:5648</c>).</summary>
    public void SpotWarp() => WarpsSpotted++;

    /// <summary>Increment <see cref="AsteroidsSpotted"/> — the port of native
    /// <c>PlayerScoreObject::SpotSpecialAsteroid()</c> (<c>m_cAsteroidsSpotted++</c>,
    /// <c>igc.h:5653</c>).</summary>
    public void SpotSpecialAsteroid() => AsteroidsSpotted++;

    /// <summary>Increment <see cref="HighValueTargetsSpotted"/> — the port of
    /// native <c>PlayerScoreObject::AddTargetSpot()</c>
    /// (<c>m_cHighValueTargetsSpotted++</c>, <c>igc.h:5658</c>).</summary>
    public void AddTargetSpot() => HighValueTargetsSpotted++;

    /// <summary>Set <see cref="Repair"/> — the port of native
    /// <c>PlayerScoreObject::SetRepair(float)</c> (<c>igc.h:5664-5667</c>).
    /// Native copies the ship's own repair accumulator into the score object
    /// periodically (<c>FedSrv.CPP:3704-3709</c>).</summary>
    public void SetRepair(float repair) => Repair = repair;

    /// <summary>Record a kill of a <paramref name="victimClass"/> ship,
    /// adding <paramref name="fraction"/> (usually 1.0 for the credited killer;
    /// native passes each damage-contributor's share, <c>FedSrv.CPP:11848</c>)
    /// to that class's counter. The port of native
    /// <c>PlayerScoreObject::KillShip(pship, fraction)</c> (<c>igc.h:5675-5706</c>),
    /// whose switch on <c>pship-&gt;GetPilotType()</c> routes miner/builder/
    /// layer/carrier kills to their own counters and everything else to
    /// <c>m_cPlayerKills</c>.</summary>
    public void KillShip(VictimClass victimClass, float fraction)
    {
        switch (victimClass)
        {
            case VictimClass.Miner: MinerKills += fraction; break;
            case VictimClass.Builder: BuilderKills += fraction; break;
            case VictimClass.Layer: LayerKills += fraction; break;
            case VictimClass.Carrier: CarrierKills += fraction; break;
            default: PlayerKills += fraction; break;
        }
    }

    /// <summary>Record a base kill — the port of native
    /// <c>PlayerScoreObject::KillBase(bPilot)</c> (<c>igc.h:5708-5717</c>): a
    /// pilot's own base kill is +1.0 (<c>m_cPilotBaseKills</c>), a turret-
    /// gunner child's is +0.5.</summary>
    public void KillBase(bool bPilot) => BaseKills += bPilot ? 1.0f : 0.5f;

    /// <summary>Record a base capture — the port of native
    /// <c>PlayerScoreObject::CaptureBase(bPilot)</c> (<c>igc.h:5718-5727</c>):
    /// a pilot's own capture is +1.0, a child's is +0.5.</summary>
    public void CaptureBase(bool bPilot) => BaseCaptures += bPilot ? 1.0f : 0.5f;

    /// <summary>Set <see cref="RankRatio"/> — the port of native
    /// <c>PlayerScoreObject::SetRankRatio(float)</c> (<c>igc.h:5576-5579</c>).</summary>
    public void SetRankRatio(float rankRatio) => RankRatio = rankRatio;

    /// <summary>Set <see cref="TimePlayed"/> — the port of the accumulated
    /// <c>Connect</c>/<c>Disconnect</c> accounting (<c>igc.h:5602-5613</c>)
    /// collapsed to a single value for this port's connection-lifetime model
    /// (a peer plays continuously from logon to release).</summary>
    public void SetTimePlayed(float seconds) => TimePlayed = seconds;

    /// <summary>Set <see cref="Win"/> — the win flag
    /// <see cref="CalculateScore"/> multiplies by 2.</summary>
    public void SetWin(bool win) => Win = win;

    /// <summary>
    /// P802 — the native <c>SetSideCiv</c> per-ship score reset
    /// (<c>FedSrv/fsmission.cpp:5274-5283</c>):
    /// <c>ppso-&gt;SetPersist(pfsship-&gt;GetPlayer()-&gt;GetPersistPlayerScore(civID))</c>.
    /// A civ change makes the player's persistent score <em>for that
    /// civilization</em> their live score. This port has no persistent per-civ
    /// score store (P193/P583: connection-lifetime only, no account layer), so
    /// that persistent object is the native default-constructed
    /// <c>PlayerScoreObject</c> — the neutral zero score — and the copy
    /// collapses to resetting every live <em>session</em> counter. Rank and
    /// combat rating are kept: native takes those from the per-civ persistent
    /// record the port does not have, and rank is the connection's only
    /// identity value. In a pre-start lobby the counters are already zero, so
    /// this is the native ordering rather than a visible lobby change.
    /// </summary>
    /// <param name="civId">The civilization the side is moving to (the native
    /// persist-score lookup key; retained in the log for traceability).</param>
    public void ResetToPersistScoreForCiv(short civId)
    {
        Score = 0f;
        Kills = 0; Deaths = 0; Ejections = 0; Rescues = 0; Assists = 0;
        TechsRecovered = 0; Artifacts = 0; Flags = 0;
        WarpsSpotted = 0; AsteroidsSpotted = 0; HighValueTargetsSpotted = 0;
        MinerKills = 0f; BuilderKills = 0f; LayerKills = 0f; CarrierKills = 0f; PlayerKills = 0f;
        BaseKills = 0f; BaseCaptures = 0f; Repair = 0f; TimePlayed = 0f;
        RankRatio = 1.0f;
        Win = false;
        Console.WriteLine($"[changeciv] player score reset to the neutral persist score for civ {civId} (native SetSideCiv)");
    }

    /// <summary>
    /// P583 — port of native <c>PlayerScoreObject::CalculateScore</c>
    /// (<c>src/Igc/common.cpp:2667-2695</c>), the full scoring formula
    /// reconstructed 1:1 from the counters above. The point values are read
    /// from the loaded core's <c>FloatConstants</c> array (<paramref name="fc"/>)
    /// at the indices in this class's <c>*Index</c> constants — the same
    /// caller-supplied seam <see cref="AdjustCombatRating"/> uses, so the math
    /// stays pure and unit-testable against hand-computed native values.
    /// </summary>
    /// <remarks>
    /// <para>The native formula (<c>common.cpp:2672-2694</c>):
    /// <c>kMax = m_dtPlayed / (15·60)</c>; then the linear terms
    /// (warps·PointsWarp, asteroids·PointsAsteroid, techs·PointsTech,
    /// playerKills·PointsPlayer, rescues·PointsRescues, artifacts·
    /// PointsArtifacts, flags·PointsFlags, min(hvt,5)·4, repair·5) plus the six
    /// diminishing-returns terms <c>(c·kMax)·Points/(c+kMax)</c> for miner/
    /// builder/layer/carrier kills and baseKills/baseCaptures. Then
    /// <c>×2 if m_bWin</c> and <c>× m_rankRatio</c>.</para>
    /// <para>Early return on <c>m_dtPlayed == 0</c> (<c>common.cpp:2669-2670</c>)
    /// is preserved — the score is left untouched.</para>
    /// </remarks>
    /// <param name="fc">The mission core's <c>FloatConstants</c> array (must be
    /// long enough to cover index 29).</param>
    public void CalculateScore(float[] fc)
    {
        if (TimePlayed == 0.0f)
            return;

        float kMax = TimePlayed / (15.0f * 60.0f);

        Score =
            WarpsSpotted * fc[PointsWarpIndex] +
            AsteroidsSpotted * fc[PointsAsteroidIndex] +
            TechsRecovered * fc[PointsTechIndex] +
            (MinerKills * kMax) * fc[PointsMinerIndex] / (MinerKills + kMax) +
            (BuilderKills * kMax) * fc[PointsBuilderIndex] / (BuilderKills + kMax) +
            (LayerKills * kMax) * fc[PointsLayerIndex] / (LayerKills + kMax) +
            (CarrierKills * kMax) * fc[PointsCarrierIndex] / (CarrierKills + kMax) +
            PlayerKills * fc[PointsPlayerIndex] +
            (BaseKills * kMax) * fc[PointsBaseKillIndex] / (BaseKills + kMax) +
            (BaseCaptures * kMax) * fc[PointsBaseCaptureIndex] / (BaseCaptures + kMax) +
            Rescues * fc[PointsRescuesIndex] +
            Artifacts * fc[PointsArtifactsIndex] +
            Flags * fc[PointsFlagsIndex] +
            (HighValueTargetsSpotted <= 5 ? HighValueTargetsSpotted : 5) * 4 +
            Repair * 5;

        if (Win)
            Score *= 2.0f;

        Score *= RankRatio;
    }

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
    /// <c>igc.h:92-93</c>), read from the loaded core by
    /// <c>Server.BroadcastKillShip</c> and passed in here — keeping this
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
