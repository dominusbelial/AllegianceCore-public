using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P906ProductionOrderTests
{
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Server_binds_independent_flags_to_every_assembled_mission(bool phases, bool collisions)
    {
        Require.Artwork();
        var config = P788cNativeTickOrderTests.Config(false, snapshot: true);
        config.Sim.NativeShipPhases = phases; config.Sim.NativeCollisionQueue = collisions;
        config.Games.Add(new() { Name = "P906 second mission", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] });
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(config);
        Assert.Equal(2, server.Missions.Count);
        Assert.All(server.Missions, m => { Assert.Equal(phases, m.NativeShipPhasesEnabled); Assert.Equal(collisions, m.NativeCollisionQueueEnabled); });
    }

    // FedSrv.CPP:4237-4254 changes which completed state is sent. With the
    // same fixed inputs it cannot change this closed-loop authoritative fight.
    // Client feedback can subsequently differ because the wire state is older.
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Broadcast_boundary_changes_same_tick_wire_pose_but_preserves_fixed_input_combat()
    {
        Require.Artwork();
        static (List<(Vector3, Vector3, float)> State, List<Vector3> Wire) Replay(bool before)
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            var config = P788cNativeTickOrderTests.Config(before, snapshot: true);
            server.Boot(config);
            var m = server.TestMission!; m.Stage = MissionStage.Started; m.CrowdingPenaltyEnabled = false;
            var peers = UpdateCadenceTests.Seed(server, ships: 2, viewers: 2);
            var a = peers[0].Ship; var b = peers[1].Ship;
            a.Position = new(5000, 5000, 5000); b.Position = new(6000, 5000, 5000);
            a.Velocity = b.Velocity = new(10, 0, 0);
            a.StateM = b.StateM = ShipControlState.CoastButton;
            var trace = new List<(Vector3, Vector3, float)>();
            var wire = new List<Vector3>();
            server.TestPublicationCapture = (peer, bytes, _, _) => {
                if (peer != peers[0].Conn) return;
                foreach (var message in FedPacket.Unpack(bytes))
                    if ((ushort)(message[2] | message[3] << 8) == SingleShipUpdate.Fmid)
                    {
                        var pose = SingleShipUpdate.Decode(message);
                        if (pose.OidTarget == a.Id) wire.Add(pose.Ship.Position);
                    }
            };
            try
            {
                for (int tick = 0; tick < 12; tick++)
                {
                    if (tick == 5)
                    {
                        byte type = (byte)Enumerable.Range(0, 20).First(r => m.Core.Constants.DamageConstants[r][b.HullType.DefenseType] > 0);
                        b.ReceiveDamage(type, b.EffectiveMaxHitPoints() * .1f, m.LastUpdate, b.Position, a.Position, null);
                    }
                    server.TickSim(); server.FlushBroadcastSnapshots();
                    trace.Add((a.Position, b.Position, b.Fraction));
                }
                Assert.True(trace[^1].Item3 < trace[0].Item3);
                Assert.Equal(0, server.TestPublisher!.EncodeFailures);
                return (trace, wire);
            }
            finally { server.TestPublicationCapture = null; }
        }
        var off = Replay(false); var on = Replay(true);
        Assert.Equal(off.State, on.State);
        Assert.Equal(12, off.Wire.Count); Assert.Equal(12, on.Wire.Count);
        Assert.NotEqual(off.Wire[0], on.Wire[0]);
        Assert.Equal(off.State[0].Item1, off.Wire[0]);
        Assert.Equal(new Vector3(5000, 5000, 5000), on.Wire[0]);
    }
}
