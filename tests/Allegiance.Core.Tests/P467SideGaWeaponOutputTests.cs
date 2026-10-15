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
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P467 — the combat half of the side-global-attribute (GA) wiring: a side's
/// <see cref="GlobalAttributeSet"/> now scales <b>weapon output</b> through the
/// single cheap <see cref="Ship.GlobalAttributeFactor"/> read (which reuses
/// P446's private side resolution). Every native site is wired:
/// <list type="bullet">
/// <item><c>c_gaDamageGuns</c> (21): gun projectile direct + blast damage
/// (<c>projectileIGC.cpp:132,186</c>), incl. negative-power nanite rounds.</item>
/// <item><c>c_gaDamageMissiles</c> (22): missile direct + blast
/// (<c>missileigc.cpp:474,523</c>).</item>
/// <item><c>c_gaSpeedAmmo</c> (14): ammo-weapon projectile speed +
/// lead solve (<c>weaponIGC.cpp:220</c>, <c>common.cpp:141</c>).</item>
/// <item><c>c_gaLifespanEnergy</c> (15): energy-weapon lifespan
/// (<c>weaponIGC.h:167-170</c>).</item>
/// <item><c>c_gaTurnRateMissile</c> (16): missile turn (<c>missileigc.cpp:389</c>).</item>
/// </list>
/// Each test is data-derived from the real PCore001 static core — nothing
/// hardcoded — and every scaling assertion compares the GA-boosted outcome to
/// the identity (1.0) baseline so the multiplier is isolated.
/// </summary>
public class P467SideGaWeaponOutputTests
{
    private static readonly Lazy<StaticCore> Core = new(() =>
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

    private static readonly Lazy<MissionMap> Map = new(() =>
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

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    /// <summary>Build a started single-side mission whose sole side carries the
    /// given GA factors (everything else identity 1.0 — a synthetic Gas isolating
    /// the multiplier under test, same recipe as P446's <c>NewMission</c>).</summary>
    private static Mission NewMission(params (int idx, float val)[] factors)
    {
        var civ = Core.Value.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(Core.Value, Map.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        var arr = new float[GlobalAttributeSet.Count];
        Array.Fill(arr, 1f);
        foreach (var (idx, val) in factors) arr[idx] = val;
        m.Sides[0].GlobalAttributes = new GlobalAttributeSet(arr);
        return m;
    }

    /// <summary>A hull that can fight (energy+ammo+fixed hardpoint) so weapon
    /// selection and firing work (CombatWeaponFiringTests' Fight recipe).</summary>
    private static readonly Lazy<DataHullTypeIGC> FightHull = new(() =>
        Core.Value.HullTypes.Values.First(h => h.MaxEnergy > 0 && h.MaxAmmo > 0
                                               && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed));

    /// <summary>A ship on the mission's sole side (so GA resolves) at
    /// <paramref name="pos"/>, with a boosted energy pool so any affordable
    /// weapon can fire a long burst.</summary>
    private static Ship NewGaShip(Mission m, DataHullTypeIGC hull, Vector3 pos)
        => new(shipId: 8010, hull: hull with { MaxEnergy = 20000f }, position: pos,
               velocity: Vector3.Zero, orientation: Orientation.Identity)
        { Mission = m, SideId = m.Sides[0].Id };

    /// <summary>A sideless target (no friendly-fire gate; fights anyone).</summary>
    private static Ship NewTarget(Mission m, DataHullTypeIGC hull, Vector3 pos)
        => new(shipId: 8020, hull: hull, position: pos, velocity: Vector3.Zero,
               orientation: Orientation.Identity);

    private static float AngleBetween(Vector3 a, Vector3 b)
    {
        float dot = Math.Clamp(a * b, -1f, 1f);   // operator* is the dot product
        return MathF.Acos(dot);
    }

    // ── gun damage: a single-round (weapon, hull) pair ────────────────────

    private static readonly Lazy<(DataPartTypeIGC.Weapon Weapon, DataProjectileTypeIGC Proj, DataHullTypeIGC Hull, float Delta)> GunHit = new(() =>
    {
        var core = Core.Value;
        foreach (var hull in core.HullTypes.Values
            .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
            .OrderBy(h => h.HitPoints))
        {
            foreach (var wp in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                // CAmmoPerShot>0 → a single round is a hard cap (ammo doesn't
                // recharge in flight; auto-reload needs a cargo ammo pack the
                // test ship never has). An energy weapon would recharge (P461)
                // and burst multiple rounds, muddying the single-hit assertion.
                .Where(w => w.CAmmoPerShot > 0 && w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy))
            {
                var pt = core.ProjectileTypes[wp.ProjectileTypeId];
                if (pt.Power <= 0f) continue;
                if (pt.BlastPower != 0f) continue;   // a pure direct-hit round: no blast-on-impact to skew the single-hit measurement
                float dtm = Dtm(core, pt.DamageType, hull.DefenseType);
                if (dtm <= 0f) continue;
                float delta = pt.Power * dtm / hull.HitPoints;
                // survivable single-round delta in a clean band, with enough
                // straight-line reach to land a real aimed hit.
                if (delta >= 0.095f && delta <= 0.545f && pt.Speed * pt.Lifespan >= 100f)
                    return (wp, pt, hull, delta);
            }
        }
        throw new InvalidOperationException("no gun hit combo in representative core");
    });

    private static float FireGunHit(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, float gaMul, out Ship target)
    {
        var m = NewMission((GlobalAttributeSet.DamageGuns, gaMul));
        var sector = m.Sectors[0];
        var shooter = NewGaShip(m, hull, Vector3.Zero);
        m.PlaceShip(shooter, sector);
        shooter.StateM |= ShipControlState.AllWeapons;
        // Cap the burst to EXACTLY one round (the established single-shot recipe):
        // ammo weapons get one round, energy weapons exactly enough energy for one.
        if (weapon.CAmmoPerShot > 0)
            shooter.Ammo = 1;
        else
            shooter.Energy = weapon.EnergyPerShot;
        shooter.MountWeapon(weapon);
        var proj = Core.Value.ProjectileTypes[weapon.ProjectileTypeId];
        float gap = MathF.Min(0.35f * proj.Speed * proj.Lifespan, 240f);
        target = NewTarget(m, hull, new Vector3(0f, 0f, -gap));
        m.PlaceShip(target, sector);
        float f0 = target.Fraction;
        double t = m.LastUpdate;
        bool hit = false;
        for (int i = 0; i < 6000 && !hit; i++)
        {
            t += 0.003;
            m.Update(t);
            if (target.Fraction < f0 - 1e-4f) hit = true;
        }
        Assert.True(hit, "projectile must hit the shieldless target");
        return target.Fraction;
    }

    /// <summary>⭐ A side whose GA has <c>DamageGuns = 1.25</c>: a projectile hit
    /// deals 1.25× the decoded <c>Power</c> to a shieldless target (vs the
    /// identity 1.0 baseline dealing exactly 1×). Mirrors native
    /// <c>projectileIGC.cpp:186</c>. Pre-fix (raw <c>ord.Power</c>) the boosted
    /// and identity hits deal the SAME damage, so the ratio assertion fails —
    /// recorded in the P467 report.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GunDamage_GA_scales_a_shieldless_direct_hit()
    {
        Require.Artwork();
        var (weapon, proj, hull, delta) = GunHit.Value;
        float idFrac = FireGunHit(hull, weapon, gaMul: 1f, out _);
        float boostFrac = FireGunHit(hull, weapon, gaMul: 1.25f, out _);
        float idDrop = 1f - idFrac, boostDrop = 1f - boostFrac;
        Assert.Equal(1f - delta, idFrac, 3);
        Assert.Equal(1f - 1.25f * delta, boostFrac, 3);
        Assert.InRange(boostDrop / idDrop, 1.15f, 1.35f);
        Console.WriteLine($"P467-GUN: identity frac={idFrac:F4} (drop {idDrop:F4}); GA1.25 frac={boostFrac:F4} (drop {boostDrop:F4}); ratio {boostDrop / idDrop:F3} (expect 1.25)");
    }

    // ── missile damage ────────────────────────────────────────────────────

    private static readonly Lazy<(DataMissileTypeIGC Missile, DataHullTypeIGC Hull, float Delta)> MissileHit = new(() =>
    {
        var core = Core.Value;
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
                if (delta >= 0.088f && delta <= 0.630f)
                    return (m, hull, delta);
            }
        }
        throw new InvalidOperationException("no missile hit combo in representative core");
    });

    private static float FireMissileHit(DataHullTypeIGC hull, DataMissileTypeIGC missile, float gaMul)
    {
        var m = NewMission((GlobalAttributeSet.DamageMissiles, gaMul));
        var sector = m.Sectors[0];
        var shooter = NewGaShip(m, hull, Vector3.Zero);
        m.PlaceShip(shooter, sector);
        float gap = MathF.Min(0.35f * missile.InitialSpeed * missile.Lifespan, 160f);
        var target = NewTarget(m, hull, new Vector3(0f, 0f, -gap));
        m.PlaceShip(target, sector);
        float f0 = target.Fraction;
        var live = shooter.FireMissile(missile, m.LastUpdate);
        double t = m.LastUpdate;
        bool hit = false;
        for (int i = 0; i < 6000 && !hit; i++)
        {
            t += 0.004;
            m.Update(t);
            if (target.Fraction < f0 - 1e-4f) hit = true;
        }
        Assert.True(hit, "missile must hit the shieldless target");
        Assert.DoesNotContain(live, live.OwnerSector.Models);   // spent on impact
        return target.Fraction;
    }

    /// <summary>⭐ The same pattern for <c>DamageMissiles = 1.25</c>: a cross-side
    /// missile hit deals 1.25× <c>Power</c> to a shieldless target (native
    /// <c>missileigc.cpp:474</c>).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void MissileDamage_GA_scales_a_shieldless_direct_hit()
    {
        Require.Artwork();
        var (missile, hull, delta) = MissileHit.Value;
        float idFrac = FireMissileHit(hull, missile, gaMul: 1f);
        float boostFrac = FireMissileHit(hull, missile, gaMul: 1.25f);
        float idDrop = 1f - idFrac, boostDrop = 1f - boostFrac;
        Assert.Equal(1f - delta, idFrac, 3);
        Assert.Equal(1f - 1.25f * delta, boostFrac, 3);
        Assert.InRange(boostDrop / idDrop, 1.15f, 1.35f);
        Console.WriteLine($"P467-MSIL: identity frac={idFrac:F4} (drop {idDrop:F4}); GA1.25 frac={boostFrac:F4} (drop {boostDrop:F4}); ratio {boostDrop / idDrop:F3} (expect 1.25)");
    }

    // ── ammo-weapon speed vs energy-weapon speed ──────────────────────────

    /// <summary>The first dispersion-0 <b>ammo</b> weapon (cAmmoPerShot&gt;0) and
    /// the first dispersion-0 pure-<b>energy</b> weapon (cAmmoPerShot==0,
    /// energyPerShot&gt;0), each with its real projectile.</summary>
    private static readonly Lazy<(DataPartTypeIGC.Weapon W, DataProjectileTypeIGC P)> AmmoWeapon =
        new(() => PickWeapon(w => w.CAmmoPerShot > 0));
    private static readonly Lazy<(DataPartTypeIGC.Weapon W, DataProjectileTypeIGC P)> EnergyWeapon =
        new(() => PickWeapon(w => w.CAmmoPerShot == 0 && w.EnergyPerShot > 0f));
    private static readonly Lazy<(DataPartTypeIGC.Weapon W, DataProjectileTypeIGC P)> PureAmmoWeapon =
        new(() => PickWeapon(w => w.EnergyPerShot == 0f && w.CAmmoPerShot > 0));

    private static (DataPartTypeIGC.Weapon, DataProjectileTypeIGC) PickWeapon(Func<DataPartTypeIGC.Weapon, bool> pred)
    {
        var core = Core.Value;
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type))
        {
            if (p.Dispersion == 0f && pred(p)
                && core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt)
                && pt.Speed > 0f && pt.Lifespan > 0f)
                return (p, pt);
        }
        throw new InvalidOperationException("no weapon satisfying predicate in representative core");
    }

    /// <summary>Mount <paramref name="weapon"/> on a fresh GA ship (side carries
    /// <c>SpeedAmmo = 2</c>), fire, and return the live projectile's velocity
    /// magnitude.</summary>
    private static float FireOnceReadSpeed(DataPartTypeIGC.Weapon weapon)
    {
        var m = NewMission((GlobalAttributeSet.SpeedAmmo, 2f));
        var sector = m.Sectors[0];
        var shooter = NewGaShip(m, FightHull.Value, Vector3.Zero);
        m.PlaceShip(shooter, sector);
        shooter.StateM |= ShipControlState.AllWeapons;
        shooter.Ammo = 9960;
        shooter.Energy = 20000f;
        shooter.MountWeapon(weapon);
        double t = m.LastUpdate;
        for (int i = 0; i < 6000; i++)
        {
            t += 0.016;
            m.Update(t);
            if (sector.Models.OfType<Projectile>().Any()) break;
        }
        var proj = sector.Models.OfType<Projectile>().First();
        return proj.Velocity.Length();
    }

    /// <summary>⭐ An ammo weapon's projectile velocity scales with
    /// <c>SpeedAmmo</c> (<c>weaponIGC.cpp:220</c>, condition <c>cAmmoPerShot</c>),
    /// and an energy weapon's does not.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void SpeedAmmo_GA_scales_ammo_weapon_speed_but_not_energy_weapon()
    {
        Require.Artwork();
        var (ammoW, ammoP) = AmmoWeapon.Value;
        var (energyW, energyP) = EnergyWeapon.Value;
        Assert.NotEqual(ammoW.ProjectileTypeId, energyW.ProjectileTypeId);

        float ammoSpeed = FireOnceReadSpeed(ammoW);
        float energySpeed = FireOnceReadSpeed(energyW);

        // Ammo round flies at 2× decoded speed; energy round at raw decoded speed.
        Assert.InRange(ammoSpeed, 2f * ammoP.Speed - 1f, 2f * ammoP.Speed + 1f);
        Assert.InRange(energySpeed, energyP.Speed - 1f, energyP.Speed + 1f);
        Console.WriteLine($"P467-SPEED: ammo round {ammoSpeed:F1} (expect {2f * ammoP.Speed:F1}); energy round {energySpeed:F1} (expect {energyP.Speed:F1})");
    }

    // ── energy-weapon lifespan ────────────────────────────────────────────

    /// <summary>⭐ An energy weapon's lifespan scales with <c>LifespanEnergy</c>
    /// under native's exact condition (<c>weaponIGC.h:167-170</c>: energyPerShot
    /// ≠ 0 — i.e. an energy weapon), while a pure-ammo weapon (energyPerShot == 0)
    /// does NOT scale.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void LifespanEnergy_GA_scales_energy_weapon_lifespan_but_not_ammo_weapon()
    {
        Require.Artwork();
        var (energyW, energyP) = EnergyWeapon.Value;
        var (pureAmmoW, pureAmmoP) = PureAmmoWeapon.Value;
        var m = NewMission((GlobalAttributeSet.LifespanEnergy, 1.5f));
        var sector = m.Sectors[0];
        var shooter = NewGaShip(m, FightHull.Value, Vector3.Zero);
        m.PlaceShip(shooter, sector);

        var energyWeapon = shooter.MountWeapon(energyW);
        var ammoWeapon = shooter.MountWeapon(pureAmmoW);

        Assert.True(energyW.EnergyPerShot != 0f, "energy weapon must cost energy to exercise the native condition");
        Assert.True(pureAmmoW.EnergyPerShot == 0f, "pure-ammo weapon must cost no energy to prove non-scaling");
        Assert.Equal(energyP.Lifespan * 1.5f, energyWeapon.GetLifespan(), 3);
        Assert.Equal(pureAmmoP.Lifespan, ammoWeapon.GetLifespan(), 3);
        Console.WriteLine($"P467-LIFE: energy lifespan {energyWeapon.GetLifespan():F3} (expect {energyP.Lifespan * 1.5f:F3}); ammo lifespan {ammoWeapon.GetLifespan():F3} (raw {pureAmmoP.Lifespan:F3})");
    }

    // ── missile turn rate ─────────────────────────────────────────────────

    private static readonly Lazy<DataMissileTypeIGC> TurningMissile = new(() =>
    {
        var core = Core.Value;
        var t = core.MissileTypes.Values
            .Where(mm => mm.InitialSpeed > 0f && mm.Acceleration > 0f && mm.TurnRate > 0f
                         && mm.TurnRate >= 1.3f && mm.TurnRate <= 3f
                         && mm.Lifespan >= 4f && mm.Lifespan < 16f)
            .OrderBy(mm => mm.Acceleration).ThenBy(mm => mm.TurnRate).FirstOrDefault();
        if (t.ExpendableTypeId == 0)
            throw new InvalidOperationException("no turning missile in representative core");
        return t with { ReadyTime = 0f };   // P549: isolate homing/turn from the arming delay (missileigc.cpp:363)
    });

    /// <summary>Launch a homing missile from a launcher on the given mission's
    /// side 0 at a far off-axis target and return the heading rotation over the
    /// FIRST tick. With the target ~68° off-axis and the turn-rate limiter
    /// (<c>missileigc.cpp:389</c>) active, the first-tick rotation is exactly
    /// <c>TurnRate·dt·gaFactor</c>, so the GA ratio is exact.</summary>
    private static float FirstTickTurn(DataMissileTypeIGC mType, Mission m)
    {
        var sector = m.Sectors[0];
        var launcher = new Ship(8111, FightHull.Value, Vector3.Zero, Vector3.Zero, Orientation.Identity)
        { Mission = m, SideId = m.Sides[0].Id };
        m.PlaceShip(launcher, sector);

        Vector3 forward = new(1f, 0f, 0f);
        Vector3 p0 = Vector3.Zero;
        Vector3 vel0 = forward * mType.InitialSpeed;
        Vector3 targetPos = new(80f, 200f, 0f);        // ~68° off-axis — deep in the ramp
        var target = new Ship(8112, FightHull.Value, targetPos, Vector3.Zero, Orientation.Identity);
        m.PlaceShip(target, sector);

        var missile = new Missile(8113, sector, mType, p0, vel0, forward, m.LastUpdate,
                                  launcher: launcher, target: target);
        sector.Add(missile);
        m.Update(m.LastUpdate + 0.022);
        return AngleBetween(forward, missile.Forward);
    }

    /// <summary>⭐ A missile's turn rate scales with <c>TurnRateMissile</c>
    /// (<c>missileigc.cpp:389</c>): with the factor 2.0 the missile re-orients
    /// twice as far on the first tick as the identity missile.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void TurnRateMissile_GA_scales_homing_turn_rate()
    {
        Require.Artwork();
        var mType = TurningMissile.Value;
        float rotId = FirstTickTurn(mType, NewMission());
        float rotGa = FirstTickTurn(mType, NewMission((GlobalAttributeSet.TurnRateMissile, 2f)));
        Assert.True(rotId > 0.002f, $"identity missile must visibly turn, got {rotId:F4} rad");
        Assert.InRange(rotGa / rotId, 1.8f, 2.2f);
        Console.WriteLine($"P467-TURN: first-tick turn GA2={rotGa:F4} rad, identity={rotId:F4} rad, ratio {rotGa / rotId:F3} (expect ~2)");
    }

    // ── GA plumbing: identity / sideless reproduce raw exactly ────────────

    /// <summary>⭐ Side-attribute indices 14,15,16,21,22 are the documented native
    /// constants, and a sideless ship / identity side read identity (1.0) — so the
    /// whole output half is a no-op for the default (no-side) case.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void GlobalAttributeFactor_identity_and_indices_match_native()
    {
        Require.Artwork();
        Assert.Equal(14, GlobalAttributeSet.SpeedAmmo);
        Assert.Equal(15, GlobalAttributeSet.LifespanEnergy);
        Assert.Equal(16, GlobalAttributeSet.TurnRateMissile);
        Assert.Equal(21, GlobalAttributeSet.DamageGuns);
        Assert.Equal(22, GlobalAttributeSet.DamageMissiles);

        // A sideless ship reads identity for every output attribute.
        var sideless = new Ship(8200, FightHull.Value, Vector3.Zero, Vector3.Zero, Orientation.Identity);
        Assert.Equal(1f, sideless.GlobalAttributeFactor(GlobalAttributeSet.DamageGuns), 6);
        Assert.Equal(1f, sideless.GlobalAttributeFactor(GlobalAttributeSet.DamageMissiles), 6);
        Assert.Equal(1f, sideless.GlobalAttributeFactor(GlobalAttributeSet.SpeedAmmo), 6);
        Assert.Equal(1f, sideless.GlobalAttributeFactor(GlobalAttributeSet.LifespanEnergy), 6);
        Assert.Equal(1f, sideless.GlobalAttributeFactor(GlobalAttributeSet.TurnRateMissile), 6);
    }
}

