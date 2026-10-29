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
/// P884 — the per-match server retention across game-over rotation.
/// <para><b>The leak.</b> <c>_teammatePositionSent</c> (P332's "this
/// (recipient, ship) pair has had its first position" latch) was only ever
/// ADDED to: neither <c>ReleasePeer</c> nor the game-over rotation's
/// <c>RunMigrationLatchSweep</c> pruned it, so every finished match left one
/// entry per (recipient, same-side ship) behind forever, and every departed
/// peer's pairs stayed as well. At the rig's 394 peers that is ~77k entries
/// per match with an idle fleet and hundreds of thousands in a real
/// 200v200 match (bot relaunches mint new ship ids every death), i.e. the
/// rig's "+20 MB per match, never returns". <c>_teammateLoadoutCache</c> (one
/// snapshot per subject ship) and <c>_crossSideShipStatusSeen</c> (one
/// visibility edge per (side, subject ship)) were keyed by old-world ship ids
/// too and only partly pruned (migrant ships only, never a retired NPC), so
/// they also carried a little of the old world forward each match.
/// <para><b>The invariant this test pins.</b> After the rotation has drained
/// and the fresh match has populated its own latches, the count of each
/// per-world cache must NOT grow with the number of matches played, and the
/// retired <see cref="Mission"/> (and one of its ships) must be collectable —
/// native keeps nothing of the old IGC world across a reset (it resets the
/// SAME CfsMission, <c>FedSrv/fsmission.cpp:3490-3527</c>, and the port rotates
/// to a freshly assembled world, so a retired world must become unreachable).</para>
/// <para>Uses the port ship-update model and no interest tiers, because the
/// P332 position latch only exists on that path
/// (<c>Server.Broadcast.cs</c> <c>!_nativeShipUpdates &amp;&amp;
/// !_broadcastInterestTiersEnabled</c>, the default config).</para>
/// </summary>
[Collection("LiveNetwork")]
public sealed class P884RotationRetentionTests(ITestOutputHelper output)
{
    private const int Peers = 24;
    private const int Rotations = 4;

