// P834 — repeatable local cost harness for the raymarched volumetric explosions.
//
// Renders the REAL VolumetricRenderer (same compiled Volumetric.fx, same LOD /
// budget / half-res code paths) into a REAL DesktopGL window at a requested
// backbuffer size, with a deterministic synthetic blast at a requested
// proximity, and logs the GPU time of the volumetric pass from GL_TIME_ELAPSED
// timer queries plus the wall frame-time percentiles (vsync off).
//
// Why: P579 tuned the coverage LOD thresholds, the per-frame MaxPixelSteps
// budget and the half-res threshold on the old Arrow-Lake iGPU at 2560×1440.
// This harness reproduces the reported "camera close to the explosion" dip at
// 1920×1080 / 2560×1440 / 3840×2160 / 5120×2160 on this workstation's GPU so
// the before/after curve is measured, not guessed.
//
// Scenarios are label:type:distance (distance in world units, directly ahead):
//   fighter far    3:1500
//   fighter near   3:450
//   fighter full   3:150   (diameter ≈ 1.0 × viewport height at 50° FOV)
//   bomber full    4:450   (diameter ≈ 1.6 × viewport height)
//   station full   7:800   (diameter ≈ 1.9 × viewport height)
//   inside         4:60    (camera inside the 165 u bomber volume)
//
// Usage (from the repo root, windowed on :0):
//   tools/p834-vol-perf/bin/Debug/net10.0/p834-vol-perf \
//     --res 1920x1080,2560x1440,3840x2160,5120x2160 \
//     --frames 150 --warmup 40 --csv .scratch/p834/before.csv --png .scratch/p834/before
//
//   --forced none|minimal|low|med|high|ultra   pin every volume's raymarch LOD
//   --budget <pixel-steps>             override the per-frame budget
//   --age <sec>                        explosion age used for the samples
//   --yaw DEG / --pitch DEG            turn the camera (the blast stays straight ahead)
//   --shader PATH                      pin the .mgfxo under test (P834b: the harness used
//                                      to read the build OUTPUT copy, so a stale bin file
//                                      could silently be measured instead of the shader
//                                      you just copied into Content/)
//   --help
//
// P834b — every pass also prints the CPU-side expectation (blast view-space
// position / projected coverage) and, when --png is on, the captured frame's
// mean luma + lit coverage, so a cell whose plume went missing is visible in the
// numbers and not only in the picture.

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Collision;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;
using XnaVec = Microsoft.Xna.Framework.Vector3;

internal sealed record Resolution(int Width, int Height)
{
    public override string ToString() => $"{Width}x{Height}";
}

internal sealed record Scenario(string Label, int Type, float Distance)
{
    public override string ToString() => $"{Label}(type {Type} @ {Distance:0}u)";
}

internal sealed record Sample(int Frame, double GpuMs, double FrameMs);

internal sealed class Options
{
    public List<Resolution> Resolutions = [new(2560, 1440)];
    public List<Scenario> Scenarios =
    [
        new("far", 3, 1500f),
        new("near", 3, 450f),
        new("full", 3, 150f),
        new("bomber", 4, 450f),
        new("station", 7, 800f),
        new("inside", 4, 60f),
    ];
    public int WarmupFrames = 40;
    public int MeasureFrames = 150;
    public float AgeSeconds = 0.80f;     // first 2-3 sub-volumes at full radius, still hot
    public float FovDegrees = 50f;       // FlightCameraZoom default
    public float YawDegrees = 0f;        // camera + blast rotated about +Y (180 = looking down world +Z)
    public float PitchDegrees = 0f;      // camera + blast pitched about the camera's right axis
    public string? ShaderPath;           // pin the .mgfxo under test (else the built copy)
    public string? ForcedLevel;
    public float? Budget;
    public string? CsvPath;
    public string? PngDir;
    public string? Label = "";
    /// <summary>Optional synthetic linear-depth "obstruction": distance in world
    /// units + screen-radius fraction of the disc (0 = no depth texture).</summary>
    public (float Distance, float RadiusFraction)? DepthObstruction;
}

