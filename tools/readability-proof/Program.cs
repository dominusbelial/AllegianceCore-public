using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaCol = Microsoft.Xna.Framework.Color;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using XnaMat = Microsoft.Xna.Framework.Matrix;

// P564 — headless readability metric proof. Renders representative native objects
// (an Advanced Fighter, a dark-liveried carrier, a native station model, an asteroid)
// from front/side/back under the real client's native per-sector lighting, in the
// brightest and darkest sectors, and computes the ShapeReadabilityMetric per cell:
// luminance std-dev, 10th-90th percentile spread, near-black fraction (pixel-based)
// plus the normal-vs-brightness correlation and Lambertian variation (mesh-based).
// Emits each cell PNG + a machine-readable metrics table (CSV + markdown) so the P564
// readability table is reproducible. Uses the NATIVE renderers (NativeShipRenderer /
// NativeStationRenderer / NativeAsteroidRenderer) on the real .mdl art — the exact
// pixels the game draws.

var options = ProofOptions.Parse(args);
using var game = new ProofRunner(options);
game.Run();
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
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p564"),
        };
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        return Directory.GetCurrentDirectory();
    }
}

sealed class ProofRunner : Game
{
    private const int Cell = 240;
    private static readonly float FovRad = MathHelper.ToRadians(40f);
    private const float CamDist = 5.2f;          // unit-radius object, camera at 5.2 radii
    private static readonly XnaCol Bg = new(0.011f, 0.016f, 0.025f);

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private readonly IAssetSource _assets;
    private readonly List<(string Obj, string Light, string View, ShapeReadabilityMetric.Readability R)> _rows = new();

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _assets = new DirectoryAssetSource(o.ArtRoot);
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = 64;
        _gdm.PreferredBackBufferHeight = 64;
        Directory.CreateDirectory(o.OutDir);
    }

    protected override void Initialize()
    {
        base.Initialize();
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[proof] FATAL: {ex}"); }
        Exit();
    }

    private static readonly (string Label, SectorLighting Light)[] Lights =
    {
        // Bright = the real m2xspiderw native light (P530/P529 measured): key colour
        // (1.00,0.667,0.667) toward (0.707,0,0.707). The flown map is light-uniform.
        ("bright", SectorLighting.FromCluster(new[] { 0.707f, 0f, 0.707f }, 0x00FFAAAA)),
        // Dark = the documented dim, cool stand-in P530 used for the readability
        // contrast (the map has no darker sector; labelled a stand-in).
        ("dark", SectorLighting.FromCluster(new[] { 0f, 1f, 0f }, 0x00304078)),
    };

    private static readonly (string Obj, string Kind, string Stem, string Desc)[] Objects =
    {
        ("ship_fighter", "ship", "faohshafig", "Advanced Fighter"),
        ("ship_carrier", "ship", "cap401", "dark-liveried carrier"),
        ("station", "station", "ss24a", "Garrison station ss24a"),
        ("asteroid", "asteroid", "bgrnd03", "asteroid bgrnd03"),
    };

    private static readonly (string Label, XnaVec Eye)[] Views =
    {
        ("front", new XnaVec(0f, 0f, CamDist)),
        ("side", new XnaVec(CamDist, 0f, 0f)),
        ("back", new XnaVec(0f, 0f, -CamDist)),
    };

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine($"[proof] art={_o.ArtRoot} out={_o.OutDir}");
        using (var probe = new BasicEffect(gd))
            Console.WriteLine($"[proof] DEBUG BasicEffect default LightingEnabled = {probe.LightingEnabled}");
        foreach (var (lLabel, lLight) in Lights)
            Console.WriteLine($"[proof] light[{lLabel}] key=({lLight.KeyColor.X:F3},{lLight.KeyColor.Y:F3},{lLight.KeyColor.Z:F3}) toward=({lLight.AltDirection.X:F3},{lLight.AltDirection.Y:F3},{lLight.AltDirection.Z:F3})");

        using var shipRenderer = new NativeShipRenderer(gd, _assets);
        using var stationRenderer = new NativeStationRenderer(gd, _assets);
        using var astRenderer = new NativeAsteroidRenderer(gd, _assets);

        foreach (var (obj, kind, stem, desc) in Objects)
        {
            // Build the model once (for the mesh-based correlation + framing).
            MdlFile mdl;
            try { mdl = MdlFile.Parse(Path.Combine(_o.ArtRoot, stem + ".mdl")); }
            catch (Exception ex) { Console.WriteLine($"[proof] {obj} ({stem}) parse failed: {ex.Message}"); continue; }

            var frame = FrameModel(mdl);
            var (posArr, nrmArr, idxArr) = Flatten(mdl);
            Console.WriteLine($"[proof] {obj} ({desc}): meshes={mdl.Meshes.Count} frameRadius={frame.Radius:F3}");

            foreach (var (lLabel, lLight) in Lights)
            {
                var (corr, lamVar) = ShapeReadabilityMetric.ComputeNormalBrightness(posArr, nrmArr, idxArr, lLight);
                foreach (var (vLabel, eye) in Views)
                {
                    var view = XnaMat.CreateLookAt(eye, XnaVec.Zero, XnaVec.Up);
                    var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, 1f, 0.1f, 1000f);
                    var (rgba, done) = RenderCell(gd, kind, stem, shipRenderer, stationRenderer, astRenderer,
                        frame.World, view, proj, lLight, $"{obj}_{lLabel}_{vLabel}.png");
                    if (!done) { Console.WriteLine($"[proof] {obj} geometry MISSING — skipped"); goto nextObject; }

                    var pix = ShapeReadabilityMetric.ComputePixelStats(rgba, Cell, Cell, Bg.R, Bg.G, Bg.B);
                    var final = pix with { NormalBrightnessCorrelation = corr, LambertVariation = lamVar };
                    _rows.Add((obj, lLabel, vLabel, final));
                }
                Console.WriteLine($"[proof] {obj} [{lLabel}]: corr={corr:F3} lamVar={lamVar:F3}");
            }
        nextObject: ;
        }

        WriteTable();
        Console.WriteLine($"[proof] done — cells + table in {_o.OutDir}");
    }

    private (byte[] Rgba, bool Done) RenderCell(GraphicsDevice gd, string kind, string stem,
        NativeShipRenderer sr, NativeStationRenderer str, NativeAsteroidRenderer ar,
        XnaMat world, XnaMat view, XnaMat proj, SectorLighting light, string fileName)
    {
        using var rt = new RenderTarget2D(gd, Cell, Cell, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Bg, 1f, 0);

        switch (kind)
        {
            case "ship":
            {
                if (sr.MeshFor(stem) is not { } g) { gd.SetRenderTarget(null); return (Array.Empty<byte>(), false); }
                var tex = Tex(sr.TextureStemFor(stem, null), sr);
                sr.Draw(g, tex, world, view, proj, 1f, 1f, light);
                break;
            }
            case "station":
            {
                if (str.MeshFor(stem) is not { } sg) { gd.SetRenderTarget(null); return (Array.Empty<byte>(), false); }
                var stex = Tex(str.TextureStemFor(stem, null), str);
                str.Draw(sg, stex, world, view, proj, 1f, light);
                break;
            }
            default:
            {
                if (ar.GeometryFor(stem) is not { } ag) { gd.SetRenderTarget(null); return (Array.Empty<byte>(), false); }
                var atex = Tex(ar.TextureStemFor(stem, null), ar);
                ar.Draw(ag, atex, world, view, proj, 1f, light);
                break;
            }
        }

        var data = new byte[Cell * Cell * 4];
        rt.GetData(data);
        gd.SetRenderTarget(null);
        SaveRgba(gd, Path.Combine(_o.OutDir, fileName), data, Cell, Cell);
        return (data, true);
    }

    private static Texture2D? Tex<T>(string? stem, T r) where T : class
    {
        // Dynamic-dispatch to whichever renderer holds the texture cache.
        return stem is null ? null
            : r switch
            {
                NativeShipRenderer s => s.TextureFor(stem),
                NativeStationRenderer st => st.TextureFor(stem),
                NativeAsteroidRenderer a => a.TextureFor(stem),
                _ => null,
            };
    }

    private static (XnaMat World, float Radius) FrameModel(MdlFile mdl)
    {
        var all = new List<XnaVec>();
        foreach (var m in mdl.Meshes)
            for (int v = 0; v < m.VertexCount; v++)
                all.Add(new XnaVec(m.Positions[3 * v], m.Positions[3 * v + 1], m.Positions[3 * v + 2]));
        var cen = XnaVec.Zero;
        foreach (var p in all) cen += p;
        if (all.Count > 0) cen /= all.Count;
        float r2 = 0f;
        foreach (var p in all) r2 = Math.Max(r2, (p - cen).LengthSquared());
        float r = (float)Math.Sqrt(r2);
        if (r <= 1e-6f) r = 1f;
        return (XnaMat.CreateTranslation(-cen) * XnaMat.CreateScale(1f / r), 1f);
    }

    private static (float[] pos, float[] nrm, int[] idx) Flatten(MdlFile mdl)
    {
        var pos = new List<float>(); var nrm = new List<float>(); var idx = new List<int>();
        foreach (var m in mdl.Meshes)
        {
            int baseV = pos.Count / 3;
            pos.AddRange(m.Positions); nrm.AddRange(m.Normals);
            foreach (var i in m.Indices) idx.Add(i + baseV);
        }
        return (pos.ToArray(), nrm.ToArray(), idx.ToArray());
    }

    private static void SaveRgba(GraphicsDevice gd, string path, byte[] rgba, int w, int h)
    {
        var cols = new XnaCol[w * h];
        for (int i = 0; i < w * h; i++) cols[i] = new XnaCol((int)rgba[i * 4], (int)rgba[i * 4 + 1], (int)rgba[i * 4 + 2], 255);
        using var tex = new Texture2D(gd, w, h, false, SurfaceFormat.Color);
        tex.SetData(cols);
        using var fs = File.Create(path);
        tex.SaveAsPng(fs, w, h);
    }

    private void WriteTable()
    {
        var csv = new StringBuilder();
        csv.AppendLine("object,light,view,meanLum,lumStdDev,spread10_90,nearBlackFrac,normalCorr,lamVar");
        foreach (var (o, l, v, r) in _rows)
            csv.AppendLine($"{o},{l},{v},{r.MeanLuminance:F2},{r.LuminanceStdDev:F2},{r.Spread10_90:F2},{r.NearBlackFraction:F3},{r.NormalBrightnessCorrelation:F3},{r.LambertVariation:F3}");
        File.WriteAllText(Path.Combine(_o.OutDir, "readability.csv"), csv.ToString());

        var sb = new StringBuilder();
        sb.AppendLine("| object | light | view | meanLum | stdDev | p10-90 | nearBlack% | normalCorr | lamVar |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
        foreach (var (o, l, v, r) in _rows)
            sb.AppendLine($"| {o} | {l} | {v} | {r.MeanLuminance:F1} | {r.LuminanceStdDev:F1} | {r.Spread10_90:F1} | {r.NearBlackFraction * 100:F1}% | {r.NormalBrightnessCorrelation:F3} | {r.LambertVariation:F3} |");
        File.WriteAllText(Path.Combine(_o.OutDir, "readability.md"), sb.ToString());
        Console.Write(sb.ToString());
    }
}

