using System.IO;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>
/// Abstract source of the client's external asset files — the original game's
/// Artwork tree (<c>.mdl</c> textures/models, <c>.cvh</c> collision hulls,
/// <c>PCore001.igc</c>). Relative paths are always POSIX-style
/// ('/' separated), rooted at the logical asset root.
///
/// <para>Every client file-IO site (see
/// <c>docs/reports/P498_CLIENT_SPLIT_ASSET_SOURCE.md</c>) reads through this
/// interface instead of touching <c>System.IO.File</c> or the
/// <c>ALLEGIANCE_ARTWORK_PATH</c> environment variable directly. The DesktopGL
/// head uses <see cref="DirectoryAssetSource"/> (today's exact on-disk
/// behaviour). Future platform heads — Android (P499) — supply their own
/// implementation over APK assets or a downloaded pack.</para>
/// </summary>
public interface IAssetSource
{
    /// <summary>Whether a file exists at the given relative path.</summary>
    bool Exists(string relativePath);

    /// <summary>Opens a readable stream over the file at the given relative
    /// path. Throws when it does not exist.</summary>
    Stream OpenRead(string relativePath);

    /// <summary>Enumerates the relative paths of every file directly inside the
    /// given directory (relative path), '/' separated. Empty when the directory
    /// does not exist.</summary>
    IEnumerable<string> Enumerate(string directoryRelativePath);
}
