using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Allegiance.Server;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P889 — bounded, re-runnable calibration of the server's worker-pool sizing on
/// THIS machine. Each candidate is a real, short skirmish at the chosen size,
/// watched through the spectator camera (the hottest fight), so the same run
/// measures all three things that matter: client frame time, server tick time,
/// and the bot driver's decision rate. The winner is saved to this machine's
/// sizing profile (<c>sizing-profile.json</c>) which the resolver then uses;
/// explicit config/env still wins.
/// </summary>
public sealed partial class Game1
{
    /// <summary>Bounded warmup after the observed fight goes live (asset loads
    /// and the camera's first cut), before the measured window starts.</summary>
    private const double CalibrationWarmupSeconds = 5;

    private sealed record CalibrationCandidate(string Name, SkirmishSizing Sizing, string Reason);
    private sealed record CalibrationRun(string Name, string Reason, int BotWorkers, int BotCoresPerWorker,
        double FrameMean, double FrameP50, double FrameP95, double TickMean, double TickP50, double TickP95,
        double DecisionHz, double Cost, double Seconds, int Frames, int Bots);

    private SizingCalibrationCommand? _calibration;
    private List<CalibrationCandidate> _calibCandidates = [];
    private int _calibIndex = -1;
    private readonly List<CalibrationRun> _calibRuns = [];
    private readonly List<double> _calibFrames = [];
    private bool _calibMeasuring;
    private long _calibMeasureStart;
    private long _calibWarmupUntil;
    private long _calibUpdateTicks;
    private CalibrationCandidate? _calibCurrent;

    /// <summary>Install the command and start the first candidate. Called from
    /// Initialize after telemetry is available; null leaves every match alone.</summary>
    private void StartSizingCalibration(SizingCalibrationCommand? command)
    {
        if (command is null) return;
        _calibration = command;
        _calibCandidates = BuildCalibrationCandidates(command.Size);
        Console.WriteLine($"[calibrate] {command.Size}: {_calibCandidates.Count} candidates × {command.Seconds:0}s "
            + $"= bounded (~{(command.Seconds + 75) * _calibCandidates.Count / 60:0} min); "
            + "profile " + (command.SaveProfile ? MachineSizingProfile.DefaultPath : "not saved"));
        StartNextCalibrationCandidate();
    }

    private List<CalibrationCandidate> BuildCalibrationCandidates(SkirmishSize size)
    {
        int cores = Math.Max(1, Environment.ProcessorCount);
        var options = new SkirmishOptions { Size = size };
        var resolved = PoolSizingResolver.Resolve(cores, options.SeatCount, HostProfile.Shared);
        var budget = SkirmishBudget.ForOptions(options, cores);
        string resolvedTransport = resolved.TransportName;
        int resolvedPool = resolved.TransportWorkers;
        var candidates = new List<CalibrationCandidate>
        {
            new("resolver", new(), "the plain resolver (this machine's rule)"),
            new("serial", new(ParallelBuild: false, BuildWorkers: 1, TransportMode: "thread-per-peer", PoolWorkers: 0),
                "pre-P887: serial build, thread-per-peer"),
            new("p887", new(ParallelBuild: true, BuildWorkers: Math.Max(1, cores - 1), TransportMode: "pool", PoolWorkers: cores),
                "P887 dedicated-server defaults (cores-1 build, cores send workers)"),
            new("build+2", new(ParallelBuild: true, BuildWorkers: resolved.BuildWorkers + 2,
                    TransportMode: resolvedTransport, PoolWorkers: resolvedPool),
                "resolver with two more build workers"),
            new("pool", new(ParallelBuild: resolved.ParallelBuild, BuildWorkers: resolved.BuildWorkers,
                    TransportMode: "pool", PoolWorkers: Math.Max(3, resolvedPool > 0 ? resolvedPool : 3)),
                "resolver, but a small send pool instead of thread-per-peer"),
            new("bots-1", new(BotWorkers: Math.Max(1, budget.Workers - 1)),
                "resolver with one fewer bot-driver worker"),
        };
        return candidates
            .Where(c => _calibration!.Candidates.Contains(c.Name, StringComparer.Ordinal))
            .ToList();
    }

