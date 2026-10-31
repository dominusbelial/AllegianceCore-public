using System.Buffers.Binary;
using Allegiance.Core.StaticCoreText;

namespace Allegiance.Core.Model;

/// <summary>Native named core sharing and last-consumer retirement.
/// Igc/missionigc.cpp:2390-2431,3033-3053. Data stays valid for a managed
/// caller retaining it; the cache relinquishes ownership at the last lease.</summary>
public sealed class StaticCoreCache
{
    public const int TextVersion = 1337;

    /// <summary>Native name/Constants.csv fallback (missionigc.cpp:2637-2640).
    /// Configured paths may include the optional binary .igc suffix.</summary>
    public static string TextDirectory(string binaryPath) =>
        binaryPath.EndsWith(".igc", StringComparison.OrdinalIgnoreCase) ? binaryPath[..^4] : binaryPath;

    private static string BinaryPath(string path) =>
        path.EndsWith(".igc", StringComparison.OrdinalIgnoreCase) ? path : path + ".igc";

    public static StaticCoreCache Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private sealed class Entry(StaticCore core)
    {
        internal readonly StaticCore Core = core;
        internal int Consumers;
    }

    public int ReadVersion(string name, string path)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(name, out var entry)) return entry.Core.Version;
            path = BinaryPath(path); // native getFile(name, ".igc"), missionigc.cpp:2410
            // Native version-only reads never parse records or acquire a consumer.
            if (!File.Exists(path))
                return File.Exists(Path.Combine(TextDirectory(path), "Constants.csv")) ? TextVersion : -1;
            using var stream = File.OpenRead(path);
            Span<byte> version = stackalloc byte[4];
            stream.ReadExactly(version);
            return BinaryPrimitives.ReadInt32LittleEndian(version);
        }
    }

    public Lease Load(string name, string path, bool encrypted = false)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(name, out var entry))
            {
                path = BinaryPath(path);
                // A present binary never probes or parses the text folder. Only
                // absence selects text; corrupt/unreadable binaries must fail.
                IgcFile file;
                try { file = IgcReader.ParseFile(path, encrypted); }
                catch (FileNotFoundException) { file = ImportText(path); }
                catch (DirectoryNotFoundException) { file = ImportText(path); }
                entry = new Entry(StaticCore.Load(file));
                _entries.Add(name, entry);
            }
            entry.Consumers++;
            return new Lease(this, name, entry.Core);
        }
    }

    private static IgcFile ImportText(string path)
    {
        string folder = TextDirectory(path);
        if (!File.Exists(Path.Combine(folder, "Constants.csv")))
            throw new FileNotFoundException($"Static core binary '{path}' and text '{folder}/Constants.csv' not found.", path);
        try
        {
            byte[] body = StaticCoreRoundtrip.ImportBody(folder, out _, runtime: true);
            byte[] raw = new byte[8 + body.Length];
            BinaryPrimitives.WriteInt32LittleEndian(raw, TextVersion);
            BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(4), body.Length);
            body.CopyTo(raw, 8);
            return IgcReader.Parse(raw);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or OverflowException)
        {
            throw new InvalidDataException($"Malformed static core text '{folder}': {e.Message}", e);
        }
    }

    public Lease? Retain(StaticCore core)
    {
        lock (_gate)
        {
            foreach (var pair in _entries)
                if (ReferenceEquals(pair.Value.Core, core))
                {
                    pair.Value.Consumers++;
                    return new Lease(this, pair.Key, core);
                }
            return null;
        }
    }

    private void Release(string name)
    {
        lock (_gate)
            if (--_entries[name].Consumers == 0) _entries.Remove(name);
    }

    public sealed class Lease : IDisposable
    {
        private StaticCoreCache? _cache;
        private readonly string _name;
        public StaticCore Core { get; }
        internal Lease(StaticCoreCache cache, string name, StaticCore core)
        { _cache = cache; _name = name; Core = core; }
        public void Dispose() => Interlocked.Exchange(ref _cache, null)?.Release(_name);
    }
}
