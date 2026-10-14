using System.IO;
using System.Text;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>
/// Central access to the client's asset sources and the single place the
/// desktop artwork root (<c>$ALLEGIANCE_ARTWORK_PATH</c>, resolved by
/// <see cref="ExternalPaths"/>) is obtained — the one remaining environment
/// lookup for asset loading, so heads and loaders never scatter
/// <c>GetEnvironmentVariable</c> calls.
/// </summary>
public static class AssetSources
{
    /// <summary>The canonical artwork-root resolver (see
    /// <see cref="ExternalPaths"/>). Throws a clear, actionable error when the
    /// corpus is not configured, so a fresh clone never silently falls back to
    /// an owner's machine path.</summary>
    public static string ResolveRoot() =>
        ExternalPaths.Require(ExternalResourceKind.Artwork);

    /// <summary>The default desktop source over the resolved artwork root
    /// (lazily built). Heads that need a different source (Android in P499)
    /// pass their own <see cref="IAssetSource"/> via
    /// <c>Game1.LaunchOptions.AssetSource</c>.</summary>
    public static DirectoryAssetSource Artwork { get; } =
        new(ResolveRoot());

    /// <summary>The optional sky-pack root (<c>ALLEGIANCE_SKY_PACK_PATH</c> or the
    /// <c>local.env</c> key of the same name), or null when not configured/present.
    /// The pack is a separate public repo:
    /// https://github.com/dominusbelial/AllegianceCore-sky.</summary>
    public static string? SkyPackRoot =>
        ExternalPaths.IsAvailable(ExternalResourceKind.SkyPack) ? ExternalPaths.Resolve(ExternalResourceKind.SkyPack) : null;

    /// <summary>The source the game reads through: the sky pack layered over the
    /// artwork (sky-pack files win; everything else falls through to the artwork),
    /// or just the artwork when no sky pack is configured — the sky renderers then
    /// skip their layers exactly as before.</summary>
    public static IAssetSource Game { get; } =
        SkyPackRoot is { } sky ? new LayeredAssetSource(new DirectoryAssetSource(sky), Artwork) : Artwork;

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
