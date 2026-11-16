using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Allegiance.ClientCore.Browser.Assets;

public sealed record PreviewAsset(string Path, string Source, long ByteSize, string Sha256, string ContentType);
public sealed record PreviewManifest(int Version, PreviewAsset[] Assets)
{
    public void Validate()
    {
        if (Version != 1 || Assets is not { Length: > 0 and <= 8 })
            throw new InvalidDataException("Unsupported artwork manifest.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in Assets)
        {
            if (asset is null || asset.Source is null || asset.Sha256 is null ||
                !Regex.IsMatch(asset.Source, @"\A[a-z0-9]+\.mdl\z") ||
                !Regex.IsMatch(asset.Sha256, @"\A[0-9a-f]{64}\z") ||
                asset.Path != $"art/{asset.Source[..^4]}.{asset.Sha256}.mdl" ||
                asset.ByteSize is <= 0 or > 4 * 1024 * 1024 ||
                asset.ContentType != "application/octet-stream" ||
                !paths.Add(asset.Path) || !sources.Add(asset.Source))
                throw new InvalidDataException("Invalid artwork manifest entry.");
        }
    }

    public static void Verify(PreviewAsset asset, byte[] data)
    {
        if (data.LongLength != asset.ByteSize ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(data)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Artwork verification failed: {asset.Source}");
    }
}

// Explicit metadata keeps this small contract safe under WASM trimming.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PreviewManifest))]
public partial class PreviewJson : JsonSerializerContext;

/// <summary>Stage all bytes before creating graphics resources. A retry starts a fresh attempt.</summary>
public sealed class PreviewAssetLoader(HttpClient http, Action<HttpRequestMessage>? prepareRequest = null)
{
    public string? Error { get; private set; }
    public bool IsLoading { get; private set; }
    public async Task<Dictionary<string, byte[]>?> LoadAsync(Action<int, string> progress)
    {
        if (IsLoading) return null;
        IsLoading = true;
        Error = null;
        try
        {
            progress(0, "Reading artwork manifest…");
            using var manifestRequest = new HttpRequestMessage(HttpMethod.Get, "asset-manifest.json");
            prepareRequest?.Invoke(manifestRequest);
            using var manifestResponse = await http.SendAsync(manifestRequest);
            manifestResponse.EnsureSuccessStatusCode();
            var manifest = await manifestResponse.Content.ReadFromJsonAsync(PreviewJson.Default.PreviewManifest)
                ?? throw new InvalidDataException("Missing artwork manifest.");
            manifest.Validate();
            var staged = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            long completed = 0, total = manifest.Assets.Sum(a => a.ByteSize);
            foreach (var asset in manifest.Assets)
            {
                // ResponseHeadersRead and a bounded read prevent a bad origin from allocating unbounded data.
                using var request = new HttpRequestMessage(HttpMethod.Get, asset.Path);
                prepareRequest?.Invoke(request);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType != asset.ContentType)
                    throw new InvalidDataException($"Unexpected content type: {asset.Source}");
                await using var stream = await response.Content.ReadAsStreamAsync();
                var bytes = new byte[checked((int)asset.ByteSize)];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var count = await stream.ReadAsync(bytes.AsMemory(offset));
                    if (count == 0) break;
                    offset += count;
                    progress((int)((completed + offset) * 100 / total), $"Verifying {asset.Source}…");
                }
                var extra = new byte[1];
                if (offset != bytes.Length || await stream.ReadAsync(extra) != 0)
                    throw new InvalidDataException($"Unexpected artwork size: {asset.Source}");
                PreviewManifest.Verify(asset, bytes);
                staged.Add(asset.Source, bytes);
                completed += asset.ByteSize;
            }
            progress(100, "Artwork verified. Starting renderer…");
            return staged;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or TaskCanceledException)
        {
            Error = "The preview could not load. " + ex.Message;
            return null;
        }
        finally { IsLoading = false; }
    }
}
