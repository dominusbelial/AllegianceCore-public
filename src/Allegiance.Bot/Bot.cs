using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Bot;

/// <summary>
/// A bot — a <see cref="TcpTransport"/> client that speaks the real ported
/// messages. Connects, logs on, waits for its <c>SET_CLUSTER(7)</c> ship
/// assignment, sends <c>SHIP_UPDATE(24)</c> control input, collects
/// <c>SINGLE_SHIP_UPDATE(78)</c> observations of its own ship's position, and
/// records every <c>KILL_SHIP(105)</c> it observes on the wire.</summary>
/// <remarks>
/// <para>Designed to be driveable two ways: interactively by the console
/// <c>Program.Main</c> (step 4), and synchronously by the in-process tests
/// (verification). It implements <see cref="ITransportSite"/> so it can sit
/// directly on a client <see cref="TcpTransport"/> and route inbound datagrams
/// to decoded messages; the read happens on the transport thread, guarded by a
/// lock so a caller on another thread can query observations safely.</para>
/// <para>Nothing here assumes wall-clock cadence — a caller (console or test)
/// decides how long to observe. The transports are loopback-real: the messages
/// are genuinely encoded, framed, CRC'd and decoded over a TCP socket.</para>
/// </remarks>
public sealed class Bot : ITransportSite, IDisposable
{
    private TcpTransport? _transport;
    private readonly object _gate = new();

    private LogonAck? _ack;
    private SetCluster? _setCluster;
    private readonly List<SetCluster> _assignments = new();
    private readonly List<ShipObservation> _observations = new();
    private readonly List<ShipObservation> _anyShipObservations = new();
    private readonly List<KillShip> _kills = new();
    private readonly List<ChatMessage> _chats = new();
    private readonly List<ChatBuoy> _chatBuoys = new();
    private readonly List<HighlightCluster> _highlights = new();
    private readonly List<SwapPart> _swapParts = new();   // P143
    private readonly List<DropPart> _dropParts = new();   // P144
    private readonly List<Reload> _reloads = new();       // P192
    private readonly List<AcquireTreasure> _acquireTreasures = new();   // P145 (per-connection part offer)
    private readonly List<AddPart> _addParts = new();                  // P145 (sector-wide part add)
    private readonly List<SetWingId> _setWingIds = new();              // P146 (side-wide wing-group tag)
    private readonly List<OrderChange> _orderChanges = new();         // P148 (ORDER_CHANGE 93, stay-docked)
    private readonly List<TreasureSpawn> _treasureSpawns = new();   // P144 (first real FM_S_TREASURE send)
    private readonly List<ShipStatusMessage> _shipStatuses = new();
    private readonly List<ClusterInfo> _clusterInfos = new();
    private readonly List<Payday> _paydays = new();
    private readonly List<DockedNotify> _dockedNotices = new();
    private readonly List<TeleportAck> _teleportAcks = new();
    private readonly List<PlayerReady> _playerReadyEchoes = new();
    private readonly List<TeamReady> _teamReadys = new();
    private readonly List<ExportShip> _exports = new();
    private readonly List<ExportAsteroid> _asteroidExports = new();
    private readonly List<PlayerInfo> _playerInfos = new();
    private readonly List<ProjectileInstance> _projectileInstances = new();
    private readonly List<FireMissile> _fireMissiles = new();
    private readonly List<MissileDestroyed> _missileDestroyeds = new();
    private readonly List<Allegiance.Core.Wire.LoadoutChange> _loadoutChanges = new();
    private readonly List<ExportMine> _mineExports = new();
    private readonly List<MineDestroyed> _mineDestroyeds = new();
    private readonly List<ExportProbe> _probeExports = new();
    private readonly List<ProbeDestroyed> _probeDestroyeds = new();
    private readonly List<WarpBomb> _warpBombs = new();
    private readonly List<FireExpendable> _fireExpendables = new();
    private readonly List<CreateChaff> _createChaffs = new();
    private readonly List<MissileSpoofed> _missileSpoofeds = new();
    private readonly List<EndSpoofing> _endSpoofings = new();
    private readonly List<AsteroidDestroyed> _asteroidDestroyeds = new();
    private readonly List<AsteroidMined> _asteroidMineds = new();
    private readonly List<StationDestroyed> _stationDestroyeds = new();
    private readonly List<StationCapture> _stationCaptures = new();
    private readonly List<GameState> _gameStates = new();
    private readonly List<GameOver> _gameOvers = new();
    private readonly List<GameOverPlayers> _gameOverPlayers = new();
    private readonly List<StationsUpdate> _stationsUpdates = new();   // P133
    private readonly List<AsteroidsUpdate> _asteroidsUpdates = new();  // P134
    private readonly List<ProbesUpdate> _probesUpdates = new();        // P135
    private readonly List<HeavyShipsUpdate> _heavyShipsUpdates = new(); // P139
    private readonly List<LightShipsUpdate> _lightShipsUpdates = new(); // P139
    private readonly List<RipcordActivate> _ripcordActivates = new();
    private readonly List<RipcordDenied> _ripcordDenieds = new();
    private readonly List<RipcordAborted> _ripcordAborteds = new();
    private readonly List<PlayerRescued> _playerRescueds = new();
    private readonly List<RelaunchShip> _relaunchShips = new();
    private readonly List<MoneyChange> _moneyChanges = new();
    private readonly List<SetMoney> _setMoneys = new();
    private readonly List<AutoDonateUpdate> _autoDonateUpdates = new();
    private readonly List<BucketStatus> _bucketStatuses = new();
    private readonly List<CreateBuckets> _createBucketsRx = new();
    private readonly List<DevCompleted> _devCompleteds = new();       // P189
    private readonly List<SideTechChange> _sideTechChanges = new();   // P189
    private readonly List<SideAttributeChange> _sideAttributeChanges = new(); // P189
    private readonly List<SetBriefingText> _setBriefingTexts = new();
    private readonly List<RankInfo> _rankInfos = new();   // P130
    private readonly List<EnterGame> _enterGames = new(); // P141
    private readonly List<TreasureSets> _treasureSetsRx = new();
    private readonly List<DestroyTreasure> _destroyTreasures = new();
    private readonly List<BuildingEffectDestroyed> _buildingEffectDestroyed = new();
    private readonly List<JoinSide> _joinSides = new();                  // P183
    private readonly List<SetTeamLeader> _setTeamLeaders = new();        // P183
    private readonly List<SetMissionOwner> _setMissionOwners = new();    // P183
    private readonly List<QuitMission> _quitMissions = new();            // P183
    private readonly List<QuitSide> _quitSides = new();                  // P183
    private readonly List<PositionReqBroadcast> _positionReqs = new();   // P184
    private readonly List<DelPositionReq> _delPositionReqs = new();      // P184
    private readonly List<ForceTeamReady> _forceTeamReadys = new();      // P184
    private readonly List<LockLobby> _lockLobbys = new();                // P186
    private readonly List<LockSides> _lockSidess = new();                // P186
    private readonly List<AutoAccept> _autoAccepts = new();              // P186
    private readonly List<ChangeTeamCiv> _changeTeamCivs = new();        // P186
    private readonly List<ChangeAlliances> _allianceBroadcasts = new();     // P190
    private readonly List<Ballot> _ballots = new();                      // P187
    private readonly List<CancelBallot> _cancelBallots = new();          // P187
    private readonly List<SetStartTime> _setStartTimes = new();          // P188
    private readonly List<Ping> _pings = new();                          // P188
    private short _myShipId = -1;

    /// <summary>A SINGLE_SHIP_UPDATE observation of a ship, timestamped with the
    /// server tick index (the uint <c>Time</c> slot of the payload), plus the
    /// ship's carried ore fraction (<c>ServerShipUpdate.OreFraction</c>, byte 45
    /// of the 47-byte body) so a test can assert ore travelled wire-visible.</summary>
    public readonly record struct ShipObservation(short ShipId, uint Tick, Vector3 Position, Vector3 Velocity, Orientation Orientation, float OreFraction = 0.0f);

    public bool Connected => _transport?.IsConnected ?? false;

    public LogonAck? Ack { get { lock (_gate) return _ack; } }

    public SetCluster? ShipAssignment { get { lock (_gate) return _setCluster; } }

    /// <summary>Every SET_CLUSTER(7) assignment received, in arrival order —
    /// the join-burst assignment plus one per warp transit (each transit makes
    /// the server re-send SET_CLUSTER with the new sector). Mirrors
    /// <see cref="ShipStatuses"/>: locked, snapshot-on-read.</summary>
    public IReadOnlyList<SetCluster> Assignments { get { lock (_gate) return _assignments.ToArray(); } }

    /// <summary>All SINGLE_SHIP_UPDATE observations received so far, in arrival order.</summary>
    public IReadOnlyList<ShipObservation> Observations { get { lock (_gate) return _observations.ToArray(); } }

    /// <summary>Every SINGLE_SHIP_UPDATE observation received for ANY ship id, not
    /// just this bot's own (unlike <see cref="Observations"/>, which is filtered to
    /// <c>OidTarget == _myShipId</c>) — the seam for asserting a teammate's live
    /// position/velocity is actually broadcast (P85), not just its existence/status.
    /// Mirrors <see cref="Observations"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetObservations"/> (both lists are reset together).</summary>
    public IReadOnlyList<ShipObservation> AnyShipObservations { get { lock (_gate) return _anyShipObservations.ToArray(); } }

    /// <summary>Clear the accumulated observations (between manoeuvres).</summary>
    public void ResetObservations() { lock (_gate) { _observations.Clear(); _anyShipObservations.Clear(); } }

    /// <summary>All KILL_SHIP(105) messages received so far, in arrival order — the
    /// deaths this bot has observed on the wire. Mirrors <see cref="Observations"/>:
    /// locked, snapshot-on-read, cleared via <see cref="ResetKills"/>.</summary>
    public IReadOnlyList<KillShip> Kills { get { lock (_gate) return _kills.ToArray(); } }

    /// <summary>Clear the accumulated kills (between engagements).</summary>
    public void ResetKills() { lock (_gate) _kills.Clear(); }

    /// <summary>All CHATMESSAGE(23) messages this bot has received, in arrival
    /// order. Mirrors <see cref="Kills"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetChatReceived"/>.</summary>
    public IReadOnlyList<ChatMessage> Chats { get { lock (_gate) return _chats.ToArray(); } }

    /// <summary>Clear the accumulated chats (between phases of a chat exchange).</summary>
    public void ResetChatReceived() { lock (_gate) _chats.Clear(); }

    /// <summary>All CHATBUOY(108) messages this bot has received, in arrival
    /// order. Mirrors <see cref="Chats"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetChatBuoyReceived"/>.</summary>
    public IReadOnlyList<ChatBuoy> ChatBuoys { get { lock (_gate) return _chatBuoys.ToArray(); } }

    /// <summary>Clear the accumulated CHATBUOY messages (between exchange phases).</summary>
    public void ResetChatBuoyReceived() { lock (_gate) _chatBuoys.Clear(); }

    /// <summary>All HIGHLIGHT_CLUSTER(199) messages this bot has received, in
    /// arrival order — the sector highlights the server relays to this bot's
    /// side. Mirrors <see cref="ChatBuoys"/>: locked, snapshot-on-read, cleared
    /// via <see cref="ResetHighlightsReceived"/>.</summary>
    public IReadOnlyList<HighlightCluster> Highlights { get { lock (_gate) return _highlights.ToArray(); } }

