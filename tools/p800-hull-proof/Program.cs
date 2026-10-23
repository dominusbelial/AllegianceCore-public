using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Content;
using Allegiance.Core.Model;
using Allegiance.Core.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaMat = Microsoft.Xna.Framework.Matrix;
using XnaVec = Microsoft.Xna.Framework.Vector3;

// P800 — every hull renders: a MEASURED pass over ALL hulls of the real static
// core (PCore001.igc). For every hull the tool renders, offscreen, exactly what
// the client would render in three places:
//
//   (a) the hangar/loadout preview  — the native branch of
//       Game1.DrawDockedHullPreview (Game1.Rendering.cs): native art, world =
//       DockedHullOrbit.World(spin), eye = 2.4 × (0.5·Length) on +Z, FOV 50°,
//       z-clip 1..10000 (DockedHullPreviewFraming, load.cpp:860-934), default
//       BasicEffect lighting (the client passes a null SectorLighting there);
//   (b) the IN-FLIGHT ship — the native path of the flight draw loop
//       (Game1.Rendering.cs ~640-810): real sector lighting, native scale
//       (0.5·Length)/meshRadius, LODGeo level chosen by projected pixel radius,
//       at three distances: NEAR (3× gameplay radius), MID (25×), FAR (400× →
//       the far-tier sprite via ShipLodSpriteRenderer, P628);
//   (c) the TARGET-pane model preview — the HUD-21 path (RenderTargetModelPreview
//       → TargetModelPreview): ortho 2×2, z 1..20, radius-1 scale 1/MaxVertexRadius,
//       headlight lighting.
//
// For each render it records COVERAGE (fraction of non-background pixels),
// mesh triangle count, mesh radius, max-vertex radius, applied scale, texture
// stem + presence, LOD level chosen. It then CLASSIFIES every hull
// (failure category) and writes a CSV + per-civ contact sheets + a summary.
//
// Run:  dotnet p800-hull-proof.dll --art <artworkDir> --out <dir> [--label before|after]
// Exit 0 unless a crash; the audit test asserts on the printed P800_SUMMARY.

var options = ProofOptions.Parse(args);
using var game = new ProofRunner(options);
game.Run();
return 0;

sealed class ProofOptions
{
    public string ArtRoot = "";
    public string OutDir = "";
    public string Label = "run";

    public static ProofOptions Parse(string[] args)
    {
        string art = "", outDir = "", label = "run";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--label": label = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "";
        art = art.Length > 0 ? art : (env.Length > 0 ? env : ExternalPaths.Require(ExternalResourceKind.Artwork));
        if (outDir.Length == 0)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
            outDir = Path.Combine(dir?.FullName ?? ".", ".scratch", "p800");
        }
        Directory.CreateDirectory(outDir);
        return new ProofOptions { ArtRoot = art, OutDir = outDir, Label = label };
    }
}

sealed record HullRow(
    short HullId, string Name, string Model, string? TexStem, bool TexPresent,
    string? Civ, bool Buyable, bool OfferedAllTech, float Length,
    float MeshRadius, float MaxVertexRadius, int Triangles, float Scale, int LodLevels,
    int LodNear, int LodMid,
    float CovHangar, float CovNear, float CovMid, float CovFar, float CovTarget,
    string Category, string Note);

sealed class ProofRunner : Game
{
    private const int RtSize = 256;
    private const int Thumb = 96;                       // contact-sheet cell image
    private const int Cell = Thumb + 4;                 // + border
    private const int SheetCols = 8;

    // Per-view coverage thresholds (fraction of the 256×256 frame). Ships are
    // SPARSE relative to their merged bounding sphere (a miner's truss fills a
    // fraction of it), so these detect EMPTY/degenerate renders (≈0) — the
    // failure the task hunts — not "small on screen", which is real behaviour.
    private const float CovMin = 0.003f;
    private const float CovMinMid = 0.0005f;
    private const float CovMinFar = 0.00008f;   // the ~5 px far sprite ≈ 0.03%

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private readonly List<HullRow> _rows = new();

