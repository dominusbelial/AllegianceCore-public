using System.IO;

namespace Allegiance.Core;

/// <summary>
/// P601 — resolver for the private, optional <b>Meshy asset pack</b>. The public
/// AllegianceCore repo no longer ships Meshy-generated models/textures (Meshy's
/// terms don't permit redistribution in a public MIT repo); they live in a
/// private local pack (<c>~/Development/AllegianceCore-meshy-pack</c>, its own
/// git repo, no remote). This resolver tells the client where the pack is so it
/// can optionally load Meshy art, and lets it detect that the pack is absent so
/// it renders all ships native.
///
/// <para><b>Resolution order</b> (mirrors <see cref="ExternalPaths"/>):</para>
/// <list type="number">
///   <item>the environment variable <c>ALLEGIANCE_MESHY_PACK_PATH</c>,</item>
///   <item>the gitignored <c>local.env</c> at the repo root (same key name),</item>
///   <item>otherwise <em>absent</em> — no guessing of user home paths.</item>
/// </list>
///
/// <para><b>Availability.</b> The pack is considered <em>available</em> only when
/// its compiled content directory (<c>&lt;root&gt;/Content/bin/DesktopGL/Content</c>,
/// produced by the pack's <c>build.sh</c>) actually exists — a configured-but-
/// not-built pack is treated as absent so the client never tries to load missing
/// content.</para>
/// </summary>
public static class MeshyPack
{
    public const string EnvVar = "ALLEGIANCE_MESHY_PACK_PATH";

    /// <summary>The resolved pack root path, or the empty string when it is
    /// neither configured nor present on disk.</summary>
    public static string ResolveRoot()
    {
        var ev = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(ev)) return ev.Trim();

        var local = ExternalPaths.ReadLocalConfig(EnvVar);
        if (local is not null) return local;

        return "";
    }

    /// <summary>The pack root was configured (env or local.env) to a directory
    /// that exists. Distinct from <see cref="IsAvailable"/>: a configured pack
    /// whose content is not yet built is not usable.</summary>
    public static bool Configured()
    {
        var r = ResolveRoot();
        return r.Length > 0 && Directory.Exists(r);
    }

    /// <summary>The directory the pack's MonoGame Content Pipeline writes its
    /// compiled <c>.xnb</c> to (the client roots its optional Meshy
    /// ContentManager here). Empty string when the pack root is unconfigured.</summary>
    public static string BuiltContentDir()
    {
        var r = ResolveRoot();
        return r.Length == 0 ? "" : Path.Combine(r, "Content", "bin", "DesktopGL", "Content");
    }

    /// <summary>The pack's replacement-model catalogue fragment
    /// (<c>catalogue.json</c>), merged at load into the public catalogue. Empty
    /// string when the pack root is unconfigured.</summary>
    public static string CatalogueFile()
    {
        var r = ResolveRoot();
        return r.Length == 0 ? "" : Path.Combine(r, "catalogue.json");
    }

    /// <summary>True when the pack is configured AND its compiled content
    /// directory exists — i.e. Meshy art can actually be loaded. Used by the
    /// client to gate the Meshy render path and by tests to skip gracefully.</summary>
    public static bool IsAvailable()
    {
        var c = BuiltContentDir();
        return c.Length > 0 && Directory.Exists(c);
    }

    /// <summary>Human-readable explanation of why the pack is unavailable and
    /// how to enable it. Shared by the client's log-once message and the test
    /// skip helper.</summary>
    public static string DescribeUnavailable()
    {
        var r = ResolveRoot();
        if (r.Length == 0)
            return "The private Meshy asset pack is not configured. Set the " + EnvVar +
                   " environment variable — or add '" + EnvVar + "=<path>' to the gitignored " +
                   "local.env at the repo root — to enable the optional Meshy ships. Without " +
                   "it every hull renders native. See docs/P601_MESHY_PRIVATE_PACK.md.";
        if (!Directory.Exists(r))
            return $"The Meshy asset pack path '{r}' does not exist. Fix {EnvVar}/local.env, " +
                   "or run the pack's build. Until then every hull renders native.";
        return $"The Meshy asset pack '{r}' has no compiled content " +
               "(Content/bin/DesktopGL/Content missing) — run its build.sh. Until then every " +
               "hull renders native.";
    }
}