    /// <summary>Clear the accumulated HIGHLIGHT_CLUSTER messages (between relay phases).</summary>
    public void ResetHighlightsReceived() { lock (_gate) _highlights.Clear(); }

    /// <summary>All SWAP_PART(34) messages this bot has received, in arrival
    /// order — the sector-wide part-swap broadcasts the server relays to every
    /// ship flying in the same cluster as the requesting ship (P143). Mirrors
    /// <see cref="Highlights"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetSwapPartsReceived"/>.</summary>
    public IReadOnlyList<SwapPart> SwapParts { get { lock (_gate) return _swapParts.ToArray(); } }

    /// <summary>Clear the accumulated SWAP_PART messages (between swap phases).</summary>
    public void ResetSwapPartsReceived() { lock (_gate) _swapParts.Clear(); }

    /// <summary>The <c>FM_CS_DROP_PART(33)</c> sector-wide echoes this bot has
    /// received (P144) — a <see cref="DropPart"/> per received message, appended
    /// on the receiver thread under <see cref="_gate"/> like
    /// <see cref="SwapParts"/>. Each carries the resolved <c>shipID</c>/<c>et</c>/
    /// <c>mount</c> the server filled in. Cleared by
    /// <see cref="ResetDropPartsReceived"/>.</summary>
    public IReadOnlyList<DropPart> DropParts { get { lock (_gate) return _dropParts.ToArray(); } }
    public void ResetDropPartsReceived() { lock (_gate) _dropParts.Clear(); }

    /// <summary>The <c>FM_CS_RELOAD(129)</c> broadcasts this bot has seen (P192) —
    /// a <see cref="Reload"/> per received sector-wide relay of a cargo reload,
    /// appended on the receiver thread under <see cref="_gate"/> like
    /// <see cref="DropParts"/>. Each carries the resolved <c>shipID</c> and the
    /// original <c>ReloadData</c> entries. Cleared by
    /// <see cref="ResetReloadsReceived"/>.</summary>
    public IReadOnlyList<Reload> ReloadBroadcasts { get { lock (_gate) return _reloads.ToArray(); } }
    public void ResetReloadsReceived() { lock (_gate) _reloads.Clear(); }

    /// <summary>The <c>FM_S_ACQUIRE_TREASURE(162)</c> notices this bot has
    /// received (P145) — the server's per-connection part-treasure offer, sent
    /// to the picking player's <em>own</em> connection only after a successful
    /// pickup (native <c>SendMessages(pfsPlayer-&gt;GetConnection(), ...)</c>,
    /// FedSrv.CPP:13639-13641). A <see cref="AcquireTreasure"/> per received
    /// message. Cleared by <see cref="ResetAcquireTreasures"/>.</summary>
    public IReadOnlyList<AcquireTreasure> AcquireTreasures { get { lock (_gate) return _acquireTreasures.ToArray(); } }
    public void ResetAcquireTreasures() { lock (_gate) _acquireTreasures.Clear(); }

    /// <summary>The <c>FM_S_ADD_PART(164)</c> sector-wide broadcasts this bot
    /// has received (P145) — the server telling every ship in the cluster that a
    /// part was added to a ship's loadout (the TREASURE_ACK accept outcome,
    /// native <c>GetGroupSectorFlying(pcluster)</c>, FedSrv.CPP:6108-6119). A
    /// <see cref="AddPart"/> per received message. Cleared by
    /// <see cref="ResetAddParts"/>.</summary>
    public IReadOnlyList<AddPart> AddParts { get { lock (_gate) return _addParts.ToArray(); } }
    public void ResetAddParts() { lock (_gate) _addParts.Clear(); }

    /// <summary>The <c>FM_CS_SET_WINGID(106)</c> side-wide broadcasts this bot
    /// has received (P146) — the server relaying a wing-group assignment to
    /// every ship seated on the tagging player's side (native
    /// <c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>, FedSrv.CPP:5337 — a
    /// side-wide, not sector-wide, audience). A <see cref="SetWingId"/> per
    /// received message, appended on the receiver thread under <see cref="_gate"/>
    /// like <see cref="SwapParts"/>, carrying the server-corrected
    /// <c>shipID</c>/<c>wingID</c>/<c>bCommanded=false</c>. Cleared by
    /// <see cref="ResetSetWingIdsReceived"/>.</summary>
    public IReadOnlyList<SetWingId> SetWingIds { get { lock (_gate) return _setWingIds.ToArray(); } }
    public void ResetSetWingIdsReceived() { lock (_gate) _setWingIds.Clear(); }

    /// <summary>The <c>FM_CS_ORDER_CHANGE(93)</c> messages this bot has received
    /// (P148) — a <see cref="OrderChange"/> per received message, appended on the
    /// receiver thread under <see cref="_gate"/> like <see cref="SwapParts"/>.
    /// The P148 stay-docked slice does not forward ORDER_CHANGE to peers (native
    /// sends only a chat ack, FedSrv.CPP:7293-7294), so a bot normally <em>sends</em>
    /// these; the inbound list exists for symmetry with the other CS relays and
    /// in case a later stage forwards the message. Cleared by
    /// <see cref="ResetOrderChangesReceived"/>.</summary>
    public IReadOnlyList<OrderChange> OrderChanges { get { lock (_gate) return _orderChanges.ToArray(); } }
    public void ResetOrderChangesReceived() { lock (_gate) _orderChanges.Clear(); }

    /// <summary>The <c>FM_S_TREASURE(28)</c> notices this bot has received
    /// (P144) — a <see cref="TreasureSpawn"/> per received message, appended on
    /// the receiver thread under <see cref="_gate"/> like
    /// <see cref="SwapParts"/>. Each carries the full
    /// <see cref="DataTreasureIGC"/> the server built for a dropped part.
    /// Cleared by <see cref="ResetTreasureSpawnsReceived"/>.</summary>
    public IReadOnlyList<TreasureSpawn> TreasureSpawns { get { lock (_gate) return _treasureSpawns.ToArray(); } }
    public void ResetTreasureSpawnsReceived() { lock (_gate) _treasureSpawns.Clear(); }

    /// <summary>All SHIP_STATUS(90) messages received so far, in arrival order —
    /// the per-ship status frames the server sends about this bot's own ship.
    /// Mirrors <see cref="Kills"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetShipStatuses"/>.</summary>
    public IReadOnlyList<ShipStatusMessage> ShipStatuses { get { lock (_gate) return _shipStatuses.ToArray(); } }

    /// <summary>Clear the accumulated ship statuses (between joins/phases).</summary>
    public void ResetShipStatuses() { lock (_gate) _shipStatuses.Clear(); }

    /// <summary>Every CLUSTERINFO(200) received so far, in arrival order — the
    /// map-overview message the server sends at the start of the join burst so
    /// the native client's <c>GameInfo.Clusters</c> population can engage the
    /// smart exploration branches (<c>ScoutExploreMap</c>). Mirrors
    /// <see cref="ShipStatuses"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetClusterInfos"/>.</summary>
    public IReadOnlyList<ClusterInfo> ClusterInfos { get { lock (_gate) return _clusterInfos.ToArray(); } }

    /// <summary>Clear the accumulated cluster infos (between joins/phases).</summary>
    public void ResetClusterInfos() { lock (_gate) _clusterInfos.Clear(); }

    /// <summary>Every FM_S_PAYDAY(114) received so far, in arrival order — the
    /// per-side payday payouts this bot has observed on the wire. Mirrors
    /// <see cref="Kills"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetPaydays"/>.</summary>
    public IReadOnlyList<Payday> Paydays { get { lock (_gate) return _paydays.ToArray(); } }

    /// <summary>Clear the accumulated paydays (between phases of a payday run).</summary>
    public void ResetPaydays() { lock (_gate) _paydays.Clear(); }

    /// <summary>Every <c>FM_S_DOCKED(61)</c> received so far, in arrival order —
    /// the server's "you are now docked at station X" notify, sent once per dock
    /// transition to the docking peer (the trigger that makes the native
    /// client's <c>ship.GetStation()</c> resolve). Mirrors <see cref="Kills"/>:
    /// locked, snapshot-on-read, cleared via <see cref="ResetDockedNotices"/>.</summary>
    public IReadOnlyList<DockedNotify> DockedNotices { get { lock (_gate) return _dockedNotices.ToArray(); } }

    /// <summary>Clear the accumulated docked notifies (between joins/phases).</summary>
    public void ResetDockedNotices() { lock (_gate) _dockedNotices.Clear(); }

    /// <summary>Every <c>FM_S_TELEPORT_ACK(102)</c> received so far, in arrival
    /// order — the server's answer to a <c>FM_C_DOCKED(48)</c> with a non-NA
    /// <c>stationID</c> (P110). <c>StationId = NA</c> means the teleport request
    /// was rejected. Mirrors <see cref="DockedNotices"/>: locked,
    /// snapshot-on-read, cleared via <see cref="ResetTeleportAcks"/>.</summary>
    public IReadOnlyList<TeleportAck> TeleportAcks { get { lock (_gate) return _teleportAcks.ToArray(); } }

    /// <summary>Clear the accumulated teleport acks (between join/phases).</summary>
    public void ResetTeleportAcks() { lock (_gate) _teleportAcks.Clear(); }

    /// <summary>All <c>FM_CS_PLAYER_READY(45)</c> relays received so far — the
    /// server's echo of this bot's (or a teammate's) ready-state announce,
    /// "Sent to everyone on team" per the message's own header comment. The
    /// real native client waits for exactly this relay before updating its
    /// own Team Lobby roster state at all (<see cref="SendPlayerReady"/>'s doc
    /// comment). Mirrors <see cref="DockedNotices"/>: locked, snapshot-on-read,
    /// cleared via <see cref="ResetPlayerReadyEchoes"/>.</summary>
    public IReadOnlyList<PlayerReady> PlayerReadyEchoes { get { lock (_gate) return _playerReadyEchoes.ToArray(); } }

    /// <summary>Clear the accumulated PLAYER_READY relays (between joins/phases).</summary>
    public void ResetPlayerReadyEchoes() { lock (_gate) _playerReadyEchoes.Clear(); }

    /// <summary>Captured <c>TEAM_READY(46)</c> broadcasts received from the
    /// server (P180): a whole-side aggregate-readiness change announced to the
    /// whole mission. Locked, snapshot-on-read, cleared via
    /// <see cref="ResetTeamReadys"/>.</summary>
    public IReadOnlyList<TeamReady> TeamReadys { get { lock (_gate) return _teamReadys.ToArray(); } }

    /// <summary>Clear the accumulated TEAM_READY broadcasts (between joins/phases).</summary>
    public void ResetTeamReadys() { lock (_gate) _teamReadys.Clear(); }

    /// <summary>Captured <c>JOIN_SIDE(125)</c> broadcasts received from the
    /// server (P183): a ship joined/switched to a side. Locked, snapshot-on-read,
    /// cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<JoinSide> JoinSides { get { lock (_gate) return _joinSides.ToArray(); } }

    /// <summary>Captured <c>SET_TEAM_LEADER(121)</c> broadcasts received from
    /// the server (P183): a ship became a side's team leader. Cleared via
    /// <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<SetTeamLeader> SetTeamLeaders { get { lock (_gate) return _setTeamLeaders.ToArray(); } }

