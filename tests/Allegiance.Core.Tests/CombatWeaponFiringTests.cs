using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// Verification of the first combat slice: <see cref="Weapon"/> firing
/// (<c>CweaponIGC::FireWeapon</c>, <c>src/Igc/weaponIGC.cpp:142-348</c>) and
/// the <see cref="Projectile"/> as a real sector model (<c>CprojectileIGC</c>,
/// <c>projectileIGC.cpp</c>). Everything here reads the weapon/projectile
/// <b>type</b> (speed, lifespan, dispersion, energy-per-shot, ammo-per-shot)
/// from the decoded <c>StaticCore</c> — nothing is hardcoded. Damage, shields,
/// death and the collision→damage hook are deliberately the <em>next</em> task
/// and are not exercised here.
/// </summary>
public class CombatWeaponFiringTests
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
    // hardpoint at mount 0 so weapon selection (IsWeaponSelected) works, paired
    // with the cheapest dispersion-0 weapon that fires a real projectile and
    // costs no more energy than the hull carries.
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile)> Fight =
        new(() =>
        {
            var core = RepresentativeCore.Value;
            foreach (var hull in core.HullTypes.Values
                         .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                         .OrderBy(h => h.HullId))
            {
                foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                             .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                             .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
                             .OrderBy(w => w.EnergyPerShot))
                {
                    if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt))
                        return (hull, p, pt);
                }
            }
            throw new InvalidOperationException("no fightable hull+weapon in the representative core");
        });

    private static Ship NewShip() => new(
        shipId: 9301,
        hull: Fight.Value.hull,
        position: new Vector3(10f, -20f, 30f),
        velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    /// <summary>Equip the fightable weapon, select it (all-weapons state), and
    /// cap the burst to exactly one projectile by exhausting the limiting
    /// resource (ammo for a projectile weapon, energy for an energy weapon) so
    /// assertions never race a multi-projectile burst.</summary>
    private static Weapon EquipSingleShot(Ship ship)
    {
        var (_, weaponType, _) = Fight.Value;
        ship.StateM |= ShipControlState.AllWeapons;
        if (weaponType.CAmmoPerShot > 0)
            ship.Ammo = 1;                            // one round, then the burst stops
        else
            ship.Energy = weaponType.EnergyPerShot;   // exactly one shot drains below threshold
        return ship.MountWeapon(weaponType);
    }

    /// <summary>Tick the mission in small single-chunk steps (each &lt; 0.25 s
    /// ⇒ n == 1 chunk), continuing from its current <see cref="Mission.LastUpdate"/>
    /// (so it is safe to call repeatedly), until <paramref name="predicate"/>
    /// holds or false after <paramref name="maxSteps"/>.</summary>
    private static bool TickUntil(Mission m, int maxSteps, Func<bool> predicate)
    {
        double t = m.LastUpdate;
        for (int i = 0; i < maxSteps; i++)
        {
            t += 0.021;
            m.Update(t);
            if (predicate()) return true;
        }
        return false;
    }

    // ── heading + speed ─────────────────────────────────────────────────────

    /// <summary>
    /// A projectile fired from a <b>stationary</b> ship travels along the ship's
    /// heading at the weapon's decoded speed. Driven deterministically: after
    /// activation the weapon fires once its cooldown elapses; with a zero-offset
    /// muzzle and identity orientation (forward <c>(0,0,-1)</c>) the projectile
    /// velocity is exactly <c>Speed · (0,0,-1)</c> = <c>(0,0,-Speed)</c>. Speed
    /// and lifespan come from the decoded <c>DataProjectileTypeIGC</c>.
    /// </summary>
    [Fact]
    public void Projectile_travels_along_heading_at_weapon_speed()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);
        var weapon = EquipSingleShot(ship);

        var (_, _, pt) = Fight.Value;
        float speed = pt.Speed;
        Assert.Equal(Vector3.Zero, ship.Velocity);          // stationary as advertised
        Assert.Equal(-1f, ship.Orientation.GetForward().Z); // identity → forward along −z

        weapon.Update(0.0);                                  // select → activate, cooldown armed
        Assert.True(weapon.Active, "a selected weapon must activate on Update");
        Assert.Empty(sector.Models.OfType<Projectile>());    // still on cooldown: no shot

        weapon.Update(weapon.GetDtBurst() + 0.002);          // first chunk past cooldown → one shot
        var proj = Assert.Single(sector.Models.OfType<Projectile>());

        Assert.Equal(pt, proj.ProjectileType);               // decoded type, not hardcoded
        Assert.Equal(speed, proj.ProjectileType.Speed);
        Assert.Equal(new Vector3(0f, 0f, -speed), proj.Velocity);  // exact heading + speed
        Assert.Equal(speed, proj.Velocity.Length());
        Assert.Equal(ship.Position, proj.Position);          // spawned at the ship's centre (no hull-mesh offset)
    }

    // ── membership + ticks with the sector ────────────────────────────────

    /// <summary>
    /// Fired through the <b>authoritative tick</b> (only <c>Mission.Update</c>
    /// creates and moves projectiles), the projectile appears in the sector's
    /// model list and ticks with it — the sector's model loop advances its
    /// position by <c>velocity·Δt</c> each chunk, exactly like any other model.
    /// </summary>
    [Fact]
    public void Projectile_lives_in_and_ticks_with_the_sector()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);
        var weapon = EquipSingleShot(ship);

        Assert.True(TickUntil(mission, 40, () => sector.Models.OfType<Projectile>().Any()),
                    "a selected weapon must eventually put a projectile in the sector");

        var proj = Assert.Single(sector.Models.OfType<Projectile>());   // capped burst → exactly one
        Assert.Contains(proj, sector.Models);                            // it is a real sector model
        float speed = proj.ProjectileType.Speed;
        Assert.Equal(new Vector3(0f, 0f, -speed), proj.Velocity);        // heading intact through the tick

        // Sample, advance the tick, sample again: constant velocity ⇒
        // displacement over the elapsed mission time equals velocity·elapsed.
        var p0 = proj.Position;
        var t0 = mission.LastUpdate;
        mission.Update(t0 + 0.071);                                      // < 0.25 ⇒ n = 1 chunk
        AssertProjectileMovedAlong(proj, p0, t0, mission.LastUpdate);

        // And again — the projectile keeps riding the tick, not a one-shot move.
        var p1 = proj.Position;
        var t1 = mission.LastUpdate;
        mission.Update(t1 + 0.023);                                      // another chunk
        AssertProjectileMovedAlong(proj, p1, t1, mission.LastUpdate);
    }

    /// <summary>Verify the projectile displaced by <c>Velocity·elapsed</c> over
    /// <c>[tSample, now]</c> (constant velocity), i.e. it was driven by the
    /// sector's model loop between the two samples.</summary>
    private static void AssertProjectileMovedAlong(Projectile proj, Vector3 pSample, double tSample, double now)
    {
        Assert.Equal(proj.ProjectileType.Speed, proj.Velocity.Length());
        var actual = proj.Position - pSample;
        var expected = proj.Velocity * (float)(now - tSample);
        // Parallel to velocity…
        var cross = Vector3.CrossProduct(actual, proj.Velocity);
        Assert.True(cross.LengthSquared() < 1e-3f, $"projectile must move along its velocity; cross={cross}");
        // …and travelled velocity·elapsed (per-chunk float integration may round
        // by an ulp or two across chunks, so compare with a small tolerance).
        Assert.Equal(expected.X, actual.X, 4);
        Assert.Equal(expected.Y, actual.Y, 4);
        Assert.Equal(expected.Z, actual.Z, 4);
    }

    // ── expiry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// ⭐ The leak check: a projectile must expire and remove itself from the
    /// sector after its <b>decoded</b> lifespan, so the sector's model count
    /// returns to its pre-fire value. Leaked projectiles would accumulate
    /// invisibly; this pins the removal. Fired through the tick, then the
    /// mission is advanced past the projectile's expiry and asserted empty of
    /// projectiles with the count restored.
    /// </summary>
    [Fact]
    public void Projectile_expires_and_restores_the_model_count()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);
        EquipSingleShot(ship);

        int preFire = sector.Models.Count;                        // just the ship
        Assert.True(TickUntil(mission, 40, () => sector.Models.OfType<Projectile>().Any()),
                    "a selected weapon must eventually put a projectile in the sector");
        var proj = Assert.Single(sector.Models.OfType<Projectile>());
        int modelCountAlive = sector.Models.Count;
        Assert.Equal(preFire + 1, modelCountAlive);           // exactly one more model while alive

        // Advance until the projectile's own expiry time is passed.
        Assert.True(TickUntil(mission, 400, () => mission.LastUpdate > proj.TimeExpire),
                    "must be able to tick past the projectile's lifespan");

        // The sector has no projectiles left and its count is back to pre-fire.
        Assert.Empty(sector.Models.OfType<Projectile>());
        Assert.Equal(preFire, sector.Models.Count);
    }

    // ── cooldown ──────────────────────────────────────────────────────────

    /// <summary>
    /// Firing respects the cooldown — the C++ gates on <c>m_nextFire &lt; now</c>
    /// (<c>weaponIGC.cpp:149</c>) and advances it by <c>dtimeBurst</c> per shot
    /// (<c>weaponIGC.cpp:310</c>). With energy and ammo plentiful, two fire
    /// attempts separated by less than a burst interval produce exactly one
    /// projectile; once the cooldown elapses and energy is replenished, a second
    /// shot lands. Energy is re-fed by the test because ship recharge is a later
    /// task — this isolates the cooldown gate.
    /// </summary>
    [Fact]
    public void Firing_respects_cooldown()
    {
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);
        var weapon = EquipSingleShot(ship);

        // Healthy pools so cooldown is the only gate (single shots forced by
        // small pools would muddy the count — here we observe multiples freely).
        ship.Energy = ship.HullType.MaxEnergy;
        ship.Ammo = 20;

        float burst = weapon.GetDtBurst();
        double t = burst + 0.007;                          // arm + past the first cooldown
        mission.Update(t);                                 // activates & fires the first shot
        int countAfterFirst = sector.Models.OfType<Projectile>().Count();
        Assert.Equal(1, countAfterFirst);

        // Well within the burst interval (cooldown not yet elapsed) → no second
        // shot, despite abundant energy/ammo.
        mission.Update(t + 0.008);
        int countWithinCooldown = sector.Models.OfType<Projectile>().Count();
        Assert.Equal(countAfterFirst, countWithinCooldown);

        // After another full burst interval, with energy topped back up, a second
        // shot lands — proving the gate is time, not energy/ammo starvation.
        ship.Energy = ship.HullType.MaxEnergy;            // hand-fed recharge (economy is a later task)
        mission.Update(t + burst + 0.006);
        Assert.True(sector.Models.OfType<Projectile>().Count() >= 2,
                    "once the cooldown elapses and energy returns, the weapon must fire again");
    }

    // ── ammo ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A projectile weapon (cAmmoPerShot &gt; 0) consumes a round per shot and
    /// stops firing once the magazine is empty (<c>weaponIGC.cpp:317,319</c>),
    /// and the ship's ammo is written back clamped at 0. Hunts the
    /// representative core for a genuine ammo weapon (in case the default pair
    /// is an energy weapon), then drives it through the tick and asserts the
    /// magazine empties after exactly its ammunition's worth of shots.
    /// </summary>
    [Fact]
    public void Projectile_weapon_consumes_ammo_and_stops_when_empty()
    {
        var core = RepresentativeCore.Value;
        var ammoWeaponType = core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type)
            .Where(w => w.CAmmoPerShot > 0 && w.Dispersion == 0f
                        && core.ProjectileTypes.ContainsKey(w.ProjectileTypeId))
            .OrderBy(w => w.EnergyPerShot)
            .First();                                  // ammo weapons exist in the representative core
        var mission = NewStartedMission();
        var sector = mission.Sectors[0];
        var ship = NewShip();
        mission.PlaceShip(ship, sector);
        ship.StateM |= ShipControlState.AllWeapons;
        ship.Energy = ship.HullType.MaxEnergy;   // plentiful: ammo is the only gate
        ship.Ammo = 2;
        ship.MountWeapon(ammoWeaponType);   // driven by the ship's tick

        int firedTotal = 0;
        double t = 0.0;
        for (int i = 0; i < 80 && ship.Ammo > 0; i++)
        {
            t += 0.044;
            mission.Update(t);
            var nowLive = sector.Models.OfType<Projectile>().Count();
            if (nowLive > firedTotal) firedTotal = nowLive;   // peak simultaneous live count
        }

        Assert.True(firedTotal >= 1, "an ammo-loaded weapon must fire at least once");
        Assert.Equal(0, ship.Ammo);                       // magazine exhausted to exactly 0
    }
}
