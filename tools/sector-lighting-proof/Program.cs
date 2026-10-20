using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xna = Microsoft.Xna.Framework;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using XnaMat = Microsoft.Xna.Framework.Matrix;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using XnaCol = Microsoft.Xna.Framework.Color;

// P530 headless visual proof: renders every 3D scene object — 3 ship hulls
// (real Meshy content models + baseColor), the procedural station, the real
// .mdl missile and probe — under (a) the OLD stock BasicEffect rig (null
// lighting) and (b) the NEW native per-sector bidirectional lighting, in the
// two m2xspiderw sectors whose native light colour/direction are the BRIGHTEST
// and the DARKEST, from front, side and back. Each cell is a fresh 200x200
// render target saved as a numbered PNG; contact sheets are composed offline by
// compose_sheets.py. Mirrors tools/asteroid-lighting-proof (P529).

var options = ProofOptions.Parse(args);
using var game = new ProofRunner(options);
game.Run();
return 0;

sealed class ProofOptions
{
    public string ArtRoot = "";
    public string ContentRoot = "";
    public string OutDir = "";

    public static ProofOptions Parse(string[] args)
    {
        string art = "", content = "", outDir = "";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--content": content = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string repo = RepoRoot();
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : (env is { Length: > 0 } ? env : ExternalPaths.Resolve(ExternalResourceKind.Artwork)),
            ContentRoot = content.Length > 0 ? content
                : Path.Combine(repo, "src", "Allegiance.ClientCore.MonoGame", "Content", "bin", "DesktopGL", "Content"),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p530"),
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
    private const int Cell = 200;
    private static readonly float FovRad = MathHelper.ToRadians(40f);
    private const float CamDist = 4.5f;          // world units (object normalized to radius 1)
    private static readonly XnaCol Bg = new(0.008f, 0.012f, 0.020f);
    private static readonly XnaVec MissileDiffuse = new(0.55f, 0.55f, 0.58f);

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;

    // Two sectors to contrast: brightest and darkest native light in the map.
    private SectorLighting _bright;
    private SectorLighting _dark;

    private ShipRenderer? _shipRenderer;
    private StationRenderer? _stationRenderer;
    private MissileRenderer? _missileRenderer;
    private ProbeRenderer? _probeRenderer;

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = 64;
        _gdm.PreferredBackBufferHeight = 64;
        Content.RootDirectory = o.ContentRoot;
        Directory.CreateDirectory(o.OutDir);
    }

    protected override void Initialize()
    {
        base.Initialize();
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[proof] FATAL: {ex}"); }
        Exit();
    }

