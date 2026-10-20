using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

using var proof = new Proof(args.Length > 0 ? args[0] : "/tmp/HUD2/proof", !args.Contains("--test"));
proof.Run();

sealed class Proof : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly string _out;
    private readonly bool _capture;
    public Proof(string output, bool capture)
    {
        _out = output; _capture = capture; _graphics = new(this) { PreferredBackBufferWidth = 2560, PreferredBackBufferHeight = 1440 };
        Content.RootDirectory = Path.Combine(AppContext.BaseDirectory, "Content");
        IsFixedTimeStep = false;
    }
    protected override void LoadContent()
    {
        Directory.CreateDirectory(_out);
        using var batch = new SpriteBatch(GraphicsDevice);
        using var white = new Texture2D(GraphicsDevice, 1, 1); white.SetData(new[] { Color.White });
        var small = Content.Load<SpriteFont>("HUD"); var big = Content.Load<SpriteFont>("HUDLarge");
        string artwork = Allegiance.Core.ExternalPaths.Resolve(Allegiance.Core.ExternalResourceKind.Artwork);
        var gauges = Directory.Exists(artwork) ? GaugeTextures.Load(GraphicsDevice, artwork) : null;
        var status = Directory.Exists(artwork) ? StatusTextures.Load(GraphicsDevice, artwork) : null;
        var settings = new HudSettings();
        using var modern = new ModernHudRenderer(batch, white, small, big, settings, Content.Load<SpriteFont>("HUDModern"));
        var classic = new HudRenderer(batch, white, small, big, gauges, statusTextures: status);
        var radar = new RadarBracketRenderer(batch, white);   // HUD-4 — classic radar path (the real client draws this separately)
        var summaries = new List<object>();
        foreach (var (width, height) in new[] { (1920, 1080), (2560, 1440), (1280, 720) })
        foreach (float size in new[] { .75f, 1f, 1.5f })
        {
            settings.Size = size; settings.ColorBlindSafe = size == 1.5f;
            foreach (string scenario in new[] { "flight", "chat-stress", "combat", "critical", "docked", "lifepod", "dead", "command", "hidden", "radar-All", "radar-Default", "radar-Target", "radar-Minimal" })
            {
                var model = Scenario(scenario, width, height);
                using var target = new RenderTarget2D(GraphicsDevice, width, height);
                foreach (var renderer in new HudRenderer[] { classic, modern })
                {
                    GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(scenario == "critical" ? new Color(225, 215, 205) : new Color(40, 53, 76));
                    renderer.Draw(model, GraphicsDevice.Viewport);
                    if (model.FlightVisible && renderer == classic)
                        radar.Draw(model, GraphicsDevice.Viewport);   // Classic radar + round border (production path)
                    // The ordinary client routes Classic notices and target-no-match
                    // feedback through this same production measured-text method.
                    if (model.Visible && renderer == classic)
                    {
                        batch.Begin();
                        foreach (var notice in model.Context.Notices)
                            renderer.DrawMeasuredText(small, notice.Text, new(width * .35f, height * .30f), Color.White);
                        batch.End();
                    }
                    GraphicsDevice.SetRenderTarget(null);
                    var boxes = renderer.Layout.TextBoxes;
                    for (int i = 0; i < boxes.Count; i++)
                    {
                        Require(boxes[i].Bounds.Left >= 0 && boxes[i].Bounds.Right <= width && boxes[i].Bounds.Top >= 0 && boxes[i].Bounds.Bottom <= height, "text outside viewport");
                        for (int j = i + 1; j < boxes.Count; j++)
                        {
                            // Native console anchors (label + value 14 px apart) may
                            // overlap in the taller port font; that is intentional
                            // (HUD-9b fixedAnchor), so only overlap-check them
                            // against non-anchored text.
                            if (boxes[i].FixedAnchor && boxes[j].FixedAnchor) continue;
                            Require(!boxes[i].Bounds.Intersects(boxes[j].Bounds), $"{scenario} {width} {size} overlap: {boxes[i]} / {boxes[j]}");
                        }
                    }
                    // HUD-9b — pin the fixed console-text anchors (top-edge based,
                    // not bottom-based) at the reference 1920×1080/scale-1 render.
                    if (size == 1 && width == 1920 && scenario is "combat" or "critical")
                    {
                        RequireBox(boxes, "Current location:", 37);
                        RequireBox(boxes, "Alystra", 51);
                        if (scenario == "combat")
                        {
                            RequireBox(boxes, "Current orders:", 37);
                            RequireBox(boxes, "attack Enemy fighter", 57);
                            RequireBox(boxes, "defend Garrison", 77);
                        }
                        RequireBox(boxes, "Reloading ammo...", 74);
                        if (scenario == "critical")
                            RequireBox(boxes, "Vote: Mutiny against the commander? (Y/N)", 109);
                        RequireBox(boxes, "Ripcord in: 9", 525, 885);
                    }
                    if (renderer == modern)
                    {
                        var missing = model.Elements.Where(e => e.Visible && !modern.DrawnElements.Contains(e.Id)).Select(e => e.Id).ToArray();
                        Require(missing.Length == 0, $"missing {scenario}: {string.Join(", ", missing)}");
                        if (model.PodVisible) Require(boxes.Any(b => b.Text == model.Context.PodReturnInstruction), "pod return-to-base key instruction missing");
                        if (!model.Visible) Require(boxes.Count == 0, "hidden text");
                        if (model.Visible) Require(boxes.Count > 0, "blank visible frame");
                    }
                    if (_capture && size == 1 && width >= 1920 && (scenario is "critical" or "combat" || scenario.StartsWith("radar-")))
                    {
                        using var file = File.Create(Path.Combine(_out, $"{scenario}-{width}x{height}-{(renderer == modern ? "Modern" : "Classic")}.png"));
                        target.SaveAsPng(file, width, height);
                    }
                    summaries.Add(new { scenario, width, height, size, colorBlindSafe = settings.ColorBlindSafe, style = renderer == modern ? "Modern" : "Classic", boxes = boxes.Count, visible = model.Elements.Count(e => e.Visible) });
                }
            }
        }
        File.WriteAllText(Path.Combine(_out, "render-checks.json"), JsonSerializer.Serialize(summaries));
        Console.WriteLine($"HUD2_RENDER_PASS {summaries.Count} rendered scenarios; real font bounds, no overlap, all visible elements");
        Exit();
    }
    static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void RequireBox(IReadOnlyList<HudTextBox> boxes, string text, float top, float? left = null)
    {
        var matches = boxes.Where(b => b.Text == text).ToArray();
        Require(matches.Length > 0, $"missing '{text}'");
        if (left is { } expectedLeft)
            Require(matches.Any(b => Math.Abs(b.Bounds.Top - top) <= 2 && Math.Abs(b.Bounds.Left - expectedLeft) <= 2),
                $"{text} top/left mismatch: " + string.Join(", ", matches.Select(b => $"{b.Bounds.Top}/{b.Bounds.Left}")));
        else
            Require(matches.Any(b => Math.Abs(b.Bounds.Top - top) <= 2),
                $"{text} top mismatch: " + string.Join(", ", matches.Select(b => b.Bounds.Top)));
    }
    static HudModel Scenario(string scenario, int w, int h)
    {
        var frame = new HudFrame(.82f, .7f, .8f, .9f, 1, 1, 260, .6f, true, true, true, 0, false, 0, true, true, 15, 3, 24,
            new WeaponStatusRow[] { new(0, "Rapid fire plasma cannon", true, 0, 200, WeaponStatus.NotReady), new(1, "Interceptor missile", false, 0, 0, WeaponStatus.Ready) }, false, 0, 2500,
            new(.2f, -.3f, .1f, false), 85, ShipWarningMask.OutOfBounds,
            SignaturePercent: 42f, Detected: false, AutopilotEngaged: true, VectorLockEngaged: true,
            CarriedFlag: true, CarriedArtifact: false);
        if (scenario == "critical") frame = frame with { HullFraction = .08f, ShieldFraction = 0, FuelFraction = .10f, EnergyFraction = .02f, Flashing = true };
        if (scenario == "lifepod") frame = frame with { IsAdriftLifepod = true, OxygenSecondsRemaining = 95 };
        float spread = Math.Clamp(h / 680f, 1.15f, 2.1f);
        bool target = scenario is "combat" or "critical" || scenario.StartsWith("radar-");
        var mode = scenario.StartsWith("radar-") ? Enum.Parse<RadarMode>(scenario[6..]) : RadarMode.Default;
        var contacts = new List<HudContact>();
        for (int i = 0; i < 18; i++)
        {
            var kind = (HudContactKind)(i % 9);
            bool isEnemy = i % 8 == 0, isSelf = i == 17, isSel = i == 0;
            float separation = 500f + i * 40f;
            bool shown = kind switch
            {
                HudContactKind.Ship => RadarLod.ShowShip(mode, !isEnemy, isSelf, isSel),
                HudContactKind.Station => RadarLod.ShowStation(mode, pedestal: false, ownOrAllied: !isEnemy, onScreen: i % 2 == 0, separation, isSel),
                HudContactKind.Warp => RadarLod.ShowWarp(mode, isSel),
                HudContactKind.Asteroid => RadarLod.ShowAsteroid(mode, namedSpecial: i % 2 == 0, rangeZero: false, separation, isSel),
                HudContactKind.Missile => RadarLod.ShowMissile(mode, aimsAtOwnSide: isEnemy, isSel),
                HudContactKind.Probe or HudContactKind.Mine => RadarLod.ShowProbeOrMine(mode, ownOrAllied: !isEnemy, separation, isSel),
                _ => RadarLod.ShowTreasure(mode, useful: true, separation, isSel),
            };
            if (!shown) continue;
            var position = new HudPoint(w * .2f + i * 70, h * .48f);
            // HUD-5 — a small number of labelled/overlaid contacts exercise the
            // radar overlay + label block in the overlap sweep without crowding.
            if (i == 0)
                contacts.Add(new(kind, position, i % 3 != 0, .9f, new(.1f, .8f, .9f), true, false,
                    "Enemy fighter with a long pilot name", 1250, .3f, .6f, null, true, false, false,
                    AcceptedOrder: true, CommandId: 2, CommandAccepted: true, NearestEnemy: true));
            else if (i == 1 && kind == HudContactKind.Station)
                contacts.Add(new(kind, position, i % 3 != 0, .9f, new(.9f, .7f, .3f), false, false,
                    "Garrison", 800, .5f, .8f, null, false, false, true));
            else if (i == 2 && kind == HudContactKind.Warp)
                contacts.Add(new(kind, position, i % 3 != 0, .9f, null, false, false,
                    "A-4 Aleph", 600));
            else
                contacts.Add(new(kind, position, i % 3 != 0, .9f,
                    kind is HudContactKind.Treasure or HudContactKind.Tech ? null : new HudColor(.1f, .8f, .9f),
                    isSel, i == 3, IsSelf: isSelf, IsTech: kind == HudContactKind.Tech));
        }
        return HudModel.Create(frame, new HudContext
        {
            Docked = scenario == "docked", Dead = scenario == "dead", CommandView = scenario == "command", Hidden = scenario == "hidden",
            Camera = scenario == "command" ? CameraMode.Command : CameraMode.Cockpit,
            Target = target ? new(new(w / 2 + 126.5f * spread, h / 2 - 112.5f * spread), true, "Enemy fighter with a long pilot name", .6f, .3f, true, null, 1250, 43, new(1, 0, 0)) : null,
            TargetPane = target ? new(true, "Enemy fighter with a long pilot name", "Fighter", "Rixian", new(1, 0, 0), .6f, .3f, 1250f, 43f, null, null, true, true) : null,
            Lead = target ? new(new(w / 2 + 100, h / 2 - 50), new(w / 2 + 200, h / 2 - 80), true, .8f, 1) : null,
            Lock = target ? new(new(w / 2 + 200, h / 2 - 80), MissileLockFeedback.LockState.Partial, 1) : null,
            HitConfirmation = target, PodReturnInstruction = "Shift+Z / G AUTOPILOT TO BASE",
            TargetBearing = target ? new(.8f, -.2f) : null,
            Notices = new[] { new HudNotice("NO ENEMY FIGHTER IN SECTOR", false, 1), new HudNotice("Reloading ammo...", true, 1) },
            Chat = new(scenario == "chat-stress" ? Enumerable.Range(0, 8).Select(i => $"[TEAM] Pilot {i}: " + new string('W', 250)).ToArray() : new[] { "[TEAM] Pilot: defend the base", "[ALL] Enemy: incoming" }, 0, scenario == "chat-stress" ? new string('W', 250) : "returning", true, 1, "[TEAM] "),
            FlightPartsLine = "WEAPONS: GROUP  BOOST: ON  [1:Plasma]", CargoLine = "CARGO 1/5  Ammo pack x3",
            Contacts = contacts, InventoryVisible = true,
            CommandLines = scenario == "command" ? new[] { "COMMAND SHIP: Scout", "Hull 82% Shield 70%" } : Array.Empty<string>(),
            RadarMode = mode,
            RoundRadar = scenario.StartsWith("radar-"),
            Ripcord = "Ripcord in: 9",
            Console = new HudConsoleText
            {
                SectorName = "Alystra",
                CarrierName = scenario is "flight" or "chat-stress" ? "Pilot Alpha" : null,
                BoardState = scenario is "flight" or "chat-stress" ? 2 : 0,
                AcceptedOrder = scenario is "flight" or "combat" ? "defend Garrison" : null,
                QueuedOrder = scenario is "flight" or "combat" ? "attack Enemy fighter" : null,
                VoteText = scenario == "critical" ? "Vote: Mutiny against the commander? (Y/N)" : null,
                OrdinaryTips = new[] { "Welcome to the sector", "Alystra: 3 miners on duty", "Payday: $500" },
                CriticalTips = new[] { new HudNotice("Reloading ammo...", true, 1) },
                SideColor = new(0.07f, 0.95f, 0.84f)
            }
        });
    }
}
