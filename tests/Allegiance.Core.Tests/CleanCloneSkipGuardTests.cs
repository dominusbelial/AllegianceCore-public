using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Allegiance.Core.Tests;

/// <summary>
/// CI-1 — regression guard for the release workflow's test gate, which runs
/// every tier on a clean GitHub runner with NO artwork corpus and NO GPU
/// (<c>tools/run-ci-gate.sh --clean-clone</c> reproduces it locally; see
/// docs/CI_TEST_GATES.md). Run 39702545411 failed because 30 tests loaded the
/// artwork corpus / created a GL device from a plain <c>[Fact]</c>.
///
/// This is a static source scan (Roslyn, no build of the scanned code): every
/// <c>[Fact]</c>/<c>[Theory]</c> in tests/ that — directly or through helpers,
/// fields, properties or constructors of its own class — touches
/// <list type="bullet">
///   <item>any <c>Require.*()</c> / <c>Skip.If*()</c> call must be
///     <c>[SkippableFact]</c>/<c>[SkippableTheory]</c> (a plain fact reports the
///     SkipException as a failure — always checked);</item>
///   <item>the artwork corpus via <c>TestArtRoot</c> must be skippable and reach
///     <c>Require.Artwork()</c> (or <c>Require.ArtworkAvailable</c>);</item>
///   <item>a real GL device (a child process with <c>SDL_VIDEODRIVER</c> set, or
///     <c>new GraphicsDevice</c>/<c>GraphicsDeviceManager</c>) must be skippable and
///     reach <c>Require.GraphicsDevice(output)</c>.</item>
/// </list>
/// Rules 2/3 do not apply to tests the gate filters out by trait
/// (<c>Requires=Artwork|NativeSource|Gpu</c>, <c>Lane=LiveTiming</c>).
/// It cannot see dependencies hidden inside product code (P729's replay loads
/// the core through ClientMissionCoreCache), so the clean-clone gate script stays
/// the authoritative check; this catches the common in-test-file cases fast.
///
/// CI-2 — this is a Fast test, so its own runtime counts against the tier's 10 s
/// limit. It parses every test file in parallel, walks each member once for both
/// the direct facts and the names it references, and computes the referenced-member
/// transitive closure once per type instead of running a BFS per test method
/// (see docs/reports/CI2_SKIPGUARD_SPEED.md). The verdicts are unchanged.
/// </summary>
public sealed class CleanCloneSkipGuardTests
{
    private static readonly string[] FactAttributes = ["Fact", "Theory", "FactAttribute", "TheoryAttribute"];

