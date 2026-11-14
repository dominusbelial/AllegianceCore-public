using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// Real DesktopGL window, production pure geometry and PCore001 row contents.
// A debug drawing of rectangles, deliberately not the future P1014 renderer.
// p1013-layout-proof <output.png> <classic|modern> <width> <height> [scale] [scrolled]
using var game = new LayoutProof(args[0], args[1] == "classic" ? HudStyle.Classic : HudStyle.Modern,
    int.Parse(args[2]), int.Parse(args[3]), args.Length > 4 ? float.Parse(args[4],
        System.Globalization.CultureInfo.InvariantCulture) : 1f, args.Contains("scrolled"));
game.Run();

sealed class LayoutProof : Game
{
    private readonly string output;
    private readonly HudStyle style;
    private readonly float scale;
    private readonly bool scrolled;
    private SpriteBatch batch = null!;
    private Texture2D white = null!;
    private SpriteFont font = null!;
    private LoadoutV2Layout layout = null!;
    private ShopCatalog catalog = null!;
    private ShopModel model = null!;
    private int frame;

    public LayoutProof(string output, HudStyle style, int width, int height, float scale, bool scrolled)
    {
        this.output = output;
        this.style = style;
        this.scale = scale;
        this.scrolled = scrolled;
        _ = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = width, PreferredBackBufferHeight = height,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false,
        };
        IsFixedTimeStep = false;
        Window.Title = $"P1013 {style} debug layout";
    }

    protected override void LoadContent()
    {
        Content.RootDirectory = Path.GetFullPath("src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
        font = Content.Load<SpriteFont>("HUD");
        batch = new SpriteBatch(GraphicsDevice);
        white = new Texture2D(GraphicsDevice, 1, 1);
        white.SetData(new[] { Color.White });
        catalog = ShopCatalog.TryLoad(ExternalPaths.Require(ExternalResourceKind.Artwork))
            ?? throw new InvalidDataException("PCore001 could not be loaded");
        model = catalog.Hulls.Where(h => h.GroupId >= 0)
            .Select(h => new ShopModel(catalog, h.HullId, Array.Empty<ExpandedPartData>()))
            .OrderByDescending(m => m.Slots.Count).First();
        model.SwitchTab(ShopTab.Parts);
        int open = scrolled ? model.Slots.Count - 1 : Math.Min(1, model.Slots.Count - 1);
        model.MoveCursor(open - model.Cursor);
        model.Activate();
        var vp = GraphicsDevice.Viewport;
        layout = LoadoutV2Layout.Compute(vp.Width, vp.Height, scale, catalog.Hulls.Count,
            model.Slots.Count, model.OpenSlotParts.Count + 1, open,
            scrolled ? int.MaxValue : 0, scrolled ? int.MaxValue : 0, scrolled ? int.MaxValue : 0);
        foreach (var element in layout.Elements)
            if (DockedTouchZones.LoadoutV2HitTest(layout, element.Bounds.CenterX, element.Bounds.CenterY) != element)
                throw new InvalidDataException("Draw/touch geometry disagree");
        Console.WriteLine($"P1013_GL style={style} viewport={vp.Width}x{vp.Height} scale={layout.Scale:0.###} "
            + $"hulls={catalog.Hulls.Count} slots={model.Slots.Count} offers={model.OpenSlotParts.Count + 1} "
            + $"scroll={layout.HullScroll}/{layout.SlotScroll}/{layout.OfferScroll}");
    }

    private static Rectangle Rect(TouchRect r) => new((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
    private void Outline(TouchRect r, Color color)
    {
        var bounds = Rect(r);
        batch.Draw(white, new Rectangle(bounds.X, bounds.Y, bounds.Width, 1), color);
        batch.Draw(white, new Rectangle(bounds.X, bounds.Bottom - 1, bounds.Width, 1), color);
        batch.Draw(white, new Rectangle(bounds.X, bounds.Y, 1, bounds.Height), color);
        batch.Draw(white, new Rectangle(bounds.Right - 1, bounds.Y, 1, bounds.Height), color);
    }

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(style == HudStyle.Classic ? new Color(4, 8, 20) : new Color(9, 20, 29));
        Color line = style == HudStyle.Classic ? new Color(88, 128, 190) : new Color(44, 168, 178);
        batch.Begin();
        foreach (var region in layout.Regions) Outline(region, line);
        if (layout.OfferCapacity > 0) Outline(layout.OfferViewport, Color.Orange);
        foreach (var element in layout.Elements)
        {
            Color color = element.Kind == LoadoutV2Layout.Kind.Offer ? Color.Orange : line;
            batch.Draw(white, Rect(element.Bounds), color * .13f);
            Outline(element.Bounds, color);
            string label = element.Kind switch
            {
                LoadoutV2Layout.Kind.Title => $"LOADOUT V2 - {style} - DEBUG RECTANGLES",
                LoadoutV2Layout.Kind.Money => "MONEY $2500 (debug label)",
                LoadoutV2Layout.Kind.HullHeading => "HULLS - PCore001",
                LoadoutV2Layout.Kind.SlotHeading => "SLOTS - " + model.EffectiveHull!.Value.Name,
                LoadoutV2Layout.Kind.Hull => $"{element.Index}: {catalog.Hulls[element.Index].Name}",
                LoadoutV2Layout.Kind.Slot => $"{element.Index}: {model.Slots[element.Index].Label}",
                LoadoutV2Layout.Kind.Offer => element.Index < model.OpenSlotParts.Count
                    ? model.OpenSlotParts[element.Index].Part.Name : "- MAKE SLOT EMPTY -",
                LoadoutV2Layout.Kind.Preview => "SHIP PREVIEW - P1014",
                LoadoutV2Layout.Kind.Stat => $"STAT {element.Index + 1} / DELTA",
                LoadoutV2Layout.Kind.Price => "PRICE / REFUND / SHORTFALL",
                LoadoutV2Layout.Kind.Buy => "Need $2147483647",
                _ => element.Kind.ToString().ToUpperInvariant(),
            };
            string fitted = LoadoutV2Layout.FitText(element, label, text => font.MeasureString(text).X, layout.Scale);
            if (font.MeasureString(fitted).X * layout.Scale > element.TextBounds.Width + .01f)
                throw new InvalidDataException("Text exceeds its geometry budget");
            batch.DrawString(font, fitted, new Vector2(element.TextBounds.X, element.TextBounds.Y),
                Color.White, 0, Vector2.Zero, layout.Scale, SpriteEffects.None, 0);
        }
        batch.DrawString(font, "84px RESERVED DOCKED FOOTER", new Vector2(20, layout.Footer.Y + 20), line);
        batch.End();
        if (++frame < 3) return;
        var vp = GraphicsDevice.Viewport;
        var pixels = new Color[vp.Width * vp.Height];
        GraphicsDevice.GetBackBufferData(pixels);
        using var capture = new Texture2D(GraphicsDevice, vp.Width, vp.Height);
        capture.SetData(pixels);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        using (var file = File.Create(output)) capture.SaveAsPng(file, vp.Width, vp.Height);
        Console.WriteLine($"P1013_PASS {Path.GetFileName(output)} elements={layout.Elements.Count}");
        Exit();
    }

    protected override void UnloadContent()
    {
        batch.Dispose();
        white.Dispose();
        base.UnloadContent();
    }
}
