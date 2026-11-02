using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Tools.MdlToGlb;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// Offline GL proof. No server, network or screenshot output. The same production
// renderer builds and draws native/imported fig30 across every threshold.
using var proof = new LodProof();
_ = new GraphicsDeviceManager(proof) { PreferredBackBufferWidth = 128, PreferredBackBufferHeight = 128 };
proof.Run();

sealed class LodProof : Game
{
    protected override void Initialize()
    {
        base.Initialize();
        try { Check(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        Exit();
    }
    private void Check()
    {
        string art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var assets = new DirectoryAssetSource(art);
        var model = NativeModelAdapter.FromNative("fig30", MdlFile.Parse(Path.Combine(art, "fig30.mdl")), null);
        using var glb = new MemoryStream(); GlbWriter.Write(model, glb); glb.Position = 0;
        var imported = MdlWriter.Write(GlbReader.Read(glb, null));
        using var native = new NativeShipRenderer(GraphicsDevice, assets);
        using var written = new NativeShipRenderer(GraphicsDevice, new OverrideModel(assets, imported));
        var baseA = native.MeshFor("fig30") ?? throw new Exception("native base missing");
        var baseB = written.MeshFor("fig30") ?? throw new Exception("imported base missing");
        float[] radii = native.LodPixelRadii("fig30").ToArray();
        if (!radii.SequenceEqual(written.LodPixelRadii("fig30"))) throw new Exception("renderer level order differs");
        var samples = radii.SelectMany(r => new[] { MathF.BitDecrement(r), r, MathF.BitIncrement(r) }).Concat(new[] {0f, 1000f}).Distinct().Order().ToArray();
        using var rt = new RenderTarget2D(GraphicsDevice, 128, 128, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var rows = new List<object>();
        foreach (float px in samples)
        {
            int a = ShipLod.NativeLodLevelFor(px, radii), b = ShipLod.NativeLodLevelFor(px, written.LodPixelRadii("fig30"));
            if (a != b) throw new Exception("selected level differs");
            var ga = a == 0 ? baseA : native.LodLevel("fig30", a-1)!;
            var gb = b == 0 ? baseB : written.LodLevel("fig30", b-1)!;
            if (ga.TriangleCount != gb.TriangleCount || ga.Vertices.VertexCount != gb.Vertices.VertexCount) throw new Exception("selected buffers differ");
            Color[] Draw(NativeShipRenderer renderer, NativeShipRenderer.NativeShipGeometry geo)
            {
                GraphicsDevice.SetRenderTarget(rt); GraphicsDevice.Clear(Color.Black);
                var world = Matrix.CreateRotationY(.5f) * Matrix.CreateRotationX(.3f);
                var view = Matrix.CreateLookAt(new Vector3(0, 0, 15), Vector3.Zero, Vector3.Up);
                var projection = Matrix.CreatePerspectiveFieldOfView(MathHelper.PiOver4, 1, .1f, 100);
                renderer.Draw(geo, null, world, view, projection, 1, 1, null, hdOverride: false);
                GraphicsDevice.SetRenderTarget(null);
                var colors = new Color[128*128]; rt.GetData(colors); return colors;
            }
            var pixelsA = Draw(native, ga); var pixelsB = Draw(written, gb);
            if (!pixelsA.SequenceEqual(pixelsB)) throw new Exception("native/imported rendered pixels differ at radius " + px);
            int painted = pixelsA.Count(c => c != Color.Black);
            if (painted == 0) throw new Exception("empty render at radius " + px);
            rows.Add(new { pixelRadius = px, level = a, triangles = ga.TriangleCount, vertices = ga.Vertices.VertexCount, paintedPixels = painted });
        }
        Console.WriteLine(JsonSerializer.Serialize(new { model = "fig30", descendingRadii = radii, samples = rows }));
        Console.WriteLine($"P921_LOD_RENDER_PASS samples={samples.Length} levels={radii.Length} exactPixels=true");
    }
    private sealed class OverrideModel(IAssetSource inner, byte[] model) : IAssetSource
    {
        public bool Exists(string path) => path == "fig30.mdl" || inner.Exists(path);
        public Stream OpenRead(string path) => path == "fig30.mdl" ? new MemoryStream(model) : inner.OpenRead(path);
        public IEnumerable<string> Enumerate(string path) => inner.Enumerate(path);
    }
}