    private void StartNextCalibrationCandidate()
    {
        _calibIndex++;
        if (_calibIndex >= _calibCandidates.Count) { FinishSizingCalibration(); return; }
        _calibCurrent = _calibCandidates[_calibIndex];
        _calibFrames.Clear();
        _calibMeasuring = false;
        _calibMeasureStart = 0;
        _calibWarmupUntil = 0;
        _calibUpdateTicks = 0;
        _calibLastFailure = null;
        var options = new SkirmishOptions
        {
            Size = _calibration!.Size,
            StartAsSpectator = true,
            Speed = SkirmishSpeed.Showcase,
            Seed = 729,
            StartCountdownSeconds = 5,
            Sizing = _calibCurrent.Sizing,
        };
        _skirmishCancel?.Dispose();
        _skirmishCancel = new();
        _skirmishError = "";
        _observerAutoStarted = true;      // keep UpdateSkirmish's env auto-start out of the way
        _skirmishStart = SkirmishHost.StartAsync(options, cancellationToken: _skirmishCancel.Token);
        Console.WriteLine($"[calibrate] candidate {_calibIndex + 1}/{_calibCandidates.Count} {_calibCurrent.Name}: "
            + _calibCurrent.Reason);
    }

    private string? _calibLastFailure;

    /// <summary>Drive one candidate: wait until the observed fight is live,
    /// measure for the bounded window, then score it and start the next.</summary>
    private void UpdateSizingCalibration()
    {
        if (_calibration is null) return;
        // Client frame time = the same per-Update wall delta the P889
        // [frameprof] log (and the P789 session recorder) measures: AIX2's
        // frame-time quantity. Bounded to one sample per Update, no I/O.
        long updateNow = Stopwatch.GetTimestamp();
        double updateMs = _calibUpdateTicks == 0 ? 0
            : Stopwatch.GetElapsedTime(_calibUpdateTicks, updateNow).TotalMilliseconds;
        _calibUpdateTicks = updateNow;
        if (_calibMeasuring && updateMs > 0 && _calibFrames.Count < 200_000) _calibFrames.Add(updateMs);
        if (_skirmish is null || !_localSpectating)
        {
            if (_skirmishStart is null && _calibCurrent is not null
                && (_calibLastFailure ?? (_skirmishError.Length > 0 ? _skirmishError : null)) is { } failure)
            {
                // A host that never came up is a failed candidate, not a wedge:
                // record it loudly and continue with the next one.
                Console.WriteLine($"[calibrate] candidate {_calibCurrent.Name} failed to start: {failure}");
                _calibRuns.Add(new(_calibCurrent.Name, _calibCurrent.Reason, 0, 0,
                    double.MaxValue, 0, double.MaxValue, double.MaxValue, 0, double.MaxValue,
                    0, double.MaxValue, 0, 0, 0));
                _calibCurrent = null;
                TeardownCalibrationCandidate();
                StartNextCalibrationCandidate();
            }
            return;
        }
        // The embedded host can die mid-run (bot driver lost); score what we got.
        if (_skirmish.Failure is { } lost)
        {
            Console.WriteLine($"[calibrate] candidate {_calibCurrent?.Name} lost its host: {lost}");
            _calibLastFailure = lost;
            if (_calibMeasuring) CompleteCalibrationCandidate(); else { TeardownCalibrationCandidate(); StartNextCalibrationCandidate(); }
            return;
        }
        if (!_calibMeasuring)
        {
            bool live = _observation is { Stage: MissionStage.Started } observation
                && observation.Objects.OfType<Allegiance.Core.Wire.SingleShipUpdate>().Count() >= 2
                && _skirmish.Stats.ValueKind == JsonValueKind.Object
                && _skirmish.Stats.TryGetProperty("botCount", out var bots) && bots.GetInt32() >= Math.Max(2, _skirmish.Options.SeatCount / 2);
            if (!live) return;
            // The first seconds after the fight goes live are asset/shader
            // loads for the spectator sector (planet posters, models), which
            // are not the steady-state frame cost under test. Discard a short
            // bounded warmup before the measured window.
            if (_calibWarmupUntil == 0) { _calibWarmupUntil = Stopwatch.GetTimestamp(); return; }
            if (Stopwatch.GetElapsedTime(_calibWarmupUntil).TotalSeconds < CalibrationWarmupSeconds) return;
            _calibFrames.Clear();
            _skirmish.BeginMeasurement();
            _calibMeasuring = true;
            _calibMeasureStart = Stopwatch.GetTimestamp();
            Console.WriteLine($"[calibrate] measuring {_calibCurrent?.Name} for {_calibration.Seconds:0}s (fight live)");
            return;
        }
        if (Stopwatch.GetElapsedTime(_calibMeasureStart).TotalSeconds < _calibration.Seconds) return;
        CompleteCalibrationCandidate();
    }

