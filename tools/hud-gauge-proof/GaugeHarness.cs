using Allegiance.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Allegiance.ClientCore.MonoGame.Rendering;

namespace HudGaugeProof;

/// <summary>
/// P395 — render the REAL HudRenderer gauge layout to an offscreen target and
/// save PNGs. Exercises the real production Draw path (real HudRenderer +
/// real GaugeTextures + real spritefonts), with all six gauges lit, so the
/// "horns vs cluster" arrangement can be compared honestly against the native
/// reference image.
/// </summary>
public sealed class GaugeHarness : Game
{
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

    public GaugeHarness(string outDir)
    {
        _graphics = new GraphicsDeviceManager(this);
        _graphics.PreferredBackBufferWidth = Width;
        _graphics.PreferredBackBufferHeight = Height;
        _outDir = outDir;
        IsMouseVisible = false;
        // Absolute content dir: the harness's own Content/ copied to output.
        Content.RootDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
    }

    protected override void Initialize()
    {
        base.Initialize();
    }

    protected override void LoadContent()
    {
        _sb = new SpriteBatch(GraphicsDevice);
        // The real spritefonts, copied from the client's built Content dir.
        _small = Content.Load<SpriteFont>("HUD");
        _big = Content.Load<SpriteFont>("HUDLarge");
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });

        string artwork = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        _gauges = GaugeTextures.Load(GraphicsDevice, artwork);
        Console.WriteLine($"[proof] gauges loaded: {_gauges.LoadedCount}/6");

        _hud = new HudRenderer(_sb, _white, _small, _big, _gauges);

        RenderFrames();
        Exit();
    }

    private void RenderFrames()
    {
        Directory.CreateDirectory(_outDir);

        // A representative moderate flight frame: every gauge mid-lit (so the
        // arcs are visible and the labels readable), throttle mid.
        var frame = new HudFrame(
            HullFraction: 0.95f,
            ShieldFraction: 0.82f,
            AmmoFraction: 0.66f,
            FuelFraction: 0.74f,
            EnergyFraction: 0.88f,
            Power: 1.0f,
            Speed: 360f,
            Throttle: 0.3f,
            AfterburnerActive: false,
            Firing: false,
            GroupFire: true,
            SelectedMount: 0,
            Flashing: false,
            WarpFlash: 0f,
            Mining: false,
            Cloaked: false,
            KillBonusPercent: 0f,
            KillBonusStreak: 0,
            RollDegrees: 0f,
            WeaponSystems: Array.Empty<Allegiance.ClientCore.MonoGame.Flight.WeaponStatusRow>(),
            IsAdriftLifepod: false,
            OxygenSecondsRemaining: 0);

        var target = new RenderTarget2D(GraphicsDevice, Width, Height, false,
            SurfaceFormat.Color, DepthFormat.None);
        GraphicsDevice.SetRenderTarget(target);
        GraphicsDevice.Clear(Color.Black);

        var viewport = new Viewport(0, 0, Width, Height);
        _hud!.Draw(frame, CameraMode.Cockpit, viewport);

        GraphicsDevice.SetRenderTarget(null);
        Save(target, Path.Combine(_outDir, "gauge_layout.png"));
        Console.WriteLine($"[proof] wrote {Path.Combine(_outDir, "gauge_layout.png")}");
    }

    private static void Save(RenderTarget2D target, string path)
    {
        using var fs = File.Create(path);
        target.SaveAsPng(fs, target.Width, target.Height);
    }
}