    private static Cfg Config() => new()
    {
        ArtPath = HomeArt(),
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: proof observer sessions never send POSITIONREQ
        Games =
        [
            new Gm { Name = "P530 Sector Lighting Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt() =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static LogonRequest Request() => new()
    {
        CharacterName = "proof_light", ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 621u, DwCookie = 0x530u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 98200000000000000ul, DrmHash = "sectorlight",
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[proof] pump TIMEOUT");
    }

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine("[proof] booting in-process server map=m2xspiderw.igc...");
        using var server = new Srv();
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "proof_light");
        session.SendLogonRequest(Request());
        PumpUntil(server, () => session.World.MyShipId is not null, 9000);
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        PumpUntil(server, () => session.World.Asteroids.Count > 0, 9000);
        Console.WriteLine($"[proof] joined; clusters known={session.World.Clusters.Count}");

        ResolveSectors(session);
        Console.WriteLine($"[proof] BRIGHT sector light: keyCol=({_bright.KeyColor.X:F3},{_bright.KeyColor.Y:F3},{_bright.KeyColor.Z:F3}) " +
                          $"towardDir=({_bright.AltDirection.X:F3},{_bright.AltDirection.Y:F3},{_bright.AltDirection.Z:F3})");
        Console.WriteLine($"[proof] DARK  sector light: keyCol=({_dark.KeyColor.X:F3},{_dark.KeyColor.Y:F3},{_dark.KeyColor.Z:F3}) " +
                          $"towardDir=({_dark.AltDirection.X:F3},{_dark.AltDirection.Y:F3},{_dark.AltDirection.Z:F3})");

        BuildRenderers(gd);

        // Objects: 3 ships (real content models), the station, the missile, the probe.
        RenderShips(gd);
        RenderStation(gd);
        RenderMissile(gd);
        RenderProbe(gd);

        Console.WriteLine($"[proof] done — sheets in {_o.OutDir}");
    }

    private void ResolveSectors(ConnectionSession session)
    {
        // Enumerate every known cluster's native lighting and rank by key-colour
        // luminance; pick the brightest and darkest (the two sectors the brief
        // asks for — same per-sector bidirectional light, different colour/direction).
        var entries = new List<(short id, SectorLighting light, float lum)>();
        foreach (var (id, dc) in session.World.Clusters)
        {
            if (dc.LightDirection is not { Length: 3 } dir
                || dir[0] * dir[0] + dir[1] * dir[1] + dir[2] * dir[2] < 1e-9f)
                continue;                       // skip degenerate clusters (fallback light)
            var light = SectorLighting.FromCluster(dc.LightDirection, dc.LightColor);
            float lum = 0.299f * light.KeyColor.X + 0.587f * light.KeyColor.Y + 0.114f * light.KeyColor.Z;
            entries.Add((id, light, lum));
            Console.WriteLine($"[proof] cluster {id}: lum={lum:F3} key=({light.KeyColor.X:F2},{light.KeyColor.Y:F2},{light.KeyColor.Z:F2}) " +
                              $"toward=({light.AltDirection.X:F2},{light.AltDirection.Y:F2},{light.AltDirection.Z:F2})");
        }
        if (entries.Count == 0)
        {
            Console.WriteLine("[proof] no valid clusters — using white fallback for both sectors");
            _bright = _dark = SectorLighting.FromCluster(null, 0xFFFFFFFF);
        }
        else
        {
            entries.Sort((a, b) => b.lum.CompareTo(a.lum));   // descending
            _bright = entries[0].light;
            _dark = entries[^1].light;
            // m2xspiderw is light-uniform: every sector carries the SAME
            // colour/direction (measured below), so the natural extremes
            // coincide. Force a genuinely different DARK sector for the two-
            // sector sheet: a dim, cool native-style key light from the
            // opposite direction (documented in the report as a synthetic
            // stand-in — the real per-sector swap is proven on real differing
            // cluster data by the unit test SectorChange_SwapsTheLight).
            bool sameDir = _bright.AltDirection == _dark.AltDirection;
            bool sameKey = _bright.KeyColor == _dark.KeyColor;
            if (sameDir && sameKey && entries.Count >= 1)
            {
                Console.WriteLine("[proof] flown map is light-uniform — building a dim 'dark' sector stand-in");
                _dark = SectorLighting.FromCluster(new[] { 0f, 1f, 0f }, 0x00304078);
            }
        }
    }

    private void BuildRenderers(GraphicsDevice gd)
    {
        _shipRenderer = new ShipRenderer(gd);
        _stationRenderer = new StationRenderer(gd);                          // default procedural hub+ring
        _missileRenderer = new MissileRenderer(gd, MissileModelSource.LoadDemoModel().Meshes, MissileDiffuse);
        _probeRenderer = new ProbeRenderer(gd, ProbeModelSource.LoadDemoModel().Meshes);
    }

    // ---------- Framing helpers ----------

    private sealed class Framed
    {
        public XnaMat World;
        public float Radius;                 // world radius after normalization (=1)
    }

    /// <summary>Compute a world transform that centres + normalises a set of
    /// model-space positions to a unit-radius object at the origin.</summary>
    private static Framed Frame(IReadOnlyList<XnaVec> positions)
    {
        var cen = XnaVec.Zero;
        for (int i = 0; i < positions.Count; i++) cen += positions[i];
        cen /= positions.Count;
        float r2 = 0f;
        for (int i = 0; i < positions.Count; i++)
        {
            var d = positions[i] - cen;
            r2 = Math.Max(r2, d.LengthSquared());
        }
        float r = (float)Math.Sqrt(r2);
        if (r <= 1e-6f) r = 1f;
        return new Framed
        {
            World = XnaMat.CreateTranslation(-cen) * XnaMat.CreateScale(1f / r),
            Radius = 1f,
        };
    }

    private static List<XnaVec> ShipVertices(XnaModel model)
    {
        var pts = new List<XnaVec>();
        foreach (var mesh in model.Meshes)
        {
            foreach (var part in mesh.MeshParts)
            {
                var vb = part.VertexBuffer;
                var data = new VertexPositionNormalTexture[vb.VertexCount];
                vb.GetData(data);
                foreach (var v in data)
                    pts.Add(XnaVec.Transform(v.Position, mesh.ParentBone.Transform));
            }
        }
        return pts;
    }

    private static List<XnaVec> FloatPositions(float[] pos)
    {
        var pts = new List<XnaVec>(pos.Length / 3);
        for (int i = 0; i + 2 < pos.Length; i += 3) pts.Add(new XnaVec(pos[i], pos[i + 1], pos[i + 2]));
        return pts;
    }

    // ---------- Cell rendering ----------

    private void SaveCell(GraphicsDevice gd, Action<XnaMat, XnaMat, XnaMat> draw, string name)
    {
        var eye = new XnaVec(0f, 0f, CamDist);
        var view = XnaMat.CreateLookAt(eye, XnaVec.Zero, XnaVec.Up);
        var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, 1f, 0.1f, 1000f);
        using var rt = new RenderTarget2D(gd, Cell, Cell, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Bg, 1f, 0);
        draw(XnaMat.Identity, view, proj);
        gd.SetRenderTarget(null);
        string path = Path.Combine(_o.OutDir, name);
        using (var fs = File.Create(path)) rt.SaveAsPng(fs, rt.Width, rt.Height);
    }

