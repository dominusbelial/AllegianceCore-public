using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Allegiance.ClientCore.Browser.Assets;

/// <summary>Version 2 game packs extend the preview's hash contract with provenance and dependencies.</summary>
public sealed record StagedAsset(string Source, string Path, long ByteSize, string Sha256,
    string ContentType, string Provenance, string[] Dependencies);

public sealed record StagedManifest(int Version, StagedAsset[] Assets)
{
    public const long MaximumFileBytes = 16 * 1024 * 1024;
    public const long MaximumPackBytes = 64 * 1024 * 1024;
    public static string Canonical(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 ||
            !Regex.IsMatch(path, @"\A[a-zA-Z0-9_@\-]+(?:[./][a-zA-Z0-9_@\-]+)*\z") ||
            path.Split('/').Any(p => p is "." or ".."))
            throw new InvalidDataException("Invalid asset name.");
        return path.ToLowerInvariant();
    }

    public void Validate()
    {
        if (Version != 2 || Assets is not { Length: > 0 and <= 1024 })
            throw new InvalidDataException("Unsupported game manifest.");
        var entries = new Dictionary<string, StagedAsset>(StringComparer.Ordinal);
        var hashes = new Dictionary<string, StagedAsset>(StringComparer.Ordinal);
        long total = 0;
        foreach (var asset in Assets)
        {
            if (asset is null) throw new InvalidDataException("Null game asset.");
            var name = Canonical(asset.Source);
            if (asset.Sha256 is null || !Regex.IsMatch(asset.Sha256, @"\A[0-9a-f]{64}\z") ||
                asset.Path != $"packs/{asset.Sha256}" || asset.ByteSize is <= 0 or > MaximumFileBytes ||
                asset.ContentType is not ("application/octet-stream" or "audio/ogg" or "image/png" or "text/plain") ||
                string.IsNullOrWhiteSpace(asset.Provenance) || asset.Provenance.Length > 512 ||
                asset.Dependencies is null || asset.Dependencies.Length > 128)
                throw new InvalidDataException($"Invalid game asset: {name}");
            if (hashes.TryGetValue(asset.Sha256, out var sameHash) &&
                (sameHash.ByteSize != asset.ByteSize || sameHash.ContentType != asset.ContentType))
                throw new InvalidDataException("Inconsistent metadata for immutable hash.");
            hashes.TryAdd(asset.Sha256, asset);
            if (entries.TryGetValue(name, out var previous))
            {
                // Same-content case aliases are one logical asset. Different bytes or
                // metadata/dependency closures under the same name are ambiguous.
                if (previous.Sha256 != asset.Sha256 || previous.ByteSize != asset.ByteSize ||
                    previous.ContentType != asset.ContentType || previous.Provenance != asset.Provenance ||
                    !previous.Dependencies.Select(Canonical).SequenceEqual(asset.Dependencies.Select(Canonical)))
                    throw new InvalidDataException($"Case-colliding game assets: {name}");
            }
            else { entries.Add(name, asset); total = checked(total + asset.ByteSize); }
        }
        if (total > MaximumPackBytes) throw new InvalidDataException("Game pack exceeds the residency budget.");
        foreach (var asset in Assets)
            foreach (var dependency in asset.Dependencies)
                if (!entries.ContainsKey(Canonical(dependency)))
                    throw new InvalidDataException($"Missing dependency: {dependency}");
    }

    [JsonIgnore]
    public string Identity => Convert.ToHexStringLower(SHA256.HashData(
        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this, StagedJson.Default.StagedManifest)));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(StagedManifest))]
public partial class StagedJson : JsonSerializerContext;
