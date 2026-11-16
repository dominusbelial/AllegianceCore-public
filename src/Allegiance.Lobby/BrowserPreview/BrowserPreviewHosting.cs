using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.Browser.Assets;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

namespace Allegiance.Lobby.BrowserPreview;

/// <summary>Closed file registry: no directory listing, SPA fallback, arbitrary art opens or API exemptions.</summary>
public static class BrowserPreviewHosting
{
    public const string Prefix = "/play/preview";
    // WASM compilation requires wasm-unsafe-eval in browsers enforcing script-src.
    // KNI uses self-hosted interop; no eval, inline script, workers, remote hosts or cross-origin isolation.
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; connect-src 'self'; img-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'none'";

    private sealed record Representation(string? File, byte[]? Bytes, long Length, string ETag);
    private sealed record Asset(string ContentType, bool Immutable, Dictionary<string, Representation> Versions);

    public static void UseBrowserPreview(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<BrowserPreviewSettings>();
        if (!settings.Enabled) return;
        var root = Path.GetFullPath(settings.BundlePath, app.Environment.ContentRootPath);
        if (!File.Exists(Path.Combine(root, "index.html")))
            throw new InvalidOperationException("BrowserPreview requires a published browser bundle. Run scripts/publish-browser-preview.sh.");
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".dat"] = "application/octet-stream";
        var registry = new Dictionary<string, Asset>(StringComparer.Ordinal);
        // .NET 10 fingerprints are ten lowercase alphanumeric characters. Plain launch files revalidate.
        var fingerprint = new Regex(@"\.[a-z0-9]{10}\.(?:wasm|js|dat|css)\z", RegexOptions.CultureInvariant);
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var path = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (path.StartsWith("art/", StringComparison.Ordinal) || path.EndsWith(".br", StringComparison.Ordinal) || path.EndsWith(".gz", StringComparison.Ordinal) ||
                path.Split('/').Any(p => p.StartsWith('.')) || !types.TryGetContentType(path, out var mime)) continue;
            var versions = new Dictionary<string, Representation> { ["identity"] = FromFile(file) };
            foreach (var (encoding, suffix) in new[] { ("br", ".br"), ("gzip", ".gz") })
                if (File.Exists(file + suffix) && !IsLink(file + suffix)) versions[encoding] = FromFile(file + suffix);
            registry.Add(path, new Asset(mime, fingerprint.IsMatch(path), versions));
        }
        var manifestPath = Path.Combine(root, "asset-manifest.json");
        if (IsLink(manifestPath)) throw new InvalidDataException("The artwork manifest must be a regular file.");
        var manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), PreviewJson.Default.PreviewManifest)
            ?? throw new InvalidDataException("Missing preview artwork manifest.");
        manifest.Validate();
        var artworkRoot = settings.ArtworkPath;
        if (string.IsNullOrWhiteSpace(artworkRoot)) artworkRoot = app.Configuration["ALLEGIANCE_ARTWORK_PATH"];
        if (string.IsNullOrWhiteSpace(artworkRoot)) throw new InvalidOperationException("BrowserPreview requires an artwork root.");
        artworkRoot = Path.GetFullPath(artworkRoot, app.Environment.ContentRootPath);
        foreach (var asset in manifest.Assets)
        {
            // Source is a single validated filename; symlinks cannot escape the configured corpus.
            var file = Path.Combine(artworkRoot, asset.Source);
            if (IsLink(file)) throw new InvalidDataException("Preview artwork must be a regular file.");
            if (new FileInfo(file).Length != asset.ByteSize) throw new InvalidDataException("Preview artwork size differs from manifest.");
            var bytes = File.ReadAllBytes(file);
            PreviewManifest.Verify(asset, bytes);
            var versions = new Dictionary<string, Representation> { ["identity"] = FromBytes(bytes) };
            versions["br"] = FromBytes(Compress(bytes, true));
            versions["gzip"] = FromBytes(Compress(bytes, false));
            registry.Add(asset.Path, new Asset(asset.ContentType, true, versions));
        }
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var rawPath = context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] ?? path;
            if (!context.Request.Path.StartsWithSegments(Prefix) && !rawPath.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase))
            { await next(context); return; }
            context.Response.Headers.ContentSecurityPolicy = ContentSecurityPolicy;
            if (rawPath.Contains('%') || rawPath.Contains('\\') || rawPath.Split('/').Any(p => p is "." or "..") ||
                !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            { context.Response.StatusCode = 404; return; }
            if (string.Equals(path, Prefix, StringComparison.OrdinalIgnoreCase))
            { context.Response.Redirect(Prefix + "/", permanent: true); return; }
            var relative = path[(Prefix.Length + 1)..];
            if (relative.Length == 0) relative = "index.html";
            if (!registry.TryGetValue(relative, out var asset)) { context.Response.StatusCode = 404; return; }
            var encodings = context.Request.GetTypedHeaders().AcceptEncoding;
            double Quality(string name)
            {
                var exact = encodings?.FirstOrDefault(e => e.Value.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (exact is not null) return exact.Quality ?? 1;
                var wildcard = encodings?.FirstOrDefault(e => e.Value.Equals("*", StringComparison.Ordinal));
                return wildcard?.Quality ?? (name == "identity" ? 1 : 0);
            }
            var selected = new[] { "br", "gzip", "identity" }.Where(asset.Versions.ContainsKey)
                .Select(name => (Name: name, Quality: Quality(name))).Where(v => v.Quality > 0)
                .OrderByDescending(v => v.Quality).FirstOrDefault();
            context.Response.Headers.Vary = "Accept-Encoding";
            if (selected.Name is null) { context.Response.StatusCode = 406; return; }
            var representation = asset.Versions[selected.Name];
            context.Response.ContentType = asset.ContentType;
            context.Response.Headers.CacheControl = asset.Immutable ? "public,max-age=31536000,immutable" : "no-cache";
            context.Response.Headers.ETag = representation.ETag;
            if (selected.Name != "identity") context.Response.Headers.ContentEncoding = selected.Name;
            var conditions = context.Request.GetTypedHeaders().IfNoneMatch;
            if (conditions?.Any(e => e.Tag.Value == "*" || e.Tag.Value == representation.ETag) == true)
            { context.Response.StatusCode = 304; return; }
            context.Response.ContentLength = representation.Length;
            if (HttpMethods.IsHead(context.Request.Method)) return;
            if (representation.Bytes is { } buffer) await context.Response.Body.WriteAsync(buffer, context.RequestAborted);
            else await context.Response.SendFileAsync(representation.File!, context.RequestAborted);
        });
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static Representation FromFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new(path, null, stream.Length, '"' + Convert.ToHexString(SHA256.HashData(stream)) + '"');
    }
    private static Representation FromBytes(byte[] bytes) => new(null, bytes, bytes.Length, '"' + Convert.ToHexString(SHA256.HashData(bytes)) + '"');
    private static byte[] Compress(byte[] bytes, bool brotli)
    {
        using var output = new MemoryStream();
        using (Stream compressor = brotli ? new BrotliStream(output, CompressionLevel.SmallestSize, true) : new GZipStream(output, CompressionLevel.SmallestSize, true))
            compressor.Write(bytes);
        return output.ToArray();
    }
}
