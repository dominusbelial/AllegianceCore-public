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
/// </summary>
public sealed class CleanCloneSkipGuardTests
{
    private static readonly string[] FactAttributes = ["Fact", "Theory", "FactAttribute", "TheoryAttribute"];

    [Fact]
    public void Artwork_and_gpu_touching_tests_skip_on_a_clean_clone()
    {
        string tests = Path.Combine(RepoRoot(), "tests");
        var violations = new List<string>();
        int scanned = 0;
        foreach (string file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(tests, file).Replace('\\', '/');
            if (rel.Contains("/bin/") || rel.Contains("/obj/") || rel.StartsWith("_shared/")) continue;
            if (rel.EndsWith(nameof(CleanCloneSkipGuardTests) + ".cs")) continue;
            var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetCompilationUnitRoot();
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                         .Where(t => t.Parent is not TypeDeclarationSyntax))
            {
                scanned++;
                violations.AddRange(Check(rel, type));
            }
        }
        Assert.True(scanned > 100, $"scanned only {scanned} test types — wrong root?");
        Assert.True(violations.Count == 0,
            "Tests that touch the artwork corpus / a GL device must skip on a clean clone " +
            "([SkippableFact] + Require.Artwork() / Require.GraphicsDevice(output)); see docs/CI_TEST_GATES.md:\n  " +
            string.Join("\n  ", violations));
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

