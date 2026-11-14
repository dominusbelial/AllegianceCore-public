// Frozen P990 performance reference, copied from commit 015019bf.
// Original Git blob: 30b507464bffa98057b93386ff875fe577c094bc.
// Only the namespace and this provenance comment differ. Do not sync this file
// with production: it is the same-run hardware reference for P1009's guard.
// It is compiled only into the test assembly; see P990_OVERHEAD_P1009.md.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;

namespace Allegiance.Core.Tests.P990Reference;

/// <summary>
/// P983 — the five named egress categories of the off-by-default broadcast
/// budget telemetry (<c>profiler.broadcastBudget</c>). The split follows the
/// real send paths, not the wire fmid alone:
/// <list type="bullet">
/// <item><see cref="HeavyShipUpdates"/> — the <c>FM_S_HEAVY_SHIPS_UPDATE(176)</c>
/// frame every seated peer receives each tick (<c>Server.RankedPose.cs</c>
/// <c>BroadcastRankedPose</c>; the legacy builder in <c>Server.Broadcast.cs</c>).
/// Matches native's per-player heavy send, FedSrv/FedSrv.CPP:4179-4189.</item>
/// <item><see cref="LightRankedPose"/> — the <c>FM_S_LIGHT_SHIPS_UPDATE(175)</c>
/// bytes appended inside that same packet (<c>FedSrv.CPP:4185-4189</c>). It
/// carries presence/controls, never a pose (<c>Igc/igc.h:2423-2432</c>).</item>
/// <item><see cref="ReliableKeyed"/> — every frame queued on a coalescing key
/// (<c>key != 0</c>): own-ship pose, teammate position batches, stations,
/// asteroids, probes, missiles, mining/cloak/track. Native's
/// <c>FM_GUARANTEED</c>/latest-wins split (FedSrv.CPP:4642).</item>
/// <item><see cref="ChatLobby"/> — chat relays and lobby announcements
/// (<c>SendSiteChat</c>, the lobby "side is no more" lines).</item>
/// <item><see cref="Other"/> — the ordered one-shot lane that is not one of the
/// above (mission def, exports, statuses, joins/quits).</item>
/// </list>
/// </summary>
internal enum BroadcastCategory : byte
{
    Other = 0,
    HeavyShipUpdates = 1,
    LightRankedPose = 2,
    ReliableKeyed = 3,
    ChatLobby = 4,
}

/// <summary>
/// P990 — one (peer, ship) pose stamp: the newest pose the peer was <em>sent</em>
/// for that subject, in simulation milliseconds, plus the subject's position at
/// that moment (when the send site knows the ship object) so the report can tell
/// a genuinely stale record from a stationary subject whose record is still exact
/// (see the <c>[bb-age-why]</c> rows).
/// </summary>
internal readonly struct PoseStamp(short shipId, double simMs, Vector3 position, bool hasPosition)
{
    public readonly short ShipId = shipId;
    public readonly double SimMs = simMs;
    public readonly Vector3 Position = position;
    public readonly bool HasPosition = hasPosition;
}

/// <summary>
/// P990 — the per-peer pose-stamp table behind the update-age diagnostic. P983
/// used a <c>Dictionary&lt;short,double&gt;</c>; at the rig's 411 peers × ~140
/// visible ships the per-tick stamp loop paid a dictionary find/store per
/// (peer, ship) inside <c>step:broadcastShipsUpdate</c> (the b104 +0.9 ms), and
/// the once-per-window report paid a full bucket-array walk per peer.
/// <para>The shape here is <b>dense entries + a small hash index</b>: the stamps
/// live in insertion-ordered, contiguous <see cref="Entry"/> records (so the
/// report's per-window walk touches exactly the occupied slots, no 3/4-empty
/// bucket array), and a power-of-two <c>int[]</c> index slot stores
/// <c>denseIndex + 1</c> (0 = empty) for one-probe lookups. The index is 4 bytes
/// per slot, so a 411-peer server's index arrays are ~0.8 MB while the stamp
/// records stay cache-dense; the per-stamp hot path is one small-array probe plus
/// one struct write, with no comparer, no boxed enumerator and no resize churn.</para>
/// <para>Keys are <c>(ushort)(shipId + 1)</c> so 0 is the empty sentinel;
/// <c>Ship.Id</c> is a <c>short</c> that never goes negative in this port (ids come
/// from the mission's monotonic id allocator). Removal compacts the dense array and
/// rebuilds the index — it happens once per world rotation, never per tick.</para>
/// </summary>
internal sealed class PoseStampTable
{
    internal struct Entry
    {
        public short ShipId;
        public bool HasPosition;
        public double SimMs;
        public Vector3 Position;
    }

