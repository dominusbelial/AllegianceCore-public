using System.Globalization;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>
/// P990 — why one (peer, visible ship) pair carries a stale pose, or none at all.
/// The list is the brief's subject-state classification, split so the report can
/// say which of them the age metric should not have counted in the first place
/// (<see cref="Docked"/>, <see cref="Dead"/>, <see cref="Lifepod"/>,
/// <see cref="Parented"/>, <see cref="OtherSector"/>, <see cref="SubjectUnknown"/>)
/// and which are real starvation (<see cref="Starved"/>, <see cref="Unserved"/>).
/// </summary>
internal enum StaleReason : byte
{
    /// <summary>The subject is docked; native removes a docked ship from its
    /// cluster's ship list (<c>Igc/shipIGC.h:421-459</c> — <c>SetStation</c> →
    /// <c>AddShip</c> "sets the ship's cluster to NULL"), and only the cluster's own
    /// ship list is walked (<c>FedSrv/FedSrv.CPP:3728-3752</c>).</summary>
    Docked = 0,
    /// <summary>The subject is destroyed/awaiting respawn; native drops it from
    /// the sector the same way (<c>Sector.GetShipUpdateCandidates</c> mirrors it).</summary>
    Dead = 1,
    /// <summary>The subject is a lifepod hull (already a pod: the pre-eject ship
    /// is dead, so a stale stamp on a pod hull is a metric artefact).</summary>
    Lifepod = 2,
    /// <summary>The subject is a turret/observer child (<c>ParentShip != null</c>):
    /// native puts those on the turret lanes, never in <c>rgShips</c>
    /// (FedSrv.CPP:3779-3790).</summary>
    Parented = 3,
    /// <summary>The subject is in another sector than the viewer's: the stamp is a
    /// leftover from an earlier sweep and the pair is not a live visible pair.</summary>
    OtherSector = 4,
    /// <summary>The viewer's own pose pass has not run for over 3 s (peer parked,
    /// mission not started, peer not seated): nothing about the subject is wrong.</summary>
    ViewerNotSwept = 5,
    /// <summary>The subject has not moved since its stamp: the record the peer
    /// holds is still exact, so re-sending it would carry no information (the
    /// "stationary and legitimately not resent" case).</summary>
    Stationary = 6,
    /// <summary>A visible, flying, in-sector subject that really was not re-posed
    /// for over 3 s: native's deviation ranking owes it a heavy record.</summary>
    Starved = 7,
    /// <summary>The pair is tracked but the ship id is not in any live mission any
    /// more (retired world / reused id): a metric artefact.</summary>
    SubjectUnknown = 8,
    /// <summary>Never posed because the viewer's visibility set is younger than
    /// the classification window (a joiner whose rotation has not come around).</summary>
    NewPair = 9,
    /// <summary>Never posed although the viewer has been swept for over 3 s: the
    /// pair was tracked but no pose record was ever queued (a real gap).</summary>
    Unserved = 10,
}

internal static class StaleReasonNames
{
    /// <summary>Index order of <see cref="StaleReason"/>; the report prints the
    /// counters in this order.</summary>
    internal static readonly string[] All =
        ["docked", "dead", "lifepod", "parented", "otherSector", "viewerNotSwept", "stationary",
         "starved", "subjectUnknown", "newPair", "unserved"];
}

/// <summary>
/// P990 — the subject state the stale-age classification needs, copied out of the
/// live <see cref="Ship"/> so the decision itself is a pure function (unit
/// testable without a world).
/// </summary>
internal readonly record struct AgeSubject(bool Known, bool Docked, bool Dead, bool Lifepod,
    bool Parented, bool SameSector, bool Stationary);

/// <summary>P990 — the viewer's pose-pass state for the same decision.</summary>
internal readonly record struct AgeViewer(bool SweptRecently, bool NewViewer);

public sealed partial class Server
{
    private const int TopPeers = 10;
    private const int TopLaneIds = 8;