    /// <summary>Captured <c>SET_MISSION_OWNER(123)</c> broadcasts received from
    /// the server (P183): a ship became the mission owner. Cleared via
    /// <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<SetMissionOwner> SetMissionOwners { get { lock (_gate) return _setMissionOwners.ToArray(); } }

    /// <summary>Captured <c>QUIT_MISSION(124)</c> broadcasts received from the
    /// server (P183): a ship left the mission entirely (e.g. disconnected).
    /// Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<QuitMission> QuitMissions { get { lock (_gate) return _quitMissions.ToArray(); } }

    /// <summary>Captured <c>QUIT_SIDE(126)</c> broadcasts received from the
    /// server (P183): a ship left a side while staying in the mission. Cleared
    /// via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<QuitSide> QuitSides { get { lock (_gate) return _quitSides.ToArray(); } }

    /// <summary>Clear all five P183 leadership/join/quit broadcast accumulators
    /// (between joins/phases).</summary>
    public void ResetLeadershipSignals()
    {
        lock (_gate)
        {
            _joinSides.Clear();
            _setTeamLeaders.Clear();
            _setMissionOwners.Clear();
            _quitMissions.Clear();
            _quitSides.Clear();
            _positionReqs.Clear();
            _delPositionReqs.Clear();
            _forceTeamReadys.Clear();
            _lockLobbys.Clear();
            _lockSidess.Clear();
            _autoAccepts.Clear();
            _changeTeamCivs.Clear();
            _allianceBroadcasts.Clear();
        }
    }

    /// <summary>Captured <c>POSITIONREQ(40)</c> broadcasts received from the
    /// server (P184): a pending side-join request announced whole-mission for
    /// leader approval. Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<PositionReqBroadcast> PositionReqs { get { lock (_gate) return _positionReqs.ToArray(); } }

    /// <summary>Captured <c>DELPOSITIONREQ(42)</c> messages received from the
    /// server (P184): a pending side-join request withdrawn/rejected/canceled.
    /// Includes both whole-mission broadcasts and the requester-only denial.
    /// Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<DelPositionReq> DelPositionReqs { get { lock (_gate) return _delPositionReqs.ToArray(); } }

    /// <summary>Captured <c>FORCE_TEAM_READY(47)</c> relays received from the
    /// server (P184): a leader forcing (or un-forcing) their side ready.
    /// Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<ForceTeamReady> ForceTeamReadys { get { lock (_gate) return _forceTeamReadys.ToArray(); } }

    /// <summary>Captured <c>LOCK_LOBBY(43)</c> relays received from the server
    /// (P186): the mission owner locking/unlocking the lobby, forwarded
    /// whole-mission. Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<LockLobby> LockLobbys { get { lock (_gate) return _lockLobbys.ToArray(); } }

    /// <summary>Captured <c>LOCK_SIDES(44)</c> relays received from the server
    /// (P186): the mission owner locking/unlocking side changes, forwarded
    /// whole-mission. Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<LockSides> LockSidess { get { lock (_gate) return _lockSidess.ToArray(); } }

    /// <summary>Captured <c>AUTO_ACCEPT(52)</c> relays received from the server
    /// (P186): a side's leader toggling auto-accept, forwarded whole-mission.
    /// Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<AutoAccept> AutoAccepts { get { lock (_gate) return _autoAccepts.ToArray(); } }

    /// <summary>Captured <c>CHANGE_TEAM_CIV(51)</c> relays received from the
    /// server (P186): a side's leader changing their side's civilization,
    /// forwarded whole-mission. Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<ChangeTeamCiv> ChangeTeamCivs { get { lock (_gate) return _changeTeamCivs.ToArray(); } }

    /// <summary>Captured <c>CHANGE_ALLIANCES(197)</c> broadcasts received from
    /// the server (P190): the whole mission's alliance-group array announced
    /// after any <c>CHANGE_ALLIANCE(196)</c>. Cleared via
    /// <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<ChangeAlliances> AllianceBroadcasts { get { lock (_gate) return _allianceBroadcasts.ToArray(); } }

    /// <summary>Captured <c>FM_S_BALLOT(17)</c> broadcasts received from the
    /// server (P187): a pending-vote announcement addressed to this bot (either
    /// its side for a player-initiated ballot, or the whole mission for an
    /// AcceptDraw). Cleared via <see cref="ResetBallotSignals"/>.</summary>
    public IReadOnlyList<Ballot> Ballots { get { lock (_gate) return _ballots.ToArray(); } }

    /// <summary>Captured <c>FM_S_CANCEL_BALLOT(18)</c> messages received from
    /// the server (P187). The native server never actually emits this message
    /// (see the CancelBallot codec / docs/BALLOT_SYSTEM_P187.md), so in practice
    /// this stays empty; it is accumulated for wire completeness only. Cleared
    /// via <see cref="ResetBallotSignals"/>.</summary>
    public IReadOnlyList<CancelBallot> CancelBallots { get { lock (_gate) return _cancelBallots.ToArray(); } }

    /// <summary>Clear the P187 ballot accumulators.</summary>
    public void ResetBallotSignals() { lock (_gate) { _ballots.Clear(); _cancelBallots.Clear(); } }

    /// <summary>Captured <c>SET_START_TIME(14)</c> broadcasts received from the
    /// server (P188): the mission's target start time announced at the start of
    /// the pre-launch countdown. Cleared via <see cref="ResetStartSignals"/>.</summary>
    public IReadOnlyList<SetStartTime> SetStartTimes { get { lock (_gate) return _setStartTimes.ToArray(); } }

    /// <summary>Captured <c>PING(35)</c> echoes received FROM the server (P188):
    /// the server stamps <c>TimeServer</c> and sends the client's ping back to
    /// the same connection. Cleared via <see cref="ResetStartSignals"/>.</summary>
    public IReadOnlyList<Ping> Pings { get { lock (_gate) return _pings.ToArray(); } }

    /// <summary>Clear the P188 start-countdown signal accumulators
    /// (<see cref="SetStartTimes"/>, <see cref="Pings"/>).</summary>
    public void ResetStartSignals() { lock (_gate) { _setStartTimes.Clear(); _pings.Clear(); } }

    /// <summary>All <c>FM_S_EXPORT(26)</c> ship exports received so far, in
    /// arrival order — including the one-shot teammate-ship exports (P62) that
    /// make the client's object model learn a same-side ship exists. P62:
    /// exactly the message the loopback tests assert on (real ShipId, real
    /// CharacterName, before any SHIP_STATUS, once per pair, same-side only).
    /// Mirrors <see cref="ShipStatuses"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetExports"/>.</summary>
    public IReadOnlyList<ExportShip> Exports { get { lock (_gate) return _exports.ToArray(); } }

    /// <summary>Clear the accumulated ship exports (between joins/phases).</summary>
    public void ResetExports() { lock (_gate) _exports.Clear(); }

    /// <summary>All <c>FM_S_EXPORT(26)</c> asteroid exports received so far, in
    /// arrival order — P64's per-join, one-time asteroid export that fills a
    /// client's <c>GetAsteroids()</c> lists with real He3-named rocks. Mirrors
    /// <see cref="Exports"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetAsteroidExports"/>.</summary>
    public IReadOnlyList<ExportAsteroid> AsteroidExports { get { lock (_gate) return _asteroidExports.ToArray(); } }

    /// <summary>Clear the accumulated asteroid exports (between joins/phases).</summary>
    public void ResetAsteroidExports() { lock (_gate) _asteroidExports.Clear(); }

    /// <summary>All <c>FM_S_PLAYERINFO(10)</c> received so far, in arrival order —
    /// including the one-shot teammate-ship PlayerInfos (P63) that attach a real
    /// <c>PlayerInfo</c> to a same-side teammate's ship object (so
    /// <c>IshipIGCWrapper::GetPlayerInfo()</c> resolves non-null instead of
    /// dereferencing a null private-data pointer and crashing the native client's
    /// line that calls <c>LastSeenState()</c>). Mirrors <see cref="Exports"/>:
    /// locked, snapshot-on-read, cleared via <see cref="ResetPlayerInfos"/>.</summary>
    public IReadOnlyList<PlayerInfo> PlayerInfos { get { lock (_gate) return _playerInfos.ToArray(); } }

    /// <summary>Clear the accumulated playerinfos (between joins/phases).</summary>
    public void ResetPlayerInfos() { lock (_gate) _playerInfos.Clear(); }

    /// <summary>All <c>FM_CS_PROJECTILE_INSTANCE(12)</c> messages received so
    /// far, in arrival order — P88: every real projectile broadcast the server
    /// sends (a shot's type id, spawn position, launch velocity/heading, and
    /// lifespan). Mirrors <see cref="Exports"/>: locked, snapshot-on-read,
    /// cleared via <see cref="ResetProjectileInstances"/>. This is test/observer
    /// instrumentation, NOT the out-of-scope AllegianceClientCore consumption —
    /// the bot simply decodes what the server sent so a loopback test can assert
    /// the same-side scope and field fidelity for real.</summary>
    public IReadOnlyList<ProjectileInstance> ProjectileInstances { get { lock (_gate) return _projectileInstances.ToArray(); } }

    /// <summary>Clear the accumulated projectile instances (between bursts).</summary>
    public void ResetProjectileInstances() { lock (_gate) _projectileInstances.Clear(); }

    /// <summary>All <c>FM_CS_FIRE_MISSILE(70)</c> messages received so far, in
    /// arrival order — P90: every real missile-launch broadcast the server sends
    /// (a launch's type id, one <see cref="MissileLaunchData"/> entry with the
    /// missile's spawn position/heading/velocity and its real id, plus the
    /// fixed lock/time/target placeholders). Test/observer instrumentation, the
    /// same nature as <see cref="ProjectileInstances"/>: the bot simply decodes
    /// what the server sent so a loopback test can assert the same-side scope
    /// and field fidelity for real.</summary>
    public IReadOnlyList<FireMissile> FireMissiles { get { lock (_gate) return _fireMissiles.ToArray(); } }

    /// <summary>All <c>FM_S_MISSILE_DESTROYED(83)</c> messages received so far,
    /// in arrival order — P90: every real missile-expiry broadcast the server
    /// sends (the expired missile's id, the sector it expired in, and where it
    /// expired, <c>Position</c>). Same test/observer instrumentation as
    /// <see cref="FireMissiles"/>/<see cref="ProjectileInstances"/>.</summary>
    public IReadOnlyList<MissileDestroyed> MissileDestroyeds { get { lock (_gate) return _missileDestroyeds.ToArray(); } }

    /// <summary>All <c>FM_S_LOADOUT_CHANGE(96)</c> messages received so far, in
    /// arrival order — P173: every ship-loadout mutation broadcast the server
    /// sends (the changed ship's current whole loadout: hull id, mounted parts,
    /// boarded-passenger list, and its own <c>SidShip</c>). Locked,
    /// snapshot-on-read, cleared via <see cref="ResetLoadoutChanges"/>.</summary>
    public IReadOnlyList<Allegiance.Core.Wire.LoadoutChange> LoadoutChanges { get { lock (_gate) return _loadoutChanges.ToArray(); } }
    public void ResetLoadoutChanges() { lock (_gate) _loadoutChanges.Clear(); }

