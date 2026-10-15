using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P684MergeTests
{
    [SkippableTheory, Trait("Requires", "Artwork")]
    [InlineData(false)]
    [InlineData(true)]
    public void Roster_quiet_ticks_preserve_immediate_join_stage_and_ordered_departure_deltas(bool pipeline)
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config();
        config.Broadcast!.ImmutableSnapshot = pipeline;
        config.Broadcast.EncodeOnce = pipeline;
        config.Broadcast.InterestTiers = pipeline;
        server.Boot(config);
        server.TestMission!.Stage = MissionStage.Started;
        var peers = UpdateCadenceTests.Seed(server, 3, 3);
        var original = peers.Take(2).ToList();
        server.TestSeedPeers(original);
        var frames = new List<(ConnectionId Peer, byte[] Message, int Key, bool Guaranteed)>();
        server.TestPublicationCapture = (peer, packet, key, guaranteed) =>
        {
            foreach (var message in FedPacket.Unpack(packet))
                if ((ushort)(message[2] | message[3] << 8) is ScoreRosterUpdate.Fmid or ScoreRosterDelta.Fmid)
                    frames.Add((peer, message, key, guaranteed));
        };
        void Tick(uint tick)
        {
            frames.Clear();
            server.TestProjectRecordedTick(tick, pipeline);
            server.FlushBroadcastSnapshots();
            Assert.All(frames, f => { Assert.Equal(0, f.Key); Assert.True(f.Guaranteed); });
        }
        Tick(0);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f => Assert.Equal(2, ScoreRosterUpdate.Decode(f.Message).Entries.Length));
        Tick(1);
        Assert.Empty(frames);
        server.TestSeedPeers(peers);
        Tick(2); // A newcomer must not wait for the 40-tick cadence.
        Assert.Equal(3, frames.Count);
        Assert.Equal(3, ScoreRosterUpdate.Decode(frames.Single(f => f.Peer == peers[2].Conn).Message).Entries.Length);
        Assert.All(frames.Where(f => f.Peer != peers[2].Conn), f =>
            Assert.Equal((ushort)3, ScoreRosterDelta.Decode(f.Message).ResultCount));
        server.TestMission.Stage = MissionStage.Starting;
        Tick(3); // A stage change is an immediate authoritative refresh.
        Assert.Equal(3, frames.Count);
        Assert.All(frames, f => Assert.Equal(3, ScoreRosterUpdate.Decode(f.Message).Entries.Length));
        server.TestSeedPeers(original);
        Tick(4);
        Assert.Empty(frames);
        Tick(43);
        Assert.Equal(2, frames.Count);
        Assert.All(frames, f =>
        {
            var delta = ScoreRosterDelta.Decode(f.Message);
            Assert.Equal((ushort)3, delta.BaseCount);
            Assert.Equal((ushort)2, delta.ResultCount);
        });
    }
}
