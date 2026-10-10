using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaCol = Microsoft.Xna.Framework.Color;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using XnaMat = Microsoft.Xna.Framework.Matrix;

// P592 — offline REAL-WINDOW proof of the GIANT sky object: the face-on Milky
// Way drawn as ONE curved patch of the sky sphere (~90° across) over TERRA.
//
// Drives the ACTUAL BlackSkyRenderer (which reads giant-objects.json from the
// artwork root and draws the giant with GiantObjectRenderer) at 2560x1440.
// Each scene renders the real sky into an offscreen render target (the exact
// same draw calls the game makes into its backbuffer) and that target is
// blitted to the real window's backbuffer AND saved as PNG. Capturing from the
// per-scene target makes every frame independent — the one-scene-per-present
// capture tool otherwise accumulates bright content across frames (p589-proof
// shows the same artifact on its sunstrip; the giant's size amplifies it), so
// an offscreen-per-scene capture is the reliable way to get truthful frames
// while still presenting to the real window (the P547/P562 class of
// render-target regressions are unaffected: the sky draws to the same
// color/depth target with the same projection). Scenes:
//   * six directional views around the object (incl. looking at the patch edge)
//   * a slow PAN strip across the whole object (frame strip)
//   * a 300% crop of the feathered border (proof the patch edge never shows)
//   * a readability frame with ship silhouettes in front of the giant
//   * the --giant-objects off frame (the giant is gone)
//   * a frame time pass for BlackSkyRenderer.Draw
// Andromeda/Alderan was dropped (report) so no Alderan giant is rendered — one
// frame confirms Alderan's sky has no giant.

var o = ProofOptions.Parse(args);
using var runner = new ProofRunner(o);
runner.Run();
return 0;

sealed class ProofOptions
{
    public string ArtRoot = "";
    public string OutDir = "";

    public static ProofOptions Parse(string[] args)
    {
        string art = "", outDir = "";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : (env is { Length: > 0 } ? env : ExternalPaths.Resolve(ExternalResourceKind.Artwork)),
            OutDir = outDir.Length > 0 ? outDir : "p592-proof-out",
        };
    }
}

sealed class ProofRunner : Game
{
    private const int W = 2560, H = 1440;
    private const int PanFrames = 12;
    private const int SixDirs = 6;
    private static readonly float FovRad = MathHelper.ToRadians(60f);

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private readonly IAssetSource _assets;
    private readonly Queue<Action<GraphicsDevice>> _scenes = new();
    private RenderTarget2D? _rt;

