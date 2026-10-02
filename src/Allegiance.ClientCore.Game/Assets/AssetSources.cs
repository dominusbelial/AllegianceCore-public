using System.IO;
using System.Text;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>
/// Central access to the client's asset sources and the single place the
/// desktop artwork root (<c>ALLEGIANCE_ARTWORK_PATH</c> + fallback) is
/// resolved — the one remaining environment lookup for asset loading, so heads
/// and loaders never scatter <c>GetEnvironmentVariable</c> calls.
/// </summary>
public static class AssetSources
{
    /// <summary>The canonical artwork-root resolver: the
    /// <c>ALLEGIANCE_ARTWORK_PATH</c> environment variable, falling back to the
    /// standard dev location. This is today's exact desktop behaviour.</summary>
    public static string ResolveRoot() =>
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH")
        ?? "/home/domingo/Development/Artwork";

    /// <summary>The default desktop source over the resolved artwork root
    /// (lazily built). Heads that need a different source (Android in P499)
    /// pass their own <see cref="IAssetSource"/> via
    /// <c>Game1.LaunchOptions.AssetSource</c>.</summary>
    public static DirectoryAssetSource Artwork { get; } =
        new(ResolveRoot());

    /// <summary>Reads a whole file's bytes through an <see cref="IAssetSource"/>,
    /// disposing the stream. Helper for the <c>.mdl</c>/<c>.igc</c> binary
    /// loaders.</summary>
    public static byte[] ReadAllBytes(IAssetSource assets, string relativePath)
    {
        using var s = assets.OpenRead(relativePath);
        if (s is MemoryStream ms) return ms.ToArray();
        using var buf = new MemoryStream();
        s.CopyTo(buf);
        return buf.ToArray();
    }

    /// <summary>Reads a whole file's UTF-8 text through an
    /// <see cref="IAssetSource"/>, disposing the stream. Helper for the
    /// <c>.cvh</c>/<c>.mdl</c> text parsers.</summary>
    public static string ReadAllText(IAssetSource assets, string relativePath)
    {
        using var s = assets.OpenRead(relativePath);
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
