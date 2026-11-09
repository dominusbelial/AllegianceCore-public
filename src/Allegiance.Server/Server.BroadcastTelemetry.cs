using System.Globalization;
using Allegiance.Core.Profiling;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

public sealed partial class Server
{
    private const int TopPeers = 10;

    private BroadcastTelemetry? _broadcastTelemetry;
    private double _broadcastBudgetReportWall;
    private long _lastBroadcastCoalesced, _lastBroadcastDropped;
    // Report-time scratch only (never touched on the send path).
    private readonly PoseAgeWindow _broadcastBudgetAge = new();
    private readonly PoseAgeWindow[] _broadcastBudgetPeerAge = Enumerable.Range(0, TopPeers).Select(_ => new PoseAgeWindow()).ToArray();

    /// <summary>P983 test seam — the live diagnostic, or null when the flag is off.</summary>
    internal BroadcastTelemetry? BroadcastBudgetTelemetry => _broadcastTelemetry;

    /// <summary>P983 — resolve <c>profiler.broadcastBudget</c> (default false) plus the
    /// <c>ALLEGIANCE_BROADCAST_TELEMETRY</c> override (env wins, strict grammar),
    /// exactly the P600 flag style. OFF = a null field: the send path then pays one
    /// field check and allocates nothing.</summary>
    internal static bool ResolveBroadcastBudgetFlag(bool configured, string? environment)
        => environment?.Trim().ToLowerInvariant() switch
        {
            null or "" => configured,
            "1" or "true" => true,
            "0" or "false" => false,
            _ => throw new InvalidDataException("ALLEGIANCE_BROADCAST_TELEMETRY must be 0/1 or false/true"),
        };

    private void InitBroadcastTelemetry()
    {
        bool enabled = ResolveBroadcastBudgetFlag(_config.Profiler?.BroadcastBudget ?? false,
            Environment.GetEnvironmentVariable("ALLEGIANCE_BROADCAST_TELEMETRY"));
        _broadcastTelemetry = enabled ? new BroadcastTelemetry() : null;
        if (_broadcastTelemetry is not { } telemetry) return;
        _broadcastBudgetReportWall = telemetry.Clock();
        TickProfiler.WindowReported += EmitBroadcastBudgetWindow;
        Console.WriteLine("[boot] *** BROADCAST BUDGET TELEMETRY ACTIVE (profiler.broadcastBudget=true) — per-peer/per-category egress + " +
                          "update age; MEASUREMENT ONLY (nothing sent, ordered or rate-limited changes) ***"
                          + (TickProfiler.Instance is null
                              ? " records only: profiler.enabled=false, so the periodic report does not run (it rides the P600 profiler window)"
                              : ""));
    }

    private void ShutdownBroadcastTelemetry()
    {
        TickProfiler.WindowReported -= EmitBroadcastBudgetWindow;
        _broadcastTelemetry = null;
        _broadcastBudgetAge.Clear();
    }

    /// <summary>P983 — report on the P600 profiler cadence: one summary block per
    /// window, the top-N peers by bytes, the category split, and the server-wide
    /// update-age percentiles. Called by <see cref="TickProfiler.WindowReported"/>
    /// (sim thread, after the profiler releases its lock).</summary>
    private void EmitBroadcastBudgetWindow()
    {
        if (_broadcastTelemetry is not { } telemetry) return;
        double now = telemetry.Clock();
        double seconds = Math.Max(1e-6, now - _broadcastBudgetReportWall);
        _broadcastBudgetReportWall = now;
        var lines = BuildBroadcastBudgetReport(telemetry, now, seconds);
        foreach (string line in lines) Console.WriteLine(line);
        WriteBroadcastBudgetCsv(telemetry, now, seconds);
    }

    /// <summary>P983 test seam — the report block for the current rolling window.</summary>
    internal string TakeBroadcastBudgetReport(double nowSeconds, double windowSeconds)
        => _broadcastTelemetry is { } telemetry
            ? string.Join(Environment.NewLine, BuildBroadcastBudgetReport(telemetry, nowSeconds, windowSeconds))
            : "[broadcast-budget] disabled (profiler.broadcastBudget=false)";

