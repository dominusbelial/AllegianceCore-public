using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P361 — the auto-reload-on-empty-ammo mechanic (the port of the
/// zero-transition block inside native <c>CshipIGC::SetAmmo</c>,
/// <c>shipIGC.h:597-621</c>). When a weapon fires a ship's ammo down to exactly
/// zero, the ship automatically pulls a spare ammo pack from its own cargo hold
/// through the EXISTING P192 reload applicator (<see cref="Ship.TryApplyReloadEntry"/>),
/// rate-limited by a real debounce (<c>0.5 / FloatConstants[c_fcidMountRate]</c>),
/// mirroring native's "Reloading ammo..." / "Ammo depleted." distinction. Fuel is
/// NOT exercised here: this port deliberately tracks no runtime fuel pool (the
/// pervasive "fuel is a documented no-op" in <c>Ship.cs</c>), so native
/// <c>SetFuel</c>'s structurally-identical block has no live trigger.
/// </summary>
public class AutoReloadOnEmptyTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static IReadOnlyList<SideSpec> SideSpecs()
    {
        var core = RepresentativeCore.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        return [new SideSpec(0, "Team 1", civ.CivilizationId)];
    }

    private static Mission NewStartedMission()
    {
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    // A hull that can actually fight (has energy/ammo to fire) and a fixed
    // hardpoint at mount 0 so weapon selection (IsWeaponSelected) works.
    private static readonly Lazy<DataHullTypeIGC> FightHull = new(() =>
        RepresentativeCore.Value.HullTypes.Values
            .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
            .OrderBy(h => h.HullId)
            .First());

    private static Ship NewShip() => new(
        shipId: 9341,
        hull: FightHull.Value,
        position: new Vector3(10f, -20f, 30f),
        velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    /// <summary>A real ammo-consuming weapon from the core that the Fight hull can
    /// actually fire (dispersion-0 for a deterministic projectile, cheap enough
    /// energy for the hull's pool, and a resolvable projectile type). Returns the
    /// weapon SUBTYPE for <see cref="Ship.MountWeapon"/>.</summary>
    private static DataPartTypeIGC.Weapon AmmoWeapon(StaticCore core) =>
        core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type)
            .Where(w => w.CAmmoPerShot > 0 && w.Dispersion == 0f
                        && core.ProjectileTypes.ContainsKey(w.ProjectileTypeId)
                        && w.EnergyPerShot <= FightHull.Value.MaxEnergy)
            .OrderBy(w => w.EnergyPerShot)
            .First();

    /// <summary>The parent <see cref="DataPartTypeIGC"/> record of a real
    /// ammo-consuming weapon — the shape <see cref="Ship.MountPart"/> (and the
    /// native NYI RELOAD gate's "kept-equipped part") needs.</summary>
    private static DataPartTypeIGC AmmoWeaponParent(StaticCore core) =>
        core.PartTypes.Values
            .Where(p => p.Type is DataPartTypeIGC.Weapon { CAmmoPerShot: > 0 })
            .OrderBy(p => p.PartId)
            .First();

    /// <summary>A constructed ammo pack part (<c>c_packAmmo</c> = 0).</summary>
    private static DataPartTypeIGC AmmoPackType() => new(
        Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ammo", Description: "",
        GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
        PartId: 910, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
        Type: new DataPartTypeIGC.Pack(PackType: 0, Amount: 0));   // c_packAmmo

    /// <summary>Mount an ammo weapon (selected) + move an ammo pack into the
    /// cargo hold, and drive the mission tick until the weapon fires the pool
    /// to zero and the FIRST auto-reload attempt fires (the ship's AllWeapons
    /// state re-activates the weapon each tick, so we must stop at the first
    /// attempt or a later firing re-exhausts the refilled pool with no spare).
    /// Returns the mission for the caller to inspect state.</summary>
    private static Mission ArmAndFireToZero(Ship ship, int ammoPackAmount)
    {
        var mission = ship.Mission!;
        ship.StateM |= ShipControlState.AllWeapons;
        ship.Energy = ship.HullType.MaxEnergy;    // plentiful: ammo is the only gate
        ship.Ammo = 2;                            // a small pool so a burst empties it
        ship.MountWeapon(AmmoWeapon(mission.Core));

        // A spare ammo pack at native equipment mount 0, moved into cargo slot 0.
        ship.MountPart(AmmoPackType(), amount: (short)ammoPackAmount, mountId: 0, fractionMounted: 255);
        Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1),
            "the ammo pack must land in the cargo hold");
        Assert.NotNull(ship.CargoHold[Ship.CargoSlotForMount(-1)]);

        double t = 0.0;
        for (int i = 0; i < 400 && ship.LastAutoReloadOutcome == AutoReloadOutcome.NotAttempted; i++)
        {
            t += 0.012;
            mission.Update(t);
        }
        return mission;
    }

    /// <summary>
    /// ⭐ MUST-FAIL-IF-REMOVED: firing a weapon down to exactly zero ammo, with
    /// a spare ammo pack in the cargo hold and NO manual reload input,
    /// automatically consumes that pack and brings the pool back above zero —
    /// the port of native <c>SetAmmo</c>'s zero-transition block
    /// (<c>shipIGC.h:603-616</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Firing_ammo_to_zero_auto_reloads_from_cargo_spare()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);

        ArmAndFireToZero(ship, ammoPackAmount: 5);

        // The weapon burned the seeded pool to exactly 0…
        Assert.Equal(AutoReloadOutcome.Reloaded, ship.LastAutoReloadOutcome);
        Assert.True(ship.Ammo > 0, "the auto-reload must bring ammo back above zero without manual input");
        // …and the cargo spare was consumed entirely (5 < the hull's MaxAmmo,
        // so the whole pack drains into the pool — native NA, clintlib.cpp:3518-3525).
        Assert.Null(ship.CargoHold[Ship.CargoSlotForMount(-1)]);
        Assert.True(ship.LastAutoReloadAt > 0, "the attempt must record its sim time");
    }

    /// <summary>
    /// ⭐ MUST-FAIL-IF-REMOVED: a ship with an EMPTY cargo hold at zero ammo does
    /// NOT crash and correctly stays at zero — native's "Ammo depleted." case
    /// (<c>shipIGC.h:610-612</c>). The failure is distinguished from success via
    /// <see cref="Ship.LastAutoReloadOutcome"/> so P362 can show the two texts.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Firing_ammo_to_zero_with_empty_cargo_stays_depleted()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);

        ship.StateM |= ShipControlState.AllWeapons;
        ship.Energy = ship.HullType.MaxEnergy;
        ship.Ammo = 2;
        ship.MountWeapon(AmmoWeapon(mission.Core));
        // NOTE: no pack placed in the cargo hold.

        double t = 0.0;
        for (int i = 0; i < 200 && ship.Ammo > 0; i++)
        {
            t += 0.032;
            mission.Update(t);
        }

        Assert.Equal(0, ship.Ammo);                      // stays empty (no crash)
        Assert.Equal(AutoReloadOutcome.Depleted, ship.LastAutoReloadOutcome);
    }

    /// <summary>The auto-reload attempt is rate-limited by the real
    /// <c>0.5 / FloatConstants[c_fcidMountRate]</c> debounce: a second
    /// zero-transition within the interval is NOT attempted, and a later one
    /// past the deadline is.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_reload_respects_the_debounce_rate_limit()
    {
        Require.Artwork();
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);

        // Cargo spare, pool already zero (simulating a post-depletion ship).
        ship.Ammo = 0;
        ship.MountPart(AmmoPackType(), amount: 5, mountId: 0, fractionMounted: 255);
        Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1));

        double t0 = mission.LastUpdate;
        Assert.Equal(AutoReloadOutcome.Reloaded, ship.TryAutoReloadAmmo(t0));   // 1st attempt, spare consumed
        Assert.Equal(0, ship.CargoHold[Ship.CargoSlotForMount(-1)] == null ? 0 : 1); // pack gone

        // Immediately re-triggering (same instant) must be debounced — the
        // pool is now empty again, but the rate limit forbids a new attempt.
        // Re-seed a second pack so the only gate is the debounce.
        ship.MountPart(AmmoPackType(), amount: 5, mountId: 0, fractionMounted: 255);
        Assert.True(ship.TrySwapPartWithCargo(EquipmentType.Pack, 0, -1));
        ship.Ammo = 0;

        Assert.Equal(AutoReloadOutcome.NotAttempted, ship.TryAutoReloadAmmo(t0));

        // After the real interval elapses, the attempt is allowed again.
        double interval = 0.5 / (mission.Core.Constants.FloatConstants is { } fc && fc.Length > 10 ? fc[10] : 1f);
        Assert.Equal(AutoReloadOutcome.Reloaded, ship.TryAutoReloadAmmo(t0 + interval + 0.01));
        Assert.Null(ship.CargoHold[Ship.CargoSlotForMount(-1)]);   // 2nd spare consumed
    }

    /// <summary>⭐⭐ LIVE-WIRE PROOF: a real server-side auto-reload on a bot's
    /// ship (through the full loopback server + observer) relays the applied
    /// <c>RELOAD(129)</c> entries sector-wide with <c>shipID</c> filled in —
    /// the same relay the manual <c>FM_CS_RELOAD</c> handler produces
    /// (<c>FedSrv.CPP:6651-6652</c>) — so peers (and P362) observe the transfer.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Auto_reload_relays_RELOAD129_sector_wide()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var ship = seat.Subject;

        // A kept-equipped part keeps Parts.Count != 0 (native RELOAD NYI gate).
        ship.MountPart(AmmoWeaponParent(Core()), amount: 0, mountId: 2, fractionMounted: 255);
        // An ammo pack into cargo slot 0 (wire mount -1) via the real SWAP_PART.
        ship.MountPart(AmmoPackType(), amount: 6, mountId: 0, fractionMounted: 255);
        seat.SubjectBot.SendSwapPart(EquipmentType.Pack, mountOld: 0, mountNew: -1);
        PumpUntil(seat.Server, () => ship.CargoHold[Ship.CargoSlotForMount(-1)] is not null, 8000);
        Assert.NotNull(ship.CargoHold[Ship.CargoSlotForMount(-1)]);   // the spare reached the hold

        foreach (var b in new[] { seat.SubjectBot, seat.ObserverBot }) b.ResetReloadsReceived();

        // Drain the live ship's pool to zero — the realistic just-depleted state
        // that native SetAmmo's zero-transition block responds to.
        ship.Ammo = 0;

        // Drive the auto-reload through the real sim clock (no manual input).
        var outcome = ship.TryAutoReloadAmmo(ship.Mission!.LastUpdate + 0.005);

        Assert.Equal(AutoReloadOutcome.Reloaded, outcome);
        // The 6-round pack (well below the hull's MaxAmmo) drained ENTIRELY into
        // the pool (native NA transfer, clintlib.cpp:3518-3525) — so ammo == 6.
        Assert.Equal(6, ship.Ammo);

        // The relay travels to the observer over the wire asynchronously — poll
        // for its arrival (the same PumpUntil the CargoBayReloadTests use).
        PumpUntil(seat.Server, () => seat.ObserverBot.ReloadBroadcasts.Any(r => r.ShipID == ship.Id), 20000);
        var relay = seat.ObserverBot.ReloadBroadcasts.Single(r => r.ShipID == ship.Id);
        var entry = Assert.Single(relay.Entries);
        Assert.Equal(new ReloadData(-1, -1), entry);   // whole pack (NA) from cargo slot 0
        Assert.Null(ship.CargoHold[Ship.CargoSlotForMount(-1)]);   // spare consumed
    }

    // ── live-wire seat (mirrors CargoBayReloadTests) ───────────────────────

    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Auto Reload Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 512u, DwCookie = 0x51u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 62000000000000000ul, DrmHash = "autoreload",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private sealed class Seat : IDisposable
    {
        public Srv Server;
        public Ship Subject;
        public FltBot SubjectBot;
        public FltBot ObserverBot;
        private readonly FltBot[] _bots;
        public Seat(Srv server, FltBot[] bots, Ship subject, FltBot subjectBot, FltBot observerBot)
        {
            Server = server; Subject = subject; SubjectBot = subjectBot; ObserverBot = observerBot; _bots = bots;
        }
        public void Dispose()
        {
            foreach (var b in _bots) b.Dispose();
            Server.Dispose();
        }
    }

    private static Seat SeatThree()
    {
        var server = new Srv();
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p361_subject", "p361_observer", "p361_third" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            short subjectId = bots[0].ShipAssignment!.Ship.ShipId;
            var subject = server.Ships.Single(s => s.Ship.Id == subjectId).Ship;

            // The shared live-wire harness boot race: after every bot holds its
            // OWN ShipAssignment, the SERVER-side Ships map can still lag that
            // assignment (wider under the parallel full suite). Rather than
            // Single()-throwing on a not-yet-registered peer, poll the server's
            // map until all three assigned ships are present — they only ever
            // GROW, so once present they stay present (no torn reads after).
            Func<bool> allRegistered = () =>
                bots.All(b => server.Ships.Any(s => s.Ship.Id == b.ShipAssignment!.Ship.ShipId));
            PumpUntil(server, allRegistered, 4000);
            Assert.True(allRegistered(), "all assigned ships must be registered server-side");

            int observerIdx = Enumerable.Range(1, 2).First(i =>
                server.Ships.Single(s => s.Ship.Id == bots[i].ShipAssignment!.Ship.ShipId).Ship.SideId == subject.SideId);
            var observerBot = bots[observerIdx];
            var observer = server.Ships.Single(s => s.Ship.Id == observerBot.ShipAssignment!.Ship.ShipId).Ship;
            var thirdBot = bots.First(b => b != observerBot && b.ShipAssignment!.Ship.ShipId != subjectId);
            var third = server.Ships.Single(s => s.Ship.Id == thirdBot.ShipAssignment!.Ship.ShipId).Ship;
            // P368: the same-side observer shares the subject's home sector (the
            // invariant the sector-wide RELOAD relay needs); the cross-side third
            // is now legitimately in a DIFFERENT sector (per-side spawn). The old
            // assertion forced everyone into one sector — the very bug P368 fixes.
            Assert.Equal(subject.Sector!.Id, observer.Sector!.Id);
            Assert.NotEqual(subject.Sector!.Id, third.Sector!.Id);
            return new Seat(server, bots, subject, bots[0], observerBot);
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            server.Dispose();
            throw;
        }
    }
}