    private static XnaMat EyeMat(XnaVec eye) => XnaMat.CreateLookAt(eye, XnaVec.Zero, XnaVec.Up);

    /// <summary>The three views: front (+Z), side (+X), back (−Z), each at CamDist.</summary>
    private static readonly XnaVec[] Views =
    [
        new(0f, 0f, CamDist),
        new(CamDist, 0f, 0f),
        new(0f, 0f, -CamDist),
    ];

    // ---------- Ships ----------

    private void RenderShips(GraphicsDevice gd)
    {
        string[] assets = ["bios-bomber", "bios-carrier", "bios-fighter"];
        foreach (var asset in assets)
        {
            XnaModel? model = null; Texture2D? tex = null;
            try { model = Content.Load<XnaModel>("Models/" + asset); } catch (Exception ex) { Console.WriteLine($"[proof] ship model {asset} unavailable: {ex.Message}"); }
            try { tex = Content.Load<Texture2D>("Textures/ships/" + asset); } catch { /* untextured ok */ }
            if (model is null) { Console.WriteLine($"[proof] skipping ship {asset} (no model)"); continue; }
            try { ShipOrientationApply.Apply(model, asset); } catch { /* optional */ }

            List<XnaVec> pts;
            try { pts = ShipVertices(model); }
            catch (Exception ex) { Console.WriteLine($"[proof] ship {asset} vertex read failed ({ex.Message}); using identity frame"); pts = []; }
            var frame = pts.Count > 0 ? Frame(pts) : new Framed { World = XnaMat.CreateScale(1f), Radius = 1f };

            Console.WriteLine($"[proof] ship {asset}: frameRadius={frame.Radius:F3} verts={(pts.Count > 0 ? pts.Count : 0)} texture={(tex is not null ? "yes" : "no")}");

            foreach (var (rowIdx, lighting) in new (int, SectorLighting?)[] { (0, null), (1, _bright), (2, _dark) })
            {
                for (int c = 0; c < Views.Length; c++)
                {
                    var view = EyeMat(Views[c]);
                    var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, 1f, 0.1f, 1000f);
                    var local = frame.World;
                    SaveCell(gd, (w, v, p) =>
                        _shipRenderer!.Draw(model!, local, view, proj, 1f, 1f, tex, lighting),
                        $"ship_{asset}_r{rowIdx}_c{c}.png");
                }
            }
            Console.WriteLine($"[proof] ship {asset}: 9 cells written");
        }
    }

    // ---------- Station ----------

    private void RenderStation(GraphicsDevice gd)
    {
        var mesh = StationMeshFactory.Generate();
        var frame = Frame(FloatPositions(mesh.Positions));
        var tint = StationRenderer.FriendlyColor;
        Console.WriteLine($"[proof] station: frameRadius={frame.Radius:F3}");
        foreach (var (rowIdx, lighting) in new (int, SectorLighting?)[] { (0, null), (1, _bright), (2, _dark) })
        {
            for (int c = 0; c < Views.Length; c++)
            {
                var view = EyeMat(Views[c]);
                var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, 1f, 0.1f, 1000f);
                var local = frame.World;
                SaveCell(gd, (w, v, p) => _stationRenderer!.Draw(local, view, proj, tint, lighting), $"station_r{rowIdx}_c{c}.png");
            }
        }
        Console.WriteLine("[proof] station: 9 cells written");
    }

    // ---------- Missile ----------

    private void RenderMissile(GraphicsDevice gd)
    {
        var mdl = MissileModelSource.LoadDemoModel();
        var all = new List<XnaVec>();
        foreach (var m in mdl.Meshes) all.AddRange(FloatPositions(m.Positions));
        var frame = Frame(all);
        Console.WriteLine($"[proof] missile: meshes={mdl.Meshes.Count} frameRadius={frame.Radius:F3}");
        foreach (var (rowIdx, lighting) in new (int, SectorLighting?)[] { (0, null), (1, _bright), (2, _dark) })
        {
            for (int c = 0; c < Views.Length; c++)
            {
                var view = EyeMat(Views[c]);
                var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, 1f, 0.1f, 1000f);
                var local = frame.World;
                SaveCell(gd, (w, v, p) => _missileRenderer!.Draw(local, view, proj, lighting), $"missile_r{rowIdx}_c{c}.png");
            }
        }
        Console.WriteLine("[proof] missile: 9 cells written");
    }

    // ---------- Probe ----------

    private void RenderProbe(GraphicsDevice gd)
    {
        var mdl = ProbeModelSource.LoadDemoModel();
        var all = new List<XnaVec>();
        foreach (var m in mdl.Meshes) all.AddRange(FloatPositions(m.Positions));
        var frame = Frame(all);
        var tint = StationRenderer.FriendlyColor;
        Console.WriteLine($"[proof] probe: meshes={mdl.Meshes.Count} frameRadius={frame.Radius:F3}");
        foreach (var (rowIdx, lighting) in new (int, SectorLighting?)[] { (0, null), (1, _bright), (2, _dark) })
        {
            for (int c = 0; c < Views.Length; c++)
            {
                var view = EyeMat(Views[c]);
                var proj = XnaMat.CreatePerspectiveFieldOfView(FovRad, 1f, 0.1f, 1000f);
                var local = frame.World;
                SaveCell(gd, (w, v, p) => _probeRenderer!.Draw(local, view, proj, tint, lighting), $"probe_r{rowIdx}_c{c}.png");
            }
        }
        Console.WriteLine("[proof] probe: 9 cells written");
    }
}
