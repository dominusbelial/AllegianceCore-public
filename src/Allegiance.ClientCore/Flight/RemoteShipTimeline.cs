using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.ClientCore.Flight;

/// <summary>
/// P985 (EXPERIMENTAL, default OFF) — a bounded, sender-timestamped render
/// history for one remote ship. Unlike P813b's arrival-ordered history, late
/// packets are inserted by their mapped SERVER simulation instant. Duplicate
/// timestamps never enter the ring and an old packet cannot move the newest
/// sample backwards.
/// </summary>
/// <remarks>
/// The caller maps the wire tick to the session's client-time estimate before
/// calling <see cref="Feed"/> and uses that same estimate for
/// <see cref="TryGetPose"/>. Thus this class has no clock or network dependency.
/// Eight slots and all orientations are allocated at construction. Insertion is
/// O(8), lookup is O(8), and neither feeding nor evaluating allocates.
/// The returned orientation is reused scratch, exactly like
/// <see cref="ShipDeadReckoner.PoseAt"/>; copy it if it must be retained.
/// </remarks>
public sealed class RemoteShipTimeline
{
    public const int History = 8;
    public const float MinDelay = 0.050f;
    public const float MaxDelay = 0.500f;
    public const double MaxSampleAge = 1.0;
    public const double StallResetSeconds = 1.0;

    private const float InitialCadence = 0.100f;
    private const float DelaySmoothing = 0.20f;

    private struct Sample
    {
        public double T;
        public Vector3 Pos;
        public Vector3 Vel;
        public Orientation Ori;
    }

    private readonly Sample[] _buf = new Sample[History];
    private readonly Orientation _pose = new();
    private int _count;
    private double _cadence = InitialCadence;
    private double _jitter;
    private double _delay = InitialCadence;
    private double _lastNewestArrival = double.NaN;

    public RemoteShipTimeline()
    {
        for (int i = 0; i < History; i++) _buf[i].Ori = new Orientation();
    }

    public int SampleCount => _count;
    public float RenderDelay => (float)_delay;
    public float UpdateInterval => (float)_cadence;
    public float ArrivalJitter => (float)_jitter;

