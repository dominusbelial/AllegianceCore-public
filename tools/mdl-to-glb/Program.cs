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
///   mdl-to-glb export   &lt;artDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]
///   mdl-to-glb roundtrip &lt;artDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]
///   mdl-to-glb inspect  &lt;artDir&gt; &lt;stem&gt;
///   mdl-to-glb import    &lt;glbDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]      (P823: glb+sidecar → .mdl + .cvh)
///   mdl-to-glb verifyimport &lt;artDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]   (P823: native → glb → mdl/cvh → compare)
/// </code>
///
/// Writes <c>&lt;stem&gt;.glb</c> + <c>&lt;stem&gt;.glbside.json</c>. This is a
/// proof tool (investigation tasks P812/P823): nothing here runs in the game.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("""
                usage:
                  mdl-to-glb export       <artDir> <stem> [--out <dir>]
                  mdl-to-glb roundtrip    <artDir> <stem> [--out <dir>]
                  mdl-to-glb inspect      <artDir> <stem>
                  mdl-to-glb import       <glbDir> <stem> [--out <dir>]
                  mdl-to-glb verifyimport <artDir> <stem> [--out <dir>]
                """);
            return 2;
        }

        var artDir = args[1];
        var stem = args[2];
        var outDir = GetOption(args, "--out") ?? ".";
        Directory.CreateDirectory(outDir);

        return args[0] switch
        {
            "export" => Export(artDir, stem, outDir),
            "roundtrip" => Roundtrip(artDir, stem, outDir),
            "inspect" => Inspect(artDir, stem),
            "import" => Import(glbDir(args[1]), stem, outDir),
            "verifyimport" => VerifyImport(artDir, stem, outDir),
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

    private static int Export(string artDir, string stem, string outDir)
    {
        var (_, _, model) = Load(artDir, stem);
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
    /// + <c>&lt;stem&gt;.cvh</c> (native's shipping formats).</summary>
    private static int Import(string glbDir, string stem, string outDir)
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

        byte[] mdl;
        string cvh;
        try
        {
            (mdl, cvh) = GlbImporter.Convert(model);
        }
        catch (InvalidDataException ex)
        {
            Console.Error.WriteLine($"import failed: {ex.Message}");
            return 1;
        }

        var mdlPath = Path.Combine(outDir, stem + ".mdl");
        var cvhPath = Path.Combine(outDir, stem + ".cvh");
        File.WriteAllBytes(mdlPath, mdl);
        File.WriteAllText(cvhPath, cvh);

        Console.WriteLine($"imported {mdlPath} ({mdl.Length} bytes) + {cvhPath} ({cvh.Length} bytes)");
        Console.WriteLine($"  meshes={model.Meshes.Count} mounts={model.Mounts.Count} " +
                          $"lights={model.Lights.Count} collisionHulls={model.Collision?.Hulls.Count ?? 0} (sidecar) / generated");
        return 0;
    }

    /// <summary>Native → glb (P812) → mdl/cvh (P823): write the converted
    /// files, parse both sides with the port's own loaders, compare structures
    /// numerically and diff the bytes. The report's round-trip evidence.</summary>
    private static int VerifyImport(string artDir, string stem, string outDir)
    {
        var (mdl, cvh, original) = Load(artDir, stem);

        // P812 export (in memory + on disk for reference).
        byte[] glb;
        string sidecarJson;
        using (var ms = new MemoryStream())
        {
            GlbWriter.Write(original, ms);
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

        var ok = pass && cvhPass && mdlPass;
        Console.WriteLine(ok ? $"VERIFY-IMPORT OK: {stem}" : $"VERIFY-IMPORT FAILED: {stem}");
        return ok ? 0 : 1;
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
