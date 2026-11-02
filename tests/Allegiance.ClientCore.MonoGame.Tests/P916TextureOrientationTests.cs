using System.Diagnostics;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.TestInfra;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Allegiance.ClientCore.MonoGame.Tests;

public sealed class P916TextureOrientationTests
{
    [Fact]
    public void Legacy_HD_binding_reflects_V_and_bitangent_without_modifying_native_mesh()
    {
        VertexPositionNormalTexture[] native = [new(Vector3.Zero, Vector3.UnitZ, Vector2.Zero),
            new(Vector3.UnitX, Vector3.UnitZ, Vector2.UnitX), new(Vector3.UnitY, Vector3.UnitZ, Vector2.UnitY)];
        var legacy = NativeMaterialParts.ReverseTextureV(native);
        var originalTangents = PbrVertex.Build(native, [0,1,2]);
        var legacyTangents = PbrVertex.Build(legacy, [0,1,2]);
        for (int i = 0; i < native.Length; i++)
        {
            Assert.Equal(native[i].Position, legacy[i].Position);
            Assert.Equal(native[i].Normal, legacy[i].Normal);
            Assert.Equal(native[i].TextureCoordinate.X, legacy[i].TextureCoordinate.X);
            Assert.Equal(1f - native[i].TextureCoordinate.Y, legacy[i].TextureCoordinate.Y);
            Assert.Equal(-originalTangents[i].Tangent.W, legacyTangents[i].Tangent.W);
            Assert.Equal(originalTangents[i].Tangent.X, legacyTangents[i].Tangent.X);
        }
        Assert.Equal(Vector2.Zero, native[0].TextureCoordinate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Short_or_non_MDL_HD_sources_preserve_their_existing_UVs(int length)
    {
        // Orientation metadata does not touch the GPU. HD's existing synthetic
        // pack contracts use hash-verified two-byte sources, not real MDLs.
        using var hd = new HdMaterialRenderer(null!, _ => new MemoryStream(new byte[length]), new HdPack("missing-p916-pack"));
        Assert.False(hd.ReverseMeshV("synthetic"));
        Assert.False(hd.ReverseMeshV("synthetic")); // cached metadata is identical
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Real_pod_panels_use_authored_rows_while_effect_image_rows_keep_legacy_order()
    {
        Require.Artwork();
        string art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var mesh = MdlFile.Parse(File.ReadAllBytes(Path.Combine(art, "utl100.mdl"))).Meshes.Single();
        var bytes = File.ReadAllBytes(Path.Combine(art, "utl100bmp.mdl"));
        var native = MdlTextureDecoder.Decode(bytes, flipVertical: false);
        var legacy = MdlTextureDecoder.Decode(bytes, flipVertical: true);
        int recovered = 0;
        foreach (var tri in mesh.Indices.Chunk(3))
        {
            float u = tri.Average(i => mesh.TexCoords[2*i]), v = tri.Average(i => mesh.TexCoords[2*i+1]);
            int x = Math.Clamp((int)(u*native.Width), 0, native.Width-1);
            int y = Math.Clamp((int)(v*native.Height), 0, native.Height-1);
            int p = 4*(y*native.Width+x);
            int before = (legacy.Rgba[p]+legacy.Rgba[p+1]+legacy.Rgba[p+2])/3;
            int after = (native.Rgba[p]+native.Rgba[p+1]+native.Rgba[p+2])/3;
            if (before < 12 && after > 60) recovered++;
        }
        Assert.True(recovered >= 4, $"Expected authored pod panels; recovered {recovered}");
        var effect = File.ReadAllBytes(Path.Combine(art, "f03bmp.mdl"));
        Assert.Equal(MdlTextureDecoder.Decode(effect, flipVertical: true).Rgba, NativeEffectTextures.Resolve(effect).Texture!.Rgba);
        Assert.Equal(MdlTextureDecoder.Decode(effect, flipVertical: false).Rgba, NativeEffectTextures.Resolve(effect, flipVertical: false).Texture!.Rgba);
    }

    [SkippableFact, Trait("Requires", "Artwork"), Trait("Requires", "Gpu"), Trait("Tier", "Live")]
    public async Task Production_uploads_sample_authored_rows_for_every_hull_and_other_mesh_families()
    {
        Require.Artwork();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName,"AllegianceCore.slnx"))) root = root.Parent;
        string repo = root!.FullName;
        string output = Path.Combine(repo,".scratch/p916/evidence/corpus-test");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { Path.Combine(repo,"tools/p916-texture-proof/bin/Debug/net10.0/p916-texture-proof.dll"), "--audit", output }) start.ArgumentList.Add(arg);
        start.Environment["SDL_VIDEODRIVER"] = "offscreen"; start.Environment["ALSOFT_DRIVERS"] = "null";
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); Assert.Fail("P916 corpus proof timed out"); }
        string log = await stdout + await stderr;
        Require.GraphicsDevice(log);
        Assert.True(process.ExitCode == 0, log);
        Assert.Contains("P916_ATLAS_PASS hulls=447", log);
    }
}
