// P1107 — offscreen before/after proof for `visuals.compactHud` (the compact Modern HUD).
//
// Renders the REAL ModernHudRenderer (the same instance type the client builds) through
// the REAL production draw path into offscreen render targets at 1280x720, 1920x1080 and
// 5120x2160, with an enemy ship targeted in sector and with an aleph targeted:
//
//   * writes the report's before/after PNG pairs (flag off / flag on),
//   * prints every HUD text element's rendered height (layout box + real ink box) and
//     every own-status ring's outer diameter + stroke,
//   * audits the recorded element boxes for the overlaps the brief forbids (own rings and
//     labels vs the crosshair, the lead indicator, the speed readout, the kill-bonus badge,
//     the target pane, the minimap, the systems panel and the chat pane), at HUD size
//     50 / 100 / 200 % for both flag states, and exits non-zero on a NEW overlap.
//
// Usage (offscreen SDL, see docs/reports/COMPACT_MODERN_HUD_P1107.md):
//   SDL_VIDEODRIVER=offscreen ALSOFT_DRIVERS=null dotnet p1107-compact-hud-proof.dll <outDir> [--no-png]

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.MonoGame.Rendering.Skins;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

string outDir = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal)) ?? ".scratch/p1107/proof";
bool png = !args.Contains("--no-png");
using var proof = new Proof(Path.GetFullPath(outDir), png);
proof.Run();
return proof.Failures == 0 ? 0 : 1;

internal enum TargetCase { Ship, Aleph }

/// <summary>One recorded text submission from the production draw path.</summary>
internal sealed record TextDraw(string Text, Vector2 Position, float Scale, Color Color, Rectangle Ink, string Owner, Action Paint);

/// <summary>One recorded arc submission (ring stroke, glow, crosshair, lead marker…).</summary>
internal sealed record ArcDraw(Vector2 Center, float Radius, float Width);

/// <summary>One recorded compact target bar (hull or shield).</summary>
internal sealed record BarDraw(string Id, Rectangle Bounds, float Fill);

internal sealed class Proof : Game
{
    private static readonly (int Width, int Height)[] Resolutions = [(1280, 720), (1920, 1080), (5120, 2160)];
    private static readonly float[] Sizes = [.5f, 1f, 2f];

    private readonly GraphicsDeviceManager _graphics;
    private readonly string _outDir;
    private readonly bool _png;
    private SpriteBatch _batch = null!;
    private Texture2D _white = null!;
    private SpriteFont _small = null!, _big = null!, _outline = null!;
    private HudSdfFont _sdf = null!;
    private readonly Dictionary<(bool Compact, float Size), Probe> _renderers = new();
    private int _failures;
    public int Failures => _failures;

