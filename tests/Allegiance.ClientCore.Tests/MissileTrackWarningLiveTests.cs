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
/// P264 — live loopback proof of the incoming-missile warning wire path: the
/// server's per-tick owner-only <c>MISSILE_TRACK_WARNING(208)</c>
/// (<c>Server.BroadcastMissileTrackWarning</c>) reaches a real
/// <see cref="ConnectionSession"/> (whose <see cref="ConnectionSession.World"/> is
/// the renderer's <c>ClientWorld</c>): when an in-sector ENEMY missile's target
/// is the local player's ship, <see cref="ClientWorld.MissileTrack"/> becomes
/// <see cref="MissileTrackState.IsTracked"/> (armed) with the missile's
/// <c>LockFraction</c> as the best-lock pitch ramp; when that missile is destroyed
/// (removed from the sector — the same server state the expiry path leaves), it
/// clears (disarmed) — exactly the arm/clear the MonoGame client's warning loop
/// follows. Mirrors <see cref="MissileTrackingLiveTests"/>'s harness (reflective
/// ShipSpawner seeding a mounted missile launcher + an enemy command target), but
/// reversed: here the ENEMY fires a homing missile at the local ship, and the local
/// ship is the target that receives the owner-only warning.
/// </summary>
[Collection("LiveNetwork")]
public class MissileTrackWarningLiveTests
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
            new Gm { Name = "Missile Track Warning Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 873u, DwCookie = 0x873u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8790000000000000000ul, DrmHash = "misstrackwarn",
    };

    /// <summary>A no-lock missile type with a GENEROUS lifespan so it stays in
    /// flight across the whole slow arm phase (never self-expires mid-assert;
    /// the clear is driven deterministically by removing it).</summary>
    private static DataMissileTypeIGC LongNoLockMissile(StaticCore core)
    {
        var t = core.MissileTypes.Values
            .Where(m => m.MaxLock == 0f && m.InitialSpeed > 0f && m.Lifespan >= 8f)
            .OrderBy(m => m.ExpendableTypeId).FirstOrDefault();
        if (t.ExpendableTypeId == 0)
            throw new InvalidOperationException("representative core has no MaxLock==0 long-lived missile type");
        return t;
    }

    /// <summary>⭐ An in-sector ENEMY missile homes on the local ship; the local
    /// client's world arms (best-lock pitch ramp carried), then disarms when the
    /// missile is destroyed — over the real transport.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void EnemyMissileHomingOnLocalShip_ArmsWarning_ThenClearsOnDestruction_OverTheWire()
    {
        Require.Artwork();
        using var server = new Srv();

        Ship? placedLocal = null;
        Ship? placedAttacker = null;
        StaticCore? placedCore = null;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First();
            var local = new Ship(shipId, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) local.SideId = mission.Sides[0].Id;
            mission.PlaceShip(local, mission.Sectors[0]);

            // An ENEMY attacker, far away in the same sector (so its missile
            // never reaches/touches the local ship during the test).
            var attacker = new Ship((short)(shipId + 806), hull, new Vector3(5000f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 1) attacker.SideId = mission.Sides[1].Id; else attacker.SideId = 99;
            mission.PlaceShip(attacker, mission.Sectors[0]);

            // Arm the attacker with a REAL missile launcher (ET_Magazine@0) naming
            // a no-lock type.
            var mt = LongNoLockMissile(core);
            var part = new DataPartTypeIGC(
                Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ml", Description: "",
                GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
                PartId: 808, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
                Type: new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId));
            attacker.MountPart(part, amount: 5, mountId: 0, fractionMounted: 255);

            placedLocal = local;
            placedAttacker = attacker;
            placedCore = core;
            return (local, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "MISSTRACKWARN");
        session.SendLogonRequest(Request("MISSTRACKWARN"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "client must be assigned a ship");
        Assert.NotNull(placedLocal);
        Assert.NotNull(placedAttacker);
        Assert.NotNull(placedCore);

        // Pre-condition: no warning armed yet (no enemy missile targeting us).
        Assert.False(session.World.MissileTrack.IsTracked);

        // The enemy fires a homing missile at the local ship, with a deliberate
        // 0.6 lock fraction (so the pitch ramp is meaningfully 0.6, not the 1.0
        // default). This is the server-side creation path the wire uses; the
        // missile's Target is the local ship.
        var attackerMission = placedAttacker.Mission!;
        var missile = placedAttacker.FireMissile(LongNoLockMissile(placedCore), attackerMission.LastUpdate,
            target: placedLocal, lockFraction: 0.6f);
        Assert.Equal(placedLocal, missile.Target);

        // (1) The local client's world arms while the enemy missile targets it,
        // carrying the best-lock pitch ramp. A slow pump keeps the receive thread
        // in pace so the armed 208 frame is actually observed (P230 lesson).
        int armed = SlowPumpUntil(server, () => session.World.MissileTrack.IsTracked, 600);
        Assert.True(armed < 600, "warning must arm while an in-sector enemy missile targets the local ship");
        Assert.Equal(0.6f, session.World.MissileTrack.BestLock, 2);

        // (2) The warning clears when the missile is destroyed (removed from the
        // sector → no in-sector missile targets the local ship any more). This is
        // the exact server state the expiry path also leaves (Missile.OnUpdate
        // removes itself from the sector on expiry), so the disarm is faithful to
        // destruction, expiry and lost-lock alike.
        missile.OwnerSector.Remove(missile);
        int cleared = SlowPumpUntil(server, () => !session.World.MissileTrack.IsTracked, 600);
        Assert.True(cleared < 600, "warning must clear once the tracking missile is destroyed");
        Assert.False(session.World.MissileTrack.IsTracked);

        Console.WriteLine($"MISSTRACKWARN-CLIENT-LIVE: enemy missile {missile.Id} homed on local ship, warning ARMED (bestLock 0.6) over {armed} ticks, CLEARED after destruction over {cleared} ticks");
    }

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Pace <see cref="Srv.TickSim"/> near the configured 20-ups real-time
    /// cadence so a real client's receive thread keeps pace (a fast pump can
    /// outrun the loopback receive and coalesce intermediate frames — P230 lesson).</summary>
    private static int SlowPumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(40); }
        return n;
    }
}
