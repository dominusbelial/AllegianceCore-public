using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// The headless server — the "Main()" this port was pointed at. It boots a
/// static core, instantiates every configured game world as a live
/// <see cref="Mission"/>, drives them at a fixed cadence, and accepts TCP
/// clients through the logon handshake. Roughly the section 1..10 boot path of
/// BOOT_PATH.md minus the dropped pieces (see README "What the server does and
/// does not do yet").
/// </summary>
/// <remarks>
/// <para><b>Clock:</b> two clocks, deliberately kept apart. The <em>simulation</em>
/// clock <c>_simNow</c> advances by exactly <c>period = 1/ups</c> per tick —
/// deterministic and strictly monotonic, so it can never trip
/// <c>Mission.Update</c>'s backwards-time or &gt;=600s guards. The <em>cadence</em>
/// clock is a <see cref="Stopwatch"/> (monotonic, not wall time) that measures
/// the real cost of a pass to decide how long to sleep and whether to log an
/// overrun. Wall-clock jumps therefore cannot corrupt the simulation or the
/// cadence accounting.</para>
///
/// <para><b>Teardown:</b> reverse of boot. Stop the loop, stop the transport
/// (which joins its background accept/read threads — no orphans), then release
/// the one site. Signal-driven via <see cref="RunAsync"/>.</para>
/// </remarks>
public sealed class Server : IDisposable
{
    private ServerConfig _config = null!;
    private ServerHost _host = null!;
    private List<Mission> _missions = null!;
    private double _period;
    private double _simNow;

    private long _loopIterations;
    private long _overruns;
    private long _connectionsJoined;
    private readonly object _statsLock = new();

    // Source of randomness for the eject branch's random fallback heading
    // (Vector::RandomDirection, vector.cpp:37-49). Shared, never seeded by the
    // caller — non-deterministic directions are fine (the native is too).
    private static readonly Random _rand = new();

    // Ships assigned to peers on validated logon (step 3). This map is owned
    // exclusively by the simulation thread (written in ProcessDatagram, which
    // Drain runs on the sim thread, and read in the broadcast step of SimPass),
    // so it needs no locking of its own.
    private readonly Dictionary<ConnectionId, LiveShip> _peerShips = new();
    private int _nextShipId = 8096;

    // Each connected peer's own character name, remembered past the logon
    // handshake. `req.CharacterName` (LogonRequest) is otherwise a local
    // variable consumed inline at the logon handler and then discarded, and
    // neither LiveShip nor Ship carries a name field — yet ExportShip's
    // CharacterName (the field that becomes the client-side ship's name) is
    // required when broadcasting a teammate's ship export. Populated at the
    // same point `_peerShips` is (validated logon), removed on disconnect.
    // Sim-thread only, like its sibling maps. P62.
    private readonly Dictionary<ConnectionId, string> _peerNames = new();

    // One-shot tracking for the P62 teammate-ship EXPORT broadcast: set once
    // the first time a (recipient, subject-ship) pair's real ExportShip has
    // been sent. Deliberately a HashSet (no cadence/time component) rather
    // than the time-windowed dictionaries beside it — a second CreateObject
    // for an id the client already owns could misbehave, so a repeat send is
    // wrong here, not merely wasteful. Sim-thread only, like the siblings.
    private readonly HashSet<(ConnectionId Recipient, short ShipId)> _teammateExportSent = new();

    // One-shot tracking for the P63 teammate-ship PLAYERINFO(10) broadcast:
    // set once the first time a (recipient, subject-ship) pair's real
    // PlayerInfo has been sent. Kept separate from `_teammateExportSent` —
    // these are two different messages with two different one-time gates, even
    // though they fire back-to-back for a given pair (EXPORT then PLAYERINFO).
    // Same reasoning as `_teammateExportSent`: a second PLAYERINFO would hit
    // CreateObject again on the same id, so a repeat is wrong, not merely
    // wasteful. Sim-thread only, like the siblings.
    private readonly HashSet<(ConnectionId Recipient, short ShipId)> _teammatePlayerInfoSent = new();

    // Loadout-dirty tracking for the teammate ExportShip(26) PartData[] tail:
    // the snapshot of PartData last sent to a given (recipient, subject-ship)
    // pair, so BroadcastShipStates can detect when a teammate's mounted parts
    // changed since the last EXPORT and resend a fresh one — unlike
    // `_teammateExportSent`/`_teammatePlayerInfoSent` this is NOT one-shot:
    // a teammate can re-buy a loadout any number of times after the initial
    // export. Sim-thread only, like the siblings above.
    private readonly Dictionary<(ConnectionId Recipient, short ShipId), IReadOnlyList<PartData>> _teammateLoadoutSent = new();

    // Last SHIP_STATUS(90) sector reported per peer, so the sector-change hook
    // in BroadcastShipStates resends only when a ship actually crossed a warp.
    // Written/read only on the sim thread (like _peerShips), no locking needed.
    private readonly Dictionary<ConnectionId, short> _lastStatusSector = new();
    // Last SHIP_STATUS(90) send tick per peer, for the periodic refresh in
    // BroadcastShipStates. Sim-thread only.
    private readonly Dictionary<ConnectionId, long> _lastStatusTick = new();

    // Last CLUSTERINFO(200) send tick per peer, for the periodic refresh in
    // BroadcastShipStates -- see ClusterInfoRefreshTicks for why this exists.
    // Sim-thread only.
    private readonly Dictionary<ConnectionId, long> _lastClusterInfoTick = new();

    // Last same-side teammate SHIP_STATUS(90) send tick per (recipient,
    // subject-ship) pair, for the periodic teammate broadcast in
    // BroadcastShipStates -- see TeammateStatusRefreshTicks for the cadence.
    // Sim-thread only, like the siblings above.
    private readonly Dictionary<(ConnectionId, short), long> _lastTeammateStatusTick = new();

    // Mission-wide CLUSTERINFO payload, encoded once (identical for every
    // peer, doesn't change during a mission) rather than re-encoded on every
    // periodic resend.
    private byte[]? _clusterInfoBytes;

    // Optional live-wire capture (--capture path). Null when off, so the only
    // overhead when disabled is a null check — capture must never change
    // behaviour or timing. Serialized across the transport (receive) and sim
    // (send) threads with a dedicated lock so a frame is never interleaved.
    private FileStream? _captureStream;
    private readonly object _captureLock = new();

    /// <summary>
    /// Raised once per notable gameplay moment (dock, undock, mine-start,
    /// mine-sell, payday, warp crossing) on the simulation thread. The payload
    /// is the same human-readable line already written to the console at the
    /// originating site (via <see cref="ServerHost.Notable"/>, which the sim
    /// feeds through <see cref="Allegiance.Core.Sim.IIgcSite.ReportNotable(string)"/>),
    /// never reformatted — so an observer (e.g. the vizfight spectator event
    /// feed) sees exactly what the server log shows. Raised inside the tick on
    /// the sim thread (like the <see cref="ServerHost.ShipKilled"/> hook), so a
    /// subscriber can reach the peer map without a lock. Null-safe: no
    /// subscribers, nothing happens.
    /// </summary>
    public event Action<string>? Notable;

    /// <summary>The position-change threshold (world units) that marks a ship
    /// dirty and worth a SINGLE_SHIP_UPDATE broadcast after a tick.</summary>
    private const float DirtyEpsilon = 1e-3f;

    /// <summary>How far past <see cref="Sector.DockRadius"/> an undocking ship
    /// gets repositioned, so it clears the sphere test
    /// (<see cref="Sector"/>'s dock-collision pass) before the next tick can
    /// re-latch it. Arbitrary but generous relative to <c>DirtyEpsilon</c>-scale
    /// position noise; see the undock handler for the full reasoning.</summary>
    private const float LaunchClearMargin = 50f;

    /// <summary>Outward speed (world units/sec) given to a ship on undock —
    /// the native reference's own default <c>c_fExitStationSpeed</c>
    /// (<c>missionigc.cpp:2689</c>), reused verbatim. Ensures fast initial
    /// separation from the station; <see cref="Sector.LaunchCooldownSeconds"/>
    /// (not this alone) is what actually guarantees no re-dock — see the
    /// undock handler's own P75 comment for why both are needed.</summary>
    private const float ExitStationSpeed = 50f;

    /// <summary>A peer-owned ship: the <see cref="Ship"/>, the
    /// <see cref="Sector"/> it lives in, and its last-broadcast position
    /// (the dirtiness watermark for the SINGLE_SHIP_UPDATE broadcast).</summary>
    internal sealed record LiveShip(Ship Ship, Sector Sector, Vector3 LastBroadcastPos);