    /// <summary>All <c>FM_S_EXPORT(26)</c> <c>OT_mine</c> exports received so far,
    /// in arrival order — P94: every real mine-deployment broadcast the server
    /// sends (the mine's real position, its decoded <c>DataMineTypeIGC</c> id,
    /// the laying ship's id and side, the armed fraction, and <c>createNow</c>).
    /// Test/observer instrumentation, the same nature as
    /// <see cref="ProjectileInstances"/>/<see cref="FireMissiles"/>: the bot simply
    /// decodes what the server sent so a loopback test can assert the same-side
    /// scope and field fidelity for real. Mirrors <see cref="Exports"/>: locked,
    /// snapshot-on-read, cleared via <see cref="ResetMineExports"/>.</summary>
    public IReadOnlyList<ExportMine> MineExports { get { lock (_gate) return _mineExports.ToArray(); } }

    /// <summary>Clear the accumulated mine exports (between joins/phases).</summary>
    public void ResetMineExports() { lock (_gate) _mineExports.Clear(); }

    /// <summary>All <c>FM_S_MINE_DESTROYED(84)</c> messages received so far, in
    /// arrival order — P94: every real mine-expiry broadcast the server sends
    /// (the expired mine's id and the sector it expired in). Same test/observer
    /// instrumentation as <see cref="MineExports"/>/<see cref="MissileDestroyeds"/>.</summary>
    public IReadOnlyList<MineDestroyed> MineDestroyeds { get { lock (_gate) return _mineDestroyeds.ToArray(); } }

    /// <summary>Clear the accumulated mine-destroyed notices (between joins/phases).</summary>
    public void ResetMineDestroyeds() { lock (_gate) _mineDestroyeds.Clear(); }

    /// <summary>P182 — the <c>FM_S_FIRE_EXPENDABLE(87)</c> echoes received
    /// (each names the firing ship + dispenser type), mirroring
    /// <see cref="MineExports"/>: locked, snapshot-on-read.</summary>
    public IReadOnlyList<FireExpendable> FireExpendables { get { lock (_gate) return _fireExpendables.ToArray(); } }

    /// <summary>P182 — the <c>FM_S_CREATE_CHAFF(109)</c> decoy-creation notices
    /// received (position/velocity/time/type-id), mirroring <see cref="MineExports"/>.</summary>
    public IReadOnlyList<CreateChaff> CreateChaffs { get { lock (_gate) return _createChaffs.ToArray(); } }

    /// <summary>P182 — the <c>FM_S_MISSILE_SPOOFED(110)</c> notices received
    /// (one per missile whose lock the chaff broke), mirroring <see cref="MineExports"/>.</summary>
    public IReadOnlyList<MissileSpoofed> MissileSpoofeds { get { lock (_gate) return _missileSpoofeds.ToArray(); } }

    /// <summary>P182 — the <c>FM_S_END_SPOOFING(111)</c> markers received
    /// (payload-free; one per chaff deployment closes the spoof window),
    /// mirroring <see cref="MineExports"/>.</summary>
    public IReadOnlyList<EndSpoofing> EndSpoofings { get { lock (_gate) return _endSpoofings.ToArray(); } }

    /// <summary>All <c>FM_S_EXPORT(26)</c>/<c>OT_probe</c> messages received so
    /// far, in arrival order — P99: every real probe-deployment broadcast the
    /// server sends (the probe's real position, its decoded
    /// <c>DataProbeTypeIGC</c> id, the deploying ship's id and side, the
    /// targetless <c>otTarget</c>/<c>oidTarget</c> = NA/-1, and <c>createNow</c>).
    /// Test/observer instrumentation, the same nature as
    /// <see cref="MineExports"/>: the bot simply decodes what the server sent so
    /// a loopback test can assert the same-side scope and field fidelity for
    /// real. Locked, snapshot-on-read, cleared via <see cref="ResetProbeExports"/>.</summary>
    public IReadOnlyList<ExportProbe> ProbeExports { get { lock (_gate) return _probeExports.ToArray(); } }

    /// <summary>Clear the accumulated probe exports (between joins/phases).</summary>
    public void ResetProbeExports() { lock (_gate) _probeExports.Clear(); }

    /// <summary>All <c>FM_S_PROBE_DESTROYED(85)</c> messages received so far, in
    /// arrival order — P99: every real probe-expiry broadcast the server sends
    /// (the expired probe's id and the sector it expired in). Same test/observer
    /// instrumentation as <see cref="MineDestroyeds"/>.</summary>
    public IReadOnlyList<ProbeDestroyed> ProbeDestroyeds { get { lock (_gate) return _probeDestroyeds.ToArray(); } }

    /// <summary>Clear the accumulated probe-destroyed notices (between joins/phases).</summary>
    public void ResetProbeDestroyeds() { lock (_gate) _probeDestroyeds.Clear(); }

    /// <summary>All <c>FM_S_ASTEROID_DESTROYED(86)</c> messages received so far,
    /// in arrival order — P102: every real asteroid-destroy broadcast the server
    /// sends (the destroyed rock's asteroid id, its sector id and whether it
    /// exploded). Like <see cref="WarpBombs"/>, this one arrives at <b>every</b>
    /// peer, cross-side included (whole mission, <c>FedSrv.CPP:12418</c>).</summary>
    public IReadOnlyList<AsteroidDestroyed> AsteroidDestroyeds { get { lock (_gate) return _asteroidDestroyeds.ToArray(); } }

    /// <summary>Clear the accumulated asteroid-destroyed notices (between joins/phases).</summary>
    public void ResetAsteroidDestroyeds() { lock (_gate) _asteroidDestroyeds.Clear(); }

    /// <summary>All <c>FM_S_ASTEROID_MINED(198)</c> messages received so far, in
    /// arrival order — P120: every real mining broadcast the server sends (the
    /// mined rock's asteroid id, its sector id and its remaining ore as a div-240
    /// byte). Like <see cref="AsteroidDestroyeds"/>, this one arrives at
    /// <b>every</b> peer, cross-side included (whole mission,
    /// <c>FedSrv.CPP:12418</c> audience).</summary>
    public IReadOnlyList<AsteroidMined> AsteroidMineds { get { lock (_gate) return _asteroidMineds.ToArray(); } }

    /// <summary>Clear the accumulated asteroid-mined notices (between joins/phases).</summary>
    public void ResetAsteroidMineds() { lock (_gate) _asteroidMineds.Clear(); }

    /// <summary>The <c>FM_S_STATION_DESTROYED(59)</c> notices this bot has seen
    /// (P125) — a <see cref="StationDestroyed"/> per received message, appended
    /// on the receiver thread under <see cref="_gate"/> like
    /// <see cref="AsteroidDestroyeds"/>. The <c>stationID</c> names the destroyed
    /// base; <c>launcher</c> is the credited ship id or NA (-1). Cleared by
    /// <see cref="ResetStationDestroyeds"/>.</summary>
    public IReadOnlyList<StationDestroyed> StationDestroyeds { get { lock (_gate) return _stationDestroyeds.ToArray(); } }
    public void ResetStationDestroyeds() { lock (_gate) _stationDestroyeds.Clear(); }

    /// <summary>The <c>FM_S_STATION_CAPTURE(58)</c> notices this bot has seen
    /// (P126) — a <see cref="StationCapture"/> per received message, appended on
    /// the receiver thread under <see cref="_gate"/> like
    /// <see cref="StationDestroyeds"/>. Carries the captured base id and the
    /// <c>sidOld</c>/<c>sidNew</c>/<c>shipIDCredit</c> triple. Cleared by
    /// <see cref="ResetStationCaptures"/>.</summary>
    public IReadOnlyList<StationCapture> StationCaptures { get { lock (_gate) return _stationCaptures.ToArray(); } }
    public void ResetStationCaptures() { lock (_gate) _stationCaptures.Clear(); }

    /// <summary>The <c>FM_S_GAME_STATE(155)</c> broadcasts this bot has seen
    /// (P191) — a <see cref="Wire.GameState"/> per received message (one per
    /// station capture/kill recompute in which a side's conquest/territory value
    /// changed), appended on the receiver thread under <see cref="_gate"/> like
    /// <see cref="StationCaptures"/>. Carries the four per-side arrays
    /// (conquest/territory/nFlags/nArtifacts — the latter two always zero).
    /// Cleared by <see cref="ResetGameStates"/>.</summary>
    public IReadOnlyList<GameState> GameStates { get { lock (_gate) return _gameStates.ToArray(); } }
    public void ResetGameStates() { lock (_gate) _gameStates.Clear(); }

    /// <summary>The <c>FM_S_GAME_OVER(62)</c> announcements this bot has seen
    /// (P191/P178) — a <see cref="Wire.GameOver"/> per received message, appended
    /// on the receiver thread under <see cref="_gate"/> like
    /// <see cref="GameStates"/>. Cleared by <see cref="ResetGameOvers"/>.</summary>
    public IReadOnlyList<GameOver> GameOvers { get { lock (_gate) return _gameOvers.ToArray(); } }
    public void ResetGameOvers() { lock (_gate) _gameOvers.Clear(); }

    /// <summary>The <c>FM_S_GAME_OVER_PLAYERS(63)</c> broadcasts this bot has
    /// seen (P194) — a <see cref="Wire.GameOverPlayers"/> per received message,
    /// appended on the receiver thread under <see cref="_gate"/> like
    /// <see cref="GameOvers"/>. Each carries the per-player endgame list
    /// (name/side/rank/score). Named deliberately differently from the
    /// <see cref="Wire.GameOverPlayers"/> type it holds so the property doesn't
    /// shadow the type inside this class (the P186 <c>ChangeAlliances</c>
    /// shadowing pitfall). Cleared by <see cref="ResetGameOverPlayerBroadcasts"/>.</summary>
    public IReadOnlyList<GameOverPlayers> GameOverPlayerBroadcasts { get { lock (_gate) return _gameOverPlayers.ToArray(); } }
    public void ResetGameOverPlayerBroadcasts() { lock (_gate) _gameOverPlayers.Clear(); }

    /// <summary>The <c>FM_S_STATIONS_UPDATE(57)</c> batches this bot has seen
    /// (P133) — a <see cref="StationsUpdate"/> per received message (one per
    /// side per tick when that side sees any station), appended on the receiver
    /// thread under <see cref="_gate"/> like <see cref="StationCaptures"/>.
    /// Each carries the per-side list of <see cref="StationsUpdateEntry"/> —
    /// the stations this side currently sees and their div-240 hull/shield
    /// fractions. Cleared by <see cref="ResetStationsUpdates"/>.</summary>
    public IReadOnlyList<StationsUpdate> StationsUpdates { get { lock (_gate) return _stationsUpdates.ToArray(); } }
    public void ResetStationsUpdates() { lock (_gate) _stationsUpdates.Clear(); }

    /// <summary>The <c>FM_S_ASTEROIDS_UPDATE(179)</c> batches this bot has seen
    /// (P134) — an <see cref="AsteroidsUpdate"/> per received message (one per
    /// side per tick when that side sees any asteroid), appended on the receiver
    /// thread under <see cref="_gate"/> like <see cref="StationsUpdates"/>.
    /// Each carries the per-side list of <see cref="AsteroidStateEntry"/> — the
    /// asteroids this side currently sees, with their raw <c>ore</c> short,
    /// div-240 health <c>bpFraction</c> and identity <c>co</c>. Cleared by
    /// <see cref="ResetAsteroidsUpdates"/>.</summary>
    public IReadOnlyList<AsteroidsUpdate> AsteroidsUpdates { get { lock (_gate) return _asteroidsUpdates.ToArray(); } }
    public void ResetAsteroidsUpdates() { lock (_gate) _asteroidsUpdates.Clear(); }

