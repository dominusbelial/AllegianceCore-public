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
    private readonly List<ShipStatusMessage> _shipStatuses = new();
    private readonly List<ClusterInfo> _clusterInfos = new();
    private readonly List<Payday> _paydays = new();
    private readonly List<DockedNotify> _dockedNotices = new();
    private readonly List<TeleportAck> _teleportAcks = new();
    private readonly List<PlayerReady> _playerReadyEchoes = new();
    private readonly List<ExportShip> _exports = new();
    private readonly List<ExportAsteroid> _asteroidExports = new();
    private readonly List<PlayerInfo> _playerInfos = new();
    private readonly List<ProjectileInstance> _projectileInstances = new();
    private readonly List<FireMissile> _fireMissiles = new();
    private readonly List<MissileDestroyed> _missileDestroyeds = new();
    private readonly List<ExportMine> _mineExports = new();
    private readonly List<MineDestroyed> _mineDestroyeds = new();
    private readonly List<ExportProbe> _probeExports = new();
    private readonly List<ProbeDestroyed> _probeDestroyeds = new();
    private readonly List<WarpBomb> _warpBombs = new();
    private readonly List<AsteroidDestroyed> _asteroidDestroyeds = new();
    private readonly List<AsteroidMined> _asteroidMineds = new();
    private readonly List<RipcordActivate> _ripcordActivates = new();
    private readonly List<RipcordDenied> _ripcordDenieds = new();
    private readonly List<RipcordAborted> _ripcordAborteds = new();
    private readonly List<PlayerRescued> _playerRescueds = new();
    private readonly List<RelaunchShip> _relaunchShips = new();
    private readonly List<MoneyChange> _moneyChanges = new();
    private readonly List<SetMoney> _setMoneys = new();
    private readonly List<BucketStatus> _bucketStatuses = new();
    private readonly List<CreateBuckets> _createBucketsRx = new();
    private readonly List<SetBriefingText> _setBriefingTexts = new();
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

    /// <summary>Captured <c>SET_BRIEFING_TEXT(9)</c> messages received from the
    /// server (P121). Snapshot-on-read, cleared via
    /// <see cref="ResetSetBriefingTexts"/>.</summary>
    public IReadOnlyList<SetBriefingText> SetBriefingTexts { get { lock (_gate) return _setBriefingTexts.ToArray(); } }
    public void ResetSetBriefingTexts() { lock (_gate) _setBriefingTexts.Clear(); }

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
                    case BucketStatus.Fmid:
                        _bucketStatuses.Add(BucketStatus.Decode(m));
                        break;
                    case CreateBuckets.Fmid:
                        _createBucketsRx.Add(CreateBuckets.Decode(m));
                        break;
                    case SetBriefingText.Fmid:
                        _setBriefingTexts.Add(SetBriefingText.Decode(m));
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