    /// <summary>
    /// Optional seam for tests (and future launch logic): replace the default
    /// ship-on-logon allocation with a caller-supplied one. Receives the core,
    /// the mission to place into and the freshly-allocated ship id; must
    /// <see cref="Mission.PlaceShip"/> the ship itself and return it plus the
    /// sector it was placed in. The default implementation picks a hull from
    /// the core, staggers the spawn by peer count, and places into the first
    /// sector. Internal so the demo behavior stays the default; tests inject a
    /// known hull + spawn so the independent trajectory reference shares them.
    /// </summary>
    internal Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)>? ShipSpawner;

    /// <summary>The game worlds, in config order.</summary>
    public IReadOnlyList<Mission> Missions => _missions ?? throw new InvalidOperationException("Boot() first");

    /// <summary>Total tick-loop passes so far (one pass ticks every mission once).</summary>
    public long LoopIterations { get { lock (_statsLock) return _loopIterations; } }

    /// <summary>How many real-time pass costs exceeded the nominal period.</summary>
    public long Overruns { get { lock (_statsLock) return _overruns; } }

    /// <summary>Peers whose greet was processed (the join was logged) since boot.</summary>
    public long ConnectionsJoined { get { lock (_statsLock) return _connectionsJoined; } }

    /// <summary>Simulation time advanced so far, seconds.</summary>
    public double SimNow { get { lock (_statsLock) return _simNow; } }

    public ServerConfig Config => _config;

    private double Period => _period;

    /// <summary>The bound port once the transport is hosted.</summary>
    public ushort BoundPort => _host?.BoundPort ?? 0;

    /// <summary>The transport-site host (gateway seam). Internal: tests drive a
    /// client transport against <see cref="BoundPort"/>, they do not need the host.</summary>
    internal ServerHost Host => _host;

    /// <summary>Peer-owned live ships (sim-thread owned). Tests resolve which
    /// ship belongs to which bot by <see cref="SingleShipUpdate.OidTarget"/> /
    /// <c>SET_CLUSTER.Ship.ShipId</c> and read its position/controls directly.</summary>
    internal IReadOnlyCollection<LiveShip> Ships => _peerShips.Values;

    // ── boot ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Load the config file, then <see cref="Boot(ServerConfig)"/>.
    /// </summary>
    public void Boot(string configPath, IReadOnlyList<(string Key, string Value)> cliOverrides) =>
        Boot(ServerConfigLoader.Load(configPath, cliOverrides));

    /// <summary>
    /// Boot in the strict order dictated by the reference engine (MISSION_INIT.md
    /// §4): static core first, then sides / sectors, then the tick. Stubbed step
    /// 5 logs that cores.txt/ranks.txt/maps.txt are intentionally not loaded.
    /// </summary>
    public void Boot(ServerConfig config)
    {
        _config = config;
        _period = _config.UpdatesPerSecond > 0 ? 1.0 / _config.UpdatesPerSecond : 0.05;
        _simNow = 0.0;

        ValidatePaths();
        var core = LoadCore();                       // checklist item 4
        StubFileLists();                             // checklist item 5 (stub)
        BindHostAndTransport();                      // checklist items 3 & 6
        OpenCaptureIfEnabled();                      // optional --capture (off by default)
        var missions = InstantiateGames(core);       // checklist item 8
        _missions = missions;
        PrintReady();
    }

    private void ValidatePaths()
    {
        if (string.IsNullOrWhiteSpace(_config.ArtPath) || !Directory.Exists(_config.ArtPath))
            throw new InvalidDataException($"artPath '{_config.ArtPath}' does not exist (a real corpus root is required)");
    }

    private StaticCore LoadCore()
    {
        var corePath = Path.Combine(_config.ArtPath, _config.StaticCore);
        if (!File.Exists(corePath))
            throw new InvalidDataException($"static core '{corePath}' not found under artPath");
        var file = IgcReader.ParseFile(corePath);
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException($"'{corePath}' is not a static core (wrapper {file.Wrapper})");
        var core = StaticCore.Load(file);
        Console.WriteLine($"[boot] static core '{_config.StaticCore}' loaded: version={core.Version}, " +
                          $"{core.Civilizations.Count} civilizations, {core.HullTypes.Count} hulls, " +
                          $"{core.StationTypes.Count} station types");

        // The config indexes civilizations by id; surface the mapping so a bad
        // id in a game definition is obviously wrong in the logs.
        foreach (var civ in core.Civilizations.OrderBy(kv => kv.Key))
            Console.WriteLine($"[boot]   civ {civ.Key,4}  '{civ.Value.Name}'");
        return core;
    }

    /// <summary>BOOT_PATH.md step 5 — cores.txt/ranks.txt/maps.txt are stubbed
    /// (dropped). We do not load them; we say so rather than silently ignoring.</summary>
    private static void StubFileLists() =>
        Console.WriteLine("[boot] (stub) cores.txt / ranks.txt / maps.txt NOT loaded — download & rank lists are out of scope");

    private void BindHostAndTransport()
    {
        _host = new ServerHost();
        _host.ShipKilled = BroadcastKillShip;   // wire the site's KillShipEvent to a broadcast
        _host.ShipWarped = OnShipWarped;        // wire the site's HitWarpEvent transit to per-peer routing
        _host.Payday = OnPayday;                // wire the site's PaydayEvent to the per-side wire payout
        _host.Mine = OnMine;                    // wire the site's MineAsteroidEvent to the host logger
        _host.ShipDocked = OnShipDocked;        // wire the site's DockWithStationEvent to the per-peer docked notify
        _host.ProjectileCreated = BroadcastProjectileInstance;   // P88: wire the site's ProjectileCreatedEvent to the wire broadcast
        _host.MissileCreated = BroadcastFireMissile;            // P90: wire the site's MissileCreatedEvent to the wire broadcast
        _host.MissileDestroyed = BroadcastMissileDestroyed;     // P90: wire the site's MissileDestroyedEvent to the wire broadcast
        _host.MineCreated = BroadcastExportMine;                // P94: wire the site's MineCreatedEvent to the wire broadcast
        _host.MineDestroyed = BroadcastMineDestroyed;           // P94: wire the site's MineDestroyedEvent to the wire broadcast
        _host.ProbeCreated = BroadcastExportProbe;             // P99: wire the site's ProbeCreatedEvent to the wire broadcast
        _host.ProbeDestroyed = BroadcastProbeDestroyed;        // P99: wire the site's ProbeDestroyedEvent to the wire broadcast
        _host.WarpBomb = BroadcastWarpBomb;                   // P101: wire the site's WarpBombEvent to the WHOLE-mission wire broadcast
        _host.AsteroidKilled = BroadcastAsteroidDestroyed;     // P102: wire the site's KillAsteroidEvent to the WHOLE-mission wire broadcast
        _host.Notable = OnNotable;              // wire the site's ReportNotable to the public spectator event
        var transport = new TcpTransport(_host);      // site set; assign the back-ref next
        _host.Transport = transport;
        transport.Host((ushort)_config.Port);         // reproduce: bind the logon transport
        Console.WriteLine($"[boot] transport listening on 127.0.0.1:{_host.BoundPort}");
    }

    /// <summary>Open the capture file (truncating any prior capture) when
    /// <c>--capture</c> was given; otherwise leave <see cref="_captureStream"/>
    /// null. No other behaviour depends on capture, so when off there is nothing
    /// to undo and no timing impact.</summary>
    private void OpenCaptureIfEnabled()
    {
        var path = _config.CapturePath;
        if (string.IsNullOrEmpty(path)) return;
        _captureStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        Console.WriteLine($"[boot] capturing packets to '{path}' (pipe through tools/fedmsgdump)");
    }

    /// <summary>Central send path: optional capture then the actual send. Every
    /// packet the server emits flows through here so capture sees all of TX.</summary>
    private void SendPeer(ConnectionId target, byte[] packet)
    {
        lock (_captureLock)
            _captureStream?.Write(CaptureStream.EncodeFrame(sent: true, packet));
        _host.Send(target, packet);   // ServerHost.Send forwards as guaranteed: true
    }

    /// <summary>Capture one received packet before it is processed. Called from
    /// the transport event drain (the sim thread) so it is naturally serialized
    /// with <see cref="SendPeer"/>'s lock-guarded writes.</summary>
    private void CaptureReceived(byte[] packet)
    {
        lock (_captureLock)
            _captureStream?.Write(CaptureStream.EncodeFrame(sent: false, packet));
    }

    private List<Mission> InstantiateGames(StaticCore core)
    {
        var missions = new List<Mission>();

        // P116 — the production landing-bay resolver/cache, keyed by hull
        // ModelName and shared by every mission this server hosts. Its loader
        // locates each carrier's .cvh under the configured art path (the same
        // asset-root convention the static core and maps use; all corpus .cvh
        // files sit flat at that root), parses it once and caches the
        // reconstructed bay geometry, so the per-tick carrier-landing pass
        // (Sector.ResolveCarrierLandings) never reparses frames.
        var landingBays = new HullLandingBayCache(name =>
        {
            var p = Path.Combine(_config.ArtPath, name + ".cvh");
            return File.Exists(p) ? CvhReader.Parse(File.ReadAllText(p)) : null;
        });

        if (_config.Games.Count == 0)
        {
            Console.WriteLine("[boot] (config) no games — booting a bare transport, nothing to tick");
            return missions;
        }

        foreach (var g in _config.Games)
        {
            var mapPath = Path.Combine(_config.ArtPath, g.Map);
            if (!File.Exists(mapPath)) throw new InvalidDataException($"map '{mapPath}' not found under artPath");
            var mapFile = IgcReader.ParseFile(mapPath);
            if (mapFile.Wrapper != IgcWrapper.DataSize)
                throw new InvalidDataException($"'{g.Map}' is not a map (wrapper {mapFile.Wrapper})");
            var map = MissionMap.Load(mapFile);

            if (g.Teams < 1)
                throw new InvalidDataException($"game '{g.Name}': teams must be >= 1, got {g.Teams}");
            if (g.CivIds.Length < g.Teams)
                throw new InvalidDataException($"game '{g.Name}': {g.Teams} teams but {g.CivIds.Length} civ ids");
            if (g.SideNames.Length < g.Teams)
                throw new InvalidDataException($"game '{g.Name}': {g.Teams} teams but {g.SideNames.Length} side names");

            var sides = new SideSpec[g.Teams];
            for (int t = 0; t < g.Teams; t++)
                sides[t] = new SideSpec((short)t, g.SideNames[t], (short)g.CivIds[t]);

            var mission = Mission.Assemble(core, map, sides, _host);  // site bound; core resolved
            mission.Stage = MissionStage.Started;                     // ready to tick
            // P116 — attach the landing-bay resolver to every sector so the
            // per-tick carrier-landing pass has real .cvh geometry to test.
            foreach (var sec in mission.Sectors) sec.LandingBays = landingBays;
            missions.Add(mission);
            Console.WriteLine($"[boot] game '{g.Name}' ready: map={g.Map}, {mission.Sectors.Count} sectors, " +
                              $"{mission.Sides.Count} sides [{string.Join(", ", mission.Sides.Select(s => s.Name ?? $"side{s.Id}"))}], " +
                              $"{mission.Warps.Count} warps, {mission.Sectors.Sum(s => s.Stations.Count)} stations, " +
                              $"{mission.Sectors.Sum(s => s.Asteroids.Count)} asteroids" +
                              (mission.DanglingWarps.Count > 0 ? $", {mission.DanglingWarps.Count} DANGLING warps" : ""));
        }
        return missions;
    }

    private void PrintReady() =>
        Console.WriteLine($"[boot] server ready: {_missions.Count} mission(s), {_config.UpdatesPerSecond} upd/s " +
                          $"(period {_period * 1000:0.###} ms). Ctrl-C to stop.");

    // ── the tick loop ───────────────────────────────────────────────────────

    /// <summary>
    /// One pass: pump transport events, advance the simulation clock by exactly
    /// one period and tick every mission, then sleep the real remainder to hold
    /// cadence. Measures the real pass cost with a Stopwatch and logs a tick
    /// that overran the period (never silently skips or accumulates — the sim
    /// clock is independent of real wall time).
    /// </summary>
    public void Tick()
    {
        var sw = Stopwatch.StartNew();
        SimPass();
        sw.Stop();
        SyncCadence(sw.Elapsed.TotalSeconds);
    }

    /// <summary>The simulation half of a tick, with no cadence sleep and no
    /// overrun accounting: pump + advance sim + tick every mission. Deterministic
    /// and fast, so tests can drive many ticks without wall-clock dependency.</summary>
    public void TickSim() => SimPass();

    /// <summary>Pump transport, advance sim by exactly one period, tick every mission.</summary>
    private void SimPass()
    {
        _host.Drain(HandleTransportEvent);
        lock (_statsLock) _simNow += _period;
        double now;
        lock (_statsLock) now = _simNow;
        foreach (var m in _missions) m.Update(now);
        lock (_statsLock) _loopIterations++;
        // P112 — broadcast ripcord aborts raised during this tick's per-tick
        // revalidation pass (Sector.ResolveRipcordCountdowns). The sim cleared
        // each ripper's ripcord state; here the host emits RIPCORD_ABORTED(187)
        // to everyone flying in the ripper's own sector, reusing P111's
        // SendRipcordSectorWide helper (FedSrv.CPP:13427-13434, the native's
        // GetGroupSectorFlying broadcast). Runs on the sim thread, so it may
        // send without locking, exactly like the request handler.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var rip in sector.DrainRipcordAborts())
                    if (rip.Sector is { } ripSec)
                        SendRipcordSectorWide(ripSec.Id,
                            new RipcordAborted { ShipidRipcord = rip.Id }.Encode());
        // P113 — perform the ripcord teleports whose countdowns reached zero
        // this tick (Sector.ResolveRipcordCountdowns). Server.PerformRipcordTeleport
        // is the port of UseRipcord (FedSrv.CPP:13440-13500): it relocates the
        // ripper (station/ship anchor), pays the energy cost, broadcasts
        // RIPCORD_ABORTED on energy-insufficiency, and clears the ripcord state.
        // Runs on the sim thread before the broadcast below so the position
        // delta is picked up by the same tick's SINGLE_SHIP_UPDATE.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var rip in sector.DrainRipcordTeleports())
                    PerformRipcordTeleport(rip);
        // P115 — perform the rescues this tick's proximity pass found
        // (Sector.ResolveRescueEvents). Server.PerformRescue is the port of
        // RescueShipEvent (FedSrv.CPP:13033-13074): it sends PLAYER_RESCUED(16)
        // to both ships' connections, then places the rescued ship at the
        // nearest friendly station. Runs on the sim thread before the broadcast
        // below so the docked rescuee's state is picked up by the same tick's
        // SINGLE_SHIP_UPDATE / SHIP_STATUS.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var (rescuee, rescuer) in sector.DrainRescues())
                    PerformRescue(rescuee, rescuer);
        // P117 — perform the real relaunch this tick's carrier-landing pass
        // found (Sector.ResolveCarrierLandings). Server.PerformRelaunch is the
        // port of LandOnCarrierEvent (FedSrv.CPP:12808-13031): restore the
        // lander's ship-level energy/ammo pools, reposition it at a real
        // launch slot (or the fallback backward offset when the carrier has no
        // launch frames), and broadcast RELAUNCH_SHIP(192) sector-wide. Runs
        // on the sim thread before the broadcast below so the lander's new
        // position/attitude is picked up by the same tick's
        // SINGLE_SHIP_UPDATE.
        foreach (var m in _missions)
            foreach (var sector in m.Sectors)
                foreach (var (carrier, lander) in sector.DrainCarrierLandings())
                    PerformRelaunch(lander, carrier);
        // Step 3: after the mission tick, broadcast every peer ship that moved.
        // Runs on the sim thread, so "only Mission.Update moves ships" holds —
        // this is purely an observation send of the authoritative state.
        BroadcastShipStates((uint)TickIndex);
    }

    /// <summary>Current completed-tick count, read under the stats lock.</summary>
    private long TickIndex { get { lock (_statsLock) return _loopIterations; } }

    /// <summary>Period between periodic own-ship SHIP_STATUS refreshes, in
    /// sim ticks. The client's strategy wires its MessageReceiver to the
    /// connection only on attach (<c>StrategyBase.cs:90</c>), which races the
    /// join burst — so the single join-burst status (and, today, the dormant
    /// sector-change resend) can arrive before the strategy is listening.
    /// Refreshting every interval guarantees a status reaches a freshly-attached
    /// strategy within one interval, matching the real FedSrv's frequent
    /// ShipStatus emission (<c>fsship.cpp</c> ShipStatusSpotted/Hidden). Same-
    /// sector repeats are harmless: ScoutExploreMap's handler skips them once
    /// it's moving/sweping (ScoutExploreMap.cs:141-146).</summary>
    private const int StatusRefreshTicks = 40;   // 2 s at 20 upd/s

    /// <summary>Period between periodic same-side TEAMMATE SHIP_STATUS(90)
    /// refreshes, in sim ticks. The reference sends every player a SHIP_STATUS
    /// for every ship in the mission (<c>fsmission.cpp:4303-4323</c>, no
    /// self-filter); this server throttles to one per (recipient,subject) pair
    /// per interval, enough for a commander's
    /// <c>UndockMinersAndStartMining</c> (which drives off its own ~5 s ping
    /// cadence) to learn a teammate is docked within a few seconds. ~5 s at
    /// 20 upd/s.</summary>
    private const int TeammateStatusRefreshTicks = 100;

    /// <summary>For every peer ship that changed position since its last
    /// broadcast, send that peer a <c>FM_S_SINGLE_SHIP_UPDATE(78)</c> carrying the raw
    /// authoritative position/velocity/orientation, timestamped with the tick
    /// index (the uint <c>Time</c> slot). No broadcast when a ship has not
    /// moved (zero thrust ⇒ silence), matching the C++ dirty-state update.
    /// Also re-sends <c>FM_S_SHIP_STATUS(90)</c> for a peer's own ship on a
    /// sector change (immediate) or periodic refresh (see
    /// <see cref="StatusRefreshTicks"/>), and — P58/P62 — periodically re-sends
    /// <c>FM_S_SHIP_STATUS(90)</c> about each same-side peer ship to that peer
    /// (see <see cref="TeammateStatusRefreshTicks"/>), preceded once per
    /// (recipient, subject) pair by a real <c>FM_S_EXPORT(26)</c> so the
    /// teammate ship object exists client-side at all, so a peer's client
    /// builds a full picture of its own side, matching the reference's
    /// per-player status send (<c>fsmission.cpp:4303-4323</c>).</summary>
    private void BroadcastShipStates(uint tickIndex)
    {
        // One snapshot reused for both the own-ship walk and the (P58)
        // same-side teammate walk below, so neither mutates mid-iteration.
        var peers = _peerShips.ToArray();
        foreach (var kv in peers)
        {
            var live = kv.Value;
            var ship = live.Ship;
            if ((ship.Position - live.LastBroadcastPos).Length() >= DirtyEpsilon)
            {
                var msg = new SingleShipUpdate
                {
                    OtTarget = 0,                          // OT_ship
                    OidTarget = ship.Id,
                    Ship = ServerShipUpdate.FromModel(ship.Id, ship, tickIndex),
                    BIsRipcording = false,
                };
                SendPeer(kv.Key, FedPacket.Pack(new[] { msg.Encode() }));
                _peerShips[kv.Key] = live with { LastBroadcastPos = ship.Position };
            }

            // FM_S_SHIP_STATUS(90) sector-change + periodic refresh hook. The
            // strategy's sweep chain is driven by these status frames; resend
            // when the ship crosses a sector (ShipWarped, appmsg.cpp:2723-2729)
            // and periodically so a post-attach strategy receives one (see
            // StatusRefreshTicks). `_lastStatusSector`/`_lastStatusTick` are
            // sim-thread only, same as the rest of this broadcast.
            short sentSector = _lastStatusSector.TryGetValue(kv.Key, out var s0) ? s0 : (short)-2;
            long lastTick = _lastStatusTick.TryGetValue(kv.Key, out var t0) ? t0 : -1;
            bool sectorChanged = live.Sector.Id != sentSector;
            bool refreshDue = lastTick < 0 || (long)tickIndex - lastTick >= StatusRefreshTicks;
            if (sectorChanged || refreshDue)
            {
                SendPeer(kv.Key, FedPacket.Pack(new[] { MakeOwnShipStatus(ship.Id, live).Encode() }));
                Console.WriteLine($"[shipstatus] peer {kv.Key} " +
                    (sectorChanged ? $"sector change {sentSector} -> {live.Sector.Id}" : $"refresh (sector {live.Sector.Id})"));
                _lastStatusSector[kv.Key] = live.Sector.Id;
                _lastStatusTick[kv.Key] = tickIndex;
            }

            // P48: CLUSTERINFO(200) periodic refresh -- the exact same reason
            // SHIP_STATUS needs one, per this method's own doc comment above:
            // StrategyBase.cs only wires up MessageReceiver.OnAppMessage
            // inside Attach(), which itself only runs after ship assignment
            // -- so the join burst's one-time CLUSTERINFO send (this file,
            // the peer-join handler) is *always* processed by the native
            // client's own internal (pre-managed-subscription) message pump
            // and never reaches the managed handler. Confirmed directly:
            // an unconditional raw-fmid diagnostic across a whole 20s trial
            // showed OnAppMessage only ever receiving fmid 78/90 (the two
            // periodically-resent messages) -- never once 200. A longer
            // interval than StatusRefreshTicks is fine (CLUSTERINFO is
            // static mission data, 4204 B vs SHIP_STATUS's ~24 B, and only
            // needs to land once after the managed subscription exists).
            long lastClusterInfoTick = _lastClusterInfoTick.TryGetValue(kv.Key, out var ci0) ? ci0 : -1;
            if (lastClusterInfoTick < 0 || (long)tickIndex - lastClusterInfoTick >= ClusterInfoRefreshTicks)
            {
                if (_clusterInfoBytes is { } bytes)
                {
                    SendPeer(kv.Key, FedPacket.Pack(new[] { bytes }));
                    _lastClusterInfoTick[kv.Key] = tickIndex;
                }
            }

            // P58: same-side TEAMMATE status broadcast. The reference's
            // per-player status send loops EVERY ship in the mission with NO
            // self-filter (CFSMission, fsmission.cpp:4303-4323), so its client
            // builds a full picture of every ship it knows about; this server
            // previously only ever sent each peer ITS OWN ship's status, so a
            // commander client could never learn a docked miner teammate even
            // existed — the exact gap CommanderResearchAndExpand.
            // UndockMinersAndStartMining hits today. Scope: same-side only (a
            // deliberate cut vs the reference's unrestricted visibility),
            // throttled per (recipient,subject) pair. The peer's own ship is
            // skipped (handled above). Reuses MakeOwnShipStatus unchanged —
            // it already accepts an arbitrary (shipId, live) pair, not just the
            // receiver's own.
            if (live.Ship.SideId is { } recipientSide)
            {
                foreach (var tv in peers)
                {
                    if (ReferenceEquals(tv.Value.Ship, ship)) continue;   // own ship, handled above
                    if (tv.Value.Ship.SideId != recipientSide) continue;  // same-side only
                    short teammateId = tv.Value.Ship.Id;

                    // P62: one-shot real ExportShip(26) about this teammate's
                    // ship, sent to the recipient BEFORE any SHIP_STATUS about
                    // the same subject. This is what makes the client's object
                    // model learn a teammate ship exists at all: FM_S_EXPORT is
                    // the object-CREATION message (appmsg.cpp:80-105 →
                    // CreateObject(OT_ship) news a CshipIGC), whereas GetShip()
                    // (SHIP_STATUS's lookup) can only update an object the
                    // client already has. Until this export, every already-
                    // connected peer is only ever told about its OWN ship
                    // (the server's single join-burst export, Server.cs:~812),
                    // so a teammate's P58 SHIP_STATUS had nothing to attach to.
                    //
                    // ShipId here is the REAL teammate id (NOT the -1/NA that
                    // the own-ship export uses as its "this is my own dummy"
                    // trick) so a genuinely new, separate client-side object
                    // is created. baseObjectID=-1 stays unchanged (docked
                    // carriage — which FM_S_DOCKED already handles — is
                    // explicitly out of scope here). Parts carries whatever
                    // the teammate has mounted AT THIS MOMENT (P61/ApplyLoadout's
                    // authoritative Ship.Parts) — see PartData's own doc comment
                    // for why the real client's ReInitialize (shipIGC.cpp:150-160)
                    // can realize a teammate's already-mounted loadout from this
                    // same EXPORT, closing the gap `BuyLoadoutAck` (owner-only,
                    // Server.cs ApplyLoadout) leaves for everyone else.
                    if (!_teammateExportSent.Contains((kv.Key, teammateId)) &&
                        _peerNames.TryGetValue(tv.Key, out var peerName))
                    {
                        var initialParts = SnapshotParts(tv.Value.Ship);
                        var teamExport = new ExportShip
                        {
                            ObjectType = ExportShip.OtShip,
                            CharacterName = peerName,
                            HullId = tv.Value.Ship.HullType.HullId,
                            ShipId = teammateId,                    // real id — creates a new client-side object
                            SideId = recipientSide,                 // the teammate's side (== recipient's, same-side only)
                            PilotType = ExportShip.PilotTypePlayer,
                            Parts = initialParts,
                        };
                        SendPeer(kv.Key, FedPacket.Pack(new[] { teamExport.Encode() }));
                        Console.WriteLine($"[teamexport] peer {kv.Key} <- ship {teammateId} '{peerName}' hull {teamExport.HullId} nParts {initialParts.Count}");
                        _teammateExportSent.Add((kv.Key, teammateId));
                        _teammateLoadoutSent[(kv.Key, teammateId)] = initialParts;
                    }

                    // P63: one-shot real PLAYERINFO(10) about this same teammate's
                    // ship — sent immediately after that pair's EXPORT (P62) and
                    // BEFORE any SHIP_STATUS about the same subject, extending P62's
                    // gating chain rather than building a competing one. Rationale
                    // (native source, not guessed): a ship's "private data" — the
                    // pointer IshipIGCWrapper::GetPlayerInfo()
                    // (AllegianceInterop/igcWrapper.cpp:1980-1982) dereferences — is
                    // set in EXACTLY one place, the FM_S_PLAYERINFO(10) handler's
                    // pPlayerInfo->SetShip(pship) (appmsg.cpp:2279), via
                    // pship->SetPrivateData((DWORD)this) (clintlib.h:311-314). Without
                    // this message a same-side ship gets an EXPORT object (P62) but
                    // never a PlayerInfo, so GetPlayerInfo() returns a wrapper built
                    // around a null/garbage native pointer, and
                    // UndockMinersAndStartMining's p.GetPlayerInfo().LastSeenState()
                    // (CommanderResearchAndExpand.cs:1558) dereferences it: an
                    // AccessViolationException, silently swallowed by OnAppMessage's
                    // blanket SEH handler (NativeClient.cpp:29-45) — no C# exception,
                    // pump keeps working, diagnostic never prints.
                    //
                    // Ordering: placed after the EXPORT (P62) above and before the
                    // STATUS below. When PLAYERINFO's handler runs, CreateObject
                    // (appmsg.cpp:2264) FAILS because the id exists (from EXPORT) and
                    // it takes the else branch — GetShip(shipID) (appmsg.cpp:2276),
                    // resolving the REAL ship EXPORT created, and calling
                    // SetShip on it (:2279). If PLAYERINFO came first it would create
                    // its own separate throwaway ship and attach to THAT — silently
                    // wrong, hence worth getting right, not something a test catches
                    // by accident. Fields match the existing own-player send
                    // (Server.cs:946-954) exactly except ShipId = the teammate's real
                    // id (NOT the -1/NA own-ship trick — that NA is a deliberate,
                    // documented dummy adoption specific to the self-referential own
                    // ship; there is no dummy for a teammate, so a real, separate
                    // object must be created). Money stays 0 (codec docstring: the
                    // native client asserts GetMoney()==0 in AddPlayerToSide,
                    // clintlib.cpp:4109). ISide = the teammate's own SideId (==
                    // recipient's, same-side only); CharacterName from `_peerNames`.
                    if (_teammateExportSent.Contains((kv.Key, teammateId)) &&
                        !_teammatePlayerInfoSent.Contains((kv.Key, teammateId)) &&
                        _peerNames.TryGetValue(tv.Key, out var teammateName))
                    {
                        var teamInfo = new PlayerInfo
                        {
                            CharacterName = teammateName,
                            Money = 0,                       // flat 0 — satisfies AddPlayerToSide's assert
                            ISide = recipientSide,           // the teammate's own side (== recipient's, same-side only)
                            ShipId = teammateId,             // REAL id — the ship the EXPORT created
                            PilotType = PlayerInfo.PilotTypePlayer,
                        };
                        SendPeer(kv.Key, FedPacket.Pack(new[] { teamInfo.Encode() }));
                        Console.WriteLine($"[teamplayerinfo] peer {kv.Key} <- ship {teammateId} '{teammateName}' side {recipientSide}");
                        _teammatePlayerInfoSent.Add((kv.Key, teammateId));
                    }

                    // P58 teammate STATUS broadcast — gated so it only proceeds
                    // once THIS pair's EXPORT has been sent (this tick or an
                    // earlier one): a status for an object the client doesn't
                    // have yet is a no-op at best, so a brand-new pair's status
                    // must never race ahead of its own export within a pass.
                    // If the name lookup above failed (so no export was/will be
                    // sent), the status is skipped too, keeping the two in step.
                    if (_teammateExportSent.Contains((kv.Key, teammateId)))
                    {
                        long lastTeammateTick = _lastTeammateStatusTick.TryGetValue((kv.Key, teammateId), out var tt0) ? tt0 : -1;
                        if (lastTeammateTick < 0 || (long)tickIndex - lastTeammateTick >= TeammateStatusRefreshTicks)
                        {
                            SendPeer(kv.Key, FedPacket.Pack(new[] { MakeOwnShipStatus(teammateId, tv.Value).Encode() }));
                            Console.WriteLine($"[teamshipstatus] peer {kv.Key} <- ship {teammateId} sector {tv.Value.Sector.Id}" +
                                (tv.Value.Ship.StationId is not null ? $" DOCKED@{tv.Value.Ship.StationId}" : " flying"));
                            _lastTeammateStatusTick[(kv.Key, teammateId)] = tickIndex;
                        }

                        // P85: teammate POSITION broadcast — the actual visibility
                        // gap this whole block left open. EXPORT/PLAYERINFO/STATUS
                        // above let a teammate's ship exist and report its coarse
                        // structural state (sector/dock), but SINGLE_SHIP_UPDATE (the
                        // message that actually carries position/velocity/orientation,
                        // same as the own-ship send at the top of this method) was
                        // never sent for anyone but a peer's own ship — confirmed live
                        // (AllegianceClientCore P3): a teammate's ship was visible and
                        // correctly statused, but never observed to move. Reuse the
                        // identical dirty-position gate the own-ship send above uses —
                        // `tv.Value`'s own `LastBroadcastPos`, read from this tick's
                        // frozen `peers` snapshot, which is safe regardless of
                        // iteration order since nothing this tick has mutated `peers`
                        // itself — so a teammate's position is broadcast to a given
                        // recipient exactly as often as it's broadcast to its own
                        // peer, no more.
                        if ((tv.Value.Ship.Position - tv.Value.LastBroadcastPos).Length() >= DirtyEpsilon)
                        {
                            var teamMove = new SingleShipUpdate
                            {
                                OtTarget = 0,                          // OT_ship
                                OidTarget = teammateId,
                                Ship = ServerShipUpdate.FromModel(teammateId, tv.Value.Ship, tickIndex),
                                BIsRipcording = false,
                            };
                            SendPeer(kv.Key, FedPacket.Pack(new[] { teamMove.Encode() }));
                        }

                        // Teammate LOADOUT re-export: the initial EXPORT above only
                        // captures whatever was mounted at export time; a teammate
                        // who buys a new loadout afterward (ApplyLoadout mutating
                        // this same Ship.Parts) would otherwise never be seen again
                        // by an already-exported recipient — BuyLoadoutAck is
                        // owner-only (SendPeer(from) in ApplyLoadout), so re-sending
                        // this same EXPORT is the only wire path a non-owning peer
                        // has to learn the change (see ExportShip's own doc comment:
                        // CshipIGC::ReInitialize handles a re-send for an id the
                        // client already owns exactly like a fresh creation). Gated
                        // on real content change, not a tick cadence — a loadout
                        // changes rarely, so there is no periodic-refresh case to
                        // cover here the way SHIP_STATUS/position need one.
                        var currentParts = SnapshotParts(tv.Value.Ship);
                        if (!PartsEqual(currentParts, _teammateLoadoutSent[(kv.Key, teammateId)]) &&
                            _peerNames.TryGetValue(tv.Key, out var currentPeerName))
                        {
                            var loadoutExport = new ExportShip
                            {
                                ObjectType = ExportShip.OtShip,
                                CharacterName = currentPeerName,
                                HullId = tv.Value.Ship.HullType.HullId,
                                ShipId = teammateId,
                                SideId = recipientSide,
                                PilotType = ExportShip.PilotTypePlayer,
                                Parts = currentParts,
                            };
                            SendPeer(kv.Key, FedPacket.Pack(new[] { loadoutExport.Encode() }));
                            Console.WriteLine($"[teamloadout] peer {kv.Key} <- ship {teammateId} loadout changed, nParts {currentParts.Count}");
                            _teammateLoadoutSent[(kv.Key, teammateId)] = currentParts;
                        }
                    }
                }
            }
        }
    }

    /// <summary>10 s at 20 upd/s -- see the CLUSTERINFO refresh block in
    /// <see cref="BroadcastShipStates"/> for why this exists.</summary>
    private const int ClusterInfoRefreshTicks = 200;

    /// <summary>Build a peer's own-ship SHIP_STATUS(90) from its live record:
    /// docked (carrying the real StationId + State=c_ssDocked) when the ship is
    /// docked, else flying free with StationID/ParentID NA; detected/unknown
    /// fixed per the simplification documented at the join-burst send site.
    /// P108: a <b>boarded</b> ship (ParentShip set) overrides with
    /// State=c_ssTurret/c_ssObserver, ParentID = the carrier's id, and
    /// HullID = NA (riding inside another hull — the native
    /// <c>pss->SetHullID(NA)</c>, FedSrv.CPP:2454). Because every status path
    /// (own-ship periodic refresh, sector change, and the P58 teammate walk)
    /// funnels through this one method, a board/leave transition is reflected
    /// on the wire by the same per-tick mechanism those paths already use — no
    /// new success-ack message is needed (the task's confirmed scoping win).</summary>
    private static ShipStatusMessage MakeOwnShipStatus(short shipId, LiveShip live)
    {
        var ship = live.Ship;
        bool docked = ship.StationId is not null;
        if (ship.ParentShip is { } parent)
        {
            // Boarded — riding inside the carrier's hull (occupancy only; the
            // turret aim/fire binding is a later P109 task).
            return new()
            {
                ShipId = shipId,
                HullId = -1,                                      // NA — riding another hull (pss->SetHullID(NA), FedSrv.CPP:2454)
                SectorId = live.Sector.Id,
                StationId = docked ? ship.StationId!.Value : (short)-1,
                ParentId = parent.Id,                             // the carrier (pss->SetParentID(carrier), FedSrv.CPP:2453)
                State = ship.TurretId is null
                    ? ShipStatusMessage.StateObserver            // c_ssObserver (igc.h:4393)
                    : ShipStatusMessage.StateTurret,             // c_ssTurret   (igc.h:4395)
                Unknown = false,
                Detected = true,
                StateTime = 0,
            };
        }
        return new()
        {
            ShipId = shipId,
            HullId = ship.HullType.HullId,
            SectorId = live.Sector.Id,
            StationId = docked ? ship.StationId!.Value : (short)-1,  // NA when flying free
            ParentId = -1,                          // NA — no docked/turreted children (ShipStatusDocked sets ParentID=NA, fsship.cpp:376)
            State = docked ? ShipStatusMessage.StateDocked : ShipStatusMessage.StateFlying,  // c_ssDocked/c_ssFlying (igc.h:4390,4394)
            Unknown = false,                 // no scanning system yet
            Detected = true,                 // no scanning system yet
            StateTime = 0,                   // sim clock unused (no dTime source here)
        };
    }

    /// <summary>The teammate ExportShip PartData[] tail as it is right now,
    /// mount order — see <see cref="PartData"/>'s own doc comment for why this
    /// drops <c>MountedPart.FractionMounted</c> (a requester-only, BUY_LOADOUT-
    /// specific concern; an already-mounted part observed via EXPORT has none).</summary>
    private static IReadOnlyList<PartData> SnapshotParts(Ship ship) =>
        ship.Parts.Select(p => new PartData(p.PartId, p.Amount, p.MountId)).ToArray();

    /// <summary>Order-and-value equality for two PartData snapshots — used to
    /// gate the teammate loadout re-export on a real content change, not a
    /// reference/identity difference (<see cref="SnapshotParts"/> always
    /// allocates a fresh array).</summary>
    private static bool PartsEqual(IReadOnlyList<PartData> a, IReadOnlyList<PartData> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>Shared by Tick() and (optionally) a caller wishing to hold real
    /// cadence on top of its own N sim advances. Sleeps the remainder of the
    /// period, or logs an overrun if the pass cost already exceeded it.</summary>
    private void SyncCadence(double cost)
    {
        double budget = _period - cost;
        if (budget > 0)
        {
            Thread.Sleep(TimeSpan.FromSeconds(budget));
        }
        else
        {
            lock (_statsLock) _overruns++;
            Console.WriteLine($"[overrun] tick cost {cost * 1000:0.##} ms exceeded period {_period * 1000:0.##} ms " +
                              $"by {(cost - _period) * 1000:0.##} ms (sim continues at full rate)");
        }
    }

    /// <summary>Standard loop body: tick until cancelled.</summary>
    public void Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Tick();
            if (ct.IsCancellationRequested) break; // observe cancellation promptly after sleeping
        }
    }

    /// <summary>Run the loop for a bounded duration; returns total ticks.</summary>
    public long RunFor(TimeSpan duration, CancellationToken external = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        cts.CancelAfter(duration);
        Loop(cts.Token);
        return LoopIterations;
    }

    // ── event processing (simulation thread) ───────────────────────────────

    private void HandleTransportEvent(ITransportEvent evt)
    {
        switch (evt)
        {
            case PeerJoinedEvt j:
                Console.WriteLine($"[conn] peer {j.Id} '{j.Name}' joined");
                lock (_statsLock) _connectionsJoined++;
                break;
            case PeerLeftEvt l:
                // A disconnected peer can no longer command its ship, so it is
                // released from the broadcast set (the ship itself stays in the
                // sector and keeps flying under Mission.Update — that is what
                // "a bot disconnect leaves the server ticking" exercises). The
                // tick never depends on a peer's presence.
                _peerShips.Remove(l.Id);
                _peerNames.Remove(l.Id);                     // P62: release the remembered name with the peer
                Console.WriteLine($"[conn] peer {l.Id} left");
                break;
            case SessionLostEvt:
                Console.WriteLine("[conn] session lost");
                break;
            case ReceivedEvt r:
                CaptureReceived(r.Data);
                ProcessDatagram(r.From, r.Data);
                break;
        }
    }

    /// <summary>Unpack, validate and answer one inbound packet. At the headless
    /// milestone the only message a client is expected to send is the logon
    /// request; anything else is reported and ignored (no crash).</summary>
    private void ProcessDatagram(ConnectionId from, byte[] data)
    {
        byte[][] messages;
        try { messages = FedPacket.Unpack(data); }
        catch (FedCrcException e) { Console.WriteLine($"[wire] crc/ framing failure from {from}: {e.Message}"); return; }

        foreach (var m in messages)
        {
            ushort fmid;
            try { fmid = FedMessageHeader.Read(m).Fmid; }
            catch (Exception e) { Console.WriteLine($"[wire] bad message header from {from}: {e.Message}"); continue; }

            if (fmid == LogonRequest.Fmid)
            {
                LogonRequest req;
                try { req = LogonRequest.Decode(m); }
                catch (Exception e) { Console.WriteLine($"[wire] bad LOGONREQ from {from}: {e.Message}"); continue; }

                var validated = req.VersionMatches;
                // Step 3: a validated logon gets a ship. Allocate an id, create
                // and place the Ship via the spawner (default or injected), then
                // answer the handshake with that ship id.
                short shipId = (short)(_nextShipId++);
                LiveShip live = default!;
                if (validated && _missions.Count > 0)
                {
                    live = AssignShip(req, shipId);
                    Console.WriteLine($"[launch] peer {from} '{req.CharacterName}' got ship {shipId} in sector '{live.Sector.Name}' " +
                                      $"({live.Ship.Position})");
                }

                var ack = new LogonAck
                {
                    CharNameOrFailureReason = validated ? $"Welcome {req.CharacterName}" : "wrong client version",
                    TimeServer = (uint)req.Time + 1,
                    ShipID = validated ? shipId : (short)0,
                    FValidated = validated,
                    FRetry = !validated,
                    Cookie = (byte)(req.DwCookie & 0xFF),
                };
                SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
                Console.WriteLine($"[logon] {from} '{req.CharacterName}' validated={validated} -> " +
                                  $"'{ack.CharNameOrFailureReason}' (server time {ack.TimeServer}, ship {ack.ShipID})");

                // Step 3: the real assignment message — SET_CLUSTER(7) tells the
                // client its sector and full ServerSingleShipUpdate.
                if (validated)
                {
                    _peerShips[from] = live with { LastBroadcastPos = live.Ship.Position };
                    _peerNames[from] = req.CharacterName ?? "";   // P62: remember past logon for teammate EXPORT name

                    // FM_S_MISSIONDEF(38) then FM_S_JOINED_MISSION(95) FIRST,
                    // before any object-creation/launch message. Reason (traced
                    // in P29): the JOINED_MISSION handler
                    // (appmsg.cpp:2150-2215) is the client's static-core load
                    // step — it calls ResetStaticData
                    // (appmsg.cpp:2162-2163 → clintlib.cpp:4193-4224), which
                    // runs FlushGameState() (destroys every ship, incl. any
                    // EXPORTed one, and the current mission) then
                    // CreateMission() + Initialize() (fresh no-hull dummy) and
                    // LoadIGCStaticCore() — the import that hydrates the
                    // mission's hull/part/station registries. So any EXPORT/
                    // SET_CLUSTER/BUY_LOADOUT_ACK sent BEFORE these two would
                    // be destroyed/replaced by the join; they MUST come after.
                    //
                    // The two fields that matter are driven by the server's own
                    // config + loaded core (no hardcoded values):
                    //   - misparms.szIGCStaticFile is the extensionless BASE
                    //     name of the static core ("PCore001"), because the
                    //     client's LoadIGCStaticCore calls UTL::getFile(name,
                    //     ".igc", ...) which APPENDS ".igc" (missionigc.cpp:
                    //     2410, _Utility/Utility.cpp:105-123). So the wire value
                    //     must NOT include the extension or the client looks for
                    //     "PCore001.igc.igc" (empirically caught by the P29
                    //     probe: filename=[C:\Source\git\Artwork\PCore001.igc.igc],
                    //     fopen=null → version gate bails, client disconnects).
                    //     Passing the extensionless base is what makes the
                    //     lstrcmp at appmsg.cpp:2162 trigger ResetStaticData at
                    //     all, pointing it at the real file. server.json:3 keeps
                    //     the full name "PCore001.igc" for OUR own LoadCore;
                    //     only the wire value is stripped.
                    //   - misparms.verIGCcore must equal the version word at
                    //     offset 0 of that .igc file (= the StaticCore the
                    //     server loaded), else ResetStaticData bails
                    //     (clintlib.cpp:4212-4218) and the client disconnects.
                    // Everything else in the large MissionParams/MISSIONDEF is
                    // simulation policy this headless server doesn't implement;
                    // grep shows the join flow reads only those two (plus the
                    // dwCookie identity / shipID tagging).
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new MissionDef
                        {
                            SzIGCStaticFile = Path.GetFileNameWithoutExtension(_config.StaticCore),
                            VerIgcCore = _missions[0].Core.Version,
                            DwCookie = 0,
                            ISideMissionOwner = 0,
                            Stage = 0,
                        }.Encode(),
                        new JoinedMission
                        {
                            ShipId = shipId,
                            DTime = 0,
                            Cookie = 0,
                            DwCookie = 0,
                        }.Encode(),
                    }));
                    Console.WriteLine($"[join] peer {from} '{req.CharacterName}' -> MISSIONDEF core '{Path.GetFileNameWithoutExtension(_config.StaticCore)}' v{_missions[0].Core.Version} + JOINED_MISSION ship {shipId} (static core load + hull-table hydration)");

                    // FM_S_EXPORT(26)/OT_cluster(13) cluster exports used to
                    // live here (right after JOINED_MISSION). Moved to AFTER
                    // PLAYERINFO/SET_CLUSTER's side-assignment dance below —
                    // see the comment down there for why: BaseClient::
                    // OnQuitSide (clintlib.cpp:3781-3783,
                    // m_pCoreIGC->ResetMission()) unconditionally nukes every
                    // registered cluster, and it fires from PLAYERINFO's own
                    // AddPlayerToSide call. Exporting clusters here just meant
                    // they got wiped moments later.

                    // FM_S_MISSIONDEF(38) again, AFTER the join reset, BEFORE
                    // the ship EXPORT. Reason (P31 empirical, probe-verified;
                    // reordered for the P33 native-side fix — cluster EXPORTs
                    // no longer need to precede this send: P31's original
                    // "civs not resolved immediately after JOINED_MISSION"
                    // finding does not reproduce empirically against the
                    // current static-core load path, confirmed directly via
                    // native P33dbg instrumentation across multiple live
                    // trials sending MISSIONDEF(38) right after JOINED_MISSION
                    // with no exports in between — UpdateSides ran clean every
                    // time): JOINED_MISSION's ResetStaticData
                    // (appmsg.cpp:2162-2163 → clintlib.cpp:4193-4224) DESTROYS
                    // the client's m_pCoreIGC and rebuilds a FRESH mission from
                    // the static core — which has NO sides (the static core is
                    // civ/hull records, not side records). The client's
                    // MISSIONDEF ELSE-branch (m_pMissionInfo != NULL,
                    // appmsg.cpp:2419-2425) runs UpdateSides here and creates
                    // the real sides AND, via CmissionIGC::AddSide
                    // (missionigc.cpp:3612), the SIDE_TEAMLOBBY side.
                    //
                    // Moved to BEFORE the ship EXPORT (was after, through P32):
                    // the ship EXPORT's ShipId is now NA (see the comment on
                    // that send below), which makes the native client's
                    // CshipIGC::Initialize dedup path match the client's own
                    // dummy ship and run CshipIGC::ReInitialize on it
                    // (shipIGC.cpp:65-163) directly — including its hull
                    // branch, which unconditionally reads
                    // pmission->GetSide(dataShip->sideID)->GetColor()
                    // (shipIGC.cpp:124-149) with NO null check. With sides
                    // sent after the ship EXPORT (the old order), that side
                    // lookup returns NULL and this is a genuine, deterministic
                    // null-pointer access violation — confirmed directly via
                    // native P33dbg instrumentation: the ship EXPORT's
                    // CreateObject call never returns (crashes inside
                    // ReInitialize, silently swallowed by
                    // NativeClient::OnAppMessage's blanket SEH handler), which
                    // is also *why* the cluster list was observed wiped by the
                    // time SET_CLUSTER ran (heap corruption from the crash).
                    // Sending sides first removes the NULL entirely.
                    var mdSides = _missions[0].Sides;
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new MissionDef
                        {
                            SzIGCStaticFile = Path.GetFileNameWithoutExtension(_config.StaticCore),
                            VerIgcCore = _missions[0].Core.Version,
                            DwCookie = 0,
                            NTems = (short)mdSides.Count,
                            RgCivId = mdSides.Select(s => s.Civilization.CivilizationId).ToArray(),
                        }.Encode(),
                    }));
                    Console.WriteLine($"[sides] peer {from} '{req.CharacterName}' -> MISSIONDEF(38) nTeams {mdSides.Count} civIds [{string.Join(", ", mdSides.Select(s => s.Civilization.CivilizationId))}] (create SIDE_TEAMLOBBY + real sides via UpdateSides)");

                    // FM_S_EXPORT(26) first: this is the real object-creation
                    // message the C++ client's dispatcher handles
                    // (appmsg.cpp:80-105 → m_pCoreIGC->CreateObject → for
                    // OT_ship=0 news a CshipIGC and AddShip-s it, so
                    // GetShip()/GetBaseHullType()/GetPosition() read from a
                    // non-null native backing — the T14 fix). SET_CLUSTER
                    // below updates that object; without this preceding
                    // creation message the client's ship wrapper has a null
                    // native backing and GetPosition() throws NRE. object
                    // creation must precede any per-tick state update
                    // referencing the id, hence the order.
                    //
                    // Own ship only — deliberately NOT broadcast to observers;
                    // mutual multi-bot visibility is out of scope here (same
                    // judgment call P19/P22 made for SingleShipUpdate
                    // broadening).
                    //
                    // nParts=0 simplification (unchanged from P26): the
                    // DataShipIGC is written with no mounted-part state and no
                    // PartData[] tail. Real per-hardpoint export would need
                    // Ship's mounted parts serialized in the exact PartData
                    // (partID/amount/mountID, 5 bytes each) format — real but
                    // separable; this task sends the object-creation header
                    // only. FM_S_EXPORT replaces the inert NEW_SHIP_INSTANCE
                    // (31) send from P26: that message has no case on the real
                    // client's dispatch switch (appmsg.cpp) so it did nothing,
                    // though its codec+tests remain as a faithful-but-inert
                    // port (fmid 31 is a real protocol message; our client
                    // just never dispatches it).
                    if (live.Ship.SideId is { } sideId)
                    {
                        // ShipId = NA (-1), NOT the real assigned shipId. This is the
                        // fix for the movement blocker traced directly (native-side,
                        // read/log evidence, not guessed): the client's OWN placeholder
                        // ship (BaseClient::CreateDummyShip, clintlib.cpp:3859-3882) is
                        // created with DataShipIGC.shipID = NA, and BaseClient::m_ship
                        // is never reassigned anywhere for the rest of the session
                        // (only two "m_ship =" sites exist in clintlib at all: NULL-init
                        // and this dummy creation). CshipIGC::Initialize
                        // (shipIGC.cpp:165-180) reconciles an incoming EXPORT with an
                        // EXISTING object purely by dataShip->shipID equality
                        // (pMission->GetShip(dataShip->shipID)). Sending the real,
                        // globally-unique shipId here (matching JoinedMission/LogonAck)
                        // makes that lookup MISS the dummy and creates an ORPHANED
                        // second ship object instead — hulled, but never the object
                        // m_ship points to. m_ship stays permanently hull-less, so when
                        // SET_CLUSTER runs, appmsg.cpp:1028
                        // (pship->GetThingSite()->SetTrailColor(...)) dereferences a
                        // NULL ThingSite (hull-less ships never LoadCVH,
                        // modelIGC.cpp:105-119) — a real access violation, confirmed
                        // directly in the P31d native debug log (pkt#7 = SET_CLUSTER,
                        // immediately followed by "NativeClient::ExceptionFilter()
                        // caught AV as expected"). NativeClient::OnAppMessage's blanket
                        // per-message SEH handler (NativeClient.cpp:29-45) silently
                        // swallows it, aborting SET_CLUSTER BEFORE
                        // pship->SetCluster(pcluster) (appmsg.cpp:1087) ever runs — so
                        // flyingF() (clintlib.h:1198) never becomes true and
                        // BaseClient::SendUpdate (clintlib.cpp:1815-1819) never fires
                        // FM_C_SHIP_UPDATE. The real, original FedSrv.CPP:907 creates
                        // its own internal placeholder ship the identical way
                        // (dataship.shipID = NA), corroborating NA as the intended wire
                        // value for a player's own ship at creation time.
                        //
                        // PLAYERINFO's ShipId (PlayerInfo.cs) deliberately keeps the
                        // REAL id, unchanged — its native handler (appmsg.cpp:2249)
                        // hardcodes DataShipIGC.hullID = NA on every call, so if it also
                        // matched the dummy by id it would immediately strip the hull
                        // this EXPORT just gave it via ReInitialize's FreeThingSite()
                        // path (shipIGC.cpp:135-138). Leaving it mismatched makes
                        // PLAYERINFO create a separate, harmless, unused bookkeeping
                        // ship instead (same as happens today), while ReInitialize
                        // (shipIGC.cpp:120-125) still applies this EXPORT's own SideId
                        // to m_ship directly, so side comes along for free.
                        const short OwnShipExportId = -1; // NA
                        var exportMsg = new ExportShip
                        {
                            ObjectType = ExportShip.OtShip,
                            CharacterName = req.CharacterName ?? "",
                            HullId = live.Ship.HullType.HullId,
                            ShipId = OwnShipExportId,
                            SideId = sideId,
                            PilotType = ExportShip.PilotTypePlayer,
                        };
                        SendPeer(from, FedPacket.Pack(new[] { exportMsg.Encode() }));
                        Console.WriteLine($"[create] peer {from} '{req.CharacterName}' -> EXPORT hull {exportMsg.HullId} ship NA(real={shipId}) side {sideId}");
                    }
                    else
                    {
                        // A ship with no SideId by SET_CLUSTER time is a real
                        // finding (every live-spawn path seats a side; see
                        // SpawnDefaultShip). Log it and skip the creation
                        // message for this peer — never fabricate side 0
                        // (guessing would put the bot on a fake side).
                        // SET_CLUSTER still goes out as before; without EXPORT
                        // the client wrapper stays null-backed, matching the
                        // T14 state, but that is the honest consequence of an
                        // unseated side.
                        Console.WriteLine($"[create] {from} '{req.CharacterName}': ship {shipId} has NO SideId — skipping EXPORT (finding, not guessed)");
                    }

                    // FM_S_PLAYERINFO(10): the missing side-carrier (T17 →
                    // P31). The client's SET_CLUSTER handler
                    // (appmsg.cpp:1034) reads
                    // GetShip()->GetSide()->GetColor(); T17 proved
                    // m_ship->GetSide() was NULL because the direct-connect
                    // client's ship never got a side assigned (JOINED_MISSION
                    // does NOT call AddPlayerToSide). This message's handler
                    // (appmsg.cpp:2217-2294) is the only dispatched path that
                    // reaches AddPlayerToSide (clintlib.cpp:4101 →
                    // pship->SetSide(pside) at :4121) — the call that
                    // actually sets m_ship's side. Placement reasoning:
                    //   • AFTER JOINED_MISSION (its ResetStaticData →
                    //     FlushGameState at appmsg.cpp:2162-2163 /
                    //     clintlib.cpp:4198 would destroy any ship/side the
                    //     PLAYERINFO handler built).
                    //   • AFTER the ship EXPORT (P33 update: sides are now
                    //     sent BEFORE the ship EXPORT, see above — the ship
                    //     EXPORT's ShipId is NA, matching the client's own
                    //     dummy, so it's the dummy that ends up with the real
                    //     hull+side via ReInitialize). PLAYERINFO's own
                    //     ShipId stays the real, non-NA id on purpose, so its
                    //     CreateObject(OT_ship,&ds,...) at :2264 does NOT match
                    //     the dummy (the real id was never assigned to it) and
                    //     takes the "create a new, separate, mostly-empty
                    //     bookkeeping ship" path instead of the GetShip(shipID)
                    //     else-branch (:2276) — harmless, since nothing reads
                    //     that object; m_ship (the dummy) is already correctly
                    //     hulled+sided by the ship EXPORT alone by this point.
                    //   • BEFORE SET_CLUSTER, whose handler is what crashed
                    //     for want of a Side.
                    // One message with the real iSide is expected to be enough
                    // (code-derived, not yet live-confirmed: AddPlayerToMission,
                    // called at :2284 before AddPlayerToSide at :2288, runs
                    // SetSideID(SIDE_TEAMLOBBY) at clintlib.cpp:4085, so the
                    // AddPlayerToSide assert at :4107 sees SideID()==-2 and
                    // should pass — no two-message dance needed). Whether that
                    // assert holds on the live client is P31b's empirical job.
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new PlayerInfo
                        {
                            CharacterName = req.CharacterName ?? "",
                            Money = 0,
                            ISide = live.Ship.SideId ?? -1,
                            ShipId = shipId,
                            PilotType = PlayerInfo.PilotTypePlayer,
                        }.Encode(),
                    }));
                    Console.WriteLine($"[playerinfo] peer {from} '{req.CharacterName}' -> PLAYERINFO(10) ship {shipId} iSide {live.Ship.SideId?.ToString() ?? "(none)"} (side assignment for the direct-connect ship)");

                    // FM_S_SET_MONEY(115): tell the joining/syncing player the
                    // side's money (P118). Native fires this inside
                    // CFSMission::SendMissionInfo (fsmission.cpp:4270-4299),
                    // part of AddPlayerToSide's "tell the joiner about everyone
                    // else" sync, one SET_MONEY per teammate with that
                    // teammate's per-player wallet. This port tracks ONE pooled
                    // number per side (Model/Side.cs:52 — no per-player
                    // wallets), so the honest port is a single message carrying
                    // the side's shared treasury, addressed with the joining
                    // player's own shipID (the only real ship in this model).
                    // Placed right after PLAYERINFO, the point where the player
                    // is definitively seated on a side.
                    if (live.Ship.SideId is { } setMoneySideId)
                    {
                        var smSide = _missions[0].Sides.FirstOrDefault(s => s.Id == setMoneySideId);
                        if (smSide is not null)
                        {
                            SendPeer(from, FedPacket.Pack(new[]
                            {
                                new SetMoney { Money = (int)smSide.Money, ShipId = shipId }.Encode(),
                            }));
                            Console.WriteLine($"[setmoney] peer {from} '{req.CharacterName}' -> SET_MONEY(115) money={smSide.Money} shipID={shipId}");
                        }
                    }

                    // FM_S_CLUSTERINFO(200): the map-overview message the
                    // reference server sends in SendMissionInfo IMMEDIATELY
                    // BEFORE the cluster EXPORT loop (fsmission.cpp:4124-4166,
                    // then :4170). It is what feeds the native Wopr client's
                    // GameInfo.Clusters (StrategyBase.cs:172:
                    // GameInfo.Clusters = message.Clusters) and therefore
                    // GetUnexploredClusters() (GameInfo.cs:69-92), which is
                    // currently always empty because this message was never
                    // sent — so ScoutExploreMap's smart branches (nearest-
                    // unexplored-warp/cluster, ScoutExploreMap.cs:577-650)
                    // never engaged and every bot fell back to the random-warp
                    // pick (:653-662). Placed here, right after PLAYERINFO and
                    // right before the cluster EXPORT burst, reproduces the
                    // reference order exactly (CLUSTERINFO before EXPORT) with
                    // no impact on the P33/P35 export-ordering invariants that
                    // this block documents: CLUSTERINFO is pure passive data
                    // the client stores (GameInfo.Clusters), it touches neither
                    // the mission side-membership path (PLAYERINFO) nor the
                    // m_pCoreIGC cluster pool that SET_CLUSTER resolves — those
                    // still come from the EXPORT burst below, so ORDERING of
                    // THAT burst is untouched. Built from the same Sectors the
                    // EXPORT burst iterates; dangling warp destinations are
                    // skipped and logged, never fabricated (ResolvedWarp.cs:28-32).
                    // P48: cached once (mission-wide, never changes) rather than
                    // re-encoded per peer/per resend -- also reused by
                    // BroadcastShipStates' periodic refresh below.
                    var clusterInfo = ClusterInfo.FromSectors(_missions[0].Sectors);
                    _clusterInfoBytes ??= clusterInfo.Encode();
                    SendPeer(from, FedPacket.Pack(new[] { _clusterInfoBytes }));
                    _lastClusterInfoTick[from] = TickIndex;
                    Console.WriteLine($"[clusterinfo] peer {from} '{req.CharacterName}' -> CLUSTERINFO(200) {clusterInfo.Clusters.Count} cluster(s), {clusterInfo.Warps.Count} warp(s)");

                    // FM_S_EXPORT(26)/OT_cluster(13): convey the MAP's sectors
                    // to the client. Placement (P33, moved here from right
                    // after JOINED_MISSION): the PLAYERINFO send just above
                    // moves the local player off SIDE_TEAMLOBBY onto its real
                    // side. On the native client that runs
                    // BaseClient::AddPlayerToSide (clintlib.cpp:4101), which —
                    // because this is the player's OWN PlayerInfo
                    // (pPlayerInfo == m_pPlayerInfo) — calls
                    // BaseClient::OnQuitSide() first (clintlib.cpp:4112-4115,
                    // matching the "leaving SIDE_TEAMLOBBY" half of the
                    // transition even on the very first join). OnQuitSide
                    // unconditionally nukes every registered cluster
                    // (clintlib.cpp:3781-3783: SetViewCluster(NULL);
                    // m_pCoreIGC->ResetMission(), which iterates
                    // GetClusters() and Terminate()s every one). Exporting
                    // clusters BEFORE PLAYERINFO (the original placement)
                    // meant they were all silently wiped moments later —
                    // confirmed directly via native P33dbg instrumentation
                    // (a live cluster-count probe read 13 right after the
                    // exports, then 0 by the time SET_CLUSTER ran) — which is
                    // what made m_pCoreIGC->GetCluster(sectorID)
                    // (appmsg.cpp:1006) return NULL and crash the very next
                    // line (pcluster->GetLastUpdate(), appmsg.cpp:1041, no
                    // null check) before pship->SetCluster(pcluster) could
                    // ever run — the actual, final blocker behind flyingF()
                    // (clintlib.h:1198) never becoming true. Sending the
                    // exports after PLAYERINFO's OnQuitSide instead means
                    // there is nothing left to wipe. We export ALL sectors
                    // (the reference approach, a trivial loop) rather than
                    // just the peer's starting sector, so the client can
                    // resolve any cluster id a later message references and
                    // there is no "which one" selection logic to get wrong;
                    // ~92 bytes × N sectors is negligible locally. Must land
                    // BEFORE SET_CLUSTER, below.
                    var clusterExports = _missions[0].Sectors
                        .Select(s => ExportCluster.FromSector(s).Encode())
                        .ToArray();
                    SendPeer(from, FedPacket.Pack(clusterExports));
                    Console.WriteLine($"[export] peer {from} '{req.CharacterName}' -> {clusterExports.Length} cluster(s): " +
                                      string.Join(", ", _missions[0].Sectors.Select(s => $"{s.Id}:'{s.Name}'{(s.Cluster.HomeSector ? "*" : "")}")));

                    // Same peer/session, immediately after the cluster exports,
                    // per sector, in the reference server's own order
                    // (fsmission.cpp:4170-4222): cluster → warps → stations.
                    // This is what finally gives the native client's
                    // FindTarget (src/Igc/common.cpp:470, station search at
                    // :500-501, adjacent-cluster warp walk at :677-707)
                    // non-empty GetWarps()/GetStations() to search, closing the
                    // P34 finding that FindTarget always returned NULL because
                    // the client's clusters had empty lists. Asteroids ARE
                    // exported (P64) — the map is m2xspiderw.igc, 134 real
                    // rocks — since the client's UndockMinersAndStartMining
                    // (CommanderResearchAndExpand.cs:1563-1568) searches
                    // GetAsteroids() for He3-named rocks. Treasure, mines and
                    // probes are intentionally skipped (none exist in this
                    // map / this codebase's scope), matching the task.
                    //
                    // Unconditional — no SeenBySide()/fog-of-war gate, exactly
                    // as P30 reasoned for clusters (Server.cs:742-747): this
                    // codebase has no scouting/visibility system, so export
                    // every warp, station and asteroid of every sector to every
                    // peer. Ships' resolved destination sectors come for free:
                    // the warp's own ClusterId addresses its sector, and all
                    // sectors are already exported above.
                    foreach (var sector in _missions[0].Sectors)
                    {
                        foreach (var resolved in sector.Warps)
                            SendPeer(from, FedPacket.Pack(new[] { ExportWarp.FromWarp(resolved).Encode() }));
                        foreach (var station in sector.Stations)
                            SendPeer(from, FedPacket.Pack(new[] { ExportStation.FromStation(station).Encode() }));
                        foreach (var asteroid in sector.Asteroids)
                            SendPeer(from, FedPacket.Pack(new[] { ExportAsteroid.FromAsteroid(asteroid).Encode() }));
                    }
                    Console.WriteLine($"[export] peer {from} '{req.CharacterName}' -> {_missions[0].Warps.Count} warp(s), {_missions[0].Sectors.Sum(s => s.Stations.Count)} station(s), {_missions[0].Sectors.Sum(s => s.Asteroids.Count)} asteroid(s) (OT_warp 7 / OT_station 1 / OT_asteroid 5)");

                    var set = new SetCluster
                    {
                        SectorId = live.Sector.Id,
                        Ship = ServerShipUpdate.FromModel(shipId, live.Ship, (uint)TickIndex),
                        Cookie = (byte)ack.Cookie,
                    };
                    SendPeer(from, FedPacket.Pack(new[] { set.Encode() }));

                    // FM_S_BUY_LOADOUT_ACK(5): after the client has been told
                    // its object (EXPORT 26) and its launch (SET_CLUSTER 7),
                    // adopt the real hull onto the client's own ship pointer.
                    // The C++ client handler (appmsg.cpp:3180-3183) calls
                    // m_ship->ProcessShipLoadout(...), which for a no-hull
                    // dummy (m_myHullType.GetHullType()==NULL) fires the
                    // "hull changed" branch (shipIGC.h:888-897) and calls
                    // SetBaseHullType(GetMyMission()->GetHullType(hullID)),
                    // making GetBaseHullType()/GetPosition() stop throwing NRE
                    // — the transition the real server achieves when a player
                    // buys a loadout (FedSrv.CPP:902-928 ProcessConnect starts
                    // every fresh player on a hullID=NA dummy too; the buy is
                    // the step that gives it a hull).
                    //
                    // nParts=0 simplification (unchanged from P26/P27): the
                    // ShipLoadout carries only the 2-byte hullID, no
                    // ExpandedPartData[] tail. Real per-part purchase is the
                    // deferred BUY_LOADOUT/economy machinery.
                    //
                    // fBoughtEverything=false, fLaunch=false: we assign a hull
                    // but buy no parts (honest), and SET_CLUSTER has already
                    // launched the ship before this ACK, so no further launch
                    // messages will follow (claiming fLaunch would be false).
                    // fLaunch=false also releases any client-side lockdown via
                    // EndLockDown (appmsg.cpp:3180-3181); for a direct-connect
                    // bot IsLockedDown() is false anyway, making it a no-op.
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new BuyLoadoutAck
                        {
                            HullId = live.Ship.HullType.HullId,
                            FBoughtEverything = false,
                            FLaunch = false,
                        }.Encode(),
                    }));

                    // FM_S_MISSION_STAGE(37): the message this whole join burst
                    // was missing (P37, 2026-08-31). The native client's entire
                    // per-tick simulation -- CmissionIGC::Update
                    // (missionigc.cpp:3133-3134) and the per-cluster ship
                    // plot/execute loop (clusterIGC.cpp:103-231, PlotShipMove/
                    // ExecuteShipMove) -- is a no-op until the client's own core
                    // mission stage is STAGE_STARTED/STAGE_STARTING. The client's
                    // only path there outside the lobby (which a direct-connect
                    // client never sees) is this message
                    // (appmsg.cpp:2314-2349): case STAGE_STARTED calls
                    // SetInProgress(true) + m_pCoreIGC->SetMissionStage(STAGE_STARTED),
                    // unblocking both gates. This server already tracks
                    // Mission.Stage = Started internally (see the mission
                    // construction above) but never told the client -- confirmed
                    // via a live capture showing zero fmid=37 frames and three
                    // native probes (GotoPlan::Execute/ExecuteShipMove/
                    // CclusterIGC::Update) that never fired even with a correct
                    // ship position and a resolved goto target. Sent last, after
                    // everything the simulation needs (mission, sides, ship,
                    // clusters, warps, stations, launch) already exists.
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        new MissionStageMessage { Stage = MissionStageMessage.StageStarted }.Encode(),
                    }));
                    Console.WriteLine($"[stage] peer {from} '{req.CharacterName}' -> MISSION_STAGE(37) Started");

                    // FM_S_SHIP_STATUS(90): the message ScoutExploreMap's whole
                    // autonomous sweep/exploration is gated on (P39 finding —
                    // <c>src/Wopr/Wopr/Strategies/ScoutExploreMap.cs:85-221</c>).
                    // The client only acts on a status for a ship it has a
                    // PLAYERINFO record for (appmsg.cpp:2719, FindPlayer gate),
                    // which this server sends only for a peer's OWN ship — so
                    // this is correctly scoped to each peer's own ship. Sent
                    // after MISSION_STAGE so the client's mission is running and
                    // its ship is in a sector (the handler returns early if
                    // GetCluster()==null, ScoutExploreMap.cs:96-97), which this
                    // is: SET_CLUSTER already launched it into live.Sector.
                    //
                    // Simplifications (this repo's honesty-about-simplifications
                    // style): no scouting/visibility system exists, so
                    // Unknown=false and Detected=true always — there is no
                    // scanning state to make them meaningful yet (matching the
                    // simplified server's "no detection system" stance). State
                    // is c_ssFlying with StationID/ParentID NA because this
                    // server has no docking — ships are born flying in a sector.
                    SendPeer(from, FedPacket.Pack(new[]
                    {
                        MakeOwnShipStatus(shipId, live).Encode(),
                    }));
                    Console.WriteLine($"[shipstatus] peer {from} '{req.CharacterName}' -> SHIP_STATUS(90) ship {shipId} sector {live.Sector.Id}");
                    _lastStatusSector[from] = live.Sector.Id;
                    _lastStatusTick[from] = TickIndex;
                }
            }
            else if (fmid == ShipUpdate.Fmid)
            {
                // Step 3: inbound control input. Decode and, for the peer's own
                // ship, ONLY set the ship's Controls/StateM. Nothing moves here —
                // the ship's Motion lives in Mission.Update (the authoritative
                // tick). Running on the sim thread (Drain), so setting controls
                // is race-free w.r.t. the tick loop, and the payload's
                // position/velocity/orientation echoes are ignored as authority.
                try
                {
                    var u = ShipUpdate.Decode(m);
                    if (_peerShips.TryGetValue(from, out var live))
                    {
                        // A boarded gunner's orientation/controls are driven by
                        // ACTIVE/INACTIVE_TURRET_UPDATE(99/100), not its normal
                        // SHIP_UPDATE — native gates the same message on
                        // `pship->GetParentShip() == NULL` (FedSrv.CPP:4859), and
                        // a real gunner client sends 99/100 instead of 24
                        // (clintlib.cpp:1884-1899). Mirror that gate: ignore a
                        // boarded ship's SHIP_UPDATE so its state can't be
                        // overwritten by the normal path.
                        if (live.Ship.ParentShip is not null)
                        {
                            Console.WriteLine($"[ctrl] {from} ship {live.Ship.Id}: boarded gunner — SHIP_UPDATE ignored, use ACTIVE_TURRET_UPDATE (FedSrv.CPP:4859)");
                        }
                        else
                        {
                            live.Ship.Controls = u.Payload.Controls;
                            live.Ship.StateM = u.Payload.StateM;
                            var c = u.Payload.Controls;
                            Console.WriteLine($"[ctrl] {from} ship {live.Ship.Id} controls=(yaw {c.Yaw:F3} pitch {c.Pitch:F3} " +
                                              $"roll {c.Roll:F3} thr {c.Throttle:F3}) state=0x{u.Payload.StateM:X}");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[ctrl] {from}: ship update for a peer with no ship — ignored");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SHIP_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == ActiveTurretUpdate.Fmid)   // FM_C_ACTIVE_TURRET_UPDATE (Messages.h:673-676)
            {
                // P109 — a turret gunner actively firing the turret, sent
                // INSTEAD of SHIP_UPDATE(24) while it controls the turret
                // (clintlib.cpp:1884-1893). Routes to the real
                // ProcessShipUpdate(const ClientActiveTurretUpdate&) port
                // (shipIGC.cpp:3349-3392) on the gunner's own ship, gated on the
                // sender actually being a boarded gunner (FedSrv.CPP:4873-4875).
                try
                {
                    var u = ActiveTurretUpdate.Decode(m);
                    if (_peerShips.TryGetValue(from, out var live))
                        HandleActiveTurretUpdate(live.Ship, u.Atu);
                    else
                        Console.WriteLine($"[turret] {from}: no ship — ACTIVE_TURRET_UPDATE ignored");
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad ACTIVE_TURRET_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == InactiveTurretUpdate.Fmid)  // FM_C_INACTIVE_TURRET_UPDATE (Messages.h:678-679)
            {
                // P109 — the gunner stops actively controlling/firing the turret
                // (sent when StateM drops weaponsMaskIGC, clintlib.cpp:1894-1899).
                // Clears the same "shooting" state bit ACTIVE_TURRET_UPDATE set.
                try
                {
                    _ = InactiveTurretUpdate.Decode(m);   // no fields; decode validates cbmsg=4
                    if (_peerShips.TryGetValue(from, out var live))
                    {
                        var gunner = live.Ship;
                        if (gunner.Sector is not null && gunner.ParentShip is not null && gunner.TurretId is not null)
                        {
                            // Native server-side stop-shooting: SetInactiveTurretUpdate
                            // → GetIGCShip()->SetStateM(0) (fsship.cpp:1223) — a full
                            // assignment to 0, clearing the weaponsMaskIGC "shooting"
                            // bit (and every other state bit) exactly as native does.
                            gunner.StateM = 0;
                            Console.WriteLine($"[turret] ship {gunner.Id} INACTIVE_TURRET_UPDATE — SetStateM(0) (stop shooting, fsship.cpp:1223)");
                        }
                        else
                        {
                            Console.WriteLine($"[turret] {from}: INACTIVE_TURRET_UPDATE from a non-gunner — ignored (FedSrv.CPP:4887-4889)");
                        }
                    }
                    else
                    {
                        Console.WriteLine($"[turret] {from}: no ship — INACTIVE_TURRET_UPDATE ignored");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad INACTIVE_TURRET_UPDATE from {from}: {e.Message}");
                }
            }
            else if (fmid == ChatMessage.Fmid)
            {
                // Step: relay an inbound FM_CS_CHATMESSAGE(23). Decode, resolve
                // the sender's ship (and thus its SideId — P21 gave ships a real
                // SideId), then forward the ORIGINAL bytes to the peers that the
                // target selects. Only two targets are wired: Everyone (every
                // connected peer, mirroring BroadcastShipStates' loop over
                // _peerShips) and Team (peers whose SideId matches the sender's,
                // reusing the side field rather than reinventing comparison).
                // Any other ChatTarget is reported and dropped — not broadcast
                // as a leniency fallback (that would be a real behavior bug) and
                // not thrown (an unimplemented target from a client must not
                // crash the server). Matches README's "report and ignore" rule.
                try
                {
                    var chat = ChatMessage.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var senderLive))
                    {
                        Console.WriteLine($"[chat] {from}: chat from a peer with no ship — ignored");
                    }
                    else
                    {
                        var targets = SelectChatTargets(from, chat, senderLive.Ship.SideId);
                        Console.WriteLine($"[chat] {from} ship {senderLive.Ship.Id} side {sideStr(senderLive.Ship.SideId)} '{chat.Message}' " +
                                          $"target {chat.ChatTarget} -> {targets.Length} peer(s)");
                        if (targets.Length > 0)
                        {
                            byte[] packet = FedPacket.Pack(new[] { m });
                            foreach (var t in targets) SendPeer(t, packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHATMESSAGE from {from}: {e.Message}");
                }
            }
            else if (fmid == ChatBuoy.Fmid)
            {
                // Step: relay an inbound FM_CS_CHATBUOY(108) — the message
                // BaseClient::SendChat actually uses for a chat targeting a buoy
                // (clintlib.cpp:2432-2444: otTarget == OT_buoy → CHATBUOY, else
                // CHATMESSAGE). The commander's real dispatch to a docked miner
                // (CommanderResearchAndExpand.cs:1610,1615-1618 passes
                // otTarget = buoy.GetObjectType(), oidTarget = buoy.GetObjectID())
                // therefore arrives HERE, not as CHATMESSAGE(23) — previously
                // logged "[wire] unsupported fmid 108 — ignored" and silently
                // dropped. Same routing as CHATMESSAGE: decode, resolve the
                // sender's ship/SideId, then call the exact same
                // SelectChatTargets method (its parameter is now the shared
                // IChatRoutingTarget, so no duplicated switch), and forward the
                // ORIGINAL bytes to whoever it selects (verbatim relay, matching
                // CS_PING's echo and ChatMessage's own relay, Server.cs:1281).
                try
                {
                    var buoy = ChatBuoy.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var senderLive))
                    {
                        Console.WriteLine($"[chatbuoy] {from}: chat from a peer with no ship — ignored");
                    }
                    else
                    {
                        var targets = SelectChatTargets(from, buoy, senderLive.Ship.SideId);
                        Console.WriteLine($"[chatbuoy] {from} ship {senderLive.Ship.Id} side {sideStr(senderLive.Ship.SideId)} " +
                                          $"'{buoy.Message}' target {buoy.ChatTarget} buoy {buoy.Db.BuoyId} -> {targets.Length} peer(s)");
                        if (targets.Length > 0)
                        {
                            byte[] packet = FedPacket.Pack(new[] { m });
                            foreach (var t in targets) SendPeer(t, packet);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad CHATBUOY from {from}: {e.Message}");
                }
            }
            else if (fmid == Docked.Fmid)
            {
                // Step: handle an inbound FM_C_DOCKED(48). The native handler
                // (FedSrv.CPP:7031-7135) is genuinely dual-purpose — read the
                // whole block — and this branch now mirrors both halves:
                //  • stationID == NA  → the pure-undock announce, already
                //    ported (PerformUndock) and preserved exactly as-is.
                //  • stationID != NA  → the teleport/relocation dock request
                //    (P110): a displaced ship lands at a specific friendly
                //    restart-capable station, acknowledged with
                //    FM_S_TELEPORT_ACK(102). Native gating (all in
                //    FedSrv.CPP:7040-7077): only a ship the server believes is
                //    already docked somewhere (GetStation()!=NULL, :7040) and
                //    that is not a boarded child (pshipParent==NULL, :7045)
                //    participates; a non-NA request names a station that must
                //    be real, on the player's side or an ally's, and
                //    c_sabmRestart-capable — else the ACK answers stationID=NA
                //    (rejected, :7068) and the ship stays put. The old
                //    "obsolete half dropped" handling is gone: this is exactly
                //    the legit-request direction Messages.h:402-404 calls
                //    obsolete *as a general client-dock trigger*, but the
                //    server still answers a real one (the "port the responder,
                //    defer the trigger" discipline).
                try
                {
                    var docked = Docked.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var undockLive))
                    {
                        Console.WriteLine($"[undock] {from}: dock message from a peer with no ship — ignored");
                    }
                    else if (undockLive.Ship.StationId is null)
                    {
                        Console.WriteLine($"[undock] {from} ship {undockLive.Ship.Id}: not docked server-side — dock message ignored (gate: GetStation()!=NULL, FedSrv.CPP:7040)");
                    }
                    else if (docked.StationId != -1)
                    {
                        // P110 — the teleport/relocation dock request.
                        PerformTeleport(from, undockLive, docked.StationId);
                    }
                    else
                    {
                        PerformUndock(from, undockLive);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad DOCKED from {from}: {e.Message}");
                }
            }
            else if (fmid == 35) // FM_CS_PING (Messages.h:280-283)
            {
                // Native's own comment is the entire spec: "just send it back
                // to client as soon as you get it." No new codec needed --
                // the client only cares that it gets a well-formed echo of
                // the exact message it sent, so re-pack the raw bytes
                // unmodified rather than decoding/re-encoding timeClient/
                // timeServer/fmg (whose exact native byte widths aren't
                // worth guessing at when a verbatim echo already satisfies
                // the spec precisely). This is CommanderResearchAndExpand's
                // own ~5s heartbeat (its AttachMessages comment: "drive
                // infrequent updates") -- UndockMinersAndStartMining and
                // QueueTechForAvailableMoney are only ever called from this
                // message's client-side handler, so without this echo those
                // never fire at all, regardless of anything else being
                // correct server-side.
                SendPeer(from, FedPacket.Pack(new[] { m }));
            }
            else if (fmid == BuyLoadoutRequest.Fmid)   // FM_C_BUY_LOADOUT (Messages.h:65-68)
            {
                // The client's requested loadout (hull + parts) — this message
                // was not received at all before P61. Decode, resolve against
                // the peer's mission core, apply to the ship's generalized
                // loadout, and answer with a real FM_S_BUY_LOADOUT_ACK carrying
                // the actual mounted-part tail (nParts = real count, not 0).
                try
                {
                    var buy = BuyLoadoutRequest.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var buyLive))
                    {
                        Console.WriteLine($"[loadout] {from}: BUY_LOADOUT from a peer with no ship — ignored");
                    }
                    else
                    {
                        ApplyLoadout(from, buyLive, buy);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad BUY_LOADOUT from {from}: {e.Message}");
                }
            }
            else if (fmid == PlayerReady.Fmid)   // FM_CS_PLAYER_READY (Messages.h:380-384)
            {
                // P84: "Sent to everyone on team" (the native message's own
                // header comment) -- the client's own handler
                // (appmsg.cpp:2297-2312) only updates PlayerInfo.fReady and
                // fires the roster-changed sink when this message is
                // RECEIVED; a click on the Team Lobby's own JOIN control
                // never applies locally, it waits for this relay. Before this,
                // the server logged "unsupported fmid 45" and the click
                // appeared completely dead, live-confirmed on the real
                // client (P84). Relay the original bytes to every peer on the
                // sender's side, including the sender itself (its own client
                // is waiting on the same relay to update its own roster
                // entry) -- same shape as ChatTarget.Team in SelectChatTargets.
                try
                {
                    var ready = PlayerReady.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var readyLive))
                    {
                        Console.WriteLine($"[ready] {from}: PLAYER_READY from a peer with no ship — ignored");
                    }
                    else
                    {
                        var targets = _peerShips
                            .Where(kv => kv.Value.Ship.SideId == readyLive.Ship.SideId)
                            .Select(kv => kv.Key)
                            .ToArray();
                        byte[] packet = FedPacket.Pack(new[] { m });
                        foreach (var t in targets) SendPeer(t, packet);
                        Console.WriteLine($"[ready] {from} ship {ready.ShipId} side {sideStr(readyLive.Ship.SideId)} fReady={ready.FReady} -> {targets.Length} peer(s)");
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad PLAYER_READY from {from}: {e.Message}");
                }
            }
            else if (fmid == Suicide.Fmid)   // FM_C_SUICIDE (Messages.h:590-591)
            {
                // The client's suicide request (P103). Decode is trivially empty
                // (header-only message); the real work is the gated death in
                // HandleSuicide. A malformed frame is reported and dropped like
                // every other inbound handler — never crashes the server.
                try
                {
                    Suicide.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var live))
                    {
                        Console.WriteLine($"[suicide] {from}: SUICIDE from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleSuicide(from, live);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad SUICIDE from {from}: {e.Message}");
                }
            }
            else if (fmid == ViewClusterRequest.Fmid)   // FM_C_VIEW_CLUSTER (Messages.h)
            {
                // The docked-spectator/map-view request (P106): a docked player
                // previews another cluster. Gate and reply mirror the native
                // handler (FedSrv.CPP:6420-6454) — only reply when docked
                // (GetStation()!=NULL), only to the requesting peer. The real
                // work (cluster/target resolution, visibility gate, reply) is
                // in HandleViewCluster. A malformed frame is reported and
                // dropped like every other inbound handler.
                try
                {
                    var req = ViewClusterRequest.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var reqLive))
                    {
                        Console.WriteLine($"[viewcluster] {from}: VIEW_CLUSTER from a peer with no ship — ignored");
                    }
                    else if (reqLive.Ship.StationId is null)
                    {
                        Console.WriteLine($"[viewcluster] {from} ship {reqLive.Ship.Id}: not docked — ignored (gate: GetStation()!=NULL, FedSrv.CPP:6421)");
                    }
                    else
                    {
                        HandleViewCluster(from, reqLive, req);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad VIEW_CLUSTER from {from}: {e.Message}");
                }
            }
            else if (fmid == BoardShip.Fmid)   // FM_C_BOARD_SHIP (Messages.h:686-688)
            {
                // The client's board/leave request (P108): dual-purpose per
                // native (FedSrv.CPP:6385-6418) — sidParent == NA leaves the
                // current parent, any other value boards that ship. Only a
                // FAILURE sends a BOARD_NACK to the requester alone; success
                // rides the existing SHIP_STATUS periodic refresh (via
                // MakeOwnShipStatus). The real work (turret-slot search,
                // observer gate, money refund, parent/child/turret state
                // transition) is in HandleBoardShip. A malformed frame is
                // reported and dropped like every other inbound handler.
                try
                {
                    var board = BoardShip.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var boardLive))
                    {
                        Console.WriteLine($"[boardship] {from}: BOARD_SHIP from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleBoardShip(from, boardLive, board);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad BOARD_SHIP from {from}: {e.Message}");
                }
            }
            else if (fmid == RipcordRequest.Fmid)   // FM_C_RIPCORD_REQUEST (Messages.h:951-953)
            {
                // The client's ripcord request (P111): start, re-target, or
                // cancel a ripcord countdown toward a chosen sector. Gates and
                // state machine in HandleRipcordRequest / RequestRipcord —
                // the port of FedSrv.CPP:7715-7728 and FedSrv.CPP:13319-13378.
                try
                {
                    var req = RipcordRequest.Decode(m);
                    if (!_peerShips.TryGetValue(from, out var ripLive))
                    {
                        Console.WriteLine($"[ripcord] {from}: RIPCORD_REQUEST from a peer with no ship — ignored");
                    }
                    else
                    {
                        HandleRipcordRequest(from, ripLive, req);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[wire] bad RIPCORD_REQUEST from {from}: {e.Message}");
                }
            }
            else
            {
                Console.WriteLine($"[wire] from {from}: unsupported fmid {fmid} — ignored at headless milestone");
            }

            static string sideStr(short? s) => s is { } v ? v.ToString() : "(none)";
        }
    }

    /// <summary>
    /// Handle a client <c>FM_C_SUICIDE(80)</c> for a peer that has a ship. This
    /// is the P103 port of <c>FedSrv.CPP:6335-6350</c> <c>case
    /// FM_C_SUICIDE</c>:
    /// <list type="bullet">
    /// <item><b>Gate: mission started.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:6337</c>). We keep it — <see cref="Ship.Mission"/>
    /// must exist and be at <see cref="MissionStage.Started"/>.</item>
    /// <item><b>Gate: the ship is actually flying.</b> The native refuses
    /// unless <c>GetCluster() != NULL</c> (<c>FedSrv.CPP:6338</c>) — i.e. the
    /// ship is in a sector, not parked. In this port a placed-but-docked ship
    /// retains <see cref="Ship.Sector"/> (the dock seam sets
    /// <see cref="Ship.StationId"/> but does not clear Sector), so
    /// "has a cluster" faithfully means placed <b>and</b> not docked:
    /// <c>Sector != null &amp;&amp; StationId == null</c>. Docked/off-sector
    /// ships are a no-op — no kill, no broadcast — matching the native gate.</item>
    /// <item><b>Deliberate relaxation (documented, not a silent drop): the
    /// child-ship / cheat gate collapses.</b> Native also requires
    /// <c>GetParentShip() != NULL</c> (a boarded/turret gunner) <em>or</em>
    /// <c>CanCheat()</c> (admin). This port has <em>neither</em> — no
    /// boarding/child-ship concept (Phase 3 cluster E) and no admin/cheat
    /// concept at all. Following the P100 precedent (a gate collapses to
    /// always-true when its own prerequisite doesn't exist yet), any placed
    /// ship may suicide. Revisit only when boarding lands.</item>
    /// <item><b>Kill path reused, not rebuilt.</b> Triggering uses the exact
    /// seam a combat death's lethal branch raises
    /// (<c>Ship.ReceiveDamage</c> → <c>Mission.Site.KillShipEvent</c>,
    /// <c>Ship.cs:650</c>) — a direct <c>KillShipEvent</c> with a NULL
    /// launcher, precisely what the native suicide does (launcher NULL,
    /// amount 1.0). No parallel kill mechanism is introduced; the server's
    /// <see cref=\"ServerHost.ShipKilled\"/> wiring therefore broadcasts the
    /// usual <c>KILL_SHIP(105)</c> and the new <c>SHIP_DELETE(20)</c>
    /// alongside. As in native, the suicide itself does not drain the hull —
    /// the death notice is what a suicide produces.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch is drained inside
    /// <see cref=\"Tick\"/>, like every other inbound handler), so it may call
    /// the kill seam and touch <c>_peerShips</c> without extra locking.
    /// </summary>
    private void HandleSuicide(ConnectionId from, LiveShip live)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[suicide] {from} ship {ship.Id}: " +
                              $"{(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — refused");
            return;
        }
        if (ship.Sector is null || ship.StationId is not null)
        {
            Console.WriteLine($"[suicide] {from} ship {ship.Id}: " +
                              $"{(ship.Sector is null ? "no cluster" : "docked")} — refused (gate: GetCluster(), FedSrv.CPP:6338)");
            return;
        }
        // Relaxed child/cheat gate (see doc comment): ANY placed ship may suicide.
        Console.WriteLine($"[suicide] {from} ship {ship.Id} — re-triggering the combat kill path (KillShipEvent, launcher NULL)");
        mission.Site.KillShipEvent(mission.LastUpdate, ship, null, 1.0f, ship.Position, Vector3.Zero);
    }

    /// <summary>
    /// Pick the peers a chat message is relayed to, per the five wired targets.
    /// <b>Everyone</b> (0) → every connected peer ship (the same loop the ship-
    /// state broadcast walks, Server.cs:305-323). <b>Team</b> (5) → only peers
    /// whose <see cref="Ship.SideId"/> equals the sender's. <b>Individual</b> /
    /// <b>Individual_NoFilter</b> (6,7) → only the peer ship whose
    /// <see cref="Ship.Id"/> equals <see cref="ChatMessage.OidRecipient"/> and,
    /// like Team, only if that ship shares the sender's side — the real
    /// <c>ForwardChatMessage</c> (<c>FedSrv.CPP:3538-3556</c>) treats both
    /// targets identically in one case block and additionally allows allied
    /// sides, but this codebase has no alliance system, so same-side-only is
    /// the correct simplification (matching Team's precedent); the
    /// "NoFilter" distinction is about bypassing mute/ignore lists, and this
    /// server has none, so the two targets collapse into one case. <b>AllSector</b>
    /// (10) → every peer whose ship is currently in the sector named by
    /// <see cref="ChatMessage.OidRecipient"/> (the client picks the sector —
    /// <c>GetCluster(pcd->oidRecipient)</c>, <c>FedSrv.CPP:3428-3441</c>),
    /// regardless of side. <b>FriendlySector</b> (11) → the same sector
    /// membership but only peers sharing the sender's side (<c>FedSrv.CPP:3392-3424</c>;
    /// allied-side allowance in the real code reduced to same-side here).
    /// <b>Group</b> / <b>GroupNoEcho</b> (12,13) → aliases for Individual, per
    /// the real server's rewrite-and-fall-through (<c>FedSrv.CPP:3531-3536</c>).
    /// Every other <see cref="ChatMessage.ChatTarget"/> value is logged plainly
    /// and yields no targets — we refuse to guess a routing behavior for
    /// leader/admin/wing modes this codebase can't express (Leaders needs a
    /// team-leader election, Admin is literally <c>//NYI</c> in the shipped real
    /// server, Wing needs a wing model — see README's chat section), and we do
    /// not fall back to a broad broadcast (a real behavior bug disguised as
    /// leniency) nor throw (an unimplemented target must not take the server
    /// down — README's "report and ignore" rule). Returns empty when nothing
    /// should receive it.
    /// </summary>
    private ConnectionId[] SelectChatTargets(ConnectionId from, IChatRoutingTarget chat, short? senderSideId)
    {
        switch (chat.ChatTarget)
        {
            case (int)ChatTarget.Everyone:      // AGCChat_Everyone = 0 (igc.h:2731)
                return _peerShips.Keys.ToArray();

            case (int)ChatTarget.Team:          // AGCChat_Team = 5 (igc.h:2733)
                return _peerShips
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)
                    .Select(kv => kv.Key)
                    .ToArray();

            // CHAT_INDIVIDUAL / CHAT_INDIVIDUAL_NOFILTER (igc.h:2733):
            // same behaviour here — collapse, don't build two paths for a
            // distinction that can't exist yet (no mute/ignore lists).
            case (int)ChatTarget.Individual:
            case (int)ChatTarget.IndividualNoFilter:      // FedSrv.CPP:3538-3556
                return _peerShips
                    .Where(kv => kv.Value.Ship.Id == chat.OidRecipient)
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)   // same-side-only simplification
                    .Select(kv => kv.Key)
                    .ToArray();

            // CHAT_GROUP / CHAT_GROUP_NOECHO: the real server rewrites these to
            // CHAT_INDIVIDUAL and falls through (FedSrv.CPP:3531-3536) — a
            // client-sent Group target is itself just an alias for Individual.
            // Match that exact fallthrough here.
            case (int)ChatTarget.Group:        // FedSrv.CPP:3531-3536
            case (int)ChatTarget.GroupNoEcho:
                goto case (int)ChatTarget.Individual;

            case (int)ChatTarget.AllSector:               // FedSrv.CPP:3428-3441
                return _peerShips
                    .Where(kv => kv.Value.Sector.Id == chat.OidRecipient)
                    .Select(kv => kv.Key)
                    .ToArray();

            case (int)ChatTarget.FriendlySector:          // FedSrv.CPP:3392-3424
                return _peerShips
                    .Where(kv => kv.Value.Sector.Id == chat.OidRecipient)
                    .Where(kv => kv.Value.Ship.SideId == senderSideId)
                    .Select(kv => kv.Key)
                    .ToArray();

            default:
                Console.WriteLine($"[chat] target {chat.ChatTarget} not implemented in this slice — message from {from} dropped");
                return Array.Empty<ConnectionId>();
        }
    }

    /// <summary>Creates and places a peer's ship, then registers the live record.</summary>
    private LiveShip AssignShip(LogonRequest req, short shipId)
    {
        var mission = _missions[0];
        var (ship, sector) = ShipSpawner is not null
            ? ShipSpawner(mission.Core, mission, shipId)
            : SpawnDefaultShip(mission, shipId);
        return new LiveShip(ship, sector, ship.Position);
    }

    /// <summary>Default spawn: first hull in the core (by id), placed at an
    /// x-offset staged by peer count so concurrent bots don't overlap, oriented
    /// straight ahead (identity) in the first sector of the first mission, and
    /// armed with the cheapest real weapon the core offers (see
    /// <see cref="MountCombatWeapon"/>).</summary>
    private (Ship Ship, Sector Sector) SpawnDefaultShip(Mission mission, short shipId)
    {
        if (mission.Core.HullTypes.Count == 0)
            throw new InvalidOperationException("core has no hull types to assign a ship");
        var hull = mission.Core.HullTypes.Values.OrderBy(h => h.HullId).First();
        float x = 720f * (1 + _peerShips.Count);
        var sector = mission.Sectors[0];
        var ship = new Ship(shipId, hull, new Vector3(x, 730f, 920f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(ship, sector);

        // Round-robin across mission.Sides as peers connect, so concurrent
        // bots split roughly evenly across teams (peer N → side N mod
        // count). Reuses the same peer counter that staggers the x-offset
        // above: _peerShips is populated only after AssignShip returns, so
        // Count here is the number of peers already seated. Nothing in the
        // config or LogonRequest carries a side preference (the reference
        // handles that in the lobby, out of scope), so this is the simplest
        // defensible policy. An empty side list (unexpected — validated at
        // boot, Server.cs:231-236, but not assumed) leaves the ship side-less
        // and logs the omission rather than fabricating one.
        if (mission.Sides.Count > 0)
        {
            var sideIdx = _peerShips.Count % mission.Sides.Count;
            ship.SideId = mission.Sides[sideIdx].Id;
            Console.WriteLine($"[spawn] ship {shipId} seated on side {ship.SideId} "
                + $"({mission.Sides[sideIdx].Name ?? $"side{ship.SideId}"})");
        }
        else
        {
            Console.WriteLine($"[spawn] ship {shipId}: mission has no sides — ship left with no side (a finding, not fabricated)");
        }

        MountCombatWeapon(ship);
        return (ship, sector);
    }

    /// <summary>
    /// Handle a client <c>FM_C_VIEW_CLUSTER(104)</c> for a <b>docked</b> peer —
    /// the P106 port of the native <c>FedSrv.CPP:6420-6454</c>
    /// <c>case FM_C_VIEW_CLUSTER</c>:
    /// <list type="bullet">
    /// <item><b>Gate on docked.</b> The dispatch branch already enforced
    /// <see cref="Ship.StationId"/> != null — the port's direct equivalent of the
    /// native <c>GetStation() != NULL</c> (<c>FedSrv.CPP:6421</c>). A flying ship
    /// never reaches this method (its request is a no-op, no reply).</item>
    /// <item><b>NA cluster = silent.</b> <c>clusterID != NA</c> gates the whole
    /// reply in native (<c>FedSrv.CPP:6423</c>); a request naming NA(-1) cluster
    /// gets no reply at all (native still calls <c>SetCluster(NULL)</c> after,
    /// but that persistence is deliberately not ported — see finding below).</item>
    /// <item><b>Target resolution.</b> When <c>otTarget != NA</c> the native
    /// resolves <c>pMission-&gt;GetModel(otTarget, oidTarget)</c>
    /// (<c>FedSrv.CPP:6428</c>) — this port's model list is the peer ships
    /// (plus sector-attached stations), resolved in
    /// <see cref="TryResolveViewTarget"/>.</item>
    /// <item><b>Reply shape.</b> Echo <c>clusterID</c> and, only when the target
    /// resolved to a real object in that cluster (<c>ptarget-&gt;GetCluster() ==
    /// pcluster</c>, <c>FedSrv.CPP:6430</c>) <b>and</b> visible to the requester's
    /// side (<c>ptarget-&gt;SeenBySide(pfsPlayer-&gt;GetSide())</c>,
    /// <c>FedSrv.CPP:6431</c>), the object's real position with
    /// <c>bUsePosition=true</c>; else bare cluster with <c>bUsePosition=false</c>.
    /// The visibility predicate reused here is this port's existing same-side
    /// gate (the P58 teammate broadcast's <c>tv.Value.Ship.SideId != recipientSide
    /// → skip</c>, <c>Server.cs:521</c>), applied to the target: an object with no
    /// side (a neutral world object) is visible; a side-carrying object is
    /// visible only to the same side — this port has no fog-of-war/scanner, so
    /// same-side is the faithful stand-in for native <c>SeenBySide</c>.</item>
    /// <item><b>Reply to requester only.</b> <c>SendPeer(from, …)</c> — the
    /// direct equivalent of <c>g.fm.SendMessages(pfsPlayer-&gt;GetConnection(), …)</c>
    /// (<c>FedSrv.CPP:6433-6434</c>); never a sector/mission broadcast.</item>
    /// </list>
    /// <para><b>Finding on the per-player "currently viewed cluster" state:</b>
    /// the native's trailing <c>pfsPlayer-&gt;SetCluster(pcluster, true)</c>
    /// (<c>FedSrv.CPP:6453</c>) persists a per-player viewed-cluster. Searching
    /// <c>FedSrv.CPP</c>'s native call sites, <b>no other handler reads that
    /// state back</b> — it is written only here and consumed nowhere else in
    /// server code (its consumer would be the client, if anything). So this
    /// port implements <c>VIEW_CLUSTER</c> as a pure request/reply with no new
    /// server-side per-player state, and says so; nothing in the wire protocol
    /// depends on the server remembering what the client is currently viewing.
    /// Confirmed for this task (details and rationale in VIEW_CLUSTER_P106.md).</para>
    /// Runs on the sim thread (the dispatch is drained inside <see cref="Tick"/>,
    /// like every other inbound handler), so it may read <c>_peerShips</c> and
    /// touch <c>SendPeer</c> without extra locking.
    /// </summary>
    private void HandleViewCluster(ConnectionId from, LiveShip live, ViewClusterRequest req)
    {
        var mission = _missions[0];

        // clusterID == NA → native resolves pcluster = NULL and sends NO reply
        // (the whole reply block sits inside the `!= NA` guard). Mirror exactly.
        if (req.ClusterId == -1)
        {
            Console.WriteLine($"[viewcluster] {from} ship {live.Ship.Id}: clusterID=NA — no reply (native FedSrv.CPP:6423)");
            return;
        }

        var requesterSide = live.Ship.SideId;
        var pcluster = mission.Sectors.FirstOrDefault(s => s.Id == req.ClusterId);

        // Target resolution + the native's own two-gate check:
        //   ptarget && ptarget->GetCluster() == pcluster && ptarget->SeenBySide(requesterSide)
        //   (FedSrv.CPP:6429-6432). Resolution is the port's GetModel;
        //   GetCluster()==pcluster maps to "the object's owning sector";
        //   SeenBySide maps to the same-side predicate (finding above).
        bool bUsePosition = false;
        Vector3 targetPos = Vector3.Zero;
        if (req.OtTarget != -1 && pcluster is not null
            && TryResolveViewTarget((IgcObjectType)req.OtTarget, req.OidTarget, out targetPos, out var objectSector, out var objectSide)
            && objectSector == pcluster
            && SeenByRequesterSide(objectSide, requesterSide))
        {
            bUsePosition = true;
        }

        var reply = new ViewClusterReply
        {
            Position = bUsePosition ? targetPos : Vector3.Zero,
            ClusterId = req.ClusterId,
            BUsePosition = bUsePosition,
        };
        SendPeer(from, FedPacket.Pack(new[] { reply.Encode() }));
        Console.WriteLine($"[viewcluster] {from} ship {live.Ship.Id} docked -> cluster {req.ClusterId}" +
            $"{(req.OtTarget != -1 ? $" otarget={(IgcObjectType)req.OtTarget}:{req.OidTarget}" : "")}" +
            $" bUsePosition={bUsePosition} (target ok={bUsePosition})");
    }

    /// <summary>
    /// P108 — the <c>FM_C_BOARD_SHIP(103)</c> handler, the port of
    /// <c>FedSrv.CPP:6385-6418</c> <c>case FM_C_BOARD_SHIP</c>. Dual-purpose
    /// like the native: <paramref name="req"/><c>.SidParent == NA</c> leaves
    /// the player's current parent ship (via <see cref="PerformLeave"/>), any
    /// other value boards that ship (via <see cref="PerformBoard"/>).
    /// <list type="bullet">
    /// <item><b>Gate: mission started.</b> The native refuses unless
    /// <c>pfsMission-&gt;GetStage() == STAGE_STARTED</c>
    /// (<c>FedSrv.CPP:6388</c>). We keep it via <see cref="Ship.Mission"/> /
    /// <see cref="MissionStage.Started"/>. The native's companion
    /// <c>IsGhost()</c> gate is skipped — this port has no ghost concept.</item>
    /// <item><b>Success ⇒ no dedicated ack.</b> Confirmed by reading the whole
    /// native handler: on success neither <c>BoardShip()</c> nor the internal
    /// <c>LeaveShip()</c> sends a dedicated acknowledgement — they only flush
    /// the <c>SHIP_STATUS</c> updates queued by their <c>ShipStatus-&gt;SetState</c>
    /// calls (<c>FedSrv.CPP:2450-2457</c>). This port's equivalent mechanism is
    /// the existing per-tick status refresh (<see cref="BroadcastShipStates"/>
    /// → <see cref="MakeOwnShipStatus"/>, which now derives the boarded state
    /// directly from the ship), so <b>no new success message is introduced</b>.</item>
    /// <item><b>Failure ⇒ <c>BOARD_NACK(178)</c> to the requester alone.</b>
    /// <c>g.fm.SendMessages(pfsPlayer-&gt;GetConnection(), ...)</c>
    /// (<c>FedSrv.CPP:6412-6416</c>). Covers: not a started mission; the leave
    /// case with no current parent; <c>LeaveShip</c> refusing (carrier not
    /// docked / not actually boarded); the board case with a missing or
    /// invalid target.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch drains inside
    /// <see cref="Tick"/>), so it may mutate <see cref="_peerShips"/>/sides
    /// without extra locking, like every other inbound handler.
    /// </summary>
    private void HandleBoardShip(ConnectionId from, LiveShip live, BoardShip req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[boardship] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — BOARD_NACK (native gate, FedSrv.CPP:6388)");
            SendBoardNack(from, req.SidParent);
            return;
        }

        bool success;
        if (req.SidParent == Na)
        {
            // Leave whatever this ship is currently boarded on (FedSrv.CPP:6393-6401).
            var carrier = ship.ParentShip;
            success = carrier is not null && PerformLeave(ship, carrier);
        }
        else
        {
            // Board the named ship (FedSrv.CPP:6404-6409): resolved on the
            // boarder's OWN side (native GetShip on GetSide()).
            var target = FindSameSideShip(req.SidParent, ship.SideId);
            success = target is not null && PerformBoard(ship, target);
        }

        if (!success)
        {
            SendBoardNack(from, req.SidParent);
            Console.WriteLine($"[boardship] {from} ship {ship.Id} -> sidParent {req.SidParent} refused — BOARD_NACK(178) to requester only");
        }
    }

    /// <summary>
    /// P109 — the real <c>CshipIGC::ProcessShipUpdate(const ClientActiveTurretUpdate&amp;)</c>
    /// port (<c>shipIGC.cpp:3349-3392</c>), applied to the <b>gunner's</b> own
    /// ship. Called from the <c>FM_C_ACTIVE_TURRET_UPDATE(99)</c> dispatch,
    /// gated here on the sender being a genuine boarded gunner (the same
    /// cluster/parent/turret gate native <c>FedSrv.CPP:4873-4875</c> applies
    /// before routing to <c>SetActiveTurretUpdate</c>). Sequence:
    /// <list type="bullet">
    /// <item><b>Staleness bound</b> — <c>deltaT = GetMyLastUpdate() − atu.time</c>;
    /// outside <c>±1.5 s</c> the whole message is dropped (<c>rc = c_susOutOfDate</c>,
    /// <c>shipIGC.cpp:3353-3354</c>).</item>
    /// <item><b>\"We're shooting\"</b> — <c>SetStateM(weaponsMaskIGC)</c>
    /// (<c>shipIGC.cpp:3359</c>); this makes the turret weapon's
    /// <c>IsWeaponSelected</c> true (gunner present + <c>weaponsMaskIGC</c>).</item>
    /// <item><b>Controls</b> — <c>shipupdate.controls.Export(&amp;m_controls)</c>
    /// (<c>shipIGC.cpp:3361</c>).</item>
    /// <item><b>Snapshot catch-up</b> — decode the reported orientation, then
    /// integrate it forward to the present in ≤ <c>c_fTimeIncrement</c> (0.25 s)
    /// steps via <see cref="Ship.ExecuteTurretMove"/> (the first of native's two
    /// real call sites, <c>shipIGC.cpp:3371-3384</c>), then
    /// <c>SetOrientation(orientation)</c>.</item>
    /// </list>
    /// The <c>CFSPlayer</c> monotonic gate (<c>atu.time &gt; m_timeUpdate</c>,
    /// <c>fsship.cpp:1208-1218</c>) that normally sits above this in the native
    /// is subsumed here: the port has no per-player last-turret-time bookkeeping,
    /// and the <c>deltaT</c> bound already rejects grossly stale frames, so the
    /// step's own staleness check is sufficient for this slice.
    /// </summary>
    private void HandleActiveTurretUpdate(Ship gunner, ClientActiveTurretUpdate atu)
    {
        // FedSrv.CPP:4873-4875 — the gate that precedes SetActiveTurretUpdate.
        if (gunner.Sector is null || gunner.ParentShip is null || gunner.TurretId is null)
        {
            Console.WriteLine($"[turret] ship {gunner.Id}: ACTIVE_TURRET_UPDATE from a non-gunner — ignored (FedSrv.CPP:4873-4875)");
            return;
        }

        // ProcessShipUpdate(const ClientActiveTurretUpdate&), shipIGC.cpp:3349-3392.
        double deltaT = gunner.LastUpdate - atu.Time;          // shipIGC.cpp:3353
        if ((deltaT < 1.5) && (deltaT > -1.5))                 // shipIGC.cpp:3354
        {
            // "If we got this ... we're shooting." (shipIGC.cpp:3358-3359)
            gunner.StateM = ShipControlState.WeaponsMask;      // SetStateM(weaponsMaskIGC)
            gunner.Controls = atu.Controls;                    // Export(&m_controls), shipIGC.cpp:3361
            Orientation orientation = atu.Orientation;         // Export(&orientation), shipIGC.cpp:3363-3364

            // Snapshot catch-up, never advancing more than c_fTimeIncrement
            // (0.25 s) per step (shipIGC.cpp:3366-3384).
            int n = 1 + (int)(MathF.Abs((float)deltaT) / 0.25f);
            double thisTime = atu.Time;
            for (int i = 1; i <= n; i++)
            {
                double nextTime = atu.Time + (deltaT * ((float)i / (float)n));
                gunner.ExecuteTurretMove(thisTime, nextTime, orientation);
                thisTime = nextTime;
            }
            gunner.SetOrientation(orientation);                // SetOrientation(orientation), shipIGC.cpp:3386

            Console.WriteLine($"[turret] ship {gunner.Id} ACTIVE_TURRET_UPDATE time={atu.Time} deltaT={deltaT:F3} -> " +
                              $"StateM=weaponsMaskIGC controls={atu.Controls} orientForward={orientation.GetForward()}");
        }
        else
        {
            Console.WriteLine($"[turret] ship {gunner.Id} ACTIVE_TURRET_UPDATE stale (deltaT={deltaT:F3} outside ±1.5) — dropped (shipIGC.cpp:3354)");
        }
    }

    /// <summary>
    /// P108+P109 — the server-side board action, the port of
    /// <c>FedSrv.CPP:2322-2475</c> <c>BoardShip()</c>. Establishes turret/
    /// observer <b>occupancy</b> (P108) <em>and</em>, since P109, the real
    /// weapon-<b>gunner binding</b> (native <c>SetTurretID</c>-to-<c>SetGunner</c>,
    /// <c>shipIGC.h:1405-1437</c>), so a boarded gunner actually crews the
    /// carrier's weapon at its slot.
    /// Returns true on a successful board; false lets the caller send
    /// <c>BOARD_NACK</c>. Steps mirrored from the native (each cited):
    /// <list type="bullet">
    /// <item><b>Self-exclusion</b> — <c>pship != pshipBoard</c>
    /// (<c>FedSrv.CPP:2332</c>).</item>
    /// <item><b>Carrier is a player ship</b> — <c>pfsShipBoard-&gt;IsPlayer()</c>
    /// (<c>FedSrv.CPP:2340</c>). In this port that means the carrier has an
    /// owning peer in <c>_peerShips</c> (the player/bot distinction). The
    /// dispatcher already enforced same-side via <see cref=\"FindSameSideShip\"/>,
    /// mirroring native <c>GetShip()</c> on the boarder's side.</item>
    /// <item><b>Both docked</b> — the native precondition
    /// (<c>FedSrv.CPP:2342-2345</c>) requires the boarder and carrier <c>not in
    /// a cluster</c> and <c>at a station</c> (boarding a docked ship from a
    /// docked ship). In this port a docked ship is <c>StationId != null</c>
    /// (docked ships retain their <see cref=\"Ship.Sector\"/>), so the gate
    /// translates to both <c>StationId</c>s being set.</item>
    /// <item><b>Turret-slot search</b> — <see cref=\"ChooseTurretSlot\"/>,
    /// faithful to the native walk (<c>FedSrv.CPP:2347-2402</c>): no turret
    /// slots ⇒ observer; else pick the first unoccupied, equipped
    /// <c>[MaxFixedWeapons, MaxWeapons)</c> slot, falling back to observer.</item>
    /// <item><b>Observer gate collapsed</b> — native
    /// <c>(turret != NA) || CanCheat()</c> (<c>FedSrv.CPP:2406</c>); this port
    /// has no cheat/admin concept, so per the P100/P103 precedent the gate
    /// collapses to always-true — observer boarding is allowed unconditionally.</item>
    /// <item><b>Defensive detach of the boarder's own children</b> — the native
    /// <c>while (psl = pchildren-&gt;first()) LeaveShip(...)</c>
    /// (<c>FedSrv.CPP:2415-2420</c>). No nested boarding exists in this port, so
    /// the list is always empty; kept defensively per the task.</item>
    /// <item><b>Money refund</b> — <c>refund = pship-&gt;GetValue()</c>
    /// (<c>FedSrv.CPP:2428</c>, hull price + Σ part prices,
    /// <c>shipIGC.h:396-406</c>). <b>Simplification:</b> the native credits the
    /// player's individual money and broadcasts <c>MONEY_CHANGE</c>
    /// (<c>FedSrv.CPP:2433-2442</c>); this port has no per-player wallet and no
    /// <c>MONEY_CHANGE</c> codec, so the refund is deposited into the boarder's
    /// side treasury (<see cref=\"Side.Money\"/>) — the P51-59 money primitive —
    /// where payday distributes it. Stated plainly, not a silent drop.</item>
    /// <item><b>State transition</b> — <c>SetParentShip / SetTurretID</c>
    /// (<c>FedSrv.CPP:2445-2446</c>); the boarder's parts are cleared (the
    /// native <c>SetParentShip</c> with no old parent terminates them,
    /// <c>shipIGC.cpp:3619-3633</c> — \"no parts allowed on an observer or
    /// turret\"); if the carrier is docked the boarder inherits its dock state
    /// (<c>FedSrv.CPP:2456-2457</c>). The wire <c>SHIP_STATUS</c> (hull NA,
    /// parent id, turret/observer state) is derived in
    /// <see cref=\"MakeOwnShipStatus\"/>, so the existing per-tick refresh is
    /// what reflects the change — no new message.</item>
    /// </list>
    /// </summary>
    private bool PerformBoard(Ship boarder, Ship carrier)
    {
        if (ReferenceEquals(boarder, carrier))
        {
            Console.WriteLine($"[boardship] ship {boarder.Id} tried to board itself — refused (pship != pshipBoard, FedSrv.CPP:2332)");
            return false;
        }

        // pfsShipBoard->IsPlayer() (FedSrv.CPP:2340) — the carrier must be a
        // peer-owned (player) ship in this port.
        if (!_peerShips.Any(kv => ReferenceEquals(kv.Value.Ship, carrier)))
        {
            Console.WriteLine($"[boardship] ship {boarder.Id} -> {carrier.Id}: carrier is not a player ship (FedSrv.CPP:2340) — refused");
            return false;
        }

        // Both docked: the native precondition (FedSrv.CPP:2342-2345).
        if (boarder.StationId is null || carrier.StationId is null)
        {
            Console.WriteLine($"[boardship] ship {boarder.Id} -> {carrier.Id}: boarder and carrier must both be docked (FedSrv.CPP:2342-2345) — refused");
            return false;
        }

        // Turret-slot search (FedSrv.CPP:2347-2402); null = observer.
        short? turret = ChooseTurretSlot(boarder, carrier);
        // Observer gate collapsed (no cheat/admin concept) — proceed regardless.

        // Defensive detach of the boarder's own existing children
        // (FedSrv.CPP:2415-2420): anyone riding on the boarder must fend for
        // themselves before the boarder climbs aboard the carrier. Always empty
        // today (no nested boarding), kept per the task's defensive guard.
        var kids = boarder.ChildShips.ToArray();
        foreach (var kid in kids)
            PerformLeave(kid, boarder);

        // Money refund (FedSrv.CPP:2426-2443) — computed before the parts are
        // cleared, matching native order (GetValue runs before SetParentShip
        // clears parts). Simplified to credit the side treasury (see doc).
        int refund = ShipValue(boarder);
        if (refund > 0 && boarder.SideId is { } bs && boarder.Mission is { } bm)
        {
            var side = bm.Sides.FirstOrDefault(s => s.Id == bs);
            if (side is not null)
            {
                side.Money += refund;
                // P118: now that the MONEY_CHANGE(60) codec exists, tell the
                // whole side its treasury moved — the faithful port of the
                // native board-refund broadcast (FedSrv.CPP:2435-2441, which
                // sends to CFSSide::FromIGC(pside)->GetGroup(), not just the
                // boarded player). sidFrom = NA marks the server-initiated
                // pooled-treasury credit (Messages.h:444 convention).
                BroadcastMoneyChangeSideWide(side.Id, refund, boarder.Id);
                Console.WriteLine($"[boardship] ship {boarder.Id} -> {carrier.Id}: refunded hardware value {refund} to side {side.Id} treasury + MONEY_CHANGE(60) to side");
            }
        }

        // SetParentShip + SetTurretID (FedSrv.CPP:2445-2446). Reparent cleanly
        // if the boarder was somehow already on another carrier, then register
        // as a child of the carrier.
        if (boarder.ParentShip is { } oldParent && !ReferenceEquals(oldParent, carrier))
            oldParent.RemoveChildShip(boarder);
        boarder.ParentShip = carrier;
        carrier.AddChildShip(boarder);
        boarder.TurretId = turret;

        // P109 — the turret weapon-gunner binding (native SetTurretID →
        // SetGunner, shipIGC.h:1405-1437): if the boarder occupies a real
        // turret slot (not an observer), bind it as the gunner of the
        // carrier's mounted weapon at that mount. The mount lookup reuses the
        // exact weapon→MountId mapping the P108 turret-slot walk already used
        // (MountedPart.MountId / Ship.Weapons[i].MountId), so no new
        // bookkeeping. An observer (turret null) binds nothing — it has no gun
        // to crew. Leave clears the binding (PerformLeave).
        if (turret is { } slot)
        {
            var turretWeapon = carrier.Weapons.FirstOrDefault(w => w.MountId == slot);
            if (turretWeapon is not null)
            {
                turretWeapon.SetGunner(boarder);
                Console.WriteLine($"[boardship] bound gunner ship {boarder.Id} to carrier {carrier.Id} turret weapon mount {slot}");
            }
            else
            {
                // A boarded turret slot with no realized weapon on it (the slot
                // check in ChooseTurretSlot looks at Parts, which may hold an
                // inert/other part). Bind nothing; occupancy stands.
                Console.WriteLine($"[boardship] turret slot {slot} on {carrier.Id} has no realized weapon — gunner binding skipped");
            }
        }

        // "No parts allowed on an observer or turret" (native SetParentShip with
        // no old parent terminates them, shipIGC.cpp:3619-3633).
        boarder.ClearLoadout();

        // If the carrier is docked, the boarder inherits its dock state
        // (FedSrv.CPP:2456-2457 SetStation(pstationBoard)).
        if (carrier.StationId is { } cs)
            boarder.StationId = cs;

        Console.WriteLine($"[boardship] ship {boarder.Id} boarded {carrier.Id} as " +
            $"{(turret is null ? "observer (no turret slot)" : $"turret gunner slot {turret}")} — no ack, rides SHIP_STATUS refresh");
        return true;
    }

    /// <summary>
    /// P108 — the server-side leave action, the port of the internal C++
    /// <c>LeaveShip(CFSShip*, IshipIGC*)</c> (<c>FedSrv.CPP:2277-2320</c>) — the
    /// <em>function</em>, not the wire message (which it builds and sends at
    /// <c>FedSrv.CPP:2312-2316</c>). Called for the <c>sidParent == NA</c> leave
    /// and for the boarder's own children during <see cref=\"BoardShip\"/>.
    /// Returns true on success; false lets the caller send <c>BOARD_NACK</c>.
    /// <list type="bullet">
    /// <item><b>Gate: the carrier is not in a cluster</b> — native
    /// <c>pshipParent-&gt;GetCluster() == NULL</c> (<c>FedSrv.CPP:2285</c>);
    /// in this port a docked carrier is <c>StationId != null</c>.</item>
    /// <item><b>Clears parent/turret</b> and removes this ship from the
    /// carrier's child list (native <c>SetParentShip(NULL)</c>,
    /// <c>shipIGC.cpp:3677-3689</c> DeleteChildShip).</item>
    /// <item><b>Restores a hull</b> — the side's civilization's Lifepod hull
    /// (native <c>SetBaseHullType(lifepod)</c>, <c>FedSrv.CPP:2301-2302</c>),
    /// the same resolution the death-respawn slice uses.</item>
    /// <item><b>Broadcasts <c>LEAVE_SHIP(94)</c> to the whole side</b>
    /// (<c>FedSrv.CPP:2312-2316</c>, <c>sidChild</c> = the detaching ship's id).</item>
    /// </list>
    /// The <c>SHIP_STATUS</c> consequences (state back to docked, parent NA,
    /// hull = the lifepod) are derived in <see cref=\"MakeOwnShipStatus\"/> and
    /// reflected by the existing per-tick refresh.
    /// </summary>
    private bool PerformLeave(Ship boarder, Ship carrier)
    {
        if (carrier.StationId is null)
        {
            Console.WriteLine($"[leaveship] ship {boarder.Id} from {carrier.Id}: carrier not docked — leave refused (FedSrv.CPP:2285)");
            return false;
        }
        if (boarder.ParentShip is not { } parent || !ReferenceEquals(parent, carrier))
        {
            Console.WriteLine($"[leaveship] ship {boarder.Id} from {carrier.Id}: not actually boarded on it — leave refused");
            return false;
        }

        // Clear the parent/turret relationship (native SetParentShip(NULL)):
        // remove from the carrier's child list, then null our back-pointer.
        carrier.RemoveChildShip(boarder);
        boarder.ParentShip = null;

        // P109 — release the turret weapon-gunner binding (native
        // SetParentShip(NULL) → SetGunner(NULL) on the occupied mount,
        // shipIGC.cpp:3652,3681). Capture the slot before clearing it and
        // unbind the carrier's weapon there so the freed turret stops firing
        // for the departing gunner.
        if (boarder.TurretId is { } slot)
        {
            var turretWeapon = carrier.Weapons.FirstOrDefault(w => w.MountId == slot);
            turretWeapon?.SetGunner(null);
        }
        boarder.TurretId = null;

        // Restore the side's Lifepod hull (FedSrv.CPP:2301-2302).
        if (boarder.SideId is { } sideId && boarder.Mission is { } leaveMission)
        {
            var side = leaveMission.Sides.FirstOrDefault(s => s.Id == sideId);
            if (side is not null && leaveMission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var lp))
            {
                boarder.ChangeHull(lp);
            }
            else
            {
                Console.WriteLine($"[leaveship] ship {boarder.Id} side {sideId}: lifepod hull unresolvable — leaving as current hull");
            }
        }

        // LEAVE_SHIP(94) to the WHOLE side, naming the detaching ship
        // (FedSrv.CPP:2312-2316) — not to a single peer.
        if (boarder.SideId is { } sideId2)
        {
            var leave = new LeaveShip { SidChild = boarder.Id };
            byte[] packet = FedPacket.Pack(new[] { leave.Encode() });
            int recipients = 0;
            foreach (var kv in _peerShips)
                if (kv.Value.Ship.SideId == sideId2) { SendPeer(kv.Key, packet); recipients++; }
            Console.WriteLine($"[leaveship] ship {boarder.Id} left {carrier.Id} — LEAVE_SHIP(94) sidChild={boarder.Id} -> side {sideId2} ({recipients} peer(s))");
        }
        else
        {
            Console.WriteLine($"[leaveship] ship {boarder.Id} left {carrier.Id} — no side, LEAVE_SHIP(94) suppressed");
        }
        return true;
    }

    /// <summary>
    /// P111 — the <c>FM_C_RIPCORD_REQUEST(184)</c> handler, the port of
    /// <c>FedSrv.CPP:7715-7728</c> <c>case FM_C_RIPCORD_REQUEST</c>.
    /// Mirrors the native gate exactly (each sub-condition confirmed in
    /// <c>FedSrv.CPP:7717-7721</c>): only a mission-started, real-side,
    /// un-boarded, flying ship whose hull can ripcord may proceed. A request
    /// that fails any gate is <b>silently ignored</b> — native does <c>break</c>;
    /// there is no denial message for a gate violation (only a request that
    /// names a cluster with no valid anchor yields <c>RIPCORD_DENIED</c>, which
    /// is decided later, inside <c>RequestRipcord</c>).
    /// <list type="bullet">
    /// <item><b>Mission started.</b> <c>GetStage() != STAGE_STARTED</c> →
    /// ignored (<c>FedSrv.CPP:7717</c>).</item>
    /// <item><b>Real side.</b> <c>GetSide()-&gt;GetObjectID() &lt; 0</c> → ignored
    /// (<c>FedSrv.CPP:7717</c>). Mirrored as <c>SideId &lt; 0</c>.</item>
    /// <item><b>Not boarded.</b> <c>GetParentShip()</c> → ignored
    /// (<c>FedSrv.CPP:7718</c>). A boarded ship is not its own actor. (Native
    /// always has this gate live; there is no landing-dependency caveat — it
    /// is independent of cluster E.)</item>
    /// <item><b>Flying.</b> <c>GetCluster() == NULL</c> → ignored
    /// (<c>FedSrv.CPP:7719</c>). Native clears the cluster on dock
    /// (<c>shipIGC.h:457,459</c>), so a docked ship has no cluster. In this port
    /// a docked ship retains its <c>Sector</c> but has <c>StationId != null</c>
    /// (the P103/P106 convention), so "flying" = <c>Sector != null &amp;&amp;
    /// StationId == null</c>.</item>
    /// <item><b>Not a flag-carrier.</b> <c>GetFlag() != NA</c> → ignored
    /// (<c>FedSrv.CPP:7720</c>). This port tracks no flags (the same
    /// always-passing gate-collapse precedent P100/P107 used); phase 4 /
    /// <c>GAIN_FLAG</c> territory.</item>
    /// <item><b>Hull can ripcord.</b> <c>GetBaseHullType()-&gt;HasCapability(
    /// c_habmNoRipcord)</c> → ignored (<c>FedSrv.CPP:7721</c>). <c>c_habmNoRipcord
    /// = 0x20</c> (<c>igc.h:731</c>), read like P107 read <c>c_habmLifepod</c>.</item>
    /// </list>
    /// Runs on the sim thread (the dispatch drains inside <see cref="Tick"/>),
    /// so it may mutate <c>_peerShips</c>/ships and send without locking.
    /// </summary>
    private void HandleRipcordRequest(ConnectionId from, LiveShip live, RipcordRequest req)
    {
        var ship = live.Ship;
        var mission = ship.Mission;

        if (mission is null || mission.Stage != MissionStage.Started)
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: {(mission is null ? "no mission" : $"stage {mission.Stage} != STAGE_STARTED")} — request ignored (gate FedSrv.CPP:7717)");
            return;
        }
        if (ship.SideId is not { } side || side < 0)
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: side {ship.SideId?.ToString() ?? "(none)"} < 0 — request ignored (gate FedSrv.CPP:7717)");
            return;
        }
        if (ship.ParentShip is not null)
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: boarded (parent {ship.ParentShip.Id}) — request ignored (gate FedSrv.CPP:7718)");
            return;
        }
        if (ship.Sector is null || ship.StationId is not null)
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: {(ship.Sector is null ? "no cluster" : "docked")} — request ignored (gate GetCluster(), FedSrv.CPP:7719)");
            return;
        }
        if (ship.HullType.HasCapability(DataHullTypeIGC.HabmNoRipcord))
        {
            Console.WriteLine($"[ripcord] {from} ship {ship.Id}: hull {ship.HullType.HullId} has c_habmNoRipcord — request ignored (gate FedSrv.CPP:7721)");
            return;
        }
        // Flag-carrier gate (FedSrv.CPP:7720) collapses to always-passing: this
        // port tracks no flags. Documented, not silently dropped.

        // Native resolves the requested sector via
        // GetIGCMission()->GetCluster(sidRipcord), which returns NULL for an
        // unknown id — and RequestRipcord treats pcluster==NULL exactly like
        // sidRipcord==NA (a cancel). Mirror that so a bogus sector id behaves
        // as a cancel, never as a wildcard.
        var target = mission.Sectors.FirstOrDefault(s => s.Id == req.SidRipcord);
        RequestRipcord(from, ship, target);
    }

    /// <summary>
    /// P111 — the port of <c>FedSrvSiteBase::RequestRipcord</c>
    /// (<c>FedSrv.CPP:13319-13378</c>), the real countdown-start state machine
    /// a client <c>RIPCORD_REQUEST</c> reaches after the handler gate.
    /// <paramref name="target"/> is the requested target sector (null when the
    /// request named <c>NA</c> or an unknown sector id). Three outcomes:
    /// <list type="number">
    /// <item><b>Cancel</b> — <c>target == null</c> with an active ripcord: clear
    /// the model and broadcast <c>RIPCORD_ABORTED(187)</c> sector-wide to
    /// everyone flying in the requester's current cluster (<c>FedSrv.CPP:13327-13339</c>).</item>
    /// <item><b>(Re)activate</b> — a valid anchor is found in the requested
    /// sector via <see cref="FindRipcordModel"/>: set the model, re-seed the
    /// countdown (<c>ResetRipcordTimeLeft</c>), and send
    /// <c>RIPCORD_ACTIVATE(185)</c> — <em>sector-wide</em> on a first activation
    /// (<c>pmodelOld == NULL</c>) or to the <em>requester alone</em> on a
    /// re-target (<c>FedSrv.CPP:13366-13368</c>). The <c>sidRipcord</c> field is
    /// the <b>requested</b> cluster id (<c>pcluster-&gt;GetObjectID()</c>,
    /// <c>FedSrv.CPP:13364</c>), not the anchor's own sector.</item>
    /// <item><b>Deny</b> — no valid anchor: <c>RIPCORD_DENIED(186)</c> to the
    /// requester alone (<c>FedSrv.CPP:13371-13376</c>).</item>
    /// </list>
    /// If the ship is already anchored in the requested sector the request is
    /// a no-op (the outer <c>else if</c> of <c>FedSrv.CPP:13341-13342</c>).
    /// </summary>
    private void RequestRipcord(ConnectionId from, Ship ship, Sector? target)
    {
        var current = ship.RipcordModel;
        if (target is null)
        {
            // sidRipcord == NA (or unknown): cancel an active ripcord.
            if (current is not null)
            {
                ship.SetRipcordModel(null);
                SendRipcordSectorWide(ship.Sector!.Id,
                    new RipcordAborted { ShipidRipcord = ship.Id }.Encode());
                Console.WriteLine($"[ripcord] ship {ship.Id} cancels -> RIPCORD_ABORTED(187) sector {ship.Sector.Id} wide");
            }
            return;
        }

        if (current is { } cur && cur.ClusterId == target.Id)
            return;   // already anchored in this sector — nothing to do (FedSrv.CPP:13341-13342)

        var found = FindRipcordModel(ship, target);
        if (found is { } anchor)
        {
            bool firstActivation = current is null;
            ship.SetRipcordModel(anchor);
            ship.ResetRipcordTimeLeft();

            var msg = new RipcordActivate
            {
                ShipidRipcord = ship.Id,
                OtRipcord = anchor.Ot,
                OidRipcord = anchor.Oid,
                SidRipcord = target.Id,   // the REQUESTED cluster, FedSrv.CPP:13364
            };
            if (firstActivation)
            {
                SendRipcordSectorWide(ship.Sector!.Id, msg.Encode());
                Console.WriteLine($"[ripcord] ship {ship.Id} FIRST activation anchor (ot={anchor.Ot} oid={anchor.Oid}) -> sector {target.Id} RIPCORD_ACTIVATE(185) sector {ship.Sector.Id} wide");
            }
            else
            {
                SendPeer(from, FedPacket.Pack(new[] { msg.Encode() }));
                Console.WriteLine($"[ripcord] ship {ship.Id} RE-TARGET anchor (ot={anchor.Ot} oid={anchor.Oid}) -> sector {target.Id} RIPCORD_ACTIVATE(185) requester-only");
            }
        }
        else
        {
            SendPeer(from, FedPacket.Pack(new[] { new RipcordDenied().Encode() }));
            Console.WriteLine($"[ripcord] ship {ship.Id}: no valid anchor in sector {target.Id} -> RIPCORD_DENIED(186) requester-only");
        }
    }

    /// <summary>Send a pre-encoded server message to every peer currently
    /// flying in <paramref name="sectorId"/> — the port's
    /// <c>GetGroupSectorFlying(pclusterShip)</c> (<c>FedSrv.CPP:13338</c>), the
    /// sector-wide broadcast both the ripcord slices and the P117 relaunch
    /// (<c>FedSrv.CPP:13019</c>) use.</summary>
    private void SendSectorWide(short sectorId, byte[] encoded)
    {
        var packet = FedPacket.Pack(new[] { encoded });
        foreach (var kv in _peerShips)
            if (kv.Value.Sector.Id == sectorId)
                SendPeer(kv.Key, packet);
    }

    /// <summary>Broadcast a <c>MONEY_CHANGE(60)</c> to every peer seated on
    /// side <paramref name="sideId"/> — the P118 side-wide money broadcast,
    /// port of the native <c>MONEY_CHANGE</c> sends that go to the whole side's
    /// peer group (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>, e.g.
    /// <c>FedSrv.CPP:2435-2441</c>, <c>7202-7206</c>). <c>sidFrom = NA</c>
    /// marks the server-initiated pooled-treasury credit, matching the native
    /// convention (<c>Messages.h:444</c>) for this port's refunds.</summary>
    private void BroadcastMoneyChangeSideWide(short sideId, int dMoney, short sidTo)
    {
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.SideId == sideId)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length == 0) return;   // no peers on the side — nothing to tell
        var msg = new MoneyChange { DMoney = dMoney, SidTo = sidTo, SidFrom = -1 /* NA */ };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[moneychange] side {sideId} -> MONEY_CHANGE(60) dMoney={dMoney} sidTo={sidTo} sidFrom=NA to {targets.Length} peer(s)");
    }

    /// <summary>Legacy name for <see cref="SendSectorWide"/> retained by the
    /// ripcord call sites; delegates to the canonical generic helper.</summary>
    private void SendRipcordSectorWide(short sectorId, byte[] encoded) => SendSectorWide(sectorId, encoded);

    /// <summary>
    /// P113 — the port of <c>FedSrvSiteBase::UseRipcord</c>
    /// (<c>FedSrv.CPP:13440-13500</c>), the actual relocation that runs when a
    /// ripcord countdown reaches zero with a still-legal anchor. Drained from
    /// <c>Sector.ResolveRipcordCountdowns</c> (via <c>DrainRipcordTeleports</c>)
    /// in the same <see cref="SimPass"/> slot the P112 abort broadcast uses —
    /// the sector pass detects the zero-cross and hands the ripper off here,
    /// exactly the sector-detects / host-acts split that governs the abort path.
    /// The anchor was just revalidated legal in the same tick, so resolving it
    /// here is a straight object lookup (no re-validation).
    /// <para>Three outcomes, mirroring the native function which always returns
    /// <c>true</c> (so the ripper's ripcord state is cleared — the attempt is
    /// <b>consumed, never retried on a later tick</b> even when it aborts):</para>
    /// <list type="number">
    /// <item><b>Station anchor</b> (<c>FedSrv.CPP:13441-13443</c>): native
    /// delegates to the station's own <c>Launch</c>, the exact mechanic the
    /// port already uses for undocking — so this calls the shared
    /// <see cref="RepositionForLaunch"/> (P69's launch core) and moves the
    /// ripper to the anchor station's cluster.</item>
    /// <item><b>Ship anchor with enough energy</b> (<c>FedSrv.CPP:13446-13474</c>):
    /// pay <c>RipcordCost</c> (the ripper's hull cost) from the target's energy,
    /// then the genuinely new ship-relative repositioning: <c>r = target.Radius +
    /// ripper.Radius + 25.0f</c> (<b>25, not the 50-margin station launch</b>),
    /// a <b>random</b> direction (<c>Vector::RandomDirection()</c>), the same
    /// <see cref="ExitStationSpeed"/> velocity (<c>c_fcidExitStationSpeed</c>),
    /// zeroed turn rates, and relocation to the target ship's cluster.</item>
    /// <item><b>Ship anchor without enough energy</b> (<c>FedSrv.CPP:13451-13461</c>):
    /// broadcast <c>RIPCORD_ABORTED(187)</c> to everyone flying in the ripper's
    /// cluster, do <b>not</b> move, and consume the attempt (state cleared) —
    /// native returns <c>true</c> either way, so it is not retried when the
    /// target later regains energy.</item>
    /// </list>
    /// <see cref="ShipStatusSpotted"/> (the native's trailing status refresh,
    /// <c>FedSrv.CPP:13499</c>) has no real equivalent in this port — P104 left
    /// <c>OBJECT_SPOTTED</c>(6) wire-only/untriggered — so it is intentionally
    /// skipped (finding stated in the P113 report), exactly as the task allows.
    /// Runs on the sim thread (the drain sits in <see cref="SimPass"/>).
    /// </summary>
    private void PerformRipcordTeleport(Ship rip)
    {
        var mission = rip.Mission;
        var anchor = rip.RipcordModel;
        if (mission is null || anchor is null)
        {
            rip.SetRipcordModel(null);
            return;
        }

        var a = anchor.Value;
        var targetSector = mission.Sectors.FirstOrDefault(s => s.Id == a.ClusterId);

        if (a.Ot == (short)IgcObjectType.Station)
        {
            // Station anchor: UseRipcord delegates to the station's Launch
            // (FedSrv.CPP:13441-13443) — reuse the undock-launch core, then move
            // to the station's cluster (SetCluster(pcluster), FedSrv.CPP:13474-13476).
            DataStationIGC? station = null;
            if (targetSector is not null)
                foreach (var st in targetSector.Stations)
                    if (st.StationId == a.Oid) { station = st; break; }

            if (station is null || targetSector is null)
            {
                // Anchor vanished between revalidation and drain (a capture/
                // destruction race). Native's UseRipcord returns true regardless,
                // so consume the attempt without relocating.
                rip.SetRipcordModel(null);
                return;
            }

            RepositionForLaunch(rip, station.Value);
            MoveShipToSector(rip, targetSector);
            // P75-style re-dock debounce: the ripper arrives just outside the
            // anchor station's DockRadius; the launch-cooldown latch stops an
            // accidental re-dock on the very next tick, mirroring PerformUndock.
            rip.LastLaunchTime = _simNow;
        }
        else if (a.Ot == (short)IgcObjectType.Ship)
        {
            // Ship anchor: resolve the target ship in its cluster.
            Ship? target = null;
            if (targetSector is not null)
                foreach (var m in targetSector.Models)
                    if (m is Ship s && s.Id == a.Oid) { target = s; break; }

            if (target is null || targetSector is null)
            {
                rip.SetRipcordModel(null);
                return;
            }

            // Energy gate (FedSrv.CPP:13452-13460): the TARGET ship must cover
            // the RIPPER's RipcordCost (pship->GetBaseHullType()->GetRipcordCost()).
            float ripcordCost = rip.HullType.RipcordCost;
            float newEnergy = target.Energy - ripcordCost;
            if (newEnergy < 0.0f)
            {
                // Insufficient energy: broadcast RIPCORD_ABORTED(187) to everyone
                // flying in the RIPPER's cluster (native "broadcast ... to everyone
                // flying in pship's cluster", FedSrv.CPP:13458-13460), do NOT move,
                // and consume the attempt (return true -> state cleared). Never
                // retried when the target later regains energy.
                var ripSec = rip.Sector;
                if (ripSec is not null)
                    SendRipcordSectorWide(ripSec.Id,
                        new RipcordAborted { ShipidRipcord = rip.Id }.Encode());
                rip.SetRipcordModel(null);
                Console.WriteLine($"[ripcord] ship {rip.Id} ripcord to ship-anchor {target.Id} ABORTED — target energy {target.Energy} < RipcordCost {ripcordCost}; attempt consumed (RIPCORD_ABORTED(187) -> ripper sector {ripSec?.Id.ToString() ?? "?"} wide)");
                return;
            }
            target.Energy = newEnergy;

            // Ship-relative reposition (FedSrv.CPP:13461-13474): NOT station-
            // relative — r = target.Radius + ripper.Radius + 25.0f (literal 25,
            // distinct from the station launch's 50 margin), a random direction,
            // ExitStationSpeed velocity, zeroed turn rates, and move to the
            // target's cluster.
            float r = ShipRadius(target) + ShipRadius(rip) + 25.0f;
            Vector3 v = RandomDirection();
            rip.Position = target.Position + v * r;
            rip.Velocity = v * ExitStationSpeed;
            var o = new Orientation();
            o.Set(v);                       // Orientation o(v), FedSrv.CPP:13465
            rip.SetOrientation(o);
            rip.SetTurnRate(ControlData.AxisYaw, 0.0f);
            rip.SetTurnRate(ControlData.AxisPitch, 0.0f);
            rip.SetTurnRate(ControlData.AxisRoll, 0.0f);
            MoveShipToSector(rip, targetSector);
            Console.WriteLine($"[ripcord] ship {rip.Id} ripcord -> ship-anchor {target.Id} at r={r:F1} (cost {ripcordCost} energy paid from target, now {target.Energy}) in sector {targetSector.Id}");
        }
        else
        {
            // Unknown anchor type — impossible per FindRipcordModel's two tiers,
            // but consume the attempt defensively rather than leave state active.
            rip.SetRipcordModel(null);
            return;
        }

        // Successful teleport: clear the ripcord state (the native's
        // SetRipcordModel(NULL) once UseRipcord returns true, shipIGC.cpp:293).
        rip.SetRipcordModel(null);
        UpdatePeerSectorSnapshot(rip, targetSector);
    }

    /// <summary>
    /// P115 — the port of <c>FedSrvSiteBase::RescueShipEvent</c>
    /// (<c>FedSrv.CPP:13033-13074</c>): a <c>c_habmLifepod</c> ship that drifted
    /// within proximity of a friendly <c>c_habmRescue</c>-capable ship is
    /// rescued — <c>PLAYER_RESCUED</c>(16) to both ships' connections, then the
    /// rescued ship is placed at the nearest friendly station. Queued by
    /// <c>Sector.ResolveRescueEvents</c> and drained here on the sim thread
    /// right after <c>Mission.Update</c> (the same accumulate/drain split as the
    /// ripcord teleport, P113). The pair (rescuee, rescuer) is already validated
    /// by the pass — same-side, exactly-one-is-the-lifepod, the other is
    /// rescue-capable, within proximity — so this method only performs the
    /// wire + placement halves of the native event.
    /// </summary>
    /// <remarks>
    /// <para><b>Score / Steam — N/A, skipped.</b> Native awards rescue
    /// achievements before the wire (<c>AddRescue</c>/<c>AwardPodPickup</c>/
    /// <c>AwardGetRescued</c>, <c>FedSrv.CPP:13039-13051</c>). This port has
    /// <b>no score system</b> (<c>PlayerInfo.cs:207</c> throws on non-empty
    /// <c>rgPersistPlayerScores</c> — "score history not supported") and no
    /// Steam integration anywhere, so both are intentionally skipped, the same
    /// precedent every score/Steam-adjacent gap this arc has already hit
    /// (P104's trigger deferral named the same absence).</para>
    /// <para><b>ResetWarpState() — no real equivalent needed here.</b> The
    /// native re-arms the rescued ship's <c>m_warpState = warpReady</c>
    /// (<c>fsship.h:199-201</c>) before docking it. This port's warp-cooldown
    /// stand-in (<c>Ship.LastWarpTime</c> / <c>Sector.WarpCooldownSeconds</c>,
    /// the collapsed <c>warpNoUpdate→warpWaiting→warpReady</c> machine,
    /// <c>Ship.cs:123-135</c>) is consulted only by the two proximity passes —
    /// <c>ResolveWarpCollisions</c> (<c>Sector.cs:1017</c>) and
    /// <c>ResolveDockCollisions</c> (<c>Sector.cs:1255</c>) — as their re-warp /
    /// re-dock debounce. This rescue places the rescuee by <b>direct</b> dock
    /// (below), which never enters those passes, so no warp-readiness gate
    /// stands between the rescue and its dock. Clearing <c>LastWarpTime</c>
    /// would only prematurely drop a legitimate 2 s re-warp debounce with no
    /// benefit to the direct placement. Conclusion: the existing cooldown
    /// already serves the native <c>warpState</c>'s debounce purpose, and no
    /// equivalent call is required here.</para>
    /// <para><b>Station search</b> — reuses P105's <see cref="FindFriendlyStation"/>
    /// (<c>Server.cs:4177</c>), the port's established stand-in for the native
    /// <c>FindTarget(..., c_ttStation | c_ttFriendly | c_ttAnyCluster |
    /// c_ttNearest, ..., c_sabmRestart)</c> nearest-friendly-station search
    /// (<c>FedSrv.CPP:13065-13066</c>). It already returns a same-side station
    /// (any friendly match, first-found — the port's documented simplification
    /// of "nearest"), so the native's same-side fallback
    /// (<c>pstation->GetSide() != pshipRescued's side → GetBase()</c>,
    /// <c>FedSrv.CPP:13067-13071</c>) is inherently satisfied — a same-side
    /// station is never discarded. When no friendly station exists anywhere,
    /// the native returns <c>false</c> with no dock (<c>FedSrv.CPP:13075</c>);
    /// mirrored (no placement, wire already sent).</para>
    /// <para><b>Placement</b> — reuses <c>PerformTeleport</c>'s relocation core
    /// (<c>Server.cs:3284-3299</c>, P110): move the rescuee to the station's
    /// cluster, latch <c>Ship.StationId</c> (and any boarded children), and
    /// point the owning peer's <c>LiveShip.Sector</c> snapshot at the station's
    /// cluster so the next SHIP_STATUS/SINGLE_SHIP_UPDATE read the right
    /// sector. Like the P110 teleport path, no <c>DockWithStationEvent</c>
    /// seam / <c>FM_S_DOCKED</c>(61) is raised here (the direct-dock latch is
    /// enough; the docked state is conveyed by the periodic SHIP_STATUS).</para>
    /// </remarks>
    private void PerformRescue(Ship rescuee, Ship rescuer)
    {
        var mission = rescuee.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[rescue] ship {rescuee.Id} (rescued by {rescuer.Id}) has no mission — rescue dropped");
            return;
        }

        // 1) Wire: PLAYER_RESCUED(16) to BOTH the rescuer's and the rescuee's
        // connections (native pfmPlayerRescued sent to both, FedSrv.CPP:13056-13065).
        // Peer-resolution doubles as the native's "if IsPlayer" gate: only a
        // peer-owned ship has a connection to send to; a non-peer ship's send is
        // skipped (it may still be placed below).
        var rescueMsg = new PlayerRescued { ShipIdRescuer = rescuer.Id, ShipIdRescuee = rescuee.Id };
        byte[] packet = FedPacket.Pack(new[] { rescueMsg.Encode() });
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rescuee) || ReferenceEquals(kv.Value.Ship, rescuer))
                SendPeer(kv.Key, packet);

        // 2) Nearest friendly station, else no placement (FedSrv.CPP:13065-13075).
        var station = FindFriendlyStation(rescuee);
        if (station is null)
        {
            Console.WriteLine($"[rescue] ship {rescuee.Id} rescued by {rescuer.Id}: no friendly station anywhere — PLAYER_RESCUED(16) sent to {packet.Length}-byte packet but no placement (native returns false, FedSrv.CPP:13075)");
            return;
        }

        // 3) Place the rescuee at the station — PerformTeleport's relocation
        // core (Server.cs:3284-3299): move to the station's cluster and latch
        // docked. FindFriendlyStation already guarantees same-side, so the
        // native's same-side fallback is satisfied.
        var destSector = mission.Sectors.First(s => s.Stations.Contains(station.Value));
        rescuee.Sector = destSector;
        foreach (var kid in rescuee.ChildShips)   // empty for a solo lifepod; mirrored for parity
            kid.StationId = station.Value.StationId;
        rescuee.StationId = station.Value.StationId;

        // Keep the owning peer's broadcast bookkeeping coherent — SHIP_STATUS
        // frames derive Sector from the LiveShip snapshot, so point it at the
        // station's cluster like PerformTeleport (Server.cs:3295) and the P105
        // respawn (Server.cs:4152) do.
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rescuee))
                _peerShips[kv.Key] = kv.Value with { Sector = destSector };

        var note = $"[rescue] ship {rescuee.Id} (lifepod) rescued by ship {rescuer.Id} -> PLAYER_RESCUED(16) rescuer={rescuer.Id}/rescuee={rescuee.Id}; placed docked at station {station.Value.StationId} '{station.Value.Name}' in sector '{destSector.Name}'";
        Console.WriteLine(note);
        Notable?.Invoke(note);
    }

    /// <summary>
    /// P117 — the port of <c>FedSrvSiteBase::LandOnCarrierEvent</c>
    /// (<c>FedSrv.CPP:12808-13031</c>), the real effect of a ship landing on a
    /// carrier: restore the lander's ship-level consumable pools, reposition it
    /// at a real launch slot (or the no-slots fallback), and broadcast
    /// <c>RELAUNCH_SHIP(192)</c> sector-wide. Drained from
    /// <c>Sector.ResolveCarrierLandings</c> (via <c>DrainCarrierLandings</c>) in
    /// the same <see cref="SimPass"/> slot the P115 rescue drain uses — the
    /// sector pass detects the geometry, the host performs the effect.
    /// </summary>
    /// <remarks>
    /// <para><b>Resupply, at this port's own granularity.</b> The native resets
    /// per-part amounts and walks the tech-tree successor of each part
    /// (<c>RefillPart</c>, <c>FedSrv.CPP:12589-12618</c>) and then also resets
    /// the ship-level scalars (<c>FedSrv.CPP:12855-12861</c>:
    /// <c>SetAmmo(SHRT_MAX); SetFuel(FLT_MAX);
    /// SetEnergy(GetHullType()-&gt;GetMaxEnergy()); SetFraction(1.0f);</c> plus a
    /// mounted shield's fraction). This port tracks ammo/energy as ship-level
    /// scalar pools seeded from <c>HullType.MaxEnergy</c>/<c>MaxAmmo</c>
    /// (<c>Sim/Ship.cs:62-63</c>) — there is no per-part inventory and no
    /// <c>SuccessorPartType</c>/tech-tree concept anywhere in this port
    /// (confirmed: zero references in <c>Data/DataPartTypeIGC.cs</c> and
    /// <c>Sim/MountedPart.cs</c>) — so the honest port of the whole resupply is
    /// to restore the two scalar pools to their hull maxima. That is exactly
    /// the native's ship-level reset above, at this port's granularity: <c>Ammo
    /// = SHRT_MAX</c> collapses to the finite hull <c>MaxAmmo</c> and
    /// <c>Energy = MaxEnergy</c> is verbatim. The native's <c>SetFraction(1.0f)</c>
    /// and shield-fraction reset are <b>not</b> performed here — the P117 scope
    /// is the consumable resupply (energy+ammo), and hull/shield repair is a
    /// separate, documented, not-yet-portable effect (see the P117 report).</para>
    /// <para><b>Fuel is a confirmed, separate, pre-existing gap.</b> Native
    /// <c>SetFuel(FLT_MAX)</c> (FedSrv.CPP:12856) has no target here: <c>grep
    /// "Fuel"</c> finds zero consumers in <c>Sim/Ship.cs</c>, <c>Model/Sector.cs</c>
    /// and <c>Server.cs</c> (only <c>DataHullTypeIGC.MaxFuel</c> is decoded,
    /// <c>Data/DataHullTypeIGC.cs:32</c>). Fuel tracking is deliberately not
    /// invented in this task.</para>
    /// <para><b>Desired-loadout / successor-upgrade</b>
    /// (<c>CFSPlayer::GetDesiredLoadout()</c>, the <c>ErasePart</c>/
    /// <c>CreateAndAddPart</c> fill-empty-slots logic and the
    /// <c>GetSuccessorPartType()</c> tech-tree walks, FedSrv.CPP:12831-12854)
    /// are the explicitly out-of-scope halves that need infrastructure this
    /// port doesn't have (per-part inventories, tech-tree bitmasks). Skipped,
    /// stated plainly.</para>
    /// <para><b>RecalculateCollisions</b> (<c>FedSrv.CPP:13006</c>) is N/A: this
    /// port has no rigid-body collision-resolution system (established P115/P116),
    /// so there is no post-move collision pass to call.</para>
    /// <para><b>Autopilot command re-tasking</b> (<c>FedSrv.CPP:13024-13030</c>,
    /// the <c>SetCommand</c>/<c>c_cidDefend</c>/<c>c_cidGoto</c> re-assign when
    /// the lander was GOTO-bound for the carrier) is skipped: this port models no
    /// AI/pilot-type/autopilot command concept — the only command-ish state is a
    /// single weapon-aim <c>Ship.CommandTarget</c> with no <c>c_cid*</c> IDs
    /// (confirmed, <c>Sim/Ship.cs:444-456</c>). Same precedent as the
    /// ShipStatusSpotted (P113) and other confirmed-absent native concepts.</para>
    /// <para><b>Round-robin launch slots are ported, not elided.</b> The native
    /// picks <c>slot = pshipCarrier-&gt;GetLaunchSlot()</c> — a per-carrier modulo
    /// counter (<c>shipIGC.h:2168-2172</c>) — so consecutive landings spread
    /// across the bay's <c>launchN</c> frames. <see cref="Ship.GetLaunchSlot"/>
    /// ports that counter faithfully (<see cref="Ship.NextLaunchSlot"/>), driven
    /// off the carrier's real reconstructed bay. The two native branches
    /// (<c>FedSrv.CPP:12970-13000</c>) are reproduced: with launch slots,
    /// position/velocity/orientation derive from the slot's local frame
    /// transformed by the carrier's orientation; with zero slots, the ship is
    /// launched straight back along the carrier's backward axis.</para>
    /// <para><b>Cookie.</b> Native sets <c>cookie = NewCookie()</c> for a player
    /// (<c>FedSrv.CPP:13018</c>); this port's <c>Cookie</c> is a single byte sent
    /// as <c>0</c> (no per-player cookie machinery), the same established
    /// simplification <c>Eject.ForSurvivor</c> and <c>Server.cs:3289,4616</c>
    /// apply — so <c>RelaunchShip.Cookie = 0</c>.</para>
    /// </remarks>
    private void PerformRelaunch(Ship lander, Ship carrier)
    {
        var mission = lander.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[relaunch] ship {lander.Id} landed on carrier {carrier.Id} has no mission — relaunch dropped");
            return;
        }

        // 1) Resupply — restore the lander's ship-level consumable pools to
        //    their hull maxima (see remarks: the port's granularity-equivalent
        //    of the native's RefillPart loop + ship-level scalar reset).
        lander.Energy = lander.HullType.MaxEnergy;
        lander.Ammo = lander.HullType.MaxAmmo;

        // 2) The carrier's real launch slots, resolved through the sector's
        //    landing-bay cache (the same production cache ResolveCarrierLandings
        //    used to detect the landing), falling back to a direct .cvh
        //    reconstruction when no cache is attached. A detected landing
        //    guarantees a resolvable bay with land slots; its launch-slot count
        //    is what selects the native's branch.
        var bay = lander.Sector?.LandingBays?.Bay(carrier.HullType)
                  ?? (carrier.Hull is not null
                        ? HullLandingBay.Reconstruct(carrier.Hull, carrier.HullType.Length)
                        : null);
        int launchSlots = bay?.LaunchSlots.Count ?? 0;

        var orientation = carrier.Orientation;                  // FedSrv.CPP:12963
        float vLaunch = ExitStationSpeed;                       // c_fcidExitStationSpeed = 50, FedSrv.CPP:12959, missionigc.cpp:2689,2744

        var position = carrier.Position + Jitter(0.5f);         // FedSrv.CPP:12964-12968 (small ±0.5 jitter)
        var velocity = carrier.Velocity;                        // FedSrv.CPP:12969

        Orientation newOrientation;
        int selectedSlot = -1;                                  // launch-slot index used (fallback branch = -1)
        if (launchSlots == 0)
        {
            // Fallback: launch straight back along the carrier's backward axis
            // (FedSrv.CPP:12971-12976). Keep the carrier's own orientation.
            var backward = orientation.GetBackward();
            position -= backward * (ShipRadius(carrier) + ShipRadius(lander) + vLaunch * 0.5f);
            velocity -= backward * vLaunch;
            newOrientation = orientation;
        }
        else
        {
            // Real launch slot: transform the slot's local frame by the
            // carrier's orientation (FedSrv.CPP:12978-12999), round-robin across
            // slots, and turn the lander to face the launch direction.
            selectedSlot = carrier.GetLaunchSlot(launchSlots);
            var slotPos = bay!.LaunchSlots[selectedSlot];
            position += slotPos.Position * orientation;               // GetLaunchPosition(slot) * orientation
            var forward = slotPos.Direction * orientation;            // GetLaunchDirection(slot) * orientation
            position += forward * (ShipRadius(lander) + vLaunch * 0.5f);
            velocity += forward * vLaunch;
            newOrientation = new Orientation(orientation);            // orientationBfr = orientation;
            newOrientation.Set(forward);                              // orientationBfr.TurnTo(forward) — align the nose to the launch direction
        }

        lander.Position = position;
        lander.Velocity = velocity;
        lander.SetOrientation(newOrientation);

        // 3) Broadcast RELAUNCH_SHIP(192) to everyone flying in the lander's
        //    cluster (GetGroupSectorFlying, FedSrv.CPP:13019). The loadout blob
        //    is the lander's own ShipLoadout (hullID + mounted parts), reusing
        //    BuyLoadoutRequest's blob shape.
        var relaunch = new RelaunchShip
        {
            ShipId = lander.Id,
            CarrierId = carrier.Id,
            Position = position,
            Velocity = velocity,
            Orientation = newOrientation,
            Cookie = 0,
            HullId = lander.HullType.HullId,
            Parts = lander.Parts.Select(p => p.ToExpandedPartData()).ToList(),
        };
        byte[] encoded = relaunch.Encode();
        SendSectorWide(lander.Sector!.Id, encoded);

        var note = $"[relaunch] ship {lander.Id} relaunched off carrier {carrier.Id} (bay '{carrier.HullType.ModelName}', {launchSlots} launch slots{(selectedSlot >= 0 ? $", slot {selectedSlot}" : "")}) -> RELAUNCH_SHIP(192) sector-wide; energy={lander.Energy:F0}/{lander.HullType.MaxEnergy:F0} ammo={lander.Ammo}/{lander.HullType.MaxAmmo}; pos=({position.X:F1},{position.Y:F1},{position.Z:F1}) vel=({velocity.X:F1},{velocity.Y:F1},{velocity.Z:F1})";
        Console.WriteLine(note);
        Notable?.Invoke(note);
    }

    /// <summary>A per-axis uniform-jitter helper for the relaunch's small
    /// <c>random(-0.5f, 0.5f)</c> positional wobble (FedSrv.CPP:12965-12967).
    /// Independent draw per axis, shared with the port's existing
    /// <see cref="_rand"/>-backed helpers.</summary>
    private static Vector3 Jitter(float amplitude) =>
        new((float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude,
            (float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude,
            (float)(_rand.NextDouble() * 2.0 - 1.0) * amplitude);

    /// <summary>P113 — the ship's bounding radius, <c>GetRadius()</c>. The port
    /// has no per-model radius accessor (test ships carry no collision hull), so
    /// the established <c>0.5 × HullType.Length</c> sphere approximation is used,
    /// the same convention ServerHost uses for its spawn offsets (ServerHost.cs:566).
    /// Feeds the ship-anchor ripcord margin <c>r = target.Radius + ripper.Radius +
    /// 25.0f</c> (FedSrv.CPP:13462).</summary>
    private static float ShipRadius(Ship ship) => 0.5f * ship.HullType.Length;

    /// <summary>P113 — move a ripper into the anchor's sector
    /// (<c>SetCluster(pcluster)</c>, FedSrv.CPP:13474-13476): out of its old
    /// sector's model list, into the target's, and update its <see cref="Ship.Sector"/>
    /// back-ref so it ticks with the new sector next pass.</summary>
    private static void MoveShipToSector(Ship ship, Sector target)
    {
        if (ship.Sector is { } old && !ReferenceEquals(old, target))
        {
            old.Remove(ship);
            target.Add(ship);
            ship.Sector = target;
        }
        else if (ship.Sector is null)
        {
            target.Add(ship);
            ship.Sector = target;
        }
    }

    /// <summary>P113 — keep the owning peer's broadcast bookkeeping coherent
    /// after a ripcord teleport changes the ripper's cluster: SHIP_STATUS /
    /// SINGLE_SHIP_UPDATE frames derive Sector from the <c>_peerShips</c>
    /// LiveShip snapshot, so point it at the anchor's cluster exactly like
    /// PerformTeleport / the lifepod respawn do (Server.cs:3072, 3929).</summary>
    private void UpdatePeerSectorSnapshot(Ship rip, Sector target)
    {
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, rip))
            {
                _peerShips[kv.Key] = kv.Value with { Sector = target };
                break;
            }
    }

    /// <summary>
    /// P111 — the port of <c>CshipIGC::FindRipcordModel</c>
    /// (<c>shipIGC.cpp:3718-3973</c>): what actually qualifies as a valid
    /// ripcord anchor in a target sector, and the priority order. The full
    /// native search has five tiers (command-target probe/ship,
    /// friendly <c>c_sabmRipcord</c> station via <c>FindTarget</c>, friendly
    /// probe, friendly ship, warp-adjacent-cluster expansion). This port
    /// implements the two realistic, deterministic tiers and documents the rest:
    /// <list type="number">
    /// <item><b>Friendly <c>c_sabmRipcord</c> station</b> in the target sector
    /// (<c>FindTarget</c> station tier, <c>shipIGC.cpp:3835-3838</c>): a
    /// station on the requester's own side whose station type has the
    /// <c>c_sabmRipcord</c> ability (<c>igc.h:747</c>).</item>
    /// <item><b>Friendly ripcord-target ship</b> in the target sector
    /// (<c>shipIGC.cpp:3898-3930</c>): a peer ship on the requester's side
    /// whose hull can host a ripcord (light-ripcord refinement reproduced —
    /// the target needs <c>c_habmIsRipcordTarget</c>, or additionally
    /// <c>c_habmIsLtRipcordTarget</c> when the ripper's own hull has
    /// <c>c_habmCanLtRipcord</c>, <c>shipIGC.cpp:3900-3906</c>).</item>
    /// </list>
    /// Deferred (documented, not silently dropped): the command-target probe/
    /// ship tier (<c>shipIGC.cpp:3802-3833</c>) and the friendly-probe tier
    /// (<c>shipIGC.cpp:3841-3896</c>) because this map/core and port have no
    /// probes deployed and no ripcord-to-target UI; the warp-adjacent-cluster
    /// expansion (<c>shipIGC.cpp:3936-3972</c>) because it relies on per-side
    /// <c>CanSee(w)</c> visibility and ripcord-debt ordering that this port
    /// does not model, and — critically — on XXV_simple's densely-connected
    /// 13-sector web (38 warps) the real side-0 station is reachable from
    /// nearly every sector, so expansion would grant an anchor almost anywhere
    /// and make the <c>RIPCORD_DENIED</c> path unreachable in practice. Scoping
    /// the anchor search to the requested sector keeps the deny path real and
    /// deterministic. Revisit with the countdown/teleport follow-up.
    /// </summary>
    private Ship.RipcordTarget? FindRipcordModel(Ship ship, Sector target)
    {
        short side = ship.SideId!.Value;
        var core = ship.Mission!.Core;

        // Tier 1: friendly c_sabmRipcord station (shipIGC.cpp:3835-3838).
        foreach (var st in target.Stations)
        {
            if (st.SideId == side &&
                core.StationTypes.TryGetValue(st.StationTypeId, out var stype) &&
                stype.HasCapability(DataStationTypeIGC.SabmRipcord))
                return new Ship.RipcordTarget((short)IgcObjectType.Station, st.StationId, st.ClusterId);
        }

        // Tier 2: friendly ripcord-target ship (shipIGC.cpp:3898-3930).
        short habm = ship.HullType.HasCapability(DataHullTypeIGC.HabmCanLtRipcord)
            ? (short)(DataHullTypeIGC.HabmIsRipcordTarget | DataHullTypeIGC.HabmIsLtRipcordTarget)
            : DataHullTypeIGC.HabmIsRipcordTarget;
        foreach (var peer in _peerShips.Values)
        {
            var s = peer.Ship;
            if (!ReferenceEquals(s, ship) && s.Sector is not null &&
                s.Sector.Id == target.Id && s.SideId == side &&
                s.HullType.HasCapability(habm))
                return new Ship.RipcordTarget((short)IgcObjectType.Ship, s.Id, target.Id);
        }

        return null;
    }

    /// <summary>Pick a turret slot for <paramref name="boarder"/> on
    /// <paramref name="carrier"/>, faithfully porting the native walk
    /// (<c>FedSrv.CPP:2347-2402</c>). Returns the chosen slot index, or
    /// <c>null</c> (= NA) when the ship boards as an <b>observer</b> — either
    /// because the carrier has no turret slots at all
    /// (<c>minMount == maxMount</c>), or because no unoccupied, equipped slot
    /// exists after a full loop. Occupancy is read from the carrier's existing
    /// <see cref="Ship.ChildShips"/> turret ids; equipment from the carrier's
    /// mounted <see cref="MountedPart.MountId"/>s (the port already tracks
    /// both, so this is a pure reuse, no new fields).</summary>
    internal static short? ChooseTurretSlot(Ship boarder, Ship carrier)
    {
        int minMount = carrier.HullType.MaxFixedWeapons;
        int maxMount = carrier.HullType.MaxWeapons;
        if (minMount == maxMount)
            return null;                                  // no turret slots at all → observer

        int startMount = boarder.TurretId ?? (maxMount - 1);
        int turret = startMount;
        while (true)
        {
            turret++;
            if (turret >= maxMount)
                turret = minMount;

            // Occupied? any existing child already rides this slot.
            bool occupied = false;
            foreach (var child in carrier.ChildShips)
                if (child.TurretId == (short)turret) { occupied = true; break; }

            // Equipped? some mounted part sits in this slot (Turkey 07/10 #145).
            bool equipped = false;
            foreach (var part in carrier.Parts)
                if (part.MountId == turret) { equipped = true; break; }

            if (!occupied && equipped)
                return (short)turret;                     // free and equipped → board here
            if (turret == startMount)
                return null;                              // full loop, no slot → observer
        }
    }

    /// <summary>The boarder's surrendered hardware value = hull price + Σ
    /// mounted part prices (<c>CshipIGC::GetValue</c>, <c>shipIGC.h:396-406</c>).</summary>
    private static int ShipValue(Ship ship) =>
        ship.HullType.Price + ship.Parts.Sum(p => p.Type.Price);

    /// <summary>
    /// P110 — the port's decision for <c>station.CanBuy(baseHullType)</c>,
    /// i.e. whether a station can sell the player's current hull
    /// (<c>FedSrv.CPP:7076</c>). Native: <c>CanBuy(b) = b->GetRequiredTechs()
    /// &lt;= (m_myStationType.GetLocalTechs() | side.GetTechs())</c>
    /// (<c>stationIGC.h:383-391</c>, bitwise containment of the buyable's
    /// required-tech mask in the union of the station's local techs and the
    /// side's techs), where the station's local techs are its station type's
    /// <c>ttbmLocal</c> (<c>stationtypeigc.h:159-161</c>) and the side's techs
    /// are the runtime-developed mask. <b>Simplification, stated plainly:</b>
    /// this port has no tech-tree/development simulation — sides never accrue
    /// developments — so the side's runtime-developed term is approximated by
    /// its civilization's <b>base</b> tech mask (<c>DataCivilizationIGC.TtbmBaseTechs</c>,
    /// the faction's starting knowledge), the closest static proxy this port
    /// carries; "can buy" therefore reduces to
    /// <c>hull.TtbmRequired ⊆ (stationType.TtbmLocal | civ.TtbmBaseTechs)</c>
    /// (all 50-byte / 400-bit tech masks, <c>DataHullTypeIGC.cs:21</c>,
    /// <c>DataStationTypeIGC.cs:29</c>, <c>DataCivilizationIGC.cs:17</c>).
    /// Measured on PCore001.igc this yields a sane dichotomy: a faction's
    /// garrison sells its starter hulls (Scout/Lt Interceptor/Fighter/Miner
    /// per civ — <c>bNewHull=false</c>) and rejects every advanced hull
    /// (<c>bNewHull=true</c>), which is exactly the pair the task's
    /// verification scenarios exercise.
    /// </summary>
    private static bool StationSellsHull(DataStationTypeIGC st, DataCivilizationIGC civ, DataHullTypeIGC hull)
    {
        var need = hull.TtbmRequired;
        var local = st.TtbmLocal;
        var baseTechs = civ.TtbmBaseTechs;
        for (int i = 0; i < need.Length && i < local.Length; i++)
            if ((need[i] & ~(local[i] | (i < baseTechs.Length ? baseTechs[i] : (byte)0))) != 0) return false;
        for (int i = local.Length; i < need.Length; i++)
            if (need[i] != 0 && (i >= baseTechs.Length || (need[i] & ~baseTechs[i]) != 0)) return false;
        return true;
    }

    /// <summary>Resolve a same-side ship by id among the peer ships — the port
    /// of native <c>GetSide()->GetShip(sidParent)</c> (<c>FedSrv.CPP:6404</c>),
    /// which looks the target up on the boarder's own side (hence the
    /// same-side requirement). Returns <c>null</c> for an unknown id or a
    /// mismatched side.</summary>
    private Ship? FindSameSideShip(short id, short? sideId)
    {
        foreach (var kv in _peerShips)
            if (kv.Value.Ship.Id == id && kv.Value.Ship.SideId == sideId)
                return kv.Value.Ship;
        return null;
    }

    /// <summary>Send <c>FM_S_BOARD_NACK(178)</c> to one peer (the requester)
    /// naming the <paramref name="requestedParent"/> id — <c>SendMessages(
    /// pfsPlayer-&gt;GetConnection(), ...)</c>, <c>FedSrv.CPP:6412-6416</c>.</summary>
    private void SendBoardNack(ConnectionId from, short requestedParent)
    {
        var nack = new BoardNack { SidRequestedParent = requestedParent };
        SendPeer(from, FedPacket.Pack(new[] { nack.Encode() }));
    }

    /// <summary>The native <c>NA</c> sentinel for a ShipID (igc.h:480):
    /// board/leave sentinel in <c>FM_C_BOARD_SHIP.sidParent</c>.</summary>
    private const short Na = -1;

    /// <summary>
    /// This port's <c>GetModel(ot, oid)</c> — resolve an object by
    /// <c>(ObjectType, ObjectID)</c> across the objects the port can name: the
    /// peer ships (the only first-class models, all present in <see cref=\"_peerShips\"/>)
    /// and sector-attached stations. On success outputs the object's real
    /// position, the sector that owns it, and its side (nullable for a neutral
    /// object). Returns false for an unnamed type or an unknown id — the caller
    /// then emits a bare-cluster reply. This matches the practical reality of a
    /// spectator target (another player's ship, or a station).
    /// </summary>
    private bool TryResolveViewTarget(IgcObjectType ot, short oid, out Vector3 position, out Sector? sector, out short? side)
    {
        switch (ot)
        {
            case IgcObjectType.Ship:
                foreach (var kv in _peerShips)
                {
                    if (kv.Value.Ship.Id == oid)
                    {
                        position = kv.Value.Ship.Position;
                        sector = kv.Value.Ship.Sector;
                        side = kv.Value.Ship.SideId;
                        return true;
                    }
                }
                break;

            case IgcObjectType.Station:
                foreach (var s in _missions[0].Sectors)
                {
                    foreach (var st in s.Stations)
                    {
                        if (st.StationId == oid)
                        {
                            position = new Vector3(st.Position[0], st.Position[1], st.Position[2]);
                            sector = s;
                            side = st.SideId;
                            return true;
                        }
                    }
                }
                break;
        }

        position = Vector3.Zero;
        sector = null;
        side = null;
        return false;
    }

    /// <summary>The port's stand-in for native <c>ImodelIGC::SeenBySide(side)</c> —
    /// the same-side predicate the P58 teammate broadcast already uses
    /// (<c>Server.cs:521</c>). An object with no side (a neutral world object)
    /// is always visible; a side-carrying object is visible only to its own
    /// side. The port has no fog-of-war/scanner, so this is the faithful
    /// existing gate, not an invented one.</summary>
    private static bool SeenByRequesterSide(short? objectSide, short? requesterSide) =>
        objectSide is null || objectSide == requesterSide;

    /// <summary>
    /// Apply a received <see cref="BuyLoadoutRequest"/> (FM_C_BUY_LOADOUT, 4)
    /// to the peer's ship and reply with the real FM_S_BUY_LOADOUT_ACK (5),
    /// tail carrying the ship's actual mounted-part count. Runs on the sim
    /// thread (ProcessDatagram). This is the P61 generalization of the P26/P27
    /// hull-only adopt-a-hull step: resolve the requested hull against the
    /// core, replace the ship's loadout with the requested parts, and echo the
    /// mounted parts back byte-for-byte.
    /// </summary>
    /// <summary>
    /// P69/P113 — the shared station-launch reposition: push a ship radially
    /// outward from a station to just clear <see cref="Sector.DockRadius"/> by
    /// <see cref="LaunchClearMargin"/> and kick it outward at
    /// <see cref="ExitStationSpeed"/>. Position = stationPos + dir·(DockRadius +
    /// LaunchClearMargin), velocity = dir·ExitStationSpeed, where <c>dir</c> is
    /// the station→ship direction (a fixed <c>(1,0,0)</c> fallback when the ship
    /// sits exactly at the station's position). This is the port's undock-launch
    /// mechanic (born P69, refined P74/P75) — <b>and</b> the exact launch shape
    /// <c>FedSrvSiteBase::UseRipcord</c>'s station-anchor branch produces by
    /// delegating to the station's own <c>Launch</c> (<c>FedSrv.CPP:13441-13443</c>:
    /// native <c>UseRipcord</c> calls <c>((IstationIGC*)pmodel)-&gt;Launch(pship)</c>,
    /// the same launch the undock path uses). The ripcord teleport calls this
    /// rather than duplicating the math.
    /// </summary>
    private void RepositionForLaunch(Ship ship, DataStationIGC station)
    {
        if (station.Position is not { Length: 3 } sp) return;
        var stationPos = new Vector3(sp[0], sp[1], sp[2]);
        var offset = ship.Position - stationPos;
        var dir = offset.LengthSquared() > 0.01f ? offset.Normalize() : new Vector3(1f, 0f, 0f);
        ship.Position = stationPos + dir * (Sector.DockRadius + LaunchClearMargin);
        ship.Velocity = dir * ExitStationSpeed;
    }

    /// <summary>
    /// Undock a docked ship: clear its station, push it clear of DockRadius
    /// with an outward kick, start the re-dock debounce, and re-send
    /// FM_S_SET_CLUSTER(7) — the only message that clears the native client's
    /// own GetStation()/GetCluster() model state (appmsg.cpp:1044-1049), which
    /// BaseClient::flyingF() (clintlib.h:1198) and the Hangar screen's launch
    /// lockdown (appmsg.cpp:991-996) both gate on. Extracted so both real
    /// undock triggers this server recognizes can share it: an explicit
    /// FM_C_DOCKED(48) request (WOPR bots, P69/P71), and BUY_LOADOUT's own
    /// FLaunch=true (the real WinTrek client's Hangar "Launch" button, which
    /// never sends a separate undock message — see ApplyLoadout below).
    /// Caller must have already confirmed <c>live.Ship.StationId != null</c>.
    /// </summary>
    private void PerformUndock(ConnectionId from, LiveShip live)
    {
        var station = live.Ship.StationId!.Value;
        live.Ship.StationId = null;

        // Push the ship physically clear of DockRadius and start the undock
        // debounce (Ship.LastLaunchTime / P75). Without the position push,
        // Sector.ResolveDockCollisions (Sector.cs:453) gates re-docking on
        // StationId==null alone, so the very next tick can re-latch a ship
        // that never moved. Reposition just outside DockRadius along the
        // station->ship direction (a fixed fallback direction if the ship
        // happens to be exactly at the station's position), with an outward
        // velocity kick — see the P69/P74 history in the original undock
        // handler (git blame) for why both the position push and the
        // LaunchCooldownSeconds debounce below are needed together.
        //
        // P113: the reposition is shared with the station-anchor ripcord
        // teleport (UseRipcord's station branch, FedSrv.CPP:13441-13443) —
        // both launch a ship from a station with the same radial margin +
        // ExitStationSpeed shape, so the core lives once here.
        DataStationIGC? stationModel = null;
        foreach (var st in live.Sector.Stations)
            if (st.StationId == station) { stationModel = st; break; }
        if (stationModel is { } stFound)
            RepositionForLaunch(live.Ship, stFound);

        // P75: start the undock debounce — Sector.ResolveDockCollisions won't
        // consider this ship for re-docking again until Sector.LaunchCooldownSeconds
        // elapses, regardless of how close it drifts back to the station.
        live.Ship.LastLaunchTime = _simNow;

        // Re-send FM_S_SET_CLUSTER(7) to the undocking peer — the only place
        // that calls m_ship->SetStation(NULL) client-side (appmsg.cpp:1044-1049).
        // Without this the client still believes GetStation()!=NULL and
        // GetCluster()==NULL forever, so flyingF() never becomes true and (for
        // the real client) the Hangar launch lockdown never clears either.
        var set = new SetCluster
        {
            SectorId = live.Sector.Id,
            Ship = ServerShipUpdate.FromModel(live.Ship.Id, live.Ship, (uint)TickIndex),
            Cookie = 0,   // same simplification OnShipWarped already uses
        };
        SendPeer(from, FedPacket.Pack(new[] { set.Encode() }));

        var undockNote = $"[undock] ship {live.Ship.Id} undocked from station {station} — next SHIP_STATUS(90) carries StationID=NA + State=c_ssFlying";
        Console.WriteLine(undockNote);
        Notable?.Invoke(undockNote);      // spectator event feed (same text)
    }

    /// <summary>
    /// P110 — the teleport/relocation dock request, the <b>non-</b><c>NA</c>
    /// half of <c>FM_C_DOCKED(48)</c>. A displaced ship asks to land at a
    /// specific <paramref name="targetStationId"/>; the server validates it
    /// and answers with <c>FM_S_TELEPORT_ACK(102)</c>. Port of
    /// <c>FedSrv.CPP:7054-7131</c>. This is <b>not</b> the proximity-triggered
    /// dock this port already handles via <see cref="Sector.ResolveDockCollisions"/>
    /// (untouched) — it is the explicit-<c>stationID</c> request direction that
    /// a relocated ship (a future rescue/warp-arrival trigger) uses to land
    /// somewhere, exactly the native responder half. The caller has already
    /// gated: a peer-owned, currently-docked ship
    /// (<c>GetStation()!=NULL</c>, <c>FedSrv.CPP:7040-7041</c>). Runs on the
    /// sim thread (called from the <c>FM_C_DOCKED</c> wire handler).
    /// <para><b>Validation</b> (<c>FedSrv.CPP:7063-7066</c>): the station must
    /// exist, be on the player's side or an ally's, and be
    /// <c>c_sabmRestart</c>-capable. If any fails, native sends the ACK with
    /// <c>stationID = NA</c> (<c>FedSrv.CPP:7067-7073</c>) and <c>break</c>s —
    /// the ship is <b>left where it is</b>, not moved. This port's ally check
    /// collapses to same-side (no alliance concept, stated plainly, matching
    /// how <see cref="FindFriendlyStation"/> defines "friendly").</para>
    /// <para><b>Acknowledge</b>: valid → echo the real <paramref
    /// name="targetStationId"/> and compute <c>bNewHull =
    /// !station.CanBuy(ship's base hull)</c> (<c>FedSrv.CPP:7076</c>). When
    /// <c>bNewHull</c>, mirror <c>FedSrv.CPP:7079-7120</c>: boot boarded
    /// children, refund the ship's hardware value, strip its parts, swap to
    /// the side's Lifepod hull — reusing this port's existing helpers
    /// (<see cref="ShipValue"/>, <see cref="Ship.ClearLoadout"/>,
    /// <see cref="Ship.ChangeHull"/>), the same three operations P105/P108
    /// built, <b>not</b> reimplemented. Finally move any boarded children to
    /// the station and the ship itself (<c>FedSrv.CPP:7124-7131</c>).</para>
    /// </summary>
    private void PerformTeleport(ConnectionId from, LiveShip live, short targetStationId)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[teleport] {from} ship {ship.Id}: no mission — teleport request dropped");
            return;
        }

        // Resolve the named station across every sector (native
        // GetStation(id) spans the mission, FedSrv.CPP:7056).
        DataStationIGC? target = null;
        foreach (var sector in mission.Sectors)
        {
            foreach (var st in sector.Stations)
                if (st.StationId == targetStationId) { target = st; break; }
            if (target is not null) break;
        }

        // Validation triple (FedSrv.CPP:7063-7066): exists, friendly/allied,
        // c_sabmRestart-capable. Side can't be null here — the caller's docked
        // gate implies a placed ship with a side — but guard anyway.
        bool exists = target is not null;
        bool sameSide = target is { } t0 && t0.SideId == ship.SideId;
        bool restartOk = target is { } t1
            && mission.Core.StationTypes.TryGetValue(t1.StationTypeId, out var stype)
            && stype.HasCapability(DataStationTypeIGC.SabmRestart);

        if (!exists || !sameSide || !restartOk)
        {
            // Reject (FedSrv.CPP:7067-7073): ack stationID = NA, ship stays put.
            var reject = new TeleportAck { StationId = -1, BNewHull = false };
            SendPeer(from, FedPacket.Pack(new[] { reject.Encode() }));
            var why = string.Join("; ",
                (exists ? null : "station not found"),
                (sameSide ? null : "not on this side or an ally's"),
                (restartOk ? null : "not c_sabmRestart-capable"))
                .Replace("  ", " ");
            Console.WriteLine($"[teleport] {from} ship {ship.Id}: request for station {targetStationId} rejected — {why} -> TELEPORT_ACK stationID=NA (FedSrv.CPP:7067-7073)");
            return;
        }

        // Valid — acknowledge with the real station id (FedSrv.CPP:7075-7077),
        // and the port's bNewHull = !CanBuy(current hull). stype is guaranteed
        // present past the restartOk guard (which itself requires the lookup
        // to succeed), so the re-lookup here is provably safe. The ship's side
        // provides the civilization base-tech term (native side.GetTechs()).
        mission.Core.StationTypes.TryGetValue(target!.Value.StationTypeId, out var validType);
        var civ = mission.Sides.FirstOrDefault(s => s.Id == ship.SideId!.Value)?.Civilization;
        if (civ is null)
        {
            Console.WriteLine($"[teleport] {from} ship {ship.Id}: side {ship.SideId} not in mission — cannot compute bNewHull; teleport rejected");
            var reject = new TeleportAck { StationId = -1, BNewHull = false };
            SendPeer(from, FedPacket.Pack(new[] { reject.Encode() }));
            return;
        }
        bool bNewHull = !StationSellsHull(validType, civ.Value, ship.HullType);
        var ack = new TeleportAck { StationId = targetStationId, BNewHull = bNewHull };
        SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
        Console.WriteLine($"[teleport] {from} ship {ship.Id} -> station {targetStationId} '{target!.Value.Name}' bNewHull={bNewHull} — TELEPORT_ACK(102)");

        if (bNewHull)
        {
            // Boot any boarded children (LeaveShip each) — FedSrv.CPP:7083-7087.
            foreach (var kid in ship.ChildShips.ToArray())
                PerformLeave(kid, ship);

            // Refund the ship's hardware value (FedSrv.CPP:7090-7105) —
            // computed BEFORE the parts are cleared, per native order, and
            // (like P108's board refund, Server.cs:2273-2282) deposited into
            // the side treasury: this port has no per-player wallet and no
            // MONEY_CHANGE codec, so the side accumulator is the money
            // primitive. State the simplification, not a silent drop.
            int refund = ShipValue(ship);
            if (refund > 0 && ship.SideId is { } refundSideId)
            {
                var rs = mission.Sides.FirstOrDefault(s => s.Id == refundSideId);
                if (rs is not null)
                {
                    rs.Money += refund;
                    // P118: broadcast the pooled-treasury refund to the whole
                    // side — the port of the native teleport refund
                    // (FedSrv.CPP:7202-7206), which likewise sends MONEY_CHANGE
                    // with sidFrom = NA (server-initiated) to the side group.
                    BroadcastMoneyChangeSideWide(rs.Id, refund, ship.Id);
                    Console.WriteLine($"[teleport] ship {ship.Id} refunded hardware value {refund} to side {rs.Id} treasury + MONEY_CHANGE(60) to side");
                }
            }

            // Sell off / strip all mounted parts (FedSrv.CPP:7107-7113) →
            // ClearLoadout, the P108-ready helper.
            ship.ClearLoadout();

            // Swap to the side's Lifepod hull (FedSrv.CPP:7116-7117) — the
            // same resolution P105/P107/P108 already use.
            var lifepodId = mission.Sides.First(s => s.Id == ship.SideId!.Value).Civilization.Lifepod;
            if (mission.Core.HullTypes.TryGetValue(lifepodId, out var lp))
            {
                ship.ChangeHull(lp);
                Console.WriteLine($"[teleport] ship {ship.Id} hull -> Lifepod {lp.HullId} (old hull not sellable at station {targetStationId})");
            }
            else
            {
                Console.WriteLine($"[teleport] ship {ship.Id} side lifepod hull {lifepodId} not in core — hull swap skipped (still stripped + refunded)");
            }
        }

        // Relocate to the target station's cluster and dock there — move any
        // boarded children first, then the ship itself (FedSrv.CPP:7124-7131).
        var destSector = mission.Sectors.First(s => s.Stations.Contains(target!.Value));
        ship.Sector = destSector;
        foreach (var kid in ship.ChildShips)          // empty in the bNewHull case (booted above)
            kid.StationId = targetStationId;
        ship.StationId = targetStationId;

        // Keep the owning peer's broadcast bookkeeping coherent — SHIP_STATUS
        // frames derive Sector from the snapshot, so point it at the station's
        // cluster like P105 does (Server.cs:3469-3470).
        _peerShips[from] = _peerShips[from] with { Sector = destSector };

        var note = $"[teleport] ship {ship.Id} docked at station {targetStationId} '{target.Value.Name}' in sector '{destSector.Name}'";
        Console.WriteLine(note);
        Notable?.Invoke(note);
    }

    private void ApplyLoadout(ConnectionId from, LiveShip live, BuyLoadoutRequest buy)
    {
        var ship = live.Ship;
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: no mission (ship not placed) — cannot apply loadout");
            return;
        }

        // Resolve the requested hull against the static core (the established
        // lookup pattern: StaticCore.HullTypes is keyed by HullId,
        // StaticCore.cs:148). Unknown hull ⇒ nothing to grant ⇒ no ACK.
        if (!mission.Core.HullTypes.TryGetValue(buy.HullId, out var hull))
        {
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: BUY_LOADOUT names unknown hull {buy.HullId} — no ACK (report, not fabricated)");
            return;
        }
        if (hull.HullId != ship.HullType.HullId)
        {
            // Ship's _hull is fixed at spawn (readonly), so a hull change can't
            // be hot-swapped here — Phase 2 territory. Grant the parts against
            // the current hull and be explicit that the ACK echoes the
            // REQUESTED hullId (the configuration granted), not the physical hull.
            Console.WriteLine($"[loadout] {from} ship {ship.Id}: requested hull {hull.HullId} ('{hull.Name}') differs from current {ship.HullType.HullId} — hull hot-swap not implemented (Phase 2); granting parts on the current hull, ACK echoes the requested hullId");
        }

        // Replace the ship's loadout with the requested parts. Mirror the
        // native ProcessShipLoadout rebuild (shipIGC.h:901-921).
        ship.ClearLoadout();
        int granted = 0, skipped = 0;
        foreach (var p in buy.Parts)
        {
            if (!mission.Core.PartTypes.TryGetValue(p.PartId, out var partType))
            {
                Console.WriteLine($"[loadout] {from} ship {ship.Id}: requested part {p.PartId} not in core — skipped (report, not fabricated)");
                skipped++;
                continue;
            }
            ship.MountPart(partType, p.Amount, p.MountId, p.FractionMounted);
            granted++;
        }

        // The ACK's tail reflects the ship's REAL mounted parts after the apply
        // (so the round-trip is byte-for-byte when every requested part mounted).
        var ackParts = ship.Parts.Select(x => x.ToExpandedPartData()).ToArray();
        bool allGranted = ackParts.Length == buy.Parts.Count && granted == buy.Parts.Count && skipped == 0;
        // fBoughtEverything = every requested part was actually mounted. Kept
        // false for the zero-part hull-only case so those bytes stay identical
        // to P26/P27/P28 (preserving the tested adopt-a-hull path exactly).
        bool fboughtEverything = buy.Parts.Count > 0 && allGranted;
        var ack = new BuyLoadoutAck
        {
            HullId = buy.HullId,
            FBoughtEverything = fboughtEverything,
            FLaunch = buy.FLaunch,
            Parts = ackParts,
        };
        SendPeer(from, FedPacket.Pack(new[] { ack.Encode() }));
        var note = $"[loadout] ship {ship.Id} adopted loadout hull {ack.HullId} + {ack.Parts.Count} part(s)";
        Console.WriteLine($"[loadout] {from} ship {ship.Id} <- BUY_LOADOUT_ACK hull {ack.HullId} nParts {ack.Parts.Count} fbought {fboughtEverything} fLaunch {ack.FLaunch}{(skipped > 0 ? $" ({skipped} skipped)" : "")}");
        Notable?.Invoke(note);

        // The real WinTrek client's Hangar screen "Launch" button sends
        // exactly this message with FLaunch=true and nothing else
        // (HangarScreen::OnButtonLaunch -> BuyLoadout(ship, true),
        // hangarscreen.cpp:145-160) -- it never sends a separate undock
        // request the way WOPR bots do (FM_C_DOCKED, see PerformUndock's own
        // doc comment). The client's BUY_LOADOUT_ACK handler deliberately
        // does NOT clear the launch lockdown when fLaunch is true
        // (appmsg.cpp:3180: "if (!pfmBuyLoadoutAck->fLaunch && IsLockedDown())
        // EndLockDown(...)") -- it is waiting for FM_S_SET_CLUSTER instead
        // (appmsg.cpp:991-996). Without this, a real player could never
        // launch at all: every click of the Hangar "Launch" button re-hits
        // HangarScreen::OnButtonLaunch's own "if (!IsLockedDown()) ... else
        // assert(false)" (hangarscreen.cpp:147-157) once the first click
        // leaves the lockdown permanently set, since nothing server-side ever
        // released it.
        if (buy.FLaunch && live.Ship.StationId != null)
        {
            PerformUndock(from, live);
        }
    }

    /// <summary>
    /// Arm the freshly-placed ship so a live bot can actually fire — the
    /// missing live half of the (unit-tested) combat slice. Requires the ship
    /// to be placed (<see cref="Ship.MountWeapon"/> resolves the weapon's
    /// projectile against the mission core). Picks, <b>per fixed hardpoint</b>,
    /// the cheapest energy-per-shot <c>Weapon</c> part type (dispersion-free,
    /// so a straight shot lands) whose projectile type exists in the core
    /// <em>and</em> whose <see cref="DataPartTypeIGC.PartMask"/> is a subset of
    /// that specific hardpoint's <see cref="DataHullTypeIGC.Hardpoint.PartMask"/>
    /// — the real engine's <c>CanMount</c> subset test (<c>src/Igc/hullTypeIGC.h:199-213</c>,
    /// <c>partType.PartMask &amp; ~hardpoint.PartMask == 0</c>). Different fixed
    /// hardpoints on the same hull may therefore end up with different weapon
    /// types — that is correct, faithful per-hardpoint behaviour, not a bug to
    /// unify. Non-fixed (turret/gunner) hardpoints are skipped:
    /// <see cref="Weapon.IsWeaponSelected"/> never selects a turret gun here
    /// (Weapon.cs:274-275), so mounting one would be a dead-end — worse than
    /// not mounting it. If the core offers no usable weapon at all, a fixed
    /// hardpoint has no PartMask-compatible weapon anywhere in the core, or the
    /// hull has no fixed hardpoints, the relevant slot is left unmounted and
    /// the omission is logged (not fabricated, never thrown).
    ///
    /// Weapon-compatibility scope, stated plainly (this slice's style — no
    /// shields, no economy, no lobby): the real engine consults only
    /// <c>Hardpoint.PartMask</c> for weapons; the hull-level
    /// <see cref="DataHullTypeIGC.PmEquipment"/> gate applies solely to
    /// non-weapon equipment (<c>hullTypeIGC.h:163-176</c>, the <c>mountID==0</c>
    /// <c>else</c> branch) and non-weapon equipment is already out of scope
    /// (no shields repo-wide). So this check is exactly hardpoint vs
    /// part-type <c>PartMask</c> for weapons — <c>PmEquipment</c> and
    /// <c>Ship</c>'s non-weapon equipment handling are deliberately untouched.
    /// </summary>
    private void MountCombatWeapon(Ship ship)
    {
        var mission = ship.Mission;
        if (mission is null)
        {
            Console.WriteLine("[combat] cannot mount a weapon: ship not placed (no mission)");
            return;
        }
        // The usable-weapon universe is exactly what it was before this task:
        // dispersion-free weapons whose projectile exists and has Power > 0,
        // ordered cheapest energy-per-shot first. This task adds the per-hardpoint
        // PartMask subset dimension; it does not change what makes a weapon usable.
        // Materialised once so each hardpoint can scan the same ordered list cheaply.
        var candidates = mission.Core.PartTypes.Values
            .Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (part: p, w: (DataPartTypeIGC.Weapon)p.Type))
            .Where(t => t.w.Dispersion == 0f
                        && mission.Core.ProjectileTypes.TryGetValue(t.w.ProjectileTypeId, out var ptyp) && ptyp.Power > 0f)
            .OrderBy(t => t.w.EnergyPerShot)
            .ToList();
        if (candidates.Count == 0)
        {
            Console.WriteLine("[combat] no usable weapon in core — ship left unarmed (a finding, not fabricated)");
            return;
        }

        var hardpoints = ship.HullType.Hardpoints;
        int fixedCount = 0, armed = 0;
        for (short mount = 0; mount < hardpoints.Length; mount++)
        {
            if (!hardpoints[mount].Fixed)
                continue;              // turret/gunner slot: never selects (Weapon.cs:274-275), so skipping is correct
            fixedCount++;
            short hpMask = hardpoints[mount].PartMask;
            var weaponRec = candidates.FirstOrDefault(t => (t.part.PartMask & ~hpMask) == 0);
            if (weaponRec.part == default)
            {
                Console.WriteLine($"[combat] ship {ship.Id}: fixed mount {mount} on hull '{ship.HullType.Name}' "
                    + $"(PartMask {hpMask}) has no PartMask-compatible weapon in core — left unmounted (a finding, not fabricated)");
                continue;
            }
            ship.MountWeapon(weaponRec.w, mount);   // the cheapest compatible weapon for THIS hardpoint
            Console.WriteLine($"[combat]   mount {mount}: '{weaponRec.part.Name}' (PartMask {weaponRec.part.PartMask}, {weaponRec.w.EnergyPerShot}/shot)");
            armed++;
        }
        if (armed == 0)
        {
            Console.WriteLine($"[combat] ship {ship.Id}: hull '{ship.HullType.Name}' has no fixed hardpoints (or none arming) — left unarmed (a finding, not fabricated)");
            return;
        }
        int nonFixed = hardpoints.Length - fixedCount;
        int unmounted = fixedCount - armed;
        Console.WriteLine($"[combat] ship {ship.Id} armed {armed} of {fixedCount} fixed hardpoints "
            + $"(per-hardpoint PartMask-compatible cheapest; {unmounted} fixed slot(s) with no compatible weapon left unmounted; "
            + $"hull {fixedCount} fixed/{hardpoints.Length} total, {nonFixed} non-fixed skipped)");
    }

    /// <summary>
    /// Handle a freshly-created projectile (<see cref="ServerHost.ProjectileCreatedEvent"/>,
    /// raised from <c>Sim/Weapon.cs</c> <c>FireWeapon</c> right after
    /// <c>sector.Add(projectile)</c> on the sim thread) by broadcasting a real
    /// <c>FM_CS_PROJECTILE_INSTANCE(12)</c> <see cref="ProjectileInstance"/>
    /// message to the same-side peers who can see the shot. This is P88: the
    /// first slice that lets a client actually SEE a projectile — until now a
    /// shot resolved entirely server-side and only its damage was observed.
    /// Scope is deliberately <b>same-side only</b>, matching every prior
    /// multi-peer-visibility broadcast (P58/P62/P63/P85/P86 are all same-side
    /// only; cross-side ship visibility is its own open roadmap item, Phase 5).
    /// The recipients are exactly: the shooter's own peer (who always sees it —
    /// that peer's ExportShip was sent at join, Server.cs:~1010) plus same-side
    /// teammates who have already received the shooter's <c>ExportShip(26)</c>
    /// (the <c>_teammateExportSent</c>-gated idiom <c>BroadcastShipStates</c>'s
    /// teammate loop already uses, Server.cs:539-557) — a teammate who has no
    /// client-side object for the shooter can't meaningfully receive the shot
    /// (yet), so the broadcast is gated on that export like every other
    /// teammate send. Cross-side/enemy projectile visibility is NOT attempted
    /// (needs Phase 5's cross-side EXPORT first — not built). Runs on the sim
    /// thread (the same one that owns <c>_peerShips</c> and broadcasts ship
    /// states), so no extra locking is needed — mirroring
    /// <see cref="BroadcastKillShip"/>.
    /// </summary>
    private void BroadcastProjectileInstance(Projectile p)
    {
        if (p.Launcher is not Ship shooter)
        {
            Console.WriteLine("[projectile] ProjectileCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            Console.WriteLine($"[projectile] shooter {shooter.Id} has no side — no PROJECTILE_INSTANCE broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as BroadcastShipStates stamps ServerShipUpdate.Time).
        uint timeCreate = (uint)TickIndex;

        var msg = ProjectileInstance.FromProjectile(p, timeCreate);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Shooter's own peer always receives it (that peer's own EXPORT was
            // sent at join). Teammates receive it only once they already hold
            // the shooter's ExportShip — the exact _teammateExportSent gate
            // (Server.cs:539-557) BroadcastShipStates' teammate loop uses for
            // every same-side teammate visibility send. Everyone else (cross-side
            // enemies, and same-side peers who never got the shooter's export)
            // is excluded.
            bool isShooter = ReferenceEquals(kv.Value.Ship, shooter);
            bool isVisibleTeammate = !isShooter
                                     && kv.Value.Ship.SideId == shooterSide
                                     && _teammateExportSent.Contains((kv.Key, shooter.Id));
            if (isShooter || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[projectile] ship {shooter.Id} fired -> PROJ_INST(12) type {p.ProjectileType.ProjectileTypeId} " +
                          $"pos {p.Position} vel {p.Velocity} fwd {p.Forward} life {p.ProjectileType.Lifespan} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a freshly-fired missile (<see cref="ServerHost.MissileCreatedEvent"/>,
    /// raised from <c>Sim/Ship.cs</c> <c>FireMissile</c> right after
    /// <c>sector.Add(missile)</c> on the sim thread) by broadcasting a real
    /// <c>FM_CS_FIRE_MISSILE(70)</c> <see cref="FireMissile"/> message to the
    /// same-side peers who can see the launch. This is P90's creation half:
    /// lets a client SEE a missile being fired (the P89 codec finally used in a
    /// live tick). Carries exactly one <see cref="MissileLaunchData"/> entry —
    /// this slice fires one missile at a time, not a salvo; a multi-missile
    /// salvo (<c>MissileLaunchData[]</c> with &gt; 1 entry per fire) is a separate,
    /// later extension, so <c>Entries</c> always has exactly one element here.
    /// Scope is deliberately <b>same-side only</b>, matching every prior
    /// multi-peer-visibility broadcast (P58/P62/P63/P85/P86/P88 are all same-side
    /// only). The recipients are exactly: the launcher's own peer (who always
    /// sees it — that peer's ExportShip was sent at join, Server.cs:~1010) plus
    /// same-side teammates who have already received the launcher's
    /// <c>ExportShip(26)</c> (the <c>_teammateExportSent</c>-gated idiom
    /// <c>BroadcastShipStates</c>'s teammate loop already uses, Server.cs:539-557)
    /// — the identical idiom <see cref="BroadcastProjectileInstance"/> uses,
    /// copied exactly. The lock/target fields are placeholders (this slice fires
    /// with no target and no lock accumulation; the homing target pipeline is a
    /// later task), so <c>Lock = 0</c>, <c>TargetType = 0</c>, <c>TargetId = NA(-1)</c>,
    /// <c>BDud = false</c>. Runs on the sim thread, so no extra locking — mirroring
    /// <see cref="BroadcastKillShip"/>. Cross-side/enemy visibility is NOT
    /// attempted (needs Phase 5's cross-side EXPORT first — not built).
    /// </summary>
    private void BroadcastFireMissile(Missile m)
    {
        if (m.Launcher is not Ship shooter)
        {
            Console.WriteLine("[missile] MissileCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            Console.WriteLine($"[missile] launcher {shooter.Id} has no side — no FIRE_MISSILE broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as BroadcastProjectileInstance stamps timeCreate=(uint)TickIndex).
        uint timeFired = (uint)TickIndex;

        var msg = new FireMissile
        {
            Lock = 0f,                               // no homing target this slice
            TimeFired = timeFired,
            MissileTypeId = m.MissileType.ExpendableTypeId,
            LauncherId = shooter.Id,
            ClusterId = m.OwnerSector.Id,
            TargetType = 0,                          // OT_* placeholder — no target this slice
            TargetId = -1,                           // NA — no target
            BDud = false,
            // Exactly one missile per fire — this slice always sends a single
            // missile, never a salvo (see class doc).
            Entries =
            [
                new MissileLaunchData(m.Position, m.Forward, m.Velocity, m.Id),
            ],
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Launcher's own peer always receives it (that peer's own EXPORT was
            // sent at join). Teammates receive it only once they already hold the
            // launcher's ExportShip — the exact _teammateExportSent gate (same as
            // BroadcastProjectileInstance, Server.cs:2002-2010). Everyone else is
            // excluded.
            bool isShooter = ReferenceEquals(kv.Value.Ship, shooter);
            bool isVisibleTeammate = !isShooter
                                     && kv.Value.Ship.SideId == shooterSide
                                     && _teammateExportSent.Contains((kv.Key, shooter.Id));
            if (isShooter || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[missile] ship {shooter.Id} fired -> FIRE_MISSILE(70) type {m.MissileType.ExpendableTypeId} " +
                          $"pos {m.Position} vel {m.Velocity} fwd {m.Forward} life {m.MissileType.Lifespan} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a missile that just expired (<see cref="ServerHost.MissileDestroyedEvent"/>,
    /// raised from <c>Sim/Missile.cs</c> <c>OnUpdate</c> alongside
    /// <c>sector.Remove(missile)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_MISSILE_DESTROYED(83)</c> <see cref="MissileDestroyed"/> message —
    /// the \"this missile was destroyed here\" notice — to the same same-side
    /// audience that saw it launch (<see cref="BroadcastFireMissile"/>). This is
    /// P90's destruction half: new territory P88 did not need (a projectile's
    /// expiry is silent), reached by modelling the missile's own expiry as the
    /// analogue of a ship's death (<c>Ship.ReceiveDamage</c> →
    /// <c>KillShipEvent</c>, <c>Sim/Ship.cs:494</c>). The position carried is
    /// where the missile <b>expired</b> (<c>m.Position</c> after its straight-line
    /// flight, ≈ <c>launchPosition + velocity·Lifespan</c>), not its launch
    /// position — the observable contract the verification asserts. Same-side
    /// only, same <c>_teammateExportSent</c>-gated audience, same sim-thread-no-
    /// lock reasoning as the creation broadcast.
    /// </summary>
    private void BroadcastMissileDestroyed(Missile m)
    {
        if (m.Launcher is not Ship shooter)
        {
            Console.WriteLine("[missile] MissileDestroyedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (shooter.SideId is not { } shooterSide)
        {
            Console.WriteLine($"[missile] launcher {shooter.Id} has no side — no MISSILE_DESTROYED broadcast");
            return;
        }

        var msg = new MissileDestroyed
        {
            Position = m.Position,              // where it expired, not where it launched
            ClusterId = m.OwnerSector.Id,
            MissileId = m.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            bool isShooter = ReferenceEquals(kv.Value.Ship, shooter);
            bool isVisibleTeammate = !isShooter
                                     && kv.Value.Ship.SideId == shooterSide
                                     && _teammateExportSent.Contains((kv.Key, shooter.Id));
            if (isShooter || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[missile] ship {shooter.Id} expired missile {m.Id} -> MISSILE_DESTROYED(83) " +
                          $"pos {m.Position} sector {m.OwnerSector.Id} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a freshly-deployed mine (<see cref="ServerHost.MineCreatedEvent"/>,
    /// raised from <c>Sim/Ship.cs</c> <c>DeployMine</c> right after
    /// <c>sector.Add(mine)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_EXPORT(26)</c> carrying an <c>OT_mine</c> (3)
    /// <see cref="DataMineExport"/> to the same-side peers who can see the
    /// deployment. This is P94's creation half: the first live use of the P93
    /// <see cref="ExportMine"/> codec. Unlike a missile, a mine needs no dedicated
    /// instance message — the reference server creates it through the same
    /// generic <c>CreateObject(..., OT_mine, &amp;obj, sizeof(obj))</c> path every
    /// other exported object uses (<c>FedSrv.CPP:10072</c>) and it is exported by
    /// <c>CmineIGC::Export</c> (<c>src/Igc/mineigc.cpp:158-185</c>), so the
    /// generic EXPORT envelope is the real wire notice. The message is built by
    /// <see cref="ExportMine.FromMine"/>, which encodes the freshly-armed
    /// fraction 1.0 via the div-240 <see cref="CompactQuant.Percent"/> convention
    /// and sets <c>createNow</c>=true (matching the reference server's
    /// <c>SetCreateNow()</c> after creation, <c>FedSrv.CPP:10072-10073</c>).
    /// Scope is deliberately <b>same-side only</b>, matching every prior
    /// multi-peer-visibility broadcast (P58/P62/P63/P85/P86/P88/P90 are all
    /// same-side only). The recipients are exactly: the laying ship's own peer
    /// (who always sees it — that peer's ExportShip was sent at join,
    /// Server.cs:~1010) plus same-side teammates who have already received the
    /// layer's <c>ExportShip(26)</c> (the <c>_teammateExportSent</c>-gated idiom
    /// <c>BroadcastShipStates</c>'s teammate loop uses, Server.cs:539-557) — the
    /// identical idiom <see cref="BroadcastProjectileInstance"/>/
    /// <see cref="BroadcastFireMissile"/> use, copied exactly. Cross-side/enemy
    /// mine visibility is NOT attempted — a real mine is arguably a hazard any
    /// side should see, but that needs Phase 5's cross-side EXPORT first, and is
    /// not this task's problem. Runs on the sim thread, so no extra locking.
    /// </summary>
    private void BroadcastExportMine(Mine m)
    {
        if (m.Launcher is not Ship layer)
        {
            Console.WriteLine("[mine] MineCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (layer.SideId is not { } layerSide)
        {
            Console.WriteLine($"[mine] layer {layer.Id} has no side — no EXPORT(mine) broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as the other broadcasts stamp timeCreate=(uint)TickIndex).
        uint timeCreate = (uint)TickIndex;
        var msg = ExportMine.FromMine(m, timeCreate, launcherId: layer.Id, sideId: layerSide);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Layer's own peer always receives it (that peer's own EXPORT was
            // sent at join). Teammates receive it only once they already hold
            // the layer's ExportShip — the exact _teammateExportSent gate (same
            // as BroadcastProjectileInstance, Server.cs:2002-2010). Everyone
            // else (cross-side enemies, and same-side peers who never got the
            // layer's export) is excluded.
            bool isLayer = ReferenceEquals(kv.Value.Ship, layer);
            bool isVisibleTeammate = !isLayer
                                     && kv.Value.Ship.SideId == layerSide
                                     && _teammateExportSent.Contains((kv.Key, layer.Id));
            if (isLayer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[mine] ship {layer.Id} deployed -> EXPORT(26)/OT_mine {m.MineType.ExpendableTypeId} " +
                          $"pos {m.Position} life {m.MineType.Lifespan} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a mine that just expired (<see cref="ServerHost.MineDestroyedEvent"/>,
    /// raised from <c>Sim/Mine.cs</c> <c>OnUpdate</c> alongside
    /// <c>sector.Remove(mine)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_MINE_DESTROYED(84)</c> <see cref="MineDestroyed"/> message — the
    /// \"this mine blew up\" notice (<c>Messages.h:603-606</c>) — to the same
    /// same-side audience that saw it deploy (<see cref="BroadcastExportMine"/>).
    /// This is P94's destruction half, reached by modelling the mine's own
    /// expiry as the analogue of a ship's death (<c>Ship.ReceiveDamage</c> →
    /// <c>KillShipEvent</c>, <c>Sim/Ship.cs:494</c>), exactly as
    /// <see cref="BroadcastMissileDestroyed"/> does for missiles. Because a mine
    /// never moves, the position carried (<c>m.Position</c>) is the <b>same</b>
    /// deployment position the creation export carried — the observable
    /// \"the mine didn't move\" contract this task's verification asserts
    /// explicitly. Same-side only, same <c>_teammateExportSent</c>-gated
    /// audience, same sim-thread-no-lock reasoning as the other broadcasts.
    /// </summary>
    private void BroadcastMineDestroyed(Mine m)
    {
        if (m.Launcher is not Ship layer)
        {
            Console.WriteLine("[mine] MineDestroyedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (layer.SideId is not { } layerSide)
        {
            Console.WriteLine($"[mine] layer {layer.Id} has no side — no MINE_DESTROYED broadcast");
            return;
        }

        var msg = new MineDestroyed
        {
            ClusterId = m.OwnerSector.Id,
            MineId = m.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            bool isLayer = ReferenceEquals(kv.Value.Ship, layer);
            bool isVisibleTeammate = !isLayer
                                     && kv.Value.Ship.SideId == layerSide
                                     && _teammateExportSent.Contains((kv.Key, layer.Id));
            if (isLayer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[mine] ship {layer.Id} expired mine {m.Id} -> MINE_DESTROYED(84) " +
                          $"pos {m.Position} sector {m.OwnerSector.Id} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a freshly-deployed probe (<see cref="ServerHost.ProbeCreatedEvent"/>,
    /// raised from <c>Sim/Ship.cs</c> <c>DeployProbe</c> right after
    /// <c>sector.Add(probe)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_EXPORT(26)</c> carrying an <c>OT_probe</c> (4)
    /// <see cref="DataProbeExport"/> to the same-side peers who can see the
    /// deployment. This is P99's creation half: the first live use of the P98
    /// <see cref="ExportProbe"/> codec. Like a mine, a probe needs no dedicated
    /// instance message — the reference server creates it through the same
    /// generic <c>CreateObject(..., OT_probe, &amp;obj, sizeof(obj))</c> path
    /// every other exported object uses and it is exported by
    /// <c>CprobeIGC::Export</c> (<c>src/Igc/probeigc.cpp:535-568</c>), so the
    /// generic EXPORT envelope is the real wire notice. The message is built by
    /// <see cref="ExportProbe.FromProbe"/>, which encodes the targetless probe
    /// with <c>otTarget</c>/<c>oidTarget</c> = NA/-1 (this slice never sets a
    /// target — see <c>Probe.cs</c> remarks) and <c>createNow</c>=true. Scope is
    /// deliberately <b>same-side only</b>, matching every prior ordnance
    /// broadcast (P58/P62/P63/P85/P86/P88/P90/P94). The recipients are exactly:
    /// the deploying ship's own peer (who always sees it — that peer's ExportShip
    /// was sent at join, Server.cs:~1010) plus same-side teammates who have
    /// already received the deployer's <c>ExportShip(26)</c> (the
    /// <c>_teammateExportSent</c>-gated idiom — the identical loop
    /// <see cref="BroadcastExportMine"/> uses, copied exactly). Cross-side/enemy
    /// probe visibility is NOT attempted (Phase 5's cross-side EXPORT remains
    /// unbuilt). Runs on the sim thread, so no extra locking.
    /// </summary>
    private void BroadcastExportProbe(Probe p)
    {
        if (p.Launcher is not Ship deployer)
        {
            Console.WriteLine("[probe] ProbeCreatedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (deployer.SideId is not { } deployerSide)
        {
            Console.WriteLine($"[probe] deployer {deployer.Id} has no side — no EXPORT(probe) broadcast");
            return;
        }

        // uint Time = the server tick index (this project's uint-Time convention,
        // same as the mine/other broadcasts stamp timeCreate=(uint)TickIndex).
        uint timeCreate = (uint)TickIndex;
        var msg = ExportProbe.FromProbe(p, timeCreate, launcherId: deployer.Id, sideId: deployerSide);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            // Deployer's own peer always receives it. Teammates receive it only
            // once they already hold the deployer's ExportShip — the exact
            // _teammateExportSent gate. Everyone else (cross-side enemies, and
            // same-side peers who never got the deployer's export) is excluded.
            bool isDeployer = ReferenceEquals(kv.Value.Ship, deployer);
            bool isVisibleTeammate = !isDeployer
                                     && kv.Value.Ship.SideId == deployerSide
                                     && _teammateExportSent.Contains((kv.Key, deployer.Id));
            if (isDeployer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[probe] ship {deployer.Id} deployed -> EXPORT(26)/OT_probe {p.ProbeType.ExpendableTypeId} " +
                          $"pos {p.Position} life {p.ProbeType.Lifespan} side {deployerSide} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a probe that just expired (<see cref="ServerHost.ProbeDestroyedEvent"/>,
    /// raised from <c>Sim/Probe.cs</c> <c>OnUpdate</c> alongside
    /// <c>sector.Remove(probe)</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_PROBE_DESTROYED(85)</c> <see cref="ProbeDestroyed"/> message —
    /// the &quot;this probe was destroyed here&quot; notice
    /// (<c>Messages.h:608-611</c>) — to the same same-side audience that saw it
    /// deploy (<see cref="BroadcastExportProbe"/>). This is P99's destruction
    /// half, reached by modelling the probe's own expiry as the analogue of a
    /// ship's death (<c>Ship.ReceiveDamage</c> → <c>KillShipEvent</c>,
    /// <c>Sim/Ship.cs:494</c>), exactly as
    /// <see cref="BroadcastMineDestroyed"/> does for mines. Because a probe
    /// never moves, the position carried (<c>p.Position</c>) is the <b>same</b>
    /// deployment position the creation export carried — the observable
    /// &quot;the probe didn't move&quot; contract this task's verification
    /// asserts explicitly. Same-side only, same <c>_teammateExportSent</c>-gated
    /// audience, same sim-thread-no-lock reasoning as the other broadcasts.
    /// </summary>
    private void BroadcastProbeDestroyed(Probe p)
    {
        if (p.Launcher is not Ship deployer)
        {
            Console.WriteLine("[probe] ProbeDestroyedEvent with a non-Ship launcher — ignored");
            return;
        }
        if (deployer.SideId is not { } deployerSide)
        {
            Console.WriteLine($"[probe] deployer {deployer.Id} has no side — no PROBE_DESTROYED broadcast");
            return;
        }

        var msg = new ProbeDestroyed
        {
            ClusterId = p.OwnerSector.Id,
            ProbeId = p.Id,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        int n = 0;
        foreach (var kv in _peerShips)
        {
            bool isDeployer = ReferenceEquals(kv.Value.Ship, deployer);
            bool isVisibleTeammate = !isDeployer
                                     && kv.Value.Ship.SideId == deployerSide
                                     && _teammateExportSent.Contains((kv.Key, deployer.Id));
            if (isDeployer || isVisibleTeammate)
            {
                SendPeer(kv.Key, packet);
                n++;
            }
        }
        Console.WriteLine($"[probe] ship {deployer.Id} expired probe {p.Id} -> PROBE_DESTROYED(85) " +
                          $"pos {p.Position} sector {p.OwnerSector.Id} -> {n} same-side peer(s)");
    }

    /// <summary>
    /// Handle a warp bombed by a missile (<see cref="ServerHost.WarpBombEvent"/>,
    /// raised from <c>Sector.ResolveWarpBombTriggers</c> on the sim thread when a
    /// live <see cref="Missile"/> reaches a warp, P101) by broadcasting a real
    /// <c>FM_S_WARP_BOMB(188)</c> <see cref="WarpBomb"/> message — the
    /// "aleph destabilized" notice (<c>Inc/Messages.h:969-973</c>) that a missile
    /// hit a warp and will explode there after the delay
    /// (<see cref="Model.Sector.WarpBombDelaySeconds"/>). <c>timeExplosion</c> is
    /// the future tick at which the sim resolves the real explosion at the warp —
    /// the delay converted to server ticks (<c>_period</c>), stamped in this
    /// project's uint-TickIndex wire-time convention (<c>(uint)TickIndex</c>,
    /// same as every other broadcast).
    /// <para><b>WHOLE MISSION — the one genuine behavioural departure from every
    /// prior ordnance broadcast in this project.</b> Native sends <c>WARP_BOMB</c>
    /// to <c>GetGroupRealSides()</c> (<c>FedSrv.CPP:12558</c>) — all real sides,
    /// not a same-side gate. Every prior ordnance broadcast
    /// (<c>BroadcastProjectileInstance</c>/<c>BroadcastFireMissile</c>/
    /// <c>BroadcastMineDestroyed</c>/<c>BroadcastProbeDestroyed</c>, P88/P90/P94/P99)
    /// is same-side only; this one reaches <b>every</b> connected peer, on any
    /// side. Do not "fix" it to same-side to match the neighbours. Runs on the
    /// sim thread (same one that owns <c>_peerShips</c>), so no extra locking.</para>
    /// </summary>
    private void BroadcastWarpBomb(ResolvedWarp warp, Missile missile)
    {
        long detonationTick = TickIndex + (long)Math.Round(Sector.WarpBombDelaySeconds / _period);
        var msg = new WarpBomb
        {
            TimeExplosion = (uint)detonationTick,
            WarpIdBombed = warp.Warp.WarpId,
            ExpendableIdMissile = missile.MissileType.ExpendableTypeId,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Whole mission: every connected peer with a seated ship, regardless of
        // side (GetGroupRealSides()).
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        Console.WriteLine($"[warpbomb] warp {warp.Warp.WarpId} bombed by missile {missile.Id} " +
                          $"(missileType {missile.MissileType.ExpendableTypeId}) -> WARP_BOMB(188) " +
                          $"timeExplosion tick {msg.TimeExplosion} -> {targets.Length} peer(s) [WHOLE MISSION]");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a combat-killed asteroid (<see cref="ServerHost.KillAsteroidEvent"/>,
    /// raised from <c>Model.Sector.ResolveCombatCollisions</c> on the sim thread
    /// when a real ordnance hit crosses the rock's <see cref="Asteroid.Fraction"/>
    /// to zero, P102) by broadcasting a real <c>FM_S_ASTEROID_DESTROYED(86)</c>
    /// <see cref="AsteroidDestroyed"/> message — the &quot;this asteroid was
    /// destroyed here&quot; notice (<c>Messages.h:613-617</c>).
    /// <para><b>WHOLE MISSION.</b> Native sends <c>ASTEROID_DESTROYED</c> from the
    /// plain <c>(IAsteroidIgc, bool)</c> overload to <c>GetGroupRealSides()</c>
    /// (<c>FedSrv.CPP:12418</c>) — all real sides, not a same-side gate, the same
    /// audience P101 settled for <c>WARP_BOMB</c>. Do not &quot;fix&quot; it to
    /// same-side to match the neighbouring ship/probe/mine broadcasts. Runs on the
    /// sim thread (same one that owns <c>_peerShips</c>), so no extra locking.
    /// <c>explodeF</c> is true from the only call site this task implements (combat
    /// death), transmitted verbatim.</para>
    /// </summary>
    private void BroadcastAsteroidDestroyed(Asteroid a, bool explodeF)
    {
        var msg = new AsteroidDestroyed
        {
            ClusterId = a.Data.ClusterId,
            AsteroidId = a.Data.AsteroidId,
            ExplodeF = explodeF,
        };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        // Whole mission: every connected peer with a seated ship, regardless of
        // side (GetGroupRealSides(), FedSrv.CPP:12418).
        var targets = _peerShips.Select(kv => kv.Key).ToArray();

        Console.WriteLine($"[asteroid] asteroid {a.Data.AsteroidId} '{a.Data.Name}' destroyed (explodeF={explodeF}) " +
                          $"=> ASTEROID_DESTROYED(86) sector {a.Data.ClusterId} -> {targets.Length} peer(s) [WHOLE MISSION]");
        foreach (var t in targets)
            SendPeer(t, packet);
    }

    /// <summary>
    /// Handle a simulated ship death (<see cref="ServerHost.KillShipEvent"/>, raised
    /// from <c>Ship.ReceiveDamage</c> on the sim thread) by broadcasting a real
    /// <c>FM_S_KILL_SHIP(105)</c> <see cref="KillShip"/> message to the peers that
    /// can see the victim. This is the live half of the (unit-tested) combat slice:
    /// the projectiles and collision pass already kill the ship in simulation; this
    /// tells every connected peer whose ship shares the victim's sector that it
    /// happened. Runs on the sim thread (the site raises it inside the mission
    /// tick, same thread that owns <see cref="_peerShips"/> and broadcasts ship
    /// states), so no extra locking is needed.
    /// </summary>
    private void BroadcastKillShip(
        double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2)
    {
        // Resolve the victim and (if it is a ship) the credited attacker to ids.
        if (ship is not Ship victim)
        {
            Console.WriteLine("[combat] KillShipEvent with a non-Ship victim — ignored");
            return;
        }
        short killerId = -1;                       // NA
        IgcObjectType killerType = IgcObjectType.Ship;
        if (launcher is Ship attacker) killerId = attacker.Id;

        // ── P107: the bKilled gate (FedSrv.CPP:11770-11785) ──
        // First, the owning peer — the port's IsPlayer() proxy (native
        // FedSrv.CPP:11772 pfsShip->IsPlayer()). Hoisted above the kill
        // broadcasts where P105 originally resolved it, because the eject
        // gate needs it. Non-player (bot) ships have no owning peer.
        ConnectionId? owner = null;
        foreach (var kv in _peerShips)
            if (ReferenceEquals(kv.Value.Ship, victim)) { owner = kv.Key; break; }

        // bLifepod = (pshipParent == NULL) && baseHull.HasCapability(c_habmLifepod)
        //   → port simplifications: no boarded/child ships yet (cluster E), so the
        //     (pshipParent==NULL) conjunct drops; the "already a lifepod" half is
        //     a real capability test on the CURRENT base hull (bit c_habmLifepod =
        //     0x04, igc.h:728; helper DataHullTypeIGC.HasCapability).
        // bKilled = (!bEjectPods) || bLifepod || (amount < 0.0f)
        //   → bEjectPods is always enabled (MissionParams not decoded in this port —
        //     stated plainly in IN_SPACE_EJECT_P107.md); no caller passes a negative
        //     amount into KillShipEvent (combat amounts are > 0, suicide = 1.0; the
        //     negative-amount repair path in Ship.ReceiveDamage never reaches the
        //     death event — it repairs and returns c_drNoDamage).
        //   → a non-player (bot) ship is always bKilled (native else branch,
        //     FedSrv.CPP:11781-11785).
        bool bLifepod = victim.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
        bool bKilled = (owner is null) || bLifepod || amount < 0.0f;

        if (!bKilled && EjectSurvivor(victim, launcher, p1, p2))
        {
            // Genuinely different code path from P103/P105: the ship survives
            // in space as a drifting Lifepod hull. No KILL_SHIP(105), no
            // SHIP_DELETE(20), no respawn/ENTER_LIFEPOD(166) — and the ship is
            // NOT removed from the sector (no SetCluster(NULL)).
            return;
        }

        // Peers whose ship is in the same sector as the victim can see the kill.
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Sector == victim.Sector)
            .Select(kv => kv.Key)
            .ToArray();

        Console.WriteLine($"[combat] ship {victim.Id} killed by {(killerId < 0 ? "(none)" : $"ship {killerId}")} " +
                          $"at sim-t {now:R} — KILL_SHIP(105) -> {targets.Length} peer(s)");
        if (targets.Length == 0) return;

        var msg = KillShip.FromVictim(victim.Id, killerType, killerId);
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });
        foreach (var t in targets)
            SendPeer(t, packet);

        // P103 belt-and-suspenders: alongside the KILL_SHIP(105) death notice,
        // emit the native's separate FM_S_SHIP_DELETE(20) for the same death.
        // The native client uses SHIP_DELETE to actually remove the client-side
        // object (appmsg.cpp:470-476: SDR_KILLED → SetCluster(NULL)) and
        // KILL_SHIP to play the death effect — both are needed, SHIP_DELETE is
        // genuinely redundant only by KILL_SHIP's own message being separate.
        // This slice emits only SDR_KILLED(1) — the one reason the death path
        // can drive without a visibility-culling system (see
        // SUICIDE_SHIP_DELETE_P103.md). Audience is identical to the KILL_SHIP
        // above: the same `targets` peers in the victim's sector.
        var shipDelete = ShipDelete.ForKilledShip(victim.Id);
        byte[] deletePacket = FedPacket.Pack(new[] { shipDelete.Encode() });
        foreach (var t in targets)
            SendPeer(t, deletePacket);
        Console.WriteLine($"[combat] ship {victim.Id} — SHIP_DELETE(20) SDR_KILLED -> {targets.Length} peer(s)");

        // P105 death-respawn, hooked at the exact seam P103 wired (ShipKilled →
        // this method) and sharing the same trigger. The native function
        // (FedSrv.CPP:12026-12074) turns a player's true death into a docked
        // Lifepod at a friendly station plus a private ENTER_LIFEPOD(166) to
        // that player only; bots just stay dead (native else:
        // FedSrv.CPP:12080-12085). Three guards reproduce it faithfully:
        //  (1) owner — only a peer-owned ship is a player (this port's player/
        //      bot distinction IS peer ownership; a ship with no owning peer in
        //      _peerShips is a bot/unowned ship and is NOT respawned);
        //  (2) a friendly station anywhere (FindFriendlyStation — the simplified
        //      stand-in for native's nearest-search + GetBase fallback);
        //  (3) implicit — FindFriendlyStation returns null when SideId is null,
        //      so a side-less ship is never respawned either.
        // No friendly station ⇒ no respawn, exactly like native's
        // `if (pstationDest)` guard (FedSrv.CPP:12055): the ship simply stays
        // gone, whatever SHIP_DELETE already said. (`owner` was resolved above,
        // before the P107 eject gate; only the guard remains here.)
        if (owner is null)
        {
            Console.WriteLine($"[lifepod] ship {victim.Id} has no owning peer (bot/non-player) — no respawn, stays dead");
            return;
        }
        DataStationIGC? station = FindFriendlyStation(victim);
        if (station is null)
        {
            Console.WriteLine($"[lifepod] player ship {victim.Id} side {(victim.SideId?.ToString() ?? "(none)")} has no friendly station anywhere — no respawn (native FedSrv.CPP:12055 guard)");
            return;
        }
        var dest = station.Value;                             // non-null capture past the guard
        var mission = victim.Mission!;
        // A valid station's ClusterId always names a sector of this mission
        // (DataStationIGC decoding guarantees it) — the station's own cluster
        // is where the Lifepod docks (native SetCluster(pcluster)).
        var destSector = mission.Sectors.First(s => s.Id == dest.ClusterId);
        var side = mission.Sides.FirstOrDefault(s => s.Id == victim.SideId!.Value);
        if (side is null)
        {
            Console.WriteLine($"[lifepod] player ship {victim.Id} side {victim.SideId} not in mission sides — no respawn");
            return;
        }
        if (!mission.Core.HullTypes.TryGetValue(side.Civilization.Lifepod, out var lp))
        {
            Console.WriteLine($"[lifepod] player ship {victim.Id} side {victim.SideId}: lifepod hull {side.Civilization.Lifepod} not in core — no respawn");
            return;
        }

        victim.Sector = destSector;      // SetCluster: out of the old sector, into the station's cluster (FedSrv.CPP:12045→12067)
        victim.ChangeHull(lp);           // SetBaseHullType(pht) — side's Lifepod hull (FedSrv.CPP:12052)
        victim.StationId = dest.StationId; // SetStation + ShipStatusDocked (FedSrv.CPP:12064-12065)

        // Keep the owning peer's broadcast bookkeeping coherent: the status
        // frames (BroadcastShipStates / MakeOwnShipStatus) derive Sector from
        // the LiveShip snapshot, so point it at the station's cluster too —
        // the next SHIP_STATUS(90) shows the Lifepod docked there.
        var conn = owner.Value;                               // non-null capture past the guard
        _peerShips[conn] = _peerShips[conn] with { Sector = destSector };

        var enter = EnterLifepod.ForPlayer(victim.Id);
        SendPeer(conn, FedPacket.Pack(new[] { enter.Encode() }));   // FM_S_ENTER_LIFEPOD(166) → own peer only
        Console.WriteLine($"[lifepod] player ship {victim.Id} -> docked Lifepod (hull {lp.HullId}) at station {dest.StationId} '{dest.Name}' " +
                          $"in sector '{destSector?.Name ?? "?"}' ; ENTER_LIFEPOD(166) -> own peer only");
    }

    /// <summary>
    /// P105's deliberately simplified stand-in for the native
    /// <c>FindTarget(..., c_ttFriendly | c_ttStation | c_ttNearest |
    /// c_ttAnyCluster, ..., c_sabmRestart)</c> nearest-friendly-station search
    /// (<c>FedSrv.CPP:12034-12038</c>). Native picks the spatially-nearest
    /// friendly station in any cluster, falling back to the mission base
    /// (<c>GetBase(pside)</c>); this port has <b>neither</b> a spatial
    /// nearest-search nor a "mission base" concept (confirmed — grep finds
    /// neither in <c>Sector.cs</c>/<c>Mission.cs</c>), so it returns <b>any</b>
    /// station whose <see cref="DataStationIGC.SideId"/> equals the ship's
    /// <see cref="Ship.SideId"/>, first-found in the mission's deterministic
    /// sector-then-station order. Stated plainly (not a silent drop): "best"
    /// station is out of scope — any one friendly match is fine, per the task.
    /// Returns <c>null</c> when no friendly station exists anywhere, mirroring
    /// native's no-<c>pstationDest</c> path (no respawn). Runs on the sim
    /// thread (called from <see cref="BroadcastKillShip"/>).
    /// </summary>
    private static DataStationIGC? FindFriendlyStation(Ship ship)
    {
        if (ship.SideId is not { } sideId || ship.Mission is not { } mission)
            return null;
        foreach (var sector in mission.Sectors)
            foreach (var station in sector.Stations)
                if (station.SideId == sideId)
                    return station;
        return null;
    }

    /// <summary>
    /// P107 — the in-space eject: the <c>!bKilled</c> branch of the native
    /// <c>KillShipEvent</c> (<c>FedSrv.CPP:11918-12022</c>). The dying player's
    /// ship survives, weakened, as a drifting Lifepod hull <b>still in the
    /// same sector</b> — not removed, not killed, no respawn. The caller
    /// (<see cref="BroadcastKillShip"/>) has already decided <c>!bKilled</c>
    /// via the <c>FedSrv.CPP:11770-11785</c> gate; this method only guards the
    /// data it needs (side/civilization/lifepod hull resolvable) and performs
    /// the port-relevant slice of the eject mechanics:
    /// <list type="bullet">
    /// <item>hull swap to the side's civilization's Lifepod hull
    /// (<c>pship-&gt;SetBaseHullType(pht)</c>, <c>FedSrv.CPP:11942</c>);</item>
    /// <item>reposition/reorient/velocity per the native math — an outward
    /// burst away from the killer's position when available, a random
    /// direction otherwise, position staying at the impact point
    /// <c>p1</c> (the parent-branch offset, FedSrv.CPP:11947-11953, is
    /// skipped — no boarded ships);</item>
    /// <item>an outward velocity and a <c>pi·2</c> roll spin
    /// (<c>FedSrv.CPP:12003,12008</c>);</item>
    /// <item>a real <c>FM_S_EJECT(77)</c> broadcast to <b>everyone flying in
    /// the same sector</b> — <c>GetGroupSectorFlying(pcluster)</c>
    /// (<c>FedSrv.CPP:12011-12021</c>), a sector-scoped, <b>cross-side</b>
    /// audience (distinct from <c>_teammateExportSent</c>'s same-side gate and
    /// from P101/P102's mission-wide <c>WARP_BOMB</c>/<c>ASTEROID_DESTROYED</c>
    /// broadcasts — this one is only to the ships in this one sector).</item>
    /// </list>
    /// The ship is deliberately <b>not</b> removed from the sector and gets no
    /// <c>SHIP_DELETE</c>/<c>KILL_SHIP</c>/<c>ENTER_LIFEPOD</c> — this is the
    /// mutually-exclusive sibling of P103/P105's true-death branch. Native also
    /// clears missiles targeting the ship (<c>FedSrv.CPP:11925-11932</c>); this
    /// port's <see cref="Missile"/> carries no target back-reference, so that
    /// step is a structural no-op (noted, not silently dropped). Returns
    /// <c>true</c> when the eject ran and the caller should stop processing
    /// this death as a kill; <c>false</c> when the required data is missing
    /// (should not happen for a live player ship) so the caller falls through
    /// to the kill path. Runs on the sim thread (called from
    /// <see cref="BroadcastKillShip"/>).
    /// </summary>
    private bool EjectSurvivor(Ship victim, IModelIgc? launcher, Vector3 p1, Vector3 p2)
    {
        var pcluster = victim.Sector;
        var mission = victim.Mission;
        if (pcluster is null || mission is null || victim.SideId is not { } sideId)
        {
            Console.WriteLine($"[eject] player ship {victim.Id} lacks sector/mission/side — cannot eject, treating as death");
            return false;
        }
        var side = mission.Sides.FirstOrDefault(s => s.Id == sideId);
        if (side is null)
        {
            Console.WriteLine($"[eject] player ship {victim.Id} side {sideId} not in mission sides — cannot eject, treating as death");
            return false;
        }
        short lifepodId = side.Civilization.Lifepod;
        if (!mission.Core.HullTypes.TryGetValue(lifepodId, out var pht))
        {
            Console.WriteLine($"[eject] player ship {victim.Id} side {sideId}: lifepod hull {lifepodId} not in core — cannot eject, treating as death");
            return false;
        }

        // v = the source ship's velocity (FedSrv.CPP:11935). No boarded ships
        // (cluster E) ⇒ source ship == the victim itself.
        Vector3 v = victim.Velocity;

        // (Missiles targeting this ship would be cleared here — FedSrv.CPP:11925-11932 —
        // but this port's Missile tracks no target; structural no-op.)

        victim.ChangeHull(pht);                 // SetBaseHullType(pht), FedSrv.CPP:11942

        // ── position / orientation / velocity math (FedSrv.CPP:11944-12006) ──
        // Parent branch (FedSrv.CPP:11947-11953) omitted — no boarded ships.
        Vector3 f = RandomDirection();         // Vector::RandomDirection(), vector.cpp:37-49
        bool bLook;
        Vector3 vectorLook;
        if (launcher is not null)
        {
            // plauncher = (source ship of) the credited killer, FedSrv.CPP:11960-11963.
            // No turret chains ⇒ a Ship launcher is its own source ship. IModelIgc
            // is a marker interface, so Position comes from the concrete Model
            // (Ship or another model); a non-Model launcher can't be located.
            if (launcher is Ship attackerShip)
            {
                bool sameSector = attackerShip.Sector == pcluster;   // GetCluster() == pcluster, FedSrv.CPP:11964
                if (sameSector) { bLook = true; vectorLook = attackerShip.Position; }
                else { bLook = false; vectorLook = default; }
            }
            else if (launcher is Model attackerModel)
            {
                bLook = true; vectorLook = attackerModel.Position;
            }
            else { bLook = false; vectorLook = default; }
        }
        else { bLook = true; vectorLook = p2; }  // no launcher → look at p2 (FedSrv.CPP:11974-11978)

        Vector3 orientationForward = f;          // o.Set(f) default (FedSrv.CPP:11995,11998)
        if (bLook)
        {
            Vector3 dp = vectorLook - p1;
            float length2 = dp.LengthSquared();
            if (length2 >= 1.0f)                 // FedSrv.CPP:11985
            {
                orientationForward = dp;         // o.Set(dp), FedSrv.CPP:11987
                // Bend the random forward slightly toward the killer:
                // fNew = f + dp*(8/sqrt(length2)), FedSrv.CPP:11989-11992.
                Vector3 fNew = f + dp * (8.0f / MathF.Sqrt(length2));
                float l2 = fNew.LengthSquared();
                if (l2 > 0.01f) f = fNew / MathF.Sqrt(l2);
            }
        }

        Vector3 position = p1;                   // position = p1 (parentless branch), FedSrv.CPP:12000
        v -= f * 100.0f;                         // outward burst, FedSrv.CPP:12003
        victim.Position = position;              // SetPosition, FedSrv.CPP:12004
        victim.Velocity = v;                     // SetVelocity, FedSrv.CPP:12005
        victim.Orientation.Set(orientationForward); // SetOrientation(o), FedSrv.CPP:12006
        victim.SetTurnRate(ControlData.AxisRoll, 2f * MathF.PI); // SetCurrentTurnRate(c_axisRoll, pi*2), FedSrv.CPP:12008

        // EJECT(77) to everyone flying in this sector — cross-side, sector-scoped.
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Sector == pcluster)
            .Select(kv => kv.Key)
            .ToArray();
        var eject = Eject.ForSurvivor(victim.Id, position, v, f);
        byte[] packet = FedPacket.Pack(new[] { eject.Encode() });
        foreach (var t in targets) SendPeer(t, packet);
        Console.WriteLine($"[eject] player ship {victim.Id} ejected -> Lifepod hull {pht.HullId} at {position}, vel ({v.X:0.#},{v.Y:0.#},{v.Z:0.#}), fwd ({f.X:0.##},{f.Y:0.##},{f.Z:0.##}); EJECT(77) -> {targets.Length} peer(s) in sector {pcluster.Id}; hull stays in sector (not removed), no SHIP_DELETE/KILL_SHIP");
        return true;
    }

    /// <summary>
    /// <c>Vector::RandomDirection()</c> (<c>vector.cpp:37-49</c>) — a uniformly
    /// distributed unit direction: <c>sinPitch ∈ [-1,1]</c>, <c>yaw ∈ [0, 2π)</c>,
    /// <c>(cos(yaw)·cosPitch, sin(yaw)·cosPitch, sinPitch)</c>. Used by the
    /// eject branch's fallback heading (no killer in the sector).
    /// </summary>
    private static Vector3 RandomDirection()
    {
        float sinPitch = (float)(_rand.NextDouble() * 2.0 - 1.0);
        float cosPitch = MathF.Sqrt(1.0f - sinPitch * sinPitch);
        float yaw = (float)(_rand.NextDouble() * 2.0 * Math.PI);
        return new Vector3(MathF.Cos(yaw) * cosPitch, MathF.Sin(yaw) * cosPitch, sinPitch);
    }

    /// <summary>Relay a notable-moment line from the host's hook to the public
    /// <see cref="Notable"/> event, unchanged. Runs on the sim thread (the host
    /// hook is raised inside the tick), so forwarding to a hub broadcast (e.g.
    /// the vizfight spectator feed) is safe without a lock, exactly like the
    /// kill broadcast's own sim-thread emission.</summary>
    private void OnNotable(string text) => Notable?.Invoke(text);

    /// <summary>
    /// Handle a simulated payday (<see cref="ServerHost.PaydayEvent"/>, raised
    /// from <see cref="Mission.Update"/>'s payday pass on the sim thread) by
    /// performing the reference <c>CFSMission::DoPayday(IsideIGC*)</c> payout
    /// (<c>fsmission.cpp:2294-2326</c>) on the wire. <see cref="Mission"/>
    /// has already accrued the side's income into <see cref="Side.Money"/> and
    /// passed it through the site seam; this handler does the distribution
    /// half — the <c>delta</c> split by the side's player count, the
    /// <c>FM_S_PAYDAY(114)</c> broadcast to that side's own connected peers
    /// (the same per-side peer grouping <c>SelectChatTargets</c> uses for
    /// <c>Team</c>, Server.cs:1124-1128), and the <c>delta·np</c> deduction
    /// that leaves only the remainder in the side treasury. Runs on the sim
    /// thread (the same one that owns <c>_peerShips</c> and broadcasts ship
    /// states), so no extra locking is needed — mirroring
    /// <see cref="BroadcastKillShip"/>.
    /// </summary>
    private void OnPayday(ISideIgc sideIfc, float incomeAccrued)
    {
        if (sideIfc is not Side side)
        {
            Console.WriteLine("[payday] PaydayEvent with a non-Side side — ignored");
            return;
        }

        // Per-side player count, matching GetCountOfPlayers(pside, true)
        // (fsmission.cpp:2298): peers seated on this side via their ship's
        // SideId. The grouping is the same peer-resolution the chat Team relay
        // uses (Server.cs:1124-1128).
        var sidePeers = _peerShips
            .Where(kv => kv.Value.Ship.SideId == side.Id)
            .Select(kv => kv.Key)
            .ToArray();
        int playerCount = sidePeers.Length;

        // A side with zero players accrues but never pays out — the money stays
        // in the side treasury (matching the reference `if (np != 0)` guard at
        // fsmission.cpp:2298-2299, which is what avoids a divide-by-zero). Not a
        // crash, not a fabricated fallback.
        if (playerCount == 0)
        {
            Console.WriteLine($"[payday] side {side.Id} '{side.Name}': 0 players — accrued {incomeAccrued:F0} kept in treasury (balance {side.Money})");
            return;
        }

        long deltaLong = side.Money / playerCount;   // integer division, fsmission.cpp:2301
        if (deltaLong <= 0)
        {
            Console.WriteLine($"[payday] side {side.Id} '{side.Name}': delta {deltaLong} — nothing to pay (balance {side.Money})");
            return;
        }

        int delta = (int)deltaLong;                  // native Money is 32-bit (AGCIDL.idl:246)
        var msg = new Payday { DMoney = delta };
        byte[] packet = FedPacket.Pack(new[] { msg.Encode() });

        foreach (var t in sidePeers)
            SendPeer(t, packet);                     // side-scoped broadcast, fsmission.cpp:2307

        side.Money -= deltaLong * playerCount;       // keep the remainder < np, fsmission.cpp:2309
        var payNote = $"[payday] side {side.Id} '{side.Name}' -> PAYDAY(114) delta={delta} balance={side.Money}";
        Console.WriteLine(payNote);
        Notable?.Invoke(payNote);                      // spectator event feed (same text)
    }

    /// <summary>
    /// Handle a simulated mining accrual (<see cref="ServerHost.MineAsteroidEvent"/>,
    /// raised from <see cref="Sector.ResolveMining"/> on the sim thread when an
    /// asteroid's ore moved past the native reporting threshold). The ore itself
    /// is already wire-visible to peers as <c>ServerShipUpdate</c> byte 45
    /// (<c>OreFraction</c>), so the host does not broadcast a new message — this
    /// handler only logs the event (the reference host would relay/score it
    /// here). The <c>newOre</c> arg is the rock's ore fraction
    /// (<c>asteroidigc.h:249</c>), i.e. <c>ore/oreMax</c>, not an absolute mass.
    /// </summary>
    private void OnMine(IAsteroidIgc pasteroid, float newOre)
    {
        if (pasteroid is not Asteroid ast)
        {
            Console.WriteLine("[mine-event] MineAsteroidEvent with a non-Asteroid asteroid — ignored");
            return;
        }
        Console.WriteLine($"[mine-event] asteroid {ast.Data.AsteroidId} '{ast.Data.Name}' ore fraction {newOre:0.###} (remaining {ast.OreRemaining:0.##}/{ast.OreMax:0.##})");
    }

    /// <summary>
    /// Handle a simulated warp transit (<see cref="ServerHost.HitWarpEvent"/>,
    /// raised from the sector tick's <c>Sector.ResolveWarpCollisions</c> on the
    /// sim thread) by refreshing the per-peer <see cref="LiveShip"/> record and
    /// sending a fresh <c>FM_S_SET_CLUSTER(7)</c>. The client's SET_CLUSTER
    /// handler (<c>appmsg.cpp:1028-1087</c>) is what calls
    /// <c>pship->SetCluster(pcluster)</c> — the client-side sector reference the
    /// whole exploration/movement loop is gated on — so a transit must re-send
    /// it, not just rebroadcast a moved position. The <see cref="ShipWarped"/>
    /// hook fires after the ship is already relocated/reassigned
    /// (<c>Ship.Sector</c> and the sector model lists), so this handler only
    /// does the wire-facing half. Scoped to a single ship by
    /// <see cref="Ship.Id"/>/reference — no multi-peer visibility concerns
    /// (P40's own-ship scoping note), matching this chain's established scope.
    /// </summary>
    /// <remarks>
    /// Two consequences ride on refreshing the record here. (1) The next
    /// <see cref="BroadcastShipStates"/> sees <c>live.Sector.Id !=
    /// _lastStatusSector</c> and re-sends <c>FM_S_SHIP_STATUS(90)</c> with the
    /// new sector — the <c>sectorChanged</c> hook P40 built that never fired for
    /// lack of a real sector change now fires. (2) All the other sector readers
    /// (chat <c>AllSector</c>/<c>FriendlySector</c> targeting,
    /// <c>BroadcastKillShip</c>) agree on the new sector. The debounce window is
    /// set here (<see cref="Ship.LastWarpTime"/>, native fsship.cpp:1188) so the
    /// fresh position near the destination warp cannot be immediately swallowed
    /// back — owned by this method because only <see cref="Server"/> knows the
    /// sim clock (<c>_simNow</c>).
    /// </remarks>
    private void OnShipWarped(Ship ship, Sector oldSector, Sector newSector)
    {
        ship.LastWarpTime = _simNow;   // debounce (native 2s fudge, fsship.cpp:1188)

        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, ship)) continue;

            // Refresh the per-peer LiveShip sector so BroadcastShipStates' own-
            // ship status / sector readers all see the new sector. Established
            // `live with { ... }` reassignment pattern (same as LastBroadcastPos,
            // Server.cs:342,500).
            _peerShips[kv.Key] = kv.Value with { Sector = newSector };

            var set = new SetCluster
            {
                SectorId = newSector.Id,
                Ship = ServerShipUpdate.FromModel(ship.Id, ship, (uint)TickIndex),
                Cookie = 0,   // no per-peer cookie is stored; join re-used the ack's local value
            };
            SendPeer(kv.Key, FedPacket.Pack(new[] { set.Encode() }));
            Console.WriteLine($"[warp] peer {kv.Key} ship {ship.Id} -> SET_CLUSTER sector {newSector.Id} (LiveShip.Sector refreshed; SHIP_STATUS(90) resend via BroadcastShipStates)");
            return;
        }

        // A transit on a ship that is not a peer's own (possible only in a pure
        // sim / future-drone case) still moved the model correctly; only the
        // wire-facing routing is skipped.
        Console.WriteLine($"[warp] ship {ship.Id} transited '{oldSector.Name}' -> '{newSector.Name}' but is not a peer ship — SET_CLUSTER/LiveShip routing skipped (pure-sim transit)");
    }

    /// <summary>
    /// Handle a simulated dock (<see cref="ServerHost.DockWithStationEvent"/>,
    /// raised from the sector tick's <c>Sector.ResolveDockCollisions</c> on the
    /// sim thread, after <see cref="Ship.StationId"/> has been latched) by
    /// sending the <c>FM_S_DOCKED(61)</c> notify to the docking peer. This is
    /// the wire trigger that makes the native client's <c>ship.GetStation()</c>
    /// resolve: the client's <c>FM_S_DOCKED</c> handler
    /// (<c>appmsg.cpp:1092-1115</c>) is the ONLY call site that calls
    /// <c>m_ship-&gt;SetStation(reallyNonNull)</c> (<c>:1113</c>) — the member
    /// <c>IshipIGC::GetStation()</c> reads (<c>shipIGC.h:417-420</c>). Mirrors
    /// <see cref="OnShipWarped"/>'s per-peer routing: scoped to a single ship,
    /// sent to the docked peer only (no multi-peer visibility concerns, P40's
    /// own-ship scoping). Raised once per dock transition because the dock pass
    /// skips already-docked ships — exactly once per dock event.
    /// </summary>
    private void OnShipDocked(Ship ship)
    {
        foreach (var kv in _peerShips)
        {
            if (!ReferenceEquals(kv.Value.Ship, ship)) continue;

            var notify = new DockedNotify { StationId = ship.StationId!.Value };
            SendPeer(kv.Key, FedPacket.Pack(new[] { notify.Encode() }));
            Console.WriteLine($"[docked] peer {kv.Key} ship {ship.Id} <- FM_S_DOCKED(61) station {notify.StationId} (native ship.GetStation() resolve trigger)");
            return;
        }

        // A dock on a ship that is not a peer's own (possible only in a pure
        // sim / future-drone case) still latched the state correctly; only the
        // wire-facing notify is skipped.
        Console.WriteLine($"[docked] ship {ship.Id} docked but is not a peer ship — FM_S_DOCKED(61) routing skipped (pure-sim dock)");
    }

    // ── teardown ────────────────────────────────────────────────────────────

    /// <summary>Tear down in reverse of boot: stop the transport (joining its
    /// background threads), then drop the host. Idempotent.</summary>
    public void Shutdown()
    {
        _host?.Transport?.Shutdown();
        lock (_captureLock)
        {
            _captureStream?.Flush();
            _captureStream?.Dispose();
            _captureStream = null;
        }
        Console.WriteLine($"[shutdown] clean: {LoopIterations} ticks, {Overruns} overruns");
    }

    /// <summary>Dispose = <see cref="Shutdown"/>, so <c>using</c> guarantees the
    /// transport is torn down (no orphan threads) even when an assertion fails.</summary>
    public void Dispose() => Shutdown();

    // ── the runnable entry (used by Program.Main) ──────────────────────────

    /// <summary>Boot, then tick until SIGINT/SIGTERM, then shut down cleanly.
    /// Returns 0.</summary>
    public int RunAsync(string configPath, IReadOnlyList<(string Key, string Value)> cliOverrides)
    {
        Boot(configPath, cliOverrides);
        using var cts = new CancellationTokenSource();

        // On Linux use PosixSignalRegistration to intercept SIGINT (Ctrl-C) and
        // SIGTERM and cancel the loop, letting the loop break and teardown run.
        // Skip Console.CancelKeyPress there — it hooks the same signal and
        // doubling up invites ambiguity; it is the fallback for other platforms.
        if (OperatingSystem.IsLinux())
        {
            // PosixSignalContext.Cancel defaults to FALSE: .NET runs the callback
            // then lets the signal's default disposition terminate the process
            // unless we suppress it. Without ctx.Cancel = true a SIGTERM/SIGINT
            // would kill the server mid-loop and teardown would never run.
            using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { cts.Cancel(); ctx.Cancel = true; });
            using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { cts.Cancel(); ctx.Cancel = true; });
            Loop(cts.Token);
        }
        else
        {
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            Loop(cts.Token);
        }

        Shutdown();
        return 0;
    }
}
