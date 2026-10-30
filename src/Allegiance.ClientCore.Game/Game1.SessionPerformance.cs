using System.Diagnostics;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>
/// P789 — the one Showcase: session performance recording for EVERY match the
/// client plays, the game-over performance panel, and the Showcase score.
///
/// Threading contract (Domingo, 2026-10-22) — the recorder must never starve
/// or stall a thread:
/// <list type="bullet">
/// <item>No NEW locks on the frame path: frame ms is a plain Stopwatch delta;
/// time dilation is read from <c>ClientWorld.TimeScale</c> exactly like the
/// existing frame code already does (Game1.FlightInput.cs reads it lock-free);
/// ship counts are sampled at ≤10 Hz only inside locks the frame code
/// ALREADY takes (the ship-warning lock for pilots, the observer's
/// apply-observation lock for spectators) — never a fresh <c>_session.Gate</c>
/// acquisition of our own.</item>
/// <item>No I/O or console on the frame path: the summary is pure computation;
/// the JSON save goes through <see cref="SessionPerformanceHandoff"/> (single
/// slot, drop, never waits) exactly once per match end.</item>
/// <item>Fixed cost: one fixed 512-bucket histogram per match, O(1) per frame,
/// zero allocations per frame (pinned by test).</item>
/// </list>
/// </summary>
public sealed partial class Game1
{
    private readonly SessionPerformanceRecorder _sessionRecorder = new();
    private readonly SessionPerformanceHandoff _perfHandoff = new();
    private bool _sessionRecording;
    private SessionPerformanceSummary? _sessionPerf;
    private GameOverPerfPanel? _perfPanel;
    private bool _perfPanelShown;
    private bool _perfCaptured;
    private SessionMatchKind _perfKind = SessionMatchKind.Normal;
    private bool _perfRestartPending;
    private long _perfLastFrameTimestamp;
    private long _perfNextWorldSample;
    private bool _perfToggleWasDown, _perfApplyWasDown;
    // P889 — AIX2's client frame-time input: one [frameprof] line per 5 s of
    // real frames (see FrameProfileWindow). Independent of the per-match
    // recorder so a log exists even when the recorder is switched off.
    private readonly FrameProfileWindow? _frameProfile =
        FrameProfileWindow.EnabledFromEnvironment() ? new FrameProfileWindow() : null;
    private long _frameProfileLast;

    /// <summary>Begin recording for a match. The user's panel preference (if
    /// they ever toggled it) wins; otherwise the panel is shown only for
    /// Showcase matches and hidden for multiplayer/normal skirmish.</summary>
    private void StartSessionRecording(SessionMatchKind kind)
    {
        // P789 A-B measurement switch (ALLEGIANCE_PERF_RECORDER=0 disables the
        // always-on recorder; the default is ON — the zero-cost claim is that
        // nobody can measure the difference).
        if (Environment.GetEnvironmentVariable("ALLEGIANCE_PERF_RECORDER") == "0") { _sessionRecording = false; return; }
        _perfKind = kind;
        _sessionRecorder.Start(kind);
        _sessionRecording = true;
        _perfCaptured = false;
        _sessionPerf = null;
        _perfPanel = null;
        _perfLastFrameTimestamp = Stopwatch.GetTimestamp();
        bool? preference = IntroPreferences.Load().PerfPanelShown;
        _perfPanelShown = preference ?? kind is SessionMatchKind.Showcase or SessionMatchKind.ShowcaseFixed;
    }

    /// <summary>Per-frame recording. Called from <see cref="Update"/> for every
    /// match state (pilot and spectator). O(1), zero allocations, no locks.
    /// Frame ms is the real wall time between Update calls; time dilation is
    /// the same lock-free <c>World.TimeScale</c> read the existing frame code
    /// performs (Game1.FlightInput.cs:295-297). Also flushes a pending
    /// next-match recorder restart once the previous match's game-over panel
    /// is gone (GameOverResult cleared at the next mission's STAGE_STARTING,
    /// ClientWorld.cs:4745-4749).</summary>
    /// <summary>
    /// P889 — feed the AIX2 <c>[frameprof]</c> window one real frame. O(1), no
    /// allocations, no I/O until a window closes (once per 5 s).
    /// </summary>
    private void UpdateFrameProfile()
    {
        if (_frameProfile is null) return;
        long now = Stopwatch.GetTimestamp();
        if (_frameProfileLast == 0) { _frameProfileLast = now; return; }
        double frameMs = Stopwatch.GetElapsedTime(_frameProfileLast, now).TotalMilliseconds;
        _frameProfileLast = now;
        if (_frameProfile.Record(frameMs) is { } line) Console.WriteLine(line);
    }

