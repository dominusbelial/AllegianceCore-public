using System.Diagnostics;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Profiling;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>Real TCP, real sim passes at 20 Hz, four seated viewers and 96
/// stationary subjects in one sector. Runs without environment gates. Ships
/// carry no weapons/AI, isolating pose scheduling from combat and routing.</summary>
[Collection("LiveNetwork")]
[Trait("Tier", "Perf")]
public sealed class UpdateCadenceLoopbackTests
{
    private sealed class Receiver : ITransportSite
    {
        private readonly object _gate = new();
        private readonly Dictionary<short, (uint Tick, double At)> _last = new();
        private readonly HashSet<short> _expected = new();
        private readonly PoseAgeWindow _ages = new();
        private bool _measuring;
        private double _start;
        private uint _startTick;
        internal long Bytes;
        internal uint MaxTickAge;
        internal int FrameCount;
        internal double MaxArrivalGap;
        private double _lastArrival;
        internal TcpTransport Client = null!;
        internal volatile int OwnId = -1;

        public void OnPeerJoined(ConnectionId id, string name) { }
        public void OnPeerLeft(ConnectionId id) { }
        public void OnSessionLost() { }
        public void OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            foreach (var msg in FedPacket.Unpack(data.ToArray()))
            {
                ushort fmid = (ushort)(msg[2] | msg[3] << 8);
                if (fmid == LogonAck.Fmid && LogonAck.Decode(msg).FValidated)
                    Client.Send(ConnectionId.Server, FedPacket.Pack(new PositionReq { SideId = -1 }.Encode()), guaranteed: true);
                if (fmid == SetCluster.Fmid) OwnId = SetCluster.Decode(msg).Ship.ShipId;
                if (fmid != HeavyShipsUpdate.Fmid) continue;
                var update = HeavyShipsUpdate.Decode(msg);
                lock (_gate)
                {
                    if (_measuring)
                    {
                        FrameCount++;
                        Bytes += data.Length;
                        if (_lastArrival > 0) MaxArrivalGap = Math.Max(MaxArrivalGap, now - _lastArrival);
                        _lastArrival = now;
                        // Sample age before applying the received snapshot. LIGHT
                        // is deliberately ignored: it has no position/orientation.
                        foreach (short id in _expected)
                        {
                            var last = _last.GetValueOrDefault(id, (Tick: _startTick, At: _start));
                            uint ageTicks = update.TimeReference - last.Tick;
                            MaxTickAge = Math.Max(MaxTickAge, ageTicks);
                            _ages.Record(0, now - last.At);
                        }
                    }
                    foreach (var entry in update.HeavyShipEntries) _last[entry.ShipId] = (update.TimeReference, now);
                }
            }
        }

        internal void Start(IEnumerable<short> expected, uint tick)
        {
            lock (_gate)
            {
                _expected.UnionWith(expected);
                _start = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
                _startTick = tick;
                _measuring = true;
            }
        }

