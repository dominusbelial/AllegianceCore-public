using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Assets;

/// <summary>
/// P749 — the bundle asset-closure check for every single-player mode. Walks
/// the training static core + every training map and the skirmish/benchmark map
/// through a packaged <see cref="RecordingAssetSource"/> (which wraps the
/// <see cref="ManifestAssetSource"/>), so a missing core/map — or any referenced
/// asset absent from the manifest — fails the bundle check at build time
/// instead of crashing a player.
///
/// Kept here (ClientCore.Game) rather than in the MonoGame head so the pure
/// logic is unit-testable via the assembly's existing InternalsVisibleTo.
/// </summary>
public static class ModeAssetClosure
{
    /// <summary>Every mode's core + maps, in the order the player reaches them.</summary>
    private static readonly string[] RequiredFiles =
    [
        "PCore014.igc", "training_1.igc", "training_3.igc", "training_5.igc",
        "training_6.igc", "training_nan.igc", "m2xspiderw.igc",
    ];

    /// <summary>
    /// Require every mode's core and map to be present, then touch the art each
    /// references. Any missing file (or any referenced asset unlisted in the
    /// manifest) throws <see cref="InvalidDataException"/>.
    /// </summary>
    public static void TouchModeAssetClosure(RecordingAssetSource recorder)
    {
        void Touch(string path)
        {
            using var stream = recorder.OpenRead(path);
            stream.CopyTo(Stream.Null);
        }
        void TouchName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            foreach (string path in new[] { name, name + ".mdl", name + ".cvh", name + ".png", name + ".ogg" })
                if (recorder.Exists(path)) Touch(path);
        }
        // A missing backing file may surface as a FileNotFound/IO fault from the
        // underlying source rather than a clean false; treat any of those as
        // absent so the caller reports "missing from bundle", not a raw IO error.
        bool ExistsRobust(RecordingAssetSource source, string name)
        {
            try { return source.Exists(name); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException)
            { return false; }
        }

        // Require every mode's core and map to be present BEFORE the heavier
        // closure parses anything — a single missing file is caught immediately.
        foreach (string required in RequiredFiles)
            if (!ExistsRobust(recorder, required))
                throw new InvalidDataException($"Mode asset missing from bundle: {required}");

        // The training static core the TrainingHost boots (PCore014). Its full
        // closure mirrors the PCore001 all-table string sweep in PlayerBundleCheck:
        // every model, texture and sound name a core table references is touched.
        TouchCoreClosure("PCore014.igc");
        // Every mode's map: five training missions + the skirmish/benchmark map.
        foreach (string map in RequiredFiles)
            if (map.EndsWith(".igc", StringComparison.Ordinal) && map != "PCore014.igc")
                TouchMap(map);

        void TouchCoreClosure(string coreName)
        {
            var file = IgcReader.Parse(AssetSources.ReadAllBytes(recorder, coreName));
            if (file.Wrapper != IgcWrapper.VersionAndDataSize)
                throw new InvalidDataException($"'{coreName}' is not a static core (wrapper {file.Wrapper})");
            var core = Allegiance.Core.Model.StaticCore.Load(file);
            foreach (var table in core.GetType().GetProperties())
            {
                if (table.GetValue(core) is not System.Collections.IEnumerable entries) continue;
                foreach (var entry in entries)
                {
                    var value = entry.GetType().GetProperty("Value")?.GetValue(entry);
                    if (value is null) continue;
                    foreach (var property in value.GetType().GetProperties())
                        if (property.PropertyType == typeof(string) && property.GetValue(value) is string name)
                            TouchName(name.Trim());
                }
            }
        }

        void TouchMap(string mapName)
        {
            var file = IgcReader.Parse(AssetSources.ReadAllBytes(recorder, mapName));
            if (file.Wrapper != IgcWrapper.DataSize)
                throw new InvalidDataException($"'{mapName}' is not a map (wrapper {file.Wrapper})");
            var map = Allegiance.Core.Model.MissionMap.Load(file);
            // The art a MAP itself introduces beyond the core closure: each
            // sector's planet/poster background and each warp's texture/icon.
            foreach (var cluster in map.Clusters)
            {
                TouchMapName(cluster.Name);
                TouchMapName(cluster.PosterName);
                TouchMapName(cluster.PlanetName);
            }
            foreach (var warp in map.Warps)
            {
                TouchMapName(warp.TextureName);
                TouchMapName(warp.IconName);
            }
        }

        // Map art resolves with the "bmp" infix native uses for its bitmap
        // surfaces (planets nebplntNNbmp.mdl, warp textures <name>bmp.mdl) in
        // addition to the plain corpus suffixes TouchName already probes.
        void TouchMapName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            TouchName(name);
            foreach (string suffix in new[] { ".mdl", ".png", ".cvh", ".ogg" })
                if (recorder.Exists(name + "bmp" + suffix)) Touch(name + "bmp" + suffix);
        }
    }
}