    private void UpdateSessionPerformance()
    {
        UpdateFrameProfile();
        if (!_sessionRecording) return;
        if (_perfRestartPending && _session.World.GameOverResult is null)
        {
            _perfRestartPending = false;
            RestartSessionRecorder();
        }
        if (!_sessionRecording) return;
        long now = Stopwatch.GetTimestamp();
        double frameMs = Stopwatch.GetElapsedTime(_perfLastFrameTimestamp, now).TotalMilliseconds;
        _perfLastFrameTimestamp = now;
        float tidi = _session.World is { } w ? 100f * (float)w.TimeScale : 100f;
        _sessionRecorder.RecordFrame(frameMs, tidi);
        if (_perfPanel is not null) UpdatePerformancePanelInput();
    }

    /// <summary>Throttled (≤10 Hz) ship-count sample. MUST be called from
    /// inside a lock the frame code already holds — this never takes
    /// <c>_session.Gate</c> by itself. Manual loops only: no LINQ, no
    /// allocation.</summary>
    private void SampleSessionPerformanceWorldLocked(ClientWorld world, short viewedSector)
    {
        if (!_sessionRecording || viewedSector < 0 || Environment.TickCount64 < _perfNextWorldSample) return;
        _perfNextWorldSample = Environment.TickCount64 + 100;
        int shipsInView = 0, total = 0;
        foreach (var ship in world.Ships.Values)
        {
            total++;
            if (ship.CanDrawInSector(viewedSector)) shipsInView++;
        }
        _sessionRecorder.SampleWorld(shipsInView, total);
    }

