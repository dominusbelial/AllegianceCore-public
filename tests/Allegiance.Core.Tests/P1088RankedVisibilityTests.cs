using System.Reflection;
using Allegiance.Core;
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

[Collection("LiveNetwork")]
public sealed class P1088RankedVisibilityTests
{
    // Base Begin / MarkPose / SelectFloor: keep the independent sector walk,
    // copy-out/write-back stamps and pre-send samples in the oracle.
    private sealed class OldCadence
    {
        private (Ship?, Sector?, short?) _view;
        private int _generation, _cursor;
        private readonly Dictionary<short, (Ship Ship, double LastPose, bool HasPose, int Generation)> _stamps = [];
        private readonly PoseAgeWindow _window = new();
        private long _extraBytes, _packetBytes;

        internal Ship[] Check(ViewerPoseCadence actual, Sector sector, Ship self, bool admin, bool sample,
            double now, double hz, HeavyShipUpdateEntry[] ranked, int packetLength)
        {
            if (_view != (self, sector, self.SideId))
            {
                _stamps.Clear();
                _cursor = 0;
                _view = (self, sector, self.SideId);
            }
            _generation++;
            var visible = sector.GetShipUpdateCandidates()
                .Where(s => s != self && Srv.ShipPoseVisible(self, s, admin)).ToArray();
            int older1 = 0, older3 = 0;
            foreach (var ship in visible)
            {
                if (!_stamps.TryGetValue(ship.Id, out var stamp) || stamp.Ship != ship)
                    stamp = (ship, now, false, _generation);
                stamp.Generation = _generation;
                _stamps[ship.Id] = stamp;
                double age = Math.Max(0, now - stamp.LastPose);
                if (age > 1) older1++;
                if (age > 3) older3++;
                if (sample) _window.Record((ship.Position - self.Position).LengthSquared(), age, !stamp.HasPose);
            }
            foreach (var pair in _stamps)
                if (pair.Value.Generation != _generation) _stamps.Remove(pair.Key);

            void Mark(short id)
            {
                if (!_stamps.TryGetValue(id, out var stamp)) return;
                stamp.LastPose = now;
                stamp.HasPose = true;
                _stamps[id] = stamp;
            }
            foreach (var entry in ranked) Mark(entry.ShipId);
            var floor = new List<Ship>();
            if (hz > 0 && visible.Length > 0)
            {
                int quota = (int)Math.Ceiling(visible.Length / Math.Floor(20 / hz));
                _cursor %= visible.Length;
                for (int n = 0; n < visible.Length; n++)
                {
                    var ship = visible[(_cursor + n) % visible.Length];
                    var stamp = _stamps[ship.Id];
                    if ((n < quota || now - stamp.LastPose >= 1 / hz) && (!stamp.HasPose || stamp.LastPose != now))
                    {
                        floor.Add(ship);
                        Mark(ship.Id);
                    }
                }
                _cursor = (_cursor + quota) % visible.Length;
            }
            Assert.Equal(visible, actual.Visible.ToArray());
            Assert.Equal(_stamps.Count, actual.TrackedCount);
            foreach (var (id, stamp) in _stamps) Assert.Equal(stamp, actual.TestReadStamp(id));
            Assert.Equal((older1, older3), (actual.Older1, actual.Older3));
            Assert.Equal((_window.Older1, _window.Older3, _window.NeverPose),
                (actual.Window.Older1, actual.Window.Older3, actual.Window.NeverPose));
            for (int band = 0; band < 4; band++) Assert.Equal(_window.Band(band), actual.Window.Band(band));
            _extraBytes += floor.Count * HeavyShipUpdateEntry.Width;
            _packetBytes += packetLength;
            Assert.Equal((_extraBytes, _packetBytes), (actual.ExtraBytes, actual.PacketBytes));
            return floor.ToArray();
        }
    }

    [SkippableTheory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [Trait("Requires", "Artwork")]
    public void Each_viewer_matches_the_old_scan_stamps_and_wire(double hz, bool sample)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config(hz);
        config.LiveSettings.AdminCheats = new() { Enabled = true, AllowNames = ["seed-*"], Secret = "fixture" };
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(config);
        if (sample) TickProfiler.Configure(true, 3600, null);
        try
        {
            var mission = server.TestMission!;
            var home = mission.Sectors[0];
            var away = mission.Sectors[1];
            var hull = mission.Core.HullTypes.Values.First();
            var random = new Random(1088);
            Ship Place(short id, Sector sector, short side)
            {
                var pos = new Vector3(random.Next(8000), random.Next(200), random.Next(300));
                var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };
                mission.PlaceShip(ship, sector);
                ship.Deviation = random.Next(4);
                return ship;
            }
            var fleet = Enumerable.Range(0, 43).Select(i =>
                Place((short)(1000 + i), i < 39 ? home : away, (short)random.Next(2))).ToArray();
            Ship child = fleet[2], parent = fleet[3], hidden = fleet[4];
            child.ParentShip = parent;
            child.TurretId = 1;
            void Cloak(Ship ship)
            {
                ship.SideId = 1;
                ship.MountPart(mission.Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Cloak),
                    1, mountId: 0, fractionMounted: 255);
                ship.Energy = 1_000_000;
                ship.StateM = ShipControlState.CloakActive;
                ship.Update(ship.LastUpdate + .05);
                ship.SideVisibility.SetSideVisibility(0, false);
                Assert.True(ship.IsCloaked);
            }
            Cloak(hidden);
            Cloak(fleet[41]);
            T Field<T>(string name) => (T)typeof(Srv).GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(server)!;
            var cameras = Field<Dictionary<ConnectionId, short>>("_spectateSector");
            var admins = Field<HashSet<ConnectionId>>("_adminAuthenticated");
            var states = Field<Dictionary<ConnectionId, ViewerPoseCadence>>("_viewerPoseCadence");
            var oracles = Enumerable.Range(0, 4).Select(_ => new OldCadence()).ToArray();
            int[] order = [0, 1, 2, 39];
            byte[]? sent = null;
            server.TestPublicationCapture = (_, bytes, _, _) => sent = bytes;

