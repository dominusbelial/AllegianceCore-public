using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaCol = Microsoft.Xna.Framework.Color;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using XnaMat = Microsoft.Xna.Framework.Matrix;

// P589 — offline REAL-WINDOW proof of the black-sky space objects.
//
// Drives the ACTUAL BlackSkyRenderer (which now draws the pre-P576 point stars,
// the native planet, the native SUN + LENS FLARE, the moons, the distant
// galaxies/nebulae cut-outs, and the deep-field galaxy + Little-Red-Dot field)
// into a real 2560x1440 window's backbuffer and SaveAsPng's each PRESENTED
// frame (real window, not --capture/offscreen — the backbuffer path that caught
// the P547/P562 regressions). Scenes, per target sector:
//   * "sun"       — camera aimed at the sector's light direction: the native
//                   sun aura + the full lens-flare element string.
//   * "sunstrip"  — a frame strip turning ACROSS the sun (camera yaws from one
//                   side of the light direction to the other), proving the
//                   flare elements slide correctly as you turn.
//   * "pm"        — camera aimed at the planet: the native planet + its moons.
//   * "objects"   — a clear view of the distant-object cut-outs + the deep
//                   field (galaxies + little red dots) on black.
//   * "read"      — the same scene with a dark ship-silhouette wedge over it
//                   (readability: ship silhouette vs the object behind).
//   * "galaxies_off" — the same cluster with --galaxies off: the deep field is
//                   stripped (proves the toggle visually).
//   * "moons_off"    — the same cluster with --moons off.
// A frame-time pass measures BlackSkyRenderer.Draw (avg/max ms over every
// frame) and reports it.

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
            ArtRoot = art.Length > 0 ? art : (env is { Length: > 0 } ? env : "/home/domingo/Development/Artwork"),
            OutDir = outDir.Length > 0 ? outDir : "p589-proof-out",
        };
    }
}

sealed class ProofRunner : Game
{
    private const int W = 2560, H = 1440;
    private const int StripFrames = 12;
    private static readonly float FovRad = MathHelper.ToRadians(60f);

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private readonly IAssetSource _assets;
    private readonly Queue<Action<GraphicsDevice>> _scenes = new();

    // Per-target cluster: star/light/planet. LightDirection is the sector's
    // toward-the-sun direction (used for the flare + sun).
    private static readonly (string Name, int Env, string Planet, byte Lat, byte Lon, byte Rad, float Lx, float Ly, float Lz)[] Targets =
    {
        ("Alderan", 3, "nebplnt06bmp", 036, 029, 089, 0.854f, 0.096f, 0.792f),
        ("Ganymede", 4, "nebplnt33bmp", 048, 074, 073, -0.366f, 0.806f, 0.915f),
        ("Rigel", 5, "nebplnt06bmp", 043, 066, 093, 0.824f, -0.091f, 0.782f),
    };

    private long _drawTotalTicks, _drawMaxTicks;
    private int _drawSamples;

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

    private DataClusterIGC ClusterFor(string name, int env, string poster, string planet,
        byte lat, byte lon, byte rad, float lx, float ly, float lz)
        => new(
            StarSeed: (uint)(601 + env), LightDirection: new[] { lx, ly, lz },
            LightColor: 0x00FFAA88, ScreenX: 0, ScreenY: 0, ClusterId: (short)(100 + env),
            Debris: 0, Stars: 830, Name: name, PosterName: poster,
            PlanetName: planet, PlanetSinLatitude: lat, PlanetLongitude: lon,
            PlanetRadius: rad, Active: false, HomeSector: false);

