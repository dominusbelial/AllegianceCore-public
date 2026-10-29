using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Collision;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Perf")]
public sealed class BroadcastSnapshotLoopbackTests
{
    // Async raw receivers avoid adding one client send thread per player to
    // the server measurement. All players greet, log on, seat, and drain real TCP.
    private sealed class Peer : IDisposable
    {
        private readonly TcpClient _client = new() { NoDelay = true };
        private readonly Task _reader;
        private readonly object _writeGate = new();
        private readonly Timer? _heartbeat;
        internal long Frames, Bytes;
        internal volatile int OwnId = -1;
        internal Exception? Error;
        internal volatile uint LastMarker;
        private readonly object _recordingGate = new();
        private List<byte[]>? _recording;
        internal void StartRecording() { lock (_recordingGate) _recording = new(); }
        internal byte[][] StopRecording()
        {
            lock (_recordingGate) { var packets = _recording!.ToArray(); _recording = null; return packets; }
        }
        private volatile bool _closing;

        internal Peer(ushort port, int index, bool keepAlive = false)
        {
            _client.Connect("127.0.0.1", port);
            var stream = _client.GetStream();
            string name = $"p668_{index}";
            Write(stream, 1, Encoding.UTF8.GetBytes(name));
            Write(stream, 2, FedPacket.Pack(new LogonRequest
            {
                CharacterName = name, ZoneTicket = [1], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
                FedsrvVer = LogonRequest.MsgVer, Time = 668, DwCookie = uint.MaxValue,
                CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
                SteamID = (ulong)(99000000000000668 + index), DrmHash = "P668",
            }.Encode()));
            _reader = Read(stream);
            if (keepAlive)
                _heartbeat = new Timer(_ =>
                {
                    if (_closing) return;
                    try { KeepAlive(); }
                    catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
                    { if (!_closing) Error = e; }
                }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }

        // Raw benchmark peers otherwise send nothing after joining. Keep the
        // existing 30 s inbound-frame deadline alive during long loaded runs.
        internal void KeepAlive() => Write(_client.GetStream(), 2,
            FedPacket.Pack(new Ping { TimeClient = unchecked((uint)Environment.TickCount64) }.Encode()));

        private void Write(NetworkStream stream, byte kind, byte[] bytes)
        {
            byte[] header = new byte[5];
            header[0] = kind;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), bytes.Length);
            lock (_writeGate)
            {
                stream.Write(header);
                stream.Write(bytes);
            }
        }