    private static ServerConfig Config() => new()
    {
        Port = 0,
        SideSelectAutoSeatSeconds = 0.2,   // ConnectionSession peers never send POSITIONREQ
        // P887 — this suite exercises the PORT teammate latch (per-tick
        // SingleShipUpdate posBatch retention across game-over rotation), so it
        // pins the old ship-update model explicitly now that "native" is the
        // server default (the native model suppresses the posBatch stream).
        Broadcast = new() { ShipUpdateModel = "port" },
        Transport = new() { MaxConnections = 256, MaxConnectionsPerIp = 256 },
        LiveSettings = new() { TimeDilation = "off", AutoNextMatch = new() { Enabled = false } },
        Games = [new()
        {
            Name = "P884", Teams = 2, CivIds = [25, 3], SideNames = ["Alpha", "Bravo"],
            MaxTeamImbalance = 32767, MaxPlayersPerTeam = Peers,
            MapGen = new() { Type = "SingleRing", PlayerSectorAsteroids = 20, PlayerSectorSpecialAsteroids = 8 },
        }],
    };

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
            .GetField("_pendingRotations", System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic)!.GetValue(server)!;
        return list.Count == 0;
    }

    private static void End(Srv server, string reason) => typeof(Srv)
        .GetMethod("DeclareGameOver", System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(server, [server.Missions[0], (short)0, reason]);

    private static ConnectionSession Connect(Srv server, string name, ulong id)
    {
        var peer = new ConnectionSession();
        peer.Connect(server.BoundPort, name);
        peer.SendLogonRequest(new()
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
            Time = 1, DwCookie = uint.MaxValue /* NA: P883 refuses unknown cookies */, CrcFileList = -1, SteamAuthTicket = new byte[16], SteamID = id,
        });
        Pump(server, () => Read(peer, () => peer.World.MyShipId is not null && peer.World.MySideId is not null));
        return peer;
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

    /// <summary>Rotate once and hand back weak references to the retired world.
    /// NoInlining + nulled locals so the test frame itself never roots them.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Mission, WeakReference Ship) Rotate(Srv server, int rot)
    {
        var old = server.Missions[0];
        old.Stage = MissionStage.Started;
        var oldShip = old.GetShips().First(s => !s.IsNpc);
        var missionRef = new WeakReference(old);
        var shipRef = new WeakReference(oldShip);
        End(server, $"P884 rotation {rot}");
        Pump(server, () => PendingRotationsEmpty(server));
        // The fresh match runs for a moment so its own latches populate the same
        // way every match does (otherwise "no growth" would be trivially true).
        for (int i = 0; i < 60; i++) { server.TickSim(); Thread.Sleep(2); }
        old = null!;
        oldShip = null!;
        return (missionRef, shipRef);
    }

    [SkippableFact, Trait("Tier", "Live"), Trait("Requires", "Artwork")]
    public void Rotation_does_not_grow_per_world_caches_and_retires_the_old_world()
    {
        Require.Artwork();
        var saved = Console.Out;
        using var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            using var server = new Srv();
            server.SpawnInitialMinersAtBoot = false;
            server.Boot(Config());
            var conns = new List<ConnectionSession>();
            for (int i = 0; i < Peers; i++)
                conns.Add(Connect(server, $"P884-p{i:D3}", 884_000ul + (ulong)i));

            var first = default((int TeammatePositionSent, int TeammateLoadoutCache, int CrossSideShipStatusSeen));
            var rows = new List<string>();
            for (int rot = 1; rot <= Rotations; rot++)
            {
                // Migrated peers get a fresh ship on the rotation burst, so wait
                // for it before readying the next match (P807/P480 rotation).
                Pump(server, () => conns.TrueForAll(peer => Read(peer, () => peer.World.MyShipId is not null)));
                foreach (var peer in conns)
                    peer.SendPlayerReady(true, ReadValue(peer, () => peer.World.MyShipId!.Value));
                Pump(server, () => server.Missions[0].Stage == MissionStage.Started);

                var (missionRef, shipRef) = Rotate(server, rot);
                ForceGc();
                var counts = server.ConnectionScopedCacheCounts();
                if (rot == 1)
                    first = (counts.TeammatePositionSent, counts.TeammateLoadoutCache, counts.CrossSideShipStatusSeen);
                rows.Add($"rot={rot} positionLatch={counts.TeammatePositionSent} " +
                         $"loadoutCache={counts.TeammateLoadoutCache} " +
                         $"crossSideSeen={counts.CrossSideShipStatusSeen} " +
                         $"oldMissionAlive={missionRef.IsAlive} oldShipAlive={shipRef.IsAlive}");

                // 1. The retired world is unreachable after the rotation: nothing
                // of the old IGC world may survive a reset (fsmission.cpp:3490-3527).
                Assert.False(missionRef.IsAlive,
                    $"rotation {rot}: the retired Mission is still reachable after a forced full GC");
                Assert.False(shipRef.IsAlive,
                    $"rotation {rot}: a retired world's ship is still reachable after a forced full GC");

                // 2. No per-world cache grows with the number of matches played.
                Assert.True(first.TeammatePositionSent > 0,
                    "the P332 position latch stayed empty — the port ship-update path was not exercised, so this test would prove nothing");
                Assert.True(counts.TeammatePositionSent <= first.TeammatePositionSent,
                    $"rotation {rot}: _teammatePositionSent grew with match count " +
                    $"({first.TeammatePositionSent} after match 1, {counts.TeammatePositionSent} after match {rot}); " +
                    "the finished world's (recipient, ship) pairs are never pruned");
                Assert.True(counts.TeammateLoadoutCache <= first.TeammateLoadoutCache,
                    $"rotation {rot}: _teammateLoadoutCache grew with match count " +
                    $"({first.TeammateLoadoutCache} after match 1, {counts.TeammateLoadoutCache} after match {rot}); " +
                    "retired ships' loadout snapshots outlive their world");
                Assert.True(counts.CrossSideShipStatusSeen <= first.CrossSideShipStatusSeen,
                    $"rotation {rot}: _crossSideShipStatusSeen grew with match count " +
                    $"({first.CrossSideShipStatusSeen} after match 1, {counts.CrossSideShipStatusSeen} after match {rot})");
            }
            output.WriteLine(string.Join(Environment.NewLine, rows));
            Assert.Equal(0, server.SimPassStageFailures);
        }
        finally { Console.SetOut(saved); }
    }
}
