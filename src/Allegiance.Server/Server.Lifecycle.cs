using System.Diagnostics;
using System.Runtime.InteropServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
// Disambiguate the LoadoutChange KIND enum (Sim) from the Allegiance.Core.Wire.LoadoutChange
// codec — both are in scope here; the enum is the natural meaning in the
// gameplay broadcast handler, the wire codec is reached as Allegiance.Core.Wire.LoadoutChange.
using LoadoutChange = Allegiance.Core.Sim.LoadoutChange;

namespace Allegiance.Server;

/// <summary>
/// Boot, tick loop, shutdown and connection-lifecycle members of <see cref="Server"/>
/// (P327 stage-1 split from <c>Server.cs</c>). Partial class — shares the <c>Server</c>
/// type, so private field/method access across the split files is unaffected.
/// </summary>
public sealed partial class Server
{
    // P324 (M4) — a side whose seated-peer set changed (a peer joined) must
    // receive an immediate per-side object sync rather than waiting for the
    // every-10 cadence tick. Populated by the seating path (TryJoinSide).
    private void MarkObjectSyncDirty(short sideId) => _objectSyncDirtySides.Add(sideId);

    /// <summary>A peer-owned ship: the <see cref="Ship"/>, the
    /// <see cref="Sector"/> it lives in, its last-broadcast position
    /// (the dirtiness watermark for the SINGLE_SHIP_UPDATE broadcast), and
    /// (P193) the peer's in-memory, connection-lifetime-only
    /// <see cref="PlayerScore"/>. A fresh <see cref="PlayerScore"/> is
    /// allocated when the <c>LiveShip</c> is created at logon and discarded
    /// with it on disconnect — the record's lifecycle <em>is</em> the
    /// PlayerScore's lifetime, so non-persistence is structural rather than an
    /// extra bookkeeping rule.</summary>
    /// <summary><c>LastBroadcastForward</c>/<c>LastBroadcastUp</c>: the
    /// orientation last echoed to this ship's own owning peer (and to same-side
    /// recipients via the teammate broadcast below), so a pure rotation — no
    /// position change at all, e.g. roll/pitch/yaw with no thrust — still
    /// crosses the dirty check and gets broadcast. Forward alone misses roll
    /// (rotation about the ship's own forward axis leaves forward unchanged),
    /// so both are tracked; together they fully determine the 3x3 orientation.</summary>
    internal sealed record LiveShip(Ship Ship, Sector Sector, Vector3 LastBroadcastPos, Vector3 LastBroadcastForward, Vector3 LastBroadcastUp, PlayerScore PlayerScore, PlayerWallet Wallet, bool LastBroadcastMining, bool PositionEverBroadcast = false);

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
        ConsoleLog.Verbose = _config.ConsoleVerbose;   // P318: gate the high-frequency per-tick lines

