using System.Text;
namespace Allegiance.ClientCore.MonoGame.Assets;
public static class AssetSources
{
    public static IAssetSource Artwork { get; set; } = null!;
    public static IAssetSource Game => Artwork;
    public static string? SkyPackRoot => null;
    public static string ResolveRoot() => throw new PlatformNotSupportedException("Physical artwork paths are not available in the browser.");
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