    private const int MinCapacity = 64;

    private Entry[] _dense = new Entry[MinCapacity];
    private int _indexMask = MinCapacity * 2 - 1;
    private int _indexShift = 32 - 7;      // 32 - log2(MinCapacity * 2)
    private int[] _index = new int[MinCapacity * 2];
    private int _count;

    internal int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort Key(short shipId) => (ushort)(shipId + 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SlotOf(ushort key, int shift) => (int)(key * 2654435761u >> shift);

    /// <summary>Linear probe of the index: the slot holding <paramref name="key"/>,
    /// or the first empty slot (caller decides whether to insert there).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Probe(ushort key)
    {
        var index = _index;
        int mask = _indexMask;
        int slot = SlotOf(key, _indexShift) & mask;
        while (true)
        {
            int entry = index[slot];
            if (entry == 0 || _dense[entry - 1].ShipId == (short)(key - 1)) return slot;
            slot = (slot + 1) & mask;
        }
    }

    private void Rehash(int size)
    {
        while ((_count + 1) * 5 >= size * 4) size *= 2;
        _index = new int[size];
        _indexMask = size - 1;
        _indexShift = 32 - System.Numerics.BitOperations.Log2((uint)size);
        for (int i = 0; i < _count; i++)
        {
            int slot = SlotOf(Key(_dense[i].ShipId), _indexShift) & _indexMask;
            while (_index[slot] != 0) slot = (slot + 1) & _indexMask;
            _index[slot] = i + 1;
        }
    }

    internal void Set(short shipId, double simMs, Vector3 position, bool hasPosition)
    {
        ushort key = Key(shipId);
        int slot = Probe(key);
        int entry = _index[slot];
        if (entry != 0)
        {
            ref var existing = ref _dense[entry - 1];
            existing.SimMs = simMs;
            existing.Position = position;
            existing.HasPosition = hasPosition;
            return;
        }
        Insert(slot, key, simMs, position, hasPosition);
    }

    private void Insert(int slot, ushort key, double simMs, Vector3 position, bool hasPosition)
    {
        if (_count == _dense.Length) Array.Resize(ref _dense, _dense.Length * 2);
        _dense[_count] = new Entry
        {
            ShipId = (short)(key - 1), SimMs = simMs, Position = position, HasPosition = hasPosition,
        };
        _count++;
        if (_count * 5 >= _index.Length * 4)
        {
            Rehash(_index.Length);
            return;
        }
        _index[slot] = _count;
    }

    /// <summary>P990 — stamp a whole per-tick sweep in one call: the index array,
    /// mask and shift load once (instead of per ship) and the resize check runs
    /// once, which is where most of the per-stamp cost of a 411-peer projection
    /// went with the dictionary.</summary>
    internal void SetMany(List<Ship> ships, double simMs)
    {
        int count = ships.Count;
        if (count == 0) return;
        if (_count + count > _dense.Length) Array.Resize(ref _dense, Math.Max(_count + count, _dense.Length * 2));
        if ((_count + count) * 5 >= _index.Length * 4) Rehash(_index.Length);
        var index = _index;
        int mask = _indexMask, shift = _indexShift;
        for (int n = 0; n < count; n++)
        {
            var ship = ships[n];
            ushort key = Key(ship.Id);
            int slot = SlotOf(key, shift) & mask;
            int entry;
            while ((entry = index[slot]) != 0 && _dense[entry - 1].ShipId != ship.Id) slot = (slot + 1) & mask;
            if (entry != 0)
            {
                ref var existing = ref _dense[entry - 1];
                existing.SimMs = simMs;
                existing.Position = ship.Position;
                existing.HasPosition = true;
                continue;
            }
            _dense[_count] = new Entry
            {
                ShipId = ship.Id, SimMs = simMs, Position = ship.Position, HasPosition = true,
            };
            _count++;
            index[slot] = _count;
        }
    }

    internal bool TryGet(short shipId, out PoseStamp stamp)
    {
        int entry = _index[Probe(Key(shipId))];
        if (entry == 0) { stamp = default; return false; }
        ref var found = ref _dense[entry - 1];
        stamp = new PoseStamp(found.ShipId, found.SimMs, found.Position, found.HasPosition);
        return true;
    }

    internal bool Contains(short shipId) => _index[Probe(Key(shipId))] != 0;

    /// <summary>Forget one subject (swap-remove + one index rebuild). Single
    /// removals only happen on a world rotation / a test; the per-tick path never
    /// removes, so the O(n) rebuild is the cheap, obviously-correct choice.</summary>
    internal bool Remove(short shipId)
    {
        int entry = _index[Probe(Key(shipId))];
        if (entry == 0) return false;
        int last = --_count;
        if (entry - 1 != last) _dense[entry - 1] = _dense[last];
        Array.Clear(_dense, last, 1);
        Rehash(_index.Length);
        return true;
    }

    internal void Clear()
    {
        if (_count == 0) return;
        Array.Clear(_dense);
        Array.Clear(_index);
        _count = 0;
    }

    /// <summary>P884 complement rule in one pass: keep only the subjects named by
    /// <paramref name="keep"/> (compaction + one index rebuild; retire runs once per
    /// world rotation, never on a hot path).</summary>
    internal void RemoveExcept(HashSet<short> keep)
    {
        int write = 0;
        for (int read = 0; read < _count; read++)
            if (keep.Contains(_dense[read].ShipId)) _dense[write++] = _dense[read];
        if (write == _count) return;
        Array.Clear(_dense, write, _count - write);
        _count = write;
        Rehash(_index.Length);
    }

    /// <summary>Allocation-free iteration over the occupied stamps, densely (the
    /// report walks every peer × subject once per profiler window).</summary>
    public Enumerator GetEnumerator() => new(_dense, _count);

    public struct Enumerator(Entry[] entries, int count)
    {
        private readonly Entry[] _entries = entries;
        private readonly int _count = count;
        private int _index = -1;

        public PoseStamp Current
        {
            get
            {
                ref readonly var entry = ref _entries[_index];
                return new PoseStamp(entry.ShipId, entry.SimMs, entry.Position, entry.HasPosition);
            }
        }

        public bool MoveNext() => ++_index < _count;
    }
}

/// <summary>
/// P983 — one peer's rolling byte/message window plus the latest pose stamp per
/// visible ship. Written by the broadcast projection (per-sector workers) and by
/// the transport sink; the per-frame update is a plain indexed add (the slot is
/// entered once per tick), so the hot broadcast lock is never taken here.
/// </summary>
internal sealed class BroadcastPeerTelemetry
{
    internal const int Categories = BroadcastTelemetry.CategoryCount;
    internal const int Buckets = BroadcastTelemetry.BucketCount;

