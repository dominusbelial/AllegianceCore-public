using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

public class P701StationBayVisualTests
{
    private static string Art => ExternalPaths.Resolve(ExternalResourceKind.Artwork);
    private static MdlFile Model(string stem) => MdlFile.Parse(Path.Combine(Art, stem + ".mdl"));

    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")] // Expensive corpus/scenario or collision-work budget; docs/testing/TIERS.md.
    [SkippableFact]
    public void Every_station_group_texture_and_light_matches_independent_binary_oracle()
    {
        Require.Artwork();
        // Counts, texture scopes, frames and all 48-byte LightData records were
        // extracted independently with the Python MDLX reader, not MdlFile.
        using var oracle = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "P701StationModels.json")));
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(Art, "PCore001.igc")));
        var stems = core.StationTypes.Values.Select(s => s.ModelName.Trim()).Distinct().Order().ToArray();
        Assert.Equal(stems, oracle.RootElement.EnumerateObject().Select(p => p.Name).Order());
        foreach (string stem in stems)
        {
            var model = Model(stem);
            var expected = oracle.RootElement.GetProperty(stem);
            Assert.Empty(model.UnsupportedGeometryNodes);
            float radius = expected.GetProperty("radius").GetSingle();
            Assert.InRange(MathF.Abs(NativeStationResolver.MeshRadius(model.Meshes, model.LodLevels) - radius),
                0f, radius * 0.000001f);
            var groups = expected.GetProperty("groups").EnumerateArray().ToArray();
            Assert.Equal(groups.Length, model.Meshes.Count);
            for (int i = 0; i < groups.Length; i++)
            {
                var mesh = model.Meshes[i];
                Assert.Equal(groups[i].GetProperty("texture").GetString(), mesh.TextureStem);
                Assert.Equal(groups[i].GetProperty("vertices").GetInt32(), mesh.VertexCount);
                Assert.Equal(groups[i].GetProperty("triangles").GetInt32(), mesh.TriangleCount);
                Assert.Same(mesh.Indices, NativeStationResolver.RenderIndices(mesh.Indices));
                foreach (var index in mesh.Indices) Assert.InRange((int)index, 0, mesh.VertexCount - 1);
                string? texture = mesh.TextureStem ?? NativeStationResolver.ResolveTextureStem(
                    model, null, stem, s => File.Exists(Path.Combine(Art, s + ".mdl")));
                Assert.False(string.IsNullOrEmpty(texture), $"{stem} group {i} has no texture");
                byte[] data = File.ReadAllBytes(Path.Combine(Art, texture + ".mdl"));
                if (NativeStationTexture.ImportedImagePath(data) is { } image)
                {
                    byte[] png = File.ReadAllBytes(Path.Combine(Art, image));
                    Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
                    Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)) > 0);
                    Assert.True(System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)) > 0);
                }
                else
                {
                    var decoded = MdlTextureDecoder.Decode(data);
                    Assert.True(decoded.Width > 0 && decoded.Height > 0, $"{stem}/{texture}");
                }
            }
            var lights = expected.GetProperty("lights").EnumerateArray().ToArray();
            Assert.Equal(lights.Length, model.Lights.Count);
            for (int i = 0; i < lights.Length; i++)
            {
                float[] saved = lights[i].EnumerateArray().Select(v => v.GetSingle()).ToArray();
                var actual = model.Lights[i];
                Assert.Equal(saved, actual.Color.Concat(actual.Position).Concat([
                    actual.Period, actual.Phase, actual.RampUp, actual.Hold, actual.RampDown]));
            }
            Assert.Equal(expected.GetProperty("frames").GetArrayLength(), model.Frames.Count);
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableTheory]
    [InlineData("ss24a", "cwbaybmp", 42)]
    [InlineData("ss27", "ss27insidebmp", 44)]
    [InlineData("ss300", "beltersbaybmp", 50)]
    [InlineData("ss101", "biosbaybmp", 41)]
    [InlineData("ss301", "beltersbaybmp", 22)]
    [InlineData("ss90", "ss27insidebmp", 15)]
    [InlineData("ss98", "rixbaybmp", 0)]
    [InlineData("ss92", "ss92abmp", 0)]
    public void Bay_textures_and_authored_light_rows_survive_import(string stem, string bay, int lights)
    {
        Require.Artwork();
        var model = Model(stem);
        Assert.Contains(model.Meshes, m => m.TextureStem == bay);
        Assert.Equal(lights, model.Lights.Count);
        Assert.Contains(model.Frames, f => f.Name.StartsWith("garage"));
        Assert.DoesNotContain(model.Frames, f => f.Name.Contains("SS")); // compiled into lights, not FrameData
        var draws = new List<NavStrobeView>();
        NativeStationLights.Append(model.Lights, Matrix.Identity, 2f, 0.1f, draws);
        Assert.Equal(lights, draws.Count); // this corpus's native default peak
        NativeStationLights.Append(model.Lights, Matrix.Identity, 2f, 0.5f, draws = []);
        Assert.Empty(draws);
    }

    [Fact]
    public void Station_radius_uses_distance_from_origin_and_includes_all_lod_geometry()
    {
        static MdlMesh Mesh(params float[] positions) => new()
        {
            Positions = positions, Normals = new float[positions.Length],
            TexCoords = new float[positions.Length / 3 * 2], Indices = [],
        };
        var body = Mesh(10, 0, 0, 0, 3, 4);
        var lod = new MdlLodLevel { PixelRadius = 16, Meshes = [Mesh(0, 0, -13)] };
        Assert.Equal(10f, NativeStationResolver.MeshRadius([body]));
        Assert.Equal(13f, NativeStationResolver.MeshRadius([body], [lod]));
        Assert.Equal(100f, NativeStationResolver.MeshRadius([Mesh(100, 0, 0)]));
        Assert.Equal(0f, NativeStationResolver.MeshRadius([]));
    }

    [Theory]
    [InlineData(0f, -100f, false)] // exactly 32 pixels: native's strict cutoff
    [InlineData(0f, -99f, true)]
    [InlineData(1000f, -99f, true)] // horizontal displacement does not change depth
    [InlineData(0f, -200f, false)]
    [InlineData(0f, 100f, false)] // behind camera
    public void Native_light_visibility_uses_projected_radius_and_camera_depth(float x, float z, bool visible)
    {
        Assert.Equal(visible, NativeStationLights.IsVisible(new Vector3(x, 0, z),
            10f, Matrix.Identity, Matrix.Identity, 640f));
    }

    [Fact]
    public void Inspector_capture_has_valid_crc_for_every_png_chunk()
    {
        byte[] png = Allegiance.ClientCore.MonoGame.Capture.CapturePngSaver.Encode(
            [Color.Red, Color.Green, Color.Blue, Color.White], 2, 2);
        int offset = 8;
        while (offset < png.Length)
        {
            int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            // Independent bitwise CRC over type+payload, including final XOR.
            uint crc = uint.MaxValue;
            foreach (byte value in png.AsSpan(offset + 4, length + 4))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320;
            }
            uint saved = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length));
            Assert.Equal(~crc, saved);
            offset += length + 12;
        }
        Assert.Equal(png.Length, offset);
    }

    [Fact]
    public void Image_import_uses_the_declared_filename_and_rejects_unsupported_text()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("use \"model\"; baybmp = ImportImageFromFile(\"shared-bay.png\", true);");
        Assert.Equal("shared-bay.png", NativeStationTexture.ImportedImagePath(data));
        Assert.Null(NativeStationTexture.ImportedImagePath([0, 223, 186, 222]));
        Assert.Throws<InvalidDataException>(() => NativeStationTexture.ImportedImagePath([1, 2, 3, 4]));
    }

    [Theory]
    [InlineData(0.2f, 0f)]    // future phase: not yet started
    [InlineData(0.3f, 0f)]
    [InlineData(0.35f, 0.5f)] // grows, holds, shrinks, off, wraps
    [InlineData(0.4f, 1f)]
    [InlineData(0.5f, 1f)]
    [InlineData(0.65f, 0.5f)]
    [InlineData(0.8f, 0f)]
    [InlineData(2.4f, 1f)]
    public void Native_light_decal_uses_saved_pose_colour_scale_and_timing(float time, float pulse)
    {
        var light = new MdlLight([0.2f, 0.8f, 0.4f, 0.6f], [2f, 3f, 4f],
            2f, 0.3f, 0.1f, 0.2f, 0.1f);
        var world = Matrix.CreateRotationZ(MathHelper.PiOver2) * Matrix.CreateTranslation(10, 20, 30);
        var output = new List<NavStrobeView>();
        NativeStationLights.Append([light], world, 4f, time, output);
        if (pulse == 0f) { Assert.Empty(output); return; }
        var draw = Assert.Single(output);
        Assert.Equal(2f * pulse, draw.HalfExtent, 4);
        // RY(pi): (-2,3,-4); scale 4; RZ(pi/2): (-12,-8,-16); translate.
        Assert.Equal(-2f, draw.Position.X, 4);
        Assert.Equal(12f, draw.Position.Y, 4);
        Assert.Equal(14f, draw.Position.Z, 4);
        Assert.Equal(new Vector3(0.2f, 0.8f, 0.4f), draw.Rgb);
        Assert.Equal(0.6f, draw.Alpha);
    }
}
