using System.Reflection;
using Allegiance.Core;
using Allegiance.TestInfra;
using Allegiance.Tools.MdlToGlb;
using Xunit;

namespace Allegiance.Core.Tests;

public sealed class P916TextureExportTests
{
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData("utl203")]
    [InlineData("wc_bifig")]
    [InlineData("cap01")]
    [InlineData("ss27")]
    [InlineData("bgrnd03")]
    public void Blender_export_keeps_native_rows_and_UVs(string stem)
    {
        Require.Artwork();
        string art = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var native = MdlFile.Parse(File.ReadAllBytes(Path.Combine(art, stem+".mdl")));
        var model = NativeModelAdapter.FromNative(stem, native, null);
        // Exercise the CLI's real export binding, which used to embed reversed
        // rows. The codecs' legacy defaults themselves remain unchanged.
        var embed = typeof(GlbWriter).Assembly.GetType("Allegiance.Tools.MdlToGlb.Program")!
            .GetMethod("EmbedTextures", BindingFlags.NonPublic | BindingFlags.Static)!;
        var embedded = (GlbExportModel)embed.Invoke(null, [model,art,false])!;
        using var glb = new MemoryStream(); GlbWriter.Write(embedded, glb); glb.Position = 0;
        var imported = GlbReader.Read(glb, null);
        foreach (var image in imported.Images!)
        {
            var decoded = MdlTextureDecoder.Decode(File.ReadAllBytes(Path.Combine(art,image.Stem+".mdl")), flipVertical: false);
            Assert.Equal(decoded.Rgba, PngCodec.Decode(image.Png).Rgba);
            var written = MdlTextureWriter.Write(image.Stem,decoded.Width,decoded.Height,decoded.Rgba,new MdlTextureWriteOptions(FlipVertical:false));
            Assert.Equal(decoded.Rgba, MdlTextureDecoder.Decode(written,flipVertical:false).Rgba);
        }
        for (int i = 0; i < model.Meshes.Count; i++) Assert.Equal(model.Meshes[i].TexCoords, imported.Meshes[i].TexCoords);
    }
}
