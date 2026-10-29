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
using Allegiance.TestInfra;

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
        ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        SideSelectAutoSeatSeconds = 30,   // P763: preserve the pre-flag 30 s auto-seat (these peers never send POSITIONREQ)
        Games =
        [
            new Gm { Name = "P437 Shop Mount Numbering", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 559u, DwCookie = uint.MaxValue,
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

    /// <summary>P441 — fund a peer's wallet via reflection (Server.Ships is
    /// internal and invisible to this assembly).</summary>
    private static void Fund(Srv server, short shipId, int amount)
    {
        var field = typeof(Srv).GetField("_peerShips", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var dict = (System.Collections.IDictionary)field.GetValue(server)!;
        foreach (System.Collections.DictionaryEntry e in dict)
        {
            var live = e.Value!;
            var ship = (Ship)live.GetType().GetProperty("Ship")!.GetValue(live)!;
            if (ship.Id != shipId) continue;
            var wallet = live.GetType().GetProperty("Wallet")!.GetValue(live)!;
            wallet.GetType().GetProperty("Money")!.SetValue(wallet, amount);
            return;
        }
        throw new InvalidOperationException($"peer ship {shipId} not found to fund");
    }

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
            if (!core.MissileTypes.TryGetValue(ml.ExpendableTypeId, out var mt) || mt.MaxLock != 0f || mt.HasWarpBombCapability) continue;
            foreach (var h in Catalog.Hulls)
                if (h.CanMountPart(lo.Part))
                    return (lo, h);
        }
        throw new InvalidOperationException("PCore001 has no MaxLock==0 missile launcher + mounting hull pair");
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
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ShopBuyOrder_LauncherGrantedAtMount0_LocksAndFires_Live()
    {
        Require.Artwork();
        using var server = new Srv();
        Ship? placedShip = null;
        Ship? placedTarget = null;
        LauncherOffer? chosenLo = null;
        DataHullTypeIGC? chosenHull = null;

        SetShipSpawner(server, (core, mission, shipId) =>
        {
            var (lo, hull) = MountableNoLockLauncher(core);
            // Native magazine locking requires a visible, damageable target;
            // warp bombs use warp targets and are excluded by the selector.
            hull = hull with { ScannerRange = 10000, Signature = 1 };
            chosenLo = lo;
            chosenHull = hull;

            // The firing ship, flying at the origin with an enemy target in range
            // and a command target — the MissileFireLiveTests arrangement.
            var ship = new Ship(shipId, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 0) ship.SideId = mission.Sides[0].Id;
            // H-06: a real friendly station is required for the purchase.
            var home = mission.Sectors.First(s => s.Stations.Any(st => st.SideId == ship.SideId));
            ship.StationId = home.Stations.First(st => st.SideId == ship.SideId).StationId;
            mission.PlaceShip(ship, home);

            var target = new Ship((short)(shipId + 553), hull, new Vector3(120f, 0f, 0f), Vector3.Zero, Orientation.Identity);
            if (mission.Sides.Count > 1) target.SideId = mission.Sides[1].Id; else target.SideId = 99;
            mission.PlaceShip(target, home);
            ship.Orientation.Set((target.Position - ship.Position).Normalize());
            ship.CommandTarget = target;

            placedShip = ship;
            placedTarget = target;
            return (ship, home);
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
        Fund(server, myId, 1_000_000);   // P441: fund the purchase

        // Open the shop exactly as the docked screen does: seeded from the ship's
        // real current loadout (empty at spawn) and the real catalogue.
        var shop = new ShopModel(Catalog, hull.HullId,
            psh.Parts.Select(p => p.ToExpandedPartData()).ToArray());

        // HUMAN SEQUENCE — Domingo's exact order: weapon, afterburner, cloak,
        // shield FIRST, then the missile launcher, through the slot-driven PARTS
        // tab. The shop offers only parts the chosen hull can actually mount
        // (compatible-only), so mount whichever of the four the hull hosts, then
        // the launcher; every non-Weapon type still lands at mount 0.
        var kit = new List<DataPartTypeIGC>();
        foreach (var et in new[] { EquipmentType.Weapon, EquipmentType.Afterburner, EquipmentType.Cloak, EquipmentType.Shield })
        {
            foreach (var p in Catalog.Parts)
                if (p.EquipmentType == et && hull.CanMountPart(p)) { kit.Add(p); break; }
        }
        foreach (var part in kit)
            ShopModelTests.MountViaSlots(shop, part.PartId);
        ShopModelTests.MountViaSlots(shop, lo.Part.PartId);

        // The launcher lands at MOUNT 0 (the fix) regardless of how many
        // non-weapon parts precede it; all non-weapons sit at mount 0 too.
        Assert.Equal(kit.Count + 1, shop.ChosenParts.Count);
        Assert.Equal((byte)0, shop.ChosenParts.Single(p => p.PartId == lo.Part.PartId).MountId);
        Assert.All(shop.ChosenParts.Where(p => TypeOf(p.PartId) != EquipmentType.Weapon && p.PartId != lo.Part.PartId),
            p => Assert.Equal((byte)0, p.MountId));

        // L — send the pending loadout over the real wire (same hull -> free-mount).
        // Keep this mechanics fixture's exact kit sellable (no successor substitution).
        foreach (var p in kit.Append(lo.Part)) EconomyLoadoutTestSetup.Sell(psh.Mission!, p);
        session.SendBuyLoadout(shop.EffectiveHullId, fLaunch: true, parts: shop.ChosenParts);

        // The server grants every part; the launcher — ET_Magazine — is at MOUNT 0.
        int applied = PumpUntil(server, () => psh.Parts.Any(p => p.PartId == lo.Part.PartId), 3000);
        Assert.True(applied < 3000, "launcher never granted server-side");
        var launcher = psh.Parts.First(p => p.PartId == lo.Part.PartId);
        Assert.Equal(EquipmentType.Magazine, launcher.EquipmentType);
        Assert.Equal((byte)0, launcher.MountId);                 // ★ the P437 fix: granted at mount 0, not 4-10
        Assert.Equal(Ship.ClampPartAmount(hull, psh.Mission!.Core, lo.Part, lo.Ammo), launcher.Amount);
        Assert.Equal(kit.Count, psh.Parts.Count(p => p.PartId != lo.Part.PartId));  // the kit parts also mounted

        // The client's authoritative granted loadout carries it at mount 0 too.
        var granted = session.World.Ships[myId].Loadout;
        Assert.NotNull(granted);
        var grantedMag = granted!.FirstOrDefault(p => p.PartId == lo.Part.PartId);
        Assert.NotEqual(default(ExpandedPartData), grantedMag);
        Assert.Equal((byte)0, grantedMag.MountId);

        // Keep the launched ship outside the station's dock volume.
        tgt.Position = psh.Position + new Vector3(120f, 0f, 0f);
        psh.Orientation.Set((tgt.Position - psh.Position).Normalize());
        Assert.Null(psh.StationId);

        // LOCK genuinely builds: with the launcher at mount 0, UpdateMountedMissileLock
        // actually runs each tick; the MaxLock==0 type snaps to full, and the owner's
        // real MISSILE_LOCK_UPDATE reaches the client -> IsFullLock.
        int locked = SlowPumpUntil(server, () => session.World.MissileLock.IsFullLock, 1200);
        Assert.True(locked < 1200, $"lock never reached full in the client world (frac {session.World.MissileLock.LockFraction:F3})");

        // FIRE: a real FIRE_MISSILE(70) is accepted and fires — a homing missile
        // appears at the command target and the launcher's ammo decrements.
        int ammoBefore = launcher.Amount;
        Missile? created = null;
        // H-08 can leave a one-round launcher. Observe its real creation
        // event, even if collision consumes the missile within this tick.
        var site = psh.Mission!.Site;
        var bridge = site.GetType().GetField("MissileCreated")!;
        var broadcast = (Action<Missile>?)bridge.GetValue(site);
        bridge.SetValue(site, broadcast + (Action<Missile>)(m => created = m));
        session.SendFireMissile();
        int fired = PumpUntil(server, () => created is not null, 2000);
        Assert.True(fired < 2000, $"FIRE_MISSILE(70) never produced a missile: ammo={psh.Parts.First(p => p.PartId == lo.Part.PartId).Amount}, dock={psh.StationId}, stage={psh.Mission.Stage}, lock={psh.MissileLock}, target={psh.CommandTarget?.Id}");
        var missile = created!;
        Assert.Same(tgt, missile.Target);
        Assert.Equal((short)(ammoBefore - 1),
            psh.Parts.First(p => p.PartId == lo.Part.PartId).Amount);

        // ── Single-slot replace, against a REAL mounted loadout ───────────────
        // Re-open the shop from the ship's now-mounted loadout and buy a SECOND
        // different launcher: it REPLACES the first (one Magazine slot), leaving
        // the other four parts intact — never stacking five inert launchers.
        var otherLo = Catalog.Launchers!
            .Where(l => l.Kind == LauncherKind.Missile && l.Part.PartId != lo.Part.PartId && hull.CanMountPart(l.Part))
            .First();
        var shop2 = new ShopModel(Catalog, hull.HullId,
            psh.Parts.Select(p => p.ToExpandedPartData()).ToArray());
        Assert.Equal(kit.Count + 1, shop2.ChosenParts.Count);   // seeded: kit + the launcher@0
        ShopModelTests.MountViaSlots(shop2, otherLo.Part.PartId);   // PARTS tab -> magazine slot -> new launcher
        var mags = shop2.ChosenParts.Where(p => TypeOf(p.PartId) == EquipmentType.Magazine).ToList();
        Assert.Single(mags);                                     // exactly ONE magazine remains
        Assert.Equal(otherLo.Part.PartId, mags[0].PartId);       // the new one
        Assert.Equal((byte)0, mags[0].MountId);
        Assert.DoesNotContain(shop2.ChosenParts, p => p.PartId == lo.Part.PartId);  // old replaced
        Assert.Equal(kit.Count + 1, shop2.ChosenParts.Count);    // kit + new launcher

        Console.WriteLine($"P437-SHOP-MOUNT-LIVE: bought weapon+AB+cloak+shield then launcher {lo.Part.PartId} -> granted ET_Magazine@0 " +
                          $"(fix; was 4-10), lock->full ({session.World.MissileLock.LockFraction:F3}) over {locked} ticks, FIRE_MISSILE " +
                          $"missile {missile.Id} -> target {tgt.Id}, ammo {ammoBefore}->{ammoBefore - 1}; second launcher replaced " +
                          $"({mags[0].PartId}@0) single-slot");
    }
}
