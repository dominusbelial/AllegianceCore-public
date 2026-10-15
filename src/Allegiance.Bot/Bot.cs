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

    // P671 — native keep-alive, ported from BaseClient::CheckServerLag
    // (clintlib.cpp:2050-2087). The Bot is a real client (used for idle/weave
    // bots in the MCP harness, and the console bot), so — like ConnectionSession —
    // it must send CS_PING every 6 s while connected in EVERY state (lobby,
    // hangar, docked, flying, pod, dead) or the server's DataLinkIdleTimeout
    // would drop a silently-idle bot at ~30 s (the P654-aftermath mass
    // disconnect). The server echoes each ping (Server.Dispatch.cs:38-67).
    // The unanswered-ping session-loss rule mirrors native: >5 unanswered pings
    // AND >= 30 s since any server message => the link is considered dead
    // (clintlib.cpp:2063-2068) and pinging stops.
    private const int KeepAliveIntervalMs = 6000;   // maxLagCheck = 6.0f
    private const int PingsTimeout = 5;             // g_cPingsTimeout
    private const int DeadTimeMs = 30000;           // g_sDeadTime (s -> ms)
    private Thread? _keepAliveThread;
    private readonly ManualResetEventSlim _keepAliveGate = new(false); // set = wake/stop
    private int _keepAliveStop;
    private int _unansweredPings;        // guarded by _gate
    private long _lastServerMessageTick; // Environment.TickCount64, guarded by _gate
    private long _keepAlivePingsSent;    // guarded by _gate
    private volatile bool _lost;

    /// <summary>P671 test seam — total CS_PING keep-alive frames sent by this
    /// bot's keep-alive thread since connect.</summary>
    public long KeepAlivePingsSent { get { lock (_gate) return _keepAlivePingsSent; } }

    /// <summary>
    /// P671 — start the native keep-alive thread up-front, in the constructor,
    /// NOT in <see cref="Connect"/>. The MCP harness (ObservedSession /
    /// StallWatchdog) injects a <c>TcpTransport</c> directly into this bot's
    /// private <c>_transport</c> and calls <c>Transport.Join(...)</c> — never
    /// this class's <see cref="Connect"/>. Tying the keep-alive to
    /// <see cref="Connect"/> left every MCP-seated idle/weave bot without a
    /// keep-alive, so a silently-docked bot was dropped by the server's
    /// <c>DataLinkIdleTimeout</c> at ~30 s. The thread is started here, uses the
    /// current <c>_transport</c> lazily each tick, and is persistent (survives a
    /// reconnect; skips ticks while disconnected), so it covers every path.
    /// </summary>
    public Bot()
    {
        StartKeepAlive();
    }

    private LogonAck? _ack;
    private byte _poseCookie;   // P590 — see PoseCookie
    private SetCluster? _setCluster;
    private readonly List<SetCluster> _assignments = new();
    private readonly List<ShipReset> _shipResets = new();   // P507 (native SHIP_RESET 183)
    private readonly List<Eject> _ejects = new();           // P590 (EJECT 77)
    private readonly List<ShipObservation> _observations = new();
    private readonly List<ShipObservation> _anyShipObservations = new();
    private readonly List<KillShip> _kills = new();
    private readonly List<Allegiance.Core.Wire.CollisionEvent> _collisionEvents = new();   // P463c
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
    private readonly List<ExportCluster> _clusterExports = new();   // P480: new-world cluster EXPORTs (re-join burst)
    private readonly List<ExportWarp> _warpExports = new();         // P480: new-world warp EXPORTs (re-join burst)
    private readonly List<ExportStation> _stationExports = new();   // P480: new-world station EXPORTs (re-join burst)
    private readonly List<PlayerInfo> _playerInfos = new();
    /// <summary>Retain projectile/heavy-update observer logs; headless fleet peers disable these.</summary>
    public bool RetainRawCombatUpdates { get; set; } = true;

    private readonly List<ProjectileInstance> _projectileInstances = new();
    private readonly List<FireMissile> _fireMissiles = new();
    private readonly List<MissileDestroyed> _missileDestroyeds = new();
    private readonly List<Allegiance.Core.Wire.LoadoutChange> _loadoutChanges = new();
    private readonly List<ExportMine> _mineExports = new();
    private readonly List<MineDestroyed> _mineDestroyeds = new();
    private readonly List<ExplosionEvent> _explosionEvents = new();   // P574 — world blasts (incl. per-hit mine pops)
    private readonly List<ExportProbe> _probeExports = new();
    private readonly List<ProbeDestroyed> _probeDestroyeds = new();
    private readonly List<ExportBuildingEffect> _buildingEffectExports = new();
    private readonly List<WarpBomb> _warpBombs = new();
    private readonly List<FireExpendable> _fireExpendables = new();
    private readonly List<CreateChaff> _createChaffs = new();
    private readonly List<MissileSpoofed> _missileSpoofeds = new();
    private readonly List<EndSpoofing> _endSpoofings = new();
    private readonly List<AsteroidDestroyed> _asteroidDestroyeds = new();
    private readonly List<AsteroidMined> _asteroidMineds = new();
    private readonly List<OwnShipMiningUpdate> _ownShipMiningUpdates = new();   // P255
    private readonly List<OwnShipKillBonusUpdate> _ownShipKillBonusUpdates = new();   // P298
    private readonly List<StationDestroyed> _stationDestroyeds = new();
    private readonly List<StationCapture> _stationCaptures = new();
    private readonly List<GameState> _gameStates = new();
    private readonly List<GameOver> _gameOvers = new();
    private readonly List<GameOverPlayers> _gameOverPlayers = new();
    private readonly List<ScoreRosterUpdate> _scoreRosters = new();
    private readonly List<ClusterWarningUpdate> _clusterWarningUpdates = new();   // P374 — per-side sector-threat changes
    private readonly List<LeadIndicatorUpdate> _leadIndicatorUpdates = new();   // P266
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
    private readonly List<RequestMoney> _requestMoneys = new();
    private readonly List<BucketStatus> _bucketStatuses = new();
    private readonly List<CreateBuckets> _createBucketsRx = new();
    private readonly List<DevCompleted> _devCompleteds = new();       // P189
    private readonly List<SideTechChange> _sideTechChanges = new();   // P189
    private readonly List<SideAttributeChange> _sideAttributeChanges = new(); // P189
    private readonly List<SetBriefingText> _setBriefingTexts = new();
    private readonly List<RankInfo> _rankInfos = new();   // P130
    private readonly List<EnterGame> _enterGames = new(); // P141
    private readonly List<MissionStageMessage> _missionStages = new(); // P309 (MISSION_STAGE 37)
    private readonly List<TreasureSets> _treasureSetsRx = new();
    private readonly List<DestroyTreasure> _destroyTreasures = new();
    private readonly List<GainFlag> _gainFlags = new();            // P201
    private readonly List<BuildingEffectDestroyed> _buildingEffectDestroyed = new();
    private readonly List<JoinSide> _joinSides = new();                  // P183
    private readonly List<SideRoster> _sideRosters = new();              // P281
    private readonly List<SetTeamLeader> _setTeamLeaders = new();        // P183
    private readonly List<SetMissionOwner> _setMissionOwners = new();    // P183
    private readonly List<QuitMission> _quitMissions = new();            // P183
    private readonly List<QuitSide> _quitSides = new();                  // P183
    private readonly List<QueuedCommandUpdate> _queuedCommandUpdates = new();  // P424
    private readonly List<PositionReqBroadcast> _positionReqs = new();   // P184
    private readonly List<DelPositionReq> _delPositionReqs = new();      // P184
    private readonly List<ForceTeamReady> _forceTeamReadys = new();      // P184
    private readonly List<LockLobby> _lockLobbys = new();                // P186
    private readonly List<LockSides> _lockSidess = new();                // P186
    private readonly List<AutoAccept> _autoAccepts = new();              // P186
    private readonly List<ChangeTeamCiv> _changeTeamCivs = new();        // P186
    private readonly List<ChangeAlliances> _allianceBroadcasts = new();     // P190
    private readonly List<SideInactive> _sideInactives = new();             // P202
    private readonly List<Ballot> _ballots = new();                      // P187
    private readonly List<CancelBallot> _cancelBallots = new();          // P187
    private readonly List<SetStartTime> _setStartTimes = new();          // P188
    private readonly List<Ping> _pings = new();                          // P188
    private readonly List<PingData> _pingDatas = new();                  // P200
    private readonly List<BuyLoadoutAck> _buyLoadoutAcks = new();        // P473 — server answer to SendBuyLoadout
    private short _myShipId = -1;

    // P281 — a bot connecting into the post-connect side-select flow must send
    // a POSITIONREQ after logon to get seated (the server now defers spawn to
    // side selection). By default the bot auto-selects (POSITIONREQ with NA =
    // auto-pick arrival order, which reproduces the old round-robin/injected-
    // spawner seating), so the existing test corpus keeps working unchanged.
    private volatile bool _autoSelectSideAfterLogon = true;
    private bool _autoSideSelectSent;

    /// <summary>P281 — whether this bot auto-sends <c>POSITIONREQ(NA)</c> right
    /// after logon to get itself seated in the side-select flow. Set false when
    /// a test wants to observe the pending side-select state before choosing.</summary>
    public bool AutoSelectSideAfterLogon { get => _autoSelectSideAfterLogon; set => _autoSelectSideAfterLogon = value; }

    /// <summary>A SINGLE_SHIP_UPDATE observation of a ship, timestamped with the
    /// server tick index (the uint <c>Time</c> slot of the payload), plus the
    /// ship's carried ore fraction (<c>ServerShipUpdate.OreFraction</c>, byte 45
    /// of the 47-byte body) so a test can assert ore travelled wire-visible.</summary>
    public readonly record struct ShipObservation(short ShipId, uint Tick, Vector3 Position, Vector3 Velocity, Orientation Orientation, int StateM = 0, float OreFraction = 0.0f);

    public bool Connected => _transport?.IsConnected ?? false;

    public LogonAck? Ack { get { lock (_gate) return _ack; } }

    /// <summary>P590 — the per-peer pose cookie the server expects the next
    /// native SHIP_UPDATE to echo. Seeded from the last SET_CLUSTER's cookie
    /// and rotated by the EJECT message's fresh cookie (native NewCookie(),
    /// FedSrv.CPP:12019 — the real client's ClientWorld.Apply(Eject) consumes
    /// it and echoes it on its pod's updates). Without consuming the rotated
    /// cookie a pose-driving harness sends a stale cookie the P507 gate drops
    /// whole, freezing its pod at the eject point.</summary>
    public byte PoseCookie
    {
        get { lock (_gate) return _poseCookie; }
        set { lock (_gate) _poseCookie = value; }
    }

    public SetCluster? ShipAssignment
    {
        get
        {
            MaybeAutoSelectSide();
            lock (_gate) return _setCluster;
        }
    }

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

    /// <summary>P463c — all COLLISION_EVENT(216) messages this bot has received,
    /// in arrival order. Mirrors <see cref="Kills"/>: locked, snapshot-on-read,
    /// cleared via <see cref="ResetCollisionEvents"/>.</summary>
    public IReadOnlyList<Allegiance.Core.Wire.CollisionEvent> CollisionEvents { get { lock (_gate) return _collisionEvents.ToArray(); } }

    /// <summary>Clear the accumulated collision events (between engagements).</summary>
    public void ResetCollisionEvents() { lock (_gate) _collisionEvents.Clear(); }

    /// <summary>Clear the accumulated kills (between engagements).</summary>
    public void ResetKills() { lock (_gate) _kills.Clear(); }

    /// <summary>P507 — all SHIP_RESET(183) messages this bot has received, in
    /// arrival order (the server's pose-rejection reply, carrying the server's
    /// own pose + a fresh cookie). Mirrors <see cref="Kills"/>: locked,
    /// snapshot-on-read, cleared via <see cref="ResetShipResetsReceived"/>.</summary>
    public IReadOnlyList<ShipReset> ShipResets { get { lock (_gate) return _shipResets.ToArray(); } }

    /// <summary>Clear the accumulated SHIP_RESET messages (between scenarios).</summary>
    public void ResetShipResetsReceived() { lock (_gate) _shipResets.Clear(); }

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

    /// <summary>Captured <c>SIDE_ROSTER(211)</c> snapshots received (P281) — the
    /// per-side {id, civId, playerCount, name} table pushed to a peer at the
    /// post-connect side-select screen. Locked, snapshot-on-read, cleared via
    /// <see cref="ResetSideRosters"/>.</summary>
    public IReadOnlyList<SideRoster> SideRosters { get { lock (_gate) return _sideRosters.ToArray(); } }

    /// <summary>Clear the accumulated SIDE_ROSTER snapshots.</summary>
    public void ResetSideRosters() { lock (_gate) _sideRosters.Clear(); }

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

    /// <summary>Captured <c>QUEUED_COMMAND_UPDATE(215)</c> owner-only
    /// notifications received from the server (P424): the local player's own
    /// pending queued order was set or cleared. Accumulated so a live test can
    /// assert the owner peer really received its queued/cleared order state.
    /// Cleared via <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<QueuedCommandUpdate> QueuedCommandUpdates { get { lock (_gate) return _queuedCommandUpdates.ToArray(); } }

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
            _queuedCommandUpdates.Clear();
            _positionReqs.Clear();
            _delPositionReqs.Clear();
            _forceTeamReadys.Clear();
            _lockLobbys.Clear();
            _lockSidess.Clear();
            _autoAccepts.Clear();
            _changeTeamCivs.Clear();
            _allianceBroadcasts.Clear();
            _sideInactives.Clear();
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

    /// <summary>Captured <c>SIDE_INACTIVE(68)</c> relays received from the
    /// server (P202): the mission owner's pre-game empty-side active/inactive
    /// toggle, forwarded whole-mission (with the server-filled
    /// <c>bChangeAET</c>/<c>bAET</c> reply fields). Named
    /// <c>SideInactiveBroadcasts</c> — not <c>SideInactives</c> — so the
    /// property does not shadow its own element type (<see cref="SideInactive"/>)
    /// inside the receive switch (the P186 CS1061 pitfall). Cleared via
    /// <see cref="ResetLeadershipSignals"/>.</summary>
    public IReadOnlyList<SideInactive> SideInactiveBroadcasts { get { lock (_gate) return _sideInactives.ToArray(); } }

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

    /// <summary>Captured <c>PINGDATA(194)</c> latency-roster entries received
    /// FROM the server (P200) — every per-player entry plus the terminator /
    /// unchanged-window marker (sentinel <c>ShipId == -1</c>), in arrival
    /// order. Cleared via <see cref="ResetPingDataReceived"/>.</summary>
    public IReadOnlyList<PingData> PingDatas { get { lock (_gate) return _pingDatas.ToArray(); } }

    /// <summary>Clear the P200 <see cref="PingDatas"/> accumulator (between
    /// REQPINGDATA exchanges).</summary>
    public void ResetPingDataReceived() { lock (_gate) _pingDatas.Clear(); }

    /// <summary>Every <c>FM_S_BUY_LOADOUT_ACK(5)</c> received so far, in arrival
    /// order — the server's answer to a <see cref="SendBuyLoadout"/>. P473: a
    /// <see cref="Bot"/>-based tool can now see a refusal or partial grant, not
    /// just fire-and-forget. Mirrors <see cref="PingDatas"/>: locked,
    /// snapshot-on-read, cleared via <see cref="ResetBuyLoadoutAcks"/>.</summary>
    public IReadOnlyList<BuyLoadoutAck> BuyLoadoutAcks { get { lock (_gate) return _buyLoadoutAcks.ToArray(); } }

    /// <summary>Clear the accumulated BUY_LOADOUT_ACKs (between purchases).</summary>
    public void ResetBuyLoadoutAcks() { lock (_gate) _buyLoadoutAcks.Clear(); }

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

    /// <summary>P480 — all <c>FM_S_EXPORT(26)/OT_cluster</c> cluster exports
    /// received so far, in arrival order. Accumulated so a live rotation test can
    /// assert a re-joined peer really re-received the NEW mission's clusters.
    /// Locked, snapshot-on-read, cleared via <see cref="ResetWorldExports"/>.</summary>
    public IReadOnlyList<ExportCluster> ClusterExports { get { lock (_gate) return _clusterExports.ToArray(); } }

    /// <summary>P480 — all <c>FM_S_EXPORT(26)/OT_warp</c> warp exports received,
    /// in arrival order. See <see cref="ClusterExports"/>.</summary>
    public IReadOnlyList<ExportWarp> WarpExports { get { lock (_gate) return _warpExports.ToArray(); } }

    /// <summary>P480 — all <c>FM_S_EXPORT(26)/OT_station</c> station exports
    /// received, in arrival order. See <see cref="ClusterExports"/>.</summary>
    public IReadOnlyList<ExportStation> StationExports { get { lock (_gate) return _stationExports.ToArray(); } }

    /// <summary>P480 — clear the accumulated cluster/warp/station world exports
    /// (between joins/phases; the asteroid exports have their own reset above).</summary>
    public void ResetWorldExports() { lock (_gate) { _clusterExports.Clear(); _warpExports.Clear(); _stationExports.Clear(); } }

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

    /// <summary>All <c>EXPLOSION_EVENT(207)</c> blasts received so far, in arrival
    /// order — P574: every world explosion the server broadcasts (incl. the
    /// per-hit <c>c_etMine</c> mine pops at P2 and the ship/station blasts). Same
    /// test/observer instrumentation as <see cref="MineDestroyeds"/>.</summary>
    public IReadOnlyList<ExplosionEvent> ExplosionEvents { get { lock (_gate) return _explosionEvents.ToArray(); } }

    /// <summary>Clear the accumulated explosion events (between joins/phases).</summary>
    public void ResetExplosionEvents() { lock (_gate) _explosionEvents.Clear(); }

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

    /// <summary>All <c>FM_S_EXPORT(26)/OT_buildingEffect</c> messages received so
    /// far, in arrival order — P294: every real building-effect-created
    /// broadcast the server sends (a Builder committed to constructing a station
    /// at an asteroid). Same test/observer instrumentation as
    /// <see cref="ProbeExports"/>.</summary>
    public IReadOnlyList<ExportBuildingEffect> BuildingEffectExports { get { lock (_gate) return _buildingEffectExports.ToArray(); } }

    /// <summary>Clear the accumulated building-effect exports (between joins/phases).</summary>
    public void ResetBuildingEffectExports() { lock (_gate) _buildingEffectExports.Clear(); }

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

    /// <summary>P255 — the owner-only <c>FM_S_OWN_SHIP_MINING_UPDATE(206)</c>
    /// messages this bot has seen (one per tick), each naming the rock its own
    /// ship is currently drilling (NA/NA when not mining).</summary>
    public IReadOnlyList<OwnShipMiningUpdate> OwnShipMiningUpdates { get { lock (_gate) return _ownShipMiningUpdates.ToArray(); } }

    /// <summary>P255 — clear the accumulated own-ship mining-target updates.</summary>
    public void ResetOwnShipMining() { lock (_gate) _ownShipMiningUpdates.Clear(); }

    /// <summary>P298 — the owner-only <c>FM_S_OWN_SHIP_KILL_BONUS_UPDATE(212)</c>
    /// messages this bot has seen (one per tick), each carrying its own ship's
    /// live Kill Bonus readout and streak.</summary>
    public IReadOnlyList<OwnShipKillBonusUpdate> OwnShipKillBonusUpdates { get { lock (_gate) return _ownShipKillBonusUpdates.ToArray(); } }

    /// <summary>P298 — clear the accumulated own-ship Kill Bonus updates.</summary>
    public void ResetOwnShipKillBonus() { lock (_gate) _ownShipKillBonusUpdates.Clear(); }

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

    /// <summary>The <c>ScoreRosterUpdate(209)</c> broadcasts this bot has seen
    /// (P265) — a <see cref="Wire.ScoreRosterUpdate"/> per received periodic
    /// message, appended on the receiver thread under <see cref="_gate"/> like
    /// <see cref="GameOverPlayerBroadcasts"/>. Each carries the real in-mission
    /// roster (name/side/kills/deaths) for the bot's mission. Named differently
    /// from the <see cref="Wire.ScoreRosterUpdate"/> type it holds so the
    /// property doesn't shadow the type (the P186 shadowing pitfall). Cleared by
    /// <see cref="ResetScoreRosterBroadcasts"/>.</summary>
    public IReadOnlyList<ScoreRosterUpdate> ScoreRosterBroadcasts { get { lock (_gate) return _scoreRosters.ToArray(); } }
    public void ResetScoreRosterBroadcasts() { lock (_gate) _scoreRosters.Clear(); }
    /// <summary>The <c>ClusterWarningUpdate(214)</c> broadcasts this bot has seen
    /// (P374) — a <see cref="Wire.ClusterWarningUpdate"/> per received per-side
    /// sector-threat change (warning level and/or secured/lost transitions),
    /// appended on the receiver thread under <see cref="_gate"/> like the other
    /// accumulators. Named <c>ClusterWarningBroadcasts</c> (not after the type)
    /// to avoid the P186 shadowing pitfall. Cleared by
    /// <see cref="ResetClusterWarningBroadcasts"/>.</summary>
    public IReadOnlyList<ClusterWarningUpdate> ClusterWarningBroadcasts { get { lock (_gate) return _clusterWarningUpdates.ToArray(); } }
    public void ResetClusterWarningBroadcasts() { lock (_gate) _clusterWarningUpdates.Clear(); }

    /// <summary>The <c>LEAD_INDICATOR_UPDATE(210)</c> owner-only lead-aim snapshots
    /// this bot has seen (P266) — a <see cref="Wire.LeadIndicatorUpdate"/> per
    /// received per-tick delivery. Named <c>LeadIndicatorBroadcasts</c> (not
    /// after the type) so the property doesn't shadow the type (the P186 shadowing
    /// pitfall). Cleared by <see cref="ResetLeadIndicatorBroadcasts"/>.</summary>
    public IReadOnlyList<LeadIndicatorUpdate> LeadIndicatorBroadcasts { get { lock (_gate) return _leadIndicatorUpdates.ToArray(); } }
    public void ResetLeadIndicatorBroadcasts() { lock (_gate) _leadIndicatorUpdates.Clear(); }

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

    /// <summary>P590 — <c>FM_S_EJECT(77)</c> frames received (incl. this bot's own).</summary>
    public IReadOnlyList<Eject> Ejects { get { lock (_gate) return _ejects.ToArray(); } }

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

    /// <summary>The <c>FM_CS_REQUEST_MONEY</c>(182) frames received since connect
    /// (P199): requests the server forwarded to this peer because it holds the
    /// team's money. Locked, snapshot-on-read, cleared via
    /// <see cref="ResetRequestMoneys"/>.</summary>
    public IReadOnlyList<RequestMoney> RequestMoneys { get { lock (_gate) return _requestMoneys.ToArray(); } }
    public void ResetRequestMoneys() { lock (_gate) _requestMoneys.Clear(); }

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

    /// <summary>Captured <c>MISSION_STAGE(37)</c> messages received from the
    /// server (P309), in arrival order — each carries the mission's stage at
    /// that moment (the join-burst real stage, then the whole-mission broadcasts
    /// on the Starting/Started transitions). Snapshot-on-read, cleared via
    /// <see cref="ResetMissionStages"/>.</summary>
    public IReadOnlyList<MissionStageMessage> MissionStages { get { lock (_gate) return _missionStages.ToArray(); } }
    public void ResetMissionStages() { lock (_gate) _missionStages.Clear(); }

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

    /// <summary>Captured <c>GAIN_FLAG(156)</c> messages received from the server
    /// (P201) — the whole-mission flag-capture announcement (a pickup or a
    /// secure), plus the join-sync per-carrier sync. Snapshot-on-read, cleared
    /// via <see cref="ResetGainFlags"/>.</summary>
    public IReadOnlyList<GainFlag> GainFlags { get { lock (_gate) return _gainFlags.ToArray(); } }
    public void ResetGainFlags() { lock (_gate) _gainFlags.Clear(); }

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
    /// (P396 also wires <see cref="ChatTarget.Leaders"/> and
    /// <see cref="ChatTarget.Wing"/>; the remainder are relayed to nobody by
    /// the server, which logs them — see <c>Server.cs::SelectChatTargets</c>).
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

    /// <summary>Send a real <c>FM_C_PLAYER_DONATE(67)</c> (P203) — a manual,
    /// one-shot, direct player-to-player gift of <paramref name="moneyGiven"/>
    /// to the ship <paramref name="shipId"/> (same-side or allied-side only;
    /// the server enforces the wallet/eligibility rules and broadcasts
    /// MONEY_CHANGE accordingly).</summary>
    public void SendPlayerDonate(short shipId, int moneyGiven)
    {
        var msg = new PlayerDonate { ShipID = shipId, MoneyGiven = moneyGiven };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_CS_REQUEST_MONEY(182)</c> (P199) — the client's
    /// "please send me money" request. The server fills in <c>shipidRequest</c>,
    /// so the bot leaves it as a harmless <c>0</c> sentinel; <c>hidFor</c> is the
    /// optional hull the money is for (decode-only, forwarded verbatim).</summary>
    public void SendRequestMoney(int amount, short hidFor)
    {
        var msg = new RequestMoney { Amount = amount, ShipIdRequest = 0, HidFor = hidFor };
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

    /// <summary>P281 — if this bot has logged on (got a validated LOGONACK) but
    /// has not yet asked the server for a side, send <c>POSITIONREQ(NA)</c>
    /// (auto-pick arrival order). Called lazily from
    /// <see cref="ShipAssignment"/> / <see cref="WaitForShipAssignment"/> so the
    /// send happens on a poll (never inside the receive-thread lock), preserving
    /// the existing test pattern of connecting a bot and waiting for its seat.
    /// Disable via <see cref="AutoSelectSideAfterLogon"/> to hold the bot at the
    /// side-select screen.</summary>
    private void MaybeAutoSelectSide()
    {
        if (!_autoSelectSideAfterLogon || _autoSideSelectSent) return;
        var ack = Ack;
        if (ack is not { FValidated: true }) return;
        _autoSideSelectSent = true;
        SendPositionReq(-1);   // NA = auto-pick arrival order
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
    /// Send a real <c>FM_C_REQPINGDATA(195)</c> (P200) — the client's request
    /// for every connected player's latency roster. A header-only, zero-payload
    /// marker (Messages.h:1012-1013). The server answers with one
    /// <c>PINGDATA(194)</c> per live player (whole server, not mission-scoped)
    /// plus a terminator — sent only to this connection — unless this connection
    /// is inside the 5-second debounce window, in which case only the
    /// unchanged-window marker is sent.
    /// </summary>
    public void SendReqPingData()
    {
        Send(FedPacket.Pack(new[] { new ReqPingData().Encode() }));
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
    /// Send a real <c>FM_CS_SIDE_INACTIVE(68)</c> — the mission owner's
    /// pre-game request to mark an empty side <paramref name="sideId"/>
    /// active (<c>bActive == true</c>) or inactive (<c>false</c>) (P202).
    /// The two AET fields are left unset: the client's request carries only
    /// <c>{sideID, bActive}</c>; the server fills in <c>bChangeAET</c>/<c>bAET</c>
    /// on its forwarded reply (<c>FedSrv.CPP:7385-7414</c>).
    /// </summary>
    public void SendSideInactive(short sideId, bool bActive)
    {
        var msg = new SideInactive { SideId = sideId, BActive = bActive };
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
    /// P420 — send a real <c>FM_CS_FIRE_MISSILE(70)</c> launch request, the
    /// bot mirror of <see cref="SendFireExpendable"/> and of the richer
    /// client's <c>ConnectionSession.SendFireMissile</c>. The server is
    /// authoritative and ignores the client-supplied ids / missile type /
    /// target ("ignore the IDs provided by the client", FedSrv.CPP:5444):
    /// <see cref="Server.Server.ShipCommands.HandleFireMissile"/> resolves the
    /// launcher's own mounted missile type and fires against the ship's
    /// server-side <see cref="Ship.CommandTarget"/> (the lock target
    /// <see cref="Ship.UpdateMissileLock"/> builds each tick) via the shared
    /// <see cref="Ship.TryFireMissile"/> gate — which requires a mounted
    /// <c>ET_Magazine</c>@0 launcher with ammo <em>and</em> lock sufficiency.
    /// So the sender's only job is to carry that real launcher in its loadout,
    /// hold a real cross-side <see cref="Ship.CommandTarget"/>, and request
    /// periodically; the lock-rebuild is the server's own fire-rate limiter.
    /// This sends the same minimal syntactically-valid request
    /// <c>ConnectionSession.SendFireMissile</c> does (one launch-data entry,
    /// <c>TargetId = NA</c>) and lets the server govern the launch.
    /// </summary>
    public void SendFireMissile()
    {
        var msg = new FireMissile
        {
            Lock = 0f,                               // server-computed lock governs
            TimeFired = 0u,                          // server-authoritative time
            MissileTypeId = 0,                       // server overrides from mounted launcher
            LauncherId = 0,                          // server overrides
            ClusterId = 0,                           // server overrides
            TargetType = 0, TargetId = -1,           // NA — server uses CommandTarget
            BDud = false,
            Entries =
            [
                new MissileLaunchData(Vector3.Zero, Vector3.Zero, Vector3.Zero, 0),
            ],
        };
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

    /// <summary>Connect to a hosting server and register our name. Defaults to
    /// loopback (every existing caller's behavior, unchanged) — pass
    /// <paramref name="host"/> to reach a server elsewhere on the network (see
    /// the identical fix/rationale on ConnectionSession.Connect, 2026-09-15).</summary>
    public void Connect(ushort port, string playerName, string host = "127.0.0.1")
    {
        _transport = new TcpTransport(this);
        _transport.Join(host, port, playerName);
        StartKeepAlive();
    }

    /// <summary>P671 — (re)arm the native keep-alive thread for this bot.
    /// Idempotent: resets the ping bookkeeping and starts the thread if it is
    /// not already running (it may have exited after a detected loss). The
    /// thread is first started from the constructor, so this is a no-op on the
    /// common path.</summary>
    private void StartKeepAlive()
    {
        lock (_gate)
        {
            _unansweredPings = 0;
            _lastServerMessageTick = Environment.TickCount64;
            _keepAlivePingsSent = 0;
        }
        if (_keepAliveThread != null) return;          // already running
        Volatile.Write(ref _keepAliveStop, 0);
        _keepAliveGate.Reset();
        var t = new Thread(KeepAliveLoop) { IsBackground = true, Name = "bot-keepalive" };
        _keepAliveThread = t;
        t.Start();
    }

    /// <summary>P671 — send CS_PING every 6 s while connected (native
    /// CheckServerLag, clintlib.cpp:2050-2087) and stop when the link is judged
    /// dead (>5 unanswered pings AND >= 30 s since any server message). PERSISTENT:
    /// it survives a not-yet-connected window and a reconnect (skipping ticks
    /// while disconnected), so it works for every connection path, including the
    /// MCP harness's direct <c>Transport.Join</c>. Exits only on dispose or a
    /// detected loss; on exit it clears <c>_keepAliveThread</c> for re-arm.</summary>
    private void KeepAliveLoop()
    {
        try
        {
            while (true)
            {
                if (_keepAliveGate.Wait(KeepAliveIntervalMs)) return;   // stop requested
                if (Volatile.Read(ref _keepAliveStop) != 0) return;
                bool lostNow = false;
                lock (_gate)
                {
                    if (_lost) return;
                    if (_transport is { IsConnected: true })
                    {
                        SendPing(0u);          // FM_CS_PING keepalive
                        _unansweredPings++;
                        _keepAlivePingsSent++;
                        if (_unansweredPings > PingsTimeout
                            && Environment.TickCount64 - _lastServerMessageTick >= DeadTimeMs)
                            lostNow = true;
                    }
                }
                if (lostNow)
                {
                    // Link judged dead (native rule) — stop pinging; the transport's
                    // own read loop will surface the eventual EOF.
                    _lost = true;
                    return;
                }
            }
        }
        finally
        {
            _keepAliveThread = null;   // allow re-arm after loss / dispose
        }
    }

    /// <summary>P671 — stop the keep-alive thread on shutdown/reconnect.</summary>
    private void StopKeepAlive()
    {
        Volatile.Write(ref _keepAliveStop, 1);
        _keepAliveGate.Set();
        var t = _keepAliveThread;
        _keepAliveThread = null;
        try { t?.Join(1000); } catch { /* best effort */ }
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

    /// <summary>P507 — send a NATIVE-style <c>FM_C_SHIP_UPDATE(24)</c> carrying the
    /// client's reported pose (server time + position + velocity + orientation +
    /// turn rates) and the given <paramref name="cookie"/> — exactly what a P508
    /// client sends, and what the server's accept/reject pipeline consumes.
    /// Contrast <see cref="CommandControls"/>, which sends the LEGACY
    /// Time=0 / zero-pose form the server detects and treats controls-only.</summary>
    public void SendOwnShipPose(uint time, byte cookie, Vector3 position, Vector3 velocity,
        Orientation orientation, ControlData controls, int stateM, float[] turnRates, float power = 1.0f)
    {
        var msg = new ShipUpdate
        {
            Time = time,
            Cookie = cookie,
            Payload = ClientShipUpdate.FromPose(time, position, velocity, orientation, stateM, controls, turnRates, power),
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }
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
            // P671 — any inbound server traffic proves the link is alive: refresh
            // the last-server-message timestamp and clear the unanswered-ping
            // count (native appmsg.cpp:25-46 / clintlib.cpp:405).
            _lastServerMessageTick = Environment.TickCount64;
            _unansweredPings = 0;

            foreach (var m in messages)
            {
                ushort fmid;
                try { fmid = FedMessageHeader.Read(m).Fmid; }
                catch { continue; }

                switch (fmid)
                {
                    case LogonAck.Fmid:
                        _ack = LogonAck.Decode(m);
                        // P281 — the server defers spawn to a post-connect
                        // side-select step, so a bot must request a side to get
                        // seated. Auto-send POSITIONREQ(NA) (auto-pick arrival
                        // order) the moment logon succeeds, so the existing test
                        // corpus seats without needing to poll ShipAssignment.
                        // Sent from the receive thread (the transport's Send is
                        // safe here — OnReceive runs outside its gate).
                        if (_autoSelectSideAfterLogon && !_autoSideSelectSent && _ack is { FValidated: true })
                        {
                            _autoSideSelectSent = true;
                            SendPositionReq(-1);
                        }
                        break;
                    case SetCluster.Fmid:
                        _setCluster = SetCluster.Decode(m);
                        _assignments.Add(_setCluster);
                        _myShipId = _setCluster.Ship.ShipId;
                        _poseCookie = _setCluster.Cookie;   // P590 — seed pose cookie
                        break;
                    case Eject.Fmid:
                        var ej = Eject.Decode(m);
                        // P590 — the real client consumes the EJECT cookie
                        // (ClientWorld.Apply: `if (e.ShipId == MyShipId)`),
                        // echoing it on its pod's SHIP_UPDATEs. Track it so a
                        // pose-driving harness keeps flying the pod after eject.
                        if (ej.ShipId == _myShipId) _poseCookie = ej.Cookie;
                        _ejects.Add(ej);
                        break;
                    case ShipReset.Fmid:
                        _shipResets.Add(ShipReset.Decode(m));
                        break;
                    case SingleShipUpdate.Fmid:
                        var u = SingleShipUpdate.Decode(m);
                        var obs = new ShipObservation(
                            u.OidTarget, u.Ship.Time, u.Ship.Position, u.Ship.Velocity, u.Ship.Orientation, u.Ship.StateM, u.Ship.OreFraction);
                        _anyShipObservations.Add(obs);
                        if (_myShipId >= 0 && u.OidTarget == _myShipId)
                            _observations.Add(obs);
                        break;
                    case KillShip.Fmid:
                        _kills.Add(KillShip.Decode(m));
                        break;
                    case Allegiance.Core.Wire.CollisionEvent.Fmid:
                        // P463c — a collision was resolved; the peer records it.
                        _collisionEvents.Add(Allegiance.Core.Wire.CollisionEvent.Decode(m));
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
                        if (RetainRawCombatUpdates) _projectileInstances.Add(ProjectileInstance.Decode(m));
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
                    case ExplosionEvent.Fmid:
                        // P574 — a world explosion broadcast (incl. per-hit mine pops).
                        _explosionEvents.Add(ExplosionEvent.Decode(m));
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
                    case OwnShipMiningUpdate.Fmid:
                        // P255 — owner-only: the rock this bot's own ship is
                        // currently drilling (NA/NA when not mining).
                        _ownShipMiningUpdates.Add(OwnShipMiningUpdate.Decode(m));
                        break;
                    case OwnShipKillBonusUpdate.Fmid:
                        // P298 — owner-only: this bot's own ship's live Kill
                        // Bonus readout + streak (per-tick stream).
                        _ownShipKillBonusUpdates.Add(OwnShipKillBonusUpdate.Decode(m));
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
                    case ScoreRosterUpdate.Fmid:
                        _scoreRosters.Add(ScoreRosterUpdate.Decode(m));
                        break;
                    case ClusterWarningUpdate.Fmid:
                        _clusterWarningUpdates.Add(ClusterWarningUpdate.Decode(m));
                        break;
                    case LeadIndicatorUpdate.Fmid:
                        _leadIndicatorUpdates.Add(LeadIndicatorUpdate.Decode(m));
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
                        if (RetainRawCombatUpdates) _heavyShipsUpdates.Add(HeavyShipsUpdate.Decode(m));
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
                    case RequestMoney.Fmid:
                        _requestMoneys.Add(RequestMoney.Decode(m));
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
                    case MissionStageMessage.Fmid:
                        _missionStages.Add(MissionStageMessage.Decode(m));
                        break;
                    case JoinSide.Fmid:
                        _joinSides.Add(JoinSide.Decode(m));
                        break;
                    case SetTeamLeader.Fmid:
                        _setTeamLeaders.Add(SetTeamLeader.Decode(m));
                        break;
                    case SideRoster.Fmid:
                        _sideRosters.Add(SideRoster.Decode(m));
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
                    case QueuedCommandUpdate.Fmid:
                        // P424 — the owner-only "you have a pending queued order"
                        // notification; accumulated so tests can assert the owner
                        // peer really received its queued/cleared order state.
                        _queuedCommandUpdates.Add(QueuedCommandUpdate.Decode(m));
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
                    case PingData.Fmid:
                        _pingDatas.Add(PingData.Decode(m));
                        break;
                    case BuyLoadoutAck.Fmid:
                        // P473 — decode the server's BUY_LOADOUT_ACK(5) answer
                        // to SendBuyLoadout, mirroring ConnectionSession
                        // (ConnectionSession.cs:641-642): a Bot-based tool can
                        // now compare the granted hull/parts with the requested
                        // ones and detect a refusal or partial grant instead of
                        // silently flying a default ship.
                        _buyLoadoutAcks.Add(BuyLoadoutAck.Decode(m));
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
                    case SideInactive.Fmid:
                        _sideInactives.Add(SideInactive.Decode(m));
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
                    case GainFlag.Fmid:
                        _gainFlags.Add(GainFlag.Decode(m));
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
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportCluster.OtCluster)
                            _clusterExports.Add(ExportCluster.Decode(m));
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportWarp.OtWarp)
                            _warpExports.Add(ExportWarp.Decode(m));
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportStation.OtStation)
                            _stationExports.Add(ExportStation.Decode(m));
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportMine.OtMine)
                            _mineExports.Add(ExportMine.Decode(m));
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportProbe.OtProbe)
                            _probeExports.Add(ExportProbe.Decode(m));
                        else if (m.Length >= 12 && BitConverter.ToInt32(m.AsSpan(8, 4)) == ExportBuildingEffect.OtBuildingEffect)
                            _buildingEffectExports.Add(ExportBuildingEffect.Decode(m));
                        break;
                }
            }
        }
    }

    /// <summary>Close the client socket. The server sees EOF / a dropped peer.</summary>
    public void Shutdown() => _transport?.Shutdown();

    public void Dispose()
    {
        StopKeepAlive();
        Shutdown();
    }
}
