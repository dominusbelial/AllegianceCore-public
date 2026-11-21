using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.Browser.Assets;

public sealed record BattlePack(string Key, string[] Sources);

/// <summary>Presentation-only closures from the resident static core. No core,
/// collision or simulation data can be supplied by this deferred manifest.</summary>
public sealed record BattleManifest(int Version, StagedAsset[] Assets, BattlePack[] Packs)
{
    public short[]? SceneGiantSectors { get; init; }

    public void Validate(StagedManifest resident)
    {
        if (SceneGiantSectors is { } sectors && (sectors.Length > 256 || sectors.Any(s => s < 0) || sectors.Distinct().Count() != sectors.Length))
            throw new InvalidDataException("Invalid scene giant sectors.");
        resident.Validate();
        if (Version != 1 || Assets is not { Length: > 0 and <= 2048 } || Packs is not { Length: > 0 and <= 1024 })
            throw new InvalidDataException("Invalid battle manifest.");
        var names = resident.Assets.Select(a => StagedManifest.Canonical(a.Source)).ToHashSet(StringComparer.Ordinal);
        var metadata = resident.Assets.DistinctBy(a => StagedManifest.Canonical(a.Source)).ToDictionary(a => StagedManifest.Canonical(a.Source), StringComparer.Ordinal);
        var baseNames = names.ToHashSet(StringComparer.Ordinal);
        var deferredNames = new HashSet<string>(StringComparer.Ordinal);
        var hashes = resident.Assets.DistinctBy(a => a.Sha256).ToDictionary(a => a.Sha256, StringComparer.Ordinal);
        foreach (var asset in Assets)
            names.Add(asset is null ? throw new InvalidDataException("Null battle asset.") : StagedManifest.Canonical(asset.Source));
        foreach (var batch in Assets.Chunk(4)) new StagedManifest(2, batch).Validate(names);
        foreach (var asset in Assets)
        {
            if (asset is null) throw new InvalidDataException("Null battle asset.");
            string name = StagedManifest.Canonical(asset.Source);
            if (!name.EndsWith(".mdl", StringComparison.Ordinal) && !name.EndsWith(".png", StringComparison.Ordinal) && name != "giant-objects.json")
                throw new InvalidDataException("Only visual battle assets may be deferred.");
            if (!deferredNames.Add(name)) throw new InvalidDataException("Duplicate battle source.");
            if (metadata.TryGetValue(name, out var old) && (old.Sha256 != asset.Sha256 || old.ByteSize != asset.ByteSize || old.ContentType != asset.ContentType ||
                !old.Dependencies.SequenceEqual(asset.Dependencies)))
                throw new InvalidDataException("Conflicting battle source.");
            if (hashes.TryGetValue(asset.Sha256, out var hash) && (hash.ByteSize != asset.ByteSize || hash.ContentType != asset.ContentType))
                throw new InvalidDataException("Conflicting battle hash.");
            if (asset.VisualDependencies is { Length: > 0 }) throw new InvalidDataException("Battle closures must be complete.");
            metadata[name] = asset; hashes[asset.Sha256] = asset; names.Add(name);
        }
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pack in Packs)
        {
            if (pack is null || string.IsNullOrEmpty(pack.Key) || pack.Key.Length > 80 || StagedManifest.Canonical(pack.Key) != pack.Key ||
                !keys.Add(pack.Key) || pack.Sources is not { Length: > 0 } || pack.Sources.Length > (pack.Key is "scene-b1" or "scene-paintings" ? 1024 : 128))
                throw new InvalidDataException("Invalid battle pack.");
            var closure = pack.Sources.Select(StagedManifest.Canonical).ToHashSet(StringComparer.Ordinal);
            var deferred = closure.Where(n => !baseNames.Contains(n)).ToArray();
            foreach (string name in closure)
            {
                if (!metadata.TryGetValue(name, out var asset) || asset.Dependencies.Any(d => !closure.Contains(StagedManifest.Canonical(d)) && !baseNames.Contains(StagedManifest.Canonical(d))))
                    throw new InvalidDataException("Incomplete battle closure.");
                used.Add(name);
            }
            if (deferred.Length > 0) new StagedManifest(2, deferred.Select(n => metadata[n]).ToArray()).Validate(names);
        }
        if (Assets.Any(a => !used.Contains(StagedManifest.Canonical(a.Source)))) throw new InvalidDataException("Unreferenced battle asset.");
    }

    public string[] RetainedHashes() => Assets.Select(a => a.Sha256).Distinct(StringComparer.Ordinal).ToArray();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BattleManifest))]
public partial class BattleJson : JsonSerializerContext;
