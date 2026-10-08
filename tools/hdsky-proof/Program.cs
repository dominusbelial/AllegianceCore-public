using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaCol = Microsoft.Xna.Framework.Color;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using XnaMat = Microsoft.Xna.Framework.Matrix;

// P577 — offline REAL-WINDOW sky proof + readability metric.
//
// Renders each environment's sky (native AND hd) with the ACTUAL per-tier sky
// renderers into a real window's backbuffer, SaveAsPng's the presented frame
// (real window, not --capture/offscreen), and computes a ship-silhouette
// readability metric per entry: the mean luminance of the sky behind the ship
// (how much a dark ship has to stand out against) plus the fraction of
// over-bright pixels. This reproduces the per-environment side-by-side sheets
// and the readability numbers of the P577 report.
//
// A single canvas is drawn per (env, tier): the sky fills the frame, a dark
// ship-silhouette polygon sits at the centre, and the frame is saved. The
// silhouette contrast = sky-centre mean luminance − ship luminance.

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
        string repo = RepoRoot();
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : (env is { Length: > 0 } ? env : "/home/domingo/Development/Artwork"),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p577"),
        };
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? Directory.GetCurrentDirectory();
    }
}

sealed class ProofRunner : Game
{
    private const int W = 1280, H = 850;
    private static readonly float FovRad = MathHelper.ToRadians(50f);

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private readonly IAssetSource _assets;
    private readonly List<string[]> _rows = new();

    // environment -> planet used by the 221 map (for a per-sector-real planet).
    private static readonly (int Env, string Planet, byte Lat, byte Lon, byte Rad)[] Map =
    {
        (1, "nebplnt06bmp", 40, 100, 125),
        (2, "nebplnt43bmp", 40, 100, 125),   // Omega
        (3, "nebplnt06bmp", 40, 100, 125),   // Alderan
        (4, "nebplnt33bmp", 40, 100, 125),   // Ganymede
        (5, "nebplnt06bmp", 40, 100, 125),   // Plato / Rigel
        (6, "nebplnt25bmp", 40, 100, 125),   // Bug Nebula
        (7, "nebplnt06bmp", 40, 100, 125),
        (8, "nebplnt33bmp", 40, 100, 125),   // Aurora / Gaia
        (9, "nebplnt06bmp", 40, 100, 125),
        (10, "nebplnt06bmp", 40, 100, 125),
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
    }