    private BroadcastTelemetry? _broadcastTelemetry;
    private double _broadcastBudgetReportWall;
    private long _lastBroadcastCoalesced, _lastBroadcastDropped;
    // Report-time scratch only (never touched on the send path).
    private readonly PoseAgeWindow _broadcastBudgetAge = new();
    private readonly PoseAgeWindow[] _broadcastBudgetPeerAge = Enumerable.Range(0, TopPeers).Select(_ => new PoseAgeWindow()).ToArray();
    private readonly long[] _broadcastBudgetWhy = new long[StaleReasonNames.All.Length];
    private readonly long[] _broadcastBudgetWhyNever = new long[StaleReasonNames.All.Length];
    private readonly long[] _broadcastBudgetLaneBytes = new long[BroadcastTelemetry.FmidSlots];
    private readonly long[] _broadcastBudgetLaneMessages = new long[BroadcastTelemetry.FmidSlots];
    private readonly long[] _broadcastBudgetKeyedBytes = new long[BroadcastTelemetry.KeyedFamilies];
    private readonly long[] _broadcastBudgetKeyedMessages = new long[BroadcastTelemetry.KeyedFamilies];

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
        var lines = new List<string>(12 + TopPeers);
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

        AddLaneReport(lines, telemetry, second, totalBytes);