    private readonly long[] _slotSecond = new long[Buckets];
    internal readonly long[] Bytes = new long[Buckets * Categories];
    internal readonly long[] Messages = new long[Buckets * Categories];
    /// <summary>Newest pose (simulation milliseconds) sent for each visible ship.
    /// One stamp per ship — no per-ship history, retired with its world/peer.</summary>
    internal readonly PoseStampTable LastPoses = new();
    /// <summary>P990 — the viewer's pose pass: how many ticks it has run and the
    /// simulation time of the last one, so the age report can separate a stale
    /// <em>subject</em> from a <em>viewer</em> whose pass stopped running.</summary>
    internal long Sweeps;
    internal double LastSweepMs;
    internal double FirstSweepMs;

    internal BroadcastPeerTelemetry() => Array.Fill(_slotSecond, long.MinValue);

    /// <summary>Enter the ring slot for <paramref name="second"/>, clearing it
    /// when it is reused. Called once per tick by the server (not per send), so
    /// the send path is a plain indexed add. A second-boundary race can at worst
    /// under-count one bucket of a diagnostic — never a wire or sim input.</summary>
    internal void Rotate(long second, int slot)
    {
        if (Volatile.Read(ref _slotSecond[slot]) == second) return;
        lock (this)
        {
            if (_slotSecond[slot] != second)
            {
                Array.Clear(Bytes, slot * Categories, Categories);
                Array.Clear(Messages, slot * Categories, Categories);
                _slotSecond[slot] = second;
            }
        }
    }

