using System.Text.Json;

namespace Allegiance.ClientCore.MonoGame.Platform;

/// <summary>Small, non-secret preferences only. The browser head imports and flushes snapshots asynchronously.</summary>
public sealed class ResidentProfileStorage : IProfileStorage
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private long _revision;
    public long Revision => _revision;
    private static string Key(string path)
    {
        var key = Path.GetFileName(path).ToLowerInvariant();
        var baseKey = key.EndsWith(".tmp", StringComparison.Ordinal) ? key[..^4] : key;
        if (baseKey is not ("player.json" or "hud.json" or "keymap.json" or "graphics.json"))
            throw new InvalidDataException("Only non-secret game preferences may be persisted in the browser.");
        return key;
    }
    public bool Exists(string path) => _files.ContainsKey(Key(path));
    public string ReadAllText(string path) => _files.TryGetValue(Key(path), out var text)
        ? text : throw new FileNotFoundException("Preference is not resident.");
    public void CreateDirectory(string path) { } // Logical names only; no browser filesystem.
    public void WriteAllText(string path, string text)
    {
        if (text.Length > 128 * 1024) throw new InvalidDataException("Preference is too large.");
        using var json = JsonDocument.Parse(text);
        RejectSecrets(json.RootElement);
        _files[Key(path)] = text;
        _revision++;
    }
    public void Move(string source, string destination, bool overwrite)
    {
        string from = Key(source), to = Key(destination);
        if (!overwrite && _files.ContainsKey(to)) throw new IOException("Preference already exists.");
        _files[to] = ReadAllText(from);
        _files.Remove(from);
        _revision++;
    }
    public Dictionary<string, string> Snapshot() => _files.Where(p => !p.Key.EndsWith(".tmp", StringComparison.Ordinal))
        .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    public void Import(IReadOnlyDictionary<string, string> snapshot)
    {
        // Validate the entire snapshot before publishing any imported settings.
        var candidate = new ResidentProfileStorage();
        foreach (var (key, text) in snapshot)
        {
            if (key.EndsWith(".tmp", StringComparison.Ordinal)) continue;
            candidate.WriteAllText(key, text);
        }
        _files.Clear();
        foreach (var item in candidate._files) _files.Add(item.Key, item.Value);
        _revision++;
    }
    private static void RejectSecrets(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                string key = property.Name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
                if (key.Contains("password", StringComparison.Ordinal) || key.Contains("token", StringComparison.Ordinal) ||
                    key.Contains("secret", StringComparison.Ordinal) || key is "credential" or "credentials" or "privatekey")
                    throw new InvalidDataException("Secrets cannot be persisted in browser preferences.");
                RejectSecrets(property.Value);
            }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectSecrets(item);
    }
}
