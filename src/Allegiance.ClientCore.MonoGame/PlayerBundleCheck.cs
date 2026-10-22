using System.Security.Cryptography;
using Allegiance.ClientCore.MonoGame.Assets;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Allegiance.Core.Content;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Display/socket-free census using the same source and catalogues as Game1.</summary>
internal static class PlayerBundleCheck
{
    public static int Run(string[] args)
    {
        try
        {
            string Option(string name, string fallback)
            {
                int i = Array.IndexOf(args, name);
                return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback;
            }
            string manifest = Option("--manifest", Path.Combine(AppContext.BaseDirectory, "artwork-manifest.txt"));
            var paths = ManifestAssetSource.ReadPaths(manifest);
            var guard = new ManifestAssetSource(AssetSources.Artwork, paths);
            var recorder = new RecordingAssetSource(guard);
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
            var catalog = ShopCatalog.TryLoad(recorder)
                ?? throw new InvalidDataException("Packaged PCore001.igc catalogue failed to load");
            bool Hull(Allegiance.Core.DataHullTypeIGC h) { TouchName(h.ModelName.Trim()); return recorder.Exists(h.ModelName.Trim() + ".mdl"); }
            bool Station(Allegiance.Core.DataStationTypeIGC s) { TouchName(s.ModelName.Trim()); return recorder.Exists(s.ModelName.Trim() + ".mdl"); }
            var hulls = InspectorHullCatalog.Enumerate(catalog.Hulls, new HullModelResolver(), true, Hull);
            var stations = InspectorStationCatalog.Enumerate(catalog.StationTypes!.Values, Station);
            // Include string references in every core table: projectile, missile,
            // probe, mine, chaff, part/icon, civ and development names as well.
            var core = catalog.Core!;
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
            // Roots are independently derived from source literals/all map data.
            // Checking against the full checkout detects catalogue/source drift.
            string roots = Option("--roots", Path.Combine(AppContext.BaseDirectory, "artwork-roots.txt"));
            foreach (string path in ManifestAssetSource.ReadPaths(roots)) Touch(path);
            foreach (string path in paths) Touch(path);
            // P749 — exercise EVERY mode's asset closure through the packaged
            // ManifestAssetSource: the training static core (PCore014) + each
            // training mission map, and the skirmish/benchmark map. A missing
            // core/map — or any referenced asset absent from the manifest — is
            // caught here at build time, before a player hits it.
            Assets.ModeAssetClosure.TouchModeAssetClosure(recorder);
            if (guard.Violations.Count != 0)
                throw new InvalidDataException("Unlisted assets: " + string.Join(", ", guard.Violations));
            if (args.Contains("--check-assets"))
            {
                string sky = ExternalPaths.Require(ExternalResourceKind.SkyPack);
                foreach (string required in new[] { "sky-hd.json", "giant-objects.json" })
                    if (!File.Exists(Path.Combine(sky, required)))
                        throw new FileNotFoundException("Sky pack is incomplete", required);
                // Validate every packaged art/sky/Content file, including rarely
                // selected sky layers, without requiring a graphics/audio device.
                // Normalise the base exactly like each entry: on Windows GetFullPath expands 8.3
                // short names (C:\Users\RUNNER~1\... from a short %TEMP%), which BaseDirectory
                // keeps as launched, and the file system is case-insensitive.
                string bundleRoot = Path.GetFullPath(AppContext.BaseDirectory);
                var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                foreach (string line in File.ReadLines(Path.Combine(bundleRoot, "bundle-files.sha256")))
                {
                    string relative = line[66..];
                    string path = Path.GetFullPath(Path.Combine(bundleRoot, relative));
                    if (!path.StartsWith(bundleRoot, comparison))
                        throw new InvalidDataException("Invalid bundle inventory path");
                    using var stream = File.OpenRead(path);
                    string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
                    if (!hash.Equals(line[..64], StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Bundle checksum mismatch: " + relative);
                }
                Console.WriteLine($"Sky pack verified: {sky}");
            }
            string output = Option("--asset-manifest", "");
            if (output.Length > 0) File.WriteAllText(output, recorder.ToManifestJson(AssetSources.ResolveRoot()));
            Console.WriteLine($"Asset census OK: {hulls.Count} hulls, {stations.Count} stations, " +
                $"{recorder.OpenedCount} artwork files; root={AssetSources.ResolveRoot()}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Asset self-check failed: " + ex.Message);
            return 2;
        }
    }
}
