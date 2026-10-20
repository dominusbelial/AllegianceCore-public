using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using Xna = Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

// P562 live-proof harness: boots a REAL in-process server hosting a mission with
// a real warp (m2xspiderw.igc — every warp Radius=100, textureName "aleph"),
// connects a real MonoGame-rendering client (ConnectionSession), and once the
// session has joined renders the warp's gate through the REAL WarpRenderer (the
// native-textured flowing vortex + counter-rotating star core) from THREE camera
// angles (head-on, 45° diagonal, side-on), an ANIMATION STRIP of the inward UV
// flow (8 frames over 2s), and a TRANSIT-SWELL STRIP (8 frames after a
// TriggerTransit). The old P341 camera-facing procedural "core" was replaced by
// the native f02 star-core decals in P562, so the isolated-core renders are gone.

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
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : Path.Combine(repo, "docs", "artwork"),
            ContentRoot = content.Length > 0 ? content : Path.Combine(repo, "src", "Allegiance.ClientCore.MonoGame", "Content"),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p562"),
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
    private WarpRenderer? _warpRenderer;
    private SpriteBatch _batch = null!;
    private SpriteFont _font = null!;

    private const int Width = 760, Height = 520;
    private static readonly float FovRad = MathHelper.ToRadians(52f);

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
        // Construct the renderer here (device exists only after base.Initialize).
        // It resolves the alephbmp funnel + f02 star textures from the artwork
        // tree via AssetSources.Artwork (the same DirectoryAssetSource the game uses).
        _warpRenderer = new WarpRenderer(GraphicsDevice, AssetSources.Artwork);
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

    // ── server / client wiring (mirrors the P341 proof) ────────────────────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = HomeArt(),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: proof observer sessions never send POSITIONREQ
        Games =
        [
            new Gm { Name = "P562 Warp Vortex Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return env is { Length: > 0 } ? env : ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 951u, DwCookie = 0x562u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 97300000000000000ul, DrmHash = "warpvortex",
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
        Console.WriteLine("[proof] booting in-process server (m2xspiderw.igc — has real warps)...");
        using var server = new Srv();
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "proof_warp");
        session.SendLogonRequest(Request("proof_warp"));

        PumpUntil(server, () => session.World.MyShipId is not null, 6000);
        Console.WriteLine($"[proof] joined; MyShipId={session.World.MyShipId}; warps known={session.World.Warps.Count}");
        if (session.World.Warps.Count == 0)
            throw new InvalidOperationException("no warps were exported to the client — cannot render a gate");

        var warp = session.World.Warps.Values.First();
        Console.WriteLine($"[proof] warp #{warp.WarpId} '{warp.Name}' radius={warp.Radius} texture='{warp.TextureName}' " +
                          $"cluster={warp.ClusterId} pos=({warp.Position[0]:0.#},{warp.Position[1]:0.#},{warp.Position[2]:0.#})");
        // Diagnostic: the per-warp texture-name distribution (P562 — the port
        // resolves each warp's own texture, and m2xspiderw.igc mixes several).
        Console.WriteLine("[proof] warp texture-name tally:");
        foreach (var g in session.World.Warps.Values.GroupBy(w => w.TextureName).OrderBy(g => g.Key))
            Console.WriteLine($"    '{g.Key}' x{g.Count()}  e.g. {string.Join(", ", g.Take(3).Select(w => $"{w.WarpId}:{w.Name}"))}");

        var fwd = forwardOf(warp);

        // ── Contact sheet: 3 distances × 3 angles, old vs new is argued in the
        //    report; here the NEW flowing vortex at 3 angles. ──
        foreach (var dist in new[] { 450f, 900f, 1400f })
            foreach (var (tag, dir) in new[]
                     {
                         ("headon", Axis(fwd)),
                         ("diag45", Diagonal(fwd)),
                         ("side90", Perpendicular(fwd)),
                     })
                RenderAngle(gd, session, warp, $"{tag}_d{dist:0}", dir, dist: dist, deltaSeconds: 0.030);

        // ── Animation strip: the inward UV flow (8 frames over 2 s — native's
        //    time/10 cycle, stepped so the stills span a full 2s of flow). ──
        for (int f = 0; f < 8; f++)
            RenderAngle(gd, session, warp, $"anim_{(f + 1):00}", Axis(fwd),
                dist: 880f, deltaSeconds: 2.0 / 8.0, annotate: $"flow {(f + 1) * (2.0 / 8.0):0.00}s");

        // ── Transit-swell strip: trigger the star swell, capture 8 frames across
        //    the 1.2 s swell + 0.25 s decay. ──
        _warpRenderer!.TriggerTransit(warp.WarpId);
        for (int f = 0; f < 8; f++)
            RenderAngle(gd, session, warp, $"swell_{(f + 1):00}", Axis(fwd),
                dist: 850f, deltaSeconds: 0.050, annotate: $"swell {(f * 0.075f + 0.0375f):0.00}s");

        Console.WriteLine($"[proof] done — contact sheet + anim + swell strips in {_o.OutDir}");
    }

    private static Xna.Vector3 forwardOf(DataWarpIGC warp) =>
        new(warp.Forward[0], warp.Forward[1], warp.Forward[2]);

    // ── per-frame rendering through the real WarpRenderer ───────────────────

    private void RenderAngle(GraphicsDevice gd, ConnectionSession session, DataWarpIGC warp,
        string name, Xna.Vector3 eyeDirection, float dist, double deltaSeconds,
        string annotate = "")
    {
        eyeDirection = Xna.Vector3.Normalize(eyeDirection);

        var warpPos = new SimVec(warp.Position[0], warp.Position[1], warp.Position[2]);
        var warpXna = PoseConverter.ToXna(warpPos);

        var eyeXna = warpXna + eyeDirection * dist;
        var viewDir = Xna.Vector3.Normalize(warpXna - eyeXna);
        var up = MathF.Abs(viewDir.Y) < 0.9f ? Xna.Vector3.Up : Xna.Vector3.Right;
        up -= viewDir * Xna.Vector3.Dot(up, viewDir);
        if (up.LengthSquared() < 1e-8f) up = Xna.Vector3.Right;
        up = Xna.Vector3.Normalize(up);

        var view = Matrix.CreateLookAt(Xna.Vector3.Zero, warpXna - eyeXna, up);
        var proj = Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 10f, 80000f);

        // Replicate Game1's warp world-matrix composition (orientation from
        // Forward, uniform scale by Radius × VisualScaleMultiplier, translation
        // by (pos − cameraEye)).
        var wo = new Orientation();
        wo.Set(new SimVec(warp.Forward[0], warp.Forward[1], warp.Forward[2]));
        var wRot = PoseConverter.ToXnaRotation(wo);
        float worldRadius = warp.Radius * WarpRenderer.VisualScaleMultiplier;
        var world = wRot * Matrix.CreateScale(worldRadius) * Matrix.CreateTranslation(warpXna - eyeXna);

        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.002f, 0.005f, 0.013f));   // deep space
        gd.DepthStencilState = DepthStencilState.Default;

        _warpRenderer!.Draw(world, view, proj, deltaSeconds, worldRadius, warp.TextureName, warp.WarpId);

        gd.DepthStencilState = DepthStencilState.None;
        if (_font is not null)
        {
            _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            string cap = $"WARP #{warp.WarpId} — {name}  (radius {warp.Radius}, visual x{WarpRenderer.VisualScaleMultiplier:0.#}){annotate}";
            var sz = _font.MeasureString(cap);
            _batch.DrawString(_font, cap, new Vector2(Width / 2f - sz.X / 2f, 8f), Color.Orange);
            _batch.End();
        }
        gd.SetRenderTarget(null);

        string path = Path.Combine(_o.OutDir, $"warp_{name}.png");
        using var fs = File.Create(path);
        rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[proof] wrote {path}");
    }

    private static Xna.Vector3 Axis(Xna.Vector3 f) => f;

    private static Xna.Vector3 Perpendicular(Xna.Vector3 f)
    {
        var refV = MathF.Abs(f.Y) < 0.9f ? Xna.Vector3.Up : Xna.Vector3.Right;
        return Xna.Vector3.Cross(f, refV);
    }

    private static Xna.Vector3 Diagonal(Xna.Vector3 f) =>
        Xna.Vector3.Normalize(Axis(f) + Perpendicular(f));
}
