// soft-frame-proof — P559: deterministically reproduce + verify the soft-particle
// depth-pass frame bug against the REAL backbuffer path.
//
// Two root causes (see docs/reports/P559_SOFT_PARTICLE_GEOMETRY_FIX.md):
//  1) The old P547 depth pass ran MID-FRAME: it switched the active render
//     target away from the color target (backbuffer / SceneRT / _capTarget) and
//     back. With MonoGame's RenderTargetUsage.DiscardContents the opaque scene
//     drawn before the switch (ships/asteroids/stations) was wiped; only what
//     was drawn after (particles/lights/HUD) survived. This harness draws a real
//     ship through the REAL renderers into the REAL backbuffer in both orderings
//     and reads the backbuffer back with GraphicsDevice.GetBackBufferData.
//  2) ShipRenderer.DrawDepth permanently replaced each mesh part's Effect with
//     the depth shader, never restoring the BasicEffect. This harness calls
//     DrawDepth and asserts every part.Effect is unchanged.
//
// Controls are included so the harness distinguishes "backbuffer not readable"
// from "geometry genuinely absent": a bright-clear backbuffer probe, and an
// offscreen-target render of the same ship (the renderer is proven to paint).
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core.Rendering;
using XnaModel = Microsoft.Xna.Framework.Graphics.Model;
using XnaVec = Microsoft.Xna.Framework.Vector3;
using SimVec = Allegiance.Core.Collision.Vector3;

internal sealed class SoftFrameProof : Game
{
    private readonly GraphicsDeviceManager _gdm;
    private readonly string _contentRoot;
    private ShipRenderer _ship = null!;
    private VfxPostProcessor _post = null!;
    private XnaModel _fighter = null!;
    private float _shipScale = 1f;
    private Vector3 _localCenter = Vector3.Zero;
    private Texture2D _tex = null!;

    public const int W = 1280, H = 720;
    private static readonly Color Bg = new(0.020f, 0.023f, 0.032f);   // near-black scene bg
    private static readonly Color Probe = Color.White;

    private static readonly SectorLighting Lit = new(
        new SimVec(0f, 0.5f, 1f), new SimVec(1f, 0.80f, 0.55f),
        new SimVec(0f, 0f, -1f), SectorLighting.AltColorConst,
        new SimVec(0.25f, 0.25f, 0.25f));

