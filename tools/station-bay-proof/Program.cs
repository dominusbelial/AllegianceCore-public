using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

// Real GPU audit of every station group, beyond filename-only resolution.
using var proof = new StationGpuAudit(args.Length > 0 ? args[0] : "station-gpu-audit.csv");
proof.Run();

sealed class StationGpuAudit : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly string _output;
    public StationGpuAudit(string output)
    {
        _output = Path.GetFullPath(output);
        _graphics = new GraphicsDeviceManager(this) { PreferredBackBufferWidth = 640, PreferredBackBufferHeight = 480 };
        IsFixedTimeStep = false;
    }

    protected override void LoadContent()
    {
        string art = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var assets = new DirectoryAssetSource(art);
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(art, "PCore001.igc")));
        using var renderer = new NativeStationRenderer(GraphicsDevice, assets);
        using var target = new RenderTarget2D(GraphicsDevice, 640, 480, false, SurfaceFormat.Color, DepthFormat.Depth24);
        var lines = new List<string> { "model,group,texture,start_index,triangles,mesh_radius,gpu_status" };
        int modelCount = 0, groups = 0;
        var checkedTextures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string stem in core.StationTypes.Values.Select(s => s.ModelName.Trim()).Distinct().Order())
        {
            var file = MdlFile.Parse(Path.Combine(art, stem + ".mdl"));
            if (file.UnsupportedGeometryNodes.Count != 0) throw new Exception($"{stem}: skipped geometry");
            var geo = renderer.MeshFor(stem) ?? throw new Exception($"{stem}: GPU model missing");
            if (geo.Parts.Count != file.Meshes.Count) throw new Exception($"{stem}: dropped draw group");
            if (geo.Indices.IndexCount != file.Meshes.Sum(m => m.Indices.Length)
                || geo.Vertices.VertexCount != file.Meshes.Sum(m => m.VertexCount))
                throw new Exception($"{stem}: GPU buffer sizes do not cover all groups");
            int indexOffset = 0;
            string? fallback = renderer.TextureStemFor(stem, null);
            for (int i = 0; i < file.Meshes.Count; i++)
            {
                var mesh = file.Meshes[i]; var part = geo.Parts[i];
                if (part.StartIndex != indexOffset || part.TriangleCount != mesh.TriangleCount || part.TextureStem != mesh.TextureStem)
                    throw new Exception($"{stem}/{i}: incorrect draw range or texture");
                indexOffset += mesh.Indices.Length;
                string texture = part.TextureStem ?? fallback ?? throw new Exception($"{stem}/{i}: no texture");
                var uploadedTexture = renderer.TextureFor(texture) ?? throw new Exception($"{stem}/{i}: texture decode/upload failed");
                if (checkedTextures.Add(texture)) VerifyPixels(GraphicsDevice, assets, texture, uploadedTexture);
                lines.Add($"{stem},{i},{texture},{part.StartIndex},{part.TriangleCount},{geo.MeshRadius.ToString("R", System.Globalization.CultureInfo.InvariantCulture)},ok");
                groups++;
            }
            GraphicsDevice.SetRenderTarget(target);
            GraphicsDevice.Clear(Color.Black);
            renderer.Draw(geo, fallback is null ? null : renderer.TextureFor(fallback), Matrix.Identity,
                Matrix.CreateLookAt(new Vector3(0, 0, geo.MeshRadius * 3), Vector3.Zero, Vector3.Up),
                Matrix.CreatePerspectiveFieldOfView(1f, 4f / 3f, 0.1f, geo.MeshRadius * 10), 1f, null);
            GraphicsDevice.SetRenderTarget(null);
            var pixels = new Color[640 * 480]; target.GetData(pixels);
            if (!pixels.Any(p => p.R != 0 || p.G != 0 || p.B != 0)) throw new Exception($"{stem}: blank GPU draw");
            modelCount++;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_output)!);
        File.WriteAllLines(_output, lines);
        Console.WriteLine($"PASS: {modelCount} station models, {groups} GPU groups; all draw ranges, buffer sizes and texture uploads verified; no blank models. {checkedTextures.Count} complete GPU texture readbacks match source RGBA.");
        Exit();
    }
    private static void VerifyPixels(GraphicsDevice gd, IAssetSource assets, string stem, Texture2D texture)
    {
        var actual = new Color[texture.Width * texture.Height];
        texture.GetData(actual);
        byte[] data = AssetSources.ReadAllBytes(assets, stem + ".mdl");
        if (NativeStationTexture.ImportedImagePath(data) is { } image)
        {
            using var input = assets.OpenRead(image);
            using var source = Texture2D.FromStream(gd, input);
            if (source.Width != texture.Width || source.Height != texture.Height)
                throw new Exception($"{stem}: image dimensions changed");
            var expected = new Color[actual.Length]; source.GetData(expected);
            for (int y = 0; y < source.Height; y++)
                for (int x = 0; x < source.Width; x++)
                    if (actual[(source.Height - 1 - y) * source.Width + x] != expected[y * source.Width + x])
                        throw new Exception($"{stem}: PNG RGBA/row orientation changed");
        }
        else
        {
            var source = MdlTextureDecoder.Decode(data);
            if (source.Width != texture.Width || source.Height != texture.Height)
                throw new Exception($"{stem}: binary image dimensions changed");
            for (int i = 0; i < actual.Length; i++)
                if (actual[i] != new Color(source.Rgba[4*i], source.Rgba[4*i+1], source.Rgba[4*i+2], source.Rgba[4*i+3]))
                    throw new Exception($"{stem}: binary RGBA changed");
        }
    }

}
