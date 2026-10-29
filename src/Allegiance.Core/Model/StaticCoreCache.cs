using System.Buffers.Binary;

namespace Allegiance.Core.Model;

/// <summary>Native named core sharing and last-consumer retirement.
/// Igc/missionigc.cpp:2390-2431,3033-3053. Data stays valid for a managed
/// caller retaining it; the cache relinquishes ownership at the last lease.</summary>
public sealed class StaticCoreCache
{
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
            // Native version-only reads never parse records or acquire a consumer.
            if (!File.Exists(path)) return -1; // missionigc.cpp:2672-2676 (NA)
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
                entry = new Entry(StaticCore.Load(IgcReader.ParseFile(path, encrypted)));
                _entries.Add(name, entry);
            }
            entry.Consumers++;
            return new Lease(this, name, entry.Core);
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