    // Terra (cluster 1057): light + planet clearly separate from the giant's
    // fixed direction (verified: ~104° from light, ~24° from planet, both >22°).
    private static readonly XnaVec TerraLight = new(-0.644f, 0.758f, 0.502f);
    private const byte TerraLat = 071, TerraLon = 037;

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _assets = new DirectoryAssetSource(o.ArtRoot);
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = W;
        _gdm.PreferredBackBufferHeight = H;
        IsMouseVisible = false;
        Directory.CreateDirectory(o.OutDir);
        BuildScenes();
    }

    protected override void LoadContent()
    {
        base.LoadContent();
        _rt = new RenderTarget2D(GraphicsDevice, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
    }

    private DataClusterIGC TerraCluster()
        => new(
            StarSeed: 704, LightDirection: new[] { TerraLight.X, TerraLight.Y, TerraLight.Z },
            LightColor: 0x00FFAA88, ScreenX: 0, ScreenY: 0, ClusterId: 1057,
            Debris: 0, Stars: 960, Name: "Terra", PosterName: "globe7",
            PlanetName: "nebplnt12bmp", PlanetSinLatitude: TerraLat, PlanetLongitude: TerraLon,
            PlanetRadius: 097, Active: false, HomeSector: false);

    private static XnaVec GiantDir()
    {
        var cfg = ReadConfig();
        if (cfg.ByCluster.TryGetValue(1057, out var s))
        {
            var d = Allegiance.Core.Rendering.GiantObjectConfig.ResolveDirection(s,
                new NativeSkyMath.Vector3Unit(TerraLight.X, TerraLight.Y, TerraLight.Z),
                NativeSkyMath.PlanetDirection(TerraLat, TerraLon));
            return new XnaVec(d.X, d.Y, d.Z);
        }
        return XnaVec.Normalize(new XnaVec(0.6637f, 0.5436f, -0.5183f));
    }

    private static Allegiance.Core.Rendering.GiantObjectConfig ReadConfig()
    {
        string json = System.IO.File.ReadAllText(System.IO.Path.Combine(ExternalPaths.Resolve(ExternalResourceKind.Artwork), "giant-objects.json"));
        return Allegiance.Core.Rendering.GiantObjectConfig.Parse(json);
    }

    private void BuildScenes()
    {
        var gdir = GiantDir();

        // Warm-up: render the sky to the RT a few times (untimed) so shaders
        // compile / the 4096 giant texture uploads once, and the reported
        // frametime reflects steady state, not the cold first frames.
        for (int w = 0; w < 3; w++)
        {
            _scenes.Enqueue(gd => Capture(gd, black => { black.SetCluster(TerraCluster()); black.Draw(LookAtDir(gdir), Proj(), XnaVec.Zero); }, null, track: false));
        }

        // 1) Six directional views: centre + five surrounding directions, incl.
        //    looking toward the patch edge (no seam / edge / stretch / banding).
        var offsets = new[]
        {
            XnaVec.Zero,                                  // centre (face-on)
            new XnaVec(0.701f, 0.491f, 0.517f),          // upper-right
            new XnaVec(-0.734f, 0.487f, 0.510f),         // upper-left
            new XnaVec(0.703f, -0.506f, 0.501f),         // lower-right
            new XnaVec(-0.738f, -0.497f, 0.483f),        // lower-left
            Perpendicular(gdir) * 0.961f,                // toward the patch EDGE
        };
        for (int i = 0; i < SixDirs; i++)
        {
            var cam = XnaVec.Normalize(gdir + offsets[i]);
            int idx = i;
            _scenes.Enqueue(gd => Capture(gd, black => { black.SetCluster(TerraCluster()); black.Draw(LookAtDir(cam), Proj(), XnaVec.Zero); }, $"terra_giant_dir{idx}.png"));
        }

        // 2) Slow PAN strip across the whole object (12 frames).
        var right = Perpendicular(gdir);
        for (int s = 0; s < PanFrames; s++)
        {
            int idx = s;
            _scenes.Enqueue(gd => Capture(gd, black =>
            {
                black.SetCluster(TerraCluster());
                float t = idx / (float)(PanFrames - 1);
                float a = -58f + 116f * t;
                var cam = XnaVec.Normalize(gdir * MathF.Cos(MathHelper.ToRadians(a))
                                         + right * MathF.Sin(MathHelper.ToRadians(a)));
                black.Draw(LookAtDir(cam), Proj(), XnaVec.Zero);
            }, $"terra_giant_pan_{idx:00}.png"));
        }

        // 3) 300% crop of the feathered border region.
        var edgeCam = XnaVec.Normalize(gdir + right * 0.895f);
        _scenes.Enqueue(gd => Capture(gd, black =>
        {
            black.SetCluster(TerraCluster());
            black.Draw(LookAtDir(edgeCam), Proj(), XnaVec.Zero);
        }, "terra_giant_border.png", cropZoom: 3f));

        // 4) Readability: dark ship-silhouette wedges in front of the giant.
        _scenes.Enqueue(gd => Capture(gd, black =>
        {
            black.SetCluster(TerraCluster());
            black.Draw(LookAtDir(gdir), Proj(), XnaVec.Zero);
            DrawShipSilhouettes(gd);
        }, "terra_giant_readability.png"));

        // 5) Toggle: --giant-objects off — the giant is gone from Terra's sky.
        _scenes.Enqueue(gd => Capture(gd, black =>
        {
            using var noGiant = new BlackSkyRenderer(gd, _assets, giantObjectsOn: false);
            noGiant.SetCluster(TerraCluster());
            noGiant.Draw(LookAtDir(gdir), Proj(), XnaVec.Zero);
        }, "terra_giant_off.png", track: false));

        // 6) Alderan (1052): the giant was DROPPED — confirm its sky has none.
        _scenes.Enqueue(gd => Capture(gd, black =>
        {
            black.SetCluster(AlderanCluster());
            black.Draw(LookAtDir(new XnaVec(-0.654f, -0.531f, 0.639f)), Proj(), XnaVec.Zero);
        }, "alderan_no_giant.png"));

        // 7) Frametime on the BACKBUFFER (the game's real path, not the RT
        // capture which pays software-GL render-target cost): warm, then time a
        // burst of BlackSkyRenderer.Draw calls and report steady-state avg/max.
        _scenes.Enqueue(gd => TimeBackbufferDraws(gd));

        Console.WriteLine($"[p592-proof] {_scenes.Count} scenes queued -> {_o.OutDir}");
    }

    /// <summary>Measure the sky draw on the backbuffer (real game path): warm
    /// several frames, then time a burst and report steady-state avg/max ms.</summary>
    private void TimeBackbufferDraws(GraphicsDevice gd)
    {
        var view = LookAtDir(GiantDir());
        gd.SetRenderTarget(null);
        // Warm the GPU/device on the backbuffer path.
        using (var warm = new BlackSkyRenderer(gd, _assets))
        {
            warm.SetCluster(TerraCluster());
            for (int i = 0; i < 5; i++)
            {
                gd.Clear(XnaCol.Black);
                warm.Draw(view, Proj(), XnaVec.Zero);
            }
        }
        // Timed burst.
        using (var black = new BlackSkyRenderer(gd, _assets))
        {
            black.SetCluster(TerraCluster());
            const int N = 30;
            long total = 0, max = 0;
            for (int i = 0; i < N; i++)
            {
                gd.Clear(XnaCol.Black);
                var st = Stopwatch.GetTimestamp();
                black.Draw(view, Proj(), XnaVec.Zero);
                var t = Stopwatch.GetTimestamp() - st;
                total += t;
                if (t > max) max = t;
            }
            double avgMs = total * 1000.0 / Stopwatch.Frequency / N;
            double maxMs = max * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"[p592-proof] backbuffer BlackSkyRenderer.Draw frametime: avg={avgMs:F3} ms  max={maxMs:F3} ms  samples={N}");
        }
        Exit();
    }

    private DataClusterIGC AlderanCluster()
        => new(
            StarSeed: 902, LightDirection: new[] { 0.775f, 0.083f, 0.769f },
            LightColor: 0x00FFAA88, ScreenX: 0, ScreenY: 0, ClusterId: 1052,
            Debris: 0, Stars: 970, Name: "Alderan", PosterName: "globe3",
            PlanetName: "nebplnt06bmp", PlanetSinLatitude: 046, PlanetLongitude: 059,
            PlanetRadius: 084, Active: false, HomeSector: false);

    private static XnaVec Perpendicular(XnaVec v)
    {
        var up = MathF.Abs(v.Y) > 0.977f ? XnaVec.UnitZ : XnaVec.UnitY;
        var r = XnaVec.Normalize(XnaVec.Cross(up, v));
        return XnaVec.Normalize(XnaVec.Cross(v, r));
    }

    private XnaMat Proj() => XnaMat.CreatePerspectiveFieldOfView(FovRad, W / (float)H, 1f, 95000f);

    private static XnaMat LookAtDir(XnaVec fwd)
    {
        if (fwd.LengthSquared() < 1e-6f) fwd = XnaVec.UnitZ;
        fwd.Normalize();
        var up = MathF.Abs(fwd.Y) > 0.996f ? XnaVec.UnitZ : XnaVec.UnitY;
        return XnaMat.CreateLookAt(XnaVec.Zero, fwd, up);
    }

    /// <summary>Render one scene into the offscreen target (clean, independent),
    /// blit it to the real backbuffer so the window shows the true frame, then
    /// save the target (plus an optional 300% border crop).</summary>
    private void Capture(GraphicsDevice gd, Action<BlackSkyRenderer> draw, string? name, float? cropZoom = null, bool track = true)
    {
        if (_rt is null) { Console.WriteLine($"[p592-proof] no RT, skipping {name}"); return; }
        using var black = new BlackSkyRenderer(gd, _assets);
        gd.SetRenderTarget(_rt);
        gd.Clear(XnaCol.Black);
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        gd.BlendState = BlendState.Opaque;
        draw(black);
        gd.SetRenderTarget(null);
        BlitToBackbuffer(gd);
        if (name is null) return;   // warm-up: don't save
        if (cropZoom is { } z) SaveZoomCropFromRt(gd, name, z);
        SaveFromRt(gd, name);
    }

    /// <summary>Present the captured target to the real window's backbuffer so
    /// the presented frame matches the saved frame.</summary>
    private void BlitToBackbuffer(GraphicsDevice gd)
    {
        if (_rt is null) return;
        gd.Clear(XnaCol.Black);
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;
        using var eff = new BasicEffect(gd)
        {
            TextureEnabled = true,
            Texture = _rt,
            VertexColorEnabled = false,
            LightingEnabled = false,
            View = XnaMat.Identity,
            Projection = XnaMat.CreateOrthographicOffCenter(0, W, H, 0, 0, 1),
            World = XnaMat.Identity,
        };
        var corners = new[]
        {
            new VertexPositionTexture(new Vector3(0, H, 0), new Vector2(0, 1)),
            new VertexPositionTexture(new Vector3(W, H, 0), new Vector2(1, 1)),
            new VertexPositionTexture(new Vector3(0, 0, 0), new Vector2(0, 0)),
            new VertexPositionTexture(new Vector3(W, 0, 0), new Vector2(1, 0)),
        };
        var idx = new short[] { 0, 1, 2, 2, 1, 3 };
        foreach (var p in eff.CurrentTechnique.Passes)
        {
            p.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, corners, 0, 4, idx, 0, 2);
        }
    }

    private void DrawShipSilhouettes(GraphicsDevice gd)
    {
        var shape = new[]
        {
            new VertexPositionColor(new Vector3(W / 2f - 90f, H / 2f + 60f, 0), new XnaCol(6, 7, 9)),
            new VertexPositionColor(new Vector3(W / 2f + 95f, H / 2f + 72f, 0), new XnaCol(6, 7, 9)),
            new VertexPositionColor(new Vector3(W / 2f + 12f, H / 2f - 88f, 0), new XnaCol(6, 7, 9)),
            new VertexPositionColor(new Vector3(W / 2f - 250f, H / 2f - 150f, 0), new XnaCol(9, 10, 13)),
            new VertexPositionColor(new Vector3(W / 2f - 60f, H / 2f - 160f, 0), new XnaCol(9, 10, 13)),
            new VertexPositionColor(new Vector3(W / 2f - 250f, H / 2f + 90f, 0), new XnaCol(9, 10, 13)),
        };
        var idx = new short[] { 0, 1, 2, 3, 4, 5 };
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;
        using var eff = new BasicEffect(gd)
        {
            VertexColorEnabled = true,
            LightingEnabled = false,
            View = XnaMat.Identity,
            Projection = XnaMat.CreateOrthographicOffCenter(0, W, H, 0, 0, 1),
            World = XnaMat.Identity,
        };
        foreach (var pass in eff.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, shape, 0, shape.Length, idx, 0, 2);
        }
    }

    private void SaveZoomCropFromRt(GraphicsDevice gd, string path, float zoom)
    {
        if (_rt is null) return;
        var full = new XnaCol[W * H];
        _rt.GetData(full);
        int cw = (int)(W / zoom), chh = (int)(H / zoom);
        int sx = (W - cw) / 2, sy = (H - chh) / 2;
        var crop = new XnaCol[cw * chh];
        for (int y = 0; y < chh; y++)
            Array.Copy(full, (sy + y) * W + sx, crop, y * cw, cw);
        using var tmp = new Texture2D(gd, cw, chh, false, SurfaceFormat.Color);
        tmp.SetData(crop);
        using var dst = new Texture2D(gd, cw * 3, chh * 3, false, SurfaceFormat.Color);
        var srcData = new XnaCol[cw * chh];
        tmp.GetData(srcData);
        var outData = new XnaCol[cw * chh * 9];
        for (int y = 0; y < chh; y++)
            for (int x = 0; x < cw; x++)
                for (int dy = 0; dy < 3; dy++)
                    for (int dx = 0; dx < 3; dx++)
                        outData[(y * 3 + dy) * (cw * 3) + (x * 3 + dx)] = srcData[y * cw + x];
        dst.SetData(outData);
        var cropPath = path.Replace(".png", "_crop300.png", StringComparison.OrdinalIgnoreCase);
        using var fs = System.IO.File.Create(System.IO.Path.Combine(_o.OutDir, cropPath));
        dst.SaveAsPng(fs, cw * 3, chh * 3);
        Console.WriteLine($"[p592-proof] wrote {cropPath}");
    }

    private void SaveFromRt(GraphicsDevice gd, string path)
    {
        if (_rt is null) return;
        var full = new XnaCol[W * H];
        _rt.GetData(full);
        using var tmp = new Texture2D(gd, W, H, false, SurfaceFormat.Color);
        tmp.SetData(full);
        using var fs = System.IO.File.Create(Path.Combine(_o.OutDir, path));
        tmp.SaveAsPng(fs, W, H);
        Console.WriteLine($"[p592-proof] wrote {path}");
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);
        if (_scenes.TryDequeue(out var scene))
        {
            try { scene(GraphicsDevice); }
            catch (Exception ex) { Console.WriteLine($"[p592-proof] scene FATAL: {ex}"); }
        }
    }
}
