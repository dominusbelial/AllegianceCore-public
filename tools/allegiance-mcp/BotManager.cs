using System.Diagnostics;
using Allegiance.Core;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using BotClass = global::Allegiance.Bot.Bot;

namespace Allegiance.Tools.McpSpawner;

/// <summary>
/// P426 — owns the real wire peers behind every spawned bot. One long-lived
/// <see cref="ConnectionSession"/> probe validates the live server at startup
/// (the same connect/logon/seat flow <c>vizfight attach</c> uses) and supplies
/// the real side roster for side validation / auto-pick. Each spawned bot is a
/// real <see cref="BotClass"/> connected as an ordinary client to that same
/// server; the caller is done the moment the tool call returns (spawn-and-forget)
/// and the behavior loop drives each bot for the life of this process.
/// </summary>
internal sealed partial class BotManager : IDisposable
{
    private readonly string _host;
    private readonly ushort _port;
    private readonly object _gate = new();
    private readonly List<ActiveBot> _bots = new();
    private readonly StallWatchdog _stallTelemetry;

    // ── P622 — per-pass target-engagement coordination ──────────────────
    // Written ONLY by the single Loop thread. `_tickEngagers` accumulates the
    // engager count per target as each combat bot picks its target in the
    // current pass (reset at the top of each pass), giving every later bot a
    // live "already engaged" view so the anti-swarm cap spreads the fleet.
    // `_engagersSnapshot` is a copy taken at the END of each pass (under _gate)
    // for read-only consumers like list_active_bots.
    private readonly Dictionary<short, int> _tickEngagers = new();
    private readonly Dictionary<short, int> _engagersSnapshot = new();
    // P622 — how long ago an enemy hitting this bot still counts as an active
    // threat (a "shooting at me" ship is engaged first).
    private const double ThreatWindowSeconds = 8.0;
    // P622 measurement switch (default OFF): when ALLEGIANCE_P622_LEGACY_NEAREST
    // is set, EngageCombat reverts to the pre-P622 nearest-enemy pick so the P622
    // live harness can measure the swarm before/after with the same binary.
    /// <summary>Showcase/battle bots never pick miners, builders, pods or other drones as
    /// targets (Domingo, 2026-10-13: "all went for the miner, we need to exclude miners as
    /// targets for the showcase"). Default ON; ALLEGIANCE_BOT_TARGET_MINERS=1 restores them.</summary>
    private static readonly bool IgnoreMinerTargets =
        Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_TARGET_MINERS") != "1";

    private static readonly bool LegacyNearestFlag =
        Environment.GetEnvironmentVariable("ALLEGIANCE_P622_LEGACY_NEAREST") == "1";

    // P636c — A/B switch (default OFF): when ALLEGIANCE_BOT_DECISION_SNAPSHOT=off,
    // the decision read path reverts to the PRE-P636 live-table reads under
    // cs.Gate (the P635 behaviour), so the P632 harness can measure snapshot-on
    // vs snapshot-off lethality in ONE binary. Everything that "off" touches is
    // a faithful re-enable of the P635 code that P636 replaced.
    // P636d — the single source of truth now lives on ClientWorld
    // (ClientWorld.DecisionSnapshotEnabled) so the CLIENT-CORE receive thread can
    // honour it directly and make `off` perform ZERO publishes (the fix this task
    // measures); this tool-side flag delegates to it so the read path and the
    // publish path can never disagree about the mode.
    // Do not capture this mutable flag at type initialization: a test's scoped
    // override must select the same mode for both publishing and decision reads.
    internal static bool DecisionSnapshotEnabled => ClientWorld.DecisionSnapshotEnabled;

    // P636c — sub-switch to isolate the staleness FIX (default ON): when
    // ALLEGIANCE_BOT_SNAPSHOT_DEADRECKON=off, the aim-critical pose reads return
    // the RAW frozen snapshot pose (the P636b behaviour, no dead-reckoning),
    // letting the harness measure the staleness fix vs the unfixed snapshot in
    // isolation. Only meaningful when DecisionSnapshotEnabled (off-mode ignores it).
    internal static readonly bool SnapshotDeadReckonEnabled =
        Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_SNAPSHOT_DEADRECKON") != "off";

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly CancellationTokenSource _loopCts = new();

    // ── P636c — snapshot-age instrumentation ─────────────────────────────
    // The age (ms) of the decision snapshot each bot's aim actually consumed,
    // sampled in EngageShipAt: how far the frozen pose lagged the decision's
    // "now". Rolling window, reported via get_perf_stats so the P632 PERF line
    // can prove whether snapshot staleness tracks the lethality drop. Static
    // because EngageShipAt (the sampler) is a static method; one BotManager per
    // process in the harness.
    private static readonly object SnapAgeGate = new();
    private static readonly double[] _snapAgesMs = new double[MaxSnapAgeSamples];
    private static int _snapAgeCount, _snapAgeNext;
    private const int MaxSnapAgeSamples = 16384;

    // ── P633 — decision-loop measurement (get_perf_stats / item 1, 6) ────
    // The single decision thread's pass cadence, process resources and the
    // _gate contention are measured live so the MCP tool can report them
    // without disturbing the behaviour loop. `TargetDecisionIntervalMs` is the
    // FIXED pass cadence: the loop sleeps `target − elapsed`, so a slow pass no
    // longer pushes the interval to "pass time + 50 ms" (item 2's fixed-cadence
    // fix). Guards: `_metricsGate` protects the rolling sample lists (the loop
    // thread writes, get_perf_stats reads).
    public const double TargetDecisionIntervalMs = 50.0;
    private readonly object _metricsGate = new();
    private readonly List<double> _passTimesMs = new();
    private const int MaxPassSamples = 512;
    private double _maxGateHoldMs;
    private DecisionMetrics.ResourceSnapshot _lastResources = new(0, 0, 0, 0, 0);
    private long _lastDecodeBytes;
    private double _lastDecodeWall;
    private double _decodeMbPerSec;
    private double _lastPassMs;
    // P636b — the real decision cadence: wall-clock ms between consecutive pass
    // STARTS (includes the fixed-cadence sleep). This is what DecisionRateHz must
    // reflect — the P636 metric fed it the pass WORK time (1000/workMs), which
    // reported ~670 Hz−9 kHz even though the loop was correctly sleeping to ~20 Hz.
    private readonly DecisionMetrics.LoopCadence _cadence = new(TargetDecisionIntervalMs);
    private double _lastWallPeriodMs = TargetDecisionIntervalMs;

    // ── P556 — time-to-kill tracking (pure observability) ─────────────────
    //
    // For each VICTIM ship, record the time of its first DAMAGING enemy hit
    // (any hit — shield or hull — by our side) and, on its death (a KILL_SHIP
    // event our side observes), the interval to that death. Reports the mean
    // time-to-kill per side even when kills are few. The server's own log
    // lines carry no wall-clock timestamps, so the MCP's clock (the real-time
    // observer attached to the server) is the timing source — the same
    // PROJECTILE_HULL_HIT / KILL_SHIP events the server's [projectile-hit] /
    // [combat] killed lines describe. Each victim contributes one sample;
    // reset between runs.
    private static readonly object TtkLock = new();
    private static readonly Dictionary<short, (double At, short Side)> TtkFirstHit = new();
    private static readonly List<(short Side, double Ttk)> TtkSamples = new();

    /// <summary>P556 — clear the TTK accumulator (call at the START of each
    /// arena run so samples never bleed across games).</summary>
    public static void ResetCombatTiming()
    {
        lock (TtkLock) { TtkFirstHit.Clear(); TtkSamples.Clear(); }
    }

    /// <summary>P556 — the mean of a set of time-to-kill samples, or <c>null</c>
    /// when empty (no kill had a recorded first hit this run). Pure, so the
    /// survivability aggregation is unit-testable without a server.</summary>
    public static double? MeanOf(IReadOnlyList<double> samples)
        => samples.Count == 0 ? null : samples.Sum() / samples.Count;

    /// <summary>P556 — the mean time-to-kill (seconds) for victims of
    /// <paramref name="side"/> in the current run, or <c>null</c> when no kill
    /// had a recorded first hit.</summary>
    public static double? MeanTimeToKill(short side)
    {
        lock (TtkLock)
        {
            var sel = TtkSamples.Where(s => s.Side == side).Select(s => s.Ttk).ToList();
            return MeanOf(sel);
        }
    }

    /// <summary>P556 — the count of TTK samples (kills with a recorded first
    /// hit) for <paramref name="side"/> in the current run.</summary>
    public static int TimeToKillCount(short side)
    {
        lock (TtkLock) return TtkSamples.Count(s => s.Side == side);
    }

    /// <summary>
    /// P539d — optional host-supplied arena relocation: given a bot's ship id,
    /// the target sector, a mirrored position and a facing, move that ship into
    /// the arena sector at that spot (the in-process live tests wire this to the
    /// server's native SetCluster relocation; a standalone MCP host provides its
    /// own supported relocation). null = arena spawns without relocation (the bot
    /// stays at home and the caller is told).
    /// </summary>
    public Func<short, short, Vector3, Vector3, bool>? Relocator;
    private readonly Thread _loopThread;
    private int _nameCounter;
    private bool _startedLoop;

    /// <summary>P430 — per-bot bounding cap on the rolling cadence sample list
    /// (real (interval, distance) observations), so an unbounded run of the
    /// behavior loop never leaks memory.</summary>
    public const int MaxSamplesPerBot = 200;

    /// <summary>P553 — per-bot bounding cap on the rolling dodge-fire trace, so a
    /// long run of a dense rock field never leaks memory (dodge fires are cheap
    /// and rare, but bounded anyway).</summary>
    public const int MaxDodgeTraces = 512;

    public ConnectionSession Probe { get; }

    /// <summary>The side the server actually seated the probe on (authoritative
    /// world state). Spawned bots default to this side so the probe can see them
    /// (a client only ever sees same-side ships anywhere — the vizfight rule).
    /// Callers may still request the other side explicitly.</summary>
    public short ProbeSide { get; private set; } = -1;

    /// <summary>True once the startup probe is connected, logged on and seated
    /// (i.e. the server is real and serving), so tools know a live server is
    /// reachable before they run.</summary>
    public bool ServerReady => Probe.Ack is { FValidated: true };

    internal WorkerPartition? Partition { get; }
    internal bool PlanOnly { get; set; }

    public BotManager(string host, ushort port, WorkerPartition? partition = null)
    {
        Partition = partition;
        _host = host;
        _port = port;
        using var telemetryOwnership = new PendingOwnership();
        _stallTelemetry = telemetryOwnership.Own(new StallWatchdog());
        using var ownership = new PendingOwnership(DisposePeer);
        Probe = ownership.Own(new ConnectionSession());
        Probe.World.CountOnlyProjectiles = true;
        Probe.World.ReckonRemotes = false;   // P633 — headless probe: no render consumption
        ConnectProbe();
        // P554 — a standalone MCP always talks to a NETWORKED server (host/port),
        // so install the wire-based arena relocator by default: it sends the
        // 'relocate' admin cheat via each bot's OWN connection and waits for the
        // bot's ClientWorld to show the new sector (SET_CLUSTER). The in-process
        // live tests override this with WireArenaRelocator right after
        // construction. When the server refuses (adminCheats off / name not
        // allow-listed) it returns false and the arena path throws a clear error.
        Relocator = CreateNetworkedRelocator();
        _loopThread = new Thread(Loop) { IsBackground = true, Name = "allegiance-mcp-behavior" };
        ownership.Transfer();
        telemetryOwnership.Transfer();
    }

    /// <summary>P554 — how long the networked arena relocator waits for the
    /// relocated bot's ClientWorld to show the target sector before declaring
    /// the relocate refused/undelivered.</summary>
    private const double RelocateTimeoutSeconds = 6.0;

    /// <summary>
    /// P554 — the networked arena relocator (installed by default for a standalone
    /// MCP). Given a bot's ship id, target sector and mirrored pose, it sends the
    /// <c>relocate &lt;shipId&gt; &lt;sectorId&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; [fx fy fz]</c>
    /// admin cheat through the PROBE connection (the allow-listed <c>mcp-probe-*</c>
    /// — see the ownership rule <c>Server.MayRelocate</c>: the probe may move its
    /// own ship or any MCP-harness bot), then waits for the BOT's own real
    /// ClientWorld to show the new sector — the authoritative proof the server
    /// processed the relocate and delivered <c>SET_CLUSTER</c> to that peer
    /// (<c>Server.OnShipWarped</c>). Returns true only on that confirmation;
    /// false = refused/undelivered (the caller throws a clear arena error).
    /// </summary>
    private Func<short, short, Vector3, Vector3, bool> CreateNetworkedRelocator()
    {
        return (shipId, sector, pos, fwd) =>
        {
            ActiveBot? bot;
            lock (_gate) bot = _bots.FirstOrDefault(b => b.ShipId == shipId);
            if (bot?.Cs is not { } cs)
            {
                Console.WriteLine($"[p554] arena: ship {shipId} has no combat wire peer to observe — aborting arena placement");
                return false;
            }
            string F(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string cmd = $"relocate {shipId} {sector} {F(pos.X)} {F(pos.Y)} {F(pos.Z)} {F(fwd.X)} {F(fwd.Y)} {F(fwd.Z)}";
            // Send via the PROBE (the allow-listed 'mcp-*' prefix), which may move
            // its own ship or any MCP-harness bot (Server.MayRelocate). On the
            // refusal path (non-privileged / disabled) the unconsumed line is
            // relayed as Individual chat to the target ship — contained, not a
            // whole-server broadcast.
            lock (Probe.Gate)
                Probe.SendChat(cmd, ChatTarget.Individual, oidRecipient: shipId);
            Console.WriteLine($"[p554] arena: sent relocate cheat '{cmd}' via the probe connection — waiting for SET_CLUSTER on ship {shipId}");

            var deadline = DateTime.UtcNow.AddSeconds(RelocateTimeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (CsSector(cs) == sector)
                {
                    Console.WriteLine($"[p554] arena: ship {shipId} confirmed in sector {sector} (SET_CLUSTER applied to its ClientWorld)");
                    return true;
                }
                Thread.Sleep(50);
            }
            Console.WriteLine($"[p554] arena: ship {shipId} never showed sector {sector} after the relocate cheat — server refused (adminCheats disabled / name not allow-listed) or the relocate was not processed");
            return false;
        };
    }

    private double Now() => _clock.Elapsed.TotalSeconds;

    // ── Startup probe (server validation + side roster) ────────────────────

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = Environment.GetEnvironmentVariable("ALLEGIANCE_PRIVILEGE_SECRET") ?? "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 911u, DwCookie = 0xC0FFEEu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 76000000000000000ul, DrmHash = "allegiance-mcp",
    };

    /// <summary>Connect the probe to the real server and seat it before serving
    /// any tool call, exactly like <c>AttachMain</c> seats its spectator. Throws
    /// with a specific message if the server is unreachable / never answers —
    /// startup therefore fails loudly instead of serving tools against nothing.</summary>
    private void ConnectProbe()
    {
        // Native mission names are unique. Include both worker identity and a
        // fresh suffix so separate supervisors/single managers can coexist.
        string name = $"mcp-probe-w{(Partition?.Index ?? 0) + 1}-{Guid.NewGuid().ToString("N")[..6]}";
        _stallTelemetry.Connect(Probe, name, _host, _port);
        Probe.SendLogonRequest(Request(name));
        if (!WaitUntil(() => Probe.Ack is not null || Probe.Lost, 12000))
            throw new InvalidOperationException($"startup probe: no validated LOGONACK from {_host}:{_port}");
        if (Probe.Ack is not { FValidated: true })
            throw new InvalidOperationException($"startup probe: logon refused: {Probe.Ack?.CharNameOrFailureReason ?? "connection lost"}");
        short side = ProbeSidePref();
        Probe.SendPositionReq(side);
        if (!WaitUntil(() => Probe.World.MyShipId is not null && Probe.World.MySideId is not null || Probe.LastPositionRefusal is not null || Probe.Lost, 15000))
            throw new InvalidOperationException("startup probe: never seated (no ship assigned by the server)");
        if (Probe.LastPositionRefusal is { } refusal)
            throw new InvalidOperationException($"startup probe: side {refusal.SideId} refused: {refusal.Reason}");
        if (Probe.Lost) throw new InvalidOperationException("startup probe: connection lost while selecting a side");
        lock (Probe.Gate)
            ProbeSide = Probe.World.MySideId ?? (short)(side >= 0 ? side : 0);

        // P485 — the freeze root-cause fix. A NON-debug map holds
        // MissionStage.NotStarted until every occupied side's connected players
        // ready up (PLAYER_READY → RecomputeSideReady → TryAutoStart), and
        // Mission.Update early-returns on NotStarted — so the whole sim is
        // frozen and a freshly-seated combat bot never moves. Debug mode
        // auto-starts at boot, which is why only normal maps froze. vizfight
        // bypasses the lobby either in-process (m.Stage = Started — impossible
        // for an out-of-process wire client) or over the wire (SendPlayerReady,
        // its Showcase ReadyUp phase). The MCP harness does the latter: it readies
        // its OWN probe (and every bot it seats, below) exactly as a human pilot
        // clicks Ready, so the mission auto-starts on normal maps too.
        short probeShip = Probe.World.MyShipId!.Value;
        Probe.SendPlayerReady(true, probeShip);
        _probeReadyShipId = probeShip;   // P598: ready state recorded so re-ready only fires on a real rotation
        Console.WriteLine($"[p485] probe ready-up for ship {probeShip} on side {ProbeSide} — enables the normal-map lobby auto-start (PLAYER_READY)");
    }

    private static bool WaitUntil(Func<bool> done, int maxMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(maxMs);
        while (!done() && DateTime.UtcNow < deadline) Thread.Sleep(20);
        return done();
    }

    private short ProbeSidePref()
    {
        lock (Probe.Gate)
            if (Probe.World.SideRoster is { Sides.Length: > 0 } sr)
            {
                // P539c test seam: ALLEGIANCE_MCP_PROBE_SIDE_IDX forces which
                // roster side the probe (and thus the "probe's side") seats on,
                // so the per-side repro can swap the probe's side to test whether
                // the dominance follows the probe. Unset → server auto-pick,
                // respecting P655 capacity/balance without harness privileges.
                if (int.TryParse(Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_PROBE_SIDE_IDX"), out var idx)
                    && idx >= 0 && idx < sr.Sides.Length)
                    return sr.Sides[idx].SideId;
            }
        return -1;
    }

    /// <summary>The set of sides this server can seat a bot on: the real positive
    /// roster ids when the roster reports them, else the {0,1} universe (a
    /// 2-team server, the documented norm) so an explicit side is still validated.</summary>
    public IReadOnlyList<short> ValidSides()
    {
        lock (Probe.Gate)
        {
            if (Probe.World.SideRoster is { Sides.Length: > 0 } sr)
            {
                var ids = sr.Sides.Select(s => s.SideId).Where(s => s >= 0).Distinct().ToList();
                if (ids.Count > 0) return ids;
            }
        }
        return new short[] { 0, 1 };
    }

    /// <summary>True once the probe's real ClientWorld is streaming the given ship
    /// (authoritative proof the server launched/accepted it — same-side visibility).</summary>
    private bool ProbeHasShip(short shipId)
    {
        lock (Probe.Gate)
            return Probe.World.Ships.TryGetValue(shipId, out var e) && e.Latest is not null;
    }

    /// <summary>Read a spawned bot's LIVE pose from the probe's real ClientWorld
    /// (the same authoritative same-side source vizfight uses). False when the
    /// probe can't see that ship (cross-side or another cluster).</summary>
    private bool TryProbePose(short shipId, out Vector3 pos, out short sector)
    {
        lock (Probe.Gate)
        {
            if (Probe.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
            {
                pos = lu.Position;
                sector = e.SectorId;
                return true;
            }
        }
        pos = Vector3.Zero;
        sector = -1;
        return false;
    }

    // ── Spawn (the primary tool) ───────────────────────────────────────────

    /// <summary>Something a <c>spawn_bot</c> call hands back the moment it returns.</summary>
    public readonly record struct SpawnResult(string Name, short ShipId, short Side, string Hull, int ActiveCount, LoadoutGrant Granted, string? Warning);

    /// <summary>P473 — the loadout the server ACTUALLY granted for a spawn: the
    /// acked hull id and the mounted part tail (from the real BUY_LOADOUT_ACK,
    /// or — for the in-world combat peer — the ack the client world applied).
    /// Compared against the requested loadout to surface a refusal or partial
    /// grant instead of silently flying a default ship.</summary>
    public readonly record struct LoadoutGrant(short HullId, IReadOnlyList<ExpandedPartData> Parts);

    /// <summary>
    /// Connect a real peer, seat it on <paramref name="side"/> (or auto-pick the
    /// first available side when null), buy <paramref name="parts"/> and launch
    /// it in one real wire call, then register it with the behavior loop. The
    /// peer is a <see cref="BotClass"/> for <c>idle</c>/<c>weave</c> (a simple
    /// client) and a <see cref="ConnectionSession"/> for <c>combat</c> — combat
    /// needs the peer's OWN <see cref="ClientWorld"/> (own pose, co-located
    /// cross-side ships, warp geometry) to navigate and engage, exactly the
    /// vizfight enemy-peer shape. Blocks only until the seat + launch round-trips
    /// (a short, real confirmation); the ongoing behavior runs in the background
    /// thereafter. Any unresolvable name/side or a genuine server failure throws
    /// a specific error — never a silent no-op.
    /// </summary>
    public SpawnResult SpawnBot(short hullId, string hullName, short? side, IReadOnlyList<ExpandedPartData> parts, string behavior, short? targetSector = null, short? launcherPartId = null, string? launcherRequestName = null,
        bool useStaging = false, bool arena = false, Vector3? arenaPosition = null, Vector3? arenaFacing = null,
        short wingmanBehaviour = Showcase.DefaultScenarioWingman,
        IReadOnlyList<short>? routePath = null, int routeIndex = -1, string? role = null, bool reportFailure = true)
    {
        try
        {
            return SpawnBotCore(hullId, hullName, side, parts, behavior, targetSector, launcherPartId, launcherRequestName,
                useStaging, arena, arenaPosition, arenaFacing, wingmanBehaviour, routePath, routeIndex, role);
        }
        catch (Exception ex)
        {
            if (reportFailure) SpawnLaunch.LogFailure(side ?? ProbeSide, hullName, ex.Message);
            throw;
        }
    }

    private SpawnResult SpawnBotCore(short hullId, string hullName, short? side, IReadOnlyList<ExpandedPartData> parts, string behavior, short? targetSector = null, short? launcherPartId = null, string? launcherRequestName = null,
        bool useStaging = false, bool arena = false, Vector3? arenaPosition = null, Vector3? arenaFacing = null,
        short wingmanBehaviour = Showcase.DefaultScenarioWingman,
        IReadOnlyList<short>? routePath = null, int routeIndex = -1, string? role = null)
    {
        if (!ServerReady)
            throw new InvalidOperationException("spawn_bot: server probe not connected/validated — cannot spawn");

        // Validate / auto-pick the side. Default = the probe's own side.
        // An explicit side is validated against
        // the real roster when it reports positive ids, else the {0,1} universe.
        short chosenSide;
        if (side is { } requested)
        {
            var sides = ValidSides();
            if (!sides.Contains(requested))
                throw new InvalidOperationException($"spawn_bot: side {requested} is not a side of this server (valid sides: {string.Join(", ", sides)})");
            chosenSide = requested;
        }
        else
        {
            chosenSide = ProbeSide >= 0 ? ProbeSide : (ValidSides().Count > 0 ? ValidSides()[0] : (short)0);
        }

        // P433 — a requested real missile launcher is appended to the loadout
        // NOW, gated against the hull's real capability mask (gun-only when it
        // does not fit — an honest, logged outcome, never a fabricated launcher).
        List<ExpandedPartData> finalParts = parts.ToList();
        double missileReady = 0;
        if (launcherPartId is { } lp)
        {
            string? note = Catalog.AppendLauncher(finalParts, lp, hullId, hullName, launcherRequestName ?? "");
            missileReady = Catalog.LauncherReadyTime(lp);
            Console.WriteLine($"[p433] spawn_bot hull '{hullName}' (id {hullId}) launcher request: {note ?? "(no launcher)"}");
        }

        string name = NextName();
        using var ownership = new PendingOwnership(DisposePeer);
        ActiveBot bot = null!;
        ownership.OnFailure(() => { if (bot is not null) RemoveInactiveBot(bot, "spawn failed"); });
        short shipId;
        short actualSide;
        LoadoutGrant granted = default;
        string? grantWarning = null;

        if (behavior == "combat")
        {
            // A combat bot is driven as its own ConnectionSession (mirrors vizfight's
            // AttachEnemyBot.Cs): it must read its own world's pose, cross-side ships
            // and warp geometry while it transits clusters the probe never sees.
            var cs = ownership.Own(SeatCombatClient(name, chosenSide, hullId, finalParts, out shipId, out actualSide, out granted, out grantWarning));
            DiagnoseProbeLaunch(name, shipId, actualSide);

            bot = new ActiveBot(name, hullId, hullName, actualSide, chosenSide, behavior, NextSeed())
            {
                Cs = cs,
                ShipId = shipId,
                Launched = true,
                TargetSector = targetSector,
                Parts = finalParts,
                WeaponPartId = Catalog.FirstWeaponPartId(finalParts),
                LauncherPartId = launcherPartId,
                MissileReadyTime = missileReady,
                Granted = granted,
                GrantWarning = grantWarning,
            };
        }
        else
        {
            // idle / weave: a plain BotClass client (same-side observable by the probe).
            var b = ownership.Own(new BotClass { AutoSelectSideAfterLogon = false, RetainRawCombatUpdates = false });
            _stallTelemetry.Connect(b, name, _host, _port);
            b.SendLogonRequest(Request(name));
            if (!WaitUntil(() => b.Ack is { FValidated: true }, 12000))
                throw new InvalidOperationException($"spawn_bot: bot '{name}' never got a validated LOGONACK from {_host}:{_port}");
            b.SendPositionReq(chosenSide);
            if (!WaitUntil(() => b.ShipAssignment is not null, 15000))
                throw new InvalidOperationException($"spawn_bot: bot '{name}' was never seated (no ship assigned) on side {chosenSide}");

            shipId = b.ShipAssignment!.Ship.ShipId;
            actualSide = ActualSide(b, shipId, chosenSide);
            // P485 — ready this bot's own seat so a normal-map mission can
            // auto-start (see ConnectProbe's ready-up comment).
            b.SendPlayerReady(true, shipId);

            // Only this peer's purchase ack and fresh own launch state count.
            // Preserve refusal / successor / partial-grant loadout reporting.
            var ownLaunched = SpawnLaunch.Observe(b, shipId);
            b.ResetBuyLoadoutAcks();
            granted = BuyAndConfirmLaunch(name, hullId, finalParts, () => b.Connected, ownLaunched,
                () => b.SendBuyLoadout(hullId, fLaunch: true, finalParts), () => b.BuyLoadoutAcks, out grantWarning);
            DiagnoseProbeLaunch(name, shipId, actualSide);

            bot = new ActiveBot(name, hullId, hullName, actualSide, chosenSide, behavior, NextSeed())
            {
                Bot = b,
                ShipId = shipId,
                Launched = true,
                Parts = finalParts,
                WeaponPartId = Catalog.FirstWeaponPartId(finalParts),
                LauncherPartId = launcherPartId,
                MissileReadyTime = missileReady,
                Granted = granted,
                GrantWarning = grantWarning,
            };
        }

        // P557 — the native wingman behaviour mask for this bot (scenario/fleet
        // override, else the default), and its real mounted MINE DISPENSER if
        // the loadout carries one (detected from the parts actually requested).
        bot.WingmanBehaviour = wingmanBehaviour;
        bot.MineDispenserPartId = FindMineDispenser(finalParts);
        // P637 — the bot's showcase role (drives its OWN target sector + route).
        if (role is { Length: > 0 }) bot.Role = role;

        // P635 — multi-route wing assignment (route path + wing index).
        if (routePath is { Count: > 0 } rp)
        {
            bot.RoutePath = rp.ToList();
            bot.RouteIndex = routeIndex;
        }

        // P539d — fair-contact bookkeeping (combat bots only; idle/weave bots
        // never travel or fight, so staging/arena leave them be).
        bot.LaunchedAt = Now();
        if (targetSector is { } objective)
        {
            lock (Probe.Gate)
                if (Probe.World.Clusters.TryGetValue(objective, out var cluster)) bot.ObjectiveName = cluster.Name;
        }
        if (bot.Cs is { } initialCs)
        {
            bot.LifeSession = initialCs;
            bot.LifeGeneration = initialCs.World.MissionGeneration;
            bot.LifeCookie = initialCs.World.Mission?.Cookie;
        }
        // P637 — a NON-rendezvous role (bomber) does NOT stage: it is on its OWN
        // objective (the enemy home), so the fair-contact staging barrier for the
        // fighter rendezvous would only strand it (it can never "assemble" a
        // fighter wing). Routing straight toward its own target (with its own
        // route path honoured by RoutePathSuffix) lets it actually reach the
        // enemy base. Fighters keep the staging barrier.
        bot.UseStaging = useStaging && behavior == "combat"
            && !string.Equals(bot.Role, "bomber", StringComparison.OrdinalIgnoreCase);
        if (arena && behavior == "combat")
        {
            bot.Arena = true;
            if (arenaPosition is { } ap) bot.ArenaPosition = ap;
            if (arenaFacing is { } af) bot.ArenaFacing = af;
        }

        lock (_gate)
        {
            _bots.Add(bot);
            if (!_startedLoop)
            {
                _startedLoop = true;
                _loopThread.Start();
            }
        }

        // P554 — the arena relocation runs AFTER the bot is registered in
        // _bots, because the networked relocator looks the bot up by shipId to
        // send the 'relocate' cheat via the bot's OWN connection (ownership = a
        // privileged player relocates their own ship). Arena REQUIRES a real
        // relocation: a missing relocator or a server that refuses (adminCheats
        // disabled / name not allow-listed) is a HARD ERROR, never the old
        // silent "bot stays at home / travels" fallback — the caller sees it in
        // the tool output instead of unknowingly staging.
        if (arena && behavior == "combat")
        {
            if (targetSector is not { } ts)
                throw FailArena(name, bot, "spawn_bot: arena mode requires a `target_sector` to place the bot in");
            if (arenaPosition is not { } ap2)
                throw FailArena(name, bot, "spawn_bot: arena mode requires a mirrored arena position");
            if (bot.Cs is null)
                throw FailArena(name, bot, "spawn_bot: arena mode needs the bot's own wire peer (combat ConnectionSession) to drive the relocate cheat");
            var reloc = Relocator
                ?? throw FailArena(name, bot, "spawn_bot: arena mode requires a relocation mechanism, but no Relocator is registered — cannot place the bot in the arena sector");

            bool ok;
            try { ok = reloc(bot.ShipId, ts, ap2, arenaFacing ?? Vector3.Zero); }
            catch (Exception ex)
            {
                throw FailArena(name, bot, $"spawn_bot: arena relocation for bot '{name}' (ship {bot.ShipId}) threw: {ex.Message}");
            }
            if (!ok)
                throw FailArena(name, bot,
                    $"spawn_bot: arena relocation for bot '{name}' (ship {bot.ShipId}) to sector {ts} was REFUSED or not delivered by the server — "
                    + "liveSettings.adminCheats is likely disabled or the bot's name not allow-listed (check the boot banner '[boot] *** ADMIN CHEATS ACTIVE'); the bot was NOT silently staged.");

            bot.Arrived = true;
            bot.ArrivedAt ??= Now();
            Console.WriteLine($"[p539d] arena: bot {name} (ship {bot.ShipId} side {actualSide}) relocated to mirrored spot in sector {ts}");
        }

        if (!IsParticipating(bot))
            throw new InvalidOperationException($"spawn_bot: bot '{name}' disconnected before registration completed");
        bot.SpawnPending = false;
        ownership.Transfer();
        int count;
        lock (_gate) count = _bots.Count;
        return new SpawnResult(name, shipId, actualSide, hullName, count, granted, grantWarning);
    }

    public bool IsSpawnActive(SpawnResult result)
    {
        lock (_gate) return _bots.Any(b => b.Name == result.Name && !b.SpawnPending && IsParticipating(b));
    }

    private void DiagnoseProbeLaunch(string name, short shipId, short side)
    {
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_MCP_SPAWN_PROBE_DIAGNOSTICS") == "1" && side == ProbeSide)
            Console.Error.WriteLine($"[p657] bot '{name}' own launch confirmed; probeSeen={ProbeHasShip(shipId)} (diagnostic only)");
    }

    private static LoadoutGrant BuyAndConfirmLaunch(string name, short hullId, IReadOnlyList<ExpandedPartData> parts,
        Func<bool> connected, Func<bool> ownLaunched, Action send, Func<IReadOnlyList<BuyLoadoutAck>> acks, out string? warning)
    {
        var ack = SpawnLaunch.Confirm(name, connected, ownLaunched, send, acks,
            a => EvaluateGrant(name, hullId, parts, new LoadoutGrant(a.HullId, a.Parts)), SpawnLaunch.TimeoutMs,
            matches: a => a.HullId == hullId || IsSuccessorOf(hullId, a.HullId));
        var grant = new LoadoutGrant(ack.HullId, ack.Parts.ToList());
        warning = EvaluateGrant(name, hullId, parts, grant);
        return grant;
    }

    /// <summary>P554 — on an arena-relocation failure the bot must not linger as
    /// an orphan wire peer; dispose its connection(s) and return the clear error
    /// that the tool surfaces (instead of silently staging).</summary>
    private Exception FailArena(string name, ActiveBot bot, string message)
    {
        RemoveInactiveBot(bot, "arena spawn failed");
        Console.WriteLine($"[p554] arena: {message}");
        return new InvalidOperationException(message);
    }

    /// <summary>
    /// P431 — seat + launch a real combat-capable <see cref="ConnectionSession"/>
    /// peer: connect, logon, request <paramref name="side"/>, buy the loadout and
    /// launch it in one real wire call. Extracted from the SpawnBot combat branch
    /// so both initial spawn and the P431 idle→combat upgrade share the exact
    /// same flow. Throws with a specific message on any genuine server failure.
    /// P473 — waits for the real BUY_LOADOUT_ACK and evaluates it against the
    /// requested loadout (refusal throws, partial grant is a warning), so a
    /// combat spawn can never silently fly a refused default ship.
    /// </summary>
    private ConnectionSession SeatCombatClient(string name, short side, short hullId, IReadOnlyList<ExpandedPartData> parts, out short shipId, out short actualSide, out LoadoutGrant granted, out string? warning)
    {
        using var ownership = new PendingOwnership(DisposePeer);
        var cs = ownership.Own(new ConnectionSession());
        cs.World.CountOnlyProjectiles = true;
        cs.World.ReckonRemotes = false;   // P633 — headless bot harness: no render consumption of dead-reckoners
        _stallTelemetry.Connect(cs, name, _host, _port);
        cs.SendLogonRequest(Request(name));
        if (!WaitUntil(() => cs.Ack is { FValidated: true }, 12000))
            throw new InvalidOperationException($"combat client '{name}' never got a validated LOGONACK from {_host}:{_port}");
        cs.SendPositionReq(side);
        if (!WaitUntil(() => cs.World.MyShipId is not null, 15000))
            throw new InvalidOperationException($"combat client '{name}' was never seated (no ship assigned) on side {side}");

        shipId = cs.World.MyShipId!.Value;
        actualSide = cs.World.MySideId ?? side;
        // P485 — ready this bot's own seat so a normal-map mission can auto-start
        // (see ConnectProbe's ready-up comment). Every MCP-managed side is thus
        // "ready" exactly as a human pilot clicking Ready would be.
        cs.SendPlayerReady(true, shipId);
        // P473 — reset the ack accumulator so the only ack considered is the one
        // answering THIS purchase (not the seat's join-burst default-hull ack).
        var ownLaunched = SpawnLaunch.Observe(cs, shipId);
        cs.ResetBuyLoadoutAcks();
        granted = BuyAndConfirmLaunch(name, hullId, parts, () => cs.Connected && !cs.Lost, ownLaunched,
            () => cs.SendBuyLoadout(hullId, fLaunch: true, parts), () => cs.BuyLoadoutAcks, out warning);
        ownership.Transfer();
        return cs;
    }

    /// <summary>A snapshot of the probe's live sector map (id → cluster record
    /// with its real Name) — the authoritative real corpus <c>target_sector</c>
    /// resolves against. Waits briefly for the join-burst cluster exports so a
    /// name resolves even on the very first tool call after startup.</summary>
    public IReadOnlyDictionary<short, DataClusterIGC> ProbeClusters()
    {
        WaitUntil(() =>
        {
            lock (Probe.Gate) return Probe.World.Clusters.Count > 0;
        }, 3000);
        lock (Probe.Gate)
            return new Dictionary<short, DataClusterIGC>(Probe.World.Clusters);
    }

    /// <summary>
    /// P539d — the geometric centre of a sector (centroid of its warps and
    /// stations, from the probe's real world), used to mirror arena spawns about
    /// a common point so travel can't favour either side. null when the sector is
    /// unknown or has no anchor geometry yet.
    /// </summary>
    public Vector3? ProbeSectorCenter(short sector)
    {
        var pts = new List<Vector3>();
        lock (Probe.Gate)
        {
            foreach (var w in Probe.World.Warps.Values)
                if (w.ClusterId == sector && w.Position is { Length: 3 } p)
                    pts.Add(new Vector3(p[0], p[1], p[2]));
            foreach (var st in Probe.World.Stations.Values)
                if (st.ClusterId == sector && st.Position is { Length: 3 } p)
                    pts.Add(new Vector3(p[0], p[1], p[2]));
        }
        if (pts.Count == 0) return null;
        var c = Vector3.Zero;
        foreach (var p in pts) c += p;
        return c / pts.Count;
    }

    // ── P486: commander tools (invest / list_drones / order_drone) ──────────
    //
    // The three tools act through a "harness commander bot": a seated `mcp-`
    // peer on the chosen side that (a) holds the P473 grant money in its
    // wallet to fund real investments, (b) has a real ship id (MyShipId) so it
    // can send Individual quick-chat orders (P457/P469 CommandToDrone), and
    // (c) streams a real ClientWorld that observes the side's ships, buckets
    // (BUCKET_STATUS), money (MONEY_CHANGE/PAYDAY) and chats ("Affirmative.")
    // — the same same-side observability the probe has. No server change; the
    // native drone AI flies the drones, MCP only commands them.

    /// <summary>P557 — the ARRIVAL aleph in sector <paramref name="sector"/>:
    /// a warp located there whose partner warp sits in a DIFFERENT sector (an
    /// outbound aleph a fleet entering the sector would transit), from the
    /// probe's real world. Used to place a minefield in front of the enemy's
    /// arrival aleph. Returns null when the sector has no such outbound aleph
    /// with a known position yet.</summary>
    public (short WarpId, string? Name, Vector3 Pos, short DestSector)? FindArrivalWarp(short sector)
    {
        lock (Probe.Gate)
        {
            foreach (var w in Probe.World.Warps.Values)
            {
                if (w.ClusterId != sector) continue;
                if (!Probe.World.Warps.TryGetValue(w.DestinationId, out var partner)) continue;
                if (partner.ClusterId == sector) continue;      // a same-sector pairing (not an arrival)
                if (w.Position is not { Length: 3 } p) continue;
                return (w.WarpId, w.Name, new Vector3(p[0], p[1], p[2]), partner.ClusterId);
            }
        }
        return null;
    }

    /// <summary>P557 — the aleph in sector <paramref name="sector"/> whose
    /// partner warp sits in <paramref name="destSector"/> (the specific warp a
    /// fleet coming FROM destSector would arrive through), or null.</summary>
    public (short WarpId, Vector3 Pos)? FindWarpToSector(short sector, short destSector)
    {
        lock (Probe.Gate)
        {
            foreach (var w in Probe.World.Warps.Values)
            {
                if (w.ClusterId != sector) continue;
                if (!Probe.World.Warps.TryGetValue(w.DestinationId, out var partner)) continue;
                if (partner.ClusterId != destSector) continue;
                if (w.Position is not { Length: 3 } p) continue;
                return (w.WarpId, new Vector3(p[0], p[1], p[2]));
            }
        }
        return null;
    }

    /// <summary>
    /// P591d — resolve the sector that side <paramref name="side"/>'s fleet
    /// comes FROM (its home/anchor sector): the cluster containing a station
    /// owned by that side (preferring the nearest restart-capable one, else any
    /// of its stations) from the probe's real world. Used to place a minefield
    /// across the ENEMY's arrival aleph: the enemy fleet transits INTO the
    /// rendezvous from this side's home region, so the enemy's arrival aleph is
    /// the warp in the rendezvous whose partner sits on the route from here.
    /// Returns null when the side owns no observed station yet.
    /// </summary>
    public short? FindSideHomeSector(short side)
    {
        var core = Catalog.RepresentativeCore();
        short? restartSector = null, anySector = null;
        lock (Probe.Gate)
        {
            foreach (var st in Probe.World.Stations.Values)
            {
                if (st.SideId != side) continue;
                if (st.ClusterId <= 0) continue;
                anySector ??= st.ClusterId;
                // Restart-capable home (c_sabmRestart) is the strongest anchor —
                // the sector a side's freshly-launched ships sit in.
                if (core.StationTypes.TryGetValue(st.StationTypeId, out var t)
                    && t.HasCapability(DataStationTypeIGC.SabmRestart))
                    restartSector ??= st.ClusterId;
            }
        }
        return restartSector ?? anySector;
    }

    /// <summary>
    /// P591d — the warp in the rendezvous sector (<paramref name="rendezvous"/>)
    /// through which the fleet of side <paramref name="enemySide"/> (whose home
    /// is <paramref name="enemyHomeSector"/>) transits to ENTER the rendezvous —
    /// the "enemy's arrival aleph" a minefield should straddle. Mirrors the
    /// bots' own routing (<c>EnsureCombatPath</c> → <c>ClusterGraph.FindShortestPath</c>:
    /// undirected edges, neighbouring clusters walked ascending so the chosen
    /// route is deterministic): the penultimate cluster of the shortest path
    /// enemyHome→rendezvous is where the enemy arrives from; the arrival aleph
    /// is the warp located in the rendezvous whose partner sits in that
    /// penultimate sector. Returns null when the enemy home is unreachable from
    /// the rendezvous or no such warp resolves. Pure over the probe's world.
    /// </summary>
    public (short WarpId, short PenultimateSector)? FindEnemyArrivalWarp(short rendezvous, short enemySide, short enemyHomeSector)
    {
        lock (Probe.Gate)
            return EnemyArrivalWarp(Probe.World.Warps, rendezvous, enemySide, enemyHomeSector);
    }

    /// <summary>
    /// P591d — the pure core of <see cref="FindEnemyArrivalWarp"/>: compute the
    /// enemy's arrival aleph from the raw warp table alone, so the placement
    /// choice is unit-provable against the real map with no live server.
    /// Neighbour-cluster order is ascending (deterministic, matching the bots'
    /// <c>ClusterGraph.FindShortestPath</c>), so the returned path — and thus the
    /// penultimate arrival sector — is reproducible.
    /// </summary>
    internal static (short WarpId, short PenultimateSector)? EnemyArrivalWarp(
        IReadOnlyDictionary<short, DataWarpIGC> warps, short rendezvous, short enemySide, short enemyHomeSector)
    {
        // Undirected adjacency: cluster → sorted neighbour clusters.
        var adj = new Dictionary<short, List<short>>();
        foreach (var w in warps.Values)
        {
            if (!warps.TryGetValue(w.DestinationId, out var partner)) continue;
            AddDir(adj, w.ClusterId, partner.ClusterId);
            AddDir(adj, partner.ClusterId, w.ClusterId);
        }
        foreach (var k in adj.Keys) adj[k].Sort();

        var path = BfsShortestPath(adj, enemyHomeSector, rendezvous);
        if (path.Count < 2) return null;               // unreachable or already there
        short penult = path[^2];                        // the sector the enemy enters FROM
        // The arrival aleph = the warp IN the rendezvous whose partner sits in penult.
        foreach (var w in warps.Values)
        {
            if (w.ClusterId != rendezvous) continue;
            if (!warps.TryGetValue(w.DestinationId, out var partner)) continue;
            if (partner.ClusterId != penult) continue;
            return (w.WarpId, penult);
        }
        return null;
    }

    private static void AddDir(Dictionary<short, List<short>> adj, short a, short b)
    {
        if (!adj.TryGetValue(a, out var l)) { l = new(); adj[a] = l; }
        if (!l.Contains(b)) l.Add(b);
    }

    private static List<short> BfsShortestPath(Dictionary<short, List<short>> adj, short from, short to)
    {
        if (from == to) return [from];
        var prev = new Dictionary<short, short>();
        var visited = new HashSet<short> { from };
        var queue = new Queue<short>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            short cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var nbrs)) continue;
            foreach (short nb in nbrs)
            {
                if (!visited.Add(nb)) continue;
                prev[nb] = cur;
                if (nb == to)
                {
                    var path = new List<short>();
                    for (short node = to; ; node = prev[node]) { path.Add(node); if (node == from) break; }
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(nb);
            }
        }
        return [];
    }