    public Proof(string outDir, bool png)
    {
        _outDir = outDir;
        _png = png;
        _graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 1280, PreferredBackBufferHeight = 720 };
        Content.RootDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
        IsFixedTimeStep = false;
    }

    protected override void LoadContent()
    {
        Directory.CreateDirectory(_outDir);
        _batch = new SpriteBatch(GraphicsDevice);
        _white = new Texture2D(GraphicsDevice, 1, 1);
        _white.SetData(new[] { Color.White });
        _small = Content.Load<SpriteFont>("HUD");
        _big = Content.Load<SpriteFont>("HUDLarge");
        _outline = Content.Load<SpriteFont>("HUDModern");
        _sdf = new HudSdfFont(GraphicsDevice, _small, .25f);
        Console.WriteLine($"P1107 FONT small lineSpacing={_small.LineSpacing} spacing={_small.Spacing} " +
            $"'ARMOR' box={_small.MeasureString("ARMOR")} '0%' box={_small.MeasureString("0%")}");

        try
        {
            MeasureAndCapture();
            Audit();
        }
        catch (Exception exception)
        {
            Console.WriteLine("P1107_FAIL " + exception);
            _failures++;
        }
        Console.WriteLine(_failures == 0 ? "P1107_PASS" : $"P1107_FAIL {_failures} check(s)");
        Exit();
    }

    // ─────────────────────────────────────────────────────────── measurement + PNGs
    private void MeasureAndCapture()
    {
        Console.WriteLine("P1107 MEASURE region resolution case state element kind height_px ink_px x y");
        foreach (var (width, height) in Resolutions)
        {
            using var target = new RenderTarget2D(GraphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.None);
            foreach (var targetCase in new[] { TargetCase.Ship, TargetCase.Aleph })
            foreach (bool compact in new[] { false, true })
            {
                var probe = Renderer(compact, 1f);
                var model = Scene(width, height, targetCase);
                probe.Reset();
                Render(probe, model, target, width, height, new Color(24, 33, 46));
                string state = compact ? "after" : "before";
                WriteTable(width, height, targetCase, state, probe, model);
                if (_png)
                {
                    var pixels = new Color[width * height];
                    target.GetData(pixels);
                    string stem = $"{state}-{width}x{height}-{targetCase.ToString().ToLowerInvariant()}";
                    string path = Path.Combine(_outDir, stem + ".png");
                    WritePng(path, pixels, width, height);
                    Console.WriteLine($"P1107 PNG {stem}.png {new FileInfo(path).Length / 1024} KiB " +
                        $"sha256={Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())))[..16]}");
                }
            }
        }
    }

    private void WriteTable(int width, int height, TargetCase targetCase, string state, Probe probe, HudModel model)
    {
        double scale = ModernHudScale.Core(1f, 1f, height);
        float spread = CompactHudLayout.Spread((float)scale, probe.Compact);
        // Own gauge rings: the recorded arcs whose centre matches a gauge anchor.
        var rings = RingElements(probe, model, width, height, spread);
        foreach (var ring in rings)
            Console.WriteLine($"P1107 MEASURE own {width}x{height} {targetCase} {state} ring:{ring.Id} " +
                $"diameter={ring.Diameter:0.0} stroke={ring.Stroke:0.0} x={ring.Bounds.Center.X} y={ring.Bounds.Center.Y}");
        var ink = MeasureInk(probe);
        for (int i = 0; i < probe.Texts.Count; i++)
        {
            var text = probe.Texts[i];
            // Layout box height (what the HUD reserves) and the REAL rendered ink
            // height (measured from the pixels of that text drawn alone).
            var box = _small.MeasureString(text.Text) * text.Scale;
            Console.WriteLine($"P1107 MEASURE text {width}x{height} {targetCase} {state} \"{Short(text.Text)}\" " +
                $"scale={text.Scale:0.###} height={box.Y:0.0} ink={ink[i].Height} x={(int)text.Position.X} y={(int)text.Position.Y}");
        }
    }

    private static string Short(string text) => text.Length <= 34 ? text : text[..31] + "...";

    // ───────────────────────────────────────────────────────────────────── audit
    private sealed record Ring(string Id, Rectangle Bounds, float Diameter, float Stroke);

    private static List<Ring> RingElements(Probe probe, HudModel model, int width, int height, float spread)
    {
        var result = new List<Ring>();
        var centre = new Vector2(width / 2f, height / 2f);
        foreach (var gauge in model.Gauges)
        {
            if (!gauge.Visible) continue;
            var anchor = centre + new Vector2(gauge.NativeOffset.X, gauge.NativeOffset.Y) * spread;
            ArcDraw? best = null;
            foreach (var arc in probe.Arcs)
                if (Vector2.Distance(arc.Center, anchor) < 1.5f && (best is null || arc.Radius > best.Radius))
                    best = arc;
            if (best is null) continue;
            float outer = 2 * (best.Radius + best.Width / 2);
            result.Add(new Ring(gauge.Kind.ToString(),
                new Rectangle((int)(best.Center.X - outer / 2), (int)(best.Center.Y - outer / 2),
                    (int)MathF.Ceiling(outer), (int)MathF.Ceiling(outer)), outer, best.Width));
        }
        return result;
    }

    private void Audit()
    {
        Console.WriteLine("P1107 AUDIT viewport size case state violations");
        foreach (var (width, height) in Resolutions)
        {
            using var target = new RenderTarget2D(GraphicsDevice, width, height, false, SurfaceFormat.Color, DepthFormat.None);
            foreach (float size in Sizes)
            foreach (var targetCase in new[] { TargetCase.Ship, TargetCase.Aleph })
            foreach (bool compact in new[] { false, true })
            {
                var probe = Renderer(compact, size);
                var model = Scene(width, height, targetCase);
                probe.Reset();
                Render(probe, model, target, width, height, new Color(24, 33, 46));
                float scale = ModernHudScale.Core(1f, size, height);
                float spread = CompactHudLayout.Spread(scale, probe.Compact);
                var problems = AuditOnce(probe, model, width, height, scale, spread);
                Console.WriteLine($"P1107 AUDIT {width}x{height} size={size:0.##} {targetCase} " +
                    $"{(compact ? "after" : "before")} violations={problems.Count}");
                foreach (string problem in problems)
                    Console.WriteLine($"P1107 AUDIT-VIOLATION {width}x{height} size={size:0.##} {targetCase} " +
                        $"{(compact ? "after" : "before")} {problem}");
                if (compact) _failures += problems.Count;
            }
        }
    }

    /// <summary>Rectangle of a recorded text draw (layout box ∪ real ink box).</summary>
    private Rectangle Box(TextDraw text)
    {
        var measure = _small.MeasureString(text.Text) * text.Scale;
        return Rectangle.Union(text.Ink, new Rectangle((int)text.Position.X, (int)text.Position.Y,
            (int)MathF.Ceiling(measure.X), (int)MathF.Ceiling(measure.Y)));
    }

    /// <summary>
    /// The brief's no-overlap rule, on the boxes the production draw path really
    /// submitted: every own-status ring and own label must stay clear of the crosshair,
    /// the lead indicator, the speed readout, the kill-bonus badge, the target pane's
    /// content, the minimap, the systems panel and the chat pane; rings must not touch
    /// each other; the target pane's content must stay clear of the HUD panels.
    /// </summary>
    private List<string> AuditOnce(Probe probe, HudModel model, int width, int height, float scale, float spread)
    {
        var problems = new List<string>();
        var rings = RingElements(probe, model, width, height, spread);
        var centre = new Vector2(width / 2f, height / 2f);
        float cross = 16f * scale;
        var crosshair = new Rectangle((int)(centre.X - cross), (int)(centre.Y - cross),
            (int)(2 * cross), (int)(2 * cross));
        // The lead indicator: the model's lead point ± its 10 px ring (ModernHudRenderer
        // DrawLeadIndicator) plus the marker radius.
        var markers = new List<(string Id, Rectangle Bounds)> { ("crosshair", crosshair) };
        if (model.Context.Lead is { } lead)
        {
            float extent = 12f * scale;
            markers.Add(("lead indicator", new Rectangle((int)(lead.Center.X - extent), (int)(lead.Center.Y - extent),
                (int)(2 * extent), (int)(2 * extent))));
        }
        // The own speed numeral and the kill-bonus badge are ordinary HUD texts: take
        // their real boxes from the recorded draws.
        string speed = HudFormat.FormatNativeNumber(model.Telemetry!.Value.Speed);
        foreach (var text in probe.Texts)
            if (text.Owner == "hud" && text.Text == speed)
                markers.Add(("speed readout", Box(text)));
        foreach (var text in probe.Texts)
            if (text.Owner == "own" && text.Text.StartsWith("KB ", StringComparison.Ordinal))
                markers.Add(("kill-bonus badge", Box(text)));
        var panels = PanelRects(model, width, height, scale);
        var targets = probe.Texts.Where(t => t.Owner == "target").Select(Box).ToList();

        foreach (var ring in rings)
        {
            if (!new Rectangle(0, 0, width, height).Contains(ring.Bounds))
                problems.Add($"ring {ring.Id} {ring.Bounds} is outside the viewport");
            foreach (var (id, rect) in markers)
                if (ring.Bounds.Intersects(rect))
                    problems.Add($"ring {ring.Id} {ring.Bounds} intersects {id} {rect}");
            foreach (var (id, rect) in panels)
                if (ring.Bounds.Intersects(rect))
                    problems.Add($"ring {ring.Id} {ring.Bounds} intersects {id} {rect}");
            foreach (var box in targets)
                if (ring.Bounds.Intersects(box))
                    problems.Add($"ring {ring.Id} {ring.Bounds} intersects target pane text {box}");
            foreach (var bar in probe.Bars)
                if (ring.Bounds.Intersects(bar.Bounds))
                    problems.Add($"ring {ring.Id} {ring.Bounds} intersects target {bar.Id} {bar.Bounds}");
            foreach (var other in rings)
                if (!ReferenceEquals(other, ring) && ring.Bounds.Intersects(other.Bounds))
                    problems.Add($"ring {ring.Id} {ring.Bounds} intersects ring {other.Id} {other.Bounds}");
        }
        // Every HUD text that is not the ring's own percentage/label must stay clear of
        // every ring; own labels must also stay clear of the target pane's content and of
        // each other's rings' forbidden zones.
        foreach (var text in probe.Texts)
        {
            var box = Box(text);
            foreach (var ring in rings)
            {
                if (!box.Intersects(ring.Bounds)) continue;
                bool ownValue = text.Owner == "own" && text.Text.EndsWith('%') && ring.Bounds.Contains(box.Center);
                bool ownLabel = text.Owner == "own" && GaugeLabel(text.Text) is { } kind
                    && string.Equals(kind, ring.Id, StringComparison.OrdinalIgnoreCase);
                if (!ownValue && !ownLabel)
                    problems.Add($"text \"{Short(text.Text)}\" ({text.Owner}) {box} intersects own ring {ring.Id} {ring.Bounds}");
            }
            if (text.Owner == "target")
                foreach (var (id, rect) in panels)
                    if (box.Intersects(rect))
                        problems.Add($"target text \"{Short(text.Text)}\" {box} intersects {id} {rect}");
        }
        // The HUD panels themselves stay disjoint (the target pane is content, checked
        // above, so its coarse reservation rectangle is not part of this test).
        for (int i = 0; i < panels.Count; i++)
            for (int j = i + 1; j < panels.Count; j++)
                if (panels[i].Bounds.Intersects(panels[j].Bounds))
                    problems.Add($"{panels[i].Id} {panels[i].Bounds} intersects {panels[j].Id} {panels[j].Bounds}");
        return problems.Distinct().ToList();
    }

    private static string? GaugeLabel(string text) => text switch
    {
        "ARMOR" => "Hull", "SHIELD" => "Shield", "ENERGY" => "Energy", "AMMO" => "Ammo",
        "THRUST" => "Thrust", "FUEL" => "Fuel", "OXY" => "Fuel", _ => null
    };

    /// <summary>The HUD panels the compact gauges/labels must never reach. Every rect is
    /// the production layout class's own geometry, at this viewport and HUD scale.</summary>
    private static List<(string Id, Rectangle Bounds)> PanelRects(HudModel model, int width, int height, float scale)
    {
        var panels = new List<(string, Rectangle)>();
        var (mx, my, mw, mh) = MinimapGeometry.CornerPanel(width, height);
        panels.Add(("minimap", new Rectangle((int)mx, (int)my, (int)mw, (int)mh)));
        int paneLines = ChatPaneLayout.ConfiguredLines(model.Context.Chat?.PaneLines ?? ChatPaneLayout.DefaultLines);
        panels.Add(("chat pane", new Rectangle(ChatPaneLayout.Left(width), ChatPaneLayout.Top(paneLines),
            ChatPaneLayout.Width, (int)MathF.Ceiling(ChatPaneLayout.NativeFontHeight * (paneLines + 1)))));
        // The weapon/systems status panel at the right edge (HudRenderer.DrawWeaponStatusList).
        float line = 17f * scale + 5;
        float panelW = Math.Min(width * .34f, 380 * Math.Max(1, scale));
        panels.Add(("systems panel", new Rectangle((int)(width - panelW - 18), (int)Math.Max(150, line * 4),
            (int)panelW, (int)(line * 5))));
        return panels;
    }

    // ─────────────────────────────────────────────────────────────── render plumbing
    private Probe Renderer(bool compact, float size)
    {
        if (_renderers.TryGetValue((compact, size), out var existing)) return existing;
        var settings = new HudSettings { Skin = HudSkinIds.Modern, Size = size };
        var visuals = new VisualOptions();
        visuals.SetToggle(VisualFlags.CompactHudKey, compact);
        var probe = new Probe(this, _batch, _white, _small, _big, settings, _outline, _sdf, visuals);
        _renderers[(compact, size)] = probe;
        return probe;
    }

    private void Render(Probe probe, HudModel model, RenderTarget2D target, int width, int height, Color backdrop)
    {
        GraphicsDevice.SetRenderTarget(target);
        GraphicsDevice.Viewport = new Viewport(0, 0, width, height);
        GraphicsDevice.Clear(backdrop);
        probe.Draw(model, GraphicsDevice.Viewport);
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Viewport = new Viewport(0, 0, _graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
    }

    /// <summary>
    /// Lossless RGBA PNG with a per-row adaptive filter (0/1/2 — None/Sub/Up, picked by
    /// the smallest sum of absolute differences). The P980 codec writes every row with
    /// filter 0, which leaves the flat HUD backdrop uncompressed per row; at 5120x2160
    /// that pushed the evidence PNGs over the report's 300 KB per-file budget. The
    /// pixels are unchanged (the filter is invertible), so the PNGs stay exact
    /// evidence.
    /// </summary>
    private static void WritePng(string path, Color[] pixels, int width, int height)
    {
        const int bpp = 4;
        int stride = width * bpp;
        var raw = new byte[(stride + 1) * height];
        var previous = new byte[stride];
        var line = new byte[stride];
        var candidate = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                var c = pixels[y * width + x];
                int i = x * bpp;
                line[i] = c.R; line[i + 1] = c.G; line[i + 2] = c.B; line[i + 3] = c.A;
            }
            int best = 0;
            long bestScore = long.MaxValue;
            for (int filter = 0; filter < 3; filter++)
            {
                long score = 0;
                for (int i = 0; i < stride; i++)
                {
                    byte left = i >= bpp ? line[i - bpp] : (byte)0;
                    candidate[i] = filter switch
                    {
                        1 => (byte)(line[i] - left),
                        2 => (byte)(line[i] - previous[i]),
                        _ => line[i],
                    };
                    score += candidate[i] < 128 ? candidate[i] : 256 - candidate[i];
                }
                if (score >= bestScore) continue;
                bestScore = score;
                best = filter;
                raw[y * (stride + 1)] = (byte)filter;
                Buffer.BlockCopy(candidate, 0, raw, y * (stride + 1) + 1, stride);
            }
            Buffer.BlockCopy(line, 0, previous, 0, stride);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 6; header[10] = 0; header[11] = 0; header[12] = 0;
        using var compressed = new MemoryStream();
        using (var deflate = new System.IO.Compression.ZLibStream(compressed,
            System.IO.Compression.CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(raw, 0, raw.Length);
        using var file = File.Create(path);
        file.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Chunk(file, "IHDR", header);
        Chunk(file, "IDAT", compressed.ToArray());
        Chunk(file, "IEND", []);
    }

    private static void Chunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in typeBytes) crc = Crc(crc, value);
        foreach (byte value in data) crc = Crc(crc, value);
        Span<byte> crcBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc ^ 0xFFFFFFFFu);
        stream.Write(crcBytes);
    }

    private static uint Crc(uint crc, byte value)
    {
        crc ^= value;
        for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        return crc;
    }

    /// <summary>
    /// Paint every recorded text submission alone onto an atlas and read it back, so the
    /// reported ink height is a real rendered pixel height (exactly what the eye sees),
    /// not a font-metric estimate. Same technique as tools/hud2-proof/OverlapProof.cs.
    /// </summary>
    private List<Rectangle> MeasureInk(Probe probe)
    {
        const int atlasW = 4096, atlasH = 2048;
        using var atlas = new RenderTarget2D(GraphicsDevice, atlasW, atlasH, false, SurfaceFormat.Color, DepthFormat.None);
        GraphicsDevice.SetRenderTarget(atlas);
        GraphicsDevice.Viewport = new Viewport(0, 0, atlasW, atlasH);
        GraphicsDevice.Clear(Color.Transparent);
        var cells = new List<Rectangle>(probe.Texts.Count);
        int x = 0, y = 0, shelf = 0;
        foreach (var text in probe.Texts)
        {
            var measure = _small.MeasureString(text.Text) * text.Scale;
            int w = (int)MathF.Ceiling(measure.X) + 16, h = (int)MathF.Ceiling(measure.Y) + 16;
            if (x + w > atlasW) { x = 0; y += shelf; shelf = 0; }
            if (y + h > atlasH) throw new InvalidOperationException("P1107 ink atlas full");
            var cell = new Rectangle(x, y, w, h);
            cells.Add(cell);
            _batch.Begin(transformMatrix: Matrix.CreateTranslation(
                x + 8 - MathF.Floor(text.Position.X), y + 8 - MathF.Floor(text.Position.Y), 0));
            text.Paint();
            _batch.End();
            x += w;
            shelf = Math.Max(shelf, h);
        }
        GraphicsDevice.SetRenderTarget(null);
        GraphicsDevice.Viewport = new Viewport(0, 0, _graphics.PreferredBackBufferWidth, _graphics.PreferredBackBufferHeight);
        var pixels = new Color[atlasW * atlasH];
        atlas.GetData(pixels);
        var result = new List<Rectangle>(cells.Count);
        foreach (var cell in cells)
        {
            int left = cell.Right, right = -1, top = cell.Bottom, bottom = -1;
            for (int py = cell.Top; py < cell.Bottom; py++)
                for (int px = cell.Left; px < cell.Right; px++)
                {
                    if (pixels[py * atlasW + px].A == 0) continue;
                    left = Math.Min(left, px); right = Math.Max(right, px);
                    top = Math.Min(top, py); bottom = Math.Max(bottom, py);
                }
            result.Add(right < left ? Rectangle.Empty : new Rectangle(left, top, right - left + 1, bottom - top + 1));
        }
        return result;
    }

    // ──────────────────────────────────────────────────────────────────── fixture
    internal static HudModel Scene(int width, int height, TargetCase targetCase)
    {
        var frame = new HudFrame(
            HullFraction: .78f, ShieldFraction: .52f, AmmoFraction: .64f, FuelFraction: .83f,
            EnergyFraction: 1f, Power: 1f, Speed: 260f, Throttle: .6f,
            AfterburnerActive: false, Firing: false, GroupFire: true, SelectedMount: 0,
            Flashing: false, WarpFlash: 0f, Mining: false, Cloaked: false,
            KillBonusPercent: 15f, RollDegrees: 0f,
            WeaponSystems: new WeaponStatusRow[]
            {
                new(0, "PW Gat Gun 1", true, 16520, 32767, WeaponStatus.Ready),
                new(1, "SRM Dumbfire", true, 2, 6, WeaponStatus.Ready),
            },
            IsAdriftLifepod: false, OxygenSecondsRemaining: 0,
            SignaturePercent: 42f, Detected: false, AutopilotEngaged: false, VectorLockEngaged: false);
        var side = new HudColor(.85f, .30f, .25f, .5f);
        var neutral = new HudColor(.5f, .5f, .5f, .5f);
        var pane = targetCase == TargetCase.Ship
            ? new HudTargetPane(true, "Peregrine", "Heavy Interceptor", "Red", side,
                .78f, .52f, 1250f, 260f, null, 21f, true, true,
                TimeToIntercept: TargetTti.Text(1250f, 260f, 260f), ClosingSpeed: 43f)
            : new HudTargetPane(true, "Aleph", "aleph", null, neutral,
                null, null, 900f, 0f, null, null, false, false,
                TimeToIntercept: TargetTti.Text(900f, 260f, 0f));
        var contacts = new List<HudContact>();
        for (int i = 0; i < 6; i++)
            contacts.Add(new HudContact(HudContactKind.Ship, new HudPoint(width * .16f + i * 40, height * .52f),
                true, .9f, new HudColor(.1f, .8f, .9f), i == 0, false,
                "Friendly " + i, 800 + i * 60, .5f, .8f, null, false, false, true));

        return HudModel.Create(frame, new HudContext
        {
            Camera = CameraMode.Cockpit,
            RadarMode = RadarMode.Default,
            // An in-sector enemy ship target: pane + projected bracket + lead marker.
            Target = targetCase == TargetCase.Ship
                ? new HudTarget(new HudPoint(width / 2f + 170, height / 2f - 96), true, "Peregrine",
                    .78f, .52f, true, null, 1250f, 43f, side)
                : new HudTarget(new HudPoint(width / 2f + 170, height / 2f - 96), false, "Aleph",
                    null, null, null, null, 900f, null, neutral),
            TargetPane = pane,
            TargetPaneSlide = 1f,
            SelectionBracket = true,
            Lead = targetCase == TargetCase.Ship
                ? new HudLead(new HudPoint(width / 2f + 110, height / 2f - 60),
                    new HudPoint(width / 2f + 170, height / 2f - 96), true, .8f, 1, Granted: true, Visible: true, TargetVisible: true)
                : null,
            Contacts = contacts,
            Chat = new HudChat(
                ["[TEAM] Pilot Alpha: defend the base", "[ALL] Pilot Bravo: enemy fighter at the north gate", "[TEAM] Pilot Delta: acknowledged"],
                0, "", false, 1, "[TEAM] ",
                LineColors: [new HudColor(.07f, .95f, .84f), new HudColor(.90f, .4f, .3f), new HudColor(.07f, .95f, .84f)]),
            LeaderCommand = new LeaderBotCommand(42, false, "DOCK PILOT  [X]"),
            Ripcord = "5",
        });
    }

    // ────────────────────────────────────────────────────────────── probe renderer
    internal sealed class Probe : ModernHudRenderer
    {
        private readonly HudSdfFont _sdf;
        public readonly List<TextDraw> Texts = new(256);
        public readonly List<ArcDraw> Arcs = new(64);
        public readonly List<BarDraw> Bars = new(4);
        private string _owner = "hud";
        private int _barCount;
        public bool Compact { get; }

        public Probe(Proof owner, SpriteBatch batch, Texture2D white, SpriteFont small, SpriteFont big,
            HudSettings settings, SpriteFont outline, HudSdfFont sdf, VisualOptions visuals)
            : base(batch, white, small, big, settings, outline, displayScale: 1f, visuals: visuals)
        {
            _sdf = sdf;
            Compact = CompactHudLayout.Enabled(visuals);
        }

        public void Reset()
        {
            Texts.Clear();
            Arcs.Clear();
            Bars.Clear();
            _owner = "hud";
            _barCount = 0;
        }

        private void Group(string owner, Action paint)
        {
            string previous = _owner;
            _owner = owner;
            paint();
            _owner = previous;
        }

        protected override void DrawGauges(HudModel model, Viewport viewport)
            => Group("own", () => base.DrawGauges(model, viewport));

        protected override void DrawOwnStatus(HudModel model, Viewport viewport)
            => Group("own", () => base.DrawOwnStatus(model, viewport));

        protected override void DrawTargetPane(HudModel model, Viewport viewport)
        {
            _barCount = 0;
            Group("target", () => base.DrawTargetPane(model, viewport));
        }

        protected override void DrawCompactTargetBar(Rectangle bar, float fill, Color side)
        {
            Bars.Add(new BarDraw(_barCount++ == 0 ? "hull bar" : "shield bar", bar, fill));
            base.DrawCompactTargetBar(bar, fill, side);
        }

        protected override void RenderText(SpriteFont font, string text, Vector2 position, Color color, float scale)
        {
            // The Modern renderer draws every HUD string through its SDF font; measure
            // the REAL ink box with the same maths the production draw uses.
            var ink = _sdf.InkBounds(text, position, scale);
            Texts.Add(new TextDraw(text, position, scale, color, ink, _owner,
                () => base.RenderText(font, text, position, color, scale)));
            base.RenderText(font, text, position, color, scale);
        }

        protected override void Arc(Vector2 center, float radius, float start, float sweep, Color color, float thickness)
        {
            Arcs.Add(new ArcDraw(center, radius, thickness));
            base.Arc(center, radius, start, sweep, color, thickness);
        }
    }
}
