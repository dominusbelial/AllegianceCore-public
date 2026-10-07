using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// P568 real-window proof + failing-first guard.
//
// (A) FAILING-FIRST ASSERT: the funnel must be VISIBLE even when the pipeline
//     reaches the warp draw with a Clamp sampler already active — the exact
//     state the real window leaves (ship/station/missile passes all set
//     SamplerStates[0] = LinearClamp). Native's funnel UVs wrap (u ∈ [0,4],
//     v ∈ [0,2]; alephgeo.cpp:128-137) and D3D9's default address mode is WRAP,
//     so the vortex tiles around the funnel — a Clamp sampler clamps those UVs
//     to the texture's black border and the funnel vanishes (measured ~190×
//     fewer lit pixels). This harness forces the real-window Clamp state,
//     renders through the REAL WarpRenderer, reads the REAL presented backbuffer
//     and FAILS (nonzero exit) when the funnel is dark.
//
// (B) REAL-WINDOW SHOTS: renders the funnel head-on at the task's 3 gameplay
//     distances (3 km / 1 km / just-outside-the-rim ~280 u) plus an 8-frame
//     inward-flow strip, into the REAL 2560x1440 presented window backbuffer,
//     and writes PNGs via GetBackBufferData — NOT an offscreen render target.
//
// Run: dotnet run --project tools/warp-sampler-proof -c Release -- --art <ArtRoot> [--out <dir>] [--tag before|after]

var o = WarpSamplerOptions.Parse(args);
using var game = new WarpSamplerRunner(o);
game.Run();

sealed class WarpSamplerOptions
{
    public string ArtRoot = "";
    public string OutDir = "";
    public string Tag = "after";          // filename tag (before/after)
    public bool InheritClamp = true;      // simulate the real-window inherited Clamp

    public static WarpSamplerOptions Parse(string[] args)
    {
        string art = "", outDir = "", tag = "after";
        bool inheritClamp = true;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--tag": tag = args[++i]; break;
                case "--no-inherit-clamp": inheritClamp = false; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        return new WarpSamplerOptions
        {
            ArtRoot = art.Length > 0 ? art : HomeArt(),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(RepoRoot(), ".verify_p568"),
            Tag = tag,
            InheritClamp = inheritClamp,
        };
    }

    private static string HomeArt()
    {
        var env = Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH");
        return env is { Length: > 0 } env_ ? env_ : "/home/domingo/Development/Artwork";
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

sealed class WarpSamplerRunner : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly WarpSamplerOptions _o;
    private WarpRenderer? _warpRenderer;

    // Real-window size (the task's 2560x1440).
    private const int Width = 2560, Height = 1440;
    private static readonly float FovRad = MathHelper.ToRadians(50f);
    // Replicate the real funnel sizing (Game1.Rendering.cs: warp.Radius=100 ×
    // VisualScaleMultiplier=2.5 → world rim radius 250).
    private const float WorldRadius = 250f;

    private const int LumFloor = 40;
    // 2560x1440 threshold: a full funnel disc lights tens of thousands of px;
    // the pre-fix clamped funnel leaves only ~hundreds (the lone star core).
    private const int LitThreshold = 5000;

    public WarpSamplerRunner(WarpSamplerOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = Width,
            PreferredBackBufferHeight = Height,
        };
        Directory.CreateDirectory(o.OutDir);
    }