    private readonly Dictionary<short, ConnectionSession> _commanders = new();

    // ── P635 — multi-route wing discovery + assignment ────────────────────

    /// <summary>
    /// P635 — enumerate up to <paramref name="maxRoutes"/> distinct entry-aleph
    /// routes from <paramref name="from"/> to <paramref name="to"/> over the raw
    /// warp table (the authoritative map topology the bots route on). Mirror of
    /// <c>ClusterGraph.FindDistinctEntryRoutes</c> that works off the probe's
    /// <c>DataWarpIGC</c> world (no ClientWorld CLUSTERINFO needed), so routes can
    /// be discovered at spawn_scenario time. Each candidate route enters the
    /// destination through a distinct neighbouring (penultimate) sector; ranked by
    /// hops ascending; capped at <paramref name="maxRoutes"/>; identical full paths
    /// deduped. Pure over the warp table, so unit-provable against the real map.
    /// </summary>
    internal static List<List<short>> RouteEnumeration(
        IReadOnlyDictionary<short, DataWarpIGC> warps, short from, short to, int maxRoutes)
    {
        if (from == to || maxRoutes <= 0) return [];
        var adj = new Dictionary<short, List<short>>();
        foreach (var w in warps.Values)
        {
            if (!warps.TryGetValue(w.DestinationId, out var partner)) continue;
            AddDir(adj, w.ClusterId, partner.ClusterId);
            AddDir(adj, partner.ClusterId, w.ClusterId);
        }
        foreach (var k in adj.Keys) adj[k].Sort();
        if (!adj.TryGetValue(to, out var toN)) return [];

        var candidates = new List<List<short>>();
        foreach (short entry in toN)
        {
            var sub = BfsAvoidingRoute(adj, from, entry, to);
            if (sub.Count == 0) continue;                 // entry unreachable without `to`
            candidates.Add(new List<short>(sub) { to });
        }
        candidates.Sort((a, b) => a.Count.CompareTo(b.Count));

        var result = new List<List<short>>();
        var seen = new HashSet<string>();
        foreach (var route in candidates)
        {
            if (result.Count >= maxRoutes) break;
            var key = string.Join(",", route);
            if (!seen.Add(key)) continue;
            result.Add(route);
        }
        return result;
    }

    private static List<short> BfsAvoidingRoute(Dictionary<short, List<short>> adj, short from, short to, short banned)
    {
        if (from == to) return [from];
        var prev = new Dictionary<short, short>();
        var visited = new HashSet<short> { from };
        var queue = new Queue<short>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            short cur = queue.Dequeue();
            if (!adj.TryGetValue(cur, out var nbrs)) continue;
            foreach (short nb in nbrs)
            {
                if (nb == banned) continue;               // never detour through the rendezvous
                if (!visited.Add(nb)) continue;
                prev[nb] = cur;
                if (nb == to)
                {
                    var path = new List<short>();
                    for (short node = to; ; node = prev[node]) { path.Add(node); if (node == from) break; }
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(nb);
            }
        }
        return [];
    }

    /// <summary>
    /// P635 — resolve the routes a side can take to <paramref name="rendezvous"/>
    /// from the probe's real world: the side's home sector (P591d anchor), then up
    /// to <paramref name="maxRoutes"/> distinct-entry routes. Empty when the home
    /// sector or the routes can't be resolved (caller falls back to single-route
    /// behaviour).</summary>
    public List<List<short>> FindRoutes(short rendezvous, short? sideHome, int maxRoutes)
    {
        if (sideHome is not { } home) return [];
        lock (Probe.Gate)
            return RouteEnumeration(Probe.World.Warps, home, rendezvous, maxRoutes);
    }

    /// <summary>P637 — a NON-rendezvous role's OWN target: a BOMBER's objective
    /// is the ENEMY HOME sector (its garrison); every other role converges on
    /// the fighter <paramref name="rendezvous"/>. Pure so the role→target rule
    /// is unit-provable. When the enemy home can't be resolved (negative) the
    /// bomber falls back to the rendezvous.</summary>
    public static short ResolveRoleTarget(Showcase.HullRole role, short rendezvous, short enemyHome)
        => role == Showcase.HullRole.Bomber && enemyHome >= 0 ? enemyHome : rendezvous;

    /// <summary>P637 — whether two bots belong to the SAME staging/release group:
    /// same side, SAME target goal sector, and same route wing. Two bots that
    /// share a side and route index but target DIFFERENT goals (a bomber aiming
    /// at the enemy home vs a fighter at the rendezvous) are NOT the same group,
    /// so neither waits on the other's release. Pure so the grouping rule is
    /// unit-provable.</summary>
    public static bool SameStagingGroup(short sideA, short goalA, int routeA, short sideB, short goalB, int routeB)
        => sideA == sideB && goalA == goalB && routeA == routeB;

    /// <summary>
    /// P635 — the pure wing-assignment rule: pick the LEAST-LOADED route (argmin of
    /// the per-route bot counts). Distributing every bot to the current argmin
    /// splits the side evenly across routes; when all routes are at the
    /// <c>maxPerRoute</c> cap the extra goes to the least-loaded (still the argmin,
    /// ties resolved to the lowest index). Pure so the split/cap/least-loaded rule
    /// is unit-provable.</summary>
    public static int PickLeastLoadedRoute(int[] counts)
    {
        int best = 0;
        for (int i = 1; i < counts.Length; i++)
            if (counts[i] < counts[best]) best = i;
        return best;
    }

    /// <summary>
    /// P486 — get (or lazily seat) the cached commander peer for
    /// <paramref name="side"/>: connect, logon, request that side, wait for the
    /// ship assignment, and ready the seat (P485, so a normal-map mission can
    /// auto-start). Deliberately does NOT buy a loadout — a seated `mcp-` peer
    /// already carries the P473 grant money in its wallet, which is all invest
    /// needs, and the default spawned hull is a valid sender. Registered only in
    /// the commander cache (not the behavior loop), so it never fights.
    /// </summary>
    private ConnectionSession EnsureCommander(short side)
    {
        lock (_commanders)
        {
            if (_commanders.TryGetValue(side, out var existing) && existing.Connected)
                return existing;
            DisposePeer(existing);
            string name = $"mcp-cmd{side}-{Interlocked.Increment(ref _nameCounter)}-{Random.Shared.Next(1000, 9999)}";
            using var ownership = new PendingOwnership(DisposePeer);
            var cs = ownership.Own(new ConnectionSession());
            cs.World.CountOnlyProjectiles = true;
            cs.World.ReckonRemotes = false;   // P633 — headless harness: no render consumption
            _stallTelemetry.Connect(cs, name, _host, _port);
            cs.SendLogonRequest(Request(name));
            if (!WaitUntil(() => cs.Ack is { FValidated: true }, 12000))
                throw new InvalidOperationException($"commander: side {side} peer '{name}' never got a validated LOGONACK from {_host}:{_port}");
            cs.SendPositionReq(side);
            if (!WaitUntil(() => cs.World.MyShipId is not null, 15000))
                throw new InvalidOperationException($"commander: side {side} peer '{name}' was never seated (no ship assigned)");
            cs.SendPlayerReady(true, cs.World.MyShipId!.Value);
            Console.WriteLine($"[p486] commander for side {side} seated: '{name}' ship {cs.World.MyShipId} — P473 grant money in wallet, Individual-chat orders + BUCKET_STATUS/MONEY_CHANGE observable");
            _commanders[side] = cs;
            ownership.Transfer();
            return cs;
        }
    }

    /// <summary>P486 — one drone's observed wire picture (see each tool for what
    /// is / isn't observable from a same-side player's view).</summary>
    public readonly record struct DroneRow(
        short ShipId, string Type, short HullId, short? SectorId,
        float[]? Position, byte? State, string? CharacterName, string? Notes);

    /// <summary>P486 — the <c>invest</c> outcome: what was donated and observed
    /// (progress / completion) plus the drone that appeared on the wire.</summary>
    public readonly record struct InvestResult(
        short Side, short BucketId, string BucketName, string BucketType,
        int AmountRequested, long AmountSpent, int? WalletBefore, int? WalletAfter,
        long InvestedMoney, int Price, long InvestedTimeMs, uint TimeToBuildSec,
        bool Completed, double ObserveSeconds, DroneRow? Drone);

    /// <summary>P486 — the <c>list_drones</c> outcome.</summary>
    public readonly record struct ListDronesResult(short Side, IReadOnlyList<DroneRow> Drones);

    /// <summary>P486 — the <c>order_drone</c> outcome: the order issued and the
    /// drone's observed reaction (an "Affirmative." reply + a state change).</summary>
    public readonly record struct OrderResult(
        short DroneId, string Command, string TargetDesc, bool Sent,
        bool AffirmativeObserved, string? StateBefore, string? StateAfter,
        short? SectorBefore, short? SectorAfter, string? Note,
        int ChatsSeen, string? DroneReply);

