using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using var proof = new PresentationProof(args.FirstOrDefault() ?? ".scratch/p1075/desktop");
proof.Run();

sealed class PresentationProof : Game
{
    private readonly string _output;
    public PresentationProof(string output)
    {
        _output = output;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
        IsFixedTimeStep = false;
    }
    protected override void LoadContent()
    {
        Directory.CreateDirectory(_output);
        var assets = new DirectoryAssetSource(ExternalPaths.Require(ExternalResourceKind.Artwork));
        Content.RootDirectory = Path.Combine(Directory.GetCurrentDirectory(), "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
        var small = Content.Load<SpriteFont>("HUD"); var big = Content.Load<SpriteFont>("HUDLarge");
        var outline = Content.Load<SpriteFont>("HUDModern");
        var fonts = new NativeFonts(Enum.GetValues<NativeFont>().ToDictionary(font => font,
            font => (Content.Load<SpriteFont>(NativeFontCatalog.Spec(font).Asset1x), Content.Load<SpriteFont>(NativeFontCatalog.Spec(font).Asset2x))));
        using var batch = new SpriteBatch(GraphicsDevice);
        using var white = new Texture2D(GraphicsDevice, 1, 1); white.SetData(new[] { Color.White });
        using var hangar = new HangarScreenRenderer(batch, white, small, fonts, GraphicsDevice, assets);
        var classic = new HudRenderer(batch, white, small, big, nativeFonts: fonts);
        using var modern = new ModernHudRenderer(batch, white, small, big, new() { Style = HudStyle.Modern }, outlineSource: outline);
        if (!hangar.CanDrawNative) throw new InvalidDataException("Native hangar art is required.");
        foreach (var style in new[] { HudStyle.Classic, HudStyle.Modern })
        {
            foreach (var (width, height) in new[] { (1920, 1080), (1280, 800), (800, 480) })
            {
                using var target = new RenderTarget2D(GraphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.Depth24);
                GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(Color.Black);
                hangar.Draw(GraphicsDevice.Viewport, HangarAction.None, new HangarLaunchAnimation(), null, true);
                Save(target, $"desktop-hangar-{style.ToString().ToLowerInvariant()}-{width}x{height}.png");
            }
            using var hudTarget = new RenderTarget2D(GraphicsDevice, 1280, 800, false, SurfaceFormat.Color, DepthFormat.Depth24);
            GraphicsDevice.SetRenderTarget(hudTarget); GraphicsDevice.Clear(new Color(2, 4, 9));
            var frame = new HudFrame(1, 1, 1, 1, 1, 0, 0, -1, true, false, true, 0, false, 0, false, false, 0, 0, [], false, 0);
            var model = HudModel.Create(frame);
            if (model.Telemetry!.Value.AfterburnerActive) throw new InvalidOperationException("Button without power cannot show ACTIVE.");
            (style == HudStyle.Classic ? classic : modern).Draw(model, GraphicsDevice.Viewport);
            Save(hudTarget, $"desktop-afterburner-{style.ToString().ToLowerInvariant()}.png");
        }
        GraphicsDevice.SetRenderTarget(null);
        Console.WriteLine("P1075_DESKTOP_PRESENTATION_PASS captures=8");
        Exit();
    }
    private void Save(RenderTarget2D target, string name)
    {
        GraphicsDevice.SetRenderTarget(null);
        using var file = File.Create(Path.Combine(_output, name)); target.SaveAsPng(file, target.Width, target.Height);
    }
}
