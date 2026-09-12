using System.Security.Cryptography;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

//
// corpusvalidate — walk the whole .igc corpus, load every static core and every
// map, assemble each mission and run CrossRefValidator, then compare the
// aggregate counts against a locked baseline. The point is a REGRESSION BASELINE,
// not a purity test: pre-existing data quirks are counted, reported and recorded
// in the baseline; the tool fails ONLY when those counts CHANGE.
//
// Usage:
//   dotnet run --project tools/corpusvalidate              # default roots
//   dotnet run --project tools/corpusvalidate -- --root <dir> [--root <dir>...]
//   dotnet run --project tools/corpusvalidate -- --emit-baseline
//
// --emit-baseline prints a C# snippet of the current aggregate counts, ready to
// paste into the Expected baseline below — the explicit act of acknowledging a
// change (run it, edit Expected, commit, update README). Without a change,
// running plain is enough and exits 0.
//
// Every map is assembled against a single representative core (the first core
// in deterministic order). Nothing in a map file names its governing core, so
// the corpus-wide convention (same as MissionAssemblyTests) is this loose
// pairing; map->core station-type references therefore legitimately dangle for
// maps not authored against that one core, and those are baseline-quirk counts,
// not regressions.
//
// Exit code: 0 when every file loads exactly, every map assembles, and the
// aggregate matches the baseline; 1 otherwise (broken file, failed assembly,
// or baseline drift).
//

internal static class Program
{
    private static int Main(string[] args)
    {
        var roots = new List<string>();
        bool emitBaseline = false;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--root")
            {
                if (i + 1 >= args.Length) { Console.Error.WriteLine("--root needs a path"); return 2; }
                roots.Add(args[++i]);
            }
            else if (args[i] == "--emit-baseline") emitBaseline = true;
            else { Console.Error.WriteLine($"unexpected argument '{args[i]}'"); return 2; }
        }

        // ── stage 1: discover the corpus (content-deduplicated) ──
        var corpus = Discover(roots);
        var summary = new Summary();
        var byIssueKind = new SortedDictionary<string, int>();

        Console.WriteLine($"corpus: {corpus.Count} unique .igc file(s)");
        Console.WriteLine();

        // ── stage 2: load every static core, run core-internal cross-refs ──
        var cores = new List<StaticCore>();
        var coreFiles = new List<string>();
        foreach (var path in corpus)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch (Exception e) { Console.Error.WriteLine($"  {Name(path)}: PARSE FAILED: {e.Message}"); summary.Bad++; continue; }

            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            if (!file.IsExact) { Console.Error.WriteLine($"  {Name(path)}: core body not consumed exactly (leftover)"); summary.Bad++; }

            StaticCore core;
            try { core = StaticCore.Load(file); }
            catch (Exception e) { Console.Error.WriteLine($"  {Name(path)}: LOAD FAILED: {e.Message}"); summary.Bad++; continue; }

            cores.Add(core);
            coreFiles.Add(path);
            summary.AddTypes(BucketCounts(core));

            var issues = CrossRefValidator.Validate(core);
            foreach (var i in issues) byIssueKind.Inc(i.Kind);

