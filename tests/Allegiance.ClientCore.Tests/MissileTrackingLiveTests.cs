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
/// P234 — live loopback proof that the <b>ongoing-position</b> missile stream
/// reaches a real client's world model: a real <see cref="ConnectionSession"/>
/// (whose <see cref="ConnectionSession.World"/> is the renderer's <c>ClientWorld</c>)
/// observes a missile its own ship fires — first via the creation
/// <c>FIRE_MISSILE(70)</c>, then kept <em>moving</em> by the per-tick
/// <c>MISSILE_UPDATE(205)</c> broadcast, then removed by the destruction
/// <c>MISSILE_DESTROYED(83)</c>. The movement assertion is the crux: without the
/// P234 per-tick broadcast, a renderer-only client would be stuck at the one-shot
/// launch pose; seeing the entity's position advance across ticks proves the 205
/// stream (not merely the 70 creation) is flowing. Mirrors
/// <see cref="MissileFireLiveTests"/>' harness: a mounted missile launcher + an
/// enemy <see cref="Ship.CommandTarget"/> so the server-side missile homes.
/// </summary>
[Collection("LiveNetwork")]
public class MissileTrackingLiveTests
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
            new Gm { Name = "Missile Tracking Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 871u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 87700000000000000ul, DrmHash = "misstrack",
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

    private static DataMissileTypeIGC NoLockMissile(StaticCore core)
    {
        var t = core.MissileTypes.Values
            .Where(m => m.MaxLock == 0f && m.InitialSpeed > 0f && m.Lifespan >= 1f && m.Lifespan < 6f)
            .OrderBy(m => m.ExpendableTypeId).FirstOrDefault();
        if (t.ExpendableTypeId == 0)
            throw new InvalidOperationException("representative core has no MaxLock==0 missile type");
        return t;
    }

    /// <summary>⭐ A real missile the client's own ship fires is observed in the
    /// client's ClientWorld — created, kept moving by per-tick 205, then removed
    /// — all over the real transport.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void FiredMissile_AppearsMovesAndDisappears_InClientWorld_OverTheWire()
    {
        Require.Artwork();
        using var server = new Srv();

        Ship? placedShip = null;
        StaticCore? placedCore = null;
        DataMissileTypeIGC? placedType = null;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var hull = core.HullTypes.Values.First();
            var ship = new Ship(shipId, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);

            // An enemy target in the same sector, directly ahead, in range.
            var target = new Ship((short)(shipId + 701), hull, new Vector3(600f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 1) target.SideId = mission.Sides[1].Id; else target.SideId = 99;
            mission.PlaceShip(target, mission.Sectors[0]);
            ship.Orientation.Set((target.Position - ship.Position).Normalize());
            ship.CommandTarget = target;

            var mt = NoLockMissile(core);
            var part = new DataPartTypeIGC(
                Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ml", Description: "",
                GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
                PartId: 703, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
                Type: new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId));
            ship.MountPart(part, amount: 5, mountId: 0, fractionMounted: 255);

            placedShip = ship;
            placedCore = core;
            placedType = mt;
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "MISSTRACK");
        session.SendLogonRequest(Request("MISSTRACK"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "client must be assigned a ship");
        Assert.NotNull(placedShip);
        Assert.NotNull(placedType);
        short firedTypeId = placedType.Value.ExpendableTypeId;

        // Pre-condition: no missiles in flight yet.
        Assert.Empty(session.World.Missiles);

        // Fire one missile (the client's own ship, so same-side visibility holds).
        session.SendFireMissile();

        // (1) The missile appears in the client's world (via 70 creation and/or
        // the first 205 update).
        int created = PumpUntil(server, () => session.World.Missiles.Count > 0, 3000);
        Assert.True(created < 3000, "missile never appeared in client world");
        var entity = Assert.Single(session.World.Missiles).Value;
        Assert.Equal(firedTypeId, entity.TypeId);
        Assert.Equal(placedShip.Sector!.Id, entity.ClusterId);
        Assert.True(entity.Position.Length() < 150f, $"missile spawn must be near the ship at origin, got {entity.Position}");

        // (2) The missile MOVES across the per-tick 205 stream — the crux proof.
        var firstPos = entity.Position;
        int moved = PumpUntil(server, () =>
        {
            if (session.World.Missiles.Count == 0) return false;   // already removed — treat as not-proven
            var p = Assert.Single(session.World.Missiles).Value.Position;
            return (p - firstPos).Length() > 5f;
        }, 150);
        Assert.True(moved < 150,
            $"missile position must advance across ticks (per-tick 205) — frozen at {firstPos} implies only the one-shot 70 arrived");

        // (3) The missile is removed once it expires or strikes the target
        // (server sends MISSILE_DESTROYED(83); the world removes it by id).
        int removed = PumpUntil(server, () => session.World.Missiles.Count == 0, 2000);
        Assert.True(removed < 2000, "missile must disappear from the client world after expiry/destruction");

        Console.WriteLine($"MISSTRACK-CLIENT-LIVE: missile {entity.MissileId} type {entity.TypeId} appeared, moved " +
                          $"{firstPos} -> (moving) -> removed over the wire ({created}/{moved}/{removed} ticks)");
    }

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);
}
