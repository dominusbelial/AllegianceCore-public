using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>
/// An ordered stack of asset sources: lookups go to the first layer that has the
/// file. Used to put the optional AllegianceCore sky pack over the FreeAllegiance
/// artwork without copying files into the artwork checkout.
/// </summary>
public sealed class LayeredAssetSource : IAssetSource
{
    private readonly IAssetSource[] _layers;

    public LayeredAssetSource(params IAssetSource[] layers) => _layers = layers;

    public IReadOnlyList<IAssetSource> Layers => _layers;

    public bool Exists(string relativePath) => _layers.Any(l => l.Exists(relativePath));

    public Stream OpenRead(string relativePath)
    {
        foreach (var l in _layers)
            if (l.Exists(relativePath)) return l.OpenRead(relativePath);
        throw new FileNotFoundException($"asset not found in any layer: {relativePath}", relativePath);
    }

    /// <summary>The union of every layer's files in the directory; when two layers
    /// have the same relative path, the earlier layer's entry wins (listed once).</summary>
    public IEnumerable<string> Enumerate(string directoryRelativePath)
    {
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        foreach (var l in _layers)
            foreach (var f in l.Enumerate(directoryRelativePath))
                if (seen.Add(f)) yield return f;
    }
}
