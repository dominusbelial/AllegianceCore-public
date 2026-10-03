using System.IO;
using Allegiance.ClientCore.MonoGame.Assets;

// The root namespace Allegiance.ClientCore.Android would shadow the Android
// namespaces below, so the platform types are referenced with an explicit
// global:: qualifier throughout this file.
using AssetManager = global::Android.Content.Res.AssetManager;
using FileNotFound = global::Java.IO.FileNotFoundException;

namespace Allegiance.ClientCore.Android;

/// <summary>
/// P499 — the <see cref="IAssetSource"/> implementation over the APK's packed
/// Android assets (the artwork files laid into <c>Assets/artwork</c> by
/// <c>scripts/pack-android-assets.sh</c> from the P498 manifest). The shared
/// library's loaders read relative POSIX paths (e.g. <c>bgrnd05bmp.mdl</c>,
/// <c>PCore001.igc</c>) through this, so the SAME game code that reads the
/// DesktopGL on-disk tree reads the APK's assets on a phone — no loader
/// changes. This is the Android analogue of <see cref="DirectoryAssetSource"/>.
/// </summary>
public sealed class AndroidAssetSource : IAssetSource
{
    private readonly AssetManager _assets;
    private readonly string _prefix;

    /// <summary>
    /// Wrap the app's asset manager, mapping the shared library's relative
    /// paths into the <paramref name="prefix"/> sub-tree of the APK assets
    /// (default <c>artwork</c>). POSIX '/'-separated relative paths pass
    /// through unchanged beneath the prefix, matching the manifest layout.
    /// </summary>
    public AndroidAssetSource(AssetManager assets, string prefix = "artwork")
    {
        _assets = assets;
        _prefix = string.IsNullOrEmpty(prefix) ? "" : prefix.TrimEnd('/');
    }

    private string Map(string rel) => _prefix.Length == 0 ? rel : _prefix + "/" + rel;

    public bool Exists(string relativePath)
    {
        try
        {
            using var s = _assets.Open(Map(relativePath));
            return true;
        }
        catch (Java.IO.FileNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    public Stream OpenRead(string relativePath)
    {
        // AssetManager.Open throws Java.IO.FileNotFoundException when missing;
        // callers treat that as a graceful missing-asset, as on desktop.
        return _assets.Open(Map(relativePath));
    }

    public IEnumerable<string> Enumerate(string directoryRelativePath)
    {
        string dir = Map(directoryRelativePath);
        string[] names = _assets.List(dir) ?? Array.Empty<string>();
        // AssetManager.List returns files AND directories indistinguishably;
        // a sub-directory fails to Open, so probe to keep only files. Collect
        // first (C# forbids yield inside try/catch), then return the list.
        var files = new List<string>();
        foreach (var n in names)
        {
            string full = dir + "/" + n;
            try
            {
                using var s = _assets.Open(full);
                files.Add(n);
            }
            catch
            {
                // directory or non-openable — skip
            }
        }
        return files;
    }
}
