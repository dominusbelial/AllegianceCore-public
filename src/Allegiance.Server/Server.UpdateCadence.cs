using System.Diagnostics;
using System.Globalization;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    internal double _minUpdateHz = 2;
    private readonly Dictionary<ConnectionId, ViewerPoseCadence> _viewerPoseCadence = new();
    private readonly PoseAgeWindow _updateAgeAggregate = new();
    private readonly PoseAgeWindow _retiredPoseAges = new();
    private int _updateAgeReportRequested;
    private double _updateAgeWindowStart;
    // Deterministic test clock; production always uses monotonic wall time.
    internal Func<double>? TestUpdateCadenceClock;
    private double UpdateCadenceNow => TestUpdateCadenceClock?.Invoke()
        ?? Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private void InitUpdateCadence()
    {
        double hz = _config.Broadcast?.MinUpdateHz ?? 2;
        if (!double.IsFinite(hz) || hz < 0)
            throw new InvalidDataException("broadcast.minUpdateHz must be finite and non-negative (0 disables the floor)");
        _minUpdateHz = Math.Min(hz, Math.Max(1, _config.UpdatesPerSecond));
        InitInterestTiers();
        _viewerPoseCadence.Clear();
        _retiredPoseAges.Clear();
        _updateAgeWindowStart = UpdateCadenceNow;
        TickProfiler.WindowReported -= RequestUpdateAgeWindow;
        TickProfiler.WindowReported += RequestUpdateAgeWindow;
        Console.WriteLine($"[boot] broadcast.minUpdateHz = {_minUpdateHz:R} (requested {hz:R}); 0 = ranked budget only");
    }

    // The profiler is process-global. Its callback may run on another test's
    // thread: only request a flush here; read viewer state on our sim thread.
    private void RequestUpdateAgeWindow() => Interlocked.Exchange(ref _updateAgeReportRequested, 1);

    private void ShutdownUpdateCadence()
    {
        TickProfiler.WindowReported -= RequestUpdateAgeWindow;
        _viewerPoseCadence.Clear();
        _retiredPoseAges.Clear();
        _retiredTierAges.Clear();
    }

    internal void RetireViewerUpdateCadence(ConnectionId id)
    {
        if (!_viewerPoseCadence.Remove(id, out var state)) return;
        // Preserve this viewer's contribution to the current window while
        // releasing its identity/ship caches immediately on disconnect.
        _retiredPoseAges.Merge(state.Window);
        _retiredTierAges.Merge(state.TierWindow);
        _retiredPoseAges.ExtraBytes += state.ExtraBytes;
        _retiredPoseAges.PacketBytes += state.PacketBytes;
    }

    internal string TakeUpdateAgeReport(double now)
    {
        var tiers = new PoseAgeWindow();
        tiers.Merge(_retiredTierAges);
        _retiredTierAges.Clear();
        var total = _updateAgeAggregate;
        total.Clear();
        total.Merge(_retiredPoseAges);
        total.ExtraBytes = _retiredPoseAges.ExtraBytes;
        total.PacketBytes = _retiredPoseAges.PacketBytes;
        _retiredPoseAges.Clear();
        long older1 = 0, older3 = 0, pairs = 0;
        foreach (var state in _viewerPoseCadence.Values)
        {
            older1 += state.Older1;
            older3 += state.Older3;
            pairs += state.TrackedCount;
            tiers.Merge(state.TierWindow);
            state.TierWindow.Clear();
            total.Merge(state.Window);
            state.Window.Clear();
            total.ExtraBytes += state.ExtraBytes;
            total.PacketBytes += state.PacketBytes;
            state.ExtraBytes = state.PacketBytes = 0;
        }
        double seconds = Math.Max(0.000001, now - _updateAgeWindowStart);
        _updateAgeWindowStart = now;
        return string.Create(CultureInfo.InvariantCulture, $"[update-age] window={seconds:0.###}s viewers={_viewerPoseCadence.Count} minHz={_minUpdateHz:0.###} {total.FormatBands()} visiblePairs={pairs} older1s={older1} older3s={older3} older1sSamples={total.Older1} older3sSamples={total.Older3} neverPose={total.NeverPose} shipsBytes={total.PacketBytes} shipsBps={total.PacketBytes / seconds:0.0} floorExtraBytes={total.ExtraBytes} floorExtraBps={total.ExtraBytes / seconds:0.0}{(_broadcastInterestTiersEnabled ? FormatTierAges(tiers) : "")}");
    }

    private void EmitUpdateAgeWindow() => Console.WriteLine(TakeUpdateAgeReport(UpdateCadenceNow));
}

