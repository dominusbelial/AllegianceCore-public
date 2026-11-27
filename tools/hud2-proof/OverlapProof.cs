using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using HudProof;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// Actual sprite submissions, replayed one at a time on transparent black so that
// later overpainting cannot hide the first glyph in a collision.
sealed class OverlapProof : Game
{
    private readonly int _width, _height;
    private readonly bool _modern, _withArt;
    private readonly float _density, _size;
    private readonly string? _captures;
    private readonly List<(string Owner, string Id, Vector2 Origin, Vector2 Size, Action Paint)> _commands = [];
    private string _owner = "HUD";
    public int Errors { get; private set; }
    private const string Gun = "PW Gat Gun 1";
    private const string Pilot = "Commander Peregrine Longflight";
    private const string Caption = Pilot + ": Heavy Interceptor";

    public OverlapProof(int width, string style, string scaleCase, string? captures)
    {
        _width = width;
        _height = width switch { 800 => 480, 1280 => 720, 1920 => 1080, _ => throw new ArgumentOutOfRangeException(nameof(width)) };
        _withArt = style == "ClassicArt";
        _modern = style switch { "Modern" => true, "Classic" or "ClassicArt" => false, _ => throw new ArgumentException(style) };
        (_density, _size) = scaleCase switch
        {
            "default" => (1f, 1f), "min" => (1f, .5f), "max" => (1f, 2f),
            "floor" => (.75f, .5f), "ceiling" => (2f, 2f), _ => throw new ArgumentException(scaleCase)
        };
        _captures = captures;
        _ = new GraphicsDeviceManager(this);
        Content.RootDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
    }

