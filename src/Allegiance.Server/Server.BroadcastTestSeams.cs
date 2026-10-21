using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>
/// P603 — internal test seams (reachable via InternalsVisibleTo) that let the
/// byte-identity harness drive <c>BroadcastShipStates</c> / <c>BroadcastShipsUpdate</c>
/// directly against a deterministically-fabricated world, in both broadcast
/// modes, capturing the exact per-peer outbound payload stream. This isolates
/// the broadcast logic from the live sim / real-client timing so the
/// serial-vs-parallel byte equality can be asserted precisely. All seams are
/// inert in production (null / no-ops when unused).
/// </summary>
public sealed partial class Server
{
    /// <summary>P603 test seam — the first mission (null if no games booted).</summary>
    internal Mission? TestMission => _missions.Count > 0 ? _missions[0] : null;

    /// <summary>P603 test seam — the first mission's sectors.</summary>
    internal IReadOnlyList<Sector> TestSectors =>
        _missions.Count > 0 ? _missions[0].Sectors : Array.Empty<Sector>();

    /// <summary>
    /// P603 test seam — reset every piece of shared mutable broadcast state to a
    /// pristine start: peer map, names, all latches, the loadout cache, the
    /// cluster-info payload, and (crucially) every mission ship's <c>Deviation</c>
    /// staleness accumulator (which <c>BroadcastShipsUpdate</c> mutates across
    /// peers in a sector). Calling this between a serial and a parallel capture
    /// makes both runs start from byte-identical state.
    /// </summary>
    internal void TestResetBroadcastState()
    {
        _peerShips.Clear();
        _peerNames.Clear();
        _lastStatusSector.Clear();
        _lastStatusTick.Clear();
        _lastClusterInfoTick.Clear();
        _teammateExportSent.Clear();
        _stationIdentitySent.Clear();
        _teammatePlayerInfoSent.Clear();
        _missionPlayerInfoSent.Clear();
        _teammatePositionSent.Clear();
        _teammateLoadoutVersionSent.Clear();
        _lastTeammateStatusTick.Clear();
        _nextTeammateRefreshTick.Clear();
        _sameSideNpcExportSent.Clear();
        _lastSameSideNpcStatusTick.Clear();
        // HUD-17 — the own-side Detected-bit baseline and its per-tick change set.
        _shipDetected.Clear();
        _shipDetectedChanged.Clear();
        _teammateLoadoutCache.Clear();
        RetireBroadcastProjectionCaches();
        _viewerPoseCadence.Clear();
        _emissionViewers.Clear();
        _missileViewers.Clear();
        _probeViewers.Clear();
        _retiredPoseAges.Clear();
        _retiredTierAges.Clear();
        _clusterInfoBytes = null;
        foreach (var m in _missions)
            foreach (var s in m.Sectors)
                foreach (var mdl in s.Models)
                    if (mdl is Ship sh) sh.Deviation = 0f;
    }

    /// <summary>
    /// P603 test seam — seed <c>_peerShips</c> (and names) for the given peers,
    /// each an already-placed <see cref="Ship"/> with its sector. Rebuilt fresh
    /// each call so both broadcast modes start from identical per-peer state.
    /// </summary>
    internal void TestSeedPeers(IReadOnlyList<(ConnectionId Conn, Ship Ship, Sector Sector)> peers)
    {
        _peerShips.Clear();
        _peerNames.Clear();
        foreach (var (conn, ship, sector) in peers)
        {
            _peerShips[conn] = new LiveShip(ship, sector, ship.Position,
                ship.Orientation.GetForward(), ship.Orientation.GetUp(),
                new PlayerScore(), new PlayerWallet(), ship.ShieldFraction,
                false, ConnectSimTime: 0);
            _peerNames[conn] = "seed-" + conn.Value;
        }
    }

    /// <summary>P603 test seam — set the shared cluster-info payload (exercises the
    /// periodic CLUSTERINFO resend path when non-null).</summary>
    internal void TestSetClusterInfoBytes(byte[]? bytes) => _clusterInfoBytes = bytes;

