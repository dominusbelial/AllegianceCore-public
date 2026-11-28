using System.Numerics;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>Sim-thread membership by (kind, object ID, recipient). IDs intentionally
/// outlive object destruction. Only the original recipient/rotation resets clear bits.</summary>
internal sealed class DiscoveryLatch
{
    private readonly Dictionary<(byte Kind, short Id), ulong[]> _objects = [];
    private readonly Dictionary<ConnectionId, int> _slots = [];
    private readonly Stack<int> _freeSlots = [];
    internal int Count { get; private set; }
    internal long RemovalVersion { get; private set; }
    internal int ObjectCapacity => _objects.Capacity;
    internal int ObjectCount => _objects.Count;

    internal bool Add((ConnectionId Recipient, byte Kind, short Id) entry)
    {
        if (!_slots.TryGetValue(entry.Recipient, out int slot))
        {
            if (!_freeSlots.TryPop(out slot)) slot = _slots.Count;
            _slots.Add(entry.Recipient, slot);
        }

        var key = (entry.Kind, entry.Id);
        int word = slot >> 6;
        if (!_objects.TryGetValue(key, out var words))
        {
            words = new ulong[word + 1];
            _objects.Add(key, words);
        }
        else if (word >= words.Length)
        {
            Array.Resize(ref words, Math.Max(words.Length * 2, word + 1));
            _objects[key] = words;
        }

        ulong bit = 1UL << (slot & 63);
        if ((words[word] & bit) != 0) return false;
        words[word] |= bit;
        Count++;
        return true;
    }

    internal void RemovePeer(ConnectionId recipient)
    {
        if (!_slots.Remove(recipient, out int slot)) return;
        int word = slot >> 6;
        ulong bit = 1UL << (slot & 63);
        foreach (var words in _objects.Values)
        {
            if (word >= words.Length || (words[word] & bit) == 0) continue;
            words[word] &= ~bit;
            Count--;
        }
        _freeSlots.Push(slot);
        RemovalVersion++;
    }

    // At migration keep the existing single directory walk for the whole cohort.
    internal void RemovePeers(HashSet<ConnectionId> recipients)
    {
        if (recipients.Count == 0 || _slots.Count == 0) return;
        int slotCount = _slots.Count + _freeSlots.Count;
        ulong[]? masks = null;
        foreach (var recipient in recipients)
        {
            if (!_slots.Remove(recipient, out int slot)) continue;
            masks ??= new ulong[(slotCount + 63) >> 6];
            masks[slot >> 6] |= 1UL << (slot & 63);
            _freeSlots.Push(slot);
        }
        if (masks == null) return;
        foreach (var words in _objects.Values)
        {
            for (int i = 0; i < Math.Min(words.Length, masks.Length); i++)
            {
                Count -= BitOperations.PopCount(words[i] & masks[i]);
                words[i] &= ~masks[i];
            }
        }
        RemovalVersion++;
    }

    internal void Clear()
    {
        _slots.Clear();
        _freeSlots.Clear();
        _objects.Clear();
        Count = 0;
        RemovalVersion++;
    }

    internal void TrimExcessIfWorthwhile()
    {
        // Retire empty rows and oversized storage only at the existing P884b
        // rotation boundary. Other missions' memberships and slots survive.
        foreach (var (key, words) in _objects)
        {
            int used = words.AsSpan().LastIndexOfAnyExcept(0UL) + 1;
            if (used == 0) _objects.Remove(key);
            else if (words.Length > 2 * used) _objects[key] = words[..used];
        }
        if (_objects.Capacity > 2 * _objects.Count + 64) _objects.TrimExcess();
        if (_slots.Count == 0)
        {
            _freeSlots.Clear();
            _slots.TrimExcess();
            _freeSlots.TrimExcess();
        }
    }
}
