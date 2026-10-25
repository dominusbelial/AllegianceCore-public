using System.Buffers.Binary;
using System.Text;
using Allegiance.Core;
using Allegiance.Core.Collision;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P812/P823 — prototype: export a native hull (compiled <c>.mdl</c> mesh +
/// <c>.cvh</c> collision) to a glTF 2.0 <c>.glb</c> with hardpoints as
/// <c>HP_&lt;Kind&gt;_&lt;Index&gt;</c> empty nodes and a collision sidecar,
/// then re-import it and compare numerically. P823 adds the writer direction:
/// a <c>.glb</c> (+ sidecar) converts back to native's shipping formats.
///
/// <code>
///   mdl-to-glb export   &lt;artDir&gt; &lt;stem&gt; [--out &lt;dir&gt;] [--no-texture]
///   mdl-to-glb roundtrip &lt;artDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]
///   mdl-to-glb inspect  &lt;artDir&gt; &lt;stem&gt;
///   mdl-to-glb import    &lt;glbDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]      (P823: glb+sidecar → .mdl + .cvh)
///                        [--texture-stem &lt;stem&gt;] [--no-texture]   (P824: + &lt;stem&gt;.mdl texture)
///   mdl-to-glb verifyimport &lt;artDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]   (P823: native → glb → mdl/cvh → compare)
///   mdl-to-glb gentexture  &lt;outDir&gt; &lt;stem&gt; [--size 128]     (P824: built-in proof texture, checker + corner markers)
///   mdl-to-glb gentesthull &lt;outDir&gt; &lt;stem&gt; [--size 128]     (P824: proof hull + proof texture → glb+sidecar)
/// </code>
///
/// Writes <c>&lt;stem&gt;.glb</c> + <c>&lt;stem&gt;.glbside.json</c>. This is a
/// proof tool (investigation tasks P812/P823/P824): nothing here runs in the game.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("""
                usage:
                  mdl-to-glb export       <artDir> <stem> [--out <dir>] [--no-texture]
                  mdl-to-glb roundtrip    <artDir> <stem> [--out <dir>]
                  mdl-to-glb inspect      <artDir> <stem>
                  mdl-to-glb import       <glbDir> <stem> [--out <dir>] [--texture-stem <stem>] [--no-texture]
                  mdl-to-glb verifyimport <artDir> <stem> [--out <dir>]
                  mdl-to-glb gentexture   <outDir> <stem> [--size 128]
                  mdl-to-glb gentesthull  <outDir> <stem> [--size 128]
                """);
            return 2;
        }

        var artDir = args[1];
        var stem = args[2];
        var outDir = GetOption(args, "--out") ?? ".";
        Directory.CreateDirectory(outDir);

        return args[0] switch
        {
            "export" => Export(artDir, stem, outDir, noTexture: HasFlag(args, "--no-texture")),
            "roundtrip" => Roundtrip(artDir, stem, outDir),
            "inspect" => Inspect(artDir, stem),
            "import" => Import(glbDir(args[1]), stem, outDir,
                textureStem: GetOption(args, "--texture-stem"),
                noTexture: HasFlag(args, "--no-texture")),
            "verifyimport" => VerifyImport(artDir, stem, outDir),
            "gentexture" => GenTextureCommand.Run(artDir, stem, ParseIntOption(args, "--size", 128)),
            "gentesthull" => GenTestHullCommand.Run(artDir, stem, ParseIntOption(args, "--size", 128)),
            _ => Usage(),
        };

        // `import` takes the directory holding the .glb, not an art dir.
        static string glbDir(string a) => a;
    }

    private static string? GetOption(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;

    private static int ParseIntOption(string[] args, string name, int fallback) =>
        int.TryParse(GetOption(args, name), out var v) ? v : fallback;

    private static int Usage() { Console.Error.WriteLine("unknown command"); return 2; }

    // -- shared load ---------------------------------------------------------

    internal static (MdlFile mdl, CvhFile? cvh, GlbExportModel model) Load(string artDir, string stem)
    {
        var mdlPath = Path.Combine(artDir, stem + ".mdl");
        var cvhPath = Path.Combine(artDir, stem + ".cvh");
        var mdl = MdlFile.Parse(File.ReadAllBytes(mdlPath));
        var cvh = File.Exists(cvhPath) ? CvhReader.Parse(File.ReadAllText(cvhPath)) : null;
        return (mdl, cvh, NativeModelAdapter.FromNative(stem, mdl, cvh));
    }

    // -- export --------------------------------------------------------------

    private static int Export(string artDir, string stem, string outDir, bool noTexture)
    {
        var (_, _, model) = Load(artDir, stem);
        model = EmbedTextures(model, artDir, noTexture);
        var glbPath = Path.Combine(outDir, stem + ".glb");
        var sidePath = Path.Combine(outDir, stem + ".glbside.json");

        using (var fs = File.Create(glbPath))
            GlbWriter.Write(model, fs);
        var sidecar = SidecarDocument.Build(model, Path.Combine(artDir, stem + ".mdl"), Path.Combine(artDir, stem + ".cvh"));
        File.WriteAllText(sidePath, sidecar.ToJson());

        Console.WriteLine($"exported {glbPath} ({new FileInfo(glbPath).Length} bytes) + {sidePath}");
        PrintSummary(model);
        return 0;
    }

    /// <summary>P824 — decode each texture stem's native <c>&lt;stem&gt;.mdl</c>
    /// texture (the file the hull's TextureGeo imports, i.e. <c>fig30bmp</c> →
    /// <c>fig30bmp.mdl</c>) and embed it as the GLB's base-colour PNG. The
    /// decoded RGBA is the decoder's 3-D <c>flipVertical:true</c> output — the
    /// exact texture the port's renderer uploads — which is also glTF's
    /// top-left-origin convention, so mesh UVs sample identically in Blender.
    /// Missing texture files are reported and the GLB stays untextured.</summary>
    internal static GlbExportModel EmbedTextures(GlbExportModel model, string artDir, bool noTexture)
    {
        if (noTexture || model.TextureStems.Count == 0)
            return model;
        var images = new List<GlbImage>();
        foreach (var texStem in model.TextureStems)
        {
            var texPath = Path.Combine(artDir, texStem + ".mdl");
            if (!File.Exists(texPath))
            {
                Console.WriteLine($"  (no texture file for stem '{texStem}' at {texPath} — GLB will be untextured)");
                continue;
            }
            var tex = MdlTextureDecoder.Decode(File.ReadAllBytes(texPath));   // flipVertical:true = GPU order
            images.Add(new GlbImage(texStem, tex.Width, tex.Height, PngCodec.Encode(tex.Width, tex.Height, tex.Rgba)));
            Console.WriteLine($"  texture {texStem}: {tex.Width}×{tex.Height} embedded as base-colour PNG");
        }
        return images.Count > 0 ? model with { Images = images } : model;
    }

    private static void PrintSummary(GlbExportModel model)
    {
        Console.WriteLine($"  meshes={model.Meshes.Count} tris={model.Meshes.Sum(m => m.Indices.Length / 3)} " +
                          $"verts={model.Meshes.Sum(m => m.Positions.Length / 3)}");
        Console.WriteLine($"  mounts={model.Mounts.Count}: {string.Join(", ", model.Mounts.Select(m => $"{m.NodeName}({m.NativeName})"))}");
        Console.WriteLine($"  lights={model.Lights.Count} lodLevels={model.LodPixelRadii.Count} " +
                          $"collisionHulls={model.Collision?.Hulls.Count ?? 0} collisionFrames={model.Collision?.Frames.Count ?? 0}");
    }

    // -- round trip ----------------------------------------------------------

    private static int Roundtrip(string artDir, string stem, string outDir)
    {
        var (mdl, cvh, original) = Load(artDir, stem);

        var glbPath = Path.Combine(outDir, stem + ".glb");
        var sidePath = Path.Combine(outDir, stem + ".glbside.json");
        if (!File.Exists(glbPath) || !File.Exists(sidePath))
        {
            Console.Error.WriteLine($"missing {glbPath} or {sidePath} — run export first");
            return 2;
        }

        using var glb = File.OpenRead(glbPath);
        using var side = File.OpenRead(sidePath);
        var reimported = GlbReader.Read(glb, side);

        var (results, pass) = Compare.Run(original, reimported);
        Console.WriteLine($"round-trip {stem}: {mdl.Meshes.Count} mdl meshes, {cvh?.Hulls.Count ?? 0} cvh hulls");
        foreach (var r in results)
            Console.WriteLine($"  {(r.Pass ? "PASS" : "FAIL")} {r.Name,-24} {r.Detail}");
        Console.WriteLine(pass ? $"ROUND-TRIP OK: {stem}" : $"ROUND-TRIP FAILED: {stem}");
        return pass ? 0 : 1;
    }

    // -- P823: glb → mdl/cvh -------------------------------------------------

    /// <summary>Convert <c>&lt;dir&gt;/&lt;stem&gt;.glb</c> (+ optional
    /// <c>&lt;stem&gt;.glbside.json</c>) to <c>&lt;out&gt;/&lt;stem&gt;.mdl</c>
    /// + <c>&lt;stem&gt;.cvh</c> (native's shipping formats). P824: when the GLB
    /// carries base-colour images the matching <c>&lt;texstem&gt;.mdl</c>
    /// texture units are written next to them (stem from the material name, or
    /// <c>--texture-stem</c> to override; <c>--no-texture</c> strips the
    /// texture references instead of failing when an image is missing).</summary>
    private static int Import(string glbDir, string stem, string outDir, string? textureStem, bool noTexture)
    {
        var glbPath = Path.Combine(glbDir, stem + ".glb");
        var sidePath = Path.Combine(glbDir, stem + ".glbside.json");
        if (!File.Exists(glbPath))
        {
            Console.Error.WriteLine($"missing {glbPath}");
            return 2;
        }

        GlbExportModel model;
        using (var glb = File.OpenRead(glbPath))
        {
            FileStream? side = File.Exists(sidePath) ? File.OpenRead(sidePath) : null;
            try
            {
                model = GlbReader.Read(glb, side);
            }
            finally
            {
                side?.Dispose();
            }
        }

        try
        {
            // P824 — resolve the texture-stem naming before validating.
            if (textureStem is not null)
            {
                model = model with
                {
                    Meshes = model.Meshes.Select(m => m.TextureStem is null ? m : m with { TextureStem = textureStem }).ToArray(),
                    TextureStems = model.TextureStems,
                    Images = model.Images?.Select(img => img with { Stem = textureStem }).ToArray(),
                };
                model = model with
                {
                    TextureStems = model.Meshes.Select(m => m.TextureStem).OfType<string>().Distinct().ToArray(),
                };
            }
            if (noTexture)
            {
                model = model with
                {
                    Meshes = model.Meshes.Select(m => m with { TextureStem = null }).ToArray(),
                    TextureStems = [],
                    Images = null,
                };
            }
            else if (model.Meshes.Any(m => m.TextureStem is not null))
            {
                // A textured mesh must have a base-colour image behind every stem.
                GlbImporter.ValidateTextureImages(model);
            }

            byte[] mdl;
            string cvh;
            (mdl, cvh) = GlbImporter.Convert(model);

            var mdlPath = Path.Combine(outDir, stem + ".mdl");
            var cvhPath = Path.Combine(outDir, stem + ".cvh");
            File.WriteAllBytes(mdlPath, mdl);
            File.WriteAllText(cvhPath, cvh);

            // P824 — write each embedded base-colour image as the native
            // <stem>.mdl texture unit the written hull file imports.
            foreach (var image in model.Images ?? [])
            {
                var png = PngCodec.Decode(image.Png);
                var texPath = Path.Combine(outDir, image.Stem + ".mdl");
                var tex = MdlTextureWriter.Write(image.Stem, png.Width, png.Height, png.Rgba);
                File.WriteAllBytes(texPath, tex);
                Console.WriteLine($"  texture {image.Stem}: {png.Width}×{png.Height} PNG → {texPath} ({tex.Length} bytes, native BinarySurfaceInfo layout)");
            }

            Console.WriteLine($"imported {mdlPath} ({mdl.Length} bytes) + {cvhPath} ({cvh.Length} bytes)");
            Console.WriteLine($"  meshes={model.Meshes.Count} mounts={model.Mounts.Count} " +
                              $"lights={model.Lights.Count} collisionHulls={model.Collision?.Hulls.Count ?? 0} (sidecar) / generated");
            return 0;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"import failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Native → glb (P812, + P824 embedded texture) → mdl/cvh (P823,
    /// + P824 texture units): write the converted files, parse both sides with
    /// the port's own loaders, compare structures numerically and diff the
    /// bytes — mesh, hull AND texture. The report's round-trip evidence.</summary>
    private static int VerifyImport(string artDir, string stem, string outDir)
    {
        var (mdl, cvh, original) = Load(artDir, stem);
        var withTextures = EmbedTextures(original, artDir, noTexture: false);

        // P812 export (in memory + on disk for reference).
        byte[] glb;
        string sidecarJson;
        using (var ms = new MemoryStream())
        {
            GlbWriter.Write(withTextures, ms);
            glb = ms.ToArray();
        }
        sidecarJson = SidecarDocument.Build(original, Path.Combine(artDir, stem + ".mdl"),
            Path.Combine(artDir, stem + ".cvh")).ToJson();
        File.WriteAllBytes(Path.Combine(outDir, stem + ".glb"), glb);
        File.WriteAllText(Path.Combine(outDir, stem + ".glbside.json"), sidecarJson);

        // P823 import.
        byte[] writtenMdl;
        string writtenCvh;
        using (var glbStream = new MemoryStream(glb))
        using (var sideStream = new MemoryStream(Encoding.UTF8.GetBytes(sidecarJson)))
        {
            var reimported = GlbReader.Read(glbStream, sideStream);
            (writtenMdl, writtenCvh) = GlbImporter.Convert(reimported);
        }
        var writtenMdlPath = Path.Combine(outDir, stem + ".written.mdl");
        var writtenCvhPath = Path.Combine(outDir, stem + ".written.cvh");
        File.WriteAllBytes(writtenMdlPath, writtenMdl);
        File.WriteAllText(writtenCvhPath, writtenCvh);

        Console.WriteLine($"verifyimport {stem}: native={new FileInfo(Path.Combine(artDir, stem + ".mdl")).Length} bytes, written={writtenMdl.Length} bytes");

        // Byte diffs.
        DiffBytes(".mdl", File.ReadAllBytes(Path.Combine(artDir, stem + ".mdl")), writtenMdl);
        DiffText(".cvh", File.ReadAllText(Path.Combine(artDir, stem + ".cvh")), writtenCvh);

        // Parsed-structure comparison (the writer's contract).
        var writtenModel = NativeModelAdapter.FromNative(stem, MdlFile.Parse(writtenMdl),
            CvhReader.Parse(writtenCvh));
        var (results, pass) = Compare.Run(original, writtenModel);
        foreach (var r in results)
            Console.WriteLine($"  {(r.Pass ? "PASS" : "FAIL")} {r.Name,-24} {r.Detail}");

        var parsedWritten = CvhReader.Parse(writtenCvh);
        var (cvhResults, cvhPass) = CvhCompare.Run(cvh, parsedWritten);
        foreach (var r in cvhResults)
            Console.WriteLine($"  {(r.Pass ? "PASS" : "FAIL")} {r.Name,-24} {r.Detail}");

        // The parsed .mdl's frames and lights must be bit-exact too (Compare
        // above works on the GLB-shaped model, which never sees the up vectors).
        var parsedOriginalMdl = MdlFile.Parse(File.ReadAllBytes(Path.Combine(artDir, stem + ".mdl")));
        var parsedWrittenMdl = MdlFile.Parse(writtenMdl);
        var (mdlResults, mdlPass) = MdlFileCompare.Run(parsedOriginalMdl, parsedWrittenMdl);
        foreach (var r in mdlResults)
            Console.WriteLine($"  {(r.Pass ? "PASS" : "FAIL")} {r.Name,-24} {r.Detail}");

        // P824 — texture: the embedded base-colour PNG must re-encode to the
        // native texture file byte-for-byte (except the explained pad bytes).
        GlbExportModel reimportedWithTextures;
        using (var glbStream = new MemoryStream(glb))
            reimportedWithTextures = GlbReader.Read(glbStream, null);
        var texturePass = VerifyTextureRoundTrip(artDir, reimportedWithTextures);

        var ok = pass && cvhPass && mdlPass && texturePass;
        Console.WriteLine(ok ? $"VERIFY-IMPORT OK: {stem}" : $"VERIFY-IMPORT FAILED: {stem}");
        return ok ? 0 : 1;
    }

    /// <summary>P824 — for each embedded base-colour image in the re-imported
    /// GLB: PNG → RGBA (GPU order) → <see cref="MdlTextureWriter.Write"/> →
    /// byte-diff against the original native texture file. Expected diffs are
    /// ONLY the 3 uninitialised BinarySurfaceInfo padding bytes mdlc leaves in
    /// the file (the writer zeroes them; the reader ignores them) — anything
    /// else fails.</summary>
    private static bool VerifyTextureRoundTrip(string artDir, GlbExportModel reimported)
    {
        var ok = true;
        foreach (var image in reimported.Images ?? [])
        {
            var originalPath = Path.Combine(artDir, image.Stem + ".mdl");
            if (!File.Exists(originalPath)) { Console.WriteLine($"  FAIL texture {image.Stem}: no original file"); ok = false; continue; }
            var originalTex = File.ReadAllBytes(originalPath);
            var png = PngCodec.Decode(image.Png);
            var writtenTex = MdlTextureWriter.Write(image.Stem, png.Width, png.Height, png.Rgba);

            // The BinarySurfaceInfo padding sits 8 bytes before the structure
            // end (alphaMask 4 + colourKey 1 + pad 3); the decode/write round
            // trip may differ exactly there — nowhere else.
            int structEnd = originalTex.Length - MdlTextureWriter.PitchFor(png.Width) * png.Height - 4;  // − trailing ObjectEnd
            int padStart = structEnd - 3;   // the 3 trailing pad bytes of the 36-byte BinarySurfaceInfo
            bool allDiffsInPad = true;
            int diffCount = 0;
            for (int i = 0; i < Math.Min(originalTex.Length, writtenTex.Length); i++)
                if (originalTex[i] != writtenTex[i])
                {
                    diffCount++;
                    if (i < padStart) allDiffsInPad = false;
                }
            if (originalTex.Length != writtenTex.Length || diffCount > 3 || !allDiffsInPad)
            {
                Console.WriteLine($"  FAIL texture {image.Stem}: {diffCount} differing bytes (pad at [{padStart},{structEnd}))");
                ok = false;
            }
            else
            {
                Console.WriteLine($"  PASS texture {image.Stem}: byte round trip " +
                    (diffCount == 0 ? "identical" : $"differs only in the {diffCount} uninitialised BinarySurfaceInfo pad bytes"));
            }
        }
        return ok;
    }

    /// <summary>Byte-level diff: differing-byte count + the differing regions
    /// (up to 12), so the report can attribute every changed byte.</summary>
    private static void DiffBytes(string label, byte[] a, byte[] b)
    {
        if (a.SequenceEqual(b))
        {
            Console.WriteLine($"  BYTE-DIFF {label}: identical ({a.Length} bytes)");
            return;
        }
        int diffCount = 0;
        int first = -1;
        var regions = new List<(int start, int end)>();
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] == b[i]) continue;
            if (first < 0) first = i;
            diffCount++;
            if (regions.Count == 0 || regions[^1].end < i)
                regions.Add((i, i));
            else
                regions[^1] = (regions[^1].start, i);
        }
        if (a.Length != b.Length)
            regions.Add((n, Math.Max(a.Length, b.Length)));
        Console.WriteLine($"  BYTE-DIFF {label}: {diffCount} differing bytes of {Math.Max(a.Length, b.Length)}, " +
                          $"first at 0x{Math.Max(first, 0):X}, length {a.Length} vs {b.Length}");
        foreach (var (start, end) in regions.Take(12))
            Console.WriteLine($"    region 0x{start:X}-0x{end:X} ({end - start + 1} bytes)");
        if (regions.Count > 12)
            Console.WriteLine($"    … {regions.Count - 12} more regions");
    }

    private static void DiffText(string label, string a, string b)
        => DiffBytes(label, Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // -- inspect -------------------------------------------------------------

    private static int Inspect(string artDir, string stem)
    {
        var (mdl, cvh, model) = Load(artDir, stem);
        Console.WriteLine($"{stem}: exports=[{string.Join(",", mdl.Exports)}] imports=[{string.Join(",", mdl.Imports)}]");
        PrintSummary(model);
        var cvhMdlMismatch = 0;
        foreach (var m in model.Mounts.Where(m => m.MdlForward is not null))
        {
            var d = Delta(m.Forward, m.MdlForward!);
            if (d > 0.01) cvhMdlMismatch++;
        }
        Console.WriteLine($"  cvh-vs-mdl forward disagreements: {cvhMdlMismatch} (positions agree to <1e-4; see report)");
        return 0;
    }

    private static double Delta(float[] a, float[] b)
    {
        double worst = 0;
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
            worst = Math.Max(worst, Math.Abs(a[i] - b[i]));
        return worst;
    }
}

// -- P824: built-in proof texture -------------------------------------------

/// <summary>Write the built-in proof texture (checker + corner markers, see
/// <see cref="TestTextureGenerator"/>) as a native <c>&lt;stem&gt;.mdl</c>
/// texture unit. Used by the new-art render proof: the marker must land where
/// the mesh UVs say, asserted numerically by tools/p823-render-proof.</summary>
internal static class GenTextureCommand
{
    public static int Run(string outDir, string stem, int size)
    {
        try
        {
            var rgba = TestTextureGenerator.BuildRgba(size);
            var bytes = MdlTextureWriter.Write(stem, size, size, rgba);
            var path = Path.Combine(outDir, stem + ".mdl");
            File.WriteAllBytes(path, bytes);
            Console.WriteLine($"wrote {path} ({bytes.Length} bytes, {size}×{size} proof texture: " +
                              "checker + TL magenta / TR yellow / BL cyan / BR green markers)");
            return 0;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"gentexture failed: {ex.Message}");
            return 1;
        }
    }
}

// -- P824: built-in new-art proof hull ----------------------------------------

/// <summary>Write the P823 hand-built new-art proof hull (the octahedron from
/// <c>GlbToMdlTests.NewArtModel</c>: 6 verts, 8 tris, nose +Z, UV corners on
/// the four hull vertices) as <c>&lt;stem&gt;.glb</c> + sidecar, with the
/// built-in proof texture (<see cref="TestTextureGenerator"/>) embedded as the
/// base-colour image. Combined with <c>import</c> this is the whole new-art
/// chain in code: model + texture → GLB → native <c>.mdl</c> +
/// <c>.cvh</c> + <c>hullbmp.mdl</c> → render proof.</summary>
internal static class GenTestHullCommand
{
    public static int Run(string outDir, string stem, int textureSize)
    {
        try
        {
            var rgba = TestTextureGenerator.BuildRgba(textureSize);
            var model = new GlbExportModel
            {
                Name = stem,
                Meshes =
                [
                    new GlbMesh(stem + "_hullbmp_0", "hullbmp",
                        Positions:
                        [
                            0f, 0f, 3f,      // nose  — UV (0,0) → TL marker
                            1f, 0f, 0f,      // +x    — UV (1,0) → TR marker
                            0f, 1f, 0f,      // +y    — UV (0,1) → BL marker
                            -1f, 0f, 0f,     // -x
                            0f, -1f, 0f,     // -y
                            0f, 0f, -3f,     // tail  — UV (1,1) → BR marker
                        ],
                        TexCoords: [0f, 0f, 1f, 0f, 0f, 1f, 1f, 1f, 0.5f, 0.5f, 0.25f, 0.75f],
                        Normals:
                        [
                            0f, 0f, 1f, 1f, 0f, 0f, 0f, 1f, 0f,
                            -1f, 0f, 0f, 0f, -1f, 0f, 0f, 0f, -1f,
                        ],
                        Indices:
                        [
                            0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1,       // front pyramid
                            5, 2, 1, 5, 3, 2, 5, 4, 3, 5, 1, 4,       // rear pyramid
                        ]),
                ],
                Mounts =
                [
                    new MountPoint { NodeName = "HP_Weapon_0", Kind = "Weapon", Index = 0, NativeName = "HP_Weapon_0",
                        Sources = ["glb"], Position = [1f, 0f, 1f], Forward = [0f, 0f, 1f] },
                    new MountPoint { NodeName = "HP_MainEngine_0", Kind = "MainEngine", Index = 0, NativeName = "HP_MainEngine_0",
                        Sources = ["glb"], Position = [0f, 0f, -3f], Forward = [0f, 0f, -1f] },
                    new MountPoint { NodeName = "HP_Cockpit_0", Kind = "Cockpit", Index = 0, NativeName = "HP_Cockpit_0",
                        Sources = ["glb"], Position = [0f, 0.5f, 1f], Forward = [0f, 0f, 1f] },
                ],
                Lights = [],
                LodPixelRadii = [],
                TextureStems = ["hullbmp"],
                Collision = null,
                Images = [new GlbImage("hullbmp", textureSize, textureSize, PngCodec.Encode(textureSize, textureSize, rgba))],
            };

            GlbImporter.Validate(model);
            var glbPath = Path.Combine(outDir, stem + ".glb");
            using (var fs = File.Create(glbPath))
                GlbWriter.Write(model, fs);
            var sidecar = new SidecarDocument
            {
                Format = SidecarDocument.FormatId,
                Mdl = stem + ".mdl",
                Cvh = stem + ".cvh",
                MdlSha256 = "",
                CvhSha256 = "",
                ModelSpace = GlbExportModel.ModelSpace,
                ScaleHint = "synthetic proof hull",
                IndexWinding = "native DirectX clockwise-front (raw order)",
                TextureStems = model.TextureStems,
                LodPixelRadii = model.LodPixelRadii,
                Lights = model.Lights,
                Mounts = model.Mounts,
                Collision = model.Collision,
                MdlFrames = model.SourceMdlFrames,
            };
            var sidePath = Path.Combine(outDir, stem + ".glbside.json");
            File.WriteAllText(sidePath, sidecar.ToJson());
            Console.WriteLine($"wrote {glbPath} ({new FileInfo(glbPath).Length} bytes) + {sidePath} " +
                              $"(proof hull with {textureSize}×{textureSize} proof texture embedded)");
            return 0;
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"gentesthull failed: {ex.Message}");
            return 1;
        }
    }
}
