namespace Allegiance.Core.Transport;

/// <summary>
/// P813d — the reliable tier of a peer's outbound messages, classified by
/// <see cref="ReliableClass"/>. A <b>one-shot</b> message (chat, kill feed,
/// death, dock, ship delete, game over) has no repair path: if the transport
/// ever sheds or coalesces it, the client is wrong for good. Native sends every
/// transactional/one-shot message <c>FM_GUARANTEED</c> (e.g. chat
/// FedSrv.CPP:3387, kill feed FedSrv.CPP:11914, ship-delete fsship.cpp:223,
/// dock fsship.cpp:1042, game-over fsmission.cpp:3436-3438) and only streams
/// best-effort (HEAVY/LIGHT_SHIPS_UPDATE FedSrv.CPP:4189/4195). This telemetry
/// gives the rig bounded per-class visibility into exactly what the transport
/// is parking (reliable one-shots waiting), shedding (best-effort stream frames
/// dropped under capacity), and coalescing (latest-wins superseded stream
/// frames), so a 200v200 run shows pressure quantitatively.
/// </summary>
public enum ReliableClass
{
    /// <summary>Ordered (coalesceKey==0), guaranteed — one-shot/transactional
    /// traffic (chat, kills, deaths, docks, ship deletes, game over, exports).</summary>
    OneShot = 0,
    /// <summary>Stream (coalesceKey!=0), guaranteed — the own-ship pose stream
    /// (P608), never shed but still latest-wins bounded.</summary>
    ReliableStream = 1,
    /// <summary>Stream (coalesceKey!=0), best-effort — self-healing ship/station/
    /// asteroid/probe state; may be coalesced and shed.</summary>
    BestEffortStream = 2,
    /// <summary>Ordered (coalesceKey==0), best-effort — the rare plain-ordered
    /// unreliable send; never coalesced, and ordered-lane frames are never shed.</summary>
    BestEffortOrdered = 3,
}

/// <summary>
/// P813d — host-only fixed storage (a handful of <c>long</c>s), allocated only
/// when the profiler is on, mirroring <see cref="EgressAccounting"/>'s null
/// fast-path. No decoding or allocation on the send path: the transport just
/// calls <see cref="RecordShed"/>/<see cref="RecordCoalesced"/>/
/// <see cref="RecordOneShotParked"/> with the class already computed. The
/// <b>bounded</b> guarantee is structural — a fixed array of counters, no
/// per-message retention — so the rig can log it every window without cost.
/// </summary>
public sealed class ReliableTelemetry
{
    private const int Classes = 4;
    private readonly long[] _shed = new long[Classes];
    private readonly long[] _coalesced = new long[Classes];
    private long _oneShotParked;
    private long _oneShotParkedLane;
    private long _windowStart = System.Diagnostics.Stopwatch.GetTimestamp();

    /// <summary>An under-capacity shed dropped a best-effort frame of
    /// <paramref name="cls"/>. Reliable classes are never shed, so this is
    /// expected to stay zero for <see cref="ReliableClass.OneShot"/> and
    /// <see cref="ReliableClass.ReliableStream"/>.</summary>
    public void RecordShed(ReliableClass cls)
        => Interlocked.Increment(ref _shed[(int)cls]);

    /// <summary>A latest-wins coalesce superseded a pending stream frame of
    /// <paramref name="cls"/> in place (the older snapshot was not sent).</summary>
    public void RecordCoalesced(ReliableClass cls)
        => Interlocked.Increment(ref _coalesced[(int)cls]);

    /// <summary>An ordered-guaranteed one-shot frame was enqueued. When
    /// <paramref name="parkLane"/> is true it was routed to the dedicated
    /// reliable park lane (P813d experimental flag ON); when false it rode the
    /// ordinary ordered lane (today's behaviour, flag OFF).</summary>
    public void RecordOneShotParked(bool parkLane)
    {
        Interlocked.Increment(ref _oneShotParked);
        if (parkLane) Interlocked.Increment(ref _oneShotParkedLane);
    }

    /// <summary>Atomic-exchange drain, conserving every counter across windows
    /// (a concurrent update may straddle a boundary). Single report consumer.</summary>
    public ReliableWindow DrainWindow()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = (now - _windowStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        _windowStart = now;
        var shed = new long[Classes];
        var coalesced = new long[Classes];
        for (int i = 0; i < Classes; i++)
        {
            shed[i] = Interlocked.Exchange(ref _shed[i], 0);
            coalesced[i] = Interlocked.Exchange(ref _coalesced[i], 0);
        }
        return new ReliableWindow(
            DateTimeOffset.UtcNow, seconds,
            Interlocked.Exchange(ref _oneShotParked, 0),
            Interlocked.Exchange(ref _oneShotParkedLane, 0),
            shed, coalesced);
    }
}

/// <summary>P813d — one drained telemetry window. Arrays are indexed by
/// <see cref="ReliableClass"/>. <c>oneShotParkedLane</c> is how many one-shots
/// took the experimental park lane (0 when the flag is OFF).</summary>
public readonly record struct ReliableWindow(
    DateTimeOffset WindowEndUtc,
    double WindowSeconds,
    long OneShotParked,
    long OneShotParkedLane,
    long[] Shed,
    long[] Coalesced);
