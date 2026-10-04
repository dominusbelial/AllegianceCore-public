// roster-capture — P305/P331 reference-sheet + networked wire-path renderer.
//
// Turns the real ship roster into legible reference sheets, reusing the exact
// rendering path the in-game client uses: HullModelResolver + ShipRenderer.Draw
// + compiled P287 content models.
//
// Two modes:
//
//   roster-capture dump [--dump PATH] [--art PATH] [--content PATH] [--out DIR]
//     Offline render straight from the vizfight `roster` server's
//     roster_dump.json: roster_all.jpg (all models, faction columns) plus one
//     roster_<civ>.jpg per faction with a civ-class label under every ship,
//     plus roster_legend.md and roster_variety.txt.
//
//   roster-capture wire [--port USHORT] [--side SHORT] [--art PATH]
//                       [--content PATH] [--out DIR]
//     Networked end-to-end proof: a real ConnectionSession client joins the
//     running vizfight `roster` server over the wire, seats on <side>, and
//     renders the ships its client ACTUALLY RECEIVES as same-side teammates
//     (the real EXPORT/UPDATE path) -> roster_net_<civ>_wire.jpg.
//
//   roster-capture turnaround [--dump PATH] [--art PATH] [--content PATH]
//                             [--out DIR]
//     (P348) Per-hull 4-angle turnaround reference images (front / right /
//     back / left) for image-conditioned 3D regeneration (Meshy-style
//     multi-image-to-3D): the same rendering path, one clean isolated shot per
//     canonical angle of a single hull on a plain solid neutral background
//     (no stars/grid/HUD), camera orbited around the ship's vertical axis at a
//     fixed low elevation, every hull scaled+centred so all 72 read at the
//     same apparent size and margin. Default output: docs/meshy_hints/
//     <asset>/<asset>_<view>.png plus docs/meshy_hints/turnaround_manifest.json.
//
// Defaults for --dump/--art/--content/--out resolve relative to the repo root
// (found by walking up to AllegianceCore.slnx from the running assembly).
//
// Requires the MonoGame client's compiled Content to exist (run a normal build
// of Allegiance.ClientCore.MonoGame first) and, for `wire`, a live
// `vizfight roster` server.

using System.Text.Json;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Content;
using Allegiance.Core.Wire;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;

internal static class Program
{
    private static void Main(string[] args)
    {
        var opts = Options.Parse(args);
        using var game = new RosterCapture(opts);
        game.Run();
        Console.WriteLine($"[roster-capture] {opts.Mode} done");
    }
}

internal sealed class Options
{
    public string Mode = "dump";
    public string DumpPath = "";
    public string ArtRoot = "";
    public string ContentRoot = "";
    public string OutDir = "";
    public ushort Port = 33143;
    public short Side = 0;
    public string HullSpecs = "";

    public static Options Parse(string[] args)
    {
        string root = ResolveRepoRoot();
        string mode = args.Length > 0 ? args[0] : "";
        mode = mode is "wire" or "turnaround" or "stride" or "orient" or "hullpreview" or "spin" ? mode : "dump";
        var o = new Options
        {
            Mode = mode,
            DumpPath = Path.Combine(root, "tools", "vizfight", "roster_dump.json"),
            ArtRoot = Path.Combine(root, "..", "Artwork"),
            ContentRoot = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame",
                "bin", "Debug", "net10.0", "Content"),
            OutDir = Path.Combine(root, "docs", mode == "turnaround" ? "meshy_hints" : "P305_roster"),
        };

        int i = (args.Length > 0 && (args[0] is "dump" or "wire" or "turnaround" or "orient" or "hullpreview" or "spin")) ? 1 : 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--dump":   o.DumpPath = args[++i]; break;
                case "--art":    o.ArtRoot = args[++i]; break;
                case "--content":o.ContentRoot = args[++i]; break;
                case "--out":    o.OutDir = args[++i]; break;
                case "--port":   o.Port = ushort.Parse(args[++i]); break;
                case "--side":   o.Side = short.Parse(args[++i]); break;
                case "--hulls":  o.HullSpecs = args[++i]; break;
                default: break;
            }
            i++;
        }
        return o;
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        // Fallback: assume run from the repo root's bin.
        return Directory.GetCurrentDirectory();
    }
}

