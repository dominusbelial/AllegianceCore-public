using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;
using WireCollision = Allegiance.Core.Wire.CollisionEvent;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P1094CollisionRosterTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo Roster = typeof(Srv).GetField("_collisionRoster", Private)!;

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Repeated_contacts_share_one_grouping_until_a_seat_changes_or_the_snapshot_closes()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(P1094CollisionFleetTests.Config());
        var peers = UpdateCadenceTests.Seed(server, 40, 40);
        var mission = server.TestMission!;
        void Hit() => mission.Site.CollisionEvent(peers[0].Ship, peers[1].Ship, peers[0].Sector.Id, default, 1, 2);
        for (int tick = 0; tick < 3; tick++)
        {
            server.BeginBroadcastSnapshot();
            Hit();
            var before = Roster.GetValue(server);
            Assert.NotNull(before);
            for (int contact = 0; contact < 128; contact++)
            {
                Hit();
                Assert.Same(before, Roster.GetValue(server));
            }
            peers[2].Ship.SideId = (short)(1 - peers[2].Ship.SideId!.Value);
            Hit();
            Assert.NotSame(before, Roster.GetValue(server));
            server.CompleteBroadcastSnapshot();
            Assert.Null(Roster.GetValue(server));
            server.FlushBroadcastSnapshots();
        }
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Late_fleet_preserves_each_tick_state_rng_and_ordered_packet_transcript()
    {
        Require.Artwork();
        var expected = Replay(old: true);
        var actual = Replay(old: false);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < actual.Count; i++)
        {
            Assert.Equal(expected[i].State, actual[i].State);
            Assert.Equal(expected[i].Wire, actual[i].Wire);
        }
    }

    private static List<(byte[] State, byte[] Wire)> Replay(bool old)
    {
        var random = new Random(1094);
        using var server = new Srv { SpawnInitialMinersAtBoot = false, SimulationRandom = random };
        server.Boot(P1094CollisionFleetTests.Config());
        var peers = UpdateCadenceTests.Seed(server, 32, 32);
        var mission = server.TestMission!;
        mission.Stage = MissionStage.Started;
        var host = (ServerHost)mission.Site;
        if (old) InstallOracle(server, host);
        var ships = peers.Select(p => p.Ship).ToArray();
        var hull = mission.Core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmFighter));
        for (int i = 0; i < ships.Length; i++)
        {
            ships[i].ChangeHull(hull);
            ships[i].SideId = (short)(i % 2);
        }

        var result = new List<(byte[], byte[])>();
        var cloak = typeof(Ship).GetField("_cloakEmitterActive", Private)!;
        for (uint tick = 0; tick < 8; tick++)
        {
            using var bytes = new MemoryStream();
            using var writer = new BinaryWriter(bytes);
            int frames = 0;
            server.TestUpdateCadenceClock = () => tick * .05;
            server.TestPublicationCapture = (peer, packet, key, guaranteed) =>
            {
                writer.Write(peer.Value); writer.Write(key); writer.Write(guaranteed);
                writer.Write(packet.Length); writer.Write(packet);
                frames++;
            };
            P1094CollisionFleetTests.Approach(ships, 12);
            server.TestProjectRecordedTick(tick, true, discovery: true, surge: () =>
            {
                mission.Update(mission.LastUpdate + .05);
                Assert.Equal(12, peers[0].Sector.LastShipShipResolved);
                var subject = ships[0];
                // Mutate between callbacks in ONE tick. Only the first member
                // of a visible enemy side must be local for its whole side to hear.
                void Hit(Ship? other) => host.CollisionEvent(subject, other, subject.Sector!.Id, subject.Position, 1, 2);
                short? side = ships[1].SideId;
                ships[1].SideId = null;
                Hit(ships[1]);
                ships[1].SideId = side;
                var original = peers.ToArray();
                var away = peers.Select(p => (p.Conn, p.Ship, mission.Sectors[1])).Reverse().ToList();
                server.TestSeedPeers(away);
                Hit(null);
                away[^2] = (away[^2].Conn, away[^2].Ship, subject.Sector!);
                server.TestSeedPeers(away);
                Hit(null);
                cloak.SetValue(subject, true);
                subject.SideVisibility.SetSideVisibility(1, false);
                Hit(null);
                subject.SideVisibility.SetSideVisibility(1, true);
                Hit(null);
                ships[3].Mission = null;
                Hit(null);
                ships[3].Mission = mission;
                ships[3].SideId = 2;
                Hit(ships[3]);
                ships[3].SideId = 1;
                cloak.SetValue(subject, false);
                server.TestSeedPeers(original);
            });
            server.FlushBroadcastSnapshots();
            server.TestPublicationCapture = null;
            Assert.True(frames > 32);
            result.Add((State(mission), bytes.ToArray()));
        }
        Assert.Equal(0, server.BroadcastPublishFailures);
        return result;
    }

    // Base audience and encoder. The existing ordered SendPeer/publisher is
    // shared so even interleaving with other event kinds is compared on wire.
    private static void InstallOracle(Srv server, ServerHost host)
    {
        var exports = (HashSet<(ConnectionId, short)>)typeof(Srv).GetField("_teammateExportSent", Private)!.GetValue(server)!;
        var send = typeof(Srv).GetMethod("SendPeer", Private, [typeof(ConnectionId), typeof(byte[])])!
            .CreateDelegate<Action<ConnectionId, byte[]>>(server);
        host.Collision = (a, b, sector, impact, j1, j2) =>
        {
            var audience = new HashSet<ConnectionId>();
            var peers = server.TestSnapshotPeers();
            void Add(Ship party)
            {
                foreach (var p in peers)
                    if (p.Ship == party || (party.SideId is { } own && p.Ship.SideId == own && exports.Contains((p.Conn, party.Id))))
                        audience.Add(p.Conn);
                if (party.Sector is null || party.SideId is null) return;
                var enemies = peers.Where(p => p.Ship.Mission == party.Mission && p.Ship.SideId != null && p.Ship.SideId != party.SideId);
                foreach (var group in enemies.GroupBy(p => p.Ship.SideId!.Value))
                    if (group.Any(p => p.Sector == party.Sector) && (!party.IsCloaked || party.SeenBySide(group.Key)))
                        foreach (var p in group) audience.Add(p.Conn);
            }
            Add(a);
            if (b != null) Add(b);
            var packet = FedPacket.Pack(new WireCollision { Party1Id = a.Id, Party2Id = b?.Id ?? -1,
                ClusterId = sector, Jiggle1 = j1, Jiggle2 = j2, Position = impact }.Encode());
            foreach (var peer in audience) send(peer, packet);
        };
    }

    // Capture the complete logical mission graph, including private fields,
    // seeded RNG state, collection order and reference aliasing. Only external
    // services, immutable input assets and two boot metadata fields are opaque.
    private static byte[] State(Mission mission)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes);
        var visited = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        Write(mission);
        return SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));

        void Write(object? value)
        {
            if (value is null) { writer.Write("null"); return; }
            var type = value.GetType();
            writer.Write(type.FullName!);
            switch (value)
            {
                case float f: writer.Write(f); return;
                case double d: writer.Write(d); return;
                case string s: writer.Write(s); return;
                case Enum e: writer.Write(e.ToString("D")); return;
                case ServerHost or Delegate or TimeProvider or StaticCore or MissionMap:
                    return;
            }
            if (type.IsPrimitive || value is decimal)
            {
                writer.Write(Convert.ToString(value, CultureInfo.InvariantCulture)!);
                return;
            }
            if (!type.IsValueType)
            {
                if (visited.TryGetValue(value, out var index)) { writer.Write(index); return; }
                writer.Write(-1);
                visited.Add(value, visited.Count);
            }
            if (value is IEnumerable sequence)
            {
                foreach (object? item in sequence) Write(item);
                writer.Write("end of sequence");
                return;
            }
            for (Type? owner = type; owner != null; owner = owner.BaseType)
                foreach (var field in owner.GetFields(Private | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (owner == typeof(Mission) && field.Name is "<CreatedWallTime>k__BackingField" or "<ResearchEpoch>k__BackingField") continue;
                    Write(field.GetValue(value));
                }
        }
    }
}
