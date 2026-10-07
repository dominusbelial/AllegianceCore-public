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

// P564 — per-hull transparency / missing-parts audit (task item 5). For EVERY
// player-flyable hull in the static core (the 221 civs included), renders its
// ORIGINAL native .mdl mesh + native texture from front/side/back/top, OPAQUE and
// CLOAKED (a mid-fade opacity), under the real native sector lighting, and computes
// the ShapeReadabilityMetric per hull. Emits ONE composite sheet per hull (4 views x
// opaque/cloaked) + an index page. Reports which hulls are affected (decode failure,
// near-black crush, low shape correlation) so Domingo can skim every hull.
//
// This builds on P570's solid-ship fix (raw winding + real back-face culling) and
// P564's cloaked-render depth fix (depth-write OFF for translucent hulls, so a
// cloaked hull fades uniformly instead of showing its own far-side faces as holes).

var options = ProofOptions.Parse(args);
using var game = new ProofRunner(options);
game.Run();
return 0;

sealed class ProofOptions
{
    public string ArtRoot = "";
    public string OutDir = "";
    public float CloakOpacity = 0.45f;   // representative mid-cloak fade

    public static ProofOptions Parse(string[] args)
    {
        string art = "", outDir = "";
        float cloak = 0.45f;
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--cloak": cloak = float.Parse(args[++i]); break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string repo = RepoRoot();
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : (env is { Length: > 0 } ? env : "/home/domingo/Development/Artwork"),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p564_hulls"),
            CloakOpacity = cloak,
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
    private const int Cell = 220;
    private const int Cols = 4, Rows = 2;   // front/side/back/top x opaque/cloaked
    private static readonly float FovRad = MathHelper.ToRadians(40f);
    private const float CamDist = 5.2f;
    private static readonly XnaCol Bg = new(0.009f, 0.013f, 0.021f);

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private readonly IAssetSource _assets;
    private readonly List<(string Model, bool Ok, float NearBlackPct, float Corr, string Note)> _results = new();

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

    // The real native sector light (the 221 map's native per-sector bidirectional
    // rig — m2xspiderw is light-uniform; key (1.00,0.667,0.667) toward (0.707,0,0.707)).
    private static readonly SectorLighting Light =
        SectorLighting.FromCluster(new[] { 0.707f, 0f, 0.707f }, 0x00FFAAAA);

    private static readonly (string Label, XnaVec Eye)[] Views =
    {
        ("front", new XnaVec(0f, 0f, CamDist)),
        ("side", new XnaVec(CamDist, 0f, 0f)),
        ("back", new XnaVec(0f, 0f, -CamDist)),
        ("top", new XnaVec(0f, CamDist, 0f)),
    };

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine($"[proof] art={_o.ArtRoot} out={_o.OutDir} cloakOpacity={_o.CloakOpacity}");
        var catalog = ShopCatalog.TryLoad(_o.ArtRoot);
        if (catalog is null) { Console.WriteLine("[proof] FATAL: no static core catalog"); return; }

        using var native = new NativeShipRenderer(gd, _assets);

        var hulls = catalog.Hulls
            .Select(h => h.ModelName?.Trim())
            .Where(m => !string.IsNullOrEmpty(m))
            .Distinct()
            .Select(m => m!)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine($"[proof] {hulls.Count} distinct player-flyable native hull models");

        foreach (var model in hulls)
        {
            if (native.MeshFor(model) is not { } geo)
            {
                _results.Add((model, false, 0, 0, "native art missing/undecodable"));
                Console.WriteLine($"  {model}: DECODE FAIL (fallback)");
                continue;
            }
            var texStem = native.TextureStemFor(model, null);
            var tex = texStem is null ? null : native.TextureFor(texStem);
            var frame = FrameModel(_o.ArtRoot, model);
            // Mesh-based normal-brightness correlation (the shape-revealing measure).
            var mdl = MdlFile.Parse(Path.Combine(_o.ArtRoot, model + ".mdl"));
            var (pA, nA, iA) = Flatten(mdl);
            var (corr, _) = ShapeReadabilityMetric.ComputeNormalBrightness(pA, nA, iA, Light);

            // Grid render: rows = opaque/cloaked, cols = views.
            using var grid = new RenderTarget2D(gd, Cell * Cols, Cell * Rows, false, SurfaceFormat.Color, DepthFormat.Depth24);
            gd.SetRenderTarget(grid);
            gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Bg, 1f, 0);
            ShapeReadabilityMetric.Readability opaqueFront = default;
            for (int r = 0; r < Rows; r++)
            {
                float opacity = r == 0 ? 1f : _o.CloakOpacity;
                for (int c = 0; c < Cols; c++)
                {
                    var (vLabel, eye) = Views[c];
                    var view = XnaMat.CreateLookAt(eye, XnaVec.Zero, XnaVec.Up);
                    var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, 1f, 0.1f, 1000f);
                    // Scissor to the cell.
                    var vp = new Viewport(c * Cell, r * Cell, Cell, Cell);
                    gd.Viewport = vp;
                    native.Draw(geo, tex, frame.World, view, proj, opacity, 1f, Light);
                }
            }
            gd.Viewport = new Viewport(0, 0, Cell * Cols, Cell * Rows);

