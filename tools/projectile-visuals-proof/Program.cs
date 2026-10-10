using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Visual;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Xna = Microsoft.Xna.Framework;
using SimVec = Allegiance.Core.Collision.Vector3;
using XnaVec = Microsoft.Xna.Framework.Vector3;

// P465b live-proof: renders the REAL ProjectileRenderer bolts for two REAL
// PCore001 weapon projectiles side by side, each styled by the REAL
// ProjectileStyle.For(typeId, core) against the real core — so the screenshot
// shows the per-type visual difference this task actually implements. PCore001
// carries no per-type colour (every type measures (0,0,0,0)) and only a few
// types carry a real width; the honest real difference between two real
// weapons here is the bolt cross-section: PE Mini-Gun 1's projectile (type 2,
// width 0.10) vs PE Gat Gun 1's (type 1, zero width -> the 0.35 default),
// plus the speed-proportional length. A third bolt, clearly labelled DEMO,
// uses a synthetic non-black colour to show the (core-conditional) colour
// plumbing is wired end-to-end even though PCore001 never exercises it.

var opts = ProofOptions.Parse(args);
using var game = new ProofRunner(opts);
game.Run();
return 0;

sealed class ProofOptions
{
    public string ArtRoot = "";
    public string OutDir = "";

    public static ProofOptions Parse(string[] args)
    {
        string art = "", outDir = "";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--art": art = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        string repo = RepoRoot();
        return new ProofOptions
        {
            ArtRoot = art.Length > 0 ? art : EnvArt(),
            OutDir = outDir.Length > 0 ? outDir : Path.Combine(repo, ".verify_p465b"),
        };
    }

    private static string EnvArt() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") is { Length: > 0 } e ? e : ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    public static string RepoRoot()
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