    /// <summary>Add one frame's bytes/messages to the current slot. Plain adds:
    /// the snapshot publisher is the only writer within a tick and the sim
    /// thread's direct publishes are rare, so a torn value is impossible on this
    /// platform and a boundary race loses at most a frame of a diagnostic.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Add(int slot, BroadcastCategory category, long bytes, long messages)
    {
        int index = slot * Categories + (int)category;
        Bytes[index] += bytes;
        Messages[index] += messages;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkPose(short shipId, double simMs) => LastPoses.Set(shipId, simMs, default, false);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkPose(in Ship ship, double simMs) => LastPoses.Set(ship.Id, simMs, ship.Position, true);

    /// <summary>P990 — the per-tick heavy/floor sweep in one call.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkPoses(List<Ship> ships, double simMs) => LastPoses.SetMany(ships, simMs);

    internal long BytesAt(long second, BroadcastCategory category)
    {
        int slot = (int)(second % Buckets);
        if (Volatile.Read(ref _slotSecond[slot]) != second) return 0;
        return Volatile.Read(ref Bytes[slot * Categories + (int)category]);
    }

    /// <summary>Sum of the last <see cref="Buckets"/> one-second slots ending at
    /// <paramref name="nowSecond"/>. Stale slots are simply skipped, so an idle
    /// server reports zeros instead of an unbounded history.</summary>
    internal long WindowBytes(BroadcastCategory category, long nowSecond)
        => Window(Bytes, category, nowSecond);

    internal long WindowMessages(BroadcastCategory category, long nowSecond)
        => Window(Messages, category, nowSecond);

    private long Window(long[] values, BroadcastCategory category, long nowSecond)
    {
        long sum = 0;
        for (int slot = 0; slot < Buckets; slot++)
        {
            long stamp = Volatile.Read(ref _slotSecond[slot]);
            if (stamp < nowSecond - (Buckets - 1) || stamp > nowSecond) continue;
            sum += Volatile.Read(ref values[slot * Categories + (int)category]);
        }
        return sum;
    }

    internal long WindowTotal(long nowSecond)
    {
        long sum = 0;
        for (int category = 0; category < Categories; category++) sum += WindowBytes((BroadcastCategory)category, nowSecond);
        return sum;
    }
}

/// <summary>
/// P983 — the broadcast byte/age diagnostic behind <c>profiler.broadcastBudget</c>
/// (default off): the server's only per-peer, per-category egress accounting and
/// its only "how stale is each peer's view of each ship" number. Measurement
/// only — it never changes what is sent, in which order, or at what rate.
/// <para><b>Idea source (ideas only).</b> FS2 Open keeps five per-second
/// samples per named byte type, per player, purely for diagnostics and separate
/// from its enforcement budget (<c>fs2open:code/network/multi_rate.cpp:24</c>,
/// <c>:26-53</c>, <c>:126-182</c>). P978 §2.1 proposed borrowing that shape
/// here, ahead of the non-native byte-budget experiment (slice D).</para>
/// <para><b>Zero cost when off.</b> The server holds a null
/// <c>BroadcastTelemetry</c>; every send-path site is a null check on a field,
/// so the disabled path allocates nothing and touches no counter.</para>
/// <para><b>P990 — lane attribution.</b> The same flag also breaks the two
/// opaque lanes open: the ordered one-shot lane (<see cref="BroadcastCategory.Other"/>,
/// key 0) is attributed per wire <c>fmid</c> by reading the frame's own message
/// headers at the sink, and the coalescing lane
/// (<see cref="BroadcastCategory.ReliableKeyed"/>) per coalesce-key family. Both
/// are server-wide (a per-peer × 1024-fmid table would be tens of MB). The split
/// is exact against the delivered bytes; a peer that disconnects mid-window
/// removes its per-peer category counters (P983 retire rule) but its lane
/// attribution stays until the slot expires, so a window that spans a disconnect
/// can show lane totals slightly above the per-peer category totals.</para>
/// </summary>
internal sealed class BroadcastTelemetry
{
    /// <summary>Rolling window length in one-second slots.</summary>
    internal const int BucketCount = 5;
    internal const int CategoryCount = 5;
    /// <summary>P990 — ordered-lane fmid slots. The port's own ids are 1..996
    /// (<c>src/Allegiance.Core/Wire/*.cs</c>), so 1024 covers every id the server
    /// can emit; slot 0 is "unparsed frame" and the last slot is the overflow
    /// bucket for any future id ≥ 1023.</summary>
    internal const int FmidSlots = 1024;
    /// <summary>P990 — coalescing-key families: keys 0..15 are the port's own
    /// (<c>Server.Broadcast.cs</c> <c>Coalesce*</c> constants, 1..13 in use), and
    /// the last slot collects any key ≥ 15 so the split always reconciles.</summary>
    internal const int KeyedFamilies = 17;
    private const int KeyedOverflow = KeyedFamilies - 1;