internal static class Program
{
    private static int Main(string[] args)
    {
        var opts = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--res": opts.Resolutions = ParseResolutions(args[++i]); break;
                case "--scenarios": opts.Scenarios = ParseScenarios(args[++i]); break;
                case "--warmup": opts.WarmupFrames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--frames": opts.MeasureFrames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--age": opts.AgeSeconds = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--fov": opts.FovDegrees = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--yaw": opts.YawDegrees = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--pitch": opts.PitchDegrees = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--shader": opts.ShaderPath = Path.GetFullPath(args[++i]); break;
                // P834b — accept every VolumeLod.Level, not the P579 four: the
                // P834 "Minimal" (screen-filling / camera-inside) rung could not be
                // pinned at all before, which is the level this task measures.
                case "--forced":
                    {
                        var want = args[++i];
                        opts.ForcedLevel = want.ToLowerInvariant() is "none" or "off"
                            ? null
                            : Enum.TryParse<VolumeLod.Level>(want, ignoreCase: true, out var lvl)
                                ? lvl.ToString()
                                : throw new ArgumentException(
                                    $"bad --forced '{want}' (one of none, {string.Join(", ", Enum.GetNames<VolumeLod.Level>())})");
                        break;
                    }
                case "--budget": opts.Budget = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--csv": opts.CsvPath = args[++i]; break;
                case "--png": opts.PngDir = args[++i]; break;
                case "--label": opts.Label = args[++i]; break;
                case "--depth":
                    {
                        var d = args[++i].Split(':');
                        opts.DepthObstruction = (float.Parse(d[0], CultureInfo.InvariantCulture),
                            d.Length > 1 ? float.Parse(d[1], CultureInfo.InvariantCulture) : 0.45f);
                        break;
                    }
                case "--help":
                    Console.WriteLine(HelpText);
                    return 0;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        Console.Error.WriteLine($"unknown option '{args[i]}'\n{HelpText}");
                        return 2;
                    }
                    break;
            }
        }
        if (opts.PngDir is not null) Directory.CreateDirectory(opts.PngDir);
        if (opts.CsvPath is not null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(opts.CsvPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        var contentRoot = opts.ShaderPath ?? ResolveContentRoot();
        if (!File.Exists(contentRoot))
        {
            Console.Error.WriteLine($"shader not found: {contentRoot}");
            return 2;
        }
        using var game = new VolPerfGame(opts, contentRoot);
        game.Run();
        return VolPerfGame.ExitCode;
    }

    private static List<Resolution> ParseResolutions(string raw)
    {
        var list = new List<Resolution>();
        foreach (var tok in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = tok.Trim().Split('x');
            if (p.Length != 2) throw new ArgumentException($"bad resolution '{tok}'");
            list.Add(new Resolution(int.Parse(p[0], CultureInfo.InvariantCulture),
                                    int.Parse(p[1], CultureInfo.InvariantCulture)));
        }
        return list;
    }

    private static List<Scenario> ParseScenarios(string raw)
    {
        var list = new List<Scenario>();
        foreach (var tok in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = tok.Trim().Split(':');
            if (p.Length == 2)
                list.Add(new Scenario(p[0], 3, float.Parse(p[1], CultureInfo.InvariantCulture)));
            else if (p.Length == 3)
                list.Add(new Scenario(p[0], int.Parse(p[1], CultureInfo.InvariantCulture),
                                      float.Parse(p[2], CultureInfo.InvariantCulture)));
            else throw new ArgumentException($"bad scenario '{tok}'");
        }
        return list;
    }

    /// <summary>The repo root (the directory holding AllegianceCore.slnx).</summary>
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }

    /// <summary>The compiled shader + mono content lives in the main head's
    /// output (built by the solution). Resolve it from the repo root so the
    /// harness can run from anywhere inside the worktree.
    ///
    /// P834b — this prefers the build OUTPUT copy, which is a stale trap: copy a
    /// different <c>Volumetric.ogl.mgfxo</c> into <c>Content/Shaders/</c> without
    /// re-running the MonoGame build and the harness keeps measuring the old
    /// bytecode (that is how "the P834 shader" and "the WIP shader" once measured
    /// the same). Use <c>--shader PATH</c> to pin the exact file; every run prints
    /// the sha256 of what it actually loaded.</summary>
    private static string ResolveContentRoot()
    {
        var root = RepoRoot();
        var candidates = new[]
        {
            Path.Combine(root, "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content/Shaders/Volumetric.ogl.mgfxo"),
            Path.Combine(root, "src/Allegiance.ClientCore.MonoGame/bin/Release/net10.0/Content/Shaders/Volumetric.ogl.mgfxo"),
            Path.Combine(root, "src/Allegiance.ClientCore.MonoGame/Content/Shaders/Volumetric.ogl.mgfxo"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        throw new FileNotFoundException("Volumetric.ogl.mgfxo not found; build the client first", candidates[0]);
    }

    private static readonly string HelpText =
        "p834-vol-perf — volumetric explosion GPU cost harness\n" +
        "  --res WxH[,WxH]            backbuffer resolutions (default 2560x1440)\n" +
        "  --scenarios l:t:d[,...]    label:explosionType:distance (default far/near/full/bomber/station/inside)\n" +
        "  --frames N                 measured frames per scenario (default 150)\n" +
        "  --warmup N                 warm-up frames (default 40)\n" +
        "  --age S                    explosion age in seconds (default 0.80)\n" +
        "  --fov DEG                  vertical FOV (default 50)\n" +
        "  --yaw DEG                  turn the camera + blast about +Y (default 0 = world -Z)\n" +
        "  --pitch DEG                pitch the camera + blast about its right axis (default 0)\n" +
        "  --shader PATH              .mgfxo under test (default: the built copy under bin/)\n" +
        $"  --forced none|{string.Join("|", Enum.GetNames<VolumeLod.Level>().Select(n => n.ToLowerInvariant()))}  pin the raymarch LOD\n" +
        "  --budget N                 per-frame pixel-step budget override\n" +
        "  --csv PATH                 write per-sample CSV\n" +
        "  --png DIR                  save one real backbuffer PNG per scenario/resolution\n" +
        "  --depth DIST[:R]           synthetic linear-depth disc (obstruction) at DIST world units, R of the screen height (default 0.45)\n" +
        "  --label S                  free-form label echoed into the CSV/console\n";
}

/// <summary>The MonoGame host: one window, resized in process between passes,
/// drawing the real renderer with a deterministic blast in front of the camera.</summary>
internal sealed class VolPerfGame : Game
{
    public static int ExitCode { get; private set; }

    private readonly GraphicsDeviceManager _gdm;
    private readonly Options _opts;
    private readonly string _shaderPath;
    private GlFrameTimer? _timer;

    private Effect? _effect;
    private VolumetricRenderer? _vol;
    private Texture2D? _depthTex;
    private int _depthW, _depthH;
    private SoftParticleContext _soft = SoftParticleContext.Off;
    private readonly List<ExplosionEntity> _explosions = new();
    private readonly List<Sample> _samples = new();
    private readonly StringBuilder _csv = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private int _resIdx;
    private int _scenarioIdx;
    private ResPhase _phase = ResPhase.Warmup;
    private int _frameInPhase;
    private Resolution _res;
    private Scenario _scenario;
    private double _lastFrameMs;
    private string _framing = "(no capture)";
    private long _anchorMs;
    private long _tagCounter;
    private readonly Dictionary<long, int> _pendingTags = new();

    private enum ResPhase { Warmup, Measure, Drain, Capture }

    public VolPerfGame(Options opts, string shaderPath)
    {
        _opts = opts;
        _shaderPath = shaderPath;
        _res = opts.Resolutions[0];
        _scenario = opts.Scenarios[0];
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = _res.Width,
            PreferredBackBufferHeight = _res.Height,
            SynchronizeWithVerticalRetrace = false,
            GraphicsProfile = GraphicsProfile.HiDef,
        };
        IsFixedTimeStep = false;
        IsMouseVisible = false;
        // Same backbuffer policy as the real client (P582b): the half-res
        // excursion must be lossless, otherwise DEBUG's guard trips every frame.
        _gdm.PreparingDeviceSettings += (_, e) =>
            e.GraphicsDeviceInformation.PresentationParameters.RenderTargetUsage = RenderTargetUsage.PreserveContents;
        _csv.AppendLine("label,resolution,scenario,type,distance,frame,gpu_ms,frame_ms,volumes,level_summary");
    }

    protected override void LoadContent()
    {
        base.LoadContent();
        var bytes = File.ReadAllBytes(_shaderPath);
        _effect = new Effect(GraphicsDevice, bytes);
        _vol = new VolumetricRenderer(_effect);
        if (!_vol.IsUsable) throw new InvalidOperationException("VolumetricRenderer not usable: " + MissingFromShader());
        if (_opts.ForcedLevel is { } fl)
            _vol.ForcedLevel = Enum.Parse<VolumeLod.Level>(fl);
        if (_opts.Budget is { } b) _vol.BudgetOverride = b;
        _vol.Preload(GraphicsDevice);
        if (_opts.DepthObstruction is { } obs) BuildDepthObstruction(obs.Distance, obs.RadiusFraction);
        // The GL context only exists now, so the timer (SDL_GL_GetProcAddress)
        // must be created here, exactly like Game1.InitializeGpuBudget.
        _timer = GlFrameTimer.TryCreate();
        Console.WriteLine($"[p834] GPU: {_timer?.Renderer ?? "timer queries unavailable"} ({_timer?.DriverVersion})");
        var shaderBytes = File.ReadAllBytes(_shaderPath);
        var sha = Convert.ToHexStringLower(SHA256.HashData(shaderBytes));
        Console.WriteLine($"[p834] shader={_shaderPath} sha256={sha}");
        var fxPath = Path.Combine(Program.RepoRoot(), "src/Allegiance.ClientCore.Game/Shaders/Volumetric.fx");
        if (File.Exists(fxPath))
            Console.WriteLine($"[p834] source={fxPath} sha256={Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fxPath)))}");
        Console.WriteLine($"[p834] forced={( _opts.ForcedLevel ?? "none")} budget={(_opts.Budget?.ToString(CultureInfo.InvariantCulture) ?? "default")} fov={_opts.FovDegrees} age={_opts.AgeSeconds}");
        _anchorMs = Environment.TickCount64;
    }

    /// <summary>Which effect parameters/techniques the renderer needs but the
    /// loaded .mgfxo does not expose (P834b: makes a wrong/edge-case shader file
    /// a clear message instead of "not usable").</summary>
    private string MissingFromShader()
    {
        if (_effect is null) return "(no effect)";
        var need = new[] { "WorldView", "WorldViewProj", "InvViewportSize", "Near", "InvRange",
                           "EyePos", "LightDir", "Time", "DepthTexture", "Brightness" };
        var have = _effect.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var missing = need.Where(n => !have.Contains(n)).ToList();
        var techs = VolumeLod.TechniqueName(VolumeLod.Level.Med);
        var msg = $"missing params=[{string.Join(",", missing)}] technique {techs}=" +
                  $"{(_effect.Techniques[techs] is not null ? "ok" : "MISSING")}";
        return msg + $" | available=[{string.Join(",", have.OrderBy(n => n))}]";
    }

    protected override void Update(GameTime gameTime)
    {
        _lastFrameMs = gameTime.ElapsedGameTime.TotalMilliseconds;
        base.Update(gameTime);
    }

    /// <summary>P834 — a synthetic linear-depth texture: a disc of radius
    /// <paramref name="radiusFraction"/> × viewport height at <paramref name="distance"/>
    /// world units, "far" everywhere else. It lets the harness measure the P834
    /// visible-depth clamp (an occluder in front of a camera-inside volume) with
    /// the same encoding the real depth pass uses (LinearDepth.Encode).</summary>
    private void BuildDepthObstruction(float distance, float radiusFraction)
    {
        int w = GraphicsDevice.PresentationParameters.BackBufferWidth;
        int h = GraphicsDevice.PresentationParameters.BackBufferHeight;
        if (_depthTex is null || _depthW != w || _depthH != h)
        {
            _depthTex?.Dispose();
            _depthTex = new Texture2D(GraphicsDevice, w, h, false, SurfaceFormat.Color);
            _depthW = w; _depthH = h;
        }
        var nearDepth = LinearDepth.Encode(LinearDepth.Normalize(distance, 1f, 7900f));
        var farDepth = LinearDepth.Encode(1f);
        var px = new Color[w * h];
        float cx = w * 0.5f, cy = h * 0.5f, r = h * radiusFraction;
        for (int y = 0; y < h; y++)
        {
            float dy = y - cy;
            for (int x = 0; x < w; x++)
            {
                float dx = x - cx;
                px[y * w + x] = dx * dx + dy * dy <= r * r ? nearDepth : farDepth;
            }
        }
        _depthTex.SetData(px);
        _soft = new SoftParticleContext(_depthTex, 1f, 7900f, 0f);
        Console.WriteLine($"[p834] synthetic depth: disc r={radiusFraction:0.00}×h at {distance}u");
    }

    // Camera orientation: the look direction is world -Z yawed about +Y then
    // pitched about the camera's right axis, and the blast is placed straight
    // ahead along it. That way every orientation is supposed to produce the SAME
    // framing (view-space blast at (0,0,-distance)), which is exactly the
    // invariant P834b checks: same look and same cost at every yaw/pitch.
    private XnaVec Forward()
    {
        float yaw = MathHelper.ToRadians(_opts.YawDegrees);
        float pitch = MathHelper.ToRadians(_opts.PitchDegrees);
        return new XnaVec(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch),
                          -MathF.Cos(yaw) * MathF.Cos(pitch));
    }

    private XnaVec Up()
    {
        float yaw = MathHelper.ToRadians(_opts.YawDegrees);
        float pitch = MathHelper.ToRadians(_opts.PitchDegrees);
        return new XnaVec(MathF.Sin(yaw) * MathF.Sin(pitch), MathF.Cos(pitch),
                          MathF.Cos(yaw) * MathF.Sin(pitch));
    }

    private XnaVec BlastPosition()
    {
        var f = Forward();
        return new XnaVec(f.X * _scenario.Distance, f.Y * _scenario.Distance, f.Z * _scenario.Distance);
    }

    private (Matrix View, Matrix Proj) Cameras()
    {
        var view = Matrix.CreateLookAt(XnaVec.Zero, Forward(), Up());
        var proj = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.ToRadians(_opts.FovDegrees),
            GraphicsDevice.PresentationParameters.BackBufferWidth / (float)GraphicsDevice.PresentationParameters.BackBufferHeight,
            10f, 80000f);
        return (view, proj);
    }

    protected override void Draw(GameTime gameTime)
    {
        var gd = GraphicsDevice;
        gd.SetRenderTarget(null);
        gd.Clear(new Color(3, 4, 6));
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;

        // Keep the window updated across passes.
        var (view, proj) = Cameras();

        // Deterministic blast directly ahead of the camera at the scenario distance.
        _explosions.Clear();
        long nowLocalMs = _anchorMs + (long)(_opts.AgeSeconds * 1000.0);
        var blastPos = BlastPosition();
        _explosions.Add(new ExplosionEntity
        {
            Type = _scenario.Type,
            Position = new SimVec(blastPos.X, blastPos.Y, blastPos.Z),
            ClusterId = 0,
            Velocity = SimVec.Zero,
            ReceivedLocalMs = _anchorMs,   // elapsed = now − anchor = AgeSeconds
        });
        if (_phase == ResPhase.Capture) _framing = DescribeFraming(view, proj, blastPos);

        // GPU pass timing (asynchronous; results arrive a frame or two later).
        // Only the measure phase brackets the pass, so LOD/RT warm-up frames and
        // the PNG capture frame never contaminate the distribution.
        if (_phase == ResPhase.Measure)
        {
            long tag = ++_tagCounter;
            _pendingTags[tag] = _frameInPhase;
            _timer?.Start(tag, post: false);
            _vol!.Draw(_explosions, 0, XnaVec.Zero, view, proj, (float)(_clock.Elapsed.TotalSeconds % 1000.0),
                _soft, nowLocalMs);
            _timer?.Stop();
        }
        else
        {
            _vol!.Draw(_explosions, 0, XnaVec.Zero, view, proj, (float)(_clock.Elapsed.TotalSeconds % 1000.0),
                _soft, nowLocalMs);
        }
        _timer?.Poll((frame, _, ms) =>
        {
            if (_pendingTags.Remove(frame, out int idx)) _samples.Add(new Sample(idx, ms, _lastFrameMs));
        });

        // Advance the phase machine (one pass per scenario × resolution).
        AdvancePass(gd, view, proj);

        base.Draw(gameTime);
    }

    private void AdvancePass(GraphicsDevice gd, Matrix view, Matrix proj)
    {
        _frameInPhase++;
        switch (_phase)
        {
            case ResPhase.Warmup:
                if (_frameInPhase >= _opts.WarmupFrames)
                {
                    _samples.Clear();
                    _phase = ResPhase.Measure;
                    _frameInPhase = 0;
                }
                break;
            case ResPhase.Measure:
                if (_frameInPhase >= _opts.MeasureFrames)
                {
                    _phase = ResPhase.Capture;
                    _frameInPhase = 0;
                }
                break;
            case ResPhase.Capture:
                Capture(gd);
                _phase = ResPhase.Drain;
                _frameInPhase = 0;
                break;
            case ResPhase.Drain:
                if (_frameInPhase >= 12)
                {
                    EmitPass();
                    NextPass();
                }
                break;
        }
    }

    private void Capture(GraphicsDevice gd)
    {
        if (_opts.PngDir is null) return;
        int w = gd.PresentationParameters.BackBufferWidth;
        int h = gd.PresentationParameters.BackBufferHeight;
        var px = new Color[w * h];
        try
        {
            gd.GetBackBufferData(px);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[p834] capture failed: {ex.Message}");
            return;
        }
        var stats = LumaStats(px, w, h);
        _framing += $" | capture luma={stats.Mean:0.00} lit={stats.Lit * 100:0.00}% hot={stats.Hot * 100:0.00}%" +
                    $" bbox=[{stats.MinX},{stats.MinY}..{stats.MaxX},{stats.MaxY}]";
        using var tex = new Texture2D(gd, w, h, false, SurfaceFormat.Color);
        tex.SetData(px);
        var file = Path.Combine(_opts.PngDir, $"{San(_opts.Label)}_{_res}_{_scenario.Label}.png");
        using (var fs = File.Create(file)) tex.SaveAsPng(fs, w, h);
        Console.WriteLine($"[p834] png {file}");
    }

    /// <summary>P834b — what the captured frame actually contains: mean luma, the
    /// fraction of pixels above the (3,4,6) clear colour ("lit") and above mid-grey
    /// ("hot"), and the bounding box of the lit pixels. A black frame is then a
    /// number in the table, not something you have to eyeball.</summary>
    private readonly record struct FrameStats(
        double Mean, double Lit, double Hot, int MinX, int MinY, int MaxX, int MaxY);

    private static FrameStats LumaStats(Color[] px, int w, int h)
    {
        double sum = 0; long lit = 0, hot = 0;
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = px[y * w + x];
                double l = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                sum += l;
                if (l <= 10.0) continue;
                lit++;
                if (l > 64.0) hot++;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }
        double n = (double)w * h;
        return new FrameStats(sum / n, lit / n, hot / n,
            maxX >= 0 ? minX : -1, maxX >= 0 ? minY : -1, maxX, maxY);
    }

    /// <summary>P834b — the CPU-side framing that every orientation must reproduce:
    /// the blast's VIEW-SPACE position (it must sit on the -Z axis, i.e. dead
    /// centre) and its projected diameter as a fraction of the viewport height.
    /// If these move with --yaw/--pitch, the harness camera is wrong; if they hold
    /// and the picture does not, the shader/renderer depends on orientation.</summary>
    private string DescribeFraming(Matrix view, Matrix proj, XnaVec blastPos)
    {
        var vs = XnaVec.Transform(blastPos, view);
        var clip = XnaVec.Transform(blastPos, view * proj);
        float h = GraphicsDevice.PresentationParameters.BackBufferHeight;
        float w = GraphicsDevice.PresentationParameters.BackBufferWidth;
        var preset = ExplosionPresets.For(_scenario.Type);
        float dist = MathF.Max(1e-4f, -vs.Z);
        float diamPx = 2f * preset.RadiusExplosion * h * proj.M22 / dist;
        return $"camera yaw={_opts.YawDegrees} pitch={_opts.PitchDegrees} fwd={Forward()} " +
               $"| blast view=(x {vs.X:0.0}, y {vs.Y:0.0}, z {vs.Z:0.0}) " +
               $"ndc=({clip.X:0.000}, {clip.Y:0.000}) " +
               $"screen=({(clip.X * 0.5f + 0.5f) * w:0},{(-clip.Y * 0.5f + 0.5f) * h:0}) " +
               $"diam={diamPx:0}px ({diamPx / h * 100f:0}% of h) radius={preset.RadiusExplosion:0}u";
    }

    private int DiagInt(string name)
        => _vol?.GetType().GetProperty(name)?.GetValue(_vol) is int v ? v : -1;

    private float DiagFloat(string name)
        => _vol?.GetType().GetProperty(name)?.GetValue(_vol) is float v ? v : -1f;

    private static string San(string? s) => string.IsNullOrEmpty(s) ? "run" : string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '-'));

    private void EmitPass()
    {
        var gpu = _samples.Where(s => s.GpuMs > 0).Select(s => s.GpuMs).OrderBy(v => v).ToArray();
        var fr = _samples.Select(s => s.FrameMs).Where(v => v > 0).OrderBy(v => v).ToArray();
        if (gpu.Length == 0)
        {
            Console.WriteLine($"[p834] {_res} {_scenario.Label}: no GPU samples");
            return;
        }
        double P(double[] a, double q) => a[Math.Min(a.Length - 1, Math.Max(0, (int)(q * (a.Length - 1))))];
        double gpuMean = gpu.Average(), gpuP50 = P(gpu, 0.50), gpuP95 = P(gpu, 0.95);
        double frP50 = fr.Length > 0 ? P(fr, 0.50) : 0, frP99 = fr.Length > 0 ? P(fr, 0.99) : 0;
        // Diagnostics come from properties that only the P834 renderer has, so
        // read them reflectively: the SAME harness binary can then measure both
        // the pre-fix (master) and post-fix renderer, which is what makes the
        // before/after curve honest.
        int volumes = DiagInt("LastVolumeCount"), fullRes = DiagInt("LastFullResVolumes"),
            halfRes = DiagInt("LastHalfResVolumes"), quarterRes = DiagInt("LastQuarterResVolumes");
        float pixelSteps = DiagFloat("LastPixelSteps"), budgetUsed = DiagFloat("LastBudget");
        Console.WriteLine($"[p834] viewport={GraphicsDevice.Viewport.Width}x{GraphicsDevice.Viewport.Height} pres={GraphicsDevice.PresentationParameters.BackBufferWidth}x{GraphicsDevice.PresentationParameters.BackBufferHeight} samples=[{string.Join(",", gpu.Take(6).Select(v => v.ToString("0.00")))}]");
        Console.WriteLine($"[p834] {_res,-10} {_scenario.Label,-8} gpu mean {gpuMean,7:0.00} p50 {gpuP50,7:0.00} p95 {gpuP95,7:0.00} ms | frame p50 {frP50,7:0.00} p99 {frP99,7:0.00} ms ({1000.0 / Math.Max(gpuMean, 1e-4),6:0.0} fps equiv) n={gpu.Length} | vols={volumes} full={fullRes} half={halfRes} quarter={quarterRes} steps={pixelSteps / 1e6:0.0}M budget={budgetUsed / 1e6:0.0}M");
        Console.WriteLine($"[p834-frame] {_res} {_scenario.Label} | {_framing}");
        Console.WriteLine($"[p834-sum] label={San(_opts.Label)} shader={_shaderPath} yaw={_opts.YawDegrees} pitch={_opts.PitchDegrees} res={_res} scenario={_scenario.Label} gpu_mean_ms={gpuMean:0.00} gpu_p50_ms={gpuP50:0.00}");
        if (_opts.CsvPath is { } csvPath)
        {
            foreach (var s in _samples)
                _csv.AppendLine($"{_opts.Label},{_res},{_scenario.Label},{_scenario.Type},{_scenario.Distance.ToString(CultureInfo.InvariantCulture)},{s.Frame},{s.GpuMs.ToString("0.0000", CultureInfo.InvariantCulture)},{s.FrameMs.ToString("0.0000", CultureInfo.InvariantCulture)},{volumes},{pixelSteps.ToString("0", CultureInfo.InvariantCulture)}");
        }
    }

    private void NextPass()
    {
        _scenarioIdx++;
        if (_scenarioIdx >= _opts.Scenarios.Count)
        {
            _scenarioIdx = 0;
            _resIdx++;
            if (_resIdx >= _opts.Resolutions.Count) { FinishAndExit(); return; }
            _res = _opts.Resolutions[_resIdx];
            _gdm.PreferredBackBufferWidth = _res.Width;
            _gdm.PreferredBackBufferHeight = _res.Height;
            _gdm.ApplyChanges();
            if (_opts.DepthObstruction is { } obs2) BuildDepthObstruction(obs2.Distance, obs2.RadiusFraction);
            Console.WriteLine($"[p834] ---- resolution {_res} ----");
        }
        _scenario = _opts.Scenarios[_scenarioIdx];
        _phase = ResPhase.Warmup;
        _frameInPhase = 0;
        _samples.Clear();
        _pendingTags.Clear();
    }

    private void FinishAndExit()
    {
        if (_opts.CsvPath is { } p) File.WriteAllText(p, _csv.ToString());
        ExitCode = 0;
        Exit();
    }
}
