using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Tests;

/// <summary>
/// P226 — domain-level proof of the missile-armament leg wired into
/// <see cref="Ship.OnUpdate"/> and the shared <see cref="Ship.TryFireMissile"/>
/// gate. Pure-domain (no sockets), mirroring <see cref="MissileHomingTests"/>'s
/// assembled-mission structure: build a started mission, mount a real missile
/// launcher (<see cref="DataPartTypeIGC.MissileLauncher"/> on
/// <c>EquipmentType.Magazine</c>), place a hostile in-range target, and assert
/// the fire gate behaves faithfully:
///   • lock insufficient (a <c>MaxLock &gt; 0</c> type with no elapsed lock
///     build time) ⇒ <see cref="Ship.TryFireMissile"/> refuses — no missile, no
///     ammo spent;
///   • lock sufficient (built by the per-tick <see cref="Ship.UpdateMountedMissileLock"/>
///     leg, or a <c>MaxLock == 0</c> type that needs none — UpdateMissileLock's own
///     gate 5) ⇒ a real <see cref="Missile"/> is created homing at
///     <see cref="Ship.CommandTarget"/>, the launcher's ammo decrements, and the
///     lock is consumed (reset to 0);
///   • the <see cref="OnUpdate"/> held-bit path (StateM &amp; MissileFire) actually
///     fires — the magazineigc.cpp:111 auto-fire lever.
/// The wire half (FM_CS_FIRE_MISSILE(70)) is proved in
/// <see cref="MissileFireLiveTests"/> (ClientCore.Tests).
/// </summary>
public class MissileFireTests
{
    private static readonly Lazy<(StaticCore Core, DataMissileTypeIGC NoLock, DataMissileTypeIGC Lock)> Rep = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            var core = StaticCore.Load(file);
            var noLock = core.MissileTypes.Values
                .Where(m => m.MaxLock == 0f && m.InitialSpeed > 0f && m.Lifespan >= 1f && m.Lifespan < 6f)
                .OrderBy(m => m.ExpendableTypeId).FirstOrDefault();
            var lockTyp = core.MissileTypes.Values
                .Where(m => m.MaxLock > 0f && m.LockTime > 0f && m.InitialSpeed > 0f && m.Lifespan >= 2f && m.Lifespan < 12f)
                .OrderBy(m => m.LockTime).FirstOrDefault();
            if (noLock.ExpendableTypeId == 0 || lockTyp.ExpendableTypeId == 0) continue;
            return (core, noLock, lockTyp);
        }
        throw new InvalidOperationException("no corpus core with both a MaxLock==0 and a MaxLock>0 missile type");
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

    private static Mission NewStartedMission(out Sector sector)
    {
        var (core, _, _) = Rep.Value;
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First();
        var m = Mission.Assemble(core, SomeMap.Value, [new SideSpec(0, "Team 1", civ.CivilizationId)], new NullIgcSite());
        m.Stage = MissionStage.Started;
        sector = m.Sectors[0];
        return m;
    }

    /// <summary>Mount a missile launcher at mount 0 naming <paramref name="mt"/>
    /// with <paramref name="amount"/> rounds (construction-only subtype — the
    /// <see cref="DataPartTypeIGC.MissileLauncher"/> has no corpus part-record,
    /// exactly like the chaff/mine launchers).</summary>
    private static void MountLauncher(Ship ship, DataMissileTypeIGC mt, short amount)
    {
        var part = new DataPartTypeIGC(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ml", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: 701, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MissileLauncher(mt.ExpendableTypeId));
        ship.MountPart(part, amount, mountId: 0, fractionMounted: 255);
    }

    /// <summary>Place a hostile target directly ahead of the launcher ship so
    /// the lock builds at its fastest (angle-off-bow 0), in-range and same-sector.</summary>
    private static (Ship Shooter, Ship Target) PlaceDuo(Mission mission, Sector sector, DataMissileTypeIGC mt)
    {
        var (core, _, _) = Rep.Value;
        var hull = core.HullTypes.Values.First();
        var shooter = new Ship(901, hull, new Vector3(0f, 0f, 0f), Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(shooter, sector);
        shooter.SideId = 0;

        Vector3 tpos = new(300f, 0f, 0f);
        var target = new Ship(902, hull, tpos, Vector3.Zero, Orientation.Identity);
        mission.PlaceShip(target, sector);
        target.SideId = 1;

        shooter.Orientation.Set((tpos - shooter.Position).Normalize());   // face the target (angle-off-bow 0)
        shooter.CommandTarget = target;
        return (shooter, target);
    }

    private static void Step(Mission mission, int ticks, double dt)
    {
        double t = mission.LastUpdate;
        for (int i = 0; i < ticks; i++) { t += dt; mission.Update(t); }
    }

    /// <summary>⭐ Negative case — the lock gate. A <c>MaxLock &gt; 0</c> missile
    /// type with no elapsed lock build time (MissileLock is still 0) must be
    /// refused by <see cref="Ship.TryFireMissile"/>: no missile is created and the
    /// launcher's ammo is untouched. This is the "fire requested but lock not yet
    /// sufficient" refusal (the caller-facing half of UpdateMissileLock's gate 5 —
    /// a MaxLock==0 type needs no lock, a MaxLock&gt;0 type needs a full build).</summary>
    [Fact]
    public void TryFireMissile_refuses_when_lock_not_yet_sufficient()
    {
        var (_, _, lockTyp) = Rep.Value;
        var mission = NewStartedMission(out var sector);
        var (shooter, _) = PlaceDuo(mission, sector, lockTyp);
        MountLauncher(shooter, lockTyp, amount: 2);

        // No ticks have elapsed → MissileLock is still 0 for a MaxLock>0 type.
        Assert.Equal(0f, shooter.MissileLock);

        bool fired = shooter.TryFireMissile(mission.LastUpdate);

        Assert.False(fired, "a MaxLock>0 missile with no elapsed lock build must be refused");
        Assert.Empty(sector.Models.OfType<Missile>());
        Assert.Equal(2, shooter.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0).Amount);
    }

    /// <summary>⭐ Positive case (lock built by the per-tick OnUpdate leg). Tick the
    /// mission so <see cref="Ship.UpdateMountedMissileLock"/> builds MissileLock to
    /// full against the in-range hostile target, then <see cref="Ship.TryFireMissile"/>
    /// fires: a real <see cref="Missile"/> is created homing at CommandTarget, the
    /// ammo decrements 2 → 1, and the lock is consumed (reset to 0 — the natural
    /// fire-rate limiter).</summary>
    [Fact]
    public void TryFireMissile_fires_when_lock_has_built_and_homes_at_target()
    {
        var (_, _, lockTyp) = Rep.Value;
        var mission = NewStartedMission(out var sector);
        var (shooter, target) = PlaceDuo(mission, sector, lockTyp);
        MountLauncher(shooter, lockTyp, amount: 2);

        // Build the lock via the real per-tick leg (dead-ahead target, fast build).
        int guard = 0;
        while (shooter.MissileLock < 1f && guard++ < 2000) Step(mission, 1, 0.05);
        Assert.True(shooter.MissileLock >= 1f, $"lock must build to full within 100s (got {shooter.MissileLock:F3})");

        bool fired = shooter.TryFireMissile(mission.LastUpdate);
        Assert.True(fired, "a fully-locked MaxLock>0 missile must fire");
        var missile = Assert.Single(sector.Models.OfType<Missile>());
        Assert.Same(target, missile.Target);            // homing at the command target
        Assert.Equal(1, shooter.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0).Amount);
        Assert.Equal(0f, shooter.MissileLock);          // lock consumed by the shot (FedSrv.CPP:5507)
    }

    /// <summary>⭐ The OnUpdate held-bit path (missileFireIGC auto-fire). With a
    /// <c>MaxLock == 0</c> type (needs no lock — gate 5 snaps it to full instantly)
    /// and the fire bit set in StateM, a single mission tick fires a real missile
    /// homing at CommandTarget and spends one round. This is the magazineigc.cpp:111
    /// lever wired into OnUpdate; it must-fail if the held-bit check is removed.</summary>
    [Fact]
    public void OnUpdate_held_missile_fire_bit_fires_on_next_tick()
    {
        var (_, noLock, _) = Rep.Value;
        var mission = NewStartedMission(out var sector);
        var (shooter, target) = PlaceDuo(mission, sector, noLock);
        MountLauncher(shooter, noLock, amount: 2);

        shooter.StateM |= ShipControlState.MissileFire;   // hold the fire bit

        Step(mission, 1, 0.016);                          // one tick: build lock (instant) + fire

        var missile = Assert.Single(sector.Models.OfType<Missile>());
        Assert.Same(target, missile.Target);              // homing at the command target
        Assert.Equal(1, shooter.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0).Amount);
    }

    /// <summary>Negative case for the held-bit path: a <c>MaxLock &gt; 0</c> type
    /// with the fire bit held but <em>no</em> lock built yet does NOT fire on that
    /// first tick — the lock gate (not merely the presence of the bit) decides.</summary>
    [Fact]
    public void OnUpdate_held_bit_does_not_fire_without_built_lock()
    {
        var (_, _, lockTyp) = Rep.Value;
        var mission = NewStartedMission(out var sector);
        var (shooter, _) = PlaceDuo(mission, sector, lockTyp);
        MountLauncher(shooter, lockTyp, amount: 2);

        shooter.StateM |= ShipControlState.MissileFire;

        Step(mission, 1, 0.005);                          // one short tick: lock far below full

        Assert.True(shooter.MissileLock < 1f, "one 5ms tick cannot fully build a MaxLock>0 missile");
        Assert.Empty(sector.Models.OfType<Missile>());
        Assert.Equal(2, shooter.Parts.Single(p => p.EquipmentType == EquipmentType.Magazine && p.MountId == 0).Amount);
    }
}
