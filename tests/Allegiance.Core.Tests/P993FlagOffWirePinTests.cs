using System.Security.Cryptography;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P993 — the flag-OFF wire pin for <c>broadcast.fireAudienceCull</c>. With the
/// flag off (the default) the <c>PROJECTILE_INSTANCE(12)</c> relay, the
/// <c>PROJECTILE_HULL_HIT(213)</c> relay and the whole
/// <c>EnsureEmissionPose</c> dependency must be <b>byte-identical</b> to the
/// pre-P993 build: the same fixture, replayed through the real sim and the whole
/// broadcast suffix, must publish the exact same per-recipient frame stream.
/// <para><b>How the pin was made.</b> This file deliberately uses ONLY seams that
/// exist in the pre-P993 tree (no reference to the new flag, its resolver or its
/// helper), so the identical scenario was replayed on the base commit
/// (<c>bdbf8423</c>, a pristine <c>git archive</c> copy of it) and on this tree;
/// the digest below is the BASE build's value and both runs agree. It double-pins
/// the two flag-off invariants: the serial and the parallel builder hash to the
/// same value, so the cull cannot have introduced a build-order dependency.</para>
/// <para>The fixture is a real <see cref="Srv"/> boot (PCore001 + XXV_simple,
/// 20 Hz, <c>shipUpdateModel = native</c>), 12 peers on two opposed lines 600 u
/// apart, four of them firing a real mounted weapon with a seeded mission RNG, so
/// the window contains 12 (rounds), 213 (landed rounds), 78 (the 0.5 s fire pose
/// refresh) and the first-sight 26/90 frames the emission path orders ahead of a
/// round.</para>
/// </summary>
[Collection("LiveNetwork")]   // boots a real in-process server; keep it serialized with other live suites
public sealed class P993FlagOffWirePinTests
{
    // Base-commit digest of the scenario below (SHA-256 over every captured frame:
    // tick, recipient, coalesce key, guaranteed, length, bytes), plus the
    // frame/byte totals. A change here means the flag-off wire changed.
    private const string OffDigest = "B4EE423CF3463009A0F78153E242610355C5557D3DF801AE0B8780F5EBD9B955";
    private const int OffFrames = 2463;
    private const int OffBytes = 147081;

    private sealed record Frame(uint Tick, uint Peer, int Key, bool Guaranteed, byte[] Bytes);

