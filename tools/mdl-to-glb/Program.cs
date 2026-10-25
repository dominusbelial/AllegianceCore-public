using Allegiance.Core;
using Allegiance.Core.Collision;

namespace Allegiance.Tools.MdlToGlb;

/// <summary>
/// P812 — prototype: export a native hull (compiled <c>.mdl</c> mesh +
/// <c>.cvh</c> collision) to a glTF 2.0 <c>.glb</c> with hardpoints as
/// <c>HP_&lt;Kind&gt;_&lt;Index&gt;</c> empty nodes and a collision sidecar,
/// then re-import it and compare numerically.
///
/// <code>
///   mdl-to-glb export   &lt;artDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]
///   mdl-to-glb roundtrip &lt;artDir&gt; &lt;stem&gt; [--out &lt;dir&gt;]
///   mdl-to-glb inspect  &lt;artDir&gt; &lt;stem&gt;
/// </code>
///
/// Writes <c>&lt;stem&gt;.glb</c> + <c>&lt;stem&gt;.glbside.json</c>. This is a
/// proof tool (investigation task P812): nothing here runs in the game.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("""
                usage:
                  mdl-to-glb export    <artDir> <stem> [--out <dir>]
                  mdl-to-glb roundtrip <artDir> <stem> [--out <dir>]
                  mdl-to-glb inspect   <artDir> <stem>
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
            _ => Usage(),
        };
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