    /// <summary>
    /// P608 — test seam like <see cref="TestCollectBroadcast"/> but records each
    /// coalesced frame's coalesce key AND guaranteed flag alongside its payload
    /// (via <c>_testSendCoalescingCapture</c>), so a test can verify the
    /// coalesce-key CONTRACT (e.g. the owner's own-ship SingleShipUpdate uses a
    /// distinct key from the teammate position batch, and is guaranteed)
    /// deterministically, without live-client timing. Ordinary (key 0) frames are
    /// excluded — they are not coalescable.
    /// </summary>
    internal Dictionary<ConnectionId, List<(byte[] Pkt, int Key, bool Guaranteed)>> TestCollectBroadcastKeys(
        uint tickIndex, bool parallel, string step = "both")
    {
        _broadcastParallelBuild = parallel;
        var outp = new Dictionary<ConnectionId, List<(byte[], int, bool)>>();
        var gate = new object();
        _testSendCoalescingCapture = (conn, pkt, key, guaranteed) =>
        {
            if (key == 0) return;
            lock (gate)
            {
                if (!outp.TryGetValue(conn, out var l)) { l = new(); outp[conn] = l; }
                l.Add((pkt, key, guaranteed));
            }
        };
        try
        {
            if (step is "shipStates") BroadcastShipStates(tickIndex);
            else if (step is "shipsUpdate") BroadcastShipsUpdate(tickIndex);
            else { BroadcastShipStates(tickIndex); BroadcastShipsUpdate(tickIndex); }
        }
        finally
        {
            _testSendCoalescingCapture = null;
        }
        return outp;
    }

    /// <summary>P604 test seam — set the ship-update model (true = native:
    /// suppress the per-tick teammate posBatch stream; false = port, the
    /// default). Mirrors <c>Server._nativeShipUpdates</c>.</summary>
    internal void TestSetShipUpdateModel(bool native)
    {
        _nativeShipUpdates = native;
        _shipUpdateModel = native ? "native" : "port";
    }

    /// <summary>
    /// P603 test seam — run the named broadcast step(s) in the given mode
    /// (serial / parallel) against the CURRENT seeded state, collecting each
    /// recipient's outbound packets in order. <paramref name="step"/> is
    /// <c>"shipStates"</c>, <c>"shipsUpdate"</c>, or <c>"both"</c> (in
    /// SimPass order: shipStates then shipsUpdate). Clears the capture on exit.
    /// </summary>
    internal Dictionary<ConnectionId, List<byte[]>> TestCollectBroadcast(
        uint tickIndex, bool parallel, string step = "both")
    {
        _broadcastParallelBuild = parallel;
        var outp = new Dictionary<ConnectionId, List<byte[]>>();
        // The capture callback can fire from pool-worker threads (BroadcastShipsUpdate
        // sends directly per sector in parallel mode), so guard the collector — a
        // plain Dictionary mutated from several workers would lose keys/entries.
        var gate = new object();
        _testSendCapture = (conn, pkt) =>
        {
            lock (gate)
            {
                if (!outp.TryGetValue(conn, out var l)) { l = new(); outp[conn] = l; }
                l.Add(pkt);
            }
        };
        try
        {
            if (step is "shipStates") BroadcastShipStates(tickIndex);
            else if (step is "shipsUpdate") BroadcastShipsUpdate(tickIndex);
            // HUD-20 — the cross-side EXPORT sweep, which now also seeds each new
            // pair's PLAYERINFO (SeedMissionPlayerInfo).
            else if (step is "crossSideExports") SweepCrossSideExports();
            else { BroadcastShipStates(tickIndex); BroadcastShipsUpdate(tickIndex); }
        }
        finally
        {
            _testSendCapture = null;
        }
        return outp;
    }
}

public sealed partial class Server
{
    internal Action<ConnectionId, byte[], int, bool>? TestPublicationCapture;
    internal Action<ConnectionId, int, int, bool>? TestPublicationSizeCapture;
    internal Action<string, long>? TestProjectionAllocationCapture;
    internal List<(ConnectionId Conn, Ship Ship, Sector Sector)> TestSnapshotPeers()
        => _peerShips.Select(p => (p.Key, p.Value.Ship, p.Value.Sector)).ToList();
    internal void TestSendSnapshotBarrier(uint marker)
    {
        var packet = Allegiance.Core.Wire.FedPacket.Pack(new Allegiance.Core.Wire.Ping { TimeServer = marker }.Encode());
        foreach (var peer in _peerShips.Keys) SendPeerCoalescing(peer, packet, int.MaxValue, guaranteed: true);
    }