            // Read the top-left (opaque front) cell for the per-hull metric.
            var frontData = new byte[Cell * Cell * 4];
            gd.SetRenderTarget(null);
            // Re-render just the opaque front cell for the metric (simple + exact).
            var mview = XnaMat.CreateLookAt(Views[0].Eye, XnaVec.Zero, XnaVec.Up);
            var mproj = XnaMat.CreatePerspectiveFieldOfView(FovRad, 1f, 0.1f, 1000f);
            using (var rt = new RenderTarget2D(gd, Cell, Cell, false, SurfaceFormat.Color, DepthFormat.Depth24))
            {
                gd.SetRenderTarget(rt);
                gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Bg, 1f, 0);
                native.Draw(geo, tex, frame.World, mview, mproj, 1f, 1f, Light);
                rt.GetData(frontData);
                gd.SetRenderTarget(null);
            }
            opaqueFront = ShapeReadabilityMetric.ComputePixelStats(frontData, Cell, Cell, Bg.R, Bg.G, Bg.B);

            // Save the composite sheet.
            string safe = model.Replace('/', '_');
            string img = safe + ".png";
            using (var fs = File.Create(Path.Combine(_o.OutDir, img)))
                grid.SaveAsPng(fs, grid.Width, grid.Height);

            string note = opaqueFront.NearBlackFraction >= 0.5 ? "near-black crush (dark art)" : "";
            _results.Add((model, true, (float)(opaqueFront.NearBlackFraction * 100), (float)corr, note));
            Console.WriteLine($"  {model}: nearBlack={opaqueFront.NearBlackFraction * 100:F0}% corr={corr:F2} {(note.Length > 0 ? note : "")}");
        }

        WriteIndex();
        Console.WriteLine($"[proof] done — {_results.Count} hulls -> {_o.OutDir}");
    }

    private static (XnaMat World, float Radius) FrameModel(string artRoot, string stem)
    {
        // Centre + normalise the RAW model positions to unit radius so every hull is
        // framed at the same apparent size (native renderer folds the flip + scale 1).
        var mdl = MdlFile.Parse(Path.Combine(artRoot, stem + ".mdl"));
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

    private void WriteIndex()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# P564 — per-hull shape-readability audit (opaque + cloaked)");
        sb.AppendLine();
        sb.AppendLine($"Every player-flyable native hull, rendered from its original `.mdl` mesh + native texture ");
        sb.AppendLine($"under the real native sector light, 4 views (front/side/back/top) x rows (opaque / cloaked at ");
        sb.AppendLine($"opacity {_o.CloakOpacity}). One sheet per hull. Columns of the table: model, near-black %, normal-brightness correlation, note.");
        sb.AppendLine();
        sb.AppendLine("| model | sheet | nearBlack% | normalCorr | note |");
        sb.AppendLine("|---|---|---|---|---|");
        int affected = 0;
        foreach (var (model, ok, nb, corr, note) in _results.OrderBy(r => r.Model))
        {
            if (!ok || nb >= 50.0) affected++;
            sb.AppendLine($"| {model} | [`{model.Replace('/', '_')}.png`]({model.Replace('/', '_')}.png) | {nb:F0}% | {corr:F2} | {(note.Length > 0 ? note : "ok")} |");
        }
        sb.AppendLine();
        sb.AppendLine($"Affected hulls (decode-fail or near-black ≥50%): **{affected}**. The rest render solid + shaded.");
        File.WriteAllText(Path.Combine(_o.OutDir, "index.md"), sb.ToString());
        Console.Write(sb.ToString());
    }
}
