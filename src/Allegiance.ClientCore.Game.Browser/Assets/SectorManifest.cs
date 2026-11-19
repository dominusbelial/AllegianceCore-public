using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Allegiance.ClientCore.Browser.Assets;

public sealed record SectorVisualPack(short SectorId, string Name, StagedAsset[] Assets);
public sealed record SectorManifest(int Version, string Map, int Seed, short EntrySectorId, SectorVisualPack[] Sectors)
{
    public static bool IsVisual(string source) => Regex.IsMatch(source,
        @"\A(?:bgrnd[0-9]+bmp|environment[0-9]+bmp|nebplnt[0-9]+(?:bmp)?)\.(?:mdl|png)\z");

    public void Validate(StagedManifest resident)
    {
        resident.Validate();
        if (Version != 1 || string.IsNullOrEmpty(Map) || !StagedManifest.Canonical(Map).EndsWith(".igc", StringComparison.Ordinal) || Sectors is not { Length: > 0 and <= 128 } ||
            !Sectors.Any(s => s is not null && s.SectorId == EntrySectorId))
            throw new InvalidDataException("Unsupported sector manifest.");
        var ids = new HashSet<short>();
        var metadata = new Dictionary<string, StagedAsset>(StringComparer.Ordinal);
        var baseNames = resident.Assets.Select(a => StagedManifest.Canonical(a.Source)).ToHashSet(StringComparer.Ordinal);
        if (Sectors.Any(s => s?.Assets is null || s.Assets.Any(a => a is null)))
            throw new InvalidDataException("Null sector visual.");
        var visualNames = Sectors.SelectMany(s => s.Assets).Select(a => StagedManifest.Canonical(a.Source)).ToHashSet(StringComparer.Ordinal);
        var hashes = resident.Assets.DistinctBy(a => a.Sha256).ToDictionary(a => a.Sha256, StringComparer.Ordinal);
        foreach (var asset in resident.Assets)
            foreach (string visual in asset.VisualDependencies ?? [])
                if (!visualNames.Contains(StagedManifest.Canonical(visual)) && !baseNames.Contains(StagedManifest.Canonical(visual)))
                    throw new InvalidDataException("Missing declared sector visual.");
        foreach (var sector in Sectors)
        {
            if (sector is null || sector.SectorId < 0 || !ids.Add(sector.SectorId) ||
                string.IsNullOrWhiteSpace(sector.Name) || sector.Name.Length > 64 ||
                sector.Assets is not { Length: > 0 and <= 128 })
                throw new InvalidDataException("Invalid sector pack.");
            var available = baseNames.Concat(sector.Assets.Select(a => StagedManifest.Canonical(a.Source))).ToHashSet(StringComparer.Ordinal);
            foreach (var batch in sector.Assets.Chunk(4)) new StagedManifest(2, batch).Validate(available);
            foreach (var asset in sector.Assets)
            {
                string name = StagedManifest.Canonical(asset.Source);
                if (!IsVisual(name)) throw new InvalidDataException("Gameplay files cannot be deferred.");
                if (hashes.TryGetValue(asset.Sha256, out var hash) && (hash.ByteSize != asset.ByteSize || hash.ContentType != asset.ContentType))
                    throw new InvalidDataException("Conflicting immutable visual metadata.");
                hashes.TryAdd(asset.Sha256, asset);
                if (metadata.TryGetValue(name, out var previous) && previous != asset &&
                    (previous.Sha256 != asset.Sha256 || previous.ByteSize != asset.ByteSize || previous.ContentType != asset.ContentType ||
                     !previous.Dependencies.SequenceEqual(asset.Dependencies)))
                    throw new InvalidDataException("Conflicting sector visual.");
                metadata[name] = asset;
            }
        }
    }

    public string[] RetainedHashes(StagedManifest resident) => resident.Assets.Concat(Sectors.SelectMany(s => s.Assets))
        .Select(a => a.Sha256).Distinct(StringComparer.Ordinal).ToArray();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SectorManifest))]
public partial class SectorJson : JsonSerializerContext;