    [Fact]
    public void Artwork_and_gpu_touching_tests_skip_on_a_clean_clone()
    {
        string tests = Path.Combine(RepoRoot(), "tests");
        var files = new List<(string Rel, string Path)>();
        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(tests, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("_shared/")) continue;
            if (rel.EndsWith(nameof(CleanCloneSkipGuardTests) + ".cs")) continue;
            files.Add((rel, file));
        }
        // Parsing + scanning is a few seconds of CPU work, almost all of it independent per
        // file, so run it in parallel. The violations are collected into a bag and sorted
        // afterwards, so the failure message is deterministic.
        var violations = new ConcurrentBag<string>();
        int scanned = 0;
        Parallel.ForEach(files, entry =>
        {
            var (types, found) = ScanFile(entry.Rel, entry.Path);
            Interlocked.Add(ref scanned, types);
            foreach (string violation in found) violations.Add(violation);
        });
        var ordered = violations.ToList();
        ordered.Sort(StringComparer.Ordinal);
        Assert.True(scanned > 100, $"scanned only {scanned} test types — wrong root?");
        Assert.True(ordered.Count == 0,
            "Tests that touch the artwork corpus / a GL device must skip on a clean clone " +
            "([SkippableFact] + Require.Artwork() / Require.GraphicsDevice(output)); see docs/CI_TEST_GATES.md:\n  " +
            string.Join("\n  ", ordered));
    }

    /// <summary>Known-safe tests the name-based scan cannot prove safe: they touch
    /// the corpus root but never read from it (key: "Class.Method", value: why).</summary>
    private static readonly Dictionary<string, string> Allowed = new()
    {
        ["UpdateCadenceTests.Floor_configuration_defaults_to_two_and_rejects_invalid_values"] =
            "Boot rejects the invalid MinUpdateHz before the corpus path is opened",
    };

    private sealed record Facts(bool Artwork, bool Gpu, bool RequireArtwork, bool RequireGpu, bool Skips)
    {
        public static readonly Facts None = new(false, false, false, false, false);
        public Facts Or(Facts o) => new(Artwork || o.Artwork, Gpu || o.Gpu,
            RequireArtwork || o.RequireArtwork, RequireGpu || o.RequireGpu, Skips || o.Skips);
    }

    /// <summary>Reads + parses one file and checks its top-level types. Runs on a worker
    /// thread: no shared state.</summary>
    private static (int Types, List<string> Violations) ScanFile(string file, string path)
    {
        var root = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
        var violations = new List<string>();
        int types = 0;
        foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                     .Where(t => t.Parent is not TypeDeclarationSyntax))
        {
            types++;
            Check(file, type, violations);
        }
        return (types, violations);
    }

    private static void Check(string file, TypeDeclarationSyntax type, List<string> violations)
    {
        // Every member of the type and its nested types, by simple name → direct facts +
        // referenced names. A nested type's constructors are keyed by the type's name
        // (reached through `new Nested(...)`); the outer type's own instance
        // constructors run before every test.
        var members = new Dictionary<string, (Facts Direct, HashSet<string> Refs)>(StringComparer.Ordinal);
        var measured = new Dictionary<SyntaxNode, (Facts Direct, HashSet<string> Refs)>();
        var methods = new List<MethodDeclarationSyntax>();
        var ctorFacts = Facts.None;
        var ctorRefs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in type.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            switch (member)
            {
                case FieldDeclarationSyntax field:
                    foreach (var v in field.Declaration.Variables) Add(v.Identifier.Text, v);
                    break;
                case ConstructorDeclarationSyntax ctor when ctor.Parent == type:
                    if (!ctor.Modifiers.Any(SyntaxKind.StaticKeyword))
                    {
                        var (facts, refs) = Measure(ctor);
                        ctorFacts = ctorFacts.Or(facts);
                        ctorRefs.UnionWith(refs);
                    }
                    break;
                case ConstructorDeclarationSyntax ctor when ctor.Parent is TypeDeclarationSyntax nested:
                    Add(nested.Identifier.Text, ctor);
                    break;
                case MethodDeclarationSyntax m: Add(m.Identifier.Text, m); methods.Add(m); break;
                case PropertyDeclarationSyntax prop: Add(prop.Identifier.Text, prop); break;
            }
        }

        // Transitive closure of the per-member facts over the reference graph, once per type:
        // the least fixed point of `facts[n] = direct[n] | facts[refs[n]]`. The old code ran a
        // fresh BFS per test method; this is the same reachable-fact set, computed once.
        var closure = new Dictionary<string, Facts>(members.Count, StringComparer.Ordinal);
        foreach (var (name, m) in members) closure[name] = m.Direct;
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (name, m) in members)
            {
                var acc = closure[name];
                foreach (string referenced in m.Refs)
                    if (closure.TryGetValue(referenced, out var reachable)) acc = acc.Or(reachable);
                if (acc != closure[name]) { closure[name] = acc; changed = true; }
            }
        }
        Facts Reach(IEnumerable<string> start, Facts direct)
        {
            var acc = direct;
            foreach (string name in start)
                if (closure.TryGetValue(name, out var reachable)) acc = acc.Or(reachable);
            return acc;
        }

        var ctorReach = Reach(ctorRefs, ctorFacts);
        var classTraits = (Requires: Traits(type, "Requires").ToList(), Lane: Traits(type, "Lane").ToList());
        foreach (var method in methods)
        {
            var attrs = method.AttributeLists.SelectMany(l => l.Attributes).ToList();
            bool plain = attrs.Any(a => a.Name is IdentifierNameSyntax n && FactAttributes.Contains(n.Identifier.Text));
            bool skippable = attrs.Any(a => a.Name is IdentifierNameSyntax n
                && n.Identifier.Text.StartsWith("Skippable", StringComparison.Ordinal));
            if (!plain && !skippable) continue;
            string id = $"{type.Identifier.Text}.{method.Identifier.Text}";
            if (Allowed.ContainsKey(id)) continue;
            // [Fact(Skip = "...")] never runs.
            if (attrs.Any(a => a.ArgumentList?.Arguments.Any(x => x.NameEquals?.Name.Identifier.Text == "Skip") == true)) continue;
            var (direct, refs) = Measure(method);
            var f = Reach(refs, direct).Or(ctorReach);
            string where = $"{file}: {id}";
            // Rule 1 (always): Require.*() / Skip.If*() throw SkipException, which a PLAIN
            // [Fact]/[Theory] reports as a FAILURE (TR-3..5 in run 39702545411).
            if (plain && f.Skips)
            {
                violations.Add(where + " calls Require.*()/Skip.If*() from a plain [Fact]/[Theory] — use [SkippableFact]/[SkippableTheory]");
                continue;
            }
            // Tests the gate filters out by trait ("Requires!=Artwork&Requires!=Gpu&...") never run there.
            var requires = Traits(method, "Requires").Concat(classTraits.Requires).ToHashSet();
            var lanes = Traits(method, "Lane").Concat(classTraits.Lane).ToHashSet();
            if (lanes.Contains("LiveTiming")) continue;
            if (requires.Contains("Artwork") || requires.Contains("NativeSource")) f = f with { Artwork = false };
            if (requires.Contains("Gpu")) f = f with { Gpu = false };
            // Rule 2: reads the corpus through TestArtRoot → must skip via Require.Artwork().
            if (f.Artwork && !(skippable && f.RequireArtwork))
                violations.Add(where + " reads the artwork corpus (TestArtRoot) but " +
                    (skippable ? "never calls Require.Artwork()" : "is a plain [Fact]/[Theory]"));
            // Rule 3: creates a real GL device → must skip via Require.GraphicsDevice(output).
            if (f.Gpu && !(skippable && f.RequireGpu))
                violations.Add(where + " creates a real GL device but " +
                    (skippable ? "never calls Require.GraphicsDevice(output)" : "is a plain [Fact]/[Theory]"));
        }

        // One walk per member node, shared by the member graph above and the test loop below.
        (Facts Direct, HashSet<string> Refs) Measure(SyntaxNode node)
        {
            if (measured.TryGetValue(node, out var cached)) return cached;
            var walked = Walk(node);
            measured[node] = walked;
            return walked;
        }

        void Add(string name, SyntaxNode node)
        {
            var walked = Measure(node);
            // Never mutate a cached ref set: copy when merging overloads / members sharing a name.
            members[name] = members.TryGetValue(name, out var existing)
                ? (walked.Direct.Or(existing.Direct), [.. walked.Refs, .. existing.Refs])
                : walked;
        }
    }

    /// <summary>Values of <c>[Trait("key", "value")]</c> declared directly on a member/type.</summary>
    private static IEnumerable<string> Traits(MemberDeclarationSyntax node, string key) =>
        node.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => a.Name.ToString() is "Trait" or "TraitAttribute" && a.ArgumentList?.Arguments.Count == 2
                && a.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax k && k.Token.ValueText == key)
            .Select(a => a.ArgumentList!.Arguments[1].Expression)
            .OfType<LiteralExpressionSyntax>().Select(v => v.Token.ValueText);

    /// <summary>One walk of the subtree: the direct facts (rules 1-3) *and* every simple name it
    /// mentions — the old separate <c>Direct</c> + <c>Refs</c> traversals, merged into one.</summary>
    private static (Facts Facts, HashSet<string> Refs) Walk(SyntaxNode node)
    {
        bool artwork = false, gpu = false, reqArt = false, reqGpu = false, skips = false;
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in node.DescendantNodes())
        {
            if (n is SimpleNameSyntax simple) refs.Add(simple.Identifier.Text);
            switch (n)
            {
                case IdentifierNameSyntax id when id.Identifier.Text == "TestArtRoot":
                    artwork = true; break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }:
                    string member = ma.Name.Identifier.Text;
                    // The old "<expr>.ToString() == \"Require\" || EndsWith(\".Require\")" test,
                    // compared structurally (no string allocation per call site).
                    bool require = EndsWithName(ma.Expression, "Require");
                    bool skip = EndsWithName(ma.Expression, "Skip") && member is "If" or "IfNot";
                    if (require && member is "Artwork" or "NativeSource" or "MeshyPack" or "GraphicsDevice") skips = true;
                    if (skip) skips = true;
                    if (require && member == "Artwork") reqArt = true;
                    if (require && member == "GraphicsDevice") reqGpu = true;
                    break;
                case MemberAccessExpressionSyntax ma2 when ma2.Name.Identifier.Text == "ArtworkAvailable"
                    && (IsDottedName(ma2.Expression, ["Require"]) || IsDottedName(ma2.Expression, ["Allegiance", "TestInfra", "Require"])):
                    reqArt = true; break;
                case LiteralExpressionSyntax lit when lit.Token.ValueText == "SDL_VIDEODRIVER":
                    gpu = true; break;
                case ObjectCreationExpressionSyntax oc when oc.Type is IdentifierNameSyntax created
                    && created.Identifier.Text is "GraphicsDevice" or "GraphicsDeviceManager":
                    gpu = true; break;
            }
        }
        return (new Facts(artwork, gpu, reqArt, reqGpu, skips), refs);
    }

    /// <summary>True when the expression's right-most identifier is <paramref name="name"/>
    /// (<c>name</c>, <c>X.name</c>, <c>A.B.name</c>).</summary>
    private static bool EndsWithName(ExpressionSyntax expression, string name) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text == name,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text == name,
        _ => false,
    };

    /// <summary>True when the expression is exactly the dotted name given (no
    /// <c>global::</c>/alias qualifier and no trivia inside the name).</summary>
    private static bool IsDottedName(ExpressionSyntax expression, ReadOnlySpan<string> parts)
    {
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            if (i == 0) return expression is IdentifierNameSyntax id && id.Identifier.Text == parts[0];
            if (expression is not MemberAccessExpressionSyntax ma || ma.Name.Identifier.Text != parts[i]) return false;
            expression = ma.Expression;
        }
        return false;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (AllegianceCore.slnx)");
    }
}
