using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.State;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P321 — a retry after a failed lobby join must not inherit anything from the
/// previous connection attempt. Two leaks were fixed: the previous
/// <see cref="TcpTransport"/> was abandoned without <c>Shutdown()</c> (its
/// socket + receive thread leaked for the process lifetime), and
/// <see cref="ClientWorld"/> was never reset (so <see cref="ClientWorld.MyShipId"/>
/// — a first-assignment-wins <c>??=</c> — and every entity table persisted from
/// the dead server into the new session).
///
/// This file carries the regression coverage: a deterministic pure-domain test
/// that <see cref="ClientWorld.Reset"/> clears everything, plus two LIVE proofs
/// against real loopback TCP — that 10 consecutive connects do not accumulate
/// sockets/threads (each retry tears down the previous transport), and that a
/// retry to a real in-process server starts from a genuinely empty world and
/// re-seats with the new connection's real assignment.
/// </summary>
public class RetryCleanupTests
{
    private static DataHullTypeIGC TestHull() => new(
        Price: 0, TimeToBuild: 0, ModelName: "mdl", IconName: "ico", Name: "test",
        Description: "", GroupId: 0, TtbmRequired: [], TtbmEffects: [],
        Mass: 550f, Signature: 0f, Speed: 120f,
        MaxTurnRates: [4.5f, 4.5f, 4.5f],
        TurnTorques: [5000f, 5000f, 5000f],
        Thrust: 52500f, SideMultiplier: 4f, BackMultiplier: 4f, ScannerRange: 1000f,
        MaxFuel: 100f, Ecm: 0f, Length: 30f, MaxEnergy: 0f, RechargeRate: 0f,
        RipcordSpeed: 0f, RipcordCost: 0f, MaxAmmo: 0, HullId: 501, SuccessorHullId: -1,
        MaxWeapons: 0, MaxFixedWeapons: 0, HitPoints: 518f, HardpointOffset: 540,
        DefenseType: 0, CapacityMagazine: 0, CapacityDispenser: 0, CapacityChaffLauncher: 0,
        PreferredPartsTypes: [], HabmCapabilities: 0, TextureName: "", PmEquipment: [],
        InteriorSound: 0, ExteriorSound: 0, MainThrusterInteriorSound: 0,
        MainThrusterExteriorSound: 0, ManuveringThrusterInteriorSound: 0,
        ManuveringThrusterExteriorSound: 0, Hardpoints: []);

    private static Ship MakeShip(short id, Vector3 position) =>
        new(id, TestHull(), position, Vector3.Zero, Orientation.Identity);

    /// <summary>
    /// Populate as much of the world as a single connection would, across every
    /// distinct table/snapshot, then prove <see cref="ClientWorld.Reset"/> returns
    /// the world to its construction-pristine state (no stale ship id, no stale
    /// entities). Pure domain test — no sockets, fast, deterministic.
    /// </summary>
    [Fact]
    public void ClientWorld_Reset_ClearsMyShipIdAndAllEntityTables()
    {
        var world = new ClientWorld();
        const short myId = 9001;

        // A SET_CLUSTER seats us (sets MyShipId, adds a my-ship)…
        world.Apply(new SetCluster
        {
            SectorId = 7,
            Ship = ServerShipUpdate.FromModel(myId, MakeShip(myId, new Vector3(1f, 2f, 3f)), 99u),
            Cookie = 0x2A,
        });
        // …the own-ship EXPORT sets MySideId, and a teammate's EXPORT/SHIP_STATUS
        // add second entities to the same table.
        world.Apply(new ExportShip { ShipId = -1, SideId = 5 });
        world.Apply(new ExportShip { ShipId = 8600, SideId = 8585, CharacterName = "alice" });
        world.Apply(new ShipStatusMessage { ShipId = 8700, HullId = 901, SectorId = 7, StationId = -1 });

        Assert.Equal((short)myId, world.MyShipId);
        Assert.Equal((short)5, world.MySideId);
        Assert.Equal(3, world.Ships.Count);                 // me + 2 others
        Assert.NotEmpty(world.Ships);
        Assert.NotNull(world.Ships[myId].Latest);           // my-ship hydrated

        world.Reset();

        Assert.Null(world.MyShipId);                        // stale first-assignment gone
        Assert.Null(world.MySideId);
        Assert.Empty(world.Ships);                          // no stale entity tables
        Assert.Null(world.SideRoster);
        Assert.Null(world.MissionOwnerBroadcast);
        Assert.Null(world.Graph);
        Assert.Null(world.Mission);
        Assert.Null(world.GameOverResult);
        Assert.Empty(world.ScoreRoster);
        Assert.Empty(world.Events.Chats);
        Assert.Empty(world.Roster.ReadyByShipId);
        Assert.Equal("", world.BriefingText);
        Assert.False(world.BriefingFGenerateCivBriefing);
    }