        ValidatePaths();
        var core = LoadCore();                       // checklist item 4
        StubFileLists();                             // checklist item 5 (stub — cores/maps; ranks now real, see below)
        _rankTable = LoadRankTable();                // P130: real rank list from <artPath>/ranks.txt
        BindHostAndTransport();                      // checklist items 3 & 6
        OpenCaptureIfEnabled();                      // optional --capture (off by default)
        var missions = InstantiateGames(core);       // checklist item 8
        _missions = missions;
        StartLobbyHeartbeat(core);                   // opt-in; no-op when config.Lobby is null
        PrintReady();
    }

    /// <summary>
    /// Construct the lobby heartbeat client when config.Lobby is set (fully opt-in),
    /// storing it for teardown in <see cref="Shutdown"/>. The payload's
    /// MissionName/CoreFile/Version are resolved once here:
    /// <list type="bullet">
    ///   <item><b>MissionName</b> — the FIRST configured game's <c>Name</c> (empty string
    ///        when <c>Games</c> is empty). The Lobby's RegisterRequest carries one singular
    ///        MissionName/CoreFile pair, but this port supports multiple concurrent missions
    ///        per process (<c>ServerConfig.Games</c> is a list) — so only the first is
    ///        reported, and the live PlayerCount aggregates across all missions via
    ///        <see cref="LivePlayerCount"/>. Documented simplification.</item>
    ///   <item><b>CoreFile</b> — the server-wide <c>StaticCore</c> (same for every mission).</item>
    ///   <item><b>Version</b> — the static core's version word.</item>
    /// </list>
    /// There is no MaxPlayers/player-cap concept in ServerConfig today, so the client
    /// sends 0 (the directory treats it as unknown/unbounded); no config field is
    /// invented for it here.
    /// </summary>
    private void StartLobbyHeartbeat(StaticCore core)
    {
        if (_config.Lobby is null)
            return;

        var firstGame = _config.Games.FirstOrDefault();
        _heartbeat = new LobbyHeartbeat(
            _config.Lobby,
            () => LivePlayerCount,
            firstGame?.Name ?? "",
            _config.StaticCore,
            core.Version.ToString(),
            _config.Port);
        Console.WriteLine($"[lobby] heartbeat enabled -> {_config.Lobby.Url}, every {_config.Lobby.HeartbeatIntervalSeconds}s");
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

    /// <summary>BOOT_PATH.md step 5 — cores.txt/maps.txt are stubbed (dropped)
    /// and we say so rather than silently ignoring. ranks.txt is NOT stubbed
    /// anymore (P130 actually loads it — see <see cref="LoadRankTable"/>), so
    /// it is named out of this note; cores.txt/maps.txt remain deferred.</summary>
    private static void StubFileLists() =>
        Console.WriteLine("[boot] (stub) cores.txt / maps.txt NOT loaded — download list out of scope (rank list IS loaded from ranks.txt, see above)");

    /// <summary>
    /// P130 — load the real standalone rank table from
    /// <c>&lt;artPath&gt;/ranks.txt</c>, using the same configured-artwork-root
    /// path-resolution convention the static core, mission maps, and P116's
    /// <c>.cvh</c> corpus use (single configured <c>ArtPath</c>, files flat at
    /// that root — cf. <c>Server.cs:251,344,356</c>). Faithfully ports native's
    /// standalone <c>LoadRankInfo</c> <c>#else</c> branch
    /// (<c>FedSrv.CPP:125-148</c>): count header then <c>&lt;rank&gt;,&lt;name&gt;</c>
    /// lines, every entry <c>civ=-1</c>/<c>requiredRanking=0</c>. Missing file →
    /// empty table (native's <c>g.cRankInfo = 0</c>, FedSrv.CPP:145-146).
    /// </summary>
    private RankInfo LoadRankTable()
    {
        var path = Path.Combine(_config.ArtPath, "ranks.txt");
        var table = RankInfo.FromRanksFile(path);
        Console.WriteLine($"[boot] rank list loaded: {table.Ranks.Length} ranks from '{path}' (standalone: civ=-1, requiredRanking=0)");
        return table;
    }

    private void BindHostAndTransport()
    {
        _host = new ServerHost();
        _host.ShipKilled = BroadcastKillShip;   // wire the site's KillShipEvent to a broadcast
        _host.ShipWarped = OnShipWarped;        // wire the site's HitWarpEvent transit to per-peer routing
        _host.Payday = OnPayday;                // wire the site's PaydayEvent to the per-side wire payout
        _host.BucketCompletedHook = OnBucketCompleted;  // P119: wire the site's BucketCompleted to the completion recorder (detection only)
        _host.Mine = OnMine;                    // wire the site's MineAsteroidEvent to the host logger
        _host.ShipDocked = OnShipDocked;        // wire the site's DockWithStationEvent to the per-peer docked notify
        _host.ProjectileCreated = BroadcastProjectileInstance;   // P88: wire the site's ProjectileCreatedEvent to the wire broadcast
        _host.MissileCreated = BroadcastFireMissile;            // P90: wire the site's MissileCreatedEvent to the wire broadcast
        _host.MissileDestroyed = BroadcastMissileDestroyed;     // P90: wire the site's MissileDestroyedEvent to the wire broadcast
        _host.LoadoutChange = BroadcastLoadoutChange;          // P173: wire the site's LoadoutChangeEvent to the wire broadcast
        _host.MineCreated = BroadcastExportMine;                // P94: wire the site's MineCreatedEvent to the wire broadcast
        _host.MineDestroyed = BroadcastMineDestroyed;           // P94: wire the site's MineDestroyedEvent to the wire broadcast
        _host.ProbeCreated = BroadcastExportProbe;             // P99: wire the site's ProbeCreatedEvent to the wire broadcast
        _host.ProbeDestroyed = BroadcastProbeDestroyed;        // P99: wire the site's ProbeDestroyedEvent to the wire broadcast
        _host.WarpBomb = BroadcastWarpBomb;                   // P101: wire the site's WarpBombEvent to the WHOLE-mission wire broadcast
        _host.AsteroidKilled = BroadcastAsteroidDestroyed;     // P102: wire the site's KillAsteroidEvent to the WHOLE-mission wire broadcast
        _host.StationKilled = BroadcastStationDestroyed;       // P125: wire the site's KillStationEvent to VacateStation + WHOLE-mission STATION_DESTROYED(59)
        _host.StationCaptured = BroadcastStationCapture;       // P126: wire the site's CaptureStationEvent to the WHOLE-mission STATION_CAPTURE(58) broadcast
        _host.VictoryByStationsComputedHook = OnVictoryByStationsComputed;  // P191: wire the site's post-capture/kill recompute to GAME_STATE(155) + P178 GameOver
        _host.ProsperityArtifactComputedHook = OnProsperityArtifactComputed;  // P302: wire the prosperity/artifact recompute to GAME_STATE(155) + P178 GameOver
        _host.FlagsKillsComputedHook = OnFlagsKillsComputed;  // P303: wire the flags/kills recompute to GAME_STATE(155) + P178 GameOver
        _host.Notable = OnNotable;              // wire the site's ReportNotable to the public spectator event
        _host.RequestRipcordReceived = OnNpcRequestRipcord;  // P172: wire the site's IIgcSite.RequestRipcord (NPC) to the ripcord state machine
        var transport = new TcpTransport(_host);      // site set; assign the back-ref next
        _host.Transport = transport;
        transport.Host((ushort)_config.Port);         // reproduce: bind the logon transport
        Console.WriteLine($"[boot] transport listening on 0.0.0.0:{_host.BoundPort} (all interfaces)");
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
            // P180 — a real team-lobby/pre-game phase: the mission is born
            // NotStarted (the stage's genuine default) and only transitions to
            // Started when every side with at least one connected player has
            // readied (see the ready-up recompute + auto-start in the
            // PLAYER_READY handler). Previously this line force-set Started at
            // boot, so the pre-game phase was skipped entirely and the mission
            // was always already running by the time anyone joined. Before it
            // was possible to gate on NotStarted, several seams assumed
            // Started-from-boot: the join-sync ENTER_GAME send is now guarded
            // (Server.cs, live.Ship.Mission.Stage == Started) and the control
            // SHIP_UPDATE path ignores input while NotStarted. Payday is
            // scheduled lazily off the sim clock (not boot), so the payday
            // continuity the task asked to verify is unaffected — confirmed:
            // nothing else in the boot/instantiate sequence reads Stage.
            mission.Stage = MissionStage.NotStarted;                // pre-game hold until every side is ready
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

    /// <summary>
    /// P314 — run one <see cref="SimPass"/> stage inside an exception barrier.
    /// The per-tick broadcast/drain/post-update section historically had no
    /// exception containment: any escape — a misbehaving <see cref="Notable"/>
    /// subscriber, an over-limit <c>ExportShip.Encode</c>, a corrupt part —
    /// propagated all the way to the server's top-level catch and terminated the
    /// process (a single bad tick took the whole server down). A bad tick must
    /// instead degrade gracefully: log the failing stage with enough context to
    /// diagnose it, skip that stage for this tick, and keep simulating.
    /// <para>Returns <c>false</c> when the stage threw, so a caller that needs
    /// to sequence dependent work can observe the failure; every stage is
    /// independently barriered so one failure can't cascade into siblings.</para>
    /// </summary>
    private bool Step(string stage, Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[simpass] stage '{stage}' threw {e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.ToString());
            lock (_statsLock) _simPassStageFailures++;
            return false;
        }
    }

    /// <summary>
    /// P314 — the sole raiser of the public <see cref="Notable"/> spectator
    /// event, run through an exception barrier. A subscriber (e.g. the vizfight
    /// event feed) must never be able to stall or kill the server: one bad
    /// subscriber is logged and skipped, the rest of the tick continues.
    /// All <c>Notify(...)</c> call sites route through here.
    /// </summary>
    private void Notify(string text)
    {
        try
        {
            Notable?.Invoke(text);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[notable] spectator subscriber threw on '{text}': {e.GetType().Name}: {e.Message}");
            Console.WriteLine(e.ToString());
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
                // released from the broadcast set. P128: its ship is then
                // retired from the sector (SDR_LOGGEDOFF + removed) so it no
                // longer keeps flying under Mission.Update — the same cleanup
                // the graceful LOGOFF path runs through this same ReleasePeer.
                ReleasePeer(l.Id);
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

    /// <summary>The connection-cleanup path for a departed peer — reused by
    /// both an abrupt transport drop (<see cref="PeerLeftEvt"/>) and a
    /// graceful <c>FM_CS_LOGOFF</c> (P127). Releasing the peer means dropping
    /// it from the broadcast/command sets (<c>_peerShips</c>,
    /// <c>_peerNames</c>) <b>and</b> (P128) retiring the ship it left behind:
    /// broadcasting <c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c> to its sector peers
    /// and removing it from the sector so it no longer flies under
    /// <c>Mission.Update</c> — the port's analogue of native's
    /// <c>CFSShip::~CFSShip</c> → <c>AnnounceExit(NULL, SDR_LOGGEDOFF)</c> +
    /// <c>m_pShip->Terminate()</c> (fsship.cpp:58-68). Callers that fired
    /// because of LOGOFF also hold the original frame for its guaranteed echo
    /// before releasing. Idempotent: a second call (LOGOFF, then the
    /// transport's own OnPeerLeft) is a no-op.</summary>
    private void ReleasePeer(ConnectionId id)
    {
        // P281 — a pending side-select peer (logged on, not yet spawned) is
        // released the same way: drop it from the pending hold.
        _pendingSideSelect.Remove(id);
        // P315 — drop this peer's unsupported-fmid log-gate state so the
        // rate-limit table stays bounded by the live peer set.
        _unsupportedLog.Remove(id.Value);
        // M1 — release every remaining connection-scoped cache keyed by this
        // peer. The original ReleasePeer only freed _pendingSideSelect /
        // _peerShips / _peerNames / _playerReady / _pendingJoinRequests; the
        // status/cluster/ping trackers below were never freed, so a
        // long-uptime server with connection churn accumulated dead entries
        // indefinitely. Placed before the idempotent early-return so a second
        // release is also a no-op here.
        _lastStatusSector.Remove(id);
        _lastStatusTick.Remove(id);
        _lastClusterInfoTick.Remove(id);
        _lastPingQuery.Remove(id);
        if (!_peerShips.Remove(id, out var live))
        {
            // Idempotent second release (LOGOFF followed by the transport's own
            // OnPeerLeft) — the peer is already gone and its ship already retired.
            _peerNames.Remove(id);
            _playerReady.Remove(id);
            return;
        }

        // M1 — the departing connection also keys the teammate-scoped caches
        // (the subject-ship dimension of the composite (recipient, ship)
        // keys), and the departing ship id keys the per-subject loadout
        // snapshot cache. Free every entry where the departing peer is the
        // recipient OR the departing ship is the subject; otherwise these
        // grow monotonically with connection churn.
        short departedShipId = live.Ship.Id;
        _teammateExportSent.RemoveWhere(e => e.Recipient == id || e.ShipId == departedShipId);
        _teammatePlayerInfoSent.RemoveWhere(e => e.Recipient == id || e.ShipId == departedShipId);
        // P349 — the cross-side export latch gets the exact same treatment: free
        // every entry where the departing peer was the recipient OR the departing
        // ship was the subject, else it grows monotonically with connection churn.
        _crossSideExportSent.RemoveWhere(e => e.Recipient == id || e.ShipId == departedShipId);
        foreach (var k in _teammateLoadoutVersionSent
                     .Where(kv => kv.Key.Recipient == id || kv.Key.ShipId == departedShipId)
                     .Select(kv => kv.Key).ToList())
            _teammateLoadoutVersionSent.Remove(k);
        _teammateLoadoutCache.Remove(departedShipId);
        foreach (var k in _lastTeammateStatusTick
                     .Where(kv => kv.Key.Item1 == id || kv.Key.Item2 == departedShipId)
                     .Select(kv => kv.Key).ToList())
            _lastTeammateStatusTick.Remove(k);

        RetireShipOnDisconnect(live);

        // P178 — win-by-elimination (native fsmission.cpp:5371-5427 →
        // CheckForVictoryByInactiveSides). A peer just left (its ship retired),
        // which is the one moment a side can go empty of players; re-evaluate
        // whether this mission has reached its endgame state. Only on this real
        // departure path — never on the idempotent early-return above.
        if (live.Ship.Mission is { } mission && live.Ship.SideId is { })
        {
            CheckForVictoryByInactiveSides(mission);

            // P183 — the RemovePlayerFromSide port's leader-succession
            // (fsmission.cpp:848-1099): if the departing ship was its side's
            // leader, promote a successor (or clear the role), broadcasting
            // SET_TEAM_LEADER / SET_MISSION_OWNER as needed. Runs after the
            // victory check and before the name/ready cleanup, mirroring native
            // RemovePlayerFromMission calling RemovePlayerFromSide first.
            HandleLeaderSuccession(live.Ship);

            // P184 — a departing player's pending side-join request(s), if any,
            // must be cleared and each broadcast as DELPOSITIONREQ(DPR_Rejected)
            // (native RemovePlayerFromMission → RemoveJoinRequest(pfsPlayer, NULL),
            // fsmission.cpp:460-525 / 4938-4960). Usually at most one per ship in
            // this port.
            foreach (var pend in _pendingJoinRequests
                         .Where(kv => kv.Value.ShipId == live.Ship.Id).ToList())
            {
                _pendingJoinRequests.Remove(pend.Key);
                BroadcastDelPositionReq(mission, pend.Value.ShipId, pend.Value.SideId, DelPositionReqReason.Rejected);
            }

            // P183 — broadcast QUIT_MISSION(124) to the whole mission. This
            // disconnect path previously sent nothing telling remaining peers
            // "ship X quit the mission" — a real, live gap now closed.
            BroadcastQuitMission(mission, live.Ship.Id, QuitSideReason.LinkDead);
        }

        _peerNames.Remove(id);                     // P62: release the remembered name with the peer
        _playerReady.Remove(id);                   // P180: release the peer's lobby-ready flag with it
    }

    /// <summary>
    /// P128 — retire the zombie ship a departing peer leaves in the sector.
    /// The port's analogue of native's <c>CFSShip::~CFSShip</c>
    /// (fsship.cpp:58-68), which on destruction announces
    /// <c>SHIP_DELETE(NULL, SDR_LOGGEDOFF)</c> (<i>"client is gone, so waste
    /// their ship and everything about it"</i>, Messages.h:191) and then
    /// <c>m_pShip->Terminate()</c>. Three steps:
    /// <list type="number">
    /// <item><b>Detach any boarded children</b> — native Terminate "blows away
    /// the children" by setting each <c>SetParentShip(NULL)</c>
    /// (shipIGC.cpp:243-250), which ejects the child as an independent ship,
    /// not destroys it (shipIGC.cpp:3671-3700). This port's existing detach
    /// helper is <see cref="PerformLeave"/> — valid here because a boarded
    /// carrier is always docked (board gates on carrier <c>StationId != null</c>,
    /// Server.cs:2660-2662), so its docked gate passes; the same reuse the
    /// teleport bNewHull boot makes (Server.cs:3981-3982).</item>
    /// <item><b>Broadcast <c>SHIP_DELETE(20, SDR_LOGGEDOFF)</c></b> to the peers
    /// who can see the ship — the same sector-scoped audience the kill path
    /// uses for SDR_KILLED (P103; native instead addresses the whole mission
    /// group, fsship.cpp:213-219 — noted, matching this port's SHIP_DELETE
    /// convention).</item>
    /// <item><b>Remove the ship from its sector</b> via the same
    /// <c>_models.Remove</c> the death contract uses (Sector.cs:1082/1499), so
    /// it stops flying under Mission.Update — reusing the mechanism, not a
    /// second one.</item>
    /// </list>
    /// Runs on the server thread (called from <see cref="ReleasePeer"/>), same
    /// as the kill broadcasts. A ship with no sector (e.g. a peer that never
    /// got a validated logon) has nothing to retire.</summary>
    private void RetireShipOnDisconnect(LiveShip live)
    {
        var ship = live.Ship;
        if (ship.Sector is not { } sector)
        {
            Console.WriteLine($"[logoff] ship {ship.Id} has no sector — nothing to retire");
            return;
        }

        // 1. Native "blow away the children": detach every boarded child
        //    (shipIGC.cpp:243-250) via the port's existing detach helper.
        foreach (var kid in ship.ChildShips.ToArray())
            PerformLeave(kid, ship);

        // 2. Broadcast SHIP_DELETE(20, SDR_LOGGEDOFF) to the sector audience.
        //    The departing peer is already out of _peerShips, so it is not a
        //    target — consistent with the kill path's audience construction.
        var targets = _peerShips
            .Where(kv => kv.Value.Ship.Sector == ship.Sector)
            .Select(kv => kv.Key)
            .ToArray();
        if (targets.Length > 0)
        {
            var delete = ShipDelete.ForLoggedOffShip(ship.Id);
            byte[] packet = FedPacket.Pack(new[] { delete.Encode() });
            foreach (var t in targets)
                SendPeer(t, packet);
        }
        Console.WriteLine($"[logoff] ship {ship.Id} — SHIP_DELETE(20) SDR_LOGGEDOFF -> {targets.Length} peer(s)");

        // 3. Remove the ship from its sector (the same _models.Remove the
        //    death contract uses, Sector.cs:1082/1499) so Mission.Update stops
        //    flying it. Nulling Sector mirrors native Terminate's
        //    GetCluster() → NULL.
        sector.Remove(ship);
        ship.Sector = null;
    }

    /// <summary>Tear down in reverse of boot: stop the transport (joining its
    /// background threads), then drop the host. Idempotent.</summary>
    public void Shutdown()
    {
        // Heartbeat constructed last in Boot → torn down first, so its best-effort
        // deregister DELETE fires before the process tears down. Independent of the
        // transport shutdown (either order is fine) — kept first for reverse-of-boot clarity.
        _heartbeat?.Dispose();
        _heartbeat = null;
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