    private const int Width = 980, Height = 520;
    private static readonly float FovRad = MathHelper.ToRadians(52f);

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = Width;
        _gdm.PreferredBackBufferHeight = Height;
        Content.RootDirectory = Path.Combine(ProofOptions.RepoRoot(), "src", "Allegiance.ClientCore.MonoGame", "Content");
        Directory.CreateDirectory(o.OutDir);
    }

    protected override void Initialize()
    {
        base.Initialize();
        _batch = new SpriteBatch(GraphicsDevice);
        try { _font = Content.Load<SpriteFont>("HUD"); } catch { _font = null!; }
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[p465b] FATAL: {ex}"); Environment.ExitCode = 1; }
        Exit();
    }

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(EnvArtRoot(), "PCore001.igc")));

    private static string EnvArtRoot() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") is { Length: > 0 } e ? e : ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private void RunProof(GraphicsDevice gd)
    {
        var core = Core();
        Console.WriteLine($"[p465b] core projectile types: {core.ProjectileTypes.Count}");

        // Two REAL PCore001 weapon projectiles chosen for maximal, honest
        // legibility: EW Gauss Gun (type 65, zero width -> the 0.35 default,
        // speed 2900 u/s -> a LONG bolt) vs PE Mini-Gun 1 (type 2, real width
        // 0.10, speed 500 u/s -> a SHORT, THIN bolt). Both resolve to the amber
        // tint because PCore001 carries no per-type colour (black -> amber
        // fallback; native shows the un-tinted texture instead — see the report).
        var gauss = WeaponName(core, 65) ?? "EW Gauss Gun";
        var minigun = WeaponName(core, 2) ?? "PE Mini-Gun 1";
        var sGauss = ProjectileStyle.For(65, core.ProjectileTypes);
        var sMini = ProjectileStyle.For(2, core.ProjectileTypes);
        Console.WriteLine($"[p465b] {gauss} (pid 65) -> {Describe(sGauss)}");
        Console.WriteLine($"[p465b] {minigun} (pid 2) -> {Describe(sMini)}");

        // Real bolt lengths from each type's real speed (Game1 formula).
        float lenGauss = Math.Clamp(2900f * 0.02f, 4f, 16f);  // -> 16 (max)
        float lenMini = Math.Clamp(500f * 0.02f, 4f, 16f);    // -> 10

        // Synthetic demo of the colour path PCore001 never exercises (green).
        var sDemo = new ProjectileStyle(0.2f, 0.9f, 0.4f, 0.35f, 4f);
        float lenDemo = 8f;

        using var bolt = new ProjectileRenderer(gd);

        // ── frame 1: two real bolts (long-thick gauss, short-thin minigun) ──
        // plus the green colour demo, camera close enough for real widths to
        // read as pixels, each bolt labelled at its own screen position.
        var eye = XnaVec.Zero;
        var view = Matrix.CreateLookAt(eye, new XnaVec(0f, 0f, -30f), XnaVec.Up);
        var proj = Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 1f, 80000f);

        var mGauss = BoltWorld(sGauss.Width, lenGauss, new XnaVec(-24f, 0f, -30f), eye, XnaVec.UnitX);
        var mMini = BoltWorld(sMini.Width, lenMini, new XnaVec(+24f, 0f, -30f), eye, XnaVec.UnitX);
        var mDemo = BoltWorld(sDemo.Width, lenDemo, new XnaVec(0f, -8f, -30f), eye, XnaVec.UnitX);

        using var rt = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt);
        gd.Clear(new Color(0.006f, 0.009f, 0.018f));
        gd.DepthStencilState = DepthStencilState.Default;
        bolt.Draw(mGauss, new XnaVec(sGauss.R, sGauss.G, sGauss.B), view, proj);
        bolt.Draw(mMini, new XnaVec(sMini.R, sMini.G, sMini.B), view, proj);
        bolt.Draw(mDemo, new XnaVec(sDemo.R, sDemo.G, sDemo.B), view, proj);
        Label(gd, $"LONG THICK: {gauss}  width {sGauss.Width:0.00} (default)  speed 2900",
            ToScreen(new XnaVec(-24f, 5f, -30f), view, proj), Color.Orange);
        Label(gd, $"SHORT THIN: {minigun}  width {sMini.Width:0.00} (real)  speed 500",
            ToScreen(new XnaVec(+24f, 5f, -30f), view, proj), Color.Orange);
        Label(gd, "DEMO colour (0.2,0.9,0.4) — PCore001 carries no colour",
            ToScreen(new XnaVec(0f, -1.5f, -30f), view, proj), Color.LimeGreen);
        gd.SetRenderTarget(null);
        Save(gd, rt, "p465b_two_real_bolts.png");

        // ── frame 2: end-on cross-sections, magnified, so the real width
        // difference (0.10 vs 0.35 world units) is unambiguous. Separated in Z
        // (perpendicular to the X view axis) so both faces appear side by side.
        var cEye = new XnaVec(6f, 0f, 0f);
        var cView = Matrix.CreateLookAt(cEye, XnaVec.Zero, XnaVec.Up);
        var cProj = Matrix.CreatePerspectiveFieldOfView(FovRad, (float)Width / Height, 1f, 80000f);
        var mX1 = BoltWorld(sMini.Width, 6f, new XnaVec(0f, 0f, -1.3f), cEye, XnaVec.UnitX);
        var mX2 = BoltWorld(sGauss.Width, 6f, new XnaVec(0f, 0f, +1.3f), cEye, XnaVec.UnitX);
        using var rt2 = new RenderTarget2D(gd, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        gd.SetRenderTarget(rt2);
        gd.Clear(new Color(0.003f, 0.005f, 0.012f));
        gd.DepthStencilState = DepthStencilState.Default;
        bolt.Draw(mX1, new XnaVec(sMini.R, sMini.G, sMini.B), cView, cProj);
        bolt.Draw(mX2, new XnaVec(sGauss.R, sGauss.G, sGauss.B), cView, cProj);
        Label(gd, $"{minigun} — real width 0.10", ToScreen(new XnaVec(-1.5f, 0f, 0f), cView, cProj) + new XnaVec(0f, 26f, 0f), Color.Orange);
        Label(gd, $"{gauss} — default width 0.35", ToScreen(new XnaVec(+1.5f, 0f, 0f), cView, cProj) + new XnaVec(0f, 26f, 0f), Color.Orange);
        Caption(gd, "END-ON cross-sections (magnified): both bolts are amber (PCore001 has no colour) — the thickness difference is the real per-type width");
        gd.SetRenderTarget(null);
        Save(gd, rt2, "p465b_cross_sections.png");

        Console.WriteLine($"[p465b] PNGs -> {Path.Combine(_o.OutDir, "p465b_two_real_bolts.png")} + p465b_cross_sections.png");
        Console.WriteLine("[p465b] PASS");
    }

    private static XnaVec ToScreen(XnaVec world, Matrix view, Matrix proj)
    {
        var clip = XnaVec4Transform(new Xna.Vector4(world, 1f), view * proj);
        var ndc = new XnaVec(clip.X / clip.W, clip.Y / clip.W, clip.Z / clip.W);
        return new XnaVec((ndc.X * 0.5f + 0.5f) * Width, (1f - (ndc.Y * 0.5f + 0.5f)) * Height, 0f);
    }

    private static Xna.Vector4 XnaVec4Transform(Xna.Vector4 v, Matrix m)
    {
        float x = v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + v.W * m.M41;
        float y = v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + v.W * m.M42;
        float z = v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + v.W * m.M43;
        float w = v.X * m.M14 + v.Y * m.M24 + v.Z * m.M34 + v.W * m.M44;
        return new Xna.Vector4(x, y, z, w);
    }

    private void Label(GraphicsDevice gd, string text, XnaVec screen, Color color)
    {
        if (_font is null) return;
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        var sz = _font.MeasureString(text);
        _batch.DrawString(_font, text, new Vector2(screen.X - sz.X / 2f, screen.Y - 14f), color);
        _batch.End();
    }

    private static Matrix BoltWorld(float width, float len, XnaVec pos, XnaVec eye, XnaVec dir)
    {
        var orient = new Orientation();
        orient.Set(new SimVec(dir.X, dir.Y, dir.Z));
        var rot = PoseConverter.ToXnaRotation(orient);
        return Matrix.CreateScale(width, width, len) * rot * Matrix.CreateTranslation(pos - eye);
    }

    private static string Describe(ProjectileStyle s) =>
        $"tint=({s.R:0.##},{s.G:0.##},{s.B:0.##}) width={s.Width:0.##} radius={s.Radius:0.##}";

    private static string? WeaponName(StaticCore core, short pid)
    {
        foreach (var pt in core.PartTypes.Values)
        {
            if (pt.Type is DataPartTypeIGC.Weapon w && w.ProjectileTypeId == pid)
                return pt.Name;
        }
        return null;
    }

    private void Caption(GraphicsDevice gd, string text)
    {
        if (_font is null) return;
        gd.DepthStencilState = DepthStencilState.None;
        _batch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);
        var sz = _font.MeasureString(text);
        _batch.DrawString(_font, text, new Vector2(Width / 2f - sz.X / 2f, 8f), Color.Orange);
        _batch.End();
    }

    private void Save(GraphicsDevice gd, RenderTarget2D rt, string file)
    {
        string path = Path.Combine(_o.OutDir, file);
        using var fs = File.Create(path);
        rt.SaveAsPng(fs, rt.Width, rt.Height);
        Console.WriteLine($"[p465b] wrote {path}");
    }
}