    /// <summary>
    /// P321's reset is caller-driven (fired only from <see cref="ConnectionSession.Connect"/>).
    /// Within ONE live session — sector-transit <c>SET_CLUSTER</c>s reusing the same
    /// ship id, with no Connect in between — the <c>??=</c> first-assignment-wins
    /// semantics are untouched: MyShipId stays the original assignment across
    /// transits, exactly as before P321. Only an explicit Reset (what a fresh
    /// Connect performs) clears it, and afterwards the next SET_CLUSTER re-establishes
    /// MyShipId first-wins for the new connection.
    /// </summary>
    [Fact]
    public void ClientWorld_SameSessionSetClusterKeepsFirstAssignment_OnlyResetClearsIt()
    {
        var world = new ClientWorld();
        const short myId = 7033;

        // Two sector-transit SET_CLUSTERs for OUR OWN ship id within one live session.
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromModel(myId, MakeShip(myId, Vector3.Zero), 1u) });
        world.Apply(new SetCluster { SectorId = 2, Ship = ServerShipUpdate.FromModel(myId, MakeShip(myId, Vector3.Zero), 2u) });

        Assert.Equal((short)myId, world.MyShipId);   // ??= preserved across the transit
        Assert.True(world.Ships[myId].IsMine);
        Assert.Single(world.Ships);                  // same id, still one entity

        // Only an explicit Reset (the Connect-boundary reset) clears the assignment…
        world.Reset();
        Assert.Null(world.MyShipId);

        // …and the next SET_CLUSTER re-seats MyShipId first-wins for the new session.
        const short newId = 7955;
        world.Apply(new SetCluster { SectorId = 3, Ship = ServerShipUpdate.FromModel(newId, MakeShip(newId, Vector3.Zero), 3u) });
        Assert.Equal((short)newId, world.MyShipId);
    }
}