    private readonly ConcurrentDictionary<ConnectionId, BroadcastPeerTelemetry> _peers = new();

    // P990 — server-wide lane attribution over the same 5 × 1 s ring.
    private readonly long[] _laneBytes = new long[BucketCount * FmidSlots];
    private readonly long[] _laneMessages = new long[BucketCount * FmidSlots];
    private readonly long[] _keyedLaneBytes = new long[BucketCount * KeyedFamilies];
    private readonly long[] _keyedLaneMessages = new long[BucketCount * KeyedFamilies];
    private readonly long[] _laneSecond = new long[BucketCount];

    /// <summary>Sends recorded since construction. Zero whenever the feature is
    /// off — the counting proof for tests. Plain increments: an approximate count
    /// is all a diagnostic needs, and an atomic add here would show up on the
    /// measured send path.</summary>
    internal long Probes;

    /// <summary>The current one-second bucket, refreshed once per tick by the
    /// server (wall clock) so the send path pays no clock read per frame.</summary>
    private long _second;
    private int _slot;

    /// <summary>Wall clock used by tests/reporting only; the send path reads the
    /// cached one-second stamp. Production refreshes it once per tick.</summary>
    internal Func<double> Clock = () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    /// <summary>Refresh the write-side bucket stamp from the wall clock.</summary>
    internal void RefreshSecond() => Advance((long)Clock());

    /// <summary>Test seam: pin the write-side bucket stamp.</summary>
    internal void SetSecond(long second) => Advance(second);

    /// <summary>Enter the one-second slot and clear every peer's copy of it.</summary>
    private void Advance(long second)
    {
        Volatile.Write(ref _second, second);
        int slot = (int)(second % BucketCount);
        Volatile.Write(ref _slot, slot);
        foreach (var peer in _peers.Values) peer.Rotate(second, slot);
        // The lane rings are server-wide: clear the whole slot row on reuse.
        if (Volatile.Read(ref _laneSecond[slot]) != second)
        {
            Array.Clear(_laneBytes, slot * FmidSlots, FmidSlots);
            Array.Clear(_laneMessages, slot * FmidSlots, FmidSlots);
            Array.Clear(_keyedLaneBytes, slot * KeyedFamilies, KeyedFamilies);
            Array.Clear(_keyedLaneMessages, slot * KeyedFamilies, KeyedFamilies);
            Volatile.Write(ref _laneSecond[slot], second);
        }
    }

    /// <summary>The server's simulation clock in milliseconds, refreshed once per
    /// tick. Pose ages are measured against this, not wall time.</summary>
    internal double SimNowMs;

    internal int PeerCount => _peers.Count;

    // The publisher sends one peer's frames in runs, so the previous peer is a
    // cache hit for most frames of a tick and skips the dictionary probe.
    [ThreadStatic] private static BroadcastTelemetry? _lastOwner;
    [ThreadStatic] private static ConnectionId _lastPeerId;
    [ThreadStatic] private static BroadcastPeerTelemetry? _lastPeerState;
    [ThreadStatic] private static int _lastGeneration;
    // Any retirement/reset invalidates every thread's cache (a removed record
    // must never keep receiving stamps or bytes).
    private int _generation;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool CacheValid(ConnectionId id)
        => ReferenceEquals(_lastOwner, this) && _lastPeerId == id
           && _lastGeneration == Volatile.Read(ref _generation);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal BroadcastPeerTelemetry Peer(ConnectionId id)
    {
        if (CacheValid(id) && _lastPeerState is { } cached) return cached;
        var peer = _peers.GetOrAdd(id, static (_, owner) =>
        {
            var created = new BroadcastPeerTelemetry();
            // A record born mid-second must belong to that second's slot, or its
            // bytes would be invisible to the window until the slot rolls.
            created.Rotate(owner._second, owner._slot);
            return created;
        }, this);
        _lastOwner = this; _lastPeerId = id; _lastPeerState = peer;
        _lastGeneration = Volatile.Read(ref _generation);
        return peer;
    }