    /// <summary>Pilot path: the ship-warning lock the frame code already takes
    /// every Update (Game1.FlightInput.cs). The player's sector is the own
    /// ship's render sector.</summary>
    private void SampleSessionPerformancePilotLocked(ClientWorld world)
    {
        if (!_sessionRecording || _localSpectating) return;
        short sector = -1;
        if (world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own)) sector = RenderSector(own.SectorId);
        if (sector < 0) return;
        SampleSessionPerformanceWorldLocked(world, sector);
    }

    /// <summary>Spectator path: inside the observer's existing
    /// apply-observation lock (Game1.SkirmishObserver.cs) — the viewed sector
    /// is the skirmish camera's sector, exactly the sector the
    /// <c>flyingShipsInView</c> proof metric already counts.</summary>
    private void SampleSessionPerformanceSpectatorLocked(ClientWorld world)
    {
        if (!_sessionRecording || !_localSpectating) return;
        SampleSessionPerformanceWorldLocked(world, _skirmishCamera.SectorId);
    }

    /// <summary>A rotated next match is a NEW match — but the game-over panel
    /// must keep showing the finished match's numbers until the panel itself
    /// closes (GameOverResult cleared at the next STAGE_STARTING). So the
    /// MissionGeneration change only MARKS the restart; the recorder actually
    /// restarts (keeping the old panel data) once the game-over panel is gone.</summary>
    private void RestartSessionRecorder()
    {
        _sessionRecorder.Start(_perfKind);
        _perfCaptured = false;
        _perfLastFrameTimestamp = Stopwatch.GetTimestamp();
        _perfNextWorldSample = 0;
    }

    /// <summary>Latch the per-match summary once — at game over, or when the
    /// match is left early. Pure computation; the JSON save is handed off
    /// off-thread and can never block or pile up.</summary>
    private void CaptureSessionPerformance()
    {
        if (!_sessionRecording || _perfCaptured) return;
        _perfCaptured = true;
        double? serverTickP95 = null;
        if (_skirmish is { } host)
        {
            var serverTick = Distribution.Of(host.TickSamples);
            serverTickP95 = serverTick.Count > 0 ? serverTick.P95 : null;
        }
        double fpsLow = _sessionRecorder.Frames > 0 ? 1000 / Math.Max(0.001, _sessionRecorder.FrameMsOnePercentLow()) : 0;
        var summary = _sessionRecorder.Finish(CurrentBenchmarkSettings(), serverTickP95,
            BenchmarkPolicy.Bottleneck(new BenchmarkRun { Rendered = true, FpsOnePercentLow = _sessionRecorder.Frames > 0 ? fpsLow : null }));
        _sessionPerf = summary;
        _perfPanel = GameOverPerfPanel.From(summary);
        var toSave = summary;
        string path = Path.Combine(PlayerConfig.DirectoryPath, "session-performance.json");
        _perfHandoff.TryPost(() => SaveSessionPerformance(toSave, path));
    }

    /// <summary>Off-thread (single-slot) JSON save. Manual writer — no
    /// reflection serialization, no allocation on the game thread.</summary>
    private static void SaveSessionPerformance(SessionPerformanceSummary s, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using var stream = File.Create(path + ".tmp");
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("schemaVersion", 2);
                writer.WriteString("kind", s.Kind.ToString());
                writer.WriteBoolean("ranked", s.Ranked);
                writer.WriteNumber("score", s.Score);
                writer.WriteNumber("matchSeconds", s.MatchSeconds);
                writer.WriteNumber("frames", s.Frames);
                writer.WriteNumber("fpsAverage", s.FpsAverage);
                writer.WriteNumber("fpsOnePercentLow", s.FpsOnePercentLow);
                writer.WriteNumber("frameP50Ms", s.FrameMs?.P50 ?? 0);
                writer.WriteNumber("frameP95Ms", s.FrameMs?.P95 ?? 0);
                writer.WriteNumber("frameP99Ms", s.FrameMs?.P99 ?? 0);
                writer.WriteNumber("frameWorstMs", s.FrameMs?.Worst ?? 0);
                writer.WriteNumber("tidiMinPercent", s.TidiMinPercent);
                writer.WriteNumber("tidiTimeWeightedMeanPercent", s.TidiTimeWeightedMeanPercent);
                writer.WriteNumber("shipsInViewMean", s.ShipsInViewMean);
                writer.WriteNumber("shipsInViewPeak", s.ShipsInViewPeak);
                writer.WriteNumber("totalPlayersPeak", s.TotalPlayersPeak);
                writer.WriteEndObject();
            }
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception) { /* Diagnostics can never fail the game. */ }
    }

    /// <summary>Game-over panel input: O toggles the panel (persisted as the
    /// user's preference), A applies the suggested settings
    /// (<see cref="ApplyBenchmarkSettings"/>) when a recommendation exists.
    /// Gated to the game-over screen and never while composing chat, so no
    /// native control's input handling changes.</summary>
    private void UpdatePerformancePanelInput()
    {
        if (_state is not (ClientState.Flying or ClientState.ReadyCheck)
            || _session.World.GameOverResult is null || _chatActive || _qcMenuOpen) return;
        var keys = Keyboard.GetState();
        bool toggle = keys.IsKeyDown(Keys.O);
        if (toggle && !_perfToggleWasDown)
        {
            _perfPanelShown = !_perfPanelShown;
            try
            {
                var prefs = IntroPreferences.Load();
                prefs.PerfPanelShown = _perfPanelShown;
                prefs.Save();
            }
            catch (Exception) { /* preference persistence is best-effort. */ }
        }
        _perfToggleWasDown = toggle;
        if (_perfPanelShown && _perfPanel?.SuggestionAvailable == true && _benchmark is null)
        {
            bool apply = keys.IsKeyDown(Keys.A);
            if (apply && !_perfApplyWasDown && _sessionPerf?.Settings is { } settings)
            {
                ApplyBenchmarkSettings(settings);
                _skirmishScreen.ApplyDefaults(settings);
                (BenchmarkPreferences.Load() with { OfferHandled = true, Applied = settings }).Save();
                _upscaling.Save(UpscalingSettings.DefaultPath);
            }
            _perfApplyWasDown = apply;
        }
    }
}
