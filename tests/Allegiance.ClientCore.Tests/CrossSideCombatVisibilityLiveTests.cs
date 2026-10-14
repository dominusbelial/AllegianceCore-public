using System.Reflection;
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
/// P375 — LIVE proof of cross-side combat/state visibility over the real wire:
/// a real in-process server, real TCP transport, and real
/// <see cref="ConnectionSession"/> peers driving real <see cref="ClientWorld"/>s
/// (the same data plane the MonoGame client uses). Verifies:
/// <list type="number">
/// <item>An enemy ship directly in view is TARGETABLE: the cross-side peer's
/// entity in the watcher's <see cref="ClientWorld"/> has a real, current
/// <c>SectorId</c> equal to the watcher's own sector (the P371 gather filter
/// <c>ship.SectorId == mySector</c>) plus a live <c>Latest</c> pose and
/// <c>IsNotVisible == false</c> — exactly the three conditions
/// <c>GatherSectorCandidates</c> requires to offer the enemy as a T target.</item>
/// <item>The watcher SEES the opponent's projectile fire (PROJECTILE_INSTANCE)
/// and missile launch + tracking (FIRE_MISSILE / MISSILE_UPDATE) in real time.</item>
/// <item>Same-side projectile/missile visibility is unaffected.</item>
/// <item>A cross-side ship's <c>SectorId</c> stays correct across a warp
/// (re-stamped on return to the watcher's sector), not just at first contact.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class CrossSideCombatVisibilityLiveTests
{
    private static readonly string ArtRoot =
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static Cfg Config(string name) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = name, Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name, ulong steam) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 901u, DwCookie = 0x901u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = steam, DrmHash = "p375",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static Ship? ServerShip(Srv server, short id)
    {
        foreach (var m in server.Missions)
            foreach (var sector in m.Sectors)
                foreach (var model in sector.Models)
                    if (model is Ship s && s.Id == id)
                        return s;
        return null;
    }

    private static ConnectionSession Connect(Srv server, string name, ulong steam)
    {
        var s = new ConnectionSession();
        s.Connect(server.BoundPort, name);
        s.SendLogonRequest(Request(name, steam));
        return s;
    }

    /// <summary>A thread-safe snapshot of one client's world (read under the
    /// session gate) so assertions never race the receive thread.</summary>
    private sealed record View(ConnectionSession S, Dictionary<short, ShipEntity> Ships, Dictionary<short, MissileEntity> Missiles, List<ProjectileEntity> Projectiles, short Me)
    {
        public static View Of(ConnectionSession s)
        {
            lock (s.Gate)
            {
                return new View(s,
                    s.World.Ships.ToDictionary(kv => kv.Key, kv => kv.Value),
                    s.World.Missiles.ToDictionary(kv => kv.Key, kv => kv.Value),
                    s.World.Projectiles.ToList(),
                    s.World.MyShipId ?? -1);
            }
        }
    }

    /// <summary>A real dispersion-0 weapon (positive-power projectile) so the
    /// auto-fire projectile velocity is deterministic — the P88 recipe.</summary>
    private static DataPartTypeIGC.Weapon RealWeapon(StaticCore core)
    {
        foreach (var w in core.PartTypes.Values
                     .Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                     .Where(w => w.Dispersion == 0f)
                     .OrderBy(w => w.EnergyPerShot))
        {
            if (core.ProjectileTypes.TryGetValue(w.ProjectileTypeId, out var pt) && pt.Power > 0f)
                return w;
        }
        throw new InvalidOperationException("no dispersion-0 weapon with a positive-power projectile in the representative core");
    }

    /// <summary>The LONGEST-LIVED <c>MaxLock == 0</c> missile type (no lock
    /// needed, so it fires immediately and lives long enough to be tracked
    /// cross-side across many ticks without expiring mid-test).</summary>
    private static DataMissileTypeIGC NoLockMissile(StaticCore core)
    {
        var t = core.MissileTypes.Values
            .Where(m => m.MaxLock == 0f && m.InitialSpeed > 0f)
            .OrderByDescending(m => m.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0)
            throw new InvalidOperationException("representative core has no MaxLock==0 missile type");
        return t;
    }

    /// <summary>Arm <paramref name="shooter"/> with a real weapon (auto-fires on
    /// ticks once AllWeapons + topped-up pools) and a real missile launcher at
    /// mount 0 (fires on the peer's FIRE_MISSILE command).</summary>
    private static void ArmShooter(Ship shooter, StaticCore core, Ship target)
    {
        var weapon = RealWeapon(core);
        shooter.StateM |= ShipControlState.AllWeapons;
        shooter.Energy = 1_000_000f;
        shooter.Ammo = 30_000;
        shooter.MountWeapon(weapon);

        var mt = NoLockMissile(core);
        var launcher = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ml", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 702, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId));
        shooter.MountPart(launcher, amount: 3, mountId: 0, fractionMounted: 255);
        shooter.CommandTarget = target;
        shooter.Orientation.Set((target.Position - shooter.Position).Normalize());
    }

    /// <summary>⭐ Bullet 1 + 2. Alpha watcher and Bravo shooter are cross-side,
    /// face-to-face in the same sector. The watcher's ClientWorld must (a) hold
    /// the Bravo ship with a real SectorId == its own sector, a live Latest pose
    /// and IsNotVisible == false (exactly the P371 T-gather conditions), (b) see
    /// Bravo's projectile fire (PROJECTILE_INSTANCE) and (c) see Bravo's missile
    /// launch AND its per-tick tracking (FIRE_MISSILE + MISSILE_UPDATE).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void CrossSide_WatcherSeesOpponent_Projectile_And_Missile_InRealTime()
    {
        Require.Artwork();
        using var server = new Srv();
        int placed = 0;
        Sector? s0 = null; Vector3 bravoSpawn = default;
        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            s0 = mission.Sectors[0];
            if (placed == 0)   // Alpha watcher at the origin
            {
                placed++;
                var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
                mission.PlaceShip(ship, s0);
                return (ship, s0);
            }
            // Bravo shooter at 250 u on +X (in weapon range of the watcher), cross-side
            bravoSpawn = new Vector3(250f, 0f, 0f);
            var bs = new Ship(id, hull, bravoSpawn, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[1].Id };
            mission.PlaceShip(bs, s0);
            placed++;
            return (bs, s0);
        });

        server.Boot(Config("P375 Cross-Side Combat"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;   // bypass the ready-up lobby

        using var alpha = Connect(server, "AlphaWatcher", 90000000000000001ul);
        using var bravo = Connect(server, "BravoShooter", 90000000000000002ul);
        int seated = PumpUntil(server, () => alpha.World.MyShipId is { } && bravo.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both peers seated");
        short alphaId = alpha.World.MyShipId!.Value;
        short bravoId = bravo.World.MyShipId!.Value;
        Assert.NotEqual(alphaId, bravoId);

        // (1) The watcher learns the cross-side Bravo ship targetable: real
        // SectorId == its own sector, live Latest pose, not gone. These are
        // precisely the P371 GatherSectorCandidates conditions (IsNotVisible
        // skip + SectorId == mySector) plus the pose guard every render/target
        // loop requires.
        int learned = PumpUntil(server,
            () =>
            {
                var v = View.Of(alpha);
                return v.Ships.TryGetValue(bravoId, out var e)
                       && e.Latest is not null && !e.IsNotVisible
                       && e.SectorId == v.Ships[alphaId].SectorId;
            }, 4000);
        Assert.True(learned < 4000, "watcher learned the cross-side ship with SectorId == own sector (P375 fix)");

        var bravoServer = ServerShip(server, bravoId)!;
        var alphaServer = ServerShip(server, alphaId)!;
        var mission = bravoServer.Mission ?? throw new InvalidOperationException("bravo must have a mission");
        ArmShooter(bravoServer, mission.Core, alphaServer);

        // (2) The Bravo shooter auto-fires projectiles AND we command one missile.
        bravo.SendFireMissile();

        int sawProjectile = PumpUntil(server, () => View.Of(alpha).Projectiles.Count > 0, 4000);
        Assert.True(sawProjectile < 4000,
            "cross-side watcher must see the opponent's projectile fire (PROJECTILE_INSTANCE)");

        // (3) Cross-side missile launch + real-time tracking:
        int missileSeen = PumpUntil(server,
            () => View.Of(alpha).Missiles.Values.Any(mm => mm.ClusterId == View.Of(alpha).Ships[alphaId].SectorId), 4000);
        Assert.True(missileSeen < 4000,
            "cross-side watcher must see the opponent's missile launch (FIRE_MISSILE) in its own sector");

        // Tracking proof: the cross-side MISSILE_UPDATE(205) stream keeps the
        // entity fresh; sample the missile's position twice a short while apart
        // and require it to have advanced (not a frozen spawn).
        short missileId = View.Of(alpha).Missiles.Values
            .First(mm => mm.ClusterId == View.Of(alpha).Ships[alphaId].SectorId).MissileId;
        var p0 = View.Of(alpha).Missiles[missileId].Position;
        int moved = PumpUntil(server,
            () => View.Of(alpha).Missiles.TryGetValue(missileId, out var mm)
                  && (mm.Position - p0).Length() > 1f, 400);
        Assert.True(moved < 400,
            $"cross-side watcher must track the opponent's missile in real time (205 stream), p0={p0}");

        Console.WriteLine($"P375-XCOMBAT: watcher alpha {alphaId} sees cross-side bravo {bravoId} " +
                          $"(SectorId {View.Of(alpha).Ships[bravoId].SectorId}==own, projectile x{sawProjectile}, " +
                          $"missile {missileId} tracked after {moved} ticks)");
    }

    /// <summary>⭐ Bullet 3 (no regression): a SAME-SIDE pair still sees each
    /// other's projectile fire and missile launch exactly as before P375.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SameSide_pairStillSeesProjectileAndMissile()
    {
        Require.Artwork();
        using var server = new Srv();
        int placed = 0;
        Sector? s0 = null;
        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            s0 = mission.Sectors[0];
            short side = mission.Sides[0].Id;   // BOTH on Alpha
            var pos = new Vector3(placed * 250f, 0f, 0f);
            var ship = new Ship(id, hull, pos, Vector3.Zero, Orientation.Identity) { SideId = side };
            mission.PlaceShip(ship, s0);
            placed++;
            return (ship, s0);
        });

        server.Boot(Config("P375 Same-Side"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var a1 = Connect(server, "AlphaOne", 91000000000000001ul);
        using var a2 = Connect(server, "AlphaTwo", 91000000000000002ul);
        int seated = PumpUntil(server, () => a1.World.MyShipId is { } && a2.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both same-side peers seated");
        short shooterId = a1.World.MyShipId!.Value;
        short watcherMe = a2.World.MyShipId!.Value;

        var shooter = ServerShip(server, shooterId)!;
        var target = ServerShip(server, watcherMe)!;
        ArmShooter(shooter, shooter.Mission!.Core, target);

        a1.SendFireMissile();

        int sawProjectile = PumpUntil(server, () => View.Of(a2).Projectiles.Count > 0, 4000);
        Assert.True(sawProjectile < 4000, "same-side watcher must still see the teammate's projectile");

        int sawMissile = PumpUntil(server,
            () => View.Of(a2).Missiles.Values.Any(mm => mm.ClusterId == View.Of(a2).Ships[watcherMe].SectorId), 4000);
        Assert.True(sawMissile < 4000, "same-side watcher must still see the teammate's missile launch");

        Console.WriteLine($"P375-SAMESIDE: watcher {watcherMe} still sees shooter {shooterId}'s projectile (x{sawProjectile}) and missile (x{sawMissile}) [NO REGRESSION]");
    }

    /// <summary>⭐ Bullet 4: a cross-side ship's SectorId stays CORRECT as it
    /// moves between sectors. The watcher learns it in sector A (SectorId == A);
    /// it warps out (watcher latches IsGone via P370); it warps back into A; the
    /// watcher clears IsGone AND the re-flowing 176 re-stamps SectorId == A —
    /// not a stale one-time value from first contact.</summary>
    [Trait("Requires", "Artwork")]
    [Trait("Lane", "LiveTiming")]   // P647 quarantine: flaky live-wire warp-return visibility even in isolation
    [SkippableFact]
    public void CrossSide_sector_stays_correct_across_warp_return()
    {
        Require.Artwork();
        using var server = new Srv();
        int placed = 0;
        Ship? bravo = null; Sector? sec0 = null; Vector3 warpCenter = default; Vector3 outDir = default;
        float hullLen = 1f; float warpRadius = 100f; ResolvedWarp? awayWarp = null;

        SetShipSpawner(server, (core, mission, id) =>
        {
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First();
            hullLen = hull.Length;
            var s0 = mission.Sectors[0];
            sec0 = s0;
            if (placed == 0)   // Alpha watcher — parked far from any warp
            {
                placed++;
                var ship = new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[0].Id };
                mission.PlaceShip(ship, s0);
                return (ship, s0);
            }
            // Bravo departer — parked outside a warp (stationary so the watcher
            // learns it first; we fling it in afterward).
            var w = mission.Warps.First(w2 => w2.Destination is not null && w2.Partner() is not null && s0.Warps.Contains(w2));
            awayWarp = w;
            warpCenter = new Vector3(w.Warp.Position![0], w.Warp.Position[1], w.Warp.Position[2]);
            warpRadius = w.Warp.Radius;
            float[] f = w.Warp.Forward is { Length: 3 } v && (v[0]*v[0]+v[1]*v[1]+v[2]*v[2]) > 0f ? v : [1f, 0f, 0f];
            outDir = new Vector3(f[0], f[1], f[2]).Normalize();
            var spawn = warpCenter + outDir * (3.0f * (w.Warp.Radius + 0.5f * hull.Length));
            var ws = new Ship(id, hull, spawn, Vector3.Zero, Orientation.Identity) { SideId = mission.Sides[1].Id };
            mission.PlaceShip(ws, s0);
            bravo = ws;
            placed++;
            return (ws, s0);
        });

        server.Boot(Config("P375 Cross-Side Sector Warp"));
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;

        using var alpha = Connect(server, "AlphaWatcherW", 92000000000000001ul);
        using var bravos = Connect(server, "BravoW", 92000000000000002ul);
        int seated = PumpUntil(server, () => alpha.World.MyShipId is { } && bravos.World.MyShipId is { }, 4000);
        Assert.True(seated < 4000, "both peers seated");
        short alphaMe = alpha.World.MyShipId!.Value;
        short bravoId = bravos.World.MyShipId!.Value;

        // Learn the cross-side ship in sector 0 with the correct SectorId.
        int learned = PumpUntil(server,
            () => View.Of(alpha).Ships.TryGetValue(bravoId, out var e)
                  && e.Latest is not null && !e.IsNotVisible
                  && e.SectorId == sec0!.Id, 20000);
        Assert.True(learned < 20000, "watcher learned the cross-side ship with SectorId == sector 0");

        // Warp Bravo out.
        lock (server)
        {
            float r = warpRadius + 0.5f * hullLen;
            bravo!.Position = warpCenter + outDir * (0.9f * r);
            bravo.Velocity = -outDir * 20f;
        }
        int transited = PumpUntil(server, () => ServerShip(server, bravoId)?.Sector?.Id != sec0!.Id, 20000);
        Assert.True(transited < 20000, "bravo transited to another sector");

        // P370: the watcher latches it gone.
        int gone = PumpUntil(server,
            () => View.Of(alpha).Ships.TryGetValue(bravoId, out var e) && e.IsGone, 20000);
        Assert.True(gone < 20000, "cross-side watcher marked the warped ship IsGone");

        // Warp Bravo BACK into the watcher's sector.
        var returnWarp = awayWarp!.Partner()!;
        var rc = new Vector3(returnWarp.Warp.Position![0], returnWarp.Warp.Position[1], returnWarp.Warp.Position[2]);
        float[] rfw = returnWarp.Warp.Forward is { Length: 3 } rv && (rv[0]*rv[0]+rv[1]*rv[1]+rv[2]*rv[2]) > 0f ? rv : [1f, 0f, 0f];
        var rdir = new Vector3(rfw[0], rfw[1], rfw[2]).Normalize();
        lock (server)
        {
            bravo!.Position = rc + rdir * (0.9f * (returnWarp.Warp.Radius + 0.5f * hullLen));
            bravo.Velocity = -rdir * 20f;
        }
        int backSector = PumpUntil(server, () => ServerShip(server, bravoId)?.Sector?.Id == sec0!.Id, 20000);
        Assert.True(backSector < 20000, "bravo warped back into the watcher's sector");
        // Park it away from the A-side warp so the 2s debounce can't re-transit.
        lock (server)
        {
            bravo.Position = warpCenter + outDir * (4.0f * (warpRadius + 0.5f * hullLen));
            bravo.Velocity = Vector3.Zero;
        }

        // It is visible again AND its SectorId is re-stamped to the current
        // sector (== watcher's), proving the value stays correct across warp.
        int returned = PumpUntil(server,
            () => View.Of(alpha).Ships.TryGetValue(bravoId, out var e)
                  && !e.IsGone && !e.IsNotVisible
                  && e.SectorId == View.Of(alpha).Ships[alphaMe].SectorId, 20000);
        Assert.True(returned < 20000, "returned cross-side ship is visible again with a correct (re-stamped) SectorId");

        Console.WriteLine($"P375-WARP: cross-side bravo {bravoId} SectorId stayed correct across warp " +
                          $"(re-stamped to {View.Of(alpha).Ships[bravoId].SectorId} on return, IsGone cleared)");
    }
}