internal sealed class RosterCapture : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly Options _o;

    private readonly ShopCatalog _catalog;
    private readonly HullModelResolver _resolver = new();
    private readonly Dictionary<string, XnaModel> _models = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _textures = new(StringComparer.Ordinal);
    private readonly ShipRenderer _shipRenderer;

    private SpriteBatch _batch = null!;
    private SpriteFont _font = null!;
    private SpriteFont _fontBig = null!;
    private Texture2D _white = null!;

    private const float ColSpacing = 230f;
    private const float RowSpacing = 175f;
    private const float TileRadius = 58f;

    /// <summary>P348 turnaround backdrop — a plain mid sage-green, chosen so it
    /// is neither black nor white and is unlikely to collide with a real hull
    /// surface colour an image generator might mistake for the subject.</summary>
    private static readonly Color BgColor = new(78, 100, 88);

    private sealed record Ship(JsonElement E)
    {
        public string Asset => E.GetProperty("asset").GetString()!;
        public string Civ => E.GetProperty("civ").GetString()!;
        public string CivClass => E.GetProperty("civClass").GetString()!;
        public string ModelName => E.GetProperty("modelName").GetString()!;
        public string DisplayName => E.GetProperty("displayName").GetString()!;
        public int Column => E.GetProperty("column").GetInt32();
        public int Row => E.GetProperty("row").GetInt32();
        public short HullId => E.GetProperty("hullId").GetInt16();
    }

    public RosterCapture(Options o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = 64;
        _gdm.PreferredBackBufferHeight = 64;
        Content.RootDirectory = o.ContentRoot;
        Directory.CreateDirectory(o.OutDir);
        _catalog = ShopCatalog.TryLoad(o.ArtRoot)
            ?? throw new InvalidOperationException($"cannot load PCore001 from '{o.ArtRoot}'");
        _shipRenderer = new ShipRenderer(GraphicsDevice);
    }

    private XnaModel LoadModel(string asset)
    {
        if (!_models.TryGetValue(asset, out var m))
        {
            m = Content.Load<XnaModel>("Models/" + asset);
            // P520 — fold the per-asset orientation override in once at load so
            // every renderer (dump / turnaround / wire thumbnails) sees the
            // corrected orientation, matching the in-game ShipOrientationApply.
            ShipOrientationApply.Apply(m, asset);
            _models[asset] = m;
        }
        return m;
    }

    /// <summary>Load a model WITHOUT applying any orientation override — the raw
    /// shipped geometry, used by <c>orient</c> so the audit measures and renders
    /// the un-modified mesh (the P520 before-state). Kept in a separate cache so
    /// it never inherits the override of <see cref="LoadModel"/>.</summary>
    private readonly Dictionary<string, XnaModel> _rawModels = new(StringComparer.Ordinal);
    private XnaModel LoadRawModel(string asset)
    {
        if (!_rawModels.TryGetValue(asset, out var m))
        {
            m = Content.Load<XnaModel>("Models/" + asset);
            _rawModels[asset] = m;
        }
        return m;
    }

    // P337 — the real Meshy baseColor for an asset, loaded once and bound into
    // ShipRenderer.Draw so the reference sheets show real faction livery.
    private Texture2D LoadTexture(string asset)
    {
        if (!_textures.TryGetValue(asset, out var t))
        {
            t = Content.Load<Texture2D>("Textures/ships/" + asset);
            _textures[asset] = t;
        }
        return t;
    }

    protected override void Initialize()
    {
        base.Initialize();
        _batch = new SpriteBatch(GraphicsDevice);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });
        _font = Content.Load<SpriteFont>("HUD");
        _fontBig = Content.Load<SpriteFont>("HUDLarge");
        var gd = GraphicsDevice;

        if (_o.Mode == "wire")
        {
            RunWire(gd);
        }
        else if (_o.Mode == "turnaround")
        {
            RunTurnaround(gd);
        }
        else if (_o.Mode == "stride")
        {
            RunStride(gd);
        }
        else if (_o.Mode == "orient")
        {
            RunOrient(gd);
        }
        else if (_o.Mode == "hullpreview")
        {
            RunHullPreview(gd);
        }
        else if (_o.Mode == "spin")
        {
            RunSpinTest(gd);
        }
        else
        {
            RunDump(gd);
        }
        Exit();
    }

    // ── stride diagnostic mode (P356) ────────────────────────────────────
    //
    // Enumerate every compiled ship model under Content/Models and report its
    // vertex-buffer declaration (stride + element usages) per mesh part. Lets us
    // confirm whether a hull carries normal data into the vertex shader without
    // needing a live roster.

    private void RunStride(GraphicsDevice gd)
    {
        string modelsDir = Path.Combine(Content.RootDirectory, "Models");
        var files = Directory.GetFiles(modelsDir, "*.xnb")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Where(n => !n.StartsWith("mis") && !n.StartsWith("artifact") &&
                        !n.StartsWith("rrocket") && !n.StartsWith("tacrock") &&
                        !n.StartsWith("drgrock") && !n.StartsWith("bios_scout"))
            .OrderBy(n => n)
            .ToList();
        Console.WriteLine($"[roster-capture] stride census: {files.Count} ship models in {modelsDir}");
        foreach (var name in files)
        {
            XnaModel m;
            try { m = Content.Load<XnaModel>("Models/" + name); }
            catch (Exception ex) { Console.WriteLine($"   {name,-24} LOAD FAIL: {ex.Message}"); continue; }
            var stride = -1;
            string decl = "?";
            foreach (var mesh in m.Meshes)
            foreach (var part in mesh.MeshParts)
            {
                var vd = part.VertexBuffer.VertexDeclaration;
                stride = vd.VertexStride;
                decl = string.Join("+",
                    vd.GetVertexElements().Select(e =>
                        $"{e.VertexElementUsage}{e.UsageIndex}:{e.VertexElementFormat}"));
                break;
            }
            Console.WriteLine($"   {name,-24} stride={stride,-3}  {decl}");
        }
    }

    // ── offline dump mode ────────────────────────────────────────────────

    private void RunDump(GraphicsDevice gd)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_o.DumpPath));
        var ships = doc.RootElement.EnumerateArray().Select(x => new Ship(x)).ToList();
        Console.WriteLine($"[roster-capture] loaded {ships.Count} roster entries from {_o.DumpPath}");

        foreach (var s in ships) LoadModel(s.Asset);

        var stats = new List<(string Asset, int Tris, float LocalRadius, float RenderRadius)>();
        foreach (var s in ships.DistinctBy(x => x.Asset))
        {
            var m = LoadModel(s.Asset);
            int tris = 0;
            foreach (var mesh in m.Meshes)
                foreach (var part in mesh.MeshParts)
                    tris += part.PrimitiveCount;
            stats.Add((s.Asset, tris, MergedSphere(m).Radius, RenderRadius(m)));
        }
        Console.WriteLine($"[roster-capture] loaded {stats.Count} distinct content models");

        RenderCombined(gd, ships);
        foreach (var civ in ships.Select(s => s.Civ).Distinct())
            RenderFaction(gd, ships, civ);
        WriteLegend(ships);
        DumpVariety(stats);
    }

    // ── offline turnaround mode (P348) ──────────────────────────────────
    //
    // One clean, isolated, consistently-framed 4-angle reference set per hull
    // (front / right / back / left — camera orbited around the ship's vertical
    // axis at a fixed low elevation) for Meshy-style multi-image-to-3D input.
    // Same rendering path as dump/wire (HullModelResolver + ShipRenderer.Draw),
    // but a plain solid neutral background (no stars/grid/HUD) so an image
    // generator never reads scene clutter as part of the subject.

    private void RunTurnaround(GraphicsDevice gd)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(_o.DumpPath));
        var ships = doc.RootElement.EnumerateArray().Select(x => new Ship(x))
                       .DistinctBy(x => x.Asset).ToList();
        Console.WriteLine($"[roster-capture] turnaround: {ships.Count} distinct hulls from {_o.DumpPath}");

        foreach (var s in ships) LoadModel(s.Asset);

        string[] views = { "front", "right", "back", "left" };
        var entries = new List<TurnaroundEntry>(ships.Count);

        foreach (var s in ships)
        {
            var m = LoadModel(s.Asset);
            // Centre the hull on the turnaround origin (in the same bone-restored
            // + nose-aligned space the render applies) and uniformly scale it so
            // its TRUE rendered radius maps to a fixed target — every hull, and
            // every one of its 4 views, frames identically.
            float rr = ShipRenderScale.RenderRadius(m);
            float target = 930f;
            float scale = rr > 0f ? target / rr : 1f;
            var center = Vector3.Transform(RenderSphereCenter(m), ShipRenderer.SharedNoseAlignment);
            var worldBase = Matrix.CreateTranslation(-center) * Matrix.CreateScale(scale);

            string safe = s.Asset.Replace('/', '_');
            string assetDir = Path.Combine(_o.OutDir, safe);
            Directory.CreateDirectory(assetDir);
            var files = new string[views.Length];

            for (int az = 0; az < views.Length; az++)
            {
                var cam = TurnaroundCam(target, az);
                using var rt = new RenderTarget2D(gd, cam.Px, cam.Py, false,
                    SurfaceFormat.Color, DepthFormat.Depth24);
                gd.SetRenderTarget(rt);
                gd.Clear(BgColor);
                gd.DepthStencilState = DepthStencilState.Default;
                _shipRenderer.Draw(m, worldBase, cam.View, cam.Proj, texture: LoadTexture(s.Asset));
                gd.DepthStencilState = DepthStencilState.None;
                gd.SetRenderTarget(null);

                string outPath = Path.Combine(assetDir, safe + "_" + views[az] + ".png");
                using (var fs = File.Create(outPath)) rt.SaveAsPng(fs, rt.Width, rt.Height);
                files[az] = outPath;
            }

            entries.Add(new TurnaroundEntry(s.Asset, s.Civ, s.CivClass,
                files[0], files[1], files[2], files[3]));
            Console.WriteLine($"[roster-capture] turnaround {s.Asset,-22} rr={rr,6:F1}u");
        }

        WriteManifest(entries);
        Console.WriteLine($"[roster-capture] turnaround complete: {entries.Count} hulls x {views.Length} views = {entries.Count * views.Length} images in {_o.OutDir}");
    }

    private sealed record TurnaroundEntry(string Asset, string Civ, string CivClass,
        string Front, string Right, string Back, string Left);

    private void WriteManifest(List<TurnaroundEntry> entries)
    {
        var rows = entries.Select(e => new
        {
            e.Asset, e.Civ, e.CivClass,
            front = e.Front, right = e.Right, back = e.Back, left = e.Left,
        });
        string json = JsonSerializer.Serialize(rows,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_o.OutDir, "turnaround_manifest.json"), json);
        Console.WriteLine($"[roster-capture] wrote turnaround_manifest.json ({entries.Count} hulls)");
    }

    private void RenderCombined(GraphicsDevice gd, List<Ship> ships)
    {
        float maxRows = ships.Max(s => (float)s.Row) + 1f;
        var cam = Camera(ships, maxRows, eleY: 0.9f, backZ: 1.4f);
        using var rt = new RenderTarget2D(gd, cam.Px, cam.Py, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.012f, 0.016f, 0.030f));
        DrawStars(gd, cam);
        gd.DepthStencilState = DepthStencilState.Default;
        foreach (var s in ships)
        {
            var m = LoadModel(s.Asset);
            float scale = (TileRadius * 0.55f) / RenderRadius(m);
            var p = TilePos(s, maxRows);
            _shipRenderer.Draw(m, Matrix.CreateScale(scale) * Matrix.CreateRotationY(-0.7f)
                                 * Matrix.CreateTranslation(p.X, 0f, p.Y), cam.View, cam.Proj,
                                 texture: LoadTexture(s.Asset));
        }
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        DrawGrid(gd, cam, maxRows, W: cam.Px, H: cam.Py);
        foreach (var g in ships.GroupBy(s => s.Civ))
        {
            var s0 = g.First();
            var p = TilePos(s0, maxRows);
            var scr = Project(cam, new Vector3(p.X, 0f, p.Y));
            string label = $"{s0.Civ.ToUpperInvariant()}  ({g.Count()})";
            var sz = _fontBig.MeasureString(label);
            _batch.DrawString(_fontBig, label, new Vector2(scr.X - sz.X / 2f, scr.Y - TileRadius / cam.ViewH * cam.Py - sz.Y - 6f), Color.LightSkyBlue);
        }
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg("roster_all.jpg", rt);
        Console.WriteLine($"[roster-capture] wrote roster_all.jpg ({ships.Count} ships, {cam.Px}x{cam.Py})");
    }

    private void RenderFaction(GraphicsDevice gd, List<Ship> ships, string civ)
    {
        var f = ships.Where(s => s.Civ == civ).ToList();
        int cols = (int)Math.Ceiling(f.Count / 5.0);
        int rows = (int)Math.Ceiling(f.Count / (double)cols);
        var loc = f.Select((s, i) => (s, c: i % cols, r: i / cols)).ToList();
        float maxR = rows - 1f, maxC = cols - 1f;
        var cam = SheetCamera(cols, rows, maxC, maxR);
        using var rt = new RenderTarget2D(gd, cam.Px, cam.Py, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.011f, 0.017f, 0.033f));
        DrawStars(gd, cam);
        gd.DepthStencilState = DepthStencilState.Default;
        foreach (var (s, c, r) in loc)
        {
            var m = LoadModel(s.Asset);
            float scale = (TileRadius * 0.6f) / RenderRadius(m);
            float x = (c - maxC / 2f) * ColSpacing;
            float z = (r - maxR / 2f) * RowSpacing;
            _shipRenderer.Draw(m, Matrix.CreateScale(scale) * Matrix.CreateRotationY(-0.7f)
                                 * Matrix.CreateTranslation(x, 0f, z), cam.View, cam.Proj,
                                 texture: LoadTexture(s.Asset));
        }
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        string title = $"{civ.ToUpperInvariant()}  ({f.Count} models)";
        var tsz = _fontBig.MeasureString(title);
        _batch.DrawString(_fontBig, title, new Vector2(cam.Px / 2f - tsz.X / 2f, 12f), Color.White);
        foreach (var (s, c, r) in loc)
        {
            float x = (c - maxC / 2f) * ColSpacing;
            float z = (r - maxR / 2f) * RowSpacing;
            var scr = Project(cam, new Vector3(x, 0f, z));
            var sz = _font.MeasureString(s.Asset);
            float dy = TileRadius / cam.ViewH * cam.Py;
            _batch.DrawString(_font, s.Asset, new Vector2(scr.X - sz.X / 2f, scr.Y + dy + 4f), Color.LightGreen);
        }
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg($"roster_{civ}.jpg", rt);
        Console.WriteLine($"[roster-capture] wrote roster_{civ}.jpg ({f.Count} ships, {cam.Px}x{cam.Py})");
    }

    private void WriteLegend(List<Ship> ships)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# P305 full ship roster");
        sb.AppendLine();
        sb.AppendLine($"{ships.Count} distinct real ship models across {ships.Select(s => s.Civ).Distinct().Count()} factions, rendered with the real HullModelResolver + ShipRenderer + P287 content models. Ships launched in-game via the real SendBuyLoadout wire path.");
        sb.AppendLine();
        sb.AppendLine("| asset | civ | class | hull id | model | display name |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var s in ships)
            sb.AppendLine($"| {s.Asset} | {s.Civ} | {s.CivClass} | {s.HullId} | {s.ModelName} | {s.DisplayName} |");
        File.WriteAllText(Path.Combine(_o.OutDir, "roster_legend.md"), sb.ToString());
        Console.WriteLine($"[roster-capture] wrote roster_legend.md ({ships.Count} rows)");
    }

    private void DumpVariety(List<(string Asset, int Tris, float LocalRadius, float RenderRadius)> stats)
    {
        var sizes = new List<(string Asset, long Bytes, int Tris, float RR)>();
        // Per-asset single renders are a reproducibility aid (the objective
        // "distinct byte sizes / distinct silhouettes" variety proof) but were
        // /tmp intermediates in P305, not a docs deliverable — keep them out of
        // the committed reference-sheet directory (docs/P305_roster tracks only
        // roster_variety.txt below).
        string singlesDir = Path.Combine(Path.GetTempPath(), "roster-capture-variety");
        Directory.CreateDirectory(singlesDir);
        foreach (var (asset, tris, lr, rr) in stats)
        {
            var m = LoadModel(asset);
            float dist = rr * 3.2f;
            var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), 640f / 460f, 0.1f, 400f);
            var eye = new Vector3(0f, rr * 0.5f, dist);
            var view = Matrix.CreateLookAt(eye, Vector3.Zero, Vector3.Up);
            using var rt = new RenderTarget2D(GraphicsDevice, 800, 570, false, SurfaceFormat.Color, DepthFormat.Depth24);
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(new Color(0.018f, 0.027f, 0.045f));
            _shipRenderer.Draw(m, Matrix.CreateRotationY(-0.7f) * Matrix.CreateRotationX(-0.15f), view, proj,
                               texture: LoadTexture(asset));
            GraphicsDevice.SetRenderTarget(null);
            string name = asset.Replace('/', '_') + ".jpg";
            using (var fs = File.Create(Path.Combine(singlesDir, name))) rt.SaveAsJpeg(fs, rt.Width, rt.Height);
            sizes.Add((asset, new FileInfo(Path.Combine(singlesDir, name)).Length, tris, rr));
        }
        Console.WriteLine("[roster-capture] per-asset single-render JPEG byte sizes:");
        foreach (var (a, b, t, rr) in sizes.OrderBy(x => x.Item1))
            Console.WriteLine($"   {a,-22} {b,8} B   tris={t,-6} renderRadius={rr:F3}");
        int distinctBytes = sizes.Select(s => s.Bytes).Distinct().Count();
        int distinctTris = sizes.Select(s => s.Tris).Distinct().Count();
        Console.WriteLine($"[roster-capture] distinct JPEG sizes={distinctBytes}/{sizes.Count}, distinct tri counts={distinctTris}/{sizes.Count}");
        if (distinctTris < 5)
            throw new InvalidOperationException("models rendered with <5 distinct tri counts — no real per-ship variety!");
        using (var w = new StreamWriter(Path.Combine(_o.OutDir, "roster_variety.txt")))
        {
            w.WriteLine("# asset  tris  localRadius(unit-normalized)  renderRadius(true, incl. ParentBone 100x scale)");
            foreach (var (a, t, lr, rr) in stats.OrderBy(x => x.Asset))
                w.WriteLine($"{a,-22} tris={t,-6} localR={lr:F3} renderR={rr:F1}");
        }
        Console.WriteLine($"[roster-capture] wrote roster_variety.txt (+ per-asset singles in {singlesDir})");
    }

    // ── orient mode (P520) ──────────────────────────────────────────────
    //
    // Measures the raw bounding-box extents of every shipped ship model (the
    // bone-restored render frame the renderer actually draws) and writes them to
    // orient_extents.json; prints the long-axis audit; and renders a fixed top-
    // view contact sheet of the whole fleet BEFORE (no per-asset override) and
    // AFTER (override applied) so a human can confirm nose direction — bounding
    // boxes can't tell nose from tail or settle near-square hulls.
    private void RunOrient(GraphicsDevice gd)
    {
        var assets = HullModelResolver.ShipAssets
            .OrderBy(a => a, StringComparer.Ordinal).ToList();

        // 1) measure raw extents (before applying any override so the audit
        //    reflects the shipped geometry).
        var entries = new List<ModelOrientationOverrides.Entry>();
        foreach (var asset in assets)
        {
            var m = LoadRawModel(asset);
            var (x, y, z) = RawExtents(m);
            int axis = ModelOrientationOverrides.LongAxisOf(x, y, z);
            bool def = ModelOrientationOverrides.IsDefinitive(x, y, z);
            var status = ModelOrientationOverrides.EntryFrom(asset, x, y, z);
            entries.Add(status);
            Console.WriteLine($"   {asset,-24} x={x,7:F3} y={y,7:F3} z={z,7:F3}  axis={(axis == 0 ? "X" : axis == 1 ? "Y" : "Z")}{(def ? "" : "  (ambiguous)")}  {status.Status}");
        }

        // 2) write the measured extents as JSON (consumed to seed
        //    ModelOrientationOverrides.Extents).
        var jsonRows = new List<string>();
        foreach (var e in entries.OrderBy(e => e.Asset, StringComparer.Ordinal))
            jsonRows.Add($"    {{\"asset\":\"{e.Asset}\",\"x\":{e.X:R},\"y\":{e.Y:R},\"z\":{e.Z:R},\"axis\":\"{(e.Axis == 0 ? "X" : e.Axis == 1 ? "Y" : "Z")}\"}}");
        var json = "{\n  \"note\": \"P520 raw render-frame bounding-box extents of each shipped Meshy ship model, captured from the compiled FBX content by roster-capture orient. Long axis should be X (forward).\",\n  \"assets\": [\n"
                   + string.Join(",\n", jsonRows) + "\n  ]\n}\n";
        File.WriteAllText(Path.Combine(_o.OutDir, "orient_extents.json"), json);

        // 3) BEFORE phase — whole-fleet contact sheet + overridden-asset
        //    close-ups, all from RAW geometry (global correction only).
        RenderOrientSheet(gd, assets, "orient_before.jpg", "BEFORE - global correction only (per-asset override OFF)");
        foreach (var asset in ModelOrientationOverrides.OverriddenAssets.OrderBy(a => a, StringComparer.Ordinal))
            RenderOrientSingle(gd, asset, applyOverride: false, $"orient_flag_{asset}_before.jpg");

        // 4) apply the per-asset overrides ONCE to the raw models (after all
        //    before-state renders, so before/after stay clean).
        foreach (var asset in ModelOrientationOverrides.OverriddenAssets)
            ShipOrientationApply.Apply(LoadRawModel(asset), asset);

        // 5) AFTER phase — same views with overrides folded in.
        RenderOrientSheet(gd, assets, "orient_after.jpg", "AFTER - per-asset override applied");
        foreach (var asset in ModelOrientationOverrides.OverriddenAssets.OrderBy(a => a, StringComparer.Ordinal))
            RenderOrientSingle(gd, asset, applyOverride: true, $"orient_flag_{asset}_after.jpg");

        // 6) side-by-side: each fixed asset vs a known-correct X-long class
        //    sibling, both AFTER the override — the unambiguous nose-direction
        //    proof (a yawed mesh sits crosswise to its sibling before, aligned
        //    after). Reference siblings are same-family X-long ships.
        RenderOrientCompare(gd, "ic-dev", "ic-devastator");
        RenderOrientCompare(gd, "gt-dev", "ic-devastator");
        RenderOrientCompare(gd, "rix-con", "rix-carrier");
        RenderOrientCompare(gd, "rix-miner", "rix-carrier");

        Console.WriteLine("[roster-capture] orient done — wrote orient_extents.json, orient_before.jpg, orient_after.jpg (+ flagged close-ups)");
    }

    /// <summary>Raw render-frame axis-aligned bounding-box extents of a model:
    /// every vertex position transformed by its mesh's ParentBone.Transform,
    /// then min/max per axis. Uniform 100× bone scale ⇒ magnitudes are real hull
    /// units; the LONG-AXIS direction is what the orientation audit keys on.</summary>
    private static (float X, float Y, float Z) RawExtents(XnaModel m)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var mesh in m.Meshes)
        {
            var T = mesh.ParentBone.Transform;
            foreach (var part in mesh.MeshParts)
                AccumulateBounds(part, T, ref min, ref max);
        }
        return (max.X - min.X, max.Y - min.Y, max.Z - min.Z);
    }

    private static void AccumulateBounds(ModelMeshPart part, Matrix T, ref Vector3 min, ref Vector3 max)
    {
        var decl = part.VertexBuffer.VertexDeclaration;
        int offset = -1;
        foreach (var el in decl.GetVertexElements())
            if (el.VertexElementUsage == VertexElementUsage.Position && el.UsageIndex == 0)
            { offset = el.Offset; break; }
        if (offset < 0) return;
        int stride = decl.VertexStride;
        int n = part.VertexBuffer.VertexCount;
        var data = new byte[n * stride];
        part.VertexBuffer.GetData(data);
        for (int i = 0; i < n; i++)
        {
            int b = i * stride + offset;
            var p = Vector3.Transform(new Vector3(
                BitConverter.ToSingle(data, b),
                BitConverter.ToSingle(data, b + 4),
                BitConverter.ToSingle(data, b + 8)), T);
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
        }
    }

    /// <summary>Fixed top-view contact sheet of every asset (optionally with the
    /// per-asset override folded in), each ship upright so its raw long axis is
    /// legible — a yawed model (e.g. ic-dev) visibly reads crosswise to its
    /// X-forward siblings.</summary>
    private void RenderOrientSheet(GraphicsDevice gd, List<string> assets, string fileName, string title)
    {
        int n = assets.Count;
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling(n / (double)cols);
        const float gap = 190f, tileR = 62f;
        float maxC = cols - 1f, maxR = rows - 1f;

        var center = Vector3.Zero;
        float viewW = (maxC * gap + tileR * 2.6f);
        float viewH = (maxR * gap + tileR * 2.6f);
        var eye = center + new Vector3(0f, Math.Max(viewW, viewH) * 3f, 0f);
        var view = Matrix.CreateLookAt(eye, center, Vector3.Backward);
        var proj = Matrix.CreateOrthographic(viewW, viewH, 1f, 60000f);
        int W = (int)Math.Ceiling(viewW * 1.5f), H = (int)Math.Ceiling(viewH * 1.5f);

        using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.012f, 0.022f, 0.040f));
        gd.DepthStencilState = DepthStencilState.Default;
        int i = 0;
        foreach (var asset in assets)
        {
            var m = LoadRawModel(asset);
            float scale = (tileR * 0.52f) / RenderRadius(m);
            int c = i % cols, r = i / cols;
            float x = (c - maxC / 2f) * gap, z = (r - maxR / 2f) * gap;
            _shipRenderer.Draw(m, Matrix.CreateScale(scale) * Matrix.CreateTranslation(x, 0f, z),
                               view, proj, texture: LoadTexture(asset));
            i++;
        }
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        var tsz = _fontBig.MeasureString(title);
        _batch.DrawString(_fontBig, title, new Vector2(W / 2f - tsz.X / 2f, 8f), Color.Orange);
        i = 0;
        float ppu = (float)W / viewW;
        foreach (var asset in assets)
        {
            int c = i % cols, r = i / cols;
            float x = (c - maxC / 2f) * gap, z = (r - maxR / 2f) * gap;
            float sx = (x / viewW + 0.5f) * W;
            float sy = (z / viewH + 0.5f) * H;   // +z is downward (up = -Z)
            var sz = _font.MeasureString(asset);
            _batch.DrawString(_font, asset, new Vector2(sx - sz.X / 2f, sy + (tileR * ppu) + 4f), Color.LightGreen);
            i++;
        }
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg(fileName, rt);
        Console.WriteLine($"[roster-capture] wrote {fileName} ({n} ships, {W}x{H})");
    }

    /// <summary>A single ship, centred, top view, larger — for a legible
    /// before/after of a flagged hull's nose direction.</summary>
    private void RenderOrientSingle(GraphicsDevice gd, string asset, bool applyOverride, string fileName)
    {
        var m = LoadRawModel(asset);
        float radius = RenderRadius(m);
        float half = radius * 1.5f;      // ship (~radius) fits with margin
        var view = Matrix.CreateLookAt(new Vector3(0f, half * 4f, 0f), Vector3.Zero, Vector3.Backward);
        var proj = Matrix.CreateOrthographic(half * 2f, half * 2f, 1f, 60000f);
        const int S = 920;
        using var rt = new RenderTarget2D(gd, S, S, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.050f, 0.075f, 0.085f));
        gd.DepthStencilState = DepthStencilState.Default;
        _shipRenderer.Draw(m, Matrix.Identity, view, proj, texture: LoadTexture(asset));
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        string label = $"{asset}  {(applyOverride ? "AFTER (override)" : "BEFORE (global only)")}";
        var sz = _fontBig.MeasureString(label);
        _batch.DrawString(_fontBig, label, new Vector2(S / 2f - sz.X / 2f, 12f), Color.Orange);
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg(fileName, rt);
        Console.WriteLine($"[roster-capture] wrote {fileName} ({asset}, {S}x{S})");
    }

    /// <summary>Side-by-side top-view of a fixed asset (left) against a
    /// known-correct X-long class sibling (right), both AFTER the override, at
    /// the same apparent scale — the unambiguous proof that the corrected
    /// mesh's long axis agrees with its siblings (and is crosswise to them
    /// without the override).</summary>
    private void RenderOrientCompare(GraphicsDevice gd, string subject, string reference)
    {
        var ms = LoadRawModel(subject);
        var mr = LoadRawModel(reference);
        float radius = Math.Max(RenderRadius(ms), RenderRadius(mr));
        float half = radius * 1.7f;
        var view = Matrix.CreateLookAt(new Vector3(0f, half * 4f, 0f), Vector3.Zero, Vector3.Backward);
        var proj = Matrix.CreateOrthographic(half * 2f, half * 2f, 1f, 60000f);
        const int PH = 870;
        using var rt = new RenderTarget2D(gd, PH * 2, PH, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.060f, 0.080f, 0.092f));
        gd.DepthStencilState = DepthStencilState.Default;
        float cx = half * 0.5f;
        _shipRenderer.Draw(ms, Matrix.CreateTranslation(-cx, 0f, 0f), view, proj, texture: LoadTexture(subject));
        _shipRenderer.Draw(mr, Matrix.CreateTranslation(cx, 0f, 0f), view, proj, texture: LoadTexture(reference));
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        string l = $"{subject}  (fixed)   vs   {reference}  (correct)   both AFTER override";
        var tsz = _fontBig.MeasureString(l);
        _batch.DrawString(_fontBig, l, new Vector2(PH - tsz.X / 2f, 12f), Color.Orange);
        foreach (var (name, side) in new[] { (subject, -1), (reference, 1) })
        {
            var sz = _font.MeasureString(name);
            float x = PH + side * PH / 2f - sz.X / 2f;
            _batch.DrawString(_font, name, new Vector2(x, PH - 34f), Color.LightGreen);
        }
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg($"orient_compare_{subject}_vs_{reference}.jpg", rt);
        Console.WriteLine($"[roster-capture] wrote orient_compare_{subject}_vs_{reference}.jpg");
    }

    // ── hullpreview mode (P525) ─────────────────────────────────────────
    //
    // Renders the docked hull preview's framing (Game1.Rendering.cs:
    // DrawDockedHullPreview, eye at z=2.4, 50° FOV) for a set of hulls in
    // BEFORE (old 1/HullLength scale — small hulls draw huge, camera inside)
    // and AFTER (native 2.4×radius normalisation, DockedHullPreviewFraming)
    // so a human can see the fix. Specs are "asset:length" pairs separated by
    // commas; length is the real HullType.Length (from PCore001) used to
    // reproduce the buggy 1/length scale faithfully.

    private void RunHullPreview(GraphicsDevice gd)
    {
        var specs = _o.HullSpecs.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (specs.Length == 0)
            throw new InvalidOperationException("hullpreview needs --hulls 'asset:length,asset:length,...'");
        foreach (var spec in specs)
        {
            var bits = spec.Split(':');
            if (bits.Length != 2) throw new InvalidOperationException($"bad hull spec '{spec}' (want asset:length)");
            string asset = bits[0];
            float length = float.Parse(bits[1], CultureInfo.InvariantCulture);
            RenderHullPreviewPair(gd, asset, length);
        }
        Console.WriteLine("[roster-capture] hullpreview done");
    }

    private void RenderHullPreviewPair(GraphicsDevice gd, string asset, float length)
    {
        var m = LoadModel(asset);
        float rr = RenderRadius(m);
        // Fixed preview-style camera (Game1.Rendering.cs:2229-2236): eye at
        // z=2.4, FOV 50 — identical for before and after so the
        // only difference is the draw scale (i.e. the framing fix).
        var world = Matrix.CreateRotationY(-0.7f) * Matrix.CreateRotationX(-0.08f);
        var eye = new Vector3(0f, 0f, DockedHullPreviewFraming.EyeDistance(1f));
        var view = Matrix.CreateLookAt(eye, Vector3.Zero, Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(DockedHullPreviewFraming.FovRadians, 16f / 9f, 0.1f, 40f);
        var tex = LoadTexture(asset);
        const int W = 1024, H = 576;

        // BEFORE — the P519 bug: scale by 1/HullLength. A fighter (len ~13,
        // render radius ~100) draws at ~7.7-unit radius with the eye at 2.4 →
        // camera INSIDE the hull; a carrier (len 300) draws at ~0.3 → tinier
        // than it should be (both are wrong, opposite ways).
        float beforeScale = length > 0f ? 1f / length : 1f;
        float beforeR = beforeScale * rr;
        bool beforeInside = beforeR > DockedHullPreviewFraming.EyeDistance(1f);
        RenderPreviewFrame(gd, m, tex, world, view, proj, beforeScale,
            $"{asset}  BEFORE (scale=1/length={length:F2})  drawnR={beforeR:F2}  eye=2.4 -> {(beforeInside ? "INSIDE (bug)" : "TOO SMALL")}",
            $"p525_{asset}_before.jpg", W, H);

        // AFTER — native 2.4×radius framing: normalise to radius 1, eye sits
        // just outside, whole hull in the 50° frustum.
        float afterScale = DockedHullPreviewFraming.NormaliseScale(rr);
        RenderPreviewFrame(gd, m, tex, world, view, proj, afterScale,
            $"{asset}  AFTER (scale=1/renderRadius={rr:F1})  drawnR=1.0  eye=2.4 -> OUTSIDE",
            $"p525_{asset}_after.jpg", W, H);

        Console.WriteLine($"[roster-capture] hullpreview {asset}: renderRadius={rr:F2} length={length:F2} " +
            $"beforeScale={beforeScale:F4} (drawnR={beforeScale * rr:F2})  afterScale={afterScale:F4} (drawnR=1.0)");
    }

    private void RenderPreviewFrame(GraphicsDevice gd, XnaModel m, Texture2D tex,
        Matrix world, Matrix view, Matrix proj, float scale, string label, string fileName, int W, int H)
    {
        using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        // The fix clears target+depth and re-enables opaque depth (the P339
        // SpriteBatch state leak): give both before and after the same state so
        // the comparison isolates the FRAMING change, not the hole artifact.
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        _shipRenderer.Draw(m, world, view, proj, 1f, scale, tex);
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        var tsz = _font.MeasureString(label);
        _batch.DrawString(_font, label, new Vector2(W / 2f - tsz.X / 2f, 12f), Color.Orange);
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg(fileName, rt);
        Console.WriteLine($"[roster-capture] wrote {fileName} ({W}x{H})");
    }

    // ── spin mode (P526) ─────────────────────────────────────────────────
    //
    // Renders the docked hull preview's NATIVE spin motion (load.cpp:877-887:
    // level spin about Y at 1 rad/s, fixed π/8 X tilt applied after the spin)
    // as a 2x2 contact sheet over a fixed time series t = 0, π/2, π, 3π/2 s of
    // ONE hull, through the exact new preview transform (DockedHullOrbit.World)
    // and eye (0, 0, 2.4, FOV 50) — P526's headless visual proof that the ship
    // level-spins about its vertical axis at a constant tilt. Spec: --hulls
    // 'asset'.

    private void RunSpinTest(GraphicsDevice gd)
    {
        string asset = _o.HullSpecs.Split(',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault()
            ?? throw new InvalidOperationException("spin needs --hulls 'asset'");
        var m = LoadModel(asset);
        float rr = RenderRadius(m);
        float scale = DockedHullPreviewFraming.NormaliseScale(rr);

        const int CW = 544, CH = 304;               // per-cell
        const int TitleH = 56;                      // top title band
        const int SheetW = CW * 2, SheetH = TitleH + CH * 2;
        using var rt = new RenderTarget2D(gd, SheetW, SheetH, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        // Clear target+depth ONCE here; per-cell we clear DEPTH only — MonoGame's
        // Clear ignores the viewport and wipes the WHOLE target, so clearing the
        // target per cell would erase the previous cells' ships.
        gd.Clear(new Color(0.015f, 0.022f, 0.040f));
        gd.Viewport = new Viewport(0, 0, SheetW, SheetH);

        // Title band.
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        string title = $"P526 native loadout spin - {asset}   (1 rad/s about Y, fixed {DockedHullOrbit.TiltRadians * 180.0 / Math.PI:F1} deg X tilt, eye z=2.4)";
        var tsz = _fontBig.MeasureString(title);
        _batch.DrawString(_fontBig, title, new Vector2(SheetW / 2f - tsz.X / 2f, 12f), Color.Orange);
        _batch.End();

        var tex = LoadTexture(asset);
        double[] times = { 0.0, Math.PI / 2.0, Math.PI, 3.0 * Math.PI / 2.0 };
        for (int i = 0; i < times.Length; i++)
        {
            int col = i % 2, row = i / 2;
            int x0 = col * CW, y0 = TitleH + row * CH;
            gd.Viewport = new Viewport(x0, y0, CW, CH);
            gd.Clear(ClearOptions.DepthBuffer, Color.White, 1f, 0);   // depth only, per cell
            gd.BlendState = BlendState.Opaque;
            gd.DepthStencilState = DepthStencilState.Default;

            double spin = times[i] * DockedHullOrbit.SpinRate;   // angle = t × 1.0 rad
            var world = DockedHullOrbit.World((float)spin);
            var eye = new Vector3(0f, 0f, DockedHullPreviewFraming.EyeDistance(1f));
            var view = Matrix.CreateLookAt(eye, Vector3.Zero, Vector3.Up);
            var proj = Matrix.CreatePerspectiveFieldOfView(
                DockedHullPreviewFraming.FovRadians, (float)CW / CH, 0.1f, 40f);
            _shipRenderer.Draw(m, world, view, proj, 1f, scale, tex);

            gd.DepthStencilState = DepthStencilState.None;
            _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            string label = $"t = {times[i]:F3} s   spin = {spin:F3} rad ({spin * 180.0 / Math.PI:F0} deg)";
            var sz = _font.MeasureString(label);
            _batch.DrawString(_font, label,
                new Vector2(x0 + CW / 2f - sz.X / 2f, y0 + CH - sz.Y - 6f), Color.Orange);
            _batch.End();
        }

        gd.SetRenderTarget(null);
        gd.Viewport = new Viewport(0, 0,
            gd.PresentationParameters.BackBufferWidth, gd.PresentationParameters.BackBufferHeight);
        SaveJpeg($"p526_spin_{asset}.jpg", rt);
        Console.WriteLine($"[roster-capture] spin {asset}: renderRadius={rr:F2} scale={scale:F4} — wrote p526_spin_{asset}.jpg ({SheetW}x{SheetH})");
    }

    // ── networked wire mode ──────────────────────────────────────────────

    private static LogonRequest WireRequest(string name) => new()
    {
        CharacterName = name,
        ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 1234u,
        DwCookie = 0x33Fu,
        CrcFileList = -1,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 70000000000000000ul,
        DrmHash = "monogame",
    };

    private void RunWire(GraphicsDevice gd)
    {
        var session = new ConnectionSession();
        session.Connect(_o.Port, "roster-capture", "127.0.0.1");
        session.SendLogonRequest(WireRequest("roster-capture"));
        var ack = session.WaitForAck(5000);
        Console.WriteLine($"[roster-capture] logged on: serverTime={ack.TimeServer}");

        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline && session.World.SideRoster is null)
            Thread.Sleep(20);
        if (session.World.SideRoster is not { } roster)
            throw new InvalidOperationException("no SIDE_ROSTER received");
        Console.WriteLine($"[roster-capture] SIDE_ROSTER: {roster.Sides.Length} sides, civIds [{string.Join(",", roster.Sides.Select(s => s.CivId))}]");
        session.SendPositionReq(_o.Side);
        Console.WriteLine($"[roster-capture] requested side {_o.Side} (POSITIONREQ)");

        deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && session.World.MyShipId is null)
            Thread.Sleep(20);
        Console.WriteLine($"[roster-capture] seated: MyShipId={session.World.MyShipId} MySideId={session.World.MySideId}");

        Dictionary<string, string> seen = new();
        deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            var world = session.World;
            // P337 — snapshot the world ship table under the session gate so the
            // receive thread's concurrent upsert can't throw "collection modified"
            // mid-enumeration (the P319/P306 unlocked-gather discipline; Game1
            // wraps the same gather in lock(_session.Gate)). Process outside.
            List<KeyValuePair<short, Allegiance.ClientCore.State.ShipEntity>> snap;
            lock (session.Gate)
                snap = world.Ships.ToList();
            foreach (var (id, ship) in snap)
            {
                if (ship.IsMine || ship.SideId != _o.Side || ship.HullId <= 0) continue;
                var civKey = CivKeyForShipSide(world, ship.SideId);
                if (civKey is null) continue;
                var asset = _resolver.ResolveAsset(civKey, ship.HullId, _catalog);
                if (asset is not null) seen[asset] = $"{ship.ShipId}:{ship.HullId}";
            }
            if (seen.Count >= 13) break;
            Thread.Sleep(30);
        }
        if (seen.Count == 0)
            throw new InvalidOperationException("received no same-side teammate ships over the wire");
        Console.WriteLine($"[roster-capture] received {seen.Count} distinct ships over the wire (side {_o.Side}):");
        foreach (var kv in seen.OrderBy(k => k.Key))
            Console.WriteLine($"   {kv.Key,-22} ship {kv.Value}");
        RenderWire(gd, seen, session);
    }

    private static string? CivKeyForShipSide(Allegiance.ClientCore.State.ClientWorld world, short sideId)
    {
        if (world.SideRoster is not { } roster) return null;
        foreach (var s in roster.Sides)
            if (s.SideId == sideId)
                return HullModelResolver.CivKeyByIdShort(s.CivId);
        return null;
    }

    private void RenderWire(GraphicsDevice gd, Dictionary<string, string> assets, ConnectionSession session)
    {
        string civ = CivKeyForShipSide(session.World, _o.Side)?.Split('_')[0] ?? _o.Side.ToString();
        int n = assets.Count;
        int cols = (int)Math.Ceiling(n / 5.0);
        int rows = (int)Math.Ceiling(n / (double)cols);
        const float colSp = 210f, rowSp = 200f, tileR = 70f;
        float maxC = cols - 1f, maxR = rows - 1f;

        var pts = new List<Vector2>();
        for (int c = 0; c < cols; c++) for (int r = 0; r < rows; r++)
            pts.Add(new Vector2((c - maxC / 2f) * colSp, (r - maxR / 2f) * rowSp));
        var center = Vector3.Zero;
        var eye = center + new Vector3(0f, (maxR * rowSp) * 0.9f, (maxR * rowSp) * 1.4f);
        var view = Matrix.CreateLookAt(eye, center, Vector3.Up);
        var vmin = new Vector2(float.MaxValue); var vmax = new Vector2(float.MinValue);
        foreach (var p in pts)
        {
            var v = Vector3.Transform(new Vector3(p.X, 0f, p.Y), view);
            vmin = Vector2.Min(vmin, new Vector2(v.X - tileR * 2.4f, v.Y - tileR * 2.4f));
            vmax = Vector2.Max(vmax, new Vector2(v.X + tileR * 2.4f, v.Y + tileR * 2.4f));
        }
        float viewW = Math.Max(vmax.X - vmin.X, 1f), viewH = Math.Max(vmax.Y - vmin.Y, 1f);
        float ppu = Math.Clamp(1400f / Math.Max(viewW, 1f), 1.2f, 3.0f);
        int W = (int)Math.Ceiling(viewW * ppu), H = (int)Math.Ceiling(viewH * ppu);
        var proj = Matrix.CreateOrthographic(viewW, viewH, 1f, 60000f);

        using var rt = new RenderTarget2D(gd, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.004f, 0.009f, 0.020f));
        gd.DepthStencilState = DepthStencilState.Default;
        int i = 0;
        foreach (var asset in assets.Keys.OrderBy(x => x))
        {
            var m = LoadModel(asset);
            float scale = (tileR * 0.62f) / RenderRadius(m);
            int c = i % cols, r = i / cols;
            float x = (c - maxC / 2f) * colSp, z = (r - maxR / 2f) * rowSp;
            _shipRenderer.Draw(m, Matrix.CreateScale(scale) * Matrix.CreateRotationY(-0.7f)
                                 * Matrix.CreateTranslation(x, 0f, z), view, proj,
                                 texture: LoadTexture(asset));
            i++;
        }
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        string title = $"WIRE RECEIVED  (side {_o.Side})  {n} ships";
        var tsz = _fontBig.MeasureString(title);
        _batch.DrawString(_fontBig, title, new Vector2(W / 2f - tsz.X / 2f, 10f), Color.Orange);
        i = 0;
        foreach (var asset in assets.Keys.OrderBy(x => x))
        {
            int c = i % cols, r = i / cols;
            float x = (c - maxC / 2f) * colSp, z = (r - maxR / 2f) * rowSp;
            var v = Vector3.Transform(new Vector3(x, 0f, z), view);
            float sx = (v.X / viewW + 0.5f) * W, sy = (0.5f - v.Y / viewH) * H;
            var sz = _font.MeasureString(asset);
            float dy = tileR / viewH * H;
            _batch.DrawString(_font, asset, new Vector2(sx - sz.X / 2f, sy + dy + 4f), Color.LightGreen);
            i++;
        }
        _batch.End();
        gd.SetRenderTarget(null);
        SaveJpeg($"roster_net_{civ}_wire.jpg", rt);
        Console.WriteLine($"[roster-capture] wrote roster_net_{civ}_wire.jpg ({n} ships, {W}x{H})");
    }

    // ── shared camera / geometry helpers ─────────────────────────────────

    private sealed record Cam(Matrix View, Matrix Proj, float ViewW, float ViewH, int Px, int Py);

    private static Vector2 TilePos(Ship s, float maxRows)
    {
        float x = (s.Column - 4f) * ColSpacing;
        float z = (s.Row - (maxRows - 1) / 2f) * RowSpacing;
        return new Vector2(x, z);
    }

    private Cam Camera(List<Ship> ships, float maxRows, float eleY = 0.9f, float backZ = 1.4f)
    {
        var pts = ships.Select(s => TilePos(s, maxRows)).ToList();
        float minX = pts.Min(p => p.X) - TileRadius * 2.4f;
        float maxX = pts.Max(p => p.X) + TileRadius * 2.4f;
        float minZ = pts.Min(p => p.Y) - TileRadius * 2.4f;
        float maxZ = pts.Max(p => p.Y) + TileRadius * 2.4f;
        var center = new Vector3((minX + maxX) / 2f, 0f, (minZ + maxZ) / 2f);
        var eye = center + new Vector3(0f, (maxZ - minZ) * eleY, (maxZ - minZ) * backZ);
        var view = Matrix.CreateLookAt(eye, center, Vector3.Up);

        var vmin = new Vector2(float.MaxValue);
        var vmax = new Vector2(float.MinValue);
        foreach (var p in pts)
        {
            var v = Vector3.Transform(new Vector3(p.X, 0f, p.Y), view);
            float m = TileRadius * 2.4f;
            vmin = Vector2.Min(vmin, new Vector2(v.X - m, v.Y - m));
            vmax = Vector2.Max(vmax, new Vector2(v.X + m, v.Y + m));
        }
        float viewW = Math.Max(vmax.X - vmin.X, 1f);
        float viewH = Math.Max(vmax.Y - vmin.Y, 1f);
        float ppu = Math.Clamp(2800f / Math.Max(viewW, 1f), 1.2f, 3.0f);
        int px = (int)Math.Ceiling(viewW * ppu);
        int py = (int)Math.Ceiling(viewH * ppu);
        var proj = Matrix.CreateOrthographic(viewW, viewH, 1f, 60000f);
        return new Cam(view, proj, viewW, viewH, px, py);
    }

    private Cam SheetCamera(int cols, int rows, float maxC, float maxR)
    {
        float minX = -maxC / 2f * ColSpacing - TileRadius * 2.4f;
        float maxX = maxC / 2f * ColSpacing + TileRadius * 2.4f;
        float minZ = -maxR / 2f * RowSpacing - TileRadius * 2.4f;
        float maxZ = maxR / 2f * RowSpacing + TileRadius * 2.4f;
        var center = new Vector3(0f, 0f, 0f);
        var eye = center + new Vector3(0f, (maxZ - minZ) * 0.9f, (maxZ - minZ) * 1.4f);
        var view = Matrix.CreateLookAt(eye, center, Vector3.Up);
        var vmin = new Vector2(float.MaxValue);
        var vmax = new Vector2(float.MinValue);
        for (int c = 0; c < cols; c++)
            for (int r = 0; r < rows; r++)
            {
                float x = (c - maxC / 2f) * ColSpacing, z = (r - maxR / 2f) * RowSpacing;
                var v = Vector3.Transform(new Vector3(x, 0f, z), view);
                vmin = Vector2.Min(vmin, new Vector2(v.X - TileRadius * 2.4f, v.Y - TileRadius * 2.4f));
                vmax = Vector2.Max(vmax, new Vector2(v.X + TileRadius * 2.4f, v.Y + TileRadius * 2.4f));
            }
        float viewW = Math.Max(vmax.X - vmin.X, 1f), viewH = Math.Max(vmax.Y - vmin.Y, 1f);
        float ppu = Math.Clamp(1500f / Math.Max(viewW, 1f), 1.2f, 3.0f);
        int px = (int)Math.Ceiling(viewW * ppu), py = (int)Math.Ceiling(viewH * ppu);
        return new Cam(view, Matrix.CreateOrthographic(viewW, viewH, 1f, 60000f), viewW, viewH, px, py);
    }

    private static Vector2 Project(Cam cam, Vector3 world)
    {
        var v = Vector3.Transform(world, cam.View);
        return new Vector2((v.X / cam.ViewW + 0.5f) * cam.Px, (0.5f - v.Y / cam.ViewH) * cam.Py);
    }

    /// <summary>Fixed low-elevation orbit camera for the 4 canonical turnaround
    /// views. <paramref name="target"/> is the ship's projected sphere radius in
    /// view units (we draw every hull scaled so its TRUE rendered radius equals
    /// <paramref name="target"/>), so the square ortho frustum is identical for
    /// every view of every hull -> each of the 4 angles frames the SAME subject
    /// to the SAME apparent size and margin, and a sphere guarantees nothing
    /// clips in any view. Azimuth order (viewed from above; the ship sits at
    /// identity with its nose along -Z): 0=front (nose faces the camera),
    /// 1=right (+X side), 2=back (tail), 3=left (-X side).</summary>
    private Cam TurnaroundCam(float target, int az)
    {
        float viewHalf = target * 1.15f;                 // ~13% margin around the guaranteed-fit sphere disc
        float viewW = viewHalf * 2f, viewH = viewHalf * 2f;
        const float elevDeg = 14f;                        // modest fixed look-down; keeps side silhouettes clean
        float elev = MathHelper.ToRadians(elevDeg);
        float horiz = (float)Math.Cos(elev), vert = (float)Math.Sin(elev);
        float dist = target * 8f;                         // finite but far enough that ortho rays stay ~parallel
        var d = new[]
        {
            new Vector2(0f, -1f),  // front
            new Vector2(1f,  0f),  // right
            new Vector2(0f,  1f),  // back
            new Vector2(-1f, 0f),  // left
        }[az];
        var eye = new Vector3(d.X, 0f, d.Y) * (dist * horiz) + new Vector3(0f, dist * vert, 0f);
        var view = Matrix.CreateLookAt(eye, Vector3.Zero, Vector3.Up);
        var proj = Matrix.CreateOrthographic(viewW, viewH, 1f, 60000f);
        const int px = 880, py = 880;
        return new Cam(view, proj, viewW, viewH, px, py);
    }

    /// <summary>Merged rendered bounding-sphere CENTER of a model in the same
    /// bone-restored space RenderRadius/ShipRenderScale measure (a uniform bone
    /// scale preserves the sphere relationship, so merging bone-transformed
    /// spheres is exact). Used to centre each hull on the turnaround origin so
    /// all 4 views frame it identically.</summary>
    private static Vector3 RenderSphereCenter(XnaModel m)
    {
        BoundingSphere? acc = null;
        foreach (var mesh in m.Meshes)
        {
            var T = mesh.ParentBone.Transform;
            float s = new Vector3(T.M11, T.M12, T.M13).Length();
            var local = mesh.BoundingSphere;
            var c = Vector3.Transform(local.Center, T);
            var sph = new BoundingSphere(c, local.Radius * s);
            acc = acc is null ? sph : BoundingSphere.CreateMerged(acc.Value, sph);
        }
        return acc is null ? Vector3.Zero : acc.Value.Center;
    }

    private void DrawStars(GraphicsDevice gd, Cam cam)
    {
        var rand = new Random(4242);
        for (int i = 0; i < 500; i++)
        {
            float x = (float)(rand.NextDouble() * 2 - 1) * 9000f;
            float z = (float)(rand.NextDouble() * 2 - 1) * 9000f;
            float y = (float)(rand.NextDouble() * 2 - 1) * 5500f;
            var clip = Vector4.Transform(new Vector4(x, y, z, 1f), cam.View * cam.Proj);
            if (clip.W <= 0f) continue;
            var ndc = new Vector2((clip.X / clip.W + 1f) / 2f, (1f - clip.Y / clip.W) / 2f);
            if (ndc.X < 0f || ndc.X > 1f || ndc.Y < 0f || ndc.Y > 1f) continue;
            int size = 1 + (i % 2);
            int px = (int)(ndc.X * cam.Px), py = (int)(ndc.Y * cam.Py);
            var vb = new VertexBuffer(gd, typeof(VertexPositionColor), 3, BufferUsage.WriteOnly);
            vb.SetData(new[]
            {
                new VertexPositionColor(new Vector3(px - size, py - size, 0), Color.White),
                new VertexPositionColor(new Vector3(px + size, py - size, 0), Color.White),
                new VertexPositionColor(new Vector3(px, py + size, 0), Color.White),
            });
            gd.SetVertexBuffer(vb);
            using var eff = new BasicEffect(gd);
            eff.World = Matrix.Identity; eff.View = Matrix.Identity;
            eff.Projection = Matrix.CreateOrthographicOffCenter(0, cam.Px, cam.Py, 0, 0, 1);
            eff.VertexColorEnabled = true;
            eff.CurrentTechnique.Passes[0].Apply();
            gd.DrawPrimitives(PrimitiveType.TriangleList, 0, 1);
        }
    }

    private void DrawGrid(GraphicsDevice gd, Cam cam, float maxRows, int W, int H)
    {
        var grid = new Color(48, 58, 92) * 0.55f;
        for (int c = 0; c <= 9; c++)
        {
            float wx = (c - 4f) * ColSpacing;
            var left = Project(cam, new Vector3(wx, 0f, 0f));
            _batch.Draw(_white, new Rectangle((int)left.X, 0, 1, H), grid);
        }
        for (int r = 0; r <= (int)maxRows; r++)
        {
            float wz = (r - (maxRows - 1) / 2f) * RowSpacing;
            var mid = Project(cam, new Vector3(0f, 0f, wz));
            _batch.Draw(_white, new Rectangle(0, (int)mid.Y, W, 1), grid);
        }
    }

    private void SaveJpeg(string name, RenderTarget2D rt)
    {
        using var fs = File.Create(Path.Combine(_o.OutDir, name));
        rt.SaveAsJpeg(fs, rt.Width, rt.Height);
    }

    /// <summary>TRUE rendered radius: the content pipeline unit-normalizes each
    /// model (MergedSphere local radius ~1.0), but ShipRenderer draws through
    /// <c>mesh.ParentBone.Transform</c>, which bakes a large (100×) uniform scale
    /// that restores real-world units (~190-unit hulls). Normalizing the sheet
    /// tile by the local radius alone therefore leaves every ship 100× too big —
    /// the P331 "one giant mesh fills the frame" bug. Transform each mesh's local
    /// sphere through its parent-bone (uniform scale ⇒ radius×scale), then merge,
    /// so the per-tile normalization matches what actually renders.</summary>
    private static float RenderRadius(XnaModel m)
    {
        BoundingSphere? acc = null;
        foreach (var mesh in m.Meshes)
        {
            var T = mesh.ParentBone.Transform;
            float s = new Vector3(T.M11, T.M12, T.M13).Length();
            var local = mesh.BoundingSphere;
            var c = Vector3.Transform(local.Center, T);
            var sph = new BoundingSphere(c, local.Radius * s);
            acc = acc is null ? sph : BoundingSphere.CreateMerged(acc.Value, sph);
        }
        return acc is null ? 1f : acc.Value.Radius;
    }

    private static BoundingSphere MergedSphere(XnaModel m)
    {
        var bs = m.Meshes[0].BoundingSphere;
        for (int i = 1; i < m.Meshes.Count; i++)
            bs = BoundingSphere.CreateMerged(bs, m.Meshes[i].BoundingSphere);
        return bs;
    }
}