/// <summary>
/// P467 — end-to-end, LIVE proof that completing a REAL development whose GA
/// raises gun damage changes actual combat: completing dev 143
/// ('GA: PW&amp;EW Damage 1', gas[21]=1.1 — a real PCore001 development) on the
/// shooter's side scales a real projectile hit's hull damage by ~1.1×.
/// </summary>
[Collection("LiveNetwork")]
public sealed class P467E2eGunDamageDevLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore RepresentativeCore()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int ups = 8) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = ups,
        Games =
        [
            new Gm { Name = "P467 GA Gun E2E", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 888u, DwCookie = 0x888u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 8820000000000000000ul, DrmHash = "p467ga",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Place seq0 (shooter) on side 0 at the origin (identity nose −z)
    /// and seq1 (enemy) on side 1 dead ahead; a far observer, if a third seats,
    /// parks aside.</summary>
    private static void InjectSpawn(Srv server, float gap)
    {
        int seq = 0;
        server.ShipSpawner = (core, mission, shipId) =>
        {
            short sideIdx = (short)(seq < 1 ? 0 : 1);
            // H-08: the weapon shooter's one round needs hull ammo capacity.
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First() with { MaxAmmo = 1 };
            var pos = seq == 0 ? new Vector3(0f, 0f, 0f)
                     : seq == 1 ? new Vector3(0f, 0f, -gap)
                     : new Vector3(400f, 940f, 880f);
            var ship = new Ship(shipId, hull, pos, Vector3.Zero, Orientation.Identity);
            ship.SideId = mission.Sides[sideIdx].Id;
            mission.PlaceShip(ship, mission.Sectors[0]);
            seq++;
            return (ship, mission.Sectors[0]);
        };
    }

    private static void SeatOne(Srv server, FltBot bot, string name)
    {
        bot.Connect(server.BoundPort, name);
        bot.SendLogonRequest(Request(name));
        int n = PumpUntil(server, () => bot.ShipAssignment is not null, 4000);
        Assert.True(n < 4000, $"{name} did not seat");
    }

    /// <summary>⭐ Complete real dev 143 ('GA: PW&amp;EW Damage 1', gas[21]=1.1) on
    /// the shooter's side through the real funding/completion pipeline, then show
    /// a real projectile hit's hull damage rises by ~1.1×.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Completing_dev_143_raises_gun_damage_in_combat()
    {
        Require.Artwork();
        var core = RepresentativeCore();
        var (weapon, proj) = E2eWeapon(core);

        using var server = new Srv();
        // gap sized so the round lands promptly (within a few sim ticks).
        float gap = MathF.Min(0.30f * proj.Speed * proj.Lifespan, 220f);
        InjectSpawn(server, gap);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        using var a = new FltBot();
        using var b = new FltBot();
        SeatOne(server, a, "ga_shooter");
        SeatOne(server, b, "ga_enemy");

        var shooter = server.Ships.Single(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Ship;
        var enemy = server.Ships.Single(s => s.Ship.Id == b.ShipAssignment!.Ship.ShipId).Ship;
        Assert.NotEqual(shooter.SideId, enemy.SideId);
        Assert.True(enemy.Position.Z < 0f, "enemy must sit dead ahead of the shooter");
        var mission = shooter.Mission!;
        var side = mission.Sides.Single(s => s.Id == shooter.SideId!.Value);

        // Arm the shooter with the chosen gun and one round per shot.
        shooter.StateM |= ShipControlState.AllWeapons;
        shooter.MountWeapon(weapon);

        // ── baseline: one real round's hull damage (identity GA) ──
        shooter.Ammo = 1;
        shooter.Energy = (float)weapon.EnergyPerShot;
        float drop1 = SingleRoundDrop(server, shooter, enemy);
        Assert.True(drop1 > 0.020f && drop1 < 0.850f, $"baseline single-round drop {drop1:F3} must be survivable and measurable");

        // ── complete dev 143 (gas[21] = 1.1) on the shooter's side ──
        var dev = mission.Core.Developments[143];
        Assert.Equal("GA: PW&EW Damage 1", dev.Name);
        Assert.InRange(dev.Gas[21], 1.099f, 1.101f);   // the +10% gun damage
        var bucket = side.Buckets.Single(x => x.ItemType == BucketItemType.BitDevelopment
                                              && x.Development is { } dd && dd.DevelopmentId == dev.DevelopmentId);
        side.DevelopmentTechs = side.DevelopmentTechs.Or(TechTreeBitMask.FromBytes(dev.TtbmRequired));   // P551: unlock dev 143 so the side can buy/fund it (CanBuy gate)
        server.Ships.First(s => s.Ship.Id == a.ShipAssignment!.Ship.ShipId).Wallet.Money = 10_000_000;
        a.ResetDevCompleteds(); a.ResetSideTechChanges(); a.ResetSideAttributeChanges();
        a.SendDonate(bucket.Id, bucket.Price);
        // TimeToBuild = 120 s (bucket accumulates 120000 ms of invest time; at
        // ups=8 each tick adds 125 ms ⇒ ~960 ticks). 5000 is ample.
        int done = PumpUntil(server, () => server.BucketCompletions.Contains(bucket.Id), 5000);
        Assert.True(done < 5000, $"dev 143 did not complete (invested {bucket.InvestedMoney}/{bucket.Price}, ms {bucket.InvestedMilliseconds}/{bucket.TimeToBuild * 1000})");
        float gas21After = side.GlobalAttributes.Get(GlobalAttributeSet.DamageGuns);
        Assert.InRange(gas21After, 1.098f * 1f, 1.102f);   // effectively 1.1 × identity base

        // ── post-dev: one real round again → hull damage scales ~1.1× ──
        shooter.Ammo = 1;
        shooter.Energy = (float)weapon.EnergyPerShot;
        float drop2 = SingleRoundDrop(server, shooter, enemy);
        Assert.InRange(drop2 / drop1, 1.06f, 1.18f);
        Console.WriteLine($"P467-E2E: dev 143 '{dev.Name}' (gas[21] {dev.Gas[21]:F3}) complete on side {side.Id}; single-round hull drop {drop1:F4} -> {drop2:F4}, ratio {drop2 / drop1:F3} (expect ~1.1)");
    }

    /// <summary>A dispersion-0 gun whose single round against the spawner's first
    /// hull lands a survivable, measurable hull delta in [0.08, 0.55].</summary>
    private static (DataPartTypeIGC.Weapon Weapon, DataProjectileTypeIGC Proj) E2eWeapon(StaticCore core)
    {
        // H-08: the weapon shooter's one round needs hull ammo capacity.
            var hull = core.HullTypes.Values.OrderBy(h => h.HullId).First() with { MaxAmmo = 1 };
        foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
            .Select(p => (DataPartTypeIGC.Weapon)p.Type))
        {
            // CAmmoPerShot>0 for a reliable single-round measurement (an energy
            // weapon would recharge in flight and burst multiple rounds).
            if (p.Dispersion != 0f || p.CAmmoPerShot <= 0 || p.EnergyPerShot > hull.MaxEnergy) continue;
            var pt = core.ProjectileTypes[p.ProjectileTypeId];
            if (pt.Power <= 0f) continue;
            float dtm = core.Constants.DamageConstants[pt.DamageType][hull.DefenseType];
            if (dtm <= 0f) continue;
            float delta = pt.Power * dtm / hull.HitPoints;
            if (delta >= 0.080f && delta <= 0.560f && pt.Speed * pt.Lifespan >= 80f)
                return (p, pt);
        }
        throw new InvalidOperationException("no e2e gun+first-hull combo in representative core");
    }

    /// <summary>Pump the live server until the enemy's hull drops by exactly one
    /// round's worth, and return that drop (rounded up to the moment of first
    /// contact). The weapon is single-round armed, so no subsequent round skews
    /// the measurement.</summary>
    private static float SingleRoundDrop(Srv server, Ship shooter, Ship enemy)
    {
        float before = enemy.Fraction;
        int n = PumpUntil(server, () => enemy.Fraction < before - 0.009f, 4000);
        Assert.True(n < 4000, "enemy must take the single round's damage");
        // Allow a couple extra ticks so the exactly-one in-flight round fully lands.
        for (int i = 0; i < 4; i++) server.TickSim();
        return before - enemy.Fraction;
    }
}
