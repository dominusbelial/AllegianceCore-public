using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Model;
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

// P341 live-proof harness: boots a REAL in-process server hosting a mission with
// a real warp (m2xspiderw.igc — every warp Radius=100, measured), connects a real
// MonoGame-rendering client (ConnectionSession), and once the session has joined
// (warps export unconditionally on join for every sector) renders the warp's gate
// through the REAL WarpRenderer (funnel + the P341 camera-facing glow-disc core)
// from THREE distinct camera angles — head-on down the axis, directly side-on
// (90° oblique), and an intermediate diagonal — saving a PNG of each. This is the
// multi-angle live proof that the aleph now reads as a coherent portal from every
// direction, not just head-on (the entire point of P341).

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
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p341"),
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

    private const int Width = 960, Height = 720;   // 4:3 portrait-friendly frame; the gate reads large
    private const float Dist = 860f;               // camera distance from the warp centre (world units)
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
        // The GraphicsDevice is only guaranteed after base.Initialize() has run
        // the device-creation phase — construct the renderer here, not in the ctor.
        _warpRenderer = new WarpRenderer(GraphicsDevice);
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

    // ── server / client wiring (mirrors hullswap-proof) ────────────────────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = HomeArt(),
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P341 Warp Visual Proof", Map = "m2xspiderw.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string HomeArt()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return env is { Length: > 0 } ? env : "/home/domingo/Development/Artwork";
    }

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 951u, DwCookie = 0x341u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 97300000000000000ul, DrmHash = "warpproof",
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
        Console.WriteLine($"[proof] warp #{warp.WarpId} '{warp.Name}' radius={warp.Radius} cluster={warp.ClusterId} " +
                          $"pos=({warp.Position[0]:0.#},{warp.Position[1]:0.#},{warp.Position[2]:0.#}) " +
                          $"fwd=({warp.Forward[0]:0.###},{warp.Forward[1]:0.###},{warp.Forward[2]:0.###})");

        RenderAngle(gd, session, warp, "headon", eyeDirection: Axis(forwardOf(warp)), coreOnly: false);
        RenderAngle(gd, session, warp, "side90", eyeDirection: Perpendicular(forwardOf(warp)), coreOnly: false);
        RenderAngle(gd, session, warp, "intermediate", eyeDirection: Diagonal(forwardOf(warp)), coreOnly: false);

        // Isolated core renders (same three angles, funnel hidden) — decisively
        // prove the glow disc is a ROUND camera-facing billboard from every
        // direction, not an ellipse flattened into the funnel's plane.
        RenderAngle(gd, session, warp, "core_headon", eyeDirection: Axis(forwardOf(warp)), coreOnly: true);
        RenderAngle(gd, session, warp, "core_side90", eyeDirection: Perpendicular(forwardOf(warp)), coreOnly: true);
        RenderAngle(gd, session, warp, "core_intermediate", eyeDirection: Diagonal(forwardOf(warp)), coreOnly: true);

        Console.WriteLine($"[proof] done — 6 PNGs in {_o.OutDir}");
    }

    private static Xna.Vector3 forwardOf(DataWarpIGC warp) =>
        new(warp.Forward[0], warp.Forward[1], warp.Forward[2]);

    // ── per-angle rendering through the real WarpRenderer ───────────────────

    private void RenderAngle(GraphicsDevice gd, ConnectionSession session, DataWarpIGC warp, string name, Xna.Vector3 eyeDirection, bool coreOnly)
    {
        eyeDirection = Xna.Vector3.Normalize(eyeDirection);

        var warpPos = new SimVec(warp.Position[0], warp.Position[1], warp.Position[2]);
        var warpXna = PoseConverter.ToXna(warpPos);

        // Camera placed along `eyeDirection` at distance Dist, looking at the warp.
        var eyeXna = warpXna + eyeDirection * Dist;

        // A camera UP that is guaranteed perpendicular to the view direction
        // (safe for head-on where the axis may be any orientation).
        var viewDir = Xna.Vector3.Normalize(warpXna - eyeXna);
        var up = MathF.Abs(viewDir.Y) < 0.9f ? Xna.Vector3.Up : Xna.Vector3.Right;
        up -= viewDir * Xna.Vector3.Dot(up, viewDir);
        if (up.LengthSquared() < 1e-8f) up = Xna.Vector3.Right;
        up = Xna.Vector3.Normalize(up);

        // Camera-relative view/world, exactly the ChaseCamera/Game1 convention
        // (view from the origin, world translated by -cameraEye).
        var view = Matrix.CreateLookAt(Xna.Vector3.Zero, warpXna - eyeXna, up);
        var proj = Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 10f, 80000f);

        // Replicate Game1's warp world-matrix composition verbatim
        // (Game1.cs:2454-2467): orientation from Forward, uniform scale by
        // Radius × VisualScaleMultiplier, translation by (pos - cameraEye).
        var wo = new Orientation();
        wo.Set(new SimVec(warp.Forward[0], warp.Forward[1], warp.Forward[2]));
        var wRot = PoseConverter.ToXnaRotation(wo);
        var world = wRot
                    * Matrix.CreateScale(warp.Radius * WarpRenderer.VisualScaleMultiplier)
                    * Matrix.CreateTranslation(warpXna - eyeXna);

        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.003f, 0.006f, 0.014f));   // deep space
        gd.DepthStencilState = DepthStencilState.Default;

        if (coreOnly)
            _warpRenderer!.DrawCore(world, view, proj);
        else
            _warpRenderer!.Draw(world, view, proj, deltaSeconds: 0.033);

        gd.DepthStencilState = DepthStencilState.None;
        if (_font is not null)
        {
            _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
            string cap = $"{(coreOnly ? "CORE-ONLY " : "")}WARP #{warp.WarpId} — {name}  (radius {warp.Radius}, visual x{WarpRenderer.VisualScaleMultiplier:0.#})";
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