/// <summary>One viewer's state; owned by its sector worker. Capacity grows only
/// on population growth. No allocations in the warmed tracking/sampling path.
/// The native ranked HEAVY/LIGHT path is independent of this state.</summary>
internal sealed class ViewerPoseCadence
{
    private struct Stamp
    {
        public Ship Ship;
        public double LastPose;
        public bool HasPose;
        public int Generation;
        public int Tier;
        public Server.InterestMembership Interest;
    }

    private readonly Dictionary<short, Stamp> _stamps = new();
    private readonly List<Ship> _visible = new();
    private Sector? _sector;
    private Ship? _viewer;
    private short? _side;
    private int _generation, _cursor;
    internal readonly PoseAgeWindow Window = new();
    internal readonly PoseAgeWindow TierWindow = new();
    internal long ExtraBytes, PacketBytes;
    internal int Older1, Older3;
    internal int TrackedCount => _stamps.Count;

    internal void Begin(IReadOnlyList<Ship> candidates, Ship self, Sector sector,
        bool adminSpectator, double now, bool sample, Func<Ship, Ship, int>? classify = null, bool cloakOnly = false, Server? tierServer = null)
    {
        if (_sector != sector || _viewer != self || _side != self.SideId)
        {
            _stamps.Clear();
            _cursor = 0;
            _sector = sector;
            _viewer = self;
            _side = self.SideId;
        }
        _visible.Clear();
        Older1 = Older3 = 0;
        _generation++;
        int exact = 0, cells = 0, cached = 0;
        var viewerInterest = tierServer is null ? default : tierServer.InterestStateFor(self);
        for (int i = 0; i < candidates.Count; i++)
        {
            var ship = candidates[i];
            if (ship == self || (!adminSpectator && self.SideId is { } side
                && (cloakOnly ? ship.IsCloaked : ship.SideId != side) && !ship.SeenBySide(side))) continue;
            _visible.Add(ship);
            if (!_stamps.TryGetValue(ship.Id, out var stamp) || stamp.Ship != ship)
                stamp = new Stamp { Ship = ship, LastPose = now };
            stamp.Generation = _generation;
            if (tierServer is not null)
            {
                stamp.Tier = tierServer.ClassifyInterestCached(viewerInterest, ship, ref stamp.Interest);
                if (stamp.Interest.LastEvaluation == 0) exact++;
                else if (stamp.Interest.LastEvaluation == 1) cells++;
                else if (stamp.Interest.LastEvaluation == 2) cached++;
            }
            else if (classify is not null) stamp.Tier = classify(self, ship);
            _stamps[ship.Id] = stamp;
            // Sample BEFORE this tick's sends, so a due update cannot hide its age.
            double age = Math.Max(0, now - stamp.LastPose);
            if (age > 1) Older1++;
            if (age > 3) Older3++;
            if (classify is not null) TierWindow.RecordBand(stamp.Tier, age, !stamp.HasPose);
            if (sample) Window.Record((ship.Position - self.Position).LengthSquared(), age, !stamp.HasPose);
        }
        if (tierServer is not null)
        {
            Interlocked.Add(ref tierServer.TierExactClassifications, exact);
            Interlocked.Add(ref tierServer.TierCellClassifications, cells);
            Interlocked.Add(ref tierServer.TierCachedClassifications, cached);
        }
        // .NET Dictionary supports Remove during enumeration. Forget departed,
        // hidden, boarded and destroyed subjects; reused ids begin a new clock.
        foreach (var pair in _stamps)
            if (pair.Value.Generation != _generation) _stamps.Remove(pair.Key);
    }