    private void BuildScenes()
    {
        // Warm-up: the very first render tick (backbuffer settling) can drop
        // geometry; burn it on a discarded frame so every real proof frame is
        // clean and captures the full sky (the production client renders the
        // sky every frame, so this is proof-only).
        _scenes.Enqueue(gd =>
        {
            gd.Clear(XnaCol.Black);
            using var warm = new BlackSkyRenderer(gd, _assets);
            warm.SetCluster(ClusterFor("Warmup", 3, "globe3", "nebplnt06bmp", 036, 029, 089, 0.854f, 0.096f, 0.792f));
            warm.Draw(XnaMat.CreateLookAt(XnaVec.Zero, -XnaVec.UnitZ, XnaVec.UnitY), Proj(), XnaVec.Zero);
        });

        foreach (var (name, env, planet, lat, lon, rad, lx, ly, lz) in Targets)
        {
            var cluster = ClusterFor(name, env, $"globe{env}", planet, lat, lon, rad, lx, ly, lz);
            var light = new XnaVec(lx, ly, lz);
            light.Normalize();

            // 1) Sun + lens flare (camera at the light direction).
            _scenes.Enqueue(gd =>
            {
                gd.Clear(XnaCol.Black);
                using var black = new BlackSkyRenderer(gd, _assets);
                black.SetCluster(cluster);
                SkyDraw(black, LookAtDir(light), Proj());
                SavePng(gd, $"{name}_sun.png");
            });

            // 2) Frame strip turning across the sun.
            for (int s = 0; s < StripFrames; s++)
            {
                int idx = s;
                _scenes.Enqueue(gd =>
                {
                    using var black = new BlackSkyRenderer(gd, _assets);
                    black.SetCluster(cluster);
                    float t = idx / (float)(StripFrames - 1);
                    var fwd = SweepAcross(light, -FovRad * 0.9f, FovRad * 0.9f, t);
                    SkyDraw(black, LookAtDir(fwd), Proj());
                    SavePng(gd, $"{name}_sunstrip_{idx:00}.png");
                });
            }

            // 3) Planet + moons (camera at the planet direction).
            _scenes.Enqueue(gd =>
            {
                gd.Clear(XnaCol.Black);
                using var black = new BlackSkyRenderer(gd, _assets);
                black.SetCluster(cluster);
                var pd = NativeSkyMath.PlanetDirection(lat, lon);
                var fwd = new XnaVec(pd.X, pd.Y, pd.Z);
                if (fwd.LengthSquared() > 1e-6f) fwd.Normalize(); else fwd = XnaVec.UnitZ;
                SkyDraw(black, LookAtDir(fwd), Proj());
                SavePng(gd, $"{name}_planet_moons.png");
            });

            // 4) Distant objects + deep-field galaxies/LRDs (a diagonal view).
            _scenes.Enqueue(gd =>
            {
                gd.Clear(XnaCol.Black);
                using var black = new BlackSkyRenderer(gd, _assets);
                black.SetCluster(cluster);
                var fwd = XnaVec.Normalize(light + new XnaVec(0.4f, 0.6f, -0.3f));
                SkyDraw(black, LookAtDir(fwd), Proj());
                SavePng(gd, $"{name}_objects_galaxies.png");
            });

            // 5) Readability: planet-centred with a dark ship-silhouette wedge.
            _scenes.Enqueue(gd =>
            {
                gd.Clear(XnaCol.Black);
                using var black = new BlackSkyRenderer(gd, _assets);
                black.SetCluster(cluster);
                var pd = NativeSkyMath.PlanetDirection(lat, lon);
                var fwd = new XnaVec(pd.X, pd.Y, pd.Z);
                if (fwd.LengthSquared() > 1e-6f) fwd.Normalize(); else fwd = XnaVec.UnitZ;
                SkyDraw(black, LookAtDir(fwd), Proj());
                DrawSilhouette(gd);
                SavePng(gd, $"{name}_readability.png");
            });

            // 6) Toggles: --galaxies off, --moons off (visually strip the field).
            _scenes.Enqueue(gd =>
            {
                using var black = new BlackSkyRenderer(gd, _assets, galaxiesOn: false);
                black.SetCluster(cluster);
                SkyDraw(black, LookAtDir(light), Proj());
                SavePng(gd, $"{name}_galaxies_off.png");
            });
            _scenes.Enqueue(gd =>
            {
                using var black = new BlackSkyRenderer(gd, _assets, moonsOn: false);
                black.SetCluster(cluster);
                SkyDraw(black, LookAtDir(light), Proj());
                SavePng(gd, $"{name}_moons_off.png");
            });
        }

        // 7) P589c — a real-window frame + 300% surroundings crop per KEPT
        //    hero object (none dropped: all reached 0 outside-envelope stars).
        foreach (var slug in KeptSlugs)
            AddObjectScenes(slug);

        Console.WriteLine($"[p589-proof] {_scenes.Count} scenes queued -> {_o.OutDir}");
    }