    /// <summary>The <c>FM_S_PROBES_UPDATE(92)</c> batches this bot has seen
    /// (P135) — a <see cref="ProbesUpdate"/> per received message (one per side
    /// per tick when that side sees any probe), appended on the receiver thread
    /// under <see cref="_gate"/> like <see cref="AsteroidsUpdates"/>. Each
    /// carries the per-side list of <see cref="ProbesUpdateEntry"/> — the
    /// probes this side currently sees, with their div-240 health
    /// <c>bpFraction</c>. Cleared by <see cref="ResetProbesUpdates"/>.</summary>
    public IReadOnlyList<ProbesUpdate> ProbesUpdates { get { lock (_gate) return _probesUpdates.ToArray(); } }
    public void ResetProbesUpdates() { lock (_gate) _probesUpdates.Clear(); }

    /// <summary>All <c>FM_S_HEAVY_SHIPS_UPDATE(176)</c> messages received so far,
    /// in arrival order — P139: one per connected player per tick. Each carries
    /// the per-player heavy ship list (every cluster candidate minus the
    /// recipient's own ship, plus the promoted target in slot 0) and the
    /// per-recipient scalar region (<c>timeReference</c>/<c>positionReference</c>,
    /// <c>bpTargetHull</c>/<c>bpTargetShield</c>, and the recipient's own
    /// fractions). Cleared by <see cref="ResetHeavyShipsUpdates"/>.</summary>
    public IReadOnlyList<HeavyShipsUpdate> HeavyShipsUpdates { get { lock (_gate) return _heavyShipsUpdates.ToArray(); } }
    public void ResetHeavyShipsUpdates() { lock (_gate) _heavyShipsUpdates.Clear(); }

    /// <summary>All <c>FM_S_LIGHT_SHIPS_UPDATE(175)</c> messages received so far,
    /// in arrival order — P139. Currently never populated by the server (the
    /// every-candidate-goes-heavy decision leaves no light tier), so this stays
    /// empty until the P141 budget reintroduces light records; retained for the
    /// eventual consumer. Cleared by <see cref="ResetLightShipsUpdates"/>.</summary>
    public IReadOnlyList<LightShipsUpdate> LightShipsUpdates { get { lock (_gate) return _lightShipsUpdates.ToArray(); } }
    public void ResetLightShipsUpdates() { lock (_gate) _lightShipsUpdates.Clear(); }

    /// <summary>All <c>FM_S_WARP_BOMB(188)</c> messages received so far, in
    /// arrival order — P101: every real warp-bomb broadcast the server sends
    /// (the future explosion tick, the bombed warp id and the missile type id).
    /// The same test/observer instrumentation as <see cref="MineDestroyeds"/>;
    /// unlike those same-side notices, this one arrives at <b>every</b> peer,
    /// cross-side included.</summary>
    public IReadOnlyList<WarpBomb> WarpBombs { get { lock (_gate) return _warpBombs.ToArray(); } }

    /// <summary>Clear the accumulated warp-bomb notices (between joins/phases).</summary>
    public void ResetWarpBombs() { lock (_gate) _warpBombs.Clear(); }

    /// <summary>Received <c>FM_S_RIPCORD_ACTIVATE(185)</c> frames (P111) —
    /// server confirmations that a ripcord countdown began/re-targeted.</summary>
    public IReadOnlyList<RipcordActivate> RipcordActivates { get { lock (_gate) return _ripcordActivates.ToArray(); } }
    public void ResetRipcordActivates() { lock (_gate) _ripcordActivates.Clear(); }

    /// <summary>Received <c>FM_S_RIPCORD_DENIED(186)</c> frames (P111) — server
    /// refusals of a ripcord request with no valid anchor, sent to the requester
    /// alone.</summary>
    public IReadOnlyList<RipcordDenied> RipcordDenieds { get { lock (_gate) return _ripcordDenieds.ToArray(); } }
    public void ResetRipcordDenieds() { lock (_gate) _ripcordDenieds.Clear(); }

    /// <summary>Received <c>FM_S_RIPCORD_ABORTED(187)</c> frames (P111) —
    /// server notices that a ripcord countdown was cancelled, broadcast
    /// sector-wide.</summary>
    public IReadOnlyList<RipcordAborted> RipcordAborteds { get { lock (_gate) return _ripcordAborteds.ToArray(); } }
    public void ResetRipcordAborteds() { lock (_gate) _ripcordAborteds.Clear(); }
    public IReadOnlyList<PlayerRescued> PlayerRescueds { get { lock (_gate) return _playerRescueds.ToArray(); } }
    public void ResetPlayerRescueds() { lock (_gate) _playerRescueds.Clear(); }

    /// <summary>The <c>FM_S_RELAUNCH_SHIP</c>(192) frames received since connect
    /// (P117): each records a carrier-landing relaunch observed by this peer.</summary>
    public IReadOnlyList<RelaunchShip> RelaunchShips { get { lock (_gate) return _relaunchShips.ToArray(); } }
    public void ResetRelaunchShips() { lock (_gate) _relaunchShips.Clear(); }

    /// <summary>The <c>FM_S_MONEY_CHANGE</c>(60) frames received since connect
    /// (P118): each side-wide money-change broadcast this peer observed on the
    /// wire (a refund credited to the side treasury). Locked, snapshot-on-read,
    /// cleared via <see cref="ResetMoneyChanges"/>.</summary>
    public IReadOnlyList<MoneyChange> MoneyChanges { get { lock (_gate) return _moneyChanges.ToArray(); } }
    public void ResetMoneyChanges() { lock (_gate) _moneyChanges.Clear(); }

    /// <summary>The <c>FM_S_SET_MONEY</c>(115) frames received since connect
    /// (P118): the join-time side-money sync sent to the joining player.
    /// Locked, snapshot-on-read, cleared via <see cref="ResetSetMoneys"/>.</summary>
    public IReadOnlyList<SetMoney> SetMoneys { get { lock (_gate) return _setMoneys.ToArray(); } }
    public void ResetSetMoneys() { lock (_gate) _setMoneys.Clear(); }

    /// <summary>The <c>FM_S_AUTODONATE</c>(116) frames received since connect
    /// (P197): server broadcasts of a side player's donate relationship.
    /// Locked, snapshot-on-read, cleared via <see cref="ResetAutoDonateUpdates"/>.</summary>
    public IReadOnlyList<AutoDonateUpdate> AutoDonateUpdates { get { lock (_gate) return _autoDonateUpdates.ToArray(); } }
    public void ResetAutoDonateUpdates() { lock (_gate) _autoDonateUpdates.Clear(); }

    /// <summary>Locked, snapshot-on-read, cleared via
    /// <see cref="ResetBucketStatuses"/>. Captures the team bucket
    /// funding/completion broadcasts (P119).</summary>
    public IReadOnlyList<BucketStatus> BucketStatuses { get { lock (_gate) return _bucketStatuses.ToArray(); } }
    public void ResetBucketStatuses() { lock (_gate) _bucketStatuses.Clear(); }

    /// <summary>Locked, snapshot-on-read, cleared via
    /// <see cref="ResetCreateBuckets"/>. Captures the bucket-catalog preface
    /// broadcasts (P119).</summary>
    public IReadOnlyList<CreateBuckets> CreateBucketsReceived { get { lock (_gate) return _createBucketsRx.ToArray(); } }
    public void ResetCreateBuckets() { lock (_gate) _createBucketsRx.Clear(); }

    /// <summary>Captured <c>DEV_COMPLETED(8)</c> messages (P189), in receive
    /// order. Cleared via <see cref="ResetDevCompleteds"/>.</summary>
    public IReadOnlyList<DevCompleted> DevCompleteds { get { lock (_gate) return _devCompleteds.ToArray(); } }
    public void ResetDevCompleteds() { lock (_gate) _devCompleteds.Clear(); }

    /// <summary>Captured <c>SIDE_TECH_CHANGE(75)</c> messages (P189), in receive
    /// order. Cleared via <see cref="ResetSideTechChanges"/>.</summary>
    public IReadOnlyList<SideTechChange> SideTechChanges { get { lock (_gate) return _sideTechChanges.ToArray(); } }
    public void ResetSideTechChanges() { lock (_gate) _sideTechChanges.Clear(); }

    /// <summary>Captured <c>SIDE_ATTRIBUTE_CHANGE(76)</c> messages (P189), in
    /// receive order. Cleared via <see cref="ResetSideAttributeChanges"/>.</summary>
    public IReadOnlyList<SideAttributeChange> SideAttributeChanges { get { lock (_gate) return _sideAttributeChanges.ToArray(); } }
    public void ResetSideAttributeChanges() { lock (_gate) _sideAttributeChanges.Clear(); }

    /// <summary>Captured <c>SET_BRIEFING_TEXT(9)</c> messages received from the
    /// server (P121). Snapshot-on-read, cleared via
    /// <see cref="ResetSetBriefingTexts"/>.</summary>
    public IReadOnlyList<SetBriefingText> SetBriefingTexts { get { lock (_gate) return _setBriefingTexts.ToArray(); } }
    public void ResetSetBriefingTexts() { lock (_gate) _setBriefingTexts.Clear(); }

    /// <summary>Captured <c>RANK_INFO(13)</c> messages received from the server
    /// (P130). Snapshot-on-read, cleared via <see cref="ResetRankInfos"/>.</summary>
    public IReadOnlyList<RankInfo> RankInfos { get { lock (_gate) return _rankInfos.ToArray(); } }
    public void ResetRankInfos() { lock (_gate) _rankInfos.Clear(); }

    /// <summary>Captured <c>ENTER_GAME(128)</c> messages received from the server
    /// (P141). Snapshot-on-read, cleared via <see cref="ResetEnterGames"/>.</summary>
    public IReadOnlyList<EnterGame> EnterGames { get { lock (_gate) return _enterGames.ToArray(); } }
    public void ResetEnterGames() { lock (_gate) _enterGames.Clear(); }

    /// <summary>Captured <c>TREASURE_SETS(89)</c> messages received from the
    /// server (P123). Snapshot-on-read, cleared via
    /// <see cref="ResetTreasureSets"/>.</summary>
    public IReadOnlyList<TreasureSets> TreasureSetsReceived { get { lock (_gate) return _treasureSetsRx.ToArray(); } }
    public void ResetTreasureSets() { lock (_gate) _treasureSetsRx.Clear(); }

    /// <summary>Captured <c>DESTROY_TREASURE(30)</c> messages received from the
    /// server (P123). Snapshot-on-read, cleared via
    /// <see cref="ResetDestroyTreasures"/>.</summary>
    public IReadOnlyList<DestroyTreasure> DestroyTreasures { get { lock (_gate) return _destroyTreasures.ToArray(); } }
    public void ResetDestroyTreasures() { lock (_gate) _destroyTreasures.Clear(); }

    /// <summary>Captured <c>BUILDINGEFFECT_DESTROYED(181)</c> messages received
    /// from the server (P164). Snapshot-on-read, cleared via
    /// <see cref="ResetBuildingEffectDestroyeds"/>.</summary>
    public IReadOnlyList<BuildingEffectDestroyed> BuildingEffectDestroyeds { get { lock (_gate) return _buildingEffectDestroyed.ToArray(); } }
    public void ResetBuildingEffectDestroyeds() { lock (_gate) _buildingEffectDestroyed.Clear(); }

