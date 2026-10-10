using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// Verification of the <b>death + <c>FM_S_KILL_SHIP(105)</c></b> slice (the
/// second half of combat, part 2). When a ship's hull reaches zero it dies,
/// the site's <c>KillShipEvent</c> fires <b>exactly once</b> (the C++
/// <c>oldFraction &gt; 0</c> guard, <c>shipIGC.cpp:1317</c>), and the dead ship
/// stops being updated — it leaves the sector. The wire form of the death
/// notice is <see cref="KillShip"/> (<c>FM_S_KILL_SHIP</c>, id 105); here we
/// pin its exact byte layout and round-trip, and observe the death through a
/// <see cref="RecordingIgcSite"/> (the seam the host bridges into a real
/// broadcast). Built on the same <c>StaticCore</c>-driven fight setup as
/// <see cref="CombatDamageTests"/>.
/// </summary>
public class CombatDeathTests
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

    private static Mission NewStartedMission(IIgcSite? site = null)
    {
        var m = Mission.Assemble(RepresentativeCore.Value, SomeMap.Value, SideSpecs(), site ?? new NullIgcSite());
        m.Stage = MissionStage.Started;
        return m;
    }

    // A hull that can actually fight, paired with the weapon that inflicts the
    // most hull damage per hit (max Power × decoded armor cell). Strongest
    // per-hit ⇒ fewest hits to kill ⇒ the death test finishes in bounded ticks.
    // (The point-blank test in CombatDamageTests deliberately uses the *gentlest*
    // survivor so one hit is never lethal; here we want the opposite.)
    private static readonly Lazy<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC projectile)> Fight =
        new(() =>
        {
            var core = RepresentativeCore.Value;
            var candidates = new List<(DataHullTypeIGC hull, DataPartTypeIGC.Weapon weapon, DataProjectileTypeIGC pt, double perHit)>();
            foreach (var hull in core.HullTypes.Values
                         .Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0 && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed)
                         .OrderBy(h => h.HullId))
            {
                foreach (var p in core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                             .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                             .Where(w => w.Dispersion == 0f && w.EnergyPerShot <= hull.MaxEnergy))
                {
                    if (core.ProjectileTypes.TryGetValue(p.ProjectileTypeId, out var pt) && pt.Power > 0f)
                    {
                        float cell = core.Constants.DamageConstants[pt.DamageType][hull.DefenseType];
                        if (cell > 0f)
                            candidates.Add((hull, p, pt, pt.Power * cell));
                    }
                }
            }
            if (candidates.Count == 0)
                throw new InvalidOperationException("no fightable hull+weapon in the representative core");
            var best = candidates.OrderByDescending(c => c.perHit).First();
            return (best.hull, best.weapon, best.pt);
        });

    private static Ship NewShooter() => new(
        shipId: 9601, hull: Fight.Value.hull,
        position: Vector3.Zero, velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static Ship NewTarget(float z) => new(
        shipId: 9701, hull: Fight.Value.hull,
        position: new Vector3(0f, 0f, z), velocity: Vector3.Zero,
        orientation: Orientation.Identity);

    private static void ArmWeapon(Ship ship)
    {
        var (_, weaponType, _) = Fight.Value;
        ship.StateM |= ShipControlState.AllWeapons;
        ship.MountWeapon(weaponType);
    }

    private static int KillShipEvents(RecordingIgcSite site) =>
        site.Calls.Count(c => c.StartsWith("KillShipEvent"));

    /// <summary>Single-chunk mission tick; <paramref name="feed"/> runs after
    /// each so the weapon never starves (no ship energy recharge yet).</summary>
    private static void Step(Mission m, double[] tRef, Action feed)
    {
        tRef[0] += 0.045;                       // < 0.25 ⇒ n == 1 chunk per Update
        m.Update(tRef[0]);
        feed();
    }

    /// <summary>
    /// ⭐ Enough hits <b>kill</b> the ship and <c>KILL_SHIP</c> is emitted
    /// <b>exactly once</b> (not once per subsequent tick). Two ships nose to
    /// nose; the shooter pours the gentlest weapon's rounds into the target
    /// (re-feeding the ship's energy pool, since recharge is a later task) until
    /// the hull bottoms out. The death is witnessed through a
    /// <see cref="RecordingIgcSite"/> — the seam the host bridges into the wire
    /// <see cref="KillShip"/> broadcast — and asserted to fire exactly once. The
    /// dead ship leaves the sector, so later ticks cannot re-kill it.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Enough_hits_kill_the_ship_and_kill_ship_event_fires_once()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var shooter = NewShooter();
        var (_, _, pt) = Fight.Value;
        float travel = pt.Speed * pt.Lifespan;
        float gap = MathF.Max(25f, 0.4f * travel);
        var target = NewTarget(z: -gap);
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);
        ArmWeapon(shooter);

        float targetMax = target.HitPoints;
        float shooterMax = shooter.HitPoints;          // the shooter must stay full
        var t = new[] { mission.LastUpdate };
        bool killed = false;
        for (int i = 0; i < 20000 && !killed; i++)
        {
            Step(mission, t, () =>
            {
                shooter.Energy = shooter.HullType.MaxEnergy;
                shooter.Ammo = Math.Max(shooter.Ammo, (short)1);
            });
            if (target.Dead) killed = true;
        }
        Assert.True(killed, "enough accumulated hits must kill the ship");
        Assert.True(target.HitPoints < targetMax);

        // Two bots: the shooter poured rounds at the target all through the
        // engagement and its own hull never dropped (friendly-fire off /
        // self-launcher exclusion — the "shooter undamaged" contract).
        Assert.Equal(shooterMax, shooter.HitPoints);

        // Exactly one KILL_SHIP notice — the oldFraction>0 guard fires it only
        // on the transition from living to dead, never again.
        Assert.Equal(1, KillShipEvents(recording));

        // The dead ship leaves the sector.
        Assert.DoesNotContain(target, sector.Models);

        // Keep ticking the shooter: further rounds fly at an already-dead space
        // (the sector no longer contains the target) and must NOT re-emit
        // KILL_SHIP.
        for (int i = 0; i < 60; i++)
            Step(mission, t, () =>
            {
                shooter.Energy = shooter.HullType.MaxEnergy;
                shooter.Ammo = Math.Max(shooter.Ammo, (short)1);
            });
        Assert.Equal(1, KillShipEvents(recording));        // still exactly one
    }

    /// <summary>
    /// The <c>oldFraction > 0</c> guard (<c>shipIGC.cpp:1317</c>) is what
    /// guarantees "exactly once": <c>Killed</c> is returned only on the
    /// transition from living to dead. After the hull is already zero, a
    /// further <see cref="Ship.ReceiveDamage"/> that still inflicts hull
    /// damage returns <c>HullDamage</c> (not <c>Killed</c>) and emits
    /// <b>no</b> second <c>KillShipEvent</c>. This pins the guard directly,
    /// independent of the collision pass or the sector-removal side effect.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void ReceiveDamage_on_a_dead_ship_never_re_emits_kill_ship()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var shooter = NewShooter();
        var target = NewTarget(z: -30f);
        mission.PlaceShip(shooter, sector);
        mission.PlaceShip(target, sector);

        var (_, _, pt) = Fight.Value;
        float monsterHit = target.HullType.HitPoints * 2f;   // well over the hull
        DamageResult first = target.ReceiveDamage(
            pt.DamageType, monsterHit, mission.LastUpdate, Vector3.Zero, Vector3.Zero, shooter);
        Assert.Equal(DamageResult.Killed, first);            // living→dead, so Killed
        Assert.Equal(1, KillShipEvents(recording));          // the death notice, once

        // Subsequent damage to the already-dead hull: the C++ guard makes the
        // return value HullDamage (oldFraction is 0) and no second event fires.
        DamageResult again = target.ReceiveDamage(
            pt.DamageType, monsterHit, mission.LastUpdate, Vector3.Zero, Vector3.Zero, shooter);
        Assert.Equal(DamageResult.HullDamage, again);        // already dead ⇒ not re-Killed
        Assert.Equal(1, KillShipEvents(recording));          // no second message
    }

    // ── the wire message ─────────────────────────────────────────────────────

    /// <summary><c>FM_S_KILL_SHIP(105)</c> round-trips exactly (same shape as
    /// the other ported messages — header + fixed fields, cbmsg = 14).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void KillShip_message_round_trips()
    {
        Require.Artwork();
        var msg = KillShip.FromVictim(victimId: 777, killerType: IgcObjectType.Ship, killerId: 808);
        msg.SideIdKiller = 3;
        msg.BKillCredit = true;

        var d = KillShip.Decode(msg.Encode());
        Assert.Equal(KillShip.Fmid, d.GetFmid());
        Assert.Equal(777, d.ShipId);
        Assert.Equal(0, d.TypeCredit);          // OT_ship
        Assert.Equal(808, d.IdCredit);
        Assert.Equal(3, d.SideIdKiller);
        Assert.True(d.BKillCredit);
        Assert.True(d.BDeathCredit);
    }

    /// <summary><c>FM_S_KILL_SHIP(105)</c> emits the exact 14 declared bytes,
    /// hand-composed from <c>Messages.h:696-703</c> (not from the encoder).</summary>
    [Fact]
    public void KillShip_message_emits_exact_declared_bytes()
    {
        var msg = new KillShip
        {
            ShipId = 999,
            TypeCredit = 0,
            IdCredit = -1,
            SideIdKiller = -1,
            BKillCredit = false,
            BDeathCredit = true,
        };
        var got = msg.Encode();

        void u16(int off, ushort v) { got[off] = (byte)(v & 0xFF); got[off + 1] = (byte)(v >> 8); }
        void i16(int off, short v) { u16(off, unchecked((ushort)v)); }
        Assert.Equal(14, got.Length);                 // sizeof(FMD_S_KILL_SHIP)

        u16(0, 14);                                   // cbmsg
        u16(2, 105);                                  // fmid
        i16(4, 999);                                  // shipID
        i16(6, 0);                                    // typeCredit = OT_ship
        i16(8, -1);                                   // idCredit = NA
        i16(10, -1);                                  // sideidKiller = NA
        got[12] = 0;                                  // bKillCredit
        got[13] = 1;                                  // bDeathCredit
        Assert.Equal(got, msg.Encode());              // construction is its own reference
    }
}