    private void CompleteCalibrationCandidate()
    {
        _calibMeasuring = false;
        var host = _skirmish!;
        var frame = Distribution.Of(_calibFrames);
        var tick = Distribution.Of(host.TickSamples);
        double decisionHz = StatsNumber(host, "decisionRateHz");
        int botCount = (int)StatsNumber(host, "botCount");
        double seconds = Stopwatch.GetElapsedTime(_calibMeasureStart).TotalSeconds;
        var candidate = _calibCurrent!;
        var budget = SkirmishBudget.ForOptions(host.Options, Environment.ProcessorCount);
        double cost = SizingCalibrationScore.Cost(frame.P95, tick.P95, decisionHz);
        var run = new CalibrationRun(candidate.Name, candidate.Reason, budget.Workers, budget.CoresPerWorker,
            frame.Mean, frame.P50, frame.P95, tick.Mean, tick.P50, tick.P95, decisionHz, cost, seconds, frame.Count, botCount);
        _calibRuns.Add(run);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[calibrate] {run.Name}: frame mean={run.FrameMean:0.00}ms p50={run.FrameP50:0.00}ms p95={run.FrameP95:0.00}ms; tick mean={run.TickMean:0.00}ms p50={run.TickP50:0.00}ms p95={run.TickP95:0.00}ms; decisions={run.DecisionHz:0.0}Hz; bots={run.Bots} workers={run.BotWorkers}×{run.BotCoresPerWorker}; score={run.Cost:0.0000}"));
        TeardownCalibrationCandidate();
        StartNextCalibrationCandidate();
    }

    private void TeardownCalibrationCandidate()
    {
        _skirmishCancel?.Cancel();
        _session.Dispose();
        _skirmish?.Dispose();
        _skirmish = null;
        _skirmishStart = null;
        _localSpectating = false;
        _observation = null;
        _seatChange = null;
        _requestedSeat = null;
        _skirmishCancel?.Dispose();
        _skirmishCancel = null;
        _state = ClientState.Intro;
    }

