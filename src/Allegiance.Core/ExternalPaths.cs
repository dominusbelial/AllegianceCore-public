using System.IO;

namespace Allegiance.Core;

/// <summary>
/// The external path kind. Artwork = the <c>.igc</c>/<c>.cvh</c> corpus
/// (core/map/model files); NativeSource = the native C++ Allegiance checkout
/// used as a reference for a small number of parity tests.
/// </summary>
public enum ExternalResourceKind
{
    Artwork,
    NativeSource,
    /// <summary>The optional AllegianceCore sky pack (HD star field, nebula backdrops,
    /// deep-field galaxies, distant/giant sky objects) — a separate public repo,
    /// https://github.com/dominusbelial/AllegianceCore-sky. Optional: when absent the
    /// client simply skips those sky layers.</summary>
    SkyPack,
}

/// <summary>
/// Single resolver for the external paths (the artwork corpus and the native
/// source checkout) used by product code, tools and tests.
/// </summary>
/// <remarks>
/// Resolution order, per <c>docs/P597_PRIVATE_PATHS_AND_CLEAN_CLONE.md</c>:
/// <list type="number">
///   <item>the environment variable
///     (<c>ALLEGIANCE_ARTWORK_PATH</c> / <c>ALLEGIANCE_NATIVE_SRC_PATH</c>),</item>
///   <item>the gitignored <c>local.env</c> at the repo root (keys use the same
///     env-var names),</item>
///   <item>otherwise <em>not found</em> — no guessing of user home paths.
///     Callers use <see cref="Require"/> to fail with a clear, actionable
///     message, or <see cref="IsAvailable"/> to skip gracefully.</item>
/// </list>
/// </remarks>
public static class ExternalPaths
{
    public const string ArtworkEnvVar = "ALLEGIANCE_ARTWORK_PATH";
    public const string NativeSourceEnvVar = "ALLEGIANCE_NATIVE_SRC_PATH";
    public const string SkyPackEnvVar = "ALLEGIANCE_SKY_PACK_PATH";

    /// <summary>Gitignored per-machine override file at the repo root
    /// (committed template: <c>local.env.example</c>). Keys are the same as the
    /// env-var names.</summary>
    public const string LocalConfigFile = "local.env";

    /// <summary>Marker file that identifies the repo root when walking up from
    /// the runtime base directory or the current working directory.</summary>
    public const string RepoMarkerFile = "AllegianceCore.slnx";

    public static string EnvVar(ExternalResourceKind kind) => kind switch
    {
        ExternalResourceKind.Artwork => ArtworkEnvVar,
        ExternalResourceKind.SkyPack => SkyPackEnvVar,
        _ => NativeSourceEnvVar,
    };

    /// <summary>Resolves the path for <paramref name="kind"/>, or the empty
    /// string when it is neither configured nor present on disk.</summary>
    public static string Resolve(ExternalResourceKind kind)
    {
        var ev = Environment.GetEnvironmentVariable(EnvVar(kind));
        if (!string.IsNullOrWhiteSpace(ev)) return ev.Trim();

        var local = ReadLocalConfig(EnvVar(kind));
        if (local is not null) return local;

        return "";
    }

    /// <summary>True when <paramref name="kind"/> resolves to a directory that
    /// actually exists. Used by tests to skip (rather than fail) when the
    /// external corpus is absent on a clean clone.</summary>
    public static bool IsAvailable(ExternalResourceKind kind)
    {
        var p = Resolve(kind);
        return p.Length > 0 && Directory.Exists(p);
    }

    /// <summary>Returns the resolved path, throwing a clear, actionable
    /// <see cref="ExternalResourceUnavailableException"/> when the resource is
    /// missing. Product code that needs the corpus calls this so a fresh clone
    /// fails loudly instead of silently falling back to an owner's machine
    /// path.</summary>
    public static string Require(ExternalResourceKind kind)
    {
        var p = Resolve(kind);
        if (!IsAvailable(kind))
            throw new ExternalResourceUnavailableException(DescribeMissing(kind));
        return p;
    }

    /// <summary>Human-readable explanation of what is missing and how to fix
    /// it. Shared by <see cref="Require"/> (thrown) and the test skip helper
    /// (passed as the skip reason).</summary>
    public static string DescribeMissing(ExternalResourceKind kind) =>
        $"Allegiance {(kind switch { ExternalResourceKind.Artwork => "artwork corpus", ExternalResourceKind.SkyPack => "sky pack", _ => "native source" })} was not found. " +
        $"Set the {EnvVar(kind)} environment variable — or, for a local checkout, add " +
        $"'{EnvVar(kind)}=&lt;path&gt;' to the gitignored {LocalConfigFile} at the repo root " +
        $"(see {LocalConfigFile}.example) — to the directory containing it. " +
        $"The FreeAllegiance artwork corpus is cloned from https://github.com/FreeAllegiance/Artwork. " +
        $"See docs/P597_PRIVATE_PATHS_AND_CLEAN_CLONE.md.";

    /// <summary>Read a single key from the gitignored repo-root <c>local.env</c>,
    /// or null when absent. Public so sibling resolvers (e.g.
    /// <see cref="MeshyPack"/>) share the same per-machine config file.</summary>
    public static string? ReadLocalConfig(string key)
    {
        var root = FindRepoRoot();
        if (root is null) return null;
        var cfg = Path.Combine(root, LocalConfigFile);
        if (!File.Exists(cfg)) return null;
        foreach (var raw in File.ReadAllLines(cfg))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            if (line[..eq].Trim() == key)
            {
                var v = line[(eq + 1)..].Trim();
                if (v.Length > 0) return v;
            }
        }
        return null;
    }

    private static string? FindRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, RepoMarkerFile))
                    || File.Exists(Path.Combine(dir.FullName, LocalConfigFile)))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }
}

/// <summary>Thrown by <see cref="ExternalPaths.Require"/> when an external
/// resource (artwork corpus or native source) is not configured or present.</summary>
public sealed class ExternalResourceUnavailableException : Exception
{
    public ExternalResourceUnavailableException(string message) : base(message) { }
}