    private static ServerConfig Config(bool parallel) => new()
    {
        ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0, UpdatesPerSecond = 20,
        Broadcast = new() { MinUpdateHz = 2, ShipUpdateModel = "native", ParallelBuild = parallel, MaxDegreeOfParallelism = 4 },
        Games = [new() { Name = "P993 pin", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3],
            SideNames = ["Alpha", "Bravo"] }],
    };

    private static (List<Frame> Frames, string Digest) Replay(bool parallel)
    {
        using var server = new Srv { SpawnInitialMinersAtBoot = false, SimulationRandom = new Random(993) };
        server.Boot(Config(parallel));
        var mission = server.TestMission!;
        mission.Random = new Random(993);              // weapon dispersion: deterministic bytes
        mission.Stage = MissionStage.Started;
        var core = mission.Core;
        var sector = server.TestSectors[0];

        // A fighter hull with real fixed hardpoints, armed from the hull's own
        // preferred weapon list (the native drone loadout).
        var hull = core.HullTypes[55];                 // "Fighter" (PCore001)
        DataPartTypeIGC weaponPart = default;
        foreach (short id in hull.PreferredPartsTypes)
        {
            if (id == -1) break;
            if (core.PartTypes.TryGetValue(id, out var candidate) && candidate.EquipmentType == EquipmentType.Weapon)
            {
                weaponPart = candidate;
                break;
            }
        }
        var weapon = (DataPartTypeIGC.Weapon)weaponPart.Type;

        const int peers = 12;
        var ships = new List<Ship>(peers);
        var peerList = new List<(ConnectionId, Ship, Sector)>(peers);
        for (int i = 0; i < peers; i++)
        {
            short side = (short)(i % 2);
            int rank = i / 2;
            // Two opposed lines 600 u apart, far from every map asteroid so the
            // mining pass never triggers on a parked combat fixture.
            var orientation = Orientation.Identity;
            if (side == 1) orientation.Yaw(MathF.PI);
            var ship = new Ship((short)(10000 + i), hull, new Vector3(50000f + rank * 150f, 0, side == 0 ? 300f : -300f),
                Vector3.Zero, orientation) { SideId = side };
            mission.PlaceShip(ship, sector);
            ship.MountWeapon(weapon, 0);
            ships.Add(ship);
            peerList.Add((new ConnectionId((uint)(700 + i)), ship, sector));
        }

        server.TestResetSnapshotReplay();
        server.TestSeedPeers(peerList);
        var frames = new List<Frame>();
        uint current = 0;
        server.TestPublicationCapture = (peer, bytes, key, guaranteed) =>
            frames.Add(new(current, peer.Value, key, guaranteed, bytes));
        double now = mission.LastUpdate;
        server.TestUpdateCadenceClock = () => now;
        for (int i = 0; i < 4; i++) ships[i].StateM = ShipControlState.AllWeapons;
        try
        {
            for (current = 0; current < 24; current++)
            {
                now += 1.0 / 20.0;
                // Sustained fire: keep every ship alive and the shooters loaded so
                // the cadence is the weapon's own dtimeBurst for the whole window.
                foreach (var ship in ships)
                {
                    ship.SetFraction(1f);
                    ship.Ammo = hull.MaxAmmo;
                    ship.Energy = ship.EffectiveMaxEnergy();
                }
                mission.Update(now);
                server.TestProjectRecordedTick(current, snapshot: true);
                server.FlushBroadcastSnapshots();
            }
        }
        finally { server.TestPublicationCapture = null; }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // Per-recipient order is the wire contract; across peers the parallel build
        // may interleave, so the digest orders by (tick, peer) with a stable sort.
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

    /// <summary>The fixture is only meaningful if it actually carries the two lanes
    /// and the dependency; assert that first so a digest match can never be a
    /// match of two empty streams.</summary>
    private static void AssertFixtureCarriesGunfire(List<Frame> frames)
    {
        long Count(int fmid) => frames.Sum(f => FedPacket.Unpack(f.Bytes)
            .Count(m => (ushort)(m[2] | m[3] << 8) == fmid));
        Assert.True(Count(ProjectileInstance.Fmid) > 0, "the pin fixture fired no PROJECTILE_INSTANCE(12)");
        Assert.True(Count(ProjectileHullHit.Fmid) > 0, "the pin fixture landed no PROJECTILE_HULL_HIT(213)");
        Assert.True(Count(SingleShipUpdate.Fmid) > 0, "the pin fixture has no SINGLE_SHIP_UPDATE(78) dependency");
        Assert.True(Count(ExportShip.Fmid) > 0, "the pin fixture has no first-sight EXPORT(26)");
        Assert.True(Count(ShipStatusMessage.Fmid) > 0, "the pin fixture has no first-sight SHIP_STATUS(90)");
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Flag_off_wire_is_byte_identical_to_the_pinned_base_commit_transcript()
    {
        Require.Artwork();
        var (frames, digest) = Replay(parallel: false);
        AssertFixtureCarriesGunfire(frames);
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
        foreach (var peer in serialFrames.Select(f => f.Peer).Distinct().OrderBy(p => p))
        {
            var left = serialFrames.Where(f => f.Peer == peer).ToArray();
            var right = parallelFrames.Where(f => f.Peer == peer).ToArray();
            Assert.Equal(left.Select(f => (f.Tick, f.Key, f.Guaranteed)), right.Select(f => (f.Tick, f.Key, f.Guaranteed)));
            Assert.Equal(left.Select(f => f.Bytes), right.Select(f => f.Bytes));
        }
    }
}
