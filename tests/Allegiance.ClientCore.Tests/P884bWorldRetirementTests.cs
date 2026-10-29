using System.Reflection;
using System.Runtime.CompilerServices;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Xunit.Abstractions;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P884b — per-WORLD server state at the game-over rotation, under the RIG'S boot
/// settings (<c>broadcast.shipUpdateModel=native</c>, immutable snapshot +
/// encode-once, parallel build, pool transport, no interest tiers), which is the
/// configuration P884 did not use.
/// <para><b>Invariants pinned here.</b>
/// <list type="number">
/// <item>A retired world's <see cref="Mission"/> and its ships are collectable
/// after the rotation + a forced full GC (nothing of the old IGC world may
/// survive: native resets the SAME <c>CfsMission</c>, <c>FedSrv/fsmission.cpp:3490-3527</c>,
/// and <c>CmissionIGC::ResetMission</c> destroys every cluster/ship binding,
/// <c>Igc/missionigc.cpp:3804-3828</c>).</item>
/// <item>No per-world, ship-keyed server cache still names a retired ship after
/// the rotation — in particular native's per-ship <c>m_atu</c>
/// (<c>FedSrv/fsship.h:541</c>), which the port keeps in
/// <c>_lastTurretUpdates</c> and which a long match fills with one entry per
/// gunner ship id.</item>
/// <item>A read path (an inbound VOTE, the per-tick ballot resolution) never
/// creates per-mission state: an empty ballot list for a retiring world would
/// keep that retired <see cref="Mission"/> reachable for the rest of the
/// process.</item>
/// <item>Retiring ONE world does not wipe another world's caches (a server can
/// host several missions, P883b) — the P884 clears were server-wide.</item>
/// </list></para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P884bWorldRetirementTests(ITestOutputHelper output)
{
    private const int Peers = 24;
    private const int Rotations = 4;

    /// <summary>The rig's boot settings (Claude's 200v200 rig 221): native ship
    /// updates, immutable snapshot + encode-once, parallel build (bounded), pool
    /// transport, no interest tiers, native NPC travel.</summary>
    private static ServerConfig RigConfig(bool portModel = false, int games = 1, int peersPerTeam = Peers)
    {
        var config = new ServerConfig
        {
            Port = 0,
            SideSelectAutoSeatSeconds = 0.2,   // ConnectionSession peers never send POSITIONREQ
            Broadcast = new()
            {
                InterestTiers = false,
                ImmutableSnapshot = true,
                EncodeOnce = true,
                ParallelBuild = true,
                MaxDegreeOfParallelism = 4,
                ShipUpdateModel = portModel ? "port" : "native",
            },
            Transport = new() { Mode = "pool", PoolWorkers = 4, MaxConnections = 256, MaxConnectionsPerIp = 256 },
            Sim = new() { NativeGotoPlan = true },
            LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
            Games = [],
        };
        for (int i = 0; i < games; i++)
            config.Games.Add(new()
            {
                Name = $"P884b-{i}", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"],
                MaxTeamImbalance = 32767, MaxPlayersPerTeam = peersPerTeam,
                MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 20, PlayerSectorSpecialAsteroids = 8 },
            });
        return config;
    }

    private static bool Read(ConnectionSession peer, Func<bool> predicate)
    { lock (peer.Gate) return predicate(); }

    private static T ReadValue<T>(ConnectionSession peer, Func<T> get)
    { lock (peer.Gate) return get(); }

    private static void Pump(Srv server, Func<bool> done, int max = 20_000)
    {
        for (int i = 0; i < max; i++)
        {
            if (done()) return;
            server.TickSim();
            Thread.Sleep(1);
        }
        Assert.True(done(), "loopback condition timed out");
    }

    private static bool PendingRotationsEmpty(Srv server)
    {
        var list = (System.Collections.ICollection)typeof(Srv)
            .GetField("_pendingRotations", BindingFlags.Instance
                | BindingFlags.NonPublic)!.GetValue(server)!;
        return list.Count == 0;
    }

    private static void End(Srv server, Mission mission, string reason) => typeof(Srv)
        .GetMethod("DeclareGameOver", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(server, [mission, (short)0, reason]);

    private static ConnectionSession Connect(Srv server, string name, ulong id, uint cookie = uint.MaxValue)
    {
        var peer = new ConnectionSession();
        peer.Connect(server.BoundPort, name);
        peer.SendLogonRequest(new()
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = cookie /* NA (uint.MaxValue) = first mission; else the mission cookie */,
            CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = id,
        });
        Pump(server, () => Read(peer, () => peer.World.MyShipId is not null && peer.World.MySideId is not null));
        return peer;
    }

    private static void ReadyAll(Srv server, Mission mission, IReadOnlyList<ConnectionSession> peers)
    {
        foreach (var peer in peers)
            peer.SendPlayerReady(true, ReadValue(peer, () => peer.World.MyShipId!.Value));
        Pump(server, () => mission.Stage == MissionStage.Started);
    }

    /// <summary>A full blocking gen2 collection (twice, with the finalizer queue
    /// drained between), so "still alive" means genuinely reachable.</summary>
    private static void ForceGc()
    {
        for (int i = 0; i < 2; i++)
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private readonly record struct RotationRow(
        WeakReference MissionRef, WeakReference ShipRef, short[] ShipIds,
        bool VoteCreatedBallotList, (int Ballots, int TurretUpdates, int ShipDetected, int LoadoutCache,
            int PendingTurretRelays, int AlliedRearmPending, int AlliedPodRedirectPending) Caches);

    /// <summary>One whole rotation (ready → plant native per-ship state → VOTE →
    /// game over → drain → settle) and hand back only plain data + weak
    /// references. NoInlining and NO Mission local in the caller: in Debug builds
    /// a test-frame local lives to the end of its scope, so holding the world in
    /// the test method would itself keep the retired Mission alive and make the
    /// WeakReference assertion meaningless.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RotationRow Rotate(Srv server, List<ConnectionSession> peers, int rot)
    {
        var mission = server.Missions[0];
        Pump(server, () => peers.TrueForAll(p => Read(p, () => p.World.MyShipId is not null)));
        ReadyAll(server, mission, peers);

        // Plant native's per-ship m_atu (CFSShip::m_atu, fsship.h:541) on a live
        // ship through its production entry point: in a long match every gunner
        // that ever fired leaves one entry for a ship id.
        var gunner = mission.GetShips().First(s => !s.IsNpc);
        server.RecordAcceptedActiveTurretUpdate(gunner, (uint)(rot * 1000), default, gunner.Orientation);

        // A VOTE for an unknown ballot must not materialise per-mission state
        // (P884b: the read path used to create the ballot list).
        peers[0].SendVote(1234, true);
        for (int i = 0; i < 3; i++) server.TickSim();
        bool voteCreatedBallotList = server.TestHasBallotList(mission);

        mission.Stage = MissionStage.Started;
        var ships = mission.GetShips().Where(s => !s.IsNpc).Take(4).ToArray();
        Assert.NotEmpty(ships);
        var missionRef = new WeakReference(mission);
        var shipRef = new WeakReference(ships[0]);
        var ids = ships.Select(s => s.Id).ToArray();
        End(server, mission, $"P884b rotation {rot}");
        Pump(server, () => PendingRotationsEmpty(server));
        // The fresh match runs for a moment so its own caches populate the same
        // way every match does (otherwise "no growth" would be trivially true).
        for (int i = 0; i < 60; i++) { server.TickSim(); Thread.Sleep(2); }
        var c = server.WorldCacheCounts();
        return new RotationRow(missionRef, shipRef, ids, voteCreatedBallotList,
            (c.Ballots, c.LastTurretUpdates, c.ShipDetected, c.TeammateLoadoutCache,
             c.PendingTurretRelays, c.AlliedRearmPending, c.AlliedPodRedirectPending));
    }

    [SkippableFact, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    public void Rotation_retires_every_per_world_ship_cache_under_the_rig_settings()
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(RigConfig());
            var peers = new List<ConnectionSession>();
            for (int i = 0; i < Peers; i++)
                peers.Add(Connect(server, $"P884b-p{i:D3}", 884_200ul + (ulong)i));

            var rows = new List<string>();
            (int Ballots, int TurretUpdates, int ShipDetected, int LoadoutCache) first = default;
            for (int rot = 1; rot <= Rotations; rot++)
            {
                var row = Rotate(server, peers, rot);
                ForceGc();

                Assert.False(row.VoteCreatedBallotList,
                    $"rotation {rot}: an inbound VOTE for an unknown ballot created a ballot list for the live mission — " +
                    "read paths must never create per-mission state (a retired world's list keeps it reachable)");
                Assert.False(row.MissionRef.IsAlive,
                    $"rotation {rot}: the retired Mission is still reachable after a forced full GC");
                Assert.False(row.ShipRef.IsAlive,
                    $"rotation {rot}: a retired world's ship is still reachable after a forced full GC");
                foreach (var id in row.ShipIds)
                    Assert.False(server.WorldCacheContainsShip(id),
                        $"rotation {rot}: a per-world ship cache still names retired ship {id} " +
                        "(native m_atu/Detected/loadout state lives on the ship, fsship.h:541 — " +
                        "the retired world's ids must not survive the reset)");

                // A .NET hash container keeps the backing arrays of the widest
                // moment it ever saw, even after the entries are pruned; the
                // rotation must release that peak too, or the floor ratchets up by
                // one match's peak difference every match. The fresh match has only
                // just started, so its live population is small.
                var caps = server.WorldCacheCapacities();
                Assert.True(caps.DiscoveryCapacity <= 4 * caps.DiscoveryCount + 64,
                    $"rotation {rot}: the discovery latch kept its finished-match peak capacity " +
                    $"({caps.DiscoveryCapacity} slots for {caps.DiscoveryCount} live entries)");
                Assert.True(caps.ShipDetectedCapacity <= 4 * server.WorldCacheCounts().ShipDetected + 64,
                    $"rotation {rot}: _shipDetected kept its finished-match peak capacity");

                var c = row.Caches;
                if (rot == 1) first = (c.Ballots, c.TurretUpdates, c.ShipDetected, c.LoadoutCache);
                Assert.True(c.Ballots <= first.Ballots,
                    $"rotation {rot}: the ballot table grew with match count ({first.Ballots} -> {c.Ballots})");
                Assert.True(c.TurretUpdates <= first.TurretUpdates,
                    $"rotation {rot}: _lastTurretUpdates grew with match count ({first.TurretUpdates} -> {c.TurretUpdates})");
                Assert.True(c.ShipDetected <= first.ShipDetected,
                    $"rotation {rot}: _shipDetected grew with match count ({first.ShipDetected} -> {c.ShipDetected})");
                Assert.True(c.LoadoutCache <= first.LoadoutCache,
                    $"rotation {rot}: _teammateLoadoutCache grew with match count ({first.LoadoutCache} -> {c.LoadoutCache})");
                Assert.Equal(0, c.PendingTurretRelays);
                Assert.Equal(0, c.AlliedRearmPending);
                Assert.Equal(0, c.AlliedPodRedirectPending);
                rows.Add($"rot={rot} ballots={c.Ballots} turretUpdates={c.TurretUpdates} " +
                         $"shipDetected={c.ShipDetected} loadoutCache={c.LoadoutCache} " +
                         $"oldMissionAlive={row.MissionRef.IsAlive} oldShipAlive={row.ShipRef.IsAlive}");
            }
            output.WriteLine(string.Join(Environment.NewLine, rows));
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { Console.SetOut(saved); }
    }

    [SkippableFact, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    public void Rotating_one_world_leaves_another_worlds_per_world_caches_alone()
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            // Port ship-update model: only that path fills the P332 teammate
            // position latch, which is the biggest per-(recipient, ship) cache the
            // P884 clear was aimed at.
            server.Boot(RigConfig(portModel: true, games: 2, peersPerTeam: 8));
            Assert.Equal(2, server.Missions.Count);
            Mission missionA = server.Missions[0], missionB = server.Missions[1];

            var peersA = new List<ConnectionSession>();
            var peersB = new List<ConnectionSession>();
            for (int i = 0; i < 8; i++)
                peersA.Add(Connect(server, $"P884b-a{i:D2}", 884_400ul + (ulong)i, missionA.Cookie));
            for (int i = 0; i < 8; i++)
                peersB.Add(Connect(server, $"P884b-b{i:D2}", 884_500ul + (ulong)i, missionB.Cookie));
            Pump(server, () => peersA.TrueForAll(p => Read(p, () => p.World.MyShipId is not null)));
            Pump(server, () => peersB.TrueForAll(p => Read(p, () => p.World.MyShipId is not null)));
            ReadyAll(server, missionA, peersA);
            ReadyAll(server, missionB, peersB);
            // Let both worlds populate their teammate latches.
            for (int i = 0; i < 40; i++) { server.TickSim(); Thread.Sleep(1); }

            var idsA = missionA.GetShips().Where(s => !s.IsNpc).Select(s => s.Id).ToArray();
            var idsB = missionB.GetShips().Where(s => !s.IsNpc).Select(s => s.Id).ToArray();
            Assert.NotEmpty(idsA);
            Assert.NotEmpty(idsB);
            var before = server.WorldCacheCounts();
            int beforePairsB = server.TestTeammatePositionPairs(missionB);
            Assert.True(before.TeammatePositionSent > 0 && beforePairsB > 0,
                "the P332 teammate position latch stayed empty — the port ship-update path was not exercised");
            foreach (var id in idsB)
                Assert.True(server.WorldCacheContainsShip(id), $"mission B ship {id} was never cached; the scoping assertion would prove nothing");

            // Rotate ONLY mission A, then inspect immediately (before any tick can
            // repopulate): another world's caches must be untouched.
            End(server, missionA, "P884b multi-mission rotation");
            var after = server.WorldCacheCounts();
            foreach (var id in idsB)
                Assert.True(server.WorldCacheContainsShip(id),
                    $"rotating mission A cleared mission B's per-world cache for ship {id} — the rotation must scope " +
                    "its retirement to the world it retires (P883b multi-mission server)");
            int afterPairsB = server.TestTeammatePositionPairs(missionB);
            Assert.Equal(beforePairsB, afterPairsB);
            Assert.True(after.TeammatePositionSent < before.TeammatePositionSent,
                "rotating mission A did not retire mission A's own teammate latches");

            // ...while mission A's own retired ids are gone.
            Pump(server, () => PendingRotationsEmpty(server));
            ForceGc();
            foreach (var id in idsA)
                Assert.False(server.WorldCacheContainsShip(id),
                    $"retired mission A ship {id} still appears in a per-world cache after the rotation");
            Assert.False(server.TestHasBallotList(missionA), "a retired mission keeps a ballot table entry");
            Assert.Equal(0, server.SimPassStageFailures);
            output.WriteLine($"scoped: positionLatchBefore={before.TeammatePositionSent} " +
                             $"after={after.TeammatePositionSent} missionAShips={idsA.Length} missionBShips={idsB.Length}");
        }
        finally { Console.SetOut(saved); }
    }
}
