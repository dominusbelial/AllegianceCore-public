using Allegiance.Core;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Tools.MdlToGlb;

internal static class DesktopProofContent
{
    internal static void WriteArtwork(string work)
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

    internal static void WriteFonts(string work)
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
}
