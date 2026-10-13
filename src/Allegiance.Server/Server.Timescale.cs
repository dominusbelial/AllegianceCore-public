using System.Diagnostics;
using System.Globalization;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P612/P618 — the sim time scale (<c>sim.timeScale</c>) for slow motion, now
/// two ways: the admin-only manual <c>timescale</c> cheat (P612, cinematic
/// slow motion) and, when <c>sim.timeDilation = auto</c>, a load-driven
/// EVE-style Time-Dilation controller (P618) that slows the clock smoothly when
/// the server can't hold real time.
/// <para>
/// Slow motion is achieved by advancing the server's sim clock by
/// <c>period × scale</c> per tick (instead of <c>period</c>) while the wall-clock
/// cadence stays fixed: the whole world — physics, bots, broadcasts — runs at
/// <c>scale</c>× speed, so a battle of any size plays in clean slow motion. Every
/// connected client learns the current scale via <see cref="TimescaleUpdate"/>
/// (server→client) and extrapolates its remote-ship dead-reckoning in scaled sim
/// time, so there is no rubber-banding.
/// </para>
/// <para>
/// Partial class sharing the <see cref="Server"/> type. The manual value is
/// clamped to [0.1, 1.0] (see <see cref="LiveSettingsConfig.ClampTimeScale"/>)
/// and only settable by an admin <c>timescale</c> cheat line (LAN/demo-grade).
/// </para>
/// </summary>
public sealed partial class Server
{
    /// <summary>The active sim time scale (1.0 = normal). Read on the sim thread;
    /// mutated on the sim thread only (P612 chat dispatch or the P618 auto
    /// controller, which runs inside <see cref="Tick"/>).</summary>
    private double _timeScale = 1.0;

    /// <summary>P618 — <c>"off"</c> or <c>"auto"</c> (from <c>sim.timeDilation</c>).</summary>
    private string _timeDilationMode = "off";

    /// <summary>P618 — the load-driven controller; null when mode is <c>off</c>.</summary>
    private TimeDilationController? _tidiController;

    /// <summary>P618 — wall clock for the controller's rate-limiting (the
    /// wall-seconds-between-observations input). Started at boot.</summary>
    private readonly Stopwatch _tidiWall = new();

    /// <summary>P618 — the controller's last-observed wall position (ms).</summary>
    private long _lastTidiWallMs;

    /// <summary>Initialize <see cref="_timeScale"/> from config, clamping and
    /// loudly logging. Called once at boot. Also resolves the P618 time-dilation
    /// mode and starts the controller when <c>auto</c>.</summary>
    private void InitTimeScale()
    {
        InitTimeDilation();
        if (_tidiController is not null) return;   // auto mode governs the clock

        var cfg = _config.LiveSettings?.TimeScale;
        if (cfg is not { } v)
        {
            Console.WriteLine("[boot] sim.timeScale = 1.0 (default, normal speed)");
            return;
        }
        _timeScale = LiveSettingsConfig.ClampTimeScale(v);
        if (_timeScale != v)
            Console.WriteLine($"[boot] *** sim.timeScale config {v:R} OUT OF RANGE — clamped to {_timeScale:R} ([{LiveSettingsConfig.MinTimeScale:R},{LiveSettingsConfig.MaxTimeScale:R}]) ***");
        Console.WriteLine($"[boot] sim.timeScale = {_timeScale:R} (cinematic slow motion: world runs at {_timeScale:P0} speed)");
    }

