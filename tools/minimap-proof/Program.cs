using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Wire;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;

// P360 live-proof harness: boots a REAL in-process server on a REAL multi-
// sector map (m2xspiderw, 13 sectors / 44 warps) and connects a REAL client
// session ("me") that joins and flies. Once the client owns a ship and has the
// real whole-mission ClusterGraph + cluster EXPORTs + its own sector, it:
//   1. renders the REAL StarmapRenderer.DrawMini(...) — the persistent, always-
//      on compact corner mini-map (the P360 default state, no toggle key needed)
//      — to an offscreen PNG;
//   2. renders the REAL StarmapRenderer.Draw(...) full centred pane PLUS the
//      real SectorInfoPaneRenderer for the player's own sector — the EXPANDED
//      view that Tab brings up (and the same data collapsed back to #1), proving
//      expand/collapse between the two states renders from the same live graph.
// Both views are drawn from the same gathered sites/edges/current, so the proof
// is exactly the code path Game1.DrawStarmap takes each flight frame.

var options = ProofOptions.Parse(args);
using var game = new ProofRunner(options);
game.Run();
return game.Failed ? 1 : 0;

sealed class ProofOptions
{
    public string ArtRoot = "";
    public string ContentRoot = "";
    public string OutDir = "";
    public bool FooterRegression;
    public bool LabelRegression;
    public bool Threats;
    public int Width = 1280, Height = 800;
    public string Tag = "after";

