using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

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
    internal readonly Dictionary<short, double> LastPoseMs = new();

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

    internal void MarkPose(short shipId, double simMs) => LastPoseMs[shipId] = simMs;

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
/// </summary>
internal sealed class BroadcastTelemetry
{
    /// <summary>Rolling window length in one-second slots.</summary>
    internal const int BucketCount = 5;
    internal const int CategoryCount = 5;
    private readonly ConcurrentDictionary<ConnectionId, BroadcastPeerTelemetry> _peers = new();

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

    /// <summary>Record one frame handed to the transport. <paramref name="key"/>
    /// maps an untagged coalescing send to <see cref="BroadcastCategory.ReliableKeyed"/>;
    /// <paramref name="splitBytes"/> is the light-pose tail of a heavy packet and
    /// is booked to <see cref="BroadcastCategory.LightRankedPose"/> so the two
    /// category sums always add up to the bytes actually sent.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void RecordSend(ConnectionId id, int key, BroadcastCategory category, int bytes, int splitBytes)
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
        if (category == BroadcastCategory.Other && key != 0) category = BroadcastCategory.ReliableKeyed;
        peer.Add(slot, category, bytes, 1);
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
        foreach (var peer in _peers.Values)
            foreach (var id in peer.LastPoseMs.Keys)
                if (!keep.Contains(id)) peer.LastPoseMs.Remove(id);
    }

    /// <summary>Test seam: forget every peer, stamp and bucket.</summary>
    internal void Reset()
    {
        _peers.Clear();
        Interlocked.Increment(ref _generation);
    }

    internal bool TelemetryTracksShip(short shipId)
    {
        foreach (var peer in _peers.Values) if (peer.LastPoseMs.ContainsKey(shipId)) return true;
        return false;
    }

    internal int PoseStampCount() => _peers.Values.Sum(peer => peer.LastPoseMs.Count);
}
