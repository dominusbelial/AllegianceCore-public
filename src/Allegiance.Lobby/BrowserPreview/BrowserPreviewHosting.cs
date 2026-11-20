using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Allegiance.ClientCore.Browser.Assets;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

namespace Allegiance.Lobby.BrowserPreview;

/// <summary>Closed file registry: no directory listing, SPA fallback, arbitrary art opens or API exemptions.
/// Production serves one published generation at <c>BrowserPreview.prefix</c> (default <c>/play/preview</c>,
/// the public host uses <c>/play</c>) from outside the ordinary wwwroot, before the API rate limiter.</summary>
public static class BrowserPreviewHosting
{
    // WASM compilation requires wasm-unsafe-eval in browsers enforcing script-src.
    // KNI uses self-hosted interop; no eval, inline script, workers, remote hosts or cross-origin isolation.
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; connect-src 'self'; img-src 'self' blob:; media-src 'self' blob:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'none'";

    /// <summary>The same policy, with the one configured public <c>wss</c> route added when a TLS proxy
    /// fronts this lobby. Only that origin is allowed to be reached; the lobby API stays <c>'self'</c>.</summary>
    public static string ContentSecurityPolicyFor(BrowserPreviewSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.PublicOrigin)) return ContentSecurityPolicy;
        var origin = new Uri(settings.PublicOrigin);
        return ContentSecurityPolicy.Replace("connect-src 'self';",
            $"connect-src 'self' wss://{origin.Authority}{settings.WebSocketPath};", StringComparison.Ordinal);
    }

    private sealed record Representation(string? File, byte[]? Bytes, long Length, string ETag);
    private sealed record Asset(string ContentType, string CacheControl, Dictionary<string, Representation> Versions);

    // .NET 10 fingerprints are ten lowercase alphanumeric characters.
    private static readonly Regex FrameworkFingerprint = new(@"\.[a-z0-9]{10}\.(?:wasm|js|dat|css)\z", RegexOptions.CultureInvariant);
    // The publication tool renames every loose application file to <stem>.<20 hex>.<ext>.
    private static readonly Regex ContentAddressed = new(@"\.[0-9a-f]{20}\.[A-Za-z0-9]+\z", RegexOptions.CultureInvariant);
    private static readonly Regex PackPath = new(@"\Apacks/[0-9a-f]{64}\z", RegexOptions.CultureInvariant);

    public static void UseBrowserPreview(this WebApplication app)
    {
        var settings = app.Services.GetRequiredService<BrowserPreviewSettings>();
        if (!settings.Enabled) return;
        settings.Validate();
        var prefix = settings.Prefix;
        var policy = ContentSecurityPolicyFor(settings);
        var root = Path.GetFullPath(settings.BundlePath, app.Environment.ContentRootPath);
        if (!File.Exists(Path.Combine(root, "index.html")))
            throw new InvalidOperationException("BrowserPreview requires a published browser bundle. Run scripts/publish-browser-flight.sh.");
        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".wasm"] = "application/wasm";
        types.Mappings[".dat"] = "application/octet-stream";
        var registry = new Dictionary<string, Asset>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            var path = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (path.StartsWith("art/", StringComparison.Ordinal) || path.EndsWith(".br", StringComparison.Ordinal) || path.EndsWith(".gz", StringComparison.Ordinal) ||
                path.Split('/').Any(p => p.StartsWith('.'))) continue;
            // Content-addressed packs have no file extension; they are the only extensionless entries allowed.
            bool pack = PackPath.IsMatch(path);
            if (!types.TryGetContentType(path, out var mime))
            {
                if (!pack) continue;
                mime = "application/octet-stream";
            }
            bool immutable = pack || FrameworkFingerprint.IsMatch(path) || ContentAddressed.IsMatch(path);
            var versions = new Dictionary<string, Representation> { ["identity"] = FromFile(file) };
            foreach (var (encoding, suffix) in new[] { ("br", ".br"), ("gzip", ".gz") })
                if (File.Exists(file + suffix) && !IsLink(file + suffix)) versions[encoding] = FromFile(file + suffix);
            registry.Add(path, new Asset(mime, CacheFor(path, immutable), versions));
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
            registry.Add(asset.Path, new Asset(asset.ContentType, CacheFor(asset.Path, immutable: true), versions));
        }
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "";
            var rawPath = context.Features.Get<IHttpRequestFeature>()?.RawTarget?.Split('?')[0] ?? path;
            if (!context.Request.Path.StartsWithSegments(prefix) && !rawPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            { await next(context); return; }
            context.Response.Headers.ContentSecurityPolicy = policy;
            if (rawPath.Contains('%') || rawPath.Contains('\\') || rawPath.Split('/').Any(p => p is "." or "..") ||
                !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            { context.Response.StatusCode = 404; return; }
            if (string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase))
            { context.Response.Redirect(prefix + "/", permanent: true); return; }
            var relative = path[(prefix.Length + 1)..];
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
            context.Response.Headers.CacheControl = asset.CacheControl;
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

    /// <summary>The intended host contract: fingerprinted/content-addressed bytes are immutable; entry
    /// pages and mutable JSON metadata are never stored; a stable-name compatibility file revalidates.</summary>
    private static string CacheFor(string path, bool immutable) => immutable
        ? "public,max-age=31536000,immutable"
        : path.EndsWith(".html", StringComparison.Ordinal) || path.EndsWith(".json", StringComparison.Ordinal) ? "no-store" : "no-cache";

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
