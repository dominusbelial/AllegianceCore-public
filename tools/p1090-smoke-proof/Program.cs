using System.Reflection;
using System.IO.Compression;
using Allegiance.Core.Wire;
using Allegiance.Tools.MdlToGlb;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Replay;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.SinglePlayer.Benchmark;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

internal static class Program
{
    internal static T Field<T>(Game1 game, string name) =>
        (T)typeof(Game1).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static int Main(string[] args)
    {
        string scenario = args.Single();
        string work = Path.GetFullPath(Path.Combine(".scratch", "p1090", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(work);
        try { Run(scenario, work); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(work, true); }
    }

    private static void Run(string scenario, string work)
    {
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", work);
        Environment.SetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH", work);
        Environment.SetEnvironmentVariable("ALLEGIANCE_NOVSYNC", "1");
        WriteArtwork(work);
        WriteFonts(work);
        bool cloud = false;
        var allocated = new List<Texture2D>();
        Stream Open(GraphicsDevice gd, string path)
        {
            cloud = Path.GetFileName(path) == "Cloud01.png";
            if (cloud && scenario == "missing") return File.OpenRead(Path.Combine(work, "missing.png"));
            if (cloud && scenario == "invalid") return new MemoryStream("invalid image"u8.ToArray());
            if (cloud && scenario == "invalid-grid")
            {
                using var image = new Texture2D(gd, 17, 16);
                image.SetData(new Color[17 * 16]);
                var stream = new MemoryStream();
                image.SaveAsPng(stream, image.Width, image.Height);
                stream.Position = 0;
                return stream;
            }
            return TitleContainer.OpenStream(path);
        }
        void Upload(Texture2D frame, Color[] pixels)
        {
            if (cloud && scenario == "partial")
            {
                allocated.Add(frame);
                if (allocated.Count == 4)
                    throw new IOException("injected fourth Cloud01 upload failure");
            }
            frame.SetData(pixels);
        }

        string recording = WriteReplay(work);
        var quality = scenario == "volumetric" ? EffectsQuality.Volumetric : EffectsQuality.Enhanced;
        bool healthy = scenario is "enhanced" or "volumetric";
        using var game = new Game1(new Game1.LaunchOptions
        {
            Intro = true, WindowWidth = 480, WindowHeight = 320, EffectsRequested = quality,
            Benchmark = BenchmarkCommand.Parse(["--benchmark", "replay:" + recording]),
            AssetSource = new DirectoryAssetSource(work)
        }) { LoadVfxAssets = gd => healthy ? new VfxAssets(gd) : new VfxAssets(gd, path => Open(gd, path), Upload) };
        game.Content.RootDirectory = work;
        game.RunOneFrame();
        var assets = Field<VfxAssets>(game, "_vfxAssets");
        Check(assets.Smoke.Length == (healthy ? 64 : 0), "unexpected Cloud01 frame count");
        int presents = FlyTwoSectors(game);
        Check(Field<EffectsQuality>(game, "_effectsQuality") == quality, "quality tier changed");
        Check((Field<EnhancedSmokeField?>(game, "_enhancedSmoke") != null) == healthy, "incorrect smoke selection");
        Check((Field<EnhancedExplosionRenderer?>(game, "_enhancedExplosionRenderer") != null) == healthy, "incorrect explosion selection");
        Check(allocated.Count == (scenario == "partial" ? 4 : 0) && allocated.All(t => t.IsDisposed),
            $"partial frames still live: {allocated.Count(t => !t.IsDisposed)}/{allocated.Count}");
        if (!healthy)
        {
            using var empty = new EnhancedSmokeField(game.GraphicsDevice, assets);
            empty.Draw([new(new Vector3(0, 0, -40), 8, .5f)], Vector3.Zero, Matrix.Identity, Matrix.Identity,
                null, default, default, default);
        }
        string receipt = PixelComparison.Compare(game, healthy, quality);
        var timer = Field<GlFrameTimer?>(game, "_gpuTimer");
        // Additionally pin the pre-fix captures where the rasterizer matches the
        // baseline. Other drivers compare every pixel with the reference on-device.
        if (healthy && timer?.Renderer == "llvmpipe (LLVM 21.1.8, 256 bits)"
            && timer.DriverVersion == "4.5 (Compatibility Profile) Mesa 26.0.8-1ubuntu0.3")
        {
            const string trails = "engine=6/CF5C504B11AD88D7 missile=5/B4F8A773F0668C48 damage=3/EC75D6597B449E1D";
            string blast = quality == EffectsQuality.Enhanced ? "blast=5/29C95ECEF0CD578A" : "blast=4/104F05C5C17E2129";
            Check(receipt == trails + " " + blast, "pre-fix pixel capture differs: " + receipt);
        }
        Console.WriteLine($"P1090_PASS {scenario} sectors=2 presents={presents} disposed={allocated.Count} {receipt}");
    }

    private static void WriteArtwork(string work)
    {
        // A small native explosion film makes the fallback observable on a clean checkout.
        byte[] rgba = Enumerable.Repeat((byte)255, 32 * 32 * 4).ToArray();
        foreach (var stem in new[] { "exp20", "exp22", "exp23", "exp24", "exp25", "fx18", "e_smoke", "e_fire", "fxmine" })
            File.WriteAllBytes(Path.Combine(work, stem + "bmp.mdl"), MdlTextureWriter.Write(stem, 32, 32, rgba));

        var triangle = new GlbMesh("triangle", null, [0, 0, 0, 1, 0, 0, 0, 1, 0],
            [0, 0, 1, 0, 0, 1], [0, 0, 1, 0, 0, 1, 0, 0, 1], [0, 1, 2]);
        foreach (var stem in new[] { MissileModelSource.DemoModelStem, ProbeModelSource.DemoModelStem })
            File.WriteAllBytes(Path.Combine(work, stem + ".mdl"), MdlWriter.Write(new GlbExportModel
            {
                Name = stem, Meshes = [triangle], TextureStems = [], LodPixelRadii = [], Mounts = [], Lights = [], Collision = null
            }));
    }

    private static void WriteFonts(string work)
    {
        string config = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        // Building the full Modern HUD distance field dominates a Fast test. Use
        // the shipped small glyph atlas for this unrelated HUD input in both passes.
        string content = Path.GetFullPath(Path.Combine("src/Allegiance.ClientCore.MonoGame/bin", config, "net10.0/Content"));
        foreach (string font in new[] { "HUD", "HUDLarge", "HUDModern" })
            File.Copy(Path.Combine(content, "HUD.xnb"), Path.Combine(work, font + ".xnb"));
        Directory.CreateDirectory(Path.Combine(work, "Fonts"));
        foreach (string font in Directory.EnumerateFiles(Path.Combine(content, "Fonts"), "*.xnb"))
            File.Copy(font, Path.Combine(work, "Fonts", Path.GetFileName(font)));
    }

    private static string WriteReplay(string work)
    {
        string recording = Path.Combine(work, "flight.alr");
        // Fixed timestamps drive real SET_CLUSTER transitions through the client session.
        using (var writer = new BinaryWriter(new GZipStream(File.Create(recording), CompressionLevel.Fastest)))
        {
            writer.Write("P729RPL2");
            void Packet(long us, byte[] message)
            {
                var packet = FedPacket.Pack(message);
                writer.Write(us); writer.Write(packet.Length); writer.Write(packet);
            }
            var pose = ServerShipUpdate.FromValues(7, 0, default, 0, SimVec.Zero,
                Orientation.Identity, SimVec.Zero, [0, 0, 0], .65f, 1, 1, 1, 1, 0, 1);
            Packet(0, new ExportShip { ShipId = 7, SideId = 0, HullId = 1, CharacterName = "Smoke pilot" }.Encode());
            Packet(0, new SetCluster { Ship = pose, SectorId = 1 }.Encode());
            Packet(500_000, new SetCluster { Ship = pose, SectorId = 2 }.Encode());
            Packet(2_000_000, new Ping().Encode());
        }
        return recording;
    }

    private static int FlyTwoSectors(Game1 game)
    {
        var replay = Field<BattleReplay>(game, "_benchReplay");
        var session = Field<ConnectionSession>(game, "_session");
        var pools = Field<FlightEffectPools>(game, "_flightEffects");
        var telemetry = Field<ClientFrameTelemetry>(game, "_frameTelemetry");
        int presents = 0;
        for (short sector = 1; sector <= 2; sector++)
        {
            if (sector == 2)
                while (replay.Frame < 29) replay.Advance(session);
            game.RunOneFrame();
            Check(pools.SectorId == sector, $"flight did not enter sector {sector} (view {pools.SectorId})");
            var ship = session.World.Ships[session.World.MyShipId!.Value];
            var pos = ship.Latest!.Position;
            var rng = new RandomTrailSource(1090);
            pools.Engines.Emit(new EngineTrailShip(ship.ShipId, pos, SimVec.Zero, Orientation.Identity, 20, 1), .016f, rng);
            pools.Damage.Emit(pos, SimVec.Zero, Orientation.Identity, 20, ship.ShipId, 2, .016f, rng);
            pools.Advance(.05f);
            session.World.Apply(new ExplosionEvent { Type = ExplosionType.SmallShip, Position = pos, ClusterId = sector, Scale = 40 });
            var blast = session.World.Explosions[^1];
            long before = telemetry.FrameId;
            game.RunOneFrame();
            Check(telemetry.FrameId == before + 1 && pools.SectorId == sector, "flight frame did not present");
            Check(Field<List<TrailParticleView>>(game, "_drawEngineTrailViews").Any(v => v.Kind == TrailParticleKind.Smoke)
                && Field<List<DamageDebrisView>>(game, "_drawDebrisViews").Count > 0
                && Field<List<ExplosionEntity>>(game, "_drawExplosions").Contains(blast), "flight omitted a seeded effect");
            presents++;
        }
        return presents;
    }
}
