using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P91 — live (real in-process <see cref="Srv"/> over loopback, mirroring
/// <see cref="MissileBroadcastTests"/>' 3-bot / 2-side construction) and domain
/// proofs that <see cref="Sector.ResolveCombatCollisions"/> now resolves
/// missile↔ship collisions with the <b>identical</b> rules as projectile↔ship:
/// a real cross-side missile deals real, computable damage via the reused
/// <see cref="Ship.ReceiveDamage"/> (shields included), is removed from the
/// sector on impact, and raises <see cref="IIgcSite.MissileDestroyedEvent"/> so
/// the same-side peer who saw it launch also receives a real
/// <c>FM_S_MISSILE_DESTROYED(83)</c> — the P91 consistency fix, proven live on
/// a hit (not just expiry). A same-side (friendly) shot deals no damage and
/// passes through, exactly like the existing projectile rule.
/// </summary>
/// <remarks>
/// Everything here is data-derived from the real <c>PCore001.igc</c> static
/// core — nothing hardcoded. The test combos (missile+hull+shield, weapon for
/// coexistence) are selected from the core, and every expected damage/shield
/// number is computed from the decoded <c>DamageConstants</c> matrix and the
/// type records, the same receipt <see cref="CombatShieldTests"/> uses. The
/// live tests read <c>Ship.Fraction</c>/<c>Ship.ShieldFraction</c> directly off
/// the server-side <see cref="Ship"/> (ground truth — the wire hull slot is only
/// streamed for moving ships, so reading the live ship avoids that gating) and
/// assert the <b>actual</b> numbers, not just "some damage".
/// </remarks>
[Collection("LiveNetwork")]
public sealed class MissileCombatTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    // ── combat combo selection (data-derived, mirroring CombatShieldTests) ──

    /// <summary>A (missile, hull) pair where one cross-side missile hit lands a
    /// clean, survivable hull delta (<c>Power·dtmArmor/MaxHP</c>) with a usable
    /// straight-line reach (travel = speed·lifespan ≥ 90 m) so we can aim a real
    /// impact within its range. Ordered by ascending Power then ascending hull HP
    /// so the least destructive surviving hit is chosen (most stable to measure).</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, DataHullTypeIGC Hull, float ExpectedDelta)> CrossHit = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.InitialSpeed > 0f && mm.Power > 0f && mm.Lifespan >= 4f && mm.Lifespan <= 8f)
            .OrderBy(mm => mm.Power))
        {
            if (m.InitialSpeed * m.Lifespan < 90f) continue;
            foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
            {
                float dtm = Dtm(core, m.DamageType, hull.DefenseType);
                if (dtm <= 0f) continue;
                float delta = m.Power * dtm / hull.HitPoints;
                if (delta >= 0.070f && delta <= 0.650f)
                    return (m, hull, delta);
            }
        }
        throw new InvalidOperationException("no cross-side (missile, hull) combo in representative core");
    });

    /// <summary>A (missile, shield, hull) trio where a fresh full shield absorbs a
    /// real, computable fraction (<c>Power·dtmShield/MaxStrength</c> ∈ [0.2,
    /// 0.45], comfortably inside the useful band) — the CombatShieldTests
    /// "SmallHit" recipe applied to a missile warhead — AND the same raw hit is
    /// survivable on an unshielded hull (so the down-shield full-leak half is
    /// provable too).</summary>
    private static readonly Lazy<(DataMissileTypeIGC Missile, DataPartTypeIGC ShieldPart, DataPartTypeIGC.Shield ShieldType, float ExpectedAbsorbed, DataHullTypeIGC Hull, float DtmHull)> ShieldCombo = new(() =>
    {
        var core = RepresentativeCore();
        foreach (var m in core.MissileTypes.Values
            .Where(mm => mm.Power > 0f && mm.InitialSpeed > 0f && mm.Lifespan >= 4f && mm.Lifespan <= 8f)
            .OrderBy(mm => mm.Power))
        {
            foreach (var sp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Shield))
            {
                var st = (DataPartTypeIGC.Shield)sp.Type;
                if (st.MaxStrength <= 0f) continue;
                float dtmShield = Dtm(core, m.DamageType, st.DefenseType);
                if (dtmShield <= 0f) continue;
                float absorbed = m.Power * dtmShield / st.MaxStrength;
                if (absorbed < 0.2f || absorbed > 0.45f) continue;
                foreach (var hull in core.HullTypes.Values.OrderBy(h => h.HitPoints))
                {
                    float dtmHull = Dtm(core, m.DamageType, hull.DefenseType);
                    if (dtmHull > 0f && m.Power * dtmHull < hull.HitPoints)
                        return (m, sp, st, absorbed, hull, dtmHull);
                }
            }
        }
        throw new InvalidOperationException("no missile-shield-hull combo in representative core");
    });

    /// <summary>A (weapon, projectile, missile, two hulls) quartet for the
    /// coexistence proof: a dispersion-0 weapon whose projectile has straight-line
    /// reach ∈ [200, 400] m (so a single round lands on a near target and expires
    /// before a far target), paired with a missile whose reach is well beyond the
    /// weapon's (≥ weapon reach + 400 m) so it reaches the far target in the same
    /// tick window. Each ordnance picks a hull whose damage cell for its type is
    /// &gt; 0 and whose single hit is survivable.</summary>
    private static readonly Lazy<(DataPartTypeIGC.Weapon Weapon, DataProjectileTypeIGC Projectile, DataMissileTypeIGC Missile, DataHullTypeIGC Ppvp, DataHullTypeIGC MsHull, float PDelta, float MDelta, float WeaponTravel)> Coexist = new(() =>
    {
        var core = RepresentativeCore();
        var (weapon, proj) = (
            from hull in core.HullTypes.Values.Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
            from wp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy)
            let pt = core.ProjectileTypes[wp.ProjectileTypeId]
            where pt.Power > 0f
            let travel = pt.Speed * pt.Lifespan
            where travel >= 200f && travel <= 400f
            select (wp, pt)).First();

        float weaponTravel = proj.Speed * proj.Lifespan;

        foreach (var m in core.MissileTypes.Values.Where(mm => mm.InitialSpeed > 0f && mm.Power > 0f))
        {
            if (m.InitialSpeed * m.Lifespan < weaponTravel + 400f) continue;
            foreach (var ph in core.HullTypes.Values.Where(h => Dtm(core, proj.DamageType, h.DefenseType) > 0f
                                                                && proj.Power * Dtm(core, proj.DamageType, h.DefenseType) < h.HitPoints)
                                                     .OrderBy(h => h.HitPoints))
            {
                float pDelta = proj.Power * Dtm(core, proj.DamageType, ph.DefenseType) / ph.HitPoints;
                if (pDelta < 0.040f || pDelta >= 1f) continue;
                foreach (var mh in core.HullTypes.Values.Where(h => Dtm(core, m.DamageType, h.DefenseType) > 0f
                                                                    && m.Power * Dtm(core, m.DamageType, h.DefenseType) < h.HitPoints)
                                                        .OrderBy(h => h.HitPoints))
                {
                    float mDelta = m.Power * Dtm(core, m.DamageType, mh.DefenseType) / mh.HitPoints;
                    if (mDelta >= 0.050f && mDelta < 1f)
                        return (weapon, proj, m, ph, mh, pDelta, mDelta, weaponTravel);
                }
            }
        }
        throw new InvalidOperationException("no coexist quartet in representative core");
    });

    // ── live loopback helpers (3-bot / 2-side construction, P90 recipe) ──────

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Missile Combat Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 666u, DwCookie = 0x666u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 66000000000000000ul, DrmHash = "missilecombat",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>
    /// A booted 3-bot / 2-side live server plus the resolved roles. The
    /// <see cref="Srv.ShipSpawner"/> seam (the exact seam LiveCombatWireTests
    /// uses) places ships deterministically by spawn (drain) order — slot 0 the
    /// <b>shooter</b> at the origin (identity nose → −z), slot 1 the <b>cross-side
    /// enemy</b> dead ahead at <c>(0,0,-gap)</c>, slot 2 the <b>same-side
    /// observer</b> far off the line of fire. Roles are resolved afterwards by
    /// spawn position (immune to socket logon-drain order); sides round-robin
    /// A,B,A so the enemy is cross-side to both peers.
    /// </summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Shooter = null!, Enemy = null!, Observer = null!;
        public FltBot ShooterBot = null!, EnemyBot = null!, ObserverBot = null!;
        public StaticCore Core = null!;

        public void Dispose()
        {
            foreach (var b in new[] { ShooterBot, EnemyBot, ObserverBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatThree(float gap, DataHullTypeIGC hull)
    {
        var seat = new Seated();
        var core = RepresentativeCore();
        seat.Core = core;
        seat.Server = new Srv();

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            var ship = placed switch
            {
                0 => new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity),
                1 => new Ship(id, hull, new Vector3(0f, 0f, -gap), Vector3.Zero, Orientation.Identity),
                _ => new Ship(id, hull, new Vector3(0f, 830f, 930f), Vector3.Zero, Orientation.Identity),
            };
            // A custom ShipSpawner bypasses SpawnDefaultShip's round-robin side
            // assignment (Server.cs:1692-1698), so do it here, identically:
            // ship N seats on side N mod sideCount (so 3 ships round-robin A,B,A —
            // two same-side, one cross-side). `placed` is the count already seated.
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p91_shooter", "p91_enemy", "p91_observer" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Shooter = ship; seat.ShooterBot = bot; }
                else if (MathF.Abs(ship.Position.Z + gap) < 2f) { seat.Enemy = ship; seat.EnemyBot = bot; }
                else { seat.Observer = ship; seat.ObserverBot = bot; }
            }
            Assert.NotNull(seat.Shooter); Assert.NotNull(seat.Enemy); Assert.NotNull(seat.Observer);
            Assert.True(seat.Shooter.SideId != seat.Enemy.SideId, "enemy must be cross-side to the shooter");
            Assert.True(seat.Shooter.SideId == seat.Observer.SideId, "observer must be same-side to the shooter");
            // Shooter/enemy/observer were all constructed with Orientation.Identity
            // (nose along −z) and zero velocity in the spawner; nothing to re-seat.
            seat.Shooter.Velocity = Vector3.Zero;
            seat.Enemy.Velocity = Vector3.Zero;
            seat.Observer.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    // ── live: cross-side impact → computed hull damage + destroy-to-peer ──

    /// <summary>⭐ A real cross-side missile at a real target ship deals the exact
    /// computed hull damage (<c>Power×dtmArmor/MaxHP</c>), disappears from the
    /// sector after impact, and the same-side peer who saw it launch receives a
    /// real <c>FM_S_MISSILE_DESTROYED(83)</c> — the P91 consistency fix proven
    /// live on a hit, not just expiry.</summary>
    [Fact]
    public void Cross_side_missile_impact_deals_computed_hull_damage_and_broadcasts_destroy_to_same_side_peer()
    {
        var (missile, hull, expectedDelta) = CrossHit.Value;
        float gap = MathF.Min(0.3f * missile.InitialSpeed * missile.Lifespan, 150f);
        using var seat = SeatThree(gap, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;

        // The observer must already hold the shooter's EXPORT, else the server
        // withholds the teammate send (P90's _teammateExportSent gate).
        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == shooter.Id), 8000);
        Assert.Contains(seat.ObserverBot.Exports, e => e.ShipId == shooter.Id);

        float fraction0 = enemy.Fraction;
        Assert.Equal(1f, fraction0, 3);
        var mission = shooter.Mission!;
        var live = shooter.FireMissile(missile, mission.LastUpdate);
        short mId = live.Id;

        PumpUntil(seat.Server, () => enemy.Fraction < fraction0 - 0.003f, 6000);
        Assert.True(enemy.Fraction < fraction0, "cross-side missile must actually hit the enemy");

        // (1) exact hull damage: fraction = 1 − expectedDelta (decoded data).
        Assert.Equal(1f - expectedDelta, enemy.Fraction, 3);

        // (3a) the missile disappeared from the sector on impact.
        Assert.DoesNotContain(live, live.OwnerSector.Models);   // removed from its sector on impact

        // (3b) the same-side peer received a real MISSILE_DESTROYED(83) for it.
        PumpUntil(seat.Server, () => seat.ObserverBot.MissileDestroyeds.Any(d => d.MissileId == mId), 4000);
        var d = seat.ObserverBot.MissileDestroyeds.First(x => x.MissileId == mId);
        Assert.Equal(shooter.Sector?.Id, d.ClusterId);
        Console.WriteLine($"P91-CROSS: enemy hull {fraction0:F2}→{enemy.Fraction:F3} (expect {1f - expectedDelta:F3}); " +
                          $"missile {mId} removed on impact, destroy broadcast to same-side peer at {d.Position}");
    }

    // ── live: cross-side missile into a mounted shield ────────────────────────

    /// <summary>⭐ A real cross-side missile into a target with a real mounted
    /// shield absorbs / leaks through with the <b>identical</b> math as a weapon
    /// hit (the reused <see cref="Ship.ReceiveDamage"/> shield path): a fresh full
    /// shield absorbs the computed <c>Power·dtmShield/MaxStrength</c> and the hull
    /// stays untouched; knocking the shield down and firing again leaks the full
    /// raw hit to the hull exactly like the no-shield path — both numbers asserted
    /// from decoded data, mirroring <see cref="CombatShieldTests"/>.</summary>
    [Fact]
    public void Cross_side_missile_against_a_mounted_shield_absorbs_then_leaks_exactly_like_a_weapon()
    {
        var (missile, shieldPart, _, expectedAbsorbed, hull, dtmHull) = ShieldCombo.Value;
        float gap = MathF.Min(0.3f * missile.InitialSpeed * missile.Lifespan, 150f);
        using var seat = SeatThree(gap, hull);
        var shooter = seat.Shooter;
        var enemy = seat.Enemy;

        enemy.MountPart(shieldPart, amount: 1, mountId: 0, fractionMounted: 255);   // CombatShieldTests' shield recipe, armed full
        Assert.Equal(1.0f, enemy.ShieldFraction, 3);
        float hullStart = enemy.Fraction;
        var mission = shooter.Mission!;

        var live = shooter.FireMissile(missile, mission.LastUpdate);
        PumpUntil(seat.Server, () => enemy.ShieldFraction < 0.998f, 6000);

        // (2) shield absorbed the exact computed fraction; hull untouched.
        Assert.Equal(1.0f - expectedAbsorbed, enemy.ShieldFraction, 3);
        Assert.Equal(hullStart, enemy.Fraction, 3);
        Assert.DoesNotContain(live, live.OwnerSector.Models);   // absorbed-impact missile removed from sector

        // Down-shield leak: knock it down (≤ 0.1 → absorbs nothing) and fire again.
        typeof(Ship).GetField("_shieldFraction", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(enemy, 0f);
        float expLeak = missile.Power * dtmHull / hull.HitPoints;
        float before = enemy.Fraction;
        var live2 = shooter.FireMissile(missile, mission.LastUpdate);
        PumpUntil(seat.Server, () => enemy.Fraction < before - 0.005f, 6000);
        Assert.Equal(before - expLeak, enemy.Fraction, 3);
        Assert.DoesNotContain(live2, live2.OwnerSector.Models);
        Console.WriteLine($"P91-SHIELD: absorbed {expectedAbsorbed:F3} (shield 1.0→{enemy.ShieldFraction:F3}, hull unchanged), " +
                          $"then down-shield leak {expLeak:F3} hull ({before:F3}→{enemy.Fraction:F3})");
    }

    // ── live: same-side friendly shot deals no damage and passes through ────

    /// <summary>⭐ A missile fired at a same-side ship deals <b>no</b> damage and
    /// passes through (terminating on nothing), exactly like the existing
    /// projectile friendly-fire rule — asserted with a real same-side shot. The
    /// passed-through missile then expires on its own and its destroy is still
    /// broadcast (P90's expiry path, unmodified).</summary>
    [Fact]
    public void Same_side_missile_passes_through_a_friendly_and_deals_no_damage_then_expires()
    {
        var (missile, hull, _) = CrossHit.Value;
        float gap = MathF.Min(0.3f * missile.InitialSpeed * missile.Lifespan, 150f);
        using var seat = SeatThree(gap, hull);
        var shooter = seat.Shooter;
        var observer = seat.Observer;      // same side as the shooter — the friendly target
        // The shooter spawns nose along −z (identity); we reposition the observer
        // directly ahead at the impact gap below so the missile flies into it.
        var mission = shooter.Mission!;
        // The observer must hold the shooter's EXPORT before we fire, else the
        // same-side destroy is withheld too (P90's gate).
        PumpUntil(seat.Server, () => seat.ObserverBot.Exports.Any(e => e.ShipId == shooter.Id), 8000);
        Assert.Contains(seat.ObserverBot.Exports, e => e.ShipId == shooter.Id);

        // Put the same-side observer directly ahead of the shooter at the usual
        // impact gap, then fire straight into it.
        observer.Position = new Vector3(0f, 0f, -gap);
        float before = observer.Fraction;
        Assert.Equal(1f, before, 3);
        var live = shooter.FireMissile(missile, mission.LastUpdate);
        short mId = live.Id;

        // (4) friendly shot deals NO damage — hull stays full while the missile
        // passes through. The destroy (expiry) is still broadcast: (5).
        PumpUntil(seat.Server, () => seat.ObserverBot.MissileDestroyeds.Any(d => d.MissileId == mId), 12000);
        Assert.Equal(before, observer.Fraction, 3);                       // no hull damage
        Assert.Equal(1f, observer.Fraction, 3);
        Assert.DoesNotContain(live, live.OwnerSector.Models);
        Assert.Contains(seat.ObserverBot.MissileDestroyeds, d => d.MissileId == mId);
        var d = seat.ObserverBot.MissileDestroyeds.First(x => x.MissileId == mId);
        Console.WriteLine($"P91-FRIENDLY: same-side hull stayed {before:F2} (no damage); missile {mId} passed through and " +
                          $"expired at {d.Position}, destroy broadcast to same-side peer");
    }

    // ── domain: a missile and a weapon projectile coexist in one pass ────────

    /// <summary>Scope 4: an assembled mission with TWO real shooters, one firing a
    /// mounted weapon and one firing a missile in the SAME tick window, at
    /// different targets on non-intersecting lines (so neither ordnance can
    /// intercept the other's target). Each target takes its own computed damage,
    /// and the missile's impact raises
    /// <see cref="IIgcSite.MissileDestroyedEvent"/> exactly once — proving the
    /// projectile and missile paths coexist in the single
    /// <see cref="Sector.ResolveCombatCollisions"/> pass without interfering.</summary>
    [Fact]
    public void Missile_and_weapon_projectile_coexist_in_one_collision_pass()
    {
        var (weapon, proj, missile, pHull, mHull, pDelta, mDelta, weaponTravel) = Coexist.Value;
        var core = RepresentativeCore();
        var site = new CountingSite();
        var mission = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team", core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId)], site);
        mission.Stage = MissionStage.Started;
        var sector = mission.Sectors[0];

        // Two non-intersecting firing lines, 400 m apart laterally: shooter P
        // (weapon, identity → −z) at x=−200 engages target P at −gapP; shooter M
        // (missile, identity → −z) at x=+200 engages target M at −gapM. Neither
        // ordnance ever approaches the other's target (400 m separation).
        float gapP = 0.5f * weaponTravel;                          // within weapon reach
        float gapM = MathF.Min(0.3f * missile.InitialSpeed * missile.Lifespan, 300f);   // well within missile reach
        Assert.True(gapM < missile.InitialSpeed * missile.Lifespan, "missile must reach target M");

        // — shooter P: one weapon round at target P.
        var shooterP = new Ship(9801, pHull, new Vector3(-200f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(shooterP, sector);                  // must be placed before MountWeapon
        shooterP.StateM |= ShipControlState.AllWeapons;
        // Cover both ammo-based and energy-based weapons: an ammo weapon needs
        // Ammo, an energy weapon needs Energy == cost (mirrors ArmShooter).
        shooterP.Ammo = 1;
        shooterP.Energy = (float)weapon.EnergyPerShot;
        shooterP.MountWeapon(weapon);

        // — shooter M: one missile at target M (fired in the same tick window).
        var shooterM = new Ship(9804, mHull, new Vector3(+200f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(shooterM, sector);

        var targetP = new Ship(9802, pHull, new Vector3(-200f, 0f, -gapP), Vector3.Zero, Orientation.Identity);
        var targetM = new Ship(9803, mHull, new Vector3(+200f, 0f, -gapM), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(targetP, sector);
        mission.PlaceShip(targetM, sector);

        float pFrac0 = targetP.Fraction, mFrac0 = targetM.Fraction;
        Assert.Equal(1f, pFrac0, 3);
        Assert.Equal(1f, mFrac0, 3);

        var liveMissile = shooterM.FireMissile(missile, mission.LastUpdate);   // same window as the weapon round

        // Tick until both targets have taken their hit (or give up).
        bool both = false;
        double t = mission.LastUpdate;
        for (int i = 0; i < 4000 && !both; i++)
        {
            t += 0.025;
            mission.Update(t);
            both = targetP.Fraction < 1f - MathF.Min(pDelta, 0.082f) * 0.5f
                && targetM.Fraction < 1f - MathF.Min(mDelta, 0.092f) * 0.5f;
        }
        Assert.True(both, $"both ordnance must land: weapon-target frac={targetP.Fraction:F3}, missile-target frac={targetM.Fraction:F3}");

        // Each ordnance dealt its OWN computed damage — no cross-interference.
        Assert.Equal(1f - pDelta, targetP.Fraction, 3);
        Assert.Equal(1f - mDelta, targetM.Fraction, 3);
        Assert.DoesNotContain(liveMissile, liveMissile.OwnerSector.Models);
        Assert.True(site.MissileDestroys == 1, $"missile impact raises MissileDestroyedEvent exactly once, got {site.MissileDestroys}");
        Console.WriteLine($"P91-COEXIST: weapon round on P-target {1f - pDelta:F3}, missile on M-target {1f - mDelta:F3}, " +
                          $"both in one pass; {site.MissileDestroys} destroy event(s)");
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    /// <summary>A site that counts missile-destroy broadcasts (and, for sanity,
    /// damage/kill events) so the coexistence test asserts the impact-path
    /// <see cref="IIgcSite.MissileDestroyedEvent"/> is raised exactly once per
    /// missile. Everything else falls through to the interface default (no-op).</summary>
    private sealed class CountingSite : IIgcSite
    {
        public int MissileDestroys { get; private set; }
        public int KillShips { get; private set; }
        public int DamageShips { get; private set; }
        public void MissileDestroyedEvent(Missile m) => MissileDestroys++;
        public void KillShipEvent(double now, IShipIgc ship, IModelIgc? launcher, float amount, Vector3 p1, Vector3 p2) => KillShips++;
        public void DamageShipEvent(double now, IShipIgc ship, IModelIgc? launcher, byte type, float amount, float leakage, Vector3 p1, Vector3 p2) => DamageShips++;
    }
}