    /// <summary>
    /// Send a real <c>FM_C_RIPCORD_REQUEST(184)</c> — the client's request to
    /// start/re-target (a real target <paramref name="sidRipcord"/>) or cancel
    /// (<paramref name="sidRipcord"/> == <c>NA</c> = -1) a ripcord countdown,
    /// the P111 port of <c>FedSrv.CPP:7715-7728</c>. The server answers with
    /// <see cref="RipcordActivates"/>, <see cref="RipcordDenieds"/>, or
    /// <see cref="RipcordAborteds"/> as appropriate.
    /// </summary>
    public void SendRipcordRequest(short sidRipcord)
    {
        var msg = new RipcordRequest { SidRipcord = sidRipcord };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_SUICIDE(80)</c> (P103) — the client's
    /// suicide request, an empty header-only message. The server routes it
    /// through <c>HandleSuicide</c>'s gates (including the
    /// <c>mission.Stage != Started</c> refusal P180 makes exercisable from the
    /// NotStarted boot state) and either kills the ship or reports-and-ignores.
    /// </summary>
    public void SendSuicide()
    {
        Send(FedPacket.Pack(new[] { new Suicide().Encode() }));
    }

    /// <summary>Send an undock announce, encoded as a real <c>FM_C_DOCKED(48)</c>
    /// — the mirror of <see cref="CommandControls"/> for the only surviving half
    /// of that message. <paramref name="stationId"/> of <c>-1</c> (NA) is the
    /// pure-undock announce the server honours (<c>Messages.h:402-404</c>;
    /// <c>stationID != NA</c> is the obsolete dock-request half, dropped by
    /// <c>Server.cs</c>). Filled as the client would — no sender field, just the
    /// single <c>StationID</c>.</summary>
    public void SendDocked(short stationId = -1)
    {
        var msg = new Docked { StationId = stationId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_BUY_LOADOUT(4)</c> — what the native
    /// WinTrek client's Hangar screen "Launch" button actually sends
    /// (<c>HangarScreen::OnButtonLaunch</c> -> <c>BuyLoadout(ship, true)</c>,
    /// hangarscreen.cpp:145-160), unlike <see cref="SendDocked"/> which is the
    /// separate undock message only bots use. <paramref name="fLaunch"/>=true
    /// is the real client's launch request; the server is expected to answer
    /// with <c>FM_S_BUY_LOADOUT_ACK</c> and, if the ship was docked, also
    /// undock it (a second <see cref="SetCluster"/> lands in
    /// <see cref="Assignments"/>).</summary>
    public void SendBuyLoadout(short hullId, bool fLaunch, IReadOnlyList<ExpandedPartData>? parts = null)
    {
        var msg = new BuyLoadoutRequest { HullId = hullId, FLaunch = fLaunch, Parts = parts ?? [] };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_CS_PLAYER_READY(45)</c> — what the native
    /// WinTrek client's Team Lobby "JOIN" control actually sends
    /// (<c>teampane.cpp</c>). The server is expected to relay it back to
    /// everyone on the sender's side, including the sender itself; see
    /// <see cref="PlayerReadyEchoes"/>.</summary>
    public void SendPlayerReady(bool fReady, short shipId)
    {
        var msg = new PlayerReady { FReady = fReady, ShipId = shipId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_BOARD_SHIP(103)</c> (P108) — the client's
    /// request to board <paramref name="sidParent"/> (or leave the current
    /// parent when it is <c>NA</c> = -1). The server answers with a
    /// <c>BOARD_NACK(178)</c> on refusal or, on success, folds the boarded
    /// state into the periodic <c>SHIP_STATUS</c> refresh and (P118) broadcasts
    /// a <c>MONEY_CHANGE(60)</c> for the hardware-value refund.</summary>
    public void SendBoard(short sidParent)
    {
        var msg = new BoardShip { SidParent = sidParent };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_BUCKET_DONATE(64)</c> — donate
    /// <paramref name="moneyGiven"/> into the side's bucket
    /// <paramref name="iBucket"/> (P119, Messages.h:488-491).</summary>
    public void SendDonate(short iBucket, int moneyGiven)
    {
        var msg = new BucketDonate { IBucket = iBucket, MoneyGiven = moneyGiven };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a chat message, encoded as a real <c>FM_CS_CHATMESSAGE(23)</c> —
    /// the mirror of <see cref="CommandControls"/> for chat. The sender ship is
    /// filled from this bot's own assignment (<c>cd.sidSender</c>,
    /// <c>Messages.h:206</c>), text is encoded as a CB_ZTS blob
    /// (<c>clintlib.cpp:2444-2445</c>), and the supported targets are
    /// <see cref="ChatTarget.Everyone"/>, <see cref="ChatTarget.Team"/>,
    /// <see cref="ChatTarget.Individual"/> (and its aliases
    /// <see cref="ChatTarget.IndividualNoFilter"/> /
    /// <see cref="ChatTarget.Group"/> / <see cref="ChatTarget.GroupNoEcho"/>),
    /// <see cref="ChatTarget.AllSector"/> and <see cref="ChatTarget.FriendlySector"/>
    /// (others are relayed to nobody by the server, which logs them — see
    /// <c>Server.cs::SelectChatTargets</c>).
    /// </summary>
    public void SendChat(
        string message,
        ChatTarget target = ChatTarget.Everyone,
        short oidRecipient = 0,
        short voiceOver = 0,
        short commandId = 0,
        bool objectModel = false,
        short oidTarget = -1,
        short otTarget = -1)
    {
        var msg = new ChatMessage
        {
            Message = message,
            SidSender = _myShipId,
            ChatTarget = (int)target,
            OidRecipient = oidRecipient,
            VoiceOver = voiceOver,
            CommandID = commandId,
            BObjectModel = objectModel,
            OidTarget = oidTarget,
            OtTarget = otTarget,
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_CHATBUOY(108)</c> — the message the native
    /// <c>BaseClient::SendChat</c> actually uses when the chat targets a buoy
    /// object (<c>clintlib.cpp:2460-2469</c>: <c>otTarget == OT_buoy</c> →
    /// CHATBUOY, with the target's <see cref="DataBuoyIGC db"/> exported into
    /// the payload; otherwise CHATMESSAGE). This is the message a commander's
    /// <c>SendChat(..., otTarget: buoy.GetObjectType(), oidTarget: buoy.GetObjectID(),
    /// buoy, ...)</c> (CommanderResearchAndExpand.cs:1615-1618) emits to
    /// dispatch a docked miner. Routing semantics match <see cref="SendChat"/>
    /// exactly (the server's <c>SelectChatTargets</c> is shared).
    /// </summary>
    public void SendChatBuoy(
        string message,
        ChatTarget target,
        short oidRecipient,
        DataBuoyIGC db,
        short voiceOver = 0,
        short commandId = 0,
        bool objectModel = false)
    {
        var msg = new ChatBuoy
        {
            Message = message,
            Cd = new ChatData
            {
                SidSender = _myShipId,
                ChatTarget = (int)target,
                OidRecipient = oidRecipient,
                VoiceOver = voiceOver,
                CommandID = commandId,
                BObjectModel = objectModel,
            },
            Db = db,
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_AUTODONATE(112)</c> (P197) — the client's
    /// auto-donate request. <paramref name="amount"/> of <c>-1</c> selects the
    /// leader-only toggle of <paramref name="sidDonateTo"/>'s donation; any other
    /// value sets/clears the sender's own donate target with an optional one-time
    /// gift (<c>sidDonateTo</c> <c>NA</c> = -1 clears it).</summary>
    public void SendAutoDonate(short sidDonateTo, int amount)
    {
        var msg = new AutoDonate { SidDonateTo = sidDonateTo, Amount = amount };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_HIGHLIGHT_CLUSTER(199)</c> — the commander marking
    /// a sector on the map for their own teammates (Xynth #208). The server
    /// relays it to the sender's whole side group verbatim
    /// (<c>FedSrv.CPP:7369-7375</c>; this port's <c>Server.cs</c>
    /// HIGHLIGHT_CLUSTER branch). Bare wire codec: <c>clusterID</c> short +
    /// <c>highlight</c> bool (<c>Messages.h:1033-1036</c>).
    /// </summary>
    public void SendHighlightCluster(short clusterId, bool highlight)
    {
        var msg = new HighlightCluster
        {
            ClusterId = clusterId,
            Highlight = highlight,
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_SWAP_PART(34)</c> — move this bot's mounted part at
    /// <c>(etOld, mountOld)</c> to <c>mountNew</c>, swapping it with a different
    /// same-type part already occupying <c>mountNew</c> if one is present
    /// (P143). <c>shipID</c> is left unset: the client sends it ignored and the
    /// server fills it in on the sector-wide echo (<c>FedSrv.CPP:6497</c>).
    /// </summary>
    public void SendSwapPart(EquipmentType etOld, sbyte mountOld, sbyte mountNew)
    {
        var msg = new SwapPart { EtOld = etOld, MountOld = mountOld, MountNew = mountNew };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_RELOAD(129)</c> — reload this bot's cargo hold into
    /// its live pools / equipped launchers (P192). <paramref name="entries"/> are
    /// up to 5 <c>{amountTransfered, mount}</c> payloads, each targeting a
    /// negative cargo-bay mount (<c>-1..-5</c>); <c>amountTransfered == -1</c>
    /// (native <c>NA</c>) terminates the whole cargo item. <c>shipID</c> is left
    /// unset: the client sends it ignored and the server fills it in on the
    /// sector-wide echo (<c>FedSrv.CPP:6651</c>).
    /// </summary>
    public void SendReload(params ReloadData[] entries)
    {
        var msg = new Reload { Entries = entries };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_C_POSITIONREQ(39)</c> — this bot's request to be
    /// seated on the given side (P184). The server either auto-accepts (leaderless
    /// side) or broadcasts <c>POSITIONREQ(40)</c> for the leader's approval.
    /// </summary>
    public void SendPositionReq(short sideId)
    {
        var msg = new PositionReq { SideId = sideId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_C_POSITIONACK(41)</c> — this bot (acting as a team
    /// leader) accepts or rejects a pending side-join request for
    /// <paramref name="shipId"/> on <paramref name="sideId"/> (P184).
    /// </summary>
    public void SendPositionAck(short shipId, short sideId, bool accepted)
    {
        var msg = new PositionAck { ShipId = shipId, SideId = sideId, FAccepted = accepted };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_DELPOSITIONREQ(42)</c> — this bot self-cancels its
    /// own pending request (<paramref name="shipId"/> == own ship,
    /// <see cref="DelPositionReqReason.Canceled"/>) or, as a leader, rejects
    /// another ship's request (<see cref="DelPositionReqReason.Rejected"/>) (P184).
    /// </summary>
    public void SendDelPositionReq(short shipId, short sideId, DelPositionReqReason reason)
    {
        var msg = new DelPositionReq { ShipId = shipId, SideId = sideId, Reason = reason };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_FORCE_TEAM_READY(47)</c> — this bot (as a team
    /// leader) forces (<paramref name="forceReady"/> == true) or un-forces
    /// (false) its side ready (P184).
    /// </summary>
    public void SendForceTeamReady(short sideId, bool forceReady)
    {
        var msg = new ForceTeamReady { SideId = sideId, FForceReady = forceReady };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_C_START_GAME(157)</c> (P188) — the mission owner's
    /// manual trigger to begin the start countdown. A header-only, zero-payload
    /// marker (Messages.h:868-869). The server gates it on ownership +
    /// NotStarted + all-occupied-sides-ready, then starts the 15s countdown.
    /// </summary>
    public void SendStartGame()
    {
        Send(FedPacket.Pack(new[] { new StartGame().Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_C_RANDOMIZE_TEAMS(21)</c> (P195) — the mission
    /// owner's "balance the teams" trigger. A header-only, zero-payload marker
    /// (Messages.h:201). The server gates it on ownership + NotStarted, then
    /// greedily rebalances non-leaders across sides by rank.
    /// </summary>
    public void SendRandomizeTeams()
    {
        Send(FedPacket.Pack(new[] { new RandomizeTeams().Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_PING(35)</c> (P188) — a keepalive/latency probe
    /// stamped with <paramref name="timeClient"/>; the server stamps its own
    /// server time and echoes the message back to this same connection.
    /// </summary>
    public void SendPing(uint timeClient, int fmg = 0)
    {
        var msg = new Ping { TimeClient = timeClient, Fmg = fmg };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_LOCK_LOBBY(43)</c> — this bot (as the mission
    /// owner) locks (<paramref name="fLock"/> == true) or unlocks (false) the
    /// mission lobby (P186). The server relays it whole-mission and stores
    /// <c>Mission.LockLobby</c>.
    /// </summary>
    public void SendLockLobby(bool fLock)
    {
        var msg = new LockLobby { FLock = fLock };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_LOCK_SIDES(44)</c> — this bot (as the mission
    /// owner) locks (<paramref name="fLock"/> == true) or unlocks (false) team
    /// changes (P186). The server relays it whole-mission, stores
    /// <c>Mission.LockSides</c>, and refuses side-switch POSITIONREQs while on.
    /// </summary>
    public void SendLockSides(bool fLock)
    {
        var msg = new LockSides { FLock = fLock };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_AUTO_ACCEPT(52)</c> — this bot (as a side's team
    /// leader) turns auto-accept on (<paramref name="autoAccept"/> == true) or
    /// off (false) for its side (P186). Turning on seats any pending requester.
    /// </summary>
    public void SendAutoAccept(short sideId, bool autoAccept)
    {
        var msg = new AutoAccept { SideId = sideId, FAutoAccept = autoAccept };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_CHANGE_TEAM_CIV(51)</c> — this bot (as a side's
    /// team leader, pre-start) changes its side's civilization to
    /// <paramref name="civId"/> (P186). <paramref name="random"/> is the native
    /// random-faction flag, ignored by this port's server.
    /// </summary>
    public void SendChangeTeamCiv(short sideId, short civId, bool random = false)
    {
        var msg = new ChangeTeamCiv { SideId = sideId, CivId = civId, Random = random };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_C_CHANGE_ALLIANCE(196)</c> — this bot (as the mission
    /// owner, pre-start) asks the server to ally side <paramref name="sideId"/>
    /// to <paramref name="sideAlly"/>, or (when <c>sideAlly</c> is <c>NA</c>,
    /// -1) to clear <paramref name="sideId"/>'s alliances (P190). The server
    /// broadcasts the resulting alliance array as <c>CHANGE_ALLIANCES(197)</c>.
    /// </summary>
    public void SendChangeAlliance(short sideId, short sideAlly)
    {
        var msg = new ChangeAlliance { SideId = sideId, SideAlly = sideAlly };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_C_VOTE(19)</c> — record this bot's vote
    /// (<paramref name="agree"/>) on the pending ballot
    /// <paramref name="ballotId"/> (P187). The one client-sendable message in
    /// the ballot cluster (Messages.h:185-188).
    /// </summary>
    public void SendVote(short ballotId, bool agree)
    {
        var msg = new Vote { BallotId = ballotId, Agree = agree };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_DROP_PART(33)</c> — drop this bot's mounted part at
    /// <c>(et, mount)</c> into space (P144). <c>shipID</c> is left unset: the
    /// client sends it ignored and the server fills it in on the sector-wide
    /// echo (<c>FedSrv.CPP:6312</c>).
    /// </summary>
    public void SendDropPart(EquipmentType et, byte mount)
    {
        var msg = new DropPart { Et = et, Mount = mount };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P182 — send a real <c>FM_C_FIRE_EXPENDABLE(82)</c> requesting the server
    /// fire this bot's mounted dispenser of <paramref name="et"/> at mount 0.
    /// The server deploys the chaff (if <paramref name="et"/> is a chaff
    /// launcher), decrements its ammo, and broadcasts the
    /// <c>FE(87)+CREATE_CHAFF(109)+MISSILE_SPOOFED(110)+END_SPOOFING(111)</c>
    /// flow to the whole cluster.
    /// </summary>
    public void SendFireExpendable(EquipmentType et)
    {
        var msg = new FireExpendableRequest { Et = et };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_SET_WINGID(106)</c> — tag this bot's <em>own</em>
    /// ship with wing-group id <paramref name="wingId"/> (P146). <c>shipID</c>
    /// must be the sender's own ship id, mirroring the native client which sends
    /// <c>shipID = GetIGC()-&gt;GetObjectID()</c> (<c>PigSrv/PigShip.cpp:240</c>);
    /// the server's self-targeting-only simplification rejects any other target.
    /// <paramref name="wingId"/> must be in <c>[0, SetWingId.WidMax)</c> (the
    /// native client validates <c>0 ≤ Val ≤ 9</c>, <c>PigSrv/PigShip.cpp:234-236</c>).
    /// The server corrects <c>bCommanded=false</c> and relays side-wide.
    /// </summary>
    public void SendSetWingId(short shipId, short wingId)
    {
        var msg = new SetWingId { ShipID = shipId, WingId = wingId, BCommanded = false };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_CS_ORDER_CHANGE(93)</c> — command a same-side ship
    /// (P148). The stay-docked slice acts when the target is a docked
    /// <see cref="Ship.IsNpc"/> drone and <paramref name="commandId"/> is
    /// <see cref="OrderChange.CidDoNothing"/> (-3): the server sets the NPC's
    /// <c>StayDocked = true</c> (native FedSrv.CPP:7290-7294). <c>shipId</c> is
    /// resolved by the server on the sending player's own side, falling back to
    /// the sender's own ship if it doesn't resolve — so pass the target NPC's id.
    /// The <c>command</c> slot selector defaults to <see cref="OrderChange.CmdQueued"/>
    /// (P148's original value, 0); pass the 3-arg overload to exercise the
    /// return-to-base slot write (P150), where the real client sends
    /// <see cref="OrderChange.CmdAccepted"/>.
    /// </summary>
    public void SendOrderChange(short shipId, short commandId)
        => SendOrderChange(shipId, commandId, OrderChange.CmdQueued);

    /// <summary>
    /// P150 — as <see cref="SendOrderChange(short, short)"/> but with an explicit
    /// <paramref name="command"/> slot selector, so a test can drive which of the
    /// port's command slots the return-to-base branch writes to (the decoded
    /// <c>OrderChange.Command</c> field — FedSrv.CPP:7330). <c>objectID</c>/
    /// <c>objectType</c> default to NA-ish values, unused by the NPC branches.
    /// </summary>
    public void SendOrderChange(short shipId, short commandId, short command)
    {
        var msg = new OrderChange { ShipID = shipId, CommandId = commandId, Command = command };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// P153 — as <see cref="SendOrderChange(short, short, short)"/> but with an
    /// explicit <paramref name="objectType"/>/<paramref name="objectId"/>, so a
    /// test can drive the generic non-NPC fallback's <c>target</c>
    /// (<c>FedSrv.CPP:7335-7339</c>) — the client's <c>SetTarget</c> UI action
    /// (<c>WinTrek.cpp:8820-8852</c>) sends <c>command=c_cmdCurrent</c> with the
    /// selected target's type/id, or <c>OT_invalid</c> + <c>NA</c> to clear.
    /// The two existing 2-arg/3-arg overloads leave these as their struct
    /// defaults (Ship/0), which the P148/P150 NPC branches never read.
    /// </summary>
    public void SendOrderChange(short shipId, short commandId, short command, IgcObjectType objectType, short objectId)
    {
        var msg = new OrderChange { ShipID = shipId, CommandId = commandId, Command = command, ObjectType = objectType, ObjectID = objectId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>
    /// Send a real <c>FM_C_TREASURE_ACK(163)</c> — the client's mount decision
    /// on an offered part-treasure pickup (P145). A real <paramref name="mountId"/>
    /// accepts the offered part into that slot; pass <see cref="TreasureAck.MountNA"/>
    /// to decline it (which makes the server re-create the part treasure at the
    /// ship's current position). The handler branches on
    /// <c>pfmTA-&gt;mountID != c_mountNA</c> (FedSrv.CPP:6099), so <c>MountId</c>
    /// is a real variable field, not the "always NA" the header comment claims.
    /// </summary>
    public void SendTreasureAck(byte mountId)
    {
        var msg = new TreasureAck { MountId = mountId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    public void Connect(ushort port, string playerName)
    {
        _transport = new TcpTransport(this);
        _transport.Join("127.0.0.1", port, playerName);
    }

    /// <summary>Send a LOGONREQ; the ACK (or rejection) is received asynchronously.</summary>
    public void SendLogonRequest(LogonRequest req) =>
        Send(FedPacket.Pack(new[] { req.Encode() }));

    /// <summary>Command the ship's controls: the four axes and the movement-bit
    /// <paramref name="stateM"/>, encoded as a real <c>FM_C_SHIP_UPDATE(24)</c>.</summary>
    public void CommandControls(ControlData controls, int stateM = 0)
    {
        var msg = new ShipUpdate
        {
            Time = 0u,              // client's own time — the server reads controls only
            Cookie = (byte)(_ack?.Cookie ?? 0),
            Payload = ClientShipUpdate.FromControls(0u, stateM, controls),
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Block until a LOGONACK arrives (or <paramref name="timeoutMs"/>). Returns the ACK.</summary>
    public LogonAck WaitForAck(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var ack = Ack;
            if (ack is not null) return ack;
            Thread.Sleep(5);
        }
        throw new TimeoutException("timed out waiting for LOGONACK");
    }

    /// <summary>Block until the SET_CLUSTER ship assignment arrives (or <paramref name="timeoutMs"/>).</summary>
    public SetCluster WaitForShipAssignment(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var set = ShipAssignment;
            if (set is not null) return set;
            Thread.Sleep(5);
        }
        throw new TimeoutException("timed out waiting for SET_CLUSTER ship assignment");
    }

    private void Send(byte[] packet) =>
        _transport!.Send(ConnectionId.Server, packet, guaranteed: true);

    // ── ITransportSite (client half: only OnReceive is raised) ─────────────
    void ITransportSite.OnPeerJoined(ConnectionId id, string name) { }
    void ITransportSite.OnPeerLeft(ConnectionId id) { }
    void ITransportSite.OnSessionLost() { }
    void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        byte[][] messages;
        try { messages = FedPacket.Unpack(data.ToArray()); }
        catch (FedCrcException) { return; }   // a bad frame is not ours to adjudicate

        lock (_gate)
        {
            foreach (var m in messages)
            {
                ushort fmid;
                try { fmid = FedMessageHeader.Read(m).Fmid; }
                catch { continue; }

                switch (fmid)
                {
                    case LogonAck.Fmid:
                        _ack = LogonAck.Decode(m);
                        break;
                    case SetCluster.Fmid:
                        _setCluster = SetCluster.Decode(m);
                        _assignments.Add(_setCluster);
                        _myShipId = _setCluster.Ship.ShipId;
                        break;
                    case SingleShipUpdate.Fmid:
                        var u = SingleShipUpdate.Decode(m);
                        var obs = new ShipObservation(
                            u.OidTarget, u.Ship.Time, u.Ship.Position, u.Ship.Velocity, u.Ship.Orientation, u.Ship.OreFraction);
                        _anyShipObservations.Add(obs);
                        if (_myShipId >= 0 && u.OidTarget == _myShipId)
                            _observations.Add(obs);
                        break;
                    case KillShip.Fmid:
                        _kills.Add(KillShip.Decode(m));
                        break;
                    case ChatMessage.Fmid:
                        _chats.Add(ChatMessage.Decode(m));
                        break;
                    case ChatBuoy.Fmid:
                        _chatBuoys.Add(ChatBuoy.Decode(m));
                        break;
                    case HighlightCluster.Fmid:
                        _highlights.Add(HighlightCluster.Decode(m));
                        break;
                    case SwapPart.Fmid:
                        _swapParts.Add(SwapPart.Decode(m));
                        break;
                    case DropPart.Fmid:
                        _dropParts.Add(DropPart.Decode(m));
                        break;
                    case Reload.Fmid:
                        _reloads.Add(Reload.Decode(m));
                        break;
                    case AcquireTreasure.Fmid:
                        _acquireTreasures.Add(AcquireTreasure.Decode(m));
                        break;
                    case AddPart.Fmid:
                        _addParts.Add(AddPart.Decode(m));
                        break;
                    case SetWingId.Fmid:
                        _setWingIds.Add(SetWingId.Decode(m));
                        break;
                    case OrderChange.Fmid:
                        _orderChanges.Add(OrderChange.Decode(m));
                        break;
                    case TreasureSpawn.Fmid:
                        _treasureSpawns.Add(TreasureSpawn.Decode(m));
                        break;
                    case ShipStatusMessage.Fmid:
                        _shipStatuses.Add(ShipStatusMessage.Decode(m));
                        break;
                    case ClusterInfo.Fmid:
                        _clusterInfos.Add(ClusterInfo.Decode(m));
                        break;
                    case Payday.Fmid:
                        _paydays.Add(Payday.Decode(m));
                        break;
                    case DockedNotify.Fmid:
                        _dockedNotices.Add(DockedNotify.Decode(m));
                        break;
                    case TeleportAck.Fmid:
                        _teleportAcks.Add(TeleportAck.Decode(m));
                        break;
                    case PlayerReady.Fmid:
                        _playerReadyEchoes.Add(PlayerReady.Decode(m));
                        break;
                    case TeamReady.Fmid:
                        _teamReadys.Add(TeamReady.Decode(m));
                        break;
                    case PlayerInfo.Fmid:
                        _playerInfos.Add(PlayerInfo.Decode(m));
                        break;
                    case ProjectileInstance.Fmid:
                        _projectileInstances.Add(ProjectileInstance.Decode(m));
                        break;
                    case FireMissile.Fmid:
                        _fireMissiles.Add(FireMissile.Decode(m));
                        break;
                    case MissileDestroyed.Fmid:
                        _missileDestroyeds.Add(MissileDestroyed.Decode(m));
                        break;
                    case Allegiance.Core.Wire.LoadoutChange.Fmid:
                        _loadoutChanges.Add(Allegiance.Core.Wire.LoadoutChange.Decode(m));
                        break;
                    case MineDestroyed.Fmid:
                        _mineDestroyeds.Add(MineDestroyed.Decode(m));
                        break;
                    case ProbeDestroyed.Fmid:
                        _probeDestroyeds.Add(ProbeDestroyed.Decode(m));
                        break;
                    case WarpBomb.Fmid:
                        _warpBombs.Add(WarpBomb.Decode(m));
                        break;
                    case AsteroidDestroyed.Fmid:
                        _asteroidDestroyeds.Add(AsteroidDestroyed.Decode(m));
                        break;
                    case AsteroidMined.Fmid:
                        _asteroidMineds.Add(AsteroidMined.Decode(m));
                        break;
                    case StationDestroyed.Fmid:
                        _stationDestroyeds.Add(StationDestroyed.Decode(m));
                        break;
                    case StationCapture.Fmid:
                        _stationCaptures.Add(StationCapture.Decode(m));
                        break;
                    case GameState.Fmid:
                        _gameStates.Add(GameState.Decode(m));
                        break;
                    case GameOver.Fmid:
                        _gameOvers.Add(GameOver.Decode(m));
                        break;
                    case GameOverPlayers.Fmid:
                        _gameOverPlayers.Add(GameOverPlayers.Decode(m));
                        break;
                    case StationsUpdate.Fmid:
                        _stationsUpdates.Add(StationsUpdate.Decode(m));
                        break;
                    case AsteroidsUpdate.Fmid:
                        _asteroidsUpdates.Add(AsteroidsUpdate.Decode(m));
                        break;
                    case ProbesUpdate.Fmid:
                        _probesUpdates.Add(ProbesUpdate.Decode(m));
                        break;
                    case HeavyShipsUpdate.Fmid:
                        _heavyShipsUpdates.Add(HeavyShipsUpdate.Decode(m));
                        break;
                    case LightShipsUpdate.Fmid:
                        _lightShipsUpdates.Add(LightShipsUpdate.Decode(m));
                        break;
                    case RipcordActivate.Fmid:
                        _ripcordActivates.Add(RipcordActivate.Decode(m));
                        break;
                    case RipcordDenied.Fmid:
                        _ripcordDenieds.Add(RipcordDenied.Decode(m));
                        break;
                    case RipcordAborted.Fmid:
                        _ripcordAborteds.Add(RipcordAborted.Decode(m));
                        break;
                    case PlayerRescued.Fmid:
                        _playerRescueds.Add(PlayerRescued.Decode(m));
                        break;
                    case RelaunchShip.Fmid:
                        _relaunchShips.Add(RelaunchShip.Decode(m));
                        break;
                    case MoneyChange.Fmid:
                        _moneyChanges.Add(MoneyChange.Decode(m));
                        break;
                    case SetMoney.Fmid:
                        _setMoneys.Add(SetMoney.Decode(m));
                        break;
                    case AutoDonateUpdate.Fmid:
                        _autoDonateUpdates.Add(AutoDonateUpdate.Decode(m));
                        break;
                    case BucketStatus.Fmid:
                        _bucketStatuses.Add(BucketStatus.Decode(m));
                        break;
                    case DevCompleted.Fmid:
                        _devCompleteds.Add(DevCompleted.Decode(m));
                        break;
                    case SideTechChange.Fmid:
                        _sideTechChanges.Add(SideTechChange.Decode(m));
                        break;
                    case SideAttributeChange.Fmid:
                        _sideAttributeChanges.Add(SideAttributeChange.Decode(m));
                        break;
                    case CreateBuckets.Fmid:
                        _createBucketsRx.Add(CreateBuckets.Decode(m));
                        break;
                    case SetBriefingText.Fmid:
                        _setBriefingTexts.Add(SetBriefingText.Decode(m));
                        break;
                    case RankInfo.Fmid:
                        _rankInfos.Add(RankInfo.Decode(m));
                        break;
                    case EnterGame.Fmid:
                        _enterGames.Add(EnterGame.Decode(m));
                        break;
                    case JoinSide.Fmid:
                        _joinSides.Add(JoinSide.Decode(m));
                        break;
                    case SetTeamLeader.Fmid:
                        _setTeamLeaders.Add(SetTeamLeader.Decode(m));
                        break;
                    case SetMissionOwner.Fmid:
                        _setMissionOwners.Add(SetMissionOwner.Decode(m));
                        break;
                    case QuitMission.Fmid:
                        _quitMissions.Add(QuitMission.Decode(m));
                        break;
                    case QuitSide.Fmid:
                        _quitSides.Add(QuitSide.Decode(m));
                        break;
                    case PositionReqBroadcast.Fmid:
                        _positionReqs.Add(PositionReqBroadcast.Decode(m));
                        break;
                    case DelPositionReq.Fmid:
                        _delPositionReqs.Add(DelPositionReq.Decode(m));
                        break;
                    case ForceTeamReady.Fmid:
                        _forceTeamReadys.Add(ForceTeamReady.Decode(m));
                        break;
                    case SetStartTime.Fmid:
                        _setStartTimes.Add(SetStartTime.Decode(m));
                        break;
                    case Ping.Fmid:
                        _pings.Add(Ping.Decode(m));
                        break;
                    case LockLobby.Fmid:
                        _lockLobbys.Add(LockLobby.Decode(m));
                        break;
                    case LockSides.Fmid:
                        _lockSidess.Add(LockSides.Decode(m));
                        break;
                    case AutoAccept.Fmid:
                        _autoAccepts.Add(AutoAccept.Decode(m));
                        break;
                    case ChangeTeamCiv.Fmid:
                        _changeTeamCivs.Add(ChangeTeamCiv.Decode(m));
                        break;
                    case ChangeAlliances.Fmid:
                        _allianceBroadcasts.Add(ChangeAlliances.Decode(m));
                        break;
                    case Ballot.Fmid:
                        _ballots.Add(Ballot.Decode(m));
                        break;
                    case CancelBallot.Fmid:
                        _cancelBallots.Add(CancelBallot.Decode(m));
                        break;
                    case TreasureSets.Fmid:
                        _treasureSetsRx.Add(TreasureSets.Decode(m));
                        break;
                    case DestroyTreasure.Fmid:
                        _destroyTreasures.Add(DestroyTreasure.Decode(m));
                        break;
                    case BuildingEffectDestroyed.Fmid:
                        _buildingEffectDestroyed.Add(BuildingEffectDestroyed.Decode(m));
                        break;
                    case FireExpendable.Fmid:
                        _fireExpendables.Add(FireExpendable.Decode(m));
                        break;
                    case CreateChaff.Fmid:
                        _createChaffs.Add(CreateChaff.Decode(m));
                        break;
                    case MissileSpoofed.Fmid:
                        _missileSpoofeds.Add(MissileSpoofed.Decode(m));
                        break;
                    case EndSpoofing.Fmid:
                        _endSpoofings.Add(EndSpoofing.Decode(m));
                        break;
                    case ExportShip.Fmid:
                        // FM_S_EXPORT(26) is the generic object-export message:
                        // the server sends ships (OT_ship=0) AND clusters
                        // (OT_cluster=13), warps (OT_warp=7), stations
                        // (OT_asteroid=5), mines (OT_mine=3), and probes
                        // (OT_probe=4) under this same
                        // fmid, distinguished only by the 4-byte `objecttype` at
                        // message offset 8. Decode the ship, asteroid, mine and
                        // probe cases; skip the others.
                        if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportShip.OtShip)
                            _exports.Add(ExportShip.Decode(m));
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportAsteroid.OtAsteroid)
                            _asteroidExports.Add(ExportAsteroid.Decode(m));
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportMine.OtMine)
                            _mineExports.Add(ExportMine.Decode(m));
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportProbe.OtProbe)
                            _probeExports.Add(ExportProbe.Decode(m));
                        break;
                }
            }
        }
    }

    /// <summary>Close the client socket. The server sees EOF / a dropped peer.</summary>
    public void Shutdown() => _transport?.Shutdown();

    public void Dispose() => Shutdown();
}