/// <summary>
/// Live network proofs for P321, against the <see cref="ConnectionSession"/> +
/// real <see cref="TcpTransport"/> stack. Serialized with the other live network
/// tests (real in-process server, loopback TCP paced by wall-clock sleeps).
/// </summary>
[Collection("LiveNetwork")]
public class RetriedConnectLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Retry Cleanup", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 912u, DwCookie = 0x512u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 81200000000000000ul, DrmHash = "retrycleanup",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static void Eventually(Func<bool> cond, int ms = 5000, string what = "")
    {
        var sw = Stopwatch.StartNew();
        while (!cond())
        {
            if (sw.ElapsedMilliseconds > ms)
                throw new TimeoutException($"timed out waiting for {what}");
            Thread.Sleep(5);
        }
    }

    /// <summary>
    /// LIVE LEAK PROOF. A real TcpListener accepts connections from a real
    /// <see cref="ConnectionSession"/> that calls <c>Connect</c> 10 times in a row
    /// (each a live-but-failed-join attempt that never completes a logon). Under
    /// the fix each retry calls Shutdown() on the previous transport, so every
    /// accepted socket must be torn down — only the CURRENT (last) connection stays
    /// open. The deterministic no-leak gate is the socket-closure count (a leaked
    /// transport keeps its socket OPEN and its read loop blocked on it, so it would
    /// show as an unclosed accepted client, not a flat count). We then run TWO
    /// identical 10-retry rounds and assert the live thread count is unchanged from
    /// one round to the next — round-to-round cancellation removes thread-pool
    /// churn, so a genuine per-attempt leak (+~2 threads × 10 = ~20) would blow the
    /// budget while self-cleaning retries land at ~0.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RepeatedConnects_LeaveNoAccumulatedSocketsOrThreads()
    {
        Require.Artwork();
        const int attempts = 10;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        // Accept-loop: record each accepted client's index; a background reader
        // watches the stream and records the index as "closed" the instant the
        // session transport closes it (ReadAsync returns 0 / throws).
        var closed = new ConcurrentBag<int>();
        int accepted = 0;
        var acceptTask = Task.Run(async () =>
        {
            while (true)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync(); }
                catch { break; }                       // listener stopped
                int idx = Interlocked.Increment(ref accepted) - 1;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var ns = c.GetStream();
                        var buf = new byte[1];
                        while (await ns.ReadAsync(buf, 0, 1) > 0) { }
                    }
                    catch { /* socket closed under us — that IS the closure */ }
                    finally { closed.Add(idx); c.Dispose(); }
                });
            }
        });

        // Two identical rounds give the flat-thread-count signal via round-to-round
        // cancellation (measured after round A, once the thread pool is warm), so no
        // separate warm-up is needed — and it would shift the accepted-index bookkeeping.
        using var session = new ConnectionSession();

        // Round A: ten consecutive failed-join connects.
        for (int i = 0; i < attempts; i++)
            session.Connect((ushort)port, $"leakA_{i}");
        // Every accepted socket except the still-current last one must be closed.
        Eventually(() => closed.Count == attempts - 1, what: "round-A prior transports shut down");
        Assert.Equal(attempts - 1, closed.Count);      // clients 0..8 closed
        Assert.Equal(attempts, accepted);
        Assert.DoesNotContain(attempts - 1, closed);   // current (index 9) still open
        int threadsAfterA = Process.GetCurrentProcess().Threads.Count;

        // Round B: ten MORE consecutive connects. With self-cleaning retries the
        // live transport count is identical to end-of-round-A (one), so the managed
        // thread count must stay flat round-to-round; a per-attempt leak would add
        // ~2 threads × 10 = ~20 here.
        for (int i = 0; i < attempts; i++)
            session.Connect((ushort)port, $"leakB_{i}");
        Eventually(() => closed.Count == attempts * 2 - 1, what: "round-B prior transports shut down");
        Assert.Equal(attempts * 2 - 1, closed.Count);  // 0..18 closed
        Assert.Equal(attempts * 2, accepted);
        Assert.DoesNotContain(attempts * 2 - 1, closed); // current (index 19) still open
        int threadsAfterB = Process.GetCurrentProcess().Threads.Count;

        int roundDelta = threadsAfterB - threadsAfterA;
        Console.WriteLine($"[P321 leak] threads after round A={threadsAfterA} after round B={threadsAfterB} round-to-round delta={roundDelta}");
        Assert.True(roundDelta <= 4,
            $"managed thread count grew between two identical 10-retry rounds: delta={roundDelta} "
            + "(would be ~20 if each retry leaked a transport)");

        // Tear down the last connection too; every accepted socket must end closed.
        session.Dispose();
        Eventually(() => closed.Count == attempts * 2, what: "last transport shut down on dispose");
        Assert.Equal(attempts * 2, closed.Count);
        listener.Stop();
    }

    /// <summary>
    /// LIVE CLEAN-WORLD PROOF. Against a real in-process server: seat the peer
    /// (populates World.MyShipId + the ship table), then call Connect again — the
    /// retry must start from a GENUINELY EMPTY world (no stale ships, MyShipId
    /// null, so the new connection's assignment is free to win), then re-seat and
    /// repopulate from the fresh connection.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void RetriedConnect_StartsFromEmptyWorld_AndReseatsWithNewAssignment()
    {
        Require.Artwork();
        using var server = new Srv();
        server.Boot(Config());

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "retry_peer");
        session.SendLogonRequest(Request("retry_peer"));

        int firstSeat = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(firstSeat < 4000, "peer was never seated on the first attempt");
        short firstId = session.World.MyShipId!.Value;
        Assert.NotEmpty(session.World.Ships);          // world fully populated by attempt 1
        Assert.Contains(firstId, session.World.Ships.Keys);
        Assert.True(session.World.Ships[firstId].IsMine);
        Console.WriteLine($"[P321 clean] attempt-1 ship id={firstId} ships={session.World.Ships.Count}");

        // Simulate the failed-join-then-retry: a fresh Connect. IMMEDIATELY after
        // Connect returns — before the new logon flow — the world must be empty.
        // (Pre-P321, MyShipId kept firstId via ??= and the ship table persisted.)
        session.Connect(server.BoundPort, "retry_peer2");   // different name: clean peer on the server side
        Assert.Null(session.World.MyShipId);
        Assert.Empty(session.World.Ships);
        Assert.Null(session.World.SideRoster);

        // The retry then logs on and is seated afresh by the real server: MyShipId
        // is whatever THIS connection was assigned (the ??= had to be cleared for
        // that to be possible), and the ship table repopulates from this session.
        session.SendLogonRequest(Request("retry_peer2"));
        int secondSeat = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(secondSeat < 4000, "peer was never re-seated after the retry");

        short secondId = session.World.MyShipId!.Value;
        Console.WriteLine($"[P321 clean] attempt-2 ship id={secondId} ships={session.World.Ships.Count}");
        Assert.Contains(secondId, session.World.Ships.Keys);
        Assert.True(session.World.Ships[secondId].IsMine);   // the new assignment is genuinely OURS
    }
}