            for (uint tick = 0; tick < 36; tick++)
            {
                // Same-clock sends, exact half-second deadlines, and stalls beyond both age bands.
                now += (tick % 7) switch { 0 => 3.25, 1 => 0, 2 => .5, _ => .05 };
                fleet[0].SideId = (short)(tick / 6 % 2);
                fleet[39].SideId = 0;
                hidden.SideVisibility.SetSideVisibility(0, tick % 5 == 4);
                if (tick == 10 || tick == 20) home.Remove(fleet[7]);
                if (tick == 10 || tick == 22) fleet[7] = Place(fleet[7].Id, home, 0);
                // With four candidates the hidden target still consumes a budget slot.
                // With two candidates the small-sector shortcut serves every remaining ship.
                if (tick == 14) { away.Remove(fleet[40]); away.Remove(fleet[42]); }
                if (tick == 18) { mission.PlaceShip(fleet[40], away); mission.PlaceShip(fleet[42], away); }
                if (tick >= 24)
                {
                    var traveler = fleet[random.Next(8, 39)];
                    var origin = traveler.Sector!;
                    origin.Remove(traveler);
                    mission.PlaceShip(traveler, origin == home ? away : home);
                }
                random.Shuffle(order);
                foreach (int index in order)
                {
                    var self = fleet[index];
                    var conn = new ConnectionId((uint)(700 + index));
                    var reference = new ConnectionId(conn.Value + 100);
                    bool admin = index == 1 && tick % 8 < 6;
                    var sector = admin && tick % 8 < 3 ? away : self.Sector!;
                    self.CommandTarget = index == 39 ? fleet[41] : (tick % 4) switch
                    {
                        0 => hidden, 1 => self, 2 => null, _ => fleet[random.Next(fleet.Length)]
                    };
                    parent.Deviation = -1000; // force the extra turret-parent slot
                    bool parallel = (tick + index) % 2 == 0;
                    byte[] Project(ConnectionId peer, bool legacy)
                    {
                        server.TestLegacySurgeProjection = legacy;
                        server._minUpdateHz = legacy ? 0 : hz;
                        server.TestSeedPeers([(peer, self, self.Sector!)]);
                        if (admin) { admins.Add(peer); cameras[peer] = sector.Id; }
                        else cameras.Remove(peer);
                        sent = null;
                        server.BeginBroadcastSnapshot();
                        try { server.TestCollectBroadcast(tick, parallel, "shipsUpdate"); }
                        finally { server.CompleteBroadcastSnapshot(); }
                        server.FlushBroadcastSnapshots();
                        return Assert.IsType<byte[]>(sent);
                    }

                    // The DTO oracle runs the old full candidate scan with its floor disabled.
                    // Restore every deviation so the pooled sender starts at the same viewer boundary.
                    var before = fleet.Select(s => s.Deviation).ToArray();
                    var messages = FedPacket.Unpack(Project(reference, true));
                    var expectedDeviations = fleet.Select(s => s.Deviation).ToArray();
                    for (int i = 0; i < fleet.Length; i++) fleet[i].Deviation = before[i];
                    var packet = Project(conn, false);
                    Assert.Equal(expectedDeviations, fleet.Select(s => s.Deviation));
                    var actual = FedPacket.Unpack(packet);
                    var ranked = HeavyShipsUpdate.Decode(messages[0]);
                    var received = HeavyShipsUpdate.Decode(actual[0]);
                    var floor = hz > 0 || sample
                        ? oracles[index == 39 ? 3 : index].Check(states[conn], sector, self, admin, sample,
                            now, hz, ranked.HeavyShipEntries, packet.Length) : [];
                    if (hz == 0 && !sample) Assert.False(states.ContainsKey(conn));
                    var receivedIds = received.HeavyShipEntries.Select(e => e.ShipId).ToArray();
                    Assert.Equal(ranked.HeavyShipEntries.Select(e => e.ShipId), receivedIds.Take(ranked.HeavyShipEntries.Length));
                    Assert.Equal(floor.Select(s => s.Id), receivedIds.Skip(ranked.HeavyShipEntries.Length));
                    static IEnumerable<short> LightIds(byte[][] frames) => frames.Skip(1)
                        .SelectMany(m => LightShipsUpdate.Decode(m).LightShipEntries).Select(e => e.ShipId);
                    Assert.Equal(LightIds(messages), LightIds(actual));
                    ranked.HeavyShipEntries = [.. ranked.HeavyShipEntries, .. floor.Select(s =>
                        HeavyShipUpdateEntry.Capture(tick, ranked.PositionReference, s, tick))];
                    messages[0] = ranked.Encode();
                    Assert.Equal(FedPacket.Pack(messages.AsSpan()), packet);
                }
            }
        }
        finally { TickProfiler.Shutdown(); }
    }
}
