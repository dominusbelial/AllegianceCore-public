using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Content;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using XnaMat = Microsoft.Xna.Framework.Matrix;
using XnaVec = Microsoft.Xna.Framework.Vector3;

// P823 — render check: load a WRITTEN .mdl (the glb→mdl writer's output)
// through the port's real renderer path (NativeShipRenderer — the same class
// the client's flight/hangar draw loop uses) and save PNGs. The written model
// sits in its own directory; textures fall through to the artwork checkout via
// LayeredAssetSource.
//
// Run:  dotnet p823-render-proof.dll --dir <dir-with-written-mdl> --stem <stem>
//           [--art <artworkDir>] [--out <dir>] [--label <label>]
// Exit 0 unless the render crashed; PNG paths are printed for review.

var options = ProofOptions.Parse(args);
using var game = new ProofRunner(options);
game.Run();
return 0;

sealed record ProofOptions(string WrittenDir, string Stem, string ArtRoot, string OutDir, string Label)
{
    public static ProofOptions Parse(string[] args)
    {
        string dir = "", stem = "", art = "", outDir = "", label = "p823";
        int i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--dir": dir = args[++i]; break;
                case "--stem": stem = args[++i]; break;
                case "--art": art = args[++i]; break;
                case "--out": outDir = args[++i]; break;
                case "--label": label = args[++i]; break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
            i++;
        }
        if (dir.Length == 0 || stem.Length == 0)
            throw new ArgumentException("--dir and --stem are required");
        art = art.Length > 0 ? art : ExternalPaths.Require(ExternalResourceKind.Artwork);
        if (outDir.Length == 0)
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "AllegianceCore.slnx"))) d = d.Parent;
            outDir = Path.Combine(d?.FullName ?? ".", ".scratch", "p823");
        }
        Directory.CreateDirectory(outDir);
        return new ProofOptions(dir, stem, art, outDir, label);
    }
}

sealed class ProofRunner : Game
{
    private const int RtSize = 512;

    private readonly GraphicsDeviceManager _gdm;
    private readonly ProofOptions _o;

    public ProofRunner(ProofOptions o)
    {
        _o = o;
        _gdm = new GraphicsDeviceManager(this);
        _gdm.PreferredBackBufferWidth = 64;
        _gdm.PreferredBackBufferHeight = 64;
    }

    protected override void Initialize()
    {
        base.Initialize();
        try { RunProof(GraphicsDevice); }
        catch (Exception ex) { Console.WriteLine($"[proof] FATAL: {ex}"); Environment.ExitCode = 2; }
        Exit();
    }

    private void RunProof(GraphicsDevice gd)
    {
        Console.WriteLine($"[proof] P823 label={_o.Label} stem={_o.Stem} dir={_o.WrittenDir} out={_o.OutDir}");
        var assets = new LayeredAssetSource(
            new DirectoryAssetSource(_o.WrittenDir),
            new DirectoryAssetSource(_o.ArtRoot));
        using var native = new NativeShipRenderer(gd, assets);
        using var rt = new RenderTarget2D(gd, RtSize, RtSize, false, SurfaceFormat.Color, DepthFormat.Depth24);

        var geo = native.MeshFor(_o.Stem);
        if (geo is null) { Console.WriteLine("[proof] FATAL: mesh missing"); Environment.ExitCode = 2; return; }
        var texStem = native.TextureStemFor(_o.Stem, null);
        var tex = texStem is null ? null : native.TextureFor(texStem);
        float radius = native.MeshRadius(_o.Stem);
        Console.WriteLine($"[proof] mesh loaded: tris={geo.TriangleCount} radius={radius} lodLevels={native.LodLevelCount(_o.Stem)} texStem={texStem ?? "(none → lit-solid)"}");

        // Two views per model: the hangar-preview framing (eye on +Z, the
        // client's docked-hull orbit) and a three-quarter in-flight view.
        var views = new (string name, XnaMat world, XnaMat view)[]
        {
            ("hangar", DockedHullOrbit.World(0.9f),
                XnaMat.CreateLookAt(new XnaVec(0f, 0f, DockedHullPreviewFraming.EyeDistance(radius)),
                    XnaVec.Zero, XnaVec.Up)),
            ("threequarter", XnaMat.Identity,
                XnaMat.CreateLookAt(new XnaVec(0.6f, 0.45f, 1f) * (2.6f * radius), XnaVec.Zero, XnaVec.Up)),
        };

        foreach (var (name, world, view) in views)
        {
            var proj = XnaMat.CreatePerspectiveFieldOfView(
                DockedHullPreviewFraming.FovRadians, 1f,
                DockedHullPreviewFraming.NativeZClipNear, DockedHullPreviewFraming.NativeZClipFar);
            gd.SetRenderTarget(rt);
            gd.Clear(Color.CornflowerBlue);
            native.Draw(geo, tex, world, view, proj, 1f, 1f, null, _o.Stem, texStem);
            gd.SetRenderTarget(null);

            var path = Path.Combine(_o.OutDir, $"p823-{_o.Label}-{_o.Stem}-{name}.png");
            using (var fs = File.Create(path))
                rt.SaveAsPng(fs, RtSize, RtSize);

            // Coverage sanity: count pixels that differ from the clear colour
            // (CornflowerBlue = 100,149,237).
            var pixels = new Color[RtSize * RtSize];
            rt.GetData(pixels);
            int covered = pixels.Count(p => Math.Abs(p.R - 100) + Math.Abs(p.G - 149) + Math.Abs(p.B - 237) > 12);
            Console.WriteLine($"[proof] wrote {path} (coverage {covered * 100.0 / pixels.Length:F1}%)");
        }
        Console.WriteLine("[proof] OK");
    }
}
