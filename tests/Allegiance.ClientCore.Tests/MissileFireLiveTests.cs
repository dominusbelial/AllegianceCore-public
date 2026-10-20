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
/// P226 — live integration proof of the missile-fire wire path in the MonoGame
/// client: <see cref="ConnectionSession.SendFireMissile"/> (the outbound half of
/// <c>FM_CS_FIRE_MISSILE(70)</c>, bound to Left Ctrl in Game1.cs) against the
/// server's inbound <c>HandleFireMissile</c> (Server.cs, port of
/// <c>FedSrv.CPP:5413-5507</c>). The firing ship carries a real mounted
/// <see cref="DataPartTypeIGC.MissileLauncher"/> naming a <c>MaxLock == 0</c>
/// missile type (needs no lock — UpdateMissileLock's gate 5 snaps to full
/// instantly) and a server-side <see cref="Ship.CommandTarget"/> = an enemy ship
/// in the same sector. Processing proof is asserted on the real server-side
/// state, exactly as <see cref="ChaffDropLiveTests"/> does for chaff: a real
/// <see cref="Missile"/> appears in the firing ship's sector homing at the
/// target (position narrowing toward it over subsequent ticks) and the
/// launcher's <c>Amount</c> decrements.
/// </summary>
[Collection("LiveNetwork")]
public class MissileFireLiveTests
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
            new Gm { Name = "Missile Fire Client Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 886u, DwCookie = 0x886u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 8880000000000000000ul, DrmHash = "missilefireclient",
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

    /// <summary>A <c>MaxLock == 0</c> missile type from the real core (no lock
    /// needed — fires immediately, so the wire test does not wait for a lock
    /// build).</summary>
    private static DataMissileTypeIGC NoLockMissile(StaticCore core)
    {
        var t = core.MissileTypes.Values
            .Where(m => m.MaxLock == 0f && m.InitialSpeed > 0f && m.Lifespan >= 1f && m.Lifespan < 6f)
            .OrderBy(m => m.ExpendableTypeId).FirstOrDefault();
        if (t.ExpendableTypeId == 0)
            throw new InvalidOperationException("representative core has no MaxLock==0 missile type");
        return t;
    }

    /// <summary>⭐ The outbound round trip over the real transport: a real
    /// <see cref="ConnectionSession"/> sends <see cref="ConnectionSession.SendFireMissile"/>
    /// for a ship carrying a mounted missile launcher with a CommandTarget; the
    /// server's HandleFireMissile fires — a real homing <see cref="Missile"/>
    /// appears in the firing ship's sector (position narrowing toward the target)
    /// and the launcher's <c>Amount</c> decrements 3 → 2.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SendFireMissile_LaunchesHomingMissileAndDecrementsAmmo_OverTheWire()
    {
        Require.Artwork();
        using var server = new Srv();

        Ship? placedShip = null;
        StaticCore? placedCore = null;
        Ship? placedTarget = null;
        SetShipSpawner(server, (core, mission, shipId) =>
        {
            // H-08 / launchertypeigc.h:130-145: use real magazine capacity.
            var hull = core.HullTypes.Values.First(h => h.CapacityMagazine >= 3 * NoLockMissile(core).LauncherExpendableSize) with { ScannerRange = 20000, Signature = 100 };
            var ship = new Ship(shipId, hull, new Vector3(0f, 5000f, 5000f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);

            // An enemy target in the same sector, directly ahead, in range.
            var target = new Ship((short)(shipId + 777), hull, new Vector3(250f, 5000f, 5000f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 1) target.SideId = mission.Sides[1].Id; else target.SideId = 99;
            mission.PlaceShip(target, mission.Sectors[0]);
            ship.Orientation.Set((target.Position - ship.Position).Normalize());
            ship.CommandTarget = target;

            // Arm the placed ship with a real missile launcher at mount 0 (ET_Magazine).
            var mt = NoLockMissile(core);
            var part = new DataPartTypeIGC(
                Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ml", Description: "",
                GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
                PartId: 702, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
                Type: new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId));
            ship.MountPart(part, amount: 3, mountId: 0, fractionMounted: 255);

            placedShip = ship;
            placedCore = core;
            placedTarget = target;
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "MISSILEFIRE");
        session.SendLogonRequest(Request("MISSILEFIRE"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "client must be assigned a ship");
        Assert.NotNull(placedShip);
        Assert.NotNull(placedCore);
        Assert.NotNull(placedTarget);
        PumpUntil(server, () => placedTarget!.SeenBySide(placedShip!.SideId!.Value), 1000);
        Assert.True(placedTarget!.SeenBySide(placedShip.SideId!.Value), "missile target must be detected");

        var launcher = placedShip.Parts.First(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0);
        Assert.Equal(3, launcher.Amount);
        Assert.Empty(placedShip.Sector!.Models.OfType<Missile>());

        // Press the missile-fire key equivalent: one fire-and-forget
        // FIRE_MISSILE(70) over the real transport.
        session.SendFireMissile();

        // Server-side proof: a real Missile appears in the firing ship's sector,
        // ammo decrements 3 -> 2.
        int fired = PumpUntil(server, () => placedShip.Sector!.Models.OfType<Missile>().Any(), 2000);
        Assert.True(fired < 2000, "missile never appeared via ConnectionSession.SendFireMissile (fired=" + fired + ")");

        var missile = placedShip.Sector.Models.OfType<Missile>().Single();
        Assert.Same(placedTarget, missile.Target);       // homing at the command target
        Assert.Equal(2, placedShip.Parts.First(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0).Amount);

        // Homing proof: over subsequent ticks the missile's position narrows
        // toward the target (real acceleration + turn toward the lead point).
        float d0 = (missile.Position - placedTarget.Position).Length();
        int homing = PumpUntil(server, () => (missile.Position - placedTarget.Position).Length() < d0 - 5f, 400);
        Assert.True(homing < 400, $"missile did not home toward the target (d0={d0:F1})");

        Console.WriteLine($"MISSILEFIRE-CLIENT-LIVE: missile {missile.Id} launched + homing toward target {placedTarget.Id} after {fired} ticks, ammo 3 -> 2 (d0={d0:F1})");
    }
}