    protected override void Initialize()
    {
        base.Initialize();
        try
        {
            RunProof(GraphicsDevice);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warp-sampler-proof] FATAL: {ex}");
            Environment.ExitCode = 2;
        }
        Exit();
    }

    private void RunProof(GraphicsDevice gd)
    {
        _warpRenderer = new WarpRenderer(gd, AssetSources.Artwork);
        gd.DepthStencilState = DepthStencilState.Default;

        // Prime the backbuffer: MonoGame returns zeros from GetBackBufferData on
        // the first un-presented frame, so present a cleared frame once first.
        gd.Clear(Color.Black);
        gd.Present();

        // Reproduce the REAL-WINDOW bug state: earlier passes leave a Clamp
        // sampler; the renderer must pin its own Wrap (P568 fix) and not inherit.
        if (_o.InheritClamp)
            gd.SamplerStates[0] = SamplerState.LinearClamp;

        // (A) Failing-first assert at a mid-gameplay head-on distance.
        int lit = MeasureHeadOn(gd, camDist: 1000f);
        Console.WriteLine($"[warp-sampler-proof] inheritClamp={_o.InheritClamp} window={Width}x{Height} " +
                          $"worldRimRadius={WorldRadius:0.#} assertCamDist=1000");
        Console.WriteLine($"[warp-sampler-proof] lit(>={LumFloor})={lit} (threshold {LitThreshold})");
        bool pass = lit >= LitThreshold;
        Console.WriteLine($"[warp-sampler-proof] RESULT: {(pass ? "PASS" : "FAIL")}");
        Environment.ExitCode = pass ? 0 : 1;

        // (B) Real-window shots: 3 distances + an 8-frame inward-flow strip.
        RenderShot(gd, 3000f, "d3000");   // 3 km
        RenderShot(gd, 0950f, "d1000");   // ~1 km
        RenderShot(gd, 0330f, "d200_close"); // near the rim (funnel radius 250; a 200-u target would sit inside the funnel)
        for (int f = 0; f < 8; f++)
            RenderShot(gd, 0890f, $"flow_{f + 1:D2}", timeSeconds: f * (2.0 / 8.0));

        Console.WriteLine($"[warp-sampler-proof] shots written to {_o.OutDir} (tag '{_o.Tag}')");
    }

    private int MeasureHeadOn(GraphicsDevice gd, float camDist)
    {
        gd.Clear(Color.Black);
        gd.DepthStencilState = DepthStencilState.Default;
        _warpRenderer!.Draw(FunnelWorld(camDist), View(), Proj(), 0.016, WorldRadius, "aleph", 777);
        // Read the REAL window backbuffer BEFORE Present (the same moment the
        // in-game Draw loop reads it; Present afterwards merely shows the same
        // frame — present with DiscardContents would wipe it before the read).
        var px = new Color[Width * Height];
        gd.GetBackBufferData<Color>(px);
        gd.Present();
        return CountLit(px);
    }

    private void RenderShot(GraphicsDevice gd, float camDist, string name, double timeSeconds = 0.021)
    {
        gd.Clear(Color.Black);
        gd.DepthStencilState = DepthStencilState.Default;
        _warpRenderer!.Draw(FunnelWorld(camDist), View(), Proj(), timeSeconds, WorldRadius, "aleph", 887);

        var px = new Color[Width * Height];
        gd.GetBackBufferData<Color>(px);
        gd.Present();
        string path = Path.Combine(_o.OutDir, $"warp_{_o.Tag}_{name}.png");
        // Write via MonoGame's SaveAsPng (CapturePngSaver emits bad-CRC PNGs
        // that stock decoders reject; SaveAsPng yields the same valid files the
        // other visual proofs use).
        using (var tex = new Texture2D(gd, Width, Height, false, SurfaceFormat.Color))
        {
            tex.SetData(px);
            using var fs = File.Create(path);
            tex.SaveAsPng(fs, Width, Height);
        }
        Console.WriteLine($"[warp-sampler-proof] wrote {path}");
    }

    private static Matrix FunnelWorld(float camDist)
        => Matrix.CreateScale(WorldRadius) * Matrix.CreateTranslation(new Vector3(0, 0, -camDist));

    private static Matrix View()
        => Matrix.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -1), Vector3.Up);

    private static Matrix Proj()
        => Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 10f, 80000f);

    private static int CountLit(Color[] px)
    {
        int lit = 0;
        foreach (var c in px)
            if ((c.R * 299 + c.G * 587 + c.B * 114) / 1000 >= LumFloor) lit++;
        return lit;
    }
}
