using System.Security.Cryptography;
using System.Text.Json;

namespace Allegiance.Core;

/// <summary>Optional atlas (schema 2) or legacy model (schema 1) catalogue.
/// Metadata only at startup; native hashes and maps are checked on demand.</summary>
public sealed class HdPack
{
    public static readonly string[] MapNames = ["basecolor", "normal", "metallic", "roughness", "emission"];
    private readonly string _root;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string, string), Material?> _resolved = new();
    private readonly Action<string> _log;
    private int _schema;
    private sealed record Entry(string Model, string Texture, string TextureHash, string ModelHash, bool Accepted);
    public sealed record Material(string Model, string Directory)
    {
        // Directory identifies the material in both schemas. Atlas materials are
        // shared across every model using that texture, including different kinds.
        public string Key => Directory;
        public string MapPath(string name) => Path.Combine(Directory, name + ".png");
    }

    public HdPack(string? root = null, Action<string>? log = null)
    {
        _root = root ?? ExternalPaths.Resolve(ExternalResourceKind.HdPack);
        _log = log ?? (message => Console.WriteLine("[hd] " + message));
        try
        {
            if (string.IsNullOrWhiteSpace(_root) || !File.Exists(Path.Combine(_root, "manifest.json")))
            {
                _log("optional texture pack absent; using native materials");
                return;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "manifest.json")));
            var manifest = doc.RootElement;
            _schema = manifest.GetProperty("schema").GetInt32();
            if (_schema is not (1 or 2)) throw new InvalidDataException("unsupported manifest schema");
            foreach (var model in manifest.GetProperty(_schema == 2 ? "textures" : "models").EnumerateObject())
            {
                if (!SafeStem(model.Name)) continue;
                try
                {
                    var native = model.Value.GetProperty("native");
                    string texture = _schema == 2 ? model.Name : native.GetProperty("Texture").GetString() ?? "";
                    if (!SafeStem(texture)) continue;
                    bool accepted = true;
                    // HD-1's job ledger records review rejection. Never activate a
                    // rejected/incomplete generated pilot just because its files exist.
                    if (_schema == 1 && manifest.TryGetProperty("jobs", out var jobs) &&
                        jobs.TryGetProperty(model.Name + ":multiview", out var job))
                        accepted = job.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.True &&
                            job.TryGetProperty("metrics", out var metrics) && metrics.TryGetProperty("accepted", out var review) &&
                            review.ValueKind == JsonValueKind.True;
                    _entries[model.Name] = new(model.Name, texture, native.GetProperty("TextureSha256").GetString() ?? "",
                        _schema == 2 ? "" : native.GetProperty("ModelSha256").GetString() ?? "", accepted);
                }
                catch (Exception e) when (e is KeyNotFoundException or InvalidOperationException) { }
            }
            _log($"catalogue loaded ({_entries.Count} materials, schema {_schema}); maps load on demand");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or FormatException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            _entries.Clear();
            _log($"texture pack unavailable ({e.Message}); using native materials");
        }
    }

    /// <summary>Verify the native atlas; legacy model entries also verify geometry,
    /// texture overrides and review status. A stale, rejected, incomplete or missing
    /// entry returns null. Reads use the platform's native asset source.</summary>
    public Material? Resolve(string model, string? texture, Func<string, Stream> openNative)
    {
        if (texture is null || !_entries.TryGetValue(_schema == 2 ? texture : model, out var entry)) return null;
        var key = (_schema == 2 ? "" : model.ToLowerInvariant(), texture.ToLowerInvariant());
        if (_resolved.TryGetValue(key, out var cached)) return cached;
        Material? material = null;
        try
        {
            if (!entry.Accepted) throw new InvalidDataException("review rejected or incomplete");
            if (!string.Equals(texture, entry.Texture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("native texture override differs");
            Verify(texture, entry.TextureHash);
            if (_schema == 1) Verify(model, entry.ModelHash);
            string directory = Path.Combine(_root, _schema == 2 ? "textures" : "models", entry.Model);
            if (MapNames.Any(name => !File.Exists(Path.Combine(directory, name + ".png"))))
                throw new InvalidDataException("incomplete map set");
            material = new(entry.Model, directory);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            _log($"'{model}' ignored ({e.Message}); using native material");
        }
        _resolved[key] = material;
        return material;

        void Verify(string stem, string expected)
        {
            using var stream = openNative(stem + ".mdl");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("native SHA256 mismatch: " + stem);
        }
    }

    private static bool SafeStem(string s) => s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
