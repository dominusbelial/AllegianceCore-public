using System.Security.Cryptography;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P991 — the flag-OFF wire pin. <c>broadcast.shipStatusChangeGate</c> must be a
/// pure no-op when off: the same seeded world, replayed through the whole
/// broadcast suffix (ship states, the native ranked ship updates, the cross-side
/// status sweep, detection, CLUSTERINFO, teammate/NPC statuses), must produce the
/// EXACT same per-recipient frame stream as the pre-P991 build.
/// <para><b>How the pin was made.</b> This file uses only seams that exist in the
/// pre-P991 tree (no P991 test seam), so the identical scenario was replayed on
/// the base commit and on this tree; the digest below is the BASE build's value
/// and both runs agree. Claude's review stretched the replay from 40 to 101 ticks
/// so all three periodic refreshes fire inside it (own ship every 40 ticks: 40 and
/// 80; teammate and cross-side every 100 ticks, every phase slot) and re-pinned
/// the digest from a clean base-commit (60390d68) worktree. It double-pins the two flag-off invariants: the serial
/// and the parallel builder hash to the same value (P603's byte-identity, re-pinned
/// around the shared status-content encoders P991 introduced).</para>
/// </summary>
[Collection("LiveNetwork")]   // boots real in-process servers; keep serialized with other live suites
public sealed class P991FlagOffWirePinTests
{
    // Base-commit digest of the scenario below (SHA-256 over every captured
    // frame: tick, recipient, coalesce key, guaranteed, length, bytes), plus the
    // frame/byte totals. A change here means the flag-off wire changed.
    private const string OffDigest = "C0A550F304589ED12761004E465072482B5082EB2FD5809AD3B4B0D83146F7BF";
    private const int OffFrames = 18131;
    private const int OffBytes = 1125908;

    private sealed record Frame(uint Tick, uint Peer, int Key, bool Guaranteed, byte[] Bytes);