    // Replays the complete broadcast suffix against a recorded tick's world.
    // No Mission.Update or socket timing changes the supplied inputs.
    internal void TestProjectRecordedTick(uint tick, bool snapshot, bool measure = false)
    {
        _broadcastSnapshotEnabled = snapshot;
        lock (_statsLock) _loopIterations = tick;
        if (measure) Allegiance.Core.Profiling.TickProfiler.EnterTick();
        BeginBroadcastSnapshot();
        if (measure) StartBroadcastProjectionMeasurement();
        try
        {
            long allocationMark;
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            SweepCrossSideExports();
            TestProjectionAllocationCapture?.Invoke("SweepCrossSideExports", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            SweepCrossSideMineExports();
            TestProjectionAllocationCapture?.Invoke("SweepCrossSideMineExports", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            SweepCrossSideShipStatuses();
            TestProjectionAllocationCapture?.Invoke("SweepCrossSideShipStatuses", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            SweepShipDetection();
            TestProjectionAllocationCapture?.Invoke("shipDetection", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastShipStates")) BroadcastShipStates(tick);
            TestProjectionAllocationCapture?.Invoke("step:broadcastShipStates", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastStationsUpdate")) BroadcastStationsUpdate();
            TestProjectionAllocationCapture?.Invoke("step:broadcastStationsUpdate", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastAsteroidsUpdate")) BroadcastAsteroidsUpdate();
            TestProjectionAllocationCapture?.Invoke("step:broadcastAsteroidsUpdate", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastProbesUpdate")) BroadcastProbesUpdate();
            TestProjectionAllocationCapture?.Invoke("step:broadcastProbesUpdate", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            _objectSyncDirtySides.Clear();
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastShipsUpdate")) BroadcastShipsUpdate(tick);
            TestProjectionAllocationCapture?.Invoke("step:broadcastShipsUpdate", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastSectorThreats")) BroadcastSectorThreats();
            TestProjectionAllocationCapture?.Invoke("step:broadcastSectorThreats", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastShipWarnings")) BroadcastShipWarnings();
            TestProjectionAllocationCapture?.Invoke("step:broadcastShipWarnings", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastMissileLock")) BroadcastMissileLock();
            TestProjectionAllocationCapture?.Invoke("step:broadcastMissileLock", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastMissilesUpdate")) BroadcastMissilesUpdate();
            TestProjectionAllocationCapture?.Invoke("step:broadcastMissilesUpdate", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastOwnShipMining")) BroadcastOwnShipMining();
            TestProjectionAllocationCapture?.Invoke("step:broadcastOwnShipMining", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastOwnShipKillBonus")) BroadcastOwnShipKillBonus();
            TestProjectionAllocationCapture?.Invoke("step:broadcastOwnShipKillBonus", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastMissileTrackWarning")) BroadcastMissileTrackWarning();
            TestProjectionAllocationCapture?.Invoke("step:broadcastMissileTrackWarning", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastScoreRoster")) BroadcastScoreRoster();
            TestProjectionAllocationCapture?.Invoke("step:broadcastScoreRoster", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastLeadIndicator")) BroadcastLeadIndicator();
            TestProjectionAllocationCapture?.Invoke("step:broadcastLeadIndicator", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
        }
        finally
        {
            CompleteBroadcastSnapshot();
            if (measure) Allegiance.Core.Profiling.TickProfiler.EndTick();
        }
    }

    internal void TestResetSnapshotReplay()
    {
        FlushBroadcastSnapshots();
        TestResetBroadcastState();
        _crossSideExportSent.Clear();
        _crossSideMineExportSent.Clear();
        _crossSideShipStatusSeen.Clear();
        _crossSideShipStatusTick.Clear();
        _shipDetected.Clear();
        _shipDetectedChanged.Clear();
        _lastSectorAssetMaskBySide.Clear();
        _scoreRosterStates.Clear();
        _objectSyncDirtySides.Clear();
    }
}
