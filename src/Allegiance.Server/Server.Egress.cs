using System.Globalization;
using System.Text.Json;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    /// <summary>Last completed profiler window; read-only diagnostic snapshot.
    /// The admin egress command prints this without draining/resetting counters.</summary>
    public EgressWindow? LastEgressWindow { get; private set; }

    /// <summary>P866 — the last drained <see cref="ServerCounters"/> window: the
    /// native <c>cTimeouts</c>/<c>cPacketsOut</c>/<c>cBytesOut</c> values and their
    /// per-second twins (read-only snapshot).</summary>
    public ServerCountersWindow? LastServerCounters { get; private set; }

    private void EmitEgressWindow()
    {
        EmitSendWindow();
        // P866 — the named native server counters (FedSrv/counters.h:10-21), on
        // the same photometer window as the egress rows. Independent of the
        // profiler-gated egress accounting below, so the line is always emitted.
        var counters = Counters.DrainWindow();
        LastServerCounters = counters;
        Console.WriteLine(FormattableString.Invariant(
            $"[server-counters] cTimeouts={counters.CTimeouts} cTimeoutsPerSecond={counters.CTimeoutsPerSecond:0.000} cPacketsOut={counters.CPacketsOut} cPacketsOutPerSecond={counters.CPacketsOutPerSecond:0.000} cBytesOut={counters.CBytesOut} cBytesOutPerSecond={counters.CBytesOutPerSecond:0.000}"));
        if (_host?.Transport?.Egress is not { } accounting) return;
        var window = accounting.DrainWindow();
        LastEgressWindow = window;
        Console.WriteLine(FormatEgressSummary(window));
    }

    private PeerSendWindow[] _sendPeerWindows = [];
    private SendWorkerWindow[] _sendWorkerWindows = [];

    private void EmitSendWindow()
    {
        if (_host?.Transport is not { } tcp) return;
        // P813d — bounded reliable-tier telemetry (shed/park/coalesce per class).
        // Shows queue pressure quantitatively so the 200v200 rig can compare the
        // flag OFF vs ON. One-shot & reliable-stream shed are expected to stay 0
        // (guaranteed frames are never shed); best-effort stream is the shedder's
        // only candidate. Allocation happens only here (one line per window).
        if (tcp.Reliable is { } rel)
        {
            var rw = rel.DrainWindow();
            Console.WriteLine(FormattableString.Invariant(
                $"[reliable] windowSecs={rw.WindowSeconds:0.000} oneShotParked={rw.OneShotParked} oneShotParkedLane={rw.OneShotParkedLane} shed=[oneShot={rw.Shed[0]} relStream={rw.Shed[1]} bestStream={rw.Shed[2]} bestOrdered={rw.Shed[3]}] coalesced=[oneShot={rw.Coalesced[0]} relStream={rw.Coalesced[1]} bestStream={rw.Coalesced[2]} bestOrdered={rw.Coalesced[3]}]"));
        }
        // Reused after the first profiler callback; no diagnostic storage on the hot path.
        int peerCapacity = Math.Max(1024, TcpTransport.MaxConnections);
        int workerCapacity = Math.Max(_transportPoolWorkers, Environment.ProcessorCount);
        if (_sendPeerWindows.Length < peerCapacity) _sendPeerWindows = new PeerSendWindow[peerCapacity];
        if (_sendWorkerWindows.Length < workerCapacity) _sendWorkerWindows = new SendWorkerWindow[workerCapacity];
        var pool = tcp as TcpTransport ?? (tcp as DualTransport)?.Tcp;
        if (pool is null) return;
        var window = pool.DrainSendWindow(_sendPeerWindows, _sendWorkerWindows);
        Console.WriteLine(FormattableString.Invariant($"[send-pool] windowSecs={window.Seconds:0.000} workers={window.Workers} peers={window.Peers} enqueuedBytes={window.EnqueuedBytes} writtenBytes={window.WrittenBytes} queuedBytes={window.QueuedBytes} queueGrowthBytes={window.QueueGrowthBytes}"));
        for (int i = 0; i < window.Workers; i++)
        {
            var w = _sendWorkerWindows[i];
            Console.WriteLine(FormattableString.Invariant($"[send-worker] worker={w.Worker} state={w.State} writeCalls={w.WriteCalls} busyWriteMs={w.BusyWriteMs:0.###} longestWriteMs={w.LongestWriteMs:0.###} currentWriteMs={w.CurrentWriteMs:0.###}"));
        }
        for (int i = 0; i < window.Peers; i++)
        {
            var p = _sendPeerWindows[i];
            if (p.EnqueuedBytes == 0 && p.WrittenBytes == 0 && p.QueuedBytes == 0) continue;
            Console.WriteLine($"[send-peer] peer={p.Peer} enqueuedBytes={p.EnqueuedBytes} writtenBytes={p.WrittenBytes} queuedBytes={p.QueuedBytes} noProgressMs={p.NoProgressMs}");
        }
        if (window.DrainCollapsed)
            Console.WriteLine(FormattableString.Invariant($"[send-stall] windowSecs={window.Seconds:0.000} enqueuedBytes={window.EnqueuedBytes} writtenBytes={window.WrittenBytes} queuedBytes={window.QueuedBytes} queueGrowthBytes={window.QueueGrowthBytes} workers={window.Workers} peers={window.Peers}"));
    }

    internal static string FormatEgressSummary(EgressWindow window)
    {
        var rows = window.Rows;
        long offered = rows.Sum(r => r.OfferedBytes), sent = rows.Sum(r => r.SentBytes);
        long coalesced = rows.Sum(r => r.CoalescedBytes), dropped = rows.Sum(r => r.DroppedBytes);
        string splits = string.Join(",", new[] { "human", "harness" }.SelectMany(kind =>
            new[] { "ordered", "stream" }.Select(lane =>
                $"{kind}/{lane}={rows.Where(r => r.PeerKind == kind && r.Lane == lane).Sum(r => r.SentBytes)}")));
        var types = rows.Where(r => r.Fmid != EgressAccounting.OverheadFmid).GroupBy(r => r.Fmid)
            .Select(g => new
            {
                Fmid = g.Key, Offered = g.Sum(r => r.OfferedBytes), Sent = g.Sum(r => r.SentBytes),
                OfferedMessages = g.Sum(r => r.OfferedMessages), SentMessages = g.Sum(r => r.SentMessages),
                CoalescedFrames = g.Sum(r => r.CoalescedFrames), DroppedFrames = g.Sum(r => r.DroppedFrames),
            }).ToArray();
        double seconds = Math.Max(window.WindowSeconds, 1e-9);
        string topSent = string.Join(";", types.Where(t => t.Sent > 0).OrderByDescending(t => t.Sent)
            .ThenBy(t => t.Fmid).Take(10).Select(t => FormattableString.Invariant(
                $"{t.Fmid}:{EgressTypeName(t.Fmid)} bytes={t.Sent} pct={(sent > 0 ? 100d * t.Sent / sent : 0):0.0} msgs/s={t.SentMessages / seconds:0.0} avg={(t.SentMessages > 0 ? t.Sent / (double)t.SentMessages : 0):0.0} coalescedFrames={t.CoalescedFrames} droppedFrames={t.DroppedFrames}")));
        string topOffered = string.Join(";", types.Where(t => t.Offered > 0).OrderByDescending(t => t.Offered)
            .ThenBy(t => t.Fmid).Take(10).Select(t => FormattableString.Invariant(
                $"{t.Fmid} bytes={t.Offered} pct={(offered > 0 ? 100d * t.Offered / offered : 0):0.0} msgs/s={t.OfferedMessages / seconds:0.0} avg={(t.OfferedMessages > 0 ? t.Offered / (double)t.OfferedMessages : 0):0.0}")));
        return string.Create(CultureInfo.InvariantCulture,
            $"[egress-by-type] windowSecs={window.WindowSeconds:0.000} offeredBytes={offered} sentBytes={sent} coalescedBytes={coalesced} droppedBytes={dropped} overheadSentBytes={rows.Where(r => r.Fmid == EgressAccounting.OverheadFmid).Sum(r => r.SentBytes)} sentScope=socket-accepted-including-framing offeredScope=enqueued-including-framing sentSplit=[{splits}] topSent=[{topSent}] topOffered=[{topOffered}]");
    }

    // Names for the principal egress types. Numeric fmid remains authoritative
    // for every other type and extensions; no reflection/codec construction.
    private static string EgressTypeName(int fmid) => fmid switch
    {
        2 => "FM_S_LOGONACK", 7 => "FM_S_SET_CLUSTER", 20 => "FM_S_SHIP_DELETE",
        26 => "FM_S_EXPORT", 38 => "FM_S_MISSIONDEF", 65 => "FM_S_BUCKET_STATUS",
        57 => "FM_S_STATIONS_UPDATE", 78 => "FM_S_SINGLE_SHIP_UPDATE", 90 => "FM_S_SHIP_STATUS",
        92 => "FM_S_PROBES_UPDATE", 95 => "FM_S_JOINED_MISSION", 175 => "FM_S_LIGHT_SHIPS_UPDATE",
        176 => "FM_S_HEAVY_SHIPS_UPDATE", 179 => "FM_S_ASTEROIDS_UPDATE",
        211 => "SideRoster", 214 => "ClusterWarningUpdate", 853 => "MineStrength", 991 => "TimescaleUpdate",
        _ => "fmid",
    };

    private void DumpEgressWindow()
    {
        if (LastEgressWindow is not { } window)
        {
            Console.WriteLine("[egress-by-type-table] no completed window; enable profiler and wait one report interval");
            return;
        }
        Console.WriteLine("[egress-by-type-table] " + JsonSerializer.Serialize(window, ServerJsonContext.Default.EgressWindow));
    }
}