        internal (double P50, double P95, double Max, int Seen) Finish()
        {
            lock (_gate)
            {
                _measuring = false;
                var b = _ages.Band(0);
                return (b.P50, b.P95, b.Max, _expected.Count(id => _last.ContainsKey(id)));
            }
        }
    }

    [SkippableTheory]
    [InlineData(true, 0)]
    [InlineData(true, 2)]
    [InlineData(false, 0)]
    [InlineData(false, 2)]
    [Trait("Requires", "Artwork")]
    public void Hundred_ship_loopback_reports_pose_ages_and_bandwidth(bool native, double hz)
    {
        Require.Artwork();
        using var server = new Srv();
        int placed = 0;
        server.ShipSpawner = (core, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var hull = core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = new Ship(id, hull, new Vector3(placed++ * 80, 10000, 0), Vector3.Zero,
                Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(ship, sector);
            return (ship, sector);
        };
        server.Boot(UpdateCadenceTests.Config(hz, native));
        foreach (var mission in server.Missions) mission.Stage = MissionStage.Started;
        var receivers = Enumerable.Range(0, 4).Select(_ => new Receiver()).ToArray();
        var clients = receivers.Select(r => r.Client = new TcpTransport(r)).ToArray();
        try
        {
            for (int i = 0; i < clients.Length; i++)
            {
                string name = $"p659_{i}";
                clients[i].Join("127.0.0.1", server.BoundPort, name);
                clients[i].Send(ConnectionId.Server, FedPacket.Pack(new LogonRequest
                {
                    CharacterName = name, ZoneTicket = [1], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
                    FedsrvVer = LogonRequest.MsgVer, Time = 659, DwCookie = 659,
                    CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
                    SteamID = 99000000000000659ul, DrmHash = "P659",
                }.Encode()), guaranteed: true);
            }
            var join = Stopwatch.StartNew();
            while ((server.Ships.Count() < 4 || receivers.Any(r => r.OwnId < 0)) && join.Elapsed.TotalSeconds < 10) server.Tick();
            Assert.Equal(4, server.Ships.Count());
            var sector = server.TestSectors[0];
            var hull = server.TestMission!.Core.HullTypes.Values.Where(h => h.Signature >= 1 && h.Length < 100)
                .OrderByDescending(h => h.Signature).First();
            for (int i = 0; i < 96; i++)
            {
                var ship = new Ship((short)(15000 + i), hull,
                    new Vector3((i + 4) * 80, 10000, 0), Vector3.Zero, Orientation.Identity) { SideId = 1 };
                server.TestMission.PlaceShip(ship, sector);
            }
            // Force all peers onto the same side; normal logon side selection
            // alternates sides independently of the custom spawn hook.
            foreach (var live in server.Ships) live.Ship.SideId = 0;
            // Profiler sampling enabled, but a long window keeps this measurement
            // intact until the explicit report below (normal server windows are 5s).
            TickProfiler.Configure(true, 60, null);
            server.TakeUpdateAgeReport(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            var allIds = sector.GetShipUpdateCandidates().Select(s => s.Id).ToArray();
            Assert.Equal(100, allIds.Length);
            // A full ranked-budget sweep before measuring avoids counting initial
            // join/export time as a steady-state pose refresh gap.
            for (int i = 0; i < 40; i++) server.Tick();
            Assert.All(sector.GetShipUpdateCandidates(), ship => Assert.True(ship.SideId == 0 || ship.SeenBySide(0),
                $"ship {ship.Id} at {ship.Position} must be visible to side 0 (sig={ship.EffectiveSignature}, scanner={server.Ships.First().Ship.EffectiveScannerRange()}, stage={server.TestMission.Stage}, updates={sector.UpdateCount})"));
            server.TakeUpdateAgeReport(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            for (int i = 0; i < receivers.Length; i++)
                receivers[i].Start(allIds.Except(new[] { (short)receivers[i].OwnId }), (uint)server.LoopIterations);
            var sw = Stopwatch.StartNew();
            double lastTick = 0, maxTickGap = 0;
            while (sw.Elapsed.TotalSeconds < 6)
            {
                double before = sw.Elapsed.TotalSeconds;
                if (lastTick > 0) maxTickGap = Math.Max(maxTickGap, before - lastTick);
                lastTick = before;
                server.Tick();
            }
            // Drain the final asynchronous TCP send before reading receiver stats.
            Thread.Sleep(100);
            string report = server.TakeUpdateAgeReport(Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
            var stats = receivers.Select(r => r.Finish()).ToArray();
            string result = $"RESULT_P659 model={(native ? "native" : "port")} hz={hz} ships=100 viewers=4 " +
                $"seconds={sw.Elapsed.TotalSeconds:0.###} rxShipsBps={receivers.Sum(r => r.Bytes) / sw.Elapsed.TotalSeconds:0.0} " +
                $"rxP50={stats.Max(s => s.P50):0.###} rxP95={stats.Max(s => s.P95):0.###} rxMax={stats.Max(s => s.Max):0.###} " +
                $"seenMin={stats.Min(s => s.Seen)} maxTickAge={receivers.Max(r => r.MaxTickAge)} maxTickGap={maxTickGap:0.###} maxArrivalGap={receivers.Max(r => r.MaxArrivalGap):0.###}";
            string path = Path.Combine(Path.GetTempPath(), $"p659-{(native ? "native" : "port")}-{hz:0}.log");
            File.WriteAllText(path, result + Environment.NewLine + report + Environment.NewLine);
            Assert.All(receivers, r => Assert.True(r.FrameCount > 80));
            if (hz > 0)
            {
                Assert.All(stats, s => Assert.Equal(99, s.Seen));
                // Tick-space bound plus an explicit wall-time tolerance of one
                // measured tick gap and 100ms for local async TCP scheduling.
                Assert.All(receivers, r => Assert.True(r.MaxTickAge <= 12, $"pose gap {r.MaxTickAge} ticks"));
                Assert.All(stats, s => Assert.True(s.Max <= 1 / hz + maxTickGap + 0.1,
                    $"wall pose age {s.Max:0.###} (tick gap {maxTickGap:0.###})"));
                Assert.DoesNotContain("floorExtraBytes=0 ", report);
            }
            else Assert.Contains("floorExtraBytes=0 ", report);
        }
        finally
        {
            foreach (var client in clients) client.Shutdown();
        }
    }
}
