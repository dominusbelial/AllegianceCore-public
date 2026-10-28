using Allegiance.Core.Identity;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

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

    /// <summary>P802 test seam — the live score object of a seated peer ship
    /// (null for an unseated/NPC id), so the native <c>SetSideCiv</c> score reset
    /// can be asserted without staging a pre-start kill.</summary>
    internal PlayerScore? TestPlayerScore(short shipId) =>
        _peerShips.Values.FirstOrDefault(live => live.Ship.Id == shipId)?.PlayerScore;

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
        _playerInfoSent.Clear();
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
        // P822 — the one-shot turret relay latches and their per-pass gathers.
        lock (_snapshotCaptureGate) _pendingTurretRelays.Clear();
        _activeTurretCandidates.Clear();
        _inactiveTurretRelays.Clear();
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
            // P369 — the cross-side EXPORT sweep (P775: no PLAYERINFO seeding
            // here anymore — PLAYERINFO goes only at the native moments).
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

    /// <summary>Encode/send failures the broadcast publisher contained (0 when
    /// the snapshot pipeline is off).</summary>
    internal long BroadcastPublishFailures => _broadcastPublisher is { } p ? Interlocked.Read(ref p.EncodeFailures) : 0;

    // Replays the complete broadcast suffix against a recorded tick's world.
    // No Mission.Update or socket timing changes the supplied inputs.
    internal void TestProjectRecordedTick(uint tick, bool snapshot, bool measure = false, bool discovery = false)
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
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:crossSideExportSweep")) SweepCrossSideExports();
            TestProjectionAllocationCapture?.Invoke("SweepCrossSideExports", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            SweepCrossSideMineExports();
            TestProjectionAllocationCapture?.Invoke("SweepCrossSideMineExports", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
            allocationMark = GC.GetAllocatedBytesForCurrentThread();
            if (discovery)
                using (Allegiance.Core.Profiling.TickProfiler.Enter("step:crossSideDiscoverySweep")) SweepCrossSideDiscoveryExports();
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:crossSideShipStatusSweep")) SweepCrossSideShipStatuses();
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
            using (Allegiance.Core.Profiling.TickProfiler.Enter("step:broadcastMissileTrackWarning")) BroadcastMissileTrackWarning();
            TestProjectionAllocationCapture?.Invoke("step:broadcastMissileTrackWarning", GC.GetAllocatedBytesForCurrentThread() - allocationMark);
        }
        finally
        {
            CompleteBroadcastSnapshot();
            if (measure) Allegiance.Core.Profiling.TickProfiler.EndTick();
        }
    }

    internal void TestInvalidateClashLatches(ConnectionId conn, short shipId)
    {
        _crossSideExportSent.RemoveWhere(e => e.Recipient == conn || e.ShipId == shipId);
        _crossSideDiscoverySent.RemoveWhere(e => e.Recipient == conn);
        foreach (var key in _crossSideShipStatusTick.Keys.Where(k => k.Item1 == conn || k.Item2 == shipId).ToArray())
            _crossSideShipStatusTick.Remove(key);
        _statusSweepRemovalVersion++;
    }

    internal void TestResetSnapshotReplay()
    {
        FlushBroadcastSnapshots();
        TestResetBroadcastState();
        _crossSideExportSent.Clear();
        _crossSideMineExportSent.Clear();
        _crossSideDiscoverySent.Clear();
        _buildingEffectExportSent.Clear();
        _crossSideShipStatusSeen.Clear();
        _crossSideShipStatusTick.Clear();
        _statusSweepRemovalVersion++;
        _shipDetected.Clear();
        _shipDetectedChanged.Clear();
        _lastSectorAssetMaskBySide.Clear();
        _objectSyncDirtySides.Clear();
    }

    /// <summary>P820c test seam — seed (or overwrite) a peer's verified
    /// <see cref="JoinIdentity"/> (the join-token carrier), so the squad helpers
    /// (<c>PeerSquads</c>/<c>PeerCanLeadSquad</c>/<c>PeerPreferredSquad</c>) have
    /// squads to read in a loopback test. Inert in production (unused).</summary>
    internal void TestSetIdentity(ConnectionId conn, JoinIdentity? identity)
    {
        if (identity is null) _peerIdentities.Remove(conn);
        else _peerIdentities[conn] = identity;
    }

    /// <summary>P820c test seam — run <see cref="EstablishSideSquadFromLeader"/>
    /// directly (native AddPlayerToSide's team-leader squad rule, fsmission.cpp:
    /// 661-690) so a loopback test can exercise the squad establishment + boot
    /// (QUIT_SIDE SquadChange) without staging a full first-seat join.</summary>
    internal void TestEstablishSideSquad(Mission mission, Side side) => EstablishSideSquadFromLeader(mission, side);

    /// <summary>P820c test seam — run <see cref="HandleLeaderSuccession"/> directly
    /// (native RemovePlayerFromSide's leader-change block, fsmission.cpp:896-1000)
    /// so a loopback test can assert that a mid-game succession KEEPS the side's
    /// squad and prefers a squad-capable successor (fix 2).</summary>
    internal void TestHandleLeaderSuccession(Ship ship) => HandleLeaderSuccession(ship);

    /// <summary>P854 test seam — the native per-side invitation gate
    /// (fsmission.cpp:4605): the refusal reason <see cref="CheckPositionRequest"/>
    /// produces for a seated ship on a side, or <c>null</c> when the seat is allowed.
    /// Lets a loopback test assert DPR_PrivateGame for an uninvited side without
    /// staging a live position request.</summary>
    internal DelPositionReqReason? TestCheckPositionRequest(Mission mission, short sideId, Ship ship)
        => CheckPositionRequest(mission, sideId, ship);

    /// <summary>P854 test seam — the native mission-join gate (FedSrv.CPP:581-594):
    /// whether the peer (connection + callsign) is admitted to a
    /// <see cref="Mission.RequiresInvitation"/> mission at entry (i.e. invited to at
    /// least one side). Inert in production (unused).</summary>
    internal bool TestAdmittedAtMissionJoin(ConnectionId conn, string name, Mission mission)
        => !(mission.RequiresInvitation && !mission.IsInvited(name, PlayerInvitationSquads(conn)));

    /// <summary>P853 test seam — feed one raw datagram through the real inbound
    /// dispatcher (<see cref="ProcessDatagram"/>) so packet-level fixtures can
    /// drive FM_CS_MISSIONPARAMS / FM_CS_SET_TEAM_INFO and their guards exactly
    /// as the transport would. Inert in production.</summary>
    internal void TestProcessDatagram(ConnectionId from, byte[] data) => ProcessDatagram(from, data);

    /// <summary>P853 test seam — the mission's current params snapshot
    /// (native <c>m_misdef.misparms</c> stand-in), seeded on first call. Lets a
    /// fixture assert that a refused edit left the snapshot untouched. Inert in
    /// production.</summary>
    internal MissionParamsMsg TestParamsOf(Mission mission) => ParamsOf(mission);
}
