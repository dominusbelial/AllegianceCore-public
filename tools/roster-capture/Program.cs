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
// Defaults for --dump/--art/--content/--out resolve relative to the repo root
// (found by walking up to AllegianceCore.slnx from the running assembly).
//
// Requires the MonoGame client's compiled Content to exist (run a normal build
// of Allegiance.ClientCore.MonoGame first) and, for `wire`, a live
// `vizfight roster` server.

using System.Text.Json;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
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

    public static Options Parse(string[] args)
    {
        string root = ResolveRepoRoot();
        var o = new Options
        {
            Mode = args.Length > 0 && args[0] == "wire" ? "wire" : "dump",
            DumpPath = Path.Combine(root, "tools", "vizfight", "roster_dump.json"),
            ArtRoot = Path.Combine(root, "..", "Artwork"),
            ContentRoot = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame",
                "bin", "Debug", "net10.0", "Content"),
            OutDir = Path.Combine(root, "docs", "P305_roster"),
        };

        int i = (args.Length > 0 && (args[0] == "dump" || args[0] == "wire")) ? 1 : 0;
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
    private readonly ShipRenderer _shipRenderer;

    private SpriteBatch _batch = null!;
    private SpriteFont _font = null!;
    private SpriteFont _fontBig = null!;
    private Texture2D _white = null!;

    private const float ColSpacing = 230f;
    private const float RowSpacing = 175f;
    private const float TileRadius = 58f;

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
            _models[asset] = m;
        }
        return m;
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
        else
        {
            RunDump(gd);
        }
        Exit();
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
                                 * Matrix.CreateTranslation(p.X, 0f, p.Y), cam.View, cam.Proj);
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
                                 * Matrix.CreateTranslation(x, 0f, z), cam.View, cam.Proj);
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
            _shipRenderer.Draw(m, Matrix.CreateRotationY(-0.7f) * Matrix.CreateRotationX(-0.15f), view, proj);
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
            foreach (var (id, ship) in world.Ships)
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
                                 * Matrix.CreateTranslation(x, 0f, z), view, proj);
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
