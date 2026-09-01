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

    // Ships assigned to peers on validated logon (step 3). This map is owned
    // exclusively by the simulation thread (written in ProcessDatagram, which
    // Drain runs on the sim thread, and read in the broadcast step of SimPass),
    // so it needs no locking of its own.
    private readonly Dictionary<ConnectionId, LiveShip> _peerShips = new();
    private int _nextShipId = 8096;

    // Optional live-wire capture (--capture path). Null when off, so the only
    // overhead when disabled is a null check — capture must never change
    // behaviour or timing. Serialized across the transport (receive) and sim
    // (send) threads with a dedicated lock so a frame is never interleaved.
    private FileStream? _captureStream;
    private readonly object _captureLock = new();

    /// <summary>The position-change threshold (world units) that marks a ship
    /// dirty and worth a SINGLE_SHIP_UPDATE broadcast after a tick.</summary>
    private const float DirtyEpsilon = 1e-3f;

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
        // Step 3: after the mission tick, broadcast every peer ship that moved.
        // Runs on the sim thread, so "only Mission.Update moves ships" holds —
        // this is purely an observation send of the authoritative state.
        BroadcastShipStates((uint)TickIndex);
    }

    /// <summary>Current completed-tick count, read under the stats lock.</summary>
    private long TickIndex { get { lock (_statsLock) return _loopIterations; } }

    /// <summary>
    /// For every peer ship that changed position since its last broadcast,
    /// send that peer a <c>FM_S_SINGLE_SHIP_UPDATE(78)</c> carrying the raw
    /// authoritative position/velocity/orientation, timestamped with the tick
    /// index (the uint <c>Time</c> slot). No broadcast when a ship has not
    /// moved (zero thrust ⇒ silence), matching the C++ dirty-state update.
    /// </summary>
    private void BroadcastShipStates(uint tickIndex)
    {
        foreach (var kv in _peerShips.ToArray())
        {
            var live = kv.Value;
            var ship = live.Ship;
            if ((ship.Position - live.LastBroadcastPos).Length() < DirtyEpsilon)
                continue;
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
                    // the client's clusters had empty lists. Asteroids, treasure,
                    // mines and probes are intentionally skipped (none exist in
                    // this map / this codebase's scope), matching the task.
                    //
                    // Unconditional — no SeenBySide()/fog-of-war gate, exactly
                    // as P30 reasoned for clusters (Server.cs:742-747): this
                    // codebase has no scouting/visibility system, so export
                    // every warp and station of every sector to every peer.
                    // Ships' resolved destination sectors come for free: the
                    // warp's own ClusterId addresses its sector, and all sectors
                    // are already exported above.
                    foreach (var sector in _missions[0].Sectors)
                    {
                        foreach (var resolved in sector.Warps)
                            SendPeer(from, FedPacket.Pack(new[] { ExportWarp.FromWarp(resolved).Encode() }));
                        foreach (var station in sector.Stations)
                            SendPeer(from, FedPacket.Pack(new[] { ExportStation.FromStation(station).Encode() }));
                    }
                    Console.WriteLine($"[export] peer {from} '{req.CharacterName}' -> {_missions[0].Warps.Count} warp(s), {_missions[0].Sectors.Sum(s => s.Stations.Count)} station(s) (OT_warp 7 / OT_station 1)");

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
                        live.Ship.Controls = u.Payload.Controls;
                        live.Ship.StateM = u.Payload.StateM;
                        var c = u.Payload.Controls;
                        Console.WriteLine($"[ctrl] {from} ship {live.Ship.Id} controls=(yaw {c.Yaw:F3} pitch {c.Pitch:F3} " +
                                          $"roll {c.Roll:F3} thr {c.Throttle:F3}) state=0x{u.Payload.StateM:X}");
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
            else
            {
                Console.WriteLine($"[wire] from {from}: unsupported fmid {fmid} — ignored at headless milestone");
            }

            static string sideStr(short? s) => s is { } v ? v.ToString() : "(none)";
        }
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
    private ConnectionId[] SelectChatTargets(ConnectionId from, ChatMessage chat, short? senderSideId)
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
