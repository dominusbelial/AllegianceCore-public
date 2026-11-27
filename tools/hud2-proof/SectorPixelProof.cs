using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace HudProof;

internal static class SectorPixelProof
{
    internal static void Run(HudStyle style)
    {
        using var game = new Game();
        _ = new GraphicsDeviceManager(game) { SynchronizeWithVerticalRetrace = false };
        game.Content.RootDirectory = "Content";
        game.RunOneFrame();
        var gd = game.GraphicsDevice;
        using var white = new Texture2D(gd, 1, 1);
        white.SetData(new[] { Color.White });
        using var batch = new SpriteBatch(gd);
        var font = game.Content.Load<SpriteFont>("HUD");
        using var modern = new ModernHudRenderer(batch, white, font, font, new HudSettings { Style = style });
        HudRenderer hud = style == HudStyle.Classic ? new HudRenderer(batch, white, font, font) : modern;
        using var target = new RenderTarget2D(gd, 640, 360, false, SurfaceFormat.Color, DepthFormat.Depth24);
        // The native planet is along +X. Place the rock beside it in the same camera.
        var view = Matrix.CreateLookAt(Vector3.Zero, Vector3.UnitX, Vector3.UnitY);
        var projection = Matrix.CreatePerspectiveFieldOfView(.65f, 16f / 9, .1f, 1000);
        var origin = new DataClusterIGC { PlanetName = "nebplnt33bmp", PlanetSinLatitude = 120, PlanetRadius = 100 };
        var cluster = new DataClusterIGC { PlanetName = "nebplnt06bmp", PlanetSinLatitude = 120, PlanetRadius = 100 };
        using var referenceRocks = new NativeAsteroidRenderer(gd, AssetSources.Artwork);
        using var referencePlanet = new PlanetPoster(gd, AssetSources.Artwork);
        var departure = Draw(referenceRocks, referencePlanet, false, true, origin);
        var reference = Draw(referenceRocks, referencePlanet, true, true);
        Check(!departure.AsSpan().SequenceEqual(reference), "departure and arrival frames match");
        Check(!reference.AsSpan().SequenceEqual(Draw(referenceRocks, referencePlanet, false, true)), "invisible rock");
        Check(!reference.AsSpan().SequenceEqual(Draw(referenceRocks, referencePlanet, true, false)), "invisible planet");

        foreach (string arrival in new[] { "ready", "partial", "interrupted", "shutdown", "cold" })
        {
            var assets = new RecordingAssetSource(AssetSources.Artwork);
            using var preparation = new SectorTexturePreparation(assets);
            using var rocks = new NativeAsteroidRenderer(gd, assets) { Preparation = preparation };
            using var planet = new PlanetPoster(gd, assets) { Preparation = preparation };
            Check(departure.AsSpan().SequenceEqual(Draw(rocks, planet, false, true, origin)), "departure RGBA");
            var rock = rocks.ColdTexture("bgrnd05bmp")!.Value;
            var poster = planet.ColdTexture(cluster.PlanetName)!.Value;
            if (arrival != "cold")
            {
                preparation.Offer(rock, true);
                preparation.Offer(poster, false);
                Check(SpinWait.SpinUntil(() => preparation.IsPrepared(rock) && preparation.IsPrepared(poster), 3000),
                    "worker timed out");
                preparation.BeginFrame(0);
                Check(preparation.Upload(gd, rock, false) == WarmTextureResult.Pending && preparation.UploadedThisFrame == 0,
                    "expired time budget admitted an upload");
                preparation.BeginFrame(1000);
                Check(preparation.Upload(gd, rock, false) == WarmTextureResult.Pending
                    && preparation.UploadedThisFrame == SectorTexturePreparation.FrameBytes && rock.Cache.Count == 0,
                    "byte budget or partial texture publication");
                Check(preparation.Upload(gd, poster, false) == WarmTextureResult.Pending && preparation.UploadedThisFrame ==
                    SectorTexturePreparation.FrameBytes, "second item exceeded shared budget");
                if (arrival == "ready")
                {
                    for (int i = 0; i < 10 && !poster.Cache.ContainsKey(poster.Stem); i++)
                    {
                        preparation.BeginFrame(1000);
                        preparation.Upload(gd, rock, false);
                        preparation.Upload(gd, poster, false);
                    }
                    Check(rock.Cache.ContainsKey(rock.Stem) && poster.Cache.ContainsKey(poster.Stem), "warm uploads never finished");
                }
                if (arrival is "interrupted" or "shutdown")
                {
                    // Inspect the unpublished GL resource across the lifetime boundary.
                    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                    var jobs = (System.Collections.IList)typeof(SectorTexturePreparation).GetField("_jobs", flags)!.GetValue(preparation)!;
                    var partial = jobs.Cast<object>().Select(job => job.GetType().GetField("Partial", flags)!.GetValue(job))
                        .OfType<Texture2D>().Single();
                    if (arrival == "interrupted") preparation.Reset();
                    else preparation.Dispose();
                    Check(partial.IsDisposed, "unpublished surface survived " + arrival);
                    Check(!preparation.IsPrepared(rock) && !preparation.IsPrepared(poster), "reset retained work");
                }
            }
            for (int frame = 0; frame < 2; frame++)
                Check(reference.AsSpan().SequenceEqual(Draw(rocks, planet, true, true)), $"{arrival} frame {frame} RGBA");
            // Required draws must finish the partially uploaded texture with a single decode.
            foreach (string stem in new[] { rock.Stem, poster.Stem })
            {
                using var original = AssetSources.Artwork.OpenRead(stem + ".mdl");
                Check(assets.OpenedBytes[stem + ".mdl"] == original.Length * (arrival is "interrupted" or "shutdown" ? 2 : 1),
                    $"{arrival} duplicate/missing decode of {stem}");
            }
        }
        CheckPng(gd);
        Console.WriteLine($"Sector pixels match {style}");

        Color[] Draw(NativeAsteroidRenderer rocks, PlanetPoster planet, bool rockVisible, bool planetVisible,
            DataClusterIGC? sector = null)
        {
            gd.SetRenderTarget(target);
            gd.Clear(new Color(28, 44, 71));
            gd.DepthStencilState = DepthStencilState.None;
            gd.RasterizerState = RasterizerState.CullNone;
            gd.SamplerStates[0] = SamplerState.LinearClamp;
            if (planetVisible)
            {
                planet.SetCluster(sector ?? cluster);
                planet.Draw(view, projection, Vector3.Zero, 500);
            }
            if (rockVisible)
            {
                var geo = rocks.GeometryFor("bgrnd05") ?? throw new InvalidDataException("missing rock mesh");
                var stem = rocks.TextureStemFor("bgrnd05", null)!;
                rocks.Draw(geo, rocks.TextureFor(stem), Matrix.CreateTranslation(100, 0, 24), view, projection,
                    NativeAsteroidResolver.ScaleForRadius(10, geo.MeshRadius), null, "bgrnd05", stem);
            }
            hud.Draw(null, CameraMode.Cockpit, gd.Viewport);
            gd.SetRenderTarget(null);
            var pixels = new Color[target.Width * target.Height];
            target.GetData(pixels);
            return pixels;
        }

        void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidDataException($"{style} sector proof: {reason}");
        }
    }
    // Exercise both image namespace forms, including transparent RGB that FromStream
    // zeroes and a partially transparent, non-black pixel that only the MDL path keys.
    private static void CheckPng(GraphicsDevice gd)
    {
        byte[] rgba = [255, 50, 100, 0, 130, 140, 150, 127, 7, 7, 7, 255, 210, 190, 80, 255];
        var png = Allegiance.Core.Imaging.RgbPng.Encode(2, 2, rgba);
        foreach (bool imported in new[] { true, false })
        {
            var source = new PosterSource(png, imported);
            using var preparation = new SectorTexturePreparation(source);
            using var planet = new PlanetPoster(gd, source);
            var dest = planet.ColdTexture("examplebmp")!.Value;
            preparation.Offer(dest, true);
            if (preparation.Upload(gd, dest, true) != WarmTextureResult.Created)
                throw new InvalidDataException("PNG preparation fell back");
            using var stream = new MemoryStream(png);
            using var reference = Texture2D.FromStream(gd, stream);
            var expected = new byte[rgba.Length];
            reference.GetData(expected);
            if (imported) expected = MdlTextureDecoder.ToPremultipliedRgba(new(2, 2, expected), 10);
            var actual = new byte[rgba.Length];
            dest.Cache[dest.Stem].GetData(actual);
            if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidDataException("PNG source pixels differ");
        }
    }

    private sealed class PosterSource(byte[] png, bool imported) : IAssetSource
    {
        private const string Stub = "examplebmp = ImportImageFromFile(\"other.png\", true);";
        public bool Exists(string path) => imported ? path is "examplebmp.mdl" or "other.png" : path == "example.png";
        public Stream OpenRead(string path) => Exists(path)
            ? new MemoryStream(path.EndsWith(".mdl", StringComparison.Ordinal) ? System.Text.Encoding.UTF8.GetBytes(Stub) : png)
            : throw new FileNotFoundException(path);
        public IEnumerable<string> Enumerate(string path) => [];
    }

}