        var subjects = BroadcastSubjectLookup();
        AddAgeReport(lines, telemetry, second, subjects);

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
            foreach (var stamp in peer.LastPoses)
            {
                if (cadence is not null && !cadence.Contains(stamp.ShipId)) continue;
                counted++;
                window.RecordBand(0, Math.Max(0, telemetry.SimNowMs - stamp.SimMs) / 1000d);
            }
            long sweptAgoMs = peer.Sweeps == 0 ? -1 : (long)Math.Max(0, telemetry.SimNowMs - peer.LastSweepMs);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"[bb-top] {label} bytes={bytes} pct={Pct(bytes, totalBytes):0.0} {Split(peer)} " +
                $"msgs={peer.WindowMessages(BroadcastCategory.HeavyShipUpdates, second) + peer.WindowMessages(BroadcastCategory.LightRankedPose, second) + peer.WindowMessages(BroadcastCategory.ReliableKeyed, second) + peer.WindowMessages(BroadcastCategory.ChatLobby, second) + peer.WindowMessages(BroadcastCategory.Other, second)} " +
                $"ageShips={counted} neverPosed={Math.Max(0, tracked - (int)counted)} sweptAgoMs={sweptAgoMs} {FormatAge(window)}"));
        }
        return lines.ToArray();
    }

    /// <summary>
    /// P990 — the two opaque lanes, broken open. <c>[bb-lane]</c> is the one-line
    /// summary (frames + bytes per lane and their share of the window);
    /// <c>[bb-other]</c> ranks the ordered one-shot lane by wire <c>fmid</c> (the
    /// top <see cref="TopLaneIds"/> ids plus the remainder, so the row reconciles
    /// with the lane total); <c>[bb-keyed]</c> prints every coalesce-key family the
    /// port uses. Both lanes are server-wide by construction (see
    /// <see cref="BroadcastTelemetry"/>).
    /// </summary>
    private void AddLaneReport(List<string> lines, BroadcastTelemetry telemetry, long second, long totalBytes)
    {
        var (ids, laneTotal) = FillLaneTables(telemetry, second);
        long otherBytes = telemetry.WindowBytes(BroadcastCategory.Other, second);
        long otherMessages = telemetry.WindowMessages(BroadcastCategory.Other, second);
        long keyedBytes = telemetry.WindowBytes(BroadcastCategory.ReliableKeyed, second);
        long keyedMessages = telemetry.WindowMessages(BroadcastCategory.ReliableKeyed, second);
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"[bb-lane] ordered=bytes={otherBytes} msgs={otherMessages} ids={ids} attributedBytes={laneTotal} " +
            $"pct={Pct(otherBytes, totalBytes):0.0} keyed=bytes={keyedBytes} msgs={keyedMessages} pct={Pct(keyedBytes, totalBytes):0.0}"));

        var ranked = Enumerable.Range(0, BroadcastTelemetry.FmidSlots)
            .Where(fmid => _broadcastBudgetLaneBytes[fmid] > 0)
            .OrderByDescending(fmid => _broadcastBudgetLaneBytes[fmid]).ThenBy(fmid => fmid)
            .Take(TopLaneIds).ToArray();
        long shownBytes = 0;
        var parts = new List<string>(TopLaneIds + 1);
        foreach (int fmid in ranked)
        {
            shownBytes += _broadcastBudgetLaneBytes[fmid];
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"fmid={fmid}({MessageName(fmid)}) bytes={_broadcastBudgetLaneBytes[fmid]} " +
                $"msgs={_broadcastBudgetLaneMessages[fmid]} pct={Pct(_broadcastBudgetLaneBytes[fmid], otherBytes):0.0}"));
        }
        if (laneTotal - shownBytes > 0 || ids > ranked.Length)
            parts.Add(string.Create(CultureInfo.InvariantCulture,
                $"rest=bytes={laneTotal - shownBytes} ids={ids - ranked.Length} msgs={otherMessages - ranked.Sum(fmid => _broadcastBudgetLaneMessages[fmid])}"));
        lines.Add("[bb-other] " + string.Join(" | ", parts));

        static string KeyName(int family) => family switch
        {
            1 => "shipStates(78 teammate posBatch)",
            2 => "shipsUpdate(175/176 HEAVY/LIGHT)",
            3 => "stations(57)",
            4 => "asteroids(179)",
            5 => "probes(92)",
            6 => "missiles(205)",
            7 => "missileLock(204)",
            8 => "missileTrack(208)",
            9 => "ownMining(206)",
            12 => "ownPose(78 own ship, guaranteed)",
            13 => "ownCloak(220)",
            _ => family == BroadcastTelemetry.KeyedFamilies - 1 ? "other keys" : $"key={family}",
        };
        var keyedParts = new List<string>(BroadcastTelemetry.KeyedFamilies);
        for (int family = 0; family < BroadcastTelemetry.KeyedFamilies; family++)
        {
            if (_broadcastBudgetKeyedBytes[family] == 0) continue;
            keyedParts.Add(string.Create(CultureInfo.InvariantCulture,
                $"key={family}:{KeyName(family)} bytes={_broadcastBudgetKeyedBytes[family]} msgs={_broadcastBudgetKeyedMessages[family]} " +
                $"pct={Pct(_broadcastBudgetKeyedBytes[family], keyedBytes):0.0}"));
        }
        lines.Add("[bb-keyed] " + (keyedParts.Count > 0 ? string.Join(" | ", keyedParts) : "bytes=0 msgs=0"));
    }

    /// <summary>P990 — copy the lane rings into the report scratch tables once per
    /// window (console + CSV read them), returning how many wire ids and how many
    /// ordered-lane bytes they hold.</summary>
    private (int Ids, long Bytes) FillLaneTables(BroadcastTelemetry telemetry, long second)
    {
        Array.Clear(_broadcastBudgetLaneBytes);
        Array.Clear(_broadcastBudgetLaneMessages);
        Array.Clear(_broadcastBudgetKeyedBytes);
        Array.Clear(_broadcastBudgetKeyedMessages);
        long laneTotal = 0;
        int ids = 0;
        for (int fmid = 0; fmid < BroadcastTelemetry.FmidSlots; fmid++)
        {
            long bytes = telemetry.OrderedLaneWindowBytes(fmid, second);
            if (bytes == 0) continue;
            _broadcastBudgetLaneBytes[fmid] = bytes;
            _broadcastBudgetLaneMessages[fmid] = telemetry.OrderedLaneWindowMessages(fmid, second);
            laneTotal += bytes;
            ids++;
        }
        for (int family = 0; family < BroadcastTelemetry.KeyedFamilies; family++)
        {
            _broadcastBudgetKeyedBytes[family] = telemetry.KeyedLaneWindowBytes(family, second);
            _broadcastBudgetKeyedMessages[family] = telemetry.KeyedLaneWindowMessages(family, second);
        }
        return (ids, laneTotal);
    }

    /// <summary>
    /// P983 — the update-age table plus P990's reason split. <c>[bb-age]</c> keeps
    /// P983's exact shape (visible pairs, stamped pairs, never-posed pairs and the
    /// binned percentiles); the two <c>[bb-age-why]</c> lines classify the pairs
    /// older than 3 s and the never-posed pairs, so a stale maximum can be read as
    /// subject state / viewer state / real starvation instead of a mystery.
    /// </summary>
    private void AddAgeReport(List<string> lines, BroadcastTelemetry telemetry, long second,
        Dictionary<short, Ship> subjects)
    {
        var window = _broadcastBudgetAge;
        window.Clear();
        Array.Clear(_broadcastBudgetWhy);
        Array.Clear(_broadcastBudgetWhyNever);
        var peers = telemetry.SnapshotPeers();
        long counted = 0, never = 0;
        long older3s = 0, neverPosed = 0;
        long visiblePairs = _viewerPoseCadence.Values.Sum(state => (long)state.TrackedCount);
        double now = telemetry.SimNowMs;
        foreach (var (id, peer) in peers)
        {
            _viewerPoseCadence.TryGetValue(id, out var cadence);
            _peerShips.TryGetValue(id, out var lived);
            var viewer = ViewerState(peer, now);
            long peerCounted = 0, peerNever = 0;
            // Walk the VIEWER's tracked set (one pose-table probe per pair) rather
            // than its stamps filtered by a cadence lookup: the same pairs, half the
            // cache misses, and the missing ones are enumerated for free for the
            // never-posed classification.
            if (cadence is null)
            {
                foreach (var stamp in peer.LastPoses) peerCounted += Tally(stamp.ShipId, stamp);
            }
            else
            {
                foreach (short shipId in cadence.TrackedShipIds())
                {
                    if (peer.LastPoses.TryGet(shipId, out var stamp)) peerCounted += Tally(shipId, stamp);
                    else
                    {
                        peerNever++;
                        neverPosed++;
                        subjects.TryGetValue(shipId, out var subject);
                        _broadcastBudgetWhyNever[(int)ClassifyNeverPosed(SubjectState(subject, lived, default), viewer)]++;
                    }
                }
            }
            counted += peerCounted;
            never += peerNever;

            long Tally(short shipId, in PoseStamp stamp)
            {
                double age = Math.Max(0, now - stamp.SimMs) / 1000d;
                window.RecordBand(0, age);
                if (age <= 3) return 1;
                older3s++;
                subjects.TryGetValue(shipId, out var subject);
                _broadcastBudgetWhy[(int)ClassifyStale(SubjectState(subject, lived, stamp), viewer)]++;
                return 1;
            }
        }
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"[bb-age] visiblePairs={visiblePairs} ships={counted} neverPosed={never} {FormatAge(window)} " +
            $"older1s={window.Older1} older3s={window.Older3} simMs={telemetry.SimNowMs:0}"));
        lines.Add("[bb-age-why] older3s=" + older3s + " " + WhyLine(_broadcastBudgetWhy));
        lines.Add("[bb-age-why] neverPosed=" + neverPosed + " " + WhyLine(_broadcastBudgetWhyNever));
    }

    private static string WhyLine(long[] counts)
    {
        var parts = new string[counts.Length];
        for (int i = 0; i < counts.Length; i++) parts[i] = $"{StaleReasonNames.All[i]}={counts[i]}";
        return string.Join(" ", parts);
    }

    private static AgeViewer ViewerState(BroadcastPeerTelemetry peer, double now)
        => peer.Sweeps == 0
            ? new AgeViewer(SweptRecently: false, NewViewer: true)
            : new AgeViewer(SweptRecently: now - peer.LastSweepMs <= 3000d, NewViewer: now - peer.FirstSweepMs <= 3000d);

    private static AgeSubject SubjectState(Ship? subject, LiveShip? viewer, in PoseStamp stamp)
    {
        if (subject is null) return new AgeSubject(Known: false, false, false, false, false, false, false);
        bool lifepod = subject.HullType.HasCapability(DataHullTypeIGC.HabmLifepod);
        bool sameSector = viewer is { } live && ReferenceEquals(subject.Sector, live.Sector);
        bool stationary = stamp.HasPosition
            && (subject.Position - stamp.Position).LengthSquared() <= 0.01f
            && subject.Velocity.LengthSquared() <= 1f;
        return new AgeSubject(Known: true, Docked: subject.StationId is not null, Dead: subject.Dead,
            Lifepod: lifepod, Parented: subject.ParentShip is not null, SameSector: sameSector,
            Stationary: stationary);
    }

    /// <summary>P990 — the pure classification of a pair whose pose is older than
    /// the 3 s window (see <see cref="StaleReason"/> for the native rule behind
    /// each bucket).</summary>
    internal static StaleReason ClassifyStale(in AgeSubject subject, in AgeViewer viewer)
    {
        if (!subject.Known) return StaleReason.SubjectUnknown;
        if (subject.Docked) return StaleReason.Docked;
        if (subject.Dead) return StaleReason.Dead;
        if (subject.Lifepod) return StaleReason.Lifepod;
        if (subject.Parented) return StaleReason.Parented;
        if (!viewer.SweptRecently) return StaleReason.ViewerNotSwept;
        if (!subject.SameSector) return StaleReason.OtherSector;
        if (subject.Stationary) return StaleReason.Stationary;
        return StaleReason.Starved;
    }

    /// <summary>P990 — same decision for a tracked pair no pose was ever queued
    /// for. A parked subject is only "legitimately not re-sent" when the peer
    /// already holds a record of it: a pair that was never posed at all is either
    /// a viewer whose rotation has not come around yet or a real gap.</summary>
    internal static StaleReason ClassifyNeverPosed(in AgeSubject subject, in AgeViewer viewer)
    {
        var reason = ClassifyStale(subject, viewer);
        if (reason is not (StaleReason.Starved or StaleReason.Stationary)) return reason;
        return viewer.NewViewer ? StaleReason.NewPair : StaleReason.Unserved;
    }

    /// <summary>One live ship per id across every mission, for the age
    /// classification. Report cadence only (one dictionary per window).</summary>
    private Dictionary<short, Ship> BroadcastSubjectLookup()
    {
        var map = new Dictionary<short, Ship>(Math.Max(16, _peerShips.Count * 4));
        foreach (var mission in _missions)
            foreach (var ship in mission.GetShips())
                map[ship.Id] = ship;
        return map;
    }

    private static double Pct(long value, long total) => total > 0 ? 100d * value / total : 0;

    private static string FormatAge(PoseAgeWindow window)
    {
        var (count, p50, p95, max) = window.Band(0);
        return string.Create(CultureInfo.InvariantCulture,
            $"samples={count} p50={p50 * 1000:0}ms p95={p95 * 1000:0}ms max={max * 1000:0}ms");
    }

    /// <summary>P990 — wire fmid → the port's message type, so the
    /// <c>[bb-other]</c> row reads as "SHIP_STATUS" instead of "90". Covers every
    /// <c>Fmid</c> constant declared in <c>src/Allegiance.Core/Wire</c> (1..996);
    /// anything else prints as its number.</summary>
    internal static string MessageName(int fmid) => fmid switch
    {
            1 => "LOGONREQUEST",
            2 => "LOGONACK",
            3 => "LOGOFF",
            4 => "BUYLOADOUTREQUEST",
            5 => "BUYLOADOUTACK",
            6 => "OBJECTSPOTTED",
            7 => "SETCLUSTER",
            8 => "DEVCOMPLETED",
            9 => "SETBRIEFINGTEXT",
            10 => "PLAYERINFO",
            11 => "STATICMAPINFO",
            12 => "PROJECTILEINSTANCE",
            13 => "RANKINFO",
            14 => "SETSTARTTIME",
            15 => "SETTEAMINFO",
            16 => "PLAYERRESCUED",
            17 => "BALLOT",
            18 => "CANCELBALLOT",
            19 => "VOTE",
            20 => "SHIPDELETE",
            21 => "RANDOMIZETEAMS",
            23 => "CHATMESSAGE",
            24 => "SHIPUPDATE",
            26 => "EXPORTASTEROID/EXPORTBUILDINGEFFECT/EXPORTCLUSTER/EXPORTMINE/EXPORTPROBE/EXPORTSHIP/EXPORTSTATION/EXPORTWARP",
            28 => "TREASURESPAWN",
            30 => "DESTROYTREASURE",
            31 => "NEWSHIPINSTANCE",
            33 => "DROPPART",
            34 => "SWAPPART",
            35 => "PING",
            37 => "MISSIONSTAGE",
            38 => "MISSIONDEF",
            39 => "POSITIONREQ",
            40 => "POSITIONREQBROADCAST",
            41 => "POSITIONACK",
            42 => "DELPOSITIONREQ",
            43 => "LOCKLOBBY",
            44 => "LOCKSIDES",
            45 => "PLAYERREADY",
            46 => "TEAMREADY",
            47 => "FORCETEAMREADY",
            48 => "DOCKED",
            51 => "CHANGETEAMCIV",
            52 => "AUTOACCEPT",
            57 => "STATIONSUPDATE",
            58 => "STATIONCAPTURE",
            59 => "STATIONDESTROYED",
            60 => "MONEYCHANGE",
            61 => "DOCKEDNOTIFY",
            62 => "GAMEOVER",
            63 => "GAMEOVERPLAYERS",
            64 => "BUCKETDONATE",
            65 => "BUCKETSTATUS",
            67 => "PLAYERDONATE",
            68 => "SIDEINACTIVE",
            70 => "FIREMISSILE",
            75 => "SIDETECHCHANGE",
            76 => "SIDEATTRIBUTECHANGE",
            77 => "EJECT",
            78 => "SINGLESHIPUPDATE",
            80 => "SUICIDE",
            82 => "FIREEXPENDABLEREQUEST",
            83 => "MISSILEDESTROYED",
            84 => "MINEDESTROYED",
            85 => "PROBEDESTROYED",
            86 => "ASTEROIDDESTROYED",
            87 => "FIREEXPENDABLE",
            89 => "TREASURESETS",
            90 => "SHIPSTATUS",
            92 => "PROBESUPDATE",
            93 => "ORDERCHANGE",
            94 => "LEAVESHIP",
            95 => "JOINEDMISSION",
            96 => "LOADOUTCHANGE",
            99 => "ACTIVETURRETUPDATE",
            100 => "INACTIVETURRETUPDATE",
            102 => "TELEPORTACK",
            103 => "BOARDSHIP",
            104 => "VIEWCLUSTERREQUEST",
            105 => "KILLSHIP",
            106 => "SETWINGID",
            108 => "CHATBUOY",
            109 => "CREATECHAFF",
            110 => "MISSILESPOOFED",
            111 => "ENDSPOOFING",
            112 => "AUTODONATE",
            113 => "MISSIONPARAMSMSG",
            114 => "PAYDAY",
            115 => "SETMONEY",
            116 => "AUTODONATEUPDATE",
            121 => "SETTEAMLEADER",
            123 => "SETMISSIONOWNER",
            124 => "QUITMISSION",
            125 => "JOINSIDE",
            126 => "QUITSIDE",
            128 => "ENTERGAME",
            129 => "RELOAD",
            155 => "GAMESTATE",
            156 => "GAINFLAG",
            157 => "STARTGAME",
            162 => "ACQUIRETREASURE",
            163 => "TREASUREACK",
            164 => "ADDPART",
            166 => "ENTERLIFEPOD",
            175 => "LIGHTSHIPSUPDATE",
            176 => "HEAVYSHIPSUPDATE",
            177 => "VIEWCLUSTERREPLY",
            178 => "BOARDNACK",
            179 => "ASTEROIDSUPDATE",
            181 => "BUILDINGEFFECTDESTROYED",
            182 => "REQUESTMONEY",
            183 => "SHIPRESET",
            184 => "RIPCORDREQUEST",
            185 => "RIPCORDACTIVATE",
            186 => "RIPCORDDENIED",
            187 => "RIPCORDABORTED",
            188 => "WARPBOMB",
            189 => "PROMOTE",
            191 => "CREATEBUCKETS",
            192 => "RELAUNCHSHIP",
            194 => "PINGDATA",
            195 => "REQPINGDATA",
            196 => "CHANGEALLIANCE",
            197 => "CHANGEALLIANCES",
            198 => "ASTEROIDMINED",
            199 => "HIGHLIGHTCLUSTER",
            200 => "CLUSTERINFO",
            204 => "MISSILELOCKUPDATE",
            205 => "MISSILEUPDATE",
            206 => "OWNSHIPMININGUPDATE",
            207 => "EXPLOSIONEVENT",
            208 => "MISSILETRACKWARNINGUPDATE",
            211 => "SIDEROSTER",
            213 => "PROJECTILEHULLHIT",
            214 => "CLUSTERWARNINGUPDATE",
            215 => "QUEUEDCOMMANDUPDATE",
            216 => "COLLISIONEVENT",
            217 => "VICTIMDAMAGED",
            218 => "SHIPWARNINGUPDATE",
            220 => "OWNSHIPCLOAKUPDATE",
            221 => "GUNFIREBATCH",
            853 => "MINESTRENGTH",
            991 => "TIMESCALEUPDATE",
            992 => "RESEARCHBRIEFING",
            993 => "RESEARCHPRODUCTION",
            994 => "SHOWCASEFINALEREQUEST",
            995 => "LASTBASENOTICE",
            996 => "MISSIONBRIEFINGINFO",
        _ => $"fmid{fmid}",
    };

    /// <summary>P983 — the same numbers on the profiler's CSV path when one is set
    /// (distinct <c>bb-*</c> row kinds; the row body is key=value, documented in
    /// docs/reports/BROADCAST_TELEMETRY_P983.md and
    /// docs/reports/BROADCAST_SLICE_D_P990.md).</summary>
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
                $"other={peer.WindowBytes(BroadcastCategory.Other, second)},ships={peer.LastPoses.Count}"));
        }
        // P990 — the ordered lane per wire id and the keyed lane per family, so a
        // rig run can be sliced per message kind without the console block.
        FillLaneTables(telemetry, second);
        for (int fmid = 0; fmid < BroadcastTelemetry.FmidSlots; fmid++)
        {
            if (_broadcastBudgetLaneBytes[fmid] == 0) continue;
            profiler.WriteCsvRow(string.Create(CultureInfo.InvariantCulture,
                $"{unix},{windowSeconds:0.###},bb-other,fmid={fmid},name={MessageName(fmid)}," +
                $"bytes={_broadcastBudgetLaneBytes[fmid]},msgs={_broadcastBudgetLaneMessages[fmid]}"));
        }
        for (int family = 0; family < BroadcastTelemetry.KeyedFamilies; family++)
        {
            if (_broadcastBudgetKeyedBytes[family] == 0) continue;
            profiler.WriteCsvRow(string.Create(CultureInfo.InvariantCulture,
                $"{unix},{windowSeconds:0.###},bb-keyed,key={family}," +
                $"bytes={_broadcastBudgetKeyedBytes[family]},msgs={_broadcastBudgetKeyedMessages[family]}"));
        }
    }
}
