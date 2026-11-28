using System.Reflection;
using Allegiance.ClientCore.MonoGame;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Rendering;
using Allegiance.SinglePlayer;
using Allegiance.Tools.MdlToGlb;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

internal static class Program
{
    internal const BindingFlags Hidden = BindingFlags.NonPublic | BindingFlags.Instance;
    internal static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, Hidden)!.GetValue(owner)!;
    internal static void Call(Game1 game, string name, params object[] args) =>
        typeof(Game1).GetMethod(name, Hidden)!.Invoke(game, args);

    internal static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    public static void Main(string[] args)
    {
        string scenario = args[0];
        bool measure = args.Contains("--measure");
        string work = Path.GetFullPath(Path.Combine(".scratch", "p1098", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(work);
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", work);
            // The embedded server needs the installed core. All renderer inputs stay fixed.
            if (scenario != "skirmish") Environment.SetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH", work);
            Environment.SetEnvironmentVariable("ALLEGIANCE_NOVSYNC", "1");
            DesktopProofContent.WriteFonts(work);
            DesktopProofContent.WriteArtwork(work);
            WriteSkyArt(work);
            int cycles = scenario == "lifetime" && !args.Contains("--single-device") ? 2 : 1;
            for (int i = 0; i < cycles; i++)
            {
                Environment.SetEnvironmentVariable(DisplayDensity.EnvOverride, i == 0 && !args.Contains("--high-density") ? "1" : "1.5");
                using var game = new Game1(new Game1.LaunchOptions
                {
                    Intro = true, WindowWidth = 480, WindowHeight = 320,
                    EffectsRequested = EffectsQuality.Native, AssetSource = new DirectoryAssetSource(work),
                    SkyTier = scenario is "hd" or "native" ? scenario : "black",
                });
                game.Content.RootDirectory = work;
                game.RunOneFrame();
                if (scenario == "skirmish") Skirmishes(game, measure);
                else if (scenario == "lifetime") SkyInventory.Lifetime(game, measure);
                else SkyFrames.Compare(game, scenario, measure);
            }
            Console.WriteLine($"P1098_PASS {scenario}");
        }
        finally { Directory.Delete(work, true); }
    }

    private static void Skirmishes(Game1 game, bool measure)
    {
        var owners = SkyInventory.Owners(game);
        HashSet<GraphicsResource>? first = null;
        for (int i = 1; i <= 2; i++)
        {
            Call(game, "BeginSkirmish", new SkirmishOptions { StartAsSpectator = true, Size = SkirmishSize.Small }, "Sky pilot");
            Field<Task>(game, "_skirmishStart").WaitAsync(TimeSpan.FromSeconds(90)).GetAwaiter().GetResult();
            Call(game, "UpdateSkirmish", new KeyboardState());
            Check(Field<SkirmishHost>(game, "_skirmish") is not null, "skirmish did not start");
            // The same authored sectors in both matches separate match lifetime from art variety.
            SkyInventory.Exercise(game);
            Call(game, "StopSkirmish");
            Field<Task>(game, "_skirmishStop").GetAwaiter().GetResult();
            var live = SkyInventory.Resources(owners);
            Check(owners.SequenceEqual(SkyInventory.Owners(game)), "match replaced sky owners");
            Check(first == null || first.Count == live.Count, "sky inventory grew on second match");
            int orphaned = first?.Count(r => !live.Contains(r) && !r.IsDisposed) ?? 0;
            Console.WriteLine($"SKIRMISH {i} held={live.Count} previousStillLive={first?.Count(r => !r.IsDisposed) ?? 0} orphaned={orphaned}");
            if (!measure) Check(orphaned == 0, "previous sky handle lost its owner");
            first = live;
        }
    }

    private static void WriteSkyArt(string work)
    {
        int seed = 0;
        void Image(string name)
        {
            seed++;
            byte[] pixels = new byte[16 * 16 * 4];
            for (int p = 0; p < pixels.Length; p += 4)
            {
                pixels[p] = (byte)(20 + (p / 4 + seed * 19) % 210);
                pixels[p + 1] = (byte)(20 + (p / 16 + seed * 23) % 180);
                pixels[p + 2] = (byte)(30 + (p / 64 + seed * 31) % 200);
                pixels[p + 3] = 255;
            }
            string path = Path.Combine(work, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, name.EndsWith(".png") ? PngCodec.Encode(16, 16, pixels)
                : MdlTextureWriter.Write(Path.GetFileNameWithoutExtension(name), 16, 16, pixels));
        }
        foreach (string stem in LensFlareElements.SurfaceArt.Concat(["moonbmp", "moon7bmp", "moonnebbmp"]))
            Image(stem + ".mdl");
        foreach (string name in new[] { "p589obj/object.png", "giantobj/giant.png", "hdsky/stars/stars_equirect_2048x1024.png" })
            Image(name);
        File.WriteAllText(Path.Combine(work, "giant-objects.json"),
            """{"objects":[{"cluster_id":1,"image":"giant.png","direction":[1,0,0.6]}]}""");
        for (int n = 1; n <= 2; n++)
        {
            Image($"planet{n}.png");
            Image($"environment{n}bmp.mdl");
            Image($"hdsky/nebula/env{n}.png");
            // Camera faces +X. An asymmetric patch exposes stale textures and matrices.
            var patch = new GlbMesh("patch", null, [4500,-2800,-3000, 4500,2600,-2500, 4500,-700,3100],
                [0,0, 1,0, 0,1], [-1,0,0, -1,0,0, -1,0,0], [0,1,2]);
            File.WriteAllBytes(Path.Combine(work, $"globe{n}.mdl"), MdlWriter.Write(new GlbExportModel
            {
                Name = "dome", Meshes = [patch], TextureStems = [], LodPixelRadii = [], Mounts = [], Lights = [], Collision = null
            }));
        }
    }

    internal static DataClusterIGC Sector(short id) => new((uint)(41 + id), [1, .12f, .06f], 0xffffffff,
        0, 0, id, 0, (short)(250 + id * 30), $"Sector{id}", $"globe{id}", $"planet{id}bmp",
        120, (byte)(id * 2), 100, true, false);
}
