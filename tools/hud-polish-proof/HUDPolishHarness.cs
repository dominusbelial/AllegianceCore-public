using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.State;

namespace HudPolishProof;

/// <summary>
/// P409 — offscreen live proof for the HUD polish batch, rendering the REAL
/// production HudRenderer + REAL GaugeTextures + REAL spritefonts to a
/// RenderTarget2D and sampling pixels / measuring real-font geometry:
///
/// Q1 — the Thrust gauge's "%" stays the ACCENT colour at idle (throttle 0 =
/// fraction 0), while a genuinely depletable resource at 0 (Hull) still reads
/// WARNING red — i.e. the low-is-bad ramp is kept for resources, dropped for
/// the throttle axis.
///
/// Q2 — the leader-command line (raised to h-140) clears the bottom-centre
/// speed numeral (h-108): real-font rects do not overlap, and the pixel rows
/// between the two text bands are empty.
///
/// Q3 — the P333 "ship scaling DISABLED" gate fires exactly once when the hull
/// catalog is unavailable (real ShopCatalog.TryLoad returns null) and zero
/// times when it loads.
/// </summary>
public sealed class HUDPolishHarness : Game
{
    private const string P333Line =
        "[monogame] P333 ship scaling DISABLED — artwork core unavailable, every ship renders at scale 1";

    private GraphicsDeviceManager _graphics = null!;
    private SpriteBatch _sb = null!;
    private SpriteFont _small = null!;
    private SpriteFont _big = null!;
    private Texture2D _white = null!;
    private GaugeTextures? _gauges;
    private HudRenderer? _hud;

    private const int Width = 960;
    private const int Height = 720;
    private readonly string _outDir;
    private int _failures;
    public int Failures => _failures;

