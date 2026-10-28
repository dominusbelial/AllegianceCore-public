using System.Buffers.Binary;
using System.Diagnostics;

namespace Allegiance.Core.Transport;

/// <summary>
/// P866 — the named server counters native keeps in
/// <c>g.pServerCounters</c> (<c>SERVER_COUNTERS</c>, <c>FedSrv/counters.h:8-30</c>)
/// and feeds from two DirectPlay callbacks:
/// <list type="bullet">
/// <item><b><c>cPacketsOut</c> / <c>cBytesOut</c></b> — every
/// <c>FM_GUARANTEED</c> message handed to <c>FedMessaging</c>:
/// <c>cPacketsOut += cConnections</c> and
/// <c>cBytesOut += cb * cConnections</c> (<c>dfm</c> per recipient), plus the
/// per-second twins (<c>FedSrvSiteBase::OnMessageSent</c>,
/// <c>FedSrv/FedSrv.CPP:4613-4652</c>). The non-guaranteed branch deliberately
/// counts nothing (its per-second counters are commented out in native,
/// <c>:4646-4650</c>).</item>
/// <item><b><c>cTimeouts</c></b> — one per connection destroyed while it still
/// had a player attached, i.e. an UNREQUESTED drop (a LOGOFF deletes the player
/// in its own handler first so <c>GetPlayerFromConnection</c> returns null;
/// <c>FedSrvSiteBase::OnDestroyConnection</c>, <c>FedSrv.CPP:4549-4566</c>, with
/// the counter bump at <c>:4558-4562</c>). The per-second twin is drained from
/// the same window.</item>
/// </list>
/// <para><b>Port mapping.</b> The DirectPlay <c>OnMessageSent</c> callback has no
/// counterpart under the TCP transport, so the counters are fed where the port
/// hands a framed batch to the per-peer send queue
/// (<see cref="TcpTransport"/>'s <c>PeerSender.EnqueueCore</c>, the same place the
/// P661 egress accounting records an <c>Offered</c> frame). One frame is one
/// peer, so <c>cConnections</c> is 1 and each <c>FM_GUARANTEED</c> message in the
/// batch adds one packet and its <c>cbmsg</c> bytes. Frame walking is a flat scan
/// of already-built headers: no allocation, no codec, no CRC work.</para>
/// <para>Thread-safe by construction: writers are the per-peer send queues
/// (several threads), readers are the profiler window / admin commands. All
/// cumulative values are monotonic <see cref="Interlocked"/> counters; the
/// per-second values are the deltas of one <see cref="DrainWindow"/> call.</para>
/// </summary>
public sealed class ServerCounters
{
    private long _timeouts;
    private long _packetsOut;
    private long _bytesOut;
    private long _windowTimeouts;
    private long _windowPacketsOut;
    private long _windowBytesOut;
    private long _windowStart = Stopwatch.GetTimestamp();

    /// <summary>Native <c>cTimeouts</c>.</summary>
    public long CTimeouts => Interlocked.Read(ref _timeouts);

    /// <summary>Native <c>cPacketsOut</c>.</summary>
    public long CPacketsOut => Interlocked.Read(ref _packetsOut);

    /// <summary>Native <c>cBytesOut</c>.</summary>
    public long CBytesOut => Interlocked.Read(ref _bytesOut);

    /// <summary>
    /// Native <c>OnDestroyConnection</c>'s <c>cTimeouts++</c>
    /// (<c>FedSrv.CPP:4560-4561</c>): one unrequested connection loss while a
    /// player was still attached.
    /// </summary>
    public void RecordTimeout() => Interlocked.Increment(ref _timeouts);

    /// <summary>
    /// Count one already-framed egress frame exactly as native's
    /// <c>OnMessageSent</c> would have (<c>FedSrv.CPP:4617-4636</c>): for a
    /// guaranteed frame, every message in the frame adds one packet and
    /// <c>cbmsg</c> bytes. A non-guaranteed frame counts nothing. The frame
    /// layout is the transport's own: <c>[type:1][bodyLen:4][body]</c> where the
    /// body is one or more FEDMESSAGEs plus the trailing 4-byte CRC, so message
    /// headers end at <c>Length - 4</c> exactly like
    /// <see cref="EgressAccounting.Record"/>. Never retains or mutates the frame.
    /// </summary>
    public void RecordFrame(ReadOnlySpan<byte> frame, bool guaranteed)
    {
        if (!guaranteed || frame.Length < 9 || frame[0] != 2) return; // 5-byte prefix + 4-byte CRC
        int messageEnd = frame.Length - 4;
        int pos = 5;
        while (messageEnd - pos >= 4)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(frame[pos..]);
            if (size < 4 || size > messageEnd - pos) break;
            // cConnections is 1: the port enqueues one frame per peer, and each
            // native FM_CS_* message is one FEDMESSAGE.
            Interlocked.Increment(ref _packetsOut);
            Interlocked.Add(ref _bytesOut, size);
            pos += size;
        }
    }

    /// <summary>
    /// Native <c>cPacketsOutPerSecond</c> / <c>cBytesOutPerSecond</c> /
    /// <c>cTimeoutsPerSecond</c> (<c>FedSrv.CPP:4561,4634,4636</c>): the delta of
    /// the cumulative counters over the elapsed wall-clock window. Single report
    /// consumer (the profiler window / admin table); the cumulative values are
    /// never reset.
    /// </summary>
    public ServerCountersWindow DrainWindow()
    {
        long now = Stopwatch.GetTimestamp();
        double seconds = Math.Max((now - _windowStart) / (double)Stopwatch.Frequency, 1e-9);
        _windowStart = now;
        long timeouts = Interlocked.Read(ref _timeouts);
        long packets = Interlocked.Read(ref _packetsOut);
        long bytes = Interlocked.Read(ref _bytesOut);
        long dTimeouts = timeouts - _windowTimeouts;
        long dPackets = packets - _windowPacketsOut;
        long dBytes = bytes - _windowBytesOut;
        _windowTimeouts = timeouts;
        _windowPacketsOut = packets;
        _windowBytesOut = bytes;
        return new ServerCountersWindow(
            CTimeouts: timeouts,
            CPacketsOut: packets,
            CBytesOut: bytes,
            CTimeoutsPerSecond: dTimeouts / seconds,
            CPacketsOutPerSecond: dPackets / seconds,
            CBytesOutPerSecond: dBytes / seconds);
    }
}

/// <summary>P866 — one drain of <see cref="ServerCounters"/>: the cumulative
/// native counter values (<c>cTimeouts</c>/<c>cPacketsOut</c>/<c>cBytesOut</c>,
/// <c>FedSrv/counters.h:10-21</c>) plus their per-second twins.</summary>
public readonly record struct ServerCountersWindow(
    long CTimeouts,
    long CPacketsOut,
    long CBytesOut,
    double CTimeoutsPerSecond,
    double CPacketsOutPerSecond,
    double CBytesOutPerSecond);