    /// <summary>
    /// P486 — resolve a bucket by id or name (exact-over-unique-substring,
    /// case-insensitive) from the side's synthesized catalog
    /// (<see cref="Allegiance.Core.Model.BucketCatalog.Describe"/>, the SAME id
    /// numbering the server baked into its list at <c>Mission.Assemble</c>).
    /// Throws a specific error on ambiguity or no-match — never a guess.
    /// </summary>
    private static Allegiance.Core.Model.BucketCatalog.BucketView ResolveBucket(
        IReadOnlyList<Allegiance.Core.Model.BucketCatalog.BucketView> views, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("invest: required argument `bucket` (name or id) is missing");
        if (short.TryParse(query.Trim(), out short id))
        {
            var byId = views.FirstOrDefault(v => v.Id == id);
            if (byId is not null) return byId;
            throw new ArgumentException($"invest: no bucket with id {id} on this side (available ids: {string.Join(", ", views.Select(v => v.Id))})");
        }
        var matches = views.Where(v => v.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return matches[0];
        var subs = views.Where(v => v.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (subs.Count == 1) return subs[0];
        if (subs.Count > 1)
            throw new ArgumentException($"invest: bucket '{query}' is ambiguous ({subs.Count} matches: {string.Join(", ", subs.Select(v => v.Name))})");
        throw new ArgumentException($"invest: no bucket named '{query}' (available: {string.Join(", ", views.Select(v => v.Name))})");
    }

    /// <summary>P486 — invest into a real side bucket. Funds through
    /// <see cref="ConnectionSession.SendBucketDonate"/> (the P473-granted
    /// commander's wallet), then observes the real BUCKET_STATUS progress and,
    /// when the bucket completes within the window (money full AND build time
    /// elapsed), the drone that appears on the wire (a NEW same-side NPC with
    /// the completed bucket's drone hull). Returns observed progress otherwise.
    /// </summary>
    public InvestResult Invest(short side, string bucketQuery, int? amountArg, double observeSeconds)
    {
        if (!ServerReady)
            throw new InvalidOperationException("invest: server probe not connected/validated — cannot invest");
        var commander = EnsureCommander(side);
        var core = Catalog.RepresentativeCore();

        // Wait for the side's initial bucket mask (CREATE_BUCKETS join sync)
        // so the synthesized catalog ids line up bucket-for-bucket with the
        // server's (P487's exact premise).
        if (!WaitUntil(() => commander.World.InitialTechsFor(side) is not null, 8000))
            throw new InvalidOperationException(
                $"invest: side {side}'s bucket/tech sync (CREATE_BUCKETS) never arrived — cannot resolve real bucket ids yet");

        var techs = commander.World.InitialTechsFor(side)!.Value;
        var ga = commander.World.GlobalAttributesFor(side);
        var views = Allegiance.Core.Model.BucketCatalog.Describe(core, techs, ga);
        var view = ResolveBucket(views, bucketQuery);
        if (view.ItemType == Allegiance.Core.Model.BucketItemType.BitDevelopment)
            // Funding a development is a real invest too, but it completes a
            // research tech, not a drone — P486 is about drone production.
            throw new ArgumentException(
                $"invest: bucket '{view.Name}' (id {view.Id}) is a DEVELOPMENT bucket (research), not a drone/station production bucket; this tool invests in drone-type and station-type buckets that spawn drones.");

        long already = 0;
        var preFund = commander.World.BucketFundingOf(side, view.Id);
        if (preFund is { } pf) already = pf.InvestedMoney;
        long needed = Math.Max(0, (long)view.Price - already);
        int donate = amountArg ?? (int)Math.Min(needed, int.MaxValue);
        if (donate > needed) donate = (int)needed;   // the server caps at the outstanding balance anyway
        int? walletBefore = commander.World.MyMoney;
        if (walletBefore is { } wb && donate > wb)
            throw new InvalidOperationException(
                $"invest: commander wallet {wb} < requested donation {donate} for bucket '{view.Name}' (price {view.Price}, already invested {already}) — donate less or raise the P473 grant money");

        // Snapshot the side's ship ids BEFORE the donation so the spawned drone
        // is recognized as genuinely NEW (the side may already have an initial
        // miner / other drones).
        HashSet<short> beforeIds;
        lock (commander.Gate) beforeIds = new HashSet<short>(
            commander.World.Ships.Where(kv => kv.Value.SideId == side && !kv.Value.IsMine).Select(kv => kv.Key));

        commander.SendBucketDonate(view.Id, donate);
        Console.WriteLine($"[p486] invested {donate} (spent ≤ {needed}) into bucket {view.Id} '{view.Name}' on side {side}");

        // Observe: wallet debit, funding progress, and — on completion — a new drone.
        // Note: the client's BUCKET_STATUS is refreshed only on a donation
        // broadcast, so InvestedTimeMs here does NOT tick visibly even though
        // the server's bucket build-time accrues every sim tick. Completion is
        // therefore signalled by the drone APPEARING on the wire once the money
        // is full — not by the observed time reaching the build threshold.
        var start = Now();
        int? walletAfter = walletBefore;
        long investedMoney = already;
        long investedTimeMs = preFund?.InvestedTimeMs ?? 0;
        bool completed = false;
        DroneRow? drone = null;
        var deadline = start + observeSeconds;
        // Phase 1: watch the funding climb to the full price (usually instant).
        while (Now() < deadline)
        {
            long im; int? mw;
            lock (commander.Gate)
            {
                im = commander.World.BucketFundingOf(side, view.Id)?.InvestedMoney ?? already;
                mw = commander.World.MyMoney;
            }
            investedMoney = Math.Max(investedMoney, im);
            if (mw is not null) walletAfter = mw;
            if (investedMoney >= view.Price) break;
            Thread.Sleep(100);
        }
        // Phase 2: money full — the server's build time is elapsing invisibly;
        // watch for the spawned drone (the real completion signal) up to the end
        // of the window.
        if (investedMoney >= view.Price)
        {
            while (Now() < deadline)
            {
                drone = FindNewDrone(commander, side, beforeIds, view);
                if (drone is not null) { completed = true; break; }
                long it; int? mw2;
                lock (commander.Gate)
                {
                    it = commander.World.BucketFundingOf(side, view.Id)?.InvestedTimeMs ?? 0;
                    mw2 = commander.World.MyMoney;
                }
                investedTimeMs = Math.Max(investedTimeMs, it);
                if (mw2 is not null) walletAfter = mw2;
                Thread.Sleep(100);
            }
        }

        return new InvestResult(side, view.Id, view.Name, view.ItemType.ToString(),
            donate, Math.Min(donate, Math.Max(0, needed)), walletBefore, walletAfter,
            investedMoney, view.Price, investedTimeMs, view.TimeToBuildSec,
            completed, observeSeconds, drone);
    }

    /// <summary>P486 — find the drone that a completed bucket produced: a NEW
    /// (post-invest) same-side NPC. Prefers the completed bucket's own drone
    /// hull (view.HullIdDrone when set) but reports any newly-appeared NPC so a
    /// station-type bucket (whose constructor hull the view doesn't carry) is
    /// still captured.</summary>
    private DroneRow? FindNewDrone(ConnectionSession commander, short side,
        HashSet<short> beforeIds, Allegiance.Core.Model.BucketCatalog.BucketView view)
    {
        List<(short Id, Allegiance.ClientCore.State.ShipEntity s)> news = new();
        lock (commander.Gate)
        {
            foreach (var kv in commander.World.Ships)
            {
                if (kv.Value.IsMine || kv.Value.SideId != side) continue;
                if (beforeIds.Contains(kv.Key)) continue;        // existed before invest
                if (kv.Value.Player is not null) continue;       // a real (new) player, not a drone
                news.Add((kv.Key, kv.Value));
            }
        }
        // Prefer the bucket's own drone hull if set (>0).
        var exact = news.Where(n => view.HullIdDrone > 0 && n.s.HullId == view.HullIdDrone)
                        .OrderBy(n => n.s.Latest is null).FirstOrDefault();
        var any = news.OrderBy(n => n.s.Latest is null).FirstOrDefault();
        var chosen = exact.Id != 0 ? exact : any;
        if (chosen.Id == 0) return null;
        return ToDroneRow(chosen.s);
    }

    /// <summary>P486 — snapshot one same-side ship as an observable
    /// <see cref="DroneRow"/> (position only when the latest update is known;
    /// sector only when telemetry has been seen).</summary>
    private static DroneRow ToDroneRow(Allegiance.ClientCore.State.ShipEntity s)
    {
        var pos = s.Latest is not null
            ? new[] { s.Latest.Position.X, s.Latest.Position.Y, s.Latest.Position.Z }
            : null;
        return new DroneRow(s.ShipId, ResolveDroneTypeName(s.HullId), s.HullId,
            s.Latest is not null ? s.SectorId : null, pos,
            s.Latest is not null ? s.State : (byte?)null,
            string.IsNullOrEmpty(s.CharacterName) ? null : s.CharacterName,
            "same-side NPC (Player==null) as observed by the commander; cargo/ore not on the ship wire from a peer's view");
    }

    /// <summary>P486 — the human drone-type name for a hull id, from the static
    /// core's drone-type roster (or the synthetic ship-type hull 504), else a
    /// plain "(hull N)" so an unknown NPC hull is still identifiable.</summary>
    private static string ResolveDroneTypeName(short hullId)
    {
        var core = Catalog.RepresentativeCore();
        foreach (var dt in core.DroneTypes.Values)
            if (dt.HullTypeId == hullId) return dt.Name.Trim('.');
        if (hullId == Allegiance.Core.Model.BucketCatalog.SyntheticDroneHullId)
            return "ship-type";
        return $"(hull {hullId})";
    }

    /// <summary>
    /// P560 — the ID of the side's native LAYER-drone bucket (the drone-type
    /// bucket whose <see cref="DataDroneTypeIGC.PilotType"/> is
    /// <see cref="Ship.PilotTypeLayer"/> = 5 — the mine-layer pilot, igc.h:688),
    /// as a decimal STRING that <see cref="ResolveBucket"/> parses back to an
    /// exact numeric id, or null when the side has no such drone bucket. The
    /// layer drone lays minefields (FedSrv.CPP:11524-11540);
    /// <c>spawn_scenario minefields</c> invests THIS bucket so it can order the
    /// spawned layer drone to build a field. Null (not an error) when the
    /// side's civ/map simply has no layer drone available.
    /// <para><b>P560</b> — this must return the bucket's ID, never its NAME:
    /// several civs' layer-drone buckets share one name ('.TowerAC' matches 10
    /// buckets on the 221 map), so the name-based invest hit ResolveBucket's
    /// ambiguity error and aborted the whole showcase. A numeric query resolves
    /// by exact id (ResolveBucket's <c>short.TryParse</c> branch) — unambiguous.
    /// </summary>
    public string? FindLayerBucketName(short side) => FindLayerBucket(side)?.Id;

    /// <summary>
    /// P560 — like <see cref="FindLayerBucketName"/>, but also returns the
    /// bucket's real <c>TimeToBuildSec</c>. The spawn-scenario minefield path
    /// needs it: the layer drone must be fully BUILT before it can be ordered to
    /// lay, and the invest observe window must span that real build time (on the
    /// 221 map the layer bucket '.TowerAC' has TimeToBuildSec 90 — far beyond the
    /// old fixed 40 s window, so the drone never appeared and the field was
    /// reported "did not complete in window").
    /// </summary>
    public (string Id, uint TimeToBuildSec)? FindLayerBucket(short side)
    {
        if (!ServerReady)
            throw new InvalidOperationException("invest: server probe not connected/validated");
        var commander = EnsureCommander(side);
        var core = Catalog.RepresentativeCore();
        if (!WaitUntil(() => commander.World.InitialTechsFor(side) is not null, 8000))
            return null;
        var techs = commander.World.InitialTechsFor(side)!.Value;
        var ga = commander.World.GlobalAttributesFor(side);
        var views = Allegiance.Core.Model.BucketCatalog.Describe(core, techs, ga);
        (string, uint)? layerAny = null;
        (string, uint)? layerMinefield = null;
        foreach (var v in views)
        {
            if (v.ItemType != Allegiance.Core.Model.BucketItemType.BitDroneType) continue;
            if (v.DroneType is not { } dt || dt.PilotType != Allegiance.Core.Sim.Ship.PilotTypeLayer) continue;
            (string, uint) item = (
                v.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                v.TimeToBuildSec);
            layerAny ??= item;
            // P591 — the showcase must buy/order a MINEFIELD layer, not a tower
            // layer. A layer's laid expendable (dt.EtidLaid, the drone type's
            // GetLaidExpendable()) decides what it deploys: on the 221 civs the
            // '.Caltrop' layer (drone type 200) lays a MINE (EtidLaid 1, icon
            // 'minefield'), while '.TowerAC'/'.TowerSC' lay PROBES (towers,
            // EtidLaid 87/92/102/107) — so a mine-laid layer is the real
            // minefield. Prefer it so `spawn_scenario … minefields` lays actual
            // mines (P452 damage + P574 pops) instead of towers.
            if (core.MineTypes.ContainsKey(dt.EtidLaid))
            {
                layerMinefield ??= item;
                if (v.Name.Contains("Layer", StringComparison.OrdinalIgnoreCase)) return item;
            }
            if (v.Name.Contains("Layer", StringComparison.OrdinalIgnoreCase)) return item;
        }
        return layerMinefield ?? layerAny;
    }

    /// <summary>P486 — <c>list_drones</c>: every same-side NPC (drone) the
    /// commander observes on the wire, with its id, drone type (from its real
    /// hull), sector, position, state and name where those are observable, plus
    /// an honest note about what a player's view can't see (cargo/ore/order).</summary>
    public ListDronesResult ListDrones(short side, string? typeFilter)
    {
        var commander = EnsureCommander(side);
        var rows = new List<DroneRow>();
        lock (commander.Gate)
        {
            foreach (var kv in commander.World.Ships)
            {
                var s = kv.Value;
                if (s.IsMine) continue;
                if (s.SideId != side) continue;          // same-side only (the only visibility)
                if (s.Player is not null) continue;      // a real player, not a drone
                var row = ToDroneRow(s);
                if (typeFilter is not null &&
                    !row.Type.Contains(typeFilter, StringComparison.OrdinalIgnoreCase) &&
                    !s.HullId.ToString().Contains(typeFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                rows.Add(row);
            }
        }
        rows.Sort((a, b) => a.ShipId.CompareTo(b.ShipId));
        return new ListDronesResult(side, rows);
    }

    /// <summary>P486 — the commander that can see a given drone id (seating a
    /// commander per side until one of them streams the ship). The drone's own
    /// side determines which commander (and which wallet/sender) is used.</summary>
    private ConnectionSession CommanderSeeing(short droneId)
    {
        foreach (var side in ValidSides())
        {
            var cs = EnsureCommander(side);
            bool has;
            lock (cs.Gate)
                has = cs.World.Ships.TryGetValue(droneId, out var e) && e.SideId == side && e.Player is null;
            if (has) return cs;
        }
        throw new InvalidOperationException(
            $"order_drone: drone {droneId} is not observable by any commander peer (not a same-side NPC on any valid side {string.Join(",", ValidSides())} — use list_drones first)");
    }

    /// <summary>
    /// P486 — <c>order_drone</c>: issue a real order to a drone through the
    /// P469/P457 path — an Individual quick-chat (FM_CS_CHATMESSAGE 23) naming
    /// the drone as recipient with the command's native <c>CommandID</c> and a
    /// resolved target — exactly the shape native's <c>CommandToDrone</c> consumes
    /// (FedSrv.CPP:3555-3570 → 3175-3325). Returns the drone's observed reaction:
    /// the server's "Affirmative." reply in the commander's chat stream and any
    /// sector/state change over a short window.
    /// </summary>
    public OrderResult OrderDrone(short droneId, string command, string? targetDesc)
    {
        if (!ServerReady)
            throw new InvalidOperationException("order_drone: server probe not connected/validated");
        if (!DroneCommands.TryParseCommand(command, out short cid))
            throw new ArgumentException(
                $"order_drone: unknown command '{command}' (recognized: default, goto, attack, defend, mine, build, donothing, repair, pickup, join)");
        if (!DroneCommands.TryParseTarget(targetDesc, out IgcObjectType ot, out short oid))
            throw new ArgumentException(
                $"order_drone: cannot parse target '{targetDesc}' (forms: asteroid:<id>, ship:<id>, station:<id>, warp:<id>, a bare ship id, or none)");

        var commander = CommanderSeeing(droneId);
        short stateBefore; short sectorBefore;
        lock (commander.Gate)
        {
            var e = commander.World.Ships[droneId];
            stateBefore = e.Latest is not null ? e.State : (short)-1;
            sectorBefore = e.Latest is not null ? e.SectorId : (short)-1;
        }

        int chatsBefore;
        lock (commander.Gate) chatsBefore = commander.World.Events.Chats.Count;

        // P469: Individual chat to the drone with CommandID + target.
        commander.SendChat("", ChatTarget.Individual, oidRecipient: droneId,
            commandId: cid, oidTarget: oid, otTarget: (short)ot);
        string targetLabel = oid < 0 ? "(none)" : $"{DroneCommands.KindName(ot)}:{oid}";
        Console.WriteLine($"[p486] ordered drone {droneId} (cid={cid} '{command}') target {targetLabel}");

        // Observe the reaction: "Affirmative." from the drone + any state change.
        bool affirmative = false;
        short stateAfter = stateBefore; short sectorAfter = sectorBefore;
        double deadline = Now() + 8.0;
        while (Now() < deadline)
        {
            bool aff; short st; short sec;
            lock (commander.Gate)
            {
                aff = commander.World.Events.Chats.Skip(Math.Max(0, chatsBefore))
                        .Any(c => c.SidSender == droneId && c.Message != null && c.Message.StartsWith("Affirmative", StringComparison.OrdinalIgnoreCase));
                st = commander.World.Ships[droneId].Latest is not null ? commander.World.Ships[droneId].State : stateAfter;
                sec = commander.World.Ships[droneId].Latest is not null ? commander.World.Ships[droneId].SectorId : sectorAfter;
            }
            if (aff) affirmative = true;
            stateAfter = st; sectorAfter = sec;
            if (affirmative && (stateAfter != stateBefore || sectorAfter != sectorBefore)) break;
            Thread.Sleep(100);
        }

        string? note = null;
        if (cid != OrderChange.CidDoNothing && oid >= 0 && !affirmative)
            note = "no \"Affirmative.\" reply observed — the order may have been refused (illegal command for this pilot type), the target unresolved, or the reply not yet relayed; the drone state/sector is the secondary check.";
        if (affirmative && stateAfter == stateBefore && sectorAfter == sectorBefore)
            note = "replied \"Affirmative.\" but no sector/state change within the window (e.g. already at target / awaiting build time) — the ack is the primary signal.";
        // P486 diagnostic: what chats the commander actually received from the drone
        // (an empty reply here pinpoints whether the server's CommandToDrone ack
        // reached the commander at all, independent of state/sector heuristics).
        string? droneReply = null;
        int chatsSeen;
        lock (commander.Gate)
        {
            var fromDrone = commander.World.Events.Chats.Where(c => c.SidSender == droneId).Select(c => c.Message).ToList();
            chatsSeen = commander.World.Events.Chats.Count;
            if (fromDrone.Count > 0) droneReply = string.Join(" | ", fromDrone.Take(3));
        }
        return new OrderResult(droneId, command, targetLabel, Sent: true, affirmative,
            StateName(stateBefore), StateName(stateAfter), sectorBefore, sectorAfter, note,
            chatsSeen, droneReply);
    }

    private static string? StateName(short state)
    {
        return state switch
        {
            0 => "no-order", 1 => "waiting-to-undock", 2 => "auto-pilot",
            3 => "disabled", 4 => "semi-active", 5 => "attack", 6 => "react-to-self",
            7 => "goto", 8 => "launch", 9 => "run-away", 10 => "stay", 11 => "dock",
            12 => "mine", 13 => "collect", 14 => "build", 15 => "repair",
            16 => "pickup", 17 => "warp", 18 => "inbattle", _ => $"({state})",
        };
    }

    private string NextName()
    {
        int n = Interlocked.Increment(ref _nameCounter);
        int rand = Random.Shared.Next(1000, 9999);
        // P473 — the "mcp-" prefix matches the server's liveSettings.testHarness
        // characterNamePrefix (default "mcp-"), so a granted debug server treats
        // every MCP bot as a harness connection — and a human's name never does.
        return Partition is { } p ? $"mcp-w{p.Index + 1}-b{n:D3}-{rand}" : $"mcp-b{n:D3}-{rand}";
    }

    /// <summary>The side the server actually seated the ship on (from the real
    /// JOIN_SIDE broadcast for this ship), falling back to the requested side.</summary>
    private static short ActualSide(BotClass b, short shipId, short requested)
    {
        foreach (var js in b.JoinSides)
            if (js.ShipId == shipId) return js.SideId;
        return requested;
    }

    /// <summary>The real core, resolved once (PCore001 — the same core the
    /// server boots), used to tell a P488 successor upgrade from a P441 refusal
    /// by walking the requested hull's successor chain.</summary>
    private static readonly Lazy<StaticCore> HarnessCore = new(() => Catalog.RepresentativeCore());

    /// <summary>P488 — true when <paramref name="grantedHull"/> is the requested
    /// hull or a descendant of it in the real successor chain (any depth, the
    /// structural GetSuccessorHullType walk). Used to recognise a legitimate
    /// successor grant vs a refusal (whose ack echoes the unchanged default
    /// hull, which is not in the requested hull's chain).</summary>
    private static bool IsSuccessorOf(short requestedHull, short grantedHull)
    {
        if (requestedHull == grantedHull) return true;
        var core = HarnessCore.Value;
        short cur = requestedHull;
        var seen = new HashSet<short> { cur };
        while (core.HullTypes.TryGetValue(cur, out var h) && h.SuccessorHullId is { } nx && nx >= 0 && seen.Add(nx))
        {
            if (nx == grantedHull) return true;
            cur = nx;
        }
        return false;
    }

    /// <summary>
    /// P473 — compare the granted loadout against the requested one. An outright
    /// refusal (the server acked a DIFFERENT — unchanged — hull, i.e.
    /// <c>fbought=false</c> under P441's log-and-refuse) is an error: throw a
    /// specific message so a <c>spawn_bot</c>/matrix row fails loudly instead of
    /// silently flying a default ship. A partial grant (requested hull acked but
    /// some requested parts not mounted — tech-gated or no compatible hardpoint)
    /// is returned as a warning string, never a silent success.
    /// <para><b>P488</b> — a granted hull that is a SUCCESSOR of the requested
    /// one is a legitimate upgrade (the side out-teched the requested hull,
    /// native GetSuccessor), not a refusal: it is surfaced as an informational
    /// warning (plus a partial-grant note when some requested parts don't fit
    /// the successor), never thrown.</para>
    /// </summary>
    private static string? EvaluateGrant(string botName, short requestedHull, IReadOnlyList<ExpandedPartData> requestedParts, LoadoutGrant granted)
    {
        var requestedIds = new HashSet<short>(requestedParts.Select(p => p.PartId));
        var grantedIds = granted.Parts.Select(p => p.PartId).ToHashSet();
        var missing = requestedIds.Where(id => !grantedIds.Contains(id)).OrderBy(id => id).ToList();

        if (granted.HullId != requestedHull)
        {
            if (IsSuccessorOf(requestedHull, granted.HullId))
            {
                // P488 — legitimate successor upgrade. Not an error; surface it.
                var note = $"bot '{botName}' was granted the SUCCESSOR hull {granted.HullId} (an upgrade of the requested {requestedHull} — the side has out-teched it, native GetSuccessor, shipIGC.h:1004)";
                if (missing.Count > 0)
                    note += $"; PARTIAL GRANT: requested parts [{string.Join(", ", missing)}] were not mounted on the successor ({granted.Parts.Count} granted)";
                return note;
            }
            throw new InvalidOperationException(
                $"spawn_bot: bot '{botName}' was REFUSED hull {requestedHull} — the server granted the unchanged hull {granted.HullId} instead (fbought=false). "
                + "Under native tech gating most hulls aren't buyable at a fresh side's garrison; enable liveSettings.testHarness (prefix match) for the harness "
                + "to buy it, or request a sellable hull.");
        }
        if (missing.Count > 0)
            return $"bot '{botName}' got a PARTIAL GRANT on hull {requestedHull}: requested parts [{string.Join(", ", missing)}] were not mounted "
                   + $"(tech-gated or no compatible hardpoint on the granted hull); {granted.Parts.Count} part(s) granted.";
        return null;
    }

    private static double NextSeed() => Random.Shared.NextDouble() * (2 * Math.PI);

    // ── Behavior loop (background; drives every bot for the process life) ──

    private void Loop()
    {
        // P636b — a FIXED deadline cadence (target 20 Hz) drives the loop, and it
        // NEVER busy-spins: the scheduler yields a minimum sleep even when a pass
        // overshoots its tick, so the loop can't monopolise a core and starve the
        // per-bot receive threads (the P636 runaway). The measured wall period
        // between pass STARTS (not the pass WORK time) is what the decision-rate
        // metric reports.
        double passStartMs = Now() * 1000.0;
        double lastPassStartMs = passStartMs;
        _cadence.Begin(passStartMs);
        while (!_loopCts.IsCancellationRequested)
        {
            passStartMs = Now() * 1000.0;
            lock (_metricsGate) _lastWallPeriodMs = passStartMs - lastPassStartMs;
            lastPassStartMs = passStartMs;
            var passSw = Stopwatch.StartNew();
            _stallTelemetry.Metrics.BeginPass();
            try
            {
                double now = Now();
                long coordinationEpoch = CoordinationEpoch;
                ActiveBot[] snap;
                lock (_gate) snap = _bots.ToArray();
                // P622 — start a fresh engagement pass: clear the accumulated
                // engager counts so each pass recomputes the coordinated spread
                // from scratch (single decision thread; readers use _engagersSnapshot).
                _tickEngagers.Clear();
                foreach (var b in snap)
                    if (IsParticipating(b) && !b.SpawnPending && !b.InScenario && b.Cs is { } lifeCs) RefreshLife(b, lifeCs, now);
                UpdateFinales(now);
                UpdateDefense(snap, now);

                // P636d — LAZY publish: request a fresh decision snapshot from every
                // seated bot's receive thread BEFORE this pass reads .Decision. The
                // request is a single volatile write (no gate, no lock), so it costs
                // ~nothing on this thread; the receive threads then rebuild their
                // worlds' snapshots on the next batch — at the decision cadence
                // instead of a fixed ~25 Hz — and the snapshots this pass reads are
                // freshly published. Idle/not-seated bots never request, so their
                // worlds never publish (zero cost), and in `off` mode
                // (DecisionSnapshotEnabled == false) no request matters because the
                // receive thread never publishes at all.
                if (DecisionSnapshotEnabled)
                {
                    for (int i = 0; i < snap.Length; i++)
                    {
                        var b = snap[i];
                        if (b.Cs?.World is { } w)
                        {
                            if (w.MyShipId is not null) w.RequestDecisionRefresh();
                            else w.CancelDecisionRefresh();   // not seated → zero publish
                        }
                    }
                }

                // P633 — PHASE A (parallel, read-only): precompute each combat
                // bot's target-choice INPUTS (its co-located different-side
                // visible candidates + the friendly-station flag) in PARALLEL
                // across cores. The expensive O(clusterShips) per-bot scan only
                // reads the bot's OWN ClientWorld under its own cs.Gate and is
                // independent of _tickEngagers, so the result is deterministic
                // regardless of scheduling — exactly the O(N²) the single-thread
                // loop paid serially (bots reacted slower exactly when the fight
                // was busiest). The anti-swarm SETTLE (which mutates
                // _tickEngagers) stays SEQUENTIAL below in bot order, so P622's
                // cap / threat-tier / detachment-and-hold / P431-order-wins
                // guarantees are preserved bit-for-bit (see the two-phase
                // equivalence unit test).
                BuildPassSpatialIndices(snap);
                var prep = new PreparedEngagement?[snap.Length];
                System.Threading.Tasks.Parallel.For(0, snap.Length, i =>
                {
                    var b = snap[i];
                    if (!IsParticipating(b) || b.SpawnPending || b.ObjectivesPending || b.InScenario || b.Behavior != "combat" || b.Cs is not { } pcs) return;
                    if (pcs.World.MyShipId is null) return;                 // not seated yet
                    // P636 — Phase A now reads the lock-free DECISION SNAPSHOT
                    // (published by each bot's receive thread) instead of scanning
                    // the live Ships/Stations under the bot's cs.Gate. This removes
                    // the O(clusterShips) gate scans from the decision hot path, so
                    // a pass no longer stalls behind the receive thread's bulk ship
                    // application under the same gate at 200v200.
                    var snapshot = pcs.World.Decision;
                    if (DecisionSnapshotEnabled && snapshot.MissionGeneration != pcs.World.MissionGeneration) return;
                    b.DecisionFrame = snapshot;
                    using var frameScope = new DecisionFrameScope(pcs, snapshot);
                    Vector3 myPos; short myCluster;
                    if (!DecisionSnapshotEnabled)
                    {
                        // P636c "off" — P635 Phase A reads: own pose + sector live.
                        if (pcs.World.MyShipId is null) return;
                        if (!TryCsPose(pcs, b.ShipId, out myPos, out _)) return;
                        myCluster = CsSector(pcs);
                    }
                    else
                    {
                        if (snapshot.MyShipId is not { }) return;
                        if (!(DecisionSnapshot.TryFind(snapshot.Ships, b.ShipId, out var meDs) && meDs.HasLatest)) return;
                        myPos = meDs.Position;
                        myCluster = meDs.SectorId;
                    }
                    double cnow = Now();
                    var cands = new List<BotTargetCandidate>();
                    bool friendly = false;
                    if (!DecisionSnapshotEnabled)
                    {
                        // P636c "off" — P635 Phase A: scan the live tables under
                        // cs.Gate (each ship filtered by IsNotVisible, side, sector).
                        lock (pcs.Gate)
                        {
                            foreach (var st in pcs.World.Stations.Values)
                                if (st.SideId == b.Side && st.ClusterId == myCluster) { friendly = true; break; }
                            foreach (var ship in pcs.World.Ships.Values)
                            {
                                if (ship.ShipId == b.ShipId || ship.IsNotVisible) continue;
                                if (ship.SideId == b.Side) continue;
                                if (ship.SectorId != myCluster) continue;
                                if (ship.Latest is not { } lu) continue;
                                float d = (lu.Position - myPos).Length();
                                var cls = CombatTargetSelector.ClassifyTargetId(ship.HullId);
                                                                if (IgnoreMinerTargets && cls == BotTargetClass.MinerDrone) continue;   // showcase: never target miners/drones (Domingo 2026-10-13)
bool isThreat =
                                    (b.RecentAttackers.TryGetValue(ship.ShipId, out var at) && cnow - at < ThreatWindowSeconds)
                                    || (friendly && CombatTargetSelector.IsStationThreatening(cls));
                                cands.Add(new BotTargetCandidate(ship.ShipId, lu.Position, cls, isThreat) { Distance = d });
                            }
                        }
                    }
                    else
                    {
                        foreach (var st in snapshot.Stations)
                            if (st.SideId == b.Side && st.ClusterId == myCluster) { friendly = true; break; }
                        foreach (var ship in snapshot.VisibleShips)
                        {
                            if (ship.ShipId == b.ShipId) continue;
                            if (ship.SideId == b.Side) continue;      // a DIFFERENT side only
                            if (ship.SectorId != myCluster) continue;   // co-located
                            if (!ship.HasLatest) continue;
                            float d = (ship.Position - myPos).Length();
                            var cls = CombatTargetSelector.ClassifyTargetId(ship.HullId);
                                                        if (IgnoreMinerTargets && cls == BotTargetClass.MinerDrone) continue;   // showcase: never target miners/drones (Domingo 2026-10-13)
bool isThreat =
                                (b.RecentAttackers.TryGetValue(ship.ShipId, out var at) && cnow - at < ThreatWindowSeconds)
                                || (friendly && CombatTargetSelector.IsStationThreatening(cls));
                            cands.Add(new BotTargetCandidate(ship.ShipId, ship.Position, cls, isThreat) { Distance = d });
                        }
                    }
                    prep[i] = new PreparedEngagement(cands, friendly);
                });

                for (int i = 0; i < snap.Length; i++)
                {
                    var bot = snap[i];
                    try
                    {
                        // P474 — a bot owned by an active run_scenario preset is
                        // driven by the scenario's own controller thread (blocking
                        // the tool call), never by this generic loop, so the two
                        // never race on the same wire peer.
                        if (bot.SpawnPending) continue;
                        if (!IsParticipating(bot)) { RemoveInactiveBot(bot, "connection lost"); continue; }
                        if (bot.InScenario) continue;

                        // P431 — every active bot accepts real teammate orders each
                        // tick (no human to ask: MCP bots obey immediately) and adopts
                        // the commanded ship-target; this may upgrade an idle/weave
                        // BotClass into a combat ConnectionSession and set Behavior.
                        using var frameScope = new DecisionFrameScope(bot.Cs, bot.DecisionFrame);
                        HandleOrders(bot, now);
                        if (bot.Behavior == "combat")
                        {
                            DriveCombat(bot, now, prep[i]);
                        }
                        else if (bot.Bot?.ShipAssignment is not null && bot.Behavior == "weave")
                        {
                            double yaw = Math.Sin(now * bot.WeaveFreq + bot.WeavePhase) * bot.WeaveAmp;
                            double throttle = 0.55 + 0.25 * Math.Sin(now * bot.WeaveFreq * 0.7 + bot.WeavePhase * 1.3);
                            bot.Bot.CommandControls(new ControlData((float)yaw, 0f, 0f, (float)throttle), 0);
                        }
                        // idle: seat but send no movement commands — the simplest real state.
                        // Every bot (any behavior) also contributes its real update
                        // cadence, so get_perf_stats can measure the live stream.
                        SampleCadence(bot, now);
                        // P598 — re-ready after a mission rotation: the server
                        // re-seats migrated peers on the rebuilt mission with a
                        // fresh ship id; re-propagate the bot's (always-on) ready
                        // intent to the new ship so a bot-driven showcase auto-
                        // starts the next wave.
                        ReReadyBot(bot);
                    }
                    catch { /* one bot's tick failure never kills the loop */ }
                }

                // P622 — end of pass: publish the completed engager distribution
                // (target → engager count) for read-only consumers like
                // list_active_bots, under _gate.
                EnterGate(() =>
                {
                    _engagersSnapshot.Clear();
                    foreach (var kv in _tickEngagers) _engagersSnapshot[kv.Key] = kv.Value;
                    return true;
                });

                // P598 — re-ready the probe after a mission rotation too: the
                // server re-seats it on the rebuilt mission with a fresh ship
                // id; re-propagate its ready intent so the next wave starts.
                try { ReReadyProbe(); } catch { }
                SampleIdleTargets(snap, now);
                if (Partition is not null) PublishCoordination(snap, now, coordinationEpoch);

            }
            catch { }
            finally
            {
                // Keep the stall pass open through resource sampling too: a
                // wait in that work must be visible as an unfinished/long pass.
                try { RecordPass(passSw.Elapsed.TotalMilliseconds); } catch { }
                passSw.Stop();
                _stallTelemetry.Metrics.EndPass(passSw.Elapsed.TotalMilliseconds);
            }

            // P633 — FIXED decision cadence (item 2): sleep `target − elapsed`,
            // never `50 ms + pass time`, so a busy fight keeps bots deciding on
            // time (a slow pass no longer drags the decision interval out).
            // P636b — via the deadline scheduler, which NEVER busy-spins: even a
            // pass that overshoots its tick yields a minimum sleep so the loop
            // can't starve the receive threads (the P636 runaway at 200v200).
            double sleepMs = _cadence.SleepForRemainder(Now() * 1000.0);
            if (sleepMs > 0) Thread.Sleep((int)sleepMs);
        }
    }

    // P598 — the probe ship id the MCP last readied for (-1 = not readied yet).
    private short _probeReadyShipId = -1;

    // ── P633 — decision-loop measurement helpers (item 1/6) ──────────────

    /// <summary>P633 — run <paramref name="fn"/> under <c>_gate</c> and record
    /// how long the lock was held (the loop thread's only <c>_gate</c> write
    /// acquisitions; reads in tools are separate). Reports the max hold time so
    /// we can see whether the MCP request path is stalling bot decisions or a
    /// slow pass is stalling tool replies (item 6).</summary>
    private T EnterGate<T>(Func<T> fn)
    {
        var sw = Stopwatch.StartNew();
        T r;
        lock (_gate) r = fn();
        sw.Stop();
        double held = sw.Elapsed.TotalMilliseconds;
        lock (_metricsGate) if (held > _maxGateHoldMs) _maxGateHoldMs = held;
        return r;
    }

    /// <summary>P633 — record one completed pass's decision time into the
    /// bounded rolling window, refresh the latest pass time (fixed cadence
    /// driver), and periodically sample process resources + decode throughput.</summary>
    private void RecordPass(double passMs)
    {
        lock (_metricsGate)
        {
            _lastPassMs = passMs;
            _passTimesMs.Add(passMs);
            if (_passTimesMs.Count > MaxPassSamples) _passTimesMs.RemoveAt(0);
        }
        SampleRuntimeOnce();
    }

    private double _lastResourceSampleAt = -1;
    private TimeSpan _prevTotalCpu = TimeSpan.Zero;
    private double _prevCpuFrac;
    // P636d — rolling snapshot-publish + decode-lag samplers (aggregate across
    // every world; populated in SampleRuntimeOnce, read under _metricsGate).
    private long _lastSnapPubCount;
    private double _lastSnapPubMs;
    private double _lastSnapPubWall;
    private double _snapPublishPerSec, _snapPublishMsPerSec;
    private double _decodeLagMean, _decodeLagP95, _decodeLagMax;

    /// <summary>P633 — sample the bot process's CPU/RSS/GC and the aggregate
    /// inbound decode throughput (MB/s) roughly once per second (cheap reads;
    /// never on a per-pass cadence).</summary>
    private void SampleRuntimeOnce()
    {
        double wall = Environment.TickCount64;
        if (_lastResourceSampleAt > 0 && wall - _lastResourceSampleAt < 1000) return;
        var proc = Process.GetCurrentProcess();
        _lastResources = DecisionMetrics.SampleResources(
            proc, _lastResourceSampleAt, _prevTotalCpu, _prevCpuFrac,
            out _lastResourceSampleAt, out _prevTotalCpu, out _prevCpuFrac);

        long tot = 0;
        lock (_gate)
            foreach (var b in _bots)
                if (b.Cs is { } cs) tot += cs.DecodedBytes;
        double dw = Environment.TickCount64;
        if (_lastDecodeWall > 0)
            _decodeMbPerSec = (tot - _lastDecodeBytes) / (1024.0 * 1024.0)
                / Math.Max(1e-3, (dw - _lastDecodeWall) / 1000.0);
        _lastDecodeBytes = tot;
        _lastDecodeWall = dw;

        // P636d — sample the aggregate snapshot-PUBLISH cost (the receive-thread
        // work this task removes) and the decode lag across every world. Publish
        // count/ms are cumulative per world (Interlocked), summed and diffed over
        // the same ~1 s wall window as decode throughput; decode lag is
        // now − world.LastAppliedTick64 (the wall stamp of the newest applied
        // update) aggregated across worlds at this instant.
        long pubTot = 0; double pubMsTot = 0;
        var lags = new List<double>();
        lock (_gate)
        {
            foreach (var b in _bots)
            {
                if (b.Cs is not { } cs) continue;
                var w = cs.World;
                pubTot += w.SnapshotPublishCount;
                pubMsTot += w.SnapshotPublishMs;
                long applied = w.LastAppliedTick64;
                if (applied > 0) lags.Add(Math.Max(0, dw - applied));
            }
        }
        if (_lastSnapPubWall > 0)
        {
            double dtSec = Math.Max(1e-3, (dw - _lastSnapPubWall) / 1000.0);
            _snapPublishPerSec = (pubTot - _lastSnapPubCount) / dtSec;
            _snapPublishMsPerSec = (pubMsTot - _lastSnapPubMs) / dtSec;
        }
        else
        {
            _snapPublishPerSec = _snapPublishMsPerSec = 0;
        }
        _lastSnapPubCount = pubTot;
        _lastSnapPubMs = pubMsTot;
        _lastSnapPubWall = dw;
        if (lags.Count > 0)
        {
            lags.Sort();
            double sum = 0; foreach (var v in lags) sum += v;
            _decodeLagMean = sum / lags.Count;
            _decodeLagP95 = lags[Math.Min(lags.Count - 1, (int)Math.Ceiling(0.95 * lags.Count) - 1)];
            _decodeLagMax = lags[^1];
        }
    }

    // P598 — re-ready the probe after a mission rotation too: the
    private void ReReadyProbe()
    {
        short cur;
        lock (Probe.Gate) cur = Probe.World.MyShipId ?? -1;
        if (cur < 0 || cur == _probeReadyShipId) return;
        _probeReadyShipId = cur;
        Probe.SendPlayerReady(true, cur);
        Console.WriteLine($"[p598] re-readied probe ship {cur} after rotation (PLAYER_READY true)");
    }

    // ── Order acceptance + translation (P431) ─────────────────────────────
    //
    // An MCP bot is a real connected peer, so it ALREADY receives the real
    // owner-only QUEUED_COMMAND_UPDATE(215) decode — the same generic path any
    // client uses (ConnectionSession.cs:625-629; Bot.cs P424 accumulator) —
    // whenever the server names its ship as the commandee of a same-side ship's
    // queued order (P424's accept/reject mechanic). MCP bots have no human to
    // ask: they auto-accept immediately, always (that is the whole point), and
    // translate the accepted order into real behavior — engaging the SPECIFIC
    // commanded enemy ship, never "nearest enemy".

    /// <summary>P431 — for ONE bot each loop tick: read its real pending queued
    /// command, auto-accept it over the real ORDER_CHANGE wire, and adopt the
    /// commanded ship-target so the combat engine engages THAT specific enemy.
    /// Universal: runs for every active bot — both the
    /// <see cref="ConnectionSession"/>-driven combat bots (incl. every
    /// spawn_scenario bot) and the plain <see cref="BotClass"/> idle/weave ones
    /// (an idle bot that accepts a ship-target order is UPGRADED to a combat
    /// ConnectionSession so it can genuinely engage).</summary>
    private void HandleOrders(ActiveBot bot, double now)
    {
        // 1) Read the bot's real pending queued order from its own wire state.
        QueuedCommandState? pending;
        if (bot.Cs is { } cs)
        {
            lock (cs.Gate) pending = cs.World.MyQueuedCommand;
        }
        else if (bot.Bot is { } b)
        {
            // A BotClass has no world; it accumulates every QUEUED_COMMAND_UPDATE(215)
            // it receives (P424). Fold ONLY the updates since the last tick; the
            // newest one wins (a set, or a CidNone clear), yielding current pending.
            var updates = b.QueuedCommandUpdates;
            for (int i = bot.QcuConsumed; i < updates.Count; i++)
            {
                var u = updates[i];
                bot.CurrentQueued = u.HasPending
                    ? new QueuedCommandState(u.CommandId, u.TargetType, u.TargetId)
                    : null;
            }
            bot.QcuConsumed = updates.Count;
            pending = bot.CurrentQueued;
        }
        else
        {
            return;   // no wire peer yet (mid-upgrade); nothing to read
        }

        // After the server clears a pending order (our accept / a reject /
        // auto-reject) it sends a CidNone update; reset the accepted bookmark so
        // a later, same-commandId order is seen as fresh (DecideAcceptTranslate).
        if (pending is null)
            bot.AcceptedQueuedId = OrderChange.CidNone;

        var d = DecideAcceptTranslate(pending, bot.AcceptedQueuedId, bot.OrderedTargetId);

        if (d.ShouldSendAccept && pending is { } pq)
        {
            short shipId = ShipIdOf(bot);
            if (bot.Bot is { } bb)
                bb.SendOrderChange(shipId, pq.CommandId, OrderChange.CmdAccepted, pq.TargetType, pq.TargetId);
            else if (bot.Cs is { } cc)
                cc.SendOrderChange(shipId, pq.CommandId, OrderChange.CmdAccepted, pq.TargetType, pq.TargetId);
            bot.AcceptedQueuedId = pq.CommandId;
            Console.WriteLine($"[p431] bot {bot.Name} (ship {shipId}) AUTO-ACCEPTED queued order cmdID={pq.CommandId} target={pq.TargetType}#{pq.TargetId} — MCP bot obeys immediately (real ORDER_CHANGE CmdAccepted)");
        }

        if (d.OrderedTarget != bot.OrderedTargetId)
        {
            bool wasOrdered = bot.OrderedTargetId is not null;
            bot.OrderedTargetId = d.OrderedTarget;
            if (bot.OrderedTargetId is not null && !wasOrdered)
                Console.WriteLine($"[p431] bot {bot.Name} adopted commanded ship-target #{bot.OrderedTargetId} — switching to combat to engage THAT ship");

            if (bot.OrderedTargetId is not null)
            {
                // An ordered bot fights. An idle/weave BotClass has no world to
                // engage with, so upgrade it to a real combat ConnectionSession
                // (a fresh client, seated+launched like any spawned combat bot).
                if (bot.Bot is not null)
                    UpgradeToCombat(bot);
                bot.Behavior = "combat";
            }
        }
    }

    /// <summary>
    /// P431 — the pure accept-and-translate decision for ONE bot tick, factored
    /// out of the Loop so the whole order lifecycle is provable with a synthetic
    /// pending <see cref="QueuedCommandState"/> and no server. A
    /// <see cref="QueuedCommandState"/> is the real pending order the wire
    /// delivers (<c>null</c> = none). <paramref name="acceptedQueuedId"/> is the
    /// command id this bot has already accepted (avoids a double-accept while
    /// the server is clearing the slot). Returns whether to send the real ACCEPT
    /// this tick and which enemy ship (if any) the bot is now committed to
    /// engage. An adopted order is PERSISTED (not dropped on a transient
    /// cross-side visibility gap) until a fresh order supersedes it.
    /// </summary>
    public static OrderDecision DecideAcceptTranslate(
        QueuedCommandState? pending,
        short acceptedQueuedId,
        short? currentOrderedTarget)
    {
        if (pending is not { } pq)
            // No pending order — nothing to accept. Keep the current ordered target
            // even if it is momentarily not visible in the bot's world: cross-side
            // visibility is unreliable (the documented P429 reality), so a temporary
            // gap must not be mistaken for a dead target and drop a live order.
            return new OrderDecision(false, currentOrderedTarget);

        bool isFresh = pq.CommandId != OrderChange.CidNone && acceptedQueuedId != pq.CommandId;

        if (pq.TargetType == IgcObjectType.Ship && pq.TargetId >= 0)
        {
            // A ship-target order IS the engage command: the bot must engage THAT
            // named ship, never "nearest enemy". A fresh one supersedes any prior
            // target and must be accepted; an already-accepted one is the ongoing
            // engagement (persisted — see the no-pending case above).
            return isFresh
                ? new OrderDecision(true, pq.TargetId)
                : new OrderDecision(false, currentOrderedTarget);
        }

        // A non-ship / targetless pending order (OT_invalid): the queued-order
        // wire carries NO steerable position, so there is no goto/waypoint
        // semantic to translate — accept it, but no ship-target is adopted.
        // (See docs/reports/P431_MCP_BOTS_ACCEPT_TEAM_ORDERS.md for the real
        // order-type findings.)
        return new OrderDecision(isFresh, currentOrderedTarget);
    }

    /// <summary>
    /// P431 — upgrade an idle/weave <see cref="BotClass"/> bot to a real combat
    /// <see cref="ConnectionSession"/> so it can genuinely obey a ship-target
    /// order (a BotClass has no ClientWorld, hence no way to navigate to or
    /// engage a named enemy). Seats + launches a fresh combat client on the
    /// bot's side with its original loadout, then swaps the peer over. A fresh
    /// unique name is used (server names are unique; the old peer is disposed
    /// only after the new one is fully seated). Failure is logged and returns
    /// false — the bot stays as-is (order accepted, not physically obeyed), a
    /// real degraded state never silently glossed over.
    /// </summary>
    private bool UpgradeToCombat(ActiveBot bot)
    {
        if (bot.Bot is not { } old) return false;
        string name = NextName();
        var parts = bot.Parts ?? Catalog.DefaultParts();
        try
        {
            var cs = SeatCombatClient(name, bot.Side, bot.HullId, parts, out short shipId, out short actualSide, out var upGranted, out var upWarning);
            try { DisposePeer(old); } catch { }   // free the old plain-client peer now that the combat one is real
            bot.Bot = null;
            bot.Cs = cs;
            bot.ShipId = shipId;
            bot.Side = actualSide;
            bot.Launched = true;
            bot.Granted = upGranted;
            bot.GrantWarning = upWarning;
            Console.WriteLine($"[p431] upgraded idle bot '{bot.Name}' -> combat client '{name}' (ship {shipId}, side {actualSide}) to obey its accepted order");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[p431] upgrade of idle bot {bot.Name} to combat failed: {ex.Message} — order accepted but not physically obeyed");
            return false;
        }
    }

    // ── Combat behavior (P429 — faithful port of vizfight's P420 choreography) ──
    //
    // Every helper below is a direct port of the equivalent in
    // tools/vizfight/Program.cs (attached as `[vz]`): the rendezvous routing
    // (AttachEnemyFlyTo / EnsureEnemyPath / EnemyNextHop / TryBuildWarpDestMap /
    // TryFindWarpTo) and the engagement (EngageEnemy / LoiterEnemy). A combat bot
    // is driven as its own ConnectionSession `Cs`, reading its own world for its
    // pose, co-located cross-side ships and warp geometry — exactly how vizfight
    // drives its opposing contingent.

    /// <summary>[vz EnemyCluster] The cluster the bot's own client is in (its own
    /// ship's sector, re-stamped by the server's SET_CLUSTER per transit).</summary>
    private static short CsSector(ConnectionSession cs)
    {
        if (!DecisionSnapshotEnabled)
        {
            // P636c "off" — P635 live read under cs.Gate.
            lock (cs.Gate)
                if (cs.World.MyShipId is { } me2 && cs.World.Ships.TryGetValue(me2, out var e2))
                    return e2.SectorId;
            return -1;
        }
        // P636 — read the lock-free decision snapshot instead of taking the world
        // gate: a decision pass no longer waits behind the receive thread's bulk
        // ship application under cs.Gate (the P632/P633 transit collapse).
        var snap = SnapshotFor(cs);
        if (snap.MyShipId is { } me && DecisionSnapshot.TryFind(snap.Ships, me, out var e))
            return e.SectorId;
        return -1;
    }

    /// <summary>[vz TryGetEnemyPose] The bot's LIVE pose from ITS OWN ClientWorld —
    /// P636 now from the lock-free decision snapshot (no gate acquisition).</summary>
    private static bool TryCsPose(ConnectionSession cs, short shipId, out Vector3 pos, out Orientation orient)
    {
        if (!DecisionSnapshotEnabled)
        {
            // P636c "off" — the pre-P636 P635 read: the live entity's Latest
            // pose under cs.Gate (fresh to the last applied update).
            lock (cs.Gate)
            {
                if (cs.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
                {
                    pos = lu.Position;
                    orient = lu.Orientation;
                    return true;
                }
            }
            pos = Vector3.Zero;
            orient = Orientation.Identity;
            return false;
        }
        var snap = SnapshotFor(cs);
        if (DecisionSnapshot.TryFind(snap.Ships, shipId, out var e2) && e2.HasLatest)
        {
            pos = e2.Position;
            orient = e2.MaterializeOrientation();
            return true;
        }
        pos = Vector3.Zero;
        orient = Orientation.Identity;
        return false;
    }

    /// <summary>
    /// P636c — a FRESH, lock-free pose for <paramref name="shipId"/>: reads the
    /// decision snapshot (no gate) but dead-reckons the frozen pose forward to
    /// the reader's "now" by the real elapsed time since the snapshot was
    /// published (<c>vel * age</c>). This restores pre-P636 aim freshness — the
    /// engaged target's (and the bot's own) position the lead/fire law consumes —
    /// without re-acquiring <c>cs.Gate</c> (the P636 lock-free benefit is kept:
    /// scans still run on the raw snapshot; only the aim-critical poses are
    /// extrapolated). Returns false exactly when <see cref="TryCsPose"/> would.
    /// </summary>
    private static bool TryCsFreshPose(ConnectionSession cs, short shipId, out Vector3 pos, out Vector3 vel, out Orientation orient)
    {
        if (!DecisionSnapshotEnabled)
        {
            lock (cs.Gate)
            {
                if (cs.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
                {
                    pos = lu.Position;
                    vel = lu.Velocity;
                    orient = lu.Orientation;
                    return true;
                }
            }
            pos = vel = Vector3.Zero;
            orient = Orientation.Identity;
            return false;
        }
        var snap = SnapshotFor(cs);
        if (DecisionSnapshot.TryFind(snap.Ships, shipId, out var fe) && fe.HasLatest)
        {
            if (SnapshotDeadReckonEnabled)
            {
                double ageSec = ((_frameSession == cs ? _frameNow : Environment.TickCount64) - snap.PublishedTick64) / 1000.0;
                if (ageSec < 0) ageSec = 0;
                pos = DecisionSnapshot.DeadReckonPosition(fe, ageSec * 1000.0);
            }
            else
            {
                pos = fe.Position;   // P636c probe — raw frozen snapshot pose (unfixed)
            }
            vel = fe.Velocity;
            orient = SnapshotDeadReckonEnabled
                ? DecisionSnapshot.DeadReckonOrientation(fe, Math.Max(0, (_frameSession == cs ? _frameNow : Environment.TickCount64) - snap.PublishedTick64))
                : fe.MaterializeOrientation();
            return true;
        }
        pos = vel = Vector3.Zero;
        orient = Orientation.Identity;
        return false;
    }

    /// <summary>P636c — record one aim's snapshot age (ms) into the rolling window.</summary>
    private static void RecordSnapAge(long publishedTick64)
    {
        if (!DecisionSnapshotEnabled) return;
        double age = System.Environment.TickCount64 - publishedTick64;
        if (age < 0) age = 0;
        RecordSnapshotAgeSample(age);
    }

    private static void RecordSnapshotAgeSample(double age)
    {
        lock (SnapAgeGate)
        {
            _snapAgesMs[_snapAgeNext] = age;
            _snapAgeNext = (_snapAgeNext + 1) % MaxSnapAgeSamples;
            _snapAgeCount = Math.Min(MaxSnapAgeSamples, _snapAgeCount + 1);
        }
    }

    /// <summary>P636c — percentile summary of the sampled snapshot ages (ms).</summary>
    private static (double Mean, double P95, double Max) SnapAgeSummary()
    {
        lock (SnapAgeGate)
        {
            if (_snapAgeCount == 0) return (0, 0, 0);
            var a = _snapAgesMs.Take(_snapAgeCount).ToArray();
            System.Array.Sort(a);
            int n = a.Length;
            double mean = 0; foreach (var v in a) mean += v; mean /= n;
            double p95 = a[(int)(n * 0.95)];
            return (mean, p95, a[n - 1]);
        }
    }

    private static Vector3 Vec3(float[] v) => v is { Length: >= 3 } ? new Vector3(v[0], v[1], v[2]) : Vector3.Zero;

    /// <summary>[vz TryBuildWarpDestMap] warpId → destination-cluster map from the
    /// bot's real world (each warp's <c>DestinationId</c> names its partner warp;
    /// the partner's cluster is the destination sector). P636 — reads the decision
    /// snapshot's warps (already resolved partner→cluster), no gate.</summary>
    private static bool TryBuildWarpDestMap(ConnectionSession cs, out Dictionary<short, short> map)
    {
        map = new Dictionary<short, short>();
        if (!DecisionSnapshotEnabled)
        {
            // P636c "off" — P635 live read under cs.Gate.
            lock (cs.Gate)
            {
                foreach (var w in cs.World.Warps.Values)
                    if (cs.World.Warps.TryGetValue(w.DestinationId, out var partner))
                        map[w.WarpId] = partner.ClusterId;
            }
            return map.Count > 0;
        }
        var snap = SnapshotFor(cs);
        foreach (var w in snap.Warps)
            if (w.DestClusterId >= 0)
                map[w.WarpId] = w.DestClusterId;
        return map.Count > 0;
    }

    /// <summary>[vz TryFindWarpTo] The position AND wire radius of the warp in
    /// cluster <paramref name="cur"/> that leads to cluster <paramref name="next"/>.
    /// The radius is the authoritative <c>DataWarpIGC.Radius</c> the client sees —
    /// the P496 warp-approach law needs it (with the ship radius) to know where
    /// the aleph's transit sphere actually is, so it can fly THROUGH it instead
    /// of halting 90+ units short (P490's board-hull "park at the aleph"). P636 —
    /// reads the decision snapshot's warps, no gate.</summary>
    private static bool TryFindWarpTo(ConnectionSession cs, short cur, short next, IReadOnlyDictionary<short, short> warpDest, out Vector3 pos, out float radius)
    {
        if (!DecisionSnapshotEnabled)
        {
            // P636c "off" — P635 live read under cs.Gate.
            lock (cs.Gate)
            {
                foreach (var w in cs.World.Warps.Values)
                {
                    if (w.ClusterId != cur) continue;
                    if (warpDest.TryGetValue(w.WarpId, out var d) && d == next)
                    {
                        pos = Vec3(w.Position);
                        radius = w.Radius;
                        return true;
                    }
                }
            }
            pos = Vector3.Zero;
            radius = 0f;
            return false;
        }
        var snap = SnapshotFor(cs);
        foreach (var w in snap.Warps)
        {
            if (w.ClusterId != cur) continue;
            if (warpDest.TryGetValue(w.WarpId, out var d) && d == next)
            {
                pos = w.Position;
                radius = w.Radius;
                return true;
            }
        }
        pos = Vector3.Zero;
        radius = 0f;
        return false;
    }

    /// <summary>P637 — resolve the warp for this bot's next hop and fly it,
    /// REFRESHING the warp-destination map on demand. The previous sticky cache
    /// (<c>bot.WarpDest ??= …</c>) snapshotted a PARTIAL map — a bot that started
    /// routing before its client's EXPORT warp batch (OT_warp, server
    /// Server.Lobby.cs:3473 sends ALL warps on export) landed was permanently
    /// blind to cross-country warps and could never fly its own route to the
    /// enemy home (warpFound=false → loiter forever). Rebuilding the ~map-size
    /// dict per consultation is cheap and always reflects the client's current
    /// warp knowledge. Returns true (and flew the leg) only when the warp is
    /// found; false → the caller loiters.</summary>
    private static bool TransitWarp(ConnectionSession cs, ActiveBot bot, short cur, short next)
    {
        if (TryBuildWarpDestMap(cs, out var m) && TryFindWarpTo(cs, cur, next, m, out var wp, out _))
        {
            bot.WarpDest = m;   // refresh — never keep a stale partial snapshot
            FlyWarpLeg(bot, wp);   // P560 — full-throttle fly-through (Pursue)
            return true;
        }
        return false;
    }

    /// <summary>P496 — the warp-hop approach law: a warp has NO "arrival" short
    /// of its transit sphere (the aleph transit fires at
    /// <c>|ship−warp| ≤ warp.Radius + ship.Radius</c>; the sector pass at
    /// <c>Sector.ResolveWarpCollisions</c> builds that threshold from the raw
    /// wire radius — P484 measured ≈110 u here). So a bot heading for a warp
    /// must FLY THROUGH the aleph, never be commanded to stop before it. Returns
    /// the (arrival, brake) pair for <see cref="CombatFlyTo"/> that places the
    /// throttle ramp INSIDE the transit sphere: the bot is still thrusting
    /// FORWARD when it crosses the boundary and only eases off at the centre —
    /// which is exactly what a slow board hull (Hvy Troop Transport id 26,
    /// accel 15.0) needs to enter the volume instead of parking 90 u outside it
    /// (P490's "park at the aleph", blamed there on hull maneuverability but
    /// actually commanded by the old fixed arrival=200 &gt; the ~115 u sphere).</summary>
    internal static (float arrival, float brake) WarpApproachParams(float warpRadius, float shipRadius)
    {
        float threshold = warpRadius + shipRadius;            // the transit sphere
        return (arrival: 0f, brake: Math.Max(1f, threshold)); // stop only at the centre
    }

    /// <summary>P634 — how long a bot waits (harness clock, sec) before
    /// re-attempting routing once the cluster graph is found missing, so it
    /// does not hammer EnsureCombatPath every tick during a slow world sync.</summary>
    private const double WorldSyncBackoffSec = 2.0;

    /// <summary>[vz EnsureEnemyPath] Recompute the bot's cluster path only when
    /// needed: keep the existing one while it still covers the current cluster
    /// and the goal is unchanged, else re-BFS from the current cluster.
    /// P634 — when the cluster GRAPH hasn't landed yet (ClientWorld not
    /// WorldReady) the bot cannot route: instead of re-requesting every tick
    /// (the P633 ~224/390 home-bound hammer), it backs off
    /// <see cref="WorldSyncBackoffSec"/> and logs the wait ONCE, counting the
    /// retries for the live measurement.</summary>
    private static void EnsureCombatPath(ActiveBot bot, short cur, short goal, double now)
    {
        if (bot.PathGoal == goal && bot.Path.Count > 0 && bot.Path.Contains(cur)) return;
        // P634 — backoff window: don't re-attempt while we're still waiting on
        // the world sync.
        if (now < bot.WorldNotReadyUntil) return;
        lock (bot.Cs!.Gate)
        {
            var g = bot.Cs.World.Graph;
            if (g is null)
            {
                bot.WorldNotReadyAttempts++;
                if (bot.WorldNotReadyAttempts == 1)
                    Console.WriteLine($"[p634] bot {bot.Name} (side {bot.Side}) awaiting world sync — no cluster graph yet (ClientWorld not WorldReady); backing off {WorldSyncBackoffSec}s");
                bot.WorldNotReadyUntil = now + WorldSyncBackoffSec;
                return;
            }
            if (bot.WorldReadyAt < 0) bot.WorldReadyAt = now;   // latch first readiness
            bot.Path = RoutePathSuffix(bot, cur, goal)
                ?? (bot.UseStaging && !IsStageReleased(bot.StagePhase) && goal == bot.StagingSector
                    && bot.TargetSector is { } rendezvous && rendezvous != cur
                    ? g.FindShortestPathAvoiding(cur, goal, rendezvous) : g.FindShortestPath(cur, goal));
        }
        bot.PathStart = cur;
        bot.PathGoal = goal;
    }

    /// <summary>
    /// P635 — when this bot is on a multi-route wing (<see cref="ActiveBot.RoutePath"/>
    /// set) and the current cluster lies on its assigned route to <paramref name="goal"/>,
    /// return the route suffix from <paramref name="cur"/> onward so the wing keeps its
    /// distinct entry aleph instead of reverting to the shared shortest path. Returns null
    /// when the bot is un-routed or off-route (caller falls back to FindShortestPath).
    /// </summary>
    private static List<short>? RoutePathSuffix(ActiveBot bot, short cur, short goal)
    {
        if (bot.RoutePath is not { Count: > 0 } rp) return null;
        int end = rp.IndexOf(goal);
        int idx = rp.IndexOf(cur);
        if (idx < 0 || end < idx) return null;
        return rp.Skip(idx).Take(end - idx + 1).ToList();
    }

    /// <summary>[vz EnemyNextHop] The next cluster on the path after
    /// <paramref name="cur"/>; <c>cur</c> itself when it is the goal.</summary>
    private static short CombatNextHop(ActiveBot bot, short cur)
    {
        for (int i = 1; i < bot.Path.Count; i++)
            if (bot.Path[i - 1] == cur) return bot.Path[i];
        return cur;
    }

    /// <summary>[vz AttachEnemyFlyTo] Face-and-thrust autopilot law (the same
    /// <c>Ship.SteerToward</c> law vizfight's FlyTo uses), reading the bot's pose
    /// from ITS OWN ClientWorld. Fires mounted weapons when <paramref name="stateM"/>
    /// has AllWeapons.
    /// <para><b>P489</b> — before the straight-line fly, the bot consults the
    /// SHARED native dodge law (<see cref="Allegiance.Core.Sim.ShipDodgeLaw"/>,
    /// the same module the server NPC autopilot's <c>Ship.TryComputeDodgeGoal</c>
    /// calls) fed from its own <c>ClientWorld</c>: asteroids, stations and known
    /// same-side ships in its cluster. When an imminent collision is found the
    /// bot flies the dodge goal instead of the obstacle-blind bee-line — the
    /// P484 "park at the aleph" fix (a straight warp-approach line into Alderan's
    /// dense mining-hub field was being bounced/destroyed before reaching the
    /// transit volume).</para></summary>
    private static void CombatFlyTo(ActiveBot bot, Vector3 target, float arrival, float brake, int stateM)
    {
        using var frameScope = new DecisionFrameScope(bot.Cs);
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out var pos, out var orient)) return;
        // P489 — dodge pre-empts the straight-line goal, exactly like the server
        // NPC autopilot's universal Stage-1 check (Dodge runs before every goal
        // branch). No imminent collision → fall through to the unchanged law.
        if (TryDodgeGoal(cs, bot, pos, out var dodgeGoal))
        {
            target = dodgeGoal;
            // P553 — when the dodge fires during COMBAT the dodge OWNS steering
            // and throttle for this tick, and weapon fire is suppressed — native's
            // combat drone "if (!bDodge)" (it doesn't thrust at / shoot the
            // target while dodging, shipIGC.cpp:2410) and the dodgeCooldown
            // no-fire rule (shipIGC.cpp:2485-2490). We keep firing only when NOT
            // dodging ("keep firing only if native does"); the bot still boosts.
            stateM = CombatStateWhileDodging(stateM);
            // P553 — telemetry: record every dodge-law fire (non-behavioural) so a
            // live proof can correlate a collision death with active dodging.
            lock (bot.DodgeGate)
            {
                bot.DodgeFires.Add(new BotDodgeFire(bot.ShipId, CsSector(cs),
                    (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds, pos, dodgeGoal));
                if (bot.DodgeFires.Count > MaxDodgeTraces) bot.DodgeFires.RemoveAt(0);
            }
        }
        // P539d — cross-side approach avoidance: steer around any ship (incl.
        // cross-side) lying in the forward path cone so the approach never rams —
        // the same cone test the friendly-fire gate uses. The bot's own actively-
        // engaged target is excluded (the P539 stand-off ring keeps that at range).
        {
            short avoidCur = CsSector(cs);
            List<(Vector3 Pos, short Id)>? pathShips = null;
            lock (cs.Gate)
            {
                if (avoidCur >= 0)
                    foreach (var kv in cs.World.Ships)
                    {
                        if (kv.Key == bot.ShipId) continue;
                        if (kv.Value.SectorId != avoidCur) continue;
                        if (kv.Value.Latest is not { } lu) continue;
                        (pathShips ??= new List<(Vector3, short)>()).Add((lu.Position, kv.Key));
                    }
            }
            if (pathShips is not null && AvoidShipsInPath(pos, target, AvoidHorizon, AvoidCosHalfAngle,
                    AvoidClearancePad + HullRadius(ActualHullId(bot)) + 6f, pathShips, bot.TargetId, out var avoidGoal))
                target = avoidGoal;
        }
        var toTarget = target - pos;
        float dist = toTarget.Length();
        if (dist <= 1e-3f) { cs.CommandControls(ControlData.Neutral, stateM); return; }
        var dir = toTarget / dist;
        var body = orient.TimesInverse(Vector3.CrossProduct(orient.GetForward(), dir));
        float yaw = Math.Clamp(2.0f * body.Y, -1f, 1f);
        float pitch = Math.Clamp(2.0f * -body.X, -1f, 1f);
        float distFactor = dist >= brake ? 1f : Math.Clamp((dist - arrival) / (brake - arrival), 0f, 1f);
        float align = Math.Clamp(-orient.TimesInverse(dir).Z, 0f, 1f);
        float throttle = Math.Clamp(2f * distFactor * align - 1f, -1f, 1f);
        cs.CommandControls(new ControlData(yaw, pitch, 0f, throttle), stateM);
    }

    /// <summary>P489/P524 — feed the SHARED native dodge law
    /// (<see cref="Allegiance.ClientCore.Flight.ClientAutopilotDodge"/>) from the
    /// bot's own <c>ClientWorld</c>: the mover's pose (position here, velocity
    /// read live) and hull, plus the asteroids / stations / known ships in its
    /// cluster. This is the SAME canonical feed the MonoGame player <c>G</c>
    /// autopilot uses (P524) — no separate copy — so the bots and the player
    /// autopilot steer with byte-identical obstacle avoidance. Returns a dodge
    /// goal when an imminent collision is found (mirrors the server NPC
    /// autopilot), else <c>false</c> — in which case the caller flies its normal
    /// straight-line goal unchanged. No <see cref="Allegiance.ClientCore.Flight.ClientAutopilotDodge.SkipGoal"/>
    /// is passed, preserving the P489 bot behaviour exactly.</summary>
    private static bool TryDodgeGoal(ConnectionSession cs, ActiveBot bot, Vector3 myPos, out Vector3 goal)
    {
        goal = default;
        short cur = CsSector(cs);
        if (cur < 0) return false;
        Vector3 myVel;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is not { } mu) return false;
            myVel = mu.Velocity;
        }
        var core = Catalog.RepresentativeCore();
        if (core.HullTypes.TryGetValue(ActualHullId(bot), out var hull) && hull.Mass > 0f && hull.Thrust > 0f)
        {
            // P497 — a board-capable hull does not dodge the station it is flying
            // into to board (native boards by flying into the bay, not by evading
            // the base); the shared feed drops every station for such a hull.
            bool dodgeStations = !hull.HasCapability(DataHullTypeIGC.HabmBoard);
            lock (cs.Gate)
            {
                return Allegiance.ClientCore.Flight.ClientAutopilotDodge.TryComputeDodgeGoal(
                    cs.World, bot.ShipId, bot.Side, myPos, myVel,
                    new Allegiance.ClientCore.Flight.ClientAutopilotDodge.MoverHull(hull.Length, hull.Thrust, hull.Mass),
                    dodgeStations,
                    id => core.HullTypes.TryGetValue(id, out var h) ? h.Length : (float?)null,
                    tid => core.StationTypes.TryGetValue(tid, out var t) && t.Radius > 0f ? t.Radius : (float?)null,
                    skipGoal: null,
                    out goal);
            }
        }
        return false;
    }

    /// <summary>[vz LoiterEnemy] A bounded in-place weave for a bot with nothing
    /// specific to fly to (transit stall, waiting on combat, no co-located enemy).</summary>
    private static void LoiterCombat(ActiveBot bot, double now)
    {
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out _, out _)) return;
        double yaw = Math.Sin(now * bot.WeaveFreq + bot.WeavePhase) * bot.WeaveAmp;
        double throttle = 0.5 + 0.2 * Math.Sin(now * bot.WeaveFreq * 0.7 + bot.WeavePhase * 1.3);
        cs.CommandControls(new ControlData((float)yaw, 0f, 0f, (float)throttle), 0);
    }

    /// <summary>[vz EngageEnemy] Pick the nearest DIFFERENT-side ship co-located in
    /// the bot's OWN cluster, point itself at it via a real cross-side
    /// ORDER_CHANGE target acquisition, and hold AllWeapons with periodic
    /// afterburner-boost / evasive-cloak beats — the full real loadout. Sets the
    /// bot's <see cref="ActiveBot.TargetId"/> so <c>list_active_bots</c> can show
    /// the engagement. Loiters when no enemy is visible. P431: a commanded
    /// ship-target order (an accepted teammate order) TAKES PRECEDENCE over
    /// nearest-enemy — the bot engages THAT specific ship, never the nearest
    /// one, while it is alive.</summary>
    private void EngageCombat(ActiveBot bot, double now, PreparedEngagement? prep, bool? legacyNearest = null)
    {
        using var frameScope = new DecisionFrameScope(bot.Cs);
        var cs = bot.Cs!;
        if (!IsParticipating(bot)) return;
        if (!TryCsPose(cs, bot.ShipId, out var myPos, out _)) return;
        short myCluster = CsSector(cs);

        // P431 — outside a temporary defence engagement, the commanded ship-target
        // wins over the coordinated choice. Defence preserves the order for resumption. While
        // it is alive and co-located, engage exactly it; if it is not currently
        // engageable (cross-side visibility can be intermittent — P429) or not yet
        // co-located, loiter (committed to this order, attacking no one else). The
        // order is only released by a NEW superseding order (DecideAcceptTranslate),
        // not by a transient visibility gap.
        ClearInvalidOrderedTarget(bot, cs);
        if (!bot.DefendingInPlace && bot.OrderedTargetId is { } ordered)
        {
            bool engageable; short osector;
            lock (cs.Gate)
            {
                bool found = cs.World.Ships.TryGetValue(ordered, out var se);
                engageable = found && !(se?.IsNotVisible ?? true) && (se?.SideId ?? bot.Side) != bot.Side && se?.Latest is not null;
                osector = found ? se!.SectorId : (short)-1;
            }
            bot.TargetId = ordered;
            if (engageable && osector == myCluster)
            {
                // Commanded focus-fire: no anti-swarm cap (the order IS the
                // concentration), but report its engager count + a flat rank 0
                // (no extra approach offset) so P431 behaviour is unchanged.
                int orderedEngagers = _tickEngagers.TryGetValue(ordered, out var oe) ? oe : 0;
                EngageShipAt(bot, cs, ordered, now, orderedEngagers, engagerRank: 0);
            }
            else
            {
                LoiterCombat(bot, now);   // committed to this order; not engageable right now
            }
            return;
        }

        if (string.Equals(bot.Role, "bomber", StringComparison.OrdinalIgnoreCase)
            && (bot.Arena || bot.TargetSector is null || bot.TargetSector == myCluster))
        {
            short? station;
            lock (cs.Gate) station = FinaleStationTarget(bot, myCluster)
                ?? PickEnemyStation(cs.World.Stations.Values, bot.Side, myCluster, myPos);
            bool attacked = HasVisibleAttacker(bot, cs, myCluster, now);
            if (station is { } id && (!attacked || bot.FinaleBomberActive))
            {
                bot.TargetId = id;
                EngageShipAt(bot, cs, id, now, 0, 0, stationTarget: true);
                return;
            }
        }

        if (PickDefenseDutyTarget(bot, myCluster, myPos) is { } duty)
        {
            bot.TargetId = duty;
            int count = (_tickEngagers.TryGetValue(duty, out var engaged) ? engaged : 0) + 1;
            _tickEngagers[duty] = count;
            EngageShipAt(bot, cs, duty, now, count, count);
            return;
        }

        // P622 measurement switch (DEFAULT OFF): when ALLEGIANCE_P622_LEGACY_NEAREST
        // is set, revert to the pre-P622 per-bot nearest-enemy pick so the P622
        // live harness can measure the swarm before/after with the same binary.
        if (legacyNearest ?? LegacyNearestFlag)
        {
            short leg = NearestEnemyFallback(bot, cs, myPos, myCluster);
            if (leg < 0) { bot.TargetId = null; LoiterCombat(bot, now); return; }
            bot.TargetId = leg;
            int lc = (_tickEngagers.TryGetValue(leg, out var le) ? le : 0) + 1;
            _tickEngagers[leg] = lc;
            EngageShipAt(bot, cs, leg, now, lc, engagerRank: lc);
            return;
        }

        // P622 — coordinated spreading + priority: replace the per-bot
        // nearest-enemy pick (the 195-on-one-miner swarm) with a shared choice
        // that respects engager caps, threat priority, and the lone-miner
        // detachment-vs-hold rule. The P558 native attack law still flies the
        // bot once a target is chosen.
        var choice = ChooseCombatTarget(bot, cs, myPos, myCluster, prep);
        if (choice.Hold || choice.TargetId is not { } best)
        {
            bot.TargetId = null;
            LoiterCombat(bot, now);      // hold/loiter at the staging point
            return;
        }
        lock (cs.Gate)
            if (!cs.World.Ships.TryGetValue(best, out var current) || current.IsNotVisible
                || current.SideId == bot.Side || current.SectorId != myCluster
                || cs.World.MyShipId != bot.ShipId
                || !cs.World.Ships.TryGetValue(bot.ShipId, out var ownCurrent) || ownCurrent.SectorId != myCluster)
            { bot.TargetId = null; LoiterCombat(bot, now); return; }
        if (!IsParticipating(bot)) return;
        bot.TargetId = best;
        // Register this engager in the current pass (single Loop thread).
        int newCount = (_tickEngagers.TryGetValue(best, out var prev) ? prev : 0) + 1;
        _tickEngagers[best] = newCount;
        EngageShipAt(bot, cs, best, now, newCount, engagerRank: newCount);
    }

    /// <summary>P622 — the pre-P622 per-bot NEAREST different-side co-located
    /// visible enemy pick (what caused the 195-on-one-miner swarm). Retained only
    /// behind the ALLEGIANCE_P622_LEGACY_NEAREST measurement switch so the live
    /// harness can quantify before/after with the same binary. -1 = no enemy.</summary>
    private static short NearestEnemyFallback(ActiveBot bot, ConnectionSession cs, Vector3 myPos, short myCluster)
    {
        short best = -1; float bestD = float.MaxValue;
        lock (cs.Gate)
        {
            foreach (var ship in cs.World.Ships.Values)
            {
                if (ship.ShipId == bot.ShipId || ship.IsNotVisible) continue;
                if (ship.SideId == bot.Side) continue;      // a DIFFERENT side only
                if (ship.SectorId != myCluster) continue;   // co-located
                if (ship.Latest is not { } lu) continue;
                if (IgnoreMinerTargets && CombatTargetSelector.ClassifyTargetId(ship.HullId) == BotTargetClass.MinerDrone) continue;   // showcase: never target miners/drones
                float d = (lu.Position - myPos).Length();
                if (d < bestD) { bestD = d; best = ship.ShipId; }
            }
        }
        return best;
    }

    /// <summary>P622 — build this bot's co-located different-side VISIBLE enemy
    /// candidates from its own ClientWorld and run the pure coordinated selector
    /// over them (engager caps, threat priority, lone-miner detachment/hold).
    /// Reading the current-pass engager counts (_tickEngagers) gives the
    /// anti-swarm view; threats are enemies that recently damaged THIS bot
    /// (see <see cref="ActiveBot.RecentAttackers"/>) or a station-threatening
    /// bomber/capital while a friendly station is co-located. Runs on the single
    /// Loop thread.</summary>
    private BotTargetChoice ChooseCombatTarget(ActiveBot bot, ConnectionSession cs, Vector3 myPos, short myCluster, PreparedEngagement? prep)
    {
        double now = Now();
        // P633 — use the candidates the parallel pre-pass (Loop PHASE A) already
        // gathered for this bot, falling back to an on-demand gather only when
        // prep is null (e.g. a bot promoted to combat mid-pass). The gather logic
        // is byte-for-byte the same either way → the coordinated choice is
        // deterministic and P622-equivalent (see the two-phase equivalence test).
        List<BotTargetCandidate> candidates;
        bool friendlyStation;
        if (prep is { } p)
        {
            candidates = p.Candidates;
            friendlyStation = p.FriendlyStation;
        }
        else
        {
            candidates = new List<BotTargetCandidate>();
            var recentAttackers = bot.RecentAttackers;
            friendlyStation = false;
            lock (cs.Gate)
            {
                foreach (var st in cs.World.Stations.Values)
                    if (st.SideId == bot.Side && st.ClusterId == myCluster) { friendlyStation = true; break; }

                foreach (var ship in cs.World.Ships.Values)
                {
                    if (ship.ShipId == bot.ShipId || ship.IsNotVisible) continue;
                    if (ship.SideId == bot.Side) continue;      // a DIFFERENT side only
                    if (ship.SectorId != myCluster) continue;   // co-located
                    if (ship.Latest is not { } lu) continue;
                    float d = (lu.Position - myPos).Length();
                    var cls = CombatTargetSelector.ClassifyTargetId(ship.HullId);
                                        if (IgnoreMinerTargets && cls == BotTargetClass.MinerDrone) continue;   // showcase: never target miners/drones (Domingo 2026-10-13)
bool isThreat =
                        (recentAttackers.TryGetValue(ship.ShipId, out var at) && now - at < ThreatWindowSeconds)
                        || (friendlyStation && CombatTargetSelector.IsStationThreatening(cls));
                    candidates.Add(new BotTargetCandidate(ship.ShipId, lu.Position, cls, isThreat) { Distance = d });
                }
            }
        }
        if (string.Equals(bot.Role, "bomber", StringComparison.OrdinalIgnoreCase)
            && !bot.Arena && bot.TargetSector is { } destination && destination != myCluster)
            candidates = candidates.Where(c => bot.RecentAttackers.TryGetValue(c.Id, out var at)
                && now - at < ThreatWindowSeconds).ToList();
        var ctx = new BotSelectionContext(bot.ShipId, myCluster, 0, _tickEngagers);
        if (bot.DefendingInPlace && bot.DefenseTargets.Count > 0)
        {
            candidates = candidates.Where(c => bot.DefenseTargets.Contains(c.Id)).ToList();
            // Fill bomber/capital threat caps first, then other observed attackers.
            var stationThreats = candidates.Where(c => CombatTargetSelector.IsStationThreatening(c.Class)).ToList();
            var priority = CombatTargetSelector.PickBy(!CombatTargetSelector.SpreadFirst, stationThreats, ctx);
            if (priority is { } pick) return pick;
        }
        return CombatTargetSelector.Select(candidates, ctx);
    }

    /// <summary>P622 — count this side's LIVING combat bots currently co-located
    /// in <paramref name="myCluster"/> (the bot's own side, for the selector's
    /// context). Cheap; runs on the single Loop thread under _gate.</summary>

    /// <summary>[vz EngageEnemy's engagement body, P558-rewritten] Point the bot
    /// at <paramref name="targetId"/> via a real cross-side ORDER_CHANGE target
    /// acquisition (re-armed every ~3s) and run the NATIVE wingman ATTACK law
    /// (<c>shipIGC.cpp:2356-2604</c>) replacing the P539 stand-off ring:
    /// <list type="bullet">
    /// <item>Dodge owns the tick (P553, <c>if (!bDodge)</c>, shipIGC.cpp:2410).</item>
    /// <item>Turn to face the solved LEAD direction; the throttle follows the
    /// lead-angle law (<c>shipIGC.cpp:2474</c>).</item>
    /// <item>Distance bands keyed to the intercept time <c>t</c> vs the
    /// projectile lifespan set the forward/backward/afterburner buttons and
    /// the match-speed throttle (<c>shipIGC.cpp:2492-2571</c>).</item>
    /// <item>Fire gate: allWeapons when <c>angleToLead ≤ 0.10 rad</c> AND
    /// <c>t ≤ lifespan·0.95</c> (<c>shipIGC.cpp:2589-2602</c>).</item>
    /// </list>
    /// The spaced behaviour is native's (lead-time bands + back-off), so tech /
    /// hull / shield decide fights instead of a ring scaled to each side's own
    /// weapon range (the P556 root cause). Shared by the nearest-enemy and P431
    /// commanded-target paths.</summary>
    private static void EngageShipAt(ActiveBot bot, ConnectionSession cs, short targetId, double now,
        int engagerCount, int engagerRank, bool stationTarget = false)
    {
        using var frameScope = new DecisionFrameScope(cs);
        lock (cs.Gate)
        {
            if (cs.World.MyShipId != bot.ShipId || !cs.World.Ships.TryGetValue(bot.ShipId, out var ownLife)
                || ownLife.StationId >= 0) return;
            if (!stationTarget && (!cs.World.Ships.TryGetValue(targetId, out var targetLife)
                || targetLife.IsNotVisible || targetLife.SideId == bot.Side || targetLife.SectorId != ownLife.SectorId))
            { LoiterCombat(bot, now); return; }
        }
        if (now - bot.LastTargetAt > 3)
        {
            cs.SendOrderChange(bot.ShipId, OrderChange.CmdQueued, OrderChange.CmdCurrent, (stationTarget ? IgcObjectType.Station : IgcObjectType.Ship), targetId);
            bot.LastTargetAt = now;
        }
        if (!TryCsPose(cs, bot.ShipId, out var myPos0, out var orient0)) return;
        var myPos = myPos0;
        var orient = orient0;

        Vector3 targetPos, targetVel, myVel;
        short targetSector; short targetHullId;
        float stationRadius = 0;
        if (stationTarget)
        {
            lock (cs.Gate)
            {
                if (!cs.World.Stations.TryGetValue(targetId, out var station) || station.SideId == bot.Side
                    || station.ClusterId != CsSector(cs) || station.Position is not { Length: 3 } p
                    || !cs.World.Ships.TryGetValue(bot.ShipId, out var own) || own.Latest is not { } ownPose)
                { LoiterCombat(bot, now); return; }
                myPos = ownPose.Position;
                orient = ownPose.Orientation;
                myVel = ownPose.Velocity;
                targetPos = new Vector3(p[0], p[1], p[2]);
                targetVel = Vector3.Zero;
                targetSector = station.ClusterId;
                targetHullId = -1;
                if (Catalog.RepresentativeCore().StationTypes.TryGetValue(station.StationTypeId, out var type))
                    stationRadius = type.Radius;
            }
        }
        else if (!DecisionSnapshotEnabled)
        {
            // P636c "off" — P635: live target + own reads under cs.Gate.
            lock (cs.Gate)
            {
                if (!cs.World.Ships.TryGetValue(targetId, out var te) || te.Latest is not { } tlu)
                { LoiterCombat(bot, now); return; }
                targetPos = tlu.Position;
                targetVel = tlu.Velocity;
                targetSector = te.SectorId;
                targetHullId = te.HullId;
                if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me) || me.Latest is not { } mu)
                { LoiterCombat(bot, now); return; }
                myVel = mu.Velocity;
            }
            if (targetSector != CsSector(cs)) { LoiterCombat(bot, now); return; }  // co-location guard (P429 one-sided)
        }
        else
        {
            // P636c FIX — the aim-critical poses are read FRESH (dead-reckoned
            // forward to "now" by vel×age, lock-free) instead of the raw frozen
            // snapshot, so the lead/fire law consumes near-live geometry. The
            // lead solve (`deltaP = targetPos − myPos`, `deltaV = targetVel −
            // myVel`) and the fire gate (`angleToLead ≤ c_fMaxOffAngle`) are
            // exactly the inputs P636's ~25 Hz freeze corrupted: a 40 ms-old
            // target/own position at a closing speed of hundreds of u/s shifts
            // the intercept point by tens of units, so the nose never settles on
            // the lead and the bot fires far less (the 150v150 shot collapse
            // 20353 → 10679 that drops kills 29 → 11). Everything else (the
            // O(N) candidate scans, navigation) still reads the raw snapshot, so
            // the P636 no-gate benefit is fully kept — no cs.Gate is acquired.
            var esnap = SnapshotFor(cs);
            if (!TryCsFreshPose(cs, bot.ShipId, out var fmyPos, out myVel, out var fmyOrient)) return;
            myPos = fmyPos;
            orient = fmyOrient;
            if (!TryCsFreshPose(cs, targetId, out var ftPos, out targetVel, out _))
            { LoiterCombat(bot, now); return; }
            targetPos = ftPos;
            if (!(DecisionSnapshot.TryFind(esnap.Ships, targetId, out var te2)))
            { LoiterCombat(bot, now); return; }
            targetSector = te2.SectorId;
            targetHullId = te2.HullId;
            RecordSnapAge(esnap.PublishedTick64);
            if (targetSector != CsSector(cs)) { LoiterCombat(bot, now); return; }  // co-location guard (P429 one-sided)
        }

