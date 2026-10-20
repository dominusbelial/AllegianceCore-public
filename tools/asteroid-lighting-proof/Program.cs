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

// P529 headless visual proof: renders ONE spinning asteroid of two resource
// types at several rotation angles and two camera distances, under (a) the OLD
// stock BasicEffect rig (EnableDefaultLighting) and (b) the NEW native
// per-sector bidirectional lighting. Each cell is rendered into its own fresh
// 160x160 render target (the proven P421 pattern — sub-viewports into one RT
// proved unreliable), saved as a numbered PNG, and the contact sheets are
// composed offline by tools/asteroid-lighting-proof/compose_sheets.py.

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
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p529"),
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
    private const int Cell = 160;
    private static readonly float FovRad = MathHelper.ToRadians(55f);
    private const float RockRadius = 150f;
    private const float DistClose = 670f;
    private const float DistFar = 4050f;

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;
    private RockTextures? _rockTextures;
    private static readonly Color Bg = new(0.007f, 0.011f, 0.019f);

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = 666;
        _gdm.PreferredBackBufferHeight = 684;
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
            new Gm { Name = "P529 Asteroid Lighting Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt() =>
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static LogonRequest Request() => new()
    {
        CharacterName = "proof_ast", ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 621u, DwCookie = 0x529u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 98100000000000000ul, DrmHash = "asterlight",
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
        session.Connect(server.BoundPort, "proof_ast");
        session.SendLogonRequest(Request());

        PumpUntil(server, () => session.World.MyShipId is not null, 9000);
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        PumpUntil(server, () => session.World.Asteroids.Count > 0, 9000);
        Console.WriteLine($"[proof] joined; asteroids known={session.World.Asteroids.Count}");

        _rockTextures = RockTextures.Load(gd, RockTextureSource.ResolveArtworkDir());
        Console.WriteLine($"[proof] real rock surfaces loaded: {_rockTextures.LoadedCount}");

        SectorLighting lighting = ResolveLighting(session);
        Console.WriteLine($"[proof] sector light: towardDir=({lighting.AltDirection.X:F3},{lighting.AltDirection.Y:F3},{lighting.AltDirection.Z:F3}) " +
                          $"keyCol=({lighting.KeyColor.X:F3},{lighting.KeyColor.Y:F3},{lighting.KeyColor.Z:F3}) " +
                          $"altCol=({lighting.AltColor.X:F3},{lighting.AltColor.Y:F3},{lighting.AltColor.Z:F3}) amb={lighting.Ambient.X:F2}");

        LogTextureMips(gd);

        var renderer = new AsteroidRenderer(gd);   // deterministic unit icosphere

        var types = new[] { RockTextureFactory.AsteroidResourceType.Carbon, RockTextureFactory.AsteroidResourceType.Helium };
        foreach (var type in types)
        {
            var tex = _rockTextures!.For(type) ?? RendererProcedural(gd, type);
            // Spin sheet cells: row 0 = OLD, row 1 = NEW; cols = angles 0..300.
            double[] angles = [0, 60, 120, 180, 240, 300];
            for (int row = 0; row < 2; row++)
            {
                for (int c = 0; c < angles.Length; c++)
                {
                    var world = Matrix.CreateRotationY(MathHelper.ToRadians((float)angles[c])) * Matrix.CreateScale(RockRadius);
                    SaveCell(gd, renderer, tex, world, DistClose, row == 1 ? lighting : null,
                        $"spin_{type}_r{row}_c{c}.png");
                }
            }
            // Distance sheet cells: row 0 = OLD, row 1 = NEW; cols = close, far.
            float[] dists = [DistClose, DistFar];
            for (int row = 0; row < 2; row++)
            {
                for (int c = 0; c < dists.Length; c++)
                {
                    var world = Matrix.CreateScale(RockRadius);
                    SaveCell(gd, renderer, tex, world, dists[c], row == 1 ? lighting : null,
                        $"dist_{type}_r{row}_c{c}.png");
                }
            }
            Console.WriteLine($"[proof] rendered cells for {type}");
        }

        Console.WriteLine($"[proof] done — {types.Length} types x 16 cell PNGs in {_o.OutDir}");
    }

    private SectorLighting ResolveLighting(ConnectionSession session)
    {
        short mySector = 0;
        if (session.World.MyShipId is { } me && session.World.Ships.TryGetValue(me, out var own))
            mySector = own.SectorId;
        return session.World.Clusters.TryGetValue(mySector, out var dc)
            ? SectorLighting.FromCluster(dc.LightDirection, dc.LightColor)
            : SectorLighting.FromCluster(null, 0xFFFFFFFF);
    }

    private Texture2D RendererProcedural(GraphicsDevice gd, RockTextureFactory.AsteroidResourceType type)
    {
        var px = RockTextureFactory.GeneratePixels(RockTextureFactory.DefaultSize, RockTextureFactory.DefaultSeed, type);
        return TextureMipmapBuilder.CreateMipmapped(gd, px, RockTextureFactory.DefaultSize, RockTextureFactory.DefaultSize);
    }

    private void LogTextureMips(GraphicsDevice gd)
    {
        foreach (RockTextureFactory.AsteroidResourceType type in Enum.GetValues<RockTextureFactory.AsteroidResourceType>())
        {
            if (_rockTextures!.For(type) is { } tex)
                Console.WriteLine($"[proof] '{type}' real surface LevelCount={tex.LevelCount} ({tex.Width}x{tex.Height})");
        }
    }

    private void SaveCell(GraphicsDevice gd, AsteroidRenderer renderer, Texture2D tex,
        Matrix world, float dist, SectorLighting? lighting, string name)
    {
        var eye = new Xna.Vector3(0f, 0f, dist);
        var view = Matrix.CreateLookAt(eye, Xna.Vector3.Zero, Xna.Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(FovRad, 1f, 10f, 40000f);

        using var rt = new RenderTarget2D(gd, Cell, Cell, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, Bg, 1f, 0);
        renderer.Draw(world, view, proj, tex, lighting);
        gd.SetRenderTarget(null);

        string path = Path.Combine(_o.OutDir, name);
        using (var fs = File.Create(path)) rt.SaveAsPng(fs, rt.Width, rt.Height);
    }
}
