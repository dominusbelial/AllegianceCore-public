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
/// <c>console.cpp:929-946</c>) post into the chat log at the right edges, and the
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
        Games =
        [
            new Gm { Name = "Missile Lock Client Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 887u, DwCookie = 0x887u,
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
            var hull = core.HullTypes.Values.First();
            var ship = new Ship(shipId, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);

            // Enemy target, directly ahead at 100 units, in range for a lock.
            var target = new Ship((short)(shipId + 666), hull, new Vector3(100f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 1) target.SideId = mission.Sides[1].Id; else target.SideId = 99;
            mission.PlaceShip(target, mission.Sectors[0]);
            ship.Orientation.Set((target.Position - ship.Position).Normalize());
            ship.CommandTarget = target;

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
        Assert.NotNull(missileName);

        // The lock builds server-side each tick; the owner-only update streams in.
        // Pace the sim near real-time (40ms/tick vs the server's 20ups) so the
        // client's receive thread keeps up and processes EVERY lock frame — a fast
        // pump can outrun the receive thread and coalesce straight past the partial
        // phase (the first observed lock lands already full), which would miss the
        // partial edge this test must observe.
        int BuildPhase() => SlowPumpUntil(server,
            () => session.World.MissileLock.IsFullLock &&
                  session.World.Events.Chats.Any(c => c.Message.StartsWith("Partial missile lock")), 900);
        int built = BuildPhase();
        Assert.True(built < 900, $"lock never reached full + partial tip in the client world (lock {session.World.MissileLock.LockFraction:F3})");

        var fullState = session.World.MissileLock;
        Assert.True(fullState.HasTarget);
        Assert.Equal(placedTarget.Id, fullState.TargetId);
        Assert.Equal(missileName, fullState.MissileName);
        Assert.InRange(fullState.TargetPosition.X, 90f, 110f);   // target dead ahead ~100

        // Both edge tips posted exactly once each (arrival order preserved).
        var chats = session.World.Events.Chats.Select(c => c.Message).ToList();
        Assert.Contains("Partial missile lock - center the target on screen to improve the lock.", chats);
        Assert.Contains($"Missile lock acquired - press Left Ctrl to fire a {missileName} missile at the target.", chats);
        Assert.Equal(2, chats.Count(m => m.StartsWith("Missile lock") || m.StartsWith("Partial missile lock")));   // partial + acquired, no spam
        Assert.DoesNotContain("Missile lock lost.", chats);                 // not lost yet

        // Lose the lock WITHOUT retargeting: drag the same target out of range so the
        // lock decays to 0 while targetShipId stays fixed (=> not a "target change"),
        // which is exactly the case that posts "Missile lock lost."
        placedTarget.Position = new Vector3(100000f, 0f, 0f);
        int lost = SlowPumpUntil(server, () =>
            session.World.Events.Chats.Any(c => c.Message == "Missile lock lost."), 900);
        Assert.True(lost < 900, "lock-lost tip never posted after target left range");
        Assert.False(session.World.MissileLock.IsFullLock);
        Assert.True(session.World.MissileLock.LockFraction < 1f);

        Console.WriteLine($"MISSILELOCK-CLIENT-LIVE: lock built to full ({fullState.LockFraction:F3}) over {built} ticks, " +
                          $"tips=[partial, acquired], then lost after target left range ({lost} ticks), missile '{missileName}'");
    }

    /// <summary>Pace <see cref="Srv.TickSim"/> near the configured 20-ups real-time
    /// cadence so a real client's receive thread keeps pace (a fast pump can
    /// outrun the loopback receive and coalesce intermediate lock frames).</summary>
    private static int SlowPumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(40); }
        return n;
    }
}