    /// <summary>The hero-object sprites kept after P589c star cleanup. All seven
    /// reached 0 outside-envelope field stars, so none was dropped. Each gets a
    /// real-window frame with the sprite drawn as the in-game poster (premul-
    /// tiplied, on the black starfield) plus a 300% crop of its surroundings.</summary>
    private static readonly string[] KeptSlugs =
    [
        "helix-nebula", "whirlpool-m51", "ring-nebula-m57", "sombrero-m104",
        "pinwheel-m101", "lion-nebula-miri", "lion-nebula-nasa",
    ];

    /// <summary>Render one kept hero object as the in-game poster (premultiplied
    /// billboard on the black starfield), capture the full backbuffer frame and a
    /// 300% crop of its surroundings — the P589c real-window proof that no field
    /// star floats around the object.</summary>
    private void AddObjectScenes(string slug)
    {
        _scenes.Enqueue(gd =>
        {
            gd.Clear(XnaCol.Black);
            var cluster = ClusterFor("Obj", 5, "globe5", "nebplnt06bmp", 044, 047, 079, 0.724f, 0.311f, 0.812f);
            using var black = new BlackSkyRenderer(gd, _assets, moonsOn: false, distantObjectsOn: false, galaxiesOn: false);
            black.SetCluster(cluster);
            var view = LookAtDir(XnaVec.UnitZ);
            SkyDraw(black, view, Proj());

            // Draw the specific object, premultiplied, centered and fairly large
            // (half-size ~0.16 of the sky shell -> the object reads clearly with
            // its surrounding sky visible), exactly like DistantObjectRenderer.
            using var tex = LoadPremultipliedPng(System.IO.Path.Combine(_o.ArtRoot, "p589obj", slug + ".png"));
            if (tex is not null)
            {
                float halfWorld = (float)Math.Tan(MathHelper.ToRadians(5f)) * ObjectDist;
                DrawBillboard(gd, view, Proj(), XnaVec.UnitZ, halfWorld, tex, XnaCol.White);
            }
            SavePng(gd, $"{slug}_realwindow.png");
            SaveZoomCrop(gd, $"{slug}_crop300.png", 3f);
        });

        // A second scene for the same object on PURE BLACK with NO ambient
        // starfield/planet — isolates the sprite itself so the reviewer can see
        // the object's surrounding sky is clean (the dots in the real-window
        // frame are the game's intentional ambient pre-P576 starfield, not
        // photographic field stars baked into the object).
        _scenes.Enqueue(gd =>
        {
            gd.Clear(XnaCol.Black);
            var view = LookAtDir(XnaVec.UnitZ);
            using var tex = LoadPremultipliedPng(System.IO.Path.Combine(_o.ArtRoot, "p589obj", slug + ".png"));
            if (tex is not null)
            {
                float halfWorld = (float)Math.Tan(MathHelper.ToRadians(5f)) * ObjectDist;
                DrawBillboard(gd, view, Proj(), XnaVec.UnitZ, halfWorld, tex, XnaCol.White);
            }
            SavePng(gd, $"{slug}_sprite.png");
            SaveZoomCrop(gd, $"{slug}_sprite_crop300.png", 3f);
        });
    }