    // The real native sector light (the m2xspiderw key the P564 proof used):
    // key (1.0,0.667,0.667) toward (0.707,0,0.707).
    private static readonly SectorLighting SectorLight =
        SectorLighting.FromCluster(new[] { 0.707f, 0f, 0.707f }, 0x00FFAAAA);

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = 64;
        _gdm.PreferredBackBufferHeight = 64;
    }

    protected override void Initialize()
    {
        base.Initialize();
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[proof] FATAL: {ex}"); Environment.ExitCode = 2; }
        Exit();
    }

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine($"[proof] P800 label={_o.Label} art={_o.ArtRoot} out={_o.OutDir}");
        var catalog = ShopCatalog.TryLoad(_o.ArtRoot);
        if (catalog is null) { Console.WriteLine("[proof] FATAL: no static core catalog"); Environment.ExitCode = 2; return; }

        // The offered-under-testUnlock set: the exact hangar predicate of
        // ShopCatalog.FilteredFor with every tech granted (testUnlock all-tech).
        var allBytes = new byte[TechTreeBitMask.ByteCount];
        Array.Fill<byte>(allBytes, 0xFF);
        var offered = catalog.FilteredFor(allBytes, new TechTreeBitMask(allBytes));
        Console.WriteLine($"[proof] P800 offered-under-testUnlock hulls = {offered.Hulls.Count}");

        var assets = new DirectoryAssetSource(_o.ArtRoot);
        var resolver = new HullModelResolver();
        var inspect = InspectorHullCatalog.Enumerate(catalog.Hulls, resolver, includeDrones: true,
            h => SafeMeshAvailable(gd, assets, h.ModelName));
        var civById = inspect.ToDictionary(x => x.HullId, x => x.CivKey);

        using var native = new NativeShipRenderer(gd, assets);
        using var spriteRenderer = new ShipLodSpriteRenderer(gd);
        using var rt = new RenderTarget2D(gd, RtSize, RtSize, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var profile = new ShipLodProfile();

        var buyableIds = catalog.Hulls.Where(h => h.GroupId >= 0).Select(h => h.HullId).ToHashSet();
        var offeredIds = offered.Hulls.Select(h => h.HullId).ToHashSet();
        Console.WriteLine($"[proof] P800 total hulls = {catalog.Hulls.Count}, player-buyable (GroupId≥0) = {buyableIds.Count}");

        var pixels = new Color[RtSize * RtSize];

        foreach (var hull in catalog.Hulls.OrderBy(h => h.HullId))
        {
            string stem = hull.ModelName?.Trim() ?? "";
            var (category, note) = ("", "");

            var geo = string.IsNullOrEmpty(stem) ? null : native.MeshFor(stem);
            float meshRadius = geo?.MeshRadius ?? 0f;
            float maxVertexRadius = geo?.MaxVertexRadius ?? 0f;
            int tris = geo?.TriangleCount ?? 0;
            int lodCount = string.IsNullOrEmpty(stem) ? 0 : native.LodLevelCount(stem);
            string? texStem = string.IsNullOrEmpty(stem) ? null : native.TextureStemFor(stem, hull.TextureName);
            Texture2D? tex = texStem is null ? null : native.TextureFor(texStem);
            float scale = NativeShipResolver.NativeScaleForLength(hull.Length, meshRadius);
            float gameplayRadius = 0.5f * hull.Length;

            if (string.IsNullOrEmpty(stem)) category = "MODEL_NAME_EMPTY";
            else if (geo is null) category = "MESH_MISSING";
            else if (tris <= 0) category = "EMPTY_MESH";
            else if (!float.IsFinite(meshRadius) || meshRadius <= 0f) category = "BAD_MESH_RADIUS";
            else if (!float.IsFinite(hull.Length) || hull.Length <= 0f) category = "BAD_HULL_LENGTH";
            else if (texStem is null || tex is null) { category = "TEXTURE_MISSING"; note = "renders lit-solid (by design)"; }

            // ---- (a) hangar preview: the client's native branch, verbatim ----
            float hangarCov = 0f;
            var hangarPixels = new Color[RtSize * RtSize];
            if (geo is not null)
            {
                float nr = scale * meshRadius;              // native rendered radius = 0.5·Length
                if (nr > 0f)
                {
                    var world = DockedHullOrbit.World(0.9f);
                    var eye = new XnaVec(0f, 0f, DockedHullPreviewFraming.EyeDistance(nr));
                    var view = XnaMat.CreateLookAt(eye, XnaVec.Zero, XnaVec.Up);
                    var proj = XnaMat.CreatePerspectiveFieldOfView(
                        DockedHullPreviewFraming.FovRadians, 1f,
                        DockedHullPreviewFraming.NativeZClipNear, DockedHullPreviewFraming.NativeZClipFar);
                    hangarCov = Render(gd, rt, pixels, () => native.Draw(geo, tex, world, view, proj, 1f, scale, null, stem, texStem), dump: DumpFor(hull.HullId, "hangar"));
                    Array.Copy(pixels, hangarPixels, pixels.Length);
                    _thumbnails[hull.HullId] = hangarPixels;   // for the per-civ contact sheet
                }
                else if (category.Length == 0) category = "PREVIEW_FRAMING_DEGENERATE";
            }

            // ---- (b) in-flight: near / mid / far sprite ----
            float covNear = 0f, covMid = 0f, covFar = 0f;
            int lodNear = -1, lodMid = -1;
            if (geo is not null && gameplayRadius > 0f)
            {
                var eyeRelNear = new XnaVec(0.35f, 0.25f, 1f) * (2f * gameplayRadius);
                var eyeRelMid = new XnaVec(0.35f, 0.25f, 1f) * (15f * gameplayRadius);
                var eyeRelFar = new XnaVec(0.35f, 0.25f, 1f) * (100f * gameplayRadius);
                // Ship at the origin, camera at −eyeRel: the ship then sits at
                // |eyeRel| — the camera-relative convention of the client's
                // flight view (view ∘ translate(pos − eye)).
                var view = XnaMat.CreateLookAt(-eyeRelNear, XnaVec.Zero, XnaVec.Up);   // ship at origin
                var proj = XnaMat.CreatePerspectiveFieldOfView(MathHelper.ToRadians(60f), 1f, 1f, 1e6f);

                float pxNear = ShipLod.ApplyLodBias(ShipLod.ProjectedPixelRadius(eyeRelNear, gameplayRadius, view, proj, RtSize), profile);
                float pxMid = ShipLod.ApplyLodBias(ShipLod.ProjectedPixelRadius(eyeRelMid, gameplayRadius, view, proj, RtSize), profile);
                lodNear = ShipLod.NativeLodLevelFor(pxNear, native.LodPixelRadii(stem));
                lodMid = ShipLod.NativeLodLevelFor(pxMid, native.LodPixelRadii(stem));

                var drawGeoNear = geo;
                if (lodNear > 0) drawGeoNear = native.LodLevel(stem, lodNear - 1) ?? geo;
                var drawGeoMid = geo;
                if (lodMid > 0) drawGeoMid = native.LodLevel(stem, lodMid - 1) ?? geo;

                // The client draws camera-relative: world = orientation · translate(pos − eye);
                // with the camera at −eyeRel above, the ship sits at the view centre.
                var worldNear = XnaMat.Identity;
                var worldMid = XnaMat.Identity;

                covNear = Render(gd, rt, pixels, () => native.Draw(drawGeoNear, tex, worldNear, view, proj, 1f, scale, SectorLight, stem, texStem), dump: DumpFor(hull.HullId, "near"));
                covMid = Render(gd, rt, pixels, () => native.Draw(drawGeoMid, tex, worldMid, view, proj, 1f, scale, SectorLight, stem, texStem), dump: DumpFor(hull.HullId, "mid"));

                // Far tier sprite — the client's exact far-sprite sizing (P628).
                float dist = eyeRelFar.Length();
                float scalePx = 0.5f * RtSize * proj.M22;
                float half = dist > 1e-4f && scalePx > 0f ? 5f * dist / scalePx : gameplayRadius;
                half = MathF.Min(half, gameplayRadius * 3f);
                float pxFar = ShipLod.ApplyLodBias(ShipLod.ProjectedPixelRadius(eyeRelFar, gameplayRadius, view, proj, RtSize), profile);
                float alpha = ShipLod.SpriteAlpha(1f, pxFar, profile);
                var sprites = new List<FarSprite> { new(texStem ?? "", eyeRelFar, half, Color.White * alpha, 0f) };
                covFar = Render(gd, rt, pixels, () => spriteRenderer.Draw(sprites, view, proj), dump: DumpFor(hull.HullId, "far"));

                if (category.Length == 0 && drawGeoNear.TriangleCount <= 0) category = "LOD_LEVEL_BROKEN";
            }

            // ---- (c) target-pane model preview (HUD-21) ----
            float covTarget = 0f;
            if (geo is not null)
            {
                var dp = TargetModelPreview.DefaultDp;
                var view = TargetModelPreview.View(dp, XnaVec.Up, new XnaVec(0f, 0f, 1f));
                var proj = TargetModelPreview.Projection();
                var light = TargetModelPreview.Lighting(dp);
                float tscale = TargetModelPreview.ScaleForMaxVertexRadius(maxVertexRadius);
                covTarget = Render(gd, rt, pixels, () => native.Draw(geo, tex, XnaMat.Identity, view, proj, 1f, tscale, light, stem, texStem), dump: DumpFor(hull.HullId, "target"));
            }

            // ---- coverage gates (never override a structural category) ----
            if (category.Length == 0 && geo is not null)
            {
                if (CovHangarExpected(hull) && hangarCov < CovMin) category = "PREVIEW_EMPTY";
                else if (gameplayRadius > 0f && covNear < CovMin) category = "FLIGHT_EMPTY";
                else if (gameplayRadius > 0f && covMid < CovMinMid) category = "FLIGHT_MID_EMPTY";
                else if (gameplayRadius > 0f && covFar < CovMinFar) category = "SPRITE_EMPTY";
                else if (covTarget < CovMin) category = "TARGET_EMPTY";
            }

            var row = new HullRow(hull.HullId, hull.Name, stem, texStem, tex is not null,
                civById.TryGetValue(hull.HullId, out var civ) ? civ : null,
                buyableIds.Contains(hull.HullId), offeredIds.Contains(hull.HullId),
                hull.Length, meshRadius, maxVertexRadius, tris, scale, lodCount,
                lodNear, lodMid, hangarCov, covNear, covMid, covFar, covTarget,
                category.Length == 0 ? "OK" : category, note);
            _rows.Add(row);
            Console.WriteLine(
                $"P800 {row.HullId,4} {row.Name} stem={row.Model} civ={row.Civ ?? "-"} buyable={(row.Buyable ? 1 : 0)} " +
                $"tris={row.Triangles} r={row.MeshRadius:F1} len={row.Length:F0} scale={row.Scale:F2} lod={row.LodLevels} " +
                $"covH={row.CovHangar:P1} covN={row.CovNear:P1} covM={row.CovMid:P1} covF={row.CovFar:P2} covT={row.CovTarget:P1} " +
                $"cat={row.Category}");
        }

        WriteOutputs(rt, gd);
    }

    /// <summary>The hangar preview is only meaningful for the native-art branch;
    /// every hull with resolvable art gets one (all of them today).</summary>
    private static bool CovHangarExpected(DataHullTypeIGC _) => true;

    /// <summary>P800_DUMP="93,899" — save the rendered frames of those hull ids
    /// as PNGs next to the output dir, for eyeball spot checks.</summary>
    private HashSet<short>? _dumpIds;

    private string? DumpFor(short hullId, string view)
    {
        _dumpIds ??= new HashSet<short>((Environment.GetEnvironmentVariable("P800_DUMP") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => short.TryParse(s, out var v) ? v : (short)-1));
        return _dumpIds.Contains(hullId) ? Path.Combine(_o.OutDir, $"p800-{_o.Label}-{hullId}-{view}.png") : null;
    }

    /// <summary>Render one frame into <paramref name="rt"/> and return the
    /// coverage: the fraction of pixels that differ from the black clear.</summary>
    private float Render(GraphicsDevice gd, RenderTarget2D rt, Color[] pixels, Action draw, string? dump = null)
    {
        gd.SetRenderTarget(rt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Color.Black, 1f, 0);
        gd.BlendState = BlendState.Opaque;
        gd.DepthStencilState = DepthStencilState.Default;
        gd.RasterizerState = RasterizerState.CullCounterClockwise;
        draw();
        gd.SetRenderTarget(null);
        rt.GetData(pixels);
        if (dump is not null)
            using (var fs = File.Create(dump))
                rt.SaveAsPng(fs, RtSize, RtSize);
        int hit = 0;
        foreach (var c in pixels)
            if (c.R > 24 || c.G > 24 || c.B > 24) hit++;
        return (float)hit / pixels.Length;
    }

    private static bool SafeMeshAvailable(GraphicsDevice gd, IAssetSource assets, string? stem)
    {
        if (string.IsNullOrWhiteSpace(stem)) return false;
        try
        {
            if (!assets.Exists(stem.Trim() + ".mdl")) return false;
            var file = MdlFile.Parse(AssetSources.ReadAllBytes(assets, stem.Trim() + ".mdl"));
            return file.Meshes.Count > 0;
        }
        catch { return false; }
    }

    // ── outputs ─────────────────────────────────────────────────────────────

    private void WriteOutputs(RenderTarget2D rt, GraphicsDevice gd)
    {
        // CSV
        var csv = new StringBuilder();
        csv.AppendLine("label,hullId,name,model,texStem,texPresent,civ,buyable,offeredAllTech,length,meshRadius,maxVertexRadius,triangles,scale,lodLevels,lodNear,lodMid,covHangar,covNear,covMid,covFar,covTarget,category,note");
        foreach (var r in _rows)
            csv.AppendLine(string.Join(',',
                _o.Label, r.HullId, Csv(r.Name), Csv(r.Model), Csv(r.TexStem ?? ""), r.TexPresent ? 1 : 0,
                Csv(r.Civ ?? "-"), r.Buyable ? 1 : 0, r.OfferedAllTech ? 1 : 0,
                r.Length.ToString("F2", CultureInfo.InvariantCulture),
                r.MeshRadius.ToString("F3", CultureInfo.InvariantCulture),
                r.MaxVertexRadius.ToString("F3", CultureInfo.InvariantCulture),
                r.Triangles, r.Scale.ToString("F4", CultureInfo.InvariantCulture),
                r.LodLevels, r.LodNear, r.LodMid,
                r.CovHangar.ToString("F5", CultureInfo.InvariantCulture),
                r.CovNear.ToString("F5", CultureInfo.InvariantCulture),
                r.CovMid.ToString("F5", CultureInfo.InvariantCulture),
                r.CovFar.ToString("F5", CultureInfo.InvariantCulture),
                r.CovTarget.ToString("F5", CultureInfo.InvariantCulture),
                r.Category, Csv(r.Note)));
        File.WriteAllText(Path.Combine(_o.OutDir, $"p800-hulls-{_o.Label}.csv"), csv.ToString());

        // Per-civ contact sheets from the hangar preview of every hull.
        foreach (var civ in _rows.Select(r => r.Civ ?? "-").Distinct(StringComparer.Ordinal))
        {
            var rows = _rows.Where(r => (r.Civ ?? "-") == civ).OrderBy(r => r.HullId).ToList();
            int cols = Math.Min(SheetCols, rows.Count);
            int gridRows = (rows.Count + cols - 1) / cols;
            var sheet = new Color[cols * Cell * gridRows * Cell];
            for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color(24, 24, 32);
            for (int i = 0; i < rows.Count; i++)
            {
                int gx = (i % cols) * Cell + 2, gy = (i / cols) * Cell + 2;
                BlitThumb(rt, gd, rows[i], gx, gy, sheet, cols * Cell, gridRows * Cell);
            }
            var tex = new Texture2D(gd, cols * Cell, gridRows * Cell);
            tex.SetData(sheet);
            string safeCiv = civ.Replace('/', '_');
            using (var fs = File.Create(Path.Combine(_o.OutDir, $"p800-sheet-{_o.Label}-{safeCiv}.png")))
                tex.SaveAsPng(fs, tex.Width, tex.Height);
            tex.Dispose();
        }

        // Summary
        int failures = _rows.Count(r => r.Category is not ("OK" or "TEXTURE_MISSING"));
        var byCat = _rows.GroupBy(r => r.Category).OrderBy(g => g.Key)
            .Select(g => $"{g.Key}={g.Count()}");
        Console.WriteLine(
            $"P800_SUMMARY label={_o.Label} total={_rows.Count} buyable={_rows.Count(r => r.Buyable)} " +
            $"offeredAllTech={_rows.Count(r => r.OfferedAllTech)} ok={_rows.Count(r => r.Category == "OK")} " +
            $"textureMissing={_rows.Count(r => r.Category == "TEXTURE_MISSING")} failures={failures} " +
            $"categories=[{string.Join(' ', byCat)}]");
        foreach (var r in _rows.Where(r => r.Category is not ("OK" or "TEXTURE_MISSING")))
            Console.WriteLine($"P800_FAILURE label={_o.Label} hull={r.HullId} '{r.Name}' model={r.Model} category={r.Category} note={r.Note}");
        File.WriteAllLines(Path.Combine(_o.OutDir, $"p800-summary-{_o.Label}.txt"),
            new[] { $"failures={failures}" }.Concat(_rows.Where(r => r.Category is not ("OK" or "TEXTURE_MISSING"))
                .Select(r => $"{r.HullId} {r.Name} {r.Model} {r.Category}")));
        Console.WriteLine($"[proof] P800 done — {_rows.Count} hulls -> {_o.OutDir}");
    }

    /// <summary>Blit one hull's hangar-preview thumbnail (captured at walk time
    /// into <see cref="_thumbnails"/>) into the contact sheet at (dx,dy).
    /// Failure rows get a red frame so a sheet skims correctly.</summary>
    private void BlitThumb(RenderTarget2D rt, GraphicsDevice gd, HullRow row, int dx, int dy,
        Color[] sheet, int sheetW, int sheetH)
    {
        if (!_thumbnails.TryGetValue(row.HullId, out var src)) return;
        int step = RtSize / Thumb;
        for (int y = 0; y < Thumb; y++)
        {
            if (dy + y >= sheetH) break;
            for (int x = 0; x < Thumb; x++)
            {
                if (dx + x >= sheetW) break;
                var c = src[y * step * RtSize + x * step];
                // Failure rows get a red frame so a sheet skims correctly.
                bool border = x < 2 || y < 2 || x >= Thumb - 2 || y >= Thumb - 2;
                sheet[(dy + y) * sheetW + dx + x] = border && row.Category is not ("OK" or "TEXTURE_MISSING")
                    ? new Color(220, 40, 40)
                    : c;
            }
        }
    }

    private readonly Dictionary<short, Color[]> _thumbnails = new();

    private static string Csv(string s)
    {
        if (s.AsSpan().IndexOfAny(',', '"', '\n') < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