    private void FinishSizingCalibration()
    {
        var command = _calibration!;
        _calibration = null;
        if (_calibRuns.Count == 0)
        {
            Console.Error.WriteLine("[calibrate] no candidate measured; nothing saved");
            if (command.ExitWhenComplete) Exit();
            return;
        }
        var winner = _calibRuns.OrderBy(r => r.Cost).ThenBy(r => r.Name, StringComparer.Ordinal).First();
        var candidate = _calibCandidates.First(c => c.Name == winner.Name);
        int cores = Math.Max(1, Environment.ProcessorCount);
        var options = new SkirmishOptions { Size = command.Size };
        var resolved = PoolSizingResolver.Resolve(cores, options.SeatCount, HostProfile.Shared);
        // Store CONCRETE sizes: a null override in the winning candidate means
        // "whatever the resolver chose", which is what the profile records.
        var sizing = candidate.Sizing;
        bool parallel = sizing.ParallelBuild ?? resolved.ParallelBuild;
        var entry = new SizingCalibrationEntry
        {
            Players = options.SeatCount,
            Profile = "shared",
            ParallelBuild = parallel,
            BuildWorkers = parallel ? sizing.BuildWorkers ?? resolved.BuildWorkers : 1,
            Transport = sizing.TransportMode ?? resolved.TransportName,
            PoolWorkers = sizing.PoolWorkers ?? resolved.TransportWorkers,
            BotWorkers = sizing.BotWorkers ?? winner.BotWorkers,
            BotCoresPerWorker = sizing.BotCoresPerWorker ?? winner.BotCoresPerWorker,
            Score = winner.Cost,
            Reason = $"winner of {_calibRuns.Count} candidates: {winner.Reason}",
            UpdatedUtc = DateTimeOffset.UtcNow,
        };
        Console.WriteLine($"[calibrate] {command.Size}: winner {winner.Name} ({winner.Reason})");
        foreach (var run in _calibRuns.OrderBy(r => r.Cost))
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {run.Name,-10} score={run.Cost:0.0000} frame mean={run.FrameMean:0.00}ms p95={run.FrameP95:0.00}ms tick mean={run.TickMean:0.00}ms p95={run.TickP95:0.00}ms decisions={run.DecisionHz:0.0}Hz{(run.Name == winner.Name ? "  ← calibrated" : "")}"));
        var baseline = _calibRuns.FirstOrDefault(r => r.Name == "resolver");
        if (baseline is not null && winner.Name != baseline.Name)
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[calibrate] calibrated {winner.Name} vs resolver: {baseline.Cost:0.0000} → {winner.Cost:0.0000} ({(winner.Cost / Math.Max(0.0000001, baseline.Cost) - 1) * 100:+0.0;-0.0}% cost)"));
        else if (baseline is not null)
            Console.WriteLine("[calibrate] the plain resolver already wins; profile records its sizes");
        if (command.SaveProfile)
        {
            var profile = MachineSizingProfile.LoadForThisMachine(cores)
                ?? new MachineSizingProfile { Cores = cores, Version = PoolSizingProfileStore.CurrentVersion };
            profile = profile.With(entry, cores);
            Console.WriteLine($"[calibrate] saved {command.Size} → {MachineSizingProfile.DefaultPath} "
                + $"(cores={profile.Cores}, version={profile.Version})");
        }
        WriteCalibrationOutput(command, entry, winner);
        if (command.ExitWhenComplete) { Exit(); return; }
        _introView = IntroView.Main;
        _state = ClientState.Intro;
    }

    private void WriteCalibrationOutput(SizingCalibrationCommand command, SizingCalibrationEntry winner,
        CalibrationRun winningRun)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(command.Output))!);
            using var stream = File.Create(command.Output + ".tmp");
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 1);
                writer.WriteString("size", command.Size.ToString());
                writer.WriteNumber("cores", Environment.ProcessorCount);
                writer.WriteString("version", PoolSizingProfileStore.CurrentVersion);
                writer.WriteNumber("secondsPerCandidate", command.Seconds);
                writer.WriteString("winner", winningRun.Name);
                writer.WriteNumber("winnerScore", winningRun.Cost);
                writer.WriteStartObject("winnerSizing");
                writer.WriteBoolean("parallelBuild", winner.ParallelBuild);
                writer.WriteNumber("buildWorkers", winner.BuildWorkers);
                writer.WriteString("transport", winner.Transport);
                writer.WriteNumber("poolWorkers", winner.PoolWorkers);
                writer.WriteNumber("botWorkers", winner.BotWorkers);
                writer.WriteNumber("botCoresPerWorker", winner.BotCoresPerWorker);
                writer.WriteEndObject();
                writer.WriteStartArray("candidates");
                foreach (var run in _calibRuns)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", run.Name);
                    writer.WriteString("reason", run.Reason);
                    writer.WriteNumber("frameMeanMs", run.FrameMean);
                    writer.WriteNumber("frameP50Ms", run.FrameP50);
                    writer.WriteNumber("frameP95Ms", run.FrameP95);
                    writer.WriteNumber("tickMeanMs", run.TickMean);
                    writer.WriteNumber("tickP50Ms", run.TickP50);
                    writer.WriteNumber("tickP95Ms", run.TickP95);
                    writer.WriteNumber("decisionHz", run.DecisionHz);
                    writer.WriteNumber("score", run.Cost);
                    writer.WriteNumber("frames", run.Frames);
                    writer.WriteNumber("bots", run.Bots);
                    writer.WriteNumber("botWorkers", run.BotWorkers);
                    writer.WriteNumber("botCoresPerWorker", run.BotCoresPerWorker);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            File.Move(command.Output + ".tmp", command.Output, true);
            Console.WriteLine($"[calibrate] wrote {Path.GetFullPath(command.Output)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("[calibrate] could not write " + command.Output + ": " + e.Message);
        }
    }

    private static double StatsNumber(SkirmishHost host, string property)
    {
        var stats = host.Stats;
        return stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    }
}