    internal List<(ConnectionId Id, BroadcastPeerTelemetry Peer)> SnapshotPeers()
        => _peers.Select(pair => (pair.Key, pair.Value)).ToList();

    internal long WindowBytes(BroadcastCategory category, long second)
    {
        long sum = 0;
        foreach (var peer in _peers.Values) sum += peer.WindowBytes(category, second);
        return sum;
    }

    /// <summary>One peer-summed one-second bucket (the trend line), not a window.</summary>
    internal long BytesAt(BroadcastCategory category, long second)
    {
        long sum = 0;
        foreach (var peer in _peers.Values) sum += peer.BytesAt(second, category);
        return sum;
    }

    internal long WindowMessages(BroadcastCategory category, long second)
    {
        long sum = 0;
        foreach (var peer in _peers.Values) sum += peer.WindowMessages(category, second);
        return sum;
    }

    internal long WindowTotal(long second)
    {
        long sum = 0;
        foreach (var peer in _peers.Values) sum += peer.WindowTotal(second);
        return sum;
    }

    /// <summary>P990 — the ordered one-shot lane's bytes for one wire fmid over
    /// the window. Every frame handed to the sink on the ordered lane lands here
    /// (its own message headers are read at the sink), so the per-id rows sum to
    /// the lane's delivered bytes exactly.</summary>
    internal long OrderedLaneWindowBytes(int fmid, long second) => LaneWindow(_laneBytes, FmidSlots, fmid, second);

    internal long OrderedLaneWindowMessages(int fmid, long second) => LaneWindow(_laneMessages, FmidSlots, fmid, second);

    /// <summary>P990 — the keyed lane's bytes for one coalesce-key family.</summary>
    internal long KeyedLaneWindowBytes(int family, long second) => LaneWindow(_keyedLaneBytes, KeyedFamilies, family, second);

    internal long KeyedLaneWindowMessages(int family, long second) => LaneWindow(_keyedLaneMessages, KeyedFamilies, family, second);

    private long LaneWindow(long[] values, int stride, int index, long second)
    {
        long sum = 0;
        for (int slot = 0; slot < BucketCount; slot++)
        {
            long stamp = Volatile.Read(ref _laneSecond[slot]);
            if (stamp < second - (BucketCount - 1) || stamp > second) continue;
            sum += Volatile.Read(ref values[slot * stride + index]);
        }
        return sum;
    }

    /// <summary>Record one frame handed to the transport. <paramref name="key"/>
    /// maps an untagged coalescing send to <see cref="BroadcastCategory.ReliableKeyed"/>;
    /// <paramref name="splitBytes"/> is the light-pose tail of a heavy packet and
    /// is booked to <see cref="BroadcastCategory.LightRankedPose"/> so the two
    /// category sums always add up to the bytes actually sent.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordSend(ConnectionId id, int key, BroadcastCategory category, int bytes, int splitBytes)
        => RecordSendCore(id, key, category, bytes, default, splitBytes);

    /// <summary>P990 — the production sink form: the frame's own bytes are
    /// available, so the ordered lane can be attributed per wire fmid without any
    /// send site having to pass a tag.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordSendFrame(ConnectionId id, int key, BroadcastCategory category, ReadOnlySpan<byte> frame,
        int splitBytes = 0)
        => RecordSendCore(id, key, category, frame.Length, frame, splitBytes);

    private void RecordSendCore(ConnectionId id, int key, BroadcastCategory category, int bytes,
        ReadOnlySpan<byte> frame, int splitBytes)
    {
        Probes++;
        var peer = Peer(id);
        int slot = _slot;
        if (splitBytes > 0)
        {
            int heavyBytes = Math.Max(0, bytes - splitBytes);
            peer.Add(slot, BroadcastCategory.HeavyShipUpdates, heavyBytes, 1);
            peer.Add(slot, BroadcastCategory.LightRankedPose, splitBytes, 1);
            return;
        }
        // The two lanes P990 breaks open are also the two hottest: the ordered
        // one-shot lane (key 0, per-fmid attribution) and the coalescing lane
        // (keyed, per-family attribution). Branch on them before anything else.
        if (category == BroadcastCategory.Other)
        {
            if (key == 0)
            {
                peer.Add(slot, BroadcastCategory.Other, bytes, 1);
                BookOrderedFrame(slot, frame, bytes);
                return;
            }
            category = BroadcastCategory.ReliableKeyed;
        }
        peer.Add(slot, category, bytes, 1);
        if (category == BroadcastCategory.ReliableKeyed)
        {
            int index = slot * KeyedFamilies + (key >= 0 && key < KeyedOverflow ? key : KeyedOverflow);
            _keyedLaneBytes[index] += bytes;
            _keyedLaneMessages[index] += 1;
        }
    }

