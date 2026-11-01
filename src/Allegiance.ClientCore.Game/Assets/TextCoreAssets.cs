using System.IO;
using Allegiance.Core.Model;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>Game-client half of native's text core fallback
/// (<c>Igc/missionigc.cpp:2637-2672</c>). Packaged and Android clients read
/// through <see cref="IAssetSource"/>, so the importer gets a private copy of
/// the folder; the shared cache keeps owning the on-disk path used by bots.</summary>
public static class TextCoreAssets
{
    /// <summary>The core in folder <paramref name="key"/>, version 1337, or
    /// null when it has no Constants.csv. Malformed text throws
    /// <see cref="InvalidDataException"/>; the mission-entry catch reports it.</summary>
    public static StaticCore? TryLoad(IAssetSource assets, string key)
    {
        if (!assets.Exists(key + "/Constants.csv")) return null;
        var scratch = Directory.CreateTempSubdirectory("allegiance-core-");
        try
        {
            string folder = Path.Combine(scratch.FullName, key);
            Directory.CreateDirectory(folder);
            foreach (var relative in assets.Enumerate(key))
            {
                using var source = assets.OpenRead(relative);
                using var copy = File.Create(Path.Combine(folder, Path.GetFileName(relative)));
                source.CopyTo(copy);
            }
            using var lease = new StaticCoreCache().Load(key, folder);
            return lease.Core;
        }
        finally { scratch.Delete(true); }
    }
}