    /// <summary>
    /// P618 — resolve <c>sim.timeDilation</c> (<c>off</c> | <c>auto</c>; default
    /// <c>off</c>), construct the controller in auto mode, and subscribe to the
    /// per-window profiler report so the <c>[tidi]</c> line and the live-scale
    /// <c>[tickprof]</c> label update each window. <c>auto</c> ignores a static
    /// <c>sim.timeScale</c> config (the controller governs the clock from tick 1);
    /// an admin <c>timescale</c> cheat still overrides it.
    /// </summary>
    private void InitTimeDilation()
    {
        var ls = _config.LiveSettings ?? new LiveSettingsConfig();
        string mode = (ls.TimeDilation ?? "off").Trim().ToLowerInvariant();
        if (mode is not ("off" or "auto"))
        {
            Console.WriteLine($"[boot] *** sim.timeDilation config '{ls.TimeDilation}' UNKNOWN — expected off|auto; defaulting to off ***");
            mode = "off";
        }

        TickProfiler.WindowReported += EmitTidiWindow;
        // P634 — per-window per-peer send-backlog sample (scope 1 measurement):
        // queue depths + the age of the oldest pending guaranteed frame, so the
        // live 200v200 baseline shows whether a joiner's world data ever waits
        // behind the position stream. Gated on peers>0 (silent for the suites).
        TickProfiler.WindowReported += EmitPeerQueueWindow;

        if (mode == "off")
        {
            _timeDilationMode = "off";
            Console.WriteLine("[boot] sim.timeDilation = off (no load-driven slow motion; P612 manual timescale only)");
            return;
        }

        _timeDilationMode = "auto";
        var p = new TimeDilationParams(
            nominalPeriod: _period,
            minScale: LiveSettingsConfig.MinTimeScale,
            maxScale: LiveSettingsConfig.MaxTimeScale,
            alpha: ls.TimeDilationAlpha ?? 0.07,
            engageRatio: ls.TimeDilationEngageRatio ?? 1.005,
            releaseRatio: ls.TimeDilationReleaseRatio ?? 0.965,
            deadband: ls.TimeDilationDeadband ?? 0.010,
            maxChangePerSecond: ls.TimeDilationMaxChangePerSecond ?? 0.040,
            controlIntervalSeconds: ls.TimeDilationControlIntervalSeconds ?? 0.5);
        _tidiController = new TimeDilationController(p);
        _tidiWall.Restart();
        _lastTidiWallMs = _tidiWall.ElapsedMilliseconds;
        Console.WriteLine("[boot] sim.timeDilation = auto — load-driven EVE-style time dilation active "
                          + $"(engages when smoothed tick cost > {p.EngageRatio:R}× period, releases < {p.ReleaseRatio:R}×; "
                          + $"scale slews toward nominal/cost in [{LiveSettingsConfig.MinTimeScale:R},{LiveSettingsConfig.MaxTimeScale:R}] "
                          + $"rate-limited to {p.MaxChangePerSecond:R}/s every {p.ControlIntervalSeconds:0.##}s); "
                          + "admin `timescale` cheat overrides auto");
    }

    /// <summary>P618 — the <c>[tickprof]</c> header-label prefix (broadcast +
    /// transport + ship-update model), captured so each window can reprint the
    /// label with the CURRENT live <c>sim.timeScale</c> (task: "the [tickprof]
    /// label shows the current scale").</summary>
    private string ComposeTidiLabelPrefix()
    {
        string bcast = _broadcastParallelBuild
            ? $"broadcast.parallelBuild=true (maxPar={_broadcastMaxParallelism})"
            : "broadcast.parallelBuild=false";
        string trans = _transportMode == TransportMode.Pool
            ? $"transport=pool ({_transportPoolWorkers} workers)"
            : "transport=thread-per-peer";
        return $"{bcast}; {trans}; shipUpdateModel={_shipUpdateModel}";
    }

    /// <summary>P618 — reprint the profiler header label with the live scale.
    /// Recomputes the prefix lazily each time so it always reflects the
    /// boot-resolved broadcast/transport/ship-update settings regardless of when
    /// this is first called.</summary>
    private void RefreshProfilerLabel()
        => TickProfiler.SetLabel($"{ComposeTidiLabelPrefix()}; sim.timeScale={_timeScale:R}");

    /// <summary>
    /// P618 — per-profiler-window callback: reprint the label (so the current
    /// scale is always visible in the <c>[tickprof]</c> header) and emit the
    /// sampler-friendly <c>[tidi]</c> line <c>scale=… cost=…</c>. Fires from the
    /// sim thread inside the profiler's window roll-over.
    /// </summary>
    private void EmitTidiWindow()
    {
        RefreshProfilerLabel();
        double cost = _tidiController?.SmoothedCost ?? 0.0;
        Console.WriteLine($"[tidi] scale={_timeScale:0.####} cost={cost * 1000:0.#}ms mode={_timeDilationMode}");
    }

    /// <summary>
    /// P634 — per-profiler-window per-peer send-backlog sample: the server-side
    /// half of scope 1's measurement. Prints, for the live baseline, how deep a
    /// peer's ordered (join/world) and stream lanes are, how many bytes are
    /// queued, and the oldest age of a pending guaranteed frame (ms) — the
    /// direct signal for "a joiner's world data is starving behind the position
    /// stream". Silent when no peers are connected (keeps the unit suites clean).
    /// </summary>
    private void EmitPeerQueueWindow()
    {
        var t = _host.Transport;
        if (t is null) return;
        var (peers, maxOrd, maxStr, maxBytes, totalBytes, maxAge) = t.SamplePeerQueues();
        if (peers == 0) return;
        Console.WriteLine($"[p634-queue] peers={peers} maxOrderedPending={maxOrd} maxStreamPending={maxStr} " +
                          $"maxQueuedBytes={maxBytes} totalQueuedBytes={totalBytes} maxOldestGuaranteedMs={maxAge}");
    }