        // The ballistics of the bot's LEAD weapon (first mounted gun) from the
        // REAL part/projectile data — never a literal.
        float projSpeed = 0f, weaponRange = 0f, lifespan = 0f; bool abs = false;
        if (bot.WeaponPartId is { } wp) TryWeaponBallistics(wp, out projSpeed, out weaponRange, out lifespan, out abs);
        if (projSpeed <= 0f || lifespan <= 0f) { LoiterCombat(bot, now); return; }

        // P622 §4 — approach spacing: when several teammates engage the SAME
        // target, fan the approach OUT laterally (deterministic per-engager
        // orbit offset) so the fleet doesn't converge into one column and grind
        // against the rock/asteroid the target sits on (rock avoidance itself is
        // the shared TryDodgeGoal below). Applied ONLY while still closing from
        // outside the weapon band — within range we converge on the exact
        // position for accurate fire (P558's bands + lead are untouched).
        if (engagerCount > 1 && engagerRank > 0)
        {
            float distTo = (targetPos - myPos).Length();
            if (distTo > weaponRange * 0.9f)
                targetPos += CombatTargetSelector.EngagementOrbitOffset(engagerRank, targetPos - myPos, 45f);
        }

        // Lead aim: where the round actually intercepts the moving target
        // (native solveForLead → the intercept time `t` powers the bands).
        float targetR = stationTarget ? stationRadius : HullRadius(targetHullId);
        Vector3 aim = Vector3.Zero;
        bool haveLead = false;
        float t = 0f;
        if (projSpeed > 0f) haveLead = TrySolveLead(myPos, myVel, targetPos, targetVel, targetR, projSpeed, abs, out aim, out t);
        Vector3 rawAim = targetPos - myPos;
        float rawLen = rawAim.Length();
        if (rawLen > 1e-4f) rawAim /= rawLen; else rawAim = orient.GetForward();
        Vector3 aimDir = haveLead ? aim : rawAim;    // fall back to raw bearing when no intercept exists

        // ── P558 — native wingman ATTACK branch (shipIGC.cpp:2356-2604) ─────
        // Dodge owns the tick: a dodging bot flies its dodge goal and fires
        // nothing (P553; shipIGC.cpp:2410, and the dodgeCooldown no-fire rule
        // at :2485-2490). CombatFlyTo's dodge branch clears AllWeapons.
        if (TryDodgeGoal(cs, bot, myPos, out var dodgeGoal))
        {
            CombatFlyTo(bot, dodgeGoal, 15f, 200f, 0);
            return;   // dodge owns steering+throttle and suppresses all weapons this tick
        }

        // Turn to face the LEAD direction; angleToLead = the residual angle by
        // which the nose still misses the lead (native turnToFace's return).
        var (yaw, pitch, angleToLead) = SteerTowardDirection(orient, aimDir);

        // Throttle law (shipIGC.cpp:2474): "fly hard" — 0.5 (angled away) …
        // 1.0 (nose on the lead).
        float throttle = AttackThrottle(angleToLead);

        // Relative speed along the bot's forward (shipIGC.cpp:2479-2481).
        var fwd = orient.GetForward();
        float ownDirSpeed = vDot(fwd, myVel);
        float targetDirSpeed = vDot(fwd, targetVel);
        float relativeSpeed = ownDirSpeed - targetDirSpeed;

        var steer = SolveAttackSteer(angleToLead, t, lifespan, relativeSpeed, targetDirSpeed,
            MaxSpeed(ActualHullId(bot)), Showcase.StrafeEnabled(bot.WingmanBehaviour),
            Showcase.InRangeAggressive(bot.WingmanBehaviour), bot.AttackAfterburnerHeld);

        if (steer.ThrottleOverride) throttle = steer.ThrottleValue;
        int state = steer.StateBits;

        // Escape / target switching (shipIGC.cpp:2545-2570): a Lost-band
        // AGGRESSIVE wingman switches to a nearer different-side ship, else
        // (and by default, non-aggressive) it burns after the escaped target.
        if (steer.Escape)
        {
            if (!bot.FinaleEscort && !bot.DefendingInPlace && !bot.FinaleBomberActive)
            {
                short alt = NearestOtherEnemy(cs, bot, CsSector(cs), myPos, exclude: targetId);
                if (alt >= 0) bot.TargetId = alt;
            }
            state |= ShipControlState.AfterburnerButton;
        }

        // P539b — hold fire when a FRIENDLY (same-side) ship lies in the line of
        // fire ahead (kept: native wingmen don't shoot through a teammate; the
        // server suppresses friendly damage anyway, shipIGC.cpp:1222-1228).
        float shotReach = Math.Min(weaponRange, Math.Max(0, (targetPos - myPos).Length() - targetR));
        bool friendlyBlocked = shotReach > 0f
            && FriendlyInCone(myPos, aimDir, shotReach, SameSideShipsInLine(cs, bot, myPos, shotReach), bot.Side);

        // Native close-band speed matching (shipIGC.cpp:2504-2511) can stop
        // opposite fleets indefinitely when the harness's friendly-line gate
        // prevents firing. Clear the line laterally, and bound even an unblocked
        // zero-speed hold to two seconds. This is a harness liveness policy using
        // native strafe controls (shipIGC.cpp:2590-2591), not a new attack band.
        if (myVel.LengthSquared() < IdleSpeedMps * IdleSpeedMps
            && (friendlyBlocked || now - bot.LastMoveAt >= CombatStationarySeconds))
            state |= (bot.ShipId & 1) == 0 ? ShipControlState.LeftButton : ShipControlState.RightButton;

        // Fire gate (shipIGC.cpp:2600): allWeapons iff angleToLead ≤ c_fMaxOffAngle
        // AND t ≤ lifespan·openFireDistPercentage, plus the kept friendly-line check.
        bool fire = !friendlyBlocked && AttackFireGate(angleToLead, t, lifespan);

