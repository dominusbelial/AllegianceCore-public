using System;
using System.Collections.Generic;
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

// P588 — offline REAL-WINDOW black-sky proof.
//
// Renders the ACTUAL per-tier sky renderers (BlackSkyRenderer, NativeSkyRenderer,
// HdSkyRenderer) into a real window's backbuffer and SaveAsPng's each presented
// frame (real window 2560x1440, not --capture/offscreen). Frames are rendered
// from the MAIN game loop's Draw (one scene per tick), so the capture is a real
// presented backbuffer — the same path the live client uses. Covers:
//   * the three target sectors (Ganymede / Alderan / Rigel) each in the BLACK
//     tier from 4 view directions, plus a planet-centred view;
//   * Alderan in the NATIVE and HD tiers to prove the opt-in domes still draw.
// Space dust is drawn by the live client (see the live-proof captures).

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
            OutDir = outDir.Length > 0 ? outDir : "blacksky-proof-out",
        };
    }
}

sealed class ProofRunner : Game
{
    private const int W = 2560, H = 1440;
    private static readonly float FovRad = MathHelper.ToRadians(50f);

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private readonly IAssetSource _assets;
    private readonly Queue<Action<GraphicsDevice>> _scenes = new();

    private static readonly (string Name, int Env, string Planet, byte Lat, byte Lon, byte Rad)[] Targets =
    {
        ("Alderan", 3, "nebplnt06bmp", 051, 032, 092),
        ("Ganymede", 4, "nebplnt33bmp", 035, 020, 069),
        ("Rigel", 5, "nebplnt06bmp", 039, 014, 095),
    };

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

    private DataClusterIGC ClusterFor(string name, int env, string poster, string planet, byte lat, byte lon, byte rad)
        => new(
            StarSeed: (uint)(601 + env), LightDirection: new[] { 0.614f, 0.068f, 0.785f },
            LightColor: 0x00FFAAAA, ScreenX: 0, ScreenY: 0, ClusterId: (short)env,
            Debris: 0, Stars: 652, Name: name, PosterName: poster,
            PlanetName: planet, PlanetSinLatitude: lat, PlanetLongitude: lon,
            PlanetRadius: rad, Active: false, HomeSector: false);

    private void BuildScenes()
    {
        foreach (var (name, env, planet, lat, lon, rad) in Targets)
        {
            var cluster = ClusterFor(name, env, $"globe{env}", planet, lat, lon, rad);
            for (int d = 0; d < 4; d++)
            {
                int dirIdx = d;
                _scenes.Enqueue(gd =>
                {
                    using var black = new BlackSkyRenderer(gd, _assets);
                    black.SetCluster(cluster);
                    var fwd = Yaw(-XnaVec.UnitZ, dirIdx * MathHelper.PiOver2);
                    var view = XnaMat.CreateLookAt(XnaVec.Zero, fwd, XnaVec.UnitY);
                    var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, W / (float)H, 1f, 94100f);
                    gd.Clear(XnaCol.Black);
                    black.Draw(view, proj, XnaVec.Zero);
                    SavePng(gd, Path.Combine(_o.OutDir, $"black_{name}_d{dirIdx}.png"));
                });
            }
            _scenes.Enqueue(gd =>
            {
                using var black = new BlackSkyRenderer(gd, _assets);
                black.SetCluster(cluster);
                DrawPlanetCentered(gd, black, cluster, Path.Combine(_o.OutDir, $"black_{name}_planet.png"));
            });
        }

        var alderan = ClusterFor("Alderan", 3, "globe3", "nebplnt06bmp", 040, 021, 086);
        var yaw0 = XnaMat.CreateLookAt(XnaVec.Zero, -XnaVec.UnitZ, XnaVec.UnitY);
        var proj0 = XnaMat.CreatePerspectiveFieldOfView(FovRad, W / (float)H, 1f, 94200f);
        _scenes.Enqueue(gd =>
        {
            using var nat = new NativeSkyRenderer(gd, _assets);
            nat.SetCluster(alderan);
            gd.Clear(XnaCol.Black);
            nat.Draw(yaw0, proj0, XnaVec.Zero);
            SavePng(gd, Path.Combine(_o.OutDir, "native_Alderan_d0.png"));
        });
        _scenes.Enqueue(gd =>
        {
            using var nat = new NativeSkyRenderer(gd, _assets);
            nat.SetCluster(alderan);
            DrawPlanetCentered(gd, nat, alderan, Path.Combine(_o.OutDir, "native_Alderan_planet.png"));
        });
        _scenes.Enqueue(gd =>
        {
            using var hd = new HdSkyRenderer(gd, _assets);
            hd.SetCluster(alderan);
            gd.Clear(XnaCol.Black);
            hd.Draw(yaw0, proj0, XnaVec.Zero);
            SavePng(gd, Path.Combine(_o.OutDir, "hd_Alderan_d0.png"));
        });
        _scenes.Enqueue(gd =>
        {
            using var hd = new HdSkyRenderer(gd, _assets);
            hd.SetCluster(alderan);
            DrawPlanetCentered(gd, hd, alderan, Path.Combine(_o.OutDir, "hd_Alderan_planet.png"));
        });
        Console.WriteLine($"[blacksky-proof] {_scenes.Count} scenes queued -> {_o.OutDir}");
    }

    protected override void Draw(GameTime gameTime)
    {
        base.Draw(gameTime);
        if (_scenes.TryDequeue(out var scene))
        {
            try { scene(GraphicsDevice); }
            catch (Exception ex) { Console.WriteLine($"[blacksky-proof] scene FATAL: {ex}"); }
        }
        if (_scenes.Count == 0) Exit();
    }

    private void DrawPlanetCentered(GraphicsDevice gd, ISkyRenderer sky,
        DataClusterIGC cluster, string path)
    {
        var dir = NativeSkyMath.PlanetDirection(cluster.PlanetSinLatitude, cluster.PlanetLongitude);
        var fwd = new XnaVec(dir.X, dir.Y, dir.Z);
        if (fwd.LengthSquared() < 1e-6f) return;
        fwd.Normalize();
        var up = Math.Abs(fwd.Y) > 0.931f ? XnaVec.UnitZ : XnaVec.UnitY;
        var view = XnaMat.CreateLookAt(XnaVec.Zero, fwd, up);
        var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, W / (float)H, 1f, 95600f);
        gd.Clear(XnaCol.Black);
        sky.Draw(view, proj, XnaVec.Zero);
        SavePng(gd, path);
    }

    private static XnaVec Yaw(XnaVec v, float radians)
    {
        float c = MathF.Cos(radians), s = MathF.Sin(radians);
        return new XnaVec(v.X * c - v.Z * s, v.Y, v.X * s + v.Z * c);
    }

    private void SavePng(GraphicsDevice gd, string path)
    {
        int w = gd.PresentationParameters.BackBufferWidth;
        int h = gd.PresentationParameters.BackBufferHeight;
        var full = new XnaCol[w * h];
        gd.GetBackBufferData(full);
        using var tmp = new Texture2D(gd, w, h, false, SurfaceFormat.Color);
        tmp.SetData(full);
        using var fs = File.Create(path);
        tmp.SaveAsPng(fs, w, h);
        Console.WriteLine($"[blacksky-proof] wrote {Path.GetFileName(path)}");
    }
}