    /// <summary>
    /// P618 — feed one tick's measured processing cost to the controller and, if
    /// it moved the scale, rebroadcast it to clients. Called at the end of
    /// <see cref="Tick"/> (after the sim pass, before the cadence sleep) so the
    /// new scale applies from the next tick. No-op when mode is <c>off</c>.
    /// </summary>
    private void ObserveTimeDilation(double cost, double wallDeltaSeconds)
    {
        var c = _tidiController;
        if (c is null) return;
        if (c.Observe(cost, wallDeltaSeconds))
            ApplyTimeDilationScale();
    }

    /// <summary>P618 — adopt the controller's new scale (rate-limited by the
    /// controller, so broadcasts are inherently throttled) and notify clients.</summary>
    private void ApplyTimeDilationScale()
    {
        if (_tidiController is not { } c) return;
        double s = c.Scale;
        if (Math.Abs(s - _timeScale) < 1e-9) return;
        _timeScale = s;
        BroadcastTimescale();
        Console.WriteLine($"[tidi] sim.timeScale -> {s:R} (auto time dilation)");
    }

    /// <summary>P618 — wall seconds since the previous controller observation
    /// (used for rate-limiting). Resets the rolling baseline.</summary>
    private double ConsumeTidiWallDelta()
    {
        long nowMs = _tidiWall.ElapsedMilliseconds;
        double d = (nowMs - _lastTidiWallMs) / 1000.0;
        _lastTidiWallMs = nowMs;
        return d < 0 ? 0 : d;
    }

    /// <summary>Set the sim time scale (clamped) and notify every connected client.
    /// Used by the admin <c>timescale</c> cheat. Runs on the sim thread. When the
    /// P618 auto controller is active, this sets a MANUAL OVERRIDE that suspends
    /// auto control (task: "when the admin sets timescale manually, that overrides
    /// auto").</summary>
    private void SetTimeScale(double value)
    {
        double clamped = LiveSettingsConfig.ClampTimeScale(value);
        if (_tidiController is { } c)
        {
            c.SetManual(clamped);
            _timeScale = clamped;
            BroadcastTimescale();
            Console.WriteLine($"[timescale] MANUAL OVERRIDE -> sim.timeScale = {clamped:R} (auto time dilation suspended)");
            return;
        }
        _timeScale = clamped;
        BroadcastTimescale();
        Console.WriteLine($"[timescale] sim.timeScale -> {clamped:R}");
    }

    /// <summary>Send the current <see cref="TimescaleUpdate"/> to every connected
    /// peer (including pending side-select peers). Called at boot (during the join
    /// burst) and on every change so clients always converge on the current scale.</summary>
    private void BroadcastTimescale()
    {
        var packet = FedPacket.Pack(new[] { new TimescaleUpdate { Scale = (float)_timeScale }.Encode() });
        foreach (var kv in _peerShips)
            SendPeer(kv.Key, packet);
        foreach (var from in _pendingSideSelect.Keys)
            SendPeer(from, packet);
    }

    /// <summary>Send the current <see cref="TimescaleUpdate"/> to a single peer
    /// (called when a new peer is seated so it learns the current scale).</summary>
    private void SendTimescaleTo(ConnectionId from)
        => SendPeer(from, FedPacket.Pack(new[] { new TimescaleUpdate { Scale = (float)_timeScale }.Encode() }));

    /// <summary>Effective sim-period advance for this tick (period × scale), used by
    /// the sim loop to advance the clock by less than the wall period under slow motion.</summary>
    private double EffectiveSimPeriod => _period * _timeScale;

    /// <summary>
    /// The <c>timescale &lt;value&gt;</c> admin cheat token — a P612 port extension.
    /// Parses a float in [0.1, 1.0] and sets the whole-world sim time scale (clamped,
    /// notifying clients). Replies via <see cref="SendAdminReply"/>; consumed (never
    /// broadcast as chat) like every admin cheat.
    /// </summary>
    private void HandleTimescaleCheat(ConnectionId from, Ship ship, string rest)
    {
        if (!double.TryParse(rest.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        {
            SendAdminReply(from, ship.Id, $"timescale: expected a number in [{LiveSettingsConfig.MinTimeScale:R},{LiveSettingsConfig.MaxTimeScale:R}] — got '{rest.Trim()}'");
            return;
        }
        double clamped = LiveSettingsConfig.ClampTimeScale(v);
        SetTimeScale(clamped);
        SendAdminReply(from, ship.Id, $"sim.timeScale = {clamped:R} (world runs at {clamped:P0} speed)" + (_tidiController is not null ? "; auto time dilation suspended (manual override)" : ""));
    }
}
