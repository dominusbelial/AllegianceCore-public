using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// HUD-17 — the pooled and non-pooled SHIP_STATUS(90) builders must produce
/// byte-identical packets, now that both carry the real own-side
/// <c>Detected</c> bit (<c>Server.BroadcastBuffers.PackStatus</c> vs
/// <c>Server.Broadcast.MakeOwnShipStatus</c>), and the bit itself must be the
/// native rule's value, including the <c>false</c> case the old unconditional
/// <c>0x80</c> could never emit (<c>FedSrv/fsship.cpp:237-335</c>).
/// </summary>
[Collection("LiveNetwork")]
public sealed class Hud17PooledStatusTests
{
    private static List<byte[]> StatusFrames(IEnumerable<(byte[] Bytes, int Key)> packets)
    {
        var frames = new List<byte[]>();
        foreach (var (bytes, _) in packets)
            foreach (var message in FedPacket.Unpack(bytes))
                if ((ushort)(message[2] | message[3] << 8) == ShipStatusMessage.Fmid)
                    frames.Add(message.ToArray());
        return frames;
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Pooled_and_non_pooled_status_packets_agree_and_carry_the_real_detected_bit()
    {
        Require.Artwork();
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        server.Boot(UpdateCadenceTests.Config());
        server.TestMission!.Stage = MissionStage.Started;
        var peers = UpdateCadenceTests.Seed(server, 2, 2);
        short side1 = server.TestMission.Sides[1].Id;

        // Ship A is seen by an enemy side; ship B is not. Both are side 0
        // (UpdateCadenceTests.Seed), so each peer receives both own-side copies.
        var a = peers[0].Ship;
        var b = peers[1].Ship;
        foreach (var ship in new[] { a, b })
            ship.SideVisibility.SetSideVisibility(ship.SideId!.Value, true);
        a.SideVisibility.SetSideVisibility(side1, true);
        b.SideVisibility.SetSideVisibility(side1, false);

        List<(byte[] Bytes, int Key)> Replay(bool encodeOnce, bool parallel)
        {
            server.TestResetSnapshotReplay();
            server._broadcastEncodeOnceEnabled = encodeOnce;
            server._broadcastParallelBuild = parallel;
            server.TestSeedPeers(peers);
            var captured = new List<(byte[] Bytes, int Key)>();
            server.TestPublicationCapture = (_, bytes, key, _) => captured.Add((bytes, key));
            try
            {
                server.TestProjectRecordedTick(5, snapshot: true);
                server.FlushBroadcastSnapshots();
            }
            finally { server.TestPublicationCapture = null; }
            return captured;
        }

        var plain = Replay(encodeOnce: false, parallel: false);
        var pooled = Replay(encodeOnce: true, parallel: false);
        var plainStatuses = StatusFrames(plain);
        var pooledStatuses = StatusFrames(pooled);

        Assert.NotEmpty(plainStatuses);
        Assert.Equal(plainStatuses, pooledStatuses);          // byte-identical builders

        var aStatus = plainStatuses.Select(m => ShipStatusMessage.Decode(m))
            .Where(s => s.ShipId == a.Id).ToList();
        var bStatus = plainStatuses.Select(m => ShipStatusMessage.Decode(m))
            .Where(s => s.ShipId == b.Id).ToList();
        Assert.NotEmpty(aStatus);
        Assert.NotEmpty(bStatus);
        // True for the enemy-seen ship, false for the unseen one — these are the
        // own-ship copy AND the teammate copy (same per-subject value).
        Assert.All(aStatus, s => Assert.True(s.Detected, "A is seen by the enemy side"));
        Assert.All(bStatus, s => Assert.False(s.Detected, "nobody sees B; the old server still sent true"));

        // The parallel build reads the same per-tick change set; it must stay
        // byte-identical too (the HUD-17 resend gate is read-only there).
        server.TestResetSnapshotReplay();
        var pooledParallel = Replay(encodeOnce: true, parallel: true);
        Assert.Equal(plainStatuses, StatusFrames(pooledParallel));
    }
}
