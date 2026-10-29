using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// P870 dev tool: native-compatible static-core text round trip.
///
///   core2txt Core2Text &lt;core.igc&gt; [outDir]   .igc → text folder
///   core2txt Text2Core &lt;textDir&gt; [out.igc]   text folder → .igc
///   core2txt SelfTest  &lt;core.igc&gt;...         per-core byte-identity check
///   core2txt SelfTest --all [artworkDir]    every static core in the corpus
///
/// Mirrors native AllSrv -Core2Text / -Text2Core (FedSrv/FedSrv.CPP:10385-10480).
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        StaticCoreRoundtrip.RegisterCodecs();

        if (args.Length == 0)
        {
            Console.Error.WriteLine(
                "usage: core2txt Core2Text <core.igc> [outDir] | Text2Core <textDir> [out.igc] | " +
                "SelfTest <core.igc>... | SelfTest --all [artworkDir]");
            return 2;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "core2text":
            {
                if (args.Length < 2) return Usage();
                var igc = Path.GetFullPath(args[1]);
                var dir = args.Length > 2
                    ? Path.GetFullPath(args[2])
                    : Path.Combine(Path.GetDirectoryName(igc)!, Path.GetFileNameWithoutExtension(igc) + ".txt");
                var file = IgcReader.ParseFile(igc);
                if (file.Wrapper != IgcWrapper.VersionAndDataSize)
                {
                    Console.Error.WriteLine($"{igc} is not a static core (wrapper {file.Wrapper})");
                    return 1;
                }
                StaticCoreRoundtrip.Export(file, dir);
                Console.WriteLine($"wrote text to {dir}");
                return 0;
            }
            case "text2core":
            {
                if (args.Length < 2) return Usage();
                var dir = Path.GetFullPath(args[1]);
                var outPath = args.Length > 2 ? Path.GetFullPath(args[2]) : dir.TrimEnd('\\', '/') + ".igc";
                File.WriteAllBytes(outPath, StaticCoreRoundtrip.BuildIgc(dir));
                Console.WriteLine($"wrote {outPath}");
                return 0;
            }
            case "selftest":
            {
                if (args.Length < 2) return Usage();
                IEnumerable<string> cores;
                if (args[1] == "--all")
                {
                    string root = args.Length > 2
                        ? Path.GetFullPath(args[2])
                        : ExternalPaths.Require(ExternalResourceKind.Artwork);
                    cores = StaticCoreVerify.StaticCoresIn(root);
                }
                else
                {
                    cores = args.Skip(1).Select(Path.GetFullPath);
                }

                int bad = 0, total = 0;
                foreach (string p in cores)
                {
                    var r = StaticCoreVerify.VerifyFile(p);
                    total++;
                    if (!r.Ok) bad++;
                    Console.WriteLine($"{Path.GetFileName(p)}: {(r.Ok ? "PASS" : "FAIL")} — {r.Detail}");
                }
                Console.WriteLine($"{total - bad}/{total} static cores byte-identical");
                return bad == 0 ? 0 : 1;
            }
            default:
                Console.Error.WriteLine($"unknown command '{args[0]}'");
                return Usage();
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            "usage: core2txt Core2Text <core.igc> [outDir] | Text2Core <textDir> [out.igc] | " +
            "SelfTest <core.igc>... | SelfTest --all [artworkDir]");
        return 2;
    }
}
