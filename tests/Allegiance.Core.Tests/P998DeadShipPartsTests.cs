using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P998 — a dying ship's parts must die with it. Native <c>KillShipEvent</c>
/// walks the dead root ship's parts, rolls one 1-in-5 treasure per part and
/// terminates EVERY one of them (<c>FedSrv/FedSrv.CPP:11686-11696</c>:
/// "Dead ship ... create treasures for all of its parts … <c>p-&gt;Terminate();</c>"),
/// zeroes ammo and fuel (<c>:11698-11728</c>) and only then swaps the hull to the
/// eject pod / docked lifepod (<c>:11942</c>, <c>:12052</c>) — which is why
/// <c>CshipIGC::SetBaseHullType</c> can assert <c>m_parts.n() == 0</c>
/// (<c>Igc/shipIGC.cpp:1377</c>). The port rolled the loot but never removed the
/// parts, so an ejected pod inherited the dead fighter's guns, shield, booster
/// and cargo.
///
/// <para>These are the SIM-level proofs: (1) what a pod could do BEFORE the fix
/// — a hull swap alone leaves every part mounted, and the inherited shield,
/// booster and cargo stay live (a pod hull with an energy pool also fires the
/// inherited gun); (2) what <see cref="Ship.TerminatePartsOnDeath"/> now removes;
/// (3) that the strip is the native scalar reset too (<c>Igc/shipIGC.cpp:1428-1437</c>);
/// (4) that the strip deliberately raises no <c>LOADOUT_CHANGE</c> (native's
/// per-part <c>c_lcRemoved</c> has no wire effect on the server —
/// <c>FedSrv/FedSrv.CPP:13530-13545</c> — while the hull swap that follows does)
/// whereas <see cref="Ship.ClearLoadout"/> still does.</para>
/// </summary>
public class P998DeadShipPartsTests
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

    private static StaticCore Core() => RepresentativeCore.Value;

    private static (Mission mission, RecordingIgcSite site) NewStartedMission()
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var site = new RecordingIgcSite();
        var mission = Mission.Assemble(core, SomeMap.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], site);
        mission.Stage = MissionStage.Started;
        return (mission, site);
    }

    /// <summary>The side's own lifepod hull — the hull both of the port's death
    /// paths swap in (<c>FedSrv.CPP:11942</c> eject, <c>:12052</c> respawn).
    /// Every shipped pod carries <c>MaxEnergy 0</c>/<c>RechargeRate 0</c>, so the
    /// port's per-tick energy clamp starves an inherited gun on the REAL pod; the
    /// synthetic <see cref="PodWithEnergyPool"/> below isolates the part itself.</summary>
    private static DataHullTypeIGC LifepodHull() =>
        Core().HullTypes[Core().Civilizations.Values.OrderBy(c => c.CivilizationId).First().Lifepod];

    /// <summary>The side's real pod hull, given a live energy pool — the same
    /// technique <c>EnergyRechargeTests</c> uses to isolate hull-energy behaviour
    /// from the shipped 0/0 pod statistics.</summary>
    private static DataHullTypeIGC PodWithEnergyPool() =>
        LifepodHull() with { MaxEnergy = 522f, RechargeRate = 40f, MaxAmmo = 400, MaxFuel = 300f };

    /// <summary>A real combat hull with a fixed hardpoint 0 and a weapon that fits
    /// it — plus a shield and an afterburner that hull can mount, so the test can
    /// prove the pod inherits (and then loses) all three kinds of part.</summary>
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC gun,
        DataPartTypeIGC? shield, DataPartTypeIGC? booster, DataPartTypeIGC pack)> Loadout =
        new(() =>
        {
            var core = Core();
            foreach (var hull in core.HullTypes.Values
                         .Where(h => h.GroupId >= 0 && h.MaxAmmo > 0 && h.MaxEnergy > 0
                                   && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                         .OrderBy(h => h.HullId))
            {
                var gun = core.PartTypes.Values
                    .Where(p => p.EquipmentType == EquipmentType.Weapon
                             && p.Type is DataPartTypeIGC.Weapon w
                             && w.Dispersion == 0f && w.CAmmoPerShot > 0
                             && w.EnergyPerShot <= hull.MaxEnergy
                             && core.ProjectileTypes.ContainsKey(w.ProjectileTypeId)
                             && hull.CanMountPart(p, 0))
                    .OrderBy(p => p.PartId)
                    .Cast<DataPartTypeIGC?>()
                    .FirstOrDefault();
                if (gun is not { } gunType) continue;
                var shield = core.PartTypes.Values
                    .Where(p => p.EquipmentType == EquipmentType.Shield && hull.CanMountPart(p, 0))
                    .OrderBy(p => p.PartId).Cast<DataPartTypeIGC?>().FirstOrDefault();
                // PCore001's shipped afterburner records carry a non-positive
                // MaxThrust (decoded straight from the art file), so give the same
                // part a real thrust value: this test is about the PART being
                // inherited, not about the artwork's balance numbers.
                var boosterBase = core.PartTypes.Values
                    .Where(p => p.EquipmentType == EquipmentType.Afterburner && hull.CanMountPart(p, 0))
                    .OrderBy(p => p.PartId).Cast<DataPartTypeIGC?>().FirstOrDefault();
                DataPartTypeIGC? booster = boosterBase is { } b
                    ? b with { Type = new DataPartTypeIGC.Afterburner(1f, 20000f, 4f, 4f, 0, 0) }
                    : null;
                var pack = core.PartTypes.Values
                    .Where(p => p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 0)
                    .OrderBy(p => p.PartId).First();
                return (hull, gunType, shield, booster, pack);
            }
            throw new InvalidOperationException("no hull with a mountable gun/shield/booster in the representative core");
        });

    /// <summary>A ship in flight, owned by side 0, carrying a FULL loadout: a
    /// realized fixed gun, a charged shield, an afterburner and an ammo pack in
    /// the cargo hold, with live ammo/fuel/energy/ore pools.</summary>
    private static Ship NewLoadedShip(Mission mission, Sector sector, short id = 4001)
    {
        var (hull, gun, shield, booster, pack) = Loadout.Value;
        var ship = new Ship(id, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);
        Assert.NotNull(ship.MountPart(gun, 1, 0, ExpandedPartData.FullyMounted));
        if (shield is { } sh) Assert.NotNull(ship.MountPart(sh, 1, 0, ExpandedPartData.FullyMounted));
        if (booster is { } bo) Assert.NotNull(ship.MountPart(bo, 1, 0, ExpandedPartData.FullyMounted));
        Assert.NotNull(ship.MountPart(pack, 3, unchecked((byte)(-1)), ExpandedPartData.FullyMounted));
        ship.Ammo = (short)Math.Min((int)ship.HullType.MaxAmmo, 400);
        ship.SetFuel(ship.HullType.MaxFuel);
        ship.Energy = ship.EffectiveMaxEnergy();
        ship.Ore = 25f;
        return ship;
    }

    /// <summary>Tick the mission in single-chunk steps from its own clock.</summary>
    private static void Tick(Mission mission, int steps)
    {
        double t = mission.LastUpdate;
        for (int i = 0; i < steps; i++)
        {
            t += 0.045;   // < 0.25 s ⇒ one chunk per Update
            mission.Update(t);
        }
    }

    private static int ShotsBy(Sector sector, Ship ship) =>
        sector.Models.OfType<Projectile>().Count(p => ReferenceEquals(p.Launcher, ship));

    // ── (1) WHAT THE POD COULD DO BEFORE THE FIX ─────────────────────────────

    /// <summary>
    /// ⭐ The pre-P998 defect mechanism, pinned: <see cref="Ship.ChangeHull"/>
    /// alone — the only loadout-relevant step the port's eject/respawn paths ran
    /// before P998 — is the native hull swap MINUS the part termination, so the
    /// pod keeps the dead fighter's mounted gun, charged shield, booster and
    /// cargo. Native cannot reach that state: its <c>SetBaseHullType</c> asserts
    /// <c>m_parts.n() == 0</c> (<c>Igc/shipIGC.cpp:1377</c>) because
    /// <c>KillShipEvent</c> already terminated every part
    /// (<c>FedSrv/FedSrv.CPP:11686-11696</c>).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void A_pod_hull_swap_alone_inherits_the_dead_loadout()
    {
        Require.Artwork();
        var (mission, _) = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewLoadedShip(mission, sector);
        int mountedParts = ship.Parts.Count;
        Assert.True(mountedParts >= 3, "the fixture must mount gun + shield + booster");
        Assert.True(ship.ShieldFraction > 0f, "the fixture's shield must be charged");

        ship.ChangeHull(LifepodHull());   // the port's pre-P998 death hull swap

        Assert.Equal(mountedParts, ship.Parts.Count);   // parts survive the hull swap
        Assert.NotEmpty(ship.Weapons);                  // …and the realized gun does
        Assert.True(ship.ShieldFraction > 0f);          // …and the shield charge
        Assert.NotNull(ship.CargoHold[0]);              // …and the cargo hold

        // Mass/thrust: this port's Mass is the hull record's alone (`Ship.Mass`,
        // shipIGC.cpp:4561-style hull mass) — parts never contributed mass here,
        // so nothing part-related ever moved a pod's flight envelope. Native's
        // AddPart/DeletePart do adjust mass and SetBaseHullType resets it to the
        // hull's (shipIGC.cpp:1401), so both sides agree on the outcome.
        Assert.Equal(ship.HullType.Mass, ship.Mass);

        Console.WriteLine($"[p998] pre-fix state reproduced: pod hull {ship.HullType.HullId} kept "
            + $"{ship.Parts.Count} part(s), {ship.Weapons.Count} realized weapon(s), shield {ship.ShieldFraction:0.##}, "
            + $"cargo {(ship.CargoHold[0] is { } c ? c.Amount : 0)}x pack");
    }

    /// <summary>
    /// ⭐ FIRE: an inherited gun is a REAL, realized weapon. On a pod whose hull
    /// carries an energy pool it fires the dead ship's rounds; the same gun on the
    /// same pod after <see cref="Ship.TerminatePartsOnDeath"/> fires nothing.
    /// (Every SHIPPED pod has <c>MaxEnergy 0</c> — see
    /// <see cref="LifepodHull"/> — so on a stock pod the port's per-tick energy
    /// clamp starves the gun; native's own hull swap also zeroes
    /// <c>m_energy</c> there, shipIGC.cpp:1437. The gun was still mounted and
    /// selected, which is the defect this task removes.)
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_inherited_gun_fires_on_a_pod_until_the_death_strip_removes_it()
    {
        Require.Artwork();
        var (mission, _) = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewLoadedShip(mission, sector);
        ship.ChangeHull(PodWithEnergyPool());
        ship.StateM |= ShipControlState.AllWeapons;

        Tick(mission, 40);
        int inheritedShots = ShotsBy(sector, ship);
        Assert.True(inheritedShots > 0,
            "a pod that kept the inherited gun really fires it — the pre-P998 behaviour this task removes");

        // The fix: the same pod, same hull, same trigger — no gun, no shot.
        // (Clear the in-flight rounds first: they expire on their own lifespan.)
        ship.TerminatePartsOnDeath();
        foreach (var round in sector.Models.OfType<Projectile>().ToArray())
            sector.Remove(round);
        Tick(mission, 40);
        Assert.Empty(ship.Weapons);
        Assert.Equal(0, ShotsBy(sector, ship));
        Console.WriteLine($"[p998] inherited gun fired {inheritedShots} shot(s) on a pod; after TerminatePartsOnDeath: 0 more");
    }

    /// <summary>
    /// ⭐ SHIELD: the inherited shield still absorbs damage (a small hit never
    /// reaches the pod's hull), and stops absorbing the moment the strip removes
    /// the part.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_inherited_shield_absorbs_damage_on_a_pod_until_the_death_strip_removes_it()
    {
        Require.Artwork();
        var (mission, _) = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewLoadedShip(mission, sector);
        Assert.True(ship.ShieldFraction > 0f);
        ship.ChangeHull(LifepodHull());
        Assert.True(ship.ShieldFraction > 0f);

        float hullBefore = ship.Fraction;
        float shieldBefore = ship.ShieldFraction;
        ship.ReceiveDamage(0, 1.0f, mission.LastUpdate, ship.Position, Vector3.Zero, null);
        Assert.Equal(hullBefore, ship.Fraction);                 // fully absorbed
        Assert.True(ship.ShieldFraction < shieldBefore);         // the inherited shield paid for it

        ship.TerminatePartsOnDeath();
        float hullAfterStrip = ship.Fraction;
        ship.ReceiveDamage(0, 1.0f, mission.LastUpdate, ship.Position, Vector3.Zero, null);
        Assert.True(ship.Fraction < hullAfterStrip, "with the shield part terminated the same hit reaches the pod's hull");
        Assert.Equal(0f, ship.ShieldFraction);
    }

    /// <summary>
    /// ⭐ BOOST + CARGO: the inherited afterburner gives the pod a real thrust
    /// boost while it has fuel, and the inherited cargo pack is what can refuel
    /// it (<see cref="Ship.TryAutoReloadFuel"/> drains a cargo fuel pack on the
    /// zero-transition). After the strip neither exists.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void An_inherited_afterburner_boosts_a_pod_and_the_strip_ends_it()
    {
        Require.Artwork();
        var (mission, _) = NewStartedMission();
        var sector = mission.Sectors[0];
        // A cargo FUEL pack, so the boost proof covers the reload path too.
        var fuelPack = Core().PartTypes.Values
            .Where(p => p.Type is DataPartTypeIGC.Pack pk && pk.PackType == 1)
            .OrderBy(p => p.PartId).First();
        var (hull, gun, _, booster, _) = Loadout.Value;
        var boosterType = booster
            ?? throw new InvalidOperationException("the representative core must have a mountable afterburner");
        var ship = new Ship(4002, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(ship, sector);
        Assert.NotNull(ship.MountPart(gun, 1, 0, ExpandedPartData.FullyMounted));
        Assert.NotNull(ship.MountPart(boosterType, 1, 0, ExpandedPartData.FullyMounted));
        Assert.NotNull(ship.MountPart(fuelPack, 3, unchecked((byte)(-1)), ExpandedPartData.FullyMounted));

        ship.ChangeHull(PodWithEnergyPool());   // the fixtures' pod hull carries fuel capacity
        ship.SetFuel(200f);
        ship.StateM |= ShipControlState.AfterburnerButton;
        float boosted = ship.MovementSpeedLimit();

        ship.TerminatePartsOnDeath();
        ship.SetFuel(200f);   // refuel so only the PART difference remains
        float cruise = ship.MovementSpeedLimit();

        Assert.True(boosted > cruise,
            $"the inherited afterburner must give the pod real extra thrust ({boosted:0.##} vs {cruise:0.##}); "
            + $"booster part {boosterType.PartId} maxThrust={(boosterType.Type is DataPartTypeIGC.Afterburner ba ? ba.MaxThrust : -1)} "
            + $"hullThrust={ship.HullType.Thrust} fuel={ship.Fuel} stateM=0x{ship.StateM:X} "
            + $"mountedAfterburner={(ship.Parts.Any(p => p.EquipmentType == EquipmentType.Afterburner) ? "yes" : "no")}");
        Assert.All(ship.CargoHold, p => Assert.Null(p));
    }

    // ── (2) THE FIX AT THE SIM LEVEL ─────────────────────────────────────────

    /// <summary>
    /// ⭐ <see cref="Ship.TerminatePartsOnDeath"/> is native's
    /// <c>p-&gt;Terminate()</c> over the whole part list — equipped AND cargo
    /// (native keeps both in one list, <c>Igc/shipIGC.cpp:1516-1549</c>) — plus
    /// <c>SetBaseHullType</c>'s scalar resets (<c>Igc/shipIGC.cpp:1428-1437</c>).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Terminate_parts_on_death_empties_the_loadout_cargo_and_pools()
    {
        Require.Artwork();
        var (mission, _) = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewLoadedShip(mission, sector);
        Assert.NotEmpty(ship.Parts);
        Assert.NotNull(ship.CargoHold[0]);

        ship.TerminatePartsOnDeath();

        Assert.Empty(ship.Parts);                       // p->Terminate() per part, FedSrv.CPP:11695
        Assert.Empty(ship.Weapons);                     // the realized gun dies with its part
        Assert.All(ship.CargoHold, p => Assert.Null(p)); // cargo parts are parts too
        Assert.Equal(0f, ship.ShieldFraction);          // no shield part ⇒ no charge
        Assert.Equal((short)0, ship.Ammo);              // m_ammo = 0, shipIGC.cpp:1430
        Assert.Equal(0f, ship.Fuel);                    // m_fuel = 0, shipIGC.cpp:1429
        Assert.Equal(0f, ship.Energy);                  // m_energy = 0, shipIGC.cpp:1437
        Assert.Equal(0f, ship.Ore);                     // m_fOre = 0, shipIGC.cpp:1428
    }

    /// <summary>
    /// The strip leaves the ship otherwise untouched (position/side/hull): it is
    /// the hull swap that changes those, exactly as native keeps the terminate
    /// loop and <c>SetBaseHullType</c> separate steps.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Terminate_parts_on_death_does_not_change_the_hull_or_position()
    {
        Require.Artwork();
        var (mission, _) = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewLoadedShip(mission, sector);
        var hull = ship.HullType;
        var position = ship.Position;

        ship.TerminatePartsOnDeath();

        Assert.Equal(hull.HullId, ship.HullType.HullId);
        Assert.Equal(position, ship.Position);
        Assert.Equal((short)0, ship.SideId);
    }

    // ── (3) THE WIRE-EVENT CONTRACT ──────────────────────────────────────────

    /// <summary>
    /// P173 seam: <see cref="Ship.ClearLoadout"/> (a purchase/board rebuild)
    /// still raises one <c>LoadoutChangeEvent(Removed)</c>, but the DEATH strip
    /// raises none. Native's per-part terminate raises <c>c_lcRemoved</c>, which
    /// on the server only re-sends an area-effect weapon's target
    /// (<c>FedSrv/FedSrv.CPP:13530-13545</c>) — the loadout itself reaches clients
    /// from the hull swap's whole-loadout message / EJECT / ENTER_LIFEPOD — so a
    /// silent strip avoids publishing a "dead hull, no parts" snapshot no native
    /// client would ever see (and which would otherwise clobber the client's
    /// P996 saved loadout, native <c>m_loadouts</c>).
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void The_death_strip_is_wire_silent_while_a_loadout_rebuild_is_not()
    {
        Require.Artwork();
        var (mission, site) = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewLoadedShip(mission, sector);

        site.Reset();
        ship.TerminatePartsOnDeath();
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("LoadoutChangeEvent"));

        // Control: the same ship's ordinary rebuild path DOES publish.
        Assert.NotNull(ship.MountPart(Loadout.Value.gun, 1, 0, ExpandedPartData.FullyMounted));
        site.Reset();
        ship.ClearLoadout();
        Assert.Contains(site.Calls, c => c.StartsWith("LoadoutChangeEvent"));
    }
}