    public static ProofOptions Parse(string[] args)
    {
        string art = "", content = "", outDir = "";
        bool footer = false, label = false, threats = false;
        int width = 1280, height = 800;
        string tag = "after";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--footer-regression": footer = true; break;
                case "--label-regression": label = true; break;
                case "--threats": threats = true; break;
                case "--window":
                    var size = args[++i].Split('x');
                    width = int.Parse(size[0]); height = int.Parse(size[1]);
                    break;
                case "--tag": tag = args[++i]; break;
                case "--art": art = args[++i]; break;
                case "--content": content = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string repo = RepoRoot();
        return new ProofOptions
        {
            FooterRegression = footer, LabelRegression = label, Threats = threats,
            Width = width, Height = height, Tag = tag,
            ArtRoot = art.Length > 0 ? art : Path.Combine(repo, "docs", "artwork"),
            ContentRoot = content.Length > 0 ? content : Path.Combine(repo, "src", "Allegiance.ClientCore.MonoGame", "Content", "bin", "DesktopGL", "Content"),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p360"),
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
    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private SpriteBatch _sb = null!;
    private Texture2D _white = null!;
    private SpriteFont _font = null!;
    private StarmapRenderer _starmap = null!;
    private SectorInfoPaneRenderer _infoPane = null!;

    private int Width => _o.Width;
    private int Height => _o.Height;
    public bool Failed { get; private set; }
    private bool _captured;
    private int _frames;

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = Width;
        _gdm.PreferredBackBufferHeight = Height;
        Content.RootDirectory = o.ContentRoot;
        Directory.CreateDirectory(o.OutDir);
    }

    protected override void Initialize()
    {
        _gdm.ApplyChanges();
        base.Initialize();
        _sb = new SpriteBatch(GraphicsDevice);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });
        _font = Content.Load<SpriteFont>("HUD");
        _starmap = new StarmapRenderer(_sb, _white, _font, GraphicsDevice,
            new DirectoryAssetSource(_o.ArtRoot));
        _infoPane = new SectorInfoPaneRenderer(_sb, _white, _font);
        if (!_o.Threats && !_o.FooterRegression && !_o.LabelRegression)
        {
            try { RunProof(GraphicsDevice); }
            catch (Exception ex) { Failed = true; Console.WriteLine($"[proof] FATAL: {ex}"); }
            Exit();
        }
    }

    // P715: deterministic bottom-row fixture on a real window backbuffer,
    // using the same DrawMini and compiled HUD font as the flight client.
    protected override void Draw(GameTime gameTime)
    {
        if ((!_o.Threats && !_o.FooterRegression && !_o.LabelRegression) || _captured) return;
        try
        {
            if (_o.Threats) { DrawThreatFixture(); return; }
            var sites = _o.LabelRegression ? LabelFixture() : FooterFixture();
            ClusterInfo.WarpEdge[] edges = _o.LabelRegression
                ? LabelFixtureEdges()
                :
                [
                    new(1, 2), new(3, 1), new(1, 4),
                ];
            GraphicsDevice.Clear(new Color(0.015f, 0.019f, 0.025f));
            _starmap.DrawMini(sites, edges, 1, null, true, GraphicsDevice.Viewport);
            if (++_frames < 5) return; // Let SDL process the initial window resize before readback.
            _captured = true;
            var mini = MinimapLayout.Build(sites, edges, 1, Width, Height, text =>
            {
                var size = _font.MeasureString(text);
                return new System.Numerics.Vector2(size.X, size.Y);
            });
            string stem = Path.Combine(_o.OutDir,
                $"{( _o.LabelRegression ? "p715b_" : "")}{_o.Tag}_{Width}x{Height}");
            if (_o.Tag != "before") File.WriteAllText(stem + ".json", System.Text.Json.JsonSerializer.Serialize(mini,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"[P715] viewport={GraphicsDevice.Viewport}; backbuffer={GraphicsDevice.PresentationParameters.BackBufferWidth}x{GraphicsDevice.PresentationParameters.BackBufferHeight}");
            var pixels = new Color[Width * Height];
            GraphicsDevice.GetBackBufferData(pixels);
            using var texture = new Texture2D(GraphicsDevice, Width, Height);
            texture.SetData(pixels);
            using (var stream = File.Create(stem + ".png")) texture.SaveAsPng(stream, Width, Height);
            // Also retain a lossless close-up of the actual captured backbuffer.
            var panel = mini.Panel;
            var region = new Rectangle((int)panel.X - 2, (int)panel.Y - 2,
                (int)MathF.Ceiling(panel.Width) + 4, (int)MathF.Ceiling(panel.Height) + 4);
            var crop = new Color[region.Width * region.Height];
            texture.GetData(0, region, crop, 0, crop.Length);
            using var closeup = new Texture2D(GraphicsDevice, region.Width, region.Height);
            closeup.SetData(crop);
            using (var stream = File.Create(stem + "_pane.png")) closeup.SaveAsPng(stream, region.Width, region.Height);
            Console.WriteLine($"[P715] captured {stem}");
            if (_o.Tag == "after")
            {
                if (mini.SectorLabels.Any(l => l.Rectangle.Bottom > mini.FooterArea.Y
                    || l.Rectangle.Y < mini.MapArea.Y
                    || l.Rectangle.X < panel.X + MinimapLayout.Border
                    || l.Rectangle.Right > panel.Right - MinimapLayout.Border)
                    || mini.FooterLabels.Any(l => l.Rectangle.Width > panel.Width - 2 * MinimapLayout.Border
                        || l.Rectangle.Y < mini.FooterArea.Y || l.Rectangle.Bottom > panel.Bottom))
                    throw new InvalidOperationException("Measured HUD-font text exceeds its reserved region");
                if (_o.LabelRegression)
                {
                    for (int i = 0; i < mini.SectorLabels.Count; i++)
                    for (int j = i + 1; j < mini.SectorLabels.Count; j++)
                        if (RectHit(mini.SectorLabels[i].Rectangle, mini.SectorLabels[j].Rectangle))
                            throw new InvalidOperationException(
                                $"Label overlap in capture: {mini.SectorLabels[i].Text} <> {mini.SectorLabels[j].Text}");
                }
            }
        }
        catch (Exception ex) { Failed = true; Console.WriteLine($"[P715] FATAL: {ex}"); }
        Exit();
    }

    // P762 — a real StarmapRenderer capture with every native threat tier and
    // the chat/selection overlays. The warnings fixture follows native's
    // sectormap.cpp:1066-1082 cascade; the chat cluster (4) gets
    // sectorhighlightbmp and the selected cluster (3) sectorselbmp.
    private void DrawThreatFixture()
    {
        StarmapLayout.ClusterSite[] sites =
        [
            new(1, "Station threat", 0, 0),
            new(2, "Capital", 220, -60),
            new(3, "Selected bomber", 120, 120),
            new(4, "Chat miner", -180, 100),
            new(5, "Combat", -120, -140),
            new(6, "Quiet", 20, 220),
        ];
        ClusterInfo.WarpEdge[] edges =
        [
            new(1, 2), new(1, 4), new(2, 3), new(3, 4), new(4, 5), new(5, 6), new(1, 6),
        ];
        short Warning(short id) => id switch
        {
            1 => ClusterWarnings.StationThreatened,
            2 => ClusterWarnings.CapitalInCluster,
            3 => ClusterWarnings.BomberInCluster,
            4 => ClusterWarnings.MinerThreatened,
            5 => ClusterWarnings.CombatInCluster,
            _ => ClusterWarnings.NoThreat,
        };
        GraphicsDevice.Clear(new Color(0.015f, 0.019f, 0.025f));
        float panelW = MathF.Min(Width * 0.80f, 1040f);
        float panelH = MathF.Min(Height * 0.66f, 640f);
        _starmap.Draw(sites, edges, currentSectorId: 1, selectedSectorId: 3,
            warnings: Warning, flashOn: true, GraphicsDevice.Viewport,
            panelW, panelH, highlightedClusters: new HashSet<short> { 4 });
        if (++_frames < 5) return;
        Capture("p762_starmap_threats");
    }

    private void Capture(string stem)
    {
        var pixels = new Color[Width * Height];
        GraphicsDevice.GetBackBufferData(pixels);
        using var texture = new Texture2D(GraphicsDevice, Width, Height);
        texture.SetData(pixels);
        using (var stream = File.Create(Path.Combine(_o.OutDir, $"{stem}_{Width}x{Height}.png")))
            texture.SaveAsPng(stream, Width, Height);
        Console.WriteLine($"[P762] captured {stem}_{Width}x{Height}.png");
        // Leave the frame visible for a moment before Exit() flushes the PNG.
        _captured = true;
        Exit();
    }

    // P715b dense fixture: current sector surrounded by a 6-sector hexagon
    // (m2xspiderw/Alderan style), with one neighbour directly above the current
    // sector to exercise the stacked-label collision.
    private static StarmapLayout.ClusterSite[] LabelFixture()
    {
        const float r = 120f;
        return
        [
            new(1, "Omega", 0, 0),
            new(2, "North", 0, -r),
            new(3, "Ne", r * 0.866f, -r * 0.5f),
            new(4, "Se", r * 0.866f, r * 0.5f),
            new(5, "South", 0, r),
            new(6, "Sw", -r * 0.866f, r * 0.5f),
            new(7, "Nw", -r * 0.866f, -r * 0.5f),
        ];
    }

    private static ClusterInfo.WarpEdge[] LabelFixtureEdges() =>
        [new(1, 2), new(1, 3), new(1, 4), new(1, 5), new(1, 6), new(1, 7)];

    private static StarmapLayout.ClusterSite[] FooterFixture() =>
    [
        new(1, "Omega", 0, 0), new(2, "Bug Nebula", -125, 100),
        new(3, "Tellus", 125, 100), new(4, "Alpha", 0, -100),
    ];

    private static bool RectHit(MinimapLayout.Bounds a, MinimapLayout.Bounds b) =>
        a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    // ── server / client wiring (mirrors targetinfo-proof / warp-visual-proof) ──

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = HomeArt(),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P360 Persistent Mini-Map Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return env is { Length: > 0 } e2 ? e2 : ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 961u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 94100000000000000ul, DrmHash = "miniproof",
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[proof] pump TIMEOUT");
    }

    // ── the proof ───────────────────────────────────────────────────────────

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine("[proof] booting in-process server (m2xspiderw)…");
        using var server = new Srv();
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass ready-up countdown

        using var me = new ConnectionSession();
        me.Connect(server.BoundPort, "proof_client");
        me.SendLogonRequest(Request("Me_Pilot"));
        me.SendPositionReq(0);

        // Join flight: own ship assigned AND the whole-mission graph + cluster
        // EXPORTs populated (DrawStarmap's exact prerequisites).
        PumpUntil(server,
            () => me.World.MyShipId is not null
                  && me.World.Graph is { } g && g.Clusters.Count > 0
                  && me.World.Clusters.Count > 0
                  && me.World.Ships.TryGetValue(me.World.MyShipId!.Value, out var ow)
                  && ow.SectorId != -1,
            12000);
        if (me.World.MyShipId is not { } meId || me.World.Graph is not { } graph)
            throw new InvalidOperationException("client never reached flying-with-graph state");

        // Gather exactly what Game1.DrawStarmap gathers each frame.
        var sites = new List<StarmapLayout.ClusterSite>();
        foreach (var ce in graph.Clusters)
            if (me.World.Clusters.TryGetValue(ce.Id, out var dc))
                sites.Add(new StarmapLayout.ClusterSite(ce.Id, dc.Name, dc.ScreenX, dc.ScreenY));
        IReadOnlyList<ClusterInfo.WarpEdge> edges = graph.Warps;
        short current = me.World.Ships[meId].SectorId;

        string curName = me.World.Clusters.TryGetValue(current, out var cdc) ? cdc.Name : "?";
        var neighbours = new List<string>();
        foreach (var e in edges)
        {
            if (e.SourceClusterId == current && me.World.Clusters.TryGetValue(e.DestClusterId, out var dn1)) neighbours.Add(dn1.Name);
            if (e.DestClusterId == current && me.World.Clusters.TryGetValue(e.SourceClusterId, out var dn2)) neighbours.Add(dn2.Name);
        }
        Console.WriteLine($"[proof] graph: {graph.Clusters.Count} clusters, {edges.Count} warp edges, {sites.Count} positioned sites");
        Console.WriteLine($"[proof] CURRENT sector #{current} '{curName}'  ({me.World.Ships[meId].SectorId})");
        Console.WriteLine($"[proof] neighbours (warp-linked to current): {string.Join(", ", neighbours)}");

        // 1) COLLAPSED — the persistent corner mini-map (P360 default, no key).
        Render(gd, "minimap_collapsed", (vp) =>
        {
            // A caption so the screenshot is self-labelling (the game HUD has
            // its own real captions; here we annotate the bare renderer).
            _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            _sb.DrawString(_font, "P360 - collapsed mini-map (always-on, no toggle)", new Vector2(12f, 8f), Color.Orange);
            _sb.End();
            _starmap.DrawMini(sites, edges, current, warnings: null, flashOn: true, vp);
        });

        // 2) EXPANDED — the full centred pane (Tab) + the real info pane for the
        //    player's own sector, composed from the SAME sites/edges/current.
        float panelW = MathF.Min(Width * 0.80f, 1040f);
        float panelH = MathF.Min(Height * 0.66f, 640f);
        float panelX = (Width - panelW) / 2f;
        float panelY = (Height - panelH) / 2f;

        // Build the real SectorInfoPane content for the current sector.
        var samples = new List<SectorInfoPaneLayout.AsteroidSample>();
        foreach (var ast in me.World.Asteroids.Values)
            if (ast.ClusterId == current)
                samples.Add(new SectorInfoPaneLayout.AsteroidSample(ast.AabmCapabilities, ast.Ore));
        var pane = SectorInfoPaneLayout.Build(curName, samples);

        Render(gd, "minimap_expanded", (vp) =>
        {
            _sb.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            _sb.DrawString(_font, "P360 - expanded full starmap (Tab)", new Vector2(12f, 8f), Color.Orange);
            _sb.End();
            _starmap.Draw(sites, edges, current, selectedSectorId: current, warnings: null, flashOn: true, vp, panelW, panelH);
            _infoPane.Draw(pane, vp, panelX, panelY, panelW, panelH);
        });

        Console.WriteLine($"[proof] done — PNGs in {_o.OutDir}");
    }

    private void Render(GraphicsDevice gd, string label, Action<Viewport> draw)
    {
        var vp = new Viewport(0, 0, Width, Height);
        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.015f, 0.019f, 0.025f));
        draw(vp);
        gd.SetRenderTarget(null);
        string path = Path.Combine(_o.OutDir, $"p360_{label}.png");
        using var fs = File.Create(path);
        rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[proof] wrote {path}");
    }
}