    /// <summary>Read the frame's own <c>cbmsg/fmid</c> headers (little-endian, the
    /// native <c>FEDMESSAGE</c> base, <c>messagecore.h:35-46</c>) and book each
    /// message to its id. The trailing CRC (and anything unparseable) is booked to
    /// the frame's first id, so a frame's bytes are always attributed exactly; a
    /// frame with no readable message lands in slot 0.</summary>
    private void BookOrderedFrame(int slot, ReadOnlySpan<byte> frame, int bytes)
    {
        int consumed = 0, first = -1, length = frame.Length;
        int baseIndex = slot * FmidSlots;
        if (length >= FedMessageHeader_Size)
        {
            // Fast path: a packet holding exactly one message (the overwhelmingly
            // common ordered frame: one status/export/warning + its CRC).
            int cb0 = frame[0] | (frame[1] << 8);
            int fmid0 = frame[2] | (frame[3] << 8);
            if (cb0 >= FedMessageHeader_Size && cb0 == length - FedPacket_CrcWidth)
            {
                int single = baseIndex + (fmid0 < FmidSlots - 1 ? fmid0 : FmidSlots - 1);
                _laneBytes[single] += bytes;
                _laneMessages[single] += 1;
                return;
            }
        }
        while (length - consumed >= FedMessageHeader_Size)
        {
            // Little-endian ushort cbmsg/fmid straight off the span (no slice
            // allocation, no BinaryPrimitives call per header field).
            int cb = frame[consumed] | (frame[consumed + 1] << 8);
            int fmid = frame[consumed + 2] | (frame[consumed + 3] << 8);
            if (cb < FedMessageHeader_Size || consumed + cb > length) break;
            int index = baseIndex + (fmid < FmidSlots - 1 ? fmid : FmidSlots - 1);
            _laneBytes[index] += cb;
            _laneMessages[index] += 1;
            if (first < 0) first = index;
            consumed += cb;
        }
        int rest = bytes - consumed;
        if (first < 0)
        {
            _laneBytes[baseIndex] += bytes;
            _laneMessages[baseIndex] += 1;
            return;
        }
        if (rest > 0) _laneBytes[first] += rest;
    }

    private const int FedMessageHeader_Size = 4;
    private const int FedPacket_CrcWidth = Allegiance.Core.Wire.FedPacket.CrcWidth;

    /// <summary>P990 — stamp the viewer's pose pass (one call per peer per tick,
    /// outside the per-ship loop) so the age report can tell a starved subject
    /// from a viewer that stopped being swept.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void MarkSweep(ConnectionId id, double simMs)
    {
        var peer = Peer(id);
        if (peer.Sweeps == 0) peer.FirstSweepMs = simMs;
        peer.Sweeps++;
        peer.LastSweepMs = simMs;
    }

    /// <summary>Drop pose stamps for every ship not named by <paramref name="keep"/>
    /// — the P884 complement rule, so a game-over rotation cannot carry a retired
    /// world's ship ids into the next match.</summary>
    internal void RetirePeer(ConnectionId id)
    {
        _peers.TryRemove(id, out _);
        Interlocked.Increment(ref _generation);
    }

    internal void RetireShips(HashSet<short> keep)
    {
        foreach (var peer in _peers.Values) peer.LastPoses.RemoveExcept(keep);
    }

    /// <summary>Test seam: forget every peer, stamp and bucket.</summary>
    internal void Reset()
    {
        _peers.Clear();
        Array.Clear(_laneBytes);
        Array.Clear(_laneMessages);
        Array.Clear(_keyedLaneBytes);
        Array.Clear(_keyedLaneMessages);
        Array.Clear(_laneSecond);
        Interlocked.Increment(ref _generation);
    }

    internal bool TelemetryTracksShip(short shipId)
    {
        foreach (var peer in _peers.Values) if (peer.LastPoses.Contains(shipId)) return true;
        return false;
    }

    internal int PoseStampCount() => _peers.Values.Sum(peer => peer.LastPoses.Count);
}