    internal struct TierCandidate
    {
        internal Ship Ship;
        internal int Tier, Index;
        internal float Deviation;
    }
    private readonly struct TierOrder : IComparer<TierCandidate>
    {
        public int Compare(TierCandidate a, TierCandidate b)
        {
            int order = a.Tier.CompareTo(b.Tier);
            if (order == 0) order = b.Deviation.CompareTo(a.Deviation);
            return order == 0 ? a.Index.CompareTo(b.Index) : order;
        }
    }
    private TierCandidate[] _tierCandidates = [];
    internal ReadOnlySpan<TierCandidate> SelectTierCandidates(Ship self, Ship? target, double now,
        double tier1Hz, double tier2Hz, int tickHz, uint tick)
    {
        if (_tierCandidates.Length < _visible.Count) Array.Resize(ref _tierCandidates, _visible.Count);
        int count = 0;
        for (int i = 0; i < _visible.Count; i++)
        {
            var ship = _visible[i];
            if (ship == target) continue;
            int tier = _stamps[ship.Id].Tier;
            if (tier == 0 || TierDue(ship.Id, now, tier == 1 ? tier1Hz : tier2Hz, tickHz, tick, self.Id))
                _tierCandidates[count++] = new() { Ship = ship, Tier = tier, Index = i, Deviation = ship.Deviation };
            else ship.Deviation += 1;
        }
        // Skipped records do not affect served order. Sorting only due records
        // preserves the stable tier/deviation ordering with much less work.
        SortTierCandidates(count);
        return _tierCandidates.AsSpan(0, count);
    }

    private void SortTierCandidates(int count)
    {
        // The framework's comparer overload creates a Comparison delegate per
        // sort. This heap sort uses a value comparer directly and a stable
        // candidate-index tie break, with no delegates or scratch allocation.
        for (int root = count / 2 - 1; root >= 0; root--) SiftTier(root, count);
        for (int end = count - 1; end > 0; end--)
        {
            (_tierCandidates[0], _tierCandidates[end]) = (_tierCandidates[end], _tierCandidates[0]);
            SiftTier(0, end);
        }
    }
    private void SiftTier(int root, int count)
    {
        var order = new TierOrder();
        while (root * 2 + 1 < count)
        {
            int child = root * 2 + 1;
            if (child + 1 < count && order.Compare(_tierCandidates[child], _tierCandidates[child + 1]) < 0) child++;
            if (order.Compare(_tierCandidates[root], _tierCandidates[child]) >= 0) return;
            (_tierCandidates[root], _tierCandidates[child]) = (_tierCandidates[child], _tierCandidates[root]);
            root = child;
        }
    }

    internal bool TierDue(short id, double now, double hz, int tickHz, uint tick, short viewerId)
    {
        if (!_stamps.TryGetValue(id, out var stamp) || !stamp.HasPose) return true;
        int period = Math.Max(1, (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(1, tickHz) / hz)));
        // Spread recurring sends across ticks after the initial sighting.
        // A wall-time deadline still catches up promptly after a stalled tick.
        uint phase = unchecked((uint)(id * 397 ^ viewerId));
        return tick % (uint)period == phase % (uint)period
            || now - stamp.LastPose + 1e-9 >= period / (double)Math.Max(1, tickHz);
    }

    internal void MarkPose(short id, double now)
    {
        if (!_stamps.TryGetValue(id, out var stamp)) return;
        stamp.LastPose = now;
        stamp.HasPose = true;
        _stamps[id] = stamp;
    }

    internal void AppendFloor(List<HeavyShipUpdateEntry> entries, uint tick,
        Vector3 posRef, double now, double hz, int tickHz, bool snapshot = false,
        Func<uint, Vector3, Ship, uint, HeavyShipUpdateEntry>? capture = null)
    {
        if (hz <= 0 || _visible.Count == 0) return;
        // A whole sweep within floor(tickHz/hz) ticks. Never let ranked serves
        // move this cursor: otherwise the shared deviation ranking can starve
        // the same far ships for a particular viewer indefinitely.
        int periodTicks = Math.Max(1, (int)Math.Min(int.MaxValue, Math.Floor(tickHz / hz)));
        int quota = (int)Math.Ceiling(_visible.Count / (double)periodTicks);
        _cursor %= _visible.Count;
        for (int n = 0; n < _visible.Count; n++)
        {
            var ship = _visible[(_cursor + n) % _visible.Count];
            var stamp = _stamps[ship.Id];
            // Deadline catch-up handles population churn and irregular tick
            // spacing. No server can promise a wall-time deadline during a stall;
            // catch up at the next broadcast rather than carrying that backlog.
            bool due = now - stamp.LastPose >= 1 / hz;
            if ((n < quota || due) && (!stamp.HasPose || stamp.LastPose != now))
            {
                entries.Add(capture is not null ? capture(tick, posRef, ship, tick)
                    : snapshot ? HeavyShipUpdateEntry.Capture(tick, posRef, ship, tick)
                    : HeavyShipUpdateEntry.From(tick, posRef, ship, tick));
                MarkPose(ship.Id, now);
                ExtraBytes += HeavyShipUpdateEntry.Width;
            }
        }
        _cursor = (_cursor + quota) % _visible.Count;
    }
}

