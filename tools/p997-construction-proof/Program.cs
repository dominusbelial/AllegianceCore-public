using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Rendering;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

// Actual production decoders, ClientWorld and renderers, deterministic local
// wire fixture. Run with timeout; no sockets or remote hosts are used.
using var game = new ConstructionProof(args.Contains("--models"), args.FirstOrDefault(a => a != "--models")
    ?? ".scratch/p997/screens");
game.Run();

sealed class ConstructionProof : Game
{
    private const int Width = 960, Height = 540;
    private readonly bool modelsOnly;
    private readonly string output;
    public ConstructionProof(bool modelsOnly, string output)
    {
        this.modelsOnly = modelsOnly; this.output = output;
        _ = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 64, PreferredBackBufferHeight = 64,
            GraphicsProfile = GraphicsProfile.HiDef, SynchronizeWithVerticalRetrace = false };
        IsFixedTimeStep = false;
    }

    protected override void LoadContent()
    {
        var assets = new DirectoryAssetSource(ExternalPaths.Require(ExternalResourceKind.Artwork));
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc")));
        using var stations = new NativeStationRenderer(GraphicsDevice, assets);
        foreach (var type in core.StationTypes.Values)
        {
            var geo = stations.MeshFor(type.ModelName.Trim());
            if (geo is null || geo.TriangleCount == 0 || geo.MeshRadius <= 0)
                throw new InvalidDataException($"Station {type.StationTypeId} '{type.Name}': {type.ModelName} failed native decode");
        }
        Console.WriteLine($"P997_ALL_STATIONS_PASS types={core.StationTypes.Count} models={stations.LoadedModelCount}");
        if (modelsOnly) { Exit(); return; }

        Directory.CreateDirectory(output);
        Content.RootDirectory = Path.Combine(Directory.GetCurrentDirectory(), "src/Allegiance.ClientCore.MonoGame/bin/Debug/net10.0/Content");
        using var batch = new SpriteBatch(GraphicsDevice);
        using var white = new Texture2D(GraphicsDevice, 1, 1); white.SetData(new[] { Color.White });
        var font = Content.Load<SpriteFont>("HUD"); var big = Content.Load<SpriteFont>("HUDLarge");
        var outline = Content.Load<SpriteFont>("HUDModern");
        using var effects = new BuildingEffectRenderer(GraphicsDevice, assets);
        using var rocks = new NativeAsteroidRenderer(GraphicsDevice, assets);
        using var placeholder = new StationRenderer(GraphicsDevice);
        using var missingStations = new NativeStationRenderer(GraphicsDevice, new MissingModelAssets(assets, "ss92.mdl"));
        var gauges = GaugeTextures.Load(GraphicsDevice, ExternalPaths.Require(ExternalResourceKind.Artwork));
        using var status = StatusTextures.Load(GraphicsDevice, ExternalPaths.Require(ExternalResourceKind.Artwork));
        using var indicators = IndicatorTextures.Load(GraphicsDevice, ExternalPaths.Require(ExternalResourceKind.Artwork));
        var classic = new HudRenderer(batch, white, font, big, gauges, statusTextures: status, indicators: indicators);
        using var modern = new ModernHudRenderer(batch, white, font, big, new HudSettings { Style = HudStyle.Modern },
            outlineSource: outline, nativeArt: status);
        var chat = new ChatRenderer(batch, white, font);
        using var target = new RenderTarget2D(GraphicsDevice, Width, Height, false, SurfaceFormat.Color, DepthFormat.Depth24);
        // ss92.cvh garage1 opens along +X; face the actual native bay.
        var eye = new Vector3(1050, 120, 0);
        var centre = new SimVec(0, 0, 0);
        var view = Matrix.CreateLookAt(Vector3.Zero, -eye, Vector3.UnitY);
        var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.ToRadians(50), Width / (float)Height, 1, 10000);
        var lighting = SectorLighting.FromCluster([1, .3f, .2f], 0xffffffff);
        var stationType = core.StationTypes.Values.First(t => t.ModelName.Equals("ss92", StringComparison.OrdinalIgnoreCase));
        var station = DataStationIGC.Decode(new byte[DataStationIGC.Size]) with
        { StationId = 8, StationTypeId = stationType.StationTypeId, ClusterId = 3, SideId = 0,
          Position = [0, 0, 0], Up = [0, 1, 0], Forward = [0, 0, 1], Name = stationType.Name };
        using var peer = new ConnectionSession();
        peer.World.NowSeconds = () => 100;
        void Receive(params IFedMessage[] messages) => ((ITransportSite)peer).OnReceive(ConnectionId.Server,
            FedPacket.Pack(messages.Select(m => m.Encode())));
        var rockData = DataAsteroidIGC.Decode(new byte[DataAsteroidIGC.Size]) with
        { AsteroidId = 7, ClusterId = 3, Radius = 140, Position = [0, 0, 0] };
        var model = "bgrnd05";
        var rockGeo = rocks.GeometryFor(model) ?? throw new InvalidDataException("Native asteroid geometry required");
        var rockTex = rocks.TextureFor(rocks.TextureStemFor(model, null) ?? model);
        var notices = new StationFallbackWarnings();
        foreach (string scene in new[] { "grow", "complete", "fallback" })
        {
            peer.World.Reset();
            Receive(new EnterGame(), ExportAsteroid.FromAsteroid(rockData),
                new ExportBuildingEffect { Effect = new(0, 140, stationType.Radius, new SimVec(0, 0, 200), centre, 3, 7, 1, 0, 0, 1) });
            if (scene != "grow") Receive(new AsteroidDestroyed { AsteroidId = 7, ClusterId = 3 }, ExportStation.FromStation(station));
            if ((scene == "grow") != peer.World.ConstructionVisuals.ContainsKey(7))
                throw new InvalidOperationException("Wrong visual lifetime at construction hand-off");
            if (scene == "fallback" && missingStations.MeshFor(stationType.ModelName) is not null)
                throw new InvalidOperationException("Fallback proof must actually fail native model resolution");
            string? warning = scene == "fallback" ? notices.Take(station.StationTypeId, stationType.Name, stationType.ModelName) : null;
            if (warning is not null) peer.World.PostSystemLine(warning);
            foreach (HudStyle style in new[] { HudStyle.Classic, HudStyle.Modern })
            {
                GraphicsDevice.SetRenderTarget(target); GraphicsDevice.Clear(new Color(3, 5, 10));
                GraphicsDevice.BlendState = BlendState.Opaque; GraphicsDevice.DepthStencilState = DepthStencilState.Default;
                if (scene == "grow")
                {
                    rocks.Draw(rockGeo, rockTex, Matrix.CreateTranslation(-eye), view, projection,
                        140 / rockGeo.MeshRadius, lighting);
                    var effect = peer.World.ConstructionVisuals[7];
                    effects.Draw(effect.Phase(effect.ReceivedLocalMs + 15000), eye, view, projection, 15);
                    if (GraphicsDevice.BlendState != BlendState.Opaque || GraphicsDevice.DepthStencilState != DepthStencilState.Default)
                        throw new InvalidOperationException("Build effect leaked render state");
                }
                else if (scene == "complete")
                {
                    var geo = stations.MeshFor(stationType.ModelName)!;
                    var texStem = stations.TextureStemFor(stationType.ModelName, stationType.TextureName);
                    stations.Draw(geo, texStem is null ? null : stations.TextureFor(texStem), Matrix.CreateRotationY(MathF.PI) * Matrix.CreateTranslation(-eye),
                        view, projection, stationType.Radius / geo.MeshRadius, lighting, stationType.ModelName, texStem);
                }
                else placeholder.Draw(Matrix.CreateScale(stationType.Radius) * Matrix.CreateTranslation(-eye), view, projection,
                    StationRenderer.FriendlyColor, lighting);
                var frame = new HudFrame(.82f, .7f, .8f, .9f, 1, 1, 180,
                    .6f, false, false, true, 0, false, 0, false, false, 15, 0, [], false, 0, 2500);
                HudRenderer hud = style == HudStyle.Classic ? classic : modern;
                hud.Draw(HudModel.Create(frame, new HudContext { Camera = CameraMode.Cockpit }), GraphicsDevice.Viewport);
                if (warning is not null)
                    chat.Draw([warning], 0, "", false, 100, GraphicsDevice.Viewport, "ALL");
                batch.Begin(); batch.DrawString(font, $"P997 {style} / {scene} / production renderers + ClientWorld",
                    new Vector2(8, 6), Color.White); batch.End();
                GraphicsDevice.SetRenderTarget(null);
                using var file = File.Create(Path.Combine(output, $"{scene}-{style.ToString().ToLowerInvariant()}.png"));
                target.SaveAsPng(file, Width, Height);
            }
        }
        Console.WriteLine("P997_VISUAL_PASS scenes=3 styles=Classic,Modern constructionUsesStationMesh=false");
        Exit();
    }
}

// Reversible missing-art fixture; the original artwork files remain untouched.
sealed class MissingModelAssets(IAssetSource assets, string missing) : IAssetSource
{
    public bool Exists(string path) => !path.Equals(missing, StringComparison.OrdinalIgnoreCase) && assets.Exists(path);
    public Stream OpenRead(string path) => path.Equals(missing, StringComparison.OrdinalIgnoreCase)
        ? throw new FileNotFoundException("P997 intentional missing model") : assets.OpenRead(path);
    public IEnumerable<string> Enumerate(string directory) => assets.Enumerate(directory)
        .Where(path => !path.Equals(missing, StringComparison.OrdinalIgnoreCase));
}