    private static (List<Frame> Frames, string Digest) Replay(bool parallel)
    {
        using var server = new Srv { SpawnInitialMinersAtBoot = false };
        var config = UpdateCadenceTests.Config();
        config.Broadcast!.ImmutableSnapshot = true;
        config.Broadcast.ParallelBuild = parallel;
        config.Broadcast.MaxDegreeOfParallelism = 4;
        double now = 0;
        server.TestUpdateCadenceClock = () => now;
        server.Boot(config);
        var peers = UpdateCadenceTests.Seed(server, ships: 24, viewers: 24);
        for (int i = 0; i < peers.Count; i++)
            peers[i].Ship.SideId = (short)(i % 2);   // both sides: same-side + cross-side statuses
        server.TestMission!.Stage = MissionStage.Started;
        var sector = server.TestSectors[0];
        var core = server.TestMission.Core;
        var probe = new Probe(25000, sector, core.ProbeTypes.Values.First(), new(10, 20, 30), 0, 0, peers[0].Ship);
        var mine = new Mine(25001, sector, core.MineTypes.Values.First(), new(20, 30, 40), 0, peers[0].Ship);
        sector.Add(probe); sector.Add(mine);
        var npc = new Ship(25003, peers[0].Ship.HullType, new(100, 200, 300), Vector3.Zero, Orientation.Identity)
            { SideId = 0, IsNpc = true };
        server.TestMission.PlaceShip(npc, sector);
        foreach (var station in sector.StationRuntimes)
            for (short side = 0; side < 2; side++) station.SideVisibility.SetSideVisibility(side, true);
        foreach (var asteroid in sector.Mineable)
            for (short side = 0; side < 2; side++) asteroid.SideVisibility.SetSideVisibility(side, true);

        var random = new Random(991);
        var tape = Enumerable.Range(0, 101).Select(tick => peers.Select((p, i) => new
        {
            Position = new Vector3(i * 13 + tick * (i % 3 + 1), (i % 7) * 100, tick * 2),
            Yaw = (float)random.NextDouble(),
            Docked = i % 11 == 0 && tick >= 3 && tick < 9,
        }).ToArray()).ToArray();

        server.TestResetSnapshotReplay();
        server.TestSeedPeers(peers);
        server._broadcastParallelBuild = parallel;
        var frames = new List<Frame>();
        uint current = 0;
        server.TestPublicationCapture = (peer, bytes, key, guaranteed) =>
            frames.Add(new(current, peer.Value, key, guaranteed, bytes));
        try
        {
            for (; current < (uint)tape.Length; current++)
            {
                now = current / 20d;
                for (int i = 0; i < peers.Count; i++)
                {
                    var input = tape[current][i];
                    var ship = peers[i].Ship;
                    ship.Position = input.Position;
                    ship.Orientation.Reset();
                    ship.Orientation.Yaw(input.Yaw);
                    ship.StationId = input.Docked ? (short)1 : null;
                    for (short side = 0; side < 2; side++)
                        ship.SideVisibility.SetSideVisibility(side, side == ship.SideId || (current / 10 + i) % 3 != 0);
                }
                // The whole broadcast suffix in SimPass order (cross-side status
                // sweep, ship detection, ship states, ships update, the rest).
                server.TestProjectRecordedTick(current, snapshot: true);
                server.FlushBroadcastSnapshots();
            }
        }
        finally { server.TestPublicationCapture = null; }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Per-recipient order is the wire contract (and is what makes a peer's
        // stream meaningful); across peers the parallel build may interleave, so
        // the digest orders by (tick, peer) with a stable sort.
        foreach (var f in frames.OrderBy(f => f.Tick).ThenBy(f => f.Peer))
        {
            hash.AppendData(BitConverter.GetBytes(f.Tick));
            hash.AppendData(BitConverter.GetBytes(f.Peer));
            hash.AppendData(BitConverter.GetBytes(f.Key));
            hash.AppendData(BitConverter.GetBytes(f.Guaranteed));
            hash.AppendData(BitConverter.GetBytes(f.Bytes.Length));
            hash.AppendData(f.Bytes);
        }
        return (frames, Convert.ToHexString(hash.GetHashAndReset()));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_off_wire_is_byte_identical_to_the_pinned_base_commit_transcript()
    {
        Require.Artwork();
        var (frames, digest) = Replay(parallel: false);
        int bytes = frames.Sum(f => f.Bytes.Length);
        Assert.True(OffFrames == frames.Count,
            $"flag-off frame count changed: {frames.Count} (pinned {OffFrames}) digest={digest} bytes={bytes}");
        Assert.True(OffBytes == bytes,
            $"flag-off byte total changed: {bytes} (pinned {OffBytes})");
        Assert.True(OffDigest == digest,
            $"flag-off wire transcript changed: digest {digest} frames {frames.Count} bytes {bytes} (pinned {OffDigest})");
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_off_serial_and_parallel_builds_emit_the_same_bytes()
    {
        Require.Artwork();
        var (serialFrames, serialDigest) = Replay(parallel: false);
        var (parallelFrames, parallelDigest) = Replay(parallel: true);
        Assert.Equal(serialFrames.Count, parallelFrames.Count);
        Assert.Equal(serialDigest, parallelDigest);
        Assert.Equal(OffDigest, serialDigest);
        // And the same per-recipient frames, in the same per-recipient order.
        foreach (var peer in serialFrames.Select(f => f.Peer).Distinct().OrderBy(p => p))
        {
            var left = serialFrames.Where(f => f.Peer == peer).ToArray();
            var right = parallelFrames.Where(f => f.Peer == peer).ToArray();
            Assert.Equal(left.Select(f => (f.Tick, f.Key, f.Guaranteed)), right.Select(f => (f.Tick, f.Key, f.Guaranteed)));
            Assert.Equal(left.Select(f => f.Bytes), right.Select(f => f.Bytes));
        }
    }
}