        int stateM = state | (fire ? ShipControlState.AllWeapons : 0);
        bot.AttackAfterburnerHeld = (stateM & ShipControlState.AfterburnerButton) != 0;

        cs.CommandControls(new ControlData(yaw, pitch, 0f, throttle), stateM);

        // Missiles: native fires only while reasonably aimed at the target
        // (inside the `if (angleToLead < π/2)` block, shipIGC.cpp:2492/2573),
        // with the lock + evasion gate (shipIGC.cpp:2584-2596, P557).
        MaybeFireMissile(bot, cs, now, aimOk: angleToLead < MathF.PI * 0.5f);
        MaybeFireMine(bot, cs, now);
    }

    /// <summary>P558 — the native steer-toward law reduced to (yaw, pitch)
    /// control values for a world-space direction, returning the residual
    /// <c>angleToLead</c> (radians, 0 = nose on the direction) exactly as
    /// native <c>turnToFace</c> leaves it for the attack branch
    /// (shipIGC.cpp:2470-2474). The yaw/pitch math is the same
    /// <c>SteerToward</c> law <see cref="CombatFlyTo"/> uses.</summary>
    private static (float Yaw, float Pitch, float AngleToLead) SteerTowardDirection(Orientation orient, Vector3 dir)
    {
        float facing = -orient.TimesInverse(dir).Z;                // forward·dir
        float angle = MathF.Acos(Math.Clamp(facing, -1f, 1f));
        // Native common.cpp:169-176: the cross product is zero directly behind;
        // choose a yaw direction so the ship can actually turn to its target.
        if (facing <= -.999f) return (1f, 0f, angle);
        var body = orient.TimesInverse(Vector3.CrossProduct(orient.GetForward(), dir));
        float yaw = Math.Clamp(2f * body.Y, -1f, 1f);
        float pitch = Math.Clamp(2f * -body.X, -1f, 1f);
        return (yaw, pitch, angle);
    }

    /// <summary>P558 — the nearest DIFFERENT-side ship co-located in the bot's
    /// cluster, excluding itself and <paramref name="exclude"/> (the escaped
    /// target), for the native target-switch on a Lost-band escape
    /// (shipIGC.cpp:2545-2559). -1 when none.</summary>
    private static short NearestOtherEnemy(ConnectionSession cs, ActiveBot bot, short myCluster, Vector3 myPos, short exclude)
    {
        short best = -1; float bestD = float.MaxValue;
        if (!DecisionSnapshotEnabled)
        {
            // P636c "off" — P635 live scan under cs.Gate.
            lock (cs.Gate)
            {
                foreach (var ship in cs.World.Ships.Values)
                {
                    if (ship.ShipId == bot.ShipId || ship.ShipId == exclude || ship.IsNotVisible) continue;
                    if (ship.SideId == bot.Side) continue;      // a DIFFERENT side only
                    if (ship.SectorId != myCluster) continue;   // co-located
                    if (ship.Latest is not { } lu) continue;
                    float d = (lu.Position - myPos).Length();
                    if (d < bestD) { bestD = d; best = ship.ShipId; }
                }
            }
            return best;
        }
        // P636 — decision snapshot, no gate. P636b — iterate the VISIBLE ships
        // only (walking the full array to skip non-visible entries was an O(N)
        // cost per bot that scaled into the 200v200 collapse).
        var snap = SnapshotFor(cs);
        foreach (var ship in snap.VisibleShips)
        {
            if (ship.ShipId == bot.ShipId || ship.ShipId == exclude) continue;
            if (ship.SideId == bot.Side) continue;      // a DIFFERENT side only
            if (ship.SectorId != myCluster) continue;   // co-located
            if (!ship.HasLatest) continue;
            float d = (ship.Position - myPos).Length();
            if (d < bestD) { bestD = d; best = ship.ShipId; }
        }
        return best;
    }

    /// <summary>P558 — the hull's max cruise speed (<c>GetHullType()-&gt;GetMaxSpeed()</c>,
    /// used by the band-Close match-speed throttle, shipIGC.cpp:2506). A sane
    /// fallback avoids a degenerate match-speed throttle for an unknown hull.</summary>
    private static float MaxSpeed(short hullId)
    {
        var core = Catalog.RepresentativeCore();
        return core.HullTypes.TryGetValue(hullId, out var h) && h.Speed > 0f ? h.Speed : 100f;
    }

    /// <summary>Dot product of two <see cref="Vector3"/>s (this core's Vector3
    /// exposes no Dot — same algebra as native's <c>·</c>).</summary>
    private static float vDot(Vector3 a, Vector3 b) =>
        a.X * b.X + a.Y * b.Y + a.Z * b.Z;

    /// <summary>P539 — the real projectile ballistics (speed, reach =
    /// speed × lifespan, absolute-flag, and the raw projectile lifespan) of a
    /// mounted weapon part, read from the REAL part/projectile data. Returns
    /// <c>false</c> when the part is not a real weapon with a real projectile
    /// (the bot then cannot lead/fire). The lifespan is exposed separately for
    /// the P558 native attack law's distance bands and fire gate
    /// (<c>m_mountedWeapons[0]-&gt;GetLifespan()</c>, shipIGC.cpp:2477).</summary>
    private static bool TryWeaponBallistics(short weaponPartId, out float speed, out float range, out float lifespan, out bool absolute)
    {
        var core = Catalog.RepresentativeCore();
        if (core.PartTypes.TryGetValue(weaponPartId, out var dp)
            && dp.Type is DataPartTypeIGC.Weapon w
            && core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt)
            && pt.Speed > 0f)
        {
            speed = pt.Speed;
            range = pt.Speed * pt.Lifespan;
            lifespan = pt.Lifespan;
            absolute = pt.AbsoluteF;
            return true;
        }
        speed = 0f; range = 0f; lifespan = 0f; absolute = false;
        return false;
    }

    /// <summary>P539 — the 0.5 × Length radius convention for a hull id (cf.
    /// the ship-radius convention, <c>Ship.cs:1873</c>). Falls back to a small
    /// nominal 6 u for an unknown hull so steering never degenerates.</summary>
    private static float HullRadius(short hullId)
    {
        var core = Catalog.RepresentativeCore();
        return core.HullTypes.TryGetValue(hullId, out var h) && h.Length > 0f ? 0.5f * h.Length : 6f;
    }

    /// <summary>P539b — tally the bot's REAL combat stats from its OWN wire/world
    /// events since the last tick, ALL attributed to THIS bot as the shooter:
    /// <list type="bullet">
    /// <item><b>Shots</b> — this bot's OWN <c>PROJECTILE_INSTANCE</c> echoes
    /// (rounds the server actually fired and broadcast back to the shooter's own
    /// peer, <c>ProjectileInstance.LauncherId == myShipId</c>) — NOT a fire
    /// decision (P539's <c>ShotsFired++</c> at the fire gate over-counted
    /// "about to fire" ticks that never resolved into a round).</item>
    /// <item><b>Hits</b> — <c>PROJECTILE_HULL_HIT</c> whose <c>SourceId</c> is
    /// our own ship (the wire now carries the shooter; the OLD code counted
    /// every sector-wide hull hit as this bot's — that is exactly why P539's
    /// 4,400 "ship hits" bore no relation to real damage). Split by the struck
    /// target's side into enemy / friendly / none(-1, no ship), and by the
    /// <c>AbsorbedByShield</c> flag into enemy shield vs hull hits.</item>
    /// <item><b>Kills</b> — <c>KILL_SHIP</c> where this bot is the credited
    /// killer (unchanged).</item>
    /// </list>
    /// Called each combat tick under <c>cs.Gate</c>.</summary>
    private static void SampleCombatStats(ActiveBot bot, ConnectionSession cs, short myShipId, double now)
    {
        lock (cs.Gate)
        {
            // Real shots: this bot's own fired rounds (the shooter's own peer
            // always receives its PROJECTILE_INSTANCE).
            if (cs.World.CountOnlyProjectiles) bot.ShotsFired += cs.World.TakeOwnProjectileCount();
            else foreach (var pj in cs.World.TakeNewProjectiles())
                if (pj.LauncherId == myShipId) bot.ShotsFired++;

            // Real hits attributed to the shooter: only hull-hits WE caused,
            // bucketed by the struck target's side + shield/hull (pure logic).
            var hits = new List<(short SourceId, short TargetId, bool Absorbed, short? TargetSide)>();
            foreach (var h in cs.World.TakeNewHullHits())
            {
                short? tgtSide = null;
                if (h.TargetId != -1 && cs.World.Ships.TryGetValue(h.TargetId, out var tgt))
                    tgtSide = tgt.SideId;
                hits.Add((h.SourceId, h.TargetId, h.AbsorbedByShield, tgtSide));

                // P622 — threat tracking: an ENEMY (different-side) ship that just
                // damaged ME is recorded as an active attacker, so the selector can
                // engage it first. Pruned lazily (bounded, windowed).
                if (h.TargetId == myShipId && h.SourceId != -1
                    && cs.World.Ships.TryGetValue(h.SourceId, out var atk)
                    && atk.SideId != -1 && atk.SideId != bot.Side)
                {
                    bot.RecentAttackers[h.SourceId] = now;
                    if (bot.FinaleBomberActive) bot.FinaleProtectedSince ??= now;
                }

                if (h.SourceId >= 0 && cs.World.Ships.TryGetValue(h.SourceId, out var assetAttacker)
                    && assetAttacker.SideId >= 0 && assetAttacker.SideId != bot.Side
                    && (tgtSide == bot.Side || (cs.World.Stations.TryGetValue(h.TargetId, out var station) && station.SideId == bot.Side)))
                    bot.RecentAssetAttackers[h.SourceId] = now;

                if (h.SourceId >= 0 && cs.World.Ships.TryGetValue(h.SourceId, out var stationAttacker)
                    && stationAttacker.SideId >= 0 && stationAttacker.SideId != bot.Side
                    // The port hit extension has no object-type discriminator.
                    // Ambiguous ship/station IDs rely on the native station alert.
                    && !cs.World.Ships.ContainsKey(h.TargetId)
                    && cs.World.Stations.TryGetValue(h.TargetId, out var attackedStation)
                    && attackedStation.SideId == bot.Side && attackedStation.ClusterId == h.ClusterId)
                    bot.RecentStationAttackers[(attackedStation.StationId, h.SourceId)] = now;

                // P556 — TTK: record the FIRST damaging enemy hit on this victim
                // (our own hit on a different-side ship; shield- or hull-absorbed —
                // a shielded victim soaks hits from first contact, so a stronger
                // shield shows up as a LONGER first-hit→death interval).
                if (h.SourceId == myShipId && h.TargetId != -1 && tgtSide is { } ts && ts != -1 && ts != bot.Side)
                    lock (TtkLock) TtkFirstHit.TryAdd(h.TargetId, (now, ts));
            }
            var buckets = BucketHits(myShipId, bot.Side, hits);
            bot.ShipHits += buckets.EnemyShield + buckets.EnemyHull;
            bot.EnemyShieldHits += buckets.EnemyShield;
            bot.EnemyHullHits += buckets.EnemyHull;
            bot.FriendlyHits += buckets.Friendly;
            bot.NoneHits += buckets.None;
            var events = cs.World.Events;
            foreach (var k in events.KillsAfter(bot.KillCursor))
            {
                if (k.IdCredit == myShipId && k.TypeCredit == 0)
                {
                    bot.Kills++;
                    if (bot.Finale is { } finale)
                        lock (finale.Sync) if (!finale.Rotated) finale.Kills.Enqueue((now, bot.Side));
                }   // OT_ship killer
                // P556 — TTK: an observed death (KILL_SHIP of a ship we first
                // hit, of any credited killer) resolves that victim's
                // first-hit→death interval into a sample for its side.
                if (k.TypeCredit == 0)
                {
                    lock (TtkLock)
                        if (TtkFirstHit.TryGetValue(k.ShipId, out var fh))
                        {
                            TtkSamples.Add((fh.Side, now - fh.At));
                            TtkFirstHit.Remove(k.ShipId);
                        }
                }
            }
            bot.KillCursor = events.KillSequence;

            // P622 — lazily prune the attacker-window map (a handful of entries
            // per tick; bounded by the enemies that actually shoot this bot).
            if (now - bot.LastAttackersPruneAt > 5.0)
            {
                bot.LastAttackersPruneAt = now;
                foreach (var id in bot.RecentAssetAttackers.Where(a => now - a.Value > ThreatWindowSeconds).Select(a => a.Key).ToArray())
                    bot.RecentAssetAttackers.Remove(id);
                foreach (var key in bot.RecentStationAttackers.Where(a => now - a.Value > ThreatWindowSeconds).Select(a => a.Key).ToArray())
                    bot.RecentStationAttackers.Remove(key);
                if (bot.RecentAttackers.Count > 0)
                {
                    var stale = bot.RecentAttackers
                        .Where(kv => now - kv.Value > ThreatWindowSeconds)
                        .Select(kv => kv.Key).ToList();
                    foreach (var s in stale) bot.RecentAttackers.Remove(s);
                }
            }
        }
    }

    /// <summary>P539b — pure bucketing of drained projectile hits into enemy
    /// (shield / hull) / friendly / none (-1 or an unknown side), counting ONLY
    /// the hits whose <paramref name="myShipId"/> is the SHOOTER
    /// (<c>SourceId == me</c>). A hit fired by ANYONE ELSE is never counted for
    /// this bot — that is the P539 metric bug this replaces (the old code counted
    /// every sector-wide hit as every bot's). Unit-provable with synthetic hits
    /// and no server.</summary>
    public readonly record struct HitBuckets(long EnemyShield, long EnemyHull, long Friendly, long None);

    public static HitBuckets BucketHits(
        short myShipId, short mySide,
        IEnumerable<(short SourceId, short TargetId, bool Absorbed, short? TargetSide)> hits)
    {
        long es = 0, eh = 0, f = 0, n = 0;
        foreach (var h in hits)
        {
            if (h.SourceId != myShipId) continue;          // someone else's hit — never ours
            if (h.TargetSide is { } ts && ts != -1 && ts != mySide)
            { if (h.Absorbed) es++; else eh++; }
            else if (h.TargetSide is { } tf && tf != -1 && tf == mySide) f++;
            else n++;
        }
        return new HitBuckets(es, eh, f, n);
    }

    /// <summary>
    /// P539 — detect whether this bot's own ship has become a LIFEPOD: the
    /// server swaps the SAME ship to the side's lifepod hull on eject
    /// (<c>Server.Combat.EjectSurvivor</c>, <c>FedSrv.CPP:11942</c>), keeping
    /// the same ship id, so the bot recognizes it from its OWN world view via
    /// the hull's real <c>HabmLifepod</c> capability. A pod drops any committed
    /// order/target and returns <c>true</c> so the caller flies it home instead
    /// of fighting (fixing the observed "bots won't die — they chase and ram as
    /// unarmed pods"). When the bot relaunches into a fresh (non-pod) hull the
    /// pod state is cleared and combat resumes.
    /// </summary>
    private static bool UpdatePodState(ActiveBot bot, ConnectionSession cs, double now)
    {
        HandleRelaunchAck(bot, cs);
        short hull;
        short stationId;
        lock (cs.Gate)
        {
            if (!cs.World.Ships.TryGetValue(bot.ShipId, out var me)) return bot.IsPod;
            hull = me.HullId;
            stationId = me.StationId;
            // True death sends KILL_SHIP before the restart's dock notification.
            // An oxygen respawn is safe to relaunch, but did not fly home.
            foreach (var killed in cs.World.Events.KillsAfter(bot.PodOutcomeCursor))
                if (killed.ShipId == bot.ShipId && killed.BDeathCredit)
                    bot.PodRestarted = true;
            bot.PodOutcomeCursor = cs.World.Events.KillSequence;
        }
        bool isPod = Catalog.IsLifepodHull(hull);
        if (isPod)
        {
            if (!bot.IsPod)
            {
                bot.IsPod = true;
                bot.EverPod = true;
                bot.Deaths++;
                if (bot.FinaleBomberActive && bot.Finale is { } finale)
                    lock (finale.Sync) { finale.BombersKilled++; bot.FinaleProtectedSince = null; bot.FinaleGrantVerified = false; }
                // P539d — classify this death: a credited ship shooter (KILL_SHIP
                // TypeCredit 0 = OT_ship, distinct killer) = gunfire kill; otherwise
                // (no credited shooter — a ram/crash) = collision/other death.
                List<(short, short, short)> kills = new();
                lock (cs.Gate)
                    foreach (var k in cs.World.Events.Kills)
                        kills.Add((k.ShipId, k.IdCredit, k.TypeCredit));
                bool gunfire = ClassifyDeath(bot.ShipId, kills) == DeathCause.GunfireKill;
                if (gunfire) bot.GunfireDeaths++; else bot.CollisionDeaths++;
                bot.OrderedTargetId = null;
                bot.TargetId = null;
                Console.WriteLine($"[p539d] DEATH bot {bot.Name} ship {bot.ShipId} side {bot.Side} cause={(gunfire ? "gunfire" : "collision")} deaths={bot.Deaths} gunfire={bot.GunfireDeaths} collision={bot.CollisionDeaths}");
                Console.WriteLine($"[p539] bot {bot.Name} (ship {bot.ShipId}) is now a LIFEPOD (hull {hull}) — stopping combat, flying home to dock");
            }
            if (!bot.PodDocked && stationId != -1)
            {
                bot.PodDocked = true;
                bot.PodDockStationId = stationId;
                if (bot.PodRestarted)
                {
                    bot.PodsRestartedTotal++;
                    Console.WriteLine($"[p699] pod bot {bot.Name} (ship {bot.ShipId}) RESTARTED at station {stationId} after true death — not a home dock");
                }
                else
                {
                    bot.PodsDockedTotal++;
                    Console.WriteLine($"[p539] pod bot {bot.Name} (ship {bot.ShipId}) DOCKED at station {stationId} — pod reached home");
                }
            }
            return true;
        }
        if (bot.IsPod && stationId >= 0) return true; // purchase ACK precedes launch SET_CLUSTER
        if (bot.IsPod)
        {
            ResetNewLife(bot, now, reinforcement: true);
            Console.WriteLine($"[p539] bot {bot.Name} (ship {bot.ShipId}) relaunched into hull {hull} — re-routing to target sector {bot.TargetSector} to rejoin the fight (P637: reinforcement skips staging, routes straight)");
        }
        return false;
    }

    /// <summary>P575 — per-station-type landing-bay geometry cache for the pod
    /// dock approach (mirrors the server/client caches; resolved from the type's
    /// real <c>.cvh</c> under the artwork root). Null = no bays.</summary>
    private static readonly Dictionary<short, Allegiance.Core.Collision.HullLandingBayGeometry?> _podDockBays = new();

    /// <summary>
    /// P575 — the pod-home dock approach: compute the SHARED native bay-approach
    /// <see cref="ControlData"/> for a lifepod returning to its friendly station,
    /// using the SAME planner (<see cref="DockApproach"/>) the player autopilot
    /// and the server NPC autopilot use. Returns <c>false</c> (caller falls back
    /// to the plain centre fly — native no-bay power glide) when the station is
    /// not dockable / its bay geometry isn't resolvable.
    /// </summary>
    private static bool TryDockApproachControl(ConnectionSession cs, ActiveBot bot, short stationId, out ControlData ctrl, out int movement)
    {
        ctrl = ControlData.Neutral;
        movement = 0;
        var core = Catalog.RepresentativeCore();
        if (!core.HullTypes.TryGetValue(ActualHullId(bot), out var hull)) return false;

        DataStationIGC st;
        lock (cs.Gate)
        {
            if (!cs.World.Stations.TryGetValue(stationId, out st)) return false;
        }
        if (!core.StationTypes.TryGetValue(st.StationTypeId, out var stype)) return false;

        // Ability gate — same need the server's dock dispatch applies
        // (Igc/shipIGC.cpp:727-729): fighters (including pods) SabmLand, others SabmCapLand.
        short need = hull.HasCapability(DataHullTypeIGC.HabmFighter)
            ? DataStationTypeIGC.SabmLand
            : DataStationTypeIGC.SabmCapLand;
        if (!stype.HasCapability(need)) return false;

        if (!_podDockBays.TryGetValue(stype.StationTypeId, out var geom))
        {
            geom = null;
            try
            {
                var path = Path.Combine(Catalog.ArtRoot, stype.ModelName + ".cvh");
                if (File.Exists(path))
                {
                    var model = CvhReader.Parse(File.ReadAllText(path));
                    var rec = HullLandingBay.ReconstructStation(model, stype.Radius);
                    geom = rec.LandSlots.Count == 0 ? null : rec;
                }
            }
            catch { geom = null; }
            _podDockBays[stype.StationTypeId] = geom;
        }
        if (geom is null || geom.LandSlots.Count == 0) return false;

        if (!TryCsPose(cs, bot.ShipId, out var pos, out var orient)) return false;
        Vector3 vel = Vector3.Zero;
        float rateYaw = 0, ratePitch = 0;
        GlobalAttributeSet attributes;
        lock (cs.Gate)
        {
            if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.Latest is { } mu)
            {
                vel = mu.Velocity;
                var rates = mu.TurnRates;
                rateYaw = rates[ControlData.AxisYaw];
                ratePitch = rates[ControlData.AxisPitch];
            }
            attributes = cs.World.GlobalAttributesFor(bot.Side);
        }

        var so = new Orientation();
        if (st.Forward is { Length: 3 } f && st.Up is { Length: 3 } u) so.Set(Vec3(f), Vec3(u));
        Vector3 center = Vec3(st.Position);

        // World-rotated bays from the landing slots (capital prefix, then fighters)
        // (native centers[i] = GetLandPosition(i,0)*itsOrientation).
        var bays = new List<DockApproach.DockBay>(geom.LandSlots.Count);
        foreach (var slot in geom.LandSlots)
        {
            var p0 = slot.Planes[0];
            bays.Add(new DockApproach.DockBay(p0.Position * so, p0.Direction.Normalize() * so));
        }
        int nLand = DockApproach.StationBaysForClass(
            hull.HasCapability(DataHullTypeIGC.HabmFighter), bays, geom.CapLandSlots);

        float speed = vel.Length();
        float distanceRest = DockApproach.StoppingDistance(speed, hull.Thrust * attributes.Get(GlobalAttributeSet.Thrust),
            hull.Mass, hull.Speed * attributes.Get(GlobalAttributeSet.MaxSpeed), hull.BackMultiplier);
        Vector3 positionRest = speed > .1f ? pos + vel * (distanceRest / speed) : pos;

        float totalRadius = HullRadius(ActualHullId(bot)) + stype.Radius + 10f;
        var step = DockApproach.DockStation(
            pos, vel, orient, center, bays, nLand, distanceRest, positionRest,
            totalRadius, rateYaw, ratePitch, experimental: false,
            bayPreference: hull.HasCapability(DataHullTypeIGC.HabmLifepod) ? bot.ShipId : 0);

        // This controls-only client follows authoritative snapshots, rather than
        // integrating its own pose. Retain its stable cross-product turn law;
        // native entry-rate gating and directional RCS still use live dynamics.
        ctrl = DockApproach.Steer(step, pos, orient, vel, distanceRest);
        movement = DockApproach.MovementButtons(step, pos, orient, vel,
            DockApproach.TurnState.FromHull(hull, attributes, rateYaw, ratePitch));
        return true;
    }

    /// <summary>
    /// P560 — a lifepod's chosen home-dock station: id, its sector, and the
    /// station centre to fly to. The pod picks it once when it starts flying
    /// home and flies there for the rest of its life.
    /// </summary>
    internal readonly record struct PodHomePick(short StationId, short SectorId, Vector3 Center);

    /// <summary>
    /// P560 — choose the pod's home-dock station the way the server chooses a
    /// respawn station (P550, <c>FedSrv.CPP:12034-12038</c>, nearest
    /// <c>c_sabmRestart</c> friendly station), made route-aware for the MCP:
    /// among same-side stations whose station type carries
    /// <see cref="DataStationTypeIGC.SabmRestart"/> (the server's <c>c_sabmRestart</c>
    /// filter), pick the one at the SHORTEST WARP-ROUTE distance from the pod's
    /// current sector (<c>routeHops</c>; unreachable stations are skipped — the
    /// straight-line nearest-station pick P560 replaced ignored sectors and could
    /// point a pod at a home in an unreachable pocket), tie-broken by Euclidean
    /// distance within a sector. Pure over plain data so the rule is unit-provable.
    /// Returns null only when no restart-capable friendly station is reachable
    /// (native's no-<c>pstationDest</c> path — the pod holds and retries).
    /// </summary>
    internal static PodHomePick? PickPodHomeStation(
        IReadOnlyList<(short Id, short Side, short Sector, Vector3 Pos, short StationTypeId)> stations,
        short mySide, short mySector, Vector3 myPos,
        Func<short, bool> isRestartCapable,
        Func<short, short, int> routeHops)
    {
        short bestId = -1; short bestSector = -1; Vector3 bestCenter = default;
        int bestHops = int.MaxValue; float bestDist = float.MaxValue;
        foreach (var (id, side, sector, pos, tid) in stations)
        {
            if (side != mySide) continue;               // c_ttFriendly (FedSrv.CPP:12034)
            if (!isRestartCapable(tid)) continue;       // c_sabmRestart filter (P550)
            int hops = routeHops(mySector, sector);     // route-distance (warp hops)
            if (hops < 0) continue;                     // unreachable pocket — skip
            float d = (pos - myPos).LengthSquared();
            if (hops < bestHops || (hops == bestHops && d < bestDist))
            { bestHops = hops; bestDist = d; bestId = id; bestSector = sector; bestCenter = pos; }
        }
        return bestId < 0 ? null : new PodHomePick(bestId, bestSector, bestCenter);
    }

    /// <summary>
    /// P560 — steer a cross-sector WARP LEG with P540b's
    /// <see cref="AutopilotSteering.Pursue"/> (full-throttle fly-through of the
    /// aleph) instead of the braking <c>CombatFlyTo</c>/<see cref="WarpApproachParams"/>
    /// law. A warp is a transit point, not a destination: the braking law eased
    /// the throttle inside the transit sphere, so a small hull emerged slowly and
    /// lingered within the warp's collision radius until the cooldown expired —
    /// re-crossing the aleph it just exited (the live Alderan⇄Rigel ping-pong of
    /// bot lifepods AND tower layer-drones). Pursue keeps full forward throttle and
    /// nose-follow through the mouth, so the hull crosses at cruise speed and clears
    /// the far-side sphere — the exact P540b fix for the player autopilot, now
    /// shared by the bots (reuse, not a third law). Dodge still pre-empts (P489) so
    /// a warp-leg approach never rams an asteroid near the aleph; when dodging the
    /// existing <see cref="CombatFlyTo"/> avoidance path runs unchanged.
    /// </summary>
    private static void FlyWarpLeg(ActiveBot bot, Vector3 warpPos)
    {
        var cs = bot.Cs!;
        if (!TryCsPose(cs, bot.ShipId, out var pos, out var orient)) return;
        if (TryDodgeGoal(cs, bot, pos, out var dodgeGoal))
        {
            CombatFlyTo(bot, dodgeGoal, 15f, 200f, 0);   // dodge owns the tick (P489/P553)
            return;
        }

        var aim = warpPos;

        // P610 — travel spacing: softly sidestep nearby same-side teammates so big
        // fleets stream instead of rubbing and the reactive dodge rarely has to
        // fire during transit. Never stalls forward progress (lateral push only).
        float hullR = HullRadius(ActualHullId(bot));
        var mates = SameSideShipsNearby(cs, bot, pos, TravelSpacingMultiple * hullR);
        if (mates is not null && mates.Count > 0)
            aim = ApplyTravelSeparation(pos, aim, mates, TravelSpacingMultiple * hullR, TravelSpacingStrength);

        // P610 — entry stagger: spread the aim across the aleph mouth (deterministic
        // per-bot point on the disc) with a light queue (yield when a teammate is
        // already just ahead in the funnel) so the fleet streams through the aleph
        // instead of converging on one point and grinding.
        aim = StaggeredAlephAim(bot, cs, pos, aim, warpPos);

        cs.CommandControls(AutopilotSteering.Pursue(pos, orient, aim), 0);
    }

    /// <summary>P610 — the entry-stagger + light-queue aim for a warp leg: spread
    /// this bot's aim across the aleph's capture disc (a deterministic per-bot
    /// point via <see cref="EntryAimPoint"/>), and if a same-side teammate is
    /// already just ahead in the funnel near this bot's aim, deflect to a wider
    /// ring point so the bot yields instead of piling on the same converging
    /// point. Falls back to the plain warp position when the aleph radius is
    /// unknown (defensive — never worse than the old behaviour).</summary>
    private static Vector3 StaggeredAlephAim(ActiveBot bot, ConnectionSession cs, Vector3 pos, Vector3 fallback, Vector3 warpPos)
    {
        // Aleph radius from the bot's own world (authoritative DataWarpIGC.Radius).
        float radius = 0f;
        lock (cs.Gate)
        {
            foreach (var w in cs.World.Warps.Values)
                if (w.Position is { Length: 3 } p &&
                    MathF.Abs(p[0] - warpPos.X) < 1e-3f &&
                    MathF.Abs(p[1] - warpPos.Y) < 1e-3f &&
                    MathF.Abs(p[2] - warpPos.Z) < 1e-3f)
                { radius = w.Radius; break; }
        }
        if (radius <= 0f) return fallback;

        var aim = EntryAimPoint(warpPos, radius, bot.ShipId, EntrySpreadFraction);

        // Light queue: a same-side mate already in the funnel just ahead and near
        // this bot's aim → deflect to a wider, distinct ring point (re-seeded) so
        // both don't converge on the same mouth region.
        if (FunnelMates(cs, bot, pos, warpPos) is { Count: > 0 } funnel &&
            EntryQueueYield(pos, aim, warpPos, radius, funnel, EntryQueueLookahead, EntryQueueFunnelWidth))
            aim = EntryAimPoint(warpPos, radius, (short)(bot.ShipId ^ 0x5A5A), MathF.Min(EntrySpreadFraction + 0.2f, 0.9f));
        return aim;
    }

    /// <summary>P610 — gather same-side teammates in the bot's sector within
    /// <paramref name="range"/> (the travel-separation term's influence
    /// neighbourhood).</summary>
    private static List<(Vector3 Pos, short Id)>? SameSideShipsNearby(ConnectionSession cs, ActiveBot bot, Vector3 myPos, float range)
    {
        List<(Vector3, short)>? list = null;
        short myCluster = CsSector(cs);
        lock (cs.Gate)
        {
            foreach (var (id, ent) in cs.World.Ships)
            {
                if (id == bot.ShipId) continue;
                if (ent.SideId != bot.Side) continue;
                if (ent.SectorId != myCluster) continue;
                if (ent.Latest is not { } lu) continue;
                float dd = (lu.Position - myPos).LengthSquared();
                if (dd > range * range) continue;
                (list ??= new List<(Vector3, short)>()).Add((lu.Position, id));
            }
        }
        return list;
    }

    /// <summary>P610 — gather same-side teammates already in the ENTRY FUNNEL
    /// ahead of the bot (between it and <paramref name="warpPos"/>, within
    /// <see cref="EntryQueueLookahead"/> of the aleph) for the entry-queue yield.</summary>
    private static List<(Vector3 Pos, short Id)>? FunnelMates(ConnectionSession cs, ActiveBot bot, Vector3 myPos, Vector3 warpPos)
    {
        List<(Vector3, short)>? list = null;
        short myCluster = CsSector(cs);
        var toAleph = warpPos - myPos;
        float dA = toAleph.Length();
        if (dA <= 1e-3f) return null;
        var line = toAleph / dA;
        lock (cs.Gate)
        {
            foreach (var (id, ent) in cs.World.Ships)
            {
                if (id == bot.ShipId) continue;
                if (ent.SideId != bot.Side) continue;
                if (ent.SectorId != myCluster) continue;
                if (ent.Latest is not { } lu) continue;
                var toMate = lu.Position - myPos;
                float along = toMate.X * line.X + toMate.Y * line.Y + toMate.Z * line.Z;
                if (along <= 0f) continue;                       // behind me
                if (dA - along > EntryQueueLookahead) continue;  // not yet in the funnel ahead
                (list ??= new List<(Vector3, short)>()).Add((lu.Position, id));
            }
        }
        return list;
    }

    /// <summary>
    /// P539 — fly a lifepod HOME to dock, as a human pilot would: route (real
    /// warps, the same P496 warp-approach law) to the nearest friendly station's
    /// sector, then enter its landing bay with no weapons (a pod doesn't
    /// fight) until docked. Once docked the server's normal dock/relaunch path
    /// takes over; when the bot relaunches into a fresh fighter
    /// (<see cref="UpdatePodState"/> clears <c>IsPod</c>) combat resumes.
    /// </summary>
    private void DrivePodHome(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (bot.PodDocked) { RelaunchDockedPod(bot, cs, now); return; }

        if (bot.PodStationId < 0)
        {
            // P560 — pick the pod's home the server's way (P550: nearest
            // c_sabmRestart friendly station), made route-aware: shortest
            // warp-route distance from the pod's CURRENT sector, tie-broken by
            // Euclidean distance, only restart-capable friendly stations. The
            // P539 straight-line nearest pick ignored sectors and could point a
            // pod at a home in an unreachable pocket (then it would oscillate).
            short curSec = CsSector(cs);
            Vector3 myPos = Vector3.Zero;
            var core = Catalog.RepresentativeCore();
            var stations = new List<(short Id, short Side, short Sector, Vector3 Pos, short StationTypeId)>();
            ClusterGraph? graph;
            lock (cs.Gate)
            {
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.Latest is { } mu) myPos = mu.Position;
                foreach (var st in cs.World.Stations.Values)
                    if (st.Position is { Length: 3 } p)
                        stations.Add((st.StationId, st.SideId, st.ClusterId,
                            new Vector3(p[0], p[1], p[2]), st.StationTypeId));
                graph = cs.World.Graph;
            }
            int Hops(short a, short b) => graph is null ? (a == b ? 0 : -1)
                : graph.FindShortestPath(a, b) is { } path ? Math.Max(0, path.Count - 1) : -1;
            var pick = PickPodHomeStation(stations, bot.Side, curSec, myPos,
                tid => core.StationTypes.TryGetValue(tid, out var t) && t.HasCapability(DataStationTypeIGC.SabmRestart),
                Hops);
            if (pick is { } pk)
            {
                bot.PodStationId = pk.StationId;
                bot.PodStationSector = pk.SectorId;
                bot.PodStationCenter = pk.Center;
            }
            if (bot.PodStationId < 0)
            {
                LoiterCombat(bot, now);   // no reachable restart-capable friendly station — hold, retry next tick
                return;
            }
            Console.WriteLine($"[p539] pod bot {bot.Name} (ship {bot.ShipId}) flying home to friendly restart-capable station {bot.PodStationId} in sector {bot.PodStationSector}");
        }

        short cur = CsSector(cs);
        if (cur < 0) return;
        if (cur == bot.PodStationSector)
        {
            // P575 — in the home sector, use the SHARED native bay-approach
            // planner (DockApproach — the same routine the player autopilot and
            // server NPCs use) to line up on the station's landing bay and enter,
            // instead of flying at the centre and hoping the line hits a bay (the
            // outer-ring grind Domingo reported). Falls back to the old centre fly
            // (native no-bay power glide) when the station isn't dockable here.
            if (TryDockApproachControl(cs, bot, bot.PodStationId, out var dockCtrl, out var dockMovement))
            {
                cs.CommandControls(dockCtrl, dockMovement);
                return;
            }
            // Native no-bay power glide (Igc/common.cpp:1517-1524): fly THROUGH
            // the target, with no arrival brake or dodge of the station itself.
            // CombatFlyTo treated home as an obstacle and stranded pods outside it.
            if (TryCsPose(cs, bot.ShipId, out var podPos, out var podOrient))
                cs.CommandControls(Allegiance.ClientCore.Flight.AutopilotSteering.Pursue(
                    podPos, podOrient, bot.PodStationCenter), 0);
            return;
        }
        // Native FindPath seeds by mouth distance (Igc/common.cpp:917-958).
        // Pods have no wing/staging objective: don't reuse the combat route or
        // choose equal-hop alephs by sector id while their oxygen burns.
        DataWarpIGC? warp = null;
        lock (cs.Gate)
            if (cs.World.Ships.TryGetValue(bot.ShipId, out var me) && me.Latest is { } pose)
            {
                // Plan once per sector/export topology, not at the 20Hz control
                // cadence for every pod. Refresh when a partial export fills in.
                if (bot.PodRouteSector != cur || bot.PodRouteWarpCount != cs.World.Warps.Count
                    || !cs.World.Warps.ContainsKey(bot.PodRouteWarpId))
                {
                    var pick = PodReturnNavigation.NextWarp(cs.World.Warps, cur, bot.PodStationSector,
                        pose.Position, bot.PodStationCenter);
                    bot.PodRouteWarpId = pick?.WarpId ?? (short)-1;
                    bot.PodRouteSector = cur;
                    bot.PodRouteWarpCount = cs.World.Warps.Count;
                }
                if (cs.World.Warps.TryGetValue(bot.PodRouteWarpId, out var chosen)) warp = chosen;
            }
        if (warp is { } mouth) FlyWarpLeg(bot, Vec3(mouth.Position));
        else LoiterCombat(bot, now);
    }

    /// <summary>P557 — the first real MINE DISPENSER part id in a loadout (a
    /// launcher whose expendable is a mine type — native
    /// <c>GetExpendableType()->GetObjectType() == OT_mineType</c>,
    /// shipIGC.cpp:2653), or null when the loadout carries none. The combat
    /// law uses this to know a bot has "mines already equipped" for the
    /// UseMines behaviour.</summary>
    private static short? FindMineDispenser(IReadOnlyList<ExpandedPartData> parts)
    {
        var core = Catalog.RepresentativeCore();
        foreach (var e in parts)
            if (Showcase.IsMineLauncher(core, e.PartId)) return e.PartId;
        return null;
    }

    /// <summary>
    /// P433 — fire the bot's real mounted missile launcher when its REAL lock
    /// reaches full on its current engagement target. The server streams the
    /// owner-only <c>MISSILE_LOCK_UPDATE(204)</c> each tick
    /// (Server.Broadcast.BroadcastMissileLock) into the bot's own ClientWorld
    /// (<c>cs.World.MissileLock</c>, a <see cref="Allegiance.ClientCore.State.MissileLockState"/>),
    /// so <c>IsFullLock</c> is the authoritative server-computed lock quality.
    /// On full lock, send the real <c>SendFireMissile()</c> (the wire request the
    /// server's HandleFireMissile → <see cref="Allegiance.Core.Sim.Ship.TryFireMissile"/>
    /// gates on lock+ammo and rate-limits via lock-rebuild + ammo decrement). The
    /// fire is additionally paced by the missile's real <see cref="MissileReadyTime"/>
    /// so a dumbfire launcher (instant lock, always-full) is never spammed —
    /// mirroring how the guns' AllWeapons beat is naturally paced by the weapon's
    /// refire. A launcher-less bot does nothing.
    /// </summary>
    private static void MaybeFireMissile(ActiveBot bot, ConnectionSession cs, double now, bool aimOk)
    {
        // P557 — native wingman gate: only a UseMissiles wingman fires its
        // missile launcher (shipIGC.cpp:2584). The default scenario mask sets
        // UseMissiles, so the standing P433 launcher behaviour is unchanged.
        if (!Showcase.UsesMissiles(bot.WingmanBehaviour)) return;
        // P558 — native fires missiles only while reasonably aimed at the
        // target: the missile rule sits inside the `if (angleToLead < π/2)`
        // block (shipIGC.cpp:2492/2573). A bot swinging its nose off the target
        // doesn't spend a missile.
        if (!aimOk) return;
        if (bot.LauncherPartId is null || bot.MissileReadyTime <= 0) return;
        if (now - bot.LastFireAt < bot.MissileReadyTime) return;
        bool full;
        lock (cs.Gate) full = cs.World.MissileLock.IsReady && cs.World.MissileLock.IsFullLock;
        if (!full) return;

        // P557 — native evasion gate (shipIGC.cpp:2585-2594): fire only when the
        // target's evasion speed is below the missile's max evasion speed.
        // Reading the bot's orientation and the current engagement target's
        // velocity from the bot's own world (same lock domain as the lock read).
        bool evasionOk = true;
        if (TryCsPose(cs, bot.ShipId, out _, out var orient) && bot.TargetId is { } tid)
        {
            Vector3 tv;
            lock (cs.Gate)
            {
                if (!cs.World.Ships.TryGetValue(tid, out var te) || te.Latest is not { } tlu) { tv = Vector3.Zero; }
                else tv = tlu.Velocity;
            }
            if (tv.LengthSquared() > 1e-6f)
            {
                float maxLock = 1.0f;   // fallback if the missile type is unknown
                var core = Catalog.RepresentativeCore();
                if (bot.LauncherPartId is { } lp && core.Launchers.TryGetValue(lp, out var l)
                    && core.MissileTypes.TryGetValue(l.ExpendabletypeId, out var mt))
                    maxLock = mt.MaxLock;
                evasionOk = Showcase.DecideWingmanMissile(
                    full ? 1.0f : 0f, tv, orient.GetRight(), orient.GetUp(), maxLock);
            }
        }
        if (!evasionOk) return;

        cs.SendFireMissile();
        bot.LastFireAt = now;
        Console.WriteLine($"[p433] bot {bot.Name} (ship {bot.ShipId}) fired real missile launcher at lock 1.0 -> SendFireMissile (ready {bot.MissileReadyTime:F2}s)");
    }

    /// <summary>
    /// P557 — fire the bot's real mounted MINE DISPENSER the native wingman way
    /// (shipIGC.cpp:2651-2665): a UseMines wingman with a mounted mine-type
    /// dispenser sets mineFireIGC whenever a DIFFERENT-side combat actor is
    /// within <see cref="Showcase.WingmanMineRange"/> (2500 u) of it. Here the
    /// bot's own world is scanned for the nearest different-side ship in its
    /// cluster within that range, and on qualification it sends the real
    /// <c>SendFireExpendable(ET_Dispenser)</c> — the wire request the server's
    /// <c>HandleFireExpendable</c> resolves at mount 0 to the mounted mine
    /// launcher, decrements its ammo, and deploys a NATIVE mine object
    /// (<c>Ship.DeployMine</c> → P452 minefield attrition / P453 visibility).
    /// Paced so a dispenser is not spammed every tick.
    /// </summary>
    private static void MaybeFireMine(ActiveBot bot, ConnectionSession cs, double now)
    {
        if (!Showcase.UsesMines(bot.WingmanBehaviour)) return;       // flag off
        if (bot.MineDispenserPartId is null) return;                 // "needs mines already equipped"
        if (now - bot.LastMineAt < 1.0) return;                      // pace (mine deploy burst)
        if (!TryCsPose(cs, bot.ShipId, out var myPos, out _)) return;
        short myCluster = CsSector(cs);
        bool enemyNear = false;
        lock (cs.Gate)
        {
            foreach (var kv in cs.World.Ships)
            {
                if (kv.Key == bot.ShipId || kv.Value.IsNotVisible) continue;
                if (kv.Value.SideId == bot.Side) continue;           // different side only
                if (kv.Value.SectorId != myCluster) continue;        // co-located
                if (kv.Value.Latest is not { } lu) continue;
                if (Showcase.EnemyWithinMineRange((lu.Position - myPos).Length()))
                { enemyNear = true; break; }
            }
        }
        if (!enemyNear) return;
        cs.SendFireExpendable(EquipmentType.Dispenser);
        bot.LastMineAt = now;
        Console.WriteLine($"[p557] bot {bot.Name} (ship {bot.ShipId}) fired mine dispenser (part {bot.MineDispenserPartId}) — enemy within {Showcase.WingmanMineRange}u (native shipIGC.cpp:2651)");
    }

    // ── P558 — native wingman ATTACK steering: pure decision helpers ──────
    //
    // The faithful port of the wingman's c_cidAttack branch
    // (shipIGC.cpp:2356-2604), replacing the P539 stand-off ring / lead-goal
    // for `attack`. Native turns to face the solved LEAD direction
    // (turnToFace → angleToLead), drives the throttle by the lead-angle law,
    // and spaces itself with DISTANCE BANDS keyed to the intercept time `t`
    // vs the projectile lifespan — not by a ring scaled to each side's own
    // weapon range (the P556 root cause). Factored out of the live
    // EngageShipAt wiring exactly like ComputePerf (P430): pure static
    // functions taking plain floats so every band, the throttle law and the
    // fire gate are provable with synthetic geometry and no server.

    /// <summary>openFireDistPercentage, shipIGC.cpp:2361 — the fraction of the
    /// projectile's lifespan that counts as "in range" for the fire gate and
    /// the engage-band edges.</summary>
    public const float AttackOpenFireDistPercentage = 0.95f;

    /// <summary>c_fMaxOffAngle, shipIGC.cpp:2476/2593 — the lead-aim cone: the
    /// nose must be within 0.10 rad of the lead before any weapon fires.</summary>
    public const float AttackMaxOffAngle = 0.10f;

    /// <summary>Native wingman skill factors (shipIGC.cpp:2359-2360). The MCP
    /// bots are wingman-pilot (not CheatPlayer, which would read its own
    /// members), so these fixed defaults apply.</summary>
    public const float AttackFShootSkill = 0.75f;
    public const float AttackFTurnSkill = 0.75f;

    /// <summary>The attack band a bot is in, from the intercept time vs the
    /// projectile lifespan (shipIGC.cpp:2492-2571). Each band spaces the bot
    /// differently relative to the target.</summary>
    public enum AttackBand { Close, Engage, Approach, Lost }

    /// <summary>Pick the attack band from the intercept time <c>t</c> vs the
    /// projectile lifespan. Native thresholds: <c>0.45·lifespan</c> (Close),
    /// <c>openFireDist−0.05 = 0.90·lifespan</c> (Engage), <c>1.5·lifespan</c>
    /// (Approach), beyond which the target has escaped (Lost).</summary>
    public static AttackBand BandOf(float t, float lifespan)
    {
        if (t < lifespan * 0.45f) return AttackBand.Close;
        if (t < lifespan * (AttackOpenFireDistPercentage - 0.05f)) return AttackBand.Engage;
        if (t < lifespan * 1.5f) return AttackBand.Approach;
        return AttackBand.Lost;
    }

    /// <summary>shipIGC.cpp:2474 — the throttle law: "make the drone always fly
    /// hard — throttle range 0.5 to 1.0, depending on how they are angled to
    /// their target": <c>0.5 + (π − angleToLead)/(2π)</c>.</summary>
    public static float AttackThrottle(float angleToLead)
        => 0.5f + (MathF.PI - angleToLead) / (2.0f * MathF.PI);

    /// <summary>shipIGC.cpp:2506-2511 — the band-Close "match speed" throttle
    /// that holds a constant speed equal to the target's speed along our
    /// forward: full forward if the target is at/over our max speed, full
    /// reverse if it is stopped/retreating, else linearly interpolated.</summary>
    public static float AttackMatchSpeedThrottle(float targetDirSpeed, float myMaxSpeed)
        => targetDirSpeed >= myMaxSpeed ? 1.0f
         : targetDirSpeed <= 0f ? -1.0f
         : 2.0f * targetDirSpeed / myMaxSpeed - 1.0f;

    /// <summary>shipIGC.cpp:2600 — the weapon FIRE GATE: allWeapons is set only
    /// when the nose is within <see cref="AttackMaxOffAngle"/> of the lead AND
    /// the intercept fits within <see cref="AttackOpenFireDistPercentage"/> of
    /// the projectile's lifespan (<c>angleToLead ≤ c_fMaxOffAngle &amp;&amp;
    /// t ≤ lifespan·openFireDistPercentage</c>).</summary>
    public static bool AttackFireGate(float angleToLead, float t, float lifespan)
        => angleToLead <= AttackMaxOffAngle && t <= lifespan * AttackOpenFireDistPercentage;

    /// <summary>The native attack branch's steering output for one tick.</summary>
    public readonly record struct AttackSteer(
        int StateBits,          // movement buttons (ShipControlState): fwd/back/left/afterburner
        bool CriticalMovement,  // gates the low-aim strafe/back-off (shipIGC.cpp:2589)
        bool Escape,            // Lost-band escape: caller retargets + burns (shipIGC.cpp:2545-2570)
        bool ThrottleOverride,  // band-Close matched speed sets the throttle axis
        float ThrottleValue);   // the match-speed throttle, when ThrottleOverride

    /// <summary>
    /// Solve the native attack band for one tick (shipIGC.cpp:2492-2596),
    /// producing the movement buttons, the critical-movement flag, and (in the
    /// band-Close "match speed" case) an explicit throttle override. This is
    /// the pure core of the P558 distance-band law; <paramref name="angleToLead"/>
    /// (radians) comes from turning to face the lead, <paramref name="t"/> from
    /// <c>solveForLead</c>, <paramref name="lifespan"/> from the mounted weapon.
    /// </summary>
    public static AttackSteer SolveAttackSteer(
        float angleToLead, float t, float lifespan,
        float relativeSpeed, float targetDirSpeed, float myMaxSpeed,
        bool strafe, bool inRangeAggressive, bool afterburnerHeld)
    {
        int state = 0;
        bool critical = false;
        bool escape = false;
        bool throttleOverride = false;
        float throttleValue = 0f;

        if (angleToLead < MathF.PI * 0.5f)
        {
            switch (BandOf(t, lifespan))
            {
                case AttackBand.Close:      // t < 0.45·lifespan
                    if (relativeSpeed < -20f)
                    {
                        state |= ShipControlState.ForwardButton;
                        if (relativeSpeed < -50f) critical = true;
                    }
                    else if (relativeSpeed > 20f)
                    {
                        state |= ShipControlState.BackwardButton;
                        if (relativeSpeed > 50f) critical = true;
                    }
                    else
                    {
                        throttleOverride = true;
                        throttleValue = AttackMatchSpeedThrottle(targetDirSpeed, myMaxSpeed);
                    }
                    break;
                case AttackBand.Engage:     // t < 0.90·lifespan
                    if (relativeSpeed > 50f)
                        state |= ShipControlState.BackwardButton;
                    else if (relativeSpeed < 10f)
                    {
                        state |= ShipControlState.ForwardButton;
                        if (relativeSpeed < -10f)
                        {
                            critical = true;
                            if (relativeSpeed < -15f && afterburnerHeld) state |= ShipControlState.AfterburnerButton;
                            else if (relativeSpeed < -30f) state |= ShipControlState.AfterburnerButton;
                        }
                        else if (t > lifespan * (AttackOpenFireDistPercentage - 0.1f))
                            critical = true;
                    }
                    break;
                case AttackBand.Approach:   // t < 1.5·lifespan
                    if (relativeSpeed > 120f)
                        state |= ShipControlState.BackwardButton;
                    else if (relativeSpeed < 80f)
                    {
                        state |= ShipControlState.ForwardButton;
                        if (relativeSpeed < 40f)
                        {
                            critical = true;
                            if (relativeSpeed < 20f) state |= ShipControlState.AfterburnerButton;
                        }
                    }
                    break;
                case AttackBand.Lost:       // target escaped
                    if (inRangeAggressive) escape = true;      // caller re-targets (shipIGC.cpp:2545-2565)
                    else state |= ShipControlState.AfterburnerButton;  // burn after it (:2570)
                    break;
            }
        }
        else if (!critical && t <= lifespan * AttackOpenFireDistPercentage)
        {
            // Low-aim but in reach: a strafing wingman holds lateral weave
            // (shipIGC.cpp:2590-2591). The inner `angleToLead < π/2` back-off
            // is unreachable here (we are in the else of that very test) —
            // ported verbatim for fidelity.
            if (strafe) state |= ShipControlState.LeftButton;
        }

        return new AttackSteer(state, critical, escape, throttleOverride, throttleValue);
    }

    // ── P539d — fair contact tuning ──
    /// <summary>How long a side's first-staged bot may hold before the side is
    /// forced to advance anyway (a straggler must not freeze the group forever;
    /// also covers a bot stuck unroutable that never stages).</summary>
    public const double StagingHoldSeconds = 15.0;
    /// <summary>P539d — the staging PHASE values on <see cref="ActiveBot.StagePhase"/>:
    /// 0 transit-to-stage, 1 holding (waiting on the wing), 2 released/advanced.</summary>
    public const byte StageTransitPhase = 0;
    public const byte StageHoldPhase = 1;
    public const byte StageReleasedPhase = 2;
    /// <summary>P637 — whether a staging phase is RELEASED (≥ the released phase):
    /// a released bot routes straight to its target instead of holding for its
    /// wing. A relaunched reinforcement is placed in the released phase so it
    /// never re-enters a barrier its wing already left.</summary>
    public static bool IsStageReleased(byte phase) => phase >= StageReleasedPhase;
    /// <summary>Horizon (units) for the cross-side approach avoidance: only a
    /// ship this close and inside the forward path cone is steered around.</summary>
    public const float AvoidHorizon = 200f;
    /// <summary>Cosine of the half-angle of the forward path-cone that counts as
    /// "in my way" for approach avoidance (≈ 30°).</summary>
    public const float AvoidCosHalfAngle = 0.860f;
    /// <summary>Extra lateral clearance added to both radii when steering around
    /// a ship in the path (units).</summary>
    public const float AvoidClearancePad = 12f;

    // ── P610 — bot fleet traffic spacing at alephs (large showcase fleets) ──
    /// <summary>Fraction of the aleph's capture radius a bot's entry aim point
    /// may sit at. Kept &lt; 1 so every aim stays INSIDE the transit sphere the
    /// bot must cross. Native places arrivals at a similar small offset
    /// (fsship.cpp:113-121, rand 2–5 u) but has no proactive fleet spread.</summary>
    public const float EntrySpreadFraction = 0.7f;
    /// <summary>Distance (units) ahead of an aleph at which the entry queue looks
    /// for a same-side teammate already in the funnel to yield to (P610).</summary>
    public const float EntryQueueLookahead = 340f;
    /// <summary>Lateral half-width (units) of the entry funnel band used by the
    /// queue (P610): a teammate inside this band, closer to the aleph than the
    /// bot and near the bot's own aim, triggers a yield.</summary>
    public const float EntryQueueFunnelWidth = 90f;
    /// <summary>Aleph exit clear-out: after crossing an aleph a bot keeps flying
    /// along the exit direction until this many units PAST the arrival aleph's
    /// radius before regrouping or engaging (P610). This is the bot-side analogue
    /// of native's exit velocity <c>−(speed + exitWarpSpeed)</c> along the exit
    /// <c>backward</c> (fsship.cpp:111-112) that carries emergent ships clear of
    /// the crowded mouth — our bots previously turned straight back into the
    /// arrival aleph to regroup, packing the cluster.</summary>
    public const float ExitClearMargin = 140f;
    /// <summary>Travel-separation influence range as a MULTIPLE of the bot's hull
    /// radius (P610): a same-side teammate closer than this many hull-radii gets
    /// a soft lateral push on the travel goal so the reactive dodge law rarely
    /// has to fire during transit.</summary>
    public const float TravelSpacingMultiple = 15f;
    /// <summary>Maximum lateral deflection (units) the travel-separation term may
    /// add to a travel goal (bounded so it can never swing the bot off course).</summary>
    public const float TravelSpacingStrength = 60f;
    /// <summary>Arena offset (units) of each mirrored spawn from the target
    /// sector centre along the facing axis.</summary>
    public const float ArenaOffset = 650f;

    /// <summary>
    /// P539 — solve the LEAD aim: the world-space unit direction to point the
    /// bot's fixed forward guns so a round travelling at
    /// <paramref name="projectileSpeed"/> intercepts a MOVING target, using the
    /// SHARED native lead solver <see cref="Allegiance.Core.Sim.ShipDodgeLaw.SolveForImpact"/>
    /// (the faithful port of native <c>solveForImpact</c>, <c>common.cpp:59-119</c>,
    /// that the wingman's <c>solveForLead</c> layers on, <c>shipIGC.cpp:2463-2468</c>;
    /// see <c>Ship.SolveForLead</c>, <c>Ship.cs:5051-5103</c> for the faithful
    /// formulation — target velocity used absolutely for an absolute projectile,
    /// else relative to the shooter). Returns <c>false</c> when no intercept
    /// exists (the round can never catch the target); the caller then does not
    /// fire.
    /// </summary>
    public static bool TrySolveLead(
        Vector3 myPos, Vector3 myVel,
        Vector3 targetPos, Vector3 targetVel,
        float targetRadius, float projectileSpeed, bool absoluteProjectile,
        out Vector3 aimDirection, out float interceptTime)
    {
        Vector3 deltaP = targetPos - myPos;
        Vector3 deltaV = absoluteProjectile ? targetVel : targetVel - myVel;   // common.cpp:144-146
        interceptTime = ShipDodgeLaw.SolveForImpact(deltaP, deltaV, projectileSpeed, targetRadius, out aimDirection);
        return aimDirection.LengthSquared() > 1e-9f;   // native's direction is unset on no-intercept
    }

    /// <summary>
    /// P539b — the friendly-in-line-of-fire gate: whether a FRIENDLY (same-side)
    /// ship lies inside the bot's line of fire ahead — within the weapon's real
    /// reach (<paramref name="reach"/>) and inside the narrow firing cone around
    /// the aim direction (angle &lt; <paramref name="cosHalfAngle"/> — default
    /// cosine 0.9975 ≈ 4°). When true the bot HOLDS FIRE, matching native
    /// wingman combat (a wingman doesn't shoot through a teammate; the server's
    /// friendly-fire gate <c>shipIGC.cpp:1222-1228</c> suppresses the damage
    /// anyway, but a round aimed into a friend wastes the shot and feeds the
    /// pack-up collision piles P539 measured). Pure: the caller gathers the
    /// candidate same-side ships (<see cref="SameSideShipsInLine"/>) and passes
    /// their positions — the math is unit-provable with no server.
    /// </summary>
    public static bool FriendlyInCone(
        Vector3 myPos, Vector3 aimDir, float reach,
        IEnumerable<(Vector3 Pos, short Id, short Side)> ships, short mySide,
        float cosHalfAngle = 0.996f)
    {
        foreach (var (pos, _, side) in ships)
        {
            if (side != mySide) continue;              // only same-side friends block
            var toFriend = pos - myPos;
            float distSq = toFriend.LengthSquared();
            if (distSq > reach * reach || distSq < 1e-4f) continue;
            float inv = 1f / MathF.Sqrt(distSq);
            float dot = (toFriend.X * aimDir.X + toFriend.Y * aimDir.Y + toFriend.Z * aimDir.Z) * inv;
            if (dot >= cosHalfAngle) return true;      // a friend is right in the aim cone
        }
        return false;
    }

    /// <summary>P539b — gather the candidate same-side ships that could lie in
    /// this bot's line of fire (its own sector, its own side, not itself), each
    /// as (position, id, side), for <see cref="FriendlyInCone"/> to test. Reads
    /// the bot's own ClientWorld under <c>cs.Gate</c>.</summary>
    private static IEnumerable<(Vector3 Pos, short Id, short Side)> SameSideShipsInLine(
        ConnectionSession cs, ActiveBot bot, Vector3 myPos, float reach)
    {
        short sector = CsSector(cs);
        if (bot.FriendlyGrids is { } grids && grids.TryGetValue(sector, out var grid))
            return grid.QueryRadius(myPos, reach).Where(s => s.Id != bot.ShipId)
                .Select(s => (s.Pos, s.Id, bot.Side));
        // A new sector or a scenario-owned bot can miss the pass cache. Copy
        // under the receive gate and filter directly; never build a grid per bot.
        var points = new List<BotSpatialIndex.ShipPoint>();
        lock (cs.Gate)
            foreach (var ship in cs.World.Ships.Values)
                if (ship.SideId == bot.Side && ship.SectorId == sector && !ship.IsDead && !ship.IsGone && ship.Latest is { } pose)
                    points.Add(new(ship.ShipId, pose.Position));
        return points.Where(s => s.Id != bot.ShipId
            && (s.Position - myPos).LengthSquared() <= reach * reach)
            .Select(s => (s.Position, s.Id, bot.Side));
    }

    private static void BuildPassSpatialIndices(ActiveBot[] bots)
    {
        foreach (var b in bots) b.FriendlyGrids = null;
        foreach (var side in bots.Where(IsParticipating).Where(b => !b.SpawnPending && !b.InScenario && b.Cs is not null).GroupBy(b => b.Side))
        {
            var cs = side.First().Cs!;
            var points = new Dictionary<short, List<BotSpatialIndex.ShipPoint>>();
            lock (cs.Gate)
                foreach (var ship in cs.World.Ships.Values)
                {
                    if (ship.SideId != side.Key || ship.IsDead || ship.IsGone || ship.Latest is not { } pose) continue;
                    if (!points.TryGetValue(ship.SectorId, out var list)) points[ship.SectorId] = list = new();
                    list.Add(new(ship.ShipId, pose.Position));
                }
            var grids = points.ToDictionary(p => p.Key, p => BotSpatialIndex.Build(p.Key, p.Value, 400f));
            foreach (var bot in side) bot.FriendlyGrids = grids;
        }
    }

    /// <summary>P553 — pure rule for the "the dodge owns the combat tick" fix:
    /// while the shared dodge fires during combat the bot keeps steering and
    /// throttling to the dodge goal and does NOT fire — native's combat drone
    /// "if (!bDodge)" (<c>shipIGC.cpp:2410</c>) and dodgeCooldown no-fire rule
    /// (<c>shipIGC.cpp:2485-2490</c>): keep firing only if native does. Clears
    /// the <see cref="ShipControlState.AllWeapons"/> fire bit, leaving boost and
    /// every other state bit intact.</summary>
    internal static int CombatStateWhileDodging(int stateM) => stateM & ~ShipControlState.AllWeapons;

    /// <summary>
    /// P539 — which LIFE-PHASE a combat bot is in: a bot whose own ship has
    /// become a LIFEPOD hull (the server swapped it on eject, keeping the same
    /// ship id — <c>Server.Combat.EjectSurvivor</c>, <c>FedSrv.CPP:11942</c>)
    /// is an unarmed survivor and must STOP combat and fly home to dock, never
    /// chase/ram. Pure decision over the observable inputs so the pod-vs-combat
    /// choice is unit-provable.
    /// </summary>
    public static bool DecidePodBehavior(bool ownHullIsLifepod) => ownHullIsLifepod;

    // ── P539d — fair contact: pure, unit-testable decision helpers ──

    /// <summary>
    /// P539d — how ONE death of a bot ship should be classified. A death is a
    /// <b>gunfire kill</b> when the death correlates with a KILL_SHIP event
    /// crediting a distinct ship (TypeCredit 0 = OT_ship) as the killer of this
    /// victim; otherwise (no credited ship shooter) it is a <b>collision/other</b>
    /// death (a ram / a crash, an eject without a shooter). Pure over a plain
    /// enumerable of (victimId, killerId, killerType) tuples so it is unit-provable
    /// with no server. A bot's own suicide/self-hit is not credited to another ship.
    /// </summary>
    public enum DeathCause { GunfireKill, CollisionOrOther }

    public static DeathCause ClassifyDeath(
        short victimId,
        IEnumerable<(short VictimId, short KillerId, short KillerType)> kills)
    {
        foreach (var (vid, kid, ktype) in kills)
        {
            if (vid == victimId && ktype == 0 && kid >= 0 && kid != victimId)
                return DeathCause.GunfireKill;
        }
        return DeathCause.CollisionOrOther;
    }

    /// <summary>
    /// P539d — the pure staging-release gate. A side advances together only when
    /// every LIVING bot on the side has reached its staging hold, or when the
    /// first-staged bot has waited past <paramref name="timeoutSeconds"/>. Dead /
    /// docked bots are excluded (they are not counted as "living"), and a side
    /// with no living bots releases trivially so a wiped side never deadlocks.
    /// Pure so the hold-vs-timeout decision is unit-provable.
    /// </summary>
    public static bool SideReadyToAdvance(
        int aliveCount, int stagedCount,
        double now, double? firstStagedAt, double timeoutSeconds)
    {
        if (aliveCount <= 0) return true;                      // nobody left to wait for
        if (stagedCount >= aliveCount) return true;            // fully assembled
        if (firstStagedAt is { } f0 && now - f0 >= timeoutSeconds) return true;  // timeout
        return false;
    }

    /// <summary>
    /// P539d — pure cross-side approach avoidance: among the ships given (their
    /// positions), if one lies within <paramref name="horizon"/> of the bot and
    /// inside the forward path-cone (cosine &ge; <paramref name="cosHalfAngle"/>)
    /// around the straight-line direction to <paramref name="target"/>, deflect
    /// the goal laterally by <paramref name="clearance"/> perpendicular to the
    /// path so the bot steers around it instead of ramming it (the same cone test
    /// the friendly-fire gate uses, applied to ALL ships incl. cross-side during
    /// the approach). The bot's own actively-engaged target is excluded (handled
    /// by the stand-off ring). Returns a deflected goal when a ship is in the way.
    /// </summary>
    public static bool AvoidShipsInPath(
        Vector3 myPos, Vector3 target, float horizon, float cosHalfAngle, float clearance,
        IEnumerable<(Vector3 Pos, short Id)> ships, short? engagedTargetId,
        out Vector3 goal)
    {
        goal = default;
        var toTarget = target - myPos;
        float dist = toTarget.Length();
        if (dist <= 1e-3f) return false;
        var dir = toTarget / dist;
        foreach (var (pos, id) in ships)
        {
            if (engagedTargetId is { } et && id == et) continue;   // stand-off handles the engaged target
            var toS = pos - myPos;
            float ds = toS.Length();
            if (ds > horizon || ds < 1e-4f) continue;
            float dot = (toS.X * dir.X + toS.Y * dir.Y + toS.Z * dir.Z) / ds;
            if (dot < cosHalfAngle) continue;                       // not in the forward path cone
            // Steer laterally around it: radial (toward-perpendicular) component, scaled to clearance.
            var radial = dir * ds - toS;                            // outward from the line
            if (radial.LengthSquared() < 1e-6f)
                radial = Vector3.CrossProduct(dir, new Vector3(0f, 1f, 0f));
            radial.Normalize();
            goal = myPos + dir * dist + radial * clearance;
            return true;
        }
        return false;
    }

    // ── P610 — bot fleet traffic spacing: pure, unit-testable decision helpers ──

    /// <summary>
    /// P610 — a deterministic per-bot aim point on the aleph's ENTRY DISC: a
    /// UNIFORM-DISC sample (radius·√u, angle 2πv seeded by <paramref name="shipId"/>)
    /// so a fleet streams through the mouth spread across its AREA instead of
    /// converging on the centre point. <paramref name="spreadFraction"/> (kept
    /// &lt; 1 by <see cref="EntrySpreadFraction"/>) scales the disc so every aim
    /// stays INSIDE the capture radius the bot must transit. Deterministic:
    /// the same ship always aims at the same point. The ring is laid out in the
    /// world X/Y plane — deliberately NOT tied to the per-tick approach direction,
    /// which would rotate the aim around the aleph as the bot closed on it. The
    /// transit threshold is a SPHERE (<c>|ship−warp| ≤ radius + shipRadius</c>,
    /// P496), so any point inside the sphere is reachable through the mouth.
    /// </summary>
    public static Vector3 EntryAimPoint(
        Vector3 alephCenter, float alephRadius, short shipId, float spreadFraction)
    {
        float r = Math.Clamp(spreadFraction, 0f, 0.998f) * alephRadius;
        uint x = (uint)shipId;
        x ^= x << 13; x ^= x >> 17; x ^= x << 5;      // cheap xorshift avalanche
        uint x2 = x ^ (x >> 13);
        float u = (x % 994901u) / 1000000f;            // 0..0.995 → √ for uniform disc
        float v = (x2 % 628318u) / 100000f;            // 0..2π
        float rr = r * MathF.Sqrt(Math.Clamp(u, 0f, 1f));
        float ang = v;
        return alephCenter + new Vector3(MathF.Cos(ang) * rr, MathF.Sin(ang) * rr, 0f);
    }

    /// <summary>
    /// P610 — whether a bot that just emerged is still too close to the arrival
    /// aleph to regroup: <c>false</c> until it is at least
    /// <paramref name="alephRadius"/> + <paramref name="margin"/> away (the
    /// native emergent offset is only radius+5, fsship.cpp:113-121 — our margin
    /// carries the bot clear of the crowded mouth before it turns around).
    /// </summary>
    public static bool IsClearOfAleph(Vector3 myPos, Vector3 alephPos, float alephRadius, float margin)
        => (myPos - alephPos).Length() >= alephRadius + margin;

    /// <summary>
    /// P610 — the EXIT-CLEAR-OUT goal: a point PAST the arrival aleph along the
    /// exit ray (through the aleph and the emergent bot), so pursuing it keeps
    /// the bot flying along the exit direction until it clears radius+margin.
    /// Mirrors native's exit velocity along <c>backward</c>
    /// (fsship.cpp:111-112). Degenerate (bot at the centre) falls back to the
    /// aleph itself, which the full-throttle Pursue then flies out of.
    /// </summary>
    public static Vector3 ExitClearGoal(Vector3 myPos, Vector3 alephPos, float alephRadius, float margin)
    {
        var exit = myPos - alephPos;
        float el = exit.Length();
        if (el < 1e-4f) return alephPos;
        var dir = exit / el;
        return alephPos + dir * (alephRadius + margin + 60f);
    }

    /// <summary>
    /// P610 — the soft TRAVEL-SPACING term: among the same-side teammates given
    /// (positions within <paramref name="range"/>), deflect <paramref name="goal"/>
    /// LATERALLY (perpendicular to the travel direction, so forward progress is
    /// never stalled) by a bounded amount that grows as a mate gets closer.
    /// Exactly zero when no mate is within range, so transit is unaffected at
    /// cruising separation. Combined with the reactive dodge law this keeps big
    /// fleets from grinding: the soft push spreads them before the dodge must fire.
    /// </summary>
    public static Vector3 ApplyTravelSeparation(
        Vector3 myPos, Vector3 goal,
        IEnumerable<(Vector3 Pos, short Id)> mates, float range, float strength)
    {
        var toGoal = goal - myPos;
        float d = toGoal.Length();
        if (d <= 1e-3f) return goal;
        var dir = toGoal / d;
        Vector3 lateral = Vector3.Zero;
        foreach (var (p, _) in mates)
        {
            var toMate = p - myPos;
            float dm = toMate.Length();
            if (dm <= 1e-4f || dm >= range) continue;
            float w = 1f - dm / range;                 // strongest when closest
            var away = -toMate / dm;                   // unit vector FROM the mate (push me away)
            // Project the away-vector onto the plane perpendicular to travel: the
            // forward component would stall progress — drop it entirely.
            float fwd = away.X * dir.X + away.Y * dir.Y + away.Z * dir.Z;
            var perp = away - dir * fwd;
            float pl = perp.Length();
            if (pl < 1e-6f) continue;                  // dead ahead/behind — no lateral push
            lateral += (perp / pl) * (strength * w);
        }
        if (lateral.LengthSquared() < 1e-8f) return goal;
        // Bound the total so it can never swing the bot off course.
        if (lateral.LengthSquared() > strength * strength)
            lateral *= strength / lateral.Length();
        return goal + lateral;
    }

    /// <summary>
    /// P610 — the light ENTRY QUEUE: whether a same-side teammate is already in
    /// the funnel JUST AHEAD of this bot (closer to the aleph, within a lateral
    /// band of the bot's line to the aleph, and within <paramref name="lookahead"/>
    /// of the aleph) and near this bot's own aim point, so this bot should deflect
    /// (yield) rather than pile onto the same converging point. Pure over the
    /// gathered funnel-mates so it is unit-provable without a server.
    /// </summary>
    public static bool EntryQueueYield(
        Vector3 myPos, Vector3 aim, Vector3 alephCenter, float alephRadius,
        IEnumerable<(Vector3 Pos, short Id)> funnelMates,
        float lookahead, float funnelWidth)
    {
        var toAleph = alephCenter - myPos;
        float dA = toAleph.Length();
        if (dA <= 1e-3f) return false;
        var line = toAleph / dA;
        foreach (var (p, _) in funnelMates)
        {
            var toMate = p - myPos;
            float dm = toMate.Length();
            if (dm <= 1e-3f || dm >= dA - 1e-3f) continue;   // must be strictly AHEAD of me
            // Lateral distance of the mate from my line-to-aleph.
            var along = toMate.X * line.X + toMate.Y * line.Y + toMate.Z * line.Z;
            if (along <= 0f) continue;                        // behind me
            var lat = toMate - line * along;
            if (lat.Length() > funnelWidth) continue;         // outside the funnel band
            // Near my own aim point (within the aim's distance to the aleph) → it IS
            // converging on the same mouth region as me.
            if ((p - aim).Length() <= alephRadius) return true;
        }
        return false;
    }

    /// <summary>
    /// P539d — pure mirrored arena placements: <paramref name="count"/> ships per
    /// side placed <paramref name="offset"/> on either side of the sector centre
    /// along <paramref name="axis"/>, each side spread along a perpendicular
    /// (<paramref name="spread"/>) lane so same-side ships don't stack on the same
    /// point. Side 0 faces +axis (toward side 1), side 1 faces −axis. Positions
    /// are equidistant from the centre and mirror images across the centre plane.
    /// </summary>
    public static (Vector3[] SideA, Vector3[] SideB, Vector3 FaceA, Vector3 FaceB) ComputeArenaPlacements(
        int count, Vector3 center, Vector3 axis, float offset, float spread)
    {
        var a = new Vector3[count];
        var b = new Vector3[count];
        axis.Normalize();
        // A perpendicular lane basis.
        var up = Math.Abs(axis.Y) > 0.9f ? new Vector3(1f, 0f, 0f) : new Vector3(0f, 1f, 0f);
        var laneX = Vector3.CrossProduct(axis, up); laneX.Normalize();
        var laneY = Vector3.CrossProduct(axis, laneX); laneY.Normalize();
        for (int i = 0; i < count; i++)
        {
            // Spread each bot along the two lanes by its index (alternating quadrants).
            float sx = (i % 2 == 0 ? 1f : -1f) * spread * (i / 2 + 1);
            float sy = (i % 4 >= 2 ? 1f : -1f) * spread * 0.6f;
            var lane = laneX * sx + laneY * sy;
            a[i] = center + axis * offset + lane;
            b[i] = center - axis * offset - lane;       // exact mirror across the centre plane
        }
        // Facing: side A sits at +axis, so it faces −axis (toward side B); side B at
        // −axis faces +axis (toward side A). They look at each other across the centre.
        return (a, b, -axis, axis);
    }

    /// <summary>Keep warp telemetry and arrival clearance on both objective and defence legs.</summary>
    private static void RecordCombatSectorTransition(ActiveBot bot, ConnectionSession cs, short cur, double now)
    {
        if (!bot.IsPod && bot.LastCluster >= 0 && cur != bot.LastCluster)
        {
            if (bot.WarpHops.Count == 0 || bot.WarpHops[^1].To != cur)
                bot.WarpHops.Add((now, bot.LastCluster, cur));   // cross-check vs server [warp] lines

            // P610 — remember the arrival aleph for EXIT CLEAR-OUT: the warp IN the
            // new sector whose partner's cluster is the sector we just left. After a
            // transit the bot keeps flying along the exit direction until it clears
            // this aleph's radius + margin (below), instead of turning straight back
            // into the crowded arrival mouth to regroup.
            bot.ExitClearFrom = null;
            bot.ExitClearRadius = 0f;
            lock (cs.Gate)
            {
                foreach (var w in cs.World.Warps.Values)
                {
                    if (w.ClusterId != cur) continue;
                    if (cs.World.Warps.TryGetValue(w.DestinationId, out var partner) && partner.ClusterId == bot.LastCluster)
                    {
                        if (w.Position is { Length: 3 } p)
                            bot.ExitClearFrom = new Vector3(p[0], p[1], p[2]);
                        bot.ExitClearRadius = w.Radius;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>Drive real combat, pod recovery and objective travel, temporarily
    /// interrupting travel for visible attackers or an active base defence.</summary>
    private void DriveCombat(ActiveBot bot, double now, PreparedEngagement? prep)
    {
        using var frameScope = new DecisionFrameScope(bot.Cs);
        var cs = bot.Cs!;
        if (!IsParticipating(bot) || !RefreshLife(bot, cs, now)) return;
        if (DecisionSnapshotEnabled && SnapshotFor(cs).MissionGeneration != cs.World.MissionGeneration) return;
        if (cs.World.MyShipId is not { } my) return;     // not seated yet
        bot.ShipId = my;

        // P539 — tally this bot's REAL combat stats (hits/kills) from its own
        // wire/world events each tick.
        SampleCombatStats(bot, cs, my, now);

        // A reseated ship must buy and launch before combat or pod routing.
        if (LaunchNextMission(bot, cs, now)) return;

        // P539 — a lifepod stops fighting and flies home to dock.
        if (UpdatePodState(bot, cs, now))
        {
            DrivePodHome(bot, cs, now);
            return;
        }

        short defenseSector = CsSector(cs);
        if (!bot.Arena && defenseSector >= 0) RecordCombatSectorTransition(bot, cs, defenseSector, now);
        // Leave the arrival volume before any engagement (including local
        // defence). Otherwise close-band matching can park the whole wing here.
        if (bot.ExitClearFrom is { } exit && TryCsPose(cs, bot.ShipId, out var exitPos, out var exitOrient)
            && !IsClearOfAleph(exitPos, exit, bot.ExitClearRadius, ExitClearMargin))
        {
            bot.TargetId = null;
            if (TryDodgeGoal(cs, bot, exitPos, out var dodge)) CombatFlyTo(bot, dodge, 15f, 200f, 0);
            else cs.CommandControls(AutopilotSteering.Pursue(exitPos, exitOrient,
                ExitClearGoal(exitPos, exit, bot.ExitClearRadius, ExitClearMargin)), 0);
            bot.LastCluster = defenseSector;
            return;
        }
        bot.ExitClearFrom = null;
        if (TryDriveFinaleEscort(bot, now)) return;
        if (defenseSector >= 0 && TryDriveDefense(bot, defenseSector, now)) return;
        // Fight what hits us or a friendly ship while travelling, without
        // manufacturing a station-defence incident in an empty battle sector.
        if (DefenseConfig.Enabled && !bot.Arena && !bot.Role.Equals("bomber", StringComparison.OrdinalIgnoreCase)
            && CombatTargetSelector.ClassifyTargetId(ActualHullId(bot)) is not (BotTargetClass.MinerDrone or BotTargetClass.Bomber)
            && HasVisibleAssetAttacker(bot, cs, defenseSector, now))
        {
            EngageCombat(bot, now, null);
            bot.LastCluster = defenseSector;
            return;
        }
        // Clear a temporary combat target before resuming travel.
        if (bot.TargetSector is { } objective && defenseSector != objective)
            bot.TargetId = null;

        if (bot.TargetSector is not { } goal) { EngageCombat(bot, now, prep); return; }

        // P539d — arena: both sides were relocated to mirrored spots in the target
        // sector at spawn; no travel, so engage in place (travel can't decide it).
        if (bot.Arena)
        {
            if (!bot.Arrived) { bot.Arrived = true; bot.ArrivedAt ??= now; }
            EngageCombat(bot, now, prep);
            return;
        }

        short cur = CsSector(cs);
        if (cur < 0) return;                             // own pose not streaming yet

        // P539d — arrival + warp-hop telemetry (a pod relaunch is not a warp hop).
        if (cur == goal && !bot.Arrived)
        {
            bot.Arrived = true;
            bot.ArrivedAt ??= now;
        }

        if (bot.Arrived && cur == goal) { EngageCombat(bot, now, prep); return; }

        if (string.Equals(bot.Role, "bomber", StringComparison.OrdinalIgnoreCase)
            && HasVisibleAttacker(bot, cs, cur, now))
        {
            EngageCombat(bot, now, prep);
            bot.LastCluster = cur;
            return;
        }

        // ── P539d — travel-with-staging: gather the whole side at a staging sector
        //    (the last sector before the rendezvous), hold just outside the final
        //    warp's transit sphere until the side is assembled (or the timeout),
        //    then the WHOLE side advances together through the final warp — so no
        //    side trickles into the other's settled position one ship at a time.
        if (bot.UseStaging)
        {
            if (bot.StagingSector < 0)
            {
                // P635 — a multi-route wing holds at ITS route's penultimate sector
                // (distinct entry aleph), not the shared shortest path's penult.
                if (bot.RoutePath is { Count: >= 2 } rp && rp[^1] == goal)
                    bot.StagingSector = rp[^2];
                else
                {
                    EnsureCombatPath(bot, cur, goal, now);
                    bot.StagingSector = bot.Path.Count >= 2 && bot.Path[^1] == goal ? bot.Path[^2] : cur;
                }
            }
            if (!IsStageReleased(bot.StagePhase))
            {
                EnsureCombatPath(bot, cur, bot.StagingSector, now);
                if (bot.Path.Count == 0) { LoiterCombat(bot, now); bot.LastCluster = cur; return; }
                short next = CombatNextHop(bot, cur);
                if (next != cur)
                {
                    // Still en route to the staging sector.
                    MaybeNarrateTransit(bot, cs, now, cur, bot.StagingSector, "stage");
                    if (!TransitWarp(cs, bot, cur, next)) LoiterCombat(bot, now);
                    bot.LastCluster = cur;
                    return;
                }
                // At the staging sector: mark staged, then HOLD (loiter in the
                // sector — dodge keeps it off rocks) until the whole living wing is
                // assembled here (P635: per-ROUTE, not per-side) or the timeout,
                // then the whole wing advances together through the final warp.
                // Loitering in-sector is robust (no reliance on a precise
                // warp-adjacent hold point) and preserves the fair-contact property:
                // no bot trickles into the rendezvous ahead of its wing's group.
                if (bot.StagePhase == StageTransitPhase) { bot.StagePhase = StageHoldPhase; bot.FirstStagingAt ??= now; }
                if (StageReleaseRequired(bot.Side, goal, bot.RouteIndex, now, missionCookie: cs.World.Mission?.Cookie))
                {
                    bot.StagePhase = StageReleasedPhase;
                    bot.ReleasedAt ??= now;
                    Console.WriteLine($"[p539d] side {bot.Side} wing r{bot.RouteIndex} bot {bot.Name} released from staging (wing assembled/timeout) — advancing together through final warp to {goal}");
                    // fall through to the normal transit below (this is the release tick)
                }
                else
                {
                    LoiterCombat(bot, now);                 // hold in the staging sector
                    if (now - bot.LastNarrateAt > 4)
                    {
                        bot.LastNarrateAt = now;
                        int alive, staged;
                        lock (_gate)
                        {
                            alive = _bots.Count(o => IsParticipating(o) && o.Cs is not null && o.Behavior == "combat" && o.UseStaging && o.TargetSector is { } og && og == goal && o.Side == bot.Side && o.RouteIndex == bot.RouteIndex && !o.IsPod && !o.PodDocked);
                            staged = _bots.Count(o => IsParticipating(o) && o.Cs is not null && o.Behavior == "combat" && o.UseStaging && o.TargetSector is { } og && og == goal && o.Side == bot.Side && o.RouteIndex == bot.RouteIndex && o.StagePhase >= StageHoldPhase && !o.IsPod && !o.PodDocked);
                        }
                        // P633 — the per-bot per-side staging narration is also
                        // verbosity-gated + non-blocking (per-bot every 4 s it
                        // is per-pass-volume noise, not a lifecycle line).
                        ConsoleLog.Write($"[p539d] STAGE bot {bot.Name} side={bot.Side} wing=r{bot.RouteIndex} sector={cur} phase={bot.StagePhase} alive={alive} staged={staged}");
                    }
                    bot.LastCluster = cur;
                    return;
                }
            }
            // released (StagePhase == 2) → fall through to the normal transit below.
        }

        EnsureCombatPath(bot, cur, goal, now);
        MaybeNarrateTransit(bot, cs, now, cur, goal, "transit");
        if (bot.Path.Count == 0) { LoiterCombat(bot, now); bot.LastCluster = cur; return; }   // unroutable / no CLUSTERINFO yet
        short gn = CombatNextHop(bot, cur);
        if (gn == cur) { bot.Arrived = true; bot.ArrivedAt ??= now; bot.LastCluster = cur; EngageCombat(bot, now, prep); return; }
        if (!TransitWarp(cs, bot, cur, gn))
            LoiterCombat(bot, now);
        bot.LastCluster = cur;
    }

    /// <summary>P539d — throttled [p433-diag]-style route-health narration so a
    /// live stall is diagnosable (preserved from P433; now covers the staging leg
    /// too, labelled by <paramref name="leg"/>).</summary>
    private void MaybeNarrateTransit(ActiveBot bot, ConnectionSession cs, double now, short cur, short goal, string leg)
    {
        if (now - bot.LastNarrateAt <= 20) return;
        bot.LastNarrateAt = now;
        lock (cs.Gate)
        {
            bool graphOk = cs.World.Graph is not null;
            int nWarps = cs.World.Warps.Count;
            short nextHop = CombatNextHop(bot, cur);
            bool destMap = TryBuildWarpDestMap(cs, out var dm);
            bool warpFound = nextHop != cur && destMap && TryFindWarpTo(cs, cur, nextHop, dm, out _, out _);
            // P633 — per-bot per-pass route-health narration goes through the
            // gated, non-blocking ConsoleLog sink (verbosity-gated OFF by
            // default; the last Mac run's 93%-[p433-diag] stderr was this
            // exact line, written synchronously through the MCP pipe). Lifecycle
            // lines elsewhere are untouched.
            ConsoleLog.Write($"[p433-diag] bot {bot.Name} ({leg}) cluster {cur} goal {goal} path[{bot.Path.Count}] next={nextHop} graph={graphOk} warps={nWarps} destMap={destMap} warpFound={warpFound}");
        }
    }

    /// <summary>
    /// P539d/P635/P637 — whether THE wing (<paramref name="side"/>, <paramref name="goal"/>,
    /// <paramref name="routeIndex"/>) has finished staging and may advance. A staging group
    /// is identified by (side, goal, routeIndex): the bots converging on the SAME target
    /// sector along the SAME route wing. P637 keys by <paramref name="goal"/> (the bot's
    /// own target) so a BOMBER (own goal = enemy home sector) is NEVER grouped with — or
    /// made to wait on — a fighter wing whose goal is the rendezvous, even if they share a
    /// route index. A wing releases when:
    ///   (a) its own living combat bots are all staged AND every OTHER group of the side
    ///       (that still has living bots, same goal) is also staged — the P635 synchronous-
    ///       arrival barrier, so all wings cross into the rendezvous together as ONE fight; or
    ///   (b) this wing's first-staged bot has waited past its timeout
    ///       (StagingHoldSeconds + a per-route sync offset that lets a LONGER route wait
    ///       longer, so its trailing bots have time to stage before it is forced out).
    /// The timeout overrides the barrier so a straggler/stuck wing never freezes the
    /// group forever. A wing with no living bots releases trivially. Single-threaded
    /// (called from the Loop); iterates the bot list under <c>_gate</c>.</summary>
    private bool StageReleaseRequired(short side, short goal, int routeIndex, double now, bool? syncOverride = null, uint? missionCookie = null)
    {
        if (Partition is not null)
            return !CoordinationHolding && FreshCoordination is { } decisions && decisions.Epoch == CoordinationEpoch
                && decisions.Releases.Contains(new StageKey(side, goal, routeIndex, missionCookie));
        // Per-group aggregate under _gate: (alive, staged, firstStagingAt, routeHops),
        // keyed by (side, goal, routeIndex) so distinct objectives never co-aggregate.
        var per = new Dictionary<(short Side, short Goal, int Route), (int Alive, int Staged, double? First, int Hops)>();
        lock (_gate)
            foreach (var o in _bots)
            {
                if (!IsParticipating(o) || o.SpawnPending || o.Cs is null || o.Behavior != "combat" || !o.UseStaging || o.TargetSector is not { } og) continue;
                if (o.Side != side || og != goal) continue;          // P637 — same objective only
                if (o.IsPod || o.PodDocked) continue;                // not living
                var key = (side, og, o.RouteIndex);
                (int Alive, int Staged, double? First, int Hops) zero = (0, 0, null, RouteHopsOf(o));
                var cur = per.TryGetValue(key, out var e) ? e : zero;
                int st = cur.Staged + (o.StagePhase >= StageHoldPhase ? 1 : 0);
                double? f = cur.First is { } oldFirst && o.FirstStagingAt is { } newFirst
                    ? Math.Min(oldFirst, newFirst) : cur.First ?? o.FirstStagingAt;
                per[key] = (cur.Alive + 1, st, f, cur.Hops);
            }

        if (!per.TryGetValue((side, goal, routeIndex), out var mine))
            return true;                                            // this wing has no living bots

        // Sync barrier: every OTHER group of the side (same goal) with living bots is assembled.
        bool allOthersAssembled = true;
        int sideMaxHops = 0;
        foreach (var ((rs, rg, ri), r) in per)
        {
            if (rs != side || rg != goal) continue;
            sideMaxHops = Math.Max(sideMaxHops, r.Hops);
            if (ri == routeIndex) continue;
            if (r.Alive > 0 && r.Staged < r.Alive) allOthersAssembled = false;
        }

        bool myAssembled = mine.Staged >= mine.Alive;
        double timeout = StagingHoldSeconds + SyncOffsetSeconds(routeIndex, sideMaxHops, mine.Hops);
        bool timedOut = mine.First is { } f0 && now - f0 >= timeout;

        // Domingo (2026-10-13): wings don't have to arrive together. By default each
        // wing advances as soon as IT is assembled (or times out); set
        // ALLEGIANCE_BOT_SYNC_WINGS=on to restore the P635 side-wide barrier.
        if (!(syncOverride ?? SyncWings)) return myAssembled || timedOut;
        return (myAssembled && allOthersAssembled) || timedOut;
    }

    /// <summary>P635 side-wide wing barrier switch; default OFF (independent wings).</summary>
    private static readonly bool SyncWings =
        string.Equals(Environment.GetEnvironmentVariable("ALLEGIANCE_BOT_SYNC_WINGS"), "on", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// P635 — the hop count of a bot's assigned route (edges = nodes − 1); 0 when
    /// un-routed (a single shortest-path route treats its penult as 1 hop from the
    /// rendezvous, but it never participates in a multi-wing sync, so 0 is fine).
    /// </summary>
    private static int RouteHopsOf(ActiveBot b) =>
        b.RoutePath is { Count: > 0 } rp ? rp.Count - 1 : 0;

    /// <summary>
    /// P635 — the per-route release-timeout offset compensates for the extra
    /// transit still needed by the widest route when a shorter wing stages first:
    /// <c>(sideWidestHops − routeHops) × SecondsPerHop</c>, clamped ≥ 0. A
    /// shorter route gets a larger window to wait for the longer wing; the widest
    /// route uses the base timeout. Pure so the timing is unit-provable.</summary>
    public static double SyncOffsetSeconds(int routeIndex, int sideWidestHops, int routeHops)
    {
        _ = routeIndex;                                    // (kept for call-site clarity)
        return Math.Max(0, (sideWidestHops - routeHops)) * RouteTransitSecondsPerHop;
    }

    /// <summary>
    /// P635 — approximate per-hop transit time (sec) used to stagger each wing's
    /// release timeout so longer routes wait longer. Sized from the measured
    /// bot-transit rate at the 200v200 baseline (bots cross a sector in tens of
    /// seconds at fighter cruise); conservative so the release offset never lets a
    /// short route arrive meaningfully ahead of a long one.
    /// </summary>
    public const double RouteTransitSecondsPerHop = 20.0;


    // ── Read-only visibility ──────────────────────────────────────────────

    /// <summary>One spawned bot's live, real state (read straight from its own /
    /// the probe's server-reported world on demand — no synthetic state).</summary>
    public sealed class ActiveBotView
    {
        public required string Name { get; init; }
        public required short ShipId { get; init; }
        public required short Side { get; init; }
        public required string Hull { get; init; }
        public required short Sector { get; init; }
        public required string Behavior { get; init; }
        public required double[] Position { get; init; }
        /// <summary>A combat bot's current engagement target ship id (null when
        /// idle/weave, or a combat bot not yet engaged).</summary>
        public required int? Target { get; init; }
        /// <summary>P431 — the specific enemy ship this bot is under a real
        /// accepted teammate order to engage (null = doing its own spawn-time
        /// behavior). Distinct from <see cref="Target"/> (the current live
        /// engagement) and from the spawn-time <see cref="Behavior"/>.</summary>
        public required short? OrderedTarget { get; init; }
        /// <summary>P622 — how many bots are CURRENTLY engaging this bot's
        /// <see cref="Target"/> (the last-completed engagement pass's engager
        /// count; 0 when the bot has no target or the target is uncontested).
        /// Lets a caller verify the anti-swarm cap live — a lone miner must show
        /// a small engager count, not the whole fleet.</summary>
        public required int Engagers { get; init; }
        // P634 — join→world-complete measurement (see EnsureCombatPath).
        /// <summary>Whether this bot's ClientWorld is world-complete (routing
        /// GRAPH present) right now.</summary>
        public required bool WorldReady { get; init; }
        /// <summary>Harness-clock seconds from launch (LaunchedAt) to when the
        /// world first became WorldReady; -1 if still not ready. The
        /// join→world-complete delay this task measures.</summary>
        public required double WorldReadyDelaySec { get; init; }
        /// <summary>How many backoffs the bot took waiting for the world sync
        /// (EnsureCombatPath found no cluster GRAPH yet).</summary>
        public required int WorldNotReadyAttempts { get; init; }
        // P635 — multi-route wings.
        /// <summary>This bot's wing/route index within its side (−1 = not on a
        /// multi-route wing; P635 routes=auto splits the side across routes).</summary>
        public required int RouteIndex { get; init; }
        /// <summary>A human-readable label for the bot's assigned route (e.g.
        /// "R1:Ganymede→Rigel→Alderan"); "single" when not on a multi-route wing.</summary>
        public required string RouteName { get; init; }
        // P637 — role → target → route → staging visibility.
        /// <summary>P637 — the bot's showcase role (Fighter/Bomber/Scout/...),
        /// so a caller can see which role got which target/route.</summary>
        public required string Role { get; init; }
        public string State { get; init; } = "active";
        public short? DefenseSector { get; init; }
        /// <summary>P637 — the sector this bot is ROUTING to (its own objective:
        /// the rendezvous for fighters, the enemy home sector for bombers).
        /// -1 when the bot has no target sector (engages in place).</summary>
        public required short TargetSector { get; init; }
        /// <summary>P637 — the sector this bot holds at while staging (−1 = not
        /// staging, or already released).</summary>
        public required short StagingSector { get; init; }
        /// <summary>P637 — the staging phase: 0 transit-to-stage, 1 holding,
        /// 2 released/advanced (a bot at home with phase 1 is still waiting on
        /// its wing).</summary>
        public required int StagePhase { get; init; }
        // P637 — live motion (scope 6).
        /// <summary>P637 — the bot's LIVE velocity (from its own ClientWorld,
        /// dead-reckoned regardless of the decision-snapshot flag).</summary>
        public required double[] Velocity { get; init; }
        /// <summary>P637 — scalar speed (velocity magnitude), for detecting idle.</summary>
        public required double Speed { get; init; }
        /// <summary>P637 — whether the bot is IDLE: speed ≈ 0 for longer than
        /// the idle window while not docked (a bot stuck at home holding shows
        /// idle=true so it can be counted live).</summary>
        public required bool Idle { get; init; }
    }

    private static bool BotWorldReady(ActiveBot bot)
    {
        if (bot.Cs is { } cs) { lock (cs.Gate) return cs.World.WorldReady; }
        return false;
    }

    private static double BotWorldReadyDelay(ActiveBot bot)
        => bot.WorldReadyAt < 0 ? -1 : bot.WorldReadyAt - bot.LaunchedAt;

    private static short ShipIdOf(ActiveBot bot)
    {
        if (bot.Bot is { } b && b.ShipAssignment is { } asn) return asn.Ship.ShipId;
        lock (bot.Cs!.Gate)
            return bot.Cs.World.MyShipId ?? -1;
    }

    /// <summary>P635 — a human-readable route label for a bot: "single" when not on
    /// a multi-route wing, else "RN:<start>→…→<goal>".</summary>
    private static string RouteNameOf(ActiveBot bot)
    {
        if (bot.RoutePath is not { Count: > 0 } rp || bot.RouteIndex < 0)
            return "single";
        return $"R{bot.RouteIndex}:" + string.Join("→", rp);
    }

    // ── P637 — idle detection ─────────────────────────────────────────────
    /// <summary>Below this scalar speed (m/s) a bot counts as "not moving".</summary>
    public const double IdleSpeedMps = 1.0;
    internal const double CombatStationarySeconds = 2.0;
    private readonly IdleTargetMetrics _idleTargetMetrics = new();

    private void SampleIdleTargets(ActiveBot[] bots, double now)
    {
        if (!_idleTargetMetrics.Due(now)) return;
        var idle = new List<string>();
        foreach (var bot in bots)
        {
            if (!IsParticipating(bot) || bot.SpawnPending || bot.TargetId is null) continue;
            bool docked = bot.PodDocked;
            if (bot.Cs is { } cs)
                lock (cs.Gate)
                    docked |= cs.World.Ships.TryGetValue(bot.ShipId, out var ship) && ship.StationId >= 0;
            if (TryBotOwnLiveMotion(bot, out _, out var velocity, out _)
                && IsBotIdle(docked, now, bot.LastMoveAt, velocity.Length())) idle.Add(bot.Name);
        }
        _idleTargetMetrics.Observe(now, idle);
    }
    /// <summary>A bot whose speed has been below the idle threshold (or with no
    /// observed movement) for this long, while NOT docked, is reported idle.</summary>
    public const double IdleWindowSec = 5.0;
    /// <summary>P637 — the pure idle rule: a bot is IDLE when it is NOT docked and
    /// has had no movement (speed below <see cref="IdleSpeedMps"/>) for longer than
    /// <see cref="IdleWindowSec"/>. Pure so the live "count the idle bots" flag is
    /// unit-provable.</summary>
    public static bool IsBotIdle(bool docked, double now, double lastMoveAt, double speed)
        => !docked && (now - lastMoveAt) > IdleWindowSec && speed < IdleSpeedMps;

    /// <summary>P637 — the bot's LIVE pose + velocity from ITS OWN ClientWorld's
    /// live entity (the fresh, snapshot-INDEPENDENT source: <c>e.Latest</c> is
    /// maintained by the receive thread even when the decision snapshot publish
    /// is OFF — the P636d default). This is the honest "where is it right now"
    /// for list_active_bots, avoiding a stale probe/snapshot pose. False when the
    /// bot has no Cs peer or its own ship isn't in its world yet.</summary>
    private static bool TryBotOwnLiveMotion(ActiveBot bot, out Vector3 pos, out Vector3 vel, out short sector)
    {
        pos = vel = Vector3.Zero;
        sector = -1;
        if (bot.Cs is not { } cs) return false;
        short sid = bot.ShipId >= 0 ? bot.ShipId : ShipIdOf(bot);
        if (sid < 0) return false;
        lock (cs.Gate)
        {
            if (cs.World.Ships.TryGetValue(sid, out var e) && e.Latest is { } lu)
            {
                pos = lu.Position;
                vel = lu.Velocity;
                sector = e.SectorId;
                return true;
            }
        }
        return false;
    }

    public IReadOnlyList<ActiveBotView> ActiveBots()
    {
        double now = Now();
        var views = new List<ActiveBotView>();
        lock (_gate)
        {
            foreach (var bot in _bots)
            {
                if (!IsParticipating(bot) || bot.SpawnPending) continue;
                try
                {
                    short shipId = ShipIdOf(bot);
                    // P637 — prefer the bot's OWN ClientWorld live pose + velocity
                    // (fresh regardless of the decision-snapshot flag), dead-reckoned
                    // honesty. The probe remains the fallback for a bot with no Cs.
                    Vector3 pos, vel = Vector3.Zero;
                    short sector;
                    bool own = TryBotOwnLiveMotion(bot, out pos, out vel, out sector);
                    if (!own) own = TryProbePose(shipId, out pos, out sector);
                    if (own)
                    {
                        double speed = vel.Length();
                        bool idle = IsBotIdle(bot.PodDocked, now, bot.LastMoveAt, speed);
                        views.Add(new ActiveBotView
                        {
                            Name = bot.Name, ShipId = shipId, Side = bot.Side, Hull = bot.HullName,
                            Sector = sector, Behavior = bot.Behavior,
                            Position = [pos.X, pos.Y, pos.Z], Target = bot.TargetId, OrderedTarget = bot.OrderedTargetId,
                            Engagers = EngagersOf(bot),
                            WorldReady = BotWorldReady(bot), WorldReadyDelaySec = BotWorldReadyDelay(bot),
                            WorldNotReadyAttempts = bot.WorldNotReadyAttempts,
                            RouteIndex = bot.RouteIndex, RouteName = RouteNameOf(bot),
                            Role = bot.Role, TargetSector = bot.TargetSector ?? -1,
                            State = bot.DefenseSector is not null || bot.DefendingInPlace ? "defending" : "active",
                            DefenseSector = bot.DefenseSector ?? (bot.DefendingInPlace ? sector : null),
                            StagingSector = bot.StagingSector, StagePhase = bot.StagePhase,
                            Velocity = [vel.X, vel.Y, vel.Z], Speed = speed, Idle = idle,
                        });
                        continue;
                    }
                    // Cross-side idle/weave (rare): report the assigned sector + a
                    // zeroed position honestly (the documented scope of visibility).
                    short s = bot.Bot is { } b2 && b2.ShipAssignment is { } a ? a.SectorId : (short)-1;
                    bool idle2 = IsBotIdle(bot.PodDocked, now, bot.LastMoveAt, 0.0);
                    views.Add(new ActiveBotView
                    {
                        Name = bot.Name, ShipId = shipId, Side = bot.Side, Hull = bot.HullName,
                        Sector = s, Behavior = bot.Behavior, Position = [0, 0, 0], Target = null, OrderedTarget = bot.OrderedTargetId,
                        Engagers = 0,
                        WorldReady = BotWorldReady(bot), WorldReadyDelaySec = BotWorldReadyDelay(bot),
                        WorldNotReadyAttempts = bot.WorldNotReadyAttempts,
                        RouteIndex = bot.RouteIndex, RouteName = RouteNameOf(bot),
                        Role = bot.Role, TargetSector = bot.TargetSector ?? -1,
                        State = bot.DefenseSector is not null || bot.DefendingInPlace ? "defending" : "active",
                        DefenseSector = bot.DefenseSector,
                        StagingSector = bot.StagingSector, StagePhase = bot.StagePhase,
                        Velocity = [0, 0, 0], Speed = 0, Idle = idle2,
                    });
                }
                catch { /* one bot's bad state never breaks list_active_bots */ }
            }
        }
        return views;
    }

    /// <summary>P622 — the current engager count for this bot's target (0 when
    /// it has none or the target is uncontested), from the last-completed
    /// engagement pass. Called under <c>_gate</c> (list_active_bots).</summary>
    private int EngagersOf(ActiveBot bot) =>
        bot.TargetId is { } tid ? (_engagersSnapshot.TryGetValue(tid, out var e) ? e : 0) : 0;

    // ── P539c — per-bot world-view diagnosis (read-only; the per-side repro) ──

    /// <summary>One combat bot's live world view as <em>that bot's own client</em>
    /// holds it — the exact data its <see cref="DriveCombat"/> branch reasons over:
    /// its own sector, the target-warp record it is flying toward (id / cluster /
    /// position / radius / destination as <c>cs.World.Warps</c> holds it), every
    /// different-side ship its world lists with its stamped <c>SectorId</c> +
    /// pose + last update tick (the P539c acquisition inputs), and its live pose.
    /// Used by the P539c per-side repro to contrast one bot's belief against the
    /// server's authoritative view and find a side-dependent divergence.</summary>
    public sealed record WorldShipDiag(short ShipId, short Sector, double[] Pos, long LastTick, bool Visible);
    public sealed record WarpDiag(short Id, short Cluster, double[] Pos, short Radius, short Dest);
    public sealed record BotWorldDiag(
        string Name, short ShipId, short Side, short Sector,
        double[] Pos, int Target, long OwnLastTick,
        List<WorldShipDiag> CrossSideShips,
        List<WarpDiag> SectorWarps,
        List<WarpDiag> AllWarps);

    public IReadOnlyList<BotWorldDiag> DiagnoseWorlds()
    {
        var outList = new List<BotWorldDiag>();
        ActiveBot[] snap;
        lock (_gate) snap = _bots.ToArray();
        foreach (var bot in snap)
        {
            if (bot.Cs is not { } cs) continue;
            var d = new BotWorldDiag(bot.Name, bot.ShipId, bot.Side, -1, [0, 0, 0],
                bot.TargetId ?? -1, -1, new List<WorldShipDiag>(), new List<WarpDiag>(), new List<WarpDiag>());
            lock (cs.Gate)
            {
                if (cs.World.Ships.TryGetValue(bot.ShipId, out var me))
                {
                    d = d with { Sector = me.SectorId, OwnLastTick = me.LastUpdateTick };
                    if (me.Latest is { } mu) d = d with { Pos = [mu.Position.X, mu.Position.Y, mu.Position.Z] };
                }
                foreach (var kv in cs.World.Ships)
                {
                    if (kv.Key == bot.ShipId) continue;
                    if (kv.Value.SideId == bot.Side) continue;
                    var e = kv.Value;
                    var p = e.Latest is { } lu ? new double[] { lu.Position.X, lu.Position.Y, lu.Position.Z } : new double[] { 0, 0, 0 };
                    d.CrossSideShips.Add(new WorldShipDiag(kv.Key, e.SectorId, p, e.LastUpdateTick, !e.IsNotVisible));
                }
                foreach (var w in cs.World.Warps.Values)
                {
                    var p = w.Position is { Length: 3 } fp ? new double[] { fp[0], fp[1], fp[2] } : new double[] { 0, 0, 0 };
                    var wd = new WarpDiag(w.WarpId, w.ClusterId, p, w.Radius, w.DestinationId);
                    if (w.ClusterId == d.Sector) d.SectorWarps.Add(wd);
                    d.AllWarps.Add(wd);
                }
            }
            outList.Add(d);
        }
        return outList;
    }

    // ── P539d — per-bot transit / arrival / death telemetry ─────────────────

    /// <summary>One combat bot's P539d fair-contact telemetry row: launch clock,
    /// arrival clock (first time in the target sector, travel mode), warp hops,
    /// and the death ledger (total / gunfire / collision-other) accumulated over
    /// the session. Read from the bot's live state on demand.</summary>
    public sealed record BotTransitRow(
        string Name, short Side, double LaunchedAt, double? ArrivedAt, int Hops,
        IReadOnlyList<(double T, short From, short To)> WarpHops,
        int Deaths, int GunfireDeaths, int CollisionDeaths);

    /// <summary>P553 — one dodge-law fire (the shared native law returned an
    /// imminent collision and a dodge goal): the bot, its sector, the wall-clock
    /// (UTC, seconds since epoch — the same clock a proof timestamps deaths
    /// with), the bot's position and the dodge goal it steered to instead.
    /// Read-only snapshot of <see cref="ActiveBot.DodgeFires"/>.</summary>
    public sealed record BotDodgeFire(
        short ShipId, short Sector, double WallUtc, Vector3 MyPos, Vector3 Goal);

    /// <summary>P553 — snapshot every bot's dodge-fire trace (locked, non-
    /// destructive) so a live proof can correlate collision deaths with active
    /// dodging. Sorted by wall clock.</summary>
    public IReadOnlyList<BotDodgeFire> DodgeTrace()
    {
        var all = new List<BotDodgeFire>();
        lock (_gate)
        {
            foreach (var b in _bots)
            {
                if (b.DodgeFires.Count == 0) continue;
                lock (b.DodgeGate) all.AddRange(b.DodgeFires);
            }
        }
        all.Sort((x, y) => x.WallUtc.CompareTo(y.WallUtc));
        return all;
    }

    public IReadOnlyList<BotTransitRow> TransitRows()
    {
        var rows = new List<BotTransitRow>();
        lock (_gate)
            foreach (var b in _bots)
                if (b.Cs is not null)
                    rows.Add(new BotTransitRow(b.Name, b.Side, b.LaunchedAt, b.ArrivedAt, b.WarpHops.Count,
                        b.WarpHops.ToArray(), b.Deaths, b.GunfireDeaths, b.CollisionDeaths));
        return rows;
    }

    // ── P635 — per-route arrival accounting ───────────────────────────────

    /// <summary>P635 — one wing's (side, route) tally: bots assigned to the wing and
    /// how many have ARRIVED in the rendezvous. Lets list_active_bots /
    /// get_perf_stats report per-route arrival counts so the multi-route split can be
    /// verified (each wing using its own entry aleph, each arrival counted per wing).</summary>
    public readonly record struct RouteArrivalRow(short Side, int RouteIndex, string RouteName, int Assigned, int Arrived);

    public IReadOnlyList<RouteArrivalRow> RouteArrivals()
    {
        var rows = new List<RouteArrivalRow>();
        lock (_gate)
            foreach (var g in _bots.Where(b => b.Behavior == "combat" && b.Cs is not null)
                                   .GroupBy(b => (b.Side, b.RouteIndex)))
            {
                var bots = g.ToList();
                rows.Add(new RouteArrivalRow(g.Key.Side, g.Key.RouteIndex, RouteNameOf(bots[0]),
                    bots.Count, bots.Count(b => b.Arrived)));
            }
        return rows;
    }

    // ── Real update-cadence tracking (P430) ───────────────────────────────
    //
    // "Teleporting" is a real symptom of the server's per-tick broadcast
    // falling behind at higher connection counts: the real wall-clock GAP
    // between successive real position updates grows, and/or the real DISTANCE
    // moved per update grows. Rather than reasoning about it, the Loop() tick
    // records a real (interval, distance) sample each time a bot's live
    // server-reported position actually changes, and get_perf_stats aggregates
    // those samples into mean / p95 / worst-offender numbers — measured
    // evidence for the concurrency sweet spot.

    /// <summary>Read a bot's CURRENT real position from the same authoritative
    /// source <c>list_active_bots</c> uses (the probe's world for a same-side
    /// bot; the bot's own ClientWorld for a cross-side combat bot).</summary>
    private bool TryCurrentPose(ActiveBot bot, out Vector3 pos)
    {
        short shipId = ShipIdOf(bot);
        if (TryProbePose(shipId, out pos, out _)) return true;
        if (bot.Cs is { } cs)
        {
            lock (cs.Gate)
                if (cs.World.Ships.TryGetValue(shipId, out var e) && e.Latest is { } lu)
                { pos = lu.Position; return true; }
        }
        pos = Vector3.Zero;
        return false;
    }

    /// <summary>Record ONE real cadence sample for a bot when its live position
    /// has changed since the last observation: the elapsed wall-clock seconds
    /// since the previous change (the real update interval the server is
    /// delivering to this bot's connection right now) and the real distance
    /// moved (meters). Kept in a bounded rolling list (see
    /// <see cref="MaxSamplesPerBot"/>) so long runs don't leak.</summary>
    private void SampleCadence(ActiveBot bot, double now)
    {
        if (!TryCurrentPose(bot, out var pos)) return;
        bool changed = bot.HasPos
            && (pos.X != bot.LastPos.X || pos.Y != bot.LastPos.Y || pos.Z != bot.LastPos.Z);
        if (changed)
        {
            double interval = now - bot.LastPosAt;
            double dist = (pos - bot.LastPos).Length();
            lock (bot.SampleGate)
            {
                bot.Samples.Add(new CadenceSample(interval, dist));
                if (bot.Samples.Count > MaxSamplesPerBot)
                    bot.Samples.RemoveRange(0, bot.Samples.Count - MaxSamplesPerBot);
            }
            bot.LastMoveAt = now;   // P637 — last observed movement (idle flag)
        }
        bot.LastPos = pos;
        bot.LastPosAt = now;
        bot.HasPos = true;
    }

    /// <summary>
    /// P598 — re-ready a live bot after a mission rotation. The server re-seats
    /// migrated peers on the rebuilt NotStarted mission with a FRESH ship id
    /// (new SET_CLUSTER); when this bot's current ship id differs from the last
    /// one it readied for, re-send PLAYER_READY(true) for the new ship so a
    /// bot-driven showcase's next wave auto-starts (native lobby flow: re-ready
    /// on the fresh NotStarted mission). MCP bots always intend to be ready, so
    /// re-readying on a ship-id change is always correct (on first seat it is a
    /// redundant no-op alongside the seat-time ready).</summary>
    private void ReReadyBot(ActiveBot bot)
    {
        if (!IsParticipating(bot)) return;
        if (bot.Cs is { } lifeCs && !RefreshLife(bot, lifeCs, Now())) return;
        short cur;
        if (bot.Cs is { } cs) { lock (cs.Gate) cur = cs.World.MyShipId ?? -1; }
        else if (bot.Bot is { } b) cur = b.ShipAssignment?.Ship.ShipId ?? -1;
        else return;
        if (cur < 0 || cur == bot.ReadyShipId) return;
        bot.ReadyShipId = cur;
        if (bot.Cs is { } cs2) cs2.SendPlayerReady(true, cur);
        else if (bot.Bot is { } b2) b2.SendPlayerReady(true, cur);
        Console.WriteLine($"[p598] re-readied bot '{bot.Name}' ship {cur} after rotation (PLAYER_READY true)");
    }

    internal BotCadence[] CadenceSamples(string? filter)
    {
        lock (_gate) return _bots.Where(b => filter is null || b.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Select(b => { lock (b.SampleGate) return new BotCadence(b.Name, b.Samples.ToArray()); }).ToArray();
    }

    /// <summary>Aggregate every tracked bot's rolling cadence samples into a real
    /// summary (optionally restricted to one bot by name substring): the number
    /// of contributing bots, and for update INTERVAL and JUMP DISTANCE each the
    /// pooled mean, nearest-rank p95 and the single worst offender (bot name +
    /// its worst single-sample value). Pure math over the recorded samples — no
    /// modeling, no guessing.</summary>
    public BotPerfReport GetPerfStats(string? botFilter)
    {
        var cadence = new List<BotCadence>();
        lock (_gate)
        {
            foreach (var bot in _bots)
            {
                if (botFilter is { Length: > 0 } && !bot.Name.Contains(botFilter, StringComparison.OrdinalIgnoreCase))
                    continue;
                CadenceSample[] arr;
                lock (bot.SampleGate) arr = bot.Samples.ToArray();
                cadence.Add(new BotCadence(bot.Name, arr));
            }
        }
        var core = ComputePerf(cadence);

        // P633 — the decision-loop metrics (pass time distribution, per-bot
        // decision rate, process resources, peak _gate hold, decode throughput)
        // measured live by the Loop. Copy the protected scalars under the
        // metrics gate so a concurrent pass never tears them.
        DecisionMetrics.PassStat pass;
        double wallPeriod, maxGate, decode;
        double snapPubPerSec, snapPubMsPerSec, dlMean, dlP95, dlMax;
        DecisionMetrics.ResourceSnapshot res;
        lock (_metricsGate)
        {
            pass = DecisionMetrics.SummarizePasses(_passTimesMs);
            wallPeriod = _lastWallPeriodMs;
            maxGate = _maxGateHoldMs;
            res = _lastResources;
            decode = _decodeMbPerSec;
            snapPubPerSec = _snapPublishPerSec;
            snapPubMsPerSec = _snapPublishMsPerSec;
            dlMean = _decodeLagMean; dlP95 = _decodeLagP95; dlMax = _decodeLagMax;
        }
        int bots = cadence.Count;
        double fleetRate = bots > 0 && wallPeriod > 0 ? bots / (wallPeriod / 1000.0) : 0.0;
        var snapAge = SnapAgeSummary();
        return new BotPerfReport(
            core,
            pass,
            // P636b — the effective per-bot decision rate is the real WALL cadence
            // (1 / period between pass starts), NOT 1000 / pass-work-ms. Feeding
            // the work time reported ~670 Hz−9 kHz even when the loop correctly
            // slept to ~20 Hz; the wall period is what the "≥ 15 Hz effective in
            // combat" target actually means.
            DecisionMetrics.DecisionRateHz(wallPeriod),
            fleetRate,                                  // fleet decisions per second
            res,
            maxGate,
            decode,
            snapAge,
            (snapPubPerSec, snapPubMsPerSec, dlMean, dlP95, dlMax),
            _stallTelemetry.Snapshot(),
            _idleTargetMetrics.Snapshot(Now(), botFilter));
    }

    // ── P539: real per-bot combat stats (shots / ship hits / kills / pods) ──

    /// <summary>P539 — one combat bot's REAL cumulative combat statistics,
    /// tallied from its own wire/world events, ALL attributed to this bot as the
    /// shooter (shots = real fired rounds; hits = the ones IT caused), plus its
    /// lifepod status. <see cref="BotCombatRow.HitRate"/> is enemy ship-hits ÷
    /// real shots-fired.</summary>
    public readonly record struct BotCombatRow(
        string Name, short Side, long ShotsFired, long ShipHits,
        long EnemyShieldHits, long EnemyHullHits, long FriendlyHits, long NoneHits,
        long Kills, double HitRate, bool IsPod, bool PodDocked)
    {
        public int PodRestarts { get; init; }
    }

    /// <summary>P539b — the per-side / overall aggregate of the combat stats and
    /// the lifepod outcome counts (pods that ever formed, reached a station, or
    /// were still lingering as pods). All hits are attributed to the shooter.</summary>
    public readonly record struct CombatSummary(
        IReadOnlyList<BotCombatRow> Bots,
        long ShotsFired, long ShipHits, long Kills, double OverallHitRate,
        long FriendlyHits, long EnemyShieldHits, long EnemyHullHits,
        int PodsCreated, int PodsDocked, int PodsLingering)
    {
        public int PodsRestarted { get; init; }
    }

    /// <summary>P539 — snapshot every bot's real combat stats (and per-side /
    /// overall aggregates + lifepod outcome counts) for get_perf_stats / the
    /// scenario summary.</summary>
    public CombatSummary GetCombatSummary()
    {
        var rows = new List<BotCombatRow>();
        int podsCreated = 0, podsDocked = 0;
        lock (_gate)
        {
            foreach (var bot in _bots)
            {
                if (bot.Cs is null) continue;   // combat bots only (idle/weave have no world)
                podsCreated += bot.Deaths;
                podsDocked += bot.PodsDockedTotal;
                double hr = bot.ShotsFired > 0 ? (double)bot.ShipHits / bot.ShotsFired : 0.0;
                rows.Add(new BotCombatRow(
                    bot.Name, bot.Side, bot.ShotsFired, bot.ShipHits,
                    bot.EnemyShieldHits, bot.EnemyHullHits, bot.FriendlyHits, bot.NoneHits,
                    bot.Kills, hr, bot.IsPod, bot.PodDocked) { PodRestarts = bot.PodsRestartedTotal });
            }
        }
        long shots = rows.Sum(r => r.ShotsFired), hits = rows.Sum(r => r.ShipHits), kills = rows.Sum(r => r.Kills);
        return new CombatSummary(
            rows, shots, hits, kills,
            shots > 0 ? (double)hits / shots : 0.0,
            rows.Sum(r => r.FriendlyHits),
            rows.Sum(r => r.EnemyShieldHits),
            rows.Sum(r => r.EnemyHullHits),
            podsCreated,
            podsDocked,
            rows.Count(r => r.IsPod && !r.PodDocked)) { PodsRestarted = rows.Sum(r => r.PodRestarts) };
    }

    /// <summary>Pure, unit-testable aggregation of a set of bots' rolling cadence
    /// samples into per-metric statistics (mean, nearest-rank p95, worst
    /// offender). Shares the same pool of samples a live <c>get_perf_stats</c>
    /// call is built from, so the math can be proven against a synthetic sample
    /// set with no server involved.</summary>
    public static PerfStats ComputePerf(IEnumerable<BotCadence> bots)
    {
        var intervals = new List<double>();
        var jumps = new List<double>();
        string worstIntervalBot = "";
        string worstJumpBot = "";
        double worstInterval = 0, worstJump = 0;
        bool haveInterval = false, haveJump = false;
        int contributing = 0;

        foreach (var b in bots)
        {
            if (b.Samples.Count == 0) continue;
            contributing++;
            double bi = double.NegativeInfinity, bj = double.NegativeInfinity;
            foreach (var s in b.Samples)
            {
                intervals.Add(s.Interval);
                jumps.Add(s.Distance);
                if (s.Interval > bi) bi = s.Interval;
                if (s.Distance > bj) bj = s.Distance;
            }
            if (!haveInterval || bi > worstInterval) { worstInterval = bi; worstIntervalBot = b.Name; haveInterval = true; }
            if (!haveJump || bj > worstJump) { worstJump = bj; worstJumpBot = b.Name; haveJump = true; }
        }

        return new PerfStats(
            contributing,
            Metric(intervals, worstIntervalBot),
            Metric(jumps, worstJumpBot));
    }

    /// <summary>Reduce a pooled value list to a mean / nearest-rank p95 / worst
    /// value, paired with the offender's name. Empty input yields a clean zero
    /// summary (no NaN / no divide-by-zero).</summary>
    private static MetricStat Metric(List<double> values, string worstBot)
    {
        if (values.Count == 0) return new MetricStat(0, 0, "", 0);
        values.Sort();
        double mean = values.Sum() / values.Count;
        double p95 = values[(int)Math.Min(values.Count - 1, Math.Max(0, Math.Ceiling(0.95 * values.Count) - 1))];
        return new MetricStat(mean, p95, worstBot, values[^1]);
    }

    // ── P432: run_hull_matrix — per-combination execution + lifecycle ────────
    //
    // The matrix tool's inner orchestration. One call runs ONE real (hull,
    // weapon, size) combination and returns its real captured stats. It spawns
    // `size` real 'combat' bots per side with that exact hull+weapon loadout,
    // lets the background behavior loop fight them for a real observation
    // window, captures real combat outcomes (via the same KILL_SHIP / ClientWorld
    // mechanism vizfight decodes) + the real per-bot update cadence, then CLEARS
    // exactly the bots it spawned — each matrix bot triggering its own real
    // disconnect (closing its ConnectionSession = a real peer logoff from the
    // server) — so the next combination starts from a clean board. This disposal
    // is self-contained to the matrix's own loop; spawn_bot / spawn_scenario
    // remain exactly as spawn-and-forget as before, and no general-purpose
    // despawn tool is exposed.

    /// <summary>One real (hull, weapon, size) combination's captured result:
    /// per-side spawn / kill / survivor counts (kills = real KILL_SHIP(105)
    /// events whose victim was one of this combination's own ships, the same
    /// mechanism vizfight decodes), time-to-first-kill from the combo start
    /// (null when no kill was observed in the window), the pooled real
    /// update-cadence stats (<see cref="PerfStats"/>) across this combo's bots,
    /// and (P473) the real granted hull per spawned bot + how many got a
    /// partial-grant warning.</summary>
    public readonly record struct MatrixComboResult(
        string HullName,
        string WeaponName,
        int Size,
        int SpawnedA,
        int KilledA,
        int SurvivedA,
        int SpawnedB,
        int KilledB,
        int SurvivedB,
        double? TimeToFirstKill,
        PerfStats Perf,
        IReadOnlyList<short> GrantedHullIds,
        int PartialGrants);

    /// <summary>Run one real combination against the live server: spawn
    /// <paramref name="size"/> 'combat' bots per side (the server's first two
    /// real sides) with the given hull + weapon loadout, observe them for
    /// <paramref name="observeSeconds"/> real seconds while the background
    /// behavior loop drives the combat, capture the real combat outcomes and
    /// update cadence, then clear exactly the bots it spawned so the next
    /// combination starts clean. Any genuine per-spawn failure aborts this combo
    /// (rolling back the bots already spawned) as a specific error — never a
    /// silent partial run.</summary>
    public MatrixComboResult RunCombination(
        short hullId, string hullName, short weaponPartId, string weaponName,
        int size, short? targetSector, double observeSeconds,
        short? launcherPartId = null, string? launcherRequestName = null)
    {
        if (!ServerReady)
            throw new InvalidOperationException("run_hull_matrix: server probe not connected/validated — cannot run a combination");
        var sides = ValidSides();
        if (sides.Count < 2)
            throw new InvalidOperationException($"run_hull_matrix: a combination needs two real sides; this server has: {string.Join(", ", sides)}");
        short sideA = sides[0], sideB = sides[1];
        // P433 — an optional real missile launcher rides the loadout, gated
        // against this hull's real capability mask (gun-only when it does not
        // fit, reported, never fabricated).
        var parts = Catalog.PartsForWeapon(weaponPartId, launcherPartId, hullId, hullName, launcherRequestName ?? "", out var launcherNote);
        if (launcherNote is not null)
            Console.WriteLine($"[p433] run_hull_matrix {hullName}+{weaponName} size {size}: {launcherNote}");

        // Spawn the whole combination (size per side). Names are unique, so we can
        // both attribute kills back to a side and clear exactly these bots later.
        var roster = new List<(string Name, short ShipId, short Side)>();
        // P473 — the REAL hull each spawned bot was granted + how many got a
        // partial-grant warning, surfaced in the per-combination row.
        var grantedHulls = new List<short>();
        int partialGrants = 0;
        double start = Now();
        try
        {
            for (int i = 0; i < size; i++)
            {
                var ra = SpawnBot(hullId, hullName, sideA, parts, "combat", targetSector, launcherPartId, launcherRequestName);
                roster.Add((ra.Name, ra.ShipId, ra.Side));
                grantedHulls.Add(ra.Granted.HullId);
                if (ra.Warning is not null) partialGrants++;
                var rb = SpawnBot(hullId, hullName, sideB, parts, "combat", targetSector, launcherPartId, launcherRequestName);
                roster.Add((rb.Name, rb.ShipId, rb.Side));
                grantedHulls.Add(rb.Granted.HullId);
                if (rb.Warning is not null) partialGrants++;
            }
        }
        catch (Exception ex)
        {
            ClearBots(roster.Select(r => r.Name).ToList());   // roll back a partial spawn
            throw new InvalidOperationException(
                $"run_hull_matrix: combo {hullName}+{weaponName} size {size} failed to spawn fully after {roster.Count} bot(s): {ex.Message}");
        }

        // Observe the real fight for the fixed window: poll every real 250ms for
        // KILL_SHIP events on any of this combo's own ships. The background
        // behavior loop does the driving; this thread only watches and times.
        var myIds = new HashSet<short>(roster.Select(r => r.ShipId));
        var killed = new HashSet<short>();
        double? firstKillSec = null;
        var deadline = start + observeSeconds;
        while (Now() < deadline)
        {
            ScanComboKills(myIds, killed, ref firstKillSec, start);
            Thread.Sleep(250);
        }

        int spawnedA = roster.Count(r => r.Side == sideA);
        int spawnedB = roster.Count(r => r.Side == sideB);
        int killedA = roster.Count(r => r.Side == sideA && killed.Contains(r.ShipId));
        int killedB = roster.Count(r => r.Side == sideB && killed.Contains(r.ShipId));

        var perf = ComboPerf(roster.Select(r => r.Name).ToList());

        // Clear exactly this combination's bots (each triggers its own real
        // disconnect) before the caller runs the next combination.
        ClearBots(roster.Select(r => r.Name).ToList());

        return new MatrixComboResult(
            hullName, weaponName, size,
            spawnedA, killedA, spawnedA - killedA,
            spawnedB, killedB, spawnedB - killedB,
            firstKillSec, perf, grantedHulls, partialGrants);
    }

    /// <summary>Poll every combat bot this combination spawned for real
    /// KILL_SHIP(105) events whose victim is one of the combination's own ships —
    /// the exact same kill mechanism vizfight decodes (each real connected peer
    /// receives the server's same-sector KILL_SHIP broadcasts via its own
    /// ClientWorld). Dedupe by victim ship id; latch the first kill's real
    /// wall-clock offset. A victim ejected-but-pod-alive is NOT (yet) a kill —
    /// faithful to the P107 eject mechanic (a player ship must be a lifepod / be
    /// finished as a pod before it produces a real KILL_SHIP).</summary>
    private void ScanComboKills(HashSet<short> myIds, HashSet<short> killed, ref double? firstKillSec, double start)
    {
        ActiveBot[] snap;
        lock (_gate) snap = _bots.Where(b => b.Cs is not null).ToArray();
        foreach (var bot in snap)
        {
            KillShip[] kills;
            lock (bot.Cs!.Gate) kills = bot.Cs.World.Events.Kills.ToArray();
            foreach (var k in kills)
            {
                if (!myIds.Contains(k.ShipId)) continue;   // a kill of OUR bot only
                if (killed.Add(k.ShipId))
                    firstKillSec ??= Now() - start;
            }
        }
    }

    /// <summary>Aggregate the real update-cadence samples of exactly this
    /// combination's bots (the same pure math get_perf_stats uses).</summary>
    private PerfStats ComboPerf(IReadOnlyCollection<string> names)
    {
        var cadence = new List<BotCadence>();
        lock (_gate)
        {
            foreach (var bot in _bots)
            {
                if (!names.Contains(bot.Name)) continue;
                CadenceSample[] arr;
                lock (bot.SampleGate) arr = bot.Samples.ToArray();
                cadence.Add(new BotCadence(bot.Name, arr));
            }
        }
        return ComputePerf(cadence);
    }

    /// <summary>
    /// P432 — remove + dispose EXACTLY the named bots, so the hull/weapon matrix
    /// can clear one combination's own bots before the next. Each removed combat
    /// bot triggers its own real disconnect (closing its ConnectionSession = a
    /// real peer logoff from the server); idle/weave plain clients are disposed
    /// the same way. Self-contained to the matrix orchestration: it is NOT
    /// exposed as a general-purpose despawn tool, and spawn_bot / spawn_scenario
    /// callers are unaffected (their bots are simply never handed to this).
    /// </summary>
    private void ClearBots(IReadOnlyCollection<string> names)
    {
        if (names is null || names.Count == 0) return;
        var nameSet = new HashSet<string>(names);
        ActiveBot[] toDispose;
        lock (_gate)
        {
            toDispose = _bots.Where(b => nameSet.Contains(b.Name)).ToArray();
            _bots.RemoveAll(b => nameSet.Contains(b.Name));
        }
        foreach (var bot in toDispose)
        {
            try { DisposePeer(bot.Bot); } catch { }
            try { DisposePeer(bot.Cs); } catch { }
        }
    }

    public void Dispose()
    {
        _loopCts.Cancel();
        if (_loopThread.IsAlive) _loopThread.Join(500);
        ActiveBot[] bots;
        lock (_gate) { bots = _bots.ToArray(); _bots.Clear(); }
        foreach (var bot in bots)
        {
            try { DisposePeer(bot.Bot); } catch { }
            try { DisposePeer(bot.Cs); } catch { }
        }
        lock (_commanders)
        {
            foreach (var c in _commanders.Values) { try { DisposePeer(c); } catch { } }
            _commanders.Clear();
        }
        DisposePeer(Probe);
        _stallTelemetry.Dispose();
    }

    private void DisposePeer(IDisposable? peer) => _stallTelemetry.Close(peer);

    /// <summary>
    /// The per-bot bookkeeping. Exactly one of <see cref="Bot"/> (idle/weave — a
    /// plain client) and <see cref="Cs"/> (combat — its own ClientWorld) is set.
    /// The combat fields mirror vizfight's <c>AttachEnemyBot</c> exactly (same
    /// names/shapes: TargetId, LastTargetAt/BoostAt/CloakAt, Path/PathStart/
    /// PathGoal/LastCluster, WarpDest, Arrived/Launched).
    /// </summary>
    private sealed class ActiveBot
    {
        public ActiveBot(string name, short hullId, string hullName, short side, short requestedSide, string behavior, double phase)
        {
            Name = name; HullId = hullId; HullName = hullName;
            Side = side; RequestedSide = requestedSide; Behavior = behavior;
            WeavePhase = phase;
            WeaveFreq = 0.033 + Random.Shared.NextDouble() * 0.009;
            WeaveAmp = 0.024 + Random.Shared.NextDouble() * 0.015;
        }

        // The wire peer — exactly one is non-null (see the class doc).
        public BotClass? Bot;
        public ConnectionSession? Cs;

        public string Name;
        public short HullId;
        public string HullName;
        public short Side;
        public short RequestedSide;
        public string Behavior;

        // weave seed (idle/weave + combat loiter).
        public double WeavePhase;
        public double WeaveFreq;
        public double WeaveAmp;

        // ── combat state (mirrors vizfight's AttachEnemyBot) ──
        public short ShipId = -1;
        // P598 — the ship id this bot last sent PLAYER_READY(true) for. When it
        // changes (the server re-seats a migrated peer on the rebuilt mission
        // after a game-over rotation with a fresh ship id), the MCP re-readies
        // the bot so bot-driven showcases keep auto-starting.
        public short ReadyShipId = -1;
        public bool MissionLaunchPending;
        public bool Launched;
        public bool Arrived;                       // reached the target sector
        // P634 — world-sync backoff + measurement. The cluster GRAPH is the
        // routing prerequisite (ClientWorld.Graph ← CLUSTERINFO). Until it
        // lands the bot cannot path; it must not hammer EnsureCombatPath every
        // tick re-requesting a route (the P633 ~224/390 home-bound symptom).
        //   WorldNotReadyUntil — harness-clock (sec, Now()) before which the
        //     bot may NOT re-attempt routing (backoff window).
        //   WorldNotReadyAttempts — how many backoffs it has taken (the
        //     "EnsureCombatPath retry count" measurement, scope 1).
        //   WorldReadyAt — harness-clock when the graph first arrived (-1 until).
        public double WorldNotReadyUntil = double.NegativeInfinity;
        public int WorldNotReadyAttempts;
        public double WorldReadyAt = -1;
        public short? TargetId;                    // the enemy ship this bot is engaging
        // P622 — enemies that have recently DAMAGED this bot (source ship id →
        // wall-clock of the last hull hit THEY caused on US), refreshed each tick
        // from this bot's real hull-hit stream in SampleCombatStats. Drives the
        // "threats first" priority: a ship shooting at me is engaged before idle
        // combat ships or miners.
        public readonly Dictionary<short, double> RecentAttackers = new();
        public readonly Dictionary<short, double> RecentAssetAttackers = new();
        public readonly Dictionary<(short Station, short Attacker), double> RecentStationAttackers = new();
        public long DefenseChatCursor;
        public short? DefenseSector;
        public bool DefendingInPlace;
        public HashSet<short> DefenseTargets = new();
        public double LastAttackersPruneAt = double.NegativeInfinity;
        public double LastTargetAt = double.NegativeInfinity;
        public double LastBoostAt = double.NegativeInfinity;
        public double LastCloakAt = double.NegativeInfinity;
        public double LastNarrateAt = double.NegativeInfinity;
        // P539 — the bot's LEAD weapon part id (the first real weapon in its
        // loadout), used by the fire loop to read the real projectile
        // ballistics it leads with. null = unarmed (no lead/fire).
        public short? WeaponPartId;
        // P539 — lifepod state: whether the bot's own ship is currently a pod,
        // the home station it chose, and whether the pod reached (docked at) a
        // station — the live "pods reached home vs lingered" measure.
        public ConnectionSession? LifeSession;
        public long LifeGeneration;
        public uint? LifeCookie;
        public string? ObjectiveName;
        public bool ObjectivesPending;
        public int RelaunchAttempts;
        public double RelaunchAt = double.NegativeInfinity;
        public bool RelaunchAckHandled;
        public bool RelaunchExhausted;
        public int PodsDockedTotal;
        public int PodsRestartedTotal;
        public long PodOutcomeCursor;
        public bool PodRestarted;
        public bool SpawnPending = true;
        public bool Inactive;
        public Dictionary<short, BotSpatialIndex>? FriendlyGrids;
        public DecisionSnapshot? DecisionFrame;
        public bool IsPod;
        public bool EverPod;                         // has this bot ever become a pod
        public short PodRouteSector = -1, PodRouteWarpId = -1;
        public int PodRouteWarpCount;
        public short PodStationId = -1;
        public short PodStationSector = -1;
        public Vector3 PodStationCenter;
        public bool PodDocked;
        public short PodDockStationId = -1;
        // P539b — REAL combat stats, all attributed to THIS bot as the shooter:
        // ShotsFired = this bot's OWN fired rounds (PROJ_INST echoes carrying its
        // ship as the launcher), NOT a fire decision; ShipHits = the damaging
        // hits on ENEMY ships THIS bot caused (the meaningful hit-rate
        // numerator); plus finer splits by the struck target's side (friendly =
        // friendly fire, none = asteroid / no ship) and by shield vs hull.
        public long ShotsFired;
        public long ShipHits;            // enemy damaging hits by this bot
        public long EnemyShieldHits;     // …of those, absorbed by the target's shield
        public long EnemyHullHits;       // …of those, hull-or-worse
        public long FriendlyHits;        // friendly-fire hits by this bot
        public long NoneHits;            // hits by this bot on a no-side/no-ship target (-1)
        public long Kills;
        public long KillCursor;
        // P433 — the real missile launcher this bot carries (null = gun-only),
        // the real inter-shot readiness delay of its missile, and the wall-clock
        // of the last SendFireMissile() (paces firing like the guns' refire).
        public short? LauncherPartId;
        public double MissileReadyTime;
        public double LastFireAt = double.NegativeInfinity;
        // P557 — the real MINE DISPENSER this bot carries (null = none). Set
        // when a showcase/fleet slot equipped a mine-type dispenser (the native
        // ET_Dispenser whose expendable is OT_mineType); gates MaybeFireMine.
        public short? MineDispenserPartId;
        // P557 — the native WingmanBehaviourBitMask (igc.h:785-794): which of
        // UseMines / UseMissiles / Strafe this bot obeys in combat (scenario
        // default = DefaultScenarioWingman; override per fleet entry / bot).
        public short WingmanBehaviour = Showcase.DefaultScenarioWingman;
        /// <summary>P637 — the bot's showcase role ("fighter"/"bomber"/...,
        /// see <see cref="Showcase.HullRole"/>). Determines its OWN target sector
        /// (bombers → enemy home) and its route/staging group.</summary>
        public string Role = "fighter";
        public int CompletedPodCycles;
        public FinaleRun? Finale;
        public FinaleOriginal? FinaleOriginal;
        public bool FinaleBomberReserved, FinaleBomberActive, FinaleEscort, FinaleResetObserved, FinaleGrantVerified;
        public double? FinaleProtectedSince;
        /// <summary>P558 — whether the PREVIOUS attack tick held the
        /// afterburner button. Native's band-Engage afterburner rule fires
        /// afterburners at relativeSpeed &lt; −15 only if the drone was already
        /// afterburning, else only below −30 (shipIGC.cpp:2522-2525); this
        /// latched bit is the <c>StateM &amp; afterburnerButtonIGC</c> read.</summary>
        public bool AttackAfterburnerHeld;
        public double LastMineAt = double.NegativeInfinity;
        public short? TargetSector;                // rendezvous sector, or null to engage in place
        public List<short> Path = new();
        public short PathStart = -1;
        public short PathGoal = -1;
        public short LastCluster = -1;
        public Dictionary<short, short>? WarpDest;

        // ── real update-cadence tracking (P430) ──
        // Mirrors the WeavePhase/Freq/Amp inline-field style: a small, bounded
        // per-bot ring of REAL (interval, distance) samples, recorded whenever
        // the bot's live server-reported position actually changes, so
        // get_perf_stats can answer "is this bot's update stream thinning /
        // teleporting?" with measured evidence instead of a guess. `SampleGate`
        // guards the list (loop thread writes it, get_perf_stats reads it).
        public bool HasPos;                  // a live position has been observed at least once
        public Vector3 LastPos;              // last-observed real position
        public double LastPosAt;             // wall-clock time of the last observed change
        /// <summary>P637 — wall-clock (harness Now()) of the last observed MOVEMENT
        /// (speed above the idle threshold). Used to flag a bot idle in
        /// list_active_bots.</summary>
        public double LastMoveAt = double.NegativeInfinity;
        public readonly object SampleGate = new();
        public List<CadenceSample> Samples = new();

        // ── accepted-order bookkeeping (P431) — inline-field style like the rest ──
        // The loadout this bot launched with (re-used when an idle bot is upgraded
        // to a combat client).
        public IReadOnlyList<ExpandedPartData>? Parts;
        // P473 — the loadout the server actually granted at spawn (from the real
        // BUY_LOADOUT_ACK) and any partial-grant warning (null = full grant).
        // Kept so an upgraded/listed bot retains the honest granted record.
        public LoadoutGrant Granted;
        public string? GrantWarning;
        // A BotClass bot's current pending queued order, folded from its
        // QUEUED_COMMAND_UPDATE(215) accumulator (Cs bots read the live
        // cs.World.MyQueuedCommand instead); QcuConsumed is the fold cursor.
        public QueuedCommandState? CurrentQueued;
        public int QcuConsumed;
        // The queued order id this bot has already accepted (prevents re-accepting
        // while the server clears the slot); reset when the slot empties.
        public short AcceptedQueuedId = OrderChange.CidNone;
        // The specific enemy ship this bot is under a real accepted teammate order
        // to engage (null = doing its own spawn-time behavior). Distinct from the
        // spawn-time `Behavior` and from `TargetId` (the current live engagement).
        public short? OrderedTargetId;
        // P474 — true while this bot is owned by an active run_scenario preset's
        // controller thread; the generic Loop then leaves it alone entirely.
        public bool InScenario;

        // ── P539d — fair contact: transit/arrival telemetry ──
        public double LaunchedAt;                       // wall-clock Now() at spawn
        public List<(double T, short From, short To)> WarpHops = new();  // real cluster transitions in transit
        public double? ArrivedAt;                       // first time in the target sector (travel mode)
        public int Deaths;                              // times this bot's ship ejected (became a lifepod)
        public int GunfireDeaths;                       // of those, killed by a credited ship shooter
        public int CollisionDeaths;                     // of those, eject without a credited shooter (ram/other)

        // ── P553 — dodge-fire telemetry ──
        // Every tick where the SHARED native dodge law (<see cref="TryDodgeGoal"/>)
        // fired for this bot while navigating (transit/stand-off/engage), recorded
        // so a live proof can correlate a collision death with whether the bot was
        // actively dodging at the time. Guarded by <see cref="DodgeGate"/> (the
        // BotManager loop thread writes, a proof reads). Non-behavioural.
        public readonly object DodgeGate = new();
        public List<BotDodgeFire> DodgeFires = new();

        // ── P539d — travel-with-staging mode ──
        public bool UseStaging;                         // gather the whole side before advancing
        public byte StagePhase;                         // 0 transit-to-stage, 1 holding, 2 released/advanced
        public short StagingSector = -1;                // the last sector before the rendezvous
        public double? FirstStagingAt;                  // when this bot first began holding
        public double? ReleasedAt;                      // when the side released it to advance

        // ── P635 — multi-route wings ──
        // The assigned wing route (home → rendezvous, inclusive) this bot follows
        // instead of the shared BFS shortest path, so each side splits across ALL
        // its distinct entry alephs into the rendezvous instead of funnelling every
        // ship through one mouth. Null = single-route behaviour (FindShortestPath).
        public List<short>? RoutePath;
        /// <summary>P635 — this bot's wing index within its side (−1 when not on a
        /// multi-route wing). Routes 0..N-1; each route has its OWN staging sector,
        /// staging/entry-stagger/exit-clear-out instance and release gate.</summary>
        public int RouteIndex = -1;

        // ── P610 — aleph exit clear-out ──
        public Vector3? ExitClearFrom;                  // the arrival aleph position to clear after a transit
        public float ExitClearRadius;                   // its wire radius (DataWarpIGC.Radius)

        // ── P539d — arena mode ──
        public bool Arena;                              // starts engaged in the target sector (mirrored spots)
        public Vector3 ArenaPosition;                   // mirrored arena spot
        public Vector3 ArenaFacing;                     // facing the opposing side's mirrored spot
    }
}

/// <summary>P430 — one real cadence observation for a bot: the wall-clock
/// interval in seconds since its previous position update, and the real
/// distance (meters) it moved in that update.</summary>
public readonly record struct CadenceSample(double Interval, double Distance);

/// <summary>P430 — a bot's bounded rolling cadence sample list, as fed to
/// <see cref="BotManager.ComputePerf"/> for aggregation.</summary>
public readonly record struct BotCadence(string Name, IReadOnlyList<CadenceSample> Samples);

/// <summary>P430 — one metric's aggregate over all pooled samples: the mean,
/// the nearest-rank p95, and the single worst offender (its name + its worst
/// single-sample value).</summary>
public readonly record struct MetricStat(double Mean, double P95, string WorstBot, double WorstValue);

/// <summary>P430 — the aggregate concurrency picture across every contributing
/// bot: how many bots yielded samples, plus interval and jump-distance
/// statistics. Produced by <see cref="BotManager.ComputePerf"/>.</summary>
public readonly record struct PerfStats(int BotCount, MetricStat Interval, MetricStat Jump);

/// <summary>
/// P633 — the inputs to ONE combat bot's coordinated target choice, gathered in
/// the parallel pre-pass (Loop PHASE A): its co-located different-side visible
/// candidates and whether a friendly station is co-located. Read-only and
/// independent of the engager-count settle, so it is safe to compute in parallel
/// and deterministic regardless of scheduling. The sequential settle (Phase B)
/// consumes it, preserving P622's order-sensitive cap assignment exactly.
/// </summary>
public readonly record struct PreparedEngagement(
    List<BotTargetCandidate> Candidates, bool FriendlyStation);

/// <summary>
/// P633 — the full live decision-loop report <c>get_perf_stats</c> returns:
/// the cadence core (<see cref="PerfStats"/>), the decision-pass time
/// distribution, the effective per-bot decision rate (Hz), the fleet decision
/// rate (bots decided per second), a sampled CPU/RSS/GC snapshot, the peak
/// <c>_gate</c> hold time, and the aggregate inbound decode throughput.
/// </summary>
public readonly record struct BotPerfReport(
    PerfStats Cadence,
    DecisionMetrics.PassStat DecisionPass,
    double DecisionRateHz,
    double FleetDecisionRateHz,
    DecisionMetrics.ResourceSnapshot Resources,
    double MaxGateHoldMs,
    double DecodeMbPerSec,
    (double MeanMs, double P95Ms, double MaxMs) SnapAgeMs,
    (double PublishPerSec, double PublishMsPerSec, double DecodeLagMean, double DecodeLagP95, double DecodeLagMax) SnapshotStats,
    StallReport Stall,
    IdleTargetReport IdleWithTarget);

/// <summary>P431 — one bot tick's accept-and-translate decision (see
/// <see cref="BotManager.DecideAcceptTranslate"/>): whether the bot must send a
/// real <c>ORDER_CHANGE</c> ACCEPT this tick, and which enemy ship (if any) the
/// bot is now committed to engage (<see cref="OrderedTarget"/> = null means the
/// bot is doing its own spawn-time behavior).</summary>
public readonly record struct OrderDecision(bool ShouldSendAccept, short? OrderedTarget);