/// <summary>Fixed 50 ms histogram bins through 10 s, plus overflow. Percentiles
/// are upper bin bounds (overflow uses the exact max); max is always exact.
/// Counts are viewer/ship/tick observations, not distinct ship identities.</summary>
internal sealed class PoseAgeWindow
{
    private const int Bins = 202;
    private readonly long[] _hist = new long[4 * Bins];
    private readonly long[] _counts = new long[4];
    private readonly double[] _max = new double[4];
    internal long Older1, Older3, NeverPose, ExtraBytes, PacketBytes;

    internal void Record(float distanceSquared, double age, bool neverPose = false)
    {
        int band = distanceSquared < 1_000_000 ? 0 : distanceSquared < 9_000_000 ? 1
            : distanceSquared < 36_000_000 ? 2 : 3;
        RecordBand(band, age, neverPose);
    }

    internal void RecordBand(int band, double age, bool neverPose = false)
    {
        int bin = (int)Math.Min(Bins - 1, Math.Ceiling(age * 20));
        _hist[band * Bins + bin]++;
        _counts[band]++;
        _max[band] = Math.Max(_max[band], age);
        if (age > 1) Older1++;
        if (age > 3) Older3++;
        if (neverPose) NeverPose++;
    }

    internal (long Count, double P50, double P95, double Max) Band(int band)
        => (_counts[band], Percentile(band, 0.5), Percentile(band, 0.95), _max[band]);

    private double Percentile(int band, double fraction)
    {
        long rank = (long)Math.Ceiling(_counts[band] * fraction), count = 0;
        if (rank == 0) return 0;
        for (int bin = 0; bin < Bins; bin++)
        {
            count += _hist[band * Bins + bin];
            if (count >= rank) return bin == Bins - 1 ? _max[band] : Math.Min(_max[band], bin / 20d);
        }
        return _max[band];
    }

    internal void Merge(PoseAgeWindow other)
    {
        for (int i = 0; i < _hist.Length; i++) _hist[i] += other._hist[i];
        for (int i = 0; i < 4; i++)
        {
            _counts[i] += other._counts[i];
            _max[i] = Math.Max(_max[i], other._max[i]);
        }
        Older1 += other.Older1; Older3 += other.Older3; NeverPose += other.NeverPose;
    }

    internal void Clear()
    {
        Array.Clear(_hist); Array.Clear(_counts); Array.Clear(_max);
        Older1 = Older3 = NeverPose = ExtraBytes = PacketBytes = 0;
    }

    internal string FormatBands()
    {
        string BandText(int i, string label)
        {
            var b = Band(i);
            return string.Create(CultureInfo.InvariantCulture,
                $"{label}km[n={b.Count},p50={b.P50:0.###},p95={b.P95:0.###},max={b.Max:0.###}s]");
        }
        return $"{BandText(0, "0-1")} {BandText(1, "1-3")} {BandText(2, "3-6")} {BandText(3, "6+")}";
    }
}