    private const float ObjectDist = 8500f;

    /// <summary>Load a straight-alpha PNG and premultiply it (mirrors
    /// DistantObjectRenderer.LoadPremultipliedPng) so the feathered alpha blends
    /// without dark fringes under the real BlendState.AlphaBlend.</summary>
    private Texture2D LoadPremultipliedPng(string path)
    {
        if (!System.IO.File.Exists(path)) return null!;
        var tex = Texture2D.FromStream(GraphicsDevice, System.IO.File.OpenRead(path));
        var data = new XnaCol[tex.Width * tex.Height];
        tex.GetData(data);
        for (int i = 0; i < data.Length; i++)
        {
            float a = data[i].A / 255f;
            data[i] = new XnaCol(
                (byte)(data[i].R * a),
                (byte)(data[i].G * a),
                (byte)(data[i].B * a),
                data[i].A);
        }
        tex.SetData(data);
        return tex;
    }

    /// <summary>A camera-facing billboard quad at a unit direction (the same
    /// poster geometry SkyBillboard / PlanetPoster use), premultiplied alpha.</summary>
    private static void DrawBillboard(GraphicsDevice gd, Matrix view, Matrix proj,
        XnaVec dir, float halfWorld, Texture2D tex, XnaCol diffuse)
    {
        var center = dir * ObjectDist;
        var up = Math.Abs(dir.Y) > 0.995f ? XnaVec.UnitZ : XnaVec.UnitY;
        var right = XnaVec.Normalize(XnaVec.Cross(up, dir));
        var upv = XnaVec.Normalize(XnaVec.Cross(dir, right));
        var c = new[]
        {
            new VertexPositionColorTexture(center - right * halfWorld - upv * halfWorld, XnaCol.White, new Vector2(0, 1)),
            new VertexPositionColorTexture(center + right * halfWorld - upv * halfWorld, XnaCol.White, new Vector2(1, 1)),
            new VertexPositionColorTexture(center - right * halfWorld + upv * halfWorld, XnaCol.White, new Vector2(0, 0)),
            new VertexPositionColorTexture(center + right * halfWorld + upv * halfWorld, XnaCol.White, new Vector2(1, 0)),
        };
        var idx = new short[] { 0, 1, 2, 2, 1, 3 };
        gd.BlendState = BlendState.AlphaBlend;
        gd.DepthStencilState = DepthStencilState.None;
        gd.RasterizerState = RasterizerState.CullNone;
        using var eff = new BasicEffect(gd)
        {
            TextureEnabled = true,
            Texture = tex,
            VertexColorEnabled = true,
            DiffuseColor = diffuse.ToVector3(),
            LightingEnabled = false,
            View = view,
            Projection = proj,
            World = Matrix.Identity,
        };
        foreach (var pass in eff.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, c, 0, c.Length, idx, 0, 2);
        }
    }

    /// <summary>Save the central region of the backbuffer scaled up by
    /// <paramref name="zoom"/> (300%) — a magnified look at the object and its
    /// surrounding sky, proving no star specks linger around it.</summary>
    private void SaveZoomCrop(GraphicsDevice gd, string path, float zoom)
    {
        int w = gd.PresentationParameters.BackBufferWidth;
        int h = gd.PresentationParameters.BackBufferHeight;
        var full = new XnaCol[w * h];
        gd.GetBackBufferData(full);
        int cw = (int)(w / zoom), chh = (int)(h / zoom);
        int sx = (w - cw) / 2, sy = (h - chh) / 2;
        var crop = new XnaCol[cw * chh];
        for (int y = 0; y < chh; y++)
            Array.Copy(full, (sy + y) * w + sx, crop, y * cw, cw);
        using var tmp = new Texture2D(gd, cw, chh, false, SurfaceFormat.Color);
        tmp.SetData(crop);
        // Nearest-neighbour upscale to 300% so the pixels are crisp.
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
        using var fs = System.IO.File.Create(System.IO.Path.Combine(_o.OutDir, path));
        dst.SaveAsPng(fs, cw * 3, chh * 3);
        Console.WriteLine($"[p589-proof] wrote {path}");
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);
        if (_scenes.TryDequeue(out var scene))
        {
            try { scene(GraphicsDevice); }
            catch (Exception ex) { Console.WriteLine($"[p589-proof] scene FATAL: {ex}"); }
        }
        if (_scenes.Count == 0)
        {
            double avgMs = _drawSamples > 0 ? _drawTotalTicks * 1000.0 / Stopwatch.Frequency / _drawSamples : 0;
            double maxMs = _drawMaxTicks * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"[p589-proof] BlackSkyRenderer.Draw frametime: avg={avgMs:F3} ms  max={maxMs:F3} ms  samples={_drawSamples}");
            Exit();
        }
    }

    private XnaMat Proj() => XnaMat.CreatePerspectiveFieldOfView(FovRad, W / (float)H, 1f, 98000f);

    private static XnaMat LookAtDir(XnaVec fwd)
    {
        if (fwd.LengthSquared() < 1e-6f) fwd = XnaVec.UnitZ;
        fwd.Normalize();
        var up = Math.Abs(fwd.Y) > 0.995f ? XnaVec.UnitZ : XnaVec.UnitY;
        return XnaMat.CreateLookAt(XnaVec.Zero, fwd, up);
    }

    /// <summary>Sweep a view direction horizontally across the light direction:
    /// returns a direction rotating around the up axis by <paramref name="t"/>
    /// ∈ [0,1] from <paramref name="a0"/> to <paramref name="a1"/>.</summary>
    private static XnaVec SweepAcross(XnaVec center, float a0, float a1, float t)
    {
        var up = Math.Abs(center.Y) > 0.995f ? XnaVec.UnitZ : XnaVec.UnitY;
        var right = XnaVec.Normalize(XnaVec.Cross(up, center));
        var ang = a0 + (a1 - a0) * t;
        return XnaVec.Normalize(center * MathF.Cos(ang) + right * MathF.Sin(ang));
    }

    private void SkyDraw(BlackSkyRenderer black, Matrix view, Matrix proj)
    {
        var st = Stopwatch.GetTimestamp();
        black.Draw(view, proj, XnaVec.Zero);
        var ticks = Stopwatch.GetTimestamp() - st;
        _drawTotalTicks += ticks;
        if (ticks > _drawMaxTicks) _drawMaxTicks = ticks;
        _drawSamples++;
    }

    private void DrawSilhouette(GraphicsDevice gd)
    {
        // A dark triangular ship-wedge at screen centre (readability proxy).
        float cx = W / 2f, cy = H / 2f, s = 70f;
        var verts = new VertexPositionColor[]
        {
            new(new Vector3(cx - s, cy + s * 1.1f, 0), new XnaCol(8, 8, 10)),
            new(new Vector3(cx + s, cy + s * 1.1f, 0), new XnaCol(8, 8, 10)),
            new(new Vector3(cx, cy - s * 1.4f, 0), new XnaCol(8, 8, 10)),
        };
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
            gd.DrawUserPrimitives(PrimitiveType.TriangleList, verts, 0, 1);
        }
    }

    private void SavePng(GraphicsDevice gd, string path)
    {
        int w = gd.PresentationParameters.BackBufferWidth;
        int h = gd.PresentationParameters.BackBufferHeight;
        var full = new XnaCol[w * h];
        gd.GetBackBufferData(full);
        using var tmp = new Texture2D(gd, w, h, false, SurfaceFormat.Color);
        tmp.SetData(full);
        using var fs = File.Create(Path.Combine(_o.OutDir, path));
        tmp.SaveAsPng(fs, w, h);
        Console.WriteLine($"[p589-proof] wrote {path}");
    }
}