    protected override void LoadContent()
    {
        using var batch = new SpriteBatch(GraphicsDevice);
        using var white = new Texture2D(GraphicsDevice, 1, 1);
        white.SetData(new[] { Color.White });
        var small = Content.Load<SpriteFont>("HUD");
        var big = Content.Load<SpriteFont>("HUDLarge");
        var modern = Content.Load<SpriteFont>("HUDModern");
        var native = new NativeFonts(Enum.GetValues<NativeFont>().ToDictionary(id => id, id =>
        {
            var spec = NativeFontCatalog.Spec(id);
            return (Content.Load<SpriteFont>(spec.Asset1x), Content.Load<SpriteFont>(spec.Asset2x));
        }));
        using var frame = new RenderTarget2D(GraphicsDevice, _width, _height);
        string artwork = Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork);
        var gauges = _withArt ? GaugeTextures.Load(GraphicsDevice, artwork) : null;
        using var status = _withArt ? StatusTextures.Load(GraphicsDevice, artwork) : null;
        using var indicators = _withArt ? IndicatorTextures.Load(GraphicsDevice, artwork) : null;
        using var targetArt = _withArt ? TargetPaneTextures.Load(GraphicsDevice, artwork) : null;
        using var renderer = _modern ? new Modern(this, batch, white, small, big, new() { Size = _size }, modern, _density) : null;
        HudRenderer hud = (HudRenderer?)renderer ?? new Classic(this, batch, white, small, big, native, gauges, status, indicators, targetArt);
        GraphicsDevice.SetRenderTarget(frame);
        GraphicsDevice.Clear(new Color(31, 42, 56));
        hud.Draw(Scene(), GraphicsDevice.Viewport);
        GraphicsDevice.SetRenderTarget(null);
        if (_captures != null || _width == 1920 && _size == 1)
        {
            var rgba = new Color[_width * _height];
            frame.GetData(rgba);
            string digest = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(rgba.AsSpan())));
            Console.WriteLine($"RGBA {_width} {(_modern ? "Modern" : "Classic")} {digest}");
            if (_captures != null)
            {
                Directory.CreateDirectory(_captures);
                PngCodec.Encode(Path.Combine(_captures, $"{(_modern ? "modern" : "classic")}-{_width}x{_height}.png"), rgba, _width, _height);
            }
        }
        var ink = MeasureInk(batch);
        void Count(string owner, string id, int expected)
        {
            int count = ink.Count(i => i.Owner == owner && i.Id == id);
            if (count != expected) Fail($"{owner} {id} draws={count} expected={expected}");
        }
        Count("target", Caption, 1);
        Count("target", "ARMOR", 1); Count("target", "SHIELD", 1);
        Count("target", _modern ? "KB 21%" : "21", 1);
        Count("own", _withArt ? "0" : "KB 0%", 1);
        Count(_modern || _withArt ? "own" : "HUD", _modern || _withArt ? "ARMOR" : "HULL", 1);
        Count(_modern || _withArt ? "own" : "HUD", "SHIELD", 1);
        var mounts = ink.Where(i => i.Id == Gun || i.Id.Contains("NOT READY") || i.Id == "mount bar").ToArray();
        string[] expected = [Gun, "16520/32767  |  NOT READY", "mount bar", Gun, "16521/32767  |  NOT READY", "mount bar"];
        if (!mounts.Select(m => m.Id).SequenceEqual(expected)) Fail("Unexpected mount draws: " + string.Join(", ", mounts.Select(m => m.Id)));
        else
            for (int i = 0; i < mounts.Length; i += 3)
            {
                var (name, ammo, bar) = (mounts[i].Bounds, mounts[i + 1].Bounds, mounts[i + 2].Bounds);
                if (name.Bottom > ammo.Top || ammo.Bottom > bar.Top || Math.Abs(name.X - bar.X) > 3 || Math.Abs(ammo.X - bar.X) > 3)
                    Fail($"Detached mount {i / 3}: {name}, {ammo}, {bar}");
            }
        var screen = new Rectangle(0, 0, _width, _height);
        foreach (var item in ink.Where(i => i.Owner != "HUD").Concat(mounts))
            if (!screen.Contains(item.Bounds)) Fail($"{item.Id} outside viewport: {item.Bounds}");
        foreach (var bar in ink.Where(i => i.Id == "mount bar"))
        foreach (var other in ink.Where(i => i.Id != "mount bar" && i.Id != "arc"))
            if (bar.Bounds.Intersects(other.Bounds)) Fail($"bar {bar.Bounds} crosses {other.Id} {other.Bounds}");
        foreach (var own in ink.Where(i => i.Owner == "own"))
            foreach (var target in ink.Where(i => i.Owner == "target"))
                if (own.Bounds.Intersects(target.Bounds)) Fail($"{own.Id} {own.Bounds} crosses target {target.Id} {target.Bounds}");
        Console.WriteLine($"MATRIX {_width} {(_modern ? "Modern" : "Classic")} density={_density} size={_size} errors={Errors}");
        Exit();
    }

    private List<(string Owner, string Id, Rectangle Bounds)> MeasureInk(SpriteBatch batch)
    {
        // Pack the submissions onto one transparent surface and read it back
        // once. Margins include glyph overhang. An integer translation preserves
        // the subpixel phase, including the fractional SDF atlas coordinates.
        using var atlas = new RenderTarget2D(GraphicsDevice, 2048, 1024);
        GraphicsDevice.SetRenderTarget(atlas);
        GraphicsDevice.Clear(Color.Transparent);
        var cells = new List<Rectangle>();
        int x = 0, y = 0, shelf = 0;
        foreach (var cmd in _commands)
        {
            int w = (int)Math.Ceiling(cmd.Size.X) + 16, h = (int)Math.Ceiling(cmd.Size.Y) + 16;
            if (x + w > atlas.Width) { x = 0; y += shelf; shelf = 0; }
            if (y + h > atlas.Height || w > atlas.Width) throw new InvalidOperationException("Proof atlas full");
            var cell = new Rectangle(x, y, w, h);
            cells.Add(cell);
            batch.Begin(transformMatrix: Matrix.CreateTranslation(
                x + 8 - MathF.Floor(cmd.Origin.X), y + 8 - MathF.Floor(cmd.Origin.Y), 0));
            cmd.Paint();
            batch.End();
            x += w;
            shelf = Math.Max(shelf, h);
        }
        GraphicsDevice.SetRenderTarget(null);
        var pixels = new Color[atlas.Width * atlas.Height];
        atlas.GetData(pixels);
        var ink = new List<(string, string, Rectangle)>();
        for (int i = 0; i < cells.Count; i++)
        {
            var cmd = _commands[i];
            var cell = cells[i];
            int left = cell.Right, right = -1, top = cell.Bottom, bottom = -1;
            for (int py = cell.Top; py < cell.Bottom; py++)
            for (int px = cell.Left; px < cell.Right; px++)
            {
                if (pixels[py * atlas.Width + px].A == 0) continue;
                left = Math.Min(left, px); right = Math.Max(right, px);
                top = Math.Min(top, py); bottom = Math.Max(bottom, py);
            }
            if (right < left || left == cell.Left || top == cell.Top || right == cell.Right - 1 || bottom == cell.Bottom - 1)
                throw new InvalidOperationException($"Empty or cropped proof cell for {cmd.Id}");
            ink.Add((cmd.Owner, cmd.Id, new(left - cell.X - 8 + (int)MathF.Floor(cmd.Origin.X),
                top - cell.Y - 8 + (int)MathF.Floor(cmd.Origin.Y), right - left + 1, bottom - top + 1)));
        }
        return ink;
    }

    private void Fail(string message) { Console.WriteLine(message); Errors++; }

    private static HudModel Scene() => HudModel.Create(new HudFrame
    {
        HullFraction = .83f, ShieldFraction = .61f, FuelFraction = .72f, EnergyFraction = .86f, AmmoFraction = .5f,
        Speed = 260, SignaturePercent = 42, TimeDilationPercent = 100,
        WeaponSystems = [new(0, Gun, true, 16520, 32767, WeaponStatus.NotReady), new(1, Gun, true, 16521, 32767, WeaponStatus.NotReady)]
    }, new HudContext
    {
        TargetPaneSlide = 1, TargetPane = new(true, Pilot, "Heavy Interceptor", "Red", new(1f, .5f, .3f),
            .75f, .5f, 2600, 180, null, 21, false, true, TimeToIntercept: "0:10")
    });

    private void Record(string id, Vector2 origin, Vector2 size, Action paint)
    {
        _commands.Add((_owner, id, origin, size, paint));
        paint();
    }

    private void Group(string owner, Action paint)
    {
        _owner = owner;
        paint();
        _owner = "HUD";
    }

    private sealed class Classic(OverlapProof proof, SpriteBatch batch, Texture2D white, SpriteFont small,
        SpriteFont big, NativeFonts fonts, GaugeTextures? gauges, StatusTextures? status, IndicatorTextures? indicators,
        TargetPaneTextures? target) : HudRenderer(batch, white, small, big, gauges, statusTextures: status,
            indicators: indicators, targetPaneTextures: target, nativeFonts: fonts)
    {
        protected override void DrawGauges(HudModel m, Viewport v) => proof.Group("own", () => base.DrawGauges(m, v));
        protected override void DrawOwnStatus(HudModel m, Viewport v) => proof.Group("own", () => base.DrawOwnStatus(m, v));
        protected override void DrawTargetPane(HudModel m, Viewport v) => proof.Group("target", () => base.DrawTargetPane(m, v));
        protected override void RenderText(SpriteFont f, string t, Vector2 p, Color c, float s)
            => proof.Record(t, p, f.MeasureString(t) * s, () => base.RenderText(f, t, p, c, s));
        protected override void DrawBar(float f, float x, float y, float w, float h)
        {
            if (h == 3) proof.Record("mount bar", new(x, y), new(w, h), () => base.DrawBar(f, x, y, w, h));
            else base.DrawBar(f, x, y, w, h);
        }
    }

    private sealed class Modern(OverlapProof proof, SpriteBatch batch, Texture2D white, SpriteFont small,
        SpriteFont big, HudSettings settings, SpriteFont outline, float density = 1)
        : ModernHudRenderer(batch, white, small, big, settings, outline, displayScale: density)
    {
        protected override void DrawGauges(HudModel m, Viewport v) => proof.Group("own", () => base.DrawGauges(m, v));
        protected override void DrawOwnStatus(HudModel m, Viewport v) => proof.Group("own", () => base.DrawOwnStatus(m, v));
        protected override void DrawTargetPane(HudModel m, Viewport v) => proof.Group("target", () => base.DrawTargetPane(m, v));
        protected override void RenderText(SpriteFont f, string t, Vector2 p, Color c, float s)
            => proof.Record(t, p, f.MeasureString(t) * s, () => base.RenderText(f, t, p, c, s));
        protected override void DrawBar(float f, float x, float y, float w, float h)
            => proof.Record("mount bar", new(x, y), new(w, h), () => base.DrawBar(f, x, y, w, h));
        protected override void Arc(Vector2 c, float r, float a, float b, Color color, float width)
        {
            if (proof._owner == "HUD") base.Arc(c, r, a, b, color, width);
            else proof.Record("arc", c - new Vector2(r + width), new Vector2(2 * (r + width)), () => base.Arc(c, r, a, b, color, width));
        }
    }
}