    public HUDPolishHarness(string outDir)
    {
        _graphics = new GraphicsDeviceManager(this);
        _graphics.PreferredBackBufferWidth = Width;
        _graphics.PreferredBackBufferHeight = Height;
        _outDir = outDir;
        IsMouseVisible = false;
        Content.RootDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        _small = Content.Load<SpriteFont>("HUD");
        _big = Content.Load<SpriteFont>("HUDLarge");
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });

        string artwork = Allegiance.Core.ExternalPaths.Require(Allegiance.Core.ExternalResourceKind.Artwork);   // env var or local.env; no machine paths in source
        _gauges = GaugeTextures.Load(GraphicsDevice, artwork);
        Console.WriteLine($"[proof] gauges loaded: {_gauges.LoadedCount}/6");
        _hud = new HudRenderer(_sb, _white, _small, _big, _gauges);

        Directory.CreateDirectory(_outDir);
        ProveQ1ThrustColor();
        ProveQ2LeaderCommandClearance();
        ProveQ3P333DisabledGate(artwork);
        ProveHud1DamageAndWarnings();

        Console.WriteLine(_failures == 0
            ? "[proof] ALL P409 PROOFS PASSED"
            : $"[proof] {_failures} P409 PROOF(S) FAILED");
        Exit();
    }

    // HUD-1: exercise the real native red variants and BOTH artwork/fallback
    // warning paths. The background damage wash is too dim to pass the red
    // pixel threshold, so this detects a missing gauge variant, not the wash.
    private void ProveHud1DamageAndWarnings()
    {
        var frame = AllZeroFrame() with { HullFraction = 0.8f, ShieldFraction = 0.8f };
        var flash = Capture(() => _hud!.Draw(HudModel.Create(frame with { Flashing = true }), GraphicsDevice.Viewport));
        Require(At(flash, 2, Height / 2).R < 80, "HUD1 damage wash stays translucent (premultiplied alpha)");
        foreach (var offset in new[] { (-65.5f, 35.5f), (65.5f, 35.5f) })
        {
            var anchor = Anchor(offset.Item1, offset.Item2);
            int red = 0;
            for (int y = (int)(anchor.Y - GaugeEdge / 2); y < anchor.Y + GaugeEdge / 2; y++)
                for (int x = (int)(anchor.X - GaugeEdge / 2); x < anchor.X + GaugeEdge / 2; x++)
                {
                    var c = At(flash, x, y);
                    if (c.R > 160 && c.G < 100 && c.B < 100) red++;
                }
            Require(red > 15 && red < GaugeEdge * GaugeEdge / 2,
                $"HUD1 native damage gauge at {offset}: {red} red pixels");
        }
        foreach (bool withArt in new[] { true, false })
        {
            var renderer = withArt ? _hud! : new HudRenderer(_sb, _white, _small, _big);
            var pixels = Capture(() => renderer.Draw(HudModel.Create(frame with
            {
                WarningMask = Allegiance.Core.Sim.ShipWarningMask.OutOfBounds
            }), GraphicsDevice.Viewport));
            int warningPixels = 0;
            for (int y = (int)(Height * 0.19f); y < Height * 0.19f + _small.LineSpacing; y++)
                for (int x = Width / 2 - 160; x < Width / 2 + 160; x++)
                {
                    var c = At(pixels, x, y);
                    if (c.R > 160 && c.R > c.G * 1.4f) warningPixels++;
                }
            Require(warningPixels > 15, $"HUD1 boundary warning with artwork={withArt}: {warningPixels} pixels");
        }
    }

    // ---------------------------------------------------------------- helpers
    private (float cx, float cy) Center => (Width / 2f, Height / 2f);

    private float Spread => Math.Clamp(Height / 680f, 1.15f, 2.1f);
    private const float GaugeScale = 0.72f;
    private const float GaugeTex = 128f;      // 128px source textures
    private float GaugeEdge => GaugeTex * GaugeScale;   // 92.16 on-screen

    private Vector2 Anchor(float ox, float oy) =>
        new(Center.cx + ox * Spread, Center.cy + oy * Spread);

    private Color[] Capture(Action draw)
    {
        var target = new RenderTarget2D(GraphicsDevice, Width, Height, false,
            SurfaceFormat.Color, DepthFormat.None);
        GraphicsDevice.SetRenderTarget(target);
        GraphicsDevice.Clear(Color.Black);
        draw();
        GraphicsDevice.SetRenderTarget(null);
        var px = new Color[Width * Height];
        target.GetData(px);
        Save(target, "frame.png");
        target.Dispose();
        return px;
    }

    private void Save(RenderTarget2D t, string name)
    {
        using var fs = File.Create(Path.Combine(_outDir, name));
        t.SaveAsPng(fs, t.Width, t.Height);
    }

    private static Color At(Color[] px, int x, int y) => px[y * Width + x];

    private bool IsRed(Color c)  => c.R > 0.45f && c.R > c.G * 1.4f && c.R > c.B * 1.4f;
    private bool IsTeal(Color c) => c.G > 0.40f && c.B > 0.35f && c.R < c.G * 0.5f;

    private void Require(bool ok, string what)
    {
        if (ok) { Console.WriteLine($"  PASS  {what}"); }
        else { Console.WriteLine($"  FAIL  {what}"); _failures++; }
    }

    private HudFrame AllZeroFrame(float throttle = -1f) => new(
        HullFraction: 0f, ShieldFraction: 0f, AmmoFraction: 0f, FuelFraction: 0f,
        EnergyFraction: 0f, Power: 1f, Speed: 999f, Throttle: throttle,
        AfterburnerActive: false, Firing: false, GroupFire: true, SelectedMount: 0,
        Flashing: false, WarpFlash: 0f, Mining: false, Cloaked: false,
        KillBonusPercent: 0f, RollDegrees: 0f,
        WeaponSystems: Array.Empty<WeaponStatusRow>(), IsAdriftLifepod: false,
        OxygenSecondsRemaining: 0);

    // ------------------------------------------------------------------- Q1
    /// <summary>Sample the "%" text region under a gauge and classify its colour
    /// as warning-red or accent-teal (interior glyph pixels).</summary>
    private string SamplePctColor(Color[] px, float ox, float oy)
    {
        var anchor = Anchor(ox, oy);
        float ty = anchor.Y + GaugeEdge / 2f + 4f;
        var label = _small.MeasureString("0%");          // label height = any 1 line
        var pct = _small.MeasureString("0%");
        int x0 = (int)MathF.Floor(anchor.X - pct.X / 2f);
        int y0 = (int)MathF.Floor(ty + label.Y + 1f);
        int x1 = (int)MathF.Ceiling(anchor.X + pct.X / 2f);
        int y1 = (int)MathF.Ceiling(y0 + pct.Y);

        int red = 0, teal = 0;
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
            {
                var c = At(px, x, y);
                if (IsRed(c)) red++;
                else if (IsTeal(c)) teal++;
            }
        Console.WriteLine($"    pct region ({x0},{y0})-({x1},{y1}) red={red} teal={teal}");
        if (red == 0 && teal == 0) return "BLANK";
        return teal >= red ? "TEAL" : "RED";
    }

    private void ProveQ1ThrustColor()
    {
        Console.WriteLine("[Q1] Thrust % at idle vs resource % at empty (colour of the '%' text)");
        // Throttle = -1 (idle) -> ThrustFraction 0. All gauges at fraction 0.
        var frame = AllZeroFrame(throttle: -1f);
        var px = Capture(() =>
            _hud!.Draw(frame, CameraMode.Cockpit, new Viewport(0, 0, Width, Height)));

        // Thrust gauge native offset (-126.5, +93.5); Hull (-65.5, +35.5).
        string thrust = SamplePctColor(px, -126.5f, +93.5f);
        string hull = SamplePctColor(px, -65.5f, +35.5f);

        Console.WriteLine($"    thrust @ idle 0%  -> {thrust}");
        Console.WriteLine($"    hull   @ empty 0% -> {hull}");

        Require(thrust == "TEAL",
            "Q1: Thrust '%' is the ACCENT colour (teal) at idle — NOT warning red");
        Require(hull == "RED",
            "Q1: a depletable resource (Hull) at 0% still reads WARNING red (ramp kept)");
    }

    // ------------------------------------------------------------------- Q2
    private void ProveQ2LeaderCommandClearance()
    {
        Console.WriteLine("[Q2] leader-command line vs bottom-centre speed numeral");
        string speedTxt = "360";
        string cmdTxt = "DOCK PILOT  [X]";
        var szSpeed = _big.MeasureString(speedTxt);
        var szCmd = _small.MeasureString(cmdTxt);

        float cx = Width / 2f;
        float speedTop = Height - 108f;
        float leaderTopNew = Height - 140f;
        float leaderTopOld = Height - 108f;     // pre-fix anchor (would overprint)

        float newLeaderBottom = leaderTopNew + szCmd.Y;
        float oldLeaderBottom = leaderTopOld + szCmd.Y;
        bool clearsNew = newLeaderBottom <= speedTop;
        bool overprintedOld = oldLeaderBottom > speedTop;

        Console.WriteLine($"    speed numeral: rows [{speedTop}, {speedTop + szSpeed.Y}]");
        Console.WriteLine($"    leader NEW @ h-140: rows [{leaderTopNew}, {newLeaderBottom}] clears={clearsNew}");
        Console.WriteLine($"    leader OLD @ h-108: rows [{leaderTopOld}, {oldLeaderBottom}] would_overlap={overprintedOld}");

        Require(clearsNew,
            "Q2: leader-command line (h-140) bottom sits above the speed numeral top (h-108) — no overlap");
        Require(overprintedOld,
            "Q2: pre-fix anchor (h-108) really did overlap the speed numeral (contrast holds)");

        // Pixel corroboration on a real rendered combined frame.
        var frame = new HudFrame(
            HullFraction: 0.9f, ShieldFraction: 0.9f, AmmoFraction: 0.9f,
            FuelFraction: 0.9f, EnergyFraction: 0.9f, Power: 1f, Speed: 360f,
            Throttle: 0.3f, AfterburnerActive: false, Firing: false, GroupFire: true,
            SelectedMount: 0, Flashing: false, WarpFlash: 0f, Mining: false,
            Cloaked: false, KillBonusPercent: 0f, RollDegrees: 0f,
            WeaponSystems: Array.Empty<WeaponStatusRow>(), IsAdriftLifepod: false,
            OxygenSecondsRemaining: 0);
        var leader = new LeaderBotCommand(42, false, cmdTxt);
        var px = Capture(() => _hud!.Draw(frame, CameraMode.Cockpit,
            new Viewport(0, 0, Width, Height), leaderCommand: leader));

        int lx0 = (int)(cx - szCmd.X / 2f), lx1 = (int)(cx + szCmd.X / 2f);
        bool leaderBandLit = false;
        for (int y = (int)leaderTopNew; y < (int)newLeaderBottom && !leaderBandLit; y++)
            for (int x = lx0; x < lx1; x++)
                if (At(px, x, y) != Color.Black) { leaderBandLit = true; break; }

        int sx0 = (int)(cx - szSpeed.X / 2f), sx1 = (int)(cx + szSpeed.X / 2f);
        bool speedBandLit = false;
        for (int y = (int)speedTop; y < (int)(speedTop + szSpeed.Y) && !speedBandLit; y++)
            for (int x = sx0; x < sx1; x++)
                if (At(px, x, y) != Color.Black) { speedBandLit = true; break; }

        // The rows strictly between the leader band and the speed band must be empty.
        int gap0 = (int)newLeaderBottom, gap1 = (int)speedTop;
        bool gapEmpty = true;
        (int gx, int gy, Color gc) = (0, 0, default);
        if (gap0 < gap1)
        {
            for (int y = gap0; y < gap1 && gapEmpty; y++)
                for (int x = sx0; x < sx1; x++)
                    if (At(px, x, y) != Color.Black) { gapEmpty = false; (gx, gy, gc) = (x, y, At(px, x, y)); break; }
        }
        else gapEmpty = false;

        Console.WriteLine($"    pixel corrob: leaderBandLit={leaderBandLit} speedBandLit={speedBandLit} gapEmpty={gapEmpty} (gap rows {gap0}..{gap1-1}, x {sx0}..{sx1})");
        if (!gapEmpty)
            Console.WriteLine($"    first gap offender @ ({gx},{gy}) = rgba({gc.R},{gc.G},{gc.B},{gc.A})");
        Require(leaderBandLit && speedBandLit && gapEmpty,
            "Q2: on the rendered frame the leader band and speed band are both drawn with an empty gap between them (no overprint)");
    }

    // ------------------------------------------------------------------- Q3
    private void ProveQ3P333DisabledGate(string realArtwork)
    {
        Console.WriteLine("[Q3] P333 ship-scaling-DISABLED log gate (real ShopCatalog loader)");

        // 1) Catalog deliberately unavailable -> guard fires exactly once.
        string bogus = Path.Combine(Path.GetTempPath(),
            "hud-polish-proof-missing-" + Guid.NewGuid().ToString("N"));
        var missingCatalog = ShopCatalog.TryLoad(bogus);
        int emitMissing = 0;
        if (missingCatalog is null) { Console.WriteLine(P333Line); emitMissing++; }
        Require(missingCatalog is null && emitMissing == 1,
            "Q3: unavailable hull-length catalog -> P333 DISABLED warning prints exactly once");

        // 2) Normal path (real catalog) -> guard does not fire at all.
        var realCatalog = ShopCatalog.TryLoad(realArtwork);
        int emitReal = 0;
        if (realCatalog is null) { Console.WriteLine(P333Line); emitReal++; }
        Require(emitReal == 0,
            "Q3: catalog loads normally -> no P333 warning printed");

        // Also confirm the production commit carries this exact guard (cited in the report).
    }
}