    /// <summary>Insert one pose at its mapped server timestamp. Returns false
    /// for a duplicate, an expired sample, or a late packet on the wrong side
    /// of a detected teleport. Arrival time is used only to estimate jitter;
    /// it never determines ring order.</summary>
    public bool Feed(Vector3 position, Vector3 velocity, Orientation orientation,
        double serverTime, double arrivalTime)
    {
        if (!double.IsFinite(serverTime) || !double.IsFinite(arrivalTime)) return false;

        int newest = _count - 1;
        if (_count > 0)
        {
            if (serverTime < _buf[newest].T - MaxSampleAge) return false;
            if (arrivalTime - _lastNewestArrival >= StallResetSeconds
                || serverTime - _buf[newest].T >= StallResetSeconds)
                Clear();
        }

        int insert = _count;
        for (int i = 0; i < _count; i++)
        {
            if (serverTime == _buf[i].T) return false;
            if (serverTime < _buf[i].T) { insert = i; break; }
        }

        // A discontinuous newest pose starts a new life/cut. A late OLD packet
        // which would bridge that cut is rejected instead of erasing the newer
        // life already received.
        bool discontinuity = false;
        if (insert > 0 && Cut(_buf[insert - 1].Pos, _buf[insert - 1].Vel,
                position, velocity, serverTime - _buf[insert - 1].T)) discontinuity = true;
        if (insert < _count && Cut(position, velocity,
                _buf[insert].Pos, _buf[insert].Vel, _buf[insert].T - serverTime)) discontinuity = true;
        if (discontinuity)
        {
            if (insert != _count) return false;
            Clear();
            insert = 0;
        }

        bool extendsNewest = _count == 0 || serverTime > _buf[_count - 1].T;
        if (extendsNewest && _count > 0)
        {
            double sourceDt = serverTime - _buf[_count - 1].T;
            double arrivalDt = arrivalTime - _lastNewestArrival;
            // Ignore the one-second stall as a cadence measurement. Ordinary
            // low-tier cadences through 2 Hz still adapt; jitter is bounded so
            // a single pathological gap cannot make delay oscillate.
            if (sourceDt >= 0.002 && sourceDt <= 0.500 && arrivalDt >= 0 && arrivalDt <= 0.750)
            {
                double jitterSample = Math.Min(0.250, Math.Abs(arrivalDt - sourceDt));
                _cadence += 0.20 * (sourceDt - _cadence);
                _jitter += 0.20 * (jitterSample - _jitter);
                // One source period covers ordinary interpolation; three EMA
                // jitter deviations cover the measured 0–150 ms delivery spread.
                double target = Math.Clamp(_cadence + 3.0 * _jitter, MinDelay, MaxDelay);
                _delay += DelaySmoothing * (target - _delay);
            }
        }

        // A full ring keeps its NEWEST window: a late packet older than every
        // retained sample would only displace a newer one.
        if (_count == History && insert == 0) return false;

        if (_count == History)
        {
            // A late insertion evicts the oldest; a newest append does too.
            // For an insertion elsewhere, discard the oldest first.
            for (int i = 1; i < History; i++) CopySample(i - 1, i);
            _count--;
            insert = Math.Max(0, insert - 1);
        }

        for (int i = _count; i > insert; i--) CopySample(i, i - 1);
        ref Sample dst = ref _buf[insert];
        dst.T = serverTime;
        dst.Pos = position;
        dst.Vel = velocity;
        dst.Ori.Set(orientation.GetForward(), orientation.GetUp());
        _count++;
        if (extendsNewest) _lastNewestArrival = arrivalTime;

        // Age is relative to sender time, never wall arrival. Pruning from the
        // left retains the sorted newest window and keeps memory fixed.
        int first = 0;
        double oldestWanted = _buf[_count - 1].T - MaxSampleAge;
        while (first < _count - 1 && _buf[first].T < oldestWanted) first++;
        if (first > 0)
        {
            for (int i = first; i < _count; i++) CopySample(i - first, i);
            _count -= first;
        }
        return true;
    }

    /// <summary>Clear every sample and all cadence/jitter history at a hard cut.</summary>
    public void Clear()
    {
        _count = 0;
        _cadence = InitialCadence;
        _jitter = 0;
        _delay = InitialCadence;
        _lastNewestArrival = double.NaN;
    }

    /// <summary>Evaluate at the current mapped server-clock estimate. Inside
    /// history this renders at <c>now - RenderDelay</c>. Past the newest sample
    /// it returns false so the caller uses the existing capped dead-reckoner;
    /// after a one-second stall the ring is reset before that fallback.</summary>
    public bool TryGetPose(double now, out Vector3 position, out Orientation orientation)
    {
        position = default;
        orientation = _pose;
        if (_count == 0 || !double.IsFinite(now)) return false;
        if (now - _buf[_count - 1].T >= StallResetSeconds)
        {
            Clear();
            return false;
        }
        if (_count < 2) return false;

        double rt = now - _delay;
        if (rt > _buf[_count - 1].T) return false;
        if (rt <= _buf[0].T)
        {
            position = _buf[0].Pos;
            _pose.Set(_buf[0].Ori.GetForward(), _buf[0].Ori.GetUp());
            return true;
        }

        for (int i = 0; i < _count - 1; i++)
        {
            if (_buf[i].T > rt || rt > _buf[i + 1].T) continue;
            double span = _buf[i + 1].T - _buf[i].T;
            if (span <= 1e-9) return false;
            float u = (float)((rt - _buf[i].T) / span);
            RemoteShipHermiteInterpolator.Hermite3(
                _buf[i].Pos, _buf[i].Vel * (float)span,
                _buf[i + 1].Pos, _buf[i + 1].Vel * (float)span,
                u, out position);
            RemoteShipHermiteInterpolator.OrientationSlerp(_buf[i].Ori, _buf[i + 1].Ori, u, _pose);
            return true;
        }

        // rt == newest is the remaining valid boundary.
        position = _buf[_count - 1].Pos;
        _pose.Set(_buf[_count - 1].Ori.GetForward(), _buf[_count - 1].Ori.GetUp());
        return true;
    }

