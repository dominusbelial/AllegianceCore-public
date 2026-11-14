using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P230 — live integration proof of the missile-lock-feedback wire path: the
/// server's per-tick owner-only <c>MISSILE_LOCK_UPDATE(204)</c>
/// (<c>Server.BroadcastMissileLock</c>) reaches a real <see cref="ConnectionSession"/>
/// and <see cref="ClientWorld"/> reflects it — the lock builds partial → full over
/// real sim time, the three native console tips (partial / acquired / lost,
/// <c>console.cpp:935-950</c>) post into the chat log at the right edges, and the
/// client's <see cref="ClientWorld.MissileLock"/> snapshot carries the real lock
/// fraction + target + missile name. Mirrors <see cref="MissileFireLiveTests"/>'s
/// harness (reflective ShipSpawner seeding a mounted missile launcher + an enemy
/// command target), but with a MaxLock &gt; 0 type so the lock actually builds.
/// </summary>
[Collection("LiveNetwork")]
public class MissileLockLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "Missile Lock Client Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 887u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 8890000000000000000ul, DrmHash = "missilelockclient",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    /// <summary>A missile type that REQUIRES a lock (MaxLock &gt; 0) and builds over a
    /// measurable LockTime — so partial → full is observable in the client world.</summary>
    private static DataMissileTypeIGC LockMissile(StaticCore core)
    {
        var t = core.MissileTypes.Values
            .Where(m => m.MaxLock > 0f && m.LockTime >= 1f && m.LockTime <= 3f
                        && m.LockAngle >= 0.1f && m.LockAngle <= 1.0f
                        && m.InitialSpeed > 0f && m.Lifespan >= 2f)
            .OrderBy(m => m.LockTime).FirstOrDefault();
        if (t.LockTime < 1f)
            throw new InvalidOperationException("representative core has no lock-eligible missile type");
        return t;
    }

    /// <summary>⭐ Live round trip: lock builds partial → full in the client world
    /// (real server-computed values over the real transport), the partial + acquired
    /// tips post to the chat log, then dragging the target out of range drops the lock
    /// to 0 (same target id) and posts the "lost" tip.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MissileLock_BuildsPartialThenFull_AndPosts_Tips_OverTheWire()
    {
        Require.Artwork();
        using var server = new Srv();

        Ship? placedShip = null;
        Ship? placedTarget = null;
        string? missileName = null;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First() with { ScannerRange = 20000, Signature = 100 };
            var ship = new Ship(shipId, hull, new Vector3(0f, 5000f, 5000f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);

            // Enemy target, directly ahead at 100 units, in range for a lock.
            var target = new Ship((short)(shipId + 666), hull, new Vector3(100f, 5000f, 5000f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 1) target.SideId = mission.Sides[1].Id; else target.SideId = 99;
            mission.PlaceShip(target, mission.Sectors[0]);
            ship.Orientation.Set((target.Position - ship.Position).Normalize());
            // P1020: NOT targeted yet. The lock builds server-side from the first tick
            // a target is set (magazineigc.cpp:108,123-167), i.e. during the fast join
            // pump below, long before this client is seated and receiving its own
            // lock stream. The test sets the target itself once the client is in sync.

            // Arm with a REAL missile launcher (ET_Magazine@0) naming a MaxLock>0 type.
            var mt = LockMissile(core);
            missileName = mt.ModelName;
            var part = new DataPartTypeIGC(
                Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ml", Description: "",
                GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
                PartId: 703, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
                Type: new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId));
            ship.MountPart(part, amount: 3, mountId: 0, fractionMounted: 255);

            placedShip = ship;
            placedTarget = target;
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "MISSILELOCK");
        session.SendLogonRequest(Request("MISSILELOCK"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "client must be assigned a ship");
        Assert.NotNull(placedShip);
        Assert.NotNull(placedTarget);
        PumpUntil(server, () => placedTarget!.SeenBySide(placedShip!.SideId!.Value), 1000);
        Assert.True(placedTarget!.SeenBySide(placedShip.SideId!.Value), "missile target must be detected");
        Assert.NotNull(missileName);

        // P1020 — start from a state the client has actually observed: armed launcher,
        // no target, lock 0 (the per-tick owner-only stream is already flowing).
        int synced = PumpUntil(server, () => session.World.MissileLock.MissileName == missileName, 4000);
        Assert.True(synced < 4000, "client never received the no-target lock stream");
        Assert.False(session.World.MissileLock.HasTarget);
        Assert.Equal(0f, session.World.MissileLock.LockFraction);
        Assert.Equal(0f, placedShip.MissileLock);
        Assert.Empty(LockTips(session));

        // Target the enemy: the server builds the lock one sim tick at a time
        // (dt / lockTime per tick dead ahead, magazineigc.cpp:158-167). The stream is
        // best-effort and latest-wins (Server.Broadcast.cs CoalesceMissileLock), so
        // the pump is LOCK-STEPPED: the next tick only runs once the client world
        // has applied the frame the previous tick produced. Every edge the server
        // crosses is therefore an edge the client observes, with no dependence on
        // wall-clock pacing or on the receive thread keeping up.
        placedShip.CommandTarget = placedTarget;
        var seen = new List<float>();

        // First tick with the target: partial lock, and ONLY the partial tip
        // (console.cpp:941-943) — posted on the very frame the lock leaves 0.
        int built = LockstepPumpUntil(server, session, placedShip, seen,
            () => session.World.MissileLock.IsPartialLock, 900);
        Assert.Equal(1, built);
        Assert.Equal(["Partial missile lock - center the target on screen to improve the lock."], LockTips(session));

        // Keep building to full: the partial tip is not repeated while the lock
        // holds partial, and "acquired" posts on the frame it reaches 1
        // (console.cpp:944-946).
        built += LockstepPumpUntil(server, session, placedShip, seen,
            () => session.World.MissileLock.IsFullLock, 900);
        Assert.True(session.World.MissileLock.IsFullLock,
            $"lock never reached full in the client world (lock {session.World.MissileLock.LockFraction:F3})");
        Assert.True(seen.Count(f => f is > 0f and < 1f) >= 2,
            $"expected several partial frames, saw [{string.Join(", ", seen.Select(f => f.ToString("F3")))}]");
        Assert.Equal(seen.OrderBy(f => f), seen);                 // monotonic build, frame by frame
        Assert.Equal(1f, seen[^1]);
        Assert.DoesNotContain(1f, seen.Take(seen.Count - 1));     // stopped on the full edge

        var fullState = session.World.MissileLock;
        Assert.True(fullState.HasTarget);
        Assert.Equal(placedTarget.Id, fullState.TargetId);
        Assert.Equal(missileName, fullState.MissileName);
        Assert.InRange(fullState.TargetPosition.X, 90f, 110f);   // target dead ahead ~100

        // Both edge tips posted exactly once each, partial BEFORE acquired, no spam,
        // and not lost yet.
        Assert.Equal(
            ["Partial missile lock - center the target on screen to improve the lock.",
             $"Missile lock acquired - press Left Ctrl to fire a {missileName} missile at the target."],
            LockTips(session));

        // Lose the lock WITHOUT retargeting: drag the same target out of range so the
        // lock decays to 0 while targetShipId stays fixed (=> not a "target change"),
        // which is exactly the case that posts "Missile lock lost."
        placedTarget.Position = new Vector3(100000f, 0f, 0f);
        int lost = LockstepPumpUntil(server, session, placedShip, seen, () =>
            session.World.Events.Chats.Any(c => c.Message == "Missile lock lost."), 900);
        Assert.True(lost < 900, "lock-lost tip never posted after target left range");
        Assert.False(session.World.MissileLock.IsFullLock);
        Assert.True(session.World.MissileLock.LockFraction < 1f);
        Assert.Equal(placedTarget.Id, session.World.MissileLock.TargetId);   // same target: a real "lost", not a retarget
        Assert.Equal(3, LockTips(session).Count);
        Assert.Equal("Missile lock lost.", LockTips(session)[^1]);

        Console.WriteLine($"MISSILELOCK-CLIENT-LIVE: lock built to full ({fullState.LockFraction:F3}) over {built} ticks, " +
                          $"tips=[partial, acquired], then lost after target left range ({lost} ticks), missile '{missileName}', " +
                          $"client frames=[{string.Join(" ", seen.Select(f => f.ToString("F3")))}]");
    }

    /// <summary>The lock console tips in the client chat log, in arrival order.</summary>
    private static List<string> LockTips(ConnectionSession session) =>
        session.World.Events.Chats.Select(c => c.Message)
            .Where(m => m.StartsWith("Missile lock") || m.StartsWith("Partial missile lock")).ToList();

    /// <summary>P1020 — tick the server ONE sim step at a time and, after each step,
    /// wait until the client world holds the lock value that step produced (the wire
    /// quantizes it to 1/240, <c>MissileLockUpdate.From</c>) before stepping again.
    /// <para>The owner-only lock stream is best-effort and latest-wins, like native's
    /// non-guaranteed ship updates, so a free-running pump may legitimately deliver
    /// fewer frames than it ticks; what the client then shows is the newest lock, and
    /// the tips follow native's frame-to-frame rule (<c>console.cpp:941-950</c>) on
    /// the frames it did see. Lock-stepping removes that freedom, so the test proves
    /// the tip edges on an exact, known sequence of lock values instead of hoping
    /// wall-clock pacing keeps a receive thread in step.</para>
    /// Each observed value is appended to <paramref name="seen"/>.</summary>
    private static int LockstepPumpUntil(Srv server, ConnectionSession session, Ship ship,
        List<float> seen, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max)
        {
            server.TickSim();
            n++;
            float expected = MissileLockUpdate.From(ship.Id, -1, ship.MissileLock, Vector3.Zero, "").LockFraction;
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (session.World.MissileLock.LockFraction != expected)
            {
                Assert.True(wait.Elapsed < TimeSpan.FromSeconds(10),
                    $"tick {n}: client never applied the server's lock frame (server {expected:F3}, client {session.World.MissileLock.LockFraction:F3})");
                Thread.Sleep(1);
            }
            seen.Add(expected);
        }
        return n;
    }
}