        private async Task Read(NetworkStream stream)
        {
            byte[] header = new byte[5];
            byte[] body = new byte[65536];
            try
            {
                while (true)
                {
                    await stream.ReadExactlyAsync(header);
                    int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
                    if (header[0] != 2 || length < 4 || length > 16 * 1024 * 1024)
                        throw new InvalidDataException("Bad transport frame");
                    if (body.Length < length) body = new byte[length];
                    await stream.ReadExactlyAsync(body.AsMemory(0, length));
                    Interlocked.Increment(ref Frames);
                    Interlocked.Add(ref Bytes, length);
                    lock (_recordingGate) _recording?.Add(body.AsSpan(0, length).ToArray());
                    if (length == Ping.FixedSize + FedPacket.CrcWidth
                        && BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(2)) == Ping.Fmid)
                    {
                        var ping = Ping.Decode(body.AsSpan(0, length - FedPacket.CrcWidth));
                        // Heartbeat replies must not replace the ordered drain
                        // marker; TestSendSnapshotBarrier leaves TimeClient zero.
                        if (ping.TimeClient == 0) LastMarker = ping.TimeServer;
                    }
                    // Walk headers without per-packet decode allocations once seated.
                    if (OwnId >= 0) continue;
                    foreach (var message in FedPacket.Unpack(body.AsSpan(0, length)))
                    {
                        ushort id = BinaryPrimitives.ReadUInt16LittleEndian(message.AsSpan(2));
                        if (id == LogonAck.Fmid && LogonAck.Decode(message).FValidated)
                            Write(stream, 2, FedPacket.Pack(new PositionReq { SideId = -1 }.Encode()));
                        if (id == SetCluster.Fmid) OwnId = SetCluster.Decode(message).Ship.ShipId;
                    }
                }
            }
            catch (Exception e) when (_closing && e is IOException or ObjectDisposedException or SocketException) { }
            catch (Exception e) { Error = e; }
        }

        public void Dispose() { _closing = true; _heartbeat?.Dispose(); _client.Dispose(); _reader.GetAwaiter().GetResult(); }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Same_recorded_session_receives_identical_TCP_packets_in_each_lane()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config(2, native: false);
        config.Broadcast!.ImmutableSnapshot = true;
        config.Broadcast.MaxDegreeOfParallelism = 4;
        server.Boot(config);
        server.TestMission!.Stage = MissionStage.Started;
        var clients = new List<Peer>();
        try
        {
            for (int i = 0; i < 12; i++) clients.Add(new Peer(server.BoundPort, i));
            var join = Stopwatch.StartNew();
            while ((server.Ships.Count != clients.Count || clients.Any(c => c.OwnId < 0)) && join.Elapsed.TotalSeconds < 15)
                server.Tick();
            Assert.Equal(clients.Count, server.Ships.Count);
            Assert.All(clients, c => Assert.True(c.OwnId >= 0));
            var peers = server.TestSnapshotPeers();
            var sector = server.TestSectors[0];
            foreach (var peer in peers)
            {
                peer.Ship.Sector!.Remove(peer.Ship);
                server.TestMission.PlaceShip(peer.Ship, sector);
                peer.Ship.SideId = (short)(peer.Conn.Value % 2);
            }
            peers = peers.Select(p => (p.Conn, p.Ship, sector)).ToList();
            var subjects = new List<Ship>(peers.Select(p => p.Ship));
            for (int i = 0; i < 84; i++)
            {
                var ship = new Ship((short)(20000 + i), peers[0].Ship.HullType,
                    new(i * 30, 0, 0), Vector3.Zero, Orientation.Identity) { SideId = (short)(i % 2) };
                server.TestMission.PlaceShip(ship, sector);
                subjects.Add(ship);
            }
            var tape = Enumerable.Range(0, 30).Select(t => subjects.Select((_, i) =>
                new Vector3(i * 30 + t, i % 5 * 50, t * 3)).ToArray()).ToArray();
            void Barrier(uint marker)
            {
                server.FlushBroadcastSnapshots();
                server.TestSendSnapshotBarrier(marker);
                Assert.True(SpinWait.SpinUntil(() => clients.All(c => c.LastMarker == marker), TimeSpan.FromSeconds(10)),
                    $"TCP delivery barrier {marker} timed out");
            }
            Barrier(9999);
            byte[][][] Replay(bool snapshot, bool encodeOnce = false)
            {
                server.TestResetSnapshotReplay();
                server._broadcastEncodeOnceEnabled = encodeOnce;
                server.TestSeedPeers(peers);
                foreach (var client in clients) client.StartRecording();
                for (uint tick = 0; tick < tape.Length; tick++)
                {
                    for (int i = 0; i < subjects.Count; i++)
                    {
                        subjects[i].Position = tape[tick][i];
                        subjects[i].SideVisibility.SetSideVisibility(0, true);
                        subjects[i].SideVisibility.SetSideVisibility(1, true);
                    }
                    server.TestUpdateCadenceClock = () => tick / 20d;
                    server.TestProjectRecordedTick(tick, snapshot);
                    // Live sim may advance as soon as publication is submitted.
                    foreach (var ship in subjects) ship.Position = new(999999, 999999, 999999);
                    Barrier(10000 + tick);
                }
                return clients.Select(c => c.StopRecording()).ToArray();
            }
            var off = Replay(false);
            var on = Replay(true);
            var shared = Replay(true, encodeOnce: true);
            for (int i = 0; i < clients.Count; i++)
            {
                Assert.NotEmpty(off[i]);
                // P634 prioritizes ordered traffic over pending stream frames,
                // so cross-lane TCP interleaving depends on socket scheduling.
                // Compare exact receipt order WITHIN each pre-existing lane.
                static bool Stream(byte[] packet)
                {
                    ushort id = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
                    return id is SingleShipUpdate.Fmid or HeavyShipsUpdate.Fmid or LightShipsUpdate.Fmid
                        or StationsUpdate.Fmid or AsteroidsUpdate.Fmid or ProbesUpdate.Fmid
                        or MissileUpdate.Fmid or MissileLockUpdate.Fmid or MissileTrackWarningUpdate.Fmid
                        or OwnShipMiningUpdate.Fmid
                        or Ping.Fmid; // the test's full-queue barrier is in the stream lane
                }
                Assert.Equal(off[i].Where(Stream), on[i].Where(Stream));
                Assert.Equal(off[i].Where(p => !Stream(p)), on[i].Where(p => !Stream(p)));
                Assert.Equal(on[i].Where(Stream), shared[i].Where(Stream));
                Assert.Equal(on[i].Where(p => !Stream(p)), shared[i].Where(p => !Stream(p)));
                Assert.Null(clients[i].Error);
            }
            Assert.Equal(0, server.TestPublisher!.CoalescedSnapshots);
            Assert.Equal(0, server.TestPublisher.EncodeFailures);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "p669-tcp-identity.json"), JsonSerializer.Serialize(new
            {
                peers = clients.Count, subjects = subjects.Count, ticks = tape.Length,
                packets = off.Sum(p => p.Length), bytes = off.Sum(p => p.Sum(b => (long)b.Length)),
                identicalPerLane = true,
            }));
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    // P744 — machine-speed-sensitive measurement: the 256-peer sim/projection/
    // publish timing barometers here are wall-clock; scaled by PerfCalibration.
    [Trait("Category", "Perf")]
    public void Crowded_real_TCP_session_measures_sim_projection_and_publish_pool()
    {
        Require.Artwork();
        RunCrowded(Environment.GetEnvironmentVariable("P669_MEASURE") == "1", false);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Category", "Perf")] // P744: machine-speed-sensitive measurement
    public void Crowded_TCP_interest_tiers_reduce_bytes_and_report_ages()
    {
        Require.Artwork();
        RunCrowded(false, true);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    [Trait("Category", "Perf")] // P744: machine-speed-sensitive measurement
    public void Crowded_TCP_pipeline_cost_three_configurations()
    {
        Require.Artwork();
        RunCrowded(false, false, true);
    }

    private static void RunCrowded(bool phase2, bool phase3, bool phase4 = false)
    {
        // P744 — hardware-relative timing: the wall-clock join/drain deadlines
        // below were tuned on the reference workstation; scale them by the
        // machine speed factor measured in this same run so a slower Docker
        // runner no longer false-fails, while a transport pipeline regression
        // still trips them (SimulateRegressionFactor shrinks only the deadline,
        // modelling a pipeline N× slower than the scaled budget expects).
        double speed = PerfCalibration.CpuSpeedFactor;
        double regr = PerfCalibration.SimulateRegressionFactor;
        bool controlled = phase2 || phase3 || phase4;
        int count = int.TryParse(Environment.GetEnvironmentVariable("P668_PEERS"), out int requested) ? requested : 256;
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        int placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var hull = core.HullTypes.Values.Where(h => h.Length < 100).OrderByDescending(h => h.ScannerRange).First();
            // No weapons or AI; separated enough to avoid collision work masking
            // the broadcast-bound case. Two sides, all in one crowded sector.
            var ship = new Ship(id, hull, new Vector3((placed % 32) * 250,
                10000 + (placed / 32) * 250, 0), Vector3.Zero, Orientation.Identity)
                { SideId = (short)(placed++ % 2) };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        var basis = UpdateCadenceTests.Config(2, native: false);
        // This benchmark deliberately seats 128v128 by default. Native's
        // ordinary 100-player team default is too small for that workload.
        foreach (var game in basis.Games) game.MaxPlayersPerTeam = (count + 1) / 2;
        var config = new ServerConfig
        {
            ArtPath = basis.ArtPath, StaticCore = basis.StaticCore, Port = 0,
            UpdatesPerSecond = 20, Games = basis.Games,
            Broadcast = new() { ImmutableSnapshot = true, MinUpdateHz = 2, MaxDegreeOfParallelism = 4 },
            Transport = new() { Mode = "pool", PoolWorkers = 4, MaxConnections = count + 16, MaxConnectionsPerIp = count + 16 },
        };
        server.Boot(config);
        server.TestMission!.Stage = MissionStage.Started;
        var peers = new List<Peer>();
        try
        {
            for (int i = 0; i < count; i++)
            {
                // Both native and recorded-projection measurements may exceed
                // the normal 30 s inbound-frame timeout on a shared machine.
                peers.Add(new Peer(server.BoundPort, i, keepAlive: true));
                if (i % 16 == 15) server.TickSim();
            }
            var join = Stopwatch.StartNew();
            while ((server.Ships.Count() != count || peers.Any(p => p.OwnId < 0)) && join.Elapsed.TotalSeconds < 30 * speed)
                server.Tick();
            Assert.Equal(count, server.Ships.Count());
            Assert.All(peers, p => Assert.True(p.OwnId >= 0, $"peer failed to seat: {p.Error}"));
            var recordedPeers = server.TestSnapshotPeers();
            var initialPositions = recordedPeers.Select(p => p.Ship.Position).ToArray();
            var result = new List<object>();
            long? expectedEgress = null, expectedPackets = null;
            foreach (int mode in phase4 ? new[] { 0, 1, 2 } : new[] { 0, 1 })
            {
                bool snapshot = mode > 0;
                bool tiers = phase4 ? mode == 2 : phase3 && snapshot;
                server.FlushBroadcastSnapshots();
                server._broadcastSnapshotEnabled = phase4 ? snapshot : controlled || snapshot;
                server._broadcastEncodeOnceEnabled = phase4 ? snapshot : phase3 || (phase2 && snapshot);
                server._broadcastInterestTiersEnabled = tiers;
                uint recordedTick = 0;
                if (controlled)
                {
                    server.TestResetSnapshotReplay();
                    for (int i = 0; i < recordedPeers.Count; i++) recordedPeers[i].Ship.Position = initialPositions[i];
                    server.TestSeedPeers(recordedPeers);
                    server.TestUpdateCadenceClock = () => recordedTick / 20d;
                }
                var heartbeat = Stopwatch.StartNew();
                // Refresh own-ship dirty poses on every tick in BOTH modes.
                void Advance()
                {
                    if (controlled && heartbeat.Elapsed.TotalSeconds >= 5)
                    {
                        foreach (var peer in peers) peer.KeepAlive();
                        heartbeat.Restart();
                    }
                    foreach (var live in server.Ships)
                        live.Ship.Position += new Vector3(1, 0, 0);
                    if (controlled) server.TestProjectRecordedTick(recordedTick++, snapshot: phase4 ? snapshot : true, measure: true);
                    else server.TickSim();
                }
                for (int tick = 0; tick < 20; tick++) { Advance(); Thread.Sleep(50); }
                server.FlushBroadcastSnapshots();
                void DrainTransport(uint marker)
                {
                    if (controlled) foreach (var peer in peers) peer.KeepAlive();
                    server.TestSendSnapshotBarrier(marker);
                    Assert.True(SpinWait.SpinUntil(() => peers.All(p => p.LastMarker == marker),
                        TimeSpan.FromSeconds(30 * speed / regr)),   // P744 hardware-relative drain budget
                        $"Transport barrier {marker}: {peers.Count(p => p.LastMarker == marker)}/{count} drained; "
                        + string.Join("; ", peers.Where(p => p.LastMarker != marker).Take(4)
                            .Select(p => $"last={p.LastMarker} frames={p.Frames} error={p.Error}")));
                }
                DrainTransport(snapshot ? 668001u : 668000u);
                string prefix = phase4 ? "p679" : phase3 ? "p677" : phase2 ? "p669" : "p668";
                string csv = Path.Combine(Path.GetTempPath(), $"{prefix}-loopback-{count}-{(phase4 ? mode.ToString() : snapshot.ToString())}.csv");
                File.Delete(csv);
                TickProfiler.Configure(true, 3600, csv);
                TickProfiler.SetLabel($"{prefix} peers={count} snapshot={controlled || snapshot} encodeOnce={phase3 || (phase2 && snapshot)} tiers={phase3 && snapshot}");
                long startHeavy = server.ShipEncodingCounters.HeavyRecords;
                long startSingle = server.ShipEncodingCounters.SingleRecords;
                long startLight = server.ShipEncodingCounters.LightRecords;
                long startRelative = server.ShipEncodingCounters.RelativePositions;
                long startPoolAlloc = server.TestPublisher!.PublishAllocatedBytes;
                server.TakeUpdateAgeReport(recordedTick / 20d); // discard warm-up ages
                var perViewer = new System.Collections.Concurrent.ConcurrentDictionary<uint, long>();
                long startEgress = 0;
                long startPackets = 0;
                server.TestPublicationSizeCapture = (viewer, length, _, _) =>
                {
                    perViewer.AddOrUpdate(viewer.Value, static (_, add) => add, static (_, prior, add) => prior + add, (long)length);
                    Interlocked.Add(ref startEgress, length);
                    Interlocked.Increment(ref startPackets);
                };
                long startBuild = server.SnapshotBuildTimestampTicks;
                long startAlloc = server.SnapshotBuildAllocatedBytes;
                long startPublish = server.TestPublisher!.PublishTimestampTicks;
                long startPublished = server.TestPublisher.PublishedSnapshots;
                long startCoalesced = server.TestPublisher.CoalescedSnapshots;
                long startFrames = peers.Sum(p => Interlocked.Read(ref p.Frames));
                var receivedBytesBefore = peers.Select(p => Interlocked.Read(ref p.Bytes)).ToArray();
                long producerAllocationStart = GC.GetAllocatedBytesForCurrentThread();
                var times = new List<double>();
                for (int tick = 0; tick < 120; tick++)
                {
                    long before = Stopwatch.GetTimestamp();
                    Advance();
                    double cost = Stopwatch.GetElapsedTime(before).TotalMilliseconds;
                    times.Add(cost);
                    if (cost < 50) Thread.Sleep(TimeSpan.FromMilliseconds(50 - cost));
                }
                long producerAllocated = GC.GetAllocatedBytesForCurrentThread() - producerAllocationStart;
                server.FlushBroadcastSnapshots();
                server.TestPublicationSizeCapture = null;
                if (controlled)
                {
                    if (expectedEgress is { } egress)
                    {
                        if (phase3 || tiers) Assert.True(startEgress < egress * .8, $"tiers must reduce crowded egress: {egress} -> {startEgress}");
                        else Assert.Equal(egress, startEgress);
                    }
                    if (!phase3 && !tiers && expectedPackets is { } packets) Assert.Equal(packets, startPackets);
                    expectedEgress = startEgress;
                    expectedPackets = startPackets;
                }
                DrainTransport(snapshot ? 668003u : 668002u);
                TickProfiler.ForceReport();
                TickProfiler.Shutdown();
                long published = server.TestPublisher.PublishedSnapshots - startPublished;
                result.Add(new
                {
                    peers = count, recordedProjection = controlled, snapshot = phase4 ? snapshot : controlled || snapshot,
                    encodeOnce = phase4 ? snapshot : phase3 || (phase2 && snapshot), interestTiers = tiers, ticks = times.Count,
                    totalSocketPayloadBytes = peers.Select((p, i) => Interlocked.Read(ref p.Bytes) - receivedBytesBefore[i]
                        - Ping.FixedSize - FedPacket.CrcWidth).Sum(),
                    perViewerSocketPayloadBytes = peers.Select((p, i) => new { peer = i, bytes = Interlocked.Read(ref p.Bytes)
                        - receivedBytesBefore[i] - Ping.FixedSize - FedPacket.CrcWidth }).ToArray(),
                    totalBytes = startEgress, perViewerBytes = perViewer.OrderBy(p => p.Key).Select(p => new { peer = p.Key, bytes = p.Value }).ToArray(),
                    updateAges = server.TakeUpdateAgeReport(recordedTick / 20d),
                    heavyEncodesPerTick = (server.ShipEncodingCounters.HeavyRecords - startHeavy) / (double)times.Count,
                    singleEncodesPerTick = (server.ShipEncodingCounters.SingleRecords - startSingle) / (double)times.Count,
                    lightEncodesPerTick = (server.ShipEncodingCounters.LightRecords - startLight) / (double)times.Count,
                    relativePositionsPerTick = (server.ShipEncodingCounters.RelativePositions - startRelative) / (double)times.Count,
                    poolAllocatedBytesPerTick = (server.TestPublisher.PublishAllocatedBytes - startPoolAlloc) / times.Count,
                    egressBytesPerTick = startEgress / (double)times.Count, packetsPerTick = startPackets / (double)times.Count,
                    tickMeanMs = times.Average(), tickP95Ms = times.Order().ElementAt((int)Math.Ceiling(times.Count * .95) - 1),
                    projectionMeanMs = (server.SnapshotBuildTimestampTicks - startBuild) * 1000d / Stopwatch.Frequency / times.Count,
                    projectionBytesPerTick = (server.SnapshotBuildAllocatedBytes - startAlloc) / times.Count,
                    simAllocatedBytesPerTick = producerAllocated / times.Count,
                    publishMeanMs = published == 0 ? 0 : (server.TestPublisher.PublishTimestampTicks - startPublish) * 1000d / Stopwatch.Frequency / published,
                    coalesced = server.TestPublisher.CoalescedSnapshots - startCoalesced,
                    containers = server.TestPublisher.SnapshotContainersCreated,
                    failures = server.TestPublisher.EncodeFailures,
                    receivedFrames = peers.Sum(p => Interlocked.Read(ref p.Frames)) - startFrames - count,
                    csv,
                });
                Assert.All(peers, p => Assert.Null(p.Error));
                Assert.Equal(count, server.Ships.Count());
                Assert.Equal(0, server.TestPublisher.EncodeFailures);
                Assert.True(peers.Sum(p => p.Frames) > startFrames + count);
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"{(phase4 ? "p679" : phase3 ? "p677" : phase2 ? "p669" : "p668")}-loopback-{count}.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { foreach (var peer in peers) peer.Dispose(); }
    }
}
