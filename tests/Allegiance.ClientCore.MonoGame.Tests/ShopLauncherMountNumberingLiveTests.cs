using System;
using System.Linq;
using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.Session;
using Allegiance.ClientCore.MonoGame.Rendering;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P437 — LIVE proof that the shop's per-equipment-type mount numbering fixes
/// the real player bug where a launcher bought AFTER a weapon + afterburner +
/// cloak + shield landed at a non-zero mount and could never lock or fire
/// (both gates — <c>Ship.UpdateMountedMissileLock</c> /
/// <c>TryFireMissile</c> and <c>Server.HandleFireMissile</c> — read
/// <c>ET_Magazine@0</c>).
///
/// Reproduces Domingo's exact real sequence: from the actual shop UI model
/// (the real PCore001 catalogue), buy a weapon + afterburner + cloak + shield
/// FIRST, then a real missile launcher — over the REAL in-process server + a
/// real <see cref="ConnectionSession"/> wire round-trip — and asserts:
///   • the authoritative BUY_LOADOUT_ACK-granted loadout carries the launcher
///     at <c>MountId 0</c> (the fix), on both the server and the client world;
///   • lock genuinely builds — the owner's real <c>MISSILE_LOCK_UPDATE</c>
///     reaches the client and <c>ClientWorld.MissileLock.IsFullLock</c> becomes
///     true (a MaxLock==0 type snaps to full once the mount-0 launcher is
///     actually found — exactly the path the bug severed);
///   • a real <c>FIRE_MISSILE(70)</c> is accepted and fires — a homing
///     <see cref="Missile"/> appears at the command target and the launcher's
///     ammo decrements (mirroring <see cref="MissileFireLiveTests"/>).
///   • buying a SECOND different launcher afterwards REPLACES the first
///     (the single-slot Magazine reality), verified against a real mounted
///     loadout.
///
/// The ship is spawned flying with an enemy in range + a command target (the
/// established <see cref="MissileFireLiveTests"/> arrangement); the shop is
/// driven as a real <see cref="ShopModel"/> (the same pure model the docked
/// screen uses) and the loadout goes out as the real <c>FM_C_BUY_LOADOUT(4)</c>.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ShopLauncherMountNumberingLiveTests
{
    private static readonly string ArtRoot =
        Environment.GetEnvironmentVariable("ALLEGIANCE_ARTWORK_PATH") ?? "/home/domingo/Development/Artwork";

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P437 Shop Mount Numbering", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 559u, DwCookie = 0x559u,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 5770000000000000000ul, DrmHash = "p437shopmount",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Pace near the configured 20-ups cadence so a real client's
    /// receive thread keeps up with lock frames (the MissileLockLiveTests
    /// discipline — a fast pump can coalesce past intermediate lock states).</summary>
    private static int SlowPumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(40); }
        return n;
    }

    private static readonly FieldInfo ShipSpawnerField =
        typeof(Srv).GetField("ShipSpawner", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Allegiance.Server.Server.ShipSpawner field not found");

    private static void SetShipSpawner(Srv server, Func<StaticCore, Mission, short, (Ship Ship, Sector Sector)> spawner) =>
        ShipSpawnerField.SetValue(server, spawner);

    private static readonly ShopCatalog Catalog = LoadCatalog();
    private static ShopCatalog LoadCatalog() =>
        ShopCatalog.TryLoad(ArtRoot)
        ?? throw new InvalidOperationException($"PCore001.igc not loadable from {ArtRoot}");

    /// <summary>A real missile-launcher offer whose missile type is
    /// <c>MaxLock == 0</c> (needs no lock — the snapshot-to-full path that
    /// proves UpdateMountedMissileLock actually runs) together with a real hull
    /// that mounts it (so the buy is a no-swap free-mount, granting every part).
    /// Scanned from the real core, not assumed from specific ids.</summary>
    private static (LauncherOffer lo, DataHullTypeIGC hull) MountableNoLockLauncher(StaticCore core)
    {
        foreach (var lo in Catalog.Launchers!)
        {
            if (lo.Kind != LauncherKind.Missile) continue;
            if (lo.Part.Type is not DataPartTypeIGC.MissileLauncher ml) continue;
            if (!core.MissileTypes.TryGetValue(ml.ExpendableTypeId, out var mt) || mt.MaxLock != 0f) continue;
            foreach (var h in Catalog.Hulls)
                if (h.CanMountPart(lo.Part))
                    return (lo, h);
        }
        throw new InvalidOperationException("PCore001 has no MaxLock==0 missile launcher + mounting hull pair");
    }

    private static int PartFlatIndex(short partId)
    {
        var list = Catalog.Parts.ToList();
        return Catalog.Hulls.Count + list.FindIndex(p => p.PartId == partId);
    }

    private static int LauncherFlatIndex(short partId)
    {
        var list = Catalog.Launchers!.ToList();
        return Catalog.Hulls.Count + Catalog.Parts.Count + list.FindIndex(l => l.Part.PartId == partId);
    }

    private static EquipmentType TypeOf(short partId)
    {
        foreach (var p in Catalog.Parts) if (p.PartId == partId) return p.EquipmentType;
        foreach (var l in Catalog.Launchers!) if (l.Part.PartId == partId) return l.Part.EquipmentType;
        throw new InvalidOperationException($"part {partId} not in catalogue");
    }

    /// <summary>⭐ The headline live proof: buy weapon + afterburner + cloak +
    /// shield FIRST, then a real missile launcher, from the real shop model; the
    /// server grants the launcher at MOUNT 0; lock genuinely builds to full; a
    /// real FIRE_MISSILE(70) fires; and a second launcher REPLACES the first.</summary>
    [Fact]
    public void ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live()
    {
        using var server = new Srv();
        Ship? placedShip = null;
        Ship? placedTarget = null;
        LauncherOffer? chosenLo = null;
        DataHullTypeIGC? chosenHull = null;

        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var (lo, hull) = MountableNoLockLauncher(core);
            chosenLo = lo;
            chosenHull = hull;

            // The firing ship, flying at the origin with an enemy target in range
            // and a command target — the MissileFireLiveTests arrangement.
            var ship = new Ship(shipId, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);

            var target = new Ship((short)(shipId + 553), hull, new Vector3(120f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 1) target.SideId = mission.Sides[1].Id; else target.SideId = 99;
            mission.PlaceShip(target, mission.Sectors[0]);
            ship.Orientation.Set((target.Position - ship.Position).Normalize());
            ship.CommandTarget = target;

            placedShip = ship;
            placedTarget = target;
            return (ship, mission.Sectors[0]);
        });
        server.Boot(Config());
        foreach (var m in server.Missions) m.Stage = MissionStage.Started;  // bypass the ready-up lobby

        using var session = new ConnectionSession();
        session.Connect(server.BoundPort, "P437SHOPMOUNT");
        session.SendLogonRequest(Request("P437SHOPMOUNT"));

        int joined = PumpUntil(server, () => session.World.MyShipId is not null, 4000);
        Assert.True(joined < 4000, "client must be assigned a ship");
        if (placedShip is not { } psh || placedTarget is not { } tgt
            || chosenLo is not { } lo || chosenHull is not { } hull)
            throw new InvalidOperationException("spawner did not populate the live fixtures (ship/target/launcher/hull)");
        short myId = session.World.MyShipId!.Value;

        // Open the shop exactly as the docked screen does: seeded from the ship's
        // real current loadout (empty at spawn) and the real catalogue.
        var shop = new ShopModel(Catalog, hull.HullId,
            psh.Parts.Select(p => p.ToExpandedPartData()).ToArray());

        // HUMAN SEQUENCE — Domingo's exact order: weapon, afterburner, cloak,
        // shield FIRST, then the missile launcher.
        var weapon = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Weapon);
        var afterburner = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Afterburner);
        var cloak = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Cloak);
        var shield = Catalog.Parts.First(p => p.EquipmentType == EquipmentType.Shield);
        foreach (var part in new[] { weapon, afterburner, cloak, shield })
        {
            shop.MoveCursor(PartFlatIndex(part.PartId) - shop.Cursor);
            shop.Activate();
        }
        shop.MoveCursor(LauncherFlatIndex(lo.Part.PartId) - shop.Cursor);
        shop.Activate();

        // All five distinct parts chosen; the launcher lands at MOUNT 0 (the fix).
        Assert.Equal(5, shop.ChosenParts.Count);
        Assert.Equal((byte)0, shop.ChosenParts.Single(p => p.PartId == lo.Part.PartId).MountId);

        // L — send the pending loadout over the real wire (same hull -> free-mount).
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: false, parts: shop.ChosenParts);

        // The server grants every part; the launcher — ET_Magazine — is at MOUNT 0.
        int applied = PumpUntil(server, () => psh.Parts.Any(p => p.PartId == lo.Part.PartId), 3000);
        Assert.True(applied < 3000, "launcher never granted server-side");
        var launcher = psh.Parts.First(p => p.PartId == lo.Part.PartId);
        Assert.Equal(EquipmentType.Magazine, launcher.EquipmentType);
        Assert.Equal((byte)0, launcher.MountId);                 // ★ the P437 fix: granted at mount 0, not 4-10
        Assert.Equal(lo.Ammo, launcher.Amount);
        Assert.Equal(4, psh.Parts.Count(p => p.PartId != lo.Part.PartId));  // weapon+ab+cloak+shield also mounted

        // The client's authoritative granted loadout carries it at mount 0 too.
        var granted = session.World.Ships[myId].Loadout;
        Assert.NotNull(granted);
        var grantedMag = granted!.FirstOrDefault(p => p.PartId == lo.Part.PartId);
        Assert.NotEqual(default(ExpandedPartData), grantedMag);
        Assert.Equal((byte)0, grantedMag.MountId);

        // LOCK genuinely builds: with the launcher at mount 0, UpdateMountedMissileLock
        // actually runs each tick; the MaxLock==0 type snaps to full, and the owner's
        // real MISSILE_LOCK_UPDATE reaches the client -> IsFullLock.
        int locked = SlowPumpUntil(server, () => session.World.MissileLock.IsFullLock, 1200);
        Assert.True(locked < 1200, $"lock never reached full in the client world (frac {session.World.MissileLock.LockFraction:F3})");

        // FIRE: a real FIRE_MISSILE(70) is accepted and fires — a homing missile
        // appears at the command target and the launcher's ammo decrements.
        int ammoBefore = launcher.Amount;
        session.SendFireMissile();
        int fired = PumpUntil(server, () => psh.Sector!.Models.OfType<Missile>().Any(), 2000);
        Assert.True(fired < 2000, "FIRE_MISSILE(70) never produced a missile");
        var missile = psh.Sector!.Models.OfType<Missile>().Single();
        Assert.Same(tgt, missile.Target);
        Assert.Equal((short)(ammoBefore - 1),
            psh.Parts.First(p => p.PartId == lo.Part.PartId).Amount);

        // ── Single-slot replace, against a REAL mounted loadout ───────────────
        // Re-open the shop from the ship's now-mounted loadout and buy a SECOND
        // different launcher: it REPLACES the first (one Magazine slot), leaving
        // the other four parts intact — never stacking five inert launchers.
        var otherLo = Catalog.Launchers!
            .First(l => l.Kind == LauncherKind.Missile && l.Part.PartId != lo.Part.PartId);
        var shop2 = new ShopModel(Catalog, hull.HullId,
            psh.Parts.Select(p => p.ToExpandedPartData()).ToArray());
        Assert.Equal(5, shop2.ChosenParts.Count);               // seeded: all five incl. the launcher@0
        shop2.MoveCursor(LauncherFlatIndex(otherLo.Part.PartId) - shop2.Cursor);
        shop2.Activate();
        var mags = shop2.ChosenParts.Where(p => TypeOf(p.PartId) == EquipmentType.Magazine).ToList();
        Assert.Single(mags);                                     // exactly ONE magazine remains
        Assert.Equal(otherLo.Part.PartId, mags[0].PartId);       // the new one
        Assert.Equal((byte)0, mags[0].MountId);
        Assert.DoesNotContain(shop2.ChosenParts, p => p.PartId == lo.Part.PartId);  // old replaced
        Assert.Equal(5, shop2.ChosenParts.Count);                // weapon+ab+cloak+shield + new launcher

        Console.WriteLine($"P437-SHOP-MOUNT-LIVE: bought weapon+AB+cloak+shield then launcher {lo.Part.PartId} -> granted ET_Magazine@0 " +
                          $"(fix; was 4-10), lock->full ({session.World.MissileLock.LockFraction:F3}) over {locked} ticks, FIRE_MISSILE " +
                          $"missile {missile.Id} -> target {tgt.Id}, ammo {ammoBefore}->{ammoBefore - 1}; second launcher replaced " +
                          $"({mags[0].PartId}@0) single-slot");
    }
}
