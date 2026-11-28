using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SimVec = Allegiance.Core.Collision.Vector3;

internal static class SkyFrames
{
    internal static void Compare(Game1 game, string tier, bool measure)
    {
        var gd = game.GraphicsDevice;
        using var timer = Allegiance.ClientCore.MonoGame.Diagnostics.GlFrameTimer.TryCreate();
        Console.WriteLine($"DRIVER {gd.Adapter.Description} / {timer?.Renderer} / {timer?.DriverVersion}");
        bool referenceDriver = timer?.Renderer == "llvmpipe (LLVM 19.1.7, 256 bits)"
            && timer.DriverVersion == "4.5 (Compatibility Profile) Mesa 25.0.7-2+deb13u1";
        var baseline = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "frames.txt"))
            .Select(line => line.Split(' ')).ToDictionary(row => (row[1], int.Parse(row[2]), row[3]));
        var world = Program.Field<ConnectionSession>(game, "_session").World;
        foreach (short id in new short[] { 1, 2 }) world.Apply(new ExportCluster { Cluster = Program.Sector(id) });
        world.Apply(new ExportShip { ShipId = 7, SideId = 0, HullId = 1, CharacterName = "Sky pilot" });
        var pose = ServerShipUpdate.FromValues(7, 0, default, 0, SimVec.Zero, Orientation.Identity, SimVec.Zero,
            [0, 0, 0], .65f, 1, 1, 1, 1, 0, 1);
        var draw = typeof(Game1).GetMethod("DrawSkyP576", Program.Hidden)!
            .CreateDelegate<Action<Matrix, Matrix, Vector3>>(game);
        var hd = Program.Field<HdSkyRenderer>(game, "_hdSky");
        hd.SkyIntensity = .63f;
        var glimpse = Program.Field<AlephGlimpseRenderer>(game, "_alephGlimpseRenderer");
        var model = HudModel.Create(new HudFrame(.82f,.7f,.8f,.9f,1,1,180,.6f,false,false,true,
            0,false,0,false,false,15,0,[],false,0,2500), new HudContext { Camera = CameraMode.Cockpit });
        var classic = Program.Field<HudRenderer>(game, "_hudRenderer");
        var modern = Program.Field<ModernHudRenderer>(game, "_modernHudRenderer");
        for (int stage = 0; stage < 4; stage++)
        {
            short id = stage == 0 ? (short)1 : (short)2;
            if (stage == 1)
            {
                var visuals = Program.Field<VisualOptions>(game, "_visuals");
                foreach (string key in new[] { VisualFlags.SimStarsKey, VisualFlags.SimGlareKey, VisualFlags.StarStreaksKey })
                    visuals.SetToggle(key, false);
            }
            if (stage < 2) world.Apply(new SetCluster { Ship = pose, SectorId = id });
            if (stage == 2)
            {
                var graphics = Program.Field<GraphicsDeviceManager>(game, "_graphics");
                graphics.PreferredBackBufferWidth = 640;
                graphics.PreferredBackBufferHeight = 400;
                graphics.ApplyChanges();
            }
            if (stage == 3) gd.Reset(gd.PresentationParameters);
            int goldenStage = Math.Min(stage, 2); // the same-size reset must reproduce the resized frame
            int w = gd.PresentationParameters.BackBufferWidth, h = gd.PresentationParameters.BackBufferHeight;
            Vector3 dir = Vector3.Normalize(new Vector3(1, id * .06f, id * .035f));
            var view = Matrix.CreateLookAt(Vector3.Zero, dir, Vector3.Up);
            var projection = Matrix.CreatePerspectiveFieldOfView(.9f, (float)w / h, 1, 10000);
            var eye = new Vector3(700, -15, id * 250);
            using var target = new RenderTarget2D(gd, w, h, false, SurfaceFormat.Color, DepthFormat.Depth24,
                0, RenderTargetUsage.PreserveContents);
            gd.SetRenderTarget(target);
            gd.SamplerStates[0] = SamplerState.LinearClamp;
            gd.DepthStencilState = DepthStencilState.Default;
            gd.BlendState = BlendState.Opaque;
            draw(view, projection, eye);
            if (stage < 2)
            {
                for (int i = 0; i < 8; i++) draw(view, projection, eye);
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 32; i++) draw(view, projection, eye);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                long bytes = allocated / 32;
                Console.WriteLine($"ALLOC {tier} stage={stage} bytesPerDraw={bytes}");
                if (!measure) Program.Check(referenceDriver ? allocated == 0 : bytes < 2048,
                    $"{tier} steady draw allocated {allocated} bytes in 32 frames");
                if (measure && OperatingSystem.IsLinux())
                {
                    long startCpu = ThreadNanoseconds();
                    for (int i = 0; i < 160; i++) draw(view, projection, eye);
                    Console.WriteLine($"CPU {tier} stage={stage} usPerDraw={(ThreadNanoseconds() - startCpu) / 160000d:F1}");
                }
            }
            var dest = Program.Sector((short)(3 - id));
            glimpse.Invalidate(dest.ClusterId);
            long start = gd.Metrics.DrawCount;
            Program.Check(glimpse.Warm(dest.ClusterId, dest, Program.Sector(id), dir) == 1, "destination sky absent");
            long postcardDraws = gd.Metrics.DrawCount - start;
            var postcard = CachedTarget(glimpse, dest.ClusterId);
            var expected = baseline[(tier, goldenStage, "aleph")];
            var postcardPixels = Capture(expected, postcardDraws, postcard, referenceDriver, stage);
            // Independently create a cold painter on this device, then draw the
            // destination and the current sector without any shared tier state.
            var assets = Program.Field<IAssetSource>(game, "_assets");
            ISkyRenderer FreshSky() => tier switch
            {
                "black" => new BlackSkyRenderer(gd, assets),
                "native" => new NativeSkyRenderer(gd, assets),
                "hd" => new HdSkyRenderer(gd, assets) { SkyIntensity = hd.SkyIntensity },
                _ => throw new ArgumentOutOfRangeException(nameof(tier))
            };
            using var freshDestination = FreshSky();
            using var referenceGlimpse = new AlephGlimpseRenderer(gd, () => freshDestination);
            start = gd.Metrics.DrawCount;
            referenceGlimpse.Warm(dest.ClusterId, dest, Program.Sector(id), dir);
            Program.Check(gd.Metrics.DrawCount - start == postcardDraws, "cold destination draw count differs");
            ComparePixels(postcardPixels, CachedTarget(referenceGlimpse, dest.ClusterId), "destination");
            using var freshCurrent = FreshSky();
            freshCurrent.SetCluster(Program.Sector(id));
            var warp = new DataWarpIGC(10, 11, 40, "", "", "Aleph", [dir.X*350, dir.Y*350, dir.Z*350],
                [1,0,0], [0,1,0], 1, id);
            GlimpseTarget[] targets = [new(warp, dest.ClusterId, dest, 350 * 350)];
            foreach ((string skin, HudRenderer hud) in new[] { ("Classic", classic), ("Modern", (HudRenderer)modern) })
            {
                long Render(bool cold)
                {
                    gd.SetRenderTarget(target);
                    gd.Clear(Color.Black);
                    gd.DepthStencilState = DepthStencilState.Default;
                    gd.BlendState = BlendState.Opaque;
                    gd.RasterizerState = RasterizerState.CullCounterClockwise;
                    gd.SamplerStates[0] = SamplerState.LinearClamp;
                    long firstDraw = gd.Metrics.DrawCount;
                    if (cold) freshCurrent.Draw(view, projection, eye, Program.Field<SpaceVisualFrame>(game, "_spaceVisualFrame"));
                    else draw(view, projection, eye);
                    Program.Check(gd.DepthStencilState == DepthStencilState.Default && gd.BlendState == BlendState.Opaque,
                        "sky leaked device state");
                    (cold ? referenceGlimpse : glimpse).Draw(targets, Vector3.Zero, view, projection, Program.Sector(id),
                        static _ => .45f, static (_, _, _, _) => false);
                    hud.Draw(model, gd.Viewport);
                    gd.SetRenderTarget(null);
                    return gd.Metrics.DrawCount - firstDraw;
                }

                long draws = Render(false);
                var pixels = Capture(baseline[(tier, goldenStage, skin)], draws, target, referenceDriver, stage);
                Program.Check(Render(true) == draws, "cold frame draw count differs");
                ComparePixels(pixels, target, skin);
            }
        }
    }

    // Linux CLOCK_THREAD_CPUTIME_ID excludes scheduling delays and GL worker time.
    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec { public nint Seconds, Nanoseconds; }

    [DllImport("libc", EntryPoint = "clock_gettime")]
    private static extern int ClockGetTime(int clock, out Timespec result);

    private static long ThreadNanoseconds() => ClockGetTime(3, out var t) == 0
        ? (long)t.Seconds * 1_000_000_000 + t.Nanoseconds : throw new InvalidOperationException("clock_gettime failed");

    private static RenderTarget2D CachedTarget(AlephGlimpseRenderer glimpse, short sector)
    {
        var cache = Program.Field<System.Collections.IDictionary>(glimpse, "_cache");
        object entry = cache[sector]!;
        return (RenderTarget2D)entry.GetType().GetField("Target")!.GetValue(entry)!;
    }

    private static void ComparePixels(Color[] expected, RenderTarget2D target, string layer)
    {
        var actual = new Color[target.Width * target.Height];
        target.GetData(actual);
        Program.Check(expected.AsSpan().SequenceEqual(actual), $"{layer} differs from the independent cold render");
    }

    private static Color[] Capture(string[] expected, long draws, RenderTarget2D target, bool referenceDriver, int stage)
    {
        var pixels = new Color[target.Width * target.Height];
        target.GetData(pixels);
        var hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())));
        string label = $"FRAME {expected[1]} {stage} {expected[3]}";
        Console.WriteLine($"{label} {target.Width}x{target.Height} {draws} {hash}");
        Program.Check(pixels.Any(c => c.R > 15) && $"{target.Width}x{target.Height}" == expected[4]
            && draws == long.Parse(expected[5]), $"{label} dimensions, visibility or draw count differ");
        if (referenceDriver) Program.Check(hash == expected[6], $"{label} pre-change pixels differ");
        return pixels;
    }
}