    protected override void Initialize()
    {
        base.Initialize();
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[hdsky-proof] FATAL: {ex}"); }
        Exit();
    }

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine($"[hdsky-proof] art={_o.ArtRoot} out={_o.OutDir}");
        foreach (var tier in new[] { "native", "hd" })
        {
            foreach (var (env, planet, lat, lon, rad) in Map)
            {
                var cluster = new DataClusterIGC(
                    StarSeed: 371, LightDirection: new[] { 0.707f, 0f, 0.707f },
                    LightColor: 0x00FFAAAA, ScreenX: 0, ScreenY: 0, ClusterId: (short)env,
                    Debris: 0, Stars: 500, Name: $"env{env}", PosterName: $"globe{env}",
                    PlanetName: planet, PlanetSinLatitude: lat, PlanetLongitude: lon,
                    PlanetRadius: rad, Active: false, HomeSector: false);

                ISkyRenderer sky = tier == "hd"
                    ? new HdSkyRenderer(gd, _assets)
                    : new NativeSkyRenderer(gd, _assets);
                using (sky)
                {
                    sky.SetCluster(cluster);
                    var view = XnaMat.CreateLookAt(XnaVec.Zero, -XnaVec.UnitZ, XnaVec.UnitY);
                    var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, W / (float)H, 1f, 80000f);
                    gd.Clear(XnaCol.Black);
                    sky.Draw(view, proj, XnaVec.Zero);

                    // Dark ship-silhouette polygon at centre.
                    DrawSilhouette(gd, view, proj);

                    string name = $"{tier}_env{env}";
                    SavePng(gd, Path.Combine(_o.OutDir, name + ".png"));

                    // P577b — HD planets use NATIVE art: render a second HD frame
                    // aimed AT the planet so the native disc is on-axis and the
                    // "native planet visible in HD mode" proof is unambiguous.
                    if (tier == "hd" && !string.IsNullOrEmpty(cluster.PlanetName))
                        DrawPlanetCentered(gd, sky, cluster, Path.Combine(_o.OutDir, $"{name}_planet.png"));

                    var skyStats = CentreStats(gd);
                    _rows.Add(new[] { tier, env.ToString(), planet,
                        skyStats.MeanLuma.ToString("F3"), skyStats.MaxLuma.ToString("F3"),
                        skyStats.BrightPct.ToString("F1"), skyStats.Contrast.ToString("F3") });
                    Console.WriteLine($"[hdsky-proof] {name}: skyMean={skyStats.MeanLuma:F3} max={skyStats.MaxLuma:F3} bright%={skyStats.BrightPct:F1} contrast={skyStats.Contrast:F3}");
                }
            }
        }
        WriteCsv();
        Console.WriteLine($"[hdsky-proof] done — {_rows.Count} rows -> {_o.OutDir}");
    }

    /// <summary>
    /// P577b — render an HD frame aimed straight AT the planet direction so the
    /// NATIVE planet disc is on-axis (a real window, same HdSkyRenderer), and
    /// save it as the "native planet visible in HD mode" proof.
    /// </summary>
    private void DrawPlanetCentered(GraphicsDevice gd, ISkyRenderer sky,
        DataClusterIGC cluster, string path)
    {
        var dir = NativeSkyMath.PlanetDirection(cluster.PlanetSinLatitude, cluster.PlanetLongitude);
        var fwd = new XnaVec(dir.X, dir.Y, dir.Z);
        if (fwd.LengthSquared() < 1e-6f) return;
        fwd.Normalize();
        var up = Math.Abs(fwd.Y) > 0.995f ? XnaVec.UnitZ : XnaVec.UnitY;
        var view = XnaMat.CreateLookAt(XnaVec.Zero, fwd, up);
        var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, W / (float)H, 1f, 80000f);
        gd.Clear(XnaCol.Black);
        sky.Draw(view, proj, XnaVec.Zero);
        SavePng(gd, path);
        Console.WriteLine($"[hdsky-proof] planet-centered: {Path.GetFileName(path)}");
    }

    private void DrawSilhouette(GraphicsDevice gd, XnaMat view, XnaMat proj)
    {
        // A dark fighter-like silhouette (two triangles) drawn as a full-bright
        // dark plane at the camera centre so it sits "in front of" the sky.
        var verts = new[]
        {
            new VertexPositionColor(new XnaVec(-0.06f, 0.07f, -1f), new XnaCol(18, 18, 22)),
            new VertexPositionColor(new XnaVec(0.06f, 0.07f, -1f), new XnaCol(18, 18, 22)),
            new VertexPositionColor(new XnaVec(0f, -0.09f, -1f), new XnaCol(18, 18, 22)),
            new VertexPositionColor(new XnaVec(-0.02f, 0.07f, -1f), new XnaCol(12, 12, 16)),
            new VertexPositionColor(new XnaVec(0.02f, 0.07f, -1f), new XnaCol(12, 12, 16)),
            new VertexPositionColor(new XnaVec(0f, 0.0f, -1f), new XnaCol(12, 12, 16)),
        };
        var idx = new short[] { 0, 1, 2, 3, 4, 5 };
        var prevDepth = gd.DepthStencilState;
        var prevBlend = gd.BlendState;
        gd.DepthStencilState = DepthStencilState.None;
        gd.BlendState = BlendState.Opaque;
        using var eff = new BasicEffect(gd)
        {
            VertexColorEnabled = true, LightingEnabled = false,
            View = view, Projection = proj, World = XnaMat.Identity,
        };
        foreach (var pass in eff.CurrentTechnique.Passes)
        {
            pass.Apply();
            gd.DrawUserIndexedPrimitives(PrimitiveType.TriangleList, verts, 0, 6, idx, 0, 2);
        }
        gd.DepthStencilState = prevDepth;
        gd.BlendState = prevBlend;
    }

    private (float MeanLuma, float MaxLuma, float BrightPct, float Contrast) CentreStats(GraphicsDevice gd)
    {
        int bw = gd.PresentationParameters.BackBufferWidth;
        int bh = gd.PresentationParameters.BackBufferHeight;
        var full = new XnaCol[bw * bh];
        gd.GetBackBufferData(full);
        int cx = bw / 2, cy = bh / 2;
        int rw = 260, rh = 260;
        int x0 = cx - rw / 2, y0 = cy - rh / 2;
        double sum = 0; float max = 0; int bright = 0;
        for (int y = 0; y < rh; y++)
            for (int x = 0; x < rw; x++)
            {
                var c = full[(y0 + y) * bw + (x0 + x)];
                float l = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
                sum += l; if (l > max) max = l; if (l > 200) bright++;
            }
        float mean = (float)(sum / (rw * rh));
        double ringSum = 0; int ringN = 0;
        for (int y = 0; y < rh; y += 4)
            for (int x = 0; x < rw; x += 4)
            {
                var c = full[(y0 + y) * bw + (x0 + x)];
                float l = 0.2126f * c.R + 0.7152f * c.G + 0.0722f * c.B;
                ringSum += l; ringN++;
            }
        float ringMean = (float)(ringSum / Math.Max(1, ringN));
        float contrast = ringMean - mean;   // how much the dark ship differs from the sky
        return (mean, max, 100f * bright / (rw * rh), contrast);
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
    }

    private void WriteCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("tier,env,planet,sky_mean_luma,sky_max_luma,bright_pct,ship_contrast");
        foreach (var r in _rows) sb.AppendLine(string.Join(",", r));
        File.WriteAllText(Path.Combine(_o.OutDir, "readability.csv"), sb.ToString());
    }
}