    private static IEnumerable<string> Check(string file, TypeDeclarationSyntax type)
    {
        // Every member of the type and its nested types, by simple name → direct facts +
        // referenced names. A nested type's constructors are keyed by the type's name
        // (reached through `new Nested(...)`); the outer type's own instance
        // constructors run before every test.
        var members = new Dictionary<string, (Facts Direct, HashSet<string> Refs)>();
        var ctorFacts = Facts.None;
        var ctorRefs = new HashSet<string>();
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
                        ctorFacts = ctorFacts.Or(Direct(ctor));
                        ctorRefs.UnionWith(Refs(ctor));
                    }
                    break;
                case ConstructorDeclarationSyntax ctor when ctor.Parent is TypeDeclarationSyntax nested:
                    Add(nested.Identifier.Text, ctor);
                    break;
                case MethodDeclarationSyntax m: Add(m.Identifier.Text, m); break;
                case PropertyDeclarationSyntax prop: Add(prop.Identifier.Text, prop); break;
            }
        }

        Facts Reach(IEnumerable<string> start, Facts direct)
        {
            var seen = new HashSet<string>(); var queue = new Queue<string>(start); var acc = direct;
            while (queue.Count > 0)
            {
                string n = queue.Dequeue();
                if (!seen.Add(n) || !members.TryGetValue(n, out var m)) continue;
                acc = acc.Or(m.Direct);
                foreach (string r in m.Refs) queue.Enqueue(r);
            }
            return acc;
        }

        var ctorReach = Reach(ctorRefs, ctorFacts);
        var classTraits = (Requires: Traits(type, "Requires").ToList(), Lane: Traits(type, "Lane").ToList());
        foreach (var method in type.DescendantNodes().OfType<MethodDeclarationSyntax>())
        {
            var attrs = method.AttributeLists.SelectMany(l => l.Attributes).ToList();
            bool plain = attrs.Any(a => FactAttributes.Contains(a.Name.ToString()));
            bool skippable = attrs.Any(a => a.Name.ToString().StartsWith("Skippable", StringComparison.Ordinal));
            if (!plain && !skippable) continue;
            string id = $"{type.Identifier.Text}.{method.Identifier.Text}";
            if (Allowed.ContainsKey(id)) continue;
            // [Fact(Skip = "...")] never runs.
            if (attrs.Any(a => a.ArgumentList?.Arguments.Any(x => x.NameEquals?.Name.Identifier.Text == "Skip") == true)) continue;
            var f = Reach(Refs(method), Direct(method)).Or(ctorReach);
            string where = $"{file}: {id}";
            // Rule 1 (always): Require.*() / Skip.If*() throw SkipException, which a PLAIN
            // [Fact]/[Theory] reports as a FAILURE (TR-3..5 in run 39702545411).
            if (plain && f.Skips)
            {
                yield return where + " calls Require.*()/Skip.If*() from a plain [Fact]/[Theory] — use [SkippableFact]/[SkippableTheory]";
                continue;
            }
            // Tests the gate filters out by trait ("Requires!=Artwork&Requires!=Gpu&...") never run there.
            var requires = Traits(method, "Requires").Concat(classTraits.Requires).ToHashSet();
            if (Traits(method, "Lane").Concat(classTraits.Lane).Contains("LiveTiming")) continue;
            if (requires.Contains("Artwork") || requires.Contains("NativeSource")) f = f with { Artwork = false };
            if (requires.Contains("Gpu")) f = f with { Gpu = false };
            // Rule 2: reads the corpus through TestArtRoot → must skip via Require.Artwork().
            if (f.Artwork && !(skippable && f.RequireArtwork))
                yield return where + " reads the artwork corpus (TestArtRoot) but " +
                    (skippable ? "never calls Require.Artwork()" : "is a plain [Fact]/[Theory]");
            // Rule 3: creates a real GL device → must skip via Require.GraphicsDevice(output).
            if (f.Gpu && !(skippable && f.RequireGpu))
                yield return where + " creates a real GL device but " +
                    (skippable ? "never calls Require.GraphicsDevice(output)" : "is a plain [Fact]/[Theory]");
        }

        void Add(string name, SyntaxNode node)
        {
            var d = Direct(node); var r = Refs(node);
            if (members.TryGetValue(name, out var existing)) { d = d.Or(existing.Direct); r.UnionWith(existing.Refs); }
            members[name] = (d, r);
        }
    }

    /// <summary>Values of <c>[Trait("key", "value")]</c> declared directly on a member/type.</summary>
    private static IEnumerable<string> Traits(MemberDeclarationSyntax node, string key) =>
        node.AttributeLists.SelectMany(l => l.Attributes)
            .Where(a => a.Name.ToString() is "Trait" or "TraitAttribute" && a.ArgumentList?.Arguments.Count == 2
                && a.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax k && k.Token.ValueText == key)
            .Select(a => a.ArgumentList!.Arguments[1].Expression)
            .OfType<LiteralExpressionSyntax>().Select(v => v.Token.ValueText);

    private static HashSet<string> Refs(SyntaxNode node) =>
        node.DescendantNodes().OfType<SimpleNameSyntax>().Select(n => n.Identifier.Text).ToHashSet();

    private static Facts Direct(SyntaxNode node)
    {
        bool artwork = false, gpu = false, reqArt = false, reqGpu = false, skips = false;
        foreach (var n in node.DescendantNodes())
        {
            switch (n)
            {
                case IdentifierNameSyntax id when id.Identifier.Text == "TestArtRoot":
                    artwork = true; break;
                case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax ma }:
                    string target = ma.Expression.ToString(), member = ma.Name.Identifier.Text;
                    bool require = target == "Require" || target.EndsWith(".Require");
                    bool skip = (target == "Skip" || target.EndsWith(".Skip")) && member is "If" or "IfNot";
                    if (require && member is "Artwork" or "NativeSource" or "MeshyPack" or "GraphicsDevice") skips = true;
                    if (skip) skips = true;
                    if (require && member == "Artwork") reqArt = true;
                    if (require && member == "GraphicsDevice") reqGpu = true;
                    break;
                case MemberAccessExpressionSyntax ma2 when ma2.Name.Identifier.Text == "ArtworkAvailable"
                    && ma2.Expression.ToString() is "Require" or "Allegiance.TestInfra.Require":
                    reqArt = true; break;
                case LiteralExpressionSyntax lit when lit.Token.ValueText == "SDL_VIDEODRIVER":
                    gpu = true; break;
                case ObjectCreationExpressionSyntax oc when oc.Type.ToString() is "GraphicsDevice" or "GraphicsDeviceManager":
                    gpu = true; break;
            }
        }
        return new Facts(artwork, gpu, reqArt, reqGpu, skips);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AllegianceCore.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root (AllegianceCore.slnx)");
    }
}