            Console.WriteLine($"core   {Name(path),-24} ver={core.Version,-10} {Join(BucketCounts(core))}");
            if (issues.Count > 0) Console.WriteLine($"       cross-ref issues: {Describe(issues)}");
            summary.Cores++;
        }
        Console.WriteLine();

        // ── stage 3: load every map, assemble against the representative core ──
        var rep = cores.FirstOrDefault();
        if (rep is null)
        {
            Console.Error.WriteLine("no static core found in corpus — cannot assemble maps");
            return 1;
        }
        Console.WriteLine($"representative core for map assembly: {Name(coreFiles[0])} (ver={rep.Version})");

        var civs = rep.Civilizations.Values.OrderBy(c => c.CivilizationId).Take(2).ToList();
        IReadOnlyList<SideSpec> sides = civs.Count == 1
            ? [new SideSpec(0, "Team 1", civs[0].CivilizationId)]
            : [new SideSpec(0, "Team 1", civs[0].CivilizationId),
               new SideSpec(1, "Team 2", civs[1].CivilizationId)];

        foreach (var path in corpus)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            if (!file.IsExact) { Console.Error.WriteLine($"  {Name(path)}: map body not consumed exactly (leftover)"); summary.Bad++; }

            MissionMap map;
            try { map = MissionMap.Load(file); }
            catch (Exception e) { Console.Error.WriteLine($"  {Name(path)}: MAP LOAD FAILED: {e.Message}"); summary.Bad++; continue; }

            Mission mission;
            try { mission = Mission.Assemble(rep, map, sides, new NullIgcSite()); }
            catch (Exception e) { Console.Error.WriteLine($"  {Name(path)}: ASSEMBLE FAILED: {e.Message}"); summary.Bad++; continue; }

            var issues = CrossRefValidator.Validate(rep, map);
            foreach (var i in issues) byIssueKind.Inc(i.Kind);
            summary.MapTally(mission);

            var res = mission.Warps.Count - mission.DanglingWarps.Count;
            Console.WriteLine($"map    {Name(path),-24} sectors={mission.Sectors.Count} warps={mission.Warps.Count} " +
                              $"(resolved {res}, dangling {mission.DanglingWarps.Count}) stations={mission.Sectors.Sum(s => s.Stations.Count)} " +
                              $"asteroids={mission.Sectors.Sum(s => s.Asteroids.Count)}" +
                              (issues.Count > 0 ? $" cross-ref: {Describe(issues)}" : ""));
            summary.Maps++;
        }
        Console.WriteLine();

        // ── stage 4: aggregate summary ──
        var agg = summary.ToAggregate();
        PrintSummary(agg, byIssueKind);
        Console.WriteLine();

        // ── stage 5: regression baseline check ──
        if (emitBaseline)
        {
            Console.WriteLine("── Expected baseline (paste into Program.cs, then update README) ──");
            PrintBaselineBlock(agg);
            return summary.Bad > 0 ? 1 : 0;
        }

        return VerifyAgainstBaseline(agg, summary);
    }

    private static string Name(string p) => Path.GetFileNameWithoutExtension(p);

    private static string Describe(IReadOnlyList<CrossRefIssue> issues) =>
        string.Join(" ", issues.GroupBy(i => i.Kind).Select(g => $"{g.Key}x{g.Count()}"));

    private static string Join(IEnumerable<Bucket> buckets) =>
        string.Join(", ", buckets.Select(t => $"{t.Label}={t.Count}"));

    private static readonly (string Label, Func<StaticCore, int> Count)[] BucketDefs =
    [
        ("projectile", c => c.ProjectileTypes.Count),
        ("missile", c => c.MissileTypes.Count),
        ("mine", c => c.MineTypes.Count),
        ("probe", c => c.ProbeTypes.Count),
        ("chaff", c => c.ChaffTypes.Count),
        ("civiliz", c => c.Civilizations.Count),
        ("hull", c => c.HullTypes.Count),
        ("part", c => c.PartTypes.Count),
        ("launcher", c => c.Launchers.Count),
        ("station-type", c => c.StationTypes.Count),
        ("develop", c => c.Developments.Count),
        ("drone", c => c.DroneTypes.Count),
        ("treasure", c => c.TreasureSets.Count),
    ];

    private static IEnumerable<Bucket> BucketCounts(StaticCore c) =>
        BucketDefs.Select(d => new Bucket(d.Label, d.Count(c)));

    private sealed record Bucket(string Label, int Count);

    private static List<string> DefaultRoots() => new()
    {
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork",
        Environment.GetEnvironmentVariable("ALLEGIANCE_NATIVE_SRC_PATH") ?? "/home/domingo/Development/Allegiance",
    };

    private static List<string> Discover(IReadOnlyList<string> rootList)
    {
        var rootsFinal = rootList.Count > 0 ? rootList : DefaultRoots();
        var byHash = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in rootsFinal)
        {
            if (!Directory.Exists(root)) { Console.Error.WriteLine($"warning: root '{root}' does not exist"); continue; }
            foreach (var p in Directory.EnumerateFiles(root, "*.igc", SearchOption.AllDirectories))
            {
                if (p.Contains("/.git/", StringComparison.Ordinal)) continue;
                var hash = Convert.ToHexString(MD5.HashData(File.ReadAllBytes(p)));
                if (!byHash.TryGetValue(hash, out var kept) || string.CompareOrdinal(p, kept) < 0)
                    byHash[hash] = p;
            }
        }
        return byHash.Values.ToList();
    }

    private sealed class Summary
    {
        public int Cores, Maps, Bad;
        public readonly SortedDictionary<string, int> CoreTypesTotal = new();
        public int Sectors, Warps, DanglingWarps, Stations, Asteroids;

        public void AddTypes(IEnumerable<Bucket> buckets)
        {
            foreach (var b in buckets)
                CoreTypesTotal[b.Label] = CoreTypesTotal.GetValueOrDefault(b.Label) + b.Count;
        }

        public void MapTally(Mission m)
        {
            Sectors += m.Sectors.Count;
            Warps += m.Warps.Count;
            DanglingWarps += m.DanglingWarps.Count;
            Stations += m.Sectors.Sum(s => s.Stations.Count);
            Asteroids += m.Sectors.Sum(s => s.Asteroids.Count);
        }

        public Aggregate ToAggregate() => new(
            Cores, Maps, CoreTypesTotal, Sectors, Warps, DanglingWarps, Stations, Asteroids);
    }

    private sealed record Aggregate(
        int Cores, int Maps, IReadOnlyDictionary<string, int> Types,
        int Sectors, int Warps, int DanglingWarps, int Stations, int Asteroids);

    private static void PrintSummary(Aggregate a, SortedDictionary<string, int> issues)
    {
        Console.WriteLine("── SUMMARY ──");
        Console.WriteLine($"static cores:        {a.Cores}");
        Console.WriteLine($"maps:                {a.Maps}");
        Console.WriteLine($"types loaded (sum):  {string.Join(", ", a.Types.OrderByDescending(t => t.Value).Select(t => $"{t.Key}={t.Value}"))}");
        Console.WriteLine($"sectors:             {a.Sectors}");
        Console.WriteLine($"warps:               {a.Warps}  (dangling {a.DanglingWarps})");
        Console.WriteLine($"stations:            {a.Stations}");
        Console.WriteLine($"asteroids:           {a.Asteroids}");
        Console.WriteLine($"cross-ref issues:    {(issues.Count == 0 ? "(none)" : string.Join(", ", issues.Select(kv => $"{kv.Key}={kv.Value}")))}");
    }

    private static void PrintBaselineBlock(Aggregate a)
    {
        Console.WriteLine($"        Cores: {a.Cores}, Maps: {a.Maps},");
        Console.WriteLine($"        Types: new Dictionary<string,int>{{ {string.Join(", ", a.Types.OrderBy(kv => kv.Key).Select(kv => $"[\"{kv.Key}\"]={kv.Value}"))} }},");
        Console.WriteLine($"        Sectors: {a.Sectors}, Warps: {a.Warps}, DanglingWarps: {a.DanglingWarps},");
        Console.WriteLine($"        Stations: {a.Stations}, Asteroids: {a.Asteroids},");
        Console.WriteLine($"        Date: \"{DateTime.Now:yyyy-MM-dd}\"");
    }

    private static int VerifyAgainstBaseline(Aggregate a, Summary summary)
    {
        if (Expect.Cores == 0 && Expect.Maps == 0)
        {
            Console.Error.WriteLine("NO BASELINE SET — run --emit-baseline and paste the numbers into Expect.");
            return summary.Bad > 0 ? 1 : 0;
        }

        var problems = new List<string>();
        void Chk(string what, int cur, int exp)
        { if (cur != exp) problems.Add($"{what}: current={cur}, baseline={exp}"); }
        void ChkDict(string what, IReadOnlyDictionary<string, int> cur, IReadOnlyDictionary<string, int> exp)
        {
            foreach (var k in cur.Keys.Concat(exp.Keys).Distinct().OrderBy(k => k))
            {
                var c = cur.GetValueOrDefault(k); var e = exp.GetValueOrDefault(k);
                if (c != e) problems.Add($"{what} '{k}': current={c}, baseline={e}");
            }
        }

        Chk("cores", a.Cores, Expect.Cores);
        Chk("maps", a.Maps, Expect.Maps);
        ChkDict("types", a.Types, Expect.Types);
        Chk("sectors", a.Sectors, Expect.Sectors);
        Chk("warps", a.Warps, Expect.Warps);
        Chk("dangling-warps", a.DanglingWarps, Expect.DanglingWarps);
        Chk("stations", a.Stations, Expect.Stations);
        Chk("asteroids", a.Asteroids, Expect.Asteroids);

        if (problems.Count > 0)
        {
            Console.Error.WriteLine("BASELINE DRIFT — the corpus no longer matches the recorded counts:");
            foreach (var p in problems) Console.Error.WriteLine($"  {p}");
            Console.Error.WriteLine("If the change is real, acknowledge it with --emit-baseline, update Expect,");
            Console.Error.WriteLine("commit, and update the README baseline table.");
            return 1;
        }
        if (summary.Bad > 0)
        {
            Console.Error.WriteLine($"{summary.Bad} file(s) failed to load/assemble exactly — see diagnostics above.");
            return 1;
        }
        Console.WriteLine($"MATCH: {a.Cores} cores, {a.Maps} maps match the baseline (last recorded {Expect.Date}).");
        return 0;
    }

    // 🔒 EMBEDDED REGRESSION BASELINE — the last-known-good aggregate counts
    // over the 210 unique .igc files. Regenerate ONLY when a deliberate change
    // moves a count, and update README §"Corpus validation" in the same commit.
    private sealed record Baseline(int Cores, int Maps, IReadOnlyDictionary<string, int> Types,
        int Sectors, int Warps, int DanglingWarps, int Stations, int Asteroids, string Date);

    private static readonly Baseline Expect = new(
        99, 111,
        new Dictionary<string, int>
        {
            ["chaff"] = 619, ["civiliz"] = 952, ["develop"] = 28500, ["drone"] = 6503,
            ["hull"] = 38927, ["launcher"] = 11511, ["mine"] = 782, ["missile"] = 5370,
            ["part"] = 10928, ["probe"] = 4760, ["projectile"] = 9369,
            ["station-type"] = 12659, ["treasure"] = 99,
        },
        1506, 5212, 0, 289, 973,
        "2026-08-29");
}

internal static class EnumHelpers
{
    public static void Inc(this SortedDictionary<string, int> d, string k)
    {
        d[k] = d.GetValueOrDefault(k) + 1;
    }
}
