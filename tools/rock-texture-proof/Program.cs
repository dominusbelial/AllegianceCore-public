using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using Xna = Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

// P421 live-proof harness: boots a REAL in-process server hosting a mission
// with a REAL asteroid field, joins a REAL MonoGame-rendering client, and once
// the session has joined renders the asteroids that actually shipped in that
// field through the REAL AsteroidRenderer + the REAL RockTextures (the new P421
// per-resource-type original surface bitmaps), from a nearby camera, saving a
// PNG per resource type encountered — plus a procedural-comparison PNG so the
// real look can be judged against the old synthetic stand-in.

var options = ProofOptions.Parse(args);
using var game = new ProofRunner(options);
game.Run();
return 0;

sealed class ProofOptions
{
    public string ArtRoot = "";
    public string ContentRoot = "";
    public string OutDir = "";
    public string Map = "";

    public static ProofOptions Parse(string[] args)
    {
        string art = "", content = "", outDir = "", map = "";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--content": content = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--map": map = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string repo = RepoRoot();
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : (env is { Length: > 0 } ? env : "/home/domingo/Development/Artwork"),
            ContentRoot = content.Length > 0 ? content
                : Path.Combine(repo, "src", "Allegiance.ClientCore.MonoGame", "Content", "bin", "DesktopGL", "Content"),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p421"),
            Map = map.Length > 0 ? map : "m2xspiderw.igc",
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
    private SpriteBatch _batch = null!;
    private SpriteFont _font = null!;
    private RockTextures? _rockTextures;

    private const int Width = 640, Height = 480;
    private const float Dist = 850f;
    private static readonly float FovRad = MathHelper.ToRadians(55f);

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
        base.Initialize();
        _batch = new SpriteBatch(GraphicsDevice);
        try { _font = Content.Load<SpriteFont>("HUD"); } catch { _font = null!; }
        try
        {
            RunProof(GraphicsDevice);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[proof] FATAL: {ex}");
        }
        Exit();
    }

    private static Cfg Config(string map) => new()
    {
        ArtPath = HomeArt(),
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P421 Rock Texture Proof", Map = map, Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
        ?? "/home/domingo/Development/Artwork";

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 714u, DwCookie = 0x421u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 97800000000000000ul, DrmHash = "rocktex",
    };