    public SoftFrameProof(string contentRoot)
    {
        _contentRoot = contentRoot;
        _gdm = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = W,
            PreferredBackBufferHeight = H,
        };
        Content.RootDirectory = contentRoot;
    }

    protected override void Initialize()
    {
        base.Initialize();
        GraphicsDevice.BlendState = BlendState.Opaque;
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;

        var shaders = ShaderAssetCache.ForDevice(GraphicsDevice);
        _post = new VfxPostProcessor(GraphicsDevice, shaders);
        _post.SetSize(W, H);
        _ship = new ShipRenderer(GraphicsDevice);

        _fighter = Content.Load<XnaModel>("Models/gt-fighter");
        ShipOrientationApply.Apply(_fighter, "gt-fighter");
        _shipScale = 26f / RenderRadius(_fighter);
        _localCenter = ModelLocalCenter(_fighter);
        try { _tex = Content.Load<Texture2D>("Textures/ships/gt-fighter"); } catch { }

        Console.WriteLine($"[soft-frame-proof] depthEffect={_post.LinearDepthEffect!=null} softReady={_post.SoftReady} shaderFailures={shaders.LoggedFailureCount} shipScale={_shipScale:0.###}");
    }

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

    private static Vector3 ModelLocalCenter(XnaModel m)
    {
        var acc = Vector3.Zero; int n = 0;
        foreach (var mesh in m.Meshes)
        {
            acc += Vector3.Transform(mesh.BoundingSphere.Center, mesh.ParentBone.Transform);
            n++;
        }
        return n == 0 ? Vector3.Zero : acc / n;
    }

    private (Matrix view, Matrix proj, Vector3 eye) Cameras()
    {
        var eye = new Vector3(0f, 26f * 0.3f, 26f * 2.2f);
        var view = Matrix.CreateLookAt(eye, new Vector3(0f, 0f, 0f), Vector3.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(45f), W / (float)H, 1f, 26f * 8f);
        return (view, proj, eye);
    }

    private Matrix ShipWorld(Vector3 eye) =>
        Matrix.CreateTranslation(-_localCenter) * Matrix.CreateTranslation(-eye);

    private static (int bright, int near) Count(Color[] bb)
    {
        int bright = 0, near = 0;
        for (int i = 0; i < bb.Length; i++)
        {
            var c = bb[i];
            int lum = (c.R + c.G + c.B) / 3;
            if (lum > 24) bright++;
            if (lum > 8) near++;
        }
        return (bright, near);
    }

    private static void Report(string label, Color[] bb, string expect)
    {
        var (bright, near) = Count(bb);
        bool present = near > 1000;
        string verdict = expect switch
        {
            "present" => present ? "PASS (geometry present)" : "FAIL (geometry absent)",
            "absent"  => present ? "FAIL (geometry present, unexpected)" : "PASS (geometry absent)",
            _         => present ? "PRESENT" : "ABSENT",
        };
        Console.WriteLine($"  [{label}] brightPx={bright} nearPx={near} => {verdict}");
    }

    protected override void Draw(GameTime gameTime)
    {
        var (view, proj, eye) = Cameras();
        var world = ShipWorld(eye);
        var depthEffect = _post.LinearDepthEffect!;

        // ── Root cause 2 snapshot: capture each part's ORIGINAL effect BEFORE
        // any DrawDepth call, so the old bug (permanent part.Effect replacement)
        // is detectable. Verified after every sequence below.
        var before = new Effect[_fighter.Meshes.Sum(mm => mm.MeshParts.Count)];
        int snapIdx = 0;
        foreach (var mm in _fighter.Meshes)
            foreach (var part in mm.MeshParts)
                before[snapIdx++] = part.Effect;

        // ── Control 1: offscreen-target render of the ship (proves the renderer
        // paints geometry — isolates "renderer" from "backbuffer").
        using (var rt = new RenderTarget2D(GraphicsDevice, W, H, false, SurfaceFormat.Color, DepthFormat.Depth24))
        {
            GraphicsDevice.SetRenderTarget(rt);
            GraphicsDevice.Clear(Bg);
            _ship.Draw(_fighter, world, view, proj, 1f, _shipScale, _tex, Lit);
            GraphicsDevice.SetRenderTarget(null);
            var off = new Color[W * H];
            rt.GetData(off);
            Console.WriteLine("[soft-frame-proof] control: offscreen-target ship render");
            Report("off-render", off, "present");
        }

        // ── Control 2: bright-clear the BACKBUFFER then read it back. If this
        // returns ~0, the headless device's backbuffer is not readable here and
        // the sequence tests below can't measure the real backbuffer.
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Probe);
        var bbProbe = new Color[W * H];
        GraphicsDevice.GetBackBufferData(bbProbe);
        int probeBright = Count(bbProbe).bright;
        // Any bright pixel from a white clear proves GetBackBufferData returns
        // real device content (the strongest proof is sequence B below, which
        // reads 10k+ ship pixels); the headless default framebuffer can expose
        // a different size, so we only require it to be non-empty here.
        bool bbReadable = probeBright > 0;
        Console.WriteLine($"[soft-frame-proof] control: backbuffer bright-clear probe => brightPx={probeBright} ({(bbReadable ? "READABLE" : "NOT READABLE here")})");

        // ── Sequence A: OLD P547 ordering — opaque scene into the backbuffer,
        // then switch away to the depth target and back mid-frame.
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Bg);
        _ship.Draw(_fighter, world, view, proj, 1f, _shipScale, _tex, Lit);
        GraphicsDevice.SetRenderTarget(_post.DepthRT);
        GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, VfxPostProcessor.DepthClearFar, 1f, 0);
        _ship.DrawDepth(_fighter, world, view, proj, _shipScale, depthEffect, 1f, 6000f);
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.BlendState = BlendState.Opaque;
        var bbOld = new Color[W * H];
        GraphicsDevice.GetBackBufferData(bbOld);
        Console.WriteLine("[soft-frame-proof] sequence A (OLD mid-frame switch) backbuffer:");
        Report("A-old", bbOld, "absent");

        // ── Sequence B: NEW P559 ordering — depth pass FIRST, then set the color
        // target once and draw; never switch away mid-frame.
        GraphicsDevice.SetRenderTarget(_post.DepthRT);
        GraphicsDevice.Clear(ClearOptions.Target | ClearOptions.DepthBuffer, VfxPostProcessor.DepthClearFar, 1f, 0);
        _ship.DrawDepth(_fighter, world, view, proj, _shipScale, depthEffect, 1f, 6000f);
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Clear(Bg);
        _ship.Draw(_fighter, world, view, proj, 1f, _shipScale, _tex, Lit);
        GraphicsDevice.BlendState = BlendState.Opaque;
        var bbNew = new Color[W * H];
        GraphicsDevice.GetBackBufferData(bbNew);
        Console.WriteLine("[soft-frame-proof] sequence B (NEW depth-first) backbuffer:");
        Report("B-new", bbNew, "present");

        // ── Root cause 2: DrawDepth must NOT mutate part.Effect. `before` was
        // snapshotted at the top of the frame, before ANY DrawDepth ran.
        _ship.DrawDepth(_fighter, world, view, proj, _shipScale, depthEffect, 1f, 6000f);
        int mutated = 0; int idx = 0;
        foreach (var mm in _fighter.Meshes)
            foreach (var part in mm.MeshParts)
                if (!ReferenceEquals(part.Effect, before[idx++])) mutated++;
        bool partOk = mutated == 0;
        Console.WriteLine($"  [effects] DrawDepth mutated {mutated}/{before.Length} part.Effect refs => {(partOk ? "PASS (none mutated)" : "FAIL (effects replaced)")}");

        var (_, nA) = Count(bbOld);
        var (_, nB) = Count(bbNew);
        bool oldKept = nA > 1000;
        bool newKept = nB > 1000;
        Console.WriteLine();
        if (bbReadable && !oldKept && newKept && partOk)
            Console.WriteLine("[soft-frame-proof] RESULT: PASS  (old loses geometry, new keeps it, part.Effect untouched)");
        else
            Console.WriteLine($"[soft-frame-proof] RESULT: {(bbReadable && !oldKept && newKept && partOk ? "PASS" : "INCONCLUSIVE/FAIL")}  (bbReadable={bbReadable} oldKept={oldKept} newKept={newKept} partOk={partOk})");
        Exit();
    }
}

internal static class Program
{
    private static string ResolveContentRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) break;
            dir = dir.Parent;
        }
        var root = dir?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "bin", "Debug", "net10.0", "Content");
    }

    private static void Main()
    {
        using var game = new SoftFrameProof(ResolveContentRoot());
        game.Run();
    }
}