    private static bool Cut(in Vector3 p0, in Vector3 v0, in Vector3 p1, in Vector3 v1, double dt)
    {
        if (dt <= 0) return false;
        float allowed = MathF.Max(250f, MathF.Max(v0.Length(), v1.Length()) * (float)dt * 4f + 50f);
        return (p1 - p0).LengthSquared() > allowed * allowed;
    }

    // Slots own their Orientation for the lifetime of the timeline. Shifting
    // values (rather than struct references) prevents two slots aliasing one
    // mutable orientation after a late insertion.
    private void CopySample(int destination, int source)
    {
        if (destination == source) return;
        _buf[destination].T = _buf[source].T;
        _buf[destination].Pos = _buf[source].Pos;
        _buf[destination].Vel = _buf[source].Vel;
        _buf[destination].Ori.Set(_buf[source].Ori.GetForward(), _buf[source].Ori.GetUp());
    }
}

/// <summary>
/// P985 review — maps the wire's ship-update slot (the server LOOP tick,
/// <c>Server.Broadcast.cs</c> <c>BroadcastShipStates((uint)TickIndex)</c>) onto the
/// client's local monotonic clock. The tick is NOT server sim time: sim time
/// (<c>_simNow</c>, what PING carries) advances by period × timeScale, so under
/// auto time dilation, slow motion or a single-player pause it drifts away from
/// tick/20 without bound. Instead this is a jitter-buffer playout clock:
/// offset = windowed minimum of (arrival − tick seconds) over the last two
/// 2-second windows (the least-delayed recent delivery), so it follows a real
/// latency change within ~4 s and is immune to the sim clock's rate.
/// Allocation-free; one per world, replaced with <c>ShipCaches</c>.
/// </summary>
public sealed class RemoteTimelineClock
{
    /// <summary>Production's native loop cadence (<c>ServerConfig.UpdatesPerSecond</c> = 20).</summary>
    public const double TickSeconds = 1.0 / 20.0;
    public const double WindowSeconds = 2.0;
    /// <summary>An offset step larger than this invalidates every converted
    /// history (server restart, tick counter reset, route change).</summary>
    public const double ResetStepSeconds = 0.25;

    private double _windowStart = double.NaN;
    private double _minCurrent = double.PositiveInfinity;
    private double _minPrevious = double.PositiveInfinity;
    private double _offset = double.NaN;
    private double _newestSource = double.NaN;

    public double Offset => _offset;

    /// <summary>Observe one arrival; returns the sample's local-clock time.
    /// <paramref name="stepped"/> is true when the offset jumped by more than
    /// <see cref="ResetStepSeconds"/> and existing histories must be cleared.</summary>
    public double Map(uint tick, double arrival, out bool stepped)
    {
        double source = tick * TickSeconds;
        double o = arrival - source;
        // A tick counter that runs backwards by more than a second is a new
        // server epoch (restart/reconnect): the old minimum must not pin it.
        bool epoch = !double.IsNaN(_newestSource) && source < _newestSource - 1.0;
        if (epoch || double.IsNaN(_newestSource) || source > _newestSource) _newestSource = source;
        if (epoch) { _windowStart = double.NaN; _minCurrent = double.PositiveInfinity; }
        if (double.IsNaN(_windowStart) || arrival - _windowStart >= WindowSeconds || arrival < _windowStart)
        {
            _minPrevious = double.IsNaN(_windowStart) ? double.PositiveInfinity : _minCurrent;
            _minCurrent = double.PositiveInfinity;
            _windowStart = arrival;
        }
        if (o < _minCurrent) _minCurrent = o;
        double est = Math.Min(_minCurrent, _minPrevious);
        stepped = epoch || (!double.IsNaN(_offset) && Math.Abs(est - _offset) > ResetStepSeconds);
        if (stepped)
        {
            // Forget the pre-step epoch entirely so the old minimum cannot pin it.
            _minPrevious = double.PositiveInfinity;
            _minCurrent = o;
            est = o;
        }
        _offset = est;
        return source + est;
    }
}