    private static void PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        if (n >= max) Console.WriteLine("[proof] pump TIMEOUT");
    }

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine($"[proof] booting in-process server map={_o.Map}...");
        using var server = new Srv();
        server.Boot(Config(_o.Map));

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "proof_rock");
        session.SendLogonRequest(Request("proof_rock"));

        PumpUntil(server, () => session.World.MyShipId is not null, 9000);
        Console.WriteLine($"[proof] joined; asteroids known={session.World.Asteroids.Count}");

        // Force the mission Started (standard P180 test trick) so asteroids
        // actually export to the client (they only ship once the mission runs).
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;
        PumpUntil(server, () => session.World.Asteroids.Count > 0, 9000);
        Console.WriteLine($"[proof] after mission start: asteroids known={session.World.Asteroids.Count}");

        // Build the shared rock mesh + the REAL per-type surface textures.
        _rockTextures = RockTextures.Load(gd, RockTextureSource.ResolveArtworkDir());
        Console.WriteLine($"[proof] real rock surfaces loaded: {_rockTextures.LoadedCount}");

        var mesh = BuildSharedMesh(gd);

        // Group the real asteroids by resource type, render one representative each.
        var byType = session.World.Asteroids.Values
            .GroupBy(a => RockTextureFactory.Classify(a.AabmCapabilities))
            .ToDictionary(g => g.Key, g => g.First());

        if (byType.Count == 0)
            throw new InvalidOperationException("no asteroids shipped to the client — cannot render a rock field");

        foreach (var (type, ast) in byType)
        {
            string label = $"{type}  ({Stem(type) ?? "PROCEDURAL"})";
            RenderRock(gd, mesh, type, ast, label);
        }

        // Decisive contrast: same mesh, plain type, real vs procedural.
        RenderProceduralComparison(gd, mesh);

        Console.WriteLine($"[proof] done — {byType.Count + 1} PNGs in {_o.OutDir}");
    }

    private string? Stem(RockTextureFactory.AsteroidResourceType t) => RockTextureSource.MdlStemFor(t);

    private AsteroidRenderer BuildSharedMesh(GraphicsDevice gd)
    {
        // Prefer the real imported drgrock mesh when the compiled content is
        // present (content-pipeline XNB), else fall back to the procedural mesh.
        try
        {
            var model = Content.Load<XnaModel>("Models/drgrock");
            var imported = ImportedRockMeshLoader.FromModel(model);
            Console.WriteLine($"[proof] using real drgrock mesh ({imported.VertexCount} verts)");
            return new AsteroidRenderer(gd, imported);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[proof] real mesh unavailable ({ex.Message}); using procedural mesh");
            return new AsteroidRenderer(gd);
        }
    }

    private void RenderRock(GraphicsDevice gd, AsteroidRenderer renderer,
        RockTextureFactory.AsteroidResourceType type, DataAsteroidIGC ast, string label)
    {
        var astPos = new SimVec(ast.Position[0], ast.Position[1], ast.Position[2]);
        var astXna = PoseConverter.ToXna(astPos);

        // Camera near the asteroid, looking at it.
        var eye = astXna + new Xna.Vector3(0f, 0.3f, Dist);
        var view = Matrix.CreateLookAt(Xna.Vector3.Zero, astXna - eye, Xna.Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 10f, 80000f);

        var o = new Orientation();
        o.Set(new SimVec(ast.Forward[0], ast.Forward[1], ast.Forward[2]));
        var rot = PoseConverter.ToXnaRotation(o);
        var world = rot * Matrix.CreateScale(ast.Radius) * Matrix.CreateTranslation(astXna - eye);

        var realTex = _rockTextures!.For(type);   // null → procedural fallback
        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.002f, 0.004f, 0.010f));
        gd.DepthStencilState = DepthStencilState.Default;
        if (realTex is not null)
            renderer.Draw(world, view, proj, realTex);
        else
            renderer.Draw(world, view, proj);   // renderer default = procedural
        gd.DepthStencilState = DepthStencilState.None;

        Caption(label);
        gd.SetRenderTarget(null);

        string path = Path.Combine(_o.OutDir, $"rock_{Sanitize(type)}_real.png");
        using (var fs = File.Create(path)) rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[proof] wrote {path}  [{type}] real={(realTex is not null ? "YES" : "PROCEDURAL-FALLBACK")} " +
                          $"cap=0x{ast.AabmCapabilities:X2} rad={ast.Radius}");
    }

    private void RenderProceduralComparison(GraphicsDevice gd, AsteroidRenderer renderer)
    {
        var pos = new SimVec(0, 0, 3500);
        var posXna = PoseConverter.ToXna(pos);
        var eye = posXna + new Xna.Vector3(0, 0.3f, Dist);
        var view = Matrix.CreateLookAt(Xna.Vector3.Zero, posXna - eye, Xna.Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 10f, 80000f);
        var world = Matrix.Identity * Matrix.CreateScale(180f) * Matrix.CreateTranslation(posXna - eye);

        // Left half: real bgrnd05bmp (plain's real surface). Right half: procedural.
        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.002f, 0.004f, 0.010f));
        gd.DepthStencilState = DepthStencilState.Default;

        var leftViewport = new Viewport(0, 0, Width / 2, Height);
        var rightViewport = new Viewport(Width / 2, 0, Width / 2, Height);

        gd.Viewport = leftViewport;
        if (_rockTextures!.For(RockTextureFactory.AsteroidResourceType.Plain) is { } real)
            renderer.Draw(world, view, proj, real);
        else
            renderer.Draw(world, view, proj);

        gd.Viewport = rightViewport;
        renderer.Draw(world, view, proj);   // procedural default (plain tint)

        gd.Viewport = new Viewport(0, 0, Width, Height);
        gd.DepthStencilState = DepthStencilState.None;
        Caption("LEFT: real bgrnd05bmp   |   RIGHT: procedural (old)");
        gd.SetRenderTarget(null);

        string path = Path.Combine(_o.OutDir, "rock_compare_real_vs_procedural.png");
        using (var fs = File.Create(path)) rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[proof] wrote {path}");
    }

    private void Caption(string text)
    {
        if (_font is null) return;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        var sz = _font.MeasureString(text);
        _batch.DrawString(_font, text, new Vector2(Width / 2f - sz.X / 2f, 8f), Color.Orange);
        _batch.End();
    }

    private static string Sanitize(RockTextureFactory.AsteroidResourceType t) => t.ToString();
}