    private string[] BuildBroadcastBudgetReport(BroadcastTelemetry telemetry, double nowSeconds, double windowSeconds)
    {
        long second = (long)nowSeconds;
        long totalBytes = telemetry.WindowTotal(second);
        double bps = totalBytes / windowSeconds;
        var lines = new List<string>(4 + TopPeers);
        string Split(BroadcastPeerTelemetry peer) => string.Create(CultureInfo.InvariantCulture,
            $"heavy={peer.WindowBytes(BroadcastCategory.HeavyShipUpdates, second)} light={peer.WindowBytes(BroadcastCategory.LightRankedPose, second)} " +
            $"keyed={peer.WindowBytes(BroadcastCategory.ReliableKeyed, second)} chat={peer.WindowBytes(BroadcastCategory.ChatLobby, second)} " +
            $"other={peer.WindowBytes(BroadcastCategory.Other, second)}");
        string SplitServer() => string.Create(CultureInfo.InvariantCulture,
            $"heavy={telemetry.WindowBytes(BroadcastCategory.HeavyShipUpdates, second)} light={telemetry.WindowBytes(BroadcastCategory.LightRankedPose, second)} " +
            $"keyed={telemetry.WindowBytes(BroadcastCategory.ReliableKeyed, second)} chat={telemetry.WindowBytes(BroadcastCategory.ChatLobby, second)} " +
            $"other={telemetry.WindowBytes(BroadcastCategory.Other, second)}");
        string TrendServer(BroadcastCategory category) => string.Join(",", Enumerable.Range(0, BroadcastTelemetry.BucketCount)
            .Select(offset => telemetry.BytesAt(category, second - (BroadcastTelemetry.BucketCount - 1) + offset)));
        long MessagesServer() => telemetry.WindowMessages(BroadcastCategory.HeavyShipUpdates, second)
            + telemetry.WindowMessages(BroadcastCategory.LightRankedPose, second)
            + telemetry.WindowMessages(BroadcastCategory.ReliableKeyed, second)
            + telemetry.WindowMessages(BroadcastCategory.ChatLobby, second)
            + telemetry.WindowMessages(BroadcastCategory.Other, second);

        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"[broadcast-budget] windowSecs={windowSeconds:0.000} peers={telemetry.PeerCount} bytes={totalBytes} bps={bps:0} " +
            $"perPeerBps={(telemetry.PeerCount > 0 ? bps / telemetry.PeerCount : 0):0} msgs={MessagesServer()}"));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"[bb-category] bytes={SplitServer()} " +
            $"pct={Pct(telemetry.WindowBytes(BroadcastCategory.HeavyShipUpdates, second), totalBytes):0.0}/{Pct(telemetry.WindowBytes(BroadcastCategory.LightRankedPose, second), totalBytes):0.0}/" +
            $"{Pct(telemetry.WindowBytes(BroadcastCategory.ReliableKeyed, second), totalBytes):0.0}/{Pct(telemetry.WindowBytes(BroadcastCategory.ChatLobby, second), totalBytes):0.0}/{Pct(telemetry.WindowBytes(BroadcastCategory.Other, second), totalBytes):0.0}"));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"[bb-trend] seconds=[-4,-3,-2,-1,0] heavy=[{TrendServer(BroadcastCategory.HeavyShipUpdates)}] " +
            $"light=[{TrendServer(BroadcastCategory.LightRankedPose)}] keyed=[{TrendServer(BroadcastCategory.ReliableKeyed)}] " +
            $"chat=[{TrendServer(BroadcastCategory.ChatLobby)}] other=[{TrendServer(BroadcastCategory.Other)}]"));

        AddAgeReport(lines, telemetry, second);

        var queues = _host?.Transport?.SamplePeerQueues();
        long coalesced = _broadcastPublisher is { } publisher ? Interlocked.Read(ref publisher.CoalescedSnapshots) : 0;
        long dropped = _broadcastPublisher is { } publisher2 ? Interlocked.Read(ref publisher2.DroppedStreamFrames) : 0;
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"[bb-queue] queuedBytesTotal={queues?.TotalQueuedBytes ?? 0} queuedBytesMax={queues?.MaxQueuedBytes ?? 0} " +
            $"orderedPendingMax={queues?.MaxOrderedPending ?? 0} streamPendingMax={queues?.MaxStreamPending ?? 0} " +
            $"oldestGuaranteedMs={queues?.MaxOldestGuaranteedMs ?? 0} coalescedSnapshots={coalesced - _lastBroadcastCoalesced} droppedStreamFrames={dropped - _lastBroadcastDropped}"));
        _lastBroadcastCoalesced = coalesced;
        _lastBroadcastDropped = dropped;

        var peers = telemetry.SnapshotPeers()
            .Select(pair => (pair.Id, pair.Peer, Bytes: pair.Peer.WindowTotal(second)))
            .Where(row => row.Bytes > 0)
            .OrderByDescending(row => row.Bytes).ThenBy(row => row.Id.Value)
            .Take(TopPeers).ToArray();
        for (int i = 0; i < peers.Length; i++)
        {
            var (id, peer, bytes) = peers[i];
            string label = _peerShips.TryGetValue(id, out var live)
                ? $"peer={id.Value} ship={live.Ship.Id} side={live.Ship.SideId?.ToString() ?? "-"}"
                : $"peer={id.Value} ship=- side=-";
            var window = _broadcastBudgetPeerAge[i];
            window.Clear();
            int tracked = 0;
            long counted = 0;
            if (_viewerPoseCadence.TryGetValue(id, out var cadence)) tracked = cadence.TrackedCount;
            foreach (var (shipId, stampMs) in peer.LastPoseMs)
            {
                if (cadence is not null && !cadence.Contains(shipId)) continue;
                counted++;
                window.RecordBand(0, Math.Max(0, telemetry.SimNowMs - stampMs) / 1000d);
            }
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"[bb-top] {label} bytes={bytes} pct={Pct(bytes, totalBytes):0.0} {Split(peer)} " +
                $"msgs={peer.WindowMessages(BroadcastCategory.HeavyShipUpdates, second) + peer.WindowMessages(BroadcastCategory.LightRankedPose, second) + peer.WindowMessages(BroadcastCategory.ReliableKeyed, second) + peer.WindowMessages(BroadcastCategory.ChatLobby, second) + peer.WindowMessages(BroadcastCategory.Other, second)} " +
                $"ageShips={counted} neverPosed={Math.Max(0, tracked - (int)counted)} {FormatAge(window)}"));
        }
        return lines.ToArray();
    }

    private void AddAgeReport(List<string> lines, BroadcastTelemetry telemetry, long second)
    {
        var window = _broadcastBudgetAge;
        window.Clear();
        var peers = telemetry.SnapshotPeers();
        long counted = 0, never = 0;
        long visiblePairs = _viewerPoseCadence.Values.Sum(state => (long)state.TrackedCount);
        foreach (var (id, peer) in peers)
        {
            _viewerPoseCadence.TryGetValue(id, out var cadence);
            int tracked = cadence?.TrackedCount ?? 0;
            long peerCounted = 0;
            foreach (var (shipId, stampMs) in peer.LastPoseMs)
            {
                if (cadence is not null && !cadence.Contains(shipId)) continue;
                peerCounted++;
                window.RecordBand(0, Math.Max(0, telemetry.SimNowMs - stampMs) / 1000d);
            }
            counted += peerCounted;
            if (tracked > peerCounted) never += tracked - peerCounted;
        }
        lines.Insert(3, string.Create(CultureInfo.InvariantCulture,
            $"[bb-age] visiblePairs={visiblePairs} ships={counted} neverPosed={never} {FormatAge(window)} " +
            $"older1s={window.Older1} older3s={window.Older3} simMs={telemetry.SimNowMs:0}"));
    }

    private static double Pct(long value, long total) => total > 0 ? 100d * value / total : 0;

    private static string FormatAge(PoseAgeWindow window)
    {
        var (count, p50, p95, max) = window.Band(0);
        return string.Create(CultureInfo.InvariantCulture,
            $"samples={count} p50={p50 * 1000:0}ms p95={p95 * 1000:0}ms max={max * 1000:0}ms");
    }

    /// <summary>P983 — the same numbers on the profiler's CSV path when one is set
    /// (distinct <c>bb-*</c> row kinds; the row body is key=value, documented in
    /// docs/reports/BROADCAST_TELEMETRY_P983.md).</summary>
    private void WriteBroadcastBudgetCsv(BroadcastTelemetry telemetry, double nowSeconds, double windowSeconds)
    {
        if (TickProfiler.Instance?.CsvPath is not { Length: > 0 } || TickProfiler.Instance is not { } profiler) return;
        long second = (long)nowSeconds;
        long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long totalBytes = telemetry.WindowTotal(second);
        profiler.WriteCsvRow(string.Create(CultureInfo.InvariantCulture,
            $"{unix},{windowSeconds:0.###},bb-total,bytes={totalBytes},bps={totalBytes / windowSeconds:0}," +
            $"heavy={telemetry.WindowBytes(BroadcastCategory.HeavyShipUpdates, second)},light={telemetry.WindowBytes(BroadcastCategory.LightRankedPose, second)}," +
            $"keyed={telemetry.WindowBytes(BroadcastCategory.ReliableKeyed, second)},chat={telemetry.WindowBytes(BroadcastCategory.ChatLobby, second)}," +
            $"other={telemetry.WindowBytes(BroadcastCategory.Other, second)},ships={_broadcastBudgetAge.Band(0).Count}," +
            $"p50ms={_broadcastBudgetAge.Band(0).P50 * 1000:0},p95ms={_broadcastBudgetAge.Band(0).P95 * 1000:0},maxms={_broadcastBudgetAge.Band(0).Max * 1000:0}"));
        foreach (var (id, peer) in telemetry.SnapshotPeers().OrderByDescending(pair => pair.Peer.WindowTotal(second)).Take(TopPeers))
        {
            profiler.WriteCsvRow(string.Create(CultureInfo.InvariantCulture,
                $"{unix},{windowSeconds:0.###},bb-peer,peer={id.Value},bytes={peer.WindowTotal(second)}," +
                $"heavy={peer.WindowBytes(BroadcastCategory.HeavyShipUpdates, second)},light={peer.WindowBytes(BroadcastCategory.LightRankedPose, second)}," +
                $"keyed={peer.WindowBytes(BroadcastCategory.ReliableKeyed, second)},chat={peer.WindowBytes(BroadcastCategory.ChatLobby, second)}," +
                $"other={peer.WindowBytes(BroadcastCategory.Other, second)},ships={peer.LastPoseMs.Count}"));
        }
    }
}
