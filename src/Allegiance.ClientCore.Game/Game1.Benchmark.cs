using System.Diagnostics;
using Allegiance.ClientCore.Replay;
using Allegiance.ClientCore.Session;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.SinglePlayer.Benchmark;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private int _benchMenuIndex;
    private static readonly string[] BenchmarkMenuLabels = ["Skirmish Small (5v5)", "Skirmish Medium (15v15)", "Skirmish Large (30v30)", "Skirmish Epic (60v60)", "Replay recorded battle", "Skip / Back"];
    private void UpdateBenchmarkMenu(bool up, bool down, bool enter, bool esc)
    {
        if (up && !_iUp) _benchMenuIndex = (_benchMenuIndex + 5) % 6;
        if (down && !_iDown) _benchMenuIndex = (_benchMenuIndex + 1) % 6;
        if ((esc && !_iEsc) || (enter && !_iEnter && _benchMenuIndex == 5))
        {
            var prefs = BenchmarkPreferences.Load();
            (prefs with { OfferHandled = true }).Save();
            _introView = IntroView.Main; return;
        }
        if (enter && !_iEnter)
        {
            (BenchmarkPreferences.Load() with { OfferHandled = true }).Save();
            BeginBenchmark(new(_benchMenuIndex == 4 ? "replay" : "skirmish", (SkirmishSize)Math.Min(3, _benchMenuIndex),
                _benchMenuIndex == 4 ? BattleReplay.DefaultPath : null, 150,
                Path.Combine(PlayerConfig.DirectoryPath, "benchmark-results.json"), false, null), true);
        }
    }
    private void DrawBenchmarkMenu(float w, float h, Color title, Color dim, Color text)
    {
        DrawIntroTitle(w, "Benchmark", _introView == IntroView.BenchmarkOffer ? "Find settings for this machine (optional)" : "Measure and auto-tune this machine", title, dim);
        for (int i = 0; i < BenchmarkMenuLabels.Length; i++)
            _spriteBatch.DrawString(_hudSmall, (i == _benchMenuIndex ? "> " : "  ") + BenchmarkMenuLabels[i], new Vector2(w * .2f, 210 + i * 38), i == _benchMenuIndex ? Color.Yellow : Color.White);
        _spriteBatch.DrawString(_hudSmall, "3 runs plus a short settings check. Skirmish: 150s/run; replay: clip length.\nArrows / Enter to select. Escape skips.", new Vector2(w * .12f, h - 78), dim);
    }
    private BenchmarkCommand? _benchmark;
    private BenchmarkResult? _benchResult;
    private readonly List<BenchmarkRun> _benchRuns = [];
    private readonly List<TuneVerification> _benchTune = [];
    private readonly List<double> _benchFrames = [], _benchCpu = [], _benchGpu = [];
    private readonly List<string> _benchHashes = [];
    private short? _benchViewSector;
    private byte _benchCameraPhase;
    private BattleReplay? _benchReplay;
    private WireRecording? _benchRecording;
    private BenchmarkCounters? _benchCounters;
    private BenchmarkRecommendation? _benchSuggestion, _benchOriginal;
    private bool _benchMeasuring, _benchPending, _benchFromMenu, _benchEnter, _benchEsc;
    private long _benchNextCommand, _benchNextView;
    private long _benchStart, _benchWarmup, _benchFirstFrame, _benchStartup;
    private int _benchReplayFrameStart, _benchReplayWarmupUntil;
    private string _benchStatus = "";
    private bool _benchmarkApplied, _benchOwnsTelemetry;
    private bool _benchOwnPrediction, _benchRemotePrediction;
    private bool _benchExitAfterDraw, _benchMouseDown, _benchWasApplied, _benchOriginalDynamic;
    private float _benchmarkEffectsDensity = 1, _benchmarkViewDistance = 6000;
    private Rectangle _benchApplyButton;
    public bool BenchmarkFailed { get; private set; }
    private long ClientNowMilliseconds => _benchReplay is { } replay ? replay.Frame * 1000L / 60 : Environment.TickCount64;

    private void InitializeBenchmark()
    {
        var saved = BenchmarkPreferences.Load();
        if (saved.Applied is { } settings)
        {
            _skirmishScreen.ApplyDefaults(settings);
            ApplyBenchmarkSettings(settings, preserveUpscaling: true);
        }
        if (_benchmark is not null) BeginBenchmark(_benchmark, false);
        else if (_state == ClientState.Intro && !saved.OfferHandled) _introView = IntroView.BenchmarkOffer;
    }
    private BenchmarkRecommendation CurrentBenchmarkSettings() => new()
    {
        BattleSize = _skirmishScreen.Options.Size, Difficulty = _skirmishScreen.Options.Difficulty,
        ResearchSpeed = _skirmishScreen.Options.ResearchSpeed, SpectatorDefault = _skirmishScreen.Options.SpectatorDefault,
        Fsr = _upscaling.Upscaling == UpscalingMode.FSR1, FsrPreset = (int)_upscaling.Preset,
        HdPbr = _nativeShipRenderer.HdPbrEnabled, HdTextures = _upscaling.HdTextures, Bloom = _bloomEnabled,
        EffectsDensity = _benchmarkEffectsDensity, ViewDistance = _benchmarkViewDistance,
        Vsync = _graphics.SynchronizeWithVerticalRetrace, FrameCap = IsFixedTimeStep ? (int)Math.Round(1 / TargetElapsedTime.TotalSeconds) : 0
    };
    private void ApplyBenchmarkSettings(BenchmarkRecommendation s, bool preserveUpscaling = false)
    {
        _benchmarkApplied = true;
        if (!preserveUpscaling)
        {
            _upscaling.Upscaling = s.Fsr ? UpscalingMode.FSR1 : UpscalingMode.Off;
            _upscaling.Preset = (FsrPreset)Math.Clamp(s.FsrPreset, 0, 3);
            _upscaling.HdTextures = s.HdTextures;
            _upscaling.DynamicResolution = false;
        }
        _nativeShipRenderer.HdTexturesEnabled = _upscaling.HdTextures;
        _nativeShipRenderer.HdPbrEnabled = s.HdPbr;
        _bloomEnabled = s.Bloom;
        _benchmarkEffectsDensity = Math.Clamp(s.EffectsDensity, .1f, 1);
        _benchmarkViewDistance = Math.Clamp(s.ViewDistance, 2000, 6000);
        _graphics.SynchronizeWithVerticalRetrace = s.Vsync;
        IsFixedTimeStep = s.FrameCap > 0;
        if (s.FrameCap > 0) TargetElapsedTime = TimeSpan.FromSeconds(1d / s.FrameCap);
        _graphics.ApplyChanges();
    }
    private void BeginBenchmark(BenchmarkCommand command, bool fromMenu)
    {
        _benchmark = command; _benchExitAfterDraw = command.ExitWhenComplete; _benchFromMenu = fromMenu; _benchResult = null; BenchmarkFailed = false;
        _benchRuns.Clear(); _benchTune.Clear(); _benchSuggestion = null;
        _benchOriginal = CurrentBenchmarkSettings();
        _benchWasApplied = _benchmarkApplied; _benchOriginalDynamic = _upscaling.DynamicResolution;
        _benchOwnPrediction = _ownShipPrediction; _benchRemotePrediction = _remoteShipDeadReckoning;
        if (command.Mode == "replay") { _ownShipPrediction = false; _remoteShipDeadReckoning = false; }
        _upscaling.DynamicResolution = false;
        _graphics.SynchronizeWithVerticalRetrace = false; IsFixedTimeStep = false; _graphics.ApplyChanges();
        _benchOwnsTelemetry = _frameTelemetry is null;
        EnableFrameTelemetry();
        _frameTelemetry!.SampleCompleted -= OnBenchmarkSample;
        _frameTelemetry.SampleCompleted += OnBenchmarkSample;
        _benchPending = true;
    }
    private void StartBenchmarkRun()
    {
        _benchPending = false; _benchMeasuring = false; _benchWarmup = 0; _benchNextCommand = 0; _benchNextView = 0; _benchViewSector = null; _benchCameraPhase = 0;
        _benchStartup = Stopwatch.GetTimestamp();
        _benchFrames.Clear(); _benchCpu.Clear(); _benchGpu.Clear(); _benchHashes.Clear();
        _nowSeconds = 0; _gameSeconds = 0; _trailNowSec = 0;
        _trailRandom = new RandomTrailSource(729);
        _debrisGeo = new DebrisGeo(1, _trailRandom);
        _flightEffects.BeginView(-2 - _benchRuns.Count - _benchTune.Count, null);
        _damageEmitters.Clear(); _engineTrailEmitters.Clear(); _shipThrustPower.Clear();
        _nozzleGlowEmitters.Clear(); _navStrobeEmitters.Clear();
        _pendingHullHits.Clear(); _pendingTeleportBurst = null; _camSmoothInit = false;
        _freeCamActive = false; _orbitCamActive = false; _spectatorFollowId = -1; _hudHidden = false;
        _autoPilotEngaged = false; _autoTarget = null;
        _asteroidSpins.Clear(); _treasureAge.Clear(); _directorActive = false;
        _nativeShipRenderer.ResetHdResidency();
        _benchStatus = _benchSuggestion is null ? $"Benchmark {_benchmark!.Mode}: run {_benchRuns.Count + 1}/3" : $"Auto-tune verification {_benchTune.Count + 1}/2";
        _session.Dispose(); _session = new ConnectionSession();
        _session.World.RemoteShipHullResolver = ResolveRemoteHullStats;
        _ownReady = true; _dockedActive = false;
        _state = ClientState.Intro;
        if (_benchmark!.Mode == "replay")
        {
            _benchReplay = new BattleReplay(_benchmark.ReplayFile!);
            _state = ClientState.ReadyCheck;
            if (_benchSuggestion is not null)
            {
                int seekFrame = (int)(_benchReplay.DurationMicroseconds / 1_000_000d * .60 * 60);
                bool HasBattle()
                {
                    var world = _session.World;
                    short? view = _benchReplay.ViewedSector;
                    if (view is null && world.MyShipId is { } id && world.Ships.TryGetValue(id, out var own)) view = own.SectorId;
                    return view is { } sector && world.Ships.Values.Where(s => s.SideId >= 0 && s.CanDrawInSector(sector))
                        .Select(s => s.SideId).Distinct().Take(2).Count() == 2;
                }
                while (!_benchReplay.Finished && (_benchReplay.Frame < seekFrame || !HasBattle())) _benchReplay.Advance(_session);
                if (_benchReplay.Finished) throw new InvalidDataException("Recording contains no visible battle for verification");
            }
            if (_benchSuggestion is null) StartBenchmarkMeasurement();
            else _benchReplayWarmupUntil = _benchReplay.Frame + 2 * BattleReplay.FramesPerSecond;
        }
        else
        {
            _benchRecording = _benchRuns.Count == 0 && _benchSuggestion is null && _benchmark.RecordFile is { } file ? new WireRecording(file) : null;
            _session.Recording = _benchRecording;
            _skirmishCancel?.Dispose(); _skirmishCancel = new();
            var options = _benchmark.Options;
            if (_benchSuggestion is { } s) options = options with { Size = s.BattleSize, Difficulty = s.Difficulty, ResearchSpeed = s.ResearchSpeed, SpectatorDefault = s.SpectatorDefault };
            _skirmishStart = SkirmishHost.StartAsync(options, cancellationToken: _skirmishCancel.Token);
        }
    }
    private void StartBenchmarkMeasurement()
    {
        if (IsBriefingVisible(out _)) DismissBriefing();
        _benchMeasuring = true; _benchStart = Stopwatch.GetTimestamp();
        _benchFirstFrame = _frameTelemetry!.FrameId + 1;
        _benchReplayFrameStart = _benchReplay?.Frame ?? 0;
        _benchCounters = new(_skirmish?.BotProcessIds, _skirmish?.Stats ?? default);
        _skirmish?.BeginMeasurement();
    }
    private void OnBenchmarkSample(long id, double frame, double cpu, double? gpu)
    {
        if (!_benchMeasuring || id < _benchFirstFrame) return;
        _benchFrames.Add(frame); _benchCpu.Add(cpu);
        if (gpu is { } ms) _benchGpu.Add(ms);
    }
    private bool UpdateBenchmark(ref GameTime gameTime)
    {
        var keys = Keyboard.GetState();
        bool enter = keys.IsKeyDown(Keys.Enter), esc = keys.IsKeyDown(Keys.Escape);
        try
        {
            if (_benchResult is not null)
            {
                if ((enter && !_benchEnter) || (!_benchMouseDown && Mouse.GetState().LeftButton == ButtonState.Pressed && _benchApplyButton.Contains(Mouse.GetState().Position)))
                {
                    ApplyBenchmarkSettings(_benchResult.Recommendation);
                    _skirmishScreen.ApplyDefaults(_benchResult.Recommendation);
                    new BenchmarkPreferences { OfferHandled = true, Applied = _benchResult.Recommendation }.Save();
                    _upscaling.Save(UpscalingSettings.DefaultPath);
                    _benchStatus = "Applied and saved. Escape returns to menu.";
                }
                if (esc && !_benchEsc) { _benchResult = null; _state = ClientState.Intro; _introView = IntroView.Main; }
                return true;
            }
            if (_benchmark is null) return false;
            if (esc && !_benchEsc) { FinishBenchmarkFailure("Benchmark cancelled", false); return true; }
            if (_benchPending) StartBenchmarkRun();
            if (_benchReplay is { } replay)
            {
                replay.Advance(_session);
                _benchViewSector = replay.ViewedSector; _benchCameraPhase = replay.CameraPhase;
                if (!_benchMeasuring && replay.Frame >= _benchReplayWarmupUntil) StartBenchmarkMeasurement();
                if (_benchMeasuring) _benchHashes.Add(replay.FrameStateHash(_session));
                gameTime = new(TimeSpan.FromSeconds(replay.Frame / 60d), TimeSpan.FromSeconds(1d / 60));
                if (_session.World.MyShipId is { } id && _session.World.Ships.TryGetValue(id, out var ship) && ship.Latest is not null)
                    _state = ClientState.Flying;
                if (!_benchMeasuring && replay.Finished) throw new InvalidDataException("Recording ends before verification warmup completes");
                if (_benchMeasuring && (replay.Finished || (_benchSuggestion is not null && replay.Frame - _benchReplayFrameStart >= 12 * 60)))
                { CompleteBenchmarkRun(); return true; }
            }
            else if (_skirmish is { } host)
            {
                if (!host.BotsAlive) throw new IOException(host.Failure);
                lock (_session.Gate)
                {
                    var world = _session.World;
                    if (world.MyShipId is { } id && world.Ships.TryGetValue(id, out var ship))
                    {
                        bool send = Environment.TickCount64 >= _benchNextCommand;
                        if (send) { _session.SendPlayerReady(true, id); _benchNextCommand = Environment.TickCount64 + 1000; }
                        if (world.Mission?.Stage == MissionStageMessage.StageStarted)
                        {
                            if (ship.StationId >= 0 && ship.Latest is not null && send)
                                _session.SendBuyLoadout(ship.HullId, true, ship.Loadout);
                            else if (ship.StationId < 0 && ship.Latest is not null)
                            {
                                _state = ClientState.Flying; _dockedActive = false;
                                if (_benchWarmup == 0) _benchWarmup = Stopwatch.GetTimestamp();
                                if (!_benchMeasuring) UpdateBenchmarkView();
                                bool battleReady = _benchViewSector is { } sector
                                    && world.Ships.Values.Any(s => s.SideId == 0 && s.CanDrawInSector(sector))
                                    && world.Ships.Values.Any(s => s.SideId == 1 && s.CanDrawInSector(sector));
                                if (!_benchMeasuring && battleReady && Stopwatch.GetElapsedTime(_benchWarmup).TotalSeconds >= 3) StartBenchmarkMeasurement();
                            }
                        }
                    }
                }
                if (_benchMeasuring) UpdateBenchmarkView();
                if (_benchMeasuring && Stopwatch.GetElapsedTime(_benchStart).TotalSeconds >= (_benchSuggestion is null ? _benchmark.Seconds : 12))
                { CompleteBenchmarkRun(); return true; }
            }
            if (!_benchMeasuring && Stopwatch.GetElapsedTime(_benchStartup).TotalSeconds > 180) throw new TimeoutException("Benchmark battle did not start: " + _skirmishError);
            return false;
        }
        catch (Exception ex) { FinishBenchmarkFailure(ex.Message, true); return true; }
        finally { _benchEnter = enter; _benchEsc = esc; _benchMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed; }
    }
    private void UpdateBenchmarkView()
    {
        double seconds = Stopwatch.GetElapsedTime(_benchStart).TotalSeconds;
        byte phase = !_benchMeasuring || _benchSuggestion is not null ? (byte)1 : BenchmarkRoute.Phase(seconds, _benchmark!.Seconds);
        lock (_session.Gate)
        {
            var world = _session.World;
            short home = world.Stations.Values.Where(s => s.SideId == 0).OrderBy(s => s.StationId).First().ClusterId;
            short battle = world.Clusters.Values.First(s => s.Name == "Alderan").ClusterId;
            short sector = phase == 1 ? battle : home;
            bool changed = _benchViewSector != sector || _benchCameraPhase != phase;
            if (!changed && Environment.TickCount64 < _benchNextView) return;
            _benchNextView = Environment.TickCount64 + 2000;
            _benchViewSector = sector; _benchCameraPhase = phase;
            _session.SendViewClusterRequest(sector);
            world.TelemetrySectorOverride = sector;
            if (changed) _benchRecording?.WriteCamera(sector, phase);
        }
    }
    private long BenchmarkCombatCounter(string key)
    {
        var stats = _skirmish?.Stats ?? default;
        return stats.ValueKind == System.Text.Json.JsonValueKind.Object && stats.TryGetProperty("combat", out var combat)
            && combat.TryGetProperty(key, out var value) ? (long)value.GetDouble() : 0;
    }
    private void CompleteBenchmarkRun()
    {
        _benchMeasuring = false;
        var distribution = Distribution.Of(_benchFrames);
        int lowCount = Math.Max(1, (int)Math.Ceiling(_benchFrames.Count * .01));
        var run = _benchCounters!.Finish(_benchRuns.Count + 1, _skirmish) with
        {
            Rendered = true, FrameMs = distribution,
            FpsAverage = distribution.Mean > 0 ? 1000 / distribution.Mean : null,
            FpsOnePercentLow = _benchFrames.Count > 0 ? 1000 / _benchFrames.OrderDescending().Take(lowCount).Average() : null,
            CpuMs = _benchCpu.Count > 0 ? _benchCpu.Average() : null,
            GpuMs = _benchGpu.Count > 0 ? _benchGpu.Average() : null,
            FrameStateHashes = _benchHashes.ToArray(),
            Shots = BenchmarkCombatCounter("shotsFired"), Hits = BenchmarkCombatCounter("shipHits")
        };
        if (_skirmish is { } measuredHost && run.BotCount != measuredHost.Options.TeamSize * 2 - 1)
            throw new IOException("Benchmark fleet lost a bot; refusing to report a partial preset");
        _session.Recording = null; _session.Dispose(); _benchRecording?.Dispose(); _benchRecording = null;
        _skirmish?.Dispose(); _skirmish = null; _benchReplay = null;
        if (_benchSuggestion is null)
        {
            _benchRuns.Add(run);
            if (_benchRuns.Count < 3) { _benchPending = true; return; }
            if (_benchmark!.Mode == "replay" && _benchRuns.Skip(1).Any(r => !r.FrameStateHashes.SequenceEqual(_benchRuns[0].FrameStateHashes)))
                throw new InvalidDataException("Replay determinism failed");
            var median = BenchmarkResult.MedianOf(_benchRuns);
            _benchSuggestion = BenchmarkPolicy.Recommend(median, _benchmark.Size, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height);
            ApplyBenchmarkSettings(_benchSuggestion);
            // Verify uncapped to measure headroom; persist the proposed cap only on Apply.
            IsFixedTimeStep = false;
            _benchPending = true; return;
        }
        bool met = BenchmarkPolicy.MeetsTarget(run);
        _benchTune.Add(new(_benchSuggestion with { FrameCap = 0, Vsync = false }, run, met));
        if (!met && _benchTune.Count == 1)
        {
            _benchSuggestion = BenchmarkPolicy.StepDown(_benchSuggestion);
            ApplyBenchmarkSettings(_benchSuggestion); IsFixedTimeStep = false;
            _benchPending = true; return;
        }
        var selected = BenchmarkResult.MedianOf(_benchRuns);
        var result = new BenchmarkResult
        {
            Mode = _benchmark!.Mode, Preset = _benchmark.Size.ToString(), DurationSeconds = _benchmark.Mode == "replay" ? new BattleReplay(_benchmark.ReplayFile!).DurationMicroseconds / 1_000_000d : _benchmark.Seconds,
            Renderer = _gpuTimer?.Renderer ?? GraphicsDevice.Adapter.Description, Width = GraphicsDevice.Viewport.Width, Height = GraphicsDevice.Viewport.Height,
            TestedSettings = _benchOriginal! with
            {
                FrameCap = 0, Vsync = false, BattleSize = _benchmark.Size,
                Difficulty = _benchmark.Options.Difficulty, ResearchSpeed = _benchmark.Options.ResearchSpeed,
                SpectatorDefault = _benchmark.Options.SpectatorDefault
            },
            ReplaySha256 = _benchmark.Mode == "replay" ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_benchmark.ReplayFile!))) : null, Runs = _benchRuns.ToArray(), Median = selected,
            Bottleneck = BenchmarkPolicy.Bottleneck(selected), Recommendation = _benchSuggestion, AutoTune = _benchTune.ToArray(),
            TargetStatus = met ? "Verified uncapped: 60 FPS 1% low and server tick p95 < 50 ms (when present)" : "Target unmet after one step down; lowest tested settings shown"
        };
        result.Save(_benchmark.Output);
        if (_benchOriginal is { } original) ApplyBenchmarkSettings(original);
        _benchmarkApplied = _benchWasApplied; _upscaling.DynamicResolution = _benchOriginalDynamic;
        _ownShipPrediction = _benchOwnPrediction; _remoteShipDeadReckoning = _benchRemotePrediction;
        StopBenchmarkTelemetry();
        _benchResult = result; _benchmark = null;
        _state = ClientState.Intro; _introView = IntroView.Main;
        _benchStatus = "Enter or click Apply to save; Escape keeps previous settings.";
    }
    private void FinishBenchmarkFailure(string reason, bool failed)
    {
        BenchmarkFailed = failed; _benchMeasuring = false; _benchPending = false;
        _skirmishCancel?.Cancel();
        _session.Recording = null; _session.Dispose();
        _benchRecording?.Dispose(); _benchRecording = null;
        _skirmish?.Dispose(); _skirmish = null; _benchReplay = null;
        if (_benchOriginal is { } original) ApplyBenchmarkSettings(original);
        _benchmarkApplied = _benchWasApplied; _upscaling.DynamicResolution = _benchOriginalDynamic;
        _ownShipPrediction = _benchOwnPrediction; _remoteShipDeadReckoning = _benchRemotePrediction;
        StopBenchmarkTelemetry();
        _benchmark = null; _benchStatus = reason;
        _state = ClientState.Intro; _introView = IntroView.Main;
        Console.Error.WriteLine("[benchmark] " + reason);
        if (!_benchFromMenu) Exit();
    }
    private void StopBenchmarkTelemetry()
    {
        if (_frameTelemetry is not null) _frameTelemetry.SampleCompleted -= OnBenchmarkSample;
        if (!_benchOwnsTelemetry) return;
        _gpuTimer?.Dispose(); _gpuTimer = null;
        _frameTelemetry?.Dispose(); _frameTelemetry = null; _receiveGpu = null;
        _benchOwnsTelemetry = false;
    }
    private void DisposeBenchmark()
    {
        _session.Recording = null;
        _benchRecording?.Dispose(); _benchRecording = null;
    }
    internal static Matrix WithBenchmarkViewDistance(Matrix projection, float distance)
    {
        if (MathF.Abs(projection.M33) < 1e-10f) return projection;
        float near = projection.M43 / projection.M33;
        float far = Math.Max(near + 1, distance);
        bool perspective = MathF.Abs(projection.M34) > .5f;
        projection.M33 = perspective ? far / (near - far) : 1 / (near - far);
        projection.M43 = perspective ? near * far / (near - far) : near / (near - far);
        return projection;
    }
    private bool OutsideBenchmarkView(Vector3 position, Vector3 eye, float radius = 0) =>
        _cameraMode != CameraMode.Command && (_benchmarkApplied || _benchmark is not null)
        && Vector3.DistanceSquared(position, eye) > MathF.Pow(_benchmarkViewDistance + radius, 2);
    private void ReduceBenchmarkEffects<T>(List<T> list)
    {
        if (_benchmarkEffectsDensity >= 1) return;
        int count = (int)(list.Count * _benchmarkEffectsDensity);
        if (count < list.Count) list.RemoveRange(count, list.Count - count);
    }
    private void ComputeBenchmarkCamera(Vector3 myPos, Matrix myRot, Allegiance.Core.Sim.Orientation orientation,
        float aspect, float dt, out Matrix view, out Matrix projection, out Vector3 eye)
    {
        double seconds = _benchReplay is { } replay ? replay.Frame / 60d : Stopwatch.GetElapsedTime(_benchStart).TotalSeconds;
        int phase = _benchCameraPhase;
        _cameraMode = CameraMode.Chase;
        if (phase == 0)
        {
            (view, projection, eye) = ComputeFlightCamera(myPos, myRot, orientation, aspect, dt);
            return;
        }
        Vector3 target = myPos;
        lock (_session.Gate)
        {
            var world = _session.World;
            if (phase == 1)
            {
                var positions = world.Ships.Values.Where(s => s.CanDrawInSector(RenderSector(world.Ships[world.MyShipId!.Value].SectorId)))
                    .Select(s => PoseConverter.ToXna(s.Latest!.Position)).ToArray();
                if (positions.Length > 0) target = positions.Aggregate(Vector3.Zero, (a, b) => a + b) / positions.Length;
            }
            else
            {
                var station = world.Stations.Values.Where(s => s.ClusterId == RenderSector(world.Ships[world.MyShipId!.Value].SectorId)).OrderBy(s => s.StationId).FirstOrDefault();
                if (station.Position is { Length: >= 3 }) target = new(station.Position[0], station.Position[1], station.Position[2]);
            }
        }
        float angle = (float)seconds * .12f;
        eye = target + new Vector3(MathF.Cos(angle) * 950, 300, MathF.Sin(angle) * 950);
        view = Matrix.CreateLookAt(Vector3.Zero, target - eye, Vector3.UnitY);
        projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50), aspect, 1, _benchmarkViewDistance);
    }
    private void DrawBenchmark()
    {
        if (_benchmark is null && _benchResult is null && _benchStatus.Length == 0) return;
        _spriteBatch.Begin();
        if (_benchResult is { } result)
        {
            _spriteBatch.Draw(_whitePixel, new Rectangle(0, 0, GraphicsDevice.Viewport.Width, GraphicsDevice.Viewport.Height), new Color(4, 12, 20, 250));
            string serverText = result.Mode == "replay" ? "Server / bots: not used (client-only replay)" :
                $"Server mean/p95/worst {result.Median.ServerTickMs?.Mean:F2}/{result.Median.ServerTickMs?.P95:F2}/{result.Median.ServerTickMs?.Worst:F2} ms; workers {result.Median.WorkerCount}\n" +
                $"Bot CPU {result.Median.AmortizedCpuMsPerBotSecond:F2} ms/bot/s (worker share); individual decision CPU in JSON";
            string text = $"Benchmark results (median of 3) - {result.Bottleneck}\n" +
                $"FPS {result.Median.FpsAverage:F1} / 1% low {result.Median.FpsOnePercentLow:F1}; p50/p99 {result.Median.FrameMs?.P50:F2}/{result.Median.FrameMs?.P99:F2} ms\n" +
                $"CPU/GPU {result.Median.CpuMs:F2}/{result.Median.GpuMs:F2} ms; {result.Renderer}\n" +
                serverText + "\n" +
                $"Memory {result.Median.WorkingSetBytes / 1048576d:F0} MiB RSS / {result.Median.ManagedBytes / 1048576d:F0} MiB heap; GC {string.Join("/", result.Median.GcCollections)}, pause {result.Median.GcPauseMs:F2} ms\n{result.TargetStatus}\n" +
                string.Join("\n", result.Recommendation.Reasons.Select(r => $"{r.Setting}: {r.Value} - {r.Reason}"));
            _spriteBatch.DrawString(_hudSmall, text, new Vector2(18, 30), Color.White);
            _benchApplyButton = new(18, GraphicsDevice.Viewport.Height - 68, 340, 42);
            _spriteBatch.Draw(_whitePixel, _benchApplyButton, new Color(25, 95, 110));
            _spriteBatch.DrawString(_hudSmall, "Apply recommended settings", new Vector2(26, _benchApplyButton.Y + 10), Color.White);
        }
        else _spriteBatch.Draw(_whitePixel, new Rectangle(0, 34, GraphicsDevice.Viewport.Width, 36), new Color(0, 0, 0, 220));
        _spriteBatch.DrawString(_hudSmall, _benchmark is not null ? _benchStatus + " - Escape cancels" : _benchStatus,
            new Vector2(18, _benchResult is null ? 42 : GraphicsDevice.Viewport.Height - 22), Color.Cyan);
        _spriteBatch.End();
    }
}
