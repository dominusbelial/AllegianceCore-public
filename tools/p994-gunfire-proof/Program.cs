using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

// Deterministic codec → ConnectionSession → ClientWorld → production renderers.
// Local only, no sockets; bound the process with timeout. Four images cover both
// HUD styles and both codecs using the same seeded visual input and particle RNG.
string output = args.Length == 2 && args[0] == "--out" ? args[1] : ".scratch/p994/render";
using var game = new GunfireProof(Path.GetFullPath(output));
game.Run();

sealed class GunfireProof : Game
{
    private const int Width = 960, Height = 540;
    private readonly string _output;
    public GunfireProof(string output)
    {
        _output = output;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
        Content.RootDirectory = Path.Combine(Directory.GetCurrentDirectory(), "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
        IsFixedTimeStep = false;
    }

    protected override void LoadContent()
    {
        Directory.CreateDirectory(_output);
        using var batch = new SpriteBatch(GraphicsDevice);
        using var white = new Texture2D(GraphicsDevice, 1, 1); white.SetData(new[] { Color.White });
        var font = Content.Load<SpriteFont>("HUD"); var big = Content.Load<SpriteFont>("HUDLarge");
        var outline = Content.Load<SpriteFont>("HUDModern");
        string art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var assets = new DirectoryAssetSource(art);
        using var effects = NativeEffectTextures.Load(GraphicsDevice, assets);
        using var sparks = new HullHitSparkRenderer(GraphicsDevice, effects, assets);
        using var flares = new ShieldFlareRenderer(GraphicsDevice, effects, assets);
        using var rounds = new ProjectileRenderer(GraphicsDevice);
        using var ships = new NativeShipRenderer(GraphicsDevice, assets);
        var geometry = ships.MeshFor("wc_icfig") ?? throw new InvalidOperationException("Native fighter geometry required");
        var texture = ships.TextureFor(ships.TextureStemFor("wc_icfig", null) ?? "wc_icfig");
        var gauges = GaugeTextures.Load(GraphicsDevice, art);
        using var status = StatusTextures.Load(GraphicsDevice, art);
        using var indicators = IndicatorTextures.Load(GraphicsDevice, art);
        var classic = new HudRenderer(batch, white, font, big, gauges, statusTextures: status, indicators: indicators);
        using var modern = new ModernHudRenderer(batch, white, font, big,
            new HudSettings { Style = HudStyle.Modern }, outlineSource: outline, nativeArt: status);
        using var target = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var view = Matrix.CreateLookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY);
        var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50), Width / (float)Height, 1, 10000);
        var lighting = SectorLighting.FromCluster([.2f, .3f, -1], 0xffdddddd);
        var centres = new[] { new SimVec(-80, 50, -250), new SimVec(80, 50, -250) };
        IFedMessage[] records = Fixture(centres);
        foreach (bool compact in new[] { false, true })
        {
            using var session = new ConnectionSession();
            session.World.NowSeconds = () => 100;
            byte[] wire = compact ? FedPacket.Pack(new GunfireBatch { Tick = 994, Records = records }.Encode())
                : FedPacket.Pack(records.Select(r => r.Encode()));
            ((ITransportSite)session).OnReceive(ConnectionId.Server, wire);
            var world = session.World;
            if (world.Projectiles.Count != 4 || world.HullHits.Count != 2 || world.HitSoundEvents.Count != 2)
                throw new InvalidOperationException("Codec/session lost a round, spark, flare or sound");
            var sparkPool = new HullHitSparkParticles(); var flarePool = new ShieldFlareParticles();
            foreach (var hit in world.HullHits)
            {
                if (hit.AbsorbedByShield)
                    flarePool.Emit(hit.TargetId, centres[0], Orientation.Identity, hit.Position, new(24, 24, 24), 24, 24);
                else sparkPool.Emit(hit.Position, hit.Normal, hit.Radius, new RandomTrailSource(994));
            }
            sparkPool.Update(.06f); flarePool.Update(.06f);
            foreach (HudStyle style in new[] { HudStyle.Classic, HudStyle.Modern })
            {
                GraphicsDevice.SetRenderTarget(target);
                GraphicsDevice.Clear(new Color(3, 5, 10));
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                GraphicsDevice.BlendState = BlendState.Opaque;
                for (int i = 0; i < centres.Length; i++)
                    ships.Draw(geometry, texture, Matrix.CreateTranslation(ToXna(centres[i])), view, projection, 1,
                        24 / geometry.MeshRadius, lighting);
                foreach (var projectile in world.Projectiles)
                {
                    Vector3 position = ToXna(projectile.PositionAt(100050));
                    Vector3 direction = Vector3.Normalize(ToXna(projectile.Velocity));
                    Matrix transform = Matrix.CreateScale(1.3f, 1.3f, 16) * Matrix.CreateWorld(position, direction, Vector3.UnitY);
                    rounds.Draw(transform, new Vector3(1, .6f, .1f), view, projection);
                }
                sparks.Draw(sparkPool.Enumerate().ToList(), sparkPool.EnumerateFlashes().ToList(), Vector3.Zero, view, projection);
                flares.Draw(flarePool.Enumerate().ToList(), Vector3.Zero, view, projection);
                var frame = new HudFrame(.82f, .7f, .8f, .9f, 1, 1, 180,
                    .6f, false, false, true, 0, false, 0, false, false, 15, 0, [], false, 0, 2500);
                HudRenderer renderer = style == HudStyle.Classic ? classic : modern;
                renderer.Draw(HudModel.Create(frame, new HudContext { Camera = CameraMode.Cockpit }), GraphicsDevice.Viewport);
                batch.Begin();
                batch.Draw(white, new Rectangle(0, 0, Width, 25), Color.Black * .85f);
                batch.DrawString(font, $"P994 {style} / batch {(compact ? "ON" : "OFF")} / rounds, shield flare (left), hull spark (right)",
                    new Vector2(6, 4), Color.White);
                batch.End();
                GraphicsDevice.SetRenderTarget(null);
                using var file = File.Create(Path.Combine(_output, $"{style.ToString().ToLowerInvariant()}_{(compact ? "on" : "off")}.png"));
                target.SaveAsPng(file, Width, Height);
            }
            // Close-ups expose the real native debris mesh and shield cap at
            // their renderer scale; the full-flight images above retain HUDs.
            using var close = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
            using var panel = new RenderTarget2D(GraphicsDevice, Width / 2, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
            var panelProjection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(52), (Width / 2f) / Height, 1, 10000);
            using var flareImage = new Texture2D(GraphicsDevice, Width / 2, Height);
            using var sparkImage = new Texture2D(GraphicsDevice, Width / 2, Height);
            for (int index = 0; index < 2; index++)
            {
                GraphicsDevice.SetRenderTarget(panel); GraphicsDevice.Clear(new Color(3, 5, 10));
                GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                GraphicsDevice.BlendState = BlendState.Opaque;
                var hit = world.HullHits[index];
                Vector3 eye = index == 0 ? ToXna(centres[0]) + new Vector3(0, 0, 24 * 2.4f)
                    : ToXna(hit.Position) - Vector3.Normalize(ToXna(hit.Normal)) * (hit.Radius * .5f * 2.2f);
                var aim = index == 0 ? ToXna(centres[0]) : ToXna(hit.Position);
                var closeView = Matrix.CreateLookAt(Vector3.Zero, aim - eye, Vector3.UnitY);
                if (index == 0) flares.Draw(flarePool.Enumerate().ToList(), eye, closeView, panelProjection);
                else sparks.Draw(sparkPool.Enumerate().ToList(), sparkPool.EnumerateFlashes().ToList(), eye, closeView, panelProjection);
                GraphicsDevice.SetRenderTarget(null);
                var pixels = new Color[Width / 2 * Height]; panel.GetData(pixels);
                (index == 0 ? flareImage : sparkImage).SetData(pixels);
            }
            GraphicsDevice.SetRenderTarget(close); GraphicsDevice.Clear(Color.Black);
            batch.Begin(); batch.Draw(flareImage, Vector2.Zero, Color.White);
            batch.Draw(sparkImage, new Vector2(Width / 2f, 0), Color.White);
            batch.DrawString(font, $"P994 {(compact ? "ON" : "OFF")} / shield cap close-up", new Vector2(6, 4), Color.White);
            batch.DrawString(font, "Hull spark close-up", new Vector2(Width / 2f + 6, 4), Color.White); batch.End();
            GraphicsDevice.SetRenderTarget(null);
            using (var file = File.Create(Path.Combine(_output, $"effects_{(compact ? "on" : "off")}.png")))
                close.SaveAsPng(file, Width, Height);
            Console.WriteLine($"P994_VISUAL_PASS batch={compact} rounds=4 hits=2 sounds=2 styles=Classic,Modern bytes={wire.Length}");
        }
        Exit();
    }

    private static IFedMessage[] Fixture(SimVec[] centres)
    {
        var records = new List<IFedMessage>();
        for (int i = 0; i < 4; i++) records.Add(new ProjectileInstance
        {
            TimeCreate = 994, ProjectileTypeId = 41, LauncherId = (short)(10 + i), Lifespan = 2.125f,
            Position = new(-55.123f + i * 35, 35.234f + i % 2 * 20, -180.345f),
            Velocity = new(10.123f, -20.234f, -1000.345f), Forward = new SimVec(.01f, -.02f, -1).Normalize(),
        });
        records.Add(new ProjectileHullHit { TargetId = 20, ClusterId = 1, SourceId = 10, AbsorbedByShield = true,
            Position = centres[0] + new SimVec(-14.123f, 10.234f, -16.345f),
            Normal = new SimVec(-1, 1, -1).Normalize(), Radius = 24 });
        records.Add(new ProjectileHullHit { TargetId = 21, ClusterId = 1, SourceId = 11,
            Position = centres[1] + new SimVec(8.123f, 5.234f, 22.345f),
            Normal = new SimVec(.2f, .1f, 1).Normalize(), Radius = 24 });
        return records.ToArray();
    }
    private static Vector3 ToXna(SimVec v) => new(v.X, v.Y, v.Z);
}
